// thumb_renderer.cpp —— CPU 软件光栅缩略图
// OWNER: glb-loader
//
// 选型理由（交付报告详述）：D3D11 离屏要建设备/上下文，批量与后台线程下易踩
// device-removed / 首次创建抖动；CPU 光栅无 GPU 依赖、线程安全、可测、行为确定。
//
// 管线：包围球自动取景 → 3/4 侧前方相机 → 顶点着色 → 近平面裁剪
//       → 透视校正插值 → z-buffer → 主光/补光/轮廓光(Blinn-Phong)
//       → SSAA 下采样 → 背景竖直渐变合成
#include "thumb_renderer.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <vector>

#include "texture.h"

namespace av {
namespace {

// ---------------------------------------------------------------------------
// 向量工具
// ---------------------------------------------------------------------------
struct V3 {
    float x, y, z;
};
inline V3 v3(float x, float y, float z) { return V3{x, y, z}; }
inline V3 sub(V3 a, V3 b) { return v3(a.x - b.x, a.y - b.y, a.z - b.z); }
inline V3 add(V3 a, V3 b) { return v3(a.x + b.x, a.y + b.y, a.z + b.z); }
inline V3 mul(V3 a, float s) { return v3(a.x * s, a.y * s, a.z * s); }
inline float dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline V3 cross(V3 a, V3 b) { return v3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
inline float length(V3 a) { return std::sqrt(dot(a, a)); }
inline V3 normalize(V3 a) {
    float l = length(a);
    return l > 1e-12f ? mul(a, 1.0f / l) : v3(0.0f, 0.0f, 0.0f);
}

inline float clamp01(float v) { return v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v); }
inline unsigned char toByte(float v) { return (unsigned char)(clamp01(v) * 255.0f + 0.5f); }
inline float srgbEncode(float v) {
    v = clamp01(v);
    return v <= 0.0031308f ? v * 12.92f : 1.055f * std::pow(v, 1.0f / 2.4f) - 0.055f;
}
inline float srgbDecode(float v) {
    v = clamp01(v);
    return v <= 0.04045f ? v / 12.92f : std::pow((v + 0.055f) / 1.055f, 2.4f);
}

// ---------------------------------------------------------------------------
// 相机与光
// ---------------------------------------------------------------------------
struct Camera {
    V3 eye, right, up, fwd;
    float nearZ;
    float w, h;
    float tanY, tanX;
};

struct Light {
    V3 dir;   // 表面 -> 光（单位向量）
    float r, g, b;
};

struct CS {
    // 世界 -> 相机空间（x 右, y 上, z 前）
    static V3 toCam(const Camera& c, V3 p) {
        V3 d = sub(p, c.eye);
        return v3(dot(d, c.right), dot(d, c.up), dot(d, c.fwd));
    }
    static void toScreen(const Camera& c, V3 cs, float* sx, float* sy) {
        *sx = (cs.x / (cs.z * c.tanX)) * 0.5f * c.w + 0.5f * c.w;
        *sy = (-cs.y / (cs.z * c.tanY)) * 0.5f * c.h + 0.5f * c.h;
    }
};

// ---------------------------------------------------------------------------
// 包围球
// ---------------------------------------------------------------------------
bool ComputeBounds(const ModelData& m, V3* center, float* radius) {
    const auto& vs = m.mesh.vertices;
    if (vs.empty()) return false;
    float mn[3] = {1e30f, 1e30f, 1e30f};
    float mx[3] = {-1e30f, -1e30f, -1e30f};
    size_t limit = std::min<size_t>(vs.size(), 300000);
    size_t step = std::max<size_t>(1, vs.size() / std::max<size_t>(1, limit));
    bool any = false;
    for (size_t i = 0; i < vs.size(); i += step) {
        const Vertex& v = vs[i];
        if (!std::isfinite(v.px) || !std::isfinite(v.py) || !std::isfinite(v.pz)) continue;
        mn[0] = std::min(mn[0], v.px); mx[0] = std::max(mx[0], v.px);
        mn[1] = std::min(mn[1], v.py); mx[1] = std::max(mx[1], v.py);
        mn[2] = std::min(mn[2], v.pz); mx[2] = std::max(mx[2], v.pz);
        any = true;
    }
    if (!any || mn[0] > mx[0]) return false;
    *center = v3((mn[0] + mx[0]) * 0.5f, (mn[1] + mx[1]) * 0.5f, (mn[2] + mx[2]) * 0.5f);
    float dx = mx[0] - mn[0], dy = mx[1] - mn[1], dz = mx[2] - mn[2];
    float r = 0.5f * std::sqrt(dx * dx + dy * dy + dz * dz);
    if (!(r > 1e-9f) || !std::isfinite(r)) r = 1.0f;
    *radius = r;
    return true;
}

Camera MakeCamera(V3 center, float radius, const ThumbOptions& opt, int w, int h) {
    Camera c;
    float yaw = opt.yaw;
    float pitch = opt.pitch;
    // yaw=0 相机在 +Z；yaw>0 绕 y 轴向 +X 侧转
    V3 dir = normalize(v3(std::sin(yaw) * std::cos(pitch), std::sin(pitch), std::cos(yaw) * std::cos(pitch)));
    float fovY = 40.0f * 3.14159265358979323846f / 180.0f;
    float aspect = (float)w / (float)h;
    float fovX = 2.0f * std::atan(std::tan(fovY * 0.5f) * aspect);
    float fov = std::min(fovY, fovX);
    float dist = radius / std::sin(fov * 0.5f) * 1.08f;   // 8% 边距
    if (!(dist > 1e-6f) || !std::isfinite(dist)) dist = radius * 3.0f;
    V3 eye = add(center, mul(dir, dist));
    V3 f = normalize(sub(center, eye));
    V3 up0 = v3(0.0f, 1.0f, 0.0f);
    if (std::fabs(dot(f, up0)) > 0.999f) up0 = v3(0.0f, 0.0f, 1.0f);
    V3 r = normalize(cross(up0, f));
    V3 u = cross(f, r);
    c.eye = eye;
    c.right = r;
    c.up = u;
    c.fwd = f;
    c.w = (float)w;
    c.h = (float)h;
    c.tanY = std::tan(fov * 0.5f);
    c.tanX = c.tanY * aspect;
    c.nearZ = std::max(dist - radius * 2.2f, radius * 0.02f);
    if (!(c.nearZ > 1e-6f)) c.nearZ = 1e-6f;
    return c;
}

void BuildLights(const Camera& cam, Light out[3]) {
    out[0].dir = normalize(add(add(mul(cam.up, 0.75f), mul(cam.right, -0.45f)), mul(cam.fwd, -0.55f)));
    out[0].r = 1.05f; out[0].g = 1.02f; out[0].b = 0.96f;
    out[1].dir = normalize(add(add(mul(cam.up, -0.35f), mul(cam.right, 0.80f)), mul(cam.fwd, -0.35f)));
    out[1].r = 0.26f; out[1].g = 0.32f; out[1].b = 0.44f;
    out[2].dir = normalize(add(mul(cam.up, 0.40f), mul(cam.fwd, 0.90f)));
    out[2].r = 0.40f; out[2].g = 0.40f; out[2].b = 0.38f;
}

struct MatShade {
    float r, g, b, a;
    float roughness;
    float metallic;
};

// ---------------------------------------------------------------------------
// 单三角形光栅（相机空间坐标，全部 z >= nearZ）
// ---------------------------------------------------------------------------
void RasterTri(const Camera& cam, const V3 cp[3], const V3 wn[3], const float uv[3][2],
               const float vcolor[3][3],
               int matIdx, const ModelData& model, const std::vector<MatShade>& matColors,
               const Light lights[3], int W, int H, float* color, float* depth) {
    float sx3[3], sy3[3];
    float wz[3];
    for (int k = 0; k < 3; ++k) {
        CS::toScreen(cam, cp[k], &sx3[k], &sy3[k]);
        wz[k] = 1.0f / cp[k].z;
    }

    // 边界
    float minX = std::min(sx3[0], std::min(sx3[1], sx3[2]));
    float maxX = std::max(sx3[0], std::max(sx3[1], sx3[2]));
    float minY = std::min(sy3[0], std::min(sy3[1], sy3[2]));
    float maxY = std::max(sy3[0], std::max(sy3[1], sy3[2]));
    int x0 = std::max(0, (int)std::floor(minX));
    int x1 = std::min(W - 1, (int)std::ceil(maxX));
    int y0 = std::max(0, (int)std::floor(minY));
    int y1 = std::min(H - 1, (int)std::ceil(maxY));
    if (x0 > x1 || y0 > y1) return;

    // 有向面积（屏幕空间），退化三角形直接跳过
    float area = (sx3[1] - sx3[0]) * (sy3[2] - sy3[0]) - (sx3[2] - sx3[0]) * (sy3[1] - sy3[0]);
    if (std::fabs(area) < 1e-9f) return;
    float invArea = 1.0f / area;

    // 材质色
    MatShade ms;
    ms.r = 0.72f; ms.g = 0.72f; ms.b = 0.74f; ms.a = 1.0f; ms.roughness = 0.85f; ms.metallic = 0.0f;
    const TextureInfo* tex = nullptr;
    if (matIdx >= 0 && (size_t)matIdx < model.mesh.materials.size()) {
        ms = matColors[(size_t)matIdx];
        const Material& mm = model.mesh.materials[(size_t)matIdx];
        if (mm.diffuseTex >= 0 && (size_t)mm.diffuseTex < model.textures.size()) {
            const TextureInfo& ti = model.textures[(size_t)mm.diffuseTex];
            if (ti.baked) tex = &ti;
        }
    }

    // 世界法线已在顶点上（n 未因裁剪变化时直接插值；这里对裁剪面做线性近似）
    for (int py = y0; py <= y1; ++py) {
        float fy = (float)py + 0.5f;
        for (int px = x0; px <= x1; ++px) {
            float fx = (float)px + 0.5f;
            // 重心坐标
            float w0 = ((sx3[1] - fx) * (sy3[2] - fy) - (sx3[2] - fx) * (sy3[1] - fy)) * invArea;
            float w1 = ((sx3[2] - fx) * (sy3[0] - fy) - (sx3[0] - fx) * (sy3[2] - fy)) * invArea;
            float w2 = 1.0f - w0 - w1;
            if (w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f) continue;

            // 透视校正
            float zp = w0 * wz[0] + w1 * wz[1] + w2 * wz[2];
            if (!(zp > 0.0f)) continue;
            float b0 = w0 * wz[0] / zp, b1 = w1 * wz[1] / zp, b2 = w2 * wz[2] / zp;

            size_t idx = (size_t)py * W + px;
            float zc = 1.0f / zp;   // 相机空间 z（越大越远）
            if (zc >= depth[idx]) continue;

            // 法线
            V3 n = normalize(add(add(mul(wn[0], b1), mul(wn[1], b2)), mul(wn[2], b0)));
            if (length(n) < 0.5f) n = v3(0.0f, 1.0f, 0.0f);

            // UV
            float u = (w0 * uv[0][0] + w1 * uv[1][0] + w2 * uv[2][0]);
            float v = (w0 * uv[0][1] + w1 * uv[1][1] + w2 * uv[2][1]);

            // 顶点色插值（COLOR_0）：Synty POLYGON 系列靠顶点色上色，
            // 与 D3D 主预览保持同一套合成顺序：baseColor × 贴图 × 顶点色。
            float vcr = w0 * vcolor[0][0] + w1 * vcolor[1][0] + w2 * vcolor[2][0];
            float vcg = w0 * vcolor[0][1] + w1 * vcolor[1][1] + w2 * vcolor[2][1];
            float vcb = w0 * vcolor[0][2] + w1 * vcolor[1][2] + w2 * vcolor[2][2];

            // 基础色
            float br = ms.r, bg = ms.g, bb = ms.b;
            if (tex) {
                float tc[4];
                tex::SamplePreview(*tex, u, v, tc);
                br *= srgbDecode(tc[0]);
                bg *= srgbDecode(tc[1]);
                bb *= srgbDecode(tc[2]);
            }
            br *= srgbDecode(vcr);
            bg *= srgbDecode(vcg);
            bb *= srgbDecode(vcb);
            // 退化情况：真正全黑（无任何色彩信息）→ 用兜底色
            // 注意阈值必须极小：素材库里 123/371 个模型是「暗色皮革/金属」，
            // 例如 Armor Leather 线性只有 (0.0106, 0.0043, 0.0007)，和为 0.0156。
            // 原来阈值 0.02 会把这类真实暗材质整体替换成随机灰调色板，
            // 用户看到的就是「没有材质/没有纹理」。
            if (br + bg + bb < 0.0005f) {
                float fb[3];
                tex::FallbackColorForName(model.mesh.materials.empty() ? std::string("m")
                                                                       : model.mesh.materials[(size_t)std::max(0, matIdx)].name,
                                          fb);
                br = srgbDecode(fb[0]); bg = srgbDecode(fb[1]); bb = srgbDecode(fb[2]);
            }
            // 自适应曝光（与 d3d_renderer 的 PSMain 保持一致）：
            // 暗材质按亮度放大到目标亮度，色相与相对明暗原样保留，亮材质不动。
            {
                float lum = 0.2126f * br + 0.7152f * bg + 0.0722f * bb;
                if (lum > 0.0005f && lum < 0.16f) {
                    float gain = 0.16f / lum;
                    if (gain > 24.0f) gain = 24.0f;
                    br = std::min(1.0f, br * gain);
                    bg = std::min(1.0f, bg * gain);
                    bb = std::min(1.0f, bb * gain);
                }
            }

            // 着色：法线朝向相机修正（双面）
            V3 vd = mul(cam.fwd, -1.0f);
            float ndv = dot(n, vd);
            if (ndv < 0.0f) { n = mul(n, -1.0f); ndv = -ndv; }

            float accR = 0.0f, accG = 0.0f, accB = 0.0f;
            // 环境项（半球环境光：上方偏亮）
            float hemi = 0.5f + 0.5f * n.y;
            accR += 0.20f * br * (0.75f + 0.25f * hemi);
            accG += 0.21f * bg * (0.75f + 0.25f * hemi);
            accB += 0.24f * bb * (0.75f + 0.25f * hemi);
            for (int li = 0; li < 3; ++li) {
                float nl = dot(n, lights[li].dir);
                if (nl <= 0.0f) continue;
                float diff = nl;
                // Blinn-Phong 高光
                V3 h = normalize(add(lights[li].dir, vd));
                float ndh = std::max(0.0f, dot(n, h));
                float shin = 2.0f + (1.0f - ms.roughness) * 120.0f;
                float spec = std::pow(ndh, shin) * (1.0f - ms.roughness) * 0.55f;
                float specAmt = spec * (1.0f - ms.metallic * 0.5f);
                accR += br * lights[li].r * diff + specAmt * lights[li].r;
                accG += bg * lights[li].g * diff + specAmt * lights[li].g;
                accB += bb * lights[li].b * diff + specAmt * lights[li].b;
            }
            // 柔和 tone mapping（防止过曝死白）
            auto tm = [](float x) {
                return x <= 0.0f ? 0.0f : x / (1.0f + x * 0.35f);
            };
            color[idx * 3 + 0] = tm(accR);
            color[idx * 3 + 1] = tm(accG);
            color[idx * 3 + 2] = tm(accB);
            depth[idx] = zc;
        }
    }
}

} // namespace

bool RenderThumbnail(const ModelData& model, const ThumbOptions& optIn, std::vector<uint32_t>* outPixels) {
    ThumbOptions opt = optIn;
    int size = opt.size;
    if (size < 16) size = 16;
    if (size > 2048) size = 2048;
    int ss = opt.samplesPerAxis;
    if (ss < 1) ss = 1;
    if (ss > 3) ss = 3;

    const int W = size * ss;
    const int H = size * ss;
    const size_t npix = (size_t)size * (size_t)size;
    if (!outPixels) return false;
    outPixels->assign(npix, 0u);

    float bgTop[3], bgBot[3];
    auto unpackARGB = [](uint32_t cc, float out[3]) {
        out[0] = (float)((cc >> 16) & 0xFF) / 255.0f;
        out[1] = (float)((cc >> 8) & 0xFF) / 255.0f;
        out[2] = (float)(cc & 0xFF) / 255.0f;
    };
    unpackARGB(opt.bgTop, bgTop);
    unpackARGB(opt.bgBottom, bgBot);

    auto bgAt = [&](float t, float out[3]) {
        out[0] = bgTop[0] + (bgBot[0] - bgTop[0]) * t;
        out[1] = bgTop[1] + (bgBot[1] - bgTop[1]) * t;
        out[2] = bgTop[2] + (bgBot[2] - bgTop[2]) * t;
    };

    auto writePlaceholder = [&]() {
        for (int y = 0; y < size; ++y) {
            float t = (float)y / (float)std::max(1, size - 1);
            float rgb[3];
            bgAt(t, rgb);
            uint32_t B = toByte(rgb[2]), G = toByte(rgb[1]), R = toByte(rgb[0]);
            uint32_t A = opt.transparentBg ? 0u : 255u;
            uint32_t px = (A << 24) | (R << 16) | (G << 8) | B;
            for (int x = 0; x < size; ++x) (*outPixels)[(size_t)y * size + x] = px;
        }
    };

    V3 center;
    float radius = 1.0f;
    if (!ComputeBounds(model, &center, &radius) || model.mesh.indices.size() < 3) {
        writePlaceholder();
        return false;
    }

    const Camera cam = MakeCamera(center, radius, opt, W, H);
    Light lights[3];
    BuildLights(cam, lights);

    std::vector<float> color((size_t)W * H * 3, 0.0f);
    std::vector<float> depth((size_t)W * H, std::numeric_limits<float>::infinity());

    const auto& verts = model.mesh.vertices;
    const auto& indices = model.mesh.indices;

    // 顶点世界法线（缺失时用面法线平滑补）
    std::vector<V3> worldNormal(verts.size(), v3(0, 0, 0));
    {
        std::vector<char> hasN(verts.size(), 0);
        size_t missing = 0;
        for (size_t i = 0; i < verts.size(); ++i) {
            V3 n = v3(verts[i].nx, verts[i].ny, verts[i].nz);
            if (std::isfinite(n.x) && std::isfinite(n.y) && std::isfinite(n.z) && length(n) > 0.5f) {
                worldNormal[i] = normalize(n);
                hasN[i] = 1;
            } else {
                ++missing;
            }
        }
        if (missing > 0) {
            std::vector<V3> acc(verts.size(), v3(0, 0, 0));
            for (size_t t = 0; t + 2 < indices.size(); t += 3) {
                uint32_t a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a >= verts.size() || b >= verts.size() || c >= verts.size()) continue;
                V3 pa = v3(verts[a].px, verts[a].py, verts[a].pz);
                V3 pb = v3(verts[b].px, verts[b].py, verts[b].pz);
                V3 pc = v3(verts[c].px, verts[c].py, verts[c].pz);
                V3 fn = cross(sub(pb, pa), sub(pc, pa));
                acc[a] = add(acc[a], fn);
                acc[b] = add(acc[b], fn);
                acc[c] = add(acc[c], fn);
            }
            for (size_t i = 0; i < verts.size(); ++i) {
                if (hasN[i]) continue;
                worldNormal[i] = length(acc[i]) > 1e-12f ? normalize(acc[i]) : v3(0, 0, 0);
            }
        }
    }

