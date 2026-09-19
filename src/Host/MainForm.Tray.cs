namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 系统托盘：精简右键菜单，按「当前游戏 → 窗口与游戏操作 → 帧率解锁组 →
/// 画面效果组 → 退出」的顺序排列；外观跟随 Web UI 深浅色，避免系统默认灰白菜单。
/// 菜单里的开关一律作用于「当前游戏」，切换游戏后文案与勾选跟着换。
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>与 Web UI FpsControl 预设一致（仅自定义帧率的游戏使用）。</summary>
    private static readonly int[] TrayFpsPresets = [60, 90, 120, 144, 165, 240];

    private ToolStripMenuItem? _trayStatusItem;
    private ToolStripMenuItem? _trayGameRoot;
    private ToolStripMenuItem? _trayGameGenshinItem;
    private ToolStripMenuItem? _trayGameStarRailItem;
    private ToolStripMenuItem? _trayLaunchItem;
    private ToolStripMenuItem? _trayEnabledItem;
    private ToolStripMenuItem? _trayAutoWatchItem;
    private ToolStripMenuItem? _trayAntiBlurPerspectiveItem;
    private ToolStripMenuItem? _trayAntiBlurDiveMosaicItem;
    private ToolStripMenuItem? _trayHideUidItem;
    private ToolStripMenuItem? _trayFpsRoot;
    private ContextMenuStrip? _trayMenu;
    private Icon? _trayIconOwned;
    private bool _trayTipShownThisSession;
    /// <summary>帧率子菜单当前是按哪款游戏构建的（切换游戏时要重建）。</summary>
    private GameId? _trayFpsBuiltFor;
    /// <summary>主窗是否已藏入托盘（气泡/提示文案用）。</summary>
    private bool _inTray;

    /// <summary>当前正在配置的游戏描述与档案。</summary>
    private GameDescriptor ActiveGameDescriptor => GameCatalog.Get(_config.ActiveGame);
    private GameProfile ActiveGameProfile => _config.Profile(_config.ActiveGame);

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
        var descriptor = ActiveGameDescriptor;
        var profile = ActiveGameProfile;
        var effective = _config.MasterEnabled && profile.Enabled;
        var pid = _service.AttachedPid;
        if (pid > 0 && _service.AttachedGame == descriptor.Id)
            return effective
                ? $"{descriptor.ShortName}  ·  PID {pid}  ·  {profile.TargetFps} FPS"
                : $"{descriptor.ShortName}  ·  已附加  ·  解锁已关";
        if (!_config.MasterEnabled) return $"{descriptor.ShortName}  ·  解锁服务已暂停";
        if (!profile.Enabled)
            return descriptor.FpsViaRegistry
                ? $"{descriptor.ShortName}  ·  帧率解锁已关闭（未写注册表）"
                : $"{descriptor.ShortName}  ·  帧率解锁已关闭  ·  {profile.TargetFps} FPS";
        if (descriptor.FpsViaRegistry)
            return $"{descriptor.ShortName}  ·  注册表解锁  ·  固定 {profile.TargetFps} FPS";
        if (_config.AutoWatch) return $"{descriptor.ShortName}  ·  自动监视中  ·  {profile.TargetFps} FPS";
        return $"{descriptor.ShortName}  ·  已就绪  ·  {profile.TargetFps} FPS";
    }

    /// <summary>
    /// 托盘悬停提示：首行固定产品名（先让人确认「它是谁」），次行一句可读状态，
    /// 第三行是当前游戏的画面效果开关与就绪状态。
    /// 提示总长受 NotifyIcon.Text 限制（63 字符，含换行），因此注入行按「只列已开启
    /// 的功能 + 附着后带就绪标记」写，最长也留得下。
    /// </summary>
    private string BuildTrayTipText()
    {
        var descriptor = ActiveGameDescriptor;
        var profile = ActiveGameProfile;
        var unlock = _config.MasterEnabled && profile.Enabled;
        var pid = _service.AttachedPid;

        var feedback = 0;
        if (pid > 0 && _service.AttachedGame == descriptor.Id)
        {
            try { feedback = _service.CurrentFpsFeedback; } catch { /* ignore */ }
        }

        string status;
        if (pid > 0 && _service.AttachedGame == descriptor.Id)
        {
            status = !unlock
                ? $"{descriptor.ShortName} 已附着 · 解锁已暂停"
                : feedback > 0
                    ? $"{descriptor.ShortName} 当前 {feedback} → 目标 {profile.TargetFps} FPS"
                    : $"{descriptor.ShortName} 已附着 · 目标 {profile.TargetFps} FPS";
        }
        else if (!_config.MasterEnabled)
        {
            status = $"{descriptor.ShortName} · 解锁已暂停";
        }
        else if (!profile.Enabled)
        {
            status = descriptor.FpsViaRegistry
                ? $"{descriptor.ShortName} · 解锁已关闭"
                : $"{descriptor.ShortName} · 解锁已关闭 · {profile.TargetFps} FPS";
        }
        else if (descriptor.FpsViaRegistry)
        {
            status = $"{descriptor.ShortName} · 注册表 {profile.TargetFps} FPS";
        }
        else
        {
            status = _config.AutoWatch
                ? $"{descriptor.ShortName} · 监视中 · {profile.TargetFps} FPS"
                : $"{descriptor.ShortName} · 待命中 · {profile.TargetFps} FPS";
        }

        var text = AppPaths.ProductDisplayName + Environment.NewLine + status;
        var injection = BuildInjectionTipLine(pid);
        if (injection.Length > 0)
            text += Environment.NewLine + injection;

        return Truncate(text, 63);
    }

    /// <summary>
    /// 当前游戏画面效果的一行状态：只列已开启的功能；附着且功能生效时按 Stub 回报的
    /// 位掩码标注就绪情况（✓ 已就绪 / … 等待适配或尚未生效），未附着时只报配置。
    /// 总开关或自动监视关闭时注入不会下发，直接报「注入已暂停」。
    /// </summary>
    private string BuildInjectionTipLine(int pid)
    {
        var descriptor = ActiveGameDescriptor;
        var profile = ActiveGameProfile;
        var anyEnabled = profile.AntiBlurPerspective || profile.HideUid
                         || (descriptor.SupportsDiveMosaic && profile.AntiBlurDiveMosaic);
        if (!anyEnabled) return string.Empty;

        var active = _config.MasterEnabled && _config.AutoWatch;
        if (!active) return "注入已暂停";

        var attached = pid > 0 && _service.AttachedGame == descriptor.Id;
        var antiBlurMask = 0;
        var hideUidMask = 0;
        if (attached)
        {
            try
            {
                antiBlurMask = _service.AntiBlurStateFeedback;
                hideUidMask = _service.HideUidStateFeedback;
            }
            catch { /* 读共享内存失败时按「未就绪」显示，不打断托盘提示 */ }
        }

        // 位定义见 src/Common/IpcData.h：AntiBlurState bit0=虚化就绪 / bit1=马赛克就绪，
        // HideUidState bit0=就绪。功能名称按游戏自己的叫法显示。
        var items = new List<string>(3);
        if (profile.AntiBlurPerspective)
            items.Add(FormatInjectionItem(descriptor.SupportsDiveMosaic ? "反虚化" : "解除虚化", attached, (antiBlurMask & 1) != 0));
        if (descriptor.SupportsDiveMosaic && profile.AntiBlurDiveMosaic)
            items.Add(FormatInjectionItem("马赛克", attached, (antiBlurMask & 2) != 0));
        if (profile.HideUid)
            items.Add(FormatInjectionItem("UID", attached, (hideUidMask & 1) != 0));

        return string.Join(" · ", items);
    }

    /// <summary>单个注入功能的显示名：未附着不带标记，附着后按就绪情况带 ✓ / …。</summary>
    private static string FormatInjectionItem(string name, bool attached, bool ready)
        => !attached ? name : ready ? name + "✓" : name + "…";

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

    /// <summary>
    /// 托盘菜单 / Web 改配置后：当前游戏、勾选、功能文案、FPS 子菜单、
    /// 提示全文与状态头全部对齐界面。
    /// </summary>
    public void SyncTrayFromConfig()
    {
        if (IsDisposed) return;

        void work()
        {
            _syncingUi = true;
            try
            {
                var descriptor = ActiveGameDescriptor;
                var profile = ActiveGameProfile;

                if (_trayStatusItem is not null)
                    _trayStatusItem.Text = BuildStatusHeaderText();

                if (_trayGameRoot is not null)
                    _trayGameRoot.Text = $"当前游戏  ·  {descriptor.ShortName}";
                if (_trayGameGenshinItem is not null)
                    _trayGameGenshinItem.Checked = _config.ActiveGame == GameId.Genshin;
                if (_trayGameStarRailItem is not null)
                    _trayGameStarRailItem.Checked = _config.ActiveGame == GameId.StarRail;
                if (_trayLaunchItem is not null)
                    _trayLaunchItem.Text = $"启动{descriptor.DisplayName}";

                if (_trayEnabledItem is not null)
                {
                    _trayEnabledItem.Checked = profile.Enabled;
                    _trayEnabledItem.ToolTipText = descriptor.FpsViaRegistry
                        ? "开启后检查注册表：已经是 120 FPS 就不覆盖，否则写入 120"
                        : "开启后按目标帧率注入；关闭则暂停解锁";
                    // 总开关关闭时仍允许改勾选，但状态头会提示暂停
                    _trayEnabledItem.Enabled = true;
                }
                if (_trayAutoWatchItem is not null)
                    _trayAutoWatchItem.Checked = _config.AutoWatch;

                // 画面效果项：文案随游戏自己的模块命名，星穹铁道没有「水下马赛克」。
                if (_trayAntiBlurPerspectiveItem is not null)
                {
                    _trayAntiBlurPerspectiveItem.Checked = profile.AntiBlurPerspective;
                    _trayAntiBlurPerspectiveItem.Text = descriptor.SupportsDiveMosaic ? "反角色虚化" : "解除角色虚化";
                    _trayAntiBlurPerspectiveItem.ToolTipText = descriptor.SupportsDiveMosaic
                        ? "镜头拉近时角色不再透明化（仅供单机体验）"
                        : "镜头拉近时角色不再透明化（由 StarRailStub.dll 提供）";
                }
                if (_trayAntiBlurDiveMosaicItem is not null)
                {
                    _trayAntiBlurDiveMosaicItem.Checked = profile.AntiBlurDiveMosaic;
                    _trayAntiBlurDiveMosaicItem.Visible = descriptor.SupportsDiveMosaic;
                }
                if (_trayHideUidItem is not null)
                {
                    _trayHideUidItem.Checked = profile.HideUid;
                    _trayHideUidItem.Text = descriptor.SupportsDiveMosaic ? "隐藏 UID" : "隐藏 UID 水印";
                    _trayHideUidItem.ToolTipText = descriptor.SupportsDiveMosaic
                        ? "隐藏水印与资料页上的 UID 文本（仅供单机体验）"
                        : "隐藏星穹铁道界面上的 UID 水印文本（仅供单机体验）";
                }

                if (_trayFpsRoot is not null)
                {
                    _trayFpsRoot.ToolTipText = descriptor.FpsViaRegistry
                        ? $"注册表：{_service.StarRailRegistryStatus}"
                        : "选择预设或自定义目标帧率";
                    if (_trayFpsBuiltFor != _config.ActiveGame)
                    {
                        BuildTrayFpsItems();
                    }
                    else
                    {
                        _trayFpsRoot.Text = descriptor.FpsViaRegistry
                            ? $"帧率  ·  固定 {profile.TargetFps} FPS"
                            : $"修改帧率  ·  {profile.TargetFps} FPS";
                        foreach (ToolStripItem it in _trayFpsRoot.DropDownItems)
                        {
                            if (it is not ToolStripMenuItem mi) continue;
                            // "120 FPS  · 推荐" / "60 FPS"
                            var txt = mi.Text ?? "";
                            var numPart = txt.Split(' ')[0];
                            if (int.TryParse(numPart, out var fps))
                                mi.Checked = fps == profile.TargetFps;
                        }
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
