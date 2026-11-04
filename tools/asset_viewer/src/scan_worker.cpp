// scan_worker.cpp —— 后台扫描 + 缩略图线程 + 磁盘缓存
#include "scan_worker.h"

#include "av_util.h"
#include "model_data.h"

#if AV_HAVE_LOADERS
// 依赖 glb-loader 提供的解析与光栅模块；缺失时退化为占位实现（见 ProduceOne）。
#include "glb_loader.h"
#include "fbx_loader.h"
#include "thumb_renderer.h"
#endif

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>

namespace av {

// ---------------------------------------------------------------------------
// 缓存文件格式
//   PNG : 缩略图像素（由 wic_util 读写）。AV_HAVE_LOADERS=0 时写自带的简单 PNG。
//   .avmeta : 二进制
//     magic "AVMT" (4) | version u32 | payloadSize u32 | payload | crc32(payload)
//     payload: ok u8, hasError u8, tri u32, vert u32, mat u32, bone u32, clip u32,
//              errorLen u32 + bytes, warnLen u32 + bytes, clipCount u32 + (len u32 + bytes)*
// ---------------------------------------------------------------------------
namespace {

constexpr uint32_t kMetaMagic = 0x544D5641u; // 'AVMT' little-endian
constexpr uint32_t kMetaVersion = 2u;

uint32_t Crc32(const unsigned char* data, size_t len) {
    static uint32_t table[256];
    static bool init = false;
    if (!init) {
        for (uint32_t i = 0; i < 256; ++i) {
            uint32_t c = i;
            for (int k = 0; k < 8; ++k) c = (c & 1) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[i] = c;
        }
        init = true;
    }
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t i = 0; i < len; ++i)
        crc = table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
}

struct ByteWriter {
    std::vector<unsigned char> buf;
    void u8(unsigned char v) { buf.push_back(v); }
    void u32(uint32_t v) {
        for (int i = 0; i < 4; ++i) buf.push_back((unsigned char)((v >> (i * 8)) & 0xFF));
    }
    void str(const std::string& s) {
        u32((uint32_t)s.size());
        buf.insert(buf.end(), s.begin(), s.end());
    }
};

struct ByteReader {
    const unsigned char* p = nullptr;
    size_t size = 0;
    size_t pos = 0;
    bool ok = true;
    bool Read(void* dst, size_t n) {
        if (!ok || pos + n > size) { ok = false; return false; }
        ::memcpy(dst, p + pos, n);
        pos += n;
        return true;
    }
    uint32_t u32() {
        uint32_t v = 0;
        unsigned char b[4];
        if (!Read(b, 4)) return 0;
        for (int i = 0; i < 4; ++i) v |= (uint32_t)b[i] << (i * 8);
        return v;
    }
    unsigned char u8() {
        unsigned char v = 0;
        Read(&v, 1);
        return v;
    }
    std::string str() {
        uint32_t n = u32();
        if (!ok || n > 1u << 20 || pos + n > size) { ok = false; return std::string(); }
        std::string s((const char*)p + pos, n);
        pos += n;
        return s;
    }
};

int64_t NowMs() {
    return (int64_t)std::chrono::duration_cast<std::chrono::milliseconds>(
               std::chrono::steady_clock::now().time_since_epoch()).count();
}

// AV_HAVE_LOADERS=0 时生成占位缩略图：深色渐变 + 网格 + 中央图标
void MakePlaceholderThumb(int size, ThumbResult* out) {
    out->width = out->height = size;
    out->pixels.assign((size_t)size * size, 0xFF20242Bu);
    for (int y = 0; y < size; ++y) {
        float fy = (float)y / (float)size;
        unsigned char r = (unsigned char)(0x22 + fy * 0x10);
        unsigned char g = (unsigned char)(0x26 + fy * 0x10);
        unsigned char b = (unsigned char)(0x2E + fy * 0x0C);
        uint32_t c = ((uint32_t)0xFF << 24) | ((uint32_t)r << 16) | ((uint32_t)g << 8) | b;
        for (int x = 0; x < size; ++x) out->pixels[(size_t)y * size + x] = c;
    }
    // 棋盘格 + 中心菱形代表「模型未渲染」
    const int cell = size / 8;
    for (int y = 0; y < size; ++y) {
        for (int x = 0; x < size; ++x) {
            if (((x / cell) + (y / cell)) & 1) continue;
            uint32_t& px = out->pixels[(size_t)y * size + x];
            unsigned char r = (unsigned char)((px >> 16) & 0xFF), g = (unsigned char)((px >> 8) & 0xFF),
                          b = (unsigned char)(px & 0xFF);
            px = ((uint32_t)0xFF << 24) | ((uint32_t)(r + 6) << 16) | ((uint32_t)(g + 6) << 8) | (b + 6);
        }
    }
    const float cx = size * 0.5f, cy = size * 0.5f, rad = size * 0.22f;
    for (int y = 0; y < size; ++y) {
        for (int x = 0; x < size; ++x) {
            float dx = (float)x - cx, dy = (float)y - cy;
            if (dx * dx + dy * dy <= rad * rad) {
                out->pixels[(size_t)y * size + x] = 0xFF607088u;
            }
        }
    }
}

// AV_HAVE_LOADERS=0 时的占位统计：给出直观的假数据，便于 UI 自测布局
void MakePlaceholderMeta(const ScanEntry& e, EntryMeta* m) {
    m->parsed = true;
    m->ok = true;
    m->triangleCount = (uint32_t)((e.fileSize / 97) % 5000 + 200);
    m->vertexCount = m->triangleCount * 2;
    m->materialCount = 2;
    m->boneCount = 0;
    m->clipCount = 0;
    m->warning = "占位数据（未链接解析模块）";
}

} // namespace

