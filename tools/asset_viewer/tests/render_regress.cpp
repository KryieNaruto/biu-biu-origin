// render_regress.cpp -- all-library regression: framing + visibility after the fixes
//
// Renders EVERY model at the real preview-pane size (1060x481) and reports:
//   * nonBg%  -- model must be visible (old bugs produced 0.00% or <3%)
//   * fill%   -- bbox area / pane area, should be roughly 8..400% depending on shape
//   * centering error in px
// Anything with nonBg < 1% is reported as a suspect.
#include "av_util.h"
#include "d3d_renderer.h"
#include "model_data.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include <windows.h>

namespace {

// The clear colour is (13,15,23); the drawn background gradient stays within a few
// units of it. Anything meaningfully off that flat colour is geometry. This is much
// more reliable than a luminance/saturation threshold, which mis-flags dark neutral
// models (a grey pistol lit at lum~42 was reported "invisible" by the old test).
bool IsModel(int r, int g, int b) {
    return std::abs(r - 13) + std::abs(g - 15) + std::abs(b - 23) > 6;
}

struct Res { float nonBg, fill, err; int x0, y0, x1, y1; };

Res Analyze(const std::vector<uint32_t>& raw, int w, int h) {
    Res s{};
    long long sx = 0, sy = 0;
    int n = 0;
    s.x0 = w; s.y0 = h; s.x1 = -1; s.y1 = -1;
    for (int y = 0; y < h; ++y)
        for (int x = 0; x < w; ++x) {
            const uint32_t p = raw[(size_t)y * w + x];
            const int b = (int)(p & 0xFF), g = (int)((p >> 8) & 0xFF), r = (int)((p >> 16) & 0xFF);
            if (!IsModel(r, g, b)) continue;
            n++; sx += x; sy += y;
            if (x < s.x0) s.x0 = x;
            if (x > s.x1) s.x1 = x;
            if (y < s.y0) s.y0 = y;
            if (y > s.y1) s.y1 = y;
        }
    s.nonBg = 100.0f * n / (float)(w * h);
    if (n > 0) {
        const float cx = (float)sx / n, cy = (float)sy / n;
        const float dx = cx - w * 0.5f, dy = cy - h * 0.5f;
        s.err = std::sqrt(dx * dx + dy * dy);
        s.fill = 100.0f * (float)((s.x1 - s.x0 + 1) * (s.y1 - s.y0 + 1)) / (float)(w * h);
    }
    return s;
}

std::vector<std::wstring> ListGlb(const std::wstring& dir) {
    std::vector<std::wstring> out;
    WIN32_FIND_DATAW fd{};
    HANDLE h = ::FindFirstFileW((dir + L"\\*.glb").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return out;
    do { out.push_back(fd.cFileName); } while (::FindNextFileW(h, &fd));
    ::FindClose(h);
    return out;
}

std::vector<std::wstring> ListDirs(const std::wstring& root) {
    std::vector<std::wstring> out;
    WIN32_FIND_DATAW fd{};
    HANDLE h = ::FindFirstFileW((root + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return out;
    do {
        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && fd.cFileName[0] != L'.')
            out.push_back(fd.cFileName);
    } while (::FindNextFileW(h, &fd));
    ::FindClose(h);
    return out;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    if (argc < 2) { std::printf("usage: rr.exe <assetroot>\n"); return 2; }
    const std::wstring root = argv[1];

    av::D3DRenderer r;
    if (!r.Initialize(nullptr, 1060, 481)) { std::printf("d3d init fail\n"); return 3; }
    if (!r.Resize(1060, 481)) { std::printf("resize fail\n"); return 3; }

    int total = 0, invisible = 0, tiny = 0;
    float minNonBg = 1e9f;
    std::vector<std::wstring> suspects;

    for (const auto& cat : ListDirs(root)) {
        const std::wstring cdir = root + L"\\" + cat;
        for (const auto& glb : ListGlb(cdir)) {
            const std::wstring full = cdir + L"\\" + glb;
            av::ModelData md;
            if (!av::LoadModel(avu::Utf8(full), &md) || !md.ok) continue;
            total++;
            r.SetModel(md);
            r.ResetCamera();
            if (!r.RenderFrame()) { std::printf("RENDER FAIL %s\n", avu::Utf8(glb).c_str()); continue; }
            std::vector<uint32_t> raw;
            int w = 0, h = 0;
            if (!r.Readback(&raw, &w, &h) || w <= 0) continue;
            const Res s = Analyze(raw, w, h);
            if (s.nonBg < minNonBg) minNonBg = s.nonBg;
            if (s.nonBg < 1.0f) { invisible++; suspects.push_back(cat + L"\\" + glb); }
            else if (s.fill < 5.0f) { tiny++; suspects.push_back(cat + L"\\" + glb); }
            if (argc > 2) {
                std::printf("  %-46s nonBg=%6.2f%% fill=%6.2f%% err=%5.1fpx\n",
                            avu::Utf8(glb).c_str(), s.nonBg, s.fill, s.err);
            }
        }
    }
    std::printf("\ntotal=%d  invisible(<1%%)=%d  tiny(fill<5%%)=%d  minNonBg=%.2f%%\n",
                total, invisible, tiny, minNonBg);
    if (!suspects.empty()) {
        std::printf("suspects:\n");
        for (size_t i = 0; i < suspects.size() && i < 40; ++i)
            std::printf("  %s\n", avu::Utf8(suspects[i]).c_str());
    }
    return 0;
}
