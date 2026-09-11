using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 应用配置（JSON）。主路径：%LocalAppData%\GenshinFpsUnlocker\config.json。
/// 持久化策略：写临时文件 → Flush → File.Replace 原子替换 → 保留 .bak 备份；
/// 读失败时依次尝试主文件 / .bak / .tmp / 便携旁路，降低丢失与半截写入风险。
/// </summary>
internal sealed class AppConfig
{
    /// <summary>目标帧率上限（1–540，默认 120）。</summary>
    public int TargetFps { get; set; } = 120;

    /// <summary>帧率解锁功能开关（与 MasterEnabled 同时为真时才真正解锁）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否监视游戏进程并在启动后自动注入。</summary>
    public bool AutoWatch { get; set; } = true;

    /// <summary>
    /// 启动时是否最小化到系统托盘。
    /// 默认 false：打开软件显示主窗口；关窗/点最小化仍进托盘后台。
    /// 勾选后：下次启动直接进托盘。
    /// </summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>配置 schema 版本（用于一次性迁移默认行为）。</summary>
    public int ConfigSchemaVersion { get; set; } = 0;

    /// <summary>是否写入 HKCU\...\Run，实现开机自启动。</summary>
    public bool AutoStartWithWindows { get; set; } = false;

    /// <summary>
    /// 总开关。关闭时：不注入、不强制帧率，后台仍可待命。
    /// 与 <see cref="Enabled"/> 的区别：总开关优先级更高，可一键暂停全部解锁行为。
    /// </summary>
    public bool MasterEnabled { get; set; } = true;

    /// <summary>监视循环基准轮询间隔（毫秒，200–10000）。</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>原神主程序完整路径（YuanShen.exe / GenshinImpact.exe）。</summary>
    public string? GamePath { get; set; }

    /// <summary>旧版配置字段别名，读入时合并到 <see cref="GamePath"/>。</summary>
    public string? GamePathHint
    {
        get => GamePath;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(GamePath))
                GamePath = value;
        }
    }

    /// <summary>用户是否已确认过安全声明。</summary>
    public bool SafetyNoticeAcknowledged { get; set; } = false;

    /// <summary>每次非自启动启动时是否再次展示安全声明。</summary>
    public bool ShowSafetyNoticeOnStartup { get; set; } = true;

    /// <summary>安装器是否已尝试添加 Defender 排除（仅记录状态）。</summary>
    public bool DefenderExclusionApplied { get; set; } = false;

    /// <summary>是否写调试日志（默认开启，便于排查注入问题）。</summary>
    public bool DebugLogging { get; set; } = true;

    /// <summary>最低日志级别：Trace / Debug / Info / Warn / Error（默认 Debug）。</summary>
    public string LogLevel { get; set; } = "Debug";

    /// <summary>日志保留天数（超过则清理 app-*.log）。</summary>
    public int LogRetainDays { get; set; } = 14;

    /// <summary>安装/首次运行时是否创建桌面快捷方式。</summary>
    public bool CreateDesktopShortcut { get; set; } = true;

    /// <summary>是否不再提示“建议管理员运行”（预留，默认不弹窗）。</summary>
    public bool SuppressAdminHint { get; set; } = true;

    /// <summary>配置文件完整路径（不序列化）。</summary>
    [JsonIgnore]
    public static string ConfigPath => AppPaths.ConfigPath;

    [JsonIgnore]
    private static string BackupPath => ConfigPath + ".bak";

    [JsonIgnore]
    private static string TempPath => ConfigPath + ".tmp";

    private static readonly object IoLock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 未知字段保留兼容，避免旧/新版本互相抹掉键
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

                    cfg.Sanitize();
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
            SaveCore(createBackup: true);
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
        var bytes = Utf8NoBom.GetBytes(json);

        // 独立临时名，避免多实例互相踩 .tmp
        var tmp = Path.Combine(dir, $".config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            // 写临时文件并刷盘
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
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

            AppLog.Debug("config saved " + primary);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>钳制数值范围并规范化游戏路径；执行 schema 迁移。</summary>
    public void Sanitize()
    {
        TargetFps = Math.Clamp(TargetFps, 1, 540);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 200, 10000);
        LogRetainDays = Math.Clamp(LogRetainDays, 1, 90);
        if (string.IsNullOrWhiteSpace(LogLevel)) LogLevel = "Debug";
        if (!string.IsNullOrWhiteSpace(GamePath))
        {
            try { GamePath = PathUtil.Normalize(GamePath); }
            catch { /* 保留原串 */ }
        }

        // v1：旧默认 startMinimized=true 导致「打开没窗口」；一次性改回 false。
        // 用户此后可再手动开启「启动后最小化到托盘」。
        if (ConfigSchemaVersion < 1)
        {
            if (StartMinimized)
            {
                StartMinimized = false;
                AppLog.Info("config migrate v1: StartMinimized false (show main window on launch)");
            }
            ConfigSchemaVersion = 1;
        }
    }

    /// <summary>实际是否应解锁 = 总开关 &amp;&amp; 功能开关。</summary>
    [JsonIgnore]
    public bool EffectiveUnlockEnabled => MasterEnabled && Enabled;

    private static IEnumerable<string> EnumerateCandidateReadPaths()
    {
        var list = new List<string>
        {
            ConfigPath,
            BackupPath,
            TempPath,
        };

        // 残留的进程临时文件（不可在 try/catch 内 yield）
        try
        {
            var dir = AppPaths.DataDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, ".config.*.tmp")
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Take(3))
                    list.Add(f);
            }
        }
        catch { /* ignore */ }

        var legacy = AppPaths.LegacyPortableConfigPath;
        if (legacy is not null) list.Add(legacy);

        return list;
    }

    private static void EnsureDataDirectory()
    {
        try
        {
            PathUtil.EnsureDir(AppPaths.DataDirectory);
        }
        catch (Exception ex)
        {
            AppLog.Warn("EnsureDataDirectory: " + ex.Message);
            // 回退：用户文档下的旁路（极少见 LocalAppData 不可写）
            try
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    AppPaths.ProductName);
                PathUtil.EnsureDir(fallback);
            }
            catch
            {
                throw new IOException("无法创建配置目录: " + AppPaths.DataDirectory, ex);
            }
        }
    }

    /// <summary>
    /// 若 AppData 尚无配置，而 exe 旁存在旧版 config.json，则复制一次到 AppData。
    /// 不删除旧文件，避免便携场景误伤。
    /// </summary>
    private static void MigrateLegacyConfigIfNeeded()
    {
        try
        {
            if (PathUtil.ExistsFile(ConfigPath)) return;
            var legacy = AppPaths.LegacyPortableConfigPath;
            if (legacy is null) return;

            PathUtil.EnsureDir(AppPaths.DataDirectory);
            File.Copy(legacy, ConfigPath, overwrite: false);
            try { File.Copy(legacy, BackupPath, overwrite: true); } catch { /* ignore */ }
            AppLog.Info("migrated portable config → " + ConfigPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn("config migrate: " + ex.Message);
        }
    }
}
