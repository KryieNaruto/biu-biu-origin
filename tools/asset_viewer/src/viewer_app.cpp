// viewer_app.cpp —— 主窗口实现
#include "viewer_app.h"

#include "av_util.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <shlobj.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <windowsx.h>

// ── 必须在所有 windows 头（以及包含 d2d1.h 的 viewer_app.h）之后 ──
// windows.h 把 DrawText 定义成宏 DrawTextW，而 d2d1.h 里的
// STDMETHOD_(void, DrawText) 用的是真标识符 DrawText。
// 宏在 d2d1.h 解析时就把成员名替换掉了，所以这里必须 #undef，
// 之后统一通过 DrawText2D 封装调用。
#ifdef DrawText
#undef DrawText
#endif
#ifdef DrawTextEx
#undef DrawTextEx
#endif

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

namespace av {

namespace {

inline void DrawText2D(ID2D1RenderTarget* rt, const wchar_t* text, UINT32 len,
                       IDWriteTextFormat* fmt, const D2D1_RECT_F* rect, ID2D1Brush* brush,
                       D2D1_DRAW_TEXT_OPTIONS opts = D2D1_DRAW_TEXT_OPTIONS_CLIP,
                       DWRITE_MEASURING_MODE mm = DWRITE_MEASURING_MODE_NATURAL) {
    if (!rt || !text || !fmt || !brush) return;
    rt->DrawText(text, len, fmt, rect, brush, opts, mm);
}

const wchar_t* kWindowClass = L"BiuBiuAssetViewerWnd";
const wchar_t* kWindowTitle = L"BiuBiu 素材快查看器";

// 配色（深色主题）
const D2D1_COLOR_F kColBg        = D2D1::ColorF(0x15181D);
const D2D1_COLOR_F kColPanel     = D2D1::ColorF(0x1C2027);
const D2D1_COLOR_F kColPanel2    = D2D1::ColorF(0x232830);
const D2D1_COLOR_F kColBorder    = D2D1::ColorF(0x2E3540);
const D2D1_COLOR_F kColAccent    = D2D1::ColorF(0x4C9AFF);
const D2D1_COLOR_F kColAccentDim = D2D1::ColorF(0x2D5F9E);
const D2D1_COLOR_F kColText      = D2D1::ColorF(0xE6E9EE);
const D2D1_COLOR_F kColSubText   = D2D1::ColorF(0x8B93A1);
const D2D1_COLOR_F kColError     = D2D1::ColorF(0xFF5A5A);
const D2D1_COLOR_F kColWarn      = D2D1::ColorF(0xFFB454);
const D2D1_COLOR_F kColButton    = D2D1::ColorF(0x2A303A);
const D2D1_COLOR_F kColButtonHot = D2D1::ColorF(0x39424F);
const D2D1_COLOR_F kColSelect    = D2D1::ColorF(0x2D5F9E);

const float kToolbarH = 52.0f;
const float kStatusH  = 28.0f;
const float kListWidth = 380.0f;
const float kCellW = 176.0f;
const float kCellH = 196.0f;
const float kCellGap = 8.0f;
const float kPad = 10.0f;

std::wstring FormatW(uint64_t v) { return avu::Wide(avu::FormatInt(v)); }
std::wstring FormatSizeW(uint64_t v) { return avu::Wide(avu::FormatSize(v)); }

// 截断过长的文本（按字符数粗略）
std::wstring Ellipsis(const std::wstring& s, size_t maxChars) {
    if (s.size() <= maxChars) return s;
    if (maxChars <= 1) return s.substr(0, maxChars);
    return s.substr(0, maxChars - 1) + L"…";
}

std::wstring ToLowerW(std::wstring s) {
    for (size_t i = 0; i < s.size(); ++i) s[i] = (wchar_t)::towlower(s[i]);
    return s;
}

} // namespace

// ===========================================================================
// 构造/析构
// ===========================================================================
ViewerApp::ViewerApp() {}
ViewerApp::~ViewerApp() { Shutdown(); }

// ===========================================================================
// 布局（纯函数，便于自测）
// ===========================================================================
ViewerApp::LayoutInfo ViewerApp::ComputeLayout(float clientW, float clientH, float s) {
    LayoutInfo L;
    L.toolbar.l = 0; L.toolbar.t = 0;
    L.toolbar.r = clientW; L.toolbar.b = kToolbarH * s;
    L.status.l = 0; L.status.t = clientH - kStatusH * s;
    L.status.r = clientW; L.status.b = clientH;

    const float midTop = L.toolbar.b;
    const float midBot = L.status.t;
    float listW = kListWidth * s;
    if (listW > clientW * 0.45f) listW = clientW * 0.45f;
    if (listW < 220.0f * s && clientW > 500.0f) listW = 220.0f * s;

    L.list.l = 0; L.list.t = midTop;
    L.list.r = listW; L.list.b = midBot;

    L.preview.l = listW; L.preview.t = midTop;
    L.preview.r = clientW;
    float infoH = (midBot - midTop) * 0.42f;
    if (infoH < 150.0f * s) infoH = 150.0f * s;
    if (infoH > (midBot - midTop) * 0.7f) infoH = (midBot - midTop) * 0.7f;
    L.preview.b = midBot - infoH;
    L.info.l = listW; L.info.t = L.preview.b;
    L.info.r = clientW; L.info.b = midBot;

    L.searchBox.r = clientW - kPad * s;
    L.searchBox.l = L.searchBox.r - 260.0f * s;
    if (L.searchBox.l < 520.0f * s) L.searchBox.l = 520.0f * s;
    L.searchBox.t = 10.0f * s;
    L.searchBox.b = kToolbarH * s - 10.0f * s;

    L.cellW = kCellW * s;
    L.cellH = kCellH * s;
    const float inner = L.list.W() - kPad * 2.0f * s - 12.0f * s; // 留滚动条
    int cols = (int)((inner + kCellGap * s) / (L.cellW + kCellGap * s));
    if (cols < 1) cols = 1;
    L.columns = cols;
    return L;
}

float ViewerApp::RowHeight() const { return layout_.cellH + kCellGap * scale_; }

float ViewerApp::ContentHeight() const {
    if (layout_.columns < 1) return 0;
    const int rows = (int)((filtered_.size() + layout_.columns - 1) / layout_.columns);
    return rows * RowHeight() + kPad * scale_;
}

ViewerApp::Rect ViewerApp::CellRect(int index) const {
    Rect out;
    if (layout_.columns < 1) return out;
    const int col = index % layout_.columns;
    const int row = index / layout_.columns;
    const float x = layout_.list.l + kPad * scale_ + col * (layout_.cellW + kCellGap * scale_);
    const float y = layout_.list.t + kPad * scale_ + row * RowHeight() - scrollY_;
    out.l = x;
    out.t = y;
    out.r = x + layout_.cellW;
    out.b = y + layout_.cellH;
    return out;
}

void ViewerApp::ClampScroll() {
    const float contentH = ContentHeight();
    const float viewH = layout_.list.H();
    float maxScroll = contentH - viewH;
    if (maxScroll < 0) maxScroll = 0;
    if (scrollY_ > maxScroll) scrollY_ = maxScroll;
    if (scrollY_ < 0) scrollY_ = 0;
}

void ViewerApp::ScrollBy(float dy) {
    scrollY_ += dy;
    ClampScroll();
}

void ViewerApp::SyncHighlightToSelection() {
    if (selectedItem_ < 0) return;
    for (size_t i = 0; i < filtered_.size(); ++i) {
        if (filtered_[i] == selectedItem_) {
            selectedFiltered_ = (int)i;
            return;
        }
    }
    selectedFiltered_ = -1;
}

// ===========================================================================
// 资源
// ===========================================================================
bool ViewerApp::Initialize(HINSTANCE hInstance, const std::wstring& cliRoot) {
    hInstance_ = hInstance;

    HRESULT hr = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    (void)hr; // 可能已被初始化（RPC_E_CHANGED_MODE）

    if (!CreateMainWindow()) return false;

    std::wstring root = cliRoot;
    if (root.empty()) root = DetectAssetRoot(hInstance_);
    if (root.empty() || !avu::DirExists(root)) {
        fatalError_ = L"素材目录不存在：" + root;
    }

    // 缓存目录：<exe目录>/cache
    wchar_t exePath[MAX_PATH] = {0};
    ::GetModuleFileNameW(hInstance_, exePath, MAX_PATH);
    std::wstring exeDir = avu::ParentDir(exePath);
    if (exeDir.empty()) exeDir = L".";
    cacheDir_ = exeDir + L"\\cache";

    StartScan(root);
    LayoutChanged();
    return true;
}

std::wstring ViewerApp::DetectAssetRoot(HINSTANCE hInstance) {
    wchar_t exePath[MAX_PATH] = {0};
    ::GetModuleFileNameW(hInstance, exePath, MAX_PATH);
    std::wstring dir = avu::ParentDir(exePath);

    // 最多向上看 5 层，找含「素材」子目录或 project.godot 的目录
    for (int i = 0; i < 6 && !dir.empty(); ++i) {
        const std::wstring assets = dir + L"\\素材";
        if (avu::DirExists(assets)) return assets;
        if (avu::FileExists(dir + L"\\project.godot")) {
            if (avu::DirExists(assets)) return assets;
        }
        std::wstring parent = avu::ParentDir(dir);
        if (parent == dir || parent.empty()) break;
        dir = parent;
    }
    // 兜底：exe 同级的 ../素材
    std::wstring dir2 = avu::ParentDir(avu::ParentDir(exePath));
    if (!dir2.empty() && avu::DirExists(dir2 + L"\\素材")) return dir2 + L"\\素材";
    return std::wstring();
}

bool ViewerApp::CreateMainWindow() {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS;
    wc.lpfnWndProc = &ViewerApp::WndProc;
    wc.hInstance = hInstance_;
    // 系统资源用 MAKEINTRESOURCEW 显式给出宽字符句柄，避免 UNICODE 未定义时
    // IDC_ARROW / IDI_APPLICATION 展开成 LPSTR 导致调用 *W API 报 C2664。
    wc.hCursor = ::LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));   // IDC_ARROW
    wc.hIcon = ::LoadIconW(nullptr, MAKEINTRESOURCEW(32512));       // IDI_APPLICATION
    wc.hbrBackground = nullptr;
    wc.lpszClassName = kWindowClass;
    if (!::RegisterClassExW(&wc)) {
        if (::GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;
    }

    dpi_ = ::GetDpiForSystem();
    scale_ = (float)dpi_ / 96.0f;

    RECT wr = {0, 0, (LONG)(1400 * scale_), (LONG)(900 * scale_)};
    ::AdjustWindowRectEx(&wr, WS_OVERLAPPEDWINDOW, FALSE, 0);
    hwnd_ = ::CreateWindowExW(0, kWindowClass, kWindowTitle, WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                              CW_USEDEFAULT, CW_USEDEFAULT, wr.right - wr.left, wr.bottom - wr.top,
                              nullptr, nullptr, hInstance_, this);
    if (!hwnd_) return false;

    ::ShowWindow(hwnd_, SW_SHOW);
    ::UpdateWindow(hwnd_);
    return true;
}

