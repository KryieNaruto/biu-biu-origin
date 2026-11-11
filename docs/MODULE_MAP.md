# MODULE_MAP · 五大模块总索引

> 版本: v1 ｜ 建立日期: 2026-11-11 ｜ 产出任务: `t-map`（整合）｜ 负责人: `integrator`
> 性质: **导航文档（只增不改）**。本文只回答三件事：**17 份文档分别归谁 · 每种任务去哪找 · 哪些地方文档已经过时**。
> 不描述架构形状、不复制契约、**不修改任何原始文档与模块 README**（发现的错全部记进 §5 待核对表）。
> 上游基线: [`docs/modules/_shared/BOUNDARY.md`](modules/_shared/BOUNDARY.md)（模块边界与文档归属总表的完整版）。

---

## 0. 一页速览

| 你问 | 答案 |
|---|---|
| 这份文档归谁 | 看 §2 **文档 × 模块矩阵**；完整的「逐节切分」在 [BOUNDARY.md](modules/_shared/BOUNDARY.md) §4 |
| 我是某个模块的人，先读哪 | 看 §3 **模块入口卡**（每张卡=一份 README 的入口） |
| 任务派下来了，怎么找人 | 看 §4 **任务路由表**（按「我要改什么」反查模块与章节） |
| 新来的人 | 看 §1 **新人 30 分钟路线** |
| 文档哪里已经不对了 | 看 §5 **待核对汇总（27 条）**——**只记录，本次未改任何原文** |
| 有冲突怎么办 | **一律以原始文档为准**；`ARCHITECTURE.md` 是唯一架构真源，ECS 形状以 `ECS.md` 为准 |

**三条全局约定（已由 captain 裁决）**：
1. **目录名用中文**：`docs/modules/策划|地编|角色|开发|ECS架构师/`。BOUNDARY §2.1 里写的 `planning/level/character/dev/ecs` 是**已作废的草稿**，不要按它建目录、也不要改别的 README 去迎合它。
2. **规则编号只写文件名口径** `rules/NN`（如 `rules/04`）。工程里存在**规则编号双体系**（`rules/03` 文内自称「规则 5」、`rules/05` 自称「规则 6」、`rules/06` 自称「规则 7」），写「规则 4」这类数字别名会指错文件（见 §5 P20）。
3. **只写章节号，不写行号**（行号随文档就地更新漂移）。

---

## 1. 新人 30 分钟路线

> 目标：30 分钟后你知道「这个项目是什么、文档在哪、我该找谁」。**不求读懂细节，只求建立地图。**
> 每段时间都标了「读完能回答什么」——答不上来就回去补，别硬往下走。

| 时间 | 读什么 | 读完能回答 |
|---|---|---|
| 00–05 | [AGENTS.md](../AGENTS.md) §0 三十秒速读 + §2 八条铁规表 | 技术栈是什么？哪 8 条铁规？违反哪条会被打回？ |
| 05–08 | [ARCHITECTURE.md](architecture/ARCHITECTURE.md) §0 一页速览 + §11 变更日志**首屏 5 行** | 现在是什么阶段？最近 5 个任务改了什么？ |
| 08–11 | [rules/07](rules/07-definition-of-done.md) §2 六步检查表 + §8.2 章节路由表 | 做完一件事要跑哪几步？改代码该读哪一节？ |
| 11–14 | [rules/08](rules/08-parallel-sessions.md) §0 + §2 六步 + §8 反例 | 什么时候必须开 worktree？在 `main` 上直接改犯什么规？ |
| 14–18 | 本文 §2 矩阵（只扫「谁必读」列） | 17 份文档分别归谁？ |
| 18–24 | **你自己模块的 README**（§3 入口卡）的 §0 速读 + 「必读」表 | 我只读哪几节？哪几节是别人的？ |
| 24–27 | 你模块 README 的「常见任务路由」节 + 「我不管什么」节 | 我要做的事去哪查？什么不归我管、该找谁？ |
| 27–30 | 本文 §5 待核对表（只扫 P1~P3 与你自己模块那几行） | 哪些「文档说的」和「代码实际」不一致，别照着错的地方改 |

**第二天的按任务深读**：见各模块 README 的「常见任务 → 该读哪一节」；开发模块 README 另有一张「新人第一天时间表」（约 4 小时，含环境验证）。

---

## 2. 文档 × 模块矩阵

> 图例：**●** 主归属（这份文档的「家」，该模块 README 必须收录并逐节路由）｜**○** 次归属（确实要读，只需指定章节）｜**◆** 跨模块必读（五个模块都要遵守，违反即打回）
> 「→」表示首次归属的模块。17 个文件里 `decor_contact_sheet.png.import` 是 Godot 生成的引擎元数据，**不是文档**，全模块皆空。

### 2.1 矩阵（行 = 文档，列 = 模块）

