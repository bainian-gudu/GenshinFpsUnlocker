namespace GenshinFpsUnlocker.Host;

/// <summary>
<<<<<<< HEAD
/// 应用图标（AI 生成派蒙主题 app.ico / app.png）。
=======
/// 应用图标（派蒙主题 app.ico / app.png）。
>>>>>>> ff5972c (chore: AppIcon comment for AI Paimon assets)
/// 用于窗体、托盘、快捷方式（exe 内嵌 ApplicationIcon）。
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;
    private static readonly object Lock = new();

    /// <summary>加载应用图标；失败返回 null（调用方回退 SystemIcons）。</summary>
    public static Icon? Load()
    {
        lock (Lock)
        {
            if (_cached is not null) return _cached;

            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    // 从文件克隆，避免长期占用文件句柄
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var tmp = new Icon(fs);
                    _cached = (Icon)tmp.Clone();
                    AppLog.Info("app icon loaded: " + path);
                    return _cached;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("app icon try " + path + ": " + ex.Message);
                }
            }

            try
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exe);
                    if (extracted is not null)
                    {
                        _cached = (Icon)extracted.Clone();
                        extracted.Dispose();
                        AppLog.Info("app icon from exe association");
                        return _cached;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("ExtractAssociatedIcon: " + ex.Message);
            }

            return null;
        }
    }

    /// <summary>供窗体/托盘使用的副本（避免共享 Icon 被 Dispose）。</summary>
    public static Icon? LoadClone()
    {
        var i = Load();
        if (i is null) return null;
        try { return (Icon)i.Clone(); }
        catch { return i; }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var exeDir = AppPaths.ExeDirectory;
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "app.ico");
        yield return Path.Combine(exeDir, "Assets", "app.ico");
        yield return Path.Combine(baseDir, "app.ico");
        yield return Path.Combine(baseDir, "Assets", "app.ico");
        // 开发时：src/Host/Assets
        yield return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "Assets", "app.ico"));
        yield return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "src", "Host", "Assets", "app.ico"));
    }
}