bool ViewerApp::CreateD2D() {
    if (d2dFactory_) return true;
    HRESULT hr = ::D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &d2dFactory_);
    if (FAILED(hr) || !d2dFactory_) return false;

    hr = ::DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                               reinterpret_cast<IUnknown**>(&dwriteFactory_));
    if (FAILED(hr) || !dwriteFactory_) return false;

    // WIC 工厂：仅供 SaveScreenshot 离屏截图用，失败不影响正常绘制
    if (!wicFactory_) {
        ::CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                           IID_PPV_ARGS(&wicFactory_));
    }

    if (!CreateTextFormats()) return false;
    return true;
}

bool ViewerApp::CreateTextFormats() {
    struct FmtSpec {
        IDWriteTextFormat** out;
        float size;
        DWRITE_FONT_WEIGHT weight;
    };
    FmtSpec specs[] = {{&fmtTitle_, 15.0f, DWRITE_FONT_WEIGHT_SEMI_BOLD},
                       {&fmtBody_, 13.0f, DWRITE_FONT_WEIGHT_NORMAL},
                       {&fmtSmall_, 11.5f, DWRITE_FONT_WEIGHT_NORMAL},
                       {&fmtCellName_, 12.0f, DWRITE_FONT_WEIGHT_SEMI_BOLD},
                       {&fmtCellSub_, 10.5f, DWRITE_FONT_WEIGHT_NORMAL}};
    for (size_t i = 0; i < _countof(specs); ++i) {
        HRESULT hr = dwriteFactory_->CreateTextFormat(
            L"Microsoft YaHei UI", nullptr, specs[i].weight, DWRITE_FONT_STYLE_NORMAL,
            DWRITE_FONT_STRETCH_NORMAL, specs[i].size * scale_, L"zh-cn", specs[i].out);
        if (FAILED(hr)) {
            // 回退字体
            hr = dwriteFactory_->CreateTextFormat(
                L"Segoe UI", nullptr, specs[i].weight, DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL, specs[i].size * scale_, L"", specs[i].out);
        }
        if (FAILED(hr)) return false;
        (*specs[i].out)->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        (*specs[i].out)->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
    }
    return true;
}

bool ViewerApp::CreateBrushes() {
    if (!renderTarget_) return false;
    struct B { ID2D1SolidColorBrush** b; const D2D1_COLOR_F* c; };
    auto make = [this](ID2D1SolidColorBrush** out, const D2D1_COLOR_F& c) -> bool {
        if (*out) return true;
        return SUCCEEDED(renderTarget_->CreateSolidColorBrush(c, out));
    };
    if (!make(&bgBrush_, kColBg)) return false;
    if (!make(&panelBrush_, kColPanel)) return false;
    if (!make(&accentBrush_, kColAccent)) return false;
    if (!make(&textBrush_, kColText)) return false;
    if (!make(&subTextBrush_, kColSubText)) return false;
    if (!make(&borderBrush_, kColBorder)) return false;
    if (!make(&errorBrush_, kColError)) return false;
    if (!make(&buttonBrush_, kColButton)) return false;
    if (!make(&buttonHotBrush_, kColButtonHot)) return false;
    if (!make(&selectBrush_, kColSelect)) return false;
    if (!make(&progressBrush_, kColAccentDim)) return false;
    (void)sizeof(B);
    return true;
}

void ViewerApp::ReleaseGraphicsResources() {
    // 位图必须早于渲染目标释放
    for (size_t i = 0; i < items_.size(); ++i) {
        if (items_[i].thumbBitmap) {
            items_[i].thumbBitmap->Release();
            items_[i].thumbBitmap = nullptr;
        }
    }
    if (previewBitmap_) { previewBitmap_->Release(); previewBitmap_ = nullptr; }
    struct B { ID2D1SolidColorBrush** b; };
    ID2D1SolidColorBrush** brushes[] = {&bgBrush_, &panelBrush_, &accentBrush_, &textBrush_,
                                        &subTextBrush_, &borderBrush_, &errorBrush_,
                                        &buttonBrush_, &buttonHotBrush_, &selectBrush_,
                                        &progressBrush_};
    for (size_t i = 0; i < _countof(brushes); ++i) {
        if (*brushes[i]) {
            (*brushes[i])->Release();
            *brushes[i] = nullptr;
        }
    }
    // renderTarget_ 只是别名，真正持有引用的是 hwndTarget_
    renderTarget_ = nullptr;
    if (hwndTarget_) {
        hwndTarget_->Release();
        hwndTarget_ = nullptr;
    }
    (void)sizeof(B);
}

void ViewerApp::LayoutChanged() {
    RECT rc;
    ::GetClientRect(hwnd_, &rc);
    clientW_ = rc.right - rc.left;
    clientH_ = rc.bottom - rc.top;
    if (clientW_ < 1) clientW_ = 1;
    if (clientH_ < 1) clientH_ = 1;

    layout_ = ComputeLayout((float)clientW_, (float)clientH_, scale_);
    layoutValid_ = true;
    scrollbarVisible_ = ContentHeight() > layout_.list.H();
    ClampScroll();

    if (hwndTarget_) {
        const D2D1_SIZE_U sz = D2D1::SizeU((UINT32)clientW_, (UINT32)clientH_);
        hwndTarget_->Resize(sz);
    }
    {
        const float pw = layout_.preview.W();
        const float ph = layout_.preview.H();
        if (pw > 8 && ph > 8) {
            renderer_.Resize((int)pw, (int)ph);
        }
    }
    previewDirty_ = true;
}

void ViewerApp::UpdateScrollFromDpi() {}

// ===========================================================================
// 扫描 / 数据
// ===========================================================================
void ViewerApp::StartScan(const std::wstring& root) {
    rootPath_ = root;
    if (!worker_) worker_.reset(new ScanWorker());

    // 清空旧数据
    for (size_t i = 0; i < items_.size(); ++i) {
        if (items_[i].thumbBitmap) {
            items_[i].thumbBitmap->Release();
            items_[i].thumbBitmap = nullptr;
        }
    }
    items_.clear();
    filtered_.clear();
    selectedItem_ = -1;
    selectedFiltered_ = -1;
    scrollY_ = 0.0f;
    scanDone_ = false;
    scanTotal_ = 0;
    currentModelLoaded_ = false;
    havePreview_ = false;
    playing_ = false;
    currentClip_ = -1;

    if (root.empty()) {
        fatalError_ = L"未找到素材目录。请用「选择文件夹」指定。";
        statusText_ = L"未找到素材目录";
        return;
    }
    fatalError_.clear();
    statusText_ = L"正在扫描…";
    worker_->Start(avu::Utf8(root), root, cacheDir_, hwnd_, (int)(256 * scale_), 2, 0);
}

void ViewerApp::OnWorkerMessage(UINT msg, WPARAM wp, LPARAM lp) {
    (void)lp;
    if (!worker_) return;
    if (msg == kMsgScanProgress) {
        statusText_ = L"正在扫描… 已发现 " + FormatW(wp) + L" 个文件";
        ::InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }
    if (msg == kMsgScanDone) {
        scanDone_ = true;
        scanTotal_ = worker_->Total();
        scanMs_ = worker_->ScanMs();
        ApplyWorkerData();
        return;
    }
    if (msg == kMsgMetaReady) {
        const size_t idx = (size_t)wp;
        EntryMeta meta;
        if (worker_->GetMeta(idx, &meta) && idx < items_.size()) {
            ListItem& it = items_[idx];
            it.parsed = meta.parsed;
            it.ok = meta.ok;
            it.triangles = meta.triangleCount;
            it.vertices = meta.vertexCount;
            it.materials = meta.materialCount;
            it.bones = meta.boneCount;
            it.clips = meta.clipCount;
            it.error = avu::Wide(meta.error);
            it.warning = avu::Wide(meta.warning);
            it.clipNames.clear();
            for (size_t c = 0; c < meta.clipNames.size(); ++c)
                it.clipNames.push_back(avu::Wide(meta.clipNames[c]));
            if ((int)idx == selectedItem_) LoadDetailFor(selectedItem_);
        }
        ::InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }
    if (msg == kMsgThumbReady) {
        const size_t idx = (size_t)wp;
        if (idx >= items_.size()) return;
        ThumbResult tr;
        if (worker_->TakeThumb(idx, &tr) && tr.width > 0 && tr.height > 0 &&
            tr.pixels.size() >= (size_t)tr.width * tr.height) {
            ListItem& it = items_[idx];
            it.thumbPixels.swap(tr.pixels);
            it.thumbW = tr.width;
            it.thumbH = tr.height;
            it.thumbState = worker_->GetThumbState(idx);
            // 创建 D2D 位图
            if (renderTarget_ && !it.thumbBitmap) {
                D2D1_BITMAP_PROPERTIES bp{};
                bp.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
                bp.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
                bp.dpiX = 96.0f * scale_;
                bp.dpiY = 96.0f * scale_;
                D2D1_SIZE_U sz = D2D1::SizeU((UINT32)tr.width, (UINT32)tr.height);
                HRESULT hr = renderTarget_->CreateBitmap(sz, it.thumbPixels.data(),
                                                         (UINT32)tr.width * 4, &bp, &it.thumbBitmap);
                if (FAILED(hr)) it.thumbBitmap = nullptr;
            }
        }
        ::InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }
    if (msg == kMsgAllDone) {
        ::InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }
}

