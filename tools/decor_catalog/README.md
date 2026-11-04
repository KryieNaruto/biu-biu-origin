# tools/decor_catalog —— 素材 → 房间装饰目录

一句话：把 `素材/` 下 301 个 `.glb` 量测成**可被地牢建场景器直接消费的 JSON 目录**，输出在
`config/decor_catalog.json`，说明文档在 [`docs/artifacts/decor_catalog.md`](../../docs/artifacts/decor_catalog.md)。

## 文件

| 文件 | 作用 | 依赖 |
|---|---|---|
| `glb_measure.py` | 纯标准库 GLB 解析：读 JSON chunk，用 POSITION accessor 的 `min/max` + node TRS 算世界 AABB | 无（只用 `json`/`struct`） |
| `rules.py` | **收录策略**：目录默认行 `FOLDER_DEFAULT` + 单文件覆盖/跳过 `RULES` + 全局缩放约定 | 无 |
| `build_catalog.py` | 生成 `config/decor_catalog.json`；`--check` 做一致性校验；`--md` 顺带重写 md | 无（纯标准库） |
| `write_md.py` | 由 JSON 生成 `docs/artifacts/decor_catalog.md`（统计表 + 方法说明） | 无 |
| `render_contact_sheet.py` | 生成 `docs/artifacts/decor_contact_sheet.png`（俯视投影 + 立面高度小样） | Pillow（用 DSH 自带 python） |

> `measure_with_godot.gd` **已挪到 [`tools/probes/`](../probes/)**（2026-11-04）。原因：GS001 只放行
> `scripts/ui/**`、`addons/**`、`tools/probes/**` 三处的 `.gd`，放在 `tools/decor_catalog/` 会触发守卫 error。
> 移动不影响功能——脚本内所有路径都是 `res://` 绝对路径，与脚本自身位置无关。它是**只读探针**：
> 只量测每个 glb 的引擎 AABB 做交叉验证，不参与运行时、不建逻辑。

## 常用命令（在工程根执行）

```powershell
# 生成 / 更新 JSON（系统 python 即可）
python tools/decor_catalog/build_catalog.py

# 收工自检：JSON 是否与 素材/ + rules.py 一致（不写文件，适合放进 check_all）
python tools/decor_catalog/build_catalog.py --check

# 引擎交叉验证（可选）：产物进 tmp/decor_catalog/godot_measured.json，再跑一次上面的命令把它并进 JSON
& "E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe" --headless --path . --script tools/probes/measure_with_godot.gd

# 重写 md / 重画小样图
python tools/decor_catalog/build_catalog.py --md
& "C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe" tools/decor_catalog/render_contact_sheet.py
```

## 为什么 `measure_with_godot.gd` 是 GDScript（与规则1 的关系）

AGENTS.md 规则1 禁 GDScript，白名单里有「`tools/probes/**`：只读探针，不进游戏」。本文件的性质与之完全相同：
**只测量、不建逻辑、不参与运行时**；放在 `tools/decor_catalog/` 是因为它和生成器是一套工具。之所以必须是 `.gd`：
Godot 4 的命令行 `--script` 只接受 GDScript，C# 没有等价入口（要跑 C# 探针就得建一个完整 scenes+csproj 场景，成本远高于收益）。

> 如需严格对齐白名单措辞：把它挪到 `tools/probes/measure_decor_with_godot.gd` 并在 §4 登记即可，逻辑不用改。

## 改收录策略的正确姿势

- **新增素材目录**：在 `rules.py` 的 `FOLDER_DEFAULT` 加一行默认规则；
- **单个模型调层/权重/类别/间距，或排除**：在 `RULES` 加 `"目录/文件名.glb": {"layer": "Wall", "weight": 6, ...}` 或 `{"skip": "原因"}`；
- **改缩放口径**：`TARGET_METERS` / `TARGET_MIN` / `TARGET_MAX` / `ASSET_LIBRARY_SCALE` 四个常量。
改完都只需重跑 `build_catalog.py --md`，不需要动 C#。

## 编号稳定性

`id` = 按「目录名字典序 → 文件名字典序」排序后的 1 基序号。**新增素材不会改动已有 id**（新文件插在所属目录内时只影响该目录之后的条目）；
**删除素材会让后面的 id 前移**——请整体重跑并让宿主重新读 JSON，不要手改。
