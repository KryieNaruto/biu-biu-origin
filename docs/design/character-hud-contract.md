# hero-1 契约（第一个人物 = 测试人物）：组件拼装 + WASD 移动 + 鼠标瞄准射击 + HUD + 可批量拼接的变体

> 版本 hero-1/v1 ｜ 2026-11-05 ｜ 本文件是本会话**所有并行开发者之间的唯一接口约定**。
> 收工前它的内容会被并入 `docs/architecture/ARCHITECTURE.md` §17（规则3）。
> **接口要变 = 先改本文件并发消息通知，不许各改各的。**

---

## 0. 需求对照（用户原话 → 落点）

| # | 用户要求 | 落点 | 验收 |
|---|---|---|---|
| 1 | 人物槽位就是「测试人物」 | `characters.xlsx` / `CharacterContentSet` 新增 `test_hero` 定义（`ActorKind.Player`，`VisualSlot=6`），开局由宿主生成在 Start 房地面 | 主场景开局站着测试人物 |
| 2 | 填基础数值 + UI 显示 | 数值全部来自 `StatPackage`（规则6/规则4）；HUD 从 `HudSnapshot` 读，**UI 不碰 ECS** | 屏幕上有名字/血/蓝/体力/护甲/移速/暴击 面板 |
| 3 | WASD 移动 | `MoveAimSystem`（新）：意图 → 速度 → `Velocity` → `MovementSystem` 积分 | 按 WASD 角色在 Start 房内移动 |
| 4 | 鼠标左键瞄准射击 | `AimAtCursorSystem`：鼠标射线 → 世界点 → `AimTarget` + 朝向；`FireProjectileSkill`：左键 → 子弹 | 按住左键持续射击，子弹飞向光标 |
| 5 | 符合 ECS 思想 | 新增 3 个 `SystemBase` 子类；新增组件只有 unmanaged 值；结构变更全走 `Commands`（J5） | 无 `class Player`；无 `if (kind==Player)` |
| 6 | 拼接即可批量出变体 | 「镜头包 / 手柄包（`[Module]` 类特性）+ 数值包 + 技能包 + 外观包」的**装配清单**：`CharacterAssemblySpec`（纯数据）；变体 = 一行数据，**0 行代码** | 一次生成 4 个变体（战士/敏捷/重甲/幸运），HUD/世界/日志都能看到差异 |

---

## 1. 分层与文件所有权（谁写哪个文件，别人不许碰）

```text
scripts/core/ecs/attributes/ModuleAttribute.cs          [Lead]   模块标记特性（[Module]）
scripts/core/ecs/SystemPipeline.cs                      [Lead]   模块驱动：AddModule / 取消注册 / ModuleNames
scripts/core/ecs/EcsConfig.cs                           [Lead]   Context/Module 默认实例（可被子世界覆盖）
scripts/core/ecs/components/OfficialComponents.cs       [Lead]   追加 AimTarget / MoveIntent / ViewSlot 组件 + HudStrings
scripts/core/Handles/ActorHandle.cs                     [Lead]   （只读，不改）

scripts/actor/ActorComponents.cs                        [Lead]   追加 ActorTag / WeaponStats（unmanaged）
scripts/actor/MoveAimSystem.cs                           [systems] 意图 → 速度 → 朝向（"标准移动包"）
scripts/skill/FireProjectileSkill.cs                     [systems] 左键射击（ISkill 实现）
scripts/skill/PrimaryAttackModule.cs                      [systems] 把射击技能装进技能包
scripts/combat/CombatComponents.cs                       [systems] Projectile / Hitbox / Contact
scripts/combat/ProjectileSystem.cs                       [systems] 直线飞行 + 生命周期
scripts/combat/HitDetectionSystem.cs                     [systems] 命中检测（球-球）
scripts/combat/ImpactEventSystem.cs                       [systems] 命中 → 事件 + 特效命令

scripts/data/CharacterAssemblySpec.cs                    [packages] 变体清单 + 外观包 + 手柄包解析
scripts/data/CharacterContentSet.cs                      [packages] 追加 test_hero + 4 个变体行
tools/data_pipeline/gen_characters_xlsx.py              [packages] Excel 同步（同一批数据）

scripts/ui/HudModel.cs                                   [ui]    DTO（本契约 §2）
scripts/ui/CharacterHudSkin.cs                           [ui]    [GlobalClass] Resource 皮肤
scripts/ui/CharacterHudDrawer.cs                         [ui]    Control 画布

scripts/host/CharacterHudPanel.cs                        [view]  CanvasLayer，把快照转交给 Drawer
scripts/host/GameViewOptions.cs                          [view]  [GlobalClass] Resource：镜头/外观/输入参数
scripts/host/view/ActorViewDirector.cs                   [view]  按 VisualKey 选外观（预制体/槽位）
scripts/host/view/TopDownCameraRig.cs                    [view]  正交俯视跟随（必须来自镜头包）
scripts/host/view/CursorAimInputSource.cs                [view]  鼠标 → 意图（唯一碰 mouse position 的地方）
scripts/host/CharacterSession.cs                         [Lead]   宿主接线（后面 §6 详述）

docs/design/character-hud-contract.md                    → 本文件被吸收进 ARCHITECTURE §17
docs/architecture/ARCHITECTURE.md                        [Lead]   收工统一改
tests/Character/**                                       [tests]  新增用例
```

