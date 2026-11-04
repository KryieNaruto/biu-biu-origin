> 状态: v1  最后更新: 2026-11-03

# 规则 7 · 可扩展性（新增功能的同时不大规模重写）

## 1. 规则原文

> 「所有任务，必须思考可扩展性！能够增加新功能的同时，不用大规模重写。例如，如果人物技能是夺舍怪物技能，无需重写大片逻辑。」

**核心判据**：任何功能需求，先回答一句话 —— **「新增一个 X，要改几个文件？」** 数字必须落在本文 §2 的预算内。超预算 = 抽象位置错了，先回去改抽象（规则 5），不要硬写。

---

## 2. 扩展预算（**硬性验收数字**）

> 计数口径：**新增或修改的文件数**，不含 `data/generated/` 产物（生成物可重建，不计入）、不含文档、不含 `config/architecture-rules.json`。Excel 工作表按 "1 个数据行" 计为 0 个代码文件。

| 新增的东西 | 允许的文件数 | 通常需要做什么 | 判违规的典型写法 |
|---|---|---|---|
| **一个新怪物** | **≤ 2**（理想 0 代码 + 1 表行 + 1 预制体） | `Characters` 加行；若复用现有预制体则只需数据 | 新写 `MonsterXxx.cs` 继承旧怪物 |
| **一个新 Boss** | **≤ 2** | 同上 + 波次表加行 | 新写 `BossController.cs` 且内含专属阶段逻辑 |
| **一个新人物的数值变体**（皮肤/拾取物） | **0 代码** | Excel 加行 | 新写类 |
| **一个新技能（组合型）** | **0 个新文件** | Excel 加 `Skills` 行，用已有效果/公式组合 | 新写一个技能类却只是"换个数值" |
| **一个新技能"种类"**（新的行为模式） | **1 个文件**（实现 `ISkill`） | `scripts/skill/*.cs` 加一个实现 + Excel 登记 | 改 `CombatResolver` 加分支 ← **重罚** |
| **一个新子弹样式** | **0 个代码文件**（1 表行 + 可选 1 个 fx 场景） | `Projectiles` 加行 | 新写 `BulletXxx.cs` |
| **一个新特效** | **0 个代码文件** | `Effects` 加行 + fx 场景 | 在 Combat 里写粒子参数 |
| **一个新词条 / 增益** | **0~1** | 纯数值词条 = 0；有专属逻辑 = 1（`IDamageModifier` 实现） | 在伤害公式里加 `if (hasTrait)` |
| **一个新伤害修正** | **1** | 实现 `IDamageModifier` + `Modifiers` 表加行 | 改 `DamageResolver` 内部顺序 |
| **一个新移动模式** | **1** | 实现 `IMovementDriver` | 在 Actor 里加 `if (isFlying)` |
| **一个新敌人 AI** | **≤ 2** | `IMovementDriver` + `ISkill` 组合；仅当需要新决策形态才加类 | 新写一个完整 AI 类复制旧 AI |
| **一件新装备** | **0 代码** | `Items`/`Weapons` 加行 | 新写类 |
| **一个新关卡/波次** | **0 代码** | `Waves`/`Rooms` 加行 | 硬编码刷怪 |
| **一个新 RenderPass** | **≤ 2** | `shaders/*.gdshader` + 注册；重逻辑进 `native/` | 改引擎源码 |
| **一个新 GDExtension 类** | **1~2** | `native/src/*.cpp` + 注册 + `Game.Native` 薄封装 | 业务逻辑写在 C++ 里 |

**超预算的处理顺序（不得跳步）**：

```text
① 先改抽象（回到规则5：这个变化点该有一个接口/数据字段吗？）
② 再改数据（能不能用 Excel 字段 + 组合表达？）
③ 最后才新写文件（并在此文档 §8 登记"这次为什么必须新写"）
```

---

## 3. 五类扩展点

| 类别 | 扩展方式 | 扩展时改什么 | 典型例子 | 预算 |
|---|---|---|---|---|
| **E1 数据扩展** | Excel 加行 | 只需 `data/excel/*.xlsx` | 新怪物、新武器、新子弹样式、新特效、新波次 | 0 代码文件 |
| **E2 组合扩展** | 资源互相引用（`ref:` / `refs:`） | Excel 里把已有零件拼起来 | 新技能（弹幕+灼烧+击退）、新敌人（追击+分裂）、新套装 | 0 代码文件 |
| **E3 策略扩展** | 实现接口，数据按名引用 | 1 个 `.cs` + 1 行 Excel 登记 | 新技能种类、新移动驱动、新伤害修正、新命中行为 | 1 文件 |
| **E4 管线扩展** | 事件/钩子订阅，不触碰主流程 | 1 个订阅文件 + `Triggers` 表加行 | 吸血、护盾、击杀连锁、成就、连击计数 | 1 文件（**`Triggers` 落地后降为 0**，见 §9.1） |
| **E5 原生扩展** | GDExtension 注册新类 | `native/src/` + `Game.Native` 门面 | 批量抛射物求解、渲染 pass、后处理 | 1~2 文件 |

**选择顺序**：**E1 > E2 > E3 > E4 > E5**。能用数据表达的绝不用代码；能用代码组合的绝不下沉 C++（与 `02-tech-selection.md` 一致）。

---

## 4. 【验收核心】「夺舍怪物技能」完整走查

> 这是本套文档的可执行性总验收。以下内容必须是**能照着写代码的程度**，不允许出现"通过抽象实现"这类空话。

### 4.0 需求拆解

