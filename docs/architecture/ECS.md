# BiuBiuOrigin ECS 架构（数据导向实现）
☞☞☞ 状态: v2 | 最后更新: 2026-11-06 | 维护规则: 与 `docs/architecture/ARCHITECTURE.md` **同事务**就地覆盖更新（见 §11） | 版本: ECS-v2

> **本文件是什么**：`ARCHITECTURE.md` 的**下游章节**，专门承载 ECS（实体-组件-系统）实现层的全部形状。
> `ARCHITECTURE.md` 仍是唯一入口与总真源；本文件只被 `ARCHITECTURE.md` 的 ECS 相关小节引用。
> 两者冲突时：**以本文件为准修正 ARCHITECTURE 的 ECS 小节**（因为这里写得细），**但分层矩阵与接口全集仍以 `docs/rules/03-abstract-first.md` §4.1 为准**。
>
> **给开工的人（人或 AI）：** 先把 §0 速读 + §1 七条判定 + §3 目录与归属 + §4 组件规范读完，再动第一行代码。
> 本文件任何一处「未落地」都不是没想清楚，而是**形状已定、等任务去填**。

---

## 0. 三十秒速读

| 你会被问 | 答案（唯一口径） |
|---|---|
| 用什么 ECS | **自研数据导向 ECS，位于 `Game.Core.Ecs`（`scripts/core/ecs/`）**，零 Godot 依赖、零第三方包 |
| 数据长什么样 | **`unmanaged` struct 组件**，按 **Archetype（原型）列式存储**，按 `[Identity]` 字段寻址 |
| 逻辑长什么样 | **`SystemBase` 小类**（手写 `OnExecute()` + `for (int i = 0; ...)` 循环）+ 按 `[UpdateInGroup]` / `[UpdateAfter]` 排成一条 **SystemPipeline** |
| 实体是什么 | **身份键 `Entity`（`index + generation`）**，不是对象引用；`IActor` 句柄 = `ActorHandle{Entity}`（`readonly record struct`） |
| 引擎在哪 | 引擎**只在宿主层**：`scripts/host/`（`WorldHost : Node3D`、`TransformBridge`、`VisualBridge`） |
| Godot 节点是什么 | **视图**，不是实体；节点不存玩法真值，生命周期由 `NodeLifecycleSystem` 单向驱动 |
| 热点并行 | `[Job]` 系统 + `Query.ForEachJob` 按 chunk 分片；**结构化命令（生成/销毁/增删组件）只能走主线程**（`CommandBuffer`） |
| 谁说了算 | §1 七条判定 + §4.2 组件类别表 + §7 依赖门禁；违反 = 任务返工 |
| 现状 | 见 §0.1 |

### 0.1 落地状态总表（收工时必须更新）

| # | 模块 | 位置 | 状态 | 任务 |
|---|---|---|---|---|
| 1 | 数值/枚举/事件/随机/容器 | `scripts/core/` | ✅ 已落地 | `ecs-core` |
| 2 | ECS 内核（Entity/World/Archetype/Query/CommandBuffer/Pipeline） | `scripts/core/ecs/` | ✅ 已落地 | `ecs-kernel` |
| 3 | 官方组件 | `scripts/core/ecs/components/` | ✅ 已落地 | `ecs-kernel` |
| 4 | 宿主（WorldHost / FixedFrame 管线 / 命令提交） | `scripts/host/` | ✅ 已落地 | `ecs-host` |
| 5 | Transform 桥 / 视图桥 | `scripts/host/bridge/` | ✅ 已落地 | `ecs-host` |
| 6 | 冒烟场景 | `scenes/debug/` | ✅ 已落地 | `ecs-host` |
| 7 | 测试工程 | `tests/` | ✅ 已落地（174 用例） | `ecs-tests` |
| 8 | C++ 批量 job 扩展骨架 | `native/` | ☐ 未落地（本轮只登记契约） | `ecs-native` |
| 9 | **内容契约**（`ObjectArchetype`/`WeaponContract`/`ProjectileContract`/`ValueRef`/`ForgeRules`） | `scripts/core/Content/`、`scripts/combat/` | ✅ 已落地（见 ARCHITECTURE §17） | `object-archetype` |
| 10 | **空间划分 / 宽相**（命中检测从 O(n·m) → O(n)） | `scripts/combat/`（尚未创建） | ☐ 未落地（§12 #4，属游戏逻辑） | `broadphase` |

---

## 1. 七条判定（一票否决，放在最前面）

| # | 判定 | 正确写法 | 违规写法（打回） |
|---|---|---|---|
| **J1** | **实体是身份键，不是对象** | `Entity`、`ActorHandle`（`readonly record struct`） | `class Actor`、`ActorId` 句柄里放对象引用、`Dictionary<Type, object>` |
| **J2** | **组件是数据，不是行为** | `[Identity] public Entity Ref; public Vector3 Velocity;` | 组件里写 `public void Update()`、组件持有 `Node3D` / `Material` / `PackedScene` |
| **J3** | **逻辑只活在 System 的 `for` 循环里** | `Query.ForEach<Velocity, Transform3>(SpanAction<…>)` | 组件方法、`if (kind == ActorKind.Boss)` 分支 |
| **J4** | **引擎只在宿主层出现** | `Game.Core.Ecs` 里 **0 个 `using Godot`** | `Game.Core` 或 `Game.Data` 里 `using Godot` |
| **J5** | **结构性变更只能主线程** | `CommandBuffer` + 帧末一次提交 | 遍历中途 `World.Create/Destroy/Add/Remove`；job 里写命令 |
| **J6** | **查询形状稳定，按 chunk 批处理** | `query.ForEach`（无每实体回调分配）/ `SpanAction` | 每帧 `new List<T>`、LINQ、每实体 delegate |
| **J7** | **热点可并行，热点可下沉 C++** | `[Job]` / `Query.ForEachJob`，或 `native/` 的 `IEcsBatchSolver` 扩展点 | 在 C# 每帧回调里做 200+ 实体的重计算且不下沉 |

