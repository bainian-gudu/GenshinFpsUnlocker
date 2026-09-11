using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>运行时检测结果（含用户可读说明与下载链接）。</summary>
internal sealed record RuntimeCheckResult(
    bool Ok,
    string Title,
    string Message,
    string? DownloadUrl,
    bool IsFrameworkDependentBuild);

/// <summary>
/// 检测启动所需运行库是否就绪：
/// - 依赖框架（FDD）发布需要 .NET Desktop Runtime 8.x（Windows x64）
/// - 游戏 Stub 始终需要 64 位 Windows
/// - VC++ x64 为软提示（缺失不硬阻断）
/// 缺失时弹窗并提供官方下载链接。
/// </summary>
internal static class RuntimePrerequisite
{
    // Microsoft 官方下载页 / 直链
    public const string DotnetDesktopRuntime8Url =
        "https://dotnet.microsoft.com/download/dotnet/8.0";

    public const string DotnetDesktopRuntime8DirectX64 =
        "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

    public const string VcRedistX64Url =
        "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>执行完整检测并返回结构化结果。</summary>
    public static RuntimeCheckResult Check()
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return new RuntimeCheckResult(
                false,
                "需要 64 位 Windows",
                "本软件仅支持 64 位 Windows 系统。",
                null,
                IsFrameworkDependent());
        }

        var fdd = IsFrameworkDependent();
        if (fdd)
        {
            if (!IsDotNetDesktopRuntime8Installed(out var detail))
            {
                return new RuntimeCheckResult(
                    false,
                    "缺少 .NET 8 桌面运行时",
                    "未检测到 .NET Desktop Runtime 8.x（Windows x64）。\n\n" +
                    "本程序以“依赖框架”方式发布，需要先安装运行库才能启动。\n\n" +
                    $"检测详情：{detail}\n\n" +
                    "请下载并安装后重新运行安装程序 / 本软件。\n\n" +
                    $"下载页：{DotnetDesktopRuntime8Url}\n" +
                    $"直链(x64)：{DotnetDesktopRuntime8DirectX64}",
                    DotnetDesktopRuntime8DirectX64,
                    true);
            }
        }

        // VC++ 通常已存在；缺失时仅警告，不硬阻断
        if (!IsVcRedistX64Present(out var vcDetail))
        {
            AppLog.Warn($"未明确检测到 VC++ 可再发行组件: {vcDetail}");
            return new RuntimeCheckResult(
                true, // soft
                "建议安装 VC++ 运行库",
                "未明确检测到 Visual C++ 2015-2022 x64 运行库。\n" +
                "若注入 Stub 失败，请安装 VC++ 可再发行组件包。\n\n" +
                $"下载：{VcRedistX64Url}\n\n详情：{vcDetail}",
                VcRedistX64Url,
                fdd);
        }

        return new RuntimeCheckResult(true, "运行库检查通过", "所需运行库已就绪。", null, fdd);
    }

    /// <summary>
    /// 缺失时弹出提示。硬性缺失且用户选择退出时返回 false。
    /// quiet 模式下硬性缺失直接失败，不弹窗。
    /// </summary>
    public static bool EnsureOrPrompt(bool quiet)
    {
        var result = Check();
        AppLog.Info($"runtime check: ok={result.Ok} title={result.Title} fdd={result.IsFrameworkDependentBuild}");

        if (result.Ok && result.DownloadUrl is null)
            return true;

        // 软警告（如 VC++）— ok=true 但带下载提示
        if (result.Ok)
        {
            if (!quiet)
            {
                var r = MessageBox.Show(
                    result.Message + "\n\n是否打开下载页面？",
                    result.Title,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (r == DialogResult.Yes && result.DownloadUrl is not null)
                    OpenUrl(result.DownloadUrl);
            }
            return true;
        }

        // 硬失败
        if (quiet)
        {
            AppLog.Error($"runtime missing (quiet): {result.Message}");
            return false;
        }

        var buttons = result.DownloadUrl is null ? MessageBoxButtons.OK : MessageBoxButtons.YesNoCancel;
        var page = MessageBox.Show(
            result.Message + (result.DownloadUrl is null ? "" : "\n\n是 = 打开下载链接并退出\n否 = 仍然尝试继续（可能无法运行）\n取消 = 退出"),
            result.Title,
            buttons,
            MessageBoxIcon.Warning);

        if (page == DialogResult.Yes && result.DownloadUrl is not null)
        {
            OpenUrl(result.DownloadUrl);
            return false;
        }

        if (page == DialogResult.No)
        {
            AppLog.Warn("用户选择在缺少运行库时继续");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断当前是否为依赖框架发布（FDD）。
    /// 旁路存在 coreclr/hostfxr 则视为自包含（SC）；否则看 runtimeconfig.json。
    /// </summary>
    public static bool IsFrameworkDependent()
    {
        try
        {
            var dir = AppPaths.ExeDirectory;
            var markers = new[]
            {
                "coreclr.dll",
                "hostfxr.dll",
                "hostpolicy.dll",
            };
            var any = markers.Any(m => File.Exists(Path.Combine(dir, m)));
            if (any) return false;

            foreach (var cfg in Directory.EnumerateFiles(dir, "*.runtimeconfig.json"))
            {
                try
                {
                    var text = File.ReadAllText(cfg);
                    if (text.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch { /* ignore */ }
            }

            return !any;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 多途径检测 .NET 8 Windows Desktop 运行时：
    /// 注册表、共享框架目录、dotnet --list-runtimes、当前进程 FrameworkDescription。
    /// </summary>
    public static bool IsDotNetDesktopRuntime8Installed(out string detail)
    {
        detail = "";
        var found = new List<string>();

        // 1) 注册表
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (name.StartsWith("8.", StringComparison.Ordinal))
                        found.Add("reg:" + name);
                }
                foreach (var sub in key.GetSubKeyNames())
                {
                    if (sub.StartsWith("8.", StringComparison.Ordinal))
                        found.Add("regkey:" + sub);
                }
            }
        }
        catch (Exception ex)
        {
            detail += "regErr=" + ex.Message + "; ";
        }

        // 2) 共享框架文件夹
        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var ver = Path.GetFileName(d);
                    if (ver.StartsWith("8.", StringComparison.Ordinal))
                        found.Add("dir:" + ver);
                }
            }
        }
        catch (Exception ex)
        {
            detail += "dirErr=" + ex.Message + "; ";
        }

        // 3) dotnet --list-runtimes
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                        && line.Contains(" 8.", StringComparison.Ordinal))
                    {
                        found.Add("dotnet:" + line.Trim());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            detail += "dotnetErr=" + ex.Message + "; ";
        }

        // 4) 若本进程已在 .NET 8 上运行，直接认可
        try
        {
            var fx = RuntimeInformation.FrameworkDescription;
            if (fx.Contains(".NET 8.", StringComparison.OrdinalIgnoreCase)
                || fx.Contains(".NET 8 ", StringComparison.OrdinalIgnoreCase))
            {
                found.Add("running:" + fx);
            }
        }
        catch { /* ignore */ }

        if (found.Count > 0)
        {
            detail = string.Join(" | ", found.Distinct().Take(8));
            return true;
        }

        detail = string.IsNullOrEmpty(detail) ? "no .NET 8 Windows Desktop runtime found" : detail;
        return false;
    }

    /// <summary>检测 VC++ 2015-2022 x64（注册表或 System32 中的 vcruntime140/msvcp140）。</summary>
    public static bool IsVcRedistX64Present(out string detail)
    {
        detail = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64")
                ?? Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (key is not null)
            {
                var installed = key.GetValue("Installed");
                var ver = key.GetValue("Version") as string ?? "";
                detail = $"installed={installed} version={ver}";
                if (installed is int i && i == 1) return true;
                if (installed is long l && l == 1) return true;
            }
        }
        catch (Exception ex)
        {
            detail = ex.Message;
        }

        // 回退：System32 中的运行库 DLL
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var a = File.Exists(Path.Combine(sys, "vcruntime140.dll"));
            var b = File.Exists(Path.Combine(sys, "msvcp140.dll"));
            detail += $" sys32 vcruntime140={a} msvcp140={b}";
            if (a && b) return true;
        }
        catch { /* ignore */ }

        return false;
    }

    /// <summary>用默认浏览器打开 URL；失败则尝试复制到剪贴板。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "open url " + url);
            try { Clipboard.SetText(url); } catch { /* ignore */ }
            MessageBox.Show(
                "无法打开浏览器，已尝试复制链接到剪贴板：\n" + url,
                "下载",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