| # | 文档（`docs/` 下） | 行数 | 策划 | 地编 | 角色 | 开发 | ECS 架构师 |
|---|---|---|:--:|:--:|:--:|:--:|:--:|
| 1 | [`architecture/ARCHITECTURE.md`](architecture/ARCHITECTURE.md) | 3,228 | ○ §4 §5 §17 §20~§23 | ○ §14 §15.8 | ○ §15 §18 §19 §22 | ○ §1 §2 §7 §9 §10 §16 §24 | **●** 托管 + §13 / §17 / §19~§21 |
| 2 | [`architecture/ECS.md`](architecture/ECS.md) | 519 | ○ §4.1 D1 | ○ §1 J6 · §12 | ○ §4 §6 | ○ §9 §11 | **●** 全文 |
| 3 | [`rules/01-gdscript-ban.md`](rules/01-gdscript-ban.md) | 296 | ○ §2 | ○ §5.1 | ○ §2 | **●** | ○ §2 |
| 4 | [`rules/02-tech-selection.md`](rules/02-tech-selection.md) | 595 | ○ §1 · §7 | — | ○ §1 T2 | **●** | ○ §2 · §4 |
| 5 | [`rules/03-abstract-first.md`](rules/03-abstract-first.md) | 535 | ○ §3.4 | ○ 只知结论 | ○ §3.4 | ○ §3.1 · §4.1 | **●** §3.4 · §6.1 + §4.1 |
| 6 | [`rules/04-data-driven.md`](rules/04-data-driven.md) | 583 | **●** 全文 | ○ §8.1 | ○ §7 | ○ §9 · §10 | ○ §4 · §7 |
| 7 | [`rules/05-numeric-package.md`](rules/05-numeric-package.md) | 811 | **●** §3~§7 | — | ○ §3 · §4 | ○ §7.3 | ○ §3 · §6 · §8 |
| 8 | [`rules/06-extensibility.md`](rules/06-extensibility.md) | 509 | ○ §2 | ○ §2 | ○ §2 · §4.7 | ○ §5 | **●** §2~§10 |
| 9 | [`rules/07-definition-of-done.md`](rules/07-definition-of-done.md) | 466 | ◆ | ◆ | ◆ | **●** | ◆ |
| 10 | [`rules/08-parallel-sessions.md`](rules/08-parallel-sessions.md) | 405 | ◆ | ◆ | ◆ | **●** | ◆ |
| 11 | [`design/character-hud-contract.md`](design/character-hud-contract.md) | 332 | ○ §5 · §7 | — | **●** 全文 | ○ §1 · §8 | ○ §4 |
| 12 | [`artifacts/decor_catalog.md`](artifacts/decor_catalog.md) | 530 | ○ §2 · §6 | **●** §4 为核心 | ○ §4 · §10 | ○ §5 · §8 · §11 | — |
| 13 | [`artifacts/decor_contact_sheet.png`](artifacts/decor_contact_sheet.png) | 232 KB | ○ 挑素材时 | **●** | — | — | — |
| 14 | `artifacts/decor_contact_sheet.png.import` | 976 B | — | — | — | — | — |
| 15 | [`prompts/QUICK-START.md`](prompts/QUICK-START.md) | 34 | ○ 派活 | ○ 派活 | ○ 派活 | **●** | ○ 派活 |
| 16 | [`prompts/task-prompt-template.md`](prompts/task-prompt-template.md) | 306 | ○ §3 · 附录A | ○ 同左 | ○ 同左 | **●** | ○ §3 + 附录B（快照） |
| 17 | [`codex/index.html`](codex/index.html) | 307 | **●** 只读 | ○ 参考 | ○ §22 呈现 | — | — |

> **跨模块必读只有 4 份**（◆）：`AGENTS.md`（仓库根，不在 17 份内）、`ARCHITECTURE.md` §0 + §11 首屏、`rules/07`、`rules/08`。
> `rules/03`（535 行）与 `rules/06`（509 行）**刻意不升级**为跨模块必读——它们是「实现者必读」，策划/地编只需知道结论（`rules/03` §3.4 一份契约、`rules/06` §2 扩展预算数字）。

### 2.2 主归属计数（16 份文档 + 1 份元数据）

| 模块 | 中文名 | 英文 id | 主归属份数 | 明细 |
|---|---|---|---:|---|
| `策划` | 策划 | `planning` | 3 | `rules/04`、`rules/05`、`codex/index.html` |
| `地编` | 地编 | `level` | 2 | `artifacts/decor_catalog.md`、`decor_contact_sheet.png` |
| `角色` | 角色 | `character` | 1 | `design/character-hud-contract.md` |
| `开发` | 开发 | `dev` | 6 | `rules/01`、`rules/02`、`rules/07`、`rules/08`、`prompts/QUICK-START.md`、`prompts/task-prompt-template.md` |
| `ECS架构师` | ECS 架构师 | `ecs` | 4 | `architecture/ARCHITECTURE.md`、`architecture/ECS.md`、`rules/03`、`rules/06` |
| （无主） | — | — | 1 | `decor_contact_sheet.png.import`（引擎元数据） |

