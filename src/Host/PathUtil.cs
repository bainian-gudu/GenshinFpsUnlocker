using System.Runtime.InteropServices;
using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 路径工具：统一规范化、Unicode/长路径、安全比较。
/// 兼容中文目录名；卸载/注入/定位游戏均应优先走本类，避免 Win32 ANSI 截断。
/// </summary>
internal static class PathUtil
{
    /// <summary>Windows 长路径前缀（&gt;=260 字符时）。</summary>
    private const string LongPathPrefix = @"\\?\";

    /// <summary>经典 MAX_PATH 上限（不含终止符）。</summary>
    private const int MaxPathLegacy = 260;

    /// <summary>
    /// 规范化路径：去引号、取绝对路径、去掉尾部分隔符与 \\?\ 前缀。
    /// 失败时尽量返回修剪后的原串，不抛异常。
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            // 日常比较/拼接使用无前缀形式，避免双重加前缀
            if (full.StartsWith(LongPathPrefix, StringComparison.Ordinal))
                full = full[LongPathPrefix.Length..];
            return full.TrimEnd('\\', '/');
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }

    /// <summary>
    /// 为易出问题的 Win32 API 生成带 \\?\ 的路径。
    /// 路径过长或含非 ASCII（如中文）时启用，提升 File/Directory 访问成功率。
    /// </summary>
    public static string ForWin32(string path)
    {
        var n = Normalize(path);
        if (string.IsNullOrEmpty(n)) return n;
        if (n.StartsWith(LongPathPrefix, StringComparison.Ordinal)) return n;
        // UNC 网络路径：\\server\share → \\?\UNC\server\share
        if (n.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + n.TrimStart('\\');
        if (n.Length >= MaxPathLegacy - 12 || ContainsNonAscii(n))
            return LongPathPrefix + n;
        return n;
    }

    /// <summary>判断文件是否存在（普通路径失败时再试长路径形式）。</summary>
    public static bool ExistsFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var n = Normalize(path);
            if (File.Exists(n)) return true;
            var lp = ForWin32(n);
            return lp != n && File.Exists(lp);
        }
        catch { return false; }
    }

    /// <summary>判断目录是否存在（同上，带长路径回退）。</summary>
    public static bool ExistsDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var n = Normalize(path);
            if (Directory.Exists(n)) return true;
            var lp = ForWin32(n);
            return lp != n && Directory.Exists(lp);
        }
        catch { return false; }
    }

    /// <summary>确保目录存在（幂等）。</summary>
    public static void EnsureDir(string path)
    {
        var n = Normalize(path);
        if (string.IsNullOrEmpty(n)) return;
        Directory.CreateDirectory(n);
    }

    /// <summary>路径等价比较（忽略大小写，先规范化）。</summary>
    public static bool EqualsPath(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断 child 是否位于 parent 目录树之下（含直接相等的子路径）。
    /// 通过追加目录分隔符避免 C:\Foo 误匹配 C:\FooBar。
    /// </summary>
    public static bool IsUnder(string? child, string? parent)
    {
        var c = Normalize(child) + Path.DirectorySeparatorChar;
        var p = Normalize(parent) + Path.DirectorySeparatorChar;
        if (string.IsNullOrEmpty(c) || string.IsNullOrEmpty(p)) return false;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>安全取父目录名；非法路径返回 null。</summary>
    public static string? GetDirectoryNameSafe(string? path)
    {
        try
        {
            var n = Normalize(path);
            return string.IsNullOrEmpty(n) ? null : Path.GetDirectoryName(n);
        }
        catch { return null; }
    }

    /// <summary>是否包含非 ASCII 字符（中文路径等）。</summary>
    public static bool ContainsNonAscii(string s)
    {
        foreach (var ch in s)
            if (ch > 127) return true;
        return false;
    }

    /// <summary>
    /// 查询进程完整映像路径（Unicode 安全）。
    /// 优先 QueryFullProcessImageName，避免 Process.MainModule 在无权限时抛异常。
    /// </summary>
    public static string? GetProcessImagePath(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (Native.QueryFullProcessImageName(h, 0, sb, ref size) && size > 0)
                return Normalize(sb.ToString());
            return null;
        }
        catch { return null; }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>
    /// 是否为“危险”路径：盘符根、系统目录、Program Files 根、用户配置根等。
    /// 卸载逻辑在删除前必须拒绝这些路径，防止误删无关数据。
    /// </summary>
    public static bool IsDangerousRootOrProfile(string? path)
    {
        var n = Normalize(path);
        if (string.IsNullOrEmpty(n)) return true;

        // 盘符根 "C:" / "C:\"
        if (n.Length <= 3)
        {
            if (n.Length >= 2 && n[1] == ':')
                return true;
        }

        var specials = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        foreach (var s in specials)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (EqualsPath(n, s)) return true;
        }

        return false;
    }
}