| 需求 | 拆成可执行的子需求 | 复用的已有抽象 |
|---|---|---|
| 按下技能键，选中前方最近的怪物，附着其身 | 目标选取 | `ISkill`、`ISkillTarget` |
| 屏幕上控制的角色变成那只怪物 | 数值替换 | `IActor`、`IStatProvider`、`StatPackage` |
| 外观、体型、配色、子弹样式都变成怪物的 | 表现替换 | `IVisualDescriptor` |
| 玩家能用上怪物的技能 | 技能列表替换 | `ISkill`、`IStatProvider.Skills` |
| 受伤、伤害、护甲全部按怪物算 | 结算改造 | `IDamageable`、`DamageResolver`、`IStatProvider` |
| 持续时间结束后变回自己 | 状态回滚 | `StatPackage` 快照 + 层替换 |
| 期间血量/击杀等玩法（如"寄居"词条）正常算 | 事件不中断 | C# event / `DamageApplied`、`ActorKilled` |

### 4.1 五步流程（含改动位置）

```text
 ┌─────────────────────────────────────────────────────────────────────────┐
 │ 步骤1  技能声明与目标选取  (Game.Skill)                                  │
 │   SkillDef: id=takeover, type=TakeoverSkillKind, params{range, duration} │
 │   TakeoverSkill.CanCast(ctx)  → 找到最近的存活敌方 IActor                │
 │   选目标用 ISkillTarget.TryResolve<IActor>(out tgt)                      │
 │   ── 复用：ISkill / ISkillTarget / IActor / IRandomProvider（不需要随机）│
 ├─────────────────────────────────────────────────────────────────────────┤
 │ 步骤2  捕获快照  (Game.Skill + Game.Actor)                              │
 │   var self = ctx.Caster.Stats;          // IStatProvider                │
 │   快照 = { Base=self.GetLayer(Base),                                     │
 │            Skills=self.Skills.ToArray(),                                 │
 │            Visual=self.Visual.Def }        // 全部是不可变值，直接持有引用 │
 │   快照存入 Temporary 层的一个 slot（slotId="takeover"）                  │
 ├─────────────────────────────────────────────────────────────────────────┤
 │ 步骤3  替换（唯一写入口）  (Game.Actor)                                  │
 │   caster.TryApplyTakeover(new TakeoverSpec{                                 │
 │        TargetDef = tgt.Visual.Def,        // CharacterDef                │
 │        Enemies    = tgt.Stats.Skills,     // IReadOnlyList<ISkill>      │
 │        Duration   = def.Duration });                                     │
 │   IActor.TryApplyTakeover 内部只做三件事：                                    │
 │     ① 替换 Base 层：BaseLayer = SelfBase.AssignFromWithDefaults(         │
 │            tgtDef.Base, StatDefaults.Of(tgtDef.Base.Schema))            │
 │     ② 替换技能列表：Skills = tgt.Stats.Skills（只换列表引用）             │
 │     ③ 换外观 + 换移动：Visual.ApplySlot(VisualSlot.Body, tgtDef);         │
 │        Movement = tgtDef.DefaultMovementDriver ?? Movement              │
 ├─────────────────────────────────────────────────────────────────────────┤
 │ 步骤4  期间运行  (Game.Combat 完全不改)                                  │
 │   开火：武器读 caster.Stats.Get(Damage/DamageMul) → 已是怪物数值          │
 │   受击：DamageResolver 读 caster.Stats.Get(ArmorFlat/…) → 已是怪物数值    │
 │   伤害来源被当作"怪物阵营伤害"→ 通过 TakeoverSpec.DamageTeam 覆盖 TeamId  │
 │   事件：DamageApplied / ActorKilled 的 Source 字段仍指向同一 IActor       │
 │   ── 关键：Combat / DamageResolver / 命中判定 **一行都不改**              │
 ├─────────────────────────────────────────────────────────────────────────┤
 │ 步骤5  结束与回滚  (Game.Skill)                                          │
 │   计时到 0 → caster.TryApplyTakeover(TakeoverSpec.Revert(snapshot))         │
 │     ① Base 层 := 快照.Base（直接换回不可变引用，O(1)）                     │
 │     ② Skills   := 快照.Skills                                            │
 │     ③ Visual.ApplySlot(VisualSlot.Body, 快照.Visual)                      │
 │   期间产生的 Growth/Equipment/Buff/Temporary 层始终未被动过 → 无需还原    │
 │   发出 TakeoverEnded 事件（C# event），表现层播变身特效                   │
 └─────────────────────────────────────────────────────────────────────────┘
```

### 4.2 抽象复用点一览（**这是"不用重写"的证明**）

> 下表每一行都标注了**必要等级**：`必要` = 没有它夺舍不成立；`重要` = 没有它要写大量重复逻辑。

