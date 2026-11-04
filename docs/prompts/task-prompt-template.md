# 任务提示词模板（复制给 AI 干活）

> 用途：把本文件**中间的代码块**整段复制，填空后交给 AI agent。
> 版本: v1 ｜ 建立日期: 2026-11-03 ｜ 配套: `AGENTS.md`、`docs/architecture/ARCHITECTURE.md`、`docs/rules/01~07`

---

## 0. 使用方法（30 秒）

1. 打开下面的「模板正文」代码块，整段复制到对话框。
2. 把 `【】` 里的内容替换成你这次的真实需求。**填不出来的项不要删，写「无」或「待定」**——空白项会被 AI 当成没约束，这是最常见的返工原因。
3. 发出后，AI 会按 `AGENTS.md` §1 自动走开工流程。你只需在最后检查它是否真的跑了收工清单。

> 三条使用要诀：
> - **涉及系统**只选 1~2 个。选 3 个以上的，先拆成 3 个任务。
> - **期望数据来源**能填 Excel 表名就一定填。填不了的，说明这任务还没想清数值从哪来。
> - **验收标准**要能"跑一下就知道"。写「手感好」这种词，AI 无法自检。

---

## 1. 模板正文（复制这一段）

```text
【任务目标】
一句话说清要做什么、为谁做。

（示例：给玩家新增一种「散射弹」，一次发射 5 发扇形子弹，每发伤害为基础伤害的 60%。）

【涉及系统】（只选 1~2 个，多选请先拆任务）
数值数据 / 战斗逻辑 / 技能 / 渲染 / 特效 / UI / 工具链

【期望数据来源】
- Excel 表名：____________（填写 data/excel/ 下的文件名，例如 Bullet.xlsx）
- 需要新增/修改的字段：____________
- 若确实是资源而非数值：资源路径 res://____________

【必须新增或复用的抽象】
- 复用的现有接口（从 ARCHITECTURE.md §3 接口清单里挑，见下方附录 B）：
  ____________
- 本次需要新增的抽象（没有就写"无"）：
  ____________ 的接口名 / 职责 / 默认空实现

【扩展性要求】（写出未来 3 个变体，AI 必须证明新增变体不用改核心逻辑）
变体 1：____________
变体 2：____________
变体 3：____________
判据：未来新增上面任意一个变体，改动文件数 ≤ 2，且不改已有的 ISkill / IProjectile 实现类主体。

【验收标准】（四项缺一不可）
1. dotnet build 编译通过，0 error；
2. 场景可跑：____________（写清怎么在编辑器里验证，例如"进入 TestScene 按左键，看到 5 发扇形散开"）；
3. .\tools\check_architecture.ps1 无 error（GS001~GS007）；
4. docs/architecture/ARCHITECTURE.md 已就地覆盖更新，且变更日志追加一行。

【禁止事项】
- 禁止在 UI 层以外新建 .gd 或挂 GDScript；
- 禁止在 C# 逻辑里写死游戏数值（伤害/血量/速度/冷却/颜色/尺寸/持续时间/特效参数）；
- 禁止复制粘贴另一处逻辑来实现，必须先找或抽公共抽象；
- 禁止另建 ARCHITECTURE_v2.md 之类的平行文档，只能就地更新 ARCHITECTURE.md；
- 禁止手改 data/generated/ 下的文件；
- 禁止修改 project.godot（除非任务明确要求）。

【交付时请一并汇报】
- 改了哪些文件（按目录列出）；
- 新增/复用了哪些抽象，为什么；
- 三个未来变体分别怎么加（各一段话即可）；
- 跑了哪些验证命令，输出贴出来；
- 你认为需要我拍板的取舍点。
```

---

## 2. 三个已填好的范例

### 范例 ① 新增一种子弹样式（偏"数据优先"）

