# 地编模块索引（`level`）

> 版本: v1 ｜ 建立日期: 2026-11-11 ｜ 产出任务: `t-level`（负责人 `level-designer`）
> 性质: **索引文档（只增不改）**。本文只做「读什么 / 改哪里 / 怎么验证」的路由，**不复制任何原文档的契约正文**；
> 凡与原文冲突，**一律以原文为准**（`docs/architecture/ARCHITECTURE.md` 是唯一架构真源）。
> 本文所属模块: 地编 `level` ｜ 模块名与目录约定见 [`../_shared/BOUNDARY.md`](../_shared/BOUNDARY.md) §2.1

<!-- 目录 -->
- [0. 地编管什么（一句话）](#0-地编管什么一句话)
- [1. 地编能改什么（六类内容 + 去哪改 + 怎么验证）](#1-地编能改什么六类内容--去哪改--怎么验证)
- [2. 必读清单](#2-必读清单)
- [3. 真源位置与生成物边界](#3-真源位置与生成物边界)
- [4. 验证手段（可跑的场景 / 只读探针 / 无头自检）](#4-验证手段可跑的场景--只读探针--无头自检)
- [5. 首版「全部先跑白模」：含义、影响、回填路径](#5-首版全部先跑白模含义影响回填路径)
- [6. 常见任务 → 该读哪一节（路由表）](#6-常见任务--该读哪一节路由表)
- [7. 我读一部分就够的（次归属）](#7-我读一部分就够的次归属)
- [8. 我不管什么（Out + 交界处）](#8-我不管什么out--交界处)
- [9. 待核对 / 我没把握的点](#9-待核对--我没把握的点)
- [10. 变更日志（本文自身）](#10-变更日志本文自身)

---

## 0. 地编管什么（一句话）

**决定「一局游戏的空间长什么样、房间里摆什么」，并守住素材 → 逻辑米的换算口径。**
空间是纯值数据（`DungeonLayout` / `DungeonMap` / `ContentSlot[]` / `DecorPlacement[]`），
地编调的是「数据 + 表现属性」，**不是**写作战逻辑，也**不是**把模型路径硬编码进代码。

> 边界的完整判据见 [`../_shared/BOUNDARY.md`](../_shared/BOUNDARY.md) §2.0 判定三问、§2.2 ② 地编的 In / Out / 交界处。
> 本章不再复述，只按它落地成「改哪里、怎么验」。

---

## 1. 地编能改什么（六类内容 + 去哪改 + 怎么验证）

| # | 内容 | 现在改哪里（真源） | 表现/消费方（只读它） | 验证手段 |
|---|---|---|---|---|
| 1 | **房间与地牢参数**（网格大小、房型数量、边长、隐藏房、环路、Boss 深度…） | `scripts/host/DungeonOptions.cs`（`[GlobalClass] Resource`，可在 Inspector / `.tres` 调）；最终落点见 §3 的「归属矩阵」 | `DungeonConfig`（纯 C# 值对象）→ `BranchDungeonPlanner` | `tools/dungeon_check`（§4.1）+ `DungeonMap2D` 调参面板（§4.2） |
| 2 | **房型与配色**（7 类 `RoomCategory` 的地面/墙体颜色、中心建筑、棋盘格密度） | `scripts/host/DungeonRoomStyle.cs`（`DungeonRoomStyleSet` 内置默认，可用 `.tres` 整体替换；目前仓库无该 `.tres`，走内置默认） | `DungeonRoomView` / `DungeonSceneBuilder` / 2D 图例（色块直接取自配色表） | 打开 `DungeonMap2D.tscn` 看图例色块随配色变；3D 看 `DungeonDebug.tscn` |
| 3 | **2D 栅格化的可调参数**（留白、障碍密度、起点房障碍系数、离墙格数…） | `DungeonMapConfig` 的字段定义在 `scripts/spawn/rooms/DungeonMapConfig.cs`；**调参入口是面板的 14 根「地图形状」滑条** | `DungeonMapBuilder` | 同 §4.2；参数含义见 ARCHITECTURE §14.8 |
| 4 | **物件随机生成**（建筑 / 奖励 / 可破坏 / 不可破坏四类的数量、尺寸、离墙） | `scripts/spawn/rooms/DungeonPropConfig.cs`；**调参入口是面板的 6 根「物件」滑条** | `DungeonPropPlanner` → `DungeonProp` → 2D 图形 / 3D 视图 | `tools/dungeon_check --props`（§4.1）+ 面板 |
| 5 | **装饰目录**（101 条装饰：收录、摆放层、权重、房间类别掩码、间距、缩放区间） | `config/decor_catalog.json` ← 由 `tools/decor_catalog/rules.py` + `build_catalog.py` 从 `素材/` 生成；文档化在 `docs/artifacts/decor_catalog.md`（生成物） | `DungeonDecorCatalog` 加载 → `DecorPlacement` → `DungeonSceneBuilder` | `build_catalog.py --check`（§4.5）+ 小样图 `decor_contact_sheet.png` |
| 6 | **外观包 / 摆放体积**（一个物件在 3D 里用哪个模型、多大） | `combat.xlsx` / `characters.xlsx` 的 `VisualPacks` 行 —— **真源与归属在角色模块**，地编**只读**它的尺寸与缩放含义 | `ObjectArchetype.VisualPackId` → 视图层 | 见 §5（首版全部白模）；口径见 ARCHITECTURE §17.10 / §17.10.1 |

补充两项「地编看得见、但不归地编改」的空间物：

| 项 | 现在的样子 | 归属 |
|---|---|---|
| **训练房 3D 测试场景** | `scenes/debug/TrainingRoom.tscn` + `scripts/host/debug/TrainingRoomScene.cs`；房间格数 / 格边长 / 墙高 / 墙厚 / 灰阶 / 棋盘明暗 / 假人偏移全是 `[Export]`，Inspector 改 | 场景是**地编的试验台**；`CharacterSession` 的接线归角色/开发（ARCHITECTURE §14.10 明写「0 行玩法逻辑」） |
| **2D 地图视图 + 调参面板** | `scenes/debug/DungeonMap2D.tscn` + `scripts/host/debug/DungeonMap2DView.cs` / `DungeonMap2DPanel.cs` / `DungeonMap2DDebugScene.cs` | 视图与面板的**接线**归开发；地编负责「它应该显示什么、参数的含义」 |

> 一句话口径：**地编改「数据行 + 表现属性 + 验证方式」，不改「算法与接线」**。
> 新增一种装饰 / 一种房型 / 一件物件**都不需要写代码**（`rules/06` §2 扩展预算：新关卡/波次 = 0 代码），
> 但要按 §3 的真源位置去改，不能去改 `data/generated/` 或运行时消费方。

---

## 2. 必读清单

> 行数是本次实测快照，**只作篇幅参考**；章节号才是稳定坐标（行号会随文档就地更新漂移，见 `_shared/BOUNDARY.md` §6 Q7）。

### 2.1 跨模块必读（任何模块都得读，违反即打回）

| 文档 | 必读章节 | 为什么地编也要读 | 篇幅 |
|---|---|---|---|
| [`AGENTS.md`](../../../AGENTS.md)（仓库根，不在 docs 的 17 份内） | §0 三十秒速读、§2 八条铁规、§3 红线清单、§5 目录契约、§7 收工动作 | 「数值不许写死」「`data/generated/` 不许手改」「`素材/` 只读」都在这；地编最容易碰到的就是这几条 | ~250 行 |
| [`ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | 文件头（维护者必读）、§0 一页速览、§11 变更日志首屏 | 开工前先知道「现在是什么状态、上一个人刚改了什么」 | 约 100 行（全篇 3,228） |
| [`rules/07-definition-of-done.md`](../../rules/07-definition-of-done.md) | §2 六步检查表、§4.1 章节影响矩阵、§5 就地覆盖 SOP、§8.2 章节路由表 | 改了 §14 的任何形状都要就地覆盖 + 留一行变更日志 | 466 行 |
| [`rules/08-parallel-sessions.md`](../../rules/08-parallel-sessions.md) | §0、§2 六步、§8 反例 | 多会话并行时在 `main` 上直接改 = 违规 | 405 行 |

### 2.2 地编自读项（只在本模块必读）

| 文档 | 必读章节 | 为什么 | 篇幅 |
|---|---|---|---|
| [`ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | **§14 全文**（§14.1 现状图 / §14.2 L1~L4 四层 / §14.3 七类房型 / §14.4 六条不变式 / §14.5 宿主边界 / §14.6 扩展点 / §14.7 约束 / §14.8 2D 栅格 + §14.8.1 回归 / §14.9 物件随机生成 / §14.10 训练房） | 空间抽象全部在这；「为什么这么切」的理由也在这，改之前先读理由 | 约 305 行 |
| [`ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | §7.5.1 窗口 → 视口、**§7.5.2 窗口 → 2D 自绘图** | 地编天天看 2D 地图；「窗口拉大图不变」这个坑的根因与修法在这 | 约 100 行 |
| [`ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | §15.8 相机跟随手感（只看「两个需求各自的机制」与「分层」两小节） | 俯视视角「看到多大一片世界」是地编的体验边界；调参在 `characters.xlsx` 的 `CameraPacks` | 约 120 行 |
| [`ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | **§17.10 + §17.10.1**（外观只走 `VisualPack`；全部先跑白模） | 决定「装饰以外的东西在 3D 里长什么样」以及现在的白模状态，直接关系到地编验收 | 约 80 行 |
| [`ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | §24 全文（3D 素材查看器：§24.5 素材过滤、§24.8 无人值守自检） | 从 `素材/` 里挑装饰时用它看真实外观；它扫出的是 358 资产 / 12 目录 | 约 123 行 |
| [`artifacts/decor_catalog.md`](../../artifacts/decor_catalog.md) | **§4 坐标与缩放口径（核心）**、§2 收录统计、§7 101 条清单（约 106 行表）、§11 宿主接线提示；**§6 跳过了什么**（挑素材前必看） | §4 是宿主解释器照着算的口径；§7 是「有什么可选」 | 530 行（按需跳读） |
| [`artifacts/decor_contact_sheet.png`](../../artifacts/decor_contact_sheet.png) | 整图 | 101 条装饰的工程图：上半俯视投影 + 0.6 m 人体圈，下半立面高度条 —— 判断「会不会挡视野 / 会不会高过墙」 | 232 KB |

### 2.3 规则类：只读这两段，不必通读

| 文档 | 必读章节 | 为什么 | 篇幅 |
|---|---|---|---|
| [`rules/06-extensibility.md`](../../rules/06-extensibility.md) | **§2 扩展预算表**（只看「一个新关卡/波次 = 0 代码」那行与计数口径） | 「加房型 / 加装饰 / 加物件要不要写代码」的硬数字 | 32 行 |
| [`rules/04-data-driven.md`](../../rules/04-data-driven.md) | **§8.1 字段级落位**（只看「颜色」「资源引用」两行） | 配色、模型路径这类表现参数能不能在 `.tres` 临时调、什么时候必须回写 Excel | 45 行 |

> `rules/03`（535 行）与 `rules/06` 的其余部分**不是地编必读**——它们服务实现者（角色 / 开发 / ECS）。
> 地编只需知道结论：「一个概念只有一份契约」「新增内容 ≤ 0 代码」。见 `_shared/BOUNDARY.md` §3 的提示段。

---

## 3. 真源位置与生成物边界

### 3.1 一句话真源表

| 内容 | 唯一真源 | 生成物 / 派生物（**不许手改**） | 消费者（只读） |
|---|---|---|---|
| 房间图拓扑 | `DungeonConfig` 值对象（今天由 `DungeonOptions` 填写） | `DungeonLayout`（运行期不可变，同 seed 逐字节相同） | `DungeonValidation`、槽位规划、视图 |
| 2D 栅格图 | `DungeonMapConfig` + 布局 | `DungeonMap`（可 `ToArt()/FromArt()` 字符化） | `DungeonMapCheck`、2D 视图、3D 建场景 |
| 物件摆放 | `DungeonPropConfig` | `DungeonProp[]` | 2D 图形 / 3D 视图 |
| 装饰目录 | **`config/decor_catalog.json`**（由 `tools/decor_catalog/rules.py` + `素材/` 生成） | `docs/artifacts/decor_catalog.md`、`decor_contact_sheet.png` 都是**从 JSON 重写出来的** | `DungeonDecorCatalog` → `DecorPlacement` → `DungeonSceneBuilder` |
| 房型配色 | `DungeonRoomStyleSet`（内置默认 / 可选 `.tres`） | — | 房间视图、2D 图例色块 |
| 外观（模型路径 / 缩放） | `combat.xlsx` / `characters.xlsx` 的 `VisualPacks`（属**角色/策划**） | `data/generated/` | `ObjectArchetype.VisualPackId` → 视图层 |
| 相机手感参数 | `characters.xlsx` 的 `CameraPacks`（属地编与角色交界，**填入值归策划**） | `data/generated/` | `CameraFeel`（纯数学）→ `TopDownCameraRig` |

### 3.2 归属矩阵（现在 / 将来）——本节最重要的一张表

地牢参数**今天是「代码里一个 `Resource` 的字段默认值」，将来要搬到 Excel**。两套口径同时存在，
地编必须先确认「我要改的那个数，现在到底住在哪」，否则改了不生效：

| 参数 | 现状真源 | 文档里的目标真源 | 依据 | 权威性 |
|---|---|---|---|---|
| 房间图尺寸 / 数量 / 边长 / 隐藏房 / 环路 / Boss 深度 | `scripts/host/DungeonOptions.cs` 的 `[Export]` 字段（Inspector 可调） | `progression.xlsx` 的 `Dungeon` Sheet | ARCHITECTURE §14.7「下一步①」、`scripts/spawn/rooms/DungeonConfig.cs` 类注释「来源三选一」 | ✅ 两者一致 |
| 房型登记表（`RoomCategorySpec` 行） | `DungeonOptions` 里的房型规格列表（Resource 行数组） | 同上 Sheet | ARCHITECTURE §14.3 + §14.7 | ✅ 一致 |
| 装饰目录 | `config/decor_catalog.json` | **没有搬家计划**（JSON 本身就是「数据真源」） | `DungeonDecorCatalog.cs` 类注释、decor_catalog §1 | ✅ 一致（路径口径另见 §9 Q2） |
| 格边长 `CellSizeMeters` | `DungeonOptions` 字段 | 未明说（§14.7 只点名了「`DungeonConfig` + 房型表」） | — | ⚠️ 见 §9 Q1 |

**执行纪律**（三条，都从原文推出）：

1. **改一个数之前先看它住哪。** 今天改 `DungeonOptions` 字段默认值 / `.tres` 是**改代码或资源**，不是改数据表；
   要按 `rules/04` §8 的 P2 口径处理（临时调可以，落地必须回写 Excel），并按 `rules/07` 就地更新 §14 的变更日志。
2. **`progression.xlsx` 目前不存在**：`data/excel/` 下只有 `combat.xlsx` / `characters.xlsx` / `l10n.xlsx`（见 §9 Q1 的核对方法）。
   在它落地之前，「改表」这件事对地牢参数**还做不到**，地编只能走 `DungeonOptions`。
3. **生成物一律不手改**：`data/generated/`、`config/decor_catalog.json`、`docs/artifacts/decor_catalog.md`、
   `decor_contact_sheet.png` 全部由脚本重写。手改会在下一次生成时被覆盖（`rules/04` §8 的 P2 行；`AGENTS.md` §3 红线 6）。

### 3.3 目录边界（谁能写）

```text
素材/                    ★美术原料区，只读，禁止当运行时资源路径引用（ARCHITECTURE §17.10.1）
config/decor_catalog.json 生成物；改 tools/decor_catalog/rules.py 后重跑
scripts/spawn/rooms/      纯 C# 规划 / 校验 / 栅格化（零 Godot 依赖）——地编读，开发/ECS 写
scripts/host/Dungeon*     L4 表现层（唯一允许 using Godot 的地方）——开发写，地编读
scripts/host/debug/       地图视图 / 调参面板 / 训练房——开发写，地编用
scenes/debug/             DungeonMap2D.tscn / TrainingRoom.tscn
tools/probes/             只读探针（规则1 白名单）
tools/dungeon_check/      离线属性测试控制台工程
tools/decor_catalog/      装饰目录生成器（build_catalog.py / rules.py / render_contact_sheet.py）
data/excel/               ★唯一真源（策划编辑区）
data/generated/           生成物，禁止手改
```

> 地编**只写**：`config/` 下的装饰规则（经生成器）、`data/excel/` 里与自己相关的列（与策划协作）、
> 以及自己的文档。**不写** `scripts/**`、`scenes/**`、`tools/**`。需要改代码 → 提给开发 / ECS 模块。

---

## 4. 验证手段（可跑的场景 / 只读探针 / 无头自检）

> 地编的内容**几乎全是「静默失效」高发区**：窗口缩放配错不报错、隐藏房判隔离用错泛洪不报错、
> 物件堵死通路不报错、用配置 A 测却用配置 B 上线也不报错。所以本节的验证**不是可选项**。

### 4.1 离线属性测试：`tools/dungeon_check`（首选，无头、毫秒级）

| 命令 | 断言什么 | 出处 |
|---|---|---|
| `dotnet run --project tools/dungeon_check -- --seeds 200` | 每个种子通过 6 条不变式；同 seed 两次结果相同；槽位不重叠；房型语义 | `tools/dungeon_check/README.md` |
| `-- --map --seeds 1000` | 2D 栅格体检：泛洪可达性、房间被障碍割裂、陷阱格、Boss 可达、隐藏房隔离 + 经秘密通道可达；`--gallery N` 打印字符图 | ARCHITECTURE §14.8 |
| `-- --props --seeds 500` | 物件不堵死通路、不压门、不压非地面 | ARCHITECTURE §14.9 |
| `-- --options` | **用 3D 实机那套 `DungeonOptions` 默认值**再跑一遍同样的属性测试 | ARCHITECTURE §14.8.1、`tools/check_all.ps1` step 4b |
| `-- --bench --seeds 500` | 重算一次（规划 + 栅格化 + 体检）的耗时，证明「拖滑条实时重画」成立 | ARCHITECTURE §14.8 |

退出码：全绿 `0` / 断言失败 `1` / 参数错误 `2`。
**为什么它不在 `tests/`**：`tests/` 只编译 `scripts/core/**`（ECS.md §10 的证明性约束），
规划器在 `scripts/spawn/rooms/`，所以单独一个控制台工程。详见 `tools/dungeon_check/README.md`。

> **必须记住的教训（§14.8.1）**：2D 层 10000 种子全绿，3D 实机却开不了——
> 因为工具测的是 `9×24 / side=3`，实机默认是 `8×8 / side=1`，后者根本塞不下隐藏房的隔离带。
> 所以**凡是改了默认配置，必须再跑一次 `--options`**。

### 4.2 实时调参面板：`scenes/debug/DungeonMap2D.tscn`（带窗口跑）

| 项 | 内容 |
|---|---|
| 打开方式 | Godot 编辑器打开 `scenes/debug/DungeonMap2D.tscn` 按 F6，或 `--path . scenes/debug/DungeonMap2D.tscn` |
| 左侧 | 俯视图：正方形 = 房间格 · 颜色 = 房型（取自配色表）· 方块 = 障碍 · `+` 门 · `?` 秘密通道 · 起始→Boss 最短路高亮；**地图右侧**是一列竖排图例（色块 + 名称 + 统计，字号 20px） |
| 右侧 | **20 根可左右拖动的滑块**：14 根「地图形状」（种子/网格宽高/小房间边长/目标房间数/容差/隐藏房/特殊房上下限/Boss 最小深度/环路距离/障碍密度/障碍离墙/格子像素）+ 6 根「物件」（每房建筑数/建筑最大边长/每房奖励数/每房可破坏数/每房不可破坏数/物件离墙） |
| 交互 | 拖动 30 ms 节流重算、松手立刻算；生成失败只在状态行说明原因，**地图保留上一次成功的结果** |
| 自检 | 场景根打印 `sliders=` / `spinBoxes=` 计数，并断言 `spinBoxes==0`（无头环境没眼睛看面板，数字真实存在的节点类型才算证据） |
| 坑 | 面板参数**不写回任何 `.tres`**：它是「试参数」的现场，**调出满意的值必须回填真源**（§3.2），否则关掉窗口就丢了 |
| 图例 | 站在**地图右侧的空白列**（`DungeonMap2DView.DrawLegend`），一份色块取自当前配色表；`格子像素` 按「地图宽 + 图例列宽」反算，所以图例永远放得进可视区 |

> 出处：ARCHITECTURE §14.8「实时调参面板」表 + `scripts/host/debug/DungeonMap2DPanel.cs` 类注释与 `SliderRanges`。

### 4.3 只读探针（规则1 白名单 `tools/probes/**`）

| 探针 | 钉住什么 | 跑法要点 |
|---|---|---|
| [`tools/probes/dungeon_map_autofit_probe.gd`](../../../tools/probes/dungeon_map_autofit_probe.gd) | **2D 地牢地图随窗口自动缩放铺满**：四种窗口下格子像素与图尺寸 | **必须带窗口**（不能 `--headless`，没有 DisplayServer 就没有窗口可测）；实测 14.64 → 37.14 px；**撤掉改动后恒为 16 px、报 2 个失败** |
| [`tools/probes/viewport_resize_probe.gd`](../../../tools/probes/viewport_resize_probe.gd) | 窗口尺寸 → 3D 视口尺寸（`stretch/mode` 是否被配错） | 空场景视口对比；`canvas_items` 下 4/4 失败，`disabled` 下 4/4 通过 |
| [`tools/probes/scene_window_resize_probe.gd`](../../../tools/probes/scene_window_resize_probe.gd) | 拿**真实场景**测「窗口变大 → 3D 相机画面真的变大」（上一个探针测不出的相机投影问题） | 传场景 `res://` 路径参数 |
| [`tools/probes/camera_follow_probe.gd`](../../../tools/probes/camera_follow_probe.gd) | 相机节点**真的**连到人物身上（读 `Camera3D.global_position`，抓真实 `Tick` 链路） | 可 `--headless`；配 `CameraFollowSelfCheck` |
| `scripts/host/debug/CameraFollowSelfCheck.cs` | 相机跟随的阶段机自检（静置收敛 / 跟随残差 ≤ 2 cm / 前瞻偏移收敛 / 翻转收敛） | 由宿主场景按命令行开关触发 |

> **为什么地编要盯这几个探针**：`stretch` 配错、`Node2D` 拿不到布局尺寸，
> **编译、日志、架构守卫都发现不了**，只表现为「画面没反应」。这类 bug 只能靠「设尺寸 → 读回尺寸 → 比对」钉住（ARCHITECTURE §7.5.1 / §7.5.2）。

### 4.4 3D 白盒场景：`scenes/main/DungeonDebug.tscn`

| 项 | 内容 |
|---|---|
| 定位 | 整层地牢的 3D 白盒：布局 → 栅格化 → 物件 → 视图，**四类物件全上** |
| 地编看什么 | 房间是否连通、门洞是否对齐、装饰是否穿墙/挡路、物件密度是否合理 |
| 注意 | 它最容易被 `DungeonOptions` 默认值坑到（§14.8.1）；跑之前先 `--options` 体检一遍 |

### 4.5 训练房：`scenes/debug/TrainingRoom.tscn`（安静看一个人）

| 项 | 内容 |
|---|---|
| 组成 | 灰色房间（`RoomCells`×`RoomCells`，默认 8×8）× `CellMeters`（默认 1 m）；逐格棋盘地板；四面墙（`WallHeightMeters` 默认 2.4 m）；`test_hero`；一个纯表现假人 |
| 为什么存在 | 看人物的前后左右、移动手感、子弹方向；**没有随机、没有生成、每次打开都是同一张图** |
| 无头自检 | `TrainingRoomScene.EmitSelfCheck()` 延迟 5 帧后数**场景树里真实存在的节点**：`[训练房 自检] room=8x8 cells cell=1.0m floors=64/64 walls=4/4 dummy=True parts=2 hero=True variant=test_hero` |
| 已知欠账（有意的） | 假人**不是 ECS 实体**，子弹会直接穿过去。要测伤害时换成 `ActorSpawnRequest` 生成的真实体 |
| 素材 | 全用几何体 + 纯色材质，**不引用 `素材/` 下任何 `.glb`** —— 与 §5 的「全部白模」一致 |

> 两个坑（ARCHITECTURE §14.10）：① 宿主**不能**对 `CharacterSession` 的 pipeline 再 `Add`；
> ② 自检不能在 `_Ready` 里立刻数 `Player`（第一次 `Tick` 才出生）。

### 4.6 装饰目录自检

```powershell
python tools/decor_catalog/build_catalog.py --check    # 只读：JSON 与 素材/ + rules.py 是否一致
```

完整重跑链路（生成 JSON → 引擎量测 → 并入 → 重写 md）见 `decor_catalog.md` §8；
小样图重画用 `render_contact_sheet.py`（需 Pillow，用 DSH 自带 python）。**命令原文见 `decor_catalog.md` §8，此处不抄。**

### 4.7 收工前的最小验证集（地编自查清单）

1. 改的是数据 / 表现属性，不是消费方代码 → 否则转开发 / ECS；
2. `dotnet run --project tools/dungeon_check -- --seeds 200` 全绿；
3. **改了默认配置** → 再跑 `-- --options`；
4. 改了栅格 / 物件 → 跑 `--map` / `--props`；
5. 改了 2D 视图显示 → 跑 `dungeon_map_autofit_probe.gd`（带窗口）；
6. 改了装饰 → `build_catalog.py --check`；
7. 改了 `.tres` 里的临时值 → **回填真源**（§3.2），并在 `ARCHITECTURE.md` §14 就地覆盖 + 变更日志留一行。

---

## 5. 首版「全部先跑白模」：含义、影响、回填路径

> 出处：ARCHITECTURE **§17.10.1**（2026-11-10 决定）+ §14.10 训练房的素材口径。本节只做地编视角的转述，细节以原文为准。

### 5.1 决定与含义

用户原话：**「先全部用白模即可，占位不要使用『素材』的路径，后续需要挑选让如项目资源文件夹中。」**

| 项 | 现状 | 对地编意味着什么 |
|---|---|---|
| `VisualPacks.ModelPath` | **全部 24 个包留空**（`combat.xlsx` 20 + `characters.xlsx` 4） | 3D 里看到的怪物 / 武器 / 子弹都是**白模**（`ActorViewDirector` 回落到白模 Box/Capsule） |
| `TintHex` / `AnimClips` | 一并清空（依赖模型才生效，留着是死字段） | 不要期待任何染色 / 动画 |
| 包壳（`VisualPacks.Id`） | **全部保留**，只当「桶键」用；`Objects.VisualPack` 的 26 条引用一条未改 | 回填时**只改 `ModelPath` 一格**，其它列不动 |
| `Scale` | 保留 | 模型回来直接生效 |
| `vp_none` | 语义改为「**永远**不要模型」（特效 / 纯逻辑物件） | 「暂时没有」≠「永远没有」：前者是校验器的 `[提醒]` 清单，后者是 `vp_none` |
| 校验器 | 「有能力位物件必须有模型」由 **error 降为 `[提醒]`** | 白模能跑；每个待配模型的物件留一条提醒，**回填后自动消失** |

**为什么占位路径必须清零（而不是留着「诚实标注」的占位）**：
`素材/` 是美术原料区，不是项目运行时资源区，路径随时会被挪动 / 清理。
历史事故：`res://素材/Characters/TestHero.glb` 正因如此**不存在却静默回退白模，长期无人发现**。
→ 地编**不要**、也**不要建议**在运行时资源里写 `res://素材/...`。

### 5.2 回填路径（等美术挑好资产之后）

```text
① 美术把选定资产放进【项目资源文件夹】（不是 素材/）
② 改 combat.xlsx / characters.xlsx 的 VisualPacks.ModelPath 一格（+ 需要的话 TintHex / AnimClips）
③ 重跑数据管线生成 data/generated/ —— 不手改生成物
④ 跑校验器：待配模型的 [提醒] 应逐条消失
⑤ 在 3D 场景里确认尺寸：这一步是地编的活 —— 见下
```

**第 ⑤ 步为什么归地编**：`ModelPath` 填的是**路径**，而「这个模型摆在房间里会不会穿墙 / 会不会太高 / 会不会挡住俯视视野」
是**摆放体积**问题。可用的判据：

| 判据 | 出处 |
|---|---|
| 装饰的缩放基准：`placement.Scale = 1` ⇔ 最大水平边 ≈ 6.0 m；目标区间 3~6 m（≈ 半格） | `decor_catalog.md` §4 |
| 装饰的 `sizeMeters[1]` = 贴地摆放的实际高度，用于判断「是否高过墙」 | `decor_catalog.md` §4 |
| 小样图里的人体圈（0.6 m 肩宽）与 2 m 肩线，用来判「挡视野 / 高过角色」 | `decor_catalog.md` §10 |
| 房间尺度：1000 m/s 级弹速 + 12 m 一格 | `decor_catalog.md` §4 |

**装饰与外观包是两条不同的回填链**——地编必须分清：

| | 装饰（`decor_catalog`） | 外观包（`VisualPacks`） |
|---|---|---|
| 谁定 | **地编** | 角色模块（口径）/ 策划（填值） |
| 真源 | `config/decor_catalog.json`（由 `rules.py` 生成） | `combat.xlsx` / `characters.xlsx` |
| 路径形态 | 允许（今天就是）`res://素材/...`，并带 `needsAssetImport` 与缺失降级 | **首版一律留空**，将来指向项目资源文件夹 |
| 缺失时 | 退回 BoxMesh 标记装饰，场景照样能跑 | 回落白模 Box/Capsule |

> ⚠️ 这两条链的路径口径**目前不一致**（装饰允许指 `素材/`，外观包禁止指 `素材/`），见 §9 Q2。

### 5.3 地编现在能做的「白模时代验收」

- 3D 白盒 / 训练房能开、能走、能打 → 说明空间与摆放成立；
- 真值清单是校验器的 `[提醒]`（「谁还在等资产」）—— 但**提醒的行数不在 `decor_catalog.md` 里**，本 README 不写这个数字，
  只在需要时现场跑校验器看（避免第二份真源，见 `_shared/BOUNDARY.md` §5 规则 4）；
- 装饰目录的 101 条**不受此决定影响**（它们是 `素材/` → JSON 的美术原料目录，不是运行时外观包）。

---

## 6. 常见任务 → 该读哪一节（路由表）

| 我想… | 先读 | 再读 | 改完必须 |
|---|---|---|---|
| 调地牢大小 / 房间数 / 隐藏房 / 环路 | `ARCHITECTURE.md` §14.2（四层）+ §14.4（六条不变式） | `DungeonOptions.cs` 字段注释 | `dungeon_check --seeds` + **`--options`** |
| 加一种「新拓扑职责」的房型 | §14.3 表 + §14.4 | `rules/06` §2（预算） | 同步 §14.3 表 + `tools/decor_catalog/rules.py` 的 `ROOM_CATEGORIES` |
| 只是想让某个房型出不同内容（新商店 / 新机关） | §14.3 末尾「新增一种房间的正确做法」 | — | 加一行 `RoomCategorySpec`，**不写代码** |
| 调 2D 栅格外观（障碍、留白、棋盘） | §14.8 | `DungeonMapConfig.cs` | `dungeon_check --map --seeds 1000` + 面板 + `dungeon_map_autofit_probe.gd` |
| 加 / 调物件（建筑、箱子、奖励） | §14.9 四类放置规则 | `DungeonPropConfig.cs`、`DungeonPropPlanner.cs` | `dungeon_check --props --seeds 500`（堵路必须为 0） |
| 新增一个装饰 | `decor_catalog.md` §8「新增一个装饰」 | §6（跳过清单）、§4（缩放口径） | `build_catalog.py` 重跑 + `--check` |
| 想看看有哪些装饰可选 | `decor_contact_sheet.png` | `decor_catalog.md` §7 清单 | — |
| 改房型配色 / 中心建筑 | `ARCHITECTURE.md` §14（L4 表现：新房型外观 = 数据 0 代码）+ `rules/04` §8.1「颜色」行 | `DungeonRoomStyle.cs` 字段注释 | 建 `.tres` 或按 §3.2 口径落地；见 §9 Q5 |
| 2D 地图窗口拉大后图不变 / 太小 | §7.5.2 | `DungeonMap2DView.cs` 的 `FitToViewport` | `dungeon_map_autofit_probe.gd`（带窗口） |
| 窗口拉大 3D 画面不变 | §7.5.1 | `project.godot` 的 `[display]` | `viewport_resize_probe.gd` / `scene_window_resize_probe.gd` |
| 相机跟随不丝滑 / 跟丢 | §15.8.1 数据来源表 + §15.8.2 分层 | `characters.xlsx` 的 `CameraPacks` | `camera_follow_probe.gd` + `CameraFollowSelfCheck` |
| 建一个 3D 测试场景（灰房间 + 假人） | §14.10 全文 | `TrainingRoomScene.cs` 的 `[Export]` 字段 | 无头跑，读 `[训练房 自检]` 行 |
| 从 `素材/` 挑模型看真实外观 | §24.3 目录 + §24.5 素材过滤 | `tools/asset_viewer2/` | 用 `--stress N` 自检（§24.8） |
| 模型回来后把白模换真模型 | §17.10.1（§5） | `decor_catalog.md` §4（缩放口径） | 校验器 `[提醒]` 清零 + 3D 目视尺寸 |

> 参照 `rules/07-definition-of-done.md` §8.2「章节路由表」的写法：**按任务类型找章节，而不是从头读**。

---

## 7. 我读一部分就够的（次归属）

来源：[`../_shared/BOUNDARY.md`](../_shared/BOUNDARY.md) §4.3「15 份非 ARCHITECTURE 文档的逐节切分」。

| 文档 | 我读哪几节 | 不读哪几节 / 为什么 |
|---|---|---|
| [`rules/04-data-driven.md`](../../rules/04-data-driven.md) | §8.1 字段级落位（颜色 / 资源引用）、§3 红线（顺带扫一眼）、§8 三条路径的选择标准 | §5 Excel 表规范、§6 示例表、§9 V1~V9 校验 → 策划；§7 字段→类型映射 → 开发 / ECS |
| [`rules/06-extensibility.md`](../../rules/06-extensibility.md) | **§2 扩展预算表**（新关卡/波次 = 0 代码）+ §2 末尾「超预算的处理顺序」 | §4 夺舍走查 → 角色 / ECS；§5 事件/钩子 → 开发 |
| [`rules/01-gdscript-ban.md`](../../rules/01-gdscript-ban.md) | §5.1 `tools/probes/**` 探针特批（跑探针时知道「为什么这里允许 `.gd`」） | 其余 → 开发 / 角色；`AGENTS.md` §4 白名单表里已有一行结论，够用可不读 |
| [`rules/03-abstract-first.md`](../../rules/03-abstract-first.md) | 只需结论：§3.1 三件套（边界 · 抽象 · 扩展点）、§3.4 一个概念一份契约 | 全文 → ECS / 角色 |
| [`codex/index.html`](../../codex/index.html) | 需要了解「装饰以外的摆放语义」时翻一眼（怪物/子弹的图鉴卡片） | 主要读者是策划 |
| [`design/character-hud-contract.md`](../../design/character-hud-contract.md) | 不看 | 全文 → 角色 |
| [`ECS.md`](../../architecture/ECS.md) | **只看 §1 J1~J7 总表里的 J6 行**（查询形状稳定 / 按 chunk 批处理）+ §12 待办里「多世界（房间预加载 / 关卡流式）」一行 | 全文 → ECS 架构师。⚠️「房间为什么不做 ECS 实体」的**理由原文在 ARCHITECTURE §14.2 末段**，不在 `ECS.md` 里 |
| [`ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) §14.5 | 宿主边界：`DungeonOptions : Resource` → `ToConfig()` → 纯 C# | 其余 ECS 章节 → ECS |

---

## 8. 我不管什么（Out + 交界处）

**明确不管（Out）**：

| 不管什么 | 归谁 | 依据 |
|---|---|---|
| 写 `IDungeonPlanner` / `IRoomContentSpawner` / `DungeonSceneBuilder` 的实现代码 | 开发 / ECS | `_shared/BOUNDARY.md` §2.2 ② Out |
| 决定数值（伤害、血量、掉落、价格） | 策划 | 同上 |
| 决定角色数值包组合、五槽技能 | 角色 | 同上 |
| 在 `素材/` 里增删文件、改美术资产 | 美术 | `AGENTS.md` §5「`素材/` 只读」 |
| 把 `素材/` 当运行时资源路径引用 | 禁止（谁都不行） | ARCHITECTURE §17.10.1 |
| `VisualPacks.ModelPath` 填什么 | 角色（口径）+ 策划（填值） | §17.10 唯一入口 |
| `DungeonDebugScene` / `DungeonMap2D*` 的接线、HUD、渲染管线 | 开发 | `_shared/BOUNDARY.md` §2.2 ②「与开发」 |
| 改 `config/architecture-rules.json` 放行白名单 | 开发 | `AGENTS.md` §4「三处缺一不可」 |

**交界处（三方共读，谁都不能单方面改）**：

| 交界 | 分界口径 |
|---|---|
| 与 **角色** | `VisualPacks` 的**归属权**在角色；地编只管**尺寸 / 摆位**含义（§5.2 第 ⑤ 步） |
| 与 **开发** | 宿主场景与探针的**接线**归开发；地编负责「应该长什么样、怎么验证」 |
| 与 **策划** | 房间可出什么装饰由 `config/decor_catalog.json` 的类别掩码决定，**地编是它的业主**；策划只在需要新增装饰种类时提需求 |
| 与 **ECS** | 房间**不做** ECS 实体（生命周期是「一层」级，不可变可序列化）；房间里的**内容**（怪、可破坏物、道具）仍是 ECS 实体，由 L3 生成。判据原文见 ARCHITECTURE §14.2 末段 |

---

## 9. 待核对 / 我没把握的点

> 按 `_shared/BOUNDARY.md` §5 规则 6：**发现过时或矛盾不直接改原文**，写在这里，交整合阶段 `t-map` 统一裁决。

| # | 问题 | 现判 / 证据 | 影响 |
|---|---|---|---|
| Q1 | **地牢参数的真源到底是 `progression.xlsx` 还是 `DungeonOptions`？** 文档内部有分歧：ARCHITECTURE §14.5/§14.7 与 `DungeonConfig.cs` 类注释说「将来从 `progression.xlsx` 的 `Dungeon` Sheet 读」；而 `DungeonOptions.cs` 类注释说「Excel 落地后只需在 `DataLayer` 里把本资源的字段按 Def 覆盖一遍」。另外**`data/excel/` 下今天没有 `progression.xlsx`**（只有 combat / characters / l10n） | 现判：**今天以 `DungeonOptions` 为准，`progression.xlsx` 是目标态**（§3.2 归属矩阵按这个写）。两处注释不是矛盾而是「数据源 vs 覆盖方式」的不同层表述，但**地编读完会困惑** | 地编改参数时到底走 Inspector 还是 Excel；建议整合时在 §14.7 明确一句 |
| Q2 | **装饰路径口径与「全部白模」口径不一致**：`decor_catalog.md` §5 说运行时用 `res://素材/...glb` 直接加载、缺 `.import` 会报 `ResourceLoader.exists = false`，而 §17.10.1 明令「占位不要使用『素材』的路径，素材是美术原料区不是运行时资源」。两者目前**并存且都「成立」**（装饰走目录 + 缺失降级，外观包走空路径） | 现判：**不是 bug，是两条链**（§5.2 对照表）。但 `decor_catalog.md` §5 的措辞容易被读成「运行时可以直接用 `素材/`」，建议将来补一句「装饰目录是例外」 | 新人（或 AI）可能照 §5 在代码里写 `res://素材/...`，撞 §17.10.1 红线 |
| Q3 | **`decor_catalog.md` 的 `.gitignore` 说法已过时**：§5 原文写「本工程无 .git 仓库，`.gitignore` 暂无 `tmp/` 条目」。但仓库已入 git（`AGENTS.md` 变更日志 2026-11-04 v3）。 | 现判：**该句过时**，建议改成「若将来 `tmp/` 需要入库，请加 `.gitignore`」 | 低。只是误导，不影响数据 |
| Q4 | **`decor_catalog.md` §10 说「真实外观请用 `tools/asset_viewer/`」**，但那是旧版（Win32+D3D11）；现行的是 `tools/asset_viewer2/`（Vulkan+ImGui，ARCHITECTURE §24） | 现判：应指向 `asset_viewer2` | 低。地编挑素材时可能去开旧工具 |
| Q5 | **`DungeonRoomStyle` 的 `.tres` 在仓库里不存在**：`DungeonRoomStyle.cs` 注释说「可在 `.tres` 覆盖」，`DungeonRoomStyleSet.CreateDefault()` 是「`.tres` 未提供时宿主用它兜底」；实测 `scenes/` 下只有 `character_hud_skin.tres` | 现判：**内置默认是当前唯一生效的配色**。改色只能改代码常量（带 `arch-ok`）或新建 `.tres`——前者违背「配色是数据」的意图 | 中。地编「改房型配色」今天实际做不了纯数据操作 |
| Q6 | **调参面板的参数没有落盘通道**：面板调出的值不写回 `DungeonOptions`/`.tres`/Excel（`DungeonMap2DPanel.cs` 里没有任何保存逻辑，且刻意与 `DungeonOptions` 默认值解耦） | 现判：面板**只是试参数**，满意后必须人工回填真源（§4.2 已写进口径） | 中。容易「调完就丢」，建议面板加一行「当前值可粘走的文本」或在状态行提示 |
| Q7 | **`BOUNDARY.md` §1 与 §4.2 有两处小笔误**：① 清点口径写「15 份原有文档一个字节都不动」，实际是 16 份（17 减 1 份 `.import` 仍算文件）；② §4.2 把 §7 的主读者写成「开发」，而 §7.5.1/§7.5.2 是地编的必读 | 现判：不影响结论，地编照读 §7.5.1/§7.5.2 | 低。建议整合时订正文字 |
| Q8 | **§14 的「历史欠账」散落在三处**：`DecorLayout` 的 v1 实现是「最小」；「新房型外观 = 数据 0 代码」写在 §14.2 的 L4 行，而今天落地形状是「一行 `DungeonRoomStyle` 配色」（本质仍是代码里的默认行，见 Q5）；门的状态机尚未落地（§14.7 下一步②） | 现判：都不影响今天验收，但**装饰在 3D 里目前不一定是真模型**——`DungeonDecorCatalog` 加载失败/为空时退回 BoxMesh | 中。地编看 3D 白盒时别把「方块」当成 bug |

---

## 10. 变更日志（本文自身）

| 日期 | 版本 | 任务 | 变更摘要 |
|---|---|---|---|
| 2026-11-11 | v2 | **2D 地图图例搬到右侧 + 字号 11→20** | 用户反馈「标注字太小」「不要放到底部，放到地图右侧，那里空了一大片」。① 图例由「地图下方三行横排小字（字号 11、按『一字≈字号』估宽）」改为**地图右侧竖排一列**（分组标题 + 一项一行 + 统计行），色块底边贴文字基线；② 字号 `DebugUiStyle.FontLegend` 11 → **20**，其余图例间距（色块 18 / 行高 28 / 项距 6）随字号同步放大；③ `FitToViewport` 的格子像素算式**横向让出图例列**（`LegendMinColumnWidth + LegendTopGap`），否则图例会伸出可视区被裁掉并逼出横向滚动条；④ 图例底板由半透明（实测整列平均亮度仅 51/255）改为**不透明深底**；⑤ 场景自检新增断言 `legend[rightOfMap=… inside=…]`，无头跑一遍即可确认图例真的在地图右侧、真的在视口内。**验证**：1600×900 与 1920×1080 自检 `rightOfMap=True inside=True`、`legendFont=20`；像素量测 20 段文字行全部落在 x=[403,663] 的右侧列内、行高 20–22px；`dungeon_map_autofit_probe.gd` 4/4 通过 | `scripts/host/debug/DungeonMap2DView.cs`、`scripts/host/debug/DungeonMap2DPanel.cs`、`scripts/host/debug/DungeonMap2DDebugScene.cs`、`scripts/host/ui/DebugUiStyle.cs` |
| 2026-11-11 | v1 | `t-level` 地编模块索引 | 建立：地编能改的六类内容（房间与地牢 / 房型配色 / 2D 栅格 / 物件随机生成 / 装饰目录 / 外观包）、必读清单（跨模块 4 + 自读 7）、真源与生成物边界 + **归属矩阵（`DungeonOptions` vs `progression.xlsx`）**、验证手段（dungeon_check 六种模式 / 2D 调参面板 20 根滑条 / 5 个只读探针 / 3D 白盒 / 训练房自检 / 装饰自检）、**首版全白模的含义与回填路径**、常见任务路由表、次归属、Out 与交界处、待核对 Q1~Q8 |
