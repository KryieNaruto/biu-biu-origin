// wic_util.cpp —— WIC 图片解码 + PNG 编码 + UTF-8 路径工具
// OWNER: glb-loader
#include "wic_util.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <wincodec.h>
#include <objbase.h>

#include <cstring>
#include <mutex>

#pragma comment(lib, "windowscodecs.lib")

namespace av {
namespace img {

// ---------------------------------------------------------------------------
// UTF-8 <-> UTF-16
// ---------------------------------------------------------------------------
std::wstring Wide(const std::string& utf8) {
    if (utf8.empty()) return std::wstring();
    int n = ::MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), nullptr, 0);
    if (n <= 0) return std::wstring();
    std::wstring w((size_t)n, L'\0');
    int got = ::MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), &w[0], n);
    if (got <= 0) return std::wstring();
    w.resize((size_t)got);
    return w;
}

std::string Utf8(const std::wstring& wide) {
    if (wide.empty()) return std::string();
    int n = ::WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(),
                                  nullptr, 0, nullptr, nullptr);
    if (n <= 0) return std::string();
    std::string s((size_t)n, '\0');
    int got = ::WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(),
                                    &s[0], n, nullptr, nullptr);
    if (got <= 0) return std::string();
    s.resize((size_t)got);
    return s;
}

