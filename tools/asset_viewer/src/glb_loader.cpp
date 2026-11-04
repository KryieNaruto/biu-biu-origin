// glb_loader.cpp —— GLB / glTF 2.0 解析（几何 / 材质 / 蒙皮 / 动画 / 内嵌贴图）
// OWNER: glb-loader
//
// 移植自 ref/glb_parse.py，并按 SHARED_SPEC 与 src/model_data.h 契约输出。
// 与参考实现相比的修正（见交付报告）：
//   * 支持全部 primitive mode（TRIANGLES/STRIP/FAN/POINTS/LINES…），不再只认 mode=4；
//   * 索引缺失时自动生成 0..N-1；
//   * 动画通道支持 translation/rotation/scale（参考实现只读 rotation）；
//   * 骨骼 bind 用 inverseBindMatrices 正确填入（参考实现恒为单位阵）；
//   * 骨骼 world 级联、bind 局部逆矩阵按 model_data.h 的「行向量 × 矩阵」约定；
//   * 所有 bufferView/accessor 访问都做越界防护。
#include "glb_loader.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <deque>
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
// 极简 JSON 解析器（只读，够 glTF 用；不抛异常，出错置 error）
// ===========================================================================
struct JsonValue {
    enum Type { Null, Bool, Number, String, Array, Object };
    Type type = Null;
    bool b = false;
    double num = 0.0;
    std::string str;
    std::vector<JsonValue> arr;
    std::vector<std::pair<std::string, JsonValue>> obj;

    const JsonValue* get(const char* key) const {
        if (type != Object) return nullptr;
        for (const auto& kv : obj)
            if (kv.first == key) return &kv.second;
        return nullptr;
    }
    bool isNum() const { return type == Number; }
    bool isArr() const { return type == Array; }
    bool isObj() const { return type == Object; }
    bool isStr() const { return type == String; }
    double number(double def = 0.0) const { return type == Number ? num : def; }
    int integer(int def = 0) const { return type == Number ? (int)num : def; }
    std::string string(const std::string& def = std::string()) const {
        return type == String ? str : def;
    }
    bool boolean(bool def = false) const { return type == Bool ? b : def; }
    size_t size() const { return type == Array ? arr.size() : (type == Object ? obj.size() : 0); }
};

class JsonParser {
public:
    JsonParser(const char* s, size_t n) : p_(s), end_(s + n) {}
    bool parse(JsonValue* out, std::string* err) {
        skipWs();
        if (!value(out)) {
            if (err) *err = err_.empty() ? "JSON 解析失败" : err_;
            return false;
        }
        skipWs();
        return true;
    }

private:
    const char* p_;
    const char* end_;
    std::string err_;

