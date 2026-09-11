using System.Diagnostics;
using System.Net.Http;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Setup;

/// <summary>
/// 仅处理 .NET Windows Desktop Runtime（x64）的检测与自动安装。
/// - 应用托管 DLL、Stub（静态 CRT + 内嵌 MinHook）等其它依赖随 Payload 自包含，不在此下载；
/// - 本项目无 Node / Python 等编程语言运行时依赖；
/// - 缺失时下载官方 .exe 安装包并以 /install /quiet /norestart 静默装入系统；
/// - 安装器自身 SC，目标机无 .NET 也能完成引导。
/// </summary>
internal static class RuntimeCheck
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(20),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            SetupConstants.ProductName + "-Setup/" + typeof(RuntimeCheck).Assembly.GetName().Version);
        return c;
    }

    public static bool HasDotNetDesktopRuntime(out string detail)
    {
        var found = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                    if (IsSupportedDesktopVersion(name)) found.Add("reg:" + name);
                foreach (var sub in key.GetSubKeyNames())
                    if (IsSupportedDesktopVersion(sub)) found.Add("regkey:" + sub);
            }
        }
        catch { /* ignore */ }

        try
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var ver = Path.GetFileName(d);
                    if (IsSupportedDesktopVersion(ver)) found.Add("dir:" + ver);
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
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                    && (line.Contains(" 8.", StringComparison.Ordinal) || line.Contains(" 9.", StringComparison.Ordinal)))
                    found.Add("dotnet:" + line.Trim());
            }
        }
        catch { /* ignore */ }

        detail = found.Count > 0
            ? string.Join(" | ", found.Distinct().Take(8))
            : "未找到 .NET Desktop Runtime 8/9 x64";
        return found.Count > 0;
    }

    private static bool IsSupportedDesktopVersion(string? ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return false;
        return ver.StartsWith("8.", StringComparison.Ordinal)
               || ver.StartsWith("9.", StringComparison.Ordinal);
    }

    /// <summary>
    /// 若 payload 为框架依赖且本机缺少运行库，则下载官方 .exe 并静默安装。
    /// 已具备运行库或 payload 自包含时直接成功。
    /// downloadCacheDir：安装包缓存目录（默认在安装目录下 .runtime-cache，装完可删）。
    /// </summary>
    public static async Task EnsureRuntimeAsync(
        string? payloadDir,
        string? downloadCacheDir,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken ct)
    {
        if (payloadDir is not null && PayloadLocator.IsSelfContained(payloadDir))
        {
            progress?.Report(new RuntimeInstallProgress("主程序自包含，跳过运行库安装。", 100, null));
            return;
        }

        if (HasDotNetDesktopRuntime(out var detail))
        {
            progress?.Report(new RuntimeInstallProgress("已检测到运行库：" + detail, 100, null));
            return;
        }

        progress?.Report(new RuntimeInstallProgress(
            "未检测到 .NET 桌面运行时，开始下载官方安装包…", 0, null));

        await DownloadAndInstallAsync(downloadCacheDir, progress, ct).ConfigureAwait(false);

        for (var i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (HasDotNetDesktopRuntime(out detail))
            {
                progress?.Report(new RuntimeInstallProgress("运行库安装成功：" + detail, 100, null));
                return;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "已尝试安装 .NET Desktop Runtime，但仍未检测到。\n" +
            "请手动安装后重试：\n" + SetupConstants.DotnetDesktopUrl + "\n" +
            "或打开：" + SetupConstants.DotnetDesktopPage);
    }

    public static void EnsureRuntime(
        string? payloadDir,
        string? downloadCacheDir,
        Action<string>? log,
        CancellationToken ct)
    {
        var progress = new Progress<RuntimeInstallProgress>(p => log?.Invoke(p.Message));
        EnsureRuntimeAsync(payloadDir, downloadCacheDir, progress, ct).GetAwaiter().GetResult();
    }

    public static async Task DownloadAndInstallAsync(
        string? downloadCacheDir,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken ct)
    {
        // 优先放在安装目录下的缓存子目录；否则用 Temp。装完删除，不长期占用。
        var cacheRoot = string.IsNullOrWhiteSpace(downloadCacheDir)
            ? Path.Combine(Path.GetTempPath(), SetupConstants.ProductName + "-runtime")
            : downloadCacheDir;
        Directory.CreateDirectory(cacheRoot);
        var installerPath = Path.Combine(cacheRoot, "windowsdesktop-runtime-win-x64.exe");

        try
        {
            await DownloadFileAsync(
                SetupConstants.DotnetDesktopUrl,
                installerPath,
                progress,
                ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            progress?.Report(new RuntimeInstallProgress("正在静默安装 .NET 桌面运行时…", 92, null));

            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                // 官方 Burn/EXE 安装包：/install /quiet /norestart
                Arguments = "/install /quiet /norestart",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动运行库安装程序。");

            var exited = await WaitForExitAsync(proc, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new TimeoutException("运行库安装超时。");
            }

            // 0 = 成功；3010 = 成功但可能需要重启
            var code = proc.ExitCode;
            if (code is not 0 and not 3010)
            {
                throw new InvalidOperationException(
                    $"运行库安装程序退出码 {code}。\n可手动下载：{SetupConstants.DotnetDesktopUrl}");
            }

            progress?.Report(new RuntimeInstallProgress(
                code == 3010
                    ? "运行库已安装（可能需要重启后完全生效）。"
                    : "运行库安装程序已完成。",
                98,
                null));
        }
        finally
        {
            try
            {
                if (File.Exists(installerPath))
                    File.Delete(installerPath);
            }
            catch { /* ignore */ }

            try
            {
                if (Directory.Exists(cacheRoot)
                    && !Directory.EnumerateFileSystemEntries(cacheRoot).Any())
                    Directory.Delete(cacheRoot, recursive: false);
            }
            catch { /* ignore */ }
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<RuntimeInstallProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new RuntimeInstallProgress("连接下载服务器…", 1, url));

        try { if (File.Exists(destPath)) File.Delete(destPath); } catch { /* ignore */ }

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var local = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        var lastPct = -1;
        while ((read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await local.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;

            int pct;
            string msg;
            if (total is > 0)
            {
                pct = 2 + (int)Math.Clamp(88.0 * readTotal / total.Value, 0, 88);
                var mb = readTotal / (1024.0 * 1024.0);
                var totalMb = total.Value / (1024.0 * 1024.0);
                msg = $"正在下载运行库安装包… {mb:F1} / {totalMb:F1} MB";
            }
            else
            {
                pct = Math.Min(80, 2 + (int)(readTotal / (256.0 * 1024)));
                msg = $"正在下载运行库安装包… {readTotal / (1024.0 * 1024.0):F1} MB";
            }

            if (pct != lastPct)
            {
                lastPct = pct;
                progress?.Report(new RuntimeInstallProgress(msg, pct, url));
            }
        }

        await local.FlushAsync(ct).ConfigureAwait(false);

        if (readTotal < 1_000_000)
            throw new IOException("下载的运行库安装包过小，可能下载失败。");

        progress?.Report(new RuntimeInstallProgress(
            $"下载完成（{readTotal / (1024.0 * 1024.0):F1} MB）。", 90, destPath));
    }

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>打开浏览器下载页 / 直链（手动备用）。</summary>
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

/// <summary>运行库下载/安装进度。</summary>
internal readonly record struct RuntimeInstallProgress(
    string Message,
    int Percent,
    string? Detail);
