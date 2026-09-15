namespace GenshinFpsUnlocker.Host;

/// <summary>托盘菜单构建与外观：菜单项工厂、深浅色主题、自定义帧率对话框。</summary>
internal sealed partial class MainForm
{
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
            // 统一左侧留白：勾选画在同一槽位，文字左对齐（避免默认 CheckMargin 把字顶歪）
            ShowImageMargin = false,
            ShowCheckMargin = false,
            AutoClose = true,
            Font = UiStyle.UiFont,
            Padding = new Padding(4, 6, 4, 6),
            Renderer = new TrayMenuRenderer(dark),
            BackColor = dark ? Color.FromArgb(0x1B, 0x1D, 0x25) : Color.FromArgb(0xF7, 0xF6, 0xFA),
            ForeColor = dark ? Color.FromArgb(0xED, 0xEC, 0xF3) : Color.FromArgb(0x1A, 0x1A, 0x22),
        };
        _trayMenu = menu;

        // —— 状态头 ——
        _trayStatusItem = MakeHeaderItem(BuildStatusHeaderText());
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(MakeSep());

        // —— 窗口与游戏操作 ——
        menu.Items.Add(MakeActionItem("显示主界面", (_, _) => RestoreFromTrayPublic()));
        menu.Items.Add(MakeActionItem("启动游戏", (_, _) =>
        {
            // 成败都只发一条信息类通知：文案本身已说明结果，不必再用警告图标
            _ = _service.TryLaunchGame(out var msg);
            ShowTrayBalloon("启动游戏", msg);
            PushUiAndRefreshTray();
        }));
        menu.Items.Add(MakeSep());

        // —— 帧率解锁组 ——
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

        // 修改帧率（预设 + 自定义）：紧随帧率解锁
        _trayFpsRoot = new ToolStripMenuItem($"修改帧率  ·  {_config.TargetFps} FPS")
        {
            ToolTipText = "选择预设或自定义目标帧率",
            Padding = TrayItemPadding,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        menu.Items.Add(_trayFpsRoot);
        BuildTrayFpsItems();

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

        // —— 反虚化组（画面效果注入，随游戏进程即时生效；联机/UGC 玩法勿开）——
        _trayAntiBlurPerspectiveItem = MakeCheckItem(
            "反角色虚化",
            _config.AntiBlurPerspective,
            "镜头拉近时角色不再透明化（仅供单机体验）");
        _trayAntiBlurPerspectiveItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAntiBlurPerspective(_trayAntiBlurPerspectiveItem.Checked);
            AfterTrayConfigChange("反角色虚化");
        };
        menu.Items.Add(_trayAntiBlurPerspectiveItem);

        _trayAntiBlurDiveMosaicItem = MakeCheckItem(
            "移除水下马赛克",
            _config.AntiBlurDiveMosaic,
            "角色入水时不再显示马赛克虚化（仅供单机体验）");
        _trayAntiBlurDiveMosaicItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAntiBlurDiveMosaic(_trayAntiBlurDiveMosaicItem.Checked);
            AfterTrayConfigChange("移除水下马赛克");
        };
        menu.Items.Add(_trayAntiBlurDiveMosaicItem);
        menu.Items.Add(MakeSep());

        // —— 超分辨率替换组（独立于 FPS/反虚化注入）——
        _trayUpscalerEnabledItem = MakeCheckItem(
            "超分辨率替换",
            _config.UpscalerReplacementEnabled,
            "启动游戏后加载 OptiScaler 代理并将 FSR2 输出到 DLSS");
        _trayUpscalerEnabledItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetUpscalerReplacementEnabled(_trayUpscalerEnabledItem.Checked);
            AfterTrayConfigChange("超分辨率替换");
        };
        menu.Items.Add(_trayUpscalerEnabledItem);

        _trayUpscalerModeRoot = new ToolStripMenuItem($"DLSS 版本  ·  {UpscalerModeLabel(_config.UpscalerMode)}")
        {
            ToolTipText = "切换 DLSS 4（标准超分辨率）或 DLSS 5（超分辨率 + 神经渲染）",
            Padding = TrayItemPadding,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        BuildTrayUpscalerModeItems();
        menu.Items.Add(_trayUpscalerModeRoot);

        _trayUpscalerQualityRoot = new ToolStripMenuItem($"超分挡位  ·  {UpscalerQualityLabel(_config.UpscalerQuality)}")
        {
            ToolTipText = "选择 DLSS 输出质量挡位",
            Padding = TrayItemPadding,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        BuildTrayUpscalerQualityItems();
        menu.Items.Add(_trayUpscalerQualityRoot);
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

        // 四角圆边：Win11 走 DWM 原生圆角，Win10 用 Region 裁角兜底
        TrayMenuCorners.Apply(menu);

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

    /// <summary>菜单项统一内边距：左侧留给勾选槽，文字与动作项对齐。</summary>
    private static Padding TrayItemPadding => new(4, 4, 10, 4);

    private static ToolStripMenuItem MakeHeaderItem(string text) =>
        new(text)
        {
            Enabled = false,
            Font = new Font(UiStyle.UiFont, FontStyle.Bold),
            Padding = TrayItemPadding,
            TextAlign = ContentAlignment.MiddleLeft,
        };

    private static ToolStripMenuItem MakeActionItem(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            Padding = TrayItemPadding,
            TextAlign = ContentAlignment.MiddleLeft,
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
            Padding = TrayItemPadding,
            TextAlign = ContentAlignment.MiddleLeft,
        };
    }

    private static ToolStripSeparator MakeSep() =>
        new() { Margin = new Padding(10, 3, 10, 3) };

    private void ApplyTrayMenuTheme()
    {
        if (_trayMenu is null) return;
        var dark = UiStyle.IsUiDark;
        // 旧 renderer 的画笔/画刷随它一起释放，别等 GC
        if (_trayMenu.Renderer is IDisposable oldRenderer) oldRenderer.Dispose();
        _trayMenu.Renderer = new TrayMenuRenderer(dark);
        _trayMenu.BackColor = dark ? Color.FromArgb(0x1B, 0x1D, 0x25) : Color.FromArgb(0xF7, 0xF6, 0xFA);
        _trayMenu.ForeColor = dark ? Color.FromArgb(0xED, 0xEC, 0xF3) : Color.FromArgb(0x1A, 0x1A, 0x22);
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
            // 子菜单（帧率预设）是独立的弹出窗口，圆角要单独设一次；可重复调用
            TrayMenuCorners.Apply(_trayFpsRoot.DropDown);
        }
        if (_trayUpscalerModeRoot is not null)
        {
            foreach (ToolStripItem it in _trayUpscalerModeRoot.DropDownItems)
                StyleTrayItem(it, dark);
            _trayUpscalerModeRoot.DropDown.Renderer = new TrayMenuRenderer(dark);
            _trayUpscalerModeRoot.DropDown.BackColor = _trayMenu.BackColor;
            _trayUpscalerModeRoot.DropDown.ForeColor = _trayMenu.ForeColor;
            TrayMenuCorners.Apply(_trayUpscalerModeRoot.DropDown);
        }
        if (_trayUpscalerQualityRoot is not null)
        {
            foreach (ToolStripItem it in _trayUpscalerQualityRoot.DropDownItems)
                StyleTrayItem(it, dark);
            _trayUpscalerQualityRoot.DropDown.Renderer = new TrayMenuRenderer(dark);
            _trayUpscalerQualityRoot.DropDown.BackColor = _trayMenu.BackColor;
            _trayUpscalerQualityRoot.DropDown.ForeColor = _trayMenu.ForeColor;
            TrayMenuCorners.Apply(_trayUpscalerQualityRoot.DropDown);
        }

        TrayMenuCorners.Apply(_trayMenu);
    }

    private static void StyleTrayItem(ToolStripItem it, bool dark)
    {
        it.ForeColor = dark ? Color.FromArgb(0xED, 0xEC, 0xF3) : Color.FromArgb(0x1A, 0x1A, 0x22);
        if (it is ToolStripMenuItem mi && !mi.Enabled)
            it.ForeColor = dark ? Color.FromArgb(0x8B, 0x8C, 0x9C) : Color.FromArgb(0x77, 0x70, 0x82);
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
            ForeColor = dark ? Color.FromArgb(0xB0, 0xAF, 0xBE) : Color.FromArgb(0x55, 0x52, 0x64),
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
            BackColor = dark ? Color.FromArgb(0xBD, 0xA2, 0xF2) : Color.FromArgb(0x90, 0x6A, 0xC7),
            ForeColor = dark ? Color.FromArgb(0x25, 0x1B, 0x36) : Color.White,
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
            BackColor = dark ? Color.FromArgb(0x22, 0x24, 0x2E) : Color.FromArgb(0xEE, 0xEC, 0xF4),
            ForeColor = dlg.ForeColor,
        };
        cancel.FlatAppearance.BorderColor = dark ? Color.FromArgb(0x2D, 0x2E, 0x3A) : Color.FromArgb(0xD8, 0xD4, 0xE4);
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
            ShowTrayBalloon("帧率", $"目标 FPS = {_config.TargetFps}");
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
                CheckOnClick = false,
                ToolTipText = p == 120 ? "推荐" : null,
                Padding = TrayItemPadding,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            if (p == 120)
                item.Text = "120 FPS  · 推荐";
            item.Click += (_, _) =>
            {
                _service.ApplyFps(p);
                _config.TrySave(out _);
                PushUiAndRefreshTray();
                ShowTrayBalloon("帧率", $"目标 FPS = {p}");
            };
            _trayFpsRoot.DropDownItems.Add(item);
        }

        _trayFpsRoot.DropDownItems.Add(MakeSep());
        var custom = new ToolStripMenuItem("自定义…")
        {
            Padding = TrayItemPadding,
            TextAlign = ContentAlignment.MiddleLeft,
            ToolTipText = "输入 1–540 之间的目标帧率",
        };
        custom.Click += (_, _) => ShowCustomFpsDialog();
        _trayFpsRoot.DropDownItems.Add(custom);

        try { ApplyTrayMenuTheme(); } catch { /* ignore */ }
    }

    private void BuildTrayUpscalerModeItems()
    {
        if (_trayUpscalerModeRoot is null) return;
        _trayUpscalerModeRoot.DropDownItems.Clear();
        var options = new[]
        {
            (Value: "dlss4", Label: "DLSS 4 · 超分辨率"),
            (Value: "dlss5", Label: "DLSS 5 · 超分辨率 + 神经渲染"),
        };
        foreach (var option in options)
        {
            var item = new ToolStripMenuItem(option.Label)
            {
                Tag = option.Value,
                Checked = string.Equals(_config.UpscalerMode, option.Value, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
                Padding = TrayItemPadding,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            item.Click += (_, _) =>
            {
                _service.SetUpscalerMode(option.Value);
                PushUiAndRefreshTray();
                var suffix = _service.UpscalerState.Active ? "，下次启动游戏时生效" : string.Empty;
                ShowTrayBalloon("DLSS 版本", $"已设置为 {option.Label}{suffix}");
            };
            _trayUpscalerModeRoot.DropDownItems.Add(item);
        }
        try { ApplyTrayMenuTheme(); } catch { /* ignore */ }
    }

    private void BuildTrayUpscalerQualityItems()
    {
        if (_trayUpscalerQualityRoot is null) return;
        _trayUpscalerQualityRoot.DropDownItems.Clear();
        var options = new[]
        {
            (Value: "nativeAA", Label: "DLAA"),
            (Value: "quality", Label: "质量"),
            (Value: "balanced", Label: "均衡"),
            (Value: "performance", Label: "性能"),
            (Value: "ultraPerformance", Label: "超高性能"),
        };
        foreach (var option in options)
        {
            var item = new ToolStripMenuItem(option.Label)
            {
                Tag = option.Value,
                Checked = string.Equals(_config.UpscalerQuality, option.Value, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
                Padding = TrayItemPadding,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            item.Click += (_, _) =>
            {
                _service.SetUpscalerQuality(option.Value);
                PushUiAndRefreshTray();
                var suffix = _service.UpscalerState.Active ? "，下次启动游戏时生效" : string.Empty;
                ShowTrayBalloon("超分挡位", $"已设置为 {option.Label}{suffix}");
            };
            _trayUpscalerQualityRoot.DropDownItems.Add(item);
        }
        try { ApplyTrayMenuTheme(); } catch { /* ignore */ }
    }
}
