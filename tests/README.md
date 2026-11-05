# tests/ — ECS 测试工程

> 真源：`docs/architecture/ECS.md` §10。
> 一键入口：`tools/check_all.ps1`（build → build -c Host → 架构守卫 → `dotnet test`）。

---

## 1. 怎么跑

```powershell
# 全量（收工前跑这个）
.\tools\check_all.ps1

# 只跑测试
dotnet test tests\BiuBiuOrigin.Tests.csproj

# 只跑某一组
dotnet test tests\BiuBiuOrigin.Tests.csproj --filter "FullyQualifiedName~Kernel"
```

要求：仅需 **Windows PowerShell 5.1 / PowerShell 7 + .NET SDK 10.0.401**。**不需要 Godot 运行时**。

---

## 2. 这是 MSTest，不是自研断言（以及为什么能离线跑）

ECS.md §10 原文写的是「测试工程 `tests/BiuBiuOrigin.Tests.csproj` 引用主工程（`<ProjectReference>`），`dotnet test` 一键跑」。
本轮落地时做了两处**偏离原文、并经 Lead 批准**的决定：

### 2.1 用 MSTest（不是自研断言）

| 包 | 版本 | 来源 |
|---|---|---|
| `Microsoft.NET.Test.Sdk` | **18.4.0** | 全局 NuGet 缓存 |
| `MSTest.TestAdapter` | **4.2.1** | 全局 NuGet 缓存 |
| `MSTest.TestFramework` | **4.2.1** | 全局 NuGet 缓存 |
| `MSTest.Analyzers` | **4.2.1**（传递依赖） | 全局 NuGet 缓存 |

