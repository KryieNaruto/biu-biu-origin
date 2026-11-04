# =============================================================================
# AssetViewer —— 素材库查看器（独立 Windows 桌面程序，不依赖 Godot / GDExtension）
# -----------------------------------------------------------------------------
# 依赖决策（2026-11-04 实测，详见 README「依赖决策」）：
#   * 零第三方依赖：只用 MSVC + Windows SDK（D3D11 / DXGI / D2D1 / DWrite / WIC）。
#   * 不做 find_package / FetchContent / vcpkg 集成；只要 VS Installer 装了
#     「使用 C++ 的桌面开发」（含 Windows 10/11 SDK）即可构建。
#   * 并行开发容错（两层）：
#       1) 源文件按 if(EXISTS) 逐个收集，缺失只 WARNING；
#       2) 公共头（model_data.h / glb_loader.h / ... / d3d_renderer.h）缺失时，
#          在 build/generated/stub_headers/ 下自动生成「空占位头」并把该目录设为
#          **最后一个** include 目录 —— 协作者头文件优先，落地后自动取代占位。
#          于是「头还没写、.cpp 已写」的模块也能编译（符号在链接期才会缺失）。
#     入口（app_main.cpp / tests/*.cpp）缺失的可执行目标会被跳过，不中断配置。
#
# 常用命令（必须先 call vcvars64.bat，即使用 Ninja 也必须，否则找不到 cl.exe）：
#   cmake -S tools/asset_viewer -B tools/asset_viewer/build -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build tools/asset_viewer/build
#   或直接： .\tools\asset_viewer\build.ps1 -Config Release
# =============================================================================
cmake_minimum_required(VERSION 3.25)

project(AssetViewer
    VERSION 0.1.0
    DESCRIPTION "BiuBiuOrigin asset library viewer (glb/fbx thumbnails)"
    LANGUAGES CXX)

# -----------------------------------------------------------------------------
# 0. 基础防护与默认值
# -----------------------------------------------------------------------------
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "AssetViewer 仅支持 64 位构建（请从 x64 Native Tools 或 vcvars64.bat 启动）。")
endif()

if(NOT MSVC)
    message(WARNING "AssetViewer 的目标工具链是 MSVC + Windows SDK；当前编译器 ${CMAKE_CXX_COMPILER_ID} 未经验证。")
endif()

# 单配置生成器（Ninja）未指定构建类型时默认 Release
get_property(_av_multi_config GLOBAL PROPERTY GENERATOR_IS_MULTI_CONFIG)
if(NOT _av_multi_config AND NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "构建类型: Debug / Release / RelWithDebInfo / MinSizeRel" FORCE)
    set_property(CACHE CMAKE_BUILD_TYPE PROPERTY STRINGS Debug Release RelWithDebInfo MinSizeRel)
endif()

# -----------------------------------------------------------------------------
# 1. 选项
# -----------------------------------------------------------------------------
option(AV_BUILD_TESTS        "构建自测目标（loader_smoke / ui_selftest / thumb_sheet）"  ON)
option(AV_STATIC_RUNTIME     "静态链接 MSVC 运行库（/MT），产物免装 VC++ 运行库"           OFF)
option(AV_WARNINGS_AS_ERRORS "把 /W4 警告升级为错误（默认只警告，并行开发阶段更友好）"     OFF)
option(AV_BUILD_PROBE        "编译 build/_probe 下的工具链探针（临时验证用，默认关）"      OFF)
option(AV_STUB_MISSING_HEADERS "自动生成缺失公共头的空占位头（并行开发容错，落地后自动失效）" ON)
set(AV_WIN_SDK_VERSION "" CACHE STRING "指定 Windows SDK 版本（空 = 跟随 VS 默认）")

if(AV_WIN_SDK_VERSION)
    set(CMAKE_SYSTEM_VERSION "${AV_WIN_SDK_VERSION}" CACHE STRING "Windows SDK 版本" FORCE)
endif()

