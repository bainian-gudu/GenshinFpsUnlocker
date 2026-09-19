// =============================================================================
// 星穹铁道隐藏 UID 水印实现。
//
// 主线程入口：RPG.Client.RPGApplication.OnUpdate RVA 0x1802FBF0（dump.cs 实测）。
// 该入口每帧执行，符合 Unity 对象接口必须在主线程调用的约束。
//
// 隐藏对象：两条路径对应的 UnityEngine.UI.Graphic。
// 修改字段：Graphic.m_Color // Offset 0x20，alpha 位于 +0x0C。
// 不关闭 s_UICamera、不 SetActive(false)，避免把整个 HUD 一起隐藏。
// =============================================================================

#include "HideUid.h"

#include <atomic>
#include <cstdint>

#include "Il2CppBridge.h"
#include "MinHook.h"

namespace
{
    constexpr DWORD kTickIntervalMs = 100;
    constexpr DWORD kRestoreWaitMs = 250;

    constexpr const char* kUidPaths[] = {
        "/UIRoot/AboveDialog/BetaHintDialog(Clone)/Contents/VersionText",
        "/UIRoot/Page/MobilePhoneMainPage(Clone)/Content/Content/LeftPlane/Tittle/UID/NumText",
    };

    struct UidTarget
    {
        const char* path;
        float originalAlpha;
        bool originalCaptured;
        bool hiddenByUs;
    };

    // 仅游戏主线程访问。
    UidTarget g_targets[] = {
        { kUidPaths[0], 1.0f, false, false },
        { kUidPaths[1], 1.0f, false, false },
    };

    void* g_boundIpc = nullptr;
    void* g_originalOnUpdate = nullptr;
    bool g_onUpdateReady = false;

    DWORD g_lastTickMs = 0;

    std::atomic_bool g_hidAnyObject{ false };
    std::atomic_bool g_restoreRequested{ false };
    std::atomic_bool g_faulted{ false };

    using OnUpdateFn = void (*)(void* self);

    /// <summary>
    /// 主线程 tick（内部会构造 il2cpp 字符串等 C++ 对象，不能直接与 __try 同函数）。
    /// </summary>
    void MainThreadTick()
    {
        IpcData* ipc = static_cast<IpcData*>(g_boundIpc);
        if (!ipc)
        {
            return;
        }

        const bool restore = g_restoreRequested.exchange(false, std::memory_order_relaxed);
        const DWORD now = GetTickCount();
        if (!restore && (now - g_lastTickMs) < kTickIntervalMs)
        {
            return;
        }
        g_lastTickMs = now;

        const bool wantHide = !restore && ipc->HideUid != 0;

        // 关闭且没有任何由本方隐藏的对象时，不执行 Find，保持零开销。
        bool anyHiddenBefore = false;
        for (const auto& target : g_targets)
        {
            anyHiddenBefore = anyHiddenBefore || target.hiddenByUs;
        }
        if (!wantHide && !anyHiddenBefore)
        {
            ipc->HideUidState = static_cast<int32_t>(IpcHideUidState::Ready);
            return;
        }

        bool anyFoundHidden = false;
        for (auto& target : g_targets)
        {
            void* graphic = Il2CppBridge::FindGraphic(target.path);
            if (!graphic)
            {
                continue;
            }

            float alpha = 1.0f;
            if (!Il2CppBridge::ReadGraphicAlpha(graphic, alpha))
            {
                continue;
            }

            if (wantHide)
            {
                // UI 重建后新对象的默认 alpha 可能比旧对象更大；保留见过的
                // 最大非零值，关闭开关时恢复到这个值，而不是旧的中间态。
                if (!target.originalCaptured || alpha > target.originalAlpha)
                {
                    // 首次隐藏前保存原值；0 视为无效，回退 1.0。
                    target.originalAlpha = alpha > 0.0f ? alpha : 1.0f;
                    target.originalCaptured = true;
                }
                if (alpha != 0.0f)
                {
                    if (Il2CppBridge::WriteGraphicAlpha(graphic, 0.0f))
                    {
                        Il2CppBridge::NotifyGraphicColorChanged(graphic);
                    }
                }
                target.hiddenByUs = true;
                anyFoundHidden = true;
            }
            else if (target.hiddenByUs)
            {
                if (Il2CppBridge::WriteGraphicAlpha(graphic, target.originalAlpha))
                {
                    Il2CppBridge::NotifyGraphicColorChanged(graphic);
                }
                target.hiddenByUs = false;
            }
        }

        // hiddenByUs 用于退出时判断「是否还需要请求主线程恢复」；
        // anyFoundHidden 才是本次真正生效的 Active 状态。
        bool anyHiddenByUs = false;
        for (const auto& target : g_targets)
        {
            anyHiddenByUs = anyHiddenByUs || target.hiddenByUs;
        }
        g_hidAnyObject.store(anyHiddenByUs, std::memory_order_relaxed);
        ipc->HideUidState =
            static_cast<int32_t>(IpcHideUidState::Ready) |
            (anyFoundHidden ? static_cast<int32_t>(IpcHideUidState::Active) : 0);
    }

