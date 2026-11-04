# AssetViewer 共享契约（SHARED SPEC）

> 本文件是 **UI 模块** 与 **解析/渲染模块** 之间唯一的接口合同。
> 任何一方改动这里的类型/函数签名，必须同时通知 Lead 并在本文件里登记。
> 版本: v1 ｜ 2026-11-04

---

## 0. 模块边界

```
                 ┌──────────────────────────────┐
                 │  asset_viewer.exe  (WIN32)   │   ← app_main.cpp / viewer_app.cpp
                 │  D3D11 预览 + 列表 + 详情    │
                 └──────────────┬───────────────┘
                                │ scan_worker.cpp（后台线程）
              ┌─────────────────┼──────────────────┐
              ▼                 ▼                  ▼
     asset_scanner.cpp      glb_loader.cpp      thumb_renderer.cpp
     （文件系统遍历）        fbx_loader.cpp     （CPU 软件光栅）
        │                       │                  │
        └──────────► model_data.h（共享结构体）◄─────┘
                         texture.cpp / wic_util.cpp（PNG/WIC/预览纹理烘焙）
```

依赖方向：**UI → (scanner, loaders, thumb_renderer) → model_data.h / texture.h / wic_util.h**。
下层不得 include 上层（loader 不能 include viewer_app.h）。

---

## 1. 公共头文件清单（谁拥有、谁可读）

| 头文件 | 拥有者 | 内容 |
|---|---|---|
| `src/model_data.h` | Lead | 全部数据结构 + 解析/渲染函数声明 + 动画采样函数 |
| `src/texture.h` | glb-loader | 纹理解码 + 预览纹理结构 |
| `src/wic_util.h` | glb-loader | PNG 读写（WIC） |
| `src/glb_loader.h` | glb-loader | `LoadGLB()` |
| `src/fbx_loader.h` | glb-loader | `LoadFBX()` |
| `src/thumb_renderer.h` | glb-loader | `RenderThumbnail()` |
| `src/asset_scanner.h` | winui-list | `ScanAssets()` |
| `src/scan_worker.h` | winui-list | 后台扫描/缩略图线程 |
| `src/viewer_app.h` | winui-list | 主窗口类 |
| `src/d3d_renderer.h` | winui-list | D3D11 预览渲染器 |

> `model_data.h` 是**唯一**被所有人 include 的共享头，任何字段增删必须 Lead 批。

---

## 2. 数据结构（`model_data.h`，权威定义以该文件为准）

```cpp
namespace av {

// ---- 字节布局（GPU/光栅共用，禁止 pad 变化）----
struct Vertex {
    float px, py, pz;      // Object 空间位置
    float nx, ny, nz;      // Object 空间法线（可能未归一化）
    float u, v;            // UV，v 已翻（glTF origin 左下 → 这里保持 glTF 约定，D3D 侧自行处理）
};

struct Material {
    std::string name;
    float baseColor[4];    // RGBA [0,1]，默认 {0.8,0.8,0.8,1}
    int   diffuseTex = -1;  // ModelData::textures 下标，-1 无
    bool  doubleSided = false;
};

struct SubMesh {           // 一个 draw call，对应一个材质
    int materialIndex = -1;
    uint32_t indexOffset = 0;
    uint32_t indexCount  = 0;
    std::string name;
};

struct MeshData {
    std::vector<Vertex>   vertices;    // 全局顶点池（所有 SubMesh 共享）
    std::vector<uint32_t> indices;     // 全局索引池
    std::vector<SubMesh>  subMeshes;
    std::vector<Material> materials;
};

// ---- 骨骼 ----
struct Bone {
    std::string name;
    int   parent = -1;
    float local[16];       // 局部位姿矩阵，行优先 or 列优先见 §5
    float bind[16];        // 逆绑定矩阵（inverse bind，若 glTF 直接提供）；无则单位阵
    float world[16];       // 当前帧世界矩阵（由 AnimSampler 填）
};

struct ClipKeyFrame {
    double time = 0.0;
    float  value[16];      // 该时刻的局部 TRS 合成矩阵
};

struct Clip {
    std::string name;
    double duration = 0.0;
    std::vector<std::vector<ClipKeyFrame>> tracks; // tracks[boneIndex] → keyframes
};

// ---- 纹理：只存「预览用」烘焙结果，不存原始字节 ----
struct TextureInfo {
    std::string name;
    int   width = 0, height = 0;
    bool  ok = false;          // 是否成功解码
    bool  baked = false;       // 是否已烘焙出预览图
    int   previewW = 0, previewH = 0;
    // 预览用小图（默认烘焙到最长边 32px，RGBA8，行从上到下），原始大树立即释放
    std::vector<unsigned char> preview;
};

struct ModelData {
    // 文件
    std::string path;          // UTF-8
    std::string name;          // 文件名（不含扩展名）
    std::string category;      // 分类（UTF-8）
    uint64_t    fileSize = 0;

    // 几何
    MeshData mesh;
    std::vector<SkinInfluences> skin;   // 顶点蒙皮权重，空 = 静态网格
    std::vector<Bone> bones;
    int skinRootBone = -1;

    // 动画
    std::vector<Clip> clips;

    // 纹理
    std::vector<TextureInfo> textures;

    // 统计（供列表直接读取，避免二次扫描）
    uint32_t triangleCount = 0;
    uint32_t vertexCount   = 0;
    uint32_t materialCount = 0;
    uint32_t boneCount     = 0;
    uint32_t clipCount     = 0;

    // 状态
    bool ok = false;
    std::string error;         // 失败原因（空 = 成功）
    std::string warning;       // 非致命问题
};
```

