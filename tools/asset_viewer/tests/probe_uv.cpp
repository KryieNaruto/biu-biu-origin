// probe_uv.cpp —— 扫描模型 UV 范围，找出 NaN/Inf/超范围 UV（越界采样会导致堆破坏）。
#include "model_data.h"

#include <windows.h>

#include <cmath>
#include <cstdio>
#include <string>

int main() {
    int argc = 0;
    LPWSTR* a = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    if (!a || argc < 2) { std::printf("usage: probe_uv.exe <model>\n"); return 2; }
    int n = ::WideCharToMultiByte(CP_UTF8, 0, a[1], -1, nullptr, 0, nullptr, nullptr);
    std::string p((size_t)(n > 0 ? n : 1), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, a[1], -1, &p[0], n, nullptr, nullptr);
    while (!p.empty() && p.back() == '\0') p.pop_back();
    ::LocalFree(a);

    av::ModelData md;
    if (!av::LoadModel(p, &md) || !md.ok) { std::printf("load fail\n"); return 1; }

    float umin = 1e30f, umax = -1e30f, vmin = 1e30f, vmax = -1e30f;
    size_t nanCnt = 0, infCnt = 0, bigCnt = 0;
    for (const auto& v : md.mesh.vertices) {
        if (!std::isfinite(v.u) || !std::isfinite(v.v)) { ++nanCnt; continue; }
        if (std::fabs(v.u) > 1e6f || std::fabs(v.v) > 1e6f) ++bigCnt;
        if (v.u < umin) umin = v.u;
        if (v.u > umax) umax = v.u;
        if (v.v < vmin) vmin = v.v;
        if (v.v > vmax) vmax = v.v;
    }
    std::printf("verts=%zu  u=[%.4f, %.4f]  v=[%.4f, %.4f]\n", md.mesh.vertices.size(), umin, umax,
                vmin, vmax);
    std::printf("non-finite=%zu  |uv|>1e6=%zu\n", nanCnt, bigCnt);
    // 检查索引越界
    size_t badIdx = 0;
    for (uint32_t idx : md.mesh.indices) {
        if (idx >= md.mesh.vertices.size()) ++badIdx;
    }
    std::printf("indices=%zu  out-of-range=%zu  maxIdx=\n", md.mesh.indices.size(), badIdx);
    // 检查 skin 数组
    std::printf("skin=%zu verts=%zu  skin<verts? %s\n", md.skin.size(), md.mesh.vertices.size(),
                md.skin.size() < md.mesh.vertices.size() ? "YES" : "no");
    return 0;
}
