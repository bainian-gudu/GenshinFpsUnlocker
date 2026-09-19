// =============================================================================
// StarRailStub 专用的 il2cpp 定位与调用实现。
//
// 定位优先级：
//   1) dump.cs 实测 RVA（国服 4.5.0，GameAssembly.dll ImageBase 0x180000000）；
//   2) 特征码唯一命中兜底（只对 GameObject.Find / GameObject.GetComponent 有
//      可用特征码；多命中直接判失败，避免调用到无关函数）。
//
// 所有 il2cpp / Unity 对象调用都必须发生在游戏主线程；本文件只提供
// 「主线程内可用」的封装，不在 worker 线程直接调用。
// =============================================================================

#include "Il2CppBridge.h"

#include <Psapi.h>

#include <cstddef>
#include <cstring>
#include <new>

#include "PatternMatch.h"
#include "Scanner.h"

#pragma comment(lib, "Psapi.lib")

namespace
{
    // ---- 国服 4.5.0 dump.cs 实测 RVA（ImageBase 0x180000000）----
    constexpr uintptr_t kRvaGameObjectFind = 0x1DEDE300;
    constexpr uintptr_t kRvaComponentGetComponent = 0x1DEDDE30;
    constexpr uintptr_t kRvaRpgApplicationOnUpdate = 0x1802FBF0;
    constexpr uintptr_t kRvaVCameraDofOnActive = 0x1C7FD870;
    constexpr uintptr_t kRvaVCameraDofUpdate = 0x1C7FCFC0;
    constexpr uintptr_t kRvaGraphicSetVerticesDirty = 0x1B78C0C0;

    // ---- 参考实现（30launchers）的特征码：4.5.0 上 Find 命中 9 处、
    //      GetComponent 命中 8 处。这里只用于 RVA 失效后的「唯一命中」兜底。----
    constexpr const char* kGameObjectFindPattern =
        "48 FF ?? ?? ?? ?? ?? 66 0F 1F 84 00 00 00 00 00 48 83 EC 28 C7 44 24 20";
    constexpr const char* kGetComponentPattern =
        "48 8B 05 ?? ?? ?? ?? 48 FF E0 66 0F 1F 44 00 00 48 8B 05 ?? ?? ?? ?? 45 31 C0 48 FF E0 0F 1F 00";

    // 无特征码目标的函数头校验（4.5.0 实测字节）。
    // 版本更新后即使 RVA 仍落在模块内，也不允许它指向一个形态不对的函数。
    constexpr const char* kRpgApplicationOnUpdateHead = "56 57 48 83 EC 48 0F 29 7C 24 30";
    constexpr const char* kVCameraDofOnActiveHead = "56 48 83 EC 20 48 89 CE 80 3D";
    constexpr const char* kVCameraDofUpdateHead = "56 48 83 EC 20 48 89 CE 80 3D";
    constexpr const char* kGraphicSetVerticesDirtyHead = "56 48 83 EC 20 48 89 CE FF 15";

    // UnityEngine.UI.Graphic.m_Color // Offset: 0x20（dump.cs 实测）
    // Color 是 4 个 float：r/g/b/a，alpha 位于 +0x0C。
    constexpr size_t kGraphicColorOffset = 0x20;
    constexpr size_t kGraphicColorAlphaOffset = kGraphicColorOffset + 3 * sizeof(float);

    using FindFn = void* (*)(void*);
    using GetComponentFn = void* (*)(void*, void*);
    using SetVerticesDirtyFn = void (*)(void*);

    Il2CppBridge::Functions g_functions{};

    /// <summary>
    /// 自建 il2cpp string。
    ///
    /// 星铁没有可用的 il2cpp_string_new 导出，参考实现同样是自建 length + chars。
    /// 这里只保证 GameObject.Find / GetComponent 读取期间有效，调用后立刻释放；
    /// 不把它交给会长期持有引用的游戏代码。
    /// </summary>
    struct Il2CppString
    {
        void* klass;
        void* monitor;
        int32_t length;
        wchar_t chars[1];
    };

    class ScopedIl2CppString
    {
    public:
        explicit ScopedIl2CppString(const char* utf8)
        {
            if (!utf8 || *utf8 == '\0')
            {
                return;
            }

            const int wideLength = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
            if (wideLength <= 1)
            {
                return;
            }

            // 多留一个 wchar_t 给结尾 NUL：IL2CPP 的 length 不含 NUL，
            // 但底层字符串缓冲区按惯例以 NUL 结尾，避免转换时越界写入。
            const size_t bytes = offsetof(Il2CppString, chars) +
                                 static_cast<size_t>(wideLength) * sizeof(wchar_t);
            auto* value = static_cast<Il2CppString*>(::operator new[](bytes, std::nothrow));
            if (!value)
            {
                return;
            }

            std::memset(value, 0, bytes);
            value->klass = nullptr;
            value->monitor = nullptr;
            value->length = wideLength - 1;
            MultiByteToWideChar(CP_UTF8, 0, utf8, -1, value->chars, wideLength);
            _value = value;
        }

