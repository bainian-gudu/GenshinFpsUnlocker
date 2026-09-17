// =============================================================================
// FpsUnlockerStub.dll — 注入到原神进程内的 FPS 解锁模块
//
// 帧率解锁注入模块：
//   1) 特征码扫描 Get/Set targetFrameRate
//   2) Hook getter，使游戏内画质菜单仍显示合法档位（30/45/60）
//   3) 周期性调用 setter 写入 Host 下发的目标 FPS
//   4) Hook setter 记录「游戏自己的档位」，关闭解锁时写回它
//
// 反虚化注入模块（AntiBlur.cpp，迁移自 Snap.Hutao.Remastered.UnlockerIsland）：
//   5) 反角色虚化：Hook 虚化函数，开启时跳过
//   6) 移除水下马赛克：开启时把马赛克调用的 call 原地 Patch 为 mov eax,0
//
// 与 Host 通过命名共享内存通信（见 Common/IpcData.h）。
//
// 健壮性约定：
//   - 特征码解析出来的都是裸函数指针，游戏更新后可能退化成假阳性。
//     因此：① 指针必须落在游戏模块映像内才使用；② 每轮工作都套 SEH 兜底
//     （/EHsc 下 catch(...) 接不住访问违例），一旦踩雷立刻停手卸钩，
//     让游戏回到未注入状态，而不是每 250ms 反复崩。
// =============================================================================

#include <Windows.h>
#include <Psapi.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>

