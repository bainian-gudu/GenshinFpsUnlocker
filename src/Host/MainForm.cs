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
                _tray.ContextMenuStrip.Items.Add("显示主界面", null, (_, _) => RestoreFromTrayPublic());
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

        // 任务栏/托盘激活时兜底：禁止残留 Opacity=0 的「幽灵窗」
        Activated += (_, _) =>
        {
            try
            {
                if (_reallyExit || _hidingToTray) return;
                if (Opacity < 0.99)
                {
                    Opacity = 1;
                    AppLog.Warn("Activated: forced Opacity=1 (was transparent)");
                }
                if (!Visible && !_inTray)
                {
                    Visible = true;
                }
            }
            catch { /* ignore */ }
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
    /// 隐藏到托盘：不占任务栏。禁止用 Opacity=0（恢复后易残留透明 → 任务栏有图标、桌面无窗）。
    /// </summary>
    public void HideToTrayPublic(bool showTip = false, bool fromStartup = false)
    {
        if (_reallyExit || IsDisposed) return;
        // 已在托盘则不再走一遍
        if (_inTray && !Visible) return;

        void work()
        {
            if (_reallyExit || IsDisposed) return;
            if (_hidingToTray) return;
            if (_inTray && !Visible) return;

            _hidingToTray = true;
            _suppressResizeHide = true;
            try
            {
                _inTray = true;

                // 保证不残留透明（历史路径 / 异常）
                try { Opacity = 1; } catch { /* ignore */ }

                // 先摘任务栏再 Hide，避免最小化动画闪烁
                ShowInTaskbar = false;
                Hide();

                // 隐藏后再把状态改回 Normal，下次 Show 直接正常窗（用户看不到）
                try
                {
                    if (WindowState != FormWindowState.Normal)
                        WindowState = FormWindowState.Normal;
                }
                catch { /* ignore */ }

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

    /// <summary>
    /// 从托盘恢复主窗口：强制可见、不透明、Normal、前台。
    /// </summary>
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

                // 1) 彻底取消透明 / 最小化残留
                try { Opacity = 1; } catch { /* ignore */ }
                try
                {
                    if (WindowState != FormWindowState.Normal)
                        WindowState = FormWindowState.Normal;
                }
                catch { /* ignore */ }

                // 2) 任务栏 + 显示
                ShowInTaskbar = true;
                if (!IsHandleCreated)
                {
                    try { _ = Handle; } catch { /* ignore */ }
                }
                Show();
                Visible = true;

                // 3) 再次确保状态（部分 shell 在 Show 后仍保持 Minimized）
                try
                {
                    if (WindowState != FormWindowState.Normal)
                        WindowState = FormWindowState.Normal;
                }
                catch { /* ignore */ }
                try { Opacity = 1; } catch { /* ignore */ }

                // 4) 尺寸/位置异常时回退到屏幕中央
                try { EnsureOnScreen(); } catch { /* ignore */ }

                Activate();
                BringToFront();
                try { NativeActivate(); } catch { /* ignore */ }

                try { UiStyle.ApplyTitleBarChrome(this, UiStyle.IsUiDark); } catch { /* ignore */ }

                SyncTrayFromConfig();
                if (_webReady) _bridge.PushState();
                AppLog.Info(
                    $"tray → window visible={Visible} state={WindowState} " +
                    $"opacity={Opacity:0.##} taskbar={ShowInTaskbar} bounds={Bounds}");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "RestoreFromTrayPublic");
                try
                {
                    Opacity = 1;
                    ShowInTaskbar = true;
                    WindowState = FormWindowState.Normal;
                    Show();
                    Visible = true;
                }
                catch { /* ignore */ }
            }
            finally
            {
                _suppressResizeHide = false;
            }
        }

        if (InvokeRequired) BeginInvoke(work);
        else work();
    }

    /// <summary>若窗口完全离开工作区，重置为居中正常大小。</summary>
    private void EnsureOnScreen()
    {
        var screen = Screen.FromControl(this) ?? Screen.PrimaryScreen;
        if (screen is null) return;
        var wa = screen.WorkingArea;
        // 完全在屏幕外，或宽高异常
        var on =
            Bounds.Right > wa.Left + 40 &&
            Bounds.Bottom > wa.Top + 40 &&
            Bounds.Left < wa.Right - 40 &&
            Bounds.Top < wa.Bottom - 40 &&
            Width >= MinimumSize.Width / 2 &&
            Height >= MinimumSize.Height / 2;
        if (on) return;

        Width = Math.Min(1180, wa.Width - 40);
        Height = Math.Min(760, wa.Height - 40);
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
        AppLog.Warn($"EnsureOnScreen reset bounds → {Bounds}");
    }

    private void NativeActivate()
    {
        var h = Handle;
        if (h == IntPtr.Zero) return;

        // SW_SHOWNA=8 / SW_RESTORE=9 / SW_SHOW=5
        ShowWindow(h, 5);  // SW_SHOW
        ShowWindow(h, 9);  // SW_RESTORE

        // 允许 SetForegroundWindow：短暂附着前台线程
        var fg = GetForegroundWindow();
        var fgTid = GetWindowThreadProcessId(fg, out _);
        var curTid = GetCurrentThreadId();
        var attached = false;
        if (fgTid != 0 && fgTid != curTid)
            attached = AttachThreadInput(fgTid, curTid, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attached)
                AttachThreadInput(fgTid, curTid, false);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

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
                try { ApplyTrayMenuTheme(); } catch { /* ignore */ }
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

                if (_trayEnabledItem is not null)
                {
                    _trayEnabledItem.Checked = _config.Enabled;
                    // 总开关关闭时仍允许改勾选，但状态头会提示暂停
                    _trayEnabledItem.Enabled = true;
                }
                if (_trayAutoWatchItem is not null)
                    _trayAutoWatchItem.Checked = _config.AutoWatch;

                if (_trayFpsRoot is not null)
                {
                    _trayFpsRoot.Text = $"修改帧率  ·  {_config.TargetFps} FPS";
                    foreach (ToolStripItem it in _trayFpsRoot.DropDownItems)
                    {
                        if (it is not ToolStripMenuItem mi) continue;
                        // "120 FPS  · 推荐" / "60 FPS"
                        var txt = mi.Text ?? "";
                        var numPart = txt.Split(' ')[0];
                        if (int.TryParse(numPart, out var fps))
                            mi.Checked = fps == _config.TargetFps;
                    }
                }
                UpdateTrayTip();
            }
            finally
            {
                _syncingUi = false;
            }
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
