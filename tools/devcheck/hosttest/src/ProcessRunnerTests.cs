using System.Diagnostics;

namespace GenshinFpsUnlocker.Host.Tests;

/// <summary>
/// ProcessRunner 的行为断言。
///
/// 为什么单测这个类：它是宿主里唯一「等外部进程」的公共通道，历史上出过
/// 「超时后只杀了直接子进程 / 排空窗口用掉整段 timeout」这类只在特定路径上复现的挂起。
/// 这里把每条承诺都钉住。
/// </summary>
internal static class ProcessRunnerTests
{
    public static void Run(Harness h)
    {
        h.Case("正常退出：合并 stdout/stderr，退出码 0 时返回 true", () =>
        {
            var result = Run(Shell.FileName, Shell.EchoBoth, TimeSpan.FromSeconds(10));
            Harness.True(result.Ok, $"期望成功，实际 output=[{result.Output}]");
            Harness.Equal(0, result.ExitCode, "退出码");
            Harness.False(result.TimedOut, "不应标记超时");
        });

        h.Case("非 0 退出码：requireZeroExit=true 时返回 false", () =>
        {
            var result = Run(Shell.FileName, Shell.Exit7, TimeSpan.FromSeconds(10));
            Harness.False(result.Ok, "退出码 7 不该算成功");
            Harness.Equal(7, result.ExitCode, "退出码");
        });

        h.Case("非 0 退出码：requireZeroExit=false 时返回 true 并带回退出码", () =>
        {
            var result = Run(
                Shell.FileName, Shell.Exit7, TimeSpan.FromSeconds(10), requireZeroExit: false);
            Harness.True(result.Ok, "requireZeroExit=false 时非 0 退出码也算正常返回");
            Harness.Equal(7, result.ExitCode, "退出码");
        });

        h.Case("超时：返回 false 并标记 timedOut", () =>
        {
            var result = Run(Shell.FileName, Shell.SleepLong, TimeSpan.FromSeconds(1));
            Harness.False(result.Ok, "超时必须返回 false");
            Harness.True(result.TimedOut, "必须标记 timedOut（调用方据此给出「执行超时」而不是「启动失败」）");
        });

        h.Case("超时会真的杀掉目标进程：超时后不该再留下标记文件", () =>
        {
            // 「返回有上限」不等于「进程真被杀」。子进程会在约 5s 后写下标记文件：
            // 只有 KillTree 生效时它才永远不会出现。
            var marker = Path.Combine(
                Path.GetTempPath(), $"devcheck-hosttest-marker-{Guid.NewGuid():N}.txt");
            try
            {
                var result = Run(Shell.FileName, Shell.ExitAfterWritingMarker(marker), TimeSpan.FromSeconds(1));
                Harness.False(result.Ok, "超时必须返回 false");
                Harness.True(result.TimedOut, "必须标记 timedOut");

                Thread.Sleep(Shell.MarkerDelay + TimeSpan.FromSeconds(1));
                Harness.False(
                    File.Exists(marker),
                    "超时后子进程还在跑并写出了标记文件 —— 进程树没有被杀掉");
            }
            finally
            {
                if (File.Exists(marker)) { File.Delete(marker); }
            }
        });

        h.Case("可执行文件不存在：返回 false、不抛异常", () =>
        {
            var result = Run("definitely-not-a-real-executable-xyz", string.Empty, TimeSpan.FromSeconds(2));
            Harness.False(result.Ok, "启动失败必须返回 false");
            Harness.True(!string.IsNullOrWhiteSpace(result.Output), "应把异常信息带进 output");
        });

        h.Case("两个管道同时写满 4KB 以上不死锁", () =>
        {
            var result = Run(Shell.FileName, Shell.FloodBothPipes, TimeSpan.FromSeconds(30));
            Harness.True(result.Ok, "并发读两个流时不该死锁，实际失败：" + result.Output);
            Harness.Contains(result.Output, "STDOUT-END", "stdout 内容");
            Harness.Contains(result.Output, "STDERR-END", "stderr 内容");
        });

        h.Case("孙进程继承管道写端：返回不等待它退出", () =>
        {
            // 父进程立刻退出，但它派生的孙进程继承了 stdout/stderr 写端并睡 10 秒。
            // 排空窗口必须从「退出那一刻」起算固定 200ms；沿用整段 timeout 的剩余预算
            // 会在这里白等到孙进程结束。
            var sw = Stopwatch.StartNew();
            var result = Harness.Bounded(
                () => Run(Shell.FileName, Shell.SpawnPipeHoldingGrandchild, TimeSpan.FromSeconds(10)),
                TimeSpan.FromSeconds(6),
                "孙进程持有管道时 TryRun 必须尽快返回");
            sw.Stop();

            Harness.True(result.Ok, "父进程退出码为 0，应算成功：" + result.Output);
            Harness.Equal(0, result.ExitCode, "退出码");
            Harness.False(result.TimedOut, "父进程已经退出，不该走到超时分支");
            Harness.True(
                sw.Elapsed < TimeSpan.FromSeconds(2),
                $"排空窗口应固定 200ms，实际耗时 {sw.Elapsed.TotalSeconds:n1}s");
        });
    }

