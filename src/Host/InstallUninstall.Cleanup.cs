using System.Diagnostics;
using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 卸载清理实现（partial）：白名单删文件、安全删目录、退出后延迟删除。
/// </summary>
internal static partial class InstallUninstall
{
    /// <summary>按白名单删除安装目录顶层文件及已知子目录（本地化/runtimes）。</summary>
    private static void DeleteKnownInstallFiles(string installDir)
    {
        foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file);
            var allowed = AllowedInstallFileNames.Contains(name)
                          || AllowedInstallExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                          || name.StartsWith("GenshinFpsUnlocker", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("FpsUnlocker", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                          || name.Equals("clretwrc.dll", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("ucrtbase", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("clr", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("coreclr", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("host", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("WindowsBase", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("DirectWrite", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("PenImc", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("wpfgfx", StringComparison.OrdinalIgnoreCase)
                          || name.StartsWith("D3D", StringComparison.OrdinalIgnoreCase);

            if (!allowed)
            {
                AppLog.Warn("skip non-whitelisted file during uninstall: " + name);
                continue;
            }

            try { File.Delete(file); }
            catch (Exception ex) { AppLog.Debug($"delete {name}: {ex.Message}"); }
        }

        // dotnet publish 常见本地化 / runtimes 子目录
        foreach (var sub in Directory.EnumerateDirectories(installDir))
        {
            var name = Path.GetFileName(sub);
            var okSub = name.Equals("runtimes", StringComparison.OrdinalIgnoreCase)
                        || name.Contains('-', StringComparison.Ordinal) // zh-CN, en-US, ...
                        || name.Length <= 5; // cs, de, ja, ko...
            if (!okSub)
            {
                AppLog.Warn("skip non-whitelisted subdir: " + name);
                continue;
            }

            try { DeleteDirectorySafe(sub); }
            catch (Exception ex) { AppLog.Debug($"del sub {name}: {ex.Message}"); }
        }
    }

    /// <summary>递归删除前再次拒绝危险根路径。</summary>
    private static void DeleteDirectorySafe(string dir)
    {
        var n = PathUtil.Normalize(dir);
        if (PathUtil.IsDangerousRootOrProfile(n)) throw new InvalidOperationException("refusing dangerous delete: " + n);
        if (PathUtil.ExistsDir(n))
            Directory.Delete(n, recursive: true);
    }

    /// <summary>
    /// 进程退出后延迟删除安装目录。
    /// 优先 PowerShell -LiteralPath（Unicode/中文安全）；失败则回退 cmd + 叶名校验。
    /// </summary>
    private static void ScheduleDirectoryDeleteAfterExit(string dir)
    {
        try
        {
            var n = PathUtil.Normalize(dir);
            // PowerShell 单引号转义
            var psPath = n.Replace("'", "''");
            var pid = Environment.ProcessId;

            var ps = Path.Combine(Path.GetTempPath(), "gfu_uninstall_" + Guid.NewGuid().ToString("N") + ".ps1");
            var script =
                "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                $"$target = '{psPath}'\r\n" +
                $"$pidWait = {pid}\r\n" +
                // 等待本进程退出（约 30s）
                "for ($i = 0; $i -lt 60; $i++) {\r\n" +
                "  if (-not (Get-Process -Id $pidWait -ErrorAction SilentlyContinue)) { break }\r\n" +
                "  Start-Sleep -Milliseconds 500\r\n" +
                "}\r\n" +
                "Start-Sleep -Seconds 1\r\n" +
                // 最终安全：叶目录名必须匹配
                "if ((Split-Path $target -Leaf) -ne 'GenshinFpsUnlocker') { exit 2 }\r\n" +
                "if (Test-Path -LiteralPath $target) {\r\n" +
                "  Remove-Item -LiteralPath $target -Recurse -Force\r\n" +
                "}\r\n" +
                "Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force\r\n";

            File.WriteAllText(ps, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            AppLog.Info("scheduled PS delete: " + n);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "schedule delete");
            // cmd 回退（UTF-8）；部分中文路径仍可能失败，故有叶名校验
            try
            {
                var bat = Path.Combine(Path.GetTempPath(), "gfu_uninstall_" + Guid.NewGuid().ToString("N") + ".cmd");
                var content =
                    "@echo off\r\nchcp 65001 >nul\r\n" +
                    "ping 127.0.0.1 -n 4 >nul\r\n" +
                    "rem Safety: only delete folder named GenshinFpsUnlocker\r\n" +
                    $"if /I not \"{Path.GetFileName(PathUtil.Normalize(dir))}\"==\"GenshinFpsUnlocker\" exit /b 2\r\n" +
                    $"rmdir /s /q \"{dir}\"\r\n" +
                    "del "%~f0"\r\n";
                File.WriteAllText(bat, content, new UTF8Encoding(true));
                Process.Start(new ProcessStartInfo
                {
                    FileName = bat,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex2)
            {
                AppLog.Error(ex2, "fallback bat delete");
            }
        }
    }

    /// <summary>估算安装体积（KB），写入 ARP EstimatedSize。</summary>
    private static int EstimateSizeKb()
    {
        try
        {
            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(AppPaths.ExeDirectory, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch { /* ignore */ }
            }
            return (int)Math.Max(1, bytes / 1024);
        }
        catch
        {
            return 20 * 1024;
        }
    }

    /// <summary>程序集三位数版本号。</summary>
    private static string GetVersionString()
    {
        try
        {
            var v = typeof(InstallUninstall).Assembly.GetName().Version;
            return v?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }
}
