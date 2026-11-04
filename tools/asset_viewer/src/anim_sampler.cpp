// anim_sampler.cpp —— 动画采样与蒙皮矩阵
// OWNER: glb-loader
//
// 契约见 model_data.h：
//   SampleClipLocals : t 秒时每条骨骼的局部矩阵（len = bones*16，行优先）
//   SampleClipWorld  : 由局部按 parent 级联出的世界矩阵（NoLoopLeftThenRight）
//   BuildSkinMatrices: skinMat[j] = world[j] * bind[j]
//
// 约定：全部为「行向量 × 矩阵」（v' = v * M），mat::multiply(a,b) 表示先 a 后 b。
// 因此世界矩阵 = local[自己] * local[父] * ... * local[根]（自内向外），
// 蒙皮 = bind（mesh 空间 → 骨骼绑定空间）再乘 world（骨骼当前世界位姿）。
#include "anim_sampler.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace av {
namespace {

// 关键帧线性插值 + 四元数 nlerp/slerp；矩阵按分量线性插值（对局部矩阵足够稳）
inline double clampd(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

bool IsQuat(const float* a, const float* b) {
    // 粗判：都是可归一四元数（长度接近 1）
    auto len = [](const float* q) { return std::sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]); };
    float la = len(a), lb = len(b);
    return std::fabs(la - 1.0f) < 0.02f && std::fabs(lb - 1.0f) < 0.02f;
}

void InterpQuat(const float* qa, const float* qb, float t, float* out) {
    // xyzw，slerp（退化时 nlerp）
    double d = (double)qa[0] * qb[0] + (double)qa[1] * qb[1] + (double)qa[2] * qb[2] + (double)qa[3] * qb[3];
    double sa = 1.0, sb = 1.0;
    if (d < 0.0) { d = -d; sb = -1.0; }
    if (d < 0.9995) {
        double th = std::acos(clampd(d, -1.0, 1.0));
        double s = std::sin(th);
        if (std::fabs(s) > 1e-9) {
            sa = std::sin((1.0 - t) * th) / s;
            sb = sb * std::sin(t * th) / s;
        }
    } else {
        sa = 1.0 - t;
        sb = sb * t;
    }
    float q[4];
    q[0] = (float)(sa * qa[0] + sb * qb[0]);
    q[1] = (float)(sa * qa[1] + sb * qb[1]);
    q[2] = (float)(sa * qa[2] + sb * qb[2]);
    q[3] = (float)(sa * qa[3] + sb * qb[3]);
    float l = std::sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
    if (l > 1e-9f) { q[0] /= l; q[1] /= l; q[2] /= l; q[3] /= l; }
    else { q[0] = qa[0]; q[1] = qa[1]; q[2] = qa[2]; q[3] = qa[3]; }
    std::memcpy(out, q, sizeof(q));
}

