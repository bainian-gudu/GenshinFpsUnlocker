using System.Collections.Concurrent;
using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>日志级别（数值越大越严重）。</summary>
internal enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// 缓冲型文件日志。默认开启 Debug。
/// 路径：%LocalAppData%\GenshinFpsUnlocker\logs\app-yyyyMMdd.log
/// 通过队列批量刷盘降低 I/O 开销；Error 立即刷盘。UTF-8 编码支持中文。
/// </summary>
internal static class AppLog
{
    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<string> Recent = new();
    private static readonly ConcurrentQueue<string> PendingWrite = new();
    private const int RecentCap = 400;
    private const int FlushThreshold = 16;

    private static bool _initialized;
    private static bool _enabled = true;
    private static LogLevel _minLevel = LogLevel.Debug;
    private static string? _filePath;
    private static int _sessionId;
    private static Timer? _flushTimer;
    private static int _pendingCount;

    /// <summary>当前日志文件完整路径（可能为 null）。</summary>
    public static string? CurrentFilePath => _filePath;

    /// <summary>是否启用文件日志（DebugLogging）。</summary>
    public static bool Enabled => _enabled;

    /// <summary>
    /// 根据配置初始化日志目录、级别与定时刷盘。
    /// 应在进程启动尽早调用（含 --install / --uninstall 路径）。
    /// </summary>
    public static void Initialize(AppConfig config)
    {
        _enabled = config.DebugLogging;
        _minLevel = ParseLevel(config.LogLevel);
        _sessionId = Environment.ProcessId;

        try
        {
            PathUtil.EnsureDir(AppPaths.LogDirectory);
            _filePath = Path.Combine(AppPaths.LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _filePath = null;
        }

        _initialized = true;

        // 周期性刷盘，避免每行都 fsync
        _flushTimer = new Timer(_ => FlushPending(), null, 1000, 1000);

        Info("========== session start ==========");
        Info($"pid={_sessionId} exe={AppPaths.ExePath}");
        Info($"installDir={AppPaths.ExeDirectory}");
        Info($"dataDir={AppPaths.DataDirectory}");
        Info($"logFile={_filePath}");
        Info($"debugLogging={_enabled} minLevel={_minLevel}");
        Info($"os={Environment.OSVersion} 64bitOS={Environment.Is64BitOperatingSystem} 64bitProc={Environment.Is64BitProcess}");
        Info($"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Info($"admin={IsAdminRough()} stubExists={PathUtil.ExistsFile(AppPaths.StubDllPath)}");

        try { TrimOldLogs(keepDays: Math.Clamp(config.LogRetainDays, 1, 90)); }
        catch (Exception ex) { Warn($"清理旧日志失败: {ex.Message}"); }

        FlushPending();
    }

    /// <summary>运行中热更新日志开关/级别（托盘勾选调试日志时）。</summary>
    public static void ApplyConfig(AppConfig config)
    {
        _enabled = config.DebugLogging;
        _minLevel = ParseLevel(config.LogLevel);
        Info($"日志设置已更新: enabled={_enabled} minLevel={_minLevel}");
        FlushPending();
    }

    public static void Trace(string message) => Write(LogLevel.Trace, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>记录异常（含类型与堆栈）。</summary>
    public static void Error(Exception ex, string message)
        => Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>内存中最近日志行（供 UI 诊断窗口使用）。</summary>
    public static IReadOnlyList<string> GetRecentLines(int max = 200)
    {
        var arr = Recent.ToArray();
        if (arr.Length <= max) return arr;
        return arr[^max..];
    }

    /// <summary>用资源管理器打开日志目录。</summary>
    public static void OpenLogFolder()
    {
        try
        {
            PathUtil.EnsureDir(AppPaths.LogDirectory);
            FlushPending();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error(ex, "打开日志目录");
        }
    }

    /// <summary>打开当日日志文件；不存在则退回打开目录。</summary>
    public static void OpenCurrentLogFile()
    {
        try
        {
            FlushPending();
            if (_filePath is null || !PathUtil.ExistsFile(_filePath))
            {
                OpenLogFolder();
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _filePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error(ex, "打开日志文件");
            OpenLogFolder();
        }
    }

    /// <summary>进程退出前刷盘并写 session end。</summary>
    public static void Shutdown()
    {
        try { _flushTimer?.Dispose(); } catch { /* ignore */ }
        _flushTimer = null;
        FlushPending();
        Info("========== session end ==========");
        FlushPending();
    }

    private static void Write(LogLevel level, string message)
    {
        if (!_initialized) return;
        // 关闭调试日志时仍保留 Error，避免静默丢关键错误
        if (!_enabled && level < LogLevel.Error) return;
        if (level < _minLevel && level < LogLevel.Error) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] [T{Environment.CurrentManagedThreadId}] {message}";

        Recent.Enqueue(line);
        while (Recent.Count > RecentCap && Recent.TryDequeue(out _)) { }

        if (_filePath is null) return;

        PendingWrite.Enqueue(line);
        var count = Interlocked.Increment(ref _pendingCount);

        // Error 立即刷；其它达到阈值再刷
        if (level >= LogLevel.Error || count >= FlushThreshold)
            FlushPending();
    }

    /// <summary>将待写队列一次性追加到文件（UTF-8）。</summary>
    private static void FlushPending()
    {
        if (_filePath is null) return;
        if (PendingWrite.IsEmpty) return;

        lock (FileLock)
        {
            try
            {
                var sb = new StringBuilder();
                while (PendingWrite.TryDequeue(out var line))
                {
                    sb.Append(line);
                    sb.Append(Environment.NewLine);
                    Interlocked.Decrement(ref _pendingCount);
                }

                if (sb.Length == 0) return;

                File.AppendAllText(_filePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志层绝不能向外抛
            }
        }
    }

    private static LogLevel ParseLevel(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return LogLevel.Debug;
        return Enum.TryParse<LogLevel>(s, ignoreCase: true, out var lv) ? lv : LogLevel.Debug;
    }

    /// <summary>删除超过保留天数的 app-yyyyMMdd.log。</summary>
    private static void TrimOldLogs(int keepDays)
    {
        var cutoff = DateTime.Now.Date.AddDays(-keepDays);
        foreach (var f in Directory.EnumerateFiles(AppPaths.LogDirectory, "app-*.log"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var datePart = name.Length >= 12 ? name[^8..] : null;
                if (datePart is not null
                    && DateTime.TryParseExact(datePart, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)
                    && d < cutoff)
                {
                    File.Delete(f);
                }
            }
            catch { /* ignore */ }
        }
    }

    private static bool IsAdminRough()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
