// thumb_sheet.cpp —— Lead 验收工具：批量解析 + 批量出缩略图 + 拼版成 PNG 供目检
// 用法:
//   thumb_sheet.exe <素材根目录> <输出目录> [每格像素=128] [--limit=N] [--only=关键字] [--threads=N]
// 输出:
//   <out>/sheet_XX.png   拼版（每版 8 列 x 6 行）
//   <out>/index.txt      每个文件的统计
// 依赖：av::LoadModel / av::RenderThumbnail / av::img::SavePngFromBgra（wic_util.h）
#include "model_data.h"
#include "thumb_renderer.h"
#include "wic_util.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <algorithm>
#include <chrono>
#include <filesystem>
#include <atomic>
#include <thread>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shellapi.h>   // CommandLineToArgvW
#endif

namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;

// Win32 控制台程序拿到的是 ANSI(CP_ACP) 的 argv（Windows 没有 UTF-8 argv 语义）。
// 本机代码页 936(GBK)，"素材" 在 UTF-8/GBK 下字节不同，直接用会打不开中文目录。
// 这里统一把 argv 按 ACP 解码再用 GetCommandLineW 重新解析为 UTF-8。
static std::vector<std::string> utf8Args(int argc, char** argv) {
    std::vector<std::string> out;
#ifdef _WIN32
    (void)argc; (void)argv;
    int wc = 0;
    LPWSTR* wargs = ::CommandLineToArgvW(::GetCommandLineW(), &wc);
    for (int i = 0; i < wc; ++i) {
        int n = ::WideCharToMultiByte(CP_UTF8, 0, wargs[i], -1, nullptr, 0, nullptr, nullptr);
        std::string s(n > 0 ? (size_t)(n - 1) : 0, '\0');
        if (n > 1) ::WideCharToMultiByte(CP_UTF8, 0, wargs[i], -1, &s[0], n, nullptr, nullptr);
        out.push_back(s);
    }
    if (wargs) ::LocalFree(wargs);
    if (out.empty()) { for (int i = 0; i < argc; ++i) out.push_back(argv[i]); }
#else
    for (int i = 0; i < argc; ++i) out.push_back(argv[i]);
#endif
    return out;
}

static double msSince(Clock::time_point t0) {
    return std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
}

static std::string utf8Of(const fs::path& p) {
    auto u8 = p.u8string();
    return std::string(u8.begin(), u8.end());
}

static bool lowerEndsWith(std::string s, const char* suf) {
    size_t n = std::strlen(suf);
    if (s.size() < n) return false;
    for (size_t i = 0; i < n; ++i) {
        char a = s[s.size() - n + i];
        if (a >= 'A' && a <= 'Z') a = (char)(a - 'A' + 'a');
        if (a != suf[i]) return false;
    }
    return true;
}

struct Result {
    std::string path;
    bool  loaded = false, ok = false, rendered = false;
    uint32_t tris = 0, verts = 0, mats = 0, bones = 0, clips = 0;
    double parseMs = 0, thumbMs = 0;
    std::string error;
    std::vector<uint32_t> px;
};

static void blit(std::vector<uint32_t>& sheet, int sheetW, int sheetH,
                 const std::vector<uint32_t>& cell, int cellW, int cellH,
                 int cx, int cy, uint32_t border) {
    int dw = std::min(cellW, sheetW - cx);
    int dh = std::min(cellH, sheetH - cy);
    for (int y = 0; y < dh; ++y)
        for (int x = 0; x < dw; ++x)
            sheet[(size_t)(cy + y) * sheetW + (cx + x)] = cell[(size_t)y * cellW + x];
    for (int x = 0; x < dw; ++x) sheet[(size_t)(cy + dh - 1) * sheetW + (cx + x)] = border;
    for (int y = 0; y < dh; ++y) sheet[(size_t)(cy + y) * sheetW + (cx + dw - 1)] = border;
}

