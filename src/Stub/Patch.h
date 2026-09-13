#pragma once

// =============================================================================
// 运行时字节补丁（备份原字节，可按需 应用 / 还原）。
// 移植自 DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland（utils/Patch.*），
// 原始代码以 MIT 协议发布（Copyright (c) DGP Studio），见仓库 LICENSE.txt。
//
// 与上游的差异（本地加固）：
//   - 构造时仅从可读的执行页备份原指令，不修改页保护；
//   - 页保护只在真正写入的瞬间放开，写完立刻还原，不把代码页长期留成 RWX；
//   - 写入按 8 字节对齐字做原子替换，避免与正在执行该指令的游戏线程「撕裂写」。
// 详见 Patch.cpp 顶部注释。
// =============================================================================

#include <cstddef>
#include <cstdint>
#include <vector>

namespace PatchUtil
{
    /// <summary>整个补丁是否位于同一个对齐的 8 字节字内。</summary>
    bool CanWriteAtomically(const void* dst, size_t n);

    /// <summary>
    /// 通过一次成功的 CAS 替换完整指令，保留同一字中的相邻字节。
    /// 跨字或超过 8 字节时返回 false，且不会修改任何内存。
    /// 调用方负责保证该字可读写，并在 Windows 下刷新指令缓存。
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

    /// <summary>构造是否成功（目标位于可读执行页、原字节已备份）。false 时 Apply/Revert 都是空操作。</summary>
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
};
