extends SceneTree
## 只读探针（AGENTS.md §4 白名单 `tools/probes/**`，GDScript 在此允许）：
## 验证「拖动/设置窗口尺寸」是否真的改变了 3D 视口大小。
##
## 用法（**必须带窗口**，不能加 --headless，因为没有 DisplayServer 就测不了窗口）：
##   & "E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe" --path . \
##       --script tools/probes/viewport_resize_probe.gd
##
## 为什么需要它：project.godot 里 stretch 写错**不会报任何错**，只会让 3D 画面在窗口
## 变化时被缩放而不是重新渲染。这种 bug 编译、日志、架构守卫都发现不了，
## 只能靠「设一个尺寸 → 读回视口尺寸 → 比对」来证明。
##
## 判据：每次设置窗口尺寸后，视口可见区必须**等于**该尺寸。
## 若视口恒为某个固定值（如 1152×648），说明 stretch 把内容钉在了基准分辨率上。

const SIZES := [
	Vector2i(1280, 720),
	Vector2i(1920, 1080),
	Vector2i(1280, 1024),
	Vector2i(800, 450),
]

var _i := 0
var _fail := 0


func _initialize() -> void:
	if DisplayServer.get_name() == "headless":
		print("[probe] headless 模式：无窗口可测，已中止（去掉 --headless 重跑）")
		quit(2)
		return
	print("[probe] 开始：窗口尺寸 → 视口尺寸")
	_tick()


func _tick() -> void:
	if _i >= SIZES.size():
		print("[probe] 结束：%d 个尺寸，%d 个失败" % [SIZES.size(), _fail])
		quit(0 if _fail == 0 else 1)
		return
	var want: Vector2i = SIZES[_i]
	DisplayServer.window_set_size(want)
	# 窗口尺寸异步生效，等下一帧再读（等 2 帧更稳，避免读到中间态）
	await process_frame
	await process_frame
	_report(want)
	_i += 1
	_tick()


func _report(want: Vector2i) -> void:
	var win := DisplayServer.window_get_size()
	var vp := root.get_visible_rect().size
	var vp_i := Vector2i(int(vp.x), int(vp.y))
	var win_ok := win == want
	var vp_ok := vp_i == win
	if not vp_ok:
		_fail += 1
	print("[probe] want=%s window=%s viewport=%s 窗口%s 视口%s" % [
		want, win, vp_i,
		"OK" if win_ok else "偏差",
		"联动OK" if vp_ok else "未联动FAIL",
	])
	if not vp_ok:
		push_error("[probe] 视口没有跟随窗口：window=%s viewport=%s（检查 project.godot 的 window/stretch/mode）" % [win, vp_i])
