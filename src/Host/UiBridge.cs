using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// WebView2 ↔ Host 消息桥：配置读写、启动游戏、路径选择、日志、卸载等。
/// 仅接受结构化 JSON，不做任意命令执行。
/// </summary>
internal sealed class UiBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly AppConfig _config;
    private readonly UnlockService _service;
    private readonly MainForm _form;
    private WebView2? _webView;
    private int _saveState; // 0 saved, 1 saving, 2 error
    private bool _disposed;

    public UiBridge(AppConfig config, UnlockService service, MainForm form)
    {
        _config = config;
        _service = service;
        _form = form;
        _service.StateChanged += OnServiceStateChanged;
    }

    public void Attach(WebView2 webView)
    {
        _webView = webView;
        webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
    }

    public void PushState()
    {
        if (_webView?.CoreWebView2 is null) return;
        Post(new
        {
            type = "state",
            state = BuildStateObject(),
        });
    }

    public void PushLog(string level, string message)
    {
        if (_webView?.CoreWebView2 is null) return;
        Post(new
        {
            type = "log",
            entry = new
            {
                id = Guid.NewGuid().ToString("N"),
                timestamp = DateTime.UtcNow.ToString("O"),
                level,
                message,
            },
        });
    }

    private void OnServiceStateChanged()
    {
        if (_disposed) return;
        try
        {
            if (_form.IsHandleCreated && !_form.IsDisposed)
                _form.BeginInvoke(PushState);
            else
                PushState();
        }
        catch { /* ignore */ }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch
        {
            try { raw = e.WebMessageAsJson; }
            catch { return; }
        }

        if (string.IsNullOrWhiteSpace(raw)) return;

        // WebView 可能把字符串再包一层 JSON 字符串
        try
        {
            if (raw.Length >= 2 && raw[0] == '"')
                raw = JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch { /* keep raw */ }

        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (Exception ex)
        {
            AppLog.Debug("UiBridge parse: " + ex.Message);
            return;
        }

        if (root is null) return;
        var type = root["type"]?.GetValue<string>();
        if (type != "call") return;

        var id = root["id"]?.ToString() ?? "";
        var method = root["method"]?.GetValue<string>() ?? "";
        var paramsNode = root["params"] as JsonObject ?? new JsonObject();

        try
        {
            var result = await HandleCallAsync(method, paramsNode).ConfigureAwait(true);
            Post(new { type = "response", id, ok = true, result });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"UiBridge {method}: {ex.Message}");
            Post(new { type = "response", id, ok = false, error = ex.Message });
        }
    }

    private Task<object?> HandleCallAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "getBootstrap":
                return Task.FromResult<object?>(new
                {
                    state = BuildStateObject(),
                    logs = ReadRecentLogs(),
                });

            case "patchConfig":
                return Task.FromResult<object?>(PatchConfig(p));

            case "setFps":
            {
                var fps = p["value"]?.GetValue<int>() ?? _config.TargetFps;
                _config.TargetFps = Math.Clamp(fps, 1, 540);
                _service.ApplyFps(_config.TargetFps);
                SaveConfig();
                return Task.FromResult<object?>(BuildStateObject());
            }

            case "browseGamePath":
            {
                GameLocateResult r = default!;
                _form.Invoke(() => { r = _service.SetGamePathManual(_form); });
                if (r.Ok) SaveConfig();
                return Task.FromResult<object?>(new
                {
                    ok = r.Ok,
                    path = r.Path,
                    detail = r.Detail,
                    state = BuildStateObject(),
                });
            }

            case "autoLocateGamePath":
            {
                var r = _service.AutoLocateGamePath();
                if (r.Ok) SaveConfig();
                return Task.FromResult<object?>(new
                {
                    ok = r.Ok,
                    path = r.Path,
                    detail = r.Detail,
                    state = BuildStateObject(),
                });
            }

            case "setGamePath":
            {
                var path = p["path"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("路径为空");
                path = path.Trim().Trim('"');
                if (!File.Exists(path))
                    throw new InvalidOperationException("文件不存在：" + path);
                var name = Path.GetFileName(path);
                if (!name.Equals("YuanShen.exe", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请选择 YuanShen.exe 或 GenshinImpact.exe");
                _config.GamePath = path;
                _service.RefreshGamePath(autoLocateIfMissing: false);
                SaveConfig();
                return Task.FromResult<object?>(BuildStateObject());
            }

            case "launchGame":
            {
                var ok = _service.TryLaunchGame(out var msg);
                return Task.FromResult<object?>(new { ok, message = msg, state = BuildStateObject() });
            }

            case "exportConfig":
            {
                var json = JsonSerializer.Serialize(BuildConfigDto(), JsonOpts);
                return Task.FromResult<object?>(new { json, fileName = "config.json" });
            }

            case "importConfig":
            {
                var json = p["json"]?.GetValue<string>()
                           ?? throw new InvalidOperationException("缺少 json");
                ApplyImportedConfig(json);
                SaveConfig();
                _service.PushConfigToIpc(force: true);
                return Task.FromResult<object?>(BuildStateObject());
            }

            case "resetConfig":
            {
                var fresh = new AppConfig();
                CopyConfig(fresh, _config);
                _config.Sanitize();
                Autostart.SetEnabled(_config.AutoStartWithWindows);
                _service.PushConfigToIpc(force: true);
                SaveConfig();
                return Task.FromResult<object?>(BuildStateObject());
            }

            case "openLogFolder":
                AppLog.OpenLogFolder();
                return Task.FromResult<object?>(true);

            case "openConfigFolder":
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = AppPaths.DataDirectory,
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex) { throw new InvalidOperationException(ex.Message); }
                return Task.FromResult<object?>(true);
            }

            case "getLogs":
                return Task.FromResult<object?>(ReadRecentLogs());

            case "exportLogs":
            {
                var lines = AppLog.GetRecentLines(500);
                var sb = new StringBuilder();
                sb.AppendLine("Genshin FPS Unlocker");
                sb.AppendLine("Exported: " + DateTime.UtcNow.ToString("O"));
                sb.AppendLine();
                foreach (var line in lines) sb.AppendLine(line);
                return Task.FromResult<object?>(new
                {
                    content = sb.ToString(),
                    fileName = $"genshin-unlocker-{DateTime.Now:yyyy-MM-dd}.log",
                });
            }

            case "acknowledgeSafety":
            {
                _config.SafetyNoticeAcknowledged = true;
                if (p["showOnStartup"] is JsonNode showNode)
                    _config.ShowSafetyNoticeOnStartup = showNode.GetValue<bool>();
                SaveConfig();
                return Task.FromResult<object?>(BuildStateObject());
            }

            case "getSafetyText":
                return Task.FromResult<object?>(new
                {
                    title = SafetyNotice.Title,
                    shortSummary = SafetyNotice.ShortSummary,
                    fullText = SafetyNotice.FullText,
                });

            case "uninstall":
                _form.BeginInvoke(() => InstallUninstall.RunUninstallInteractive(quiet: false));
                return Task.FromResult<object?>(true);

            case "showWindow":
                _form.BeginInvoke(() => _form.RestoreFromTrayPublic());
                return Task.FromResult<object?>(true);

            case "minimizeToTray":
                _form.BeginInvoke(() => _form.HideToTrayPublic(showTip: true, fromStartup: false));
                return Task.FromResult<object?>(true);

            case "exitApp":
                _form.BeginInvoke(() =>
                {
                    _form.RequestExit();
                });
                return Task.FromResult<object?>(true);

            case "restartElevated":
            {
                // 在 UI 线程执行：释放互斥 → runas → 退出
                string? err = null;
                var ok = false;
                _form.Invoke(() =>
                {
                    ok = _form.TryRestartElevated(out err);
                });
                return Task.FromResult<object?>(new
                {
                    ok,
                    message = ok
                        ? "已请求管理员授权，本窗口即将关闭。"
                        : (err ?? "无法以管理员身份重新启动"),
                    isElevated = Elevation.IsAdministrator(),
                    state = BuildStateObject(),
                });
            }

            case "setUiTheme":
            {
                // Web UI 深/浅色 → 同步 Win11 标题栏与窗体底色，与设计稿一致
                var theme = p["theme"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "dark";
                var dark = theme is not ("light" or "day");
                _form.BeginInvoke(() =>
                {
                    try
                    {
                        UiStyle.SetUiTheme(dark);
                        _form.ApplyWebChromeTheme(dark);
                    }
                    catch (Exception ex) { AppLog.Debug("setUiTheme: " + ex.Message); }
                });
                return Task.FromResult<object?>(new { ok = true, theme = dark ? "dark" : "light" });
            }

            default:
                throw new InvalidOperationException("未知方法: " + method);
        }
    }

    private object PatchConfig(JsonObject p)
    {
        Interlocked.Exchange(ref _saveState, 1);
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
            if (p["startMinimized"] is JsonNode min)
                _config.StartMinimized = min.GetValue<bool>();
            if (p["createDesktopShortcut"] is JsonNode createDeskNode)
            {
                _config.CreateDesktopShortcut = createDeskNode.GetValue<bool>();
                try
                {
                    ShortcutHelper.CleanupDuplicateShortcuts();
                    if (_config.CreateDesktopShortcut)
                        ShortcutHelper.CreateDesktopShortcut(AppPaths.ExePath, AppPaths.ExeDirectory);
                    else
                    {
                        // 关闭维护：移除桌面中英文快捷方式，保留开始菜单
                        foreach (var desktopDir in new[]
                                 {
                                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                 })
                        {
                            if (string.IsNullOrEmpty(desktopDir)) continue;
                            foreach (var n in new[]
                                     {
                                         AppPaths.ProductDisplayName + ".lnk",
                                         AppPaths.ProductName + ".lnk",
                                     })
                            {
                                var f = Path.Combine(desktopDir, n);
                                if (File.Exists(f)) File.Delete(f);
                            }
                        }
                    }
                }
                catch (Exception ex) { AppLog.Warn(ex.Message); }
            }
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
            SaveConfig();
            Interlocked.Exchange(ref _saveState, 0);
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
        SetBool(root, "debugLogging", v => _config.DebugLogging = v);
        SetBool(root, "createDesktopShortcut", v => _config.CreateDesktopShortcut = v);
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
        to.PollIntervalMs = from.PollIntervalMs;
        to.GamePath = from.GamePath;
        to.SafetyNoticeAcknowledged = from.SafetyNoticeAcknowledged;
        to.ShowSafetyNoticeOnStartup = from.ShowSafetyNoticeOnStartup;
        to.DefenderExclusionApplied = from.DefenderExclusionApplied;
        to.DebugLogging = from.DebugLogging;
        to.LogLevel = from.LogLevel;
        to.LogRetainDays = from.LogRetainDays;
        to.CreateDesktopShortcut = from.CreateDesktopShortcut;
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

    private object BuildStateObject()
    {
        var save = Volatile.Read(ref _saveState);
        var elevated = Elevation.IsAdministrator();
        return new
        {
            config = BuildConfigDto(),
            statusText = _service.StatusText,
            gamePathStatus = _service.GamePathStatus,
            attachedPid = _service.AttachedPid,
            currentFps = _service.CurrentFpsFeedback,
            stubStatus = (int)_service.StubStatus,
            antiBlurState = _service.AntiBlurStateFeedback,
            saveState = save == 1 ? "saving" : save == 2 ? "error" : "saved",
            isNative = true,
            isElevated = elevated,
            needsAdminForUnlock = !elevated,
            version = typeof(UiBridge).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        };
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
        pollIntervalMs = _config.PollIntervalMs,
        gamePath = _config.GamePath,
        safetyNoticeAcknowledged = _config.SafetyNoticeAcknowledged,
        showSafetyNoticeOnStartup = _config.ShowSafetyNoticeOnStartup,
        defenderExclusionApplied = _config.DefenderExclusionApplied,
        debugLogging = _config.DebugLogging,
        logLevel = _config.LogLevel,
        logRetainDays = _config.LogRetainDays,
        createDesktopShortcut = _config.CreateDesktopShortcut,
        suppressAdminHint = _config.SuppressAdminHint,
    };

    private static object ReadRecentLogs()
    {
        var lines = AppLog.GetRecentLines(200);
        var list = new List<object>(lines.Count);
        var i = 0;
        foreach (var line in lines)
        {
            // 期望格式类似: 2026-... [INFO] message
            var level = "Info";
            var message = line;
            var ts = DateTime.UtcNow.ToString("O");
            try
            {
                var lb = line.IndexOf('[');
                var rb = line.IndexOf(']', lb + 1);
                if (lb >= 0 && rb > lb)
                {
                    var tag = line[(lb + 1)..rb].Trim();
                    if (tag.Equals("TRACE", StringComparison.OrdinalIgnoreCase)) level = "Trace";
                    else if (tag.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)) level = "Debug";
                    else if (tag.Equals("INFO", StringComparison.OrdinalIgnoreCase)) level = "Info";
                    else if (tag.Equals("WARN", StringComparison.OrdinalIgnoreCase) || tag.Equals("WARNING", StringComparison.OrdinalIgnoreCase)) level = "Warn";
                    else if (tag.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) level = "Error";
                    message = line[(rb + 1)..].Trim();
                    var head = line[..lb].Trim();
                    if (DateTime.TryParse(head, out var dt))
                        ts = dt.ToUniversalTime().ToString("O");
                }
            }
            catch { /* keep defaults */ }

            list.Add(new
            {
                id = $"log-{i++}",
                timestamp = ts,
                level,
                message,
            });
        }
        return list;
    }

    private void Post(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            _webView?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            AppLog.Debug("UiBridge post: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.StateChanged -= OnServiceStateChanged;
        if (_webView?.CoreWebView2 is not null)
        {
            try { _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived; }
            catch { /* ignore */ }
        }
    }
}
