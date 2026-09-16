namespace GenshinFpsUnlocker.Host.Tests;

/// <summary>
/// 极简断言器：每个用例失败都记下来并继续跑，最后汇总。
/// 一个用例的断言失败不应该掩盖后面用例的结论。
/// </summary>
internal sealed class Harness
{
    private int _passed;
    private int _failed;

    public void Case(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  ok   {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL {name}");
            Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void True(bool condition, string because)
    {
        if (!condition) { throw new InvalidOperationException(because); }
    }

    public static void False(bool condition, string because)
    {
        if (condition) { throw new InvalidOperationException(because); }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{what}：期望 [{expected}]，实际 [{actual}]");
        }
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what}：输出里没有 [{needle}]，实际 [{haystack}]");
        }
    }

    /// <summary>
    /// 跑一个可能**永久挂起**的操作：超时不算「通过」也不让它把整个测试进程拖死，
    /// 而是直接判定失败。ProcessRunner 的核心承诺就是「有上限」，用挂起的操作测它，
    /// 测试自己必须先站得住。
    /// </summary>
    public static T Bounded<T>(Func<T> body, TimeSpan limit, string what)
    {
        var task = Task.Run(body);
        if (!task.Wait(limit))
        {
            throw new TimeoutException($"{what}：超过 {limit.TotalSeconds:n0}s 仍未返回");
        }
        return task.Result;
    }

    public int Report()
    {
        Console.WriteLine($"==== PASS {_passed} / FAIL {_failed} ====");
        return _failed == 0 ? 0 : 1;
    }
}
