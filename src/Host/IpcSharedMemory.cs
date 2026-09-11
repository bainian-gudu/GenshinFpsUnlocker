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

    public IpcSharedMemory()
    {
        // 非管理员无 Global\ 权限时 CreateOrOpen 会抛；必须回退，且不得让 Host 启动失败
        MemoryMappedFile? file = null;
        Exception? last = null;
        foreach (var name in new[] { MappingName, MappingNameLocal, @"Local\" + MappingNameLocal })
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

    /// <summary>整体覆盖写入（慎用：会冲掉 Stub 的 Status）。</summary>
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
            _accessor.Read(0, out IpcData data);
            data.TargetFps = Math.Clamp(targetFps, 1, 540);
            data.Enabled = enabled ? 1 : 0;
            data.AntiBlurPerspective = antiBlurPerspective ? 1 : 0;
            data.AntiBlurDiveMosaic = antiBlurDiveMosaic ? 1 : 0;
            data.Magic = Magic;
        _accessor.Write(0, ref data);
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
            _accessor.Read(0, out IpcData data);
            data.Status = IpcStatus.None;
            data.LastError = 0;
            data.CurrentFps = 0;
            data.AntiBlurState = 0;
            data.TargetFps = Math.Clamp(targetFps, 1, 540);
            data.Enabled = enabled ? 1 : 0;
            data.AntiBlurPerspective = antiBlurPerspective ? 1 : 0;
            data.AntiBlurDiveMosaic = antiBlurDiveMosaic ? 1 : 0;
            data.Magic = Magic;
            _accessor.Write(0, ref data);
        }
    }

    /// <summary>通知 Stub 退出工作循环（设置 Status=Exiting）。</summary>
    public void RequestExit()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _accessor.Read(0, out IpcData data);
            data.Status = IpcStatus.Exiting;
            _accessor.Write(0, ref data);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _accessor.Dispose(); } catch { /* ignore */ }
        try { _file.Dispose(); } catch { /* ignore */ }
    }
}