// 采样一条轨道（16 元素矩阵关键帧序列）到 t
void SampleTrack(const std::vector<KeyFrame>& tr, double t, float* out16) {
    if (tr.empty()) return;   // 调用方已填默认值
    if (tr.size() == 1 || t <= tr.front().time) {
        std::memcpy(out16, tr.front().local, sizeof(float) * 16);
        return;
    }
    if (t >= tr.back().time) {
        std::memcpy(out16, tr.back().local, sizeof(float) * 16);
        return;
    }
    // 二分找区间
    size_t lo = 0, hi = tr.size() - 1;
    while (hi - lo > 1) {
        size_t mid = (lo + hi) / 2;
        if ((double)tr[mid].time <= t) lo = mid;
        else hi = mid;
    }
    const KeyFrame& a = tr[lo];
    const KeyFrame& b = tr[hi];
    double span = (double)b.time - (double)a.time;
    float u = 0.0f;
    if (span > 1e-12) u = (float)(((double)t - (double)a.time) / span);
    u = (float)clampd((double)u, 0.0, 1.0);

    // 16 元素矩阵里，只有当两者都可视为「旋转块」时用四元数插值，
    // 否则逐分量线性插值 —— 兼顾 TRS 合成结果与任意矩阵轨道。
    // 判断依据：3x3 子块正交且末行为 (0,0,0,1) → 视为纯旋转+平移，用 slerp。
    bool pureRigid = true;
    for (int i = 0; i < 16 && pureRigid; ++i) {
        if (!std::isfinite(a.local[i]) || !std::isfinite(b.local[i])) pureRigid = false;
    }
    if (pureRigid) {
        // 末行 (0,0,0,1)
        if (std::fabs(a.local[12]) > 1e-4f || std::fabs(a.local[13]) > 1e-4f ||
            std::fabs(a.local[14]) > 1e-4f || std::fabs(a.local[15] - 1.0f) > 1e-4f ||
            std::fabs(b.local[12]) > 1e-4f || std::fabs(b.local[13]) > 1e-4f ||
            std::fabs(b.local[14]) > 1e-4f || std::fabs(b.local[15] - 1.0f) > 1e-4f) {
            pureRigid = false;
        }
    }
    if (pureRigid) {
        // 从 3x3 提取四元数（行优先）
        auto quatFromM = [](const float* m, float* q) {
            float t0 = m[0] + m[5] + m[10];
            if (t0 > 0.0f) {
                float s = std::sqrt(t0 + 1.0f) * 2.0f;
                q[3] = 0.25f * s;
                q[0] = (m[6] - m[9]) / s;
                q[1] = (m[8] - m[2]) / s;
                q[2] = (m[1] - m[4]) / s;
            } else if (m[0] > m[5] && m[0] > m[10]) {
                float s = std::sqrt(1.0f + m[0] - m[5] - m[10]) * 2.0f;
                q[3] = (m[6] - m[9]) / s;
                q[0] = 0.25f * s;
                q[1] = (m[4] + m[1]) / s;
                q[2] = (m[8] + m[2]) / s;
            } else if (m[5] > m[10]) {
                float s = std::sqrt(1.0f + m[5] - m[0] - m[10]) * 2.0f;
                q[3] = (m[8] - m[2]) / s;
                q[0] = (m[4] + m[1]) / s;
                q[1] = 0.25f * s;
                q[2] = (m[9] + m[6]) / s;
            } else {
                float s = std::sqrt(1.0f + m[10] - m[0] - m[5]) * 2.0f;
                q[3] = (m[1] - m[4]) / s;
                q[0] = (m[8] + m[2]) / s;
                q[1] = (m[9] + m[6]) / s;
                q[2] = 0.25f * s;
            }
        };
        float qa[4], qb[4], q[4];
        quatFromM(a.local, qa);
        quatFromM(b.local, qb);
        // 3x3 是否近似正交
        auto ortho = [](const float* v) {
            for (int r = 0; r < 3; ++r) {
                float len = std::sqrt(v[r * 4 + 0] * v[r * 4 + 0] + v[r * 4 + 1] * v[r * 4 + 1] +
                                      v[r * 4 + 2] * v[r * 4 + 2]);
                if (std::fabs(len - 1.0f) > 0.01f) return false;
            }
            return true;
        };
        if (ortho(a.local) && ortho(b.local)) {
            InterpQuat(qa, qb, u, q);
            // 3x3（行优先）
            float x = q[0], y = q[1], z = q[2], w = q[3];
            out16[0] = 1.0f - 2.0f * (y * y + z * z);
            out16[1] = 2.0f * (x * y + z * w);
            out16[2] = 2.0f * (x * z - y * w);
            out16[4] = 2.0f * (x * y - z * w);
            out16[5] = 1.0f - 2.0f * (x * x + z * z);
            out16[6] = 2.0f * (y * z + x * w);
            out16[8] = 2.0f * (x * z + y * w);
            out16[9] = 2.0f * (y * z - x * w);
            out16[10] = 1.0f - 2.0f * (x * x + y * y);
            // 平移（第 3 行）与末行
            for (int c = 0; c < 3; ++c) out16[12 + c] = a.local[12 + c] + (b.local[12 + c] - a.local[12 + c]) * u;
            out16[3] = out16[7] = out16[11] = 0.0f;
            out16[15] = 1.0f;
            return;
        }
    }
    // 逐分量
    for (int i = 0; i < 16; ++i) out16[i] = a.local[i] + (b.local[i] - a.local[i]) * u;
    (void)IsQuat;
}

} // namespace

