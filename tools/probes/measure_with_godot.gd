"""目录外观量测：用 headless Godot 加载 decor_catalog.json 里每个 glb，记录**引擎实测**包围盒。

用法（在工程根）：

    & "E:\\Godot\\Godot_v4.7.2-stable_mono_win64_console.exe" --headless --path . \
        --script tools/decor_catalog/measure_with_godot.gd

输出：tmp/decor_catalog/godot_measured.json（tmp/ 由 .gitignore 忽略，不需要也不要提交）。
它是 `--headless --script` 模式跑的**只读探针**（AGENTS.md §4 白名单「tools/probes/**」同性质：
只测量、不进游戏、不建逻辑）。为什么是 .gd：Godot 4 的命令行 `--script` 只接受 GDScript，
C# 没有等价入口；这里不承载任何玩法逻辑。

产物用途（不是运行时依赖）：
  * 交叉验证「glb accessor min/max + node TRS」的包围盒测量是否与引擎一致；
  * 量出 `.import` 的导入缩放（1 单位 → 1 m 还是 0.01 m），写进 docs/artifacts/decor_catalog.md。
"""

extends SceneTree

const CATALOG_PATH := "res://config/decor_catalog.json"
const OUT_PATH := "res://tmp/decor_catalog/godot_measured.json"
const FIXTURE_SCALE := 1.0


func _initialize() -> void:
	var json := JSON.new()
	var text := FileAccess.get_file_as_string(CATALOG_PATH)
	if json.parse(text) != OK:
		push_error("解析 %s 失败：%s" % [CATALOG_PATH, json.get_error_message()])
		quit(1)
		return
	var doc: Dictionary = json.data
	var items: Array = doc.get("items", [])
	var rows: Array = []
	var ok_count := 0
	var fail_count := 0
	var scale_samples: Array = []
	for it in items:
		var path := String(it.get("path", ""))
		var row := {"id": int(it.get("id", 0)), "path": path, "name": String(it.get("name", ""))}
		if not ResourceLoader.exists(path):
			row["error"] = "ResourceLoader.exists = false"
			fail_count += 1
			rows.append(row)
			continue
		var packed := load(path) as PackedScene
		if packed == null:
			row["error"] = "load 返回非 PackedScene"
			fail_count += 1
			rows.append(row)
			continue
		var inst := packed.instantiate() as Node3D
		if inst == null:
			row["error"] = "instantiate 非 Node3D"
			fail_count += 1
			rows.append(row)
			continue
		root.add_child(inst)
		inst.scale = Vector3.ONE * FIXTURE_SCALE
		inst.position = Vector3.ZERO
		row["engineAabb"] = _aabb_of(inst)
		row["engineSize"] = row["engineAabb"]["size"]
		row["meshInstances"] = _count_mesh_instances(inst)
		ok_count += 1
		rows.append(row)
		inst.free()
	# 抽样几个模型量 `.import` 的导入缩放：原始 glb 单位 → m
	for sample in ["res://素材/海盗场景带动画/Barrel.glb", "res://素材/RTS建筑/Temple.glb",
			"res://素材/炮台/Emp Turret.glb", "res://素材/RPG武器/Chest.glb",
			"res://素材/小房子/House.glb"]:
		if not ResourceLoader.exists(sample):
			continue
		var ps := load(sample) as PackedScene
		if ps == null:
			continue
		var n := ps.instantiate() as Node3D
		root.add_child(n)
		scale_samples.append({"path": sample, "aabb": _aabb_of(n)})
		n.free()
	var result := {
		"fixtureScale": FIXTURE_SCALE,
		"itemCount": items.size(),
		"okCount": ok_count,
		"failCount": fail_count,
		"scaleSamples": scale_samples,
		"items": rows,
	}
	var dir := OUT_PATH.get_base_dir()
	DirAccess.make_dir_recursive_absolute(dir)
	var f := FileAccess.open(OUT_PATH, FileAccess.WRITE)
	if f == null:
		push_error("无法写 %s" % OUT_PATH)
		quit(1)
		return
	f.store_string(JSON.stringify(result, "  "))
	f.close()
	print("DECOR_PROBE ok=%d fail=%d → %s" % [ok_count, fail_count, OUT_PATH])
	quit(0 if fail_count == 0 else 3)


func _collect_aabb(node: Node, box: AABB, state: Array) -> AABB:
	if node is MeshInstance3D:
		var mi := node as MeshInstance3D
		if mi.mesh != null:
			var local := mi.mesh.get_aabb()
			var xf := Transform3D.IDENTITY
			var cur: Node3D = mi
			while cur != null and cur != root:
				xf = cur.transform * xf
				cur = cur.get_parent() as Node3D
			var world_box: AABB = xf * local
			if state[0] == true:
				box = world_box
				state[0] = false
			else:
				box = box.merge(world_box)
	for child in node.get_children():
		box = _collect_aabb(child, box, state)
	return box


func _aabb_of(n: Node3D) -> Dictionary:
	var state := [true]
	var box := _collect_aabb(n, AABB(), state)
	return {
		"min": [box.position.x, box.position.y, box.position.z],
		"max": [box.end.x, box.end.y, box.end.z],
		"size": [box.size.x, box.size.y, box.size.z],
	}


func _count_mesh_instances(node: Node) -> int:
	var n := 1 if node is MeshInstance3D else 0
	for child in node.get_children():
		n += _count_mesh_instances(child)
	return n