> `ARCHITECTURE.md` 的「家」给 ECS 架构师是**托管**不是独占：它同时承载 §4 数据流、§7 渲染、§8 UI、§14 地牢。各模块必须读自己那几节（矩阵第 1 行），改正文时按 `rules/07` 就地覆盖**自己那一节**。

---

## 3. 模块入口卡

> 每张卡 = 一条「进门路径」。**详细内容一律去 README，本文不重复。**
> 卡里的「必读」是**该模块自己声称必读**的（已与 BOUNDARY 核对，差异见 §5 P24）。

### 3.1 策划 · `docs/modules/策划/` · id `planning`

| 项 | 内容 |
|---|---|
| 一句话 | 用 Excel 决定「玩家看到什么内容、数值是多少、什么时候出现、叫什么名字」——**不写代码** |
| 入口 | [`策划/README.md`](modules/策划/README.md)（386 行） |
| 必读 | **跨模块 4 份** + `rules/04` 全文、`rules/05` §3~§7、`rules/06` **仅 §2**、`ARCHITECTURE.md` §4/§5/§6/§15/§17/§22/§23、`character-hud-contract.md` §5+§7、`tools/data_pipeline/README.md`（仓库内改表手册，548 行） |
| 我改什么 | `data/excel/{combat,characters,l10n}.xlsx` 的**数据格**（第 6 行起）；三条路径：改数值 / 加角色 / 加武器 |
| 我不管 | 写 `*.cs`；`data/generated/`；房间怎么排（地编）；组件与系统（ECS）；数值填多少以外的一切 |
| 卡上必看 | README §4.3/§4.4 加角色/加武器六步、§5 验收口径、§6 禁令 |

### 3.2 地编 · `docs/modules/地编/` · id `level`

| 项 | 内容 |
|---|---|
| 一句话 | 决定「一局游戏的空间长什么样、房间里摆什么」，并守住素材 → 逻辑米的换算口径 |
| 入口 | [`地编/README.md`](modules/地编/README.md)（391 行） |
| 必读 | **跨模块 4 份** + `ARCHITECTURE.md` §14 全文、§7.5.1/§7.5.2、§15.8、§17.10+§17.10.1、§24、`decor_catalog.md` §2/§4/§6/§7/§11 + 小样图、`rules/06` §2、`rules/04` §8.1 |
| 我改什么 | 房间与地牢参数（`DungeonOptions`）、房型配色、2D 栅格参数、物件随机生成、装饰目录（`config/decor_catalog.json`） |
| 我不管 | `IDungeonPlanner` 等实现代码（开发/ECS）、数值（策划）、`VisualPacks.ModelPath`（角色口径 + 策划填值）、宿主接线（开发） |
| 卡上必看 | README §3.2 **归属矩阵**（地牢参数今天在哪 vs 目标态）、§4.7 收工最小验证集 7 条、§5 全白模 |

### 3.3 角色 · `docs/modules/角色/` · id `character`

| 项 | 内容 |
|---|---|
| 一句话 | 角色 = 数值包 + 技能包 + 模块集合（装配清单），全部落在数据行里；**C# 侧 0 行改动**（前提见 §0 与 P2） |
| 入口 | [`角色/README.md`](modules/角色/README.md)（270 行） |
| 必读 | `AGENTS.md` §0/§2/§3/§5/§7 → `ARCHITECTURE.md` **§15 → §18 → §19 → §22**（按此顺序，跳读 §18 看不懂 §19）→ `character-hud-contract.md` 全文 → `rules/05` §3/§4 → `rules/06` §2/§4.7 |
| 我改什么 | 一个角色/怪物的**包组合**（用哪几层包、五槽填谁、外观包指谁）、五槽技能从占位变真实现、HUD 显示什么、夺舍入口字段 |
| 我不管 | 数值具体填多少（策划）、组件/系统本身（ECS）、房间与相机摆位（地编）、SOP 与守卫（开发） |
| 卡上必看 | README §2 **五槽现状表**（只有 Dash 与 PrimaryAttack 是真实现）、§3 加角色最小清单、§5 夺舍 |

### 3.4 开发 · `docs/modules/开发/` · id `dev`

| 项 | 内容 |
|---|---|
| 一句话 | 「**所有人怎么干活**」：开收工命令级顺序、代码放哪、用什么语言、构建与守卫怎么过、哪些坑踩过 |
| 入口 | [`开发/README.md`](modules/开发/README.md)（384 行） |
| 必读 | `AGENTS.md` 全文 + `rules/01`、`rules/02`、`rules/07`、`rules/08`、`prompts` ×2（**主归属 6 份**）+ `ARCHITECTURE.md` §1/§2/§4/§7/§8/§9/§10/§16/§24 |
| 我改什么 | `tools/*.ps1`（守卫、环境、会话、check_all）、目录契约与分层矩阵、语言选型、`config/architecture-rules.json` 白名单、CI 式收工动作 |
| 我不管 | 架构形状（ECS）、数值与表（策划）、房间规则（地编）、角色包组合（角色） |
| 卡上必看 | README §3 **命令级清单**（开工 ⓿~⑥ / 收工 DoD 七行 / `check_all.ps1` 四步 / `session.ps1` 六步 + 10 个退出码）、§7 **坑清单 23 条** |

