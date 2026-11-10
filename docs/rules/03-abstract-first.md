> 状态: v1  最后更新: 2026-11-03

# 规则 5 · 抽象优先（先想抽象类，再动手）

## 1. 规则原文

> 「所有任务，必须先思考抽象类！而不是临时搭建，任务与任务之间的代码无法复用！」

本文档把这句口号转成**可执行的门禁**：任务在写第一行业务代码之前，必须先产出一张「任务抽象卡」；实现期只允许在卡片声明的边界内写代码；任务收尾必须回写 `docs/architecture/ARCHITECTURE.md` 的接口表与映射表。

**适用对象**：人和 AI 一视同仁。AI 在本仓库开工的默认行为就是先出抽象卡，再出代码。

---

## 2. 为什么（根因分析）

### 2.1 「任务与任务之间的代码无法复用」的五个根因

| # | 根因 | 典型症状 | 后果 |
|---|---|---|---|
| R1 | 以**界面/场景**为起点组织代码（"先搭个能跑的怪物场景"） | 逻辑写在具体 Node 的 `_Ready`/`_PhysicsProcess` 里 | 第二个任务想复用，只能复制整个场景再剪改 |
| R2 | **变化点没有命名**：变化藏在 `if` 分支里 | `if (actorType == ActorType.Elite)` 不断加长 | 新增变体 = 改老代码，且要回归所有老分支 |
| R3 | **数据与行为耦合**在同一个类型里 | 一个类里既有 50 个数值字段又有一堆行为方法 | 数值要配置化（规则 4）时无处下手，只能为数值再造一层 |
| R4 | 先写**具体实现**，事后"回头抽接口" | 抽接口时发现签名被实现细节绑死 | 抽象不成立，或抽出的是"伪抽象"（只被一个实现使用且带实现细节） |
| R5 | 复用件**没有公共归属地** | 每次都临时写道具箱，下一次又写一遍 | 公共能力散落各处，AI 也不知道去哪找 |

### 2.2 为什么本项目特别在意

- **肉鸽**的可玩性来自"组合爆炸"（词条 × 武器 × 技能 × 怪物）。组合不可能靠继承枚举，只能靠组合 + 接口。
- **AI 协作**：AI 靠上下文推断"应该改哪里"。有抽象卡就有唯一正确落点；没有抽象卡，AI 就会新写一套（这正是规则 5 想杜绝的）。
- **规则 7 是规则 5 的下游**：没有抽象，`06-extensibility.md` 里的「新增一个技能 = 0 个文件」就不可能成立。

---

## 3. 怎么做

### 3.1 强制流程：任务开工先出「三件套」

任何任务（含 Issue、AI 的任务提示词）在开工前必须回答下面三件套，并写进**任务描述**（PR 描述 / `docs/prompts/task-prompt-template.md` 的对应小节）。没有三件套 = 任务未开工。

#### ① 边界（Boundary）

| 问题 | 必答内容 |
|---|---|
| 谁拥有这份数据？ | 明确 owner 类型（如 `CharacterDef` 拥有静态数值，`StatPackage` 拥有运行时数值） |
| 谁是调用方？ | 上游调用方清单（宁可多列） |
| 谁是数据的写入方？ | 一次性写死？每帧写？只由 Combat 管线写？ |
| 生命周期 | 创建点 / 销毁点 / 是否跨关卡存活 |
| 线程/时序假设 | 是否跨帧、是否可在物理回调里改 |

#### ② 抽象（Abstraction）

必须同时给出：

1. **接口签名**（C# `interface`，方法级，含空实现语义）。
2. **数据载体**（`sealed record struct` / `record` / `Resource` 子类），说明是值语义还是引用语义。
3. **它属于哪个命名空间**（见 3.3 的命名空间表）。
4. **它是否已在 `ARCHITECTURE.md` 登记**；若没有，本任务负责登记。

#### ③ 扩展点（Extension Points）

写出**未来最可能的 3 个变体**，并对每个变体标注"今天的写法是否会被它打破"：

| 变体假设 | 如果明天要做，需要改几个文件？ | 结论 |
|---|---|---|
| 例：新增"吸蓝盾"词条 | 0（纯数据行） | ✅ 已被组合扩展覆盖 |
| 例：新增"持续性伤害"技能种类 | 1（实现 `ISkill`） | ✅ 已被策略扩展覆盖 |
| 例：子弹要支持"反弹" | 改 `ProjectileDef` + 改渲染 | ⚠️ 需在抽象卡中写明是"预留字段"还是"本轮明确不做" |

> 关键纪律：**扩展点可以是"现在不做"**，但必须写清"如果做，落点在哪"。未命名的扩展点在规则 7 里会被判为返工风险。

### 3.2 抽象层次规范（怎么写抽象）

#### 3.2.1 选型矩阵

| 场景 | 首选 | 次选 | 禁止 |
|---|---|---|---|
| 跨模块协作、需要 mock 测试 | `interface` | — | `abstract class` |
| 纯数据载体（数值、伤害包、配置快照） | `sealed record struct`（≤ 约 6 字段、无引用）或 `sealed record` | `readonly struct` | 可变 `class` + public setter |
| 运行时可变状态 + 行为 | `sealed class` 实现若干接口 | `class` + Composition | 深继承 |
| Godot 资源（Excel 生成物、可视化编辑） | `[GlobalClass] partial class XDef : Resource` | — | 把 `Resource` 当纯逻辑容器 |
| 需要在编辑器里做 `@tool` 预览 | `[Tool]` + 上述 `Resource` | `EditorInspectorPlugin` | 在 `Resource` 里跑游戏逻辑 |
| 引擎热点路径 | C++ GDExtension 类 + C# 薄封装 | — | 在 C# 每帧回调里做重计算 |

#### 3.2.2 interface 优先于 abstract class 的四条理由

1. **C# 单继承**：一旦占据唯一基类槽位，后续任何"另一个维度"的能力都塞不进去（典型的 `ActorBase` 想同时继承 `DamageableBase` 和 `SkillCasterBase` 会失败）。
2. **肉鸽是组合不是分层**：一个对象同时是"可被夺舍的"、"可被减速的"、"可被标记的"，这些是正交能力，天然是接口。
3. **可测试**：接口让战斗逻辑用假实现跑单测，不依赖 Godot 场景树。
4. **版本兼容**：C# 支持 `default interface method`，接口可无痛加方法；抽象类加抽象方法会打断所有派生类（详见 `06-extensibility.md` §6）。