| 抽象 | 必要等级 | 在夺舍里被复用的方式 | 如果**没有**这个抽象，代价 |
|---|---|---|---|
| **`IActor`** | **必要** | 夺舍目标、玩家自身、被夺舍后的自身都是 `IActor`；`TryApplyTakeover` 是唯一写入口，`TryXxx` 提供失败语义（目标被占用/不可夺/无敌帧） | 要把"玩家"和"怪物"当成两个类型，赋值点需要 `is/as` + 分支，且每处用它们的地方都要分支 |
| **`IStatProvider`** | **必要** | 替换数值时只换 `Base` 层，其余层不动；读取方（Combat/UI/技能）零改动 | 每个读属性的地方都要 `if (isTakenOver) 读怪物属性 else 读玩家属性` |
| **`StatPackage` + `AssignFromWithDefaults`** | **必要** | 玩家 Base → 怪物 Base 的跨 schema 赋值，缺的取默认、多的进 Unknown，**不抛异常** | 玩家没有 `ShieldMax` 时直接崩，或需要每个怪物单独写转换函数 |
| **`IVisualDescriptor`** | **必要（夺舍成立的必要抽象）** | 换外观是"换描述符 + `ApplySlot`"，渲染层完全不知道自己在画玩家还是怪物 —— 夺舍后体型/配色/模型/枪口/子弹样式自动全换，**不碰任何 `.tscn`** | 每个 `Node3D` 里 `if (isPlayer) 加载 player.tscn else 加载 monster.tscn`；换皮还要处理骨骼/动画/挂点；每加一个可变身单位就要改渲染分支。**这是夺舍走查里最容易退化为"重写一片逻辑"的地方** |
| **`ISkill`** | **必要** | 技能列表是"一组 `ISkill`"，夺舍只是换掉这个列表的引用，玩家原技能压入快照；怪物技能无需新写系统 | 怪物技能要用自己的一整套技能类，夺舍后无法执行，或者要写桥接适配器 |
| **`ISkillTarget`** | 重要 | 目标选取与目标类型解耦，`TryResolve<IActor>` 一套代码服务所有技能 | 每个技能自己写一套目标搜索，规则不一致 |
| **`IMovementDriver`** | 重要 | 夺舍后可换成怪物的移动手感（如章鱼怪用地形漂移），也可保留玩家手感（策划可配） | 移动逻辑写死在输入处理里，无法复用怪物移动 |
| **`IPoolable`** | 重要 | 变身/回收时不产生 `new`（外观节点走池） | 变身时分配峰值导致卡顿 |
| **C# 事件（E4）** | 重要 | `TakeoverStarted/Ended`、`DamageApplied` 照常广播，吸血/成就等订阅者零改动 | 每个订阅者都要知道夺舍的存在 |
| **`StatPackage` 只读视图（05 §6.1.1 M6）** | **必要** | 快照直接持有不可变引用（零拷贝），且**物理上不可能**把怪物的包改脏 | 若包可写，夺舍后对玩家的伤害写入会顺着共享引用污染 `CharacterDef`，全场同种怪物血量一起错乱（05 §6.1.1 有完整推演） |

**结论**：`IVisualDescriptor` 不是"锦上添花"，而是本走查从「3 个文件」变成「重写渲染」的分水岭。**它已被 Lead 仲裁 #3-1 定为正式接口**（见 03 §4.1 仲裁要点 A）。

### 4.3 关键数据结构（可直接照写）

```csharp
namespace Game.Data;   // 声明位置 scripts/data/

/// 夺舍规格：进入与回滚共用同一个结构（Revert 时 Restore 非空）。
public readonly record struct TakeoverSpec
{
    public CharacterDef Def { get; init; }              // 目标怪物的静态定义
    public IReadOnlyList<ISkill> Skills { get; init; }  // 目标的技能列表（引用共享，不复制）
    public TakeoverSnapshot? Restore { get; init; }     // 非空 = 回滚模式
    public float Duration { get; init; }                // 秒；来自 Skills 表的公式行
    public TeamId? DamageTeam { get; init; }            // 夺舍期间的伤害阵营（策划可配）
    public bool ReplaceSkills { get; init; }            // v1 恒 true（玩家技能压入快照）；见 §4.6 变体
}

/// 快照：全部是不可变引用，回滚 O(1)，无需深拷贝。
/// 注意 StatPackage 的只读视图保证这些引用被共享也不会被改脏（05 §6.1.1）。
public readonly record struct TakeoverSnapshot(
    StatPackage BaseLayer,
    IReadOnlyList<ISkill> Skills,
    CharacterDef VisualDef,
    IMovementDriver Movement,
    TeamId Team);
```

