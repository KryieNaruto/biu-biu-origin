// render_probe7.cpp -- bug2: is the model centered and filling the preview pane?
//
// Renders a model through D3DRenderer at the EXACT preview-pane size of each window
// size, then measures the model's pixel footprint:
//   - non-background pixel count / fraction
//   - bbox of the model in pixels
//   - centroid vs the pane center  (centering error, in px and as % of pane)
//   - fill ratio: bbox area / pane area
//   - margin on all four sides (how much unused space)
// Compare against the CPU reference (thumb_renderer) which is known-good.
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
    wchar_t exe[MAX_PATH] = {0};
    ::GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring dir = avu::ParentDir(exe);
    for (int i = 0; i < 8 && !dir.empty(); ++i) {
        if (avu::DirExists(dir + L"\\\u7d20\u6750")) return dir + L"\\\u7d20\u6750";
        std::wstring p = avu::ParentDir(dir);
        if (p == dir) break;
        dir = p;
    }
    return std::wstring();
}

struct Stats {
    int n = 0;              // non-bg pixels
    float frac = 0.0f;      // fraction of pane
    int x0 = 0, x1 = 0, y0 = 0, y1 = 0;
    float cx = 0, cy = 0;   // centroid
    float fill = 0.0f;      // bbox area / pane area
};

// Background of the D3D preview is a gradient around (0.10,0.12,0.16)->(0.05,0.06,0.09)
// which in 8-bit lands near (13..25, 15..30, 23..40) plus a faint grid. A pixel counts
// as "model" when it is clearly brighter OR clearly more saturated than that band.
bool IsModel(int r, int g, int b) {
    const int lum = (r * 30 + g * 59 + b * 11) / 100;
    if (lum > 62) return true;                                  // clearly brighter than bg
    const int mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
    const int mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
    if (mx - mn > 22) return true;                              // clearly saturated
    return false;
}

Stats Analyze(const std::vector<unsigned char>& px, int w, int h) {
    Stats s;
    long long sx = 0, sy = 0;
    s.x0 = w; s.y0 = h; s.x1 = -1; s.y1 = -1;
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            const size_t i = ((size_t)y * w + x) * 4;
            const int b = px[i + 0], g = px[i + 1], r = px[i + 2];
            if (!IsModel(r, g, b)) continue;
            s.n++;
            sx += x; sy += y;
            if (x < s.x0) s.x0 = x;
            if (x > s.x1) s.x1 = x;
            if (y < s.y0) s.y0 = y;
            if (y > s.y1) s.y1 = y;
        }
    }
    s.frac = (float)s.n / (float)(w * h);
    if (s.n > 0) {
        s.cx = (float)sx / s.n;
        s.cy = (float)sy / s.n;
        const float bw = (float)(s.x1 - s.x0 + 1);
        const float bh = (float)(s.y1 - s.y0 + 1);
        s.fill = (bw * bh) / (float)(w * h);
    }
    return s;
}

struct Pane { int w, h; };

} // namespace

int wmain(int argc, wchar_t** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    std::wstring root = FindRoot(argc, argv);
    if (root.empty()) { std::printf("no root\n"); return 2; }

    const wchar_t* models[] = {
        L"RPG\u6b66\u5668\\Coin.glb",
        L"\u5361\u901a\u4eba\u7269\u5e26\u5168\u5957\u52a8\u753b\\\u53f2\u8482\u592b.glb",
        L"RTS\u5efa\u7b51\\Archery Towers.glb",
        L"\u5c0f\u623f\u5b50\\House.glb",
    };

    // preview pane sizes measured by probe6 for each window size
    const struct { const char* n; Pane p; } panes[] = {
        {"1400x900 -> pane 1060x481", {1060, 481}},
        {"1000x700 -> pane  660x365", {660, 365}},
        {"1680x1050 -> pane 1340x568", {1340, 568}},
    };

    av::D3DRenderer r;
    if (!r.Initialize(nullptr, 1024, 512)) { std::printf("D3D init failed\n"); return 3; }

    for (int mi = 0; mi < 4; ++mi) {
        std::wstring path = root + L"\\" + models[mi];
        if (!avu::FileExists(path)) { std::printf("\n[skip] %s\n", avu::Utf8(path).c_str()); continue; }
        av::ModelData md;
        if (!av::LoadModel(avu::Utf8(path), &md) || !md.ok) {
            std::printf("\n[skip load] %s\n", avu::Utf8(path).c_str());
            continue;
        }
        std::printf("\n=== %s  tris=%zu clips=%zu ===\n",
                    avu::Utf8(models[mi]).c_str(), md.mesh.indices.size() / 3, md.clips.size());
        r.SetModel(md);
        r.ResetCamera();

        for (int pi = 0; pi < 3; ++pi) {
            const Pane pn = panes[pi].p;
            if (!r.Resize(pn.w, pn.h)) { std::printf("  resize fail\n"); continue; }
            if (!r.RenderFrame()) { std::printf("  render fail\n"); continue; }
            int w = 0, h = 0;
            std::vector<uint32_t> raw;
            if (!r.Readback(&raw, &w, &h) || w <= 0 || h <= 0) {
                std::printf("  [%s] readback FAILED w=%d h=%d\n", panes[pi].n, w, h);
                continue;
            }
            // BGRA8 packed in uint32 little-endian: b=low byte, r=high byte
            std::vector<unsigned char> px((size_t)w * h * 4);
            for (size_t i = 0; i < raw.size(); ++i) {
                px[i * 4 + 0] = (unsigned char)(raw[i] & 0xFF);
                px[i * 4 + 1] = (unsigned char)((raw[i] >> 8) & 0xFF);
                px[i * 4 + 2] = (unsigned char)((raw[i] >> 16) & 0xFF);
                px[i * 4 + 3] = (unsigned char)((raw[i] >> 24) & 0xFF);
            }
            const Stats s = Analyze(px, w, h);
            const float paneCx = w * 0.5f, paneCy = h * 0.5f;
            const float ecx = s.cx - paneCx, ecy = s.cy - paneCy;
            std::printf("  %-26s nonBg=%6.2f%%  bbox=(%d,%d)-(%d,%d) %dx%d\n",
                        panes[pi].n, 100.0 * s.frac,
                        s.x0, s.y0, s.x1, s.y1,
                        s.x1 - s.x0 + 1, s.y1 - s.y0 + 1);
            std::printf("      centroid=(%.1f,%.1f) paneCenter=(%.1f,%.1f) err=(%+.1f,%+.1f)px"
                        "  fill=%.1f%%  margins L=%d R=%d T=%d B=%d\n",
                        (double)s.cx, (double)s.cy, (double)paneCx, (double)paneCy,
                        (double)ecx, (double)ecy, 100.0 * s.fill,
                        s.x0, w - 1 - s.x1, s.y0, h - 1 - s.y1);
        }
    }
    return 0;
}
