using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 开机自启动：读写 HKCU\...\Run（无需管理员、无 UAC）。
/// 登录后带 --autostart；是否进托盘跟随配置 StartMinimized。
///
/// 可靠性约定：
/// - 写入前校验 exe 必须存在；旧值指向已不存在的目录（便携目录被删、
///   开发 dist 目录被重建等）时自动修复重写，避免「重启后开机自启静默失败」。
/// - 每次启动按配置同步：配置为真 → 保证指向当前 exe（自愈）；
///   配置为假但配置文件本身读不到（残损/丢失，拿到的是默认值）→ 不删除，
///   避免「配置意外丢失 → 下次启动把自启项静默删掉 → 重启后自启失败」。
/// - 每次写入/删除都记日志，便于在 logs 中追踪自启项去向。
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppPaths.ProductName;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 开启或关闭自启动。
    /// 命令行仅 --autostart（不加 --minimized），避免强制「开机后无界面」。
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            // 关闭时只打开已有键，不为禁用状态额外创建空的 Run 子键。
            // 开启时才在缺失时创建，避免每次启动都修改用户注册表结构。
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? (enabled ? Registry.CurrentUser.CreateSubKey(RunKey) : null);
            if (key is null) return;

            var existing = key.GetValue(ValueName) as string;

            if (enabled)
            {
                var exe = AppPaths.ExePath;
                if (!PathUtil.ExistsFile(exe))
                {
                    AppLog.Error("autostart enable aborted: exe 不存在 " + exe);
                    return;
                }

                var cmd = $"\"{exe}\" --autostart";
                if (!string.Equals(existing, cmd, StringComparison.OrdinalIgnoreCase))
                {
                    var oldTarget = ParseTarget(existing);
                    if (existing is not null && oldTarget is not null && !PathUtil.ExistsFile(oldTarget))
                        AppLog.Info("autostart 修复: 旧值指向不存在的路径 " + oldTarget);
                    else if (existing is not null)
                        AppLog.Info("autostart 更新: " + existing + " → " + cmd);

                    key.SetValue(ValueName, cmd);
                    AppLog.Info("autostart enabled: " + (key.GetValue(ValueName) ?? cmd));
                }
            }
            else if (existing is not null)
            {
                // 防御：仅删除含本产品名的值，避免误删同名异常值
                if (!existing.Contains(AppPaths.ProductName, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Warn("autostart 值不含本产品名，跳过删除: " + existing);
                    return;
                }

                var oldTarget = ParseTarget(existing);
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                var stale = oldTarget is not null && !PathUtil.ExistsFile(oldTarget)
                    ? "（原目标已不存在，按失效项清理）"
                    : "";
                AppLog.Info("autostart removed: " + existing + stale);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Autostart.SetEnabled: " + ex.Message);
        }
    }

    /// <summary>
    /// 启动时按配置同步自启项（替代直接 SetEnabled，防止误删）：
    /// - 配置为真 → 确保值存在且指向当前 exe（路径漂移自愈）。
    /// - 配置为假且配置确实从磁盘读到了 → 删除本产品值。
    /// - 配置为假但配置读不到（默认值）→ 保留现有值并告警。
    /// </summary>
    public static void SyncOnStartup(bool configEnabled, bool configLoadedFromDisk)
    {
        if (configEnabled)
        {
            SetEnabled(true);
            return;
        }

        if (!configLoadedFromDisk)
        {
            var cmd = GetCommand();
            if (cmd is not null)
                AppLog.Warn("配置未从磁盘加载（可能残损/缺失）；保留现有自启项不删除: " + cmd);
            return;
        }

        SetEnabled(false);
    }

    public static void Remove() => SetEnabled(false);

    public static string? GetCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 Run 值（形如 "C:\a b\x.exe" --autostart）中的 exe 路径；失败返回 null。</summary>
    public static string? ParseTarget(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        try
        {
            var s = command.Trim();
            if (s.StartsWith('"'))
            {
                var end = s.IndexOf('"', 1);
                if (end <= 1) return null;
                return PathUtil.Normalize(s[1..end]);
            }
            var sp = s.IndexOf(' ');
            return PathUtil.Normalize(sp > 0 ? s[..sp] : s);
        }
        catch
        {
            return null;
        }
    }
}
