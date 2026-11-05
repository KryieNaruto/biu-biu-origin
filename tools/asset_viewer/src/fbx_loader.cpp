// fbx_loader.cpp —— FBX 二进制解析（7200 / 7400 / 7500）
// OWNER: glb-loader
//
// 参考：ref/fbx_parse.py（若存在）+ 对 素材 全部 70 个 FBX 的实测。
//
// 实测格式要点（与常见资料不同，勿凭记忆改）：
//   * 70 个文件版本分布：7200×49（主库）、7400×12（Collision）、7500×9（Bean 系列）。
//   * 文件头 "Kaydara FBX Binary  \x00\x1a\x00" 27 字节，其后 u32 版本号；
//   * 所有版本首节点都从 offset 27 开始（27 = 头长，版本号 u32 在 23 处）；
//   * 记录头：7200/7400 = u32 end + u32 numProps + u32 propLen + u8 nameLen（13 字节）
//              7500      = u64 end + u64 numProps + u64 propLen + u8 nameLen（25 字节）
//   * 数组属性（f/d/l/i/b）：u8 元素类型 + u32 数量 + u32 编码 + u32 压缩长度（13 字节，
//     7500 也一样），编码 1 = zlib。压缩长度仅在编码 1 时有效。
//   * 子节点列表 = [记录头 + 属性] .. (end - 13)，末尾 13 字节 0 为终止符。
#include "fbx_loader.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#include "texture.h"
#include "wic_util.h"

namespace av {
namespace {

// ===========================================================================
// zlib 解压（Inflate，支持存储块/固定 Huffman/动态 Huffman）
// 不依赖第三方库：FBX 数组压缩就是标准 zlib (RFC 1950/1951)。
// ===========================================================================
struct HuffTable {
    short counts[16];
    short symbols[288];
};

// 标准 canonical Huffman inflate（RFC 1951），逐位 LSB-first 读取。
// 只依赖标准库；对 FBX 的 zlib 数组是唯一解压路径，因此按 zlib 语义严格实现。
class Inflater {
public:
    Inflater(const unsigned char* data, size_t size) : p_(data), end_(data + size) {}

    bool run(std::vector<unsigned char>* out) {
        out_ = out;
        // 输出上限：DEFLATE 最大压缩比约 1032:1，解压结果不可能超过 (输入长度-2)*1032 + 余量。
        // 没有这个上限时，一旦码表解码出错产生大量「复制」符号，out_ 会无限增长直至 OOM
        // （实测：28KB 的 FBX 把进程撑到 4GB+）。这是权威校验，不是性能优化。
        const size_t kMaxRatio = 1032u;
        const size_t inSize = (size_t)(end_ - p_);
        size_t inBody = (inSize > 2) ? (inSize - 2) : 0;
        if (inBody > (SIZE_MAX / kMaxRatio) - 4096u) return false;   // 防溢出
        outLimit_ = inBody * kMaxRatio + 4096u;
        // zlib 头（RFC 1950）
        if (end_ - p_ < 2) return false;
        unsigned cmf = p_[0], flg = p_[1];
        if ((cmf & 0x0F) != 8) return false;
        if (((cmf << 8) + flg) % 31 != 0) return false;
        if (flg & 0x20) return false;   // 预设字典不支持
        p_ += 2;
        
        active_ = true;
        bool ok = false;
        for (int blk = 0; blk < 1000000; ++blk) {
            int last = 0;
            if (!getBits(1, &last)) { active_ = false; return false; }
            int type = 0;
            if (!getBits(2, &type)) { active_ = false; return false; }
            bool r = false;
            if (type == 0) r = inflateStored();
            else if (type == 1) r = inflateBlock(fixedLit(), fixedDist());
            else if (type == 2) r = inflateDynamic();
            else { active_ = false; return false; }
            if (!r) { active_ = false; return false; }
            if (last) { ok = true; break; }
        }
        active_ = false;
        return ok;
    }

private:
    const unsigned char* p_;
    const unsigned char* end_;
    std::vector<unsigned char>* out_ = nullptr;
    size_t outLimit_ = 0;          // run() 按输入长度算出的输出上限
    bool active_ = false;
    uint32_t bitBuf_ = 0;
    int bitCnt_ = 0;

    // 码表：counts[len] 与按码长排序的 symbols
    struct Table {
        short counts[16];
        short symbols[320];
    };

    static bool build(Table* t, const unsigned char* lens, int n) {
        for (int i = 0; i < 16; ++i) t->counts[i] = 0;
        for (int i = 0; i < n; ++i) {
            if (lens[i] > 15) return false;
            t->counts[lens[i]]++;
        }
        if (t->counts[0] == n) return true;   // 全零码表（合法，解码时不会命中）
        int left = 1;
        for (int len = 1; len <= 15; ++len) {
            left <<= 1;
            left -= t->counts[len];
            if (left < 0) return false;   // 超额订阅
        }
        short offs[16];
        // symbols 只排「有码长」的符号：counts[0]（零长）不参与偏移累加，
        // 否则零长符号数会把所有码长 >=1 的符号整体挤后，解码查表全部错位。
        offs[0] = 0;
        offs[1] = 0;
        for (int len = 2; len < 16; ++len) offs[len] = (short)(offs[len - 1] + t->counts[len - 1]);
        for (int i = 0; i < n; ++i)
            if (lens[i] != 0) t->symbols[offs[lens[i]]++] = (short)i;
        return true;
    }

    bool refill(int need) {
        while (bitCnt_ < need) {
            if (p_ >= end_) return false;
            bitBuf_ |= (uint32_t)(*p_++) << bitCnt_;
            bitCnt_ += 8;
        }
        return true;
    }
    bool getBits(int n, int* v) {
        if (n == 0) { *v = 0; return true; }
        if (!refill(n)) return false;
        *v = (int)(bitBuf_ & ((1u << n) - 1u));
        bitBuf_ >>= n;
        bitCnt_ -= n;
        return true;
    }
    // 解一个 Huffman 符号（canonical；判定式与 zlib inflate.c 的 PUP 宏等价）
    bool decodeSym(const Table* t, int* sym) {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len <= 15; ++len) {
            int b = 0;
            if (!getBits(1, &b)) return false;
            code |= b;
            int count = t->counts[len];
            if (code < first + count) {
                *sym = t->symbols[index + (code - first)];
                return true;
            }
            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }
        return false;
    }

    // 固定 Huffman 码表：进程内只构建一次（等价于 zlib 的静态 inflate_table 结果）
    static const Table& fixedLit() {
        static Table t;
        static bool ready = false;
        if (!ready) {
            unsigned char lens[288];
            for (int i = 0; i < 144; ++i) lens[i] = 8;
            for (int i = 144; i < 256; ++i) lens[i] = 9;
            for (int i = 256; i < 280; ++i) lens[i] = 7;
            for (int i = 280; i < 288; ++i) lens[i] = 8;
            build(&t, lens, 288);
            ready = true;
        }
        return t;
    }
    static const Table& fixedDist() {
        static Table t;
        static bool ready = false;
        if (!ready) {
            unsigned char dl[30];
            for (int i = 0; i < 30; ++i) dl[i] = 5;
            build(&t, dl, 30);
            ready = true;
        }
        return t;
    }

    bool inflateStored() {
        // 丢弃到字节边界
        int drop = bitCnt_ & 7;
        bitBuf_ >>= drop;
        bitCnt_ -= drop;
        if (bitCnt_ >= 8) {
            // 位缓冲里还有整字节，退回输入指针
            int whole = bitCnt_ >> 3;
            p_ -= whole;
            bitBuf_ = 0;
            bitCnt_ = 0;
        }
        if (end_ - p_ < 4) return false;
        unsigned len = (unsigned)p_[0] | ((unsigned)p_[1] << 8);
        unsigned nlen = (unsigned)p_[2] | ((unsigned)p_[3] << 8);
        p_ += 4;
        if ((len ^ 0xFFFFu) != nlen) return false;
        if ((size_t)(end_ - p_) < len) return false;
        if (out_->size() + len > outLimit_) return false;
        out_->insert(out_->end(), p_, p_ + len);
        p_ += len;
        return true;
    }