int main(int argc, char** argv) {
    if (argc < 3) {
        std::printf("usage: thumb_sheet <assetRoot> <outDir> [cellSize=128] [--limit=N] [--only=kw] [--threads=N]\n");
        return 1;
    }
    std::vector<std::string> args = utf8Args(argc, argv);
    std::string root = args[1], outDir = args[2];
    int cellSize = 128;
    size_t limit = (size_t)-1;
    std::string only;
    int threads = (int)std::max(1u, std::thread::hardware_concurrency());
    for (int i = 3; i < (int)args.size(); ++i) {
        std::string a = args[i];
        if (a.rfind("--limit=", 0) == 0) limit = (size_t)std::stoul(a.substr(8));
        else if (a.rfind("--only=", 0) == 0) only = a.substr(7);
        else if (a.rfind("--threads=", 0) == 0) threads = std::max(1, std::atoi(a.substr(10).c_str()));
        else cellSize = std::max(48, std::atoi(a.c_str()));
    }

    // ---- 收集文件 ----
    std::vector<std::string> files;
    {
        std::error_code ec;
        if (!fs::exists(fs::u8path(root), ec)) {
            std::printf("root not found: %s\n", root.c_str());
            return 2;
        }
        for (auto it = fs::recursive_directory_iterator(fs::u8path(root), ec);
             it != fs::recursive_directory_iterator(); it.increment(ec)) {
            if (ec) break;
            const auto& p = it->path();
            if (!it->is_regular_file(ec)) continue;
            std::string s = utf8Of(p);
            if (s.find("/.godot/") != std::string::npos) continue;
            std::string low = s;
            if (lowerEndsWith(low, ".glb") || lowerEndsWith(low, ".fbx")) {
                if (!only.empty() && s.find(only) == std::string::npos) continue;
                files.push_back(s);
            }
        }
    }
    std::sort(files.begin(), files.end());
    if (files.size() > limit) files.resize(limit);
    std::printf("files=%zu cell=%d threads=%d\n", files.size(), cellSize, threads);

    std::error_code ec;
    fs::create_directories(fs::u8path(outDir), ec);

    std::vector<Result> results(files.size());
    std::atomic<size_t> next{0};

    auto worker = [&](int) {
        for (;;) {
            size_t i = next.fetch_add(1);
            if (i >= files.size()) break;
            Result& r = results[i];
            r.path = files[i];
            auto t0 = Clock::now();
            av::ModelData m;
            r.loaded = av::LoadModel(files[i], &m);
            r.parseMs = msSince(t0);
            r.ok = r.loaded && m.ok;
            r.tris = m.triangleCount; r.verts = m.vertexCount; r.mats = m.materialCount;
            r.bones = m.boneCount; r.clips = m.clipCount;
            r.error = m.error;
            auto t1 = Clock::now();
            av::ThumbOptions opt;
            opt.size = cellSize;
            opt.samplesPerAxis = 2;
            r.rendered = av::RenderThumbnail(m, opt, &r.px);
            r.thumbMs = msSince(t1);
            if (!r.rendered || r.px.size() != (size_t)cellSize * cellSize) {
                r.px.assign((size_t)cellSize * cellSize, 0xFF0000FFu); // 红 = 渲染失败
                r.rendered = false;
            }
        }
    };
    std::vector<std::thread> pool;
    for (int t = 1; t < threads; ++t) pool.emplace_back(worker, t);
    worker(0);
    for (auto& th : pool) th.join();

    // ---- 统计 + 拼版 ----
    size_t okCount = 0, failCount = 0, renderFail = 0, sheetIndex = 0;
    double totalParse = 0, totalThumb = 0, worstParse = 0, worstThumb = 0;
    std::string worstParseName, worstThumbName, failList;

    const int COLS = 8, ROWS = 6, perSheet = COLS * ROWS;
    const int sheetW = COLS * cellSize, sheetH = ROWS * cellSize;
    std::vector<uint32_t> sheet;

    FILE* idx = nullptr;
    std::string idxPath = outDir + "/index.txt";
    if (fopen_s(&idx, idxPath.c_str(), "wb") == 0 && idx) {
        std::fprintf(idx, "\xEF\xBB\xBF"); // UTF-8 BOM，方便记事本
    }

    auto flush = [&]() {
        if (sheet.empty()) return;
        char name[64];
        std::snprintf(name, sizeof(name), "/sheet_%02zu.png", sheetIndex++);
        std::string path = outDir + name;
        if (!av::img::SavePngFromBgra(av::img::Wide(path), sheet.data(), sheetW, sheetH))
            std::printf("!! SavePng failed: %s\n", path.c_str());
        sheet.clear();
    };

    for (size_t i = 0; i < results.size(); ++i) {
        Result& r = results[i];
        totalParse += r.parseMs; totalThumb += r.thumbMs;
        if (r.parseMs > worstParse) { worstParse = r.parseMs; worstParseName = r.path; }
        if (r.thumbMs > worstThumb) { worstThumb = r.thumbMs; worstThumbName = r.path; }
        if (r.ok) ++okCount;
        else {
            ++failCount;
            if (failList.size() < 4000) failList += "\n  FAIL " + r.path + " : " + (r.error.empty() ? "(unknown)" : r.error);
        }
        if (!r.rendered) ++renderFail;

        if ((int)(i % perSheet) == 0) sheet.assign((size_t)sheetW * sheetH, 0xFF1E1E1E);
        int slot = (int)(i % perSheet);
        blit(sheet, sheetW, sheetH, r.px, cellSize, cellSize,
             (slot % COLS) * cellSize, (slot / COLS) * cellSize, 0xFF000000u);
        if (slot == perSheet - 1 || i + 1 == results.size()) flush();

        if (idx) {
            std::fprintf(idx, "%s\tok=%d tris=%u verts=%u mats=%u bones=%u clips=%u parse=%.2fms thumb=%.2fms\terr=%s\n",
                         r.path.c_str(), r.ok ? 1 : 0, r.tris, r.verts, r.mats, r.bones, r.clips,
                         r.parseMs, r.thumbMs, r.error.empty() ? "-" : r.error.c_str());
        }
    }
    if (idx) std::fclose(idx);

    std::printf("\n==== summary ====\n");
    std::printf("ok=%zu fail=%zu renderFail=%zu\n", okCount, failCount, renderFail);
    std::printf("parse avg=%.2fms worst=%.2fms (%s)\n",
                files.empty() ? 0.0 : totalParse / files.size(), worstParse, worstParseName.c_str());
    std::printf("thumb avg=%.2fms worst=%.2fms (%s)\n",
                files.empty() ? 0.0 : totalThumb / files.size(), worstThumb, worstThumbName.c_str());
    std::printf("total(parse+thumb, wall over %d threads)=%.2fs  sheets=%zu out=%s\n",
                threads, (totalParse + totalThumb) / 1000.0, sheetIndex, outDir.c_str());
    if (!failList.empty()) std::printf("failures:%s\n", failList.c_str());
    return (failCount == 0 && okCount > 0) ? 0 : 3;
}
