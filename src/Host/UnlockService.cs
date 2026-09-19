using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 核心后台服务：按游戏分别监视进程、注入各自的 Stub、经共享内存下发目标 FPS；
/// 崩坏：星穹铁道的帧率不走注入，而是直接改注册表里的画面设置。
/// UI / 托盘通过属性与 StateChanged 事件读取状态；本类负责节流以降低开销。
/// 另外负责一次性回收上一版本「超分替换」留在原神目录里的代理组件
/// （见 <see cref="LegacyProxyCleanup"/>）：只删不写，且不受解锁开关影响。
/// </summary>
internal sealed partial class UnlockService : IDisposable
{
    /// <summary>
    /// 单款游戏的运行期状态：注入尝试 / 退避 / 路径与注册表核对结果。
    /// 两款游戏各持一份，互不覆盖。
    /// </summary>
    private sealed class GameSession
    {
        /// <summary>已尝试注入的 PID（含 Stub 未就绪），避免对同一进程重复注入。</summary>
        public int InjectAttemptedPid;
        /// <summary>连续注入失败次数，用于指数退避。</summary>
        public int InjectFailStreak;
        /// <summary>下一次允许尝试注入的 UTC 时间。</summary>
        public DateTime NextInjectAttemptUtc = DateTime.MinValue;
        /// <summary>该游戏的路径状态文案（UI 按当前游戏显示）。</summary>
        public string PathStatus = "";
        /// <summary>本进程内是否已经跑过自动定位（避免反复扫盘）。</summary>
        public bool AutoLocateAttempted;
        /// <summary>星穹铁道：是否需要重新核对注册表。</summary>
        public bool RegistryCheckPending = true;
        /// <summary>星穹铁道：最近一次注册表核对的结果文案。</summary>
        public string RegistryStatus = "尚未检查注册表";
        /// <summary>该游戏注入模块的完整路径。</summary>
        public string StubPath = "";
    }

    private readonly AppConfig _config;
    private readonly IpcSharedMemory _ipc;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _raiseLock = new();
    private readonly Dictionary<GameId, GameSession> _sessions =
        GameCatalog.All.ToDictionary(game => game.Id, _ => new GameSession());

    private Task? _loop;
    /// <summary>当前认为已成功附着的游戏与 PID（IPC 只有一个槽位，同时只附着一款）。</summary>
    private GameId? _attachedGame;
    /// <summary>
    /// 共享内存当前属于哪款游戏的 Stub（注入时确定）。映射只有一个槽位，
    /// 属于 A 游戏时不能再拿 B 游戏的档案去写它，否则会把 A 的目标帧率 / 开关冲掉。
    /// </summary>
    private GameId? _ipcOwner;
    private int _attachedPid;
    private string _statusText = "空闲 — 等待游戏启动";
    private bool _disposed;

    // ---- IPC / UI 节流状态 ----
    private int _lastPushedFps = int.MinValue;
    private int _lastPushedEnabled = int.MinValue;
    private DateTime _lastIpcPushUtc = DateTime.MinValue;
    private DateTime _lastUiRaiseUtc = DateTime.MinValue;

    // ---- 历史残留组件清理（见 LegacyProxyCleanup，仅原神） ----
    /// <summary>游戏目录里是否还可能有上一版本部署的代理组件（1 = 需要重试清理）。</summary>
    private int _legacyCleanupPending;
    /// <summary>下一次允许重试清理的 UTC 时间：文件被游戏占用时不必每轮都撞一遍。</summary>
    private DateTime _nextLegacyCleanupUtc = DateTime.MinValue;

    /// <summary>状态变化（UI 应 Invoke 到 UI 线程后刷新）。</summary>
    public event Action? StateChanged;

