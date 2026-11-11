extends SceneTree
# 只读探针：训练房 / 地牢调试场景里「真正可见」的角色视图有几个（池里挂着的空壳不算）。
#
# 用法：
#   Godot_v4.7.2-stable_mono_win64_console.exe --headless --path <项目> --script tools/probes/roster_probe.gd -- --scene=res://scenes/debug/TrainingRoom.tscn
# 输出 "[probe] ..." 行；不改仓库、不写 user://。

var _scene_path := "res://scenes/debug/TrainingRoom.tscn"
var _frames := 0
var _root: Node = null


func _init() -> void:
    for a in OS.get_cmdline_user_args():
        if a.begins_with("--scene="):
            _scene_path = a.substr("--scene=".length())
    var packed: PackedScene = load(_scene_path)
    if packed == null:
        print("[probe] 加载失败：", _scene_path)
        quit(2)
        return
    _root = packed.instantiate()
    get_root().add_child(_root)
    current_scene = _root
    print("[probe] 场景=", _scene_path)


func _process(_delta: float) -> bool:
    _frames += 1
    if _frames < 30:
        return false
    var views := _root.get_node_or_null("Views")
    var shown := 0
    var ghosts := 0
    if views == null:
        print("[probe] 没有 Views 根节点")
    else:
        for c in views.get_children():
            if c is Node3D and (c as Node3D).visible:
                shown += 1
                print("[probe] 可见视图: ", c.name, " @", (c as Node3D).global_position)
    print("[probe] 结论：可见角色视图=", shown, " / 池节点=", 0 if views == null else views.get_child_count())
    quit(0)
    return true
