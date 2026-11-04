// wic_util.h —— PNG 读写 + 图片解码（WIC）+ UTF-8 路径工具
// OWNER: glb-loader
// 只用 Windows 自带 WIC（windowscodecs.lib），不依赖任何第三方图像库。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace av {
namespace img {

// ---------------------------------------------------------------------------
// 图片位图：RGBA8，行从上到下（与 PNG 行序一致），每像素 4 字节 R,G,B,A
// ---------------------------------------------------------------------------
struct Bitmap {
    int width  = 0;
    int height = 0;
    std::vector<unsigned char> rgba;   // width*height*4

    bool valid() const { return width > 0 && height > 0 && (int)rgba.size() >= width * height * 4; }
    void reset() { width = height = 0; rgba.clear(); }
};

// ---------------------------------------------------------------------------
// 路径：UTF-8 std::string <-> UTF-16 std::wstring（非法序列替换为 U+FFFD）
// ---------------------------------------------------------------------------
std::wstring Wide(const std::string& utf8);
std::string  Utf8(const std::wstring& wide);
// 读整个文件；失败返回 false
bool ReadFileBytes(const std::wstring& path, std::vector<unsigned char>* out);
bool ReadFileBytesUtf8(const std::string& utf8Path, std::vector<unsigned char>* out);
// 递归创建目录 / 写整个文件（失败返回 false）
bool EnsureDir(const std::wstring& dir);
bool WriteFileBytes(const std::wstring& path, const std::vector<unsigned char>& bytes);

// ---------------------------------------------------------------------------
// 内存解码：从内存字节解码任意 WIC 认识的格式（PNG/JPEG/BMP/GIF/TIFF）为 RGBA8
// 失败返回 false。线程安全（内部每次 CoInitializeEx 幂等调用）。
// ---------------------------------------------------------------------------
bool DecodeImageMemory(const unsigned char* data, size_t size, Bitmap* out);
bool DecodeImageFile(const std::wstring& path, Bitmap* out);

// ---------------------------------------------------------------------------
// PNG 编码（RGBA8 或 BGRA8 输入）
// ---------------------------------------------------------------------------
bool EncodePng(const Bitmap& src, std::vector<unsigned char>* outPng);
bool SavePng(const std::wstring& path, const Bitmap& src);
bool SavePngUtf8(const std::string& utf8Path, const Bitmap& src);
// 从任意 32 位像素缓冲（BGRA 内存序）存 PNG；pixels 为 size*size
bool SavePngFromBgra(const std::wstring& path, const uint32_t* pixels, int w, int h);

// ---------------------------------------------------------------------------
// 内部：WIC 工厂懒初始化（每线程 COM 初始化），失败返回 false
// ---------------------------------------------------------------------------
bool EnsureWic();

} // namespace img
} // namespace av