**红线复述**：`scripts/ui/**` 只准 `using Godot` + `using Game.UI`；系统层（core/actor/combat/skill/data 的逻辑部分）不准 `using Godot`。

---

## 2. DTO —— `scripts/ui/HudModel.cs`，`namespace Game.UI;`

```csharp
public readonly struct HudBar { public readonly float Value; public readonly float Max; public float Ratio => ...; public HudBar(float value, float max); }

public readonly struct HudSkillSlot
{
    public readonly bool HasSkill;
    public readonly bool Ready;
    public readonly float CooldownProgress; // 0=就绪 1=刚放完
    public readonly string Glyph;           // 槽位字形（HUD 只画不解释）
    public readonly string KeyLabel;        // "LMB" / "LSHIFT" ...
    public HudSkillSlot(bool hasSkill, bool ready, float cooldownProgress, string glyph, string keyLabel);
}

public struct HudSnapshot
{
    public bool HasCharacter;
    public string CharacterName;   // "测试人物 · 标准型"
    public string VariantTag;      // "变体 · 重甲"（空串 = 不画）
    public HudBar Health, Mana, Stamina;
    public float Armor;
    public float ArmorReduction;   // 0..1，已算好
    public float MoveSpeed;
    public float CritChance;
    public float CritMultiplier;
    public int Level;
    public HudSkillSlot[] Skills;  // 长度 5（索引 = (int)SkillSlot - 1 对应 1..5）
    public float AimAngleDeg;
    public float FrameSeconds;
    public void EnsureSkills();    // 宿主调用：Skills 为 null 时新建长度 5 的数组（仅一次）
}

/// <summary>技能槽的静态文案（宿主在装配时读一次，HUD 复用）。</summary>
public static class SkillSlotLabels
{
    public static string GlyphOf(SkillSlot slot);    // 如 ">>" "ATK" "SWAP" "SIG" "INT"
    public static string KeyLabelOf(SkillSlot slot); // 如 "LSHIFT" "LMB" "Q" "R" "F"
}
```

`SkillSlotLabels` 用了 `Game.Core.SkillSlot` → 所以它**只能**放在 `scripts/ui/HudModel.cs` 之外的 `scripts/ui/HudLabels.cs`？
**不行**——`scripts/ui/**` 禁止 `using Game.Core`。因此：`GlyphOf/KeyLabelOf` 的返回字符串由**宿主填进 `HudSkillSlot`**，
DTO 里不出现 `SkillSlot` 类型；宿主用 `SkillIndex`（0..4）索引。

---

## 3. 组件（`Game.Core.Ecs` 命名空间，官方组件文件追加）

