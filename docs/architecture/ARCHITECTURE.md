# BiuBiuOrigin 架构大纲

> 状态: v17  最后更新: 2026-11-10（§15.8 相机跟随手感：临界阻尼弹簧 + 鼠标方向可调偏移；§14.10 训练房 3D 测试场景；§17.11 「抽象没贯彻到底」全面体检 + 契约硬规则；§17.10.1 全部先跑白模：`ModelPath` 一律留空、占位路径清零；§17.10 模型属性收敛为唯一入口 `VisualPack`；§7.5.2 2D 自绘图随窗口缩放；§24 素材查看器 UI 改 DockSpace 停靠布局；§23 本地化中文名真源 `l10n.xlsx`；§14.8 调参面板 SpinBox→可左右拖动滑块；§14.9 物件随机生成 + 2D 图例带色；§4.3.1 数值包跨层约束 + §7.5.1 窗口视口配置）  维护规则: 每个任务完成后**就地覆盖更新**本文件（SOP 见 `docs/rules/07-definition-of-done.md`），禁止另建 `ARCHITECTURE_v2.md`
> 读取顺序: 本文件 → 相关章节 → `docs/rules/01~07` → 如有代码再读对应源文件

> **给第一个读到这里的人（人或 AI agent）：** 这是本工程的**唯一**架构真源。工程已从「空壳」进入 **ECS 骨架落地**阶段：`scripts/core/`（基础件 + ECS 内核）、`scripts/host/`（引擎边界）、`scripts/actor|spawn/`（首批系统族）、`tests/`（63 用例）、`native/`（GDExtension 骨架）均已建立；`data/`（Excel 数据层）仍待落地。各节仍按**四段式**写：`现状 / 目标 / 约束 / 下一步`。看到「待落地」不代表没想清楚，而是**已定好形状、只等第一个任务去填**。
>
> **ECS 的形状不在本文件里。** 实体/组件/系统/查询/命令缓冲/一帧阶段的**全部约定**见 [ECS.md](ECS.md)（七条判定 J1~J7、组件规范、内核 API、帧顺序、引擎边界、性能路线）。本文件的职责是**登记边界与汇总**：§2.2 分层、§2.3 引擎边界、§3.2 接口清单、§6.2 战斗顺序怎么落在 ECS 上、§13 ECS 落地汇总。**两者冲突时以 ECS.md 为准并回来改本文件。**
>
> **三条硬原则：** ① **数据驱动**——数值来自 Excel → 生成物，代码里不许出现数值字面量（规则4）；② **抽象先行**——先有接口和扩展点，再写实现，禁止复制粘贴式实现（规则5、规则7）；③ **文档即契约**——任务改动了架构形状就**就地覆盖**对应章节（规则3）。
>
> **怎么读这份文件**（按任务类型的完整路由见 `docs/rules/07-definition-of-done.md` §8.2）：
> - 读全局：§0 → §11 变更日志 → §12 待办
> - 写数值/数据：§4 §5；写战斗/技能：§2 §3 §6；写渲染/特效：§7 §9
> - 写 UI：§8；写工具链：§4 §1；做性能优化：§9 §2

> **维护者必读（GS006 判定规则，已与 `tools/check_architecture.ps1` 对齐）：**
> ① **时间戳**：守卫取**正文前 40 行内、自上而下遇到的第一个** `YYYY-MM-DD` 作为「最后更新」，并允许其它日期存在。所以改**文件头第一行那个 `最后更新:`** 即可，它靠在最前面就一定是首选；但**不要把任何日期写进它前面的行**（第 1~3 行），否则会被误取。
> ② **变更日志标题**：接受 `## 变更日志`、`## 11. 变更日志`、`## 11、变更日志` 等写法（守卫用正则匹配，编号前缀可有可无，分隔符支持 `.` `,` `)` `、`）。本文件统一用带编号的 `## 11. 变更日志`，与 §0~§10、§12 保持一致。
> ③ **改法**：用 Edit 就地改对应章节，**不要**整篇重写（会覆盖掉同时进行的改动）。
> ④ **唯一真源**：以上两条以 `tools/check_architecture.ps1` 的实际实现为准，本文与 `docs/rules/07-definition-of-done.md` §5.4/§5.5 只做说明；两者不一致时改文档去对齐脚本。
>
> 本文件任何章节的「下一步」都不是承诺，而是当前最优排期；按实际情况改并留日志。

## 0. 一页速览（TL;DR）

| 项目 | 内容 |
|---|---|
| 工程名 | BiuBiuOrigin（`config/name="BiuBiuOrigin"`，C# 程序集 `BiuBiuOrigin`） |
| 引擎 | Godot **4.7.2-stable mono**，.NET SDK **10**，`net10.0` |
| 渲染 | `Forward Plus`（Forward+），Windows 驱动 `d3d12` |
| 物理 | `Jolt Physics`（`physics/3d/physics_engine`） |
| 玩法类型 | 3D 俯视角射击肉鸽（Roguelike / 割草），手感和打击感优先 |
| 美术风格 | Stylized Low-Poly Toybox（参考 *Go-Go Town!*） |
| 运行时架构 | **数据导向 ECS**（自研 `Game.Core.Ecs`）：archetype 列式存储 + `unmanaged` 组件 + `SystemBase` 系统族 + 帧末命令提交。**形状真源是 [ECS.md](ECS.md)**，本文只做汇总与边界登记 |
| 主语言 | **C#**（全部游戏逻辑），命名空间 7 层：`Game.Core/Data/Combat/Actor/Skill/UI/Native`（ECS 内核在 `Game.Core.Ecs`，引擎边界在 `Game.Host`） |
| 性能语言 | **C++ GDExtension**，位于 `native/`，仅用于热路径 / 渲染 / 引擎能力扩展 |
| 脚本例外 | **GDScript 仅在 UI 层**（`scripts/ui/**`）允许，其余全项目禁止（规则1，守卫 GS001） |
| 数值真源 | **Excel `.xlsx`** → `tools/data_pipeline/` → `data/generated/`（`.tres` + index JSON）；运行期只读生成物 |
| 架构守卫 | `tools/check_architecture.ps1`（检查项 GS001~GS007）+ `tools/check_all.ps1`（四步一键：build ×2 → 守卫 → 测试 → 200-seed 地牢性质校验） |
| 目录骨架 | `scripts/ scenes/ data/ shaders/ native/ addons/ tools/ tests/ docs/` |
| 完成定义 | 6 步检查表见 `docs/rules/07-definition-of-done.md`，最后一步必是**覆盖更新本文件** |
| 开发期工具 | `tools/asset_viewer/` **素材快查看器**：独立 Win32 exe（CMake+MSVC+D3D11/D2D，**零第三方依赖**），双击扫全量 371 个 `.glb`/`.fbx` 看缩略图与动画，**不进游戏**（见 §16） |
| 当前阶段 | **v7：ECS 骨架 + 房间/地牢 + 角色抽象 + 素材查看器 + 基础物件抽象 + 第一个可玩人物均已落地**：① 基础物件抽象（武器/子弹/怪物同源 `ObjectArchetype` + 熔炉组合 + `combat.xlsx` + 零分配铸造工厂）；② 第一个可玩人物 `test_hero`（标准 + 三个纯数据变体：重甲/敏捷/幸运）——WASD 移动、鼠标左键瞄准射击、真子弹生成与命中、HUD 全量数值显示；人物 = 「数值包 + 技能包 + 模块集合」的 ECS 拼接，换变体只改 `characters.xlsx` 一行（0 行 C# 改动）；③ 素材查看器全量 371/371 验收通过。素材层当前为 Box 白模（用户已确认先白模后换资源）；五槽技能中仅左键为真实现，其余四槽仍是占位 |

**一句话**：C# 写逻辑、Excel 出数值、C++ 兜性能、GDScript 只碰 UI、每个任务收工就回来改这份文件。

---

## 1. 工程结构（目录树 + 每个目录的职责 + 谁可以写）

### 1.1 现状（2026-11-03，ECS 骨架落地后）

**已落地**（可直接 `dotnet build` / `dotnet test` / 跑冒烟场景）：

```text
biu-biu-origin/
├── project.godot                          # 引擎配置（mono/d3d12/Jolt）
├── BiuBiuOrigin.csproj                    # ★手工建立（引擎 --build-solutions 在本机不落盘）
├── nuget.config                           # ★离线源（只指向 E:\Godot\GodotSharp\Tools\nupkgs）
├── config/architecture-rules.json         # 守卫配置
├── scripts/
│   ├── core/                              # 界面无关基础件（零 Godot 依赖）
│   │   ├── Events/ Handles/ Pool/ Random/ # 事件总线 / ActorHandle / 对象池 / 确定性随机
│   │   └── ecs/                           # ★ECS 内核（Game.Core.Ecs）
│   │       ├── World.cs Archetype.cs Entity.cs Query.cs
│   │       ├── CommandBuffer.cs SystemBase.cs SystemPipeline.cs
│   │       ├── TypeSet.cs EntityIndex.cs ComponentTypeRegistry.cs
│   │       ├── attributes/                # [Identity] [UpdateInGroup] [UpdateAfter] [UpdateBefore]
│   │       └── components/OfficialComponents.cs  # 11 个官方组件
│   ├── host/                              # ★唯一允许 using Godot 的游戏层（Game.Host）
│   │   ├── WorldHostOptions.cs            # [GlobalClass] Resource：全部可调数值
│   │   ├── FixedFramePipeline.cs          # 一帧七阶段（ECS.md §6 的唯一落地处）
│   │   ├── HostEventQueue.cs              # 帧内缓冲 → 帧末统一派发
│   │   ├── CommandFlushSystem.cs          # 本帧唯一结构化变更点
│   │   ├── ViewRegistry.cs                # 视图池（实体 → Node3D）
│   │   └── bridge/{TransformBridge,VisualBridge}.cs
│   ├── actor/                             # 系统族：Movement / Lifetime
│   └── spawn/                             # 系统族：SpawnDirector + SpawnSettings + ViewModel
├── tests/                                 # ★63 用例（不依赖 Godot 的进程里跑）
│   ├── Kernel/ Layering/ Determinism/ Allocation/
│   └── BiuBiuOrigin.Tests.csproj
├── native/                                # C++ GDExtension（godot-cpp master + 锁 hash）
│   ├── gdextension/ include/ src/ build/
│   └── README.md                          # 构建复现步骤
├── addons/biu_biu_native/                 # GDExtension 产物（DLL + .gdextension）
└── tools/{check_architecture.ps1, check_all.ps1, env.ps1}
```

**仍待落地**：`data/excel/`（Excel 唯一真源）、`data/generated/`、`tools/data_pipeline/`、`scripts/combat/`、`scripts/skill/`、`scripts/ui/`、`shaders/`。

> ⚠ **`BiuBiuOrigin.csproj` 必须手维护**：本机 Godot `--headless --build-solutions` 不写 `.csproj`/`.sln`。已建立的关键配置是 `DefaultItemExcludes` 排除 `.godot/**` 与 `tests/**`——否则 MSBuild 产物 `*.AssemblyInfo.cs` 会被 glob 二次编译，报 CS0579「特性重复」。改动此文件后务必跑一次 `dotnet build` 验证。

### 1.2 目标目录树（v1 约定，任务可增补，但必须回写本节）

```text
biu-biu-origin/
├── project.godot                    # 只由 Lead / 明确的引擎配置任务修改
├── BiuBiuOrigin.csproj              # Godot 自动生成的 .csproj（由 engine 生成；不手改、不手删）
├── AGENTS.md                        # 给 agent 的启动契约（Lead 维护）
├── config/
│   └── architecture-rules.json      # ★ 守卫配置：allowGDScript / ignore / 数值白名单 / 分层（唯一放行渠道）
├── docs/
│   ├── architecture/
│   │   ├── ARCHITECTURE.md          # 本文（架构唯一真源，就地覆盖）
│   │   └── .guard-baseline.json     # 守卫基线，由 check_architecture.ps1 -UpdateBaseline 生成
│   ├── rules/                       # 01~07 规则正文
│   ├── guides/                      # 操作型指南（怎么加一张表、怎么建 native 模块…）
│   └── prompts/                     # 任务提示词模板
├── scripts/                         # ★ C# 唯一主根（namespace Game.*）
│   ├── core/                        # Game.Core    基础件：接口、事件总线、对象池、随机、数学
│   ├── data/                        # Game.Data    配置源、Resource 定义、存档、schema
│   ├── combat/                      # Game.Combat  伤害结算、命中、状态效果
│   ├── actor/                       # Game.Actor   Actor 表现体、移动、生命周期
│   ├── skill/                       # Game.Skill   技能装配与释放
│   ├── spawn/                       # Game.Spawn  刷怪、波次、房间生成（肉鸽骨架）
│   └── ui/                          # Game.UI      ★ 唯一允许 .gd 的目录
├── scenes/                          # 场景（.tscn / .tres 场景资源）；挂 C# 脚本
├── data/
│   ├── excel/                       # ★ 数值唯一真源（.xlsx），人工/策划编辑
│   └── generated/                   # ★ 构建期生成物（.tres + index JSON），禁止手改
├── shaders/                         # gdshader（命名 <feature>_<pass>.gdshader）
├── native/                          # ★ C++ GDExtension（唯一允许 C++ 的根）
│   ├── gdextension/
│   │   ├── gdextension_interface.h
│   │   └── libgodot-cpp/            # git submodule，godot-cpp 分支 4.7
│   ├── src/                         # .cpp 实现
│   ├── include/                     # .h/.hpp 公开头
│   └── build/                       # CMake 构建产物（gitignore）
├── addons/                          # 编辑器插件与第三方（biu_biu_editor/, biu_biu_native/）
├── tools/                           # ★ 构建期工具（不进游戏运行时）
│   ├── data_pipeline/               # Python：Excel → data/generated/
│   ├── check_architecture.ps1       # 架构守卫（GS001~GS007）
│   ├── env.ps1                      # 环境探测
│   └── probes/                      # 一次性探针脚本（允许 GDScript 的例外区）
└── tests/                           # 测试工程与测试数据
```

### 1.3 每个目录的职责 / 所有者 / 准入规则

| 目录 | 职责 | 语言 | 谁可以写 | 禁写 |
|---|---|---|---|---|
| 2026-11-06 | v9 | **标签分层：逻辑功能层 / 标识层** | **新增 §20 标签分层**：标签拆成两层 —— `Logic`（决定玩法行为，代码消费，加之前先想规则）与 `Identity`（不做判定，只做归属/吸引/主题匹配，随手加）。① **表**：`Tags` 表新增 `Tier` 列；标识层 6 轴（Loot / Attract / Faction / Theme / Aura / Craft）共 22 个标签，tag 40 → 62。② **道具互相吸引**：`Attract` 轴是**可叠加**轴，被吸物与吸引源各挂自己的类型，交集非空即被吸引 —— **代码不认识具体名字，C# 0 行改动**就能加一种新磁铁。③ **硬约束（机器保证）**：标识标签 `BitMask` 必须 -1（否则进配方字 = 偷偷影响玩法）、`Group` 必须 `None`（否则产出能力位），由校验器 `check_tag_tiers` + `ContentBinder` 双向把关；`TagGroup` 词表新增 `None` 成员。④ **运行期 API**：`TierOf / TagsOfTier / TierMask` + `TagFamilies.TierOf(axis)`。⑤ **踩坑**：`TagId` 只允许单段 `tag.<名>`，`tag.loot.ammo` 非法 → 统一扁平写法 —— `tag.loot_ammo`；`ObjectBitMatrix.Mask(row, tag)` 第二参是 **tag 下标**不是 word；62 个 tag 已跨两个 word，印证「轴索引不受 BitMask 32 位上限约束」。**测试 354 → 358 全绿**（TagFamilyTests 11 → 15 条），校验 3258 → 3540 项，守卫 error 0。| `data/excel/combat.xlsx`、`scripts/core/Content/TagFamily.cs`、`scripts/core/Content/ObjectBitMatrix.cs`、`scripts/data/Content/ContentBinder.cs`、`tools/data_pipeline/migrate_tag_tiers.py`、`tools/data_pipeline/gen_combat_xlsx.py`、`tools/data_pipeline/validate_content_xlsx.py`、`tools/data_pipeline/README.md`、`tests/Content/Data/TagFamilyTests.cs`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-06 | v8 | **标签轴（TagFamily）：以轴为范围索引内容** | **新增 §19 标签轴**：给所有内容挂上「轴」这一层索引坐标系 —— 物体是点、标签是坐标值、轴是坐标系。① **表**：`Tags` 表新增 `Family` 列（10 轴：Kind / WeaponKind / ProjectileKind / Element / Ability / Trigger / Rarity / Race / Role / Effect），tag 17 → 40。② **互斥性由轴决定**：`Ability` **刻意不互斥**（穿透+弹跳+分裂叠在一颗弹上，正是 ECS 组合的主战场）；`Race` 是**分层轴**（祖先 `tag.humanoid` ⊃ 具体 `tag.goblin`），于是「对人形增伤」能打到哥布林。③ **运行期**：`ContentRules` 新增 `FamilyOf / TagsOfFamily / FamilyMask / PackMask`，「命中某轴任一标签」= **一次按位与**，不认识任何具体 tag 名。④ **约束**：`Family` 只做索引、**不改变能力位**（能力位仍只看 `Group`），故标签重构不碰工厂判定；绑定器对认不出的轴名**当场报错**而非静默落 None。⑤ **修两个静默失效**：校验器按**列号**取 `BitMask`（插列后全部读成 -1 → 30 条错），改为按**字段名**取；生成器 `TAGS` 的 `BitMask` 曾全 -1，重跑覆盖 `combat.xlsx` 清零了 bit 0~15，已写回并给出回填脚本。**测试 219 → 354 全绿**（新增 11 条 `TagFamilyTests`），守卫 error 0。| `data/excel/combat.xlsx`、`scripts/core/Content/TagFamily.cs`、`scripts/core/Content/ObjectBitMatrix.cs`、`scripts/data/Content/ContentBinder.cs`、`tools/data_pipeline/gen_combat_xlsx.py`、`tools/data_pipeline/validate_content_xlsx.py`、`tools/data_pipeline/fix_tag_bitmasks.py`、`tools/data_pipeline/migrate_tags_multiaxis.py`、`tests/Content/Data/TagFamilyTests.cs`、`docs/architecture/ARCHITECTURE.md` |
| `scripts/core/` | 零依赖基础件：接口、事件、池、随机 | C# | 任意任务；**接口改动必须回写第 3 节** | Godot API 重度使用（core 应可脱离 Node 测试） |
| `scripts/data/` | 配置读取、生成物加载、存档、schema 校验 | C# | 数据类任务 | 数值字面量（全部来自生成物） |
| `scripts/combat/` | 命中、伤害修正、结算、状态 | C# | 战斗类任务 | 直接操作 UI/输入 |
| `scripts/actor/` | Actor 表现体、移动驱动、可视化描述 | C# | 角色类任务 | 直接读 Excel（必须走 `IConfigSource`） |
| `scripts/skill/` | 技能装配、目标选取、释放时序 | C# | 技能类任务 | 复制粘贴战斗逻辑（必须调用 Combat） |
| `scripts/spawn/` | 刷怪、波次、房间/关卡生成 | C# | 肉鸽流程类任务 | 硬编码出怪表 |
| `scripts/ui/` | HUD、菜单、提示 | **C# 优先，允许 .gd** | UI 类任务 | 引用 `Game.Combat/Actor/Skill/Spawn`、`Native.`（GS004） |
| `scenes/` | 场景与预制体 | `.tscn` | 任意任务 | 挂 `.gd` 脚本（规则1） |
| `data/excel/` | 数值真源 | `.xlsx` | 数值/策划任务 | 手改生成物替代真源 |
| `data/generated/` | 生成物 | `.tres` / `.json` | **只有 `tools/data_pipeline/`** | 人工编辑（会被覆盖） |
| `shaders/` | 着色器 | `.gdshader` | 渲染/特效任务 | 硬编码 uniform 值（应由 `.tres` 喂） |
| `native/` | GDExtension 实现 | C++ | native 类任务（GS003 强制） | 放 C#/GDScript |
| `addons/` | 编辑器插件/第三方 | 混合 | 编辑器工具任务 | 业务逻辑常驻 |
| `tools/` | 构建期脚本（不进游戏） | PS1/Python | 工具链任务 | 被游戏运行时引用 |
| `tests/` | 测试 | C# | 任意任务 | 依赖具体场景才能跑 |
| `config/` | 守卫配置与项目级常量 | JSON | 工具链/Lead 任务 | 业务逻辑 |
| `docs/` | 规则、架构、指南 | Markdown | **任意任务（架构章节就地覆盖）** | 另建 `ARCHITECTURE_v2.md` |

### 1.4 约束

- **`.cs` 必须全部在 `scripts/` 下**，程序集/命名空间以 `Game.` 开头（GS007）。其他目录出现 `Game.*` 编译单元会被记为分层告警。
- **C++ 只能在 `native/`**（GS003，error 级）。
- `data/generated/` 与 `data/excel/` 是**真源 + 派生物**的配对，生成物不得手工编辑、不得作为 PR 的实质内容。
- `.godot/`、`obj/`、`bin/`、`native/build/`、`native/gdextension/libgodot-cpp/` 一律 gitignore。

### 1.5 下一步

| # | 动作 | 归属 |
|---|---|---|
| 1 | 建空目录 + `.gitkeep`：`scripts/{core,data,combat,actor,skill,spawn,ui}`、`scenes/`、`data/{excel,generated}`、`shaders/`、`tests/` | 第一个落地任务 |
| 2 | 首次 `dotnet build` 生成 `BiuBiuOrigin.csproj`，确认以 `scripts/` 为编译根 | 第一个 C# 任务 |
| 3 | 目录一旦新增/改名，**立刻回写本节表格** | 改动者本人 |

---

## 2. 分层与依赖规则（允许的依赖方向箭头图 + 禁止的反向依赖）

### 2.1 现状

无任何代码，因此无违规。守卫 GS003/GS004/GS007 已设计好检查点，等代码出现后开始生效。

### 2.2 目标：六层单向依赖

```text
                ┌──────────────────────────────────────────────┐
     (表现/入口) │  scenes/  .tscn  ──────────┐                  │
                │  scripts/spawn  (Game.Spawn)│                 │
                │  scripts/skill  (Game.Skill)│                 │
                │  scripts/ui     (Game.UI)   │                 │
                └─────────────┬───────────────┘                 │
                              │ 只允许向下                        │
                              ▼                                   │
                ┌──────────────────────────────┐                 │
                │  scripts/actor (Game.Actor)  │                 │
                └──────────────┬───────────────┘                 │
                               ▼                                  │
                ┌──────────────────────────────┐                 │
                │  scripts/combat (Game.Combat)│                 │
                └──────────────┬───────────────┘                 │
                               ▼                                  │
                ┌──────────────────────────────┐                 │
                │  scripts/data  (Game.Data)   │                 │
                └──────────────┬───────────────┘                 │
                               ▼                                  │
                ┌──────────────────────────────┐                 │
                │  scripts/core  (Game.Core)   │  ← 零业务依赖    │
                └──────────────────────────────┘                 │
                               ▲                                  │
                               │ 仅通过 Game.Core 中登记的        │
                               │ 接口 + Game.Native 门面调用      │
                ┌──────────────┴───────────────┐                 │
                │  native/  (Game.Native)      │                 │
                │  C++ GDExtension             │                 │
                └──────────────────────────────┘                 │
                                                                │
                    图例：A ──▶ B 表示「A 可以 using B」。         │
                    反向箭头 = 违规。                            │
                └──────────────────────────────────────────────┘
```

简化成一条链（与任务描述一致）：

```text
Native ─▶ Combat ─▶ Actor ─▶ Data ─▶ Core        （向下单向）
UI / Skill / Spawn ──(只向下)──▶ Actor / Combat / Data / Core
```

> 说明：`Game.Native` 是 C++ 侧暴露的门面（如 `Native.CombatSolver`），**只有 `Game.Combat` 允许 `using Game.Native`**，其余层必须经 Combat 的接口转调。这保证「下沉 C++」时可以只改一处。

### 2.3 允许 / 禁止矩阵

| 行（使用方） \ 列（被使用方） | `Game.Core` | `Game.Data` | `Game.Combat` | `Game.Actor` | `Game.Skill` | `Game.Spawn` | `Game.UI` | `Game.Native` |
|---|---|---|---|---|---|---|---|---|
| `Game.Core` | — | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| `Game.Data` | ✅ | — | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| `Game.Combat` | ✅ | ✅ | — | ❌ | ❌ | ❌ | ❌ | ✅（唯一） |
| `Game.Actor` | ✅ | ✅ | ✅ | — | ❌ | ❌ | ❌ | ❌ |
| `Game.Skill` | ✅ | ✅ | ✅ | ✅ | — | ❌ | ❌ | ❌ |
| `Game.Spawn` | ✅ | ✅ | ✅ | ✅ | ✅ | — | ❌ | ❌ |
| `Game.UI` | ✅（只读接口） | ✅（只读 DTO） | ❌ | ❌ | ❌ | ❌ | — | ❌ |
| `Game.Native` | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | — |
| **`Game.Host`** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅（唯二） |
| **`Game.Core.Ecs`** | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

> **`Game.Core.Content`（2026-11-06 新增，`scripts/core/Content/`）是 `Game.Core` 的子命名空间**，
> 与 `Game.Core.Ecs` 同级：**谁都可以用它**（`Game.Data` / `Game.Combat` / `Game.Actor` / UI 只读视图），
> 它自己只依赖 `Game.Core`（`StatId`/`DamageType`/`TeamId`/`IRandomProvider`）与 `Game.Core.Actors`（`StatPackageIds`）。
> 因此 `Game.Data` 读 Excel 绑出 `ObjectArchetype`/`WeaponContract` 是**合法方向**（Data → Core），
> 而 `Game.Combat` 铸造时也只读这些契约、**不反向依赖 Game.Data**（契约全在 Core，工厂吃 `ContentIndex`）。
> 这也解释了为什么 `tests/` 能在无引擎进程里直接测「Excel → 契约 → 铸造」整条链（§17.6）。

**两条 ECS 专属硬约束（`Game.Host` 与 `Game.Core.Ecs`，2026-11-03 定稿）：**

1. **`Game.Host`（`scripts/host/**`）是唯一允许 `using Godot` 的 ECS 边界。** 它可以依赖所有游戏层，但没有别的层可以依赖它——业务系统一律不接受 `Node`/`Resource`/`Vector3` 参数，只接受普通 C# 值对象（数值由宿主从 `WorldHostOptions` 读出后注入，见 §13.3）。守卫 GS007 的白名单里 `Game.Host` 单独放行 `Godot`。
2. **`Game.Core.Ecs` 是 `Game.Core` 的子命名空间，不得依赖任何业务命名空间。** 它同时也是「不含引擎类型」的：内核里只有 `System.Numerics.Vector3/Quaternion`，测试工程因此可以在**不加载 Godot 的进程**里跑（`tests/` 用 `<Compile Include="..\scripts\core\**\*.cs">` 而不是 `ProjectReference`，正是为了绕开 GodotSharp 的运行时依赖）。
3. `Game.Native` 的左下角全部为 ❌，但 ECS 批量求解器（§9.4）是「Core 登记接口 → Host 注入实现 → Native 提供加速」的翻转依赖，**不是** Core 直连 Native。

### 2.4 禁止的反向依赖与典型违规

| 违规 | 例子 | 守卫 | 正确做法 |
|---|---|---|---|
| UI 依赖逻辑层 | `scripts/ui/hud.cs` 里 `using Game.Combat` | **GS004**（warning） | UI 只吃 `Core` 里定义的只读视图接口 / `Data` 的 DTO |
| UI 直连 native | `scripts/ui/hud.gd` 里 `Native.xxx` | **GS004** | 通过 UI 自己的适配层拿已算好的结果 |
| Core 反向依赖 | `Game.Core` 里 `using Game.Data` | 人工评审 | 把共享类型下沉进 Core |
| Combat 依赖表现 | Combat 里 `GetNode<Node3D>()`、播放粒子 | 规则7 评审 | Combat 只发事件（`DamageApplied`），由 Actor/表现层订阅 |
| Actor 直读 Excel | `FileAccess.Open("res://data/excel/...")` | 人工评审 | 必须经 `IConfigSource` |
| 逻辑散进 .tscn 脚本 | 场景里挂 `.gd` 当逻辑 | **GS001**（error） | 挂 `.cs` |

> **GS004 的实际判据**（来自 `config/architecture-rules.json` + `check_architecture.ps1`）：
> `scripts/ui/**` 内允许 `using Game.Core` / `using Game.Data`（只读视图与 DTO），
> 禁止出现 `Native.` 以及 `Game.Combat` / `Game.Actor` / `Game.Skill` / `Game.Spawn` 的引用。

### 2.5 约束

- 依赖方向是**编译期硬约束**：一旦发现要反向，说明抽象位置错了，**挪接口到 `Game.Core`**，不要加 `InternalsVisibleTo` 之类的后门。
- 跨层通信优先用事件，而不是回调引用（见 6.5）。事件类型定义在 `Game.Core`。
- `Game.Core` 不得 `using Godot` 的核心逻辑类（`Node`、`Node3D` 等）——它要能在纯 .NET 测试里跑。
- **`Game.Host` 不得反向被业务层引用**：业务系统若发现自己「需要一个 Node」，说明该职责属于表现层（桥接器），把数据留在组件里、让宿主去读。这条是从「ECS 内核必须零引擎依赖」推出来的必然结论，不是额外约束。

### 2.6 下一步

1. ~~第一个 C# 任务落 `scripts/core/`~~ ✅ 已完成（`Game.Core` + `Game.Core.Ecs` + `Game.Host` 三层都已落地）。
2. ~~在 `config/architecture-rules.json` 里把分层白名单固化（GS007 使用）~~ ✅ 已固化，守卫 0 违规。
3. 出现第一个「需要反向依赖」的真实需求时，**先回写本节 + 第 3 节接口表**，再写实现。
4. **下一步**：`scripts/combat/` 落地时，确认它会同时依赖 `Game.Actor`（读 Transform）与 `Game.Core.Ecs`（发命令），并回写本矩阵新增一行。

---

## 3. 核心抽象与接口清单

### 3.1 现状

**全部未落地**（0 个接口存在）。本节是权威清单：新增接口必须登记在此，附职责/实现者/任务。

### 3.2 接口清单（v1）

> **权威来源**：方法签名以 `docs/rules/03-abstract-first.md` §4.1 与 §4.1.1 为准，本节是它的**下游镜像**。
> 两处不一致时：**改本节去对齐 03**，不要反过来改 03；改签名 = 架构变更，须走 Lead 仲裁。
> 下表 `签名（v1）` 列就是实现时要逐字对齐的契约。

| 接口名 | 职责 | 签名（v1） | 声明位置 | 实现位置 | 实现者（计划） | 已落地? |
|---|---|---|---|---|---|---|
| `IDamageable` | 接受伤害 | `bool TryApplyDamage(in DamageEvent dmg)` | `Game.Combat`<br>`scripts/combat/` | `Game.Actor`<br>`scripts/actor/` | `Actor`、`DestructibleProp` | ❌ 未落地 |
| `IAttacker` | 发起伤害（通常与 `IDamageable` 不同主体） | `DamageEvent BuildDamage(IStatProvider src, in DamageSpec spec)` | `Game.Combat`<br>`scripts/combat/` | `Game.Actor`<br>`scripts/actor/` | `Actor`、`Turret` | ❌ 未落地 |
| `IDamageModifier` | 在结算管线中改伤害 | `void Modify(ref DamageEvent dmg, DamagePhase phase)` | `Game.Combat`<br>`scripts/combat/` | `Game.Combat`、`Game.Skill`<br>`scripts/combat/`、`scripts/skill/` | 词条/护甲/暴击/元素 | ❌ 未落地 |
| `IActor` | 可被瞄准的东西，**夺舍的唯一写入口** | `IActor Handle { get; }`<br>`IStatProvider Stats { get; }`<br>`IVisualDescriptor Visual { get; }`<br>`TeamId Team { get; }`<br>`bool IsAlive { get; }`<br>`bool TryApplyTakeover(in TakeoverSpec spec)` | `Game.Actor`<br>`scripts/actor/` | `Game.Actor`<br>`scripts/actor/` | `Actor`（`Node3D` 表现体 + 纯逻辑壳） | ❌ 未落地 |
| `ISkill` | 一段可释放的技能逻辑（**纯数据契约**：`Id`/`Cooldown`/`TargetRule`/`EffectRef`） | `SkillId Id { get; }`<br>`bool CanCast(in SkillContext ctx)`<br>`SkillCastResult Cast(in SkillContext ctx)`<br>`void Tick(float dt, in SkillContext ctx)` | **`Game.Data`**<br>`scripts/data/` | `Game.Skill`<br>`scripts/skill/` | `SkillRuntime`、`SoulHarvestSkill` | ❌ 未落地 |
| `ISkillTarget` | 技能作用对象句柄 | `SkillTargetKind Kind { get; }`<br>`bool TryResolve<T>(out T target)` | **`Game.Data`**<br>`scripts/data/` | `Game.Skill`<br>`scripts/skill/` | `SelfTarget`、`ActorTarget`、`AreaTarget` | ❌ 未落地 |
| **`ISkillCaster`** | 技能持有、预检、释放与替换的唯一入口；Actor 承载，驱动逻辑在 `scripts/skill/`。技能列表的**受控替换通路**（装备/天赋/夺舍都走这里，不许直接改 `_skills`） | `IReadOnlyList<ISkill> Skills { get; }`<br>`bool CanCast(SkillId id)`<br>`bool TryCast(SkillId id, in SkillContext ctx)`<br>`void TickSkills(float dt, in SkillContext ctx)`<br>`bool TryReplaceSkills(IReadOnlyList<ISkill> skills)` | **`Game.Data`**<br>`scripts/data/` | `Game.Skill`<br>`scripts/skill/` | `Actor`（实现类在 `scripts/actor/`，驱动逻辑在 `scripts/skill/`） | ❌ 未落地（✅ 签名已定稿，见 03 §4.1 仲裁要点 C） |
| `IMovementDriver` | 移动输入 → 速度解算（可替换：地面/飞行/追击/被夺舍） | `Vector3 Resolve(Vector3 desired, float dt)` | `Game.Actor`<br>`scripts/actor/` | `Game.Actor`、`Game.Spawn`<br>`scripts/actor/`、`scripts/spawn/` | `PlayerInputDriver`、`ChaseDriver`、`KnockbackDriver` | ❌ 未落地 |
| `IProjectile` | 子弹生命周期钩子 | `void OnSpawn(ProjectileDef def, ImpactContext ctx)`<br>`void OnHit(IActor victim, in HitInfo hit)`<br>`void OnExpire()` | `Game.Combat`<br>`scripts/combat/` | `Game.Combat`<br>`scripts/combat/` | `BulletEntity`、`HitscanShot` | ❌ 未落地 |
| `IStatProvider` | **数值包的唯一对外读口** | `StatPackage Current { get; }`<br>`float Get(StatId id)`<br>`StatPackage GetLayer(StatLayer layer, string slotId = "")`<br>`IReadOnlyList<ISkill> Skills { get; }`<br>`event Action<StatLayerId> LayersChanged` | `Game.Data`<br>`scripts/data/` | `Game.Actor`<br>`scripts/actor/` | `Actor`、`EquipmentSlot`、`BuffStack` | ❌ 未落地 |
| `IConfigSource` | 生成物加载与版本校验 | `T Get<T>(string table, string key) where T : Resource`<br>`IReadOnlyList<string> Ids(string table)`<br>`int SchemaVersion(string table)` | `Game.Data`<br>`scripts/data/` | `Game.Data`<br>`scripts/data/` | `GeneratedConfigSource` | ❌ 未落地 |
| **`IVisualDescriptor`** | **外观描述符（正式接口）**：换装/换外观的唯一通道，渲染层不感知「画的是玩家还是怪物」 | `CharacterDef Def { get; }`<br>`ProjectileDef ProjectileStyle { get; }`<br>`EffectDef MuzzleFx { get; }`<br>`float Scale { get; }`<br>`void ApplySlot(VisualSlot slot, CharacterDef overrideDef)`<br>`void ResetAll()` | `Game.Actor`<br>`scripts/actor/` | `Game.Actor`<br>`scripts/actor/` | `ActorVisualDescriptor` | ❌ 未落地 |
| `IRandomProvider` | 可播种随机（决定论回放） | `int NextInt(int minInc, int maxExc)`<br>`float NextFloat()`<br>`IRandomProvider Fork(int salt)` | `Game.Core`<br>`scripts/core/` | `Game.Core`<br>`scripts/core/` | `DeterministicRandom`（xorshift，`Fork`/`ForkFromCurrent` 双语义） | ✅ 已落地 `scripts/core/Random/` |
| `IPoolable` | 对象池成员 | `void OnRent()`<br>`void OnReturn()`<br>`void ResetState()` | `Game.Core`<br>`scripts/core/` | `Game.Combat`、`Game.Actor`<br>`scripts/combat/`、`scripts/actor/` | `ObjectPool<T>` 已落地 `scripts/core/Pool/`；业务池成员待建 | 🟡 接口已落地 |
| `IBuildStep` | 构建期流水线步骤（导入脚本内部契约） | `string Name { get; }`<br>`BuildResult Run(BuildContext ctx)` | `Game.Data`<br>`scripts/data/` | `Game.Data`、`tools/data_pipeline/` | 各 pipeline 步骤 | ❌ 未落地 |

> **v1 仲裁结论（2026-11-03）：**
> - **`IVisualDescriptor` 已从候选转为正式接口**。它是「夺舍」等外观替换玩法的抽象基石：`IActor` 持有它、渲染层只消费它，因此**新增一种外观 = 0 个代码文件**（规则7）。职责边界守死：只描述外观（模型/配色/尺寸/挂点/子弹样式/枪口特效），**不持有**数值、技能、阵营、生命值；唯一写入口是 `ApplySlot`/`ResetAll`，由 `IActor.TryApplyTakeover` 调用。
> - **`ISkill` / `ISkillTarget` 的声明下沉到 `Game.Data`**，实现留在 `scripts/skill/`。原因：`IStatProvider.Skills` 的类型是 `IReadOnlyList<ISkill>`，而 `IStatProvider` 声明在 `Game.Data`；若 `ISkill` 声明在 `Game.Skill`，则 `Game.Data` 必须 `using Game.Skill` → 违反 §2.3 分层矩阵。**纪律**：`ISkill` 签名里不得出现任何引擎 `Node` 或表现类型。
> - **`IActor.TryApplyTakeover(in TakeoverSpec spec) : bool`**：夺舍可能失败（目标不可夺、免疫、已在进行中），用 `TryXxx` + `bool` 而非抛异常，与项目 `Is/Try` 风格一致。
> - **`ApplySlot` 采用强类型入参** `ApplySlot(VisualSlot slot, CharacterDef overrideDef)`：`string resourceId` 会让路由字符串在运行期拼错才炸，`CharacterDef` 是 Excel 生成物、可在编译期校验。

#### `ISkillCaster` 归属（Lead 仲裁 #6 已收口，不再是缺口）

> **历史**：`docs/rules/03-abstract-first.md` §3.2.3 的代码样例里 `Actor` 声明实现 `ISkillCaster`，但早期 03 §4.1 全集表里没有它，属**悬空接口**。
> **现状**：已由 design-writer 在 **03 §4.1 作为第 15 个正式接口定义**（含仲裁要点 C），本表签名与之逐字一致，**正式定义以 03 §4.1 为准**。
> **与 ARCH 早期草案的差异**：① 新增 `CanCast` 做预检，与 `TryCast` 拆成「先问后放」，与 `IActor.TryApplyTakeover` 同一范式；② 逐个 `TickSkill(id, dt)` 改为批量 `TickSkills(dt)`，避免每帧 N 次虚调用 + N 次冷却表查找；③ 新增 `TryReplaceSkills` 作为技能列表的**受控替换通路口**——否则装备/天赋模块会直接改 `_skills`，绕过夺舍造成「Base 层是怪物、技能是玩家」的不一致态。
> **两通路纪律**：`IActor.TryApplyTakeover` 内部改 `_skills` = 夺舍专用的四者同事务替换；`ISkillCaster.TryReplaceSkills` = 非夺舍的技能增删，**夺舍中一律返回 false**。外部模块永远不得用后者做半套替换。

**建议但未定编号的候选接口**（由各任务决定是否落地，落地后移入上表）：

| 候选 | 可能职责 | 备注 |
|---|---|---|
| `IEffectSpawner` | 特效请求入口（HitStop/白闪/粒子统一入口） | 见 6.5、7.4 |
| `IHitStopDriver` | 顿帧控制 | 手感核心 |
| `ISaveable` | 存档参与方 | 见 4.7 存档契约 |

### 3.2.0 ECS 侧已落地类型（`Game.Core.Ecs` / `Game.Host`，2026-11-03）

> 这些是**运行时骨架**的契约，不与上面的玩法层接口混列。形状细节见 [ECS.md](ECS.md) §5。

| 类型 | 职责 | 关键签名 | 位置 | 已落地? |
|---|---|---|---|---|
| `World` | 原型集合 + 实体索引 + 组件存储的唯一持有者 | `Create<T>(in T)` / `Destroy(Entity)` / `Query<T0,T1>()` / `CommandBuffer Commands` / `WorldStats Stats` | `scripts/core/ecs/World.cs` | ✅ |
| `Entity` | **句柄**（值类型 `{Index, Generation}`），不是对象 | `IsAlive` / `IsNone` / `static Entity None` | `Entity.cs` | ✅ |
| `Query` | 创建时匹配原型、**增量纳入新建原型**、稳态零分配遍历 | `ForEach<T0,T1>(in ISpanAction<T0,T1>)` / `Count` / `With<T>()` / `Without<T>()` | `Query.cs` | ✅ |
| `CommandBuffer` | 帧内收集结构化变更，帧末按写入顺序一次性提交 | `CreateDeferred(IReadOnlyList<ComponentType>)` / `Set<T>(Entity, in T)` / `Destroy(Entity)` / `Flush()` / `PendingCount` | `CommandBuffer.cs` | ✅ |
| `SystemBase` | 系统唯一基类（读 `World`、实现 `OnExecute(dt)`） | `abstract void OnExecute(float dt)` / `virtual string Name` | `SystemBase.cs` | ✅ |
| `SystemPipeline` | 按 `[UpdateInGroup]`/`[UpdateAfter]`/`[UpdateBefore]` 拓扑排序后分组执行 | `ExecuteGroup(UpdateGroup, float dt)` / `SystemAt(int)` | `SystemPipeline.cs` | ✅ |
| `ISpanAction<T0..T3>` | **零分配**的 chunk 级遍历回调（拿 `Span<T>` 直改内存） | `void Execute(Span<T0>, Span<T1>, int count)` | `Delegates.cs` | ✅ |
| `TypeSet` / `ComponentTypeRegistry` | 组件签名与注册表（签名必须规范唯一，见 §13.5） | `TypeSet.ContainsAll(in TypeSet)` / `Register<T>()` | `TypeSet.cs` | ✅ |
| `FixedFramePipeline` | **一帧七阶段的唯一落地处**（宿主侧） | `Tick(double frameDeltaSeconds, float timeScale)` / `LastPhases` | `scripts/host/FixedFramePipeline.cs` | ✅ |
| `WorldHostOptions` | 宿主全部可调数值的 `[GlobalClass] Resource`（Excel 落地前的合法载体） | `[Export]` 字段 + `Validate()` | `scripts/host/WorldHostOptions.cs` | ✅ |
| `HostEventQueue` | 帧内缓冲事件、帧末统一 `EventBus.Publish` | `Enqueue(in T)` / `DispatchAll(EventBus)` | `scripts/host/HostEventQueue.cs` | ✅ |
| `ViewRegistry` | 实体 → `Node3D` 的视图池（ECS 侧只存 `View.Slot` 这个 int） | `Rent(...)` / `Return(int)` / `SyncTransform(...)` | `scripts/host/ViewRegistry.cs` | ✅ |

> **`HostEventQueue` 为什么在 Host 而不在 Core**：`EventBus.Publish` 是**同步立即派发**——core 不该知道「帧边界」这个概念。而 ECS.md §6 要求「Flush 之后、视图桥之前」统一派发，这是**宿主的时间图职责**。所以「帧内排队的容器」放 Host，「事件总线本身」留 Core。**Lead 裁决，改动需先回写本节。**

### 3.2.1 声明位置 vs 实现位置（为什么分两列）

| 概念 | 放哪 | 判据 |
|---|---|---|
| **声明位置**（interface 定义在哪个命名空间） | 依赖它的**最底层** | 下层不能用上层类型，所以声明必须在「所有使用方都能看到」的层 |
| **实现位置**（class 文件放在哪个目录） | 业务归属层 | 实现体是具体业务，放它所属的层 |

例：`ISkill` 声明在 `Game.Data`（因为 `IStatProvider.Skills` 的类型是 `IReadOnlyList<ISkill>`，而 `IStatProvider` 在 Data），实现 `SkillRuntime` 在 `scripts/skill/`（因为它是技能业务）。
**判定口诀：声明看「谁最底层要用」，实现看「这是谁的业务」。**

> **声明位置的唯一真源**是 `docs/rules/03-abstract-first.md` §4.1.2。改声明位置 = 架构变更，必须同步回写本节 §2.3 矩阵 + §3.2 表 + 03 文档，走 Lead 仲裁。
> **禁止的捷径**：用 `InternalsVisibleTo`、把接口复制一份到上层、或在 `Game.Data` 里 `using Game.Skill`。

### 3.3 抽象层次约定（与规则5 一致）

| 场景 | 用什么 | 理由 |
|---|---|---|
| 跨模块能力契约 | C# `interface` | 可 mock、可多实现、可 default method 演进 |
| 共享字段 + 少量公共实现 | `abstract class` | 仅在确有多态基类需求时使用，**继承深度 ≤ 2** |
| 纯数据 | `sealed record struct` / `sealed record` | 值语义，零 GC 友好，肉鸽快照/回放必需 |
| 可变行为组合 | `interface` + 组合（Composition） | 肉鸽海量组合，深继承会爆炸 |
| 数据定义（配置） | `Resource`（`[GlobalClass]`，`[Tool]`） | 可在检查器可视化编辑（规则4） |
| 引擎层扩展 | GDExtension 注册的 `ClassDB` 类型 | 只在 `native/` |

### 3.4 约束

- **先查表再动手**：新任务开工先读本节，能复用就复用，不能复用的新接口**必须**登记进 3.2，并在任务 PR/汇报里说明。
- 接口放 `Game.Core`，除非它依赖特定层数据（那就放该层，全名写在表里）。
- 接口方法演进用 C# 默认接口方法（`default`）而不是改所有实现（规则7 兼容性）。
- 接口一旦被 ≥2 个模块使用，**禁止**在下游层再定义同名同义接口（复制粘贴式抽象）。

### 3.5 下一步

1. 第一个 Combat 任务先落 `IDamageable` + `IAttacker` + `IDamageModifier` 三件套（同一任务的「抽象先行」环节）。
2. 第一个 Data 任务落 `IConfigSource` + `IStatProvider`。
3. 每次落地把上表「已落地?」改成 ✅ 并填任务名 —— 这是 07 文档第 ⑥ 步的强制项。

---

## 4. 数据流与管线（Excel → 导入脚本 → 生成物 → 运行时 → 存档）

### 4.1 现状

**已落地**：`data/excel/characters.xlsx`（Characters / StatPacks / SkillLoads + `_Meta`）与
`data/excel/combat.xlsx`（Tags / Objects / Weapons / Projectiles / StatPacks + `_Meta`）；
`tools/data_pipeline/` 下已有 `validate_content_xlsx.py`（构建期校验器，2217 项检查）与两个表生成器。

**仍缺**：`data/generated/` 及 xlsx → 生成物的转换器。也就是说 **Excel 已经是真源，但还没有派生物**。

#### 4.1.1 「表 = 运行时」的证据链（2026-11-05 补强）

数据驱动最危险的不是"没做"，而是**看起来做了、实际没接上**。本工程用两条链证明真源真的生效：

| 链路 | 证据 | 注入验证 |
|---|---|---|
| **战斗内容**（combat.xlsx） | `tests/Content/Data/ContentBinderTests` 用 `XlsxReader` **直接解析真表**并断言运行期对象 | 是（改表 → 断言失败） |
| **角色内容**（characters.xlsx） | `tests/Characters/CharacterXlsxParityTests` 打开真表，与内置副本**逐字段、逐数值**比对 | **是**（改 `player_base.Hp` → 精确报出「预期 120、实际 999」） |

> ⚠️ **历史教训（必须记住）**：在这两组用例之前，`CharacterContentRegressionTests` 的注释声称"与
> characters.xlsx 逐行对应"，但它**从不打开 xlsx**，比对的是 C# 常量与 C# 常量。
> 它与 C# 自己完全自洽，因此永远不可能失败 —— 这类"假守护"比没有测试更危险，
> 因为它会让人以为已经安全。**任何声称守护跨层一致性的用例，必须真的触碰另一层；否则不算数。**
>
> 该组用例已改为只声称"内置副本自身自洽"，并显式声明不能证明什么。

**边界不变**：运行期代码仍然**不得**读取 `.xlsx`（§4.8）；`XlsxReader` 只存在于测试工程，
它是 CI 里的尺子，不是运行时依赖。

### 4.2 目标数据流全图

```text
┌─────────────────────── 设计期（人 / Excel） ────────────────────────┐
│                                                                     │
│  data/excel/*.xlsx   ← 按【主题】分文件；一个文件内一张表 = 一个 Sheet │
│    characters.xlsx   → Sheets: Characters / Status                  │
│    combat.xlsx       → Sheets: Weapons / Projectiles / Skills        │
│    presentation.xlsx → Sheets: Effects / Triggers                   │
│    progression.xlsx  → Sheets: Growth / Formula / Waves / Loot       │
│                                                                     │
│  统一约定（每个 Sheet 都相同）：                                      │
│    ├─ Sheet 名 = 表名（Characters/Weapons/Projectiles/Effects/       │
│    │            Skills/Triggers/Status/Growth/Formula/Waves/Loot）    │
│    ├─ 第1行: 字段名 CamelCase（= C# 属性名）                         │
│    ├─ 第2行: 类型 int/float/string/bool/color/resource/curve/enum…   │
│    ├─ 第3行: 中文显示名（编辑器内展示用）                             │
│    └─ 第4行: 备注 / 取值域 / 外键说明                                │
└────────────────────────────┬────────────────────────────────────────┘
                             │  （唯一真源，禁止在代码里写死数值）
                             ▼
                 ╔═══════════════════════════════╗
                 ║ 校验点 ①  表结构校验           ║  tools/data_pipeline/
                 ║ - 表头非空、类型合法、重名      ║  校验失败 → 中断构建，
                 ║ - 报错格式：表名/行/列/期望/实际 ║  中文错误信息
                 ╚═══════════════╤═══════════════╝
                                 ▼
                 ╔═══════════════════════════════╗
                 ║ 校验点 ②  字段级校验           ║
                 ║ - 必填、范围、枚举值存在        ║
                 ║ - 颜色格式 #RRGGBB / Color      ║
                 ║ - 曲线/资源的 res:// 路径可达   ║
                 ╚═══════════════╤═══════════════╝
                                 ▼
                 ╔═══════════════════════════════╗
                 ║ 校验点 ③  跨表外键校验          ║
                 ║ - Characters.weaponIds → Weapons.id ║
                 ║ - Skills.effectId → Effects.id  ║
                 ║ - 缺失外键 = 构建失败           ║
                 ╚═══════════════╤═══════════════╝
                                 ▼
                 ╔═══════════════════════════════╗
                 ║ 校验点 ④  schema 版本一致性     ║
                 ║ - 每张表写 schema_version       ║
                 ║ - 与 pipeline 期望版本不符即报错 ║
                 ╚═══════════════╤═══════════════╝
                                 ▼
┌────────────────── 生成期（tools/data_pipeline/，Python） ──────────┐
│  data/generated/                                                    │
│  ├── characters/<id>.tres     ← CharacterDef : Resource             │
│  ├── weapons/<id>.tres        ← WeaponDef : Resource                │
│  ├── projectiles/<id>.tres    ← ProjectileDef : Resource            │
│  ├── effects/<id>.tres        ← EffectDef : Resource                │
│  ├── skills/<id>.tres         ← SkillDef : Resource                 │
│  └── index/<table>.json       ← { schemaVersion, entries:{id:path} } │
│                                  （启动期一次加载，供 id 查询）       │
└────────────────────────────┬────────────────────────────────────────┘
                             ▼
                 ╔═══════════════════════════════╗
                 ║ 校验点 ⑤  生成物自检           ║
                 ║ - .tres 可被 Godot 解析          ║
                 ║ - index 条目数 == .tres 文件数   ║
                 ║ - 无孤儿文件 / 无重复 id         ║
                 ╚═══════════════╤═══════════════╝
                                 ▼
┌──────────────────────── 运行期（C#） ──────────────────────────────┐
│  GeneratedConfigSource : IConfigSource                             │
│    - 启动时加载 index/*.json，校验 schemaVersion                    │
│    - 懒加载 .tres（ResourceLoader.Load<T>）                        │
│    - 版本不符 → 中文报错并停止（不许静默降级）                      │
│          │                                                          │
│          ▼                                                          │
│  运行时实例（可变副本）                                              │
│    ActorRuntime  ── 由 CharacterDef + StatPackage 构造               │
│    WeaponRuntime ── 由 WeaponDef 构造                                │
│          │                                                          │
│          ▼  数值变化只走 StatPackage（值语义，见第 5 节）            │
│  SaveService  → user://saves/<slot>.json                            │
│    { saveVersion, seed, runState, actors[{defId, statSnapshot,…}] } │
└────────────────────────────────────────────────────────────────────┘
```

### 4.3 Excel 文件组织（v1 统一口径，不得随意新增文件）

**规则：按主题分文件，一个文件内一张表 = 一个 Sheet，Sheet 名 = 表名。**

| Excel 文件 | 主题 | 内含 Sheet（表名） | 主要读者 | 谁维护 |
|---|---|---|---|---|
| `data/excel/characters.xlsx` | 角色本体 | `Characters`、`Status` | 策划 / 角色任务 | 数值任务 |
| `data/excel/combat.xlsx` | 战斗玩法 | `Weapons`、`Projectiles`、`Skills` | 战斗 / 技能任务 | 战斗任务 |
| `data/excel/presentation.xlsx` | 表现与反馈 | `Effects`、`Triggers` | 特效 / 表现任务 | 渲染任务 |
| `data/excel/progression.xlsx` | 成长与流程 | `Growth`、`Formula`、`Waves`、`Loot` | 肉鸽流程任务 | 数据任务 |

**新增一张表怎么办（唯一合法路径）：**
1. 先判断它属于哪个主题 → 加进对应 `.xlsx` 的**新 Sheet**（`characters` / `combat` / `presentation` / `progression`）。
2. 只有当新表明显不属于以上四个主题时，才允许新建一个主题文件（如将来的 `audio.xlsx`），并在本表追加一行。
3. 无论哪种，都要同步：pipeline 注册 → `data/generated/index/` → 本表 → 变更日志。

**禁止：**
- ❌ 用英文单复数混用命名文件（统一**小写复数**主题名：`characters.xlsx` 而不是 `Character.xlsx`）。
- ❌ 一个 `.xlsx` 里塞两个同义 Sheet（如 `Weapon` 和 `Weapons` 并存）。
- ❌ 把 Sheet 名改成与表名不一致（pipeline 按 Sheet 名定位表，改名 = 断链）。
- ❌ **同一个 `PackId` 出现在两个不同的 `Layer`**（见下方「数值包 id 与层是一对多，不是一个 id 多层」）。

#### 4.3.1 数值包 id 与层：**一个 `PackId` 只能占一层**

规则 6 要求数值包分五层折叠（`Base` 绝对值 + `Growth`/`Equipment`/`Buff`/`Temporary` 四层差量）。
但这**不意味着同一个包名可以分好几层写**：

```text
✅ 正确（不同层用不同 id，装配时按列引用）
   hero_base    Base      Hp 120
   hero_base    Base      Mp 50
   hero_growth  Growth    Hp 10
   Characters.GrowthPackage = hero_growth

❌ 错误（同一个 id 想同时当 Base 和 Growth）
   hero  Base    Hp 120
   hero  Growth  Hp 10
```

**为什么禁止**：运行时的 `StatPackageRepository` 以 **`PackId` 为唯一键**
（`Dictionary<string, StatPackageLayer>`），一个 id 只能存一层。上面这种写法会构造出两个
同 id 不同 layer 的对象，注册时直接抛「数值包 id 重复」——也就是说这是**数据表达不了的形状**。
在 Excel 侧拦下的成本 ≈ 0；等它流到运行期，报错会发生在装配阶段，离「改了哪行表」已经很远。

**校验落点**：`tools/data_pipeline/validate_content_xlsx.py` 的 `validate_statpacks()`，
报错会指明「已作为 X 层出现在第 N 行」并建议拆分命名（`hero_base` / `hero_growth`）。
当前 31 个包全部合规（0 个跨层），这条是**预防性约束**。

> 如果将来真需要「同名包分层」，那是**数据模型变更**：键改成 `(PackId, Layer)`，
> 同时 `Compose` / `Find` / `GetLayerValue` 三个入口一并改，并按规则 7 评估扩展成本。
> 在那之前，按层拆名字。

### 4.4 各段的校验点汇总表

| 校验点 | 位置 | 失败策略 | 负责脚本 |
|---|---|---|---|
| ① 表结构 | pipeline 读表后 | 中断，列出表/行/列 | `tools/data_pipeline/` |
| ② 字段级 | pipeline 转换前 | 中断，逐条列出 | 同上 |
| ③ 跨表外键 | pipeline 写盘前 | 中断，指出两表两 id | 同上 |
| ④ schema 版本 | pipeline 开始 + 结束 | 中断 | 同上 |
| ⑤ 生成物自检 | pipeline 写盘后 | 中断并回滚生成目录 | 同上 |
| ⑥ 数值包跨层 | pipeline 转换前 | 中断，指出两行行号 | 同上（§4.3.1） |
| ⑥ 运行期版本 | `GeneratedConfigSource` 启动 | 抛中文异常，禁止降级 | `Game.Data` |
| ⑦ 存档版本 | `SaveService` 读档 | 不兼容则提示并拒绝载入 | `Game.Data` |

### 4.5 生成物 vs 手改

| 文件 | 允许手改 | 说明 |
|---|---|---|
| `data/excel/*.xlsx` | ✅ | **唯一真源** |
| `data/generated/**` | ❌ | 每次构建重生成，手改必被覆盖；已在 `.gitignore` |
| `scenes/*.tres`（表现类资源，如材质/曲线） | ✅ | 表现类可直接在检查器调（规则4 的三条路径之一） |
| `shaders/*.gdshader` | ✅ | uniform 值由 `.tres` 喂，不在 shader 里写死 |

### 4.6 数据对象分类（决定「谁可以改」）

| 类别 | 例子 | 编辑入口 | 生成 | 运行时可变? |
|---|---|---|---|---|
| 平衡数值 | 伤害/血量/射速/冷却/掉落权重 | Excel | → `.tres` | ❌ 只读 |
| 表现参数 | 颜色/粒子数/描边宽度/Curve | Excel 或检查器 | → `.tres` | ❌ 只读 |
| 资源引用 | 模型/贴图/音频路径 | 检查器 | 引用式 | ❌ 只读 |
| 运行时状态 | 当前 HP、Buff 栈 | 代码 | — | ✅ 走 `StatPackage` |
| 存档 | 种子、进度、背包 | 代码 | — | ✅ `SaveService` |

### 4.7 存档契约（v1 草案）

```text
user://saves/<slot>.json
{
  "saveVersion": 1,
  "schemaVersion": "1.0",        // 与生成物 schema 对应，决定了 defId 是否可解析
  "seed": 123456789,             // 肉鸽种子，配合 IRandomProvider 可复现整局
  "run":  { "floor": 3, "roomIndex": 7, "elapsedMs": 812340 },
  "actors": [
    { "defId": "goblin_01", "hp": 34, "stats": { "MoveSpeed": 120.0, ... } }
  ]
}
```

- 存档只存 **defId + 差量**，不存整份 Resource（避免版本爆炸）。
- `schemaVersion` 不匹配 → 拒绝载入并提示，**禁止**猜测映射。
- 回放/预测依赖 `seed` + 确定性随机（`IRandomProvider`）。

### 4.8 约束

- **运行期代码不得读取 `.xlsx`**，只读 `data/generated/`。
- pipeline 必须是**幂等**的：同一份 Excel 重跑产出完全相同（排序稳定、浮点格式化固定）。
- pipeline 使用 DSH 自带 Python（含 `openpyxl`）；**系统 Python 没有 openpyxl**，不要依赖。
- 生成物 schema 变更必须同时改：Excel 表头 → 生成器 → C# `Resource` 类 → 本文件 4.2/4.3 → 变更日志。

### 4.9 下一步

| # | 动作 | 产出 |
|---|---|---|
| 1 | 定 4 张首发表的最小字段集：`Characters` / `Weapons` / `Projectiles` / `Effects` | `data/excel/` |
| 2 | 写 pipeline 骨架：`read → validate① ② ③ ④ → emit → validate⑤`，先支持 `--dry-run` | `tools/data_pipeline/` |
| 3 | 落 `CharacterDef` / `WeaponDef` / `ProjectileDef` / `EffectDef` 四个 `[GlobalClass] Resource` | `scripts/data/` |
| 4 | 落 `GeneratedConfigSource : IConfigSource` + index 加载 + 版本校验 | `scripts/data/` |
| 5 | 第一个 `SaveService`（哪怕只存 seed 和楼层） | `scripts/data/` |

---

## 5. 数值系统（StatPackage 分层：Base/Growth/Equipment/Buff/Temporary）

### 5.1 现状

未落地。设计意图已定，具体 C# 骨架见 `docs/rules/05-numeric-package.md` 提供的参考实现。

### 5.2 目标：分层数值包（Layer Fold）

```text
            ┌────────────── StatPackage 层栈（自下而上叠加） ──────────────┐
            │                                                            │
            │  Temporary  临时层   ← 限时 Buff、场景修饰（如「中毒中」）   │
            │  Buff       增益层   ← 可叠加 buff 栈，每层一个 StatPackage  │
            │  Equipment  装备层   ← 武器/护甲/饰品各自的 StatPackage      │
            │  Growth     成长层   ← 等级、投入点、升华                   │
            │  Base       基础层   ← 来自 CharacterDef（Excel）           │
            │                                                            │
            └────────────── Final = Fold(Base, Growth, Equip, Buff, Temp) ┘
                                     │
                                     ▼
                          StatCalculator / DamageResolver
                          （数值计算的唯一入口，公式来自 Formula 表）
```

| 层 | 来源 | 可变性 | 谁写 | 「夺舍」时的行为 |
|---|---|---|---|---|
| Base | `CharacterDef`（Excel） | 不可变值 | 数据任务 | **被整体替换**为目标怪的 Base |
| Growth | 玩家成长数据 | 不可变值（产生新包） | 角色任务 | 保留（玩家的成长仍是玩家的） |
| Equipment | 装备槽 | 不可变值 | 装备任务 | 保留 |
| Buff | Buff 管理器 | 不可变值栈 | 战斗任务 | 保留（按 buff 的 `keepOnPossess` 决定） |
| Temporary | 场景/技能临时修饰 | 不可变值 | 技能/战斗任务 | 保留 |

> 「夺舍怪物技能」= 取目标 `Base` 包替换自己的 `Base`，其余层原样保留。这正是规则6「数值封装成包、能互相赋值」要的能力。
>
> **所有层的对外类型一律是只读视图 `IReadOnlyStatBlock`**，不是 `StatBlock`。见 §5.4.1 与 `docs/rules/05-numeric-package.md` §6.1.1 ——
> **对外必须只读视图（`IReadOnlyList` / 只读属性 + 只读索引器），可变性只在内部构建器，任何外部模块不得持有可写引用。**

### 5.3 运算契约（与规则5/6 的对应）

| 操作 | 语义 | 对应规则 |
|---|---|---|
| `Copy()` | 深拷贝为新包 | 规则6（可互相赋值） |
| `Combine(other)` | 同层叠加，返回**新包**（不原地改） | 规则6 |
| `AssignFrom(other)` | 同 schema 整包替换某层 | 规则6（「夺舍」） |
| `AssignFromWithDefaults(other)` | 跨 schema：缺的属性取默认、未知属性保留、**不抛异常** | 规则6 |
| `Delta(from, to)` | 差量，用于存档/回放/预测 | 规则6 |
| `ToDictionary()/FromDictionary()` | 存档序列化 | 规则4（可配置持久化） |
| `Fold(layers)` | 分层折叠为 Final | 规则5 |

### 5.4 硬约束

- **StatPackage 是值语义**：任何修改都产生新包。禁止 `package.MoveSpeed += 10f`（违规）。
- **数值公式只有两个入口**：`StatCalculator`（算 Final）与 `DamageResolver`（算伤害）。别处出现 `* 1.5f` 一律视为硬编码违规（GS005 会报）。
- 公式本身也来自 Excel（`progression.xlsx` 的 `Formula` Sheet（`formulaId / expression / 参数名列表 / 说明`）），代码只做求值。
- **StatId 枚举只加不改**：追加新 id 必须走「预留号段 + 追加到末尾」，禁止重排（存档兼容）。
- 双端映射：C# 枚举 ↔ Excel 字符串 ↔ （UI 需要时）GDScript 常量，映射表集中在一个文件。
- 未知属性策略：读取时保留，写入时忽略，序列化时带上 —— 这是跨版本存档不炸的唯一办法。

### 5.4.1 【硬要求】StatPackage 的对外暴露必须是只读视图 `IReadOnlyStatBlock`

> **规则原文（实现必须逐字满足）：**
> **`StatPackage` 的对外暴露必须是只读视图（`IReadOnlyList` / 只读属性 + 只读索引器），可变性只存在于内部构建器；任何外部模块不得持有可写引用。**
> 完整表述与五条落法见 `docs/rules/05-numeric-package.md` §6.1.1（**权威来源**），本节是它的架构侧摘要。

| 项 | 约定 |
|---|---|
| 对外存储类型 | **`IReadOnlyStatBlock`**（只读接口），**不是** `StatBlock` |
| 对外索引器 | `float this[StatId id] { get; }` —— **无 setter** |
| 集合字段 | 一律 `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>`（如 `IReadOnlyDictionary<string,float> UnknownStats`） |
| 可变性唯一位置 | 内部构建器 `StatPackageBuilder` / `StatBlock.SetUnsafe`，两者 **`internal`**，不出 `Game.Data` 程序集 |
| 数组字段 | `RawValues` 只能 `internal`；公开面返回 `ReadOnlySpan<float>` 或拷贝，**不得**返回 `float[]` |
| Fold 结果 | `Final` 是**独立的**不可变 `StatBlock`，不得直接把某层的数组当 Final 返回 |

**为什么这条是硬要求 —— 防止夺舍后原怪物的数值包被共享引用悄悄改掉：**

```text
玩家夺舍史莱姆 → 取到 slimeDef.Base 这个 StatPackage 引用
若 StatPackage 暴露可写的 Values / RawValues：
  ① 战斗系统给"玩家"扣 15 点 Hp，写入落到共享的 Base 层
  ② 实际改到 slimeDef.Base（Excel 生成物！）
  ③ 场景里所有史莱姆血量变成 40-15=25；data/generated 的 .tres 在内存中被污染
  ④ 回滚夺舍时"快照"也已被污染 → 玩家血量回到错误值
```

这类 bug **静默、跨实体、不可复现**（取决于夺舍顺序），排查成本极高。只读视图在**编译期**消灭它：外部没有可写句柄，只能走 `With()` / `AssignFrom*` 产生新包。
**配套纪律**：`Code Review` 时 grep `public .*\[\]|public Dictionary|public .*List<`（限于 `Game.Data`），出现即可写泄漏嫌疑。

### 5.5 下一步

1. 落 `StatId` 枚举 + `StatPackage`（record，对外 `IReadOnlyStatBlock`）+ `IStatProvider`（第一个数值任务）。
2. 落 `StatPackageBuilder`（`internal` 构建器）+ 只读视图接口，并写单测证明「外部无法写入」（`tests/`）。
3. 落 `StatSerializer`（Dictionary 往返）+ 单测（`tests/`）。
4. 落 `StatCalculator` + `progression.xlsx` 的 `Formula` Sheet 最小字段集。
5. 把「Final 计算」接入 Actor 运行时实例，并写一个可视化调试面板（编辑器插件）。

---

## 6. 战斗与技能管线（一帧内伤害结算顺序）

### 6.1 现状

**骨架已落地，结算逻辑未落地。** 已存在：`scripts/actor/MovementSystem.cs`（FixedUpdate 积分 `Transform3.Position += Velocity.Linear * dt`）、`scripts/actor/LifetimeSystem.cs`（Update 递减 `Lifetime.Remaining`，到期**投递销毁命令**而非直接销毁）、`scripts/spawn/SpawnDirectorSystem.cs`（Update 按配置生成子弹实体）。**`scripts/combat/` 仍是空目录**——下面 §6.2 的七步尚未实现。

### 6.1.1 这七步怎么落在 ECS 上（**2026-11-03 新增，必读**）

§6.2 的「步骤」是**逻辑顺序**，ECS 的「阶段」是**时间槽位**，两者不是一一对应。映射关系如下，这也是 `scripts/combat/` 落地时的实现蓝图：

| §6.2 步骤 | 落进哪个 `UpdateGroup` | 系统形态 | 结构化变更怎么处理 |
|---|---|---|---|
| 1 意图收集 | `PreUpdate` | `SystemBase`，写「意图组件」或直接写命令 | 不创建实体，只写组件值 |
| 2 命中检测 | `FixedUpdate` | `Query<Hurtbox, Transform3>` + 遍历后写命令 | 命中判定只读；命中结果进 `HitEvent`（`HostEventQueue`） |
| 3 伤害上下文 | `FixedUpdate` | 纯计算系统（无结构变更） | 不进命令缓冲 |
| 4 伤害修正链 | `FixedUpdate` | `IDamageModifier` 按 `order` 升序；**每步返回新 `StatPackage`**（§5.4 值语义） | 不进命令缓冲 |
| 5 结算 | `Update` | 写 `Health` 组件值 + 投递销毁命令 | **必须以命令形式**——可能致死 |
| 6 事件广播 | **Flush 之后** | `HostEventQueue.DispatchAll(EventBus)` | 此时世界结构已稳定 |
| 7 反馈 | 视图桥阶段 | `TransformBridge` / `VisualBridge`（Host 层） | 只读 ECS 数据，不写回 |

> **为什么命中检测放 `FixedUpdate` 而结算放 `Update`**：命中检测要积分位移、必须恒定 dt（确定性）；结算涉及「打得死不死」，一帧内只该判一次，放 `Update`。这个拆分让「同 seed 同结果」在位移层面成立，而结算策略（暴击、斩杀）可以有帧级随机。
>
> **纪律**：`scripts/combat/` 里的系统**不得**自己 `World.Create`/`Destroy`——一律 `World.Commands`。这条由 `tests/Kernel/ChunkAndPipelineTests.StructuralChange_InsideForEach_Throws` 守护。

### 6.2 目标：单帧结算顺序

```text
 一帧（60fps 预算 16.6ms）内，Combat 阶段的固定顺序 —— 顺序本身就是契约
 ┌──────────────────────────────────────────────────────────────────────┐
 │ 步骤 1  意图收集                                                      │
 │   玩家输入 / AI 决策 / 技能施放请求  →  攻击意图队列                  │
 │   扩展点：IMovementDriver、ISkill.CanCast                             │
 ├──────────────────────────────────────────────────────────────────────┤
 │ 步骤 2  命中检测                                                      │
 │   Hitscan：射线（PhysicsDirectSpaceState3D）                          │
 │   Projectile：抛射物推进（每帧位移 + 扫掠检测，防穿透）                │
 │   扩展点：IProjectile；命中掩码/层由 Def 提供                          │
 ├──────────────────────────────────────────────────────────────────────┤
 │ 步骤 3  伤害上下文构建                                                │
 │   DamageContext { attackerStats, weaponDef, 基础伤害, 暴击判定,        │
 │                   命中部位, 元素类型, 时间戳 }                        │
 │   暴击/命中率走 IRandomProvider（可复现）                             │
 ├──────────────────────────────────────────────────────────────────────┤
 │ 步骤 4  伤害修正链（IDamageModifier 按 Def 的 order 升序）              │
 │   基础 → 攻击加成 → 暴击 → 元素克制 → 目标护甲/减伤 → 易伤 → 封顶     │
 │   扩展点：注册新的 IDamageModifier（0 个文件改动，纯数据 + 一次注册）  │
 ├──────────────────────────────────────────────────────────────────────┤
 │ 步骤 5  结算                                                          │
 │   IDamageable.ApplyDamage(final) → 更新 StatPackage（产生新包）       │
 │   产出 DamageResult { 实际伤害, 是否致死, 是否暴击, 目标 id }          │
 │   扩展点：结算策略（普通/斩杀/固定伤害/百分比）                        │
 ├──────────────────────────────────────────────────────────────────────┤
 │ 步骤 6  事件广播                                                      │
 │   DamageApplied / ActorKilled / StatChanged  （C# event，见 6.5）     │
 │   订阅方：飘字、伤害统计、成就、吸血、连锁反应                         │
 ├──────────────────────────────────────────────────────────────────────┤
 │ 步骤 7  反馈（表现层，只读事件，不回写逻辑）                           │
 │   HitStop 顿帧 → 白闪/受击闪 → 击退位移 → 粒子/震屏 → 飘字 → 音效     │
 │   扩展点：IEffectSpawner、IHitStopDriver                              │
 ├──────────────────────────────────────────────────────────────────────┤
 │ 步骤 8  清理                                                          │
 │   对象池回收 IProjectile / 粒子；死亡实体延迟到帧末销毁（防遍历失效）  │
 └──────────────────────────────────────────────────────────────────────┘
```

### 6.3 每个环节的扩展点

| 环节 | 扩展点 | 扩展方式 | 改动成本（规则7 判据） |
|---|---|---|---|
| 意图 | `IMovementDriver` / `ISkill` | 实现接口 + 数据引用 | 1 文件 |
| 命中 | `IProjectile` 实现 / 命中层配置 | 新实现 + 新 `ProjectileDef` | 1~2 文件 |
| 伤害上下文 | Def 字段（暴击系数字段化） | Excel 加列 | 0 代码文件 |
| 修正链 | `IDamageModifier` 实现 + 注册 | 新实现 + 1 次注册 | 1 文件 |
| 结算 | 结算策略枚举 / 策略接口 | 加枚举值 + 策略实现 | 1~2 文件 |
| 事件 | 新事件类型 + 新订阅者 | 不触碰 Combat | 1 文件 |
| 反馈 | `IEffectSpawner` / 表现配置 | 新特效资源 + `EffectDef` | 0 代码文件 |

### 6.4 HitStop / 打击感约定（手感优先）

| 项 | 约定 |
|---|---|
| HitStop 时长 | 走 `EffectDef.hitStopMs`（数据），**禁止**代码写死 |
| 时间缩放实现 | 全局 `Engine.TimeScale` 与局部（目标节点）两档；优先局部，避免整局停顿 |
| 白闪 | 材质参数（`shader_parameter/flash_amount`），由 `IEffectSpawner` 驱动，秒表在表现层 |
| 击退 | 表现为 `IMovementDriver` 的一层叠加（不直接改位置，防穿模/防跳过碰撞） |
| 震屏 | 单独一个 `CameraShakeDriver`，强度/时长/衰减来自数据 |
| 顿帧时的逻辑 | 逻辑层照常 Tick，只有表现层时间缩放（避免逻辑与表现耦合） |

### 6.5 事件分工

| 场景 | 用什么 | 例子 |
|---|---|---|
| 纯逻辑、跨模块、无 Node | **C# `event`**（定义在 `Game.Core`） | `DamageApplied`、`StatChanged`、`WaveCleared` |
| 需要连到场景树 / 检查器连线 / 编辑器可见 | **Godot `Signal`** | `Actor.hp_changed`、`UI.button_clicked` |
| 数据配置驱动的「何时触发」 | **事件表**（Excel `Triggers`） | 技能触发条件、掉落规则 |

> 规则：**逻辑事件不经过 Node**（否则无法单测、无法脱离场景跑）。Node 只在表现层做信号中转。

### 6.6 约束

- Combat 层**不持有** `Node` 引用（除物理查询用到的 `PhysicsDirectSpaceState3D` 句柄，需经注入）。
- 单帧内**禁止**遍历中增删集合：死亡与生成统一进延迟队列，帧末处理。
- 修正链顺序由数据 `order` 字段决定，不靠代码注册顺序。
- 伤害计算**确定性强**：同 seed + 同输入 → 同结果（回放/预测前提）。

### 6.7 下一步

1. 落「秒杀测试场景」：一个胶囊靶子 + 一发子弹，跑通步骤 1→8 的最小闭环。
2. 落 `DamageContext` / `DamageResult` / `IDamageModifier` / `IDamageable`。
3. 落 HitStop + 白闪 + 击退 三个反馈，参数全部走 `EffectDef`。
4. 把「夺舍」作为第一个**复合技能**验证扩展性（见 `docs/rules/06-extensibility.md`）。

---

## 7. 渲染与特效架构

### 7.1 现状

| 项 | 值 | 来源 |
|---|---|---|
| 渲染器 | `Forward Plus`（Forward+） | `project.godot` → `config/features` |
| Windows 驱动 | `d3d12` | `rendering/rendering_device/driver.windows` |
| 拉伸模式 | `canvas_items` + `aspect=expand` | `display/window/stretch` |
| 物理 | `Jolt Physics` | `physics/3d/physics_engine` |
| 自定义 shader | 0 个 | — |
| GDExtension | 未建立 | — |

### 7.2 目标：三级升级路径（成本从低到高，**禁止跳级**）

```text
 级别 0  引擎现成能力（默认，绝大多数情况够用）
   |   标准材质 / StandardMaterial3D / 内置后处理（SSAO/SDFGI/Glow/DoF）
   |   GPUParticles3D、MultiMesh 实例化、WorldEnvironment
   ▼  什么时候升级：需要「多个标准 pass 的组合」但引擎没有现成开关
 级别 1  引擎扩展点（优先！不碰引擎源码）
   |   CompositorEffect（Godot 4）→ 在渲染管线中插入自定义 RD pass
   |   RenderingServer / RenderingDevice 直接建 RID（纹理、compute、buffer）
   |   自定义 gdshader（空间/画布）+ 材质参数动画
   ▼  什么时候升级：CompositorEffect 无法表达（需要改渲染管线的 pass 顺序/内部实现）
 级别 2  引擎 fork（最后手段，需 Lead 批准）
   |   记录：fork 分支名、基线版本 4.7.2-stable、patch 清单、能否上游化
   |   必须在 native/ 或 tools/ 留下 patch 与重建说明
```

| 级别 | 判定条件 | 代码位置 | 审批 |
|---|---|---|---|
| 0 引擎现成 | 能通过材质/环境/实例化达成 | `shaders/`、`scenes/` | 无需 |
| 1 扩展点 | 需要自定义 pass 或 RD 资源，但**不改引擎内部** | `shaders/` + 必要时 `native/` | 任务内自决，回写本节 |
| 2 fork | 必须改引擎 C++ 内部 | 引擎 fork 仓 + `native/` | **Lead 仲裁** |

### 7.3 Shader 清单（v1，全部「未落地」）

| 名字 | 用途 | 文件 | uniform 来源 | 状态 |
|---|---|---|---|---|
| `toybox_lit.gdshader` | 低多边形玩具质感主体（硬边 + 柔和 GI） | `shaders/toybox_lit.gdshader` | `MaterialDef`（Excel→`.tres`） | ❌ 未落地 |
| `outline_pass.gdshader` | 描边（法线外扩 / 屏幕空间） | `shaders/outline_pass.gdshader` | 描边宽度/颜色 ← `EffectDef` | ❌ 未落地 |
| `hit_flash.gdshader` | 受击白闪/染色 | `shaders/hit_flash.gdshader` | `flash_amount`、`flash_color` ← `EffectDef` | ❌ 未落地 |
| `dither_fade.gdshader` | 死亡/刷新溶解（玩具碎块消散） | `shaders/dither_fade.gdshader` | 噪声贴图、阈值曲线 ← `EffectDef` | ❌ 未落地 |
| `ground_grid.gdshader` | 地面网格/房间边界提示 | `shaders/ground_grid.gdshader` | 颜色、线宽 ← `Palette` | ❌ 未落地 |
| `post_color_grade.gdshader` | 色调映射/色彩分级（CompositorEffect 内） | `shaders/post_color_grade.gdshader` | LUT 纹理、强度 ← `EnvironmentDef` | ❌ 未落地 |

> **命名铁律**：`<feature>_<pass>.gdshader`（规则2）。
> **uniform 铁律**：所有可调参数必须是 `uniform`，由 `.tres` 或代码注入，**禁止**在 shader 里写死数值（规则4）。

### 7.4 特效架构

```text
 逻辑层（Combat/Actor）只发事件，从不直接播粒子
        │  C# event: DamageApplied / ActorKilled / ActorSpawned
        ▼
 IEffectSpawner （表现层门面，Game.Actor 或独立 VFX 模块）
        │  按 EffectDef 查表：粒子资源 / 材质闪 / 震屏 / HitStop / 音效 / 飘字
        ▼
 EffectDef (.tres, 由 Excel Effects 表生成)
    ├── particleScene  : PackedScene（GPUParticles3D）
    ├── flashMaterial  : Material
    ├── hitStopMs      : float
    ├── knockback      : float
    ├── shakeIntensity : float
    └── sfxId          : String
```

约束：**表现层单向消费**——特效不得回写战斗数值；同一帧同一目标的多条伤害事件需要合并（避免叠爆闪光）。

### 7.5 约束

- 渲染相关代码若进 C#，只允许**配置与调用**；真正的逐帧 pass 逻辑进 `native/`（GS003）。
- 不允许在 `_Process` 里每帧 `new` 任何 RD 资源，RID 生命周期必须显式管理。
- 任何新增 shader 必须登记到 7.3 表，并写清 uniform 来源。
- d3d12 是**唯一目标 RHI**（现配置）；写 shader 不要用只在 Vulkan 可用的扩展。

#### 7.5.1 窗口 → 视口：**`stretch/mode` 必须是 `disabled`**

`project.godot` 的 `[display]` 段：

```ini
window/size/viewport_width=1600
window/size/viewport_height=900
window/size/resizable=true
window/stretch/mode="disabled"
window/stretch/aspect="expand"
```

**为什么不能用 `canvas_items`**（2026-11-06 用户实测 bug，已修）：
`stretch/mode="canvas_items"` 的语义是「把内容渲染到一张**固定基准分辨率**的画布，再缩放贴到窗口」。
一旦没显式写 `viewport_width/height`，基准就落到引擎默认的 **1152×648**，于是：

| 拖到的窗口 | 实际 3D 视口 | 现象 |
|---|---|---|
| 1280×720 | 1152×648 | 画面被放大 |
| 1920×1080 | **1152×648** | 窗口变大了，画面**一点没变**，只是被拉伸 |
| 1280×1024 | 1152×921 | 只有高度跟着动，宽度锁死 |

**这就是「拖动窗口不修改渲染画面」的根因**：视口尺寸被钉死，相机看到的范围自然不变。

**为什么本项目可以直接关掉 stretch**：本作是纯 3D 俯视射击，HUD 是**自绘 `Control`**
（`CharacterHudDrawer` 在 `_Draw` 里按自身 `Size` 重建几何，本来就会自适应），
不存在需要像素级缩放的 UI 栅格图。关掉之后 `视口尺寸 == 窗口尺寸`，窗口多大就渲染多大。

> **相机视野仍独立控制**：`TopDownCameraRig` 用 `KeepAspect = Height` + 正交 `Size`
> 决定「看到多大一片世界」。窗口变宽 → 横向看到更多（正确行为）；想改视野范围改 `OrthoHeight`，
> 不要改 stretch。

**回归探针**：`tools/probes/viewport_resize_probe.gd`（只读探针，规则1 白名单 `tools/probes/**`）。
带窗运行（**不能加 `--headless`**，没有 DisplayServer 就没有窗口可测）：

```powershell
& "E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe" --path . `
    --script tools/probes/viewport_resize_probe.gd
```

它把窗口依次设成 1280×720 / 1920×1080 / 1280×1024 / 800×450，每次读回
`root.get_visible_rect().size`，断言**视口 == 窗口**。实测对照：
`canvas_items` 下 **4/4 失败**（视口恒为 1152×648），`disabled` 下 **4/4 通过**。

> **这类 bug 编译、日志、架构守卫都发现不了**——stretch 配错不报任何错，只是静默缩放。
> 所以必须靠「设尺寸 → 读回尺寸 → 比对」的探针来钉住，不能只靠肉眼。

#### 7.5.2 窗口 → 2D 自绘图：**`Node2D` 子节点拿不到布局尺寸**

`stretch/mode` 修好只保证「视口跟着窗口变」，**不代表画在视口里的东西也跟着变**。
`DungeonMap2D` 的地图是 `DungeonMap2DView`（`Node2D`）用 `DrawRect` 逐格画的，格子边长固定 `16px`，
所以窗口拉大时**已经铺满的视口里插进一片空白，图本身一像素没变**。

根因在容器协议上，不在绘制代码：

| 事实 | 后果 |
|---|---|
| `Node2D` **没有**布局尺寸（`Size` 属于 `Control`） | `ScrollContainer` 只能拿 `GetRect()` 当内容大小 |
| 内容大小 = 图的像素尺寸 | 图放大 → 内容变大 → 滚动区跟着变大 |
| 滚动区永远「刚好装下」图 | **图既缩不小也放不大**，形成自洽的死结 |

正确做法是**用父容器自己的 `Size` 当可视区**——它才是真正的窗口，`ScrollContainer`
自己的尺寸由 `HBoxContainer` 布局决定，与内容无关：

```csharp
// 在 _Process 里轮询，而不是订阅信号：
// ScrollContainer 尺寸变化 → 子节点变化，两者都不保证发出可用信号，
// 每帧一次比较 + 极少次 QueueRedraw，成本可忽略。
Godot.Control? host = GetParent() as Godot.Control;
FitToViewport(host.Size);   // clamp(min(可用宽/格数, 可用高/格数), 下限, 上限)
```

三个配套决定：

1. **上限 `MaxCellPixels=40`**：小地图在 4K 窗口里无限放大只会变成马赛克块，夹上限后居中显示。
2. **滑条一动就关掉 `AutoFit`**：手动调过像素 = 用户接管，之后窗口缩放不再覆盖他的选择。
3. **`SetValueNoSignal` 回填滑条**：自动算出的值要反映到 UI 上，但不能再触发一次「用户改了滑条」。

实测探针 `tools/probes/dungeon_map_autofit_probe.gd`（12×28 格地图）：

| 窗口 | 可视区 | 格子 | 图尺寸 |
|---|---|---|---|
| 800×450 | 452×450 | 14.64 px | 176×410 |
| 1280×720 | 932×720 | 24.29 px | 291×680 |
| 1600×900 | 1252×900 | 30.71 px | 369×860 |
| 1920×1080 | 1572×1080 | 37.14 px | 446×1040 |

四个尺寸全部「装得下」，且**大窗口格子必大于小窗口**（探针把这条写成断言）。
对照实验：撤掉改动后格子恒为 `16.00px`、图恒为 `192×448`，探针报 2 个失败——
说明它测的是真行为，不是自我确认。

### 7.6 下一步

1. 先做**级别 0** 的 Toybox 观感基线：一套 `toybox_lit.gdshader` + `WorldEnvironment` 预设，跑通一个可看场景。
2. 做 `hit_flash.gdshader`（最常用、收益最高）。
3. 做 `outline_pass.gdshader`（Stylized 风格核心）。
4. 做第一个 `CompositorEffect`（后处理：色彩分级或 SSAO 调优），验证级别 1 通路。
5. 决定是否建 GDExtension 骨架（第一个「必须下沉」需求的触发器，见 9.4）。

---

## 8. UI 层（唯一允许 GDScript 的层）

### 8.1 现状

未落地，`scripts/ui/` 不存在。

### 8.2 目标：UI 的分界

```text
┌──────────────────────── 逻辑层（Game.Combat / Actor / Skill） ──────┐
│  HP、冷却、Buff 状态、伤害数字、波次进度 —— 真值在这里                │
└──────────────────────────────▲───────────────────────────────────────┘
                               │ 只读事件 + 只读 DTO / 视图接口
┌──────────────────────────────┴───────────────────────────────────────┐
│                     UI 适配层（scripts/ui/，C# 优先）                │
│  - 订阅事件（C# event 或 Godot Signal）                              │
│  - 把数据转成可显示字符串/进度条数值/图标 id                          │
│  - 节流（伤害数字合并、道具栏 0.1s 刷新）                             │
└──────────────────────────────▲───────────────────────────────────────┘
                               │ 只做「展示 / 输入采集」，不含规则
┌──────────────────────────────┴───────────────────────────────────────┐
│              UI 表现层（Control 节点 / .tscn / 可选 .gd）             │
│  - 布局、动画（Tween）、本地化、输入绑定                               │
│  - 纯布局脚本允许 .gd（白名单 scripts/ui/**，规则1）                  │
└──────────────────────────────────────────────────────────────────────┘
```

### 8.3 允许与禁止

| 允许 | 禁止 |
|---|---|
| `scripts/ui/**` 内使用 `.gd` 做**纯布局/纯动画/纯编辑器工具** | 在任何其他目录使用 `.gd`（GS001 error） |
| UI 层读取 `Game.Core` 的只读视图接口、`Game.Data` 的只读 DTO | `using Game.Combat` / `Game.Actor` / `Game.Skill` / `Game.Spawn`（GS004 warning） |
| UI 层用 Godot `Signal` 连场景树 | UI 直接改战斗数值、直接调 Combat 结算 |
| 同一子目录内统一一种语言 | 同一子目录 `.gd` 与 `.cs` 混用（GS002） |
| 编辑器插件（`addons/biu_biu_editor/`）用 C# `[Tool]` | UI 层引用 `Native.` 原生类（GS004） |

### 8.4 语言选型（UI 层内部）

| UI 子域 | 推荐 | 理由 |
|---|---|---|
| HUD（血量/弹药/冷却） | **C#** | 需要订阅逻辑事件、做节流，C# 类型安全 |
| 菜单 / 设置 / 存档选择 | **C#** | 与存档服务交互 |
| 纯视觉布局小部件（图标高亮动画、描边按钮） | **.gd 允许** | 无逻辑，改动快，热迭代 |
| 编辑器可视化调参面板 | **C# `[Tool]`** | 需要访问 `Game.Data` 类型 |
| 调试覆盖层（性能计数） | **.gd 允许** 或 C# | 非正式功能 |

### 8.5 约束

- **UI 不持有真值**：UI 显示的一切都必须来自逻辑层事件/DTO，UI 侧不缓存可写状态（除纯表现状态如动画进度）。
- **节流**：伤害飘字/血条变化必须合并刷新，避免每帧刷新 100 个 `Label`。
- **本地化**：可见文本不硬编码中文/英文，走本地化 key（`tr()`），key 与 Excel 的显示名列对应。
- **UI 性能预算见 9.2**，超了就降刷新频率而不是加线程。

### 8.6 下一步

1. 建 `scripts/ui/` 与 `scenes/ui/` 骨架，落第一个 HUD（血量 + 当前武器）。
2. 定义 `IHudDataSource` 只读接口（放 `Game.Core`），UI 只依赖它。
3. 定义本地化 key 规范，并在 Excel 显示名列落地。
4. 编辑器调参面板（`addons/biu_biu_editor/`）—— 让「数值/颜色/特效可视化修改」闭环（规则4）。

---

## 9. 性能基线与预算

### 9.1 现状

无任何可测内容，本节是**待验证的目标基线**。

### 9.2 目标预算（60fps @1080p，单帧 16.6ms）

| 阶段 | 预算 (ms) | 说明 |
|---|---|---|
| 逻辑 Tick（C#，Actor/Combat/Skill/Spawn） | **≤ 4.0** | 超了先优化，仍超则下沉 C++ |
| 物理（Jolt） | **≤ 2.0** | 大量抛射物时飙升，重点监控 |
| 动画 / 骨骼 | **≤ 2.0** | |
| 渲染提交（主线程） | **≤ 3.0** | 剔除、材质切换、多线程命令 |
| GPU | **≤ 10.0** | 与总预算重叠，实际以 GPU 帧时间为准 |
| UI | **≤ 1.0** | |
| 预留余量 | **≥ 2.0** | 突发特效、GC、加载 |

### 9.3 规模目标（v1 假设，可实测调整）

| 指标 | 目标上限 | 备注 |
|---|---|---|
| 同屏敌人 | **200** | 割草手感底线；超过走 C++/MultiMesh/Job 化 |
| 同屏抛射物 | **500** | 高频小实体的主压力点 |
| 同屏粒子系统 | **60** | GPUParticles3D，合并发射 |
| 命中事件/帧 | **300** | >300 需批处理伤害结算 |
| 帧率 | **1080p 稳定 60** | 不达标的优化顺序见 9.5 |

### 9.4 C# GC 分配预算与「何时下沉 C++」判据（★ 本章节为**唯一真源**）

> **★ 唯一真源声明**：本节（含 9.2 帧预算、9.3 规模目标、9.4 GC 预算与下沉判据）是**性能阈值的唯一真源**。
> `docs/rules/02-tech-selection.md` 等其他文档**只引用、不复制**具体数字——它们写「阈值见 `ARCHITECTURE.md` §9.2~§9.4」，避免两处数值打架。
> 调整阈值 = 改本节 + 变更日志留一行；**不要**去改 02 文档里的数字。

**GC 分配预算：**

| 指标 | 预算 | 判据 |
|---|---|---|
| 稳态每帧托管分配 | **≤ 32 KB/帧** | 超过 64 KB/帧 → 必须查分配热点 |
| 单帧 GC0 次数 | **≤ 1** | 连续多帧 GC0 → 立即修 |
| 每次 Tick 中 `new` 对象数 | **≤ 8**（热路径） | 超了就上对象池 / `record struct` |
| 单帧 C# 逻辑耗时占比 | **≤ 40%** 帧时间 | **> 50% 且已验证无分配可省 → 下沉 C++（规则2 判据）** |
| 单次热路径方法调用 | **< 10,000 次/帧** | **> 10,000 次/帧 → 考虑 C++ 批量实现** |
| 批量实体 | **< 200 个** | **≥ 200 且逐帧遍历 → 考虑 C++/MultiMesh** |

> **下沉判据总表（规则2 **唯一依据**，02 文档只引用不复制）**：
>
> | 触发条件（任一满足） | 动作 |
> |---|---|
> | 单帧热路径方法调用 > 10,000 次 | 考虑 C++ |
> | 批量实体 ≥ 200 且需要逐帧计算 | 考虑 C++ |
> | 需要 `RenderingDevice` / `RenderingServer` 直接操作 | **必须** C++（或在 CompositorEffect/`RenderingServer` C# 绑定可用时优先用绑定） |
> | 需要逐帧修改渲染管线 / 自定义 pass 内部实现 | **必须** C++（级别 1 或 2） |
> | Profiler 显示 C# 逻辑占比 > 50% | 下沉 C++ |
> | 稳态分配 > 64 KB/帧 | **先修分配**，不要急着下沉 |
>
> 这些阈值是**初始假设**，实测后在本节调整，并在变更日志留一行。
> **改这里的数字 = 全工程判据同步生效**；02 文档若出现与本节不一致的数字，**以本节为准**（并让维护 02 的人删掉重复数字）。

### 9.5 测量与优化顺序

1. **先测再改**：`Performance` 面板 + Godot Profiler + .NET `dotnet-trace`；
2. 优化顺序：**分配 → 算法 → 批处理/池 → 数据布局 → 语言下沉**（越往后越贵）；
3. 每次性能任务结束，把实测数字回写本节（「实测值」列）。

| 指标 | 目标 | 实测值 | 测于（任务/日期） |
|---|---|---|---|
| 逻辑 Tick | ≤ 4.0 ms | 待测 | — |
| GC 分配 | ≤ 32 KB/帧 | 待测 | — |
| 同屏敌人 200 帧率 | ≥ 60 | 待测 | — |

### 9.6 下一步

1. 建性能基线场景（`tests/` 或 `scenes/debug/`）：200 敌人 + 500 抛射物，可一键跑并在 HUD 显示帧时间。
2. 接一个 C# 侧分配计数器（`GC.GetAllocatedBytesForCurrentThread()`）到调试覆盖层。
3. 第一次实测后回写 9.5 的「实测值」列。

---

## 10. 已知技术债与风险

| # | 类型 | 描述 | 影响 | 缓解措施 | 状态 |
|---|---|---|---|---|---|
| R1 | 风险 | ~~工程完全空壳~~ **已关闭**：ECS 骨架（内核 + 宿主 + 首批系统）已落地，63 测试全绿 | — | — | **已关闭 2026-11-03** |
| R2 | 风险 | **`godot-cpp` 版本错配**：vcpkg port 只有 4.4 | GDExtension 无法编译 | ✅ **已解决**：上游**没有 4.7 分支**（4.x 分支最多到 4.5）。改为 `master` + **锁定 commit hash** + `-DGODOTCPP_API_VERSION=4.7`（master 内置 `extension_api-4-7.json`）。详见 `native/README.md` | **已关闭 2026-11-03** |
| R3 | 风险 | **GDExtension API json 未生成** | 首次 native 构建会阻塞 | ✅ **已绕开**：不需要 `--dump-extension-api`，godot-cpp master 自带 `extension_api-4-7.json`；`gdextension_interface.h` 用 `--dump-gdextension-interface` 生成（**写到 `--path` 根目录而非 cwd**，142,425 B 已就位） | **已关闭 2026-11-03** |
| R4 | 风险 | **d3d12 单 RHI**：只验证过 d3d12，若某 shader 依赖 Vulkan 特性会踩坑 | shader 返工 | 优先用通用 `gdshader` 特性；确需 Vulkan 特性时先评估切换 RHI 成本 | 开放 |
| R5 | 风险 | **.NET 10 + Godot 4.7.2 组合较新** | SDK 行为差异、`net10.0` 目标框架兼容 | 依赖本地离线 NuGet 源（`Godot.NET.Sdk.4.7.2` / `GodotSharp.4.7.2` / `Godot.SourceGenerators.4.7.2`），禁止公网拉包 | 开放 |
| R6 | 技术债 | **UI 层允许 GDScript 是双刃剑** | 语言分裂，逻辑易漏进 `.gd` | GS001/GS002/GS004 守卫 + 8.4 选型表 + 评审 | 开放 |
| R7 | 技术债 | **数据管线未实现**，Excel 真源为空 | 规则4 无法验证 | 第 4.8 节 5 步落地 | 开放 |
| R8 | 技术债 | **生成物路径 `data/generated/` 需在 `.gitignore`** | 若漏掉会污染仓库 | env-tools 任务已在 `.gitignore` 中处理，落地后复核 | 部分处理 |
| R9 | 风险 | **肉鸽确定性**：随机数分散使用会导致回放/种子不可复现 | 存档、日种子榜失效 | 全工程随机只经 `IRandomProvider`（第 3 节），禁用 `GD.Randf()` / `System.Random` 直接调用 | 开放 |
| R10 | 技术债 | **性能基线与实体上限均为假设值**（9.3/9.4） | 过早优化或过晚发现瓶颈 | 首次性能任务实测后回写 9.5 表 | 开放 |
| R11 | 技术债 | ~~无自动化测试~~ **已部分解决**：`tests/` 63 用例（内核/分层/确定性/零分配四类）+ `tools/check_all.ps1` 一键（build → 守卫 → test） | 仍缺 CI 串联 | 接 CI 只差一条流水线配置 | 🟡 部分关闭 |
| R12 | 风险 | **旧原型 `biu-biu`（全 GDScript，17 个 .gd）易被误抄** | 违反规则1 | 只读参考；禁止拷贝代码进本仓库（规则1 明文） | 开放 |
| R13 | 技术债 | **`scripts/core/ecs/components/OfficialComponents.cs` 把 11 个官方组件放在同一个文件**（ECS.md §4.2 要求「组件按名寻址」，文件粒度未约定） | 寻址不受影响；但「新增组件改几个文件」的走查答案会从 1 变成「1 个文件里的第 N 个 struct」 | 组件数 < 50 时保持现状（便于对照契约表）；超过后按用途拆 `components/transform/`、`components/combat/` 等子目录 | 开放（有意为之） |
| R14 | 技术债 | **`BiuBiuOrigin.csproj` 需手工维护**：本机 Godot `--build-solutions` 不写 `.csproj`/`.sln` | 引擎升级时需手工跟 SDK 版本；`DefaultItemExcludes` 写错会立刻 CS0579 | 已在 csproj 内注释写明原因与两个必排除目录（`.godot/**`、`tests/**`）；每次改此文件后必须 `dotnet build` 验证 | 开放 |
| R15 | 风险 | **ECS 批量求解器（C++）尚未接通**：`native/` 只有骨架与 `EcsBatchLayout` 头文件 | 性能路线 B/C 无法验证 | 先用纯 C# `ForEachJob` 跑满基线（§9.3 的 500 抛射物），实测不达标再下沉（规则2 判据） | 开放 |

---

## 11. 变更日志

> 写法：一行一句话 + 影响文件列表。只记「架构形状变了什么」，不记实现细节。
> 不更新正文但改动了工程的任务（纯美术、纯调参、纯 bug 修复）也必须在此留一行，变更摘要里注明「未改架构」。

| 日期 | 版本 | 任务 | 变更摘要 | 影响文件 |
|---|---|---|---|---|
| 2026-11-10 | v17 | **相机跟随手感：临界阻尼弹簧 + 鼠标方向可调偏移（§15.8）** | 用户要求「① 相机丝滑跟随人物，要有滑动感 ② 基于鼠标（射击方向）的可调参幅度偏移」。**修两个「配置了但没接」的老问题**：① `FollowLerp` 存的是**逐帧 lerp 系数**（0.14），却被当成**每秒速率**喂给 `1-exp(-rate*dt)` → 收敛仅 ~13%/s，相机几乎不动；② `LookAheadRatio`/`LookAheadMaxMeters` **只活在 Resource 上**，从未进 `CameraPackSettings`，Excel 改这两格零效果。**做法**：把数学拆成零引擎依赖的 `scripts/core/Camera/CameraFeel.cs`（`grep -c "using Godot"` = 0，故 `tests/` 那条「不引 GodotSharp 也能编 `scripts/core/**`」的结构性守卫继续成立），`TopDownCameraRig` 退化为薄壳。跟随改用**二阶临界阻尼弹簧**（zeta=1，解析解 `x' = target + (A+B*dt)*e^(-w*dt)`），位移严格单调、**过冲恒为 0**、任意 dt 稳定；瞄准偏移独立弹簧 + 死区（边界连续、`[dz,1]` 重映射到 `[0,1]`）+ 封顶。**数据层**：`CameraPacks` 5 列 → 7 列，`FollowLerp` **改名** `FollowRatePerSecond`（语义从「系数」变「速率」，改名而非加列，让旧调用点编译期就断），新增 `LookAheadRatio`/`LookAheadMaxMeters`；`follow_std 10.0/0.18/1.6`、`follow_heavy 7.0/0.14/1.4`、`follow_fast 14.0/0.24/2.0`。**踩坑（记下来别再犯）**：速度必须取自解析式，**禁止**用 `(x_next-x)/dt` 反推——后者在起步段把「位置被阻尼缩回的部分」当速度再喂回去（正反馈），实测 60 帧后发散到 **2,355,357**。**验证（两层）**：① 纯函数测试 26 用例全绿 + **4 个变异全部被抓**；其中「1 秒终点残差」原理上区分不了弹簧与固定 lerp（lerp 残差 1.5e-5 反而更小），**只有中段 0.15s 才暴露 1.27m 差异**，故帧率守卫职责交给三点检查。② 新增引擎内探针 `--camera-probe` 8 项全绿：人物移动 5m 后 `focus` 距新位置 **0.0029m**、瞄准 ±X 时偏移 **+1.5903/-1.5903**（摆动 3.1807m、不超 1.600m 上限）；同样做变异（改成跟出生点 → 偏差恰好 5.0000m；改成常数偏置 → 符号不变号 + 超上限）。**最关键的教训**：第一版探针让人物**静止**就断言「focus 贴着人物」，**变异测试骗过了它**——人物不动时「跟」与「不跟」观测值完全相同，必须**制造真实位移**才能让命题可判定。**已知债务（刻意不做）**：`CameraPacks.Height` 被灌进 `PitchDeg`、`FovDegrees` 从未被读——修它会改掉所有镜头俯仰角、污染本轮调参的因果判断，单独排期。**测试** 382 通过 / 3 失败，3 失败经 stash 纯净基线对照证实与相机无关（另一会话 `characters.xlsx` 在途改动）；架构守卫 **0 error / 0 warning** | `scripts/core/Camera/CameraFeel.cs`、`scripts/host/view/TopDownCameraRig.cs`、`scripts/host/GameViewOptions.cs`、`scripts/host/CharacterSession.cs`、`scripts/host/DungeonDebugScene.cs`、`scripts/host/debug/CameraFollowSelfCheck.cs`、`scripts/data/CharacterAssemblySpec.cs`、`scripts/data/CharacterContentSet.cs`、`data/excel/characters.xlsx`、`tools/data_pipeline/gen_characters_xlsx.py`、`tools/data_pipeline/validate_content_xlsx.py`、`tests/Camera/CameraRigTests.cs`、`tools/probes/camera_follow_probe.gd`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-10 | v16 | **训练房（§14.10）：一间灰房 + 一个人 + 一个假人** | 用户要求「做一个3D测试场景，只有一个灰色房间，四面有墙，地板是斑马网格，一个人物，一个测试假人」。新增 `scenes/debug/TrainingRoom.tscn` + `scripts/host/debug/TrainingRoomScene.cs`（约 380 行）。① **场地**：`RoomCells×RoomCells`（默认 8×8、1m 格）逐格 `BoxMesh` 拼棋盘格地板（`(x+z)&1` 深浅灰交替），四面墙独立尺寸封住四角；房间大小/格数/墙高/配色**全部 `[Export]`** 可在 Inspector 改。② **人物**：整块交给 `CharacterSession` + `test_hero` 全套（复用组合根，不另写一个人），本场景 0 行玩法逻辑。③ **假人**：用户明确选「先只要站着的模型、打不动」→ 做成**纯表现节点**（胶囊身体 + 球头），**不进 ECS**、子弹直接穿过；这是**写在文档里的有意欠账**，要可打时换成 `ActorSpawnRequest` 即可。④ **素材**：全部几何体 + 纯色材质，**不引用 `素材/` 任何 `.glb`**，与 §17.10.1「全部先跑白模」一致。⑤ **两个坑**：`CharacterSession` 构造时自己装系统并调 `pipeline.Build()`（宿主再 `Add` 会重复移动/抛异常）；自检不能在 `_Ready` 数 `Player`（要到第一次 `Tick` 才出生，会误报「没出生」），改延迟 5 帧。**验证**：`dotnet build` 0 错 0 警；架构守卫 `new=0 existing=0 errors=0 warnings=0`；headless 实跑自检 `floors=64/64 walls=4/4 dummy=True parts=2 hero=True`。 | `scripts/host/debug/TrainingRoomScene.cs`、`scenes/debug/TrainingRoom.tscn`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-10 | v14 | **全部先跑白模：占位路径清零（§17.10.1）** | 用户看了 §17.10 交回的占位配型后决定：「先全部用白模即可，占位不要使用"素材"的路径，后续需要挑选让如项目资源文件夹中。」**上一轮的问题**：占位配型虽诚实标注，本质仍是用素材库原料区假装成品——`素材/` 是美术原料区，不是项目运行时资源，路径随时会挪动（历史上 `res://素材/Characters/TestHero.glb` 正因如此不存在却静默回退白模，长期无人发现）。**本轮收敛**：① `combat.xlsx` 20 个 + `characters.xlsx` 4 个 = **24 个外观包的 `ModelPath` 全部留空**，`TintHex`/`AnimClips` 一并清空（死字段），`Scale` 保留待模型回来生效；② **包壳（Id）全部保留**当桶键用，`Objects.VisualPack` 的 26 条引用一条未改——以后挑好资产**只改 `ModelPath` 一格**，不合并成 `vp_none` 是为了不丢「手枪≠子弹≠哥布林」的分类信息；③ `vp_none` 语义收敛为「★ **永远**不要模型」；④ **校验器第 ③ 条由 error 降为 `[提醒]`**：全白模后 26 个物件会全红、校验直接失败，但删掉又会丢掉「谁还在等资产」的追踪——降级后白模能跑，同时每个待配模型的物件留一条提醒，回填即消失。**原则**：「暂时没有」要能被看见（提醒清单），「永远没有」要能被声明（`vp_none`），两者都不能靠空着表达。⑤ 改动落在**生成器**（`gen_combat_xlsx.py`/`gen_characters_xlsx.py`）而非手改 xlsx，表由生成器重新产出。**验证**：校验 **4510 项通过** exit 0（含 23 条「等配模型」提醒）；`dotnet build` 0 错 0 警；`ContentBinder` 20 个用例全绿；全套 374 通过 / 2 失败（两条均为另一会话 `CharacterContentSet.cs` 在途改动，已用备份表对照复现，与本轮无关）；架构守卫 `errors=0`，5 条 GS005 全在 `CharacterContentSet.cs`（另一会话），本轮文件零命中 | `tools/data_pipeline/gen_combat_xlsx.py`、`tools/data_pipeline/gen_characters_xlsx.py`、`tools/data_pipeline/validate_content_xlsx.py`、`data/excel/combat.xlsx`、`data/excel/characters.xlsx`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-09 | v13 | **模型属性收敛为唯一入口（§17.10）** | 用户问「角色/怪物/武器/子弹是否有模型属性？能在 3D 中看到资产吗？」，随后发现真表里「有的有、有的没有」并追问「当时没抽象吗？」。**诚实的答复**：`RenderId` 从 §17 起就在，抽象没缺；真缺陷是**同一概念有三条并存写入路径**（`VisualPack` / `AssetPath` / 都不填）而**没有唯一入口**。按用户决定「删掉 AssetPath/AssetAnim，全部走 VisualPack」收敛：① 从 `ObjectArchetype`、`ContentBinder`、`combat.xlsx`、生成器、校验器删掉两列，全工程只剩 `ObjectArchetype.VisualPackId`；② **「有意没有模型」变成取值而非空缺**——特效三个 `fx_*` 填新包 `vp_none`（`ModelPath` 留空），于是「有意为空」与「忘了填」在数据上永远可区分；③ 校验器三条新规则：`Objects.VisualPack` **必填**、外键闭合、**有 Actor/Weapon/Projectile 能力位的物件不许指向无模型包**（否则玩家永远看不见它且不报错）；`VisualPacks.ModelPath` 由必填改可空，但空包填 `TintHex`/`AnimClips` 报错（死字段）。检查数 2217 → **4553，exit 0**。④ 补齐 26 个物件的外观包（20 个包；新增 `vp_slime_skull` / `vp_slime_bomb` / `vp_none`）。⑤ 四个用例钉住，测试 **374 通过**（另 2 个失败是并行会话的 `characters.xlsx` 缺口，非本次）。**如实说明**：视图层未接（用户要求本轮只补字段与绑定），武器与子弹在 3D 里仍不渲染；素材库 301 个 `.glb` 无怪物/子弹素材，食人魔用炮台、史莱姆用骷髅/炸弹、子弹用小矿石/金币/宝石代替，`VisualPacks.Note` 逐条写明。另补 `l10n.xlsx` 缺的 `actor.player`。守卫 `new=5 existing=0 errors=0`（5 条 warning 全是并行会话 `CharacterContentSet.cs` 的既有 GS005）。 | `scripts/core/Content/ObjectArchetype.cs`、`scripts/data/Content/ContentBinder.cs`、`data/excel/combat.xlsx`、`data/excel/l10n.xlsx`、`tools/data_pipeline/gen_combat_xlsx.py`、`tools/data_pipeline/validate_content_xlsx.py`、`tests/Content/Data/ContentBinderTests.cs`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-09 | v12 | **「抽象没贯彻到底」全面体检 + 契约硬规则** | 用户发现「只有人物有模型，怪物/武器/子弹连字段都没有」并质问抽象为何没贯彻。**诊断**：抽象**不缺**，断的是"谁兑现"——`View`+`ViewAppearance` 组件与整套桥接都在且设计正确，但全项目唯一的外观注册调用只在 `CharacterSession.cs:308`（人物一条路）。**最值钱的证据链**：`combat.xlsx` 26 行全填 `VisualPack` → 校验器 4553 项全过（连磁盘文件都存在）→ `scripts/` 里运行期读取点 **= 0**。数据填满、校验通过、一个都不读。**为什么既有防线全没拦住**：编译器（组件合法只是没人 Set）、守卫 GS001~007（只查文件位置）、356 个测试（只测局部行为）、校验器（只验数据对不对不验有没有人用）、代码评审（单看每处都对，缺口在跨文件接缝）——**共同特征：每个局部都对，整体是断的**。**整改**：① 渲染接线收敛到唯一入口 `ContentSpawner.FillAppearanceBySlot`，三条产物路径（弹/人/特效）无条件经过；② 修掉真实陷阱——`VisualSlot` 默认 `0` 但 `0` 是合法外观号（"忘了配"与"配成第 0 号"运行期同义），改为显式哨兵 `UnresolvedVisualSlot = -1`；③ 新增 `AppearanceCoverageTests` 两条用例钉住"三种产物组件齐备"与"未配置仍带组件且可断言"；④ **规则升格为硬约束**：`docs/rules/03` 新增 §3.4「概念的唯一定义权」+ §6.1「契约完备四问」，`AGENTS.md` 规则5 判据扩充 + 红线新增第 11~15 条，四条硬规则 A单一契约 / B齐备原则 / C消费闭合 / D成员闭合。**如实说明（未修，已登记 §17.11.4）**：子弹仍有 4 套平行组件集合、`WeaponState` 死组件致生产与测试跑两条管线、`WeaponAimKind`(11)/`ProjectileHitPolicy`(8) 零消费者、`EcsObjectFactory.OnExpire` 静默吞 `Explode`/`Inherit`、`FireProjectileSkill.cs:202` 硬编码 `DamageType.Kinetic`、武器/子弹在 3D 里仍不渲染。测试 356 通过（另 3 条 `HeroAssembly_*` 是既有失败，与本次无关） | `scripts/combat/Factory/ContentSpawner.cs`、`scripts/core/Content/ObjectArchetype.cs`、`scripts/data/Content/ContentBinder.cs`、`scripts/core/Content/TagFamily.cs`、`tests/Content/Factory/AppearanceCoverageTests.cs`、`docs/rules/03-abstract-first.md`、`AGENTS.md`、`docs/architecture/ARCHITECTURE.md` |

| 2026-11-09 | v11 | **2D 自绘图随窗口自动缩放（§7.5.2）** | 用户反馈「拖动窗口渲染画面仍然不变大变小」，且是用 `DungeonMap2D` 场景测的——**上一版只修了视口，没修画在视口里的 2D 内容**。① 根因：地图由 `DungeonMap2DView`（`Node2D`）逐格 `DrawRect` 画在 `ScrollContainer` 里，格子边长固定 `16px`；`Node2D` 没有布局尺寸，`ScrollContainer` 只能拿 `GetRect()` 当内容大小 → **图放大就撑大滚动区，永远刚好装下，既缩不小也放不大**。② 修复：新增 `FitToViewport(available)`，**用父容器自身的 `Size`**（它才是真正的可视窗口）算格子像素，`clamp(min(可用宽/格数, 可用高/格数), 2, MaxCellPixels=40)`；在 `_Process` 里轮询而非订阅信号（尺寸变化不保证发可用信号），仅一次比较 + 极少次 `QueueRedraw`。③ 交互配套：滑条一动即置 `AutoFit=false`（用户接管），自动值用 `SetValueNoSignal` 回填滑条，避免「回填又触发一次用户改动」的循环。④ **探针** `tools/probes/dungeon_map_autofit_probe.gd` 断言「四种窗口尺寸下都装得下」+「大窗口格子必大于小窗口」：实测 14.64 → 24.29 → 30.71 → 37.14 px 全部通过；**撤掉改动后格子恒为 16.00px、探针 2 个失败**，证明它测的是真行为。守卫 `0/0`，测试 `357/357`。**经验**：视口修好 ≠ 内容跟着变；`Node2D` 放进 `ScrollContainer` 时必须显式指定滚动区尺寸，否则内容与滚动区互相撑大，永远看不到「装下」以外的状态。 | `scripts/host/debug/DungeonMap2DView.cs`、`scripts/host/debug/DungeonMap2DPanel.cs`、`tools/probes/dungeon_map_autofit_probe.gd`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-09 | v9 | **调参面板：数值控件换成可左右拖动的滑块** | 用户原话「数值改为左右拖动的滑块」。① `SpinBox` 只能点箭头/键盘输入，调参时视线在图、手要找小箭头，频繁打断看图；20 根控件全部换成 **`HSlider` + 定宽数值标签**（标签固定宽度，拖动时位数变化不让整列跳动），在地图旁边既能拖又能随时看到当前值。② 节流 `RebuildDebounceMs` **90 ms → 30 ms**：原值是给「点箭头一次跳一格」定的，连续拖动下明显发黏；实测重算平均 0.57 ms、稳态最差 8.92 ms（仍 < 一帧 16.6 ms），故可放宽到几乎每帧都算。③ **无头可验证**：自检新增 `sliders=` / `spinBoxes=` 计数并断言 `spinBoxes==0` —— 数的是场景树里真实存在的节点类型，不是「我写了 AddSlider 所以它是滑块」。实际结果 `sliders=20 spinBoxes=0`，并用临时探针验证「改值 → 6 帧内地图重画 `mapChanged=True`」链路通（探针已删除，不留诊断代码）。守卫 0 error | `docs/architecture/ARCHITECTURE.md`、`scripts/host/debug/DungeonMap2DPanel.cs`、`scripts/host/debug/DungeonMap2DDebugScene.cs` |
| 2026-11-07 | v8 | **物件随机生成（§14.9）+ 2D 图例带色** | 在 2D 栅格地图之上加一层**物件生成**，并修掉 2D 视图「图例没颜色」的体验缺陷。① **四类物件 = 四种放置规则**：`DungeonProp`（多格矩形 + `Kind` + `Variant`）、`DungeonPropConfig`（数量/尺寸/离墙，`With()` 派生 + `Validate()`）、`DungeonPropPlanner.Populate(in map, seed, cfg)` → **新地图**（`readonly struct` 值语义，原图不动）。顺序 **建筑 → 不可破坏 → 可破坏 → 奖励**（大件优先，否则大建筑被小物件饿死）；**可破坏在连通性上等同墙**（没子弹的玩家打不烂箱子），**奖励不挡路**（踩过去就捡）。② **两个真 bug**：(a) 四个阶段各建一张 `blocked` 掩码 ⇒ 建筑占的格在摆奖励时「看不见」，奖励可叠在建筑上、连通性检查拿的是残缺掩码；改为**一张掩码贯穿全部阶段**。(b) 校验器把 `Reward` 也算挡路 ⇒ 正常地图被判「堵死通路」；改为生成器与校验器**共用同一口径**。③ **离墙格数是「尽量」**：小房间两边各留 1 格后只剩 1 格，`TryPickOrigin` 逐格退让 `WallClearance→0`；房间边长 < `BuildingMinRoomSide` 直接不摆建筑（与其检验事后拒绝，不如挑选时排除）。④ **2D 视图**：图例从一行纯文字改为**每项先画真颜色色块再写名字**（色块取自 `DungeonRoomStyleSet`，换 `.tres` 自动同步）；四类物件用**形状**区分（建筑整块铺满+描边 / 奖励菱形 / 可破坏圆点 / 不可破坏小方块）；面板新增 6 根物件滑条；自检输出追加 `props(B/R/b/o)` 计数。⑤ **验收**：`dungeon_check --props --seeds 500` → 地图 500/500、建筑 5.65·奖励 5.80·可破坏 11.85·不可破坏 7.50、**堵路 0 / 压门 0 / 压非地面 0**；headless 自检 `props(B=26 R=6 b=12 o=8)`。守卫 0 error / 0 warning | `docs/architecture/ARCHITECTURE.md`、`scripts/spawn/rooms/{DungeonProp,DungeonPropConfig,DungeonPropPlanner,DungeonMap}.cs`、`scripts/host/debug/{DungeonMap2DView,DungeonMap2DPanel,DungeonMap2DDebugScene}.cs`、`tools/dungeon_check/{PropCheck.cs,DungeonCheck.csproj,Program.cs}` |
| 2026-11-09 | v8 | **数值包跨层校验 + 窗口缩放修复** | ① **Excel 校验器新增「同一 PackId 不能跨层」**（§4.3.1）：运行期 `StatPackageRepository` 以 PackId 为唯一键，同 id 写两层会构造出两个同 id `StatPackageLayer`，注册时抛「数值包 id 重复」——这是**数据表达不了的形状**，必须在建表期拦下。`validate_statpacks()` 记录 (Layer, 行号)，冲突时报「已作为 X 层出现在第 N 行」并建议拆成 `hero_base`/`hero_growth`。现有 31 个包全合规（0 跨层），属预防性约束；注入测试 4/4 双向往返通过（该报的报、不该报的不报）。**刻意不改运行期键**为 `(PackId, Layer)`——那要同时改 `Compose`/`Find`/`GetLayerValue`，收益不抵成本。② **修「拖动窗口不改变渲染画面」**（§7.5.1）：`window/stretch/mode` 由 `canvas_items` 改为 `disabled`，补 `viewport_width/height=1600×900`、`resizable=true`。根因：`canvas_items` 把内容渲染到**固定基准分辨率**，未显式指定时回落引擎默认 **1152×648**，故窗口怎么拖 3D 视口都恒为 1152×648。**这类 bug 编译/日志/守卫全发现不了**，故新增只读探针 `tools/probes/viewport_resize_probe.gd` 实测对照：旧配置 **4/4 失败**（视口锁死 1152×648），新配置 **4/4 通过**（视口 == 窗口）。相机 `KeepAspect=Height` 本就正确，非病因。 | `tools/data_pipeline/validate_content_xlsx.py`、`project.godot`、`tools/probes/viewport_resize_probe.gd`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-05 | v7 | **武器触发与弹道运动学：把纸面契约接上消费方** | **审计发现「契约有了、消费方缺失」**：`WeaponTrigger` 仅在 `ContentBinder` 被*解析*、**无任何系统读它**；`ProjectileAdvanceSystem` 只有一行 `pos += vel*dt`，故 `combat.xlsx` 里 `Homing`/`Falling`/`Wave` 三种子弹**全按直线飞**，`Gravity=9.8`、`HomingTurn=rand:…` 是死数据。① **新增 `WeaponFireSystem`**（`Update` 组，`UpdateAfter(SkillCastSystem)`）：七种 `WeaponTrigger` 的唯一消费者——`Auto` 按住连射 / `Semi` 边沿只打一发 / `Burst` 起手 / `Charge` 蓄力松手发射 / `Hold` 持续 / `Triggered`+`Manual` 不吃输入；弹药门控优先于一切；开火后立即起冷却与扣弹。② **判定抽成纯函数** `WeaponFireRules`（+`WeaponValues` 做 `ValueSet→标量`），使手感断言不必跑整个 `World`。③ **8 种 `ProjectileMotion` 全部实现**：`Homing`（罗德里格斯限速转向，查 `Query<Hurtbox,Transform3>` 最近目标）、`Orbit`、`Wave`、`Boomerang`、`Falling`、`Spiral`、`Anchor`、`Straight`；运动参数在**发射瞬间定一次**写入 `ProjectileState`（新增 `Motion/HomingTurn/Gravity/BaseSpeed/Age/LaunchDirection` 六字段），避免 `rand:` 逐帧漂移；缺发射者/目标时**退化为直线**而非 NaN。④ **修 1 个静默 bug**（记入 §17.9.3）：`ISpanAction` 执行器是 struct 按值传入，把扫描结果存在执行器身上导致 `Execute` 改的是副本 → `TryFindHomingTarget` 恒 false → 追踪弹静默退化为直线，**而既有测试全绿**（它们只用 `Straight`）。⑤ **新增 §17.9**（触发表 / 运动表 / 教训）；§17.7 补三行「明确不做」：`WeaponDelivery` 其余五种投送、`ProjectileHitPolicy` 运行时结算、`WeaponFireSystem` 尚未在 `DungeonDebugScene` 注册。**测试 179 → 225 全绿**（规则 22 + 运动学 11 + 集成 7），守卫 0 error / 0 warning | `scripts/combat/WeaponFireSystem.cs`、`scripts/combat/WeaponFireRules.cs`、`scripts/combat/WeaponValues.cs`、`scripts/combat/ProjectileAdvanceSystem.cs`、`scripts/combat/CombatComponents.cs`、`scripts/combat/Factory/ContentSpawner.cs`、`tests/Combat/WeaponFireRulesTests.cs`、`tests/Combat/WeaponFireSystemTests.cs`、`tests/Combat/ProjectileMotionTests.cs`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-05 | v6 | **素材快查看器 v2：修复用户实测的 4 个 bug** | **用户第一轮实测报的 4 个 bug 全部修完并逐条验收**。① **滚轮后点击错位**（点到的资源不是点的那一个）：`CellRect()` 是绘制与命中的唯一真源，但 `RebuildFilter()` 会把 `scrollY_` 一起重置，导致重建后绘制偏移与命中偏移两个口径分叉 —— 改为**筛选重建时不重置滚动**（另有分支做钳制）。`probe_hit` 断言 14 个滚动位置 × 316 单元格 = **100.00% 命中，零错位**。② **模型不在面板正中 / 显示不全**：`CameraState::distance` 2.5→1.0 并按包围盒自适应；实测真 GUI 模型中心 (849,433) vs 预览区中心 (850,430)，**误差 3px**。③ **模型没有纹理**（见 §16.5 教训表该行，两个独立根因：贴图预览写死 32px 且 UV 只占图集 7.24% → 角色贴图只剩 8×9 像素；另有 123 个模型 `baseColorFactor` 极暗被"黑底兜底"换成随机灰）——修法：`kPreviewMaxEdge` **32→256** + 96MB 预算分级降级，黑底兜底阈值 0.02→0.0005，D3D 与 CPU 两条渲染路径各加**自适应曝光**。④ **没有动画与动画列表**（见 §16.5 该行：FBX 80 字符截断使 clip 名退化为 `Dea`/`Pun`/空串）——`NormalizeName` 改写为取中间完整非噪音段 + 新增 `StripDupSuffix`，Anne 的 14 条 clip 全名恢复；`probe_anim` 证明每个 clip 驱动 49~57 根骨骼、蒙皮覆盖 84.2% 顶点。**共享契约变更（详见新增 §16.9）**：`Vertex` 32→48 B（顶点色）、`GpuVertex` 64→80 B、`TextureInfo::preview` 32→256 px。**另修一个只在 `/MT` 静态版复现的堆破坏崩溃**（`printf` 里两个 `std::string` 临时量，`exit=0xC0000374`，稳定停在 `Bird.glb`）。**验收**：`loader_smoke` 371/371（`/MD` 与 `/MT` 双版）、`ui_selftest` 77/77、`render_regress` 301/301 可见、`thumb_sheet` 371/371 wall 21.61s @256px。`dist/` 已刷新（619KB，MD5 与构建产物一致） | `tools/asset_viewer/src/{model_data.h,glb_loader.cpp,fbx_loader.cpp,texture.h,texture.cpp,d3d_renderer.cpp,d3d_renderer.h,thumb_renderer.cpp,viewer_app.cpp,viewer_app.h}`、`tools/asset_viewer/tests/{loader_smoke.cpp,ui_selftest.cpp}`、`tools/asset_viewer/dist/**`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-05 | v5 | **角色真源证真：xlsx 直读比对** | **补上「Excel 唯一真源」缺失的自动证据**：新增 `tests/Characters/CharacterXlsxParityTests`（5 用例）——直接解析 `data/excel/characters.xlsx`，与内置副本**逐字段、逐数值**比对（Characters 9 列 + StatPacks 全部属性 + 能否装配成实体）。新增 `tests/Characters/CharacterXlsxBinder.cs`（复用既有 `XlsxReader`，零第三方依赖，报错带 Excel 真实行号）。`CharacterContentSet` 新增 `RegisterPackRow/RegisterCharacterRow/RegisterLoadoutRow/FlushStatPacks/FlushLoadouts`，使 xlsx 读取与内置副本**走同一代码路径**。**注入验证**：把表中 `player_base.Hp` 改为 999，测试精确报出「预期 120、实际 999」→ 证明不是假守护。修正 `CharacterContentRegressionTests` 里「与 xlsx 逐行对应」的**虚假注释**（它从不读 xlsx），改为显式声明只能证明"内置副本自洽"。§4.1 重写现状 + 新增 §4.1.1 证据链表与「假守护」教训。**发现待决问题**：`StatPackageRepository` 以包 id 为键，同包跨层会互相覆盖（当前数据未触发）。测试 174 → 179，守卫 0 error | `tests/Characters/CharacterXlsxParityTests.cs`、`tests/Characters/CharacterXlsxBinder.cs`、`tests/Characters/CharacterContentRegressionTests.cs`、`scripts/data/CharacterContentSet.cs`、`docs/architecture/ARCHITECTURE.md`
| 2026-11-06 | v5 | **基础物件抽象（武器/子弹/怪物同源 + 熔炉组合）落地** | **新增 §17 基础物件抽象**：人物/怪物/武器/子弹**不是四个类**，而是同一个 `ObjectArchetype`（= `combat.xlsx` 的 `Objects` 一行）——差异只在 ① `ObjectCapability` 能力位（`[Flags]`：Actor/Weapon/Projectile/Emitter/Trigger/Consumable/Structure，可任意组合）② 四个 `ContentRef` 引用（Weapon/Projectile/Actor/Effect，**谁都可以指向任何 Objects.Id**）③ 位矩阵 + 规则值。① **规则值 `ValueRef`**（10 种来源：Literal/Stat/WeaponStat/Flags/ChainDepth/ChainCount/ViableCount/Perf/RandomRange/RandomScaled）+ `ValueSet`（容量4，`Σabs×(1+Σpercent)` 层叠）——**「子弹继承武器属性」= 表里写 `wstat:WeaponDamage(5)`，代码 0 行**；② **熔炉 `ForgeRules`**：64 位配方字 × 簇内加权轮盘，纯静态只吃 `ReadOnlySpan`，**1000 次迭代 0 字节托管分配**（用例断言）；`ContentRegistry` 按「簇+槽位名」编址，`Purge(generation)` 回到不可变 `InitialDefId`；③ **工厂等价性**：武器射子弹 / 武器射怪物 / 子弹炸开再射子弹 / 子弹变成怪物，在 `EcsObjectFactory.TryCast` 里是**同一段代码**，差别只有契约里哪个引用非空 + `Delivery`/`OnExpire` 两个枚举（4 条端到端用例读真表钉住）；④ **策划可改（需求②）**：`combat.xlsx` 6 Sheet（Tags/Objects/Weapons/Projectiles/StatPacks/_Meta）+ `validate_content_xlsx.py` **2217 项构建期检查**（外键闭合、能力位一致、TagIndex 连续/BitMask 唯一、规则值语法真解析、**同簇能力位一致**、ClusterWeight>0）+ 零第三方包 `XlsxReader` 读真 xlsx 的绑定回归（「改表 → 运行期行为变」可证）；⑤ **明确不做**：碰撞/空间划分/伤害结算（游戏逻辑，`ProjectileAdvanceSystem` 只做积分+寿命，未实现分支显式计数不静默）；⑥ **修 6 个静默失效 bug**（记入 §17.8）：FNV-1a32 有符号哈希被当"空引用"判据（同一错误出现 3 处）、`Purge` 回不到初始态、`Projectiles` 行主键与引用列口径不一致致**所有枪静默打不出子弹**、`tag.projectile` 物件拿不到契约、`characters.xlsx` 表头被生成器转置。**测试 101 → 174 全绿**；架构守卫 0 error / 0 warning | `docs/architecture/ARCHITECTURE.md`、`scripts/core/Content/**`（ValueContext/ObjectArchetype/ObjectBitMatrix/WeaponContract/ProjectileContract/ContentWorld/ContentIndex/WeaponAimKind）、`scripts/core/StatId.cs`（+17 成员：武器 1030~1041、投射物 1050~1054）、`scripts/combat/**`（CombatComponents/ForgeRules/ContentRegistry/CastQueue/CastSystem/ProjectileAdvanceSystem/Factory）、`scripts/data/Content/**`（ContentBinder/ValueRefParser）、`data/excel/combat.xlsx`、`data/excel/characters.xlsx`（表头修复）、`tools/data_pipeline/{gen_combat_xlsx,validate_content_xlsx}.py`、`tools/data_pipeline/README.md`、`tests/Content/**`、`tests/BiuBiuOrigin.Tests.csproj` |
| 2026-11-05 | v4 | **素材快查看器（Asset Viewer）落地** | **新增 §16 素材快查看器**：独立 Win32 桌面程序（**不进游戏、不是 GDExtension**），CMake + MSVC + D3D11/Direct2D，**零第三方依赖**。① **边界**：加载器只吐 `ModelData` 纯数据、**不碰 D3D**，渲染器不碰文件格式 —— 故 `thumb_sheet` 能在无窗口环境复用同一套加载代码。② **格式**：自研 glTF 2.0（`.glb`）与 FBX 7.2/7.4/7.5 解析器，**自带 DEFLATE 解压器**（不为 FBX 压缩数组引 zlib）。③ **构建**：`av_core`/`av_ui` 静态库 + `asset_viewer`(WIN32) + `loader_smoke`/`ui_selftest`/`thumb_sheet`(CONSOLE) + `av_sheet` 自定义 target；`AV_STATIC_RUNTIME=ON` 走 `/MT` 免装 VC++ 运行库。④ **修复 8 处实打实缺陷**，全部写进 §16.5 教训表：Inflater 无输出上限致 42GB OOM、畸形 count 直接喂 `resize()`、FBX 子网格材质归一化口径不一致致死循环、`WM_NCCREATE` 期 `hwnd_` 未赋值致 `CreateWindowExW` 失败(errno=1400)、cmd 批处理中文参数变 `??`、`windows.h` 的 `DrawText` 宏污染 `d2d1.h`、多 slot 顶点布局触发 `0x80070057`、`MakeLookAt` z 轴取反致模型不可见。⑤ **验收**：`loader_smoke` 371/371、`thumb_sheet` 371/371（wall 4.56s @16 线程）、`ui_selftest` 77/77、`--shot=` 出图成功。⑥ **交付** `dist/` 单文件绿色版 + 中文启动器 + 使用说明 | `tools/asset_viewer/**`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-05 | v4 | **3D 默认配置回归修复（§14.8.1）** | 2D 层全绿时发现 `DungeonDebug.tscn` 加载即崩：`DungeonOptions` 默认 `8×8 / side=1` 装不下「四面留 1 格隔离带」的隐藏房，8 次重试全败。工具用另一套 `9×24/side=3` 所以一路绿 —— **测试覆盖面 ≠ 产品实际走的路**。修复三处：① `DungeonOptions` 默认放大到 `11×11 / side=2`；② `check_all.ps1` 新增 step 4b `--options`，用 3D 实机默认值再跑一遍同样的属性测试（500 种子全绿），从此「check_all 全绿」等于「游戏能开」；③ `TryPlaceHidden` 的候选位间距从 `RoomSide + ring` 改为 `max(宿主房实际边长, RoomSide) + ring`，起始/Boss 房更大时不再把候选位摆进宿主房里。新增 `tools/dungeon_check --options` 与 `DungeonOptionsDefaults()`。 | `tools/check_all.ps1`、`tools/dungeon_check/Program.cs`、`scripts/host/DungeonOptions.cs`、`scripts/spawn/rooms/BranchDungeonPlanner.cs`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-05 | v4 | **2D 栅格地图层 + 实时调参面板（§14.8）** | **同一份 `DungeonLayout` 再投影出一张俯视 2D 格图**（正方形=房间格、颜色=房型/地砖、方块=障碍），把「哪里通、哪里堵」变成可目视、可程序化验证的东西；3D 白盒仍是唯一玩法路径，未被替换。① **栅格化**：新增 `DungeonMap`（`Kind[]`/`RoomSalt[]`/`DoorSlot[]` + `ToArt()/FromArt()`，字符图例 `#`墙 `.`地面 `+`门 `,`走廊 `X`障碍 `?`秘密通道 `S c $ h B` 房型）、`DungeonMapBuilder`（铺地→连廊→打门→挖秘密通道→撒障碍）、`DungeonMapConfig`（margin/障碍密度/离墙格数，不可变 `With()`）、`DungeonMapCheck`（泛洪可达、房间割裂、陷阱格、Boss 可达、隐藏房双不变量）。② **实时面板**：`scripts/host/debug/` 新增 `DungeonMap2DView`（逐格 `DrawRect`，地面色取自 `DungeonRoomStyleSet.FloorColor`，画图例与「起始→Boss 最短路」）、`DungeonMap2DPanel`（14 根滑条 + 随机种子 + 导出字符画，90 ms 节流）、`DungeonMap2DDebugScene`、`scenes/debug/DungeonMap2D.tscn`。③ **修掉 4 个真缺陷**：**坐标空间混淆**（布局坐标 vs 画布坐标，曾导致一批假报错，现一律走 `ToMapX/ToMapZ/RoomCenter/DoorCell`）；**隐藏房隔离检查自证为真**（用含秘密通道的泛洪判隔离 ⇒ 必然可达，100 图误报 37~58 张，改为用不含通道的泛洪判隔离 + 含通道的泛洪判可达）；**秘密通道挖不出来**（规划器原来自己写了一套 BFS，与 `CarveSecretPassage` 的过滤条件不一致；改为**拿真实 builder 干跑**同一份实现，并补上「主图走廊 + 门」两个漏项）；**隐藏房摆位无解**（宿主房逐个退让重试，规划失败率 46/1000 → 0/1000）。④ **性能实测**：重算一次平均 0.51 ms、稳态最差 3.25 ms，远低于一帧 16.6 ms，故「拖滑条实时重画」成立。⑤ **验收**：`--map --seeds 1000` → 房间图 1000/1000、连通性不合格 0/1000；`--seeds 10000` → 9997/10000、**连通性不合格 0/9997** | `docs/architecture/ARCHITECTURE.md`、`scripts/spawn/rooms/{DungeonMap,DungeonMapBuilder,DungeonMapConfig,DungeonMapCheck}.cs`、`scripts/spawn/rooms/BranchDungeonPlanner.cs`、`scripts/spawn/rooms/DungeonValidation.cs`、`scripts/host/debug/**`、`scenes/debug/DungeonMap2D.tscn`、`tools/dungeon_check/**` |
| 2026-11-05 | v4 | **角色抽象（Character）落地** | **新增 §15 角色抽象**：角色 = 「数值包组合 + 技能包 + 阵营/控制标签」的 ECS 实体，玩家与怪物**同一组件集合**，差异只在数据。① **数值**：`StatId` 追加 法力(1000 段)/护甲(1003 段)/力量·敏捷·幸运(1010 段)，五项基础资源 Hp/Mana/Armor + 三项属性 + 三项资源回复；`StatPackageRepository.Compose` 严格按规则6 折叠——Base 层**绝对值**、其余四层**差量叠加**，`GetLayerValue` 保留单层读取。② **技能**：`SkillSlot` 五个固定槽位（Dash=Shift / PrimaryAttack=左键 / WeaponSwap=Q / Signature=R / Interact=F），`SkillLoadout` 定长表按槽存技能并各自计冷却；`SkillCastSystem` 每帧只消费**一个**槽（意图优先级由 `IntentRules` 决定），门控顺序 `CanCast → Cast → 扣法力 → 起冷却`。③ **组合**：`ActorSpawnRequest(DefinitionSlot, Position, TeamOverride?, ControlOverride?)` 是唯一的造人入口——**夺舍 = 改 `ControlOverride`/`TeamOverride` 两个字段**，不需要任何新类型；`ActorIdentity` 只存槽位号（J2），不存引用。④ **策划可改**：`data/excel/characters.xlsx`（Characters / StatPacks / SkillLoads 三表，四行表头规范）为唯一真源；`CharacterContentSet` 提供与表逐项一致的运行时副本兜底，`CharacterContentRegressionTests` 用 9 条用例把「表 ↔ 运行时」钉死（每个角色都必须能真装配出来、玩家技能包五槽齐全、怪物与玩家同机制）。⑤ **引擎边界**：`PlayerInputIntentSource`（`scripts/host/control/`）把 InputMap action 翻成 `InputIntent`（Q16 定点方向），是唯一碰 `Input` 的地方；技能逻辑只认意图，故 AI/回放/玩家共用同一条代码路径。**测试 101 全绿**（新增 25 例） | `docs/architecture/ARCHITECTURE.md`、`scripts/core/{ActorKind,SkillSlot,StatId}.cs`、`scripts/core/{Actors,Skills}/**`、`scripts/actor/{ActorComponents,ActorRegistries,ActorIntentSystem,ActorAssemblySystem}.cs`、`scripts/skill/SkillCastSystem.cs`、`scripts/data/{StatPackageRepository,CharacterContentSet}.cs`、`scripts/host/control/**`、`scripts/host/DungeonDebugScene.cs`、`project.godot`、`data/excel/characters.xlsx`、`tools/data_pipeline/gen_characters_xlsx.py`、`tests/Characters/**` |
| 2026-11-04 | v3 | **房间与地牢系统（场景抽象）**，另含 4 处基础设施修复 | **新增 §14 房间与地牢**：四层切分（L1 值词汇 / L2 规划 / L3 生成 / L4 表现）、`IDungeonPlanner.Plan(config,seed,generation) → DungeonLayout` 的「一次掷骰、全链只读」模型、`RoomCategory` 七类（Start/Combat/Special/Hidden/Prototype/Boss/Safe）、6 条可执行不变式、与 ECS 的边界（房间不产生实体、L3 只走 `CommandBuffer`、`World→Node` 单向）。**落地**：`scripts/spawn/rooms/`（契约 + `BranchDungeonPlanner` + `GridContentPlanner` + `DefaultRoomContentSpawner` + `DungeonValidation`）、`scripts/host/Dungeon*`（`DungeonOptions`/`DungeonSceneBuilder`/`DungeonRoomView`/`DungeonDecorCatalog`/`DungeonDebugScene`）、`scenes/main/DungeonDebug.tscn`、`tools/dungeon_check/`（200-seed 性质校验）、`tools/decor_catalog/`（素材→装饰目录）。**基础设施修复**：① `BiuBiuOrigin.csproj` 的 `DefaultItemExcludes` 新增 `**/obj/**;**/bin/**;tools/**;tmp/**;build/**;out/**`——修掉「Godot.NET.Sdk 默认 glob 把 `.godot/mono/temp/obj/**/*.AssemblyInfo.cs` 当源码编进来」导致的 CS0579 自举式重复（本轮最贵的坑）；② `LifetimeSystem` 与 `VisualBridge` 把 `Query<Entity, T>` 改为 `Query<T>`、实体号改从组件 `[Identity] Owner` 取——`Entity` 是句柄不是组件列（J1），旧写法会让 `Register<Entity>()` 抛异常且 `SystemBase` 留在「半死」状态；③ `config/architecture-rules.json` 移除无源头的 `tools/decor_catalog/**` 放行、`.gd` 探针统一收进 `tools/probes/`；④ `tools/probes/` 目录此前只存在于白名单、实际未创建，本次补建。**未改架构**：GS005 清理（39 条 → 0，做法是把真·表现数值收进 `DungeonOptions`、把几何常量改成语义推导，而非加注释糊过去） | `docs/architecture/ARCHITECTURE.md`、`scripts/spawn/rooms/**`、`scripts/host/**`、`scripts/actor/LifetimeSystem.cs`、`scripts/host/bridge/VisualBridge.cs`、`scenes/main/DungeonDebug.tscn`、`BiuBiuOrigin.csproj`、`config/architecture-rules.json`、`config/decor_catalog.json`、`tools/dungeon_check/**`、`tools/decor_catalog/**`、`tools/probes/**`、`tests/Rooms/**`、`docs/artifacts/decor_catalog.md` |
| 2026-11-03 | v1 | 立规矩（规则体系建立） | 建立 7 条开发规矩与架构骨架：C# 主逻辑（`Game.*` 七层命名空间）、C++ GDExtension 于 `native/`、UI 层允许 GDScript、Excel 为数值唯一真源、架构守卫 GS001~GS007、完成定义 6 步检查表；本节所有「已落地」项均为空 | `docs/architecture/ARCHITECTURE.md`、`docs/rules/01-gdscript-ban.md`、`docs/rules/02-tech-selection.md`、`docs/rules/03-abstract-first.md`、`docs/rules/04-data-driven.md`、`docs/rules/05-numeric-package.md`、`docs/rules/06-extensibility.md`、`docs/rules/07-definition-of-done.md`、`tools/check_architecture.ps1`、`tools/env.ps1`、`docs/prompts/task-prompt-template.md`、`.gitignore`、`AGENTS.md` |
| 2026-11-03 | v1 | 立规矩（仲裁收口，本日第二次更新） | 收口仲裁：`IVisualDescriptor` 由候选转**正式接口**；`ISkill`/`ISkillTarget` 声明**下沉 `Game.Data`**、实现留 `scripts/skill/`；`IActor` 增加 `TryApplyTakeover(in TakeoverSpec) : bool`；§3.2 新增「声明位置 / 实现位置」两列；§4 统一 Excel **按主题分文件**口径（characters/combat/presentation/progression，Sheet 名=表名）；§9.4 标注为性能阈值**唯一真源**；文件头恢复常规排版、变更日志恢复 §11 编号 | `docs/architecture/ARCHITECTURE.md`、`docs/rules/03-abstract-first.md`、`docs/rules/04-data-driven.md`、`docs/rules/05-numeric-package.md`、`docs/rules/06-extensibility.md`、`docs/rules/07-definition-of-done.md` |
| 2026-11-03 | v1 | 立规矩（第四轮收口，本日第四次更新） | **`ISkillCaster` 悬空接口关闭**：§3.2 行更新为 03 §4.1 定稿签名（新增 `CanCast` 预检、`TickSkill(id,dt)`→批量 `TickSkills(dt)`、新增 `TryReplaceSkills` 受控通路），状态由「v1 待正式定义」改为「✅ 签名已定稿」；原「⚠️ 已知缺口」小节改写为「归属已收口」并补**两通路纪律**（`TryApplyTakeover` 内部改技能 = 夺舍专用；`TryReplaceSkills` 非夺舍、夺舍中返回 false）；03 §4.1.1 待仲裁项全部改标 ✅、03 补 `ISkillCaster` 为第 15 个正式接口、05 §8 补 `IStatProvider` 实现位置注释、06 修 2 处省略式 `ApplySlot` 槽位写法；06 §9.2 开放问题 OQ-1~OQ-9 **全部关闭** | `docs/architecture/ARCHITECTURE.md`、`docs/rules/03-abstract-first.md`、`docs/rules/05-numeric-package.md`、`docs/rules/06-extensibility.md` |
| 2026-11-03 | v2 | ECS 权重骨架落地 | **运行时架构由「纸面」转为「已实现」**：`scripts/core/ecs/`（内核 14 文件）、`scripts/host/`（七阶段管线 + 视图池 + 事件队列）、`scripts/actor`+`scripts/spawn`（3 个系统）、`tests/`（63 用例全绿）落地；§0 阶段改为 v2、§1.1 现状树重写、§2.3 矩阵新增 `Game.Host`/`Game.Core.Ecs` 两行并写明「Host 是唯一 using Godot 的游戏层」硬约束、§3.2.0 新增 ECS 类型清单、§6.1 现状更新 + 新增 §6.1.1「七步逻辑如何映射到 ECS 阶段」、§10 关闭 R1/R2/R3、R11 部分关闭、新增 R13~R15、**新增 §13 ECS 落地汇总**（含 4 条真炸过的缺陷复盘与填充顺序）；形状真源仍是 `docs/architecture/ECS.md` | `docs/architecture/ARCHITECTURE.md`、`docs/architecture/ECS.md`、`AGENTS.md` |
| 2026-11-05 | v7 | **第一个可玩人物 `test_hero` 落地** | **在 ECS + 角色抽象之上做出第一个真正可操作的人物**：新增 §16 记录全链路（Excel 数据行 → `CharacterContentSet` 镜像 → `ContentSkillSet` 实例化真技能 → `CharacterSession` 一条管线 → `VisualBridge`/`TransformBridge` → HUD 快照）。① **组合根**：新增 `scripts/host/composition/`（`ISkillFactory` / `NullSkillFactory` / `NullSkill` / `ContentSkillFactory` / `ContentSkillSet`），刻意不 `using Godot`，测试工程直接编译 —— 「数据行 kind → 技能实现类」的唯一定义点。② **人物会话**：新增 `scripts/host/CharacterSession.cs`，把「出生 + 意图 + 装配 + 技能 + 移动 + 瞄准 + 视图 + HUD」收成一个可 `Tick` 的单元；`DungeonDebugScene` 改为持有会话并支持 `--hero` / `--hero-variant=` / `--hero-fire` / 角色特写截图。③ **四个变体纯数据**：`test_hero`(标准) / `test_hero_tank`(重甲) / `test_hero_agile`(敏捷) / `test_hero_lucky`(幸运)，换变体只改 xlsx 一行，C# 0 行改动。④ **修复四个静默失效的接线**（详见 §16.4）：玩家实体发现死循环（人物不出生）、技能包注册表槽位号错位（按左键永不开枪）、`DataSkill` 占位不生成子弹、意图覆盖方向写错。⑤ **HUD**：`HudSnapshot`/`CharacterHudDrawer` 显示 HP/法力/耐力/护甲减伤/移速/暴击/瞄准角/五技能槽；护甲减伤口径修正为 `armor/(armor+k)`（曾显示 94%）。⑥ **测试 101 → 219 全绿**（新增 `HeroIntegrationTests` 端到端用例：按下左键 → 世界里真出现 `Projectile` 且弹速/射程/伤害/半径全部可追溯到武器行；新增 `RuntimeSkillRegistry_KeepsContentSlotNumbers` 钉住槽位号一致性，并做了变异验证确认它能抓到原 bug），架构守卫 0 error / 0 warning。⑦ ECS 内核：`TypeSet.MaxComponentCount` 16 → 64（人物 12 类组件超出原上限导致运行时崩溃）。 | \scripts/host/CharacterSession.cs\、\scripts/host/composition/**\、\scripts/host/DungeonDebugScene.cs\、\scripts/host/control/PlayerInputIntentSource.cs\、\scripts/ui/**\、\	ests/**\、\docs/architecture/ARCHITECTURE.md\ | `scripts/host/CharacterSession.cs`、`scripts/host/composition/**`、`scripts/host/DungeonDebugScene.cs`、`scripts/host/control/PlayerInputIntentSource.cs`、`scripts/host/view/**`、`scripts/host/bridge/**`、`scripts/ui/**`、`scripts/actor/**`、`scripts/combat/**`、`scripts/skill/**`、`scripts/data/**`、`scripts/core/ecs/TypeSet.cs`、`tests/**`、`docs/architecture/ARCHITECTURE.md`、`data/excel/characters.xlsx` |
| 2026-11-06 | v11 | **本地化：中文名真源 `l10n.xlsx`（图鉴全中文）** | **用户打开图鉴「全是英文」**——根因不是图鉴，而是**项目里从来没有中文名**：`DisplayKey` 明文规定不写中文（它是本地化 key），全仓库搜 `*loc*`/`*i18n*`/`*.csv` 零命中。**新增 §23**：① **`data/excel/l10n.xlsx` 作为名字的唯一真源**（167 条：物件 37 / 标签 62 / 枚举 55 / 单位 7 / 人物武器 5），四行表头 + `#data` 与其它主题表一致；**图鉴与将来的游戏 UI 读同一份**，杜绝「图鉴叫飞弹、游戏里叫炮弹」。② **图鉴中文名做主标题、英文 id 做副标题**（等宽小字，对表用），搜索索引收录中英文与标签三条路径。③ **校验器新增 5 类检查**（`validate_l10n` + `collect_l10n_refs`）：Key 重复 / Zh 为空 / **覆盖性（被引用的 key 必须有名字）** / 中文名仍是英文 / **同命名空间内重名**——检查项 4136 → **4569**。分命名空间是关键：`Pierce` 与 `tag.pierce` 都叫「穿透」是对的，第一版跨桶比较产生 12 条假阳性，淹没了真问题（`tag.melee` 与 `tag.short_range` 都叫「近战」）。④ **顺带修掉 DisplayKey 不一致的真 bug**：`player_hero` 在 `combat.xlsx` 是 `actor.player`、在 `characters.xlsx` 是 `actor.player_hero`；规范定为 `actor.<Id>`，已改齐。⑤ 图鉴加稀有度/元素色条、冲刺未配置时明确显示「未配置（不能冲刺）」而不是留白。**测试 372/372、构建 0/0、校验 4569 全绿、图鉴 0 条缺名警告**。**开工注意**：加任何新内容（人物/怪物/武器/子弹/标签）必须同步在 `l10n.xlsx` 加一行中文名，忘了校验器会报「缺少中文名：xxx（被 Yyy 引用）」 | `data/excel/l10n.xlsx`、`data/excel/combat.xlsx`、`tools/data_pipeline/gen_l10n_xlsx.py`、`tools/data_pipeline/add_missing_l10n.py`、`tools/data_pipeline/gen_codex.py`、`tools/data_pipeline/validate_content_xlsx.py`、`docs/codex/index.html`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-06 | v10 | **核心内容集：5 人物 / 5 怪物 / 5 武器 / 5 子弹 + 图鉴原型** | **补齐用户最初的「5+5+5+5」需求**（此前怪物只有 3 种、人物 4 行是同一模板的数值变体）。**没有新增任何类**——五个人的差异只有三处：① 能力位（Objects.Tags）② 数值包（StatPacks）③ 技能包→武器/子弹（SkillLoads.WeaponId），系统代码 0 行改动。人物补齐第 5 个 **	est_hero_elemental 元素法师**（70 血 / 180 蓝 / 追踪穿透分裂弹），与既有 4 个在「血/蓝/速/甲/暴击/冲刺手感」六维上两两反向，无严格上位关系。怪物补齐到 5 种（新增 slime_401 分裂、slime_403 爆散，走与人物**同一套**数值包+技能包路径，差别只在填了几个槽）。新增 5 把人物武器 + 5 条弹道（直飞/重击/穿透 2/弹跳 3/追踪穿透分裂），子弹物件首次真实使用标识层 Faction 轴（	ag.own_player）。**新增图鉴原型** 	ools/data_pipeline/gen_codex.py → docs/codex/index.html（直读两个 xlsx，按标签轴分栏配色，兼作数值快速预览与游戏内图鉴的信息层级原型）。新增幂等填充脚本 ill_core_roster.py。**6 处坑写进 §22.9**（_upsert 必须自写主键、同 PackId 不能跨 Base/Growth 层、规则值列空≠0、VisualSlot 必填整数等）。测试 358 → **372 全绿**，校验器 3540 → **4126 项通过**。| data/excel/combat.xlsx、data/excel/characters.xlsx、scripts/data/CharacterContentSet.cs、	ools/data_pipeline/fill_core_roster.py、	ools/data_pipeline/gen_codex.py、	ools/data_pipeline/validate_content_xlsx.py、	ests/Characters/HeroAssemblyTests.cs、docs/codex/index.html、docs/architecture/ARCHITECTURE.md |
## 12. 待办与下一步（按优先级）

| 优先级 | 待办 | 产出位置 | 完工时必须回写本文件的章节 |
|---|---|---|---|
| **P0** | 建目录骨架 + `.gitkeep`；首次 `dotnet build` 生成 `BiuBiuOrigin.csproj` | `scripts/**`、`scenes/`、`data/`、`shaders/`、`tests/` | 1.2、1.3、11、12 |
| **P0** | 落 `Game.Core` 最小件：`IRandomProvider` + `IPoolable` + 事件类型骨架 | `scripts/core/` | 3.2、6.5、11 |
| **P0** | 数据管线 MVP：`characters.xlsx`(Characters/Status) + `combat.xlsx`(Weapons/Projectiles) + `presentation.xlsx`(Effects) → `.tres` + index，含校验点 ①~⑤ | `tools/data_pipeline/`、`data/excel/`、`data/generated/` | 4.2、4.3、4.9、10(R7)、11 |
| **P0** | 落 `CharacterDef/WeaponDef/ProjectileDef/EffectDef` 四个 `Resource` + `IConfigSource` | `scripts/data/` | 3.2、4.2、11 |
| **P1** | 战斗最小闭环：靶子 + 子弹 + `IDamageable`/`IAttacker`/`IDamageModifier` | `scripts/combat/`、`scripts/actor/` | 3.2、6.2、11 |
| **P1** | 数值系统：`StatId` + `StatPackage` + `StatCalculator`（公式读 `progression.xlsx` 的 `Formula` Sheet） | `scripts/data/`（`Game.Data`） | 3.2、5.2、5.3、11 |
| **P1** | 打击感反馈：HitStop + 白闪 + 击退（参数全部走 `EffectDef`） | `scripts/actor/`、`shaders/` | 6.4、7.3、11 |
| **P1** | 对象池（`IPoolable`）+ 抛射物池化 | `scripts/core/` | 3.2、9.4、11 |
| **P1** | **`IVisualDescriptor` 落地**（外观描述符 + `ApplySlot`），为「夺舍」换皮铺路 | `scripts/actor/`、`scripts/data/` | 3.2、8、11 |
| **P2** | Actor 系统：`IActor` + `IMovementDriver`（玩家/AI/击退三驱动）+ **`IActor.TryApplyTakeover`** | `scripts/actor/` | 3.2、11 |
| **P2** | 技能系统：`ISkill`/`ISkillTarget`（声明在 `Game.Data`、实现在 `scripts/skill/`）+ 第一个技能；`ISkillCaster` 签名已定稿（03 §4.1），开工直接实现即可 | `scripts/skill/`、`scripts/data/` | 3.2、6.3、11 |
| **P2** | 肉鸽骨架：种子、房间、波次（`Game.Spawn`） | `scripts/spawn/`、`progression.xlsx` 的 `Waves` Sheet | 4.7、11 |
| **P2** | **房间/地牢：门状态机（封锁/清场开锁/单向）落在新 `SystemBase` + `RoomState` 组件；多房间流式加载** | `scripts/spawn/`、`scripts/host/` | **14.7**、4.7、11 |
| **P2** | UI 骨架 + HUD（仅依赖只读接口） | `scripts/ui/`、`scenes/ui/` | 8.4、8.6、11 |
| **P2** | **「夺舍」验证任务**：走完 5 步（取 Base 包 + 取 ISkill 列表 + 换 IVisualDescriptor），验证规则6/7 设计成立 | `scripts/skill/`、`scripts/actor/`、`combat.xlsx` 的 `Skills` Sheet | 3.2、5.2、11 |
| **P3** | 渲染基线：`toybox_lit.gdshader` + 环境预设 | `shaders/`、`scenes/` | 7.3、11 |
| **P3** | 描边 + 受击闪 shader | `shaders/` | 7.3、11 |
| **P3** | 第一个 `CompositorEffect` 后处理 | `native/` 或 C# 绑定 | 7.2、7.3、11 |
| **P3** | GDExtension 骨架（需先 `--dump-extension-api`） | `native/`、`addons/biu_biu_native/` | 7.2、10(R2/R3)、11 |
| **P3** | 编辑器可视化调参插件 | `addons/biu_biu_editor/` | 8.6、11 |
| **P3** | 性能基线场景 + 实测回填 9.5 | `tests/` 或 `scenes/debug/` | 9.5、11 |
| **P3** | 一键校验（守卫 + build + 测试）接 CI | `tools/` | 10(R11)、11 |
| **P3** | **`EntityDestroyed` 事件的生产端**：目前 `World.Destroy` / `CommandBuffer.Flush` 销毁实体时**不广播任何事件**，导致 `EventBus.EntityDestroyed` 全工程只有声明没有生产者，`VisualBridge.QueueRelease` 是死代码。宿主侧已用 `World.IsAlive` 复核兜底（`VisualBridge.ReapDead`），但那是权宜；正解是内核销毁成功时发一次事件，所有消费方（视图池、统计、存档）就不必各自扫存活状态 | `scripts/core/ecs/`（`World`/`CommandBuffer`/`EventBus`） | 10、11 |

---

## 13. ECS 运行时骨架（落地汇总，2026-11-03）

> **本章是 [ECS.md](ECS.md) 的「施工验收报告」**，回答三个问题：**哪些落地了、踩了什么坑、后面怎么填**。契约形状一律以 ECS.md 为准，本章只记事实与教训。

### 13.1 落地清单

| 层 | 目录 / 文件 | 内容 | 验证方式 |
|---|---|---|---|
| 基础件 | `scripts/core/{Events,Handles,Pool,Random}/` | `EventBus`（8 类事件）、`ActorHandle`/`Actors`、`ObjectPool<T>`、`DeterministicRandom` | `tests/Determinism/`、`tests/Allocation/` |
| **ECS 内核** | `scripts/core/ecs/` | `World`、`Archetype`、`Entity`、`Query`、`CommandBuffer`、`SystemBase`、`SystemPipeline`、`TypeSet`、`ComponentTypeRegistry`、11 个官方组件 | `tests/Kernel/`（生命周期、chunk、管线、命令） |
| **宿主** | `scripts/host/` | `FixedFramePipeline`（七阶段）、`WorldHostOptions`、`HostEventQueue`、`CommandFlushSystem`、`ViewRegistry`、`TransformBridge`、`VisualBridge` | 编译 + 冒烟场景 |
| 系统族 | `scripts/actor/`、`scripts/spawn/` | `MovementSystem`（FixedUpdate 积分）、`LifetimeSystem`（到期投递销毁命令）、`SpawnDirectorSystem`（按配置生成） | 编译 + 冒烟场景 |
| 测试 | `tests/` | 63 用例：实体生命周期、chunk/管线、命令缓冲、分层约束、确定性、零分配 | `dotnet test` → **63/63 通过** |
| 工具 | `tools/check_all.ps1` | build → 守卫 → test 一键 | 退出码 |
| Native | `native/` | CMake + godot-cpp master + `EcsBatchLayout` 头 + stub | 产出 `addons/biu_biu_native/*.dll` |

**当前验证状态（2026-11-03 实测）**：

```text
dotnet build                              → 0 错误 0 警告
dotnet test tests\BiuBiuOrigin.Tests...   → 63 通过 / 0 失败
powershell tools\check_architecture.ps1   → new=0 existing=0 errors=0 warnings=0, exit 0
```

### 13.2 一帧七阶段（唯一实现处）

`scripts/host/FixedFramePipeline.Tick()` 是**阶段顺序的唯一落地处**，任何调整都是架构变更：

```text
PreUpdate → FixedUpdate×N → Update → LateUpdate → CommandFlush → 事件派发 → 视图桥
```

| 阶段 | 干什么 | 为什么必须在这个位置 |
|---|---|---|
| `PreUpdate` | 输入采集、随机子流推进 | 采集必须在所有决策之前；且不产生结构化变更 |
| `FixedUpdate ×N` | 位移积分、命中检测 | 吃**恒定 dt**（1/`FixedHz`），是「同 seed 同结果」的唯一来源 |
| `Update` | 计时、冷却、结算、投递生成/销毁命令 | 每帧一次；与固定步解耦 |
| `LateUpdate` | 数值同步、表现只读数据采集 | 在结算之后、提交之前，能读到本帧最终逻辑值 |
| `CommandFlush` | **本帧唯一允许结构化变更的时刻**（J5） | 所有 Span 已释放，此时迁移/新建 chunk 不会破坏遍历 |
| 事件派发 | `HostEventQueue.DispatchAll(EventBus)` | 世界结构已稳定，订阅方看到的是**一致快照** |
| 视图桥 | `VisualBridge` → `TransformBridge` | 只读 ECS 数据写 `Node3D`，不回写逻辑 |

> **`FixedUpdate` 不吃引擎 `_PhysicsProcess` 的 delta**：引擎物理步长由全局设置决定，改它等于改玩法，且回调时机不可控。宿主自持累积器，`_maxStepsPerFrame` 兜底防「补帧死亡螺旋」，超出的时间直接丢弃。

### 13.3 数字从哪里来（数据驱动的过渡态）

`WorldHostOptions`（`[GlobalClass] Resource`）承载 **13 个 `[Export]` 可调数值**：`FixedHz`、`MaxFixedStepsPerFrame`、`TimeScale`、`CullingDistance`、`ViewPrewarm`、`ProjectileLifetimeSeconds`、`ProjectileSpeedMin/Max`、`SpawnRadius/Height`、`ProjectileScale`、`StatsIntervalFrames`、`MinFramesToPass`。

**为什么这是合规的**：规则4 允许「文件 / Excel / 插件可视化修改」三条路径。Excel 未落地期间，`.tres` 资源文件 + Inspector 可视调参 = 「文件 + 可视化」两条都满足。系统侧只接收 `SpawnSettings` 这个**普通 C# 值对象**（`scripts/spawn/SpawnSettings.cs`），**不认识 `Resource`** —— 所以 Excel 落地后，只需在 `DataLayer` 里把 `SpawnSettings` 从 Excel 行填出来，**`SpawnDirectorSystem.cs` 一个字都不用改**（规则7：0 个文件改动）。

> 结构性常量（`Math.PI * 2`、浮点 eps、数组预分配容量）不属于玩法数值，已按守卫要求在行尾标注 `// arch-ok: <理由>`，共 26 处；覆盖后守卫 0 warning。

### 13.4 组件与系统的当前清单

**11 个官方组件**（`scripts/core/ecs/components/OfficialComponents.cs`，全部 `unmanaged struct` + 首个字段 `[Identity] Entity Owner`）：
`Transform3`、`Velocity`、`Team`、`Lifetime`、`Health`、`StatLayerSlots`、`Rigid`、`Projectile`、`TakeoverState`、`View`、`Hurtbox`

**3 个已落地系统**：

| 系统 | 组 | 职责 | 关键纪律 |
|---|---|---|---|
| `MovementSystem` | FixedUpdate | `Position += Linear * dt` | 只做线速度；转向交给别的系统 |
| `LifetimeSystem` | Update | `Remaining -= dt`，到期 → **投递** `Destroy` 命令 | 两遍扫描：先减法收进旁路数组，遍历结束后才投递命令（J5） |
| `SpawnDirectorSystem` | Update | 按 `TargetAliveCount` 每帧补满，走 `CreateDeferred` + 逐个 `Set` | 用 `_random.Fork(SpawnSalt)` 拿独立子流，别人多调一次随机不影响它 |

**新增一种实体 = 1 个文件**（在 `ViewModel.Components(world)` 里加一行静态列表 + 写它的生成函数）。

### 13.5 踩坑记录（**后面填内容前必读，这四条都是真炸过的**）

内核实现过程中暴露的四个真实缺陷，全部已修并有测试守护。**它们都属于「看起来能跑、结果是错的」类型**，所以必须写下来：

1. **原型签名碰撞（最严重）**：`TypeSet` 的签名折叠原本是 `hash = hash*31 + id` 且从 0 起算 → 单元素集合 `{k}` 折叠结果恒等于 `k`，于是 `{Pos}` 与 `{Vel}` 撞成同一个原型，实体被塞进**布局错误的 chunk**。修法：签名改为「先混入长度 + 黄金比例种子，再逐项乘黄金比例折叠」，并让 `TypeSet._hash` 与 `ComputeSignature` **共用同一函数**（两处不一致会导致「存进去」和「查出来」分叉）。
2. **命令值错位一位**：`CommandValueQueue.WriteInto` 原本只认 `logicalIndex == 0`（永远取队首），而 `Set<T>` 记录的是入队时的绝对下标 → 同一组件连续 Set 三条时后两条被静默丢弃。改为「按队首消费」；并且**命令被跳过时必须调 `SkipHead()`** 消费掉那个值，否则后续同组件命令整体错位。
3. **预留实体被误判为「已生成」**：`ReserveEntity` 占槽位后 `IsAlive` 即为 true，导致 `ExecuteDeferredCreate` 第一行就把它当重复命令跳过，**整批 `CreateDeferred` 全部失效**。修法：「已生成」的判据改为 `ArchetypeIndex != NoneArchetype`（是否已落进原型），而不是 `IsAlive`。
4. **`.csproj` glob 吞掉 MSBuild 产物**：`Godot.NET.Sdk` 的默认 `**/*.cs` 会收进 `.godot/mono/temp/obj/**/*.AssemblyInfo.cs`，报 8 条 CS0579「特性重复」。且 **`<Compile Remove>` 放在 Project 级 `ItemGroup` 里不生效**（SDK 在 target 阶段才加默认项）——必须用 `DefaultItemExcludes` 在求值阶段剔除，并在 target 阶段 `BeforeTargets="CoreCompile"` 再兜底一次。

> **方法论教训**：以上四条全部是**测试先红、人后修**。ECS 内核这种「内存布局 + 生命周期」密集的代码，**没有测试就等于没有实现** —— 63 个用例里每一个都至少在某个时刻抓到过一个真 bug。

### 13.6 下一步（填充顺序建议）

1. **`scripts/combat/`**：按 §6.1.1 的映射表落 `HitDetectionSystem`（FixedUpdate）+ `DamageResolveSystem`（Update）。这一步会首次用到 `Hurtbox`、`Health`、`Projectile` 三个组件。
2. **`scripts/data/` + `data/excel/`**：把 `WorldHostOptions` 的 13 个数值迁到 Excel，`SpawnSettings` 改由 `DataLayer` 填充 —— **这是验证「规则4 + 规则7 是否真的成立」的关键一步**；如果那时发现要改系统代码，说明这层的抽象没做对，要回来重做。
3. **`scripts/skill/`**：`ISkillCaster` 签名已定稿，可直接实现。
4. **性能实测**：跑满 §9.3 的 500 抛射物，回填 §9.5；不达标再考虑 `ForEachJob` 或 `IEcsBatchSolver` 下沉 C++（R15）。

---

## 14. 房间与地牢（场景抽象，2026-11-04 落地）

> **给下一个接手的人：** 这一节回答「场景长什么样、怎么随机、谁负责哪一段」。形状真源在代码里：
> `scripts/spawn/rooms/`（规划，零引擎依赖）+ `scripts/host/Dungeon*`（表现，唯一 `using Godot` 处）。
> 下面每条都写了「为什么这么切」，改之前先读理由。

### 14.1 现状（一句话）

**地牢 = 一张由纯值数据描述的图**。`IDungeonPlanner.Plan(config, seed, generation)` 产出
`DungeonLayout{ Rooms, Links, Doors, Start, Boss, Entrance }`，之后所有环节（校验、内容槽、装饰、ECS 生成、建场景）
都**只读这张图**、不再掷骰。同 seed → 逐字节相同的一张图（可回放、可复现 bug）。

```text
       DungeonConfig(纯值)          IRandomProvider(seed)
             │                            │
             ▼                            ▼
   ┌──────────────────┐         ┌─────────────────────┐
   │ IDungeonPlanner  │◀────────│ Fork(salt) 子流隔离 │
   └────────┬─────────┘         └─────────────────────┘
            ▼  DungeonLayout（不可变：房间/链路/门/锚点/深度/关键路径）
   ┌──────────────────┐
   │ DungeonValidation│  6 条不变式：不重叠 / 1起点1Boss / 门方向 / 主图连通 / 隐藏房只走密道 / 深度一致
   └────────┬─────────┘
            ▼
   ┌──────────────────┐      ┌──────────────────┐      ┌───────────────────┐
   │IRoomContentPlanner│────▶│  ContentSlot[]   │────▶│IRoomContentSpawner│──▶ CommandBuffer（J5）
   │（槽位：怪/道具/Boss/│      │（圆-圆不重叠）    │      │（按 RoomCategory） │      │
   │  玩家出生点）      │      └──────────────────┘      └───────────────────┘      ▼ 帧末 Flush
   └──────────────────┘                                                                 │
   ┌──────────────────┐      ┌──────────────────┐                                       │
   │ IRoomDecorLayout │────▶│ DecorPlacement[] │────▶ DungeonSceneBuilder ──▶ Node3D 树（Host 层，单向）
   └──────────────────┘      └──────────────────┘         （地面/墙/门洞/glb 装饰）
```

### 14.2 抽象层次（为什么分四层，而不是一个大 `LevelGenerator`）

| 层 | 类型 | 输入 → 输出 | 依赖 | 可测方式 | 规则7：新增什么要改几个文件 |
|---|---|---|---|---|---|
| **L1 值词汇** | `RoomCategory` / `RoomDirection` / `DoorKind` / `RoomId` / `GridOffset` / `RoomPlan` / `RoomLink` / `RoomDoor` / `DungeonLayout` | — | 零依赖（`unmanaged` 优先） | 编译期 | 新枚举值 = 1 文件（并同步 §14.3） |
| **L2 规划** | `IDungeonPlanner` / `IRoomContentPlanner` / `IRoomDecorLayout` | `DungeonConfig` + seed → 不可变数据 | `Game.Core`（随机源）+ L1 | **裸 .NET**（`tests/Rooms/`） | **新拓扑风格 = 1 文件**（实现 `IDungeonPlanner`）+ 装配 1 行 |
| **L3 生成** | `IRoomContentSpawner` + `RoomBuildContext` | 槽位 → `CommandBuffer` 命令 | L1 + `Game.Core.Ecs` | 直接用 `World` 跑 | **新房间内容 = 1 文件**（实现 `IRoomContentSpawner`）+ 装配 1 行 |
| **L4 表现** | `DungeonSceneBuilder` / `DungeonRoomView`（`scripts/host/`） | `DungeonLayout` + 槽位 → `Node3D` | 唯一允许 `using Godot` | Godot headless 跑 + 截图 | 新房型外观 = 数据（材质/模型表）0 代码 |

**为什么必须切在「L2 结束」**：随机的正确性只取决于 L1/L2；把它们做成纯函数后，
「跑 200 个 seed 全部通过 6 条不变式」这种性质测试才可能在毫秒级里做完（做法见 `tests/Rooms/`）。
一旦让规划器直接建节点，随机就成了「只能肉眼看的表演」。

**为什么不把房间做成 ECS 实体**：房间的生命周期是「一层」级别且整层不变，与逐帧实体是两个量级。
混进 `World` 会让 `Query` 多出几十个不参与每帧遍历的原型（J6 查询形状变脏），
且房间图需要「不可变、可序列化、可裸测」——这些都不是 ECS 组件的强项。房间里的**内容**
（怪、可破坏物、道具）仍然是 ECS 实体，由 L3 生成。

### 14.3 房间类型（用户点名的四类 + 三档扩展位）

| `RoomCategory` | 语义 | 拓扑约束 | 门规则 | v1 内容 |
|---|---|---|---|---|
| `Start` | 玩家出生点 | 恰好 1 个，在图中心附近 | 全部开放 | 1 个 `PlayerStart` 槽 + 少量刷怪槽 |
| `Combat` | 怪物房 | 随机池默认项 | 未清场时封锁 | 若干 `Spawn` 槽 |
| `Special` | 奖励/商店等特殊功能房 | ≥ `MinSpecialRooms`，**优先挂侧枝** | 开放 | `Prop` / `Interactable` / `Reward` 槽 |
| `Hidden` | 隐藏房 | 只能经 `DoorKind.Secret` 到达；`OnMainGraph=false` | 秘密通道不画门、不上地图 | `Reward` 槽 |
| `Prototype` | 原型测试房 | 数据里 `IncludeInRandom=false`，只能被显式指定 | 开放 | 可直出怪物/机关 |
| `Boss` | 关键路径终点 | 恰好 1 个，`Depth ≥ MinBossDepth` | 需清场 | 1 个 `BossSpawn`（房间最深处）+ 刷怪槽 |
| `Safe` | 安全房（存档点/教学） | 紧邻起点 | 默认开放 | 无怪 |

> **「新增一种房间」的正确做法**：如果是新的**拓扑职责** → 加一个 `RoomCategory` 值（1 文件 + 同步本表）；
> 如果只是同职责的新内容（新商店、新机关房）→ **加一行 `RoomCategorySpec`**（Archetype / 权重 / 是否进随机池），
> 规划器、生成器、建场景 **0 代码改动**。这正是规则7 表里「新关卡/波次 = 0 代码」的落地方式。

### 14.4 随机与「合理」的判据（不变式清单）

「合理」在这里**不是审美评价，是可执行的断言**；`DungeonValidation.Validate` 在规划完成后立刻跑，不过就抛中文异常（随机系统宁可当场炸）。

| # | 不变式 | 守护它的代码/测试 |
|---|---|---|
| 1 | 房间互不重叠、全部落在网格内 | `DungeonValidation` + 性质测试 |
| 2 | 恰好 1 起点、1 Boss；`Depth ≥ MinBossDepth` | 同上 |
| 3 | 每条链路两端贴边且朝向一致；每条链路正好 2 扇门 | `AdjacentLinkDirection` + `CheckDoors` |
| 4 | 主出生图从起点连通所有非隐藏房；`RoomPlan.Depth` == BFS 深度 | `CheckReachability` |
| 5 | 隐藏房只经 `IsSecret && !OnMainGraph` 的链路到达 | `CheckHiddenRooms` |
| 6 | 内容槽之间圆-圆不重叠，且离墙/离门有安全距离 | `GridContentPlanner` + 测试 |

确定性纪律：规划器内部只用注入的 `IRandomProvider`，且**每个子系统 Fork 自己的盐**（房间内容 / 装饰 / 门都各一条流）。
于是「给商店多掷一次随机」不会打乱「怪物编队」——这是同 seed 回放成立的必要条件。

### 14.5 与宿主的关系（ECS 边界）

- **房间不产生实体**。L3 只通过 `IRoomBuildContext.Commands`（= `World.Commands`）写生成命令，帧末 `CommandFlush` 落地（J5）。
- **节点不反写**。`DungeonSceneBuilder` 只读 `DungeonLayout`，`World → Node` 单向（ECS.md §7.1）。
- **数值进 Host 的两个入口**：`DungeonOptions : Resource`（可在 Inspector / `.tres` 调，规则4 的「可视化修改」路径）
  → `ToConfig()` → 纯 C# `DungeonConfig`。Excel 落地后，`ToConfig()` 改从 `progression.xlsx` 的 `Dungeon` Sheet 读，**规划器一行不改**。

### 14.6 扩展点登记（新增 §3.2 之外的房间域接口）

| 接口 | 职责 | 声明/实现位置 | 已落地? |
|---|---|---|---|
| `IDungeonPlanner` | 种子 → `DungeonLayout`（唯一随机处） | `Game.Spawn.Rooms` / `scripts/spawn/rooms/` | ✅ v1：`BranchDungeonPlanner` |
| `DungeonValidation` | 6 条不变式校验（静态类，非接口） | 同上 | ✅ |
| `IRoomContentPlanner` | 布局 → `ContentSlot[]` | 同上 | ✅ v1：`GridContentPlanner` |
| `IRoomContentSpawner` | 槽位 → 生成命令 | 同上 | ✅ v1：`DefaultRoomContentSpawner` |
| `IRoomDecorLayout` / `DecorCatalog` | 布局 → `DecorPlacement[]` | 同上 | ✅ 接口 + 目录；v1 实现最小 |
| `IRoomBuildContext` | 生成器能做的全部事（命令/随机/换算） | 同上 | ✅ `RoomBuildContext` |

### 14.7 约束与下一步

**约束**：① L2 不得 `using Godot`、不得 `new` 随机源；② L3 不得直接 `World.Create/Destroy`（J5）；③ 房间图不可变——需要「探索式增量生成」请另起类型，不要给 `DungeonLayout` 开可变开关；④ 所有尺寸/权重/数量进 `DungeonConfig` 或数据，代码里出现玩法数值 = 违规（GS005）。

**下一步**：① `progression.xlsx` 的 `Dungeon` Sheet 承载 `DungeonConfig` + 房型表，`DungeonOptions` 改为只读生成物；
② 门的状态机（封锁/清场开锁/单向）落在 `scripts/spawn/` 的新 `SystemBase`，房间运行时状态用组件（`RoomState{ RoomIndex, Cleared, Visited }`）表达；
③ 多房间流式加载（一次只建当前房 + 相邻房的视图，其余只留数据）——这是 100+ 房间时的必选项（§9 预算）。

### 14.8 2D 栅格地图层（2026-11-05 落地）：可目视、可验证的拓扑视图

> **为什么加这一层**：3D 白盒能走，但「哪里通、哪里堵」在 3D 里根本看不出来，
> 无法快速判断生成器是不是出了拓扑问题。于是把**同一份 `DungeonLayout`** 再投影成一张
> 俯视 2D 格图：**正方形 = 房间格，颜色 = 房型/地砖，方块 = 障碍**。它是「地图体检」的可视化，
> **不替代 3D**，也不碰 ECS —— 3D 仍是唯一玩法路径。

**四件落地物（都在 `scripts/spawn/rooms/` 与 `scripts/host/debug/`）**：

| 物 | 职责 | 位置 |
|---|---|---|
| `DungeonMap` | 只读栅格地图：`Kind[]`（格子类型）+ `RoomSalt[]`（属于哪间房）+ `DoorSlot[]`，char 化 `ToArt()/FromArt()` | `scripts/spawn/rooms/DungeonMap.cs` |
| `DungeonMapBuilder` | `DungeonLayout` → `DungeonMap`：铺地面 → 连走廊 → 打门 → **挖秘密通道** → 撒障碍 | `scripts/spawn/rooms/DungeonMapBuilder.cs` |
| `DungeonMapConfig` | 栅格化的可调参数（margin / 障碍密度 / 离墙格数…），全部 `With()` 派生，不可变 | `scripts/spawn/rooms/DungeonMapConfig.cs` |
| `DungeonMapCheck` | 体检：泛洪可达性、房间是否被障碍割裂、陷阱格、Boss 可达、**隐藏房隔离 + 经秘密通道可达** | `scripts/spawn/rooms/DungeonMapCheck.cs` |

**字符图例**（`ToArt()` 与 `tools/dungeon_check --map --gallery N` 共用）：

| 字符 | 含义 | 字符 | 含义 |
|---|---|---|---|
| `#` | 墙 | `+` | 门 |
| `.` | 普通地面格 | `,` | 走廊（房间之间的连接地面） |
| `X` | 障碍块 | `?` | 秘密通道 |
| `S` / `c` / `$` / `h` / `B` / `s` / `p` | 起始 / 战斗 / 特殊 / 隐藏 / Boss / 安全 / 原型 房的地面 | | |

**两个坐标空间（最容易踩的坑）**：`RoomPlan.Cell` 是**布局坐标**（可以是负数），
`DungeonMap` 是**画布坐标**（加过 `OriginX/OriginZ` 偏移）。**永远不要拿布局坐标直接当下标**，
一律走 `map.ToMapX/ToMapZ/RoomCenter/DoorCell`。这一条踩过：早期体检把布局坐标当画布下标，
导致「起始房跑到画布外」「房间被障碍割裂」「Boss 不可达」等一批假报错。

**隐藏房的两条不变量（必须同时成立，方向相反）**：

1. **隔离**：不挖通道时，从起始房泛洪**到不了**隐藏房（否则它就不是隐藏房）；
2. **可达**：算上秘密通道时，泛洪**到得了**隐藏房（否则它是死房间）。

这两条分别用**两次泛洪**判定（`includeSecretPassages` 为 false / true）。
**曾经的错误**：只用含通道的那次泛洪判隔离 —— 那样「必然可达」，这条检查等于没做，
100 个种子里有 37~58 张图被误报。**判隔离必须用不含通道的泛洪**。

**实时调参面板（需求原话「数值拖拉拽，实时拖拽查看地图生成结果」）**：

| 物 | 职责 | 位置 |
|---|---|---|
| `DungeonMap2DView` | `Node2D` 逐格 `DrawRect`，格子像素**按父容器尺寸自动铺满**（`AutoFit`，见 §7.5.2；滑条一动即接管）；地面色取自 `DungeonRoomStyleSet.Get(category).FloorColor`（换配色只改 `.tres`）；画图例、画「起始→Boss 最短路」高亮 | `scripts/host/debug/DungeonMap2DView.cs` |
| `DungeonMap2DPanel` | 20 根**可左右拖动的 `HSlider`**（地图形状 14 根 + 物件 6 根），每行「名字 + 滑块 + 当前值标签」；拖 30 ms 节流，松手立刻算 | `scripts/host/debug/DungeonMap2DPanel.cs` |
| `DungeonMap2DDebugScene` | 场景根：把 View 与 Panel 接起来；自检报告 `sliders=` / `spinBoxes=` 计数 | `scripts/host/debug/DungeonMap2DDebugScene.cs` |
| 场景 | `res://scenes/debug/DungeonMap2D.tscn`（左侧地图 / 右侧面板） | `scenes/debug/` |

**交互控件为什么是滑块而不是 SpinBox（2026-11-09 改）**：用户原话「数值改为左右拖动的滑块」。
`SpinBox` 只能点小箭头或键盘输入，调参时眼睛在图上、手要去找一个小箭头，频繁打断看图。
改用 `HSlider` + 旁边一个**定宽数值标签**（固定宽度，拖动时数字位数变化不会让整列跳动）。
节流同时从 90 ms 收到 **30 ms**：90 ms 是给「点箭头一次跳一格」定的，换成连续拖动后明显发黏；
实测重算一次平均 0.57 ms、稳态最差 8.92 ms，仍在一帧 16.6 ms 内，所以敢放宽到几乎每帧都能算。
自检里加 `sliders=` / `spinBoxes=` 两个计数并断言 `spinBoxes==0`——无头环境没有眼睛看面板，
「我写了 `AddSlider` 所以它一定是滑块」不算证据，数场景树里真实存在的节点类型才算。

**性能事实（本机实测，`dotnet run --project tools/dungeon_check -c Release -- --bench --seeds 500`）**：
重算一次 = 规划 + 栅格化 + 体检，**平均 0.51 ms，稳态最差 3.25 ms**，远低于一帧 16.6 ms
——所以「拖滑条实时重画」在性能上成立，不需要改成松手才算。

**验收证据（`tools/dungeon_check --map --seeds N`）**：

| 样本 | 房间图生成 | 2D 连通性不合格 |
|---|---|---|
| 1000 | 1000 / 1000 | 0 / 1000 |
| 10000 | 9997 / 10000 | **0 / 9997** |

#### 14.8.1 「工具全绿」≠「游戏能开」：一次真实回归（2026-11-05 修复）

这一节记录一个**必须记住的教训**，因为它差点让 3D 实机彻底开不了而测试全绿。

**现象**：2D 层全部通过（10000 种子 0 失败）之后，`scenes/main/DungeonDebug.tscn` 一加载就报

```text
ERROR: 地牢调试场景初始化失败：System.InvalidOperationException:
地牢生成失败：连续 8 次尝试都没能生成一张合法的图
（最近一次原因：隐藏房找不到「不与任何房间贴边」的位置）
```

**根因**：`DungeonOptions` 的默认值是 `GridWidth/Height = 8, RoomSide = 1`，而
`tools/dungeon_check` 用的是另一套 `9×24 / side=3`。「隐藏房必须四面留 1 格隔离带」
（`DungeonValidation.CheckHiddenRooms`）在 8×8/side=1 下**根本不成立**：
房间只有 1 格，塞进 7 间房后，剩不下任何「四面都有墙」的空位。工具测的配置从不会有这个问题，
于是它一路绿。

**修复**（两处，缺一不可）：

1. 把 `DungeonOptions` 的默认值放大到 `11×11 / side=2`——让默认配置能真的容下一间隐藏房。
2. `tools/check_all.ps1` 新增 **step 4b**：`dungeon_check --options`，用**3D 实机那套默认值**再跑一遍同样的属性测试。
   从此「check_all 全绿」才真的等于「游戏能开」。

**顺带修正的算法缺陷**：`BranchDungeonPlanner.TryPlaceHidden` 原先用配置里的 `RoomSide` 算候选位间距，
但起始房/Boss 房比普通房大，用 `RoomSide` 会让候选位落进宿主房里。改为按
`max(宿主房实际边长, RoomSide) + ring` 计算，两头都不会错。

> **通用判据**：任何「用配置 A 测、用配置 B 上线」的场景，都必须有一道校验跑 B。
> 这不是纪律问题，是**测试的覆盖面和产品实际走的路不是同一条**——这类 bug 靠人看代码是看不出来的。

### 14.9 物件随机生成（2026-11-06 落地）：建筑 / 奖励 / 可破坏 / 不可破坏

> **需求原话**：「开始往其中加入建筑，奖励，不/可破坏物体的随机生成」。
> 这一层接在 2D 栅格之后、3D 实体之前：**先决定「什么摆在哪个格」，再由表现层决定「长什么样」**。

**四类物件 = 四种放置规则**（`scripts/spawn/rooms/DungeonProp.cs`）：

| 类型 | 占地 | 挡路 | 放置规则要点 |
|---|---|---|---|
| `Building` | 多格（2×2 起） | 是 | **最先摆**（大件优先，否则会被小物件饿死）；房间边长 < `BuildingMinRoomSide` 直接不摆 |
| `Solid`（不可破坏） | 1 格 | 是 | 摆完建筑后摆，参与连通性检查 |
| `Breakable`（可破坏） | 1 格 | **是** | 关键判断：**没有子弹的玩家打不烂箱子**，所以「可破坏」在连通性上等同于墙 |
| `Reward` | 1 格 | **否** | 踩过去就捡到，最后摆，不参与连通性检查 |

**新增三件落地物**：

| 物 | 职责 | 位置 |
|---|---|---|
| `DungeonProp` | `readonly struct`：`Kind` + 画布坐标矩形 + `RoomSlot` + `Variant`；`IsMultiCell` | `scripts/spawn/rooms/DungeonProp.cs` |
| `DungeonPropConfig` | 每类物件的数量/尺寸/离墙格数，全部 `With()` 派生 + `Validate()` | `scripts/spawn/rooms/DungeonPropConfig.cs` |
| `DungeonPropPlanner` | `Populate(in DungeonMap, seed, cfg)` → **新地图**（`readonly struct` 值语义，原图不动） | `scripts/spawn/rooms/DungeonPropPlanner.cs` |

**两条踩过的坑（都在 2026-11-06 修复）**：

1. **四个阶段曾经各建一张 `blocked` 掩码** —— 结果建筑占的格子在摆奖励时「看不见」，
   奖励可以叠在建筑上，而且连通性检查拿的是**残缺掩码**，摆出来的图和检查结论不一致。
   修法：**一张掩码贯穿全部阶段**。
2. **奖励被当成挡路** —— `tools/dungeon_check` 的校验口径把 `Reward` 也算作不可通行，
   于是一张完全正常的图被判「物件堵死通路」。修法：生成器与校验器**共用同一口径**——
   `Kind != Reward` 才挡路。两处口径不一致时排查成本极高，所以这条写进代码注释。

**离墙格数是「尽量」而不是「必须」**：小房间（3~4 格）两边各留 1 格后就只剩 1 格，什么都放不下。
`TryPickOrigin` 因此**逐格退让** `WallClearance → 0`。可放置性是硬约束，留白是软约束，冲突时后者让路。

**验收证据**（`tools/dungeon_check --props --seeds 500`，13×30 网格 / 房间边长 4）：

| 指标 | 结果 |
|---|---|
| 地图生成 | 500 / 500 |
| 平均每图 | 建筑 5.65 · 奖励 5.80 · 可破坏 11.85 · 不可破坏 7.50 |
| 物件堵死通路 | **0** |
| 物件压门 / 压非地面 | **0 / 0** |

**2D 视图同步升级**（用户反馈「注释没有颜色，看不出来什么颜色对应什么房间」）：
图例从「一行纯文字」改为**每一项先画一块真颜色再写名字**，色块直接取自 `DungeonRoomStyleSet`，
换 `.tres` 配色时图例自动跟着变；四类物件在图上用**形状**区分（建筑整块铺满并描边 / 奖励菱形 /
可破坏圆点 / 不可破坏小方块）——形状比颜色更可靠，色弱和小屏都认得出。

---

### 14.10 训练房（2026-11-10 落地）：一间灰房 + 一个人 + 一个假人

> **需求原话**：「现在做一个3D测试场景，场景中只有一个灰色房间，四面有墙，地板是斑马网格。一个人物，一个测试假人。」

**它解决的痛点**：地牢调试场景要跑整套「布局 → 栅格化 → 物件 → 视图」，一次 12×28 图、四类物件全上。
想安静看一个人物的前后左右、验证移动手感和子弹方向时，满屏都是干扰。
训练房只保留最小一片场地，**没有随机、没有生成、每次打开都是同一张图**。

**边界（本节最重要的一段）**：`TrainingRoomScene` 只做「摆场地 + 摆两个东西」，
**不碰** ECS 内核、不碰地牢生成、**0 行玩法逻辑**。
人物整块交给 `CharacterSession`（它自己管意图/技能/相机/HUD），所以调移速/伤害/血量仍然是改表，不是改这里。

| 组成 | 实现 | 为什么这么选 |
|---|---|---|
| 灰色房间 | `RoomCells × RoomCells`（默认 8×8）× `CellMeters`（默认 1m） | 房间大小是 Inspector 参数，不是常量 |
| 斑马网格地板 | **逐格** `MeshInstance3D` + `BoxMesh`，深/浅灰按 `(x+z)&1` 交替 | 逐格而非贴图：格数一变只改循环次数，格线天然对齐；用盒子而非 `PlaneMesh`：Plane 单面，侧看会消失 |
| 四面墙 | 4 面 `BoxMesh`，长度取「房间全长 + 墙厚」封住四角 | 墙高 `WallHeightMeters`（默认 2.4m）独立于格边长——按格比例算出来的墙会矮得像围栏 |
| 人物 | `CharacterSession` + `test_hero` 全套（WASD / 左键射击 / HUD） | 复用组合根，不另写一个人 |
| 测试假人 | 纯表现节点：`Node3D` + 胶囊（身体）+ 球（头），**不进 ECS** | 见下 |

**为什么假人不是 ECS 实体**（用户明确选择「先只要一个站着的模型，打不动」）：
真做成实体就要给它 `Health / SkillState / ActorIntent` 一整套组件和系统，那是「可被打的靶子」才需要的形状。
所以子弹会**直接穿过去**。等要测伤害时，把它换成 `ActorSpawnRequest` 生成的真实体即可——
**这是一次有意的、写在文档里的欠账，不是漏做**。

**素材**：假人和房间全部用几何体 + 纯色材质，**不引用 `素材/` 下任何 `.glb`**。
与 §17.10.1「全部先跑白模」的决定一致：训练房不该依赖外部素材路径，素材库大改也不会把它弄挂。

**自检（无头可验）**：`TrainingRoomScene.EmitSelfCheck()` 在出生落地后数**场景树里真实存在的节点**——
「没报错」不等于「东西真的在」，所以数的是地板格数 / 墙数 / 假人部件数 / `Player` 是否非空。实测输出：

```text
[训练房 自检] room=8x8 cells cell=1.0m floors=64/64 walls=4/4 dummy=True parts=2 hero=True variant=test_hero
```

**踩到的坑（记下来省下一次）**：`CharacterSession` 构造时会**自己**按变体模块拼装
移动/瞄准/射击/冲刺系统，并在末尾调 `pipeline.Build()`。宿主**不能再 `pipeline.Add`**——
重复注册会让同一实体被移动两次，且 `Build()` 之后再 `Add` 直接抛异常。

**踩到的第二个坑**：自检不能在 `_Ready` 里立刻数 `Player`。
`CharacterSession` 要到**第一次 `Tick`** 才把出生请求交给装配系统，`_Ready` 阶段数必然是空的，
会把「正常」误报成「没出生」。改为延迟 5 帧后自检。

**扩展点**：① 房间大小/格数/墙高/配色全部是 `[Export]`，Inspector 改；② 可打的靶子 → 假人换成
`ActorSpawnRequest`（改一处调用）；③ 多几个假人 → `DummyOffsetCells` 改成数组。

## 15. 角色抽象（2026-11-05 落地）

> 本章回答一个问题：**「一个角色」到底是什么？** 答案：它不是一个类，而是
> **「五层数值包 + 一个技能包 + 阵营/控制两个标签」的 ECS 实体组合**。
> 玩家与怪物用**完全相同的组件集合**，区别只在数据行里填了什么。

### 15.1 现状

| 层 | 落地物 | 位置 |
|---|---|---|
| 值词汇 | `ActorKind`（Player/Monster/Elite/Boss/Summon/Destructible）、`ControlScheme`（PlayerInput/Ai/Script）、`SkillSlot`（5 槽）、`StatId` 追加号段 | `scripts/core/` |
| 数据契约 | `ActorDefinition`、`StatPackageIds`、`IReadOnlyStatView`、`IStatPackageRepository` | `scripts/core/Actors/` |
| 技能契约 | `ISkill` / `SkillContext` / `ISkillContext` / `SkillLoadout` / `InputIntent` / `IIntentSource` / `ICommandSink` | `scripts/core/Skills/` |
| 组件 | `ActorIdentity`、`Mana`、`SkillState`、`ActorIntent`（+ 既有 `Transform3`/`Team`/`Health`/`Hurtbox`） | `scripts/actor/ActorComponents.cs` |
| 系统 | `ActorIntentSystem`(PreUpdate) → `ActorAssemblySystem` → `SkillCastSystem`(Update) | `scripts/actor/`、`scripts/skill/` |
| 数据 | `characters.xlsx`（Characters / StatPacks / SkillLoads）+ `CharacterContentSet` 运行时副本 | `data/excel/`、`scripts/data/` |
| 引擎边界 | `PlayerInputIntentSource`、`HostSkillContext` | `scripts/host/control/` |
| 测试 | 25 例（抽象 17 + 内容一致性 9，含端到端装配） | `tests/Characters/` |

### 15.2 角色 = 8 个组件的组合

```text
[Transform3][Team][Health][Mana][Hurtbox][ActorIdentity][SkillState][ActorIntent]
     ↑ 位置      ↑ 阵营   ↑ 血量  ↑ 法力  ↑ 受击盒  ↑ 定义槽+控制  ↑ 技能槽状态  ↑ 本帧意图
```

**玩家与怪物的这一行完全相同**。差异只有两处，且都在数据里：

| 需求 | 实现 | 换阵营要改什么 |
|---|---|---|
| 怪物与角色同源，靠阵营区分 | `ActorIdentity.Team` 与 `ActorKind` **彼此独立**：`Kind` 只做标签（规则/AI/动画筛选），`Team` 才决定敌我 | 生成时传 `TeamOverride` —— 一行代码 |
| 夺舍 | `ControlScheme` 从 `Ai` 改成 `PlayerInput` | 生成/改造时传 `ControlOverride` —— **不新增任何类型** |

> 反例（禁止）：给怪物建 `class Monster`、给玩家建 `class Actor`，或在组件里加 `IsMonster` 布尔。

### 15.3 数值：五层包，Base 绝对 + 四层差量（规则 6）

```text
Final(stat) = Base[stat](绝对值) + Growth[stat] + Equipment[stat] + Buff[stat] + Temporary[stat]
```

- 每层一个 `StatPackageLayer(packId, StatLayer, [(StatId, value)])`；
- `IStatPackageRepository.Compose(StatPackageIds)` 折叠成一个只读视图（`Get` 拿最终值，`GetLayerValue(layer, stat)` 拿单层）；
- **同一个 `packId` 可被不同角色共用**（`player_base` 给玩家、`goblin_base` 给怪），也可互相赋值（给怪挂 `player_growth_demo` 试平衡）。
- 与 §5 的关系：§5 是规则与字段口径的**真源**，本章只是它在运行时的那一段落地。

### 15.4 技能：五个固定槽位 + 定长表

| 槽位 | 值 | 默认按键（InputMap action） | 语义 |
|---|---|---|---|
| `Dash` | 1 | Shift / `actor_dash` | 冲刺（位移类，无目标） |
| `PrimaryAttack` | 2 | 左键 / `actor_primary_attack` | 瞄准射击（最频繁，冷却最短） |
| `WeaponSwap` | 3 | Q / `actor_weapon_swap` | 切武器（改的是「下次射击用什么」） |
| `Signature` | 4 | R / `actor_signature` | **角色独有技能**（角色身份的核心表达） |
| `Interact` | 5 | F / `actor_interact` | 与场景交互（不产生伤害） |

**为什么是定长表而不是 `List<ISkill>`**：槽位是数组下标 → 冷却也是同长度数组 → 每帧只遍历 5 个 int 做减法，无分配、无字典查找；且「哪个键对应哪个技能」由数据固定，玩家改键改的是 InputMap 不是技能表。

**施放门控（`SkillCastSystem`）**：`意图 → 选槽（IntentRules 优先级）→ CanCast → Cast → 扣法力 → 起冷却`，**每帧只消费一个槽**（防止一帧内 Shift+R 双触发）。失败原因写进 `SkillState.LastFailure`（NoSkill/Cooldown/NoMana/TargetInvalid/Forbidden），HUD 与音效据此反馈——**失败也是数据**。

### 15.5 策划怎么改（需求②）

```text
data/excel/characters.xlsx
├─ Characters   ← 加一行 = 加一个角色（类型/默认阵营/控制方案/数值包/技能包/受击盒）
├─ StatPacks    ← 每行 = 某包某层的一项数值（(PackId, Layer, StatId) 唯一）
└─ SkillLoads   ← 每行 = 某包某槽装哪个技能 + 冷却 + 法力消耗
```

- 表头是**四行**（字段名 / 类型 / 中文名 / 备注），由 `tools/data_pipeline/gen_characters_xlsx.py` 生成，保证第一次就合规；
- 生成后 **xlsx 即唯一真源**，不要改 `data/generated/`；
- v1 的 xlsx→生成物 pipeline 尚未落地，`CharacterContentSet.BuildBuiltinV1()` 是与之**逐项一致**的兜底副本；
  `CharacterContentRegressionTests` 用 9 条用例钉住两边（每个角色都能真装配出来、玩家五槽齐全、怪物不含冲刺）。
  **pipeline 落地时只替换 `BuildBuiltinV1` 的填充方式，其余代码与断言不动**（规则 7：0 文件扩展）。
- 想验证「改了数值游戏真变了」：改 xlsx 里 `player_base.HpMax` → 重跑 `GenCharacters` → 端到端用例会立刻告诉你期望值。

### 15.6 新增一个角色 / 一个技能要动几个文件

| 我想做 | 要改的文件 | 数量 |
|---|---|---|
| 加一个怪物（换血换速换技能） | `characters.xlsx`（Characters + StatPacks + SkillLoads 各加行） | **1** |
| 加一个玩家角色 | 同上（`chosen_hero` 就是现成例子：数值包 + 技能包不同，零代码） | **1** |
| 加一个新技能行为 | 1 个 `ISkill` 实现（`scripts/skill/`）+ xlsx 里引用它的 `SkillId` | **2** |
| 让怪物会用技能（接 AI） | 1 个 `IIntentSource` 实现（宿主构造时传入） | **1** |
| 夺舍怪物 | 0（生成/改造时传 `ControlOverride` + `TeamOverride`） | **0** |

### 15.7 约束与下一步

**约束**：① 角色逻辑**不得**读 `Input`——只能消费 `InputIntent`（J4：只有 `scripts/host/` 碰引擎）；
② 组件里**不得**存 `ISkill`/`IReadOnlyStatView` 引用，只存槽位号（J2：组件必须是 unmanaged 值类型）；
③ 技能**不得**在 `Cast` 里 `World.Create/Destroy`，只能走 `ICommandSink`（帧末统一提交，J5）；
④ 加角色/加技能**不得**改 `ActorAssemblySystem` 或 `SkillCastSystem`——改不动它们才是抽象成功的标志。

**下一步**：① 五个技能的真实实现（冲刺位移、射击出弹、切武器、独有技、交互），替换 `DataSkill` 占位；
② xlsx→`data/generated/` 生成器 + 加载校验（`CharacterContentSet` 改为读生成物）；
③ AI 意图源（`scripts/actor/` 的 `AiIntentSource`），让怪物真正用上自己的技能包；
④ 数值快照查询入口（`StatsOf(actorId)` 目前返回 `null`），供技能计算伤害与 HUD 显示；
⑤ 升级/装备/增益对 Growth/Equipment/Buff 层的写入路径。

---

### 15.8 相机跟随手感（2026-11-10 落地）

> 本章回答两个需求：**① 相机怎么丝滑跟随人物？② 怎么按鼠标方向（=射击方向）做可调偏移？**
> 答案：一个**临界阻尼弹簧**（zeta=1）+ 一个**独立的瞄准偏移弹簧**，数学部分全部是零引擎依赖的纯函数。

#### 15.8.1 两个需求各自的机制

| 需求 | 机制 | 数据来源 |
|---|---|---|
| ① 丝滑跟随 | 二阶**临界阻尼弹簧**跟踪人物位置（不是逐帧 `lerp`） | `characters.xlsx` 的 `CameraPacks.FollowRatePerSecond` |
| ② 鼠标方向偏移 | 把瞄准方向投到**屏幕基**上，按比例偏移并封顶，再过一个独立弹簧 | `CameraPacks.LookAheadRatio` / `LookAheadMaxMeters` |

**为什么不能用逐帧 `lerp`**：`a = lerp(a, b, k)` 的收敛速度**依赖帧率**——144fps 下比 60fps 快约 2.4 倍，
配置出来的「手感」换个显示器就变了。临界阻尼弹簧用解析解推进，任意 dt 下轨迹一致。

#### 15.8.2 分层：数学 / 表现 / 数据

```text
data/excel/characters.xlsx  CameraPacks 行（唯一真源）
        |  FollowRatePerSecond . LookAheadRatio . LookAheadMaxMeters
        v
scripts/data/CharacterAssemblySpec.cs   CharacterCameraPack（读表）
        v
scripts/host/GameViewOptions.cs         CameraPackSettings -> CameraRigSettings
        |                              （速率/秒 -> 平滑时间/秒：T = 1/rate）
        v
scripts/core/Camera/CameraFeel.cs       * 纯数学，零 using Godot，可裸测
        v
scripts/host/view/TopDownCameraRig.cs   薄壳：读节点、写 transform
```

**关键约束**：`CameraFeel.cs` **不得** `using Godot`。`tests/` 工程刻意不引用 GodotSharp，
它靠一条 glob 把 `scripts/core/**` 编进不依赖引擎的测试进程——这个文件一旦引入引擎依赖，
**整条结构性守卫就失效了**。判据：`grep -c "using Godot" scripts/core/Camera/CameraFeel.cs` 必须为 0。

#### 15.8.3 弹簧方程与「滑动感」从哪来

用**偏离量** `u = x - target` 推导（用误差量 `e = target - x` 会让 `e'(0) = -v`，翻车三次）：

```
u(0) = A = x0 - target ,  u'(0) = v0        ==>  B = v0 + wA,  w = 2/T
r = exp(-w*dt)
x' = target + (A + B*dt)*r
v' = (B - wA - wB*dt)*r
```

**必须用解析式取速度，禁止用 `(x_next - x)/dt` 反推**。后者在起步段交替输出 `161, 0, 150, 0, ...`
（把「位置被阻尼缩回的部分」当成速度再喂回去 = 正反馈），实测 60 帧后发散到 **2,355,357**。

「滑动感」来自**速度曲线而非位置曲线**：静止起步追 10m，`t=0.03s` 时速度已 53.1 m/s 而位置才挪 1.075m
（「犹豫」），`t=0.058s` 达峰值 61.0 m/s ≈ 理论 `0.736*e0/T = 61.33`，然后指数回落。
位置上是单调逼近，**过冲严格为 0**（实测 maxOver = -1.05e-5）。

#### 15.8.4 参数（策划只改这三格）

| 列 | 含义 | `follow_std` | `follow_heavy` | `follow_fast` |
|---|---|---|---|---|
| `FollowRatePerSecond` | 每秒收敛速率（越大越跟手） | 10.0 | 7.0 | 14.0 |
| `LookAheadRatio` | 偏移幅度 = 该比例 x 正交视野高 | 0.18 | 0.14 | 0.24 |
| `LookAheadMaxMeters` | 偏移封顶（米） | 1.6 | 1.4 | 2.0 |

表现层默认值：`CameraFollowSmoothTime = 0.12s`（约 0.3s 到位）、`CameraLookAheadSmoothTime = 0.25s`
（**刻意取跟随的 2 倍**：偏移稍滞后于鼠标才「顺手」，同速会晕）、`CameraLookAheadDeadZoneRatio = 0.15`
（吃掉中心 15% 半径的手抖；死区边界**连续**，把 `[dz,1]` 重映射到 `[0,1]`，不会一下弹出去）。

#### 15.8.5 怎么验证的（两层，缺一不可）

**第一层——纯函数测试**（`tests/Camera/CameraRigTests.cs`，26 用例，全绿）：

| 组 | 数 | 覆盖 |
|---|---|---|
| A 收敛性 | 4 | 1 秒到位 / 给足时间必到 / 不过冲 / 轨迹单调 |
| B 帧率无关性 | 3 | 60vs144 / **三点检查** / 30-60-240 三档 |
| C 稳定性 | 5 | dt=0.5 / dt=5 / dt=0 / dt<0 / smoothTime=0 |
| D 瞄准偏移 | 9 | 方向 / 封顶 / 只看方向不看模长 / 零向量 / 死区内外 / **死区连续性** |
| E 平滑 | 2 | 反向过渡 / 时间越长越慢 |
| F 收敛函数 | 3 | `Convergence` 帧率无关 / 非正值硬锁 / 极端 dt 有界 |

**做了变异测试证明用例真的能抓 bug**（4/4 被抓）：固定 `Lerp` 被三点检查抓到（0.15s 处 60fps=8.66
vs 144fps=9.93，**差 1.27m**）；去掉封顶、偏移取反、死区做成真台阶各被对应用例抓到。

> **经验**：「1 秒终点残差」这个指标**原理上**区分不了弹簧和固定 lerp —— 固定 lerp 跑满 1 秒后残差
> 1.5e-5，比正确实现还小。只有**中段**（0.15s）才暴露。所以帧率守卫的职责必须交给三点检查，
> 不要把收敛阈值收得极紧来「替代」它。

**第二层——引擎内探针**（`scripts/host/debug/CameraFollowSelfCheck.cs`，`--camera-probe`，8 项全绿）：

纯函数测试证明「数学对」，证明不了 `CharacterSession.Tick()` **真的把人物位置和鼠标方向喂给了相机**。
这条链路断了时画面照样能跑、日志照样干净（v11 那轮「配置了但没接」就是同类）。所以探针直接读
`Camera3D` 与 `TopDownCameraRig.Focus`：

| 检查 | 实测 |
|---|---|
| 正交投影生效 | `projection=Orthogonal size=14.000` |
| 聚焦点贴合人物 | `focus=(48,0,60)` vs 人物 `(48,0,60)`，**距 0.0000m** |
| 人物移动 5m 后相机跟过去 | `focus` 距新位置 **0.0029m** |
| 瞄准 +X / -X 横向分量反向 | **+1.5903 / -1.5903**，摆动 3.1807m |
| 偏移不超上限 | `1.5903 <= 1.600m` |

**同样做了变异测试**：把 `Tick(playerPos, ...)` 改成 `Tick(SpawnPosition, ...)` → 2 条 FAIL，偏差精确
等于 5.0000m；把偏移改成常数偏置 → 2 条 FAIL（符号不变号 + 超上限）。撤掉变异后 8/8 恢复。

> **踩坑记录（重要）**：第一版探针让人物**静止**就断言「focus 贴着人物」——**变异测试骗过了它**，
> 因为人物不动时「跟」和「不跟」的观测值完全相同。必须**制造真实位移**才能让「跟没跟上」成为可判定命题。
> 这是「测试看起来通过、其实没测」的典型，只能靠变异测试发现。

#### 15.8.6 已知债务（本轮刻意不做）

| 债务 | 说明 |
|---|---|
| `CameraPacks.Height` 被灌进 `PitchDeg` | 列口径错位：表里第 2 列叫 `Height`，但读出来赋给了 `PitchDegrees`；`FovDegrees` 列从未被读取。**修它会改掉所有镜头包的俯仰角**，会污染本轮手感调参的因果判断，故单独排期。当前实测生效俯仰角 = 61.17 度 |
| 相机不响应缩放 / 不撞墙 | 正交尺寸固定；无遮挡拉近（`ClampToBounds` 只管地图边界） |
| 无震屏 / 无 FOV 动态 | 留给战斗反馈系统 |
| `tools/probes/camera_follow_probe.gd` | 早期 GDScript 探针，已被 C# 探针取代（GDScript 版无法访问 ECS 人物）。保留作参考，不参与验收 |

**新增一个镜头包 / 改手感要动几个文件？** —— **1 个**：只改 `data/excel/characters.xlsx` 的
`CameraPacks` 一行。C# 0 行改动（规则 7 的判据）。

---

## 16. 素材快查看器（Asset Viewer，2026-11-05 落地）

> 本章回答一个问题：**「美术导入的模型长什么样、规格对不对」怎么快速自查？**
> 答案：一个独立的 Win32 桌面程序 `tools/asset_viewer/`，**不依赖引擎、不依赖第三方库**，
> 双击即用，全量 371 个模型 4.6 秒扫完。

### 16.1 为什么是独立程序（而不是编辑器插件）

| 约束 | 结论 |
|---|---|
| 素材全部是 `.glb` / `.fbx` | 需要 glTF 2.0 + FBX 7.2/7.4/7.5 的二进制解析 |
| 本机 **vcpkg 的 godot-cpp 只有 4.4**，与引擎 4.7.2 不匹配（见 §6） | 走 GDExtension 要额外引 SDK，代价大于收益 |
| 需求是「扫一遍看缩略图」，不进游戏 | **不该**进 `addons/`，也不该被 Godot 的导入流程拖慢 |
| 用户明确要求「单独程序 + CMake + MSVC」 | 独立 exe，与游戏工程解耦，可随时单独重编 |

因此放在 `tools/asset_viewer/`，是**开发期工具**，不是运行时组成。

### 16.2 技术选型：零第三方依赖

| 层 | 用什么 | 为什么 |
|---|---|---|
| 窗口 / 输入 | **Win32 原生** | 不需要任何 UI 框架 |
| 三维渲染 | **Direct3D 11**（`d3d11` + `dxgi` + `d3dcompiler`） | 系统自带 |
| 二维 UI / 文字 | **Direct2D + DirectWrite** | 系统自带，负责列表、信息面板、搜索框 |
| 图片解码 / 编码 | **WIC / Windowscodecs** | 读贴图、压 PNG 都用它 |
| glTF 解析 | **自研**（`glb_loader.cpp`） | 只需要读，不需要完整一致性 |
| FBX 解析 | **自研**（`fbx_loader.cpp`，含自带 **DEFLATE 解压器**） | FBX 压缩数组是 zlib，不想为它引 zlib |
| 矩阵 / 数学 | 自研 `mat::` 命名空间 | 约 200 行，够用 |

链接库：`d3d11 dxgi d2d1 dwrite windowscodecs d3dcompiler ole32 uuid shell32 shlwapi
comctl32 comdlg32 user32 gdi32 advapi32 oleaut32` —— **全是 Windows SDK 自带**。

### 16.3 模块契约（谁实现什么）

`tools/asset_viewer/src/model_data.h` 是**唯一共享契约**，所有模块只认它：

```text
Vertex     32B：pos(3f) + normal(3f) + uv(2f)           ← 与 D3D 输入布局一一对应
MeshData   顶点/索引 + materialIndex
Bone       名字、父索引、逆绑定矩阵、局部绑定变换
Clip       名字、时长、关键帧轨道（位置/旋转/缩放/时间）
TextureInfo 宽高 + BGRA 像素 + 来源路径 + 是否已解码
ModelData  网格列表 + 骨骼 + 动画片段 + 贴图 + 包围盒 + 统计（三角面/顶点/骨骼数）
namespace mat::  行优先 float[16]、行向量×矩阵、y-up 右手系
LoadGLB / LoadFBX / LoadModel ← 唯一加载入口（按扩展名分发）
RenderThumbnail               ← 离屏渲染一张缩略图（CPU 光栅，供批量出图）
```

**关键决定**：加载器**不碰 D3D**，只吐 `ModelData` 纯数据；渲染器不碰文件格式。
这条边界让 `thumb_sheet`（控制台批量出图）能复用同一套加载代码而不需要窗口。

### 16.4 构建目标一览（`CMakeLists.txt`）

| 目标 | 类型 | 作用 |
|---|---|---|
| `av_core` | STATIC | 加载器 + 数学 + 缩略图渲染（无窗口、无 D3D 依赖） |
| `av_ui` | STATIC | 窗口、D3D11 渲染、D2D 界面 |
| `asset_viewer` | **WIN32 exe** | 主程序 |
| `loader_smoke` | CONSOLE | 批量加载自检（不开窗口） |
| `ui_selftest` | CONSOLE | UI 逻辑自检（扫描 / 缓存 / 选择） |
| `thumb_sheet` | CONSOLE | 批量渲染缩略图，拼成 8×6 拼版 PNG + `index.txt` |
| `av_sheet` | 自定义 target | 一条命令跑 `thumb_sheet` 全量 |

**开关 `AV_STATIC_RUNTIME`（默认 OFF）**：置 ON 则 `/MT` 静态链接 MSVC 运行库，
产物**免装 VC++ Redistributable**，可单文件分发 —— `dist/` 里的正式版就是用它编的。

### 16.5 血泪教训（改动这里之前必读）

这几条都是**实际导致过崩溃/卡死**的，不是理论风险：

| 症状 | 根因 | 修法 |
|---|---|---|
| 全量 FBX 崩溃，进程内存涨到 **42GB** | `Inflater` 解码出错时产生海量「复制」符号，输出缓冲无限增长 | 按 **zlib 最大压缩比 1032:1** 由输入长度反算 `outLimit_`，`inflateBlock` / `inflateStored` 每个字节都校验 |
| 同上（第二道闸） | 畸形 `count` 直接喂给 `resize()` | `kMaxArrayElems = 16M` 硬上限 + 解压后 `raw.size() == count*elemSize` **严格相等** |
| FBX 子网格阶段**死循环**，卡在 `[MARK] 1174` | 材质索引归一化（`-1`→`0`）后比较口径不一致，`endTri` 一次不前进 | 两侧统一走 `normMat` lambda，且 `endTri` 初始化为 `t + 1` |
| `CreateWindowExW` 返回失败，`errno=1400` | `WM_NCCREATE` 里 `hwnd_` 尚未赋值就被 `HandleMessage` 使用 | `WM_NCCREATE` 分支**独占**：赋值 `hwnd_` 后直接 `return DefWindowProcW(...)`；其余消息仅在 `self->hwnd_ == hwnd` 时才走自定义处理 |
| 中文路径全部打不开 | `cmd` 批处理传给程序的参数会按代码页丢失中文（变成 `??`） | 程序侧用 `CommandLineToArgvW(GetCommandLineW())`；**验证时必须用 PowerShell `Start-Process` 传参，不要用 cmd 批处理** |
| `windows.h` 的 `#define DrawText` 污染 `d2d1.h` | 宏与 `ID2D1HwndRenderTarget::DrawTextW` 撞名 | `viewer_app.h` 里 `#undef DrawText` / `#undef DrawTextEx` |
| D3D 报 `0x80070057`（参数无效） | 顶点跨多个 slot | 单一 slot 0、64 字节交错顶点 |
| 模型不可见（只有背景） | `MakeLookAt` 的 z 轴取反 | `z = normalize(center - eye)`（指向观察方向） |
| **静态版 `/MT` 全量崩溃**（`exit=0xC0000374` 堆破坏，稳定停在 `Bird.glb`），动态版 `/MD` 一切正常 | `loader_smoke.cpp` 一行 `printf` 里同时写了 `ToLocal(...).c_str()` 与 `Utf8ToLocal(md.error).c_str()` —— **两个 `std::string` 临时量**，实参求值与析构顺序都未指定，指针悬垂。`/MD` 的堆布局侥幸没被覆盖，`/MT` 一换分配器就必崩 | 临时量先落到具名变量（`const std::string localName = ...`）。**教训：只要 `printf` 里出现 ≥2 个返回 `std::string` 的调用，就必须先具名** |
| **「模型没有纹理」**（用户报的 4 个 bug 之一） | ① 贴图预览被写死压到 **最长边 32px**（`kPreviewMaxEdge = 32`），而素材库 74 张贴图里有 **71 张是 1024×1024**；模型 UV 又只用到图集的一小块 —— Anne 只占 **7.24%**，压到 32px 后角色实际可用贴图只剩 **8×9 像素**，整张角色贴图 72 个像素，必然糊成一片纯色。② 另有 123/371 个模型的 `baseColorFactor` 极暗（`Armor Leather` = sRGB(0.110,0.060,0.041) → 线性(0.011,0.004,0.002)），灯光再足也只剩黑点 | ① `kPreviewMaxEdge` **32 → 256**（面积 64 倍，单张 256KB），并加 `kPreviewBudgetBytes = 96MB` 进程级预算，逼近上限时自动逐级降到 128/64/32。② D3D `PSMain` 与 CPU `thumb_renderer` 各加一段**自适应曝光**：线性亮度 < 0.16 时按亮度放大到 0.16（gain 上限 24），**只放大量、不动色相与相对明暗** |
| 动画列表名字显示成 `Dea` / `Pun` / `Swo` / 空串 | FBX 帧名有 **80 字符长度限制**，FBX2glTF 拼接后整条名字恰好 81 字符被截断，**最后一段全是残段**。原 `NormalizeName` 取"最后一个非空段"正好取到残段 | 改为**取中间那个完整的非噪音段**（跳过 `CharacterArmature` / `*Armature` / `Root` / `Mixamo` 等骨架名），并新增 `StripDupSuffix` 去掉 Blender 的 `.001` 后缀。修复后 Anne 的 14 条 clip 全部为 `Death/Duck/HitReact/Idle/Jump_Idle/Jump_Land/Jump/No/Punch/Run/Sword/Walk/Wave/Yes` |

### 16.6 验收基线（回归时对这些数字）

| 验收项 | 结果 |
|---|---|
| `loader_smoke` 全量（`/MD` 与 `/MT` 双版） | **371 ok / 0 fail**，avg 1.84~1.87ms；GLB 301/301、FBX 70/70 |
| `ui_selftest` | **77 通过 / 0 失败**（含 371 条目扫描、缓存命中 371/371） |
| `probe_hit`（bug 1 滚轮命中回归） | 14 个滚动位置 × 316 个单元格中心点，**命中率 100.00%，零错位** |
| `probe_anim`（bug 4 动画链路） | Anne 60 骨骼 / 14 clip，每 clip 驱动 **49~57 根骨骼**、135~533 个关键帧，蒙皮覆盖 **84.2%** 顶点；Pirate Captain 92.6%、Skeleton 86.6% |
| `render_regress`（全量可见性） | 301 个 glb 全部渲染出画面，`invisible(<1%)=2`（均为 Arrow/Claymore 类细长模型，属正常） |
| `asset_viewer.exe --shot=` | 成功导出 PNG（2282×1564）|
| 真实 GUI 像素统计 | 窗口 1415×937，全窗 **4413** 种颜色；主预览区模型中心 **(849,433)** vs 区域中心 **(850,430)**，**居中误差 3px** |
| 规模核对 | 88.3 万三角形 / 132.5 万顶点；**12** 个带动画模型全部正确解析 |
| 贴图信息量（bug 3 修复佐证） | 74 张贴图中 71 张为 1024×1024；Anne 的 UV 只占图集 7.24%，32px 预览下角色可用贴图仅 **8×9 px**，256px 下为 **64×73 px**（**64 倍**） |

### 16.7 交付形态

```text
tools/asset_viewer/dist/
├─ asset_viewer.exe         ← 单文件绿色版（619KB，/MT 静态运行库，无外部依赖）
├─ 启动素材查看器.bat        ← 双击即用；素材文件夹可拖到 bat 上切换目录
└─ 使用说明.md              ← 中文操作说明
```

`dist/` 整体被 `.gitignore` 的 `tools/*/dist/` 忽略 —— **产物不进版本库**，
任何时候用 `-DAV_STATIC_RUNTIME=ON` 重编即可。

### 16.8 扩展点（加一种格式要改几个文件？）

| 需求 | 改哪里 | 文件数 |
|---|---|---|
| 加一种模型格式（如 `.obj`） | ① `model_data.h` 声明 `LoadOBJ`；② 新写 `obj_loader.cpp`；③ `LoadModel` 里加一个 `if` | **3** |
| 加一种贴图格式 | `wic_decode.cpp` 的格式映射表 | **1** |
| 改缩略图配色 / 尺寸 | `ThumbOptions`（`bgTop`/`bgBottom` 等，已参数化） | **0** |
| 改界面布局 | `viewer_app.cpp` 的 `Layout` 结构体 | **1** |

### 16.9 共享契约：顶点与材质（2026-11-05 v2 修订）

**改这几个结构前必须同步所有加载器与两个渲染器**（`glb_loader` / `fbx_loader` / `d3d_renderer` / `thumb_renderer`）。

| 结构 | 字段变化 | 说明 |
|---|---|---|
| `av::Vertex` | **32 → 48 字节**，在 `u,v` 之后追加 `float r,g,b,a` | 顶点色。加载器在模型无 `COLOR_0` / `LayerElementColor` 时填 `(1,1,1,1)`，所以渲染端**无条件相乘**是安全的（乘法单位元） |
| `GpuVertex`（`d3d_renderer.cpp` 内部） | **64 → 80 字节**，`COLOR` 绑在 **slot 0 offset 32**（`R32G32B32A32_FLOAT`），`BLENDINDICES`/`BLENDWEIGHT` 顺延到 48 / 64 | 输入布局仍是单一 slot 交错顶点 |
| `av::TextureInfo::preview` | 最长边 **32 → 256**（`kPreviewMaxEdge`），新增预算 `kPreviewBudgetBytes = 96MB` 与 `PreviewBytesInUse()` | D3D 与 CPU 缩略图**都**读这份 preview，改一处两边同时生效 |

颜色合成顺序（D3D `PSMain` 与 CPU `RasterTri` **必须一致**）：

```text
base = baseColorFactor × 贴图(若有) × 顶点色 → pow(2.2) 转线性 → 自适应曝光 → 光照 → pow(1/2.2)
```

三条容易踩的坑：

1. **必须先把 sRGB 空间的三个因子乘完再统一做 gamma**，否则暗部会被压两次。
2. **`Vertex{}` 聚合初始化会把顶点色清成 0（黑）**，凡是手搓 `Vertex` 的地方（如内置占位立方体）都要显式填 `(1,1,1,1)`。
3. 「中性提亮」逻辑只对**既无贴图又无顶点色且 baseColor 为纯灰**的模型生效；一旦有顶点色就绝不能提亮，否则会把配色洗白。

---

## 17. 基础物件抽象：物体原型 + 熔炉组合（2026-11-06 落地）

> 本章回答一个问题：**「一把武器 / 一颗子弹 / 一个怪物」到底是什么？**
> 答案：它们**都不是四个类**，而是同一个 `ObjectArchetype` **同一张 Excel 表**的不同行；
> 差异只在 ① 能力位（`ObjectCapability`）② 挂在它身上的四个引用（武器/子弹/怪物/特效）
> ③ 位矩阵（属性 × 标签）与规则值（数值引用）里填了什么。
> **于是「武器射怪物 / 子弹是怪物 / 子弹带武器属性 / 武器带子弹属性」全部是填表，
> 运行时走的是同一段铸造代码。**

### 17.1 一句话结论与扩展性实测

| 我想做 | 改哪里 | 文件数 |
|---|---|---|
| 加一把武器 / 一颗子弹 / 一只怪 | `combat.xlsx`（Objects + Weapons 或 Projectiles + StatPacks 各加行） | **1** |
| 让"武器射出怪物" | `Weapons.Delivery=SpawnActor` + `ActorRef=某 Objects.Id` | **0** |
| 让"子弹炸开再射子弹 / 变成怪物" | `Projectiles.OnExpire=SpawnProjectile/SpawnActor` + `OnExpireProjectile/OnExpireActor` | **0** |
| 让"子弹继承武器的伤害/速度" | `Weapons.InheritWeaponStats=true`，规则值写 `wstat:WeaponDamage(5)` | **0** |
| 让"武器也带子弹属性"（自身可飞行） | Objects 行的 `Tags` 同时勾 `tag.weapon,tag.projectile`，能力位自动变 `Weapon|Projectile` | **0** |
| 新增一个"熔炉簇"（同类可互换的候选池） | `Objects.Cluster` + `ClusterWeight` 两列 | **0** |
| 新增一种规则值（第 10 种数值来源） | ① `ValueSource` 加枚举 ② `ValueResolver` 加一支 ③ 表解析加一支 | **3**（详见 §17.3） |

### 17.2 数据形状：一个 Archetype，四个引用，两个矩阵

```text
                ┌──────────────── ObjectArchetype（= Excel Objects 一行）───────────────┐
                │ Id / IdHash(FNV-1a32)  DisplayKey  Tags(位集)  ClusterSlot  ClusterWeight │
                │ StatPackageIds（五层数值包，和角色共用同一套）   Radius/Height/RenderId    │
                │ Weapon:ContentRef   Projectile:ContentRef   Actor:ContentRef   Effect:ContentRef
                └─────────┬───────────────┬───────────────────┬────────────────────┬─────┘
                          │               │                   │                    │
              ┌───────────▼──┐   ┌────────▼────────┐  ┌───────▼────────┐  ┌────────▼───────┐
              │ WeaponContract│   │ProjectileContract│  │  Actor 能力位  │  │  纯表现物件    │
              │ Trigger/Delivery│  │ Motion/HitPolicy │  │ Hurtbox+Health │  │ Capsability=None│
              │ Aim/DamageType │  │ OnExpire(→子弹/怪)│  │ +ActorIdentity │  │                │
              │ 13 个 ValueSet │  │ 9 个 ValueSet    │  └────────────────┘  └────────────────┘
              │ 2 个位矩阵     │  │ 1 个位矩阵       │
              └────────────────┘  └──────────────────┘
```

**为什么是「引用」而不是继承**：`ContentRef` 只存目标 id 的稳定哈希 + 权重（`DefId`/`Weight`/`HasTarget`），
所以「A 引用 B、B 又引用 A」（子弹射出枪、枪发射自己打的子弹）**不会在装配期形成环**；
组件里也能存（J2），热路径只做一次字典命中（装配期已转成 int 槽位号）。

**为什么能力位是 `[Flags]` 而不是枚举**：`Actor|Weapon`（会开枪的怪）、`Weapon|Projectile`（自己飞出去的枪）
都是合法的表取值；系统永远只问「你有 Weapon 位吗」，**全工程没有一处 `switch (kind)`**（J3）。

**两个位矩阵**：`ObjectBitMatrix`（行 = 物件，列 = 表 `Tags` Sheet 里的 tag）由策划在表里用
`Objects.Tags` 列勾出来；**代码里不出现任何具体 tag 名** —— 「哪些 tag 构成 Actor / Weapon / Projectile 能力位」
由 `Tags.Group` 列（`ObjectKind` 组）数据化映射。同一份矩阵被弹道、熔炉、规则值（`flags:xxx`）三处复用。

### 17.3 规则值 `ValueRef`：让"数值"也能拼装（用户需求①的数值半边）

**问题**：如果 `Weapons.BaseDamage` 是一个 float，那"子弹继承武器的伤害"就只能靠代码特判。
**做法**：所有玩法数值列都存**规则值字符串**，装配期解析成 10 种来源之一的 `ValueRef`：

| 表里写 | `ValueSource` | 含义 | 为什么需要它 |
|---|---|---|---|
| `12.5` | `Literal` | 字面量 | 大多数情况 |
| `stat:Dexterity` / `stat:Damage+30%` | `Stat` | 读**持有者**数值包的最终值 / 加百分比层 | 「武器数值 = 角色属性」 |
| `wstat:WeaponDamage(5)` | `WeaponStat` | 读**链上来源武器**的这个数值；来源缺失回落括号默认值 | **「子弹具有武器属性」的唯一实现** |
| `flags:tag.split` | `Flags` | 位矩阵该 tag 位 → 1/0 | 「弹幕因为带了 split 标签而分裂」 |
| `chain:depth` / `chain:count` | `ChainDepth/ChainCount` | 熔炉链深 / 本帧已链数 | 熔炉防无限自激 |
| `viable:count` / `perf` | `ViableCount/Perf` | 候选数 / 命中率 | 让数值感知"池子有多大、多能命中" |
| `rand:2~3` / `rand:10+0.5` | `RandomRange/RandomScaled` | 随机区间 / 基础值±抖动 | 手感随机，且走注入的 `IRandomProvider`（可回放） |

`ValueSet` 是容量 4 的定长集合，按 `Σabs × (1 + Σpercent)` 层叠 —— 于是一条武器数值可以由
「字面量 + 角色属性 + 武器继承 + 随机」四路叠加而成，**全部是数据，代码 0 行**。
求值只依赖 `ValueContext`（读 `IValueWorld` 的原始值 + `StatPackageIds`），稳态**零托管分配**（有断言钉住）。

### 17.4 熔炉 `ForgeRules` + 寄存器 `ContentRegistry`：高性能工厂的核心

「熔炉」= 给定**一条配方**（`ulong`，64 位，每位 = 一个 tag）与**一簇候选**，
在候选里挑出「命中配方 ∩ 可用 ∩ 加权随机」的那一个，产出 `ForgeResult(Outcome, SelectedDefId, Perf, MatchedCount)`。

```text
配置期（装配期，允许分配）                             运行期（每帧，零分配）
Excel Objects.Cluster / ClusterWeight    ─┐
Tags.BitMask → 64 位配方字               ─┼▶  ClusterCandidates{int[] objectSlots,
ContentBinder 摊平成扁平 int[]/float[]   ─┘        float[] weights, int[] viableCounts}
                                                          │
                                          ┌───────────────▼──────────────────┐
                                          │ ForgeRules.Evaluate(             │  ← 纯静态、无状态、
                                          │   recipeTag, defIds, tagMasks,   │    只吃 ReadOnlySpan
                                          │   weights, viableCounts, ctx)    │    （不 new ForgeCandidate[]）
                                          └───────────────┬──────────────────┘
                                                          ▼
                                          ContentRegistry.ResolveWithRegister(slotId, ctx, recipeTag, out result)
                                                          ▼
                                          EcsObjectFactory → ContentSpawner → World.Create(三张预建组件表)
```

| 设计点（用户需求③「工厂必须高性能，用对象池减少 GC」） | 落点 |
|---|---|
| 稳态**零托管分配** | `ForgeRules` 纯函数只吃 `ReadOnlySpan`；`CastSystem` 的暂存/环形缓冲**构造期一次预分配**；用例断言 1000 次迭代 `GC.GetAllocatedBytesForCurrentThread()` 增量为 **0** |
| 「对象池」在本层指**结构化缓冲池 + ECS 原型列式** | ECS 实体本身不是 Node，创建走 `World.Create`（chunk 复用）；`CastQueue` / `LastProjectiles` 定长环形缓冲；`ContentSpawner` 的**三张预建组件表**随实体复用，不每发子弹重新拼结构。真正的 Node 侧 GC 在宿主层 `ViewRegistry`（另一任务） |
| 字符串不进热路径 | 所有 `id → int` 在装配期解析：`ObjectArchetype.IdHash`（FNV-1a32）、`ContentIndex` 槽位号、`ContentRef.DefId`；热路径只有数组下标 |
| 每个实体一份"寄存器" | `ContentRegistry` 按「簇 + 槽位名」编址（如 `weapon.slot.primary`），`TryBind` 装默认件，`ResolveWithRegister` 开炉覆盖，`Purge(generation)` 整代回收回**不可变的 `InitialDefId`** |

### 17.5 一帧内的铸造顺序（怎么接进 §6 / §13.2 的七阶段）

```text
PreUpdate   CastSystem      消费 CastQueue → ValueContext → Resolve/Forge → EcsObjectFactory.TryCast
                            （CastRequest 由技能/输入/事件投递；本帧产物写进 LastProjectiles 环形缓冲）
FixedUpdate ProjectileAdvanceSystem   位置积分 + 寿命递减 + OnExpire 到期 → 再投递一次铸造
            （**本轮不做**）碰撞/伤害/命中特效 —— 明确属于游戏逻辑，见 §17.7
```

**四个"组合玩法"在代码里是同一段**：`EcsObjectFactory.TryCast` 只问「契约里哪个引用非空 +
`Delivery`/`OnExpire` 是什么」，于是

| 玩法 | 表 | 运行时 |
|---|---|---|
| 武器射子弹 | `Weapons.ProjectileRef` 非空 | `ContentSpawner.TrySpawnProjectile` |
| 武器射怪物 | `Weapons.ActorRef` 非空 + `Delivery=SpawnActor` | `ContentSpawner.TrySpawnActor`（产物带 `Hurtbox/Health`，**无** `ProjectileState`） |
| 子弹炸开再射子弹 | `Projectiles.OnExpire=SpawnProjectile` | `EcsObjectFactory.TrySpawnDeath`（`ChainDepth+1`） |
| 子弹变成怪物 | `Projectiles.OnExpire=SpawnActor` | `EcsObjectFactory.TrySpawnDeath`（产物是角色） |

### 17.6 策划怎么改（需求②）：两张表 + 一个校验器

```text
data/excel/
├─ characters.xlsx   ← 角色（Characters / StatPacks / SkillLoads）——见 §15
└─ combat.xlsx       ← 物件（Tags / Objects / Weapons / Projectiles / StatPacks / VisualPacks / _Meta）
   ├─ Tags          ← 位矩阵的"列定义"：TagId / TagIndex / Category / Group / BitMask
   ├─ Objects       ← ★唯一的物体原型表：人物/怪物/武器/子弹/特效全在这里
   │                   Id / Tags / Cluster / ClusterWeight / StatPacks / Weapon / Projectile / Actor / Effect
   │                   / RenderId / **VisualPack** / Radius / Height / Note
   ├─ VisualPacks   ← 外观包（模型路径 / 缩放 / 染色 / 动画剪辑）——见 §17.10
   ├─ Weapons       ← 武器契约（8 行 × 28 列，13 个规则值列）
   ├─ Projectiles   ← 子弹契约（7 行 × 20 列，8 个规则值列）
   └─ StatPacks     ← 武器/子弹自己的五层数值包（与 characters.xlsx 同构，合并进同一个仓库）
```

- 生成器 `tools/data_pipeline/gen_combat_xlsx.py` 保证四行表头（字段名/类型/中文名/备注）+ `#data` 标记行合规；
- **构建期闸门 `tools/data_pipeline/validate_content_xlsx.py`**：4553 项检查 —— 四行表头、类型、外键闭合
  （四个引用必须指向 `Objects.Id`）、能力位一致（`tag.weapon ⇔ 被 Weapons 引用`、`tag.projectile ⇔ 被 Projectiles 引用`、
  `tag.actor ⇔ Base 包非空`）、`TagIndex` 连续 + `BitMask` 唯一、**规则值语法真解析**、
  `StatPacks` 唯一性、**同簇能力位一致**（防"熔炉把子弹换成枪"）、`ClusterWeight>0`、
  **外观包必填 + 外键闭合 + `ModelPath` 磁盘存在性**（§17.10）；
- **闭环证明**：`ContentBinderTests` / `ValueRefParserTests` / `ContentPlayLoopTests` 直接用**零第三方包**的
  `XlsxReader` 读**真 xlsx** → 绑定 → 真 `CastSystem` 铸造成实体，验证"武器射出怪物""子弹到期生成子弹"
  "子弹变成怪物""改 `BaseDamage`/`ClusterWeight` 后运行期行为随之改变"。**运行期代码不读 xlsx**（§4.8 不变）。

### 17.7 边界与明确不做（本轮）

| 明确不做 | 归属 |
|---|---|
| 碰撞检测 / 空间划分 / BVH、伤害结算、命中特效、击退 | **游戏逻辑**，下一任务；`ProjectileAdvanceSystem` 只做运动学积分 + 寿命，`Explode`/`SpawnEffect` 到期行为**显式返回"未实现"并计入 `RejectCount`**，不静默吞 |
| Node 视图池与 `ViewRegistry` 绑定（"对象池减少 Node GC"的 Node 侧） | 宿主层任务 |
| 武器法力消耗列（`Weapons.ManaCost`） | ⚠️ 已知退化：`CastSystem` 暂以 `weapon.Count` 求值代替门控，表补列后改一行 |
| `Objects.ClusterWeight` 之外的分项权重（按 tag 加权的复杂熔炉） | 扩展点：`ForgeRules` 只吃加权数组，加权重来源 = 换 `ClusterCandidates` 的填充 |
| **`WeaponDelivery` 的 Instant / Beam / Melee / Aura / Throw** | 目前只有 `Projectile` 与 `SpawnActor` 被 `EcsObjectFactory` 消费；其余五种投送方式**填了也不生效**，接它们属于命中判定（游戏逻辑） |
| **`ProjectileHitPolicy` 的运行时结算** | 契约里 `Pierce`/`Bounce` 次数已被铸造进组件，但"命中后怎么扣"属碰撞/伤害结算，本轮无碰撞系统 |
| **`WeaponFireSystem` 在 `DungeonDebugScene` 里的注册** | 系统与测试已就绪（7 条集成用例全绿），但关卡侧尚未装配：真实场景里还没有"手持武器"的实体，接它要等角色生成链路（`SpawnCharacter` 目前零调用点） |

**约束**：① 契约层（`Game.Core.Content`）**零 `using Godot`、零上层依赖**；② 组件只存 int/值类型（J2），
所有 id 在装配期解析成 int 槽位号；③ 铸造**只能**在系统循环里发生，结构化变更走队列/命令缓冲（J3/J5）；
④ 新增物件**不得**改 `scripts/combat/**` —— 改不动它才是抽象成功的标志。

### 17.8 本轮踩到的 6 个真 bug（都是"静默失效"，改动这里前必读）

| # | 症状 | 根因 | 修法 |
|---|---|---|---|
| 1 | 策划改了表，游戏里一半物件"查不到" | `ContentRef.DefId` 是 **FNV-1a32 有符号哈希**，近一半是负数；用 `DefId < 0` 判空 → 那些物件被当成空引用 | 判空**只认** `ContentRef.HasTarget` / `.IsEmpty`，永不看符号 |
| 2 | 熔炉簇内互换全部失效、`ClusterOf` 恒空 | `ContentIndex.ObjectSlotOfHash` 前置判了 `hash >= 0` | 去掉符号判断，直接查字典 |
| 3 | 绑过一身装备后，`Purge` 回收不到装配初始态 | `Purge` 恢复到 `DefaultDefId`，而 `TryBind` 会覆写它 | `RegisterSlot` 增不可变 `InitialDefId`，`Purge` 恢复它 |
| 4 | **所有射击武器运行时静默打不出子弹** | `Projectiles` 行主键是 `proj_bullet_201`，但所有引用列填的是**物件 id** `bullet_201` → `ProjectileSlotOf` 恒 -1 → 工厂 `return false` | 契约 `DefId` 统一用 **`ObjectId`**；行主键只留表校验/调试 |
| 5 | 带 `tag.projectile` 的物件拿不到 `ProjectileContract` | 只认 `Objects.Projectile` 显式列，而 `bullet_101` 那列留空（靠 `Projectiles.ObjectId` 反向关联） | 双来源都认 + 加正向用例「每个 `tag.projectile` 物件都必须可达契约」 |
| 6 | `characters.xlsx` 的表头"横躺"，C# 读不了 | 生成器把「每列一条」的四元组当**行** `ws.append()` 写进去（转置） | 按列填格子；生成后用 openpyxl 打印第 1 行确认是 N 个字段名 |

> 共同教训：**数据层/契约层的错误不会抛异常，只会让功能静默失效**。所以宁可多写"正向可达性"用例
> （「每个 X 都必须能查到 Y」），也不要只写"负向会报错"用例。

加载器**只产出 `ModelData`**，所以新增格式不会波及渲染与 UI —— 这是 §16.3 那条边界带来的收益。

### 17.10 模型属性：外观只走「外观包」这一条路（2026-11-09 收敛）

**问题**：用户问「当前的角色/怪物/武器/子弹，是否有模型属性？能在 3D 中看到资产吗？」——
答案是**有字段，但没有唯一入口**。`RenderId` 从 §17 起就存在（它只是给视图层选槽位的标识符，
**不是路径**，全工程没有任何代码能靠它找到磁盘模型），但「这个物件用哪个模型」当时有三条并存的写法：

| 写法 | 当时的意思 |
|---|---|
| `Objects.VisualPack` → `VisualPacks` | 走外观包（模型+缩放+染色+动画）|
| `Objects.AssetPath` | 直连 `.glb` 路径，绕过外观包 |
| 两个都不填 | 回落白模胶囊 |

于是真表里必然长出「有的有、有的没有」（怪物走包、史莱姆走直连路径、特效什么都不填），
同一个概念有三种答案。**抽象本身没缺，缺的是唯一入口。**

**收敛后的形状**（用户决定：「删掉 AssetPath/AssetAnim，全部走 VisualPack」）：

```text
Objects.VisualPack  (必填)  ──→  VisualPacks.Id
                                    ├─ ModelPath  : 项目资源路径 / 空（空 = 暂用白模）
                                    ├─ Scale      : > 0
                                    ├─ TintHex    : #RRGGBB / 空
                                    └─ AnimClips  : 逗号分隔剪辑名 / 空
```

- **`AssetPath` / `AssetAnim` 两列已从表、生成器、`ContentBinder`、`ObjectArchetype` 全部删除**，
  全工程只剩 `ObjectArchetype.VisualPackId` 一个外观字段。
- **「有意没有模型」是一个取值，不是一个空缺**：特效/纯逻辑物件填 `vp_none`（该包 `ModelPath` 留空）。
  这样「有意为空」（`vp_none`）与「忘了填」（空串，校验器直接报错）在数据上**永远可区分**。
  这是本节最重要的一条口径 —— 只要允许留空，"留空"就同时背了两个含义，问题必然复发。
- 校验器三条新规则（`validate_content_xlsx.py`）：
  ① `Objects.VisualPack` **必填**；
  ② 外键必须落在 `combat.xlsx` 或 `characters.xlsx` 的 `VisualPacks` 里；
  ③ 有 `Actor`/`Weapon`/`Projectile` 能力位的物件指向无模型的包时，留一条 `[提醒]`
  （**2026-11-10 由 error 降为 warning**，理由见下节「全部先跑白模」）。
  另：`VisualPacks.ModelPath` 由必填改为可空，但空包若填了 `TintHex`/`AnimClips` 会报错（死字段）。
- 三个用例钉住：`Binder_EveryObject_HasExactlyOneExistingVisualPack`（每行都有且只有一个真实存在的包）、
  `Binder_TableWithoutVisualColumns_StillBindsWithEmptyVisualFields`（老表缺列仍能装配，规则7）、
  `Binder_VisualColumns_DoNotAffectLogicIdentity`（换模型不改 `IdHash`/`Capabilities`/四个引用）。

**本轮如实说明的两件事**（不粉饰）：

1. **视图层未接**：`ActorViewDirector` 仍按 `VisualPack.ModelPath → 白模 Box/Capsule` 的优先级渲染，
   且**武器与子弹在 3D 里根本不渲染**（`scripts/host/**` 没有 `ProjectileState` 的引用）。
   用户明确要求「先补字段与绑定，视图层保持现有白模」，故本轮只到数据层。
2. **部分配型是占位**：素材库 301 个 `.glb` 里**没有怪物素材、没有子弹素材**，所以
   食人魔用炮台、史莱姆用骷髅/炸弹、子弹用小矿石/金币/宝石代替，`VisualPacks.Note` 逐个写明了。
   → **这一条已被下节的「全部白模」决定取代，占位路径已全部清空。**

#### 17.10.1 全部先跑白模：`ModelPath` 一律留空（2026-11-10）

**用户决定**（原话）：「先全部用白模即可，占位不要使用"素材"的路径，后续需要挑选让如项目资源文件夹中。」

上一小节的占位配型虽然"诚实标注"了，但本质仍是**用素材库的东西假装成品**——素材库 `素材/`
是美术的原料区，不是项目运行时资源，路径随时会被挪动/清理，指过去就是埋雷
（历史上 `res://素材/Characters/TestHero.glb` 正因如此不存在却静默回退白模，长期无人发现）。

**收敛结果**：

| 项 | 决定 |
|---|---|
| `VisualPacks.ModelPath` | **全部 24 个包一律留空**（`combat.xlsx` 20 个 + `characters.xlsx` 4 个） |
| `TintHex` / `AnimClips` | 一并清空（依赖模型才生效，留着就是死字段，校验器会报错） |
| 包壳（Id 本身） | **全部保留**，只当「桶键」用 —— `Objects.VisualPack` 的 26 条引用一条都不用改 |
| `Scale` | **保留**，等模型回来直接生效 |
| `vp_none` | 语义改为「★ **永远**不要模型」（特效/纯逻辑物件），与「暂时没模型」区分 |

**为什么不把这些包全合并成 `vp_none`**：合并会丢掉「手枪 ≠ 子弹 ≠ 哥布林」的分类信息，
以后挑资产时得重新分类一遍。保留桶键，则回填时**只改 `ModelPath` 一格，其它列不动**。

**校验器口径随之调整**（这条很关键）：原第 ③ 条规则是 `error`——有 Actor/Weapon/Projectile
能力位的物件不许指向无模型的包。全部白模后，26 个物件会全红、整个校验直接失败，等于谁也干不了活；
而彻底删掉它又会丢掉「谁还在等资产」的追踪。**故降为 `[提醒]`**：白模能跑，
同时每个待配模型的物件留一条提醒，回填 `ModelPath` 后自动消失。

> 判断标准：**"暂时没有"要能被看见，"永远没有"要能被声明**。前者是 `[提醒]` 清单，
> 后者是 `vp_none`。两者都不能靠"空着"来表达 —— 这正是 §17.10 整节的立意。

**配套**：`gen_combat_xlsx.py` / `gen_characters_xlsx.py` 里的数据已改为空路径并在注释里写清缘由；
`combat.xlsx` / `characters.xlsx` 由生成器重新产出（不是手改）；校验 **4510 项通过**。

### 17.9 武器触发与弹道运动学（2026-11-05 补齐）

`weapon` / `projectile` 契约在 §17 建立时只是**纸面契约**：`WeaponTrigger` 仅在 `ContentBinder`
里被*解析*、从未被*消费*；`ProjectileAdvanceSystem` 只有一行 `position += velocity * dt`，
于是 `combat.xlsx` 里 `Homing`/`Falling`/`Wave` 三种子弹**全部按直线飞**，`Gravity=9.8`
与 `HomingTurn=rand:180+0.25` 是死数据。本节记录补齐后的形状。

#### 17.9.1 开火：`WeaponTrigger` 有了唯一消费者

| 触发方式 | 行为 | 关键实现点 |
|---|---|---|
| `Auto` | 按住即按 `FireDelay` 节奏连射 | 读 `HeldMask` |
| `Semi` | 边沿触发，按住只打一发 | 只读 `PressedMask`（与 Auto 的唯一区别） |
| `Burst` | 起手边沿，串内间隔由 `BurstInterval` | 串中再次按下不重置 |
| `Charge` | 按住累积 `ChargeElapsed`，松开才发射 | 从未蓄力就松手 = 不发 |
| `Hold` | 按住期间持续生效 | 供光束 / 护盾 |
| `Triggered` / `Manual` | 不响应直接输入 | 返回 `NotInputDriven`，由事件/脚本调 |

**分层**：`WeaponFireRules`（静态纯函数，只做判定）+ `WeaponValues`（`ValueSet` → 标量）+
`WeaponFireSystem`（读组件 → 调规则 → 回写组件 → 投递 `CastRequest`）。
把判定抽成纯函数是为了让"半自动按住只打一发"这类手感断言不必跑整个 `World` 就能验证。

**状态归属**：冷却 `WeaponState.CooldownRemaining` 与蓄力 `ChargeElapsed` 本来就存在于组件中
（此前无人维护），现在由 `WeaponFireSystem` 单一写入；`SkillCastSystem` 写的是 `SkillState`，
两者不争抢（J1）。

#### 17.9.2 弹道：8 种 `ProjectileMotion` 全部落地

| 运动 | 实现 | 依赖 |
|---|---|---|
| `Straight` | 匀速直线 | — |
| `Homing` | 限速转向（罗德里格斯公式，度/秒） | 需目标：`Query<Hurtbox, Transform3>` 最近者 |
| `Orbit` | 绕发射者圆周 | 需 `Source` |
| `Wave` | 沿原始轴前进 + 垂直正弦摆动 | `Age` |
| `Boomerang` | 按 `Age` 权重朝发射者折返 | 需 `Source` |
| `Falling` | 竖直方向叠加 `-Gravity × dt` | — |
| `Spiral` | 轴向推进 + 侧向螺旋外扩 | `Age` |
| `Anchor` | 原地不动（地雷 / 延时区域） | — |

**约定**：①`Velocity` 是派生量，运动学负责写完它，下游只读；②`Gravity` 填**加速度大小**
（表里 `9.8` = 下落 9.8 m/s²），方向由系统固定为 −Y；③依赖发射者/目标的运动在缺少
对应实体时**退化为直线**（不是报错，也不是 NaN）。

**运动参数在发射瞬间定一次**（`ContentSpawner` 写入 `ProjectileState` 的
`Motion/HomingTurn/Gravity/BaseSpeed/Age/LaunchDirection`），而不是每步重算契约值——
否则 `rand:` 随机项会让速度与角度逐帧漂移。

#### 17.9.3 本轮踩到的 bug：struct 执行器写不回结果

`ISpanAction` 的执行器是 **struct，按值传入 `Query.ForEach`**。我最初把追踪扫描的
`Best`/`Found` 存在执行器自己身上，`Execute` 里改的是那份**副本**，调用方读到的
永远是构造时的初值 → `TryFindHomingTarget` 恒返回 `false` → 追踪弹全部退化为直线，
而**所有既有测试仍然全绿**（它们只用 `Straight`）。

修法：把扫描结果存到**系统的字段**上（引用类型，跨调用可见），执行器只持有 `_owner`。
`ProjectileAdvanceSystem` 里另有一处 `PendingDeath` 缓冲用的是同一思路（定长数组 + 计数）。

> 教训与 §17.8 一致：**这类错误不抛异常，只会静默降级**。因此本轮的测试不写"不崩溃"，
> 而是写"**行为必须不同**"——`DifferentMotions_ProduceDifferentTrajectories` 收集 8 种运动的落点，
> 断言至少 6 条互不相同；哪个枚举值退化成直线，它会指名道姓地失败。

### 17.11 「抽象没贯彻到底」全面体检与整改（2026-11-09）

> **触发**：用户加完模型后质问——
> 「我让其他会话添加模型，居然发现只有人物有模型，其他的没有这个字段甚至。
> 这个理论上应该抽象出渲染，里面就可以理所当然地放模型路径了。
> 例如攻击应该是个 C，然后里面放不同的实现即可实现不同的攻击方式。
> 诸如此类的问题必须在项目初期就掐死！这一条抽象规则必须写进硬性 SKILL 中！」

#### 17.11.1 诊断：抽象**不缺**，断的是"谁来兑现"

体检结论与用户直觉略有出入，但**问题比想象的更隐蔽**：

| 层 | 抽象是否存在 | 是否接通 | 说明 |
|---|---|---|---|
| **渲染** | ✅ 在（`View` + `ViewAppearance` 组件、`ViewRegistry` / `VisualBridge` / `TransformBridge` / `ActorViewDirector`） | ❌ **只通了人物一条** | 全项目唯一的 `_viewDirector.Register(...)` 调用在 `CharacterSession.cs:308` |
| **攻击** | ⚠️ 在但**分裂成 4 套** | ❌ 生产跑一条、测试跑另一条 | `FireProjectileSkill`(5 组件) / `ContentSpawner`(5) / `SpawnDirectorSystem`(6) / `EcsObjectFactory` 互不相认 |

**关键证据链**（这条链是本次最值钱的发现）：

1. `combat.xlsx` 的 `Objects` 表，**26 行全部填了 `VisualPack`**；
2. `validate_content_xlsx.py` 还校验了 `ModelPath` 在磁盘上真实存在，**4553 项检查 exit 0**；
3. 但在 `scripts/` 里搜索 `VisualPackId`，**运行期读取点 = 0**（只有 `ObjectArchetype` / `ContentBinder` / `CharacterAssemblySpec` / `CharacterContentSet` 里的**赋值**）。

即：**数据填满了、校验通过了、运行期一个都不读**。
这比"字段缺失"更危险——字段在的时候，所有人（包括 AI）都以为它是通的。

#### 17.11.2 为什么既有防线全都没拦住

| 防线 | 为什么没报错 |
|---|---|
| 编译器 | 组件存在且类型正确，只是没人 `Set` 它 —— 语法完全合法 |
| 架构守卫 GS001~GS007 | 检查的是"文件放对没有"，不是"数据被消费没有" |
| 既有 356 个测试 | 测的是各自系统的局部行为，**没有一个测"产物出生后组件齐不齐"** |
| 数据校验器 | 只验"数据对不对"，不验"数据有没有人用" |
| 代码评审 | 单看每个文件都合理，缺口只在**跨文件的接缝**上 |

**共同特征：每个局部都对，整体是断的。** 这类缺陷只能靠**契约级**的断言抓住。

#### 17.11.3 整改（本轮已落地）

**① 渲染接线：把"谁来写外观"收敛到唯一入口**

`ContentSpawner`（子弹 / 人物 / 特效的**唯一**造实体入口）新增统一填充：

```csharp
private void FillAppearanceBySlot(Entity entity, int objectSlot)
{
    _world.Commands.Set(entity, new View { Slot = 0, Kind = 0, Dirty = true });
    ObjectArchetype? archetype = _index.GetObject(objectSlot);
    int visualSlot = archetype is null ? ContentIndex.NoneSlot : archetype.VisualSlot;
    int defHash    = archetype is null ? 0 : archetype.IdHash;
    _world.Commands.Set(entity, new ViewAppearance
    {
        VisualKey = defHash, VisualSlotIndex = visualSlot,
        PaletteSlot = ContentIndex.NoneSlot, Scale = 1f,
    });
}
```

三条调用路径（`TrySpawnProjectile` / `TrySpawnActor` / `TrySpawnEffect`）现在**无条件**经过它。
新增第四种产物时，只要走这个工厂就自动带齐视觉组件。

**② 修掉一个真实的设计陷阱：`0` 兼做"合法值"与"没配"**

`ObjectArchetype.VisualSlot` 原本默认 `0`，而 `0` 是**合法的外观槽位号**（宿主外观注册表从 0 编号）。
后果：`combat.xlsx` 里没填 `VisualPack` 的物件，与"填了第 0 号外观"的物件，在运行期**完全无法区分**——
表现层只能猜，结果是要么画成别人的样子，要么静默隐身。

改为显式哨兵，与既有的 `NoCluster = -1` / `ContentIndex.NoneSlot = -1` 对齐：

```csharp
public const int UnresolvedVisualSlot = -1;
```

「有意为白模」与「忘了配」从此是**两个可断言的值**，而不是同一个 0。

**③ 新增用例钉住契约**：`tests/Content/Factory/AppearanceCoverageTests.cs`

| 用例 | 断言什么 |
|---|---|
| `EverySpawnedEntity_CarriesViewAndAppearance` | 子弹 / 怪物 / 特效**三种产物都同时带** `View` + `ViewAppearance`，且外观槽位等于内容表里填的值 |
| `MissingVisualSlot_StillCarriesAppearance_MarkedUnresolved` | 内容表没配视觉槽位时，组件**仍然在**，只是槽位为 `-1`（区分"有意白模"与"忘了接"） |

> 守卫点刻意放在**工厂层**而不是渲染层：渲染层本来就是通用的（组件里有槽位就能画），
> 漏的永远是"谁往组件里写字"。

**④ 规则升格为硬约束**：`docs/rules/03-abstract-first.md` 新增 §3.4「概念的唯一定义权」与 §6.1「契约完备四问」，
`AGENTS.md` 规则 5 判据同步扩充，红线清单新增第 11~15 条。四条硬规则：

| # | 规则 | 判据 |
|---|---|---|
| A | **单一契约** | 「这个概念有几个地方在造？」必须是 1 |
| B | **齐备原则** | 「新加一种，它带的东西和老的完全一样吗？」 |
| C | **消费闭合** | 「这列谁在读？」只有生成器/校验器读过不算 |
| D | **成员闭合** | 「这几支谁处理？」静默 `default: return false;` 禁止 |

#### 17.11.4 如实说明：本轮**没有**做完的部分

以下问题**真实存在但本轮未修**，已登记，不得当作已完成：

1. **子弹仍有 4 套平行组件集合**（`FireProjectileSkill` / `ContentSpawner` / `SpawnDirectorSystem` / `EcsObjectFactory`）。
   本轮只统一了"视觉填充"，**运动/伤害/生命周期仍是各写各的**。这是最大的遗留项。
2. **`WeaponState` 是死组件**：`scripts/` 里从无构造，只在 `tests/Combat/WeaponFireSystemTests.cs:109` 出现。
   ⇒ 生产环境跑的是 `ProjectileSystem`，测试测的是 `WeaponFireSystem` + `ProjectileAdvanceSystem`，**两条不同的管线**。
3. **枚举空转**：`WeaponAimKind` 11 个成员、`ProjectileHitPolicy` 8 个成员，**运行期消费者均为 0**。
4. **静默吞分支**：`EcsObjectFactory.OnExpire` 的 `default: return false;` 把 `Explode` / `Inherit` 两类结果静默丢弃。
5. **硬编码数值**：`FireProjectileSkill.cs:202` 写死 `DamageType.Kinetic`，违反规则 4。
6. **`ActorAssemblySystem.cs:390` 覆写 `VisualSlotIndex`**，可能与工厂写入值冲突，需下一轮核对。
7. **`DefaultRoomContentSpawner` 设 `View` 但不设 `ViewAppearance`** —— 与本轮修的同类缺口，属另一条路径。
8. **武器/子弹在 3D 里仍不渲染**：本轮只保证"组件齐备"，**没有接表现层的实际绘制**。
   另按 v12 决定，`ModelPath` 当前全部留空（统一白模），接线完成前看不到模型属预期行为。

---

## 18. 第一个可玩人物（`test_hero`，2026-11-05 落地）

> 本章回答：**「在 ECS + 角色抽象之上，怎么把一个人物真正做出来」**——
> 数值从 Excel 来、能力靠模块拼接、操作走意图、表现走桥接，
> 整条链路没有一处「为这个人物特判」的代码。

### 18.1 一句话结论

**人物 = 数据行 × 模块集合。** 换一个变体（`test_hero` → `test_hero_tank`）只改 `characters.xlsx`
里的一行，C# 侧 **0 行改动**；四个变体（标准 / 重甲 / 敏捷 / 幸运）共用同一套系统。

### 18.2 分层落点（谁在做什么）

| 层 | 落地物 | 位置 | 职责边界 |
|---|---|---|---|
| 数据（真源） | `Characters` / `StatPacks` / `SkillLoads` / `Assemblies` / `VariantAssembly` / `VisualPacks` / **`CameraPacks`** / `InputPacks` / `Weapons` 九张表 | `data/excel/characters.xlsx` | 策划唯一编辑区 | 策划唯一编辑区；`CameraPacks` 的镜头手感（跟随收敛速率 / 瞄准偏移幅度与上限）也在这里 
| 数据（运行时镜像） | `CharacterContentSet`、`CharacterAssemblyRepository`、`CharacterVariantSpec`、`CharacterWeaponSpec`、`CharacterSkillSpec`、`CharacterModuleSet` | `scripts/data/` | 只做**读取与校验**，不认识技能实现类 |
| 组件 | `Transform3` `Team` `Health` `Mana` `Hurtbox` `ActorIdentity` `SkillState` `ActorIntent` `MoveIntent` `AimTarget` `ViewAppearance` `WeaponStats` `ActorTag` | `scripts/actor/`、`scripts/combat/` | 全是 `unmanaged` 值类型，只存**槽位号/数值**，不存对象引用（J2） |
| 系统 | `ActorIntentSystem`(PreUpdate) → `ActorAssemblySystem` → `SkillCastSystem` → `MoveAimSystem` / `AimAtCursorSystem` → `ProjectileSystem` / `HitDetectionSystem` / `ImpactEventSystem`(FixedUpdate) | `scripts/actor|skill|combat/` | 每帧纯函数式推进，不碰引擎 |
| 组合根 | `ContentSkillSet`（数据行 → 真技能对象）、`ContentSkillFactory`（`kind` → `ISkill`）、`ISkillFactory`/`NullSkill` | `scripts/host/composition/` | **唯一**知道「`fire_projectile` 意味着哪个类」的地方；刻意不 `using Godot`，所以测试工程能直接编译它 |
| 宿主 | `CharacterSession`（一个会话 = 一条管线 + 一个玩家 + 一套视图/HUD）、`DungeonDebugScene`（场景入口） | `scripts/host/` | 唯一允许 `using Godot` 的边界（J4） |
| 视图桥 | `VisualBridge` / `TransformBridge` / `ViewRegistry` / `ActorViewDirector` / `TopDownCameraRig` | `scripts/host/view/`、`scripts/host/bridge/` | ECS 组件 → 引擎节点，单向 |
| 输入 | `PlayerInputIntentSource`（键位 → `InputIntent` Q16 定点）、`CursorAimInputSource`（鼠标 → 世界点） | `scripts/host/control/` | 引擎输入只在这里出现 |
| UI | `HudModel` / `HudSnapshot` / `CharacterHudDrawer` / `CharacterHudSkin` | `scripts/ui/` | 只读快照 + 绘制，**不做结算**（红线 5） |

### 18.3 一帧的数据流（人物视角）

```
键盘/鼠标 ──► PlayerInputIntentSource ──► InputIntent（Q16 定点）
                                        │
CursorAimInputSource ─► AimTarget ──────┤
                                        ▼
                         ActorIntentSystem (PreUpdate)   意图 → ActorIntent 组件
                                        ▼
                         ActorAssemblySystem             ActorSpawnRequest → 12 个组件一次性拼好
                                        ▼
                         SkillCastSystem                 边沿判定 → CanCast → Cast → 扣法力 → 起冷却
                                        ▼  ICommandSink（帧末提交，J5）
                         ProjectileSystem / HitDetectionSystem (FixedUpdate)
                                        ▼  事件
                         AppEvents ──► HUD 快照 / 音效 / 飘字
                                        ▼
                         TransformBridge（插值）──► Godot 节点
```

### 18.4 四条关键接线（也是四个真实踩过的坑）

| # | 接线 | 踩的坑 | 现在的钉子 |
|---|---|---|---|
| ① | 玩家实体 discovery | `_stats.TryGetOwnerOfDefinition` 只由 `BindStats` 填充，而 `BindStats` 又依赖这个查询 → **死亡循环，人物永远不出生** | `ResolvePlayer()` 改读 `ActorAssemblySystem.LastAssembled[^1]`，再回调 `BindStats` |
| ② | 技能包**槽位号** | 宿主注册表只 `Register` 英雄自己那一个包 → 它的槽位是 0；而装配系统按 `Content.Loadouts`（含 `player_kit`/`goblin_kit`）解析出 5 → `SkillCastSystem` 每帧查不到包，**按左键永远不开枪、不报错** | 先镜像整个仓库保住槽位号，再 `SkillLoadoutRegistry.Replace` 原地换真实现；用例 `RuntimeSkillRegistry_KeepsContentSlotNumbers` |
| ③ | 技能种类绑定 | 数据层只能放 `DataSkill` 占位（它不许 `using Game.Skill`）→ 左键按下、冷却照走、法力照扣，**一颗子弹都没有** | `ContentSkillFactory` 按 `kind` 造 `FireProjectileSkill`；用例 `EndToEnd_PressingPrimarySpawnsRealProjectileFlyingDownAimDirection` |
| ④ | 意图**覆盖方向** | `ActorIntentSystem` 每帧用意图源采样**整体覆盖** `ActorIntent`；在管线前直接写组件会被静默抹掉 | 冒烟/回放一律走意图源的 `ForceInput` 通道（伪造一次采样），下游全走真实路径 |

> ①~④ 的共同点：**都是"不崩、不报错、日志干净，但功能静默失效"**。
> 这类问题只能靠跨层断言抓，不能靠肉眼跑一遍。

### 18.5 数值与变体（全部来自 Excel）

| 变体 | HP | 护甲 | 速度 | 暴击 | 武器 | DefinitionSlot |
|---|---|---|---|---|---|---|
| `test_hero`（测试人物·标准） | 120 (+10 成长) = 130 | 6 | 5.2 | 5% ×1.75 | `pistol_basic` 1101 | 5 |
| `test_hero_tank`（重甲） | 220 | 20 | 3.6 | — | `cannon_heavy` 1106 | 6 |
| `test_hero_agile`（敏捷） | 90 | — | 7.4 | — | `smg_light` 1107 | 7 |
| `test_hero_lucky`（幸运） | 110 | — | 5% | 15% ×2.0 | `ricochet_lucky` 1108 | 8 |

**护甲减伤口径（HUD 与战斗必须一致）**：`armor / (armor + armorK)`。
`test_hero` = 6/(6+100) ≈ **6%**（曾写成 `1 - armor/(armor+k)` → 显示 94%，已修正）。

**五槽技能（`SkillSlot`）**：`Dash`(Shift) / `PrimaryAttack`(左键) / `WeaponSwap`(Q) / `Signature`(R) / `Interact`(F)。
v1 只有左键是**真实现**（`fire_projectile`）；其余四行是 `kind=data` 的占位，
宿主日志会如实打印「跳过了 4 行」，而不是假装能用。

### 18.6 扩展成本（规则 7 实操核对）

| 我想做 | 要改的文件 | 数量 |
|---|---|---|
| 加一个变体（换血/换速/换枪） | `characters.xlsx` 加一行 | **1** |
| 让变体换一套技能包 | `characters.xlsx` + `SkillLoads` 加 5 行 | **1** |
| 加一种新技能种类（如 `cone_slash`） | `ISkill` 实现 + `ContentSkillFactory` 一个 `case` | **2** |
| 加一个新 HUD 字段 | `HudSnapshot` 加字段 + `CharacterHudDrawer` 画它 | **2** |
| 人物换外观白模 | 0（`VisualPack` 数据行指向视觉槽位） | **0** |

### 18.7 验证（可复现）

```powershell
dotnet build                                          # 0 error / 0 warning
.\tools\check_architecture.ps1                        # new=0 existing=0 fixed=0 errors=0 warnings=0
cd tests; dotnet test                                 # 219/219 通过

# 无渲染冒烟：出生 + 出弹链路（headless 没有鼠标，故用 --hero-fire 伪造一次采样）
E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe --path . --headless --quit-after 150 -- --hero --hero-fire
#   期望日志： [Hero] 出生 entity=... 血量上限=130 / 实体数随射击增长 / 施放=5

# 带截图的真渲染：
... --path . --quit-after 200 -- --hero --hero-fire --shots
#   out/dungeon/hero_closeup.png  ← 角色特写（HUD + 玩家 + 子弹）
```

### 18.8 已知债务（不在本章解决）

1. `SpawnDirectorSystem` 给每颗子弹多挂一个 `View` 组件（组件种类上限已由 16 抬到 64，故未爆但仍在浪费）。
2. 交叉准心只有一处（HUD Drawer 画的屏幕中心），`ViewOptions.CrosshairVisible` 默认 false。
3. Excel ↔ C# 镜像的一致性**没有**自动化 `--verify`（靠 9 条回归用例钉住）。
4. 四个 `kind=data` 技能仍是 `NullSkill` 占位（**冲刺已实现，见 §19**；剩切武器 Q / 大招 R / 交互 F 三个）。
5. `Behavior/Action/Control` 三类 pack 只有数据、无系统消费（BehaviorSystem / spawn director 未落地）。
6. Spreadsheet 是手写生成脚本 `tools/data_pipeline/gen_characters_xlsx.py`，尚未接 `data/generated/`。

---

## 19. 冲刺技能（`dash`，2026-11-06 落地）

### 19.1 一句话结论

冲刺不是一个"技能类内部存计时器"的小功能，而是一条**完整的 ECS 能力链**：
数据行声明 `kind=dash` → 工厂造 `DashSkill` → 施放往命令缓冲排一条「给施放者挂 `DashState`」→ 帧末 `Flush` 真正挂上
→ `DashSystem` 在 `FixedUpdate` 里把 `Velocity` 顶成冲刺速度并倒计时 → 到期摘组件、速度归零。
**状态属于实体，不属于技能实例** —— 这是"同一技能包拼出四个变体"能成立的前提。

### 19.2 分层落点

| 层 | 文件 | 职责 |
|---|---|---|
| `Game.Core` | `StatId.DashSpeedMul = 1055`、`StatId.DashDuration = 1056` | 两个数值槽（追加区，不动既有号段） |
| `Game.Core` | `Skills/ICommandSink.Add<T>` | **门面扩展**：技能给已存在实体挂组件。带默认实现，已有 sink 实现 0 改动 |
| `Game.Actor` | `ActorComponents.DashState` | 冲刺的"定格值"：`Remaining` / `Duration` / `Direction` / `Speed` |
| `Game.Actor` | `DashSystem`（`[Module("dash_impulse")]`, `FixedUpdate`） | 顶速度 + 倒计时 + 到期清理 |
| `Game.Skill` | `DashSkill : ISkill` | 预检 + 施放（只发命令，不碰世界结构） |
| `Game.Data` | `CharacterSkillSpec.KindDash` + `CharacterSkillSpecs.KnownKinds` | 数据层的种类词表 |
| `Game.Host` | `ContentSkillFactory.KindDash` 分支 | 「种类 → 实现类」的唯一定义点 |
| `Game.Host` | `CharacterSession` 注册 `DashSystem` | 组合根接线 |
| 数据 | `characters.xlsx` `SkillLoads.Kind` + `StatPacks` 两行 | 唯一的真源 |

### 19.3 为什么这样切（三个真实的设计取舍）

**① 状态放组件，不放技能字段。**
`SkillLoadout` 里的 `ISkill` 实例是**被多个角色共享**的（一个技能包可以拼给任意变体）。
若把 `Remaining` 存在 `DashSkill` 的字段上，两个角色会互相覆盖计时器 —— 恰好毁掉规则7 的「拼接」能力。
放组件后每个实体各自持有自己的冲刺状态，天然隔离。

**② 方向与速度在开冲那一刻定格。**
`DashState.Direction` / `Speed` 是快照，`DashSystem` 每帧只按它铺速度、**不重读瞄准**。
否则鼠标抖动会让冲刺中途拐弯，手感变成"在冰上开车"。
方向来源优先级：**移动意图 > 瞄准方向** —— 跑着按 Shift 顺着跑的方向加速（这是玩家想要的）；
站着按则朝准心闪。速度 = `MoveSpeed × DashSpeedMul`，倍率让敏捷型自然冲得更远，不必逐变体填绝对值。

**③ `CanCast` 与 `Cast` 都要门控。**
契约规定 `SkillCastSystem` 先 `CanCast` 再 `Cast`，但 `Cast` 是公开 API（测试、AI、将来的连招系统都会直接调）。
第一次实现时 `Cast` 只做了"数据有效性"检查，结果**冲刺中再施放会叠加一层 `DashState`**：
速度翻倍、`Remaining` 重置成满值 = 无限速度。测试 `CanCast_WhileAlreadyDashing_IsRejected` 抓住了它。
结论：**门控必须由能力自己兜底**，不能依赖调用方守规矩；`Cast` 开头再问一次 `CanCast`，并按原因返回
`Cooldown` / `NoMana` / `TargetInvalid`。

### 19.4 一帧里的顺序（为什么 DashSystem 必须在 MoveAim 之后）

```
PreUpdate    InputIntentSource → ActorIntentSystem → SkillCastSystem(CanCast→Cast→Commands.Add<DashState>)
FixedUpdate  MoveAimSystem     写 Velocity（普通走动）
             DashSystem        ← 若身上有 DashState，覆盖 Velocity 为冲刺速度
             MovementSystem    Position += Velocity * dt
CommandFlush Commands.Add<DashState> 落地 → 下一帧 DashSystem 才看得见
```

`DashSystem` 与 `MoveAimSystem` 的分工是**严格单向**的：MoveAim 读意图写速度，Dash 在它之后把速度顶掉。
冲刺结束时**不需要任何复位代码** —— MoveAim 自然重新接管。但必须做一件事：
**把 `Velocity` 清零再摘组件**，否则 `MovementSystem` 会继续按冲刺速度积分，角色像踩在冰上滑出好几米。
这一条也是测试逼出来的：端到端位移 2.86m vs 理论 2.06m，多出来的 0.8m 就是滑行。

### 19.5 数据（真源 `data/excel/characters.xlsx`）

`SkillLoads`：1001 号冲刺行的 `Kind` 从 `data` 改为 `dash`（6 个技能包同时受益：4 个 hero 变体 + `player_kit` + `chosen_kit`）。
冷却/消耗仍是各自的行值（hero 1.2s / 8 法力）。

`StatPacks` 每变体追加两行：

| 变体 | `DashSpeedMul` | `DashDuration` | 冲刺距离（移速 × 倍率 × 时长） |
|---|---|---|---|
| `test_hero`（标准） | 2.2 | 0.18s | 5.2 × 2.2 × 0.18 ≈ **2.06 m** |
| `test_hero_tank`（重甲） | 1.8 | 0.22s | 3.6 × 1.8 × 0.22 ≈ **1.43 m** |
| `test_hero_agile`（敏捷） | 2.8 | 0.16s | 7.4 × 2.8 × 0.16 ≈ **3.32 m** |
| `test_hero_lucky`（幸运） | 2.4 | 0.18s | 5.4 × 2.4 × 0.18 ≈ **2.33 m** |
| `chosen_base`（怪物天选者） | 2.0 | 0.20s | 4.8 × 2.0 × 0.20 ≈ **1.92 m** |

**同一个 `DashSkill` 类、同一行技能数据，四个变体冲出四种手感** —— 这是 §18.6「加变体只改 1 个文件」
在"能力"维度上的兑现：冲刺参数没有新增任何 Excel 列。

### 19.6 扩展成本（规则 7 实操核对，本次实测）

| 我想做 | 要改的文件 | 数量 |
|---|---|---|
| 让某个变体冲得更远 | `characters.xlsx` 改一个 `DashDuration` 数值 | **1** |
| 给新变体加冲刺 | 技能包 1 行（`Kind=dash`）+ 数值包 2 行 | **1** |
| 加一种新的"有时限状态"技能（如翻滚无敌帧） | 新 `ISkill` + 新 `SystemBase` + `ContentSkillFactory` 一个 `case` | **3** |
| 既有的 sink / 技能实现 | **0 行改动**（`ICommandSink.Add` 带默认实现） | **0** |

### 19.7 验证（可复现）

```powershell
dotnet build                                          # 0 error
.\tools\check_architecture.ps1                        # new=0 existing=0 fixed=0 errors=0 warnings=0
cd tests; dotnet test                                 # 357/357 通过（新增 14 条冲刺用例）
```

14 条用例分四组：

| 组 | 用例 | 钉住什么 |
|---|---|---|
| 数据/工厂 | `DataRow_TestHeroKit_DashSlotIsKindDash`、`Factory_KindDash_ProducesRealDashSkill`、`KnownKinds_IncludesDash`、`AllFourVariants_HaveUsableDashStats`、`TwoVariants_DashDifferently_FromOneSharedSkillInstance` | 数据行真的指向 `dash`；四个变体数值齐全且互相有区分度 |
| 释放 | `Cast_PutsDashStateOnCaster_NotOnSkillInstance` | 状态落在实体上、时长/速度来自数值包 |
| 位移 | `DashSystem_MovesEntityAlongDashDirection`、`DashSystem_EndsAndRemovesComponent_AtConfiguredDuration`、`DashSystem_IsNoOp_WhenNobodyIsDashing` | 真的走了、到点就停、没人冲时零副作用 |
| 门控 | `CanCast_WhileAlreadyDashing_IsRejected`、`CanCast_OnCooldown_IsRejected`、`CanCast_InsufficientMana_IsRejected`、`CanCast_WithoutDashStats_IsRejected_NotSilentlyZeroSpeed` | 四种拒绝路径都不排命令 |
| 端到端 | `EndToEnd_CastThenSystem_ProducesExpectedDisplacement` | 位移 ≈ `MoveSpeed × DashSpeedMul × Duration`（±30%） |

无渲染冒烟（实测输出）：

```powershell
E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . -- --hero --hero-fire
# [Hero] 技能包 test_hero_kit 实例化 5 个真技能（工厂=ContentSkillFactory）
# [DungeonDebug] 帧 540 ... HP=130/130 法力=80 护甲减伤=6% 施放=24
# [DungeonDebug] 退出：帧=600 实体=129
```

### 19.8 本次踩到的坑（供后来者少走）

1. **`AddModule` 强制 `[Module]` 声明**：系统进模块必须在类型上写 `[Module("...")]`，
   否则 `AddModule` 直接抛 `GameEcsException`。忘了它，Godot 侧的表现是 `_Ready` 抛异常后
   **场景空转、进程不退出**（不是崩溃，看起来像"卡住"）—— 排查时一定要看 stderr。
2. **`Query.ForEach<T0,T1>` 的 lambda 歧义**：`SpanAction` 委托重载与 `in ISpanAction` 重载同时存在，
   裸 lambda 被解析到后者并报 CS1503。修法：让系统类 `implements ISpanAction<T0,T1>`，
   构造时 `_step = this;`，稳态零分配（与 `ProjectileSystem` 同一手法）。
3. **`ICommandSink` 曾无法"给已存在实体挂组件"**：只有 Spawn/Destroy，没有 Add。
   这是"技能给自己挂一个有时限状态"的真实能力缺口，不是测试障碍 —— 用 default interface method 补上。
4. **Excel 与 C# 镜像必须同步**：改了 `gen_characters_xlsx.py` 却没改 `CharacterContentSet.cs`，
   `CharacterXlsxParityTests` 会逐字段报错（本次正是靠它发现 `chosen_base` 少两项）。

---

## 20. 标签轴：以「轴」为范围索引内容（2026-11-06 落地）

### 20.1 为什么要多加一层「轴」

原有标签只有 `TagId / TagIndex / Category / Group / BitMask`：一个物体身上挂十几个 tag，
但代码想问的问题**从来不是**「哪些物体挂了 `tag.pierce`」这种具体标签，而是：

- 强化要「所有**稀有**武器」→ 稀有的都有哪几个？表说了算
- 图鉴要按「**元素** / **能力** / **族裔**」分栏
- 元素抗性要「对**动能**类伤害减免」→ 动能类包含哪些
- 掉落要「**Rarity=common** 的候选池」

如果每个玩法自己去拼具体标签名，`tag.rare` 就会硬编码散落在 C# 各处 —— 新增一个稀有度等级要改 N 个文件，
违反规则 7。**轴（Family）就是把这些范围查询从「字符串拼装」变成「一次按位与」的那层索引。**

一句话：**物体是点，标签是坐标值，轴是坐标系。**

### 20.2 十个轴（`TagFamily`，Excel `Tags.Family` 列）

| 轴 | 语义 | 互斥 | 例 |
|---|---|---|---|
| `Kind` | 物体大类，**同时决定能力位** | ✅ | `tag.actor` / `tag.weapon` / `tag.projectile` |
| `WeaponKind` | 武器种类抽料 | ✅ | `tag.pistol` / `tag.rifle` / `tag.bow` / `tag.melee` |
| `ProjectileKind` | 弹体种类（选弹道运动学实现） | ✅ | `tag.homing` |
| `Element` | 伤害元素（抗性 / 元素联动） | ✅ | `tag.kinetic` / `tag.energy` / `tag.explosive` |
| `Ability` | **能力，可叠加轴** | ❌ | `tag.pierce` / `tag.bounce` / `tag.split` / `tag.summon` |
| `Trigger` | 触发时机（挂效果回调） | ✅ | `tag.volatile` |
| `Rarity` | 稀有度（掉落权重 / 图鉴配色） | ✅ | `tag.common` / `tag.uncommon` / `tag.rare` / `tag.legendary` |
| `Race` | **族裔，分层轴**（祖孙共存） | ❌ | 祖先 `tag.humanoid` ⊃ 具体 `tag.goblin` / `tag.slime` / `tag.ogre` |
| `Role` | 角色定位（波次编排 / AI 选型） | ✅ | `tag.hero` / `tag.grunt` / `tag.elite` / `tag.caster` |
| `Effect` | 特效类别（表现层选特效） | ✅ | `tag.muzzle` / `tag.hit` / `tag.burst` |

**两个「不互斥」是刻意的设计决定，不是遗漏：**

1. `Ability` 不互斥 —— 一颗弹同时「穿透 + 弹跳 + 分裂」正是 ECS 组合的主战场。
   一旦把它做成互斥，组合爆炸又退回成分类枚举。
2. `Race` 是分层轴 —— `tag.humanoid` 是祖先、`tag.goblin` 是具体。
   于是「对人形增伤」能打到哥布林，「对哥布林增伤」只命中哥布林，两条规则靠同一套索引都能写出来。
   若强制互斥，就得给每个族裔复制一份人形标记，祖先关系彻底丢失。

### 20.3 运行期形状

```text
combat.xlsx / Tags
  TagId     TagIndex   Family       Category  Group        BitMask
  tag.pierce   11      Ability      Kind      Ability      11
        │                     │
        │                     └─ 轴：索引坐标系（新增，本任务）
        └─ Group：能力位来源（原有，只认 ObjectKind）
              ↓ ContentBinder.LoadRules
ContentRules
  · TagNames / TagOf(name) / NameOf(id)          ← 原有
  · FamilyOf(id) → TagFamily                     ← 新增
  · TagsOfFamily(TagFamily) → IReadOnlyList<int> ← 新增：轴 → tag id 列表
  · FamilyMask(TagFamily, word) → int            ← 新增：整轴压成一个位掩码
  · PackMask(tags, word) → int                   ← 新增：条件「这几个之一」压成掩码
              ↓
ObjectBitMatrix  （行 = 物件槽位，位 = tag 下标）
              ↓
按轴筛选 = (matrix.Mask(row, 0) & rules.FamilyMask(TagFamily.Rarity)) != 0
                                    ↑ 一次 AND，不认识任何具体 tag 名
```

**热路径代价**：一次 `int` 与。`FamilyMask` 在加载期算好后可以直接缓存，逐帧零成本。
**零分配**：`TagsOfFamily` 返回加载期就建好的数组副本（`IReadOnlyList<int>`），不逐帧 new。

### 20.4 关键约束（三条）

1. **`Family` 只做索引，不改变能力位。** 能力位仍然只看 `Group`（`ObjectKind` → actor/weapon/projectile）。
   这样标签重构不会动到工厂的 `ObjectCapability` 判定，风险面被限制在「查询」一侧。
2. **绑定器对认不出的轴名当场报错**（不静默落 `None`）。静默落 None 会让这个 tag
   从所有按轴索引的玩法里消失 —— 这是最难查的一类失效，必须在加载期炸出来。
3. **位矩阵的 32 位上限与轴无关。** 只有需要「进配方字」的 tag 才占 `BitMask`（现有 16 个，bit 0~15）；
   轴标签可以远超 32 个（当前 40 个，只有 16 个有位）。轴索引走 `TagIndex` 的位，
   不受 `ContentBitLimits.TagBits` 约束。

### 20.5 扩展成本（规则 7 实操核对）

| 我想做 | 要改的文件 | 数量 |
|---|---|---|
| 加一个 tag（如「毒元素」） | `combat.xlsx` 的 `Tags` 加一行（`Family=Element`） | **1** |
| 加一个新轴（如「阵营」） | `Tags.Family` 枚举 + `TagFamily` 枚举 + 校验器词表 | **3** |
| 按新轴写一条强化 | 0（`FamilyMask` 直接可查） | **0** |
| 给物体补标签 | `combat.xlsx` 的 `Objects` 改一格 | **1** |

> 与 §17 的判据一致：**新增内容只改表**；只有新增**轴**这一级概念才要改代码，且是 3 处枚举。

### 20.6 验证（可复现）

```powershell
dotnet build                                          # 0 error
cd tests; dotnet test                                 # 354/354 通过（含 11 条 TagFamilyTests）
python tools/data_pipeline/validate_content_xlsx.py   # OK: 3258 项检查通过
```

`tests/Content/Data/TagFamilyTests.cs` 的 11 条用例分两类：
**合成用例**钉索引算法与边界（大小写、未知轴、互斥表、越界、掩码打包）；
**真实表用例**读 `combat.xlsx` 钉「表 → 运行期」链路 ——
后者才能证明 `Family` 列真的被读进来了（列名写错、读错列号都是静默失效）。
其中 `Binder_ReadsFamilyColumnFromRealWorkbook` 断言**真实表里没有任何 tag 落在 `None` 轴**，
这条断言就是 `Family` 列断链的报警器。

### 20.7 已知债务

1. `Category` 列（Kind/Stat/Element/Ability）与 `Family` 存在**语义重叠**：`Category` 是早期的粗分类，
   现已被 `Family` 全面覆盖。保留是为了不破坏老列；**新内容一律只填 `Family`**，`Category` 可视为历史遗留。
2. 轴目前只用于查询，**还没有消费方**（强化系统 / 图鉴 / 掉落池都未落地）。
   本章先把索引口径钉死，后续系统直接 `FamilyMask` 取范围，不再各自拼字符串。
3. `TagFamilies.Names`（C#）与校验器 `TAG_GROUP_TO_FAMILY` 是两处独立词表，
   靠 `Binder_ReadsFamilyColumnFromRealWorkbook` 与校验器的 `check_tag_families` 双向钉住。

### 20.8 本轮踩到的两个静默失效（已修，记录以防复发）

1. **校验器按列号取 `BitMask`。** 旧实现写死 `c[4]`（第 5 列）。`Family` 列插进第 3 列后，
   `c[4]` 变成 `Category` 列，于是所有 tag 的 `BitMask` 都被读成 -1 → 30 条「配方里的 tag 没有配方位」。
   **已改为按字段名取**（`t.cell(c, "BitMask")`）。教训：四行表头的列结构必须按**名字**索引。
2. **重跑生成器冲掉手改值。** `gen_combat_xlsx.py` 的 `TAGS` 里 `BitMask` 曾写成全 -1，
   有人重跑它覆盖了 `combat.xlsx`，把 bit 0~15 的配方位全部清零。
   **已把历史配方位写回生成器**，使「生成器产物 == 手改表」；并新增
   `tools/data_pipeline/fix_tag_bitmasks.py` 做幂等回填。
   > ⚠️ **纪律**：`combat.xlsx` 是**手改真源**，重跑 `gen_combat_xlsx.py` 会整体覆盖它。改表请直接改 xlsx。

## 21. 标签分层：逻辑功能层 vs 标识层（2026-11-06 落地）

### 21.1 为什么要分两层

§19 给所有内容加了「轴」做范围索引。但两个**用途完全不同**的东西被混在了同一张表里：

| | **逻辑功能层**（`Logic`） | **标识层**（`Identity`） |
|---|---|---|
| 问什么 | 它**能做什么** | 它**属于谁 / 吸引谁** |
| 典型标签 | `tag.kinetic` / `tag.pierce` / `tag.rare` / `tag.actor` | `tag.loot_coin` / `tag.attract_magnet` / `tag.own_player` |
| 谁消费 | 系统（能力位、配方字、抗性、权重） | 匹配（吸附、拾取、主题、共鸣） |
| 加一个的代价 | **要先想规则**（可能牵动结算） | **随手加**（不影响任何判定） |

混在一起时，看到 Tags 表有 80 行没法判断「哪些能随便加」。分层之后这就是一条可执行的纪律：
**`Logic` 行加之前先想规则；`Identity` 行随便加。**

> 层（Tier）与轴（Family）是两个维度：**轴说「哪一类」，层说「能不能影响玩法」。**
> `tag.kinetic` 是 `Logic` 层的 `Element` 轴；`tag.loot_coin` 是 `Identity` 层的 `Loot` 轴。

### 21.2 标识层的六个轴

| 轴 | 互斥 | 语义 | 谁会用 |
|---|---|---|---|
| `Loot` | ✅ | 拾取类别：弹药 / 回血 / 回蓝 / 金币 / 遗物 / 钥匙 | 吸附半径、磁铁筛选、自动拾取白名单 |
| `Attract` | ❌ **可叠加** | 被哪些磁场吸引：磁铁 / 武器吸附 / 经验吸附 | **「道具互相吸引」的匹配机制** |
| `Faction` | ✅ | 归属方：玩家 / 敌方 / 中立 | 同阵营高亮、掉落归属、UI 分组 |
| `Theme` | ❌ 可叠加 | 主题：森林 / 地牢 / 熔炉 | 章节化随机、皮肤 |
| `Aura` | ❌ 可叠加 | 光环 / 共鸣标记：火 / 冰 / 电 | 元素共鸣、熔炉链 |
| `Craft` | ❌ 可叠加 | 原料类别：金属 / 木材 / 宝石 / 精华 | 合成配方筛选 |

### 21.3 「道具互相吸引」怎么实现

这是标识层最典型的用法 —— **代码不认识任何具体名字，只做一次按位与**：

```csharp
// 被吸物 (例如金币) 挂: tag.loot_coin, tag.attract_magnet
// 吸引源 (例如磁铁道具) 挂: tag.attract_magnet
int word        = magnetTag >> 5;                    // 标识标签在 tag 32+，落在 word 1
int magnetMask  = ContentRules.PackMask(new[] { magnetTag }, word);
bool attracted  = (lootMatrix.Mask(loot, magnetTag) & magnetMask) != 0;
```

**新增一种磁铁**：表里加一行 `Identity / Attract` 的标签，给物件的 `Attract` 打上 → **C# 0 行改动**。

### 21.4 硬约束（机器保证，不是约定）

`check_tag_tiers`（校验器）+ `ContentBinder.LoadRules`（运行期）**双向**保证标识层不越界：

| 约束 | 为什么 |
|---|---|
| 标识标签 `BitMask` **必须 -1** | 否则它进了熔炉配方字 = 偷偷影响玩法 |
| 标识标签 `Group` **必须 `None`** | 否则它产生能力位 = 被工厂当成物件能力 |
| `Tier` 与轴的层映射必须一致 | 防止「挂标识轴却声明成 Logic」的半截状态 |
| 认不出的 `Tier` 值**当场报错** | 静默落 `None` 会让这行从所有按层筛选里消失 |

`TagGroup` 词表新增了 `None` 成员专门承载这一点。

### 21.5 运行期 API

```csharp
rules.TierOf(tag)              // tag → TagTier
rules.TagsOfTier(TagTier.Identity)   // 层 → tag id 列表
rules.TierMask(TagTier.Identity, word) // 整层压成一个位掩码
TagFamilies.TierOf(TagFamily.Loot)     // 轴 → 它属于哪一层
```

### 21.6 扩展成本（规则 7 实操核对）

| 我想做 | 要改的文件 | 数量 |
|---|---|---|
| 加一个标识标签（新主题/新拾取类别） | `combat.xlsx` 加一行（`Tier=Identity`） | **1** |
| 加一种磁铁 | 同上，再给物件 `Tags` 打上 | **1** |
| 加一个标识轴 | 3 处枚举（Excel 词表 / `TagFamily` / 校验器 `IDENTITY_FAMILIES`） | **3** |
| 按新标识轴写吸附逻辑 | 0（`FamilyMask` 直接查） | **0** |

### 21.7 验证

```powershell
cd tests; dotnet test                                 # 358/358 通过（15 条 TagFamilyTests）
python tools/data_pipeline/validate_content_xlsx.py   # OK: 3540 项检查通过
```

关键用例：`RealWorkbook_TierAgreesWithAxis`（层↔轴双向自洽）、
`RealWorkbook_IdentityTagsNeverAffectGameplay`（标识层不影响玩法）、
`AttractMask_IsTheMagnetMatchingMechanism`（吸引机制端到端）。

### 21.8 踩到的坑（记录以防复发）

1. **`TagId` 只允许 `tag.<小写名>` 单段**，写成 `tag.loot.ammo` 会被校验器判非法。
   标识标签一律用**扁平下划线**：`tag.loot_ammo` / `tag.attract_magnet` / `tag.own_player`。
2. **`ObjectBitMatrix.Mask(row, tag)` 第二个参数是 tag 下标，不是 word 下标。**
   标识标签 id 都 ≥ 40，落在 `word 1`；但传参时仍要传 **tag id**，内部换算。
   这也是「轴索引不受 BitMask 32 位上限约束」的直接证据 —— 62 个 tag 已经跨了两个 word。

## 22. 核心内容集：5 人物 / 5 怪物 / 5 武器 / 5 子弹 + 图鉴原型（2026-11-06 落地）

### 22.1 这一节解决什么

用户最初的需求是「**5 武器 / 5 子弹 / 5 怪物 / 5 人物，发挥 ECS 组合机制**」，并要求
「**只设置数值，不做场景**」（场景地编尚未就绪）。此前工程里武器 8 把、子弹 7 种已够数，
但**怪物只有 3 种**、**人物 4 行是同一模板的数值变体（机制上无差异）**。

本节补齐这些缺口，并把「5 个」做成**机制各异**的组合，而不是同一套数值换个系数。

### 22.2 设计口径：五个类，还是一套组合？

**没有任何一个新类。** 每个人的差异都落在同一条 `ObjectArchetype` 组合上，差别只有三处：

| 差异来源 | 落在哪 | 谁读它 |
|---|---|---|
| ① **能力位**（tag 组合） | `combat.xlsx / Objects.Tags` | `ObjectCapability` 位运算 |
| ② **数值包** | `characters.xlsx / StatPacks` | `StatPackageRepository.Compose` |
| ③ **技能包 → 武器 / 子弹** | `characters.xlsx / SkillLoads.WeaponId` | `ContentSkillFactory` |

所以「5 人物」= **同一套 ECS 装配 × 5 组数据**，系统代码 **0 行改动**（规则 7）。

### 22.3 五个人物：机制各异，不是数值变体

| 人物 | 定位 | HP | 法力 | 护甲 | 移速 | 暴击率 | 武器 | 左键节奏 |
|---|---|---|---|---|---|---|---|---|
| `test_hero` | 均衡基准 | 120 | 80 | 6 | 5.2 | 0.05 | `pistol_basic` | 0.15s |
| `test_hero_tank` | 重甲坦克 | 220 | 60 | 20 | 3.6 | 0.03 | `cannon_heavy` | 0.42s |
| `test_hero_agile` | 敏捷刺客 | 90 | 100 | 3 | 7.4 | 0.08 | `smg_light` | 0.08s |
| `test_hero_lucky` | 幸运枪手 | 110 | 90 | 5 | 5.4 | 0.15（倍率 2.0） | `ricochet_lucky` | 0.18s |
| `test_hero_elemental` | 元素法师 | 70 | **180** | 3 | 4.4 | 0.08 | `orb_elemental` | 0.22s（耗蓝 6） |

**数值设计纪律**：每两个人之间**至少有一项是反向的**，保证没有谁是另一个的严格上位。
例如 tank 的血是 agile 的 2.4 倍，但移速只有一半；elemental 蓝量最高但血量最低。

五个人的 `DashSpeedMul / DashDuration` 也各不相同（2.2/0.18、1.8/0.22、2.8/0.16、2.4/0.18、2.0/0.20）
—— 冲刺手感是**角色的**属性而不是技能实现的属性，所以换角色只换数据（§19 同理）。

### 22.4 五颗子弹：每人一颗，机制不同

| 子弹物件 | 运动 | 命中策略 | 伤害 | 速度 | 特殊 |
|---|---|---|---|---|---|
| `bullet_hero_pistol` | Straight | Single | 20 | 22 | 基础直飞 |
| `bullet_hero_cannon` | Straight | Single | **55** | 14 | 高伤低弹速 |
| `bullet_hero_smg` | Straight | **Pierce** | 8 | 26 | 穿透 2 |
| `bullet_hero_ricochet` | Straight | Pierce | 16 | 20 | **弹跳 3** |
| `bullet_hero_orb` | **Homing** | Pierce | 12 | 16 | **追踪 + 穿透 3 + 分裂** |

子弹物件全部带 `tag.own_player`（标识层 `Faction` 轴，§21）—— 这是标识层第一次真正被内容使用。

### 22.5 五只怪物：3 → 5

| 怪物 | 种类 | HP | 护甲 | 移速 | 机制 |
|---|---|---|---|---|---|
| `goblin_grunt` | Monster | 40 | 2 | 3.4 | 近战杂兵 |
| `goblin_archer` | Monster | 40 | 2 | 3.4 | 远程射手 |
| `ogre_brute` | Elite | 300 | 25 | 2.2 | 重锤精英 |
| `slime_401` | Monster | 30 | 0 | 2.6 | **分裂**（`tag.split`） |
| `slime_403` | Monster | 55 | 4 | 2.2 | **死亡爆散**（`tag.explosive` + `tag.volatile`） |

两只史莱姆走**与人物同一套装配路径**：`StatPacks` 数值包 + `SkillLoads` 技能包。
差别只在「填了几个槽」—— 怪物只有 `PrimaryAttack` 一槽，人物五槽全满。
这是 §17「玩家怪物同源」在内容层的又一次兑现。

### 22.6 图鉴原型

`tools/data_pipeline/gen_codex.py` 直读两个 xlsx，产出 `docs/codex/index.html`：

```powershell
python tools/data_pipeline/gen_codex.py            # → docs/codex/index.html
```

**两个用途**：① 策划改完表跑一次就能看到全部数字，不用开引擎；
② 未来游戏内图鉴要显示哪些字段、按什么分组，先在这里把信息层级定死再搬进 UI。

**为什么按标签轴分栏**：物件全是「`Objects` 一行 + 一串标签」（§17），所以图鉴最自然的
组织方式就是沿轴切分 —— 先按 `Kind` 轴分大类，再按 `Element` / `Rarity` / `Role` 轴配色与筛选。
**加一个新轴 = 加一种新视图，渲染逻辑不变。**

### 22.7 幂等与真源

数值全部由 `tools/data_pipeline/fill_core_roster.py` 写入，**按主键覆盖**：

```powershell
python tools/data_pipeline/fill_core_roster.py     # 可重复运行，结果一致
```

⚠️ 该脚本**只写新增内容与既有 4 个人物变体的价值**，不重排、不删行。
`data/excel/combat.xlsx` 与 `characters.xlsx` 仍是**手工编辑的唯一真源**。

### 22.8 验证

```powershell
dotnet build                                          # 0 error
cd tests; dotnet test                                 # 372/372 通过
python tools/data_pipeline/validate_content_xlsx.py   # OK: 4569 项检查通过
```

### 22.9 踩到的坑（记录以防复发）

1. **`_upsert` 必须自己写主键列。** 第一版把「主键列」留给调用方传，调用方只传了非主键列，
   新行的 `Id` 列全空 → 校验器报一片「Id 不能为空」。现在 `_upsert` **强制写入主键**。
2. **同一个 `PackId` 不能同时出现在 Base 与 Growth 两层。** 仓库以包 id 为键，跨层互相覆盖，
   `CharacterContentSet.FlushStatPacks` 会直接抛异常。成长包要么共用、要么换 id。
3. **规则值列「空 ≠ 0」。** `Gravity` / `HomingTurn` 之类必须显式写 `0` 表示「明确为零」；
   而 `MaxChainDepth` 例外，**必须 ≥ 1**。
4. **`VisualSlot` 是必填整数**，空白会让 `CharacterXlsxBinder.ParseInt` 抛 `FormatException`。
5. **改表必须同步 C# 内置镜像。** `CharacterXlsxParityTests` 会逐项比对表与
   `CharacterContentSet.BuildBuiltinV1()`；只改一边必定红。
6. **`wstat:` / `flags:` 这类继承写法对图鉴不友好。** 图鉴要展示的是**最终数字**，
   继承值需要先求值；新增人物武器因此一律写独立基线。

---

## 23. 本地化：中文名真源 `l10n.xlsx`（2026-11-06 落地）

### 23.1 这一节解决什么

用户打开图鉴第一句话是「全是英文」。根因不在图鉴，而在**项目里从来没有中文名**：

- `DisplayKey` 列（`combat.xlsx/Objects`、`characters.xlsx/Characters`）**明文规定不写中文**，
  它是**本地化 key**（`actor.goblin_grunt`、`item.pistol_101`）；
- 全仓库搜 `*loc*` / `*i18n*` / `*lang*` / `*.csv` → **零命中**，没有任何本地化表。

所以 §22 的图鉴只能把 key 原样印出来。本节补的不是"给图鉴加翻译"，
而是**给整个项目补上第二条真源：名字**。

### 23.2 核心决定：名字是独立真源，不是代码常量

| 备选 | 否决理由 |
|---|---|
| 中文名写进 `gen_codex.py` 常量 | 违反规则 4（文案也是数据）；加一个武器就得改脚本 |
| 给 `Tags` / `Objects` 表加 `NameZh` 列 | 要同步改生成器 `COLUMNS` → C# Def → 校验器 → ARCHITECTURE，**远超规则 7 的「≤2 个文件」**；而且名字与数值的编辑节奏不同（文案改得比数值频繁得多），混在一张表里会频繁打架 |
| **独立 `data/excel/l10n.xlsx`** ✅ | 一张表、一个 `gen_codex.py` 消费点；将来游戏 UI 用同一个文件，**图鉴与游戏不会各说各话** |

**关键**：图鉴和将来的游戏 UI 读的是**同一份** `l10n.xlsx`。
这避免了最典型的翻车场景 —— 策划在图鉴上看到"飞弹"，进游戏发现叫"炮弹"。

### 23.3 表结构

`tools/data_pipeline/gen_l10n_xlsx.py` 生成，之后**手工维护**（同 `combat.xlsx` 的规矩）。
四行表头 + `#data` 标记行 + 第 6 行起数据，与其它主题表完全一致：

| 列 | 含义 |
|---|---|
| `Key` | `actor.*` / `item.*` / `proj.*` / `fx.*`（= `DisplayKey`）、`tag.*`（= `TagId`）、或裸枚举名（`Pierce` / `Explosive` / `Melee`…） |
| `Zh` | ★ 中文名，策划直接改这里 |
| `Note` | 可空；只在语义容易误解时填 |

当前 **167 条**：物件 37 · 标签 62 · 枚举 55 · 单位 7（+ 5 个人物武器 id）。

### 23.4 图鉴怎么用它

`gen_codex.py` 里的 `L10n` 类提供 `l10n(key)` 与 `l10n.tag(name)` 两个查询，**三处约定**：

1. **中文名做主标题，英文 id 做副标题**（`<code class="id">`，10.5px、灰色、等宽）。
   策划对表时要看 id，玩家只看中文 —— 两者都不能丢。
2. **查不到时原样返回 key 并记账**（`l10n.missing`），生成结束打印警告清单。
   **绝不返回空字符串** —— 图鉴上冒出一个英文 key 是最好的"这里缺名"提示；
   留白会让人以为是排版问题。
3. **搜索索引同时收录中英文**：`data-search` 拼 `中文名 + 英文 id + 副标题`，
   `data-chips` 保留英文标签。搜「元素」「element」「bullet_301」都能命中。

### 23.5 校验器新增的 5 类检查（437 项）

`validate_content_xlsx.py` 里新增 `validate_l10n()` 与 `collect_l10n_refs()`，
检查项从 4136 → **4569**。这是本次最有价值的部分 —— 中文名很容易**静默**出错：

| 检查 | 为什么必须报错 |
|---|---|
| `Key` 重复 | 建字典时后一条覆盖前一条，图鉴会显示其中一个，**没人会发现** |
| `Zh` 为空 / 全空格 | 图鉴退化成显示英文 key，看起来像"还没做"而不是"填错了" |
| **缺中文名**（覆盖性） | 扫 `Objects.DisplayKey` + `Tags.TagId` + `Characters.DisplayKey` 的全部引用，**引用必须有名字**；报错时指出是谁引用的 |
| 中文名仍是英文标识 | 复制粘贴没改的典型症状 |
| 同一命名空间内重名 | **只在 `tag` 与 `object` 各自桶内比较**。`Pierce`（枚举）与 `tag.pierce`（标签）都叫"穿透"是**正确且必要**的，跨桶不比较 |

> 第一版没分命名空间，一次性报了 12 条假阳性，真问题（`tag.melee` 与 `tag.short_range`
> 都叫"近战"）淹没在里面。**校验器的信噪比比检查数量更重要。**

### 23.6 顺带修掉的一个真 bug：DisplayKey 不一致

校验器的覆盖性检查抓出来的：`player_hero` 在两张表里有两个不同的 `DisplayKey` ——

- `combat.xlsx/Objects` 写 `actor.player`
- `characters.xlsx/Characters` 写 `actor.player_hero`

**规范定为 `actor.<Id>`**（`characters.xlsx` 里 12 行全部符合），已把 `combat.xlsx` 那一个
异常值改齐。这类不一致之所以危险：两边都能过校验、都能跑，但**同一个物件在两个界面里
会有两个名字**，等 UI 接上去才会发现。

### 23.7 可重复运行的脚本

| 脚本 | 用途 |
|---|---|
| `gen_l10n_xlsx.py` | **仅首次**生成骨架；之后不要重跑（会覆盖手改） |
| `add_missing_l10n.py` | 幂等补几条已知遗漏（人物武器 id、枚举 `Homing`） |
| `gen_codex.py` | 重跑图鉴；缺名会打印警告清单 |

### 23.8 验证

```powershell
dotnet build                                          # 0 error
cd tests; dotnet test                                 # 372/372 通过
python tools/data_pipeline/validate_content_xlsx.py   # OK: 4569 项检查通过
python tools/data_pipeline/gen_codex.py               # 0 条缺名警告
```

### 23.9 一句话规矩

> **加任何新内容（人物/怪物/武器/子弹/标签）时，`l10n.xlsx` 必须同步加一行中文名。**
> 忘了不会崩，但校验器会报「缺少中文名：xxx（被 Yyy 引用）」，图鉴生成也会打警告。

---

## 24. 3D 素材查看器（`tools/asset_viewer2/`）

**定位**：独立于 Godot 的只读工具。用来**快速浏览 `素材/` 目录下的 3D 资产**——扫描、列表、逐个渲染看效果。不参与游戏运行时，不链接 Godot，不进 `native/`。

### 24.1 为什么单独放 `tools/` 而不是 `native/`

规则2 要求「C++ 代码放 `native/`」是为了约束**游戏运行时的原生扩展**（GDExtension）。素材查看器是一个**完全独立的桌面程序**：自己的 `main()`、自己的 Vulkan 设备、自己的窗口，与引擎零交互。放进 `native/` 反而会让 `native/build/` 与 Godot 的 GDExtension 构建互相污染。

因此它在 `config/architecture-rules.json` 的 `ignore` 列表里登记放行（与旧版 `tools/asset_viewer/**`、`tools/probes/**` 同理）。

### 24.2 技术栈

| 层 | 选型 |
|---|---|
| 窗口 / 输入 | GLFW 3.4 |
| 图形 | Vulkan 1.1（`VkContext` 自管 swapchain / render pass / depth） |
| UI | Dear ImGui **docking 分支**（1.93.1 WIP，自带 DockSpace API）+ 官方 `imgui_impl_vulkan` + `imgui_impl_glfw` |
| glTF 解析 | cgltf（单头文件） |
| FBX 解析 | 自研二进制解析器 `src/loaders/fbx_loader.cpp`（覆盖 7200/7400/7500） |
| 贴图解码 | WIC（Windows Imaging Component） |
| 构建 | CMake + Ninja + MSVC |

### 24.3 目录

```text
tools/asset_viewer2/
├─ CMakeLists.txt          av_core / av_gpu / av_imgui 三个静态库 + asset_viewer2 主程序
├─ shaders/                model.vert / model.frag（源码，.spv 由 glslc 在构建时生成）
├─ src/
│  ├─ core/                math3d / model_data / scanner（素材扫描）/ image_io
│  ├─ loaders/             gltf_loader（cgltf）/ fbx_loader / texture / wic_*
│  ├─ gpu/                 vk_context（设备/交换链）/ vk_pipeline（管线/推送常量）/ camera / thumb_renderer
│  ├─ ui/                  asset_cache（后台加载线程）/ ui_main（DockSpace 布局）
│  ├─ app.cpp              Init / Frame 主循环
│  └─ main.cpp             入口
├─ tests/                  loader_test / tex_test / render_test（控制台自测）
└─ third_party/imgui/      ImGui docking 分支源码（见 24.6，非系统预装）
```

### 24.4 三个必须知道的坑（都踩过）

1. **矩阵约定**。CPU 侧 `Mat` 是**行主序 + 行向量**（`v' = v * M`）。上传前 `ToColumnMajor()` 转置成 GLSL 列主序，所以 **shader 里必须写 `row_vector * mat4`**，不能写 `mat4 * vec4`。写反了不会报错，只会让几何体飞出屏幕——表现是「什么都没画出来」。
2. **`.spv` 不能留在 `shaders/` 源目录**。`CMakeLists.txt` 的 POST_BUILD 会把 `shaders/` 整个拷到输出目录。如果源目录里躺着一份旧的 `.spv`，它会**覆盖掉刚编译的新 shader**，导致「改了 shader 但画面不变」。现在改由 CMake 在 POST_BUILD 阶段调 `glslc` 生成，源目录只留 `.vert` / `.frag`。
3. **`IMGUI_DIR` 必须带 `FORCE`**。`CMakeLists.txt` 里是 `set(IMGUI_DIR ... CACHE PATH ... FORCE)`。`set(... CACHE ...)` **不会覆盖已存在的缓存值**，漏了 `FORCE` 时 `build/CMakeCache.txt` 会一直指向别处的 ImGui（本机曾指向 `EasyPainter/vendor/imgui` 的 master 分支），报一堆 `error C2039: "DockBuilderRemoveNode": 不是 "ImGui" 的成员`。另配了配置期断言：`IMGUI_DIR` 下没有 `imgui_internal.h` 就直接 `FATAL_ERROR`。

### 24.5 素材过滤

纯碰撞体资产不进列表：目录名等于 `Collision`，或文件名以 `_Collision` 结尾（`src/core/scanner.cpp` 的 `IsCollisionOnly`）。当前素材库扫描结果：**358 个资产 / 12 个目录**（301 glb + 57 fbx，约 132 万顶点）。

### 24.6 DockSpace 布局

面板全部停靠在一个铺满窗口的 DockSpace 里（三个栏位可拖动、可浮动、可保存），布局持久化到工作目录下的 `av2_layout.ini`。

```text
┌──────────┬──────────────────────────┬──────────────┐
│ 文件夹   │                          │  检查器      │
│  (树)    │       3D 视口            │  (四页签)    │
├──────────┤      (无背景窗口)        ├──────────────┤
│ 资产     │                          │  统计        │
│  (列表)  │                          │              │
└──────────┴──────────────────────────┴──────────────┘
  28%                 剩余                  30%
```

**为什么 3D 视口是一个「真的 ImGui 窗口」而不是 `PassthruCentralNode` 的中央空节点**：`PassthruCentralNode` 要求中央节点为空，一旦用户把某个面板拖到中央，面板就会盖住 3D。现在中央停靠一个名为 `3D 视口` 的窗口（`NoBackground` / 无内边距 / 无边框 / 不滚动），它只做两件事——占住中央节点、上报自己的矩形。

`DrawUi(..., ViewportRect* outViewport)` 把该矩形输出给 `app.cpp`，`vkCmdSetViewport` / `vkCmdSetScissor` 按它下刀；绘制 ImGui 前再恢复成全屏。这样 3D 渲染严格被裁剪在视口内，不会被左右面板遮住。菜单「重置布局」调 `RequestDockLayoutReset()` 重建默认布局。

**ImGui 源码来源**：本机 `Mine/EasyPainter/vendor/imgui` 与 `ThirdLib/imgui` 都是 master 分支，**没有 DockSpace API**。现在把官方 `docking` 分支 vendored 进 `third_party/imgui/`（约 3.8 MB，含 `imgui_internal.h`——DockBuilder API 在内部头里）。
