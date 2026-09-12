using Microsoft.Web.WebView2.WinForms;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 主窗口：嵌入 WebView2 呈现设计稿 UI；关闭/最小化 → 托盘，与 UI「启动后最小化」一致。
/// </summary>
internal sealed partial class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly UnlockService _service;
    private readonly UiBridge _bridge;
    private readonly WebView2 _webView;
    private NotifyIcon _tray = null!;

    private bool _reallyExit;
    private bool _syncingUi;
    private bool _webReady;
    private bool _suppressResizeHide;
    /// <summary>正在执行最小化→托盘，防止 Resize 重入导致闪烁/连弹。</summary>
    private bool _hidingToTray;
    /// <summary>
    /// 启动时若「最小化到托盘」：在 Web 就绪前拦截 Show，避免主窗闪几秒再消失。
    /// </summary>
    private bool _allowVisible = true;
    /// <summary>仍处于「启动进托盘」阶段（尚未完成首次入托盘）。</summary>
    private bool _startupTrayPending;
    /// <summary>
    /// 「启动进托盘」本进程只允许发生一次。
    /// 之后用户主动打开的主窗（托盘图标 / 二次点快捷方式唤醒 / UI showWindow）
    /// 绝不能再被任何延迟到达的兜底调用藏回托盘。
    /// </summary>
    private bool _startupTrayDone;
    /// <summary>启动托盘时暂存正常位置，恢复时用。</summary>
    private Point _restoreLocation;
    private bool _hasRestoreLocation;
    private CancellationTokenSource? _wakeCts;

    public MainForm(AppConfig config, UnlockService service)
    {
        _config = config;
        _service = service;
        _bridge = new UiBridge(config, service, this);

        Text = "原神帧率解锁 · Genshin FPS Unlocker";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        BackColor = UiStyle.IsUiDark ? UiStyle.UiDarkBg : UiStyle.UiLightBg;

        // 启动进托盘：多管齐下防止「闪几秒再消失」
        // 1) SetVisibleCore 拒绝显示  2) 屏外+透明  3) TOOLWINDOW/NOACTIVATE
        // 4) 不等 Web 就绪，构造末尾即 FinishStartupToTray
        if (_config.StartMinimized)
        {
            _allowVisible = false;
            _startupTrayPending = true;
            _inTray = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            try { Opacity = 0; } catch { /* ignore */ }
        }
        try
        {
            var ico = AppIcon.LoadClone();
            if (ico is not null) Icon = ico;
        }
        catch { /* ignore */ }
        UiStyle.ApplyToForm(this);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(0x12, 0x13, 0x19),
        };
        Controls.Add(_webView);

        WireTrayFallback();

        WireStartupToTray();

        WireInstanceWake();

        WireLoadHandler();

        WireWindowEvents();
    }

    public void RequestExit()
    {
        _reallyExit = true;
        Close();
    }

}
