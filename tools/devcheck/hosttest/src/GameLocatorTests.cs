namespace GenshinFpsUnlocker.Host.Tests;

/// <summary>
/// GameLocator 的路径推导与扫描策略断言。
///
/// 这些用例针对「游戏装在非官方目录」这条真实诉求：自定义安装必须被扫描命中，
/// 但系统目录要被剪枝、同名假 exe 要被排除、快捷方式目标要能反推回主程序。
/// </summary>
internal static class GameLocatorTests
{
    public static void Run(Harness h)
    {
        h.Case("快扫能在自定义目录找到游戏主程序", () =>
        {
            var root = NewTempRoot();
            try
            {
                var gameDir = Path.Combine(root, "Games", "HoYoPlay", "Genshin Impact", "Genshin Impact Game");
                var streaming = Path.Combine(gameDir, "GenshinImpact_Data", "StreamingAssets");
                Directory.CreateDirectory(streaming);
                File.WriteAllText(Path.Combine(gameDir, "GenshinImpact.exe"), string.Empty);
                File.WriteAllText(Path.Combine(streaming, "data.bin"), "x");

                var result = GameLocator.ScanRoots([root], maxDepth: 5, System.Diagnostics.Stopwatch.StartNew());

                Harness.True(result.Ok, "自定义安装路径必须能被快扫命中");
                Harness.Equal(GameLocateSource.QuickScan, result.Source, "来源");
                Harness.Equal(
                    PathUtil.Normalize(Path.Combine(gameDir, "GenshinImpact.exe")),
                    PathUtil.Normalize(result.Path!),
                    "命中路径");
            }
            finally { Cleanup(root); }
        });

        h.Case("快扫跳过系统目录与 installer", () =>
        {
            var root = NewTempRoot();
            try
            {
                // 放在被剪枝目录里的同名 exe：不该被当成游戏
                var trap = Path.Combine(root, "Windows", "System32", "Genshin Impact Game");
                Directory.CreateDirectory(Path.Combine(trap, "YuanShen_Data", "StreamingAssets"));
                File.WriteAllText(Path.Combine(trap, "YuanShen.exe"), string.Empty);

                // 真正可用的游戏放在普通目录
                var good = Path.Combine(root, "我的游戏", "原神");
                WriteFile(Path.Combine(good, "YuanShen.exe"), string.Empty);
                WriteFile(Path.Combine(good, "YuanShen_Data", "app.info"), "x");

                var result = GameLocator.ScanRoots([root], maxDepth: 5, System.Diagnostics.Stopwatch.StartNew());

                Harness.True(result.Ok, "应命中普通目录里的游戏");
                Harness.True(
                    result.Path!.Contains("我的游戏", StringComparison.Ordinal),
                    $"不该命中被剪枝的 Windows/System32，实际：{result.Path}");
            }
            finally { Cleanup(root); }
        });

        h.Case("资源特征文件可以不在 exe 同级目录", () =>
        {
            var root = NewTempRoot();
            try
            {
                // 主程序在 Genshin Impact Game\ 下，而 _Data 在上一层
                var outer = Path.Combine(root, "Games", "Genshin Impact");
                var inner = Path.Combine(outer, "Genshin Impact Game");
                WriteFile(Path.Combine(inner, "YuanShen.exe"), string.Empty);
                WriteFile(Path.Combine(outer, "YuanShen_Data", "app.info"), "x");

                var result = GameLocator.ScanRoots([root], maxDepth: 4, System.Diagnostics.Stopwatch.StartNew());

                Harness.True(result.Ok, "资源在上一层的布局也要能命中");
                Harness.True(result.Path!.EndsWith("YuanShen.exe", StringComparison.OrdinalIgnoreCase), "应指向 exe");
            }
            finally { Cleanup(root); }
        });

        h.Case("只有 Unity 资源特征的 exe 才算命中的游戏", () =>
        {
            var root = NewTempRoot();
            try
            {
                var fake = Path.Combine(root, "Downloads", "some-tool");
                Directory.CreateDirectory(fake);
                File.WriteAllText(Path.Combine(fake, "GenshinImpact.exe"), string.Empty);

                var result = GameLocator.ScanRoots([root], maxDepth: 3, System.Diagnostics.Stopwatch.StartNew());

                Harness.False(result.Ok, "没有 _Data 特征的假 exe 不该被当成游戏");
            }
            finally { Cleanup(root); }
        });

        h.Case("主程序文件名判定", () =>
        {
            Harness.True(GameLocator.IsCandidateExeName("YuanShen.exe"), "国服主程序");
            Harness.True(GameLocator.IsCandidateExeName("genshinimpact.EXE"), "国际服主程序（大小写不敏感）");
            Harness.False(GameLocator.IsCandidateExeName("GenshinImpact_launcher.exe"), "启动器不是主程序");
            Harness.False(GameLocator.IsCandidateExeName("YuanShen.exe.bak"), "备份文件不是主程序");
        });

        h.Case("目录剪枝规则", () =>
        {
            var temp = NewTempRoot();
            try
            {
                foreach (var name in new[]
                         {
                             "Windows", "System32", "installer", "Installer", "AppData",
                             "node_modules", "$Recycle.Bin",
                         })
                {
                    var dir = Path.Combine(temp, name);
                    Directory.CreateDirectory(dir);
                    Harness.True(GameLocator.ShouldSkipDirectory(dir), $"{name} 应被剪枝");
                }

                foreach (var name in new[] { "Games", "Genshin Impact Game", "原神", "HoYoPlay" })
                {
                    var dir = Path.Combine(temp, name);
                    Directory.CreateDirectory(dir);
                    Harness.False(GameLocator.ShouldSkipDirectory(dir), $"{name} 不该被剪枝");
                }

                // Program Files / ProgramData 只在驱动器根剪枝；显式给定这些目录时仍要能进去找
                foreach (var name in new[] { "Program Files", "ProgramData" })
                {
                    var dir = Path.Combine(temp, name);
                    Directory.CreateDirectory(dir);
                    Harness.True(GameLocator.ShouldSkipDirectory(dir, atDriveRoot: true), $"驱动器根的 {name} 应剪枝");
                    Harness.False(GameLocator.ShouldSkipDirectory(dir), $"显式给定的 {name} 不该剪枝");
                }
            }
            finally { Cleanup(temp); }
        });

        h.Case("快捷方式目标反推：目录目标也能找到主程序", () =>
        {
            var root = NewTempRoot();
            try
            {
                var gameDir = Path.Combine(root, "Games", "原神");
                WriteFile(Path.Combine(gameDir, "YuanShen.exe"), string.Empty);
                WriteFile(Path.Combine(gameDir, "YuanShen_Data", "app.info"), "x");

                var resolved = GameLocator.ResolveFromShortcutTarget(gameDir).FirstOrDefault();

                Harness.True(resolved is not null, "目录目标应反推出主程序");
                Harness.True(
                    resolved!.EndsWith("YuanShen.exe", StringComparison.OrdinalIgnoreCase),
                    $"应指向 YuanShen.exe，实际 {resolved}");
            }
            finally { Cleanup(root); }
        });
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"devcheck-game-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>写文件前先建父目录，避免测试自己踩 DirectoryNotFoundException。</summary>
    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}