> 例外：确实需要**共享实现**且**只有一个维度**的（如所有 `Resource` 派生的配置类共同基类 `DefBase`），允许抽象类。`DefBase` 是唯一保留的抽象类族。

#### 3.2.3 行为用 Composition 不用深继承

```csharp
// 好：能力由接口组合出来，Actor 只做装配
public sealed partial class Actor : Node3D, IActor, IDamageable, IStatProvider, ISkillCaster
{
    private readonly List<ISkill> _skills = new();
    private readonly List<IDamageModifier> _modifiers = new();
    private IMovementDriver _movement = NullMovementDriver.Instance; // 空对象模式，永不为 null
}
```

> `ISkillCaster` 的正式签名见 §4.1 接口全集表（第 15 行，`Game.Data` 声明 / `Game.Skill` 实现），此处仅为装配示例。

```csharp
// 坏：靠继承堆能力，第 4 层就崩
class Entity {}
class LivingEntity : Entity {}
class DamageableEntity : LivingEntity {}
class SkillCastingEntity : DamageableEntity {}      // 已经是第 4 层
class EliteDamageableSkillCastingEntity : SkillCastingEntity {}  // 第 5 层，判违规
```

**继承深度红线**：`Entity` 类族最多 **2 层**（`Node3D` 派生 + 一个项目基类）。超过即判违规，见 §6 反模式 A2。

### 3.3 命名空间与所有权边界（C# 侧）

| 命名空间 | 目录 | 职责 | 允许依赖 | 禁止依赖 |
|---|---|---|---|---|
| `Game.Core` | `scripts/core/` | 纯 C# 无引擎逻辑：数学、随机、事件总线、池化、扩展方法 | 仅 .NET BCL | ❌ `using Godot`、❌ 任何其它 `Game.*` |
| `Game.Data` | `scripts/data/` | 数据契约：`*Def` 资源类、`StatPackage`、`Schema` 校验、生成物加载 | `Game.Core`、Godot.Api（`Resource`） | ❌ `Game.Combat`、❌ `Game.Actor`、❌ `Game.UI` |
| `Game.Combat` | `scripts/combat/` | 伤害结算、`IDamageable`/`IDamageModifier`、命中判定 | `Game.Core`、`Game.Data`、`Game.Native`（唯一允许） | ❌ 持有 `Node` 引用、❌ `Game.UI` |
| `Game.Actor` | `scripts/actor/` | 角色/怪物装配、`IActor` 实现、`IMovementDriver`、`IVisualDescriptor` 实现 | `Game.Core`、`Game.Data`、`Game.Combat` | ❌ `Game.UI` |
| `Game.Skill` | `scripts/skill/` | `ISkill` 实现族、效果管线、目标选取 | `Game.Core`、`Game.Data`、`Game.Combat`、`Game.Actor` | ❌ 直接操作渲染节点 |
| `Game.Spawn` | `scripts/spawn/` | 刷怪、波次、房间/关卡编排（肉鸽流程） | `Game.Core`、`Game.Data`、`Game.Combat`、`Game.Actor`、`Game.Skill` | ❌ 硬编码出怪表 |
| `Game.UI` | `scripts/ui/` （**唯一允许 `.gd`**） | 视图层：HUD/菜单/飘字；只读视图接口 + 发事件 | `Game.Core`（只读接口）、`Game.Data`（只读 DTO） | ❌ `Game.Combat`/`Game.Actor`/`Game.Skill` 的结算、❌ `Native.*` |
| `Game.Native` | `native/` | GDExtension 的 C# 薄封装（类型安全门面） | — | ❌ 业务逻辑 |

**强约束**

1. `Game.Core` 永远可以在无引擎环境下被单测（不得 `using Godot`）。
2. `Game.Data` 只允许出现引擎的值类型与 `Resource`，不允许出现 `Node`/`Node3D`。
3. 目录名与命名空间严格一一对应（GS007）：`scripts/core/` ⇔ `Game.Core`，以此类推。
4. 依赖方向见 `ARCHITECTURE.md` §2.3 的允许/禁止矩阵，本节是它的摘要，冲突以 ARCHITECTURE 为准（且必须回写修掉冲突）。

---

### 3.4 ★硬性规则：概念的**唯一契约**（违反即打回）

> **立此节的原因（真实事故，2026-11-09）**
> 用户加完模型后质问：「只有人物有模型，其他的连这个字段都没有！这在项目初期必须掐死！」
> 复盘结论不是"少了个字段"，而是 **同一个概念有多份互不相认的写法**：
> `View` / `ViewAppearance` 组件一直都在、设计也正确，但**没有任何代码往里写过外观**——
> 只有人物那一条路接了线。武器、子弹、怪物、特效全都是"组件在场、无人问津"。
> 数据表每行都填了 `VisualPack`、构建期还校验了磁盘文件存在，**校验全过、运行期零读取**。
>
> 这类缺陷的共同特征：**每一处局部都"看起来对"，但整体是断的，而且编译、测试、架构守卫全都不报错。**
> 所以它必须变成机器能查的硬约束，而不是写在文档里的倡议。

#### 3.4.1 四条硬规则

| # | 规则 | 一句话判据（PR 自检就问这句） | 违规长相 |
|---|---|---|---|
| **A** | **单一契约**：一个概念只有**一份**组件集合 / 一个写入入口 | 「这个概念有几个地方在造？」答案必须是 **1** | 4 套子弹组件、2 条开火管线并存 |
| **B** | **齐备原则**：凡同一类产物，**必带同一套组件**，哪怕某字段暂时填"空" | 「新加一种{子弹/怪物/特效}，它带的东西和老的**一模一样**吗？」 | 子弹有 `View` 没 `ViewAppearance`；特效两样都没有 |
| **C** | **消费闭合**：数据表里**每一列都必须有读取方**，否则删列或删数据 | 「这列谁在读？」答不出 → 它是死的 | `VisualPack` 填满 26 行，`scripts/` 里 0 处读取 |
| **D** | **成员闭合**：枚举 / 分支里的**每个成员都必须有实现** | 「`switch` 里这几支谁处理？」 | `WeaponAimKind` 11 个成员、`ProjectileHitPolicy` 8 个成员，**0 个消费者** |

