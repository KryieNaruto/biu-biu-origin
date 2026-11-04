// d3d_renderer.cpp —— D3D11 预览渲染实现
#include "d3d_renderer.h"

#include <d3dcompiler.h>

#include <cmath>
#include <cstring>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace av {

namespace {

constexpr int kMaxBones = 512;

// GPU 顶点（64 字节）：pos3 nrm3 uv2 + boneIdx uint4 + weight float4
// 静态模型把 boneIdx 置 0、weight 置 (0,0,0,0)，着色器里 wsum<=0 直接跳过蒙皮分支。
struct GpuVertex {
    float px, py, pz;
    float nx, ny, nz;
    float u, v;
    uint32_t boneIdx[4];
    float    weight[4];
};
static_assert(sizeof(GpuVertex) == 64, "GpuVertex must be 64 bytes");

// ---------------------------------------------------------------------------
// 着色器源码（内嵌，避免运行期依赖 .hlsl 文件路径）
// 矩阵：CPU 为行优先 + 行向量×矩阵，故 HLSL 用 row_major 并 v * M
// ---------------------------------------------------------------------------
const char* kSkinVS = R"(
cbuffer FrameCB : register(b0) {
    row_major float4x4 gViewProj;
    float3 gEye;
    float3 gCamDir;
    float3 gLightKeyDir;   float3 gLightKeyColor;
    float3 gLightFillDir;  float3 gLightFillColor;
    float3 gLightRimDir;   float3 gLightRimColor;
    float3 gAmbient;
    float  gTime;
};

cbuffer SkinCB : register(b1) {
    row_major float4x4 gSkin[512];
};

struct VSIn {
    float3 pos : POSITION;
    float3 nrm : NORMAL;
    float2 uv  : TEXCOORD0;
    uint4  bones : BLENDINDICES;
    float4 weights : BLENDWEIGHT;
};

struct VSOut {
    float4 pos : SV_Position;
    float3 nrm : NORMAL;
    float3 worldPos : TEXCOORD1;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(VSIn i) {
    float4 p = float4(i.pos, 1.0);
    float3 n = i.nrm;

    float wsum = i.weights.x + i.weights.y + i.weights.z + i.weights.w;
    if (wsum > 0.0001 && gSkin[0][0][0] != -12345.0) {
        row_major float4x4 m0 = gSkin[i.bones.x];
        row_major float4x4 m1 = gSkin[i.bones.y];
        row_major float4x4 m2 = gSkin[i.bones.z];
        row_major float4x4 m3 = gSkin[i.bones.w];
        float4 p4 = mul(p, m0) * i.weights.x + mul(p, m1) * i.weights.y
                  + mul(p, m2) * i.weights.z + mul(p, m3) * i.weights.w;
        float4 n4 = mul(float4(n, 0.0), m0) * i.weights.x + mul(float4(n, 0.0), m1) * i.weights.y
                  + mul(float4(n, 0.0), m2) * i.weights.z + mul(float4(n, 0.0), m3) * i.weights.w;
        p = p4 / max(wsum, 0.0001);
        n = n4.xyz;
    }

    VSOut o;
    o.worldPos = p.xyz;
    o.pos = mul(float4(p.xyz, 1.0), gViewProj);
    o.nrm = n;
    o.uv = i.uv;
    return o;
}
)";

const char* kMainPS = R"(
cbuffer FrameCB : register(b0) {
    row_major float4x4 gViewProj;
    float3 gEye;
    float3 gCamDir;
    float3 gLightKeyDir;   float3 gLightKeyColor;
    float3 gLightFillDir;  float3 gLightFillColor;
    float3 gLightRimDir;   float3 gLightRimColor;
    float3 gAmbient;
    float  gTime;
};

cbuffer ObjectCB : register(b2) {
    float4 gBaseColor;
    float  gMetallic;
    float  gRoughness;
    int    gHasTex;
    float  gUnused;
};

Texture2D gDiffuse : register(t0);
SamplerState gSamp : register(s0);

float3 ShadeBRDF(float3 N, float3 V, float3 L, float3 lc, float3 base, float rough) {
    float ndl = saturate(dot(N, L));
    float3 H = normalize(L + V);
    float ndh = saturate(dot(N, H));
    float shin = lerp(48.0, 4.0, rough);
    float spec = pow(ndh, shin) * (1.0 - rough) * 0.6;
    return (base + spec) * lc * ndl;
}

float4 PSMain(float4 pos : SV_Position, float3 nrm : NORMAL,
              float3 worldPos : TEXCOORD1, float2 uv : TEXCOORD0) : SV_Target {
    float3 N = normalize(nrm);
    if (!all(isfinite(N)) || length(N) < 0.001) N = float3(0.0, 1.0, 0.0);
    float3 V = normalize(gEye - worldPos);
    // 双面光照：背面翻转法线
    N = dot(N, V) >= 0.0 ? N : -N;

    float3 base = gBaseColor.rgb;
    if (gHasTex != 0) {
        float4 t = gDiffuse.Sample(gSamp, uv);
        base *= t.rgb;
    }
    base = pow(saturate(base), 2.2);   // sRGB -> 线性

    float3 col = base * gAmbient;
    col += ShadeBRDF(N, V, -gLightKeyDir,  gLightKeyColor,  base, gRoughness);
    col += ShadeBRDF(N, V, -gLightFillDir, gLightFillColor, base, gRoughness);
    // rim
    float rim = pow(1.0 - saturate(dot(N, V)), 3.0) * 0.45;
    col += gLightRimColor * rim * base;

    // gamma 回写
    col = pow(saturate(col), 1.0 / 2.2);
    return float4(col, gBaseColor.a);
}
)";

