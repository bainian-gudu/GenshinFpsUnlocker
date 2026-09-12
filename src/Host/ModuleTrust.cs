using System.IO;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 「本程序目录里将被执行 / 被注入的文件」可信度校验。
///
/// 为什么需要：这些路径都是我们自己拼出来的（<c>ExeDirectory\xxx</c>），看起来不需要校验，
/// 但本程序<b>可能运行在已提权状态</b>（用户点过「以管理员重新启动」）。此时若安装目录
/// 位于普通用户可写的位置（便携版放在 D:\Tools\ 之类），攻击者放一个同名文件进去，
/// 就能借我们的管理员令牌执行任意代码：
///   - 卸载器路径 → 直接以 High IL 启动恶意 exe；
///   - 注入模块路径 → 恶意 DLL 被注入游戏进程，或在备用 Hook 注入路径下
///     被映射进提权的 Host 进程。
/// 所以两条路径都要过这里的同一套检查。
/// </summary>
internal static class ModuleTrust
{
    /// <summary>
    /// 校验文件是否可信。
    /// </summary>
    /// <param name="path">待校验的文件路径。</param>
    /// <param name="expectedFileName">约定的文件名（防路径被改指向）。</param>
    /// <param name="noun">错误文案里的称呼，例如「卸载程序」「注入模块」。</param>
    /// <param name="error">失败原因（中文，可直接给用户看）。</param>
    /// <param name="elevatedHint">已提权且目录不受保护时，附加在错误后面的处置建议。</param>
    public static bool IsTrustworthy(
        string path,
        string expectedFileName,
        string noun,
        out string error,
        string? elevatedHint = null)
    {
        error = string.Empty;
        try
        {
            var full = PathUtil.Normalize(Path.GetFullPath(path));
            var dir = PathUtil.Normalize(AppPaths.ExeDirectory);

            // 1) 必须就在本程序目录里，且文件名与约定一致（防路径穿越 / 被改指向）
            if (!PathUtil.IsUnder(full, dir))
            {
                error = $"{noun}不在本程序目录内: {full}";
                return false;
            }
            if (!string.Equals(Path.GetFileName(full), expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                error = $"{noun}文件名不符合预期: {Path.GetFileName(full)}";
                return false;
            }

            // 2) 不能是空文件；不能是符号链接 / junction（含所在目录），
            //    否则「校验的路径」和「真正执行的路径」可能不是同一个
            var file = new FileInfo(full);
            if (!file.Exists || file.Length == 0)
            {
                error = $"{noun}不存在或为空文件: {full}";
                return false;
            }
            if (IsReparsePoint(file.Attributes))
            {
                error = $"{noun}是符号链接，已拒绝使用: {full}";
                return false;
            }
            var parent = PathUtil.GetDirectoryNameSafe(full);
            if (parent is not null && Directory.Exists(parent) &&
                IsReparsePoint(new DirectoryInfo(parent).Attributes))
            {
                error = $"{noun}所在目录是符号链接，已拒绝使用: {parent}";
                return false;
            }

            // 3) 已提权时，安装目录必须位于受保护的 Program Files 下。
            //    其它位置（用户可写目录）存在被替换成恶意同名文件的风险。
            if (Elevation.IsAdministrator() && !AppPaths.IsInstalledUnderProgramFiles())
            {
                error = $"当前以管理员身份运行，但程序目录不在 Program Files 下（{dir}）。" +
                        $"为避免使用被替换的{noun}，已拒绝从这里加载。" +
                        (elevatedHint ?? "请退出管理员实例后按普通权限重试。");
                return false;
            }

            // 4) 兜底：目录本身不能是盘符根 / 系统目录 / 用户配置目录
            if (PathUtil.IsDangerousRootOrProfile(dir))
            {
                error = $"程序目录位置异常，已拒绝使用{noun}: {dir}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"校验{noun}失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>是否是符号链接 / 挂载点之类的重解析点。</summary>
    private static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;
}