> **A 与 B 的关系**：A 管「别造第二套」，B 管「这一套要造全」。
> 前者防分裂，后者防漏配——**两者都会造成"抽象没贯彻到底"，且互不覆盖**。

#### 3.4.2 不许用「空值」表达「未配置」

未配置必须是一个**显式取值**，且**不能与任何合法值相撞**。

```csharp
// ❌ 错：0 是合法的外观槽位号，"忘了配"和"配的是第 0 号外观"在运行期无法区分
public int VisualSlot { get; }   // 默认 0

// ✅ 对：-1 = 未解析，与 NoCluster / NoneSlot 等既有哨兵保持一致
public const int UnresolvedVisualSlot = -1;
public int VisualSlot { get; }   // 默认 -1
```

**判据**：写出默认值前先问「这个值和某个真实取值相等吗？」相等就换个哨兵。
「有意为空」和「忘了填」必须**能被断言区分**，否则排查只能靠肉眼。

#### 3.4.3 唯一入口原则

同一件事**只允许有一个写入方**。所有构造路径必须收敛到同一个函数，由**它**统一填充该概念的全部必需组件。

```csharp
// scripts/combat/Factory/ContentSpawner.cs —— 子弹 / 人物 / 特效的**唯一**造实体入口
private void FillAppearanceBySlot(Entity entity, int objectSlot)
{
    _world.Commands.Set(entity, new View { Slot = 0, Kind = 0, Dirty = true });
    ObjectArchetype? archetype = _index.GetObject(objectSlot);
    int visualSlot = archetype is null ? ContentIndex.NoneSlot : archetype.VisualSlot;
    //                                     ↑ 两种"未解析"用同一个 -1，不会各说各话
    _world.Commands.Set(entity, new ViewAppearance { /* ... */ });
}
```

**检验方法（可操作）**：`grep` 该组件的写入点。
```powershell
grep -rn "Commands.Set(entity, new ViewAppearance" scripts/     # 期望：只有 1 处
```
出现第 2 处 → 要么合并，要么在 PR 里写明为什么必须两份。

#### 3.4.4 落地方式：规则必须**机器可查**

文档约束会被忘掉，测试不会。每条硬规则都要配一个能失败的检查：

| 规则 | 落地手段 | 失败时长什么样 |
|---|---|---|
| A 单一契约 | `grep` 写入点数量 + PR 自检 | 同一组件出现 2+ 构造入口 |
| B 齐备原则 | **工厂层用例**（`tests/Content/Factory/AppearanceCoverageTests.cs`）：造出每种产物，断言组件齐 | 新产物漏带组件 → 用例红 |
| C 消费闭合 | 数据列扫描守卫（见 §3.4.5） | 加了列没人读 |
| D 成员闭合 | 枚举成员消费者扫描（见 §3.4.5） | 枚举加了分支没实现 |

> **B 的守卫点必须放在工厂层，不是渲染层。**
> 渲染层（`ActorViewDirector`）本来就是通用的——组件里有槽位它就能画；
> 漏的永远是"谁往组件里写字"。所以断言要钉在**造实体的那一端**。

#### 3.4.5 新增数据列 / 枚举成员时的强制动作

1. **加列**：同一 PR 内必须补齐 **(a)** 生成器、**(b)** 校验器、**(c) 运行期读取方**、**(d)** 至少一个用例。
   四者缺一 → 该列不许进库。**允许"先加列后接线"，但必须同时加一条 `xfail` 用例**把缺口钉住，
   而不是留一句 TODO（TODO 不会让任何人未来某天想起它）。
2. **加枚举成员**：同一 PR 内必须补齐**消费分支**，或显式落到 `default:` 并**抛异常说明"尚未实现"**。
   **静默 `default: return false;` 是禁止的**——它会把"未实现"伪装成"业务上不该发生"。
3. **改默认值**：必须 `grep` 全部消费点，确认新默认值不会与合法值相撞（§3.4.2）。

#### 3.4.6 本节与其它规则的关系

- 规则 7（可扩展性，「新增一种 X 改几个文件 ≤ 2」）：**本节的 B 条是它的前提**。
  组件不齐时，"新增一种子弹"必然要去每个消费点单独改——可扩展性无从谈起。
- 规则 4（数据驱动）：**C 条是它的下限**。没有消费方的数据列不是"数据驱动"，是"数据装点"。
- 规则 3（DoD）：本节的对应用例与守卫不通过 = 任务未完成。

---

## 4. 代码 / 表样例

### 4.1 通用接口清单 v1（**全集**，其它文档只引用不新增）

> 这张表是**唯一来源**。任何人需要新接口，先在此处登记 + 在 `ARCHITECTURE.md` 接口表里登记，再写代码。
> **两列位置必须分开看**（Lead 仲裁 #3-2 引入）：
> - **声明位置**：`interface` 本体所在的命名空间与目录 —— 只放"纯契约"（方法签名 + 值类型），不放实现。
> - **实现位置**：具体实现类所在的命名空间与目录 —— 由消费该能力的层持有。
> 声明与实现分居两层是本项目的解耦手段：下游只依赖"声明"，上游实现可随时替换。
> 落地顺序建议：`Combat` 三件套 → `IConfigSource` + `IStatProvider` → `IActor` → `ISkill` 族（见 `ARCHITECTURE.md` §3.5）。