// ---------------------------------------------------------------------------
// 生命周期
// ---------------------------------------------------------------------------
ScanWorker::ScanWorker() {}
ScanWorker::~ScanWorker() { Stop(); }

bool ScanWorker::Start(const std::string& rootUtf8, const std::wstring& rootDir,
                       const std::wstring& cacheDir, HWND hwnd, int thumbSize,
                       int samplesPerAxis, int threads) {
    Stop();

    hwnd_ = hwnd;
    rootUtf8_ = rootUtf8;
    rootDir_ = rootDir;
    cacheDir_ = cacheDir;
    thumbSize_ = thumbSize;
    samplesPerAxis_ = samplesPerAxis;
    if (threads <= 0) {
        SYSTEM_INFO si;
        ::GetSystemInfo(&si);
        int n = (int)si.dwNumberOfProcessors - 1;
        if (n < 1) n = 1;
        if (n > 8) n = 8;
        threads = n;
    }
    threadCount_ = threads;

    {
        std::lock_guard<std::mutex> lk(dataMutex_);
        entries_.clear();
    }
    {
        std::lock_guard<std::mutex> lk(jobMutex_);
        jobs_.clear();
        queued_.clear();
        stopping_ = false;
        scanFinished_ = false;
        allDonePosted_ = false;
    }
    thumbDone_ = 0;
    thumbFailed_ = 0;
    cacheHits_ = 0;
    cacheMisses_ = 0;
    scanMs_ = 0.0;
    startTick_ = NowMs();
    scanTick_ = 0;

    if (!cacheDir_.empty()) avu::EnsureDir(cacheDir_);

    scanThread_ = std::thread(&ScanWorker::RunScan, this);
    thumbThreads_.clear();
    for (int i = 0; i < threadCount_; ++i)
        thumbThreads_.emplace_back(&ScanWorker::RunThumb, this);
    return true;
}

void ScanWorker::Stop() {
    {
        std::lock_guard<std::mutex> lk(jobMutex_);
        stopping_ = true;
        scanFinished_ = true;
    }
    jobCv_.notify_all();
    if (scanThread_.joinable()) scanThread_.join();
    for (size_t i = 0; i < thumbThreads_.size(); ++i)
        if (thumbThreads_[i].joinable()) thumbThreads_[i].join();
    thumbThreads_.clear();
}

