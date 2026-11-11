# 角色模块（character）索引

> 模块序号 **03**（01 策划 / 02 地编 / **03 角色** / 04 开发 / 05 ECS）｜ 建立 2026-11-11 ｜ 状态：索引已建立
> 读者：角色模块负责人；以及需要「加一个人物 / 加一只怪 / 改 HUD / 接夺舍」的任何人。
> 边界定义见 [`../_shared/BOUNDARY.md`](../_shared/BOUNDARY.md) 的 §2-③「角色」。
> **本文只做索引与口径，不复制原文契约**——凡是本文与原文冲突，以原文为准，并请把冲突反馈给本模块负责人。

---

## 0. 一句话口径

> **角色 = 数值包 + 技能包 + 模块集合（装配清单），全部落在数据行里；C# 侧 0 行改动。**
> 玩家与怪物是**同一套组件、同一条装配路径**，区别只在「填了几个槽、填了什么数」。

这句话有三个出处，互相印证，不是本文新造：

| 说法 | 出处 |
|---|---|
| 「五层数值包 + 一个技能包 + 阵营/控制两个标签」的 ECS 实体组合 | `ARCHITECTURE.md` §15 导语 |
| 「人物 = 数据行 × 模块集合」，换变体只改 xlsx 一行 | `ARCHITECTURE.md` §18.1 |
| 差异只有三处：能力位（tag）/ 数值包（StatPacks）/ 技能包→武器子弹（SkillLoads.WeaponId） | `ARCHITECTURE.md` §22.2 |
| 怪物与人物「差别只在填了几个槽」——怪物只有 `PrimaryAttack` 一槽 | `ARCHITECTURE.md` §22.5 |

**口径拆解（本模块的判据）**：

```text
角色(Characters 一行)
├─ 数值包  = BasePackage + GrowthPackage + EquipmentPackage   → StatPacks 若干行（按 (PackId, Layer) 归组）
├─ 技能包  = SkillLoadout                                     → SkillLoads 五槽（Slot 定长 5）
├─ 模块集合 = Assemblies.Modules（+ VariantAssembly.ExtraModules）→ 决定管线里挂哪些系统
└─ 表现/操作包 = VisualPack / CameraPack / InputPack / Weapon   → 长什么样、镜头怎么跟、拿什么枪
```

| 层 | 契约真源（只读，不在此复制） | 本模块的职责 |
|---|---|---|
| 数值层栈与 Fold 语义、赋值矩阵 | [`../../rules/05-numeric-package.md`](../../rules/05-numeric-package.md) §3 / §4 | 说清「一个角色由哪几层包拼成」「夺舍替换哪一层」 |
| 扩展预算（加一个角色 ≤ 几文件） | [`../../rules/06-extensibility.md`](../../rules/06-extensibility.md) §2 / §4.7 | 把预算数字落到「哪几张表哪几格」 |
| 组件 / 系统 / 五槽 / 意图契约 | `ARCHITECTURE.md` §15、§18、§19 | 索引到「哪一节讲什么」 |
| HUD 只读快照 | [`../../design/character-hud-contract.md`](../../design/character-hud-contract.md) §2 | 列出 DTO 字段与「不许碰结算」的红线 |
| 具体数值填多少 | 由**策划**决定（见 [`../_shared/BOUNDARY.md`](../_shared/BOUNDARY.md) §2-①） | 本模块只定义「填在哪一列」，不定义「填几」 |

---

## 1. 必读（按顺序，共约 3 份 + 3 节）

