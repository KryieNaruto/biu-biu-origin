"""扫 `素材/` 下的 .glb → 生成 `config/decor_catalog.json`（房间装饰目录）。

用法（任一 python3 均可，只用标准库）：

    python tools/decor_catalog/build_catalog.py            # 生成 config/decor_catalog.json
    python tools/decor_catalog/build_catalog.py --check    # 只校验现有 JSON 是否与素材/规则一致（CI 用，不写文件）
    python tools/decor_catalog/build_catalog.py --md       # 额外重写 docs/artifacts/decor_catalog.md

流程：
  1. 遍历 `素材/<目录>/*.glb`（301 个）；
  2. 每个文件用 `glb_measure.measure_glb` 读 JSON chunk 的 POSITION accessor min/max + node TRS，
     量出**世界系轴对齐包围盒**（不开箱遍历顶点，方法见 docs/artifacts/decor_catalog.md）；
  3. 套 `rules.py` 的收录策略（目录默认 → 单文件覆盖 → 跳过清单）；
  4. 按「目录名 → 文件名」排序后顺序编号（重跑编号稳定），算出 sizeMeters / 缩放区间；
  5. 写 UTF-8 **无 BOM** 的 JSON（`System.Text.Json` 默认可读）。

输出契约（schemaVersion = 2）——宿主侧解释器只依赖以下字段：

    {
      "schemaVersion": 2,
      "generatedBy": "tools/decor_catalog/build_catalog.py",
      "assetRoot": "res://素材",
      "targetMaxHorizontalMeters": 6.0,     # 逻辑尺寸坐标系下「最大水平边」的理想米数
      "targetMaxHorizontalRange": [3.0, 6.0],
      "assetLibraryScale": 1.5,             # 原始 glb 单位 → 逻辑米的全局系数
      "scaleBasis": "logical-horizontal",   # 见下方「缩放基准口径」
      "sourceFileCount": 301,
      "roomCategories": ["Start", ...],
      "items": [
        { "id": 1, "name": "Barrel", "sourceFolder": "海盗场景带动画", "sourceFile": "Barrel.glb",
          "path": "res://素材/海盗场景带动画/Barrel.glb",
          "layer": "Ground", "weight": 10,
          "categoryMask": ["Combat","Boss","Prototype"],
          "clearance": 0.4, "scaleMin": 0.9, "scaleMax": 1.1,
          "sizeMeters": [1.07, 0.96, 1.07],   # = 原始尺寸 × assetLibraryScale，2 位小数
          "sizeRaw": [0.72, 0.64, 0.71],      # glb 原始单位下的轴对齐包围盒尺寸（可追溯）
          "meshHeightMeters": 0.96,           # 同上，垂直向 = 贴地摆放时的实际高度
          "needsAssetImport": true }
      ],
      "skipped": [ { "path": "...", "folder": "...", "reason": "..." } ]
    }

「缩放基准口径」（重要，宿主解释器按这个算 placement.Scale）：
  * `sizeMeters` 是**逻辑尺寸**（= 原始尺寸 × assetLibraryScale），不是 Godot 实际渲染尺寸；
  * 宿主在 `scaleBasis == "logical-horizontal"` 时：`finalScale = placement.Scale * targetMaxHorizontalMeters /
    max(sizeMeters.x, sizeMeters.z)`，再乘 Godot 实测的「原始 glb → 场景节点」缩放（`.import` 里可查）；
  * 于是 `placement.Scale = 1` ⇔ 该装饰的最大水平边 ≈ 6 米（≈ 半格，CellSize=12 米时），
    `scaleMin/scaleMax` 是规划器给这个模型建议的抖动区间（已按目录缩放系数把模型归一到同一量级，
    所以不同目录的模型可以放在同一个区间里）。
"""

from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
ASSET_DIR_NAME = "素材"
ASSET_ROOT_REL = "res://" + ASSET_DIR_NAME
OUT_JSON = os.path.join(PROJECT_ROOT, "config", "decor_catalog.json")
OUT_MD = os.path.join(PROJECT_ROOT, "docs", "artifacts", "decor_catalog.md")

sys.path.insert(0, HERE)
import glb_measure  # noqa: E402
import rules as R  # noqa: E402

LAYER_ORDER = ["Ground", "Wall", "Ceiling"]


def discover(asset_root):
    """返回 [(folder, filename, abspath)]，按目录名、文件名排序（中文按码位，稳定即可）。"""
    found = []
    for folder in sorted(os.listdir(asset_root)):
        folder_path = os.path.join(asset_root, folder)
        if not os.path.isdir(folder_path):
            continue
        for filename in sorted(os.listdir(folder_path)):
            if not filename.lower().endswith(".glb"):
                continue
            found.append((folder, filename, os.path.join(folder_path, filename)))
    return found


