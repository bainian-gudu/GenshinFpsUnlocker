using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 超分辨率替换组件：按照 OptiScaler 标准部署方式适配原神。
/// 将 OptiScaler Aurora 代理以 dxgi.dll 形式放入游戏目录，游戏启动时自然加载，
/// 拦截 FSR2 调用并重定向到 DLSS。不再使用远程线程注入。
/// 支持 DLSS 4（标准超分辨率）和 DLSS 5（超分辨率 + 神经渲染）两种模式。
/// </summary>
internal sealed class UpscalerReplacement : IDisposable
{
    private const long MinimumDlssRuntimeBytes = 16L * 1024 * 1024;
    private const long MinimumDlssNrRuntimeBytes = 128L * 1024 * 1024;
    private const long MinimumDlssNrForwarderBytes = 4L * 1024;

    /// <summary>代理 DLL 在游戏目录中的文件名（OptiScaler 标准 proxy 名称）。</summary>
    private const string ProxyDllFileName = "dxgi.dll";

    /// <summary>部署标记文件，用于识别由本程序部署的文件。</summary>
    private const string DeployMarkerFileName = ".genshin-fps-unlocker-optiscaler";

    private readonly object _sync = new();
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc, string Hash)> _sourceHashCache =
        new(StringComparer.OrdinalIgnoreCase);
    private Capability _capability = Inspect(null, AppConfig.DefaultUpscalerMode, AppConfig.DefaultUpscalerQuality);
    private bool _enabled;
    private int _activePid;
    private string _status = "组件未检测";
    private string _quality = AppConfig.DefaultUpscalerQuality;
    private string _mode = AppConfig.DefaultUpscalerMode;
    private string? _gamePath;
    private DateTime _nextVerificationUtc = DateTime.MinValue;
    private DateTime _gameStartedUtc = DateTime.MinValue;
    private string? _forwardedLogPath;
    private long _forwardedLogOffset;
    private bool _fsrHookWarningLogged;
    private bool _deployed;

    public Snapshot State
    {
        get
        {
            lock (_sync)
                return new Snapshot(_enabled, _activePid != 0, _activePid, _capability.Available,
                    _capability.ProxyPresent, _capability.DlssRuntimePresent,
                    _capability.DlssNrRuntimePresent, _capability.GameConfigured,
                    _quality, _mode, _status);
        }
    }

    public void SetEnabled(bool enabled, string? gamePath, string? quality = null, string? mode = null)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _gamePath = gamePath;
            var previousQuality = _quality;
            var previousMode = _mode;
            if (!string.IsNullOrWhiteSpace(quality)
                && AppConfig.UpscalerQualityValues.Contains(quality, StringComparer.OrdinalIgnoreCase))
            {
                _quality = AppConfig.UpscalerQualityValues.First(v =>
                    string.Equals(v, quality, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(mode)
                && AppConfig.UpscalerModeValues.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                _mode = AppConfig.UpscalerModeValues.First(v =>
                    string.Equals(v, mode, StringComparison.OrdinalIgnoreCase));
            }
            _capability = Inspect(gamePath, _mode, _quality);
            if (!enabled)
            {
                StopTrackingLocked();
                TryRemoveDeploymentLocked(gamePath);
                _status = "替换功能已关闭";
                return;
            }
            if (_activePid != 0
                && (!string.Equals(previousQuality, _quality, StringComparison.Ordinal)
                    || !string.Equals(previousMode, _mode, StringComparison.Ordinal)))
            {
                _status = $"设置已更新为 {ModeLabel(_mode)} {QualityLabel(_quality)}，下次启动游戏时生效";
                return;
            }
            _status = _capability.Status;
        }
    }

    /// <summary>
    /// 独立观察原神 PID。
    /// 游戏未运行时部署文件；游戏运行时检查 OptiScaler 日志确认替换状态。
    /// </summary>
    public void Observe(int? pid, string? gamePath, string? quality = null, string? mode = null)
    {
        lock (_sync)
        {
            _gamePath = gamePath;
            if (!string.IsNullOrWhiteSpace(quality)
                && AppConfig.UpscalerQualityValues.Contains(quality, StringComparer.OrdinalIgnoreCase))
            {
                _quality = AppConfig.UpscalerQualityValues.First(v =>
                    string.Equals(v, quality, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(mode)
                && AppConfig.UpscalerModeValues.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                _mode = AppConfig.UpscalerModeValues.First(v =>
                    string.Equals(v, mode, StringComparison.OrdinalIgnoreCase));
            }
            if (!_enabled)
            {
                StopTrackingLocked();
                TryRemoveDeploymentLocked(gamePath);
                return;
            }

            // 游戏未运行：确保文件已部署
            if (pid is null)
            {
                StopTrackingLocked();
                _capability = Inspect(gamePath, _mode, _quality);
                if (_capability.Available)
                {
                    TryDeployLocked(gamePath);
                    _status = _deployed ? $"已就绪（{ModeLabel(_mode)} {QualityLabel(_quality)}）" : _capability.Status;
                }
                else
                {
                    _status = _capability.Status;
                }
                return;
            }

            // 游戏正在运行
            if (_activePid == pid.Value)
            {
                VerifyProxyActivityLocked();
                return;
            }

            // 新游戏进程出现：确保已部署并标记为活动
            StopTrackingLocked();
            _capability = Inspect(gamePath, _mode, _quality);
            if (!_capability.Available)
            {
                _status = _capability.Status;
                return;
            }

            TryDeployLocked(gamePath);
            _activePid = pid.Value;
            _gameStartedUtc = DateTime.UtcNow;
            InitializeOptiScalerLogForwardingLocked();
            _status = _deployed
                ? $"原神已启动 PID {pid.Value}（{ModeLabel(_mode)} {QualityLabel(_quality)}），等待 OptiScaler 加载确认"
                : "部署失败，请检查游戏目录写入权限";
            AppLog.Info($"upscaler tracking pid={pid.Value} mode={_mode} quality={_quality} deployed={_deployed}");
        }
    }

    public void Dispose()
    {
        lock (_sync) StopTrackingLocked();
    }

    // -----------------------------------------------------------------------
    // 部署 / 移除
    // -----------------------------------------------------------------------

    /// <summary>
    /// 将 OptiScaler Aurora 代理、DLSS 运行库和配置文件部署到游戏目录。
    /// DLSS 5 模式额外部署 nvngx_dlssnr.dll（神经渲染模型）和 nvngx.dll_dlssnr.dll（转发器）。
    /// </summary>
    private void TryDeployLocked(string? gamePath)
    {
        var gameDir = Path.GetDirectoryName(gamePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(gameDir)) { _deployed = false; return; }

        try
        {
            var isDlss5 = IsDlss5Mode();
            var manifest = ReadComponentManifest();
            var markerPath = Path.Combine(gameDir, DeployMarkerFileName);
            var marker = ReadKeyValueFile(markerPath);
            var changed = false;

            var proxyDest = Path.Combine(gameDir, ProxyDllFileName);
            var dlssDest = Path.Combine(gameDir, "nvngx_dlss.dll");
            var dlssNrDest = Path.Combine(gameDir, "nvngx_dlssnr.dll");
            var dlssNrForwarderDest = Path.Combine(gameDir, "nvngx.dll_dlssnr.dll");

            // 只同步内容哈希变化（或目标缺失）的组件；宿主/UI 单独更新时清单不变，这里不会覆盖旧组件。
            changed |= SyncComponent(manifest, marker, "OptiScaler.dll", AppPaths.UpscalerProxyPath, proxyDest);
            changed |= SyncComponent(manifest, marker, "nvngx_dlss.dll", AppPaths.DlssRuntimePath, dlssDest);

            if (isDlss5)
            {
                changed |= SyncComponent(manifest, marker, "nvngx_dlssnr.dll", AppPaths.DlssNrRuntimePath, dlssNrDest);
                changed |= SyncComponent(manifest, marker, "nvngx.dll_dlssnr.dll", AppPaths.DlssNrForwarderPath, dlssNrForwarderDest);
            }
            else
            {
                changed |= RemoveComponent(marker, "nvngx_dlssnr.dll", dlssNrDest);
                changed |= RemoveComponent(marker, "nvngx.dll_dlssnr.dll", dlssNrForwarderDest);
            }

            if (!string.Equals(marker.GetValueOrDefault("mode"), _mode, StringComparison.OrdinalIgnoreCase))
            {
                marker["mode"] = _mode;
                changed = true;
            }

            EnsureOptiScalerConfigLocked(gameDir);

            if (changed || !File.Exists(markerPath))
            {
                marker["deployed"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                marker["proxy"] = ProxyDllFileName;
                marker["source_optiscaler"] = AppPaths.UpscalerProxyPath;
                marker["source_dlss"] = AppPaths.DlssRuntimePath;
                if (isDlss5)
                {
                    marker["source_dlssnr"] = AppPaths.DlssNrRuntimePath;
                    marker["source_dlssnr_forwarder"] = AppPaths.DlssNrForwarderPath;
                }
                else
                {
                    marker.Remove("source_dlssnr");
                    marker.Remove("source_dlssnr_forwarder");
                }
                WriteKeyValueFile(markerPath, marker);
            }

            _deployed = true;
            if (changed)
                AppLog.Info($"OptiScaler Aurora 组件已同步到游戏目录（{ModeLabel(_mode)}）：{gameDir}");
        }
        catch (Exception ex)
        {
            _deployed = false;
            AppLog.Warn($"OptiScaler 部署失败：{ex.Message}");
        }
    }

    /// <summary>移除由本程序部署到游戏目录的文件。</summary>
    private void TryRemoveDeploymentLocked(string? gamePath)
    {
        var gameDir = Path.GetDirectoryName(gamePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(gameDir)) return;

        var markerPath = Path.Combine(gameDir, DeployMarkerFileName);
        if (!File.Exists(markerPath)) return;

        // 游戏运行时不能删除正在使用的 DLL
        if (_activePid != 0)
        {
            try { Process.GetProcessById(_activePid); return; }
            catch { /* 进程已退出，可以继续清理 */ }
        }

        try
        {
            SafeDeleteFile(Path.Combine(gameDir, ProxyDllFileName));
            SafeDeleteFile(Path.Combine(gameDir, "nvngx_dlss.dll"));
            SafeDeleteFile(Path.Combine(gameDir, "nvngx_dlssnr.dll"));
            SafeDeleteFile(Path.Combine(gameDir, "nvngx.dll_dlssnr.dll"));
            SafeDeleteFile(Path.Combine(gameDir, "OptiScaler.ini"));
            SafeDeleteFile(Path.Combine(gameDir, "OptiScaler.log"));
            SafeDeleteFile(markerPath);
            AppLog.Info($"OptiScaler 已从游戏目录移除：{gameDir}");
        }
        catch (Exception ex)
        {
            AppLog.Debug($"OptiScaler 移除部分失败：{ex.Message}");
        }
    }

    private static void SafeCopyFile(string source, string dest, bool force = false)
    {
        if (!File.Exists(source)) return;
        var srcInfo = new FileInfo(source);
        // 非强制模式只在源文件更新时才覆盖，避免游戏运行时重复写入。
        if (!force && File.Exists(dest))
        {
            var dstInfo = new FileInfo(dest);
            if (srcInfo.Length == dstInfo.Length
                && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                return;
        }
        var temp = dest + ".tmp";
        File.Copy(source, temp, overwrite: true);
        if (File.Exists(dest))
        {
            try { File.SetAttributes(dest, FileAttributes.Normal); } catch { /* ignore */ }
        }
        File.Move(temp, dest, overwrite: true);
        try { File.SetLastWriteTimeUtc(dest, srcInfo.LastWriteTimeUtc); } catch { /* ignore */ }
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 文件可能被占用，忽略 */ }
    }

    private bool SyncComponent(
        IReadOnlyDictionary<string, string> manifest,
        IDictionary<string, string> marker,
        string componentName,
        string source,
        string destination)
    {
        if (!File.Exists(source)) return false;

        var hash = GetComponentHash(manifest, source, componentName);
        var sourceInfo = new FileInfo(source);
        var destInfo = new FileInfo(destination);
        var markerKey = "hash_" + componentName;
        var hasMarkerHash = marker.TryGetValue(markerKey, out var deployedHash);
        var needsHashWrite = !hasMarkerHash
                             || !string.Equals(deployedHash, hash, StringComparison.OrdinalIgnoreCase);
        var unchanged = destInfo.Exists
                        && destInfo.Length == sourceInfo.Length
                        && ((hasMarkerHash
                             && string.Equals(deployedHash, hash, StringComparison.OrdinalIgnoreCase))
                            || (!hasMarkerHash
                                && string.Equals(GetFileHashCached(destination), hash, StringComparison.OrdinalIgnoreCase)));

        if (!unchanged)
        {
            SafeCopyFile(source, destination, force: true);
            AppLog.Info($"OptiScaler 组件已更新：{componentName}");
        }

        marker[markerKey] = hash;
        return !unchanged || needsHashWrite;
    }

    private static bool RemoveComponent(
        IDictionary<string, string> marker,
        string componentName,
        string destination)
    {
        var existed = File.Exists(destination);
        if (existed) SafeDeleteFile(destination);
        var hadMarker = marker.Remove("hash_" + componentName);
        return existed || hadMarker;
    }

    private string GetComponentHash(IReadOnlyDictionary<string, string> manifest, string path, string componentName)
    {
        if (manifest.TryGetValue(componentName, out var hash) && hash.Length == 64)
            return hash;
        return GetFileHashCached(path);
    }

    private string GetFileHashCached(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (_sourceHashCache.TryGetValue(fullPath, out var cached)
            && cached.Length == info.Length
            && cached.LastWriteUtc == info.LastWriteTimeUtc)
        {
            return cached.Hash;
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        _sourceHashCache[fullPath] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    private static Dictionary<string, string> ReadComponentManifest()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = AppPaths.UpscalerManifestPath;
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var split = line.IndexOf('=');
            if (split <= 0 || split == line.Length - 1) continue;
            var name = line[..split].Trim();
            var hash = line[(split + 1)..].Trim();
            if (name.Length > 0 && hash.Length == 64) result[name] = hash;
        }
        return result;
    }

    private static Dictionary<string, string> ReadKeyValueFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            result[line[..split].Trim()] = line[(split + 1)..].Trim();
        }
        return result;
    }

    private static void WriteKeyValueFile(string path, IReadOnlyDictionary<string, string> values)
    {
        var lines = values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}");
        var temp = path + ".tmp";
        File.WriteAllLines(temp, lines, new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* ignore */ }
        }
        File.Move(temp, path, true);
    }

    // -----------------------------------------------------------------------
    // 状态检测
    // -----------------------------------------------------------------------

    private bool IsDlss5Mode() =>
        string.Equals(_mode, "dlss5", StringComparison.OrdinalIgnoreCase);

    private void StopTrackingLocked()
    {
        if (_activePid == 0) return;
        AppLog.Info($"upscaler tracking stopped pid={_activePid}");
        _activePid = 0;
        _gameStartedUtc = DateTime.MinValue;
        _forwardedLogPath = null;
        _forwardedLogOffset = 0;
        _fsrHookWarningLogged = false;
    }

    private static Capability Inspect(string? gamePath, string mode, string quality)
    {
        var proxyExists = IsValidProxy(AppPaths.UpscalerProxyPath, out var proxyError);
        var runtimeFileExists = PathUtil.ExistsFile(AppPaths.DlssRuntimePath);
        var runtimeExists = runtimeFileExists && IsRuntimeFileUsable(AppPaths.DlssRuntimePath, MinimumDlssRuntimeBytes);
        var isDlss5 = string.Equals(mode, "dlss5", StringComparison.OrdinalIgnoreCase);
        var nrRuntimeFileExists = PathUtil.ExistsFile(AppPaths.DlssNrRuntimePath);
        var nrRuntimeExists = isDlss5
                              && nrRuntimeFileExists
                              && IsRuntimeFileUsable(AppPaths.DlssNrRuntimePath, MinimumDlssNrRuntimeBytes);
        var nrForwarderFileExists = PathUtil.ExistsFile(AppPaths.DlssNrForwarderPath);
        var nrForwarderExists = isDlss5
                                && nrForwarderFileExists
                                && IsRuntimeFileUsable(AppPaths.DlssNrForwarderPath, MinimumDlssNrForwarderBytes);
        var gameConfigured = GameLocator.IsValidGameExe(gamePath);

        string status;
        bool available;
        if (!proxyExists)
        {
            status = proxyError;
            available = false;
        }
        else if (!runtimeExists)
        {
            status = runtimeFileExists
                ? "nvngx_dlss.dll 文件不完整（请检查安装目录 upscaler 是否完整）"
                : "缺少 nvngx_dlss.dll（随项目分发，请检查安装目录 upscaler 是否完整）";
            available = false;
        }
        else if (isDlss5 && !nrRuntimeExists)
        {
            status = nrRuntimeFileExists
                ? "nvngx_dlssnr.dll 文件不完整（可能仍是 Git LFS 指针，请重新安装完整组件）"
                : "缺少 nvngx_dlssnr.dll（DLSS 5 神经渲染模型，请检查安装目录 upscaler 是否完整）";
            available = false;
        }
        else if (isDlss5 && !nrForwarderExists)
        {
            status = nrForwarderFileExists
                ? "nvngx.dll_dlssnr.dll 文件不完整（请重新安装完整组件）"
                : "缺少 nvngx.dll_dlssnr.dll（DLSS 5 转发器，请检查安装目录 upscaler 是否完整）";
            available = false;
        }
        else if (!gameConfigured)
        {
            status = "请先设置游戏路径";
            available = false;
        }
        else
        {
            status = $"已就绪（{ModeLabel(mode)} {QualityLabel(quality)}）";
            available = true;
        }

        return new Capability(
            Available: available,
            ProxyPresent: proxyExists,
            DlssRuntimePresent: runtimeExists,
            DlssNrRuntimePresent: nrRuntimeExists,
            GameConfigured: gameConfigured,
            Status: status);
    }

    private static bool IsRuntimeFileUsable(string path, long minimumBytes)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length >= minimumBytes;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidProxy(string path, out string error)
    {
        error = "缺少 OptiScaler.dll（随项目分发，请检查安装目录 upscaler 是否完整）";
        if (!PathUtil.ExistsFile(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 4096) { error = "OptiScaler 组件文件过小或已损坏"; return false; }
            return true;
        }
        catch (Exception ex) { error = "无法读取 OptiScaler 组件：" + ex.Message; return false; }
    }

    // -----------------------------------------------------------------------
    // 配置文件
    // -----------------------------------------------------------------------

    /// <summary>
    /// 将 OptiScaler Aurora 配置写入游戏目录。
    /// DLSS 5 模式额外写入 [DlssNr] 段落以启用神经渲染。
    /// </summary>
    private void EnsureOptiScalerConfigLocked(string gameDir)
    {
        var ratio = _quality switch
        {
            "balanced" => 1.7,
            "performance" => 2.0,
            "ultraPerformance" => 3.0,
            "nativeAA" => 1.0,
            _ => 1.5,
        };
        var ratioText = ratio.ToString("0.0", CultureInfo.InvariantCulture);
        var chineseFontPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc");
        var isDlss5 = IsDlss5Mode();
        var text = $"; 由原神帧率解锁生成（OptiScaler Aurora），FSR2 输入使用 DLSS 输出（{ModeLabel(_mode)}）\n"
            + "[Upscalers]\n"
            + "Dx11Upscaler=dlss\n"
            + "Dx12Upscaler=dlss\n"
            + "VulkanUpscaler=dlss\n\n"
            + "[Libraries]\nNvngxDlssPath=nvngx_dlss.dll\n"
            + (isDlss5 ? "NvngxDlssNrPath=nvngx_dlssnr.dll\n" : string.Empty) + "\n"
            + "[DLSS]\nEnabled=true\n\n"
            + (isDlss5
                ? "[DlssNr]\nEnabled=true\nToggleKey=0x75\n\n"
                : string.Empty)
            + "[Log]\nLogToFile=true\nLogLevel=1\nLogFileName=OptiScaler.log\nSingleFile=true\n\n"
            + "[QualityOverrides]\nQualityRatioOverrideEnabled=true\n"
            + $"QualityRatioDLAA={ratioText}\n"
            + $"QualityRatioUltraQuality={ratioText}\n"
            + $"QualityRatioQuality={ratioText}\n"
            + $"QualityRatioBalanced={ratioText}\n"
            + $"QualityRatioPerformance={ratioText}\n"
            + $"QualityRatioUltraPerformance={ratioText}\n"
            + "\n[Inputs]\n"
            + "EnableFsr2Inputs=true\n"
            + "UseFsr2Inputs=true\n"
            + "UseFsr2Dx11Inputs=true\n"
            + "Fsr2Pattern=true\n"
            + "\n[Menu]\n"
            + "OverlayMenu=true\n"
            + "ShowFps=true\n"
            + "FpsOverlayPos=2\n"
            + "FpsOverlayType=1\n"
            + "FpsOverlayHorizontal=false\n"
            + "FpsOverlayAlpha=0.85\n"
            + "UseHQFont=true\n"
            + (File.Exists(chineseFontPath) ? $"TTFFontPath={chineseFontPath}\n" : string.Empty)
            + "OverlaysUseTheme=true\n";
        var path = Path.Combine(gameDir, "OptiScaler.ini");
        try
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal))
                return;
        }
        catch
        {
            // 读取失败时照常重写配置。
        }
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
        AppLog.Info($"OptiScaler Aurora 配置已写入游戏目录：{ModeLabel(_mode)} {QualityLabel(_quality)}、DX11 FSR2 输入检测");
    }

    private static string QualityLabel(string quality) => quality switch
    {
        "balanced" => "均衡",
        "performance" => "性能",
        "ultraPerformance" => "超高性能",
        "nativeAA" => "DLAA",
        _ => "质量",
    };

    private static string ModeLabel(string mode) => mode switch
    {
        "dlss5" => "DLSS 5",
        _ => "DLSS 4",
    };

    // -----------------------------------------------------------------------
    // 日志分析
    // -----------------------------------------------------------------------

    private void VerifyProxyActivityLocked()
    {
        if (DateTime.UtcNow < _nextVerificationUtc)
            return;
        _nextVerificationUtc = DateTime.UtcNow.AddSeconds(2);

        var gameDirectory = Path.GetDirectoryName(_gamePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(gameDirectory)) return;

        var logPath = Path.Combine(gameDirectory, "OptiScaler.log");
        if (!PathUtil.ExistsFile(logPath)) return;

        try
        {
            var info = new FileInfo(logPath);
            ForwardOptiScalerLogLocked(logPath, info.Length);
            if (_gameStartedUtc != DateTime.MinValue && info.LastWriteTimeUtc < _gameStartedUtc.AddSeconds(-2))
                return;
            var length = Math.Min(info.Length, 128 * 1024);
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-length, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var tail = reader.ReadToEnd();
            var isDlss5 = IsDlss5Mode();
            if (tail.Contains("_EvaluateFeature ok!", StringComparison.OrdinalIgnoreCase))
            {
                var nrSuffix = isDlss5 && tail.Contains("DlssNr", StringComparison.OrdinalIgnoreCase)
                    ? " + 神经渲染" : string.Empty;
                _status = $"超分辨率替换已确认（Evaluate 成功，{ModeLabel(_mode)}{nrSuffix}，{QualityLabel(_quality)}）";
            }
            else if (tail.Contains("_EvaluateFeature result", StringComparison.OrdinalIgnoreCase)
                     || tail.Contains("_EvaluateFeature is nullptr", StringComparison.OrdinalIgnoreCase))
            {
                _status = "代理已加载，但 Evaluate 失败，请检查 OptiScaler 日志";
            }
            else if (tail.Contains("nvngx_dlss.dll not found", StringComparison.OrdinalIgnoreCase)
                     || tail.Contains("disabling DLSS", StringComparison.OrdinalIgnoreCase))
            {
                _status = "OptiScaler 已加载，但未找到 DLSS Runtime";
            }
            else if (isDlss5 && tail.Contains("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)
                     && tail.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                _status = "OptiScaler 已加载，但未找到 DLSS 5 神经渲染模型";
            }
            else if (isDlss5 && tail.Contains("nvngx.dll_dlssnr.dll", StringComparison.OrdinalIgnoreCase)
                     && tail.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                _status = "OptiScaler 已加载，但未找到 DLSS 5 转发器";
            }
            else if (Fsr2HooksMissing(tail))
            {
                _status = $"代理已加载（{ModeLabel(_mode)}），FSR2 接口尚未拦截，进入游戏场景后可能生效";
                if (!_fsrHookWarningLogged)
                {
                    _fsrHookWarningLogged = true;
                    AppLog.Warn("OptiScaler 初始化时未找到原神 FSR2 接口（UnityPlayer.dll 可能尚未加载）；进入游戏场景后若仍无 Evaluate 记录，当前游戏版本需要专用适配。");
                }
            }
            else if (tail.Contains("Creating DLSS feature", StringComparison.OrdinalIgnoreCase)
                     || tail.Contains("Enabling DLSS", StringComparison.OrdinalIgnoreCase)
                     || tail.Contains("init successful", StringComparison.OrdinalIgnoreCase))
            {
                _status = "DLSS 功能已创建，等待实际渲染调用";
            }
            else if (tail.Contains("OptiScaler v", StringComparison.OrdinalIgnoreCase)
                     && (tail.Contains("Setting DllPath", StringComparison.OrdinalIgnoreCase)
                         || tail.Contains("Running on", StringComparison.OrdinalIgnoreCase)
                         || tail.Contains("DLSS.Enabled", StringComparison.OrdinalIgnoreCase)))
            {
                _status = "OptiScaler 已加载并初始化，等待 FSR2 渲染调用";
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("读取 OptiScaler 日志失败: " + ex.Message);
        }
    }

    private static bool Fsr2HooksMissing(string tail)
    {
        var dx11 = tail.Contains("Trying to hook FSR2 Dx11 methods", StringComparison.OrdinalIgnoreCase)
            && tail.Contains("ffxFsr2ContextCreate_Dx11: 0", StringComparison.OrdinalIgnoreCase)
            && tail.Contains("ffxFsr2ContextDispatch_Dx11: 0", StringComparison.OrdinalIgnoreCase);
        var generic = tail.Contains("HookFSR2ExeInputs Trying to hook FSR2 methods", StringComparison.OrdinalIgnoreCase)
            && tail.Contains("ffxFsr2ContextCreate_Dx12: 0", StringComparison.OrdinalIgnoreCase)
            && tail.Contains("ffxFsr2ContextDispatch_Dx12: 0", StringComparison.OrdinalIgnoreCase);
        return dx11 || generic;
    }

    private void InitializeOptiScalerLogForwardingLocked()
    {
        _forwardedLogPath = null;
        _forwardedLogOffset = 0;
        var gameDirectory = Path.GetDirectoryName(_gamePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(gameDirectory)) return;
        var path = Path.Combine(gameDirectory, "OptiScaler.log");
        if (!PathUtil.ExistsFile(path)) return;
        try
        {
            _forwardedLogPath = path;
            _forwardedLogOffset = new FileInfo(path).Length;
        }
        catch { /* 日志转发初始化失败不影响部署 */ }
    }

    private void ForwardOptiScalerLogLocked(string path, long length)
    {
        try
        {
            if (!string.Equals(_forwardedLogPath, path, StringComparison.OrdinalIgnoreCase)
                || length < _forwardedLogOffset)
            {
                _forwardedLogPath = path;
                _forwardedLogOffset = 0;
            }
            if (length <= _forwardedLogOffset) return;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(_forwardedLogOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var forwarded = 0;
            while (forwarded < 32 && reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || !IsRelevantOptiScalerLog(line)) continue;
                var level = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("nullptr", StringComparison.OrdinalIgnoreCase)
                    ? "Warn" : "Info";
                if (level == "Warn") AppLog.Warn("[OptiScaler] " + line);
                else AppLog.Info("[OptiScaler] " + line);
                forwarded++;
            }
            _forwardedLogOffset = length;
        }
        catch (Exception ex)
        {
            AppLog.Debug("转发 OptiScaler 日志失败: " + ex.Message);
        }
    }

    private static bool IsRelevantOptiScalerLog(string line) =>
        line.Contains("Evaluate", StringComparison.OrdinalIgnoreCase)
        || line.Contains("DLSS", StringComparison.OrdinalIgnoreCase)
        || line.Contains("DlssNr", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Neural", StringComparison.OrdinalIgnoreCase)
        || line.Contains("FSR", StringComparison.OrdinalIgnoreCase)
        || line.Contains("upscal", StringComparison.OrdinalIgnoreCase)
        || line.Contains("feature", StringComparison.OrdinalIgnoreCase)
        || line.Contains("init", StringComparison.OrdinalIgnoreCase)
        || line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("fail", StringComparison.OrdinalIgnoreCase);

    internal sealed record Snapshot(
        bool Enabled,
        bool Active,
        int ActivePid,
        bool Available,
        bool ProxyPresent,
        bool DlssRuntimePresent,
        bool DlssNrRuntimePresent,
        bool GameConfigured,
        string Quality,
        string Mode,
        string Status);

    private sealed record Capability(
        bool Available,
        bool ProxyPresent,
        bool DlssRuntimePresent,
        bool DlssNrRuntimePresent,
        bool GameConfigured,
        string Status);
}
