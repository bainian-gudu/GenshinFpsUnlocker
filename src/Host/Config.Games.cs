namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 单个游戏的独立配置。两款游戏各持一份，互不影响：
/// 帧率、解锁开关、画面效果与游戏路径都在这里，切换游戏不会串台。
/// </summary>
internal sealed class GameProfile
{
    /// <summary>目标帧率上限（1–540）。走注册表的游戏会被锁定为固定值。</summary>
    public int TargetFps { get; set; } = 120;

    /// <summary>该游戏的帧率解锁开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>反角色虚化（两款游戏显示名统一）。</summary>
    public bool AntiBlurPerspective { get; set; } = false;

    /// <summary>移除水下马赛克（仅原神的注入模块提供）。</summary>
    public bool AntiBlurDiveMosaic { get; set; } = false;

    /// <summary>隐藏 UID（水印 / 资料页）。</summary>
    public bool HideUid { get; set; } = false;

    /// <summary>该游戏主程序完整路径。</summary>
    public string? GamePath { get; set; }

    /// <summary>复制一份（恢复默认 / 导入配置时用）。</summary>
    public GameProfile Clone() => new()
    {
        TargetFps = TargetFps,
        Enabled = Enabled,
        AntiBlurPerspective = AntiBlurPerspective,
        AntiBlurDiveMosaic = AntiBlurDiveMosaic,
        HideUid = HideUid,
        GamePath = GamePath,
    };

    /// <summary>按游戏规则钳制数值：走注册表的游戏只认自己的固定帧率。</summary>
    public void Sanitize(GameDescriptor game)
    {
        TargetFps = game.LockedFps > 0 ? game.LockedFps : Math.Clamp(TargetFps, 1, 540);
        // 该游戏的注入模块没有这项功能时不允许留下 true，避免下发无意义的开关。
        if (!game.SupportsDiveMosaic) AntiBlurDiveMosaic = false;
        if (string.IsNullOrWhiteSpace(GamePath))
        {
            GamePath = null;
            return;
        }
        try { GamePath = PathUtil.Normalize(GamePath); }
        catch { /* 保留原串 */ }
    }
}

/// <summary>两款游戏的配置档案容器（JSON 里的 <c>games</c> 对象）。</summary>
internal sealed class GameProfiles
{
    public GameProfile Genshin { get; set; } = new();

    public GameProfile StarRail { get; set; } = new();

    public GameProfile Get(GameId id) => id == GameId.StarRail ? StarRail : Genshin;

    public void Sanitize()
    {
        foreach (var game in GameCatalog.All) Get(game.Id).Sanitize(game);
    }
}
