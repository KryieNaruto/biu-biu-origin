# BiuBiuOrigin 架构大纲

> 状态: v5  最后更新: 2026-11-06  维护规则: 每个任务完成后**就地覆盖更新**本文件（SOP 见 `docs/rules/07-definition-of-done.md`），禁止另建 `ARCHITECTURE_v2.md`
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
| 当前阶段 | **v5：ECS 骨架 + 房间/地牢 + 角色抽象 + 素材查看器 + 基础物件抽象（武器/子弹/怪物同源 + 熔炉组合）均已落地**（174 测试全绿；素材查看器全量 371/371 验收通过）；战斗/技能的具体实现在填充，素材层当前为 Box 白模（用户已确认先白模后换资源），已落地的是「数据 + 契约 + 工厂」，碰撞/伤害结算仍待下一任务 |

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

### 4.4 各段的校验点汇总表

| 校验点 | 位置 | 失败策略 | 负责脚本 |
|---|---|---|---|
| ① 表结构 | pipeline 读表后 | 中断，列出表/行/列 | `tools/data_pipeline/` |
| ② 字段级 | pipeline 转换前 | 中断，逐条列出 | 同上 |
| ③ 跨表外键 | pipeline 写盘前 | 中断，指出两表两 id | 同上 |
| ④ schema 版本 | pipeline 开始 + 结束 | 中断 | 同上 |
| ⑤ 生成物自检 | pipeline 写盘后 | 中断并回滚生成目录 | 同上 |
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
| 2026-11-06 | v5 | **基础物件抽象（武器/子弹/怪物同源 + 熔炉组合）落地** | **新增 §17 基础物件抽象**：人物/怪物/武器/子弹**不是四个类**，而是同一个 `ObjectArchetype`（= `combat.xlsx` 的 `Objects` 一行）——差异只在 ① `ObjectCapability` 能力位（`[Flags]`：Actor/Weapon/Projectile/Emitter/Trigger/Consumable/Structure，可任意组合）② 四个 `ContentRef` 引用（Weapon/Projectile/Actor/Effect，**谁都可以指向任何 Objects.Id**）③ 位矩阵 + 规则值。① **规则值 `ValueRef`**（10 种来源：Literal/Stat/WeaponStat/Flags/ChainDepth/ChainCount/ViableCount/Perf/RandomRange/RandomScaled）+ `ValueSet`（容量4，`Σabs×(1+Σpercent)` 层叠）——**「子弹继承武器属性」= 表里写 `wstat:WeaponDamage(5)`，代码 0 行**；② **熔炉 `ForgeRules`**：64 位配方字 × 簇内加权轮盘，纯静态只吃 `ReadOnlySpan`，**1000 次迭代 0 字节托管分配**（用例断言）；`ContentRegistry` 按「簇+槽位名」编址，`Purge(generation)` 回到不可变 `InitialDefId`；③ **工厂等价性**：武器射子弹 / 武器射怪物 / 子弹炸开再射子弹 / 子弹变成怪物，在 `EcsObjectFactory.TryCast` 里是**同一段代码**，差别只有契约里哪个引用非空 + `Delivery`/`OnExpire` 两个枚举（4 条端到端用例读真表钉住）；④ **策划可改（需求②）**：`combat.xlsx` 6 Sheet（Tags/Objects/Weapons/Projectiles/StatPacks/_Meta）+ `validate_content_xlsx.py` **2217 项构建期检查**（外键闭合、能力位一致、TagIndex 连续/BitMask 唯一、规则值语法真解析、**同簇能力位一致**、ClusterWeight>0）+ 零第三方包 `XlsxReader` 读真 xlsx 的绑定回归（「改表 → 运行期行为变」可证）；⑤ **明确不做**：碰撞/空间划分/伤害结算（游戏逻辑，`ProjectileAdvanceSystem` 只做积分+寿命，未实现分支显式计数不静默）；⑥ **修 6 个静默失效 bug**（记入 §17.8）：FNV-1a32 有符号哈希被当"空引用"判据（同一错误出现 3 处）、`Purge` 回不到初始态、`Projectiles` 行主键与引用列口径不一致致**所有枪静默打不出子弹**、`tag.projectile` 物件拿不到契约、`characters.xlsx` 表头被生成器转置。**测试 101 → 174 全绿**；架构守卫 0 error / 0 warning | `docs/architecture/ARCHITECTURE.md`、`scripts/core/Content/**`（ValueContext/ObjectArchetype/ObjectBitMatrix/WeaponContract/ProjectileContract/ContentWorld/ContentIndex/WeaponAimKind）、`scripts/core/StatId.cs`（+17 成员：武器 1030~1041、投射物 1050~1054）、`scripts/combat/**`（CombatComponents/ForgeRules/ContentRegistry/CastQueue/CastSystem/ProjectileAdvanceSystem/Factory）、`scripts/data/Content/**`（ContentBinder/ValueRefParser）、`data/excel/combat.xlsx`、`data/excel/characters.xlsx`（表头修复）、`tools/data_pipeline/{gen_combat_xlsx,validate_content_xlsx}.py`、`tools/data_pipeline/README.md`、`tests/Content/**`、`tests/BiuBiuOrigin.Tests.csproj` |
| 2026-11-05 | v4 | **素材快查看器（Asset Viewer）落地** | **新增 §16 素材快查看器**：独立 Win32 桌面程序（**不进游戏、不是 GDExtension**），CMake + MSVC + D3D11/Direct2D，**零第三方依赖**。① **边界**：加载器只吐 `ModelData` 纯数据、**不碰 D3D**，渲染器不碰文件格式 —— 故 `thumb_sheet` 能在无窗口环境复用同一套加载代码。② **格式**：自研 glTF 2.0（`.glb`）与 FBX 7.2/7.4/7.5 解析器，**自带 DEFLATE 解压器**（不为 FBX 压缩数组引 zlib）。③ **构建**：`av_core`/`av_ui` 静态库 + `asset_viewer`(WIN32) + `loader_smoke`/`ui_selftest`/`thumb_sheet`(CONSOLE) + `av_sheet` 自定义 target；`AV_STATIC_RUNTIME=ON` 走 `/MT` 免装 VC++ 运行库。④ **修复 8 处实打实缺陷**，全部写进 §16.5 教训表：Inflater 无输出上限致 42GB OOM、畸形 count 直接喂 `resize()`、FBX 子网格材质归一化口径不一致致死循环、`WM_NCCREATE` 期 `hwnd_` 未赋值致 `CreateWindowExW` 失败(errno=1400)、cmd 批处理中文参数变 `??`、`windows.h` 的 `DrawText` 宏污染 `d2d1.h`、多 slot 顶点布局触发 `0x80070057`、`MakeLookAt` z 轴取反致模型不可见。⑤ **验收**：`loader_smoke` 371/371、`thumb_sheet` 371/371（wall 4.56s @16 线程）、`ui_selftest` 77/77、`--shot=` 出图成功。⑥ **交付** `dist/` 单文件绿色版 + 中文启动器 + 使用说明 | `tools/asset_viewer/**`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-05 | v4 | **角色抽象（Character）落地** | **新增 §15 角色抽象**：角色 = 「数值包组合 + 技能包 + 阵营/控制标签」的 ECS 实体，玩家与怪物**同一组件集合**，差异只在数据。① **数值**：`StatId` 追加 法力(1000 段)/护甲(1003 段)/力量·敏捷·幸运(1010 段)，五项基础资源 Hp/Mana/Armor + 三项属性 + 三项资源回复；`StatPackageRepository.Compose` 严格按规则6 折叠——Base 层**绝对值**、其余四层**差量叠加**，`GetLayerValue` 保留单层读取。② **技能**：`SkillSlot` 五个固定槽位（Dash=Shift / PrimaryAttack=左键 / WeaponSwap=Q / Signature=R / Interact=F），`SkillLoadout` 定长表按槽存技能并各自计冷却；`SkillCastSystem` 每帧只消费**一个**槽（意图优先级由 `IntentRules` 决定），门控顺序 `CanCast → Cast → 扣法力 → 起冷却`。③ **组合**：`ActorSpawnRequest(DefinitionSlot, Position, TeamOverride?, ControlOverride?)` 是唯一的造人入口——**夺舍 = 改 `ControlOverride`/`TeamOverride` 两个字段**，不需要任何新类型；`ActorIdentity` 只存槽位号（J2），不存引用。④ **策划可改**：`data/excel/characters.xlsx`（Characters / StatPacks / SkillLoads 三表，四行表头规范）为唯一真源；`CharacterContentSet` 提供与表逐项一致的运行时副本兜底，`CharacterContentRegressionTests` 用 9 条用例把「表 ↔ 运行时」钉死（每个角色都必须能真装配出来、玩家技能包五槽齐全、怪物与玩家同机制）。⑤ **引擎边界**：`PlayerInputIntentSource`（`scripts/host/control/`）把 InputMap action 翻成 `InputIntent`（Q16 定点方向），是唯一碰 `Input` 的地方；技能逻辑只认意图，故 AI/回放/玩家共用同一条代码路径。**测试 101 全绿**（新增 25 例） | `docs/architecture/ARCHITECTURE.md`、`scripts/core/{ActorKind,SkillSlot,StatId}.cs`、`scripts/core/{Actors,Skills}/**`、`scripts/actor/{ActorComponents,ActorRegistries,ActorIntentSystem,ActorAssemblySystem}.cs`、`scripts/skill/SkillCastSystem.cs`、`scripts/data/{StatPackageRepository,CharacterContentSet}.cs`、`scripts/host/control/**`、`scripts/host/DungeonDebugScene.cs`、`project.godot`、`data/excel/characters.xlsx`、`tools/data_pipeline/gen_characters_xlsx.py`、`tests/Characters/**` |
| 2026-11-04 | v3 | **房间与地牢系统（场景抽象）**，另含 4 处基础设施修复 | **新增 §14 房间与地牢**：四层切分（L1 值词汇 / L2 规划 / L3 生成 / L4 表现）、`IDungeonPlanner.Plan(config,seed,generation) → DungeonLayout` 的「一次掷骰、全链只读」模型、`RoomCategory` 七类（Start/Combat/Special/Hidden/Prototype/Boss/Safe）、6 条可执行不变式、与 ECS 的边界（房间不产生实体、L3 只走 `CommandBuffer`、`World→Node` 单向）。**落地**：`scripts/spawn/rooms/`（契约 + `BranchDungeonPlanner` + `GridContentPlanner` + `DefaultRoomContentSpawner` + `DungeonValidation`）、`scripts/host/Dungeon*`（`DungeonOptions`/`DungeonSceneBuilder`/`DungeonRoomView`/`DungeonDecorCatalog`/`DungeonDebugScene`）、`scenes/main/DungeonDebug.tscn`、`tools/dungeon_check/`（200-seed 性质校验）、`tools/decor_catalog/`（素材→装饰目录）。**基础设施修复**：① `BiuBiuOrigin.csproj` 的 `DefaultItemExcludes` 新增 `**/obj/**;**/bin/**;tools/**;tmp/**;build/**;out/**`——修掉「Godot.NET.Sdk 默认 glob 把 `.godot/mono/temp/obj/**/*.AssemblyInfo.cs` 当源码编进来」导致的 CS0579 自举式重复（本轮最贵的坑）；② `LifetimeSystem` 与 `VisualBridge` 把 `Query<Entity, T>` 改为 `Query<T>`、实体号改从组件 `[Identity] Owner` 取——`Entity` 是句柄不是组件列（J1），旧写法会让 `Register<Entity>()` 抛异常且 `SystemBase` 留在「半死」状态；③ `config/architecture-rules.json` 移除无源头的 `tools/decor_catalog/**` 放行、`.gd` 探针统一收进 `tools/probes/`；④ `tools/probes/` 目录此前只存在于白名单、实际未创建，本次补建。**未改架构**：GS005 清理（39 条 → 0，做法是把真·表现数值收进 `DungeonOptions`、把几何常量改成语义推导，而非加注释糊过去） | `docs/architecture/ARCHITECTURE.md`、`scripts/spawn/rooms/**`、`scripts/host/**`、`scripts/actor/LifetimeSystem.cs`、`scripts/host/bridge/VisualBridge.cs`、`scenes/main/DungeonDebug.tscn`、`BiuBiuOrigin.csproj`、`config/architecture-rules.json`、`config/decor_catalog.json`、`tools/dungeon_check/**`、`tools/decor_catalog/**`、`tools/probes/**`、`tests/Rooms/**`、`docs/artifacts/decor_catalog.md` |
| 2026-11-03 | v1 | 立规矩（规则体系建立） | 建立 7 条开发规矩与架构骨架：C# 主逻辑（`Game.*` 七层命名空间）、C++ GDExtension 于 `native/`、UI 层允许 GDScript、Excel 为数值唯一真源、架构守卫 GS001~GS007、完成定义 6 步检查表；本节所有「已落地」项均为空 | `docs/architecture/ARCHITECTURE.md`、`docs/rules/01-gdscript-ban.md`、`docs/rules/02-tech-selection.md`、`docs/rules/03-abstract-first.md`、`docs/rules/04-data-driven.md`、`docs/rules/05-numeric-package.md`、`docs/rules/06-extensibility.md`、`docs/rules/07-definition-of-done.md`、`tools/check_architecture.ps1`、`tools/env.ps1`、`docs/prompts/task-prompt-template.md`、`.gitignore`、`AGENTS.md` |
| 2026-11-03 | v1 | 立规矩（仲裁收口，本日第二次更新） | 收口仲裁：`IVisualDescriptor` 由候选转**正式接口**；`ISkill`/`ISkillTarget` 声明**下沉 `Game.Data`**、实现留 `scripts/skill/`；`IActor` 增加 `TryApplyTakeover(in TakeoverSpec) : bool`；§3.2 新增「声明位置 / 实现位置」两列；§4 统一 Excel **按主题分文件**口径（characters/combat/presentation/progression，Sheet 名=表名）；§9.4 标注为性能阈值**唯一真源**；文件头恢复常规排版、变更日志恢复 §11 编号 | `docs/architecture/ARCHITECTURE.md`、`docs/rules/03-abstract-first.md`、`docs/rules/04-data-driven.md`、`docs/rules/05-numeric-package.md`、`docs/rules/06-extensibility.md`、`docs/rules/07-definition-of-done.md` |
| 2026-11-03 | v1 | 立规矩（第四轮收口，本日第四次更新） | **`ISkillCaster` 悬空接口关闭**：§3.2 行更新为 03 §4.1 定稿签名（新增 `CanCast` 预检、`TickSkill(id,dt)`→批量 `TickSkills(dt)`、新增 `TryReplaceSkills` 受控通路），状态由「v1 待正式定义」改为「✅ 签名已定稿」；原「⚠️ 已知缺口」小节改写为「归属已收口」并补**两通路纪律**（`TryApplyTakeover` 内部改技能 = 夺舍专用；`TryReplaceSkills` 非夺舍、夺舍中返回 false）；03 §4.1.1 待仲裁项全部改标 ✅、03 补 `ISkillCaster` 为第 15 个正式接口、05 §8 补 `IStatProvider` 实现位置注释、06 修 2 处省略式 `ApplySlot` 槽位写法；06 §9.2 开放问题 OQ-1~OQ-9 **全部关闭** | `docs/architecture/ARCHITECTURE.md`、`docs/rules/03-abstract-first.md`、`docs/rules/05-numeric-package.md`、`docs/rules/06-extensibility.md` |
| 2026-11-03 | v2 | ECS 权重骨架落地 | **运行时架构由「纸面」转为「已实现」**：`scripts/core/ecs/`（内核 14 文件）、`scripts/host/`（七阶段管线 + 视图池 + 事件队列）、`scripts/actor`+`scripts/spawn`（3 个系统）、`tests/`（63 用例全绿）落地；§0 阶段改为 v2、§1.1 现状树重写、§2.3 矩阵新增 `Game.Host`/`Game.Core.Ecs` 两行并写明「Host 是唯一 using Godot 的游戏层」硬约束、§3.2.0 新增 ECS 类型清单、§6.1 现状更新 + 新增 §6.1.1「七步逻辑如何映射到 ECS 阶段」、§10 关闭 R1/R2/R3、R11 部分关闭、新增 R13~R15、**新增 §13 ECS 落地汇总**（含 4 条真炸过的缺陷复盘与填充顺序）；形状真源仍是 `docs/architecture/ECS.md` | `docs/architecture/ARCHITECTURE.md`、`docs/architecture/ECS.md`、`AGENTS.md` |

