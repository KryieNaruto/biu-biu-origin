> 状态: v1  最后更新: 2026-11-03

# 规则 6 · 数值包（人物/怪物封装成包，可互相赋值）

## 1. 规则原文

> 「人物，怪物等数值封装成包，能够互相赋值，加强后期的可扩展性玩法！」

**一句话落地**：所有实体（玩家、怪物、Boss、召唤物、可破坏道具）的数值都是同一个类型 `StatPackage` 的不同**层**组合；"换一身数值"= 换掉其中一层，而不是换掉一个类型。因此**任意两个包的同一层可以互相赋值**，这是"夺舍"这类玩法的类型学前提。

---

## 2. 为什么「包 + 可互相赋值」是硬需求

| 需求 | 如果没有包会怎样 |
|---|---|
| 夺舍/寄生/变形（本项目的验收场景） | 玩家和怪物是两个类 → 赋值要用 `is/as` 转换 → 战斗层到处 `if (isMonster)`（违反规则 5 / 规则 7） |
| 装备/词条/等级/Buff 同时作用 | 每个系统各改一份"当前属性"字段 → 计算顺序随机、互相覆盖、无法回滚 |
| 存档 / 回放 / 预测 | 可变状态满地都是 → 只能全量快照，且无法确定性重演 |
| 平衡调参 | 数值散在多个类里 → 改一个漏三个（违反规则 4） |
| 数据驱动 | Excel 的 `stats` 列没有对应的运行时载体 |

**核心判断**：肉鸽的可玩性来自**组合**，组合的前提是"同一份数值可被多种来源叠加、可被整体替换、可被快照"。这三件事只有**值语义 + 分层 + 统一读口**能同时满足。

---

## 3. 怎么做：分层模型

### 3.1 层栈（自上而下 = Fold 的从低到高）

```text
   ┌───────────────────────────────────────────────────────────────┐
   │ Temporary  临时层  ← 限时技能修饰、场景效果（「中毒中」「狂暴地带」）│  每层一个 StatPackage
   ├───────────────────────────────────────────────────────────────┤
   │ Buff       增益层  ← 可叠加 buff 栈（每层一个包，按 stack 次数）  │
   ├───────────────────────────────────────────────────────────────┤
   │ Equipment  装备层  ← 武器/护甲/饰品/遗物各自的包                  │
   ├───────────────────────────────────────────────────────────────┤
   │ Growth     成长层  ← 等级点、升级选择、升华、永久词条            │
   ├───────────────────────────────────────────────────────────────┤
   │ Base       基础层  ← 来自 CharacterDef（Excel Characters 表）     │  ← 夺舍替换的就是这一层
   └───────────────────────────────────────────────────────────────┘
                                │
                                ▼
              Final = Calculator.Fold(Base, Growth, Equipment, Buff, Temporary)
                                │
                                ▼
                     IStatProvider.Current  （只读）
                                │
                                ▼
              StatCalculator（算 Final 值） / DamageResolver（算伤害）
```

### 3.2 每层的契约（谁写 / 可变性 / 夺舍行为）

| 层 | `StatLayer` | 来源 | 数量 | 可变性 | 谁写 | 夺舍时的行为 |
|---|---|---|---|---|---|---|
| Base | `0` | `CharacterDef.Base`（Excel `Characters.Base` 列） | 恒 1 个 | 不可变值 | 数据任务 / 装配 | **被整体替换**为目标怪的 Base（`AssignFromWithDefaults`） |
| Growth | `1` | 玩家成长数据（`RunProgression`） | 恒 1 个 | 不可变值 | 角色任务 | **保留**（玩家的成长永远是玩家的） |
| Equipment | `2` | 装备槽求和后的一个包 | 恒 1 个（内部由多件合成） | 不可变值 | 装备任务 | **保留** |
| Buff | `3` | Buff 栈（可 0..N 个包） | 0..N | 不可变值栈 | 战斗/技能任务 | **按 `BuffDef.KeepOnPossess` 决定**：true 保留、false 丢弃 |
| Temporary | `4` | 场景/技能临时修饰 | 0..N | 不可变值 | 技能/战斗任务 | **保留**（并在夺舍结束后按原时长继续倒计时） |

> 层的**顺序是契约**，写在 `enum StatLayer : byte { Base=0, Growth=1, Equipment=2, Buff=3, Temporary=4 }`。任何新层只能追加在末尾（5、6…），**禁止插入中间**（会让既有公式与存档错位）。

### 3.3 Fold 的加法语义（要害）

每一层存的是 **Delta（差量）** 而不是绝对值 —— 除了 Base 存绝对值。Fold 的默认实现是：

```text
Final[id] = Base[id]
          + Σ GrowthDelta[id]
          + Σ EquipmentDelta[id]
          + Σ BuffDelta[id]
          + Σ TemporaryDelta[id]
```

| 字段类别 | 叠加方式 | V1 落地 |
|---|---|---|
| 资源型（Hp、HpMax、MoveSpeed、Armor、Ammo 上限） | **加法** | ✅ 默认加法 |
| 百分比型（MoveSpeedMul、DamageMul、CritChance） | **加法后乘法**；使用 `*Mul` 后缀命名，默认 1.0，层内 Delta 记 `+0.2` | ✅ 用后缀 + `StatKind` 表区分 |
| 比值型（CritChance 0~1） | 加法后 clamp 到 `[0, 容量]` | ✅ 由 `StatDefinition` 声明 |
| 绝对值覆盖型（`Override=true` 稀有字段） | 后层覆盖前层 | ❌ v1 不做，进 `progression.xlsx` 的 `Formula` Sheet |

> **公式由数据驱动（规则 4）**：Fold 每层"怎么叠"由 `StatDefinition`（来自 Excel `StatDefs` 表）的 `FoldKind` 列决定：`Add` / `Mul` / `AddThenClamp` / `Max` / `Min`。**代码里不出现任何每属性的 if 分支**。这保证了"新增一个属性"= Excel 加一行（规则 7 的"0 个文件"）。

### 3.4 双入口：StatCalculator 与 DamageResolver