    bool inflateBlock(const Table& lit, const Table& dist) {
        static const short lenBase[29] = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
                                          35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
        static const short lenExtra[29] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
                                           3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
        static const short distBase[30] = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
                                           257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097,
                                           6145, 8193, 12289, 16385, 24577};
        static const short distExtra[30] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
                                            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
        for (;;) {
            int sym = 0;
            if (!decodeSym(&lit, &sym)) return false;
            if (sym < 256) {
                if (out_->size() >= outLimit_) return false;
                out_->push_back((unsigned char)sym);
                continue;
            }
            if (sym == 256) return true;
            sym -= 257;
            if (sym >= 29) return false;
            int extra = 0;
            if (!getBits(lenExtra[sym], &extra)) return false;
            size_t len = (size_t)(lenBase[sym] + extra);
            if (out_->size() + len > outLimit_) return false;
            int dsym = 0;
            if (!decodeSym(&dist, &dsym)) return false;
            if (dsym >= 30) return false;
            if (!getBits(distExtra[dsym], &extra)) return false;
            size_t d = (size_t)(distBase[dsym] + extra);
            if (d > out_->size()) return false;
            size_t start = out_->size() - d;
            for (size_t i = 0; i < len; ++i) out_->push_back((*out_)[start + i]);
        }
    }

    bool inflateDynamic() {
        static const short order[19] = {16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};
        int hlit = 0, hdist = 0, hclen = 0;
        if (!getBits(5, &hlit) || !getBits(5, &hdist) || !getBits(4, &hclen)) return false;
        hlit += 257;
        hdist += 1;
        hclen += 4;
        if (hlit > 286 || hdist > 30) return false;
        unsigned char clLens[19];
        for (int i = 0; i < 19; ++i) clLens[i] = 0;
        for (int i = 0; i < hclen; ++i) {
            int v = 0;
            if (!getBits(3, &v)) return false;
            clLens[order[i]] = (unsigned char)v;
        }
        Table cl;
        if (!build(&cl, clLens, 19)) return false;
        unsigned char lens[320];
        for (int i = 0; i < 320; ++i) lens[i] = 0;
        int idx = 0, total = hlit + hdist;
        while (idx < total) {
            int sym = 0;
            if (!decodeSym(&cl, &sym)) return false;
            if (sym < 16) {
                lens[idx++] = (unsigned char)sym;
            } else {
                int rep = 0;
                unsigned char val = 0;
                if (sym == 16) {
                    if (idx == 0) return false;
                    if (!getBits(2, &rep)) return false;
                    rep += 3;
                    val = lens[idx - 1];
                } else if (sym == 17) {
                    if (!getBits(3, &rep)) return false;
                    rep += 3;
                } else if (sym == 18) {
                    if (!getBits(7, &rep)) return false;
                    rep += 11;
                } else {
                    return false;
                }
                if (idx + rep > total) return false;
                while (rep-- > 0) lens[idx++] = val;
            }
        }
        Table lit, dist;
        if (!build(&lit, lens, hlit)) return false;
        if (!build(&dist, lens + hlit, hdist)) return false;
        return inflateBlock(lit, dist);
    }
};

bool ZlibDecompress(const unsigned char* data, size_t size, std::vector<unsigned char>* out) {
    out->clear();
    if (!data || size == 0) return false;
    Inflater inf(data, size);
    return inf.run(out);
}

// ===========================================================================
// FBX 节点树
// ===========================================================================
struct FProp {
    enum Kind { Null, Bool, I8, I16, I32, I64, F32, F64, Str, Blob,
                ArrF32, ArrF64, ArrI32, ArrI64 } kind = Null;
    int64_t i = 0;
    double d = 0.0;
    std::string s;
    std::vector<float> af;
    std::vector<double> ad;
    std::vector<int32_t> ai32;
    std::vector<int64_t> ai64;
    std::vector<unsigned char> blob;

    double num() const {
        switch (kind) {
            case I8: case I16: case I32: case I64: return (double)i;
            case F32: case F64: return d;
            default: return 0.0;
        }
    }
    std::string str() const { return s; }
    bool isNum() const { return kind >= I8 && kind <= F64; }
    bool isStr() const { return kind == Str; }
};

struct FNode {
    std::string name;
    std::vector<FProp> props;
    std::vector<std::unique_ptr<FNode>> children;
    // 基本访问
    bool is(const char* n) const { return name == n; }
    const FNode* child(const char* n) const {
        for (const auto& c : children)
            if (c->name == n) return c.get();
        return nullptr;
    }
    const FProp* prop(size_t i) const { return i < props.size() ? &props[i] : nullptr; }
    std::string propStr(size_t i) const { return i < props.size() ? props[i].s : std::string(); }
    // Properties70 里的 'P' 子节点 → 按名字找属性（返回第 4 个属性值，即值本身）
    const FProp* findP(const char* pname) const {
        const FNode* p70 = child("Properties70");
        if (!p70) return nullptr;
        for (const auto& p : p70->children) {
            if (!p->is("P")) continue;
            if (p->propStr(0) == pname) {
                // P: "PropName", "type", "label", "flags", value...
                size_t vi = p->props.size() >= 5 ? 4 : 0;
                return p->prop(vi);
            }
        }
        return nullptr;
    }
};

class FbxReader {
public:
    FbxReader(const std::vector<unsigned char>& data) : data_(data) {}

    bool parse(std::unique_ptr<FNode>* root, uint32_t* version) {
        if (data_.size() < 27) return false;
        // 文件头："Kaydara FBX Binary  "（20 字节）+ 00 1A 00（3 字节）= 23 字节，
        // 紧接着 4 字节版本号（offset 23）。之后才是第一条记录。
        static const unsigned char kMagic[23] = {
            'K','a','y','d','a','r','a',' ','F','B','X',' ','B','i','n','a','r','y',' ',' ',0x00,0x1A,0x00
        };
        if (std::memcmp(data_.data(), kMagic, 23) != 0) return false;
        if (data_.size() < 31) return false;
        std::memcpy(&ver_, data_.data() + 23, 4);
        if (version) *version = ver_;
        headerSize_ = (ver_ >= 7500) ? 25 : 13;
        root->reset(new FNode());
        (*root)->name = "<root>";
        parseList(27, data_.size(), (*root)->children);
        return true;
    }

private:
    const std::vector<unsigned char>& data_;
    uint32_t ver_ = 7400;
    size_t headerSize_ = 13;

    bool readN(size_t off, size_t n, uint64_t* out) {
        if (off + n > data_.size()) return false;
        uint64_t v = 0;
        std::memcpy(&v, data_.data() + off, n);
        *out = v;
        return true;
    }

    // 解析节点，返回记录结束偏移
    bool parseNode(size_t off, uint64_t* endOut, std::unique_ptr<FNode>* out) {
        uint64_t end = 0, nprops = 0, plen = 0;
        if (ver_ >= 7500) {
            if (!readN(off, 8, &end) || !readN(off + 8, 8, &nprops) || !readN(off + 16, 8, &plen))
                return false;
        } else {
            uint64_t a = 0, b = 0, c = 0;
            if (!readN(off, 4, &a) || !readN(off + 4, 4, &b) || !readN(off + 8, 4, &c)) return false;
            end = a; nprops = b; plen = c;
        }
        if (off + headerSize_ > data_.size()) return false;
        uint8_t nameLen = data_[off + headerSize_ - 1];
        size_t p = off + headerSize_;
        if (p + nameLen > data_.size()) return false;
        std::unique_ptr<FNode> node(new FNode());
        node->name.assign((const char*)data_.data() + p, nameLen);
        p += nameLen;
        if (nprops > 1024 * 1024) return false;
        for (uint64_t k = 0; k < nprops; ++k) {
            FProp prop;
            if (p >= data_.size()) return false;
            char type = (char)data_[p++];
            switch (type) {
                case 'Y': { uint64_t v; if (!readN(p, 2, &v)) return false; p += 2; prop.kind = FProp::I16; prop.i = (int16_t)v; break; }
                case 'C': { if (p >= data_.size()) return false; prop.kind = FProp::Bool; prop.i = data_[p++] ? 1 : 0; break; }
                case 'I': { uint64_t v; if (!readN(p, 4, &v)) return false; p += 4; prop.kind = FProp::I32; prop.i = (int32_t)v; break; }
                case 'F': { uint64_t v; if (!readN(p, 4, &v)) return false; p += 4; prop.kind = FProp::F32; float f; std::memcpy(&f, &v, 4); prop.d = f; break; }
                case 'D': { uint64_t v; if (!readN(p, 8, &v)) return false; p += 8; prop.kind = FProp::F64; double f; std::memcpy(&f, &v, 8); prop.d = f; break; }
                case 'L': { uint64_t v; if (!readN(p, 8, &v)) return false; p += 8; prop.kind = FProp::I64; prop.i = (int64_t)v; break; }
                case 'S': {
                    uint64_t len = 0;
                    if (ver_ >= 7500) { if (!readN(p, 4, &len)) return false; p += 4; }
                    else { if (!readN(p, 4, &len)) return false; p += 4; }
                    if (p + len > data_.size()) return false;
                    prop.kind = FProp::Str;
                    prop.s.assign((const char*)data_.data() + p, (size_t)len);
                    p += (size_t)len;
                    break;
                }
                case 'R': {   // raw bytes（FBX 7.5+ 偶见）
                    uint64_t len = 0;
                    if (!readN(p, 4, &len)) return false;
                    p += 4;
                    if (p + len > data_.size()) return false;
                    prop.kind = FProp::Blob;
                    prop.blob.assign(data_.begin() + (ptrdiff_t)p, data_.begin() + (ptrdiff_t)(p + len));
                    p += (size_t)len;
                    break;
                }
                case 'f': case 'd': case 'i': case 'l': case 'b': {
                    uint64_t count = 0, enc = 0, clen = 0;
                    if (!readN(p, 4, &count) || !readN(p + 4, 4, &enc)) return false;
                    p += 8;
                    size_t elemSize = (type == 'f' || type == 'i') ? 4u : (type == 'b' ? 1u : 8u);
                    // 元素数硬上限：真实 FBX 最大数组也就几十万量级，16M 足以覆盖极端资产，
                    // 同时拦住误解析出来的天文 count 直接喂给 resize() 造成一次性数 GB 分配。
                    if (count > kMaxArrayElems) return false;
                    if (enc == 0) {
                        if (count * elemSize > data_.size() || p + count * elemSize > data_.size()) return false;
                        clen = 0;
                        if (!decodeArray(type, data_.data() + p, (size_t)count, &prop)) return false;
                        p += (size_t)(count * elemSize);
                    } else {
                        if (!readN(p, 4, &clen)) return false;
                        p += 4;
                        if (p + clen > data_.size()) return false;
                        std::vector<unsigned char> raw;
                        if (!ZlibDecompress(data_.data() + p, (size_t)clen, &raw)) return false;
                        if (type == 'b') {
                            prop.kind = FProp::Blob;
                            prop.blob = std::move(raw);
                        } else {
                            // 解压后必须严格等于 count*elemSize：既防越界读，也防 count 失真
                            if (raw.size() != (size_t)count * elemSize) return false;
                            if (!decodeArray(type, raw.data(), (size_t)count, &prop)) return false;
                        }
                        p += (size_t)clen;
                    }
                    break;
                }
                default:
                    return false;   // 未知属性类型 → 该文件无法解析
            }
            node->props.push_back(std::move(prop));
        }
        (void)plen;
        if (end < p || end > data_.size()) {
            // 有些导出器的 end 不含属性长度；以 end 为准但仍校验子列表边界
            if (end == 0 || end > data_.size()) return false;
        }
        // 子节点：从 p 到 end-13（末尾 13 字节 0 终止）
        if (!parseList(p, end, node->children)) return false;
        *endOut = end;
        *out = std::move(node);
        return true;
    }

