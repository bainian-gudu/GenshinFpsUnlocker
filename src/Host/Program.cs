namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 进程入口：单实例、安装/卸载分支、运行时检测、安全声明、启动监视服务与主窗体。
/// 清单为 asInvoker：开机自启不弹 UAC；仅 --install / --uninstall 在需要时主动提权。
/// 命令行：
///   --install [--no-run] [--quiet]
///   --uninstall [--quiet]
///   --autostart / --minimized
///   --fps N / --no-watch / --master-on|off / --no-log / --log-level LEVEL
/// </summary>
internal static class Program
{
    /// <summary>全局单实例互斥体名称。</summary>
    private const string MutexName = @"Global\GenshinFpsUnlocker.Instance.v1";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var quiet = args.Any(a => a is "--quiet" or "/S" or "/s");
        var isUninstall = args.Any(a => a is "--uninstall" or "/uninstall");
        var isInstall = args.Any(a => a is "--install" or "/install");
        var isAutostart = args.Any(a => a is "--autostart");

        // 尽早加载配置并初始化日志（安装/卸载路径也需要排障日志）
        AppConfig? earlyConfig = null;
        try
        {
            earlyConfig = AppConfig.Load();
            AppLog.Initialize(earlyConfig);
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

        AppLog.Info($"args=[{string.Join(' ', args)}] admin={Elevation.IsAdministrator()} autostart={isAutostart}");

        // Windows 10 / 11 x64 最低要求（自启路径 quiet 不弹窗）
        if (!OsCompatibility.EnsureOrPrompt(quiet || isAutostart))
        {
            AppLog.Error("OS 兼容性检查未通过 — 退出");
            return;
        }

        // ---- 卸载：需要写 PF / HKLM 时按需提权（用户主动操作，可接受一次 UAC）----
        if (isUninstall)
        {
            AppLog.Info("收到卸载请求");
            if (!Elevation.IsAdministrator())
            {
                var argLine = string.Join(' ', args.Select(QuoteIfNeeded));
                if (!Elevation.EnsureAdminOrRelaunch(argLine, quiet, out var relaunched) && relaunched)
                {
                    AppLog.Info("已拉起提权卸载实例，本进程退出");
                    return;
                }
                if (!Elevation.IsAdministrator())
                {
                    AppLog.Warn("无管理员权限，尝试有限卸载（用户数据/HKCU/快捷方式）");
                }
            }
            InstallUninstall.RunUninstall(quiet);
            return;
        }

        // ---- 安装收尾同样按需提权 ----
        if (isInstall && !Elevation.IsAdministrator())
        {
            var argLine = string.Join(' ', args.Select(QuoteIfNeeded));
            if (!Elevation.EnsureAdminOrRelaunch(argLine, quiet, out var relaunched) && relaunched)
            {
                AppLog.Info("已拉起提权安装实例，本进程退出");
                return;
            }
        }

        // ---- 单实例（asInvoker 下 Global\ 互斥体通常仍可用）----
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            AppLog.Warn("已有实例在运行 — 退出");
            if (!quiet && !isAutostart)
            {
                MessageBox.Show(
                    "程序已在运行（可在系统托盘查看）。",
                    AppPaths.ProductDisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        // ---- 运行时依赖（FDD 需要 .NET 8 Desktop）----
        if (!RuntimePrerequisite.EnsureOrPrompt(quiet || isAutostart))
        {
            AppLog.Error("运行时前置条件不满足 — 退出");
            return;
        }

        // ---- 安装收尾：标记、ARP、快捷方式、Defender ----
        if (isInstall)
        {
            AppLog.Info("安装收尾: 注册 ARP、快捷方式、Defender");
            InstallUninstall.WriteInstallMarker();
            InstallUninstall.RegisterUninstallInfo();
            InstallUninstall.WriteUninstallCmdShim();
            ShortcutHelper.CreateAll();
            if (Elevation.IsAdministrator())
            {
                BackgroundResilience.TryAddDefenderExclusions(out var defMsg);
                AppLog.Info(defMsg);
            }

            if (!quiet)
            {
                MessageBox.Show(
                    $"安装完成。\n\n安装目录：\n{AppPaths.ExeDirectory}\n\n配置目录：\n{AppPaths.DataDirectory}\n\n日志目录：\n{AppPaths.LogDirectory}\n\n已创建开始菜单与桌面快捷方式。\n\n提示：日常运行与开机自启不再弹出系统授权框。",
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

        // ---- 日常启动：asInvoker，不弹 UAC ----
        // 开机自启（--autostart）路径：禁止任何会阻塞登录的 MessageBox / UAC
        if (!Elevation.IsAdministrator())
        {
            AppLog.Info("以标准用户完整性运行（asInvoker，无 UAC）");
            // 仅手动启动时给一次可选提示；自启静默
            if (!isAutostart && !quiet && !(earlyConfig?.SuppressAdminHint ?? false))
            {
                // 不阻断；用户可在设置里关闭提示
                AppLog.Debug("非管理员：注入部分受保护进程时可能失败，可在托盘查看状态");
            }
        }
        else
        {
            AppLog.Info("当前进程已提权");
        }

        BackgroundResilience.Apply();

        var config = earlyConfig ?? AppConfig.Load();
        AppLog.ApplyConfig(config);

        // ---- 命令行覆盖 ----
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--fps" or "-f" && i + 1 < args.Length && int.TryParse(args[i + 1], out var fps))
                config.TargetFps = fps;
            if (args[i] is "--no-watch")
                config.AutoWatch = false;
            if (args[i] is "--minimized" or "-m" or "--autostart")
                config.StartMinimized = true;
            if (args[i] is "--master-off")
                config.MasterEnabled = false;
            if (args[i] is "--master-on")
                config.MasterEnabled = true;
            if (args[i] is "--no-log")
                config.DebugLogging = false;
            if (args[i] is "--log-level" && i + 1 < args.Length)
                config.LogLevel = args[i + 1];
        }

        config.Sanitize();
        AppLog.ApplyConfig(config);

        // 自启项写入 HKCU\Run，无需管理员，不弹 UAC
        Autostart.SetEnabled(config.AutoStartWithWindows);
        AppLog.Info($"autostart={config.AutoStartWithWindows} cmd={Autostart.GetCommand()}");

        // 已安装副本：刷新标记 / ARP / 缺失的快捷方式（HKLM ARP 失败则静默）
        if (AppPaths.IsInstalledUnderProgramFiles()
            || PathUtil.ExistsFile(Path.Combine(AppPaths.ExeDirectory, InstallUninstall.InstallMarkerFileName)))
        {
            try { InstallUninstall.WriteInstallMarker(); } catch { /* 无写权限时忽略 */ }
            try { InstallUninstall.RegisterUninstallInfo(); } catch { /* HKLM 可能失败 */ }
            try { InstallUninstall.WriteUninstallCmdShim(); } catch { /* ignore */ }
            try { ShortcutHelper.CreateStartMenuShortcuts(AppPaths.ExePath, AppPaths.ExeDirectory); }
            catch (Exception ex) { AppLog.Warn("刷新开始菜单: " + ex.Message); }
            if (config.CreateDesktopShortcut)
            {
                try { ShortcutHelper.CreateDesktopShortcut(AppPaths.ExePath, AppPaths.ExeDirectory); }
                catch (Exception ex) { AppLog.Warn("刷新桌面快捷方式: " + ex.Message); }
            }
        }

        config.Save();
        AppLog.Info($"配置已保存 targetFps={config.TargetFps} master={config.MasterEnabled} enabled={config.Enabled} autoWatch={config.AutoWatch}");

        // ---- 安全声明：开机自启不弹窗 ----
        var needNotice = !config.SafetyNoticeAcknowledged
                         || (config.ShowSafetyNoticeOnStartup && !isAutostart);
        if (needNotice && !quiet && !isAutostart)
        {
            AppLog.Info("显示安全声明对话框");
            SafetyDialog.Show(null, config, force: true);
        }

        using var service = new UnlockService(config);
        service.Start();
        AppLog.Info("UnlockService 已启动 — 进入 UI 消息循环");

        try
        {
            Application.Run(new MainForm(config, service));
        }
        finally
        {
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
