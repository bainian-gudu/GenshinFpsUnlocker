using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 卸载入口：宿主只负责「拉起 Kachina 安装器生成的卸载程序」，
/// 自身不做任何文件删除、注册表写入或 ARP 卸载项维护 —— 卸载路径保持唯一。
///
/// 权限说明：uninst.exe 会按安装配置的 uacStrategy 自行申请管理员
/// （kachina 的 run_elevated），因此这里用普通权限启动即可，
/// 与 Windows「设置 → 应用 → 安装的应用」中的卸载行为一致，不会多弹一次 UAC。
/// </summary>
internal static class UninstallLauncher
{
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
                    "配置与日志位于 %LocalAppData%\\GenshinFpsUnlocker。";
            AppLog.Warn("卸载中止: " + error);
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
            Process.Start(psi);
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
}
