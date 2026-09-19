using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 配置读写：字段级 patch、导入 / 复制 / 保存、DTO 组装与近期日志读取。
/// 游戏相关字段一律按 <c>games[game]</c> 分组下发与接收，两款游戏互不影响。
/// </summary>
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
            // 当前正在配置的游戏（界面顶部切换器）
            if (TryGetString(p["activeGame"]) is { } activeKey
                && GameCatalog.TryParseKey(activeKey, out var activeGame))
            {
                _service.SetActiveGame(activeGame);
            }

            // 按游戏分组的字段：games.genshin / games.starRail
            if (p["games"] is JsonObject games)
            {
                foreach (var (key, node) in games)
                {
                    if (node is not JsonObject gamePatch) continue;
                    if (!GameCatalog.TryParseKey(key, out var game)) continue;
                    ApplyGamePatch(game, gamePatch);
                }
            }

            // 两个游戏共用的解锁器级设置
            if (p["masterEnabled"] is JsonNode master)
                _service.SetMasterEnabled(master.GetValue<bool>());
            if (p["autoWatch"] is JsonNode watch)
                _service.SetAutoWatch(watch.GetValue<bool>());
            // 两个自启开关可能落在同一次 patch 里：这里只改配置，收尾时统一同步一次，
            // 免得先按普通权限登记、再改成管理员，中途出现两条自启项并存的窗口。
            if (p["autoStartWithWindows"] is JsonNode auto)
                _config.AutoStartWithWindows = auto.GetValue<bool>();
            if (p["autoStartAsAdministrator"] is JsonNode autoAdmin)
                _config.AutoStartAsAdministrator = autoAdmin.GetValue<bool>();
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

            if (p["autoStartWithWindows"] is not null || p["autoStartAsAdministrator"] is not null)
                _service.SyncAutostart();

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

    /// <summary>把一条 <c>games[game]</c> 里的字段补丁交给服务层。</summary>
    private void ApplyGamePatch(GameId game, JsonObject p)
    {
        if (p["targetFps"] is JsonNode fps)
            _service.ApplyFps(game, Math.Clamp(fps.GetValue<int>(), 1, 540));
        if (p["enabled"] is JsonNode en)
            _service.SetEnabled(game, en.GetValue<bool>());
        if (p["antiBlurPerspective"] is JsonNode abp)
            _service.SetAntiBlurPerspective(game, abp.GetValue<bool>());
        if (p["antiBlurDiveMosaic"] is JsonNode abm)
            _service.SetAntiBlurDiveMosaic(game, abm.GetValue<bool>());
        if (p["hideUid"] is JsonNode uid)
            _service.SetHideUid(game, uid.GetValue<bool>());
        if (p["gamePath"] is JsonNode path)
            ApplyGamePathPatch(game, path);
    }

    /// <summary>游戏路径补丁：空值清空，字符串必须是对应游戏的主程序路径。</summary>
    private void ApplyGamePathPatch(GameId game, JsonNode node)
    {
        var descriptor = GameCatalog.Get(game);
        if (node.GetValueKind() == JsonValueKind.Null)
        {
            _config.Profile(game).GamePath = null;
            _config.TrySave(out _);
            return;
        }

        var text = TryGetString(node)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(text))
        {
            _config.Profile(game).GamePath = null;
            return;
        }

        var result = _service.SetGamePath(game, text);
        if (!result.Ok)
            throw new InvalidOperationException(result.Detail ?? $"请选择 {GameCatalog.ExeNameList(descriptor)}");
    }

    private void ApplyImportedConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("配置文件必须是 JSON 对象");

        // 每个游戏一份的档案：新格式是 games 段，旧格式（单游戏扁平结构）整段按原神档案迁移。
        if (root.TryGetProperty("games", out var gamesEl) && gamesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var game in GameCatalog.All)
            {
                if (!gamesEl.TryGetProperty(game.Key, out var profileEl)) continue;
                if (profileEl.ValueKind != JsonValueKind.Object) continue;
                ApplyImportedGameProfile(game.Id, profileEl);
            }
        }
        else
        {
            ApplyImportedGameProfile(GameId.Genshin, root);
        }

        if (root.TryGetProperty("activeGame", out var activeEl)
            && activeEl.ValueKind == JsonValueKind.String
            && GameCatalog.TryParseKey(activeEl.GetString(), out var activeGame))
        {
            _config.ActiveGame = activeGame;
            _service.SetDisplayGame(activeGame);
        }

        SetBool(root, "masterEnabled", v => _config.MasterEnabled = v);
        SetBool(root, "autoWatch", v => _config.AutoWatch = v);
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

        _config.Sanitize();
        // 导入的配置可能换掉游戏路径：重新核对一次上一版本的残留组件。
        _service.QueueLegacyCleanup();
        // 导入的配置可能换掉自启开关的组合，同样按唯一入口重新同步，
        // 否则会出现「配置说管理员自启、实际还是旧通道」的错位。
        _service.SyncAutostart();
        AppLog.ApplyConfig(_config);
        _form.SyncTrayFromConfig();
    }

    /// <summary>导入单个游戏的档案（只认该游戏真实存在的字段）。</summary>
    private void ApplyImportedGameProfile(GameId game, JsonElement profileEl)
    {
        var descriptor = GameCatalog.Get(game);
        var profile = _config.Profile(game);

        if (profileEl.TryGetProperty("targetFps", out var fpsEl) && fpsEl.TryGetInt32(out var fps))
            profile.TargetFps = descriptor.LockedFps > 0 ? descriptor.LockedFps : Math.Clamp(fps, 1, 540);
        SetBool(profileEl, "enabled", v => profile.Enabled = v);
        SetBool(profileEl, "antiBlurPerspective", v => profile.AntiBlurPerspective = v);
        SetBool(profileEl, "antiBlurDiveMosaic", v => profile.AntiBlurDiveMosaic = v);
        SetBool(profileEl, "hideUid", v => profile.HideUid = v);

        // 兼容更早的扁平字段名：gamePathHint
        var pathElement = profileEl.TryGetProperty("gamePath", out var gp)
            ? gp
            : profileEl.TryGetProperty("gamePathHint", out var gph) ? gph : default;
        if (pathElement.ValueKind == JsonValueKind.String)
        {
            var text = pathElement.GetString();
            profile.GamePath = string.IsNullOrWhiteSpace(text) ? null : text;
        }
        else if (pathElement.ValueKind == JsonValueKind.Null)
        {
            profile.GamePath = null;
        }
    }

    private static void SetBool(JsonElement root, string name, Action<bool> set)
    {
        if (root.TryGetProperty(name, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            set(el.GetBoolean());
    }

    private static void CopyConfig(AppConfig from, AppConfig to)
    {
        to.ActiveGame = from.ActiveGame;
        to.Games = new GameProfiles
        {
            Genshin = from.Games.Genshin.Clone(),
            StarRail = from.Games.StarRail.Clone(),
        };
        to.MasterEnabled = from.MasterEnabled;
        to.AutoWatch = from.AutoWatch;
        to.StartMinimized = from.StartMinimized;
        to.AutoStartWithWindows = from.AutoStartWithWindows;
        to.AutoStartAsAdministrator = from.AutoStartAsAdministrator;
        to.PollIntervalMs = from.PollIntervalMs;
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

    /// <summary>单个游戏的配置 DTO（键名与前端 GameProfile 一致）。</summary>
    private object BuildGameProfileDto(GameId game)
    {
        var profile = _config.Profile(game);
        return new
        {
            targetFps = profile.TargetFps,
            enabled = profile.Enabled,
            antiBlurPerspective = profile.AntiBlurPerspective,
            antiBlurDiveMosaic = profile.AntiBlurDiveMosaic,
            hideUid = profile.HideUid,
            gamePath = profile.GamePath,
        };
    }

    private object BuildConfigDto() => new
    {
        activeGame = GameCatalog.Get(_config.ActiveGame).Key,
        games = new
        {
            genshin = BuildGameProfileDto(GameId.Genshin),
            starRail = BuildGameProfileDto(GameId.StarRail),
        },
        masterEnabled = _config.MasterEnabled,
        autoWatch = _config.AutoWatch,
        startMinimized = _config.StartMinimized,
        autoStartWithWindows = _config.AutoStartWithWindows,
        autoStartAsAdministrator = _config.AutoStartAsAdministrator,
        pollIntervalMs = _config.PollIntervalMs,
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