// 背景：竖直渐变 + 细微网格
const char* kBgVS = R"(
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VSBg(uint id : SV_VertexID) {
    VSOut o;
    float2 p = float2((id == 1) ? 3.0 : -1.0, (id == 2) ? 3.0 : -1.0);
    o.pos = float4(p, 0.0, 1.0);
    o.uv = p * 0.5 + 0.5;
    return o;
}
)";

const char* kBgPS = R"(
float4 PSBg(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    float3 top = float3(0.10, 0.12, 0.16);
    float3 bot = float3(0.05, 0.06, 0.09);
    float3 c = lerp(top, bot, uv.y);
    // 细微网格
    float2 g = abs(frac(uv * 24.0) - 0.5);
    float line = smoothstep(0.47, 0.5, max(1.0 - g.x * 2.0, 1.0 - g.y * 2.0));
    c += line * 0.012;
    return float4(c, 1.0);
}
)";

inline void MatIdentity(float* m) {
    for (int i = 0; i < 16; ++i) m[i] = (i % 5 == 0) ? 1.0f : 0.0f;
}

// 行向量约定下的 LookAt（D3D 左手系：view 空间 +z 指向场景内部）
// 对行向量 v' = v * M，标准形式为
//   | x.x  y.x  z.x  0 |
//   | x.y  y.y  z.y  0 |
//   | x.z  y.z  z.z  0 |
//   | -dot(x,eye) -dot(y,eye) -dot(z,eye) 1 |
// 其中 z = normalize(center - eye)（前向），x = normalize(cross(up, z))，y = cross(z, x)。
void MakeLookAt(const float eye[3], const float center[3], const float up[3], float outView[16]) {
    float z[3] = {center[0] - eye[0], center[1] - eye[1], center[2] - eye[2]}; // 前向
    float zl = std::sqrt(z[0] * z[0] + z[1] * z[1] + z[2] * z[2]);
    if (zl < 1e-6f) zl = 1e-6f;
    z[0] /= zl; z[1] /= zl; z[2] /= zl;

    // x = cross(up, z)
    float x[3] = {up[1] * z[2] - up[2] * z[1], up[2] * z[0] - up[0] * z[2], up[0] * z[1] - up[1] * z[0]};
    float xl = std::sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
    if (xl < 1e-6f) { x[0] = 1; x[1] = 0; x[2] = 0; xl = 1.0f; }
    x[0] /= xl; x[1] /= xl; x[2] /= xl;

    // y = cross(z, x)
    float y[3] = {z[1] * x[2] - z[2] * x[1], z[2] * x[0] - z[0] * x[2], z[0] * x[1] - z[1] * x[0]};

    // 行优先存储：outView[r*4+c]，第 c 列为基向量 x/y/z 的第 r 个分量
    outView[0] = x[0]; outView[1] = y[0]; outView[2] = z[0]; outView[3] = 0;
    outView[4] = x[1]; outView[5] = y[1]; outView[6] = z[1]; outView[7] = 0;
    outView[8] = x[2]; outView[9] = y[2]; outView[10] = z[2]; outView[11] = 0;
    outView[12] = -(x[0] * eye[0] + x[1] * eye[1] + x[2] * eye[2]);
    outView[13] = -(y[0] * eye[0] + y[1] * eye[1] + y[2] * eye[2]);
    outView[14] = -(z[0] * eye[0] + z[1] * eye[1] + z[2] * eye[2]);
    outView[15] = 1.0f;
}

// 行向量约定：viewProj = view * proj
void MakePerspective(float fovY, float aspect, float zn, float zf, float outProj[16]) {
    const float yScale = 1.0f / std::tan(fovY * 0.5f);
    const float xScale = yScale / aspect;
    for (int i = 0; i < 16; ++i) outProj[i] = 0.0f;
    outProj[0] = xScale;
    outProj[5] = yScale;
    outProj[10] = zf / (zf - zn);
    outProj[11] = 1.0f;
    outProj[14] = -zn * zf / (zf - zn);
}

struct FrameCBData {
    float viewProj[16];
    float eye[3];   float pad0;
    float camDir[3]; float pad1;
    float keyDir[3];   float pad2;
    float keyColor[3]; float pad3;
    float fillDir[3];  float pad4;
    float fillColor[3]; float pad5;
    float rimDir[3];   float pad6;
    float rimColor[3]; float pad7;
    float ambient[3];  float pad8;
    float time;        float pad9[3];
};

struct ObjectCBData {
    float baseColor[4];
    float metallic;
    float roughness;
    int   hasTex;
    float unused;
};

} // namespace