        ~ScopedIl2CppString()
        {
            ::operator delete[](_value);
        }

        ScopedIl2CppString(const ScopedIl2CppString&) = delete;
        ScopedIl2CppString& operator=(const ScopedIl2CppString&) = delete;

        bool Valid() const { return _value != nullptr; }
        void* Get() const { return _value; }

    private:
        Il2CppString* _value = nullptr;
    };

    /// <summary>地址是否落在模块映像内且可执行。</summary>
    bool IsExecutableInModule(HMODULE module, const void* address)
    {
        if (!module || !address)
        {
            return false;
        }

        MODULEINFO mi{};
        if (!GetModuleInformation(GetCurrentProcess(), module, &mi, sizeof(mi)))
        {
            return false;
        }

        const uintptr_t base = reinterpret_cast<uintptr_t>(mi.lpBaseOfDll);
        const uintptr_t addr = reinterpret_cast<uintptr_t>(address);
        if (addr < base || addr >= base + mi.SizeOfImage)
        {
            return false;
        }

        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(address, &mbi, sizeof(mbi)))
        {
            return false;
        }
        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0)
        {
            return false;
        }
        return (mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE |
                               PAGE_EXECUTE_WRITECOPY)) != 0;
    }

    /// <summary>按 RVA 取函数地址；不在模块内或不可执行返回 nullptr。</summary>
    void* ResolveRva(HMODULE module, uintptr_t rva)
    {
        auto* address = reinterpret_cast<uint8_t*>(module) + rva;
        return IsExecutableInModule(module, address) ? address : nullptr;
    }

    /// <summary>地址处是否匹配给定特征码（读取前先确认整个模式在同一内存区域内）。</summary>
    bool MatchesPatternAt(void* address, const char* pattern)
    {
        if (!address || !pattern)
        {
            return false;
        }

        const auto parsed = Scanner::ParsePattern(pattern);
        if (parsed.empty())
        {
            return false;
        }
        const auto compiled = Scanner::PatternMatch::Compile(parsed);

        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(address, &mbi, sizeof(mbi)))
        {
            return false;
        }
        const uintptr_t start = reinterpret_cast<uintptr_t>(address);
        const uintptr_t regionEnd = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0 ||
            start + compiled.size() > regionEnd)
        {
            return false;
        }
        return Scanner::PatternMatch::MatchAt(static_cast<const uint8_t*>(address), compiled);
    }

    /// <summary>RVA 命中且函数头匹配时才返回地址。</summary>
    void* ResolveRvaWithPattern(HMODULE module, uintptr_t rva, const char* pattern)
    {
        void* address = ResolveRva(module, rva);
        return MatchesPatternAt(address, pattern) ? address : nullptr;
    }

    /// <summary>特征码唯一命中才返回地址；0 处或多处都返回 nullptr。</summary>
    void* ResolveUniquePattern(HMODULE module, const char* pattern)
    {
        const auto hits = Scanner::ScanModuleAll(module, pattern);
        return hits.size() == 1 ? hits.front() : nullptr;
    }

    /// <summary>
    /// 带 SEH 的 GameObject.Find 调用。
    /// 该函数内不能出现需要析构的 C++ 对象（MSVC C2712），字符串构造放在调用方。
    /// </summary>
    void* CallFindRaw(void* str)
    {
        if (!g_functions.gameObjectFind)
        {
            return nullptr;
        }
#if defined(_MSC_VER)
        __try
        {
            return reinterpret_cast<FindFn>(g_functions.gameObjectFind)(str);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return nullptr;
        }
#else
        return reinterpret_cast<FindFn>(g_functions.gameObjectFind)(str);
#endif
    }

    /// <summary>带 SEH 的 GameObject.GetComponent(string) 调用。</summary>
    void* CallGetComponentRaw(void* gameObject, void* typeName)
    {
        if (!g_functions.componentGetComponent)
        {
            return nullptr;
        }
#if defined(_MSC_VER)
        __try
        {
            return reinterpret_cast<GetComponentFn>(g_functions.componentGetComponent)(gameObject, typeName);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return nullptr;
        }
#else
        return reinterpret_cast<GetComponentFn>(g_functions.componentGetComponent)(gameObject, typeName);
#endif
    }
}