    private static RunResult Run(
        string fileName, string arguments, TimeSpan timeout, bool requireZeroExit = true)
    {
        var ok = ProcessRunner.TryRun(
            fileName, arguments, timeout, requireZeroExit,
            out var output, out var exitCode, out var timedOut);
        return new RunResult(ok, exitCode, timedOut, output);
    }

    private sealed record RunResult(bool Ok, int ExitCode, bool TimedOut, string Output);
}

/// <summary>
/// 平台无关的外部命令片段。Windows 用 cmd / ping，类 Unix 用 sh / sleep。
/// </summary>
internal static class Shell
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static string FileName => IsWindows ? "cmd.exe" : "/bin/sh";

    public static string EchoBoth => IsWindows ? "/c echo hello" : "-c \"echo hello\"";

    public static string Exit7 => IsWindows ? "/c exit 7" : "-c \"exit 7\"";

    public static string SleepLong => IsWindows ? "/c ping -n 30 127.0.0.1 > nul" : "-c \"sleep 30\"";

    /// <summary>标记文件最早出现的时间；远大于下面用到的超时值。</summary>
    public static TimeSpan MarkerDelay => TimeSpan.FromSeconds(5);

    /// <summary>stdout / stderr 各写 8KB，确保两侧都超过 Windows 上 4KB 的管道缓冲。</summary>
    public static string FloodBothPipes => IsWindows
        ? "/c \"for /L %i in (1,1,200) do @(echo STDOUT-LINE-%i & echo STDERR-LINE-%i 1>&2) & echo STDOUT-END & echo STDERR-END 1>&2\""
        : "-c \"i=0; while [ $i -lt 200 ]; do echo STDOUT-LINE-$i; echo STDERR-LINE-$i 1>&2; i=$((i+1)); done; echo STDOUT-END; echo STDERR-END 1>&2\"";

    /// <summary>
    /// 父进程起一个后台孙进程（持有继承来的 stdout/stderr）后立刻退出。
    /// Windows：start /b 拆出子进程，ping 跑 10 秒；类 Unix：sh &amp; 拆出子进程，sleep 10。
    /// </summary>
    public static string SpawnPipeHoldingGrandchild => IsWindows
        ? "/c start /b ping -n 10 127.0.0.1 > nul"
        : "-c \"sleep 10 & exit 0\"";

    /// <summary>
    /// 睡够 MarkerDelay 之后才写下标记文件。不用重定向，避免两层引号在 cmd 上打架；
    /// 参数都是不带空格的临时路径。调用方给的超时远小于这个时间：
    /// 正确杀掉进程 → 文件永不出现；没杀掉 → 到点就出现。
    /// </summary>
    public static string ExitAfterWritingMarker(string markerPath) => IsWindows
        ? $"/c ping -n 6 127.0.0.1 & echo alive>{markerPath}"
        : $"-c \"sleep {MarkerDelay.TotalSeconds:n0}; echo alive > '{markerPath}'\"";
}
