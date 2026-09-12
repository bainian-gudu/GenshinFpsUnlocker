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

    // EXECUTION_STATE 组合：持续运行 + 远离显示休眠策略干扰
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

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
        TrySetExecutionState();
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
            var ok = SetProcessInformation(
                Process.GetCurrentProcess().Handle,
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

    /// <summary>提示系统本进程需要持续运行（不阻止用户手动睡眠）。</summary>
    private static void TrySetExecutionState()
    {
        try
        {
            // CONTINUOUS | SYSTEM_REQUIRED | AWAYMODE — 退出时须 Clear，否则可能影响休眠
            var flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED;
            SetThreadExecutionState(flags);
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
