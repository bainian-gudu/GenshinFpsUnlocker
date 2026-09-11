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

        // LoadLibraryW 需要目标进程能打开的路径；\\?\ 前缀对远程 LoadLibrary 不友好，尽量去掉
        var injectPath = fullDll;
        if (injectPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            injectPath = injectPath[4..];

        // 超过经典 MAX_PATH 时尝试 8.3 短路径（仍可能含中文卷标，但长度更短）
        if (injectPath.Length >= 260)
        {
            try
            {
                var shortPath = GetShortPath(injectPath);
                if (!string.IsNullOrEmpty(shortPath))
                {
                    AppLog.Info($"注入使用短路径: {shortPath}");
                    injectPath = shortPath;
                }
            }
            catch (Exception ex) { AppLog.Warn("GetShortPath: " + ex.Message); }
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

            // LoadLibraryW 返回模块句柄；0 表示失败（路径不可达/位数不匹配等）
            if (!Native.GetExitCodeThread(hThread, out var exitCode) || exitCode == 0)
            {
                error = $"LoadLibraryW 在目标进程返回 0 (err={Marshal.GetLastWin32Error()}) — 路径可能无法被游戏进程访问: {dllPath}";
                return false;
            }

            AppLog.Debug($"LoadLibraryW 远程模块句柄=0x{exitCode:X}");
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

        var localModule = Native.LoadLibrary(dllPath);
        if (localModule == IntPtr.Zero)
        {
            error = $"本地 LoadLibrary 失败 ({Marshal.GetLastWin32Error()}) path={dllPath}";
            return false;
        }

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

            var hook = Native.SetWindowsHookEx(Native.WH_GETMESSAGE, wndProc, localModule, threadId);
            if (hook == IntPtr.Zero)
            {
                error = $"SetWindowsHookEx 失败 ({Marshal.GetLastWin32Error()})";
                return false;
            }

            // 触发一条消息，促使 Hook 回调在目标线程执行
            Native.PostThreadMessage(threadId, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        finally
        {
            // Hook 存活期间需保持模块加载，此处不 FreeLibrary
        }
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
