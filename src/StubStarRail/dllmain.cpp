// =============================================================================
// StarRailStub.dll — 注入到崩坏：星穹铁道进程内的画面效果模块。
//
// 职责边界（与原神 FpsUnlockerStub.dll 完全独立）：
//   1) 反角色虚化：Hook VCameraDOFEffectOverride.OnActiveVCamera / Update，
//      在开启时把 EnableDOF（Offset 0x18）压回 false；
//   2) 隐藏 UID 水印：Hook RPGApplication.OnUpdate 作为主线程入口，
//      按两条层级路径把 UnityEngine.UI.Graphic.m_Color.a 写 0。
//
// 本模块不处理帧率：星铁解锁帧率由 Host 的 StarRailFpsRegistry.cs 直接写注册表。
// 本模块不修改游戏目录、不碰存档与网络。
//
// 与 Host 通过命名共享内存通信（见 src/Common/IpcData.h）。
//
// 健壮性约定：
//   - 所有 RVA / 特征码定位结果必须落在 GameAssembly.dll 映像内且可执行；
//   - 首轮定位全部成功才置 Ready，任何一步失败置 Error + 错误码，绝不半开；
//   - 所有 il2cpp / Unity 对象调用都在游戏主线程；worker 线程只做定位、
//     状态监视与 Hook 生命周期管理。
// =============================================================================

#include <Windows.h>
#include <Psapi.h>

#include <atomic>
#include <cstdint>

#include "MinHook.h"
#include "AntiBlur.h"
#include "HideUid.h"
#include "Il2CppBridge.h"
#include "../Common/IpcData.h"

#pragma comment(lib, "Psapi.lib")

namespace
{
    // ---- 星铁专用错误码（与 Host 的 0xE001 原神错误码区分）----
    constexpr int32_t kErrGameAssemblyMissing = 0xE101;
    constexpr int32_t kErrFindMissing = 0xE102;
    constexpr int32_t kErrGetComponentMissing = 0xE103;
    constexpr int32_t kErrMainThreadEntryMissing = 0xE104;
    constexpr int32_t kErrAntiBlurEntryMissing = 0xE105;
    constexpr int32_t kErrAntiBlurHook = 0xE106;
    constexpr int32_t kErrHideUidHook = 0xE107;
    constexpr int32_t kErrEnableHooks = 0xE108;
    constexpr int32_t kErrTickFaulted = 0xE109;
    constexpr int32_t kErrMinHookInit = 0xE10A;
    constexpr int32_t kErrPin = 0xE10B;

    HMODULE g_gameAssembly = nullptr;
    HMODULE g_hModule = nullptr;
    HANDLE g_workerThread = nullptr;
    std::atomic_bool g_running{ false };

    IpcData* g_ipc = nullptr;
    HANDLE g_mapHandle = nullptr;

    /// <summary>把 ResolveStatus 映射为宿主可显示的错误码。</summary>
    int32_t ErrorCodeFor(Il2CppBridge::ResolveStatus status)
    {
        switch (status)
        {
        case Il2CppBridge::ResolveStatus::GameAssemblyMissing:
            return kErrGameAssemblyMissing;
        case Il2CppBridge::ResolveStatus::FindMissing:
            return kErrFindMissing;
        case Il2CppBridge::ResolveStatus::GetComponentMissing:
            return kErrGetComponentMissing;
        case Il2CppBridge::ResolveStatus::MainThreadEntryMissing:
            return kErrMainThreadEntryMissing;
        case Il2CppBridge::ResolveStatus::AntiBlurEntryMissing:
            return kErrAntiBlurEntryMissing;
        default:
            return kErrGameAssemblyMissing;
        }
    }

    /// <summary>定位 GameAssembly.dll（星铁 IL2CPP 运行时模块）。</summary>
    HMODULE FindGameAssembly()
    {
        HMODULE module = GetModuleHandleW(L"GameAssembly.dll");
        return module;
    }

