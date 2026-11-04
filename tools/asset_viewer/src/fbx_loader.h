// fbx_loader.h —— FBX 二进制解析入口
// OWNER: glb-loader
#pragma once

#include <string>

#include "model_data.h"

namespace av {

// 解析 FBX 二进制（7200 / 7400 / 7500 均支持，ASCII FBX 不支持，返回 false）。
// utf8Path: UTF-8 路径（中文路径必须能打开）。任何畸形数据都不抛异常。
bool LoadFBX(const std::string& utf8Path, ModelData* out);

} // namespace av
