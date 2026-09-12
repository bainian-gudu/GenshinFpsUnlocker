using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 开始菜单 / 桌面快捷方式。
/// 统一显示名为 <see cref="AppPaths.ProductDisplayName"/>（中文），并清理 Kachina 等
/// 以英文 <see cref="AppPaths.ProductName"/> 创建的重复项，避免「一个英文无图标 + 一个中文有图标」。
/// </summary>
internal static class ShortcutHelper
{
    /// <summary>创建/刷新开始菜单 + 桌面；先清重复再写规范项。</summary>
    public static void CreateAll(string? exePath = null, string? workDir = null)
    {
        exePath ??= AppPaths.ExePath;
        workDir ??= AppPaths.ExeDirectory;

        try
        {
            CleanupDuplicateShortcuts(exePath);
        }
        catch (Exception ex)
        {
            AppLog.Warn("清理重复快捷方式: " + ex.Message);
        }

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
    /// 开始菜单：仅「原神帧率解锁」主项 + 卸载。
    /// 文件夹名仍用 ProductName（与安装目录/注册表一致）；.lnk 文件名为中文显示名。
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

        // 清同目录下英文主快捷方式 / 错误命名
        TryDelete(Path.Combine(dir, AppPaths.ProductName + ".lnk"));
        TryDelete(Path.Combine(dir, AppPaths.ProductName + ".exe.lnk"));
        TryDelete(Path.Combine(dir, "卸载" + AppPaths.ProductName + ".lnk"));
        TryDelete(Path.Combine(dir, "卸载 " + AppPaths.ProductName + ".lnk"));
        TryDelete(Path.Combine(dir, "打开日志目录.lnk"));

        var icon = ResolveIconPath(exePath, workDir);

        CreateShortcut(
            Path.Combine(dir, AppPaths.ProductDisplayName + ".lnk"),
            exePath,
            arguments: null,
            workDir,
            description: AppPaths.ProductDisplayName + " — 自定义 FPS · 后台注入",
            iconPath: icon);

        var uninstExe = AppPaths.UninstExePath;
        if (!File.Exists(uninstExe))
        {
            var legacy = Path.Combine(workDir, "Uninst.exe");
            if (File.Exists(legacy)) uninstExe = legacy;
            else uninstExe = null;
        }

        if (uninstExe is not null && File.Exists(uninstExe))
        {
            CreateShortcut(
                Path.Combine(dir, "卸载 " + AppPaths.ProductDisplayName + ".lnk"),
                uninstExe,
                arguments: null,
                workDir,
                description: "卸载 " + AppPaths.ProductDisplayName,
                iconPath: uninstExe);
        }
        else
        {
            CreateShortcut(
                Path.Combine(dir, "卸载 " + AppPaths.ProductDisplayName + ".lnk"),
                exePath,
                arguments: "--uninstall",
                workDir,
                description: "卸载 " + AppPaths.ProductDisplayName,
                iconPath: icon);
        }
    }

    /// <summary>
    /// 桌面仅保留一个中文主快捷方式（有图标）。
    /// 公共桌面（C:\Users\Public\Desktop，所有用户桌面可见）若已有本快捷方式，
    /// 即为规范位置——绝不在用户桌面再写第二份（历史上「公共+用户」双写
    /// 导致桌面出现两个一模一样图标，且每次启动都会重建）；
    /// 仅当公共桌面没有、且当前进程写不了公共桌面时，才写用户桌面。
    /// </summary>
    public static void CreateDesktopShortcut(string exePath, string workDir)
    {
        var icon = ResolveIconPath(exePath, workDir);
        const string desc = " — 自定义 FPS · 后台注入";

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // 两侧英文命名历史残留（Kachina 默认名等）一律清理
        CleanDesktopAliases(common);
        CleanDesktopAliases(user);

        var commonLnk = string.IsNullOrEmpty(common) ? null : Path.Combine(common, AppPaths.ProductDisplayName + ".lnk");
        var userLnk = string.IsNullOrEmpty(user) ? null : Path.Combine(user, AppPaths.ProductDisplayName + ".lnk");

        // 1) 公共桌面已有 → 规范位置：移除用户桌面第二份，管理员则刷新目标
        if (commonLnk is not null && PathUtil.ExistsFile(commonLnk))
        {
            if (userLnk is not null) TryDelete(userLnk);
            if (CanWriteDir(common))
            {
                CreateShortcut(commonLnk, exePath, null, workDir,
                    AppPaths.ProductDisplayName + desc, icon);
                AppLog.Info("桌面快捷方式（公共，规范）→ " + commonLnk);
            }
            else
            {
                AppLog.Debug("公共桌面快捷方式已存在，当前进程无写权限 — 保留，不写用户桌面");
            }
            return;
        }

        // 2) 公共桌面没有且可写（管理员）→ 写公共桌面，并移除用户桌面旧副本
        if (!string.IsNullOrEmpty(common) && Directory.Exists(common) && CanWriteDir(common))
        {
            CreateShortcut(commonLnk!, exePath, null, workDir,
                AppPaths.ProductDisplayName + desc, icon);
            if (userLnk is not null) TryDelete(userLnk);
            AppLog.Info("桌面快捷方式（公共）→ " + commonLnk);
            return;
        }

        // 3) 标准用户 → 只写用户桌面一份
        if (userLnk is not null)
        {
            CreateShortcut(userLnk, exePath, null, workDir,
                AppPaths.ProductDisplayName + desc, icon);
            AppLog.Info("桌面快捷方式（用户）→ " + userLnk);
            return;
        }

        throw new IOException("无法写入任何桌面目录的快捷方式");
    }