void ViewerApp::ApplyWorkerData() {
    if (!worker_) return;
    const size_t n = worker_->Count();
    items_.clear();
    items_.resize(n);
    for (size_t i = 0; i < n; ++i) {
        ScanEntry e;
        if (!worker_->GetScan(i, &e)) continue;
        ListItem& it = items_[i];
        it.name = avu::Wide(e.name);
        it.category = avu::Wide(e.category);
        it.path = avu::Wide(e.path);
        it.ext = e.isFBX ? L"FBX" : L"GLB";
        it.fileSize = e.fileSize;
        EntryMeta meta;
        if (worker_->GetMeta(i, &meta) && meta.parsed) {
            it.parsed = true;
            it.ok = meta.ok;
            it.triangles = meta.triangleCount;
            it.vertices = meta.vertexCount;
            it.materials = meta.materialCount;
            it.bones = meta.boneCount;
            it.clips = meta.clipCount;
            it.error = avu::Wide(meta.error);
            it.warning = avu::Wide(meta.warning);
            for (size_t c = 0; c < meta.clipNames.size(); ++c)
                it.clipNames.push_back(avu::Wide(meta.clipNames[c]));
        }
        it.thumbState = worker_->GetThumbState(i);
    }
    RebuildFilter();

    // 优先生产当前可见区域的缩略图
    if (layoutValid_ && !filtered_.empty()) {
        int firstRow = (int)(scrollY_ / RowHeight());
        int rowsVisible = (int)(layout_.list.H() / RowHeight()) + 1;
        int first = firstRow * layout_.columns;
        int last = (firstRow + rowsVisible) * layout_.columns + layout_.columns;
        if (last > (int)filtered_.size()) last = (int)filtered_.size();
        if (first < 0) first = 0;
        if (first < last && first < (int)filtered_.size()) worker_->Prioritize((size_t)first, (size_t)(last - 1));
    }

    if (!filtered_.empty() && selectedItem_ < 0) SelectItem(filtered_[0]);
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void ViewerApp::RebuildFilter() {
    const std::wstring needle = ToLowerW(searchText_);
    const int keep = selectedItem_;
    filtered_.clear();
    filtered_.reserve(items_.size());
    for (size_t i = 0; i < items_.size(); ++i) {
        if (!needle.empty()) {
            const std::wstring hay = ToLowerW(items_[i].name + L" " + items_[i].category);
            if (hay.find(needle) == std::wstring::npos) continue;
        }
        filtered_.push_back((int)i);
    }
    selectedFiltered_ = -1;
    for (size_t i = 0; i < filtered_.size(); ++i) {
        if (filtered_[i] == keep) {
            selectedFiltered_ = (int)i;
            break;
        }
    }
    if (selectedFiltered_ < 0 && !filtered_.empty()) {
        selectedItem_ = filtered_[0];
        selectedFiltered_ = 0;
        LoadDetailFor(selectedItem_);
    }
    scrollY_ = 0.0f;
    if (layoutValid_) scrollbarVisible_ = ContentHeight() > layout_.list.H();
}

void ViewerApp::SelectItem(int itemIndex) {
    if (itemIndex < 0 || itemIndex >= (int)items_.size()) return;
    if (selectedItem_ == itemIndex) return;
    selectedItem_ = itemIndex;
    SyncHighlightToSelection();
    // 保证可见：滚动到选中项
    if (selectedFiltered_ >= 0 && layoutValid_) {
        const Rect r = CellRect(selectedFiltered_);
        const float top = layout_.list.t + kPad * scale_;
        const float bottom = layout_.list.b - kPad * scale_;
        if (r.t < top) scrollY_ -= (top - r.t);
        else if (r.b > bottom) scrollY_ += (r.b - bottom);
        ClampScroll();
    }
    LoadDetailFor(itemIndex);
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void ViewerApp::LoadDetailFor(int itemIndex) {
    havePreview_ = false;
    playing_ = false;
    currentClip_ = -1;
    clipTime_ = 0.0;
    currentModelLoaded_ = false;
    if (itemIndex < 0 || itemIndex >= (int)items_.size()) {
        renderer_.ClearModel();
        return;
    }
    if (!renderer_.IsReady()) {
        previewDirty_ = true;
        return;
    }
    currentModel_ = ModelData();
#if AV_HAVE_LOADERS
    const ListItem& it = items_[itemIndex];
    if (av::LoadModel(avu::Utf8(it.path), &currentModel_) && currentModel_.ok) {
        currentModelLoaded_ = true;
        renderer_.SetModel(currentModel_);
        havePreview_ = true;
        if (!currentModel_.clips.empty()) {
            currentClip_ = 0;
            clipBoneCount_ = (int)currentModel_.bones.size();
            worldMats_.assign((size_t)clipBoneCount_ * 16, 0.0f);
            skinMats_.assign((size_t)clipBoneCount_ * 16, 0.0f);
            UpdateSkinAt(0.0);
        } else {
            renderer_.SetSkinMatrices(nullptr);
        }
    } else {
        MakeFallbackModel(&currentModel_);
        renderer_.SetModel(currentModel_);
        renderer_.SetSkinMatrices(nullptr);
        havePreview_ = true;
    }
#else
    MakeFallbackModel(&currentModel_);
    renderer_.SetModel(currentModel_);
    renderer_.SetSkinMatrices(nullptr);
    havePreview_ = true;
#endif
    renderer_.ResetCamera();
    previewDirty_ = true;
}

void ViewerApp::UpdateSkinAt(double t) {
    if (!currentModelLoaded_ || currentClip_ < 0) return;
#if AV_HAVE_LOADERS
    if (clipBoneCount_ <= 0) return;
    if (!av::SampleClipWorld(currentModel_, currentClip_, t, &worldMats_)) return;
    av::BuildSkinMatrices(currentModel_, worldMats_, &skinMats_);
    renderer_.SetSkinMatrices(&skinMats_);
#else
    (void)t;
#endif
    previewDirty_ = true;
}

void ViewerApp::OpenSelectedFile() {
    if (selectedItem_ < 0 || selectedItem_ >= (int)items_.size()) return;
    const std::wstring path = items_[selectedItem_].path;
    HINSTANCE r = ::ShellExecuteW(hwnd_, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    if ((INT_PTR)r <= 32) {
        statusText_ = L"无法打开文件（可能没有关联程序）：" + path;
        ::InvalidateRect(hwnd_, nullptr, FALSE);
    }
}

void ViewerApp::ChooseDirectory() {
    // 用 IFileDialog 选择文件夹（Vista+）
    IFileOpenDialog* dlg = nullptr;
    HRESULT hr = ::CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_PPV_ARGS(&dlg));
    if (FAILED(hr) || !dlg) {
        statusText_ = L"无法打开文件夹选择对话框";
        return;
    }
    DWORD opts = 0;
    dlg->GetOptions(&opts);
    dlg->SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
    if (!rootPath_.empty()) {
        IShellItem* startItem = nullptr;
        if (SUCCEEDED(::SHCreateItemFromParsingName(rootPath_.c_str(), nullptr, IID_PPV_ARGS(&startItem)))) {
            dlg->SetFolder(startItem);
            startItem->Release();
        }
    }
    hr = dlg->Show(hwnd_);
    if (SUCCEEDED(hr)) {
        IShellItem* item = nullptr;
        if (SUCCEEDED(dlg->GetResult(&item))) {
            PWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) {
                std::wstring newRoot(path);
                ::CoTaskMemFree(path);
                StartScan(newRoot);
            }
            item->Release();
        }
    }
    dlg->Release();
}

void ViewerApp::RefreshThumbs() {
    // 让后台按当前可见区优先重排任务
    RefreshPriorities();
    // 丢弃过期的 D2D 位图（像素仍在 items_ 里，重建一次）
    for (size_t i = 0; i < items_.size(); ++i) {
        if (items_[i].thumbBitmap) {
            items_[i].thumbBitmap->Release();
            items_[i].thumbBitmap = nullptr;
        }
    }
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void ViewerApp::RefreshPriorities() {
    if (!worker_ || filtered_.empty() || !layoutValid_) return;
    const float rowH = RowHeight();
    if (rowH < 1) return;
    int firstRow = (int)(scrollY_ / rowH);
    if (firstRow < 0) firstRow = 0;
    int rowsVisible = (int)(layout_.list.H() / rowH) + 2;
    int first = firstRow * layout_.columns;
    int last = (firstRow + rowsVisible) * layout_.columns;
    if (first < 0) first = 0;
    if (last > (int)filtered_.size()) last = (int)filtered_.size();
    if (first < last) worker_->Prioritize((size_t)first, (size_t)(last - 1));
}

void ViewerApp::SelectByOffset(int delta) {
    if (filtered_.empty()) return;
    if (selectedFiltered_ < 0) {
        SelectItem(filtered_[0]);
        return;
    }
    int n = selectedFiltered_ + delta;
    if (n < 0) n = 0;
    if (n >= (int)filtered_.size()) n = (int)filtered_.size() - 1;
    SelectItem(filtered_[n]);
}

void ViewerApp::TogglePlay() {
    if (currentClip_ < 0 || !currentModelLoaded_) {
        statusText_ = L"当前素材没有可播放的动画";
        ::InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }
    playing_ = !playing_;
    if (playing_) {
        lastFrameTick_ = ::GetTickCount64();
        animating_ = true;
        ::SetTimer(hwnd_, 1, 16, nullptr);
    } else {
        animating_ = false;
        ::KillTimer(hwnd_, 1);
    }
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void ViewerApp::OnAnimTick() {
    if (!playing_ || currentClip_ < 0) {
        ::KillTimer(hwnd_, 1);
        animating_ = false;
        return;
    }
    const ULONGLONG now = ::GetTickCount64();
    double dt = (double)(now - lastFrameTick_) / 1000.0;
    lastFrameTick_ = now;
    if (dt < 0.0) dt = 0.0;
    if (dt > 0.25) dt = 0.25;   // 防止卡顿后跳变

    double dur = 1.0;
    if (currentClip_ >= 0 && currentClip_ < (int)currentModel_.clips.size())
        dur = currentModel_.clips[(size_t)currentClip_].duration;
    if (dur <= 1e-6) dur = 1.0;

    clipTime_ += dt;
    if (clipTime_ > dur) {
        if (looping_) {
            clipTime_ = std::fmod(clipTime_, dur);
        } else {
            clipTime_ = dur;
            playing_ = false;
            animating_ = false;
            ::KillTimer(hwnd_, 1);
        }
    }
    UpdateSkinAt(clipTime_);
    previewDirty_ = true;
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void ViewerApp::ScrubTo(float mouseX) {
    if (currentClip_ < 0 || progressRect_.W() <= 1) return;
    double dur = 1.0;
    if (currentClip_ < (int)currentModel_.clips.size())
        dur = currentModel_.clips[(size_t)currentClip_].duration;
    if (dur <= 1e-6) dur = 1.0;
    float ratio = (mouseX - progressRect_.l) / progressRect_.W();
    if (ratio < 0) ratio = 0;
    if (ratio > 1) ratio = 1;
    clipTime_ = ratio * dur;
    UpdateSkinAt(clipTime_);
    previewDirty_ = true;
    ::InvalidateRect(hwnd_, nullptr, FALSE);
}

void ViewerApp::EnsureThumbBitmaps() {
    if (!renderTarget_) return;
    for (size_t i = 0; i < items_.size(); ++i) {
        ListItem& it = items_[i];
        if (it.thumbBitmap || it.thumbPixels.empty() || it.thumbW <= 0 || it.thumbH <= 0) continue;
        D2D1_BITMAP_PROPERTIES bp{};
        bp.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
        bp.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
        bp.dpiX = 96.0f;
        bp.dpiY = 96.0f;
        D2D1_SIZE_U sz = D2D1::SizeU((UINT32)it.thumbW, (UINT32)it.thumbH);
        if (FAILED(renderTarget_->CreateBitmap(sz, it.thumbPixels.data(), (UINT32)it.thumbW * 4,
                                               &bp, &it.thumbBitmap)))
            it.thumbBitmap = nullptr;
    }
}

// ===========================================================================
// 绘制
// ===========================================================================
void ViewerApp::Render() {
    // 一次性诊断（仅在 AV_UI_TRACE 环境变量存在时写文件）
    static int sTrace = -1;
    static FILE* sLog = nullptr;
    if (sTrace < 0) {
        wchar_t env[8] = {};
        sTrace = ::GetEnvironmentVariableW(L"AV_UI_TRACE", env, 8) > 0 ? 1 : 0;
        if (sTrace && !sLog) ::fopen_s(&sLog, "av_ui_trace.log", "w");
    }
    auto TR = [&](const char* what, long long a, long long b) {
        if (!sLog) return;
        ::fprintf(sLog, "%s a=%lld b=%lld\n", what, a, b);
        ::fflush(sLog);
    };

    if (!renderTarget_) {
        if (!CreateD2D()) { TR("CreateD2D_FAILED", 0, 0); return; }
        RECT rc;
        ::GetClientRect(hwnd_, &rc);
        TR("clientRect", rc.right, rc.bottom);
        D2D1_SIZE_U sz = D2D1::SizeU((UINT32)(rc.right > 0 ? rc.right : 1),
                                     (UINT32)(rc.bottom > 0 ? rc.bottom : 1));
        D2D1_RENDER_TARGET_PROPERTIES rtp = D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE), 96.0f * scale_,
            96.0f * scale_, D2D1_RENDER_TARGET_USAGE_NONE, D2D1_FEATURE_LEVEL_DEFAULT);
        D2D1_HWND_RENDER_TARGET_PROPERTIES hrtp = D2D1::HwndRenderTargetProperties(hwnd_, sz);
        HRESULT hr = d2dFactory_->CreateHwndRenderTarget(rtp, hrtp, &hwndTarget_);
        TR("CreateHwndRenderTarget", (long long)hr, (long long)(hwndTarget_ != nullptr));
        if (FAILED(hr) || !hwndTarget_) return;
        renderTarget_ = hwndTarget_;   // 正常绘制走窗口目标
        CreateBrushes();

        // 3D 渲染器（用主窗口做 device 关联，但渲染目标是自己的离屏纹理）
        const float pw = layout_.preview.W();
        const float ph = layout_.preview.H();
        TR("previewSize", (long long)pw, (long long)ph);
        if (!renderer_.IsReady() && pw > 8 && ph > 8) {
            const bool ok = renderer_.Initialize(hwnd_, (int)pw, (int)ph);
            TR("rendererInit", (long long)ok, 0);
            previewDirty_ = true;
        }
    }
    if (!renderTarget_) return;

    // 预览位图必须在 BeginDraw 之前准备好（期间不能操作 D2D 资源）
    UpdatePreviewBitmap();
    EnsureThumbBitmaps();

    renderTarget_->BeginDraw();
    renderTarget_->SetTransform(D2D1::Matrix3x2F::Identity());

    // 背景
    renderTarget_->Clear(kColBg);
    D2D1_RECT_F full = D2D1::RectF(0, 0, layout_.list.r, layout_.status.t);
    renderTarget_->FillRectangle(&full, bgBrush_);

    DrawToolbar();
    DrawList();
    DrawPreview();
    DrawInfoPanel();
    DrawStatusBar();

    const HRESULT hr = renderTarget_->EndDraw();
    TR("EndDraw", (long long)hr, (long long)items_.size());
    if (hr == D2DERR_RECREATE_TARGET) {
        ReleaseGraphicsResources();
        ::InvalidateRect(hwnd_, nullptr, FALSE);
    }
}

// 离屏渲染到 WIC 位图并存 PNG。用于交付自检：不依赖桌面窗口合成，
// 即使程序被遮挡 / 屏幕截图抓不到 D2D 表面，也能证明界面确实画出来了。
bool ViewerApp::SaveScreenshot(const std::wstring& pngPath) {
    if (!d2dFactory_ || !wicFactory_) return false;
    if (!layoutValid_ || clientW_ <= 0 || clientH_ <= 0) return false;

    const UINT32 w = (UINT32)clientW_;
    const UINT32 h = (UINT32)clientH_;

    // 1) 保存并替换画刷，切到离屏 target
    ID2D1SolidColorBrush* oldBg = bgBrush_;
    ID2D1SolidColorBrush* oldPanel = panelBrush_;
    ID2D1SolidColorBrush* oldAccent = accentBrush_;
    ID2D1SolidColorBrush* oldText = textBrush_;
    ID2D1SolidColorBrush* oldSub = subTextBrush_;
    ID2D1SolidColorBrush* oldBorder = borderBrush_;
    ID2D1SolidColorBrush* oldErr = errorBrush_;
    ID2D1SolidColorBrush* oldBtn = buttonBrush_;
    ID2D1SolidColorBrush* oldBtnH = buttonHotBrush_;
    ID2D1SolidColorBrush* oldSel = selectBrush_;
    ID2D1SolidColorBrush* oldProg = progressBrush_;
    ID2D1RenderTarget* oldTarget = renderTarget_;
    auto restore = [&]() {
        bgBrush_ = oldBg; panelBrush_ = oldPanel; accentBrush_ = oldAccent;
        textBrush_ = oldText; subTextBrush_ = oldSub; borderBrush_ = oldBorder;
        errorBrush_ = oldErr; buttonBrush_ = oldBtn; buttonHotBrush_ = oldBtnH;
        selectBrush_ = oldSel; progressBrush_ = oldProg;
        renderTarget_ = oldTarget;
    };

    ComPtr<IWICBitmap> wicBitmap;
    HRESULT hr = wicFactory_->CreateBitmap(w, h, GUID_WICPixelFormat32bppPBGRA,
                                           WICBitmapCacheOnLoad, &wicBitmap);
    if (FAILED(hr)) { restore(); return false; }

    D2D1_RENDER_TARGET_PROPERTIES rtp = D2D1::RenderTargetProperties(
        D2D1_RENDER_TARGET_TYPE_SOFTWARE,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
        96.0f * scale_, 96.0f * scale_);
    ComPtr<ID2D1RenderTarget> offscreen;
    hr = d2dFactory_->CreateWicBitmapRenderTarget(wicBitmap.Get(), rtp, &offscreen);
    if (FAILED(hr) || !offscreen) { restore(); return false; }

    // 在离屏 target 上重建全部画刷
    ComPtr<ID2D1SolidColorBrush> bgB, panelB, accentB, textB, subB, borderB, errB, btnB, btnHB, selB, progB;
    auto mk = [&](ComPtr<ID2D1SolidColorBrush>& out, const D2D1_COLOR_F& c) {
        return SUCCEEDED(offscreen->CreateSolidColorBrush(c, &out));
    };
    const bool ok = mk(bgB, kColBg) && mk(panelB, kColPanel) && mk(accentB, kColAccent) &&
                    mk(textB, kColText) && mk(subB, kColSubText) && mk(borderB, kColBorder) &&
                    mk(errB, kColError) && mk(btnB, kColButton) && mk(btnHB, kColButtonHot) &&
                    mk(selB, kColSelect) && mk(progB, kColAccentDim);
    if (!ok) { restore(); return false; }

    bgBrush_ = bgB.Get(); panelBrush_ = panelB.Get(); accentBrush_ = accentB.Get();
    textBrush_ = textB.Get(); subTextBrush_ = subB.Get(); borderBrush_ = borderB.Get();
    errorBrush_ = errB.Get(); buttonBrush_ = btnB.Get(); buttonHotBrush_ = btnHB.Get();
    selectBrush_ = selB.Get(); progressBrush_ = progB.Get();
    renderTarget_ = offscreen.Get();   // 绘制函数统一走 renderTarget_

    offscreen->BeginDraw();
    offscreen->SetTransform(D2D1::Matrix3x2F::Identity());
    offscreen->Clear(kColBg);
    D2D1_RECT_F full = D2D1::RectF(0, 0, layout_.list.r, layout_.status.t);
    offscreen->FillRectangle(&full, bgBrush_);

    DrawToolbar();
    DrawList();
    DrawPreview();
    DrawInfoPanel();
    DrawStatusBar();
    offscreen->EndDraw();

    restore();   // 恢复画刷与窗口 target（不影响后续正常重绘）
    offscreen.Reset();
    bgB.Reset(); panelB.Reset(); accentB.Reset(); textB.Reset(); subB.Reset(); borderB.Reset();
    errB.Reset(); btnB.Reset(); btnHB.Reset(); selB.Reset(); progB.Reset();

    // 2) PNG 编码
    ComPtr<IWICStream> stream;
    hr = wicFactory_->CreateStream(&stream);
    if (FAILED(hr)) return false;
    hr = stream->InitializeFromFilename(pngPath.c_str(), GENERIC_WRITE);
    if (FAILED(hr)) return false;

    ComPtr<IWICBitmapEncoder> encoder;
    hr = wicFactory_->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (FAILED(hr)) return false;
    hr = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    if (FAILED(hr)) return false;

    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> props;
    hr = encoder->CreateNewFrame(&frame, &props);
    if (FAILED(hr)) return false;
    hr = frame->Initialize(props.Get());
    if (FAILED(hr)) return false;
    hr = frame->SetSize(w, h);
    if (FAILED(hr)) return false;
    WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppPBGRA;
    hr = frame->SetPixelFormat(&fmt);
    if (FAILED(hr)) return false;
    hr = frame->WriteSource(wicBitmap.Get(), nullptr);
    if (FAILED(hr)) return false;
    hr = frame->Commit();
    if (FAILED(hr)) return false;
    hr = encoder->Commit();
    return SUCCEEDED(hr);
}

bool ViewerApp::RunCaptureMode(const std::wstring& pngPath) {
    FILE* log = nullptr;
    ::fopen_s(&log, "av_ui_trace.log", "w");
    auto LOG = [&](const char* s, int a) {
        if (!log) return;
        ::fprintf(log, "%s %d\n", s, a);
        ::fflush(log);
    };
    LOG("enter", 0);

    int sw = ::GetSystemMetrics(SM_CXSCREEN);
    int sh = ::GetSystemMetrics(SM_CYSCREEN);
    if (sw < 1200) sw = 1200;
    if (sh < 800) sh = 800;
    const int ww = (sw * 3) / 5;
    const int wh = (sh * 3) / 4;
    ::MoveWindow(hwnd_, (sw - ww) / 2, (sh - wh) / 2, ww, wh, TRUE);
    LOG("moved", ww);

    const DWORD kDeadline = ::GetTickCount() + 12000;
    int frames = 0;
    while (::GetTickCount() < kDeadline) {
        MSG msg;
        while (::PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) { LOG("quit", frames); if (log) ::fclose(log); return false; }
            ::TranslateMessage(&msg);
            ::DispatchMessageW(&msg);
        }
        Render();
        ++frames;
        ::Sleep(16);
    }
    LOG("pumped", frames);
    const bool ok = SaveScreenshot(pngPath);
    LOG("shot", ok ? 1 : 0);
    if (log) ::fclose(log);
    return ok && frames > 10;
}

void ViewerApp::DrawToolbar() {
    const Rect& tb = layout_.toolbar;
    D2D1_RECT_F r = D2D1::RectF(tb.l, tb.t, tb.r, tb.b);
    renderTarget_->FillRectangle(&r, panelBrush_);
    D2D1_RECT_F line = D2D1::RectF(tb.l, tb.b - 1, tb.r, tb.b);
    renderTarget_->FillRectangle(&line, borderBrush_);

    // 标题
    const std::wstring title = L"BiuBiu 素材快查看器";
    D2D1_RECT_F tr = D2D1::RectF(12 * scale_, (tb.H() - 22 * scale_) * 0.5f, 240 * scale_,
                                 tb.H() * 0.5f + 14 * scale_);
    DrawText2D(renderTarget_, title.c_str(), (UINT32)title.size(), fmtTitle_, &tr, textBrush_);

    // 按钮
    const wchar_t* names[kBtnCount] = {L"选择文件夹", L"重新扫描", L"刷新", L"打开",
                                       L"播放", L"上一个", L"下一个", L"循环"};
    float x = 246 * scale_;
    const float bh = tb.H() - 20 * scale_;
    const float by = 10 * scale_;
    for (int i = 0; i < kBtnCount; ++i) {
        if (i > kBtnRefresh) break; // 工具条只放前三个
        float bw = 96 * scale_;
        buttonRects_[i] = Rect{x, by, x + bw, by + bh};
        D2D1_RECT_F br = D2D1::RectF(x, by, x + bw, by + bh);
        D2D1_ROUNDED_RECT rr = D2D1::RoundedRect(br, 4 * scale_, 4 * scale_);
        renderTarget_->FillRoundedRectangle(&rr, i == hotButton_ ? buttonHotBrush_ : buttonBrush_);
        renderTarget_->DrawRoundedRectangle(&rr, borderBrush_, 1.0f);
        D2D1_RECT_F textR = D2D1::RectF(x, by, x + bw, by + bh);
        DrawText2D(renderTarget_, names[i], (UINT32)wcslen(names[i]), fmtBody_, &textR, textBrush_);
        x += bw + 6 * scale_;
    }

    // 搜索框
    const Rect& sb = layout_.searchBox;
    D2D1_RECT_F sbr = D2D1::RectF(sb.l, sb.t, sb.r, sb.b);
    D2D1_ROUNDED_RECT srr = D2D1::RoundedRect(sbr, 4 * scale_, 4 * scale_);
    renderTarget_->FillRoundedRectangle(&srr, renderTarget_ ? buttonBrush_ : buttonBrush_);
    renderTarget_->DrawRoundedRectangle(&srr, searchFocused_ ? accentBrush_ : borderBrush_,
                                        1.0f + (searchFocused_ ? 1.0f : 0.0f));
    std::wstring shown = searchText_.empty() ? L"搜索名称 / 分类…" : searchText_;
    D2D1_RECT_F str = D2D1::RectF(sb.l + 10 * scale_, sb.t, sb.r - 8 * scale_, sb.b);
    DrawText2D(renderTarget_, shown.c_str(), (UINT32)shown.size(), fmtBody_, &str,
                             searchText_.empty() ? subTextBrush_ : textBrush_);
}

void ViewerApp::DrawPlaceholderThumb(const Rect& r, int state) {
    D2D1_RECT_F rf = D2D1::RectF(r.l, r.t, r.r, r.b);
    renderTarget_->FillRectangle(&rf, panelBrush_);
    // 棋盘格
    const float cell = 12 * scale_;
    for (int gy = 0; gy * cell < r.H(); ++gy) {
        for (int gx = 0; gx * cell < r.W(); ++gx) {
            if (((gx / 2) + (gy / 2)) & 1) continue;
            D2D1_RECT_F c = D2D1::RectF(r.l + gx * cell, r.t + gy * cell,
                                        std::min(r.l + (gx + 1) * cell, r.r),
                                        std::min(r.t + (gy + 1) * cell, r.b));
            renderTarget_->FillRectangle(&c, buttonBrush_);
        }
    }
    std::wstring label = state == kThumbFailed ? L"解析失败" : L"生成中…";
    D2D1_RECT_F tr = D2D1::RectF(r.l, r.t + r.H() * 0.5f - 8 * scale_, r.r, r.t + r.H() * 0.5f + 8 * scale_);
    DrawText2D(renderTarget_, label.c_str(), (UINT32)label.size(), fmtSmall_, &tr,
                             state == kThumbFailed ? errorBrush_ : subTextBrush_);
}

void ViewerApp::DrawList() {
    const Rect& L = layout_.list;
    // 面板底
    D2D1_RECT_F lp = D2D1::RectF(L.l, L.t, L.r, L.b);
    renderTarget_->FillRectangle(&lp, bgBrush_);

    if (filtered_.empty()) {
        std::wstring msg = fatalError_.empty() ? L"没有找到模型文件" : fatalError_;
        D2D1_RECT_F tr = D2D1::RectF(L.l + kPad * scale_, L.t + 40 * scale_, L.r - kPad * scale_,
                                     L.t + 120 * scale_);
        DrawText2D(renderTarget_, msg.c_str(), (UINT32)msg.size(), fmtBody_, &tr,
                                 fatalError_.empty() ? subTextBrush_ : errorBrush_);
        return;
    }

    // 裁剪列表区
    renderTarget_->PushAxisAlignedClip(D2D1::RectF(L.l, L.t, L.r, L.b), D2D1_ANTIALIAS_MODE_ALIASED);

    const int firstRow = (int)(scrollY_ / RowHeight());
    const int lastRow = (int)((scrollY_ + L.H()) / RowHeight()) + 1;
    int firstIdx = firstRow * layout_.columns;
    int lastIdx = (lastRow + 1) * layout_.columns;
    if (firstIdx < 0) firstIdx = 0;
    if (lastIdx > (int)filtered_.size()) lastIdx = (int)filtered_.size();

    for (int i = firstIdx; i < lastIdx; ++i) {
        const int itemIdx = filtered_[i];
        ListItem& it = items_[itemIdx];
        const Rect cr = CellRect(i);
        if (cr.b < L.t || cr.t > L.b) continue;

        const bool isSel = (itemIdx == selectedItem_);
        D2D1_RECT_F card = D2D1::RectF(cr.l, cr.t, cr.r, cr.b);
        D2D1_ROUNDED_RECT rr = D2D1::RoundedRect(card, 6 * scale_, 6 * scale_);
        renderTarget_->FillRoundedRectangle(&rr, isSel ? selectBrush_ : panelBrush_);
        renderTarget_->DrawRoundedRectangle(&rr, isSel ? accentBrush_ : borderBrush_, 1.0f);

        // 缩略图
        const float pad = 6 * scale_;
        Rect tr{cr.l + pad, cr.t + pad, cr.r - pad, cr.t + pad + (cr.r - cr.l - pad * 2)};
        if (it.thumbBitmap) {
            D2D1_RECT_F dst = D2D1::RectF(tr.l, tr.t, tr.r, tr.b);
            renderTarget_->DrawBitmap(it.thumbBitmap, &dst, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            if (it.thumbState == kThumbFailed) {
                D2D1_RECT_F band = D2D1::RectF(tr.l, tr.b - 18 * scale_, tr.r, tr.b);
                renderTarget_->FillRectangle(&band, errorBrush_);
                std::wstring t = L"解析失败";
                D2D1_RECT_F bob = band;
                DrawText2D(renderTarget_, t.c_str(), (UINT32)t.size(), fmtSmall_, &bob, bgBrush_);
            }
        } else {
            DrawPlaceholderThumb(tr, it.thumbState);
        }

        // 文字
        const float textTop = tr.b + 4 * scale_;
        D2D1_RECT_F nameR = D2D1::RectF(cr.l + 8 * scale_, textTop, cr.r - 8 * scale_,
                                        textTop + 16 * scale_);
        DrawText2D(renderTarget_, it.name.c_str(), (UINT32)it.name.size(), fmtCellName_, &nameR,
                                 textBrush_);
        D2D1_RECT_F catR = D2D1::RectF(cr.l + 8 * scale_, textTop + 16 * scale_, cr.r - 8 * scale_,
                                       textTop + 31 * scale_);
        const std::wstring catLine = it.category.empty() ? it.ext : (it.ext + L"  ·  " + it.category);
        DrawText2D(renderTarget_, catLine.c_str(), (UINT32)catLine.size(), fmtCellSub_, &catR,
                                 subTextBrush_);
        D2D1_RECT_F triR = D2D1::RectF(cr.l + 8 * scale_, textTop + 31 * scale_, cr.r - 8 * scale_,
                                       textTop + 46 * scale_);
        std::wstring triLine;
        if (it.triangles > 0) triLine = FormatW(it.triangles) + L" 面";
        else if (it.parsed && !it.ok) triLine = L"解析失败";
        else triLine = FormatSizeW(it.fileSize);
        DrawText2D(renderTarget_, triLine.c_str(), (UINT32)triLine.size(), fmtCellSub_, &triR,
                                 (it.parsed && !it.ok) ? errorBrush_ : subTextBrush_);
    }

    renderTarget_->PopAxisAlignedClip();

    // 滚动条
    const float contentH = ContentHeight();
    const float viewH = L.H();
    if (contentH > viewH + 1) {
        const float trackX = L.r - 8 * scale_;
        const float trackW = 6 * scale_;
        D2D1_RECT_F track = D2D1::RectF(trackX, L.t + 2, trackX + trackW, L.b - 2);
        D2D1_ROUNDED_RECT trk = D2D1::RoundedRect(track, trackW * 0.5f, trackW * 0.5f);
        renderTarget_->FillRoundedRectangle(&trk, panelBrush_);
        const float thumbH = std::max(viewH * viewH / contentH, 30 * scale_);
        const float ratio = scrollY_ / std::max(contentH - viewH, 1.0f);
        const float thumbY = L.t + 2 + ratio * ((L.b - 4) - thumbH);
        D2D1_RECT_F thumb = D2D1::RectF(trackX, thumbY, trackX + trackW, thumbY + thumbH);
        D2D1_ROUNDED_RECT thr = D2D1::RoundedRect(thumb, trackW * 0.5f, trackW * 0.5f);
        renderTarget_->FillRoundedRectangle(&thr, subTextBrush_);
        scrollTrack_ = Rect{trackX, L.t + 2, trackX + trackW, L.b - 2};
        scrollThumb_ = Rect{trackX, thumbY, trackX + trackW, thumbY + thumbH};
        scrollbarVisible_ = true;
    } else {
        scrollbarVisible_ = false;
    }

    // 列表右边界线
    D2D1_RECT_F sep = D2D1::RectF(L.r - 1, L.t, L.r, L.b);
    renderTarget_->FillRectangle(&sep, borderBrush_);
}

void ViewerApp::UpdatePreviewBitmap() {
    if (!renderTarget_ || !renderer_.IsReady()) return;
    const Rect& p = layout_.preview;
    const int w = (int)p.W(), h = (int)p.H();
    if (w < 8 || h < 8) return;

    renderer_.Resize(w, h);
    if (!havePreview_) {
        if (previewBitmap_) {
            previewBitmap_->Release();
            previewBitmap_ = nullptr;
        }
        return;
    }
    if (playing_) {
        // 播放时每帧更新骨骼
        UpdateSkinAt(clipTime_);
    }
    if (!previewDirty_ && previewBitmap_) return;

    // 渲染到离屏纹理，再读出到 D2D 位图（跨 device 简单可靠）
    if (renderer_.RenderFrame()) {
        std::vector<uint32_t> px;
        int rw = 0, rh = 0;
        if (renderer_.Readback(&px, &rw, &rh) && rw > 0 && rh > 0 &&
            px.size() >= (size_t)rw * rh) {
            for (size_t i = 0; i < px.size(); ++i) px[i] |= 0xFF000000u;  // 强制不透明
            D2D1_BITMAP_PROPERTIES bp{};
            bp.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
            bp.pixelFormat.alphaMode = D2D1_ALPHA_MODE_IGNORE;
            bp.dpiX = 96.0f * scale_;
            bp.dpiY = 96.0f * scale_;
            if (previewBitmap_) {
                previewBitmap_->Release();
                previewBitmap_ = nullptr;
            }
            D2D1_SIZE_U sz = D2D1::SizeU((UINT32)rw, (UINT32)rh);
            if (SUCCEEDED(renderTarget_->CreateBitmap(sz, px.data(), (UINT32)rw * 4, &bp,
                                                      &previewBitmap_))) {
                previewBmpW_ = rw;
                previewBmpH_ = rh;
            }
        }
    }
    previewDirty_ = false;
}

void ViewerApp::DrawPreview() {
    const Rect& p = layout_.preview;
    D2D1_RECT_F pr = D2D1::RectF(p.l, p.t, p.r, p.b);
    renderTarget_->FillRectangle(&pr, panelBrush_);

    if (!havePreview_) {
        const wchar_t* msg = L"请选择左侧的素材查看预览";
        std::wstring m(msg);
        D2D1_RECT_F tr = D2D1::RectF(p.l, p.t + p.H() * 0.5f - 10 * scale_, p.r,
                                     p.t + p.H() * 0.5f + 10 * scale_);
        DrawText2D(renderTarget_, m.c_str(), (UINT32)m.size(), fmtBody_, &tr, subTextBrush_);
    } else if (previewBitmap_) {
        D2D1_RECT_F dst = D2D1::RectF(p.l, p.t, p.l + previewBmpW_, p.t + previewBmpH_);
        renderTarget_->DrawBitmap(previewBitmap_, &dst, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
        // 操作提示
        std::wstring hint = L"左键拖拽旋转 · 滚轮缩放 · 双击复位 · Space 播放/暂停";
        D2D1_RECT_F hr = D2D1::RectF(p.l + 12 * scale_, p.b - 22 * scale_, p.r, p.b - 4 * scale_);
        DrawText2D(renderTarget_, hint.c_str(), (UINT32)hint.size(), fmtSmall_, &hr, subTextBrush_);
    } else {
        std::wstring m = L"正在初始化 3D 预览…";
        D2D1_RECT_F tr = D2D1::RectF(p.l, p.t + p.H() * 0.5f - 10 * scale_, p.r,
                                     p.t + p.H() * 0.5f + 10 * scale_);
        DrawText2D(renderTarget_, m.c_str(), (UINT32)m.size(), fmtBody_, &tr, subTextBrush_);
    }

    // 边框
    renderTarget_->DrawRectangle(&pr, borderBrush_, 1.0f);
}

void ViewerApp::DrawInfoPanel() {
    const Rect& p = layout_.info;
    D2D1_RECT_F pr = D2D1::RectF(p.l, p.t, p.r, p.b);
    renderTarget_->FillRectangle(&pr, panelBrush_);
    D2D1_RECT_F top = D2D1::RectF(p.l, p.t, p.r, p.t + 1);
    renderTarget_->FillRectangle(&top, borderBrush_);

    renderTarget_->PushAxisAlignedClip(pr, D2D1_ANTIALIAS_MODE_ALIASED);
    const float padX = 16 * scale_;
    float y = p.t + 10 * scale_;
    const float lineH = 19 * scale_;

    if (selectedItem_ < 0 || selectedItem_ >= (int)items_.size()) {
        std::wstring m = L"未选择素材";
        D2D1_RECT_F r = D2D1::RectF(p.l + padX, y, p.r - padX, y + lineH);
        DrawText2D(renderTarget_, m.c_str(), (UINT32)m.size(), fmtBody_, &r, subTextBrush_);
        renderTarget_->PopAxisAlignedClip();
        return;
    }

    const ListItem& it = items_[selectedItem_];

    // 标题行：文件名
    {
        D2D1_RECT_F r = D2D1::RectF(p.l + padX, y, p.r - padX, y + 24 * scale_);
        DrawText2D(renderTarget_, it.name.c_str(), (UINT32)it.name.size(), fmtTitle_, &r, textBrush_);
        y += 26 * scale_;
    }
    // 失败提示
    if (it.parsed && !it.ok) {
        std::wstring e = L"解析失败：" + it.error;
        D2D1_RECT_F r = D2D1::RectF(p.l + padX, y, p.r - padX, y + lineH);
        DrawText2D(renderTarget_, e.c_str(), (UINT32)e.size(), fmtBody_, &r, errorBrush_);
        y += lineH;
    }

    // 两列布局：左列字段，右列动画
    const float colW = (p.W() - padX * 2) * 0.5f;
    const float leftX = p.l + padX;
    const float rightX = p.l + padX + colW;
    float ly = y;
    float ry = y;

    auto kv = [&](const std::wstring& k, const std::wstring& v, float x, float& cy,
                  ID2D1SolidColorBrush* vb) {
        std::wstring line = k + L"：" + v;
        D2D1_RECT_F r = D2D1::RectF(x, cy, p.r - padX, cy + lineH);
        DrawText2D(renderTarget_, line.c_str(), (UINT32)line.size(), fmtBody_, &r, vb);
        cy += lineH;
    };

    kv(L"分类", it.category.empty() ? L"—" : it.category, leftX, ly, textBrush_);
    kv(L"格式", it.ext, leftX, ly, textBrush_);
    kv(L"大小", FormatSizeW(it.fileSize), leftX, ly, textBrush_);
    if (it.parsed && it.ok) {
        kv(L"三角面", FormatW(it.triangles), leftX, ly, textBrush_);
        kv(L"顶点", FormatW(it.vertices), leftX, ly, textBrush_);
        kv(L"材质", FormatW(it.materials), leftX, ly, textBrush_);
        kv(L"骨骼", FormatW(it.bones), leftX, ly, textBrush_);
        kv(L"动画", FormatW(it.clips), leftX, ly, textBrush_);
    } else if (!it.parsed) {
        kv(L"状态", L"正在分析…", leftX, ly, subTextBrush_);
    }
    kv(L"路径", Ellipsis(it.path, 90), leftX, ly, subTextBrush_);
    if (!it.warning.empty()) kv(L"警告", Ellipsis(it.warning, 70), leftX, ly, subTextBrush_);

    // 右列：动画列表 + 控件
    if (it.clips > 0 && !it.clipNames.empty()) {
        D2D1_RECT_F hdr = D2D1::RectF(rightX, ry, p.r - padX, ry + lineH);
        std::wstring h = L"动画（" + FormatW((uint64_t)it.clipNames.size()) + L"）";
        DrawText2D(renderTarget_, h.c_str(), (UINT32)h.size(), fmtBody_, &hdr, textBrush_);
        ry += lineH;
        const int maxShow = 6;
        for (int i = 0; i < (int)it.clipNames.size() && i < maxShow; ++i) {
            std::wstring line = (i == currentClip_ ? L"▶ " : L"   ") + Ellipsis(it.clipNames[i], 24);
            D2D1_RECT_F r = D2D1::RectF(rightX, ry, p.r - padX, ry + lineH);
            DrawText2D(renderTarget_, line.c_str(), (UINT32)line.size(), fmtBody_, &r,
                                     i == currentClip_ ? accentBrush_ : subTextBrush_);
            ry += lineH;
        }
        if ((int)it.clipNames.size() > maxShow) {
            std::wstring line = L"   … 还有 " + FormatW((uint64_t)it.clipNames.size() - maxShow) + L" 个";
            D2D1_RECT_F r = D2D1::RectF(rightX, ry, p.r - padX, ry + lineH);
            DrawText2D(renderTarget_, line.c_str(), (UINT32)line.size(), fmtSmall_, &r, subTextBrush_);
            ry += lineH;
        }

        // 播放控件行
        const float bh = 26 * scale_;
        float bx = rightX;
        const wchar_t* btnNames[4] = {playing_ ? L"暂停" : L"播放", L"上一个", L"下一个",
                                      looping_ ? L"循环:开" : L"循环:关"};
        const int btnIds[4] = {kBtnPlay, kBtnPrev, kBtnNext, kBtnLoop};
        for (int i = 0; i < 4; ++i) {
            float bw = (i == 0 || i == 3) ? 74 * scale_ : 62 * scale_;
            if (bx + bw > p.r - padX) break;
            buttonRects_[btnIds[i]] = Rect{bx, ry, bx + bw, ry + bh};
            D2D1_RECT_F br = D2D1::RectF(bx, ry, bx + bw, ry + bh);
            D2D1_ROUNDED_RECT rr = D2D1::RoundedRect(br, 4 * scale_, 4 * scale_);
            renderTarget_->FillRoundedRectangle(&rr, hotButton_ == btnIds[i] ? buttonHotBrush_ : buttonBrush_);
            renderTarget_->DrawRoundedRectangle(&rr, borderBrush_, 1.0f);
            D2D1_RECT_F tr = D2D1::RectF(bx, ry, bx + bw, ry + bh);
            DrawText2D(renderTarget_, btnNames[i], (UINT32)wcslen(btnNames[i]), fmtSmall_, &tr, textBrush_);
            bx += bw + 6 * scale_;
        }
        ry += bh + 8 * scale_;

        // 进度条
        const double dur = (currentClip_ >= 0 && currentClip_ < (int)currentModel_.clips.size())
                               ? currentModel_.clips[(size_t)currentClip_].duration
                               : 0.0;
        const float barW = p.r - padX - rightX;
        if (barW > 40 * scale_) {
            D2D1_RECT_F track = D2D1::RectF(rightX, ry + 10 * scale_, rightX + barW, ry + 16 * scale_);
            D2D1_ROUNDED_RECT trk = D2D1::RoundedRect(track, 3 * scale_, 3 * scale_);
            renderTarget_->FillRoundedRectangle(&trk, buttonBrush_);
            float ratio = 0.0f;
            if (dur > 1e-6) {
                ratio = (float)(clipTime_ / dur);
                if (ratio < 0) ratio = 0;
                if (ratio > 1) ratio = 1;
            }
            if (ratio > 0.001f) {
                D2D1_RECT_F fill = D2D1::RectF(rightX, ry + 10 * scale_,
                                               rightX + barW * ratio, ry + 16 * scale_);
                D2D1_ROUNDED_RECT fr = D2D1::RoundedRect(fill, 3 * scale_, 3 * scale_);
                renderTarget_->FillRoundedRectangle(&fr, accentBrush_);
            }
            progressRect_ = Rect{rightX, ry + 6 * scale_, rightX + barW, ry + 20 * scale_};
            std::wstring t = avu::Wide(avu::FormatDuration(clipTime_)) + L" / " +
                             avu::Wide(avu::FormatDuration(dur));
            D2D1_RECT_F r = D2D1::RectF(rightX, ry + 18 * scale_, p.r - padX, ry + 34 * scale_);
            DrawText2D(renderTarget_, t.c_str(), (UINT32)t.size(), fmtSmall_, &r, subTextBrush_);
            ry += 36 * scale_;
        }
        (void)ry;
    } else if (it.parsed && it.ok) {
        D2D1_RECT_F r = D2D1::RectF(rightX, ry, p.r - padX, ry + lineH);
        std::wstring m = L"（无动画）";
        DrawText2D(renderTarget_, m.c_str(), (UINT32)m.size(), fmtBody_, &r, subTextBrush_);
    }

    renderTarget_->PopAxisAlignedClip();
}

void ViewerApp::DrawStatusBar() {
    const Rect& s = layout_.status;
    D2D1_RECT_F r = D2D1::RectF(s.l, s.t, s.r, s.b);
    renderTarget_->FillRectangle(&r, panelBrush_);
    D2D1_RECT_F line = D2D1::RectF(s.l, s.t, s.r, s.t + 1);
    renderTarget_->FillRectangle(&line, borderBrush_);

    std::wstring text;
    if (worker_) {
        const size_t done = worker_->DoneCount();
        const size_t total = worker_->Total();
        text = statusText_;
        if (scanDone_ && total > 0) {
            text = L"共 " + FormatW((uint64_t)filtered_.size());
            if (!searchText_.empty()) text += L" / " + FormatW((uint64_t)total);
            text += L" 个素材";
            text += L"   ·   缩略图 " + FormatW((uint64_t)done) + L"/" + FormatW((uint64_t)total);
            if (worker_->ThumbFailed() > 0)
                text += L"（失败 " + FormatW((uint64_t)worker_->ThumbFailed()) + L"）";
            text += L"   ·   缓存命中 " + FormatW(worker_->CacheHits());
            char buf[64];
            ::sprintf_s(buf, sizeof(buf), "   ·   扫描 %.1f ms", scanMs_);
            text += avu::Wide(std::string(buf));
        }
    }
    if (!rootPath_.empty()) {
        text += L"   ·   " + Ellipsis(rootPath_, 60);
    }
    if (!statusText_.empty() && text.find(statusText_) == std::wstring::npos && !scanDone_)
        text = statusText_ + L"   ·   " + Ellipsis(rootPath_, 60);

    D2D1_RECT_F tr = D2D1::RectF(s.l + 10 * scale_, s.t, s.r - 10 * scale_, s.b);
    DrawText2D(renderTarget_, text.c_str(), (UINT32)text.size(), fmtSmall_, &tr, subTextBrush_);
}

// ===========================================================================
// 命中测试 / 交互
// ===========================================================================
int ViewerApp::HitTestItem(float x, float y) const {
    if (!layout_.list.Contains(x, y)) return -1;
    if (filtered_.empty() || layout_.columns < 1) return -1;
    const float relX = x - (layout_.list.l + kPad * scale_);
    const float relY = y - (layout_.list.t + kPad * scale_) + scrollY_;
    if (relX < 0 || relY < 0) return -1;
    const int col = (int)(relX / (layout_.cellW + kCellGap * scale_));
    const int row = (int)(relY / RowHeight());
    if (col >= layout_.columns) return -1;
    // 落在卡片间隙内也算
    const float inCellX = relX - col * (layout_.cellW + kCellGap * scale_);
    const float inCellY = relY - row * RowHeight();
    if (inCellX > layout_.cellW || inCellY > layout_.cellH) return -1;
    const int idx = row * layout_.columns + col;
    if (idx < 0 || idx >= (int)filtered_.size()) return -1;
    return filtered_[idx];
}

// ===========================================================================
// 窗口过程
// ===========================================================================
LRESULT CALLBACK ViewerApp::WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    ViewerApp* self = nullptr;
    if (msg == WM_NCCREATE) {
        CREATESTRUCTW* cs = (CREATESTRUCTW*)lp;
        self = (ViewerApp*)cs->lpCreateParams;
        ::SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)self);
    } else {
        self = (ViewerApp*)::GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    }
    if (self) return self->HandleMessage(msg, wp, lp);
    return ::DefWindowProcW(hwnd, msg, wp, lp);
}

