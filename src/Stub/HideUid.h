#pragma once

// =============================================================================
// 隐藏 UID 注入功能（实现思路迁移自 DGP Studio 的
// Snap.Hutao.Remastered.UnlockerIsland，MIT 协议，Copyright (c) DGP Studio）：
//
//   - function/HidePlayerInfo.cpp : 用 FindString / FindGameObject / SetActive
//                                   三个 il2cpp 函数按 UI 层级路径隐藏对象
//   - hook/Hooks.cpp             : SetWaterMaskUID / SetupPlayerProfilePage
//                                   两个事件钩子（原函数先跑，再隐藏）
//   - Constants.cpp              : 各特征码与 UI 路径
//
// 隐藏对象：
//   1) 水印 UID：/BetaWatermarkCanvas(Clone)/Panel/TxtUID
//   2) 资料页 UID：/Canvas/Pages/PlayerProfilePage/GrpProfile/Right/GrpPlayerCard
//                  /UID/Layout/PlayerID
//
// 与原实现的差异：
//   - 不依赖硬编码偏移表，全部走特征码扫描（与本项目 FPS 解锁 / 反虚化一致）。
//   - 额外 Hook MainThreadDispatcher.Update 作为主线程兜底：游戏重建 UI 后补一次
//     隐藏，关闭开关时在同一主线程把对象恢复显示。
//   - 所有 il2cpp 调用都发生在游戏主线程（Unity 对象接口不可跨线程调用），
//     因此本模块不提供 worker 线程 Tick。
// =============================================================================

#include <Windows.h>
#include "../Common/IpcData.h"

namespace HideUid
{
    /// <summary>
    /// 扫描特征码并创建 Hook。幂等：已就绪后直接返回 true。
    /// 解析失败返回 false（游戏模块可能尚未加载完，可重试），不影响 FPS 解锁；
    /// 解析结果写入 ipc->HideUidState 状态掩码。
    /// </summary>
    bool Initialize(HMODULE gameModule, IpcData* ipc);

    /// <summary>
    /// 卸载：清空状态掩码。已创建的 Hook 由宿主统一 MH_DisableHook(MH_ALL_HOOKS)。
    /// </summary>
    void Shutdown(IpcData* ipc);
}
