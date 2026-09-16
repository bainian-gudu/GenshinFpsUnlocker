using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>游戏路径定位来源（用于 UI 展示）。</summary>
internal enum GameLocateSource
{
    Config,
    RunningProcess,
    UnityLog,
    LauncherConfig,
    Shortcut,
    QuickScan,
    RegistryUninstall,
    Manual,
    None,
}

/// <summary>定位结果：是否成功、路径、来源与说明。</summary>
internal readonly record struct GameLocateResult(bool Ok, string? Path, GameLocateSource Source, string? Detail = null)
{
    public static GameLocateResult Fail(string detail) => new(false, null, GameLocateSource.None, detail);
    public static GameLocateResult Success(string path, GameLocateSource source, string? detail = null)
        => new(true, path, source, detail);
}

/// <summary>
/// 多源游戏主程序定位器（Unity 日志 + 启动器/注册表回退）。
/// 优先级：配置 → 运行中进程 → Unity 日志 → 启动器 config.ini/注册表 →
/// 桌面/开始菜单快捷方式反推 → 有预算上限的全盘快扫 → 卸载注册表。
/// 支持中文路径，所有目录枚举都设深度与耗时上限。
/// </summary>
internal static partial class GameLocator
{
    /// <summary>国服主程序文件名。</summary>
    public const string YuanShenExe = "YuanShen.exe";
    /// <summary>国际服主程序文件名。</summary>
    public const string GenshinImpactExe = "GenshinImpact.exe";

    // 从 Unity 日志中的 .../(GenshinImpact|YuanShen)_Data 还原 exe 路径。
    // 非贪婪 + 文件名边界：日志里同一行可能先出现别的 _Data 目录（如崩溃转储路径）。
    [GeneratedRegex(@".:(?:\\|/)(.+?)(?:GenshinImpact|YuanShen)(?=_Data)", RegexOptions.IgnoreCase)]
    private static partial Regex WarmupFileLine { get; }

    [GeneratedRegex(@"game_install_path\s*=\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex GameInstallPathLine { get; }

    /// <summary>
    /// 按优先级尝试所有自动来源。不打开文件对话框。
    /// </summary>
    public static GameLocateResult LocateAutomatic(string? configHint)
    {
        // 1) 已保存的配置路径
        if (IsValidGameExe(configHint))
        {
            return GameLocateResult.Success(PathUtil.Normalize(configHint!), GameLocateSource.Config, "来自配置文件");
        }

        // 2) 当前运行中的游戏进程
        var fromProcess = LocateFromRunningProcess();
        if (fromProcess.Ok) return fromProcess;

        // 3) Unity output_log.txt
        var fromLog = LocateFromUnityLog();
        if (fromLog.Ok) return fromLog;

        // 4) 官方启动器 config.ini（HYP / 旧版）
        var fromLauncher = LocateFromLauncherConfigs();
        if (fromLauncher.Ok) return fromLauncher;

        // 5) 快捷方式反推：玩家常从桌面/开始菜单的快捷方式启动，目标即游戏或安装目录
        var fromShortcut = LocateFromShortcuts();
        if (fromShortcut.Ok) return fromShortcut;

        // 6) 自定义目录快扫：非官方安装位置（D:\Games\… 之类）只在前几步找不到时兜底
        var fromScan = LocateFromQuickScan();
        if (fromScan.Ok) return fromScan;

        // 7) 卸载信息注册表 InstallLocation
        var fromReg = LocateFromUninstallRegistry();
        if (fromReg.Ok) return fromReg;

        return GameLocateResult.Fail(
            "未能自动找到原神主程序（已查配置、运行进程、Unity 日志、启动器、快捷方式、磁盘快扫与注册表），" +
            "请手动选择 YuanShen.exe 或 GenshinImpact.exe");
    }

    /// <summary>打开文件对话框供用户手动选择主程序。</summary>
    public static GameLocateResult LocateManual(IWin32Window? owner = null)
    {
        using var ofd = new OpenFileDialog
        {
            Title = "选择原神主程序 (YuanShen.exe / GenshinImpact.exe)",
            Filter = "原神主程序|YuanShen.exe;GenshinImpact.exe|可执行文件 (*.exe)|*.exe|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        var result = owner is null ? ofd.ShowDialog() : ofd.ShowDialog(owner);
        if (result != DialogResult.OK) return GameLocateResult.Fail("已取消手动选择");

        var path = ofd.FileName;
        if (!IsValidGameExe(path))
        {
            return GameLocateResult.Fail("请选择 YuanShen.exe 或 GenshinImpact.exe");
        }

        return GameLocateResult.Success(PathUtil.Normalize(path), GameLocateSource.Manual, "用户手动选择");
    }

    /// <summary>是否为有效的原神主程序路径（存在且文件名匹配）。</summary>
    public static bool IsValidGameExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = PathUtil.Normalize(path);
        if (!PathUtil.ExistsFile(path)) return false;
        var name = Path.GetFileName(path);
        return name.Equals(YuanShenExe, StringComparison.OrdinalIgnoreCase)
               || name.Equals(GenshinImpactExe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>来源枚举的中文显示名。</summary>
    public static string SourceDisplayName(GameLocateSource source) => source switch
    {
        GameLocateSource.Config => "配置文件",
        GameLocateSource.RunningProcess => "运行中进程",
        GameLocateSource.UnityLog => "Unity 日志",
        GameLocateSource.LauncherConfig => "启动器/安装目录",
        GameLocateSource.Shortcut => "快捷方式",
        GameLocateSource.QuickScan => "磁盘扫描",
        GameLocateSource.RegistryUninstall => "注册表",
        GameLocateSource.Manual => "手动选择",
        _ => "未知",
    };


}
