#pragma once

// =============================================================================
// 运行时字节补丁（备份原字节，可按需 应用 / 还原）。
// 移植自 DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland（utils/Patch.*），
// 原始代码以 MIT 协议发布（Copyright (c) DGP Studio），见仓库 LICENSE.txt。
//
// 与上游的差异（本地加固）：
//   - 构造时校验 VirtualProtect 是否成功，失败即标记为无效，绝不读写目标内存；
//   - 页保护只在真正写入的瞬间放开，写完立刻还原，不把代码页长期留成 RWX；
//   - 写入按 8 字节对齐字做原子替换，避免与正在执行该指令的游戏线程「撕裂写」。
// 详见 Patch.cpp 顶部注释。
// =============================================================================

#include <cstddef>
#include <cstdint>
#include <vector>

namespace PatchUtil
{
    /// <summary>
    /// 以 8 字节为单位原子写入 n 字节（n 任意）。
    /// 跨多个 8 字节字时，从最后一个字往第一个字写：含首字节的那个字最后落地，
    /// 于是任何中间状态都不会让 CPU 把「半条新指令」当旧指令执行
    /// （例如 call 的操作码已改、rel32 还是旧值 → 跳去错误地址）。
    /// 页对齐是 8 的整数倍，所以向下对齐到 8 字节永远不会跨页。
    /// 调用方需保证 [dst, dst+n) 所在页可读写。
    /// </summary>
    bool AtomicWriteBytes(void* dst, const void* src, size_t n);
}

class Patch
{
public:
    Patch(void* address, const char* patchBytes, size_t count);
    ~Patch();

    void Apply();
    void Revert();
    void SetIsPatched(bool isPatched);

    bool IsPatched() const { return m_isPatched; }

    /// <summary>构造是否成功（页保护可放开、原字节已备份）。false 时 Apply/Revert 都是空操作。</summary>
    bool IsValid() const { return m_valid; }

private:
    /// <summary>把 src 的 m_count 字节写进目标（临时放开页保护 + 原子写 + 还原 + 刷指令缓存）。</summary>
    bool WriteBytes(const void* src);

    void*             m_address;
    size_t            m_count;
    const char*       m_patchBytes;
    std::vector<char> m_originalBytes;
    bool              m_isPatched;
    bool              m_valid;
    uint32_t          m_protect;   // 目标原始页保护（DWORD）
};
