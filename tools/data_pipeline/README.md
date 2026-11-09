# 数据管线（tools/data_pipeline）· 策划改表手册

> **一句话**：这个目录下是「Excel → 校验 → 生成物」的构建期工具。**你只需要改 `data/excel/*.xlsx`**，
> 改完跑一次校验，绿了就提交；红了一条一条照着报错改。

| 文件 | 作用 |
|---|---|
| `gen_combat_xlsx.py` | 生成 `data/excel/combat.xlsx`（战斗内容：标签 / 物体 / 武器 / 子弹 / 数值包） |
| `gen_characters_xlsx.py` | 生成 `data/excel/characters.xlsx`（角色：角色定义 / 数值包 / 技能包） |
| `validate_content_xlsx.py` | ★ **构建期校验器**：两个 xlsx 全表校验，失败则中断构建 |

> ⚠️ **`.xlsx` 生成出来以后就是唯一真源**。之后请**直接改 Excel**，不要再跑生成器覆盖
> （生成器只负责「第一次就把表头建对」；重跑会把你手改的内容冲掉）。若确需调整表头列，
> 改生成器里 `COLUMNS` 的同一行，再重跑，然后把数据补回去。

---

## 1. 怎么跑（必须用 DSH 自带 Python）

系统自带的 Python **没有 openpyxl**，会直接报 `ModuleNotFoundError`。请用：

```powershell
# 项目根目录下
$py = "E:\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe"

# ① 校验（改完表必跑）
& $py -B tools\data_pipeline\validate_content_xlsx.py

# ② 重新生成表（只在需要重建表头时跑，会覆盖手改内容！）
& $py -B tools\data_pipeline\gen_combat_xlsx.py
& $py -B tools\data_pipeline\gen_characters_xlsx.py
```

- 通过：只打印**一行** `OK: <n> 项检查通过`，退出码 0。
- 失败：打印**全部**中文错误，退出码 1。错误格式：

```text
[错误] combat.xlsx/Weapons 第6行G列（pistol_101.BaseDamage）: 无法解析的规则值「30+50%」；……
```

读法：**源文件 / Sheet 第几行 第几列（哪一行的哪个 Id.哪个字段）: 为什么错、该怎么写**。
`第6行` 是 **Excel 里的真实行号**（含上面 4 行表头 + 1 行 `#data`），直接在 Excel 里跳到那一格即可。

---

## 2. 表结构约定（每张 Sheet 都一样）

```text
第 1 行  字段名（CamelCase，= C# 属性名）
第 2 行  类型（string / int / float / bool / 规则值 / enum:Xxx）
第 3 行  中文名（编辑器里给人看）
第 4 行  备注（取值域 / 外键说明）
第 5 行  #data          ← 数据起始标记，A 列写 #data，其余列必须空
第 6 行  第一条数据
```

硬规则（违反 = 校验失败）：

1. **Sheet 名 = 表名**。不要新增、不要改名、不要删 Sheet。
2. **不能有合并单元格**、不能插入/删除/移动列（列集合与顺序由校验器定死；要加列必须同时改生成器 + C# 表读取）。
3. **Id 必须在第 1 列**，全局稳定：小写字母开头，只能有小写字母/数字/下划线（业务 Id 允许点号分段，如 `tag.pistol`）。
4. **空 ≠ 0**。规则值列空着会报错——「没有效果」要**显式写 `0`**。`StatPacks.Value` 同理。
5. 以 `#` 开头的行会被当注释跳过；整行为空的行会被跳过（但请直接删除，别留空行）。

---

## 3. `combat.xlsx` —— 五张表 + `_Meta`

### 3.1 `Tags` —— 标签字典（位矩阵的列定义）

| 列 | 类型 | 含义 |
|---|---|---|
| `TagId` | string | 标签 id，**必须** `tag.` 开头，如 `tag.pistol` |
| `TagIndex` | int | ★ 运行时的 tag id，**必须从 0 连续递增**（0,1,2,…，不能跳号、不能重号） |
| `Family` | enum `TagFamily` | ★ **标签轴**（索引坐标系）：强化 / 图鉴 / 筛选按轴取范围，见下 |
| `Tier` | enum `TagTier` | ★ **标签层**：`Logic`=逻辑功能层（决定玩法）/ `Identity`=标识层（只做匹配），见 §7 |
| `Category` | enum `TagCategory` | `Kind`/`Stat`/`Element`/`Ability`/`Trigger`（**历史遗留**，新内容只填 `Family`） |
| `Group` | enum `TagGroup` | `ObjectKind`/`WeaponKind`/`ProjectileKind`/`Element`/`Ability`/`Chain`/`Trigger`/`None` |
| `BitMask` | int | 该标签在 64 位配方字里的位，**0..63 且唯一**；写 `-1` = 不进配方字 |
| `Note` | string | 人读说明 |

**为什么要 `Group`**：运行时按「物体命中的 `ObjectKind` 组标签 → 能力位（Actor / Weapon / Projectile）」落子，
代码里**不出现任何具体 tag 名**。所以一个物体**最多只能命中一个** `ObjectKind` 标签
（都命不中 = 纯表现物体，如特效）。

