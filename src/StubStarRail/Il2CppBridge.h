#pragma once

// =============================================================================
// StarRailStub 专用的 il2cpp 调用封装。
//
// 星铁的 GameAssembly.dll 只导出 il2cpp_get_api_table 一个符号，没有原神那套
// il2cpp_* 导出；本模块按 dump.cs 的 RVA 定位，必要时用参考实现的特征码兜底。
//
// 该文件只服务星穹铁道，与原神 Stub 相互独立：
//   - 不引用 src/Stub 下的任何业务代码；
//   - 只复用 src/Common 的 Scanner / IpcData 这类与游戏无关的基础设施。
// =============================================================================

#include <Windows.h>

#include <cstdint>

namespace Il2CppBridge
{
    /// <summary>已定位的星铁 il2cpp 目标地址。</summary>
    struct Functions
    {
        void* gameObjectFind = nullptr;           // GameObject.Find(string)
        void* componentGetComponent = nullptr;    // GameObject.GetComponent(string)
        void* rpgApplicationOnUpdate = nullptr;   // RPG.Client.RPGApplication.OnUpdate
        void* ditherSetAlphaValue = nullptr;      // BaseShaderPropertyTransition 私有相机 Dither 汇合入口
        void* ditherSetDistanceAlpha = nullptr;   // BaseShaderPropertyTransition.SetDistanceDitherAlphaValue
        void* ditherSetElevationAlpha = nullptr;  // BaseShaderPropertyTransition.SetElevationDitherAlphaValue
        void* graphicSetVerticesDirty = nullptr;  // Graphic.SetVerticesDirty（可选，触发 UI 重建）
    };

    /// <summary>定位结果，用于宿主错误码分级。</summary>
    enum class ResolveStatus
    {
        Ok = 0,
        GameAssemblyMissing,
        FindMissing,
        GetComponentMissing,
        MainThreadEntryMissing,
        DitherEntryMissing,
    };

    /// <summary>
    /// 定位全部目标。RVA 优先（4.5.0 dump.cs 实测值）；RVA 失效时只接受
    /// 特征码的「唯一命中」，多命中一律失败 —— 参考实现的多候选硬试会调用到
    /// Texture2D.SetPixels32 / Animator.Play 等无关函数，不能照搬。
    /// </summary>
    ResolveStatus Resolve(HMODULE gameAssembly, Functions& out);

    /// <summary>
    /// 按层级路径取 UnityEngine.UI.Graphic 组件。
    /// 必须在游戏主线程调用；失败返回 nullptr。
    /// </summary>
    void* FindGraphic(const char* path);

    /// <summary>读取 Graphic.m_Color.a（dump.cs：m_Color Offset 0x20）。</summary>
    bool ReadGraphicAlpha(void* graphic, float& alpha);

    /// <summary>写入 Graphic.m_Color.a。只改 alpha，不动 RGB。</summary>
    bool WriteGraphicAlpha(void* graphic, float alpha);

    /// <summary>
    /// 通知 Graphic 顶点需要重建，让直接写入的 m_Color 立即生效。
    /// 定位失败时是空操作；调用点不需要把它当成硬依赖。
    /// </summary>
    void NotifyGraphicColorChanged(void* graphic);

    /// <summary>最近一次 Resolve 的地址表（供主线程 tick 读取）。</summary>
    const Functions& Resolved();
}