    private static void CleanDesktopAliases(string? desktop)
    {
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return;
        foreach (var name in new[]
                 {
                     AppPaths.ProductName + ".lnk",
                     AppPaths.ProductName + ".exe.lnk",
                     "Genshin FPS Unlocker.lnk",
                 })
            TryDelete(Path.Combine(desktop, name));
    }

    /// <summary>
    /// 卸载：删除产品开始菜单文件夹，以及桌面上中英文相关 .lnk。
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
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) { AppLog.Warn("移除开始菜单: " + ex.Message); }

        try
        {
            foreach (var desk in DesktopRoots())
            {
                if (string.IsNullOrEmpty(desk)) continue;
                foreach (var name in ShortcutNameAliases())
                    TryDelete(Path.Combine(desk, name));
            }
        }
        catch (Exception ex) { AppLog.Warn("移除桌面快捷方式: " + ex.Message); }
    }

    /// <summary>
    /// 清理桌面/开始菜单中指向本 exe 的重复项，以及英文命名的 Kachina 默认快捷方式。
    /// </summary>
    public static void CleanupDuplicateShortcuts(string? exePath = null)
    {
        exePath ??= AppPaths.ExePath;
        var exeFull = PathUtil.Normalize(exePath);

        foreach (var desk in DesktopRoots())
        {
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) continue;
            // 明确删英文名
            foreach (var name in new[]
                     {
                         AppPaths.ProductName + ".lnk",
                         AppPaths.ProductName + ".exe.lnk",
                         "Genshin FPS Unlocker.lnk",
                     })
                TryDelete(Path.Combine(desk, name));

            // 同目录其它 .lnk 若指向本 exe 且文件名不是中文标准名 → 删除
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(desk, "*.lnk"))
                {
                    var leaf = Path.GetFileName(lnk);
                    if (leaf.Equals(AppPaths.ProductDisplayName + ".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ShortcutTargetsExe(lnk, exeFull))
                    {
                        TryDelete(lnk);
                        AppLog.Info("移除重复桌面快捷方式: " + leaf);
                    }
                }
            }
            catch (Exception ex) { AppLog.Debug("scan desktop lnk: " + ex.Message); }
        }

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
                    // 保留规范中文名
                    if (leaf.Equals(AppPaths.ProductDisplayName + ".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (leaf.Equals("卸载 " + AppPaths.ProductDisplayName + ".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // 英文主项 / 旧卸载 / 日志
                    if (leaf.Equals(AppPaths.ProductName + ".lnk", StringComparison.OrdinalIgnoreCase)
                        || leaf.StartsWith("卸载", StringComparison.OrdinalIgnoreCase)
                        || leaf.Contains("日志", StringComparison.Ordinal)
                        || ShortcutTargetsExe(lnk, exeFull))
                    {
                        // 卸载项若是英文也删，后面会重建中文卸载
                        TryDelete(lnk);
                        AppLog.Info("移除开始菜单重复项: " + leaf);
                    }
                }
            }
            catch (Exception ex) { AppLog.Debug("scan start menu lnk: " + ex.Message); }
        }
    }

    private static IEnumerable<string> DesktopRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static IEnumerable<string> ShortcutNameAliases()
    {
        yield return AppPaths.ProductDisplayName + ".lnk";
        yield return AppPaths.ProductName + ".lnk";
        yield return AppPaths.ProductName + ".exe.lnk";
        yield return "Genshin FPS Unlocker.lnk";
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

    private static string OtherProgramsRoot(string root)
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        return string.Equals(root, common, StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            : common;
    }

    /// <summary>清理「另一侧」开始菜单目录（历史双写 / 英文命名残留）。</summary>
    private static void CleanupOtherStartMenuDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var name in new[]
                 {
                     AppPaths.ProductName + ".lnk",
                     AppPaths.ProductName + ".exe.lnk",
                     AppPaths.ProductDisplayName + ".lnk",
                     "卸载 " + AppPaths.ProductDisplayName + ".lnk",
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
