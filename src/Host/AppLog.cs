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
/// 路径：%LocalAppData%\GenshinFpsUnlocker\logs\app-yyyyMMdd.log（跨日自动换文件）
/// 通过队列批量刷盘降低 I/O 开销；Error 立即刷盘。UTF-8 编码支持中文。
///
/// 内存里保留最近若干条<b>结构化</b>记录（<see cref="Entry"/>），一方面供 UI 的
/// 日志页读取（<see cref="GetRecentEntries"/>，不用再从格式化字符串里反解析级别），
/// 另一方面通过 <see cref="EntryLogged"/> 事件让 UI 做增量推送。
/// </summary>
internal static class AppLog
{
    /// <summary>一条日志记录（时间戳为本地时间，与文件里的格式一致）。</summary>
    public sealed record Entry(DateTime Timestamp, LogLevel Level, int ThreadId, string Message)
    {
        /// <summary>级别名与前端约定的字符串一致（Trace/Debug/Info/Warn/Error）。</summary>
        public string LevelName => Level.ToString();

        /// <summary>UTC 的 ISO-8601 时间戳，供前端 <c>&lt;time datetime&gt;</c> 使用。</summary>
        public string UtcTimestamp => Timestamp.ToUniversalTime().ToString("O");
    }

    private static readonly object FileLock = new();
    private static readonly ConcurrentQueue<Entry> Recent = new();
    private static readonly ConcurrentQueue<string> PendingWrite = new();
    private const int RecentCap = 400;
    private const int FlushThreshold = 16;

    private static bool _initialized;
    private static bool _enabled = true;
    private static LogLevel _minLevel = LogLevel.Debug;
    private static string? _filePath;
    private static DateOnly _fileDate;
    private static int _sessionId;
    private static System.Threading.Timer? _flushTimer;
    private static int _pendingCount;

    /// <summary>
    /// 每写入一条日志就触发（在<b>写日志的那个线程</b>上，且已过滤掉被关掉的级别）。
    /// 订阅方若要碰 UI 必须自己 marshaling；处理器里也不要再写日志（会递归）。
    /// </summary>
    public static event Action<Entry>? EntryLogged;

    /// <summary>当前日志文件完整路径（可能为 null）。</summary>
    public static string? CurrentFilePath => _filePath;

    /// <summary>是否启用文件日志（DebugLogging）。</summary>
    public static bool Enabled => _enabled;

    /// <summary>
    /// 根据配置初始化日志目录、级别与定时刷盘。
    /// 应在进程启动尽早调用（含 --autostart 静默路径）。
    /// </summary>
    public static void Initialize(AppConfig config)
    {
        _enabled = config.DebugLogging;
        _minLevel = ParseLevel(config.LogLevel);
        _sessionId = Environment.ProcessId;

        try
        {
            PathUtil.EnsureDir(AppPaths.LogDirectory);
            _fileDate = DateOnly.FromDateTime(DateTime.Now);
            _filePath = Path.Combine(AppPaths.LogDirectory, $"app-{_fileDate:yyyyMMdd}.log");
        }
        catch
        {
            _filePath = null;
        }

        // 允许启动路径二次调用：只补定时器，不重复刷 session banner
        var first = !_initialized;
        _initialized = true;

        if (_flushTimer is null)
            _flushTimer = new System.Threading.Timer(_ => FlushPending(), null, 1000, 1000);

        if (!first)
        {
            Info($"日志重新绑定: enabled={_enabled} minLevel={_minLevel} file={_filePath}");
            FlushPending();
            return;
        }

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
        var prevEn = _enabled;
        var prevLv = _minLevel;
        _enabled = config.DebugLogging;
        _minLevel = ParseLevel(config.LogLevel);
        if (prevEn != _enabled || prevLv != _minLevel)
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

    /// <summary>内存中最近的日志记录（供 UI 日志页使用，免去从字符串反解析）。</summary>
    public static IReadOnlyList<Entry> GetRecentEntries(int max = 200)
    {
        var arr = Recent.ToArray();
        if (arr.Length > max) arr = arr[^max..];
        return arr;
    }

    /// <summary>内存中最近日志行的格式化文本（导出 / 诊断窗口用）。</summary>
    public static IReadOnlyList<string> GetRecentLines(int max = 200)
    {
        var entries = GetRecentEntries(max);
        var list = new List<string>(entries.Count);
        foreach (var e in entries) list.Add(FormatLine(e));
        return list;
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
            })?.Dispose();
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
            })?.Dispose();
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

    private static string FormatLine(Entry e) =>
        $"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{e.LevelName.ToUpperInvariant(),-5}] [T{e.ThreadId}] {e.Message}";

    private static void Write(LogLevel level, string message)
    {
        if (!_initialized) return;
        // 关闭调试日志时仍保留 Error，避免静默丢关键错误
        if (!_enabled && level < LogLevel.Error) return;
        if (level < _minLevel && level < LogLevel.Error) return;

        var entry = new Entry(DateTime.Now, level, Environment.CurrentManagedThreadId, message);

        Recent.Enqueue(entry);
        while (Recent.Count > RecentCap && Recent.TryDequeue(out _)) { }

        // UI 增量推送。处理器自己负责 marshaling 与异常兜底；这里再兜一层，
        // 免得某个订阅者把异常抛回写日志的调用方。
        try { EntryLogged?.Invoke(entry); } catch { /* ignore */ }

        if (_filePath is null) return;

        PendingWrite.Enqueue(FormatLine(entry));
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
                // 跨日换文件：常驻多日的进程不能一直往启动那天的文件里写
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (today != _fileDate)
                {
                    _fileDate = today;
                    _filePath = Path.Combine(AppPaths.LogDirectory, $"app-{today:yyyyMMdd}.log");
                    PathUtil.EnsureDir(AppPaths.LogDirectory);
                }

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
