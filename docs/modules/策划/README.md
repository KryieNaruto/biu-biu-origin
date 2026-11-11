# 策划模块（planning）· README

> 版本: v1 ｜ 建立日期: 2026-11-11 ｜ 产出任务: `t-plan`（策划模块索引）
> 性质: **索引文档（只增不改）**。本文件只描述「策划要读哪一节 / 哪张表归策划管 / 改表走哪条路」，
> **不复制任何原文契约**——凡是需要精确字段表、阈值、报错口径的地方，一律写「原文见 …」，避免两处真源。
> 基线: [`docs/modules/_shared/BOUNDARY.md`](../_shared/BOUNDARY.md)（五大模块边界与文档归属总表）。
> 与基线冲突时以基线为准；本文件自己发现的问题统一写在 §8「待核对」，不改基线、不改原有 17 份文档。

---

## 0. 三十秒速读

| 你问 | 答案 |
|---|---|
| 策划管什么 | `data/excel/*.xlsx` 里的**每一行 / 每一格**填什么（数值、内容、名字），以及改完表跑哪条校验 |
| 策划不管什么 | `*.cs` 一行都不写；`data/generated/` 一个字节不改；房间怎么排 → 地编；代码结构 → ECS |
| 改一个数要动几处 | **1 处：表里那一格**（代码 0 行）。见 §4 三条路径 |
| 加一个角色 / 武器 | **还是只有表**：`characters.xlsx` 或 `combat.xlsx` 加行；代码 0 行。见 §4 |
| 改完怎么算「做完了」 | 校验器打印 `OK: N 项检查通过` + 退出码 0 + 图鉴重跑无缺名警告（§5） |
| 一天只看一页的话看哪页 | 本 README §4 三条路径 + §6 禁令 |

---

## 1. 这个模块负责什么

用 **Excel 决定「玩家看到什么内容、数值是多少、什么时候出现、叫什么名字」**——不写代码。

展开成四条：

1. **数值的写法**：五层数值包（`Base` 绝对值 + `Growth`/`Equipment`/`Buff`/`Temporary` 四层差量）怎么叠成一个最终值；`StatPacks` 表里 `(PackId, Layer, StatId) → Value` 这一行怎么填。
2. **内容的写法**：`combat.xlsx/Objects` 一行 = 一个物件（人物 / 怪物 / 武器 / 子弹 / 特效**同源**），差异只在 `Tags` 标签组合、`StatPacks` 五段引用、`Weapon/Projectile/Actor/Effect` 四个契约引用、`VisualPack` 外观包。
3. **规则值的写法**：武器/子弹的所有数值列是**规则值字符串**（字面量 / `stat:` / `wstat:` / `flags:` / `rand:` / `chain:`），表里写「子弹继承武器的伤害」= 0 行代码。
4. **名字与展示**：`DisplayKey` 只写本地化 key，中文名写在 `l10n.xlsx` 的 `Zh` 列；图鉴 `docs/codex/index.html` 是这些表的**只读可视化**。

**一句话边界**：策划决定「这格填多少」，别人决定「这格是什么、由谁读」。字段有哪些、怎么组合 → 角色 / ECS 模块；房间怎么排 → 地编模块。

---

## 2. 我必读的文档（路径 · 章节 · 一句话理由）

> 行号为 2026-11-11 实测，仅作「大概多长」参考；**章节号才是稳定锚点**（基线 §6 Q7：README 只写章节号）。
> 全部 17 份文档 = 6,400+ 行，策划真正必读的合计约 **1,900 行**（其中 `rules/04` 全文一项占 583 行）。

### 2.1 跨模块必读（所有模块都要读，策划不例外）