| # | 文档 | 读哪一节 | 为什么读 |
|---|---|---|---|
| 0 | 仓库根 `AGENTS.md` | §0 / §2 / §3 / §5 / §7 | 铁规 6（数值包）、规则 5/7；红线 5（UI 不结算）、红线 12（同类产物带同样组件） |
| 1 | [`../../architecture/ARCHITECTURE.md`](../../architecture/ARCHITECTURE.md) | §15 → §18 → §19 → §22 | 抽象 → 第一个可玩人物 → 一个真技能怎么落地 → 内容集口径。**按这个顺序读**，跳读 §18 会看不懂 §19 的坑 |
| 2 | [`../../design/character-hud-contract.md`](../../design/character-hud-contract.md) | 全文（332 行） | 「本会话所有并行开发者之间的唯一接口约定」；**接口要变 = 先改它并通知** |
| 3 | [`../../rules/05-numeric-package.md`](../../rules/05-numeric-package.md) | §3 层栈 · §3.3 Fold · §4 赋值矩阵 · §4.4 未知属性 · §4.5 夺舍示例 | 「是什么」= 包；「怎么叠加」= Fold；「互相赋值」= 矩阵 |
| 4 | [`../../rules/06-extensibility.md`](../../rules/06-extensibility.md) | §2 扩展预算表 · §4 夺舍走查 · §4.7 夺舍语义 S1~S6 | 「新增一个 X 要改几个文件」的硬性验收数字 |

**次归属（用到再看，不要求通读）**：

| 文档 | 次归属章节 | 角色模块何时需要 |
|---|---|---|
| `ARCHITECTURE.md` §5 | 数值系统 StatPackage 分层 | 与 `rules/05` 配套的落地版；改数值口径先看这里 |
| `ARCHITECTURE.md` §6 | 战斗与技能管线（一帧结算顺序） | 新技能要插进帧序时 |
| `ARCHITECTURE.md` §8 | UI 层（唯一允许 GDScript） | 改 HUD 画法 |
| `ARCHITECTURE.md` §20 / §21 | 标签轴 / 标签分层 | 角色行为按 `Role`/`Race` 轴筛内容时（如「对族裔增伤」） |
| `ARCHITECTURE.md` §23 | 本地化 `l10n.xlsx` | **加任何人物/怪物必须同步加中文名**（§23.9） |
| `ARCHITECTURE.md` §14.10 | 训练房场景 | 起 `TrainingRoom` 单测一个人物时 |
| `ARCHITECTURE.md` §15.8 | 相机跟随手感 | 改 `CameraPacks` 或头部相机手感 |
| [`../../architecture/ECS.md`](../../architecture/ECS.md) §4 / §6 | 组件规范 / 一帧顺序 | 新增角色组件或系统时（权威在 ECS 模块） |
| `rules/01` §2 | UI 层唯一定义 | HUD 能不能用 `.gd` |
| `rules/03` §3.4 | 概念唯一契约（四问） | 新增角色相关组件/字段前的完备性判据 |

---

## 2. 五槽技能现状（`SkillSlot` 定长 5）

**槽位是契约**（`ARCHITECTURE.md` §15.4），「哪个键对哪个槽」由 InputMap 固定，改键改的是 `project.godot` 不是技能表。

> 现状口径：`SkillLoads.Kind` 只有 **`fire_projectile` / `dash` / `data`** 三种合法取值
> （唯一定义点 `scripts/host/composition/ContentSkillFactory.cs` 只认前两种，其余一律造 `NullSkill`；`data` = 占位）。
> **「占位」的含义是：冷却照走、法力照扣、HUD 照显示，但没有行为。**

| 槽 | 值 | 默认键（InputMap action） | HUD 字形 / 键标 | 数据（`SkillLoads.Kind`） | 运行时实现 | 现状判定 | 出处 |
|---|---|---|---|---|---|---|---|
| `Dash` | 1 | Shift / `actor_dash` | `>>` / `LSHIFT` | `dash`（5 个英雄/玩家包 + `chosen_kit` 全部改为 `dash`） | `DashSkill` + `DashState` + `DashSystem`（`FixedUpdate`） | ✅ **真实现** | §19 全文；§15.4 |
| `PrimaryAttack` | 2 | 左键 / `actor_primary_attack` | `ATK` / `LMB` | `fire_projectile`（英雄包 + `player_kit`）+ `WeaponId` | `FireProjectileSkill` → `CastSystem.TryCast` → 物件工厂铸造真子弹 | ✅ **真实现**（真子弹飞出去、真命中检测） | §18.4 ③；§18.6 |
| `WeaponSwap` | 3 | Q / `actor_weapon_swap` | `SWAP` / `Q` | `data`（所有包，含 `player_kit`/`chosen_kit`） | `NullSkill` | ⛔ **占位**（「改下次射击用什么」未实现） | §18.8 ④；§15.7 下一步① |
| `Signature` | 4 | R / `actor_signature` | `SIG` / `R` | `data`（每个英雄包一个不同 `SkillId`：1301/1304~1308，另 1302 ogre、1303 chosen） | `NullSkill` | ⛔ **占位**（角色独有技能，§15.4 称其为「角色身份的核心表达」） | §18.8 ④；§15.7 下一步① |
| `Interact` | 5 | F / `actor_interact` | `INT` / `F` | `data`（所有包 1401） | `NullSkill` | ⛔ **占位**（与场景交互，不产生伤害） | §18.8 ④；§15.7 下一步① |

