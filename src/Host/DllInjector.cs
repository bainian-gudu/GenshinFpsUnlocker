using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 将 FpsUnlockerStub.dll 注入到游戏进程。
/// 主路径：CreateRemoteThread + LoadLibraryW（Unicode，中文目录可用）。
/// 备用路径：SetWindowsHookEx（需导出 WndProc）。
/// </summary>
internal static class DllInjector
{
    /// <summary>
    /// 尝试注入。成功返回 true；失败时 error 含中文说明。
    /// </summary>
    public static bool TryInject(Process process, string dllPath, out string error)
    {
        error = string.Empty;
        var fullDll = PathUtil.Normalize(dllPath);
        AppLog.Debug($"TryInject pid={process.Id} dll={fullDll}");

        if (!PathUtil.ExistsFile(fullDll))
        {
            error = $"Stub DLL 不存在: {fullDll}";
            AppLog.Error(error);
            return false;
        }

        // 已加载的 Stub 通过共享内存的 None 请求重新初始化。
        // 重复 LoadLibrary 只会增加引用计数，不会再次执行 DllMain。
        if (IsModuleLoaded(process.Id, fullDll, Path.GetFileName(fullDll)))
            return true;

        // LoadLibraryW 需要目标进程能打开的路径；\\?\ 前缀对远程 LoadLibrary 不友好，尽量去掉
        var injectPath = fullDll;
        if (injectPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            injectPath = injectPath[4..];

        // 超过经典 MAX_PATH 时尝试 8.3 短路径（仍可能含中文卷标，但长度更短）
        if (injectPath.Length >= 260)
        {
            string? shortPath = null;
            try
            {
                shortPath = GetShortPath(injectPath);
            }
            catch (Exception ex) { AppLog.Warn("GetShortPath: " + ex.Message); }

            if (!string.IsNullOrEmpty(shortPath))
            {
                AppLog.Info($"注入使用短路径: {shortPath}");
                injectPath = shortPath;
            }
            else
            {
                // 系统可能关闭了 8.3 短路径生成（fsutil 8dot3name），此时长路径下的
                // LoadLibraryW 很可能失败 —— 把原因写清楚，别让用户对着「注入失败」猜
                AppLog.Warn($"注入路径长度 {injectPath.Length} ≥ 260 且取不到 8.3 短路径" +
                            $"（卷可能已关闭短路径生成），LoadLibraryW 可能失败: {injectPath}");
            }
        }

        if (TryRemoteLoadLibrary(process.Id, injectPath, out error))
        {
            AppLog.Info($"RemoteLoadLibrary 成功 pid={process.Id}");
            return true;
        }

        AppLog.Warn($"RemoteLoadLibrary 失败: {error}");
        var remoteError = error;
        if (TryWindowsHook(process, injectPath, out error))
        {
            AppLog.Info($"WindowsHook 注入成功 pid={process.Id}");
            return true;
        }

        error = $"远程线程注入失败: {remoteError}; Hook 注入失败: {error}";
        AppLog.Error(error);
        return false;
    }

    /// <summary>
    /// 经典远程 LoadLibraryW：在目标进程分配 UTF-16 路径缓冲区并 CreateRemoteThread。
    /// </summary>
    private static bool TryRemoteLoadLibrary(int processId, string dllPath, out string error)
    {
        error = string.Empty;
        var hProcess = Native.OpenProcess(Native.PROCESS_ALL, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            error = $"OpenProcess 失败 ({Marshal.GetLastWin32Error()})，请以管理员身份运行";
            return false;
        }

        IntPtr remoteMemory = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        try
        {
            // UTF-16 LE + 终止符 —— LoadLibraryW 与中文路径的正确编码
            var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remoteMemory = Native.VirtualAllocEx(
                hProcess, IntPtr.Zero, (UIntPtr)bytes.Length,
                Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remoteMemory == IntPtr.Zero)
            {
                error = $"VirtualAllocEx 失败 ({Marshal.GetLastWin32Error()})";
                return false;
            }

            if (!Native.WriteProcessMemory(hProcess, remoteMemory, bytes, (UIntPtr)bytes.Length, out var written)
                || written.ToUInt64() != (ulong)bytes.Length)
            {
                error = $"WriteProcessMemory 失败 ({Marshal.GetLastWin32Error()}) written={written}";
                return false;
            }

            var hKernel = Native.GetModuleHandle("kernel32.dll");
            if (hKernel == IntPtr.Zero)
            {
                error = "GetModuleHandle(kernel32) 失败";
                return false;
            }

            var loadLibrary = Native.GetProcAddress(hKernel, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                error = "GetProcAddress(LoadLibraryW) 失败";
                return false;
            }

            hThread = Native.CreateRemoteThread(hProcess, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remoteMemory, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                error = $"CreateRemoteThread 失败 ({Marshal.GetLastWin32Error()})";
                return false;
            }

            var wait = Native.WaitForSingleObject(hThread, 15000);
            if (wait != Native.WAIT_OBJECT_0)
            {
                error = $"等待远程线程超时 (wait={wait})";
                return false;
            }

            // 线程退出码只有 32 位，而 LoadLibraryW 返回的是 64 位 HMODULE：
            // DLL 基址低 32 位恰好为 0 时（4 GiB 对齐）会误判成「注入失败」，
            // 进而触发退避重试和备用 Hook 注入。退出码只写日志，
            // 成败以「目标进程模块表里有没有这个 DLL」为准。
            if (Native.GetExitCodeThread(hThread, out var exitCode))
            {
                AppLog.Debug($"LoadLibraryW 远程返回值低 32 位=0x{exitCode:X}");
            }

            if (!WaitForModuleInProcess(processId, dllPath, 3000))
            {
                error = $"目标进程模块表中未出现 {Path.GetFileName(dllPath)} — " +
                        $"路径可能无法被游戏进程访问，或位数/权限不匹配: {dllPath}";
                return false;
            }

            return true;
        }
        finally
        {
            if (hThread != IntPtr.Zero) Native.CloseHandle(hThread);
            if (remoteMemory != IntPtr.Zero) Native.VirtualFreeEx(hProcess, remoteMemory, UIntPtr.Zero, Native.MEM_RELEASE);
            Native.CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// 备用：对游戏 UI 线程设 WH_GETMESSAGE Hook，迫使加载本 DLL。
    /// 需要 Stub 导出 WndProc；且游戏窗口类名为 UnityWndClass。
    /// </summary>
    private static bool TryWindowsHook(Process process, string dllPath, out string error)
    {
        error = string.Empty;

        // 用 DONT_RESOLVE_DLL_REFERENCES 映射：拿得到导出函数地址，但系统不会调用
        // Stub 的 DllMain。旧代码用 LoadLibrary，会在 Host 自己进程里把 Stub 跑起来 ——
        // MinHook 初始化、工作线程启动，还会因为 FindGameModule 的回退分支去扫
        // Host 自己的 exe，并把共享内存的 Status 写成 Waiting，污染 Host 的判断。
        var localModule = Native.LoadLibraryEx(dllPath, IntPtr.Zero, Native.DONT_RESOLVE_DLL_REFERENCES);
        if (localModule == IntPtr.Zero)
        {
            error = $"本地 LoadLibraryEx 失败 ({Marshal.GetLastWin32Error()}) path={dllPath}";
            return false;
        }

        var hook = IntPtr.Zero;
        try
        {
            var wndProc = Native.GetProcAddress(localModule, "WndProc");
            if (wndProc == IntPtr.Zero)
            {
                error = "Stub 未导出 WndProc，无法使用 Hook 注入";
                return false;
            }

            var hwnd = FindUnityWindow(process.Id);
            if (hwnd == IntPtr.Zero)
            {
                error = "未找到游戏窗口 (UnityWndClass)";
                return false;
            }

            var threadId = Native.GetWindowThreadProcessId(hwnd, out _);
            if (threadId == 0)
            {
                error = "GetWindowThreadProcessId 失败";
                return false;
            }

            hook = Native.SetWindowsHookEx(Native.WH_GETMESSAGE, wndProc, localModule, threadId);
            if (hook == IntPtr.Zero)
            {
                error = $"SetWindowsHookEx 失败 ({Marshal.GetLastWin32Error()})";
                return false;
            }

            // 触发一条消息（WM_NULL），促使系统把 DLL 映射进目标线程
            Native.PostThreadMessage(threadId, 0, IntPtr.Zero, IntPtr.Zero);

            // 给系统一点时间完成映射，再摘 Hook：早摘会导致目标进程拿不到 DLL 路径。
            // 本方法只在后台监视线程上调用，睡这一下不会卡 UI。
            Thread.Sleep(1500);

            if (!WaitForModuleInProcess(process.Id, dllPath, 3000))
            {
                error = $"Hook 注入后目标进程模块表中未出现 {Path.GetFileName(dllPath)}";
                return false;
            }

            return true;
        }
        finally
        {
            // 摘掉 Hook（否则游戏每条消息都要过一遍我们的空钩子），再释放本地映射。
            // 模块是用 DONT_RESOLVE_DLL_REFERENCES 映射的，DllMain 从未执行，释放是安全的。
            if (hook != IntPtr.Zero)
            {
                try { Native.UnhookWindowsHookEx(hook); } catch { /* ignore */ }
            }
            try { Native.FreeLibrary(localModule); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 轮询目标进程的模块表，确认指定 DLL 已经加载。
    /// 优先按完整路径比对，路径拿不到时退回按文件名比对。
    /// </summary>
    private static bool WaitForModuleInProcess(int processId, string dllPath, int timeoutMs)
    {
        var fileName = Path.GetFileName(dllPath);
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (IsModuleLoaded(processId, dllPath, fileName))
            {
                return true;
            }
            Thread.Sleep(100);
        }

        return false;
    }

    private static bool IsModuleLoaded(int processId, string fullPath, string fileName)
    {
        var snapshot = Native.CreateToolhelp32Snapshot(
            Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, (uint)processId);
        if (snapshot == IntPtr.Zero || snapshot == Native.INVALID_HANDLE_VALUE)
        {
            return false;
        }

        try
        {
            var entry = new Native.MODULEENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>(),
            };

            if (!Native.Module32First(snapshot, ref entry))
            {
                return false;
            }

            do
            {
                if (PathUtil.EqualsPath(entry.szExePath, fullPath))
                {
                    return true;
                }
                if (!string.IsNullOrEmpty(entry.szModule) &&
                    string.Equals(entry.szModule, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            while (Native.Module32Next(snapshot, ref entry));
        }
        catch (Exception ex)
        {
            AppLog.Debug("IsModuleLoaded: " + ex.Message);
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }

        return false;
    }

    /// <summary>枚举顶层窗口，查找指定 PID 的 Unity 主窗口。</summary>
    private static IntPtr FindUnityWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hWnd, _) =>
        {
            Native.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != (uint)processId) return true;

            var sb = new StringBuilder(256);
            Native.GetClassName(hWnd, sb, sb.Capacity);
            if (sb.ToString() == "UnityWndClass")
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);

    /// <summary>获取 8.3 短路径；系统关闭短路径时可能失败。</summary>
    private static string? GetShortPath(string longPath)
    {
        var sb = new StringBuilder(1024);
        var n = GetShortPathName(longPath, sb, sb.Capacity);
        if (n == 0) return null;
        return sb.ToString();
    }
}
