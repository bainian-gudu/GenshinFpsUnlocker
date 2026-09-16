using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 开机自启动：按配置在两种登记方式里二选一，绝不并存（并存会在登录时拉起两个实例）：
/// - 普通权限：HKCU\...\Run 的值（无需管理员、无 UAC），登录后带 --autostart；
/// - 管理员权限：任务计划程序的登录任务 <see cref="ElevatedTaskName"/>
///   （RunLevel=HighestAvailable），登录即以最高权限启动、不弹 UAC。
///   需要同时开启「开机自启动」与「启动时自动以管理员权限运行」，且程序安装在
///   Program Files 下；登记不了会退回 HKCU\Run 并把原因带回界面。
/// 登录后是否进托盘跟随配置 StartMinimized。
///
/// 可靠性约定：
/// - 写入前校验 exe 必须存在；旧值指向已不存在的目录（便携目录被删、
///   开发 dist 目录被重建等）时自动修复重写，避免「重启后开机自启静默失败」。
/// - 每次启动按配置同步：配置为真 → 保证指向当前 exe（自愈）；
///   配置为假但配置文件本身读不到（残损/丢失，拿到的是默认值）→ 不删除，
///   避免「配置意外丢失 → 下次启动把自启项静默删掉 → 重启后自启失败」。
/// - 每次写入/删除都记日志，便于在 logs 中追踪自启项去向。
/// - 同步只有一个入口 <see cref="SyncLoginStartup"/>：启动、导入/重置配置、
///   界面开关都走它，避免出现「计划任务与 Run 值同时存在」或
///   「关掉开关却漏删某一条」这类只在登录时才暴露的问题。
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = AppPaths.ProductName;
    private const string ElevatedTaskName = "GenshinFpsUnlocker.AutoStart";

    /// <summary>登录自启动当前实际登记的通道。</summary>
    internal enum AutostartMode
    {
        /// <summary>没有登记任何自启项。</summary>
        Disabled,

        /// <summary>HKCU\...\Run：登录后以标准权限启动。</summary>
        Standard,

        /// <summary>计划任务：登录后以最高权限启动，不弹 UAC。</summary>
        Elevated,

        /// <summary>想要计划任务但没能登记，已退回 HKCU\Run 兜底。</summary>
        Fallback,
    }

    /// <summary>一次自启同步的结果：实际生效的模式 + 需要告诉用户的提示（无提示为 null）。</summary>
    internal sealed record AutostartReport(AutostartMode Mode, string? Notice);

    /// <summary>
    /// 最近一次同步结果。界面（UiBridge）直接读它，不需要再查一遍注册表 / 任务计划程序。
    /// 启动时 Program 先同步、后建界面，所以首次读到的就是本次登录的真实状态。
    /// </summary>
    public static AutostartReport LastReport { get; private set; } =
        new(AutostartMode.Disabled, null);

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
    /// 按配置同步登录自启动（唯一入口：启动、导入/重置配置、界面开关都走这里）：
    /// - 关闭 → 删计划任务 + 删 HKCU\Run。配置没从磁盘读到（默认值）时保持现状，
    ///   避免「配置意外丢失 → 自启项被静默删掉」。
    /// - 开启 + 管理员 → 登记最高权限计划任务，并删掉 HKCU\Run（两条并存会在登录时
    ///   拉起两个实例）；登记条件不满足时退回 HKCU\Run，把原因放进 <see cref="AutostartReport.Notice"/>。
    /// - 开启 + 普通 → 删计划任务，写 HKCU\Run。
    /// </summary>
    public static AutostartReport SyncLoginStartup(
        bool autoStartWithWindows,
        bool asAdministrator,
        bool configLoadedFromDisk)
    {
        AutostartReport report;
        try
        {
            report = Apply(autoStartWithWindows, asAdministrator, configLoadedFromDisk);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Autostart.SyncLoginStartup: " + ex.Message);
            report = LastReport;
        }

        LastReport = report;
        AppLog.Info(
            $"autostart={autoStartWithWindows} asAdmin={asAdministrator} " +
            $"mode={report.Mode} cmd={GetCommand() ?? "(none)"}" +
            (report.Notice is null ? "" : " notice=" + report.Notice));
        return report;
    }

    private static AutostartReport Apply(bool enabled, bool asAdmin, bool configLoadedFromDisk)
    {
        if (!enabled)
        {
            var current = DescribeCurrentMode();
            if (!configLoadedFromDisk && current != AutostartMode.Disabled)
            {
                AppLog.Warn("配置未从磁盘加载（可能残损/缺失）；保留现有自启项不删除");
                return new AutostartReport(
                    current,
                    "读取 config.json 失败，已保留现有自启动项；请检查配置文件后再改动开关。");
            }

            var taskRemoved = DeleteElevatedTask();
            SetEnabled(false);
            return taskRemoved
                ? new AutostartReport(AutostartMode.Disabled, null)
                : new AutostartReport(
                    AutostartMode.Disabled,
                    "管理员自启动计划任务未能删除：请以管理员身份运行本程序后再确认关闭，"
                    + "否则登录时仍会自动启动。");
        }

        if (asAdmin)
        {
            if (EnsureElevatedTask(out var notice))
            {
                // 登录自启交给计划任务，必须清掉 HKCU\Run，否则登录时两个入口一起启动。
                SetEnabled(false);
                return new AutostartReport(AutostartMode.Elevated, null);
            }

            SetEnabled(true);
            return new AutostartReport(AutostartMode.Fallback, notice);
        }

        if (!DeleteElevatedTask())
        {
            // 计划任务删不掉（多半是当前进程不是管理员）：保留它作为唯一入口，
            // 不能再写 HKCU\Run，否则下次登录会同时被拉起两个实例。
            SetEnabled(false);
            return new AutostartReport(
                AutostartMode.Elevated,
                "旧的管理员自启动计划任务未能删除（需要管理员权限）：登录仍会以管理员权限启动；"
                + "请以管理员身份运行本程序后再改回标准权限自启。");
        }

        SetEnabled(true);
        return new AutostartReport(AutostartMode.Standard, null);
    }

    /// <summary>当前实际登记的通道，用于「配置没读到 → 保持现状」这条分支。</summary>
    private static AutostartMode DescribeCurrentMode()
    {
        if (TryQueryElevatedTaskXml(out _)) return AutostartMode.Elevated;
        return IsEnabled() ? AutostartMode.Standard : AutostartMode.Disabled;
    }

    public static void Remove() => SetEnabled(false);

    /// <summary>
    /// 确保「登录即以最高权限启动」的计划任务存在且指向当前 exe。
    /// 失败时把原因写进 <paramref name="notice"/>，调用方回退到 HKCU\Run。
    /// </summary>
    private static bool EnsureElevatedTask(out string? notice)
    {
        notice = null;
        try
        {
            var exe = AppPaths.ExePath;
            if (!PathUtil.ExistsFile(exe))
            {
                notice = "找不到程序文件，无法登记管理员自启动计划任务。";
                AppLog.Warn("管理员自启动任务未同步：exe 不存在 " + exe);
                return false;
            }

            // 任务已存在且指向当前 exe → 直接复用。标准权限进程也能确认这一点，
            // 不必为了「重建」去要求管理员（否则每次开机都会白跑一次提权判定）。
            if (ElevatedTaskMatches(exe))
            {
                AppLog.Info("管理员自启动任务已存在且指向当前程序");
                return true;
            }

            var schtasks = SchtasksPath;
            if (schtasks is null)
            {
                notice = "系统里找不到 schtasks.exe，管理员自启动未登记；已回退为普通权限自启动。";
                AppLog.Warn("未找到 schtasks.exe，无法配置管理员自启动任务");
                return false;
            }

            // 计划任务以最高权限启动，可执行文件必须放在用户改不了的位置，
            // 否则「用户可写目录 + 登录自动提权」等于把提权入口交给任何能写盘的程序。
            if (!AppPaths.IsInstalledUnderProgramFiles())
            {
                notice = "管理员自启动需要把程序安装在 Program Files 下，当前已回退为普通权限自启动。";
                AppLog.Warn("管理员自启动任务被拒绝：程序目录未受保护 " + AppPaths.ExeDirectory);
                return false;
            }

            var trustError = string.Empty;
            if (!ModuleTrust.IsTrustworthy(exe, "GenshinFpsUnlocker.exe", "自启动程序", out trustError))
            {
                notice = "管理员自启动被拒绝：" + trustError;
                AppLog.Warn("管理员自启动任务被拒绝：" + trustError);
                return false;
            }

            if (!Elevation.IsAdministrator())
            {
                notice = "登记管理员自启动计划任务需要管理员权限：请点「以管理员重新启动」后保持开关开启；"
                         + "在此之前登录自启先以标准权限运行。";
                AppLog.Warn("管理员自启动任务未同步：当前进程不是管理员");
                return false;
            }

            var userSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(userSid))
            {
                notice = "无法确定当前用户账户，管理员自启动未登记；已回退为普通权限自启动。";
                AppLog.Warn("管理员自启动任务未同步：拿不到当前用户 SID");
                return false;
            }

            var xmlPath = Path.Combine(Path.GetTempPath(), AppPaths.ProductName + ".elevated-task.xml");
            File.WriteAllText(xmlPath, BuildElevatedTaskXml(userSid), System.Text.Encoding.Unicode);
            try
            {
                if (!RunSchtasks(schtasks, $"/Create /TN \"{ElevatedTaskName}\" /XML \"{xmlPath}\" /F", out var output))
                {
                    AppLog.Warn("管理员自启动任务创建失败: " + output);
                    notice = "管理员自启动计划任务创建失败：" + OneLine(output) + "；已回退为普通权限自启动。";
                    return false;
                }
            }
            finally { try { File.Delete(xmlPath); } catch { /* ignore */ } }

            AppLog.Info("管理员自启动任务已同步: " + exe);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("EnsureElevatedTask: " + ex.Message);
            notice = "管理员自启动计划任务同步失败：" + ex.Message + "；已回退为普通权限自启动。";
            return false;
        }
    }

    /// <summary>删除登录计划任务；返回 true 表示「本来就没有 / 已删除」。</summary>
    private static bool DeleteElevatedTask()
    {
        try
        {
            var schtasks = SchtasksPath;
            if (schtasks is null) return true;
            if (!TryQueryElevatedTaskXml(out _)) return true;

            // HighestAvailable 的任务只有管理员能改/删，标准权限进程删不掉。
            if (!Elevation.IsAdministrator())
            {
                AppLog.Warn("管理员自启动计划任务存在，但当前进程不是管理员，无法删除");
                return false;
            }

            var ok = RunSchtasks(schtasks, $"/Delete /TN \"{ElevatedTaskName}\" /F", out var output);
            if (ok) AppLog.Info("管理员自启动任务已删除");
            else AppLog.Warn("管理员自启动任务删除失败: " + output);
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn("DeleteElevatedTask: " + ex.Message);
            return false;
        }
    }

    /// <summary>计划任务是否已登记（不校验指向哪个 exe）。</summary>
    private static bool TryQueryElevatedTaskXml(out string xml)
    {
        xml = string.Empty;
        var schtasks = SchtasksPath;
        if (schtasks is null) return false;
        if (!RunSchtasks(schtasks, $"/Query /TN \"{ElevatedTaskName}\" /XML", out var output)) return false;
        xml = output;
        return true;
    }

    /// <summary>计划任务是否已登记且指向当前 exe（路径按 XML 转义后的形式比较）。</summary>
    private static bool ElevatedTaskMatches(string exe)
    {
        if (!TryQueryElevatedTaskXml(out var xml)) return false;
        var escaped = System.Security.SecurityElement.Escape(exe) ?? exe;
        return xml.Contains(escaped, StringComparison.OrdinalIgnoreCase)
               && xml.Contains("--elevated-task", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 登录计划任务 XML。
    /// - InteractiveToken + HighestAvailable：登录即以最高权限启动，不弹 UAC；
    /// - 触发器绑定当前用户 SID，别的账户登录不会拉起它；
    /// - 关掉「仅使用交流电源才启动」等默认限制，并在 <see cref="SyncLoginStartup"/> 里
    ///   校验任务确实登记成功，避免笔记本电池下静默不启动。
    /// </summary>
    private static string BuildElevatedTaskXml(string userSid)
    {
        var esc = System.Security.SecurityElement.Escape;
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n"
            + "  <RegistrationInfo>\n"
            + $"    <Author>{esc(AppPaths.ProductName)}</Author>\n"
            + $"    <Description>登录时以最高权限启动 {esc(AppPaths.ProductDisplayName)}（不弹 UAC）</Description>\n"
            + "  </RegistrationInfo>\n"
            + "  <Triggers>\n"
            + $"    <LogonTrigger><Enabled>true</Enabled><UserId>{esc(userSid)}</UserId></LogonTrigger>\n"
            + "  </Triggers>\n"
            + "  <Principals>\n"
            + $"    <Principal id=\"Author\"><UserId>{esc(userSid)}</UserId>"
            + "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>\n"
            + "  </Principals>\n"
            + "  <Settings>\n"
            + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n"
            + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n"
            + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n"
            + "    <AllowHardTerminate>true</AllowHardTerminate>\n"
            + "    <StartWhenAvailable>true</StartWhenAvailable>\n"
            + "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n"
            + "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n"
            + "    <AllowStartOnDemand>true</AllowStartOnDemand>\n"
            + "    <Enabled>true</Enabled>\n"
            + "    <Hidden>false</Hidden>\n"
            + "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n"
            + "    <WakeToRun>false</WakeToRun>\n"
            + "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n"
            + "    <Priority>7</Priority>\n"
            + "  </Settings>\n"
            + "  <Actions Context=\"Author\">\n"
            + $"    <Exec><Command>{esc(AppPaths.ExePath)}</Command>"
            + "<Arguments>--autostart --elevated-task</Arguments>"
            + $"<WorkingDirectory>{esc(AppPaths.ExeDirectory)}</WorkingDirectory></Exec>\n"
            + "  </Actions>\n"
            + "</Task>";
    }

    /// <summary>
    /// 把登录计划任务 XML 写到指定文件（本机不会创建任何任务）。
    /// 供 <c>--dump-elevated-task-xml</c> 使用：CI 拿它跑一次真实的
    /// <c>schtasks /Create</c> 校验，确保任务计划程序确实接受这份 XML。
    /// </summary>
    public static void DumpElevatedTaskXml(string path)
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
            throw new InvalidOperationException("拿不到当前用户 SID，无法生成登录计划任务 XML");
        File.WriteAllText(path, BuildElevatedTaskXml(userSid), System.Text.Encoding.Unicode);
    }

    /// <summary>schtasks.exe 路径；Windows 上正常必然存在。</summary>
    private static string? SchtasksPath
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
            return PathUtil.ExistsFile(path) ? path : null;
        }
    }

    /// <summary>把 schtasks 的多行输出压成一行，便于在界面上显示提示。</summary>
    private static string OneLine(string? text)
        => string.Join(
            ' ',
            (text ?? string.Empty).Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool RunSchtasks(string fileName, string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) { output = "进程启动失败"; return false; }

            // 两条管道必须同时读：只看一条时，另一条写满 4KB 缓冲区就会互相等死
            // （/Query /XML 的输出正好在这个量级上）。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                output = "执行超时";
                return false;
            }

            output = (stderr.GetAwaiter().GetResult() + " " + stdout.GetAwaiter().GetResult()).Trim();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }

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
