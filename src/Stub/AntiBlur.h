#pragma once

// =============================================================================
// 反虚化注入功能（迁移自 DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland，
// MIT 协议，Copyright (c) DGP Studio）：
//
//   1) 反角色虚化（DisablePlayerPerspective）
//      Hook 角色虚化函数：开启时直接跳过，镜头拉近时角色不再透明化。
//
//   2) 移除水下马赛克（DisablePlayerDiveMosaic）
//      定位“调用 DisplayEffect 的 call 指令”，开启时把该 call 原地
//      Patch 为 `mov eax,0`（5 字节），吞掉马赛克效果调用；关闭时还原。
//
// 与原实现的差异：
//   - 不依赖每个游戏版本的硬编码偏移表，全部走特征码扫描（与本项目 FPS
//     解锁模块一致的自适配方式）。
//   - 未移植“千星奇域（UGC 玩法）抵抗锁”（上游依赖 il2cpp 字符串钩子链
//     检测玩法状态）。请用户在联机/UGC 玩法中保持关闭，风险自负。
// =============================================================================

#include <Windows.h>
#include "../Common/IpcData.h"

namespace AntiBlur
{
    /// <summary>
    /// 扫描特征码并创建 Hook / Patch。幂等：已就绪后直接返回 true。
    /// 解析失败返回 false（可在游戏模块加载完成后重试），不影响 FPS 解锁。
    /// 解析结果写入 ipc->AntiBlurState 状态掩码。
    /// </summary>
    bool Initialize(HMODULE gameModule, IpcData* ipc);

    /// <summary>
    /// 每轮询周期调用：按共享内存开关应用/还原马赛克 Patch，
    /// 并刷新 ipc->AntiBlurState 状态掩码。线程安全（仅 worker 线程调用）。
    /// </summary>
    void Tick(IpcData* ipc);

    /// <summary>卸载：还原 Patch（Hook 由宿主统一 MH_DisableHook）。</summary>
    void Shutdown(IpcData* ipc);
}
