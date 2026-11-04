# ui_preview.cmake —— winui-list 的临时自测构建脚本（不修改根 CMakeLists.txt）
#
# 用法（在 VS 环境里，或在命令行直接调用 cmake）：
#   cmake -S tools/asset_viewer -B tools/asset_viewer/build/_uipreview -G Ninja \
#         -DCMAKE_TOOLCHAIN_FILE=... -C tools/asset_viewer/src/ui_preview.cmake
#
# 更简单的方式：直接作为顶层 CMakeLists 使用
#   cmake -S tools/asset_viewer/src -B tools/asset_viewer/build/_uipreview -G Ninja
# （因为文件名不是 CMakeLists.txt，需要 -C 包含：-C ui_preview.cmake）
#
# 它独立产出两个目标：
#   asset_viewer_ui_preview  —— WIN32 预览程序（AV_HAVE_LOADERS 可配）
#   ui_selftest_ui_preview   —— CONSOLE 无头自测
#
# 环境变量 AV_HAVE_LOADERS=1 时会额外链接 glb_loader/fbx_loader/thumb_renderer/
# anim_sampler 的目标（目标名以 Lead 的 CMakeLists 为准，缺失则自动回退到 0）。

cmake_minimum_required(VERSION 3.20)

# 支持两种用法：
#   a) 作为顶层脚本：cmake -S <dir with CMakeLists that include() this> -B build
#   b) 被 include 到别的 CMakeLists 中
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_LIST_DIR OR NOT DEFINED PROJECT_NAME)
  project(AssetViewerUIPreview CXX)
else()
  project(AssetViewerUIPreview CXX)
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

get_filename_component(AV_SRC_DIR "${CMAKE_CURRENT_LIST_DIR}" ABSOLUTE)
get_filename_component(AV_VIEWER_DIR "${AV_SRC_DIR}/.." ABSOLUTE)
get_filename_component(AV_ROOT_DIR "${AV_VIEWER_DIR}/../.." ABSOLUTE)
set(AV_TESTS_DIR "${AV_VIEWER_DIR}/tests")
set(AV_BUILD_DIR "${AV_VIEWER_DIR}/build")

set(AV_HAVE_LOADERS 0 CACHE STRING "1 = 链接 glb-loader 的解析/缩略图/动画模块")
set(AV_UI_SOURCES
  "${AV_SRC_DIR}/av_util.cpp"
  "${AV_SRC_DIR}/asset_scanner.cpp"
  "${AV_SRC_DIR}/scan_worker.cpp"
  "${AV_SRC_DIR}/d3d_renderer.cpp"
  "${AV_SRC_DIR}/viewer_app.cpp"
)

if(MSVC)
  add_compile_options(/utf-8 /W4 /permissive- /MP /wd4819)
  add_compile_definitions(UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

add_library(av_ui_preview STATIC ${AV_UI_SOURCES})
target_include_directories(av_ui_preview PUBLIC "${AV_SRC_DIR}")
target_compile_definitions(av_ui_preview PUBLIC AV_HAVE_LOADERS=${AV_HAVE_LOADERS})
target_link_libraries(av_ui_preview PUBLIC
  d3d11 dxgi d3dcompiler d2d1 dwrite windowscodecs shell32 ole32 comdlg32 user32 gdi32)

if(AV_HAVE_LOADERS)
  # 依赖 Lead/glb-loader 提供的目标；若不存在会在配置期报错，便于发现问题
  target_link_libraries(av_ui_preview PUBLIC av_loader av_thumb av_anim)
endif()

add_executable(asset_viewer_ui_preview WIN32 "${AV_SRC_DIR}/app_main.cpp")
target_link_libraries(asset_viewer_ui_preview PRIVATE av_ui_preview)

add_executable(ui_selftest_ui_preview "${AV_TESTS_DIR}/ui_selftest.cpp")
target_link_libraries(ui_selftest_ui_preview PRIVATE av_ui_preview)

set_target_properties(asset_viewer_ui_preview ui_selftest_ui_preview PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${AV_BUILD_DIR}"
  RUNTIME_OUTPUT_DIRECTORY_DEBUG "${AV_BUILD_DIR}"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${AV_BUILD_DIR}")