### 3.5 ECS 架构师 · `docs/modules/ECS架构师/` · id `ecs`

| 项 | 内容 |
|---|---|
| 一句话 | 别人写系统，我定形状；别人加内容，我算预算；别人改字段，我登破坏性变更 |
| 入口 | [`ECS架构师/README.md`](modules/ECS架构师/README.md)（334 行） |
| 必读 | `ECS.md` 全文 + `ARCHITECTURE.md` §0/§2.3/§3.2.0/§6/§6.1.1/§9.2~§9.5/§10/§11/§12/§13/§17/§19/§20/§21 + `rules/03` §3.4·§6.1 + `rules/06` §2~§10 + `rules/05` §3~§8 |
| 我改什么 | 组件/系统/查询/命令缓冲/帧顺序、引擎边界（`Game.Host`）、性能四路线与下沉判据、内容契约、组件完备性裁决 |
| 我不管 | Excel 数值（策划）、房间怎么排（地编）、角色技能与 HUD 内容（角色）、构建脚本与守卫实现（开发） |
| 卡上必看 | README §4 **J1~J7 摘要**、§8 **决策树 + 超预算申诉六步**、§9 技术债 D1~D13 |

### 3.6 分工口径（一句话记住）

> **组件字段语义 / 角色能做什么 = 角色模块；组件形状 / 系统组织 / 扩展预算 = ECS 模块；表里每列填什么 = 策划；空间摆放 = 地编；怎么干活 = 开发。**
>
> 交界示例（`VisualPack`）：**模型路径口径归角色，摆位尺寸归地编**。其余三方交界见 BOUNDARY §2.2 每个模块的「交界处」，以及各 README 的「我不管什么」节。

---

## 4. 任务路由表（按「我要改什么」反查）

> 这是 `rules/07` §8.2「章节路由表」的**跨模块镜像**：那一张按「任务类型→章节」，这一张按「任务→模块→再进 README 哪节」。

| 我要做的事 | 模块 | 进 README 看 | 关键原文 |
|---|---|---|---|
| 改一个伤害 / 血量 / 射速 / 冷却 | 策划 | §4.2 路径 A + 先问「这个数在哪一层」 | `rules/04` §5 · §8；`rules/05` §3 |
| 加一把武器 / 一种子弹 | 策划 | §4.4 路径 C | `ARCHITECTURE.md` §17.1 · §17.6 |
| 加一只怪 / 一个人物 | 策划（表）+ 角色（包组合） | 策划 §4.3 路径 B；角色 §3 | `ARCHITECTURE.md` §15.5 · §15.6 |
| 改中文名 / 加本地化 key | 策划 | §3.4 关键列 | `ARCHITECTURE.md` §23 |
| 调地牢大小 / 房间数 / 隐藏房 | 地编 | §6 路由表第 1 行 | `ARCHITECTURE.md` §14.2 · §14.4 |
| 加 / 调房间物件（建筑、箱子） | 地编 | §6 第 5 行 | `ARCHITECTURE.md` §14.9 |
| 新增一个装饰 | 地编 | §6 第 6 行 | `decor_catalog.md` §8 |
| 2D 地图窗口拉大后图不变 | 地编 | §6 第 9 行 | `ARCHITECTURE.md` §7.5.2 |
| 相机跟随不丝滑 / 跟丢 | 地编（手感）+ 角色（镜头包口径） | 地编 §6 第 11 行 | `ARCHITECTURE.md` §15.8 |
| 把某个占位技能做成真的 | 角色 | §2「占位改真实现六步」 | `ARCHITECTURE.md` §19（冲刺范式） |
| 改 HUD 显示什么 | 角色 | §4 | `character-hud-contract.md` §2 |
| 夺舍 | 角色（入口）+ ECS（语义） | 角色 §5 | `rules/06` §4.7；`rules/05` §4 |
| 新增一个玩法系统 / 组件 | ECS 架构师 | §8 决策树 | `ECS.md` §5 · §6；`rules/06` §2 |
| 判断「该用 C# 还是 C++」 | ECS 架构师（判据）+ 开发（流程） | ECS §8 Q5；开发 §5.2 | `ARCHITECTURE.md` **§9.4（阈值唯一真源）** |
| 判断组件「齐不齐」 | ECS 架构师 | §5.3 | `rules/03` §3.4 + §6.1 |
| 写 / 改 PowerShell 脚本 | 开发 | §5.4 | `rules/02` §3.5 |
| 构建挂了 / 编辑器刷 ERROR | 开发 | §7.2 坑 18~20 | `tools/fix_godot_scan_dirs.ps1` |
| 开会话 / 合并冲突 | 开发 | §3.4 | `rules/08` §2 · §4 · §7 |
| 不知道收工漏了什么 | 开发 | §3.2 DoD 七行 | `rules/07` §2 · §9 |
| 派任务给 AI | 开发 | §2 主归属 | `prompts/task-prompt-template.md` |