    // 这两个串由后台监视线程写、UI 线程读：引用赋值本身原子，但没有屏障时
    // UI 可能长时间读到旧值，所以显式走 Volatile（也把这层意图写在代码里）。
    public string StatusText => Volatile.Read(ref _statusText);
    /// <summary>当前配置中那款游戏的路径状态（切游戏时界面跟着换）。</summary>
    public string GamePathStatus => _sessions[_config.ActiveGame].PathStatus;
    public int AttachedPid => Volatile.Read(ref _attachedPid);
    /// <summary>当前附着的是哪款游戏（未附着时为 null）。</summary>
    public GameId? AttachedGame => _attachedGame;
    public IpcStatus StubStatus => _ipc.Read().Status;
    public int CurrentFpsFeedback => _ipc.Read().CurrentFps;

    /// <summary>Stub 上报的错误码（0xE001 起，0 表示无错误）。</summary>
    public int LastErrorFeedback => _ipc.Read().LastError;

    /// <summary>Stub 上报的反虚化就绪状态掩码（bit0 虚化 / bit1 马赛克 / bit2 马赛克已生效）。</summary>
    public int AntiBlurStateFeedback => _ipc.Read().AntiBlurState;

    /// <summary>Stub 上报的 UID 隐藏状态掩码（bit0 已就绪 / bit1 隐藏生效中）。</summary>
    public int HideUidStateFeedback => _ipc.Read().HideUidState;

    /// <summary>星穹铁道注册表解锁的最近一次核对结果（未启用时说明原因）。</summary>
    public string StarRailRegistryStatus => _sessions[GameId.StarRail].RegistryStatus;

    public AppConfig Config => _config;

    /// <summary>指定游戏是否处于「启用」状态且总开关打开。</summary>
    public bool IsGameEnabled(GameId game) =>
        _config.MasterEnabled && _config.Profile(game).Enabled;

    /// <summary>
    /// 指定游戏是否有任一功能需要把 Stub 注入到游戏进程。
    /// 星穹铁道的帧率走注册表，只有画面效果才需要注入；
    /// 自动监视关闭时，同时停用已经附着的功能，避免 Stub 继续强制写入。
    /// </summary>
    private bool NeedsInjection(GameId game)
    {
        if (!_config.MasterEnabled || !_config.AutoWatch) return false;
        var descriptor = GameCatalog.Get(game);
        var profile = _config.Profile(game);
        var fpsNeedsInject = profile.Enabled && !descriptor.FpsViaRegistry;
        var features = profile.AntiBlurPerspective
                       || profile.HideUid
                       || (descriptor.SupportsDiveMosaic && profile.AntiBlurDiveMosaic);
        return fpsNeedsInject || features;
    }

    public UnlockService(AppConfig config)
    {
        _config = config;
        try
        {
            _ipc = new IpcSharedMemory();
        }
        catch (Exception ex)
        {
            // 共享内存失败不应阻止主窗/托盘；后续注入会提示
            AppLog.Error(ex, "IpcSharedMemory");
            throw new InvalidOperationException(
                "无法初始化进程通信（共享内存）。\n" +
                "请确认以当前用户身份运行（无需管理员），并检查安全软件是否拦截。\n" +
                ex.Message, ex);
        }

        foreach (var game in GameCatalog.All)
        {
            var session = _sessions[game.Id];
            session.StubPath = PathUtil.Normalize(AppPaths.StubPathFor(game));
            session.PathStatus = "游戏路径: 未设置";
        }

        // 配置里已经写好的路径直接确认；缺失的交给后台监视循环异步查找，
        // 避免启动时在 UI 线程上跑磁盘扫描。
        try { RefreshGamePath(GameId.Genshin, autoLocateIfMissing: false); } catch (Exception ex) { AppLog.Warn(ex.Message); }
        try { RefreshGamePath(GameId.StarRail, autoLocateIfMissing: false); } catch (Exception ex) { AppLog.Warn(ex.Message); }
        try { PushConfigToIpc(force: true); } catch (Exception ex) { AppLog.Warn(ex.Message); }
    }

