namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 主窗体 — 系统托盘菜单与最小化行为（partial）。
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>构建托盘图标与右键菜单（含 FPS 预设、路径、日志、卸载等）。</summary>
    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "原神帧率解锁",
            Icon = SystemIcons.Application,
        };

        var menu = new ContextMenuStrip();

        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());

        _trayMasterItem = new ToolStripMenuItem("总开关") { CheckOnClick = true, Checked = _config.MasterEnabled };
        _trayMasterItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _syncingUi = true;
            _masterBox.Checked = _trayMasterItem.Checked;
            _syncingUi = false;
            _service.SetMasterEnabled(_trayMasterItem.Checked);
            UpdateStatusUi();
        };
        menu.Items.Add(_trayMasterItem);

        _trayEnabledItem = new ToolStripMenuItem("启用帧率解锁") { CheckOnClick = true, Checked = _config.Enabled };
        _trayEnabledItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _syncingUi = true;
            _enabledBox.Checked = _trayEnabledItem.Checked;
            _syncingUi = false;
            _service.SetEnabled(_trayEnabledItem.Checked);
            UpdateStatusUi();
        };
        menu.Items.Add(_trayEnabledItem);

        _trayAutoWatchItem = new ToolStripMenuItem("自动监视并注入") { CheckOnClick = true, Checked = _config.AutoWatch };
        _trayAutoWatchItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _syncingUi = true;
            _autoWatchBox.Checked = _trayAutoWatchItem.Checked;
            _syncingUi = false;
            _service.SetAutoWatch(_trayAutoWatchItem.Checked);
            UpdateStatusUi();
        };
        menu.Items.Add(_trayAutoWatchItem);

        _trayAutoStartItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true, Checked = _config.AutoStartWithWindows };
        _trayAutoStartItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _syncingUi = true;
            _autoStartBox.Checked = _trayAutoStartItem.Checked;
            _syncingUi = false;
            _service.SetAutoStartWithWindows(_trayAutoStartItem.Checked);
            UpdateStatusUi();
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
                _fpsInput.Value = num.Value;
                PersistAll(showTip: false);
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
        });

        menu.Items.Add("自动查找游戏路径", null, (_, _) =>
        {
            var r = _service.AutoLocateGamePath();
            _gamePathBox.Text = _config.GamePath ?? "";
            _tray.ShowBalloonTip(2500, "自动查找",
                r.Ok ? $"已找到\n{r.Path}" : (r.Detail ?? "失败"),
                r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            UpdateStatusUi();
        });

        menu.Items.Add("手动选择游戏路径…", null, (_, _) =>
        {
            RestoreFromTray();
            var r = _service.SetGamePathManual(this);
            if (r.Ok)
            {
                _gamePathBox.Text = r.Path ?? "";
                PersistAll(showTip: false);
            }
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("查看安全声明…", null, (_, _) => ShowSafetyDialog(force: true));
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
            _syncingUi = true;
            _logBox.Checked = _trayLogItem.Checked;
            _syncingUi = false;
            _config.DebugLogging = _trayLogItem.Checked;
            _config.Save();
            AppLog.ApplyConfig(_config);
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
            InstallUninstall.RunUninstall(quiet: false);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _reallyExit = true;
            Close();
        });

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    /// <summary>重建托盘“目标帧率”子菜单勾选状态。</summary>
    private void BuildTrayFpsItems()
    {
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
                _fpsInput.Value = p;
                PersistAll(showTip: false);
                _tray.ShowBalloonTip(1000, "帧率", $"目标 FPS = {p}", ToolTipIcon.Info);
            };
            _trayFpsRoot.DropDownItems.Add(item);
        }
    }

    /// <summary>把界面控件写回配置、推送 IPC、同步托盘；可选气球提示。</summary>
    private void PersistAll(bool showTip)
    {
        _config.TargetFps = (int)_fpsInput.Value;
        _config.MasterEnabled = _masterBox.Checked;
        _config.Enabled = _enabledBox.Checked;
        _config.AutoWatch = _autoWatchBox.Checked;
        _config.StartMinimized = _startMinBox.Checked;
        _config.AutoStartWithWindows = _autoStartBox.Checked;
        _config.DebugLogging = _logBox.Checked;
        _config.CreateDesktopShortcut = _desktopShortcutBox.Checked;
        _config.Save();
        AppLog.ApplyConfig(_config);
        _service.PushConfigToIpc();
        Autostart.SetEnabled(_config.AutoStartWithWindows);
        if (_config.CreateDesktopShortcut)
        {
            try { ShortcutHelper.CreateDesktopShortcut(AppPaths.ExePath, AppPaths.ExeDirectory); }
            catch (Exception ex) { AppLog.Warn("desktop shortcut: " + ex.Message); }
        }
        SyncTrayChecks();
        BuildTrayFpsItems();
        UpdateStatusUi();
        if (showTip)
            _tray.ShowBalloonTip(1500, "已保存", $"目标 FPS = {_config.TargetFps} | 总开关={_config.MasterEnabled}", ToolTipIcon.Info);
    }

    /// <summary>主界面 ↔ 托盘勾选双向同步（_syncingUi 防递归）。</summary>
    private void SyncTrayChecks()
    {
        _syncingUi = true;
        try
        {
            _trayMasterItem.Checked = _config.MasterEnabled;
            _trayEnabledItem.Checked = _config.Enabled;
            _trayAutoWatchItem.Checked = _config.AutoWatch;
            _trayAutoStartItem.Checked = _config.AutoStartWithWindows;
            _masterBox.Checked = _config.MasterEnabled;
            _enabledBox.Checked = _config.Enabled;
            _autoWatchBox.Checked = _config.AutoWatch;
            _autoStartBox.Checked = _config.AutoStartWithWindows;
            _fpsInput.Value = Math.Clamp(_config.TargetFps, 1, 540);
            _gamePathBox.Text = _config.GamePath ?? "";
        }
        finally
        {
            _syncingUi = false;
        }
    }

    /// <summary>刷新状态标签与托盘提示文字（最长 63 字符）。</summary>
    private void UpdateStatusUi()
    {
        if (IsDisposed) return;
        _statusLabel.Text = $"状态: {_service.StatusText}";
        _pathStatusLabel.Text = _service.GamePathStatus;
        _gamePathBox.Text = _config.GamePath ?? _gamePathBox.Text;
        _tray.Text = Truncate(
            $"FPS {_config.TargetFps} | {(_config.MasterEnabled ? "开" : "关")} | {_service.StatusText}",
            63);
    }

    /// <summary>隐藏主窗口到托盘（任务栏不显示）。</summary>
    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    /// <summary>从托盘恢复主窗口。</summary>
    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        SyncTrayChecks();
        UpdateStatusUi();
    }


}
