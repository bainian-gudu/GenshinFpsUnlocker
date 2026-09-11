using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Setup;

/// <summary>单条安装进度（供 UI 滚动列表与进度条使用）。</summary>
internal readonly record struct InstallProgressEvent(
    string Message,
    string? RelativePath,
    string? TargetPath,
    int Current,
    int Total,
    bool IsFileCopy);

/// <summary>执行复制、标记、快捷方式、ARP、可选 Defender 排除与收尾。</summary>
internal sealed class InstallEngine
{
    public required string PayloadDir { get; init; }
    public required string InstallDir { get; init; }
    public bool CreateDesktopShortcut { get; init; } = true;
    public bool AddDefenderExclusion { get; init; } = true;
    public bool StartAfterInstall { get; init; } = true;
    public bool EnableAutostart { get; init; } = false;
    public CancellationToken CancellationToken { get; init; }

    /// <summary>进度回调（可能在后台线程）。</summary>
    public event Action<InstallProgressEvent>? Progress;

    public void Run()
    {
        ValidateInstallDir();
        ThrowIfCancel();
        Report("准备安装目录…", null, null, 0, 1, false);
        Directory.CreateDirectory(InstallDir);

        var files = CollectPayloadFiles(PayloadDir);
        var total = Math.Max(1, files.Count + 6); // 复制 + 后续步骤
        var step = 0;

        Report($"开始复制 {files.Count} 个文件…", null, null, step, total, false);

        foreach (var (src, rel) in files)
        {
            ThrowIfCancel();
            step++;
            var target = Path.Combine(InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(src, target, overwrite: true);
            Report($"复制 {rel}", rel, target, step, total, true);
        }

        ThrowIfCancel();
        step++;
        Report("写入安装标记…", SetupConstants.MarkerName, Path.Combine(InstallDir, SetupConstants.MarkerName), step, total, false);
        WriteMarker(InstallDir);

        var data = SetupConstants.DataDir;
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(data, "logs"));

        var exe = Path.Combine(InstallDir, SetupConstants.ExeName);
        if (!File.Exists(exe))
            throw new InvalidOperationException("安装后未找到主程序: " + exe);

        ThrowIfCancel();
        step++;
        Report("创建快捷方式…", null, null, step, total, false);
        CreateShortcuts(exe, InstallDir, CreateDesktopShortcut);

        ThrowIfCancel();
        step++;
        Report("注册卸载信息…", null, null, step, total, false);
        RegisterArp(exe, InstallDir);
        WriteUninstallCmd(exe, InstallDir);

        step++;
        if (AddDefenderExclusion)
        {
            Report("添加 Defender 排除（尽力而为）…", null, null, step, total, false);
            TryDefender(InstallDir, data);
        }
        else
        {
            Report("跳过 Defender 排除", null, null, step, total, false);
        }

        step++;
        if (EnableAutostart)
        {
            Report("写入开机自启（HKCU）…", null, null, step, total, false);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                key?.SetValue(SetupConstants.ProductName, $"\"{exe}\" --autostart --minimized");
            }
            catch (Exception ex)
            {
                Report("自启写入失败: " + ex.Message, null, null, step, total, false);
            }
        }
        else
        {
            Report("未启用开机自启", null, null, step, total, false);
        }

        ThrowIfCancel();
        step++;
        Report("主程序收尾注册…", SetupConstants.ExeName, exe, step, total, false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--install --no-run --quiet",
                UseShellExecute = true,
                WorkingDirectory = InstallDir,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
        }
        catch (Exception ex)
        {
            Report("主程序收尾: " + ex.Message, null, null, step, total, false);
        }

