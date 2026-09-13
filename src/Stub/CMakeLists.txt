cmake_minimum_required(VERSION 3.20)
project(FpsUnlockerStub LANGUAGES C CXX)

# C++20：消除 MSVC STL4038（<span> 仅 C++20+ 可用）
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# MinHook sources（直接编入 Stub，不另发 minhook.dll）
set(MINHOOK_DIR "${CMAKE_SOURCE_DIR}/../../third_party/minhook")
set(MINHOOK_SOURCES
    ${MINHOOK_DIR}/src/buffer.c
    ${MINHOOK_DIR}/src/hook.c
    ${MINHOOK_DIR}/src/trampoline.c
    ${MINHOOK_DIR}/src/hde/hde32.c
    ${MINHOOK_DIR}/src/hde/hde64.c
)

add_library(FpsUnlockerStub SHARED
    dllmain.cpp
    Scanner.cpp
    Patch.cpp
    AntiBlur.cpp
    FpsUnlockerStub.def
    ${MINHOOK_SOURCES}
)

target_include_directories(FpsUnlockerStub PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../Common
    ${MINHOOK_DIR}/include
)

target_compile_definitions(FpsUnlockerStub PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

if(MSVC)
    # 静态链接 CRT（/MT）：注入 DLL 不依赖目标机 VC++ 可再发行组件，减小“额外依赖”
    set_property(TARGET FpsUnlockerStub PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    # 源文件包含中文注释，明确告诉 MSVC 按 UTF-8 读取，避免本地代码页误解析注释。
    target_compile_options(FpsUnlockerStub PRIVATE /W3 /EHsc /utf-8)
    set_target_properties(FpsUnlockerStub PROPERTIES
        OUTPUT_NAME "FpsUnlockerStub"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    )
else()
    # MinGW：静态 libgcc/libstdc++，同样不依赖外部运行库 DLL
    target_compile_options(FpsUnlockerStub PRIVATE -Wall -O2)
    target_link_options(FpsUnlockerStub PRIVATE -static-libgcc -static-libstdc++ -static)
    set_target_properties(FpsUnlockerStub PROPERTIES
        PREFIX ""
        OUTPUT_NAME "FpsUnlockerStub"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
endif()
