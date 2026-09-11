// =============================================================================
// FpsUnlockerStub.dll — 注入到原神进程内的 FPS 解锁模块
//
// 帧率解锁注入模块：
//   1) 特征码扫描 Get/Set targetFrameRate
//   2) Hook getter，使游戏内画质菜单仍显示合法档位（30/45/60）
//   3) 周期性调用 setter 写入 Host 下发的目标 FPS
//
// 与 Host 通过命名共享内存通信（见 Common/IpcData.h）。
// =============================================================================

#include <Windows.h>
#include <Psapi.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>

#include "MinHook.h"
#include "Scanner.h"
#include "../Common/IpcData.h"

#pragma comment(lib, "Psapi.lib")

namespace
{
    // ---- 特征码（Get/Set targetFrameRate 调用点）----
    // Get：读取当前 targetFrameRate 的 call 点
    constexpr const char* kGetFrameCountPattern =
        "E8 ? ? ? ? 85 C0 7E 0E E8 ? ? ? ? 0F 57 C0 F3 0F 2A C0 EB 08";
    // Set：写入 targetFrameRate 的 call 点
    constexpr const char* kSetFrameCountPattern =
        "E8 ? ? ? ? E8 ? ? ? ? 83 F8 1F 0F 9C 05 ? ? ? ? 48 8B 05";

    using GetFrameCountFn = int (*)();
    using SetFrameCountFn = int (*)(int);

    HMODULE g_selfModule = nullptr;
    HMODULE g_gameModule = nullptr;
    HANDLE g_workerThread = nullptr;
    std::atomic_bool g_running{ false };

    IpcData* g_ipc = nullptr;
    HANDLE g_mapHandle = nullptr;

    GetFrameCountFn g_originalGetFrameCount = nullptr;
    SetFrameCountFn g_setFrameCount = nullptr;

    // 缓存上次写入值：未变化时跳过调用，降低开销；仍每 2s 强制刷新一次（防游戏重置）
    int g_lastAppliedFps = -1;
    int g_lastEnabled = -1;
    DWORD g_lastApplyTick = 0;

    /// <summary>
    /// Hook 后的 GetFrameCount：把真实高帧率“伪装”回菜单合法档位，
    /// 避免画质设置 UI 出现异常选项。
    /// </summary>
    int HookGetFrameCount()
    {
        if (!g_originalGetFrameCount)
        {
            return 60;
        }

        const int ret = g_originalGetFrameCount();
        if (ret >= 60) return 60;
        if (ret >= 45) return 45;
        if (ret >= 30) return 30;
        return ret;
    }

    /// <summary>定位游戏主模块（国服 / 国际服 / 当前进程映像）。</summary>
    HMODULE FindGameModule()
    {
        HMODULE module = GetModuleHandleW(L"YuanShen.exe");
        if (!module)
        {
            module = GetModuleHandleW(L"GenshinImpact.exe");
        }
        // 回退：当前进程映像（兼容重命名启动器）
        if (!module)
        {
            module = GetModuleHandleW(nullptr);
        }
        return module;
    }

