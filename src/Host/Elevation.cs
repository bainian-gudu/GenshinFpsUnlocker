using System.Diagnostics;
using System.Security.Principal;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 权限辅助：日常运行 asInvoker（无 UAC）；安装/卸载与「用户主动」提权解锁时 runas。
/// 开机自启路径绝不可触发 UAC 弹窗。
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
            Process.Start(psi);
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
    /// 确保以管理员执行指定动作；若需提权则重启并返回 false（调用方应退出）。
    /// alreadyElevatedOrContinue 为 true 表示当前已是管理员，可继续执行。
    /// </summary>
    public static bool EnsureAdminOrRelaunch(string arguments, bool quiet, out bool relaunched)
    {
        relaunched = false;
        if (IsAdministrator())
            return true;

        if (!TryRelaunchElevated(arguments, out var err))
        {
            if (!quiet)
            {
                MessageBox.Show(
                    "此操作需要管理员权限（写入 Program Files / 注册表等）。\n" +
                    "您取消了授权，或系统拒绝了提权请求。\n\n" + err,
                    AppPaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return false;
        }

        relaunched = true;
        return false; // 当前进程应退出，由提权实例继续
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

        // --show：提权后显示主窗；不带 --autostart，避免与自启语义混淆
        if (!TryRelaunchElevated("--show", out error))
            return false;

        AppLog.Info("已请求以管理员身份重新启动");
        return true;
    }
}
