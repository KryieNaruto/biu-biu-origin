// scan_worker.h —— 后台线程：扫描目录 + 生成缩略图 + 缓存，结果投递给 UI 线程
// OWNER: winui-list
//
// 线程模型（见 ref/SHARED_SPEC.md §8）：
//   1 个扫描线程：快速产出条目 -> 立刻发 kMsgScanDone，UI 可先显示列表
//   N 个缩略图线程（N = CPU 核数-1，上限 8）：按优先级生产（可见窗口优先）
//   UI 线程通过 WM_APP 消息收到结果；消息只携带索引，数据从本对象读取并加锁保护。
//
// 缓存：<exe目录>/cache/<sha1(path|size|mtime)>_<thumbSize>.png（缩略图像素，BGRA）
//       同名 .avmeta 二进制（统计信息 + 失败原因），二次启动直接秒读。
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "asset_scanner.h"

namespace av {

// ---- 每条素材的缓存元数据（不含大像素数据）----
struct EntryMeta {
    bool        parsed = false;   // 是否已尝试解析（成功或失败都算）
    bool        ok = false;       // 解析成功
    std::string error;            // 失败原因（UTF-8）
    uint32_t    triangleCount = 0;
    uint32_t    vertexCount   = 0;
    uint32_t    materialCount = 0;
    uint32_t    boneCount     = 0;
    uint32_t    clipCount     = 0;
    std::vector<std::string> clipNames;   // 动画名列表（UTF-8）
    std::string warning;                   // 非致命警告
    bool        fromCache = false;         // 是否来自磁盘缓存
};

enum ThumbState : int {
    kThumbPending = 0,
    kThumbReady   = 1,
    kThumbFailed  = 2,
};

struct ThumbResult {
    int width = 0;
    int height = 0;
    // BGRA8，行从上到下（与 av::RenderThumbnail 输出和 D3D11 B8G8R8A8 兼容）
    std::vector<uint32_t> pixels;
};

// 缩略图通知消息（发给 UI 线程）
constexpr UINT kMsgScanProgress = WM_APP + 1;   // wParam=已扫描数
constexpr UINT kMsgScanDone     = WM_APP + 2;   // wParam=条目总数
constexpr UINT kMsgThumbReady   = WM_APP + 3;   // wParam=索引
constexpr UINT kMsgMetaReady    = WM_APP + 4;   // wParam=索引（解析完成）
constexpr UINT kMsgAllDone      = WM_APP + 5;   // 全部处理完毕

// 内部条目：扫描元数据 + 解析统计 + 缩略图状态与像素
struct WorkerEntry {
    ScanEntry   scan;
    EntryMeta   meta;
    int         thumbState = kThumbPending;
    ThumbResult thumb;                     // 仅在 dataMutex_ 保护下读写
    bool        hasThumb   = false;
};

class ScanWorker {
public:
    ScanWorker();
    ~ScanWorker();

    ScanWorker(const ScanWorker&) = delete;
    ScanWorker& operator=(const ScanWorker&) = delete;

    // 启动后台扫描。rootUtf8 为 UTF-8 编码的绝对路径；rootDir 为同一路径的宽字符形式。
    bool Start(const std::string& rootUtf8, const std::wstring& rootDir,
               const std::wstring& cacheDir, HWND hwnd, int thumbSize = 256,
               int samplesPerAxis = 2, int threads = 0);

    // 请求停止并等待线程退出（幂等）
    void Stop();

    // ---- 线程安全读取 ----
    size_t Count() const;
    bool   GetScan(size_t index, ScanEntry* out) const;
    bool   GetMeta(size_t index, EntryMeta* out) const;   // 拷贝，线程安全
    int    GetThumbState(size_t index) const;

    // 取走缩略图像素（线程安全；未就绪返回 false）
    bool TakeThumb(size_t index, ThumbResult* out);

    // 提高优先级：把 [first, last] 区间插到任务队列头（可见窗口优先）
    void Prioritize(size_t first, size_t last);

    // 进度统计
    size_t   ThumbDone() const;
    size_t   ThumbFailed() const;
    size_t   DoneCount() const;      // 已处理（成功或失败）
    size_t   Total() const;
    uint64_t CacheHits() const;
    uint64_t CacheMisses() const;
    double   ScanMs() const;
    double   ElapsedMs() const;

    const std::wstring& RootDir() const { return rootDir_; }
    const std::string&  RootUtf8() const { return rootUtf8_; }
    const std::wstring& CacheDir() const { return cacheDir_; }
    int ThumbSize() const { return thumbSize_; }

private:
    void RunScan();
    void RunThumb();
    void PushJob(size_t idx, bool front);
    bool PopJob(size_t* idx);
    bool TryLoadCached(size_t idx, ThumbResult* thumb, EntryMeta* meta);
    bool SaveThumbCache(size_t idx, const ThumbResult& r);
    bool SaveMetaCache(size_t idx, const EntryMeta& m);
    std::wstring CacheBase(size_t idx) const;

    void ProduceOne(size_t idx);      // 生成一条（依赖 loader；AV_HAVE_LOADERS=0 时占位）
    void Notify(UINT msg, size_t idx) const;

private:
    mutable std::mutex       dataMutex_;
    std::vector<WorkerEntry> entries_;

    mutable std::mutex      jobMutex_;
    std::condition_variable jobCv_;
    std::deque<size_t>      jobs_;
    std::vector<bool>       queued_;
    bool                    stopping_ = false;
    bool                    scanFinished_ = false;
    bool                    allDonePosted_ = false;

    std::thread              scanThread_;
    std::vector<std::thread> thumbThreads_;

    HWND                hwnd_ = nullptr;
    std::string         rootUtf8_;
    std::wstring        rootDir_;
    std::wstring        cacheDir_;
    int                 thumbSize_ = 256;
    int                 samplesPerAxis_ = 2;
    int                 threadCount_ = 0;
    bool                skipHidden_ = true;
    int64_t             startTick_ = 0;
    int64_t             scanTick_ = 0;

    std::atomic<uint32_t> thumbDone_{0};
    std::atomic<uint32_t> thumbFailed_{0};
    std::atomic<uint64_t> cacheHits_{0};
    std::atomic<uint64_t> cacheMisses_{0};
    std::atomic<double>   scanMs_{0.0};
};

} // namespace av