> 一句话：**「数据是 struct、逻辑是 System 循环、引擎在宿主层、结构变更排到帧末、热点可 job 可下沉」。**

---

## 2. 为什么是现在这套（技术路线决策记录）

| 决策 | 选择 | 理由 | 被否决的方案 |
|---|---|---|---|
| ECS 来源 | **自研**（`Game.Core.Ecs`） | ① 规则2：「规则7 的『0 文件扩展』必须在热路径上成立」，第三方 ECS 的数据布局/迭代协议会把扩展点锁死；② 项目要求 `Game.Core` 零引擎依赖、可裸测，第三方包多为 `Id=object` 字典式或绑定 Unity；③ GDExtension 下沉需要**我们自己的 chunk 内存契约**（§9），第三方实现无法保证 | DefaultEcs / Morpeh / EnTT（C++）/ Friflo.Engine.ECS |
| 存储模型 | **Archetype 列式**（SoA）+ 每组件 `Memory<T>` 池，`unmanaged` 组件搬到新原型 | 割草玩法最热的查询是「同形状实体的推进 + 命中」，连续内存 + `Span` 天然可向量化、可被 C++ 消费；增删组件会搬数据，但增删频率远低于逐帧遍历 | 组件独立池 + 间接索引（cache 不友好）；`Dictionary<Type, object>`（每帧装箱） |
| 代码生成 | **不用 source generator** | 本轮要「先立骨架 + 可读」，手写系统循环更直观；生成器是后续优化项，登记在 §12 | 用 SG 生成访问器（版本升级脆弱、调试困难） |
| 逻辑组织 | **SystemBase 小类 + `for` 循环**（混合式 ECS） | 规则7「1 个文件扩展」最直观：新玩法 = 新 `SystemBase`；且架构守卫 GS005/GS003 易判定 | 纯函数式 `Execute(ref World)` 无状态（难做分组/排序/开关）；巨型 `World.Update()` switch |
| 引擎耦合边界 | **宿主层 `scripts/host/`** | Godot 节点必须存在（渲染/物理/输入都建立在节点上），把桥接集中成一个可替换薄层，`Game.Core.Ecs` 保持纯净 | ECS 内核里直接 `using Godot`（丧失裸测能力，违 J4） |
| 并行 | **主线程结构化 + chunk 并行 job** | 结构化命令天然串行化；纯计算（移动/抛射物/寻路/伤害预计算）按 chunk 并行即可覆盖 200 敌人 / 500 抛射物规模 | 全帧多线程 ECS（复杂度爆炸）；单线程只靠 C#（见 §9 下沉判据） |

---

## 3. 目录契约与归属（本 ECS 的「谁写谁读」）

```text
scripts/
├─ core/                       Game.Core       纯 C#，零 Godot（可裸测）
│  ├─ Events/                  ☞ 事件与事件总线（规则 06 §5）
│  ├─ Math/                    ☞ 纯数学（不进引擎类型）
│  ├─ Random/                  ☝ IRandomProvider / DeterministicRandom（全工程唯一随机源）
│  ├─ Pool/                    ☞ ObjectPool<T> : IPoolable
│  ├─ Handles/                 ☞ ActorHandle / Actors（批次取里，避免每次查表重新解析）
│  └─ ecs/                     ☟☝ ECS 内核（ECSv1）
│     ├─ World.cs                 ♂ 实体/组件/原型的唯一拥有者
│     ├─ Entity.cs                ♂ index + generation 身份键
│     ├─ Archetype.cs             ♂ 同组件组合的 chunk 列式存储（OnStruct + OnUnstruct）
│     ├─ ComponentTypeRegistry.cs ☞ 组件 id / 大小 / 池分配
│     ├─ IComponentPool.cs        ♞ 每个组件的 Memory<T> 池（`T[]` + 用于定位的 pool id）
│     ├─ Query.cs                 ☞ 原型匹配（必含/排除）、chunk 迭代、SpanAction、Job
│     ├─ CommandBuffer.cs         ☞ 帧末一次性提交的结构化：Create/Destroy/Set/Remove（顺序与去重）
│     ├─ SystemPipeline.cs        ☞ 按分组/顺序驱动系统（分组的游戏阶段时间图）
│     ├─ SpanIteration.cs         ☞ 零分配回调（`SpanAction<T0,T1>` 等）
│     └─ attributes/              ☞ Identity / UpdateInGroup / UpdateAfter / UpdateBefore / Job
│     └─ components/              ☞ 官方组件（§4.2 表格）
├─ data/                       Game.Data       配置与数值（§4.1）
├─ combat/ actor/ skill/ spawn/    业务层：系统族（§6 分组表）
├─ host/                       Game.Host       ☟☝ 唯一允许引擎耦合的 ECS 宿主
│  ├─ WorldHost.cs             ☞ Node3D，创建 World / Pipeline / Random，驱动帧
│  ├─ FixedFramePipeline.cs    ☞ 帧阶段时间图（PreUpdate → FixedUpdate → Update → LateUpdate → 提交）
│  ├─ CommandFlushSystem.cs    ☞ 每帧唯一的结构化提交点
│  └─ bridge/
│     ├─ TransformBridge.cs    ☞ Transform3 → Node3D（插值/剔除）
│     └─ VisualBridge.cs       ☞ View / Visual 组件 ↔ 场景实例（池化换外观）
└─ ui/                         Game.UI         只读视图（§8）
native/                        C++ GDExtension 批量 job 扩展点（§9，本轮只登记）
tests/                         C# 测试（§10）
```

