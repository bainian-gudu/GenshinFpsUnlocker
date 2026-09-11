namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 主窗口 + 系统托盘。
/// - 主界面：目标 FPS、总开关/功能开关、监视、自启、日志、游戏路径、安全声明、卸载
/// - 托盘：最小化后可完成几乎全部设置；关闭窗口默认隐藏到托盘而非退出
/// </summary>
internal sealed partial class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly UnlockService _service;
    private readonly NotifyIcon _tray;

    private readonly NumericUpDown _fpsInput;
    private readonly CheckBox _masterBox;
    private readonly CheckBox _enabledBox;
    private readonly CheckBox _autoWatchBox;
    private readonly CheckBox _startMinBox;
    private readonly CheckBox _autoStartBox;
    private readonly CheckBox _logBox;
    private readonly CheckBox _desktopShortcutBox;
    private readonly TextBox _gamePathBox;
    private readonly Label _statusLabel;
    private readonly Label _pathStatusLabel;
    private readonly Label _safetyLabel;
    private readonly System.Windows.Forms.Timer _uiTimer;

    // ---- 托盘快捷设置项 ----
    private ToolStripMenuItem _trayMasterItem = null!;
    private ToolStripMenuItem _trayEnabledItem = null!;
    private ToolStripMenuItem _trayAutoWatchItem = null!;
    private ToolStripMenuItem _trayAutoStartItem = null!;
    private ToolStripMenuItem _trayFpsRoot = null!;
    private ToolStripMenuItem _trayLogItem = null!;

    /// <summary>为 true 时 FormClosing 才真正退出（否则隐藏到托盘）。</summary>
    private bool _reallyExit;
    /// <summary>同步主界面与托盘勾选时置位，防止 CheckedChanged 递归。</summary>
    private bool _syncingUi;
    private System.Windows.Forms.Timer? _fpsSaveTimer;

    public MainForm(AppConfig config, UnlockService service)
    {
        _config = config;
        _service = service;

        Text = "原神帧率解锁 · Genshin FPS Unlocker";
        Width = 560;
        Height = 600;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        UiStyle.ApplyToForm(this);
        ShowInTaskbar = true;
        MinimumSize = new Size(520, 520);

        var title = new Label
        {
            Text = "仅帧率解锁 · 自定义 FPS · 检测游戏后自动后台运行",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            Font = UiStyle.UiFontBold(1.5f),
        };

        _safetyLabel = new Label
        {
            Text = "⚠ " + SafetyNotice.ShortSummary,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 4, 12, 4),
            BackColor = UiStyle.SafetyBannerBack,
            ForeColor = UiStyle.SafetyBannerFore,
            Cursor = Cursors.Hand,
        };
        _safetyLabel.Click += (_, _) => ShowSafetyDialog(force: true);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 13,
            Padding = new Padding(14, 8, 14, 8),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        for (var r = 0; r < 13; r++)
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---- 行 0：目标 FPS + 预设按钮 ----
        panel.Controls.Add(MakeLabel("目标帧率"), 0, 0);
        _fpsInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 540,
            Value = Math.Clamp(config.TargetFps, 1, 540),
            Width = 100,
            Anchor = AnchorStyles.Left,
        };
        _fpsInput.ValueChanged += (_, _) =>
        {
            if (_syncingUi) return;
            // 实时推送 IPC；落盘防抖，避免滚轮连发时频繁原子写盘
            _config.TargetFps = (int)_fpsInput.Value;
            _service.PushConfigToIpc(force: true);
            BuildTrayFpsItems();
            UpdateStatusUi();
            ScheduleFpsSave();
        };
        var fpsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left };
        fpsRow.Controls.Add(_fpsInput);
        foreach (var preset in new[] { 60, 90, 120, 144, 165, 240 })
        {
            var p = preset;
            var b = new Button { Text = p.ToString(), Width = 44, Height = 26, Margin = new Padding(4, 0, 0, 0) };
            b.Click += (_, _) => { _fpsInput.Value = p; PersistAll(showTip: false); };
            fpsRow.Controls.Add(b);
        }
        panel.SetColumnSpan(fpsRow, 2);
        panel.Controls.Add(fpsRow, 1, 0);

        // ---- 行 1：总开关 ----
        _masterBox = new CheckBox
        {
            Text = "总开关（关闭后不注入、不强制帧率，可后台待命）",
            Checked = config.MasterEnabled,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        panel.SetColumnSpan(_masterBox, 2);
        panel.Controls.Add(_masterBox, 1, 1);

        // ---- 行 2：帧率解锁功能开关 ----
        _enabledBox = new CheckBox
        {
            Text = "启用帧率解锁",
            Checked = config.Enabled,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        panel.SetColumnSpan(_enabledBox, 2);
        panel.Controls.Add(_enabledBox, 1, 2);

        // ---- 行 3：自动监视 ----
        _autoWatchBox = new CheckBox
        {
            Text = "检测游戏启动后自动注入（后台监视）",
            Checked = config.AutoWatch,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        panel.SetColumnSpan(_autoWatchBox, 2);
        panel.Controls.Add(_autoWatchBox, 1, 3);

        // ---- 行 4：启动最小化 ----
        _startMinBox = new CheckBox
        {
            Text = "启动后最小化到托盘",
            Checked = config.StartMinimized,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        panel.SetColumnSpan(_startMinBox, 2);
        panel.Controls.Add(_startMinBox, 1, 4);

        // ---- 行 5：开机自启 ----
        _autoStartBox = new CheckBox
        {
            Text = "开机自启动（登录后托盘后台运行）",
            Checked = config.AutoStartWithWindows,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        panel.SetColumnSpan(_autoStartBox, 2);
        panel.Controls.Add(_autoStartBox, 1, 5);

        _logBox = new CheckBox
        {
            Text = "调试日志（默认开启）",
            Checked = config.DebugLogging,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        _desktopShortcutBox = new CheckBox
        {
            Text = "维护桌面快捷方式",
            Checked = config.CreateDesktopShortcut,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(16, 0, 0, 0),
        };
        var logRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left };
        logRow.Controls.Add(_logBox);
        logRow.Controls.Add(_desktopShortcutBox);
        var openLogBtn = new Button { Text = "打开日志", Width = 80, Height = 26, Margin = new Padding(8, 0, 0, 0) };
        openLogBtn.Click += (_, _) => AppLog.OpenLogFolder();
        logRow.Controls.Add(openLogBtn);
        panel.SetColumnSpan(logRow, 2);
        panel.Controls.Add(logRow, 1, 6);

        // ---- 行 7：游戏路径 ----
        panel.Controls.Add(MakeLabel("游戏路径"), 0, 7);
        _gamePathBox = new TextBox
        {
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Text = config.GamePath ?? "",
        };
        panel.Controls.Add(_gamePathBox, 1, 7);

        var pathButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
        };
        var browseBtn = new Button { Text = "手动选择…", Width = 90, Height = 28 };
        browseBtn.Click += (_, _) =>
        {
            var r = _service.SetGamePathManual(this);
            if (r.Ok)
            {
                _gamePathBox.Text = r.Path ?? "";
                PersistAll(showTip: false);
            }
            else if (!string.IsNullOrEmpty(r.Detail) && r.Detail != "已取消手动选择")
            {
                MessageBox.Show(r.Detail, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var autoBtn = new Button { Text = "自动查找", Width = 90, Height = 28 };
        autoBtn.Click += (_, _) =>
        {
            var r = _service.AutoLocateGamePath();
            _gamePathBox.Text = _config.GamePath ?? "";
            if (r.Ok)
            {
                MessageBox.Show(
                    $"已找到游戏：\n{r.Path}\n来源：{GameLocator.SourceDisplayName(r.Source)}",
                    "自动查找",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                PersistAll(showTip: false);
            }
            else
            {
                MessageBox.Show(r.Detail ?? "未找到", "自动查找", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        pathButtons.Controls.Add(browseBtn);
        pathButtons.Controls.Add(autoBtn);
        panel.Controls.Add(pathButtons, 2, 7);

        // ---- 行 8：操作按钮 ----
        var launchBtn = new Button { Text = "启动游戏", Width = 100, Height = 30, Anchor = AnchorStyles.Left };
        launchBtn.Click += (_, _) =>
        {
            PersistAll(showTip: false);
            if (_service.TryLaunchGame(out var msg))
                _tray.ShowBalloonTip(2000, "启动游戏", msg, ToolTipIcon.Info);
            else
                MessageBox.Show(msg, "启动游戏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        var applyBtn = new Button { Text = "应用并保存", Width = 100, Height = 30, Anchor = AnchorStyles.Left };
        applyBtn.Click += (_, _) => PersistAll(showTip: true);
        var safetyBtn = new Button { Text = "安全声明", Width = 100, Height = 30, Anchor = AnchorStyles.Left };
        safetyBtn.Click += (_, _) => ShowSafetyDialog(force: true);
        var uninstallBtn = new Button { Text = "卸载清理", Width = 100, Height = 30, Anchor = AnchorStyles.Left };
        uninstallBtn.Click += (_, _) => InstallUninstall.RunUninstallInteractive(quiet: false);

        var actionRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        actionRow.Controls.Add(applyBtn);
        actionRow.Controls.Add(launchBtn);
        actionRow.Controls.Add(safetyBtn);
        actionRow.Controls.Add(uninstallBtn);
        panel.SetColumnSpan(actionRow, 2);
        panel.Controls.Add(actionRow, 1, 8);

        // ---- 行 9–10：状态 ----
        _statusLabel = new Label
        {
            Text = "状态: …",
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Fill,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        panel.SetColumnSpan(_statusLabel, 3);
        panel.Controls.Add(_statusLabel, 0, 9);

        _pathStatusLabel = new Label
        {
            Text = "",
            AutoSize = false,
            Height = 36,
            Dock = DockStyle.Fill,
            ForeColor = UiStyle.SecondaryText,
        };
        panel.SetColumnSpan(_pathStatusLabel, 3);
        panel.Controls.Add(_pathStatusLabel, 0, 10);

        var tip = new Label
        {
            Text = "提示：开机自启不会弹出系统授权框。游戏内请关闭 V-Sync。关闭窗口会隐藏到托盘。\n" +
                   "默认安装于 C:\\Program Files\\GenshinFpsUnlocker\\ ；配置/日志在 %LocalAppData%\\GenshinFpsUnlocker\\ 。\n" +
                   "官方安装包含 GenshinFpsUnlocker.uninst.exe 卸载。调试日志默认开启。",
            AutoSize = false,
            Height = 52,
            Dock = DockStyle.Fill,
            ForeColor = UiStyle.SecondaryText,
        };
        panel.SetColumnSpan(tip, 3);
        panel.Controls.Add(tip, 0, 11);

        Controls.Add(panel);
        Controls.Add(_safetyLabel);
        Controls.Add(title);

        // ---- 即时开关（改完即生效并写配置）----
        _masterBox.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetMasterEnabled(_masterBox.Checked);
            SyncTrayChecks();
        };
        _enabledBox.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetEnabled(_enabledBox.Checked);
            SyncTrayChecks();
        };
        _autoWatchBox.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAutoWatch(_autoWatchBox.Checked);
            SyncTrayChecks();
        };
        _autoStartBox.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAutoStartWithWindows(_autoStartBox.Checked);
            SyncTrayChecks();
        };
        _startMinBox.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _config.StartMinimized = _startMinBox.Checked;
            _config.TrySave(out _);
        };
        _logBox.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _config.DebugLogging = _logBox.Checked;
            _config.TrySave(out _);
            AppLog.ApplyConfig(_config);
            AppLog.Info($"debugLogging toggled => {_config.DebugLogging}");
        };
        _desktopShortcutBox.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _config.CreateDesktopShortcut = _desktopShortcutBox.Checked;
            _config.TrySave(out _);
            if (_config.CreateDesktopShortcut)
            {
                try { ShortcutHelper.CreateDesktopShortcut(AppPaths.ExePath, AppPaths.ExeDirectory); }
                catch (Exception ex) { AppLog.Warn(ex.Message); }
            }
        };

        BuildTray();

        _service.StateChanged += () =>
        {
            if (IsDisposed) return;
            try { BeginInvoke(UpdateStatusUi); }
            catch { /* ignore */ }
        };

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => UpdateStatusUi();
        _uiTimer.Start();

        Load += (_, _) =>
        {
            ApplyThemeColors();
            UpdateStatusUi();
            if (_config.StartMinimized)
            {
                BeginInvoke(() =>
                {
                    WindowState = FormWindowState.Minimized;
                    HideToTray();
                });
            }
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        };

        FormClosing += (_, e) =>
        {
            // 用户点关闭 → 托盘；退出菜单 / Application.Exit / Windows 关机 → 真退出
            if (!_reallyExit
                && e.CloseReason is CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _reallyExit = true;
            try { _fpsSaveTimer?.Stop(); _fpsSaveTimer?.Dispose(); } catch { /* ignore */ }
            _uiTimer.Stop();
            _tray.Visible = false;
            try { PersistAll(showTip: false); } catch { /* ignore */ }
        };
    }

    private void ScheduleFpsSave()
    {
        _fpsSaveTimer ??= new System.Windows.Forms.Timer { Interval = 600 };
        _fpsSaveTimer.Stop();
        _fpsSaveTimer.Tick -= FpsSaveTimerOnTick;
        _fpsSaveTimer.Tick += FpsSaveTimerOnTick;
        _fpsSaveTimer.Start();
    }

    private void FpsSaveTimerOnTick(object? sender, EventArgs e)
    {
        try { _fpsSaveTimer?.Stop(); } catch { /* ignore */ }
        if (IsDisposed) return;
        _config.TargetFps = (int)_fpsInput.Value;
        if (!_config.TrySave(out var fpsErr))
            AppLog.Warn("fps save: " + fpsErr);
    }

    private void ShowSafetyDialog(bool force) => SafetyDialog.Show(this, _config, force);

    private void ApplyThemeColors()
    {
        try
        {
            _safetyLabel.BackColor = UiStyle.SafetyBannerBack;
            _safetyLabel.ForeColor = UiStyle.SafetyBannerFore;
            _pathStatusLabel.ForeColor = UiStyle.SecondaryText;
        }
        catch { /* ignore */ }
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 6, 0, 0),
    };

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