// ---------------------------------------------------------------------------
// 文件 IO（宽字符路径，中文路径必须能打开）
// ---------------------------------------------------------------------------
bool ReadFileBytes(const std::wstring& path, std::vector<unsigned char>* out) {
    if (!out) return false;
    out->clear();
    HANDLE h = ::CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER sz;
    if (!::GetFileSizeEx(h, &sz) || sz.QuadPart < 0 || sz.QuadPart > (LONGLONG)512 * 1024 * 1024) {
        ::CloseHandle(h);
        return false;
    }
    size_t total = (size_t)sz.QuadPart;
    out->resize(total);
    size_t done = 0;
    while (done < total) {
        DWORD want = (DWORD)((total - done) > 0x40000000u ? 0x40000000u : (total - done));
        DWORD got = 0;
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

bool ReadFileBytesUtf8(const std::string& utf8Path, std::vector<unsigned char>* out) {
    std::wstring w = Wide(utf8Path);
    if (w.empty()) return false;
    return ReadFileBytes(w, out);
}

bool EnsureDir(const std::wstring& dir) {
    if (dir.empty()) return false;
    DWORD attr = ::GetFileAttributesW(dir.c_str());
    if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) return true;
    ::CreateDirectoryW(dir.c_str(), nullptr);
    attr = ::GetFileAttributesW(dir.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

bool WriteFileBytes(const std::wstring& path, const std::vector<unsigned char>& bytes) {
    HANDLE h = ::CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    size_t total = bytes.size(), done = 0;
    bool ok = true;
    while (done < total) {
        DWORD want = (DWORD)((total - done) > 0x40000000u ? 0x40000000u : (total - done));
        DWORD wrote = 0;
        if (!::WriteFile(h, bytes.data() + done, want, &wrote, nullptr) || wrote == 0) {
            ok = false;
            break;
        }
        done += wrote;
    }
    ::CloseHandle(h);
    return ok;
}

// ---------------------------------------------------------------------------
// WIC 初始化（每线程幂等）
// ---------------------------------------------------------------------------
namespace {
thread_local bool g_comInitTried = false;

IWICImagingFactory* GetFactory() {
    static IWICImagingFactory* s_factory = nullptr;
    static std::once_flag s_once;
    std::call_once(s_once, []() {
        IWICImagingFactory* f = nullptr;
        HRESULT hr = ::CoCreateInstance(CLSID_WICImagingFactory, nullptr,
                                        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&f));
        if (SUCCEEDED(hr)) s_factory = f;
    });
    return s_factory;
}
} // namespace

bool EnsureWic() {
    if (!g_comInitTried) {
        g_comInitTried = true;
        // COINIT_APARTMENTTHREADED：RPC_E_CHANGED_MODE 表示已初始化为 MTA，可继续用 WIC
        HRESULT hr = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        (void)hr;
    }
    return GetFactory() != nullptr;
}

// ---------------------------------------------------------------------------
// 解码：任意格式 -> RGBA8
// ---------------------------------------------------------------------------
static bool DecodeFrame(IWICBitmapDecoder* dec, Bitmap* out) {
    IWICImagingFactory* factory = GetFactory();
    if (!factory) return false;

    IWICBitmapFrameDecode* frame = nullptr;
    if (FAILED(dec->GetFrame(0, &frame)) || !frame) return false;

    IWICFormatConverter* conv = nullptr;
    bool ok = false;
    if (SUCCEEDED(factory->CreateFormatConverter(&conv)) && conv) {
        // 预乘 alpha 的源（PBGRA）也能正确转直通 RGBA
        HRESULT hr = conv->Initialize(frame, GUID_WICPixelFormat32bppRGBA,
                                      WICBitmapDitherTypeNone, nullptr, 0.0,
                                      WICBitmapPaletteTypeCustom);
        if (SUCCEEDED(hr)) {
            UINT w = 0, h = 0;
            if (SUCCEEDED(conv->GetSize(&w, &h)) && w > 0 && h > 0 &&
                (uint64_t)w * h <= 64ull * 1024 * 1024) {
                out->width = (int)w;
                out->height = (int)h;
                out->rgba.resize((size_t)w * h * 4);
                UINT stride = w * 4;
                if (SUCCEEDED(conv->CopyPixels(nullptr, stride, (UINT)out->rgba.size(),
                                               out->rgba.data()))) {
                    ok = true;
                } else {
                    out->reset();
                }
            }
        }
        conv->Release();
    }
    frame->Release();
    return ok;
}

bool DecodeImageMemory(const unsigned char* data, size_t size, Bitmap* out) {
    if (!out) return false;
    out->reset();
    if (!data || size == 0) return false;
    if (!EnsureWic()) return false;
    IWICImagingFactory* factory = GetFactory();

    IWICStream* stream = nullptr;
    if (FAILED(factory->CreateStream(&stream)) || !stream) return false;
    bool ok = false;
    if (SUCCEEDED(stream->InitializeFromMemory(const_cast<BYTE*>(data), (DWORD)size))) {
        IWICBitmapDecoder* dec = nullptr;
        if (SUCCEEDED(factory->CreateDecoderFromStream(stream, nullptr,
                                                       WICDecodeMetadataCacheOnDemand, &dec)) &&
            dec) {
            ok = DecodeFrame(dec, out);
            dec->Release();
        }
    }
    stream->Release();
    return ok;
}

bool DecodeImageFile(const std::wstring& path, Bitmap* out) {
    if (!out) return false;
    out->reset();
    if (!EnsureWic()) return false;
    IWICImagingFactory* factory = GetFactory();
    IWICBitmapDecoder* dec = nullptr;
    bool ok = false;
    if (SUCCEEDED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                                     WICDecodeMetadataCacheOnDemand, &dec)) &&
        dec) {
        ok = DecodeFrame(dec, out);
        dec->Release();
    }
    return ok;
}

