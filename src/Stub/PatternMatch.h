#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

// =============================================================================
// 特征码匹配核心（与平台无关，不依赖 Windows.h，便于单测）。
//
// 旧实现是逐字节滑窗：外层每个偏移、内层逐字节比对，O(n*m) 且内层是
// vector<int> 比较，编译器没法向量化。扫描目标是游戏模块的几十 MB 映像，
// 这段就是注入耗时的大头。
//
// 现在的做法：
//   1. 编译期把模式拆成 bytes + mask（uint8_t，缓存友好）；
//   2. 取第一个非通配字节 b0（下标 k），用 memchr（libc 里是 SIMD 实现）
//      在区域里跳到下一个 b0，再回退 k 个字节校验整条模式；
//      不匹配时一次跳过一大段，而不是逐字节试。
// 匹配语义与旧实现完全一致（含「全通配模式命中首位置」这种退化情况），
// 等价性由 tools 里的 fuzz 对照测试保证（旧/新实现同输入同输出）。
// =============================================================================
namespace Scanner::PatternMatch
{
    struct CompiledPattern
    {
        std::vector<uint8_t> bytes;  // 目标字节（通配位填 0）
        std::vector<uint8_t> mask;   // 1 = 必须相等，0 = 通配
        size_t firstFixed = 0;       // 第一个非通配字节下标
        bool anyFixed = false;

        bool empty() const { return bytes.empty(); }
        size_t size() const { return bytes.size(); }
    };

    /// <summary>把 ParsePattern 的产物（-1 = 通配）编译成 bytes/mask 形式。</summary>
    inline CompiledPattern Compile(const std::vector<int>& pattern)
    {
        CompiledPattern c;
        c.bytes.reserve(pattern.size());
        c.mask.reserve(pattern.size());
        for (size_t i = 0; i < pattern.size(); ++i)
        {
            const int v = pattern[i];
            if (v < 0 || v > 0xFF)
            {
                c.bytes.push_back(0);
                c.mask.push_back(0);
            }
            else
            {
                c.bytes.push_back(static_cast<uint8_t>(v));
                c.mask.push_back(1);
                if (!c.anyFixed)
                {
                    c.anyFixed = true;
                    c.firstFixed = i;
                }
            }
        }
        return c;
    }

    /// <summary>data 处是否整条命中（只比较非通配字节）。</summary>
    inline bool MatchAt(const uint8_t* data, const CompiledPattern& p)
    {
        for (size_t j = 0; j < p.bytes.size(); ++j)
        {
            if (p.mask[j] && p.bytes[j] != data[j])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 在 [data, data+size) 中返回第一处匹配的起点；找不到返回 nullptr。
    /// 与旧滑窗实现返回同一地址（都是「最左匹配」）。
    /// </summary>
    inline const uint8_t* Find(const uint8_t* data, size_t size, const CompiledPattern& p)
    {
        const size_t n = p.size();
        if (n == 0 || size < n)
        {
            return nullptr;
        }
        if (!p.anyFixed)
        {
            return data;  // 全通配：旧实现同样在 i=0 处命中
        }

        const size_t k = p.firstFixed;
        const size_t lastStart = size - n;     // 匹配起点的最大下标
        const size_t b0Last = lastStart + k;   // b0 允许出现的最大下标
        size_t pos = k;

        while (pos <= b0Last)
        {
            const uint8_t* hit = static_cast<const uint8_t*>(
                std::memchr(data + pos, p.bytes[k], b0Last - pos + 1));
            if (!hit)
            {
                return nullptr;
            }
            const size_t hitIdx = static_cast<size_t>(hit - data);
            if (MatchAt(data + (hitIdx - k), p))
            {
                return data + (hitIdx - k);
            }
            pos = hitIdx + 1;
        }
        return nullptr;
    }
}  // namespace Scanner::PatternMatch
