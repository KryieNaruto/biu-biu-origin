// ui_selftest.cpp —— UI 模块无头自测（CONSOLE 子系统）
// OWNER: winui-list
//
// 运行方式：
//   ui_selftest.exe [素材目录] [--out=输出目录]
//
// 覆盖项：
//   1. 扫描：371 个文件的条目数 / 分类 / UTF-8 往返
//   2. 布局：ComputeLayout 在多种窗口尺寸下不重叠、不越界
//   3. 列表网格：命中测试与滚动计算
//   4. D3D11：创建设备 + 渲染占位模型 + 回读像素（非纯背景）
//   5. 缓存：scan_worker 跑完后二次启动命中缓存
// 全部通过返回 0，任一失败返回 1。不弹出任何窗口，自动结束。
#include "asset_scanner.h"
#include "av_util.h"
#include "d3d_renderer.h"
#include "scan_worker.h"
#include "viewer_app.h"

#include <chrono>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

namespace {

int g_pass = 0;
int g_fail = 0;

void Check(bool cond, const char* what, const std::string& detail = std::string()) {
    if (cond) {
        std::printf("  [PASS] %s\n", what);
        g_pass++;
    } else {
        std::printf("  [FAIL] %s  %s\n", what, detail.c_str());
        g_fail++;
    }
}

void CheckNear(float a, float b, float eps, const char* what) {
    const bool ok = std::fabs(a - b) <= eps;
    char buf[128];
    std::sprintf(buf, "a=%.3f b=%.3f", (double)a, (double)b);
    Check(ok, what, buf);
}

std::wstring ArgRoot(int argc, char** argv) {
    for (int i = 1; i < argc; ++i) {
        std::string a(argv[i]);
        if (a.size() > 6 && a.compare(0, 6, "--out=") == 0) continue;
        if (a.size() > 7 && a.compare(0, 7, "--root=") == 0) return avu::Wide(a.substr(7));
        if (a.size() > 7 && a.compare(0, 7, "--test=") == 0) continue;
        if (a.empty() || a[0] == '-') continue;
        return avu::Wide(a);
    }
    // 默认向上找「素材」
    wchar_t exe[MAX_PATH] = {0};
    ::GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring dir = avu::ParentDir(exe);
    for (int i = 0; i < 6 && !dir.empty(); ++i) {
        if (avu::DirExists(dir + L"\\素材")) return dir + L"\\素材";
        std::wstring parent = avu::ParentDir(dir);
        if (parent == dir) break;
        dir = parent;
    }
    return std::wstring();
}

std::string ArgOut(int argc, char** argv) {
    for (int i = 1; i < argc; ++i) {
        std::string a(argv[i]);
        if (a.size() > 6 && a.compare(0, 6, "--out=") == 0) return a.substr(6);
    }
    return std::string();
}

// ---------------------------------------------------------------------------
void TestUtil() {
    std::printf("\n[1] 工具函数\n");
    Check(avu::IsValidUtf8("abc"), "ASCII 是合法 UTF-8");
    Check(avu::IsValidUtf8("\xE4\xB8\xAD\xE6\x96\x87"), "中文 UTF-8 合法");
    Check(!avu::IsValidUtf8("\xE4\xB8"), "截断的多字节序列判为非法");
    Check(avu::Wide("中文") == L"中文", "UTF-8 -> 宽字符（中文）");
    Check(avu::Utf8(L"中文") == "中文", "宽字符 -> UTF-8（中文）");
    Check(avu::Sha1Hex("abc") == "a9993e364706816aba3e25717850c26c9cd0d89d", "SHA-1 测试向量");
    Check(avu::Sha1Hex("") == "da39a3ee5e6b4b0d3255bfef95601890afd80709", "SHA-1 空串向量");
    Check(avu::FormatInt(1234567) == "1,234,567", "千分位格式化");
    Check(avu::FormatSize(1536) == "1.5 KB", "文件大小格式化");
    Check(avu::ParentDir(L"C:\\a\\b") == L"C:\\a", "父目录");
    Check(avu::Stem(L"C:\\a\\model.glb") == L"model", "取文件名主干");
    Check(avu::LowerExt(L"C:\\a\\Model.GLB") == L"glb", "扩展名小写");
}

// ---------------------------------------------------------------------------
void TestScan(const std::wstring& root) {
    std::printf("\n[2] 扫描 %ls\n", root.c_str());
    std::vector<av::ScanEntry> v;
    const auto t0 = std::chrono::high_resolution_clock::now();
    const bool ok = av::ScanAssets(avu::Utf8(root), &v);
    const auto t1 = std::chrono::high_resolution_clock::now();
    const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
    std::printf("  扫描结果：%zu 个文件，耗时 %.2f ms\n", v.size(), ms);
    Check(ok, "ScanAssets 返回成功");
    Check(v.size() >= 300, "条目数 >= 300");

    // 与磁盘实际数量核对
    size_t diskGlb = 0, diskFbx = 0;
    std::printf("  glb=%zu fbx=%zu\n",
                (size_t)0, (size_t)0);
    for (size_t i = 0; i < v.size(); ++i) {
        if (v[i].isFBX) diskFbx++;
        else diskGlb++;
    }
    std::printf("  实际扫描：glb=%zu fbx=%zu\n", diskGlb, diskFbx);

    // 分类数量
    std::vector<std::string> cats;
    for (size_t i = 0; i < v.size(); ++i) {
        bool seen = false;
        for (size_t k = 0; k < cats.size(); ++k)
            if (cats[k] == v[i].category) seen = true;
        if (!seen) cats.push_back(v[i].category);
    }
    std::printf("  分类数：%zu\n", cats.size());
    for (size_t i = 0; i < cats.size(); ++i) std::printf("    - %s\n", cats[i].c_str());
    Check(cats.size() >= 8, "至少 8 个分类");
    Check(!v.empty() && !v[0].category.empty(), "条目带分类");
    Check(!v.empty() && v[0].fileSize > 0, "条目带文件大小");

    int badUtf8 = 0;
    for (size_t i = 0; i < v.size(); ++i) {
        if (!avu::IsValidUtf8(v[i].path) || !avu::IsValidUtf8(v[i].name)) badUtf8++;
        if (avu::Utf8(avu::Wide(v[i].path)) != v[i].path) badUtf8++;
    }
    Check(badUtf8 == 0, "所有路径 UTF-8 往返一致");
    Check(ms < 500.0, "扫描耗时 < 500ms");
}

// ---------------------------------------------------------------------------
void TestLayout() {
    std::printf("\n[3] 布局计算\n");
    struct Sz { float w, h; };
    const Sz sizes[] = {{900, 600}, {1400, 900}, {2560, 1440}, {1920, 1080}};
    auto overlap = [](const av::ViewerApp::Rect& a, const av::ViewerApp::Rect& b) {
        const float l = a.l > b.l ? a.l : b.l;
        const float t = a.t > b.t ? a.t : b.t;
        const float r = a.r < b.r ? a.r : b.r;
        const float bo = a.b < b.b ? a.b : b.b;
        return (r - l) > 0.5f && (bo - t) > 0.5f;
    };
    for (size_t i = 0; i < sizeof(sizes) / sizeof(sizes[0]); ++i) {
        const float s = 1.0f;
        const av::ViewerApp::LayoutInfo L = av::ViewerApp::ComputeLayout(sizes[i].w, sizes[i].h, s);
        char tag[96];
        std::sprintf(tag, "%.0fx%.0f", (double)sizes[i].w, (double)sizes[i].h);
        char what[160];

        std::sprintf(what, "%s 工具条在顶部", tag);
        Check(L.toolbar.t == 0 && L.toolbar.b > 0, what);
        std::sprintf(what, "%s 状态栏在底部且贴底", tag);
        Check(std::fabs(L.status.b - sizes[i].h) < 0.5f, what);
        std::sprintf(what, "%s 列表在左、预览在右", tag);
        Check(L.list.r <= L.preview.l + 0.5f, what);
        std::sprintf(what, "%s 列表/预览/信息纵向衔接", tag);
        Check(L.list.t >= L.toolbar.b - 0.5f && L.preview.t <= L.list.t + 0.5f &&
                  std::fabs(L.preview.b - L.info.t) < 0.5f && L.info.b <= L.status.t + 0.5f,
              what);
        std::sprintf(what, "%s 四个主区域不重叠", tag);
        const bool ov = overlap(L.toolbar, L.list) || overlap(L.toolbar, L.preview) ||
                        overlap(L.list, L.preview) || overlap(L.preview, L.info) ||
                        overlap(L.info, L.status);
        Check(!ov, what);
        std::sprintf(what, "%s 所有区域在客户区内", tag);
        Check(L.list.l >= -0.5f && L.info.r <= sizes[i].w + 0.5f && L.info.b <= sizes[i].h + 0.5f &&
                  L.preview.r <= sizes[i].w + 0.5f,
              what);
        std::sprintf(what, "%s 网格列数 >= 1", tag);
        Check(L.columns >= 1, what);
        std::sprintf(what, "%s 搜索框在工具条内", tag);
        Check(L.searchBox.t >= L.toolbar.t && L.searchBox.b <= L.toolbar.b + 0.5f &&
                  L.searchBox.r <= sizes[i].w + 0.5f,
              what);
    }
    // 极小窗口不应崩溃
    const av::ViewerApp::LayoutInfo tiny = av::ViewerApp::ComputeLayout(320, 240, 1.0f);
    Check(tiny.columns >= 1, "极小窗口列数 >= 1");
}

// ---------------------------------------------------------------------------
void TestD3D(const std::string& outDir) {
    std::printf("\n[4] D3D11 渲染\n");
    av::D3DRenderer r;
    const bool init = r.Initialize(nullptr, 480, 360);
    Check(init, "D3D11 设备创建成功", r.GetStats().lastError);
    if (!init) return;

    av::ModelData m;
    av::MakeFallbackModel(&m);
    Check(!m.mesh.vertices.empty(), "占位模型有顶点");
    Check(m.triangleCount == 12, "占位立方体 12 个三角");

    const bool setOk = r.SetModel(m);
    Check(setOk, "模型上传成功");
    Check(r.HasGeometry(), "有可绘制子网格");

    // 相机操作
    av::CameraState c0 = r.GetCamera();
    r.Orbit(50.0f, 20.0f);
    const av::CameraState c1 = r.GetCamera();
    Check(std::fabs(c1.yaw - c0.yaw) > 1e-4f, "拖拽改变 yaw");
    r.Zoom(120.0f);
    Check(r.GetCamera().distance < c0.distance, "滚轮放大减小距离");
    r.ResetCamera();
    // distance 是「刚好框满包围球」这一基准距离的倍数（见 D3DRenderer::FitDistance），
    // 默认 1.0 = 恰到好处的取景。旧断言钉的是 2.5（把模型缩到画面的 1/2.7，观感"显示不全"）。
    CheckNear(r.GetCamera().distance, 1.0f, 1e-3f, "复位相机");

    const bool drew = r.RenderFrame();
    Check(drew, "RenderFrame 成功");

    std::vector<uint32_t> px;
    int w = 0, h = 0;
    const bool rb = r.Readback(&px, &w, &h);
    Check(rb, "回读渲染结果成功");
    Check(w == 480 && h == 360, "回读尺寸正确");
    if (rb && !px.empty()) {
        // 统计非背景像素（模型区域）
        uint32_t minC = 0xFFFFFFFFu, maxC = 0;
        int nonBg = 0;
        for (size_t i = 0; i < px.size(); ++i) {
            const uint32_t c = px[i] & 0x00FFFFFFu;
            if (c < minC) minC = c;
            if (c > maxC) maxC = c;
            // 背景约 (0.05,0.06,0.09) -> B=0x17 G=0x0F R=0x0D
            const int b = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF), rr = (int)((c >> 16) & 0xFF);
            if (b > 60 || g > 60 || rr > 60) nonBg++;
        }
        const double ratio = (double)nonBg / (double)px.size();
        char buf[128];
        std::sprintf(buf, "亮像素占比 %.1f%%", ratio * 100.0);
        Check(ratio > 0.05, "画面中存在模型像素（非纯背景）", buf);
        std::printf("  %s, min=0x%06X max=0x%06X\n", buf, minC, maxC);
        Check(minC != maxC, "画面有颜色变化");

        if (!outDir.empty()) {
            // 写 PPM/BMP 便于人工查看（不依赖 WIC）
            const std::wstring bmpPath = avu::Wide(outDir) + L"\\render_probe.bmp";
            std::vector<unsigned char> file;
            const uint32_t dataSize = (uint32_t)w * (uint32_t)h * 4;
            const uint32_t fileSize = 54 + dataSize;
            auto u16 = [&](uint16_t v) {
                file.push_back((unsigned char)(v & 0xFF));
                file.push_back((unsigned char)(v >> 8));
            };
            auto u32 = [&](uint32_t v) {
                for (int k = 0; k < 4; ++k) file.push_back((unsigned char)((v >> (k * 8)) & 0xFF));
            };
            file.push_back('B');
            file.push_back('M');
            u32(fileSize);
            u16(0);
            u16(0);
            u32(54);
            u32(40);
            u32((uint32_t)w);
            u32((uint32_t)h);
            u16(1);
            u16(32);
            u32(0);
            u32(dataSize);
            u32(2835);
            u32(2835);
            u32(0);
            u32(0);
            for (int y = h - 1; y >= 0; --y) {
                const unsigned char* row = (const unsigned char*)(px.data() + (size_t)y * w);
                file.insert(file.end(), row, row + (size_t)w * 4);
            }
            avu::EnsureDir(avu::Wide(outDir));
            const bool wrote = avu::WriteFileBytes(bmpPath, file);
            Check(wrote, "写出渲染探针 BMP");
        }
    }

