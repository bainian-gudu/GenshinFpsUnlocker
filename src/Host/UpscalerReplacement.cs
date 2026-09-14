using System.Diagnostics;
using System.Globalization;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 超分辨率替换组件的能力检测。
/// 该组件只负责管理独立代理与运行库，不参与 FPS/反虚化注入。
/// OptiScaler.dll 和 nvngx_dlss.dll 均已随项目分发，构建工作流会在文件缺失时自动下载。
/// </summary>
internal sealed class UpscalerReplacement : IDisposable
{
    private readonly object _sync = new();
    private Capability _capability = Inspect(null);
    private bool _enabled;
    private int _activePid;
    private string _status = "组件未检测";
    private string _quality = AppConfig.DefaultUpscalerQuality;
    // 保存最近一次配置的游戏路径。
    // Inspect(null) 会把「已设置游戏路径」错误地显示为未设置。
    private string? _gamePath;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private int _attemptedPid;
    private DateTime _nextVerificationUtc = DateTime.MinValue;
    private DateTime _proxyInjectUtc = DateTime.MinValue;
    private string? _forwardedLogPath;
    private long _forwardedLogOffset;

    public Snapshot State
    {
        get
        {
            lock (_sync)
                return new Snapshot(_enabled, _activePid != 0, _activePid, _capability.Available,
                    _capability.ProxyPresent, _capability.DlssRuntimePresent, _capability.GameConfigured,
                    _quality, _status);
        }
    }