    /// <summary>
    /// SEH 版本：一旦 tick 内出现访问违例，标记 faulted 并停止后续执行，
    /// 由 dllmain 上报 Error 后统一卸钩。
    /// </summary>
    void MainThreadTickSafe()
    {
        if (g_faulted.load(std::memory_order_relaxed))
        {
            return;
        }
#if defined(_MSC_VER)
        __try
        {
            MainThreadTick();
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            g_faulted.store(true, std::memory_order_relaxed);
        }
#else
        MainThreadTick();
#endif
    }

    void HookOnUpdate(void* self)
    {
        if (g_originalOnUpdate)
        {
            reinterpret_cast<OnUpdateFn>(g_originalOnUpdate)(self);
        }
        MainThreadTickSafe();
    }
}

namespace HideUid
{
    bool Initialize(IpcData* ipc, void* rpgApplicationOnUpdate)
    {
        if (!ipc || !rpgApplicationOnUpdate)
        {
            return false;
        }

        g_boundIpc = ipc;
        // 新一轮会话开始时清除上一轮的 tick 故障标志；否则 Host 重试后
        // 会立刻被旧的 faulted 状态再次判为 Error。
        g_faulted.store(false, std::memory_order_relaxed);

        if (!g_onUpdateReady)
        {
            g_onUpdateReady =
                MH_CreateHook(rpgApplicationOnUpdate, reinterpret_cast<void*>(&HookOnUpdate),
                              reinterpret_cast<LPVOID*>(&g_originalOnUpdate)) == MH_OK;
        }

        const auto& functions = Il2CppBridge::Resolved();
        const bool ready = g_onUpdateReady && functions.gameObjectFind != nullptr &&
                           functions.componentGetComponent != nullptr;
        ipc->HideUidState = ready ? static_cast<int32_t>(IpcHideUidState::Ready) : 0;
        return ready;
    }

    void Shutdown(IpcData* ipc)
    {
        // 退出注入时把恢复请求交给仍启用的 OnUpdate Hook；等不到主线程出帧
        // 就放弃，绝不在 worker 线程调用 Unity 接口。
        if (g_hidAnyObject.load(std::memory_order_relaxed) && g_onUpdateReady)
        {
            g_restoreRequested.store(true, std::memory_order_relaxed);
            const DWORD deadline = GetTickCount() + kRestoreWaitMs;
            while (g_restoreRequested.load(std::memory_order_relaxed) && GetTickCount() < deadline)
            {
                Sleep(10);
            }
            g_restoreRequested.store(false, std::memory_order_relaxed);
        }

        g_hidAnyObject.store(false, std::memory_order_relaxed);
        if (ipc)
        {
            ipc->HideUidState = static_cast<int32_t>(IpcHideUidState::None);
        }
    }

    bool HasFaulted()
    {
        return g_faulted.load(std::memory_order_relaxed);
    }
}