// ---------------------------------------------------------------------------
// 扫描线程
// ---------------------------------------------------------------------------
void ScanWorker::RunScan() {
    std::vector<ScanEntry> list;
    const int64_t t0 = NowMs();

    // 进度回调：每隔一段报一次，UI 可以更新状态栏
    uint32_t lastReport = 0;
    av::ScanProgressFn cb = [this, &lastReport](uint32_t scanned, const std::wstring&) -> bool {
        {
            std::lock_guard<std::mutex> lk(jobMutex_);
            if (stopping_) return false;
        }
        if (scanned - lastReport >= 32) {
            lastReport = scanned;
            Notify(kMsgScanProgress, scanned);
        }
        return true;
    };
    const bool ok = av::ScanAssets(rootUtf8_, &list, cb, skipHidden_);

    {
        std::lock_guard<std::mutex> lk(jobMutex_);
        if (stopping_) return;
    }
    {
        std::lock_guard<std::mutex> lk(dataMutex_);
        entries_.resize(list.size());
        for (size_t i = 0; i < list.size(); ++i) entries_[i].scan = list[i];
    }
    {
        std::lock_guard<std::mutex> lk(jobMutex_);
        queued_.assign(list.size(), false);
        for (size_t i = 0; i < list.size(); ++i) {
            jobs_.push_back(i);
            queued_[i] = true;
        }
        scanFinished_ = true;
    }
    scanTick_ = NowMs();
    scanMs_ = (double)(scanTick_ - t0);
    jobCv_.notify_all();

    Notify(kMsgScanDone, ok ? list.size() : 0);
    if (list.empty()) Notify(kMsgAllDone, 0);
}

// ---------------------------------------------------------------------------
// 缩略图线程
// ---------------------------------------------------------------------------
void ScanWorker::PushJob(size_t idx, bool front) {
    {
        std::lock_guard<std::mutex> lk(jobMutex_);
        if (stopping_ || idx >= queued_.size()) return;
        if (queued_[idx]) return;
        queued_[idx] = true;
        if (front) jobs_.push_front(idx);
        else       jobs_.push_back(idx);
    }
    jobCv_.notify_one();
}

bool ScanWorker::PopJob(size_t* idx) {
    std::unique_lock<std::mutex> lk(jobMutex_);
    for (;;) {
        if (stopping_) return false;
        if (!jobs_.empty()) {
            *idx = jobs_.front();
            jobs_.pop_front();
            return true;
        }
        // 队列空且扫描已结束 -> 线程退出；否则等新任务
        if (scanFinished_) {
            // 再检查一次，避免扫描线程刚发布
            if (jobs_.empty()) return false;
        } else {
            jobCv_.wait(lk);
        }
    }
}

void ScanWorker::Prioritize(size_t first, size_t last) {
    const size_t total = Count();
    if (total == 0) return;
    if (last >= total) last = total - 1;
    std::vector<size_t> want;
    {
        std::lock_guard<std::mutex> lk(dataMutex_);
        for (size_t i = first; i <= last && i < total; ++i) {
            // 只对仍处于 pending 的条目提升优先级，避免重复生产导致计数错乱
            if (entries_[i].thumbState == kThumbPending) want.push_back(i);
        }
    }
    // 逆序前插，保证 first 排在最前
    for (size_t k = want.size(); k > 0; --k) PushJob(want[k - 1], true);
}

void ScanWorker::RunThumb() {
    for (;;) {
        size_t idx = 0;
        if (!PopJob(&idx)) break;
        ProduceOne(idx);
    }
}

// ---------------------------------------------------------------------------
// 单条生产
// ---------------------------------------------------------------------------
std::wstring ScanWorker::CacheBase(size_t idx) const {
    ScanEntry e;
    {
        std::lock_guard<std::mutex> lk(dataMutex_);
        if (idx >= entries_.size()) return std::wstring();
        e = entries_[idx].scan;
    }
    char keyBuf[64];
    ::sprintf_s(keyBuf, sizeof(keyBuf), "|%llu|%lld|%d", (unsigned long long)e.fileSize,
                (long long)e.mtimeUnix, thumbSize_);
    const std::string key = e.path + keyBuf;
    const std::string hex = avu::Sha1Hex(key);
    std::wstring base = cacheDir_;
    if (!base.empty() && base.back() != L'\\') base += L'\\';
    base += avu::Wide(hex);
    return base;
}

