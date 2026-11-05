// viewer_app.h —— 主窗口：布局、列表、详情、动画控制与交互
// OWNER: winui-list
//
// 结构：
//   顶部工具条（选择文件夹 / 重新扫描 / 刷新 / 搜索框）
//   左侧：可滚动缩略图网格（缩略图 + 文件名 + 分类 + 三角面）
//   右侧：上方 D3D11 预览，下方信息面板 + 动画控件
//   底部：状态栏（进度 / 缓存命中 / 耗时）
//
// 绘制：Direct2D 1.0 + DirectWrite（文字）。3D 预览由 D3DRenderer 渲染到离屏纹理，
//       再用 D2D 的 Bitmap 画到预览区（跨 device 需用 D3D 共享纹理或 DIB 中转）。
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

// ── 关键：必须在 d2d1.h 之前 undef DrawText ──
// windows.h 定义 `#define DrawText DrawTextW`。若先包含 windows.h 再包含 d2d1.h，
// d2d1.h 中的 `STDMETHOD_(void, DrawText)` 会被宏替换成 DrawTextW，
// 使成员名随包含顺序漂移（一处 C2039，一处又能编过）。这里先清掉，让成员名稳定为 DrawText。
#ifdef DrawText
#undef DrawText
#endif
#ifdef DrawTextEx
#undef DrawTextEx
#endif

#include <d2d1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <memory>
#include <string>
#include <vector>

#include "d3d_renderer.h"
#include "scan_worker.h"

namespace av {

template <typename T>
using ComPtr = Microsoft::WRL::ComPtr<T>;

class ViewerApp {
public:
    ViewerApp();
    ~ViewerApp();

    ViewerApp(const ViewerApp&) = delete;
    ViewerApp& operator=(const ViewerApp&) = delete;

    // rootUtf8 为命令行指定的扫描根（可为空 -> 自动探测）
    bool Initialize(HINSTANCE hInstance, const std::wstring& cliRoot);
    int  Run();
    void Shutdown();

    // 供 ui_selftest 使用：不创建窗口也能跑扫描与布局计算
    static std::wstring DetectAssetRoot(HINSTANCE hInstance);

    // 把当前界面渲染到离屏 WIC 位图并存为 PNG（不依赖屏幕合成，用于交付截图自检）。
    // 需要窗口已创建且已完成至少一次布局；返回 true 表示文件已写出。
    bool SaveScreenshot(const std::wstring& pngPath);

    // 截图模式：启动后跑消息泵，等待扫描/缩略图就绪，离屏渲染存 PNG 再退出。
    // 返回 true 表示连续跑满至少 MinFrames 帧且截图成功。
    bool RunCaptureMode(const std::wstring& pngPath);

    // ---- 布局计算（自测可单独调用）----
    struct Rect {
        float l = 0, t = 0, r = 0, b = 0;
        float W() const { return r - l; }
        float H() const { return b - t; }
        bool Contains(float x, float y) const { return x >= l && x < r && y >= t && y < b; }
    };
    struct LayoutInfo {
        Rect toolbar;
        Rect list;
        Rect preview;
        Rect info;
        Rect status;
        Rect searchBox;
        float cellW = 0, cellH = 0;
        int columns = 1;
    };
    // 纯函数：根据窗口客户区尺寸计算布局（自测可直接断言）
    static LayoutInfo ComputeLayout(float clientW, float clientH, float dpiScale);

private:
    struct ListItem {
        std::wstring name;
        std::wstring category;
        std::wstring path;
        std::wstring ext;
        uint64_t fileSize = 0;
        uint32_t triangles = 0;
        uint32_t vertices = 0;
        uint32_t materials = 0;
        uint32_t bones = 0;
        uint32_t clips = 0;
        std::vector<std::wstring> clipNames;
        bool parsed = false;
        bool ok = true;
        std::wstring error;
        std::wstring warning;
        int thumbState = kThumbPending;
        ID2D1Bitmap* thumbBitmap = nullptr;   // 设备相关，resize 时重建
        std::vector<uint32_t> thumbPixels;    // 原始 BGRA，用于重建位图
        int thumbW = 0, thumbH = 0;
    };

    // ---- 窗口过程 ----
    static LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);
    LRESULT HandleMessage(UINT msg, WPARAM wp, LPARAM lp);

    // ---- 初始化 ----
    bool CreateMainWindow();
    bool CreateD2D();
    bool CreateTextFormats();
    bool CreateBrushes();
    void ReleaseGraphicsResources();

    // ---- 数据 ----
    void StartScan(const std::wstring& root);
    void OnWorkerMessage(UINT msg, WPARAM wp, LPARAM lp);
    void ApplyWorkerData();
    void RebuildFilter();
    void ScrollToFilteredIndex(int fi);
    void SelectItem(int index);
    void SelectByOffset(int delta);
    void LoadDetailFor(int itemIndex);
    void OpenSelectedFile();
    void ChooseDirectory();
    void RefreshThumbs();
    void RefreshPriorities();

    // ---- 动画 ----
    void TogglePlay();
    void OnAnimTick();
    void ScrubTo(float mouseX);
    void UpdateSkinAt(double t);

    // ---- 绘制 ----
    void Render();
    void DrawToolbar();
    void DrawList();
    void DrawPreview();
    void DrawInfoPanel();
    void DrawStatusBar();
    void DrawPlaceholderThumb(const Rect& r, int state);
    void UpdatePreviewBitmap();
    void EnsureThumbBitmaps();