```csharp
namespace Game.Actor;   // 声明位置 scripts/actor/

public sealed partial class Actor : Node3D, IActor, IStatProvider, IDamageable, ISkillCaster
{
    private StatPackage _baseLayer;          // 可变的是"槽位引用"，不是包本身
    private IReadOnlyList<ISkill> _skills = Array.Empty<ISkill>();
    private IMovementDriver _movement = NullMovementDriver.Instance;
    private TakeoverSnapshot? _takeoverSnapshot;
    private TeamId _team;

    public TeamId Team => _team;
    public IVisualDescriptor Visual { get; private set; } = NullVisualDescriptor.Instance;

    // ISkillCaster 实现（03 §4.1 第 14 行）：_skills 只能经此替换，外部不得直接赋值
    IReadOnlyList<ISkill> ISkillCaster.Skills => _skills;
    public bool CanCast(SkillId id) => /* 查冷却表 */ false;
    public bool TryCast(SkillId id, in SkillContext ctx) => /* 冷却门控 + Cast */ false;
    public void TickSkills(float dt, in SkillContext ctx) { /* 遍历 _skills 驱动 */ }
    bool ISkillCaster.TryReplaceSkills(IReadOnlyList<ISkill> skills)
    {
        if (_takeoverSnapshot is not null) return false;   // 夺舍中不得外部替换，零副作用
        _skills = skills; return true;
    }

    /// IActor 的唯一写入口（03 §4.2、Lead 仲裁 #3-3）。
    /// 契约：失败返回 false 且**不产生任何副作用**（不改层、不改技能、不改外观、不改阵营）。
    public bool TryApplyTakeover(in TakeoverSpec spec)
    {
        // ── 前置校验（全部在写入任何状态之前完成）──
        if (spec.Restore is null)                       // 进入模式
        {
            if (_takeoverSnapshot is not null) return false;   // 已在夺舍中，不支持叠加
            if (spec.Def is null || spec.Skills is null) return false;  // 参数非法
        }
        else if (_takeoverSnapshot is null)
        {
            return false;                               // 没有可回滚的快照
        }

        // ── 回滚 ──
        if (spec.Restore is { } snap)
        {
            _baseLayer = snap.BaseLayer;
            _skills = snap.Skills;
            Visual.ApplySlot(VisualSlot.Body, snap.VisualDef);
            Visual.ResetAll();                          // 其余槽位回默认
            _movement = snap.Movement;
            _team = snap.Team;
            _takeoverSnapshot = null;
        }
        // ── 进入 ──
        else
        {
            _takeoverSnapshot = new TakeoverSnapshot(
                _baseLayer, _skills, Visual.Def, _movement, _team);
            _baseLayer = _baseLayer.AssignFromWithDefaults(   // ← 规则6 跨 schema 赋值
                spec.Def.Base, StatDefinitionTable.DefaultsOf(spec.Def.Base.Schema));
            _skills = spec.Skills;                          // 玩家原技能已在快照里，可回滚
            Visual.ApplySlot(VisualSlot.Body, spec.Def);
            if (spec.Def.DefaultMovementDriver is { } mv) _movement = mv;
            if (spec.DamageTeam is { } t) _team = t;
        }

        OnLayersChanged(new StatLayerId(StatLayer.Base));    // IStatProvider 变更通知
        return true;
    }
}
```

> **注意**：`TryApplyTakeover` 是 `IActor` 上的**唯一**数值/技能/外观替换入口。任何任务想要"临时变成别的样子"都走它 —— 这是规则 7 在类型系统上的体现。
> **失败语义很重要**：`CanCast` 会调 `TryApplyTakeover` 的预检分支（或技能侧先做同样的检查），保证"点了技能但放不出来"时不会半途改了一半状态。
>
> **`ISkillCaster.TryReplaceSkills` 与 `TryApplyTakeover` 的两条通路（03 §4.1 仲裁要点 C，Lead 仲裁 #6）**：
> | 通路 | 谁调 | 语义 |
> |---|---|---|
> | `IActor.TryApplyTakeover` 内部直接改 `_skills` | `TakeoverSkill`（夺舍流程） | **夺舍专用**：与 Base 层、外观、阵营**同一事务**替换，四者必须一致 |
> | `ISkillCaster.TryReplaceSkills` | 外部模块（装备、天赋树等） | **非夺舍的技能增删**；夺舍中返回 false，避免与快照不一致 |
> 两者不可互相替代：外部模块**永远不得**用 `TryReplaceSkills` 绕过夺舍做"半套替换"（会留下 Base 层是怪物、技能是玩家的不一致态）。

### 4.4 Combat 结算为什么一行都不用改（逐条论证）

| Combat 的代码 | 夺舍后依然正确的原因 |
|---|---|
| `dmg = source.Get(StatId.Damage) * source.Get(StatId.DamageMul)` | `source` 是 `IStatProvider`，它的 `Current` 已由 `StatCalculator.Fold` 用新 Base 重算过 |
| `armor = target.Get(StatId.ArmorFlat)` | 同上 |
| `if (target.Get(StatId.Hp) <= 0) → ActorKilled` | Hp 来自怪物 Base 语义（Hp 是随层叠加的资源量，由 DamageResolver 写回层栈） |
| `IDamageModifier` 链 | Modifier 由 Excel `Modifiers` 表配置，与"是谁"无关 |
| 事件 `DamageApplied{Source, Target}` | `Source` 仍是同一个 `IActor` 句柄；订阅者（吸血/成就/连击）不需要知道夺舍 |
| 命中判定 / 子弹 | `IProjectile` 用的是 `ProjectileDef`（子弹样式）；开火时从 `IVisualDescriptor.ProjectileStyle` 取，夺舍后自动是怪物子弹 |
| 目标选取（`IsEnemy`） | 读 `IActor.Team`，夺舍期间由 `TakeoverSpec.DamageTeam` 决定 |
| 伤害写入层栈 | 写的是 `IActor` 自己的 Base 层槽位；`StatPackage` 只读视图保证不会顺着共享引用改到 `CharacterDef`（05 §6.1.1） |

**结论**：`scripts/combat/**` 的 diff **必须为 0 行**。若发现需要改 Combat，说明抽象没抽对，回到 §2 的超预算处理顺序。

### 4.5 这个功能实际要改的文件（预算核算）

| 文件 | 类型 | 说明 |
|---|---|---|
| `data/excel/*.xlsx`（`Skills` 加 1 行，`Formula` 可能加 1 行） | 数据 | E1 数据扩展 |
| `scripts/skill/TakeoverSkill.cs` | **新增 1 个** | E3 策略扩展：实现 `ISkill`（含步骤 1、2、5） |
| `scripts/data/TakeoverSpec.cs` | **新增 1 个** | 值对象（`record struct` + 快照，约 30 行） |
| `scripts/actor/Actor.cs` | 修改 | 加 `TryApplyTakeover` 实现（若 Actor 骨架已有 `IActor` 实现，这里是补实现体） |
| `scripts/data/StatDefinitionTable` 的 `DefaultsOf` | 修改（可能） | 若已有则不动 |
| **`scripts/combat/**`** | **0 行** | ✅ 验收项 |
| **`scenes/actors/*.tscn`** | **0 行** | ✅ 验收项（外观走 `IVisualDescriptor`，不需要改场景） |