    // 单个属性的元素数上限（约 128MB @8 字节），见 parseNode 中 'f'/'d'/'i'/'l'/'b' 分支
    static constexpr uint64_t kMaxArrayElems = 16u * 1024u * 1024u;

    bool decodeArray(char type, const unsigned char* src, size_t count, FProp* prop) {
        switch (type) {
            case 'f':
                prop->kind = FProp::ArrF32;
                prop->af.resize(count);
                std::memcpy(prop->af.data(), src, count * 4);
                return true;
            case 'd':
                prop->kind = FProp::ArrF64;
                prop->ad.resize(count);
                std::memcpy(prop->ad.data(), src, count * 8);
                return true;
            case 'i':
                prop->kind = FProp::ArrI32;
                prop->ai32.resize(count);
                std::memcpy(prop->ai32.data(), src, count * 4);
                return true;
            case 'l':
                prop->kind = FProp::ArrI64;
                prop->ai64.resize(count);
                std::memcpy(prop->ai64.data(), src, count * 8);
                return true;
            default:
                return false;
        }
    }

    // 解析兄弟列表：off .. listEnd（不含末尾 13 字节 0）
    bool parseList(size_t off, uint64_t listEnd, std::vector<std::unique_ptr<FNode>>& out) {
        size_t guard = 0;
        while (off + 13 <= listEnd && guard++ < 1000000) {
            // 终止符检测：13 / 25 字节 0
            uint64_t e = 0;
            if (!readN(off, 8, &e)) return true;
            if (e == 0) return true;   // null record
            uint64_t end = 0;
            std::unique_ptr<FNode> node;
            if (!parseNode(off, &end, &node)) return false;
            if (end <= off) return false;   // 防死循环
            out.push_back(std::move(node));
            off = (size_t)end;
        }
        return true;
    }
};

// ===========================================================================
// 矩阵工具（行优先，行向量 × 矩阵；与 model_data.h / glb_loader 一致）
// ===========================================================================
using Mat4 = float[16];

void MatIdentity(float* m) { mat::identity(m); }

void MatMul(const float* a, const float* b, float* out) { mat::multiply(a, b, out); }

// FBX 的 Lcl Rotation 是 XYZ 欧拉角（度），按 R = Rx * Ry * Rz（列向量约定）
// 行向量约定下取转置：R_row = Rz^T * Ry^T * Rx^T
void EulerDegToRowMatrix(double rx, double ry, double rz, float* m) {
    double a = rx * 3.14159265358979323846 / 180.0;
    double b = ry * 3.14159265358979323846 / 180.0;
    double c = rz * 3.14159265358979323846 / 180.0;
    double ca = std::cos(a), sa = std::sin(a);
    double cb = std::cos(b), sb = std::sin(b);
    double cc = std::cos(c), sc = std::sin(c);
    // 列向量约定 R = Rx * Ry * Rz
    double r[3][3];
    r[0][0] = cb * cc;                 r[0][1] = -cb * sc;                r[0][2] = sb;
    r[1][0] = sa * sb * cc + ca * sc;  r[1][1] = -sa * sb * sc + ca * cc; r[1][2] = -sa * cb;
    r[2][0] = -ca * sb * cc + sa * sc; r[2][1] = ca * sb * sc + sa * cc;  r[2][2] = ca * cb;
    for (int row = 0; row < 3; ++row)
        for (int col = 0; col < 3; ++col)
            m[row * 4 + col] = (float)r[col][row];   // 转置
    m[3] = m[7] = m[11] = m[12 + 0] * 0.0f;   // 占位，下一行覆盖
    m[12] = m[13] = m[14] = 0.0f;
    m[15] = 1.0f;
    m[3] = 0;
    m[7] = 0;
    m[11] = 0;
}

// 生成 FBX Model 的局部矩阵：M = T * R * S（行向量：p' = p*S*R*T）
void BuildLclMatrix(double tx, double ty, double tz, double rx, double ry, double rz,
                    double sx, double sy, double sz, float* out) {
    float R[16];
    EulerDegToRowMatrix(rx, ry, rz, R);
    out[0] = R[0] * (float)sx; out[1] = R[1] * (float)sx; out[2] = R[2] * (float)sx; out[3] = 0;
    out[4] = R[4] * (float)sy; out[5] = R[5] * (float)sy; out[6] = R[6] * (float)sy; out[7] = 0;
    out[8] = R[8] * (float)sz; out[9] = R[9] * (float)sz; out[10] = R[10] * (float)sz; out[11] = 0;
    out[12] = (float)tx; out[13] = (float)ty; out[14] = (float)tz; out[15] = 1;
}

// FBX 是 Z 向上、右手系；目标 y 向上：y' = z, z' = -y
void ZUpToYUpPoint(const double in[3], float out[3]) {
    out[0] = (float)in[0];
    out[1] = (float)in[2];
    out[2] = (float)(-in[1]);
}

void ZUpToYUpMatrix(const float* m, float* out) {
    // 变换矩阵：把世界从 Z-up 转到 Y-up，即 M' = S^-1 * M * S，
    // 其中 S = [[1,0,0],[0,0,1],[0,-1,0]]（y'=z, z'=-y）
    static const float S[16] = {1, 0, 0, 0,
                                0, 0, 1, 0,
                                0, -1, 0, 0,
                                0, 0, 0, 1};
    // S 的逆 = S^T：把 Y-up 转回 Z-up 的旋转
    float Sinv[16];
    for (int r = 0; r < 4; ++r)
        for (int c = 0; c < 4; ++c) Sinv[r * 4 + c] = S[c * 4 + r];
    float tmp[16];
    MatMul(Sinv, m, tmp);
    MatMul(tmp, S, out);
}

// 多边形三角化（扇形：quads / n-gon 通用；凹多边形可能出现细微问题但缩略图可接受）
inline void FanTriangulate(const std::vector<int>& poly, std::vector<uint32_t>* out) {
    size_t n = poly.size();
    if (n < 3) return;
    for (size_t k = 1; k + 1 < n; ++k) {
        out->push_back((uint32_t)poly[0]);
        out->push_back((uint32_t)poly[k]);
        out->push_back((uint32_t)poly[k + 1]);
    }
}

