extends SceneTree
## 只读探针：把**真实场景**加载进来，测「窗口变大 → 3D 相机画面是否真的变大」。
##
## 与 viewport_resize_probe.gd 的区别：那个只测空场景的视口尺寸，
## 测不出「相机投影/KeepAspect/overlay 把画面钉死」这类问题。
##
## 用法（必须带窗口）：
##   & "E:\Godot\...\Godot_v4.7.2-stable_mono_win64_console.exe" --path . \
##       --script tools/probes/scene_window_resize_probe.gd -- <场景res路径>

const SIZES := [Vector2i(1280, 720), Vector2i(1920, 1080)]

var _scene_path := "res://scenes/debug/DungeonMap2D.tscn"
var _scene: Node = null
var _i := 0


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() > 0:
		_scene_path = args[0]
	print("[probe] 场景 = %s" % _scene_path)
	_scene = load(_scene_path).instantiate()
	root.add_child(_scene)
	_dump_cameras("初始")
	_tick()


func _dump_cameras(tag: String) -> void:
	var cams := _find(_scene, [])
	print("[probe] %s 相机数=%d" % [tag, cams.size()])
	for c in cams:
		var cam: Camera3D = c
		print("    %s current=%s proj=%s size=%.2f keepAspect=%d fov=%.1f" % [
			cam.name, cam.current, cam.projection,
			cam.size, cam.keep_aspect, cam.fov])


func _find(n: Node, out: Array) -> Array:
	if n is Camera3D:
		out.append(n)
	for c in n.get_children():
		_find(c, out)
	return out


func _tick() -> void:
	if _i >= SIZES.size():
		quit(0)
		return
	var want: Vector2i = SIZES[_i]
	DisplayServer.window_set_size(want)
	await process_frame
	await process_frame
	await process_frame
	var vp := root.get_visible_rect().size
	var win := DisplayServer.window_get_size()
	var cam := root.get_viewport().get_camera_3d()
	var cam_str := "<无相机>"
	if cam != null:
		cam_str = "proj=%s size=%.2f keepAspect=%d fov=%.1f" % [
			cam.projection, cam.size, cam.keep_aspect, cam.fov]
	print("[probe] want=%s window=%s viewport=%s 当前相机=%s" % [want, win, vp, cam_str])
	_i += 1
	_tick()
