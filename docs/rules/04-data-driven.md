> 状态: v1  最后更新: 2026-11-03

# 规则 4 · 数据驱动（所有数值/表现都走文件，代码里不许有魔法数）

## 1. 规则原文

> 「所有的数值，全部使用文件或者 excel 或者插件可视化修改！！例如颜色，伤害，特效文件，子弹样式，人物等！」

**一句话落地**：**运行期代码里出现的游戏数值字面量 = 缺陷（Defect），不是风格问题。** 数值、颜色、特效引用、子弹样式、人物定义、怪物定义，一律来自 `data/excel/*.xlsx`（唯一真源）经构建期生成到 `data/generated/` 的产物。

---

## 2. 为什么

| 理由 | 说明 |
|---|---|
| **调参速度** | 手感优先的射击肉鸽，一天要改几十次数值。改代码→编译→重启 的循环会让"手感优先"落空；改 Excel→重新生成→重进 是秒级。 |
| **人和 AI 都不会漏改** | 当"伤害=12"只存在于表中时，AI 改平衡只需改一行 YAML/Excel，不会碰坏逻辑；Review 时 diff 是可读的。 |
| **规则 5/6/7 的地基** | 「组合扩展 0 文件」的前提就是"新内容 = 新数据行"（见 `06-extensibility.md`）。 |
| **可视化编辑** | 用户要求"插件可视化修改"——颜色、曲线、特效需要看得见地调，这要求值本身是 `Resource` 字段而不是代码常量。 |
| **可回滚/可审阅** | `.xlsx` 与生成的 `.tres` 都在版本库里，平衡改动是可 diff 的历史。 |

---

## 3. 硬规则（红线清单）

### 3.1 什么算违规

代码中（`*.cs` 的 `Game.*` 命名空间、`*.gd` 的 UI 层）出现下列**字面量**且未标注 `// arch-ok:` = 违规：

| 类别 | 违规示例 | 正确来源 |
|---|---|---|
| 伤害 / 血量 / 防御 | `dmg = 12;` `maxHp = 100f;` | `Characters.Base` 列 / `Weapons.Damage` 列 / `StatId.Hp` |
| 速度 / 加速度 / 转身 | `speed = 6f;`（含玩家移动速度） | `Characters.Base` 的 `MoveSpeed` 等 |
| 射速 / 冷却 / 装填 | `fireInterval = 0.12f;` | `Weapons.FireInterval` |
| 暴击率 / 暴击倍率 | `if (rnd < 0.15f) dmg *= 2f;` | `Weapons.CritChance` / `Weapons.CritMultiplier` |
| 掉落率 / 权重 / 价格 | `dropChance = 0.3f;` `price = 50;` | `LootTables` / `Items` 表 |
| 尺寸 / 缩放 / 碰撞盒 | `Scale = new Vector3(1.5f,1.5f,1.5f);` `Radius = 0.4f;` | `Characters.Scale` / `Projectiles.CollisionRadius` |
| 颜色 | `new Color(0.9f, 0.3f, 0.2f)` | `Palette` 资源的颜色 id |
| 持续时间 / 层数 / 上限 | `burnDuration = 3f;` `maxStacks = 5;` | `Effects.Duration` / `Modifiers` 表 |
| 特效参数（粒子数、拖尾长度、闪光时长） | `particles = 30;` | `Effects` 表 → `EffectDef` |
| 子弹样式（拖尾颜色、大小、形状 id） | `trailWidth = 0.06f;` | `Projectiles` 表 → `ProjectileDef` |
| 人物/怪物基础定义 | 代码里 `new CharacterDef { DisplayName="Slime" }` | `Characters` 表 |
| 技能数值 | `damageMul = 1.5f;` | `Skills` 表 + `Formula` 表 |
| 关卡/刷怪预算 | `spawnCount = 20;` | `Waves` 表 |
| 相机/手感参数 | `shakeAmount = 0.3f;` `hitStop = 0.05f;` | `Feel` 表（打击感参数同样受本规则约束） |

### 3.2 允许的例外（必须标注）

结构性常量不算"游戏数值"，但**必须在同一行或上一行写 `// arch-ok: <理由>`**，否则视为违规（架构守卫 GS005 会按此规则扫描，见 §10）。

```csharp
// arch-ok: 结构性常量，代表"无/关闭"
const float Disabled = 0f;
var dir = (target - self).NormalizedSafe();   // arch-ok: 纯归一化，不含平衡值
if (delta <= 0f) return;                      // arch-ok: 数值防御，非平衡参数
var half = value * 0.5f;                      // arch-ok: 几何计算中的 1/2
```

**白名单的"结构性常量"清单**（只有这些可以直接写）：

| 允许值 | 语义 |
|---|---|
| `0` / `0f` | 零、空、无 |
| `1` / `1f` | 单位、满、整数步长 |
| `-1`（`-1f`） | 哨兵值「无效/未设置」 |
| `0.5f` / `2f` | 仅用于几何/插值（中点、半、倍增索引），不得表示任何平衡倍率 |
| `float.Epsilon` / `1e-6f` | 浮点比较容差 |
| `int.MaxValue` 等 | 上限哨兵（如"无限层数"）；**UI 展示层例外**见 3.3 |

**禁止**以"常量集中在一个 `BalanceConsts.cs` 文件"的方式绕过：把魔法数挪进一个类只是把它们从代码搬到了另一个代码文件，依然不可配、不可热重载、策划改不了，同样判违规。

### 3.3 两个明确例外分区

| 例外 | 范围 | 边界 |
|---|---|---|
| **界面布局常量** | 仅 `Game.UI` 里的边距、字号、锚点百分比 | 不得包含任何影响战斗结果的量；出现者退回规则 4 |
| **构建期工具** | `tools/data_pipeline/` 的 Python 脚本 | 只处理数据，不进运行期 |

---

## 4. 数据分层金字塔（三层模型）

```text
┌─────────────────────────────────────────────────────────────┐
│ L1 静态定义（Static Def）  —— 策划改，Excel 唯一真源          │
│    载体: data/excel/*.xlsx → [构建期] → data/generated/*.tres │
│    语义: 不可变。运行期只读，绝不 load 后修改                 │
│    例: Characters / Weapons / Projectiles / Effects / Palette │
├─────────────────────────────────────────────────────────────┤
│ L2 运行时实例（Runtime Instance）—— 程序 & 战斗系统写          │
│    载体: StatPackage 的 Base/Growth/Equipment/Buff/Temporary  │
│          各层 + 运行时 Def 副本（见 05-numeric-package.md）    │
│    语义: 值语义不可变，改动 = 产生新包                          │
│    例: "这个具名精英怪的当前攻击力"                              │
├─────────────────────────────────────────────────────────────┤
│ L3 存档（Save）—— 只在存盘点写，只在载入时读                    │
│    载体: user://save/<slot>.json + schema_version             │
│    语义: 只存"与 L1 的差量 + 运行进度"，不存 L1 的冗余副本      │
└─────────────────────────────────────────────────────────────┘
```

