# ECS 架构师模块索引（`ecs`）

> 版本: v1 ｜ 建立日期: 2026-11-11 ｜ 产出任务: `t-ecs`（ECS 架构师模块索引）
> 性质: **索引 / 路由文档（只增不改）**。本文只回答「这个模块管什么、读哪几节、新增东西要改几个文件、超支了找谁」，
> **不复制原文契约**（签名、字段、阈值一律指向原文），也**不改动任何现有文档与代码**。
> 基线: [`../_shared/BOUNDARY.md`](../_shared/BOUNDARY.md)（17 份文档清点、五大模块边界、归属总表、只增不改纪律）。

---

## 1. 这个模块负责什么

- **说清「运行时的数据和执行怎么组织」**：什么该是实体、什么该是组件、什么该是系统；
  查询怎么写、结构性变更排在什么时候、引擎在哪个边界出现。
- 守住 **七条判定 J1~J7**（一票否决）+ **组件规范**（`unmanaged`、正好一个 `[Identity]`、无方法、无玩法数值）+ **一帧固定顺序**。
- 管 **ECS 内核（`Game.Core.Ecs`）与宿主（`Game.Host`）的形状**，以及 **C++ 批量求解扩展点**的契约（`IEcsBatchSolver`）。
- 管 **内容契约**（`ObjectArchetype` / `ValueRef` / `ForgeRules` / 位矩阵）与 **标签轴分层**在运行期的落地口径。
- 充当 **规则 5 §3.4（概念唯一契约）与规则 7（扩展预算）的裁决人**：组件齐不齐、新东西改几个文件，最终按本模块口径判。

3 行版：**别人写系统，我定形状；别人加内容，我算预算；别人改字段，我登破坏性变更。**

---

## 2. 我必读的文档

> 纪律（来自 `BOUNDARY.md` §2.3 / §5）：只写**章节号 + 一句话 + 相对路径**，不抄原文；行号会漂移，所以本表不写行号（对应 `BOUNDARY.md` §6 的 Q7）。

### 2.1 本模块主归属（必须通读）

| # | 文档 | 必读章节 | 为什么读 |
|---|---|---|---|
| 1 | [../../architecture/ECS.md](../../architecture/ECS.md)（519 行，**主真源**） | **全文** §0~§12 | ECS 形状唯一真源；§1 判定、§4 组件规范、§5 内核 API、§6 帧顺序、§7 引擎边界、§9 性能路线、§11 变更纪律都在这里 |
| 2 | [../../architecture/ARCHITECTURE.md](../../architecture/ARCHITECTURE.md)（3,228 行，**按节读，别通读**） | §0、§2.3、§3.2.0、§6 + §6.1.1、§9.2~§9.5、§10、§11 首屏、§12、§13（含 §13.5 踩坑）、§17、§19、§20、§21 | 唯一架构真源里属于 ECS 的那几节；§9.4 是**性能阈值唯一真源**，§13 是施工验收报告 |
| 3 | [../../rules/03-abstract-first.md](../../rules/03-abstract-first.md)（535 行） | **§3.4 概念唯一契约四问（A/B/C/D + §3.4.2 空值不许当"未配置"）**、§6.1 契约完备四问 C1~C4、§4.1 接口全集 v1、§4.1.1 签名对齐、§6.2 何时才抽 interface | 「同类产物必带同一套组件」是本模块判组件完备的直接依据 |
| 4 | [../../rules/06-extensibility.md](../../rules/06-extensibility.md)（509 行） | **§2 扩展预算表**、§3 E1~E5 五类扩展点、§4 夺舍走查（§4.4 结论 / §4.5 预算核算）、§6 版本兼容、§7 何时该重构、§8 扩展性登记表、§9.1 前置依赖、§10 审查 10 问 | 「新增一个 X 要改几个文件」是硬验收数字，§5 决策树全靠它 |
| 5 | [../../rules/05-numeric-package.md](../../rules/05-numeric-package.md)（811 行） | §3 层栈契约 + §3.3 Fold 加法语义、§3.4 双入口、§4 赋值矩阵、§5.4 枚举扩展流程、**§6.1 值语义硬规则 M1~M7 + §6.1.1 只读视图**、§7.1 两个唯一入口 + §7.3 表达式白名单、§8.1 骨架可见性、§8.2 三个"待落地"点 | 组件里的数值是**热路径副本**，权威在不可变层栈；`StatLayerSlots` 的槽位口径来自这里 |

### 2.2 跨模块必读（本模块也要守）