**总计**：新增 2 个 `.cs` + 修改 1~2 个 `.cs` + Excel 2 行。**远低于"重写一片逻辑"**，符合规则 7。

> 若 `IVisualDescriptor` 不存在，则需在 `Actor.cs` 里实现"换子树 + 换动画"，并额外改 `scenes/actors/*.tscn` 若干，新增 ≥3 文件 —— 这正是它被定为**必要抽象**的理由。

### 4.6 变体不破（规则 5 的扩展点 3 变体检验）

| 未来变体 | 需要改什么 | 结论 |
|---|---|---|
| 夺舍后**保留玩家技能**（叠加而不是替换） | `TakeoverSpec` 加 `ReplaceSkills: bool`；`Actor.TryApplyTakeover` 加 3 行 | ✅ 不破 |
| 夺舍**永久**（直到死亡） | 由 Excel `Skills` / `Triggers` 配置驱动（见 §9.2 OQ-2 裁决） | ✅ 不破 |
| 夺舍后**原怪物消失**（寄生） | 新增 `IDamageModifier`/技能效果"移除目标"，Steps 2~3 不变 | ✅ 不破 |
| 连续夺舍（夺舍一个，再夺舍另一个） | 快照栈（`List<TakeoverSnapshot>`）而不是单槽 | ⚠️ 需改 `Actor` 1 处；v1 明确为单层，`TryApplyTakeover` 直接返回 false |
| 多人同时夺舍同一只怪 | 需要目标占用标记（`IActor.ClaimedBy`），新增字段 | ⚠️ 已登记为扩展点，v1 不做 |

### 4.7 夺舍语义（**已确认，作为实现验收条件**）

> Lead 仲裁 #3-4 确认：以下语义即用户原话「人物技能是夺舍怪物技能」的落地解释，**实现时不得偏离**。

| # | 语义要点 | 验收标志 |
|---|---|---|
| S1 | **玩家能用上怪物的技能**：技能列表随 `Base` 层一起替换 | 夺舍后按技能键释放的是怪物的 `ISkill` |
| S2 | **玩家原技能暂屏蔽**：压入 `TakeoverSnapshot.Skills`，不参与 `CanCast`/`Cast` | 夺舍期间玩家原技能不可用；快照里能找到它们 |
| S3 | **回滚完整**：结束时恢复 Base 层、技能列表、外观、移动驱动、阵营 | 夺舍前后 `Get(StatId)` 逐个相等（Growth/Equipment/Buff/Temporary 层全程未动） |
| S4 | **不叠加**：夺舍中再次 `TryApplyTakeover` 返回 false 且无副作用 | 原有夺舍状态不变 |
| S5 | **结算不变**：Combat 与伤害修正链零改动 | `scripts/combat/**` diff = 0（§4.4） |
| S6 | **外观全换**：模型/配色/体型/枪口/子弹样式 | 全程不修改任何 `.tscn`（§4.5） |

**S1 + S2 的实现就是两行**：进入分支 `_skills = spec.Skills;`，回滚分支 `_skills = snap.Skills;`。
—— 这就是"人物技能是夺舍怪物技能，无需重写大片逻辑"最直接的证据：**技能系统本身完全不知道"夺舍"这回事**。

---

## 5. 事件总线 / 钩子系统

### 5.1 分工（C# event vs Godot Signal vs Excel Triggers）

| 场景 | 用什么 | 定义位置 | 例子 |
|---|---|---|---|
| 纯逻辑、跨模块、不依赖 Node | **C# `event`** | `Game.Core` | `DamageApplied`、`ActorKilled`、`StatChanged`、`TakeoverStarted` |
| 需要连到场景树 / 检查器连线 / 编辑器可见 | **Godot `Signal`** | `Node` 派生类 | `Actor.hp_changed`、`UI.button_clicked` |
| 需要策划配置"何时触发" | **Excel `Triggers` 表** | `data/excel/` | 技能触发条件、词条触发、掉落规则 |

**铁律**：逻辑事件**不经过 Node**。Node 只在表现层做信号中转（`Actor` 收到 C# event 后 `EmitSignal` 给场景内子节点）。

### 5.2 事件命名规范

| 要素 | 规定 |
|---|---|
| 事件名 | 过去式（已发生）：`DamageApplied`、`ActorKilled`、`TakeoverStarted` |
| 载荷 | `readonly record struct`，字段全 `init`，禁止 `object` |
| 定义处 | `Game.Core/Events/*.cs`，每个事件一个文件 |
| 订阅代际 | `EventBus.Subscribe<T>(handler, generation)`；场景切换时 `++generation` 一次性失效所有旧订阅，防泄漏 |
| 线程 | 只在主线程派发；不允许在物理回调里派发（延迟到帧末队列） |

```csharp
namespace Game.Core.Events;

public readonly record struct DamageApplied(IActorView Source, IActorView Target,
                                            float Amount, bool IsCrit, DamageType Type);

public readonly record struct ActorKilled(IActorView Victim, IActorView? Killer);
```

> `IActorView` 是 `Game.Core` 里的只读句柄接口，避免 `Game.Core` 依赖 `Game.Actor`（见 `ARCHITECTURE.md` §2.3 矩阵）。**这正是"把接口下沉到能被依赖的层"的实例**。

### 5.3 事件必须可被数据配置

Excel `Triggers` 表字段：

