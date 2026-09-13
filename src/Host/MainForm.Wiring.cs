namespace GenshinFpsUnlocker.Host;

/// <summary>窗体构造期的事件接线与初始化步骤（自 <c>MainForm</c> 构造函数拆出，逻辑未变）。</summary>
internal sealed partial class MainForm
{
    private void WireTrayFallback()
    {
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

        StartTrayRecoveryTimer();
    }

    /// <summary>
    /// 登录时 Explorer 的通知区域可能晚于本进程启动；短时间重注册图标，
    /// 避免 NotifyIcon 首次消息被 Shell 丢弃后长时间不可见。
    /// </summary>
    private void StartTrayRecoveryTimer()
    {
        if (_trayRecoveryTimer is not null) return;
        var attempts = 0;
        _trayRecoveryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _trayRecoveryTimer.Tick += (_, _) =>
        {
            if (IsDisposed || _tray is null)
            {
                _trayRecoveryTimer?.Stop();
                return;
            }

            attempts++;
            try
            {
                // 启动后的 3 分钟内按递增间隔重注册；避免首次 NIM_ADD 被 Shell 丢弃。
                // 前 10 秒每 2 秒尝试，之后降低频率，既覆盖慢启动又避免图标闪烁。
                var retryNow = attempts <= 5 || attempts % 5 == 0;
                if (attempts <= 90 && retryNow)
                {
                    _tray.Visible = false;
                    _tray.Visible = true;
                    UpdateTrayTip();
                }
                if (attempts >= 90)
                    _trayRecoveryTimer.Stop();
            }
            catch (Exception ex)
            {
                AppLog.Debug("托盘启动恢复: " + ex.Message);
            }
        };
        _trayRecoveryTimer.Start();
    }

