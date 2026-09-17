namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 单实例互斥。
/// 标准用户通常无 SeCreateGlobalPrivilege，创建 Global\ 会 UnauthorizedAccessException；
/// 旧代码未捕获 → 进程静默退出（无主窗、无托盘）。必须 Local 回退。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string GlobalName = @"Global\GenshinFpsUnlocker.Instance.v1";
    public const string LocalName = @"Local\GenshinFpsUnlocker.Instance.v1";

    private Mutex? _mutex;
    private bool _owned;

    public string? Name { get; private set; }

    /// <summary>
    /// 尝试成为主实例。
    /// 返回 false = 已有实例在跑（调用方应提示后退出）。
    /// 权限失败会自动 Local 回退；两边都失败时仍返回 true 允许启动（避免完全打不开）。
    /// </summary>
    public bool TryAcquire()
    {
        // 1) Global（跨会话；管理员更稳）
        var g = TryOpenOrCreate(GlobalName);
        if (g.Status == AcquireStatus.Primary)
        {
            _mutex = g.Mutex;
            _owned = true;
            Name = GlobalName;
            AppLog.Info("single-instance primary: " + GlobalName);
            return true;
        }
        if (g.Status == AcquireStatus.AlreadyRunning)
        {
            AppLog.Info("single-instance already running (Global)");
            return false;
        }
        AppLog.Warn("single-instance Global 不可用: " + g.Error);

        // 2) Local（非管理员日常）
        var l = TryOpenOrCreate(LocalName);
        if (l.Status == AcquireStatus.Primary)
        {
            _mutex = l.Mutex;
            _owned = true;
            Name = LocalName;
            AppLog.Info("single-instance primary: " + LocalName);
            return true;
        }
        if (l.Status == AcquireStatus.AlreadyRunning)
        {
            AppLog.Info("single-instance already running (Local)");
            return false;
        }

        AppLog.Error("single-instance Local 也失败，放行启动: " + l.Error);
        _owned = true;
        Name = null;
        return true;
    }

    private enum AcquireStatus { Primary, AlreadyRunning, Failed }

    private readonly record struct AcquireResult(AcquireStatus Status, Mutex? Mutex, string Error);

    private static AcquireResult TryOpenOrCreate(string name)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, name: name, createdNew: out var createdNew);
            if (createdNew)
                return new AcquireResult(AcquireStatus.Primary, mutex, "");

            // 已存在：我们不应持有
            try { mutex.Dispose(); } catch { /* ignore */ }
            return new AcquireResult(AcquireStatus.AlreadyRunning, null, "");
        }
        catch (UnauthorizedAccessException ex)
        {
            // 无权限创建，或更高完整性实例已占用且 ACL 拒绝打开
            // 对 Global 名称：多数是「无 SeCreateGlobalPrivilege」，应回退 Local，而非判已运行
            return new AcquireResult(AcquireStatus.Failed, null, "UnauthorizedAccess: " + ex.Message);
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            return new AcquireResult(AcquireStatus.Failed, null, "CannotOpen: " + ex.Message);
        }
        catch (IOException ex)
        {
            return new AcquireResult(AcquireStatus.Failed, null, "IO: " + ex.Message);
        }
        catch (Exception ex)
        {
            return new AcquireResult(AcquireStatus.Failed, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try
        {
            if (_owned)
            {
                try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
            }
        }
        finally
        {
            try { _mutex.Dispose(); } catch { /* ignore */ }
            _mutex = null;
            _owned = false;
        }
    }
}
