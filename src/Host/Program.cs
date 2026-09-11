namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 进程入口：单实例、安装/卸载分支、运行时检测、启动监视服务与主窗体。
/// 清单为 asInvoker：非管理员日常启动不弹 UAC，必须能显示主窗 + 托盘。
/// 仅 --install / --uninstall 在需要时主动提权。
/// </summary>
internal static class Program
{
    /// <summary>当前持有的单实例互斥；提权重启前需释放。</summary>
    private static SingleInstance? _activeInstance;

    /// <summary>释放单实例锁，供「以管理员重新启动」在拉起新进程前调用。</summary>
    internal static void ReleaseSingleInstance()
    {
        var inst = Interlocked.Exchange(ref _activeInstance, null);
        if (inst is null) return;
        try { inst.Dispose(); }
        catch (Exception ex) { AppLog.Warn("ReleaseSingleInstance dispose: " + ex.Message); }
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
        var isUninstall = args.Any(a => a is "--uninstall" or "/uninstall");
        var isInstall = args.Any(a => a is "--install" or "/install");
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

        if (!OsCompatibility.EnsureOrPrompt(quiet || isAutostart))
        {
            AppLog.Error("OS 兼容性检查未通过 — 退出");
            return;
        }

        // ---- 卸载 ----
        if (isUninstall)
        {
            AppLog.Info("收到卸载请求");
            if (InstallUninstall.TryLaunchExternalUninstaller(quiet))
                return;

            if (!Elevation.IsAdministrator())
            {
                var argLine = string.Join(' ', args.Select(QuoteIfNeeded));
                if (!Elevation.EnsureAdminOrRelaunch(argLine, quiet, out var relaunched) && relaunched)
                {
                    AppLog.Info("已拉起提权卸载实例，本进程退出");
                    return;
                }
                if (!Elevation.IsAdministrator())
                    AppLog.Warn("无管理员权限，尝试有限卸载（用户数据/HKCU/快捷方式）");
            }
            InstallUninstall.RunUninstall(quiet);
            return;
        }

        // ---- 安装收尾按需提权 ----
        if (isInstall && !Elevation.IsAdministrator())
        {
            var argLine = string.Join(' ', args.Select(QuoteIfNeeded));
            if (!Elevation.EnsureAdminOrRelaunch(argLine, quiet, out var relaunched) && relaunched)
            {
                AppLog.Info("已拉起提权安装实例，本进程退出");
                return;
            }
        }

        // ---- 单实例：Global 失败自动 Local（非管理员关键路径）----
        using var instance = new SingleInstance();
        if (!instance.TryAcquire())
        {
            AppLog.Warn("已有实例在运行 — 退出");
            if (!quiet && !isAutostart)
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
            AppLog.Error("运行时前置条件不满足 — 退出");
            return;
        }

        // ---- 安装收尾 ----
        if (isInstall)
        {
            AppLog.Info("安装收尾: 注册 ARP、快捷方式、Defender");
            try { InstallUninstall.WriteInstallMarker(); } catch (Exception ex) { AppLog.Warn(ex.Message); }
            try { InstallUninstall.RegisterUninstallInfo(); } catch (Exception ex) { AppLog.Warn(ex.Message); }
            try { InstallUninstall.WriteUninstallCmdShim(); } catch (Exception ex) { AppLog.Warn(ex.Message); }
            try { ShortcutHelper.CreateAll(); } catch (Exception ex) { AppLog.Warn(ex.Message); }
            if (Elevation.IsAdministrator())
            {
                BackgroundResilience.TryAddDefenderExclusions(out var defMsg);
                AppLog.Info(defMsg);
            }

            if (!quiet)
            {
                MessageBox.Show(
                    $"安装完成。\n\n安装目录：\n{AppPaths.ExeDirectory}\n\n配置目录：\n{AppPaths.DataDirectory}\n\n" +
                    $"日志目录：\n{AppPaths.LogDirectory}\n\n日常运行无需管理员，不会弹出 UAC。",
                    AppPaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            if (args.Any(a => a is "--no-run"))
            {
                AppLog.Info("install --no-run，退出");
                return;
            }
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

        try { Autostart.SetEnabled(config.AutoStartWithWindows); }
        catch (Exception ex) { AppLog.Warn("Autostart: " + ex.Message); }
        AppLog.Info($"autostart={config.AutoStartWithWindows} cmd={Autostart.GetCommand()}");

        // 已安装副本：无管理员时写 PF/HKLM 会失败，全部吞掉
        if (AppPaths.IsInstalledUnderProgramFiles()
            || PathUtil.ExistsFile(Path.Combine(AppPaths.ExeDirectory, InstallUninstall.InstallMarkerFileName)))
        {
            try { InstallUninstall.WriteInstallMarker(); } catch { /* PF 无写权限 */ }
            try { InstallUninstall.RegisterUninstallInfo(); } catch { /* HKLM */ }
            try { InstallUninstall.WriteUninstallCmdShim(); } catch { /* ignore */ }
            try { ShortcutHelper.CreateStartMenuShortcuts(AppPaths.ExePath, AppPaths.ExeDirectory); }
            catch (Exception ex) { AppLog.Warn("刷新开始菜单: " + ex.Message); }
            if (config.CreateDesktopShortcut)
            {
                try { ShortcutHelper.CreateDesktopShortcut(AppPaths.ExePath, AppPaths.ExeDirectory); }
                catch (Exception ex) { AppLog.Warn("刷新桌面快捷方式: " + ex.Message); }
            }
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

    private static string QuoteIfNeeded(string a)
    {
        if (string.IsNullOrEmpty(a)) return "\"\"";
        if (a.Contains(' ') || a.Contains('\t'))
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        return a;
    }
}
