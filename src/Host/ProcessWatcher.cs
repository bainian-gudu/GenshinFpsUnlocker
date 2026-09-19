using System.Diagnostics;

namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 游戏进程查找辅助：进程名来自 <see cref="GameDescriptor"/>，两款游戏共用这一套查找。
/// 原神国服 YuanShen / 国际服 GenshinImpact；星穹铁道 StarRail（国服与国际服同名）。
/// </summary>
internal static class GameProcess
{
    /// <summary>
    /// 查找正在运行的指定游戏进程。
    /// 避免昂贵的 MainModule 访问；若存在多个实例，只保留第一个并释放其余句柄。
    /// 调用方负责 Dispose 返回值。
    /// </summary>
    public static Process? Find(GameDescriptor game)
    {
        foreach (var name in game.ProcessNames)
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

    /// <summary>该游戏当前是否有进程在运行（不保留句柄）。</summary>
    public static bool IsRunning(GameDescriptor game)
    {
        using var process = Find(game);
        return process is not null;
    }

}
