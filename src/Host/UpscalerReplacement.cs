using System.Diagnostics;
using System.Globalization;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 超分辨率替换组件：按照 OptiScaler 标准部署方式适配原神。
/// 将 OptiScaler Aurora 代理以 dxgi.dll 形式放入游戏目录，游戏启动时自然加载，
/// 拦截 FSR2 调用并重定向到 DLSS。不再使用远程线程注入。
/// </summary>
internal sealed class UpscalerReplacement : IDisposable
{
    /// <summary>代理 DLL 在游戏目录中的文件名（OptiScaler 标准 proxy 名称）。</summary>
    private const string ProxyDllFileName = "dxgi.dll";

    /// <summary>部署标记文件，用于识别由本程序部署的文件。</summary>
    private const string DeployMarkerFileName = ".genshin-fps-unlocker-optiscaler";

    private readonly object _sync = new();
    private Capability _capability = Inspect(null);
    private bool _enabled;
    private int _activePid;
    private string _status = "组件未检测";
    private string _quality = AppConfig.DefaultUpscalerQuality;
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
                StopTrackingLocked();
                TryRemoveDeploymentLocked(gamePath);
                _status = "替换功能已关闭";
                return;
            }
            if (_activePid != 0 && !string.Equals(previousQuality, _quality, StringComparison.Ordinal))
            {
                _status = $"挡位已更新为 {QualityLabel(_quality)}，下次启动游戏时生效";
                return;
            }
            _status = _capability.Status;
        }
    }

    /// <summary>
    /// 独立观察原神 PID。
    /// 游戏未运行时部署文件；游戏运行时检查 OptiScaler 日志确认替换状态。
    /// </summary>
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
                _capability = Inspect(gamePath);
                if (_capability.Available)
                {
                    TryDeployLocked(gamePath);
                    _status = _deployed ? "组件已部署，等待原神启动" : _capability.Status;
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
            _capability = Inspect(gamePath);
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
                ? $"原神已启动 PID {pid.Value}（{QualityLabel(_quality)}），等待 OptiScaler 加载确认"
                : "部署失败，请检查游戏目录写入权限";
            AppLog.Info($"upscaler tracking pid={pid.Value} quality={_quality} deployed={_deployed}");
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
    /// 游戏启动时会自动加载 dxgi.dll，无需远程线程注入。
    /// </summary>
    private void TryDeployLocked(string? gamePath)
    {
        var gameDir = Path.GetDirectoryName(gamePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(gameDir)) { _deployed = false; return; }

        try
        {
            // 写入 OptiScaler 配置（每次部署都更新，确保挡位和参数最新）
            EnsureOptiScalerConfigLocked(gameDir);

            // 复制 OptiScaler.dll → dxgi.dll
            var proxyDest = Path.Combine(gameDir, ProxyDllFileName);
            SafeCopyFile(AppPaths.UpscalerProxyPath, proxyDest);

            // 复制 nvngx_dlss.dll
            var dlssDest = Path.Combine(gameDir, "nvngx_dlss.dll");
            SafeCopyFile(AppPaths.DlssRuntimePath, dlssDest);

            // 写入部署标记（记录部署时间和版本信息，供卸载时识别）
            var markerPath = Path.Combine(gameDir, DeployMarkerFileName);
            File.WriteAllText(markerPath,
                $"deployed={DateTime.UtcNow:O}\n" +
                $"proxy={ProxyDllFileName}\n" +
                $"source_optiscaler={AppPaths.UpscalerProxyPath}\n" +
                $"source_dlss={AppPaths.DlssRuntimePath}\n",
                new System.Text.UTF8Encoding(false));

            _deployed = true;
            AppLog.Info($"OptiScaler Aurora 已部署到游戏目录：{gameDir}");
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

    private static void SafeCopyFile(string source, string dest)
    {
        if (!File.Exists(source)) return;
        // 只在源文件更新时才覆盖，避免游戏运行时写入冲突
        if (File.Exists(dest))
        {
            var srcInfo = new FileInfo(source);
            var dstInfo = new FileInfo(dest);
            if (srcInfo.Length == dstInfo.Length
                && srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                return;
        }
        var temp = dest + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, dest, overwrite: true);
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 文件可能被占用，忽略 */ }
    }

    // -----------------------------------------------------------------------
    // 状态检测
    // -----------------------------------------------------------------------

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

    private static bool IsValidProxy(string path, out string error)
    {
        error = "缺少 OptiScaler.dll（随项目分发，请检查安装目录 upscaler 是否完整）";
        if (!PathUtil.ExistsFile(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 4096) { error = "OptiScaler 组件文件过小或已损坏"; return false; }
            Span<byte> dos = stackalloc byte[64];
            if (stream.Read(dos) != dos.Length || dos[0] != 'M' || dos[1] != 'Z')
            { error = "OptiScaler 组件不是有效的 Windows DLL"; return false; }
            var peOffset = BitConverter.ToInt32(dos[0x3c..0x40]);
            if (peOffset < 0 || peOffset > stream.Length - 6)
            { error = "OptiScaler 组件 PE 头无效"; return false; }
            stream.Position = peOffset;
            Span<byte> pe = stackalloc byte[6];
            if (stream.Read(pe) != pe.Length || pe[0] != 'P' || pe[1] != 'E'
                || pe[2] != 0 || pe[3] != 0 || BitConverter.ToUInt16(pe[4..6]) != 0x8664)
            { error = "OptiScaler 组件不是 x64 DLL"; return false; }
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { error = "无法读取 OptiScaler 组件：" + ex.Message; return false; }
    }

    // -----------------------------------------------------------------------
    // 配置文件
    // -----------------------------------------------------------------------

    /// <summary>
    /// 将 OptiScaler Aurora 配置写入游戏目录。
    /// 由于代理和运行库都在同一目录，使用相对路径即可。
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
        var text = "; 由原神帧率解锁生成（OptiScaler Aurora），FSR2 输入使用 DLSS 输出\n"
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
            + $"QualityRatioUltraPerformance={ratioText}\n"
            + "\n[Inputs]\n"
            + "EnableFsr2Inputs=true\n"
            + "UseFsr2Inputs=true\n"
            + "UseFsr2Dx11Inputs=true\n"
            // 原神将 FSR2 静态链接到 UnityPlayer.dll，不导出 ffxFsr2* 符号，
            // 必须开启内存模式扫描才能找到 FSR2 函数地址。
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
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
        AppLog.Info($"OptiScaler Aurora 配置已写入游戏目录：{QualityLabel(_quality)}、DX11 FSR2 输入检测");
    }

    private static string QualityLabel(string quality) => quality switch
    {
        "balanced" => "均衡",
        "performance" => "性能",
        "ultraPerformance" => "超高性能",
        "nativeAA" => "DLAA",
        _ => "质量",
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
            if (tail.Contains("_EvaluateFeature ok!", StringComparison.OrdinalIgnoreCase))
            {
                _status = $"超分辨率替换已确认（Evaluate 成功，{QualityLabel(_quality)}）";
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
            else if (Fsr2HooksMissing(tail))
            {
                _status = "代理已加载，但未检测到原神 FSR2 接口（当前版本可能不兼容）";
                if (!_fsrHookWarningLogged)
                {
                    _fsrHookWarningLogged = true;
                    AppLog.Warn("OptiScaler 未找到原神 FSR2 导出接口；已尝试 DX11 检测。若仍无 Evaluate 记录，当前游戏版本需要专用适配。");
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
