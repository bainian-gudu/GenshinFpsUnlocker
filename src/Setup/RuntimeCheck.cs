using System.Diagnostics;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Setup;

/// <summary>安装前检测 .NET 8 桌面运行时（依赖框架构建时）。</summary>
internal static class RuntimeCheck
{
    public static bool HasDotNetDesktop8(out string detail)
    {
        var found = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                    if (name.StartsWith("8.", StringComparison.Ordinal)) found.Add("reg:" + name);
                foreach (var sub in key.GetSubKeyNames())
                    if (sub.StartsWith("8.", StringComparison.Ordinal)) found.Add("regkey:" + sub);
            }
        }
        catch { /* ignore */ }

        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(root))
            {
                foreach (var d in Directory.GetDirectories(root))
                {
                    var ver = Path.GetFileName(d);
                    if (ver.StartsWith("8.", StringComparison.Ordinal)) found.Add("dir:" + ver);
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(4000);
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                    && line.Contains(" 8.", StringComparison.Ordinal))
                    found.Add("dotnet:" + line.Trim());
            }
        }
        catch { /* ignore */ }

        detail = found.Count > 0 ? string.Join(" | ", found.Distinct().Take(6)) : "未找到";
        return found.Count > 0;
    }

    public static void OpenDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SetupConstants.DotnetDesktopUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SetupConstants.DotnetDesktopPage,
                    UseShellExecute = true,
                });
            }
            catch { /* ignore */ }
        }
    }
}