    /// <summary>
    /// 打开 Host 创建的共享内存。优先 Global\ 命名；失败再试本地命名。校验 Magic。
    /// </summary>
    bool OpenSharedMemory()
    {
        g_mapHandle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kIpcMappingName);
        if (!g_mapHandle)
        {
            g_mapHandle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, L"GenshinFpsUnlocker.Shared.v3");
        }
        if (!g_mapHandle)
        {
            return false;
        }

        g_ipc = static_cast<IpcData*>(
            MapViewOfFile(g_mapHandle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(IpcData)));
        if (!g_ipc)
        {
            CloseHandle(g_mapHandle);
            g_mapHandle = nullptr;
            return false;
        }

        if (g_ipc->Magic != kIpcMagic)
        {
            UnmapViewOfFile(g_ipc);
            g_ipc = nullptr;
            CloseHandle(g_mapHandle);
            g_mapHandle = nullptr;
            return false;
        }

        return true;
    }

    void CloseSharedMemory()
    {
        if (g_ipc)
        {
            UnmapViewOfFile(g_ipc);
            g_ipc = nullptr;
        }
        if (g_mapHandle)
        {
            CloseHandle(g_mapHandle);
            g_mapHandle = nullptr;
        }
    }

    /// <summary>
    /// 一轮会话：定位 → 创建 Hook → 启用 → 监视 Host 状态。
    /// 返回 0 表示正常结束（None / Exiting），非 0 表示错误退出。
    /// </summary>
    DWORD RunSession()
    {
        if (!g_ipc)
        {
            return 1;
        }

        g_ipc->Status = IpcStatus::Waiting;
        g_ipc->LastError = 0;

        Il2CppBridge::Functions functions{};
        Il2CppBridge::ResolveStatus lastStatus = Il2CppBridge::ResolveStatus::GameAssemblyMissing;
        bool resolved = false;
        bool aborted = false;

        // GameAssembly.dll 可能尚未完成加载；最多重试 60s。
        for (int attempt = 0; attempt < 120 && g_running.load(std::memory_order_relaxed); ++attempt)
        {
            if (!g_ipc || g_ipc->Status == IpcStatus::None || g_ipc->Status == IpcStatus::Exiting)
            {
                aborted = true;
                break;
            }

            if (!g_gameAssembly)
            {
                g_gameAssembly = FindGameAssembly();
            }

            lastStatus = Il2CppBridge::Resolve(g_gameAssembly, functions);
            if (lastStatus == Il2CppBridge::ResolveStatus::Ok)
            {
                resolved = true;
                break;
            }
            Sleep(500);
        }

        if (aborted)
        {
            MH_DisableHook(MH_ALL_HOOKS);
            return 0;
        }

        if (!resolved)
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = ErrorCodeFor(lastStatus);
            return 2;
        }

        // 两个功能各自创建 Hook；任一失败即整模块 Error，避免「界面显示已开启
        // 但实际只生效一半」。
        if (!AntiBlur::Initialize(g_ipc, functions.vCameraDofOnActive, functions.vCameraDofUpdate))
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = kErrAntiBlurHook;
            return 3;
        }

        if (!HideUid::Initialize(g_ipc, functions.rpgApplicationOnUpdate))
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = kErrHideUidHook;
            return 4;
        }

        if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = kErrEnableHooks;
            return 5;
        }

        g_ipc->Status = IpcStatus::Ready;
        g_ipc->LastError = 0;

        // worker 线程不调用 il2cpp / Unity 接口；只监视 Host 会话状态与主线程
        // tick 是否踩到异常。
        while (g_running.load(std::memory_order_relaxed))
        {
            if (!g_ipc || g_ipc->Status == IpcStatus::None || g_ipc->Status == IpcStatus::Exiting)
            {
                break;
            }
            if (HideUid::HasFaulted())
            {
                g_ipc->Status = IpcStatus::Error;
                g_ipc->LastError = kErrTickFaulted;
                break;
            }
            Sleep(250);
        }

        // 先恢复 UID 原 alpha，再统一卸钩。
        HideUid::Shutdown(g_ipc);
        AntiBlur::Shutdown(g_ipc);
        MH_DisableHook(MH_ALL_HOOKS);

        if (g_ipc && g_ipc->Status != IpcStatus::Error && g_ipc->Status != IpcStatus::Exiting)
        {
            g_ipc->Status = IpcStatus::None;
        }
        return 0;
    }

    /// <summary>
    /// 工作线程内的初始化失败出口：经共享内存 Status=Error 上报给 Host。
    /// 仅用于 MH_Initialize / PIN 这两步。
    /// </summary>
    void FailInit(int32_t code)
    {
        if (g_ipc)
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = code;
        }
        g_running.store(false, std::memory_order_relaxed);
    }

    DWORD WINAPI WorkerThread(LPVOID)
    {
        // 初始化与失败清理都在 DllMain 返回之后进行，避免 loader lock 死锁。
        while (g_running.load(std::memory_order_relaxed))
        {
            if (OpenSharedMemory())
            {
                break;
            }
            Sleep(500);
        }
        if (!g_ipc)
        {
            return 1;
        }

        if (MH_Initialize() != MH_OK)
        {
            FailInit(kErrMinHookInit);
            return 2;
        }

        // PIN 不可逆，必须在初始化就绪后才钉；PIN 失败时模块仍以普通引用计数
        // 驻留，工作线程退出后同样经 Status=Error 可观测。
        HMODULE pinned = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_PIN,
                                reinterpret_cast<LPCWSTR>(g_hModule), &pinned))
        {
            MH_Uninitialize();
            FailInit(kErrPin);
            return 3;
        }

        while (g_running.load(std::memory_order_relaxed))
        {
            const DWORD result = RunSession();
            if (result != 0)
            {
                // 失败路径统一撤回本轮可能已创建的 Hook。
                HideUid::Shutdown(g_ipc);
                AntiBlur::Shutdown(g_ipc);
                MH_DisableHook(MH_ALL_HOOKS);
            }

            // 等待 Host 的 ResetForNewInject（Status=None）后重新进入会话。
            while (g_running.load(std::memory_order_relaxed) && g_ipc->Status != IpcStatus::None)
            {
                Sleep(250);
            }
        }

        CloseSharedMemory();
        return 0;
    }
}

/// <summary>
/// 导出给 SetWindowsHookEx 备用注入路径的空钩子过程。
/// 必须存在，否则 Host 的 Hook 注入会失败。
/// </summary>
extern "C" __declspec(dllexport) LRESULT CALLBACK WndProc(int code, WPARAM wParam, LPARAM lParam)
{
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
    {
        DisableThreadLibraryCalls(hModule);
        g_hModule = hModule;
        g_running.store(true, std::memory_order_relaxed);
        // DllMain 只做最小动作：起工作线程。连接共享内存 / MH_Initialize / PIN
        // 全部下放给 WorkerThread，避免在 loader lock 下等待。
        g_workerThread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
        if (!g_workerThread)
        {
            g_running.store(false, std::memory_order_relaxed);
            return FALSE;
        }
        break;
    }

    case DLL_PROCESS_DETACH:
    {
        g_running.store(false, std::memory_order_relaxed);
        break;
    }
    }
    return TRUE;
}
