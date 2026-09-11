namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 主窗体 — 系统托盘菜单（partial）。Web UI 为主界面；托盘仍可完成全部设置。
/// </summary>
internal sealed partial class MainForm
{
    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "原神帧率解锁",
            Icon = SystemIcons.Application,
        };

        var menu = new ContextMenuStrip();

        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTrayPublic());
        menu.Items.Add(new ToolStripSeparator());

        _trayMasterItem = new ToolStripMenuItem("总开关") { CheckOnClick = true, Checked = _config.MasterEnabled };
        _trayMasterItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetMasterEnabled(_trayMasterItem.Checked);
            _config.TrySave(out _);
            _bridge.PushState();
            UpdateTrayTip();
        };
        menu.Items.Add(_trayMasterItem);

        _trayEnabledItem = new ToolStripMenuItem("启用帧率解锁") { CheckOnClick = true, Checked = _config.Enabled };
        _trayEnabledItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetEnabled(_trayEnabledItem.Checked);
            _config.TrySave(out _);
            _bridge.PushState();
            UpdateTrayTip();
        };
        menu.Items.Add(_trayEnabledItem);

        _trayAutoWatchItem = new ToolStripMenuItem("自动监视并注入") { CheckOnClick = true, Checked = _config.AutoWatch };
        _trayAutoWatchItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAutoWatch(_trayAutoWatchItem.Checked);
            _config.TrySave(out _);
            _bridge.PushState();
            UpdateTrayTip();
        };
        menu.Items.Add(_trayAutoWatchItem);

        _trayAutoStartItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true, Checked = _config.AutoStartWithWindows };
        _trayAutoStartItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAutoStartWithWindows(_trayAutoStartItem.Checked);
            _config.TrySave(out _);
            _bridge.PushState();
            UpdateTrayTip();
        };
        menu.Items.Add(_trayAutoStartItem);

        menu.Items.Add(new ToolStripSeparator());

        _trayFpsRoot = new ToolStripMenuItem("目标帧率");
        menu.Items.Add(_trayFpsRoot);
        BuildTrayFpsItems();

        var fpsCustom = new ToolStripMenuItem("自定义帧率…");
        fpsCustom.Click += (_, _) =>
        {
            using var dlg = new Form
            {
                Text = "自定义目标帧率",
                Width = 280,
                Height = 140,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false,
            };
            var num = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 540,
                Value = Math.Clamp(_config.TargetFps, 1, 540),
                Left = 20,
                Top = 20,
                Width = 120,
            };
            var ok = new Button { Text = "确定", Left = 160, Top = 18, Width = 80, DialogResult = DialogResult.OK };
            dlg.Controls.Add(num);
            dlg.Controls.Add(ok);
            dlg.AcceptButton = ok;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _service.ApplyFps((int)num.Value);
                _config.TrySave(out _);
                BuildTrayFpsItems();
                _bridge.PushState();
                _tray.ShowBalloonTip(1200, "帧率", $"目标 FPS = {_config.TargetFps}", ToolTipIcon.Info);
            }
        };
        menu.Items.Add(fpsCustom);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("启动游戏", null, (_, _) =>
        {
            if (_service.TryLaunchGame(out var msg))
                _tray.ShowBalloonTip(2000, "启动游戏", msg, ToolTipIcon.Info);
            else
                _tray.ShowBalloonTip(2500, "启动游戏", msg, ToolTipIcon.Warning);
            _bridge.PushState();
        });

        menu.Items.Add("自动查找游戏路径", null, (_, _) =>
        {
            var r = _service.AutoLocateGamePath();
            if (r.Ok) _config.TrySave(out _);
            _tray.ShowBalloonTip(2500, "自动查找",
                r.Ok ? $"已找到\n{r.Path}" : (r.Detail ?? "失败"),
                r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            _bridge.PushState();
            UpdateTrayTip();
        });

        menu.Items.Add("手动选择游戏路径…", null, (_, _) =>
        {
            RestoreFromTrayPublic();
            var r = _service.SetGamePathManual(this);
            if (r.Ok)
            {
                _config.TrySave(out _);
                _bridge.PushState();
            }
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("查看安全声明…", null, (_, _) =>
        {
            RestoreFromTrayPublic();
            SafetyDialog.Show(this, _config, force: true);
            _bridge.PushState();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开配置目录", null, (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppPaths.DataDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "打开配置目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        menu.Items.Add("打开日志目录", null, (_, _) => AppLog.OpenLogFolder());
        menu.Items.Add("打开当前日志文件", null, (_, _) => AppLog.OpenCurrentLogFile());
        _trayLogItem = new ToolStripMenuItem("调试日志") { CheckOnClick = true, Checked = _config.DebugLogging };
        _trayLogItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _config.DebugLogging = _trayLogItem.Checked;
            if (!_config.TrySave(out var logSaveErr))
                AppLog.Error("配置保存失败: " + logSaveErr);
            AppLog.ApplyConfig(_config);
            _bridge.PushState();
        };
        menu.Items.Add(_trayLogItem);
        menu.Items.Add("创建/刷新快捷方式", null, (_, _) =>
        {
            try
            {
                ShortcutHelper.CreateAll();
                _tray.ShowBalloonTip(1500, "快捷方式", "开始菜单与桌面快捷方式已更新", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "快捷方式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        menu.Items.Add("卸载并清理全部数据…", null, (_, _) =>
        {
            InstallUninstall.RunUninstallInteractive(quiet: false);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _reallyExit = true;
            Close();
        });

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTrayPublic();
        UpdateTrayTip();
    }

    private void BuildTrayFpsItems()
    {
        if (_trayFpsRoot is null) return;
        _trayFpsRoot.DropDownItems.Clear();
        foreach (var preset in new[] { 30, 60, 90, 120, 144, 165, 180, 240, 360 })
        {
            var p = preset;
            var item = new ToolStripMenuItem($"{p} FPS")
            {
                Checked = _config.TargetFps == p,
            };
            item.Click += (_, _) =>
            {
                _service.ApplyFps(p);
                _config.TrySave(out _);
                BuildTrayFpsItems();
                _bridge.PushState();
                _tray.ShowBalloonTip(1000, "帧率", $"目标 FPS = {p}", ToolTipIcon.Info);
            };
            _trayFpsRoot.DropDownItems.Add(item);
        }
    }
}
