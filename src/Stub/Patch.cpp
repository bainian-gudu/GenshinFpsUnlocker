// 移植自 DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland（utils/Patch.cpp，MIT）。
//
// 本地加固（对应审查报告 P0-3）：
//   1. VirtualProtect 的返回值必须检查。上游直接忽略，失败时后面的
//      memcpy(m_originalBytes, address, count) 会在构造函数里就 AV。
//   2. 页保护只在写入瞬间放开，写完立刻还原。上游把代码页永久改成
//      PAGE_EXECUTE_READWRITE，既扩大安全面，也更容易被反作弊/AV 记账。
//   3. 写入改成按 8 字节对齐字的原子 CAS。上游用 memcpy 写 5 字节：游戏渲染
//      线程正在执行这条 call 时被改一半，就是撕裂指令 → 崩溃。
//      跨字时从后往前写，保证中间状态永远是「旧操作码 + 任意操作数」这种无害组合。
//   4. 写完 FlushInstructionCache（x86/x64 上其实不需要，但这是文档要求的做法，
//      成本一次系统调用，只在开关切换时发生）。
//
// AtomicWriteBytes 不依赖 Windows API，可在非 Windows 上单测（见 tools 里的对照测试）。
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
    bool AtomicWriteBytes(void* dst, const void* src, size_t n)
    {
        if (!dst || !src || n == 0)
        {
            return false;
        }

        auto* const d = static_cast<uint8_t*>(dst);
        const auto* const s = static_cast<const uint8_t*>(src);
        const uintptr_t dAddr = reinterpret_cast<uintptr_t>(d);
        const uintptr_t firstWord = dAddr & kAlignMask;
        const uintptr_t lastWord = (dAddr + n - 1) & kAlignMask;

        // 从最后一个字倒着写到第一个字（含首字节的那个字最后落地）
        for (uintptr_t word = lastWord;; word -= 8)
        {
            auto* const w = reinterpret_cast<uint64_t*>(word);
            const auto* const wb = reinterpret_cast<const uint8_t*>(word);

            // 该 8 字节字与 [d, d+n) 的交集
            size_t begin = 0;
            size_t end = 8;
            if (d > wb)
            {
                begin = static_cast<size_t>(d - wb);
            }
            if (d + n < wb + 8)
            {
                end = static_cast<size_t>(d + n - wb);
            }

            // 交集内的首字节在 src 里的偏移
            const size_t srcOffset = static_cast<size_t>(word + begin - dAddr);

            for (;;)
            {
                const uint64_t current = AtomicLoad64(w);
                uint64_t next = current;
                std::memcpy(reinterpret_cast<uint8_t*>(&next) + begin, s + srcOffset, end - begin);
                if (AtomicCas64(w, current, next))
                {
                    break;
                }
            }

            if (word == firstWord)
            {
                break;
            }
        }

        return true;
    }
}

Patch::Patch(void* address, const char* patchBytes, size_t count)
    : m_address(address)
    , m_count(count)
    , m_patchBytes(patchBytes)
    , m_originalBytes(count)
    , m_isPatched(false)
    , m_valid(false)
    , m_protect(0)
{
    if (!address || !patchBytes || count == 0)
    {
        return;
    }

#if defined(_WIN32)
    DWORD oldProtect = 0;
    if (!VirtualProtect(address, count, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        // 放不开页保护：不要读、不要写，标记无效让调用方放弃这个补丁
        return;
    }

    std::memcpy(m_originalBytes.data(), address, count);

    // 立刻还原原始保护；真正写入时再临时放开
    DWORD tmp = 0;
    if (!VirtualProtect(address, count, oldProtect, &tmp))
    {
        // 还原失败就把页留在可写状态（比留下错误的原字节更安全）
        m_protect = 0;
    }
    else
    {
        m_protect = static_cast<uint32_t>(oldProtect);
    }

    m_valid = true;
#else
    std::memcpy(m_originalBytes.data(), address, count);
    m_valid = true;
#endif
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
    DWORD tmp = 0;
    if (!VirtualProtect(m_address, m_count, PAGE_EXECUTE_READWRITE, &tmp))
    {
        return false;
    }

    const bool ok = PatchUtil::AtomicWriteBytes(m_address, src, m_count);

    DWORD restore = 0;
    const DWORD target = m_protect ? static_cast<DWORD>(m_protect) : tmp;
    VirtualProtect(m_address, m_count, target, &restore);
    FlushInstructionCache(GetCurrentProcess(), m_address, m_count);
    return ok;
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
