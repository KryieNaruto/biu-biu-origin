// render_probe.cpp —— 临时探针（render-fix 诊断用，不入交付）
#include <windows.h>
#include <objbase.h>

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "d3d_renderer.h"
#include "glb_loader.h"
#include "model_data.h"
#include "thumb_renderer.h"
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

struct Stat {
    int nonBg = 0;
    double minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
    double cx = 0, cy = 0;
    double sumR = 0, sumG = 0, sumB = 0;
    double varR = 0;
    int bgTL = 0;
};

static Stat Analyze(const std::vector<uint32_t>& px, int w, int h) {
    Stat s;
    // 背景取样：四角 8x8 平均
    double br = 0, bg = 0, bb = 0;
    int bn = 0;
    int cor[4][2] = {{0, 0}, {w - 8, 0}, {0, h - 8}, {w - 8, h - 8}};
    for (int c = 0; c < 4; ++c) {
        for (int y = 0; y < 8; ++y)
            for (int x = 0; x < 8; ++x) {
                uint32_t p = px[(size_t)(cor[c][1] + y) * w + cor[c][0] + x];
                bb += (p & 0xFF);
                bg += ((p >> 8) & 0xFF);
                br += ((p >> 16) & 0xFF);
                ++bn;
            }
    }
    br /= bn; bg /= bn; bb /= bn;
    double means[3] = {br, bg, bb};
    std::vector<double> rs;
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            uint32_t p = px[(size_t)y * w + x];
            double b = p & 0xFF, g = (p >> 8) & 0xFF, r = (p >> 16) & 0xFF;
            bool isBg = std::fabs(r - means[0]) < 10 && std::fabs(g - means[1]) < 10 &&
                        std::fabs(b - means[2]) < 10;
            if (!isBg) {
                ++s.nonBg;
                s.cx += x; s.cy += y;
                if (x < s.minX) s.minX = x;
                if (x > s.maxX) s.maxX = x;
                if (y < s.minY) s.minY = y;
                if (y > s.maxY) s.maxY = y;
                rs.push_back(r);
            }
            s.sumR += r; s.sumG += g; s.sumB += b;
        }
    }
    if (s.nonBg > 0) { s.cx /= s.nonBg; s.cy /= s.nonBg; }
    double mr = rs.empty() ? 0 : 0;
    for (size_t i = 0; i < rs.size(); ++i) mr += rs[i];
    if (!rs.empty()) mr /= rs.size();
    for (size_t i = 0; i < rs.size(); ++i) s.varR += (rs[i] - mr) * (rs[i] - mr);
    if (rs.size() > 1) s.varR /= (double)(rs.size() - 1);
    return s;
}

static void DumpPng(const std::wstring& path, const std::vector<uint32_t>& px, int w, int h) {
    av::img::SavePngFromBgra(path, px.data(), w, h);
}

