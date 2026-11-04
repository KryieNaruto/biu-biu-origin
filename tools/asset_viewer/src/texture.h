// texture.h —— 贴图解码与预览纹理烘焙
// OWNER: glb-loader
// 约定（SHARED_SPEC §6）：glb 内嵌图可能是 512~2048 图集；本模块解码一次为 RGBA8，
// 立刻双线性多级下采样烘焙成 32x32（最长边 <=32）预览图写进 TextureInfo.preview，
// 原始大图随即释放。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "model_data.h"
#include "wic_util.h"

namespace av {
namespace img { struct Bitmap; }   // 前置声明（定义见 wic_util.h）

namespace tex {

// 预览图最长边（px）
constexpr int kPreviewMaxEdge = 32;

// ---------------------------------------------------------------------------
// 从内存字节解码（PNG/JPEG/...）并烘焙预览，填充 TextureInfo。
// name 用于日志/调试。返回是否成功。
// ---------------------------------------------------------------------------
bool BakeFromMemory(const std::string& name, const unsigned char* data, size_t size,
                    TextureInfo* out);

// 从文件烘焙（extern glTF 图 / dds 等；dds 不支持时返回 false 且 ok=false）
bool BakeFromFile(const std::string& name, const std::wstring& absPath, TextureInfo* out);

// 从已解码的 Bitmap 烘焙预览（不改动 src）。失败返回 false。
bool BakeFromBitmap(const img::Bitmap& src, TextureInfo* out);

// ---------------------------------------------------------------------------
// 双线性采样：uv 为 glTF 约定（原点左下）；内部预览按行从上到下存储，
// 因此 v=0 取最后一行。返回 RGBA float [0,1]。
// ---------------------------------------------------------------------------
void SamplePreview(const TextureInfo& t, float u, float v, float outRGBA[4]);

// 采样像素缓冲（raw RGBA8，行从上到下），uv 同 glTF 约定
void SampleRgba(const unsigned char* rgba, int w, int h, float u, float v, float outRGBA[4]);

// 材质基础色的兜底色：按材质名字哈希出一个舒服的低饱和色（无贴图时用）
void FallbackColorForName(const std::string& name, float outRGB[3]);

} // namespace tex
} // namespace av
