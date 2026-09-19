namespace GenshinFpsUnlocker.Host.Tests;

/// <summary>
/// 托盘自动跟随的回归断言：重点是「手动切回原神后再切回正在运行的星铁，
/// 星铁退出时仍回到原神」这条真实反馈过的路径。
/// </summary>
internal static class TrayGameFollowStateTests
{
    public static void Run(Harness h)
    {
        h.Case("星铁启动自动跟随，退出回到原神", () =>
        {
            var state = new TrayGameFollowState();

            var follow = state.Update(1, GameId.StarRail, GameId.Genshin);
            Harness.True(follow.Switch, "星铁新进程应自动跟随");
            Harness.Equal(GameId.StarRail, follow.Game, "跟随目标");
            Harness.True(follow.IsFollow, "应标记为跟随");

            var restore = state.Update(1, null, GameId.StarRail);
            Harness.True(restore.Switch, "星铁退出应回退");
            Harness.Equal(GameId.Genshin, restore.Game, "回退目标");
            Harness.True(restore.IsRestore, "应标记为回退");
        });

        h.Case("手动回原神再切回运行中的星铁，退出仍回原神", () =>
        {
            var state = new TrayGameFollowState();
            var follow = state.Update(1, GameId.StarRail, GameId.Genshin);
            Harness.Equal(GameId.StarRail, follow.Game, "先自动跟随星铁");

            // 手动切回原神：只是查看，不应触发新的自动切换。
            var backToGenshin = state.Update(1, GameId.StarRail, GameId.Genshin);
            Harness.False(backToGenshin.Switch, "手动切回原神不应自动切走");

            // 再手动切回运行中的星铁：仍属于临时查看，不能把回退目标改成星铁。
            var viewStarRail = state.Update(1, GameId.StarRail, GameId.StarRail);
            Harness.False(viewStarRail.Switch, "展示已经是星铁时无需额外切换");

            var restore = state.Update(1, null, GameId.StarRail);
            Harness.True(restore.Switch, "星铁退出应回退");
            Harness.Equal(GameId.Genshin, restore.Game, "应回到自动跟随前的原神");
            Harness.True(restore.IsRestore, "应标记为回退");
        });

        h.Case("手动回原神后直接退出，保持原神不回跳", () =>
        {
            var state = new TrayGameFollowState();
            state.Update(1, GameId.StarRail, GameId.Genshin);

            var exited = state.Update(1, null, GameId.Genshin);
            Harness.False(exited.Switch, "已经停在原神上，退出时不需要再切换");
        });

        h.Case("同一进程会话的状态抖动不会重复跟随", () =>
        {
            var state = new TrayGameFollowState();
            state.Update(1, GameId.StarRail, GameId.Genshin);

            // 监视循环偶尔短暂报告无进程：不能因此再次触发跟随。
            var flicker = state.Update(1, null, GameId.Genshin);
            Harness.False(flicker.Switch, "抖动时已经停在原神上，不应切换");

            var resumed = state.Update(1, GameId.StarRail, GameId.Genshin);
            Harness.False(resumed.Switch, "同一进程会话恢复后不应重复跟随");
        });

        h.Case("用户本来就选着星铁，退出不改变选择", () =>
        {
            var state = new TrayGameFollowState();
            var follow = state.Update(1, GameId.StarRail, GameId.StarRail);
            Harness.False(follow.Switch, "展示已经是星铁，不需要跟随");

            var exited = state.Update(1, null, GameId.StarRail);
            Harness.False(exited.Switch, "没有发生自动跟随时不应改变用户选择");
        });

        h.Case("星铁重启（新进程会话）重新跟随一次", () =>
        {
            var state = new TrayGameFollowState();
            state.Update(1, GameId.StarRail, GameId.Genshin);
            state.Update(1, null, GameId.StarRail);

            var second = state.Update(2, GameId.StarRail, GameId.Genshin);
            Harness.True(second.Switch, "新的星铁进程应再次跟随");
            Harness.Equal(GameId.StarRail, second.Game, "跟随目标");
            Harness.True(second.IsFollow, "应标记为跟随");
        });
    }
}