**层级与允许依赖（ECS 补充 §2.3 矩阵）：**

```text
host (Game.Host)  ─┬─▶ ecs-core (Game.Core.Ecs)   ✓ 唯一允许 using Godot
                   ├─▶ combat / actor / skill / spawn（系统族装配）  ✓
                   └─▶ data (Game.Data)            ✓
combat/actor/skill/spawn ─▶ Game.Core.Ecs + Game.Data     ✓   ✗ 不得 using Godot、✗ 不得使用 Node/Resource 类型
Game.Core.Ecs            ─▶ 仅 .NET BCL                   ✗ 不得 using Godot / Game.* / 第三方包
Game.UI                  ─▶ Game.Core（只读视图）         ✗ 不得 using Game.Host / 系统族 / Native.*
```

> 与 `ARCHITECTURE.md` §2.3 的关系：§2.3 的反向箭头「**Native → 只能被 Combat 使用**」保持不变；
> 本节新增的 `Game.Host` 是**表现/入口层**，方向与 `Game.Skill/Spawn` 同级，同样只允许向下。

---

## 4. 数据模型（组件规范）

### 4.1 三类数据（**先分类，再动手**）

| 类别 | 运行时代表 | 存放 | 可变性 | 例子 |
|---|---|---|---|---|
| **静态配置（Excel 唯一真源）** | `*Def : Resource` | `data/generated/` → `IConfigSource` 缓存 | 只读 | `CharacterDef`、`WeaponDef`、`ProjectileDef`、`EffectDef`、`SkillDef` |
| **运行时可变状态** | **`unmanaged` struct 组件** | Archetype chunk | 每帧可改（只在 System 里） | `Transform3`、`Velocity`、`Health`、`Team`、`Timers` |
| **视图/表现引用** | `Node` 引用 | **`View` 组件的旁路表 `ViewRegistry`**（`World` 外部，`scripts/host`） | 只有视图桥写 | `Node3D`、`MeshInstance3D`、材质句柄 |

> **硬约束 D1**：组件**不得**持有 `Resource` / `Node` / `Material` / `System.Object` 引用。需要引用时存 **`DefId` / `ViewId`（int）**，或存到 `ViewRegistry`。
> **硬约束 D2**：组件内的 `fixed` 数组/指针/可变长集合一律禁止（`Save<T>`/`With<T>` 处理不了）。需要 N 个同类型数据 = N 个组件类型（如 `Weapon0..Weapon2`）或独立实体。

### 4.2 官方组件清单（v1，落地在 `scripts/core/ecs/components/`）

> 命名铁律：组件 = 裸 struct 名（**无 `Component`/`Data` 后缀**），因为查询里会大量出现 `Query.Of<Rigid, Health>()`。
> 每个组件**必须**正好有一个 `[Identity]` 字段（J1 的执行方式，见 §5.2）。

| 组件 | 类别 | 字段（v1 形状，落地时以代码为准并回写本节） | 归属层 | 谁写 |
|---|---|---|---|---|
| `Transform3` | ☞ core.ecs | `Vector3 Position; Quaternion Rotation; Vector3 Scale` | ECS 内核 | 移动/AI 系统（**唯一位置真值**） |
| `Velocity` | ☞ core.ecs | `Vector3 Linear; Vector3 Angular` | ECS 内核 | `IMovementDriver` 实装（移动系统） |
| `Team` | ☞ core.ecs | `TeamId Value` | ECS 内核 | 生成系统（夺舍时由夺舍系统改） |
| `Lifetime` | ☞ core.ecs | `float Remaining` | ECS 内核 | 生成系统初始化，`LifetimeSystem` 递减 |
| `Health` | ☞ core.ecs | `float Current; float Max` | ECS 内核 | `DamageResolutionSystem`（唯一写入方） |
| `StatLayerSlots` | ☞ core.ecs | `int BaseId; int GrowthId; int EquipmentId; int BuffId; int TemporaryId`（指向 `Game.Data.StatPackage` 的不可变快照槽位，§4.3） | ECS 内核 | 伤害/装备/夺舍系统 |
| `Rigid` | ☞ core.ecs | ☞ 标记（物理体型、质量） | ECS 内核 | 生成系统 |
| `Projectile` | ☞ combat | `int ShooterIndex; int ShooterGeneration; TeamId Team; float Damage; float Radius; float Speed; float RangeMeters; bool Critical; int WeaponId; DamageType DamageType; float Traveled; bool Spent`（唯一真源 `Game.Combat.Projectile`，落在 `scripts/combat/CombatComponents.cs`；`scripts/core/ecs/components/` 里曾有的同名示例组件已删除） | `scripts/combat/` | 开火技能 / `ProjectileSystem` / `HitDetectionSystem` |
| `TakeoverState` | ☞ core.ecs | `Entity Self; Entity Target; float Remaining; int SnapshotSlot` | ECS 内核 | 夺舍系统 |
| `View` | ☞ **host 旁路** | `int ViewId`（→ `ViewRegistry`） | `ViewRegistry` | `VisualBridge` |
| `Hurtbox` | ☞ combat | `float Radius`（命中形状的 v1 简化；后续可拆 `ColliderShape`） | ECS 内核 | 生成系统 |

