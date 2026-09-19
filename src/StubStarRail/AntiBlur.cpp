// =============================================================================
// 星穹铁道解除角色虚化实现。
//
// 4.5.0 dump.cs：
//   VCameraDOFEffectOverride.EnableDOF // Offset: 0x18
//   OnActiveVCamera  RVA 0x1C7FD870
//   Update           RVA 0x1C7FCFC0
//
// 采用「原函数先执行、再写 false」的顺序，避免游戏在 OnActiveVCamera / Update
// 内重新把 EnableDOF 置回 true。关闭开关时不写字段，完全交还游戏控制。
// =============================================================================

#include "AntiBlur.h"

#include <cstddef>
#include <cstdint>

#include "MinHook.h"

namespace
{
    // VCameraDOFEffectOverride.EnableDOF // Offset: 0x18
    constexpr size_t kEnableDofOffset = 0x18;

    using DofEntryFn = void (*)(void* self);

    void* g_boundIpc = nullptr;
    void* g_originalOnActive = nullptr;
    void* g_originalUpdate = nullptr;
    bool g_onActiveReady = false;
    bool g_updateReady = false;

    /// <summary>
    /// 把 EnableDOF 压为 false。只对已确认的类实例使用；SEH 兜底，避免版本
    /// 变化导致偏移失效时把游戏打崩。
    /// </summary>
    void ApplyOverride(void* self)
    {
        IpcData* ipc = static_cast<IpcData*>(g_boundIpc);
        if (!self || !ipc || ipc->AntiBlurPerspective == 0)
        {
            return;
        }

#if defined(_MSC_VER)
        __try
        {
            *reinterpret_cast<bool*>(reinterpret_cast<uint8_t*>(self) + kEnableDofOffset) = false;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // 版本更新导致字段偏移变化时只放弃本次写入，不向上传播异常。
        }
#else
        *reinterpret_cast<bool*>(reinterpret_cast<uint8_t*>(self) + kEnableDofOffset) = false;
#endif
    }

    void HookOnActiveVCamera(void* self)
    {
        if (g_originalOnActive)
        {
            reinterpret_cast<DofEntryFn>(g_originalOnActive)(self);
        }
        ApplyOverride(self);
    }

    void HookUpdate(void* self)
    {
        if (g_originalUpdate)
        {
            reinterpret_cast<DofEntryFn>(g_originalUpdate)(self);
        }
        ApplyOverride(self);
    }
}

namespace AntiBlur
{
    bool Initialize(IpcData* ipc, void* onActiveVCamera, void* update)
    {
        if (!ipc)
        {
            return false;
        }

        g_boundIpc = ipc;

        if (!g_onActiveReady && onActiveVCamera)
        {
            g_onActiveReady =
                MH_CreateHook(onActiveVCamera, reinterpret_cast<void*>(&HookOnActiveVCamera),
                              &g_originalOnActive) == MH_OK;
        }

        if (!g_updateReady && update)
        {
            g_updateReady =
                MH_CreateHook(update, reinterpret_cast<void*>(&HookUpdate),
                              &g_originalUpdate) == MH_OK;
        }

        const bool ready = g_onActiveReady || g_updateReady;
        ipc->AntiBlurState =
            ready ? static_cast<int32_t>(IpcAntiBlurState::PerspectiveReady) : 0;
        return ready;
    }

    void Shutdown(IpcData* ipc)
    {
        // Hook 由 dllmain 统一 MH_DisableHook(MH_ALL_HOOKS)；这里保留 g_original*
        // 与 ready 标志，使同一进程内的重试保持幂等。
        if (ipc)
        {
            ipc->AntiBlurState = static_cast<int32_t>(IpcAntiBlurState::None);
        }
    }
}
