using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GenshinFpsUnlocker.Host;

/// <summary>WebView2 承载：初始化、UI 目录解析、Web 不可用时的原生兜底界面与窗口配色。</summary>
internal sealed partial class MainForm : Form
{
    /// <summary>
    /// 延迟创建 WebView2。启动进托盘时不触碰 WebView2/运行时，避免其环境锁或磁盘
    /// 初始化阻塞 UI 消息泵；用户真正打开主窗口后才创建界面控件。
    /// </summary>
    private void InitializeWebControls()
    {
        if (_webView is not null && _webEnvironmentTask is not null)
            return;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            // 导航完成前保留原生加载层，完成后再显示 WebView2。
            Visible = false,
            // 透明背景交给窗口亚克力层；深色主题切换时会同步为不透明色。
            DefaultBackgroundColor = Color.FromArgb(0, UiStyle.UiLightBg),
        };
        _webEnvironmentTask = CreateWebEnvironmentAsync();
        Controls.Add(_webView);

        EnsureLoadingSurface();
    }

    /// <summary>创建冷启动加载层；普通启动提前创建，确保 WebView2 初始化期间文字可见。</summary>
    private void EnsureLoadingSurface()
    {
        _webReady = false;
        UiStyle.ApplyBackdrop(this, UiStyle.IsUiDark);
        if (_webLoadingSurface is not null)
        {
            // 复用加载层时恢复其可见性和层级。
            _webLoadingSurface.Visible = true;
            _webLoadingSurface.BringToFront();
            return;
        }
        _webLoadingSurface = new Panel
        {
            Dock = DockStyle.Fill,
            // 原生加载层使用实体底色，亚克力由 Web 页面就绪后接管。
            BackColor = UiStyle.IsUiDark ? UiStyle.UiDarkBg : UiStyle.UiLightBg,
        };
        _webLoadingText = new Label
        {
            Dock = DockStyle.Fill,
            Text = "正在加载界面…",
            ForeColor = UiStyle.IsUiDark ? UiStyle.UiDarkText : UiStyle.UiLightText,
            Font = UiStyle.UiFont,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _webLoadingSurface.Controls.Add(_webLoadingText);
        Controls.Add(_webLoadingSurface);
        _webLoadingSurface.BringToFront();
    }

    private async Task InitializeWebAsync()
    {
        // 环境创建已在窗体构造期间启动，这里只等待结果并在 UI 线程绑定控件。
        var env = await _webEnvironmentTask;
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        // DefaultDownloadDialog* 在 CoreWebView2 上（非 Profile）
        try
        {
            core.DefaultDownloadDialogCornerAlignment =
                CoreWebView2DefaultDownloadDialogCornerAlignment.TopRight;
        }
        catch (Exception ex)
        {
            AppLog.Debug("DefaultDownloadDialogCornerAlignment: " + ex.Message);
        }

        var uiDir = ResolveUiDirectory();
        if (uiDir is null)
            throw new DirectoryNotFoundException("未找到界面资源目录 ui/（请确认发布时已包含 Web UI 构建产物）");

        core.SetVirtualHostNameToFolderMapping(
            "app.local",
            uiDir,
            CoreWebView2HostResourceAccessKind.Allow);

        _bridge.Attach(_webView);

        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                AppLog.Warn($"Web UI 导航失败: {args.WebErrorStatus}");
                try { BeginInvoke(() => ShowNativeFallbackUi($"页面导航失败：{args.WebErrorStatus}")); }
                catch { /* 窗体销毁阶段忽略 */ }
                return;
            }
            // 先显示已经完成导航的 WebView2，再移除加载面板，避免出现黑窗闪烁。
            _webView.Visible = true;
            _webView.BringToFront();
            HideWebLoadingSurface();
            _webReady = true;
            UiStyle.ApplyBackdrop(this, UiStyle.IsUiDark);
            _bridge.PushState();
            AppLog.Info("Web UI ready");
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            try
            {
                var uri = e.Uri;
                if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = uri,
                        UseShellExecute = true,
                    });
                }
            }
            catch (Exception ex) { AppLog.Warn("open external: " + ex.Message); }
        };

        core.Navigate("https://app.local/index.html");
    }

    /// <summary>异步创建 WebView2 环境，供窗体构造阶段提前启动。</summary>
    private static async Task<CoreWebView2Environment> CreateWebEnvironmentAsync()
    {
        var userData = Path.Combine(AppPaths.DataDirectory, "webview2");
        PathUtil.EnsureDir(userData);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
    }

    private static string? ResolveUiDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppPaths.ExeDirectory, "ui"),
            Path.Combine(AppContext.BaseDirectory, "ui"),
            Path.Combine(AppPaths.ExeDirectory, "wwwroot"),
            Path.GetFullPath(Path.Combine(AppPaths.ExeDirectory, "..", "..", "..", "..", "src", "Ui", "dist")),
            Path.GetFullPath(Path.Combine(AppPaths.ExeDirectory, "..", "..", "..", "src", "Ui", "dist")),
        };
        foreach (var dir in candidates)
        {
            try
            {
                if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "index.html")))
                    return Path.GetFullPath(dir);
            }
            catch { /* ignore */ }
        }
        return null;
    }

    /// <summary>
    /// WebView2 / UI 资源失败时的简易原生界面，避免「黑窗 + 无托盘」完全失联。
    /// </summary>
    private void ShowNativeFallbackUi(string reason)
    {
        _webReady = false;
        UiStyle.ApplyBackdrop(this, UiStyle.IsUiDark);
        HideWebLoadingSurface();
        try { _webView.Visible = false; } catch { /* ignore */ }

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            BackColor = Color.FromArgb(0x12, 0x13, 0x19),
            ForeColor = Color.White,
        };

        var title = new Label
        {
            Text = "原神帧率解锁",
            AutoSize = true,
            Font = UiStyle.UiFontBold(6f),
            ForeColor = Color.FromArgb(0xBD, 0xA2, 0xF2),
            Location = new Point(8, 8),
        };

        var body = new Label
        {
            Text =
                "主界面未能加载（WebView2 或 ui 资源）。\n\n" +
                "原因：\n" + reason + "\n\n" +
                "请检查：\n" +
                "1. 已安装 Edge WebView2 Runtime\n" +
                "2. 安装目录下存在 ui\\index.html\n" +
                "3. 系统托盘（含 ^ 溢出区）是否有本程序图标\n\n" +
                "日志：%LocalAppData%\\GenshinFpsUnlocker\\logs\\\n" +
                "右键托盘仍可改帧率 / 开关 / 退出。",
            AutoSize = false,
            Size = new Size(900, 360),
            Location = new Point(8, 56),
            ForeColor = Color.FromArgb(220, 220, 230),
        };

        var btnLog = new Button
        {
            Text = "打开日志目录",
            Width = 140,
            Height = 36,
            Location = new Point(8, 430),
        };
        btnLog.Click += (_, _) => AppLog.OpenLogFolder();

        var btnTray = new Button
        {
            Text = "最小化到托盘",
            Width = 140,
            Height = 36,
            Location = new Point(160, 430),
        };
        btnTray.Click += (_, _) => HideToTrayPublic(showTip: true, fromStartup: false);

        var btnWeb = new Button
        {
            Text = "打开 WebView2 下载",
            Width = 180,
            Height = 36,
            Location = new Point(312, 430),
        };
        btnWeb.Click += (_, _) => RuntimePrerequisite.OpenUrl(RuntimePrerequisite.WebView2RuntimeUrl);

        panel.Controls.Add(title);
        panel.Controls.Add(body);
        panel.Controls.Add(btnLog);
        panel.Controls.Add(btnTray);
        panel.Controls.Add(btnWeb);
        Controls.Add(panel);
        panel.BringToFront();

        try
        {
            if (_tray is not null) _tray.Visible = true;
        }
        catch { /* ignore */ }

        AppLog.Warn("native fallback UI shown");
    }

    /// <summary>页面导航完成或切换到原生兜底界面后，移除启动加载层。</summary>
    private void HideWebLoadingSurface()
    {
        try
        {
            _webLoadingSurface.Visible = false;
            _webLoadingSurface.SendToBack();
        }
        catch { /* 窗体销毁阶段忽略 */ }
    }

    /// <summary>Web UI 主题变化时同步窗体底色与 WebView 默认背景。</summary>
    public void ApplyWebChromeTheme(bool dark)
    {
        void work()
        {
            try
            {
                // 顶层窗体保留可绘制底色，透明材质由窗口合成属性提供。
                BackColor = dark ? UiStyle.UiDarkBg : UiStyle.UiLightBg;
                try
                {
                    _webView.DefaultBackgroundColor = dark
                        ? UiStyle.UiDarkBg
                        : Color.FromArgb(0, UiStyle.UiLightBg);
                }
                catch { /* ignore */ }
                try
                {
                    _webLoadingSurface.BackColor = dark
                        ? UiStyle.UiDarkBg
                        : UiStyle.UiLightBg;
                    _webLoadingText.ForeColor = dark ? UiStyle.UiDarkText : UiStyle.UiLightText;
                }
                catch { /* ignore */ }
                UiStyle.ApplyTitleBarChrome(this, dark);
                UiStyle.ApplyBackdrop(this, dark);
                try { ApplyTrayMenuTheme(); } catch { /* ignore */ }
            }
            catch (Exception ex) { AppLog.Debug("ApplyWebChromeTheme: " + ex.Message); }
        }
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(work);
        else work();
    }
}
