namespace GenshinFpsUnlocker.Host;

/// <summary>受支持的游戏。两个游戏各自持有独立配置，互不共享。</summary>
internal enum GameId
{
    Genshin,
    StarRail,
}

/// <summary>
/// 一款游戏的静态描述：进程名、主程序名、注入模块、定位线索与帧率解锁方式。
/// 定位流水线（<see cref="GameLocator"/>）按这里的字段跑同一套逻辑，
/// 两款游戏的差异只体现在数据上，不再各写一份查找代码。
/// </summary>
internal sealed class GameDescriptor
{
    /// <summary>内部标识。</summary>
    public required GameId Id { get; init; }

    /// <summary>配置 / 前端使用的键（<c>genshin</c> / <c>starRail</c>）。</summary>
    public required string Key { get; init; }

    /// <summary>完整显示名（界面标题、日志、托盘）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>窄容器使用的短名。</summary>
    public required string ShortName { get; init; }

    /// <summary>运行中的进程名（不含 .exe）。</summary>
    public required string[] ProcessNames { get; init; }

    /// <summary>游戏主程序文件名（含 .exe）。</summary>
    public required string[] ExeNames { get; init; }

    /// <summary>该游戏自己的注入模块文件名，与另一款游戏完全不共用。</summary>
    public required string StubFileName { get; init; }

    /// <summary>Unity 日志所在的 <c>%LocalLow%\miHoYo\</c> 子目录候选。</summary>
    public required string[] UnityLogFolders { get; init; }

    /// <summary>常见安装根目录名（在各盘符根 / Program Files 下探测）。</summary>
    public required string[] InstallRootNames { get; init; }

    /// <summary>Unity 资源目录名，用于判断某个 exe 是否真的在游戏根目录里。</summary>
    public required string[] ArtifactDataFolders { get; init; }

    /// <summary>快捷方式文件名线索（命中的排在前面解析）。</summary>
    public required string[] ShortcutHints { get; init; }

    /// <summary>「应用和功能」里该游戏的 DisplayName 关键词。</summary>
    public required string[] UninstallKeywords { get; init; }

    /// <summary>
    /// 帧率是否通过注册表解锁（星穹铁道）：为 true 时不做帧率注入，
    /// 目标帧率固定为 <see cref="LockedFps"/>。
    /// </summary>
    public bool FpsViaRegistry { get; init; }

    /// <summary>锁定的目标帧率；0 表示由用户自定义（1–540）。</summary>
    public int LockedFps { get; init; }

    /// <summary>该游戏的注入模块是否提供「移除水下马赛克」。</summary>
    public bool SupportsDiveMosaic { get; init; }

    /// <summary>注入模块提供的画面效果条数（托盘提示用）。</summary>
    public int FeatureCount => SupportsDiveMosaic ? 3 : 2;
}

/// <summary>受支持游戏的静态目录（唯一事实来源）。</summary>
internal static class GameCatalog
{
    /// <summary>原神：注入 FpsUnlockerStub.dll 解锁帧率并做画面效果。</summary>
    public static readonly GameDescriptor Genshin = new()
    {
        Id = GameId.Genshin,
        Key = "genshin",
        DisplayName = "原神",
        ShortName = "原神",
        ProcessNames = ["YuanShen", "GenshinImpact"],
        ExeNames = ["YuanShen.exe", "GenshinImpact.exe"],
        StubFileName = "FpsUnlockerStub.dll",
        UnityLogFolders = ["Genshin Impact", "原神"],
        InstallRootNames = ["Genshin Impact", "GenshinImpact", "Yuanshen", "原神", "miHoYo", "HoYoVerse"],
        ArtifactDataFolders = ["GenshinImpact_Data", "YuanShen_Data"],
        ShortcutHints = ["原神", "Yuanshen", "Genshin", "HoYo", "米哈游", "miHoYo"],
        UninstallKeywords = ["Genshin", "原神", "YuanShen"],
        FpsViaRegistry = false,
        LockedFps = 0,
        SupportsDiveMosaic = true,
    };

    /// <summary>崩坏：星穹铁道：帧率写注册表（只支持 120），画面效果走 StarRailStub.dll。</summary>
    public static readonly GameDescriptor StarRail = new()
    {
        Id = GameId.StarRail,
        Key = "starRail",
        DisplayName = "崩坏：星穹铁道",
        ShortName = "星穹铁道",
        ProcessNames = ["StarRail"],
        ExeNames = ["StarRail.exe"],
        StubFileName = "StarRailStub.dll",
        UnityLogFolders = ["Star Rail", "崩坏：星穹铁道", "StarRail"],
        InstallRootNames = ["Star Rail", "StarRail", "崩坏：星穹铁道", "miHoYo", "HoYoVerse", "HoYoPlay"],
        ArtifactDataFolders = ["StarRail_Data"],
        ShortcutHints = ["星穹铁道", "星铁", "StarRail", "HoYo", "米哈游", "miHoYo"],
        UninstallKeywords = ["Star Rail", "StarRail", "崩坏：星穹铁道", "星穹铁道"],
        FpsViaRegistry = true,
        LockedFps = 120,
        SupportsDiveMosaic = false,
    };

    /// <summary>全部游戏，顺序即界面与日志里的默认顺序。</summary>
    public static readonly IReadOnlyList<GameDescriptor> All = [Genshin, StarRail];

    public static GameDescriptor Get(GameId id) => id == GameId.StarRail ? StarRail : Genshin;

    /// <summary>配置键 → 游戏；不认识的键返回 false（调用方保持原值）。</summary>
    public static bool TryParseKey(string? key, out GameId id)
    {
        foreach (var game in All)
        {
            if (string.Equals(game.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                id = game.Id;
                return true;
            }
        }
        id = GameId.Genshin;
        return false;
    }

    /// <summary>按进程名找游戏（监视循环用）。</summary>
    public static GameDescriptor? FromProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        foreach (var game in All)
        {
            foreach (var name in game.ProcessNames)
            {
                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) return game;
            }
        }
        return null;
    }

    /// <summary>按主程序文件名找游戏（路径校验用）。</summary>
    public static GameDescriptor? FromExeName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        foreach (var game in All)
        {
            foreach (var name in game.ExeNames)
            {
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return game;
            }
        }
        return null;
    }

    /// <summary>主程序文件名的可读列表，例如「YuanShen.exe 或 GenshinImpact.exe」。</summary>
    public static string ExeNameList(GameDescriptor game) => string.Join(" 或 ", game.ExeNames);
}
