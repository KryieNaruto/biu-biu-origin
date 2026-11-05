// render_probe2.cpp —— 屏幕空间包围盒分析（render-fix 诊断用）
// 在多个视口宽高比下渲染同一模型，统计"非背景像素"的 bbox 占画面的比例，
// 用来把"取景/居中"变成可判定的数字。
#include <windows.h>
#include <objbase.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
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

// 背景是竖直渐变 (0.05,0.06,0.09)->(0.10,0.12,0.16) 线性；按行取背景参考色，
// 与参考色差 > 6/255 视为前景。比"四角取样"更能容忍网格纹理。
struct Box {
    int n = 0;
    int x0 = 1 << 30, y0 = 1 << 30, x1 = -1, y1 = -1;
    double mx = 0, my = 0;
    double r = 0, g = 0, b = 0;
};
static Box Analyze(const std::vector<uint32_t>& px, int w, int h) {
    Box box;
    for (int y = 0; y < h; ++y) {
        const float t = (float)y / (float)(h > 1 ? h - 1 : 1);
        const double br = (0.10 + (0.05 - 0.10) * t) * 255.0;
        const double bg = (0.12 + (0.06 - 0.12) * t) * 255.0;
        const double bb = (0.16 + (0.09 - 0.16) * t) * 255.0;
        for (int x = 0; x < w; ++x) {
            const uint32_t p = px[(size_t)y * w + x];
            const double B = p & 0xFF, G = (p >> 8) & 0xFF, R = (p >> 16) & 0xFF;
            if (std::fabs(R - br) <= 7 && std::fabs(G - bg) <= 7 && std::fabs(B - bb) <= 7) continue;
            ++box.n;
            box.mx += x; box.my += y;
            box.r += R; box.g += G; box.b += B;
            if (x < box.x0) box.x0 = x;
            if (x > box.x1) box.x1 = x;
            if (y < box.y0) box.y0 = y;
            if (y > box.y1) box.y1 = y;
        }
    }
    if (box.n) {
        box.mx /= box.n; box.my /= box.n; box.r /= box.n; box.g /= box.n; box.b /= box.n;
    }
    return box;
}
static double Sat(double v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
static double LumStd(const std::vector<uint32_t>& px, const Box& box, int w, int h) {
    if (box.n == 0) return 0;
    double m = 0;
    for (int y = box.y0; y <= box.y1; ++y)
        for (int x = box.x0; x <= box.x1; ++x) {
            uint32_t p = px[(size_t)y * w + x];
            m += 0.299 * (p & 0xFF) + 0.587 * ((p >> 8) & 0xFF) + 0.114 * ((p >> 16) & 0xFF);
        }
    m /= (double)((box.x1 - box.x0 + 1) * (box.y1 - box.y0 + 1));
    double v = 0;
    for (int y = box.y0; y <= box.y1; ++y)
        for (int x = box.x0; x <= box.x1; ++x) {
            uint32_t p = px[(size_t)y * w + x];
            double l = 0.299 * (p & 0xFF) + 0.587 * ((p >> 8) & 0xFF) + 0.114 * ((p >> 16) & 0xFF);
            v += (l - m) * (l - m);
        }
    return std::sqrt(v / (double)((box.x1 - box.x0 + 1) * (box.y1 - box.y0 + 1)));
}
static void Save(const std::wstring& path, const std::vector<uint32_t>& px, int w, int h) {
    av::img::SavePngFromBgra(path, px.data(), w, h);
}

int main(int argc, char** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    std::string root = "E:\\workspace\\myspace\\godot_game\\biu-biu-origin\\素材";
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a.rfind("--root=", 0) == 0) root = a.substr(7);
    }
    std::wstring proot = W(root);
    std::wstring outDir = L"E:\\workspace\\myspace\\godot_game\\biu-biu-origin\\tools\\asset_viewer\\build\\out\\_probe";
    av::img::EnsureDir(outDir);

    const wchar_t* cases[] = {L"卡通人物带全套动画\\史蒂夫.glb", L"海盗场景带动画\\Anne.glb",
                              L"RPG武器\\Coin.glb",
                              L"POLYGON_Starter_SourceFiles_v2\\SourceFiles\\FBX\\SM_Bean_Cop_01.fbx"};
    // 模拟真实界面：默认 1400x900 窗口 → 列表宽 380 → 预览 1020 宽；info 高 42% → 预览 1020x~476
    const int sizes[][2] = {{1020, 476}, {800, 520}, {600, 900}};

    for (int ci = 0; ci < 4; ++ci) {
        av::ModelData md;
        if (!av::LoadModel(U8(proot + L"\\" + cases[ci]), &md)) { std::printf("load fail\n"); continue; }
        std::printf("\n==== %s (bone=%zu clips=%zu tex=%zu)\n", U8(cases[ci]).c_str(), md.bones.size(),
                    md.clips.size(), md.textures.size());
        for (int si = 0; si < 3; ++si) {
            const int vw = sizes[si][0], vh = sizes[si][1];
            av::D3DRenderer r;
            if (!r.Initialize(nullptr, vw, vh)) { std::printf("  init fail\n"); continue; }
            r.SetModel(md);
            r.ResetCamera();
            r.RenderFrame();
            std::vector<uint32_t> px;
            int w = 0, h = 0;
            if (!r.Readback(&px, &w, &h)) { std::printf("  readback FAILED w=%d h=%d\n", w, h); r.Shutdown(); continue; }
            Box b = Analyze(px, w, h);
            const double fw = (b.x1 >= b.x0) ? (b.x1 - b.x0 + 1) : 0;
            const double fh = (b.y1 >= b.y0) ? (b.y1 - b.y0 + 1) : 0;
            std::printf("  %4dx%-4d cover=%5.2f%% bboxW=%5.1f%% bboxH=%5.1f%%  coverCx=%6.1f/%d (%.1f%%) coverCy=%6.1f/%d (%.1f%%)  lumStd=%5.1f meanRGB=(%.0f,%.0f,%.0f)\n",
                        vw, vh, 100.0 * b.n / ((double)w * h), 100.0 * fw / w, 100.0 * fh / h, b.mx, w,
                        100.0 * b.mx / w, b.my, h, 100.0 * b.my / h, LumStd(px, b, w, h), b.r, b.g, b.b);
            if (si == 0) {
                wchar_t nm[96];
                ::swprintf_s(nm, L"\\fit_%d.png", ci);
                Save(outDir + nm, px, w, h);
            }
            r.Shutdown();
        }
    }
    ::CoUninitialize();
    return 0;
}
