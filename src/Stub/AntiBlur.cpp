// =============================================================================
// 反虚化注入功能实现。
// 特征码与 Hook/Patch 机制迁移自 DGP Studio 的
// Snap.Hutao.Remastered.UnlockerIsland（MIT 协议，Copyright (c) DGP Studio）：
//   - Constants.cpp        : PlayerPerspectivePattern / PlayerDiveMosaicPattern
//                            / DisplayEffectPattern
//   - hook/Hooks.cpp       : ScanPlayerDiveMosaic（窗口内最后一个 call 定位法）
//   - function/DisablePlayerPerspective.cpp / DisablePlayerDiveMosaic.cpp
// 详见 AntiBlur.h 注释。
// =============================================================================

#include "AntiBlur.h"

#include <Psapi.h>

#include <algorithm>
#include <cstring>
#include <cstdint>

#include "MinHook.h"
#include "Scanner.h"
#include "Patch.h"

namespace
{
    // ---- 特征码（源自 UnlockerIsland Constants.cpp，国服/国际服通用 x64）----

    // 角色虚化函数本体（扫描结果即函数地址）。
    constexpr const char* kPlayerPerspectivePattern =
        "41 56 56 57 55 53 48 83 EC 20 41 89 D0 48 89 CE 80 3D ?? ?? ?? ?? 00 "
        "0F 85 ?? ?? ?? ?? 48 8B BE ?? ?? ?? ?? 48 85 FF 0F 84";

    // 水下马赛克：调用者函数（实际要 Patch 的 call 在其内部，见下）。
    constexpr const char* kPlayerDiveMosaicPattern =
        "41 57 41 56 56 57 53 48 81 EC ?? ?? ?? ?? 48 89 CE 80 3D ?? ?? ?? ?? ?? "
        "0F 85 ?? ?? ?? ?? 48 8B 86 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 8B 80 ?? ?? ?? ?? 48 83 F8";

    // 水下马赛克：被调用的效果函数（DisplayEffect）。
    constexpr const char* kDisplayEffectPattern =
        "41 57 41 56 41 55 41 54 56 57 55 53 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? "
        "4C 89 CF 48 89 D6 49 89 CC 48 8B AC 24";

    // 新版客户端的水下遮罩处理函数。旧版是调用 DisplayEffect 后 Patch，
    // 新版拆成进入/主处理/退出三个阶段，直接 Hook 返回值更稳定。
    constexpr const char* kUnderwaterMaskPrePattern =
        "41 56 56 57 55 53 48 81 EC F0 04 00 00";
    constexpr const char* kUnderwaterMaskMainPattern =
        "41 57 41 56 56 57 53 48 81 EC D0 04 00 00 48 89 CE";
    constexpr const char* kUnderwaterMaskPostPattern =
        "41 56 56 57 55 53 48 81 EC E0 00 00 00 48 89 CE 80 3D ?? ?? ?? ?? ?? 75 ?? 48 8B 86 ?? ?? ?? ?? 48 85 C0";
    constexpr const char* kUnderwaterMaskClearPattern =
        "56 57 48 83 EC 28 48 89 CE 80 3D ?? ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? 80 3D ?? ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? 48 8D BE ?? ?? ?? ?? 80 3D";

    // 在调用者体内向前搜索 call DisplayEffect 的最大窗口（字节）。
    constexpr int kMosaicCallWindow = 0x800;

    // Patch 字节：把 5 字节 call 指令替换为 `mov eax, 0`（吞掉调用）。
    // 与 UnlockerIsland 的 playerDiveMosaicPatchBytes 一致。
    // （0xB8 经显式转换，避免 brace-init 窄化报错）
    const char kMosaicPatchBytes[5] = { (char)0xB8, (char)0x00, (char)0x00, (char)0x00, (char)0x00 };

    using PlayerPerspectiveFn = void (*)(void* rcx, bool display);
    using UnderwaterMaskFn = int64_t (*)(void* self, double deltaTime);
    using ClearMaskFn = void (*)(void* self);

    PlayerPerspectiveFn g_originalPlayerPerspective = nullptr;
    Patch* g_mosaicPatch = nullptr;
    UnderwaterMaskFn g_originalMaskPre = nullptr;
    UnderwaterMaskFn g_originalMaskMain = nullptr;
    UnderwaterMaskFn g_originalMaskPost = nullptr;
    ClearMaskFn g_clearMask = nullptr;

    bool g_perspectiveReady = false;
    bool g_mosaicReady = false;
    bool g_maskHookReady = false;

    // hook 内绑定的 IPC 指针（Initialize 时写入，先于 MH_EnableHook 生效）。
    void* g_boundIpc = nullptr;

