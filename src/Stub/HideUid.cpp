// =============================================================================
// 隐藏 UID 注入功能实现。特征码与隐藏机制迁移自 DGP Studio 的
// Snap.Hutao.Remastered.UnlockerIsland（MIT 协议，Copyright (c) DGP Studio）：
//   - Constants.cpp              : FindString / FindGameObject / SetActive
//                                  / SetWaterMaskUID / SetupPlayerProfilePage
//                                  / GameUpdate 特征码与两条 UI 路径
//   - function/HidePlayerInfo.cpp: 按路径找对象后 setActive(false)
//   - hook/Hooks.cpp             : SetWaterMaskUID / SetupPlayerProfilePage 钩子
// 详见 HideUid.h 注释。
// =============================================================================

#include "HideUid.h"

#include <atomic>
#include <cstdint>

#include "MinHook.h"
#include "Scanner.h"

namespace
{
    // ---- 特征码（源自 UnlockerIsland Constants.cpp，国服/国际服通用 x64）----

    // 内部方法：C 字符串 → il2cpp string。
    constexpr const char* kFindStringPattern =
        "56 48 83 EC 20 48 89 CE E8 ? ? ? ? 48 89 F1 89 C2 48 83 C4 20 5E E9 ? ? ? ? CC CC CC CC";

    // GameObject.Find：按层级路径找对象。
    constexpr const char* kFindGameObjectPattern =
        "40 53 48 83 EC ? 48 89 4C 24 ? 48 8D 54 24 ? 48 8D 4C 24 ? E8 ? ? ? ? 48 8B 08 "
        "48 85 C9 75 ? 48 8D 48 ? E8 ? ? ? ? 48 8B 4C 24 ? 48 8B D8 48 85 C9 74 ? 48 83 7C 24 ? 00 76";

    // GameObject.set_active：特征码命中的是 call 指令，解析 rel32 后才是函数本体。
    constexpr const char* kSetActivePattern =
        "E8 ?? ?? ?? ?? 41 8B 47 ?? 3D ?? ?? ?? ?? 0F 8D ?? ?? ?? ?? 49 8B 4E ?? 48 85 C9 "
        "0F 84 ?? ?? ?? ?? 48 8B 01 4C 89 FA 45 31 C0 FF 90";

    // MonoUIWaterMask.SetWaterMaskUID：游戏写入水印 UID 文本的时机。
    constexpr const char* kSetWaterMaskUidPattern =
        "56 57 55 53 48 83 EC ?? 44 89 C5 48 89 D7 48 89 CB 80 3D ?? ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? "
        "48 89 D9 E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ??";

    // 资料页打开时机。
    constexpr const char* kSetupPlayerProfilePagePattern =
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC ?? ?? ?? ?? 48 8D AC 24 ?? ?? ?? ?? "
        "0F 29 75 ?? 48 C7 45 ?? ?? ?? ?? ?? 49 89 CC 80 3D ?? ?? ?? ?? ?? 0F 84 ?? ?? ?? ?? "
        "80 3D ?? ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 0F 85 ?? ?? ?? ??";

    // MainThreadDispatcher.Update：每帧执行的主线程调度点，用作兜底与恢复时机。
    constexpr const char* kGameUpdatePattern =
        "55 56 57 53 48 83 EC ?? 48 8D 6C 24 ?? 48 C7 45 ?? ?? ?? ?? ?? 48 8B 41 ?? 48 85 C0 "
        "0F 84 ?? ?? ?? ?? 83 78 ?? ?? 0F 8E ?? ?? ?? ?? 48 89 CF 48 8B 49 ?? 48 85 C9";

    // ---- 要隐藏的 UI 层级路径 ----

    constexpr const char* kWatermarkUidPath = "/BetaWatermarkCanvas(Clone)/Panel/TxtUID";
    constexpr const char* kProfileUidPath =
        "/Canvas/Pages/PlayerProfilePage/GrpProfile/Right/GrpPlayerCard/UID/Layout/PlayerID";

    // 主线程兜底间隔（毫秒）。事件钩子已覆盖正常时机，这里只兜「UI 被重建后
    // 没走事件钩子」的情况，1s 足够且几乎没有开销。
    constexpr DWORD kMainThreadTickIntervalMs = 1000;

