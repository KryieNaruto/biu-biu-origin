#include "ecs_batch_solver_stub.h"

#include "ecs_batch_layout.h"

#include <godot_cpp/core/error_macros.hpp>

namespace biu {

using namespace godot;

// ABI 版本：布局契约变更时必须 +1，C# 侧据此拒绝不匹配的 dll。
// 本轮为 1（首次落地）。
static const int32_t BIU_NATIVE_ABI_VERSION = 1;

void NativeEcsBatchSolverStub::_bind_methods() {
    ClassDB::bind_method(D_METHOD("get_abi_version"), &NativeEcsBatchSolverStub::get_abi_version);
    ClassDB::bind_method(D_METHOD("get_hit_record_stride"), &NativeEcsBatchSolverStub::get_hit_record_stride);
    ClassDB::bind_method(D_METHOD("is_implemented"), &NativeEcsBatchSolverStub::is_implemented);
    ClassDB::bind_method(
            D_METHOD("solve_projectiles_stub", "positions", "velocities", "count", "dt", "out_hits", "out_capacity"),
            &NativeEcsBatchSolverStub::solve_projectiles_stub);
}

int32_t NativeEcsBatchSolverStub::get_abi_version() const {
    return BIU_NATIVE_ABI_VERSION;
}

int32_t NativeEcsBatchSolverStub::get_hit_record_stride() const {
    return static_cast<int32_t>(sizeof(BiuHitRecord));
}

bool NativeEcsBatchSolverStub::is_implemented() const {
    return false;
}

int32_t NativeEcsBatchSolverStub::solve_projectiles_stub(int64_t p_positions, int64_t p_velocities, int32_t p_count,
        double p_dt, int64_t p_out_hits, int32_t p_out_capacity) const {
    // 只做契约校验，不做任何求解 —— 返回值如实反映"未实现"。
    if (p_count < 0) {
        return BIU_BATCH_ERR_BAD_COUNT;
    }
    if (p_count > 0 && (p_positions == 0 || p_velocities == 0)) {
        return BIU_BATCH_ERR_NULL_ARG;
    }
    if (p_out_hits == 0 && p_out_capacity > 0) {
        return BIU_BATCH_ERR_NULL_ARG;
    }
    if (p_out_capacity < 0) {
        return BIU_BATCH_ERR_OUT_CAPACITY;
    }
    (void)p_dt; // 求解未实现，dt 暂不参与计算。
    return BIU_BATCH_ERR_NOT_IMPLEMENTED;
}

} // namespace biu
