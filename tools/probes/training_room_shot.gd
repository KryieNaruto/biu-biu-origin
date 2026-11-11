extends SceneTree
# 鍙鎺㈤拡锛氳缁冩埧闂达紙鐏版埧 + 浜虹墿 + 鍋囦汉锛夊湪銆屾湁娓叉煋銆嶇殑鎯呭喌涓嬬殑鏍峰瓙銆?# 鐢ㄦ硶锛堝繀椤荤敤甯︾獥鍙ｇ殑寮曟搸锛宧eadless 涓嶅嚭鍥撅級锛?#   Godot_v4.7.2-stable_mono_win64.exe --path <椤圭洰> --script tools/probes/training_room_shot.gd
# 杈撳嚭锛歶ser://training_room_shot.png锛堝悓鏃舵墦鍗拌矾寰勶級

var _frames := 0


func _init() -> void:
    var packed: PackedScene = load("res://scenes/debug/TrainingRoom.tscn")
    var root: Node = packed.instantiate()
    get_root().add_child(root)
    current_scene = root


func _process(_delta: float) -> bool:
    _frames += 1
    if _frames < 40:
        return false
    var img := get_root().get_texture().get_image()
    var path := "res://out/plan_review/training_room_shot.png"
    var err := img.save_png(path)
    print("[shot] ", ProjectSettings.globalize_path(path), " err=", err, " size=", img.get_size())
    quit(0)
    return true

