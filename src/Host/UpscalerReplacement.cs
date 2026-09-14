using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 超分辨率替换组件的能力检测。
/// 该组件只负责管理独立代理与运行库，不参与 FPS/反虚化注入。
/// </summary>
internal sealed class UpscalerReplacement : IDisposable
{
    private const string DlssRuntimeUrl =
        "https://raw.githubusercontent.com/NVIDIA/DLSS/374959484e79a640feaba44c93ac8cfb0a03f5b5/lib/Windows_x86_64/rel/nvngx_dlss.dll";
    private const string DlssRuntimeSha256 = "3975567b8943c53acce397f2b72380092f84f162d00b0d2c7d08a1025c563983";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly object _sync = new();
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private Capability _capability = Inspect(null);
    private bool _enabled;
    private int _activePid;
    private string _status = "组件未检测";
    private string _quality = AppConfig.DefaultUpscalerQuality;
    // 保存最近一次配置的游戏路径。下载运行库完成后重新检测时必须沿用该路径，
    // 否则 Inspect(null) 会把「已设置游戏路径」错误地显示为未设置。
    private string? _gamePath;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private int _attemptedPid;

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
            if (_activePid == pid.Value) return;
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
                using var process = Process.GetProcessById(pid.Value);
                if (process.HasExited) return;
                if (DllInjector.TryInject(process, AppPaths.UpscalerProxyPath, out var error, "超分辨率代理"))
                {
                    _activePid = pid.Value;
                    _attemptedPid = pid.Value;
                    _nextAttemptUtc = DateTime.MinValue;
                    _status = $"超分辨率代理已注入 PID {pid.Value}（{QualityLabel(_quality)}）";
                    AppLog.Info($"upscaler proxy injected pid={pid.Value} quality={_quality}");
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

    public void Dispose()
    {
        lock (_sync) StopProxyLocked();
    }

    public async Task<DownloadResult> DownloadDlssRuntimeAsync(CancellationToken token)
    {
        var temporary = AppPaths.DlssRuntimePath + ".download";
        var entered = false;
        try
        {
            if (!await _downloadGate.WaitAsync(0, token).ConfigureAwait(false))
                return new DownloadResult(false, "下载已在进行中");
            entered = true;

            Directory.CreateDirectory(AppPaths.UpscalerDirectory);
            using var response = await Http.GetAsync(
                DlssRuntimeUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var target = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, token).ConfigureAwait(false);
            }

            string hash;
            // 先关闭校验文件句柄，再替换目标文件。Windows 下打开的句柄会阻止
            // File.Move(overwrite:true)，此前下载成功后仍可能报告“文件被占用”。
            await using (var verify = new FileStream(
                temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, useAsync: true))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, token).ConfigureAwait(false))
                    .ToLowerInvariant();
            }
            if (!hash.Equals(DlssRuntimeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DLSS Runtime 校验失败");

            File.Move(temporary, AppPaths.DlssRuntimePath, overwrite: true);
            AppLog.Info("DLSS Runtime 下载并校验完成");
            lock (_sync)
            {
                _capability = Inspect(_gamePath);
                if (_enabled) _status = _capability.Status;
            }
            return new DownloadResult(true, "DLSS Runtime 已下载并通过校验");
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult(false, "下载已取消");
        }
        catch (Exception ex)
        {
            AppLog.Warn("DLSS Runtime 下载失败: " + ex.Message);
            return new DownloadResult(false, "下载失败：" + ex.Message);
        }
        finally
        {
            // 无论取消、校验失败还是替换成功，都清理残留临时文件；成功时
            // File.Move 已经移走它，Delete 会安静地忽略不存在的路径。
            if (entered)
            {
                try { File.Delete(temporary); } catch { /* 清理失败不影响结果反馈 */ }
                _downloadGate.Release();
            }
        }
    }

    private void StopProxyLocked()
    {
        if (_activePid == 0) return;
        AppLog.Info($"upscaler replacement stopped pid={_activePid}");
        _activePid = 0;
    }

    private static Capability Inspect(string? gamePath)
    {
        var proxyExists = PathUtil.ExistsFile(AppPaths.UpscalerProxyPath);
        var runtimeExists = PathUtil.ExistsFile(AppPaths.DlssRuntimePath);
        var gameConfigured = GameLocator.IsValidGameExe(gamePath);

        var status = !proxyExists
            ? "缺少超分辨率代理组件"
            : !runtimeExists
                ? "缺少用户提供的 DLSS Runtime"
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
            + "[Libraries]\nNvngxDlssPath=nvngx_dlss.dll\n\n"
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

    internal sealed record DownloadResult(bool Ok, string Message);
}
