namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 进程入口：单实例、运行时检测、启动监视服务与主窗体。
/// 清单为 asInvoker：非管理员日常启动不弹 UAC，必须能显示主窗 + 托盘。
///
/// 安装 / 卸载只有一种实现：Kachina 安装器（installer/ 打包出的
/// GenshinFpsUnlocker.Install.{ver}.exe，安装目录内自带 *.uninst.exe / *.update.exe）。
/// 宿主自身不再提供 --install / --uninstall、Uninstall.cmd 垫片、自写 ARP 卸载项、
/// 内置白名单删目录等任何「第二种安装卸载方式」；本进程也不会为安装目的主动提权。
/// </summary>
internal static class Program
{
    /// <summary>当前持有的单实例互斥；提权重启前需释放。</summary>
    private static SingleInstance? _activeInstance;

    /// <summary>
    /// 旧版本内置安装逻辑写下的安装标记文件名。
    /// 内置安装/卸载已移除（统一走 Kachina），这里仅用于向后兼容地识别历史安装副本，
    /// 不再创建该文件。
    /// </summary>
    private const string LegacyInstallMarkerFileName = "GenshinFpsUnlocker.install";

    /// <summary>释放单实例锁，供「以管理员重新启动」在拉起新进程前调用。</summary>
    internal static void ReleaseSingleInstance()
    {
        var inst = Interlocked.Exchange(ref _activeInstance, null);
        if (inst is null) return;
        try { inst.Dispose(); }
        catch (Exception ex) { AppLog.Warn("ReleaseSingleInstance dispose: " + ex.Message); }
    }

