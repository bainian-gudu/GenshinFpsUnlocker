using CommunityToolkit.WinUI.Notifications;

namespace GenshinFpsUnlocker.Host;

/// <summary>非打包 Win32 宿主使用的 Windows 现代 Toast 通知。</summary>
internal static class ToastNotifications
{
    public static bool TryShow(string title, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .Show();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Toast 通知不可用: " + ex.Message);
            return false;
        }
    }

}
