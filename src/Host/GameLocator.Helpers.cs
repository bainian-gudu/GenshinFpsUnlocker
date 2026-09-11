using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 定位辅助：枚举候选 exe、config.ini、注册表路径（partial）。
/// </summary>
internal static partial class GameLocator
{
    /// <summary>在 root 及其浅层子目录中枚举候选主程序路径。</summary>
    private static IEnumerable<string> EnumerateCandidateExes(string root)
    {
        if (!Directory.Exists(root)) yield break;

        foreach (var name in new[] { YuanShenExe, GenshinImpactExe })
        {
            var direct = Path.Combine(root, name);
            if (File.Exists(direct)) yield return direct;

            // 常见游戏子目录名
            foreach (var sub in new[] { "Genshin Impact Game", "GenshinImpactGame", "Game", "Games" })
            {
                var p = Path.Combine(root, sub, name);
                if (File.Exists(p)) yield return p;
            }
        }

        // 再向下浅搜一层
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch { yield break; }

        foreach (var dir in dirs)
        {
            foreach (var name in new[] { YuanShenExe, GenshinImpactExe })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) yield return p;
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
            // ignore
        }

        return null;
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
            // ignore
        }
    }
}