| 入口 | 职责 | 输入 | 输出 | 位置 |
|---|---|---|---|---|
| `StatCalculator` | 把层栈折成一个 `StatBlock`（Final） | `StatPackage[]` + `StatDefinition[]` | `StatBlock` | `Game.Data`（`scripts/data/`） |
| `DamageResolver` | 一次伤害结算 | `DamageEvent` + `IStatProvider` 双方 | `DamageResult` | `Game.Combat`（`scripts/combat/`） |

**禁止**在别处出现 `* 1.5f`、`+ 10`、`Mathf.Max(hp, 0)` 之外的数值运算（GS005 会报）。所有倍率/加成必须来自 `StatId` 或 `progression.xlsx` 的 `Formula` Sheet。

---

## 4. 互相赋值规则（**本文档最重要的契约**）

### 4.1 同 schema / 跨 schema 的定义

| 概念 | 定义 |
|---|---|
| **schema** | 一层包"包含哪些 `StatId`"的集合签名。`StatPackage.Schema.Id` 是稳定字符串（如 `"base.v1"`、`"base.elite.v1"`）。 |
| **同 schema** | 两个包 `Schema.Id` 完全一致，且 `StatId` 集合完全一致 |
| **跨 schema** | `StatId` 集合不同（精英怪多了 `ShieldMax`；Boss 多了 `PhaseCount`；某武器包只有 3 个属性） |
| **schema 版本** | `Schema.Version` 递增；只能"加 stat"或"标记 deprecated"，禁止删改语义 |

### 4.2 赋值矩阵（**实现必须严格照此**）

| 操作 | 前提 | 语义 | 抛异常？ | 典型用途 |
|---|---|---|---|---|
| `AssignFrom(other)` | **同 schema** | 整包元素级替换，`this` 视为被整体覆盖 | ❌ 不抛；跨 schema 调用**断言失败**（Debug 断言 + Editor 报错，Release 降级为 `AssignFromWithDefaults`） | Base←Base（夺舍同族）、装备槽内替换 |
| `AssignFromWithDefaults(other, defaults)` | 任意（含跨 schema） | 对 `other` 有的每个 `StatId` 取 `other` 的值；`other` **没有而 `this` 有**的取 `defaults`（通常是该层初始/零增量）；`other` 有而 `this` 没有的 → **保留为未知属性**（见 4.4） | ❌ **绝不抛异常** | Base←Base（夺舍跨族，如玩家夺舍 Boss） |
| `Combine(layers)` | 任意 | 按 `StatLayer` 顺序 Fold 成 Final `StatBlock` | ❌ | 每帧/每次属性变化后重算 |
| `Delta(from, to)` | 任意同 schema | 返回 `to - from` 的稀疏包 | ❌ | 存档、回放、UI 对比、动画插值 |
| `With(StatId, float)` | 任意 | 返回**新包**，单条覆盖 | ❌ | 运行时单点修改（如"血药 +20"） |
| `ToDictionary()/FromDictionary()` | — | 存档序列化（含未知键） | ❌ | 存档 |

### 4.3 跨 schema 的取值规则（三句话记牢）

```text
① 对方有的 → 用对方的（这是"赋值"的本意）
② 对方没有、我有的 → 取默认值（defaults，通常是 0 增量 / 层初始值）
③ 对方有、我没有的 → 记为未知属性，原样保留，不参与本层计算（见 4.4）
```

**为什么"取默认"而不是"保留我原来的值"**：夺舍的本意是"我变成了那个怪我"。若怪物没有 `CritChance`，而我保留玩家原来的 5%，那等于玩家凭空获得一个优势；平衡上不可控，且容易在 demo 里被误判为"夺舍太强"。**取默认 = 夺舍是彻底的置换**。

**"取默认"的默认值从哪来**：由调用方显式传入 `StatBlock defaults`（通常取 `StatDefinition.DefaultOf(id)`，来自 Excel `StatDefs` 表的 `Default` 列）。**不允许**在代码里 `new StatPackage()` 再猜。

### 4.4 未知属性（UnknownStats）策略

| 环节 | 规则 |
|---|---|
| 读取 | 保留在 `UnknownStats` 里（`Dictionary<string, float>`），**不参与 Fold** |
| 写入 | 忽略（不报错、不丢弃） |
| 序列化 | **带上**（`ToDictionary` 输出，`FromDictionary` 还原） |
| 为什么 | 跨版本存档 / 跨内容包（DLC 新属性）不炸；旧存档在新版本里能读、能存、能继续玩 |

```csharp
// 反例：绝对不要这样写
if (!other.Contains(StatId.ShieldMax))
    throw new InvalidOperationException("schema mismatch");   // ❌ 夺舍直接崩

// 正例
var merged = mine.AssignFromWithDefaults(theirs, defaults);   // ✅ 缺的取默认，多的保留
```

### 4.5 跨 schema 赋值的可视化示例（夺舍 Boss）

```text
玩家的 Base 包:            { Hp=100, MoveSpeed=6.0, CritChance=0.05, Ammo=12 }   schema="base.player.v1"
玩具王的 Base 包:          { Hp=1800, MoveSpeed=2.0, PhaseCount=3 }              schema="base.boss.v1"

AssignFromWithDefaults(boss, defaults)

结果 Base 包:              { Hp=1800, MoveSpeed=2.0, PhaseCount=3,
                             CritChance=0.0 ← 取默认,
                             Ammo=0        ← 取默认（Boss 用武器时由武器层补） }
UnknownStats:              { }        （boss 专有属性 PhaseCount 已在 new schema 里登记）
```

> `PhaseCount` 若未被玩家 schema 声明，则进入 `UnknownStats`，由技能在需要时通过 `GetExtra("PhaseCount")` 读取。**这保证"怪物有、玩家 schema 没有"的属性不会丢**。

---

## 5. StatId 枚举与双端映射

### 5.1 StatId 设计（只加不改）

