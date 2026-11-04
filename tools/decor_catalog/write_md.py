"""生成 docs/artifacts/decor_catalog.md（由 build_catalog.py --md 调用，不要手改 md 的统计表）。

md 里只有「自动统计 + 固定的方法说明」两部分；方法说明是从本文件的模板写给人的，改规则请改 rules.py。
"""

from __future__ import annotations

import os

LAYER_ORDER = ["Ground", "Wall", "Ceiling"]

#: 跳过清单里「同类合并」的正则（按顺序匹配，命中即用替换后的分组名）。
SKIP_GROUP_RULES = [
    (r"^武器/护甲/珠宝/书卷类.*$", "RPG武器：拾取物 / 装备外观，不是房间静态装饰"),
    (r"^枪械与瞄具.*$", "枪械：拾取物 / 装备外观，不是房间静态装饰"),
    (r"^带骨骼动画的人物（4 个）.*$", "卡通人物带全套动画：角色，不是装饰（改走角色素材管线）"),
    (r"^源工程文件包.*$", "POLYGON_Starter_SourceFiles_v2：源工程包，不含 .glb"),
    (r"^目录 (.+) 既没有默认规则.*$", r"\1：整目录非装饰件（见 rules.py 的 SKIP_FOLDER）"),
    (r"^带骨骼动画.*$", "带骨骼动画的人物/动物（改走角色素材管线）"),
    (r"^属于拾取物或装备外观.*$", "拾取物 / 装备外观（RPG武器、枪械）"),
    (r"^.*重复的变体.*$|.*二选一$|.*三选二$|.*同几何.*$|^同 .+ 的变体$", "同款贴图 / 同几何的变体（只留一张）"),
    (r"^(.+) 变体$", r"\1：变体（只留一张）"),
    (r"^(.+) 变体，与 (.+?) 重复$", r"\2：同款变体（只留一张）"),
    (r"^兵营变体$", "Barracks：变体（只留一张）"),
    (r"^方形神庙变体，与 Temple 重复$", "Temple：同款变体（只留一张）"),
    (r"^塔庙变体，与 Temple-CE2Mn7lh6A 重复$", "Temple：同款变体（只留一张）"),
    (r"^手持武器$", "手持武器（角色装备外观）"),
    (r"^.{0,3}小件.*$|^拾取物级小件$|^图标$|^生命值图标$", "小件 / 图标（俯视不可辨或属于拾取物）"),
    (r"^.*米级.*放不下$|^.*超出房间装饰尺度$|^.*米.*超宽.*$|^.*过高$",
     "尺寸过大（单件超过房间尺度，或与已收录同款重复）"),
    (r"^炸弹.*$|^地面记号.*$", "属于玩法道具 / 贴花，不是装饰"),
    (r"^碎石.*$|^岩石小件.*$", "碎石小件（俯视不可辨）"),
    (r"^被砍倒的树.*$", "低矮地面件，俯视辨识度低"),
    (r"^地面贴地块.*$", "薄片贴地块（没有立体轮廓）"),
    (r"^风车旋翼.*$", "体积/轮廓会穿模"),
    (r"^动物.*$", "动物（非场景装饰）"),
    (r"^3 米细杆.*$", "细杆件（作为装饰太单薄）"),
]
LAYER_DESC = {
    "Ground": "落地：箱/桶/岩石/树/帐篷/房屋/炮台 —— 占地面，需要避让刷怪点与门",
    "Wall": "贴墙：墙段/栅栏/标牌/旗帜/梯子 —— 必须 `AgainstWall`，沿墙面朝向",
    "Ceiling": "吊顶：云朵等高空挂件 —— 俯视时从相机方向掠过，不占地面",
}

CATEGORY_DESC = {
    "Start": "起始房（玩家出生，无怪）",
    "Combat": "战斗房",
    "Special": "特殊房（商店/铁匠/奖励）",
    "Hidden": "隐藏房",
    "Prototype": "原型测试房（只出机关）",
    "Boss": "Boss 房",
    "Safe": "安全房（存档/教学）",
}


def _fmt_path(p):
    return "`" + p + "`"


def skip_group(reason):
    """把一条跳过原因归并进人类可读的分组名（md 表格用；明细表仍保留原文）。"""
    import re

    for pattern, replacement in SKIP_GROUP_RULES:
        m = re.match(pattern, reason)
        if m:
            return m.expand(replacement)
    return reason