### 4.1 谁写谁读

| 层 | 写入方 | 读取方 | 允许的修改时机 |
|---|---|---|---|
| L1 静态定义 | 策划（Excel）+ 构建期脚本 | 所有运行期系统（只读） | 仅重新生成（构建期） |
| L2 运行时实例 | `Game.Combat`（结算）、`Game.Skill`（效果）、`Game.Data`（装配） | 渲染、UI、AI、数值计算 | 任意帧，但只能"产生新值" |
| L3 存档 | `SaveService` | `SaveService` → 重建 L2 | 存盘点 / 退出 / 自动存档 |

**硬约束**

1. L1 → L2 的拷贝必须**深拷贝到运行时层**，绝不允许运行期 `def.Power *= 1.2f` 直接改 L1 资源（会污染后续所有实例与热重载）。
2. L3 **不得**存 L1 全量；只存 `{"charId":"Slime", "growthDelta":{...}, "buffIds":[...]}` 这类引用+差量，重建时由 L1+L3 重新 Fold。
3. 任何"我从存档里读到了伤害值"的写法判违规。

---

## 5. Excel 表规范（唯一真源格式）

### 5.1 文件与 Sheet 组织

> **总原则（与 `ARCHITECTURE.md` §4.2 一致）**：**按主题分文件，一个文件内一张表 = 一个 Sheet，Sheet 名 = 表名。**
> 不存在 `Weapons.xlsx` / `Characters.xlsx` 这种"一表一文件"的组织方式，也不存在"一张 Sheet 装两张表"。

**四个主题文件与 Sheet 归属（权威口径）**

| 主题文件 | Sheet 列表 | 负责内容 | 主要维护者 |
|---|---|---|---|
| `data/excel/characters.xlsx` | `Characters`、`Status` | 角色/怪物本体与状态定义 | 策划 / 角色任务 |
| `data/excel/combat.xlsx` | `Weapons`、`Projectiles`、`Skills` | 战斗玩法：武器、子弹、技能 | 战斗 / 技能任务 |
| `data/excel/presentation.xlsx` | `Effects`、`Triggers` | 表现与反馈：特效、事件触发 | 特效 / 表现任务 |
| `data/excel/progression.xlsx` | `Growth`、`Formula`、`Waves`、`Loot` | 成长与流程：成长曲线、公式、波次、掉落 | 肉鸽流程任务 |

> 新表先判断属于哪个主题 → 加进对应 `.xlsx` 的**新 Sheet**；只有明显不属于以上四主题时才新建主题文件（如 `audio.xlsx`），并回写 `ARCHITECTURE.md` §4.2。
> **下文所有示例表都按此归位**：`Characters` → `characters.xlsx`；`Weapons`/`Projectiles` → `combat.xlsx`；`Effects` → `presentation.xlsx`；`Formula`/`StatDefs` → `progression.xlsx`。

| 约定 | 规定 |
|---|---|
| 源目录 | `data/excel/*.xlsx`（**仓库内，纳入版本控制**） |
| 文件命名 | 小写复数主题名：`characters.xlsx` 而非 `Character.xlsx`；禁止中英文混用 |
| 一表一 Sheet | **Sheet 名 = 表名**（Pascal 复数，如 `Characters`），生成物目录同名小写（`data/generated/characters/`） |
| 禁止 | 一个 `.xlsx` 里塞两个同义 Sheet（如 `Weapon` 与 `Weapons` 并存）；单表独立成文件（如 `Weapons.xlsx`） |
| 表名 → 生成物 | `Characters` → `data/generated/characters/<id>.tres` + `data/generated/index/characters.json` |
| 列顺序 | 无关（按第 1 行名字取值），但约定 `Id` 永远在第 1 列 |
| 空行 | 整行为空的跳过；表尾追加「注释行」请用第 1 列写 `#` 开头 |
| 合并单元格 | **禁止**（解析器无法可靠还原） |
| 公式单元格 | **禁止**（解析器读到的是缓存值，易与显示不一致）；需要计算请进 `progression.xlsx` 的 `Formula` Sheet 由运行期求值 |

**引用写法（全文统一）**：提到某张表时写「`combat.xlsx` 的 `Projectiles` Sheet」，不要写「`Projectiles` 表文件」或「`Projectiles.xlsx`」。错误信息格式见 §9.2，形如 `[错误] combat.xlsx/Projectiles 单元格 J9（...）`。

### 5.2 表头四行规约（**必守**）

| 行号 | 内容 | 示例 | 解析规则 |
|---|---|---|---|
| **第 1 行** | **字段名**（CamelCase，必须与 C# `Def` 属性名完全一致） | `Id` `DisplayName` `BaseMoveSpeed` | 大小写敏感精确匹配；不允许空格/中文/横线 |
| **第 2 行** | **字段类型**（见 §5.4 类型词表） | `string` `int` `float` `color` `resource` `bool` `enum:TeamId` `curve` `vector2` | 不在词表内 = 构建失败 |
| **第 3 行** | **中文显示名**（本地化/编辑器显示） | `编号` `显示名称` `基础移速` | 构建器导出到 JSON 索引的 `display` 映射，供 `[Tool]` 检查器与策划核对 |
| **第 4 行** | **备注 / 取值域**（可留空） | `唯一ID，英文小写下划线` `>0，单位:米/秒` | 导出到 JSON 索引的 `note`；不作为校验依据（校验以 §5.6 字段规约表为准） |
| **第 5 行起** | 数据行 | `slime_basic, 史莱姆, 3.2` | 每行 = 一个实例 |

> **AI 提示**：读表时永远按"第 1-4 行是表头，数据从第 5 行开始"处理。`openpyxl` 读到的 `header_rows = 4`。

### 5.3 Id 规则（外键的基础）

| 规则 | 规定 |
|---|---|
| 字符集 | `^[a-z][a-z0-9_]{1,47}$`（小写字母开头，仅小写字母/数字/下划线，2–48 字符） |
| 全局唯一性 | 在**同一张表**内唯一；跨表**不去重**（允许同名：怪物 `slime_basic` 与武器 `slime_basic` 不冲突，因为带表名前缀引用） |
| 跨表引用 | 一律写裸 Id（生成器按字段声明的目标表解析，见 §5.6 `ref:` 类型） |
| 保留 Id | `default` / `null` / `none` / `debug` 为保留值，禁止作为业务数据 Id |

### 5.4 字段类型词表（Excel 第 2 行写法）