        if (StartAfterInstall)
        {
            Report("启动程序…", SetupConstants.ExeName, exe, total, total, false);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = InstallDir,
                });
            }
            catch (Exception ex)
            {
                Report("启动失败: " + ex.Message, null, null, total, total, false);
            }
        }

        Report("安装完成。", null, InstallDir, total, total, false);
    }

    private void ThrowIfCancel()
    {
        if (CancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("用户取消了安装。");
    }

    private void ValidateInstallDir()
    {
        var full = Path.GetFullPath(InstallDir).TrimEnd('\\', '/');
        var leaf = Path.GetFileName(full);
        if (!leaf.Equals(SetupConstants.ProductName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"安装目录名必须为 {SetupConstants.ProductName}（当前: {leaf}）。");

        if (full.Length <= 3)
            throw new InvalidOperationException("拒绝安装到盘符根目录。");

        // 拒绝危险系统根
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\', '/');
        if (string.Equals(full, pf, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("禁止直接安装到 Program Files 根目录，请使用子文件夹 GenshinFpsUnlocker。");
    }

    /// <summary>枚举 Payload 内待复制文件（相对路径）。</summary>
    public static List<(string FullPath, string Relative)> CollectPayloadFiles(string src)
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(src)) return list;

        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (ShouldSkipPayloadFile(rel, src)) continue;
            list.Add((file, rel));
        }

        list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static bool ShouldSkipPayloadFile(string rel, string srcRoot)
    {
        if (rel.EndsWith(".Setup.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.Equals("GenshinFpsUnlocker.Setup.exe", StringComparison.OrdinalIgnoreCase)) return true;
        // 避免嵌套 Payload
        if (rel.StartsWith("Payload" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static void WriteMarker(string installDir)
    {
        var path = Path.Combine(installDir, SetupConstants.MarkerName);
        var content =
            $"product={SetupConstants.ProductName}\n" +
            $"installUtc={DateTime.UtcNow:O}\n" +
            $"installDir={installDir}\n" +
            $"via=GenshinFpsUnlocker.Setup\n";
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteUninstallCmd(string exe, string installDir)
    {
        try
        {
            var cmd = Path.Combine(installDir, "Uninstall.cmd");
            var content =
                "@echo off\r\nchcp 65001 >nul\r\n" +
                $"\"{exe}\" --uninstall\r\n";
            File.WriteAllText(cmd, content, new UTF8Encoding(true));
        }
        catch { /* ignore */ }
    }

    private static void RegisterArp(string exe, string installDir)
    {
        try
        {
            var path = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{SetupConstants.ProductName}";
            using var key = Registry.LocalMachine.CreateSubKey(path)
                            ?? Registry.CurrentUser.CreateSubKey(path);
            if (key is null) return;
            key.SetValue("DisplayName", SetupConstants.DisplayName);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("Publisher", SetupConstants.ProductName);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("GFU_ProductId", SetupConstants.ProductName);
        }
        catch { /* ignore */ }
    }

    private static void TryDefender(string installDir, string dataDir)
    {
        try
        {
            var ps =
                $"Add-MpPreference -ExclusionPath '{installDir.Replace("'", "''")}' -ErrorAction SilentlyContinue; " +
                $"Add-MpPreference -ExclusionPath '{dataDir.Replace("'", "''")}' -ErrorAction SilentlyContinue";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit(15000);
        }
        catch { /* ignore */ }
    }

    private static void CreateShortcuts(string exe, string workDir, bool desktop)
    {
        var programsRoot = GetProgramsRoot();
        var dir = Path.Combine(programsRoot, SetupConstants.ProductName);
        Directory.CreateDirectory(dir);

        WriteLnk(Path.Combine(dir, SetupConstants.DisplayName + ".lnk"), exe, null, workDir, SetupConstants.DisplayName);
        WriteLnk(Path.Combine(dir, "卸载 " + SetupConstants.DisplayName + ".lnk"), exe, "--uninstall", workDir, "卸载");
        var logDir = Path.Combine(SetupConstants.DataDir, "logs");
        Directory.CreateDirectory(logDir);
        WriteLnk(Path.Combine(dir, "打开日志目录.lnk"), "explorer.exe", $"\"{logDir}\"", logDir, "日志");

        if (desktop)
        {
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk))
                desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            WriteLnk(Path.Combine(desk, SetupConstants.DisplayName + ".lnk"), exe, null, workDir, SetupConstants.DisplayName);
        }
    }

    private static string GetProgramsRoot()
    {
        try
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            if (!string.IsNullOrEmpty(common))
            {
                Directory.CreateDirectory(Path.Combine(common, SetupConstants.ProductName));
                return common;
            }
        }
        catch { /* ignore */ }
        return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    }

    private static void WriteLnk(string lnkPath, string target, string? args, string workDir, string desc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
        link.SetWorkingDirectory(workDir);
        link.SetDescription(desc);
        try { link.SetIconLocation(target, 0); } catch { /* ignore */ }
        ((IPersistFile)link).Save(lnkPath, true);
    }

    private void Report(string msg, string? rel, string? target, int cur, int total, bool isFile)
        => Progress?.Invoke(new InstallProgressEvent(msg, rel, target, cur, total, isFile));

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
