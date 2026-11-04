// loader_smoke.cpp —— 加载器自测（可执行）
// OWNER: glb-loader
//
// 用途：
//   1) 扫描 素材/ 下全部 .glb + .fbx，逐个 LoadModel（LoadGLB/LoadFBX），
//      打印 名称/三角形/顶点/材质/骨骼/动画/耗时/失败原因，并统计汇总。
//   2) 对代表性文件渲染缩略图，输出 PNG 到 build/_selftest/。
//
// 用法：
//   loader_smoke.exe               # 全量扫描 + 渲染 6 张代表性缩略图
//   loader_smoke.exe --no-scan     # 只渲染缩略图（快速目检）
//   loader_smoke.exe --scan-only   # 只扫描不渲染
//   loader_smoke.exe <root>        # 指定扫描根目录（默认 ..\..\..\..\素材）
#include <windows.h>
#include <objbase.h>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "anim_sampler.h"
#include "model_data.h"
#include "wic_util.h"

namespace {

// ---------------------------------------------------------------------------
// 目录遍历（UTF-16 原生，中文路径无障碍）
// ---------------------------------------------------------------------------
void LowerExt(const std::wstring& path, std::wstring* ext) {
    size_t dot = path.find_last_of(L'.');
    size_t sep = path.find_last_of(L"/\\");
    ext->clear();
    if (dot == std::wstring::npos) return;
    if (sep != std::wstring::npos && dot < sep) return;
    for (size_t i = dot; i < path.size(); ++i) {
        wchar_t c = path[i];
        if (c >= L'A' && c <= L'Z') c = (wchar_t)(c - L'A' + L'a');
        ext->push_back(c);
    }
}

void Walk(const std::wstring& dir, int depth, std::vector<std::wstring>* glbs, std::vector<std::wstring>* fbxes) {
    if (depth > 8) return;
    WIN32_FIND_DATAW fd;
    std::wstring pattern = dir + L"\\*";
    HANDLE h = ::FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.cFileName[0] == L'.' && fd.cFileName[1] == 0) continue;
        if (fd.cFileName[0] == L'.' && fd.cFileName[1] == L'.' && fd.cFileName[2] == 0) continue;
        std::wstring full = dir + L"\\" + fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            Walk(full, depth + 1, glbs, fbxes);
        } else {
            std::wstring ext;
            LowerExt(full, &ext);
            if (ext == L".glb" || ext == L".gltf") glbs->push_back(full);
            else if (ext == L".fbx") fbxes->push_back(full);
        }
    } while (::FindNextFileW(h, &fd));
    ::FindClose(h);
}

std::wstring BaseNameNoExt(const std::wstring& p) {
    size_t sep = p.find_last_of(L"/\\");
    std::wstring name = (sep == std::wstring::npos) ? p : p.substr(sep + 1);
    size_t dot = name.find_last_of(L'.');
    if (dot != std::wstring::npos) name = name.substr(0, dot);
    return name;
}

