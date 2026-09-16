using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 一次性兼容清理：回收历史版本「超分辨率替换」功能写进游戏目录的代理组件。
/// 该功能已从本项目移除，这里只负责删除残留文件，不会写入任何内容。
///
/// 删除范围刻意收紧：只有能确认是本程序放进去的文件才会删 ——
///   - 部署标记文件存在时，清单里的同名文件都算本程序部署的；
///   - 标记文件不在（旧版本清理过一半、或用户手删过）时逐文件核对内容：
///     代理 DLL 里必须含 OptiScaler 标识、配置首行必须是本程序的生成标记、
///     日志正文里必须出现过 OptiScaler；对不上的一律不动，避免误删用户自己的
///     dxgi.dll（ReShade 等）或游戏自带的 DLSS 运行库。
///
/// 文件被运行中的游戏占用时返回 false，调用方下一轮继续重试。
/// </summary>
internal static class LegacyProxyCleanup
{
    /// <summary>上一版本的部署标记文件（K-V 文本，含已部署组件清单）。</summary>
    private const string DeployMarkerFileName = ".genshin-fps-unlocker-optiscaler";

    /// <summary>上一版本生成的 OptiScaler.ini 首行标记。</summary>
    private const string ConfigHeaderMarker = "; 由原神帧率解锁生成（OptiScaler Aurora）";

    /// <summary>文件内容里出现的组件标识（代理 DLL / 日志的身份依据）。</summary>
    private const string ComponentMarker = "OptiScaler";

    /// <summary>内容特征扫描的字节上限（超大文件不读进内存）。</summary>
    private const long MaximumScanBytes = 32L * 1024 * 1024;

    private static readonly string[] ProxyFileNames = ["dxgi.dll"];

    /// <summary>只有部署标记在时才删：没有标记时无法把游戏自带的同名运行库和被替换的那份区分开。</summary>
    private static readonly string[] PayloadFileNames =
        ["nvngx_dlss.dll", "nvngx_dlssnr.dll", "nvngx.dll_dlssnr.dll"];

    private static readonly string[] ConfigFileNames = ["OptiScaler.ini", "OptiScaler.log"];

    /// <summary>
    /// 尝试清理游戏目录里的残留组件；返回是否已经清理干净（没有残留或全部删掉）。
    /// 游戏路径为空 / 目录不存在时按「无需清理」处理。
    /// </summary>
    public static bool TryCleanup(string? gamePath)
    {
        string? gameDir;
        try
        {
            gameDir = Path.GetDirectoryName(gamePath ?? string.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"清理残留组件：解析游戏目录失败 — {ex.Message}");
            return true;
        }

        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return true;

        var markerPath = Path.Combine(gameDir, DeployMarkerFileName);
        var markerExists = File.Exists(markerPath);

        var removedAny = false;
        var allRemoved = true;
        foreach (var path in CollectTargets(gameDir, markerExists))
        {
            if (SafeDeleteFile(path)) removedAny = true;
            else allRemoved = false;
        }

        if (allRemoved && markerExists)
        {
            // 组件都清掉了才删标记：删一半就把标记抹掉，下一轮会失去身份依据。
            if (SafeDeleteFile(markerPath)) removedAny = true;
            else allRemoved = false;
        }

        if (removedAny)
            AppLog.Info($"已清理游戏目录里上一版本残留的替换组件：{gameDir}");
        else if (!allRemoved)
            AppLog.Debug($"残留组件暂未清理完（文件被占用，稍后重试）：{gameDir}");

        return allRemoved;
    }

    private static IEnumerable<string> CollectTargets(string gameDir, bool markerExists)
    {
        foreach (var name in ProxyFileNames)
        {
            var path = Path.Combine(gameDir, name);
            if (!File.Exists(path)) continue;
            if (markerExists || FileContainsMarker(path, ComponentMarker)) yield return path;
        }

        foreach (var name in PayloadFileNames)
        {
            if (!markerExists) continue;
            var path = Path.Combine(gameDir, name);
            if (File.Exists(path)) yield return path;
        }

        foreach (var name in ConfigFileNames)
        {
            var path = Path.Combine(gameDir, name);
            if (!File.Exists(path)) continue;
            if (markerExists || IsOurConfigFile(path, name)) yield return path;
        }
    }

    /// <summary>配置 / 日志的身份核对：配置认首行生成标记，日志认正文里的组件标识。</summary>
    private static bool IsOurConfigFile(string path, string fileName)
    {
        try
        {
            if (fileName.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
                return File.ReadLines(path).FirstOrDefault()
                    ?.Contains(ConfigHeaderMarker, StringComparison.Ordinal) == true;
            return FileContainsMarker(path, ComponentMarker);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"核对残留文件失败：{path} — {ex.Message}");
            return false;
        }
    }

    /// <summary>文件内容里是否出现给定 ASCII 标识。</summary>
    private static bool FileContainsMarker(string path, string marker)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaximumScanBytes) return false;
            var bytes = File.ReadAllBytes(path);
            return bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删除文件；返回是否「已经不存在」（本来就没有 / 删除成功）。</summary>
    private static bool SafeDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* 只读属性清不掉也继续试删 */ }
            File.Delete(path);
            return true;
        }
        catch
        {
            // 游戏运行时 DLL 会被占用，交给下一轮重试；已不存在的情况按成功算。
            return !File.Exists(path);
        }
    }
}