#include "MinHook.h"
#include "Scanner.h"
#include "AntiBlur.h"
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

    HMODULE g_gameModule = nullptr;
    HANDLE g_workerThread = nullptr;
    HMODULE g_hModule = nullptr;
    std::atomic_bool g_running{ false };

    IpcData* g_ipc = nullptr;
    HANDLE g_mapHandle = nullptr;

    GetFrameCountFn g_originalGetFrameCount = nullptr;  // getter 的 trampoline
    SetFrameCountFn g_originalSetFrameCount = nullptr;  // setter 的 trampoline
    SetFrameCountFn g_setFrameCount = nullptr;          // 实际调用的 setter（优先 trampoline）

    /// <summary>游戏自身的帧率档位（由 setter Hook 记录，关闭解锁时写回）。</summary>
    std::atomic_int g_gameOwnFps{ 0 };

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

    /// <summary>
    /// Hook 后的 SetFrameCount：只记录「游戏自己」设置的档位再原样转发。
    /// 本模块写入时走的是 trampoline（g_originalSetFrameCount），不会经过这里，
    /// 因此记录到的值就是游戏/画质菜单的真实意图。
    /// </summary>
    int HookSetFrameCount(int fps)
    {
        if (fps > 0)
        {
            g_gameOwnFps.store(fps, std::memory_order_relaxed);
        }
        return g_originalSetFrameCount ? g_originalSetFrameCount(fps) : 0;
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
    /// 函数指针是否落在模块映像范围内。特征码假阳性最常见的形态就是解析出
    /// 一个指向映像外（或未提交页）的地址，直接调用等于让游戏崩。
    /// </summary>
    bool IsInsideModule(HMODULE module, void* fn)
    {
        if (!module || !fn)
        {
            return false;
        }

        MODULEINFO mi{};
        if (!GetModuleInformation(GetCurrentProcess(), module, &mi, sizeof(mi)))
        {
            return false;
        }

        const uintptr_t base = reinterpret_cast<uintptr_t>(mi.lpBaseOfDll);
        const uintptr_t addr = reinterpret_cast<uintptr_t>(fn);
        return addr >= base && addr < base + mi.SizeOfImage;
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
            g_mapHandle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, L"GenshinFpsUnlocker.Shared.v2");
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
    /// 特征扫描并创建 Get/Set Hook。
    /// 幂等：Hook 只创建一次。解析结果必须落在游戏模块映像内，否则视为失败。
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

        // 假阳性防线：解析结果必须在游戏模块映像内
        if (!IsInsideModule(g_gameModule, getFn) || !IsInsideModule(g_gameModule, setFn))
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

        // setter Hook 用来观察「游戏自己」把帧率设成了多少；失败不致命，
        // 关闭解锁时退回用启用瞬间抓到的初值。
        if (!g_originalSetFrameCount)
        {
            if (MH_CreateHook(setFn, &HookSetFrameCount, reinterpret_cast<LPVOID*>(&g_originalSetFrameCount)) != MH_OK)
            {
                g_originalSetFrameCount = nullptr;
            }
        }

        // 走 trampoline 调用：既避开我们自己的 Hook（不会污染 g_gameOwnFps），也少一层跳转
        g_setFrameCount = g_originalSetFrameCount
                              ? g_originalSetFrameCount
                              : reinterpret_cast<SetFrameCountFn>(setFn);
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
            // 关闭解锁的边沿：把帧率写回游戏自身的档位。
            // 仅仅「停止写入」是不够的 —— 游戏会一直维持我们最后写进去的高帧率，
            // 而界面上显示的是「已暂停」，与实际不符。
            if (g_lastEnabled != 0)
            {
                const int own = g_gameOwnFps.load(std::memory_order_relaxed);
                if (own > 0)
                {
                    g_setFrameCount(own);
                    g_ipc->CurrentFps = own;
                }
                g_lastAppliedFps = -1;  // 重新开启时强制再写一次
                g_lastApplyTick = GetTickCount();
            }
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

    /// <summary>一轮工作：写帧率 + 反虚化开关落地。</summary>
    void TickOnce()
    {
        ApplyTargetFps();
        // 反虚化：按共享内存开关应用/还原水下马赛克字节 Patch
        AntiBlur::Tick(g_ipc);
    }

    /// <summary>
    /// SEH 兜底的一轮工作。返回 false 表示踩到了结构化异常（多半是特征码假阳性
    /// 或游戏改了代码布局），调用方应停止工作并卸钩。
    /// 单独成函数、且函数内不含需要展开的 C++ 对象：MSVC 不允许在同一个函数里
    /// 混用 __try 与 try/catch（C2713），也不允许 __try 与析构对象共存（C2712）。
    /// </summary>
    bool TickOnceSafely()
    {
#if defined(_MSC_VER)
        __try
        {
            TickOnce();
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        TickOnce();
        return true;
#endif
    }

    /// <summary>
    /// 工作线程主循环：
    /// 等共享内存 → 扫描特征 → 启用 Hook → 周期 ApplyTargetFps → 收到 Exiting 暂停会话。
    /// </summary>
    DWORD RunSession()
    {
        g_ipc->Status = IpcStatus::Waiting;

        // 游戏模块可能尚未完全加载，重试扫描。
        // FPS 函数必须解析成功；反虚化特征（游戏版本更新可能失效）只在有限
        // 次数内尝试，解析不到则跳过该功能，不阻塞帧率解锁。
        bool resolved = false;
        bool antiBlurDone = false;
        bool scanAborted = false;
        int antiBlurTries = 0;
        constexpr int kAntiBlurMaxTries = 20;  // 约 10s，独立于 FPS 解析的重试计数
        for (int i = 0; i < 120 && g_running.load(std::memory_order_relaxed); ++i)
        {
            // Host 重启（构造期冲 None）或 ResetForNewInject 写 None 都意味着
            // 「请放弃本轮、重新回到外层等待环」；扫描环最长 60s，不设检出会把
            // 重启恢复信号吞掉一整轮。
            if (!g_ipc || g_ipc->Status == IpcStatus::None || g_ipc->Status == IpcStatus::Exiting)
            {
                scanAborted = true;
                break;
            }
            if (!resolved)
            {
                resolved = ResolveFpsFunctions();
            }
            else if (!antiBlurDone)
            {
                // 计数独立：旧实现与 FPS 解析共用循环变量，游戏加载慢时
                // 反虚化会只剩一次尝试机会，表现为「启动慢就没生效」。
                ++antiBlurTries;
                if (AntiBlur::Initialize(g_gameModule, g_ipc) || antiBlurTries >= kAntiBlurMaxTries)
                {
                    antiBlurDone = true;
                }
            }

            if (resolved && antiBlurDone)
            {
                break;
            }
            Sleep(500);
        }

        if (scanAborted)
        {
            // 会话重建请求打断扫描：撤回本轮可能已创建的 Patch/Hook（与 worker
            // 的异常收尾同一语义），由外层等待环重新进入会话。
            AntiBlur::Shutdown(g_ipc);
            MH_DisableHook(MH_ALL_HOOKS);
            return 0;
        }

        if (!resolved)
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = 0xE001;  // 特征码未命中（游戏版本更新后最常见）
            return 2;
        }

        if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
        {
            g_ipc->Status = IpcStatus::Error;
            g_ipc->LastError = 0xE002;
            return 3;
        }

        // 抓一次游戏当前的帧率档位（走 trampoline，拿到的是未经 Hook 改写的真值），
        // 作为「游戏自己的档位」初值；之后 setter Hook 会持续更新它。
        if (g_originalGetFrameCount)
        {
            const int own = g_originalGetFrameCount();
            if (own > 0)
            {
                g_gameOwnFps.store(own, std::memory_order_relaxed);
            }
        }

        g_ipc->Status = IpcStatus::Ready;
        g_ipc->LastError = 0;

        // 轮询共享内存；仅在需要时写 FPS
        while (g_running.load(std::memory_order_relaxed))
        {
            // None 与 Exiting 同属「结束本会话」信号：Host 重启 / ResetForNewInject
            // 会把 Status 冲回 None 请求重建（Socket 一样的重入协议），只响应
            // Exiting 的话旧会话永远占着位置，新 Host 等 Ready 必超时。
            if (!g_ipc || g_ipc->Status == IpcStatus::Exiting || g_ipc->Status == IpcStatus::None)
            {
                break;
            }

            bool faulted = false;
            try
            {
                // SEH（访问违例等）由 TickOnceSafely 兜住；这里再接一层 C++ 异常
                faulted = !TickOnceSafely();
            }
            catch (...)
            {
                Sleep(1000);
            }

            if (faulted)
            {
                // 踩雷了：停手并卸钩，让游戏回到未注入状态，别每 250ms 反复崩
                g_ipc->Status = IpcStatus::Error;
                g_ipc->LastError = 0xE003;
                break;
            }

            Sleep(250);
        }

        AntiBlur::Shutdown(g_ipc);
        MH_DisableHook(MH_ALL_HOOKS);
        // Error 必须保留给 Host 读取，Exiting 也保留到下次 Host 重置。
        if (g_ipc && g_ipc->Status != IpcStatus::Error && g_ipc->Status != IpcStatus::Exiting)
            g_ipc->Status = IpcStatus::None;
        return 0;
    }

    /// <summary>
    /// 工作线程内的初始化失败出口：经共享内存 Status=Error 上报给 Host
    /// （重试协议本来就靠 IPC 判定，LoadLibrary 返回值只在 DllMain FALSE 时有用），
    /// 然后停线程。仅用于 MH_Initialize / PIN 这两步。
    /// </summary>
    void FailInit(int32_t code)
    {
        g_ipc->Status = IpcStatus::Error;
        g_ipc->LastError = code;
        g_running.store(false, std::memory_order_relaxed);
    }

    DWORD WINAPI WorkerThread(LPVOID)
    {
        // 所有初始化与失败清理都在 DllMain 返回之后进行，原因有二：
        // 1) Loader Lock 语义：DllMain 在 loader lock 下执行，CreateThread 成功后
        //    新线程要等 DllMain 返回、loader 完成 THREAD_ATTACH 分发才开始运行；
        //    若在 DllMain 里等待本线程必然死等超时，超时后 LoadLibrary 按失败
        //    流程卸载模块，本线程却在卸载后才开始执行 —— 悬空代码地址。
        // 2) 重试协议：重复 LoadLibrary 不会重跑 DllMain；注入后的初始化失败
        //    必须经 Status=Error 上报，而不是靠 LoadLibrary 返回值。
        while (g_running.load(std::memory_order_relaxed))
        {
            if (OpenSharedMemory()) break;
            Sleep(500);
        }
        if (!g_ipc) return 1;

        if (MH_Initialize() != MH_OK)
        {
            FailInit(0xE004);  // MinHook 初始化失败
            return 2;
        }

        // 把模块固定到目标进程生命周期：PIN 把引用计数钉死到 0xFFFF，此后
        // FreeLibrary 一律不再生效（LDRP_PIN），卸载只能等进程退出统一回收。
        // 因此必须在一切初始化就绪之后才钉 —— PIN 不可逆，过早钉死会让失败
        // 路径下的模块永远无法卸载。PIN 失败时模块以普通引用计数驻留（无人
        // 释放），惰性但无害，工作线程退出后同样经 Status=Error 可观测。
        HMODULE pinned = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_PIN,
                                reinterpret_cast<LPCWSTR>(g_hModule), &pinned))
        {
            MH_Uninitialize();
            FailInit(0xE005);  // 模块 PIN 失败
            return 3;
        }

        while (g_running.load(std::memory_order_relaxed))
        {
            const DWORD result = RunSession();
            if (result != 0)
            {
                // 扫描或启用失败也要撤回本轮可能已创建的 Patch/Hook，
                // 否则下一次 Host 重试会叠加旧状态。
                AntiBlur::Shutdown(g_ipc);
                MH_DisableHook(MH_ALL_HOOKS);
            }
            // 重复 LoadLibrary 不会重新执行 DllMain。保留线程并等待 Host 的
            // ResetForNewInject 请求（None），使错误重试和 Host 重启能够重新初始化。
            //
            // 已知边界：宿主的映射对象按 CreateOrOpen 语义复用（宿主重启而本进程仍
            // 持 handle 时旧映射存活），故 Stub 不感知「宿主销毁后重建同名映射」——
            // 那会让这里永久读写旧页失联。当前宿主永不销毁重建（IpcSharedMemory
            // 构造期把 Status 冲回 None，恰好就是本环等待的重入信号），若未来宿主
            // 改为销毁重建，Stub 必须先补上映射存活探测再谈兼容。
            while (g_running.load(std::memory_order_relaxed) && g_ipc->Status != IpcStatus::None)
                Sleep(250);
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
        // DllMain 只做「失败即可整段放弃」的最小动作：起工作线程。
        // 连接共享内存 / MH_Initialize / PIN 全部下放给 WorkerThread，因为本
        // 函数在 loader lock 下执行，而新线程要等 DllMain 返回后才开始运行；
        // 这里对 worker 做任何等待都是确定性死锁，等不到再 return FALSE 还会
        // 把模块从尚未起跑的线程脚下卸载掉。初始化失败由 worker 经 IPC
        // Status=Error 上报，Host 本来就按 IPC 判定成败。
        g_workerThread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
        if (!g_workerThread)
        {
            // 线程没起来：此刻还没有任何 MH / 钩子状态要清理，直接失败回滚；
            // 模块随 LoadLibrary 失败被正常卸载，此后再无本方代码在运行。
            g_running.store(false, std::memory_order_relaxed);
            return FALSE;
        }
        break;
    }

    case DLL_PROCESS_DETACH:
    {
        // 正常流程里模块已被 worker 钉住（PIN 是初始化成功路径的最后一步），
        // 只有 worker 初始化失败且未钉住的场景才会真实抵达这里 —— 此时 worker
        // 已经退出，没有需要收尾的 Hook 状态。
        // 进程终止时不要在 loader lock 上等待或访问正在销毁的游戏地址空间。
        g_running.store(false, std::memory_order_relaxed);
        break;
    }
    }
    return TRUE;
}