// 把 UTF-16 名转成 UTF-8 打印（控制台是 GBK，直接写 UTF-8 会乱码，
// 这里转本地代码页以保证中文可读）
std::string ToLocal(const std::wstring& w) {
    if (w.empty()) return std::string();
    int n = ::WideCharToMultiByte(CP_ACP, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string s((size_t)n, '\0');
    ::WideCharToMultiByte(CP_ACP, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

std::string ToUtf8(const std::wstring& w) {
    if (w.empty()) return std::string();
    int n = ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string s((size_t)n, '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

// UTF-8（加载器 error 文本）→ 本地代码页，便于控制台显示
std::string Utf8ToLocal(const std::string& u) {
    if (u.empty()) return u;
    int n = ::MultiByteToWideChar(CP_UTF8, 0, u.c_str(), (int)u.size(), nullptr, 0);
    if (n <= 0) return u;
    std::wstring w((size_t)n, L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, u.c_str(), (int)u.size(), &w[0], n);
    return ToLocal(w);
}

double NowMs() {
    using clk = std::chrono::high_resolution_clock;
    static clk::time_point t0 = clk::now();
    return std::chrono::duration<double, std::milli>(clk::now() - t0).count();
}

} // namespace

int main(int argc, char** argv) {
    ::SetConsoleOutputCP(936);
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    bool doScan = true;
    bool doRender = true;
    std::wstring root;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--no-scan") == 0) doScan = false;
        else if (std::strcmp(argv[i], "--scan-only") == 0) doRender = false;
        else {
            // argv 是本地代码页
            int n = ::MultiByteToWideChar(CP_ACP, 0, argv[i], -1, nullptr, 0);
            if (n > 1) {
                std::wstring w((size_t)n, L'\0');
                ::MultiByteToWideChar(CP_ACP, 0, argv[i], -1, &w[0], n);
                w.resize((size_t)n - 1);
                root = w;
            }
        }
    }
    if (root.empty()) {
        // 默认：从 exe 所在目录上溯到工程根，再进 素材
        // build\_check\smoke.exe → _check(build 子目录) → build → asset_viewer → tools → 工程根
        wchar_t exePath[MAX_PATH] = {0};
        ::GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        std::wstring dir = exePath;
        size_t sep = dir.find_last_of(L"/\\");
        if (sep != std::wstring::npos) dir = dir.substr(0, sep);
        // 逐级上溯，直到找到含 project.godot 的目录；找不到再用固定层数
        std::wstring probe = dir;
        bool found = false;
        for (int i = 0; i < 8; ++i) {
            if (::GetFileAttributesW((probe + L"\\project.godot").c_str()) != INVALID_FILE_ATTRIBUTES) {
                found = true;
                break;
            }
            size_t s2 = probe.find_last_of(L"/\\");
            if (s2 == std::wstring::npos) break;
            probe = probe.substr(0, s2);
        }
        if (!found) {
            probe = dir;
            for (int i = 0; i < 5; ++i) {
                size_t s2 = probe.find_last_of(L"/\\");
                if (s2 == std::wstring::npos) break;
                probe = probe.substr(0, s2);
            }
        }
        root = probe + L"\\素材";
    }

    std::printf("== loader_smoke ==\n");
    std::printf("scan root: %s\n", ToLocal(root).c_str());

    std::vector<std::wstring> glbs, fbxes;
    Walk(root, 0, &glbs, &fbxes);
    std::sort(glbs.begin(), glbs.end());
    std::sort(fbxes.begin(), fbxes.end());
    std::printf("found: %d glb/gltf, %d fbx\n\n", (int)glbs.size(), (int)fbxes.size());

    // -----------------------------------------------------------------
    // 1) 全量扫描
    // -----------------------------------------------------------------
    if (doScan) {
        struct Stat {
            int total = 0, ok = 0, fail = 0;
            double totalMs = 0.0, worstMs = 0.0;
            std::wstring worstFile;
            uint64_t verts = 0, tris = 0;
        } sGlb, sFbx;

        auto scanOne = [&](const std::wstring& full, Stat* st) -> bool {
            av::ModelData md;
            double t0 = NowMs();
            bool ok = av::LoadModel(ToUtf8(full), &md);
            double dt = NowMs() - t0;
            ++st->total;
            st->totalMs += dt;
            if (dt > st->worstMs) { st->worstMs = dt; st->worstFile = BaseNameNoExt(full); }
            if (ok && md.ok) ++st->ok;
            else ++st->fail;
            st->verts += md.mesh.vertices.size();
            st->tris += md.mesh.indices.size() / 3;

            std::printf("%-34s %7u tris %7u verts  m=%-2u b=%-3u c=%-3u t=%-4zu %7.2fms %s%s%s\n",
                        ToLocal(BaseNameNoExt(full)).c_str(),
                        (unsigned)(md.mesh.indices.size() / 3),
                        (unsigned)md.mesh.vertices.size(),
                        md.materialCount, md.boneCount, md.clipCount, md.textures.size(), dt,
                        ok ? "OK" : "FAIL",
                        md.error.empty() ? "" : " | ",
                        md.error.empty() ? "" : Utf8ToLocal(md.error).c_str());
            return ok;
        };

        std::printf("---- GLB/GLTF ----\n");
        for (const auto& f : glbs) scanOne(f, &sGlb);
        std::printf("\n---- FBX ----\n");
        for (const auto& f : fbxes) scanOne(f, &sFbx);

        auto report = [](const char* label, const Stat& s) {
            double avg = s.total > 0 ? s.totalMs / s.total : 0.0;
            std::printf("\n[%s] total=%d ok=%d fail=%d  avg=%.2fms worst=%.2fms (%s)  verts=%llu tris=%llu\n",
                        label, s.total, s.ok, s.fail, avg, s.worstMs, ToLocal(s.worstFile).c_str(),
                        (unsigned long long)s.verts, (unsigned long long)s.tris);
        };
        report("GLB", sGlb);
        report("FBX", sFbx);
        double allMs = sGlb.totalMs + sFbx.totalMs;
        int allN = sGlb.total + sFbx.total;
        std::printf("\n[TOTAL] files=%d ok=%d fail=%d avg=%.2fms total=%.1fms\n",
                    allN, sGlb.ok + sFbx.ok, sGlb.fail + sFbx.fail, allN > 0 ? allMs / allN : 0.0, allMs);
    }

    // -----------------------------------------------------------------
    // 2) 代表性缩略图
    // -----------------------------------------------------------------
    if (doRender) {
        struct Case { std::wstring rel; const char* out; };
        // 中文目录用 wstring 拼接
        std::vector<Case> cases = {
            {L"卡通人物带全套动画\\史蒂夫.glb", "01_steve"},
            {L"海盗场景带动画\\Anne.glb", "02_anne"},
            {L"RPG武器\\Coin.glb", "03_coin"},
            {L"RTS建筑\\Crops.glb", "04_crops"},
            {L"POLYGON_Starter_SourceFiles_v2\\SourceFiles\\FBX\\SM_Generic_Ground_01.fbx", "05_ground_fbx"},
            {L"POLYGON_Starter_SourceFiles_v2\\SourceFiles\\FBX\\SM_Bean_Cop_01.fbx", "06_bean_fbx"},
        };

        std::wstring outDir;
        {
            wchar_t exePath[MAX_PATH] = {0};
            ::GetModuleFileNameW(nullptr, exePath, MAX_PATH);
            outDir = exePath;
            size_t sep = outDir.find_last_of(L"/\\");
            if (sep != std::wstring::npos) outDir = outDir.substr(0, sep);
        }
        outDir += L"\\_selftest";
        av::img::EnsureDir(outDir);

        std::printf("\n---- thumbnails => %s ----\n", ToLocal(outDir).c_str());
        av::ThumbOptions opt;
        opt.size = 256;
        opt.samplesPerAxis = 3;
        for (const auto& c : cases) {
            std::wstring full = root + L"\\" + c.rel;
            av::ModelData md;
            double t0 = NowMs();
            bool ok = av::LoadModel(ToUtf8(full), &md);
            double loadMs = NowMs() - t0;
            std::vector<uint32_t> px;
            double t1 = NowMs();
            bool rok = av::RenderThumbnail(md, opt, &px);
            double renderMs = NowMs() - t1;
            std::wstring outPath = outDir + L"\\" + std::wstring(c.out, c.out + std::strlen(c.out)) + L".png";
            bool saved = false;
            if (rok && px.size() >= (size_t)opt.size * opt.size) {
                saved = av::img::SavePngFromBgra(outPath, px.data(), opt.size, opt.size);
            }
            std::printf("%-18s load=%-6s %6.2fms | render=%s %6.2fms | save=%s | %s\n",
                        c.out, ok ? "OK" : "FAIL", loadMs, rok ? "OK" : "FAIL", renderMs,
                        saved ? "OK" : "FAIL", ToLocal(outPath).c_str());
        }
    }

    ::CoUninitialize();
    std::printf("\ndone.\n");
    return 0;
}