| 接口 | 声明位置（namespace / 目录） | 实现位置（namespace / 目录） | 职责一句话 | 方法签名（v1） | 空实现（Null Object） | 首个使用方 |
|---|---|---|---|---|---|---|
| `IDamageable` | `Game.Combat` / `scripts/combat/` | `Game.Actor` / `scripts/actor/` | 接受伤害 | `bool TryApplyDamage(in DamageEvent dmg)` | `NullDamageable` | Actor / Projectile 命中 |
| `IAttacker` | `Game.Combat` / `scripts/combat/` | `Game.Actor` / `scripts/actor/` | 发起伤害（通常与 `IDamageable` 不同主体） | `DamageEvent BuildDamage(IStatProvider src, in DamageSpec spec)` | `NullAttacker` | 武器 |
| `IDamageModifier` | `Game.Combat` / `scripts/combat/` | `Game.Combat`、`Game.Skill` / `scripts/combat/`、`scripts/skill/` | 在结算管线中改伤害 | `void Modify(ref DamageEvent dmg, DamagePhase phase)` | 不参与（集合为空即可） | 词条 / 护甲 / 暴击 |
| `IActor` | `Game.Actor` / `scripts/actor/` | `Game.Actor` / `scripts/actor/` | 场景里的"一个可被瞄准的东西"，同时是**夺舍的唯一写入口** | `IActor Handle { get; }`<br>`IStatProvider Stats { get; }`<br>`IVisualDescriptor Visual { get; }`<br>`TeamId Team { get; }`<br>`bool IsAlive { get; }`<br>`bool TryApplyTakeover(in TakeoverSpec spec)` | `NullActor` | 准星 / 目标选取 / 夺舍 |
| `ISkill` | **`Game.Data` / `scripts/data/`** | `Game.Skill` / `scripts/skill/` | 一段可释放的技能逻辑（纯数据契约：`Id` / `Cooldown` / `TargetRule` / `EffectRef`） | `SkillId Id { get; }`<br>`bool CanCast(in SkillContext ctx)`<br>`SkillCastResult Cast(in SkillContext ctx)`<br>`void Tick(float dt, in SkillContext ctx)` | `NullSkill` | 玩家 / 怪物 AI / 夺舍 |
| `ISkillTarget` | **`Game.Data` / `scripts/data/`** | `Game.Skill` / `scripts/skill/` | 技能作用对象句柄 | `SkillTargetKind Kind { get; }`<br>`bool TryResolve<T>(out T target)` | `NullSkillTarget` | 夺舍 / 治疗 / 嘲讽 |
| `IMovementDriver` | `Game.Actor` / `scripts/actor/` | `Game.Actor`、`Game.Spawn` / `scripts/actor/`、`scripts/spawn/` | 移动输入 → 速度解算（可替换：地面/飞行/追击/被夺舍） | `Vector3 Resolve(Vector3 desired, float dt)` | `NullMovementDriver` | 玩家控制 / 怪物 AI |
| `IProjectile` | `Game.Combat` / `scripts/combat/` | `Game.Combat` / `scripts/combat/` | 子弹生命周期钩子 | `void OnSpawn(ProjectileDef def, ImpactContext ctx)`<br>`void OnHit(IActor victim, in HitInfo hit)`<br>`void OnExpire()` | `NullProjectile` | 子弹池 |
| `IStatProvider` | `Game.Data` / `scripts/data/` | `Game.Actor` / `scripts/actor/` | **数值包的唯一对外读口** | `StatPackage Current { get; }`<br>`float Get(StatId id)`<br>`StatPackage GetLayer(StatLayer layer, string slotId = "")`<br>`IReadOnlyList<ISkill> Skills { get; }`<br>`event Action<StatLayerId> LayersChanged` | `NullStatProvider`（全默认值） | 结算 / UI / 夺舍 |
| `IRandomProvider` | `Game.Core` / `scripts/core/` | `Game.Core` / `scripts/core/` | 可播种随机（决定论回放） | `int NextInt(int minInc, int maxExc)`<br>`float NextFloat()`<br>`IRandomProvider Fork(int salt)` | `DeterministicRandom` | 掉落 / 暴击 / 刷怪 |
| `IConfigSource` | `Game.Data` / `scripts/data/` | `Game.Data` / `scripts/data/` | 生成物加载与版本校验 | `T Get<T>(string table, string key) where T : Resource`<br>`IReadOnlyList<string> Ids(string table)`<br>`int SchemaVersion(string table)` | `NullConfigSource` | 启动引导 |
| `IBuildStep` | `Game.Data` / `scripts/data/` | `Game.Data`、`tools/data_pipeline/` | 构建期流水线步骤（导入脚本内部契约） | `string Name { get; }`<br>`BuildResult Run(BuildContext ctx)` | — | `tools/data_pipeline/` |
| `IPoolable` | `Game.Core` / `scripts/core/` | `Game.Combat`、`Game.Actor` / `scripts/combat/`、`scripts/actor/` | 对象池成员 | `void OnRent()`<br>`void OnReturn()`<br>`void ResetState()` | — | 子弹 / 特效 / 伤害飘字 |
| `IVisualDescriptor` | `Game.Actor` / `scripts/actor/` | `Game.Actor` / `scripts/actor/` | **外观描述符（正式接口）**：换装/换外观的唯一通道，渲染层不感知"画的是玩家还是怪物" | `CharacterDef Def { get; }`<br>`ProjectileDef ProjectileStyle { get; }`<br>`EffectDef MuzzleFx { get; }`<br>`float Scale { get; }`<br>`void ApplySlot(VisualSlot slot, CharacterDef overrideDef)`<br>`void ResetAll()` | `NullVisualDescriptor` | 渲染 / 夺舍 |
| `ISkillCaster` | `Game.Data` / `scripts/data/` | `Game.Skill` / `scripts/skill/` | **技能持有与释放入口**：装配技能列表、按冷却门控释放、统一驱动 tick（含夺舍后的技能替换） | `IReadOnlyList<ISkill> Skills { get; }`<br>`bool CanCast(SkillId id)`<br>`bool TryCast(SkillId id, in SkillContext ctx)`<br>`void TickSkills(float dt, in SkillContext ctx)`<br>`bool TryReplaceSkills(IReadOnlyList<ISkill> skills)` | `NullSkillCaster`（永远 `false`） | Actor / AI / 夺舍 |

**仲裁要点 A · `IVisualDescriptor` 已转正（Lead 仲裁 #3-1）**

> 状态：**正式接口（v1），不再候选。** 它曾只出现在 `ARCHITECTURE.md` §3.2 的候选区，现已转正。
> **为什么必须有它**：「夺舍怪物技能」如果只换数值，外观就得靠 `if (isTakenOver)` 分支渲染（触犯反模式 A3），并且"换模型 + 换动画 + 换子弹样式 + 换挂点"会散进每个场景脚本。
> **职责边界（实现时守死）**：
> - 它**只描述外观**（模型 / 配色 / 尺寸 / 挂点 / 子弹样式 / 枪口特效），**不持有**数值、技能、阵营、生命值。
> - 渲染层只读它，**不知道**画的对象是玩家还是怪物 —— 这是"夺舍零成本"的根因。
> - 唯一写入口是 `ApplySlot` / `ResetAll`，由 `IActor.TryApplyTakeover` 调用，**不对外部模块开放直接改**。