---

## 5. 待核对汇总（本次归纳发现的文档缺口 / 过期处）

> **本表只记录，本次任务未修改任何原始文档、也未修改五份模块 README。**
> 五份 README 各自记录了 Q/D/B 项共 34 条；下表**去重合并为 27 条**（合并项注明来源编号）。
> 「优先级」判据：**P1 = 会让人照着文档做出错误动作或得出错误结论**；P2 = 文档内部自相矛盾/过时，但当前有兜底；P3 = 低风险措辞或台账。

### 5.1 必须先看的 3 条（P1~P3）

| 编号 | 优先级 | 问题 | 证据 | 影响 | 建议动作 | 归属模块 |
|---|:--:|---|---|---|---|---|
| **P1** | 高 | **内容三方不一致**：`ARCHITECTURE.md` §22.3 写「五个人物」，但 `characters.xlsx` 真表只有 **9 行**人物/怪物、**没有 `test_hero_elemental`**（`git HEAD` 版同样 9 行，`orb_elemental` 武器与 `bullet_hero_orb` 子弹也查不到）；而 C# 镜像 `scripts/data/CharacterContentSet.cs` 里**却有**该角色全套（captain 核实镜像侧 6 处命中） | 角色 README Q-C1 / Q-C2；策划 README §3.1 实测 9 行 | §22.3 的「第 5 个人物」在真表里不存在；**表 ⊆ 镜像的单向比对让这种不一致测不出来**（若 parity 用例仍绿，说明它只覆盖表里有的行） | 由角色 + 策划 + 开发三方定：① 补表还是删镜像；② 把 `CharacterXlsxParityTests` 改成**双向**比对并加「镜像多出的行」用例 | 角色（主）/ 策划 / 开发 |
| **P2** | 高 | **「代码 0 行」有前提**：改 `characters.xlsx` 的数值必须**同步 `CharacterContentSet.cs` 镜像**，否则 `CharacterXlsxParityTests` 必红 | 策划 README Q2（镜像里硬编码 5 把武器数值 + `arch-ok` 注释）；角色 README Q-C3 | 策划以为「只改表就生效」，实际运行时读的是镜像；这与 §15.6/§18.1「0 行改动」的说法并存 | ① 在 `ARCHITECTURE.md` §15.5 / §22.9 坑 5 旁点明「镜像仍是手写」；② 中期把镜像改成**生成物**，届时「0 行」才成立；③ 各模块 README 已按此口径写 | 角色 / 开发 |
| **P3** | 高 | **`data/generated/` 目录当前不存在**，而 `rules/04` §5.1/§7.1、`ARCHITECTURE.md` §4.2 大量描述「生成物 .tres / index.json」 | 策划 README Q1（`ls data/` 只有 `excel/`；`ARCHITECTURE.md` §4.1 自己也写「仍缺」） | 所有「禁止手改 generated」目前是**将来规则**；现状是「运行期读 `CharacterContentSet` 内置副本 + xlsx 只被校验器与测试读」 | 在 `ARCHITECTURE.md` §4.1 现状里把「未落地」写清楚；MODULE_MAP 与各 README 一律按「将来规则」表述（已如此） | 开发 / ECS |

### 5.2 全量清单（P1~P27）