```csharp
namespace Game.Data;

/// 属性 id。**只加不改**：禁止删除、禁止重排、禁止改变已有成员含义。
/// 数值 = 存档/网络的稳定编码，一旦发布永不改变。
public enum StatId : ushort
{
    None = 0,

    // [1..99] 生存
    Hp = 1,
    HpMax = 2,
    HpRegenPerSec = 3,
    ArmorFlat = 4,
    DamageReduceRatio = 5,
    ShieldMax = 6,
    ShieldRegenPerSec = 7,
    HurtInvulnTime = 8,

    // [100..199] 移动
    MoveSpeed = 100,
    MoveSpeedMul = 101,
    Acceleration = 102,
    TurnSpeed = 103,
    DashCooldown = 104,
    DashDistance = 105,

    // [200..299] 攻击
    Damage = 200,
    DamageMul = 201,
    CritChance = 202,
    CritMultiplier = 203,
    AttackSpeedMul = 204,
    FireInterval = 205,
    ReloadTime = 206,

    // [300..399] 投射物
    ProjectileSpeed = 300,
    ProjectileRange = 301,
    ProjectilePierce = 302,
    ProjectileSize = 303,

    // [400..499] 资源/成长
    XpGainMul = 400,
    LootChanceMul = 401,
    CooldownReduction = 402,

    // [500..599] 技能
    SkillDamageMul = 500,
    SkillCooldownMul = 501,
    SkillDurationMul = 502,

    // [900..999] 预留：新品类不得插入中间，只能追加在本段之后
    Reserved900 = 900,

    // [1000..] 追加区（新增一律从这里往上）
    // ---- 后续任务在此追加，保持数字递增，不改动上面的任何成员 ----
}
```

### 5.2 号段规则

| 规则 | 规定 |
|---|---|
| **只加不改** | 新增成员追加在 `1000+` 区并保持递增；已发布成员的数值永不改变 |
| **号段预留** | 每类留 100 个号（生存 1–99、移动 100–199…），新属性优先落在所属号段 |
| **顺序无关** | 枚举成员的**声明顺序**不重要，`StatBlock` 按数值下标存，Fold 按 `StatDefinition` 声明顺序 |
| **废弃** | 用 `[Obsolete]` + 保留原值不删；`StatDefs` 表的 `Deprecated` 列置 true |
| **不要用隐式值** | 每个成员显式写数值，禁止 `Hp, HpMax,` 连写（重排时会静默改变存档含义） |

### 5.3 双端映射（C# 枚举 ↔ Excel 字符串 ↔ GDScript）

| 端 | 表示 | 来源 | 落地位置 |
|---|---|---|---|
| C# | `StatId` 枚举 | 手写（唯一真源） | `scripts/data/StatId.cs` |
| Excel | 字符串（`Hp` / `MoveSpeed`） | `Characters.Base`、`StatDefs.StatId` | `characters.xlsx` 的 `Characters` Sheet / `progression.xlsx` 的 `StatDefs` Sheet |
| 生成物 | 字符串 → 运行时由 `StatIdRegistry` 解析 | `data/generated/schema/statids.json` | 构建期反射导出 |
| GDScript（UI） | `const HP = 1` 之类整数常量 | **由生成器写 `scripts/ui/generated/stat_ids.gd`** | 只读、禁止手改 |

> **生成器职责**：从 C# 程序集反射出 `StatId` → 名字表 → 写 `statids.json` + 可选的 `.gd` 常量文件。这样**枚举只有一份真源**（C# 代码），不会出现 Excel 写错名字而无人发现。
> UI 层若真需要 `StatId`，**只允许用生成的 `.gd` 常量**，不得在 `.gd` 里手写数字（否则规则 4 的 GS005 等价问题在 GDScript 侧复现）。

**一致性校验**：生成期比对 Excel 中 `stats` 列出现的所有 key、`StatDefs` 表的 `StatId` 列、C# 枚举成员，三者必须一致，否则**中断构建**（错误格式见 `04-data-driven.md` §9.2）。

### 5.4 枚举扩展流程（走一遍）

```text
① 在 StatId 追加成员（新号段，1000+，显式数值）
② 在 data/excel/progression.xlsx 的 StatDefs Sheet 加一行：StatId / StatKind=Add / Default=0 / Capacity / Deprecated=FALSE
③ 需要出现在某角色上时，在 Characters.Base 列加 `NewStat=值`
④ 跑 tools/data_pipeline/ 生成（会同时刷新 statids.json 与 stat_ids.gd）
⑤ 在 ARCHITECTURE.md §5 的层/属性表加一行
⑥ 若 UI 要显示，在 UI 数据映射里补一行（Game.UI 的格式化层，不碰逻辑）
```

**改了几个文件**：C# 1（`StatId.cs`）+ Excel 1（+0~N 行）+ 生成产物（不计）。符合规则 7。

---

## 6. 修改语义（值语义，不原地改）

### 6.1 硬规则

| # | 规则 | 说明 |
|---|---|---|
| M1 | `StatPackage` / `StatBlock` 一经创建**永不修改** | 所有字段 `readonly`；`AssignFrom*` / `With` / `Combine` 返回**新实例** |
| M2 | 层栈的替换只能通过 `IStatProvider` 的装配方法 | `SetLayer(StatLayer, StatPackage)` 由实现类产生新 `Current` 并触发 `LayersChanged` |
| M3 | 对实体数值的**伤害类**修改必须走 `DamageResolver` | 不允许外部 `actor.ReduceHp(10)` 绕过结算链 |
| M4 | 存档序列化包含未知属性 | 见 4.4 |
| M5 | 任何"读-改-写"必须在单帧内完成，不允许跨帧持有旧引用后又写回 | 防止"过期包覆盖新包" |
| **M6** | **对外暴露必须是只读视图** | 见下方 §6.1.1 —— 这是 Lead 追加的硬要求 |
| M7 | 外部模块**不得持有 `StatBlock` 的可写引用** | 同上 |

#### 6.1.1 【硬要求】只读视图原则（Lead 仲裁追加）

> **规则原文（实现必须逐字满足）**：
> **`StatPackage` 的对外暴露必须是只读视图（`IReadOnlyList` / 只读属性 + 只读索引器），可变性只存在于内部构建器；任何外部模块不得持有可写引用。**

具体落法（五条，逐条对应代码骨架）：

