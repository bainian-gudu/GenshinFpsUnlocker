using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 主窗口：嵌入 WebView2 呈现设计稿 UI；系统托盘保留完整设置入口。
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

            if (_config.StartMinimized)
            {
                BeginInvoke(() =>
                {
                    WindowState = FormWindowState.Minimized;
                    HideToTrayPublic();
                });
            }
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTrayPublic();
        };

        FormClosing += (_, e) =>
        {
            if (!_reallyExit && e.CloseReason is CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTrayPublic();
                return;
            }

            _reallyExit = true;
            try { _tray.Visible = false; } catch { /* ignore */ }
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

        // 仅允许本地 UI 资源
        core.Profile.DefaultDownloadDialogCornerAlignment = CoreWebView2DefaultDownloadDialogCornerAlignment.TopRight;

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

        // 拦截外链：用系统浏览器打开
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
            // 开发：仓库 src/Ui/dist
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

    public void HideToTrayPublic()
    {
        Hide();
        ShowInTaskbar = false;
    }

    public void RestoreFromTrayPublic()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        SyncTrayFromConfig();
        if (_webReady) _bridge.PushState();
    }

    public void RequestExit()
    {
        _reallyExit = true;
        Close();
    }

    public void SyncTrayFromConfig()
    {
        if (IsDisposed) return;
        void work()
        {
            _syncingUi = true;
            try
            {
                if (_trayMasterItem is not null) _trayMasterItem.Checked = _config.MasterEnabled;
                if (_trayEnabledItem is not null) _trayEnabledItem.Checked = _config.Enabled;
                if (_trayAutoWatchItem is not null) _trayAutoWatchItem.Checked = _config.AutoWatch;
                if (_trayAutoStartItem is not null) _trayAutoStartItem.Checked = _config.AutoStartWithWindows;
                if (_trayLogItem is not null) _trayLogItem.Checked = _config.DebugLogging;
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
            _tray.Text = Truncate(
                $"FPS {_config.TargetFps} | {(_config.MasterEnabled ? "开" : "关")} | {_service.StatusText}",
                63);
        }
        catch { /* ignore */ }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