    /// <summary>启动后台监视循环（线程池 Task）。</summary>
    public void Start()
    {
        AppLog.Info("UnlockService.Start()");
        _loop = Task.Run(() => WatchLoopAsync(_cts.Token));
    }

    /// <summary>切换当前正在配置的游戏（界面三个游戏页跟着换）。</summary>
    public void SetActiveGame(GameId game)
    {
        if (_config.ActiveGame == game) return;
        _config.ActiveGame = game;
        AppLog.Info($"active game → {GameCatalog.Get(game).Key}");
        _config.TrySave(out _);
        Raise(forceUi: true);
    }

    /// <summary>
    /// 游戏路径确定或变化后，标记「需要检查上一版本残留在游戏目录里的代理组件」。
    /// 实际删除在监视循环的固定节拍里做：文件被运行中的游戏占用时下一轮继续重试。
    /// 该清理只针对原神（历史版本只在原神目录里部署过代理组件）。
    /// </summary>
    public void QueueLegacyCleanup()
    {
        _nextLegacyCleanupUtc = DateTime.MinValue;
        Interlocked.Exchange(ref _legacyCleanupPending, 1);
    }

    /// <summary>监视循环节拍：清理尚未删掉的残留组件（没有待处理项时几乎零开销）。</summary>
    private void TryRunLegacyCleanup()
    {
        if (Volatile.Read(ref _legacyCleanupPending) == 0) return;
        var now = DateTime.UtcNow;
        if (now < _nextLegacyCleanupUtc) return;
        _nextLegacyCleanupUtc = now.AddSeconds(10);

        if (LegacyProxyCleanup.TryCleanup(_config.Profile(GameId.Genshin).GamePath))
            Interlocked.Exchange(ref _legacyCleanupPending, 0);
    }

    /// <summary>
    /// 将当前附着游戏（没有附着时用界面上选中的游戏）的目标 FPS 与有效开关推入共享内存。
    /// 默认 400ms 内相同值不重复写，降低 IPC 与日志噪声；force=true 立即推送。
    /// </summary>
    public void PushConfigToIpc(bool force = false)
    {
        _config.Sanitize();
        var game = _attachedGame ?? _config.ActiveGame;
        // 映射已经被另一款游戏的 Stub 占用（它可能仍在运行）：不要动它的任何字段。
        if (_ipcOwner is GameId owner && owner != game) return;
        var descriptor = GameCatalog.Get(game);
        var profile = _config.Profile(game);
        var fps = profile.TargetFps;
        // 走注册表的游戏不通过 Stub 改帧率，只下发画面效果开关。
        var en = NeedsInjection(game) && profile.Enabled && !descriptor.FpsViaRegistry ? 1 : 0;
        var now = DateTime.UtcNow;

        // 跳过冗余写入
        var changed = fps != _lastPushedFps || en != _lastPushedEnabled;
        if (!force && !changed && (now - _lastIpcPushUtc).TotalMilliseconds < 400)
        {
            return;
        }

        var featuresActive = _config.MasterEnabled && _config.AutoWatch;
        var stubShouldRun = NeedsInjection(game);
        if (!stubShouldRun)
        {
            var status = _ipc.Read().Status;
            if (status is IpcStatus.Waiting or IpcStatus.Ready)
                _ipc.RequestExit();
        }
        _ipc.UpdateHostFields(fps, en != 0,
            featuresActive && profile.AntiBlurPerspective,
            featuresActive && descriptor.SupportsDiveMosaic && profile.AntiBlurDiveMosaic,
            featuresActive && profile.HideUid);
        _lastPushedFps = fps;
        _lastPushedEnabled = en;
        _lastIpcPushUtc = now;

        // 只在「真的变了」或强制推送时记一行：保活式的重复写入每秒能有两三次，
        // 全记下来会把日志文件和界面日志页（现在会实时增量显示宿主日志）刷满。
        if (force || changed)
            AppLog.Debug($"IPC push game={descriptor.Key} fps={fps} effective={en != 0}");
        Raise(forceUi: force);
    }