**仲裁要点 B · `ISkill` / `ISkillTarget` 声明下沉到 `Game.Data`（Lead 仲裁 #3-2）**

> **问题**：`IStatProvider.Skills` 的类型是 `IReadOnlyList<ISkill>`，而 `IStatProvider` 声明在 `Game.Data`。若 `ISkill` 声明在 `Game.Skill`，则 `Game.Data` 必须 `using Game.Skill` —— **直接违反 §3.3 与 `ARCHITECTURE.md` §2.3 的分层矩阵**。
> **仲裁结论**：`ISkill` / `ISkillTarget` 是**纯数据契约**（只含 `Id`、`Cooldown`、`TargetRule`、`EffectRef` 等签名与值类型），因此：
> - **声明位置**：`Game.Data`（`scripts/data/`）
> - **实现位置**：`Game.Skill`（`scripts/skill/`，如 `SkillRuntime`）
> - `Game.Data` 不再依赖 `Game.Skill`；`Game.Skill` 依赖 `Game.Data` 合法（见依赖矩阵）。
> **纪律**：`ISkill` 的签名里**不得出现任何引擎 `Node` 或表现类型**（否则 `Game.Data` 会被强制注入依赖）。若某技能实现需要 Node，那属于实现细节，留在 `scripts/skill/` 内部，不进接口签名。

**仲裁要点 C · `ISkillCaster` 转为正式接口（Lead 仲裁 #6，关闭 ARCHITECTURE「已知缺口」）**

> **问题**：`ARCHITECTURE.md` §3.2 与本文件 §3.2.3 的装配示例里 `Actor` 都实现了 `ISkillCaster`，但 §4.1 全集表里没有它 —— 属于**悬空接口**（架构守卫查不出，但读者会照着示例写出无契约实现的类型）。
> **仲裁结论**：正式纳入 §4.1 全集表（第 **15** 个，全集由 14 增至 15）。归属沿用「声明 / 实现分离」原则：
> - **声明位置**：`Game.Data`（`scripts/data/`）—— 因为 `ISkill`、`SkillId`、`SkillContext`、`SkillCastResult` 都已在 `Game.Data`，签名里不出现任何上层或引擎类型。
> - **实现位置**：`Game.Skill`（`scripts/skill/`）—— 技能驱动属技能业务；`Actor` 在 `scripts/actor/` 仅做**装配与转发**，不含驱动逻辑。
> **与 `IStatProvider.Skills` 的分工（实现时必须守住）**：
> - `IStatProvider.Skills` 是**只读数据视图**，回答"我有哪些技能"。
> - `ISkillCaster` 是**行为入口**，负责"能不能放 / 放出去 / 每帧 tick"。
> - 两者不得互相替代；`ISkillCaster.TryReplaceSkills` 是夺舍时换技能列表的**唯一**通路，内部转调 `Actor.TryApplyTakeover`，不直接改 `_skills`。
> **签名取舍说明**：
> - 保留 `CanCast(SkillId)` 与 `TryCast(SkillId, ctx)` 分离 —— 与 `IActor.TryApplyTakeover` 同一「预检 + 执行」范式，UI/AI 可先问后放。
> - `TickSkills` 用**单次批量**而非 `TickSkill(SkillId)` 逐个调 —— 避免每帧 N 次虚调用与 N 次冷却表查找。
> - `TryReplaceSkills` 返回 `bool`（`TryXxx` 规则）：槽位被夺舍占用时返回 false，零副作用。

### 4.1.1 与 `ARCHITECTURE.md` §3.2 的签名对齐（**以本节为准**）

`ARCHITECTURE.md` §3.2 已与本节完成对齐（arch-outline 第三轮同步，逐字核对 18 条签名）。下表保留**归一历史**，供追溯每一处签名为何长这样 —— 实现时仍以本节为准。

| 接口 | ARCHITECTURE §3.2 写法 | 本文档 v1 写法（以此为准） | 归一理由 | 状态 |
|---|---|---|---|---|
| `IActor` | 身份与生命周期：`ActorId`、`IStatProvider`、状态查询 | 加 `bool TryApplyTakeover(in TakeoverSpec spec)`（唯一写入口） | 夺舍要失败语义；`TryXxx` 与项目 `Is/Try` 风格一致 | **✅ 已对齐**（ARCH §3.2 L255 已含该方法） |
| `ISkill` | 技能行为：`CanCast` / `Cast(ISkillTarget)`；声明 `Game.Data`、实现 `scripts/skill/` | `CanCast(in SkillContext)` / `Cast(in SkillContext)` / `Tick`；声明/实现位置同上 | 技能需要 tick 时序，不能只有一次性 Cast | **✅ 声明位置已对齐**；仅签名细化（`ISkillTarget` → `SkillContext`）待回写 |
| `ISkillTarget` | 候选者集合与选取策略的消费方；声明 `Game.Data` | `Kind` + `TryResolve<T>` | 夺舍需要"按类型取目标"，泛型解析是唯一安全写法 | **✅ 声明位置已对齐**；签名细化待回写 |
| `IVisualDescriptor` | **正式接口**（ARCH 原写作 `ApplySlot(slotId, resourceId)`，**已废弃**） | `ApplySlot(VisualSlot, CharacterDef)` + `ResetAll()` + 只读表现属性 | 夺舍的可执行前提；强类型入参可编译期校验 | **✅ 已转正且已回写**：ARCH §3.2 已归一并废弃字符串写法（仲裁 #5 裁定、#6 确认，见 §4.1.1.1） |
| `IStatProvider` | `StatPackage Current { get; }` + 变更通知 | `Current` + `Get(StatId)` + `GetLayer(layer, slotId)` + `Skills` + `LayersChanged` | 夺舍需要按层读；`Get` 是热路径读口的便利方法 | ✅ 已回写（ARCH 原为简写） |
| `IConfigSource` | `GetDef<T>(DefId)`、列表枚举、schema 校验 | `Get<T>(table, key)` + `Ids(table)` + `SchemaVersion(table)` | 表名 + Id 是生成物的物理组织方式，直接映射 `index/<table>.json` | ✅ 已回写（无冲突） |
| `IPoolable` | `OnSpawn / OnDespawn / Reset` | `OnRent / OnReturn / ResetState` | 与"池化"语义一致，避免与 `IActor` 的 `Spawn` 混淆 | ✅ 已回写（无冲突） |

