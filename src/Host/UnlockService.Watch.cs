using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 监视循环与注入时序（partial）。
/// </summary>
internal sealed partial class UnlockService
{
    /// <summary>
    /// 监视主循环：
    /// 1) 总开关/自动监视关闭 → 空闲等待
    /// 2) 无游戏 → 长间隔轮询
    /// 3) 已注入同 PID → 保活推送 IPC
    /// 4) 新 PID → 等主窗口 → 注入 → 等 Stub Ready → 保活直到退出
    /// </summary>
    private async Task WatchLoopAsync(CancellationToken token)
    {
        // 空闲间隔更长以降 CPU；游戏运行时用较短间隔
        var idlePoll = Math.Clamp(_config.PollIntervalMs, 500, 5000);
        var activePoll = Math.Clamp(Math.Min(_config.PollIntervalMs, 800), 300, 2000);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_config.MasterEnabled)
                {
                    PushConfigToIpc();
                    SetAttached(0);
                    SetStatus("总开关已关闭 — 后台待命（不注入）");
                    await Task.Delay(idlePoll, token);
                    continue;
                }

                if (!_config.AutoWatch)
                {
                    PushConfigToIpc();
                    SetAttached(0);
                    SetStatus("自动监视已关闭 — 可在托盘重新开启");
                    await Task.Delay(idlePoll, token);
                    continue;
                }

                using var process = GameProcess.Find();
                if (process is null)
                {
                    if (_attachedPid != 0 || _injectAttemptedPid != 0)
                    {
                        SetAttached(0);
                        Volatile.Write(ref _injectAttemptedPid, 0);
                        _injectFailStreak = 0;
                        SetStatus("游戏已退出 — 继续后台等待");
                        AppLog.Info("game process exited");
                    }
                    else
                    {
                        // SetStatus 内部去重，避免每秒刷 UI
                        SetStatus("后台运行中 — 等待 YuanShen / GenshinImpact 启动");
                    }

                    await Task.Delay(idlePoll, token);
                    continue;
                }

                TryCapturePathFromProcess(process);

                // 已对应该 PID 注入过 — 轻量保活
                if (_injectAttemptedPid == process.Id)
                {
                    PushConfigToIpc();
                    var live = _ipc.Read();
                    if (live.Status == IpcStatus.Error)
                    {
                        SetAttached(0);
                        Volatile.Write(ref _injectAttemptedPid, 0);
                        _injectFailStreak++;
                        _nextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Min(90, 15 * _injectFailStreak));
                        continue;
                    }
                    if (live.Status == IpcStatus.Exiting)
                    {
                        // 关闭开关时 Host 请求 Stub 结束当前会话；重新开启时让外层
                        // 重新走注入/ResetForNewInject，而不是把 Exiting 当成附着状态。
                        SetAttached(0);
                        Volatile.Write(ref _injectAttemptedPid, 0);
                        continue;
                    }
                    SetAttached(live.Status == IpcStatus.Ready && ShouldInject ? process.Id : 0);
                    SetStatus($"已附着 PID {process.Id} | Stub={live.Status} | 目标 {_config.TargetFps} FPS | 反馈 {live.CurrentFps}");

                    await Task.Delay(activePoll, token);

                    try
                    {
                        if (process.HasExited)
                        {
                            SetAttached(0);
                            Volatile.Write(ref _injectAttemptedPid, 0);
                        }
                    }
                    catch
                    {
                        SetAttached(0);
                        Volatile.Write(ref _injectAttemptedPid, 0);
                    }