| 文档 | 必读章节 | 为什么 |
|---|---|---|
| [AGENTS.md](../../../AGENTS.md)（仓库根） | §0、**§1（第 ② 步之后那段 ECS 开工前置）**、§2 八条铁规、§3 红线、§5 目录契约、§7 收工 | 项目唯一入口；§1 明写「动手前必须读完 ECS.md §1 七条判定与 §4.2 组件类别表」 |
| [../../rules/07-definition-of-done.md](../../rules/07-definition-of-done.md) | §2 六步、§4.1 章节影响矩阵、§5 就地覆盖 SOP、§8.2 章节路由表 | 收工动作；改 ECS 就必然改 `ARCHITECTURE.md` 的 ECS 小节 |
| [../../rules/08-parallel-sessions.md](../../rules/08-parallel-sessions.md) | §0、§2 六步、§7 `ARCHITECTURE.md` 合并特例、§8 反例 | 内核 / 宿主是最容易被两个会话同时改的目录 |

---

## 3. 我只读一部分就够的（次归属：别人家的文档，但里面有我的判据）

| 文档 | 我读哪几节 | 用来干什么 |
|---|---|---|
| [../../rules/02-tech-selection.md](../../rules/02-tech-selection.md) | §2 判据 H1~H6 + §2.1 下沉流程、§2.3 阈值（**只引用不复制，数字去 ARCH §9.4 取**）、§4 GDExtension 约定 | 判「要不要下沉 C++」时，判据数字不在规则 2，在 ARCH §9.4 |
| [../../rules/04-data-driven.md](../../rules/04-data-driven.md) | §7 字段→Godot 类型映射、§7.2 `schema_version`、§9 V1~V9 校验 | 组件要存的是 `DefId` 而不是 `Def` 引用（ECS.md §4.1 D1），落表时按这里 |
| [../../rules/01-gdscript-ban.md](../../rules/01-gdscript-ban.md) | §2「UI 层」唯一定义 | 判定「UI 属于引擎边界内、和 host 同级」的依据 |
| [../../design/character-hud-contract.md](../../design/character-hud-contract.md) | §4 `[Module]` 拼接机制 | 系统族按模块装配的机制说明（落在 `SystemPipeline.AddModule`） |
| [../../prompts/task-prompt-template.md](../../prompts/task-prompt-template.md) | §3 开工必读清单、附录 B 接口清单快照 | 附录 B 只是快照，**权威在 ARCH §3** |

---

## 4. 七条判定 J1~J7：摘要与出处

> 全文（正确写法 / 打回写法两列）见 [ECS.md §1](../../architecture/ECS.md)。
> 下表是**给我自己复述用的摘要**，任何冲突以 ECS.md §1 原文为准。

| # | 一句话 | 违规长相（会被打回的） | 本模块怎么查 |
|---|---|---|---|
| **J1** | 实体是**身份键**（`index + generation`），不是对象 | `class Actor`、句柄里塞对象引用、`Dictionary<Type, object>` | 组件里必须有且只有一个 `[Identity] Entity`（`ComponentTypeRegistry` 在注册期校验）；跨层只传 `ActorHandle` |
| **J2** | 组件是**数据**，不是行为；必须 `unmanaged` | 组件方法、组件持有 `Node3D` / `Material` / `PackedScene` / `Resource` | ECS.md §4.1 硬约束 D1（不放引用）与 D2（禁 `fixed` / 指针 / 变长集合） |
| **J3** | 逻辑只活在 System 的 `for` 循环里 | 组件方法、`if (kind == ActorKind.Boss)` 类型分支 | 新增行为 = 新增 `SystemBase`，不许往组件加方法 |
| **J4** | 引擎只在**宿主层**出现（详见 §7） | `Game.Core` / `Game.Data` / 系统族里 `using Godot` | 测试工程硬守卫 + `tests/Layering/**` 源码扫描断言（见 §7） |
| **J5** | 结构性变更（Create/Destroy/Set/Remove）**只能主线程 + 帧末一次提交** | 遍历中途改世界、job 里写命令 | `CommandBuffer` + `CommandFlushSystem`；`ForEach` 回调里改结构 = 违规 |
| **J6** | 查询形状稳定，**按 chunk 批处理**，稳态零分配 | 每帧 `new List<T>`、LINQ、每实体 delegate | `Query.ForEach(SpanAction)`；零分配由用例用 `GC.GetAllocatedBytesForCurrentThread()` 钉住 |
| **J7** | 热点可 `[Job]` 并行，热点可下沉 C++ | 200+ 实体逐帧重计算却在 C# 回调里硬扛 | ECS.md §9 四路线 A/B/C/D，触发阈值去 ARCH §9.4 |

