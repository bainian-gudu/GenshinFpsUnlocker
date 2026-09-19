#pragma once

#include <Windows.h>
#include <string>
#include <vector>
#include <span>
#include <cstdint>

// =============================================================================
// 特征码扫描器（公共工具）：在模块映像中查找字节模式，并解析相对 call/jmp 目标。
// 模式字符串形如 "E8 ? ? ? ? 85 C0"（空格分隔，? / ?? 为通配）。
//
// 该文件被两个游戏的注入模块共同引用；游戏相关特征码与业务逻辑不放在这里，
// 由各自的 Stub 目录维护，保持原神 / 星穹铁道相互解耦。
// =============================================================================
namespace Scanner
{
    /// <summary>将 "AA BB ??" 形式的特征串解析为字节数组（-1 表示通配；非法签名返回空数组）。</summary>
    std::vector<int> ParsePattern(const std::string& signature);

    /// <summary>在指定模块的可执行节中扫描特征，返回首处匹配地址；失败返回 nullptr。</summary>
    void* ScanModule(HMODULE module, const std::string& signature);

    /// <summary>
    /// 在指定模块中扫描特征，按地址升序返回全部命中。
    /// 用于「特征码必然有多处命中，需要再按已知 RVA / 上下文筛选」的场景。
    /// </summary>
    std::vector<void*> ScanModuleAll(HMODULE module, const std::string& signature);

    /// <summary>
    /// 解析相对寻址指令的目标地址（默认 E8 call：offset=1, instrSize=5）。
    /// target = instruction + instrSize + *(int32_t*)(instruction + offset)
    /// </summary>
    void* ResolveRelative(void* instruction, int offset = 1, int instrSize = 5);
}
