using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 各自动定位来源实现（partial）。
/// </summary>
internal static partial class GameLocator
{
    /// <summary>Unity 日志里最多尝试几个 *_Data 候选路径（日志可能很长）。</summary>
    private const int MaxUnityLogCandidates = 50;

    /// <summary>从正在运行的 YuanShen / GenshinImpact 进程取映像路径。</summary>
    public static GameLocateResult LocateFromRunningProcess()
    {
        foreach (var name in GameProcess.ProcessNames)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }

            try
            {
                foreach (var p in list)
                {
                    try
                    {
                        var path = PathUtil.GetProcessImagePath(p.Id) ?? p.MainModule?.FileName;
                        path = PathUtil.Normalize(path);
                        if (IsValidGameExe(path))
                        {
                            return GameLocateResult.Success(path!, GameLocateSource.RunningProcess, $"运行中进程 PID {p.Id}");
                        }
                    }
                    catch
                    {
                        // MainModule 在无提权时可能抛异常
                    }
                }
            }
            finally
            {
                foreach (var p in list) p.Dispose();
            }
        }

        return GameLocateResult.Fail("当前没有运行中的原神进程");
    }

    /// <summary>
    /// 解析 %LocalLow%\miHoYo\原神|Genshin Impact\output_log.txt 中的 _Data 路径。
    /// </summary>
    public static GameLocateResult LocateFromUnityLog()
    {
        var localLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // LocalLow 与 Local 同级，位于 UserProfile\AppData 下
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            PathUtil.Normalize(Path.Combine(appData, @"..\LocalLow\miHoYo\Genshin Impact\output_log.txt")),
            PathUtil.Normalize(Path.Combine(appData, @"..\LocalLow\miHoYo\原神\output_log.txt")),
            PathUtil.Normalize(Path.Combine(localLow, @"..\LocalLow\miHoYo\Genshin Impact\output_log.txt")),
            PathUtil.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppData\LocalLow\miHoYo\Genshin Impact\output_log.txt")),
            PathUtil.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppData\LocalLow\miHoYo\原神\output_log.txt")),
        };

        foreach (var logPath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(logPath)) continue;

            string content;
            try { content = File.ReadAllText(logPath); }
            catch { continue; }

            // 同一行/同一份日志里可能有多个 *_Data 路径（崩溃转储、缓存目录…），
            // 逐个试到真的存在主程序为止，而不是只看第一条。
            var tried = 0;
            foreach (Match match in WarmupFileLine.Matches(content))
            {
                if (++tried > MaxUnityLogCandidates) break;
                var fullPath = PathUtil.Normalize(match.Value + ".exe");
                if (IsValidGameExe(fullPath))
                {
                    return GameLocateResult.Success(fullPath, GameLocateSource.UnityLog, logPath);
                }
            }
        }

        return GameLocateResult.Fail("Unity 日志中未解析到有效路径（请先成功启动过一次游戏）");
    }

    /// <summary>扫描常见安装根与启动器 config.ini 中的 game_install_path。</summary>
    public static GameLocateResult LocateFromLauncherConfigs()
    {
        var roots = new List<string>();

        // 常见安装根目录（含中文「原神」）
        foreach (var drive in Environment.GetLogicalDrives())
        {
            roots.Add(Path.Combine(drive, "Program Files", "Genshin Impact"));
            roots.Add(Path.Combine(drive, "Program Files", "GenshinImpact"));
            roots.Add(Path.Combine(drive, "Program Files", "Yuanshen"));
            roots.Add(Path.Combine(drive, "Program Files", "原神"));
            roots.Add(Path.Combine(drive, "Genshin Impact"));
            roots.Add(Path.Combine(drive, "GenshinImpact"));
            roots.Add(Path.Combine(drive, "Yuanshen"));
            roots.Add(Path.Combine(drive, "原神"));
            roots.Add(Path.Combine(drive, "miHoYo"));
            roots.Add(Path.Combine(drive, "HoYoVerse"));
        }

        // LocalAppData / ProgramData 下的启动器目录
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        roots.Add(Path.Combine(local, "miHoYo"));
        roots.Add(Path.Combine(local, "HoYoVerse"));
        roots.Add(Path.Combine(programData, "miHoYo"));
        roots.Add(Path.Combine(programData, "Hyphub"));
        roots.Add(Path.Combine(programData, "Hyp"));

        // HYP 注册表常保存启动器路径
        TryAddRegistryPath(roots, RegistryHive.CurrentUser, @"Software\miHoYo\HYP");
        TryAddRegistryPath(roots, RegistryHive.CurrentUser, @"Software\Cognosphere\HYP");
        TryAddRegistryPath(roots, RegistryHive.LocalMachine, @"SOFTWARE\miHoYo\HYP");
        TryAddRegistryPath(roots, RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\miHoYo");

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (!visited.Add(root)) continue;

            // 根目录或「Genshin Impact Game」子目录下的 exe
            foreach (var exe in EnumerateCandidateExes(root))
            {
                if (IsValidGameExe(exe))
                    return GameLocateResult.Success(exe, GameLocateSource.LauncherConfig, root);
            }

            // config.ini 中的 game_install_path=
            foreach (var ini in EnumerateConfigIni(root))
            {
                var path = ReadGameInstallPathFromIni(ini);
                if (path is null) continue;
                foreach (var exe in EnumerateCandidateExes(path))
                {
                    if (IsValidGameExe(exe))
                        return GameLocateResult.Success(exe, GameLocateSource.LauncherConfig, ini);
                }
            }
        }

        return GameLocateResult.Fail("启动器目录 / config.ini 中未找到游戏");
    }

    /// <summary>快扫预算：整层来源的耗时上限。</summary>
    public static readonly TimeSpan QuickScanBudget = TimeSpan.FromSeconds(8);

    /// <summary>驱动器根下最多进几层；自定义安装一般在 5 层以内，再深就不值得扫。</summary>
    private const int DriveScanDepth = 5;

    /// <summary>用户目录下最多进几层（Downloads/Desktop 里常是解压出来的游戏目录）。</summary>
    private const int UserDirScanDepth = 6;

    /// <summary>
    /// 非官方安装路径兜底：在用户目录与所有本地/可移动驱动器上做**有预算的**广度优先扫描。
    /// 这不是为了替代前面的来源，而是覆盖「D:\Games\原神\…」这类既不在
    /// Program Files、注册表里也没记录的情况。目录名剪枝 + 深度上限 + 总耗时上限
    /// 保证最坏情况也只是几秒。
    /// </summary>
    public static GameLocateResult LocateFromQuickScan()
    {
        var stopwatch = Stopwatch.StartNew();
        var (userRoots, driveRoots) = CollectQuickScanRoots();

        // 用户目录优先（命中率更高、扫描面更小），随后是整盘。
        var fromUserDirs = ScanRoots(userRoots, UserDirScanDepth, stopwatch);
        if (fromUserDirs.Ok) return fromUserDirs;

        var fromDrives = ScanRoots(driveRoots, DriveScanDepth, stopwatch);
        if (fromDrives.Ok) return fromDrives;

        return string.IsNullOrEmpty(fromDrives.Detail)
            ? GameLocateResult.Fail("磁盘快扫未找到原神主程序")
            : fromDrives;
    }

    /// <summary>在给定根目录集合上做一轮广度优先扫描（共用同一个耗时预算）。</summary>
    public static GameLocateResult ScanRoots(IEnumerable<string> roots, int maxDepth, Stopwatch stopwatch)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Dir, int Depth)>();

        foreach (var root in roots)
        {
            var normalized = PathUtil.Normalize(root);
            if (string.IsNullOrEmpty(normalized) || !PathUtil.ExistsDir(normalized)) continue;
            if (!visited.Add(normalized)) continue;
            queue.Enqueue((normalized, 0));
        }

        while (queue.Count > 0)
        {
            if (stopwatch.Elapsed > QuickScanBudget)
            {
                return GameLocateResult.Fail(
                    $"磁盘快扫超过 {QuickScanBudget.TotalSeconds:n0}s 预算，已放弃（看过 {visited.Count} 个目录）");
            }

            var (dir, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
            {
                if (!IsCandidateExeName(Path.GetFileName(file))) continue;
                var normalized = PathUtil.Normalize(file);
                if (!PathUtil.ExistsFile(normalized)) continue;
                // 只认带 Unity 资源特征的目录，避免把同名的无关 exe 当游戏
                if (!IsPlausibleGameRoot(normalized)) continue;
                return GameLocateResult.Success(
                    normalized, GameLocateSource.QuickScan,
                    $"磁盘扫描命中：{PathUtil.GetDirectoryNameSafe(normalized)}");
            }

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subs)
            {
                if (ShouldSkipDirectory(sub, atDriveRoot: depth == 0)) continue;
                var normalized = PathUtil.Normalize(sub);
                if (!visited.Add(normalized)) continue;
                queue.Enqueue((normalized, depth + 1));
            }
        }

        return GameLocateResult.Fail("这组目录里没有找到原神主程序");
    }

    /// <summary>快扫根目录：用户目录（含 OneDrive）与所有本地/可移动驱动器。</summary>
    public static (List<string> UserRoots, List<string> DriveRoots) CollectQuickScanRoots()
    {
        var userRoots = new List<string>();
        var driveRoots = new List<string>();

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyPictures,
                 })
        {
            Add(Environment.GetFolderPath(folder));
        }
        foreach (var env in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            Add(Environment.GetEnvironmentVariable(env));
        }
        // Downloads 不在 SpecialFolder 里
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                var root = PathUtil.Normalize(drive.RootDirectory.FullName);
                if (string.IsNullOrEmpty(root)) continue;
                if (!driveRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) driveRoots.Add(root);
            }
            catch { /* 光驱 / 未就绪的卷 */ }
        }

        return (userRoots, driveRoots);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            var normalized = PathUtil.Normalize(dir);
            if (string.IsNullOrEmpty(normalized)) return;
            if (driveRoots.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return;
            if (!userRoots.Contains(normalized, StringComparer.OrdinalIgnoreCase)) userRoots.Add(normalized);
        }
    }

    /// <summary>从“应用和功能”卸载信息中查找原神 InstallLocation。</summary>
    public static GameLocateResult LocateFromUninstallRegistry()
    {
        string[] subKeys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var sub in subKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(sub)
                                     ?? RegistryKey.OpenBaseKey(hive, RegistryView.Registry32).OpenSubKey(sub);
                    if (baseKey is null) continue;

                    foreach (var name in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var app = baseKey.OpenSubKey(name);
                            if (app is null) continue;
                            var display = app.GetValue("DisplayName") as string ?? "";
                            if (display.IndexOf("Genshin", StringComparison.OrdinalIgnoreCase) < 0
                                && display.IndexOf("原神", StringComparison.OrdinalIgnoreCase) < 0
                                && display.IndexOf("YuanShen", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            var location = app.GetValue("InstallLocation") as string
                                           ?? app.GetValue("DisplayIcon") as string;
                            if (string.IsNullOrWhiteSpace(location)) continue;

                            // DisplayIcon 可能是 "path\to\exe,0"
                            location = location.Split(',')[0].Trim().Trim('"');
                            if (File.Exists(location) && IsValidGameExe(location))
                                return GameLocateResult.Success(location, GameLocateSource.RegistryUninstall, display);

                            if (Directory.Exists(location))
                            {
                                foreach (var exe in EnumerateCandidateExes(location))
                                {
                                    if (IsValidGameExe(exe))
                                        return GameLocateResult.Success(exe, GameLocateSource.RegistryUninstall, display);
                                }
                            }
                        }
                        catch
                        {
                            // continue
                        }
                    }
                }
                catch
                {
                    // continue
                }
            }
        }

        return GameLocateResult.Fail("卸载信息注册表中未找到原神");
    }


}
