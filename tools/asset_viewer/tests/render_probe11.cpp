// render_probe11.cpp -- why do 38/301 models render "invisible"?
//
// Dumps the actual pixel content for a few of the suspects:
//   * luminance histogram
//   * how many pixels differ from the pure-clear colour at all (any delta)
//   * exact bbox of "any-delta" pixels
// This distinguishes "model really is off-screen / degenerate" from
// "model is drawn but my IsModel() heuristic is too strict".
#include "av_util.h"
#include "d3d_renderer.h"
#include "model_data.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include <windows.h>

int wmain(int argc, wchar_t** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    if (argc < 3) { std::printf("usage: rp11.exe <root> <relpath>\n"); return 2; }
    std::wstring path = std::wstring(argv[1]) + L"\\" + std::wstring(argv[2]);

    av::ModelData md;
    if (!av::LoadModel(avu::Utf8(path), &md) || !md.ok) { std::printf("load fail\n"); return 3; }

    av::D3DRenderer r;
    if (!r.Initialize(nullptr, 1060, 481)) return 3;
    if (!r.Resize(1060, 481)) return 3;
    r.SetModel(md);
    r.ResetCamera();
    if (!r.RenderFrame()) { std::printf("render fail\n"); return 3; }
    std::vector<uint32_t> raw;
    int w = 0, h = 0;
    if (!r.Readback(&raw, &w, &h) || w <= 0) { std::printf("readback fail\n"); return 3; }

    // the renderer clears to (0.05,0.06,0.09) linear -> written directly, so 8-bit ~ (13,15,23)
    int hist[16] = {0};
    int anyDelta = 0;
    int x0 = w, y0 = h, x1 = -1, y1 = -1;
    int maxLum = 0;
    double sumLum = 0;
    for (int y = 0; y < h; ++y)
        for (int x = 0; x < w; ++x) {
            const uint32_t p = raw[(size_t)y * w + x];
            const int b = (int)(p & 0xFF), g = (int)((p >> 8) & 0xFF), rr = (int)((p >> 16) & 0xFF);
            const int lum = (rr * 30 + g * 59 + b * 11) / 100;
            hist[lum / 16 > 15 ? 15 : lum / 16]++;
            if (lum > maxLum) maxLum = lum;
            sumLum += lum;
            // anything that is not the flat clear colour
            if (std::abs(rr - 13) + std::abs(g - 15) + std::abs(b - 23) > 6) {
                anyDelta++;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }
    std::printf("=== %s  verts=%zu tris=%zu ===\n", avu::Utf8(argv[2]).c_str(),
                md.mesh.vertices.size(), md.mesh.indices.size() / 3);
    std::printf("anyDeltaFromClear = %d (%.2f%%)  bbox=(%d,%d)-(%d,%d)\n",
                anyDelta, 100.0 * anyDelta / (w * h), x0, y0, x1, y1);
    std::printf("maxLum=%d  meanLum=%.2f\n", maxLum, sumLum / (w * h));
    std::printf("hist(lum/16): ");
    for (int i = 0; i < 16; ++i) std::printf("%d ", hist[i]);
    std::printf("\n");

    // corner sample = clear colour reference
    auto px = [&](int x, int y) {
        const uint32_t p = raw[(size_t)y * w + x];
        std::printf("  (%d,%d) = (%d,%d,%d)\n", x, y,
                    (int)((p >> 16) & 0xFF), (int)((p >> 8) & 0xFF), (int)(p & 0xFF));
    };
    std::printf("samples:\n");
    px(2, 2);
    px(w / 2, h / 2);
    if (x1 >= 0) px((x0 + x1) / 2, (y0 + y1) / 2);
    return 0;
}
