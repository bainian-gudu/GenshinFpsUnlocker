namespace GenshinFpsUnlocker.Setup;

/// <summary>安装器常量（与主程序产品名保持一致）。</summary>
internal static class SetupConstants
{
    public const string ProductName = "GenshinFpsUnlocker";
    public const string DisplayName = "原神帧率解锁";
    public const string ExeName = "GenshinFpsUnlocker.exe";
    public const string StubName = "FpsUnlockerStub.dll";
    public const string MarkerName = "GenshinFpsUnlocker.install";
    public const string PayloadFolderName = "Payload";

    /// <summary>
    /// .NET Desktop Runtime x64 官方安装包（.exe）。
    /// 安装时按需下载并用 /quiet 静默安装到系统；不打进分发包以减小体积。
    /// 本项目无 Node/Python 等其它语言运行时依赖。
    /// </summary>
    public const string DotnetDesktopUrl =
        "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe";

    /// <summary>官方下载页（手动备用）。</summary>
    public const string DotnetDesktopPage =
        "https://dotnet.microsoft.com/download/dotnet/9.0";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
}
