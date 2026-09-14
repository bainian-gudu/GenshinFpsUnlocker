using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenshinFpsUnlocker.Host;

/// <summary>配置读写：字段级 patch、导入 / 复制 / 保存、DTO 组装与近期日志读取。</summary>
internal sealed partial class UiBridge
{
    private object PatchConfig(JsonObject p)
    {
        Interlocked.Exchange(ref _saveState, 1);
        // 一次 patchConfig 可能带多个键，而下面每个服务层 setter 都会 TrySave。
        // 批量窗口把它们合并成最后的一次落盘（旧实现最多连着写 4 次，每次都是
        // Flush(true) + 备份拷贝 + File.Replace + 校验读，且全在 UI 线程上）。
        using var batch = _config.BeginBatch();
        try
        {
            if (p["targetFps"] is JsonNode fps)
            {
                _config.TargetFps = Math.Clamp(fps.GetValue<int>(), 1, 540);
                _service.ApplyFps(_config.TargetFps);
            }
            if (p["enabled"] is JsonNode en)
                _service.SetEnabled(en.GetValue<bool>());
            if (p["masterEnabled"] is JsonNode master)
                _service.SetMasterEnabled(master.GetValue<bool>());
            if (p["autoWatch"] is JsonNode watch)
                _service.SetAutoWatch(watch.GetValue<bool>());
            if (p["antiBlurPerspective"] is JsonNode abp)
                _service.SetAntiBlurPerspective(abp.GetValue<bool>());
            if (p["antiBlurDiveMosaic"] is JsonNode abm)
                _service.SetAntiBlurDiveMosaic(abm.GetValue<bool>());
            if (p["autoStartWithWindows"] is JsonNode auto)
                _service.SetAutoStartWithWindows(auto.GetValue<bool>());
            if (p["autoStartAsAdministrator"] is JsonNode autoAdmin)
                _service.SetAutoStartAsAdministrator(autoAdmin.GetValue<bool>());
            if (p["startMinimized"] is JsonNode min)
                _config.StartMinimized = min.GetValue<bool>();
            if (p["debugLogging"] is JsonNode dbg)
            {
                _config.DebugLogging = dbg.GetValue<bool>();
                AppLog.ApplyConfig(_config);
            }
            if (p["logLevel"] is JsonNode lv)
            {
                _config.LogLevel = lv.GetValue<string>() ?? "Debug";
                AppLog.ApplyConfig(_config);
            }
            if (p["logRetainDays"] is JsonNode days)
                _config.LogRetainDays = Math.Clamp(days.GetValue<int>(), 1, 90);
            if (p["pollIntervalMs"] is JsonNode poll)
                _config.PollIntervalMs = Math.Clamp(poll.GetValue<int>(), 200, 10000);
            if (p["showSafetyNoticeOnStartup"] is JsonNode show)
                _config.ShowSafetyNoticeOnStartup = show.GetValue<bool>();
            if (p["safetyNoticeAcknowledged"] is JsonNode ack)
                _config.SafetyNoticeAcknowledged = ack.GetValue<bool>();
            if (p["suppressAdminHint"] is JsonNode adm)
                _config.SuppressAdminHint = adm.GetValue<bool>();

            _config.Sanitize();
            batch.Flush();      // 合并后的唯一一次落盘
            SaveConfig();       // 内容未变 → 跳过写盘，只维护 _saveState 与日志设置
            _form.SyncTrayFromConfig();
            return BuildStateObject();
        }
        catch
        {
            Interlocked.Exchange(ref _saveState, 2);
            throw;
        }
    }

    private void ApplyImportedConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("配置文件必须是 JSON 对象");

        if (root.TryGetProperty("targetFps", out var fpsEl) && fpsEl.TryGetInt32(out var fps))
            _config.TargetFps = Math.Clamp(fps, 1, 540);
        SetBool(root, "enabled", v => _config.Enabled = v);
        SetBool(root, "masterEnabled", v => _config.MasterEnabled = v);
        SetBool(root, "autoWatch", v => _config.AutoWatch = v);
        SetBool(root, "antiBlurPerspective", v => _config.AntiBlurPerspective = v);
        SetBool(root, "antiBlurDiveMosaic", v => _config.AntiBlurDiveMosaic = v);
        SetBool(root, "startMinimized", v => _config.StartMinimized = v);
        SetBool(root, "autoStartWithWindows", v => _config.AutoStartWithWindows = v);
        SetBool(root, "autoStartAsAdministrator", v => _config.AutoStartAsAdministrator = v);
        SetBool(root, "debugLogging", v => _config.DebugLogging = v);
        SetBool(root, "showSafetyNoticeOnStartup", v => _config.ShowSafetyNoticeOnStartup = v);
        SetBool(root, "safetyNoticeAcknowledged", v => _config.SafetyNoticeAcknowledged = v);
        SetBool(root, "suppressAdminHint", v => _config.SuppressAdminHint = v);
        if (root.TryGetProperty("pollIntervalMs", out var poll) && poll.TryGetInt32(out var pms))
            _config.PollIntervalMs = Math.Clamp(pms, 200, 10000);
        if (root.TryGetProperty("logRetainDays", out var days) && days.TryGetInt32(out var d))
            _config.LogRetainDays = Math.Clamp(d, 1, 90);
        if (root.TryGetProperty("logLevel", out var lv) && lv.ValueKind == JsonValueKind.String)
            _config.LogLevel = lv.GetString() ?? "Debug";
        if (root.TryGetProperty("gamePath", out var gp))
        {
            if (gp.ValueKind == JsonValueKind.Null || (gp.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(gp.GetString())))
                _config.GamePath = null;
            else if (gp.ValueKind == JsonValueKind.String)
                _config.GamePath = gp.GetString();
        }

