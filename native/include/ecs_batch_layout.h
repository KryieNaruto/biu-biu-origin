// =============================================================================
//  ecs_batch_layout.h —— IEcsBatchSolver 的 C ABI 布局契约（POD，仅裸内存）
// =============================================================================
//
//  对应文档：docs/architecture/ECS.md §9「性能路线 / C++ 批量求解器」与 §5.3「chunk 列式存储」
//  对应规则：docs/rules/02-tech-selection.md §4.2「C# <-> C++ 边界契约」、§2.2
//
//  设计要点（为什么这样定义）
//  -------------------------------------------------------------------------
//  1) **零拷贝**：本头文件只描述"一块连续内存长什么样"，不描述"谁来分配"。
//     指针由 C# 侧（chunk 的列存缓冲）给出，C++ 只读写，不持有、不释放。
//  2) **禁止跨帧持有**：本文件里没有任何托管句柄/对象引用字段，
//     调用方必须在同一帧、同一调用内传完用完（02 §4.2「生命周期」）。
//  3) **纯 C 布局**：所有结构体满足 static_assert 的 offsetof/sizeof 断言，
//     保证 x86_64 上 C# `[StructLayout(LayoutKind.Sequential)]` 的平移是确定的。
//  4) **粒度自由**：接口只有一个 `count`，调用方可以
//        - 传整个 chunk（ECS.md §5.3 的「每个 chunk 回调一次」，count = chunk 有效实体数），或
//        - 传整个原型（跨多个 chunk 的整批）。
//     两种粒度都用同一份布局，C++ 侧无分支、无额外拷贝。
//
//  注意：本头文件**不引入**任何 Godot 头文件，可被纯 C++/C# 侧共同引用。
// =============================================================================

#ifndef BIU_BIU_ECS_BATCH_LAYOUT_H
#define BIU_BIU_ECS_BATCH_LAYOUT_H

#include <cstddef>
#include <cstdint>

// 结构体按 4/8 字节对齐打包；用显式 pack 防止不同编译器的默认对齐差异。
#pragma pack(push, 8)

/// 单个抛射物位置 / 速度的 POD。与 ECS 组件 `Projectile.Position` / `.Velocity` 同构，
/// 但**字段名与顺序是 ABI 契约**：改这里 = 破坏性变更（ECS.md §11 第 3 条）。
struct BiuVec3f {
    float x;
    float y;
    float z;
};

/// 单条命中结果。`outHits` 缓冲区的**元素**，stride = sizeof(BiuHitRecord)。
struct BiuHitRecord {
    /// 命中发生的实体下标（相对于本次调用的起始元素，[0, count)）。
    int32_t src_index;
    /// 被命中方实体下标；由调用方负责把它换算到全局实体（本结构不携带 chunk 基址）。
    int32_t dst_index;
    /// 命中点到射线起点的行进距离（世界单位）；用于排序取最近命中。
    float travel_distance;
    /// 命中面法线（世界空间）。
    BiuVec3f normal;

    // 保留字段：保持 stride 为 4 的倍数、便于将来加标记位而不破 ABI。
    uint32_t flags;
};

#pragma pack(pop)

// ---------------------------------------------------------------------------
// 错误码：`IEcsBatchSolver.solve_projectiles` 返回 **命中数**；负值 = 错误码。
// 对应 ECS.md §9 契约「返回为命中数（负值 = 错误码）」。
// ---------------------------------------------------------------------------
enum BiuBatchSolverError : int32_t {
    BIU_BATCH_OK = 0,
    BIU_BATCH_ERR_NULL_ARG = -1,      // positions / velocities / out_hits 为空而 count > 0
    BIU_BATCH_ERR_BAD_COUNT = -2,     // count < 0，或 count 超出 out_hits 容量
    BIU_BATCH_ERR_OUT_CAPACITY = -3,  // out_hits 容量不足，无法容纳命中
    BIU_BATCH_ERR_NOT_IMPLEMENTED = -4 // 本轮骨架：求解器故意返回此码
};

// ---------------------------------------------------------------------------
// 编译期布局断言：C# 侧 `Marshal.SizeOf` / `sizeof` 必须与下列值逐字一致。
// 改任何一个 `static_assert` 失败 = 立刻在编译期暴露 ABI 漂移，而不是运行时踩内存。
// ---------------------------------------------------------------------------
static_assert(sizeof(BiuVec3f) == 12, "BiuVec3f must be 12 bytes (3 x float)");
// 实测（MSVC 14.51 / x86_64）：28 字节 = 8 + 4 + 12 + 4，自然按 4 对齐，无尾部填充。
// C# 侧 [StructLayout(LayoutKind.Sequential)] 的 Marshal.SizeOf 必须同为 28。
static_assert(sizeof(BiuHitRecord) == 28, "BiuHitRecord must be 28 bytes");
static_assert(offsetof(BiuHitRecord, src_index) == 0, "BiuHitRecord.src_index offset");
static_assert(offsetof(BiuHitRecord, dst_index) == 4, "BiuHitRecord.dst_index offset");
static_assert(offsetof(BiuHitRecord, travel_distance) == 8, "BiuHitRecord.travel_distance offset");
static_assert(offsetof(BiuHitRecord, normal) == 12, "BiuHitRecord.normal offset");
static_assert(offsetof(BiuHitRecord, flags) == 24, "BiuHitRecord.flags offset");

#endif // BIU_BIU_ECS_BATCH_LAYOUT_H
