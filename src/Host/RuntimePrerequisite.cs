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
/// - 默认 FDD：强制检测本机 .NET Desktop Runtime 8/9（安装器下载官方 .exe 静默安装）
/// - 可选 SC：旁路含 coreclr 时不强制本机 Runtime
/// - 无 Node/Python 等语言依赖；Stub 静态 CRT，VC++ 仅记日志
/// 硬性缺失时弹窗并提供官方下载链接。
/// </summary>
internal static class RuntimePrerequisite
{
    // Microsoft 官方下载页 / 直链
    public const string DotnetDesktopRuntimeUrl =
        "https://dotnet.microsoft.com/download/dotnet/9.0";

    public const string DotnetDesktopRuntimeDirectX64 =
        "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe";

    public const string VcRedistX64Url =
        "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    public const string WebView2RuntimeUrl =
        "https://developer.microsoft.com/microsoft-edge/webview2/";

    public const string WebView2RuntimeDirectX64 =
        "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>执行完整检测并返回结构化结果。</summary>
    public static RuntimeCheckResult Check()
    {
        if (!OsCompatibility.MeetsMinimumOs(out var osDetail))
        {
            return new RuntimeCheckResult(
                false,
                "需要 Windows 10 / Windows 11（x64）",
                "本软件支持 64 位 Windows 10（1607 及以上）与 Windows 11。\n\n" +
                "检测：" + osDetail,
                null,
                IsFrameworkDependent());
        }

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
            if (!IsDotNetDesktopRuntimeInstalled(out var detail))
            {
                return new RuntimeCheckResult(
                    false,
                    "缺少 .NET 桌面运行时",
                    "未检测到 .NET Desktop Runtime 8/9 x64（Windows x64）。\n\n" +
                    "应用 DLL / Stub 等已随安装包提供；仅需系统 .NET 桌面运行时。\n" +
                    "请使用官方安装器（会自动下载并静默安装 .exe），或手动安装后重试。\n\n" +
                    $"检测详情：{detail}\n\n" +
                    $"下载页：{DotnetDesktopRuntimeUrl}\n" +
                    $"安装包(x64)：{DotnetDesktopRuntimeDirectX64}",
                    DotnetDesktopRuntimeDirectX64,
                    true);
            }
        }

        // 主界面依赖 Edge WebView2 Runtime
        if (!IsWebView2RuntimeInstalled(out var wvDetail))
        {
            return new RuntimeCheckResult(
                false,
                "缺少 WebView2 运行时",
                "未检测到 Microsoft Edge WebView2 Runtime。\n\n" +
                "主界面需要 WebView2 才能显示。Windows 10/11 通常已预装；\n" +
                "若被卸载或企业环境缺失，请安装官方 Evergreen Runtime 后重试。\n\n" +
                $"检测详情：{wvDetail}\n\n" +
                $"下载页：{WebView2RuntimeUrl}\n" +
                $"安装包(x64)：{WebView2RuntimeDirectX64}",
                WebView2RuntimeDirectX64,
                fdd);
        }

        // Stub 已静态链接 CRT（/MT），一般不再需要单独 VC++ 红包。
        // 仅记录检测结果，不弹窗、不阻断。
        if (!IsVcRedistX64Present(out var vcDetail))
            AppLog.Debug("VC++ redist not clearly present (Stub uses static CRT): " + vcDetail);
        else
            AppLog.Debug("VC++ redist: " + vcDetail);

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

        // ok=true 且带 DownloadUrl 时的软提示（预留；VC++ 已改为仅记日志）
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
    public static bool IsDotNetDesktopRuntimeInstalled(out string detail)
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
                    if (name.StartsWith("8.", StringComparison.Ordinal) || name.StartsWith("9.", StringComparison.Ordinal))
                        found.Add("reg:" + name);
                }
                foreach (var sub in key.GetSubKeyNames())
                {
                    if (sub.StartsWith("8.", StringComparison.Ordinal) || sub.StartsWith("9.", StringComparison.Ordinal))
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
                    if (ver.StartsWith("8.", StringComparison.Ordinal) || ver.StartsWith("9.", StringComparison.Ordinal))
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
                FileName = ResolveDotNetCli(),
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
                        && (line.Contains(" 8.", StringComparison.Ordinal) || line.Contains(" 9.", StringComparison.Ordinal)))
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
                || fx.Contains(".NET 8 ", StringComparison.OrdinalIgnoreCase)
                || fx.Contains(".NET 9.", StringComparison.OrdinalIgnoreCase)
                || fx.Contains(".NET 9 ", StringComparison.OrdinalIgnoreCase))
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

        detail = string.IsNullOrEmpty(detail) ? "no .NET 8/9 Windows Desktop runtime found" : detail;
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

    /// <summary>
    /// 检测 Evergreen WebView2 Runtime（注册表 pv 或安装路径）。
    /// </summary>
    public static bool IsWebView2RuntimeInstalled(out string detail)
    {
        var notes = new List<string>();
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var path in new[]
                         {
                             @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                             @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                         })
                {
                    try
                    {
                        using var key = root.OpenSubKey(path);
                        var pv = key?.GetValue("pv") as string;
                        if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0")
                        {
                            detail = $"registry pv={pv}";
                            return true;
                        }
                        if (pv is not null) notes.Add($"pv={pv}");
                    }
                    catch (Exception ex) { notes.Add(ex.Message); }
                }
            }
        }
        catch (Exception ex) { notes.Add(ex.Message); }

        try
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var c in new[]
                     {
                         Path.Combine(pf86, "Microsoft", "EdgeWebView", "Application", "msedgewebview2.exe"),
                         Path.Combine(pf, "Microsoft", "EdgeWebView", "Application", "msedgewebview2.exe"),
                     })
            {
                if (File.Exists(c))
                {
                    detail = "found " + c;
                    return true;
                }
            }
        }
        catch (Exception ex) { notes.Add(ex.Message); }

        detail = notes.Count == 0 ? "WebView2 runtime not found" : string.Join("; ", notes);
        return false;
    }

    /// <summary>用默认浏览器打开 URL；失败则尝试复制到剪贴板。</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
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

    /// <summary>
    /// 定位 dotnet CLI：优先使用受保护安装目录下的绝对路径。
    ///
    /// 本进程可能以管理员身份运行，而 <c>FileName = "dotnet"</c> 会按 PATH 搜索；
    /// 若 PATH 中存在普通用户可写的目录（常见于被植入的机器配置），攻击者放一个
    /// 同名 exe 就能借我们的管理员令牌执行任意代码。因此只在找不到绝对路径时
    /// 才回退到 PATH 搜索。
    /// </summary>
    private static string ResolveDotNetCli()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "dotnet", "dotnet.exe");
            if (PathUtil.ExistsFile(candidate)) return candidate;
        }
        return "dotnet";
    }
}