> **新增组件 = 新加 1 个文件（不需要改 `World`/`Query`/注册表）**。这是规则7 在本层的直接体现。
> **修改组件字段形状 = 必须回写本节 + 变更日志**（存盘/回放兼容性）。

### 4.3 数值包的挂接（与规则6/05 的关系）

`Health` 只是**热路径副本**（伤害系统读写最快的那几个数），**权威数值仍在 `StatPackage` 层栈**：

```text
StatPackage(不可变)  ── 通过 StatLayerSlots 的槽位 id 引用 ──▶  热路径 Health / 组件副本
      ▲                                                              │
      │            每帧/每次战斗事件后回写（唯一写回点：DamageResolutionSystem）
      └──────────────────────────────────────────────────────────────┘
```

- `Hp** 的权威源仍是 `StatPackage` 的 Final 层**；`Health` 是它的一份快照，一次写入已在帧内同步（`StatSyncSystem`）。
- 夺舍 = 改 `StatLayerSlots.BaseId` 指向目标的 Base 包，然后 `StatSyncSystem` 下一帧把 `Health.Max` 刷成怪物值（**Combat 结算代码 0 行改动**，满足 06 §4.4）。
- 见 `docs/rules/05-numeric-package.md` 的只读视图纪律：`World` 里的 `Health` 是可写 struct，但 **`StatPackage` 本体依旧不可变**。

---

## 5. ECS 内核 API（**签名级契约**，实现必须逐字满足）

### 5.1 `Entity` 与句柄

```csharp
namespace Game.Core.Ecs;

/// 实体身份键：index 是槽位号，generation 防止"已销毁实体被旧句柄寻址"。
public readonly record struct Entity
{
    public static readonly Entity None = new(-1, 0);
    public int Index { get; }
    public int Generation { get; }
    public bool IsNone => Index < 0;
    public static bool operator ==(Entity a, Entity b) => a.Index == b.Index && a.Generation == b.Generation;
    public interface string ToString() => $"E{Index}:{Generation}";   // 实现示意，最终以代码为准
}

/// 句柄：所有跨层传递的"某个东西"都是它（IActor 的落地形态，见 §7.2）。
public readonly record struct ActorHandle(Entity Id);
```

**句柄规则**：句柄只在**本帧/下一帧内**有效；跨帧缓存必须每帧用 `World.IsAlive(entity)` 复核（`Actors` 工具类提供 `TryResolve`）。

### 5.2 `World`（唯一数据拥有者）

```csharp
namespace Game.Core.Ecs;

/// 实体/组件/原型的唯一拥有者。**只在主线程创建/销毁/增删组件；只读查询可跨线程。**
public sealed class World : IDisposable
{
    public World(ComponentTypeRegistry? registry = null);   // 不传 = 本世界私有注册表（测试友好）

    // ── 生命周期 ──
    public Entity Create(IReadOnlyList<ComponentType> components);   // 结构化：只允许主线程/命令缓冲
    public Entity Create<TA, TB>(in TA a, in TB b);  // 便捷重载（至多 4 个组件，见代码）
    public bool  Destroy(Entity e);                                   // 只允许主线程或帧末提交
    public bool  IsAlive(Entity e);                                   // 跨帧复核

    // ── 组件读写（结构化变更 = 改原型，只允许主线程） ──
    public bool  Has<T>(Entity e) where T : unmanaged;
    public ref T Get<T>(Entity e) where T : unmanaged;                // 未继承/未命中抛 GameEcsException
    public bool  TryGet<T>(Entity e, out T value) where T : unmanaged;
    public void  Set<T>(Entity e, in T value) where T : unmanaged;    // 有则更新，无则继承组件（= 一次原型迁移）
    public bool  Remove<T>(Entity e) where T : unmanaged;

    // ── 查询 ──
    public Query Query(IReadOnlyList<ComponentType> required, IReadOnlyList<ComponentType>? excluded = null);
    public Query Query<T0>();  // 便捷重载 T0..T7

    // ── 结构化命令（主线程立即执行，帧末由 CommandFlushSystem 统一提交 CommandBuffer 的内容） ──
    public CommandBuffer Commands { get; }   // 每世界一个，帧内可随意写，帧末一次提交

    // ── 调试/统计 ──
    public WorldStats Stats { get; }         // 实体数/原型数/chunk 数/已分配内存
    public int  Version { get; }             // 每次结构化变更 +1，供 Job 检测"遍历期间被改"
}
```

> **为什么 `Set<T>` 可以"有则更新无则添加"**：割草玩法高频用到"给实体挂一个 buff/状态"，一步到位比 `Has→Add` 两步更省且天然幂等。
> **为什么 `Get<T>` 抛异常而不返回 default**：`default(Transform3)` 会让实体站在原地不动，**静默错误**最贵；未继承组件是编程错误，必须在开发期炸出来。
> **性能口子**（给系统用，见 §5.4）：`Query.ForEach<T0,..>(SpanAction<..>)` 里拿的是 `Span`/`ref`，**不经过 `Get<T>(Entity)`**，零虚调用。

### 5.3 `Archetype` / `Query` / 零分配迭代

```csharp
namespace Game.Core.Ecs;