| # | 要求 | 代码表现 | 反例 |
|---|---|---|---|
| V1 | 对外只暴露**只读属性** | `StatPackage.Values` 类型为 `IReadOnlyStatBlock`（只读接口），而非 `StatBlock` | `public StatBlock Values { get; }` ← **可写泄漏** |
| V2 | 只暴露**只读索引器** | `float this[StatId id] { get; }`（无 setter） | `float this[StatId id] { get; set; }` |
| V3 | 集合类字段一律 `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` | `IReadOnlyDictionary<string,float> UnknownStats` | `Dictionary<string,float> UnknownStats` |
| V4 | 可变性**只在内部构建器** | 只有 `StatPackageBuilder` / `StatBlock.SetUnsafe`（`internal`）能改；两者不出 `Game.Data` 程序集 | 在 `Game.Actor` 里 `package.Values.RawValues[i] = x` |
| V5 | 数组类字段**不得直接返回** | `RawValues` 只能是 `internal`，公开方法返回 `ReadOnlySpan<float>` 或拷贝 | `public float[] RawValues { get; }` |

**为什么这条是硬要求 —— 「夺舍后原怪物的包被悄悄改掉」**

这是本规则要防的真实 bug 类型，走一遍：

```text
场景：玩家夺舍史莱姆，取到 slimeDef.Base 这个 StatPackage 引用
      （AssignFromWithDefaults 把它的值复制进玩家 Base 层，
       但玩家 IActor 同时缓存了 target.Visual.Def 等引用）

如果 StatPackage 暴露了可写的 Values / RawValues：
  ① 战斗系统对玩家造成 15 点伤害 → 写入 "玩家当前 Base 层" 的 Hp
  ② 若该 Base 层是**引用共享**的（快照/回滚优化、或 Fold 时直接复用引用）
     → 写入实际落到 slimeDef.Base 上
  ③ 结果：这只史莱姆的 CharacterDef（Excel 生成物）被改了
     → 场景里所有史莱姆的血量都变成 "40 - 15 = 25"
     → 且 data/generated 的 .tres 在内存中被污染，热重载/重开房间都恢复不了
     → 更糟：回滚夺舍时恢复的"快照"也已被污染，玩家血量回到错误值
```

这类 bug 的特征是**静默、跨实体、不可复现**（取决于夺舍顺序），排查成本极高。只读视图把它在**编译期**消灭：外部代码没有可写句柄，就只能走 `With()` / `AssignFrom*` 产生新包。

**配套纪律**

1. `StatPackage` 的字段**一律 `private` 或 `internal`**，公开面只有 getter。
2. `StatBlock` 的 `SetUnsafe` 只能被 `Game.Data` 内部（构建器 + 反序列化）调用。
3. Fold 结果 `Final` 也是一个独立的不可变 `StatBlock`，**不得**把某层的数组直接当 Final 返回（否则改 Final 等于改层）。
4. 快照（`TakeoverSnapshot`）持有的是**不可变引用**，不是拷贝 —— 这正是值语义的收益：只有"不可写"才能安全地共享引用。
5. Code Review 检查点：grep `public .*\[\]|public Dictionary|public .*List<` 于 `Game.Data`，出现即可写泄漏嫌疑。

### 6.2 为什么肉鸽必须是值语义

| 场景 | 不可变包怎么支持 | 可变包为什么会坏 |
|---|---|---|
| **快照（状态存档/回滚）** | 直接保存 `StatBlock` 引用（不可变，零拷贝） | 需要深拷贝全部状态，且漏一处就存错 |
| **回滚（Rogue-lite 重开 / 撤销拾取）** | 丢弃新包，回到旧引用（O(1)） | 需要反向计算所有已应用修改，几乎不可能正确 |
| **预测/网络同步（如将来联机）** | 发送层包，接收方 Fold 出同样结果 | 依赖应用顺序，顺序不同结果不同 |
| **回放/录像** | 录制 `(seed, 输入, 层变更事件)`，回放时重 Fold | 必须录制每次数值变更，且要保证重放一致 |
| **UI 对比（"新装备 +12 攻击"）** | `Delta(old, new)` 直接出差异 | 只能靠 UI 自己记录旧值 |
| **多系统叠加** | Fold 顺序固定，与写入时机无关 | Buff 到期顺序不同 → 数值不同 → 手感抖动 |

---

## 7. 数值公式与 Modifier

### 7.1 两个唯一入口（重申）

| 入口 | 何时用 | 禁止 |
|---|---|---|
| `StatCalculator.Fold(...)` | 计算当前最终属性 | 在 Actor/技能里手算属性 |
| `DamageResolver.Resolve(...)` | 一次伤害 | 在子弹/技能里手算伤害 |

### 7.2 公式由数据驱动

公式表位于 **`data/excel/progression.xlsx` 的 `Formula` Sheet**（一个文件内一张表 = 一个 Sheet，Sheet 名 = 表名；与 `ARCHITECTURE.md` §4.2 一致）。字段：

| 字段名（第1行） | 类型（第2行） | 中文显示名（第3行） | 备注/取值域（第4行） |
|---|---|---|---|
| `Id` | `string` | 公式编号 | 小写下划线，全局唯一 |
| `DisplayName` | `string` | 显示名称 | 中文 |
| `Expression` | `text` | 表达式 | 允许的算子集见 §7.3 |
| `VarNames` | `string` | 变量名列表 | 分号分隔，必须全在 `Expression` 中出现 |
| `OutputStat` | `stats` | 输出属性 | 可选；输出 `StatId` |
| `ClampMin` | `float` | 最小值 | 空=不限制 |
| `ClampMax` | `float` | 最大值 | 空=不限制 |
| `Note` | `text` | 说明 | 人读的设计意图 |

示例行：

| Id | DisplayName | Expression | VarNames | OutputStat | ClampMin | ClampMax | Note |
|---|---|---|---|---|---|---|---|
| `dmg_basic` | 基础伤害 | `weaponDamage * attacker.DamageMul * crit` | `weaponDamage;attacker.DamageMul;crit` | — | `0` | — | 通用子弹伤害 |
| `dmg_armor` | 护甲减免 | `dmg * (1 - armor / (armor + k))` | `dmg;armor;k` | — | `1` | — | k 来自 `Characters.ArmorK` |
| `hp_levelup` | 升级血量 | `base * (1 + level * growthRatio)` | `base;level;growthRatio` | `HpMax` | `1` | — | 成长层用 |