| 编号 | 优先级 | 问题 | 证据 | 影响 | 建议动作 | 归属模块 | 来源 |
|---|:--:|---|---|---|---|---|---|
| P4 | 中 | **地牢参数真源分歧**：`ARCHITECTURE.md` §14.5/§14.7 与 `DungeonConfig.cs` 注释说「将来从 `progression.xlsx` 的 `Dungeon` Sheet 读」，`DungeonOptions.cs` 注释说「Excel 落地后按 Def 覆盖本资源字段」；而 `data/excel/` 下**没有** `progression.xlsx` | 地编 README Q1 | 地编改参数时走 Inspector 还是 Excel 会困惑 | 在 §14.7 加一句「今天以 `DungeonOptions` 为准，`progression.xlsx` 为目标态」 | 地编 / 开发 | Q1 |
| P5 | 中 | **`DungeonRoomStyle` 的 `.tres` 在仓库里不存在**（`scenes/` 下只有 `character_hud_skin.tres`）→ 房型配色今天只能改代码常量（带 `arch-ok`）或新建 `.tres` | 地编 README Q5 | 「配色是数据」的意图暂时落不了地；地编「改房型配色」做不了纯数据操作 | 建一个 `.tres` 或在 §14 注明「当前唯一生效的是内置默认」 | 地编 | Q5 |
| P6 | 中 | **装饰链允许 `res://素材/...`，外观包链禁止**，两链并存且各自都「成立」（装饰走目录 + 缺失降级；外观包首版一律空路径） | 地编 README Q2；`ARCHITECTURE.md` §17.10.1 | 新人/AI 可能照 `decor_catalog.md` §5 在代码里写 `res://素材/...`，撞 §17.10.1 红线 | 在 `decor_catalog.md` §5 补一句「装饰目录是例外，外观包不受此豁免」 | 地编 / 角色 | Q2 |
| P7 | 中 | **`ARCHITECTURE.md` §7.1「现状」仍写 `stretch = canvas_items`**，而 §7.5.1 与变更日志已改为 `disabled` | 开发 README D4 | 照着 §7.1 配 `project.godot` 会复现「拖窗口画面不变」的静默失效 | 维护者下次就地覆盖时同步 §7.1 表 | 开发（托管方 ECS 回写） | D4 |
| P8 | 中 | **`ECS.md` §4.2 组件表与代码漂移**：`Projectile`/`Hurtbox` 归属层写 combat 但实际在 `scripts/core/ecs/components/OfficialComponents.cs`；`ProjectileState` 在 §4.2 无行；`ARCHITECTURE.md` §13.4 仍写「11 个官方组件」 | ECS README B2 | 新人照 §4.2 放组件会放错目录 | 按 `ECS.md` §4.2 尾注「落地时以代码为准并回写本节」做一次回写 | ECS 架构师 | B2 |
| P9 | 中 | **`decor_catalog.md` 两处过时**：§5 写「本工程无 .git 仓库，`.gitignore` 暂无 `tmp/` 条目」（仓库已入 git，`AGENTS.md` 变更日志 2026-11-04 v3）；§10 说「真实外观请用 `tools/asset_viewer/`」（现行是 `tools/asset_viewer2/`） | 地编 README Q3 + Q4 | 误导：去开已换代的工具 / 以为无版本库 | 该文件是**自动生成物**（`build_catalog.py --md`），应改**生成器的模板**而非手改 md | 地编 | Q3 · Q4 |
| P10 | 低 | **BOUNDARY.md 自身两处笔误**：§1 清点口径写「15 份原有文档一个字节都不动」（应为 16 份：17 减去 `.import`）；§4.2 把 §7 的主读者写成「开发」，而 §7.5.1/§7.5.2 是地编必读 | 地编 README Q7 | 不影响结论，仅计数与主读者标注 | 基线文档归 integrator，本次按 captain 要求**未改**，建议下一轮基线修订时订正 | integrator（基线） | Q7 |
| P11 | 中 | **`character-hud-contract.md` §7 的 sheet 清单已过时**：写「追加 4 个 sheet 段」并列 `hero_default/hero_tank/…`、`topdown_standard/…`、`kbm_standard/…`；真表现有 **10 张 Sheet**，实际为 `lit_test_hero*`、`follow_std/heavy/fast`、`pad_std/heavy/fast`，另有 §7 未提的 `Assemblies`/`VariantAssembly`/`Weapons` | 策划 README Q3 | 策划照 §7 找表会找不到 | 在 §7 顶部加「列清单以真表与 `validate_content_xlsx.py` 的 `CHARACTERS_COLUMNS` 为准」 | 角色 / 策划 | Q3 |
| P12 | 中 | **`character-hud-contract.md` 头注「收工前会被并入 `ARCHITECTURE.md` §17」**，实际并入的是 §15 / §18（§17 是基础物件抽象） | 角色 README Q-C6 | 按头注去 §17 找会扑空 | 契约文件（会话内契约，已过期）标注实际落点 | 角色 | Q-C6 |
| P13 | 中 | **`WeaponStats` 在 `character-hud-contract.md` §3 出现两次且字段不同**（一版 `ShotIntervalSeconds`+`PelletsPerShot`，一版 `ShotCooldown` 无 `PelletsPerShot`） | 角色 README Q-C7 | 两版并存容易误读 | 契约 §3 已写「以源码 XML 注释为准」，建议删掉旧版 | 角色 | Q-C7 |
| P14 | 中 | **`rules/06` §9.1「前置状态（截至 2026-11-03）」已过时**（E1/E2 需要的 Excel + 生成器已落地） | ECS README B6 | 照表中 ❌ 读，会低估「新增一个 X」的诚实口径 | 引用预算数字前先按现实核对前置件；该节需要一次更新 | ECS 架构师 | B6 |
| P15 | 中 | **`rules/04` §10 写 GS005 是 Error（阻断 CI）**，而 `tools/check_architecture.ps1` 头写 **warning** | 开发 README D2 | 级别认知不一致 | 以脚本为准（warning），但「新条目 = 打回」的语义保留 | 开发 | D2 |
| P16 | 低 | **`ARCHITECTURE.md` §18.5「v1 只有左键是真实现，其余四行占位」已过时**（§18.8 ④ 已自行更正：冲刺已实现，剩 Q/R/F 三个） | 角色 README Q-C8 | 低：同章后文已更正 | §18.5 就地修正 | 角色（回写方 ECS） | Q-C8 |
| P17 | 低 | **`data_pipeline/README.md` 多处过期**：§3.6 写 `combat.xlsx` 的 `SchemaVersion(=1)`（真表 `_Meta` 是 2）；§3.1 写「现有 40 个 tag，16 个占 BitMask」（真表 `Tags` ≈ 62 行） | 策划 README Q5 + Q6 | 策划看到过期数字会怀疑真表 | 该 README 归开发维护，就地更新 | 开发 | Q5 · Q6 |
| P18 | 低 | **校验器检查项数三处互相漂移**：`ARCHITECTURE.md` §17.6 写 4553、§22.8 写 4569、2026-11-11 实测 4531 | 策划 README Q4 | 任何写死的数字都会随内容增长漂 | 各文档**只写「退出码 0 + 最后一行 OK」**，不写具体项数（策划 README 已按此处理） | 策划 / 开发 | Q4 |
| P19 | **高** | **`characters.xlsx` 有两张语义不同的 `Weapons`**：`characters.xlsx/Weapons`（角色视角：`WeaponId` 整数主键 / `Damage` / `ShotCooldown`）与 `combat.xlsx/Weapons`（战斗契约：`ObjectId` 外键 / 13 个规则值列）；`SkillLoads.WeaponId` 指前者，`Objects.Weapon` 指后者 | 策划 README Q8 | **双真源风险**：改一处另一处不动 → 玩家拿到的武器与战斗契约不一致 | 在 `ARCHITECTURE.md` §4.3 明确两者分工与「谁是数值真源」（策划判断：手感以 `combat.xlsx` 为准，装配以 `characters.xlsx` 为准），并加一条跨表一致性校验 | 策划 | Q8 |
| P20 | 中 | **规则编号双体系**：`AGENTS.md` §2 铁规表第 5 行写作「规则 5」但链接指向 `rules/03`；`rules/03` 文内自称「规则 5」、`rules/05` 自称「规则 6」、`rules/06` 自称「规则 7」 | ECS README B1 | 跨文档引用容易指错文件 | 全仓统一用文件名口径 `rules/NN`（本文与五份 README 已如此） | 开发 | B1 |
| P21 | 中 | **「唯一允许 `using Godot` 的边界」两种字面口径**：`ECS.md` §3/§7 写 `scripts/host/`；而 `ECS.md` §10 的分层守卫与 `tests/Layering/LayerDependencyTests.cs` 实际允许 `scripts/host/**` **与** `scripts/ui/**`（UI 约束是「不得 using 业务层」） | ECS README B3 | 新人会理解成「ui 目录里也不许用 Godot」 | 在 `ECS.md` §3/§7 补一句「ECS 侧唯一；UI 与 host 同级，另有其约束」 | ECS 架构师 | B3 |
| P22 | 低 | **`ECS.md` §6 点名的若干系统在代码里无同名 `SystemBase`**（伤害结算 / 数值同步 / 夺舍 / 技能 tick / 表现），同职责由既有系统承担 | ECS README B4 | 别把 §6 当「还有这些系统没写」来排期 | §6 是**形状契约**（阶段与顺序）不是系统清单——建议加一句醒目标注 | ECS 架构师 | B4 |
| P23 | 低 | **`config/architecture-rules.json` 里没有 `using Godot` 的规则项**，J4 实际靠「测试工程编译期失败 + `tests/Layering` 源码扫描」守卫 | ECS README B8 | 以为守卫脚本会报 J4 违规 | 在 `rules/02` §8 或 `ECS.md` §10 写明「J4 是双守卫」 | ECS 架构师 / 开发 | B8 |
| P24 | 低 | **五份 README 的必读清单与 BOUNDARY 有小幅出入**：① 策划把 `tools/data_pipeline/README.md`（548 行，仓库内文件，不在 17 份内）列为必读；② ECS 把 `rules/05` 列为主归属（BOUNDARY 记为策划主归属 + ECS 次归属）；③ 角色把 `rules/06` §4.7 列为必读（BOUNDARY 只记 §2） | 各 README §2；BOUNDARY §4.1 | 不影响结论，但「主归属」口径出现第二套 | 以本文 §2 矩阵为一页口径；细节差异不改 README（只记录），下一轮基线修订时对齐 | integrator / 各模块 | 交叉核对 |
| P25 | 低 | **`rules/07` 标题写「6 步检查表」，实际列出 ①~⑦**（⑦ 是并行时的「会话外壳」） | 开发 README D1 | 计数困惑 | 保持现状；「六步」指 ①~⑥，「七步」含合并 | 开发 | D1 |
| P26 | 低 | **`decor_catalog.md` 的 `.gitignore` 段与 `rules/08` §10 对「非 git 仓库退出码」的措辞不一致**（脚本说正常不抛，`rules/08` 说以退出码 2 正常退出） | 开发 README D3 | 脚本化判断时可能读错退出码 | 以脚本实现为准，两处措辞对齐一次 | 开发 | D3 |
| P27 | 低 | **台账类**：① 工作区 `data/excel/{characters,combat}.xlsx`、`CharacterContentSet.cs`、`ARCHITECTURE.md` 均有未提交修改（`M`），ADR §22.3 的不一致**不是**未提交改动造成的；② `ARCHITECTURE.md` §16（v1 查看器）与 §24（v2 查看器）并存，§16 是否维护未说明；③ §14 的「历史欠账」散落三处；④ 调参面板参数无落盘通道（试完必须人工回填） | 角色 README Q-C9；开发 README D8；地编 README Q8 + Q6 | 交接与排期时会漏 | 台账；建议 §16 标明是否废弃、面板加一行「当前值可粘走」 | 各模块 | Q-C9 · D8 · Q8 · Q6 |

