using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 核心后台服务：监视游戏进程、注入 Stub、经共享内存下发目标 FPS。
/// UI / 托盘通过属性与 StateChanged 事件读取状态；本类负责节流以降低开销。
/// 另外负责一次性回收上一版本「超分替换」留在游戏目录里的代理组件
/// （见 <see cref="LegacyProxyCleanup"/>）：只删不写，且不受解锁开关影响。
/// </summary>
internal sealed partial class UnlockService : IDisposable
{
    private readonly AppConfig _config;
    private readonly IpcSharedMemory _ipc;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _stubPath;
    private readonly object _raiseLock = new();

    private Task? _loop;
    /// <summary>当前认为已成功附着的游戏 PID（0 = 无）。</summary>
    private int _attachedPid;
    /// <summary>已尝试注入的 PID（含 Stub 未就绪），避免对同一进程重复注入。</summary>
    private int _injectAttemptedPid;
    /// <summary>连续注入失败次数，用于指数退避。</summary>
    private int _injectFailStreak;
    private string _statusText = "空闲 — 等待游戏启动";
    private string _gamePathStatus = "";
    private bool _disposed;

    // ---- IPC / UI 节流状态 ----
    private int _lastPushedFps = int.MinValue;
    private int _lastPushedEnabled = int.MinValue;
    private DateTime _lastIpcPushUtc = DateTime.MinValue;
    private DateTime _lastUiRaiseUtc = DateTime.MinValue;
    /// <summary>下一次允许尝试注入的 UTC 时间（失败退避）。</summary>
    private DateTime _nextInjectAttemptUtc = DateTime.MinValue;

    // ---- 历史残留组件清理（见 LegacyProxyCleanup） ----
    /// <summary>游戏目录里是否还可能有上一版本部署的代理组件（1 = 需要重试清理）。</summary>
    private int _legacyCleanupPending;
    /// <summary>下一次允许重试清理的 UTC 时间：文件被游戏占用时不必每轮都撞一遍。</summary>
    private DateTime _nextLegacyCleanupUtc = DateTime.MinValue;

    /// <summary>状态变化（UI 应 Invoke 到 UI 线程后刷新）。</summary>
    public event Action? StateChanged;

    // 这两个串由后台监视线程写、UI 线程读：引用赋值本身原子，但没有屏障时
    // UI 可能长时间读到旧值，所以显式走 Volatile（也把这层意图写在代码里）。
    public string StatusText => Volatile.Read(ref _statusText);
    public string GamePathStatus => Volatile.Read(ref _gamePathStatus);
    public int AttachedPid => Volatile.Read(ref _attachedPid);
    public IpcStatus StubStatus => _ipc.Read().Status;
    public int CurrentFpsFeedback => _ipc.Read().CurrentFps;

    /// <summary>Stub 上报的错误码（0xE001 起，0 表示无错误）。</summary>
    public int LastErrorFeedback => _ipc.Read().LastError;

    /// <summary>Stub 上报的反虚化就绪状态掩码（bit0 虚化 / bit1 马赛克 / bit2 马赛克已生效）。</summary>
    public int AntiBlurStateFeedback => _ipc.Read().AntiBlurState;

    /// <summary>Stub 上报的 UID 隐藏状态掩码（bit0 已就绪 / bit1 隐藏生效中）。</summary>
    public int HideUidStateFeedback => _ipc.Read().HideUidState;

    public AppConfig Config => _config;