### 7.3 表达式白名单（安全求值，不引入 `eval`）

| 支持 | 不支持（构建期报错） |
|---|---|
| 四则运算、括号、一元负号 | 函数调用、赋值、比较以上逻辑 |
| `+ - * /`、数字字面量 | 字符串运算 |
| 变量引用（`VarNames` 中的名字，`a.b` 形式读 `IStatProvider`） | 任何 I/O、反射 |
| 内置函数：`min(a,b)` `max(a,b)` `clamp(v,lo,hi)` `abs(x)` `lerp(a,b,t)` | `pow` 之外的任意扩展（需要时先在 Excel `Functions` 表登记） |

> **为什么不用表达式字符串 eval**：无法单测、性能不可控、无法静态校验变量名。落地任务实现一个手写递归下降求值器（约 200 行），构建期把 `Expression` 预编译成节点树（生成物里写整数后缀表达式），运行时零解析开销。

### 7.4 Modifier 链（伤害结算）

```text
DamageEvent { BaseDamage, DamageType, Crit, Source(IStatProvider), Target , HitInfo }
   │
   ├─ IDamageModifier.Modify(ref dmg, DamagePhase.Multiplier)   // 攻击者加成、暴击
   ├─ IDamageModifier.Modify(ref dmg, DamagePhase.TargetReduce) // 目标护甲/减伤
   └─ IDamageModifier.Modify(ref dmg, DamagePhase.FinalRound)   // 易伤、封顶、取整
   ▼
DamageResult { AppliedDamage, IsCrit, IsLethal, Overkill }
```

- Modifier 的**执行顺序**由 Excel `Modifiers` 表的 `Order` 列决定，不靠代码注册顺序（否则顺序依赖 C# 程序集加载，无法复现）。
- 每条 Modifier 也实现 `IDamageModifier`，新增一条 = Excel 加行 + （若需新逻辑）1 个实现类。

---

## 8. 参考实现骨架（落地任务时放入 `Game.Data` / `scripts/data/`）

> 下面是一份**可编译风格**的参考实现，约 200 行。落地任务可原样使用，只需补 `using`、XML 注释与单测。
> 所有数值运算都在 `StatCalculator` 中，`StatPackage` 自身只做数据容器与赋值语义。

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using Godot;

namespace Game.Data
{
    // ─────────────────────────────────────────────────────────────
    // 1. 层枚举：顺序是契约，只在末尾追加
    // ─────────────────────────────────────────────────────────────
    public enum StatLayer : byte
    {
        Base = 0,
        Growth = 1,
        Equipment = 2,
        Buff = 3,
        Temporary = 4,
    }

    /// 层标识：同层可能有多个来源（如多个 Buff），用 (Layer, SlotId) 唯一标识。
    public readonly record struct StatLayerId(StatLayer Layer, string SlotId = "")
    {
        public static readonly StatLayerId BaseLayer = new(StatLayer.Base, "");
    }

    /// schema 签名：决定两个包能否 "同一集合" 互相赋值。
    public readonly record struct StatSchema(string Id, int Version)
    {
        public static readonly StatSchema Empty = new("", 0);
        public bool IsCompatibleWith(in StatSchema other) =>
            Version < 0 || other.Version < 0 || Id == other.Id;   // 版本 < 0 表示 "任意兼容"
    }

    // ─────────────────────────────────────────────────────────────
    // 2a. IReadOnlyStatBlock：对外只读视图（M6）
    //     约定：外部模块只能拿到这个接口，永远拿不到 StatBlock 实体。
    // ─────────────────────────────────────────────────────────────
    public interface IReadOnlyStatBlock
    {
        int Capacity { get; }
        bool Has(StatId id);
        float this[StatId id] { get; }
        ReadOnlySpan<float> Span { get; }
        bool HasAt(int index);
    }

    // ─────────────────────────────────────────────────────────────
    // 2b. StatBlock：一层/一包的密集存储（值语义）
    //     用 float[] 按 StatId 数值下标存，热路径零字典查找。
    //     实现 IReadOnlyStatBlock；可变方法一律 internal，不出 Game.Data。
    // ─────────────────────────────────────────────────────────────
    public sealed class StatBlock : IReadOnlyStatBlock
    {
        private readonly float[] _values;                      // 下标 = (ushort)StatId
        private readonly bool[] _has;                          // 是否显式拥有该属性

        public StatBlock(int capacity)
        {
            _values = new float[capacity];
            _has = new bool[capacity];
        }

        public bool Has(StatId id) => (int)id < _has.Length && _has[(int)id];
        public float this[StatId id] => Has(id) ? _values[(int)id] : 0f;

        /// 内部构造用；对外不暴露可变接口
        internal void SetUnsafe(StatId id, float value)
        {
            _values[(int)id] = value;
            _has[(int)id] = true;
        }

        // ── 只读视图（M6/V1/V5）：数组本身绝不外泄 ──────────────
        public int Capacity => _values.Length;
        public int Count { get; private set; }
        public ReadOnlySpan<float> Span => new ReadOnlySpan<float>(_values);
        public bool HasAt(int index) => index >= 0 && index < _has.Length && _has[index];

        // 仅 Game.Data 内部（构建器 / Fold / 反序列化）可用
        internal float[] RawValues => _values;
        internal bool[] RawHas => _has;
        internal void IncrementCount() => Count++;
    }

    // ─────────────────────────────────────────────────────────────
    // 3. StatPackage：一层不可变包 + 赋值语义（规则6 的核心）
    // ─────────────────────────────────────────────────────────────
    public sealed class StatPackage : IEquatable<StatPackage>
    {
        public StatSchema Schema { get; }
        public StatLayer Layer { get; }

        /// 只读视图（M6/V1）：外部拿不到可写句柄，只能 With()/AssignFrom*() 产生新包。
        public IReadOnlyStatBlock Values { get; }

