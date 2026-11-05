// model_data.h —— AssetViewer 共享数据结构与契约声明
// OWNER: Lead。任何字段/签名增删必须先改本文件 + ref/SHARED_SPEC.md，并通知所有模块。
// 约定：矩阵行优先（行向量 × 矩阵），y-up，右手系；像素对外 BGRA。
#pragma once

#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

namespace av {

// ---------------------------------------------------------------------------
// 顶点（CPU 光栅与 D3D11 共用布局，成员顺序即字节布局，禁止插入 padding）
// ---------------------------------------------------------------------------
struct Vertex {
    float px, py, pz;
    float nx, ny, nz;
    float u, v;
    // 顶点色（RGBA，线性 0..1）。Synty POLYGON 系列大量模型**不放贴图**，
    // 颜色烘在顶点上（glTF 的 COLOR_0 / FBX 的 LayerElementColor）。
    // 不保留这一项，这类模型就只能显示成灰色 —— 这正是「没有纹理」的根因。
    // 加载器在无 COLOR_0 时填 (1,1,1,1)，着色器用 `base *= vColor` 即可统一处理。
    float r, g, b, a;
};
static_assert(sizeof(Vertex) == 48, "Vertex must be 48 bytes (D3D11 interleaved)");

struct Material {
    std::string name;
    float baseColor[4] = {0.80f, 0.80f, 0.82f, 1.0f};
    int   diffuseTex = -1;      // ModelData::textures 下标，-1 表示无
    bool  doubleSided = false;
    // 光照相关（loader 从 glTF 的 metallicRoughness 或 FBX 材质推导）
    float metallic = 0.0f;
    float roughness = 0.85f;
};

// 一个 draw call（一个材质一个子网格）
struct SubMesh {
    int         materialIndex = -1;
    uint32_t    indexOffset = 0;
    uint32_t    indexCount  = 0;
    std::string name;
};

struct MeshData {
    std::vector<Vertex>   vertices;   // 全局顶点池
    std::vector<uint32_t> indices;    // 全局索引池（三角形列表）
    std::vector<SubMesh>  subMeshes;
    std::vector<Material> materials;

    int findMaterial(const std::string& n) const {
        for (size_t i = 0; i < materials.size(); ++i)
            if (materials[i].name == n) return (int)i;
        return -1;
    }
};

// ---------------------------------------------------------------------------
// 骨骼与动画
// ---------------------------------------------------------------------------
struct Bone {
    std::string name;
    int   parent = -1;
    float local[16]  = {1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1}; // 局部位姿（绑定姿态或首帧）
    float bind[16]   = {1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1}; // 逆绑定矩阵（inverse bind）
};

struct KeyFrame {
    double time = 0.0;
    float  local[16];            // 该时刻的局部 TRS 合成矩阵
};

struct Clip {
    std::string name;
    double duration = 0.0;
    // tracks[boneIndex] = 按 time 升序的局部矩阵关键帧；骨骼无通道时该 track 为空
    std::vector<std::vector<KeyFrame>> tracks;
};

// 顶点蒙皮权重（与 MeshData::vertices 下标对齐）
struct SkinInfluences {
    uint32_t boneIndex[4] = {0, 0, 0, 0};
    float    weight[4]    = {1.0f, 0.0f, 0.0f, 0.0f};
};

// ---------------------------------------------------------------------------
// 纹理（只保留预览用的小图，原始大树立即释放）
// ---------------------------------------------------------------------------
struct TextureInfo {
    std::string name;
    int  width = 0, height = 0;
    bool ok    = false;      // 是否解码成功
    bool baked = false;      // 是否烘焙出 preview
    int  previewW = 0, previewH = 0;
    std::vector<unsigned char> preview;   // previewW*previewH*4，RGBA8，行从上到下
};

// ---------------------------------------------------------------------------
// 模型（一个文件解析结果）
// ---------------------------------------------------------------------------
struct ModelData {
    // ---- 身份 ----
    std::string path;       // UTF-8 绝对路径
    std::string name;       // 文件名（不含扩展名）
    std::string category;   // 分类（扫描根下的第一层目录，或父目录名）
    uint64_t    fileSize = 0;

    // ---- 几何 ----
    MeshData mesh;
    std::vector<SkinInfluences> skin;   // 空 = 静态网格

    // ---- 骨骼/动画 ----
    std::vector<Bone> bones;
    std::vector<Clip> clips;
    int  skinRootBone = -1;