**四条读表须知**：

1. **怪物只有一槽**：`goblin_kit` / `goblin_archer_kit` 只有 `PrimaryAttack`（且 `Kind=data`）；`ogre_kit` 是 `PrimaryAttack` + `Signature` 两槽；`chosen_kit` 与人物一样五槽全满（§22.5「填了几个槽」）。
2. **冷却写在两张表里且必须对齐**：`SkillLoads.CooldownSeconds`（英雄包左键 0.12 / 0.15 / 0.42 / 0.08 / 0.18 s，对应五个人物节奏，§22.3）与 `Weapons.ShotCooldown` 同值；表头备注写明「冷却 == 武器 ShotCooldown」。
3. **失败也是数据**：门控失败写进 `SkillState.LastFailure`（`NoSkill`/`Cooldown`/`NoMana`/`TargetInvalid`/`Forbidden`），HUD 与音效据此反馈，**每帧只消费一个槽**（§15.4）。
4. **冲刺手感属于角色而非技能**：`DashSpeedMul` / `DashDuration` 是 `StatPacks` 的列，同一个 `DashSkill` 类、同一行技能数据冲出五种手感（§19.5、§22.3）。

**把一个占位槽改成真实现的固定动作**（照 §19 的冲刺走一遍即可）：

```text
① 数据：SkillLoads.Kind 由 data 改成新种类名（如 weapon_swap / signature_xxx）
② 契约：Game.Core 若需要新 StatId（如新技能的倍率/时长），走 rules/05 §5.4 追加号段
③ 实现：scripts/skill/ 新增 ISkill 实现（只做 预检 + 施放；状态放组件、结构变更走 ICommandSink）
④ 工厂：ContentSkillFactory 加一个常量 + 一个分支（「种类 → 实现类」的唯一定义点）
⑤ 若需要每帧推进：新增 SystemBase（带 [Module("...")]），在 CharacterSession 注册
⑥ 测试：CanCast/Cast 双向门控、状态落在实体而非技能实例、端到端位移/伤害
```

---

## 3. 加一个新角色 / 新怪物的最小改动清单

> 预算数字来自 `rules/06` §2：**新怪物 ≤ 2 文件（理想 0 代码 + 1 表行）；新人物数值变体 0 代码；新技能种类 1 文件**。
> 实测量来自 `ARCHITECTURE.md` §15.6 / §18.6 / §19.6 / §22.9 的「规则 7 实操核对」表。

### 3.1 加一个新人物（同一装配的数值变体）——**1 个文件**

| 步 | 动哪张表的哪几行 | 依据 |
|---|---|---|
| 1 | `characters.xlsx / Characters` 加 1 行：`Id` / `DisplayKey` / `Kind` / `DefaultTeam` / `Control` / `BasePackage` / `SkillLoadout` / `HitRadius` / `HitHeight` / `VisualSlot` | §15.5 |
| 2 | `characters.xlsx / StatPacks` 加 1 个 Base 包（同一个人物通常 20+ 行，`(PackId, Layer, StatId)` 唯一） | §15.5 |
| 3 | `characters.xlsx / SkillLoads` 加 5 行（`Dash` / `PrimaryAttack` / `WeaponSwap` / `Signature` / `Interact`）→ `Kind` 填 `dash` / `fire_projectile` / `data`… | §15.4、§18.5 |
| 4 | `characters.xlsx / VariantAssembly` 加 1 行（指向 `AssemblyId=hero`、各包、`Weapon`、`VisualPack`、`CameraPack`、`InputPack`） | §18.2 |
| 5 | 若复用现有外观包则 `VisualPacks` 不用动；换外观 = 加 1 行 `VisualPacks` | §18.6「人物换外观白模 = 0 文件」 |
| 6 | `combat.xlsx / Objects` 若要上阵对打，加 1 行（`tag.actor` + `tag.hero` 等）；5 人物的做法是每人一颗子弹物件（§22.4） | §22.2 |
| 7 | `l10n.xlsx` 加中文名（人物、技能若新增 `Key`） | §23.9「忘了不崩，但校验器会报缺名」 |
| 8 | **同步 C# 内置镜像** `CharacterContentSet.BuildBuiltinV1()` | §22.9 坑 5：`CharacterXlsxParityTests` 逐项比对，只改一边必红 |