### 5.3 尚未定论的协调项

| 编号 | 事项 | 现判 | 谁定 |
|---|---|---|---|
| ~~C1~~ | 模块目录名 | **已裁决**：用中文目录 `策划/地编/角色/开发/ECS架构师/`，BOUNDARY §2.1 的英文 id 作废（英文 id 只用于本文标注检索） | captain（已定） |
| C2 | `AGENTS.md`（仓库根）是否列为第 0 号必读 | 五份 README 已全部把它写进必读，实际已生效 | captain（已事实生效） |
| C3 | `rules/03` / `rules/06` 是否升级为跨模块必读 | 维持「不升级」：两份各 500+ 行，只服务实现者；策划/地编只取结论 | captain（维持 BOUNDARY 现判） |
| C4 | `ARCHITECTURE.md` 托管给 ECS 架构师 | 托管 ≠ 独占：各模块读自己那几节；正文按 `rules/07` 就地覆盖各自章节 | captain（维持现判） |
| C5 | `rules/04` 主归属给策划还是开发 | 策划为主（`rules/04` 是「数值怎么写」的说明书），开发为次（§9 校验 / §10 GS005） | captain（维持现判） |
| C6 | `decor_contact_sheet.png.import` 算不算文档 | 不算（Godot 引擎元数据），不单独写节 | captain（维持现判） |
| C7 | README 写行号还是只写章节号 | **已裁决**：只写章节号（行号会漂）；各 README 里已有的「快照行号」保留不动 | captain（已定） |

