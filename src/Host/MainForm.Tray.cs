using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 系统托盘：文案与开关与 Web UI 设计稿对齐；打开菜单时从配置/服务全量同步。
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>与 Web UI FpsControl 预设一致。</summary>
    private static readonly int[] TrayFpsPresets = [60, 90, 120, 144, 165, 240];

    private ToolStripMenuItem? _trayStatusItem;
    private ToolStripMenuItem? _trayStartMinItem;
    private Icon? _trayIconOwned;
    private bool _trayTipShownThisSession;
    private bool _inTray;

    private void BuildTray()
    {
        _trayIconOwned = CreateBrandTrayIcon();
        _tray = new NotifyIcon
        {
            Visible = true,
            Text = BuildTrayTipText(),
            Icon = _trayIconOwned ?? SystemIcons.Application,
        };

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            AutoClose = true,
        };

        // 状态头（不可点，打开时刷新）
        _trayStatusItem = new ToolStripMenuItem(BuildStatusHeaderText())
        {
            Enabled = false,
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTrayPublic());
        menu.Items.Add(new ToolStripSeparator());

        // —— 与概览「快捷设置 / 帧率解锁」对齐 ——
        _trayMasterItem = new ToolStripMenuItem("解锁服务总开关")
        {
            CheckOnClick = true,
            Checked = _config.MasterEnabled,
            ToolTipText = "关闭后暂停所有注入与帧率解锁",
        };
        _trayMasterItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetMasterEnabled(_trayMasterItem.Checked);
            AfterTrayConfigChange("解锁服务总开关");
        };
        menu.Items.Add(_trayMasterItem);

        _trayEnabledItem = new ToolStripMenuItem("帧率解锁")
        {
            CheckOnClick = true,
            Checked = _config.Enabled,
            ToolTipText = "与总开关同时开启时目标帧率才会生效",
        };
        _trayEnabledItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetEnabled(_trayEnabledItem.Checked);
            AfterTrayConfigChange("帧率解锁");
        };
        menu.Items.Add(_trayEnabledItem);

        _trayAutoWatchItem = new ToolStripMenuItem("自动解锁")
        {
            CheckOnClick = true,
            Checked = _config.AutoWatch,
            ToolTipText = "检测到游戏启动后自动应用设置",
        };
        _trayAutoWatchItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAutoWatch(_trayAutoWatchItem.Checked);
            AfterTrayConfigChange("自动解锁");
        };
        menu.Items.Add(_trayAutoWatchItem);

        _trayAutoStartItem = new ToolStripMenuItem("开机自启动")
        {
            CheckOnClick = true,
            Checked = _config.AutoStartWithWindows,
            ToolTipText = "登录 Windows 后在后台运行",
        };
        _trayAutoStartItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _service.SetAutoStartWithWindows(_trayAutoStartItem.Checked);
            AfterTrayConfigChange("开机自启动");
        };
        menu.Items.Add(_trayAutoStartItem);

        _trayStartMinItem = new ToolStripMenuItem("启动后最小化")
        {
            CheckOnClick = true,
            Checked = _config.StartMinimized,
            ToolTipText = "启动时直接驻留系统托盘",
        };
        _trayStartMinItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _config.StartMinimized = _trayStartMinItem.Checked;
            _config.TrySave(out _);
            AfterTrayConfigChange("启动后最小化");
        };
        menu.Items.Add(_trayStartMinItem);

        menu.Items.Add(new ToolStripSeparator());

        _trayFpsRoot = new ToolStripMenuItem($"目标帧率  {_config.TargetFps} FPS");
        menu.Items.Add(_trayFpsRoot);
        BuildTrayFpsItems();

        var fpsCustom = new ToolStripMenuItem("自定义帧率…");
        fpsCustom.Click += (_, _) => ShowCustomFpsDialog();
        menu.Items.Add(fpsCustom);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("启动游戏", null, (_, _) =>
        {
            if (_service.TryLaunchGame(out var msg))
                ShowTrayBalloon("启动游戏", msg, ToolTipIcon.Info);
            else
                ShowTrayBalloon("启动游戏", msg, ToolTipIcon.Warning);
            PushUiAndRefreshTray();
        });

        menu.Items.Add("自动查找游戏路径", null, (_, _) =>
        {
            var r = _service.AutoLocateGamePath();
            if (r.Ok) _config.TrySave(out _);
            ShowTrayBalloon(
                "自动查找",
                r.Ok ? $"已找到\n{r.Path}" : (r.Detail ?? "失败"),
                r.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            PushUiAndRefreshTray();
        });

        menu.Items.Add("手动选择游戏路径…", null, (_, _) =>
        {
            RestoreFromTrayPublic();
            var r = _service.SetGamePathManual(this);
            if (r.Ok)
            {
                _config.TrySave(out _);
                PushUiAndRefreshTray();
                ShowTrayBalloon("游戏路径", r.Path ?? "已更新", ToolTipIcon.Info);
            }
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("查看安全声明…", null, (_, _) =>
        {
            RestoreFromTrayPublic();
            // Web UI 内也有声明；托盘路径用原生对话框保证托盘-only 可用
            SafetyDialog.Show(this, _config, force: true);
            PushUiAndRefreshTray();
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

        _trayLogItem = new ToolStripMenuItem("调试日志")
        {
            CheckOnClick = true,
            Checked = _config.DebugLogging,
            ToolTipText = "记录详细诊断信息",
        };
        _trayLogItem.CheckedChanged += (_, _) =>
        {
            if (_syncingUi) return;
            _config.DebugLogging = _trayLogItem.Checked;
            if (!_config.TrySave(out var logSaveErr))
                AppLog.Error("配置保存失败: " + logSaveErr);
            AppLog.ApplyConfig(_config);
            AfterTrayConfigChange("调试日志");
        };
        menu.Items.Add(_trayLogItem);

        menu.Items.Add("创建/刷新快捷方式", null, (_, _) =>
        {
            try
            {
                ShortcutHelper.CreateAll();
                ShowTrayBalloon("快捷方式", "开始菜单与桌面快捷方式已更新", ToolTipIcon.Info);
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

        // 每次打开菜单：与 UI / 服务状态对齐
        menu.Opening += (_, _) => SyncTrayFromConfig();

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTrayPublic();
        _tray.MouseClick += (_, e) =>
        {
            // 左键单击也恢复主窗口（与常见设计稿桌面工具一致）
            if (e.Button == MouseButtons.Left)
                RestoreFromTrayPublic();
        };

        _service.StateChanged += OnServiceStateForTray;
        UpdateTrayTip();
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
        using var dlg = new Form
        {
            Text = "自定义目标帧率",
            Width = 300,
            Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        UiStyle.ApplyToForm(dlg);
        var label = new Label
        {
            Text = "目标帧率（1 – 540）",
            Left = 20,
            Top = 18,
            AutoSize = true,
        };
        var num = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 540,
            Value = Math.Clamp(_config.TargetFps, 1, 540),
            Left = 20,
            Top = 48,
            Width = 120,
        };
        var ok = new Button { Text = "确定", Left = 160, Top = 46, Width = 90, DialogResult = DialogResult.OK };
        dlg.Controls.Add(label);
        dlg.Controls.Add(num);
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;
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
        _trayFpsRoot.Text = $"目标帧率  {_config.TargetFps} FPS";
        _trayFpsRoot.DropDownItems.Clear();
        foreach (var preset in TrayFpsPresets)
        {
            var p = preset;
            var item = new ToolStripMenuItem($"{p} FPS")
            {
                Checked = _config.TargetFps == p,
                ToolTipText = p == 120 ? "推荐" : null,
            };
            item.Click += (_, _) =>
            {
                _service.ApplyFps(p);
                _config.TrySave(out _);
                PushUiAndRefreshTray();
                ShowTrayBalloon("帧率", $"目标 FPS = {p}", ToolTipIcon.Info);
            };
            _trayFpsRoot.DropDownItems.Add(item);
        }
    }

    private string BuildStatusHeaderText()
    {
        var effective = _config.MasterEnabled && _config.Enabled;
        var pid = _service.AttachedPid;
        if (pid > 0)
            return effective
                ? $"运行中 · PID {pid} · {_config.TargetFps} FPS"
                : $"已附加 · 解锁暂停 · PID {pid}";
        if (!_config.MasterEnabled) return "解锁服务已暂停";
        if (!_config.Enabled) return "帧率解锁已关闭";
        if (_config.AutoWatch) return $"自动监视中 · 目标 {_config.TargetFps} FPS";
        return $"已就绪 · 目标 {_config.TargetFps} FPS";
    }

    private string BuildTrayTipText()
    {
        var effective = _config.MasterEnabled && _config.Enabled ? "开" : "关";
        var watch = _config.AutoWatch ? "监视" : "待命";
        var pid = _service.AttachedPid;
        var core = pid > 0
            ? $"FPS {_config.TargetFps} | {effective} | PID {pid}"
            : $"FPS {_config.TargetFps} | {effective} | {watch}";
        var status = _service.StatusText;
        if (!string.IsNullOrWhiteSpace(status) && status.Length < 28)
            core += " | " + status;
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

    /// <summary>与设计稿 Brand 星形接近的托盘图标（紫调）。</summary>
    private static Icon? CreateBrandTrayIcon()
    {
        try
        {
            const int size = 32;
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                // 圆底
                using (var bg = new SolidBrush(Color.FromArgb(0x16, 0x17, 0x1E)))
                    g.FillEllipse(bg, 1, 1, size - 3, size - 3);
                // 星
                var cx = size / 2f;
                var cy = size / 2f;
                var pts = StarPoints(cx, cy, outer: 12f, inner: 5.2f, points: 4);
                using var fill = new SolidBrush(Color.FromArgb(0xBD, 0xA2, 0xF2));
                g.FillPolygon(fill, pts);
                var pts2 = StarPoints(cx, cy, outer: 6.5f, inner: 2.8f, points: 4);
                using var inner = new SolidBrush(Color.FromArgb(0x15, 0x16, 0x1D));
                g.FillPolygon(inner, pts2);
            }

            var hIcon = bmp.GetHicon();
            // Clone so we can free the temp handle
            using var tmp = Icon.FromHandle(hIcon);
            var clone = (Icon)tmp.Clone();
            NativeMethods.DestroyIcon(hIcon);
            return clone;
        }
        catch (Exception ex)
        {
            AppLog.Debug("tray icon: " + ex.Message);
            return null;
        }
    }

    private static PointF[] StarPoints(float cx, float cy, float outer, float inner, int points)
    {
        var list = new PointF[points * 2];
        var step = MathF.PI / points;
        var angle = -MathF.PI / 2;
        for (var i = 0; i < points * 2; i++)
        {
            var r = (i % 2 == 0) ? outer : inner;
            list[i] = new PointF(cx + r * MathF.Cos(angle), cy + r * MathF.Sin(angle));
            angle += step;
        }
        return list;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