| 字段名 | 类型 | 中文显示名 | 备注 |
|---|---|---|---|
| `Id` | `string` | 编号 | 小写下划线 |
| `EventName` | `enum:GameEvent` | 触发事件 | 必须与 C# 事件注册表一致（构建期校验） |
| `Condition` | `text` | 触发条件 | `Formula` 表引用的表达式，读事件载荷 |
| `ActionKind` | `enum:ActionKind` | 动作类型 | `ApplyBuff` / `Heal` / `SpawnFx` / `RunSkill` / `ModifyStat` |
| `ActionParams` | `text` | 动作参数 | `key=value` 分号分隔 |
| `Cooldown` | `float` | 冷却 | 秒，0=不限 |
| `MaxTriggers` | `int` | 最大触发次数 | -1=无限 |
| `Priority` | `int` | 优先级 | 同一事件多个触发器按此排序 |

> 这样"吸血词条"= `Triggers` 加一行（`EventName=DamageApplied; Condition=isCrit; ActionKind=Heal; ActionParams=ratio=0.15`），**0 代码**。只有 `ActionKind` 的**新动作类型**才需要 1 个实现类（E3）。
>
> ⚠️ **前置依赖**：`Triggers` 表**当前未落地**（Lead 仲裁 #3-5，见 §9.1）。在它落地之前，E4 类扩展**不能**享受"0 代码"预算 —— 必须如实写"当前 1 个 C# 订阅文件"。任何人引用本节表格前请先看 §9.1。

### 5.4 钩子（Hook）的放置原则

| 允许放钩子的位置 | 禁止 |
|---|---|
| `DamageResolver` 的 `DamagePhase` 三个相位 | 在技能里 `if (词条A) ...` |
| `IProjectile.OnSpawn/OnHit/OnExpire` | 在子弹类里写死特殊效果 |
| `ISkill` 的 `OnCastStart/OnTick/OnCastEnd` | 在 `CombatTimer` 里写技能特例 |
| `Actor.TryApplyTakeover` 前后（E4 订阅 `TakeoverStarted/Ended`） | 在 `TryApplyTakeover` 里 `if (是夺舍Boss) ...` |

---

## 6. 版本兼容与弃用

### 6.1 接口演进

| 做法 | 规则 | 示例 |
|---|---|---|
| **加方法** | 用 `default interface method`，给安全默认实现 | `interface ISkill { void OnCastEnd() { } }` |
| **加参数** | 加一个**新重载**，旧签名标 `[Obsolete]` 转发 | `Cast(ctx)` → `Cast(in SkillContext ctx)` |
| **改语义** | 新接口名（如 `IDamageable2` 不允许；改为 `IDamageApplier`），旧接口弃用 | — |
| **删方法** | 至少保留一个大版本，标 `[Obsolete("改用 Xxx，见 docs/rules/06 §6", error: false)]` | — |
| **改枚举值** | `StatId` 只加不改（`05` §5.1）；其它枚举加成员也要显式数值 | — |

### 6.2 数据 schema 版本

| 位置 | 字段 | 规则 |
|---|---|---|
| 生成物 `.tres` | `schema_version` | 每次生成写入；C# 侧 `CurrentSchemaVersion` 比对，不符启动即报错 |
| 索引 JSON | `schema_version`（表级） | 与 `.tres` 必须一致 |
| 存档 | `schemaVersion` | 不匹配拒绝载入（`ARCHITECTURE.md` §4.6） |

**什么时候升版本**：新增字段（且给定默认）；字段语义变化；字段类型变化。**什么时候不升**：只改数值、只加数据行、只加一列可选且默认的字段（可原地兼容）。

### 6.3 弃用登记

弃用必须三处同步：

1. 代码 `[Obsolete("...，见 docs/rules/06 §6")]`
2. `ARCHITECTURE.md` §3.2 接口表「已落地」列标 `⚠️ 已弃用（替代：Xxx）`
3. Excel `StatDefs.Deprecated = TRUE`（数值类）或表内加 `Deprecated` 列（数据类）

---

## 7. 「什么时候该重构」判据

| 判据 | 触发条件 | 动作 |
|---|---|---|
| **三次法则** | 同一段相似代码出现 **3 次** | 抽成公共件进 `Game.Core`（规则 5），并登记 ARCHITECTURE |
| **扩展点复用度** | 一个扩展点（接口/字段）被 **≥3 个** 功能使用，且各自写法不一致 | 把该扩展点提升为一等抽象（新接口 + 数据列），迁移旧用法 |
| **分支蔓延** | 同一个 `if/switch` 在 ≥2 个文件里按类型分支 | 立刻转成策略对象（03 A3） |
| **超预算回收** | §2 的预算被连续违反 ≥2 次 | 停止加功能，先做抽象整改任务 |
| **文件规模** | 单 `.cs` > 400 行 或 > 8 个职责 | 拆分（03 A1） |
| **测试失守** | 某模块无法单测（需要起场景） | 把逻辑从 Node 里剥出来（03 A6） |
| **数据绕行** | 有人在代码里写数值来"绕开 Excel" | 视为红线（04 GS005），不是重构是修复 |

**禁止的"重构"**：无需求驱动的纯风格重写；为对称而抽象；把 3 行代码包成 3 层接口。

---

## 8. 扩展性登记表（每任务收工必填）

> 放在 `ARCHITECTURE.md` §3 末尾或本文件附录。新写文件时必须在此说明"为什么不能用 E1/E2 解决"。

