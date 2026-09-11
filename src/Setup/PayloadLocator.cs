namespace GenshinFpsUnlocker.Setup;

/// <summary>
/// 定位待安装文件来源。
/// 优先：安装器旁的 Payload\ 目录（build.ps1 生成的标准布局）。
/// 回退：安装器所在目录本身（若已含主程序，便于便携测试）。
/// </summary>
internal static class PayloadLocator
{
    public static string? FindPayloadDirectory()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var candidates = new[]
        {
            Path.Combine(baseDir, SetupConstants.PayloadFolderName),
            baseDir,
            // 开发时：从 src/Setup 运行，尝试仓库 dist
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "dist")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "dist")),
        };

        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            var exe = Path.Combine(dir, SetupConstants.ExeName);
            if (File.Exists(exe))
                return Path.GetFullPath(dir);
        }

        return null;
    }

    public static bool IsSelfContained(string payloadDir)
        => File.Exists(Path.Combine(payloadDir, "coreclr.dll"))
           || File.Exists(Path.Combine(payloadDir, "hostfxr.dll"));
}