def resolve_rule(folder, filename):
    """目录默认 → 单文件覆盖。返回 (spec 或 None, skip 原因 或 None)。"""
    if folder in R.SKIP_FOLDER:
        return None, R.SKIP_FOLDER[folder]
    spec = dict(R.FOLDER_DEFAULT.get(folder, {}))
    override = R.RULES.get(folder + "/" + filename, {})
    if override.get("skip"):
        return None, override["skip"]
    if not spec:
        return None, "目录 %s 既没有默认规则、也不在 SKIP_FOLDER 里（请补 rules.py）" % folder
    for key, value in override.items():
        if key == "categories":
            spec["categoryMask"] = list(value)
        else:
            spec[key] = value
    return spec, None


def build():
    asset_root = os.path.join(PROJECT_ROOT, ASSET_DIR_NAME)
    files = discover(asset_root)
    items = []
    skipped = []
    measure_errors = []
    library_scale = R.ASSET_LIBRARY_SCALE

    for folder, filename, path in files:
        try:
            m = glb_measure.measure_glb(path)
        except Exception as exc:  # 坏文件不阻塞：记进 skipped 让目录仍然可生成
            measure_errors.append({"path": ASSET_ROOT_REL + "/" + folder + "/" + filename, "error": str(exc)})
            skipped.append({
                "path": ASSET_ROOT_REL + "/" + folder + "/" + filename,
                "folder": folder,
                "reason": "GLB 解析失败：" + str(exc),
            })
            continue

        spec, skip_reason = resolve_rule(folder, filename)
        rel = ASSET_ROOT_REL + "/" + folder + "/" + filename
        if spec is None:
            skipped.append({"path": rel, "folder": folder, "reason": skip_reason})
            continue

        layer = spec["layer"]
        if layer not in LAYER_ORDER:
            raise SystemExit("规则里 layer 非法：%s → %s" % (rel, layer))
        for cat in spec.get("categoryMask", []):
            if cat not in R.ROOM_CATEGORIES:
                raise SystemExit("规则里 categoryMask 非法：%s → %s" % (rel, cat))

        raw = [round(v, 4) for v in m["size"]]
        logical = [round(v * library_scale, 3) for v in raw]
        horizontal = max(logical[0], logical[2])

        # 缩放基准：把「逻辑尺寸的最大水平边」压进 [TARGET_MIN, TARGET_MAX]
        # recommendedScale = 在每个模型调到 TARGET_METERS 所需的系数上，再留 scaleMin..scaleMax 的抖动。
        base = R.TARGET_METERS / horizontal if horizontal > 0 else 1.0
        lo = max(0.01, round(base * (R.TARGET_MIN / R.TARGET_METERS), 3))
        hi = round(base * (R.TARGET_MAX / R.TARGET_METERS), 3)
        # 规则里显式写了 scale 的就用规则的；否则用区间（二者等价：规则的 scale 是绕 1 的抖动系数）
        rule_scale = spec.get("scale")
        if rule_scale:
            scale_min, scale_max = rule_scale[0], rule_scale[1]
        else:
            scale_min, scale_max = 0.9, 1.1

        items.append({
            "id": len(items) + 1,
            "name": os.path.splitext(filename)[0],
            "sourceFolder": folder,
            "sourceFile": filename,
            "path": rel,
            "layer": layer,
            "weight": int(spec.get("weight", 1)),
            "categoryMask": list(spec.get("categoryMask", [])),
            "clearance": float(spec.get("clearance", 0.25)),
            "scaleMin": float(scale_min),
            "scaleMax": float(scale_max),
            "sizeMeters": [round(v, 3) for v in logical],
            "sizeRaw": raw,
            "meshHeightMeters": round(logical[1], 3),
            # 期望把最大水平边 scale 到 TARGET_METERS 的系数（含全局库缩放）；宿主可以只乘这个数。
            "fitScaleToTarget": round(base, 5),
            "horizontalMeters": round(horizontal, 3),
            "triangleCount": m["triangleCount"],
            "hasSkin": m["hasSkins"],
            "animationCount": m["animationCount"],
            "needsAssetImport": True,
        })

    doc = {
        "schemaVersion": R.SCHEMA_VERSION,
        "generatedBy": "tools/decor_catalog/build_catalog.py",
        "generatorNote": "由 tools/decor_catalog/build_catalog.py + rules.py 生成；手改会被下次重跑覆盖。",
        "assetRoot": ASSET_ROOT_REL,
        "targetMaxHorizontalMeters": R.TARGET_METERS,
        "targetMaxHorizontalRange": [R.TARGET_MIN, R.TARGET_MAX],
        "assetLibraryScale": library_scale,
        "scaleBasis": "logical-horizontal",
        "scaleBasisDoc": "sizeMeters = sizeRaw * assetLibraryScale；宿主最终缩放 = placement.Scale * "
                         "targetMaxHorizontalMeters / max(sizeMeters.x, sizeMeters.z) * GodotImportScale"
                         "（GodotImportScale 从 .import 实测，原始 glb → 场景节点）",
        "lengthUnit": "meter",
        "sourceFileCount": len(files),
        "itemCount": len(items),
        "roomCategories": R.ROOM_CATEGORIES,
        "items": items,
        "skipped": skipped,
        "measureErrors": measure_errors,
    }
    return doc