    private void WireStartupToTray()
    {
        // 启动即进托盘：不等 WebView（其初始化可达数秒，否则用户会看到空窗闪现）
        if (_startupTrayPending)
        {
            HandleCreated += (_, _) =>
            {
                try
                {
                    // 句柄一出即强制隐藏（兜底 SetVisibleCore）
                    if (IsHandleCreated)
                        ShowWindow(Handle, 0); // SW_HIDE
                }
                catch { /* ignore */ }

                // 修复「无法启动：在创建窗口句柄之前，不能在控件上调用 Invoke 或 BeginInvoke」：
                // 原实现在构造函数里直接 BeginInvoke，此时窗口句柄尚未创建
                // （Application.Run → SetVisibleCore → CreateHandle 才创建），
                // StartMinimized（启动后最小化/开机自启最小化/--minimized）时必定抛
                // InvalidOperationException 并被 Program.Main 兜底捕获成「无法启动」弹窗。
                // 正确做法：等 HandleCreated 事件（句柄已就绪）后再投递到消息队列。
                if (!_startupTrayPending) return; // 句柄重建时不重复执行
                try
                {
                    BeginInvoke(() =>
                    {
                        try { FinishStartupToTray(); }
                        catch (Exception ex) { AppLog.Warn("early FinishStartupToTray: " + ex.Message); }
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Warn("early tray BeginInvoke: " + ex.Message);
                    // 兜底：消息队列不可用时直接完成，保证托盘图标一定出现
                    try { FinishStartupToTray(); }
                    catch (Exception ex2) { AppLog.Warn("early FinishStartupToTray(direct): " + ex2.Message); }
                }
            };
        }

        // 窗体句柄就绪后再强制刷新一次托盘可见性（部分环境构造阶段 Visible 会被吞）
        Shown += (_, _) =>
        {
            try
            {
                // 启动托盘模式：Shown 不应出现；若仍处于启动阶段则立刻藏。
                // 注意：判定必须用「启动阶段」标志，不能用 _config.StartMinimized ——
                // 后者是持久化的用户偏好，用户之后从托盘打开主窗时它仍为 true，
                // 旧代码据此把刚弹出的界面又藏回托盘（一闪即最小化）。
                if (_startupTrayPending || (_inTray && !_allowVisible))
                {
                    try { FinishStartupToTray(); } catch { /* ignore */ }
                    return;
                }

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
    }

    private void WireInstanceWake()
    {
        // 二次启动快捷方式 → 唤醒本实例
        _wakeCts = new CancellationTokenSource();
        InstanceWake.StartListener(() =>
        {
            try
            {
                if (IsDisposed || _reallyExit) return;
                // 句柄可能还没创建（构造函数里就起了监听线程，而句柄要等
                // Application.Run → SetVisibleCore → CreateHandle）。
                // 此时 BeginInvoke 会抛 InvalidOperationException「在创建窗口句柄之前，
                // 不能在控件上调用 Invoke 或 BeginInvoke」，被外层 catch 吞掉后
                // 表现为「双击快捷方式没反应」。这里在监听线程上等句柄就绪再投递。
                for (var i = 0; i < 100 && !IsHandleCreated && !IsDisposed && !_reallyExit; i++)
                    Thread.Sleep(100);
                if (!IsHandleCreated || IsDisposed || _reallyExit) return;
                BeginInvoke(() =>
                {
                    try
                    {
                        // 用户主动再点快捷方式：打开主界面（比仅弹「已在运行」更合理）
                        RestoreFromTrayPublic();
                        ShowTrayBalloon(AppPaths.ProductDisplayName, "主窗口已打开。");
                    }
                    catch (Exception ex) { AppLog.Warn("wake restore: " + ex.Message); }
                });
            }
            catch { /* ignore */ }
        }, _wakeCts.Token);
    }

    private void WireLoadHandler()
    {
        Load += async (_, _) =>
        {
            var webOk = false;
            try
            {
                InitializeWebControls();
                await InitializeWebAsync();
                webOk = true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "WebView2 初始化失败");
                try { ShowNativeFallbackUi(ex.Message); } catch { /* ignore */ }
                // 仍在托盘后台（含启动进托盘阶段）：不要 MessageBox 抢焦点；托盘气球即可
                if (!_inTray && !_startupTrayPending)
                {
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
                else
                {
                    try
                    {
                        ShowTrayBalloon(
                            AppPaths.ProductDisplayName,
                            "界面引擎加载失败，可在托盘右键进行基本设置。");
                    }
                    catch { /* ignore */ }
                }
            }

            // 启动托盘兜底：只在「启动进托盘」阶段才允许隐藏。
            //
            // 【本次修复】旧条件含 _config.StartMinimized，而它是持久化偏好：
            // StartMinimized=true 时主窗句柄在启动阶段被 SetVisibleCore 拦住，
            // Load 事件其实一直不触发，直到用户第一次从托盘打开主窗（或二次点快捷方式
            // 唤醒本实例）才真正 Show → Load → await WebView2 初始化。
            // 此时旧条件依旧为真，于是又调 FinishStartupToTray() 把刚弹出的界面藏回去，
            // 表现为「启动软件会弹出界面，然后自己最小化」。
            // 现在只认 _startupTrayPending（阶段标志），并叠加 FinishStartupToTray 的一次性守卫。
            if (_startupTrayPending)
            {
                BeginInvoke(() =>
                {
                    try { FinishStartupToTray(); }
                    catch (Exception ex)
                    {
                        AppLog.Warn("startup tray: " + ex.Message);
                    }
                });
            }
            else if (!webOk)
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        // 不再改写 _config.StartMinimized：那是用户的持久化偏好，
                        // WebView2 加载失败不应把它悄悄关掉（下次启动行为被改）。
                        _startupTrayPending = false;
                        _allowVisible = true;
                        RestoreFromTrayPublic();
                        if (_tray is not null)
                        {
                            _tray.Visible = true;
                            ShowTrayBalloon(
                                AppPaths.ProductDisplayName,
                                "界面加载异常，已保留窗口与托盘。右键托盘可调整设置。");
                        }
                    }
                    catch { /* ignore */ }
                });
            }
            else
            {
                BeginInvoke(() =>
                {
                    try
                    {
                        _startupTrayPending = false;
                        _allowVisible = true;
                        if (!Visible || WindowState == FormWindowState.Minimized)
                            RestoreFromTrayPublic();
                    }
                    catch { /* ignore */ }
                });
            }
        };
    }

    private void WireWindowEvents()
    {
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
            try { _wakeCts?.Cancel(); } catch { /* ignore */ }
            try { _wakeCts?.Dispose(); } catch { /* ignore */ }
            try { _trayRecoveryTimer?.Stop(); _trayRecoveryTimer?.Dispose(); } catch { /* ignore */ }
            try { _service.StateChanged -= OnServiceStateForTray; } catch { /* ignore */ }
            try { _tray.Visible = false; } catch { /* ignore */ }
            try { (_trayMenu?.Renderer as IDisposable)?.Dispose(); } catch { /* ignore */ }
            try { _tray.Dispose(); } catch { /* ignore */ }
            try { _trayIconOwned?.Dispose(); } catch { /* ignore */ }
            try { _bridge.Dispose(); } catch { /* ignore */ }
            try { _webView?.Dispose(); } catch { /* ignore */ }
        };
    }
}