| 第 2 行写法 | 解析结果 | Godot/C# 目标类型 | 单元格取值格式 | 默认（空单元格） |
|---|---|---|---|---|
| `string` | 字符串 | `string` | `slime_basic` | `""` |
| `text` | 长字符串（含换行/中文描述） | `string` | 支持 `\n` | `""` |
| `int` | 整数 | `int` | `12` | `0` |
| `float` | 浮点 | `float` | `1.25` / `1,25`（逗号视为小数点） | `0f` |
| `bool` | 布尔 | `bool` | `TRUE`/`FALSE`/`1`/`0`/`是`/`否` | `false` |
| `color` | 颜色 | `Color` | `#RRGGBB` / `#RRGGBBAA` / `#AARRGGBB`（Excel 默认的 ARGB 也被接受） | `Color.White`（并**告警**） |
| `resource` | 资源路径 | `Resource`（按目标类型加载） | `res://art/fx/muzzle_flash.tres` | `null`（若列必填则报错） |
| `curve` | 曲线 | `Curve` | `#8x1x1,0,1,1,0`（Godot Curve.to_string() 格式） | 线性曲线 |
| `enum:TeamId` | 枚举 | `TeamId` | `Player`（枚举成员名，大小写不敏感，但生成后规范化） | 枚举零值（必须显式定义 `None = 0`） |
| `vector2` | 二维向量 | `Vector2` | `(1.5, 0.4)` 或 `1.5;0.4` | `Vector2.Zero` |
| `vector3` | 三维向量 | `Vector3` | `(1, 2, 0)` 或 `1;2;0` | `Vector3.Zero` |
| `stat:<StatId>` | 单条属性覆盖 | `(StatId, float)` | `Hp=120` | — |
| `stats` | 属性映射（**推荐**） | `StatPackage` 的 Base 覆盖行 | `Hp=120;MoveSpeed=3.2;CritChance=0.05` | 全默认 |
| `ref:Weapons` | 单外键引用 | `string idol` → 解析为对应 `*Def` | `shotgun_basic` | `null`（若必填则报错） |
| `refs:Skills` | 外键数组引用 | `string[]` | `dash;shove;takeover` | `[]` |
| `curve2d` / `gradient` | 复杂表现资源 | `Curve2D` / `Gradient` | `res://...`（推荐直接给 Resource 路径） | `null` |

> 校验以本文 §5.6 的「字段规约表」为准；类型词表只负责**解析**，规约表负责**业务合法性**（必填、范围、外键存在）。

### 5.5 数值书写规约（避免"看起来对但生成不对"）

| 项目 | 规定 |
|---|---|
| 小数点 | 统一用 `.`（生成器把 `,` 当小数点处理，但请写 `.`） |
| 单位 | 在**第 4 行备注**里写单位（`单位:米/秒`），值本身永远是引擎内部单位（米、秒） |
| 百分比 | 一律写**小数**：暴击率 15% 写 `0.15`，不写 `15`（第 3 行显示名可写「暴击率(0~1)」） |
| 角度 | 一律写**弧度**；需要度数的字段在第 4 行注明「度」并由生成器转换（转换逻辑写进生成器，不写进运行期代码） |
| 时间 | 秒（float），毫秒只存在于 UI 展示 |
| 空 vs 0 | 空单元格 = 取默认值（可能告警）；显式 `0` = 明确设为零，语义不同，生成器必须区分 |

---

## 6. 示例表（完整 Markdown 预览）

> 以下四张表是**演示用最小集**，字段以本表为准落地。字段名 = C# `Def` 属性名，生成器按名绑定。

### 6.1 `Characters`（人物 / 怪物 —— 同一张表，用 `Kind` 区分）

| 第1行字段名 | Id | Kind | DisplayName | Team | Base | PrefabPath | VisualPalette | Scale | Skills | ProjectileStyle | MovementDriver | DropTable | Tags |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 第2行类型 | `string` | `enum:ActorKind` | `string` | `enum:TeamId` | `stats` | `resource` | `ref:Palettes` | `float` | `refs:Skills` | `ref:Projectiles` | `enum:MovementDriverKind` | `ref:LootTables` | `refs:Tags` |
| 第3行显示名 | 编号 | 类别 | 显示名称 | 阵营 | 基础属性 | 预制体路径 | 配色方案 | 缩放 | 技能列表 | 子弹样式 | 移动驱动 | 掉落表 | 标签 |
| 第4行备注 | 小写下划线 | Player/Enemy/Boss | 中文显示名 | Player/Enemy/Neutral | 分号分隔 key=value | res:// 路径 | 调色板 id | 1=原尺寸 | 按顺序释放 | 空=不射击 | Input/Chase/Fly/Null；夺舍时替换 | 空=不掉落 | 用于筛选 |
| `player_runner` | `Player` | 玩家·跑攻者 | `Player` | `Hp=100;MoveSpeed=6.0;CritChance=0.05;HurtInvulnTime=0.8` | `res://scenes/actors/player.tscn` | `pal_player_main` | `1.0` | `dash;takeover;dodge_roll` | `proj_player_bullet` | `Input` | — | `player` |
| `slime_basic` | `Enemy` | 史莱姆 | `Enemy` | `Hp=40;MoveSpeed=2.4;ContactDamage=6` | `res://scenes/actors/slime.tscn` | `pal_slime_green` | `1.0` | `hop_attack` | — | `Chase` | `loot_common` | `melee;chase` |
| `slime_spitter` | `Enemy` | 喷吐史莱姆 | `Enemy` | `Hp=55;MoveSpeed=1.9;FireInterval=1.6` | `res://scenes/actors/slime_spitter.tscn` | `pal_slime_violet` | `1.15` | `spit_bolt` | `proj_slime_bolt` | `Chase` | `loot_common` | `ranged;chase` |
| `brute_charger` | `Enemy` | 冲锋蛮怪 | `Enemy` | `Hp=120;MoveSpeed=3.0;ChargeSpeedMul=2.2;ChargeWindup=0.7` | `res://scenes/actors/brute.tscn` | `pal_brute_rust` | `1.4` | `telegraph_charge;body_slam` | — | `Chase` | `loot_rare` | `charger;elite` |
| `boss_toyking` | `Boss` | 玩具王 | `Enemy` | `Hp=1800;MoveSpeed=2.0;PhaseCount=3` | `res://scenes/actors/boss_toyking.tscn` | `pal_boss_regal` | `3.0` | `summon_toys;stomp_slam;toy_beam` | `proj_boss_orb` | `Chase` | `loot_boss` | `boss;phased` |

> **怪物 = `Characters` 表的 `Kind=Enemy/Boss` 行**，不存在独立的「怪物表」。这是规则 6「人物与怪物数值封成同一个包、可互相赋值」在数据层的体现：夺舍走查要求 `Characters.Slime` 的 Base 包能直接赋给 `Characters.Player` 槽位（见 `06-extensibility.md` §4）。