bool ScanWorker::SaveThumbCache(size_t idx, const ThumbResult& r) {
    if (cacheDir_.empty()) return false;
    std::wstring base = CacheBase(idx);
    if (base.empty()) return false;

    // 自写的简单 PNG 编码（无条件可控、不依赖 WIC 是否可用）；
    // 若 loader 模块提供了 wic_util 的写函数则优先使用（见 AV_HAVE_LOADERS 分支）。
    std::wstring path = base + L".png";
    const int w = r.width, h = r.height;
    if (w <= 0 || h <= 0 || r.pixels.size() < (size_t)w * h) return false;

    // raw scanlines: filter byte 0 + BGRA->RGBA
    std::vector<unsigned char> raw((size_t)h * (1 + (size_t)w * 4));
    for (int y = 0; y < h; ++y) {
        unsigned char* dst = raw.data() + (size_t)y * (1 + (size_t)w * 4);
        dst[0] = 0;
        for (int x = 0; x < w; ++x) {
            uint32_t c = r.pixels[(size_t)y * w + x];
            unsigned char b = (unsigned char)(c & 0xFF), g = (unsigned char)((c >> 8) & 0xFF),
                          rr = (unsigned char)((c >> 16) & 0xFF), a = (unsigned char)((c >> 24) & 0xFF);
            dst[1 + x * 4 + 0] = rr;
            dst[1 + x * 4 + 1] = g;
            dst[1 + x * 4 + 2] = b;
            dst[1 + x * 4 + 3] = a;
        }
    }
    // zlib store-only（无压缩，代码短且稳健）
    std::vector<unsigned char> z;
    z.push_back(0x78);
    z.push_back(0x01);
    uint32_t a = 1, b = 0;
    size_t pos = 0;
    while (pos < raw.size()) {
        size_t n = raw.size() - pos;
        if (n > 65535) n = 65535;
        z.push_back(pos + n >= raw.size() ? 1 : 0);
        z.push_back((unsigned char)(n & 0xFF));
        z.push_back((unsigned char)(n >> 8));
        z.push_back((unsigned char)(~n & 0xFF));
        z.push_back((unsigned char)((~n >> 8) & 0xFF));
        for (size_t i = 0; i < n; ++i) {
            unsigned char c = raw[pos + i];
            z.push_back(c);
            a = (a + c) % 65521;
            b = (b + a) % 65521;
        }
        pos += n;
    }
    z.push_back((unsigned char)(b >> 8));
    z.push_back((unsigned char)(b & 0xFF));
    z.push_back((unsigned char)(a >> 8));
    z.push_back((unsigned char)(a & 0xFF));

    auto be32 = [](std::vector<unsigned char>& v, uint32_t x) {
        v.push_back((unsigned char)(x >> 24));
        v.push_back((unsigned char)(x >> 16));
        v.push_back((unsigned char)(x >> 8));
        v.push_back((unsigned char)x);
    };
    auto chunk = [&](const char* type, const std::vector<unsigned char>& data) {
        std::vector<unsigned char> d;
        be32(d, (uint32_t)data.size());
        const char* t = type;
        for (int i = 0; i < 4; ++i) d.push_back((unsigned char)t[i]);
        d.insert(d.end(), data.begin(), data.end());
        std::vector<unsigned char> crcIn(d.begin() + 4, d.end());
        be32(d, Crc32(crcIn.data(), crcIn.size()));
        return d;
    };

    std::vector<unsigned char> png;
    const unsigned char sig[8] = {0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A};
    png.insert(png.end(), sig, sig + 8);
    std::vector<unsigned char> ihdr;
    be32(ihdr, (uint32_t)w);
    be32(ihdr, (uint32_t)h);
    ihdr.push_back(8);
    ihdr.push_back(6);
    ihdr.push_back(0);
    ihdr.push_back(0);
    ihdr.push_back(0);
    std::vector<unsigned char> c1 = chunk("IHDR", ihdr);
    png.insert(png.end(), c1.begin(), c1.end());
    std::vector<unsigned char> c2 = chunk("IDAT", z);
    png.insert(png.end(), c2.begin(), c2.end());
    std::vector<unsigned char> empty;
    std::vector<unsigned char> c3 = chunk("IEND", empty);
    png.insert(png.end(), c3.begin(), c3.end());

    return avu::WriteFileBytes(path, png);
}

