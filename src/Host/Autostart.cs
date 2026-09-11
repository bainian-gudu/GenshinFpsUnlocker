using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 开机自启动：读写 HKCU\...\Run（无需管理员、无 UAC）。
/// 登录后带 --autostart；是否进托盘跟随配置 StartMinimized。
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppPaths.ProductName;

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
    /// 命令行仅 --autostart（不加 --minimized），避免强制「开机后无界面」。
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
                key.SetValue(ValueName, $"\"{exe}\" --autostart");
                AppLog.Info("autostart enabled: " + key.GetValue(ValueName));
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Info("autostart disabled");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Autostart.SetEnabled: " + ex.Message);
        }
    }

    public static void Remove() => SetEnabled(false);

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
