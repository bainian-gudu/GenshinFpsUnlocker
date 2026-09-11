using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 应用配置（JSON）。存储于 %LocalAppData%\GenshinFpsUnlocker\config.json。
/// 属性名序列化为 camelCase，与 config.example.json 对齐。
/// </summary>
internal sealed class AppConfig
{
    /// <summary>目标帧率上限（1–540，默认 120）。</summary>
    public int TargetFps { get; set; } = 120;

    /// <summary>帧率解锁功能开关（与 MasterEnabled 同时为真时才真正解锁）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否监视游戏进程并在启动后自动注入。</summary>
    public bool AutoWatch { get; set; } = true;

    /// <summary>启动时是否最小化到系统托盘。</summary>
    public bool StartMinimized { get; set; } = true;

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

    /// <summary>是否不再提示“建议管理员运行”（预留，默认不弹窗）。</summary>
    public bool SuppressAdminHint { get; set; } = true;

    /// <summary>配置文件完整路径（不序列化）。</summary>
    [JsonIgnore]
    public static string ConfigPath => AppPaths.ConfigPath;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>从磁盘加载配置；不存在则返回默认值。必要时迁移旧版便携配置。</summary>
    public static AppConfig Load()
    {
        MigrateLegacyConfigIfNeeded();

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg != null)
                {
                    cfg.Sanitize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 损坏的 JSON 等：回退默认值
        }

        var defaults = new AppConfig();
        defaults.Sanitize();
        return defaults;
    }

    /// <summary>原子写入配置（先写 .tmp 再覆盖），避免断电半截文件。</summary>
    public void Save()
    {
        Sanitize();
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(this, Options);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, ConfigPath, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    /// <summary>钳制数值范围并规范化游戏路径。</summary>
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

    /// <summary>实际是否应解锁 = 总开关 &amp;&amp; 功能开关。</summary>
    [JsonIgnore]
    public bool EffectiveUnlockEnabled => MasterEnabled && Enabled;

    /// <summary>
    /// 若 AppData 尚无配置，而 exe 旁存在旧版 config.json，则复制一次到 AppData。
    /// 不删除旧文件，避免便携场景误伤。
    /// </summary>
    private static void MigrateLegacyConfigIfNeeded()
    {
        try
        {
            if (File.Exists(ConfigPath)) return;
            var legacy = AppPaths.LegacyPortableConfigPath;
            if (legacy is null) return;

            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.Copy(legacy, ConfigPath, overwrite: false);
        }
        catch
        {
            // 迁移失败不影响启动
        }
    }
}
