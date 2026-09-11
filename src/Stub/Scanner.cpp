#include "Scanner.h"

#include <Psapi.h>
#include <sstream>

namespace Scanner
{
    /// <summary>
    /// 解析特征串：空格分隔的十六进制字节，"?" / "??" 表示通配（存为 -1）。
    /// </summary>
    std::vector<int> ParsePattern(const std::string& signature)
    {
        std::vector<int> pattern;
        std::stringstream ss(signature);
        std::string word;
        while (ss >> word)
        {
            if (word == "?" || word == "??")
            {
                pattern.push_back(-1);
            }
            else
            {
                try
                {
                    pattern.push_back(std::stoi(word, nullptr, 16));
                }
                catch (...)
                {
                    pattern.push_back(-1);
                }
            }
        }
        return pattern;
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
                    for (size_t i = 0; i <= regionSize - pSize; ++i)
                    {
                        bool found = true;
                        for (size_t j = 0; j < pSize; ++j)
                        {
                            if (pattern[j] != -1 && pattern[j] != pStart[i + j])
                            {
                                found = false;
                                break;
                            }
                        }
                        if (found)
                        {
                            return const_cast<uint8_t*>(pStart + i);
                        }
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
        if (!VirtualQuery(reinterpret_cast<LPCVOID>(instrAddr + offset), &mbi, sizeof(mbi)))
        {
            return nullptr;
        }
        if (mbi.State != MEM_COMMIT)
        {
            return nullptr;
        }

        const int32_t relative = *reinterpret_cast<int32_t*>(instrAddr + offset);
        return reinterpret_cast<void*>(instrAddr + instrSize + relative);
    }
}
