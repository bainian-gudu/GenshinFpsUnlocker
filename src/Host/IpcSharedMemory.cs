using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace GenshinFpsUnlocker.Host;

/// <summary>与 Stub 侧 IpcStatus 枚举一一对应（共享内存中的 int32）。</summary>
internal enum IpcStatus : int
{
    None = 0,
    Waiting = 1,
    Ready = 2,
    Error = 3,
    Exiting = 4,
}

/// <summary>
/// Host ↔ Stub 共享结构体（Pack=8，字段顺序与 IpcData.h 必须一致）。
/// 协议 v2：新增反虚化开关（Host 写）与就绪状态掩码（Stub 写）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct IpcData
{
    public IpcStatus Status;
    public int LastError;
    public int TargetFps;
    public int Enabled;
    public int CurrentFps;
    public int AntiBlurPerspective;
    public int AntiBlurDiveMosaic;
    public int AntiBlurState;
    public ulong Magic;
}

/// <summary>
/// 基于 Memory-Mapped File 的跨进程 IPC。
/// 优先创建 Global\ 命名对象（跨会话）；失败则回退到本地命名空间。
/// Host 写 TargetFps/Enabled；Stub 写 Status/CurrentFps/LastError。
/// </summary>
internal sealed class IpcSharedMemory : IDisposable
{
    /// <summary>魔数 "FPSUNLKR"，防止误连其它映射。</summary>
    public const ulong Magic = 0x465053554E4C4B52ul;

    /// <summary>全局命名（服务会话/提权场景更稳）。</summary>
    public const string MappingName = @"Global\GenshinFpsUnlocker.Shared.v2";

    /// <summary>本地命名回退（无 Global 权限时）。</summary>
    public const string MappingNameLocal = @"GenshinFpsUnlocker.Shared.v2";

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly object _sync = new();
    private bool _disposed;

