using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 监视循环与注入时序（partial）。两款游戏共用这一套循环：
/// 进程查找、路径捕获、注入、保活都按当前游戏自己的档案与注入模块执行。
/// </summary>
internal sealed partial class UnlockService
{
    /// <summary>
    /// 监视主循环：
    /// 0) 先按固定节拍清理上一版本残留在原神目录里的代理组件
    /// 1) 总开关/自动监视关闭 → 空闲等待
    /// 2) 星穹铁道注册表解锁（与进程无关，按需核对）
    /// 3) 无游戏 → 长间隔轮询
    /// 4) 已注入同 PID → 保活推送 IPC
    /// 5) 新 PID → 等主窗口 → 注入该游戏的 Stub → 等 Ready → 保活直到退出
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
                // 与解锁状态无关：残留清理是历史数据的回收，不注入、不改配置。
                TryRunLegacyCleanup();

                if (!_config.MasterEnabled)
                {
                    PushConfigToIpc();
                    SetAttached(null, 0);
                    SetStatus("总开关已关闭 — 后台待命（不注入）");
                    await Task.Delay(idlePoll, token);
                    continue;
                }

                if (!_config.AutoWatch)
                {
                    PushConfigToIpc();
                    SetAttached(null, 0);
                    SetStatus("自动监视已关闭 — 可在托盘重新开启");
                    await Task.Delay(idlePoll, token);
                    continue;
                }

                // 星穹铁道的帧率写注册表：不依赖进程，启用后按需核对一次。
                if (_sessions[GameId.StarRail].RegistryCheckPending)
                    SyncStarRailRegistry();

                // 首次运行时的后台自动定位：放在监视线程上做，避免卡住界面。
                TryAutoLocateMissingPaths();

                using var process = FindRunningGame(out var runningGame);
                if (process is null || runningGame is null)
                {
                    _runningGame = null;
                    // 没有游戏进程：清掉待核对标记，避免退出后才补写注册表。
                    _sessions[GameId.StarRail].RegistryCheckPending = false;
                    _sessions[GameId.StarRail].RegistryCheckedPid = 0;

                    if (_attachedPid != 0 || AnyInjectAttempted())
                    {
                        ResetInjectState();
                        SetAttached(null, 0);
                        SetStatus("游戏已退出 — 继续后台等待");
                        AppLog.Info("game process exited");
                    }
                    else
                    {
                        // SetStatus 内部去重，避免每秒刷 UI
                        SetStatus($"后台运行中 — 等待{WaitProcessNames()}启动");
                    }

                    await Task.Delay(idlePoll, token);
                    continue;
                }

                var game = runningGame.Value;
                // 运行状态按游戏归属：界面只让这一款显示「运行中」，另一款保持等待启动。
                _runningGame = game;
                var descriptor = GameCatalog.Get(game);
                var profile = _config.Profile(game);
                var session = _sessions[game];

                // Process 对象可能对应一个刚退出的残留进程：先确认存活，
                // 否则会去读它的模块（部分读取失败），并在退出后触发注册表核对。
                try
                {
                    if (process.HasExited)
                    {
                        session.RegistryCheckPending = false;
                        session.RegistryCheckedPid = 0;
                        session.InjectAttemptedPid = 0;
                        _runningGame = null;
                        SetAttached(null, 0);
                        await Task.Delay(activePoll, token);
                        continue;
                    }
                }
                catch
                {
                    session.RegistryCheckPending = false;
                    session.RegistryCheckedPid = 0;
                    session.InjectAttemptedPid = 0;
                    _runningGame = null;
                    SetAttached(null, 0);
                    await Task.Delay(activePoll, token);
                    continue;
                }

                // 之前附着的是另一款游戏：它的进程已经退出（否则上面会优先返回它），
                // 先收尾旧会话，IPC 槽位再交给现在这款游戏。
                if (_attachedGame is GameId previous && previous != game)
                {
                    var old = _sessions[previous];
                    old.InjectAttemptedPid = 0;
                    old.InjectFailStreak = 0;
                    SetAttached(null, 0);
                    AppLog.Info($"detach {GameCatalog.Get(previous).Key}: 让位给 {descriptor.Key}");
                }

