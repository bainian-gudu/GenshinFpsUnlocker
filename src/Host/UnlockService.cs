using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 核心后台服务：监视游戏进程、注入 Stub、经共享内存下发目标 FPS。
/// UI / 托盘通过属性与 StateChanged 事件读取状态；本类负责节流以降低开销。
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

    /// <summary>状态变化（UI 应 Invoke 到 UI 线程后刷新）。</summary>
    public event Action? StateChanged;

    public string StatusText => _statusText;
    public string GamePathStatus => _gamePathStatus;
    public int AttachedPid => Volatile.Read(ref _attachedPid);
    public IpcStatus StubStatus => _ipc.Read().Status;
    public int CurrentFpsFeedback => _ipc.Read().CurrentFps;
    public AppConfig Config => _config;

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
    /// 将目标 FPS 与有效开关推入共享内存。
    /// 默认 400ms 内相同值不重复写，降低 IPC 与日志噪声；force=true 立即推送。
    /// </summary>
    public void PushConfigToIpc(bool force = false)
    {
        _config.Sanitize();
        var fps = _config.TargetFps;
        var en = _config.EffectiveUnlockEnabled ? 1 : 0;
        var now = DateTime.UtcNow;

        // 跳过冗余写入
        if (!force
            && fps == _lastPushedFps
            && en == _lastPushedEnabled
            && (now - _lastIpcPushUtc).TotalMilliseconds < 400)
        {
            return;
        }

        _ipc.UpdateHostFields(fps, en != 0);
        _lastPushedFps = fps;
        _lastPushedEnabled = en;
        _lastIpcPushUtc = now;

        if (force || AppLog.Enabled)
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
        Raise(forceUi: true);
    }

    /// <summary>开机自启动开关（同步注册表）。</summary>
    public void SetAutoStartWithWindows(bool enabled)
    {
        _config.AutoStartWithWindows = enabled;
        _config.TrySave(out _);
        Autostart.SetEnabled(enabled);
        Raise(forceUi: true);
    }

    /// <summary>刷新游戏路径状态；配置无效且 autoLocateIfMissing 时自动多源查找。</summary>
    public GameLocateResult RefreshGamePath(bool autoLocateIfMissing)
    {
        if (GameLocator.IsValidGameExe(_config.GamePath))
        {
            _config.GamePath = PathUtil.Normalize(_config.GamePath);
            _gamePathStatus = $"游戏路径: {_config.GamePath}（{GameLocator.SourceDisplayName(GameLocateSource.Config)}）";
            Raise(forceUi: true);
            return GameLocateResult.Success(_config.GamePath!, GameLocateSource.Config);
        }

        if (!autoLocateIfMissing)
        {
            _gamePathStatus = "游戏路径: 未设置";
            Raise(forceUi: true);
            return GameLocateResult.Fail("未设置");
        }

        var result = GameLocator.LocateAutomatic(_config.GamePath);
        if (result.Ok && result.Path is not null)
        {
            _config.GamePath = PathUtil.Normalize(result.Path);
            _config.TrySave(out _);
            _gamePathStatus = $"游戏路径: {_config.GamePath}（{GameLocator.SourceDisplayName(result.Source)}）";
        }
        else
        {
            _gamePathStatus = $"游戏路径: 未找到 — {result.Detail}";
        }

        Raise(forceUi: true);
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
            _gamePathStatus = $"游戏路径: {_config.GamePath}（手动选择）";
            AppLog.Info("manual game path: " + _config.GamePath);
            Raise(forceUi: true);
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
            _gamePathStatus = $"游戏路径: {_config.GamePath}（{GameLocator.SourceDisplayName(result.Source)}）";
        }
        else
        {
            AppLog.Warn($"game path auto failed: {result.Detail}");
            _gamePathStatus = $"自动查找失败: {result.Detail}";
        }

        Raise(forceUi: true);
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
            var psi = new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = PathUtil.GetDirectoryNameSafe(path) ?? "",
                UseShellExecute = true,
            };
            Process.Start(psi);
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
