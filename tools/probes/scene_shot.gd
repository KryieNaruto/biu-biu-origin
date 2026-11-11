extends SceneTree
## 只读探针：把真实场景按指定窗口尺寸渲染出来并存 PNG，用于「肉眼 + 像素」对比。
## 用法：
##   Godot ... --script tools/probes/scene_shot.gd -- <场景res> <宽> <高> <输出png>

var _scene: Node


func _initialize() -> void:
	var a := OS.get_cmdline_user_args()
	var path := a[0] if a.size() > 0 else "res://scenes/debug/DungeonMap2D.tscn"
	var w := int(a[1]) if a.size() > 1 else 1280
	var h := int(a[2]) if a.size() > 2 else 720
	var out := a[3] if a.size() > 3 else "user://shot.png"
	DisplayServer.window_set_size(Vector2i(w, h))
	_scene = load(path).instantiate()
	root.add_child(_scene)
	_go(out, w, h)


func _go(out: String, w: int, h: int) -> void:
	# 等几帧让布局/容器/绘制稳定
	for i in 12:
		await process_frame
	await process_frame
	var img := root.get_viewport().get_texture().get_image()
	var err := img.save_png(out)
	var vp := root.get_visible_rect().size
	print("[shot] %dx%d viewport=%s -> %s (err=%d)" % [w, h, vp, out, err])
	quit(0)
