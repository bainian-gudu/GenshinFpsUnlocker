namespace GenshinFpsUnlocker.Host;

/// <summary>
/// 托盘自动跟随的纯状态机：只决定「什么时候把展示游戏切到运行中的那款 /
/// 游戏退出后什么时候回退」，不碰 WinForms，便于覆盖「手动查看不打断退出回退」等回归场景。
///
/// 输入来自 <see cref="UnlockService"/>：运行会话号在出现新的游戏进程 PID 时 +1。
/// 自动跟随只改展示游戏，不改用户保存的选择；游戏退出时回退到跟随前的展示游戏，
/// 即使中途用户手动切回运行中的那款也不改变这个回退目标。
/// </summary>
internal sealed class TrayGameFollowState
{
    /// <summary>一次状态推进的结论：Switch 为 false 时其余字段无意义。</summary>
    internal readonly record struct Decision(bool Switch, GameId Game, bool IsFollow, bool IsRestore);

    /// <summary>当前自动跟随的那款游戏（null = 没有跟随）。</summary>
    private GameId? _followedGame;
    /// <summary>跟随之前展示的游戏：游戏退出且当前仍停留在跟随游戏上时回到这里。</summary>
    private GameId _restoreGame = GameId.Genshin;
    /// <summary>已经处理过的运行会话号。</summary>
    private int _seenSession;

    public Decision Update(int runningSession, GameId? runningGame, GameId displayGame)
    {
        // 新的游戏进程会话：自动跟随一次；用户当前展示的就是它则不必切换。
        if (runningSession != _seenSession)
        {
            _seenSession = runningSession;
            if (runningGame is GameId game && displayGame != game)
            {
                _followedGame = game;
                _restoreGame = displayGame;
                return new Decision(true, game, IsFollow: true, IsRestore: false);
            }
            return default;
        }

        // 同一会话内游戏退出：只有还停留在自动跟随的那款上，才回退到启动前的展示游戏。
        if (runningGame is null && _followedGame is GameId exited)
        {
            _followedGame = null;
            if (displayGame == exited && _restoreGame != exited)
                return new Decision(true, _restoreGame, IsFollow: false, IsRestore: true);
        }
        return default;
    }
}
