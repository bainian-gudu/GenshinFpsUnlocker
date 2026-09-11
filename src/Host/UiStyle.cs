using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 界面跟随系统：字体用 SystemFonts，主题用 .NET 9 SystemColorMode + 系统色。
/// 深色/浅色切换时通过 UserPreferenceChanged 尽量刷新已打开窗体。
/// </summary>
internal static class UiStyle
{
    private static bool _hooked;

    /// <summary>在创建任何窗体之前调用。</summary>
    public static void ApplyApplicationTheme()
    {
        try
        {
            // .NET 9+：WinForms 控件随系统浅色/深色
            Application.SetColorMode(SystemColorMode.System);
        }
        catch (Exception ex)
        {
            AppLog.Debug("SetColorMode: " + ex.Message);
        }

        try
        {
            Application.SetDefaultFont(SystemFonts.MessageBoxFont);
        }
        catch (Exception ex)
        {
            AppLog.Debug("SetDefaultFont: " + ex.Message);
        }

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    /// <summary>正文/界面默认字体（跟随系统）。</summary>
    public static Font UiFont => SystemFonts.MessageBoxFont;

    /// <summary>加粗标题（基于系统字体族与尺寸）。</summary>
    public static Font UiFontBold(float sizeDelta = 1.5f)
    {
        var baseFont = SystemFonts.MessageBoxFont;
        var size = Math.Max(8f, baseFont.Size + sizeDelta);
        return new Font(baseFont.FontFamily, size, FontStyle.Bold, baseFont.Unit);
    }

    /// <summary>等宽字体（日志/文件列表）；不可用时回退系统 UI 字体。</summary>
    public static Font MonoFont(float size = 8.5f)
    {
        try
        {
            return new Font(FontFamily.GenericMonospace, size, FontStyle.Regular);
        }
        catch
        {
            return SystemFonts.MessageBoxFont;
        }
    }

    public static bool IsAppsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0;
            if (v is long l) return l == 0;
        }
        catch { /* ignore */ }
        return false;
    }

    /// <summary>次要说明文字色（随浅/深色）。</summary>
    public static Color SecondaryText =>
        IsAppsDarkMode()
            ? Color.FromArgb(180, 180, 180)
            : SystemColors.GrayText;

    /// <summary>安全提示条背景/前景（深浅自适应）。</summary>
    public static Color SafetyBannerBack =>
        IsAppsDarkMode()
            ? Color.FromArgb(64, 52, 20)
            : Color.FromArgb(255, 250, 230);

    public static Color SafetyBannerFore =>
        IsAppsDarkMode()
            ? Color.FromArgb(255, 210, 120)
            : Color.FromArgb(120, 80, 0);

    public static Color StatusOk =>
        IsAppsDarkMode() ? Color.FromArgb(120, 200, 140) : Color.DarkGreen;

    public static Color StatusWarn =>
        IsAppsDarkMode() ? Color.FromArgb(255, 180, 80) : Color.DarkOrange;

    public static Color StatusError =>
        IsAppsDarkMode() ? Color.FromArgb(255, 120, 120) : Color.DarkRed;

    /// <summary>窗体加载时：系统字体 + 标题栏深色模式。</summary>
    public static void ApplyToForm(Form form)
    {
        try
        {
            form.Font = UiFont;
        }
        catch { /* ignore */ }

        TrySetTitleBarDarkMode(form, IsAppsDarkMode());
        form.HandleCreated += (_, _) => TrySetTitleBarDarkMode(form, IsAppsDarkMode());
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color or UserPreferenceCategory.Window))
            return;

        try
        {
            // 重新声明跟随系统（用户改主题后）
            Application.SetColorMode(SystemColorMode.System);
        }
        catch { /* ignore */ }

        foreach (Form f in Application.OpenForms)
        {
            try
            {
                if (f.IsHandleCreated)
                    TrySetTitleBarDarkMode(f, IsAppsDarkMode());
                f.BeginInvoke(() =>
                {
                    try
                    {
                        f.Font = UiFont;
                        f.Invalidate(true);
                        f.Refresh();
                    }
                    catch { /* ignore */ }
                });
            }
            catch { /* ignore */ }
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>标题栏随系统深色（Win10 1809+ / Win11）。</summary>
    public static void TrySetTitleBarDarkMode(Form form, bool dark)
    {
        try
        {
            if (!form.IsHandleCreated) return;
            var v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref v, sizeof(int));
        }
        catch { /* ignore */ }
    }
}