> ⚠️ `Group` 决定**能力位**，`Family` 决定**索引轴**。两者刻意分开：标签体系怎么扩，都不会动到工厂判定。

**为什么要 `Family`（标签轴）**：玩法要问的范围几乎从来不是「哪些物体挂了 `tag.pierce`」，
而是「所有**稀有**武器」「所有**元素**标签」「所有**族裔**」。
`Family` 把这些范围查询编成一个坐标系，运行期「命中某轴任一标签」= **一次按位与**
（`matrix.Mask(row,0) & rules.FamilyMask(TagFamily.Rarity)`），代码不认识任何具体 tag 名。
详见 [ARCHITECTURE §19](../../docs/architecture/ARCHITECTURE.md)。

十个轴（不在这里的轴名一律被绑定器**当场报错**，不会静默落入 `None`）：

| 轴 | 互斥 | 含义 | 现有标签 |
|---|---|---|---|
| `Kind` | ✅ | 物体大类（同时决定能力位） | `actor` / `weapon` / `projectile` |
| `WeaponKind` | ✅ | 武器种类 | `pistol` / `rifle` / `bow` / `melee` |
| `ProjectileKind` | ✅ | 弹体种类 | `homing` |
| `Element` | ✅ | 伤害元素 | `kinetic` / `energy` / `explosive` |
| `Ability` | ❌ **可叠加** | 能力（穿透+弹跳+分裂能叠在同一颗弹上） | `pierce` / `bounce` / `split` / `summon` / `chain` / `projectile_fired` / `short_range` / `multi_shot` / `charge` / `ricochet` / `high_damage` |
| `Trigger` | ✅ | 触发时机 | `volatile` |
| `Rarity` | ✅ | 稀有度 | `common` / `uncommon` / `rare` / `legendary` |
| `Race` | ❌ **分层** | 族裔（祖先 `humanoid` ⊃ 具体 `goblin`/`slime`/`ogre`） | `humanoid` / `goblin` / `slime` / `ogre` |
| `Role` | ✅ | 角色定位 | `hero` / `grunt` / `ranged` / `caster` / `elite` / `assassin` |
| `Effect` | ✅ | 特效类别 | `muzzle` / `hit` / `burst` |

**两个「不互斥」是刻意的**：
`Ability` 可叠加 = ECS 组合的主战场，一旦互斥就退回成分类枚举；
`Race` 分层 = 「对人形增伤」要能打到哥布林，而「对哥布林增伤」不该误伤其他人形。

**怎么加一个标签**：在最后加一行，`TagIndex` 填「现有最大 +1」，`Family` 填所属轴，
`BitMask` 填一个没用过的位（不需要进配方字就写 `-1`）。
**不要删、不要改已有的 `TagIndex` / `BitMask`**——它们会被存档与配方字引用。

> 现有 40 个 tag 里只有 16 个占 `BitMask`（bit 0~15，进配方字）；其余 24 个 `BitMask = -1`，
> 它们只走轴索引、不占位矩阵的 32 位预算。**轴标签数不受 `BitMask` 上限约束。**

### 3.2 `Objects` —— 唯一的「物体原型」表（人物 / 怪物 / 武器 / 子弹 / 特效全在这）

这是「人物怪物同源 + 高自由度拼装」的核心：**所有东西都是同一个 `Objects` 行**，区别只在标签和引用。

| 列 | 类型 | 含义 |
|---|---|---|
| `Id` | string | 物体 id |
| `DisplayKey` | string | 本地化 key（不写中文） |
| `Tags` | string | 逗号分隔的 `TagId`，必须都在 `Tags` 表里 |
| `Cluster` | string | 可替换簇（熔炉按权重在同簇里随机选一个替换）；空 = 独立不参与熔炉 |
| `ClusterWeight` | float | 熔炉权重，**必填且 > 0**；同簇内按权重加权随机，越大越常被选中（稀有物件给小值） |
| `StatPacks` | string | **5 段**用 `;` 分隔：`Base;Growth;Equipment;Buff;Temporary`，指向 `characters.xlsx` 或本文件 `StatPacks` 的 `PackId`；空段 = 该层无包 |
| `Weapon` | string | → `Weapons.Id`；空 = 没武器 |
| `Projectile` | string | → **`Objects.Id`**，且该物件必须被某行 `Projectiles` 引用；空 = 没子弹契约 |
| `Actor` | string | → `Objects.Id`；★ 子弹也能引用怪物 |
| `Effect` | string | → `Objects.Id`；空 = 无 |
| `RenderId` | string | 表现层选模型/材质用，逻辑层不解释 |
| `Radius` / `Height` | float | 米 |

#### 熔炉簇（`Cluster` + `ClusterWeight`）怎么配

熔炉遇到「可替换」的时候，会在同一个 `Cluster` 里的候选之间**按 `ClusterWeight` 加权随机**挑一个。
所以一个簇要满足三条硬规矩：

