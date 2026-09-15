namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 标准目录布局约定：
///   安装目录：C:\Program Files\GenshinFpsUnlocker\   （应用二进制，专用子目录）
///   数据目录：%LocalAppData%\GenshinFpsUnlocker\     （config.json、logs）
///   安装/卸载：只有 Kachina 一种（Install.exe + 安装目录 *.uninst.exe / *.update.exe）
///              宿主自身不提供任何安装/卸载入口，也不写 ARP 卸载注册表
///   运行库：安装器可装 .NET Desktop；运行时仍可检测；无 Node/Python 等语言依赖
/// 所有路径访问尽量经 <see cref="PathUtil"/> 规范化，兼容中文目录。
/// </summary>
internal static class AppPaths
{
    /// <summary>产品内部名（目录名、注册表键、互斥体等）。</summary>
    public const string ProductName = "GenshinFpsUnlocker";

    /// <summary>面向用户的显示名。</summary>
    public const string ProductDisplayName = "原神帧率解锁";

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
    /// <summary>注入模块的约定文件名（注入前的可信度校验要用）。</summary>
    public const string StubDllFileName = "FpsUnlockerStub.dll";

    public static string StubDllPath => Path.Combine(ExeDirectory, StubDllFileName);

    /// <summary>独立的超分辨率组件目录；包含 OptiScaler Aurora 和 DLSS 运行库源文件。</summary>
    public static string UpscalerDirectory => Path.Combine(ExeDirectory, "upscaler");

    /// <summary>OptiScaler Aurora 源文件（部署时复制为游戏目录的 dxgi.dll）。</summary>
    public static string UpscalerProxyPath => Path.Combine(UpscalerDirectory, "OptiScaler.dll");

    /// <summary>NVIDIA DLSS Super Resolution Runtime 源文件（部署时复制到游戏目录）。</summary>
    public static string DlssRuntimePath => Path.Combine(UpscalerDirectory, "nvngx_dlss.dll");

    /// <summary>NVIDIA DLSS Neural Rendering（DLSS 5）运行库源文件（部署时复制到游戏目录）。</summary>
    public static string DlssNrRuntimePath => Path.Combine(UpscalerDirectory, "nvngx_dlssnr.dll");

    /// <summary>OptiScaler DLSS Neural Rendering 转发器（部署时复制到游戏目录）。</summary>
    public static string DlssNrForwarderPath => Path.Combine(UpscalerDirectory, "nvngx.dll_dlssnr.dll");

    /// <summary>Kachina 写入的卸载程序（开始菜单「卸载」快捷方式指向它）。</summary>
    public static string UninstExePath => Path.Combine(ExeDirectory, ProductName + ".uninst.exe");

    /// <summary>Kachina 更新器（便携目录可选）。</summary>
    public static string UpdateExePath => Path.Combine(ExeDirectory, ProductName + ".update.exe");


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