                    continue;
                }

                if (token.IsCancellationRequested) break;

                if (!PathUtil.ExistsFile(_stubPath))
                {
                    SetStatus($"缺少 FpsUnlockerStub.dll（应位于: {_stubPath}）");
                    await Task.Delay(5000, token);
                    continue;
                }

                // 注入前校验模块可信度：已提权时安装目录必须受保护（Program Files），
                // 否则用户可写目录里的同名 DLL 会被我们的管理员令牌注入游戏，
                // 或在备用 Hook 注入路径下被映射进 Host 自己。与卸载器同一套检查。
                if (!ModuleTrust.IsTrustworthy(
                        _stubPath,
                        AppPaths.StubDllFileName,
                        "注入模块",
                        out var trustError,
                        elevatedHint: "请把程序安装到 Program Files 下，或退出管理员实例后以普通权限运行。"))
                {
                    SetStatus($"拒绝注入：{trustError}");
                    AppLog.Error("stub 可信度校验失败: " + trustError);
                    _nextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(60);
                    await Task.Delay(5000, token);
                    continue;
                }

                if (!ShouldInject)
                {
                    SetStatus($"游戏运行中 PID {process.Id}，当前没有启用需要注入的功能");
                    await Task.Delay(idlePoll, token);
                    continue;
                }

                // 注入失败后的退避窗口
                if (DateTime.UtcNow < _nextInjectAttemptUtc)
                {
                    var waitSec = Math.Max(1, (int)(_nextInjectAttemptUtc - DateTime.UtcNow).TotalSeconds);
                    SetStatus($"注入冷却中（{waitSec}s）… 上次失败后的退避");
                    await Task.Delay(1000, token);
                    continue;
                }

                // 注入前重置 Stub 状态字段，并推送最新 Host 配置（勿整块乱序写）
                _config.Sanitize();
                var activeUnlock = ShouldInject && _config.Enabled;
                _ipc.ResetForNewInject(_config.TargetFps, activeUnlock,
                    _config.MasterEnabled && _config.AutoWatch && _config.AntiBlurPerspective,
                    _config.MasterEnabled && _config.AutoWatch && _config.AntiBlurDiveMosaic);
                _lastPushedFps = _config.TargetFps;
                _lastPushedEnabled = activeUnlock ? 1 : 0;
                _lastIpcPushUtc = DateTime.UtcNow;

                SetStatus($"检测到游戏 PID {process.Id}，等待主窗口后注入…");
                await WaitForMainWindowAsync(process, token, TimeSpan.FromSeconds(45));

                if (token.IsCancellationRequested) break;
                try { if (process.HasExited) continue; } catch { continue; }

                SetStatus($"正在注入 Stub → PID {process.Id}…");
                AppLog.Info($"inject begin pid={process.Id} stub={_stubPath}");
                if (!DllInjector.TryInject(process, _stubPath, out var error))
                {
                    _injectFailStreak++;
                    var backoff = Math.Min(60, 5 * _injectFailStreak);
                    _nextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
                    AppLog.Error($"inject failed pid={process.Id} streak={_injectFailStreak} backoff={backoff}s: {error}");
                    var adminHint = Elevation.IsAdministrator()
                        ? string.Empty
                        : " — 可在界面或托盘选择「以管理员重新启动」";
                    SetStatus($"注入失败: {error}（{backoff}s 后重试）{adminHint}");
                    await Task.Delay(1000, token);
                    continue;
                }

                _injectFailStreak = 0;
                _nextInjectAttemptUtc = DateTime.MinValue;
                AppLog.Info($"inject OK pid={process.Id}, waiting stub ready…");
                Volatile.Write(ref _injectAttemptedPid, process.Id);

                var ok = await WaitForStubReadyAsync(token, TimeSpan.FromSeconds(90));
                if (ok)
                {
                    SetAttached(process.Id);
                    AppLog.Info($"stub Ready pid={process.Id} targetFps={_config.TargetFps}");
                    SetStatus($"解锁成功 PID {process.Id} | 目标 {_config.TargetFps} FPS");
                }
                else
                {
                    var st = _ipc.Read();
                    AppLog.Error($"stub not ready pid={process.Id} status={st.Status} lastError=0x{st.LastError:X}");
                    // 已注入但未 Ready：短时保活观察；若长期 Error/None 则允许冷却后重试
                    SetAttached(process.Id);
                    if (st.Status == IpcStatus.Error)
                    {
                        _injectFailStreak++;
                        var backoff = Math.Min(90, 15 * _injectFailStreak);
                        _nextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
                        Volatile.Write(ref _injectAttemptedPid, 0);
                        SetAttached(0);
                        SetStatus($"Stub 报告错误 0x{st.LastError:X}（{backoff}s 后可重试注入）");
                        // 不要进入下面的保活循环；否则同一 PID 会永远停在 Error，
                        // 外层的退避重试逻辑永远没有机会执行。
                        continue;
                    }
                    else
                    {
                        SetStatus($"Stub 未就绪: Status={st.Status}, LastError=0x{st.LastError:X}（已注入，监视中）");
                    }
                }

                // 游戏运行期间保活（PushConfigToIpc 内部已节流）
                while (!token.IsCancellationRequested && _config.MasterEnabled && _config.AutoWatch)
                {
                    try
                    {
                        if (process.HasExited) break;
                    }
                    catch { break; }

                    PushConfigToIpc();
                    var st = _ipc.Read();
                    if (st.Status == IpcStatus.Error)
                    {
                        AppLog.Error($"stub entered Error while attached pid={process.Id} lastError=0x{st.LastError:X}");
                        Volatile.Write(ref _injectAttemptedPid, 0);
                        _injectFailStreak++;
                        _nextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Min(90, 15 * _injectFailStreak));
                        break;
                    }
                    SetStatus($"运行中 PID {process.Id} | Stub={st.Status} | 目标 {_config.TargetFps} | 反馈 {st.CurrentFps}");
                    await Task.Delay(activePoll, token);
                }

                SetAttached(0);
                // 暂停时保留已经加载的 DLL 连接；重新开启不应重置其 Ready 状态。
                if (_config.MasterEnabled && _config.AutoWatch)
                    Volatile.Write(ref _injectAttemptedPid, 0);
                await Task.Delay(250, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "watch loop");
                SetStatus($"监视循环异常: {ex.Message}");
                try { await Task.Delay(2000, token); } catch { break; }
            }
        }

        AppLog.Info("UnlockService watch loop ended");
    }

    /// <summary>
    /// 统一维护附着 PID：同时把「系统保持唤醒」的请求绑定到游戏是否真的在跑。
    /// 旧实现在启动时就一直请求，程序常驻托盘 → 系统永不自动睡眠。
    /// </summary>
    private void SetAttached(int pid)
    {
        Volatile.Write(ref _attachedPid, pid);
        BackgroundResilience.SetGameActive(pid != 0);
    }

    /// <summary>从运行中进程回写游戏路径（中文路径优先 QueryFullProcessImageName）。</summary>
    private void TryCapturePathFromProcess(Process process)
    {
        try
        {
            var path = PathUtil.GetProcessImagePath(process.Id)
                       ?? process.MainModule?.FileName;
            path = PathUtil.Normalize(path);
            if (GameLocator.IsValidGameExe(path) &&
                !PathUtil.EqualsPath(_config.GamePath, path))
            {
                _config.GamePath = path;
                if (!_config.TrySave(out var pathSaveErr))
                    AppLog.Warn("game path save: " + pathSaveErr);
                Volatile.Write(ref _gamePathStatus, $"游戏路径: {path}（运行中进程）");
                AppLog.Info("captured game path from process: " + path);
                Raise(forceUi: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("TryCapturePathFromProcess: " + ex.Message);
        }
    }

    /// <summary>等待主窗口出现后再注入（Unity 初始化完成更稳）。</summary>
    private static async Task WaitForMainWindowAsync(Process process, CancellationToken token, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !token.IsCancellationRequested)
        {
            try
            {
                if (process.HasExited) return;
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    await Task.Delay(1500, token);
                    return;
                }
            }
            catch { return; }

            await Task.Delay(400, token);
        }
    }

    /// <summary>轮询共享内存直到 Stub 报告 Ready 或 Error。</summary>
    private async Task<bool> WaitForStubReadyAsync(CancellationToken token, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !token.IsCancellationRequested)
        {
            var st = _ipc.Read();
            if (st.Status == IpcStatus.Ready) return true;
            if (st.Status == IpcStatus.Error) return false;
            await Task.Delay(200, token);
        }
        return _ipc.Read().Status == IpcStatus.Ready;
    }

    /// <summary>更新状态文本（相同内容跳过，避免无意义刷新）。</summary>
    private void SetStatus(string text)
    {
        if (Volatile.Read(ref _statusText) == text) return;
        Volatile.Write(ref _statusText, text);
        Raise(forceUi: false);
    }

    /// <summary>触发 StateChanged；默认 250ms 节流，forceUi 时立即触发。</summary>
    private void Raise(bool forceUi)
    {
        var now = DateTime.UtcNow;
        if (!forceUi && (now - _lastUiRaiseUtc).TotalMilliseconds < 250)
            return;
        _lastUiRaiseUtc = now;

        lock (_raiseLock)
        {
            try { StateChanged?.Invoke(); }
            catch (Exception ex) { AppLog.Debug("StateChanged handler: " + ex.Message); }
        }
    }
}