1. **`ClusterWeight` 必填且 > 0**。写 `0` 或负数 = 这个候选永远选不中（校验器直接报错）。
2. **同簇的能力位必须一致** —— 它们会被互相替换，所以必须都是同一种东西：
   全 `tag.projectile` / 全 `tag.weapon` / 全 `tag.actor` / 全为纯表现（无 ObjectKind）。
   **把子弹和武器配进同一个簇是错的**：熔炉会把一颗子弹换成一把枪。
3. **一个簇最好 ≥ 2 个候选**。只有 1 个候选时加权随机的结果恒定，配簇没有实际意义
   —— 校验器给 `[提醒]`（不中断构建），建议留空或补候选。

本表的实际配法（权重体现稀有度）：

| 簇 | 候选（权重） | 说明 |
|---|---|---|
| `proj_kinetic` | `bullet_101`(5.0) `bullet_102`(2.0) `bullet_103`(0.5) `arrow_202`(0.2) | 动能弹，手枪弹最常见、带武器属性的箭最稀有 |
| `weapon_gun` | `pistol_101`(3.0) `smg_102`(1.5) `revolver_103`(0.6) | 手枪类 |
| `weapon_rifle` | `rifle_202`(2.5) `bow_201`(1.0) `sniper_203`(0.5) | 长枪类 |
| `actor_goblin` | `goblin_grunt`(5.0) `goblin_archer`(2.0) `goblin_shaman`(0.8) | 哥布林 |
| `actor_slime` | `slime_401`(3.0) `slime_402`(1.0) `slime_403`(0.4) | 史莱姆变种 |
| `actor_player` / `actor_ogre` / `weapon_melee` / `weapon_staff` / `proj_homing` / `proj_explosive` / `proj_energy` | 各 1 个（1.0） | 单候选簇，校验器给 `[提醒]` |

> 想调「某种子弹出现的频率」：直接改它的 `ClusterWeight`，**不用碰代码**。


**高自由度拼装的例子（表里真实存在）**：

| 想做的效果 | 怎么写 |
|---|---|
| 子弹里喷出怪物 | `bullet_301`（tags 带 `tag.projectile`）再让 `Projectiles.proj_bomb_301.OnExpireActor = slime_401` |
| 子弹拥有武器属性、落地后再射一发 | `arrow_202` 的 `Weapon = bow_201`；`proj_arrow_202.OnExpireProjectile = bullet_201` |
| 怪物拿武器但不用子弹（近战） | `ogre_brute` 的 `Weapon = club_401`，`club_401` 的 `Delivery = Melee`、`ProjectileRef` 空 |
| 一次打出怪物而不是子弹 | `orb_501` 的 `Delivery = SpawnActor`、`ActorRef = slime_401` |

> 校验器会查**引用不能成环**（`Weapon/Projectile/Actor/Effect` 四列构成的图）。子弹→怪物→武器→子弹这种环会被拦下。

### 3.3 `Weapons` —— 武器契约

| 列 | 类型 | 含义 |
|---|---|---|
| `Id` / `ObjectId` | string | 武器 id / 必填，指向 `Objects.Id`，那个物体必须带 `tag.weapon` |
| `Trigger` | enum `WeaponTrigger` | `Auto`/`Semi`/`Burst`/`Charge`/`Hold`/`Triggered`/`Manual` |
| `Delivery` | enum `WeaponDelivery` | `Instant`/`Projectile`/`Beam`/`Melee`/`Aura`/`SpawnActor`/`Throw` |
| `Aim` | enum `WeaponAimKind` | `Forward`/`AimPoint`/`AimLock`/`Orbit`/`RandomSpread`/`Grid`/`TargetSeek`/`Ground`/`ScreenEdge`/`AroundSelf`/`Cursor` |
| `FireInterval` | 规则值 | 射击间隔（秒） |
| `BaseDamage` | 规则值 | 基础伤害 |
| `ProjectileSpeed` | 规则值 | 弹速（米/秒） |
| `Range` | 规则值 | 射程（米） |
| `SpreadDeg` | 规则值 | 散布角（度） |
| `Count` | 规则值 | 一次发射几个 |
| `Pierce` / `Bounce` | 规则值 | 穿透 / 弹跳 |
| `Knockback` / `Recoil` | 规则值 | 击退 / 后坐力 |
| `HitRadius` | 规则值 | 命中半径（米） |
| `Lifetime` | 规则值 | 存活时长（秒） |
| `ChargeTime` | 规则值 | 蓄力时长（秒），0 = 不蓄力 |
| `ProjectileRef` | string | → `Objects.Id`，那个物体必须带 `tag.projectile`；`Delivery=Projectile` 时必填 |
| `ActorRef` / `EffectRef` | string | → `Objects.Id`；空 = 无 |
| `DamageType` | enum `DamageType` | `None`/`Kinetic`/`Energy`/`Fire` |
| `TeamLayer` | enum `TeamId` | `Neutral`/`Player`/`Enemy` |
| `InheritWeaponStats` | bool | `TRUE`/`FALSE`：发射出去的子弹是否继承武器数值 |
| `ChainDepthLimit` | int | 熔炉链深上限，≥1 |
| `ChainShrink` | float | 链深衰减，0..1（每深一层乘一次） |
| `Recipe` | string | 熔炉配方：**8 段** TagId，见 §5 |
| `Note` | string | 备注 |

