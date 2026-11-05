// d3d_renderer.h —— D3D11 实时预览渲染器
// OWNER: winui-list
//
// 职责：
//   - 把 av::ModelData 的几何上传到 GPU（顶点/索引/纹理）
//   - 相机：包围球自动 fit、鼠标拖拽旋转、滚轮缩放、双击复位
//   - 每帧渲染到屏外 BGRA 纹理（不独占 swapchain，便于 UI 自由布局）
//   - 4 骨骼蒙皮：每帧由 anim 采样得到的 skin 矩阵上传到常量缓冲
//
// 约定：矩阵行优先、行向量 × 矩阵（v' = v * M）。HLSL 里用 row_major 声明匹配，
//       因此 CPU 侧不需要转置。
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

#include <cstdint>
#include <string>
#include <vector>

#include "model_data.h"

namespace av {

// 相机控制状态（UI 线程持有，渲染器读取）
struct CameraState {
    float yaw = 0.61f;      // 弧度（绕 Y 轴）
    float pitch = 0.35f;    // 弧度（绕 X 轴）
    // distance 是「刚好把包围球框满」这一基准距离的倍数（见 D3DRenderer::FitDistance）。
    // 必须与 FitDistance 的语义一致：FitDistance 已经包含了半径与 FOV，
    // 所以 1.0 = 恰到好处的取景。旧值 2.5 会让默认视角缩到画面的 ~1/2.7，
    // 这正是「模型没在正中心 / 显示不全」的观感来源（实测模型只占预览区 2.4%）。
    float distance = 1.0f;
    float target[3] = {0, 0, 0};
    void Reset() {
        yaw = 0.61f;
        pitch = 0.35f;
        distance = 1.0f;
        target[0] = target[1] = target[2] = 0.0f;
    }
};

// 渲染统计（自测/调试用）
struct RenderStats {
    uint32_t drawCalls = 0;
    uint32_t triangles = 0;
    uint32_t textureCount = 0;
    bool     gpuReady = false;
    std::string lastError;
};

class D3DRenderer {
public:
    D3DRenderer();
    ~D3DRenderer();

    D3DRenderer(const D3DRenderer&) = delete;
    D3DRenderer& operator=(const D3DRenderer&) = delete;

    // 创建 D3D11 设备与渲染目标（离屏纹理）。失败返回 false，GetStats().lastError 有原因。
    bool Initialize(HWND hwnd, int width, int height);
    void Shutdown();
    bool IsReady() const { return ready_; }

    // 尺寸变化时重建离屏 RT
    bool Resize(int width, int height);
    int  Width() const { return width_; }
    int  Height() const { return height_; }

    // 设置当前要预览的模型（浅拷贝几何到 GPU，然后可以释放 CPU 端 ModelData）
    // 返回 false 表示上传失败；仍会显示占位网格。
    bool SetModel(const ModelData& model);
    void ClearModel();

    // 相机
    void SetCamera(const CameraState& c) { camera_ = c; }
    const CameraState& GetCamera() const { return camera_; }
    void Orbit(float dxPixels, float dyPixels);   // 拖拽旋转
    void Zoom(float wheelDelta);                  // 滚轮缩放
    void ResetCamera();

    // 每帧更新：skinnedMatrices 可为空（静态网格）。
    // skinMatrices: bones.size()*16 个 float，行优先，每骨骼一个 4x4。
    void SetSkinMatrices(const std::vector<float>* skinMatrices);

    // 渲染一帧到内部离屏纹理。返回 false 表示设备丢失或失败（UI 可重建）。
    bool RenderFrame();

    // 拿到渲染结果（BGRA8，与 D2D/D3D 兼容），只有在 RenderFrame 成功后有效。
    // 同时返回 D3D SRV 供 D2D 直接画（如果 UI 用 D3D 共享纹理路径）。
    ID3D11Texture2D*  ResultTexture() const { return rtTexture_; }
    ID3D11ShaderResourceView* ResultView() const { return rtSrv_; }

    // 读取渲染结果到 CPU（每像素 BGRA，行从上到下）。供无头自测/截图使用。
    bool Readback(std::vector<uint32_t>* outPixels, int* w, int* h);

    const RenderStats& GetStats() const { return stats_; }
    bool HasGeometry() const { return !subMeshes_.empty(); }
    // 模型包围球（object 空间中心与半径），用于 UI 提示
    void GetBounds(float outCenter[3], float* outRadius) const;

private:
    struct GpuSubMesh {
        av::SubMesh desc;
        UINT minIndex = 0;
        UINT maxIndex = 0;
    };

    bool CreateDevice(HWND hwnd);
    bool CreateShaders();
    bool CreateStates();
    bool CreateRenderTargets(int w, int h);
    void ReleaseModelResources();
    bool CreateTextureFromPreview(const TextureInfo& tex, ID3D11ShaderResourceView** out, int* w, int* h);
    void BuildFallbackGeometry();
    void ComputeBounds();
    float FitDistance() const;
    void UpdateCameraMatrices(float* outView, float* outProj, float* outViewProj);

private:
    HWND  hwnd_ = nullptr;
    bool  ready_ = false;
    int   width_ = 0;
    int   height_ = 0;

    ID3D11Device*            device_ = nullptr;
    ID3D11DeviceContext*     context_ = nullptr;
    IDXGISwapChain*          swapChain_ = nullptr;   // 未使用（离屏渲染），保留清理
    ID3D11Texture2D*         rtTexture_ = nullptr;
    ID3D11RenderTargetView*  rtView_ = nullptr;
    ID3D11ShaderResourceView* rtSrv_ = nullptr;
    ID3D11Texture2D*         dsTexture_ = nullptr;
    ID3D11DepthStencilView*  dsView_ = nullptr;

    ID3D11VertexShader* skinVS_ = nullptr;
    ID3D11PixelShader*  mainPS_ = nullptr;
    ID3D11InputLayout*  inputLayout_ = nullptr;

    ID3D11Buffer* vb_ = nullptr;
    ID3D11Buffer* ib_ = nullptr;
    ID3D11Buffer* cbFrame_ = nullptr;   // viewProj / 光
    ID3D11Buffer* cbObject_ = nullptr;  // 材质参数
    ID3D11Buffer* cbSkin_ = nullptr;    // 骨骼矩阵（最多 512 骨骼）

    std::vector<GpuSubMesh> subMeshes_;
    std::vector<Material>   materials_;
    std::vector<ID3D11ShaderResourceView*> textureSrv_;  // 与 ModelData::textures 对齐
    std::vector<ID3D11SamplerState*>       samplers_;

    ID3D11RasterizerState* rsSolid_ = nullptr;
    ID3D11RasterizerState* rsWire_ = nullptr;
    ID3D11DepthStencilState* dsEnable_ = nullptr;
    ID3D11DepthStencilState* dsDisable_ = nullptr;
    ID3D11BlendState* bsOpaque_ = nullptr;
    ID3D11BlendState* bsAlpha_ = nullptr;
    ID3D11SamplerState* samplerLinear_ = nullptr;
    ID3D11SamplerState* samplerPoint_ = nullptr;

    CameraState camera_;
    float modelCenter_[3] = {0, 0, 0};
    float modelRadius_ = 1.0f;
    uint32_t vertexCount_ = 0;
    uint32_t indexCount_ = 0;

    std::vector<float> skinMatrices_;   // CPU 侧缓存
    bool skinDirty_ = true;
    bool haveSkin_ = false;

    RenderStats stats_;
};

// 生成一个内置的占位模型（立方体 + 地面网格），用于未链接解析模块时预览
void MakeFallbackModel(ModelData* out);

} // namespace av