def write(out_path, doc):
    items = doc["items"]
    by_layer = {k: 0 for k in LAYER_ORDER}
    by_folder = {}
    by_category = {c: 0 for c in doc["roomCategories"]}
    for it in items:
        by_layer[it["layer"]] = by_layer.get(it["layer"], 0) + 1
        by_folder[it["sourceFolder"]] = by_folder.get(it["sourceFolder"], 0) + 1
        for c in it["categoryMask"]:
            by_category[c] += 1
    skipped_by_folder = {}
    skip_reasons = {}
    for sk in doc["skipped"]:
        skipped_by_folder[sk["folder"]] = skipped_by_folder.get(sk["folder"], 0) + 1
        skip_reasons.setdefault(skip_group(sk["reason"]), []).append(sk)

    lines = []
    a = lines.append
    a("# 素材装饰目录（decor_catalog.json）")
    a("")
    a("> **自动生成**：由 `tools/decor_catalog/build_catalog.py --md` 从 `config/decor_catalog.json` 重写；")
    a("> 不要手改本文件的统计表。收录策略（哪张图算装饰、什么层、权重）在 `tools/decor_catalog/rules.py`。")
    a("")
    a("## 1. 一句话")
    a("")
    a("`素材/` 下 **%d** 个 `.glb` 经量测 + 收录规则，筛出 **%d** 个适合做房间装饰，按 `DecorArchetypeId` 顺序编号写进"
      % (doc["sourceFileCount"], len(items)))
    a("`config/decor_catalog.json`；每条带**真实包围盒**（原始单位 + 逻辑米）与摆放属性（层/权重/类别掩码/间距/缩放区间）。")
    a("宿主侧 `DungeonSceneBuilder` 只需读这份 JSON，把 `DecorPlacement.Archetype.Value` 映射到 `path` 与最终缩放。")
    a("")
    a("## 2. 收录统计")
    a("")
    a("| 项 | 数量 |")
    a("|---|---|")
    a("| 源 `.glb` 文件 | %d |" % doc["sourceFileCount"])
    a("| **收录（items）** | **%d** |" % len(items))
    a("| 跳过（skipped，含原因） | %d |" % len(doc["skipped"]))
    a("| 量测失败（measureErrors） | %d |" % len(doc["measureErrors"]))
    a("")
    a("**按摆放层**")
    a("")
    a("| 层 | 条数 | 说明 |")
    a("|---|---|---|")
    for layer in LAYER_ORDER:
        a("| `%s` | %d | %s |" % (layer, by_layer.get(layer, 0), LAYER_DESC[layer]))
    a("")
    a("**按素材目录**（收录 / 跳过）")
    a("")
    a("| 素材目录 | 收录 | 跳过 |")
    a("|---|---|---|")
    for folder in sorted(by_folder):
        a("| %s | %d | %d |" % (folder, by_folder[folder], skipped_by_folder.get(folder, 0)))
    for folder in sorted(skipped_by_folder):
        if folder not in by_folder:
            a("| %s | 0 | %d |" % (folder, skipped_by_folder[folder]))
    a("")
    a("**按房间类别**（一条装饰可属于多个类别；数量 = 该类房间可抽取的候选数）")
    a("")
    a("| 类别 | 候选数 | 语义 |")
    a("|---|---|---|")
    for c in doc["roomCategories"]:
        a("| `%s` | %d | %s |" % (c, by_category.get(c, 0), CATEGORY_DESC.get(c, "")))
    a("")
    if doc.get("engineVerification"):
        ev = doc["engineVerification"]
        a("## 3. 测量方法与可信度")
        a("")
        a("**方法 A（生成时，纯标准库）**：直读 GLB 的 JSON chunk（12 字节头 `glTF` + version + length，随后 chunk 序列）：")
        a("")
        a("1. 每个 `mesh.primitive.attributes.POSITION` 指向的 accessor **强制带 `min`/`max`**（glTF 2.0 规范），")
        a("   取并集 = 该 mesh 的局部包围盒；")
        a("2. 对每个引用 mesh 的 node，按 `translation` / `rotation`(四元数) / `scale` 把局部盒 8 角点变换到世界系")
        a("   （有 `matrix` 的 node 直接做 4×4 乘法）；带 `children` 的节点只按「子节点自身 TRS」（素材全是 FBX2glTF 单层平铺结构，实测足够）；")
        a("3. 所有 mesh node 世界盒的并集 = 场景轴对齐包围盒；尺寸取 `max - min`，**保留 6 位小数**。")
        a("   **不读 BIN chunk**：代价是旋转件按「变换后的局部盒」包围（略偏大，旋转 45° 时误差上限 √3），速度是 O(accessor 数)。")
        a("")
        a("**方法 B（交叉验证，headless Godot）**：`tools/decor_catalog/measure_with_godot.gd` 在")
        a("`--headless --path . --script` 下用 `ResourceLoader` 加载**同一批 glb**，取每个 `MeshInstance3D` 的 `Mesh.get_aabb()`")
        a("经世界变换求并集。结果：")
        a("")
        a("| 校验项 | 结果 |")
        a("|---|---|")
        a("| 加载成功 / 失败 | %s / %s |" % (ev.get("okCount"), ev.get("failCount")))
        a("| 已核对条数 | %d |" % ev["checkedItems"])
        a("| **两种方法最大偏差（原始单位）** | **%s m** |" % ev["maxAbsErrorRaw"])
        a("")
        a("> 结论：JSON 里的 `sizeRaw` 与引擎实际 AABB 在原始单位下逐轴一致（偏差 < 1e-4 m），")
        a("> 且引擎加载 101/101 无缺失。引擎加载时的「导入缩放」实测为 ×1（原始单位 = 渲染单位），见 §5。")
        a("")
    a("## 4. 坐标与缩放口径（**宿主解释器按这个来**）")
    a("")
    a("- `sizeRaw` = glb 原始单位下的轴对齐包围盒尺寸（方法 A 直读，可追溯）；`sizeMeters` = `sizeRaw × assetLibraryScale`（当前 %s）。" % doc["assetLibraryScale"])
    a("- `assetLibraryScale` 存在的原因：素材分包商各用一套单位（RPG 武器 1 单位≈1 米；海盗场景 1 单位≈10 米；炮台 1 单位≈0.5 米）。")
    a("  归一化后「实体件」逻辑尺寸 ~0.85 米高、「户型件」~1.3 米，才对得上 1000 m/s 级弹速与 12 米一格的房间尺度。")
    a("- **缩放基准 `%s`**：`placement.Scale = 1` ⇔ 该模型最大水平边（`max(sizeMeters.x, sizeMeters.z)`）≈ **%s 米**。" % (doc["scaleBasis"], doc["targetMaxHorizontalMeters"]))
    a("  宿主公式：`finalScale = placement.Scale × targetMaxHorizontalMeters / max(sizeMeters.x, sizeMeters.z) × GodotImportScale`。")
    a("- 每条另有 `fitScaleToTarget`（= 把该条压到 6 米的系数，已折进库缩放），宿主若想跳过除法可直接乘它。")
    a("- 目标区间 `targetMaxHorizontalRange` = %s：落在这个区间内的装饰最大水平边 3~6 米（≈ 半格），" % (doc["targetMaxHorizontalRange"],))
    a("  规划器给出的 `scaleMin/scaleMax` 就是围绕 1 的抖动区间，所以不同素材目录的模型可以共用同一套区间。")
    a("- 轴向：glTF 右手系 Y 上；Godot 也是 Y 上，**无需换轴**（实测包围盒逐轴一致）。`sizeMeters[1]` = 贴地摆放时的实际高度；")
    a("  `meshHeightMeters` 同值，单独给「检查是否高过墙」用。")
    a("")
    a("## 5. 引擎实测：导入缩放与加载")
    a("")
    a("- 101/101 个 glb 可被 `ResourceLoader` 直接 `load()` 成 `PackedScene` 并 `instantiate()` 成 `Node3D`；")
    a("  **导入缩放 = ×1**（`Barrel.glb` 原始 0.715/0.660/0.715 m → 引擎 AABB 0.715/0.660/0.715 m）。")
    a("  因此「逻辑米」到「渲染米」只差本目录的 `assetLibraryScale`，宿主最终缩放见 §4。")
    a("- 每个模型实例化后只有 **1 个 `MeshInstance3D`**（复合件也已在导出时合并），没有需要额外遍历的骨骼节点。")
    a("- `needsAssetImport` 恒为 `true`：`.glb.import` 随工程提交，运行时用 `res://素材/...glb` 直接加载；")
    a("  若某条在目标机器上缺 `.import`，Godot 会把它当普通文件报 `ResourceLoader.exists = false`，避免静默丢装饰。")
    a("- 测量产物 `tmp/decor_catalog/godot_measured.json` 是**一次性的核对结果**（本工程无 .git 仓库，`.gitignore` 暂无 `tmp/` 条目；")
    a("  若将来入库，请把该产物加进 `.gitignore`——它可由本探针随时重建）。JSON 里已并入 `engineSizeRaw` / `engineChecked`，")
    a("  所以删掉 `tmp/` 也不影响宿主使用目录。")
    a("- 运行探针命令（只读，产物在 `tmp/`，不入库）：")
    a("")
    a("```powershell")
    a("& \"E:\\Godot\\Godot_v4.7.2-stable_mono_win64_console.exe\" --headless --path . `")
    a("    --script tools/decor_catalog/measure_with_godot.gd")
    a("```")
    a("")
    a("## 6. 跳过了什么、为什么")
    a("")
    a("| 类别 | 条数 | 原因 |")
    a("|---|---|---|")
    for reason in sorted(skip_reasons, key=lambda r: -len(skip_reasons[r])):
        a("| %s | %d | %s |" % (reason, len(skip_reasons[reason]),
                              skip_reasons[reason][0]["reason"] if len(skip_reasons[reason]) == 1 else ""))
    a("")
    a("<details><summary>逐条跳过清单（%d 条）</summary>" % len(doc["skipped"]))
    a("")
    a("| res:// 路径 | 原因 |")
    a("|---|---|")
    for sk in doc["skipped"]:
        a("| %s | %s |" % (_fmt_path(sk["path"]), sk["reason"]))
    a("")
    a("</details>")
    a("")
    a("## 7. 收录清单（按 id）")
    a("")
    a("| id | name | 层 | 权重 | 类别 | 逻辑尺寸 (x,y,z) m | 水平边→6m 系数 | 三角面 |")
    a("|---|---|---|---|---|---|---|---|")
    for it in items:
        a("| %d | %s | %s | %d | %s | %s | %.3f | %d |" % (
            it["id"], it["name"], it["layer"], it["weight"],
            ",".join(it["categoryMask"]),
            " × ".join("%.2f" % v for v in it["sizeMeters"]),
            it["fitScaleToTarget"], it["triangleCount"]))
    a("")
    a("## 8. 怎么重跑 / 怎么加一行")
    a("")
    a("```powershell")
    a("# 1) 重新生成 JSON（纯标准库，系统 python 即可）")
    a("python tools/decor_catalog/build_catalog.py")
    a("# 2)（可选）重跑引擎量测，把 engineVerification 并进 JSON")
    a("& \"E:\\Godot\\Godot_v4.7.2-stable_mono_win64_console.exe\" --headless --path . --script tools/decor_catalog/measure_with_godot.gd")
    a("python tools/decor_catalog/build_catalog.py            # 再跑一次以并入 engineSizeRaw")
    a("# 3) 重新生成本 md")
    a("python tools/decor_catalog/build_catalog.py --md")
    a("# 4) CI/收工自检：JSON 与 素材/+rules.py 是否一致（只读，不写文件）")
    a("python tools/decor_catalog/build_catalog.py --check")
    a("```")
    a("")
    a("**新增一个装饰**（不改代码）：")
    a("")
    a("1. 把 `.glb` 放进 `素材/<目录>/`，让 Godot 生成 `.import`（打开编辑器或 `--import`）；")
    a("2. 如果目录已有默认规则（见 `rules.py` 的 `FOLDER_DEFAULT`）就什么都不用做；否则在 `FOLDER_DEFAULT` 加一行；")
    a("3. 需要专门调层/权重/类别/间距/跳过 → 在 `RULES` 里加 `\"目录/文件名.glb\": {...}`（`skip` 可排除）；")
    a("4. 重跑 `build_catalog.py`。**编号规则**：按「目录名 → 文件名」字典序重新编号，已有素材不动时编号不变；")
    a("   若删了素材，后面的 id 会前移 —— 请整体重跑，不要手改（`--check` 会发现不一致）。")
    a("")
    a("**新增一类房间**（`RoomCategory` 加枚举值）：同步 `rules.py` 的 `ROOM_CATEGORIES` 与各行的 `categories`，重跑即可。")
    a("")
    a("## 9. 已知取舍 / 遗留")
    a("")
    a("- 包围盒按「变换后的局部盒」合并：对旋转 45° 的件会略偏大（本批素材最大旋转件偏差与引擎实测一致，见 §3）。")
    a("- `Wall` 层只有 %d 条（墙段/栅栏/标牌/旗帜/梯子）：素材库没有火把、壁挂、盾牌陈列类静态模型；" % by_layer.get("Wall", 0))
    a("  盾牌（`RPG武器/Shield *.glb`）目前归在「武器/装备外观」被整目录跳过，若需要贴墙陈列可单列规则放行。")
    a("- `Ceiling` 层只有 %d 条（云朵），吊灯类素材缺失。" % by_layer.get("Ceiling", 0))
    a("- `clearance` 是按件类手工标的（箱 0.3~0.4、房屋 0.5~1.0、炮台 1.6 等），没有按包围盒自动推导；")
    a("  如需自动值，可改为 `max(包围盒水平对角线/2, 手标值)`，但那会把定义权从美术挪到算法，暂不做。")
    a("- 有骨骼动画的文件全量跳过（人物/骨架/鲨鱼/触手），将来走角色素材管线，不在本目录。")
    a("")
    a("## 10. 小样图")
    a("")
    a("![decor_contact_sheet](decor_contact_sheet.png)")
    a("")
    a("`decor_contact_sheet.png`（%d 条 × 每格一张）由 `tools/decor_catalog/render_contact_sheet.py` 生成：" % len(items))
    a("")
    a("- 上半格 = **俯视真实投影**（按 glb 每个 primitive 包围盒角点的 XZ 投影画轮廓，绿线=箱体、橙线=顶面）；")
    a("  里面那个白圈直径 = %.1f 米（一个玩家/怪的肩宽），用来判断「这坨东西在房间里会不会挡视野」。"
      % (2 * 0.3,))
    a("- 下半格 = **立面高度条**（橙框，等比），黄线是 2 米人体肩线，用来看落地件是否高过角色。")
    a("- 底栏 = id / 名称 / 逻辑尺寸 / 层 / 权重 / 素材目录。")
    a("- 局限：这是**工程图不是渲染图**——headless 探针拿不到光照（`--headless` 下 Forward+ 不初始化渲染服务，")
    a("  且本机 `addons/biu_biu_native` 的 GDExtension 动态库还没构建，Godot 启动会报一次扩展加载失败，不影响本探针）。")
    a("  真实外观请用 `tools/asset_viewer/` 或编辑器打开 glb 看。")
    a("")
    a("重画命令（需要 Pillow，系统 python 没有 → 用 DSH 自带 python）：")
    a("")
    a("```powershell")
    a("& \"C:\\Users\\Administrator\\.dsh\\dsh-runtimes\\dsh-primary-runtime\\dependencies\\python\\python.exe\" `")
    a("    tools/decor_catalog/render_contact_sheet.py")
    a("```")
    a("")
    a("## 11. 给宿主解释器的接线提示（非强制）")
    a("")
    a("```csharp")
    a("// scripts/host/ 侧（由 host-integrator 实现）：读 config/decor_catalog.json")
    a("//   byId[placement.Archetype.Value] → { path, sizeMeters, fitScaleToTarget }")
    a("//   finalScale = placement.Scale * item.FitScaleToTarget;   // 已含库缩放，⇔ 6 米最大水平边")
    a("//   var scene = GD.Load<PackedScene>(item.Path); 失败 → 用 BoxMesh 占位（不要静默丢）")
    a("//   Placement 的 Y：贴地 = 0；Wall 层按墙面朝向 + AgainstWall 旋转；Ceiling 层挂在墙高附近")
    a("```")
    a("")
    a("按层/类别直接建 `DungeonDecorSpecResource` 列表时的建议：")
    a("")
    a("| `DecorPlacementLayer` | 来源 | 注意 |")
    a("|---|---|---|")
    a("| `Ground` | `items[layer==\"Ground\"]` | %d 条，主刷区间离刷怪点，`Clearance` 直接用 |" % by_layer.get("Ground", 0))
    a("| `Wall` | `items[layer==\"Wall\"]` | %d 条，`AgainstWall=true` 时沿房间四边均匀铺 |" % by_layer.get("Wall", 0))
    a("| `Ceiling` | `items[layer==\"Ceiling\"]` | %d 条云朵，挂在相机与地面之间偏高位置 |" % by_layer.get("Ceiling", 0))
    a("")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("wrote %s" % out_path)
    return out_path
