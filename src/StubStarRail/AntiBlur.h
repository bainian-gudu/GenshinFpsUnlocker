#pragma once

// =============================================================================
// 星穹铁道「反角色虚化」注入功能。
//
// 目标：RPG.Client.BaseShaderPropertyTransition 的相机 Dither 链路。
//   HBPKIAAKMPE（私有汇合入口）       RVA 0x19F1BE00
//   SetDistanceDitherAlphaValue       RVA 0x19F1C0E0（兜底）
//   SetElevationDitherAlphaValue      RVA 0x19F1BD70（兜底）
//
// 开启时只把 DitherSourcePriority.Camera 的透明值改成 1.0；剧情 / 逻辑来源
// 的淡入淡出保持原样。关闭时完全放行。与原神 Stub 的 AntiBlur 实现无耦合。
// =============================================================================

#include <Windows.h>

#include "../Common/IpcData.h"

namespace AntiBlur
{
    /// <summary>
    /// 创建反角色虚化 Hook。优先挂私有相机 Dither 汇合入口；不可用时退回
    /// 距离 / 高度两个公开入口。三个地址都允许为空，但至少一个 Hook 成功
    /// 才返回 true。幂等：同一进程内重复调用不会重复创建 Hook。
    /// </summary>
    bool Initialize(IpcData* ipc, void* ditherSetAlphaValue, void* ditherSetDistanceAlpha,
                    void* ditherSetElevationAlpha);

    /// <summary>清空状态掩码；已创建的 Hook 由 dllmain 统一 MH_DisableHook。</summary>
    void Shutdown(IpcData* ipc);
}
