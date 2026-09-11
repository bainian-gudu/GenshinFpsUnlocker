using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Setup;

/// <summary>安装器 UI：跟随系统字体与浅色/深色主题。</summary>
internal static class UiStyle
{
    private static bool _hooked;

    public static void ApplyApplicationTheme()
    {
        try { Application.SetColorMode(SystemColorMode.System); }
        catch { /* ignore */ }

        try { Application.SetDefaultFont(SystemFonts.MessageBoxFont); }
        catch { /* ignore */ }

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
                    or UserPreferenceCategory.Color or UserPreferenceCategory.Window))
                    return;
                try { Application.SetColorMode(SystemColorMode.System); } catch { /* ignore */ }
                foreach (Form f in Application.OpenForms)
                {
                    try
                    {
                        TrySetTitleBarDarkMode(f, IsAppsDarkMode());
                        f.BeginInvoke(() =>
                        {
                            try { f.Font = UiFont; f.Invalidate(true); f.Refresh(); }
                            catch { /* ignore */ }
                        });
                    }
                    catch { /* ignore */ }
                }
            };
        }
    }

    public static Font UiFont => SystemFonts.MessageBoxFont;

    public static Font UiFontBold(float sizeDelta = 1.5f)
    {
        var b = SystemFonts.MessageBoxFont;
        return new Font(b.FontFamily, Math.Max(8f, b.Size + sizeDelta), FontStyle.Bold, b.Unit);
    }

    public static Font MonoFont(float size = 8.5f)
    {
        try { return new Font(FontFamily.GenericMonospace, size, FontStyle.Regular); }
        catch { return SystemFonts.MessageBoxFont; }
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

    public static Color SecondaryText =>
        IsAppsDarkMode() ? Color.FromArgb(180, 180, 180) : SystemColors.GrayText;

    public static Color StatusOk =>
        IsAppsDarkMode() ? Color.FromArgb(120, 200, 140) : Color.DarkGreen;

    public static Color StatusWarn =>
        IsAppsDarkMode() ? Color.FromArgb(255, 180, 80) : Color.DarkOrange;

    public static Color StatusError =>
        IsAppsDarkMode() ? Color.FromArgb(255, 120, 120) : Color.DarkRed;

    public static Color Separator =>
        IsAppsDarkMode() ? Color.FromArgb(70, 70, 70) : Color.FromArgb(200, 200, 200);

    public static void ApplyToForm(Form form)
    {
        try { form.Font = UiFont; } catch { /* ignore */ }
        TrySetTitleBarDarkMode(form, IsAppsDarkMode());
        form.HandleCreated += (_, _) => TrySetTitleBarDarkMode(form, IsAppsDarkMode());
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

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
