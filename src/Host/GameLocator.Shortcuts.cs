using System.Runtime.InteropServices;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 桌面 / 开始菜单快捷方式反推（partial）：
/// 玩家常把游戏快捷方式放在桌面，快捷方式的目标就是主程序或游戏目录，
/// 这类线索不用扫盘、不受安装位置限制，所以排在全盘快扫之前。
/// </summary>
internal static partial class GameLocator
{
    /// <summary>快捷方式文件名里出现这些词就优先解析（其它 .lnk 也会解析，只是排在后面）。</summary>
    private static readonly string[] ShortcutNameHints =
        ["原神", "Yuanshen", "Genshin", "HoYo", "米哈游", "miHoYo"];

    /// <summary>单个位置最多解析多少个快捷方式（解析失败时不重试）。</summary>
    private const int MaxShortcutsPerLocation = 40;

    /// <summary>单个位置最多读取多少个 .lnk 文件名（只读文件名，不做 COM 解析）。</summary>
    private const int MaxShortcutsListedPerLocation = 400;

    /// <summary>
    /// 解析桌面 / 公共桌面 / 开始菜单里的快捷方式，反推游戏主程序路径。
    /// 目标是目录时在目录内枚举主程序；目标是启动器时在同目录内枚举。
    /// </summary>
    public static GameLocateResult LocateFromShortcuts()
    {
        var shortcutFiles = CollectShortcutFiles().ToList();
        if (shortcutFiles.Count == 0) return GameLocateResult.Fail("没有找到可解析的快捷方式");

        foreach (var (lnk, location) in shortcutFiles)
        {
            var target = ResolveShortcutTarget(lnk);
            if (string.IsNullOrWhiteSpace(target)) continue;
            var normalized = PathUtil.Normalize(target);
            if (string.IsNullOrEmpty(normalized)) continue;

            // a) 目标本身就是游戏主程序
            if (IsValidGameExe(normalized))
            {
                return GameLocateResult.Success(
                    normalized, GameLocateSource.Shortcut, $"{location}：{Path.GetFileName(lnk)}");
            }

            // b) 目标是目录（或目录已被删）：在目录内浅层枚举主程序
            foreach (var exe in ResolveFromShortcutTarget(normalized))
            {
                return GameLocateResult.Success(
                    exe, GameLocateSource.Shortcut, $"{location}：{Path.GetFileName(lnk)}");
            }
        }

        return GameLocateResult.Fail("桌面 / 开始菜单的快捷方式里没有指向原神");
    }

    /// <summary>
    /// 从一个已解析的快捷方式目标推出游戏主程序：
    /// 目标是主程序直接用；是目录或启动器时在目标（或其父目录）内浅层枚举。
    /// </summary>
    public static IEnumerable<string> ResolveFromShortcutTarget(string target)
    {
        var normalized = PathUtil.Normalize(target);
        if (string.IsNullOrEmpty(normalized)) yield break;

        if (IsValidGameExe(normalized))
        {
            yield return normalized;
            yield break;
        }

        var searchRoot = PathUtil.ExistsDir(normalized)
            ? normalized
            : PathUtil.GetDirectoryNameSafe(normalized);
        if (string.IsNullOrEmpty(searchRoot)) yield break;

        foreach (var exe in EnumerateCandidateExes(searchRoot!, maxDepth: 3, budget: TimeSpan.FromSeconds(2)))
        {
            if (IsValidGameExe(exe)) yield return exe;
        }
    }

    /// <summary>
    /// 收集桌面、公共桌面、开始菜单与快速启动栏里的 .lnk。
    /// 文件名带原神/HoYo 线索的排前面，其余排在后面（仍会解析）。
    /// </summary>
    public static IEnumerable<(string Path, string Location)> CollectShortcutFiles()
    {
        var roots = new List<(string Dir, string Location)>();
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "开始菜单");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "公共开始菜单");
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), "任务栏");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Path, int Rank, string Location)>();
        foreach (var (dir, location) in roots)
        {
            if (string.IsNullOrWhiteSpace(dir) || !PathUtil.ExistsDir(dir)) continue;

            List<string> found;
            try
            {
                // 全树递归枚举桌面/开始菜单目录，命中上限由 MaxShortcutsListedPerLocation 截断
                found = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories)
                    .Take(MaxShortcutsListedPerLocation)
                    .ToList();
            }
            catch
            {
                continue;
            }

            // 先按「名字像原神」排序再截断：否则桌面上快捷方式很多时，
            // 真正要找的那个可能排在第 40 个之后被丢掉。
            foreach (var lnk in found
                         .Where(seen.Add)
                         .OrderBy(lnk => HasGameHint(lnk) ? 0 : 1)
                         .Take(MaxShortcutsPerLocation))
            {
                files.Add((lnk, HasGameHint(lnk) ? 0 : 1, location));
            }
        }

        return files
            .OrderBy(f => f.Rank)
            .Select(f => (f.Path, f.Location));

        static bool HasGameHint(string lnk)
        {
            var name = Path.GetFileNameWithoutExtension(lnk);
            return ShortcutNameHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
        }

        void Add(string dir, string location)
        {
            if (!string.IsNullOrWhiteSpace(dir)) roots.Add((dir, location));
        }
    }

    /// <summary>
    /// 用 WScript.Shell 解析 .lnk 的目标路径。解析不了（损坏、目标被删、权限）返回 null。
    /// </summary>
    public static string? ResolveShortcutTarget(string lnkPath)
    {
        if (!PathUtil.ExistsFile(lnkPath)) return null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = new WshShell();
            var type = shell.GetType();
            shortcut = type.InvokeMember(
                "CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [lnkPath]);
            if (shortcut is null) return null;
            var target = shortcut.GetType().InvokeMember(
                "TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>WScript.Shell 的 COM 类（Windows 自带，无需注册表安装）。</summary>
    [ComImport]
    [Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")]
    [ClassInterface(ClassInterfaceType.None)]
    private class WshShell
    {
    }
}