**状态：下表 7 项已全部回写完毕 ✅**（arch-outline 确认 ARCHITECTURE §3.2 已按本节定稿）。本表此后仅作「为什么这样定」的追溯依据，不再是待办。`ISkillCaster` 已于仲裁 #6 正式纳入 §4.1 全集表（第 15 个，见 §4.1 仲裁要点 C）。

#### 4.1.1.1 `ApplySlot` 签名已裁决并已回写（Lead 仲裁 #5）

> **状态：已裁决 + 已回写 ARCHITECTURE，不再开放讨论。**
>
> **结论**：`IVisualDescriptor.ApplySlot` 的**正式签名**为强类型写法 ——
> ```csharp
> void ApplySlot(VisualSlot slot, CharacterDef overrideDef);
> ```
> 本文 §4.1 接口表与 06 §4.3 / §4.5 的调用写法（`Visual.ApplySlot(VisualSlot.Body, spec.Def)`）**均为最终形态**。
>
> **裁决理由（Lead 仲裁 #5）**：
> 1. `string resourceId` 让路由字符串拼错只能到**运行期**才炸；架构守卫（GS001~GS007）无法对字符串内容做静态检查，等于放弃了一道守卫。
> 2. `CharacterDef` 是 Excel 生成物，**编译期即可校验** —— 传错类型编译不过，与规则 4「数值/定义走数据」的强类型链路一致。
> 3. 与项目既有风格一致：`IStatProvider.Get(StatId id)`、`ISkillTarget.TryResolve<T>` 都是强类型入参，`ApplySlot` 不应是唯一用裸字符串的接口方法。
>
> **回写状态：✅ 已完成**（Lead 仲裁 #6 确认）——`ARCHITECTURE.md` §3.2 的 `IVisualDescriptor` 行已按本节落定为 `ApplySlot(VisualSlot slot, CharacterDef overrideDef)`，两处口径一致，无残留待办。
>
> **历史备注**：本条在同步 #2 时曾以「待仲裁项」形式登记，仲裁 #5 裁定、仲裁 #6 确认回写完毕。**至此全项目唯一口径就是强类型写法**，任何文档再出现 `ApplySlot(slotId, resourceId)` 均属缺陷。

### 4.1.2 声明位置的唯一真源（防分层违规）

| 规则 | 说明 |
|---|---|
| 声明位置只放契约 | `interface` 与其参数/返回值用到的值类型（`StatId`、`TakeoverSpec`、`SkillContext`）必须同层或更下层 |
| 实现位置可以更上层 | `Game.Actor` 实现 `Game.Data` 的 `IStatProvider`、`Game.Skill` 实现 `Game.Data` 的 `ISkill`，都是合法方向 |
| 改声明位置 = 架构变更 | 必须回写 `ARCHITECTURE.md` §2.3 矩阵与 §3.2 表，走 Lead 仲裁 |
| 禁止的捷径 | 用 `InternalsVisibleTo`、把接口复制一份到上层、或在 `Game.Data` 里 `using Game.Skill` |

### 4.2 接口签名细节（写实现前必读）

```csharp
namespace Game.Data;   // 声明位置：scripts/data/

/// 数值包唯一对外读口。实现方（scripts/actor/ 的 Actor）负责 Fold 分层，调用方只读。
/// 注意：ISkill 也声明在 Game.Data（见 §4.1 仲裁要点 B），所以本接口不引入任何跨层依赖。
public interface IStatProvider
{
    StatPackage Current { get; }                      // 已 Fold 的 Final（只读视图）
    float Get(StatId id);
    StatPackage GetLayer(StatLayer layer, string slotId = "");  // 返回不可变快照
    IReadOnlyList<ISkill> Skills { get; }             // 技能列表，夺舍时整组替换
    event Action<StatLayerId> LayersChanged;          // 层变更通知（UI/表现层订阅）
}
```

```csharp
namespace Game.Data;   // 声明位置：scripts/data/ —— 纯数据契约，不含任何 Node 依赖
// 实现位置：scripts/skill/（SkillRuntime、TakeoverSkill 等）

/// 技能契约：只管"能不能放 / 怎么放 / 怎么 tick"，不含表现命令。
public interface ISkill
{
    SkillId Id { get; }
    bool CanCast(in SkillContext ctx);
    SkillCastResult Cast(in SkillContext ctx);
    void Tick(float dt, in SkillContext ctx);
}
```

```csharp
namespace Game.Actor;   // 声明位置：scripts/actor/

/// 场景中一个可被瞄准/交互的实体。注意：这是"句柄"，不是 Node。
public interface IActor
{
    IActor Handle { get; }                 // 供 UI/技能缓存，避免持有 Node 引用
    IStatProvider Stats { get; }           // 读数值
    IVisualDescriptor Visual { get; }      // 换外观
    TeamId Team { get; }                   // 敌我判定
    bool IsAlive { get; }

    /// 夺舍的唯一写入口（Lead 仲裁 #3-3，见 06 §4）。
    /// 返回 false 的合法原因：目标已被占用 / 目标不可夺取 / 施法者处于无敌帧 / 已在夺舍中。
    /// 失败时不得产生任何副作用（不改层、不改技能、不改外观）。
    bool TryApplyTakeover(in TakeoverSpec spec);
}
```

```csharp
namespace Game.Actor;   // 声明位置：scripts/actor/

/// 外观描述符（正式接口，Lead 仲裁 #3-1）。
/// 只描述"长什么样"，不持有数值/技能/阵营；渲染层据此渲染，不感知对象身份。
public interface IVisualDescriptor
{
    CharacterDef Def { get; }                        // 当前外观定义（夺舍后即目标的 CharacterDef）
    ProjectileDef ProjectileStyle { get; }           // 当前子弹样式（开火时读）
    EffectDef MuzzleFx { get; }                      // 枪口特效
    float Scale { get; }                             // 体型缩放
    void ApplySlot(VisualSlot slot, CharacterDef overrideDef);  // 换某槽位外观
    void ResetAll();                                 // 恢复 Def 默认外观（回滚用）
}
```

