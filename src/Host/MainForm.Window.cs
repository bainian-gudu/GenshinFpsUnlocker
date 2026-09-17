using System.Runtime.InteropServices;

namespace GenshinFpsUnlocker.Host;

/// <summary>窗口与托盘切换：启动进托盘、隐藏 / 恢复、屏幕内校正、原生激活与提权重启。</summary>
internal sealed partial class MainForm : Form
{
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    /// <summary>Explorer 重启或登录后就绪时，重新注册托盘图标；标题栏最小化 → 托盘。</summary>
    protected override void WndProc(ref Message m)
    {
        // 拦截标题栏 ▁ 的系统最小化命令：直接进托盘，窗口永不进入 Minimized 态。
        // 否则 Resize 兜底路径会在「已最小化 + 摘除任务栏」的窗口上触发 RecreateHandle，
        // Windows 把这个瞬时窗口以遗留「最小化图标条」画在工作区左下角并闪现一帧。
        // （退出 / 启动进托盘阶段不拦；Win+↓、任务栏「最小化所有窗口」等不发
        //   SC_MINIMIZE 的旁路仍由 Resize 事件兜底。）
        const int wmSyscommand = 0x0112; // WM_SYSCOMMAND
        const int scMinimize = 0xF020;   // SC_MINIMIZE
        if (m.Msg == wmSyscommand
            && (m.WParam.ToInt32() & 0xFFF0) == scMinimize
            && !_reallyExit && !_startupTrayPending)
        {
            HideToTrayPublic(showTip: true, fromStartup: false);
            return; // 不交给 base → 永远不发生系统最小化
        }

        if (m.Msg == TaskbarCreatedMessage && _tray is not null && !IsDisposed)
        {
            try
            {
                _tray.Visible = false;
                _tray.Visible = true;
                UpdateTrayTip();
                AppLog.Info("Explorer 通知区域已重置，托盘图标已重新注册");
            }
            catch (Exception ex)
            {
                AppLog.Debug("TaskbarCreated 托盘恢复: " + ex.Message);
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// 启动托盘：工具窗口 + 不激活，进一步降低任务栏/动画闪现。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // 注意：base 构造期 _config 可能尚未赋值，只看 _allowVisible（字段初值 true，ctor 里 StartMinimized 时改 false）
            if (!_allowVisible)
            {
                const int wsExToolwindow = 0x00000080;
                const int wsExNoactivate = 0x08000000;
                cp.ExStyle |= wsExToolwindow | wsExNoactivate;
            }
            return cp;
        }
    }

    /// <summary>
    /// 拦截启动期 Show：StartMinimized 时只创建句柄、不把窗口画到屏幕上。
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!_allowVisible)
        {
            if (!IsHandleCreated)
            {
                try { CreateHandle(); } catch { /* ignore */ }
            }
            // 即便框架强制 Show，也立刻 SW_HIDE + 屏外
            value = false;
            try
            {
                if (IsHandleCreated)
                    ShowWindow(Handle, 0);
            }
            catch { /* ignore */ }
        }
        base.SetVisibleCore(value);
    }

    /// <summary>
    /// 启动配置为进托盘：尽早调用。窗口保持隐藏/屏外，用户看不到主界面。
    /// 一次性：本进程只会真正执行一次，之后（用户主动打开主窗后）任何兜底调用都直接忽略，
    /// 避免出现「界面弹出 → 又被自动藏回托盘」。
    /// </summary>
    private void FinishStartupToTray()
    {
        if (_reallyExit || IsDisposed) return;

        // 已完成过「启动进托盘」→ 现在窗口若可见，一定是用户主动打开的，不得再藏
        if (_startupTrayDone)
        {
            if (_startupTrayPending)
                AppLog.Warn("FinishStartupToTray 被重复调用（启动阶段已结束）— 忽略");
            _startupTrayPending = false;
            return;
        }

        _startupTrayDone = true;
        _startupTrayPending = false;
        _allowVisible = false; // 仍禁止误 Show，直到用户点「显示主界面」
        _inTray = true;
        _suppressResizeHide = true;
        try
        {
            ShowInTaskbar = false;
            try
            {
                if (!_hasRestoreLocation)
                {
                    // 记下一个合理的恢复位置（屏幕中央），不要用 -32000
                    var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
                    _restoreLocation = new Point(
                        screen.Left + Math.Max(0, (screen.Width - Width) / 2),
                        screen.Top + Math.Max(0, (screen.Height - Height) / 2));
                    _hasRestoreLocation = true;
                }
            }
            catch { /* ignore */ }

            try
            {
                if (IsHandleCreated)
                    ShowWindow(Handle, 0); // SW_HIDE
            }
            catch { /* ignore */ }
            try { Hide(); } catch { /* ignore */ }
            try
            {
                if (WindowState != FormWindowState.Normal)
                    WindowState = FormWindowState.Normal;
            }
            catch { /* ignore */ }
            // 保持透明+屏外，直到用户主动恢复（恢复时再 Opacity=1 / 复位 Location）
            try { if (Opacity > 0.01) Opacity = 0; } catch { /* ignore */ }
            try { if (Location.X > -10000) Location = new Point(-32000, -32000); } catch { /* ignore */ }

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

            if (!_trayTipShownThisSession)
            {
                _trayTipShownThisSession = true;
                ShowTrayBalloon(
                    AppPaths.ProductDisplayName,
                    "已在后台运行。若托盘区看不到图标，请点任务栏 ^ 展开「显示隐藏的图标」。左键打开主窗口，右键可设置。");
            }
            AppLog.Info("startup → tray (no flash)");
        }
        finally
        {
            _suppressResizeHide = false;
        }
    }

    /// <summary>
    /// 隐藏到托盘：不占任务栏。禁止用 Opacity=0（恢复后易残留透明 → 任务栏有图标、桌面无窗）。
    /// </summary>
    public void HideToTrayPublic(bool showTip = false, bool fromStartup = false)
    {
        if (_reallyExit || IsDisposed) return;
        // 已在托盘则不再走一遍
        if (_inTray && !Visible && !_startupTrayPending) return;

        void work()
        {
            if (_reallyExit || IsDisposed) return;
            if (_hidingToTray) return;
            if (_inTray && !Visible && !_startupTrayPending) return;

            // 启动路径优先走无闪现逻辑；启动进托盘已完成过则走常规隐藏
            if ((fromStartup || _startupTrayPending) && !_startupTrayDone)
            {
                FinishStartupToTray();
                return;
            }

            _hidingToTray = true;
            _suppressResizeHide = true;
            try
            {
                _inTray = true;
                _allowVisible = false;

                // 保证不残留透明（历史路径 / 异常）
                try { Opacity = 1; } catch { /* ignore */ }

                // 先藏窗再摘任务栏：Hide 即时隐藏、无最小化动画；
                // 且 ShowInTaskbar 变更触发 RecreateHandle 时窗口已不可见，
                // 不会在左下角造出「最小化图标条」残影。
                Hide();
                ShowInTaskbar = false;

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
                        "已在后台运行。左键单击或双击托盘图标可打开主窗口；右键可调整设置。");
                }

                // 复位界面页签：下次从托盘打开停在「游戏概览」，而不是上次浏览的页面
                _bridge.ResetUiPage();

                AppLog.Info("window → tray");
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
                _startupTrayPending = false;
                _allowVisible = true; // 允许 SetVisibleCore 真正显示

                // 1) 彻底取消透明 / 最小化 / 屏外残留
                try { Opacity = 1; } catch { /* ignore */ }
                try
                {
                    if (WindowState != FormWindowState.Normal)
                        WindowState = FormWindowState.Normal;
                }
                catch { /* ignore */ }
                try
                {
                    if (_hasRestoreLocation)
                        Location = _restoreLocation;
                    else if (Location.X < -1000 || Location.Y < -1000)
                    {
                        StartPosition = FormStartPosition.CenterScreen;
                        // 触发一次居中：先放到工作区中心
                        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
                        Location = new Point(
                            screen.Left + Math.Max(0, (screen.Width - Width) / 2),
                            screen.Top + Math.Max(0, (screen.Height - Height) / 2));
                    }
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

        // SW_SHOWNA=8（显示但不激活）/ SW_RESTORE=9（还原被最小化的窗口）/ SW_SHOW=5（显示）
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
}