bool ScanWorker::SaveMetaCache(size_t idx, const EntryMeta& m) {
    if (cacheDir_.empty()) return false;
    std::wstring base = CacheBase(idx);
    if (base.empty()) return false;

    ByteWriter payload;
    payload.u8(1);
    payload.u8(m.ok ? 1 : 0);
    payload.u32(m.triangleCount);
    payload.u32(m.vertexCount);
    payload.u32(m.materialCount);
    payload.u32(m.boneCount);
    payload.u32(m.clipCount);
    payload.str(m.error);
    payload.str(m.warning);
    payload.u32((uint32_t)m.clipNames.size());
    for (size_t i = 0; i < m.clipNames.size(); ++i) payload.str(m.clipNames[i]);

    std::vector<unsigned char> file;
    auto le32 = [&](uint32_t v) {
        file.push_back((unsigned char)(v & 0xFF));
        file.push_back((unsigned char)((v >> 8) & 0xFF));
        file.push_back((unsigned char)((v >> 16) & 0xFF));
        file.push_back((unsigned char)((v >> 24) & 0xFF));
    };
    le32(kMetaMagic);
    le32(kMetaVersion);
    le32((uint32_t)payload.buf.size());
    file.insert(file.end(), payload.buf.begin(), payload.buf.end());
    le32(Crc32(payload.buf.data(), payload.buf.size()));

    return avu::WriteFileBytes(base + L".avmeta", file);
}

