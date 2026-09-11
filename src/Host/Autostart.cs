using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 开机自启动：读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 登录后以 --autostart --minimized 启动，直接进入托盘后台。
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名 = 产品名，避免与其它软件冲突。</summary>
    private const string ValueName = AppPaths.ProductName;

    /// <summary>当前是否已注册自启动项。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 开启或关闭自启动。
    /// 开启时命令行固定带 --autostart --minimized，路径加引号以支持空格/中文。
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                var exe = AppPaths.ExePath;
                // 登录启动时始终最小化到托盘
                key.SetValue(ValueName, $"\"{exe}\" --autostart --minimized");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表失败静默忽略（企业策略锁定等）
        }
    }

    /// <summary>移除自启动项（卸载时调用）。</summary>
    public static void Remove()
    {
        SetEnabled(false);
    }

    /// <summary>读取当前注册的命令行（调试用）。</summary>
    public static string? GetCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