    // 退出 / 关闭注入时等待主线程执行恢复的上限。游戏不在出帧（最小化、加载中）
    // 时等不到，超时直接放弃：宁可留着隐藏，也不跨线程调用游戏接口。
    constexpr DWORD kRestoreWaitMs = 250;

    using FindStringFn = void* (*)(const char*);
    using FindGameObjectFn = void* (*)(void*);
    using SetActiveFn = void (*)(void*, bool);
    using SetWaterMaskUidFn = void (*)(void*, void*, bool);
    using ProfilePageFn = void (*)(void*);
    using GameUpdateFn = void (*)(void*);

    FindStringFn g_findString = nullptr;
    FindGameObjectFn g_findGameObject = nullptr;
    SetActiveFn g_setActive = nullptr;

    SetWaterMaskUidFn g_originalSetWaterMaskUid = nullptr;
    ProfilePageFn g_originalProfilePage = nullptr;
    GameUpdateFn g_originalGameUpdate = nullptr;

    bool g_watermarkHookReady = false;
    bool g_profileHookReady = false;
    bool g_gameUpdateHookReady = false;

    // hook 内绑定的 IPC 指针（Initialize 时写入，先于 MH_EnableHook 生效）。
    void* g_boundIpc = nullptr;

    // 以下状态只在游戏主线程读写（Initialize 例外，它在 Hook 启用前赋值）。
    DWORD g_lastTickMs = 0;

    /// <summary>本轮会话是否隐藏过对象（关闭开关 / 退出时据此决定要不要恢复）。</summary>
    std::atomic_bool g_hidAnyObject{ false };

    /// <summary>退出注入时请求主线程恢复显示（worker 线程写、主线程消费）。</summary>
    std::atomic_bool g_restoreRequested{ false };