### 6.2 `Weapons`

| Id | DisplayName | FireMode | FireInterval | Damage | DamageType | ProjectileSpeed | ProjectileDef | PelletCount | SpreadDeg | MagSize | ReloadTime | CritChance | CritMultiplier | RecoilKick | MuzzleFx | HitFx | SoundFire |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 类型 | `string` | `string` | `enum:FireMode` | `float` | `float` | `enum:DamageType` | `float` | `ref:Projectiles` | `int` | `float` | `int` | `float` | `float` | `float` | `float` | `ref:Effects` | `ref:Effects` | `resource` |
| 显示名 | 编号 | 显示名称 | 射击模式 | 射击间隔 | 单发伤害 | 伤害类型 | 弹速 | 子弹样式 | 弹丸数 | 散布角 | 弹匣容量 | 装填时长 | 暴击率 | 暴击倍率 | 后坐力 | 枪口特效 | 命中特效 | 开火音效 |
| 备注 | 小写下划线 | 中文 | Single/Auto/Charge | 秒，0.12≈8发/秒 | >0 | Kinetic/Energy/Fire | 米/秒 | 引用 Projectiles.Id | ≥1 | 度 | ≥1 | 秒 | 0~1 | ≥1 | 像素位移 | 空=无 | 空=无 | 空=无 |
| `pistol_basic` | 基础手枪 | `Single` | `0.22` | `12` | `Kinetic` | `18` | `proj_player_bullet` | `1` | `1.5` | `12` | `0.9` | `0.05` | `2.0` | `0.06` | `fx_muzzle_small` | `fx_hit_spark` | `res://audio/sfx/pistol.ogg` |
| `smg_toy` | 玩具冲锋枪 | `Auto` | `0.075` | `6` | `Kinetic` | `20` | `proj_player_bullet_small` | `1` | `4.0` | `32` | `1.3` | `0.03` | `2.0` | `0.03` | `fx_muzzle_small` | `fx_hit_spark` | `res://audio/sfx/smg.ogg` |
| `shotgun_boom` | 爆裂霰弹 | `Single` | `0.62` | `7` | `Kinetic` | `16` | `proj_player_bullet_small` | `7` | `12.0` | `6` | `1.6` | `0.05` | `2.0` | `0.35` | `fx_muzzle_wide` | `fx_hit_dust` | `res://audio/sfx/shotgun.ogg` |
| `rail_lance` | 轨道长枪 | `Charge` | `1.1` | `85` | `Energy` | `40` | `proj_rail_beam` | `1` | `0.0` | `4` | `2.0` | `0.20` | `2.5` | `0.5` | `fx_muzzle_charge` | `fx_hit_burn` | `res://audio/sfx/rail.ogg` |

### 6.3 `Projectiles`（子弹样式）

| 第1行字段名 | Id | DisplayName | CollisionRadius | ProjectileSpeed | MaxRange | Pierce | Gravity | ShapeId | TrailColor | TrailWidth | TrailLifetime | CoreScale | HomingStrength | ImpactBehavior | OnHitFx | SpawnFx |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 类型 | `string` | `string` | `float` | `float` | `float` | `int` | `float` | `enum:ProjectileShape` | `color` | `float` | `float` | `float` | `float` | `enum:ImpactBehavior` | `ref:Effects` | `ref:Effects` |
| 显示名 | 编号 | 显示名称 | 碰撞半径 | 弹速 | 最大射程 | 穿透数 | 重力 | 造型 | 拖尾颜色 | 拖尾宽度 | 拖尾时长 | 弹芯缩放 | 追踪强度 | 命中行为 | 命中特效 | 生成特效 |
| 备注 | 小写下划线 | 中文 | 米 | 米/秒 | 米 | 0=不穿透 | 米/秒² | Sphere/Capsule/Cube/Star | #RRGGBB | 米 | 秒 | 1=原尺寸 | 0=直线 | Destroy/Pierce/Explode/Bounce | 空=无 | 空=无 |
| `proj_player_bullet` | 玩家标准弹 | `0.10` | `18` | `22` | `0` | `0` | `Capsule` | `#FFD54A` | `0.05` | `0.15` | `0.35` | `0` | `Destroy` | `fx_hit_spark` | `fx_muzzle_small` |
| `proj_player_bullet_small` | 玩家小弹 | `0.08` | `20` | `18` | `0` | `0` | `Capsule` | `#FFE082` | `0.04` | `0.12` | `0.28` | `0` | `Destroy` | `fx_hit_spark` | `fx_muzzle_small` |
| `proj_rail_beam` | 轨道光束 | `0.16` | `40` | `40` | `3` | `0` | `Capsule` | `#7FE3FF` | `0.11` | `0.25` | `0.55` | `0` | `Pierce` | `fx_hit_burn` | `fx_muzzle_charge` |
| `proj_slime_bolt` | 史莱姆弹 | `0.14` | `9` | `14` | `0` | `9.8` | `Sphere` | `#7BD66A` | `0.07` | `0.2` | `0.45` | `0` | `Explode` | `fx_slime_splat` | `fx_slime_windup` |
| `proj_boss_orb` | 玩具王光球 | `0.30` | `6` | `30` | `0` | `0` | `Sphere` | `#C078FF` | `0.14` | `0.4` | `0.8` | `1.2` | `Explode` | `fx_boss_burst` | `fx_boss_charge` |

### 6.4 `Effects`（特效）

| Id | DisplayName | FxScene | Duration | Loop | Scale | Color | EmitCount | AttachSlot | FollowTarget | ScreenShake | HitStop |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 类型 | `string` | `string` | `resource` | `float` | `bool` | `float` | `color` | `int` | `enum:VisualSlot` | `bool` | `float` | `float` |
| 显示名 | 编号 | 显示名称 | 特效首选项 | 时长 | 循环 | 缩放 | 染色 | 粒子数 | 挂点 | 跟随目标 | 屏震 | 卡帧 |
| 备注 | 小写下划线 | 中文 | res:// 的 fx .tscn | 秒 | 是否持续 | 1=原尺寸 | #RRGGBB | 0=用资源默认 | Muzzle/Hit/Feet/Body | 是否跟随 | 0=不震 | 秒，不要 >0.12 |
| `fx_muzzle_small` | 小枪口火花 | `res://art/fx/muzzle_small.tscn` | `0.08` | `FALSE` | `1.0` | `#FFE9A8` | `0` | `Muzzle` | `TRUE` | `0` | `0` |
| `fx_muzzle_charge` | 蓄能枪口 | `res://art/fx/muzzle_charge.tscn` | `0.25` | `FALSE` | `1.3` | `#7FE3FF` | `0` | `Muzzle` | `TRUE` | `0.05` | `0` |
| `fx_hit_spark` | 命中火花 | `res://art/fx/hit_spark.tscn` | `0.12` | `FALSE` | `1.0` | `#FFFFFF` | `14` | `Hit` | `FALSE` | `0.04` | `0.03` |
| `fx_slime_splat` | 黏液飞溅 | `res://art/fx/slime_splat.tscn` | `0.35` | `FALSE` | `1.2` | `#7BD66A` | `20` | `Hit` | `FALSE` | `0.06` | `0.04` |
| `fx_boss_burst` | Boss 爆散 | `res://art/fx/boss_burst.tscn` | `0.9` | `FALSE` | `2.0` | `#C078FF` | `60` | `Hit` | `FALSE` | `0.25` | `0.08` |