// ---------------------------------------------------------------------------
void MakeFallbackModel(ModelData* out) {
    if (!out) return;
    *out = ModelData();
    out->path = "<fallback>";
    out->name = "占位模型";
    out->category = "内置";
    out->ok = true;
    out->warning = "内置占位模型（未加载真实素材）";

    MeshData& m = out->mesh;
    // 立方体
    const float s = 0.5f;
    const float pos[8][3] = {{-s,-s,-s},{s,-s,-s},{s,s,-s},{-s,s,-s},
                             {-s,-s, s},{s,-s, s},{s,s, s},{-s,s, s}};
    const uint32_t faces[6][4] = {{0,1,2,3},{5,4,7,6},{4,0,3,7},
                                  {1,5,6,2},{3,2,6,7},{4,5,1,0}};
    const float norms[6][3] = {{0,0,-1},{0,0,1},{-1,0,0},{1,0,0},{0,1,0},{0,-1,0}};
    for (int f = 0; f < 6; ++f) {
        const uint32_t base = (uint32_t)m.vertices.size();
        for (int k = 0; k < 4; ++k) {
            const int vi = (int)faces[f][k];
            Vertex v{};
            v.px = pos[vi][0]; v.py = pos[vi][1]; v.pz = pos[vi][2];
            v.nx = norms[f][0]; v.ny = norms[f][1]; v.nz = norms[f][2];
            v.u = (k == 1 || k == 2) ? 1.0f : 0.0f;
            v.v = (k >= 2) ? 1.0f : 0.0f;
            m.vertices.push_back(v);
        }
        m.indices.push_back(base + 0); m.indices.push_back(base + 1); m.indices.push_back(base + 2);
        m.indices.push_back(base + 0); m.indices.push_back(base + 2); m.indices.push_back(base + 3);
    }
    Material mat;
    mat.name = "placeholder";
    mat.baseColor[0] = 0.45f; mat.baseColor[1] = 0.72f; mat.baseColor[2] = 0.95f; mat.baseColor[3] = 1.0f;
    m.materials.push_back(mat);

    SubMesh sm;
    sm.materialIndex = 0;
    sm.indexOffset = 0;
    sm.indexCount = (uint32_t)m.indices.size();
    sm.name = "cube";
    m.subMeshes.push_back(sm);

    out->triangleCount = (uint32_t)(m.indices.size() / 3);
    out->vertexCount = (uint32_t)m.vertices.size();
    out->materialCount = 1;
}

// ---------------------------------------------------------------------------
D3DRenderer::D3DRenderer() { camera_.Reset(); }
D3DRenderer::~D3DRenderer() { Shutdown(); }

bool D3DRenderer::Initialize(HWND hwnd, int width, int height) {
    hwnd_ = hwnd;
    if (width < 16) width = 16;
    if (height < 16) height = 16;
    if (!CreateDevice(hwnd)) return false;
    if (!CreateShaders()) return false;
    if (!CreateStates()) return false;
    if (!CreateRenderTargets(width, height)) return false;
    ready_ = true;
    stats_.gpuReady = true;
    return true;
}

void D3DRenderer::Shutdown() {
    ReleaseModelResources();
    if (context_) context_->ClearState();
    if (rtSrv_) { rtSrv_->Release(); rtSrv_ = nullptr; }
    if (rtView_) { rtView_->Release(); rtView_ = nullptr; }
    if (rtTexture_) { rtTexture_->Release(); rtTexture_ = nullptr; }
    if (dsView_) { dsView_->Release(); dsView_ = nullptr; }
    if (dsTexture_) { dsTexture_->Release(); dsTexture_ = nullptr; }
    if (swapChain_) { swapChain_->Release(); swapChain_ = nullptr; }
    if (skinVS_) { skinVS_->Release(); skinVS_ = nullptr; }
    if (mainPS_) { mainPS_->Release(); mainPS_ = nullptr; }
    if (inputLayout_) { inputLayout_->Release(); inputLayout_ = nullptr; }
    if (cbFrame_) { cbFrame_->Release(); cbFrame_ = nullptr; }
    if (cbObject_) { cbObject_->Release(); cbObject_ = nullptr; }
    if (cbSkin_) { cbSkin_->Release(); cbSkin_ = nullptr; }
    if (rsSolid_) { rsSolid_->Release(); rsSolid_ = nullptr; }
    if (rsWire_) { rsWire_->Release(); rsWire_ = nullptr; }
    if (dsEnable_) { dsEnable_->Release(); dsEnable_ = nullptr; }
    if (dsDisable_) { dsDisable_->Release(); dsDisable_ = nullptr; }
    if (bsOpaque_) { bsOpaque_->Release(); bsOpaque_ = nullptr; }
    if (bsAlpha_) { bsAlpha_->Release(); bsAlpha_ = nullptr; }
    if (samplerLinear_) { samplerLinear_->Release(); samplerLinear_ = nullptr; }
    if (samplerPoint_) { samplerPoint_->Release(); samplerPoint_ = nullptr; }
    if (context_) { context_->Release(); context_ = nullptr; }
    if (device_) { device_->Release(); device_ = nullptr; }
    ready_ = false;
    stats_.gpuReady = false;
}

bool D3DRenderer::CreateDevice(HWND hwnd) {
    UINT flags = 0;
#if defined(_DEBUG)
    flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
    D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
                                  D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
    D3D_FEATURE_LEVEL got = D3D_FEATURE_LEVEL_11_0;

    HRESULT hr = ::D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels,
                                     _countof(levels), D3D11_SDK_VERSION, &device_, &got, &context_);
    if (FAILED(hr)) {
        // 退回 WARP 软件光栅
        flags &= ~(UINT)D3D11_CREATE_DEVICE_DEBUG;
        hr = ::D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, levels,
                                 _countof(levels), D3D11_SDK_VERSION, &device_, &got, &context_);
    }
    if (FAILED(hr) || !device_ || !context_) {
        stats_.lastError = "D3D11CreateDevice 失败";
        return false;
    }
    (void)hwnd;
    return true;
}