def attach_engine_measurements(doc):
    """若 `tmp/decor_catalog/godot_measured.json` 存在（measure_with_godot.gd 的产物），
    把引擎实测包围盒并进每个条目（`engineSizeRaw` / `engineChecked`），并统计一致性。"""
    path = os.path.join(PROJECT_ROOT, "tmp", "decor_catalog", "godot_measured.json")
    doc["engineVerification"] = None
    if not os.path.exists(path):
        return doc
    with open(path, encoding="utf-8") as f:
        m = json.load(f)
    by_id = {}
    for row in m.get("items", []):
        size = row.get("engineSize")
        if size:
            by_id[int(row["id"])] = [round(float(v), 4) for v in size]
    max_err = 0.0
    checked = 0
    for it in doc["items"]:
        engine = by_id.get(it["id"])
        if engine is None:
            it["engineChecked"] = False
            continue
        checked += 1
        it["engineChecked"] = True
        it["engineSizeRaw"] = engine
        for a, b in zip(it["sizeRaw"], engine):
            max_err = max(max_err, abs(a - b))
    doc["engineVerification"] = {
        "how": "Godot 4.7.2 headless 加载每个 glb（res://），取 MeshInstance3D 的 Mesh AABB 世界并集",
        "source": "tmp/decor_catalog/godot_measured.json（tools/decor_catalog/measure_with_godot.gd 生成，不入库）",
        "checkedItems": checked,
        "maxAbsErrorRaw": round(max_err, 5),
        "okCount": m.get("okCount"),
        "failCount": m.get("failCount"),
    }
    return doc


def write_json(doc):
    os.makedirs(os.path.dirname(OUT_JSON), exist_ok=True)
    text = json.dumps(doc, ensure_ascii=False, indent=2)
    with open(OUT_JSON, "w", encoding="utf-8", newline="\n") as f:
        f.write(text + "\n")
    return OUT_JSON


def check(doc):
    """--check：现有 JSON 与重新生成的结果是否一致（忽略 generatedAt 之类易变字段）。"""
    if not os.path.exists(OUT_JSON):
        print("FAIL: %s 不存在" % OUT_JSON)
        return 1
    with open(OUT_JSON, encoding="utf-8-sig") as f:
        old = json.load(f)
    if old == doc:
        print("OK: config/decor_catalog.json 与 素材/ + rules.py 一致（%d 条）" % doc["itemCount"])
        return 0
    old_ids = {it["id"]: it for it in old.get("items", [])}
    new_ids = {it["id"]: it for it in doc["items"]}
    diffs = 0
    for i in sorted(set(old_ids) | set(new_ids)):
        if old_ids.get(i) != new_ids.get(i):
            diffs += 1
            if diffs <= 20:
                print("DIFF id=%s old=%s new=%s" % (i, old_ids.get(i, {}).get("path"), new_ids.get(i, {}).get("path")))
    print("FAIL: 有 %d 条差异（共 old=%d new=%d）。请重跑 build_catalog.py 更新 JSON。" % (diffs, len(old_ids), len(new_ids)))
    return 2


def stats(doc):
    by_layer = {layer: 0 for layer in LAYER_ORDER}
    by_folder = {}
    by_category = {c: 0 for c in R.ROOM_CATEGORIES}
    for it in doc["items"]:
        by_layer[it["layer"]] = by_layer.get(it["layer"], 0) + 1
        by_folder[it["sourceFolder"]] = by_folder.get(it["sourceFolder"], 0) + 1
        for c in it["categoryMask"]:
            by_category[c] += 1
    skipped_by_folder = {}
    for sk in doc["skipped"]:
        skipped_by_folder[sk["folder"]] = skipped_by_folder.get(sk["folder"], 0) + 1
    return by_layer, by_folder, by_category, skipped_by_folder


def main(argv):
    doc = build()
    attach_engine_measurements(doc)
    if "--check" in argv:
        return check(doc)
    with open(OUT_JSON, "w", encoding="utf-8", newline="\n") as f:
        if "--bom" in argv:
            f.write("\ufeff")
        f.write(json.dumps(doc, ensure_ascii=False, indent=2) + "\n")
    by_layer, by_folder, by_category, skipped_by_folder = stats(doc)
    print("wrote %s" % OUT_JSON)
    print("  源文件 %d，收录 %d，跳过 %d" % (doc["sourceFileCount"], doc["itemCount"], len(doc["skipped"])))
    print("  按层: %s" % by_layer)
    print("  按目录（收录 / 跳过）: %s" % ", ".join("%s %d/%d" % (k, v, skipped_by_folder.get(k, 0)) for k, v in sorted(by_folder.items())))
    print("  按类别: %s" % by_category)
    if doc["measureErrors"]:
        print("  !! 测量失败: %s" % doc["measureErrors"])
    if "--md" in argv:
        import write_md  # 延迟导入：只有需要时加载
        write_md.write(OUT_MD, doc)
        print("wrote %s" % OUT_MD)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