**C# 代码改动：0 行**（系统、装配、HUD 都不用动）。若上面第 8 步被判定为「代码」——它现在确实是代码，见 §7 待核对 Q-C3。

### 3.2 加一只怪物 —— **1 个文件（理想 0 代码）**

与 3.1 同构，差异只有三处：

| 差异 | 填法 |
|---|---|
| `Characters.Kind` / `DefaultTeam` / `Control` | `Monster`（或 `Elite`/`Boss`）/ `Enemy` / `Ai` |
| 技能包只填需要的那几个槽 | 现有怪物只填 `PrimaryAttack`（`ogre_kit` 另填 `Signature`） |
| 数值包只填怪需要的 StatId | `goblin_base` 11 项 vs 人物 22 项；缺的项按 `StatDefs.Default` 取默认（`rules/05` §4.3） |
| 能力位 | `combat.xlsx / Objects` 一行 `tag.actor` + 族裔 `tag.goblin`/`tag.slime`/`tag.ogre` + `Role` + `Rarity` |
| 外观 | `VisualPack` **必填**；「有意没有模型」填 `vp_none`（§17.10 唯一入口） |

**怪物用上自己的技能 = 1 个文件**：需要一个 `IIntentSource` 实现（宿主构造时传入），见 §15.6「让怪物会用技能」。

### 3.3 加一个新技能种类 —— **2 个文件**

| 文件 | 改什么 |
|---|---|
| 新 `ISkill` 实现（`scripts/skill/`） | 预检 + 施放；**不许**在 `Cast` 里 `World.Create/Destroy`（J5） |
| `ContentSkillFactory` | 加一个 `Kind` 常量 + 一个分支 |
| （若需每帧推进）新 `SystemBase` + `[Module]` | 记 §19.8 坑 1：忘了 `[Module]` 直接抛 `GameEcsException`，Godot 侧表现为**场景空转** |

### 3.4 最小改动清单的**反面清单**（改了就是违规）

1. ❌ 改 `ActorAssemblySystem` / `SkillCastSystem` 来适配新角色（§15.7 约束④：「改不动它们才是抽象成功的标志」）。
2. ❌ 新建 `class Monster` / `class Player` 或在组件里加 `IsMonster`（§15.2 反例）。
3. ❌ 在组件里存 `ISkill` / `IReadOnlyStatView` 引用（J2：组件必须 unmanaged，只存槽位号）。
4. ❌ 角色逻辑里读 `Input`（只有 `scripts/host/` 碰引擎，J4）。
5. ❌ 只改 xlsx 不改 `CharacterContentSet`（`CharacterXlsxParityTests` 必红）。
6. ❌ 同一个 `PackId` 同时出现在两层（`CharacterContentSet.FlushStatPacks` 直接抛异常，§22.9 坑 2）。

---

## 4. HUD 契约要点

**红线**：`scripts/ui/**` **只读快照 + 绘制，不做结算**（`AGENTS.md` 红线 5；`character-hud-contract.md` §1、§2）。
UI 层不准 `using Game.Core` / `Game.Combat` / `Game.Skill`——槽位号、字形、键标一律由**宿主填进 DTO**（契约 §2 那只 C# 注释块讲的就是这件事）。