> 一句话总纲（ECS.md §1 末尾）：**「数据是 struct、逻辑是 System 循环、引擎在宿主层、结构变更排到帧末、热点可 job 可下沉」。**

---

## 5. 组件类别表与「新增组件的正确姿势」

### 5.1 先分类，再动手（ECS.md §4.1）

| 类别 | 运行时代表 | 存哪 | 可变性 | 例 |
|---|---|---|---|---|
| **静态配置**（Excel 唯一真源） | `*Def : Resource` | `data/generated/` → `IConfigSource` 缓存 | 只读 | `CharacterDef`、`WeaponDef`、`ProjectileDef` |
| **运行时可变状态** | **`unmanaged` struct 组件** | Archetype chunk（列式） | 每帧可改，但**只在 System 里** | `Transform3`、`Velocity`、`Health` |
| **视图/表现引用** | `Node` 引用 | **`View` 组件的旁路表 `ViewRegistry`**（`World` 之外，`scripts/host/`） | 只有视图桥写 | `Node3D`、`MeshInstance3D` |

两条硬约束（原文见 ECS.md §4.1 D1/D2）：
- **D1**：组件不得持有 `Resource` / `Node` / `Material` / `object` → 需要引用就存 `DefId` / `ViewId`（int）。
- **D2**：组件内禁 `fixed` 数组 / 指针 / 变长集合 → 需要 N 个同类数据 = N 个组件类型或独立实体。

### 5.2 组件按「归属层」分类（ECS.md §4.2 + 现行代码）

> ECS.md §4.2 的表是**契约表**，代码是落地。下表按**归属层**重排，方便回答「这个组件该放哪个目录」。
> 现行代码里的组件都在 [脚本 `scripts/core/ecs/components/OfficialComponents.cs`](../../../scripts/core/ecs/components/OfficialComponents.cs)（一个文件），
> 业务域组件在系统族目录里（见 ARCH §10 的 R13）。

| 归属层 | 放哪 | 现行组件 | 谁写 |
|---|---|---|---|
| **ECS 内核·官方** | `scripts/core/ecs/components/` | `Transform3`、`Velocity`、`Team`、`Lifetime`、`Health`、`StatLayerSlots`、`Rigid`、`TakeoverState`、`View`、`Hurtbox` + 后加的 `AimTarget`、`MoveIntent`、`ViewAppearance` | 生成系统 / 结算系统 / 视图桥（各组件写入方以 §4.2「谁写」列为准） |
| **战斗域** | `scripts/combat/` | `WeaponState`、`ProjectileState`、`CastState`、`Projectile` | 开火技能 / `ProjectileSystem` / `HitDetectionSystem` |
| **角色域（系统族）** | `scripts/actor/` | `ActorIdentity`、`Mana`、`SkillState`、`ActorIntent`、`ActorTag`、`WeaponStats`、`DashState` | 装配 / 意图 / 技能 / 冲刺系统 |
| **数据契约（不一定进 chunk）** | `scripts/core/Content/` | `ObjectArchetype`、`WeaponContract`、`ProjectileContract`、`ValueRef`、`ObjectBitMatrix` | 纯数据；组件里只存 int 槽位号 |

> **当前与契约的差异（别照代码改契约表）**：ECS.md §4.2 仍写「`Projectile` ☞ combat / `Hurtbox` ☞ combat」，而代码里这两者在官方组件文件中、
> 另有 `ProjectileState` 承担战斗域职责；ECS.md §4.2 记的组件总数也不等于现行 struct 数。**判断以 ARCH 为准前先看 ARCH §13.4**。
> 这些差异已登记在 §9.2「待核对」B2，本模块**不改原文**。

### 5.3 新增组件的正确姿势（照做即可，六步）

1. **先答「为什么必须是一个新组件」**：它是不是某个已有组件的一个字段（那就是改字段，走 §10 破坏性变更）？
   是不是"另一个概念"（那就按 §3.4 A 单一契约，找现成的那一套）？
2. **选归属层**：内核通用 → `scripts/core/ecs/components/`；业务域 → 对应系统族目录（`combat/` / `actor/` / `skill/` / `spawn/`）。
3. **写 struct 的三条硬纪律**（`OfficialComponents.cs` 文件头已逐条写明）：
   ① `unmanaged`，不持有任何托管引用（J2）；② **正好一个 `[Identity] Entity`，且必须是第一个字段**（J1 的执行方式，注册期校验）；
   ③ 结构里没有方法、没有玩法数值（数值来自 Excel，行为在 System）。