    public void SetEnabled(bool enabled, string? gamePath, string? quality = null)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _gamePath = gamePath;
            var previousQuality = _quality;
            if (!string.IsNullOrWhiteSpace(quality)
                && AppConfig.UpscalerQualityValues.Contains(quality, StringComparer.OrdinalIgnoreCase))
            {
                _quality = AppConfig.UpscalerQualityValues.First(v =>
                    string.Equals(v, quality, StringComparison.OrdinalIgnoreCase));
            }
            _capability = Inspect(gamePath);
            if (!enabled)
            {
                StopProxyLocked();
                _status = "替换功能已关闭";
                return;
            }
            if (_activePid != 0 && !string.Equals(previousQuality, _quality, StringComparison.Ordinal))
            {
                // OptiScaler 在 DLL 加载时读取 ini，运行中的代理不能安全热重载。
                // 保留当前会话，新的挡位从下一次游戏启动开始使用。
                _status = $"挡位已更新为 {QualityLabel(_quality)}，下次启动游戏时生效";
                return;
            }
            _status = _capability.Status;
        }
    }

    /// <summary>独立观察原神 PID；游戏退出或关闭功能时停止当前替换会话。</summary>
    public void Observe(int? pid, string? gamePath, string? quality = null)
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
            if (!_enabled) { StopProxyLocked(); return; }
            if (pid is null)
            {
                StopProxyLocked();
                _attemptedPid = 0;
                _nextAttemptUtc = DateTime.MinValue;
                _capability = Inspect(gamePath);
                _status = _capability.Available ? "等待原神启动" : _capability.Status;
                return;
            }
            if (_activePid == pid.Value)
            {
                VerifyProxyActivityLocked();
                return;
            }
            StopProxyLocked();
            _capability = Inspect(gamePath);
            if (!_capability.Available)
            {
                _status = _capability.Status;
                return;
            }
            if (_attemptedPid == pid.Value && DateTime.UtcNow < _nextAttemptUtc)
                return;

            if (!ModuleTrust.IsTrustworthy(AppPaths.UpscalerProxyPath, Path.GetFileName(AppPaths.UpscalerProxyPath),
                    "超分辨率代理", out var trustError,
                    elevatedHint: "请把程序安装到 Program Files 下，或退出管理员实例后以普通权限运行。"))
            {
                _status = trustError;
                _attemptedPid = pid.Value;
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(60);
                return;
            }

            try
            {
                try
                {
                    EnsureOptiScalerConfigLocked();
                }
                catch (Exception configError)
                {
                    // 配置文件写入失败时仍尝试加载代理；代理内置默认值可继续工作，
                    // 同时把原因记录下来，避免因安装目录只读而完全失去替换能力。
                    AppLog.Warn("OptiScaler 配置写入失败，将使用现有配置: " + configError.Message);
                }
                var injectStartedUtc = DateTime.UtcNow;
                using var process = Process.GetProcessById(pid.Value);
                if (process.HasExited) return;
                if (DllInjector.TryInject(process, AppPaths.UpscalerProxyPath, out var error, "超分辨率代理", allowHookFallback: false))
                {
                    _activePid = pid.Value;
                    _attemptedPid = pid.Value;
                    _nextAttemptUtc = DateTime.MinValue;
                    _proxyInjectUtc = DateTime.UtcNow;
                    InitializeOptiScalerLogForwardingLocked();
                    _status = $"代理已加载 PID {pid.Value}（{QualityLabel(_quality)}），请查看 OptiScaler.log 确认 FSR2 调用";
                    AppLog.Info($"upscaler proxy injected pid={pid.Value} quality={_quality}");
                }
                else if (TryConfirmFreshOptiScalerLoadLocked(injectStartedUtc))
                {
                    // 某些游戏/安全软件会让远程线程返回值或模块枚举不可靠，
                    // 但代理已经执行 DllMain 并写出了本次启动日志。以新日志作为成功凭据，
                    // 避免把实际已加载的代理显示成注入失败。
                    _activePid = pid.Value;
                    _attemptedPid = pid.Value;
                    _nextAttemptUtc = DateTime.MinValue;
                    _proxyInjectUtc = injectStartedUtc;
                    InitializeOptiScalerLogForwardingLocked();
                    _status = $"代理已加载 PID {pid.Value}（{QualityLabel(_quality)}），等待渲染确认";
                    AppLog.Warn($"upscaler proxy load confirmed by fresh OptiScaler.log despite injector result pid={pid.Value}");
                }
                else
                {
                    _attemptedPid = pid.Value;
                    _nextAttemptUtc = DateTime.UtcNow.AddSeconds(15);
                    _status = $"超分辨率代理注入失败：{error}";
                    AppLog.Warn($"upscaler proxy injection failed pid={pid.Value}: {error}");
                }
            }
            catch (Exception ex)
            {
                _attemptedPid = pid.Value;
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(15);
                _status = $"超分辨率代理注入失败：{ex.Message}";
                AppLog.Warn("upscaler proxy injection exception: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 注入器返回失败时，检查目标游戏目录是否刚刚生成了本次代理日志。
    /// 这只作为超分代理的兜底确认，不能用于 FPS Stub。
    /// </summary>
    private bool TryConfirmFreshOptiScalerLoadLocked(DateTime sinceUtc)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppPaths.UpscalerDirectory, "OptiScaler.log"),
        };
        var gameDirectory = Path.GetDirectoryName(_gamePath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(gameDirectory))
            candidates.Add(Path.Combine(gameDirectory, "OptiScaler.log"));

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!PathUtil.ExistsFile(path)) continue;
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < sinceUtc.AddSeconds(-2)) continue;
                var length = Math.Min(info.Length, 128 * 1024);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(-length, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (text.Contains("OptiScaler v", StringComparison.OrdinalIgnoreCase)
                    && (text.Contains("Setting DllPath", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Running on", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("DLSS.Enabled", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (Exception ex)
            {
                AppLog.Debug("确认 OptiScaler 新日志失败: " + ex.Message);
            }
        }
        return false;
    }

    public void Dispose()
    {
        lock (_sync) StopProxyLocked();
    }

    private void StopProxyLocked()
    {
        if (_activePid == 0) return;
        AppLog.Info($"upscaler replacement stopped pid={_activePid}");
        _activePid = 0;
        _proxyInjectUtc = DateTime.MinValue;
        _forwardedLogPath = null;
        _forwardedLogOffset = 0;
    }

    private static Capability Inspect(string? gamePath)
    {
        var proxyExists = IsValidProxy(AppPaths.UpscalerProxyPath, out var proxyError);
        var runtimeExists = PathUtil.ExistsFile(AppPaths.DlssRuntimePath);
        var gameConfigured = GameLocator.IsValidGameExe(gamePath);

        var status = !proxyExists
            ? proxyError
            : !runtimeExists
                ? "缺少 nvngx_dlss.dll（随项目分发，请检查安装目录 upscaler 是否完整）"
                : !gameConfigured
                    ? "请先设置游戏路径"
                : "组件已就绪，等待原神启动";

        return new Capability(
            Available: proxyExists && runtimeExists && gameConfigured,
            ProxyPresent: proxyExists,
            DlssRuntimePresent: runtimeExists,
            GameConfigured: gameConfigured,
            Status: status);
    }

    /// <summary>检查代理是否为可加载的 x64 PE DLL，避免仅凭同名空文件误判就绪。</summary>
    private static bool IsValidProxy(string path, out string error)
    {
        error = "缺少 OptiScaler.dll（随项目分发，请检查安装目录 upscaler 是否完整）";
        if (!PathUtil.ExistsFile(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 4096) { error = "超分辨率代理组件文件过小或已损坏"; return false; }
            Span<byte> dos = stackalloc byte[64];
            if (stream.Read(dos) != dos.Length || dos[0] != 'M' || dos[1] != 'Z')
            { error = "超分辨率代理组件不是有效的 Windows DLL"; return false; }
            var peOffset = BitConverter.ToInt32(dos[0x3c..0x40]);
            if (peOffset < 0 || peOffset > stream.Length - 6)
            { error = "超分辨率代理组件 PE 头无效"; return false; }
            stream.Position = peOffset;
            Span<byte> pe = stackalloc byte[6];
            if (stream.Read(pe) != pe.Length || pe[0] != 'P' || pe[1] != 'E'
                || pe[2] != 0 || pe[3] != 0 || BitConverter.ToUInt16(pe[4..6]) != 0x8664)
            { error = "超分辨率代理组件不是 x64 DLL"; return false; }
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { error = "无法读取超分辨率代理组件：" + ex.Message; return false; }
    }

    /// <summary>写入 OptiScaler 的独立配置，使 FSR2 输入走 DLSS 输出。</summary>
    private void EnsureOptiScalerConfigLocked()
    {
        Directory.CreateDirectory(AppPaths.UpscalerDirectory);
        var ratio = _quality switch
        {
            "balanced" => 1.7,
            "performance" => 2.0,
            "ultraPerformance" => 3.0,
            "nativeAA" => 1.0,
            _ => 1.5,
        };
        var ratioText = ratio.ToString("0.0", CultureInfo.InvariantCulture);
        var text = "; 由原神帧率解锁生成，FSR2 输入使用 DLSS 输出\n"
            + "[Upscalers]\n"
            + "Dx11Upscaler=dlss\n"
            + "Dx12Upscaler=dlss\n"
            + "VulkanUpscaler=dlss\n\n"
            + "[Libraries]\nNvngxDlssPath=nvngx_dlss.dll\n\n"
            + "[DLSS]\nEnabled=true\n\n"
            + "[Log]\nLogToFile=true\nLogLevel=1\nLogFileName=OptiScaler.log\nSingleFile=true\n\n"
            + "[QualityOverrides]\nQualityRatioOverrideEnabled=true\n"
            + $"QualityRatioDLAA={ratioText}\n"
            + $"QualityRatioUltraQuality={ratioText}\n"
            + $"QualityRatioQuality={ratioText}\n"
            + $"QualityRatioBalanced={ratioText}\n"
            + $"QualityRatioPerformance={ratioText}\n"
            + $"QualityRatioUltraPerformance={ratioText}\n";
        var path = Path.Combine(AppPaths.UpscalerDirectory, "OptiScaler.ini");
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static string QualityLabel(string quality) => quality switch
    {
        "balanced" => "均衡",
        "performance" => "性能",
        "ultraPerformance" => "超高性能",
        "nativeAA" => "DLAA",
        _ => "质量",
    };

    /// <summary>
    /// 根据 OptiScaler 日志更新可理解的运行状态。模块加载和 Feature 初始化
    /// 不能单独证明每帧替换成功，只有 Evaluate 成功记录才显示“已确认”。
    /// </summary>
    private void VerifyProxyActivityLocked()
    {
        if (DateTime.UtcNow < _nextVerificationUtc)
            return;
        _nextVerificationUtc = DateTime.UtcNow.AddSeconds(2);

        var candidates = new List<string> { Path.Combine(AppPaths.UpscalerDirectory, "OptiScaler.log") };
        var gameDirectory = Path.GetDirectoryName(_gamePath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(gameDirectory))
            candidates.Add(Path.Combine(gameDirectory, "OptiScaler.log"));
        var logPath = candidates.FirstOrDefault(PathUtil.ExistsFile);
        if (logPath is null)
            return;

        try
        {
            var info = new FileInfo(logPath);
            ForwardOptiScalerLogLocked(logPath, info.Length);
            if (_proxyInjectUtc != DateTime.MinValue && info.LastWriteTimeUtc < _proxyInjectUtc.AddSeconds(-2))
                return; // 旧日志中的 Evaluate 成功不能证明本次游戏已替换。
            var length = Math.Min(info.Length, 128 * 1024);
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-length, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var tail = reader.ReadToEnd();
            if (tail.Contains("_EvaluateFeature ok!", StringComparison.OrdinalIgnoreCase))
            {
                _status = $"超分辨率替换已确认（Evaluate 成功，{QualityLabel(_quality)}）";
            }
            else if (tail.Contains("_EvaluateFeature result", StringComparison.OrdinalIgnoreCase)
                     || tail.Contains("_EvaluateFeature is nullptr", StringComparison.OrdinalIgnoreCase))
            {
                _status = "代理已加载，但 Evaluate 失败，请检查 OptiScaler 日志";
            }
            else if (tail.Contains("Creating DLSS feature", StringComparison.OrdinalIgnoreCase)
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

    /// <summary>从代理注入时刻开始转发新增的关键日志，供软件运行日志统一查看。</summary>
    private void InitializeOptiScalerLogForwardingLocked()
    {
        _forwardedLogPath = null;
        _forwardedLogOffset = 0;
        var candidates = new List<string>
        {
            Path.Combine(AppPaths.UpscalerDirectory, "OptiScaler.log"),
        };
        var gameDirectory = Path.GetDirectoryName(_gamePath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(gameDirectory))
            candidates.Add(Path.Combine(gameDirectory, "OptiScaler.log"));
        var path = candidates.FirstOrDefault(PathUtil.ExistsFile);
        if (path is null) return;
        try
        {
            _forwardedLogPath = path;
            _forwardedLogOffset = new FileInfo(path).Length;
        }
        catch { /* 日志转发失败不影响代理注入 */ }
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
        bool GameConfigured,
        string Quality,
        string Status);

    private sealed record Capability(
        bool Available,
        bool ProxyPresent,
        bool DlssRuntimePresent,
        bool GameConfigured,
        string Status);

}