| 要点 | 内容 | 出处 |
|---|---|---|
| 唯一输入类型 | `HudSnapshot`（`readonly struct`）：`HasCharacter` / `CharacterName` / `VariantTag` / `Health·Mana·Stamina`（`HudBar`）/ `Armor` / **`ArmorReduction`（已算好 0..1）** / `MoveSpeed` / `CritChance` / `CritMultiplier` / `Level` / `Skills[5]` / `AimAngleDeg` / `FrameSeconds` | 契约 §2 |
| 技能格 | `HudSkillSlot { HasSkill, Ready, CooldownProgress, Glyph, KeyLabel }`；`Skills` 长度恒 5，索引 = `(int)SkillSlot - 1`，宿主 `EnsureSkills()` **只分配一次** | 契约 §2 |
| 字形 / 键标 | `>>` `ATK` `SWAP` `SIG` `INT`；`LSHIFT` `LMB` `Q` `R` `F`（实际落点在宿主的 `HudGlyphs`，DTO 里不出现 `SkillSlot` 类型） | 契约 §2；§15.4 |
| **护甲减伤口径（必须与战斗一致）** | `armor / (armor + armorK)`。`test_hero` = 6/(6+100) ≈ **6%**；曾写成 `1 - armor/(armor+k)` → 显示 94% | §18.5 |
| 显示哪些字段 | HP / 法力 / 耐力 / 护甲减伤 / 移速 / 暴击 / 瞄准角 / 五技能槽 | §18 |
| 枚举化 | 加一个新 HUD 字段 = `HudSnapshot` 加字段 + `CharacterHudDrawer` 画它 = **2 个文件** | §18.6 |
| 命名一致性 | `Characters.DisplayKey` 规范为 `actor.<Id>`（`combat.xlsx` 曾不一致，已改齐） | §23.6 |
| 日志约定（自动校验用） | `[Hero] variant=… assembly=… modules=…` 开局一行；`[Hero] hud name=… hp=… mana=… armor=…(<pct>%) speed=… crit=…` 每 60 帧 | 契约 §8 |

**已知债务**：交叉准心只有一处（HUD Drawer 画的屏幕中心），`ViewOptions.CrosshairVisible` 默认 false（§18.8）。

---

## 5. 夺舍 / 互相赋值的入口字段

本模块只**索引**入口；语义真源在 `rules/06` §4.7（S1~S6）与 `rules/05` §4。

### 5.1 两个字段（生成期）——`ActorSpawnRequest`

`ActorSpawnRequest` 是**唯一造人入口**（命令载荷，队列化到帧边界，J5）。夺舍**不新增任何类型**，只改两个可空覆盖字段：

| 字段 | 类型 | 语义 | 夺舍时的填法 |
|---|---|---|---|
| `TeamOverride` | `TeamId?` | 阵营覆盖（`null` = 用定义的 `DefaultTeam`） | 玩家夺舍敌人 → 填 `Player`（决定敌我，**不是** `ActorKind`） |
| `ControlOverride` | `ControlScheme?` | 控制方案覆盖（`null` = 用定义的 `Control`） | 由 `Ai` 改成 `PlayerInput` —— 这就是「夺舍」本身 |
| `WeaponOverride` | `WeaponStats?` | 武器数值覆盖（`null` = 按定义查表） | 「这只怪拿什么枪」是调用方的决定，夺舍/刷怪/掉落共用 |

> `ActorKind` 只做**标签**（规则/AI/动画筛选），与 `Team` **彼此独立**（§15.2 那张表）。

### 5.2 数值互相赋值（运行期）——`StatPackage` 层面

| 操作 | 前提 | 语义 | 出处 |
|---|---|---|---|
| `AssignFrom(other)` | 同 schema | 整包替换；跨 schema = Debug 断言失败 | `rules/05` §4.2 |
| `AssignFromWithDefaults(other, defaults)` | 任意（含跨 schema） | 对方有的用对方；对方没有、我有的取 `defaults`；对方有、我没有的进 `UnknownStats`；**绝不抛异常** | `rules/05` §4.2 / §4.3 |

**夺舍替换哪一层**：只替换 **Base 层**（`rules/05` §3.2）；
`Growth` / `Equipment` **保留**（玩家的成长永远是玩家的）；
`Buff` 按 `BuffDef.KeepOnPossess`；`Temporary` 保留并按原时长继续倒计时。