        /// 本包未声明、但对方有的属性：只保留不参与计算
        public IReadOnlyDictionary<string, float> UnknownStats { get; }

        private readonly StatBlock _values;      // 内部可写实体，不出类

        private StatPackage(StatSchema schema, StatLayer layer, StatBlock values,
                            IReadOnlyDictionary<string, float> unknown)
        {
            Schema = schema; Layer = layer; _values = values;
            Values = values;                     // 对外只读视图
            UnknownStats = unknown;
        }

        // ── 构造 ──────────────────────────────────────────────
        public static StatPackage Create(in StatSchema schema, StatLayer layer, int capacity,
                                         IReadOnlyDictionary<string, float> unknown = null) =>
            new(schema, layer, new StatBlock(capacity),
                unknown ?? new Dictionary<string, float>(0));

        // ── 只读查询 ──────────────────────────────────────────
        public bool Has(StatId id) => _values.Has(id);
        public float Get(StatId id) => Values[id];

        // ── 复制（值语义：返回全新实例）────────────────────────
        public StatPackage Copy()
        {
            var b = new StatBlock(_values.RawValues.Length);
            Array.Copy(_values.RawValues, b.RawValues, _values.RawValues.Length);
            Array.Copy(_values.RawHas, b.RawHas, _values.RawHas.Length);
            return new StatPackage(Schema, Layer, b, UnknownStats);
        }

        // ── 同 schema 整包赋值 ────────────────────────────────
        /// 前提：同 schema。跨 schema 在 Debug 下断言失败；
        /// Release 下自动降级为 AssignFromWithDefaults（保证不炸游戏）。
        public StatPackage AssignFrom(in StatPackage other, StatBlock defaults = null)
        {
            if (!Schema.IsCompatibleWith(other.Schema))
            {
                StatPackageDiagnostics.ReportSchemaMismatch(Schema, other.Schema);
                return AssignFromWithDefaults(other, defaults);
            }
            return ReplaceValues(other, defaults, fillMissing: true);
        }

        // ── 跨 schema 赋值：缺失取默认、未知保留、绝不抛 ──────
        /// 规则：对方有的取对方；对方没有我有 → 取 defaults；
        ///       对方有我无 → 进 UnknownStats。
        public StatPackage AssignFromWithDefaults(in StatPackage other, StatBlock defaults)
        {
            var merged = new Dictionary<string, float>(UnknownStats);
            foreach (var kv in other.UnknownStats)
                merged[kv.Key] = kv.Value;                    // 对方的未知属性一并接过来

            return ReplaceValues(other, defaults, fillMissing: true, unknown: merged);
        }

        private StatPackage ReplaceValues(in StatPackage src, StatBlock defaults,
                                          bool fillMissing,
                                          Dictionary<string, float> unknown = null)
        {
            var b = new StatBlock(_values.RawValues.Length);
            for (int i = 0; i < _values.RawValues.Length; i++)
            {
                var id = (StatId)i;
                if (src._values.Has(id))
                {
                    b.SetUnsafe(id, src.Values[id]);          // ① 对方有 → 用对方
                }
                else if (fillMissing && defaults != null && defaults.Has(id))
                {
                    b.SetUnsafe(id, defaults[id]);            // ② 对方没有 → 取默认
                }
                else if (fillMissing)
                {
                    b.SetUnsafe(id, StatDefinition.DefaultOf(id)); // ② 兜底默认
                }
            }
            return new StatPackage(Schema, Layer, b,
                unknown ?? UnknownStats);
        }

        // ── 单点修改：返回新包 ────────────────────────────────
        public StatPackage With(StatId id, float value)
        {
            var copy = Copy();
            copy._values.SetUnsafe(id, value);
            return copy;
        }

        // ── 叠加（不含默认填充，用于 Buff/Temporary 这类"增量层"）──
        /// CombineLayers 不在这里，见 StatCalculator.Fold。
        public StatPackage CombineDelta(in StatPackage other)
        {
            var b = new StatBlock(_values.RawValues.Length);
            for (int i = 0; i < _values.RawValues.Length; i++)
            {
                var id = (StatId)i;
                bool hasA = _values.Has(id), hasB = other._values.Has(id);
                if (!hasA && !hasB) continue;
                b.SetUnsafe(id, (hasA ? Values[id] : 0f) + (hasB ? other.Values[id] : 0f));
            }
            return new StatPackage(Schema, Layer, b, UnknownStats);
        }

        // ── 差量 ─────────────────────────────────────────────
        public static StatPackage Delta(StatPackage from, StatPackage to)
        {
            var b = new StatBlock(from._values.RawValues.Length);
            for (int i = 0; i < from._values.RawValues.Length; i++)
            {
                var id = (StatId)i;
                float v = to.Get(id) - from.Get(id);
                if (v != 0f) b.SetUnsafe(id, v);
            }
            return new StatPackage(to.Schema, to.Layer, b, to.UnknownStats);
        }

        // ── 存档序列化（含未知属性）──────────────────────────
        public Godot.Collections.Dictionary ToDictionary()
        {
            var values = new Godot.Collections.Dictionary();
            for (int i = 0; i < _values.RawValues.Length; i++)
            {
                var id = (StatId)i;
                if (_values.Has(id))
                    values[StatIdRegistry.NameOf(id)] = _values[id];
            }
            var unknown = new Godot.Collections.Dictionary();
            foreach (var kv in UnknownStats) unknown[kv.Key] = kv.Value;

            return new Godot.Collections.Dictionary
            {
                ["schemaId"] = Schema.Id,
                ["schemaVersion"] = Schema.Version,
                ["layer"] = (int)Layer,
                ["values"] = values,
                ["unknown"] = unknown,
            };
        }