bool ScanWorker::TryLoadCached(size_t idx, ThumbResult* thumb, EntryMeta* meta) {
    std::wstring base = CacheBase(idx);
    if (base.empty() || thumb == nullptr) return false;

    std::vector<unsigned char> png;
    if (!avu::ReadFileBytes(base + L".png", &png) || png.size() < 8) {
        cacheMisses_++;
        return false;
    }

    // 解析 PNG：只支持本文件写出的 8bit RGBA、无隔行、store-only zlib 或普通 deflate
    if (!(png[0] == 0x89 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G')) {
        cacheMisses_++;
        return false;
    }
    size_t pos = 8;
    uint32_t w = 0, h = 0;
    int bitDepth = 0, colorType = 0, interlace = 0;
    std::vector<unsigned char> idat;
    while (pos + 8 <= png.size()) {
        uint32_t len = ((uint32_t)png[pos] << 24) | ((uint32_t)png[pos + 1] << 16) |
                       ((uint32_t)png[pos + 2] << 8) | (uint32_t)png[pos + 3];
        const char* type = (const char*)&png[pos + 4];
        if (pos + 12 + len > png.size()) break;
        if (::memcmp(type, "IHDR", 4) == 0 && len >= 13) {
            w = ((uint32_t)png[pos + 8] << 24) | ((uint32_t)png[pos + 9] << 16) |
                ((uint32_t)png[pos + 10] << 8) | (uint32_t)png[pos + 11];
            h = ((uint32_t)png[pos + 12] << 24) | ((uint32_t)png[pos + 13] << 16) |
                ((uint32_t)png[pos + 14] << 8) | (uint32_t)png[pos + 15];
            bitDepth = png[pos + 16];
            colorType = png[pos + 17];
            interlace = png[pos + 20];
        } else if (::memcmp(type, "IDAT", 4) == 0) {
            idat.insert(idat.end(), png.begin() + pos + 8, png.begin() + pos + 8 + len);
        } else if (::memcmp(type, "IEND", 4) == 0) {
            break;
        }
        pos += 12 + len;
    }
    if (w == 0 || h == 0 || bitDepth != 8 || (colorType != 6 && colorType != 2) || interlace != 0 ||
        w > 4096 || h > 4096) {
        cacheMisses_++;
        return false;
    }

    // 解压：遇到非 store-only 块就放弃（写端只产生 store-only）
    const size_t bpp = (colorType == 6) ? 4 : 3;
    const size_t stride = (size_t)w * bpp;
    std::vector<unsigned char> raw;
    raw.reserve((size_t)h * (stride + 1));
    if (idat.size() < 6) {
        cacheMisses_++;
        return false;
    }
    size_t zpos = 2; // 跳过 zlib 头
    bool done = false;
    while (!done && zpos + 5 <= idat.size()) {
        const unsigned char hdr = idat[zpos];
        const int bfinal = hdr & 1;
        const int btype = (hdr >> 1) & 3;
        if (btype != 0) break; // 只支持 store-only
        const size_t n = (size_t)idat[zpos + 1] | ((size_t)idat[zpos + 2] << 8);
        if (zpos + 5 + n > idat.size()) break;
        raw.insert(raw.end(), idat.begin() + zpos + 5, idat.begin() + zpos + 5 + n);
        zpos += 5 + n;
        if (bfinal) done = true;
    }
    if (!done || raw.size() < (size_t)h * (stride + 1)) {
        cacheMisses_++;
        return false;
    }

    thumb->width = (int)w;
    thumb->height = (int)h;
    thumb->pixels.assign((size_t)w * h, 0xFF000000u);
    for (uint32_t y = 0; y < h; ++y) {
        const unsigned char* src = raw.data() + (size_t)y * (stride + 1);
        if (src[0] != 0) { // 不支持滤波器
            cacheMisses_++;
            return false;
        }
        src += 1;
        for (uint32_t x = 0; x < w; ++x) {
            unsigned char r = src[x * bpp + 0];
            unsigned char g = src[x * bpp + 1];
            unsigned char b = src[x * bpp + 2];
            unsigned char a = (bpp == 4) ? src[x * bpp + 3] : 0xFF;
            thumb->pixels[(size_t)y * w + x] =
                ((uint32_t)a << 24) | ((uint32_t)r << 16) | ((uint32_t)g << 8) | (uint32_t)b;
        }
    }

    if (meta) {
        std::vector<unsigned char> mf;
        if (avu::ReadFileBytes(base + L".avmeta", &mf) && mf.size() >= 16) {
            auto le = [&](size_t at) -> uint32_t {
                return (uint32_t)mf[at] | ((uint32_t)mf[at + 1] << 8) |
                       ((uint32_t)mf[at + 2] << 16) | ((uint32_t)mf[at + 3] << 24);
            };
            const uint32_t magic = le(0), ver = le(4), plen = le(8);
            if (magic == kMetaMagic && ver == kMetaVersion && 16 + plen <= mf.size()) {
                const uint32_t crc = le(12 + plen);
                if (crc == Crc32(mf.data() + 12, plen)) {
                    ByteReader r;
                    r.p = mf.data() + 12;
                    r.size = plen;
                    r.u8(); // hasMeta
                    meta->ok = r.u8() != 0;
                    meta->triangleCount = r.u32();
                    meta->vertexCount = r.u32();
                    meta->materialCount = r.u32();
                    meta->boneCount = r.u32();
                    meta->clipCount = r.u32();
                    meta->error = r.str();
                    meta->warning = r.str();
                    const uint32_t clipN = r.u32();
                    meta->clipNames.clear();
                    for (uint32_t i = 0; i < clipN && r.ok && i < 4096; ++i)
                        meta->clipNames.push_back(r.str());
                    if (r.ok) {
                        meta->parsed = true;
                        meta->fromCache = true;
                    } else {
                        *meta = EntryMeta();
                    }
                }
            }
        }
    }
    cacheHits_++;
    return true;
}

void ScanWorker::ProduceOne(size_t idx) {
    ScanEntry e;
    {
        std::lock_guard<std::mutex> lk(dataMutex_);
        if (idx >= entries_.size()) return;
        e = entries_[idx].scan;
    }

    EntryMeta meta;
    ThumbResult thumb;
    bool haveThumb = false;

    if (TryLoadCached(idx, &thumb, &meta)) {
        haveThumb = true;
    } else {
#if AV_HAVE_LOADERS
        ModelData model;
        const bool loaded = av::LoadModel(e.path, &model) && model.ok;
        meta.parsed = true;
        meta.ok = loaded;
        if (loaded) {
            meta.triangleCount = model.triangleCount;
            meta.vertexCount = model.vertexCount;
            meta.materialCount = model.materialCount;
            meta.boneCount = model.boneCount;
            meta.clipCount = model.clipCount;
            meta.clipNames.clear();
            for (size_t c = 0; c < model.clips.size(); ++c)
                meta.clipNames.push_back(model.clips[c].name);
            meta.error = model.error;
            meta.warning = model.warning;

            ThumbOptions opt;
            opt.size = thumbSize_;
            opt.samplesPerAxis = samplesPerAxis_;
            std::vector<uint32_t> px;
            if (av::RenderThumbnail(model, opt, &px)) {
                thumb.width = opt.size;
                thumb.height = opt.size;
                thumb.pixels.swap(px);
                haveThumb = true;
            }
        } else {
            meta.error = model.error.empty() ? std::string("解析失败") : model.error;
        }
#else
        MakePlaceholderMeta(e, &meta);
        MakePlaceholderThumb(thumbSize_, &thumb);
        haveThumb = true;
#endif
    }

    if (!haveThumb && thumbSize_ > 0) {
        // 解析失败也要有占位（红色标记在 UI 侧绘制）
        MakePlaceholderThumb(thumbSize_, &thumb);
    }

    if (haveThumb) {
        SaveThumbCache(idx, thumb);
        if (meta.parsed) SaveMetaCache(idx, meta);
    }

    int state = kThumbPending;
    if (meta.parsed) state = meta.ok ? kThumbReady : kThumbFailed;
    else            state = haveThumb ? kThumbReady : kThumbFailed;

    {
        std::lock_guard<std::mutex> lk(dataMutex_);
        if (idx >= entries_.size()) return;
        WorkerEntry& w = entries_[idx];
        w.meta = meta;
        w.thumbState = state;
        w.thumb = thumb;
        w.hasThumb = true;
    }
    if (state == kThumbReady) thumbDone_++;
    else                      thumbFailed_++;

    Notify(kMsgMetaReady, idx);
    Notify(kMsgThumbReady, idx);

    // 全部完成时发一次（先取 Count，避免在 jobMutex_ 下再取 dataMutex_ 造成锁序反转）
    const size_t total = Count();
    {
        std::lock_guard<std::mutex> jl(jobMutex_);
        if (scanFinished_ && !allDonePosted_ && total > 0 &&
            jobs_.empty() && (thumbDone_.load() + thumbFailed_.load()) >= total) {
            allDonePosted_ = true;
            ::PostMessageW(hwnd_, kMsgAllDone, (WPARAM)total, 0);
        }
    }
}

void ScanWorker::Notify(UINT msg, size_t idx) const {
    if (hwnd_) ::PostMessageW(hwnd_, msg, (WPARAM)idx, 0);
}

// ---------------------------------------------------------------------------
// 读取接口
// ---------------------------------------------------------------------------
size_t ScanWorker::Count() const {
    std::lock_guard<std::mutex> lk(dataMutex_);
    return entries_.size();
}

bool ScanWorker::GetScan(size_t index, ScanEntry* out) const {
    std::lock_guard<std::mutex> lk(dataMutex_);
    if (index >= entries_.size()) return false;
    *out = entries_[index].scan;
    return true;
}

bool ScanWorker::GetMeta(size_t index, EntryMeta* out) const {
    std::lock_guard<std::mutex> lk(dataMutex_);
    if (index >= entries_.size()) return false;
    *out = entries_[index].meta;
    return true;
}

int ScanWorker::GetThumbState(size_t index) const {
    std::lock_guard<std::mutex> lk(dataMutex_);
    if (index >= entries_.size()) return kThumbPending;
    return entries_[index].thumbState;
}

bool ScanWorker::TakeThumb(size_t index, ThumbResult* out) {
    std::lock_guard<std::mutex> lk(dataMutex_);
    if (index >= entries_.size()) return false;
    WorkerEntry& w = entries_[index];
    if (!w.hasThumb) return false;
    *out = std::move(w.thumb);
    w.thumb = ThumbResult();
    w.hasThumb = false;
    return true;
}

size_t ScanWorker::ThumbDone() const { return thumbDone_; }
size_t ScanWorker::ThumbFailed() const { return thumbFailed_; }
size_t ScanWorker::DoneCount() const { return thumbDone_ + thumbFailed_; }
size_t ScanWorker::Total() const { return Count(); }
uint64_t ScanWorker::CacheHits() const { return cacheHits_; }
uint64_t ScanWorker::CacheMisses() const { return cacheMisses_; }
double ScanWorker::ScanMs() const { return scanMs_; }
double ScanWorker::ElapsedMs() const {
    if (scanTick_ == 0) return 0.0;
    return (double)(NowMs() - scanTick_);
}

} // namespace av