**同一个包可以互相赋值**（§15.3）：`player_base` 可给怪、`player_growth_demo` 可挂给怪试平衡。

### 5.3 夺舍的六条语义（验收条件，不得偏离）

| # | 语义 | 验收标志 |
|---|---|---|
| S1 | 技能列表随 Base 层一起替换 | 夺舍后技能键释放的是怪物的 `ISkill` |
| S2 | 玩家原技能压入快照、不参与 `CanCast`/`Cast` | 快照里能找到原技能 |
| S3 | 回滚完整（Base / 技能 / 外观 / 移动驱动 / 阵营） | 前后 `Get(StatId)` 逐个相等 |
| S4 | 不叠加 | 夺舍中再次 `TryApplyTakeover` 返回 false 且无副作用 |
| S5 | 结算不变 | `scripts/combat/**` diff = 0 |
| S6 | 外观全换，不改任何 `.tscn` | — |

> **重要落差**：上面 S1~S6 写在 `rules/06` §4.7，是 `Actor.TryApplyTakeover` 风格的**设计契约**；
> 而工程里实际已落地的是 §15.2 的**生成期两字段**（`ControlOverride` / `TeamOverride`）。
> 「运行期夺舍一个已有怪物」（保留玩家技能/回滚/外观全换）**尚未落地**——见 §7 待核对 Q-C5。

---

## 6. 常见任务路由

| 你想做的事 | 去哪 |
|---|---|
| 新增/修改一个角色的数值 | 表 → **策划**；口径（填哪一层）= 本 README §0 + `rules/05` |
| 加一个人物/怪物 | 本 README §3；回归：`CharacterXlsxParityTests` |
| 把某个占位技能做成真的 | 本 README §2 的六步；范式照 §19（冲刺） |
| 改 HUD 显示什么 | 本 README §4；字段清单在 `character-hud-contract.md` §2 |
| 改 HUD 怎么画（字体/皮肤/布局） | 契约 §1 的 `CharacterHudSkin` / `CharacterHudDrawer` |
| 夺舍 | 本 README §5；语义真源 `rules/06` §4.7 |
| 相机手感 / 镜头包 | §15.8；`characters.xlsx / CameraPacks`（**地编**主责） |
| 新增角色组件/系统 | → **ECS 架构师**（`ECS.md` §4/§6；`rules/03` §3.4 契约四问） |
| 角色外观模型 / `VisualPack` | 本模块定口径（§17.10 唯一入口），**地编**负责摆进空间 |
| 角色中文名 | `l10n.xlsx`，规则见 §23.9 |

**我不管什么**：组件/系统本身的定义（ECS）；数值填多少（策划）；房间/相机/摆位（地编）；SOP 与架构守卫（开发）；渲染与 shader（开发）。

---

## 7. 待核对（**只记录，不改原文**）

> 以下均为只读核对结果（`openpyxl` 读 `data/excel/*.xlsx` + 读源码），不是本 README 的断言优先级高于原文；
> 请各自负责人确认后再决定是「文档过时」还是「数据缺失」。