> **⚠ 2026-11-05 更新：本节以源码 XML 注释为准**。实现期发现 `ViewSlot` 与 `Game.Core.VisualSlot`
> 枚举撞名，故改名为 `ViewAppearance`（字段 `VisualSlotIndex`）；`AimTarget`/`MoveIntent`/`WeaponStats`
> 也各追加了 1~2 个字段。下面是**实际落地的形状**，契约的其余章节（DTO、模块、装配清单）不变。

```csharp
/// <summary>瞄准目标：世界空间点 + 朝向。HasTarget=false 表示本帧没有有效瞄准（保留上次方向）。</summary>
public struct AimTarget
{
    [Identity] public Entity Owner;
    public bool HasTarget;
    public System.Numerics.Vector3 Point;
    public System.Numerics.Vector3 Direction;
}

/// <summary>移动意图：本帧希望的速度（米/秒，世界空间）+ 输入强度（0..1，手柄用）。</summary>
public struct MoveIntent
{
    [Identity] public Entity Owner;
    public System.Numerics.Vector3 Velocity;
    public float Magnitude;
}

/// <summary>外观选择（不叫 ViewSlot：与 Game.Core.VisualSlot 枚举撞名）。</summary>
public struct ViewAppearance
{
    [Identity] public Entity Owner;
    public int VisualKey;         // 外观包 id 的稳定哈希（0 = 无外观）
    public int VisualSlotIndex;   // 外观包在宿主外观注册表里的槽位号（-1 = 未解析）
    public int PaletteSlot;       // 配色档位号（表现层取色用）
    public float Scale;           // 视图缩放（表现层用）
}

// scripts/actor/ActorComponents.cs 追加：
public struct WeaponStats
{
    [Identity] public Entity Owner;
    public float Damage;
    public float BulletSpeed;
    public float RangeMeters;          // 射程 = 速度 × 生命周期（由技能换算）
    public float SpreadDegrees;
    public float CriticalChance;
    public float CriticalMultiplier;
    public float ProjectileRadius;
    public float ShotIntervalSeconds;  // 两发间隔（冷却，来自数据）
    public int PelletsPerShot;         // 一次发射几颗（霰弹 > 1）
    public int WeaponId;
}
```

> **`ViewAppearance` 与官方 `View` 的区别**：官方 `View` = 视图池句柄（租/还）；`ViewAppearance` = **外观选择**（长什么样）。
> 两者别混用。`VisualBridge` 只认 `View`。

角色域组件（`scripts/actor/ActorComponents.cs` 追加）：

```csharp
/// <summary>角色标签：用于"这批实体是某次装配产生的"（统计/回收/夺舍归组）。</summary>
public struct ActorTag
{
    [Identity] public Entity Owner;
    public int AssemblySlot;   // CharacterAssemblySpec 槽位
    public int VariantIndex;   // 同一装配的第 N 个变体
}

/// <summary>武器数值（来自 WeaponStats 数据行；射击技能只读它，不写死）。</summary>
public struct WeaponStats
{
    [Identity] public Entity Owner;
    public float Damage;
    public float BulletSpeed;
    public float RangeMeters;      // 射程 = 速度 × 生命周期
    public float SpreadDegrees;    // 0 = 完全扩散
    public float CriticalChance;
    public float CriticalMultiplier;
    public float ProjectileRadius;
    public float ShotCooldown;
    public int WeaponId;
}
```

战斗组件（`scripts/combat/CombatComponents.cs`，`Game.Combat` 命名空间）：

```csharp
public struct Projectile { [Identity] public Entity Owner; public int ShooterIndex; public int ShooterGeneration; public TeamId Team; public float Damage; public float Radius; public float Speed; public bool Critical; public int WeaponId; }
public struct Hitbox { [Identity] public Entity Owner; public float Radius; public float Height; public TeamId Team; public bool Critical; }   // ← 待定，见 §3.1
```

