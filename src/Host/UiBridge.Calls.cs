using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenshinFpsUnlocker.Host;

/// <summary>Web → Host 调用分发：<c>HandleCallAsync</c> 按 method 名把前端请求路由到宿主能力。</summary>
internal sealed partial class UiBridge
{
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
                using (var batch = _config.BeginBatch())
                {
                    _service.ApplyFps(_config.TargetFps);  // 内部 TrySave 被合并进批量窗口
                    batch.Flush();                         // 真正落盘一次
                    SaveConfig();                          // 内容未变 → 不再写盘，只维护保存状态
                }
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
                _service.QueueLegacyCleanup();
                // 恢复默认同样要把两条自启通道一起收敛（默认是「不开自启」）。
                _service.SyncAutostart();
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

            case "uninstall":
            {
                // 只拉起 Kachina 卸载器（uninst.exe）；文件、快捷方式、自启动注册表
                // 与 ARP 卸载项全部由卸载器清理，宿主不做任何删除动作。
                var ok = UninstallLauncher.TryLaunch(out var err);
                if (ok)
                {
                    // 先让响应回到 UI（提示「卸载向导已打开」），再退出本进程，
                    // 否则卸载器会把主程序当作需要先关闭的运行中进程。
                    _form.BeginInvoke(() =>
                    {
                        _form.RequestExit();
                    });
                }

                return Task.FromResult<object?>(new
                {
                    ok,
                    message = ok ? "已启动卸载向导，本窗口即将关闭。" : err,
                });
            }

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

}
