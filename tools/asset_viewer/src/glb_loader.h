// glb_loader.h —— GLB / glTF 解析入口
// OWNER: glb-loader
#pragma once

#include <cstdint>
#include <string>

#include "model_data.h"

namespace av {

// 解析 .glb（也接受 .gltf + 外置/内嵌 buffer）。
// utf8Path: UTF-8 路径（中文路径必须能打开）。
// 返回 true 表示 ok=true。任何畸形数据都不抛异常：失败时 out->ok=false 且 out->error 非空，
// 并保留已解析出的部分数据（可渲染的半空 ModelData）。
bool LoadGLB(const std::string& utf8Path, ModelData* out);

} // namespace av