### 3.4 `Projectiles` —— 子弹契约

| 列 | 类型 | 含义 |
|---|---|---|
| `Id` / `ObjectId` | string | 子弹 id / 必填，指向 `Objects.Id`，那个物体必须带 `tag.projectile` |
| `Motion` | enum `ProjectileMotion` | `Straight`/`Homing`/`Orbit`/`Wave`/`Boomerang`/`Falling`/`Spiral`/`Anchor` |
| `HitPolicy` | enum `ProjectileHitPolicy` | `Single`/`Pierce`/`Bounce`/`Splash`/`Explode`/`Chain`/`Return`/`Ignore` |
| `OnExpire` | enum `ProjectileDeath` | `None`/`SpawnEffect`/`SpawnProjectile`/`SpawnActor`/`Explode`/`Inherit` |
| `Speed`/`Radius`/`Lifetime`/`Damage`/`Pierce`/`Bounce`/`Gravity`/`HomingTurn` | 规则值 | 弹体数值 |
| `OnHitProjectile` / `OnHitActor` | string | 命中时生成什么（→ `Objects.Id`） |
| `OnExpireProjectile` / `OnExpireActor` | string | 消散时生成什么（→ `Objects.Id`） |
| `MaxChainDepth` | int | ≥1 |
| `Recipe` | string | 熔炉配方：**8 段** TagId，见 §5 |
| `Note` | string | 备注 |

### 3.5 `StatPacks` —— 武器 / 子弹的数值包

与 `characters.xlsx` 的 `StatPacks` **完全同构**：`PackId` / `Layer` / `StatId` / `Value`，
`(PackId, Layer, StatId)` 唯一，`Layer` ∈ `Base`/`Growth`/`Equipment`/`Buff`/`Temporary`。

武器和子弹的数值挂在这里（例：`wpn_pistol_101` 的 `WeaponDamage=10`、`bullet_101_base` 的 `Damage=6`），
然后武器表用 `wstat:WeaponDamage` 去读它 —— **改数值请优先改这里，而不是把数字写进规则值列**。

### 3.6 `_Meta`

`SchemaVersion`(=1) / `Owner` / `Source` / `Updated`。**`SchemaVersion` 不要改**，改了就是换了表结构，
要同时改生成器、校验器与 C# 读取代码。

---

## 4. 规则值语法（武器/子弹数值列的统一写法）

武器与子弹的**所有数值列**都是 `规则值`，可以写字面量，也可以引用别的数值 ——
这就是「子弹继承武器属性」「伤害随链深增长」「带某标签就翻倍」的实现方式。

| 写法 | 语义 | 例子 |
|---|---|---|
| `12.5` | 字面量 | `12.5`、`-5`、`0` |
| `stat:Dexterity` | 读持有者数值包里的该属性最终值 | `stat:MoveSpeed` |
| `stat:Damage+20%` | 上面的基础上，按百分比层叠加 | `stat:Damage+20%` |
| `wstat:WeaponDamage` | 读**链上来源武器**的该属性（子弹继承武器属性）；来源武器不存在时回落到 `0` | `wstat:WeaponDamage` |
| `wstat:WeaponDamage(5)` | 同上，但来源武器缺失时用括号里的默认值 `5` | `wstat:ProjectileSpeed(25)` |
| `wstat:WeaponDamage+20%` | 来源武器属性 + 20% 百分比层 | — |
| `flags:pierce` | 位矩阵：该物体带 `pierce` 标签 → `1`，否则 `0` | `flags:tag.split` |
| `chain:depth` | 熔炉链深 | `chain:depth` |
| `chain:count` | 已链物件数 | `chain:count` |
| `viable:count` | 当前可发动数（越少越强） | `viable:count` |
| `perf` | 性能预算系数 0..1 | `perf` |
| `rand:a~b` | `a` 到 `b` 之间均匀随机 | `rand:-5~5`、`rand:2~3` |
| `rand:a+b` | 基础值 `a`，**±b×100%** 抖动（`b` 必须是 0~1） | `rand:10+0.5` = 10 ± 50% |

**常见写错（都会在校验器里被拦下）**：

| ❌ 错 | ✅ 对 | 原因 |
|---|---|---|
| `30+50%` | `rand:30+0.5` 或 `stat:X+50%` | `+50%` 只能跟在 `stat:`/`wstat:` 后面；纯数值抖动要写 `rand:` |
| `rand:10+50` | `rand:10+0.5` | `+` 后面是**比例**（0~1），不是百分数 |
| `rand:3~1` | `rand:1~3` | 区间必须小 ≤ 大 |
| `stat:Dexterty` | `stat:Dexterity` | StatId 拼错（报错会给「是否想写 …?」提示） |
| `flags:split` | `flags:tag.split` | `flags:` 后面必须是 `Tags` 表里的完整 `TagId` |
| `stat:Damage+30` | `stat:Damage+30%` | `stat:` 的 `±` 是百分比层，必须带 `%` |
| （空着） | `0` | 规则值列不允许空；空 ≠ 0 |