**关键事实**：仓库的离线源 `E:\Godot\GodotSharp\Tools\nupkgs` 里**只有** Godot 的四个包，没有测试框架。
真正让测试可跑的是 **`C:\Users\Administrator\.nuget\packages\` 全局包缓存**——它恰好已缓存上面这套包。
`nuget.config` 清空了所有远程源，所以还原只可能命中全局缓存。

### 2.2 离线可用范围（其他机器还原失败时照这张表排查）

| 场景 | 是否可用 | 处理 |
|---|---|---|
| 本机（缓存已就位） | ✅ 实测通过 | — |
| 本机缓存被 `dotnet nuget locals all --clear` 清掉 | ❌ 还原失败 | 无法从公网补（`nuget.config` 禁网）。需 Lead 批准把这 4 个 `.nupkg` 放进离线源或另行登记 |
| 换一台没这套缓存的机器 | ❌ 还原失败 | 同上 |
| 想升级测试框架版本 | ⚠️ | 新版本不在缓存里 → 必然还原失败。**版本号必须钉死在上面这张表**，改动前先确认新版本已缓存并被 Lead 批准 |

> 给未来的排查者：`dotnet restore tests\BiuBiuOrigin.Tests.csproj` 的报错若是 `NU1101 找不到包 X`，
> 先跑 `Get-ChildItem C:\Users\Administrator\.nuget\packages | Select-Object Name` 对照 §2.1 的版本表，
> 而不是去改 `nuget.config` 加公网源（那会违反规则 2 §3.1「禁止从公网拉包」）。

### 2.3 用 `<Compile Include>` 而不是 `<ProjectReference>`（**硬约束，别改回去**）

ECS.md §10 写的是 `ProjectReference`，但那样做会**破坏该节自己的硬约束**：

> 「测试必须能在没有 Godot 的情况下 `dotnet test` 通过（Game.Core 不依赖引擎，这是本工程的硬约束）」

`BiuBiuOrigin.csproj` 是 `Sdk="Godot.NET.Sdk/4.7.2"`，一旦 `ProjectReference` 它，GodotSharp 的整条引用闭包
会被拖进测试进程 —— 于是「内核不依赖引擎」这件事就**从未被证明过**，只是没碰巧炸而已。

所以本项目改为**直接编译同一批无引擎源码**：

```xml
<Compile Include="..\scripts\core\**\*.cs" Link="Game.Core\%(RecursiveDir)%(Filename)%(Extension)" />
```

同一 `AssemblyName`（`BiuBiuOrigin`）、同一命名空间、同一访问级别，行为与引用主程序集等价，
但测试进程里**一个 Godot 类型都不会出现**。

代价与兜底（三条守卫用例，见 §4）：

1. `Game.Core` 里一旦出现 `using Godot` / `Godot.*` 全限定名 → 立即红（这本来就是 ECS.md §10 的守卫组）；
2. 测试程序集的引用表里一旦出现 `Godot*` → 立即红（等价于「有人把 csproj 改回 ProjectReference」）；
3. 被 glob 编进来的源码集合一旦为空 → 立即红（防「0 个文件却测试全绿」的假阳性）。

**`scripts/core/**` 之外的内容不由本工程编译**：宿主层（`scripts/host/**`，唯一允许 `using Godot` 的层）
由 `tools/check_all.ps1` 的 `dotnet build -c Host` 步骤覆盖。两条路径合起来才是完整覆盖。

---

## 3. 测试组与 ECS.md §10 的对应

| 目录 | 组 | 覆盖 |
|---|---|---|
| `Layering/` | 分层守卫 | `Game.Core` 源码 0 个 `using Godot`；测试程序集不引用 Godot；源码集合非空；**四层依赖方向**（ui 不碰业务层 / data 不反向依赖 / core 是最底层 / 逻辑层不碰 Godot / host·view 不做结构变更） |
| `Kernel/` | ECS 内核不变量 | 创建/销毁/世代复用、原型迁移后旧引用失效、Query 含/排除、chunk 边界 0/1/128/129、CommandBuffer 顺序与冲突、管线拓扑排序与环检测 |
| `Characters/` | 角色装配与英雄系统 | 装配/变体解析、模块拼接、组件形状（unmanaged + 唯一 `[Identity]` 首字段）、WASD 移动、鼠标瞄准、开火节流、**端到端整链路**（装配 → 移动 → 瞄准 → 左键出真子弹） |
| `Combat/` | 战斗系统族 | 弹丸推进/射程/寿命三条死法、命令缓冲提交时机、事件上报次数 |
| `Determinism/` | 确定性 | 同 seed 两次跑同一系统序列，组件快照逐字节一致 |
| `Allocation/` | 零分配 | `Query.ForEach` 稳态 `GC.GetAllocatedBytesForCurrentThread` 前后差 = 0 |

> **`Layering/LayerDependencyTests` 的判据是纯文本扫描**：注释里提到被禁的名字不算违规（说明文字常要举反例），
> 字符串字面量里的 `//` 不会被当成注释截断。这样它和 `Game_Core_Layering` 一样不依赖 Godot，`dotnet test` 直接可跑。


---

## 4. 写用例的规矩

1. **断言必须对应用例语义**。为了跑绿而放松断言（比如把「0 分配」改成「分配 < 1KB」、把「逐字节一致」改成「长度一致」）视为**测试无效**，比没有测试更糟。
2. **不许 `[Ignore]` 掩盖未实现的契约**。内核没落地 = 测试工程编译不过，这是如实信号，不要用空用例凑数。
3. **断言里不写魔法数值**：确定性用「同 seed 序列逐元素相等 / 不同 seed 不等 / Fork 子流独立」表达，不写死具体随机数（实现微调时不会假红，语义不弱）。
4. **零分配用例要预热**：先跑几轮 warm-up 让 JIT / 首次路径完成，再测稳态；只断言稳态那一段的 delta。
5. 离线、无网络、无 Godot 依赖——新增用例不得引入这三类依赖中的任意一种。
6. **写零分配系统桩时，`World.Query<T>()` 必须在 `OnInitialize` 里建一次并缓存**。
   `World.BuildQuery` 内部是 `new Query(...)`，**每次调用都分配**；在 `OnExecute` 里现调就等于把分配
   引进热路径，用例永远过不了。生产系统（`MovementSystem` / `MoveAimSystem` / `ProjectileSystem`）全都
   是"初始化建查询、执行期只 `ForEach`"，测试桩必须同形。
7. **`ForEach` 一律用 `ISpanAction<T>` 接口回调，不要用 lambda**。
   `ForEach((Span<T> s) => ...)` 每次调用都会新建闭包委托实例（实测约 500 字节/帧）；
   内核提供接口重载就是为了避开它。
   > 这两条是实测踩出来的：一个测试桩因为同时犯 6 与 7，稳定产生 **528 字节/帧**，
   > 二分了 `MovementSystem` / `MoveAimSystem` / `Query.ForEach` 之后才定位到桩本身。
8. **`LifetimeSystem` 必须传 `selfManaged` 谓语**（`e => world.Has<Projectile>(e)`）来跳过子弹。
   子弹的倒计时与销毁归 `ProjectileSystem`；漏传会导致 Update 组的 `LifetimeSystem` 抢先销毁子弹，
   `ProjectileSystem.LifetimeExpiredCount` 恒为 0 —— 那是一个曾经真实存在、且完全静默的 bug。

---

## 5. hero-1 验收清单

> 契约：[`docs/design/character-hud-contract.md`](../docs/design/character-hud-contract.md) §0 / §3 / §5 / §8。
> 每条「怎么证明」都是**可直接粘贴执行的过滤条件**（在仓库根目录跑）。
> 前缀统一为 `dotnet test tests\BiuBiuOrigin.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~<测试名>"`。

| 需求编号 | 需求的原文（摘录） | 怎么证明它成立（命令或测试名） | 当前结论 |
|---|---|---|---|
| 1 | 「人物槽位就是『测试人物』」 | `EndToEnd_AssemblyProducesPlayableComponentSet`（4 变体） | ✅ 通过 |
| 2 | 「填基础数值 + UI 显示」 | `Compose_MissingLayerIsSimplyNotFolded` + `HeroStats_StandardVariantFoldsToExpectedShape` + `HeroStats_FourVariantsDifferInTheDirectionTheDesignTableSays`（数值包折叠）+ `UiSources_DoNotUseBusinessLayers`（UI 不碰业务层） | ✅ 通过 |
| 3 | 「WASD 移动」 | `Wasd_MovesHeroInAllFourDirections`（4 变体）+ `EndToEnd_HeroMovesForwardAtMoveSpeed_ForSixtyFrames` + `EndToEnd_HeroTurnsTowardsAimWhileMoving` + `EndToEnd_StoppingStopsTheHero` + `MoveAim_TurnsHeroTowardsAimDirection` | ✅ 通过 |
| 4 | 「鼠标左键瞄准射击」 | `EndToEnd_PressingPrimarySpawnsRealProjectileFlyingDownAimDirection`（**真子弹** + 武器侧闭环）+ `EndToEnd_PrimaryAttackSlotIsFireProjectileSkill`（4 变体）+ `HeroSkillRows_DeclareRealProjectileSkillsPointingAtExistingWeapons`（4 变体）+ `Fire_TappingLeftButtonOnce_ProducesOneProjectile` + `ManaIsSpent_AndInsufficientManaBlocksCast` + 工厂逐字段核对：`（终局补齐）` | ⚠️ 数据层与门控全绿；**`EndToEnd_PressingPrimarySpawnsRealProjectile…` 报红**（`bulletCount == 0`），等宿主 `ISkillFactory` 接线 |
| 5 | 「符合 ECS 思想」 | `HeroComponents_AreUnmanagedValueTypes`（5 个组件）+ `HeroComponents_HaveExactlyOneIdentityFieldAndItIsFirst`（5 个）+ `HeroComponents_DeclareNoMethodsAndNoGameplayConstants` + `AddModule_*`（5 条）+ `LayerDependencyTests`（6 条方向守卫） | ✅ 通过（`DataSources_DoNotUseActorCombatSkill` 已随 `ActorDefinitionRegistry` 迁入 `core/` 转绿） |
| 6 | 「拼接即可批量出变体」 | `HeroAssembly_EveryVariantResolvesWithZeroCode` + `HeroAssembly_ExposesExactlyFourResolvableVariants` + `HeroAssembly_ModulesAreAssemblyUnionVariant`（变体解析/零代码新增）+ `EndToEnd_AllFourVariantsMoveAtTheirOwnSpeed` + `HeroSkillLoadouts_HaveCorrectSkillIdAtEverySlot` + `HeroSkillLoadoutSlots_FollowVariantStructure` | ✅ 通过 |

### 逐条要点

- **需求 1**：4 个变体每个都装配出**恰好 1 个**实体，并带上 `Transform3 / Velocity / MoveIntent / AimTarget / ActorIdentity / Team / Health / Mana / SkillState / ActorIntent / ViewAppearance`。
- **需求 2**：`Compose(StatPackageIds)` 的 `Final = Base + Σ Delta` 逐字段校验；4 变体在同一 `StatId` 上必须**方向性**不同（不是"只要不等就行"）。HUD 的**像素表现**不在此覆盖——那要带窗口看截图。
- **需求 3**：方向约定 W=−Z / S=+Z / A=−X / D=+X；跑满 `response` 秒后速度**精确等于**标称值；端到端用解析解 `speed·(T − τ(1 − e^(−T/τ)))` 加 2% 容差，不给任意宽容。
- **需求 4**：四段链路各自有用例 —— 瞄准收敛 / 冷却节流 / 左键槽是 `FireProjectileSkill` 且冷却 == 武器行 / 按左键后世界里真出现 `Projectile` 且前向 ≈ +X。这条做过**变异验证**（`PressedMask` 置 0 立刻红）。
- **需求 5**：组件 unmanaged、唯一 `[Identity]` 首字段、无方法/静态字段；`[Module]` 拼接语义；外加四条依赖方向守卫。
- **需求 6**：零代码新增变体走完整装配路径成功；模块并集生效；变体 id/数值包 id 不存在时报**中文**异常而非 `NullReferenceException`。

### ⚠️ 当前唯一红点

`LayerDependencyTests.DataSources_DoNotUseActorCombatSkill` —— `scripts/data/CharacterContentSet.cs` 反向 `using Game.Actor` / `Game.Skill`。

**这是真违规，不是测试 bug，且按 Lead 裁决保留红着**（走方案 A：数据层只声明技能行、由组合根实例化）。修复后本表需求 5 转为 ✅。
**任何人不要给守卫加白名单或放宽它** —— 放宽后这个红点就永远消失了。

---

## 6. 怎么加一个新变体

目标：从数据表加一行，到能在游戏里跑起来，**0 行 C# 代码**。

### 步骤（数据层 → 运行）

1. **Excel 加一行**（`data/excel/characters.xlsx`）
   在对应 sheet 段里加一行变体，关键列：变体 id、`BasePackageId`、`GrowthPackageId`、`LoadoutId`（技能包）、`WeaponRowId`（武器行）、外观包 / 镜头包 / 手柄包、模块列表。
   > 武器数值不用编——它来自 `Weapons` 表的一行，变体行只写 `WeaponRowId` 引用它。

2. **重新生成数据**（`data/generated/` 禁止手改）
   ```powershell
   python tools\data_pipeline\gen_characters_xlsx.py
   ```

3. **同步运行时兜底副本**（`CharacterContentSet.BuildBuiltinV1()`）
   运行时副本是 Excel 的镜像。**它和 Excel 的一致性由回归用例保证**——两者都改了才算改完。

4. **运行验证**
   ```powershell
   dotnet build
   .\tools\check_architecture.ps1
   dotnet test tests\BiuBiuOrigin.Tests.csproj --no-build --nologo
   # 游戏内（有窗口）：
   <Godot 可执行> --path . --hero-variant=<你的新变体id>
   ```

### 一条命令证明「拼接生效」

```powershell
dotnet test tests\BiuBiuOrigin.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~HeroAssemblyTests|FullyQualifiedName~EndToEnd_AssemblyProducesPlayableComponentSet"
```

- `HeroAssemblyTests` 里有一条「零代码新增变体」用例：构造一个新变体行 → 走完整装配路径 → 必须成功。
  你新增的变体若 `Resolve` 失败（缺数值包 / 技能包 / 武器行），会在这里以**中文错误信息**报出来。
- `EndToEnd_AssemblyProducesPlayableComponentSet` 用 `[DataRow]` 覆盖 4 个既有变体；
  若要覆盖新变体，**在 `[DataRow]` 里加一行 id** 即可（这是唯一需要碰测试代码的地方，且只是加数据行）。

### 不需要做的事

- ❌ 不需要新建 `class XxxHero`
- ❌ 不需要加 `if (variantId == "...")` 分支
- ❌ 不需要改 `ActorAssemblySystem`
- ❌ 不需要改 `csproj`

> 若上面任一项「不得不做」，说明扩展性判据（规则 7）失守了 —— 那是一个**设计问题**，不是加变体的问题。

---

## 7. 回归说明

以后谁改动了以下东西，**哪个测试会先红**：

| 你改了什么 | 先红的测试 | 说明 |
|---|---|---|
| 组件加了引用类型字段 / 方法 / 静态字段 | `HeroComponentShapeTests` | J1/J2 破：组件不能再进 archetype 列存储 |
| 删了 `[Identity]` 或把它挪到非首字段 | `HeroComponentShapeTests` | 内核靠首字段定位实体号 |
| `ActorAssemblySystem` 少写某个组件 | `EndToEnd_AssemblyProducesPlayableComponentSet` | 表现为分散的运行期症状（能动但没血条等） |
| 装配清单/变体定义写错 | `HeroAssemblyTests` | 中文异常直接指出是哪个 id 缺什么 |
| 数值包 `Compose` 折叠规则变了 | `HeroAssemblyTests` | `Final = Base + Σ Delta` 逐字段 |
| `MoveAimSystem` 的补间公式或 `MovementSystem` 的积分顺序变了 | `HeroMovementTests` / `EndToEnd_HeroMoves...` | 解析解断言会立刻对不上 |
| 左键槽换回 `DataSkill` 占位 | `EndToEnd_PrimaryAttackSlotIsFireProjectileSkill` | 端到端就没子弹了 |
| 开火技能冷却与武器行脱钩 | `EndToEnd_PrimaryAttackSlotIsFireProjectileSkill` | 冷却 == `ShotCooldown` |
| `IWeaponStatsSource` 没接 / 映射写错 | `EndToEnd_HostWiresWeaponStatsFromData` | 武器行逐字段校验 |
| 武器表改了数值 | `EndToEnd_HostWiresWeaponStatsFromData` | 关系式断言（组件 == 数据行），改表不假红、映射错才红 |
| `LifetimeSystem` 漏传 `selfManaged` | `Projectile_LifetimeExpiry_IsOwnedByProjectileSystem` | `LifetimeExpiredCount` 恒 0 |
| 有人在热路径里现调 `World.Query` 或用 lambda `ForEach` | `HeroZeroAllocationTests` | 稳态分配从 0 变成每帧几百字节 |
| 依赖方向被带偏（谁 using 谁） | `LayerDependencyTests` | 四条方向守卫 + 引擎边界守卫 |
| 测试工程被改回 `ProjectReference`（引入 Godot） | `TestAssembly_DoesNotReferenceGodotSharp` | 测试不再能在无引擎进程里跑 |
| 数据层反向依赖业务层 | `DataSources_DoNotUseActorCombatSkill` | 架构反转 |
| 表现层直接 `World.Create/Destroy` | `HostViewSources_DoNotCreateOrDestroyEntities` | J5：结构变更必须走 `CommandBuffer` |
| 新增变体没同步运行时副本 | `CharacterContentRegressionTests` | Excel ↔ `CharacterContentSet` 一致性 |

### 一条红线

> 任何用例**不允许**为了让流水变绿而放宽断言。
> 断言过不了，先怀疑实现；确认是需求变了，就**改契约文档并说明理由**，再改用例。
> 把「0 分配」改成「分配 < 1KB」、把「逐字节一致」改成「长度一致」这类改动视为**测试无效**。

---

## 8. 测试纪律（血换来的，逐条都有事故编号）

### 8.1 变异验证只允许改测试自己的代码

> **绝不允许临时修改 `scripts/**` 的生产代码做变异验证。**
> 需要验证"守卫测试真会红"时，复制到 `tmp/`（或 `%TEMP%`）下的临时副本，
> 或者直接在测试内构造坏数据（给被测方法喂非法输入）。

**事故 #1**：为了验证"暴击倍率守卫会红吗"，直接改了 `scripts/data/CharacterContentSet.cs` 里的
`CriticalMultiplier`，结果把整个文件的中文编码弄坏（见 8.2），557 行文件、372 行改动全部报废，
由文件作者手工重建。

**判据（比"注意编码"高一层）**：验证"我的东西有效"时，产生的破坏**必须落在我的东西范围内**。
这次的形式是编码，下次可能是误删、误格式化、误批量替换 —— 形状会变，边界不会。

### 8.2 含中文的源码一律用 `write` / `edit` 工具落盘

> **任何含中文的 C# / Python / Markdown 文件，一律用 `write` / `edit` 工具写。**
> PowerShell 5.1 的 `ReadAllLines` / `WriteAllLines` 走系统 ANSI（GBK）代码页，
> 中文字符的 GBK **第二字节撞上 `0x5C`（反斜杠）** 会被 C# 当成转义符，吞掉紧随的引号 ——
> 字符串字面量失去闭合引号，整行语法崩掉。同时 `\n` 也可能被吞，
> 导致代码被并进上一行注释里（武器行全部失效，而编译错误只报在别处）。

**事故 #1 / #2**：本项目已发生两次，两次都是 PowerShell 读写含中文的 `.cs`。
第一次表现为 `CS1010: 常量中有换行符`，第二次连换行都丢了，
`utf8 → cp936 → utf8` 逆向还原有 530 个字符映射失败 —— **原始字节不可逆丢失**，
只能靠 ASCII 骨架（数值/标识符/结构全在）+ 人工重写中文来恢复。

**如果在 shell 里非改不可**：用 Python + 显式 `encoding="utf-8"`，不要用 PowerShell 的 `-replace`。

### 8.3 `dotnet build` 失败时禁止 `dotnet test --no-build`

> 那跑的是**上一次成功的旧二进制**，会产生**假绿** —— 最危险的一类错误：
> 你以为验证通过了，实际上新写的代码根本没被编译过。

**事故 #1 的后续**：文件被弄坏后 build 失败，但紧接着的
`dotnet test --no-build` 显示"通过 1 / 失败 0"，差点被当成"变异验证成功"的证据。
**先看 build 全绿，再看 test 结果** —— 两条命令必须串联检查，不能只看后一条。

### 8.4 正确的变异验证姿势（可复用）

不改工作树，只改**编译输入指向**：

```powershell
# ① Python 把整个目录复制到 %TEMP%，在副本里做字节级替换（UTF-8 显式编码）
# ② 用 -p:<GlobProp>=<副本路径> 让测试工程编译副本
dotnet build tests\BiuBiuOrigin.Tests.csproj -p:CharacterSourceGlob2=<副本目录>\**\*.cs
dotnet test  tests\BiuBiuOrigin.Tests.csproj --no-build --filter "FullyQualifiedName~<守卫用例>"
```

**注意**：glob 属性必须指向**整个目录**，不能指向单个文件 ——
否则同目录的其他类型（如 `SkillCastSystem`）会一起丢失，编译失败，验证无效。

**验证完必须**：删副本、用正常参数重新 build、`git status` 确认为空。

---

## 9. 宿主接线契约（给写 `ISkillFactory` / 组合根的人）

### 9.1 `LifetimeSystem` 必须传 `selfManaged`

```csharp
new LifetimeSystem(events, selfManaged: e => world.Has<Game.Combat.Projectile>(e));
```

**漏传的后果**：`LifetimeSystem`（`Update` 组）会先于 `ProjectileSystem`（`FixedUpdate` 组）
把子弹销毁，`ProjectileSystem.LifetimeExpiredCount` **恒为 0** —— 一个曾经真实存在、
完全静默的 bug（子弹寿命"无主"，射程/寿命判定形同虚设）。

钉住它的用例：`Projectile_LifetimeExpiry_IsOwnedByProjectileSystem`、
`Hero_ManySimultaneousExpiries_DoNotAllocate`。

### 9.2 工厂里 `WeaponId` 与 `ProjectileId` 绝不可传反

```csharp
var row = content.SkillRowOf(loadoutId, slotNumber)!.Value;

// 外观侧：传 ProjectileId（proj_pistol_basic）
ISkill skill = row.IsProjectile
    ? new FireProjectileSkill(row.SkillId, row.ProjectileId, row.CooldownSeconds, row.ManaCost, slot)
    : new DataSkill(row.SkillId, row.ProjectileId, slot, row.CooldownSeconds, row.ManaCost);

// 武器侧：查数值用 WeaponId（pistol_basic），不是 ProjectileId
var weapon = content.Assemblies.FindWeapon(row.WeaponId);
```

**两者都是 `string`，传反不会有编译错误**，症状只有一行日志（"首个子弹请求 pistol_basic"）。

为什么最终验收要**两侧都验**：
- 只验外观侧（`fire.DefId == row.ProjectileId`）→ 两个参数**整体传反**时 `DefId` 凑巧正确，**照样绿**
- 必须再验武器侧（`proj.WeaponId / Damage / RangeMeters / Speed` 来自正确的武器行）→ 才能抓到

对应用例：`HeroSkillRows_DeclareRealProjectileSkillsPointingAtExistingWeapons`（数据侧）、
`EndToEnd_PressingPrimarySpawnsRealProjectileFlyingDownAimDirection`（实体侧闭环）。

### 9.3 `PelletsPerShot` 必须真的被消费

数据表 v1 全为 1，但**代码里必须真的读它去循环生成子弹**。
否则将来做霰弹，把数据改成 5 之后开一枪还是只出 1 颗，**而且不会有任何报错**。

钉住它的用例：`EndToEnd_PressingPrimarySpawnsRealProjectile...` 里的
`Assert.AreEqual(heroWeapon.PelletsPerShot, bulletCount, ...)`。



