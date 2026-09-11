cmake_minimum_required(VERSION 3.20)
project(FpsUnlockerStub LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# MinHook sources
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
    target_compile_options(FpsUnlockerStub PRIVATE /W3 /EHsc)
    # Export nothing special; DllMain is enough for LoadLibrary injection
    set_target_properties(FpsUnlockerStub PROPERTIES
        OUTPUT_NAME "FpsUnlockerStub"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    )
else()
    # MinGW cross-compile
    target_compile_options(FpsUnlockerStub PRIVATE -Wall -O2)
    target_link_options(FpsUnlockerStub PRIVATE -static-libgcc -static-libstdc++ -static)
    set_target_properties(FpsUnlockerStub PROPERTIES
        PREFIX ""
        OUTPUT_NAME "FpsUnlockerStub"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    )
endif()