bool D3DRenderer::CreateShaders() {
    ID3DBlob* vsBlob = nullptr;
    ID3DBlob* psBlob = nullptr;
    ID3DBlob* err = nullptr;

    HRESULT hr = ::D3DCompile(kSkinVS, std::strlen(kSkinVS), "skin_vs", nullptr, nullptr, "VSMain",
                              "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &vsBlob, &err);
    if (FAILED(hr)) {
        stats_.lastError = "VS 编译失败";
        if (err) { stats_.lastError += (const char*)err->GetBufferPointer(); err->Release(); }
        return false;
    }
    hr = ::D3DCompile(kMainPS, std::strlen(kMainPS), "main_ps", nullptr, nullptr, "PSMain", "ps_5_0",
                      D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &psBlob, &err);
    if (FAILED(hr)) {
        stats_.lastError = "PS 编译失败";
        if (err) { stats_.lastError += (const char*)err->GetBufferPointer(); err->Release(); }
        vsBlob->Release();
        return false;
    }

    hr = device_->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &skinVS_);
    if (SUCCEEDED(hr))
        hr = device_->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &mainPS_);

    if (SUCCEEDED(hr)) {
        // 全部属性放在同一个顶点缓冲 slot 0：
        //   pos(0..11) nrm(12..23) uv(24..31) boneIdx uint4(32..47) weight float4(48..63)
        // 蒙皮属性使用独立的 GPU 缓冲（kGpuVertex = 64 字节），避免 slot 1 的偏移一致性校验失败。
        D3D11_INPUT_ELEMENT_DESC layout[] = {
            {"POSITION",     0, DXGI_FORMAT_R32G32B32_FLOAT,    0, 0,  D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"NORMAL",       0, DXGI_FORMAT_R32G32B32_FLOAT,    0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"TEXCOORD",     0, DXGI_FORMAT_R32G32_FLOAT,       0, 24, D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"BLENDINDICES", 0, DXGI_FORMAT_R32G32B32A32_UINT,  0, 32, D3D11_INPUT_PER_VERTEX_DATA, 0},
            {"BLENDWEIGHT",  0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 48, D3D11_INPUT_PER_VERTEX_DATA, 0},
        };
        hr = device_->CreateInputLayout(layout, _countof(layout), vsBlob->GetBufferPointer(),
                                        vsBlob->GetBufferSize(), &inputLayout_);
    }
    vsBlob->Release();
    psBlob->Release();
    if (FAILED(hr)) {
        char buf[64];
        ::sprintf_s(buf, sizeof(buf), "着色器/输入布局创建失败 hr=0x%08X", (unsigned)hr);
        stats_.lastError = buf;
        return false;
    }

    // 常量缓冲
    D3D11_BUFFER_DESC bd{};
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    bd.ByteWidth = (UINT)((sizeof(FrameCBData) + 15) & ~15u);
    if (FAILED(device_->CreateBuffer(&bd, nullptr, &cbFrame_))) return false;
    bd.ByteWidth = (UINT)((sizeof(ObjectCBData) + 15) & ~15u);
    if (FAILED(device_->CreateBuffer(&bd, nullptr, &cbObject_))) return false;
    bd.ByteWidth = (UINT)(sizeof(float) * 16 * kMaxBones);
    if (FAILED(device_->CreateBuffer(&bd, nullptr, &cbSkin_))) return false;
    return true;
}

bool D3DRenderer::CreateStates() {
    D3D11_RASTERIZER_DESC rd{};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;   // 双面渲染，避免单面模型消失
    rd.DepthClipEnable = TRUE;
    rd.FrontCounterClockwise = FALSE;
    if (FAILED(device_->CreateRasterizerState(&rd, &rsSolid_))) return false;
    rd.FillMode = D3D11_FILL_WIREFRAME;
    if (FAILED(device_->CreateRasterizerState(&rd, &rsWire_))) return false;

    D3D11_DEPTH_STENCIL_DESC dd{};
    dd.DepthEnable = TRUE;
    dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    dd.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    if (FAILED(device_->CreateDepthStencilState(&dd, &dsEnable_))) return false;
    dd.DepthEnable = FALSE;
    dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
    if (FAILED(device_->CreateDepthStencilState(&dd, &dsDisable_))) return false;

    D3D11_BLEND_DESC bd{};
    bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    bd.RenderTarget[0].BlendEnable = FALSE;
    if (FAILED(device_->CreateBlendState(&bd, &bsOpaque_))) return false;
    bd.RenderTarget[0].BlendEnable = TRUE;
    bd.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
    bd.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    if (FAILED(device_->CreateBlendState(&bd, &bsAlpha_))) return false;

    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_WRAP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device_->CreateSamplerState(&sd, &samplerLinear_))) return false;
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    if (FAILED(device_->CreateSamplerState(&sd, &samplerPoint_))) return false;
    return true;
}