---

## 6. 本次归纳的产物与纪律

| 产物 | 路径 | 说明 |
|---|---|---|
| 总索引（本文） | `docs/MODULE_MAP.md` | 一页读完：矩阵 / 入口卡 / 路由 / 待核对 |
| 模块目录说明 | [`modules/README.md`](modules/README.md) | `docs/modules/` 下有哪些文件、怎么用 |
| 基线 | [`modules/_shared/BOUNDARY.md`](modules/_shared/BOUNDARY.md) | 17 份清点 + 边界定义 + 归属总表 + 逐节切分（v1） |
| 五份模块索引 | [`策划`](modules/策划/README.md) · [`地编`](modules/地编/README.md) · [`角色`](modules/角色/README.md) · [`开发`](modules/开发/README.md) · [`ECS架构师`](modules/ECS架构师/README.md) | 各 270~391 行，结构见 BOUNDARY §2.3 的六段模板 |

**只增不改的落实情况**：
1. `docs/` 下**17 份原有文档零改动**（本次任务与 t1~t6 全程未编辑它们）。
2. 新增内容只落在 `docs/modules/**` 与本文。
3. 发现的错（§5 共 27 条）**只记录不修改**；其中 P9 明确指出 `decor_catalog.md` 是自动生成物，必须改生成器而非手改。
4. 不复制原文契约：本文只给章节号、一句话与相对路径；精确签名/阈值/表结构一律指向原文（`ARCHITECTURE.md` 是唯一架构真源，ECS 形状以 `ECS.md` 为准）。

---

## 7. 变更日志（本文自身）

| 日期 | 版本 | 任务 | 变更摘要 |
|---|---|---|---|
| 2026-11-11 | v1 | `t-map` 整合 | 建立总索引：§1 新人 30 分钟路线、§2 文档×模块矩阵（17 行 × 5 模块 + 主归属计数）、§3 五张模块入口卡 + 分工口径一句话、§4 任务路由表（20 行）、§5 待核对汇总（**34 条去重合并为 27 条**，P1~P3 置顶，含 captain 裁决的 C1~C7）、§6 产物与纪律。执行 captain 裁决：中文目录名、`rules/NN` 文件名口径、只写章节号 |