4. **命名**：裸 struct 名，**不加 `Component` / `Data` 后缀**（查询里会大量出现 `Query.Of<Rigid, Health>()`）。
5. **不注册、不改内核**：新增组件 = 新加一个 struct，**不需要动 `World` / `Query` / 组件注册表**（规则 7 在本层的直接体现）。
6. **回写契约表**：同一任务里把新组件加进 ECS.md §4.2（就地覆盖），**改字段形状同样必须回写 §4.2 + 变更日志**（存档/回放兼容性）。

> **组件齐备性（本模块的第二道闸）**：同一类产物（子弹 / 怪物 / 特效 / 武器）必须带**同一套组件**，哪怕某字段暂时是空值——
> 判据与真实事故见 [rules/03 §3.4](../../rules/03-abstract-first.md)，落法是把断言钉在**造实体的工厂层**，不是渲染层。
> 「有意为空」必须是一个**与所有合法值不撞的显式取值**，不许用 `0` / `""` / `null` 兼做两种含义（§3.4.2）。

---

## 6. 内核 API 要点 + 一帧固定顺序

### 6.1 内核 API 要点（签名级契约见 [ECS.md §5](../../architecture/ECS.md)）

| 类型 | 一句话 | 我要盯的点 |
|---|---|---|
| `Entity` / `ActorHandle` | `index + generation` 身份键；句柄是 `readonly record struct` | **句柄只在当前帧/下一帧有效**，跨帧缓存必须用 `IsAlive` / `Actors.TryResolve` 复核 |
| `World` | 实体/组件/原型的唯一拥有者 | `Get<T>` 未命中**抛 `GameEcsException`**（不返回 `default`，怕静默错误）；`Set<T>` 有则更新无则添加 |
| `Archetype` / `TypeSet` | 同组件组合的实体一块连续内存，按 chunk 分块 | 原型签名折叠**只有一处实现**（历史上插过「签名碰撞」的坑，见 §8 与 ARCH §13.5） |
| `Query` | 创建时匹配一次；`ForEach` 每 chunk 回调一次（`Span`） | 回调里**只允许原地写**；结构化变更一律写 `World.Commands`；`ForEachJob` 只允许纯计算 |
| `CommandBuffer` | 帧内积累、帧末一次提交 | 提交顺序 = 写入顺序；冲突（如先 Destroy 后 Add）按顺序判定，后到的**静默跳过并计数** |
| `SystemBase` | `OnInitialize()` 建 Query、`OnExecute(dt)` 只写 for 循环 | 系统实例**不能跨 World 复用**；`OnExecute` 内不得分配 |
| `SystemPipeline` | 注册 + 按 `[UpdateInGroup]` / `[UpdateAfter]` / `[UpdateBefore]` 拓扑排序 | 同优先级按注册顺序稳定排序；**成环 → 构造期抛中文异常**；`[Module("…")]` 用于按模块装配（`AddModule`） |
| `WorldStats` / `GameEcsException` | 统计 HUD 与唯一异常类型 | 性能 HUD 直接读 `WorldStats`；异常信息要带实体/组件/系统名 |

> **排序口径**：`[Module]` 让**"某个变体不注册某系统"**成为装配问题，而不是系统里写 `if (kind == Turret)`（J3 的正面用法，见 `MoveAimSystem` 类注释）。

### 6.2 一帧固定顺序（唯一实现处：`scripts/host/FixedFramePipeline.cs`）

```text
PreUpdate → FixedUpdate ×N → Update → LateUpdate → CommandFlush → 事件派发 → 视图桥
```

| 阶段 | 干什么 | 为什么必须在这 |
|---|---|---|
| `PreUpdate` | 输入采集、随机子流推进、命令准备 | 采集必须在所有决策之前，且不产生结构化变更 |
| `FixedUpdate ×N` | 位移积分、命中推进、推进类计时 | 吃**恒定 dt**；这是「同 seed 同结果」的唯一来源（不用引擎 `_PhysicsProcess` 的 delta） |
| `Update` | 计时、冷却、结算、投递生成/销毁命令 | 每帧一次，与固定步解耦 |
| `LateUpdate` | 数值同步、表现只读数据采集 | 在结算之后、提交之前，能读到本帧最终逻辑值 |
| `CommandFlush` | **本帧唯一允许结构化变更的时刻**（J5） | 所有 `Span` 已释放，迁移/新建 chunk 不会破坏遍历 |
| 事件派发 | 帧末统一派发 | 世界结构已稳定，订阅方看到的是一致快照 |
| 视图桥 | `VisualBridge` / `TransformBridge` 写 `Node3D` | 只读 ECS 数据，**不回写逻辑** |

**纪律**：这张图的顺序**本身就是契约**；改它就是架构变更（回写 ECS.md §6 + 变更日志）。补充见 ARCH §13.2。