bool D3DRenderer::CreateRenderTargets(int w, int h) {
    if (rtSrv_) { rtSrv_->Release(); rtSrv_ = nullptr; }
    if (rtView_) { rtView_->Release(); rtView_ = nullptr; }
    if (rtTexture_) { rtTexture_->Release(); rtTexture_ = nullptr; }
    if (dsView_) { dsView_->Release(); dsView_ = nullptr; }
    if (dsTexture_) { dsTexture_->Release(); dsTexture_ = nullptr; }

    D3D11_TEXTURE2D_DESC td{};
    td.Width = (UINT)w;
    td.Height = (UINT)h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;   // BGRA，与 av::RenderThumbnail 输出一致
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    HRESULT hr = device_->CreateTexture2D(&td, nullptr, &rtTexture_);
    if (FAILED(hr)) { stats_.lastError = "创造渲染目标纹理失败"; return false; }
    hr = device_->CreateRenderTargetView(rtTexture_, nullptr, &rtView_);
    if (FAILED(hr)) { stats_.lastError = "创造 RTV 失败"; return false; }
    hr = device_->CreateShaderResourceView(rtTexture_, nullptr, &rtSrv_);
    if (FAILED(hr)) { stats_.lastError = "创造 SRV 失败"; return false; }

    D3D11_TEXTURE2D_DESC dd{};
    dd.Width = (UINT)w;
    dd.Height = (UINT)h;
    dd.MipLevels = 1;
    dd.ArraySize = 1;
    dd.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
    dd.SampleDesc.Count = 1;
    dd.Usage = D3D11_USAGE_DEFAULT;
    dd.BindFlags = D3D11_BIND_DEPTH_STENCIL;
    hr = device_->CreateTexture2D(&dd, nullptr, &dsTexture_);
    if (FAILED(hr)) { stats_.lastError = "创造深度缓冲失败"; return false; }
    hr = device_->CreateDepthStencilView(dsTexture_, nullptr, &dsView_);
    if (FAILED(hr)) { stats_.lastError = "创造 DSV 失败"; return false; }

    width_ = w;
    height_ = h;
    return true;
}

bool D3DRenderer::Resize(int width, int height) {
    if (!ready_) return false;
    if (width < 16) width = 16;
    if (height < 16) height = 16;
    if (width == width_ && height == height_) return true;
    return CreateRenderTargets(width, height);
}

void D3DRenderer::ReleaseModelResources() {
    for (size_t i = 0; i < textureSrv_.size(); ++i)
        if (textureSrv_[i]) textureSrv_[i]->Release();
    textureSrv_.clear();
    for (size_t i = 0; i < samplers_.size(); ++i)
        if (samplers_[i]) samplers_[i]->Release();
    samplers_.clear();
    if (vb_) { vb_->Release(); vb_ = nullptr; }
    if (ib_) { ib_->Release(); ib_ = nullptr; }
    subMeshes_.clear();
    materials_.clear();
    vertexCount_ = 0;
    indexCount_ = 0;
    haveSkin_ = false;
    skinMatrices_.clear();
}

bool D3DRenderer::CreateTextureFromPreview(const TextureInfo& tex, ID3D11ShaderResourceView** out,
                                           int* outW, int* outH) {
    *out = nullptr;
    if (!tex.ok || !tex.baked || tex.previewW <= 0 || tex.previewH <= 0 || tex.preview.empty()) return false;
    const size_t need = (size_t)tex.previewW * (size_t)tex.previewH * 4;
    if (tex.preview.size() < need) return false;

    D3D11_TEXTURE2D_DESC td{};
    td.Width = (UINT)tex.previewW;
    td.Height = (UINT)tex.previewH;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;   // TextureInfo::preview 为 RGBA8
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_IMMUTABLE;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    D3D11_SUBRESOURCE_DATA sd{};
    sd.pSysMem = tex.preview.data();
    sd.SysMemPitch = (UINT)tex.previewW * 4;

    ID3D11Texture2D* t = nullptr;
    if (FAILED(device_->CreateTexture2D(&td, &sd, &t))) return false;
    const HRESULT hr = device_->CreateShaderResourceView(t, nullptr, out);
    t->Release();
    if (FAILED(hr)) return false;
    if (outW) *outW = tex.previewW;
    if (outH) *outH = tex.previewH;
    return true;
}