    // ---- 命中测试/交互 ----
    void UpdateScrollFromDpi();
    int  HitTestItem(float x, float y) const;
    void ScrollBy(float dy);
    void ClampScroll();
    void LayoutChanged();
    void SyncHighlightToSelection();

    // ---- 列表几何 ----
    float RowHeight() const;
    float ContentHeight() const;
    Rect  CellRect(int index) const;

private:
    HINSTANCE hInstance_ = nullptr;
    HWND      hwnd_ = nullptr;
    UINT      dpi_ = 96;
    float     scale_ = 1.0f;
    int       clientW_ = 0, clientH_ = 0;
    bool      running_ = true;
    int       frameTickMs_ = 16;
    bool      animating_ = false;
    ULONGLONG lastFrameTick_ = 0;

    // D2D
    ID2D1Factory*       d2dFactory_ = nullptr;
    // hwndTarget_ 是窗口渲染目标（只有它有 Resize）；
    // renderTarget_ 是当前正在绘制的目标（窗口时为 hwndTarget_，离屏截图时为 WIC target）。
    ID2D1HwndRenderTarget* hwndTarget_ = nullptr;
    ID2D1RenderTarget* renderTarget_ = nullptr;
    IWICImagingFactory* wicFactory_ = nullptr;
    ID2D1SolidColorBrush* bgBrush_ = nullptr;
    ID2D1SolidColorBrush* panelBrush_ = nullptr;
    ID2D1SolidColorBrush* accentBrush_ = nullptr;
    ID2D1SolidColorBrush* textBrush_ = nullptr;
    ID2D1SolidColorBrush* subTextBrush_ = nullptr;
    ID2D1SolidColorBrush* borderBrush_ = nullptr;
    ID2D1SolidColorBrush* errorBrush_ = nullptr;
    ID2D1SolidColorBrush* buttonBrush_ = nullptr;
    ID2D1SolidColorBrush* buttonHotBrush_ = nullptr;
    ID2D1SolidColorBrush* selectBrush_ = nullptr;
    ID2D1SolidColorBrush* progressBrush_ = nullptr;

    // DWrite
    IDWriteFactory*     dwriteFactory_ = nullptr;
    IDWriteTextFormat*  fmtTitle_ = nullptr;
    IDWriteTextFormat*  fmtBody_ = nullptr;
    IDWriteTextFormat*  fmtSmall_ = nullptr;
    IDWriteTextFormat*  fmtCellName_ = nullptr;
    IDWriteTextFormat*  fmtCellSub_ = nullptr;

    // 预览
    D3DRenderer renderer_;
    ID2D1Bitmap* previewBitmap_ = nullptr;
    std::vector<uint32_t> previewPixels_;
    int previewBmpW_ = 0, previewBmpH_ = 0;
    bool previewDirty_ = true;
    bool havePreview_ = false;

    // 数据
    std::unique_ptr<ScanWorker> worker_;
    std::vector<ListItem> items_;        // 全量
    std::vector<int>      filtered_;     // 当前过滤后的索引（指向 items_）
    int  selectedItem_ = -1;             // 指向 items_
    int  selectedFiltered_ = -1;         // 指向 filtered_
    float scrollY_ = 0.0f;
    LayoutInfo layout_;
    bool layoutValid_ = false;

    // 搜索
    std::wstring searchText_;
    bool searchFocused_ = false;

    // 按钮
    int hotButton_ = -1;
    enum ButtonId { kBtnChooseDir = 0, kBtnRescan = 1, kBtnRefresh = 2, kBtnOpen = 3,
                    kBtnPlay = 4, kBtnPrev = 5, kBtnNext = 6, kBtnLoop = 7, kBtnCount = 8 };
    Rect buttonRects_[kBtnCount];
    Rect progressRect_;
    bool hoverProgress_ = false;
    bool draggingProgress_ = false;

    // 动画
    ModelData currentModel_;
    bool currentModelLoaded_ = false;
    int  currentClip_ = -1;
    double clipTime_ = 0.0;
    bool playing_ = false;
    bool looping_ = true;
    std::vector<float> worldMats_;
    std::vector<float> skinMats_;
    int  clipBoneCount_ = 0;

    // 鼠标
    bool   mouseDownLeft_ = false;
    POINT  lastMouse_{0, 0};
    POINT  dragStart_{0, 0};
    bool   draggingCamera_ = false;
    bool   draggingScroll_ = false;
    float  scrollGrabY_ = 0.0f;
    bool   scrollbarVisible_ = false;
    // 注意：scrollTrack_ 只在 DrawList() 里赋值。若点击早于首次绘制，它就是空的
    // （全 0 矩形→Contains 永假，反而安全）；不要把它当成"已初始化"来用。
    Rect   scrollThumb_;
    Rect   scrollTrack_;

    // 缩略图"孤儿暂存区"：kMsgThumbReady 早于 kMsgScanDone 到达时先存这里，
    // ApplyWorkerData() 建好 items_ 后并回去（像素是取走即删的，丢了就要重算）。
    std::vector<ThumbResult> pendingThumbs_;

    // 状态
    std::wstring statusText_;
    std::wstring rootPath_;
    std::wstring cacheDir_;
    bool  scanDone_ = false;
    size_t scanTotal_ = 0;
    double scanMs_ = 0;
    std::wstring fatalError_;
};

} // namespace av
