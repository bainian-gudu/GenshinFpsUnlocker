using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 开始菜单快捷方式。
/// 统一显示名为 <see cref="AppPaths.ProductDisplayName"/>，并清理 Kachina 等
/// 以内部名 <see cref="AppPaths.ProductName"/> 或历史中文名创建的重复项。
/// </summary>
internal static class ShortcutHelper
{
    /// <summary>
    /// 开始菜单：仅 HoYoEnhance 主项 + 卸载。
    /// 文件夹名仍用 ProductName（与安装目录/注册表一致）；.lnk 文件名用英文显示名。
    /// 位置策略：公共（所有用户）开始菜单若已有本产品目录即为规范位置——
    /// 即使当前进程（标准用户）无写权限，也不在用户开始菜单再建一份，
    /// 避免开始菜单出现两个一模一样条目。
    /// </summary>
    public static void CreateStartMenuShortcuts(string exePath, string workDir)
    {
        var (root, writable) = PickProgramsRoot();
        var dir = Path.Combine(root, AppPaths.ProductName);
        var otherDir = Path.Combine(OtherProgramsRoot(root), AppPaths.ProductName);

        // 另一侧（用户/公共）的历史重复项一律清理
        CleanupOtherStartMenuDir(otherDir);

        if (!writable)
        {
            AppLog.Info("公共开始菜单已有 " + AppPaths.ProductName
                        + "，当前进程无写权限 — 保留公共项，不写用户开始菜单");
            return;
        }

        Directory.CreateDirectory(dir);

        // 清同目录下内部名 / 历史中文名 / 错误命名
        TryDelete(Path.Combine(dir, AppPaths.ProductName + ".lnk"));
        TryDelete(Path.Combine(dir, AppPaths.ProductName + ".exe.lnk"));
        TryDelete(Path.Combine(dir, "卸载" + AppPaths.ProductName + ".lnk"));
        TryDelete(Path.Combine(dir, "卸载 " + AppPaths.ProductName + ".lnk"));
        TryDelete(Path.Combine(dir, "原神帧率解锁.lnk"));
        TryDelete(Path.Combine(dir, "卸载 原神帧率解锁.lnk"));
        TryDelete(Path.Combine(dir, "卸载 " + AppPaths.ProductDisplayName + ".lnk"));
        TryDelete(Path.Combine(dir, "打开日志目录.lnk"));

        var icon = ResolveIconPath(exePath, workDir);

        CreateShortcut(
            Path.Combine(dir, AppPaths.ProductDisplayName + ".lnk"),
            exePath,
            arguments: null,
            workDir,
            description: AppPaths.ProductDisplayName + " — 自定义 FPS · 后台注入",
            iconPath: icon);

        // 卸载快捷方式只指向 Kachina 安装器生成的 uninst.exe；
        // 便携 / 开发目录没有该文件时，顺手把历史残留的卸载快捷方式清掉。
        var uninstExe = AppPaths.UninstExePath;
        if (!File.Exists(uninstExe))
        {
            var legacy = Path.Combine(workDir, "Uninst.exe");
            uninstExe = File.Exists(legacy) ? legacy : null;
        }

        var uninstLnk = Path.Combine(dir, "Uninstall " + AppPaths.ProductDisplayName + ".lnk");
        if (uninstExe is not null)
        {
            CreateShortcut(
                uninstLnk,
                uninstExe,
                arguments: null,
                workDir,
                description: "Uninstall " + AppPaths.ProductDisplayName,
                iconPath: uninstExe);
        }
        else
        {
            TryDelete(uninstLnk);
            AppLog.Info("未找到 Kachina 卸载程序（便携/开发目录）— 不创建卸载快捷方式");
        }
    }