    void skipWs() {
        while (p_ < end_ && (*p_ == ' ' || *p_ == '\t' || *p_ == '\n' || *p_ == '\r')) ++p_;
    }
    bool fail(const char* m) {
        if (err_.empty()) err_ = m;
        return false;
    }
    bool literal(const char* lit) {
        size_t n = std::strlen(lit);
        if ((size_t)(end_ - p_) < n || std::memcmp(p_, lit, n) != 0) return fail("非法字面量");
        p_ += n;
        return true;
    }
    bool value(JsonValue* v) {
        if (p_ >= end_) return fail("JSON 意外结束");
        char c = *p_;
        if (c == '{') return object(v);
        if (c == '[') return array(v);
        if (c == '"') {
            v->type = JsonValue::String;
            return string(&v->str);
        }
        if (c == 't') {
            v->type = JsonValue::Bool;
            v->b = true;
            return literal("true");
        }
        if (c == 'f') {
            v->type = JsonValue::Bool;
            v->b = false;
            return literal("false");
        }
        if (c == 'n') {
            v->type = JsonValue::Null;
            return literal("null");
        }
        return number(v);
    }
    bool number(JsonValue* v) {
        const char* s = p_;
        if (p_ < end_ && (*p_ == '-' || *p_ == '+')) ++p_;
        bool any = false;
        while (p_ < end_ && *p_ >= '0' && *p_ <= '9') { ++p_; any = true; }
        if (p_ < end_ && *p_ == '.') {
            ++p_;
            while (p_ < end_ && *p_ >= '0' && *p_ <= '9') { ++p_; any = true; }
        }
        if (!any) return fail("非法数字");
        if (p_ < end_ && (*p_ == 'e' || *p_ == 'E')) {
            ++p_;
            if (p_ < end_ && (*p_ == '-' || *p_ == '+')) ++p_;
            while (p_ < end_ && *p_ >= '0' && *p_ <= '9') ++p_;
        }
        std::string tmp(s, (size_t)(p_ - s));
        v->type = JsonValue::Number;
        v->num = std::strtod(tmp.c_str(), nullptr);
        return true;
    }
    static void appendUtf8(std::string* o, unsigned cp) {
        if (cp < 0x80) {
            o->push_back((char)cp);
        } else if (cp < 0x800) {
            o->push_back((char)(0xC0 | (cp >> 6)));
            o->push_back((char)(0x80 | (cp & 0x3F)));
        } else if (cp < 0x10000) {
            o->push_back((char)(0xE0 | (cp >> 12)));
            o->push_back((char)(0x80 | ((cp >> 6) & 0x3F)));
            o->push_back((char)(0x80 | (cp & 0x3F)));
        } else {
            o->push_back((char)(0xF0 | (cp >> 18)));
            o->push_back((char)(0x80 | ((cp >> 12) & 0x3F)));
            o->push_back((char)(0x80 | ((cp >> 6) & 0x3F)));
            o->push_back((char)(0x80 | (cp & 0x3F)));
        }
    }
    bool hex4(unsigned* out) {
        if (end_ - p_ < 4) return fail("JSON \\u 转义不完整");
        unsigned v = 0;
        for (int i = 0; i < 4; ++i) {
            char c = p_[i];
            v <<= 4;
            if (c >= '0' && c <= '9') v |= (unsigned)(c - '0');
            else if (c >= 'a' && c <= 'f') v |= (unsigned)(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') v |= (unsigned)(c - 'A' + 10);
            else return fail("JSON \\u 转义非法");
        }
        p_ += 4;
        *out = v;
        return true;
    }
    bool string(std::string* out) {
        ++p_;  // 跳过起始引号
        out->clear();
        while (p_ < end_) {
            char c = *p_++;
            if (c == '"') return true;
            if (c != '\\') {
                out->push_back(c);
                continue;
            }
            if (p_ >= end_) return fail("JSON 字符串转义不完整");
            char e = *p_++;
            switch (e) {
                case '"': out->push_back('"'); break;
                case '\\': out->push_back('\\'); break;
                case '/': out->push_back('/'); break;
                case 'b': out->push_back('\b'); break;
                case 'f': out->push_back('\f'); break;
                case 'n': out->push_back('\n'); break;
                case 'r': out->push_back('\r'); break;
                case 't': out->push_back('\t'); break;
                case 'u': {
                    unsigned cp = 0;
                    if (!hex4(&cp)) return false;
                    if (cp >= 0xD800 && cp <= 0xDBFF && p_ + 1 < end_ && p_[0] == '\\' && p_[1] == 'u') {
                        const char* save = p_;
                        p_ += 2;
                        unsigned lo = 0;
                        if (!hex4(&lo)) return false;
                        if (lo >= 0xDC00 && lo <= 0xDFFF) {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                        } else {
                            p_ = save;
                        }
                    }
                    appendUtf8(out, cp);
                    break;
                }
                default: return fail("JSON 未知转义");
            }
        }
        return fail("JSON 字符串未闭合");
    }
    bool array(JsonValue* v) {
        v->type = JsonValue::Array;
        ++p_;
        skipWs();
        if (p_ < end_ && *p_ == ']') { ++p_; return true; }
        while (true) {
            JsonValue item;
            skipWs();
            if (!value(&item)) return false;
            v->arr.push_back(std::move(item));
            skipWs();
            if (p_ >= end_) return fail("JSON 数组未闭合");
            if (*p_ == ',') { ++p_; continue; }
            if (*p_ == ']') { ++p_; return true; }
            return fail("JSON 数组语法错误");
        }
    }
    bool object(JsonValue* v) {
        v->type = JsonValue::Object;
        ++p_;
        skipWs();
        if (p_ < end_ && *p_ == '}') { ++p_; return true; }
        while (true) {
            skipWs();
            if (p_ >= end_ || *p_ != '"') return fail("JSON 对象键非法");
            std::string key;
            if (!string(&key)) return false;
            skipWs();
            if (p_ >= end_ || *p_ != ':') return fail("JSON 对象缺少冒号");
            ++p_;
            skipWs();
            JsonValue item;
            if (!value(&item)) return false;
            v->obj.emplace_back(std::move(key), std::move(item));
            skipWs();
            if (p_ >= end_) return fail("JSON 对象未闭合");
            if (*p_ == ',') { ++p_; continue; }
            if (*p_ == '}') { ++p_; return true; }
            return fail("JSON 对象语法错误");
        }
    }
};

// ===========================================================================
// 矩阵工具（与 model_data.h 的 mat:: 一致：行优先、行向量 × 矩阵）
// ===========================================================================
using Mat4 = float[16];

inline void MatIdentity(float* m) { mat::identity(m); }

void MatFromColumnMajor(const float* cm, float* out) { mat::fromColumnMajor(cm, out); }

void QuatToRowMajorRotation(const float q[4], float* m) {
    // q = (x,y,z,w)；行向量约定下 R_row = R_col^T
    float x = q[0], y = q[1], z = q[2], w = q[3];
    float n = std::sqrt(x * x + y * y + z * z + w * w);
    if (n > 1e-12f) {
        x /= n; y /= n; z /= n; w /= n;
    } else {
        x = y = z = 0.0f; w = 1.0f;
    }
    float xx = x * x, yy = y * y, zz = z * z;
    float xy = x * y, xz = x * z, yz = y * z;
    float wx = w * x, wy = w * y, wz = w * z;
    // 列向量版本的行：
    // R_col = [[1-2(yy+zz), 2(xy-wz), 2(xz+wy)],
    //          [2(xy+wz), 1-2(xx+zz), 2(yz-wx)],
    //          [2(xz-wy), 2(yz+wx), 1-2(xx+yy)]]
    // 行向量 = 转置
    m[0] = 1 - 2 * (yy + zz); m[1] = 2 * (xy + wz);       m[2] = 2 * (xz - wy);       m[3] = 0;
    m[4] = 2 * (xy - wz);     m[5] = 1 - 2 * (xx + zz);   m[6] = 2 * (yz + wx);       m[7] = 0;
    m[8] = 2 * (xz + wy);     m[9] = 2 * (yz - wx);       m[10] = 1 - 2 * (xx + yy);  m[11] = 0;
    m[12] = 0; m[13] = 0; m[14] = 0; m[15] = 1;
}

// M = T * R * S（行向量：p' = p*S*R*T）
void ComposeTRS(const float t[3], const float q[4], const float s[3], float* out) {
    float R[16];
    QuatToRowMajorRotation(q, R);
    out[0] = R[0] * s[0];  out[1] = R[1] * s[0];  out[2] = R[2] * s[0];  out[3] = 0;
    out[4] = R[4] * s[1];  out[5] = R[5] * s[1];  out[6] = R[6] * s[1];  out[7] = 0;
    out[8] = R[8] * s[2];  out[9] = R[9] * s[2];  out[10] = R[10] * s[2]; out[11] = 0;
    out[12] = t[0]; out[13] = t[1]; out[14] = t[2]; out[15] = 1;
}

void TransformPoint(const float* m, const float p[3], float out[3]) {
    out[0] = p[0] * m[0] + p[1] * m[4] + p[2] * m[8] + m[12];
    out[1] = p[0] * m[1] + p[1] * m[5] + p[2] * m[9] + m[13];
    out[2] = p[0] * m[2] + p[1] * m[6] + p[2] * m[10] + m[14];
}

void TransformDir(const float* m, const float p[3], float out[3]) {
    out[0] = p[0] * m[0] + p[1] * m[4] + p[2] * m[8];
    out[1] = p[0] * m[1] + p[1] * m[5] + p[2] * m[9];
    out[2] = p[0] * m[2] + p[1] * m[6] + p[2] * m[10];
}

void MatMul(const float* a, const float* b, float* out) { mat::multiply(a, b, out); }

// ===========================================================================
// accessor 读取
// ===========================================================================
enum CompType { CT_I8 = 5120, CT_U8 = 5121, CT_I16 = 5122, CT_U16 = 5123, CT_U32 = 5125, CT_F32 = 5126 };

inline int CompSize(int ct) {
    switch (ct) {
        case CT_I8: case CT_U8: return 1;
        case CT_I16: case CT_U16: return 2;
        case CT_U32: case CT_F32: return 4;
        default: return 0;
    }
}
inline int NumComponents(const std::string& t) {
    if (t == "SCALAR") return 1;
    if (t == "VEC2") return 2;
    if (t == "VEC3") return 3;
    if (t == "VEC4") return 4;
    if (t == "MAT2") return 4;
    if (t == "MAT3") return 9;
    if (t == "MAT4") return 16;
    return 0;
}

// 反归一化：integer normalized → [-1,1] 或 [0,1]
inline float Denorm(double v, int ct, bool normalized) {
    if (!normalized) return (float)v;
    switch (ct) {
        case CT_I8: return std::max((float)v / 127.0f, -1.0f);
        case CT_U8: return (float)v / 255.0f;
        case CT_I16: return std::max((float)v / 32767.0f, -1.0f);
        case CT_U16: return (float)v / 65535.0f;
        default: return (float)v;
    }
}

// 一个已解析的 buffer（glb 的 BIN chunk 或外置文件）
struct BufferSrc {
    const unsigned char* data = nullptr;
    size_t size = 0;
};

struct Ctx {
    const JsonValue* js = nullptr;
    std::deque<std::vector<unsigned char>> ownedBufs;   // bin chunk / 外置 buffer / data URI 的内存
    std::vector<BufferSrc> bufs;
    std::string baseDirWide;   // 外置 buffer/图片的相对目录（wide 由 img::Wide 转换）
    std::vector<unsigned char> fileBytes;                // glb 原文
};

// 读取 accessor → floats（interleaved 展开，nComp 个分量一组）。失败返回 false。
// out 长度 = count * nComp。
bool ReadAccessor(const Ctx& ctx, int accIndex, std::vector<float>* out, int* outNComp) {
    const JsonValue& accs = *ctx.js->get("accessors");
    if (!accs.isArr() || accIndex < 0 || (size_t)accIndex >= accs.arr.size()) return false;
    const JsonValue& acc = accs.arr[accIndex];
    int n = std::max(0, acc.get("count") ? acc.get("count")->integer(0) : 0);
    int ct = acc.get("componentType") ? acc.get("componentType")->integer(CT_F32) : CT_F32;
    int ncomp = acc.get("type") ? NumComponents(acc.get("type")->string("SCALAR")) : 1;
    if (ncomp <= 0) ncomp = 1;
    bool normalized = acc.get("normalized") ? acc.get("normalized")->boolean(false) : false;
    int csize = CompSize(ct);
    if (csize == 0 || n == 0) {
        out->assign((size_t)std::max(0, n) * ncomp, 0.0f);
        *outNComp = ncomp;
        return n > 0;
    }
    if ((uint64_t)n * ncomp > 64ull * 1024 * 1024) return false;   // 防御：异常大
    out->assign((size_t)n * ncomp, 0.0f);
    *outNComp = ncomp;

    const JsonValue* bvs = ctx.js->get("bufferViews");

    // ---- 基础数据 ----
    if (const JsonValue* bvIdx = acc.get("bufferView")) {
        int bi = bvIdx->integer(-1);
        if (bvs && bvs->isArr() && bi >= 0 && (size_t)bi < bvs->arr.size()) {
            const JsonValue& bv = bvs->arr[bi];
            int bufIdx = bv.get("buffer") ? bv.get("buffer")->integer(0) : 0;
            uint64_t bvOff = bv.get("byteOffset") ? (uint64_t)std::max(0.0, bv.get("byteOffset")->number(0)) : 0;
            uint64_t accOff = acc.get("byteOffset") ? (uint64_t)std::max(0.0, acc.get("byteOffset")->number(0)) : 0;
            uint64_t stride = bv.get("byteStride") ? (uint64_t)bv.get("byteStride")->integer(0) : 0;
            uint64_t elemSize = (uint64_t)csize * ncomp;
            if (stride == 0) stride = elemSize;
            if (stride < elemSize) stride = elemSize;
            const BufferSrc* src = nullptr;
            if (bufIdx >= 0 && (size_t)bufIdx < ctx.bufs.size()) src = &ctx.bufs[(size_t)bufIdx];
            if (src && src->data) {
                uint64_t base = bvOff + accOff;
                for (int i = 0; i < n; ++i) {
                    uint64_t off = base + (uint64_t)i * stride;
                    if (off + elemSize > src->size) break;   // 越界防护：停止填充
                    const unsigned char* sp = src->data + off;
                    float* d = out->data() + (size_t)i * ncomp;
                    for (int c = 0; c < ncomp; ++c) {
                        const unsigned char* cp = sp + (size_t)c * csize;
                        double v = 0.0;
                        switch (ct) {
                            case CT_I8: v = (double)*(const int8_t*)cp; break;
                            case CT_U8: v = (double)*(const uint8_t*)cp; break;
                            case CT_I16: { int16_t t; std::memcpy(&t, cp, 2); v = t; break; }
                            case CT_U16: { uint16_t t; std::memcpy(&t, cp, 2); v = t; break; }
                            case CT_U32: { uint32_t t; std::memcpy(&t, cp, 4); v = (double)t; break; }
                            case CT_F32: { float t; std::memcpy(&t, cp, 4); v = t; break; }
                            default: break;
                        }
                        d[c] = Denorm(v, ct, normalized);
                    }
                }
            }
        }
    }

    // ---- sparse 覆盖 ----
    if (const JsonValue* sp = acc.get("sparse")) {
        if (sp->isObj()) {
            int scnt = sp->get("count") ? sp->get("count")->integer(0) : 0;
            const JsonValue* sIdx = sp->get("indices");
            const JsonValue* sVal = sp->get("values");
            if (scnt > 0 && sIdx && sVal && bvs && bvs->isArr()) {
                scnt = std::min(scnt, n);
                int iBv = sIdx->get("bufferView") ? sIdx->get("bufferView")->integer(-1) : -1;
                int vBv = sVal->get("bufferView") ? sVal->get("bufferView")->integer(-1) : -1;
                int ict = sIdx->get("componentType") ? sIdx->get("componentType")->integer(CT_U32) : CT_U32;
                int isize = CompSize(ict);
                if (isize == 0) isize = 4;
                auto viewPtr = [&](int bvi, uint64_t* outOff, size_t* outSize) -> bool {
                    if (!bvs || bvi < 0 || (size_t)bvi >= bvs->arr.size()) return false;
                    const JsonValue& bv = bvs->arr[bvi];
                    int bufIdx = bv.get("buffer") ? bv.get("buffer")->integer(0) : 0;
                    if (bufIdx < 0 || (size_t)bufIdx >= ctx.bufs.size()) return false;
                    const BufferSrc& src = ctx.bufs[(size_t)bufIdx];
                    if (!src.data) return false;
                    uint64_t o = bv.get("byteOffset") ? (uint64_t)std::max(0.0, bv.get("byteOffset")->number(0)) : 0;
                    uint64_t len = bv.get("byteLength") ? (uint64_t)std::max(0.0, bv.get("byteLength")->number(0)) : 0;
                    if (len == 0 || o + len > src.size) len = src.size > o ? src.size - o : 0;
                    *outOff = o;
                    *outSize = (size_t)len;
                    return true;
                };
                uint64_t iOff = 0, vOff = 0;
                size_t iLen = 0, vLen = 0;
                bool okI = viewPtr(iBv, &iOff, &iLen);
                bool okV = viewPtr(vBv, &vOff, &vLen);
                if (okI && okV) {
                    int iBufIdx = bvs->arr[iBv].get("buffer") ? bvs->arr[iBv].get("buffer")->integer(0) : 0;
                    int vBufIdx = bvs->arr[vBv].get("buffer") ? bvs->arr[vBv].get("buffer")->integer(0) : 0;
                    if (iBufIdx >= 0 && vBufIdx >= 0 && (size_t)iBufIdx < ctx.bufs.size() &&
                        (size_t)vBufIdx < ctx.bufs.size()) {
                        const BufferSrc& ib = ctx.bufs[(size_t)iBufIdx];
                        const BufferSrc& vb = ctx.bufs[(size_t)vBufIdx];
                        uint64_t baseI = iOff + (uint64_t)std::max(0.0, sIdx->get("byteOffset") ? sIdx->get("byteOffset")->number(0) : 0.0);
                        uint64_t baseV = vOff + (uint64_t)std::max(0.0, sVal->get("byteOffset") ? sVal->get("byteOffset")->number(0) : 0.0);
                        uint64_t elemSize = (uint64_t)csize * ncomp;
                        for (int i = 0; i < scnt; ++i) {
                            uint64_t io = baseI + (uint64_t)i * (uint64_t)isize;
                            if (io + (uint64_t)isize > iLen + iOff || io + (uint64_t)isize > ib.size) break;
                            uint64_t target = 0;
                            const unsigned char* cp = ib.data + io;
                            switch (ict) {
                                case CT_U8: target = *(const uint8_t*)cp; break;
                                case CT_U16: { uint16_t t; std::memcpy(&t, cp, 2); target = t; break; }
                                case CT_I16: { int16_t t; std::memcpy(&t, cp, 2); target = (uint64_t)std::max<int>(0, t); break; }
                                case CT_I8: target = (uint64_t)std::max<int>(0, *(const int8_t*)cp); break;
                                default: { uint32_t t; std::memcpy(&t, cp, 4); target = t; break; }
                            }
                            if (target >= (uint64_t)n) continue;
                            uint64_t vo = baseV + (uint64_t)i * elemSize;
                            if (vo + elemSize > vLen + vOff || vo + elemSize > vb.size) break;
                            float* d = out->data() + (size_t)target * ncomp;
                            for (int c = 0; c < ncomp; ++c) {
                                const unsigned char* vp = vb.data + vo + (size_t)c * csize;
                                double v = 0.0;
                                switch (ct) {
                                    case CT_I8: v = (double)*(const int8_t*)vp; break;
                                    case CT_U8: v = (double)*(const uint8_t*)vp; break;
                                    case CT_I16: { int16_t t; std::memcpy(&t, vp, 2); v = t; break; }
                                    case CT_U16: { uint16_t t; std::memcpy(&t, vp, 2); v = t; break; }
                                    case CT_U32: { uint32_t t; std::memcpy(&t, vp, 4); v = (double)t; break; }
                                    case CT_F32: { float t; std::memcpy(&t, vp, 4); v = t; break; }
                                    default: break;
                                }
                                d[c] = Denorm(v, ct, normalized);
                            }
                        }
                    }
                }
            }
        }
    }
    return true;
}

// 读取 accessor 的原始整数（索引/关节用）→ uint32
bool ReadAccessorU32(const Ctx& ctx, int accIndex, std::vector<uint32_t>* out) {
    out->clear();
    if (accIndex < 0) return false;
    const JsonValue& accs = *ctx.js->get("accessors");
    if (!accs.isArr() || (size_t)accIndex >= accs.arr.size()) return false;
    const JsonValue& acc = accs.arr[accIndex];
    int n = acc.get("count") ? acc.get("count")->integer(0) : 0;
    int ct = acc.get("componentType") ? acc.get("componentType")->integer(CT_U32) : CT_U32;
    int ncomp = acc.get("type") ? NumComponents(acc.get("type")->string("SCALAR")) : 1;
    int csize = CompSize(ct);
    if (n <= 0 || csize == 0 || ncomp <= 0) return false;
    if ((uint64_t)n * ncomp > 64ull * 1024 * 1024) return false;
    out->assign((size_t)n, 0u);
    const JsonValue* bvs = ctx.js->get("bufferViews");
    const JsonValue* bvIdx = acc.get("bufferView");
    if (!bvIdx || !bvs || !bvs->isArr()) return true;   // 全 0
    int bi = bvIdx->integer(-1);
    if (bi < 0 || (size_t)bi >= bvs->arr.size()) return true;
    const JsonValue& bv = bvs->arr[bi];
    int bufIdx = bv.get("buffer") ? bv.get("buffer")->integer(0) : 0;
    if (bufIdx < 0 || (size_t)bufIdx >= ctx.bufs.size()) return true;
    const BufferSrc& src = ctx.bufs[(size_t)bufIdx];
    if (!src.data) return true;
    uint64_t bvOff = bv.get("byteOffset") ? (uint64_t)std::max(0.0, bv.get("byteOffset")->number(0)) : 0;
    uint64_t accOff = acc.get("byteOffset") ? (uint64_t)std::max(0.0, acc.get("byteOffset")->number(0)) : 0;
    uint64_t stride = 0;
    if (ncomp == 1) {
        stride = bv.get("byteStride") ? (uint64_t)bv.get("byteStride")->integer(0) : 0;
        if (stride == 0) stride = (uint64_t)csize;
        if (stride < (uint64_t)csize) stride = (uint64_t)csize;
        uint64_t base = bvOff + accOff;
        for (int i = 0; i < n; ++i) {
            uint64_t off = base + (uint64_t)i * stride;
            if (off + (uint64_t)csize > src.size) break;
            const unsigned char* cp = src.data + off;
            uint32_t v = 0;
            switch (ct) {
                case CT_U8: v = *(const uint8_t*)cp; break;
                case CT_I8: v = (uint32_t)std::max<int>(0, *(const int8_t*)cp); break;
                case CT_U16: { uint16_t t; std::memcpy(&t, cp, 2); v = t; break; }
                case CT_I16: { int16_t t; std::memcpy(&t, cp, 2); v = (uint32_t)std::max<int>(0, t); break; }
                default: { uint32_t t; std::memcpy(&t, cp, 4); v = t; break; }
            }
            (*out)[(size_t)i] = v;
        }
        return true;
    }
    // 非 SCALAR（JOINTS_0 走 ReadAccessor 的 float 路径后再转）
    return false;
}

inline void AppendWarning(std::string* warn, const std::string& w) {
    if (warn->size() > 600) return;
    if (!warn->empty()) *warn += "; ";
    *warn += w;
}

// ===========================================================================
// 动画 / 骨骼
// ===========================================================================
struct TrackSampler {
    std::vector<double> times;
    std::vector<float> values;   // 每帧 nComp 个分量
    int nComp = 0;
    std::string path;            // "translation" / "rotation" / "scale" / "weights"
    std::string interp;          // "LINEAR" / "STEP" / "CUBICSPLINE"
};

// 采样某一时刻的局部 TRS 值
void SampleTRS(const TrackSampler& s, double t, float out[4], bool* has) {
    *has = false;
    size_t n = s.times.size();
    if (n == 0 || s.values.empty()) return;
    if (s.path == "rotation") {
        out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 1;
    } else {
        out[0] = 0; out[1] = 0; out[2] = s.path == "scale" ? 1.0f : 0.0f; out[3] = 1;
    }
    *has = true;
    // 二分
    size_t lo = 0, hi = n - 1;
    if (t <= s.times[0]) {
        hi = 0;
        lo = 0;
    } else if (t >= s.times[n - 1]) {
        lo = hi = n - 1;
    } else {
        while (hi - lo > 1) {
            size_t mid = (lo + hi) / 2;
            if (s.times[mid] <= t) lo = mid; else hi = mid;
        }
    }
    int nc = s.nComp;
    const float* a = s.values.data() + lo * (size_t)nc;
    const float* b = s.values.data() + hi * (size_t)nc;
    if (s.interp == "STEP" || lo == hi) {
        for (int c = 0; c < nc && c < 4; ++c) out[c] = a[c];
        return;
    }
    double span = s.times[hi] - s.times[lo];
    float k = span > 1e-12 ? (float)((t - s.times[lo]) / span) : 0.0f;
    if (s.path == "rotation" && nc >= 4) {
        float q0[4] = {a[0], a[1], a[2], a[3]}, q1[4] = {b[0], b[1], b[2], b[3]};
        float d = q0[0] * q1[0] + q0[1] * q1[1] + q0[2] * q1[2] + q0[3] * q1[3];
        if (d < 0.0f) {
            for (int i = 0; i < 4; ++i) q1[i] = -q1[i];
            d = -d;
        }
        float r[4];
        if (d > 0.9995f) {
            for (int i = 0; i < 4; ++i) r[i] = q0[i] + (q1[i] - q0[i]) * k;
        } else {
            float th = std::acos(std::min(1.0f, std::max(-1.0f, d)));
            float s0 = std::sin((1.0f - k) * th) / std::sin(th);
            float s1 = std::sin(k * th) / std::sin(th);
            for (int i = 0; i < 4; ++i) r[i] = q0[i] * s0 + q1[i] * s1;
        }
        float len = std::sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2] + r[3] * r[3]);
        if (len < 1e-12f) len = 1.0f;
        for (int i = 0; i < 4; ++i) out[i] = r[i] / len;
    } else {
        for (int c = 0; c < nc && c < 4; ++c) out[c] = a[c] + (b[c] - a[c]) * k;
    }
}

