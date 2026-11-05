// render_probe9.cpp -- why is Pirate Captain static?
//
// Compares a model that animates (Steve) with one that does not (Pirate Captain):
//   * does the sampled world matrix actually change between t=0 and t=dur/2?
//   * does skinMat = world * bind differ from identity?
//   * are the vertex bone indices/weights sane (in range, weights summing to 1)?
// This separates "clip data is empty/degenerate" from "skinning math cancels out".
#include "av_util.h"
#include "model_data.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include <windows.h>

namespace {

float maxAbsDiff(const std::vector<float>& a, const std::vector<float>& b) {
    if (a.size() != b.size() || a.empty()) return -1.0f;
    float m = 0.0f;
    for (size_t i = 0; i < a.size(); ++i) {
        const float d = std::fabs(a[i] - b[i]);
        if (d > m) m = d;
    }
    return m;
}

int countNonIdentity(const std::vector<float>& m) {
    int c = 0;
    for (size_t b = 0; b + 16 <= m.size(); b += 16) {
        const float* p = &m[b];
        bool ident = true;
        for (int r = 0; r < 4 && ident; ++r)
            for (int col = 0; col < 4; ++col) {
                const float want = (r == col) ? 1.0f : 0.0f;
                if (std::fabs(p[r * 4 + col] - want) > 1e-4f) { ident = false; break; }
            }
        if (!ident) c++;
    }
    return c;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    if (argc < 3) { std::printf("usage: rp9.exe <root> <relpath>\n"); return 2; }
    std::wstring root = argv[1];
    std::wstring rel = argv[2];

    av::ModelData md;
    const std::wstring path = root + L"\\" + rel;
    if (!av::LoadModel(avu::Utf8(path), &md) || !md.ok) {
        std::printf("load fail: %s\n", avu::Utf8(path).c_str());
        return 3;
    }
    std::printf("=== %s ===\nbones=%zu verts=%zu skin=%zu clips=%zu\n",
                avu::Utf8(rel).c_str(), md.bones.size(), md.mesh.vertices.size(),
                md.skin.size(), md.clips.size());

    // bind matrix sanity
    int bindNonIdent = 0;
    for (size_t b = 0; b < md.bones.size(); ++b) {
        const float* p = md.bones[b].bind;
        bool ident = true;
        for (int r = 0; r < 4 && ident; ++r)
            for (int c = 0; c < 4; ++c) {
                const float want = (r == c) ? 1.0f : 0.0f;
                if (std::fabs(p[r * 4 + c] - want) > 1e-4f) { ident = false; break; }
            }
        if (!ident) bindNonIdent++;
    }
    std::printf("bones with non-identity bind = %d / %zu\n", bindNonIdent, md.bones.size());

    // vertex influence sanity
    int overRange = 0, zeroWsum = 0, badWsum = 0;
    for (size_t i = 0; i < md.skin.size(); ++i) {
        const av::SkinInfluences& si = md.skin[i];
        float ws = 0.0f;
        for (int k = 0; k < 4; ++k) {
            ws += si.weight[k];
            if (si.weight[k] > 0.0f && si.boneIndex[k] >= md.bones.size()) overRange++;
        }
        if (ws < 0.0001f) zeroWsum++;
        else if (std::fabs(ws - 1.0f) > 0.05f) badWsum++;
    }
    std::printf("skin: idxOutOfRange=%d  wsum==0:%d  wsum!=1:%d  (total %zu)\n",
                overRange, zeroWsum, badWsum, md.skin.size());

    if (md.clips.empty()) return 0;

    for (int ci = 0; ci < (int)md.clips.size() && ci < 4; ++ci) {
        const av::Clip& clip = md.clips[ci];
        int nonEmptyTracks = 0;
        size_t totalKeys = 0;
        for (size_t t = 0; t < clip.tracks.size(); ++t) {
            if (!clip.tracks[t].empty()) nonEmptyTracks++;
            totalKeys += clip.tracks[t].size();
        }
        std::printf("\nclip[%d] dur=%.3f tracks=%zu nonEmptyTracks=%d totalKeys=%zu\n",
                    ci, clip.duration, clip.tracks.size(), nonEmptyTracks, totalKeys);

        std::vector<float> w0, w1, s0, s1;
        if (!av::SampleClipWorld(md, ci, 0.0, &w0)) { std::printf("  sample t=0 FAILED\n"); continue; }
        if (!av::SampleClipWorld(md, ci, clip.duration * 0.5, &w1)) {
            std::printf("  sample t=dur/2 FAILED\n"); continue;
        }
        av::BuildSkinMatrices(md, w0, &s0);
        av::BuildSkinMatrices(md, w1, &s1);

        std::printf("  world : size=%zu maxDiff(t0,t/2)=%.6f\n", w0.size(), maxAbsDiff(w0, w1));
        std::printf("  skin  : size=%zu maxDiff(t0,t/2)=%.6f  nonIdentity@t0=%d\n",
                    s0.size(), maxAbsDiff(s0, s1), countNonIdentity(s0));

        // how far do skinned vertices actually move?
        float maxMove = 0.0f;
        int moved = 0;
        for (size_t i = 0; i < md.skin.size() && i < md.mesh.vertices.size(); ++i) {
            const av::Vertex& v = md.mesh.vertices[i];
            const av::SkinInfluences& si = md.skin[i];
            float ws = 0.0f;
            for (int k = 0; k < 4; ++k) ws += si.weight[k];
            if (ws < 0.0001f) continue;
            auto skinned = [&](const std::vector<float>& S, float out[3]) {
                out[0] = out[1] = out[2] = 0.0f;
                for (int k = 0; k < 4; ++k) {
                    const float w = si.weight[k];
                    if (w <= 0.0f) continue;
                    const size_t bi = si.boneIndex[k];
                    if (bi * 16 + 16 > S.size()) continue;
                    const float* m = &S[bi * 16];
                    out[0] += w * (v.px * m[0] + v.py * m[4] + v.pz * m[8] + m[12]);
                    out[1] += w * (v.px * m[1] + v.py * m[5] + v.pz * m[9] + m[13]);
                    out[2] += w * (v.px * m[2] + v.py * m[6] + v.pz * m[10] + m[14]);
                }
            };
            float a[3], b[3];
            skinned(s0, a);
            skinned(s1, b);
            const float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            const float d = std::sqrt(dx * dx + dy * dy + dz * dz);
            if (d > maxMove) maxMove = d;
            if (d > 1e-3f) moved++;
        }
        std::printf("  skinned verts that move t0->t/2: %d / %zu   maxMove=%.5f\n",
                    moved, md.skin.size(), maxMove);
    }
    return 0;
}
