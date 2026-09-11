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
        ApplicationConfiguration.Initialize();

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
                if (args[i] is "--dir" or "-d" && i + 1 < args.Length)
                    dir = args[i + 1];
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
            catch
            {
                Environment.ExitCode = 1;
            }

            return;
        }

        Application.Run(new SetupForm(payload));
    }
}