    /// <summary>
    /// 打开 Host 创建的共享内存。
    /// 优先 Global\ 命名；失败再试本地命名。校验 Magic。
    /// </summary>
    bool OpenSharedMemory()
    {
        g_mapHandle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kIpcMappingName);
        if (!g_mapHandle)
        {
            g_mapHandle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, L"GenshinFpsUnlocker.Shared.v1");
        }
        if (!g_mapHandle)
        {
            return false;
        }

        g_ipc = static_cast<IpcData*>(MapViewOfFile(g_mapHandle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(IpcData)));
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
    /// 特征扫描并创建 GetFrameCount Hook；解析 SetFrameCount 函数指针。
    /// 幂等：Hook 只创建一次。
    /// </summary>
    bool ResolveFpsFunctions()
    {
        g_gameModule = FindGameModule();
        if (!g_gameModule)
        {
            return false;
        }

        void* getCall = Scanner::ScanModule(g_gameModule, kGetFrameCountPattern);
        void* setCall = Scanner::ScanModule(g_gameModule, kSetFrameCountPattern);
        if (!getCall || !setCall)
        {
            return false;
        }

        void* getFn = Scanner::ResolveRelative(getCall);
        void* setFn = Scanner::ResolveRelative(setCall);
        if (!getFn || !setFn)
        {
            return false;
        }

        if (!g_originalGetFrameCount)
        {
            if (MH_CreateHook(getFn, &HookGetFrameCount, reinterpret_cast<LPVOID*>(&g_originalGetFrameCount)) != MH_OK)
            {
                return false;
            }
        }

        g_setFrameCount = reinterpret_cast<SetFrameCountFn>(setFn);
        return true;
    }

    /// <summary>
    /// 根据共享内存中的 TargetFps / Enabled 调用游戏 setter。
    /// 仅在值变化或距上次写入 ≥2s 时执行，降低性能开销。
    /// </summary>
    void ApplyTargetFps()
    {
        if (!g_ipc || !g_setFrameCount)
        {
            return;
        }

        const int enabled = g_ipc->Enabled;
        if (enabled == 0)
        {
            g_lastEnabled = 0;
            return;
        }

        int fps = g_ipc->TargetFps;
        if (fps < 1) fps = 1;
        if (fps > 540) fps = 540;

        const DWORD now = GetTickCount();
        const bool changed = (fps != g_lastAppliedFps) || (enabled != g_lastEnabled);
        const bool due = (now - g_lastApplyTick) >= 2000;
        if (!changed && !due)
        {
            return;
        }

        g_setFrameCount(fps);
        g_ipc->CurrentFps = fps;
        g_lastAppliedFps = fps;
        g_lastEnabled = enabled;
        g_lastApplyTick = now;
    }

    /// <summary>
    /// 工作线程主循环：
    /// 等共享内存 → 扫描特征 → 启用 Hook → 周期 ApplyTargetFps → 收到 Exiting 退出。
    /// </summary>
    DWORD WINAPI WorkerThread(LPVOID)
    {
        // 等待 Host 共享内存就绪（最多约 15s）
        for (int i = 0; i < 150 && g_running.load(std::memory_order_relaxed); ++i)
        {
            if (OpenSharedMemory())
            {
                break;
            }
            Sleep(100);
        }

        if (!g_ipc)
        {
            return 1;
        }

        g_ipc->Status = IpcStatus::Waiting;

        // 游戏模块可能尚未完全加载，重试扫描
        bool resolved = false;
        for (int i = 0; i < 120 && g_running.load(std::memory_order_relaxed); ++i)
        {
            if (ResolveFpsFunctions())
            {
                resolved = true;
                break;
            }
            Sleep(500);
        }

        if (!resolved)
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = static_cast<int32_t>(GetLastError() ? GetLastError() : 0xE001);
            return 2;
        }

        if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = 0xE002;
            return 3;
        }

        g_ipc->Status = IpcStatus::Ready;
        g_ipc->LastError = 0;

        // 轮询共享内存；仅在需要时写 FPS
        while (g_running.load(std::memory_order_relaxed))
        {
            if (!g_ipc || g_ipc->Status == IpcStatus::Exiting)
            {
                break;
            }

            // 捕获 C++ 异常，避免异常穿透搞崩游戏进程
            try
            {
                ApplyTargetFps();
            }
            catch (...)
            {
                Sleep(1000);
            }

            Sleep(250);
        }

        MH_DisableHook(MH_ALL_HOOKS);
        if (g_ipc)
        {
            g_ipc->Status = IpcStatus::None;
        }
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

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_selfModule = hModule;
        DisableThreadLibraryCalls(hModule);
        g_running.store(true, std::memory_order_relaxed);
        if (MH_Initialize() != MH_OK)
        {
            return FALSE;
        }
        // 在独立线程完成扫描与循环，避免阻塞装载器锁
        g_workerThread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
        break;

    case DLL_PROCESS_DETACH:
        g_running.store(false, std::memory_order_relaxed);
        if (g_workerThread)
        {
            // 不要在装载器锁上阻塞太久
            WaitForSingleObject(g_workerThread, 1500);
            CloseHandle(g_workerThread);
            g_workerThread = nullptr;
        }
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        CloseSharedMemory();
        break;
    }
    return TRUE;
}