                // 首次发现该游戏进程：注册表解锁的游戏重新核对一次（新启动要读新值）。
                // 用 PID 去重，避免同一进程在注入重试期间反复写注册表。
                if (descriptor.FpsViaRegistry && profile.Enabled && session.RegistryCheckedPid != process.Id)
                    session.RegistryCheckPending = true;

                TryCapturePathFromProcess(descriptor, process);

                // 已对应该 PID 注入过 — 轻量保活
                if (session.InjectAttemptedPid == process.Id)
                {
                    PushConfigToIpc();
                    var live = _ipc.Read();
                    if (live.Status == IpcStatus.Error)
                    {
                        // 该分支此前不写状态文本，用户在 UI 上看不到 Stub 报错的恢复过程；
                        // 与下游 Error 路径同一措辞给出原因与退避时长。
                        SetAttached(null, 0);
                        session.InjectAttemptedPid = 0;
                        session.InjectFailStreak++;
                        var backoff = Math.Min(90, 15 * session.InjectFailStreak);
                        session.NextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
                        SetStatus($"{descriptor.ShortName}：Stub 报告错误 0x{live.LastError:X}（{backoff}s 后可重试注入）");
                        continue;
                    }
                    if (live.Status == IpcStatus.Exiting)
                    {
                        // 关闭开关时 Host 请求 Stub 结束当前会话；重新开启时让外层
                        // 重新走注入/ResetForNewInject，而不是把 Exiting 当成附着状态。
                        SetAttached(null, 0);
                        session.InjectAttemptedPid = 0;
                        continue;
                    }
                    SetAttached(game, live.Status == IpcStatus.Ready && NeedsInjection(game) ? process.Id : 0);
                    SetStatus(AttachedStatusText(descriptor, profile, process.Id, live));

                    await Task.Delay(activePoll, token);

                    try
                    {
                        if (process.HasExited)
                        {
                            SetAttached(null, 0);
                            session.InjectAttemptedPid = 0;
                        }
                    }
                    catch
                    {
                        SetAttached(null, 0);
                        session.InjectAttemptedPid = 0;
                    }

                    continue;
                }

                if (token.IsCancellationRequested) break;

                if (!NeedsInjection(game))
                {
                    // 星铁：进程已确认在运行，此时才消费「新启动需核对注册表」标记。
                    if (descriptor.FpsViaRegistry && session.RegistryCheckPending)
                    {
                        session.RegistryCheckedPid = process.Id;
                        SyncStarRailRegistry();
                    }

                    // 星穹铁道默认就落在这里：帧率走注册表，不需要注入。
                    SetStatus(descriptor.FpsViaRegistry && profile.Enabled
                        ? $"{descriptor.ShortName}运行中 PID {process.Id} — 帧率由注册表解锁（{StarRailRegistrySummary()}），无需注入"
                        : $"{descriptor.ShortName}运行中 PID {process.Id}，当前没有启用需要注入的功能");
                    await Task.Delay(idlePoll, token);
                    continue;
                }

                if (!PathUtil.ExistsFile(session.StubPath))
                {
                    SetStatus($"{descriptor.ShortName}：缺少 {descriptor.StubFileName}（应位于: {session.StubPath}）");
                    await Task.Delay(5000, token);
                    continue;
                }

                // 注入前的最后一道防线：目标进程必须就是这款游戏，绝不把 A 游戏的
                // Stub 注进 B 游戏进程（路径读不到时按进程名放行，保持原可用性）。
                if (!ProcessMatchesGame(descriptor, process, out var mismatch))
                {
                    SetStatus($"{descriptor.ShortName}：进程与游戏不匹配，已跳过注入");
                    AppLog.Warn($"inject skipped game={descriptor.Key} pid={process.Id}: {mismatch}");
                    session.NextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                    await Task.Delay(activePoll, token);
                    continue;
                }

                // 注入前校验模块可信度：已提权时安装目录必须受保护（Program Files），
                // 否则用户可写目录里的同名 DLL 会被我们的管理员令牌注入游戏，
                // 或在备用 Hook 注入路径下被映射进 Host 自己。与卸载器同一套检查。
                if (!ModuleTrust.IsTrustworthy(
                        session.StubPath,
                        descriptor.StubFileName,
                        $"{descriptor.DisplayName}注入模块",
                        out var trustError,
                        elevatedHint: "请把程序安装到 Program Files 下，或退出管理员实例后以普通权限运行。"))
                {
                    SetStatus($"拒绝注入：{trustError}");
                    AppLog.Error("stub 可信度校验失败: " + trustError);
                    session.NextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(60);
                    await Task.Delay(5000, token);
                    continue;
                }

