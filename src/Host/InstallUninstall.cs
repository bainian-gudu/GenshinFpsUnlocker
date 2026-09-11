using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 安装与卸载。
/// 安装布局：C:\Program Files\GenshinFpsUnlocker\（专用子目录）。
/// 卸载采用多层白名单校验，绝不删除无关目录/文件。
/// </summary>
internal static partial class InstallUninstall
{

    /// <summary>控制面板卸载项注册表路径。</summary>
    private const string UninstallRegPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppPaths.UninstallRegKeyName;

    /// <summary>安装时写入的标记文件；删除安装目录前强烈依赖此标记。</summary>
    public const string InstallMarkerFileName = "GenshinFpsUnlocker.install";

    /// <summary>安装目录顶层允许删除的文件名白名单（含自包含发布常见依赖）。</summary>
    private static readonly HashSet<string> AllowedInstallFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "GenshinFpsUnlocker.exe",
        "GenshinFpsUnlocker.dll",
        "GenshinFpsUnlocker.deps.json",
        "GenshinFpsUnlocker.runtimeconfig.json",
        "GenshinFpsUnlocker.pdb",
        "FpsUnlockerStub.dll",
        "FpsUnlockerStub.pdb",
        "Uninstall.cmd",
        "Uninst.exe",
        "unins000.exe",
        "unins000.dat",
        InstallMarkerFileName,
        "config.example.json",
        "README.md",
        "LICENSE",
        "createdump.exe",
        // .NET 自包含发布常见宿主/运行时文件
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "clrjit.dll",
        "clrcompression.dll",
        "mscordaccore.dll",
        "mscordbi.dll",
        "System.IO.Compression.Native.dll",
        "clretwrc.dll",
    };

    private static readonly string[] AllowedInstallExtensions =
    [
        ".dll", ".exe", ".json", ".pdb", ".txt", ".md", ".cmd", ".ps1", ".config", ".xml", ".deps.json", ".install",
    ];

    /// <summary>粗判当前是否为已安装副本（有 exe + 标记或位于 PF 下）。</summary>
    public static bool IsLikelyInstalledCopy()
        => PathUtil.ExistsFile(Path.Combine(AppPaths.ExeDirectory, "GenshinFpsUnlocker.exe"))
           && (PathUtil.ExistsFile(Path.Combine(AppPaths.ExeDirectory, InstallMarkerFileName))
               || AppPaths.IsInstalledUnderProgramFiles());

    /// <summary>写入 UTF-8 BOM 安装标记（含版本与安装时间）。</summary>
    public static void WriteInstallMarker()
    {
        try
        {
            var path = Path.Combine(AppPaths.ExeDirectory, InstallMarkerFileName);
            var content =
                $"product={AppPaths.ProductName}\n" +
                $"version={GetVersionString()}\n" +
                $"installUtc={DateTime.UtcNow:O}\n" +
                $"exe={AppPaths.ExePath}\n";
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            AppLog.Info("install marker written: " + path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("write install marker: " + ex.Message);
        }
    }

    /// <summary>注册/刷新“应用和功能”卸载信息（ARP）。</summary>
    public static void RegisterUninstallInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(UninstallRegPath)
                            ?? Registry.CurrentUser.CreateSubKey(UninstallRegPath);
            if (key is null) return;

            var exe = AppPaths.ExePath;
            var uninst = FindExternalUninstaller();
            var uninstallCmd = uninst is not null
                ? $"\"{uninst}\""
                : $"\"{exe}\" --uninstall";
            var quietUninstall = uninst is not null
                ? $"\"{uninst}\" /S"
                : $"\"{exe}\" --uninstall --quiet";

            key.SetValue("DisplayName", AppPaths.ProductDisplayName);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("Publisher", AppPaths.Publisher);
            key.SetValue("InstallLocation", AppPaths.ExeDirectory);
            key.SetValue("UninstallString", uninstallCmd);
            key.SetValue("QuietUninstallString", quietUninstall);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", EstimateSizeKb(), RegistryValueKind.DWord);
            key.SetValue("DisplayVersion", GetVersionString());
            // 自定义产品 ID，卸载校验时可读
            key.SetValue("GFU_ProductId", AppPaths.ProductName);
        }
        catch (Exception ex)
        {
            AppLog.Warn("RegisterUninstallInfo: " + ex.Message);
        }
    }

    /// <summary>生成 Uninstall.cmd 垫片（UTF-8 BOM + chcp 65001，兼容中文路径）。</summary>
    public static void WriteUninstallCmdShim()
    {
        try
        {
            var cmd = AppPaths.UninstallCmdPath;
            var uninst = FindExternalUninstaller();
            string body;
            if (uninst is not null)
            {
                body =
                    "@echo off\r\n" +
                    "chcp 65001 >nul\r\n" +
                    "rem 启动安装器配套卸载程序\r\n" +
                    $"start \"\" \"{uninst}\"\r\n";
            }
            else
            {
                body =
                    "@echo off\r\n" +
                    "chcp 65001 >nul\r\n" +
                    "rem 由程序生成 — 启动内置卸载逻辑\r\n" +
                    $"\"{AppPaths.ExePath}\" --uninstall\r\n";
            }
            File.WriteAllText(cmd, body, new UTF8Encoding(true));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 安装器生成的卸载程序路径（MicaSetup: Uninst.exe）。
    /// 官方安装布局优先走外部卸载器；便携/开发目录回退内置安全清理。
    /// </summary>
    public static string? FindExternalUninstaller()
    {
        try
        {
            var dir = AppPaths.ExeDirectory;
            foreach (var name in new[] { "Uninst.exe", "uninst.exe", "Uninstall.exe" })
            {
                var p = Path.Combine(dir, name);
                if (PathUtil.ExistsFile(p))
                    return PathUtil.Normalize(p);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// UI / 托盘入口：优先启动安装目录 Uninst.exe；否则提权后走内置清理。
    /// </summary>
    public static void RunUninstallInteractive(bool quiet)
    {
        if (TryLaunchExternalUninstaller(quiet))
            return;

        if (!Elevation.IsAdministrator())
        {
            var args = quiet ? "--uninstall --quiet" : "--uninstall";
            if (!Elevation.EnsureAdminOrRelaunch(args, quiet, out var relaunched) && relaunched)
            {
                AppLog.Info("已拉起提权卸载实例，结束本进程");
                try { Application.Exit(); } catch { /* ignore */ }
                Environment.Exit(0);
                return;
            }
            if (!Elevation.IsAdministrator())
                AppLog.Warn("无管理员权限，继续有限卸载");
        }
        RunUninstall(quiet);
    }

    /// <summary>若存在 Uninst.exe 则启动并结束当前进程。</summary>
    public static bool TryLaunchExternalUninstaller(bool quiet)
    {
        var uninst = FindExternalUninstaller();
        if (uninst is null) return false;

        try
        {
            AppLog.Info("launch external uninstaller: " + uninst);
            // 先尽量清理本软件用户数据/自启（Uninst 主要负责安装目录 + ARP）
            try { Autostart.SetEnabled(false); } catch { /* ignore */ }
            try
            {
                if (TryGetSafeDataDirectory(out var data) && PathUtil.ExistsDir(data))
                {
                    AppLog.Info("pre-uninst data cleanup: " + data);
                    DeleteDirectorySafe(data);
                }
            }
            catch (Exception ex) { AppLog.Warn("pre-uninst data: " + ex.Message); }

            var psi = new ProcessStartInfo
            {
                FileName = uninst,
                WorkingDirectory = Path.GetDirectoryName(uninst) ?? AppPaths.ExeDirectory,
                UseShellExecute = true,
                Verb = "runas",
            };
            if (quiet)
                psi.Arguments = "/S";

            Process.Start(psi);
            try { Application.Exit(); } catch { /* ignore */ }
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("external uninstaller failed, fallback built-in: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 完整卸载：多层安全校验，绝不删除无关路径。
    /// 顺序：外部 Uninst（若有）→ 自启 → 用户数据 → 便携配置 → ARP → 快捷方式 → 安装目录。
    /// </summary>
    public static void RunUninstall(bool quiet)
    {
        // 安装器布局：优先外部卸载程序（与安装包配套）
        if (TryLaunchExternalUninstaller(quiet))
            return;

        if (!quiet)
        {
            var r = MessageBox.Show(
                "将卸载本软件并仅清理本软件相关内容：\n" +
                "• 安装目录（须通过安全校验的 GenshinFpsUnlocker 目录）\n" +
                "• 配置与日志（%LocalAppData%\\GenshinFpsUnlocker）\n" +
                "• 开机自启项与卸载注册表项\n" +
                "• 本软件创建的开始菜单与桌面快捷方式\n\n" +
                "不会删除其它软件或用户文件。\n\n是否继续？",
                "卸载 " + AppPaths.ProductDisplayName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }

        AppLog.Info("uninstall confirmed — starting safe cleanup (built-in)");

        // 1) 开机自启（仅删除本产品精确值名）
        try { Autostart.SetEnabled(false); AppLog.Info("autostart removed"); }
        catch (Exception ex) { AppLog.Warn("autostart: " + ex.Message); }

        // 2) 用户数据 — 仅允许 %LocalAppData%\GenshinFpsUnlocker
        try
        {
            if (TryGetSafeDataDirectory(out var data) && PathUtil.ExistsDir(data))
            {
                AppLog.Info("deleting data dir: " + data);
                DeleteDirectorySafe(data);
            }
            else
            {
                AppLog.Warn("data dir failed safety check — skipped");
            }
        }
        catch (Exception ex) { AppLog.Warn("data cleanup: " + ex.Message); }

        // 便携配置（exe 旁 config.json）仅在安装目录通过校验时删除
        try
        {
            if (TryValidateInstallDirectory(AppPaths.ExeDirectory, out var validatedInstall))
            {
                var portable = Path.Combine(validatedInstall, "config.json");
                if (PathUtil.ExistsFile(portable)) File.Delete(portable);
            }
        }
        catch { /* ignore */ }

        // 3) 卸载注册表（仅本产品键名）
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(UninstallRegPath, throwOnMissingSubKey: false);
            AppLog.Info("HKLM uninstall key removed");
        }
        catch (Exception ex) { AppLog.Warn("HKLM uninstall: " + ex.Message); }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegPath, throwOnMissingSubKey: false);
            AppLog.Info("HKCU uninstall key removed");
        }
        catch (Exception ex) { AppLog.Warn("HKCU uninstall: " + ex.Message); }

        // 4) 快捷方式 — 仅本产品名称相关项
        try { ShortcutHelper.RemoveCreatedShortcuts(); }
        catch (Exception ex) { AppLog.Warn("shortcuts: " + ex.Message); }

        // 5) 安装目录 — 严格校验 + 白名单擦除 + 退出后删除
        var installDir = AppPaths.ExeDirectory;
        var safeToDelete = TryValidateInstallDirectory(installDir, out var safeDir);

        if (safeToDelete)
        {
            AppLog.Info("install dir validated for delete: " + safeDir);
            try { DeleteKnownInstallFiles(safeDir); } catch (Exception ex) { AppLog.Warn("file wipe: " + ex.Message); }
            ScheduleDirectoryDeleteAfterExit(safeDir);
        }
        else
        {
            AppLog.Warn($"install dir NOT deleted (safety): {installDir}");
        }

        if (!quiet)
        {
            MessageBox.Show(
                safeToDelete
                    ? "卸载完成。\n已清理配置/日志/自启/快捷方式。\n安装目录将在程序退出后删除（已通过安全校验）。"
                    : "已清理配置、日志、自启与注册表。\n\n安装目录未通过安全校验，未自动删除，请手动确认后删除：\n" + installDir,
                "卸载完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        try { Application.Exit(); } catch { /* ignore */ }
        Environment.Exit(0);
    }


}