> `FixedUpdate` 的补步有上限，超出的积压**直接丢弃**（`DroppedSeconds` 可观测）——宁可慢半拍，不要补帧死亡螺旋。

---

## 7. 引擎边界：唯一允许 `using Godot` 的 ECS 边界

### 7.1 契约（ECS.md §3 / §7 + ARCH §2.3）

```text
host (Game.Host)  ─┬─▶ ecs-core (Game.Core.Ecs)   ✓ 唯一允许 using Godot 的 ECS 边界
                   ├─▶ combat / actor / skill / spawn（系统族装配）
                   └─▶ data (Game.Data)
系统族 ─▶ Game.Core.Ecs + Game.Data      ✗ 不得 using Godot、✗ 不得用 Node / Resource 类型
Game.Core.Ecs ─▶ 仅 .NET BCL             ✗ 不得 using Godot / Game.* / 第三方包
Game.UI ─▶ Game.Core（只读视图）          ✗ 不得 using Game.Host / 系统族 / Native.*
```

- **一句话**：`Game.Host` 是**唯一**允许 `using Godot` 的 ECS 边界；`Game.Core.Ecs` 必须能**裸测**（不启引擎、无第三方包）。
- `Game.UI` 在引擎边界内（HUD 用 `Control` 画），但约束变成「**不得 using 业务层**」，不是「不得 using Godot」——
  这条口径的原文在 [rules/01 §2](../../rules/01-gdscript-ban.md)，守卫实现在 `tests/Layering/LayerDependencyTests.cs`。
- 宿主层「唯一」的实际含义：**`scripts/host/**` 与 `scripts/ui/**` 两处**（UI 是表现层，与 host 同级），其余逻辑目录必须纯 .NET。
  这是 ECS.md §3 的宿主口径与 §7 宿主设计的**现行实现**，两者字面不矛盾。

### 7.2 宿主层组成部分（ECS.md §7.1，任何一段越界都算违规）

| 组件 | 职责 | 禁止 |
|---|---|---|
| `WorldHost`（节点） | 建 `World` / `SystemPipeline` / 随机源，推帧 | 写业务逻辑、读 Excel、结算伤害 |
| `FixedFramePipeline` | 累积器 → 固定步长，每帧推七个阶段 | 直接调用具体系统 |
| `TransformBridge` | `Transform3` → `Node3D`（插值/剔除） | **反写** ECS 的 `Transform3` |
| `VisualBridge` | 按外观数据从池里租/还节点 | 判断「这是玩家还是怪物」（只看等价数据） |
| `ViewRegistry` | `ViewId(int)` ↔ `Node` 实例，池化复用 | 让 ECS 侧知道 `Node` 的存在 |

### 7.3 `View` 组件为什么是 int 而不是节点引用

组件只存 `int ViewId`（+ 池种类 + 脏标记），节点引用全部落在 `ViewRegistry` 旁路表里。
这条是 J2/J4 的联合约束：组件一旦持有 `Node`，chunk 会被 GC 扫描、数据层反向依赖引擎层。

---

## 8. 决策树：新增系统 / 组件 / 系统族要改几个文件？超预算怎么申诉？

### 8.1 三种典型新增

| 你新增的是 | 通常要动什么 | 预算口径（出处） |
|---|---|---|
| **一个新系统** | `1` 个新文件（`SystemBase` 子类）+ 在装配处注册（宿主/组合根里的一行） | ECS.md §5.4 的规则 7 判据：**1 个新文件 + 1 行注册，不得改任何已有系统** |
| **一个新组件** | `1` 个新 struct（内核通用放 `components/`，业务域放系统族目录）；**内核 0 行改动**；外加回写 ECS.md §4.2 | ECS.md §4.2 尾注：「新增组件 = 新加 1 个文件（不需要改 `World`/`Query`/注册表）」 |
| **一个新系统族**（如 `scripts/loot/`） | 目录 + N 个系统各 `1` 个文件 + 在装配清单里挂模块 + 分层矩阵/目录契约登记 | rules/06 §3 E3「1 文件」按**单个扩展点**计；整族属于「多文件」→ 走 §8.3 的申诉 |

> 三个判断先做：① 这是**行为**（→ 系统）还是**状态**（→ 组件）还是**新一套产物**（→ 内容契约 + 工厂）？
> ② 同一件事已经有写入入口了吗（§3.4 A 单一契约）？③ 新产物带的东西和老的**一模一样**吗（§3.4 B 齐备原则）？

### 8.2 完整决策树