                // 注入失败后的退避窗口
                if (DateTime.UtcNow < session.NextInjectAttemptUtc)
                {
                    var waitSec = Math.Max(1, (int)(session.NextInjectAttemptUtc - DateTime.UtcNow).TotalSeconds);
                    SetStatus($"注入冷却中（{waitSec}s）… 上次失败后的退避");
                    await Task.Delay(1000, token);
                    continue;
                }

                // 注入前重置 Stub 状态字段，并推送最新 Host 配置（勿整块乱序写）
                _config.Sanitize();
                var activeUnlock = profile.Enabled && !descriptor.FpsViaRegistry;
                var featuresActive = _config.MasterEnabled && _config.AutoWatch;
                _ipc.ResetForNewInject(profile.TargetFps, activeUnlock,
                    featuresActive && profile.AntiBlurPerspective,
                    featuresActive && descriptor.SupportsDiveMosaic && profile.AntiBlurDiveMosaic,
                    featuresActive && profile.HideUid);
                _ipcOwner = game;
                _lastPushedFps = profile.TargetFps;
                _lastPushedEnabled = activeUnlock ? 1 : 0;
                _lastIpcPushUtc = DateTime.UtcNow;

                SetStatus($"{descriptor.ShortName}：检测到游戏 PID {process.Id}，等待主窗口后注入…");
                await WaitForMainWindowAsync(process, token, TimeSpan.FromSeconds(45));

                if (token.IsCancellationRequested) break;
                try
                {
                    if (process.HasExited)
                    {
                        session.RegistryCheckPending = false;
                        session.RegistryCheckedPid = 0;
                        _runningGame = null;
                        continue;
                    }
                }
                catch
                {
                    session.RegistryCheckPending = false;
                    session.RegistryCheckedPid = 0;
                    _runningGame = null;
                    continue;
                }

                // 星铁：主窗口出现说明游戏已完成初始化、画面设置已写入注册表，
                // 此时核对并确保 120 FPS；不要留到进程退出后再补做。
                if (descriptor.FpsViaRegistry && session.RegistryCheckPending)
                {
                    session.RegistryCheckedPid = process.Id;
                    SyncStarRailRegistry();
                }

                SetStatus($"{descriptor.ShortName}：正在注入 {descriptor.StubFileName} → PID {process.Id}…");
                AppLog.Info($"inject begin game={descriptor.Key} pid={process.Id} stub={session.StubPath}");
                if (!DllInjector.TryInject(process, session.StubPath, out var error))
                {
                    session.InjectFailStreak++;
                    var backoff = Math.Min(60, 5 * session.InjectFailStreak);
                    session.NextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
                    AppLog.Error($"inject failed pid={process.Id} streak={session.InjectFailStreak} backoff={backoff}s: {error}");
                    var adminHint = Elevation.IsAdministrator()
                        ? string.Empty
                        : " — 可在界面或托盘选择「以管理员重新启动」";
                    SetStatus($"注入失败: {error}（{backoff}s 后重试）{adminHint}");
                    await Task.Delay(1000, token);
                    continue;
                }

                session.InjectFailStreak = 0;
                session.NextInjectAttemptUtc = DateTime.MinValue;
                AppLog.Info($"inject OK pid={process.Id}, waiting stub ready…");
                session.InjectAttemptedPid = process.Id;

