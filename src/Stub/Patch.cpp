// 移植自 DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland（utils/Patch.cpp，MIT）。
#include "Patch.h"

#include <Windows.h>
#include <cstring>

Patch::Patch(void* address, const char* patchBytes, size_t count)
{
    m_address = address;
    m_patchBytes = patchBytes;
    m_count = count;
    m_isPatched = false;
    m_originalBytes = new char[count];

    DWORD oldProtect = 0;
    VirtualProtect(address, count, PAGE_EXECUTE_READWRITE, &oldProtect);
    memcpy(m_originalBytes, address, count);
}

Patch::~Patch()
{
    Revert();
    delete[] m_originalBytes;
}

void Patch::Apply()
{
    if (!m_isPatched)
    {
        memcpy(m_address, m_patchBytes, m_count);
        m_isPatched = true;
    }
}

void Patch::Revert()
{
    if (m_isPatched)
    {
        memcpy(m_address, m_originalBytes, m_count);
        m_isPatched = false;
    }
}

void Patch::SetIsPatched(bool isPatched)
{
    if (isPatched)
    {
        Apply();
    }
    else
    {
        Revert();
    }
}