> 骨骼「当前帧世界矩阵」不放在 `Bone` 里，而是由 `SampleClipWorld()` 输出到独立缓冲，UI 再交给 `BuildSkinMatrices()`。见 §7。

---

## 3. 解析入口（两个 loader 必须提供同名签名）

```cpp
// glb_loader.h
namespace av {
// 解析 .glb / .gltf。path: UTF-8 路径。返回 false 时 out->ok=false 且 out->error 非空。
bool LoadGLB(const std::string& path, ModelData* out);
}

// fbx_loader.h
namespace av {
bool LoadFBX(const std::string& path, ModelData* out);
}
```

**必须满足的约定：**
1. 内部用 `std::ifstream` + UTF-8→宽字符（`MultiByteToWideChar(CP_UTF8)`）打开；禁止用 `std::ifstream` 直接吃 ANSI 路径。
2. 任何异常/越界/畸形数据 → 不抛异常、不崩，返回 false 并写 `out->error`。
3. 单文件解析（不含缩略图渲染）目标耗时 < 20ms（素材最大 1.9MB）。
4. 必须填好 `triangleCount`、`vertexCount`（来自实际索引）。
5. 解析后调用方会用 `RenderThumbnail()` 生成缩略图；loader 内部**不要**做缩略图光栅。

---

## 4. 缩略图渲染入口

```cpp
// thumb_renderer.h
namespace av {
struct ThumbOptions {
    int  size = 256;          // 输出方形边长
    int  samplesPerAxis = 2;  // SSAA（1/2/3）
    bool transparentBg = false;
    unsigned bgTop = 0xFF2A2F38, bgBottom = 0xFF1A1D23; // 竖直渐变，ABGR 还是 ARGB 见 §5
    bool useTexturePreview = true;
};

// 输出：size*size 个 32 位像素，**BGRA** 字节序（即 Windows DIB 的 BB GG RR AA 内存顺序，
//       D3D11 R8G8B8A8_UNORM 与 Direct2D B8G8R8A8 均兼容）。
// 注意：bgTop/bgBottom 两个 uint32 字段按 **0xAARRGGBB** 语义书写（不是像素内存序）。
bool RenderThumbnail(const ModelData& model, const ThumbOptions& opt,
                     std::vector<uint32_t>* outPixels);
}
```

**视觉要求**：自动选择相机让模型 fill 画面（包围球 fit），3/4 侧前方（yaw≈+35°, pitch≈+20°），
三光源（key 顶部前方 + fill 侧后 + 微弱 rim），法线缺失自动计算，材质基础色/贴图采样着色，
裁剪严格（近远面），像素边界抗锯齿（SSAA 下采样），失败时也要输出占位图而非崩溃。

**性能要求**：256px/SSAA2 下单个模型 < 15ms，批量 371 个 < 3s。

---

## 5. 矩阵与坐标约定（**所有模块必须一致**）

- 矩阵 4x4，**行优先存储 `float m[16]`，按数学惯例（行向量 × 矩阵）**，即 `p' = p * M`。
  同一约定下：`M = T * R * S`（先缩放再旋转再平移），`M_parent * M_local` 合成世界矩阵。
  > glTF 的 `matrix` 节点本身就是列向量约定（列主序数组），移植时需要**转置**成行优先。
- 位置 `(x, y, z)`；**y 轴向上**（DX 约定），右手系 → 相机沿 `-Z` 看。
  > glTF 原生 y 向上 ✓；FBX 是 Z 向上，解析时 `y' = z, z' = -y`（右手系 Z-up→Y-up）。
- UV：`u` 向右、`v` 向下（图像坐标，与 PNG 行序一致），`u = du/ds`，`v = 1 - dv/dt`。
- 颜色字节序：一切**对外像素缓冲统一 BGRA**；内部计算用 `[0,1]` float RGBA。
- 相机：`LookAt(eye, target=(0,0,0), up=(0,1,0))`，FOV 40°，near = 0.01 * r，far = 1000 * r（r = 包围球半径）。

