# =============================================================================
#  BiuBiuOrigin —— biu_biu_native (C++ GDExtension)
# =============================================================================
#  构建（Windows x86_64 / MSVC / Ninja）：
#    用 tools\build_native.ps1 一键构建；手工命令见 native/README.md
#
#  关键点：
#  1) godot-cpp 用 master 分支 + 锁定 commit hash（上游没有 4.7 分支），
#     通过 GODOTCPP_API_VERSION=4.7 选内置的 extension_api-4-7.json。
#  2) 输出到 addons/biu_biu_native/bin/，文件名须与 .gdextension 的 [libraries] 键一致。
# =============================================================================

cmake_minimum_required(VERSION 3.20)
project(biu_biu_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# -----------------------------------------------------------------------------
# 1) godot-cpp：唯一允许的 API 版本由调用方/默认值钉死为 4.7
# -----------------------------------------------------------------------------
set(GODOTCPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/gdextension/libgodot-cpp")
if(NOT EXISTS "${GODOTCPP_DIR}/CMakeLists.txt")
    message(FATAL_ERROR
        "godot-cpp not found at ${GODOTCPP_DIR}.\n"
        "Run tools\\build_native.ps1 (it clones godot-cpp at the pinned commit), "
        "or see native/README.md for the manual steps.")
endif()

# 上游无 4.7 分支；master 内置 extension_api-4-7.json，故显式指定 API 版本。
if(NOT DEFINED GODOTCPP_API_VERSION OR GODOTCPP_API_VERSION STREQUAL "")
    set(GODOTCPP_API_VERSION "4.7" CACHE STRING "godot-cpp API version to bind against")
endif()

# 绑定生成需要的 Python 解释器（godot-cpp 用 python 脚本生成绑定）。
if(NOT DEFINED GODOTCPP_PYTHON)
    find_program(GODOTCPP_PYTHON NAMES python python3)
endif()

if(NOT TARGET godot-cpp)
    add_subdirectory("${GODOTCPP_DIR}" "${CMAKE_BINARY_DIR}/godot-cpp")
endif()

# -----------------------------------------------------------------------------
# 2) 本扩展的源文件
# -----------------------------------------------------------------------------
file(GLOB_RECURSE BIU_SOURCES CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp"
)

add_library(biu_biu_native SHARED ${BIU_SOURCES})

target_include_directories(biu_biu_native PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
)

target_link_libraries(biu_biu_native PRIVATE godot-cpp)

# 把 C 布局契约头暴露给 C# 侧做对照（不做编译，仅 include 路径）
target_include_directories(biu_biu_native PUBLIC
    "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
)

# MSVC：源码 UTF-8、关掉 PDB 之外的噪声；/utf-8 避免中文注释按 ANSI 解析。
if(MSVC)
    target_compile_options(biu_biu_native PRIVATE /utf-8 /W3)
endif()

# -----------------------------------------------------------------------------
# 3) 输出：addons/biu_biu_native/bin/biu_biu_native.windows.<Config>.x86_64.dll
#    文件名大小写必须与 .gdextension 的 [libraries] 键一致（02 §4.3 注意事项）
# -----------------------------------------------------------------------------
set(BIU_BIN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../addons/biu_biu_native/bin")
file(MAKE_DIRECTORY "${BIU_BIN_DIR}")

set_target_properties(biu_biu_native PROPERTIES
    PREFIX ""
    OUTPUT_NAME "biu_biu_native"
    RUNTIME_OUTPUT_DIRECTORY "${BIU_BIN_DIR}"
)

foreach(cfg Debug Release RelWithDebInfo MinSizeRel)
    string(TOUPPER "${cfg}" CFG_UPPER)
    # 只区分 Debug / Release 两档：GDExtension 按构建类型选库，其余档位归入 Release 命名。
    if(cfg STREQUAL "Debug")
        set(_cfg_name "Debug")
    else()
        set(_cfg_name "Release")
    endif()
    set_target_properties(biu_biu_native PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${CFG_UPPER} "${BIU_BIN_DIR}"
        OUTPUT_NAME_${CFG_UPPER} "biu_biu_native.windows.${_cfg_name}.x86_64"
    )
endforeach()

# 把中间产物（.lib/.exp/.pdb）收进构建目录，别污染 addons/
set_target_properties(biu_biu_native PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)
