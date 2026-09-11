namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 标准目录布局约定：
///   安装目录：C:\Program Files\GenshinFpsUnlocker\   （仅二进制，专用子目录，不污染 PF 根）
///   数据目录：%LocalAppData%\GenshinFpsUnlocker\     （config.json、logs）
/// 所有路径访问尽量经 <see cref="PathUtil"/> 规范化，兼容中文目录。
/// </summary>
internal static class AppPaths
{
    /// <summary>产品内部名（目录名、注册表键、互斥体等）。</summary>
    public const string ProductName = "GenshinFpsUnlocker";

    /// <summary>面向用户的显示名。</summary>
    public const string ProductDisplayName = "原神帧率解锁";

    /// <summary>发布者（ARP 显示）。</summary>
    public const string Publisher = "GenshinFpsUnlocker";

    /// <summary>控制面板“卸载程序”注册表键名。</summary>
    public const string UninstallRegKeyName = "GenshinFpsUnlocker";

    /// <summary>默认安装目录：%ProgramFiles%\GenshinFpsUnlocker。</summary>
    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    /// <summary>当前运行中可执行文件所在目录（规范化）。</summary>
    public static string ExeDirectory
    {
        get
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            try
            {
                return PathUtil.Normalize(Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory);
            }
            catch
            {
                return AppContext.BaseDirectory.TrimEnd('\\', '/');
            }
        }
    }

    /// <summary>当前进程 exe 完整路径。</summary>
    public static string ExePath =>
        PathUtil.Normalize(Environment.ProcessPath ?? Path.Combine(ExeDirectory, "GenshinFpsUnlocker.exe"));

    /// <summary>与 Host 同目录的注入 Stub DLL。</summary>
    public static string StubDllPath => Path.Combine(ExeDirectory, "FpsUnlockerStub.dll");

    /// <summary>安装目录下的卸载脚本垫片（双击即可卸载）。</summary>
    public static string UninstallCmdPath => Path.Combine(ExeDirectory, "Uninstall.cmd");

    /// <summary>
    /// 每用户可写数据目录（配置不得放在 Program Files 下，否则无管理员权限无法保存）。
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductName);
            PathUtil.EnsureDir(dir);
            return PathUtil.Normalize(dir);
        }
    }

    /// <summary>主配置文件路径：%LocalAppData%\GenshinFpsUnlocker\config.json。</summary>
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>日志目录：%LocalAppData%\GenshinFpsUnlocker\logs。</summary>
    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            PathUtil.EnsureDir(dir);
            return PathUtil.Normalize(dir);
        }
    }

    /// <summary>
    /// 旧版便携配置（exe 旁 config.json）。首次启动可迁移到 AppData。
    /// </summary>
    public static string? LegacyPortableConfigPath
    {
        get
        {
            try
            {
                var p = Path.Combine(ExeDirectory, "config.json");
                return PathUtil.ExistsFile(p) ? p : null;
            }
            catch { return null; }
        }
    }

    /// <summary>当前是否安装在 Program Files / Program Files (x86) 树下。</summary>
    public static bool IsInstalledUnderProgramFiles()
    {
        try
        {
            var pf = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            var pfx86 = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            var exeDir = ExeDirectory;
            return PathUtil.IsUnder(exeDir, pf)
                   || (!string.IsNullOrEmpty(pfx86) && PathUtil.IsUnder(exeDir, pfx86));
        }
        catch
        {
            return false;
        }
    }
}
