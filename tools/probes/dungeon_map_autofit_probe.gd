extends SceneTree

## 只读探针：验证 DungeonMap2D 的「拖动窗口 → 地图自动缩放铺满」。
##
## 为什么需要它：格子像素是 C# 里按父容器尺寸算出来的，配错不会报任何错，
## 只表现为「窗口拉大了图还是那么大」，编译/日志/架构守卫都发现不了。
## 这里直接改真实窗口尺寸，再读回视图的 CellPixels 是否随之变化。

const SCENE := "res://scenes/debug/DungeonMap2D.tscn"

var _failures := 0


func _initialize() -> void:
	if OS.has_feature("headless"):
		print("[probe] headless 模式没有真实窗口，跳过")
		quit(2)
		return
	_run()


func _run() -> void:
	var packed: PackedScene = load(SCENE)
	if packed == null:
		print("[probe] 无法加载场景 ", SCENE)
		quit(2)
		return
	var scene: Node = packed.instantiate()
	root.add_child(scene)
	await process_frame
	await process_frame

	var view: Node2D = scene.get_node("Layout/MapScroll/MapView")
	var scroll: Control = scene.get_node("Layout/MapScroll")

	# 场景打开时不会自动生成地图，这里显式生成一张，否则 View 没有可铺满的内容。
	var panel: Node = scene.get_node("Layout/Panel")
	var gen_call := panel.get_method_list().filter(
		func(m): return String(m["name"]).to_lower().contains("build") or String(m["name"]).to_lower().contains("gen"))
	if gen_call.is_empty():
		print("[probe] 面板上没有找到生成入口")
		quit(2)
		return
	panel.call(gen_call[0]["name"])
	await process_frame
	await process_frame

	var map_w: int = int(view.get("MapWidth"))
	var map_h: int = int(view.get("MapHeight"))
	if map_w <= 0 or map_h <= 0:
		print("[probe] 地图没生成（", map_w, "x", map_h, "）")
		quit(2)
		return
	print("[probe] 地图 %dx%d 格" % [map_w, map_h])

	var sizes := [Vector2i(1280, 720), Vector2i(1920, 1080), Vector2i(800, 450), Vector2i(1600, 900)]
	var prev := -1.0
	var samples := []
	for s in sizes:
		DisplayServer.window_set_size(s)
		await process_frame
		await process_frame
		await process_frame
		var cell := float(view.get("CellPixels"))
		var need_w := cell * float(map_w)
		var fit_w := need_w <= scroll.size.x + 1.0
		var fit_h := cell * float(map_h) <= scroll.size.y + 1.0
		var fits: bool = fit_w and fit_h
		if not fits:
			_failures += 1
		print("[probe] 窗口=%s 可视区=%s 格子=%.2fpx 图=%.0fx%.0f 装得下=%s"
			% [s, scroll.size, cell, need_w, cell * float(map_h), fits])
		samples.append(cell)
		prev = cell

	# 大窗口里的格子必须比小窗口里大，否则就是「没跟着变」。
	if samples[1] <= samples[3]:
		print("[probe] 1920x1080 的格子没有比 1600x900 更大：", samples)
		_failures += 1
	if samples[2] >= samples[0]:
		print("[probe] 800x450 的格子没有比 1280x720 更小：", samples)
		_failures += 1

	print("[probe] 结束：%d 个尺寸，%d 个失败" % [sizes.size(), _failures])
	quit(1 if _failures > 0 else 0)
