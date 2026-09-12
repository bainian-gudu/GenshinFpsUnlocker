using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 应用配置（JSON）。主路径：%LocalAppData%\GenshinFpsUnlocker\config.json。
/// 持久化策略：写临时文件 → Flush → File.Replace 原子替换 → 保留 .bak 备份；
/// 读失败时依次尝试主文件 / .bak / .tmp / 便携旁路，降低丢失与半截写入风险。
/// </summary>
internal sealed partial class AppConfig
{
    /// <summary>目标帧率上限（1–540，默认 120）。</summary>
    public int TargetFps { get; set; } = 120;

    /// <summary>帧率解锁功能开关（与 MasterEnabled 同时为真时才真正解锁）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否监视游戏进程并在启动后自动注入。</summary>
    public bool AutoWatch { get; set; } = true;

    /// <summary>
    /// 反角色虚化注入功能（迁移自 Snap.Hutao.Remastered）：开启后镜头拉近时角色不再透明化。
    /// 默认关闭；联机/UGC 玩法中请勿开启。
    /// </summary>
    public bool AntiBlurPerspective { get; set; } = false;

    /// <summary>
    /// 移除水下马赛克注入功能（迁移自 Snap.Hutao.Remastered）：开启后角色入水不再显示马赛克虚化。
    /// 默认关闭；联机/UGC 玩法中请勿开启。
    /// </summary>
    public bool AntiBlurDiveMosaic { get; set; } = false;

    /// <summary>
    /// 启动时是否最小化到系统托盘。
    /// 默认 false：打开软件显示主窗口；关窗/点最小化仍进托盘后台。
    /// 勾选后：下次启动直接进托盘。
    /// </summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>配置 schema 版本（用于一次性迁移默认行为）。</summary>
    public int ConfigSchemaVersion { get; set; } = 0;

    /// <summary>是否写入 HKCU\...\Run，实现开机自启动。</summary>
    public bool AutoStartWithWindows { get; set; } = false;

    /// <summary>
    /// 总开关。关闭时：不注入、不强制帧率，后台仍可待命。
    /// 与 <see cref="Enabled"/> 的区别：总开关优先级更高，可一键暂停全部解锁行为。
    /// </summary>
    public bool MasterEnabled { get; set; } = true;

    /// <summary>监视循环基准轮询间隔（毫秒，200–10000）。</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>原神主程序完整路径（YuanShen.exe / GenshinImpact.exe）。</summary>
    public string? GamePath { get; set; }

    /// <summary>旧版配置字段别名，读入时合并到 <see cref="GamePath"/>。</summary>
    public string? GamePathHint
    {
        get => GamePath;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(GamePath))
                GamePath = value;
        }
    }

    /// <summary>用户是否已确认过安全声明。</summary>
    public bool SafetyNoticeAcknowledged { get; set; } = false;

    /// <summary>每次非自启动启动时是否再次展示安全声明。</summary>
    public bool ShowSafetyNoticeOnStartup { get; set; } = true;

    /// <summary>安装器是否已尝试添加 Defender 排除（仅记录状态）。</summary>
    public bool DefenderExclusionApplied { get; set; } = false;

    /// <summary>是否写调试日志（默认开启，便于排查注入问题）。</summary>
    public bool DebugLogging { get; set; } = true;

    /// <summary>最低日志级别：Trace / Debug / Info / Warn / Error（默认 Debug）。</summary>
    public string LogLevel { get; set; } = "Debug";

    /// <summary>日志保留天数（超过则清理 app-*.log）。</summary>
    public int LogRetainDays { get; set; } = 14;

    /// <summary>安装/首次运行时是否创建桌面快捷方式。</summary>
    public bool CreateDesktopShortcut { get; set; } = true;

    /// <summary>是否隐藏界面上的「建议管理员运行」提示条（默认显示；可在设置中关闭）。</summary>
    public bool SuppressAdminHint { get; set; } = false;

    /// <summary>配置文件完整路径（不序列化）。</summary>
    [JsonIgnore]
    public static string ConfigPath => AppPaths.ConfigPath;

    /// <summary>
    /// 本实例是否成功反序列化自磁盘配置文件。
    /// false = 走的是默认值（文件缺失/残损），调用方不得把默认值当成
    /// 「用户明确关闭了某项」去执行破坏性同步（例如删除开机自启注册表项）。
    /// </summary>
    [JsonIgnore]
    public bool LoadedFromDisk { get; internal set; }

    [JsonIgnore]
    private static string BackupPath => ConfigPath + ".bak";

    [JsonIgnore]
    private static string TempPath => ConfigPath + ".tmp";

    private static readonly object IoLock = new();

    // ---- 落盘合并 ----
    // 批量窗口 >0 时，Save/TrySave 只标脏不写盘，窗口关闭时统一写一次。
    private int _batchDepth;
    private bool _batchDirty;
    /// <summary>上次成功落盘的 JSON；内容没变就跳过整套原子写（备份/Replace/校验读）。</summary>
    private string? _lastSavedJson;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 未知字段保留兼容，避免旧/新版本互相抹掉键
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>钳制数值范围并规范化游戏路径；执行 schema 迁移。</summary>
    public void Sanitize()
    {
        TargetFps = Math.Clamp(TargetFps, 1, 540);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 200, 10000);
        LogRetainDays = Math.Clamp(LogRetainDays, 1, 90);
        if (string.IsNullOrWhiteSpace(LogLevel)) LogLevel = "Debug";
        if (!string.IsNullOrWhiteSpace(GamePath))
        {
            try { GamePath = PathUtil.Normalize(GamePath); }
            catch { /* 保留原串 */ }
        }

    }

    /// <summary>
    /// 一次性 schema 迁移。<b>只在从磁盘加载后调用，不要放进 <see cref="Sanitize"/></b>：
    /// Sanitize() 会被「每次保存」和「UI 改配置」的热路径反复执行，把破坏性的默认值回退
    /// 放里面会导致——用户在设置里刚勾选「启动后最小化到托盘」，同一次 PatchConfig 里的
    /// Sanitize() 就把它抹回 false（表现为开关自己弹回去、重启后不进托盘）。
    /// </summary>
    private void Migrate()
    {
        // v1：旧默认 startMinimized=true 导致「打开没窗口」；一次性改回 false。
        // 用户此后可再手动开启「启动后最小化到托盘」，之后的值一律尊重用户选择。
        if (ConfigSchemaVersion < 1)
        {
            if (StartMinimized)
            {
                StartMinimized = false;
                AppLog.Info("config migrate v1: StartMinimized false (show main window on launch)");
            }
            ConfigSchemaVersion = 1;
        }
    }

    /// <summary>实际是否应解锁 = 总开关 &amp;&amp; 功能开关。</summary>
    [JsonIgnore]
    public bool EffectiveUnlockEnabled => MasterEnabled && Enabled;

}
