// render_probe3.cpp —— 直接打印渲染结果的像素分布，定位"模型在哪"
#include <windows.h>
#include <objbase.h>

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include "d3d_renderer.h"
#include "model_data.h"
#include "texture.h"
#include "wic_util.h"

static std::string U8(const std::wstring& w) {
    int n = ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s((size_t)n, 0);
    ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}
static std::wstring W(const std::string& s) {
    int n = ::MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w((size_t)n, 0);
    ::MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

int main(int argc, char** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    std::string root = "E:\\workspace\\myspace\\godot_game\\biu-biu-origin\\素材";
    std::wstring proot = W(root);
    std::wstring outDir = L"E:\\workspace\\myspace\\godot_game\\biu-biu-origin\\tools\\asset_viewer\\build\\out\\_probe";
    av::img::EnsureDir(outDir);

    const wchar_t* cs = L"RPG武器\\Coin.glb";
    av::ModelData md;
    av::LoadModel(U8(proot + L"\\" + cs), &md);
    float c[3], rad;
    const int vw = 800, vh = 520;
    av::D3DRenderer r;
    r.Initialize(nullptr, vw, vh);
    r.SetModel(md);
    r.ResetCamera();
    r.GetBounds(c, &rad);
    std::printf("bounds center=(%.3f %.3f %.3f) radius=%.3f\n", c[0], c[1], c[2], rad);
    r.RenderFrame();
    std::vector<uint32_t> px;
    int w = 0, h = 0;
    r.Readback(&px, &w, &h);
    std::printf("readback w=%d h=%d n=%zu\n", w, h, px.size());
    av::img::SavePngFromBgra(outDir + L"\\coin_view.png", px.data(), w, h);

    // 全图亮度直方图 + 每 16 行的"非背景"像素数（背景 = 竖直渐变，见 PSSDump）
    int hist[16] = {0};
    long long mxSum = 0, mySum = 0, cnt = 0;
    for (int y = 0; y < h; ++y) {
        const float t = (float)y / (float)(h - 1);
        const double br = (0.10 + (0.05 - 0.10) * t) * 255.0;
        const double bg = (0.12 + (0.06 - 0.12) * t) * 255.0;
        const double bb = (0.16 + (0.09 - 0.16) * t) * 255.0;
        for (int x = 0; x < w; ++x) {
            uint32_t p = px[(size_t)y * w + x];
            double B = p & 0xFF, G = (p >> 8) & 0xFF, R = (p >> 16) & 0xFF;
            double lum = 0.299 * R + 0.587 * G + 0.114 * B;
            hist[(int)(lum / 16.0) > 15 ? 15 : (int)(lum / 16.0)]++;
            double d = std::fabs(R - br) + std::fabs(G - bg) + std::fabs(B - bb);
            if (d > 12) { mxSum += x; mySum += y; ++cnt; }
        }
    }
    std::printf("hist(lum/16): ");
    for (int i = 0; i < 16; ++i) std::printf("%d ", hist[i]);
    std::printf("\nnonBg(d>12) n=%lld (%.2f%%) centroid=(%.1f,%.1f)\n", cnt,
                100.0 * (double)cnt / ((double)w * h), cnt ? (double)mxSum / cnt : -1,
                cnt ? (double)mySum / cnt : -1);
    // 竖直剖面：列 x=w/2 上每 20 行的像素
    std::printf("\ncolumn x=%d profile:\n", w / 2);
    for (int y = 0; y < h; y += 20) {
        uint32_t p = px[(size_t)y * w + w / 2];
        std::printf("  y=%3d rgb=(%3u,%3u,%3u)\n", y, (p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF);
    }
    std::printf("\nrow y=%d profile:\n", h / 2);
    for (int x = 0; x < w; x += 40) {
        uint32_t p = px[(size_t)h / 2 * w + x];
        std::printf("  x=%3d rgb=(%3u,%3u,%3u)\n", x, (p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF);
    }
    r.Shutdown();
    ::CoUninitialize();
    return 0;
}
