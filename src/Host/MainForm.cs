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
    /// <summary>正在执行最小化→托盘，防止 Resize 重入导致闪烁/连弹。</summary>
    private bool _hidingToTray;

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

        try
        {
            BuildTray();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "BuildTray");
            // 托盘创建失败时仍保证有一个系统图标，避免「进程在但完全不可见」
            try
            {
                _tray = new NotifyIcon
                {
                    Visible = true,
                    Text = AppPaths.ProductDisplayName,
                    Icon = AppIcon.LoadClone() ?? SystemIcons.Application,
                    ContextMenuStrip = new ContextMenuStrip(),
                };
                _tray.ContextMenuStrip.Items.Add("显示主窗口", null, (_, _) => RestoreFromTrayPublic());
                _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => { _reallyExit = true; Close(); });
                _tray.DoubleClick += (_, _) => RestoreFromTrayPublic();
            }
            catch (Exception ex2)
            {
                AppLog.Error(ex2, "fallback tray");
            }
        }

        // 窗体句柄就绪后再强制刷新一次托盘可见性（部分环境构造阶段 Visible 会被吞）
        Shown += (_, _) =>
        {
            try
            {
                if (_tray is not null)
                {
                    _tray.Visible = false;
                    _tray.Visible = true;
                    UpdateTrayTip();
                }
                AppLog.Info($"MainForm shown; StartMinimized={_config.StartMinimized} trayVisible={_tray?.Visible}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Shown tray refresh: " + ex.Message);
            }
        };

        Load += async (_, _) =>
        {
            var webOk = false;
            try
            {
                await InitializeWebAsync();
                webOk = true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "WebView2 初始化失败");
                try { ShowNativeFallbackUi(ex.Message); } catch { /* ignore */ }
                try
                {
                    MessageBox.Show(
                        this,
                        "界面引擎（WebView2）初始化失败：\n" + ex.Message +
                        "\n\n已显示简易原生界面与系统托盘。\n" +
                        "请安装 Microsoft Edge WebView2 Runtime 后重开：\n" +
                        "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                        "也可右键托盘图标进行基本设置。",
                        Text,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                catch { /* ignore */ }
            }

            // 默认打开显示主窗口；仅当勾选「启动后最小化到托盘」且 Web UI 正常时才启动进托盘
            if (webOk && _config.StartMinimized)
            {
                BeginInvoke(() => HideToTrayPublic(showTip: true, fromStartup: true));
            }
            else if (!webOk)
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        // 强制前台，避免黑窗/无托盘
                        _config.StartMinimized = false;
                        RestoreFromTrayPublic();
                        if (_tray is not null)
                        {
                            _tray.Visible = true;
                            ShowTrayBalloon(
                                AppPaths.ProductDisplayName,
                                "界面加载异常，已保留窗口与托盘。右键托盘可调整设置。",
                                ToolTipIcon.Warning);
                        }
                    }
                    catch { /* ignore */ }
                });
            }
            else
            {
                // 明确保持窗口可见（覆盖历史配置误藏）
                BeginInvoke(() =>
                {
                    try
                    {
                        if (!Visible || WindowState == FormWindowState.Minimized)
                            RestoreFromTrayPublic();
                    }
                    catch { /* ignore */ }
                });
            }
        };

        // 标题栏最小化 → 托盘（始终）。优先 WndProc 拦截 SC_MINIMIZE，避免系统最小化动画闪烁；
        // Resize 仅作兜底（例如任务栏「最小化所有窗口」等路径）。
        Resize += (_, _) =>
        {
            if (_suppressResizeHide || _hidingToTray || _reallyExit || _inTray) return;
            if (WindowState == FormWindowState.Minimized)
                HideToTrayPublic(showTip: true, fromStartup: false);
        };

        // 关窗（×）：进托盘继续后台解锁；托盘「退出」才真正结束
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
    /// WebView2 / UI 资源失败时的简易原生界面，避免「黑窗 + 无托盘」完全失联。
    /// </summary>
    private void ShowNativeFallbackUi(string reason)
    {
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

    /// <summary>
    /// 隐藏到托盘：任务栏不占位；恢复前窗口状态保持 Normal，避免再次最小化异常。
    /// </summary>
    public void HideToTrayPublic(bool showTip = false, bool fromStartup = false)
    {
        if (_reallyExit || IsDisposed) return;
        // 已在托盘则不再走一遍（避免连点最小化多次闪/多次气泡逻辑）
        if (_inTray && !Visible && !ShowInTaskbar) return;

        void work()
        {
            if (_reallyExit || IsDisposed) return;
            if (_hidingToTray) return;
            if (_inTray && !Visible && !ShowInTaskbar) return;

            _hidingToTray = true;
            _suppressResizeHide = true;
            try
            {
                _inTray = true;

                // 先从任务栏摘掉，再隐藏。切勿在可见时 Minimized→Normal（会整窗弹回再消失 = 闪烁）。
                ShowInTaskbar = false;

                // 若已是最小化：保持最小化状态直接 Hide，不在屏幕上还原
                var wasMin = WindowState == FormWindowState.Minimized;
                try { Opacity = 0; } catch { /* ignore */ }

                Hide();

                // 已隐藏后再恢复 Normal，供下次 Show；用户看不到这一步
                if (wasMin || WindowState == FormWindowState.Minimized)
                {
                    try { WindowState = FormWindowState.Normal; } catch { /* ignore */ }
                }

                try { Opacity = 1; } catch { /* ignore */ }

                UpdateTrayTip();

                try
                {
                    if (_tray is not null)
                    {
                        _tray.Visible = true;
                        _tray.Text = BuildTrayTipText();
                    }
                }
                catch { /* ignore */ }

                if (showTip && !_trayTipShownThisSession)
                {
                    _trayTipShownThisSession = true;
                    ShowTrayBalloon(
                        AppPaths.ProductDisplayName,
                        fromStartup
                            ? "已在后台运行。若托盘区看不到图标，请点任务栏 ^ 展开「显示隐藏的图标」。左键打开主窗口，右键可设置。"
                            : "已在后台运行。左键单击或双击托盘图标可打开主窗口；右键可调整设置。",
                        ToolTipIcon.Info);
                }

                AppLog.Info(fromStartup ? "startup → tray" : "window → tray");
            }
            finally
            {
                _suppressResizeHide = false;
                _hidingToTray = false;
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
            if (_reallyExit || IsDisposed) return;
            _suppressResizeHide = true;
            _hidingToTray = false;
            try
            {
                _inTray = false;
                try { Opacity = 1; } catch { /* ignore */ }
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                ShowInTaskbar = true;
                Show();
                Activate();
                BringToFront();
                try { NativeActivate(); } catch { /* ignore */ }

                // 恢复后重新刷一次标题栏配色（部分 GPU/DWM 在 Hide 后会丢自定义 caption）
                try { UiStyle.ApplyTitleBarChrome(this, UiStyle.IsUiDark); } catch { /* ignore */ }

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

    /// <summary>Web UI 主题变化时同步窗体底色与 WebView 默认背景。</summary>
    public void ApplyWebChromeTheme(bool dark)
    {
        void work()
        {
            try
            {
                BackColor = dark ? UiStyle.UiDarkBg : UiStyle.UiLightBg;
                try
                {
                    _webView.DefaultBackgroundColor = dark ? UiStyle.UiDarkBg : UiStyle.UiLightBg;
                }
                catch { /* ignore */ }
                UiStyle.ApplyTitleBarChrome(this, dark);
            }
            catch (Exception ex) { AppLog.Debug("ApplyWebChromeTheme: " + ex.Message); }
        }
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(work);
        else work();
    }

    /// <summary>
    /// 以管理员身份重新启动（UAC 一次）。成功后本进程退出。
    /// 用于帧率解锁注入在非管理员下 OpenProcess 失败时的用户主动授权。
    /// </summary>
    public bool TryRestartElevated(out string error)
    {
        error = string.Empty;
        if (Elevation.IsAdministrator())
        {
            error = "当前已是管理员权限。";
            return false;
        }

        if (!Elevation.TryRestartElevatedForUnlock(out error))
            return false;

        // 提权实例已拉起：真正退出，不藏托盘
        _reallyExit = true;
        try
        {
            BeginInvoke(() =>
            {
                try { Close(); }
                catch { Environment.Exit(0); }
            });
        }
        catch
        {
            Environment.Exit(0);
        }
        return true;
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
