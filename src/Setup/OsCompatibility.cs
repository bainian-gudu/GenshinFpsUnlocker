namespace GenshinFpsUnlocker.Setup;

/// <summary>安装器侧 Windows 10/11 检测（与主程序规则一致）。</summary>
internal static class OsCompatibility
{
    public const int MinBuildNumber = 14393;

    public static bool MeetsMinimumOs(out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "非 Windows 系统";
            return false;
        }

        if (!Environment.Is64BitOperatingSystem)
        {
            detail = "需要 64 位 Windows（x64）";
            return false;
        }

        var v = Environment.OSVersion.Version;
        if (v.Major < 10 || (v.Major == 10 && v.Build < MinBuildNumber))
        {
            detail = $"当前 {FriendlyName()}（{v}），需要 Windows 10 1607+ 或 Windows 11";
            return false;
        }

        detail = $"{FriendlyName()}  build={v.Build}  x64";
        return true;
    }

    public static string FriendlyName()
    {
        var v = Environment.OSVersion.Version;
        if (v.Major >= 10 && v.Build >= 22000)
            return $"Windows 11 (10.0.{v.Build})";
        if (v.Major >= 10)
            return $"Windows 10 (10.0.{v.Build})";
        return $"Windows {v}";
    }
}