/// 一簇"组件组合相同"的实体；列式存储（每组件一块连续内存），按 chunk 分块。
public sealed class Archetype
{
    public TypeSet Components { get; }                 // 有序类型集合（== 原型签名）
    public int EntityCount { get; }
    public int ChunkCapacity { get; }                  // 每 chunk 容量（默认 128，经配置注入）
}

/// 原型匹配结果。**创建 Query 时匹配一次**，遍历时不重复匹配（仅剔除已销毁原型）。
public readonly struct Query
{
    public int MatchingChunkCount { get; }
    public Query With<T>() where T : unmanaged;
    public Query Without<T>() where T : unmanaged;

    // 逐实体回调（无委托 = 无分配；T0..T3 重载由代码生成/手写提供）
    public void ForEach<T0>(SpanAction<T0> body) where T0 : unmanaged;
    public void ForEach<T0, T1>(SpanAction<T0, T1> body) where T0 : unmanaged, T1 : unmanaged;
    public void ForEach<T0, T1, T2, T3>(...) where T0..T3 : unmanaged;

    // 逐 chunk 回调（需要"整块批处理/统计"时用）
    public void ForEachChunk<T0, T1>(ChunkAction<T0, T1> body) ...;

    // 并行：按 chunk 分片，**只允许纯计算**（不得结构化变更、不得写命令）
    public void ForEachJob<T0, T1>(SpanAction<T0, T1> body, int minChunkBatch = 1) ...;
}

public delegate void SpanAction<T0>(Span<T0> items);                       // 每个 chunk 调一次
public delegate void SpanAction<T0, T1>(Span<T0> a, Span<T1> b);
public delegate void ChunkAction<T0, T1>(Chunk<T0, T1> chunk, int count);
```

**迭代语义（必须实现成契约，测试要覆盖）：**

| 规则 | 说明 |
|---|---|
| 回调粒度 | **每个 chunk 回调一次**，参数是 `Span`；`Span.Length` 即本 chunk 有效实体数 |
| 写入 | 回调里允许**原地写组件**（`span[i] = ...`） |
| 结构化变更 | 回调里**禁止** `Create/Destroy/Set/Remove`（会搬移 chunk）；要改请写 `World.Commands`，帧末提交 |
| 实体寻址 | 需要 `Entity` 时，用 `chunk.Entities[i]`（`ReadOnlySpan<Entity>`）或先声明 `[Identity]` 字段 |
| 顺序 | 未承诺稳定顺序；需要确定性时（回放）用 **确定性 chunk 排序 + 固定并行分块**（§9 登记） |
| 并行 | `ForEachJob` 分片**由 chunk 边界确定**，与线程数无关 → 结果与单线程一致（只要回调无共享写） |

### 5.4 `SystemBase` / `SystemPipeline` / 分组

```csharp
namespace Game.Core.Ecs;

public abstract class SystemBase
{
    protected World World { get; }          // 由管线注入
    /// 每帧调用入口：内部必须是一次或几次 Query 的 for 循环，不得做分配
    protected abstract void OnExecute(float dt);
    public virtual bool IsEnabled => true;
}

[AttributeUsage(AttributeTargets.Class)] public sealed class UpdateInGroupAttribute : Attribute { public UpdateInGroupAttribute(UpdateGroup group); }
[AttributeUsage(AttributeTargets.Class)] public sealed class UpdateAfterAttribute : Attribute { public UpdateAfterAttribute(Type systemType); }
[AttributeUsage(AttributeTargets.Class)] public sealed class UpdateBeforeAttribute : Attribute { public UpdateBeforeAttribute(Type systemType); }
[AttributeUsage(AttributeTargets.Class)] public sealed class JobAttribute : Attribute { }   // 标记可并行/可给 native 批处理

public enum UpdateGroup
{
    PreUpdate,      // 输入采集、随机种子推进、命令缓冲准备
    FixedUpdate,    // 固定步长逻辑：移动/AI/抛射物推进（可确定性回放）
    Update,         // 每帧逻辑：技能冷却/生命周期/伤害结算/夺舍
    LateUpdate,     // 表现只读数据采集、实体位置插值参数
}

public sealed class SystemPipeline
{
    public SystemPipeline(World world);
    public T Add<T>() where T : SystemBase, new();          // 注册系统（反射读特性做排序，注册期一次）
    public bool TryGet<T>(out T system) where T : SystemBase;
    public void Execute(float dt, UpdateGroup group);        // 宿主按阶段调用
    public IReadOnlyList<Type> ExecutionOrder(UpdateGroup group);  // 调试/测试断言用
}
```

**排序规则（唯一口径）**：同一分组内，按 `[UpdateAfter]` / `[UpdateBefore]` 构成有向图做**拓扑排序**，同优先级按**注册顺序稳定排序**；存在环 → 构造管线时抛 `GameEcsException`（中文信息，指出环上的系统）。

> 规则7 判据：**新增一个系统 = 1 个新文件（`SystemBase` 子类）+ 1 行注册**；不得改任何已有系统。

### 5.5 `CommandBuffer`（结构化命令）

```csharp
namespace Game.Core.Ecs;

