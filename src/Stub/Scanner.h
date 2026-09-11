#pragma once

#include <Windows.h>
#include <string>
#include <vector>
#include <span>
#include <cstdint>

// =============================================================================
// 特征码扫描器：在模块映像中查找字节模式，并解析相对 call/jmp 目标。
// 模式字符串形如 "E8 ? ? ? ? 85 C0"（空格分隔，? / ?? 为通配）。
// =============================================================================
namespace Scanner
{
    /// <summary>将 "AA BB ??" 形式的特征串解析为字节数组（-1 表示通配）。</summary>
    std::vector<int> ParsePattern(const std::string& signature);

    /// <summary>在指定模块的可执行节中扫描特征，返回首处匹配地址；失败返回 nullptr。</summary>
    void* ScanModule(HMODULE module, const std::string& signature);

    /// <summary>
    /// 解析相对寻址指令的目标地址（默认 E8 call：offset=1, instrSize=5）。
    /// target = instruction + instrSize + *(int32_t*)(instruction + offset)
    /// </summary>
    void* ResolveRelative(void* instruction, int offset = 1, int instrSize = 5);
}