---

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

---

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

### 16.6 验收基线（回归时对这些数字）

| 验收项 | 结果 |
|---|---|
| `loader_smoke` 全量 | **371 ok / 0 fail**，avg 1.46ms；GLB 301/301、FBX 70/70 |
| `thumb_sheet` 全量（16 线程） | **371 ok / 0 fail**，parse avg 4.02ms / thumb avg 8.27ms，wall **4.56s** |
| `ui_selftest` | **77 通过 / 0 失败**（含 371 条目扫描、缓存命中 371/371） |
| `asset_viewer.exe --shot=` | 成功导出 PNG（2282×1564）|
| 规模核对 | 88.3 万三角形 / 132.5 万顶点；9 个带骨骼动画模型全部正确解析 |

### 16.7 交付形态

```text
tools/asset_viewer/dist/
├─ asset_viewer.exe         ← 单文件绿色版（607KB，/MT 静态运行库，无外部依赖）
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
└─ combat.xlsx       ← 物件（Tags / Objects / Weapons / Projectiles / StatPacks / _Meta）
   ├─ Tags          ← 位矩阵的"列定义"：TagId / TagIndex / Category / Group / BitMask
   ├─ Objects       ← ★唯一的物体原型表：人物/怪物/武器/子弹/特效全在这里
   │                   Id / Tags / Cluster / ClusterWeight / StatPacks / Weapon / Projectile / Actor / Effect
   ├─ Weapons       ← 武器契约（8 行 × 28 列，13 个规则值列）
   ├─ Projectiles   ← 子弹契约（7 行 × 20 列，8 个规则值列）
   └─ StatPacks     ← 武器/子弹自己的五层数值包（与 characters.xlsx 同构，合并进同一个仓库）
```