    // ---- 纹理 ----
    std::vector<TextureInfo> textures;

    // ---- 列表用统计 ----
    uint32_t triangleCount = 0;
    uint32_t vertexCount   = 0;
    uint32_t materialCount = 0;
    uint32_t boneCount     = 0;
    uint32_t clipCount     = 0;

    // ---- 状态 ----
    bool        ok = false;
    std::string error;      // 失败原因；ok=true 时可为空或为「警告」
    std::string warning;    // 非致命问题

    ModelData() = default;
};

// ---------------------------------------------------------------------------
// 坐标/矩阵工具（所有模块共用，避免各写一套转置）
// ---------------------------------------------------------------------------
namespace mat {
// 行优先 4x4，m[r*4+c]。以下均为「行向量 × 矩阵」约定：v' = v * M
inline void identity(float* m) {
    for (int i = 0; i < 16; ++i) m[i] = (i % 5 == 0) ? 1.0f : 0.0f;
}
// a * b（先 a 后 b），out 可与 a/b 重叠以外的任意缓冲
inline void multiply(const float* a, const float* b, float* out) {
    float r[16];
    for (int i = 0; i < 4; ++i)
        for (int j = 0; j < 4; ++j) {
            float s = 0.0f;
            for (int k = 0; k < 4; ++k) s += a[i * 4 + k] * b[k * 4 + j];
            r[i * 4 + j] = s;
        }
    for (int i = 0; i < 16; ++i) out[i] = r[i];
}
// glTF 列主序 16 元素 → 行优先生成（等同转置）
inline void fromColumnMajor(const float* colMajor, float* outRowMajor) {
    for (int r = 0; r < 4; ++r)
        for (int c = 0; c < 4; ++c) outRowMajor[r * 4 + c] = colMajor[c * 4 + r];
}
// 局部 TRS：M = T * R * S（行向量约定，先缩放再旋转再平移）
void composeTRS(const float t[3], const float q[4] /* xyzw */, const float s[3], float* out16);
// 4x4 求逆（失败返回 false，out 置单位阵）
bool invert(const float* m, float* out16);
// 变换点 / 方向（w=1 / w=0）
void transformPoint(const float* m, const float xyz[3], float out[3]);
void transformDir(const float* m, const float xyz[3], float out[3]);
} // namespace mat

// ---------------------------------------------------------------------------
// 解析入口（实现：glb_loader.cpp / fbx_loader.cpp）
// ---------------------------------------------------------------------------
bool LoadGLB(const std::string& utf8Path, ModelData* out);
bool LoadFBX(const std::string& utf8Path, ModelData* out);

// 按扩展名分派。返回是否成功。
bool LoadModel(const std::string& utf8Path, ModelData* out);

// ---------------------------------------------------------------------------
// 动画采样（实现：anim_sampler.cpp，归 glb-loader 名下）
// ---------------------------------------------------------------------------
// 计算 t 秒时每条骨骼的「局部」矩阵（len = bones.size()*16，行优先）
bool SampleClipLocals(const ModelData& model, int clipIndex, double t, std::vector<float>* outLocals);
// 计算 t 秒时每条骨骼的「世界」矩阵（从 root 向下级联；无父为局部）
bool SampleClipWorld(const ModelData& model, int clipIndex, double t, std::vector<float>* outWorld);
// 由世界矩阵生成蒙皮矩阵 skinMat[j] = world[j] * bind[j]（len = bones.size()*16）
void BuildSkinMatrices(const ModelData& model, const std::vector<float>& world,
                       std::vector<float>* outSkinMatrices);

// ---------------------------------------------------------------------------
// 缩略图渲染（实现：thumb_renderer.cpp）
// ---------------------------------------------------------------------------
struct ThumbOptions {
    int  size = 256;
    int  samplesPerAxis = 2;   // SSAA
    bool transparentBg = false;
    uint32_t bgTop = 0xFF3A4150;      // 0xAARRGGBB 语义（即深灰蓝 #3A4150）
    uint32_t bgBottom = 0xFF1B1E24;   // 0xAARRGGBB 语义（#1B1E24）
    bool useTexturePreview = true;
    float yaw = 0.61f;         // ~35°，弧度
    float pitch = 0.35f;       // ~20°
};

// outPixels: size*size 个 32 位像素，内存顺序 B,G,R,A
bool RenderThumbnail(const ModelData& model, const ThumbOptions& opt, std::vector<uint32_t>* outPixels);

} // namespace av