---

## 6. 纹理 / 预览烘焙

原始 glb 里的内嵌 PNG/JPEG 可能是 512~2048 的图集。本工具只需要"预览用颜色"：
- **解码一次**为 RGBA8（WIC：`CoCreateInstance(CLSID_WICImagingFactory)` 解 PNG/JPEG/BMP/GIF/TIFF；DDS 自行解析 BC1/BC3/BC7，若麻烦可跳过并标记 ok=false）；
- **立即烘焙**成 `preview32`（32x32，双线性多级下采样），随后**释放原图**（省内存）；
- CPU 光栅采样纹理时用 32x32 预览 + 双线性过滤；这对缩略图足够，且几乎零成本；
- D3D11 预览（UI 模块）也直接把这个 32x32 当纹理上传即可（用 `D3D11_FILTER_MIN_MAG_MIP_LINEAR` 放大）。
- 若一个文件有 71 张图（海盗场景），全部烘焙后内存约 71*32*32*4 = 291KB，可接受。

---

## 7. 动画采样（UI 播放用）

```cpp
// model_data.h
namespace av {
// 计算 t（秒，会自动 clamp 到 [0, duration]）时刻所有骨骼的世界矩阵。
// 写回 clip 对应 tracks 的骨骼到 outWorld（与 model.bones 同长度）。
// 若无动画/无骨骼，直接写单位阵。返回是否真的应用了动画。
bool SampleAnimation(const ModelData& model, int clipIndex, double t,
                     std::vector<float>* outWorld /* size = bones.size()*16 */);

// 只计算骨骼局部矩阵的时刻值（loader 内部用，导出族加速用）
void BuildClipTrackFromBones(ModelData* model); // 可选
}
```

骨骼世界矩阵 = `bind_local_world * anim_local * inverse(bind_local_world)` 的常见简式：
```
worldBone_i = Σ ... 实际实现：从 root 向下 world_i = world_parent * localAt(t)_i
顶点变换： v' = Σ w_j * (world_j * inverseBind_j) * v
```

> 该函数由 **glb-loader** 实现（SampleAnimation 放在 texture.cpp 或新文件 anim_sampler.cpp，属 glb-loader 名下）。

---

## 8. 扫描/缓存（UI 侧）

- `asset_scanner.h`：
```cpp
namespace av {
struct ScanEntry {
    std::string path;      // UTF-8 绝对路径
    std::string name;      // 不含扩展名
    std::string category;  // 父目录（相对扫描根）
    uint64_t    fileSize;
    int64_t     mtimeUnix;
    bool        isFBX;
};
bool ScanAssets(const std::string& rootUtf8, std::vector<ScanEntry>* out);
}}
```
- 缩略图缓存文件：`build/cache/<sha1(path)>_<size>.png`，key 带 mtime+size，二次启动直接加载（由 wic_util 读写 PNG）。
- 线程模型：**1 个扫描线程** 先快速产出条目（百毫秒内），再 **N 个并行缩略图线程**（N = CPU 核数-1，上限 8）按需产缩略图；UI 线程用 `WM_APP+n` 消息收结果。

---

## 9. 错误处理铁律

| 场景 | 行为 |
|---|---|
| 文件打不开 / 非 GLB 魔数 | ok=false, error="不是 GLB 文件"/"文件不存在" |
| 无 mesh（纯空节点） | ok=true 但给空占位模型，error 为空；UI 显示「无可见网格」 |
| 图片解码失败 | 该 TextureInfo.ok=false，不影响模型 ok |
| 缩略图渲染失败 | 输出纯色占位图形（不返回空指针） |
| 内存上限 | 单个模型顶点上限 2M、索引上限 6M，超出截断并记 error 警告但仍 ok |

---

## 10. 协作与文件所有权

| 成员 | 只改这些文件 |
|---|---|
| Lead | `CMakeLists.txt`（最终裁定）、`src/model_data.h`、`ref/*`、`tests/thumb_sheet.cpp`、`build.ps1`（最终） |
| glb-loader | `src/{glb_loader,fbx_loader,texture,wic_util,thumb_renderer,anim_sampler}.{c,h}`、`tests/loader_smoke.cpp` |
| winui-list | `src/{app_main,viewer_app,asset_scanner,scan_worker,d3d_renderer}.{c,h}`、`tests/ui_selftest.cpp` |
| env-pkg | `CMakeLists.txt` 初稿、`build.ps1` 初稿、`README.md` |

改动 `model_data.h` 必须经 Lead（会同步本文件 §2）。
