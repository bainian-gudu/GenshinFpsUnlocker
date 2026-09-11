#pragma once

// =============================================================================
// 运行时字节补丁（备份原字节，可按需 应用 / 还原）。
// 移植自 DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland（utils/Patch.*），
// 原始代码以 MIT 协议发布（Copyright (c) DGP Studio），见仓库 LICENSE.txt。
// =============================================================================

#include <cstddef>

class Patch
{
public:
    Patch(void* address, const char* patchBytes, size_t count);
    ~Patch();

    void Apply();
    void Revert();
    void SetIsPatched(bool isPatched);

    bool IsPatched() const { return m_isPatched; }

private:
    void*       m_address;
    size_t      m_count;
    const char* m_patchBytes;
    char*       m_originalBytes;
    bool        m_isPatched;
};