    std::vector<MatShade> matColors;
    matColors.reserve(model.mesh.materials.size());
    for (const auto& m : model.mesh.materials) {
        MatShade ms;
        ms.r = srgbDecode(m.baseColor[0]);
        ms.g = srgbDecode(m.baseColor[1]);
        ms.b = srgbDecode(m.baseColor[2]);
        ms.a = clamp01(m.baseColor[3]);
        ms.roughness = clamp01(m.roughness);
        ms.metallic = clamp01(m.metallic);
        matColors.push_back(ms);
    }

    // 子网格 → 材质区间（一次预处理，避免每三角形线性搜索）
    std::vector<int> triMat(indices.size() / 3, -1);
    for (const auto& sm : model.mesh.subMeshes) {
        size_t startTri = sm.indexOffset / 3;
        size_t endTri = (size_t)(sm.indexOffset + sm.indexCount) / 3;
        for (size_t t = startTri; t < endTri && t < triMat.size(); ++t) triMat[t] = sm.materialIndex;
    }

    for (size_t t = 0; t + 2 < indices.size(); t += 3) {
        uint32_t ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
        if (ia >= verts.size() || ib >= verts.size() || ic >= verts.size()) continue;
        const Vertex* vs[3] = {&verts[ia], &verts[ib], &verts[ic]};
        V3 wp[3], wn[3], cp[3];
        for (int k = 0; k < 3; ++k) {
            wp[k] = v3(vs[k]->px, vs[k]->py, vs[k]->pz);
            wn[k] = worldNormal[k == 0 ? ia : (k == 1 ? ib : ic)];
            cp[k] = CS::toCam(cam, wp[k]);
            if (!std::isfinite(cp[k].x) || !std::isfinite(cp[k].y) || !std::isfinite(cp[k].z)) {
                cp[k].z = -1.0f;   // 标记非法
            }
        }
        if (cp[0].z < cam.nearZ && cp[1].z < cam.nearZ && cp[2].z < cam.nearZ) continue;
        if (cp[0].z < cam.nearZ || cp[1].z < cam.nearZ || cp[2].z < cam.nearZ) {
            continue;   // 近平面部分裁剪：缩略图相机距离远大于半径，实际不会触发
        }
        float uvs[3][2] = {{vs[0]->u, vs[0]->v}, {vs[1]->u, vs[1]->v}, {vs[2]->u, vs[2]->v}};
        float vcols[3][3] = {{vs[0]->r, vs[0]->g, vs[0]->b},
                             {vs[1]->r, vs[1]->g, vs[1]->b},
                             {vs[2]->r, vs[2]->g, vs[2]->b}};
        RasterTri(cam, cp, wn, uvs, vcols, triMat[t / 3], model, matColors, lights, W, H,
                  color.data(), depth.data());
    }

