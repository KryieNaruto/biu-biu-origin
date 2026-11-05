// render_probe8.cpp -- bug4: does the animation actually deform the model?
//
// Samples a clip at several times through SampleClipLocals/World + BuildSkinMatrices
// (the exact same path ViewerApp::UpdateSkinAt uses), feeds the result to D3DRenderer,
// and measures how much the silhouette moves on screen between t=0 and t=dur/2.
//
// If the model is static (diff ~= 0) the "model has no animation" complaint is about
// the render path, not about data.

#include "av_util.h"
#include "d3d_renderer.h"
#include "model_data.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include <windows.h>

namespace {

std::wstring FindRoot(int argc, wchar_t** argv) {
    if (argc > 1 && argv[1][0]) return std::wstring(argv[1]);
    return std::wstring();
}

bool IsModel(int r, int g, int b) {
    const int lum = (r * 30 + g * 59 + b * 11) / 100;
    if (lum > 62) return true;
    const int mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
    const int mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
    return (mx - mn) > 22;
}

struct Mask {
    std::vector<unsigned char> m;
    int w = 0, h = 0, n = 0;
    int x0 = 0, x1 = 0, y0 = 0, y1 = 0;
};

Mask Grab(av::D3DRenderer& r) {
    Mask out;
    if (!r.RenderFrame()) return out;
    std::vector<uint32_t> raw;
    int w = 0, h = 0;
    if (!r.Readback(&raw, &w, &h) || w <= 0) return out;
    out.w = w; out.h = h;
    out.m.assign((size_t)w * h, 0);
    out.x0 = w; out.y0 = h; out.x1 = -1; out.y1 = -1;
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            const size_t i = (size_t)y * w + x;
            const int b = (int)(raw[i] & 0xFF), g = (int)((raw[i] >> 8) & 0xFF),
                      rr = (int)((raw[i] >> 16) & 0xFF);
            if (!IsModel(rr, g, b)) continue;
            out.m[i] = 1;
            out.n++;
            if (x < out.x0) out.x0 = x;
            if (x > out.x1) out.x1 = x;
            if (y < out.y0) out.y0 = y;
            if (y > out.y1) out.y1 = y;
        }
    }
    return out;
}

float DiffFrac(const Mask& a, const Mask& b) {
    if (a.w != b.w || a.m.empty()) return -1.0f;
    int d = 0;
    for (size_t i = 0; i < a.m.size(); ++i) if (a.m[i] != b.m[i]) d++;
    return (float)d / (float)a.m.size();
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    std::wstring root = FindRoot(argc, argv);
    if (root.empty()) { std::printf("usage: rp8.exe <assetroot>\n"); return 2; }

    const wchar_t* models[] = {
        L"\u5361\u901a\u4eba\u7269\u5e26\u5168\u5957\u52a8\u753b\\\u53f2\u8482\u592b.glb",
        L"\u5361\u901a\u4eba\u7269\u5e26\u5168\u5957\u52a8\u753b\\Anne.glb",
        L"\u6d77\u76d7\u573a\u666f\u5e26\u52a8\u753b\\Pirate Captain.glb",
    };

    av::D3DRenderer r;
    if (!r.Initialize(nullptr, 800, 400)) { std::printf("d3d init fail\n"); return 3; }
    if (!r.Resize(800, 400)) { std::printf("resize fail\n"); return 3; }

    for (int mi = 0; mi < 3; ++mi) {
        std::wstring path = root + L"\\" + models[mi];
        if (!avu::FileExists(path)) {
            std::printf("\n[skip] %s\n", avu::Utf8(models[mi]).c_str());
            continue;
        }
        av::ModelData md;
        if (!av::LoadModel(avu::Utf8(path), &md) || !md.ok) {
            std::printf("\n[loadfail] %s\n", avu::Utf8(models[mi]).c_str());
            continue;
        }
        std::printf("\n=== %s ===\n", avu::Utf8(models[mi]).c_str());
        std::printf("  bones=%zu clips=%zu skin=%zu verts=%zu\n", md.bones.size(), md.clips.size(),
                    md.skin.size(), md.mesh.vertices.size());
        if (md.clips.empty()) { std::printf("  (no clips)\n"); continue; }

        r.SetModel(md);
        r.ResetCamera();

        // clip 0, t = 0 vs t = dur/2, and a couple more clips
        const int clipsToTry[] = {0, 1, 2};
        for (int ci = 0; ci < 3; ++ci) {
            if (ci >= (int)md.clips.size()) break;
            const av::Clip& clip = md.clips[ci];
            std::printf("  clip[%d] name=%s dur=%.3fs tracks=%zu\n", ci, clip.name.c_str(),
                        clip.duration, clip.tracks.size());
            if (clip.duration <= 0.0) { std::printf("    zero duration, skipped\n"); continue; }

            std::vector<float> world, skins;
            auto sample = [&](double t) {
                if (!av::SampleClipWorld(md, ci, t, &world)) return;
                av::BuildSkinMatrices(md, world, &skins);
                r.SetSkinMatrices(&skins);
            };
            sample(0.0);
            const Mask a = Grab(r);
            sample(clip.duration * 0.5);
            const Mask b = Grab(r);
            sample(clip.duration * 0.25);
            const Mask c = Grab(r);

            const float d1 = DiffFrac(a, b);
            const float d2 = DiffFrac(a, c);
            std::printf("    t=0   nonBg=%6.2f%% bbox=(%d,%d)-(%d,%d)\n",
                        100.0 * a.n / (double)(a.w * a.h), a.x0, a.y0, a.x1, a.y1);
            std::printf("    t=dur/2 nonBg=%6.2f%% bbox=(%d,%d)-(%d,%d)\n",
                        100.0 * b.n / (double)(b.w * b.h), b.x0, b.y0, b.x1, b.y1);
            std::printf("    diff(t0,t/2)=%.2f%%  diff(t0,t/4)=%.2f%%  %s\n",
                        100.0 * d1, 100.0 * d2,
                        d1 > 0.002f ? "ANIMATES" : "*** STATIC ***");
        }
        r.SetSkinMatrices(nullptr);
    }
    return 0;
}
