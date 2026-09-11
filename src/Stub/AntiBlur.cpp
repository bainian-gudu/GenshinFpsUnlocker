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

#include <cstring>

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

    // 在调用者体内向前搜索 call DisplayEffect 的最大窗口（字节）。
    constexpr int kMosaicCallWindow = 0x800;

    // Patch 字节：把 5 字节 call 指令替换为 `mov eax, 0`（吞掉调用）。
    // 与 UnlockerIsland 的 playerDiveMosaicPatchBytes 一致。
    // （0xB8 经显式转换，避免 brace-init 窄化报错）
    const char kMosaicPatchBytes[5] = { (char)0xB8, (char)0x00, (char)0x00, (char)0x00, (char)0x00 };

    using PlayerPerspectiveFn = void (*)(void* rcx, bool display);

    PlayerPerspectiveFn g_originalPlayerPerspective = nullptr;
    Patch* g_mosaicPatch = nullptr;

    bool g_perspectiveReady = false;
    bool g_mosaicReady = false;

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

    /// <summary>
    /// 在马赛克调用者函数体内，定位“最后一个 call DisplayEffect”的指令地址。
    /// 对应 UnlockerIsland Hooks.cpp 的 ScanPlayerDiveMosaic。
    /// </summary>
    void* FindMosaicCallSite(void* caller, void* displayEffect)
    {
        if (!caller || !displayEffect)
        {
            return nullptr;
        }

        void* lastCall = nullptr;
        for (int i = 0; i < kMosaicCallWindow - 4; ++i)
        {
            auto* p = static_cast<unsigned char*>(caller) + i;
            if (*p != 0xE8) // call rel32
            {
                continue;
            }

            if (Scanner::ResolveRelative(p, 1, 5) == displayEffect)
            {
                lastCall = p;
            }
        }
        return lastCall;
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
            if (void* callSite = FindMosaicCallSite(caller, displayEffect))
            {
                // 与原实现一致：仅处理 E8 call 补丁路径。
                // （上游的非 call 分支在其扫描路径下为死代码，offset 必定指向 call。）
                g_mosaicPatch = new Patch(callSite, kMosaicPatchBytes, sizeof(kMosaicPatchBytes));
                g_mosaicReady = true;
            }
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
    }

    void Shutdown(IpcData* ipc)
    {
        if (g_mosaicPatch)
        {
            g_mosaicPatch->SetIsPatched(false);
            delete g_mosaicPatch;
            g_mosaicPatch = nullptr;
        }
        g_mosaicReady = false;

        // 反角色虚化 Hook 由宿主统一 MH_DisableHook(MH_ALL_HOOKS) 处理。
        if (ipc)
        {
            ipc->AntiBlurState = static_cast<int32_t>(IpcAntiBlurState::None);
        }
    }
}
