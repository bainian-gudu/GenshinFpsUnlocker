using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 调用外部命令并带回输出。所有等待都有明确上限：既不会因为子进程卡住无限挂起，
/// 也不会被继承了管道句柄的孙进程拖住。
/// </summary>
internal static class ProcessRunner
{
    /// <summary>进程退出后再给输出管道一点排空时间，避免刚好卡在边界上。</summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 运行 <paramref name="fileName"/>，返回是否正常退出（0 表示成功）以及合并后的输出。
    /// 超时会杀掉整棵进程树并返回 false。
    /// </summary>
    public static bool TryRun(
        string fileName,
        string arguments,
        TimeSpan timeout,
        bool requireZeroExit,
        out string output,
        out int exitCode,
        out bool timedOut)
    {
        output = string.Empty;
        exitCode = -1;
        timedOut = false;
        var deadline = DateTime.UtcNow + timeout;

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
            if (process is null) { return false; }

            // 两条管道必须同时读：只看一条时，另一条写满缓冲区就会互相等死。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!WaitBounded(process.WaitForExitAsync(), deadline))
            {
                KillTree(process);
                timedOut = true;
                return false;
            }

            // 进程已退出，但管道可能还没到 EOF（例如孙进程继承了写端）。排空窗口从
            // **退出这一刻**起算固定 200ms：不能沿用整段 timeout 的剩余预算，
            // 否则进程 0.1s 就退出时这里会白等近 timeout 那么久。
            var drainDeadline = DateTime.UtcNow + DrainWindow;
            var outOk = WaitBounded(stdout, drainDeadline);
            var errOk = WaitBounded(stderr, drainDeadline);
            var text = ((outOk ? stdout.Result : string.Empty) + " " +
                        (errOk ? stderr.Result : string.Empty)).Trim();
            output = text;
            exitCode = process.ExitCode;
            return requireZeroExit ? exitCode == 0 : true;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }

    /// <summary>在 <paramref name="deadline"/> 之前等到 <paramref name="task"/> 完成。</summary>
    private static bool WaitBounded(Task task, DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        // 至少留 1ms 给 WhenAny，超时返回 true 的任务在剩余为 0 时会直接判定超时。
        if (remaining <= TimeSpan.Zero) { return task.IsCompleted; }
        return Task.WhenAny(task, Task.Delay(remaining)).GetAwaiter().GetResult() == task;
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
    }
}