void NormalizeName(std::string* s) {
    // 去掉 FBX2glTF 常见的 "Armature|Armature|Armature|Idle" 前缀重复，保留最后一节并去重
    // 例："CharacterArmature|CharacterArmature|CharacterArmature|Idle" → "Idle"
    std::vector<std::string> parts;
    size_t start = 0;
    while (true) {
        size_t pos = s->find('|', start);
        if (pos == std::string::npos) {
            parts.push_back(s->substr(start));
            break;
        }
        parts.push_back(s->substr(start, pos - start));
        start = pos + 1;
    }
    if (parts.size() > 1) {
        std::string last = parts.back();
        if (!last.empty()) *s = last;
    }
    if (s->empty()) *s = "clip";
}

} // namespace

// ===========================================================================
// 入口
// ===========================================================================
bool LoadGLB(const std::string& utf8Path, ModelData* out) {
    if (!out) return false;
    ModelData& md = *out;
    md = ModelData();
    md.path = utf8Path;

    // ---- 文件名 / 分类 ----
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

    std::vector<unsigned char> file;
    if (!img::ReadFileBytesUtf8(utf8Path, &file)) {
        md.ok = false;
        md.error = "文件不存在或无法读取";
        return false;
    }
    md.fileSize = file.size();
    if (file.size() < 12) {
        md.ok = false;
        md.error = "文件太小";
        return false;
    }

    // ---- GLB 容器 ----
    std::string jsonText;
    std::vector<unsigned char> binChunk;
    {
        uint32_t magic, version, total;
        std::memcpy(&magic, file.data(), 4);
        std::memcpy(&version, file.data() + 4, 4);
        std::memcpy(&total, file.data() + 8, 4);
        if (magic != 0x46546C67u) {
            // 允许直接传 .gltf JSON 文本
            bool looksJson = file[0] == '{';
            if (!looksJson) {
                md.ok = false;
                md.error = "不是 GLB 文件（魔数错误）";
                return false;
            }
            jsonText.assign((const char*)file.data(), file.size());
        } else {
            if (version != 2) AppendWarning(&md.warning, "GLB 版本非 2");
            uint64_t limit = std::min<size_t>(file.size(), (size_t)std::max(total, 12u));
            size_t off = 12;
            bool hasJson = false;
            while (off + 8 <= limit) {
                uint32_t clen = 0, ctype = 0;
                std::memcpy(&clen, file.data() + off, 4);
                std::memcpy(&ctype, file.data() + off + 4, 4);
                off += 8;
                if (clen > limit - off) {
                    AppendWarning(&md.warning, "chunk 长度越界已截断");
                    clen = (uint32_t)(limit - off);
                }
                if (ctype == 0x4E4F534Au && !hasJson) {
                    jsonText.assign((const char*)file.data() + off, clen);
                    while (!jsonText.empty() && (jsonText.back() == '\0' || jsonText.back() == ' ' ||
                                                 jsonText.back() == '\t' || jsonText.back() == '\r' ||
                                                 jsonText.back() == '\n'))
                        jsonText.pop_back();
                    hasJson = true;
                } else if (ctype == 0x004E4942u && binChunk.empty()) {
                    binChunk.assign(file.data() + off, file.data() + off + clen);
                }
                off += clen;
            }
            if (!hasJson) {
                md.ok = false;
                md.error = "GLB 缺少 JSON chunk";
                return false;
            }
        }
    }

    // ---- JSON ----
    JsonValue js;
    {
        std::string jerr;
        JsonParser jp(jsonText.data(), jsonText.size());
        if (!jp.parse(&js, &jerr) || !js.isObj()) {
            md.ok = false;
            md.error = "JSON 解析失败: " + jerr;
            return false;
        }
    }
    Ctx ctx;
    ctx.js = &js;
    ctx.fileBytes = std::move(file);

    // ---- buffers ----
    std::string baseDir;
    {
        size_t slash = utf8Path.find_last_of("\\/");
        if (slash != std::string::npos) baseDir = utf8Path.substr(0, slash + 1);
    }
    // 注意：ownedBufs 用 deque，push_back 不会使已有元素的 data() 指针失效
    if (!binChunk.empty()) ctx.ownedBufs.push_back(std::move(binChunk));
    BufferSrc binSrc;
    if (!ctx.ownedBufs.empty()) {
        binSrc.data = ctx.ownedBufs.front().data();
        binSrc.size = ctx.ownedBufs.front().size();
    }
    if (const JsonValue* bufs = js.get("buffers")) {
        if (bufs->isArr()) {
            bool usedBin = false;
            for (size_t i = 0; i < bufs->arr.size(); ++i) {
                const JsonValue& b = bufs->arr[i];
                std::string uri = b.get("uri") ? b.get("uri")->string() : std::string();
                BufferSrc src;
                if (uri.empty()) {
                    if (!usedBin && binSrc.data) {
                        src = binSrc;
                        usedBin = true;
                    }
                } else if (uri.compare(0, 5, "data:") == 0) {
                    size_t comma = uri.find(',');
                    if (comma != std::string::npos && uri.size() > comma + 1) {
                        std::vector<unsigned char> dec;
                        // base64 解码
                        static const int8_t tbl[256] = {
                            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
                            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
                            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,62,-1,-1,-1,63,
                            52,53,54,55,56,57,58,59,60,61,-1,-1,-1,-1,-1,-1,
                            -1,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,
                            15,16,17,18,19,20,21,22,23,24,25,-1,-1,-1,-1,-1,
                            -1,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,
                            41,42,43,44,45,46,47,48,49,50,51,-1,-1,-1,-1,-1,
                        };
                        int acc = 0, bits = 0;
                        dec.reserve((uri.size() - comma) * 3 / 4 + 4);
                        for (size_t k = comma + 1; k < uri.size(); ++k) {
                            unsigned char ch = (unsigned char)uri[k];
                            int v = tbl[ch];
                            if (v < 0) continue;
                            acc = (acc << 6) | v;
                            bits += 6;
                            if (bits >= 8) {
                                bits -= 8;
                                dec.push_back((unsigned char)((acc >> bits) & 0xFF));
                            }
                        }
                        ctx.ownedBufs.push_back(std::move(dec));
                        src.data = ctx.ownedBufs.back().data();
                        src.size = ctx.ownedBufs.back().size();
                    }
                } else {
                    std::vector<unsigned char> bytes;
                    std::string path = baseDir + uri;
                    if (img::ReadFileBytesUtf8(path, &bytes)) {
                        ctx.ownedBufs.push_back(std::move(bytes));
                        src.data = ctx.ownedBufs.back().data();
                        src.size = ctx.ownedBufs.back().size();
                    } else {
                        AppendWarning(&md.warning, "外置 buffer 读取失败: " + uri);
                    }
                }
                ctx.bufs.push_back(src);
            }
        }
    }

    const JsonValue* nodesJ = js.get("nodes");
    const JsonValue* meshesJ = js.get("meshes");
    const JsonValue* matsJ = js.get("materials");
    const JsonValue* accsJ = js.get("accessors");
    const size_t nodeCount = (nodesJ && nodesJ->isArr()) ? nodesJ->arr.size() : 0;
    const size_t meshCount = (meshesJ && meshesJ->isArr()) ? meshesJ->arr.size() : 0;
    const size_t accCount = (accsJ && accsJ->isArr()) ? accsJ->arr.size() : 0;

    if (nodeCount == 0 || meshCount == 0) {
        // 无网格：ok=true 空占位（SPEC §9）
        md.ok = true;
        md.error.clear();
        if (md.warning.empty()) md.warning = "无可见网格";
        return true;
    }

    // ---- 父表 ----
    std::vector<int> parent(nodeCount, -1);
    for (size_t i = 0; i < nodeCount; ++i) {
        if (const JsonValue* ch = nodesJ->arr[i].get("children")) {
            if (ch->isArr()) {
                for (const auto& c : ch->arr) {
                    int ci = c.integer(-1);
                    if (ci >= 0 && (size_t)ci < nodeCount) parent[(size_t)ci] = (int)i;
                }
            }
        }
    }

    // ---- 节点局部矩阵 ----
    std::vector<std::array<float, 16>> local(nodeCount);
    for (size_t i = 0; i < nodeCount; ++i) {
        const JsonValue& n = nodesJ->arr[i];
        float* L = local[i].data();
        if (const JsonValue* m = n.get("matrix")) {
            if (m->isArr() && m->arr.size() >= 16) {
                float cm[16];
                for (int k = 0; k < 16; ++k) cm[k] = (float)m->arr[(size_t)k].number(0.0);
                MatFromColumnMajor(cm, L);
                continue;
            }
        }
        float t[3] = {0, 0, 0}, q[4] = {0, 0, 0, 1}, s[3] = {1, 1, 1};
        if (const JsonValue* v = n.get("translation")) {
            if (v->isArr()) for (size_t k = 0; k < 3 && k < v->arr.size(); ++k) t[k] = (float)v->arr[k].number(0.0);
        }
        if (const JsonValue* v = n.get("rotation")) {
            if (v->isArr()) for (size_t k = 0; k < 4 && k < v->arr.size(); ++k) q[k] = (float)v->arr[k].number(k == 3 ? 1.0 : 0.0);
        }
        if (const JsonValue* v = n.get("scale")) {
            if (v->isArr()) for (size_t k = 0; k < 3 && k < v->arr.size(); ++k) s[k] = (float)v->arr[k].number(1.0);
        }
        ComposeTRS(t, q, s, L);
    }

    // ---- 世界矩阵 ----
    std::vector<std::array<float, 16>> world(nodeCount);
    std::vector<char> visited(nodeCount, 0);
    // 迭代式求世界矩阵，避免深链递归栈溢出
    {
        std::vector<int> stack;
        for (size_t i = 0; i < nodeCount; ++i) {
            if (parent[i] != -1) continue;
            stack.push_back((int)i);
        }
        // 从上往下 BFS
        std::vector<int> order;
        order.reserve(nodeCount);
        while (!stack.empty()) {
            int ni = stack.back();
            stack.pop_back();
            if (visited[(size_t)ni]) continue;
            visited[(size_t)ni] = 1;
            order.push_back(ni);
            if (const JsonValue* ch = nodesJ->arr[(size_t)ni].get("children")) {
                if (ch->isArr()) {
                    for (const auto& c : ch->arr) {
                        int ci = c.integer(-1);
                        if (ci >= 0 && (size_t)ci < nodeCount && !visited[(size_t)ci]) stack.push_back(ci);
                    }
                }
            }
        }
        // 兜底：环/孤立节点
        for (size_t i = 0; i < nodeCount; ++i)
            if (!visited[i]) { visited[i] = 1; order.push_back((int)i); }
        for (int ni : order) {
            int p = parent[(size_t)ni];
            if (p >= 0 && visited[(size_t)p]) {
                MatMul(local[(size_t)ni].data(), world[(size_t)p].data(), world[(size_t)ni].data());
            } else {
                std::memcpy(world[(size_t)ni].data(), local[(size_t)ni].data(), sizeof(float) * 16);
            }
        }
    }

    // ---- 材质 ----
    std::vector<int> matTexMap;   // glTF texture index -> ModelData::textures 下标（暂为 glTF 下标）
    if (matsJ && matsJ->isArr()) {
        for (size_t mi = 0; mi < matsJ->arr.size(); ++mi) {
            const JsonValue& m = matsJ->arr[mi];
            Material mat;
            mat.name = m.get("name") ? m.get("name")->string("") : std::string();
            if (mat.name.empty()) mat.name = "mat" + std::to_string(mi);
            const JsonValue* pbr = m.get("pbrMetallicRoughness");
            if (pbr && pbr->isObj()) {
                if (const JsonValue* bcf = pbr->get("baseColorFactor")) {
                    if (bcf->isArr()) {
                        for (size_t k = 0; k < 4 && k < bcf->arr.size(); ++k)
                            mat.baseColor[k] = (float)bcf->arr[k].number(k == 3 ? 1.0 : 1.0);
                    }
                }
                if (const JsonValue* bct = pbr->get("baseColorTexture")) {
                    if (bct->isObj()) mat.diffuseTex = bct->get("index") ? bct->get("index")->integer(-1) : -1;
                }
                if (const JsonValue* mf = pbr->get("metallicFactor")) mat.metallic = (float)mf->number(1.0);
                if (const JsonValue* rf = pbr->get("roughnessFactor")) mat.roughness = (float)rf->number(1.0);
            }
            mat.doubleSided = m.get("doubleSided") ? m.get("doubleSided")->boolean(false) : false;
            md.mesh.materials.push_back(std::move(mat));
        }
    }
    md.materialCount = (uint32_t)md.mesh.materials.size();

    // ---- 纹理：glTF image/texture → TextureInfo ----
    {
        const JsonValue* imagesJ = js.get("images");
        const JsonValue* texturesJ = js.get("textures");
        const JsonValue* bvsJ = js.get("bufferViews");
        std::vector<int> imageToTex;   // glTF image index -> textures index
        if (imagesJ && imagesJ->isArr()) {
            for (size_t ii = 0; ii < imagesJ->arr.size(); ++ii) {
                const JsonValue& im = imagesJ->arr[ii];
                TextureInfo ti;
                ti.name = im.get("name") ? im.get("name")->string("") : std::string("image" + std::to_string(ii));
                bool decoded = false;
                if (const JsonValue* bvi = im.get("bufferView")) {
                    int bi = bvi->integer(-1);
                    if (bvsJ && bvsJ->isArr() && bi >= 0 && (size_t)bi < bvsJ->arr.size()) {
                        const JsonValue& bv = bvsJ->arr[bi];
                        int bufIdx = bv.get("buffer") ? bv.get("buffer")->integer(0) : 0;
                        uint64_t off = bv.get("byteOffset") ? (uint64_t)std::max(0.0, bv.get("byteOffset")->number(0)) : 0;
                        uint64_t len = bv.get("byteLength") ? (uint64_t)std::max(0.0, bv.get("byteLength")->number(0)) : 0;
                        if (bufIdx >= 0 && (size_t)bufIdx < ctx.bufs.size() && ctx.bufs[(size_t)bufIdx].data) {
                            const BufferSrc& src = ctx.bufs[(size_t)bufIdx];
                            if (off < src.size) {
                                uint64_t avail = src.size - off;
                                if (len == 0 || len > avail) len = avail;
                                decoded = tex::BakeFromMemory(ti.name, src.data + off, (size_t)len, &ti);
                            }
                        }
                    }
                } else if (const JsonValue* uriJ = im.get("uri")) {
                    std::string uri = uriJ->string();
                    if (uri.compare(0, 5, "data:") == 0) {
                        size_t comma = uri.find(',');
                        if (comma != std::string::npos) {
                            // 内嵌 base64 图片：复用 buffer 解码逻辑不便，这里单独解 base64
                            std::vector<unsigned char> bin;
                            static const char* b64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
                            int acc = 0, bits = 0;
                            for (size_t k = comma + 1; k < uri.size(); ++k) {
                                const char* p = std::strchr(b64, uri[k]);
                                if (!p || uri[k] == '\0') continue;
                                acc = (acc << 6) | (int)(p - b64);
                                bits += 6;
                                if (bits >= 8) {
                                    bits -= 8;
                                    bin.push_back((unsigned char)((acc >> bits) & 0xFF));
                                }
                            }
                            decoded = tex::BakeFromMemory(ti.name, bin.data(), bin.size(), &ti);
                        }
                    } else if (!uri.empty()) {
                        std::wstring wpath = img::Wide(baseDir + uri);
                        decoded = tex::BakeFromFile(ti.name, wpath, &ti);
                    }
                }
                if (!decoded) {
                    ti.ok = false;
                    ti.baked = false;
                }
                imageToTex.push_back((int)md.textures.size());
                md.textures.push_back(std::move(ti));
            }
        }
        // glTF texture index → image index 映射，回填材质 diffuseTex
        if (texturesJ && texturesJ->isArr()) {
            std::vector<int> texTexIdx;   // glTF texture -> ModelData textures
            for (size_t ti = 0; ti < texturesJ->arr.size(); ++ti) {
                int src = texturesJ->arr[ti].get("source") ? texturesJ->arr[ti].get("source")->integer(-1) : -1;
                int mapped = -1;
                if (src >= 0 && (size_t)src < imageToTex.size()) mapped = imageToTex[(size_t)src];
                texTexIdx.push_back(mapped);
            }
            for (auto& m : md.mesh.materials) {
                if (m.diffuseTex >= 0) {
                    if ((size_t)m.diffuseTex < texTexIdx.size()) m.diffuseTex = texTexIdx[(size_t)m.diffuseTex];
                    else m.diffuseTex = -1;
                }
            }
        } else {
            for (auto& m : md.mesh.materials) m.diffuseTex = -1;
        }
    }

    // ---- 蒙皮 ----
    const JsonValue* skinsJ = js.get("skins");
    // 骨骼：按 node 索引去重
    std::vector<int> nodeToBone(nodeCount, -1);
    // 递归 intern：先声明 std::function 再赋值，避免 auto 自引用（C7746）
    std::function<int(int)> internBone = [&](int nodeIdx) -> int {
        if (nodeIdx < 0 || (size_t)nodeIdx >= nodeCount) return -1;
        if (nodeToBone[(size_t)nodeIdx] >= 0) return nodeToBone[(size_t)nodeIdx];
        Bone b;
        const JsonValue& n = nodesJ->arr[(size_t)nodeIdx];
        b.name = n.get("name") ? n.get("name")->string("") : std::string();
        if (b.name.empty()) b.name = "bone" + std::to_string(nodeIdx);
        // 父：优先节点父链（skin 的 joints 通常构成子树）
        int p = parent[(size_t)nodeIdx];
        if (p >= 0) b.parent = nodeToBone[(size_t)p];   // 可能为 -1，后面补齐
        std::memcpy(b.local, local[(size_t)nodeIdx].data(), sizeof(float) * 16);
        MatIdentity(b.bind);
        int bi = (int)md.bones.size();
        md.bones.push_back(std::move(b));
        nodeToBone[(size_t)nodeIdx] = bi;
        // 父节点可能尚未 intern：递归确保父先建
        if (p >= 0 && md.bones[(size_t)bi].parent < 0) {
            int pb = internBone(p);
            md.bones[(size_t)bi].parent = pb;
        }
        return bi;
    };

    // ---- 网格 ----
    struct PrimJob {
        int meshIdx = 0;
        int nodeIdx = 0;
        int primIdx = 0;
        const JsonValue* prim = nullptr;
    };
    size_t totalVert = 0;
    std::vector<PrimJob> jobs;

    // 遍历场景节点
    {
        std::vector<int> roots;
        if (const JsonValue* sceneJ = js.get("scene")) {
            int sc = sceneJ->integer(0);
            if (const JsonValue* scenesJ = js.get("scenes")) {
                if (scenesJ->isArr() && sc >= 0 && (size_t)sc < scenesJ->arr.size()) {
                    if (const JsonValue* rn = scenesJ->arr[(size_t)sc].get("nodes")) {
                        if (rn->isArr()) for (const auto& r : rn->arr) roots.push_back(r.integer(-1));
                    }
                }
            }
        }
        if (roots.empty()) {
            for (size_t i = 0; i < nodeCount; ++i)
                if (parent[i] == -1) roots.push_back((int)i);
        }
        std::vector<int> stack(roots.rbegin(), roots.rend());
        std::vector<char> seen(nodeCount, 0);
        while (!stack.empty()) {
            int ni = stack.back();
            stack.pop_back();
            if (ni < 0 || (size_t)ni >= nodeCount || seen[(size_t)ni]) continue;
            seen[(size_t)ni] = 1;
            const JsonValue& n = nodesJ->arr[(size_t)ni];
            int meshIdx = n.get("mesh") ? n.get("mesh")->integer(-1) : -1;
            if (meshIdx >= 0 && (size_t)meshIdx < meshCount) {
                const JsonValue& mesh = meshesJ->arr[(size_t)meshIdx];
                if (const JsonValue* prims = mesh.get("primitives")) {
                    if (prims->isArr()) {
                        for (size_t pi = 0; pi < prims->arr.size(); ++pi) {
                            PrimJob j;
                            j.meshIdx = meshIdx;
                            j.nodeIdx = ni;
                            j.primIdx = (int)pi;
                            j.prim = &prims->arr[pi];
                            jobs.push_back(j);
                        }
                    }
                }
            }
            if (const JsonValue* ch = n.get("children")) {
                if (ch->isArr()) {
                    for (size_t k = 0; k < ch->arr.size(); ++k) {
                        int ci = ch->arr[k].integer(-1);
                        if (ci >= 0 && (size_t)ci < nodeCount) stack.push_back(ci);
                    }
                }
            }
        }
    }

    // 顶点上限（SPEC §9：2M 顶点 / 6M 索引）
    const size_t kMaxVertices = 2u * 1024 * 1024;
    const size_t kMaxIndices = 6u * 1024 * 1024;
    bool truncated = false;

    // 当前节点蒙皮信息
    struct SkinBinding {
        bool hasSkin = false;
        std::vector<int> jointsToBone;   // skin joint 序号 -> 骨骼下标
        std::vector<std::array<float, 16>> invBind;
        int meshNode = -1;
        std::array<float, 16> meshNodeWorld{};
    };

    for (const PrimJob& job : jobs) {
        const JsonValue& prim = *job.prim;
        int mode = prim.get("mode") ? prim.get("mode")->integer(4) : 4;
        const JsonValue* attr = prim.get("attributes");
        if (!attr || !attr->isObj()) continue;
        const JsonValue* posAccJ = attr->get("POSITION");
        if (!posAccJ) continue;
        int posAcc = posAccJ->integer(-1);
        if (posAcc < 0 || (size_t)posAcc >= accCount) continue;

        std::vector<float> pos;
        int posNc = 0;
        if (!ReadAccessor(ctx, posAcc, &pos, &posNc) || posNc < 3) {
            AppendWarning(&md.warning, "POSITION accessor 读取失败");
            continue;
        }
        size_t vcount = pos.size() / (size_t)posNc;
        if (vcount == 0) continue;
        if (totalVert + vcount > kMaxVertices) {
            truncated = true;
            break;
        }

        std::vector<float> nrm;
        int nrmNc = 0;
        if (const JsonValue* nJ = attr->get("NORMAL")) {
            int ni = nJ->integer(-1);
            if (ni >= 0 && (size_t)ni < accCount) ReadAccessor(ctx, ni, &nrm, &nrmNc);
        }
        std::vector<float> uv;
        int uvNc = 0;
        if (const JsonValue* uJ = attr->get("TEXCOORD_0")) {
            int ui = uJ->integer(-1);
            if (ui >= 0 && (size_t)ui < accCount) ReadAccessor(ctx, ui, &uv, &uvNc);
        }
        // 骨骼权重
        std::vector<float> joints, weights;
        int jNc = 0, wNc = 0;
        bool hasSkinAttr = false;
        if (const JsonValue* jJ = attr->get("JOINTS_0")) {
            int ji = jJ->integer(-1);
            if (ji >= 0 && (size_t)ji < accCount && ReadAccessor(ctx, ji, &joints, &jNc)) hasSkinAttr = true;
        }
        if (const JsonValue* wJ = attr->get("WEIGHTS_0")) {
            int wi = wJ->integer(-1);
            if (wi >= 0 && (size_t)wi < accCount) ReadAccessor(ctx, wi, &weights, &wNc);
        }

        // 索引
        std::vector<uint32_t> idx;
        bool haveIdx = false;
        if (const JsonValue* iJ = prim.get("indices")) {
            int ii = iJ->integer(-1);
            if (ii >= 0 && (size_t)ii < accCount) {
                if (ReadAccessorU32(ctx, ii, &idx)) haveIdx = true;
                else {
                    // 非 SCALAR 索引（罕见）：退回 float 读取
                    std::vector<float> fidx;
                    int fc = 0;
                    if (ReadAccessor(ctx, ii, &fidx, &fc) && fc >= 1) {
                        idx.resize(fidx.size() / (size_t)fc);
                        for (size_t k = 0; k < idx.size(); ++k) idx[k] = (uint32_t)fidx[k * (size_t)fc];
                        haveIdx = true;
                    }
                }
            }
        }
        if (!haveIdx) {
            idx.resize(vcount);
            for (size_t k = 0; k < vcount; ++k) idx[k] = (uint32_t)k;
        }
        // 索引越界防护
        for (uint32_t& v : idx)
            if (v >= vcount) v = 0;

        // ---- 图元组装为三角形列表 ----
        std::vector<uint32_t> tris;
        tris.reserve(idx.size());
        size_t n = idx.size();
        switch (mode) {
            case 4: {   // TRIANGLES
                for (size_t k = 0; k + 2 < n; k += 3) {
                    tris.push_back(idx[k]); tris.push_back(idx[k + 1]); tris.push_back(idx[k + 2]);
                }
                break;
            }
            case 5: {   // TRIANGLE_STRIP
                for (size_t k = 0; k + 2 < n; ++k) {
                    if (k & 1) { tris.push_back(idx[k + 1]); tris.push_back(idx[k]); tris.push_back(idx[k + 2]); }
                    else       { tris.push_back(idx[k]); tris.push_back(idx[k + 1]); tris.push_back(idx[k + 2]); }
                }
                break;
            }
            case 6: {   // TRIANGLE_FAN
                for (size_t k = 1; k + 1 < n; ++k) {
                    tris.push_back(idx[0]); tris.push_back(idx[k]); tris.push_back(idx[k + 1]);
                }
                break;
            }
            case 0: {   // POINTS → 各点生成极小三角形（无法显示点；跳过但计数）
                AppendWarning(&md.warning, "POINTS 图元已跳过");
                break;
            }
            case 1:     // LINES
            case 2:     // LINE_LOOP
            case 3: {   // LINE_STRIP
                AppendWarning(&md.warning, "线段图元已跳过");
                break;
            }
            default:
                AppendWarning(&md.warning, "未知图元 mode=" + std::to_string(mode));
                break;
        }
        if (tris.empty() && mode != 0 && mode != 1 && mode != 2 && mode != 3) continue;

        // ---- 蒙皮绑定（每个 node 首次遇到 skin 时建立骨骼表）----
        SkinBinding sb;
        const JsonValue& nodeVal = nodesJ->arr[(size_t)job.nodeIdx];
        int skinIdx = nodeVal.get("skin") ? nodeVal.get("skin")->integer(-1) : -1;
        if (skinIdx >= 0 && skinsJ && skinsJ->isArr() && (size_t)skinIdx < skinsJ->arr.size()) {
            const JsonValue& sk = skinsJ->arr[(size_t)skinIdx];
            const JsonValue* jointsJ = sk.get("joints");
            if (jointsJ && jointsJ->isArr()) {
                sb.hasSkin = true;
                for (const auto& jn : jointsJ->arr) sb.jointsToBone.push_back(internBone(jn.integer(-1)));
                // 逆绑定矩阵
                int ibm = sk.get("inverseBindMatrices") ? sk.get("inverseBindMatrices")->integer(-1) : -1;
                std::vector<float> ibmData;
                int ibmNc = 0;
                std::vector<std::array<float, 16>> inv(sb.jointsToBone.size());
                if (ibm >= 0 && (size_t)ibm < accCount && ReadAccessor(ctx, ibm, &ibmData, &ibmNc)) {
                    size_t rows = ibmData.size() / (size_t)std::max(1, ibmNc);
                    for (size_t k = 0; k < inv.size() && k < rows; ++k) {
                        if (ibmNc >= 16) {
                            float cm[16];
                            for (int c = 0; c < 16; ++c) cm[c] = ibmData[k * (size_t)ibmNc + (size_t)c];
                            MatFromColumnMajor(cm, inv[k].data());   // glTF 列主序 → 行优先
                        } else {
                            MatIdentity(inv[k].data());
                        }
                    }
                } else {
                    for (auto& m : inv) MatIdentity(m.data());
                }
                sb.invBind = std::move(inv);
                sb.meshNode = job.nodeIdx;
                std::memcpy(sb.meshNodeWorld.data(), world[(size_t)job.nodeIdx].data(), sizeof(float) * 16);
            }
        }

        // ---- 顶点写入（节点世界矩阵烘焙到顶点，与参考实现一致）----
        const float* nodew = world[(size_t)job.nodeIdx].data();
        uint32_t vbase = (uint32_t)md.mesh.vertices.size();
        md.mesh.vertices.reserve(md.mesh.vertices.size() + vcount);
        if (sb.hasSkin) md.skin.resize(md.skin.size() + vcount);

        for (size_t k = 0; k < vcount; ++k) {
            Vertex v;
            float p[3] = {pos[k * (size_t)posNc + 0], pos[k * (size_t)posNc + 1],
                          pos[k * (size_t)posNc + 2]};
            float wp[3];
            TransformPoint(nodew, p, wp);
            v.px = wp[0];
            v.py = wp[1];
            v.pz = wp[2];
            if (nrmNc >= 3 && (k * (size_t)nrmNc + 2) < nrm.size()) {
                float d[3] = {nrm[k * (size_t)nrmNc + 0], nrm[k * (size_t)nrmNc + 1],
                              nrm[k * (size_t)nrmNc + 2]};
                float wd[3];
                TransformDir(nodew, d, wd);
                float len = std::sqrt(wd[0] * wd[0] + wd[1] * wd[1] + wd[2] * wd[2]);
                if (len > 1e-12f) { wd[0] /= len; wd[1] /= len; wd[2] /= len; }
                v.nx = wd[0]; v.ny = wd[1]; v.nz = wd[2];
            } else {
                v.nx = v.ny = v.nz = 0.0f;   // 0 → 由渲染器按面法线补
            }
            if (uvNc >= 2 && (k * (size_t)uvNc + 1) < uv.size()) {
                v.u = uv[k * (size_t)uvNc + 0];
                v.v = uv[k * (size_t)uvNc + 1];
            } else {
                v.u = v.v = 0.0f;
            }
            md.mesh.vertices.push_back(v);

            if (sb.hasSkin) {
                SkinInfluences si;
                bool okW = false;
                if (hasSkinAttr && (k * (size_t)std::max(1, jNc) + 3) < joints.size() &&
                    (k * (size_t)std::max(1, wNc) + 3) < weights.size()) {
                    float wsum = 0.0f;
                    for (int c = 0; c < 4; ++c) {
                        int jnum = (int)joints[k * (size_t)jNc + (size_t)c];
                        float w = weights[k * (size_t)wNc + (size_t)c];
                        if (jnum < 0 || (size_t)jnum >= sb.jointsToBone.size()) w = 0.0f;
                        if (sb.jointsToBone.empty()) w = 0.0f;
                        int bone = sb.jointsToBone.empty() ? 0 : sb.jointsToBone[(size_t)std::max(0, std::min((int)sb.jointsToBone.size() - 1, jnum))];
                        si.boneIndex[c] = (uint32_t)std::max(0, bone);
                        si.weight[c] = w;
                        wsum += w;
                    }
                    if (wsum > 1e-6f) {
                        for (int c = 0; c < 4; ++c) si.weight[c] /= wsum;
                        okW = true;
                    }
                }
                if (!okW) {
                    si.boneIndex[0] = 0;
                    si.weight[0] = 1.0f;
                }
                md.skin[(size_t)vbase + k] = si;
            }
        }

        // 骨骼 bind：bind = inverseBind（行优先存储，供 skinMat = world * bind）
        if (sb.hasSkin) {
            for (size_t k = 0; k < sb.jointsToBone.size(); ++k) {
                int bi = sb.jointsToBone[k];
                if (bi >= 0 && (size_t)bi < md.bones.size() && k < sb.invBind.size()) {
                    std::memcpy(md.bones[(size_t)bi].bind, sb.invBind[k].data(), sizeof(float) * 16);
                }
            }
            if (md.skinRootBone < 0 && !sb.jointsToBone.empty()) md.skinRootBone = sb.jointsToBone[0];
        }

        // 索引偏移 + 子网格
        size_t idxStart = md.mesh.indices.size();
        if (idxStart + tris.size() > kMaxIndices) {
            truncated = true;
            break;
        }
        SubMesh sm;
        int matIdx = prim.get("material") ? prim.get("material")->integer(-1) : -1;
        if (matIdx >= 0 && (size_t)matIdx >= md.mesh.materials.size()) matIdx = -1;
        sm.materialIndex = matIdx;
        sm.indexOffset = (uint32_t)idxStart;
        sm.indexCount = (uint32_t)tris.size();
        {
            const JsonValue& mesh = meshesJ->arr[(size_t)job.meshIdx];
            std::string mn = mesh.get("name") ? mesh.get("name")->string("") : std::string("mesh" + std::to_string(job.meshIdx));
            if (mn.empty()) mn = "mesh" + std::to_string(job.meshIdx);
            sm.name = job.primIdx > 0 ? mn + "_" + std::to_string(job.primIdx) : mn;
        }
        md.mesh.indices.reserve(md.mesh.indices.size() + tris.size());
        for (uint32_t t : tris) md.mesh.indices.push_back(t + vbase);
        md.mesh.subMeshes.push_back(std::move(sm));
        totalVert += vcount;
    }
    if (truncated) AppendWarning(&md.warning, "网格超出上限已截断");

    // ---- 修正骨骼父链（节点父可能不在 joints 里 → 用祖先 joints 作父）----
    for (size_t bi = 0; bi < md.bones.size(); ++bi) {
        if (md.bones[bi].parent >= 0) continue;
        // 找节点
        int nodeIdx = -1;
        for (size_t ni = 0; ni < nodeCount; ++ni)
            if (nodeToBone[ni] == (int)bi) { nodeIdx = (int)ni; break; }
        if (nodeIdx < 0) continue;
        int p = parent[(size_t)nodeIdx];
        int guard = 0;
        while (p >= 0 && guard++ < 4096) {
            if (nodeToBone[(size_t)p] >= 0) { md.bones[bi].parent = nodeToBone[(size_t)p]; break; }
            p = parent[(size_t)p];
        }
    }

    md.boneCount = (uint32_t)md.bones.size();
    md.vertexCount = (uint32_t)md.mesh.vertices.size();
    md.triangleCount = (uint32_t)(md.mesh.indices.size() / 3);

    // ---- 动画 ----
    const JsonValue* animsJ = js.get("animations");
    if (animsJ && animsJ->isArr()) {
        for (size_t ai = 0; ai < animsJ->arr.size(); ++ai) {
            const JsonValue& anim = animsJ->arr[ai];
            Clip clip;
            clip.name = anim.get("name") ? anim.get("name")->string("") : std::string("clip" + std::to_string(ai));
            NormalizeName(&clip.name);
            clip.tracks.resize(md.bones.size());

            const JsonValue* chans = anim.get("channels");
            const JsonValue* samplers = anim.get("samplers");
            if (!chans || !chans->isArr() || !samplers || !samplers->isArr()) {
                md.clips.push_back(std::move(clip));
                continue;
            }
            // 骨骼下标 → 该骨骼的所有通道（T/R/S）
            struct BoneChans {
                std::vector<TrackSampler> ts, rs, ss;
            };
            std::vector<BoneChans> perBone(md.bones.size());
            double dur = 0.0;
            for (const auto& ch : chans->arr) {
                const JsonValue* tgt = ch.get("target");
                if (!tgt || !tgt->isObj()) continue;
                int nodeIdx = tgt->get("node") ? tgt->get("node")->integer(-1) : -1;
                std::string path = tgt->get("path") ? tgt->get("path")->string("") : std::string();
                if (nodeIdx < 0 || (size_t)nodeIdx >= nodeCount) continue;
                int bi = nodeToBone[(size_t)nodeIdx];
                if (bi < 0) continue;
                if (path != "translation" && path != "rotation" && path != "scale" && path != "weights") continue;
                int si = ch.get("sampler") ? ch.get("sampler")->integer(-1) : -1;
                if (si < 0 || (size_t)si >= samplers->arr.size()) continue;
                const JsonValue& smpJ = samplers->arr[(size_t)si];
                int inAcc = smpJ.get("input") ? smpJ.get("input")->integer(-1) : -1;
                int outAcc = smpJ.get("output") ? smpJ.get("output")->integer(-1) : -1;
                if (inAcc < 0 || outAcc < 0) continue;
                std::vector<float> timesF;
                int tNc = 0;
                std::vector<float> vals;
                int vNc = 0;
                if (!ReadAccessor(ctx, inAcc, &timesF, &tNc) || tNc < 1) continue;
                if (!ReadAccessor(ctx, outAcc, &vals, &vNc) || vNc < 1) continue;
                TrackSampler ts;
                ts.path = path;
                ts.interp = smpJ.get("interpolation") ? smpJ.get("interpolation")->string("LINEAR") : std::string("LINEAR");
                ts.nComp = vNc;
                ts.times.reserve(timesF.size());
                for (float t : timesF) ts.times.push_back((double)t);
                ts.values = std::move(vals);
                if (ts.interp == "CUBICSPLINE") {
                    // CUBICSPLINE 输出是 (inTangent, value, outTangent) 三元组，取中间值为 value
                    std::vector<float> compact;
                    size_t frames = ts.times.size();
                    compact.reserve(frames * (size_t)vNc);
                    for (size_t f = 0; f < frames; ++f) {
                        size_t base = (f * 3 + 1) * (size_t)vNc;
                        for (int c = 0; c < vNc; ++c) compact.push_back(base + (size_t)c < ts.values.size() ? ts.values[base + (size_t)c] : 0.0f);
                    }
                    ts.values = std::move(compact);
                    ts.interp = "LINEAR";   // 用线性近似（缩略图/预览足够）
                }
                std::vector<double>& tv = ts.times;
                if (!tv.empty()) dur = std::max(dur, tv.back());
                if (path == "translation") perBone[(size_t)bi].ts.push_back(std::move(ts));
                else if (path == "rotation") perBone[(size_t)bi].rs.push_back(std::move(ts));
                else if (path == "scale") perBone[(size_t)bi].ss.push_back(std::move(ts));
            }
            clip.duration = dur;
            // 生成每个骨骼的关键帧序列（按所有通道时间的并集采样）
            for (size_t bi = 0; bi < md.bones.size(); ++bi) {
                BoneChans& bc = perBone[bi];
                if (bc.ts.empty() && bc.rs.empty() && bc.ss.empty()) continue;
                std::vector<double> times;
                for (const auto* v : {&bc.ts, &bc.rs, &bc.ss})
                    for (const auto& s : *v)
                        for (double t : s.times) times.push_back(t);
                std::sort(times.begin(), times.end());
                times.erase(std::unique(times.begin(), times.end(),
                                        [](double a, double b) { return std::fabs(a - b) < 1e-7; }),
                            times.end());
                if (times.empty()) continue;
                // 限制单骨骼关键帧数，防止超长 clip 撑爆内存
                const size_t kMaxKeys = 512;
                if (times.size() > kMaxKeys) {
                    std::vector<double> reduced;
                    reduced.reserve(kMaxKeys);
                    for (size_t k = 0; k < kMaxKeys; ++k)
                        reduced.push_back(times[k * (times.size() - 1) / (kMaxKeys - 1)]);
                    times = std::move(reduced);
                }
                const Bone& bone = md.bones[bi];
                std::vector<KeyFrame>& track = clip.tracks[bi];
                track.reserve(times.size());
                for (double t : times) {
                    KeyFrame kf;
                    kf.time = t;
                    float q[4] = {0, 0, 0, 1}, tt[3] = {0, 0, 0}, ss[3] = {1, 1, 1};
                    bool h;
                    for (const auto& s : bc.rs) { SampleTRS(s, t, q, &h); break; }
                    for (const auto& s : bc.ts) { SampleTRS(s, t, tt, &h); break; }
                    for (const auto& s : bc.ss) { SampleTRS(s, t, ss, &h); break; }
                    // 未提供通道的用绑定姿态（节点局部）反推
                    if (bc.ts.empty()) { tt[0] = bone.local[12]; tt[1] = bone.local[13]; tt[2] = bone.local[14]; }
                    ComposeTRS(tt, q, ss, kf.local);
                    track.push_back(kf);
                }
            }
            md.clips.push_back(std::move(clip));
        }
    }
    md.clipCount = (uint32_t)md.clips.size();

    md.ok = true;
    md.error.clear();
    return true;
}

// ---------------------------------------------------------------------------
// 按扩展名分派（model_data.h 声明；本模块是「加载」这一职责的入口）
// ---------------------------------------------------------------------------
bool LoadModel(const std::string& utf8Path, ModelData* out) {
    if (!out) return false;
    // 从 UTF-8 路径取扩展名（纯 ASCII 比较，无需转宽字符）
    std::string ext;
    size_t dot = utf8Path.find_last_of('.');
    size_t sep = utf8Path.find_last_of("/\\");
    if (dot != std::string::npos && (sep == std::string::npos || dot > sep)) {
        ext = utf8Path.substr(dot + 1);
        for (size_t i = 0; i < ext.size(); ++i) {
            char c = ext[i];
            if (c >= 'A' && c <= 'Z') ext[i] = (char)(c - 'A' + 'a');
        }
    }
    if (ext == "glb" || ext == "gltf") return LoadGLB(utf8Path, out);
    if (ext == "fbx") return LoadFBX(utf8Path, out);
    // 未知扩展名：交给 GLB（它能从文件头判断并给出明确 error）
    bool r = LoadGLB(utf8Path, out);
    if (!r && out->error.empty()) out->error = "不支持的扩展名";
    return r;
}

} // namespace av
