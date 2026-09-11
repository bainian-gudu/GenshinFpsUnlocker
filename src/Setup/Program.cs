namespace GenshinFpsUnlocker.Setup;

/// <summary>
/// 图形安装器入口（唯一推荐安装方式）。
/// 清单 requireAdministrator：仅安装时 UAC；主程序 asInvoker，自启无弹窗。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        UiStyle.ApplyApplicationTheme();
        ApplicationConfiguration.Initialize();

        if (!OsCompatibility.MeetsMinimumOs(out var osDetail))
        {
            MessageBox.Show(
                "本安装程序需要 64 位 Windows 10（1607+）或 Windows 11。\n\n" + osDetail,
                SetupConstants.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var quiet = args.Any(a => a is "/S" or "/s" or "--quiet" or "-q");
        var payload = PayloadLocator.FindPayloadDirectory();

        if (quiet)
        {
            if (payload is null)
            {
                Environment.ExitCode = 2;
                return;
            }

            var dir = SetupConstants.DefaultInstallDir;
            for (var i = 0; i < args.Length; i++)
            {
                if ((args[i] is "--dir" or "-d") && i + 1 < args.Length)
                    dir = args[++i];
            }

            var leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (!leaf.Equals(SetupConstants.ProductName, StringComparison.OrdinalIgnoreCase))
                dir = Path.Combine(dir, SetupConstants.ProductName);

            try
            {
                new InstallEngine
                {
                    PayloadDir = payload,
                    InstallDir = Path.GetFullPath(dir),
                    CreateDesktopShortcut = !args.Any(a => a is "--no-desktop"),
                    AddDefenderExclusion = !args.Any(a => a is "--no-defender"),
                    StartAfterInstall = !args.Any(a => a is "--no-run"),
                    EnableAutostart = args.Any(a => a is "--autostart"),
                    CancellationToken = CancellationToken.None,
                }.Run();
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine("quiet install failed: " + ex);
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "GenshinFpsUnlocker-setup-error.txt"),
                        ex.ToString());
                }
                catch { /* ignore */ }
                Environment.ExitCode = 1;
            }

            return;
        }

        Application.Run(new SetupForm(payload));
    }
}
