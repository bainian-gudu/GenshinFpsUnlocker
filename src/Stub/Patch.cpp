// 移植自 DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland（utils/Patch.cpp，MIT）。
//
// 可执行指令补丁必须完整落在同一个对齐的 8 字节字中。
// 多次 CAS 不能组成一个原子指令替换：旧 call 操作码加新 rel32 仍会跳错地址。
// 无法满足边界时放弃该功能；页保护仅在写入期间临时放开。
#include "Patch.h"

#include <cstring>

#if defined(_WIN32)
#include <Windows.h>
#endif

namespace
{
    // ---- 8 字节原子读写：MSVC 用 Interlocked*，GCC/Clang 用 __atomic 内建 ----
#if defined(_MSC_VER)
    inline uint64_t AtomicLoad64(const uint64_t* p)
    {
        // 对齐的 64 位读在 x64 上本身原子；用 CAS(0,0) 换取明确的内存序
        return static_cast<uint64_t>(
            InterlockedCompareExchange64(reinterpret_cast<volatile LONG64*>(const_cast<uint64_t*>(p)), 0, 0));
    }

    inline bool AtomicCas64(uint64_t* p, uint64_t expected, uint64_t desired)
    {
        const LONG64 old = InterlockedCompareExchange64(
            reinterpret_cast<volatile LONG64*>(p),
            static_cast<LONG64>(desired),
            static_cast<LONG64>(expected));
        return old == static_cast<LONG64>(expected);
    }
#elif defined(__GNUC__) || defined(__clang__)
    inline uint64_t AtomicLoad64(const uint64_t* p)
    {
        return __atomic_load_n(p, __ATOMIC_SEQ_CST);
    }

    inline bool AtomicCas64(uint64_t* p, uint64_t expected, uint64_t desired)
    {
        return __atomic_compare_exchange_n(p, &expected, desired, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    }
#else
    // 没有原子内建的编译器：退化为普通读写（仅影响非 Windows/非 GCC 的构建）
    inline uint64_t AtomicLoad64(const uint64_t* p) { return *p; }
    inline bool AtomicCas64(uint64_t* p, uint64_t expected, uint64_t desired)
    {
        if (*p != expected) return false;
        *p = desired;
        return true;
    }
#endif

    constexpr uintptr_t kAlignMask = ~static_cast<uintptr_t>(7);
}

namespace PatchUtil
{
    bool CanWriteAtomically(const void* dst, size_t n)
    {
        if (!dst || n == 0 || n > sizeof(uint64_t)) return false;
        return (reinterpret_cast<uintptr_t>(dst) & 7) + n <= sizeof(uint64_t);
    }

    bool AtomicWriteBytes(void* dst, const void* src, size_t n)
    {
        if (!src || !CanWriteAtomically(dst, n)) return false;

        const uintptr_t address = reinterpret_cast<uintptr_t>(dst);
        auto* word = reinterpret_cast<uint64_t*>(address & kAlignMask);
        const size_t offset = address & 7;
        for (;;)
        {
            const uint64_t current = AtomicLoad64(word);
            uint64_t next = current;
            std::memcpy(reinterpret_cast<uint8_t*>(&next) + offset, src, n);
            if (AtomicCas64(word, current, next)) return true;
        }
    }
}

Patch::Patch(void* address, const char* patchBytes, size_t count)
    : m_address(address)
    , m_count(count)
    , m_patchBytes(patchBytes)
    , m_originalBytes(count)
    , m_isPatched(false)
    , m_valid(false)
{
    if (!patchBytes || !PatchUtil::CanWriteAtomically(address, count))
    {
        return;
    }

#if defined(_WIN32)
    // 备份只需要读取，不为构造一个补丁对象而修改代码页保护。
    MEMORY_BASIC_INFORMATION region{};
    if (!VirtualQuery(address, &region, sizeof(region)) ||
        region.State != MEM_COMMIT || (region.Protect & PAGE_GUARD) ||
        !(region.Protect & (PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
        return;
#endif
    std::memcpy(m_originalBytes.data(), address, count);
    m_valid = true;
}

Patch::~Patch()
{
    Revert();
}

bool Patch::WriteBytes(const void* src)
{
    if (!m_valid || !src)
    {
        return false;
    }

#if defined(_WIN32)
    DWORD oldProtect = 0;
    if (!VirtualProtect(m_address, m_count, PAGE_EXECUTE_READWRITE, &oldProtect))
        return false;

    const bool ok = PatchUtil::AtomicWriteBytes(m_address, src, m_count);
    DWORD ignored = 0;
    if (!VirtualProtect(m_address, m_count, oldProtect, &ignored))
    {
        // 写入成功但恢复保护失败时，不能报告“未写入”而留下生效的补丁。
        // 恢复原指令并停用这个补丁对象，再尝试恢复原保护。
        PatchUtil::AtomicWriteBytes(m_address, m_originalBytes.data(), m_count);
        m_isPatched = false;
        m_valid = false;
        VirtualProtect(m_address, m_count, oldProtect, &ignored);
        FlushInstructionCache(GetCurrentProcess(), m_address, m_count);
        return false;
    }
    return ok && FlushInstructionCache(GetCurrentProcess(), m_address, m_count);
#else
    return PatchUtil::AtomicWriteBytes(m_address, src, m_count);
#endif
}

void Patch::Apply()
{
    if (m_isPatched)
    {
        return;
    }
    if (WriteBytes(m_patchBytes))
    {
        m_isPatched = true;
    }
}

void Patch::Revert()
{
    if (!m_isPatched)
    {
        return;
    }
    if (m_originalBytes.size() == m_count && WriteBytes(m_originalBytes.data()))
    {
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