        _config.Sanitize();
        Autostart.SetEnabled(_config.AutoStartWithWindows);
        AppLog.ApplyConfig(_config);
        _form.SyncTrayFromConfig();
    }

    private static void SetBool(JsonElement root, string name, Action<bool> set)
    {
        if (root.TryGetProperty(name, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            set(el.GetBoolean());
    }

    private static void CopyConfig(AppConfig from, AppConfig to)
    {
        to.TargetFps = from.TargetFps;
        to.Enabled = from.Enabled;
        to.AntiBlurPerspective = from.AntiBlurPerspective;
        to.AntiBlurDiveMosaic = from.AntiBlurDiveMosaic;
        to.MasterEnabled = from.MasterEnabled;
        to.AutoWatch = from.AutoWatch;
        to.StartMinimized = from.StartMinimized;
        to.AutoStartWithWindows = from.AutoStartWithWindows;
        to.AutoStartAsAdministrator = from.AutoStartAsAdministrator;
        to.PollIntervalMs = from.PollIntervalMs;
        to.GamePath = from.GamePath;
        to.SafetyNoticeAcknowledged = from.SafetyNoticeAcknowledged;
        to.ShowSafetyNoticeOnStartup = from.ShowSafetyNoticeOnStartup;
        to.DefenderExclusionApplied = from.DefenderExclusionApplied;
        to.DebugLogging = from.DebugLogging;
        to.LogLevel = from.LogLevel;
        to.LogRetainDays = from.LogRetainDays;
        to.SuppressAdminHint = from.SuppressAdminHint;
    }

    private void SaveConfig()
    {
        Interlocked.Exchange(ref _saveState, 1);
        if (!_config.TrySave(out var err))
        {
            Interlocked.Exchange(ref _saveState, 2);
            AppLog.Error("配置保存失败: " + err);
            throw new InvalidOperationException(err ?? "配置保存失败");
        }
        AppLog.ApplyConfig(_config);
        Interlocked.Exchange(ref _saveState, 0);
    }

    private object BuildConfigDto() => new
    {
        targetFps = _config.TargetFps,
        enabled = _config.Enabled,
        masterEnabled = _config.MasterEnabled,
        autoWatch = _config.AutoWatch,
        antiBlurPerspective = _config.AntiBlurPerspective,
        antiBlurDiveMosaic = _config.AntiBlurDiveMosaic,
        startMinimized = _config.StartMinimized,
        autoStartWithWindows = _config.AutoStartWithWindows,
        autoStartAsAdministrator = _config.AutoStartAsAdministrator,
        pollIntervalMs = _config.PollIntervalMs,
        gamePath = _config.GamePath,
        safetyNoticeAcknowledged = _config.SafetyNoticeAcknowledged,
        showSafetyNoticeOnStartup = _config.ShowSafetyNoticeOnStartup,
        defenderExclusionApplied = _config.DefenderExclusionApplied,
        debugLogging = _config.DebugLogging,
        logLevel = _config.LogLevel,
        logRetainDays = _config.LogRetainDays,
        suppressAdminHint = _config.SuppressAdminHint,
    };

    private static object ReadRecentLogs()
    {
        // 直接取结构化记录：旧实现是从格式化字符串里反解析级别/时间，
        // 还会把 "[T12]" 这样的线程标记一起塞进 message 显示给用户。
        var entries = AppLog.GetRecentEntries(200);
        var list = new List<object>(entries.Count);
        var i = 0;
        foreach (var e in entries)
        {
            list.Add(new
            {
                id = $"log-{i++}",
                timestamp = e.UtcTimestamp,
                level = e.LevelName,
                message = e.Message,
            });
        }
        return list;
    }
}