**接口设计四条硬性写法**：

1. **方法参数用 `in`/`ref` 传值语义结构体**，避免热路径分配。
2. **不要在接口里暴露 `Node`**：任何 `Node` 都藏在实现类内部；接口层只出现值类型、接口、以及 `Game.Data` 的资源类型。
3. **每个接口都要有一个 Null Object 实现**（`NullMovementDriver.Instance` 等），保证组合时不用写 `if (x != null)`。
4. **会失败的操作一律 `TryXxx` 返回 `bool`**（Lead 仲裁 #3-3）：`TryApplyDamage`、`TryApplyTakeover`、`TryResolve<T>`。`void` 动作型方法只允许用于"不可失败"的操作。

### 4.3 Game.Core 最小公共件（先建这些，别每个任务重复造）

| 公共件 | 用途 | 落地命名空间 |
|---|---|---|
| `EventBus` | 类型安全的 C# 事件总线，带"订阅代际"防泄漏 | `Game.Core` |
| `DeterministicRandom` | 可播种随机，实现 `IRandomProvider` | `Game.Core` |
| `ObjectPool<T> where T : IPoolable` | 统一对象池 | `Game.Core` |
| `StatBlock` / `StatPackage` | 数值包（详见 `05-numeric-package.md`） | `Game.Data`（`scripts/data/`） |
| `DamageEvent` / `DamageSpec` | 伤害值对象 | `Game.Combat`（`scripts/combat/`） |
| `DefBase`（抽象类） | 所有 `*Def` 资源的共同基类，承载 `Id`/`SchemaVersion`/`DisplayName` | `Game.Data`（`scripts/data/`） |

> 判定标准：**任何"我在别的任务里写过类似的东西"的感觉，先去上表 + `ARCHITECTURE.md` 找，找不到再加。**

### 4.4 抽象卡模板（复制进任务描述）

```markdown
## 任务抽象卡

### ① 边界
- 数据 owner：
- 调用方：
- 写入方：
- 生命周期：
- 时序/线程假设：

### ② 抽象
- 新增/复用的接口：            （必须是 03 §4 表里的，或本次登记的新接口）
- 新增的数据载体类型（值/引用语义）：
- 命名空间归属：
- ARCHITECTURE.md 是否已登记： 是 / 否（本任务负责登记）

### ③ 扩展点（未来最可能的 3 个变体）
| 变体 | 需要的改动文件数 | 结论 |
|---|---|---|
|  |  |  |
|  |  |  |
|  |  |  |

### 反例自查
- [ ] 没有新增继承深度 > 2 的类
- [ ] 没有 `if (类型 == ...)` 分支
- [ ] 没有数值字面量（见 04）
- [ ] 没有静态可变异步全局状态
```

---

## 5. 反例（出现即打回）

| # | 反模式 | 具体表现 | 为什么错 | 正确做法 |
|---|---|---|---|---|
| A1 | **上帝类** | `GameManager` 同时管输入、刷怪、存档、UI 通知；单文件 > 400 行且 > 8 个职责 | 任何改动影响全局，不可测 | 拆成 `RunController` / `SpawnDirector` / `SaveService`，各实现单一接口 |
| A2 | **深继承 5 层** | `Entity→Living→Damageable→SkillCasting→Elite…` | 单继承槽位耗尽，能力无法正交组合 | 改成 `sealed class` + 多接口组合 |
| A3 | **`if (类型 == ...)` 分支** | `switch (actor.Kind) { case Boss: ... }` 遍地开花 | 新增类型要改所有分支（违反规则 7） | 变化点抽成策略对象：`IBossPhase`、`IDamageModifier` |
| A4 | **静态全局可变状态** | `public static List<Actor> AllActors`、`public static float TimeScale` | 无法多实例、无法回放、单测互相污染 | 实例化服务 + 依赖注入；只读常量才允许 `static` |
| A5 | **搬运 MonoBehaviour 风格** | 每个脚本自己 `FindObjectOfType`、自己存大量 public 字段给编辑器拖 | Unity 习惯里"场景即容器"，在 Godot 里会造成隐式耦合 + 不可测 | 显式依赖：构造/初始化传参，用 `[Export]` 只暴露**数据**不暴露**引用图** |
| A6 | **Godot Node 里塞纯逻辑** | `_PhysicsProcess` 里直接算伤害与命中 | 无法脱离引擎单测；热路径性能不可控 | 逻辑放 `Game.Combat`（纯 C#），Node 只做驱动与表现 |
| A7 | **伪抽象** | 抽了 `IWeapon` 但只有一个实现，且方法签名带"我们唯一的霰弹枪"专属参数 | 增加维护成本，无扩展收益 | 要么给出第二个变体的理由，要么暂不抽象，写进扩展点再说 |
| A8 | **事件参数用 `object`** | `EventBus.Publish("hit", (object)ctx)` | 运行时才炸，无编译期保障 | 泛型 `Publish<T>(in T)` + `record` 载荷 |
| A9 | **接口暴露引擎节点** | `interface ISkill { Node GetNode(); }` | 把可测逻辑拖回引擎依赖 | 返回值/接口，Node 是实现细节 |
| A10 | **`async void` 做游戏时序** | `async void Cast()` 里 `await Task.Delay(500)` | 帧率无关的时序 bug、无法暂停/回放 | 用 `Tick(dt)` 计时的技能阶段机 |
| A11 | **平行契约**（违反 §3.4 A） | 同一个概念有 2+ 套组件集合 / 2+ 条执行管线，各走各的。**实例**：子弹有 `Projectile` / `ProjectileState` / `WeaponFireSystem` / `ProjectileSystem` 四条并存，生产跑一条、测试跑另一条 | 改一处只管一条，测试全绿但线上不一样 | 收敛成一份组件 + 一个入口；旧路径删掉，不是留着"兼容" |
| A12 | **漏配组件**（违反 §3.4 B） | 同类产物带的东西不一样。**实例**：子弹带 `View` 不带 `ViewAppearance`，特效两样都没有，只有人物是齐的 | 表现层拿不到数据 → 实体静默隐身，且**编译/测试/守卫全不报错** | 工厂统一填充；用例断言"每种产物都带全套" |
| A13 | **死数据列**（违反 §3.4 C） | 表里填得整整齐齐，代码里 0 处读取。**实例**：`VisualPack` 26 行全填、校验器还验磁盘文件存在，`scripts/` 零读取 | "数据驱动"变成"数据装点"；策划改了没反应，还以为生效了 | 加列必须同 PR 补消费方，否则删列 |
| A14 | **空枚举**（违反 §3.4 D） | 枚举/`switch` 的成员无人实现，或落进静默 `default: return false;`。**实例**：`WeaponAimKind` 11 成员、`ProjectileHitPolicy` 8 成员，0 消费者；`EcsObjectFactory.OnExpire` 的 `default` 把 `Explode`/`Inherit` 静默吞掉 | 把"未实现"伪装成"业务上不该发生"，排查方向被彻底带偏 | 要么实现，要么 `default` 抛异常说明未实现 |
| A15 | **0 当"没配"**（违反 §3.4.2） | 用 `0` / `""` / `null` 兼做"合法取值"与"未配置"。**实例**：`VisualSlot` 默认 `0`，而 `0` 是合法外观号 | "忘了配"与"配成第 0 号"运行期同义，只能靠人肉猜 | 用与所有合法值不撞的哨兵（`-1`），让"有意为空"可断言 |