int main(int argc, char** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    std::string root;
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a.rfind("--root=", 0) == 0) root = a.substr(7);
    }
    if (root.empty()) root = "E:\\workspace\\myspace\\godot_game\\biu-biu-origin\\素材";
    std::wstring proot = W(root);
    std::wstring outDir = L"E:\\workspace\\myspace\\godot_game\\biu-biu-origin\\tools\\asset_viewer\\build\\out\\_probe";
    av::img::EnsureDir(outDir);

    const wchar_t* cases[] = {L"卡通人物带全套动画\\史蒂夫.glb", L"海盗场景带动画\\Anne.glb",
                              L"RPG武器\\Coin.glb", L"POLYGON_Starter_SourceFiles_v2\\SourceFiles\\FBX\\SM_Bean_Cop_01.fbx"};
    for (int ci = 0; ci < 4; ++ci) {
        std::wstring full = proot + L"\\" + cases[ci];
        av::ModelData md;
        bool ok = av::LoadModel(U8(full), &md);
        std::printf("\n==== [%d] %s load=%d ok=%d verts=%zu tris=%zu mats=%zu bones=%zu clips=%zu tex=%zu skin=%zu\n",
                    ci, U8(cases[ci]).c_str(), (int)ok, (int)md.ok, md.mesh.vertices.size(),
                    md.mesh.indices.size() / 3, md.mesh.materials.size(), md.bones.size(),
                    md.clips.size(), md.textures.size(), md.skin.size());
        // AABB
        float mn[3] = {1e30f, 1e30f, 1e30f}, mx[3] = {-1e30f, -1e30f, -1e30f};
        for (auto& v : md.mesh.vertices) {
            float p[3] = {v.px, v.py, v.pz};
            for (int k = 0; k < 3; ++k) { if (p[k] < mn[k]) mn[k] = p[k]; if (p[k] > mx[k]) mx[k] = p[k]; }
        }
        std::printf("  AABB min=(%.3f %.3f %.3f) max=(%.3f %.3f %.3f) size=(%.3f %.3f %.3f)\n",
                    mn[0], mn[1], mn[2], mx[0], mx[1], mx[2], mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]);
        // 纹理统计
        int baked = 0, okinfo = 0;
        for (size_t t = 0; t < md.textures.size(); ++t) {
            if (md.textures[t].baked) ++baked;
            if (md.textures[t].ok) ++okinfo;
            if (t < 3) std::printf("  tex[%zu] name=%s ok=%d baked=%d %dx%d preview=%dx%d bytes=%zu\n", t,
                                   md.textures[t].name.c_str(), (int)md.textures[t].ok,
                                   (int)md.textures[t].baked, md.textures[t].width, md.textures[t].height,
                                   md.textures[t].previewW, md.textures[t].previewH, md.textures[t].preview.size());
        }
        std::printf("  textures: ok=%d baked=%d / %zu\n", okinfo, baked, md.textures.size());
        for (size_t m = 0; m < md.mesh.materials.size() && m < 4; ++m) {
            std::printf("  mat[%zu] name=%s diffuseTex=%d base=(%.3f %.3f %.3f %.3f) rough=%.2f\n", m,
                        md.mesh.materials[m].name.c_str(), md.mesh.materials[m].diffuseTex,
                        md.mesh.materials[m].baseColor[0], md.mesh.materials[m].baseColor[1],
                        md.mesh.materials[m].baseColor[2], md.mesh.materials[m].baseColor[3],
                        md.mesh.materials[m].roughness);
        }
        // 采样预览纹理的中心色（看纹理本身有颜色吗）
        if (!md.textures.empty() && md.textures[0].baked && md.textures[0].preview.size() >= 4) {
            const av::TextureInfo& tx = md.textures[0];
            size_t mx2 = (size_t)tx.previewW * tx.previewH / 2;
            std::printf("  tex[0] center rgba = %u %u %u %u\n", tx.preview[mx2 * 4], tx.preview[mx2 * 4 + 1],
                        tx.preview[mx2 * 4 + 2], tx.preview[mx2 * 4 + 3]);
        }
        // UV 范围
        if (!md.mesh.vertices.empty()) {
            float umn = 1e9, umx = -1e9, vmn = 1e9, vmx = -1e9;
            int zeroUv = 0;
            for (auto& v : md.mesh.vertices) {
                if (v.u < umn) umn = v.u;
                if (v.u > umx) umx = v.u;
                if (v.v < vmn) vmn = v.v;
                if (v.v > vmx) vmx = v.v;
                if (v.u == 0.0f && v.v == 0.0f) ++zeroUv;
            }
            std::printf("  UV u=[%.3f %.3f] v=[%.3f %.3f] zeroUV=%d/%zu\n", umn, umx, vmn, vmx, zeroUv,
                        md.mesh.vertices.size());
        }
        // 骨骼/动画
        for (size_t b = 0; b < md.bones.size() && b < 4; ++b) {
            std::printf("  bone[%zu] name=%s parent=%d local[12..14]=(%.3f %.3f %.3f) bind[0]=%.3f bind[5]=%.3f\n",
                        b, md.bones[b].name.c_str(), md.bones[b].parent, md.bones[b].local[12],
                        md.bones[b].local[13], md.bones[b].local[14], md.bones[b].bind[0], md.bones[b].bind[5]);
        }
        for (size_t c = 0; c < md.clips.size() && c < 3; ++c) {
            size_t nonEmpty = 0;
            for (auto& tr : md.clips[c].tracks) if (!tr.empty()) ++nonEmpty;
            std::printf("  clip[%zu] name=%s dur=%.3f tracks=%zu nonEmpty=%zu\n", c,
                        md.clips[c].name.c_str(), md.clips[c].duration, md.clips[c].tracks.size(), nonEmpty);
        }

        // ---- CPU 缩略图（参考实现）----
        {
            av::ThumbOptions opt;
            opt.size = 256;
            opt.samplesPerAxis = 2;
            std::vector<uint32_t> px;
            bool rok = av::RenderThumbnail(md, opt, &px);
            Stat s = Analyze(px, 256, 256);
            std::printf("  [CPU ] render=%d nonBg=%.2f%% center=(%.1f,%.1f) bbox=[%.0f,%.0f]-[%.0f,%.0f] varR=%.1f meanRGB=(%.0f,%.0f,%.0f)\n",
                        (int)rok, 100.0 * s.nonBg / (256.0 * 256.0), s.cx, s.cy, s.minX, s.minY, s.maxX, s.maxY,
                        s.varR, s.sumR / 65536.0, s.sumG / 65536.0, s.sumB / 65536.0);
            wchar_t nm[64];
            ::swprintf_s(nm, L"\\cpu_%d.png", ci);
            DumpPng(outDir + nm, px, 256, 256);
        }

        // ---- D3D 预览 ----
        {
            av::D3DRenderer r;
            bool ini = r.Initialize(nullptr, 800, 520);
            std::printf("  [D3D ] init=%d err=%s\n", (int)ini, r.GetStats().lastError.c_str());
            if (ini) {
                r.SetModel(md);
                r.ResetCamera();
                r.RenderFrame();
                std::vector<uint32_t> px;
                int w = 0, h = 0;
                r.Readback(&px, &w, &h);
                Stat s = Analyze(px, w, h);
                std::printf("  [D3D ] drawcalls=%u tris=%u tex=%u nonBg=%.2f%% center=(%.1f,%.1f) bbox=[%.0f,%.0f]-[%.0f,%.0f] w=%.0f h=%.0f varR=%.1f meanRGB=(%.0f,%.0f,%.0f)\n",
                            r.GetStats().drawCalls, r.GetStats().triangles, r.GetStats().textureCount,
                            100.0 * s.nonBg / ((double)w * h), s.cx, s.cy, s.minX, s.minY, s.maxX, s.maxY,
                            s.maxX - s.minX, s.maxY - s.minY, s.varR, s.sumR / ((double)w * h),
                            s.sumG / ((double)w * h), s.sumB / ((double)w * h));
                wchar_t nm[64];
                ::swprintf_s(nm, L"\\d3d_%d.png", ci);
                DumpPng(outDir + nm, px, w, h);
                // 动画：t=0 与 t=dur/2 的顶点质心差
                if (!md.clips.empty() && !md.bones.empty()) {
                    std::vector<float> wm, sm;
                    av::SampleClipWorld(md, 0, 0.0, &wm);
                    av::BuildSkinMatrices(md, wm, &sm);
                    r.SetSkinMatrices(&sm);
                    r.RenderFrame();
                    std::vector<uint32_t> p0;
                    int w0 = 0, h0 = 0;
                    r.Readback(&p0, &w0, &h0);
                    double dur = md.clips[0].duration;
                    av::SampleClipWorld(md, 0, dur * 0.5, &wm);
                    av::BuildSkinMatrices(md, wm, &sm);
                    r.SetSkinMatrices(&sm);
                    r.RenderFrame();
                    std::vector<uint32_t> p1;
                    int w1 = 0, h1 = 0;
                    r.Readback(&p1, &w1, &h1);
                    size_t diff = 0;
                    for (size_t i = 0; i < p0.size() && i < p1.size(); ++i)
                        if ((p0[i] & 0xFFFFFF) != (p1[i] & 0xFFFFFF)) ++diff;
                    Stat s0 = Analyze(p0, w0, h0), s1 = Analyze(p1, w1, h1);
                    std::printf("  [D3D ] anim t=0 vs t=%.2f: diffPix=%.2f%% centroid (%.1f,%.1f)->(%.1f,%.1f) bboxY %.0f..%.0f -> %.0f..%.0f\n",
                                dur * 0.5, 100.0 * diff / (double)p0.size(), s0.cx, s0.cy, s1.cx, s1.cy,
                                s0.minY, s0.maxY, s1.minY, s1.maxY);
                    wchar_t nm2[64];
                    ::swprintf_s(nm2, L"\\d3d_%d_animhalf.png", ci);
                    DumpPng(outDir + nm2, p1, w1, h1);
                    // 蒙皮矩阵合理性：skinMat 平移量级
                    double maxT = 0;
                    for (size_t b = 0; b < sm.size() / 16; ++b) {
                        double t0 = sm[b * 16 + 12], t1 = sm[b * 16 + 13], t2 = sm[b * 16 + 14];
                        double m = std::sqrt(t0 * t0 + t1 * t1 + t2 * t2);
                        if (m > maxT) maxT = m;
                    }
                    std::printf("  [D3D ] skinMat max translation magnitude = %.3f (AABB diag ~%.2f)\n", maxT,
                                std::sqrt((mx[0] - mn[0]) * (mx[0] - mn[0]) + (mx[1] - mn[1]) * (mx[1] - mn[1]) +
                                          (mx[2] - mn[2]) * (mx[2] - mn[2])));
                }
                r.Shutdown();
            }
        }
    }
    ::CoUninitialize();
    return 0;
}