    /// <summary>
    /// 是否有任一功能需要把 Stub 注入到游戏。
    /// 自动监视关闭时，同时停用已经附着的功能，避免 Stub 继续强制写入。
    /// </summary>
    private bool ShouldInject =>
        _config.MasterEnabled && _config.AutoWatch &&
        (_config.Enabled || _config.AntiBlurPerspective || _config.AntiBlurDiveMosaic || _config.HideUid);

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
        _stubPath = PathUtil.Normalize(AppPaths.StubDllPath);
        try { RefreshGamePath(autoLocateIfMissing: true); } catch (Exception ex) { AppLog.Warn(ex.Message); }
        try { PushConfigToIpc(force: true); } catch (Exception ex) { AppLog.Warn(ex.Message); }
    }

    /// <summary>启动后台监视循环（线程池 Task）。</summary>
    public void Start()
    {
        AppLog.Info("UnlockService.Start()");
        _loop = Task.Run(() => WatchLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 游戏路径确定或变化后，标记「需要检查上一版本残留在游戏目录里的代理组件」。
    /// 实际删除在监视循环的固定节拍里做：文件被运行中的游戏占用时下一轮继续重试。
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

        if (LegacyProxyCleanup.TryCleanup(_config.GamePath))
            Interlocked.Exchange(ref _legacyCleanupPending, 0);
    }

    /// <summary>
    /// 将目标 FPS 与有效开关推入共享内存。
    /// 默认 400ms 内相同值不重复写，降低 IPC 与日志噪声；force=true 立即推送。
    /// </summary>
    public void PushConfigToIpc(bool force = false)
    {
        _config.Sanitize();
        var fps = _config.TargetFps;
        var en = ShouldInject && _config.Enabled ? 1 : 0;
        var now = DateTime.UtcNow;

        // 跳过冗余写入
        var changed = fps != _lastPushedFps || en != _lastPushedEnabled;
        if (!force && !changed && (now - _lastIpcPushUtc).TotalMilliseconds < 400)
        {
            return;
        }

        var featuresActive = _config.MasterEnabled && _config.AutoWatch;
        var stubShouldRun = ShouldInject;
        if (!stubShouldRun)
        {
            var status = _ipc.Read().Status;
            if (status is IpcStatus.Waiting or IpcStatus.Ready)
                _ipc.RequestExit();
        }
        _ipc.UpdateHostFields(fps, en != 0,
            featuresActive && _config.AntiBlurPerspective,
            featuresActive && _config.AntiBlurDiveMosaic,
            featuresActive && _config.HideUid);
        _lastPushedFps = fps;
        _lastPushedEnabled = en;
        _lastIpcPushUtc = now;

        // 只在「真的变了」或强制推送时记一行：保活式的重复写入每秒能有两三次，
        // 全记下来会把日志文件和界面日志页（现在会实时增量显示宿主日志）刷满。
        if (force || changed)
            AppLog.Debug($"IPC push fps={fps} effective={en != 0}");
        Raise(forceUi: force);
    }

    /// <summary>设置目标帧率并持久化、立即推送 IPC。</summary>
    public void ApplyFps(int fps)
    {
        _config.TargetFps = fps;
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>设置功能开关（帧率解锁）并推送。</summary>
    public void SetEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>设置总开关：关闭时不注入、不强制帧率。</summary>
    public void SetMasterEnabled(bool enabled)
    {
        AppLog.Info($"SetMasterEnabled={enabled}");
        _config.MasterEnabled = enabled;
        _config.TrySave(out _);
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
            SetAttached(0);
        }
        Raise(forceUi: true);
    }

    /// <summary>反角色虚化注入开关（迁移自 Snap.Hutao.Remastered）：保存并推送 IPC。</summary>
    public void SetAntiBlurPerspective(bool enabled)
    {
        _config.AntiBlurPerspective = enabled;
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>移除水下马赛克注入开关（迁移自 Snap.Hutao.Remastered）：保存并推送 IPC。</summary>
    public void SetAntiBlurDiveMosaic(bool enabled)
    {
        _config.AntiBlurDiveMosaic = enabled;
        _config.TrySave(out _);
        PushConfigToIpc(force: true);
    }

    /// <summary>隐藏 UID 注入开关（同源迁移自 Snap.Hutao.Remastered）：保存并推送 IPC。</summary>
    public void SetHideUid(bool enabled)
    {
        _config.HideUid = enabled;
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

    /// <summary>刷新游戏路径状态；配置无效且 autoLocateIfMissing 时自动多源查找。</summary>
    public GameLocateResult RefreshGamePath(bool autoLocateIfMissing)
    {
        if (GameLocator.IsValidGameExe(_config.GamePath))
        {
            _config.GamePath = PathUtil.Normalize(_config.GamePath);
            Volatile.Write(ref _gamePathStatus, $"游戏路径: {_config.GamePath}（{GameLocator.SourceDisplayName(GameLocateSource.Config)}）");
            QueueLegacyCleanup();
            return GameLocateResult.Success(_config.GamePath!, GameLocateSource.Config);
        }

        if (!autoLocateIfMissing)
        {
            Volatile.Write(ref _gamePathStatus, "游戏路径: 未设置");
            return GameLocateResult.Fail("未设置");
        }

        var result = GameLocator.LocateAutomatic(_config.GamePath);
        if (result.Ok && result.Path is not null)
        {
            _config.GamePath = PathUtil.Normalize(result.Path);
            _config.TrySave(out _);
            Volatile.Write(ref _gamePathStatus, $"游戏路径: {_config.GamePath}（{GameLocator.SourceDisplayName(result.Source)}）");
        }
        else
        {
            Volatile.Write(ref _gamePathStatus, $"游戏路径: 未找到 — {result.Detail}");
        }

        if (result.Ok) QueueLegacyCleanup();
        return result;
    }

    /// <summary>弹出文件对话框手动选择游戏主程序。</summary>
    public GameLocateResult SetGamePathManual(IWin32Window? owner)
    {
        var result = GameLocator.LocateManual(owner);
        if (result.Ok && result.Path is not null)
        {
            _config.GamePath = PathUtil.Normalize(result.Path);
            _config.TrySave(out _);
            Volatile.Write(ref _gamePathStatus, $"游戏路径: {_config.GamePath}（手动选择）");
            AppLog.Info("manual game path: " + _config.GamePath);
            QueueLegacyCleanup();
        }
        return result;
    }

    /// <summary>忽略当前配置路径，强制自动多源查找。</summary>
    public GameLocateResult AutoLocateGamePath()
    {
        if (!GameLocator.IsValidGameExe(_config.GamePath))
            _config.GamePath = null;

        var result = GameLocator.LocateAutomatic(null);
        if (result.Ok && result.Path is not null)
        {
            AppLog.Info($"game path auto: {result.Path} source={result.Source}");
            _config.GamePath = PathUtil.Normalize(result.Path);
            _config.TrySave(out _);
            Volatile.Write(ref _gamePathStatus, $"游戏路径: {_config.GamePath}（{GameLocator.SourceDisplayName(result.Source)}）");
        }
        else
        {
            AppLog.Warn($"game path auto failed: {result.Detail}");
            Volatile.Write(ref _gamePathStatus, $"自动查找失败: {result.Detail}");
        }

        if (result.Ok) QueueLegacyCleanup();
        return result;
    }

    /// <summary>若游戏未运行则尝试启动已配置的主程序。</summary>
    public bool TryLaunchGame(out string message)
    {
        if (GameProcess.Find() is not null)
        {
            message = "游戏已在运行";
            return false;
        }

        RefreshGamePath(autoLocateIfMissing: true);
        if (!GameLocator.IsValidGameExe(_config.GamePath))
        {
            message = "未配置有效的游戏路径，请先自动查找或手动选择";
            return false;
        }

        try
        {
            var path = PathUtil.Normalize(_config.GamePath!);
            // 当前宿主已经是管理员时直接 CreateProcess，让子进程继承现有令牌。
            // 一律交给 ShellExecute 会再次经过 Shell 的兼容性/UAC 判断，导致
            // 用户已经授权后点击「启动游戏」仍重复弹窗。普通权限下保留
            // ShellExecute，让游戏自身的 requireAdministrator 清单按系统规则提示。
            // 游戏启动会立刻加载目录里的 dxgi.dll / DLSS 组件：先把上一版本的残留
            // 清掉再拉起游戏，清不掉（被占用）就记成待处理，交给监视循环继续重试。
            if (LegacyProxyCleanup.TryCleanup(path))
                Interlocked.Exchange(ref _legacyCleanupPending, 0);
            else
                QueueLegacyCleanup();
            var psi = new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = PathUtil.GetDirectoryNameSafe(path) ?? "",
                UseShellExecute = !Elevation.IsAdministrator(),
            };
            Process.Start(psi)?.Dispose();
            message = $"已启动: {path}";
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
