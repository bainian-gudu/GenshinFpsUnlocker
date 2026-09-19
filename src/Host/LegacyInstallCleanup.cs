using System.IO;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 清理旧品牌安装目录里的残留文件。升级安装器已经能通过 metadata 删除大部分内容，
/// 这里再做一次启动期兜底：旧 exe / 卸载器 / 更新器以及转换前的位图都不应继续留在安装目录。
/// </summary>
internal static class LegacyInstallCleanup
{
    private static readonly string[] LegacyFiles =
    {
        AppPaths.LegacyExecutableFileName,
        AppPaths.LegacyUninstallerFileName,
        AppPaths.LegacyUpdaterFileName,
        "app.png",
        "ui/favicon.png",
        "ui/images/brand-fox.png",
        "ui/images/game-icon.png",
        "ui/images/installer-hero.jpg",
        "ui/images/sidebar-yae.jpg",
        "ui/images/teyvat-landscape.jpg",
        "ui/images/yae-card.jpg",
        "ui/images/yae-portrait-wide.jpg",
    };

    public static void TryCleanup()
    {
        foreach (var relative in LegacyFiles)
        {
            var path = Path.Combine(AppPaths.ExeDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                AppLog.Info("已清理旧版安装文件: " + path);
            }
            catch (Exception ex)
            {
                // 旧更新器/卸载器可能仍在运行，删不掉只记日志，交给下一次启动或卸载器处理。
                AppLog.Debug("清理旧版安装文件失败 " + path + ": " + ex.Message);
            }
        }
    }
}
