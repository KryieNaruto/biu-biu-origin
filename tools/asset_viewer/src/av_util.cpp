// av_util.cpp —— UI 模块内部公共工具实现
#include "av_util.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <ctime>

namespace avu {

// ---------------------------------------------------------------------------
// 字符串
// ---------------------------------------------------------------------------
std::wstring Wide(const std::string& utf8) {
    if (utf8.empty()) return std::wstring();
    // 先探测长度（失败时宽松处理，把非法字节替换掉而不是整串失败）
    int n = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(),
                                  (int)utf8.size(), nullptr, 0);
    DWORD flags = MB_ERR_INVALID_CHARS;
    if (n <= 0) {
        flags = 0; // 非法序列 -> 系统替换字符
        n = ::MultiByteToWideChar(CP_UTF8, flags, utf8.data(), (int)utf8.size(), nullptr, 0);
        if (n <= 0) return std::wstring();
    }
    std::wstring out((size_t)n, L'\0');
    if (::MultiByteToWideChar(CP_UTF8, flags, utf8.data(), (int)utf8.size(), &out[0], n) <= 0)
        return std::wstring();
    return out;
}

std::string Utf8(const std::wstring& wide) {
    if (wide.empty()) return std::string();
    int n = ::WideCharToMultiByte(CP_UTF8, 0, wide.data(), (int)wide.size(),
                                  nullptr, 0, nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string out((size_t)n, '\0');
    if (::WideCharToMultiByte(CP_UTF8, 0, wide.data(), (int)wide.size(),
                              &out[0], n, nullptr, nullptr) <= 0)
        return std::string();
    return out;
}

bool IsValidUtf8(const std::string& s) {
    size_t i = 0;
    const size_t n = s.size();
    while (i < n) {
        const unsigned char c = (unsigned char)s[i];
        size_t need = 0;
        if (c < 0x80) {
            i++;
            continue;
        } else if ((c & 0xE0) == 0xC0) {
            need = 1;
            if (c < 0xC2) return false; // 过长编码
        } else if ((c & 0xF0) == 0xE0) {
            need = 2;
        } else if ((c & 0xF8) == 0xF0) {
            need = 3;
            if (c > 0xF4) return false; // 超出 U+10FFFF
        } else {
            return false;
        }
        if (i + need >= n) return false;
        for (size_t k = 1; k <= need; ++k) {
            const unsigned char cc = (unsigned char)s[i + k];
            if ((cc & 0xC0) != 0x80) return false;
        }
        i += need + 1;
    }
    return true;
}

// ---------------------------------------------------------------------------
// 文件
// ---------------------------------------------------------------------------
bool ReadFileBytes(const std::wstring& path, std::vector<unsigned char>* out) {
    if (!out) return false;
    out->clear();
    HANDLE h = ::CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER sz;
    if (!::GetFileSizeEx(h, &sz) || sz.QuadPart < 0 || sz.QuadPart > (LONGLONG)(1024ull * 1024 * 1024)) {
        ::CloseHandle(h);
        return false;
    }
    out->resize((size_t)sz.QuadPart);
    size_t done = 0;
    while (done < out->size()) {
        DWORD got = 0;
        DWORD want = (DWORD)((out->size() - done) > 0x10000000u ? 0x10000000u : (out->size() - done));
        if (!::ReadFile(h, out->data() + done, want, &got, nullptr) || got == 0) {
            ::CloseHandle(h);
            out->clear();
            return false;
        }
        done += got;
    }
    ::CloseHandle(h);
    return true;
}

bool WriteFileBytes(const std::wstring& path, const std::vector<unsigned char>& bytes) {
    std::wstring dir = ParentDir(path);
    if (!dir.empty()) EnsureDir(dir);
    HANDLE h = ::CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    size_t done = 0;
    bool ok = true;
    while (done < bytes.size()) {
        DWORD put = 0;
        DWORD want = (DWORD)((bytes.size() - done) > 0x10000000u ? 0x10000000u : (bytes.size() - done));
        if (!::WriteFile(h, bytes.data() + done, want, &put, nullptr) || put == 0) {
            ok = false;
            break;
        }
        done += put;
    }
    ::CloseHandle(h);
    return ok;
}

bool WriteFileText(const std::wstring& path, const std::string& text) {
    std::vector<unsigned char> b(text.begin(), text.end());
    return WriteFileBytes(path, b);
}

bool FileExists(const std::wstring& path) {
    DWORD a = ::GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool DirExists(const std::wstring& path) {
    DWORD a = ::GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

bool EnsureDir(const std::wstring& dir) {
    if (dir.empty()) return false;
    if (DirExists(dir)) return true;
    std::wstring parent = ParentDir(dir);
    if (!parent.empty() && parent != dir) EnsureDir(parent);
    if (::CreateDirectoryW(dir.c_str(), nullptr)) return true;
    return ::GetLastError() == ERROR_ALREADY_EXISTS;
}

std::wstring ParentDir(const std::wstring& path) {
    if (path.empty()) return std::wstring();
    size_t end = path.size();
    // 去掉尾部反斜杠
    while (end > 1 && (path[end - 1] == L'\\' || path[end - 1] == L'/')) --end;
    size_t pos = path.find_last_of(L"\\/", end - 1);
    if (pos == std::wstring::npos) return std::wstring();
    if (pos == 0) return path.substr(0, 1);
    // 处理 "C:\" 形式
    if (pos == 2 && path[1] == L':') return path.substr(0, 3);
    return path.substr(0, pos);
}

std::wstring FileName(const std::wstring& path) {
    size_t pos = path.find_last_of(L"\\/");
    if (pos == std::wstring::npos) return path;
    return path.substr(pos + 1);
}

std::wstring Stem(const std::wstring& path) {
    std::wstring f = FileName(path);
    size_t dot = f.find_last_of(L'.');
    if (dot == std::wstring::npos || dot == 0) return f;
    return f.substr(0, dot);
}

std::wstring LowerExt(const std::wstring& path) {
    std::wstring f = FileName(path);
    size_t dot = f.find_last_of(L'.');
    if (dot == std::wstring::npos) return std::wstring();
    std::wstring ext = f.substr(dot + 1);
    for (size_t i = 0; i < ext.size(); ++i)
        ext[i] = (wchar_t)::towlower(ext[i]);
    return ext;
}

// ---------------------------------------------------------------------------
// 哈希
// ---------------------------------------------------------------------------
uint64_t Fnv1a64(const std::string& s) {
    uint64_t h = 1469598103934665603ull;
    for (size_t i = 0; i < s.size(); ++i) {
        h ^= (uint64_t)(unsigned char)s[i];
        h *= 1099511628211ull;
    }
    return h;
}

// ---- SHA-1（自实现，避免依赖 CAPI / OpenSSL）----
namespace {

inline uint32_t Rol32(uint32_t v, int bits) {
    return (v << bits) | (v >> (32 - bits));
}

void Sha1Block(const unsigned char* block, uint32_t h[5]) {
    uint32_t w[80];
    for (int i = 0; i < 16; ++i) {
        w[i] = ((uint32_t)block[i * 4] << 24) | ((uint32_t)block[i * 4 + 1] << 16) |
               ((uint32_t)block[i * 4 + 2] << 8) | (uint32_t)block[i * 4 + 3];
    }
    for (int i = 16; i < 80; ++i)
        w[i] = Rol32(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);

    uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
    for (int i = 0; i < 80; ++i) {
        uint32_t f, k;
        if (i < 20) {
            f = (b & c) | ((~b) & d);
            k = 0x5A827999u;
        } else if (i < 40) {
            f = b ^ c ^ d;
            k = 0x6ED9EBA1u;
        } else if (i < 60) {
            f = (b & c) | (b & d) | (c & d);
            k = 0x8F1BBCDCu;
        } else {
            f = b ^ c ^ d;
            k = 0xCA62C1D6u;
        }
        uint32_t tmp = Rol32(a, 5) + f + e + k + w[i];
        e = d;
        d = c;
        c = Rol32(b, 30);
        b = a;
        a = tmp;
    }
    h[0] += a;
    h[1] += b;
    h[2] += c;
    h[3] += d;
    h[4] += e;
}

} // namespace

std::string Sha1Hex(const std::string& s) {
    uint32_t h[5] = {0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u};

    // 完整块
    size_t i = 0;
    while (i + 64 <= s.size()) {
        Sha1Block((const unsigned char*)s.data() + i, h);
        i += 64;
    }
    // 尾块填充
    unsigned char tail[128];
    ::memset(tail, 0, sizeof(tail));
    size_t rest = s.size() - i;
    if (rest) ::memcpy(tail, s.data() + i, rest);
    tail[rest] = 0x80;
    const size_t tailLen = (rest + 1 + 8 <= 64) ? 64 : 128;
    const uint64_t bitLen = (uint64_t)s.size() * 8ull;
    for (int k = 0; k < 8; ++k)
        tail[tailLen - 1 - k] = (unsigned char)((bitLen >> (k * 8)) & 0xFF);
    Sha1Block(tail, h);
    if (tailLen == 128) Sha1Block(tail + 64, h);

    char out[41];
    for (int k = 0; k < 5; ++k) {
        char buf[9];
        ::sprintf_s(buf, sizeof(buf), "%08x", h[k]);
        ::memcpy(out + k * 8, buf, 8);
    }
    out[40] = '\0';
    return std::string(out, 40);
}

// ---------------------------------------------------------------------------
// 格式化
// ---------------------------------------------------------------------------
std::string FormatInt(uint64_t v) {
    char raw[32];
    ::sprintf_s(raw, sizeof(raw), "%llu", (unsigned long long)v);
    std::string digits(raw);
    std::string out;
    int count = 0;
    for (int i = (int)digits.size() - 1; i >= 0; --i) {
        out.insert(out.begin(), digits[(size_t)i]);
        if (++count % 3 == 0 && i > 0) out.insert(out.begin(), ',');
    }
    return out;
}

std::string FormatSize(uint64_t bytes) {
    char buf[64];
    if (bytes >= 1024ull * 1024ull) {
        double mb = (double)bytes / (1024.0 * 1024.0);
        ::sprintf_s(buf, sizeof(buf), "%.2f MB", mb);
    } else if (bytes >= 1024ull) {
        double kb = (double)bytes / 1024.0;
        ::sprintf_s(buf, sizeof(buf), "%.1f KB", kb);
    } else {
        ::sprintf_s(buf, sizeof(buf), "%llu B", (unsigned long long)bytes);
    }
    return std::string(buf);
}

std::string FormatDuration(double seconds) {
    char buf[64];
    ::sprintf_s(buf, sizeof(buf), "%.2f s", seconds);
    return std::string(buf);
}

int64_t NowUnixMillis() {
    FILETIME ft;
    ::GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER u;
    u.LowPart = ft.dwLowDateTime;
    u.HighPart = ft.dwHighDateTime;
    // 100ns since 1601-01-01 -> ms since 1970-01-01
    return (int64_t)((u.QuadPart - 116444736000000000ull) / 10000ull);
}

} // namespace avu
