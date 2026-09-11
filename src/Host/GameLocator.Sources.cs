using System.Diagnostics;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 各自动定位来源实现（partial）。
/// </summary>
internal static partial class GameLocator
{
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

            var match = WarmupFileLine.Match(content);
            if (!match.Success) continue;

            var fullPath = PathUtil.Normalize(match.Value + ".exe");
            if (IsValidGameExe(fullPath))
            {
                return GameLocateResult.Success(fullPath, GameLocateSource.UnityLog, logPath);
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