**取值范围**（超出会报错，防止「射速写成 0 → 每帧无限开火」这类事故）：
`FireInterval` ∈ (0,60] 秒、`Lifetime` ∈ (0,120] 秒、`SpreadDeg` ∈ [0,360] 度、
`Pierce`/`Bounce` ∈ [0,100]、`Count` ∈ [0,100] 等。只对能算出典型值的写法（字面量 / `rand:`）检查；
`stat:`/`wstat:`/`flags:` 运行期才知道，不查范围。

---

## 5. `Recipe` —— 熔炉配方（8 段）

一个配方 = 64 位配方字 = **8 个 TagId**，每个 TagId 占 8 位。

**分隔符用 `|`**：

```text
tag.pistol|tag.pistol|tag.kinetic|tag.pierce|tag.kinetic|tag.pistol|tag.kinetic|tag.pistol
```

> 为什么不用设计稿里的 `.`：`TagId` 本身带 `tag.` 前缀，`tag.a.tag.b` 用 `.` 切分会被切成 4 段，
> 无法还原 8 段。校验器**两种都接受**（`.` 分隔时按 `tag.` 前缀切），但**新写请用 `|`**，一眼能数清段数。

规则：
- 必须**正好 8 段**；空着 = 不进熔炉（合法）。
- 每段必须是 `Tags` 表里存在的 `TagId`。
- 被引用的 tag 的 `BitMask` **不能是 `-1`**（`-1` 表示「不进配方字」）。

---

## 6. StatId 号段（`StatPacks.StatId` 可填的值）

**只加不改**。新增属性要**同时**改 `scripts/core/StatId.cs` 与 `validate_content_xlsx.py` 的 `STAT_ID_VALUES`。

| 号段 | 属性 | 值 |
|---|---|---|
| 生存 | `Hp` `HpMax` `ArmorFlat` `HurtInvulnTime` | 1 2 4 8 |
| 移动 | `MoveSpeed` `MoveSpeedMul` | 100 101 |
| 攻击 | `Damage` `DamageMul` `CritChance` `CritMultiplier` | 200 201 202 203 |
| 预留 | `Reserved900` | 900 |
| 角色（1000+） | `Mana` `ManaMax` `ManaRegenPerSec` `Armor` `ArmorK` | 1000–1004 |
| | `Strength` `Dexterity` `Luck` `StrengthMul` `DexterityMul` `LuckMul` | 1010–1015 |
| | `Stamina` `StaminaMax` `StaminaRegenPerSec` | 1020–1022 |
| **武器（1030+）** | `WeaponFireRate` `WeaponDamage` `WeaponProjectileSpeed` `WeaponProjectileCount` | 1030–1033 |
| | `WeaponSpread` `WeaponRange` `WeaponPierce` `WeaponKnockback` `WeaponRecoil` | 1034–1038 |
| | `WeaponChargeTime` `WeaponCritChance` `WeaponCritMultiplier` | 1039–1041 |
| **弹体（1050+）** | `ProjectileLifeTime` `ProjectileRadius` `ProjectileBounce` `ProjectileHomingSpeed` `ProjectileGravity` | 1050–1054 |

> 倍率型（名字以 `Mul` 结尾、以及 `CritMultiplier`）默认 `1.0`，**写成 0 会让结果恒为 0**，校验器会提醒。

---

## 7. 你可能想改什么 → 改哪张表哪一列

| 我想改…… | 改哪里 |
|---|---|
| 手枪伤害 | `Weapons.BaseDamage`（是 `wstat:WeaponDamage(10)` → 实际值在 `StatPacks` 的 `wpn_pistol_101` / `WeaponDamage`） |
| 武器射速手感 | `Weapons.FireInterval` 或 `StatPacks.WeaponFireRate` |
| 子弹飞行速度 | `Projectiles.Speed` 或 `StatPacks.WeaponProjectileSpeed` |
| 子弹打几下消失 | `Projectiles.Pierce` + `Projectiles.Lifetime` |
| 子弹命中后干什么 | `Projectiles.HitPolicy` + `OnHit*` 两列 |
| 子弹消失后变成什么 | `Projectiles.OnExpire` + `OnExpire*` 两列 |
| 让某把武器打出另一种子弹 | `Weapons.ProjectileRef` → 填目标 `Objects.Id` |
| 让子弹具有武器属性 | `Objects` 里那个子弹行的 `Weapon` 列 → 填武器 `Id` |
| 让子弹里喷出怪物 | `Projectiles.OnExpireActor` → 填怪物 `Objects.Id`，并把 `OnExpire` 设成 `SpawnActor` |
| 新增一把武器 | ① `Objects` 加一行（`Tags` 含 `tag.weapon` + 一个 `WeaponKind`，`StatPacks` 填 `Base` 包名）<br>② `StatPacks` 加该包的数值行<br>③ `Weapons` 加一行，`ObjectId` 指向①的 `Id` |
| 新增一种子弹 | ① `Objects` 加一行（`Tags` 含 `tag.projectile`，`StatPacks` 填 `Base` 包名）<br>② `StatPacks` 加数值行<br>③ `Projectiles` 加一行，`ObjectId` 指向①的 `Id`<br>④ 回到武器表 `ProjectileRef` 指向它 |
| 新增一个怪物 | ① `characters.xlsx` 的 `Characters` 加一行（`BasePackage` 必填）<br>② `Characters.StatPacks` 加该 Base 包的数值行<br>③ 若要让它拿武器：`weapons`/`combat` 里对应加行并在 `Objects.Weapon` 指向 |
| 新增一个标签 | `Tags` 表最后加一行（`TagIndex` = 最大值+1，`Family` 填轴，`BitMask` 用没用过的位或 `-1`） |
| 改中文显示名 | `DisplayKey`（本地化 key），不要直接写中文 |