    /// <summary>
    /// 目标地址是否落在可执行内存。特征码假阳性最常见的形态是命中数据区，
    /// 直接 Hook 或调用这类地址就是让游戏崩。
    /// </summary>
    bool IsExecutableAddress(const void* address)
    {
        if (!address)
        {
            return false;
        }

        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(address, &mbi, sizeof(mbi)))
        {
            return false;
        }
        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0)
        {
            return false;
        }
        return (mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE |
                               PAGE_EXECUTE_WRITECOPY)) != 0;
    }

    /// <summary>
    /// C 字符串 → il2cpp string → 按层级路径找对象。任一环节为空都返回 nullptr。
    /// 必须在游戏主线程调用（il2cpp / Unity 对象接口不可跨线程）。
    /// </summary>
    void* FindObjectByPath(const char* path)
    {
        if (!g_findString || !g_findGameObject || !path)
        {
            return nullptr;
        }

        void* pathString = g_findString(path);
        return pathString ? g_findGameObject(pathString) : nullptr;
    }

    /// <summary>
    /// FindObjectByPath 的 SEH 版本。游戏更新后特征码可能解析出错误函数，
    /// 一次访问违例就会把游戏打崩，因此调用点一律走这里。
    /// </summary>
    void* FindObjectByPathSafe(const char* path)
    {
#if defined(_MSC_VER)
        __try
        {
            return FindObjectByPath(path);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return nullptr;
        }
#else
        return FindObjectByPath(path);
#endif
    }

    /// <summary>
    /// 设置对象激活状态。路径命中不代表对象仍存活（UI 可能刚被销毁），
    /// 因此整段调用套 SEH；失败只影响本次隐藏 / 恢复。
    /// </summary>
    void ForceSetActive(void* object, bool active)
    {
        if (!object || !g_setActive)
        {
            return;
        }

#if defined(_MSC_VER)
        __try
        {
            g_setActive(object, active);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
        }
#else
        g_setActive(object, active);
#endif
    }

    /// <summary>隐藏两条 UID 路径；返回本次是否至少命中一个对象。</summary>
    bool HideUidObjects()
    {
        bool hit = false;

        if (void* watermark = FindObjectByPathSafe(kWatermarkUidPath))
        {
            ForceSetActive(watermark, false);
            hit = true;
        }
        if (void* profile = FindObjectByPathSafe(kProfileUidPath))
        {
            ForceSetActive(profile, false);
            hit = true;
        }
        return hit;
    }

    /// <summary>
    /// 恢复显示。只在本方确实隐藏过对象时调用，恢复的是「我们改过的那个状态」，
    /// 不碰没隐藏过的路径。
    /// </summary>
    void RestoreUidObjects()
    {
        if (void* watermark = FindObjectByPathSafe(kWatermarkUidPath))
        {
            ForceSetActive(watermark, true);
        }
        if (void* profile = FindObjectByPathSafe(kProfileUidPath))
        {
            ForceSetActive(profile, true);
        }
    }

    /// <summary>刷新状态掩码（整字段赋值，避免与 worker 线程读-改-写打架）。</summary>
    void RefreshState(IpcData* ipc)
    {
        if (!ipc)
        {
            return;
        }

        const bool active = ipc->HideUid != 0 && g_hidAnyObject.load(std::memory_order_relaxed);
        ipc->HideUidState = static_cast<int32_t>(IpcHideUidState::Ready) |
                            (active ? static_cast<int32_t>(IpcHideUidState::Active) : 0);
    }

    /// <summary>
    /// 主线程兜底（GameUpdate Hook 内调用，每帧都会进）：
    ///   开关开启：限频补一次隐藏，兜住游戏重建 UI 后没走事件钩子；
    ///   开关关闭 / 收到恢复请求：把隐藏过的对象恢复显示（只做一次）。
    /// </summary>
    void MainThreadTick()
    {
        IpcData* ipc = static_cast<IpcData*>(g_boundIpc);
        if (!ipc)
        {
            return;
        }

        // 恢复请求优先且不受限频约束：注入即将结束时主线程只有极短的窗口。
        if (g_restoreRequested.load(std::memory_order_relaxed))
        {
            RestoreUidObjects();
            g_hidAnyObject.store(false, std::memory_order_relaxed);
            g_restoreRequested.store(false, std::memory_order_relaxed);
            RefreshState(ipc);
            return;
        }

        const DWORD now = GetTickCount();
        if (now - g_lastTickMs < kMainThreadTickIntervalMs)
        {
            return;
        }
        g_lastTickMs = now;

        if (ipc->HideUid != 0)
        {
            if (HideUidObjects())
            {
                g_hidAnyObject.store(true, std::memory_order_relaxed);
            }
        }
        else if (g_hidAnyObject.load(std::memory_order_relaxed))
        {
            RestoreUidObjects();
            g_hidAnyObject.store(false, std::memory_order_relaxed);
        }

        RefreshState(ipc);
    }

    /// <summary>
    /// Hook 水印 UID 写入：原函数先跑（游戏逻辑不变），随后隐藏。
    /// 顺序不能反 —— 游戏在写入过程中会把对象重新激活。
    /// </summary>
    void HookSetWaterMaskUid(void* self, void* text, bool flag)
    {
        if (g_originalSetWaterMaskUid)
        {
            g_originalSetWaterMaskUid(self, text, flag);
        }

        IpcData* ipc = static_cast<IpcData*>(g_boundIpc);
        if (!ipc || ipc->HideUid == 0)
        {
            return;
        }

        if (void* object = FindObjectByPathSafe(kWatermarkUidPath))
        {
            ForceSetActive(object, false);
            g_hidAnyObject.store(true, std::memory_order_relaxed);
        }
        RefreshState(ipc);
    }

    /// <summary>
    /// Hook 资料页打开：原函数先跑（页面正常打开），随后只隐藏身份对象里的 UID。
    /// </summary>
    void HookSetupPlayerProfilePage(void* self)
    {
        if (g_originalProfilePage)
        {
            g_originalProfilePage(self);
        }

        IpcData* ipc = static_cast<IpcData*>(g_boundIpc);
        if (!ipc || ipc->HideUid == 0)
        {
            return;
        }

        if (void* object = FindObjectByPathSafe(kProfileUidPath))
        {
            ForceSetActive(object, false);
            g_hidAnyObject.store(true, std::memory_order_relaxed);
        }
        RefreshState(ipc);
    }

    /// <summary>主线程调度 Hook：原函数先跑，再执行本方兜底逻辑。</summary>
    void HookGameUpdate(void* self)
    {
        if (g_originalGameUpdate)
        {
            g_originalGameUpdate(self);
        }
        MainThreadTick();
    }

    /// <summary>扫描并创建单个直接寻址的 Hook（特征码命中即函数本体）。</summary>
    bool InstallDirectHook(HMODULE gameModule, const char* pattern, void* hook, void** original)
    {
        if (*original)
        {
            return true;
        }

        void* target = Scanner::ScanModule(gameModule, pattern);
        if (!IsExecutableAddress(target))
        {
            return false;
        }
        return MH_CreateHook(target, hook, original) == MH_OK;
    }
}

