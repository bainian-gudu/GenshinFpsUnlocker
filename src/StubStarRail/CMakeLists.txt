cmake_minimum_required(VERSION 3.20)
project(StarRailStub LANGUAGES C CXX)

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

add_library(StarRailStub SHARED
    dllmain.cpp
    Il2CppBridge.cpp
    AntiBlur.cpp
    HideUid.cpp
    ../Common/Scanner.cpp
    StarRailStub.def
    ${MINHOOK_SOURCES}
)

target_include_directories(StarRailStub PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../Common
    ${MINHOOK_DIR}/include
)

target_compile_definitions(StarRailStub PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

if(MSVC)
    # 静态链接 CRT（/MT）：注入 DLL 不依赖目标机 VC++ 可再发行组件。
    set_property(TARGET StarRailStub PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    # 源文件包含中文注释，明确告诉 MSVC 按 UTF-8 读取。
    target_compile_options(StarRailStub PRIVATE /W3 /EHsc /utf-8)
    set_target_properties(StarRailStub PROPERTIES
        OUTPUT_NAME "StarRailStub"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    )
else()
    target_compile_options(StarRailStub PRIVATE -Wall -O2)
    target_link_options(StarRailStub PRIVATE -static-libgcc -static-libstdc++ -static)
    set_target_properties(StarRailStub PROPERTIES
        PREFIX ""
        OUTPUT_NAME "StarRailStub"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
endif()