> **注意**：`Weapons.Damage` 与 `Projectiles.ProjectileSpeed` **语义重复**。约定：**伤害以 `Weapons.Damage` 为准**，`Projectiles.ProjectileSpeed` 是"子弹飞行表现速度"，若某武器需覆盖飞行速度则在 `Formula`/运行时取 `max(weapon, projectileDef)`。此重复点已列入 §12 仲裁项。

---

## 7. 字段 → Godot 类型映射（生成器实现契约）

| Excel 类型 | 生成器写入 `.tres` 的写法 | C# 侧属性 | 备注 |
|---|---|---|---|
| `string` | `DisplayName = "史莱姆"` | `string` | 转义 `"` |
| `text` | 同上，保留 `\n` | `string` | 多行 |
| `int` | `HpMax = 40` | `int` | |
| `float` | `MoveSpeed = 2.4` | `float` | 必须带小数点 |
| `bool` | `Loop = false` | `bool` | |
| `color` | `TrailColor = Color(1, 0.835, 0.29, 1)` | `Color`（`[Export] Color`） | 统一输出 `Color(r,g,b,a)` 四位浮点，避免 `#AARRGGBB` 歧义 |
| `resource` | `FxScene = ExtResource("1_abcd")` + 顶部资源声明 | `PackedScene` / `Resource` | 生成器负责把 `res://` 路径登记进 `[ext_resource]` 段 |
| `curve` | `Falloff = SubResource("Curve_3f2")` | `Curve` | 生成子资源 |
| `enum:X` | `Kind = 1`（整数值）或 `Kind = "Enemy"`（Godot 支持 `@export_enum` 名） | `enum` | **统一写整数** + 生成器在 JSON 索引里附带 `enum` 名表，便于人读 |
| `vector2/3` | `Offset = Vector2(1.5, 0.4)` | `Vector2/Vector3` | |
| `stats` | `Base = SubResource("StatPackage_base")` | `StatPackage` | 见 `05-numeric-package.md`，生成 `StatPackage` 子资源 |
| `ref:Weapons` | `ProjectileStyle = ExtResource("...proj_...tres")` | `ProjectileDef` | 生成期解析外键，写具体路径 |
| `refs:Skills` | `Skills = Array[ExtResource("...")]([...])` | `Godot.Collections.Array<SkillDef>` | 有序数组，顺序即释放顺序 |

### 7.1 生成物样例

`data/generated/projectiles/proj_player_bullet.tres`（由 `tools/data_pipeline/` 生成，**禁止手改**）：

```text
[gd_resource type="Resource" script_class="ProjectileDef" load_steps=3 format=3]

[ext_resource type="Script" path="res://scripts/data//ProjectileDef.cs" id="1_script"]
[ext_resource type="PackedScene" path="res://art/fx/hit_spark.tscn" id="2_hitfx"]

[sub_resource type="Curve" id="Curve_falloff"]
_data = [Vector2(0, 1), 0.0, 0.0, 0, 0, Vector2(1, 0), 0.0, 0.0, 0, 0]
point_count = 2

[resource]
script = ExtResource("1_script")
id = "proj_player_bullet"
schema_version = 1
display_name = "玩家标准弹"
collision_radius = 0.1
projectile_speed = 18.0
max_range = 22.0
pierce = 0
gravity = 0.0
shape_id = 1
trail_color = Color(1, 0.835, 0.29, 1)
trail_width = 0.05
trail_lifetime = 0.15
core_scale = 0.35
homing_strength = 0.0
impact_behavior = 0
on_hit_fx = ExtResource("2_hitfx")
falloff = SubResource("Curve_falloff")
```

`data/generated/index/projectiles.json`：

```json
{
  "table": "Projectiles",
  "schema_version": 1,
  "generated_at": "2026-11-03T12:00:00+08:00",
  "source_file": "data/excel/combat.xlsx",
  "source_sheet": "Projectiles",
  "header_rows": 4,
  "generator_version": "0.1.0",
  "fields": [
    { "name": "CollisionRadius", "type": "float", "display": "碰撞半径", "note": "米" },
    { "name": "TrailColor", "type": "color", "display": "拖尾颜色", "note": "#RRGGBB" }
  ],
  "entries": {
    "proj_player_bullet": {
      "res": "res://data/generated/projectiles/proj_player_bullet.tres",
      "row": 9
    },
    "proj_rail_beam": {
      "res": "res://data/generated/projectiles/proj_rail_beam.tres",
      "row": 12
    }
  }
}
```

> **`row` 字段是刻意保留的**：校验失败或运行时异常可以直接报「combat.xlsx / Projectiles 第 12 行」，把人和策划直接送回 Excel 单元格，实现 §9 的"第几行第几列"要求。

### 7.2 schema_version 与运行时校验

| 位置 | 内容 | 运行时行为 |
|---|---|---|
| `*.tres` 的 `schema_version` 字段 | 每个生成资源都写 | 加载时与 C# 代码内 `CurrentSchemaVersion` 常量比对 |
| `index/*.json` 的 `schema_version` | 表级版本 | 与上者必须一致，不一致 = 生成物损坏 |
| 版本不符 | — | **启动即抛错并中断**，中文报错指引：「生成物版本 2 与代码期望 3 不符，请运行 `tools/data_pipeline/build.ps1`」 |

`CurrentSchemaVersion` 是**代码常量，允许**（属于结构性版本号，不是游戏数值），但必须集中在 `scripts/data/Schema/SchemaVersions.cs`（`Game.Data` 命名空间） 一处。

---

## 8. 修改可视化的三条路径（选择标准）