| # | 发现 | 证据 | 影响 | 建议 |
|---|---|---|---|---|
| **Q-C1** | **`characters.xlsx` 里没有 `test_hero_elemental`** | `Characters` 表 9 行：`player_hero` / `test_hero` / `test_hero_tank` / `test_hero_agile` / `test_hero_lucky` / `goblin_grunt` / `goblin_archer` / `ogre_brute` / `chosen_hero`；`SkillLoads` 9 个包，无 `*_elemental_kit`。git `HEAD` 版本同样是 9 行 | §22.3「五个人物」表里第 5 行 `test_hero_elemental`（元素法师）在真表中**不存在**；`orb_elemental` 武器、`bullet_hero_orb` 子弹（§22.4）也查不到（`Weapons` 只有 4 行、`combat.xlsx/Objects` 无 `bullet_hero_*`） | 由策划 + 本模块确认：是表漏了，还是 §22.3 超前于表 |
| **Q-C2** | **C# 内置镜像里却有 `test_hero_elemental`** | `scripts/data/CharacterContentSet.cs` 有 `test_hero_elemental_base`、`orbElemental`、`test_hero_elemental_kit`、`HeroRow("test_hero_elemental", …)` | 与 §22.9 坑 5「表 ↔ `BuildBuiltinV1()` 逐项比对」方向相反：**是镜像多出来**。若 `CharacterXlsxParityTests` 现在仍绿，说明比对口径只覆盖表里有的行 | 请开发/ECS 确认 parity 用例是否只做「表 ⊆ 镜像」单向比对 |
| **Q-C3** | 「加一个人物 = 0 行 C# 改动」与「必须同步 `CharacterContentSet`」并存 | §15.6 / §18.1 说 0 行；§15.5 与 §22.9 坑 5 说改表必须同步镜像 | 「0 行」只在「镜像已被生成器覆盖」后成立；当前镜像仍是手写 | 与 `rules/06` §2 的计数口径（生成物不计）对齐说明，避免新人误判 |
| **Q-C4** | `VariantAssembly` 只有 4 行（4 个 `test_hero_*` 变体），`Assemblies` 只有 1 行（`hero`） | 读表 | 怪物与 `player_hero` / `chosen_hero` 走的是 `Characters` 直填，不经变体装配；§18.2 却把 `VariantAssembly` 列为九张表之一 | 请确认「谁必须进 `VariantAssembly`、谁不用」的判据 |
| **Q-C5** | 运行期夺舍未落地 | `rules/06` §4.7 描述的是 `Actor.TryApplyTakeover` + `TakeoverSnapshot`；工程里能检索到的是 `ActorSpawnRequest.ControlOverride/TeamOverride`（生成期） | README §5 已按「生成期已落地 / 运行期未落地」分层陈述 | 请 ECS 与本模块确认夺舍任务的当前状态 |
| **Q-C6** | `character-hud-contract.md` 头注「收工前会被并入 `ARCHITECTURE.md` §17」 | 契约文件第 4 行 | 实际内容并入了 **§15 / §18**（§17 是「基础物件抽象」） | 契约文件注明指向已过时；**本模块不改原文**，仅记录 |
| **Q-C7** | `WeaponStats` 结构在契约里出现两次且字段不同 | 契约 §3 与 §3（角色域组件段）各一版：一版 `ShotIntervalSeconds` + `PelletsPerShot`，一版 `ShotCooldown` 且无 `PelletsPerShot` | 契约 §3 顶部已写「以源码 XML 注释为准」，但两版并存仍易误读 | 以源码 `scripts/actor/ActorComponents.cs` 为准 |
| **Q-C8** | §18.5「v1 只有左键是真实现，其余四行占位」已过时 | §18.8 ④ 已自行更正：「冲刺已实现（见 §19）；剩切武器 Q / 大招 R / 交互 F 三个」 | 本文 §2 采用 §18.8 口径 | 建议 §18.5 就地修正（由 ARCHITECTURE 托管方 ECS 处理） |
| **Q-C9** | 工作区里 `data/excel/characters.xlsx`、`combat.xlsx`、`CharacterContentSet.cs` 均为未提交修改（`git status` 为 `M`），mtime 2026-11-10 12:07~12:16 | 本次只读核对：`HEAD` 版与工作区版的 `Characters` 行数相同（9），故 Q-C1 不是未提交改动造成的 | 台账 | 无 |
| **Q-C10** | 槽位「R 独有技能」在不同包里 `SkillId` 不同（1301/1304~1308、1302、1303），但 `Kind` 全是 `data` | `SkillLoads` 全表 | 说明「独有技能」目前只有编号、没有行为 | 做 Signature 实现时先定「1301~1308 各是什么」 |

---

## 8. 只增不改纪律（本文遵守）

1. 本次只写 `docs/modules/角色/**`；17 份原文档、`data/excel/*.xlsx`、`scripts/**` **零改动**。
2. 核对用的临时脚本与导出已删除，未留在仓库。
3. 发现与原文不一致处一律写进 §7「待核对」，不直接改原文。
4. 本文不复制原文契约（DTO/组件定义/层栈图），只给指针与口径；契约变更请改原文并通知。