                var ok = await WaitForStubReadyAsync(token, TimeSpan.FromSeconds(90));
                if (ok)
                {
                    SetAttached(game, process.Id);
                    AppLog.Info($"stub Ready game={descriptor.Key} pid={process.Id} targetFps={profile.TargetFps}");
                    SetStatus($"{descriptor.ShortName}：解锁成功 PID {process.Id} | 目标 {profile.TargetFps} FPS");
                }
                else
                {
                    var st = _ipc.Read();
                    AppLog.Error($"stub not ready pid={process.Id} status={st.Status} lastError=0x{st.LastError:X}");
                    // 已注入但未 Ready：短时保活观察；若长期 Error/None 则允许冷却后重试
                    SetAttached(game, process.Id);
                    if (st.Status == IpcStatus.Error)
                    {
                        session.InjectFailStreak++;
                        var backoff = Math.Min(90, 15 * session.InjectFailStreak);
                        session.NextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
                        session.InjectAttemptedPid = 0;
                        SetAttached(null, 0);
                        SetStatus($"{descriptor.ShortName}：Stub 报告错误 0x{st.LastError:X}（{backoff}s 后可重试注入）");
                        // 不要进入下面的保活循环；否则同一 PID 会永远停在 Error，
                        // 外层的退避重试逻辑永远没有机会执行。
                        continue;
                    }
                    else
                    {
                        SetStatus($"{descriptor.ShortName}：Stub 未就绪 Status={st.Status}, LastError=0x{st.LastError:X}（已注入，监视中）");
                    }
                }

