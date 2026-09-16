namespace GenshinFpsUnlocker.Host.Tests;

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("hosttest — Host 行为断言（自包含，无外部测试框架）");
        var harness = new Harness();
        ProcessRunnerTests.Run(harness);
        GameLocatorTests.Run(harness);
        return harness.Report();
    }
}
