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
    private const string StartupApprovedRunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
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
                ClearRunAsAdminCompatibility(exe);
                if (!string.Equals(existing, cmd, StringComparison.OrdinalIgnoreCase))
                {
                    var oldTarget = ParseTarget(existing);
                    if (existing is not null && oldTarget is not null && !PathUtil.ExistsFile(oldTarget))
                        AppLog.Info("autostart 修复: 旧值指向不存在的路径 " + oldTarget);
                    else if (existing is not null)
                        AppLog.Info("autostart 更新: " + existing + " → " + cmd);

                    key.SetValue(ValueName, cmd);
                    SetStartupApproved(enabled: true);
                    AppLog.Info("autostart enabled: " + (key.GetValue(ValueName) ?? cmd));
                }
                else
                {
                    // Windows 任务管理器可单独禁用启动项；每次同步时重新启用，
                    // 避免 Run 值存在但登录时被 StartupApproved 静默拦截。
                    SetStartupApproved(enabled: true);
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
                SetStartupApproved(enabled: false);
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

    /// <summary>
    /// 同步 Windows 任务管理器维护的启动项批准状态。
    /// HKCU\...\Run 仅表示“要启动”，StartupApproved 才决定登录时是否实际执行。
    /// </summary>
    private static void SetStartupApproved(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, writable: true)
                            ?? (enabled ? Registry.CurrentUser.CreateSubKey(StartupApprovedRunKey) : null);
            if (key is null) return;

            if (enabled)
            {
                // 02 = 已启用，后 11 字节为 Windows 保留的时间/状态字段。
                var value = new byte[12];
                value[0] = 0x02;
                key.SetValue(ValueName, value, RegistryValueKind.Binary);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            // 某些精简系统没有 StartupApproved，不应阻断 Run 值写入。
            AppLog.Debug("StartupApproved 同步失败: " + ex.Message);
        }
    }

    /// <summary>清除当前用户为本程序设置的 RUNASADMIN 兼容层，防止 Run 自启被 UAC 阻断。</summary>
    private static void ClearRunAsAdminCompatibility(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", writable: true);
            if (key is null) return;
            var value = key.GetValue(exe) as string;
            if (string.IsNullOrWhiteSpace(value)) return;

            var flags = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(flag => !flag.Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // 单独的“~”只是兼容层关闭标记，没有保留价值，直接删除整项。
            if (flags.Length == 0 || flags.All(flag => flag == "~"))
                key.DeleteValue(exe, throwOnMissingValue: false);
            else if (flags.Length != value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                key.SetValue(exe, string.Join(' ', flags), RegistryValueKind.String);
            else
                return;

            AppLog.Info("已清除自启兼容层 RUNASADMIN: " + exe);
        }
        catch (Exception ex)
        {
            AppLog.Warn("清除自启 RUNASADMIN 失败: " + ex.Message);
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
