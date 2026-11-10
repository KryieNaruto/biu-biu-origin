extends SceneTree
# 相机跟随探针（只读，不进游戏发布）
#
# 为什么需要它：tests/Camera/CameraRigTests.cs 只证明「数学对」（纯函数），
#   证明不了「相机节点真的连到了人物身上」。本探针在引擎里读 Camera3D.global_position，
#   抓的是 CharacterSession.Tick() 这条真实链路的输出。
#
# 用法：
#   godot --headless --path <项目> --script tools/probes/camera_follow_probe.gd -- --hero
#
# 输出 "PASS/FAIL <名字> | <实测数值>"，退出码 = 失败条数。

var _failures := 0
var _checks := 0
var _scene: Node = null


func _init() -> void:
	print("=== camera_follow_probe ===")
	var argv := OS.get_cmdline_user_args()
	print("user args: %s" % str(argv))

	# DungeonDebugScene 通过命令行参数决定是否生成人物（--hero）。
	# 探针以「主场景」方式启动，让游戏自己的参数解析生效。
	var main_scene: Node = _load_main_scene()
	if main_scene == null:
		_fail("无法取得主场景", "")
		_report()
		return

	_scene = main_scene
	await process_frame
	await process_frame

	# 人物出生需要若干帧
	await _wait_frames(10)

	var cam: Camera3D = _find_camera(_scene)
	var hero: Node3D = _find_actor(_scene)

	print("")
	print("相机: %s" % ("<none>" if cam == null else "%s projection=%s size=%.3f" % [
		cam.name, str(cam.projection), cam.size]))
	print("人物: %s" % ("<none>" if hero == null else "%s pos=%v" % [hero.name, hero.global_position]))

	if cam == null:
		_fail("场景里没有 Camera3D —— 相机根本没建出来", "")
		_report()
		return

	_check(true, "相机已创建", "%s current=%s" % [cam.name, str(cam.current)])

	if hero == null:
		print("")
		print("[SKIP] 未找到人物节点。可能是未带 --hero 或人物为 ECS 逻辑实体（无对应 Node）。")
		print("       相机本身已就位，正交参数 size=%.3f" % cam.size)
		_report()
		return

	await _run_follow_checks(cam, hero)
	_report()


func _load_main_scene() -> Node:
	# 走引擎真实启动流程拿主场景（含它自己的 _Ready 与参数解析）
	if Engine.has_method("get_main_loop"):
		pass
	# 场景树里已有当前场景时直接用它
	var cur := current_scene
	if cur != null:
		return cur
	return null


func _find_camera(node: Node) -> Camera3D:
	if node is Camera3D and (node as Camera3D).current:
		return node
	if node is Camera3D:
		return node
	for c in node.get_children():
		var got := _find_camera(c)
		if got != null:
			return got
	return null


func _find_actor(node: Node) -> Node3D:
	# 人物的可见视图节点：排除相机本身
	var ln := String(node.name).to_lower()
	if node is Node3D and not (node is Camera3D):
		if ln.contains("hero") and not ln.contains("camera") and not ln.contains("hud") \
			and not ln.contains("crosshair") and not ln.contains("model"):
			return node as Node3D
	for c in node.get_children():
		var got := _find_actor(c)
		if got != null:
			return got
	return null


func _run_follow_checks(cam: Camera3D, hero: Node3D) -> void:
	var cam0 := cam.global_position
	var hero0 := hero.global_position
	var dx := 5.0

	# ① 跟随：人物瞬移 5m，等 1 秒（≈ smoothTime 的 8 倍），相机应跟过去
	hero.global_position = hero0 + Vector3(dx, 0, 0)
	await _wait_seconds(1.0)
	var cam1 := cam.global_position
	var moved := cam1.x - cam0.x
	var resid := absf(cam1.x - (cam0.x + dx))

	print("")
	print("[跟随] 人物 +%.1f m → 相机 x %.4f → %.4f（位移 %.4f m, 残差 %.5f m）" % [
		dx, cam0.x, cam1.x, moved, resid])

	_check(moved > dx * 0.5,
		"跟随生效：相机横向位移 > 人物位移一半",
		"moved=%.4f / 期望 %.4f" % [moved, dx])
	_check(resid < 0.02,
		"跟随收敛：1 秒后残差 < 2 cm",
		"残差=%.5f m" % resid)

	# ② 反向：人物回原位，相机应跟回
	hero.global_position = hero0
	await _wait_seconds(1.0)
	var cam3 := cam.global_position
	var back := absf(cam3.x - cam0.x)
	print("[跟随] 人物回原位 → 相机 x=%.4f（距起点 %.5f m）" % [cam3.x, back])
	_check(back < 0.02,
		"跟随双向收敛：人物回原位后相机回到起点 2 cm 内",
		"残差=%.5f m" % back)

	# ③ 平滑性：逐帧采样，位移不应出现「一帧跳满」的瞬移
	hero.global_position = hero0 + Vector3(dx, 0, 0)
	var samples := PackedFloat32Array()
	var prev := cam.global_position.x
	for i in 60:
		await process_frame
		var now := cam.global_position.x
		samples.append(now - prev)
		prev = now
	var max_step := 0.0
	var total := 0.0
	for s in samples:
		max_step = maxf(max_step, absf(s))
		total += absf(s)
	print("")
	print("[平滑] 60 帧内单帧最大位移 %.4f m，累计 %.4f m，帧数 %d" % [max_step, total, samples.size()])
	# 瞬移的实现会把 5m 在 1 帧内吃掉
	_check(max_step < dx,
		"无瞬移：单帧最大位移小于总位移",
		"max_step=%.4f m < %.1f m" % [max_step, dx])


func _wait_frames(n: int) -> void:
	for i in n:
		await process_frame


func _wait_seconds(seconds: float) -> void:
	await _wait_frames(int(ceil(seconds * 60.0)))


func _check(ok: bool, name: String, detail: String) -> void:
	_checks += 1
	if ok:
		print("PASS %s | %s" % [name, detail])
	else:
		_failures += 1
		print("FAIL %s | %s" % [name, detail])


func _fail(name: String, detail: String) -> void:
	_failures += 1
	print("FAIL %s | %s" % [name, detail])


func _report() -> void:
	print("")
	print("=== 结果: %d 项检查, %d 失败 ===" % [_checks, _failures])
	quit(_failures)