    /// <summary>设置指定游戏的目标帧率并持久化、立即推送 IPC。</summary>
    public void ApplyFps(GameId game, int fps)
    {
        var descriptor = GameCatalog.Get(game);
        _config.Profile(game).TargetFps = descriptor.LockedFps > 0 ? descriptor.LockedFps : fps;
        _config.Sanitize();
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>设置指定游戏的帧率解锁开关并推送。</summary>
    public void SetEnabled(GameId game, bool enabled)
    {
        var profile = _config.Profile(game);
        profile.Enabled = enabled;
        _config.TrySave(out _);

        var viaRegistry = GameCatalog.Get(game).FpsViaRegistry;
        // 星穹铁道：开启即核对注册表（已是 120 就不覆盖），关闭不动注册表。
        if (viaRegistry) _sessions[game].RegistryCheckPending = enabled;

        PushConfigToIpc(force: true);
        if (viaRegistry && enabled) SyncStarRailRegistry(force: true);
    }

    /// <summary>设置总开关：关闭时不注入、不强制帧率。</summary>
    public void SetMasterEnabled(bool enabled)
    {
        AppLog.Info($"SetMasterEnabled={enabled}");
        _config.MasterEnabled = enabled;
        _config.TrySave(out _);
        if (enabled) _sessions[GameId.StarRail].RegistryCheckPending = true;
        PushConfigToIpc(force: true);
        SetStatus(enabled
            ? (_config.AutoWatch ? "总开关已开启 — 后台监视中" : "总开关已开启 — 自动监视关闭")
            : "总开关已关闭 — 不会注入 / 不会强制帧率");
    }

    /// <summary>是否自动监视并注入游戏进程。</summary>
    public void SetAutoWatch(bool enabled)
    {
        _config.AutoWatch = enabled;
        _config.TrySave(out _);
        // 关闭自动监视也必须立即停用已有 Stub；否则后台循环可能还在保活并强制写 FPS。
        PushConfigToIpc(force: true);
        if (!enabled)
        {
            SetAttached(null, 0);
        }
        Raise(forceUi: true);
    }

    /// <summary>反角色虚化注入开关（迁移自 Snap.Hutao.Remastered）：保存并推送 IPC。</summary>
    public void SetAntiBlurPerspective(GameId game, bool enabled)
    {
        _config.Profile(game).AntiBlurPerspective = enabled;
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>移除水下马赛克注入开关（仅原神模块提供）：保存并推送 IPC。</summary>
    public void SetAntiBlurDiveMosaic(GameId game, bool enabled)
    {
        var descriptor = GameCatalog.Get(game);
        _config.Profile(game).AntiBlurDiveMosaic = descriptor.SupportsDiveMosaic && enabled;
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>隐藏 UID 注入开关（同源迁移自 Snap.Hutao.Remastered）：保存并推送 IPC。</summary>
    public void SetHideUid(GameId game, bool enabled)
    {
        _config.Profile(game).HideUid = enabled;
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>
    /// 按配置同步登录自启：普通权限写 HKCU\Run，管理员权限登记最高权限计划任务
    /// （二选一，见 <see cref="Autostart.SyncLoginStartup"/>）。结果同时记录在
    /// <see cref="Autostart.LastReport"/>，界面据此显示实际生效的方式与提示。
    /// </summary>
    public Autostart.AutostartReport SyncAutostart() => Autostart.SyncLoginStartup(
        _config.AutoStartWithWindows,
        _config.AutoStartAsAdministrator,
        configLoadedFromDisk: true);

    /// <summary>刷新指定游戏的路径状态；配置无效且 autoLocateIfMissing 时自动多源查找。</summary>
    public GameLocateResult RefreshGamePath(GameId game, bool autoLocateIfMissing)
    {
        var descriptor = GameCatalog.Get(game);
        var profile = _config.Profile(game);
        var session = _sessions[game];

        if (GameLocator.IsValidGameExe(descriptor, profile.GamePath))
        {
            profile.GamePath = PathUtil.Normalize(profile.GamePath);
            session.PathStatus = $"游戏路径: {profile.GamePath}（{GameLocator.SourceDisplayName(GameLocateSource.Config)}）";
            if (game == GameId.Genshin) QueueLegacyCleanup();
            return GameLocateResult.Success(profile.GamePath!, GameLocateSource.Config);
        }

        if (!autoLocateIfMissing)
        {
            session.PathStatus = "游戏路径: 未设置";
            return GameLocateResult.Fail("未设置");
        }

        var result = GameLocator.LocateAutomatic(descriptor, profile.GamePath);
        if (result.Ok && result.Path is not null)
        {
            profile.GamePath = PathUtil.Normalize(result.Path);
            _config.TrySave(out _);
            session.PathStatus = $"游戏路径: {profile.GamePath}（{GameLocator.SourceDisplayName(result.Source)}）";
        }
        else
        {
            session.PathStatus = $"游戏路径: 未找到 — {result.Detail}";
        }
        session.AutoLocateAttempted = true;

        if (result.Ok && game == GameId.Genshin) QueueLegacyCleanup();
        Raise(forceUi: true);
        return result;
    }

    /// <summary>弹出文件对话框手动选择指定游戏的主程序。</summary>
    public GameLocateResult SetGamePathManual(GameId game, IWin32Window? owner)
    {
        var descriptor = GameCatalog.Get(game);
        var profile = _config.Profile(game);
        var session = _sessions[game];
        var result = GameLocator.LocateManual(descriptor, owner);
        if (result.Ok && result.Path is not null)
        {
            profile.GamePath = PathUtil.Normalize(result.Path);
            _config.TrySave(out _);
            session.PathStatus = $"游戏路径: {profile.GamePath}（手动选择）";
            AppLog.Info($"manual game path ({descriptor.Key}): {profile.GamePath}");
            if (game == GameId.Genshin) QueueLegacyCleanup();
            Raise(forceUi: true);
        }
        return result;
    }

    /// <summary>直接写入指定游戏的主程序路径（前端 setGamePath 调用，路径已校验）。</summary>
    public GameLocateResult SetGamePath(GameId game, string path)
    {
        var descriptor = GameCatalog.Get(game);
        var normalized = PathUtil.Normalize(path);
        if (!GameLocator.IsValidGameExe(descriptor, normalized))
            return GameLocateResult.Fail($"请选择 {GameCatalog.ExeNameList(descriptor)}");

        _config.Profile(game).GamePath = normalized;
        _config.TrySave(out _);
        _sessions[game].PathStatus = $"游戏路径: {normalized}（手动选择）";
        if (game == GameId.Genshin) QueueLegacyCleanup();
        Raise(forceUi: true);
        return GameLocateResult.Success(normalized, GameLocateSource.Manual);
    }

    /// <summary>忽略当前配置路径，强制自动多源查找指定游戏。</summary>
    public GameLocateResult AutoLocateGamePath(GameId game)
    {
        var descriptor = GameCatalog.Get(game);
        var profile = _config.Profile(game);
        var session = _sessions[game];
        if (!GameLocator.IsValidGameExe(descriptor, profile.GamePath))
            profile.GamePath = null;

        var result = GameLocator.LocateAutomatic(descriptor, null);
        if (result.Ok && result.Path is not null)
        {
            AppLog.Info($"game path auto ({descriptor.Key}): {result.Path} source={result.Source}");
            profile.GamePath = PathUtil.Normalize(result.Path);
            _config.TrySave(out _);
            session.PathStatus = $"游戏路径: {profile.GamePath}（{GameLocator.SourceDisplayName(result.Source)}）";
        }
        else
        {
            AppLog.Warn($"game path auto failed ({descriptor.Key}): {result.Detail}");
            session.PathStatus = $"自动查找失败: {result.Detail}";
        }
        session.AutoLocateAttempted = true;

        if (result.Ok && game == GameId.Genshin) QueueLegacyCleanup();
        Raise(forceUi: true);
        return result;
    }

    /// <summary>
    /// 星穹铁道：核对并（必要时）写入注册表里的 120 FPS。
    /// 启用解锁时才调用；已经是 120 就不覆盖，关闭开关不会走到这里。
    /// </summary>
    public StarRailFpsResult SyncStarRailRegistry(bool force = false)
    {
        var session = _sessions[GameId.StarRail];
        var profile = _config.Profile(GameId.StarRail);

        if (!_config.MasterEnabled || !profile.Enabled)
        {
            session.RegistryCheckPending = false;
            session.RegistryStatus = _config.MasterEnabled
                ? "帧率解锁已关闭：未检查注册表"
                : "总开关已关闭：未检查注册表";
            return new StarRailFpsResult(StarRailFpsOutcome.ValueMissing, null, null, session.RegistryStatus);
        }

        if (!force && !session.RegistryCheckPending) return StarRailFpsRegistry.Read();

        session.RegistryCheckPending = false;
        var result = StarRailFpsRegistry.Ensure();
        session.RegistryStatus = result.Detail;
        if (result.Changed) AppLog.Info("star rail registry fps: " + result.Detail);
        else AppLog.Debug("star rail registry fps: " + result.Detail);
        Raise(forceUi: true);
        return result;
    }

    /// <summary>若指定游戏未运行则尝试启动已配置的主程序。</summary>
    public bool TryLaunchGame(GameId game, out string message)
    {
        var descriptor = GameCatalog.Get(game);
        if (GameProcess.Find(descriptor) is not null)
        {
            message = $"{descriptor.DisplayName}已在运行";
            return false;
        }

        RefreshGamePath(game, autoLocateIfMissing: true);
        var profile = _config.Profile(game);
        if (!GameLocator.IsValidGameExe(descriptor, profile.GamePath))
        {
            message = $"未配置{descriptor.DisplayName}的有效游戏路径，请先自动查找或手动选择";
            return false;
        }

        try
        {
            var path = PathUtil.Normalize(profile.GamePath!);
            // 星穹铁道的帧率来自注册表：启动前先核对一次，游戏读到 120 才会生效。
            if (descriptor.FpsViaRegistry) SyncStarRailRegistry(force: true);
            // 原神启动会立刻加载目录里的 dxgi.dll / DLSS 组件：先把上一版本的残留
            // 清掉再拉起游戏，清不掉（被占用）就记成待处理，交给监视循环继续重试。
            if (game == GameId.Genshin)
            {
                if (LegacyProxyCleanup.TryCleanup(path))
                    Interlocked.Exchange(ref _legacyCleanupPending, 0);
                else
                    QueueLegacyCleanup();
            }
            // 当前宿主已经是管理员时直接 CreateProcess，让子进程继承现有令牌。
            // 一律交给 ShellExecute 会再次经过 Shell 的兼容性/UAC 判断，导致
            // 用户已经授权后点击「启动游戏」仍重复弹窗。普通权限下保留
            // ShellExecute，让游戏自身的 requireAdministrator 清单按系统规则提示。
            var psi = new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = PathUtil.GetDirectoryNameSafe(path) ?? "",
                UseShellExecute = !Elevation.IsAdministrator(),
            };
            Process.Start(psi)?.Dispose();
            message = $"已启动{descriptor.DisplayName}: {path}";
            AppLog.Info(message);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "launch game");
            message = $"启动失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>通知 Stub 退出并停止监视循环。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ipc.RequestExit(); } catch { /* ignore */ }
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { /* ignore */ }
        _cts.Dispose();
        _ipc.Dispose();
        AppLog.Info("UnlockService disposed");
    }
}