inline void AppendWarning(std::string* warn, const std::string& w) {
    if (warn->size() > 600) return;
    if (!warn->empty()) *warn += "; ";
    *warn += w;
}

// "CharacterArmature|Armature|Idle" → "Idle"
inline void NormalizeClipName(std::string* s) {
    size_t pos = s->find_last_of('|');
    if (pos != std::string::npos && pos + 1 < s->size()) *s = s->substr(pos + 1);
    if (s->empty()) *s = "clip";
}

// FBX 对象名形如 "SM_Generic_Ground_01\x00\x01Model"
std::string BaseName(const std::string& raw) {
    size_t z = raw.find('\x00');
    return z == std::string::npos ? raw : raw.substr(0, z);
}

// FBX 对象全名形如 "lambert2\x00\x01Material"
std::string TypeSuffix(const std::string& raw) {
    size_t z = raw.find('\x00');
    if (z == std::string::npos) return std::string();
    return raw.substr(z + 1);
}

// ===========================================================================
// 动画曲线
// ===========================================================================
struct FbxCurve {
    std::vector<double> times;   // 秒
    std::vector<double> values;
    char axis = 'd';             // 'T' / 'R' / 'S'
    int comp = 0;                // x/y/z
    std::string interp;          // LINEAR / CONSTANT / CUBIC
    unsigned flags = 0;
    // 采样
    double sample(double t) const {
        size_t n = times.size();
        if (n == 0) return 0.0;
        if (values.empty()) return 0.0;
        if (n != values.size()) return values.front();
        if (t <= times[0]) return values[0];
        if (t >= times[n - 1]) return values[n - 1];
        size_t lo = 0, hi = n - 1;
        while (hi - lo > 1) {
            size_t mid = (lo + hi) / 2;
            if (times[mid] <= t) lo = mid; else hi = mid;
        }
        if (interp == "CONSTANT") return values[lo];
        double span = times[hi] - times[lo];
        double k = span > 1e-12 ? (t - times[lo]) / span : 0.0;
        return values[lo] + (values[hi] - values[lo]) * k;
    }
};

} // namespace