/// 帧内积累、帧末一次性提交的结构化命令。只允许主线程写、主线程提交。
public sealed class CommandBuffer
{
    public void Create(IReadOnlyList<ComponentType> components);          // 需要回读实体号时用 Deferred
    public Entity Deferred { get; }                                        // 占位实体，提交后才可寻址
    public void Add<T>(Entity e, in T value) where T : unmanaged;
    public void Set<T>(Entity e, in T value) where T : unmanaged;
    public void Remove<T>(Entity e) where T : unmanaged;
    public void Destroy(Entity e);
    public int PendingCount { get; }
    internal void Flush(World world);                                      // 仅 CommandFlushSystem 调用
}
```

- 提交顺序 = 写入顺序；同一实体"先 Destroy 后 Add"等冲突按顺序判定（后者对已销毁实体 **静默跳过并计数**，不抛异常 —— 防止 C# 结构化错误打断一帧）。
- 每帧提交后 `PendingCount == 0`（调试版断言，测试覆盖）。
- **命令只在帧末一次提交**是 J5 的执行方式，也是"遍历中途改世界"这个 bug 的根治手段。

### 5.6 `SpanIteration` / `WorldStats` / 异常

| 类型 | 契约 |
|---|---|
| `SpanIteration.SpanAction<T0..T3>` | 每 chunk 一次回调，`Span` 生命周期只在回调内 |
| `WorldStats` | `EntityCount / ArchetypeCount / ChunkCount / ComponentMemoryBytes`；`ARCHITECTURE.md` §9 的性能 HUD 直接读它 |
| `GameEcsException` | 内核唯一异常类型；结构误用（`Get` 未继承、job 内结构化变更、排序环）必须抛它，信息含实体/组件/系统名 |

---

## 6. 一帧的固定顺序（SystemPipelines 分组表）

```text
 每帧（60fps 预算 16.6ms，见 ARCHITECTURE.md §9.2）
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ PreUpdate       输入采集(IMovementDriver 写入 Velocity) → 随机源推进       │
 │                 → AI 决策写"意图"组件（不直接动世界结构）                  │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ FixedUpdate     MovementSystem(Transform3 += Velocity*dt)                  │
 │                 → ProjectileAdvanceSystem（扫掠检测，写命中事件）          │
 │                 → LifetimeSystem                                           │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ Update         DamageResolutionSystem（读命中→IDamageModifier 链→写 Health） │
 │                 → SkillTickSystem（冷却/施放）→ TakeoverSystem             │
 │                 → SpawnDirectorSystem（波次，写 CommandBuffer）            │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ LateUpdate     StatSyncSystem（Health ↔ StatPackage 槽位）                  │
 │                 → PresentationSystem（写表现只读 DTO，不做回写）           │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ Flush          CommandFlushSystem（本帧唯一结构化提交点）                   │
 │                 → 事件派发（DamageApplied / ActorKilled …）                 │
 │                 → 视图桥（TransformBridge / VisualBridge 推送到 Node3D）    │
 └──────────────────────────────────────────────────────────────────────────┘
```

- **顺序本身就是契约**：改写这个顺序 = 架构变更（回写本节 + 变更日志）。
- `CommandFlushSystem` 必须在 `LateUpdate` 之后、帧末之前运行；**视图桥不需要 ECS 层的 `UpdateGroup`——由宿主在 flush 之后调用**。
- 顿帧（HitStop）只改表现时间缩放与 `dt`，不跳过热区渲染；逻辑 `dt` 的影响由 `FixedFramePipeline` 统一处理（见 `ARCHITECTURE.md` §6.4）。

---

## 7. 引擎边界（宿主层设计）

### 7.1 宿主组成

| 组件 | 职责 | 禁止 |
|---|---|---|
| `WorldHost : Node3D` | 创建 `World`/`SystemPipeline`/`IRandomProvider`；在 `_Process`/`_PhysicsProcess` 里推帧；暴露 `World` 只读属性给宿主层 | 写业务逻辑、读 Excel、结算伤害 |
| `FixedFramePipeline` | 累积器 → 固定步长 `FixedUpdate`（可回放）；每帧一次 `PreUpdate/Update/LateUpdate/Flush` | 直接调用具体系统 |
| `TransformBridge` | 把 `Transform3` 推到 `ViewRegistry` 里的 `Node3D`（只读，带回插值/距离剔除） | 反写 ECS `Transform3` |
| `VisualBridge` | 按 `VisualSlot` / `CharacterDef` 从池里取/还节点，挂 `Node3D` | 判断"这是玩家还是怪物"（J2：只看 `IVisualDescriptor` 等价数据） |

### 7.2 `IActor` 在 ECS 上怎么落地（**显式登记的分层松绑**）

项目接口全集（`docs/rules/03-abstract-first.md` §4.1）把 `IActor` 声明在 `Game.Actor`，并要求 `Handle : IActor`。
ECS 下「Actor」不是对象而是 `Entity + ActorHandle`，因此：

| 项 | ECS-v1 落法 |
|---|---|
| 身份 | `ActorHandle(Entity)` —— `readonly record struct`，实现 `IActorView`（`Game.Core`） |
| `IActor.Id` | **由 `ActorId` 改为 `ActorHandle`**（破坏性变更，见 §11 变更日志） |
| `IActor` 实体化 | `IActor` 是**句柄 + 组件视图的适配器**，由 `Game.Host` 的 `Handles/` 生成（`IActorActor()` 每帧由 `Actors` 缓存），**不是每个实体一个对象** |
| 谁实现 `IActor` | **`Game.Host`**（唯一能同时看见 `Entity` 与 `Node` 的层）；声明位置仍在 `Game.Actor`（保持 §2.3 矩阵：`Game.Host` 可依赖 `Game.Actor`） |
| `IStatProvider` | `StatProviderAdapter` 读 `StatLayerSlots` → `Game.Data` 的不可变层栈（`GetLayer` 返回快照） |
| `IVisualDescriptor` | 读 `View`/`VisualSlots` 组件的旁路数据；`ApplySlot` 走 `VisualBridge` + `CommandBuffer` |
| `ISkillCaster` | 由 `SkillSystem` 驱动；`TryReplaceSkills` 写组件 `SkillSet`，夺舍中返回 false（`TakeoverState` 存在） |
| `TryApplyTakeover` | 由 `TakeoverSystem` 执行（写 `StatLayerSlots.BaseId`、`SkillSet`、`VisualSlots`、`Team`），**四者同一次提交** |

> **纪律**：除 `Game.Host/Handles/` 外，任何地方都**不得**把 `Entity` 包成对象再到处传；跨层只传 `ActorHandle`。
> 这条松绑必须在 `ARCHITECTURE.md` §2.3/§3.2 的 ECS 小节显式登记（本轮已写）。

### 7.3 视图登记表 `ViewRegistry`

```csharp
namespace Game.Host;