| 日期 | 任务 | 新增的扩展点 | 类别 | 新增文件数 | 为什么 E1/E2 不够 | 未来变体 |
|---|---|---|---|---|---|---|
| 2026-11-03 | 立规矩 | 本文档建立预算表 | — | 0 | — | — |
| （待第一个任务填写） | | | | | | |

---

## 9. 前置依赖清单与开放问题

### 9.1 【必须知道的前提】前置依赖清单（Lead 仲裁 #3-5 升级）

> **本节的用途**：本套规则里有几条"扩展预算是 0 代码文件"的承诺，它们**依赖某些前置件先落地**。在前置件未落地之前，对应扩展点**只能做到"改 Excel 加行"的程度，连 Excel 都没有对应 Sheet**。
> **任何人引用本文的预算数字之前，先对照本表确认前置件是否已落地**，否则会把"将来能做到 0 文件"误读成"现在已经能做到"。

| 扩展点 / 能力 | 承诺的预算 | **前置依赖** | 前置状态（截至 2026-11-03） | 未落地时的真实情况 |
|---|---|---|---|---|
| **E4 事件/钩子扩展**（吸血、护盾、击杀连锁、成就） | **0 代码文件** | **`Triggers` 表**（`data/excel/` 内，字段见 §5.3）+ 事件注册表 + `ActionKind` 实现 | ❌ **未落地（Lead 仲裁 #3-5：不在本批落地）** | **E4 目前不可用**。任何"事件驱动的新功能"都只能写 C# 订阅代码（约 1 文件），**不能**声称"0 代码"。 |
| E1 数据扩展（新怪物/武器/子弹样式/特效） | 0 代码文件 | Excel 四表 + `tools/data_pipeline/` 生成器 + `*Def` 资源类 | ❌ 未落地（ARCHITECTURE §4.8 P0） | 只能手写 `.cs` 定义 |
| E2 组合扩展（新技能=已有零件组合） | 0 代码文件 | `Skills` 表 + `ISkill` 实现族 + `Formula` 表 | ❌ 未落地 | — |
| E3 策略扩展（新技能种类/新伤害修正） | 1 代码文件 | 对应接口（`ISkill`、`IDamageModifier`）已登记但与实现分离声明 | ✅ 接口已定义（03 §4.1）；实现未落地 | 可写实现，但需先落接口 |
| **夺舍走查（§4）** | 2 新 + 1~2 改 | `IActor` / `IStatProvider` / `IVisualDescriptor` / `ISkill` / `StatPackage` **全部要有实现** | ⚠️ 全部为设计态，0 实现 | 走查是**验收清单**，不是"现在就能跑"的说明 |
| E5 原生扩展 | 1~2 文件 | `native/` + godot-cpp 4.7（ARCHITECTURE §10 R2/R3） | ❌ 未落地 | — |

**使用纪律**

1. 引用 §2 预算前，先读本表确认前置件状态；前置件 ❌ 时，如实写"当前实际 N 文件，前置落地后 0 文件"。
2. 前置件落地后，**必须回来更新本表的状态列**并把日期改成落地日。
3. **禁止**用"0 代码文件"的承诺去驳回一个合理的新文件需求 —— 先看前置在不在。

### 9.2 开放问题

| # | 问题 | 结论 / 我的建议 | 影响 |
|---|---|---|---|
| ~~OQ-1~~ | ~~`TryApplyTakeover` 是否属于 `IActor`，还是独立 `ITakeoverHost`~~ | **已裁定（Lead 仲裁 #3-3）**：放 `IActor`，签名 `bool TryApplyTakeover(in TakeoverSpec spec)`，`TryXxx` 提供失败语义 | 已关闭 |
| ~~OQ-4~~ | ~~`IVisualDescriptor` 必须转正~~ | **已裁定（Lead 仲裁 #3-1）**：转正为正式接口，并在 §4.2 标注为「夺舍成立的必要抽象」 | 已关闭 |
| ~~OQ-5~~ | ~~`Triggers` 表 v1 是否落地~~ | **已裁定（Lead 仲裁 #3-5）**：本批不落地。依赖关系已升级为 §9.1 前置依赖清单 | 已关闭 |
| ~~OQ-2~~ | ~~夺舍"永久"（Duration=∞）的语义：用哨兵值 `-1` 还是新字段 `IsPermanent`~~ | **已裁定（Lead 仲裁 #5）**：v1 由 Excel `Skills` / `Triggers` 配置驱动，**代码不提供永久/限时的硬分支**；默认读配置，缺省为限时 | 已关闭 |
| ~~OQ-3~~ | ~~夺舍期间的"伤害阵营"覆盖（`DamageTeam`）是否 v1 就要~~ | **已裁定（Lead 仲裁 #5）：v1 就要**。夺舍后「玩家打玩家」「玩家打自己的原队友」是必然出现的问题，团队归属必须从第一天就是**数据字段**而不是代码分支（呼应规则 4） | 已关闭 |
| ~~OQ-6~~ | ~~夺舍是否允许"叠加夺舍"（夺舍中再夺舍）~~ | **已裁定（Lead 仲裁 #5）：v1 不支持**。`TryApplyTakeover` 在夺舍中直接返回 false、零副作用；连夺涉及快照栈结算顺序与 Buff 归属，留作 v2 扩展点（届时改 `List<TakeoverSnapshot>` 即可，约 30 行，不会造成大规模重写） | 已关闭 |
| ~~OQ-7~~ | ~~E4 前置件（`Triggers`）的落地排期~~ | **已裁定（Lead 仲裁 #5）：本批不落地**，保持 §9.1 前置依赖清单现状 | 已关闭 |
| ~~OQ-8~~ | ~~`ARCHITECTURE.md` §3.2 的 `ApplySlot` 是否回写为强类型~~ | **已裁定（Lead 仲裁 #5）+ 已回写（仲裁 #6）**：以强类型 `ApplySlot(VisualSlot, CharacterDef)` 为准，ARCH §3.2 已归一，全项目唯一口径 | 已关闭 |
| ~~OQ-9~~ | 夺舍时换技能列表走 `IStatProvider.Skills` 还是 `ISkillCaster` | **已裁定（Lead 仲裁 #6）**：`ISkillCaster.TryReplaceSkills` 是唯一通路（内部转调 `IActor.TryApplyTakeover`）；`IStatProvider.Skills` 仅只读视图。见 03 §4.1 仲裁要点 C | 已关闭 |

