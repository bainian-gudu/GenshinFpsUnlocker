using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 定位辅助：枚举候选 exe、config.ini、注册表路径（partial）。
/// </summary>
internal static partial class GameLocator
{
    /// <summary>
    /// 在 root 下枚举候选主程序路径。深度可配（默认 3 层）：自定义安装常见形如
    /// <c>D:\Games\HoYoPlay\Genshin Impact\Genshin Impact Game\YuanShen.exe</c>，
    /// 只查两层会漏掉。系统/数据目录在遍历前剪掉，并带耗时上限。
    /// </summary>
    private static IEnumerable<string> EnumerateCandidateExes(
        GameDescriptor game, string root, int maxDepth = 3, TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(5));
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((PathUtil.Normalize(root), 0));

        while (queue.Count > 0)
        {
            if (DateTime.UtcNow > deadline) yield break;
            var (dir, depth) = queue.Dequeue();
            if (!PathUtil.ExistsDir(dir)) continue;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (var f in files)
            {
                if (IsCandidateExeName(game, Path.GetFileName(f))) yield return f;
            }

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subs)
            {
                if (ShouldSkipDirectory(sub, atDriveRoot: depth == 0)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    /// <summary>枚举 root 下的 config.ini（顶层 + 一层子目录，跳过 _Data）。</summary>
    private static IEnumerable<string> EnumerateConfigIni(string root)
    {
        var direct = Path.Combine(root, "config.ini");
        if (File.Exists(direct)) yield return direct;

        // 仅浅层搜索 — 大目录树 AllDirectories 开销大且风险高
        string[] files;
        try
        {
            files = Directory.GetFiles(root, "config.ini", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            files = Array.Empty<string>();
        }
        foreach (var f in files) yield return f;

        // 仅再下一层
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch { yield break; }
        var count = 0;
        foreach (var d in dirs)
        {
            if (d.Contains("_Data", StringComparison.OrdinalIgnoreCase)) continue;
            var f = Path.Combine(d, "config.ini");
            if (PathUtil.ExistsFile(f))
            {
                yield return f;
                if (++count >= 20) yield break;
            }
        }
    }

    /// <summary>从 config.ini 读取 game_install_path= 值。</summary>
    private static string? ReadGameInstallPathFromIni(string iniPath)
    {
        try
        {
            foreach (var line in File.ReadLines(iniPath))
            {
                var m = GameInstallPathLine.Match(line);
                if (!m.Success) continue;
                var value = m.Groups[1].Value.Trim().Trim('"', '\'');
                if (Directory.Exists(value) || File.Exists(value)) return value;
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    /// <summary>候选主程序文件名是否属于该游戏（不看是否存在）。</summary>
    public static bool IsCandidateExeName(GameDescriptor game, string? fileName)
    {
        if (fileName is null) return false;
        foreach (var exe in game.ExeNames)
        {
            if (fileName.Equals(exe, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 目录名是否值得跳过。按完整目录名匹配（不是包含），避免把
    /// <c>Genshin Impact Game</c> 这类名字里的 "Game" 误判成要跳过。
    /// </summary>
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 系统与用户数据（里面有同名 exe 也基本不是游戏本体，扫进去纯浪费时间）
        "Windows", "WinSxS", "System32", "SysWOW64", "SystemApps", "servicing",
        "WinRE", "Recovery", "PerfLogs", "Boot", "System Volume Information",
        "$Recycle.Bin", "MSOCache", "$WinREAgent", "Config.Msi",
        "AppData", "Application Data", "Cookies", "NetHood", "PrintHood", "Recent",
        "SendTo", "Templates", "Start Menu", "Local Settings", "OneDriveTemp",
        "node_modules", ".git", ".svn", "packages", "NuGet", "NuGetFallbackFolder",
        "Installer", "WindowsApps", "Microsoft", "Windows Defender",
        "WindowsPowerShell", "DriverStore", "assembly", "WinMetadata",
    };

    /// <summary>系统盘根下不值得进入的目录（Program Files 由专门来源处理）。</summary>
    private static readonly HashSet<string> SkippedRootDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "ProgramData", "Users",
    };

    /// <summary>目录是否应跳过：名称命中剪枝表，或本身是符号链接/联接点。</summary>
    public static bool ShouldSkipDirectory(string path, bool atDriveRoot = false)
    {
        try
        {
            var name = Path.GetFileName(PathUtil.Normalize(path).TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) return false;
            if (SkippedDirectoryNames.Contains(name)) return true;
            if (atDriveRoot && SkippedRootDirectoryNames.Contains(name)) return true;

            // 递归跟随联接点/符号链接会绕圈或翻倍耗时
            var info = new DirectoryInfo(PathUtil.Normalize(path));
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        catch
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// 目录下是否有该游戏的 Unity 资源特征文件。只认文件名，不看内容。
    /// </summary>
    public static bool LooksLikeGameArtifacts(GameDescriptor game, string dir)
    {
        foreach (var sub in game.ArtifactDataFolders)
        {
            var dataDir = Path.Combine(dir, sub);
            if (!PathUtil.ExistsDir(dataDir)) continue;
            if (PathUtil.ExistsFile(Path.Combine(dataDir, "app.info"))) return true;
            if (PathUtil.ExistsDir(Path.Combine(dataDir, "StreamingAssets"))) return true;
        }
        return false;
    }

    /// <summary>
    /// exe 所在目录（或其上一层）是不是游戏根目录。
    /// 有的安装把主程序放在 <c>Genshin Impact Game</c> 子目录里，资源在其上一层。
    /// </summary>
    public static bool IsPlausibleGameRoot(GameDescriptor game, string exePath)
    {
        var dir = PathUtil.GetDirectoryNameSafe(exePath);
        for (var i = 0; i < 2 && !string.IsNullOrEmpty(dir); i++)
        {
            if (LooksLikeGameArtifacts(game, dir!)) return true;
            dir = PathUtil.GetDirectoryNameSafe(dir);
        }
        return false;
    }


    /// <summary>把注册表中出现的安装/启动器路径加入扫描根列表。</summary>
    private static void TryAddRegistryPath(List<string> roots, RegistryHive hive, string subKey)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(subKey)
                            ?? RegistryKey.OpenBaseKey(hive, RegistryView.Registry32).OpenSubKey(subKey);
            if (key is null) return;

            foreach (var valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is string s && !string.IsNullOrWhiteSpace(s))
                {
                    if (Directory.Exists(s)) roots.Add(s);
                    else if (File.Exists(s))
                    {
                        var dir = Path.GetDirectoryName(s);
                        if (!string.IsNullOrEmpty(dir)) roots.Add(dir);
                    }
                }
            }

            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var child = key.OpenSubKey(sub);
                    if (child?.GetValue("InstallPath") is string p && Directory.Exists(p))
                        roots.Add(p);
                    if (child?.GetValue("GameInstallPath") is string gp && Directory.Exists(gp))
                        roots.Add(gp);
                }
                catch { /* ignore */ }
            }
        }
        catch
        {
            // 忽略
        }
    }
}