- 生成器 `tools/data_pipeline/gen_combat_xlsx.py` 保证四行表头（字段名/类型/中文名/备注）+ `#data` 标记行合规；
- **构建期闸门 `tools/data_pipeline/validate_content_xlsx.py`**：2217 项检查 —— 四行表头、类型、外键闭合
  （四个引用必须指向 `Objects.Id`）、能力位一致（`tag.weapon ⇔ 被 Weapons 引用`、`tag.projectile ⇔ 被 Projectiles 引用`、
  `tag.actor ⇔ Base 包非空`）、`TagIndex` 连续 + `BitMask` 唯一、**规则值语法真解析**、
  `StatPacks` 唯一性、**同簇能力位一致**（防"熔炉把子弹换成枪"）、`ClusterWeight>0`。exit≠0 即中止构建；
- **闭环证明**：`ContentBinderTests` / `ValueRefParserTests` / `ContentPlayLoopTests` 直接用**零第三方包**的
  `XlsxReader` 读**真 xlsx** → 绑定 → 真 `CastSystem` 铸造成实体，验证"武器射出怪物""子弹到期生成子弹"
  "子弹变成怪物""改 `BaseDamage`/`ClusterWeight` 后运行期行为随之改变"。**运行期代码不读 xlsx**（§4.8 不变）。

### 17.7 边界与明确不做（本轮）

| 明确不做 | 归属 |
|---|---|
| 碰撞检测 / 空间划分 / BVH、伤害结算、命中特效、击退 | **游戏逻辑**，下一任务；本轮 `ProjectileAdvanceSystem` 只做位置积分 + 寿命，`Explode`/`SpawnEffect` 到期行为**显式返回"未实现"并计入 `RejectCount`**，不静默吞 |
| Node 视图池与 `ViewRegistry` 绑定（"对象池减少 Node GC"的 Node 侧） | 宿主层任务 |
| 武器法力消耗列（`Weapons.ManaCost`） | ⚠️ 已知退化：`CastSystem` 暂以 `weapon.Count` 求值代替门控，表补列后改一行 |
| `Objects.ClusterWeight` 之外的分项权重（按 tag 加权的复杂熔炉） | 扩展点：`ForgeRules` 只吃加权数组，加权重来源 = 换 `ClusterCandidates` 的填充 |

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
