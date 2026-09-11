using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// Windows 10 / Windows 11 兼容性检测与说明。
/// .NET 8 官方最低：Windows 10 1607 (10.0.14393) 及以上；本工具按 x64 桌面验证 Win10 + Win11。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class OsCompatibility
{
    /// <summary>Windows 10 1607 内部版本（.NET 8 常见下限）。</summary>
    public const int MinBuildNumber = 14393;

    /// <summary>是否为 64 位操作系统（游戏与 Stub 均为 x64）。</summary>
    public static bool Is64BitOs => Environment.Is64BitOperatingSystem;

    /// <summary>当前 OS 是否为 Windows。</summary>
    public static bool IsWindows =>
        OperatingSystem.IsWindows();

    /// <summary>
    /// 是否满足最低运行条件：Windows 10 build ≥ 14393（含 Windows 11，其主版本号仍为 10.0）。
    /// Windows 11 的 Environment.OSVersion 一般为 10.0.22000+。
    /// </summary>
    public static bool MeetsMinimumOs(out string detail)
    {
        if (!IsWindows)
        {
            detail = "非 Windows 系统";
            return false;
        }

        if (!Is64BitOs)
        {
            detail = "需要 64 位 Windows（x64）";
            return false;
        }

        // 确保拿到真实版本（清单已声明 supportedOS）
        var v = Environment.OSVersion.Version;
        var build = v.Build;
        // 某些环境 Version.Revision 才有 UBR；Build 对 Win10/11 足够
        if (v.Major < 10 || (v.Major == 10 && build < MinBuildNumber))
        {
            detail = $"当前 {GetFriendlyOsName()}（{v}），需要 Windows 10 版本 1607 或更高（含 Windows 11）";
            return false;
        }

        detail = $"{GetFriendlyOsName()}  build={build}  arch=x64";
        return true;
    }

    /// <summary>用户可读的系统名称（Win10 / Win11 粗分）。</summary>
    public static string GetFriendlyOsName()
    {
        if (!IsWindows) return Environment.OSVersion.ToString();

        var v = Environment.OSVersion.Version;
        // Windows 11 起 build ≥ 22000
        if (v.Major >= 10 && v.Build >= 22000)
            return $"Windows 11 (10.0.{v.Build})";
        if (v.Major >= 10)
            return $"Windows 10 (10.0.{v.Build})";
        return $"Windows {v}";
    }

    /// <summary>
    /// 启动时检查；不满足则提示。
    /// quiet/autostart 时不弹窗，仅记日志并返回 false。
    /// </summary>
    public static bool EnsureOrPrompt(bool quiet)
    {
        var ok = MeetsMinimumOs(out var detail);
        AppLog.Info($"OS check: ok={ok} {detail} Is64BitProcess={Environment.Is64BitProcess}");

        if (ok)
        {
            if (!Environment.Is64BitProcess)
            {
                AppLog.Warn("进程不是 64 位，注入可能失败");
                if (!quiet)
                {
                    MessageBox.Show(
                        "当前进程不是 64 位。请使用官方 x64 构建。\n\n" + detail,
                        AppPaths.ProductDisplayName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return false;
            }
            return true;
        }

        if (quiet)
        {
            AppLog.Error("OS 不满足最低要求: " + detail);
            return false;
        }

        var r = MessageBox.Show(
            "本软件需要：\n" +
            "• 64 位 Windows 10（1607 及以上）或 Windows 11\n\n" +
            "检测结果：\n" + detail + "\n\n" +
            "是否仍要继续尝试启动？（不保证可用）",
            AppPaths.ProductDisplayName + " — 系统要求",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        return r == DialogResult.Yes;
    }
}
