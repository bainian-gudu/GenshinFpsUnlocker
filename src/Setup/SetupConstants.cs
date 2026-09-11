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

    public const string DotnetDesktopUrl =
        "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
    public const string DotnetDesktopPage =
        "https://dotnet.microsoft.com/download/dotnet/8.0";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
}
