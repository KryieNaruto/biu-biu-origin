// =============================================================================
//  ecs_batch_solver_stub.h —— ClassDB 里的空壳类（本轮只验证编译与装载通道）
// =============================================================================
//
//  为什么注册一个"空类"而不是直接实现求解：
//    ECS.md §9 路线 C（C++ 批量求解）**当前不满足触发条件**，按 02 §2.1「下沉流程」
//    第 1~3 步必须先有 C# 侧实测数据。本轮只打通"编译 -> 装载 -> ClassDB 可见"的通道，
//    业务求解留待实测超标后再落地。所以这里的 solve_* 一律返回
//    BIU_BATCH_ERR_NOT_IMPLEMENTED，**不伪造任何计算结果**。
// =============================================================================

#ifndef BIU_BIU_ECS_BATCH_SOLVER_STUB_H
#define BIU_BIU_ECS_BATCH_SOLVER_STUB_H

#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/core/class_db.hpp>

namespace biu {

/// GDExtension 侧的求解器空壳。命名遵守 02 §2.2「导出给 C# 的类统一 Native 前缀」。
class NativeEcsBatchSolverStub : public godot::RefCounted {
    GDCLASS(NativeEcsBatchSolverStub, godot::RefCounted)

protected:
    static void _bind_methods();

public:
    NativeEcsBatchSolverStub() = default;
    ~NativeEcsBatchSolverStub() override = default;

    /// ABI 版本号：C# 侧启动时读它做版本握手，避免"dll 与 C# 封装不匹配"静默错算。
    int32_t get_abi_version() const;

    /// BiuHitRecord 的 stride（字节）。C# 侧用它校验布局，不匹配立即失败。
    int32_t get_hit_record_stride() const;

    /// 是否已实现真实求解。本轮恒为 false —— 调用方据此走 C# 回退路径。
    bool is_implemented() const;

    /// 占位求解入口：形状与 ECS.md §9 契约一致，但只做参数校验后返回
    /// BIU_BATCH_ERR_NOT_IMPLEMENTED，绝不返回伪造的命中数。
    int32_t solve_projectiles_stub(int64_t p_positions, int64_t p_velocities, int32_t p_count,
            double p_dt, int64_t p_out_hits, int32_t p_out_capacity) const;
};

} // namespace biu

#endif // BIU_BIU_ECS_BATCH_SOLVER_STUB_H
