using Microsoft.Web.WebView2.Core;
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

    private ToolStripMenuItem _trayMasterItem = null!;
    private ToolStripMenuItem _trayEnabledItem = null!;
    private ToolStripMenuItem _trayAutoWatchItem = null!;
    private ToolStripMenuItem _trayAutoStartItem = null!;
    private ToolStripMenuItem _trayFpsRoot = null!;
    private ToolStripMenuItem _trayLogItem = null!;

    private bool _reallyExit;
    private bool _syncingUi;
    private bool _webReady;
    private bool _suppressResizeHide;

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
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(0x12, 0x13, 0x19);
        UiStyle.ApplyToForm(this);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(0x12, 0x13, 0x19),
        };
        Controls.Add(_webView);

        BuildTray();

        Load += async (_, _) =>
        {
            try
            {
                await InitializeWebAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "WebView2 初始化失败");
                MessageBox.Show(
                    this,
                    "界面引擎初始化失败：\n" + ex.Message +
                    "\n\n请安装 Microsoft Edge WebView2 Runtime 后重试。\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            // 与 UI「启动后最小化 / 安静驻留托盘」一致
            if (_config.StartMinimized)
            {
                BeginInvoke(() =>
                {
                    HideToTrayPublic(showTip: false, fromStartup: true);
                });
            }
        };

        Resize += (_, _) =>
        {
            if (_suppressResizeHide || _reallyExit) return;
            if (WindowState == FormWindowState.Minimized)
                HideToTrayPublic(showTip: true, fromStartup: false);
        };

        FormClosing += (_, e) =>
        {
            if (!_reallyExit && e.CloseReason is CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTrayPublic(showTip: true, fromStartup: false);
                return;
            }

            _reallyExit = true;
            try { _service.StateChanged -= OnServiceStateForTray; } catch { /* ignore */ }
            try { _tray.Visible = false; } catch { /* ignore */ }
            try { _tray.Dispose(); } catch { /* ignore */ }
            try { _trayIconOwned?.Dispose(); } catch { /* ignore */ }
            try { _bridge.Dispose(); } catch { /* ignore */ }
            try { _webView.Dispose(); } catch { /* ignore */ }
        };
    }

    private async Task InitializeWebAsync()
    {
        var userData = Path.Combine(AppPaths.DataDirectory, "webview2");
        PathUtil.EnsureDir(userData);

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
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
            if (!args.IsSuccess) return;
            _webReady = true;
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
    /// 隐藏到托盘：任务栏不占位；恢复前窗口状态保持 Normal，避免再次最小化异常。
    /// </summary>
    public void HideToTrayPublic(bool showTip = false, bool fromStartup = false)
    {
        if (_reallyExit || IsDisposed) return;

        void work()
        {
            _suppressResizeHide = true;
            try
            {
                _inTray = true;
                ShowInTaskbar = false;
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Hide();

                UpdateTrayTip();

                // 用户主动关窗/最小化：给一次气泡；开机自启安静
                if (showTip && !fromStartup && !_trayTipShownThisSession)
                {
                    _trayTipShownThisSession = true;
                    ShowTrayBalloon(
                        AppPaths.ProductDisplayName,
                        "已在后台运行。左键单击或双击托盘图标可打开主窗口；右键可调整设置。",
                        ToolTipIcon.Info);
                }

                AppLog.Debug(fromStartup ? "startup → tray" : "window → tray");
            }
            finally
            {
                _suppressResizeHide = false;
            }
        }

        if (InvokeRequired) BeginInvoke(work);
        else work();
    }

    public void RestoreFromTrayPublic()
    {
        if (_reallyExit || IsDisposed) return;

        void work()
        {
            _suppressResizeHide = true;
            try
            {
                _inTray = false;
                Show();
                ShowInTaskbar = true;
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
                try { NativeActivate(); } catch { /* ignore */ }

                SyncTrayFromConfig();
                if (_webReady) _bridge.PushState();
                AppLog.Debug("tray → window");
            }
            finally
            {
                _suppressResizeHide = false;
            }
        }

        if (InvokeRequired) BeginInvoke(work);
        else work();
    }

    private void NativeActivate()
    {
        // 轻微置前，避免托盘恢复后窗口仍在后台
        var h = Handle;
        if (h == IntPtr.Zero) return;
        ShowWindow(h, 9); // SW_RESTORE
        SetForegroundWindow(h);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public void RequestExit()
    {
        _reallyExit = true;
        Close();
    }

    /// <summary>托盘菜单 / Web 改配置后：勾选、FPS 子菜单、提示全文与状态头对齐 UI。</summary>
    public void SyncTrayFromConfig()
    {
        if (IsDisposed) return;

        void work()
        {
            _syncingUi = true;
            try
            {
                if (_trayStatusItem is not null)
                    _trayStatusItem.Text = BuildStatusHeaderText();

                if (_trayMasterItem is not null) _trayMasterItem.Checked = _config.MasterEnabled;
                if (_trayEnabledItem is not null) _trayEnabledItem.Checked = _config.Enabled;
                if (_trayAutoWatchItem is not null) _trayAutoWatchItem.Checked = _config.AutoWatch;
                if (_trayAutoStartItem is not null) _trayAutoStartItem.Checked = _config.AutoStartWithWindows;
                if (_trayStartMinItem is not null) _trayStartMinItem.Checked = _config.StartMinimized;
                if (_trayLogItem is not null) _trayLogItem.Checked = _config.DebugLogging;

                // 总开关关闭时，帧率项视觉上仍可改，但状态头会提示暂停（与 UI 一致）
                if (_trayEnabledItem is not null)
                    _trayEnabledItem.Enabled = true;

                BuildTrayFpsItems();
                UpdateTrayTip();
            }
            finally { _syncingUi = false; }
        }

        if (InvokeRequired) BeginInvoke(work);
        else work();
    }

    private void UpdateTrayTip()
    {
        try
        {
            if (_tray is null) return;
            _tray.Text = BuildTrayTipText();
            if (_trayStatusItem is not null && !_syncingUi)
            {
                // 仅更新文案，不进 _syncingUi 全量路径时也刷新头
                _trayStatusItem.Text = BuildStatusHeaderText();
            }
        }
        catch { /* ignore */ }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
