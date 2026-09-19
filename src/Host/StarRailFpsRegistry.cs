using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>星穹铁道注册表解锁的结果类型。</summary>
internal enum StarRailFpsOutcome
{
    /// <summary>已经是 120 FPS，本次没有改动注册表。</summary>
    AlreadyAtTarget,
    /// <summary>写入 120 FPS 成功（需要重启游戏生效）。</summary>
    Written,
    /// <summary>游戏还没写过画面设置（没有 GraphicsSettings_Model_h* 值）。</summary>
    ValueMissing,
    /// <summary>值存在但不是可解析的 JSON，或没有 FPS 字段。</summary>
    UnsupportedValue,
    /// <summary>读写注册表失败（权限 / 键不存在）。</summary>
    RegistryError,
}

/// <summary>一次注册表核对的结果。</summary>
internal readonly record struct StarRailFpsResult(
    StarRailFpsOutcome Outcome,
    int? CurrentFps,
    string? ValueName,
    string Detail)
{
    /// <summary>是否已经处于目标状态（含本次写入成功）。</summary>
    public bool Ok => Outcome is StarRailFpsOutcome.AlreadyAtTarget or StarRailFpsOutcome.Written;

    /// <summary>本次是否真的改了注册表。</summary>
    public bool Changed => Outcome == StarRailFpsOutcome.Written;
}

/// <summary>
/// 崩坏：星穹铁道的帧率解锁：直接改注册表里的画面设置，不注入游戏进程。
///
/// 规则（用户确认的方案）：
///   1) 只在启用解锁时才动注册表；
///   2) 先读 <c>GraphicsSettings_Model_h&lt;版本号&gt;</c>，已经是 120 FPS 就不覆盖；
///   3) 没到 120 才写入 120，只支持 120 这一个值；
///   4) 关闭开关不回写，游戏沿用注册表里现有的设置。
///
/// 版本号按前缀匹配而不是写死：游戏每次更新都会换一个 <c>_h</c> 后缀，
/// 写死某个数字会在版本更新后静默失效。存在多个时取后缀最大的那个。
/// </summary>
internal static class StarRailFpsRegistry
{
    /// <summary>该游戏唯一支持的解锁帧率。</summary>
    public const int TargetFps = 120;

    /// <summary>画面设置值名前缀。</summary>
    private const string ModelValuePrefix = "GraphicsSettings_Model_h";

    /// <summary>设置值里的帧率字段名。</summary>
    private const string FpsPropertyName = "FPS";

    /// <summary>可能的注册表位置（国服键名是中文；另两个是历史 / 国际服写法）。</summary>
    private static readonly string[] CandidateKeyPaths =
    [
        @"Software\miHoYo\崩坏：星穹铁道",
        @"Software\miHoYo\Star Rail",
        @"Software\miHoYo\StarRail",
        @"Software\Cognosphere\Star Rail",
    ];

    /// <summary>读取当前注册表里的帧率设置（不改动）。</summary>
    public static StarRailFpsResult Read()
    {
        foreach (var path in CandidateKeyPaths)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
                if (key is null) continue;

                var target = FindNewestModelValue(key);
                if (target is null)
                    return new StarRailFpsResult(StarRailFpsOutcome.ValueMissing, null, null,
                        $"{path} 下还没有画面设置（GraphicsSettings_Model_h*）：请先启动一次游戏");

                var (name, json) = target.Value;
                if (!TryReadFps(json, out var fps))
                    return new StarRailFpsResult(StarRailFpsOutcome.UnsupportedValue, null, name,
                        $"无法从 {name} 解析 FPS 字段");

                return new StarRailFpsResult(StarRailFpsOutcome.AlreadyAtTarget, fps, name,
                    $"{name} 当前 {fps} FPS");
            }
            catch (Exception ex)
            {
                return new StarRailFpsResult(StarRailFpsOutcome.RegistryError, null, null,
                    $"读取注册表失败：{ex.Message}");
            }
        }

        return new StarRailFpsResult(StarRailFpsOutcome.ValueMissing, null, null,
            "注册表里没有找到星穹铁道的画面设置：请先启动一次游戏");
    }

    /// <summary>
    /// 启用解锁时的核对入口：已经是 120 就不写，否则写入 120。
    /// </summary>
    public static StarRailFpsResult Ensure()
    {
        foreach (var path in CandidateKeyPaths)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
                if (key is null) continue;

                var target = FindNewestModelValue(key);
                if (target is null)
                    return new StarRailFpsResult(StarRailFpsOutcome.ValueMissing, null, null,
                        $"{path} 下还没有画面设置：请先启动一次游戏再开启解锁");

                var (name, json) = target.Value;
                if (!TryReadFps(json, out var fps))
                    return new StarRailFpsResult(StarRailFpsOutcome.UnsupportedValue, null, name,
                        $"无法从 {name} 解析 FPS 字段，已跳过写入");

                if (fps == TargetFps)
                    return new StarRailFpsResult(StarRailFpsOutcome.AlreadyAtTarget, fps, name,
                        $"{name} 已经是 {TargetFps} FPS，未覆盖");

                var updated = WriteFps(json, TargetFps);
                using var writable = Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (writable is null)
                    return new StarRailFpsResult(StarRailFpsOutcome.RegistryError, fps, name,
                        $"注册表项不可写：{path}");

                writable.SetValue(name, updated, RegistryValueKind.String);
                return new StarRailFpsResult(StarRailFpsOutcome.Written, TargetFps, name,
                    $"{name}：{fps} → {TargetFps} FPS（重启游戏后生效）");
            }
            catch (Exception ex)
            {
                return new StarRailFpsResult(StarRailFpsOutcome.RegistryError, null, null,
                    $"写入注册表失败：{ex.Message}");
            }
        }

        return new StarRailFpsResult(StarRailFpsOutcome.ValueMissing, null, null,
            "注册表里没有找到星穹铁道的画面设置：请先启动一次游戏再开启解锁");
    }

    /// <summary>按前缀找出后缀版本号最大的画面设置值。</summary>
    private static (string Name, string Json)? FindNewestModelValue(RegistryKey key)
    {
        string? bestName = null;
        var bestVersion = -1L;

        foreach (var name in key.GetValueNames())
        {
            if (!name.StartsWith(ModelValuePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = name[ModelValuePrefix.Length..];
            var version = long.TryParse(suffix, out var parsed) ? parsed : 0;
            if (version < bestVersion) continue;
            bestVersion = version;
            bestName = name;
        }

        if (bestName is null) return null;
        var raw = key.GetValue(bestName);
        var json = raw switch
        {
            string s => s,
            string[] array when array.Length > 0 => array[0],
            _ => null,
        };
        return json is null ? null : (bestName, json);
    }

    /// <summary>从设置 JSON 里读 FPS 字段（大小写不敏感）。</summary>
    private static bool TryReadFps(string json, out int fps)
    {
        fps = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals(FpsPropertyName, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Number) return false;
                fps = property.Value.GetInt32();
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>把设置 JSON 里的 FPS 字段改成目标值，其余字段原样保留。</summary>
    private static string WriteFps(string json, int fps)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonObject obj) throw new InvalidOperationException("画面设置不是 JSON 对象");

        string? key = null;
        foreach (var pair in obj)
        {
            if (pair.Key.Equals(FpsPropertyName, StringComparison.OrdinalIgnoreCase))
            {
                key = pair.Key;
                break;
            }
        }

        obj[key ?? FpsPropertyName] = fps;
        return obj.ToJsonString();
    }
}