                // 游戏运行期间保活（PushConfigToIpc 内部已节流）
                var processExited = false;
                while (!token.IsCancellationRequested && _config.MasterEnabled && _config.AutoWatch)
                {
                    try
                    {
                        if (process.HasExited) { processExited = true; break; }
                    }
                    catch { processExited = true; break; }

                    PushConfigToIpc();
                    var st = _ipc.Read();
                    if (st.Status == IpcStatus.Error)
                    {
                        AppLog.Error($"stub entered Error while attached pid={process.Id} lastError=0x{st.LastError:X}");
                        session.InjectAttemptedPid = 0;
                        session.InjectFailStreak++;
                        session.NextInjectAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Min(90, 15 * session.InjectFailStreak));
                        break;
                    }
                    SetStatus($"{descriptor.ShortName}：运行中 PID {process.Id} | Stub={st.Status} | 目标 {profile.TargetFps} | 反馈 {st.CurrentFps}");
                    await Task.Delay(activePoll, token);
                }

                SetAttached(null, 0);
                // 附着结束（进程退出 / 暂停 / Stub 出错）后不再补做注册表核对，
                // 否则会在游戏退出后才去写注册表并弹出「请先启动一次游戏」。
                session.RegistryCheckPending = false;
                session.RegistryCheckedPid = 0;
                if (processExited)
                {
                    _runningGame = null;
                    if (descriptor.FpsViaRegistry)
                        session.RegistryStatus = "游戏未运行 — 启动后会自动核对注册表";
                }
                // 暂停时保留已经加载的 DLL 连接；重新开启不应重置其 Ready 状态。
                if (_config.MasterEnabled && _config.AutoWatch)
                    session.InjectAttemptedPid = 0;
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
    /// 找出当前要处理的游戏进程。优先级：已经附着的游戏（保持不换）→ 界面上选中的游戏
    /// → 目录顺序。共享内存只有一个槽位，所以同时只服务一款游戏。
    /// </summary>
    private Process? FindRunningGame(out GameId? game)
    {
        var order = new List<GameDescriptor>();
        if (_attachedGame is GameId attached) order.Add(GameCatalog.Get(attached));
        order.Add(GameCatalog.Get(_config.ActiveGame));
        foreach (var descriptor in GameCatalog.All)
        {
            if (!order.Contains(descriptor)) order.Add(descriptor);
        }

        foreach (var descriptor in order)
        {
            var process = GameProcess.Find(descriptor);
            if (process is null) continue;
            game = descriptor.Id;
            return process;
        }

        game = null;
        return null;
    }

    /// <summary>启动后为还没有路径的游戏各做一次后台自动定位（每款游戏只做一次）。</summary>
    private void TryAutoLocateMissingPaths()
    {
        foreach (var descriptor in GameCatalog.All)
        {
            var session = _sessions[descriptor.Id];
            if (session.AutoLocateAttempted) continue;
            var profile = _config.Profile(descriptor.Id);
            if (GameLocator.IsValidGameExe(descriptor, profile.GamePath))
            {
                session.AutoLocateAttempted = true;
                continue;
            }

            session.AutoLocateAttempted = true;
            try
            {
                var result = GameLocator.LocateAutomatic(descriptor, null);
                if (!result.Ok || result.Path is null)
                {
                    session.PathStatus = $"游戏路径: 未找到 — {result.Detail}";
                    continue;
                }
                profile.GamePath = PathUtil.Normalize(result.Path);
                _config.TrySave(out _);
                session.PathStatus = $"游戏路径: {profile.GamePath}（{GameLocator.SourceDisplayName(result.Source)}）";
                AppLog.Info($"game path auto ({descriptor.Key}): {profile.GamePath} source={result.Source}");
                if (descriptor.Id == GameId.Genshin) QueueLegacyCleanup();
                Raise(forceUi: true);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"auto locate ({descriptor.Key}): {ex.Message}");
            }
        }
    }

    /// <summary>等待启动的游戏名列表，用于空闲状态文案。</summary>
    private static string WaitProcessNames() =>
        string.Join(" / ", GameCatalog.All.Select(game => game.DisplayName));

    /// <summary>星穹铁道注册表结果的短摘要（状态行用）。</summary>
    private string StarRailRegistrySummary()
    {
        var status = _sessions[GameId.StarRail].RegistryStatus;
        return string.IsNullOrWhiteSpace(status) ? "尚未检查" : status;
    }

    /// <summary>附着状态的统一文案。</summary>
    private static string AttachedStatusText(GameDescriptor descriptor, GameProfile profile, int pid, IpcData live)
    {
        if (descriptor.FpsViaRegistry)
            return $"{descriptor.ShortName}：已附着 PID {pid} | 画面效果 {live.Status} | 帧率走注册表";
        return $"{descriptor.ShortName}：已附着 PID {pid} | Stub={live.Status} | 目标 {profile.TargetFps} FPS | 反馈 {live.CurrentFps}";
    }

    /// <summary>是否还有任一游戏处于「已尝试注入」状态。</summary>
    private bool AnyInjectAttempted()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.InjectAttemptedPid != 0) return true;
        }
        return false;
    }

    /// <summary>清空所有游戏的注入尝试记录（游戏全部退出时）。</summary>
    private void ResetInjectState()
    {
        foreach (var session in _sessions.Values)
        {
            session.InjectAttemptedPid = 0;
            session.InjectFailStreak = 0;
        }
    }

    /// <summary>
    /// 统一维护附着状态：同时把「系统保持唤醒」的请求绑定到游戏是否真的在跑。
    /// 旧实现在启动时就一直请求，程序常驻托盘 → 系统永不自动睡眠。
    /// </summary>
    private void SetAttached(GameId? game, int pid)
    {
        _attachedGame = pid != 0 ? game : null;
        Volatile.Write(ref _attachedPid, pid);
        BackgroundResilience.SetGameActive(pid != 0);
    }

    /// <summary>从运行中进程回写该游戏的路径（中文路径优先 QueryFullProcessImageName）。</summary>
    private void TryCapturePathFromProcess(GameDescriptor descriptor, Process process)
    {
        try
        {
            var path = PathUtil.GetProcessImagePath(process.Id)
                       ?? process.MainModule?.FileName;
            path = PathUtil.Normalize(path);
            var profile = _config.Profile(descriptor.Id);
            if (GameLocator.IsValidGameExe(descriptor, path) &&
                !PathUtil.EqualsPath(profile.GamePath, path))
            {
                profile.GamePath = path;
                if (!_config.TrySave(out var pathSaveErr))
                    AppLog.Warn("game path save: " + pathSaveErr);
                _sessions[descriptor.Id].PathStatus = $"游戏路径: {path}（运行中进程）";
                AppLog.Info($"captured game path from process ({descriptor.Key}): {path}");
                Raise(forceUi: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("TryCapturePathFromProcess: " + ex.Message);
        }
    }

    /// <summary>
    /// 目标进程是否确实是这款游戏。进程路径能读到就必须是本游戏主程序；
    /// 读不到（反作弊保护、权限不足）时按进程名放行，不因为拿不到路径就放弃注入。
    /// </summary>
    private static bool ProcessMatchesGame(GameDescriptor descriptor, Process process, out string detail)
    {
        detail = string.Empty;
        try
        {
            var raw = PathUtil.GetProcessImagePath(process.Id);
            if (string.IsNullOrEmpty(raw)) raw = process.MainModule?.FileName;
            var path = PathUtil.Normalize(raw);
            if (string.IsNullOrEmpty(path)) return true;
            if (GameLocator.IsValidGameExe(descriptor, path)) return true;
            detail = path;
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return true;
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
