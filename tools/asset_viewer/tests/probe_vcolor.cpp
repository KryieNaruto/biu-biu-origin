// probe_vcolor.cpp —— 验证顶点色（COLOR_0 / LayerElementColor）是否被加载器读进来。
//
// 用法：probe_vcolor.exe <素材根> [--limit=N]
// 输出：每个模型一行「有顶点色 / 全白 / 比例」，末尾给汇总。
//
// 判定「有顶点色」的规则与渲染端一致：r,g,b 三者不全 >= 0.999 即视为真实顶点色。
#include "model_data.h"

#include <windows.h>
#include <shellapi.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static bool ToUtf8(const std::wstring& w, std::string* out) {
    if (w.empty()) { out->clear(); return true; }
    int n = ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return false;
    out->resize((size_t)n);
    ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &(*out)[0], n, nullptr, nullptr);
    return true;
}

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: probe_vcolor.exe <assetRoot> [--limit=N]\n");
        return 2;
    }
    // 用宽字符 argv，避免中文路径被 ACP 破坏
    int wargc = 0;
    LPWSTR* wargv = ::CommandLineToArgvW(::GetCommandLineW(), &wargc);
    std::wstring root;
    int limit = 0;
    if (wargv) {
        for (int i = 1; i < wargc; ++i) {
            std::wstring a(wargv[i]);
            if (a.rfind(L"--limit=", 0) == 0) limit = (int)::wcstol(a.c_str() + 8, nullptr, 10);
            else if (a.rfind(L"--", 0) != 0) root = a;
        }
        ::LocalFree(wargv);
    }
    if (root.empty()) { std::printf("no root\n"); return 2; }

    std::vector<std::wstring> files;
    std::error_code ec;
    for (auto it = fs::recursive_directory_iterator(root, ec);
         it != fs::recursive_directory_iterator(); it.increment(ec)) {
        if (ec) break;
        if (!it->is_regular_file(ec)) continue;
        std::wstring ext = it->path().extension().wstring();
        for (auto& c : ext) c = (wchar_t)::towlower(c);
        if (ext == L".glb" || ext == L".fbx") files.push_back(it->path().wstring());
    }
    std::sort(files.begin(), files.end());
    if (limit > 0 && (int)files.size() > limit) files.resize((size_t)limit);

    int totalOk = 0, withColor = 0, allWhite = 0, coloredTris = 0, totalTris = 0;
    for (const auto& wpath : files) {
        std::string utf8;
        if (!ToUtf8(wpath, &utf8)) continue;
        av::ModelData md;
        if (!av::LoadModel(utf8, &md) || !md.ok) {
            std::printf("[SKIP] %s\n", utf8.c_str());
            continue;
        }
        ++totalOk;
        size_t coloredVerts = 0;
        for (const auto& v : md.mesh.vertices) {
            if (!(v.r >= 0.999f && v.g >= 0.999f && v.b >= 0.999f)) ++coloredVerts;
        }
        const size_t nv = md.mesh.vertices.size();
        const bool has = coloredVerts > 0;
        if (has) ++withColor; else ++allWhite;
        coloredTris += (int)(has ? md.triangleCount : 0);
        totalTris += (int)md.triangleCount;
        if (has) {
            std::printf("[VC ] verts=%zu colored=%zu (%.1f%%) tris=%u  %s\n", nv, coloredVerts,
                        100.0 * (double)coloredVerts / (double)(nv ? nv : 1),
                        (unsigned)md.triangleCount, utf8.c_str());
        }
    }
    std::printf("\n==== summary ====\n");
    std::printf("models=%d ok=%d withVertexColor=%d allWhite=%d\n", (int)files.size(), totalOk,
                withColor, allWhite);
    std::printf("triangles covered by vertex color = %d / %d (%.1f%%)\n", coloredTris, totalTris,
                totalTris ? 100.0 * (double)coloredTris / (double)totalTris : 0.0);
    return 0;
}