---

## 8. 校验器到底查什么（失败时的语义）

1. **表结构**：四行表头齐全、字段名集合与期望完全一致（缺列 / 多列都报错）、列顺序、`#data` 标记行、
   无合并单元格、Sheet 名合法、`_Meta.SchemaVersion` 一致。
2. **类型**：`int`/`float`/`bool`（写 `TRUE`/`FALSE`）/`enum:Xxx` 取值必须在允许集合内。
3. **外键闭合**：
   - `Objects.Weapon` → `Weapons.Id`；`Objects.Projectile/Actor/Effect` → `Objects.Id`
   - `Weapons.ObjectId/ProjectileRef/ActorRef/EffectRef` → 对应表
   - `Projectiles.ObjectId/OnHit*/OnExpire*` → `Objects`
   - `Objects.StatPacks` 五段 → 两个 `StatPacks` 表里的 `PackId`
4. **能力位一致**：
   - `Tags` 含 `tag.weapon` ⇔ 有 `Weapons` 行指向它（双向）
   - `Tags` 含 `tag.projectile` ⇔ 有 `Projectiles` 行指向它（双向）
   - `Tags` 含 `tag.actor` ⇒ `StatPacks` 的 `Base` 段非空
   - 一个物体最多命中一个 `ObjectKind` 标签
5. **熔炉簇**：`ClusterWeight` 必填且 > 0；同簇候选的能力位（ObjectKind）必须一致；
   无 `Cluster` 时 `ClusterWeight` 只能是 1；单候选簇给 `[提醒]`（不中断）。
6. **Tags 位阵**：`TagIndex` 从 0 连续；`BitMask` ∈ [0,63] ∪ {-1} 且非 -1 唯一。
7. **`Recipe`**：8 段、每段 TagId 存在、且该 tag 有配方位。
8. **规则值**：逐格真解析（正则 + StatId 存在性 + `flags:` 的 TagId 存在性 + 数值合理范围）。
9. **`StatPacks`**：`StatId` 在白名单、`Layer` 合法、`(PackId, Layer, StatId)` 唯一、`Value` 必填。
10. **`characters.xlsx`**：`Characters` / `StatPacks` / `SkillLoads` 三张业务表一起校验（向后兼容）。
11. **引用回环**：`Objects` 的四个引用列构成的图不能成环。

> `[提醒]` 与 `[错误]` 的区别：`[提醒]` **不会**让构建失败（exit 仍为 0），它提示「表能跑，但很
> 可能不是你的本意」；`[错误]` 会让构建中止（exit 1）。输出顺序固定为**先逐条提醒、最后一行
> `OK: N 项检查通过`**，方便脚本取尾。


---

## 9. 常见错误速查

| 报错 | 怎么修 |
|---|---|
| `缺少列「X」` / `多出未登记的列「X」` | 列的集合被定死了。要加列必须同时改生成器 `COLUMNS` + C# 表读取 + 校验器，走评审 |
| `` 缺少 `#data` 数据起始标记行 `` | 第 5 行 A 列写 `#data`，其余列清空 |
| `TagIndex 必须从 0 连续递增…缺少 [3]` | 标签表中间有洞；按 0..N-1 重排（只影响新加的行，别动已有的） |
| `位 N 已被「tag.x」占用` | 两个标签用了同一个 `BitMask`，换一个没用的位 |
| `stat: 后面的「X」不是已知 StatId` | 拼写错误（报错里会给提示）或该属性还没登记 |
| `引用的「X」在 Y 里不存在` | 外键打错字 / 目标行还没加 |
| `带 tag.actor 的物体必须有非空 Base 段` | `StatPacks` 第 1 段（Base）填包名，并在 `StatPacks` 表里给它填数值 |
| `Weapons 表第 N 行把它当武器用，但它的 Tags 里没有 tag.weapon` | 能力位不一致，补标签或删武器行 |
| `规则值列不能为空` | 显式写 `0` |
| `典型值 … 超出合理范围` | 数值离谱（例如射速写成 1000） |
| `配方必须是 8 个 TagId` | 段数不对，用 `|` 分隔并数够 8 段 |
| `引用成环：a → b → a` | A 的子弹变成 B、B 又变回 A；把其中一条引用清空 |
| `熔炉权重必须 > 0（实际 0）` | `ClusterWeight` 写成了 0 或负数，改成正数（越常见越大） |
| `簇「X」里的候选能力位不一致` | 同簇混进了不同种类的物体（例如武器和子弹），把那个候选挪到别的簇或清空 `Cluster` |
| `没有填 Cluster…ClusterWeight=7 无意义` | 不参与熔炉就别配权重，写 `1` 或补一个 `Cluster` |
| `[提醒] 簇「X」只有 1 个候选` | 单候选簇不影响运行；想让它真的可替换就再补 1~2 个同能力位的候选 |

