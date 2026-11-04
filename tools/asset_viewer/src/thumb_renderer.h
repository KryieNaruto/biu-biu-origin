// thumb_renderer.h —— 缩略图渲染（CPU 软件光栅）
// OWNER: glb-loader
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "model_data.h"

namespace av {

// 渲染缩略图。
// outPixels: size*size 个 32 位像素，内存顺序 B,G,R,A（Windows DIB / D3D11 R8G8B8A8 / D2D B8G8R8A8 通用）
// 失败时也输出纯色占位图（不返回空），仅返回 false。
bool RenderThumbnail(const ModelData& model, const ThumbOptions& opt,
                     std::vector<uint32_t>* outPixels);

} // namespace av