```text
【任务目标】
新增一种子弹样式「弹跳弹」：命中后反弹 2 次，每次反弹伤害衰减 20%。

【涉及系统】
战斗逻辑（+ 数值数据）

【期望数据来源】
- Excel 表名：Bullet.xlsx
- 需要新增/修改的字段：新增一行 projectile.bounce，字段为 bounce_count=2、bounce_damage_falloff=0.2
- 资源路径（若需美术表现）：res://shaders/projectile_bounce.gdshader

【必须新增或复用的抽象】
- 复用：IProjectile（生命周期钩子 OnSpawn/OnHit/OnExpire）
- 复用：IStatProvider（读伤害数值，不写死）
- 新增：无（反弹是 IProjectile 的一个实现变体，不需要新接口）

【扩展性要求】
变体 1：穿透弹（命中不消失，穿透 N 个敌人）
变体 2：追踪弹（飞行中朝向最近敌人偏转）
变体 3：分裂弹（命中后生成 3 个子弹）
判据：上述任一变体都应是 IProjectile 的**新实现类** + Bullet.xlsx 新增一行，不改 BulletPool 的池化逻辑、不改弹道解算核心。

【验收标准】
1. dotnet build 0 error；
2. 进入 TestScene 射击墙壁，子弹可见反弹 2 次且伤害逐次衰减（可用调试打印验证）；
3. .\tools\check_architecture.ps1 无 error；
4. ARCHITECTURE.md §6 战斗管线补一句"弹跳弹走 IProjectile 变体"，变更日志加一行。

【禁止事项】（同上标准红线）
...
```

**这个范例体现的原则**：*数据优先*——反弹次数和衰减率都在 Excel 里，代码只读不写死；*先抽象*——先确认 `IProjectile` 已有的钩子够不够用，够用就不新增接口。

---

### 范例 ② 新增一个技能：夺舍（复用怪物技能）

```text
【任务目标】
新增技能「夺舍」：玩家对当前瞄准的怪物施放后，接管该怪物的身体与技能，持续 8 秒，期间玩家的原身体留在原地无法操作。

【涉及系统】
技能（+ 战斗逻辑）

【期望数据来源】
- Excel 表名：Skill.xlsx
- 需要新增/修改的字段：新增一行 skill.takeover，含 duration=8.0、cast_range=6.0、cooldown=25.0
- 目标怪物要暴露"可被夺舍的技能列表"：由 Monster.xlsx 增加 skill_ids 字段引用 Skill.xlsx

【必须新增或复用的抽象】
- 复用：ISkill（夺舍本身就是一个 ISkill 实现）
- 复用：ISkillTarget（按类型解析目标，TryResolve<T> 取 IActor）
- 复用：IActor.ApplyTakeover(TakeoverSpec)（已预留的钩子，直接调用，不要另写接管流程）
- 复用：IMovementDriver（被夺舍后换成怪物的移动解算，体现可替换）
- 复用：IStatProvider（夺舍期间读的是怪物身上的 StatPackage，玩家数值包不动）
- 新增：TakeoverSpec 的字段定义（若 ARCHITECTURE.md 已定义则直接复用，不要另建）

【扩展性要求】
变体 1：夺舍后可主动解除（提前结束）
变体 2：夺舍时强制转移怪物身上已有的 Buff
变体 3：夺舍对精英怪无效（需要条件判定）
判据：三个变体都应通过 TakeoverSpec 加字段 + Skill.xlsx 加列实现，不改 SkillCast 主流程、不复制玩家的移动代码。

【验收标准】
1. dotnet build 0 error；
2. TestScene 里对怪物施放后，摄像机跟随怪物、玩家原身体僵直、8 秒后自动归还且双方状态正确；
3. .\tools\check_architecture.ps1 无 error；
4. ARCHITECTURE.md §6 技能管线补"夺舍走 IActor.ApplyTakeover 钩子"，§3 接口清单标注 TakeoverSpec 状态，变更日志加一行。

【禁止事项】（同上标准红线，额外注意）
- 禁止另起一套"夺舍管理器"绕过 IActor，必须走已有的 ApplyTakeover 钩子；
- 禁止把玩家的移动/技能代码复制一份给怪物用，必须通过 IMovementDriver / ISkill 替换。
```

**这个范例体现的原则**：*复用优先*——夺舍的价值就在"复用怪物已有的技能"，如果 AI 重新实现一遍怪物技能，这个任务的意义就没了；先抽象——`ApplyTakeover` 是架构里预留的钩子，AI 应该找到它，而不是自己发明。

---

### 范例 ③ 新增一个后处理特效（描边）