### 3.1 命中检测的最小实现（systems 决定）
- `HitDetectionSystem`：`Query<Projectile, Transform3>()` × `Query<Hurtbox, Team, Transform3>()`，
  竖直方向比较 `|dy| <= Height/2 + Radius`，水平比较 `dist2 <= (Radius + Hurtbox.Radius)^2`，且 `!Team.IsHostileTo(其他)` 跳过，
  且 `ShooterIndex/Generation` 与目标相同跳过（不打自己）。
- 命中 → 投递 `HitEvent`（`Game.Core.Events` 里的 record）→ 下一帧由 `ImpactEventSystem` 转成组件（事件即数据，J1）。
  **v1 允许简化**：`ProjectileSystem` 每帧开头把「已过期/已命中」的子弹记进 `List<Entity>`，
  由 `commands.Destroy` 投递销毁命令；命中计数写进 `HitStats`。
- **不实现伤害结算**（`Health` 扣血属于 §6 战斗闭环，另一个任务）——但要预留 `HitEvent.Damage` 字段并按数值填。

---

## 4. 模块机制（"拼接"的核心）—— `scripts/core/ecs/attributes/ModuleAttribute.cs` + `SystemPipeline`

```csharp
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class ModuleAttribute : Attribute
{
    public ModuleAttribute(params string[] moduleIds);   // 如 [Module("move_standard", "aim_cursor")]
    public IReadOnlyList<string> ModuleIds { get; }
}
```

`SystemPipeline` 新增（**只能由 Lead 改，别人不碰**）：

```csharp
public void AddModule(string moduleId, ...systems);   // 断言每个系统的 [Module] 含这个 id；实际是逐个 Add
public int ModuleCount { get; }
public IReadOnlyList<string> ModuleNames { get; }     // 按加入顺序
public bool Remove(SystemBase system);                // 取消注册（只在 Build 之前合法；之后抛）
```

> 为什么不是"运行时动态拔插系统"：`SystemPipeline.Build()` 之后是扁平数组，取消注册会让帧中顺序不可推理。
> **本期的"拼接"发生在装配期（开局/加载）**，运行期切换由"换包 + 重建管线"实现（夺舍任务再做）。

---

## 5. 装配清单（批量拼接变体）—— `scripts/data/CharacterAssemblySpec.cs`，`namespace Game.Data;`

```csharp
public sealed class CharacterAssemblySpec
{
    public string Id { get; }                 // "hero"
    public CharacterModuleSet Modules { get; }// 模块 id 集合（move_standard / aim_cursor / fire_primary / camera_ortho_topdown / hud / input_cursor）
    public IReadOnlyList<CharacterVariantSpec> Variants { get; }
}

public sealed class CharacterVariantSpec
{
    public string Id { get; }          // "test_hero" (标准) / "test_hero_tank" ...
    public string DisplayName { get; } // "测试人物 · 标准"
    public string DefinitionId { get; }// → ActorDefinitionRegistry
    public string BasePackageId { get; }       // StatPackage
    public string GrowthPackageId { get; }     // 可空
    public string EquipmentPackageId { get; }  // 可空
    public string LoadoutId { get; }           // SkillLoadout
    public string WeaponRowId { get; }         // WeaponStats 数据行
    public string VisualPackId { get; }        // 外观包
    public string CameraPackId { get; }        // 镜头包（可为空 = 用默认）
    public string InputPackId { get; }         // 手柄包（可为空 = 用默认）
    public string VariantTag { get; }          // HUD 上那行小字
}

/// <summary>外观包：模型/槽位/配色/尺寸。Excel 一行。</summary>
public sealed class CharacterVisualPack
{
    public string Id; public string ModelPath; public float ModelScale; public int Palette; public string ColorHex;
}

/// <summary>镜头包 / 手柄包：影响"玩家怎么玩"，不影响"他能打多少"。Excel 一行。</summary>
public sealed class CharacterCameraPack { public string Id; public float Distance; public float PitchDeg; public float YawDeg; public float FollowLerp; public float CursorScreenHeightRatio; }
public sealed class CharacterInputPack  { public string Id; public string MoveAction; public string AimAxisAction; public string PrimaryAction; }
```