    // ---- 字段偏移 ----
    // IpcData 是 Pack=8 的固定布局，偏移在类型加载时算一次。
    // 必须按字段写：整块「读-改-写」会和 Stub 的写入互相覆盖 —— Stub 只在状态
    // 跃迁时写一次 Ready，一旦被 Host 回写成 Waiting，它不会重写，Host 就会
    // 永久显示「Stub 未就绪」（而解锁其实是好的）。CurrentFps / AntiBlurState
    // 被覆盖则表现为 UI 反馈闪回旧值。
    private static readonly long OffStatus = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.Status));
    private static readonly long OffLastError = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.LastError));
    private static readonly long OffTargetFps = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.TargetFps));
    private static readonly long OffEnabled = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.Enabled));
    private static readonly long OffCurrentFps = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.CurrentFps));
    private static readonly long OffAntiBlurPerspective = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.AntiBlurPerspective));
    private static readonly long OffAntiBlurDiveMosaic = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.AntiBlurDiveMosaic));
    private static readonly long OffAntiBlurState = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.AntiBlurState));
    private static readonly long OffMagic = (long)Marshal.OffsetOf<IpcData>(nameof(IpcData.Magic));

    public IpcSharedMemory()
    {
        // 非管理员无 Global\ 权限时 CreateOrOpen 会抛；必须回退，且不得让 Host 启动失败
        MemoryMappedFile? file = null;
        Exception? last = null;
        // 标准用户没有创建 Global\ 对象的权限，CreateOrOpen 会在内部反复重试，
        // 登录时可阻塞约 80 秒才抛出异常。按权限选择顺序，避免无意义的全局重试；
        // Stub 同时尝试 Global 和本地名称，因此两种会话都能正常通信。
        var names = Elevation.IsAdministrator()
            ? new[] { MappingName, MappingNameLocal, @"Local\" + MappingNameLocal }
            : new[] { MappingNameLocal, @"Local\" + MappingNameLocal };
        foreach (var name in names)
        {
            try
            {
                file = MemoryMappedFile.CreateOrOpen(name, Marshal.SizeOf<IpcData>(), MemoryMappedFileAccess.ReadWrite);
                AppLog.Info("IPC MMF opened: " + name);
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                AppLog.Warn($"IPC MMF '{name}' 失败: {ex.Message}");
            }
        }

        if (file is null)
            throw new InvalidOperationException("无法创建共享内存 IPC（Global/Local 均失败）: " + last?.Message);

        _file = file;
        _accessor = _file.CreateViewAccessor(0, Marshal.SizeOf<IpcData>(), MemoryMappedFileAccess.ReadWrite);

        var data = new IpcData
        {
            Status = IpcStatus.None,
            LastError = 0,
            TargetFps = 120,
            Enabled = 1,
            CurrentFps = 0,
            AntiBlurPerspective = 0,
            AntiBlurDiveMosaic = 0,
            AntiBlurState = 0,
            Magic = Magic,
        };
        Write(data);
    }

    /// <summary>
    /// 整体覆盖写入。<b>只允许在构造函数里用</b>（此时 Stub 还没连上来）：
    /// 运行期整块回写会冲掉 Stub 写入的 Status / CurrentFps / AntiBlurState。
    /// </summary>
    public void Write(IpcData data)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _accessor.Write(0, ref data);
        }
    }

    /// <summary>读取当前共享快照。</summary>
    public IpcData Read()
    {
        lock (_sync)
        {
            if (_disposed) return default;
            _accessor.Read(0, out IpcData data);
            return data;
        }
    }

    /// <summary>
    /// 仅更新 Host 侧字段（TargetFps / Enabled / 反虚化开关 / Magic），保留 Stub 写入的 Status 等。
    /// 监视循环应优先调用本方法，避免把 Stub 状态抹成 None。
    /// </summary>
    public void UpdateHostFields(int targetFps, bool enabled, bool antiBlurPerspective = false, bool antiBlurDiveMosaic = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _accessor.Write(OffTargetFps, Math.Clamp(targetFps, 1, 540));
            _accessor.Write(OffEnabled, enabled ? 1 : 0);
            _accessor.Write(OffAntiBlurPerspective, antiBlurPerspective ? 1 : 0);
            _accessor.Write(OffAntiBlurDiveMosaic, antiBlurDiveMosaic ? 1 : 0);
            _accessor.Write(OffMagic, Magic);
        }
    }

    /// <summary>
    /// 新一次注入前：清 Stub 状态/错误，写入 Host 目标，保留 Magic。
    /// </summary>
    public void ResetForNewInject(int targetFps, bool enabled, bool antiBlurPerspective = false, bool antiBlurDiveMosaic = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            // 这是唯一一处 Host 主动写 Stub 字段的地方：新一次注入前把上一轮
            // 的状态清零，否则 WaitForStubReady 会读到上个进程的 Ready/Error。
            _accessor.Write(OffStatus, (int)IpcStatus.None);
            _accessor.Write(OffLastError, 0);
            _accessor.Write(OffCurrentFps, 0);
            _accessor.Write(OffAntiBlurState, 0);
            _accessor.Write(OffTargetFps, Math.Clamp(targetFps, 1, 540));
            _accessor.Write(OffEnabled, enabled ? 1 : 0);
            _accessor.Write(OffAntiBlurPerspective, antiBlurPerspective ? 1 : 0);
            _accessor.Write(OffAntiBlurDiveMosaic, antiBlurDiveMosaic ? 1 : 0);
            _accessor.Write(OffMagic, Magic);
        }
    }

    /// <summary>通知 Stub 退出工作循环（设置 Status=Exiting）。</summary>
    public void RequestExit()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _accessor.Write(OffStatus, (int)IpcStatus.Exiting);
        }
    }

    public void Dispose()
    {
        // 先拿锁再置位：读写方法都在锁内检查 _disposed，否则可能正写着就被释放
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _accessor.Dispose(); } catch { /* ignore */ }
        try { _file.Dispose(); } catch { /* ignore */ }
    }
}
