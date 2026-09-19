namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 标准目录布局约定：
///   安装目录：打包配置指定的目录（兼容旧版升级时沿用原目录）
///   数据目录：用户数据目录                         （config.json、logs）
///   安装/卸载：只有 Kachina 一种（Install.exe + 安装目录 *.uninst.exe / *.update.exe）
///              宿主自身不提供任何安装/卸载入口，也不写 ARP 卸载注册表
///   运行库：安装器可装 .NET Desktop；运行时仍可检测；无 Node/Python 等语言依赖
/// 所有路径访问尽量经 <see cref="PathUtil"/> 规范化，兼容中文目录。
/// </summary>
internal static class AppPaths
{
    /// <summary>产品内部名（目录名、注册表键、互斥体等）。</summary>
    public const string ProductName = "GenshinFpsUnlocker";

    /// <summary>面向用户的显示名（品牌名，托盘提示、快捷方式、通知都用它）。</summary>
    public const string ProductDisplayName = "HoYoEnhance";

    /// <summary>窗口标题。</summary>
    public const string ProductTitle = ProductDisplayName;

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

    /// <summary>与 Host 同目录的注入 Stub DLL（注入前按这个文件名做可信度校验）。</summary>
    public const string StubDllFileName = "FpsUnlockerStub.dll";

    public static string StubDllPath => Path.Combine(ExeDirectory, StubDllFileName);

    /// <summary>指定游戏的注入模块路径（两款游戏的模块相互独立，文件名也不同）。</summary>
    public static string StubPathFor(GameDescriptor game) => Path.Combine(ExeDirectory, game.StubFileName);

    /// <summary>Kachina 写入的卸载程序（开始菜单「卸载」快捷方式指向它）。</summary>
    public static string UninstExePath => Path.Combine(ExeDirectory, ProductName + ".uninst.exe");



    private static string? _dataDirectory;
    private static readonly object DataDirLock = new();

    /// <summary>
    /// 每用户可写数据目录（配置不得放在 Program Files 下，否则无管理员权限无法保存）。
    /// 探测一次后缓存：LocalAppData → AppData → 文档。
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            if (_dataDirectory is not null) return _dataDirectory;
            lock (DataDirLock)
            {
                if (_dataDirectory is not null) return _dataDirectory;

                // 优先 LocalAppData（标准、本机持久化）
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ProductName),
                };
                Exception? last = null;
                foreach (var dir in candidates)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        PathUtil.EnsureDir(dir);
                        // 探测可写
                        var probe = Path.Combine(dir, ".write_test");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);
                        _dataDirectory = PathUtil.Normalize(dir);
                        return _dataDirectory;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
                // 最后回退：仍返回 LocalAppData 路径（调用方 Save 时再报错）
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductName);
                try { Directory.CreateDirectory(fallback); } catch { /* ignore */ }
                if (last is not null)
                    AppLog.Warn("DataDirectory writable probe failed: " + last.Message);
                _dataDirectory = PathUtil.Normalize(fallback);
                return _dataDirectory;
            }
        }
    }

    /// <summary>主配置文件路径：用户数据目录下的 config.json。</summary>
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>日志目录：用户数据目录下的 logs。</summary>
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