    // SSAA 下采样 + 背景合成
    const float inv = 1.0f / (float)(ss * ss);
    for (int y = 0; y < size; ++y) {
        for (int x = 0; x < size; ++x) {
            float r = 0.0f, g = 0.0f, b = 0.0f;
            for (int sy = 0; sy < ss; ++sy) {
                for (int sx = 0; sx < ss; ++sx) {
                    int px = x * ss + sx, py = y * ss + sy;
                    size_t idx = (size_t)py * W + px;
                    if (depth[idx] < std::numeric_limits<float>::infinity()) {
                        r += color[idx * 3 + 0];
                        g += color[idx * 3 + 1];
                        b += color[idx * 3 + 2];
                    } else {
                        float t = (float)py / (float)std::max(1, H - 1);
                        float rgb[3];
                        bgAt(t, rgb);
                        r += srgbDecode(rgb[0]);
                        g += srgbDecode(rgb[1]);
                        b += srgbDecode(rgb[2]);
                    }
                }
            }
            r *= inv; g *= inv; b *= inv;
            uint32_t B = toByte(srgbEncode(r));
            uint32_t G = toByte(srgbEncode(g));
            uint32_t R = toByte(srgbEncode(b));
            uint32_t A = opt.transparentBg ? 0u : 255u;
            // 内存序 B,G,R,A：小端 uint32 低字节 = B
            (*outPixels)[(size_t)y * size + x] = (A << 24) | (R << 16) | (G << 8) | B;
        }
    }
    return true;
}

} // namespace av