| 路径 | 适用 | 谁用 | 优点 | 代价/风险 | 必须遵守 |
|---|---|---|---|---|---|
| **P1 Excel** | 数值平衡、掉落、关卡、词条、**任何影响胜负的量** | 策划 / AI 批量改 | 唯一真源，可批量、可审阅 diff、可校验 | 需要重跑生成器（秒级） | **平衡类数值必须走 P1**，不允许只在 `.tres` 改 |
| **P2 Godot 检查器直接改 `.tres`** | 表现类调参：拖尾宽度、颜色微调、特效缩放、挂点偏移 | 程序/美术调试 | 实时看效果、无需生成 | 生成物被手改会在下次生成时**被覆盖丢失** | 调好后**必须回写 Excel** 并重跑生成器；`.tres` 里手改的行必须提交到 Excel 才算落地 |
| **P3 自研编辑器插件**（`addons/biu_biu_editor/`） | 需要"看得见"的：调色板取色、Gradient/Curve 编辑、特效实时预览、子弹拖尾预览、技能连招时间轴 | 程序/美术/策划 | 所见即所得，减少来回 | 开发成本，属于工具债 | 插件**只能写 Excel 能表达的内容**；插件产出必须能回落到 `data/excel/*.xlsx`（或写入 P2 的中间产物并强制回写） |

### 8.1 字段级落位规定（哪类字段走哪条路径）

| 字段类别 | 举例 | 必须走 | 允许 P2 临时调 | 允许 P3 编辑 |
|---|---|---|---|---|
| 战斗平衡数值 | 伤害、血量、速度、射速、冷却、暴击、掉落率、价格 | **P1** | ❌ | ✅（但结果写回 Excel） |
| 成长曲线 | 等级成长、难度曲线 | **P1** | ❌ | ✅（Curve 控件） |
| 表现参数（可量化） | 拖尾宽度/时长、粒子数、屏震、卡帧、缩放 | **P1** | ✅ | ✅ |
| 颜色 | Palette / TrailColor / 染色 | **P1** | ✅ | ✅（取色器） |
| 资源引用 | 特效场景、音效、预制体路径 | **P1** | ✅ | ✅（拖资源） |
| 复杂曲线/渐变 | `Curve`、`Gradient`、`Curve2D` | **P1**（存字符串或子资源路径） | ✅ | ✅（首选） |
| 技能/词条组合 | `Skills`、`Tags`、`DropTable` | **P1** | ❌ | ✅ |

> **判定口诀**：**影响"能不能赢"的 → 只能 Excel；影响"看起来爽不爽"的 → Excel 为准、检查器可临时调**。两者最终都以 Excel 为真源。

### 8.2 具体落位（颜色 / 特效 / 子弹样式 / 人物）

| 内容 | 数据表 | 生成资源类 | 运行时消费方 | 可视化方式 |
|---|---|---|---|---|
| **颜色** | `Palettes` 表（一组具名颜色） | `PaletteDef`（`[GlobalClass] Resource`，含 `Dictionary<string, Color>` 或具名 `Color` 数组） | 材质参数、UI 主题、特效染色 | P3 取色器 + P1 单元格 |
| **特效** | `Effects` 表 | `EffectDef`（场景引用 + 时长 + 缩放 + 染色 + 挂点 + 打击感参数） | `Game.Skill` / `Game.Combat` 的效果发射器 | P3 实时预览（在 3D 视口里播一次） |
| **子弹样式** | `Projectiles` 表 | `ProjectileDef`（碰撞/造型/拖尾颜色宽度时长/命中行为/追踪） | 子弹池 + 渲染 | P3 拖尾预览 |
| **人物/怪物** | `Characters` 表（`Kind=Player/Enemy/Boss`） | `CharacterDef` | `Game.Actor` 装配 | P3 角色卡预览（模型+调色板+缩放） |
| **武器** | `Weapons` 表 | `WeaponDef` | 玩家开火 | P3 手感面板 |
| **技能** | `Skills` 表 + `Formula` 表 | `SkillDef` | `Game.Skill` | P1 + 时间轴插件（阶段3） |

**「怪物 = CharacterDef 的变体」的落地细节**\n\n> 口径说明：本文所有路径按 `AGENTS.md` §5 目录契约写定（C# 在 `scripts/{core,data,combat,actor,skill,spawn,ui}/`，Excel 在 `data/excel/`）。若与 `ARCHITECTURE.md` 冲突，以 AGENTS.md 为准并回写 ARCHITECTURE。

```csharp
namespace Game.Data;   // 落地路径 scripts/data/（AGENTS.md §5 目录契约）

// CharacterDef 是"场景里能站的东西"的唯一静态定义。
// 人物(Player)与怪物(Enemy/Boss)都是它的实例，靠 Kind 列区分 —— 没有独立的 MonsterDef。
public partial class CharacterDef : DefBase
{
    [Export] public ActorKind Kind { get; set; } = ActorKind.Enemy;  // arch-ok: 枚举零值即默认
    [Export] public TeamId Team { get; set; }
    [Export] public StatPackage Base { get; set; }       // 基础数值包，见 05-numeric-package.md
    [Export] public PackedScene PrefabPath { get; set; }
    [Export] public PaletteDef VisualPalette { get; set; }
    [Export] public float Scale { get; set; } = 1f;      // arch-ok: 单位缩放，1 为原尺寸
    [Export] public Godot.Collections.Array<SkillDef> Skills { get; set; }
    [Export] public ProjectileDef ProjectileStyle { get; set; }
    [Export] public MovementDriverKind MovementDriver { get; set; } = MovementDriverKind.Chase; // arch-ok: 枚举零值为空驱动，缺省给追击
    [Export] public LootTableDef DropTable { get; set; }
    [Export] public Godot.Collections.Array<string> Tags { get; set; }
}
```

> **刻意不建 `ActorDef` 中间抽象类**：`ARCHITECTURE.md` §4.8 已把首批发包的 `Def` 定为扁平四件套（`CharacterDef`/`WeaponDef`/`ProjectileDef`/`EffectDef`），且它们共同继承 `DefBase` 就够用。再加一层 `ActorDef` 只是"为对称而抽象"（反模式 A7 伪抽象）。
> **为什么绝对不能有 `MonsterDef`**：夺舍要求"把怪物的包赋给玩家的槽位"。一旦 `Player` 与 `Monster` 是两个不相容的静态类型，赋值点就必须写 `as`/`is` 转换与类型判断（触犯 A3），类型的差异会从数据层一路上浮到战斗层。**人物与怪物在类型系统里必须是同一个类型**，只在 `Kind` 字段上不同。
> 若未来确实出现只有 Actor 才有的共性字段（如"移动动画集"），再引入 `ActorDef`，并在 `ARCHITECTURE.md` §3 登记；在此之前不预支复杂度。

> 只有一个派生类 `CharacterDef`，且不打算再加第二个（人物与怪物共用）——**这是刻意的**：避免出现 `PlayerDef`/`MonsterDef` 两条派生链，否则夺舍的"跨类型赋值"立刻会撞上类型系统（见 `06-extensibility.md` §4 的「为什么不能有 MonsterDef」）。

---

