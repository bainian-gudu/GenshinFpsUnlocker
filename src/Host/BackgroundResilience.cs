using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 降低后台进程被“莫名杀掉/挂起”概率的软措施：
/// - 进程优先级 AboveNormal（不过度抬到 High/Realtime，避免显眼）
/// - 关闭节电执行节流（Win10 1709+ / Win11 效率模式相关 power throttling，失败则忽略）
/// - 请求系统执行状态，减少被休眠掐断
/// 不使用“关键进程”标志（崩溃会导致蓝屏，不安全）。
/// </summary>
internal static class BackgroundResilience
{
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const int ProcessPowerThrottling = 4;

    // EXECUTION_STATE 组合：仅在解锁生效期间请求系统保持唤醒
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    /// <summary>当前的「游戏附着」执行状态（0/1），用于跳过重复请求。</summary>
    private static int _gameActive;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(
        IntPtr hProcess, int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>在 UI 启动后调用一次，应用优先级与节流豁免。</summary>
    public static void Apply()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            // AboveNormal：负载下保持监视线程响应，又不过分“抢戏”
            try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; }
            catch (Exception ex) { AppLog.Debug("设置优先级失败: " + ex.Message); }

            // 退出时稍晚被结束，便于写日志/通知 Stub
            try { proc.PriorityBoostEnabled = true; } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            AppLog.Debug("BackgroundResilience 优先级: " + ex.Message);
        }

        TryDisablePowerThrottling();
        // 注意：这里不再请求执行状态。程序常驻托盘，启动即设 ES_SYSTEM_REQUIRED
        // 等于永久禁用自动睡眠；改成由 SetGameActive 在游戏附着期间才请求。
        AppLog.Info("BackgroundResilience.Apply 完成");
    }

    /// <summary>关闭 Execution Speed 节流，避免效率模式把后台轮询拖慢。</summary>
    private static void TryDisablePowerThrottling()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0, // 0 = 不启用节流
            };
            using var self = Process.GetCurrentProcess();   // 别把 Process 句柄丢给 GC
            var ok = SetProcessInformation(
                self.Handle,
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            AppLog.Debug(ok ? "已关闭 power throttling" : "SetProcessInformation 返回 false（系统可能不支持）");
        }
        catch (Exception ex)
        {
            AppLog.Debug("power throttling: " + ex.Message);
        }
    }

    /// <summary>
    /// 游戏是否正在被解锁：只有这段时间才请求「系统不要自动睡眠」。
    /// 不阻止用户手动睡眠；也不再用 ES_AWAYMODE_REQUIRED（那是给媒体应用 away mode
    /// 用的，普通后台程序带着它没有收益）。
    /// </summary>
    public static void SetGameActive(bool active)
    {
        var next = active ? 1 : 0;
        if (Interlocked.Exchange(ref _gameActive, next) == next)
        {
            return;   // 状态没变，省一次系统调用
        }

        try
        {
            SetThreadExecutionState(active ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
            AppLog.Debug(active
                ? "ExecutionState: 游戏运行中 — 请求系统保持唤醒"
                : "ExecutionState: 游戏已退出 — 交还系统睡眠策略");
        }
        catch (Exception ex)
        {
            AppLog.Debug("ExecutionState: " + ex.Message);
        }
    }

    /// <summary>进程退出前清除执行状态请求。</summary>
    public static void Clear()
    {
        try { SetThreadExecutionState(ES_CONTINUOUS); }
        catch { /* ignore */ }
    }

}