namespace HideUid
{
    bool Initialize(HMODULE gameModule, IpcData* ipc)
    {
        if (!gameModule || !ipc)
        {
            return false;
        }

        g_boundIpc = ipc;

        // ---- 1) 三个 il2cpp 定位函数：具备隐藏能力的前提 ----
        // 每条特征码只在解析成功前重复扫描，避免每轮重试都全量扫模块。
        if (!g_findString)
        {
            void* fn = Scanner::ScanModule(gameModule, kFindStringPattern);
            if (IsExecutableAddress(fn))
            {
                g_findString = reinterpret_cast<FindStringFn>(fn);
            }
        }
        if (!g_findGameObject)
        {
            void* fn = Scanner::ScanModule(gameModule, kFindGameObjectPattern);
            if (IsExecutableAddress(fn))
            {
                g_findGameObject = reinterpret_cast<FindGameObjectFn>(fn);
            }
        }
        if (!g_setActive)
        {
            // 特征码命中的是 call 指令，rel32 目标才是 GameObject.set_active
            if (void* call = Scanner::ScanModule(gameModule, kSetActivePattern))
            {
                void* fn = Scanner::ResolveRelative(call);
                if (IsExecutableAddress(fn))
                {
                    g_setActive = reinterpret_cast<SetActiveFn>(fn);
                }
            }
        }

        // ---- 2) 事件钩子：水印写入 / 资料页打开 ----
        if (!g_watermarkHookReady)
        {
            g_watermarkHookReady = InstallDirectHook(
                gameModule, kSetWaterMaskUidPattern,
                reinterpret_cast<void*>(&HookSetWaterMaskUid),
                reinterpret_cast<void**>(&g_originalSetWaterMaskUid));
        }
        if (!g_profileHookReady)
        {
            g_profileHookReady = InstallDirectHook(
                gameModule, kSetupPlayerProfilePagePattern,
                reinterpret_cast<void*>(&HookSetupPlayerProfilePage),
                reinterpret_cast<void**>(&g_originalProfilePage));
        }

        // ---- 3) 主线程兜底钩子：UI 重建补隐藏 + 关闭开关后恢复 ----
        if (!g_gameUpdateHookReady)
        {
            g_gameUpdateHookReady = InstallDirectHook(
                gameModule, kGameUpdatePattern,
                reinterpret_cast<void*>(&HookGameUpdate),
                reinterpret_cast<void**>(&g_originalGameUpdate));
        }

        // 就绪 = 三个定位函数齐 + 至少有一个主线程入口（事件钩子或调度钩子）。
        // 三者全缺时没有任何安全的主线程执行点，硬报就绪只会让界面显示「已开启」
        // 却毫无效果。
        const bool hasEntryPoint = g_watermarkHookReady || g_profileHookReady || g_gameUpdateHookReady;
        const bool ready = g_findString != nullptr && g_findGameObject != nullptr &&
                           g_setActive != nullptr && hasEntryPoint;
        ipc->HideUidState = ready ? static_cast<int32_t>(IpcHideUidState::Ready) : 0;
        return ready;
    }

    void Shutdown(IpcData* ipc)
    {
        // 退出注入 / 关闭开关时恢复显示。恢复必须回到游戏主线程，因此这里挂请求位，
        // 由仍启用的 GameUpdate Hook 消费；等不到（游戏不出帧）就放弃，绝不在
        // worker 线程上调用游戏接口。
        if (g_hidAnyObject.load(std::memory_order_relaxed) && g_gameUpdateHookReady)
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
}