// ===========================================================================
// 入口
// ===========================================================================
bool LoadFBX(const std::string& utf8Path, ModelData* out) {
    if (!out) return false;
    ModelData& md = *out;
    md = ModelData();
    md.path = utf8Path;

    {
        size_t slash = utf8Path.find_last_of("\\/");
        std::string file = slash == std::string::npos ? utf8Path : utf8Path.substr(slash + 1);
        size_t dot = file.find_last_of('.');
        md.name = dot == std::string::npos ? file : file.substr(0, dot);
        if (slash != std::string::npos) {
            std::string dir = utf8Path.substr(0, slash);
            size_t p2 = dir.find_last_of("\\/");
            md.category = p2 == std::string::npos ? dir : dir.substr(p2 + 1);
        }
    }

    std::vector<unsigned char> bytes;
    if (!img::ReadFileBytesUtf8(utf8Path, &bytes)) {
        md.ok = false;
        md.error = "文件不存在或无法读取";
        return false;
    }
    md.fileSize = bytes.size();
    if (bytes.size() < 32) {
        md.ok = false;
        md.error = "文件太小";
        return false;
    }
    // ASCII FBX
    if (std::memcmp(bytes.data(), "Kaydara FBX Binary", 18) != 0) {
        md.ok = false;
        md.error = "不是 FBX 二进制文件（可能是 ASCII FBX）";
        return false;
    }

    std::unique_ptr<FNode> root;
    FbxReader reader(bytes);
    uint32_t version = 0;
    if (!reader.parse(&root, &version)) {
        md.ok = false;
        md.error = "FBX 二进制解析失败";
        return false;
    }

    const FNode* objects = root->child("Objects");
    const FNode* connections = root->child("Connections");
    const FNode* global = root->child("GlobalSettings");

    // ---- 单位 / 轴 ----
    double unitScale = 1.0;
    double upAxis = 1.0;
    int frontAxisSign = 1;
    if (global) {
        if (const FProp* p = global->findP("UnitScaleFactor")) unitScale = p->num();
        if (const FProp* p = global->findP("UpAxis")) upAxis = p->num();
        if (const FProp* p = global->findP("FrontAxisSign")) frontAxisSign = (int)p->num();
    }
    if (unitScale <= 0.0) unitScale = 1.0;

    // ---- 收集对象 ----
    struct FbxModel {
        int64_t uid = 0;
        std::string name;
        std::string type;   // Mesh / Null / LimbNode / Camera ...
        double tx = 0, ty = 0, tz = 0;
        double rx = 0, ry = 0, rz = 0;
        double sx = 1, sy = 1, sz = 1;
        float lcl[16];
        float world[16];
        int64_t parentUid = 0;
        bool hasParent = false;
    };
    struct FbxGeom {
        int64_t uid = 0;
        std::string name;
        std::vector<double> verts;
        std::vector<int32_t> polyIdx;
        std::vector<float> normals;
        std::vector<float> normalsIdxMapping;   // 可能不存在
        std::vector<double> uv;
        std::vector<int32_t> uvIdx;
        std::vector<int32_t> matIdx;
        bool hasNormals = false;
        bool hasNormalsByPoly = false;
        bool hasUV = false;
        bool hasMatIdx = false;
        std::string normalLayer;   // ByPolygonVertex / ByVertex
        std::string uvLayer;
        // 顶点色：Synty POLYGON 的 FBX 同样靠顶点色上色（LayerElementColor）。
        std::vector<double> vcolors;          // RGBA 平铺
        std::vector<int32_t> vcolorIdx;      // 可选的 IndexToDirect 映射
        bool hasVColor = false;
        std::string vcolorLayer;
    };
    struct FbxMaterial {
        int64_t uid = 0;
        std::string name;
        float diffuse[4] = {0.8f, 0.8f, 0.8f, 1.0f};
        float specular = 0.0f;
        float shininess = 0.0f;
        double opacity = 1.0;
        std::string texName;
        int64_t texUid = 0;
    };

    std::map<int64_t, FbxModel> modelsById;
    std::vector<int64_t> modelOrder;
    std::map<int64_t, FbxGeom> geomsById;
    std::map<int64_t, FbxMaterial> matsById;
    std::vector<int64_t> matOrder;
    // 纹理/视频
    std::map<int64_t, std::string> textureFileById;   // Texture uid → RelativeFilename/Filename
    std::map<int64_t, std::string> videoFileById;

    if (objects) {
        for (const auto& obj : objects->children) {
            if (obj->props.empty()) continue;
            int64_t uid = obj->prop(0)->kind == FProp::I64 ? obj->prop(0)->i
                                                            : (int64_t)(obj->prop(0)->kind == FProp::I32 ? obj->prop(0)->i : 0);
            std::string rawName = obj->propStr(1);
            std::string suffix = obj->propStr(2);

            if (obj->is("Model")) {
                FbxModel m;
                m.uid = uid;
                m.name = BaseName(rawName);
                m.type = suffix;
                if (const FProp* p = obj->findP("Lcl Translation")) {
                    if (p->kind == FProp::ArrF64 && p->ad.size() >= 3) { m.tx = p->ad[0]; m.ty = p->ad[1]; m.tz = p->ad[2]; }
                    else if (p->kind == FProp::ArrF32 && p->af.size() >= 3) { m.tx = p->af[0]; m.ty = p->af[1]; m.tz = p->af[2]; }
                }
                if (const FProp* p = obj->findP("Lcl Rotation")) {
                    if (p->kind == FProp::ArrF64 && p->ad.size() >= 3) { m.rx = p->ad[0]; m.ry = p->ad[1]; m.rz = p->ad[2]; }
                    else if (p->kind == FProp::ArrF32 && p->af.size() >= 3) { m.rx = p->af[0]; m.ry = p->af[1]; m.rz = p->af[2]; }
                }
                if (const FProp* p = obj->findP("Lcl Scaling")) {
                    if (p->kind == FProp::ArrF64 && p->ad.size() >= 3) { m.sx = p->ad[0]; m.sy = p->ad[1]; m.sz = p->ad[2]; }
                    else if (p->kind == FProp::ArrF32 && p->af.size() >= 3) { m.sx = p->af[0]; m.sy = p->af[1]; m.sz = p->af[2]; }
                }
                BuildLclMatrix(m.tx, m.ty, m.tz, m.rx, m.ry, m.rz, m.sx, m.sy, m.sz, m.lcl);
                modelsById[uid] = std::move(m);
                modelOrder.push_back(uid);
            } else if (obj->is("Geometry")) {
                FbxGeom g;
                g.uid = uid;
                g.name = BaseName(rawName);
                std::string gtype = suffix;
                if (gtype != "Mesh" && !gtype.empty()) continue;   // 跳过 Shape/Deformer 等
                if (const FNode* v = obj->child("Vertices")) {
                    if (!v->props.empty() && v->props[0].kind == FProp::ArrF64) {
                        g.verts = v->props[0].ad;
                    } else if (!v->props.empty() && v->props[0].kind == FProp::ArrF32) {
                        g.verts.assign(v->props[0].af.begin(), v->props[0].af.end());
                    }
                }
                if (const FNode* v = obj->child("PolygonVertexIndex")) {
                    if (!v->props.empty() && v->props[0].kind == FProp::ArrI32) g.polyIdx = v->props[0].ai32;
                }
                if (const FNode* ln = obj->child("LayerElementNormal")) {
                    if (const FNode* v = ln->child("Normals")) {
                        if (!v->props.empty() && v->props[0].kind == FProp::ArrF64) {
                            g.normals.assign(v->props[0].ad.begin(), v->props[0].ad.end());
                        } else if (!v->props.empty() && v->props[0].kind == FProp::ArrF32) {
                            g.normals = v->props[0].af;
                        }
                        g.hasNormals = !g.normals.empty();
                    }
                    if (const FNode* mp = ln->child("NormalsIndex")) {
                        if (!mp->props.empty() && mp->props[0].kind == FProp::ArrI32) {
                            g.normalsIdxMapping.assign(mp->props[0].ai32.begin(), mp->props[0].ai32.end());
                            g.hasNormalsByPoly = true;
                        }
                    }
                    if (const FNode* mt = ln->child("MappingInformationType")) {
                        g.normalLayer = mt->propStr(0);
                    }
                }
                if (const FNode* lu = obj->child("LayerElementUV")) {
                    if (const FNode* v = lu->child("UV")) {
                        if (!v->props.empty() && v->props[0].kind == FProp::ArrF64) g.uv = v->props[0].ad;
                        else if (!v->props.empty() && v->props[0].kind == FProp::ArrF32) g.uv.assign(v->props[0].af.begin(), v->props[0].af.end());
                        g.hasUV = !g.uv.empty();
                    }
                    if (const FNode* mp = lu->child("UVIndex")) {
                        if (!mp->props.empty() && mp->props[0].kind == FProp::ArrI32) g.uvIdx = mp->props[0].ai32;
                    }
                    if (const FNode* mt = lu->child("MappingInformationType")) g.uvLayer = mt->propStr(0);
                }
                if (const FNode* lm = obj->child("LayerElementMaterial")) {
                    if (const FNode* v = lm->child("Materials")) {
                        if (!v->props.empty() && v->props[0].kind == FProp::ArrI32) {
                            g.matIdx = v->props[0].ai32;
                            g.hasMatIdx = !g.matIdx.empty();
                        }
                    }
                }
                // 顶点色：与 UV 完全同构（Colors / ColorIndex / MappingInformationType）
                if (const FNode* lc = obj->child("LayerElementColor")) {
                    if (const FNode* v = lc->child("Colors")) {
                        if (!v->props.empty() && v->props[0].kind == FProp::ArrF64) g.vcolors = v->props[0].ad;
                        else if (!v->props.empty() && v->props[0].kind == FProp::ArrF32)
                            g.vcolors.assign(v->props[0].af.begin(), v->props[0].af.end());
                        g.hasVColor = g.vcolors.size() >= 3;
                    }
                    if (const FNode* mp = lc->child("ColorIndex")) {
                        if (!mp->props.empty() && mp->props[0].kind == FProp::ArrI32) g.vcolorIdx = mp->props[0].ai32;
                    }
                    if (const FNode* mt = lc->child("MappingInformationType")) g.vcolorLayer = mt->propStr(0);
                }
                geomsById[uid] = std::move(g);
            } else if (obj->is("Material")) {
                FbxMaterial m;
                m.uid = uid;
                m.name = BaseName(rawName);
                if (const FProp* p = obj->findP("DiffuseColor")) {
                    if (p->kind == FProp::ArrF64 && p->ad.size() >= 3) {
                        m.diffuse[0] = (float)p->ad[0]; m.diffuse[1] = (float)p->ad[1]; m.diffuse[2] = (float)p->ad[2];
                    } else if (p->kind == FProp::ArrF32 && p->af.size() >= 3) {
                        m.diffuse[0] = p->af[0]; m.diffuse[1] = p->af[1]; m.diffuse[2] = p->af[2];
                    }
                }
                if (const FProp* p = obj->findP("Diffuse")) {
                    if (p->kind == FProp::ArrF64 && p->ad.size() >= 3) {
                        // Diffuse 是缩放因子（Synty 用 [0.8,0.8,0.8]）：仅当 DiffuseColor 为白时用于调色
                        if (m.diffuse[0] > 0.99f && m.diffuse[1] > 0.99f && m.diffuse[2] > 0.99f) {
                            m.diffuse[0] = (float)p->ad[0]; m.diffuse[1] = (float)p->ad[1]; m.diffuse[2] = (float)p->ad[2];
                        }
                    }
                }
                if (const FProp* p = obj->findP("Opacity")) m.opacity = p->num();
                if (const FProp* p = obj->findP("TransparencyFactor")) {
                    double tf = p->num();
                    // Synty 的 TransparencyFactor=1 但 Material 是 Opaque；仅当 <1 时视为不透明因子
                    if (tf > 0.0 && tf < 1.0) m.opacity *= (1.0 - tf);
                }
                if (const FProp* p = obj->findP("Shininess")) m.shininess = (float)p->num();
                m.diffuse[3] = (float)std::max(0.0, std::min(1.0, m.opacity));
                matsById[uid] = std::move(m);
                matOrder.push_back(uid);
            } else if (obj->is("Texture")) {
                std::string file;
                if (const FNode* fn = obj->child("RelativeFilename")) file = BaseName(fn->propStr(0));
                if (file.empty()) {
                    if (const FNode* fn = obj->child("FileName")) file = BaseName(fn->propStr(0));
                }
                textureFileById[uid] = file;
            } else if (obj->is("Video")) {
                std::string file;
                if (const FNode* fn = obj->child("RelativeFilename")) file = BaseName(fn->propStr(0));
                if (file.empty()) {
                    if (const FNode* fn = obj->child("FileName")) file = BaseName(fn->propStr(0));
                }
                videoFileById[uid] = file;
            }
        }
    }

    // ---- Connections：父子 & 材质挂接 ----
    // C: "OO" child parent | C: "OP" child parent "PropertyName"
    struct MatLink { int64_t geomUid; int64_t matUid; };
    std::vector<MatLink> matLinks;
    std::map<int64_t, int> texOfMaterial;   // Material uid → Texture uid
    std::map<int64_t, int64_t> videoOfTexture;
    if (connections) {
        for (const auto& c : connections->children) {
            if (!c->is("C") || c->props.size() < 3) continue;
            std::string kind = c->props[0].s;
            int64_t child = c->prop(1)->i;
            int64_t parent = c->prop(2)->i;
            std::string prop = c->props.size() >= 4 ? c->props[3].s : std::string();
            if (kind == "OP") {
                if (prop == "DiffuseColor" || prop == "Diffuse") {
                    if (matsById.count(parent) && textureFileById.count(child)) texOfMaterial[parent] = child;
                }
            } else if (kind == "OO") {
                // Model → Model 父链
                auto mi = modelsById.find(child);
                if (mi != modelsById.end() && modelsById.count(parent)) {
                    mi->second.parentUid = parent;
                    mi->second.hasParent = true;
                } else if (geomsById.count(child) && modelsById.count(parent)) {
                    // Geometry 挂到 Model 上：记录在 Model 侧
                    // 复用 parentUid 不行，单独记录
                } else if (matsById.count(child) && geomsById.count(parent)) {
                    matLinks.push_back({parent, child});
                } else if (textureFileById.count(child) && videoFileById.count(parent)) {
                    videoOfTexture[child] = parent;
                }
            }
        }
    }
    // Geometry → Model
    std::map<int64_t, int64_t> geomOfModel;
    if (connections) {
        for (const auto& c : connections->children) {
            if (!c->is("C") || c->props.size() < 3) continue;
            std::string kind = c->props[0].s;
            if (kind != "OO") continue;
            int64_t child = c->prop(1)->i;
            int64_t parent = c->prop(2)->i;
            if (geomsById.count(child) && modelsById.count(parent)) geomOfModel[parent] = child;
        }
    }

    // ---- 世界矩阵（Model 层级）----
    {
        std::map<int64_t, char> state;   // 0 未访问 1 计算中 2 完成
        std::function<void(int64_t)> compute = [&](int64_t uid) {
            auto it = modelsById.find(uid);
            if (it == modelsById.end()) return;
            FbxModel& m = it->second;
            if (state[uid] == 2) return;
            if (state[uid] == 1) {
                // 检测到环，直接使用局部矩阵
                std::memcpy(m.world, m.lcl, sizeof(m.lcl));
                state[uid] = 2;
                return;
            }
            state[uid] = 1;
            if (m.hasParent && modelsById.count(m.parentUid)) {
                compute(m.parentUid);
                FbxModel& p = modelsById[m.parentUid];
                MatMul(m.lcl, p.world, m.world);
            } else {
                std::memcpy(m.world, m.lcl, sizeof(m.lcl));
            }
            state[uid] = 2;
        };
        for (int64_t uid : modelOrder) compute(uid);
    }

    // ---- 材质 ----
    // 每个 Model 几何可能引用多个材质 → 子网格按材质拆分

    // 先建材质表（保持 matOrder 顺序）
    std::map<int64_t, int> matToIndex;
    for (int64_t mu : matOrder) {
        FbxMaterial& fm = matsById[mu];
        Material mat;
        mat.name = fm.name;
        // 贴图：FBX 侧无配套贴图（文件名指向不存在的 PSD），这里只保留 diffuse 色
        mat.diffuseTex = -1;
        mat.baseColor[0] = fm.diffuse[0];
        mat.baseColor[1] = fm.diffuse[1];
        mat.baseColor[2] = fm.diffuse[2];
        mat.baseColor[3] = fm.diffuse[3];
        mat.doubleSided = true;   // FBX 默认 CullingOff 时双面；缩略图统一双面更稳妥
        mat.metallic = 0.0f;
        mat.roughness = fm.shininess > 20.0f ? 0.35f : 0.8f;
        matToIndex[mu] = (int)md.mesh.materials.size();
        md.mesh.materials.push_back(std::move(mat));
    }
    if (md.mesh.materials.empty()) {
        // 无材质：生成一个默认材质，保证有东西可画
        Material mat;
        mat.name = "default";
        md.mesh.materials.push_back(mat);
    }

    // ---- 几何 → 顶点/索引 ----
    const size_t kMaxVertices = 2u * 1024 * 1024;
    const size_t kMaxIndices = 6u * 1024 * 1024;
    bool truncated = false;

    for (int64_t muid : modelOrder) {
        FbxModel& m = modelsById[muid];
        auto git = geomOfModel.find(muid);
        if (git == geomOfModel.end()) continue;
        auto gi = geomsById.find(git->second);
        if (gi == geomsById.end()) continue;
        FbxGeom& g = gi->second;
        if (g.verts.size() < 9) continue;

        size_t vertCount = g.verts.size() / 3;
        if (truncated) break;

        // ---- 多边形三角化 ----
        // PolygonVertexIndex：每个多边形最后一个顶点的索引是 ~(idx)（按位取反表示结束）
        std::vector<uint32_t> tris;
        std::vector<uint32_t> triPoly;      // 每个三角形 → 多边形序号（材质分组用）
        std::vector<int32_t> polyCorner;    // 每个三角形角点 → polygon corner 序号（法线/UV 取值）
        tris.reserve(g.polyIdx.size() * 2);
        polyCorner.reserve(g.polyIdx.size() * 2);
        {
            std::vector<int> poly;
            std::vector<int32_t> polyCornerNo;   // 本多边形内各顶点的全局 corner 序号
            poly.reserve(64);
            polyCornerNo.reserve(64);
            uint32_t polyNo = 0;
            int32_t corner = 0;   // 全局（文件内）多边形顶点序号，与 PolygonVertexIndex 顺序一致
            auto emit = [&]() {
                if (poly.size() >= 3) {
                    for (size_t t = 1; t + 1 < poly.size(); ++t) {
                        tris.push_back((uint32_t)poly[0]);
                        tris.push_back((uint32_t)poly[t]);
                        tris.push_back((uint32_t)poly[t + 1]);
                        triPoly.push_back(polyNo);
                        polyCorner.push_back(polyCornerNo[0]);
                        polyCorner.push_back(polyCornerNo[t]);
                        polyCorner.push_back(polyCornerNo[t + 1]);
                    }
                }
                poly.clear();
                polyCornerNo.clear();
            };
            for (size_t k = 0; k < g.polyIdx.size(); ++k) {
                int32_t v = g.polyIdx[k];
                int32_t curCorner = corner++;
                if (v < 0) {
                    int idx = ~v;
                    if (idx >= 0 && (size_t)idx < vertCount) {
                        poly.push_back(idx);
                        polyCornerNo.push_back(curCorner);
                    }
                    emit();
                    ++polyNo;
                } else {
                    if ((size_t)v < vertCount) {
                        poly.push_back(v);
                        polyCornerNo.push_back(curCorner);
                    }
                }
            }
            emit();   // 容错：末尾未闭合的多边形
        }
        if (tris.empty()) continue;

        // ---- 按材质拆子网格 ----
        // matIdx（LayerElementMaterial）：AllSame → 单个；ByPolygon → 每个多边形一个索引
        int matCount = (int)md.mesh.materials.size();
        std::vector<int> triMat(tris.size() / 3, -1);
        {
            // 找出该几何连接的材质 uid
            std::vector<int64_t> linkMats;
            for (const auto& ml : matLinks)
                if (ml.geomUid == g.uid) linkMats.push_back(ml.matUid);
            int baseMatIndex = -1;
            if (!linkMats.empty() && matToIndex.count(linkMats[0])) baseMatIndex = matToIndex[linkMats[0]];
            else if (!linkMats.empty()) baseMatIndex = 0;
            if (g.hasMatIdx && g.matIdx.size() >= 1) {
                if (g.matIdx.size() == 1) {
                    // AllSame
                    int k = g.matIdx[0];
                    if (k >= 0 && (size_t)k < linkMats.size() && matToIndex.count(linkMats[(size_t)k]))
                        baseMatIndex = matToIndex[linkMats[(size_t)k]];
                    for (auto& tm : triMat) tm = baseMatIndex;
                } else {
                    // ByPolygon：matIdx[polyNo] → linkMats 下标
                    for (size_t t = 0; t < triMat.size(); ++t) {
                        uint32_t pn = triPoly[t];
                        int k = (pn < g.matIdx.size()) ? g.matIdx[pn] : 0;
                        int mm = baseMatIndex;
                        if (k >= 0 && (size_t)k < linkMats.size() && matToIndex.count(linkMats[(size_t)k]))
                            mm = matToIndex[linkMats[(size_t)k]];
                        triMat[t] = mm;
                    }
                }
            } else {
                for (auto& tm : triMat) tm = baseMatIndex;
            }
        }

        // ---- 顶点烘焙 ----
        float worldY[16];
        ZUpToYUpMatrix(m.world, worldY);
        uint32_t vbase = (uint32_t)md.mesh.vertices.size();
        if (vbase + vertCount > kMaxVertices) {
            truncated = true;
            break;
        }
        md.mesh.vertices.reserve(md.mesh.vertices.size() + vertCount);
        for (size_t k = 0; k < vertCount; ++k) {
            double p[3] = {g.verts[k * 3 + 0] * unitScale, g.verts[k * 3 + 1] * unitScale,
                           g.verts[k * 3 + 2] * unitScale};
            float pz[3];
            ZUpToYUpPoint(p, pz);
            Vertex v;
            float out3[3];
            // 世界变换（行向量约定）
            out3[0] = pz[0] * worldY[0] + pz[1] * worldY[4] + pz[2] * worldY[8] + worldY[12];
            out3[1] = pz[0] * worldY[1] + pz[1] * worldY[5] + pz[2] * worldY[9] + worldY[13];
            out3[2] = pz[0] * worldY[2] + pz[1] * worldY[6] + pz[2] * worldY[10] + worldY[14];
            v.px = out3[0];
            v.py = out3[1];
            v.pz = out3[2];
            // 法线（FBX 的 Normal 有独立索引，ByPolygonVertex）
            v.nx = v.ny = v.nz = 0.0f;
            v.u = v.v = 0.0f;
            // 顶点色：FBX 的 LayerElementColor 走 UV 同款的 corner 映射，
            // 在下面「按 corner 展开顶点」时填充；这里先置白色（乘法单位元）。
            v.r = v.g = v.b = v.a = 1.0f;
            md.mesh.vertices.push_back(v);
        }

        // 法线/UV 按「多边形顶点序号(corner)」取值，与顶点索引不是一一对应
        // （一个顶点可属于多个 corner，UV/法线接缝处必须拆顶点）。
        // 因此这里按 (顶点索引, corner) 唯一化展开顶点。
        {
            std::vector<uint32_t> outIndices;
            outIndices.reserve(tris.size());

            const bool hasCornerNormal = g.hasNormals && (g.normalLayer == "ByPolygonVertex");
            const bool hasCornerUV = g.hasUV && (g.uvLayer == "ByPolygonVertex" || !g.uvIdx.empty());

            uint64_t keyBase = (uint64_t)vertCount + 1;
            std::vector<uint32_t> splitIndex(vbase + vertCount, 0xFFFFFFFFu);   // 已展开顶点索引（按全局）
            auto cornerKey = [&](uint32_t vidx, int32_t corner) -> uint64_t {
                return (uint64_t)vidx * keyBase + (uint64_t)(uint32_t)corner;
            };
            std::unordered_map<uint64_t, uint32_t> splitMap;

            auto normalAt = [&](int32_t corner, uint32_t vidx, float out3n[3]) -> bool {
                if (!g.hasNormals) return false;
                int32_t ni = -1;
                if (g.normalLayer == "ByPolygonVertex" && (size_t)corner * 3 + 2 < g.normals.size()) {
                    ni = corner;
                } else if (g.hasNormalsByPoly && (size_t)corner < g.normalsIdxMapping.size()) {
                    int32_t m = g.normalsIdxMapping[(size_t)corner];
                    if (m >= 0 && (size_t)m * 3 + 2 < g.normals.size()) ni = m;
                } else if ((size_t)vidx * 3 + 2 < g.normals.size()) {
                    ni = (int32_t)vidx;
                }
                if (ni < 0) return false;
                size_t base = (size_t)ni * 3;
                if (base + 2 >= g.normals.size()) return false;
                double d[3] = {g.normals[base], g.normals[base + 1], g.normals[base + 2]};
                float dz[3];
                ZUpToYUpPoint(d, dz);
                out3n[0] = dz[0] * worldY[0] + dz[1] * worldY[4] + dz[2] * worldY[8];
                out3n[1] = dz[0] * worldY[1] + dz[1] * worldY[5] + dz[2] * worldY[9];
                out3n[2] = dz[0] * worldY[2] + dz[1] * worldY[6] + dz[2] * worldY[10];
                float len = std::sqrt(out3n[0] * out3n[0] + out3n[1] * out3n[1] + out3n[2] * out3n[2]);
                if (len > 1e-12f) { out3n[0] /= len; out3n[1] /= len; out3n[2] /= len; }
                return true;
            };
            auto uvAt = [&](int32_t corner, uint32_t vidx, float* u, float* v) -> bool {
                if (!g.hasUV || g.uv.size() < 2) return false;
                int32_t ui = -1;
                if (!g.uvIdx.empty() && corner >= 0 && (size_t)corner < g.uvIdx.size()) {
                    int32_t t = g.uvIdx[(size_t)corner];
                    if (t >= 0) ui = t;
                } else if (g.uvLayer == "ByPolygonVertex" && corner >= 0 &&
                           (size_t)corner * 2 + 1 < g.uv.size()) {
                    ui = corner;
                } else if ((size_t)vidx * 2 + 1 < g.uv.size()) {
                    ui = (int32_t)vidx;
                }
                if (ui < 0 || (size_t)ui * 2 + 1 >= g.uv.size()) return false;
                // FBX 的 UV 是左下原点、v 向上；与 glTF 相同 → 直接使用
                *u = (float)g.uv[(size_t)ui * 2];
                *v = (float)g.uv[(size_t)ui * 2 + 1];
                return true;
            };
            // 顶点色：与 uvAt 同构，但每条是 RGBA 四分量
            auto colorAt = [&](int32_t corner, uint32_t vidx, float* out4) -> bool {
                if (!g.hasVColor || g.vcolors.size() < 3) return false;
                int32_t ci = -1;
                if (!g.vcolorIdx.empty() && corner >= 0 && (size_t)corner < g.vcolorIdx.size()) {
                    int32_t t = g.vcolorIdx[(size_t)corner];
                    if (t >= 0) ci = t;
                } else if (g.vcolorLayer == "ByPolygonVertex" && corner >= 0 &&
                           (size_t)corner * 4 + 2 < g.vcolors.size()) {
                    ci = corner;
                } else if ((size_t)vidx * 4 + 2 < g.vcolors.size()) {
                    ci = (int32_t)vidx;
                }
                if (ci < 0 || (size_t)ci * 4 + 2 >= g.vcolors.size()) return false;
                out4[0] = (float)g.vcolors[(size_t)ci * 4 + 0];
                out4[1] = (float)g.vcolors[(size_t)ci * 4 + 1];
                out4[2] = (float)g.vcolors[(size_t)ci * 4 + 2];
                out4[3] = ((size_t)ci * 4 + 3 < g.vcolors.size())
                              ? (float)g.vcolors[(size_t)ci * 4 + 3] : 1.0f;
                return true;
            };

            for (size_t t = 0; t < tris.size(); ++t) {
                uint32_t vidx = tris[t];
                int32_t corner = polyCorner[t];
                uint32_t globalV = vbase + vidx;

                float nn[3];
                bool okN = normalAt(corner, vidx, nn);
                float uu = 0.0f, vv = 0.0f;
                bool okU = uvAt(corner, vidx, &uu, &vv);
                float cc[4] = {1.0f, 1.0f, 1.0f, 1.0f};
                bool okC = colorAt(corner, vidx, cc);

                if (okN || okU || okC) {
                    uint64_t key = cornerKey(vidx, corner);
                    auto it = splitMap.find(key);
                    if (it == splitMap.end()) {
                        if (md.mesh.vertices.size() >= kMaxVertices) {
                            truncated = true;
                            break;
                        }
                        Vertex nv = md.mesh.vertices[globalV];
                        if (okN) { nv.nx = nn[0]; nv.ny = nn[1]; nv.nz = nn[2]; }
                        if (okU) { nv.u = uu; nv.v = vv; }
                        if (okC) { nv.r = cc[0]; nv.g = cc[1]; nv.b = cc[2]; nv.a = cc[3]; }
                        uint32_t idx = (uint32_t)md.mesh.vertices.size();
                        md.mesh.vertices.push_back(nv);
                        it = splitMap.emplace(key, idx).first;
                    }
                    outIndices.push_back(it->second);
                } else {
                    outIndices.push_back(globalV);
                }
            }
            if (truncated) break;
            (void)splitIndex;
            (void)hasCornerNormal;
            (void)hasCornerUV;

            // 按材质拆分索引
            size_t tri = 0;
            for (size_t t = 0; t < triMat.size(); ++t) {
                // 分组比较必须用「归一化后的材质疑」两侧同口径比较。
                // 若 triMat[t] == -1（未挂材质）而 mm 被归一成 0，旧写法里 while 一次都不前进，
                // t 被回退到 t-1 再由 ++t 拉回 t → 死循环，进程内存涨到数 GB 后被系统杀掉。
                auto normMat = [&](int k) -> int {
                    if (k < 0 || k >= matCount) return (matCount > 0 ? 0 : -1);
                    return k;
                };
                int mm = normMat(triMat[t]);
                size_t startTri = t;
                size_t endTri = t + 1;               // 至少推进 1 格，保证 for 一定前进
                while (endTri < triMat.size() && normMat(triMat[endTri]) == mm) ++endTri;
                t = endTri - 1;
                SubMesh sm;
                sm.materialIndex = mm;
                sm.indexOffset = (uint32_t)md.mesh.indices.size();
                sm.indexCount = (uint32_t)((endTri - startTri) * 3);
                sm.name = m.name;
                if (md.mesh.indices.size() + sm.indexCount > kMaxIndices) {
                    truncated = true;
                    break;
                }
                for (size_t k = startTri * 3; k < endTri * 3; ++k) md.mesh.indices.push_back(outIndices[k]);
                md.mesh.subMeshes.push_back(std::move(sm));
            }
            if (truncated) break;
            (void)tri;
        }
    }
    if (truncated) AppendWarning(&md.warning, "网格超出上限已截断");

    // ---- 动画（Take / AnimationCurveNode）----
    // FBX 动画：Model(uid) ← AnimationCurveNode(uid) ← AnimationCurve(uid)
    // 仅当存在 Model 动画时才生成 clip；缩略图取绑定姿态。
    {
        std::map<int64_t, std::map<char, std::map<int, FbxCurve>>> modelCurves;
        std::map<int64_t, std::string> curveNodeProp;   // AnimationCurveNode uid → 'T'/'R'/'S'
        if (objects || connections) {
            // AnimCurveNode → 属性名
            if (objects) {
                for (const auto& obj : objects->children) {
                    if (!obj->is("AnimationCurveNode") || obj->props.empty()) continue;
                    int64_t uid = obj->prop(0)->i;
                    std::string nm = BaseName(obj->propStr(1));
                    char axis = 0;
                    if (nm.find('T') != std::string::npos) axis = 'T';
                    else if (nm.find('R') != std::string::npos) axis = 'R';
                    else if (nm.find('S') != std::string::npos) axis = 'S';
                    if (const FProp* d = obj->findP("d|X")) { (void)d; }
                    // 用默认值 d|X 推断：T 的默认 0，R 默认 0，S 默认 1
                    if (!axis) {
                        const FProp* dx = obj->findP("d|X");
                        if (dx && dx->isNum() && std::fabs(dx->num() - 1.0) < 1e-9) axis = 'S';
                        else axis = 'T';
                    }
                    curveNodeProp[uid] = std::string(1, axis);
                }
            }
            if (connections) {
                std::map<int64_t, int> curveAxis;   // AnimationCurve uid → 0/1/2
                std::map<int64_t, int64_t> curveOwner;   // AnimationCurve uid → CurveNode uid
                for (const auto& c : connections->children) {
                    if (!c->is("C") || c->props.size() < 4) continue;
                    std::string kind = c->props[0].s;
                    if (kind != "OP") continue;
                    int64_t child = c->prop(1)->i;
                    int64_t parent = c->prop(2)->i;
                    std::string prop = c->props[3].s;
                    if (prop == "d|X") curveAxis[child] = 0;
                    else if (prop == "d|Y") curveAxis[child] = 1;
                    else if (prop == "d|Z") curveAxis[child] = 2;
                    else continue;
                    curveOwner[child] = parent;
                }
                // CurveNode ← Model
                std::map<int64_t, int64_t> nodeOfModel;
                for (const auto& c : connections->children) {
                    if (!c->is("C") || c->props.size() < 3) continue;
                    std::string kind = c->props[0].s;
                    if (kind != "OO") continue;
                    int64_t child = c->prop(1)->i;
                    int64_t parent = c->prop(2)->i;
                    if (curveNodeProp.count(child) && modelsById.count(parent)) nodeOfModel[child] = parent;
                }
                // AnimationCurve 数据
                std::map<int64_t, FbxCurve> curves;
                if (objects) {
                    for (const auto& obj : objects->children) {
                        if (!obj->is("AnimationCurve") || obj->props.empty()) continue;
                        int64_t uid = obj->prop(0)->i;
                        FbxCurve cv;
                        if (const FNode* kt = obj->child("KeyTime")) {
                            if (!kt->props.empty() && kt->props[0].kind == FProp::ArrI64) {
                                for (int64_t kv : kt->props[0].ai64) cv.times.push_back((double)kv / 46186158000.0);   // FBX 时间单位
                            }
                        }
                        if (const FNode* kfv = obj->child("KeyValueFloat")) {
                            if (!kfv->props.empty() && kfv->props[0].kind == FProp::ArrF32) {
                                cv.values.assign(kfv->props[0].af.begin(), kfv->props[0].af.end());
                            } else if (!kfv->props.empty() && kfv->props[0].kind == FProp::ArrF64) {
                                cv.values.assign(kfv->props[0].ad.begin(), kfv->props[0].ad.end());
                            }
                        }
                        if (const FNode* ia = obj->child("KeyAttrFlags")) {
                            if (!ia->props.empty() && ia->props[0].kind == FProp::ArrI32 && !ia->props[0].ai32.empty())
                                cv.flags = (unsigned)ia->props[0].ai32[0];
                        }
                        // 插值：KeyAttrFlags 位 0x2 = CONSTANT, 0x4 = LINEAR, 0x8 = CUBIC
                        if (cv.flags & 0x2) cv.interp = "CONSTANT";
                        else cv.interp = "LINEAR";
                        if (!cv.times.empty() && cv.times.size() == cv.values.size()) curves[uid] = std::move(cv);
                    }
                }
                for (const auto& kv : curveOwner) {
                    int64_t curveUid = kv.first;
                    int64_t nodeUid = kv.second;
                    auto cit = curves.find(curveUid);
                    if (cit == curves.end()) continue;
                    auto nit = nodeOfModel.find(nodeUid);
                    if (nit == nodeOfModel.end()) continue;
                    char axisCh = curveNodeProp[nodeUid][0];
                    int comp = curveAxis[curveUid];
                    FbxCurve cv = cit->second;
                    cv.axis = axisCh;
                    cv.comp = comp;
                    modelCurves[nit->second][axisCh][comp] = std::move(cv);
                }
            }
        }

        // 合成 clip（每个 Model 的 TRS 通道）
        if (!modelCurves.empty()) {
            // 建立 node→bone 映射（FBX 里没有 skin，骨骼取 Model 层级全部节点）
            std::map<int64_t, int> uidToBone;
            for (int64_t muid : modelOrder) {
                auto it = modelCurves.find(muid);
                if (it == modelCurves.end()) continue;
                Bone b;
                b.name = modelsById[muid].name;
                int parentBone = -1;
                if (modelsById[muid].hasParent) {
                    auto pit = uidToBone.find(modelsById[muid].parentUid);
                    if (pit != uidToBone.end()) parentBone = pit->second;
                }
                b.parent = parentBone;
                std::memcpy(b.local, modelsById[muid].lcl, sizeof(float) * 16);
                MatIdentity(b.bind);
                uidToBone[muid] = (int)md.bones.size();
                md.bones.push_back(std::move(b));
            }
            if (!md.bones.empty()) {
                double dur = 0.0;
                for (const auto& kv : modelCurves)
                    for (const auto& ax : kv.second)
                        for (const auto& cp : ax.second)
                            if (!cp.second.times.empty()) dur = std::max(dur, cp.second.times.back());
                Clip clip;
                clip.name = "Take 001";
                if (const FNode* takes = root->child("Takes")) {
                    if (const FNode* take = takes->child("Take")) clip.name = BaseName(take->propStr(0));
                }
                NormalizeClipName(&clip.name);
                clip.duration = dur;
                clip.tracks.resize(md.bones.size());
                const size_t kMaxKeys = 256;
                for (const auto& kv : modelCurves) {
                    int bi = uidToBone[kv.first];
                    Bone& bone = md.bones[(size_t)bi];
                    // 收集时间
                    std::vector<double> times;
                    for (const auto& ax : kv.second)
                        for (const auto& cp : ax.second)
                            for (double t : cp.second.times) times.push_back(t);
                    std::sort(times.begin(), times.end());
                    times.erase(std::unique(times.begin(), times.end(),
                                            [](double a, double b) { return std::fabs(a - b) < 1e-7; }),
                                times.end());
                    if (times.empty()) continue;
                    if (times.size() > kMaxKeys) {
                        std::vector<double> red;
                        red.reserve(kMaxKeys);
                        for (size_t k = 0; k < kMaxKeys; ++k) red.push_back(times[k * (times.size() - 1) / (kMaxKeys - 1)]);
                        times = std::move(red);
                    }
                    std::vector<KeyFrame>& track = clip.tracks[(size_t)bi];
                    track.reserve(times.size());
                    for (double t : times) {
                        KeyFrame kf;
                        kf.time = t;
                        double tt[3] = {bone.local[12], bone.local[13], bone.local[14]};
                        // 从绑定姿态矩阵反推欧拉角不方便，直接用节点 Lcl 值
                        auto& m = modelsById[kv.first];
                        tt[0] = m.tx; tt[1] = m.ty; tt[2] = m.tz;
                        double rr[3] = {m.rx, m.ry, m.rz};
                        double ss[3] = {m.sx, m.sy, m.sz};
                        auto itT = kv.second.find('T');
                        auto itR = kv.second.find('R');
                        auto itS = kv.second.find('S');
                        if (itT != kv.second.end()) {
                            for (auto& c : itT->second) if (c.second.comp >= 0 && c.second.comp < 3) tt[c.second.comp] = c.second.sample(t);
                        }
                        if (itR != kv.second.end()) {
                            for (auto& c : itR->second) if (c.second.comp >= 0 && c.second.comp < 3) rr[c.second.comp] = c.second.sample(t);
                        }
                        if (itS != kv.second.end()) {
                            for (auto& c : itS->second) if (c.second.comp >= 0 && c.second.comp < 3) ss[c.second.comp] = c.second.sample(t);
                        }
                        BuildLclMatrix(tt[0], tt[1], tt[2], rr[0], rr[1], rr[2], ss[0], ss[1], ss[2], kf.local);
                        track.push_back(kf);
                    }
                }
                md.clips.push_back(std::move(clip));
            }
        }
    }

    md.materialCount = (uint32_t)md.mesh.materials.size();
    md.boneCount = (uint32_t)md.bones.size();
    md.vertexCount = (uint32_t)md.mesh.vertices.size();
    md.triangleCount = (uint32_t)(md.mesh.indices.size() / 3);
    md.clipCount = (uint32_t)md.clips.size();
    if (md.mesh.vertices.empty()) {
        md.ok = true;
        md.error.clear();
        if (md.warning.empty()) md.warning = "无可见网格";
        return true;
    }
    md.ok = true;
    md.error.clear();
    return true;
}

} // namespace av