---

## 6. 自检清单：抽象审查 10 问 + 契约完备 4 问（PR 自检，逐项勾选）

> 全部勾选才算通过。任何一项打叉，必须在 PR 描述里写明理由并获得 Lead 明确同意（写进 `ARCHITECTURE.md` 的例外登记）。

- [ ] **Q1 抽象卡**：任务描述里有完整三件套（边界 / 抽象 / 扩展点 3 变体）？
- [ ] **Q2 复用查找**：写代码前查过 `ARCHITECTURE.md` 接口表 + §4.3 公共件表，并注明"复用了哪一个"或"确认为新增"？
- [ ] **Q3 接口选型**：新抽象优先用了 `interface`，不是 `abstract class`？（例外仅 `DefBase` 扁平四件套基类）另：新接口在 §4.1 表里写清了**声明位置**与**实现位置**？
- [ ] **Q4 数据载体**：纯数据用了 `sealed record struct` / `record`，且**不可变**（无 public setter，无原地 `+=`）？
- [ ] **Q5 继承深度**：整个类型链深度 ≤ 2？没有为了加能力而新增派生类？
- [ ] **Q6 变化点命名**：没有 `if (类型 == ...)` / `switch (Kind)` 这类按具体类型分支的地方？（全部转成了策略对象）
- [ ] **Q7 空对象与失败语义**：组合进来的每个接口依赖都有 Null Object 默认值（没有 `if (x != null)` 链路）？会失败的操作都是 `TryXxx` 返回 `bool` 且失败无副作用？
- [ ] **Q8 引擎隔离**：`Game.Core` 不依赖 Godot；`Game.Data` 不出现 `Node`；逻辑不写在 `_PhysicsProcess` 里？
- [ ] **Q9 静态状态**：除 `static readonly` 常量与单例式无状态服务（如 `NullXxx.Instance`）外，没有 `public static` 可变字段？
- [ ] **Q10 回写架构**：本任务新增/变更的接口、数据契约已在 `ARCHITECTURE.md` 的接口表登记（含"首个使用方"）？

### 6.1 ★契约完备四问（§3.4 配套，2026-11-09 起**必答**）

> 这四问专门抓"每处局部都对、整体是断的"那类缺陷——它们**不会**被 Q1~Q10 抓到，
> 也不会被编译、架构守卫、既有测试抓到。答不出就是没做完。

- [ ] **C1 几处在造？**（对应规则 A）这个概念有**几个**造实体的入口 / 几套组件集合？
      用 `grep` 数出来写进 PR：`grep -rn "Commands.Set(entity, new <组件>" scripts/` → 期望 **1 处**。
- [ ] **C2 新加一种也齐吗？**（对应规则 B）现在加第四种产物（新子弹/新怪物/新特效），它带的东西和老的**完全一样**吗？
      有没有用例能证明？（没有 → 去写 `AppearanceCoverageTests` 那样的工厂层用例）
- [ ] **C3 这列谁在读？**（对应规则 C）本次新增/涉及的每个数据列，`grep` 出**运行期读取方**的**文件:行号**。
      只有生成器/校验器读过不算——那只是"数据进去了"，不是"数据起作用了"。
- [ ] **C4 这几支谁处理？**（对应规则 D）本次新增/涉及的每个枚举成员、每个 `switch` 分支，有无实现？
      `default:` 是不是静默 `return`？（是 → 改成抛异常说明未实现）


### 6.2 抽成 interface 的判定：满足 2 条以上才抽象

| 判据 | 说明 |
|---|---|
| 已有或明确计划 ≥ 2 个实现 | 1 个实现 + 1 个测试假实现也算 |
| 出现在多个命名空间边界上 | 需要解耦（如 `Game.Skill` 想调 `Game.Actor`） |
| 是已知的变异点 | 写进了 §4.4 的"扩展点 3 变体" |
| 需要被数据配置引用 | Excel 的 `Projectiles.ImpactBehavior` 列要按名查到实现 |

只有 1 个实现且不满足以上任何一条 → **不抽象**，但要在代码注释里留 `// ARCH: 暂不抽象，扩展时落点=<...>`。

---

## 7. 与其它规则的交叉

| 规则 | 交叉点 | 本文档负责的部分 |
|---|---|---|
| 规则 4 · 数据驱动 | `04-data-driven.md` | 抽象卡的第 ② 项必须说明"哪些字段进 Excel"，禁止把数值写进抽象 |
| 规则 6 · 数值包 | `05-numeric-package.md` | `IStatProvider` 是本文档 §4.1 登记、由 05 定义实现 |
| 规则 7 · 可扩展性 | `06-extensibility.md` | 抽象卡第 ③ 项是 06 的"新增文件数预算"来源 |
| 规则 1/2 · 语言与选型 | `01-gdscript-ban.md`、`02-tech-selection.md` | UI 例外与 GDExtension 下沉判据以那两份为准，本节只做命名空间摘要 |
| 规则 3 · 架构大纲 | `docs/architecture/ARCHITECTURE.md` | §4.1 接口表必须同步登记，本文档不得成为第二真源 |