bool D3DRenderer::SetModel(const ModelData& model) {
    if (!ready_) return false;
    ReleaseModelResources();

    const MeshData& mesh = model.mesh;
    vertexCount_ = (uint32_t)mesh.vertices.size();
    indexCount_ = (uint32_t)mesh.indices.size();
    materials_ = mesh.materials;

    if (vertexCount_ == 0 || indexCount_ == 0) {
        stats_.lastError = "模型无可见几何";
        ComputeBounds();
        return false;
    }

    // 顶点缓冲：把 av::Vertex 与 av::SkinInfluences 合并成 64 字节 GPU 顶点
    std::vector<GpuVertex> gpuVerts(vertexCount_);
    const bool hasSkin = !model.skin.empty() && model.skin.size() == mesh.vertices.size();
    for (uint32_t i = 0; i < vertexCount_; ++i) {
        const Vertex& src = mesh.vertices[i];
        GpuVertex& dst = gpuVerts[i];
        dst.px = src.px; dst.py = src.py; dst.pz = src.pz;
        dst.nx = src.nx; dst.ny = src.ny; dst.nz = src.nz;
        dst.u = src.u;   dst.v = src.v;
        if (hasSkin) {
            const SkinInfluences& si = model.skin[i];
            for (int k = 0; k < 4; ++k) {
                dst.boneIdx[k] = si.boneIndex[k];
                dst.weight[k] = si.weight[k];
            }
        } else {
            for (int k = 0; k < 4; ++k) {
                dst.boneIdx[k] = 0;
                dst.weight[k] = 0.0f;   // wsum = 0 -> 着色器跳过蒙皮
            }
        }
    }
    haveSkin_ = hasSkin;

    D3D11_BUFFER_DESC bd{};
    bd.Usage = D3D11_USAGE_IMMUTABLE;
    bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    bd.ByteWidth = vertexCount_ * (UINT)sizeof(GpuVertex);
    D3D11_SUBRESOURCE_DATA init{};
    init.pSysMem = gpuVerts.data();
    if (FAILED(device_->CreateBuffer(&bd, &init, &vb_))) {
        stats_.lastError = "创建顶点缓冲失败";
        return false;
    }

    // 索引缓冲
    bd.BindFlags = D3D11_BIND_INDEX_BUFFER;
    bd.ByteWidth = indexCount_ * (UINT)sizeof(uint32_t);
    init.pSysMem = mesh.indices.data();
    if (FAILED(device_->CreateBuffer(&bd, &init, &ib_))) {
        stats_.lastError = "创建索引缓冲失败";
        return false;
    }

    // 子网格 + 索引范围（按 materialIndex 分组已是 loader 的职责）
    subMeshes_.reserve(mesh.subMeshes.size());
    for (size_t i = 0; i < mesh.subMeshes.size(); ++i) {
        GpuSubMesh gs;
        gs.desc = mesh.subMeshes[i];
        if (gs.desc.indexCount == 0) continue;
        if (gs.desc.indexOffset + gs.desc.indexCount > indexCount_) continue; // 坏数据防御
        UINT mn = UINT_MAX, mx = 0;
        for (uint32_t k = 0; k < gs.desc.indexCount; ++k) {
            const uint32_t idx = mesh.indices[gs.desc.indexOffset + k];
            if (idx < mn) mn = idx;
            if (idx > mx) mx = idx;
        }
        if (mx >= vertexCount_) continue;
        gs.minIndex = mn;
        gs.maxIndex = mx;
        subMeshes_.push_back(gs);
    }
    if (subMeshes_.empty()) {
        GpuSubMesh gs;
        gs.desc.materialIndex = -1;
        gs.desc.indexOffset = 0;
        gs.desc.indexCount = indexCount_;
        gs.desc.name = "all";
        gs.minIndex = 0;
        gs.maxIndex = vertexCount_ - 1;
        subMeshes_.push_back(gs);
    }

    // 纹理（预览小图）
    textureSrv_.assign(model.textures.size(), (ID3D11ShaderResourceView*)nullptr);
    for (size_t i = 0; i < model.textures.size(); ++i) {
        int w = 0, h = 0;
        CreateTextureFromPreview(model.textures[i], &textureSrv_[i], &w, &h);
    }

    // 包围球
    modelCenter_[0] = modelCenter_[1] = modelCenter_[2] = 0.0f;
    modelRadius_ = 1.0f;
    float mn[3] = {1e30f, 1e30f, 1e30f}, mx[3] = {-1e30f, -1e30f, -1e30f};
    for (size_t i = 0; i < mesh.vertices.size(); ++i) {
        const Vertex& v = mesh.vertices[i];
        if (v.px < mn[0]) mn[0] = v.px;
        if (v.py < mn[1]) mn[1] = v.py;
        if (v.pz < mn[2]) mn[2] = v.pz;
        if (v.px > mx[0]) mx[0] = v.px;
        if (v.py > mx[1]) mx[1] = v.py;
        if (v.pz > mx[2]) mx[2] = v.pz;
    }
    bool finite = true;
    for (int k = 0; k < 3; ++k) {
        if (!(mn[k] <= mx[k]) || !(std::isfinite(mn[k]) && std::isfinite(mx[k]))) finite = false;
    }
    if (!finite) {
        mn[0] = mn[1] = mn[2] = -1.0f;
        mx[0] = mx[1] = mx[2] = 1.0f;
    }
    for (int k = 0; k < 3; ++k) modelCenter_[k] = (mn[k] + mx[k]) * 0.5f;
    float maxExt = 0.0f;
    for (int k = 0; k < 3; ++k) {
        const float e = (mx[k] - mn[k]) * 0.5f;
        if (e > maxExt) maxExt = e;
    }
    modelRadius_ = maxExt > 1e-5f ? maxExt * 1.4142f : 1.0f;

    stats_.drawCalls = (uint32_t)subMeshes_.size();
    stats_.triangles = indexCount_ / 3;
    stats_.textureCount = (uint32_t)textureSrv_.size();
    stats_.lastError.clear();
    camera_.target[0] = camera_.target[1] = camera_.target[2] = 0.0f;
    return true;
}

void D3DRenderer::ClearModel() {
    ReleaseModelResources();
    stats_.lastError = "未选择模型";
}

void D3DRenderer::ComputeBounds() {
    modelCenter_[0] = modelCenter_[1] = modelCenter_[2] = 0.0f;
    modelRadius_ = 1.0f;
}

void D3DRenderer::GetBounds(float outCenter[3], float* outRadius) const {
    outCenter[0] = modelCenter_[0];
    outCenter[1] = modelCenter_[1];
    outCenter[2] = modelCenter_[2];
    if (outRadius) *outRadius = modelRadius_;
}

