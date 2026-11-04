// av_util.h —— UI 模块内部公共小工具（字符串转换 / 哈希 / 时间）
// OWNER: winui-list。仅供 src/ 下 UI 侧 .cpp 使用，不对外暴露给 loader。
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace avu {

// ---- UTF-8 <-> UTF-16（Unicode-safe，非法序列替换为 U+FFFD）----
std::wstring Wide(const std::string& utf8);
std::string  Utf8(const std::wstring& wide);

// 判断 UTF-8 是否合法（扫描到非法字节返回 false）
bool IsValidUtf8(const std::string& s);

// ---- 文件 ----
// 读整个文件到内存；失败返回 false
bool ReadFileBytes(const std::wstring& path, std::vector<unsigned char>* out);
// 写整个文件（UTF-8 文本）；目录不存在时自动创建
bool WriteFileText(const std::wstring& path, const std::string& text);
bool WriteFileBytes(const std::wstring& path, const std::vector<unsigned char>& bytes);
bool FileExists(const std::wstring& path);
bool DirExists(const std::wstring& path);
// 递归创建目录（支持多级）
bool EnsureDir(const std::wstring& dir);
// 取父目录（不带尾部反斜杠）；无父目录返回空
std::wstring ParentDir(const std::wstring& path);
// 取文件名（含扩展名）
std::wstring FileName(const std::wstring& path);
// 取不含扩展名的文件名
std::wstring Stem(const std::wstring& path);
// 小写扩展名（不含点），如 "glb"
std::wstring LowerExt(const std::wstring& path);

// ---- 哈希 ----
// FNV-1a 64 位，用于缓存键
uint64_t Fnv1a64(const std::string& s);
// SHA-1（16 字节输出，十六进制 40 字符小写）。用于缩略图缓存文件名。
std::string Sha1Hex(const std::string& s);

// ---- 文本格式化（数字千分位）----
std::string FormatInt(uint64_t v);          // 12345 -> "12,345"
std::string FormatSize(uint64_t bytes);     // 单位自动 KB/MB
std::string FormatDuration(double seconds); // "1.25s"

// ---- 时间 ----
int64_t NowUnixMillis();

} // namespace avu
