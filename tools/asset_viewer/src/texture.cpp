// texture.cpp —— 贴图解码 + 预览纹理烘焙（32px 盒式多级下采样）
// OWNER: glb-loader
#include "texture.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <utility>

namespace av {
namespace tex {

namespace {

inline float Clamp01(float v) { return v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v); }

// 2x2 平均下采样（硬件双线性的近似，速度快、不产生棋盘噪点）
void Downsample2x(const unsigned char* src, int sw, int sh,
                  unsigned char* dst, int dw, int dh) {
    for (int y = 0; y < dh; ++y) {
        int sy0 = std::min(sh - 1, y * 2);
        int sy1 = std::min(sh - 1, y * 2 + 1);
        const unsigned char* r0 = src + (size_t)sy0 * sw * 4;
        const unsigned char* r1 = src + (size_t)sy1 * sw * 4;
        unsigned char* d = dst + (size_t)y * dw * 4;
        for (int x = 0; x < dw; ++x) {
            int sx0 = std::min(sw - 1, x * 2);
            int sx1 = std::min(sw - 1, x * 2 + 1);
            for (int c = 0; c < 4; ++c) {
                int s = (int)r0[sx0 * 4 + c] + r0[sx1 * 4 + c] + r1[sx0 * 4 + c] + r1[sx1 * 4 + c];
                d[x * 4 + c] = (unsigned char)((s + 2) >> 2);
            }
        }
    }
}

} // namespace

bool BakeFromBitmap(const img::Bitmap& src, TextureInfo* out) {
    if (!out) return false;
    out->preview.clear();
    out->previewW = 0;
    out->previewH = 0;
    out->width = src.width;
    out->height = src.height;
    out->baked = false;
    if (!src.valid()) {
        out->ok = false;
        return false;
    }
    out->ok = true;

    // 目标尺寸：最长边 32，保持长宽比；非常窄/扁的图至少 1px
    int W = src.width, H = src.height;
    int dw, dh;
    if (W >= H) {
        dw = std::min(kPreviewMaxEdge, W);
        dh = std::max(1, (int)std::lround((double)H * dw / (double)W));
    } else {
        dh = std::min(kPreviewMaxEdge, H);
        dw = std::max(1, (int)std::lround((double)W * dh / (double)H));
    }
    dw = std::min(dw, kPreviewMaxEdge);
    dh = std::min(dh, kPreviewMaxEdge);

    // 多级 2x 下采样到接近目标尺寸，再单步缩放到目标（避免直接大跨度采样时的走样）
    img::Bitmap cur;
    cur.width = W;
    cur.height = H;
    cur.rgba = src.rgba;
    while (cur.width > dw * 2 || cur.height > dh * 2) {
        int nw = std::max(dw, (cur.width + 1) / 2);
        int nh = std::max(dh, (cur.height + 1) / 2);
        img::Bitmap next;
        next.width = nw;
        next.height = nh;
        next.rgba.resize((size_t)nw * nh * 4);
        Downsample2x(cur.rgba.data(), cur.width, cur.height, next.rgba.data(), nw, nh);
        cur = std::move(next);
    }

    out->previewW = dw;
    out->previewH = dh;
    out->preview.resize((size_t)dw * dh * 4);
    if (cur.width == dw && cur.height == dh) {
        out->preview = std::move(cur.rgba);
    } else {
        // 面积加权盒式重采样：把 cur 像素按覆盖比例累加到 dw x dh
        std::vector<float> acc((size_t)dw * dh * 4, 0.0f);
        std::vector<float> wsum((size_t)dw * dh, 0.0f);
        float fx = (float)cur.width / (float)dw;
        float fy = (float)cur.height / (float)dh;
        for (int y = 0; y < cur.height; ++y) {
            int dy = std::min(dh - 1, (int)((y + 0.5f) / fy));
            for (int x = 0; x < cur.width; ++x) {
                int dx = std::min(dw - 1, (int)((x + 0.5f) / fx));
                const unsigned char* s = cur.rgba.data() + ((size_t)y * cur.width + x) * 4;
                float* d = acc.data() + ((size_t)dy * dw + dx) * 4;
                for (int c = 0; c < 4; ++c) d[c] += (float)s[c];
                wsum[(size_t)dy * dw + dx] += 1.0f;
            }
        }
        for (size_t i = 0; i < (size_t)dw * dh; ++i) {
            float w = wsum[i] > 0.0f ? wsum[i] : 1.0f;
            for (int c = 0; c < 4; ++c) {
                float v = acc[i * 4 + c] / w;
                out->preview[i * 4 + c] = (unsigned char)(Clamp01(v / 255.0f) * 255.0f + 0.5f);
            }
        }
    }
    out->baked = true;
    return true;
}

bool BakeFromMemory(const std::string& name, const unsigned char* data, size_t size,
                    TextureInfo* out) {
    if (!out) return false;
    out->name = name;
    out->preview.clear();
    if (!data || size == 0) {
        out->ok = false;
        out->baked = false;
        return false;
    }
    img::Bitmap bmp;
    if (!img::DecodeImageMemory(data, size, &bmp)) {
        out->ok = false;
        out->baked = false;
        return false;
    }
    return BakeFromBitmap(bmp, out);
}

bool BakeFromFile(const std::string& name, const std::wstring& absPath, TextureInfo* out) {
    if (!out) return false;
    out->name = name;
    out->preview.clear();
    img::Bitmap bmp;
    if (!img::DecodeImageFile(absPath, &bmp)) {
        out->ok = false;
        out->baked = false;
        return false;
    }
    return BakeFromBitmap(bmp, out);
}

void SampleRgba(const unsigned char* rgba, int w, int h, float u, float v, float outRGBA[4]) {
    if (!rgba || w <= 0 || h <= 0) {
        outRGBA[0] = outRGBA[1] = outRGBA[2] = 0.8f;
        outRGBA[3] = 1.0f;
        return;
    }
    // glTF UV：原点左下、v 向上；缓冲行从上到下 → 翻转 v
    float fu = u, fv = 1.0f - v;
    // 平铺（部分 FBX2glTF 导出 UV 会超出 [0,1]）
    fu = fu - std::floor(fu);
    fv = fv - std::floor(fv);
    float px = fu * (float)w - 0.5f;
    float py = fv * (float)h - 0.5f;
    int x0 = (int)std::floor(px), y0 = (int)std::floor(py);
    float ax = px - (float)x0, ay = py - (float)y0;
    int x1 = x0 + 1, y1 = y0 + 1;
    if (x0 < 0) x0 += w;
    if (y0 < 0) y0 += h;
    if (x1 < 0) x1 += w;
    if (y1 < 0) y1 += h;
    if (x0 >= w) x0 -= w;
    if (y0 >= h) y0 -= h;
    if (x1 >= w) x1 -= w;
    if (y1 >= h) y1 -= h;
    const unsigned char* p00 = rgba + ((size_t)y0 * w + x0) * 4;
    const unsigned char* p10 = rgba + ((size_t)y0 * w + x1) * 4;
    const unsigned char* p01 = rgba + ((size_t)y1 * w + x0) * 4;
    const unsigned char* p11 = rgba + ((size_t)y1 * w + x1) * 4;
    for (int c = 0; c < 4; ++c) {
        float top = (float)p00[c] + ((float)p10[c] - (float)p00[c]) * ax;
        float bot = (float)p01[c] + ((float)p11[c] - (float)p01[c]) * ax;
        float val = top + (bot - top) * ay;
        outRGBA[c] = Clamp01(val / 255.0f);
    }
}

void SamplePreview(const TextureInfo& t, float u, float v, float outRGBA[4]) {
    if (!t.baked || t.previewW <= 0 || t.previewH <= 0 || t.preview.empty()) {
        outRGBA[0] = outRGBA[1] = outRGBA[2] = 0.8f;
        outRGBA[3] = 1.0f;
        return;
    }
    SampleRgba(t.preview.data(), t.previewW, t.previewH, u, v, outRGBA);
}

void FallbackColorForName(const std::string& name, float outRGB[3]) {
    // 低饱和的调色板：让无贴图材质也有区分度但不会五彩斑斓
    static const float kPalette[][3] = {
        {0.78f, 0.78f, 0.80f}, {0.72f, 0.76f, 0.82f}, {0.80f, 0.76f, 0.70f},
        {0.74f, 0.80f, 0.74f}, {0.80f, 0.74f, 0.78f}, {0.76f, 0.74f, 0.80f},
    };
    uint32_t h = 2166136261u;
    for (unsigned char c : name) {
        h ^= c;
        h *= 16777619u;
    }
    const float* c = kPalette[h % (sizeof(kPalette) / sizeof(kPalette[0]))];
    outRGB[0] = c[0];
    outRGB[1] = c[1];
    outRGB[2] = c[2];
}

} // namespace tex
} // namespace av
