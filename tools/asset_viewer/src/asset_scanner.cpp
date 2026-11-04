// asset_scanner.cpp —— 递归扫描实现
#include "asset_scanner.h"

#include "av_util.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <algorithm>
#include <map>

namespace av {

bool IsModelExtension(const std::wstring& extLower) {
    return extLower == L"glb" || extLower == L"gltf" || extLower == L"fbx";
}

std::string CategoryFromRelativePath(const std::wstring& relPath) {
    // 去掉可能的 "./" 前缀
    size_t start = 0;
    while (start + 1 < relPath.size() && relPath[start] == L'.' &&
           (relPath[start + 1] == L'\\' || relPath[start + 1] == L'/'))
        start += 2;
    size_t pos = relPath.find_first_of(L"\\/", start);
    if (pos == std::wstring::npos) return std::string(); // 根目录下的文件
    return avu::Utf8(relPath.substr(start, pos - start));
}

namespace {

struct DirNode {
    std::wstring path;       // 绝对路径
    std::wstring relPrefix;  // 相对扫描根的前缀（分类 = 前缀第一段）
    int depth = 0;
};

void SortEntries(std::vector<ScanEntry>* out) {
    // 同分类内按 name 排序（大小写不敏感），分类之间按名字排序。
    // 分类排序在前，让 9 个分类连续呈现，浏览器体验更好。
    std::stable_sort(out->begin(), out->end(),
                     [](const ScanEntry& a, const ScanEntry& b) {
                         if (a.category != b.category) return a.category < b.category;
                         std::string an = a.name, bn = b.name;
                         for (size_t i = 0; i < an.size(); ++i) an[i] = (char)::tolower((unsigned char)an[i]);
                         for (size_t i = 0; i < bn.size(); ++i) bn[i] = (char)::tolower((unsigned char)bn[i]);
                         return an < bn;
                     });
}

} // namespace

bool ScanAssets(const std::string& rootUtf8, std::vector<ScanEntry>* out,
                const ScanProgressFn& progress, bool skipHidden) {
    if (!out) return false;
    out->clear();

    const std::wstring root = avu::Wide(rootUtf8);
    if (root.empty() || !avu::DirExists(root)) return false;

    // 规范化根路径：去掉尾部反斜杠，最后补一个反斜杠作为前缀
    std::wstring rootNorm = root;
    while (rootNorm.size() > 3 && (rootNorm.back() == L'\\' || rootNorm.back() == L'/'))
        rootNorm.pop_back();
    if (rootNorm.size() == 2 && rootNorm[1] == L':') rootNorm += L'\\';
    if (!rootNorm.empty() && rootNorm.back() != L'\\') rootNorm += L'\\';

    std::vector<DirNode> stack;
    DirNode first;
    first.path = rootNorm;
    first.relPrefix = L"";
    first.depth = 0;
    stack.push_back(first);

    // 防止符号链接成环：记录已访问的目录（规范化路径）
    uint32_t visitedCount = 0;
    const uint32_t kMaxDirs = 20000;
    const int kMaxDepth = 24;

    while (!stack.empty()) {
        DirNode node = stack.back();
        stack.pop_back();
        ++visitedCount;
        if (visitedCount > kMaxDirs) break;

        // 枚举目录项
        std::wstring pattern = node.path;
        if (!pattern.empty() && pattern.back() != L'\\') pattern += L'\\';
        pattern += L'*';

        WIN32_FIND_DATAW fd;
        HANDLE hFind = ::FindFirstFileExW(pattern.c_str(), FindExInfoBasic, &fd,
                                          FindExSearchNameMatch, nullptr,
                                          FIND_FIRST_EX_LARGE_FETCH);
        if (hFind == INVALID_HANDLE_VALUE) continue;

        // 记录子目录的「名字」和相对于扫描根的「前缀」两套信息
        std::vector<std::wstring> subNames;
        std::vector<std::wstring> subRels;
        do {
            const std::wstring fname = fd.cFileName;
            if (fname == L"." || fname == L"..") continue;

            const bool isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            if (skipHidden && (fd.dwFileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0) continue;
            if (skipHidden && (fd.dwFileAttributes & FILE_ATTRIBUTE_SYSTEM) != 0) continue;
            // 跳过符号链接/重解析点，避免环
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) continue;

            const std::wstring rel = node.relPrefix + fname;

            if (isDir) {
                if (node.depth + 1 <= kMaxDepth) {
                    subNames.push_back(fname);
                    subRels.push_back(rel);
                }
                continue;
            }

            const std::wstring ext = avu::LowerExt(fname);
            if (!IsModelExtension(ext)) continue;

            ScanEntry e;
            e.path = avu::Utf8(node.path + fname);
            e.name = avu::Utf8(avu::Stem(fname));
            e.fileSize = ((uint64_t)fd.nFileSizeHigh << 32) | (uint64_t)fd.nFileSizeLow;
            {
                ULARGE_INTEGER u;
                u.LowPart = fd.ftLastWriteTime.dwLowDateTime;
                u.HighPart = fd.ftLastWriteTime.dwHighDateTime;
                // 100ns since 1601 -> seconds since 1970
                e.mtimeUnix = (int64_t)((u.QuadPart - 116444736000000000ull) / 10000000ull);
            }
            e.isFBX = (ext == L"fbx");
            e.category = CategoryFromRelativePath(rel);
            out->push_back(e);

            if (progress && (out->size() % 16) == 0) {
                if (!progress((uint32_t)out->size(), node.path)) {
                    ::FindClose(hFind);
                    SortEntries(out);
                    return true;
                }
            }
        } while (::FindNextFileW(hFind, &fd));
        ::FindClose(hFind);

        // 按名字排序；逆序入栈，使字母序靠前的目录先被遍历
        std::vector<size_t> order(subNames.size());
        for (size_t i = 0; i < order.size(); ++i) order[i] = i;
        std::sort(order.begin(), order.end(),
                  [&subNames](size_t a, size_t b) { return subNames[a] < subNames[b]; });
        for (size_t k = order.size(); k > 0; --k) {
            const size_t i = order[k - 1];
            DirNode child;
            child.path = node.path + subNames[i];
            if (!child.path.empty() && child.path.back() != L'\\') child.path += L'\\';
            child.relPrefix = subRels[i] + std::wstring(1, L'\\');
            child.depth = node.depth + 1;
            stack.push_back(child);
        }
    }

    SortEntries(out);
    if (progress) progress((uint32_t)out->size(), std::wstring());
    return true;
}

} // namespace av