| 文档 | 必读章节 | 一句话理由 | 行数 |
|---|---|---|---|
| [`AGENTS.md`](../../../AGENTS.md)（仓库根） | §0 三十秒速读、§2 八条铁规、§3 红线清单（尤其 6、13）、§5 目录契约 | 项目唯一启动入口；「Excel 是唯一真源 / 代码写数值 = 违规 / 不许手改 `data/generated/`」都在这 | ~250 |
| [`docs/architecture/ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | §0 一页速览 + §11 变更日志首屏（最近 5 行） | 开工前先知道「现在是什么状态、最近谁改了什么」 | 3,228（只读 ~100） |
| [`docs/rules/07-definition-of-done.md`](../../rules/07-definition-of-done.md) | §2 六步检查表、§8.2 章节路由表 | 收工动作全模块通用；策划改了影响运行期的内容也要留一行变更日志 | 466 |
| [`docs/rules/08-parallel-sessions.md`](../../rules/08-parallel-sessions.md) | §0 速读、§2 六步流程、§8 反例 | 多会话同时改表时在 `main` 上直接干 = 违规 | 405 |

### 2.2 策划本模块必读（主归属 + 核心条款）

| 文档 | 必读章节 | 一句话理由 | 行数 |
|---|---|---|---|
| [`docs/rules/04-data-driven.md`](../../rules/04-data-driven.md)（**主归属 = 策划**） | **全文**。最关键的：§3 红线清单 / §4 数据分层金字塔 / §5 Excel 规范（§5.1 文件与 Sheet 组织 · §5.2 四行表头 · §5.3 Id 规则 · §5.4 类型词表 · §5.5 数值书写规约）/ §8 三条可视化路径 / §11 热重载 / §12 反例 / §13 自检清单 | 规则 4 就是「策划怎么把数值写进文件」的完整说明书；不读就会往代码里写数值、或把表写成交不出来的形状 | 583 |
| [`docs/rules/05-numeric-package.md`](../../rules/05-numeric-package.md) | §3 层栈（§3.1~§3.3 Fold 加法语义）、§4 赋值矩阵（§4.2）、§5 StatId 枚举与号段（§5.1~§5.3）、§6 修改语义、§7 公式与 Modifier（§7.2 公式由数据驱动 · §7.3 表达式白名单）、§9 反例、§10 自检 | 数值包是「策划表达数值」的语言：同一 `PackId` 只能占一层、Base 是绝对值其余是差量、所有对外读取都是只读视图 | 811 |
| [`docs/rules/06-extensibility.md`](../../rules/06-extensibility.md) | **§2 扩展预算表**（只需这一节，约 30 行） | 「加一个怪物 ≤2 文件 / 加一个数值变体 0 代码」是硬性验收数字；策划提案前先对一遍预算。其余章节是实现者的事 | 509（只读 §2） |
| [`docs/architecture/ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | **§4** 数据流与管线（§4.1.1 证据链 · §4.3 Excel 文件组织 · §4.3.1 一个 `PackId` 只能占一层 · §4.4 校验点 · §4.5 生成物 vs 手改 · §4.6 数据对象分类）、**§5** 数值系统（§5.2 层栈 · §5.4 硬约束）、**§6** 战斗与技能管线（§6.2 八步顺序 · §6.3 扩展点）、**§15** 角色抽象（§15.5 策划怎么改 · §15.6 扩展成本）、**§17** 基础物件抽象（§17.2 数据形状 · §17.3 规则值 `ValueRef` · §17.6 两张表 + 一个校验器 · §17.10 外观包唯一入口）、**§22** 核心内容集与图鉴（§22.6 图鉴 · §22.9 六个坑）、**§23** 本地化 `l10n.xlsx` | 见下表 2.3 逐节理由 | 3,228（只读约 700） |
| [`docs/design/character-hud-contract.md`](../../design/character-hud-contract.md) | **§7 数据表**（`characters.xlsx` 追加哪些 sheet）、**§5 装配清单**（`CharacterAssemblySpec` 的字段结构） | 这张表的**列清单**就是 `characters.xlsx` 的来历；§7 说明「哪张表为什么存在」。注意：§7 写于 2026-11-05，列清单已被后续迁移改写，**以真表与生成器为准**（见 §8 Q3） | 332（只读 ~40） |
| [`tools/data_pipeline/README.md`](../../../tools/data_pipeline/README.md)（**仓库内，不在 17 份里，但策划最常用**） | §1 怎么跑（必须用 DSH 自带 Python）、§2 表结构约定、§3 各 Sheet 逐列说明、§4 规则值语法 + 常见写错、§5 `Recipe` 8 段、§6 StatId 号段、**§7 想改什么 → 改哪张表哪一列**、§8 校验器查什么、§9 常见错误速查、§10 提交前自查清单、§7.3（Tier）/§8.4（六个坑）、§6.2（不要重跑 `gen_combat_xlsx.py`） | 这就是「策划改表手册」本身；日常 90% 的问题在这里能查到。**它是代码仓里的文档，改动归开发，但内容归策划用** | 548 |

### 2.3 `ARCHITECTURE.md` 只读这七节（逐节理由）

| 节 | 内容 | 策划为什么读 |
|---|---|---|
| §4 | 数据流与管线 | 「Excel → 校验 → 生成物 → 运行时」的链路。**§4.3.1**：同一个 `PackId` 不能分两层写（运行期以 `PackId` 为唯一键）；**§4.5**：`data/excel/*.xlsx` ✅ 可手改、`data/generated/**` ❌ 禁止手改；**§4.6**：平衡数值 / 表现参数 / 资源引用 三类数据的编辑入口不同 |
| §5 | 数值系统 | §5.2 层栈与「夺舍时哪层被替换」；**§5.4 硬约束**：StatPackage 是值语义、数值公式只有 `StatCalculator` / `DamageResolver` 两个入口、公式本身来自 `Formula` Sheet、StatId 只加不改 |
| §6 | 战斗与技能管线 | §6.2 一帧八步（意图 → 命中 → 伤害上下文 → 修正链 → 结算 → 事件 → 反馈 → 清理）；**§6.3 扩展点表**里「伤害上下文字段化 = Excel 加列 = 0 代码文件」「修正链顺序由数据 `order` 决定，不靠代码注册顺序」是策划提案的依据 |
| §15 | 角色抽象 | §15.3 五层折叠公式；**§15.5 策划怎么改**（`Characters` 加一行 = 加一个角色）；§15.6 扩展成本表（加角色 1 文件 = 那张表）；§15.7 约束（加角色不得改 `ActorAssemblySystem` / `SkillCastSystem`） |
| §17 | 基础物件抽象 | **§17.2** 一个 `ObjectArchetype` + 四个引用 + 两个位矩阵；**§17.3** 规则值 10 种来源；**§17.6** `combat.xlsx` 六张表的逐表说明 + 校验器 4553 项检查清单；**§17.10** 外观只走 `VisualPack` 一条路、`vp_none` 与「暂时没模型」的区别 |
| §22 | 核心内容集与图鉴 | 5 人物 / 5 怪物 / 5 武器 / 5 子弹「机制各异不是数值变体」的设计口径；§22.7 幂等写入脚本；**§22.9 六个坑**（`_upsert` 主键、`PackId` 跨层、规则值空 ≠ 0、`VisualSlot` 必填、改表要同步 C# 镜像、图鉴要最终数字） |
| §23 | 本地化 | **§23.9 一句话规矩**：加任何新内容时 `l10n.xlsx` 必须同步加一行中文名；§23.3 表结构；§23.5 五类检查；§23.6 `DisplayKey` 规范 `actor.<Id>` |

### 2.4 读一部分就够的（次归属）

| 文档 | 章节 | 为什么策划要读这一节 |
|---|---|---|
| [`docs/rules/02-tech-selection.md`](../../rules/02-tech-selection.md) | §1 T1~T10 矩阵、§7 数据流谁用什么语言 | 判断「这个需求能不能纯数据做、还是必须落代码」；选语言不是策划的事，但「能不能不写代码」是策划的事 |
| [`docs/rules/03-abstract-first.md`](../../rules/03-abstract-first.md) | **§3.4 契约完备四问**（平行契约 / 漏配 / 死数据列 / 枚举无实现） | 策划提案时最常踩的是「**死数据列**」：加一列却没人读 = 违规。读完四问即可，不必读全文 535 行 |
| [`docs/architecture/ECS.md`](../../architecture/ECS.md) | §4.1 D1（组件里不放 `Def` 引用，只存 id） | 它决定「表里加一列会不会影响运行时」——策划改表前的第一道闸 |
| [`docs/artifacts/decor_catalog.md`](../../artifacts/decor_catalog.md) | §2 收录统计、§6 跳过了什么 | 只在「要不要新增一种装饰种类」时看；收录与尺寸口径归地编 |
| [`docs/artifacts/decor_contact_sheet.png`](../../artifacts/decor_contact_sheet.png) | 整图 | 挑装饰 / 判断遮挡时看 |
| [`docs/codex/index.html`](../../codex/index.html) | 整页（**只读，生成物**） | 改完表重跑 `gen_codex.py` 后对着看：人物 7 / 怪物 5 / 人物武器 5 / 子弹 12 = 60 张卡片。**不要手改这个 html**（见 §6） |
| [`docs/prompts/task-prompt-template.md`](../../prompts/task-prompt-template.md) | §3 开工必读清单、附录 A 红线 | 让 AI 帮自己改生成器 / 写提案时，用这个模板交任务 |
| [`docs/rules/01-gdscript-ban.md`](../../rules/01-gdscript-ban.md) | §2「UI 层」的唯一定义 | 只在「文案 / 名字要不要进 GDScript」这种问题上需要；一般不用读 |

---

## 3. 策划拥有、且唯一可改的数据表

### 3.1 三张真表（`data/excel/*.xlsx`）

> 一切以**真表**为准。下表「Sheet · 列数 · 数据行」为 2026-11-11 用 DSH 自带 Python + openpyxl **只读**核对的结果
> （四行表头 + 第 5 行 `#data` 标记 + 第 6 行起数据），随时可能因内容增长而变化。

| 文件（`data/excel/`） | Sheet | 列数 | 数据行（约） | `_Meta.SchemaVersion` | 一句话 | 业主 |
|---|---|---|---|---|---|---|
| [`combat.xlsx`](../../../data/excel/combat.xlsx) | `Tags` | 8 | 62 | 2 | 标签字典：`TagId / TagIndex / Tier / Family / Category / Group / BitMask / Note` | 策划 |
| | `Objects` | 15 | 26 | | ★**唯一的物体原型表**：人物 / 怪物 / 武器 / 子弹 / 特效全这一张 | 策划 |
| | `Weapons` | 28 | 8 | | 武器契约（13 个规则值列 + 4 个引用列 + 6 个枚举/整型列） | 策划 |
| | `Projectiles` | 20 | 7 | | 子弹契约（8 个规则值列 + 4 个 `OnHit*/OnExpire*` 引用列） | 策划 |
| | `StatPacks` | 4 | 104 | | 武器 / 子弹 / 怪物的五层数值包（与 `characters.xlsx` 同构） | 策划 |
| | `VisualPacks` | 6 | 20 | | 外观包：`ModelPath / Scale / TintHex / AnimClips`（当前全部白模） | 策划 / 角色（口径） |
| | `_Meta` | 2 | — | | `SchemaVersion / Owner / Source`（**不要改**） | 策划 |
| [`characters.xlsx`](../../../data/excel/characters.xlsx) | `Characters` | 12 | 9 | 6 | 角色 / 怪物本体（`Kind / DefaultTeam / Control / BasePackage / SkillLoadout / HitRadius…`） | 策划 |
| | `StatPacks` | 4 | 156 | | 角色五层数值包 | 策划 |
| | `SkillLoads` | 9 | 34 | | 技能包：五槽 `Dash / PrimaryAttack / WeaponSwap / Signature / Interact` + 冷却 + 法力 | 策划 |
| | `Assemblies` | 3 | 1 | | 变体共用的模块集合（`Stat,Skill,Weapon,Aim,Move,Lifecycle,PlayerInput`） | 角色 |
| | `VariantAssembly` | 13 | 4 | | ★**运行时真正装配的角色变体表**（`test_hero` 及三个变体） | 角色 + 策划 |
| | `VisualPacks` | 6 | 4 | | 角色外观包（与 `combat.xlsx` 同名同构） | 策划 / 角色 |
| | `CameraPacks` | 7 | 3 | | 镜头包（`Distance / Height / FollowRatePerSecond / FovDegrees / LookAhead*`） | 地编（手感口径）+ 策划（数值） |
| | `InputPacks` | 4 | 3 | | 手柄包（`LookSensitivity / StickDeadZone / AimAssistStrength`） | 策划 |
| | `Weapons` | 11 | 5 | | **角色视角的武器行**（`Damage / BulletSpeed / RangeMeters / CriticalChance…`） | 策划 |
| | `_Meta` | 2 | — | | **不要改** | 策划 |
| [`l10n.xlsx`](../../../data/excel/l10n.xlsx) | `L10n` | 3 | 168 | 1 | 中文名唯一真源：`Key / Zh / Note` | 策划 |
| | `_Meta` | 2 | — | | **不要改** | 策划 |

### 3.2 「谁可以改」逐格判定

| 位置 | 策划能不能改 | 备注 |
|---|---|---|
| 第 1~4 行（表头：字段名 / 类型 / 中文名 / 备注） | ❌ | 表头由生成器 `COLUMNS` 定死；校验器会报「缺少列 / 多出未登记的列」。要加列 → 走开发评审（生成器 + C# 读取 + 校验器三处同步） |
| 第 5 行 `#data` 标记行 | ❌ | A 列 `#data`、其余列必须空 |
| 第 6 行起任何**数据格** | ✅ | 这就是策划的全部工作区 |
| Sheet 名、Sheet 顺序、列顺序 | ❌ | Sheet 名 = 表名，校验器按名字定位 |
| 新增一行（新物件 / 新数值 / 新武器 / 新名字） | ✅ | §4 三条路径 |
| `_Meta.SchemaVersion` | ❌ | 改了就是换表结构，要同时改生成器 / 校验器 / C# |
| 合并单元格、公式单元格 | ❌ | 规则 4 §5.1 明确禁止 |

### 3.3 不是策划的表（但策划会读）

| 文件 | 业主 | 策划只读的理由 |
|---|---|---|
| `data/generated/**` | 工具链生成 | **当前仓库里这个目录还不存在**（见 §8 Q1）。存在时也一律只读 |
| `config/architecture-rules.json` | 开发 | 架构守卫白名单 |
| `config/decor_catalog.json` | 地编 | 房间可出哪些装饰 |
| `scripts/core/StatId.cs` | ECS | `StatPacks.StatId` 能填哪些值由这个枚举定；加属性要同时改它 |
| `scripts/data/CharacterContentSet.cs` | 角色 | 角色内容的**运行时镜像**，改表后要让它跟上（见 §8 Q2） |
| `docs/codex/index.html` | 生成器 | 图鉴，**只读**；重跑 `gen_codex.py` 生成 |

### 3.4 关键列速查（填之前先看这几列）

> 只给「最容易填错 / 最不直观」的列。逐列全表见 [`tools/data_pipeline/README.md`](../../../tools/data_pipeline/README.md) §3。

**`combat.xlsx/Objects`**

| 列 | 要点 |
|---|---|
| `Tags` | 逗号分隔的 `TagId`，必须都在 `Tags` 表里；**最多命中一个 `ObjectKind` 标签**（`tag.actor` / `tag.weapon` / `tag.projectile`），都命不中 = 纯表现物件 |
| `Cluster` / `ClusterWeight` | 同簇物件会被熔炉互相替换 → **同簇能力位必须一致**（别把子弹和枪配进一个簇）；权重必填 > 0；单候选簇只给 `[提醒]` |
| `StatPacks` | **五段用 `;` 分隔**：`Base;Growth;Equipment;Buff;Temporary`，空段 = 该层无包；带 `tag.actor` 的物件 `Base` 段必填 |
| `Weapon` / `Projectile` / `Actor` / `Effect` | 四个契约引用；`Projectile` 指向的物件必须被某行 `Projectiles` 引用；四者构成图**不许成环** |
| `VisualPack` | ★**必填**；"有意不要模型"（特效 / 纯逻辑物件）写 `vp_none`，**不要留空** |

**`combat.xlsx/Weapons` 与 `Projectiles`**

| 列 | 要点 |
|---|---|
| 13 个 / 8 个**规则值**列 | 可以写字面量，也可以写 `wstat:WeaponDamage(5)` 引用武器属性；**空 ≠ 0**，「没有效果」要显式写 `0` |
| `Delivery` | 目前只有 `Projectile` 与 `SpawnActor` 真的生效；`Instant/Beam/Melee/Aura/Throw` **填了也不生效**（§17.7 明确列为未接） |
| `ProjectileRef` / `ActorRef` | `Delivery=Projectile` 时 `ProjectileRef` 必填 |

**`characters.xlsx/SkillLoads`**

| 列 | 要点 |
|---|---|
| `Slot` | 五固定槽 `Dash / PrimaryAttack / WeaponSwap / Signature / Interact` —— 一个技能包每槽最多一行 |
| `CooldownSeconds` | 左键射击行**必须 == 该武器行 `Weapons.ShotCooldown`**（单一真源是武器行） |
| `WeaponId` / `ProjectileId` | 非射击技能留空；两者**不得填成一样** |

**`*/StatPacks`（两张同构）**

| 列 | 要点 |
|---|---|
| `Layer` | `Base` 是绝对值，其余四层是差量；**同一个 `PackId` 只能占一层**（§4.3.1） |
| `StatId` | 必须在 `StatId` 白名单里；**只加不改**、留号段 |
| `Value` | 倍率型（以 `Mul` 结尾）默认 1.0 |

**`l10n.xlsx/L10n`**

| 列 | 要点 |
|---|---|
| `Key` | `actor.*` / `item.*` / `proj.*` / `fx.*`（= `DisplayKey`）、`tag.*`（= `TagId`）、或裸枚举名 |
| `Zh` | 中文名；**不许留空、不许直接抄英文 id** |

---

## 4. 数据驱动红线（规则 4）与三条典型任务的逐步路径

### 4.1 红线（违反即打回）

> 原文见 [`docs/rules/04-data-driven.md`](../../rules/04-data-driven.md) §3 与 [`AGENTS.md`](../../../AGENTS.md) §3 红线 1~15。
> 下面是**策划视角的复述**，不是原文，判断以原文为准。

| # | 红线 | 为什么策划要关心 |
|---|---|---|
| R1 | **代码里出现游戏数值 = 违规**（伤害 / 血量 / 速度 / 射速 / 冷却 / 掉落率 / 尺寸 / 颜色 / 持续时间 / 特效参数 / 相机手感……） | 「这个数能不能不写代码就改」是策划的提案判据；要求开发写死数值 = 主动制造违规 |
| R2 | **Excel 是唯一真源**；运行期代码**不读 `.xlsx`**，只读生成物 | 策划的表是权威，不是「参考」 |
| R3 | **不许手改 `data/generated/`** | 手改必被覆盖，且会让人误以为改表没生效 |
| R4 | **同一 `PackId` 不能跨层** | 跨层会直接抛「数值包 id 重复」；按层拆名（`hero_base` / `hero_growth`） |
| R5 | **规则值列空 ≠ 0** | 空着会被校验器拦下；「不生效」要显式写 `0` |
| R6 | **不许合并单元格、不许公式单元格** | 解析器读缓存值会与显示不一致 |
| R7 | **不许新增 / 改名 / 删 Sheet，不许插删列** | 列集合被校验器定死 |
| R8 | **同义 Sheet 不许并存、文件命名用小写复数主题名** | `Weapons` 与 `Weapon` 并存 = 断链 |
| R9 | **加一列却没人读 = 死数据列违规**（规则 4 / 规则 5 §3.4-C） | 策划最常提的「加个字段」必须先回答「谁读它」 |
| R10 | **`DisplayKey` 不写中文**，中文写 `l10n.xlsx` | 文案也是数据；写进代码常量同样违规 |
| R11 | **新增内容必须同步加中文名** | 忘了不会崩，但校验器报「缺少中文名：xxx（被 Yyy 引用）」 |
| R12 | **`_Meta.SchemaVersion` 不许手改** | 改了 = 换了表结构 |

### 4.2 路径 A：改一个数值（最常见，5 步，约 2 分钟）

> 例：把 `pistol_101` 的伤害从 10 调到 12。

```text
① 定位
   先问「这个数在链上哪一层」：
   · Weapons.BaseDamage 写的是 wstat:WeaponDamage(10) → 实际值在 combat.xlsx/StatPacks 的 wpn_pistol_101(WeaponDamage)
   · 子弹自己的伤害在 Projectiles.Damage 或 bullet_*_base 的 StatPacks
   · 角色属性在 characters.xlsx/StatPacks 的 <pack>_base
② 改表
   打开 data/excel/combat.xlsx → StatPacks Sheet → 找到 PackId=wpn_pistol_101, StatId=WeaponDamage 那一行 → 把 Value 改成 12
   （改的是数据格；表头、_Meta、Sheet 名一律不碰）
③ 校验（必跑）
   & $py -B tools\data_pipeline\validate_content_xlsx.py     # $py = DSH 自带 python.exe
   期望：最后一行 OK: N 项检查通过，退出码 0
④ 跑回归（会真读表）
   dotnet test    # ContentBinderTests / CharacterXlsxParityTests 直接解析真 xlsx，改表/改镜像只改一边必红
⑤ 看图鉴
   & $py -B tools\data_pipeline\gen_codex.py
   打开 docs/codex/index.html 确认数字变了；输出里不应有「缺名」警告
```

**注意**：如果这个数值同时被 `CharacterContentSet.cs`（角色内容的运行时镜像）复刻了，**只改表会让回归变红**——见 §8 Q2，这时要同步一行镜像并保留 `// arch-ok: 镜像 Weapons 表` 注释。

### 4.3 路径 B：加一个角色（怪物同源，6 步，代码 0 行）

> 依据：`ARCHITECTURE.md` §15.5 / §15.6（加一个角色 = 1 个文件），`rules/06` §2 预算，`characters.xlsx` 真表的 10 张 Sheet。

```text
① 定「它是什么」
   Characters 加一行：Id / DisplayKey(actor.<Id>) / Kind / DefaultTeam / Control / BasePackage / SkillLoadout / HitRadius / HitHeight / VisualSlot
   · 怪物与人物同一行结构，差别只在 Control=Ai 与 DefaultTeam=Enemy
   · 夺舍 = 运行时改 ControlOverride，不是表里多一行
② 数值包（StatPacks）
   StatPacks 加 <id>_base 的行（Layer=Base，一行一个 StatId）；成长另起 <id>_growth（Layer=Growth）
   ★ 同一个 PackId 不许跨层
③ 技能包（SkillLoads）
   SkillLoads 加 <id>_kit：主槽 PrimaryAttack 一行；怪物只填该槽，人物五槽
   射击类：WeaponId 指向武器行，CooldownSeconds == 该武器行 ShotCooldown
④ 外观包（VisualPacks）
   加 <id>_vis：ModelPath 留空 = 白模；Scale 必填 > 0；TintHex/AnimClips 空着（空模型不许填，校验器报死字段）
⑤ 让它能进游戏（二选一）
   · 走 VariantAssembly（当前实际装配路径）：加一行 Id / AssemblyId=hero / DisplayName / VariantTag / BasePackage / SkillLoadout / Weapon / VisualPack / CameraPack / InputPack
   · 走战斗内容（combat.xlsx/Objects）：加一行 Objects（Tags 含 tag.actor，StatPacks 五段 Base 段填②的包），让刷怪/熔炉能选到它
⑥ 收尾（三件必做）
   ① l10n.xlsx 加中文名：actor.<Id> → 「中文名」
   ② 跑 validate_content_xlsx.py（OK + 退出码 0）+ gen_codex.py（无缺名警告）+ dotnet test
   ③ 角色模块/开发那里同步运行时镜像（CharacterContentSet）与文档——策划负责提需求，不自己写 .cs
```

**扩展预算（硬数字，`rules/06` §2）**：一个新的数值变体 = **0 代码**；一个新怪物 = **≤2 文件**（理想 0 代码 + 1 表行）。超出预算就先回头问「是抽象缺了，还是数据表达不了」。

### 4.4 路径 C：加一把武器（6 步，代码 0 行）

> 依据：`ARCHITECTURE.md` §17.1 / §17.6、`tools/data_pipeline/README.md` §7「新增一把武器」。

```text
① Objects 加一行
   Tags 必须含 tag.weapon + 一个 WeaponKind 标签（tag.pistol / tag.rifle / tag.bow / tag.melee…）
   StatPacks 五段：Base 段填 ②的包名（例 mygun_base;;;;）
   VisualPack：★必填，填 ④的外观包；Cluster/ClusterWeight 可选（要进熔炉才填，且同簇能力位必须一致）
② StatPacks 加数值行
   combat.xlsx/StatPacks：PackId=mygun_base, Layer=Base, StatId=WeaponDamage/WeaponFireRate/WeaponProjectileSpeed…, Value=…
   ★ 改数值优先改这里，不要把数字直接写进规则值列
③ Weapons 加一行
   ObjectId = ①的 Objects.Id
   Trigger(Auto/Semi/…) / Delivery(当前只 Projectile、SpawnActor 生效) / Aim / DamageType / TeamLayer
   13 个规则值列：写 wstat:WeaponDamage(10) 这类继承写法，或直接字面量；空 ≠ 0
   ProjectileRef = 要发射的子弹 Objects.Id（Delivery=Projectile 必填）
   InheritWeaponStats / ChainDepthLimit(≥1) / ChainShrink / Recipe(8 段用 | 分隔，空 = 不进熔炉)
④ 外观包（若①里的包没建）
   VisualPacks 加一行：Id / ModelPath(可空=白模) / Scale(>0) / TintHex / AnimClips / Note
⑤ 接到一个人物上（想让玩家能拿到）
   characters.xlsx/SkillLoads 的射击槽 WeaponId 指向 ③的 Weapons.Id
   characters.xlsx/VariantAssembly 的 Weapon 指向 characters.xlsx/Weapons 里的武器行
   ★ characters.xlsx 与 combat.xlsx 各有一张 Weapons，语义不同（角色视角 vs 战斗契约），两边都要看
⑥ 收尾
   l10n.xlsx 加 item.<Id> / proj.<Id> 中文名 → validate → gen_codex → dotnet test
```

**四种「组合玩法」全是 0 代码**（§17.1）：武器射子弹 / 武器射怪物（`Delivery=SpawnActor` + `ActorRef`）/ 子弹炸开再射子弹（`OnExpire=SpawnProjectile` + `OnExpireProjectile`）/ 子弹继承武器属性（`InheritWeaponStats=TRUE` + `wstat:`）。

### 4.5 通用纪律：**不许**做的三件事

| ❌ 不许 | ✅ 应该 |
|---|---|
| 手改 `data/generated/**` | 改 `data/excel/*.xlsx` 再跑生成 |
| 在 `.cs` 里写死数值（哪怕「只临时改一下」） | 改表；确实需要临时值时加 `// arch-ok: <理由>` 并走评审 |
| 直接改 `docs/codex/index.html` / 往 `gen_codex.py` 里写中文名常量 | 改 `l10n.xlsx` 后重跑 `gen_codex.py` |

---

## 5. 验收口径（「改表算做完了」的判据）

> 顺序即执行顺序；**一条不绿就不算做完**。命令里的 `$py` = DSH 自带 Python（见 `AGENTS.md` §6）。

| # | 检查 | 命令 / 动作 | 期望 |
|---|---|---|---|
| 1 | 表结构 + 类型 + 外键 + 能力位 + 规则值 + 唯一性 | `& $py -B tools\data_pipeline\validate_content_xlsx.py` | 最后一行 `OK: N 项检查通过`，退出码 **0**；`[错误]` 一条都不许有 |
| 2 | 单候选簇 / 白模等待办 | 同上 | `[提醒]` **允许存在**（不中断构建），但每一条都要能解释「我知道它为什么在」 |
| 3 | 表 ↔ 运行时一致 | `dotnet test` | 全绿；`ContentBinderTests`（combat 真表）、`CharacterXlsxParityTests`（characters 真表逐字段比对）必须过 |
| 4 | 图鉴可读 | `& $py -B tools\data_pipeline\gen_codex.py` | `0 条缺名警告`；打开 `docs/codex/index.html` 能看到新卡片、中文名正确 |
| 5 | 改动面 | `git diff --stat` | 只看得到 `data/excel/*.xlsx`（+ `l10n.xlsx`）；没有 `data/generated/**`、没有 `.cs` |
| 6 | 留痕 | 在 `ARCHITECTURE.md` 变更日志追加一行 / 由开发代劳 | 规则 3（`rules/07` §2）要求所有影响运行期的改动留一行 |

**当前基线（2026-11-11 只读实测）**：校验器 `OK: 4531 项检查通过`、退出码 0，其中含 **13 条 `[提醒]`**（11 条「有能力的物件外观包 `ModelPath` 为空 → 回退白模」+ 2 条「单候选簇」）。
`docs/codex/index.html` 当前为：人物 7 / 怪物 5 / 人物武器 5 / 子弹 12。

---

## 6. 禁令（策划版红线速查）

```text
❌ 修改 data/generated/** 下任何文件（改表后重新生成）
❌ 在 *.cs / *.gd 里写死游戏数值（伤害/血量/速度/射速/冷却/颜色/尺寸/持续时间/特效参数/相机参数…）
❌ 在 gen_codex.py / 任何脚本里写中文名常量（名字的唯一真源是 l10n.xlsx）
❌ 手改 docs/codex/index.html（生成物，重跑 gen_codex.py）
❌ 重跑 gen_combat_xlsx.py / gen_characters_xlsx.py 去"更新"内容
   （它们是"第一次建表头"用的种子，整体覆盖会冲掉手改与 BitMask，见 tools/data_pipeline/README.md §6.2）
❌ 改表头 / 加列 / 改 Sheet 名 / 改 _Meta.SchemaVersion（走开发评审）
❌ 给同一 PackId 写两个 Layer；规则值列留空当 0；合并单元格；公式单元格
❌ 把 素材/ 下的路径填进 VisualPacks.ModelPath（素材库是美术原料区，不是运行时资源区）
❌ 新增内容不加 l10n.xlsx（忘了不崩，但一定报缺名）
```

**唯一放行的例外**：结构性常量（`0` / `1` / `-1` 之类的「无 / 空 / 关闭」语义）可以写进代码，但必须同一行或上一行写 `// arch-ok: <理由>`——**这是开发的事，不是策划批准的理由**。

---

## 7. 常见任务 → 改哪里（路由表）

| 我想…… | 改哪里 | 详见 |
|---|---|---|
| 改手枪伤害 | `combat.xlsx/StatPacks` → `wpn_pistol_101.WeaponDamage` | §4.2 |
| 改武器射速手感 | `Weapons.FireInterval` 或 `StatPacks.WeaponFireRate` | 路径 A |
| 改子弹飞行速度 / 打几下消失 | `Projectiles.Speed` / `Projectiles.Pierce` + `Lifetime` | 路径 A |
| 改子弹命中后 / 消失后变成什么 | `Projectiles.HitPolicy` + `OnHit*` / `OnExpire` + `OnExpire*` | 路径 C 末段 |
| 让某把武器打另一种子弹 | `Weapons.ProjectileRef` | 路径 C |
| 让子弹拥有武器属性 | `Objects` 里子弹行的 `Weapon` 列 | 路径 C |
| 加一把武器 / 一种子弹 | §4.4 | 路径 C |
| 加一个怪物 / 一个角色变体 | §4.3 | 路径 B |
| 改角色的血 / 蓝 / 护甲 / 移速 | `characters.xlsx/StatPacks` → `<pack>_base` 的对应 `StatId` | 路径 A |
| 改五槽技能（冷却 / 法力 / 用哪把枪） | `characters.xlsx/SkillLoads` | §3.4 |
| 改中文名 | `l10n.xlsx/Zh`，然后重跑 `gen_codex.py` | §3.4 |
| 加一个标签 | `combat.xlsx/Tags` 末尾加行（`TagIndex`=最大+1，`Family` 填轴，`BitMask` 用没占过的位或 `-1`） | `tools/data_pipeline/README.md` §3.1 |
| 调配簇（某种子弹出现频率） | `Objects.ClusterWeight` | §3.4 |
| 换模型外观 | `VisualPacks.ModelPath`（一格），同时清 `TintHex`/`AnimClips` 或按需填 | `ARCHITECTURE.md` §17.10.1 |
| 加一种新属性（`StatId`） | ❌ 不是纯表任务：要同时改 `scripts/core/StatId.cs` + 生成器 + 校验器 —— 走开发 | `tools/data_pipeline/README.md` §8.4 |
| 加一张新表 | ❌ 走 §4.3「唯一合法路径」评审 | `ARCHITECTURE.md` §4.3 |

---

## 8. 待核对（我没把握 / 与原文对不上的点，交给整合阶段裁决）

> 按基线 §5 纪律：**发现问题不改原文，写在这里**。以下每条都附了我的证据与建议，不含猜测性结论。

| # | 问题 | 证据 | 建议 |
|---|---|---|---|
| Q1 | **`data/generated/` 目前不存在**，而 `rules/04` §5.1/§7.1、`ARCHITECTURE.md` §4.2 大量描述「生成物 .tres / index.json」 | `ls data/` 只有 `excel/`；`ARCHITECTURE.md` §4.1 自己也写「仍缺：`data/generated/` 及 xlsx → 生成物的转换器」 | 策划 README 与任务里「不许手改 `data/generated`」的禁令**写的是将来规则**；当前事实是「运行期直接读 `CharacterContentSet` 内置副本 + xlsx 只被校验器与测试读」。请整合阶段确认是否要在 `MODULE_MAP.md` 里标注这条现状差 |
| Q2 | **改表之后「运行时是否真的变了」取决于 C# 镜像**：`CharacterContentSet.BuildBuiltinV1()` 里硬编码了一份角色内容（含 5 把武器数值），与 `characters.xlsx` 逐项比对（`CharacterXlsxParityTests`） | `scripts/data/CharacterContentSet.cs` 里 `new CharacterWeaponSpec("pistol_basic", 2001, 20f, …)`，注释写明「镜像 Weapons 表」+ `arch-ok`；`ARCHITECTURE.md` §15.5 也写「`CharacterContentSet` 是逐项一致的兜底副本」 | 策划改 `characters.xlsx` 数值时**必须同时提一句「要同步 C# 镜像」**给角色/开发，否则回归必红。这是「代码 0 行」口号与当前实现之间唯一的落差，建议在 `MODULE_MAP.md` 的单页总览里点明 |
| Q3 | `design/character-hud-contract.md` §7 的 `characters.xlsx` sheet 清单已过时：它写「追加 4 个 sheet 段」并列举 `VisualPacks`= `hero_default/hero_tank/…`、`CameraPacks`=`topdown_standard/…`、`InputPacks`=`kbm_standard/…` | 真表（只读核对）现有 10 张 Sheet，`VisualPacks` 实际为 `lit_test_hero*`、`CameraPacks` 为 `follow_std/heavy/fast`、`InputPacks` 为 `pad_std/heavy/fast`；另有 §7 未提的 `Assemblies` / `VariantAssembly` / `Weapons` | 策划读 §7 时**只用来理解「为什么有这几张表」**，列清单以真表 + `validate_content_xlsx.py` 的 `CHARACTERS_COLUMNS` 为准。建议整合阶段在 `MODULE_MAP.md` 标注该节「列清单已过时」 |
| Q4 | `tools/data_pipeline/README.md` 与 `ARCHITECTURE.md` 的检查项数**互相漂移**：前者没写总数，`ARCHITECTURE.md` §17.6 写 4553 项、§22.8 写 4569 项 | 2026-11-11 实测 `validate_content_xlsx.py` = `OK: 4531 项检查通过` | 检查项数是内容增长的函数，任何一处写死的数字都会漂。建议各模块 README **只写「退出码 0 + 最后一行 OK」**，不写具体数字（本 README §5 已按此处理，数字只作当日快照引用） |
| Q5 | `tools/data_pipeline/README.md` §3.6 写 `combat.xlsx` 的 `SchemaVersion(=1)`，真表 `_Meta` 是 `2`；`validate_content_xlsx.py` 的 `SCHEMA_VERSIONS` 也是 `combat.xlsx: 2` | 只读核对：`combat.xlsx/_Meta` → `SchemaVersion=2, Updated=2026-11-09` | 该 README 属开发维护，建议开发侧就地更新（策划不越界改） |
| Q6 | `tools/data_pipeline/README.md` §3.1 写「现有 40 个 tag，其中 16 个占 BitMask（bit 0~15）」 | 真表 `Tags` 数据行 ≈ 62 行 | 同为开发维护的过期数字，建议一并更新 |
| Q7 | `docs/codex/index.html` 是**生成物**（基线 §6 Q6 建议写成只读）——本 README 已在 §2.4 / §6 写明「只读、重跑 `gen_codex.py`」 | `ARCHITECTURE.md` §22.6、§23.4 | 已按建议执行，无需裁决；保留在此供整合阶段核对 |
| Q8 | `characters.xlsx` 有两张语义不同的 `Weapons`：`characters.xlsx/Weapons`（角色视角：`WeaponId` 整数主键 / `Damage` / `ShotCooldown`）与 `combat.xlsx/Weapons`（战斗契约：`ObjectId` 外键 / 13 个规则值列） | 只读核对两表列定义；`SkillLoads.WeaponId` 指向前者，`Objects.Weapon` 指向后者 | 这是真实的双真源风险（改一处、另一处不动 → 玩家拿到的武器与战斗契约不一致）。建议整合阶段在 `MODULE_MAP.md` 显式列出「两把 Weapons」并指定「谁是数值真源」（我的判断：战斗手感以 `combat.xlsx` 为准，角色装配以 `characters.xlsx` 为准） |

---

## 9. 变更日志（本文件自身）

| 日期 | 版本 | 任务 | 变更摘要 |
|---|---|---|---|
| 2026-11-11 | v1 | `t-plan` | 建立策划模块索引：必读清单（含 `ARCHITECTURE.md` 七节逐节理由）、三张真表与 22 张 Sheet 的只读核对结果、关键列速查、12 条数据驱动红线、三条典型任务路径（改数值 / 加角色 / 加武器）、6 项验收口径、禁令速查、任务路由表、8 条待核对 |
