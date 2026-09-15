namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 独立的超分辨率替换监视循环。它不受 FPS 解锁总开关和 AutoWatch 影响，
/// 这样两个功能可以真正解耦；代理模块自身仍负责在游戏图形接口内完成替换。
/// </summary>
internal sealed partial class UnlockService
{
    private Task? _upscalerLoop;

    private void StartUpscalerMonitor()
    {
        _upscalerLoop = Task.Run(() => UpscalerLoopAsync(_cts.Token));
    }

    private async Task UpscalerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var enabled = _config.UpscalerReplacementEnabled;
            try
            {
                if (!enabled)
                {
                    ObserveUpscaler(null);
                    await Task.Delay(1500, token);
                    continue;
                }

                using var process = GameProcess.Find();
                if (process is null)
                {
                    ObserveUpscaler(null);
                    await Task.Delay(1000, token);
                    continue;
                }

                // 游戏路径可能首次在游戏已运行时才被发现，沿用注入监视器的
                // 安全路径解析逻辑，避免通过 MainModule 读取失败导致状态失真。
                TryCapturePathFromProcess(process);
                ObserveUpscaler(process.Id);
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Debug("upscaler monitor: " + ex.Message);
                try { await Task.Delay(1500, token); } catch (OperationCanceledException) { break; }
            }
        }

        ObserveUpscaler(null);
    }

    private void ObserveUpscaler(int? pid)
    {
        var before = _upscaler.State;
        _upscaler.Observe(pid, _config.GamePath, _config.UpscalerQuality, _config.UpscalerMode);
        if (before != _upscaler.State && !_disposed)
            Raise(forceUi: true);
    }
}