        public static StatPackage FromDictionary(Godot.Collections.Dictionary d)
        {
            var schema = new StatSchema((string)d["schemaId"], (int)d["schemaVersion"]);
            var layer = (StatLayer)(int)d["layer"];
            var b = new StatBlock(StatIdRegistry.Capacity);
            foreach (var kv in (Godot.Collections.Dictionary)d["values"])
                b.SetUnsafe(StatIdRegistry.Parse((string)kv.Key), (float)kv.Value);

            Dictionary<string, float> unknown = null;
            if (d.TryGetValue("unknown", out var u) && u.VariantType == Variant.Type.Dictionary)
            {
                unknown = new Dictionary<string, float>();
                foreach (var kv in (Godot.Collections.Dictionary)u)
                    unknown[(string)kv.Key] = (float)kv.Value;
            }
            return new StatPackage(schema, layer, b,
                unknown ?? new Dictionary<string, float>(0));
        }

        public bool Equals(StatPackage other) =>
            other is not null && Schema == other.Schema && Layer == other.Layer
            && ReferenceEqualsValues(Values, other.Values);

        private static bool ReferenceEqualsValues(StatBlock a, StatBlock b)
        {
            if (a.RawValues.Length != b.RawValues.Length) return false;
            for (int i = 0; i < a.RawValues.Length; i++)
                if (a.RawHas[i] != b.RawHas[i] || a.RawValues[i] != b.RawValues[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => Equals(obj as StatPackage);
        public override int GetHashCode() => HashCode.Combine(Schema, (byte)Layer);
    }

    // ─────────────────────────────────────────────────────────────
    // 4. IStatProvider：对外唯一读口（夺舍/装备/Buff 都实现它）
    //    声明位置：Game.Data / scripts/data/   （与 StatPackage 同层，本骨架所在）
    //    实现位置：Game.Actor / scripts/actor/ （★ Actor / EquipmentSlot / BuffStack 承载）
    //    ⚠️ 实现类不得落在 scripts/data/ —— Game.Data 不依赖 Game.Actor（见 ARCHITECTURE §2.3）
    // ─────────────────────────────────────────────────────────────
    public interface IStatProvider
    {
        StatPackage Current { get; }                       // 已 Fold 的 Final（懒计算+缓存失效）
        float Get(StatId id);
        StatPackage GetLayer(StatLayer layer, string slotId = "");
        IReadOnlyList<ISkill> Skills { get; }              // 技能列表也随包一起换（夺舍用）
        event Action<StatLayerId> LayersChanged;
    }

    // ─────────────────────────────────────────────────────────────
    // 5. StatCalculator：层栈 → Final 的唯一入口
    // ─────────────────────────────────────────────────────────────
    public sealed class StatCalculator
    {
        private readonly StatDefinition[] _defs;           // 来自 Excel StatDefs 表
        private readonly int _capacity;

        public StatCalculator(StatDefinition[] defs, int capacity)
        { _defs = defs; _capacity = capacity; }

        /// Fold：按 StatLayer 升序累加（Base 打底）。
        /// FoldKind 决定每个属性的叠加方式 —— 全部来自数据，代码无分支。
        public StatBlock Fold(IReadOnlyList<StatPackage> layers)
        {
            var acc = new StatBlock(_capacity);
            foreach (var p in layers)
            {
                for (int i = 0; i < _capacity; i++)
                {
                    var id = (StatId)i;
                    if (!p.Values.Has(id)) continue;   // IReadOnlyStatBlock.Has
                    var def = _defs[i];
                    float v = p.Values[id];
                    float cur = acc.Has(id) ? acc[id] : def.Default;
                    acc.SetUnsafe(id, def.FoldKind switch
                    {
                        StatFoldKind.Add          => cur + v,
                        StatFoldKind.Mul          => cur * v,
                        StatFoldKind.AddThenClamp => Mathf.Clamp(cur + v, def.Min, def.Max),
                        StatFoldKind.Max          => Mathf.Max(cur, v),
                        StatFoldKind.Min          => Mathf.Min(cur, v),
                        _                         => cur + v,
                    });
                }
            }
            return acc;
        }
    }

    public enum StatFoldKind : byte { Add = 0, Mul = 1, AddThenClamp = 2, Max = 3, Min = 4 }

    /// 单个属性的定义（Excel StatDefs 表的一行，构建期生成）
    public sealed record StatDefinition(StatId Id, StatFoldKind FoldKind, float Default, float Min, float Max)
    {
        public static float DefaultOf(StatId id) => StatDefinitionTable.Instance.Get(id).Default;
    }
}
```

### 8.1 骨架的可见性约定（M6 的代码落点）

| 成员 | 可见性 | 理由 |
|---|---|---|
| `StatPackage.Values` | `public IReadOnlyStatBlock` | 只读视图；外部拿不到 `StatBlock` 实体 |
| `StatPackage.UnknownStats` | `public IReadOnlyDictionary<string,float>` | 只读 |
| `StatPackage` 的公开成员 | 全部 `get`-only / 返回新实例的方法 | 无可写入口 |
| `StatPackage._values` | `private` | 类内 Fold/赋值用 |
| `StatBlock.SetUnsafe` / `RawValues` / `RawHas` | `internal` | 只有 `Game.Data` 程序集内（构建器、Fold、反序列化）可调 |
| `StatBlock` 实现 `IReadOnlyStatBlock` | `public` 接口 | 跨程序集消费时只能看到接口 |

> **落地检查**：`scripts/data/` 编译出的程序集之外，**不允许**出现对 `RawValues`/`RawHas`/`SetUnsafe` 的引用（编译器会直接报错，因为它们是 `internal`）。
> 这是 M6 从"文档约定"变成"编译期强制"的关键：只要外部模块拿不到可写句柄，"夺舍后原怪物的包被悄悄改掉"这类 bug 在类型层面就不可能出现。

### 8.2 骨架里刻意留的三个"待落地"点

| 点 | 说明 | 落地任务 |
|---|---|---|
| `StatDefinitionTable` / `StatIdRegistry` | 由生成物 `data/generated/schema/statdefs.json` + `statids.json` 加载 | 数据管线任务 |
| `ISkill` 的引用 | `IStatProvider.Skills` 用 `IReadOnlyList<ISkill>`。**已由 Lead 仲裁 #3-2 定案：`ISkill` / `ISkillTarget` 声明下沉到 `Game.Data`**（纯数据契约），实现留在 `scripts/skill/`。因此 `Game.Data` 不依赖 `Game.Skill`，本骨架可自洽编译 | 见 03 §4.1 仲裁要点 B |
| `StatPackageDiagnostics` | Debug 断言 + 编辑器报错通道 | 数值任务 |

---

## 9. 反例

| # | 反例 | 问题 | 正确 |
|---|---|---|---|
| N1 | `actor.Hp -= damage;` 直接在 Actor 上改字段 | 破坏值语义、绕过结算链、无法回滚 | `DamageResolver.Resolve` → 产生新 Base/Current 包 |
| N2 | 玩家类与怪物类各有一份 `Speed/Hp/Damage` 字段 | 无法互相赋值，夺舍要靠类型转换 | 都用 `StatPackage` 层 |
| N3 | 跨 schema 赋值时 `throw` | 夺舍直接崩；内容扩展必炸 | `AssignFromWithDefaults`，绝不抛 |
| N4 | 缺的属性"保留我原来的值" | 夺舍后玩家凭空获得怪物没有的优势 | 缺的取 `defaults` |
| N5 | `StatId` 用连续隐式值，中间插入新成员 | 已发布存档的数值全部错位 | 显式数值 + 只加不改 |
| N6 | Buff 到期时"逆向减回去" | 浮点累积误差 + 顺序依赖 | 直接丢弃该 Buff 层重 Fold |
| N7 | 把 `CritChance` 写成 `15`（百分比） | 与 Excel 口径不一致，暴击 1500% | 小数 `0.15`，并在 `StatDefs.Min/Max` 里声明 |
| N8 | 公式写成 C# `switch (statId)` 硬编码 | 新增属性要改代码（违反规则 7） | `StatDefinition.FoldKind` 数据驱动 |
| N9 | 在 `Game.Data` 里 `using Game.Skill` 只为用 `ISkill` | 违反分层矩阵 | `ISkill` 声明在 `Game.Data`（Lead 仲裁 #3-2） |
| N10 | `public float[] RawValues { get; }` 之类要把数组外泄 | 外部可写 → 共享引用被改 → 所有同 def 的实体数值错乱 | 返回 `ReadOnlySpan<float>` 或 `IReadOnlyStatBlock`（M6/V5） |
| N11 | 把某层的数组直接当 `Final` 返回 | 改 Final 等于改层，回滚时数据已脏 | Fold 结果必须是新分配的 `StatBlock` |

---

## 10. 自检清单（PR 自检，逐项勾选）

- [ ] **Q1** 我新增的属性走完了 §5.4 的六步（`StatId` + `StatDefs` 行 + Excel 引用 + 生成 + ARCHITECTURE + UI 映射）？
- [ ] **Q2** 我新增的 `StatId` 只用显式数值、只追加在末尾、没改动任何已有成员？
- [ ] **Q3** 我没有任何原地修改包的代码（没有 `package.X = y`、没有 `+=`）？**也没有从外部模块持有 `StatBlock` 可写引用**（M6：只能拿 `IReadOnlyStatBlock`）？
- [ ] **Q4** 所有跨包赋值都用了 `AssignFrom`（同 schema）或 `AssignFromWithDefaults`（跨 schema），且**没有 `throw`**？
- [ ] **Q5** 我的跨 schema 赋值显式传了 `defaults`（没有依赖 `new StatPackage()` 猜默认值）？
- [ ] **Q6** 未知属性在 `ToDictionary`/`FromDictionary` 里都被带上了？
- [ ] **Q7** 我没有在 `StatPackage`/技能/子弹里手算伤害或属性，全部走了 `StatCalculator` / `DamageResolver`？
- [ ] **Q8** 我新增的每条公式都进了 `Formula` 表，变量名全在 `VarNames` 里，且能通过构建期校验？
- [ ] **Q9** 我的层顺序假设没有引入"插入中间层"（新层只追加在 `Temporary` 之后）？
- [ ] **Q10** 我为赋值语义写了单测（同 schema 赋值 / 跨 schema 缺属性 / 未知属性往返 / Delta）？

---

## 11. 需要 Lead 仲裁的开放问题

| # | 问题 | 我的建议 | 影响 |
|---|---|---|---|
| OQ-1 | `StatPackage` 用 `class`（引用）还是 `readonly record struct`（值）？本文用 `class` + 全字段不可变 | 用 `class`：内含 `float[]` 数组，`struct` 拷贝成本不低且易误用；不可变性靠 API 保证 | 性能/语义 |
| ~~OQ-2~~ | ~~`ISkill` 声明在哪个命名空间？~~ | **已裁定（Lead 仲裁 #3-2）**：`ISkill` / `ISkillTarget` 声明下沉到 `Game.Data`（纯数据契约），实现留在 `scripts/skill/`。见 03 §4.1 仲裁要点 B | 已关闭 |
| OQ-6 | M6 只读视图是否用 `ReadOnlyMemory<float>` 替代 `ReadOnlySpan<float>`（跨 async/存储场景） | v1 用 `IReadOnlyStatBlock` 接口 + `ReadOnlySpan<float>`；若将来需要跨帧持有，再提供 `ToArray()` 的显式拷贝方法 | 热路径 API |
| OQ-7 | `StatPackage.Values` 返回 `IReadOnlyStatBlock` 会让每次属性访问多一次接口派发，是否接受？ | 接受：属性访问不在最内层循环里（最内层是 `Fold` 用 `p.Values.Has(id)` 的一次调用，可由 JIT 内联）；且这是 M6 的必要代价 | 性能 |
| OQ-3 | Fold 是否允许"绝对值覆盖层"（如 Boss 阶段强行设 HpMax） | v1 不加，用 `progression.xlsx` 的 `Formula` Sheet + Temporary 层实现 | 公式复杂度 |
| OQ-4 | `StatBlock` 用 `float[]`（快）还是 `Dictionary<StatId,float>`（灵活） | 用 `float[]` + `bool[]`（已写入骨架），`Dictionary` 只用于 `UnknownStats` | 热路径性能 |
| OQ-5 | 夺舍后 Growth 层保留还是清零？ | 保留（见 3.2）；若策划要"完全变成怪物"，新增 `AssignFromWithDefaults` 的 mode 参数 | 玩法手感 |