void D3DRenderer::Orbit(float dxPixels, float dyPixels) {
    camera_.yaw -= dxPixels * 0.01f;
    camera_.pitch += dyPixels * 0.01f;
    const float limit = 1.5533f; // ~89°
    if (camera_.pitch > limit) camera_.pitch = limit;
    if (camera_.pitch < -limit) camera_.pitch = -limit;
    while (camera_.yaw > 6.2831853f) camera_.yaw -= 6.2831853f;
    while (camera_.yaw < 0.0f) camera_.yaw += 6.2831853f;
}

void D3DRenderer::Zoom(float wheelDelta) {
    const float steps = wheelDelta / 120.0f;
    camera_.distance *= std::pow(1.15f, -steps);
    if (camera_.distance < 0.35f) camera_.distance = 0.35f;
    if (camera_.distance > 12.0f) camera_.distance = 12.0f;
}

void D3DRenderer::ResetCamera() {
    camera_.Reset();   // Reset 已把 target 归零（target 是相对模型中心的偏移）
}

void D3DRenderer::SetSkinMatrices(const std::vector<float>* skinMatrices) {
    if (skinMatrices && !skinMatrices->empty()) {
        skinMatrices_ = *skinMatrices;
        haveSkin_ = true;
    } else {
        skinMatrices_.clear();
        haveSkin_ = false;
    }
    skinDirty_ = true;
}

void D3DRenderer::UpdateCameraMatrices(float* outView, float* outProj, float* outViewProj) {
    const float r = modelRadius_;
    const float dist = camera_.distance;
    const float cx = modelCenter_[0] + camera_.target[0];
    const float cy = modelCenter_[1] + camera_.target[1];
    const float cz = modelCenter_[2] + camera_.target[2];

    const float cp = std::cos(camera_.pitch), sp = std::sin(camera_.pitch);
    const float cyw = std::cos(camera_.yaw), syw = std::sin(camera_.yaw);
    const float eye[3] = {
        cx + dist * r * cp * syw * 2.0f,
        cy + dist * r * sp * 2.0f,
        cz + dist * r * cp * cyw * 2.0f,
    };
    const float at[3] = {cx, cy, cz};
    const float up[3] = {0.0f, 1.0f, 0.0f};

    MakeLookAt(eye, at, up, outView);
    const float aspect = (height_ > 0) ? (float)width_ / (float)height_ : 1.0f;
    const float nearZ = 0.01f * r;
    const float farZ = 1000.0f * r;
    MakePerspective(0.698f /*40°*/, aspect, nearZ, farZ, outProj);
    av::mat::multiply(outView, outProj, outViewProj);
}

