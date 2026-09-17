using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 游戏进程查找辅助。
/// 国服进程名 YuanShen，国际服 GenshinImpact。
/// </summary>
internal static class GameProcess
{
    /// <summary>受支持的游戏进程名（不含 .exe）。</summary>
    public static readonly string[] ProcessNames = ["YuanShen", "GenshinImpact"];

    /// <summary>
    /// 查找正在运行的游戏进程。
    /// 避免昂贵的 MainModule 访问；若存在多个实例，只保留第一个并释放其余句柄。
    /// 调用方负责 Dispose 返回值。
    /// </summary>
    public static Process? Find()
    {
        foreach (var name in ProcessNames)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }

            if (list.Length == 0) continue;

            Process? keep = list[0];
            for (var i = 1; i < list.Length; i++)
            {
                try { list[i].Dispose(); } catch { /* ignore */ }
            }

            return keep;
        }

        return null;
    }

}
