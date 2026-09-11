namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 二次启动时唤醒已有实例（避免再弹「已在运行」后用户误以为要重开）。
/// 使用命名 EventWaitHandle，主实例后台线程 WaitOne。
/// </summary>
internal static class InstanceWake
{
    public const string EventName = @"Local\GenshinFpsUnlocker.Wake.v1";

    /// <summary>主实例：启动监听；收到信号后 invoke onWake（通常在 UI 线程）。</summary>
    public static Thread? StartListener(Action onWake, CancellationToken cancel = default)
    {
        EventWaitHandle? ev = null;
        try
        {
            ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch (Exception ex)
        {
            AppLog.Warn("InstanceWake create: " + ex.Message);
            return null;
        }

        var thread = new Thread(() =>
        {
            using var handle = ev;
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (!handle.WaitOne(500))
                        continue;
                    try { onWake(); }
                    catch (Exception ex) { AppLog.Warn("InstanceWake onWake: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("InstanceWake listener exit: " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "GenshinFpsUnlocker.Wake",
        };
        thread.Start();
        return thread;
    }

    /// <summary>次实例：通知主实例。成功返回 true。</summary>
    public static bool TrySignal()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventName);
            return ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Debug("InstanceWake.TrySignal: " + ex.Message);
            return false;
        }
    }
}
