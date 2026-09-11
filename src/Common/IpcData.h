#pragma once

#include <cstdint>

// =============================================================================
// Host ↔ Stub 共享内存布局（须与 C# IpcSharedMemory / IpcData 保持二进制一致）
// 映射名：Global\GenshinFpsUnlocker.Shared.v2
// Magic ：0x465053554E4C4B52ull  （ASCII "FPSUNLKR"）
// Pack  ：8 字节对齐
//
// 协议 v2：新增反虚化注入功能（反角色虚化 / 移除水下马赛克）的开关与就绪状态。
// =============================================================================

/// <summary>Stub 生命周期状态（由 Stub 写入，Host 读取）。</summary>
enum class IpcStatus : int32_t
{
    None    = 0,  // 未初始化 / 已退出
    Waiting = 1,  // 已连接共享内存，正在解析特征码
    Ready   = 2,  // 钩子已启用，可接受目标 FPS
    Error   = 3,  // 解析或钩子失败
    Exiting = 4,  // Host 请求 Stub 退出工作线程
};

/// <summary>AntiBlurState 位掩码（Stub 写入，Host 读取）。</summary>
enum class IpcAntiBlurState : int32_t
{
    None                = 0,
    PerspectiveReady    = 1 << 0,  // 反角色虚化 Hook 已就绪（特征解析成功）
    DiveMosaicReady     = 1 << 1,  // 移除水下马赛克 Patch 已就绪（call 点已定位）
    DiveMosaicPatched   = 1 << 2,  // 移除水下马赛克当前处于已 Patch 生效状态
};

#pragma pack(push, 8)
struct IpcData
{
    IpcStatus Status;              // Stub 写入：当前状态
    int32_t   LastError;           // Stub 写入：Win32 或自定义错误码
    int32_t   TargetFps;           // Host 写入：目标帧率（1..540）
    int32_t   Enabled;             // Host 写入：是否启用解锁（0/1）
    int32_t   CurrentFps;          // Stub 写入：最近一次实际写入的 FPS（反馈）
    int32_t   AntiBlurPerspective; // Host 写入：反角色虚化开关（0/1）
    int32_t   AntiBlurDiveMosaic;  // Host 写入：移除水下马赛克开关（0/1）
    int32_t   AntiBlurState;       // Stub 写入：反虚化功能就绪状态（IpcAntiBlurState 位掩码）
    uint64_t  Magic;               // 魔数，用于校验映射是否为本协议
};
#pragma pack(pop)

inline constexpr uint64_t kIpcMagic = 0x465053554E4C4B52ull; // "FPSUNLKR"
inline constexpr wchar_t kIpcMappingName[] = L"Global\\GenshinFpsUnlocker.Shared.v2";
inline constexpr wchar_t kIpcMutexName[]   = L"Global\\GenshinFpsUnlocker.Instance.v1";