    /// <summary>
    /// 把单实例锁重新拿回来。提权重启失败（用户在 UAC 点「否」）时必须调用：
    /// 锁已经释放但本进程还在跑，此时用户再点一次快捷方式就会双开，
    /// 两个 Host 同时写共享内存、同时对同一个游戏进程注入。
    /// </summary>
    internal static bool ReacquireSingleInstance()
    {
        if (_activeInstance is not null) return true;

        var inst = new SingleInstance();
        if (!inst.TryAcquire())
        {
            inst.Dispose();
            AppLog.Warn("ReacquireSingleInstance: 已被其它实例占用");
            return false;
        }

        _activeInstance = inst;
        AppLog.Info("single-instance reacquired: " + (inst.Name ?? "(none)"));
        return true;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // 顶层兜底：任何未捕获异常都弹窗，禁止「双击无反应」
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            try { AppLog.Error(e.Exception, "UI ThreadException"); } catch { /* ignore */ }
            try
            {
                MessageBox.Show(
                    "界面线程异常：\n" + e.Exception.Message +
                    "\n\n日志：%LocalAppData%\\GenshinFpsUnlocker\\logs\\",
                    AppPaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { /* ignore */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            try { if (ex is not null) AppLog.Error(ex, "UnhandledException"); } catch { /* ignore */ }
            try
            {
                MessageBox.Show(
                    "启动失败：\n" + (ex?.Message ?? e.ExceptionObject?.ToString() ?? "unknown") +
                    "\n\n若以标准用户运行，请确认已安装 .NET Desktop Runtime 9 与 WebView2。\n" +
                    "日志：%LocalAppData%\\GenshinFpsUnlocker\\logs\\",
                    "原神帧率解锁",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { /* ignore */ }
        };

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            try { AppLog.Error(ex, "Main"); } catch { /* ignore */ }
            try
            {
                MessageBox.Show(
                    "无法启动：\n" + ex.Message +
                    "\n\n" + ex.GetType().FullName +
                    "\n\n日志目录：\n%LocalAppData%\\GenshinFpsUnlocker\\logs\\",
                    "原神帧率解锁",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { /* ignore */ }
        }
    }

    private static void Run(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        UiStyle.ApplyApplicationTheme();
        ApplicationConfiguration.Initialize();

        var quiet = args.Any(a => a is "--quiet" or "/S" or "/s");
        var isAutostart = args.Any(a => a is "--autostart");

        AppConfig? earlyConfig = null;
        try
        {
            AppLog.Initialize(new AppConfig());
            earlyConfig = AppConfig.Load();
            AppLog.ApplyConfig(earlyConfig);
        }
        catch
        {
            try
            {
                earlyConfig ??= new AppConfig();
                AppLog.Initialize(earlyConfig);
            }
            catch { /* 日志失败不得阻断启动 */ }
        }

        AppLog.Info(
            $"args=[{string.Join(' ', args)}] admin={Elevation.IsAdministrator()} " +
            $"autostart={isAutostart} user={Environment.UserName} " +
            $"integrity={(Elevation.IsAdministrator() ? "high" : "medium")}");

        // 开机自启（静默）：任何提前退出都必须留下可检索的日志
        if (isAutostart)
            AppLog.Info("autostart launch active (silent): 后续任何退出都会记录原因");

        if (!OsCompatibility.EnsureOrPrompt(quiet || isAutostart))
        {
            AppLog.Error("OS 兼容性检查未通过 — 退出" + (isAutostart ? "（autostart launch）" : ""));
            return;
        }

        // ---- 遗留的安装/卸载命令行 ----
        // 安装卸载统一由 Kachina 完成（安装目录内的 GenshinFpsUnlocker.uninst.exe，
        // 或「设置 → 应用和功能」里由 Kachina 注册的卸载项）。
        // 这里只负责把老快捷方式/老命令行的调用引导过去，绝不自己动文件系统。
        if (args.Any(a => a is "--install" or "/install" or "--uninstall" or "/uninstall"))
        {
            AppLog.Warn("已移除内置安装/卸载入口，忽略参数: " + string.Join(' ', args));
            if (!quiet)
            {
                MessageBox.Show(
                    "本程序已不再自带安装 / 卸载功能。\n\n" +
                    "• 卸载：运行安装目录下的 GenshinFpsUnlocker.uninst.exe，\n" +
                    "  或在「设置 → 应用 → 安装的应用」里卸载「原神帧率解锁」。\n" +
                    "• 安装 / 更新：使用 GenshinFpsUnlocker.Install.{版本}.exe，\n" +
                    "  或安装目录下的 GenshinFpsUnlocker.update.exe。",
                    AppPaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        // ---- 单实例：Global 失败自动 Local（非管理员关键路径）----
        using var instance = new SingleInstance();
        if (!instance.TryAcquire())
        {
            AppLog.Warn("已有实例在运行 — 尝试唤醒主实例后退出" +
                        (isAutostart ? "（autostart launch 放弃二次启动，主实例仍在工作）" : ""));
            // 快捷方式二次点击：唤醒已有进程主窗，不再弹「已在运行」阻塞框
            var signaled = false;
            try { signaled = InstanceWake.TrySignal(); } catch { /* ignore */ }
            if (!signaled && !quiet && !isAutostart)
            {
                MessageBox.Show(
                    "程序已在运行。\n\n" +
                    "请查看系统托盘（任务栏 ^「显示隐藏的图标」）。\n" +
                    "若仍找不到，请在任务管理器结束 GenshinFpsUnlocker.exe 后重试。",
                    AppPaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }
        _activeInstance = instance;
        AppLog.Info("single-instance acquired: " + (instance.Name ?? "(none)"));

        // ---- 运行时依赖 ----
        if (!RuntimePrerequisite.EnsureOrPrompt(quiet || isAutostart))
        {
            AppLog.Error("运行时前置条件不满足 — 退出" +
                         (isAutostart ? "（autostart launch：.NET Desktop Runtime / WebView2 缺失或损坏）" : ""));
            return;
        }

        if (!Elevation.IsAdministrator())
            AppLog.Info("以标准用户完整性运行（asInvoker，无 UAC）— 应正常显示主窗与托盘");
        else
            AppLog.Info("当前进程已提权");

        try { BackgroundResilience.Apply(); }
        catch (Exception ex) { AppLog.Warn("BackgroundResilience: " + ex.Message); }

        var config = earlyConfig ?? AppConfig.Load();
        AppLog.ApplyConfig(config);

        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] is "--fps" or "-f") && i + 1 < args.Length && int.TryParse(args[i + 1], out var fps))
                config.TargetFps = fps;
            if (args[i] is "--no-watch")
                config.AutoWatch = false;
            // 仅 --minimized / -m 强制启动进托盘；--autostart 跟随配置（默认显示窗，可勾选最小化）
            if (args[i] is "--minimized" or "-m")
                config.StartMinimized = true;
            if (args[i] is "--show" or "--no-minimize")
                config.StartMinimized = false;
            if (args[i] is "--master-off")
                config.MasterEnabled = false;
            if (args[i] is "--master-on")
                config.MasterEnabled = true;
            if (args[i] is "--no-log")
                config.DebugLogging = false;
            if (args[i] is "--log-level" && i + 1 < args.Length)
                config.LogLevel = args[i + 1];
        }

        // 开机自启：若用户未勾选「启动后最小化」，仍显示主窗（避免「开机后找不到」）
        // 若勾选了，则进托盘。
        if (isAutostart && !args.Any(a => a is "--minimized" or "-m" or "--show" or "--no-minimize"))
        {
            // 保持 config.StartMinimized 原值
            AppLog.Info("autostart: StartMinimized=" + config.StartMinimized);
        }

        config.Sanitize();
        AppLog.ApplyConfig(config);

        // 自启项同步：配置为真 → 指向当前 exe（路径漂移自愈）；
        // 配置读不到时不删除现有值，避免配置意外丢失导致「重启后自启失败」。
        try { Autostart.SyncOnStartup(config.AutoStartWithWindows, config.LoadedFromDisk); }
        catch (Exception ex) { AppLog.Warn("Autostart: " + ex.Message); }
        AppLog.Info($"autostart={config.AutoStartWithWindows} cmd={Autostart.GetCommand()}");

        // 快捷方式维护：Kachina 安装时会建快捷方式，但用的是英文 appName
        // （GenshinFpsUnlocker.lnk），这里每次启动自愈一次——统一成中文显示名、
        // 清理英文重复项、exe 路径漂移后重新指向当前路径。
        // 标准用户写不了公共目录时 ShortcutHelper 内部会自动退回用户目录。
        //
        // 只对「Kachina 装出来的副本」做，便携/开发目录不要往桌面塞图标。
        // 判据：安装目录里有 Kachina 的 uninst.exe，或位于 Program Files 下，
        // 或存在旧版本写下的安装标记（向后兼容历史安装）。
        var isInstalledCopy =
            PathUtil.ExistsFile(AppPaths.UninstExePath)
            || AppPaths.IsInstalledUnderProgramFiles()
            || PathUtil.ExistsFile(Path.Combine(AppPaths.ExeDirectory, LegacyInstallMarkerFileName));

        if (isInstalledCopy)
        {
            try
            {
                ShortcutHelper.CleanupDuplicateShortcuts();
                ShortcutHelper.CreateStartMenuShortcuts(AppPaths.ExePath, AppPaths.ExeDirectory);
                if (config.CreateDesktopShortcut)
                    ShortcutHelper.CreateDesktopShortcut(AppPaths.ExePath, AppPaths.ExeDirectory);
            }
            catch (Exception ex) { AppLog.Warn("刷新快捷方式: " + ex.Message); }
        }

        if (!config.TrySave(out var cfgErr)) AppLog.Warn("startup config save: " + cfgErr);
        AppLog.Info(
            $"config ok targetFps={config.TargetFps} master={config.MasterEnabled} " +
            $"enabled={config.Enabled} startMin={config.StartMinimized} data={AppPaths.DataDirectory}");

        if (!config.SafetyNoticeAcknowledged && !quiet && !isAutostart)
            AppLog.Info("首次运行：将由界面展示安全声明");

        UnlockService? service = null;
        try
        {
            service = new UnlockService(config);
            service.Start();
            AppLog.Info("UnlockService 已启动 — 进入 UI 消息循环");
            Application.Run(new MainForm(config, service));
        }
        finally
        {
            try { service?.Dispose(); } catch { /* ignore */ }
            try { BackgroundResilience.Clear(); } catch { /* ignore */ }
            try { ReleaseSingleInstance(); } catch { /* ignore */ }
            AppLog.Shutdown();
        }
    }
}
