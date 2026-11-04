// asset_scanner.h —— 递归扫描素材目录，收集条目元数据
// OWNER: winui-list。结构定义见 ref/SHARED_SPEC.md §8（字段名与语义一致）。
#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace av {

// 扫描到的一个素材条目
struct ScanEntry {
    std::string path;       // UTF-8 绝对路径
    std::string name;       // 不含扩展名的文件名（UTF-8）
    std::string category;   // 扫描根下的第一层目录名（UTF-8），根目录下的文件为空
    uint64_t    fileSize = 0;
    int64_t     mtimeUnix = 0;   // 秒
    bool        isFBX = false;
};

// 扫描进度回调：scanned = 已发现条目数，currentDir = 当前正在遍历的目录（宽字符）
// 返回 false 表示请求取消扫描。
typedef std::function<bool(uint32_t scanned, const std::wstring& currentDir)> ScanProgressFn;

// 递归扫描 rootUtf8 下所有 .glb / .gltf / .fbx。
// - 结果按 category（稳定顺序：A-Z 然后按发现顺序）同分类内按 name 排序，方便浏览。
// - 单个条目读不到不会被计入错误（目录项自己就带 size/mtime）。
// - skipHidden=true 时跳过隐藏文件/系统目录。
// 返回是否扫描成功（root 存在且是目录）。
bool ScanAssets(const std::string& rootUtf8, std::vector<ScanEntry>* out,
                const ScanProgressFn& progress = ScanProgressFn(),
                bool skipHidden = true);

// 判断扩展名是否为受支持的模型格式（"glb"/"gltf"/"fbx"，大小写不敏感）
bool IsModelExtension(const std::wstring& extLower);

// 把相对路径的第一段目录名作为分类。rel 使用反斜杠或正斜杠均可。
std::string CategoryFromRelativePath(const std::wstring& relPath);

} // namespace av