---

## 10. 提交前自查清单

- [ ] `validate_content_xlsx.py` 打印 `OK: N 项检查通过`，退出码 0
- [ ] 只改了 `data/excel/*.xlsx`（没有手改 `data/generated/`）
- [ ] 新增的 Id 与已有 Id 风格一致（小写下划线）
- [ ] 新增内容至少有一条数据真的引用了它（否则它永远进不了游戏）
- [ ] `git diff` 只看得到你预期的那几个单元格

---

## 6. 标签轴（Family）与维护脚本

2026-11-06 起 Tags 表多了 Family 列。要点：

- **Family 只做索引，不改能力位。** 能力位仍只看 Group（ObjectKind）。
- **绑定器对认不出的轴名当场报错**（ContentBinder.LoadRules），不会静默落 None。
  静默落 None 会让这个 tag 从所有按轴索引的玩法里消失，是最难查的一类失效。
- **校验器按字段名取列，不按列号。** 曾因写死 c[4] 在插列后把 BitMask 全读成 -1。

### 6.1 表结构变更脚本

| 脚本 | 用途 | 幂等 |
|---|---|---|
| migrate_tags_multiaxis.py | 给已有 combat.xlsx 补 Family 列、追加 23 个轴标签、给 26 个物体补多轴标签 | ✅ |
| ix_tag_bitmasks.py | 按生成器内存数据把 Tags.BitMask 对齐回历史值（生成器曾被重跑冲掉引线时用） | ✅ |

### 6.2 ⚠️ 不要重跑 gen_combat_xlsx.py

data/excel/combat.xlsx 是**手改真源**，gen_combat_xlsx.py **整体覆盖**写它。
2026-11-06 曾因重跑该脚本，把手工维护的 BitMask（bit 0~15）全部清零，
导致 30 条「配方里的 tag 没有配方位」错误。

- **要改内容：直接改 xlsx。**
- 生成器里的 TAGS / OBJECTS 只作为「从零重建」的种子，已同步为当前真值；
  若确需重建，先 ix_tag_bitmasks.py 校验再比对 diff。
---

## 7. 标签分层（Tier，2026-11-06）

`Tags` 表多了 `Tier` 列，把标签拆成两层：

| 层 | 谁消费 | 加一个的代价 | 例 |
|---|---|---|---|
| `Logic` | 系统（能力位 / 配方字 / 抗性 / 权重） | **先想规则** | `tag.kinetic` / `tag.pierce` / `tag.rare` |
| `Identity` | 匹配（吸附 / 拾取 / 主题 / 共鸣） | **随手加** | `tag.loot_coin` / `tag.attract_magnet` / `tag.own_player` |

> 层回答「能不能影响玩法」，轴回答「属于哪一类」。两者是两个维度。

### 7.1 标识层六轴

| 轴 | 互斥 | 标签 |
|---|---|---|
| `Loot` | ✅ | `tag.loot_ammo` / `loot_health` / `loot_mana` / `loot_coin` / `loot_relic` / `loot_key` |
| `Attract` | ❌ 可叠加 | `tag.attract_magnet` / `attract_weapon` / `attract_exp` |
| `Faction` | ✅ | `tag.own_player` / `own_enemy` / `own_neutral` |
| `Theme` | ❌ 可叠加 | `tag.theme_forest` / `theme_dungeon` / `theme_forge` |
| `Aura` | ❌ 可叠加 | `tag.aura_fire` / `aura_ice` / `aura_shock` |
| `Craft` | ❌ 可叠加 | `tag.craft_metal` / `craft_wood` / `craft_gem` / `craft_essence` |

### 7.2 标识层的硬约束（校验器强制）

| 约束 | 为什么 |
|---|---|
| `BitMask` **必须是 -1** | 标识标签不得进配方字（否则偷偷影响玩法） |
| `Group` **必须是 `None`** | 标识标签不得产出能力位 |
| `Tier` 必须与轴的层一致 | 防止「挂标识轴却声明 Logic」 |

### 7.3 ⚠️ TagId 命名规则

`TagId` 只允许 **`tag.<小写名>` 单段**，名里可以有下划线但**不能再有点**：

- ✅ `tag.loot_coin`、`tag.attract_magnet`、`tag.own_player`
- ❌ `tag.loot.coin`、`tag.attract.magnet`（校验器判「Id 格式非法」）

