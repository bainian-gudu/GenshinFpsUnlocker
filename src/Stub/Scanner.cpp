#include "Scanner.h"
#include "PatternMatch.h"

#include <Psapi.h>

namespace Scanner
{
    /// <summary>
    /// 解析特征串：空格分隔的十六进制字节，"?" / "??" 表示通配（存为 -1）。
    /// </summary>
    namespace
    {
        /// <summary>单个 hex 字符 → 0..15；非法返回 -1。</summary>
        int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    std::vector<int> ParsePattern(const std::string& signature)
    {
        std::vector<int> pattern;
        pattern.reserve(signature.size() / 3 + 1);
        bool invalid = false;

        size_t i = 0;
        while (i < signature.size())
        {
            // 跳过分隔空白
            while (i < signature.size() && (signature[i] == ' ' || signature[i] == '\t' ||
                                            signature[i] == '\r' || signature[i] == '\n'))
            {
                ++i;
            }
            if (i >= signature.size())
            {
                break;
            }

            // 读一个 token（到下一个空白为止）
            const size_t tokenStart = i;
            while (i < signature.size() && signature[i] != ' ' && signature[i] != '\t' &&
                   signature[i] != '\r' && signature[i] != '\n')
            {
                ++i;
            }
            const size_t tokenLen = i - tokenStart;

            if (tokenLen == 1 && signature[tokenStart] == '?')
            {
                pattern.push_back(-1);
            }
            else if (tokenLen == 1 && HexNibble(signature[tokenStart]) >= 0)
            {
                pattern.push_back(HexNibble(signature[tokenStart]));
            }
            else if (tokenLen == 2 && signature[tokenStart] == '?' && signature[tokenStart + 1] == '?')
            {
                pattern.push_back(-1);
            }
            else if (tokenLen == 2)
            {
                const int hi = HexNibble(signature[tokenStart]);
                const int lo = HexNibble(signature[tokenStart + 1]);
                if (hi < 0 || lo < 0)
                {
                    invalid = true;
                    break;
                }
                pattern.push_back((hi << 4) | lo);
            }
            else
            {
                // 签名拼写错误不能静默变成通配符，否则会把匹配范围扩大到
                // 无关代码并诱发错误 Hook/Patch。
                invalid = true;
                break;
            }
        }
        return invalid ? std::vector<int>{} : pattern;
    }

    /// <summary>
    /// 在模块映像的已提交、可读/可执行内存区域中滑动匹配特征码。
    /// 跳过 PAGE_GUARD 页；跨 Region 边界不跨区匹配。
    /// </summary>
    void* ScanModule(HMODULE module, const std::string& signature)
    {
        if (!module || signature.empty())
        {
            return nullptr;
        }

        const auto pattern = ParsePattern(signature);
        if (pattern.empty())
        {
            return nullptr;
        }

        MODULEINFO modInfo{};
        if (!GetModuleInformation(GetCurrentProcess(), module, &modInfo, sizeof(modInfo)))
        {
            return nullptr;
        }

        const uintptr_t startAddr = reinterpret_cast<uintptr_t>(modInfo.lpBaseOfDll);
        const uintptr_t endAddr = startAddr + modInfo.SizeOfImage;
        const size_t pSize = pattern.size();
        const auto compiled = PatternMatch::Compile(pattern);

        uintptr_t current = startAddr;
        while (current < endAddr)
        {
            MEMORY_BASIC_INFORMATION mbi{};
            if (!VirtualQuery(reinterpret_cast<LPCVOID>(current), &mbi, sizeof(mbi)))
            {
                break;
            }

            // 仅扫描已提交且可读（含可执行）的区域
            const bool isGood =
                (mbi.State == MEM_COMMIT) &&
                ((mbi.Protect & PAGE_GUARD) == 0) &&
                (mbi.Protect & (PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_READWRITE | PAGE_READONLY));

            if (isGood)
            {
                size_t regionSize = mbi.RegionSize;
                if (reinterpret_cast<uintptr_t>(mbi.BaseAddress) + regionSize > endAddr)
                {
                    regionSize = endAddr - reinterpret_cast<uintptr_t>(mbi.BaseAddress);
                }

                if (regionSize >= pSize)
                {
                    const uint8_t* pStart = static_cast<const uint8_t*>(mbi.BaseAddress);
                    // memchr 跳到下一个「首固定字节」再整条校验，见 PatternMatch.h
                    if (const uint8_t* hit = PatternMatch::Find(pStart, regionSize, compiled))
                    {
                        return const_cast<uint8_t*>(hit);
                    }
                }
            }

            const uintptr_t nextAddr = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
            if (nextAddr <= current)
            {
                break; // 防止死循环
            }
            current = nextAddr;
        }

        return nullptr;
    }

    /// <summary>
    /// 解析相对寻址（默认 x64 E8 call）。
    /// 用 VirtualQuery 确认可读，避免 SEH，便于 MSVC / MinGW 共用源码。
    /// </summary>
    void* ResolveRelative(void* instruction, int offset, int instrSize)
    {
        if (!instruction)
        {
            return nullptr;
        }

        const uintptr_t instrAddr = reinterpret_cast<uintptr_t>(instruction);
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<LPCVOID>(instrAddr), &mbi, sizeof(mbi)))
            {
                return nullptr;
            }
        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0 ||
            !(mbi.Protect & (PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
            {
                return nullptr;
            }
        if (offset == 1 && instrSize == 5 && *reinterpret_cast<const uint8_t*>(instrAddr) != 0xE8)
            return nullptr;
        if (!VirtualQuery(reinterpret_cast<LPCVOID>(instrAddr + offset), &mbi, sizeof(mbi)))
            return nullptr;
        // 要读的是 offset..offset+3 共 4 字节；跨区域边界就放弃，别赌下一页可读
        const uintptr_t regionEnd = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
        if (instrAddr + static_cast<uintptr_t>(offset) + sizeof(int32_t) > regionEnd)
        {
            return nullptr;
        }

        const int32_t relative = *reinterpret_cast<int32_t*>(instrAddr + offset);
        return reinterpret_cast<void*>(instrAddr + instrSize + relative);
    }
}
