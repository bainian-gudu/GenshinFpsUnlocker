using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 卸载路径安全校验（partial）：安装目录门禁、数据目录门禁、注册表匹配。
/// </summary>
internal static partial class InstallUninstall
{
    /// <summary>
    /// 删除安装目录前的多重条件门禁。
    /// 必须同时满足：非危险根、叶目录名=产品名、非 PF 根、存在 exe，
    /// 且（有安装标记 | 位于 PF 下且有 Stub | 注册表 InstallLocation 匹配且有 Stub）。
    /// </summary>
    public static bool TryValidateInstallDirectory(string? candidate, out string normalized)
    {
        normalized = PathUtil.Normalize(candidate);
        if (string.IsNullOrEmpty(normalized)) return false;

        // 拒绝盘符根、用户配置根、系统目录等
        if (PathUtil.IsDangerousRootOrProfile(normalized))
        {
            AppLog.Warn("reject dangerous path: " + normalized);
            return false;
        }

        // 叶目录名必须恰好为产品名
        var leaf = Path.GetFileName(normalized.TrimEnd('\\', '/'));
        if (!leaf.Equals(AppPaths.ProductName, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn($"reject leaf '{leaf}' != {AppPaths.ProductName}");
            return false;
        }

        // 不得是 Program Files 根目录本身
        var pf = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var pfx86 = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        if (PathUtil.EqualsPath(normalized, pf) || PathUtil.EqualsPath(normalized, pfx86))
            return false;

        var underPf = PathUtil.IsUnder(normalized, pf)
                      || (!string.IsNullOrEmpty(pfx86) && PathUtil.IsUnder(normalized, pfx86));

        var exe = Path.Combine(normalized, "GenshinFpsUnlocker.exe");
        var marker = Path.Combine(normalized, InstallMarkerFileName);
        var stub = Path.Combine(normalized, "FpsUnlockerStub.dll");

        var hasExe = PathUtil.ExistsFile(exe);
        var hasMarker = PathUtil.ExistsFile(marker);
        var hasStub = PathUtil.ExistsFile(stub);

        if (!hasExe)
        {
            AppLog.Warn("reject: missing GenshinFpsUnlocker.exe");
            return false;
        }

        // 要求：标记 或 (PF下+Stub) 或 (注册表匹配+Stub)
        var regMatch = MatchesRegisteredInstallLocation(normalized);
        if (!(hasMarker || (underPf && hasStub) || (regMatch && hasStub)))
        {
            AppLog.Warn($"reject: marker={hasMarker} underPf={underPf} stub={hasStub} regMatch={regMatch}");
            return false;
        }

        // 记录顶层子目录（本地化/runtimes 等）；未知子目录仅记日志，不因此直接拒绝
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(normalized))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.', StringComparison.Ordinal)) continue;
                if (name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("en", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("en-US", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("runtimes", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cs", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("de", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ja", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ko", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ru", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("fr", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("es", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("tr", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("it", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pl", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 本地化 / 运行时包
                }

                AppLog.Debug("install subdir noted: " + name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("enumerate install dir: " + ex.Message);
        }

        return true;
    }

    /// <summary>
    /// 校验数据目录必须精确等于 %LocalAppData%\GenshinFpsUnlocker。
    /// </summary>
    public static bool TryGetSafeDataDirectory(out string dataDir)
    {
        dataDir = PathUtil.Normalize(AppPaths.DataDirectory);
        var localApp = PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (string.IsNullOrEmpty(localApp) || string.IsNullOrEmpty(dataDir)) return false;

        // 必须精确匹配
        var expected = PathUtil.Normalize(Path.Combine(localApp, AppPaths.ProductName));
        if (!PathUtil.EqualsPath(dataDir, expected))
        {
            AppLog.Warn($"data dir mismatch: {dataDir} vs {expected}");
            return false;
        }

        if (PathUtil.IsDangerousRootOrProfile(dataDir)) return false;

        var leaf = Path.GetFileName(dataDir.TrimEnd('\\', '/'));
        return leaf.Equals(AppPaths.ProductName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ARP InstallLocation 是否与候选目录一致。</summary>
    private static bool MatchesRegisteredInstallLocation(string dir)
    {
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = root.OpenSubKey(UninstallRegPath);
                var loc = key?.GetValue("InstallLocation") as string;
                var product = key?.GetValue("GFU_ProductId") as string;
                if (!string.Equals(product, AppPaths.ProductName, StringComparison.OrdinalIgnoreCase)
                    && product is not null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(loc) && PathUtil.EqualsPath(loc, dir))
                    return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }


}