```text
【任务目标】
给所有敌人加一层可开关的描边后处理，史诗稀有度用金色描边，普通怪用白色细描边。

【涉及系统】
特效 / 渲染（+ 数值数据）

【期望数据来源】
- Excel 表名：Visual.xlsx（或 MonoMaterial.xlsx，按现有表结构）
- 需要新增/修改的字段：outline_color（按稀有度）、outline_width、outline_enabled
- 资源路径：res://shaders/outline.gdshader（新增）、res://materials/outline_material.tres

【必须新增或复用的抽象】
- 复用：IVisualDescriptor（描边参数作为表现描述的一部分读取，不要各怪物各写一套）
- 新增：可选 —— 若现有渲染管线没有"后处理链"抽象，则先补 IPostProcessEffect 接口（空实现 NullPostProcessEffect），再把描边作为其首个实现
- 复用：IStatProvider / Excel 生成物读数值，禁止在 shader 里写死颜色和宽度

【扩展性要求】
变体 1：受击时描边闪红（临时叠加参数）
变体 2：可被遮挡时描边半透明（深度处理）
变体 3：不同阵营不同描边色（TeamId 驱动）
判据：三个变体都改 shader 的 uniform + Visual.xlsx 字段，不改渲染主循环；若需要"临时叠加"，走已有的数值分层思路（Base/Temporary），不要新写一套状态管理。

【验收标准】
1. dotnet build 0 error；
2. TestScene 里敌人可见描边，关闭开关后完全消失，史诗怪描边为金色；
3. .\tools\check_architecture.ps1 无 error；
4. ARCHITECTURE.md §7 渲染与特效架构补"描边后处理"一节，变更日志加一行。

【禁止事项】（同上标准红线，额外注意）
- 禁止把颜色/宽度写死在 .gdshader 里，必须是 uniform 且由 .tres 或生成物驱动；
- 禁止为每个怪物单独做一份描边材质，必须参数化。
```

**这个范例体现的原则**：*数据优先*——描边颜色宽度全在表里；*先抽象*——若渲染管线缺后处理抽象，先补接口再实现，而不是直接在某个怪物的脚本里加 shader 参数。

---

## 3. AI 开工前必须先读的清单（强制顺序）

AI 收到模板后，**必须按这个顺序**读完再动手，顺序不可跳：

| 步骤 | 读什么 | 为什么 | 完成标志 |
|---|---|---|---|
| 1 | `AGENTS.md` 全文 | 项目唯一启动契约，7 条铁规 + 红线清单 | 能复述本次任务触碰了哪几条规矩 |
| 2 | `docs/architecture/ARCHITECTURE.md` 的 **§0 一页速览** + **变更日志** + 与任务相关的章节（按 §2 下方路由表） | 知道现有抽象、目录契约、最近改了什么 | 能说出本次改动落在哪些章节 |
| 3 | `docs/rules/` 下**对应规则全文** | 规则摘要不等于规则正文，正文才有判据和反例 | 能说出本次任务的自检判据 |
| 4 | `.\tools\env.ps1 -Verify` | 确认 Godot / dotnet / 离线 NuGet / cmake / python 可用 | 输出里环境项全 PASS（PEND 表示骨架未落地，属正常） |
| 5 | **输出三件套预判**：边界 · 抽象 · 扩展点（规则5） | 先想清楚再写代码，避免临时搭建 | 三行文字写进回复里 |

**步骤 2 的章节路由表**（按涉及系统找对应章节）：

| 涉及系统 | ARCHITECTURE.md 必读章节 | 必读规则 |
|---|---|---|
| 数值数据 | §4 数据流与管线、§5 数值系统 | `04-data-driven.md`、`05-numeric-package.md` |
| 战斗逻辑 | §6 战斗与技能管线、§3 接口清单 | `03-abstract-first.md`、`05-numeric-package.md` |
| 技能 | §6 战斗与技能管线、§3 接口清单 | `03-abstract-first.md`、`06-extensibility.md` |
| 渲染 | §7 渲染与特效架构、§9 性能基线 | `02-tech-selection.md` |
| 特效 | §7 渲染与特效架构 | `02-tech-selection.md`、`04-data-driven.md` |
| UI | §8 UI 层 | `01-gdscript-ban.md` |
| 工具链 | §1 工程结构、§4 数据流与管线 | `02-tech-selection.md` |
| （涉及 `.gd` 一律先看） | §8 UI 层 | `01-gdscript-ban.md` |

> **停在第 2 步就卡住 = ARCHITECTURE.md 没维护好**。此时先补架构文档，再开工，不要硬写。

---

## 4. AI 收工前必须跑的清单（Definition of Done）

四项全过才算完成，任一不过就是「未完成」：

```powershell
# 1. C# 编译零错误
dotnet build

# 2. 架构守卫零 error（GS001~GS007）
.\tools\check_architecture.ps1

# 3. 就地覆盖更新 docs/architecture/ARCHITECTURE.md
#    - 改对应章节正文（不要追加"v2"章节）
#    - 刷新文件头「最后更新」日期
#    - 在「变更日志」表追加一行

# 4. 环境回归（确认没把环境搞坏）
.\tools\env.ps1 -Verify
```

