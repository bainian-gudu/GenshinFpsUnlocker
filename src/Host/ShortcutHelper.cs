using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 通过 Shell Link COM 创建开始菜单与桌面快捷方式（无需额外 NuGet）。
/// 卸载时仅删除本产品名称相关的 .lnk / 开始菜单子文件夹。
/// </summary>
internal static class ShortcutHelper
{
    /// <summary>创建/刷新开始菜单 + 桌面全部快捷方式。</summary>
    public static void CreateAll(string? exePath = null, string? workDir = null)
    {
        exePath ??= AppPaths.ExePath;
        workDir ??= AppPaths.ExeDirectory;

        try
        {
            CreateStartMenuShortcuts(exePath, workDir);
            AppLog.Info("开始菜单快捷方式已创建/更新");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "创建开始菜单快捷方式");
        }

        try
        {
            CreateDesktopShortcut(exePath, workDir);
            AppLog.Info("桌面快捷方式已创建/更新");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "创建桌面快捷方式");
        }
    }

    /// <summary>
    /// 开始菜单：主程序、卸载、打开日志目录。
    /// 提权时优先写 All Users；否则写当前用户。
    /// </summary>
    public static void CreateStartMenuShortcuts(string exePath, string workDir)
    {
        var programsRoot = GetProgramsRoot();
        var dir = Path.Combine(programsRoot, AppPaths.ProductName);
        Directory.CreateDirectory(dir);

        CreateShortcut(
            Path.Combine(dir, AppPaths.ProductDisplayName + ".lnk"),
            exePath,
            arguments: null,
            workDir,
            description: AppPaths.ProductDisplayName);

        CreateShortcut(
            Path.Combine(dir, "卸载 " + AppPaths.ProductDisplayName + ".lnk"),
            exePath,
            arguments: "--uninstall",
            workDir,
            description: "卸载并清理全部数据");

        CreateShortcut(
            Path.Combine(dir, "打开日志目录.lnk"),
            "explorer.exe",
            arguments: $"\"{AppPaths.LogDirectory}\"",
            workDir: AppPaths.LogDirectory,
            description: "打开调试日志目录");
    }

    /// <summary>在公共桌面（失败则用户桌面）创建主程序快捷方式。</summary>
    public static void CreateDesktopShortcut(string exePath, string workDir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        CreateShortcut(
            Path.Combine(desktop, AppPaths.ProductDisplayName + ".lnk"),
            exePath,
            arguments: null,
            workDir,
            description: AppPaths.ProductDisplayName);
    }

    /// <summary>
    /// 卸载时清理：仅删除名为产品名的开始菜单文件夹，
    /// 以及桌面上「原神帧率解锁.lnk」。
    /// </summary>
    public static void RemoveCreatedShortcuts()
    {
        try
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                         Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var dir = Path.Combine(root, AppPaths.ProductName);
                // 叶名已由 ProductName 限定，避免误删其它开始菜单项
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) { AppLog.Warn("移除开始菜单: " + ex.Message); }

        try
        {
            foreach (var desk in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     })
            {
                if (string.IsNullOrEmpty(desk)) continue;
                var lnk = Path.Combine(desk, AppPaths.ProductDisplayName + ".lnk");
                if (File.Exists(lnk)) File.Delete(lnk);
            }
        }
        catch (Exception ex) { AppLog.Warn("移除桌面快捷方式: " + ex.Message); }
    }

    /// <summary>优先返回可写的公共开始菜单根；否则当前用户。</summary>
    private static string GetProgramsRoot()
    {
        try
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            if (!string.IsNullOrEmpty(common))
            {
                var testDir = Path.Combine(common, AppPaths.ProductName);
                Directory.CreateDirectory(testDir);
                return common;
            }
        }
        catch
        {
            // 无管理员权限写公共目录时回退
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    }

    /// <summary>创建单个 .lnk（IShellLinkW + IPersistFile）。</summary>
    private static void CreateShortcut(string lnkPath, string targetPath, string? arguments, string workDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetPath);
        if (!string.IsNullOrEmpty(arguments))
            link.SetArguments(arguments);
        link.SetWorkingDirectory(workDir);
        link.SetDescription(description);
        try { link.SetIconLocation(targetPath, 0); } catch { /* ignore */ }

        var file = (IPersistFile)link;
        file.Save(lnkPath, true);

        AppLog.Debug($"shortcut: {lnkPath} -> {targetPath} {arguments}");
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary>IShellLinkW 最小子集（Unicode）。</summary>
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