// ---------------------------------------------------------------------------
// PNG 编码
// ---------------------------------------------------------------------------
bool EncodePng(const Bitmap& src, std::vector<unsigned char>* outPng) {
    if (!outPng) return false;
    outPng->clear();
    if (!src.valid()) return false;
    if (!EnsureWic()) return false;
    IWICImagingFactory* factory = GetFactory();

    // 用 COM 的 IStream（CreateStreamOnHGlobal）承载，最后取出字节
    bool ok = false;
    // fDeleteOnRelease = FALSE：句柄由我们自己管理，编码结束后仍可 GlobalLock
    HGLOBAL hMem = ::GlobalAlloc(GMEM_MOVEABLE, 0);
    if (!hMem) return false;
    IStream* memStream = nullptr;
    if (FAILED(::CreateStreamOnHGlobal(hMem, FALSE, &memStream)) || !memStream) {
        ::GlobalFree(hMem);
        return false;
    }
    IWICBitmapEncoder* enc = nullptr;
    IWICBitmapFrameEncode* frame = nullptr;
    do {
        if (FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &enc)) || !enc) break;
        if (FAILED(enc->Initialize(memStream, WICBitmapEncoderNoCache))) break;
        if (FAILED(enc->CreateNewFrame(&frame, nullptr)) || !frame) break;
        if (FAILED(frame->Initialize(nullptr))) break;
        WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppRGBA;
        if (FAILED(frame->SetPixelFormat(&fmt))) break;
        // PNG 编码器常把 RGBA 协商成 BGRA：按协商结果重排通道，而不是放弃
        bool swapRB = false;
        if (IsEqualGUID(fmt, GUID_WICPixelFormat32bppRGBA)) {
            swapRB = false;
        } else if (IsEqualGUID(fmt, GUID_WICPixelFormat32bppBGRA)) {
            swapRB = true;
        } else {
            break;   // 其它格式（如 24bpp）不支持
        }
        if (FAILED(frame->SetSize((UINT)src.width, (UINT)src.height))) break;
        std::vector<unsigned char> pixels;
        const unsigned char* px = src.rgba.data();
        if (swapRB) {
            pixels = src.rgba;
            const size_t n = (size_t)src.width * (size_t)src.height;
            for (size_t i = 0; i < n; ++i) {
                std::swap(pixels[i * 4 + 0], pixels[i * 4 + 2]);
            }
            px = pixels.data();
        }
        if (FAILED(frame->WritePixels((UINT)src.height, (UINT)src.width * 4,
                                      (UINT)((size_t)src.width * (size_t)src.height * 4),
                                      const_cast<BYTE*>(px)))) break;
        if (FAILED(frame->Commit())) break;
        if (FAILED(enc->Commit())) break;

        // 取字节：用 GetHGlobalFromStream 拿到实际句柄（流内部可能已重建内存块）
        HGLOBAL hReal = nullptr;
        if (FAILED(::GetHGlobalFromStream(memStream, &hReal)) || !hReal) break;
        SIZE_T hsize = ::GlobalSize(hReal);
        if (hsize == 0 || hsize > (SIZE_T)256 * 1024 * 1024) break;
        void* hptr = ::GlobalLock(hReal);
        if (!hptr) break;
        outPng->assign((const unsigned char*)hptr, (const unsigned char*)hptr + hsize);
        ::GlobalUnlock(hReal);
        ok = true;
    } while (false);

    if (frame) frame->Release();
    if (enc) enc->Release();
    memStream->Release();   // fDeleteOnRelease = FALSE：句柄未被释放
    ::GlobalFree(hMem);
    if (!ok) outPng->clear();
    return ok;
}

bool SavePng(const std::wstring& path, const Bitmap& src) {
    std::vector<unsigned char> png;
    if (!EncodePng(src, &png)) return false;
    return WriteFileBytes(path, png);
}

bool SavePngUtf8(const std::string& utf8Path, const Bitmap& src) {
    std::wstring w = Wide(utf8Path);
    if (w.empty()) return false;
    return SavePng(w, src);
}

bool SavePngFromBgra(const std::wstring& path, const uint32_t* pixels, int w, int h) {
    if (!pixels || w <= 0 || h <= 0) return false;
    Bitmap bmp;
    bmp.width = w;
    bmp.height = h;
    bmp.rgba.resize((size_t)w * h * 4);
    for (int i = 0; i < w * h; ++i) {
        uint32_t p = pixels[i];   // 内存序 B,G,R,A（小端：低字节 = B，最高字节 = A）
        unsigned char B = (unsigned char)(p & 0xFF);
        unsigned char G = (unsigned char)((p >> 8) & 0xFF);
        unsigned char R = (unsigned char)((p >> 16) & 0xFF);
        unsigned char A = (unsigned char)((p >> 24) & 0xFF);
        bmp.rgba[(size_t)i * 4 + 0] = R;
        bmp.rgba[(size_t)i * 4 + 1] = G;
        bmp.rgba[(size_t)i * 4 + 2] = B;
        bmp.rgba[(size_t)i * 4 + 3] = A;
    }
    return SavePng(path, bmp);
}

} // namespace img
} // namespace av
