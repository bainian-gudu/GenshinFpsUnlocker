using System.Diagnostics;
using System.IO;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 卸载入口：宿主只负责「拉起 Kachina 安装器生成的卸载程序」，
/// 自身不做任何文件删除、注册表写入或 ARP 卸载项维护 —— 卸载路径保持唯一。
///
/// 权限说明：uninst.exe 会按安装配置的 uacStrategy 自行申请管理员
/// （kachina 的 run_elevated），因此这里用普通权限启动即可，
/// 与 Windows「设置 → 应用 → 安装的应用」中的卸载行为一致，不会多弹一次 UAC。
///
/// 安全说明（防提权）：本方法可能在一个**已提权**的宿主进程里被调用
/// （用户点过「以管理员重新启动」）。此时若安装目录可被普通用户写入，
/// 攻击者放一个同名 uninst.exe 就能借我们的管理员令牌执行任意代码，
/// 因此启动前要做 <see cref="IsTrustworthyUninstaller"/> 里的那几项校验。
/// Web UI 侧不能指定路径（`uninstall` 消息不接受任何参数），路径全部由本类算出。
/// </summary>
internal static class UninstallLauncher
{
    private const string UninstFileName = AppPaths.ProductName + ".uninst.exe";

    /// <summary>Kachina 卸载程序路径；不存在（便携版）时返回 null。</summary>
    public static string? FindUninstaller()
    {
        try
        {
            var path = AppPaths.UninstExePath;
            return PathUtil.ExistsFile(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 启动卸载程序。成功返回 true，调用方随后应退出本进程，
    /// 避免卸载器把主程序当作「正在运行、需要先关闭」的进程处理。
    /// </summary>
    public static bool TryLaunch(out string error)
    {
        error = string.Empty;

        var uninst = FindUninstaller();
        if (uninst is null)
        {
            error = "未找到卸载程序：" + AppPaths.UninstExePath +
                    "。便携版没有注册卸载项，直接删除所在目录即可；" +
                    "配置与日志位于用户数据目录。";
            AppLog.Warn("卸载中止: " + error);
            return false;
        }

        if (!IsTrustworthyUninstaller(uninst, out error))
        {
            AppLog.Error("拒绝启动卸载程序: " + error);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = uninst,
                UseShellExecute = true, // 交给 shell 启动，UAC 由卸载器自己按需申请
                WorkingDirectory = Path.GetDirectoryName(uninst) ?? AppPaths.ExeDirectory,
            };
            Process.Start(psi)?.Dispose();
            AppLog.Info("已启动 Kachina 卸载程序: " + uninst);
            return true;
        }
        catch (Exception ex)
        {
            error = "启动卸载程序失败: " + ex.Message;
            AppLog.Error(error);
            return false;
        }
    }

    /// <summary>
    /// 启动前校验：统一走 <see cref="ModuleTrust"/>（与注入模块同一套检查）。
    /// 任何一条不满足就拒绝启动 —— 宁可让用户去「设置 → 应用」卸载，
    /// 也不要把管理员令牌交给一个来路不明的 exe。
    /// </summary>
    private static bool IsTrustworthyUninstaller(string path, out string error) =>
        ModuleTrust.IsTrustworthy(
            path,
            UninstFileName,
            "卸载程序",
            out error,
            elevatedHint: "请退出管理员实例后按普通权限重试，或在 Windows「设置 → 应用 → 安装的应用」中卸载。");
}
