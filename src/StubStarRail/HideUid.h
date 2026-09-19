#pragma once

// =============================================================================
// 星穹铁道「隐藏 UID 水印」注入功能。
//
// 两条目标路径（按 2 条处理，与原神侧两条路径互不相干）：
//   1) /UIRoot/AboveDialog/BetaHintDialog(Clone)/Contents/VersionText
//   2) /UIRoot/Page/MobilePhoneMainPage(Clone)/Content/Content/LeftPlane/Tittle/UID/NumText
//
// 实现方式：Hook RPG.Client.RPGApplication.OnUpdate 作为游戏主线程入口，
// 每 100ms 用 GameObject.Find + GameObject.GetComponent("UnityEngine.UI.Graphic")
// 找到目标 Graphic，保存原 alpha 后写 0；关闭开关时写回原 alpha。
// 所有 il2cpp / Unity 调用都在游戏主线程，绝不在 worker 线程直接调用。
// =============================================================================

#include <Windows.h>

#include "../Common/IpcData.h"

namespace HideUid
{
    /// <summary>
    /// 安装主线程入口 Hook。rpgApplicationOnUpdate 必须有效；成功返回 true。
    /// 幂等：同一进程内重复调用不会重复创建 Hook。
    /// </summary>
    bool Initialize(IpcData* ipc, void* rpgApplicationOnUpdate);

    /// <summary>清空状态掩码；退出前请求主线程恢复原 alpha。</summary>
    void Shutdown(IpcData* ipc);

    /// <summary>主线程 tick 内是否踩到结构化异常（供 dllmain 上报 Error）。</summary>
    bool HasFaulted();
}