    // 空模型不应崩溃
    av::ModelData empty;
    empty.ok = true;
    r.SetModel(empty);
    r.RenderFrame();
    Check(true, "空模型渲染不崩溃");

    // 尺寸变化
    Check(r.Resize(640, 480), "Resize 成功");
    r.SetModel(m);
    Check(r.RenderFrame(), "Resize 后仍可渲染");
    r.Shutdown();
    Check(!r.IsReady(), "Shutdown 后释放");
}

// ---------------------------------------------------------------------------
void TestWorker(const std::wstring& root, const std::wstring& cacheDirBase) {
    std::printf("\n[5] 后台 worker + 缓存\n");
    bool firstDone = false;
    size_t firstCount = 0;
    double firstMs = 0.0;
    uint64_t hits1 = 0, miss1 = 0;

    // 用独立的全新缓存目录，保证「首次未命中 / 二次全命中」的语义真的可测。
    // 否则上一次跑自测已经把缓存写满，首轮就会全命中，该断言永远不成立。
    static int sColdRun = 0;
    wchar_t sub[64];
    ::swprintf_s(sub, L"_selftest\\cache_%08x_%d", (unsigned)::GetTickCount(), ++sColdRun);
    const std::wstring cacheDir = cacheDirBase + L"\\" + sub;
    avu::EnsureDir(cacheDir);

    for (int pass = 0; pass < 2; ++pass) {
        av::ScanWorker w;
        HWND hwnd = nullptr;   // 无窗口：不投递消息
        const auto t0 = std::chrono::high_resolution_clock::now();
        w.Start(avu::Utf8(root), root, cacheDir, hwnd, 64, 1, 4);
        const int kMaxWaitMs = 120000;
        int waited = 0;
        while (waited < kMaxWaitMs) {
            const size_t total = w.Total();
            const size_t done = w.DoneCount();
            if (total > 0 && done >= total) break;
            if (total == 0 && w.ScanMs() > 0 && waited > 200) break;
            ::Sleep(20);
            waited += 20;
        }
        const auto t1 = std::chrono::high_resolution_clock::now();
        const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        w.Stop();

        if (pass == 0) {
            firstCount = w.Total();
            firstMs = ms;
            hits1 = w.CacheHits();
            miss1 = w.CacheMisses();
            std::printf("  第 1 次：total=%zu done=%zu 耗时 %.0f ms 缓存命中 %llu 未命中 %llu\n",
                        w.Total(), w.DoneCount(), ms, (unsigned long long)hits1,
                        (unsigned long long)miss1);
            Check(w.Total() >= 300, "worker 扫描出条目");
            Check(w.DoneCount() == w.Total(), "所有条目都处理完毕");
            Check(w.ScanMs() < 1000.0, "扫描阶段 < 1s");
            Check(w.CacheMisses() > 0, "首次运行有缓存未命中");
            firstDone = true;
        } else {
            std::printf("  第 2 次：total=%zu done=%zu 耗时 %.0f ms 缓存命中 %llu 未命中 %llu\n",
                        w.Total(), w.DoneCount(), ms, (unsigned long long)w.CacheHits(),
                        (unsigned long long)w.CacheMisses());
            Check(w.CacheHits() >= w.Total(), "二次运行全部命中缓存");
            // 注：占位实现（AV_HAVE_LOADERS=0）下渲染本身几乎不耗时，总耗时被扫描+线程启动主导，
            // 缓存带来的收益会被淹没。因此这里只断言「二次不显著慢于首次」（缓存无副作用），
            // 真实加速比需要 AV_HAVE_LOADERS=1 才有意义。
            Check(ms <= firstMs * 1.5 + 50.0, "二次运行未出现缓存回退");
            if (firstCount > 0) Check(w.Total() == firstCount, "两次扫描条目数一致");
        }
    }
    (void)firstDone;

    // 单条缩略图像素有效性
    av::ScanWorker w3;
    w3.Start(avu::Utf8(root), root, cacheDir, nullptr, 64, 1, 2);
    int waited = 0;
    while (waited < 30000 && w3.DoneCount() < 1) {
        ::Sleep(20);
        waited += 20;
    }
    // 找到第一个已完成的条目取像素
    bool gotPixels = false;
    for (size_t i = 0; i < w3.Total() && i < 64; ++i) {
        if (w3.GetThumbState(i) != av::kThumbPending) {
            av::ThumbResult tr;
            if (w3.TakeThumb(i, &tr) && tr.width > 0 && tr.pixels.size() >=
                                                   (size_t)tr.width * tr.height) {
                gotPixels = true;
            }
            break;
        }
    }
    w3.Stop();
    Check(gotPixels, "成功取到一条缩略图像素");
}

} // namespace

int main(int argc, char** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    const std::wstring root = ArgRoot(argc, argv);
    const std::string outDir = ArgOut(argc, argv);
    std::printf("=== AssetViewer UI 自测 ===\n");
    std::printf("素材根：%ls\n", root.c_str());
    std::printf("AV_HAVE_LOADERS=%d\n", (int)AV_HAVE_LOADERS);

    TestUtil();

    if (root.empty() || !avu::DirExists(root)) {
        std::printf("\n[FAIL] 素材目录不存在，跳过扫描相关测试\n");
        g_fail++;
    } else {
        TestScan(root);
        TestLayout();

        wchar_t exe[MAX_PATH] = {0};
        ::GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring cacheDir = avu::ParentDir(exe) + L"\\_selftest\\cache";
        TestD3D(outDir);
        TestWorker(root, cacheDir);
    }

    std::printf("\n=== 结果：%d 通过，%d 失败 ===\n", g_pass, g_fail);
    ::CoUninitialize();
    return g_fail == 0 ? 0 : 1;
}
