#pragma once

// =============================================================================
// 星穹铁道「解除角色虚化」注入功能。
//
// 目标：RPG.Client.VCameraDOFEffectOverride
//   EnableDOF // Offset: 0x18（dump.cs 实测）
//   OnActiveVCamera RVA 0x1C7FD870
//   Update          RVA 0x1C7FCFC0
//
// 开启时在两个入口调用原函数之后把 EnableDOF 压回 false；关闭时完全放行，
// 不再干预游戏自身的景深逻辑。与原神 Stub 的 AntiBlur 实现无任何耦合。
// =============================================================================

#include <Windows.h>

#include "../Common/IpcData.h"

namespace AntiBlur
{
    /// <summary>
    /// 创建反虚化 Hook。onActiveVCamera / update 允许一个为空；至少一个成功
    /// 才返回 true。幂等：同一进程内重复调用不会重复创建 Hook。
    /// </summary>
    bool Initialize(IpcData* ipc, void* onActiveVCamera, void* update);

    /// <summary>清空状态掩码；已创建的 Hook 由 dllmain 统一 MH_DisableHook。</summary>
    void Shutdown(IpcData* ipc);
}