### 7.4 「道具互相吸引」怎么写

被吸物与吸引源各挂**自己的** `Attract` 类型标签，运行期做交集：

```csharp
int word = magnetTag >> 5;                        // 标识标签 id ≥ 40，落在 word 1
int mask = ContentRules.PackMask(new[] { magnetTag }, word);
bool hit = (lootMatrix.Mask(loot, magnetTag) & mask) != 0;
```

新增一种磁铁 = 表里加一行 + 物件打上标签，**C# 0 行改动**。

> ⚠️ `ObjectBitMatrix.Mask(row, tag)` 第二个参数是 **tag 下标**，不是 word 下标。

---

## 8. 核心内容集填表（`fill_core_roster.py`）与图鉴（`gen_codex.py`）

### 8.1 批量填人/怪/武器/子弹的数值

```powershell
python tools/data_pipeline/fill_core_roster.py    # 幂等：按主键覆盖，可重复跑
python tools/data_pipeline/validate_content_xlsx.py
```

它写四类内容：① 人物与怪物的 `StatPacks` 数值包 ② `Characters` 行 ③ 人物武器 `Weapons` 行
④ 人物子弹的 `Objects` + `Projectiles` 行，并把 5 个人物的 `SkillLoads` 整体重写（左键槽指向各自武器）。

> ⚠️ **它只写这几样，不重排、不删别的行。** 两个 xlsx 仍是手工编辑的唯一真源。

### 8.2 生成图鉴（数值快速预览 + 游戏内图鉴原型）

```powershell
python tools/data_pipeline/gen_codex.py      # → docs/codex/index.html，浏览器直接打开
```

直读三个 xlsx，**按标签轴分栏配色**（`Kind` 分大类，`Element` / `Rarity` / `Role` 配色），
带搜索框（中英文都能搜）。改表后重跑即可，不需要开引擎。

### 8.3 中文名：`l10n.xlsx`（名字的唯一真源）

> 图鉴"全是英文"不是图鉴的问题 —— 项目里原来**根本没有中文名**。
> `DisplayKey` 列明文规定**不写中文**（它是本地化 key，如 `actor.goblin_grunt`），
> 所以名字单独放在 `data/excel/l10n.xlsx`。**图鉴和将来的游戏 UI 读同一份**。

| Key 形态 | 例子 | 谁在用 |
|---|---|---|
| `actor.*` / `item.*` / `proj.*` / `fx.*` | `actor.slime_401`、`item.pistol_101` | `Objects.DisplayKey`、`Characters.DisplayKey` |
| `tag.*` | `tag.kinetic` | `Tags.TagId` |
| 裸枚举名 | `Pierce`、`Explosive`、`Semi` | 表里的枚举单元格（图鉴直接显示） |

**加任何新内容（人物/怪物/武器/子弹/标签）时，必须同步加一行中文名。**
忘了不会崩，但会收到两处提醒：

```powershell
python tools/data_pipeline/validate_content_xlsx.py
#   → [错误] l10n.xlsx/L10n: 缺少中文名：actor.新怪物（被 Characters.新怪物 引用）

python tools/data_pipeline/gen_codex.py
#   → [warn] 1 个 key 缺中文名（已原样显示英文）： - 新怪物
```

图鉴里**中文名做主标题、英文 id 做副标题**（灰色等宽小字，给策划对表用），两者都不丢。

> ⚠️ `gen_l10n_xlsx.py` **只在首次生成骨架时用一次**，之后手工维护 `l10n.xlsx`；
> 重跑会覆盖手改内容（同 `combat.xlsx` 的规矩）。

### 8.4 填表时的六个坑（都是实际踩过的）

| 坑 | 症状 | 正解 |
|---|---|---|
| 追加行不写主键列 | 校验器报一片「Id 不能为空」 | `_upsert` 里**强制**写主键，别指望调用方传 |
| 同 `PackId` 跨 Base/Growth 两层 | `FlushStatPacks` 抛「同时出现两层」 | 成长包共用或换 id |
| 规则值列留空 | 「规则值列不能为空；没有效果请显式写 0」 | 无效果写 `0`；`MaxChainDepth` 例外，**必须 >= 1** |
| `VisualSlot` 留空 | `ParseInt` 抛 `FormatException` | 必填整数，每人/怪一个 |
| 只改表不改 C# | `CharacterXlsxParityTests` 红 | 同步 `CharacterContentSet.BuildBuiltinV1()` |
| 用 `wstat:` 继承写法 | 图鉴看不到最终数字 | 图鉴要展示的武器写**独立基线** |

### 8.4 新增 StatId 要改三处

`stat:` 规则里能用的属性名由 `scripts/core/StatId.cs` 定义，校验器另有 `STAT_ID_VALUES` 号段表，
`characters.xlsx` 的 `StatPacks.StatId` 还经过另一套登记。**三处必须同时加**，否则报「不是已知 StatId」。
（例：冲刺的 `DashSpeedMul=1055 / DashDuration=1056` 就是照这个流程补的。）
