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
internal sealed partial class UiBridge : IDisposable
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
    private string? _lastStateJson;
    /// <summary>增量日志的自增序号（与 getBootstrap 的 "log-N" 区分，避免 React key 撞车）。</summary>
    private int _liveLogSeq;
    /// <summary>增量推送失败过一次就停手，等下次 Attach 再恢复（见 PushLog 注释）。</summary>
    private bool _logPushBroken;

    public UiBridge(AppConfig config, UnlockService service, MainForm form)
    {
        _config = config;
        _service = service;
        _form = form;
        _service.StateChanged += OnServiceStateChanged;
        // 宿主日志 → 前端日志页的增量推送（前端 native.ts 的 onNativeLog 一直在监听）
        AppLog.EntryLogged += OnAppLogEntry;
    }

    public void Attach(WebView2 webView)
    {
        _webView = webView;
        _lastStateJson = null;  // 新 webview 没收到过任何状态，作废去重缓存
        _logPushBroken = false; // 新 webview 重新允许增量日志
        webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
    }

    public void PushState()
    {
        if (_webView?.CoreWebView2 is null) return;
        // 监视循环每秒会触发几次 StateChanged（状态串里带实时 FPS），但绝大部分
        // 推送的 JSON 与上一次完全相同：序列化 + WebView2 消息 + 前端整树重渲染
        // 全是白做。这里按序列化结果去重，只有真正变化才过桥。
        var json = JsonSerializer.Serialize(new
        {
            type = "state",
            state = BuildStateObject(),
        }, JsonOpts);
        if (json == _lastStateJson) return;
        if (PostJson(json)) _lastStateJson = json;
    }

    /// <summary>
    /// 让 Web UI 回到默认页（游戏概览）。窗口进托盘（最小化 / 关窗）时调用，
    /// 这样下次从托盘打开主界面不会还停在上次浏览的页面。
    /// 前端在 native.ts 的 onNativeNavigate 里监听；webview 未就绪时静默跳过。
    /// </summary>
    public void ResetUiPage() => Post(new { type = "navigate", page = "overview" });

    /// <summary>
    /// 把一条宿主日志增量推给前端日志页。
    /// 前端（native.ts 的 onNativeLog）早就在监听了，缺的一直是宿主这边的接线：
    /// 旧实现里 PushLog 没有任何调用方，日志页只显示 getBootstrap 拿到的那一批，
    /// 之后宿主再记什么都看不见。
    /// </summary>
    private void PushLog(AppLog.Entry entry)
    {
        if (_disposed || _logPushBroken || _webView?.CoreWebView2 is null) return;

        var payload = new
        {
            type = "log",
            entry = new
            {
                id = $"live-{Interlocked.Increment(ref _liveLogSeq)}",
                timestamp = entry.UtcTimestamp,
                level = entry.LevelName,
                message = entry.Message,
            },
        };

        // 故意不走 Post()：那条路径失败时会 AppLog.Debug，而 AppLog 现在会把每条
        // 日志再推给前端 —— 「推送失败 → 记日志 → 又推送」会自己喂自己，
        // 在 webview 正在拆除的当口刷爆消息队列。这里失败就静默停手，
        // 等下次 Attach（新 webview）再恢复。
        try
        {
            _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
        }
        catch
        {
            _logPushBroken = true;
        }
    }

    /// <summary>
    /// AppLog 的事件在「写日志的那个线程」上触发（监视循环、线程池都可能），
    /// 而 PostWebMessageAsJson 必须在 UI 线程调用 —— 统一 marshaling 过去。
    /// </summary>
    private void OnAppLogEntry(AppLog.Entry entry)
    {
        if (_disposed) return;
        try
        {
            if (_form.IsHandleCreated && !_form.IsDisposed)
                _form.BeginInvoke(() => PushLog(entry));
            else
                PushLog(entry);
        }
        catch { /* ignore */ }
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

    /// <summary>
    /// WebView 消息入口。<b>async void 的异常没有任何调用方能接住</b>，会直接掀掉进程，
    /// 所以真正的处理放到 <see cref="HandleWebMessageAsync"/> 里，这里整体兜一层。
    /// </summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            await HandleWebMessageAsync(e).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("UiBridge 消息处理异常: " + ex.Message);
        }
    }

    private async Task HandleWebMessageAsync(CoreWebView2WebMessageReceivedEventArgs e)
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

        // 一律用 TryGetString 取值：GetValue<string>() 在值不是字符串时会抛
        // InvalidOperationException，而 root["x"] 在 root 不是对象时也会抛 ——
        // 页面发来一条畸形消息就能让宿主崩掉。
        if (TryGetString(root["type"]) != "call") return;

        var id = root["id"]?.ToString() ?? "";
        var method = TryGetString(root["method"]) ?? "";
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

    /// <summary>安全取字符串：节点不是字符串（或不存在）时返回 null，不抛异常。</summary>
    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
            // 运行状态按游戏归属：正在运行的游戏与当前附着的游戏各自上报，
            // 界面只让对应游戏显示「运行中 / 已注入」，另一款保持等待启动。
            runningGame = _service.RunningGame is GameId running ? GameCatalog.Get(running).Key : null,
            // 当前附着的是哪款游戏（未附着时为 null）与星穹铁道注册表解锁的最近结果。
            attachedGame = _service.AttachedGame is GameId attached ? GameCatalog.Get(attached).Key : null,
            starRailRegistryStatus = _service.StarRailRegistryStatus,
            currentFps = _service.CurrentFpsFeedback,
            stubStatus = (int)_service.StubStatus,
            stubLastError = _service.LastErrorFeedback,
            antiBlurState = _service.AntiBlurStateFeedback,
            hideUidState = _service.HideUidStateFeedback,
            saveState = save == 1 ? "saving" : save == 2 ? "error" : "saved",
            isNative = true,
            isElevated = elevated,
            // 登录自启实际生效的方式（普通权限 Run / 最高权限计划任务 / 回退）与提示：
            // 直接读同步结果，界面不需要再去猜注册表和任务计划程序里的状态。
            autostart = new
            {
                mode = Autostart.LastReport.Mode switch
                {
                    Autostart.AutostartMode.Elevated => "elevated",
                    Autostart.AutostartMode.Standard => "standard",
                    Autostart.AutostartMode.Fallback => "fallback",
                    _ => "disabled",
                },
                notice = Autostart.LastReport.Notice,
            },
            // 权限状态只看当前进程令牌；真实注入被拒绝时再提示提权。
            // 当前进程不是管理员时始终提供手动提权入口；历史授权记录不能替代真实令牌。
            needsAdminForUnlock = !elevated,
            version = typeof(UiBridge).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        };
    }

    private void Post(object payload)
    {
        PostJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>发送已序列化的消息；返回是否真的发出去了（webview 未就绪时不算）。</summary>
    private bool PostJson(string json)
    {
        try
        {
            if (_webView?.CoreWebView2 is null) return false;
            _webView.CoreWebView2.PostWebMessageAsJson(json);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("UiBridge post: " + ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.StateChanged -= OnServiceStateChanged;
        AppLog.EntryLogged -= OnAppLogEntry;
        if (_webView?.CoreWebView2 is not null)
        {
            try { _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived; }
            catch { /* ignore */ }
        }
    }
}