bool D3DRenderer::RenderFrame() {
    if (!ready_ || !context_ || !rtView_) return false;

    // 背景清屏
    const float clear[4] = {0.05f, 0.06f, 0.09f, 1.0f};
    context_->OMSetRenderTargets(1, &rtView_, dsView_);
    context_->ClearRenderTargetView(rtView_, clear);
    if (dsView_) context_->ClearDepthStencilView(dsView_, D3D11_CLEAR_DEPTH | D3D11_CLEAR_STENCIL, 1.0f, 0);

    D3D11_VIEWPORT vp{};
    vp.Width = (float)width_;
    vp.Height = (float)height_;
    vp.MaxDepth = 1.0f;
    context_->RSSetViewports(1, &vp);

    if (subMeshes_.empty()) return true;

    float view[16], proj[16], viewProj[16];
    UpdateCameraMatrices(view, proj, viewProj);

    // Frame 常量
    FrameCBData fcb{};
    std::memcpy(fcb.viewProj, viewProj, sizeof(viewProj));
    {
        const float r = modelRadius_, d = camera_.distance;
        const float cp = std::cos(camera_.pitch), sp = std::sin(camera_.pitch);
        const float cyw = std::cos(camera_.yaw), syw = std::sin(camera_.yaw);
        fcb.eye[0] = modelCenter_[0] + d * r * cp * syw * 2.0f;
        fcb.eye[1] = modelCenter_[1] + d * r * sp * 2.0f;
        fcb.eye[2] = modelCenter_[2] + d * r * cp * cyw * 2.0f;
    }
    fcb.camDir[0] = 0; fcb.camDir[1] = 0; fcb.camDir[2] = 1;
    // 三光源：key 顶部前方 / fill 侧后 / rim 背后
    fcb.keyDir[0] = -0.45f; fcb.keyDir[1] = 0.80f; fcb.keyDir[2] = -0.40f;
    fcb.keyColor[0] = 1.00f; fcb.keyColor[1] = 0.97f; fcb.keyColor[2] = 0.92f;
    fcb.fillDir[0] = 0.75f; fcb.fillDir[1] = -0.15f; fcb.fillDir[2] = -0.65f;
    fcb.fillColor[0] = 0.42f; fcb.fillColor[1] = 0.52f; fcb.fillColor[2] = 0.70f;
    fcb.rimDir[0] = -0.2f; fcb.rimDir[1] = -0.5f; fcb.rimDir[2] = 0.85f;
    fcb.rimColor[0] = 0.55f; fcb.rimColor[1] = 0.40f; fcb.rimColor[2] = 0.75f;
    fcb.ambient[0] = 0.20f; fcb.ambient[1] = 0.22f; fcb.ambient[2] = 0.28f;
    fcb.time = (float)(::GetTickCount64() % 100000ull) * 0.001f;

    D3D11_MAPPED_SUBRESOURCE ms{};
    if (SUCCEEDED(context_->Map(cbFrame_, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) {
        std::memcpy(ms.pData, &fcb, sizeof(fcb));
        context_->Unmap(cbFrame_, 0);
    }

    // 蒙皮矩阵更新
    if (skinDirty_) {
        if (SUCCEEDED(context_->Map(cbSkin_, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) {
            const size_t n = skinMatrices_.size() / 16;
            const size_t copyBones = n > (size_t)kMaxBones ? (size_t)kMaxBones : n;
            std::memcpy(ms.pData, skinMatrices_.data(), copyBones * 16 * sizeof(float));
            context_->Unmap(cbSkin_, 0);
        }
        skinDirty_ = false;
    }

    context_->VSSetShader(skinVS_, nullptr, 0);
    context_->PSSetShader(mainPS_, nullptr, 0);
    context_->IASetInputLayout(inputLayout_);
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

    // slot 0 = 交错 GPU 顶点（pos/nrm/uv/骨骼索引/权重）
    ID3D11Buffer* vb = vb_;
    UINT stride = sizeof(GpuVertex);
    UINT offset = 0;
    context_->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
    ID3D11Buffer* ib = ib_;
    context_->IASetIndexBuffer(ib, DXGI_FORMAT_R32_UINT, 0);

    ID3D11Buffer* cbs[2] = {cbFrame_, cbSkin_};
    context_->VSSetConstantBuffers(0, 2, cbs);
    context_->PSSetConstantBuffers(0, 1, &cbFrame_);

    context_->OMSetDepthStencilState(dsEnable_, 0);
    context_->OMSetBlendState(bsOpaque_, nullptr, 0xFFFFFFFF);
    context_->RSSetState(rsSolid_);
    context_->PSSetSamplers(0, 1, &samplerLinear_);

    for (size_t i = 0; i < subMeshes_.size(); ++i) {
        const GpuSubMesh& gs = subMeshes_[i];
        ObjectCBData ocb{};
        const Material* mat = nullptr;
        if (gs.desc.materialIndex >= 0 && (size_t)gs.desc.materialIndex < materials_.size())
            mat = &materials_[(size_t)gs.desc.materialIndex];
        if (mat) {
            ocb.baseColor[0] = mat->baseColor[0];
            ocb.baseColor[1] = mat->baseColor[1];
            ocb.baseColor[2] = mat->baseColor[2];
            ocb.baseColor[3] = mat->baseColor[3];
            ocb.metallic = mat->metallic;
            ocb.roughness = mat->roughness;
            ocb.hasTex = 0;
            ID3D11ShaderResourceView* srv = nullptr;
            if (mat->diffuseTex >= 0 && (size_t)mat->diffuseTex < textureSrv_.size()) {
                srv = textureSrv_[(size_t)mat->diffuseTex];
                if (srv) ocb.hasTex = 1;
            }
            context_->PSSetShaderResources(0, 1, &srv);
        } else {
            ocb.baseColor[0] = 0.75f; ocb.baseColor[1] = 0.75f; ocb.baseColor[2] = 0.78f;
            ocb.baseColor[3] = 1.0f;
            ocb.roughness = 0.85f;
            ocb.hasTex = 0;
            ID3D11ShaderResourceView* none = nullptr;
            context_->PSSetShaderResources(0, 1, &none);
        }
        if (SUCCEEDED(context_->Map(cbObject_, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) {
            std::memcpy(ms.pData, &ocb, sizeof(ocb));
            context_->Unmap(cbObject_, 0);
        }
        context_->PSSetConstantBuffers(2, 1, &cbObject_);
        context_->DrawIndexed(gs.desc.indexCount, gs.desc.indexOffset, 0);
    }

    // 解绑 SRV 防止下次 UpdateSubresource 冲突
    ID3D11ShaderResourceView* nullSrv = nullptr;
    context_->PSSetShaderResources(0, 1, &nullSrv);
    return true;
}

bool D3DRenderer::Readback(std::vector<uint32_t>* outPixels, int* w, int* h) {
    if (!outPixels || !rtTexture_ || !context_ || !device_) return false;
    D3D11_TEXTURE2D_DESC td{};
    rtTexture_->GetDesc(&td);
    td.Usage = D3D11_USAGE_STAGING;
    td.BindFlags = 0;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    td.MiscFlags = 0;
    ID3D11Texture2D* staging = nullptr;
    if (FAILED(device_->CreateTexture2D(&td, nullptr, &staging))) return false;
    context_->CopyResource(staging, rtTexture_);
    D3D11_MAPPED_SUBRESOURCE ms{};
    bool ok = false;
    if (SUCCEEDED(context_->Map(staging, 0, D3D11_MAP_READ, 0, &ms))) {
        outPixels->resize((size_t)td.Width * td.Height);
        for (UINT y = 0; y < td.Height; ++y) {
            const uint32_t* src = (const uint32_t*)((const unsigned char*)ms.pData + (size_t)y * ms.RowPitch);
            std::memcpy(outPixels->data() + (size_t)y * td.Width, src, (size_t)td.Width * 4);
        }
        context_->Unmap(staging, 0);
        ok = true;
    }
    staging->Release();
    if (w) *w = (int)td.Width;
    if (h) *h = (int)td.Height;
    return ok;
}

} // namespace av
