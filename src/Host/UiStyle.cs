using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 界面跟随系统：字体用 SystemFonts；主窗标题栏跟随 Web UI 深/浅色（与 #121319 / #f5f5f8 一致）。
/// </summary>
internal static class UiStyle
{
    private static bool _hooked;

    /// <summary>当前主 UI 主题（由 WebView 同步；默认深色与设计稿一致）。</summary>
    private static bool _uiDark = true;

    /// <summary>在创建任何窗体之前调用。</summary>
    public static void ApplyApplicationTheme()
    {
        try
        {
#pragma warning disable WFO5001
            Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        }
        catch (Exception ex)
        {
            AppLog.Debug("SetColorMode: " + ex.Message);
        }

        try
        {
            var font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            if (font is not null)
                Application.SetDefaultFont(font);
        }
        catch (Exception ex)
        {
            AppLog.Debug("SetDefaultFont: " + ex.Message);
        }

        // 启动时先按系统 Apps 主题猜一次，Web UI 加载后会再同步
        _uiDark = IsAppsDarkMode();

        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    public static bool IsUiDark => _uiDark;

    /// <summary>正文/界面默认字体（跟随系统）。</summary>
    public static Font UiFont =>
        SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont ?? new Font(FontFamily.GenericSansSerif, 9f);

    /// <summary>加粗标题（基于系统字体族与尺寸）。</summary>
    public static Font UiFontBold(float sizeDelta = 1.5f)
    {
        var baseFont = UiFont;
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
            return UiFont;
        }
    }

    /// <summary>系统的「应用」深浅色（HKCU Themes\Personalize\AppsUseLightTheme），读不到按浅色算。</summary>
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

    /// <summary>设计稿深色背景 #121319。</summary>
    public static Color UiDarkBg => Color.FromArgb(0x12, 0x13, 0x19);

    /// <summary>设计稿浅色背景 #f5f5f8。</summary>
    public static Color UiLightBg => Color.FromArgb(0xF5, 0xF5, 0xF8);

    public static Color UiDarkText => Color.FromArgb(0xED, 0xEC, 0xF3);
    public static Color UiLightText => Color.FromArgb(0x1A, 0x1A, 0x22);
    public static Color UiDarkBorder => Color.FromArgb(0x27, 0x29, 0x34);
    public static Color UiLightBorder => Color.FromArgb(0xE4, 0xE2, 0xEC);

    /// <summary>窗体加载时：系统字体 + 标题栏配色。</summary>
    public static void ApplyToForm(Form form)
    {
        try
        {
            form.Font = UiFont;
        }
        catch { /* ignore */ }

        ApplyTitleBarChrome(form, _uiDark);
        form.HandleCreated += (_, _) => ApplyTitleBarChrome(form, _uiDark);
    }

    /// <summary>
    /// Web UI 切换深/浅后调用：同步所有窗体标题栏与主窗客户区底色。
    /// </summary>
    public static void SetUiTheme(bool dark)
    {
        _uiDark = dark;
        AppLog.Debug("UiStyle.SetUiTheme dark=" + dark);
        foreach (Form f in Application.OpenForms)
        {
            try
            {
                void apply()
                {
                    try
                    {
                        ApplyTitleBarChrome(f, dark);
                        // 主窗客户区与 WebView 默认底对齐，避免露白/露黑边
                        if (f is MainForm)
                        {
                            f.BackColor = dark ? UiDarkBg : UiLightBg;
                        }
                        f.Invalidate(true);
                    }
                    catch { /* ignore */ }
                }

                if (f.IsHandleCreated && f.InvokeRequired)
                    f.BeginInvoke(apply);
                else
                    apply();
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>系统主题/配色变化时重刷界面；其它类别的偏好变化直接忽略。</summary>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color or UserPreferenceCategory.Window))
            return;

        try
        {
#pragma warning disable WFO5001
            Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        }
        catch { /* ignore */ }

        // 系统主题变化时：若用户未强制，仍保持 Web 已选主题的标题栏（不覆盖 _uiDark）
        foreach (Form f in Application.OpenForms)
        {
            try
            {
                if (f.IsHandleCreated)
                    ApplyTitleBarChrome(f, _uiDark);
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
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>RGB → COLORREF (0x00BBGGRR)。</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>
    /// 让 DWM 给窗口做原生圆角（Win11 build 22000+ 的
    /// <c>DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND</c>）。
    /// 成功返回 true；Win10 上这个属性不存在，返回 false 让调用方走兜底
    /// （托盘菜单用 Region 裁角，见 <see cref="TrayMenuCorners"/>）。
    /// 对弹出式窗口（ContextMenuStrip / 其子菜单 DropDown）同样有效。
    /// </summary>
    public static bool TryApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            // 自己判版本而不是调 OsCompatibility：那边带 [SupportedOSPlatform("windows")]，
            // 本类没有标注，跨过去会招 CA1416。Environment.OSVersion 是跨平台 API。
            var v = Environment.OSVersion.Version;
            if (v.Major < 10 || v.Build < 22000) return false;
            var pref = DWMWCP_ROUND;
            return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 标题栏配色与设计稿一致：深色 #121319 / 浅色 #f5f5f8（Win11 caption/text/border；旧系统 immersive dark）。
    /// </summary>
    public static void ApplyTitleBarChrome(Form form, bool dark)
    {
        try
        {
            if (!form.IsHandleCreated) return;
            var hwnd = form.Handle;

            var immersive = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref immersive, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref immersive, sizeof(int));

            var caption = ToColorRef(dark ? UiDarkBg : UiLightBg);
            var text = ToColorRef(dark ? UiDarkText : UiLightText);
            var border = ToColorRef(dark ? UiDarkBorder : UiLightBorder);
            // Win11 22H2+：自定义标题栏颜色，与 UI topbar 一致
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch { /* ignore */ }
    }

    /// <summary>兼容旧调用名。</summary>
    public static void TrySetTitleBarDarkMode(Form form, bool dark) =>
        ApplyTitleBarChrome(form, dark);
}
