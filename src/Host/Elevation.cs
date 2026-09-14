using System.Diagnostics;
using System.Security.Principal;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 权限辅助：日常运行 asInvoker（无 UAC）；仅「用户主动以管理员身份重启」时 runas。
/// 开机自启路径绝不可触发 UAC 弹窗。
/// 安装 / 卸载由 Kachina 安装器自行处理提权，宿主不再有任何安装用途的提权路径。
/// </summary>
internal static class Elevation
{
    /// <summary>当前进程是否已具备管理员令牌。</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 若尚未提权，则用 runas 重新启动自身并带上指定参数，当前进程应随后退出。
    /// 成功拉起返回 true；用户取消 UAC 或失败返回 false。
    /// </summary>
    public static bool TryRelaunchElevated(string arguments, out string error)
    {
        error = string.Empty;
        if (IsAdministrator())
            return true;

        try
        {
            var exe = AppPaths.ExePath;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas", // 触发一次 UAC（用户主动）
                WorkingDirectory = AppPaths.ExeDirectory,
            };
            Process.Start(psi)?.Dispose();   // 只要句柄别留给 GC，进程本身照跑
            return true;
        }
        catch (Exception ex)
        {
            // 用户点“否”会抛 Win32Exception
            error = ex.Message;
            AppLog.Warn("提权启动失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 用户主动：释放单实例后以管理员重新启动主程序（显示主窗）。
    /// 成功拉起后应退出当前进程。不用于开机自启。
    /// </summary>
    public static bool TryRestartElevatedForUnlock(out string error)
    {
        error = string.Empty;
        if (IsAdministrator())
        {
            error = "当前已是管理员权限。";
            return false;
        }

        // 先释放互斥，避免提权实例被「已在运行」挡住
        try { Program.ReleaseSingleInstance(); }
        catch (Exception ex) { AppLog.Warn("ReleaseSingleInstance: " + ex.Message); }

        // --elevated-restart：提权后显示主窗但不改动「启动后最小化」配置；
        // --elevated-handoff 让新实例在旧实例
        // 释放互斥的交接窗口内短暂重试，避免启动竞态导致管理员实例立即退出。
        if (!TryRelaunchElevated("--elevated-restart --elevated-handoff", out error))
        {
            // 提权没成（多半是用户在 UAC 点了「否」）：把刚释放的锁拿回来，
            // 否则本进程还在跑却不再持有单实例锁 → 再点快捷方式就双开。
            if (!Program.ReacquireSingleInstance())
            {
                error = "提权已取消，且无法恢复单实例状态（可能已有另一个实例在运行）。" + error;
            }
            return false;
        }

        AppLog.Info("已请求以管理员身份重新启动");
        return true;
    }
}
