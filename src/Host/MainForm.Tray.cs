namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 系统托盘：精简右键菜单，按「窗口与游戏操作 → 帧率解锁组 → 反虚化组 → 退出」
/// 的顺序排列；外观跟随 Web UI 深浅色，避免系统默认灰白菜单。
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>与 Web UI FpsControl 预设一致。</summary>
    private static readonly int[] TrayFpsPresets = [60, 90, 120, 144, 165, 240];

    private ToolStripMenuItem? _trayStatusItem;
    private ToolStripMenuItem? _trayEnabledItem;
    private ToolStripMenuItem? _trayAutoWatchItem;
    private ToolStripMenuItem? _trayAntiBlurPerspectiveItem;
    private ToolStripMenuItem? _trayAntiBlurDiveMosaicItem;
    private ToolStripMenuItem? _trayUpscalerEnabledItem;
    private ToolStripMenuItem? _trayUpscalerModeRoot;
    private ToolStripMenuItem? _trayUpscalerQualityRoot;
    private ToolStripMenuItem? _trayFpsRoot;
    private ContextMenuStrip? _trayMenu;
    private Icon? _trayIconOwned;
    private bool _trayTipShownThisSession;
    /// <summary>主窗是否已藏入托盘（气泡/提示文案用）。</summary>
    private bool _inTray;

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

    private string BuildStatusHeaderText()
    {
        var effective = _config.MasterEnabled && _config.Enabled;
        var pid = _service.AttachedPid;
        var upscaler = BuildUpscalerStatusText();
        if (pid > 0)
            return effective
                ? $"运行中  ·  PID {pid}  ·  {_config.TargetFps} FPS  ·  {upscaler}"
                : $"已附加  ·  解锁已关  ·  PID {pid}  ·  {upscaler}";
        if (!_config.MasterEnabled) return $"解锁服务已暂停  ·  {upscaler}";
        if (!_config.Enabled) return $"帧率解锁已关闭  ·  {_config.TargetFps} FPS  ·  {upscaler}";
        if (_config.AutoWatch) return $"自动监视中  ·  {_config.TargetFps} FPS  ·  {upscaler}";
        return $"已就绪  ·  {_config.TargetFps} FPS  ·  {upscaler}";
    }

    private string BuildUpscalerStatusText()
    {
        var state = _service.UpscalerState;
        if (!state.Enabled) return "超分关闭";
        if (!state.Available) return "超分不可用";
        if (!state.Active) return "超分待命";
        if (state.Status.Contains("已确认", StringComparison.Ordinal)) return "超分已生效";
        if (state.Status.Contains("失败", StringComparison.Ordinal)
            || state.Status.Contains("未检测到", StringComparison.Ordinal)) return "超分未生效";
        return "超分已加载";
    }

    private string BuildTrayTipText()
    {
        var effective = _config.MasterEnabled && _config.Enabled ? "开" : "关";
        var watch = _config.AutoWatch ? "监视" : "待命";
        var pid = _service.AttachedPid;
        var upscaler = BuildUpscalerStatusText();
        var mode = _inTray ? "托盘" : "窗口";
        var core = pid > 0
            ? $"FPS {_config.TargetFps} | {effective} | PID {pid} | {upscaler} | {mode}"
            : $"FPS {_config.TargetFps} | {effective} | {watch} | {upscaler} | {mode}";
        return Truncate(core, 63);
    }

    /// <summary>
    /// 弹一条 Windows 通知（Win10/11 上即操作中心 Toast）。
    /// 图标固定为信息类型：这些提示只是状态告知，用警告图标会在操作中心里
    /// 显示成黄色感叹号，让人误以为出了问题。
    /// </summary>
    private void ShowTrayBalloon(string title, string text)
    {
        try
        {
            if (ToastNotifications.TryShow(title, text))
                return;

            // 旧系统或通知服务被禁用时保留兼容提示，避免状态变化完全无反馈。
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(2200);
        }
        catch { /* ignore */ }
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
                if (_trayAntiBlurPerspectiveItem is not null)
                    _trayAntiBlurPerspectiveItem.Checked = _config.AntiBlurPerspective;
                if (_trayAntiBlurDiveMosaicItem is not null)
                    _trayAntiBlurDiveMosaicItem.Checked = _config.AntiBlurDiveMosaic;
                if (_trayUpscalerEnabledItem is not null)
                    _trayUpscalerEnabledItem.Checked = _config.UpscalerReplacementEnabled;

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
                if (_trayUpscalerModeRoot is not null)
                {
                    _trayUpscalerModeRoot.Text = $"DLSS 版本  ·  {UpscalerModeLabel(_config.UpscalerMode)}";
                    foreach (ToolStripItem it in _trayUpscalerModeRoot.DropDownItems)
                    {
                        if (it is ToolStripMenuItem mi && mi.Tag is string mode)
                            mi.Checked = string.Equals(mode, _config.UpscalerMode, StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (_trayUpscalerQualityRoot is not null)
                {
                    _trayUpscalerQualityRoot.Text = $"超分挡位  ·  {UpscalerQualityLabel(_config.UpscalerQuality)}";
                    foreach (ToolStripItem it in _trayUpscalerQualityRoot.DropDownItems)
                    {
                        if (it is ToolStripMenuItem mi && mi.Tag is string quality)
                            mi.Checked = string.Equals(quality, _config.UpscalerQuality, StringComparison.OrdinalIgnoreCase);
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

    private static string UpscalerQualityLabel(string quality) => quality switch
    {
        "nativeAA" => "DLAA",
        "balanced" => "均衡",
        "performance" => "性能",
        "ultraPerformance" => "超高性能",
        _ => "质量",
    };

    private static string UpscalerModeLabel(string mode) => mode switch
    {
        "dlss5" => "DLSS 5",
        _ => "DLSS 4",
    };

}