> **v1 开放问题已全部关闭**（OQ-1 ~ OQ-9）。后续若出现新问题，从 **OQ-10** 起编号，不得复用旧号。

---

## 10. 扩展性审查 10 问（PR 自检，逐项勾选）

- [ ] **Q1** 我在 PR 描述里写了「新增一个 X 要改几个文件」，且落在 §2 预算内？
- [ ] **Q2** 我的改动**没有**修改 `scripts/combat/**`（除明确是战斗任务）？
- [ ] **Q3** 我没有新增按具体类型分支的 `if`/`switch`（新增的每个分支都对应一个策略对象）？
- [ ] **Q4** 新增内容优先用了 E1（Excel 加行）/ E2（资源组合），并且我能解释为什么没做到 0 代码文件？
- [ ] **Q5** 我新增的扩展点已登记到 `ARCHITECTURE.md` §3，并填了 §8 扩展性登记表？
- [ ] **Q6** 我给新接口的方法留了 `default` 实现（或给出了必须全实现的理由）？
- [ ] **Q7** 我改动涉及 schema 时同步升了版本号，并写清了默认值？
- [ ] **Q8** 我检查了 03 §4.4 的三个扩展点变体，今天的设计**都不会被它们打破**（或已写明"明确不做"）？
- [ ] **Q9** 我没有为了省事复制粘贴（三次法则已触发时是否抽了公共件）？
- [ ] **Q10** 若我引入了新事件/钩子，它是否已登记且可被 Excel `Triggers` 表配置？

---

## 11. 反例

| # | 反例 | 为什么违反规则 7 | 正确做法 |
|---|---|---|---|
| X1 | 为"夺舍"新写一套 `TakeoverCombat` 结算 | 结算逻辑第二份，未来每加一条伤害规则都要改两处 | 复用 `DamageResolver`，只换 `IStatProvider` |
| X2 | 在 `Bullet.cs` 里 `if (bulletType == Piercing) ...` | 新子弹样式要改代码（超预算） | `ImpactBehavior` 枚举 → 策略对象 |
| X3 | 玩家类 `PlayerActor` 与 `MonsterActor` 互不相通 | 夺舍需要类型转换，规则 6 失效 | 同一 `Actor` + `StatPackage` 层 |
| X4 | 把 `IVisualDescriptor` 省掉，靠 `if (isPlayer)` 换模型 | 每加一个可变身单位就要改渲染分支 | 外观走描述符 |
| X5 | 新技能 = 复制 `FireballSkill.cs` 改 3 个数值 | 违反三次法则；数值也不该在代码里（规则 4） | Excel 加行（0 文件） |
| X6 | 事件用字符串 + `object` 载荷 | 无法静态校验，重构即炸 | 泛型 + `record struct` |
| X7 | 为将来可能的需求先写 5 个空接口 | 伪抽象，增加阅读成本 | 三变体写明落点即可，不预支 |
| X8 | 用 `InternalsVisibleTo` / 全局单例绕过分层 | 破坏依赖矩阵，扩展时无法定位影响面 | 接口下沉到 `Game.Core` |
| X9 | 在 `TryApplyTakeover` 里 `if (def.Kind == Boss) ...` | 夺舍 Boss 时要改代码，且下一种怪要再加分支 | 差异进 `CharacterDef` 字段（数据） |
| X10 | 技能释放时序用 `await Task.Delay` | 无法暂停/回放/倍速 | `ISkill.Tick(dt)` 阶段机 |

> 注：X2 的正确写法是 `ImpactBehavior` 枚举 + `IImpactStrategy` 实现；`Projectiles` 表的 `ImpactBehavior` 列按名解析到实现（构建期校验实现存在）。

---

## 12. 与其它规则的交叉

| 规则 | 交叉点 | 本文档负责 | 对方负责 |
|---|---|---|---|
| 规则 4 · 数据驱动 | `04-data-driven.md` | E1/E2 扩展的实现路径（加行怎么做） | Excel 表格式、校验、生成物 |
| 规则 5 · 抽象优先 | `03-abstract-first.md` | §2 预算数字是抽象卡的"扩展点"判据 | 接口清单、反模式、抽象审查 10 问 |
| 规则 6 · 数值包 | `05-numeric-package.md` | 夺舍对 `StatPackage` 的用法（§4 走查） | 层模型、赋值语义、未知属性 |
| 规则 2 · 技术选型 | `02-tech-selection.md` | E5 原生扩展的落点 | 何时该下沉 C++ 的判据 |
| 规则 3 · 架构大纲 | `docs/architecture/ARCHITECTURE.md` | §8 登记表、§3 接口表更新 | 架构总览、守卫编号 |