# -----------------------------------------------------------------------------
# 2. 目录约定
# -----------------------------------------------------------------------------
set(AV_SRC_DIR   "${CMAKE_CURRENT_SOURCE_DIR}/src")
set(AV_TESTS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/tests")
set(AV_GEN_DIR   "${CMAKE_BINARY_DIR}/generated")
set(AV_OUT_DIR   "${CMAKE_BINARY_DIR}/out")               # 全部产物（exe/lib）输出到这里
file(REAL_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../.."
     AV_PROJECT_ROOT)                                      # 工程根：运行时定位 素材/（只作 cwd）

file(MAKE_DIRECTORY "${AV_GEN_DIR}")

# -----------------------------------------------------------------------------
# 2.1 特性开关：AV_HAVE_LOADERS
#   scan_worker.cpp / viewer_app.cpp 用 `#if AV_HAVE_LOADERS` 切换「真实解析」与「占位缩略图」。
#   实测：该宏在源码里只有 #if 与 (int) 打印两种用法，**没有 #ifdef/#ifndef/#if defined(...)**，
#   因此未定义时 MSVC 会按 0 处理，#if 分支被静默裁掉 —— 占位模式「悄悄生效」。
#   为了避免这种静默降级，这里按源文件是否落地显式定义 0/1（并进 CMakeCache，配置输出里可见）。
#   -DAV_HAVE_LOADERS=OFF 可强制用占位模式（UI 联调、没有素材时很有用）。
# -----------------------------------------------------------------------------
option(AV_HAVE_LOADERS "启用真实 glb/fbx 解析（OFF=占位缩略图/假统计，UI 联调用）" ON)
set(_av_loaders_ok ${AV_HAVE_LOADERS})
foreach(_req IN ITEMS glb_loader.cpp fbx_loader.cpp texture.cpp thumb_renderer.cpp wic_util.cpp)
    if(NOT EXISTS "${AV_SRC_DIR}/${_req}")
        set(_av_loaders_ok OFF)
    endif()
endforeach()
if(_av_loaders_ok)
    set(AV_LOADERS_DEFINE 1)
else()
    set(AV_LOADERS_DEFINE 0)
    if(AV_HAVE_LOADERS)
        message(STATUS "AssetViewer: 解析模块源文件尚未齐全，AV_HAVE_LOADERS 自动降为 0（占位缩略图）。")
    endif()
endif()

# -----------------------------------------------------------------------------
# 3. 源文件清单（并行开发容错：按 EXIST 逐个收集）
# -----------------------------------------------------------------------------
set(AV_EXPECTED_SOURCES
    # 共享头（Lead）
    src/model_data.h
    # 解析 / 贴图 / 渲染（解析渲染同事）
    src/glb_loader.cpp       src/glb_loader.h
    src/fbx_loader.cpp       src/fbx_loader.h
    src/texture.cpp          src/texture.h
    src/wic_util.cpp         src/wic_util.h
    src/thumb_renderer.cpp   src/thumb_renderer.h
    src/anim_sampler.cpp     src/anim_sampler.h
    # 应用层（UI 同事）
    src/app_main.cpp
    src/viewer_app.cpp       src/viewer_app.h
    src/asset_scanner.cpp    src/asset_scanner.h
    src/scan_worker.cpp      src/scan_worker.h
    src/d3d_renderer.cpp     src/d3d_renderer.h
    # UI 侧内部工具（winui-list 名下，非契约头）
    src/av_util.cpp          src/av_util.h
    # 测试入口（各自有 main）
    tests/loader_smoke.cpp
    tests/ui_selftest.cpp
    tests/thumb_sheet.cpp
)

# 公共头白名单：只有这些才会生成占位头。
# 注意：不包含 av_util.h —— 它是 UI 侧内部头，生成空占位会静默掩盖真实 API，
#       所以缺它就如实报错（见 README「并行开发」）。
set(AV_STUBBABLE_HEADERS
    model_data.h
    glb_loader.h
    fbx_loader.h
    texture.h
    wic_util.h
    thumb_renderer.h
    anim_sampler.h
    asset_scanner.h
    scan_worker.h
    viewer_app.h
    d3d_renderer.h
)

set(AV_PRESENT_SOURCES "")
set(AV_MISSING_SOURCES "")
foreach(_rel IN LISTS AV_EXPECTED_SOURCES)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/${_rel}")
        list(APPEND AV_PRESENT_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/${_rel}")
    else()
        list(APPEND AV_MISSING_SOURCES "${_rel}")
    endif()
endforeach()

# 切片：.cpp / .h
set(AV_PRESENT_CPP "")
set(AV_PRESENT_H "")
foreach(_f IN LISTS AV_PRESENT_SOURCES)
    get_filename_component(_ext "${_f}" EXT)
    if(_ext STREQUAL ".cpp")
        list(APPEND AV_PRESENT_CPP "${_f}")
    else()
        list(APPEND AV_PRESENT_H "${_f}")
    endif()
endforeach()

# 切片：UI 层 .cpp（带 d3d_renderer）+ 其它底层 .cpp
set(AV_UI_CPP "")
set(AV_FOUNDATION_CPP "")
list(APPEND AV_UI_CPP  "${AV_SRC_DIR}/app_main.cpp")
list(APPEND AV_UI_CPP  "${AV_SRC_DIR}/viewer_app.cpp")
list(APPEND AV_UI_CPP  "${AV_SRC_DIR}/asset_scanner.cpp")
list(APPEND AV_UI_CPP  "${AV_SRC_DIR}/scan_worker.cpp")
list(APPEND AV_UI_CPP  "${AV_SRC_DIR}/d3d_renderer.cpp")
list(APPEND AV_UI_CPP  "${AV_SRC_DIR}/av_util.cpp")
list(APPEND AV_FOUNDATION_CPP
    "${AV_SRC_DIR}/wic_util.cpp"
    "${AV_SRC_DIR}/texture.cpp"
    "${AV_SRC_DIR}/glb_loader.cpp"
    "${AV_SRC_DIR}/fbx_loader.cpp"
    "${AV_SRC_DIR}/thumb_renderer.cpp"
    "${AV_SRC_DIR}/anim_sampler.cpp")
set(AV_UI_CPP_PRESENT "")
foreach(_f IN LISTS AV_UI_CPP)
    if(EXISTS "${_f}")
        list(APPEND AV_UI_CPP_PRESENT "${_f}")
    endif()
endforeach()
set(AV_FOUNDATION_CPP_PRESENT "")
foreach(_f IN LISTS AV_FOUNDATION_CPP)
    if(EXISTS "${_f}")
        list(APPEND AV_FOUNDATION_CPP_PRESENT "${_f}")
    endif()
endforeach()

list(LENGTH AV_PRESENT_CPP AV_PRESENT_CPP_COUNT)
list(LENGTH AV_PRESENT_H   AV_PRESENT_H_COUNT)
list(LENGTH AV_MISSING_SOURCES AV_MISSING_COUNT)

if(AV_MISSING_SOURCES)
    string(REPLACE ";" "\n      " _av_missing_lines "${AV_MISSING_SOURCES}")
    message(WARNING
        "AssetViewer: 以下源文件尚未落地（并行开发中，已跳过，配置继续）：\n"
        "      ${_av_missing_lines}\n"
        "  入口缺失的可执行目标会被自动跳过；公共头缺失时可用占位头编译（AV_STUB_MISSING_HEADERS）。")
endif()

# 手写的「额外源文件」？src/*.cpp 或 tests/*.cpp 里有不认识的（例如新加了 foo.cpp）
file(GLOB _av_src_glob RELATIVE "${CMAKE_CURRENT_SOURCE_DIR}" "${AV_SRC_DIR}/*.cpp")
file(GLOB _av_test_glob RELATIVE "${CMAKE_CURRENT_SOURCE_DIR}" "${AV_TESTS_DIR}/*.cpp")
set(_av_known "")
foreach(_rel IN LISTS AV_EXPECTED_SOURCES)
    if(_rel MATCHES "\\.cpp$")
        list(APPEND _av_known "${_rel}")
    endif()
endforeach()
set(AV_EXTRA_SOURCES "")
foreach(_f IN LISTS _av_src_glob _av_test_glob)
    list(FIND _av_known "${_f}" _idx)
    if(_idx EQUAL -1 AND NOT _f MATCHES "^(src|tests)/$")
        list(APPEND AV_EXTRA_SOURCES "${_f}")
    endif()
endforeach()
if(AV_EXTRA_SOURCES)
    string(REPLACE ";" "\n      " _av_extra_lines "${AV_EXTRA_SOURCES}")
    message(WARNING
        "AssetViewer: 发现清单外的 .cpp（已**未**自动加入构建，避免与已知模块重复符号）：\n"
        "      ${_av_extra_lines}\n"
        "  确属新模块请把它登记到 CMakeLists.txt 的 AV_EXPECTED_SOURCES / 对应目标里。")
endif()

message(STATUS "AssetViewer: src .cpp 就位 ${AV_PRESENT_CPP_COUNT} 个；头文件就位 ${AV_PRESENT_H_COUNT} 个；"
               "缺失 ${AV_MISSING_COUNT} 个；ref/*.py 不参与编译。")

# -----------------------------------------------------------------------------
# 4. 缺失公共头 → 空占位头（build/generated/stub_headers，最后搜索）
# -----------------------------------------------------------------------------
set(AV_STUB_INCLUDE_DIR "${AV_GEN_DIR}/stub_headers")
file(REMOVE_RECURSE "${AV_STUB_INCLUDE_DIR}")
file(MAKE_DIRECTORY "${AV_STUB_INCLUDE_DIR}")
set(AV_STUBBED_HEADERS "")
if(AV_STUB_MISSING_HEADERS)
    foreach(_hdr IN LISTS AV_STUBBABLE_HEADERS)
        if(NOT EXISTS "${AV_SRC_DIR}/${_hdr}")
            file(WRITE "${AV_STUB_INCLUDE_DIR}/${_hdr}"
"// 自动生成的空占位头：${_hdr} 尚未由协作者落地。
// 生成者：tools/asset_viewer/CMakeLists.txt（AV_STUB_MISSING_HEADERS=ON）
// 真实头文件一旦出现，src/ 会先于本目录被搜索，本文件自动失效。
// 此阶段编译通过不代表产出可运行：引用其中的符号会在链接期报 LNK2019。
#pragma once
")
            list(APPEND AV_STUBBED_HEADERS "${_hdr}")
        endif()
    endforeach()
endif()
if(AV_STUBBED_HEADERS)
    string(REPLACE ";" " " _av_stub_names "${AV_STUBBED_HEADERS}")
    message(STATUS "AssetViewer: 已生成占位头（尚未落地）：${_av_stub_names}")
else()
    message(STATUS "AssetViewer: 公共头齐备，未生成占位头。")
endif()
if(NOT EXISTS "${AV_SRC_DIR}/av_util.h" AND EXISTS "${AV_SRC_DIR}/av_util.cpp")
    message(WARNING "AssetViewer: src/av_util.cpp 存在但 src/av_util.h 缺失 —— 该文件无法编译，请让 UI 同事补头。")
endif()

# -----------------------------------------------------------------------------
# 5. 公共编译选项
# -----------------------------------------------------------------------------
if(MSVC)
    # /utf-8        源码与执行字符集同为 UTF-8（中文注释、L"中文" 安全）
    # /permissive-  标准一致性，尽早暴露协作者代码里的非标写法
    # /Zc:__cplusplus 让 __cplusplus 反映 /std:c++17
    # /MP           多进程并行编译（VS / MSBuild 生成器有效，Ninja 自动忽略）
    set(AV_MSVC_OPTIONS /utf-8 /W4 /permissive- /Zc:__cplusplus /EHsc /nologo /MP)
    # UNICODE/_UNICODE：全项目统一走 W 版 Win32 API（LoadCursorW/LoadIconW + IDC_ARROW 等宏）。
    # 不定义时 IDC_ARROW 会展开成 MAKEINTRESOURCEA（LPSTR），传给显式 *W 函数直接编译失败。
    # NOMINMAX / WIN32_LEAN_AND_MEAN：避免 min/max 宏与 <algorithm> 打架、剪掉 winsock 等噪音头。
    set(AV_MSVC_DEFINITIONS UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
    if(AV_WARNINGS_AS_ERRORS)
        list(APPEND AV_MSVC_OPTIONS /WX)
    endif()
endif()

# 把公共选项挂到一个目标上（编译选项 / 包含目录 / Release-Debug 分化 / CRT / 输出目录）
function(av_apply_common_options target)
    target_compile_features(${target} PUBLIC cxx_std_17)
    # 顺序即搜索优先级：src/ 优先，占位头目录垫底
    target_include_directories(${target} PUBLIC "${AV_SRC_DIR}")
    if(AV_STUBBED_HEADERS)
        target_include_directories(${target} PUBLIC "${AV_STUB_INCLUDE_DIR}")
    endif()

    if(MSVC)
        target_compile_options(${target} PRIVATE ${AV_MSVC_OPTIONS})
        target_compile_definitions(${target} PRIVATE ${AV_MSVC_DEFINITIONS})
        target_compile_definitions(${target} PRIVATE "AV_HAVE_LOADERS=${AV_LOADERS_DEFINE}")
        target_compile_options(${target} PRIVATE
            "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebInfo>,$<CONFIG:MinSizeRel>>:/O2;/Gy;/Oi;/DNDEBUG>"
            "$<$<CONFIG:Debug>:/Od;/Zi;/RTC1;/D_DEBUG>")
        target_compile_definitions(${target} PRIVATE
            $<$<NOT:$<CONFIG:Debug>>:NDEBUG>)
        target_link_options(${target} PRIVATE
            "$<$<NOT:$<CONFIG:Debug>>:/OPT:REF;/OPT:ICF>"
            "$<$<OR:$<CONFIG:Debug>,$<CONFIG:RelWithDebInfo>>:/DEBUG>")
        if(AV_STATIC_RUNTIME)
            set_target_properties(${target} PROPERTIES
                MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
        else()
            set_target_properties(${target} PROPERTIES
                MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
        endif()
    else()
        target_compile_options(${target} PRIVATE
            "$<$<NOT:$<CONFIG:Debug>>:-O2;-DNDEBUG>"
            "$<$<CONFIG:Debug>:-g;-O0>")
    endif()

    foreach(_cfg "" _DEBUG _RELEASE _RELWITHDEBINFO _MINSIZEREL)
        set_target_properties(${target} PROPERTIES
            "RUNTIME_OUTPUT_DIRECTORY${_cfg}" "${AV_OUT_DIR}"
            "ARCHIVE_OUTPUT_DIRECTORY${_cfg}" "${AV_OUT_DIR}")
    endforeach()
endfunction()

# 创建一个可执行目标；入口缺失则创建同名自定义目标占位（不报错）
function(av_add_exe)
    if("${ARGN}" MATCHES "MAIN;([^;]+)" )
        set(_av_main_file "${CMAKE_MATCH_1}")
    else()
        set(_av_main_file "")
    endif()
    set(_av_gui FALSE)
    if("${ARGN}" MATCHES "(^|;)WIN32(;|$)")
        set(_av_gui TRUE)
    endif()
    set(_target "${ARGV0}")
    set(_main "${CMAKE_CURRENT_SOURCE_DIR}/${_av_main_file}")
    if(NOT EXISTS "${_main}")
        message(WARNING "AssetViewer: 跳过目标 ${_target} —— 入口文件 ${_av_main_file} 尚未落地。")
        add_custom_target(${_target} COMMENT "跳过 ${_target}：入口源文件 ${_av_main_file} 尚未落地。")
        return()
    endif()
    # 入口探测：WinMain/wWinMain → WIN32 子系统；只有 main → CONSOLE 子系统。
    # （WIN32 子系统下 main/mainCRTStartup 依然可用，但用 CONSOLE 才能直接看到 stdout 输出）
    file(READ "${_main}" _av_entry_src)
    set(_av_has_gui_entry FALSE)
    if(_av_entry_src MATCHES "(wWinMain|WinMain)[ \t]*\\(")
        set(_av_has_gui_entry TRUE)
    endif()
    message(STATUS "AssetViewer: 入口判定 ${_target} ← ${_av_main_file}（含 WinMain: ${_av_has_gui_entry}）")
    if(_av_gui AND _av_has_gui_entry)
        add_executable(${_target} WIN32 "${_main}")
    elseif(_av_gui)
        message(WARNING "AssetViewer: ${_av_main_file} 里没有 WinMain/wWinMain，按 CONSOLE 子系统构建 ${_target}。")
        add_executable(${_target} "${_main}")
    else()
        add_executable(${_target} "${_main}")
    endif()
    av_apply_common_options(${_target})
    # VS 调试器工作目录 = 工程根（能直接扫到 素材/）
    set_target_properties(${_target} PROPERTIES
        VS_DEBUGGER_WORKING_DIRECTORY "${AV_PROJECT_ROOT}")
endfunction()

# 系统库全部来自 Windows SDK（无需 vcpkg / 无第三方依赖）
set(AV_WIN32_LIBS
    d3d11          # D3D11 设备 / 纹理 / 预览渲染
    dxgi           # 交换链 / 适配器枚举
    d2d1           # Direct2D（UI 绘制）
    dwrite         # DirectWrite（文字）
    windowscodecs  # WIC（PNG/JPG/BMP 编解码 + PNG 写出）
    d3dcompiler    # 运行时编译 HLSL（D3DCompile）
    ole32          # COM（CoInitializeEx / CoCreateInstance）
    uuid           # GUID 定义
    shell32        # 图标 / 路径杂项
    shlwapi        # Path* 系列（宽字符路径处理）
    comctl32       # ListView / StatusBar 等通用控件
    comdlg32       # 打开文件对话框（GetOpenFileName）
    user32
    gdi32
    advapi32
    oleaut32)

# -----------------------------------------------------------------------------
# 6. 目标划分
#    av_core      STATIC : 解析 / 贴图 / 缩略图渲染（与 UI 无关；loader_smoke、thumb_sheet 直接链接）
#    av_ui        STATIC : 应用层（扫描 / 后台线程 / 列表 / 预览渲染器）
#    asset_viewer WIN32  : 主程序，入口 src/app_main.cpp
#    loader_smoke / ui_selftest / thumb_sheet : 各自有 main 的独立可执行（AV_BUILD_TESTS）
# -----------------------------------------------------------------------------
set(_av_stub_cpp "${AV_GEN_DIR}/stub_empty.cpp")
if(NOT EXISTS "${_av_stub_cpp}")
    file(WRITE "${_av_stub_cpp}" "// 自动生成：目标暂时没有源文件时的占位翻译单元。\n")
endif()

# ---- av_core -----------------------------------------------------------------
if(AV_FOUNDATION_CPP_PRESENT)
    add_library(av_core STATIC ${AV_FOUNDATION_CPP_PRESENT})
else()
    add_library(av_core STATIC "${_av_stub_cpp}")
    message(STATUS "AssetViewer: av_core 为空库（解析/渲染源文件尚未落地），使用占位翻译单元。")
endif()
av_apply_common_options(av_core)
if(MSVC)
    target_compile_options(av_core PRIVATE /bigobj)   # glb/fbx 生成代码较大，避免 C1128
endif()
target_link_libraries(av_core PUBLIC ${AV_WIN32_LIBS})

# ---- av_ui -------------------------------------------------------------------
if(AV_UI_CPP_PRESENT)
    # app_main.cpp 已由 asset_viewer 自己编译，不放进库
    set(_av_ui_lib_srcs "")
    foreach(_f IN LISTS AV_UI_CPP_PRESENT)
        if(NOT _f STREQUAL "${AV_SRC_DIR}/app_main.cpp")
            list(APPEND _av_ui_lib_srcs "${_f}")
        endif()
    endforeach()
    if(_av_ui_lib_srcs)
        add_library(av_ui STATIC ${_av_ui_lib_srcs})
    else()
        add_library(av_ui STATIC "${_av_stub_cpp}")
    endif()
else()
    add_library(av_ui STATIC "${_av_stub_cpp}")
    message(STATUS "AssetViewer: av_ui 为空库（UI 源文件尚未落地），使用占位翻译单元。")
endif()
av_apply_common_options(av_ui)
target_link_libraries(av_ui PUBLIC av_core ${AV_WIN32_LIBS})

# ---- asset_viewer 主程序（WIN32 子系统）----------------------------------------
av_add_exe(asset_viewer WIN32 MAIN "src/app_main.cpp")
if(TARGET asset_viewer)
    target_link_libraries(asset_viewer PRIVATE av_ui)
    # 可选的图标 / 清单：resources/ 下存在才挂
    set(_av_rc       "${CMAKE_CURRENT_SOURCE_DIR}/resources/asset_viewer.rc")
    set(_av_ico      "${CMAKE_CURRENT_SOURCE_DIR}/resources/asset_viewer.ico")
    set(_av_manifest "${CMAKE_CURRENT_SOURCE_DIR}/resources/asset_viewer.manifest")
    if(MSVC)
        if(EXISTS "${_av_rc}")
            target_sources(asset_viewer PRIVATE "${_av_rc}")
            message(STATUS "AssetViewer: 使用图标资源 resources/asset_viewer.rc")
        elseif(EXISTS "${_av_ico}")
            set(_gen_rc "${AV_GEN_DIR}/asset_viewer_icon.rc")
            file(WRITE "${_gen_rc}" "IDI_ASSET_VIEWER ICON \"${_av_ico}\"\n")
            target_sources(asset_viewer PRIVATE "${_gen_rc}")
            message(STATUS "AssetViewer: 使用图标 resources/asset_viewer.ico")
        endif()
        if(EXISTS "${_av_manifest}")
            target_sources(asset_viewer PRIVATE "${_av_manifest}")
            message(STATUS "AssetViewer: 使用清单 resources/asset_viewer.manifest")
        endif()
    endif()
endif()

# ---- 测试目标 ----------------------------------------------------------------
if(AV_BUILD_TESTS)
    # loader_smoke：控制台，直接测 loader 层
    av_add_exe(loader_smoke MAIN "tests/loader_smoke.cpp")
    if(TARGET loader_smoke)
        target_link_libraries(loader_smoke PRIVATE av_core ${AV_WIN32_LIBS})
    endif()

    # ui_selftest：控制台可执行（winui-list 约定：ui_selftest.exe [素材目录] --out=<输出目录>）
    # 它能自己创建窗口 / D3D 设备做自测，但进程本身仍是 CONSOLE，方便直接看输出与日志。
    av_add_exe(ui_selftest MAIN "tests/ui_selftest.cpp")
    if(TARGET ui_selftest)
        target_link_libraries(ui_selftest PRIVATE av_ui ${AV_WIN32_LIBS})
    endif()

    # thumb_sheet：控制台，批量出缩略图拼图（只用 model_data.h + wic_util.h，不依赖 UI 层）
    av_add_exe(thumb_sheet MAIN "tests/thumb_sheet.cpp")
    if(TARGET thumb_sheet)
        target_link_libraries(thumb_sheet PRIVATE av_core ${AV_WIN32_LIBS})
    endif()

    enable_testing()
    foreach(_t loader_smoke ui_selftest thumb_sheet)
        if(TARGET ${_t})
            get_target_property(_t_type ${_t} TYPE)
            if(_t_type STREQUAL "EXECUTABLE")
                if(_t STREQUAL "ui_selftest")
                    add_test(NAME ${_t} COMMAND ${_t} "${AV_ASSET_ROOT}" "--out=${CMAKE_BINARY_DIR}/_uic"
                             WORKING_DIRECTORY "${AV_PROJECT_ROOT}")
                else()
                    add_test(NAME ${_t} COMMAND ${_t} WORKING_DIRECTORY "${AV_PROJECT_ROOT}")
                endif()
            endif()
        endif()
    endforeach()

    # ---- 验收便捷目标：av_sheet ------------------------------------------------
    #   cmake --build build --target av_sheet
    #   => 跑 <build>/out/thumb_sheet.exe  <素材根>  <build>/_sheet  [AV_SHEET_ARGS]
    # 素材根自动推导（工程根/素材），可用 -DAV_ASSET_ROOT=<dir> 覆盖。
    set(AV_SHEET_OUT_DIR "${CMAKE_BINARY_DIR}/_sheet" CACHE PATH "av_sheet 拼版产物目录")
    set(AV_SHEET_ARGS    "" CACHE STRING "av_sheet 透传给 thumb_sheet 的额外参数，例如 --limit=40")
    set(_av_asset_root "${AV_PROJECT_ROOT}")
    foreach(_cand IN ITEMS "${AV_PROJECT_ROOT}/素材" "${AV_PROJECT_ROOT}/assets")
        if(IS_DIRECTORY "${_cand}")
            set(_av_asset_root "${_cand}")
            break()
        endif()
    endforeach()
    set(AV_ASSET_ROOT "${_av_asset_root}" CACHE PATH "素材根目录（av_sheet 使用；运行时也可由参数传入）")

    set(_av_ts_type "NOTFOUND")
    if(TARGET thumb_sheet)
        get_target_property(_av_ts_type thumb_sheet TYPE)
    endif()
    if(_av_ts_type STREQUAL "EXECUTABLE")
        add_custom_target(av_sheet
            # 注意：不清空 AV_SHEET_OUT_DIR —— Lead 依赖其中的 index.txt 做统计
            COMMAND "${CMAKE_COMMAND}" -E make_directory "${AV_SHEET_OUT_DIR}"
            COMMAND "$<TARGET_FILE:thumb_sheet>" "${AV_ASSET_ROOT}" "${AV_SHEET_OUT_DIR}" ${AV_SHEET_ARGS}
            WORKING_DIRECTORY "${AV_PROJECT_ROOT}"
            DEPENDS thumb_sheet
            USES_TERMINAL
            COMMENT "av_sheet: 生成缩略图拼版 -> ${AV_SHEET_OUT_DIR}")
        message(STATUS "AssetViewer: 自定义目标 av_sheet 已启用（素材根 ${AV_ASSET_ROOT}）")
    else()
        add_custom_target(av_sheet
            COMMAND "${CMAKE_COMMAND}" -E echo "跳过 av_sheet：tests/thumb_sheet.cpp 尚未落地。"
            COMMENT "av_sheet (skipped)")
        message(STATUS "AssetViewer: av_sheet 暂不可用（等 tests/thumb_sheet.cpp 落地）。")
    endif()
else()
    message(STATUS "AssetViewer: AV_BUILD_TESTS=OFF，跳过测试目标。")
endif()

# ---- 可选：工具链探针（默认关；源文件在 build/_probe，属临时目录）--------------------
if(AV_BUILD_PROBE)
    set(_probe_dir "${CMAKE_CURRENT_SOURCE_DIR}/build/_probe")
    foreach(_probe hello_console wic_cn)
        if(EXISTS "${_probe_dir}/${_probe}.cpp")
            add_executable(av_probe_${_probe} "${_probe_dir}/${_probe}.cpp")
            av_apply_common_options(av_probe_${_probe})
            target_link_libraries(av_probe_${_probe} PRIVATE ${AV_WIN32_LIBS})
            if(MSVC)
                target_compile_options(av_probe_${_probe} PRIVATE /WX)
            endif()
        endif()
    endforeach()
    message(STATUS "AssetViewer: AV_BUILD_PROBE=ON，已加入 _probe 探针目标。")
endif()

# -----------------------------------------------------------------------------
# 7. 安装（可选：cmake --install ... --prefix <dir>）
# -----------------------------------------------------------------------------
include(GNUInstallDirs)
if(TARGET asset_viewer)
    get_target_property(_av_av_type asset_viewer TYPE)
    if(_av_av_type STREQUAL "EXECUTABLE")
        install(TARGETS asset_viewer RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}")
    else()
        message(STATUS "AssetViewer: asset_viewer 尚未构建，install 无内容可装（等 app_main.cpp 落地）。")
    endif()
else()
    message(STATUS "AssetViewer: asset_viewer 尚未构建，install 无内容可装（等 app_main.cpp 落地）。")
endif()
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/resources")
    install(DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/resources/"
        DESTINATION "${CMAKE_INSTALL_DATADIR}/AssetViewer"
        FILES_MATCHING PATTERN "*.ico" PATTERN "*.png" PATTERN "*.manifest")
endif()

# -----------------------------------------------------------------------------
# 8. 配置摘要
# -----------------------------------------------------------------------------
message(STATUS "-----------------------------------------------------------")
message(STATUS " AssetViewer 构建摘要")
message(STATUS "   生成器      : ${CMAKE_GENERATOR}")
if(_av_multi_config)
    message(STATUS "   构建类型    : 多配置（--build 时传 --config Debug|Release）")
else()
    message(STATUS "   构建类型    : ${CMAKE_BUILD_TYPE}")
endif()
message(STATUS "   编译器      : ${CMAKE_CXX_COMPILER_ID} ${CMAKE_CXX_COMPILER_VERSION}")
if(MSVC)
    if(AV_STATIC_RUNTIME)
        message(STATUS "   运行库      : /MT（静态）")
    else()
        message(STATUS "   运行库      : /MD（动态，默认）")
    endif()
endif()
message(STATUS "   外部依赖    : 无（仅 Windows SDK）")
message(STATUS "   产物目录    : ${AV_OUT_DIR}")
message(STATUS "   工程根(cwd) : ${AV_PROJECT_ROOT}")
message(STATUS "   测试目标    : ${AV_BUILD_TESTS}")
message(STATUS "   素材根      : ${AV_ASSET_ROOT}")
message(STATUS "   占位头      : ${AV_STUB_MISSING_HEADERS}")
message(STATUS "   加载器      : AV_HAVE_LOADERS=${AV_LOADERS_DEFINE}")
message(STATUS "-----------------------------------------------------------")