解析器：`CharacterAssemblyRepository`（同文件）负责把三张表（Variant/Visual/Camera/Input/Weapon）解析成
`CharacterAssembly[]`；**`CharacterContentSet` 在 `BuildBuiltinV1()` 里注册内容时同步注册它们**（单一真源仍是 Excel）。

---

## 6. 宿主接线 —— `scripts/host/CharacterSession.cs`（Lead）

```csharp
public sealed class CharacterSession
{
    public CharacterSession(Node3D worldRoot, World world, SystemPipeline pipeline, WorldHostOptions hostOptions);
    public bool Start(string assemblyId, string variantId, Godot.Vector3 spawn);  // 生成玩家 + 建镜头/输入/HUD
    public void Tick(float alpha, double delta);   // 视图层每帧：相机跟随、准星、HUD 提交
    public HudSnapshot CurrentHud { get; }         // 上一帧填的快照（测试/截图脚本读）
    public PlayerInputIntentSource? AimSource { get; }
}
```

**集成点（`DungeonDebugScene`，只加不改语义）**：
`BuildHost()` 末尾 → `_character = new CharacterSession(...)`；
`_Process()` 里 `_fixed.Tick(...)` 之后 → `_character?.Tick(...)`；
命令行 `--hero-variant=<id>` 可换变体（截图/回归用）。

---

## 7. 数据表（Excel，`data/excel/characters.xlsx` 追加 4 个 sheet 段，`tools/data_pipeline/gen_characters_xlsx.py` 同步）

| Sheet | 关键列 |
|---|---|
| `Characters` | 追加 `test_hero` 及其 3 个变体行（Base / SkillLoadout / HitRadius / HitHeight / VisualSlot） |
| `StatPacks` | 追加 `test_hero_base`（标准）、`test_hero_tank_base`、`test_hero_agile_base`、`test_hero_lucky_base` |
| `SkillLoads` | 追加 `test_hero_kit`（左键 = 1101 射击）、`test_hero_tank_kit`（重型射击 1106，慢而痛） |
| `Weapons`（新） | `pistol_basic` / `smg_light` / `cannon_heavy` / `ricochet_lucky` |
| `VisualPacks`（新） | `hero_default` / `hero_tank` / `hero_agile` / `hero_lucky` |
| `CameraPacks`（新） | `topdown_standard` / `topdown_wide` |
| `InputPacks`（新） | `kbm_standard` / `kbm_alt` |
| `VariantAssembly`（新） | 每个变体一行：指向上面各包 + 模块集合 |

> 数值怎么定：`test_hero_base` 在现有 `player_base` 的基础上**不复制粘贴**，而是复用同一套 StatId 词汇。
> 具体数值由 packages 负责人在表里定义并给出理由（标准 / 重甲=血厚移速慢伤害高 / 敏捷=移速快射速快伤害低 / 幸运=暴击高）。

---

## 8. 验收（全员）

```powershell
dotnet build                                                   # 0 error
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_architecture.ps1   # 0 error（warning 尽量 0）
dotnet test tests\BiuBiuOrigin.Tests.csproj --no-restore        # 全绿
# 无头冒烟（50 帧）：
& 'E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe' --headless --path . --quit-after 50 -- --hero --hero-variant=test_hero
# 有窗口 + 截图：
& 'E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe' --path . --quit-after 200 -- --hero --shots
```

截图落盘：`out/screenshots/hero_*.png`（Lead 统一出图并在收尾汇报里展示）。

**日志约定（便于自动校验）**：
- `[Hero] variant=<id> assembly=<id> modules=<a,b,c>` 开局一行
- `[Hero] hud name=<..> hp=<cur>/<max> mana=<..> armor=<..>(<pct>%) speed=<..> crit=<..>` 每 60 帧一行
- `[Combat] projectiles=<n> hits=<n> spawned=<n>` 每 60 帧一行
