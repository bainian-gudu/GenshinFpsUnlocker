namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 系统托盘：精简右键菜单（主界面 / 启动游戏 / 帧率解锁 / 自动解锁 / 修改帧率 / 退出），
/// 外观跟随 Web UI 深浅色，避免系统默认灰白菜单。
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>与 Web UI FpsControl 预设一致。</summary>
    private static readonly int[] TrayFpsPresets = [60, 90, 120, 144, 165, 240];

    private ToolStripMenuItem? _trayStatusItem;
    private ToolStripMenuItem? _trayEnabledItem;
    private ToolStripMenuItem? _trayAutoWatchItem;
    private ToolStripMenuItem? _trayFpsRoot;
    private ContextMenuStrip? _trayMenu;
    private Icon? _trayIconOwned;
    private bool _trayTipShownThisSession;
    /// <summary>主窗是否已藏入托盘（气泡/提示文案用）。</summary>
    private bool _inTray;

    /// <summary>当前是否在托盘后台模式。</summary>
    internal bool IsInTray => _inTray;

    private void BuildTray()
    {
        _trayIconOwned = AppIcon.LoadClone();
        var icon = _trayIconOwned ?? SystemIcons.Application;
        _tray = new NotifyIcon
        {
            Visible = true,
            Text = Truncate(BuildTrayTipText(), 63),
            Icon = icon,
            BalloonTipIcon = ToolTipIcon.Info,
        };
        AppLog.Info($"tray created visible={_tray.Visible} hasAppIcon={_trayIconOwned is not null}");

        var dark = UiStyle.IsUiDark;
        var menu = new ContextMenuStrip
        {
            Name = "TrayMenu",
            ShowImageMargin = false,
            ShowCheckMargin = true,
            AutoClose = true,
            Font = UiStyle.UiFont,
            Padding = new Padding(6, 8, 6, 8),
            Renderer = new TrayMenuRenderer(dark),
            BackColor = dark ? Color.FromArgb(0x1C, 0x17, 0x26) : Color.FromArgb(0xFA, 0xF5, 0xFC),
            ForeColor = dark ? Color.FromArgb(0xF3, 0xEE, 0xF8) : Color.FromArgb(0x2A, 0x1F, 0x35),
        };
        _trayMenu = menu;

        // —— 状态头 ——
        _trayStatusItem = MakeHeaderItem(BuildStatusHeaderText());
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(MakeSep());

        // —— 主界面 ——
        menu.Items.Add(MakeActionItem("显示主界面", (_, _) => RestoreFromTrayPublic()));
        menu.Items.Add(MakeSep());

        // —— 启动游戏 ——
        menu.Items.Add(MakeActionItem("启动游戏", (_, _) =>
        {
            if (_service.TryLaunchGame(out var msg))
                ShowTrayBalloon("启动游戏", msg, ToolTipIcon.Info);
            else
                ShowTrayBalloon("启动游戏", msg, ToolTipIcon.Warning);
            PushUiAndRefreshTray();
        }));
        menu.Items.Add(MakeSep());

        // —— 帧率解锁 ——
        _trayEnabledItem = MakeCheckItem(
            "帧率解锁",
            _config.Enabled,
            "开启后按目标帧率注入；关闭则暂停解锁");
        _trayEnabledItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetEnabled(_trayEnabledItem.Checked);
            AfterTrayConfigChange("帧率解锁");
        };
        menu.Items.Add(_trayEnabledItem);

        // —— 自动解锁 ——
        _trayAutoWatchItem = MakeCheckItem(
            "自动解锁",
            _config.AutoWatch,
            "检测到游戏启动后自动应用帧率设置");
        _trayAutoWatchItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAutoWatch(_trayAutoWatchItem.Checked);
            AfterTrayConfigChange("自动解锁");
        };
        menu.Items.Add(_trayAutoWatchItem);
        menu.Items.Add(MakeSep());

        // —— 修改帧率（预设 + 自定义）——
        _trayFpsRoot = new ToolStripMenuItem($"修改帧率  ·  {_config.TargetFps} FPS")
        {
            ToolTipText = "选择预设或自定义目标帧率",
            Padding = new Padding(4, 4, 4, 4),
        };
        menu.Items.Add(_trayFpsRoot);
        BuildTrayFpsItems();
        menu.Items.Add(MakeSep());

        // —— 退出 ——
        menu.Items.Add(MakeActionItem("退出", (_, _) =>
        {
            _reallyExit = true;
            Close();
        }));

        menu.Opening += (_, _) =>
        {
            try { ApplyTrayMenuTheme(); } catch { /* ignore */ }
            SyncTrayFromConfig();
        };

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) =>
        {
            try { RestoreFromTrayPublic(); }
            catch (Exception ex) { AppLog.Error(ex, "tray DoubleClick restore"); }
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            try { RestoreFromTrayPublic(); }
            catch (Exception ex) { AppLog.Error(ex, "tray MouseClick restore"); }
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            try { RestoreFromTrayPublic(); }
            catch (Exception ex) { AppLog.Error(ex, "tray BalloonTipClicked restore"); }
        };

        _service.StateChanged += OnServiceStateForTray;
        UpdateTrayTip();
    }

    private static ToolStripMenuItem MakeHeaderItem(string text) =>
        new(text)
        {
            Enabled = false,
            Font = new Font(UiStyle.UiFont, FontStyle.Bold),
            Padding = new Padding(4, 6, 4, 6),
        };

    private static ToolStripMenuItem MakeActionItem(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            Padding = new Padding(4, 5, 4, 5),
        };
        item.Click += onClick;
        return item;
    }

    private static ToolStripMenuItem MakeCheckItem(string text, bool checkedState, string tip)
    {
        return new ToolStripMenuItem(text)
        {
            CheckOnClick = true,
            Checked = checkedState,
            ToolTipText = tip,
            Padding = new Padding(4, 5, 4, 5),
        };
    }

    private static ToolStripSeparator MakeSep() =>
        new() { Margin = new Padding(8, 4, 8, 4) };

    private void ApplyTrayMenuTheme()
    {
        if (_trayMenu is null) return;
        var dark = UiStyle.IsUiDark;
        _trayMenu.Renderer = new TrayMenuRenderer(dark);
        _trayMenu.BackColor = dark ? Color.FromArgb(0x1C, 0x17, 0x26) : Color.FromArgb(0xFA, 0xF5, 0xFC);
        _trayMenu.ForeColor = dark ? Color.FromArgb(0xF3, 0xEE, 0xF8) : Color.FromArgb(0x2A, 0x1F, 0x35);
        _trayMenu.Font = UiStyle.UiFont;
        foreach (ToolStripItem it in _trayMenu.Items)
            StyleTrayItem(it, dark);
        if (_trayFpsRoot is not null)
        {
            foreach (ToolStripItem it in _trayFpsRoot.DropDownItems)
                StyleTrayItem(it, dark);
            _trayFpsRoot.DropDown.Renderer = new TrayMenuRenderer(dark);
            _trayFpsRoot.DropDown.BackColor = _trayMenu.BackColor;
            _trayFpsRoot.DropDown.ForeColor = _trayMenu.ForeColor;
        }
    }

    private static void StyleTrayItem(ToolStripItem it, bool dark)
    {
        it.ForeColor = dark ? Color.FromArgb(0xF3, 0xEE, 0xF8) : Color.FromArgb(0x2A, 0x1F, 0x35);
        if (it is ToolStripMenuItem mi && !mi.Enabled)
            it.ForeColor = dark ? Color.FromArgb(0x96, 0x86, 0xA8) : Color.FromArgb(0x85, 0x74, 0x92);
    }

    private void OnServiceStateForTray()
    {
        if (IsDisposed) return;
        try
        {
            if (IsHandleCreated)
                BeginInvoke(UpdateTrayTip);
            else
                UpdateTrayTip();
        }
        catch { /* ignore */ }
    }

    private void AfterTrayConfigChange(string what)
    {
        AppLog.Info($"托盘更改: {what}");
        PushUiAndRefreshTray();
    }

    private void PushUiAndRefreshTray()
    {
        SyncTrayFromConfig();
        if (_webReady) _bridge.PushState();
    }

    private void ShowCustomFpsDialog()
    {
        var dark = UiStyle.IsUiDark;
        using var dlg = new Form
        {
            Text = "修改目标帧率",
            Width = 340,
            Height = 188,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = dark ? UiStyle.UiDarkBg : UiStyle.UiLightBg,
            ForeColor = dark ? UiStyle.UiDarkText : UiStyle.UiLightText,
            Font = UiStyle.UiFont,
        };
        UiStyle.ApplyToForm(dlg);
        UiStyle.ApplyTitleBarChrome(dlg, dark);

        var label = new Label
        {
            Text = "目标帧率（1 – 540）",
            Left = 22,
            Top = 22,
            AutoSize = true,
            ForeColor = dark ? Color.FromArgb(0xC4, 0xB6, 0xD4) : Color.FromArgb(0x6B, 0x5A, 0x78),
        };
        var num = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 540,
            Value = Math.Clamp(_config.TargetFps, 1, 540),
            Left = 22,
            Top = 52,
            Width = 140,
            Font = UiStyle.UiFontBold(2f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = new Button
        {
            Text = "确定",
            Left = 200,
            Top = 50,
            Width = 100,
            Height = 32,
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = dark ? Color.FromArgb(0xE8, 0x79, 0xF9) : Color.FromArgb(0xC0, 0x26, 0xD3),
            ForeColor = dark ? Color.FromArgb(0x2A, 0x0A, 0x36) : Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            Left = 200,
            Top = 96,
            Width = 100,
            Height = 30,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = dark ? Color.FromArgb(0x25, 0x1F, 0x32) : Color.FromArgb(0xF8, 0xF1, 0xFB),
            ForeColor = dlg.ForeColor,
        };
        cancel.FlatAppearance.BorderColor = dark ? Color.FromArgb(0x35, 0x2B, 0x45) : Color.FromArgb(0xEB, 0xDF, 0xF3);
        dlg.Controls.Add(label);
        dlg.Controls.Add(num);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(Visible ? this : null) == DialogResult.OK)
        {
            _service.ApplyFps((int)num.Value);
            _config.TrySave(out _);
            PushUiAndRefreshTray();
            ShowTrayBalloon("帧率", $"目标 FPS = {_config.TargetFps}", ToolTipIcon.Info);
        }
    }

    private void BuildTrayFpsItems()
    {
        if (_trayFpsRoot is null) return;
        _trayFpsRoot.Text = $"修改帧率  ·  {_config.TargetFps} FPS";
        _trayFpsRoot.DropDownItems.Clear();

        foreach (var preset in TrayFpsPresets)
        {
            var p = preset;
            var item = new ToolStripMenuItem($"{p} FPS")
            {
                Checked = _config.TargetFps == p,
                ToolTipText = p == 120 ? "推荐" : null,
                Padding = new Padding(4, 4, 4, 4),
            };
            if (p == 120)
                item.Text = "120 FPS  · 推荐";
            item.Click += (_, _) =>
            {
                _service.ApplyFps(p);
                _config.TrySave(out _);
                PushUiAndRefreshTray();
                ShowTrayBalloon("帧率", $"目标 FPS = {p}", ToolTipIcon.Info);
            };
            _trayFpsRoot.DropDownItems.Add(item);
        }

        _trayFpsRoot.DropDownItems.Add(MakeSep());
        var custom = new ToolStripMenuItem("自定义…")
        {
            Padding = new Padding(4, 4, 4, 4),
            ToolTipText = "输入 1–540 之间的目标帧率",
        };
        custom.Click += (_, _) => ShowCustomFpsDialog();
        _trayFpsRoot.DropDownItems.Add(custom);

        try { ApplyTrayMenuTheme(); } catch { /* ignore */ }
    }

    private string BuildStatusHeaderText()
    {
        var effective = _config.MasterEnabled && _config.Enabled;
        var pid = _service.AttachedPid;
        if (pid > 0)
            return effective
                ? $"运行中  ·  PID {pid}  ·  {_config.TargetFps} FPS"
                : $"已附加  ·  解锁已关  ·  PID {pid}";
        if (!_config.MasterEnabled) return "解锁服务已暂停";
        if (!_config.Enabled) return $"帧率解锁已关闭  ·  {_config.TargetFps} FPS";
        if (_config.AutoWatch) return $"自动监视中  ·  {_config.TargetFps} FPS";
        return $"已就绪  ·  {_config.TargetFps} FPS";
    }

    private string BuildTrayTipText()
    {
        var effective = _config.MasterEnabled && _config.Enabled ? "开" : "关";
        var watch = _config.AutoWatch ? "监视" : "待命";
        var pid = _service.AttachedPid;
        var mode = _inTray ? "托盘" : "窗口";
        var core = pid > 0
            ? $"FPS {_config.TargetFps} | {effective} | PID {pid} | {mode}"
            : $"FPS {_config.TargetFps} | {effective} | {watch} | {mode}";
        return Truncate(core, 63);
    }

    private void ShowTrayBalloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(2200);
        }
        catch { /* ignore */ }
    }

    /// <summary>托盘菜单绘制：圆角选中条 + 设计稿紫强调色。</summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _dark;
        private readonly Color _bg;
        private readonly Color _hover;
        private readonly Color _accent;
        private readonly Color _text;
        private readonly Color _muted;
        private readonly Color _sep;

        public TrayMenuRenderer(bool dark)
            : base(new TrayColorTable(dark))
        {
            _dark = dark;
            _bg = dark ? Color.FromArgb(0x1C, 0x17, 0x26) : Color.FromArgb(0xFA, 0xF5, 0xFC);
            _hover = dark ? Color.FromArgb(0x2A, 0x22, 0x38) : Color.FromArgb(0xF0, 0xE4, 0xF7);
            _accent = dark ? Color.FromArgb(0xE8, 0x79, 0xF9) : Color.FromArgb(0xC0, 0x26, 0xD3);
            _text = dark ? Color.FromArgb(0xF3, 0xEE, 0xF8) : Color.FromArgb(0x2A, 0x1F, 0x35);
            _muted = dark ? Color.FromArgb(0x96, 0x86, 0xA8) : Color.FromArgb(0x85, 0x74, 0x92);
            _sep = dark ? Color.FromArgb(0x35, 0x2B, 0x45) : Color.FromArgb(0xEB, 0xDF, 0xF3);
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(_bg);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_sep);
            var r = e.AffectedBounds;
            r.Width -= 1;
            r.Height -= 1;
            e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 无左侧图标栏
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rc = e.ImageRectangle;
            if (rc.Width < 8) rc = new Rectangle(e.Item.ContentRectangle.X + 4, e.Item.ContentRectangle.Y + (e.Item.Height - 14) / 2, 14, 14);
            using var pen = new Pen(_accent, 1.8f);
            // 简单对勾
            var x = rc.Left + 2;
            var y = rc.Top + rc.Height / 2;
            g.DrawLines(pen, new[]
            {
                new Point(x, y),
                new Point(x + 4, y + 4),
                new Point(x + 10, y - 4),
            });
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var item = e.Item;
            var bounds = new Rectangle(4, 1, item.Width - 8, item.Height - 2);

            if (!item.Selected && !item.Pressed)
            {
                using var b = new SolidBrush(_bg);
                g.FillRectangle(b, item.ContentRectangle);
                return;
            }

            if (!item.Enabled) return;

            using var path = RoundRect(bounds, 6);
            using var brush = new SolidBrush(_hover);
            g.FillPath(brush, path);
            // 左侧强调条
            using var accent = new SolidBrush(_accent);
            g.FillRectangle(accent, new Rectangle(bounds.X, bounds.Y + 4, 3, bounds.Height - 8));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? (e.Item.Selected ? _accent : _text)
                : _muted;
            if (e.Item is ToolStripMenuItem { Checked: true, CheckOnClick: true })
                e.TextColor = _accent;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
            using var pen = new Pen(_sep);
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle bounds, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        private readonly Color _bg;
        private readonly Color _hover;

        public TrayColorTable(bool dark)
        {
            _bg = dark ? Color.FromArgb(0x1C, 0x17, 0x26) : Color.FromArgb(0xFA, 0xF5, 0xFC);
            _hover = dark ? Color.FromArgb(0x2A, 0x22, 0x38) : Color.FromArgb(0xF0, 0xE4, 0xF7);
        }

        public override Color MenuBorder => _bg;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color MenuStripGradientBegin => _bg;
        public override Color MenuStripGradientEnd => _bg;
        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color SeparatorDark => _bg;
        public override Color SeparatorLight => _bg;
        public override Color CheckBackground => _bg;
        public override Color CheckSelectedBackground => _hover;
        public override Color CheckPressedBackground => _hover;
    }
}