    /// <summary>
    /// 清理开始菜单中指向本 exe 的重复项，以及内部名 / 历史命名的快捷方式。
    /// </summary>
    public static void CleanupDuplicateShortcuts(string? exePath = null)
    {
        exePath ??= AppPaths.ExePath;
        var exeFull = PathUtil.Normalize(exePath);

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var dir = Path.Combine(root, AppPaths.ProductName);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    var leaf = Path.GetFileName(lnk);
                    // 保留规范名（主项与卸载项）
                    if (leaf.Equals(AppPaths.ProductDisplayName + ".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (leaf.Equals("Uninstall " + AppPaths.ProductDisplayName + ".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // 内部名 / 历史中文名 / 旧卸载 / 日志
                    if (leaf.Equals(AppPaths.ProductName + ".lnk", StringComparison.OrdinalIgnoreCase)
                        || leaf.Equals("原神帧率解锁.lnk", StringComparison.OrdinalIgnoreCase)
                        || leaf.StartsWith("卸载", StringComparison.OrdinalIgnoreCase)
                        || leaf.Contains("日志", StringComparison.Ordinal)
                        || ShortcutTargetsExe(lnk, exeFull))
                    {
                        // 旧卸载项也删，后面按英文名重建
                        TryDelete(lnk);
                        AppLog.Info("移除开始菜单重复项: " + leaf);
                    }
                }
            }
            catch (Exception ex) { AppLog.Debug("scan start menu lnk: " + ex.Message); }
        }
    }

    /// <summary>
    /// 清理桌面上的历史快捷方式（旧中文名 / 内部名）。
    /// 只删除文件名命中历史名单、且目标确实是本程序 exe 的 <c>.lnk</c>，不碰其他桌面文件。
    /// </summary>
    public static void CleanupLegacyDesktopShortcuts(string? exePath = null)
    {
        exePath ??= AppPaths.ExePath;
        var exeFull = PathUtil.Normalize(exePath);
        var legacyNames = new[]
        {
            "原神帧率解锁.lnk",
            "GenshinFpsUnlocker.lnk",
            "GenshinFpsUnlocker.exe.lnk",
            "Genshin FPS Unlocker.lnk",
        };

        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var name in legacyNames)
            {
                var lnk = Path.Combine(dir, name);
                try
                {
                    if (!File.Exists(lnk) || !ShortcutTargetsExe(lnk, exeFull)) continue;
                    File.Delete(lnk);
                    AppLog.Info("移除桌面历史快捷方式: " + name);
                }
                catch (Exception ex) { AppLog.Debug("delete desktop lnk " + name + ": " + ex.Message); }
            }
        }
    }

    /// <summary>优先 exe 旁 app.ico（完整多尺寸），否则 exe 自身。</summary>
    private static string ResolveIconPath(string exePath, string workDir)
    {
        foreach (var c in new[]
                 {
                     Path.Combine(workDir, "app.ico"),
                     Path.Combine(AppPaths.ExeDirectory, "app.ico"),
                     Path.Combine(workDir, "Assets", "app.ico"),
                     exePath,
                 })
        {
            try
            {
                if (File.Exists(c)) return PathUtil.Normalize(c);
            }
            catch { /* ignore */ }
        }
        return PathUtil.Normalize(exePath);
    }

    /// <summary>
    /// 开始菜单根目录选择：公共（所有用户）开始菜单已有本产品目录 → 视为规范位置
    /// （即便当前进程无写权限，也不在用户侧再建一份）；否则可写公共用公共，再退回用户侧。
    /// </summary>
    private static (string root, bool writable) PickProgramsRoot()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        if (!string.IsNullOrEmpty(common))
        {
            var commonDir = Path.Combine(common, AppPaths.ProductName);
            if (Directory.Exists(commonDir))
                return (common, CanWriteDir(commonDir));

            try
            {
                Directory.CreateDirectory(commonDir);
                return (common, true);
            }
            catch
            {
                // 无管理员权限写公共目录时回退
            }
        }

        return (user, true);
    }

    /// <summary>给定一侧开始菜单目录，返回另一侧（全体用户 ↔ 当前用户）。</summary>
    private static string OtherProgramsRoot(string root)
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        return string.Equals(root, common, StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            : common;
    }

    /// <summary>清理「另一侧」开始菜单目录（历史双写 / 内部名与历史名残留）。</summary>
    private static void CleanupOtherStartMenuDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var name in new[]
                 {
                     AppPaths.ProductName + ".lnk",
                     AppPaths.ProductName + ".exe.lnk",
                     AppPaths.ProductDisplayName + ".lnk",
                     "Uninstall " + AppPaths.ProductDisplayName + ".lnk",
                     "卸载 " + AppPaths.ProductDisplayName + ".lnk",
                     "原神帧率解锁.lnk",
                     "卸载 原神帧率解锁.lnk",
                     "打开日志目录.lnk",
                 })
        {
            try
            {
                if (File.Exists(Path.Combine(dir, name))) File.Delete(Path.Combine(dir, name));
            }
            catch (Exception ex) { AppLog.Debug("delete other start menu lnk " + name + ": " + ex.Message); }
        }
    }

    /// <summary>探测目录可写（写临时文件后删除）。</summary>
    private static bool CanWriteDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        try
        {
            var probe = Path.Combine(dir, ".gfu_write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删快捷方式，失败只记 Debug 日志：清理残留不该打断安装/卸载主流程。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Debug("delete lnk " + path + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 这个 <c>.lnk</c> 的目标是不是本程序的 exe。卸载时先确认再删，
    /// 同名但指向别处的快捷方式一律不碰。
    /// </summary>
    private static bool ShortcutTargetsExe(string lnkPath, string exeFull)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            var file = (IPersistFile)link;
            file.Load(lnkPath, 0);
            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = PathUtil.Normalize(sb.ToString());
            return !string.IsNullOrEmpty(target)
                   && target.Equals(exeFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用 <c>IShellLinkW</c> 写 <c>.lnk</c>（走 COM，不依赖 WScript.Shell）。</summary>
    private static void CreateShortcut(
        string lnkPath,
        string targetPath,
        string? arguments,
        string workDir,
        string description,
        string? iconPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetPath);
        if (!string.IsNullOrEmpty(arguments))
            link.SetArguments(arguments);
        link.SetWorkingDirectory(workDir);
        link.SetDescription(description);

        var icon = string.IsNullOrEmpty(iconPath) ? targetPath : iconPath;
        try { link.SetIconLocation(icon, 0); }
        catch
        {
            try { link.SetIconLocation(targetPath, 0); } catch { /* ignore */ }
        }

        var file = (IPersistFile)link;
        file.Save(lnkPath, true);

        AppLog.Debug($"shortcut: {lnkPath} -> {targetPath} icon={icon}");
    }

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