// ---------------------------------------------------------------------------
bool SampleClipLocals(const ModelData& model, int clipIndex, double t, std::vector<float>* outLocals) {
    if (!outLocals) return false;
    const size_t nb = model.bones.size();
    outLocals->assign(nb * 16, 0.0f);
    for (size_t j = 0; j < nb; ++j) {
        float* dst = outLocals->data() + j * 16;
        // 默认用绑定姿势的局部矩阵（Bone.local）
        std::memcpy(dst, model.bones[j].local, sizeof(float) * 16);
    }
    if (clipIndex < 0 || (size_t)clipIndex >= model.clips.size()) return nb > 0;
    const Clip& clip = model.clips[(size_t)clipIndex];
    double tt = t;
    if (!std::isfinite(tt)) tt = 0.0;
    if (clip.duration > 0.0) {
        tt = std::fmod(tt, clip.duration);
        if (tt < 0.0) tt += clip.duration;
    } else {
        tt = 0.0;
    }
    const size_t nt = std::min(clip.tracks.size(), nb);
    for (size_t j = 0; j < nt; ++j) {
        if (clip.tracks[j].empty()) continue;
        SampleTrack(clip.tracks[j], tt, outLocals->data() + j * 16);
    }
    return nb > 0;
}

// ---------------------------------------------------------------------------
bool SampleClipWorld(const ModelData& model, int clipIndex, double t, std::vector<float>* outWorld) {
    if (!outWorld) return false;
    std::vector<float> locals;
    if (!SampleClipLocals(model, clipIndex, t, &locals)) {
        outWorld->clear();
        return false;
    }
    const size_t nb = model.bones.size();
    outWorld->assign(nb * 16, 0.0f);
    // 递归求世界矩阵（带深度上限防环）
    std::vector<int> state(nb, 0);   // 0=未处理 1=处理中 2=完成
    // 显式栈，避免深骨骼链爆栈
    struct Frame { size_t bone; int stage; };
    std::vector<Frame> stack;
    for (size_t start = 0; start < nb; ++start) {
        if (state[start] == 2) continue;
        stack.clear();
        stack.push_back({start, 0});
        int guard = 0;
        while (!stack.empty()) {
            if (++guard > (int)nb * 8 + 64) {
                // 环或异常：把剩余按局部矩阵处理
                for (size_t k = 0; k < stack.size(); ++k) {
                    size_t b = stack[k].bone;
                    std::memcpy(outWorld->data() + b * 16, locals.data() + b * 16, sizeof(float) * 16);
                    state[b] = 2;
                }
                stack.clear();
                break;
            }
            Frame& f = stack.back();
            if (f.stage == 0) {
                if (state[f.bone] == 2) { stack.pop_back(); continue; }
                state[f.bone] = 1;
                f.stage = 1;
                int p = model.bones[f.bone].parent;
                if (p >= 0 && (size_t)p < nb && state[(size_t)p] != 2) {
                    if (state[(size_t)p] == 1) {
                        // 检测到环：当前骨骼当作根
                        std::memcpy(outWorld->data() + f.bone * 16, locals.data() + f.bone * 16, sizeof(float) * 16);
                        state[f.bone] = 2;
                        stack.pop_back();
                        continue;
                    }
                    stack.push_back({(size_t)p, 0});
                    continue;
                }
                // 无父或父已完成 → 计算
                const float* L = locals.data() + f.bone * 16;
                int pp = model.bones[f.bone].parent;
                if (pp >= 0 && (size_t)pp < nb && state[(size_t)pp] == 2) {
                    mat::multiply(L, outWorld->data() + (size_t)pp * 16, outWorld->data() + f.bone * 16);
                } else {
                    std::memcpy(outWorld->data() + f.bone * 16, L, sizeof(float) * 16);
                }
                state[f.bone] = 2;
                stack.pop_back();
            } else {
                stack.pop_back();
            }
        }
    }
    return nb > 0;
}

// ---------------------------------------------------------------------------
void BuildSkinMatrices(const ModelData& model, const std::vector<float>& world,
                       std::vector<float>* outSkinMatrices) {
    if (!outSkinMatrices) return;
    const size_t nb = model.bones.size();
    outSkinMatrices->assign(nb * 16, 0.0f);
    if (nb == 0) return;
    const bool worldOk = world.size() >= nb * 16;
    for (size_t j = 0; j < nb; ++j) {
        float* dst = outSkinMatrices->data() + j * 16;
        if (!worldOk) {
            mat::identity(dst);
            continue;
        }
        // bind = mesh 空间 → 骨骼绑定空间；world = 骨骼当前世界位姿
        // 行向量约定下：p' = p * bind * world
        mat::multiply(world.data() + j * 16, model.bones[j].bind, dst);
    }
}

} // namespace av