/// ECS 与场景树之间的唯一边界：ViewId(int) ↔ Node 实例，池化复用。
public sealed class ViewRegistry : IDisposable
{
    public int Rent(PackedScene scene);          // 从池取/实例化，返回 ViewId
    public void Return(int viewId);              // 入池（节点不销毁）
    public Node3D? Get(int viewId);              // 每帧热路径：只读
    public void SyncTransforms(World world, float alpha);   // 插值写 Node3D.GlobalTransform
}
```

- `ViewId` 存在 `View` 组件里（`scripts/core/ecs/components/View.cs`），内核不知道它是什么。
- 视图池的容量 / 预实例化数量走 `presentation.xlsx`（规则4）。

---

## 8. 与其它系统的接缝

| 接缝 | 约定 |
|---|---|
| **UI** | UI 只读 `Game.Core` 的只读视图接口（`IActorView`、`IHudSource`）；**不得**引用 `Game.Host` / 系统族；禁止直接读 `World` |
| **物理（Jolt）** | 逻辑命中在 ECS 里判定（`Hurtbox` + 空间哈希，后续）；Jolt 只用于**静态场景碰撞与角色贴地**，其回调通过 `CommandBuffer`/事件进 ECS，**不得在物理回调里结构化改世界** |
| **数据（Excel）** | 系统只读 `IConfigSource` 返回的 `*Def`；**组件里不放 Def 引用**（存 `DefId`，§4.1 D1） |
| **随机** | 系统构造函数**必须**从 `WorldHost` 注入 `IRandomProvider`（同 seed 同结果，`ARCHITECTURE.md` §10 R9） |
| **事件** | 系统只写 `CommandBuffer` + 追加事件到帧末事件队列；**订阅方**（飘字/成就/吸血）由 `Triggers`（Excel）或 `Game.Core.Events` 订阅，不触碰系统族 |
| **存档** | 存档只序列化「原型签名 + 每实体组件值」，见 §12 |

---

## 9. 性能路线（与 `ARCHITECTURE.md` §9.4 的阈值联动）

| 路线 | 触发条件（阈值唯一真源：ARCHITECTURE §9.4） | 做法 | 成本 |
|---|---|---|---|
| A `ForEach` 手写循环 | 默认 | 已内建零分配 | 0 |
| B `ForEachJob` chunk 并行 | 单系统 > 2ms 或实体 ≥ 200 且可并行 | `[Job]` 系统按 chunk 分片 | 1 个文件 |
| C **C++ 批量求解器** | 单帧热路径调用 > 10,000 或 Profiler 显示 C# > 50% | `native/` 实现 `IEcsBatchSolver`，C# 薄封装（`Game.Native` 只被 `Game.Combat`/ecs 内核引用） | 2~3 文件 |
| D 数据布局再调 | 实测 cache miss 高 | SoA 拆分、chunk 容量调整、AoS→SoA 分组 | 组件文件 |

**C++ 扩展点契约定死（本轮只登记，不实现）：**

```csharp
namespace Game.Combat;   // 允许 using Game.Native 的唯一门面
/// C++ 批量求解入口：入参是 chunk 级连续内存（指针+长度），与 §5.3 的 SoA 布局一致。
public interface IEcsBatchSolver
{
    int SolveProjectiles(IntPtr positions, IntPtr velocities, int count, float dt, IntPtr outHits);
}
```

> **纪律**：C++ 侧**不得**持有 C# 对象引用跨帧（`docs/rules/02-tech-selection.md` §4）；只接收裸内存 + 长度。

---

## 10. 测试（`tests/`）

| 测试组 | 必须覆盖 |
|---|---|
| ECS 内核 | 创建/销毁/世代号复用；组件继承迁移后**旧引用失效**（`Get` 拿不到旧数据）；`Query` 含/排除；`ForEach` chunk 边界（0/1/128/129）；`CommandBuffer` 顺序与冲突；管线拓扑排序与环检测抛出中文异常 |
| 确定性 | 同 seed 两次跑同一系统序列，**组件快照逐字节一致** |
| 零分配 | `Query.ForEach` 稳态分配 = 0（`GC.GetAllocatedBytesForCurrentThread` 前后差） |
| 分层守卫 | `Game.Core.Ecs` 源码里 0 个 `using Godot`（读源码断言） |

`dotnet build` 管主工程；测试工程 `tests/BiuBiuOrigin.Tests.csproj` 引用主工程（`<ProjectReference>`），`dotnet test` 一键跑（见 `tools/check_all.ps1`）。

---

## 11. 变更纪律（本 ECS 章节的收工动作）

1. **就地改本文件**对应小节 + 追加下表一行；
2. 同一次任务里**必须同步** `ARCHITECTURE.md` 的 ECS 小节（§2.3/§3.2/§6/变更日志）与文件头时间戳（规则07 §5）；
3. 改**组件字段形状 / 系统执行顺序 / 内核 API 签名** = **破坏性变更**，必须在表格摘要里写「**破坏性变更**」并说明兼容做法（迁移方法/默认值/旧字段映射）；
4. 卡片/进度以 §0.1 为唯一进度指标（☐ → ☑），并在 `ARCHITECTURE.md` §12 上同步增删对应行。

### 11.1 变更日志

| 日期 | 版本 | 任务 | 变更摘要 | 影响文件 |
|---|---|---|---|---|
| 2026-11-06 | ECS-v2 | 基础物件抽象（ARC §17） | **组件表扩容 + 铸造工厂的零分配约定**：① 新增内容域契约 `Game.Core.Content`（`ObjectArchetype` / `WeaponContract` / `ProjectileContract` / `ValueRef` / `ObjectBitMatrix`）——它们是**纯数据、进不进 chunk 都可**（目前放仓库、组件只存 int 槽位号，符合 J2）；② 新增战斗域组件 `WeaponState` / `ProjectileState` / `CastState`（`scripts/combat/CombatComponents.cs`，unmanaged、`[Identity]` 首字段、无方法）；③ **工厂约定**：结构化生成走 `CastQueue`（帧内队列）→ `CastSystem`(PreUpdate) → `EcsObjectFactory`；**稳态零托管分配**由 `GC.GetAllocatedBytesForCurrentThread()` 用例钉住（J6 的具体化：不 new `List`/不 LINQ/不装箱，`IReadOnlyList<Entity>` 用定长环形缓冲 + struct 枚举器实现）；④ §0.1 补第 9/10 行（内容契约 ✅、空间划分/宽相 ☐ 仍未做）；⑤ 判空纪律补一条：**`ContentRef` 的空引用判据是 `HasTarget`，不是 `DefId < 0`**（FNV-1a32 有符号哈希近一半为负，用符号判空会让近半内容静默消失，见 ARCHITECTURE §17.8 bug#1） | `docs/architecture/ECS.md`、`docs/architecture/ARCHITECTURE.md`、`scripts/core/Content/**`、`scripts/combat/**` |
| 2026-11-03 | ECS-v1 | ECS 架构立骨架（Lead 定稿） | 建立 ECS 技术路线与全部形状：自研数据导向 ECS（`Game.Core.Ecs`，archetype 列式存储 + unmanaged 组件 + chunk 迭代）、`SystemBase`+`SystemPipeline` 分组执行、`CommandBuffer` 帧末唯一结构化提交、`Game.Host` 宿主层（唯一 `using Godot` 处）、`ViewRegistry` 视图旁路、七条判定 J1~J7、§4.2 官方组件 v1 表、§6 一帧顺序、§9 性能四路线 + `IEcsBatchSolver` C++ 扩展契约；`IActor.Id: ActorId → ActorHandle` 记为**破坏性变更**（实现由 `Game.Host/Handles` 提供） | `docs/architecture/ECS.md`、`docs/architecture/ARCHITECTURE.md`、`docs/rules/03-abstract-first.md`、`docs/rules/06-extensibility.md`、`docs/rules/07-definition-of-done.md`、`config/architecture-rules.json`、`AGENTS.md` |

---

## 12. 待办 / 后续优化项（不阻塞本轮）

| # | 事项 | 触发时机 |
|---|---|---|
| 1 | Source Generator 生成 `Query.ForEach` 重载（现在手写 T0..T3） | 系统数量 > 15 或重载数不够时 |
| 2 | 确定性 chunk 排序 + 固定并行分块（回放/日种子榜） | 第一次做回放时 |
| 3 | 存档序列化（原型签名 + 组件值；带 schema 版本） | 第一条 `SaveService` 任务 |
| 4 | 空间哈希 / 宽相（命中检测从 O(n·m) 降为 O(n)） | **下一步就做**：物件契约已就位（`ProjectileState.Radius` / `Hurtbox.Radius` / 位矩阵），宽相只需要再加一个系统，不改契约 |
| 5 | `IEcsBatchSolver` 的 C++ 实现 | 达到 §9.4 下沉判据时 |
| 6 | 编辑器调试面板（原型/组件/系统计时可视化，`[Tool]`） | 第一次性能实测时 |
| 7 | 多世界（房间预加载/关卡流式） | 肉鸽流程落地后 |