namespace Il2CppBridge
{
    ResolveStatus Resolve(HMODULE gameAssembly, Functions& out)
    {
        out = Functions{};
        if (!gameAssembly)
        {
            return ResolveStatus::GameAssemblyMissing;
        }

        // GameObject.Find：RVA 优先，特征码唯一命中兜底。
        out.gameObjectFind = ResolveRvaWithPattern(gameAssembly, kRvaGameObjectFind, kGameObjectFindPattern);
        if (!out.gameObjectFind)
        {
            out.gameObjectFind = ResolveUniquePattern(gameAssembly, kGameObjectFindPattern);
        }
        if (!out.gameObjectFind)
        {
            return ResolveStatus::FindMissing;
        }

        // GameObject.GetComponent(string)：同上（传入的是 GameObject.Find 的返回值）。
        out.componentGetComponent =
            ResolveRvaWithPattern(gameAssembly, kRvaComponentGetComponent, kGetComponentPattern);
        if (!out.componentGetComponent)
        {
            out.componentGetComponent = ResolveUniquePattern(gameAssembly, kGetComponentPattern);
        }
        if (!out.componentGetComponent)
        {
            return ResolveStatus::GetComponentMissing;
        }

        // RPGApplication.OnUpdate：UID 隐藏唯一安全的主线程入口，无特征码兜底。
        out.rpgApplicationOnUpdate =
            ResolveRvaWithPattern(gameAssembly, kRvaRpgApplicationOnUpdate, kRpgApplicationOnUpdateHead);
        if (!out.rpgApplicationOnUpdate)
        {
            return ResolveStatus::MainThreadEntryMissing;
        }

        // 反虚化入口：OnActiveVCamera / Update 至少一个可用。
        out.vCameraDofOnActive =
            ResolveRvaWithPattern(gameAssembly, kRvaVCameraDofOnActive, kVCameraDofOnActiveHead);
        out.vCameraDofUpdate =
            ResolveRvaWithPattern(gameAssembly, kRvaVCameraDofUpdate, kVCameraDofUpdateHead);
        if (!out.vCameraDofOnActive && !out.vCameraDofUpdate)
        {
            return ResolveStatus::AntiBlurEntryMissing;
        }

        // UI 重建通知：可选辅助路径，定位失败只降级为「直接写 m_Color」，
        // 不让整个模块因此 Error。
        out.graphicSetVerticesDirty =
            ResolveRvaWithPattern(gameAssembly, kRvaGraphicSetVerticesDirty, kGraphicSetVerticesDirtyHead);

        g_functions = out;
        return ResolveStatus::Ok;
    }

    void* FindGraphic(const char* path)
    {
        if (!path || !g_functions.gameObjectFind || !g_functions.componentGetComponent)
        {
            return nullptr;
        }

        ScopedIl2CppString pathString(path);
        if (!pathString.Valid())
        {
            return nullptr;
        }

        void* gameObject = CallFindRaw(pathString.Get());
        if (!gameObject)
        {
            return nullptr;
        }

        ScopedIl2CppString typeName("UnityEngine.UI.Graphic");
        if (!typeName.Valid())
        {
            return nullptr;
        }
        return CallGetComponentRaw(gameObject, typeName.Get());
    }

    bool ReadGraphicAlpha(void* graphic, float& alpha)
    {
        if (!graphic)
        {
            return false;
        }
#if defined(_MSC_VER)
        __try
        {
            alpha = *reinterpret_cast<const float*>(
                reinterpret_cast<const uint8_t*>(graphic) + kGraphicColorAlphaOffset);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        alpha = *reinterpret_cast<const float*>(
            reinterpret_cast<const uint8_t*>(graphic) + kGraphicColorAlphaOffset);
        return true;
#endif
    }

    bool WriteGraphicAlpha(void* graphic, float alpha)
    {
        if (!graphic)
        {
            return false;
        }
#if defined(_MSC_VER)
        __try
        {
            *reinterpret_cast<float*>(
                reinterpret_cast<uint8_t*>(graphic) + kGraphicColorAlphaOffset) = alpha;
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        *reinterpret_cast<float*>(
            reinterpret_cast<uint8_t*>(graphic) + kGraphicColorAlphaOffset) = alpha;
        return true;
#endif
    }

    void NotifyGraphicColorChanged(void* graphic)
    {
        if (!graphic || !g_functions.graphicSetVerticesDirty)
        {
            return;
        }
#if defined(_MSC_VER)
        __try
        {
            reinterpret_cast<SetVerticesDirtyFn>(g_functions.graphicSetVerticesDirty)(graphic);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // 辅助路径失败不影响隐藏本身；下一次 tick 仍会重试。
        }
#else
        reinterpret_cast<SetVerticesDirtyFn>(g_functions.graphicSetVerticesDirty)(graphic);
#endif
    }

    const Functions& Resolved()
    {
        return g_functions;
    }
}