## 9. 数据契约校验（导入期，失败即中断构建）

校验器是 `tools/data_pipeline/` 的一个 `IBuildStep` 实现，**任何一条失败都中断构建**，退出码非 0，并且必须打印**中文错误 + 精确到 Excel 单元格**。

### 9.1 校验层次

| 层 | 校验内容 | 失败输出示例 |
|---|---|---|
| V1 表结构 | 四行表头存在、第 1 行字段名与 C# `Def` 属性一一对应（缺字段/多字段均报错）、第 2 行类型在词表内、无合并单元格 | `[错误] combat.xlsx/Projectiles 第1行F列: 字段 "TrailColour" 在 ProjectileDef 中不存在（是否想写 TrailColor?）` |
| V2 必填 | `Id` 必填；`Def` 上标 `[Required]` 的字段必填 | `[错误] characters.xlsx/Characters 单元格 E7（slime_basic.Base）: 必填字段为空` |
| V3 范围 | 数值范围（来自字段规约表的 `min/max`）、`float` 非 NaN/Inf | `[错误] combat.xlsx/Weapons 单元格 D5（pistol_basic.FireInterval）: 值 0 不满足 >0` |
| V4 枚举 | 枚举成员存在 | `[错误] combat.xlsx/Projectiles 单元格 J9（proj_player_bullet.ShapeId）: "Capsul" 不是 ProjectileShape 的成员` |
| V5 外键 | `ref:` / `refs:` 目标表中的 Id 存在 | `[错误] characters.xlsx/Characters 单元格 J6（slime_basic.Skills）: 引用的技能 "hop_atack" 在 Skills 表中不存在（是否想写 hop_attack?）` |
| V6 格式 | 颜色 `#RRGGBB`、资源路径以 `res://` 开头且文件存在、Id 正则 | `[错误] presentation.xlsx/Effects 单元格 I6（fx_hit_spark.Color）: 颜色格式非法 "FF00FF" （应形如 #FF00FF）` |
| V7 唯一/唯一性校验 | 表内 Id 重复、枚举值重复 | `[错误] characters.xlsx/Characters: Id "slime_basic" 在第 6 行与第 9 行重复` |
| V8 版本 | `schema_version` 自增规则与字段删除检查（见 `06-extensibility.md` §6） | `[错误] 表 Projectiles 的字段 "TrailColor" 在 v1 存在、v2 被移除但未标记 deprecated` |
| V9 引用闭环 | 落盘后重新加载全部 `.tres` 并检查 `ExtResource` 全部可解析 | `[错误] 生成物 proj_rail_beam.tres 引用的 res://art/fx/hit_burn.tscn 不存在` |

### 9.2 错误信息格式（强制）

```text
[错误] <源文件>/<Sheet> 单元格 <列行>（<Id>.<字段>）: <中文说明>
```

- **必须**包含：源文件、Sheet 名、Excel 风格单元格坐标（列字母+行号，行号是 Excel 真实行号，含 4 行表头偏移）、业务 Id.字段名、中文可读说明。
- **禁止**只打印「解析失败」或空指针堆栈。
- 生成器必须把所有错误**收集完再退出**（不要第一个错误就抛），最后打印 `共 N 个错误，构建中止`。

### 9.3 字段规约表的落地形式

字段规约不写在 Python 里硬编码，而是从 C# `Def` 类的特性 **反射导出** 一份 `data/generated/schema/<table>.schema.json` 作为生成器输入（避免 C# 与 Python 双份真源）：

```csharp
public sealed class DefBase : Resource
{
    [Export, Required, IdFormat]
    public string Id { get; set; } = "";

    [Export]
    public string DisplayName { get; set; } = "";

    [Export]
    public int SchemaVersion { get; set; }
}

// 用特性声明业务规约，构建期反射导出为 schema.json
public partial class ProjectileDef : DefBase
{
    [Export, Min(0.01f), Note("米")] public float CollisionRadius { get; set; }
    [Export, Min(0f), Note("米/秒")] public float ProjectileSpeed { get; set; }
    [Export, RequiredRef("Effects")] public EffectDef OnHitFx { get; set; }
}
```

> `[Required]` / `[Min]` / `[Max]` / `[Note]` / `[RequiredRef]` 是**本项目自定义特性**，落地任务时放在 `Game.Data.Validation`。它们的唯一用途是被反射导出给构建期，运行期不参与。

---

## 10. 与架构守卫 GS005（硬编码检测）的联动

| 项目 | 规定 |
|---|---|
| 守卫编号 | **GS005 = 硬编码数值检测**（GS001–GS004 由 `ARCHITECTURE.md` 分配） |
| 扫描范围 | `scripts/**/*.cs`、`scripts/ui/**/*.gd`；排除 `tools/`、`data/generated/`、`*.schema.json` |
| 判定 | 检测到 `int/float/double` 字面量且该字面量不属于 §3.2 白名单，且**所在行或上一行没有 `// arch-ok:`**，报违规 |
| 严重度 | **Error**（阻断 CI 与 PR 合并） |
| 豁免注释 | `// arch-ok: <理由>`，理由 < 8 字视为无效豁免 |
| 逐行豁免上限 | 单文件 > 5 处 `arch-ok` 视为"把平衡表写在代码里"，升级为人工 Review 项 |
| 输出格式 | `GS005 <文件>:<行> 硬编码数值 <值>，请移入 data/excel/*.xlsx（表: <猜测表名>）` |

**与 03/06 的联动**：GS005 判 Error，GS001–GS004 的编号由 `ARCHITECTURE.md` 守卫表统一登记；本文档只负责 GS005 的语义定义，实现与 CI 挂接属落地任务。

---

## 11. 热重载与调参闭环（三级策略）

| 级别 | 触发方式 | 覆盖内容 | 实现要点 | 延迟 |
|---|---|---|---|---|
| **L1 编辑器内手动重载** | 编辑器工具栏按钮「Reload Data」/ 快捷键 | 重新读 `data/generated/**`，重建所有 `IConfigSource` 索引 | `[Tool]` 插件按钮 → 清空缓存 → 重新加载 → 通知视图刷新 | < 1s |
| **L2 运行期文件监听** | `FileSystemWatcher` 或 Godot `FileSystem` 轮询变更 | 仅**表现类**与**已存在实例的数值**热更新 | 变更 → 重载受影响表 → Diff 后**重新 Fold** 受影响的 `StatProvider`；不重置关卡 | < 2s |
| **L3 关闭重进** | 手动 | 一切（含结构性 schema 变更、新增表、技能种类变更） | — | 数秒 |

### 11.1 各字段级别的热重载支持

