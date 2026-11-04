// =============================================================================
//  register_types.cpp —— GDExtension 入口（库初始化 / 反初始化）
// =============================================================================
//  entry_symbol 必须与 addons/biu_biu_native/biu_biu_native.gdextension 的
//  `[configuration] entry_symbol` 逐字一致：biu_biu_library_init
// =============================================================================

#include "ecs_batch_solver_stub.h"

#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/godot.hpp>

using namespace godot;

static void initialize_biu_biu_module(ModuleInitializationLevel p_level) {
    // 只在 SCENE 级注册：本扩展不含编辑器工具，无需 EDITOR 级。
    if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) {
        return;
    }
    GDREGISTER_CLASS(biu::NativeEcsBatchSolverStub);
}

static void uninitialize_biu_biu_module(ModuleInitializationLevel p_level) {
    if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) {
        return;
    }
    // ClassDB 在关闭时会自行清理已注册类型，这里无需手动反注册。
}

extern "C" {

GDExtensionBool GDE_EXPORT biu_biu_library_init(GDExtensionInterfaceGetProcAddress p_get_proc_address,
        GDExtensionClassLibraryPtr p_library, GDExtensionInitialization *r_initialization) {
    GDExtensionBinding::InitObject init_obj(p_get_proc_address, p_library, r_initialization);

    init_obj.register_initializer(initialize_biu_biu_module);
    init_obj.register_terminator(uninitialize_biu_biu_module);
    init_obj.set_minimum_library_initialization_level(MODULE_INITIALIZATION_LEVEL_SCENE);

    return init_obj.init();
}

} // extern "C"