    /// <summary>
    /// Hook 角色虚化函数：开启反虚化时直接返回，不执行虚化效果；
    /// 关闭时转调原函数，游戏行为与未注入完全一致。
    /// </summary>
    void HookPlayerPerspective(void* rcx, bool display)
    {
        IpcData* ipc = static_cast<IpcData*>(g_boundIpc);
        if (ipc && ipc->AntiBlurPerspective != 0)
        {
            return;
        }

        if (g_originalPlayerPerspective)
        {
            g_originalPlayerPerspective(rcx, display);
        }
    }

    int64_t HookUnderwaterMask(UnderwaterMaskFn original, void* self, double deltaTime)
    {
        IpcData* ipc = static_cast<IpcData*>(g_boundIpc);
        if (!ipc || ipc->AntiBlurDiveMosaic == 0)
        {
            return original ? original(self, deltaTime) : 0;
        }

        // 清理旧遮罩对象，避免仅跳过计算后残留上一帧马赛克。
        if (g_clearMask && self)
        {
#if defined(_MSC_VER)
            __try
            {
                g_clearMask(self);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                // 清理函数随版本变化时可能失效；跳过清理仍可阻止本帧新遮罩。
            }
#else
            g_clearMask(self);
#endif
        }
        return 0;
    }

    int64_t HookMaskPre(void* self, double deltaTime)
    {
        return HookUnderwaterMask(g_originalMaskPre, self, deltaTime);
    }

    int64_t HookMaskMain(void* self, double deltaTime)
    {
        return HookUnderwaterMask(g_originalMaskMain, self, deltaTime);
    }

    int64_t HookMaskPost(void* self, double deltaTime)
    {
        return HookUnderwaterMask(g_originalMaskPost, self, deltaTime);
    }

    void InstallMaskHook(HMODULE gameModule, const char* pattern, void* hook,
                         UnderwaterMaskFn* original)
    {
        void* target = Scanner::ScanModule(gameModule, pattern);
        if (!target || *original)
        {
            return;
        }
        if (MH_CreateHook(target, hook, reinterpret_cast<LPVOID*>(original)) == MH_OK)
        {
            g_maskHookReady = true;
        }
    }

    /// <summary>
    /// 在马赛克调用者函数体内，定位“最后一个 call DisplayEffect”的指令地址。
    /// 对应 UnlockerIsland Hooks.cpp 的 ScanPlayerDiveMosaic。
    ///
    /// 读窗口必须夹在「模块映像 ∩ 当前内存区域」内：caller 只是特征码命中的地址，
    /// 没有任何保证它离映像末尾有 0x800 字节，裸读窗口尾部就是越界访问 → AV。
    /// 边界只在入口算一次（1 次 GetModuleInformation + 1 次 VirtualQuery），
    /// 循环内直接做 rel32 算术，每个候选零系统调用。
    /// </summary>
    void* FindMosaicCallSite(void* caller, void* displayEffect, uintptr_t moduleEnd)
    {
        if (!caller || !displayEffect)
        {
            return nullptr;
        }

        const uintptr_t start = reinterpret_cast<uintptr_t>(caller);
        // 至少要能读出一条完整 call：opcode + rel32
        if (moduleEnd < start + 5)
        {
            return nullptr;
        }

        // 窗口上界再被 caller 所在内存区域夹一次（区域可能比模块映像短）
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<LPCVOID>(start), &mbi, sizeof(mbi)))
        {
            return nullptr;
        }
        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0)
        {
            return nullptr;
        }

        const uintptr_t regionEnd = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
        const uintptr_t limit = std::min(moduleEnd, regionEnd);
        if (limit < start + 5)
        {
            return nullptr;
        }

        // 最后一个可以安全读取 rel32 的起点：p+1..p+4 必须落在 limit 之内
        const uintptr_t lastP = std::min(start + static_cast<uintptr_t>(kMosaicCallWindow) - 5, limit - 5);

        void* lastCall = nullptr;
        for (uintptr_t addr = start; addr <= lastP; ++addr)
        {
            auto* p = reinterpret_cast<unsigned char*>(addr);
            if (*p != 0xE8) // call rel32
            {
                continue;
            }

            const auto rel = *reinterpret_cast<const int32_t*>(p + 1);
            if (static_cast<void*>(p + 5 + rel) == displayEffect)
            {
                lastCall = p;
            }
        }
        return lastCall;
    }

    /// <summary>模块映像的结束地址（不含）；取不到信息时返回 0。</summary>
    uintptr_t ModuleEnd(HMODULE module)
    {
        MODULEINFO mi{};
        if (!GetModuleInformation(GetCurrentProcess(), module, &mi, sizeof(mi)))
        {
            return 0;
        }
        return reinterpret_cast<uintptr_t>(mi.lpBaseOfDll) + mi.SizeOfImage;
    }
}