| # | 检查项 | 通过标准 | 失败怎么办 |
|---|---|---|---|
| 1 | 编译 | `dotnet build` 输出 `Build succeeded` / 0 Error | 修到过为止，不许注释掉报错代码 |
| 2 | 架构守卫 | `check_architecture.ps1` 无 error（GS001~GS007） | 按守卫提示改代码；确需放行则改 `config/architecture-rules.json` + 在 AGENTS.md §4 白名单登记 |
| 3 | 架构大纲 | 对应章节已就地更新 + 变更日志 +1 行 | 补写；**这是规则3 的硬要求** |
| 4 | 环境 | `env.ps1 -Verify` 环境项全 PASS | 环境问题不是本任务引入的，但要报告 |

**交付时必须附上**：
- 上述 4 条命令的**实际输出**（不要口头说"跑过了"）；
- 改动文件清单；
- 需要人拍板的取舍点。

---

## 附录 A：红线速查（抄自 AGENTS.md §3）

1. ❌ UI 层以外新建 `*.gd`、或在 `.tscn` 里挂 GDScript。
2. ❌ 在 C# 逻辑代码里写死游戏数值（伤害、血量、速度、射速、冷却、颜色、尺寸、持续时间、特效参数……）。
3. ❌ 复制粘贴另一处逻辑来「快速实现」，而不是先找/抽公共抽象。
4. ❌ 另建 `ARCHITECTURE_v2.md` 之类的平行文档代替就地更新。
5. ❌ 在 `scripts/ui/` 里调用 `Game.Combat` / `Game.Skill` 的结算逻辑（UI 只能读数据和发事件）。
6. ❌ 直接手改 `data/generated/` 下的文件（改 Excel 后重新生成）。
7. ❌ 修改引擎源码（确需时走 `02-tech-selection.md` 的 fork 流程并记录引擎版本）。

---

## 附录 B：现有抽象接口清单（摘自 ARCHITECTURE.md §3）

> **接口清单的权威来源是 `docs/architecture/ARCHITECTURE.md` §3，本文档仅为快照，如有出入以 ARCHITECTURE.md 为准。**

填「必须新增或复用的抽象」时，**先从这里挑**；挑不到才考虑新增。

| 接口 | 所在层 | 一句话职责 |
|---|---|---|
| `IDamageable` | `Game.Combat` | 接受伤害 |
| `IAttacker` | `Game.Combat` | 发起伤害 |
| `IDamageModifier` | `Game.Combat` | 结算管线中改伤害（词条/护甲/暴击） |
| `IActor` | `Game.Actor` | 场景里「一个可被瞄准的东西」，含 `ApplyTakeover` |
| `ISkill` | `Game.Skill` | 一段可释放的技能逻辑 |
| `ISkillTarget` | `Game.Skill` | 技能作用对象句柄，按类型解析目标 |
| `IMovementDriver` | `Game.Actor` | 移动输入 → 速度解算，可替换（地面/飞行/追击/被夺舍） |
| `IProjectile` | `Game.Combat` | 子弹生命周期钩子 |
| `IStatProvider` | `Game.Data` | **数值包的唯一对外读口** |
| `IRandomProvider` | `Game.Core` | 可播种随机（决定论回放） |
| `IConfigSource` | `Game.Data` | 生成物加载与版本校验 |
| `IPoolable` | `Game.Core` | 对象池成员（子弹/特效/飘字） |
| `IVisualDescriptor` | `Game.Actor` | 表现层可替换描述（夺舍时整体换外观） |

> 完整签名见 `ARCHITECTURE.md` §3，或 `docs/rules/03-abstract-first.md` 的接口清单章节。
> 本表为写作时的快照，接口的增删与「候选·待转正」等状态变更不会自动同步到这里——实际使用时以 ARCHITECTURE.md §3 为准。

---

## 附录 C：常见坏提示词 vs 好提示词

| ❌ 坏 | 为什么坏 | ✅ 好 |
|---|---|---|
| 「加个散弹枪」 | 没有数据来源、没有抽象、没有验收 | 「新增散射弹，数据在 Bullet.xlsx 新增一行，复用 IProjectile，验收 TestScene 见 5 发扇形」 |
| 「优化一下性能」 | 没有基线、没有可测目标 | 「把每帧伤害结算的 GC 分配降到 0，用 §9 性能基线里的帧预算验收」 |
| 「顺便把 UI 也改了」 | 一个任务两个系统，违反拆分原则 | 拆成两个任务分别发 |
| 「你看着办，怎么好怎么来」 | AI 会选最省事的路，通常就是临时搭建 | 至少写清「扩展性要求」的三个变体 |
