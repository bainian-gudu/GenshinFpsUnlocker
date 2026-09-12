using System.Text.Json;

namespace GenshinFpsUnlocker.Host;

/// <summary><c>AppConfig</c> 的加载与保存：批量写入、原子落盘、失败回退。</summary>
internal sealed partial class AppConfig
{
    /// <summary>
    /// 从磁盘加载配置。顺序：主文件 → .bak → .tmp → 便携 config.json；
    /// 全部失败则返回默认值（不在 Load 时写盘，避免覆盖用户残损文件前未备份）。
    /// </summary>
    public static AppConfig Load()
    {
        lock (IoLock)
        {
            MigrateLegacyConfigIfNeeded();

            foreach (var path in EnumerateCandidateReadPaths())
            {
                try
                {
                    if (!PathUtil.ExistsFile(path)) continue;
                    var json = File.ReadAllText(path, Utf8NoBom);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                    if (cfg is null) continue;

                    cfg.Migrate();
                    cfg.Sanitize();
                    cfg.LoadedFromDisk = true;
                    AppLog.Info($"config loaded from {path}");

                    // 若是从备份/临时恢复，立刻写回主路径巩固
                    if (!PathUtil.EqualsPath(path, ConfigPath))
                    {
                        try
                        {
                            cfg.SaveCore(createBackup: false);
                            AppLog.Warn($"config recovered from {path} → {ConfigPath}");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("config recover write: " + ex.Message);
                        }
                    }

                    return cfg;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"config read failed ({path}): {ex.Message}");
                }
            }

            AppLog.Warn("config not found or unreadable — using defaults");
            var defaults = new AppConfig();
            defaults.Migrate();
            defaults.Sanitize();
            // 首次运行写出默认配置，确保目录与文件存在
            try { defaults.SaveCore(createBackup: false); }
            catch (Exception ex) { AppLog.Warn("config initial save: " + ex.Message); }
            return defaults;
        }
    }

    /// <summary>原子持久化；失败时抛出（UI 可提示）。内部带锁。</summary>
    public void Save()
    {
        lock (IoLock)
        {
            if (_batchDepth > 0)
            {
                _batchDirty = true;   // 批量窗口内合并，Dispose/Flush 时落盘一次
                return;
            }
            SaveCore(createBackup: true);
        }
    }

    /// <summary>
    /// 开启一个批量修改窗口：窗口内所有 Save/TrySave 合并成退出时的一次落盘。
    /// 一次 patchConfig 里改多个键时，旧实现每个 setter 都会做一整套原子保存
    /// （WriteThrough + Flush(true) + 备份拷贝 + File.Replace + 校验读），全部同步
    /// 跑在 UI 线程上，最多能连着做四次。
    /// </summary>
    public BatchScope BeginBatch()
    {
        lock (IoLock)
        {
            _batchDepth++;
        }
        return new BatchScope(this);
    }

    /// <summary>批量修改窗口。Dispose 时若仍有未落盘的改动则写一次（异常只记日志）。</summary>
    public sealed class BatchScope : IDisposable
    {
        private readonly AppConfig _config;
        private bool _finished;

        internal BatchScope(AppConfig config) => _config = config;

        /// <summary>立即落盘（若窗口内有改动）。失败会抛出，供调用方置错误状态。</summary>
        public void Flush()
        {
            lock (IoLock)
            {
                if (!_config._batchDirty) return;
                _config._batchDirty = false;
                _config.SaveCore(createBackup: true);
            }
        }

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "config batch flush");
            }
            finally
            {
                lock (IoLock)
                {
                    if (_config._batchDepth > 0) _config._batchDepth--;
                }
            }
        }
    }

    /// <summary>尽力保存，不向外抛（托盘开关等热路径）。</summary>
    public bool TrySave(out string? error)
    {
        error = null;
        try
        {
            Save();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { AppLog.Error(ex, "config Save"); } catch { /* ignore */ }
            return false;
        }
    }

    private void SaveCore(bool createBackup)
    {
        Sanitize();
        EnsureDataDirectory();

        var primary = ConfigPath;
        var dir = Path.GetDirectoryName(primary);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("配置目录无效");

        var json = JsonSerializer.Serialize(this, Options);

        // 内容没变就别再走一整套原子写：UI 的热路径（滑块、开关）经常连着触发保存，
        // 而其中相当一部分根本没有实际改动。文件被外部删掉时仍会重写。
        if (json == _lastSavedJson && PathUtil.ExistsFile(ConfigPath))
        {
            return;
        }

        var bytes = Utf8NoBom.GetBytes(json);

        // 独立临时名，避免多实例互相踩 .tmp
        var tmp = Path.Combine(dir, $".config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            // 写临时文件并刷盘
            // Flush(flushToDisk: true) 已经会把文件缓冲刷到盘，不必再叠 WriteThrough
            // （两者同时用等于每条写指令都绕过系统缓存，配置这种小文件纯属浪费）。
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 4096))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (PathUtil.ExistsFile(primary))
            {
                if (createBackup)
                {
                    try
                    {
                        // 先备份当前好文件
                        File.Copy(primary, BackupPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Debug("config bak: " + ex.Message);
                    }
                }

                try
                {
                    // 原子替换（NTFS）；保留 backupPath 作为 Replace 的备份参数再稳一层
                    var replaceBackup = Path.Combine(dir, $".config.replace.{Guid.NewGuid():N}.bak");
                    File.Replace(tmp, primary, replaceBackup, ignoreMetadataErrors: true);
                    try { File.Delete(replaceBackup); } catch { /* ignore */ }
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(tmp, primary, overwrite: true);
                    try { File.Delete(tmp); } catch { /* ignore */ }
                }
                catch (IOException)
                {
                    // Replace 失败（跨卷等）：回退拷贝
                    File.Copy(tmp, primary, overwrite: true);
                    try { File.Delete(tmp); } catch { /* ignore */ }
                }
            }
            else
            {
                File.Move(tmp, primary, overwrite: true);
            }

            // 校验可读
            try
            {
                var check = File.ReadAllText(primary, Utf8NoBom);
                if (string.IsNullOrWhiteSpace(check) || check.Length < 2)
                    throw new IOException("写入后配置文件为空");
            }
            catch (Exception ex)
            {
                // 校验失败：尝试从刚写的内容再救一次
                AppLog.Error(ex, "config verify");
                File.WriteAllBytes(primary, bytes);
            }

            _lastSavedJson = json;
            AppLog.Debug("config saved " + primary);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