| 变更内容 | L1 | L2 | L3 |
|---|---|---|---|
| 数值（伤害/血量…） | ✅ | ✅（重 Fold） | ✅ |
| 颜色/特效参数 | ✅ | ✅（重建材质参数） | ✅ |
| 新增一行数据（新怪物） | ✅ | ✅ | ✅ |
| 删除/重命名 Id | ✅ | ⚠️ 需重建实例映射 | ✅ |
| **schema 版本变化** | ✅（需重进关卡） | ❌ 明确不支持 | ✅ |
| 新增技能种类（新 C# 类） | ❌ | ❌ | ✅（需重编译） |

### 11.2 闭环纪律

1. **调参闭环必须是"改 Excel → 生成 → 重进"，不得"改代码 → 编译"**。
2. L2 热重载是**表现优先**；战斗数值热更要在 `05-numeric-package.md` 的 Fold 管线里做，且必须写日志：`[Data] 热重载影响 N 个 StatProvider，重算耗时 X ms`。
3. 热重载引入的任何状态**不得写进存档**（L3 只存差量，见 §4.1）。

---

## 12. 反例（出现即打回）

| # | 反例 | 问题 | 正确 |
|---|---|---|---|
| D1 | `const float Damage = 12f;` 集中在一个 `Balance.cs` | 仍是代码，策划改不了、不可热重载 | 进 `Weapons` 表 |
| D2 | 代码里 `if (isElite) hp *= 3f;` | 数值硬编码 + 类型分支（同时违反 03 A3） | `Characters` 加一行 `slime_elite`，或用 `Formula` 表的精英加成 |
| D3 | 运行期 `def.ProjectileSpeed = 40f;` 直接改 L1 资源 | 污染所有实例 + 热重载失效 | 写进 L2 运行时包（05） |
| D4 | 手改 `data/generated/*.tres` 后提交，不回写 Excel | 下次生成被覆盖，改动丢失 | P2 调好必须回写 Excel |
| D5 | Excel 第 1 行写中文/带空格 `Move Speed` | 生成器无法绑定，或静默取默认值 | 严格 CamelCase 且与 C# 属性同名 |
| D6 | 用合并单元格做"分组" | 解析器读到 `None` | 拆列 / 取消合并 |
| D7 | Excel 里写公式 `=C5*1.2` | 读到缓存值，与实际不一致，且无法审阅 | 用 `Formula` 表（见 05 §6） |
| D8 | 校验失败只打印「Error loading data」 | 人找不到哪里错 | §9.2 格式，精确到单元格 |
| D9 | 存档里存全部静态定义 | 存档臃肿，改平衡后旧档数据错乱 | 只存引用 + 差量 |
| D10 | 在 `.tres` 里 `@export var damage: float = 12`（代码默认值） | 默认值即隐藏的数值来源，且容易与 Excel 偏离 | 默认值只允许"结构性零值"，业务值必须必填（`[Required]`） |
| D11 | 把"平衡"写进 GDScript UI 层（因为改起来方便） | UI 层被限权（见技术决策：除 UI 层外禁止 GDScript），且状态污染 | 平衡只走 Excel + C# |
| D12 | 新增游戏内容时新写一个 `.cs` 常量表 | 违反 06 的"新增怪物 ≤ 2 文件"预算 | 只在 Excel 加行 |

---

## 13. 自检清单（PR 自检，逐项勾选）

- [ ] **Q1** 新增/修改的每个数值、颜色、特效、子弹样式、人物属性，都能在我的 diff 里找到对应的 **Excel 行**（不是代码）。
- [ ] **Q2** 我的代码里没有未标注 `// arch-ok:` 的数值字面量（跑一遍 GS005 或手工 grep）。
- [ ] **Q3** 我改 `.tres` 只是调试，且已**回写 Excel** 并重跑生成器？
- [ ] **Q4** 我没有提交 `data/generated/**` 的手工修改（生成物可重建，diff 里应是脚本产物）？
- [ ] **Q5** 新增字段已同时更新：Excel 表头四行、C# `Def` 属性 + 规约特性、`data/generated/schema/*.schema.json`（由生成步骤产出）三处一致？
- [ ] **Q6** 新增/重命名 Id 后，所有 `ref:` 引用都能解析（构建期 V5 校验通过）？
- [ ] **Q7** 颜色用 `#RRGGBB`，百分比用小数，时间用秒，角度用弧度？
- [ ] **Q8** 运行期没有写 L1 资源（没有对 `def.*` 赋值的语句）？
- [ ] **Q9** 新增字段的"路径归属"符合 §8.1（平衡类 → Excel；表现类 → Excel 为准、检查器可调）？
- [ ] **Q10** 我改完后跑过 `tools/data_pipeline/` 全量构建，`data/generated/index/*.json` 的 `schema_version` 与代码常量一致？

---

## 14. 与其它规则的交叉

| 规则 | 交叉点 | 本文档负责 | 对方负责 |
|---|---|---|---|
| 规则 5 · 抽象优先 | `03-abstract-first.md` | 提供给抽象卡"哪些字段进 Excel"的判据；GS005 是抽象的落地检查 | 接口/命名空间边界 |
| 规则 6 · 数值包 | `05-numeric-package.md` | `stats` 类型如何从 Excel 生成 `StatPackage`；`Formula` 表 | 包的分层、Fold、赋值语义 |
| 规则 7 · 可扩展性 | `06-extensibility.md` | "加数据行"这条扩展路径的具体操作 | 新增文件数预算、夺舍走查 |
| 规则 3 · 架构大纲 | `docs/architecture/ARCHITECTURE.md` | 表清单、生成物路径、GS005 在守卫表中登记 | 架构总览与守卫编号分配 |

---

## 15. 需要 Lead 仲裁的开放问题（本文档的已知取舍）

| # | 问题 | 我的建议 | 影响面 |
|---|---|---|---|
| OQ-1 | `Weapons.Damage` 与 `Projectiles.ProjectileSpeed`/伤害重复 | 伤害以 `Weapons` 为准，`Projectiles` 只管表现 | 生成器外键关系 |
| OQ-2 | 曲线/渐变在 Excel 里存字符串 vs 存独立 `.tres` 路径 | v1 存字符串（便于 diff），P3 插件负责可视化 | `curve` 类型解析器 |
| OQ-3 | 是否允许 `data/generated/` 不提交（`.gitignore` 已排除） | 允许，但 CI 必须做到"从 Excel 可重建"，且启动时会话需要先跑生成 | 构建流程 |
| OQ-4 | 是否引入 Unity 式的 `ScriptableObject` 等价物做"运行时可变配置" | 不需要；运行时变更一律进 `StatPackage` | 05 |
| OQ-5 | P3 编辑器插件与 Excel 的写回是"插件直接改 xlsx"还是"插件导出中间 csv 再灌回" | v1 采用"插件只导出 patch json，由生成器应用到 Excel" | 插件设计 |