LRESULT ViewerApp::HandleMessage(UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_PAINT: {
        PAINTSTRUCT ps;
        ::BeginPaint(hwnd_, &ps);
        Render();
        ::EndPaint(hwnd_, &ps);
        return 0;
    }
    case WM_ERASEBKGND:
        return 1;

    case WM_SIZE:
        LayoutChanged();
        if (animating_) ::SetTimer(hwnd_, 1, 16, nullptr);
        ::InvalidateRect(hwnd_, nullptr, FALSE);
        return 0;

    case WM_MOUSEWHEEL: {
        POINT pt = {GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
        ::ScreenToClient(hwnd_, &pt);
        const float f = (float)pt.x, fy = (float)pt.y;
        const float delta = (float)GET_WHEEL_DELTA_WPARAM(wp);
        if (layout_.list.Contains(f, fy)) {
            ScrollBy(-delta / 120.0f * RowHeight() * 0.6f);
            RefreshPriorities();
            ::InvalidateRect(hwnd_, nullptr, FALSE);
        } else if (layout_.preview.Contains(f, fy)) {
            renderer_.Zoom(delta);
            previewDirty_ = true;
            ::InvalidateRect(hwnd_, nullptr, FALSE);
        }
        return 0;
    }

    case WM_LBUTTONDOWN: {
        ::SetFocus(hwnd_);
        const float x = (float)GET_X_LPARAM(lp), y = (float)GET_Y_LPARAM(lp);
        mouseDownLeft_ = true;
        lastMouse_.x = GET_X_LPARAM(lp);
        lastMouse_.y = GET_Y_LPARAM(lp);
        dragStart_ = lastMouse_;

        // 搜索框
        if (layout_.searchBox.Contains(x, y)) {
            searchFocused_ = true;
            ::InvalidateRect(hwnd_, nullptr, FALSE);
            return 0;
        }
        searchFocused_ = false;

        // 工具条按钮
        for (int i = 0; i <= kBtnRefresh; ++i) {
            if (buttonRects_[i].Contains(x, y)) {
                if (i == kBtnChooseDir) ChooseDirectory();
                else if (i == kBtnRescan) StartScan(rootPath_);
                else RefreshThumbs();
                return 0;
            }
        }

        // 预览区：开始拖拽相机
        if (layout_.preview.Contains(x, y) && havePreview_) {
            draggingCamera_ = true;
            return 0;
        }

        // 动画按钮
        if (layout_.info.Contains(x, y)) {
            if (buttonRects_[kBtnPlay].Contains(x, y)) { TogglePlay(); return 0; }
            if (buttonRects_[kBtnPrev].Contains(x, y)) { SelectByOffset(-1); return 0; }
            if (buttonRects_[kBtnNext].Contains(x, y)) { SelectByOffset(1); return 0; }
            if (buttonRects_[kBtnLoop].Contains(x, y)) { looping_ = !looping_; ::InvalidateRect(hwnd_, nullptr, FALSE); return 0; }
            if (progressRect_.W() > 1 && progressRect_.Contains(x, y)) {
                draggingProgress_ = true;
                ScrubTo(x);
                return 0;
            }
        }

        // 列表：滚动条 / 卡片
        if (layout_.list.Contains(x, y)) {
            if (scrollbarVisible_ && scrollThumb_.Contains(x, y)) {
                draggingScroll_ = true;
                scrollGrabY_ = y - scrollThumb_.t;
                return 0;
            }
            if (scrollbarVisible_ && scrollTrack_.Contains(x, y)) {
                // 点击轨道翻页
                ScrollBy(y < scrollThumb_.t ? -layout_.list.H() : layout_.list.H());
                RefreshPriorities();
                ::InvalidateRect(hwnd_, nullptr, FALSE);
                return 0;
            }
            const int idx = HitTestItem(x, y);
            if (idx >= 0) SelectItem(idx);
        }
        ::InvalidateRect(hwnd_, nullptr, FALSE);
        return 0;
    }

    case WM_LBUTTONDBLCLK: {
        const float x = (float)GET_X_LPARAM(lp), y = (float)GET_Y_LPARAM(lp);
        if (layout_.preview.Contains(x, y)) {
            renderer_.ResetCamera();
            previewDirty_ = true;
            ::InvalidateRect(hwnd_, nullptr, FALSE);
            return 0;
        }
        const int idx = HitTestItem(x, y);
        if (idx >= 0) {
            SelectItem(idx);
            OpenSelectedFile();
            return 0;
        }
        return 0;
    }

    case WM_LBUTTONUP: {
        mouseDownLeft_ = false;
        draggingCamera_ = false;
        draggingScroll_ = false;
        draggingProgress_ = false;
        return 0;
    }

    case WM_MOUSEMOVE: {
        const float x = (float)GET_X_LPARAM(lp), y = (float)GET_Y_LPARAM(lp);
        if (draggingCamera_) {
            const float dx = (float)(lastMouse_.x - GET_X_LPARAM(lp));
            const float dy = (float)(lastMouse_.y - GET_Y_LPARAM(lp));
            renderer_.Orbit(dx, dy);
            previewDirty_ = true;
            ::InvalidateRect(hwnd_, nullptr, FALSE);
        }
        if (draggingScroll_ && scrollbarVisible_) {
            const float trackH = scrollTrack_.H() - scrollThumb_.H();
            if (trackH > 1) {
                const float ratio = (y - scrollGrabY_ - scrollTrack_.t) / trackH;
                const float contentH = ContentHeight();
                scrollY_ = ratio * (contentH - layout_.list.H());
                ClampScroll();
                RefreshPriorities();
            }
            ::InvalidateRect(hwnd_, nullptr, FALSE);
        }
        if (draggingProgress_) {
            ScrubTo(x);
        }
        lastMouse_.x = GET_X_LPARAM(lp);
        lastMouse_.y = GET_Y_LPARAM(lp);

        // hover 态
        int hot = -1;
        for (int i = 0; i <= kBtnRefresh; ++i)
            if (buttonRects_[i].Contains(x, y)) hot = i;
        if (layout_.info.Contains(x, y)) {
            if (buttonRects_[kBtnPlay].Contains(x, y)) hot = kBtnPlay;
            if (buttonRects_[kBtnPrev].Contains(x, y)) hot = kBtnPrev;
            if (buttonRects_[kBtnNext].Contains(x, y)) hot = kBtnNext;
            if (buttonRects_[kBtnLoop].Contains(x, y)) hot = kBtnLoop;
        }
        if (hot != hotButton_) {
            hotButton_ = hot;
            ::InvalidateRect(hwnd_, nullptr, FALSE);
        }
        // 鼠标指针
        LPCWSTR cursor = MAKEINTRESOURCEW(32512);                    // IDC_ARROW
        if (layout_.preview.Contains(x, y) && havePreview_) cursor = MAKEINTRESOURCEW(32644); // IDC_SIZEALL
        ::SetCursor(::LoadCursorW(nullptr, cursor));
        return 0;
    }

    case WM_KEYDOWN: {
        // 搜索框获得焦点时：回车确认、退格删除，其余交给默认处理
        if (searchFocused_) {
            if (wp == VK_BACK) {
                if (!searchText_.empty()) {
                    searchText_.pop_back();
                    RebuildFilter();
                    ::InvalidateRect(hwnd_, nullptr, FALSE);
                }
                return 0;
            }
            if (wp == VK_RETURN || wp == VK_ESCAPE) {
                searchFocused_ = false;
                ::InvalidateRect(hwnd_, nullptr, FALSE);
                return 0;
            }
            // 其它键留给 WM_CHAR 处理
            return 0;
        }
        switch (wp) {
        case VK_UP:    SelectByOffset(-layout_.columns); return 0;
        case VK_DOWN:  SelectByOffset(layout_.columns); return 0;
        case VK_LEFT:  SelectByOffset(-1); return 0;
        case VK_RIGHT: SelectByOffset(1); return 0;
        case VK_RETURN: OpenSelectedFile(); return 0;
        case VK_SPACE:  TogglePlay(); return 0;
        case VK_F5:     StartScan(rootPath_); return 0;
        case 'R':       renderer_.ResetCamera(); previewDirty_ = true; ::InvalidateRect(hwnd_, nullptr, FALSE); return 0;
        case VK_ESCAPE: ::DestroyWindow(hwnd_); return 0;
        default: break;
        }
        return 0;
    }

    case WM_CHAR: {
        if (wp == VK_RETURN) return 0;
        if (wp >= 32 && wp != 127) {
            wchar_t ch = (wchar_t)wp;
            searchText_ += ch;
            RebuildFilter();
            ::InvalidateRect(hwnd_, nullptr, FALSE);
            return 0;
        }
        return 0;
    }

    case WM_TIMER:
        if (wp == 1) OnAnimTick();
        return 0;

    case WM_GETMINMAXINFO: {
        MINMAXINFO* mmi = (MINMAXINFO*)lp;
        mmi->ptMinTrackSize.x = (LONG)(900 * scale_);
        mmi->ptMinTrackSize.y = (LONG)(600 * scale_);
        return 0;
    }

    case WM_DESTROY:
        ::KillTimer(hwnd_, 1);
        ::PostQuitMessage(0);
        return 0;

    default:
        if (msg >= WM_APP + 1 && msg <= WM_APP + 5) {
            OnWorkerMessage(msg, wp, lp);
            return 0;
        }
        break;
    }
    return ::DefWindowProcW(hwnd_, msg, wp, lp);
}