namespace AntiBlur
{
    bool Initialize(HMODULE gameModule, IpcData* ipc)
    {
        if (!gameModule || !ipc)
        {
            return false;
        }

        g_boundIpc = ipc;

        // ---- 1) 反角色虚化：直接 Hook 虚化函数本体 ----
        if (!g_perspectiveReady)
        {
            if (void* fn = Scanner::ScanModule(gameModule, kPlayerPerspectivePattern))
            {
                if (MH_CreateHook(fn, &HookPlayerPerspective,
                        reinterpret_cast<LPVOID*>(&g_originalPlayerPerspective)) == MH_OK)
                {
                    g_perspectiveReady = true;
                }
            }
        }

        // ---- 2) 移除水下马赛克：定位 call 点并准备字节 Patch ----
        if (!g_mosaicReady)
        {
            void* caller = Scanner::ScanModule(gameModule, kPlayerDiveMosaicPattern);
            void* displayEffect = Scanner::ScanModule(gameModule, kDisplayEffectPattern);
            if (void* callSite = FindMosaicCallSite(caller, displayEffect, ModuleEnd(gameModule)))
            {
                // 与原实现一致：仅处理 E8 call 补丁路径。
                // （上游的非 call 分支在其扫描路径下为死代码，offset 必定指向 call。）
                auto* patch = new Patch(callSite, kMosaicPatchBytes, sizeof(kMosaicPatchBytes));
                if (patch->IsValid())
                {
                    g_mosaicPatch = patch;
                    g_mosaicReady = true;
                }
                else
                {
                    // 页保护放不开（被保护页 / 权限问题）：放弃这个功能，别留半死对象
                    delete patch;
                }
            }
        }

        // 新版客户端兼容路径：三段水下遮罩函数直接 Hook。至少成功一个即视为
        // 已就绪；Hook 内按共享内存开关决定跳过或调用原函数。
        if (!g_maskHookReady)
        {
            if (void* clear = Scanner::ScanModule(gameModule, kUnderwaterMaskClearPattern))
            {
                g_clearMask = reinterpret_cast<ClearMaskFn>(clear);
            }
            InstallMaskHook(gameModule, kUnderwaterMaskPrePattern,
                            reinterpret_cast<void*>(&HookMaskPre), &g_originalMaskPre);
            InstallMaskHook(gameModule, kUnderwaterMaskMainPattern,
                            reinterpret_cast<void*>(&HookMaskMain), &g_originalMaskMain);
            InstallMaskHook(gameModule, kUnderwaterMaskPostPattern,
                            reinterpret_cast<void*>(&HookMaskPost), &g_originalMaskPost);
            if (g_maskHookReady)
            {
                g_mosaicReady = true;
            }
        }

        // 兼容重试：Hook 已创建但本轮调用点 Patch 未找到时，仍保留 Hook 路径。
        if (g_maskHookReady)
        {
            g_mosaicReady = true;
        }

        // 刷新状态掩码
        ipc->AntiBlurState =
            (g_perspectiveReady ? static_cast<int32_t>(IpcAntiBlurState::PerspectiveReady) : 0) |
            (g_mosaicReady ? static_cast<int32_t>(IpcAntiBlurState::DiveMosaicReady) : 0);

        return g_perspectiveReady && g_mosaicReady;
    }

    void Tick(IpcData* ipc)
    {
        if (!ipc)
        {
            return;
        }

        if (g_mosaicReady && g_mosaicPatch)
        {
            g_mosaicPatch->SetIsPatched(ipc->AntiBlurDiveMosaic != 0);

            if (g_mosaicPatch->IsPatched())
            {
                ipc->AntiBlurState |= static_cast<int32_t>(IpcAntiBlurState::DiveMosaicPatched);
            }
            else
            {
                ipc->AntiBlurState &= ~static_cast<int32_t>(IpcAntiBlurState::DiveMosaicPatched);
            }
        }

        if (g_maskHookReady)
        {
            if (ipc->AntiBlurDiveMosaic != 0)
            {
                ipc->AntiBlurState |= static_cast<int32_t>(IpcAntiBlurState::DiveMosaicPatched);
            }
            else
            {
                ipc->AntiBlurState &= ~static_cast<int32_t>(IpcAntiBlurState::DiveMosaicPatched);
            }
        }
    }

    void Shutdown(IpcData* ipc)
    {
        if (g_mosaicPatch)
        {
            g_mosaicPatch->SetIsPatched(false);
            delete g_mosaicPatch;
            g_mosaicPatch = nullptr;
        }
        // 已创建的 MinHook 会由宿主统一禁用，保留就绪标记以支持同一进程内重试。
        g_mosaicReady = g_maskHookReady;

        // 反角色虚化 Hook 由宿主统一 MH_DisableHook(MH_ALL_HOOKS) 处理。
        if (ipc)
        {
            ipc->AntiBlurState = static_cast<int32_t>(IpcAntiBlurState::None);
        }
    }
}
