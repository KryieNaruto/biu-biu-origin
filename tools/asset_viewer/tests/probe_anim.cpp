// probe_anim.cpp —— 验证动画链路：clip 列表 + 骨骼矩阵是否随时间变化 + 蒙皮权重覆盖率。
//
// 用法：probe_anim.exe <模型路径>
// 输出：每个 clip 的名字/时长/被驱动骨骼数，以及 t=0/0.25/0.5/0.75 时刻的骨骼矩阵差异。
#include "model_data.h"

#include <windows.h>

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

int main() {
    int argc = 0;
    LPWSTR* a = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    if (!a || argc < 2) { std::printf("usage: probe_anim.exe <model>\n"); return 2; }
    int n = ::WideCharToMultiByte(CP_UTF8, 0, a[1], -1, nullptr, 0, nullptr, nullptr);
    std::string p((size_t)(n > 0 ? n : 1), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, a[1], -1, &p[0], n, nullptr, nullptr);
    while (!p.empty() && p.back() == '\0') p.pop_back();
    ::LocalFree(a);

    av::ModelData md;
    if (!av::LoadModel(p, &md) || !md.ok) {
        std::printf("load fail: %s\n", md.error.c_str());
        return 1;
    }
    std::printf("model=%s\nbones=%zu clips=%zu verts=%zu tris=%u\n", p.c_str(), md.bones.size(),
                md.clips.size(), md.mesh.vertices.size(), md.triangleCount);

    // 蒙皮覆盖率：有多少顶点的权重和 > 0
    size_t skinned = 0;
    for (size_t i = 0; i < md.skin.size(); ++i) {
        const av::SkinInfluences& s = md.skin[i];
        float w = s.weight[0] + s.weight[1] + s.weight[2] + s.weight[3];
        if (w > 1e-4f) ++skinned;
    }
    std::printf("skin=%zu skinnedVerts=%zu (%.1f%% of verts) bones=%zu\n", md.skin.size(), skinned,
                md.mesh.vertices.size() ? 100.0 * (double)skinned / (double)md.mesh.vertices.size()
                                        : 0.0,
                md.bones.size());

    if (md.clips.empty()) {
        std::printf("NO ANIMATION CLIPS\n");
        return 0;
    }

    for (size_t ci = 0; ci < md.clips.size() && ci < 40; ++ci) {
        const av::Clip& c = md.clips[ci];
        size_t nonEmptyTracks = 0;
        size_t totalKeys = 0;
        for (const auto& tr : c.tracks) {
            if (!tr.empty()) ++nonEmptyTracks;
            totalKeys += tr.size();
        }
        // 在 4 个时刻采样世界矩阵，统计「有变化的骨骼数」
        const double times[4] = {0.0, c.duration * 0.25, c.duration * 0.5, c.duration * 0.75};
        size_t movingBones = 0;
        std::vector<float> w0, wk;
        if (av::SampleClipWorld(md, (int)ci, times[0], &w0)) {
            for (size_t b = 0; b < md.bones.size(); ++b) {
                bool differs = false;
                for (int k = 1; k < 4 && !differs; ++k) {
                    if (!av::SampleClipWorld(md, (int)ci, times[k], &wk)) continue;
                    for (int e = 0; e < 16; ++e) {
                        const size_t idx = b * 16 + (size_t)e;
                        if (idx < w0.size() && idx < wk.size() &&
                            std::fabs(w0[idx] - wk[idx]) > 1e-4f) {
                            differs = true;
                            break;
                        }
                    }
                }
                if (differs) ++movingBones;
            }
        }
        std::printf("  clip[%zu] '%-16s' dur=%.3f tracks=%zu keys=%zu animatedBones=%zu\n", ci,
                    c.name.c_str(), c.duration, nonEmptyTracks, totalKeys, movingBones);
    }
    return 0;
}