```text
新增需求进来
│
├─ Q1 能不能用数据表达（Excel 加行 / 资源互相引用）？
│    └─ 能 → E1 / E2，0 代码文件。**先看 rules/06 §9.1：前置件没落地时只能如实写"当前实际 N 文件"**
│
├─ Q2 需要新的「行为」吗？
│    ├─ 是 → 新 SystemBase 子类（1 新文件 + 1 行注册），不改已有系统
│    │        需要新状态 → 加组件（1 新 struct，内核 0 行改动）
│    └─ 否 → 继续
│
├─ Q3 需要新的「策略」吗（新技能种类 / 新伤害修正 / 新移动驱动）？
│    └─ 是 → E3：实现接口（1 文件）+ Excel 按名登记；**不许**在 `combat/**` 里加分支
│
├─ Q4 需要「在管线外挂一段逻辑」吗（吸血 / 护盾 / 成就）？
│    └─ 是 → E4 事件/钩子：1 个订阅文件；**`Triggers` 表未落地前不得声称 0 代码**（rules/06 §9.1）
│
├─ Q5 是热路径且 C# 已到极限吗？
│    └─ 按 ARCH §9.4 的阈值判定（A ForEach → B ForEachJob → C C++ 批量求解器 → D 数据布局）。
│       只有触发阈值才走 E5/C，成本 2~3 文件，且 `IEcsBatchSolver` 契约已定死，C++ 侧不得持有 C# 对象引用跨帧
│
└─ Q6 文件数超预算了吗？
     └─ 是 → §8.3 的处理顺序 + 申诉，**不许硬写**
```

### 8.3 超预算的处理顺序（不得跳步，rules/06 §2 原文口径）

1. **先改抽象**（回到规则 5）：这个变化点该有一个接口 / 数据字段吗？
2. **再改数据**：能不能用 Excel 字段 + 组合表达？
3. **最后才新写文件**，并**在 rules/06 §8 扩展性登记表登记「这次为什么必须新写」**（说明为什么 E1/E2 不够 + 未来变体）。

**申诉（本模块作为裁决人时走的路径）**：

| 步骤 | 做什么 | 证据 |
|---|---|---|
| 1 | PR 描述里写清「新增一个 X 要改几个文件」，并**对照 rules/06 §2 预算表** | 数字 + 文件清单 |
| 2 | 若超预算，先按上面 ①②③ 自查一遍，把排查过程写进 PR | 「为什么 E1/E2 表达不了」 |
| 3 | 填 **rules/06 §8 扩展性登记表**（日期 / 任务 / 扩展点 / 类别 / 新增文件数 / 为什么 E1/E2 不够 / 未来变体） | 表格一行 |
| 4 | 跑 **rules/06 §10 审查 10 问** + **rules/03 §6 抽象审查 10 问 + §6.1 契约完备四问 C1~C4**，任一打叉必须写理由 | 勾选记录 |
| 5 | 若是**破坏性变更**（组件字段形状 / 系统执行顺序 / 内核 API 签名）→ 按 ECS.md §11 标「**破坏性变更**」并写兼容做法，同事务更新 ARCH 的 ECS 小节与变更日志 | 变更日志一行 |
| 6 | 预算被**连续违反 ≥ 2 次** → 触发 rules/06 §7「超预算回收」：停止加功能，先做抽象整改 | 重构任务 |

> 一句话：**超预算不是"申请更多文件"，而是"证明抽象位置错了"**——rules/06 §1 的原文就是这句话。

---

## 9. 技术债与待办

### 9.1 摘录（每条都标出处；原文数字一律以出处为准）

| # | 事项 | 出处 | 备注 |
|---|---|---|---|
| D1 | Source Generator 生成 `Query.ForEach` 重载（现在手写 T0..T3） | ECS.md §12 #1 | 触发：系统数 > 15 或重载不够 |
| D2 | 确定性 chunk 排序 + 固定并行分块（回放 / 日种子榜） | ECS.md §12 #2 | 触发：第一次做回放 |
| D3 | 存档序列化（原型签名 + 组件值 + schema 版本） | ECS.md §12 #3 | 触发：第一条 `SaveService` 任务 |
| D4 | **空间划分 / 宽相**（命中检测 O(n·m) → O(n)） | ECS.md §12 #4 + §0.1 第 10 行 | **「下一步就做」**：物件契约已就位，只需再加一个系统、不改契约 |
| D5 | `IEcsBatchSolver` 的 C++ 实现 | ECS.md §12 #5 + §0.1 第 8 行 | 触发：达到 ARCH §9.4 下沉判据（对应 ARCH §10 R15） |
| D6 | 编辑器调试面板（原型/组件/系统计时可视化） | ECS.md §12 #6 | 触发：第一次性能实测 |
| D7 | 多世界（房间预加载 / 关卡流式） | ECS.md §12 #7 | 触发：肉鸽流程落地 |
| D8 | 官方组件全堆在一个文件（`OfficialComponents.cs`） | ARCH §10 **R13**（有意为之） | 组件数 < 50 保持现状；超过后按用途拆子目录 |
| D9 | `EntityDestroyed` 事件**只有声明没有生产者**，`VisualBridge.QueueRelease` 是死代码；宿主用 `IsAlive` 复核兜底 | ARCH §12 最后一行（P3） | 正解：内核销毁成功时发一次事件 |
| D10 | `BiuBiuOrigin.csproj` 需手工维护（引擎升级要跟 SDK 版本） | ARCH §10 **R14** | 改此文件后必须 `dotnet build` 验证 |
| D11 | 性能基线与实体上限仍是**假设值** | ARCH §10 **R10** | 首次性能任务实测后回填 ARCH §9.5 |
| D12 | M6「只读视图」在骨架里只是文档约定，**编译期强制**要等落地 | rules/05 §8.2 三个"待落地"点 | `StatDefinitionTable` / `ISkill` 引用 / 诊断通道 |
| D13 | E4 类扩展（事件/钩子）的「0 代码」承诺**尚未成立** | rules/06 §9.1 | `Triggers` 表未落地时如实写"当前 N 文件" |

### 9.2 待核对（本次只记录，不修改任何原文，提交整合阶段裁决）

| # | 现象 | 现判 | 影响 |
|---|---|---|---|
| **B1** | 规则编号命名不一：`AGENTS.md` §2 铁规表的第 5 行是「规则 5」但全文链接指向 `docs/rules/03-abstract-first.md`，第 4 行是「规则 4」指向 `rules/04`；`rules/03` 文内自称「规则 5」、`rules/05` 自称「规则 6」、`rules/06` 自称「规则 7」 | 两个编号体系并存（按文件序号 NN / 按规则序号）。本文一律写**文件名** `rules/NN`，避开歧义 | 跨文档引用时容易指错文件 |
| **B2** | ECS.md §4.2 组件表与代码不符：表里 `Projectile` / `Hurtbox` 的「归属层」写 combat，代码里两者在官方组件文件 `scripts/core/ecs/components/OfficialComponents.cs`；`ProjectileState`（在 `scripts/combat/CombatComponents.cs`）在 §4.2 没有行；§4.2 另有 `View` 写「host 旁路 / 归属 `ViewRegistry`」；ARCH §13.4 仍写「11 个官方组件」 | 以**现行代码 + ARCH §13.4** 为事实、ECS.md §4.2 为待回写契约（ECS.md §4.2 尾注本来就要求「落地时以代码为准并回写本节」） | §5.2 的归属层表格只做导航，不是第二真源 |
| **B3** | 「唯一允许 `using Godot` 的边界」有两种字面口径：ECS.md §3 目录 / §7 写 `scripts/host/`，而 ECS.md §10 的分层守卫与 `tests/Layering/LayerDependencyTests.cs` 实际允许 `scripts/host/**` **与** `scripts/ui/**`（UI 在引擎边界内，约束是「不得 using 业务层」） | 实际操作口径 = host + ui；「唯一」指的是 **ECS 侧**唯一 | 新人容易理解成「ui 目录里也不许用 Godot」 |
| **B4** | ECS.md §6 一帧图里点名的若干系统（如伤害结算 / 数值同步 / 夺舍 / 技能 tick / 表现）目前**代码里没有同名 `SystemBase`**，同职责由 `combat/` / `actor/` / `skill/` 里的既有系统承担 | §6 是**形状契约**（阶段与顺序），不是系统清单 | 别把 §6 当"还有这些系统没写"来排期 |
| **B5** | ECS.md 文件头「最后更新 2026-11-06 / v2」，ARCH 已到 v17；ECS.md 内部示例（如 `ForEach` 重载 T0..T3）与现行 `Query.cs` 可能有出入 | 发现就记，不由本任务改 | ECS.md 的时效性需一次专任务校准 |
| **B6** | rules/06 §9.1 的「前置状态（截至 2026-11-03）」已明显过时（E1/E2 需要的 Excel + 生成器已落地） | 引用预算数字前先按现实核对前置件，别照抄表里的 ❌ | 影响「新增一个 X 改几个文件」的诚实口径 |
| **B7** | `docs/modules/<模块>/` 目录名：`BOUNDARY.md` §2.1 写 `docs/modules/ecs/`，本次任务指定 `docs/modules/ECS架构师/` | 本 README 按任务指定的中文目录名落地；若整合阶段统一改成 `ecs/` 只需移动文件 | 交叉链接 |
| **B8** | `config/architecture-rules.json`（架构守卫的唯一放行渠道）里**没有** `using Godot` 的规则项，J4 目前靠**测试工程编译期失败 + `tests/Layering` 源码扫描**守卫，而不是守卫脚本 | J4 是"双守卫"：测试硬守卫 + 人工评审（ECS.md §10） | 别以为守卫脚本会报 J4 违规 |

---

## 10. 常见任务 → 该读哪一节（路由表）

| 我要做的事 | 先读 | 再读 | 收工必须做的 |
|---|---|---|---|
| 新增一个玩法系统 | ECS.md §5.4（`SystemBase` / 排序规则）、§6 帧顺序 | rules/06 §2 预算 | 1 新文件 + 1 行注册；§8 登记表；ARCH 变更日志一行 |
| 新增一个组件 | ECS.md §4.1（三类数据 + D1/D2）、§4.2（命名铁律 + `[Identity]`） | rules/03 §3.4（齐备/单一契约） | 回写 ECS.md §4.2；若改字段形状 = 破坏性变更 |
| 加一个系统族（新目录） | ECS.md §3（目录契约 + 允许依赖） | ARCH §1 / §2.3 | 目录契约 + 分层矩阵登记；扩展性登记表 |
| 改内核 API 签名 / 改执行顺序 | ECS.md §5 / §6、§11 变更纪律 | ARCH §13.5 踩坑记录 | **破坏性变更**标注 + 兼容做法；同事务改 ARCH |
| 决定要不要并行 / 下沉 C++ | ECS.md §9（四路线与成本）、§7（边界） | ARCH §9.2~§9.4（**阈值唯一真源**） | 实测数据回填 ARCH §9.5 |
| 判断组件「齐不齐」 | rules/03 §3.4 四条硬规则 + §6.1 四问 | 工厂层用例（`tests/**/Factory/**`） | 断言钉在**造实体**那一端 |
| 数值该放组件还是层栈 | ECS.md §4.3、rules/05 §3 / §6.1（值语义 + 只读视图） | ARCH §5 | 组件只放热路径副本；权威在不可变层栈 |
| UI 想读运行时数据 | ECS.md §8「UI 接缝」 | ARCH §8 | 只读视图接口，禁引 `Game.Host` / 系统族 |
| 物理回调要改实体 | ECS.md §8「物理接缝」 | ARCH §6.4 | 一律走 `CommandBuffer` / 事件，不在物理回调里结构化改世界 |
| 想加第二个"造实体入口" | rules/03 §3.4 A / §3.4.3 | 现有工厂（`ContentSpawner` 一类） | 收敛到唯一入口，否则 PR 里写明为什么必须两份 |

---

## 11. 我不管什么（Out + 交界处）

- **不管 Excel 里的数值与行**（策划）；**不管房间怎么排、朝向多少度**（地编）；**不管角色的技能与 HUD 内容**（角色）。
- **不管构建脚本、架构守卫实现、CI**（开发）；发现 J4 之类需要机器化的检查，提需求给开发，不自己改 `tools/`。
- **不管渲染 / shader / 特效参数**（开发）；只规定「渲染不许反写逻辑」。
- **不新建平行架构文档**（红线：不许 `ARCHITECTURE_v2.md` 之类；本文件只是索引，不描述架构形状的新定义）。

**交界处三条（最容易越界的地方）**：

1. **与开发**：破坏性变更（组件字段形状 / 系统顺序 / 内核签名）→ 两条纪律同时生效：ECS.md §11 的「破坏性变更」标注 + `rules/07` 的就地覆盖与变更日志。
2. **与策划**：`ECS.md §4.1 D1`（组件不放 `Def` 引用、只存 `DefId`）决定「表里加一列要不要改代码」，是策划改表前的第一道闸。
3. **与角色**：角色的一切行为都必须是「新增一个 `SystemBase`」——这条判定的原文在 ECS.md §1 J3，不由角色模块另立规矩。

---

## 12. 变更日志（本文自身）

| 日期 | 版本 | 任务 | 变更摘要 |
|---|---|---|---|
| 2026-11-11 | v1 | `t-ecs` ECS 架构师模块索引 | 建立：模块职责（§1）、必读/次归属（§2~§3）、J1~J7 摘要与出处（§4）、组件分类 + 新增组件六步（§5）、内核 API 要点 + 一帧七阶段（§6）、引擎边界（§7）、新增系统/组件/系统族的预算决策树 + 超预算申诉路径（§8）、技术债 D1~D13 与待核对 B1~B8（§9）、任务路由表（§10）、Out 与交界处（§11）。**未修改任何现有文档与代码。** |