int ViewerApp::Run() {
    MSG msg;
    while (running_ && ::GetMessageW(&msg, nullptr, 0, 0)) {
        ::TranslateMessage(&msg);
        ::DispatchMessageW(&msg);
    }
    if (worker_) worker_->Stop();
    Shutdown();
    return 0;
}

void ViewerApp::Shutdown() {
    if (worker_) worker_->Stop();
    ReleaseGraphicsResources();
    renderer_.Shutdown();
    if (fmtTitle_) { fmtTitle_->Release(); fmtTitle_ = nullptr; }
    if (fmtBody_) { fmtBody_->Release(); fmtBody_ = nullptr; }
    if (fmtSmall_) { fmtSmall_->Release(); fmtSmall_ = nullptr; }
    if (fmtCellName_) { fmtCellName_->Release(); fmtCellName_ = nullptr; }
    if (fmtCellSub_) { fmtCellSub_->Release(); fmtCellSub_ = nullptr; }
    if (dwriteFactory_) { dwriteFactory_->Release(); dwriteFactory_ = nullptr; }
    if (d2dFactory_) { d2dFactory_->Release(); d2dFactory_ = nullptr; }
    if (hwnd_ && ::IsWindow(hwnd_)) ::DestroyWindow(hwnd_);
    hwnd_ = nullptr;
}

} // namespace av
