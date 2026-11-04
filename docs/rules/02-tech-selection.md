# 规则 2 · 技术选型：C# 为主，渲染/性能用 C++ GDExtension，特效用自研 shader

> 自动检查: `tools/check_architecture.ps1` → **GS003**（error）、**GS004**（warning）、**GS007**（warning）
> 上游真源: [ARCHITECTURE.md](../architecture/ARCHITECTURE.md)（架构形状与全部量化阈值）
> 关联: [01-gdscript-ban.md](01-gdscript-ban.md)（规则 1）、[03-abstract-first.md](03-abstract-first.md)（规则 5）、[06-extensibility.md](06-extensibility.md)（规则 7）

---

## 0. 一句话

> **默认写 C#。只有「每帧热路径 / 需要逐帧碰渲染」时，才把它下沉成 C++ GDExtension；效果一律用自研 `.gdshader`。引擎源码默认不改。**

三条判据（本文档的核心，其余都是展开）：

1. **逻辑在 C#** —— 玩法、状态机、AI、技能、结算，全部 `scripts/**` 下的 C#，可读可调试优先；
2. **渲染/性能在 C++** —— 只有跨过 ARCHITECTURE.md §9 的预算、或必须在 RID 级逐帧操作渲染时，才下沉 `native/`；
3. **外观在 shader** —— 视觉效果用 `.gdshader`，参数通过 `uniform` 暴露、由 `.tres` / C# 驱动，**不写死**。

---

## 1. 决策矩阵（任务类型 → 语言 → 位置）

| # | 任务类型 | 语言 | 落点 | 判据要点 |
|---|---|---|---|---|
| T1 | **UI / 菜单 / HUD / 飘字 / 设置面板** | **C#**（GDScript 仅限纯布局，见规则 1） | `scripts/ui/` | UI 更新频率低且集中在少数节点，C# 完全够；规则 1 允许 `.gd` 但推荐 C# |
| T2 | **游戏玩法逻辑**：状态机、AI 决策、技能、战斗结算、关卡流程 | **C#** | `scripts/actor/`、`scripts/combat/`、`scripts/skill/`、`scripts/spawn/` | 逻辑可读性、可调试性优先；一帧只跑一次 |
| T3 | **数据定义与装配**：`Resource` 子类、Excel 生成物加载 | **C#** | `scripts/data/` | 需要检查器可视化编辑（规则 4） |
| T4 | **渲染特性**：自定义几何体、后处理、自定义渲染管线、自定义 pass | **C++ GDExtension** | `native/src/` | 需要 `RenderingDevice` / `RenderingServer` 的 RID 级访问；C# 绑定每帧跨界成本高 |
| T5 | **批量实体**：大量子弹 / 粒子 / 命中判定的逐帧推进 | **先 C#，达标再 C++** | `scripts/combat/` → `native/src/` | 见 §2 H2；批量计算能转到 GPU / 多线程时才必须下沉 |
| T6 | **视觉特效、描边、受击闪、溶解、水流** | **`.gdshader`**（自研） | `shaders/` | 只改材质就能做到 → shader；要改 pass 结构 → T4 |
| T7 | **构建期工具**：Excel → `.tres`/`json` 生成器 | **Python** | `tools/data_pipeline/` | 只在构建期跑，不进游戏包 |
| T8 | **构建/校验脚本**：守卫、环境探测、打包 | **PowerShell 5.1** | `tools/*.ps1` | 见 §3.5 编码铁律 |
| T9 | **编辑器扩展**：可视化调参面板、导入插件 | **C# 或 GDScript `@tool`** | `addons/` | 不限语言，不参与运行时玩法 |
| T10 | **引擎源码修改** | **默认禁止** | — | 确需时走 §5.1 的 fork 流程并记录引擎版本 |

---

## 2. 硬性判据：什么时候必须下沉 C++ GDExtension

**满足任意一条即可（不是必须全满足）**，按 §2 表判断。**未达判据一律先写 C#**。

> **阈值见 [docs/architecture/ARCHITECTURE.md](../architecture/ARCHITECTURE.md) §9.2~§9.4（唯一真源），本文档只给判据不给数字。**
>
> 本文档**不复制**任何具体阈值。改阈值 = 改 ARCHITECTURE.md §9.2~§9.4 + 在其「变更日志」加一行，**不需要改本文档**
> （唯一例外：判据本身增删时同步本表）。详见 §2.3。

| # | 判据（定性） | 触发动作 | 测量方式 |
|---|---|---|---|
| H1 | 单帧热路径方法调用次数超出 §9.4 的调用预算 | 考虑 C++ 批量实现 | profiler 的 call count，或自加计数器 |
| H2 | 逐帧遍历的批量实体数超出 §9.2 的同屏实体预算 | 考虑 C++ / MultiMesh | 实体计数器（子弹、伤害判定体） |
| H3 | 需要访问 `RenderingDevice` / `RenderingServer` 直接操作 | 优先 `CompositorEffect` / C# 绑定；做不到则**必须** C++ | 代码审查 |
| H4 | 需要逐帧修改渲染管线 / 自定义 pass 内部实现 | **必须** C++ | 代码审查：pass 注册、`add_render_pass` 类调用 |
| H5 | C# 逻辑耗时占单帧时间超出 §9.4 的逻辑时间预算，且已验证无可省分配 | 下沉 C++ | Godot Profiler + `dotnet-trace`，连续多次采样 |
| H6 | 稳态托管分配超出 §9.4 的分配预算 | **先修分配，不要急着下沉**（代码问题优先于语言问题） | `GC.GetAllocatedBytesForCurrentThread()` |

**注意 H6 的方向**：分配超标是**代码问题**，解决顺序是 ARCHITECTURE.md §9.5 的「分配 → 算法 → 批处理/池 → 数据布局 → 语言下沉」，**不能**用"下沉 C++"来掩盖 C# 侧的分配浪费。

### 2.1 下沉流程（必须按序）

1. **先测**：用 profiler 证明热点在这里，不靠猜；
2. **先优化 C#**：分配 → 算法 → 批处理/池 → 数据布局（§9.5 顺序）；
3. **仍超标**才写下沉申请：写清指标、实测值、§9 对应条目、预期收益；
4. **定义 C# ↔ C++ 边界**（只暴露粗粒度 API，不允许每帧传大量对象）；
5. **落地 C++** + 给 C# 封装（`Game.Native`）+ 加回归基准场景；
6. **回写** ARCHITECTURE.md §9.3 的实测值列与变更日志。

> 跳步 1~3 直接写 C++ 属于"提前优化"，按 §11 反模式 P1 打回。

### 2.2 C# ↔ C++ 边界契约

| 契约 | 规定 |
|---|---|
| 调用粒度 | **粗粒度**：一次调用处理一批实体，不允许"每个实体跨一次界" |
| 数据形态 | 值类型 + 连续内存（`PackedFloat32Array` / `Span<T>` / `NativeArray`） |
| 零拷贝优先 | 传数组视图而非逐元素封送；避免每帧构造 C# 对象 |
| 生命周期 | C++ 侧不得持有 C# 对象引用跨越帧边界；需要回调用 ID/句柄 |
| 错误处理 | 返回错误码或 `bool`，**不抛异常**穿越语言边界 |
| 线程 | 明确标注是否线程安全；默认只在主线程调用 |
| 命名 | 导出给 C# 的类统一 `Native` 前缀（`NativeHitBatch`），C# 侧封装在 `Game.Native` |

### 2.3 性能阈值的唯一真源

> **阈值见 `docs/architecture/ARCHITECTURE.md` §9.2~§9.4（唯一真源），本文档只给判据不给数字。**

| 项 | 规定 |
|---|---|
| 真源位置 | `docs/architecture/ARCHITECTURE.md` **§9.2（性能基线总表，含同屏实体预算）**、**§9.3（实测口径）**、**§9.4（C# GC 分配预算与下沉判据）** |
| 本文档的角色 | 只**引用判据名称**（H1~H6）、**不复制任何数值**；选型时按判据名去 ARCHITECTURE.md 查当期阈值 |
| 冲突处理 | 本文档**不含数值**，因此不存在"数值打架"；若发现本文档出现具体阈值，视为**本文档缺陷**，直接删除 |
| 修改流程 | 调阈值 = 只改 `ARCHITECTURE.md` §9.2~§9.4 + 在该文件「变更日志」加一行。**本文档无需同步** |
| 新增/删除判据 | 只在判据**种类**变化时改本文档 §2 表（并同步 §11 反模式、ARCHITECTURE.md 的判据表） |
| 实测回填 | 每次性能任务把实测值回写 ARCHITECTURE.md §9.3 的「实测值」列，不回写这里 |

**为什么采取"只引用不复制"**：历史上本文档曾抄写过一套阈值（并一度与 ARCHITECTURE.md 不一致），结果两处数字各自漂移、以哪个为准无人能答。现在只保留 `H1~H6` 这 6 个**判据名**作为跨文档的稳定锚点，数值全部收敛到 §9.2~§9.4 一处。

**按判据查阈值的路径**：

| 判据 | 去 ARCHITECTURE.md 看哪一节 | 查什么 |
|---|---|---|
| H1 单帧调用次数 | §9.4 下沉判据总表 | 每帧调用预算 |
| H2 批量实体数 | §9.2 性能基线总表 | 同屏敌人 / 抛射物 / 粒子预算 |
| H5 C# 逻辑耗时占比 | §9.4 下沉判据总表 | C# 逻辑时间预算与下沉条件 |
| H6 稳态托管分配 | §9.4 GC 分配预算 | 每帧分配预算、GC0 次数预算 |

> H3 / H4 是**定性判据**（是否需要 RID 级渲染访问），没有数值，不依赖 §9。

---

## 3. C# 约定

### 3.1 工程文件（由 Godot 生成，不要手改）

| 文件 | 说明 |
|---|---|
| `BiuBiuOrigin.csproj` | `Sdk="Godot.NET.Sdk/4.7.2"`，`TargetFramework=net10.0`，由 Godot 编辑器生成；**不要手删、不要手改 SDK 版本** |
| `BiuBiuOrigin.sln` | 可选，供 Rider / VS 打开 |
| `.godot/` | 编辑器缓存，**已在 `.gitignore`** |

- 依赖只能来自**离线 NuGet 源** `E:\Godot\GodotSharp\Tools\nupkgs`（`GodotSharp` / `Godot.NET.Sdk` / `Godot.SourceGenerators` 4.7.2）；
- **禁止**从公网拉包：`nuget.config` 只保留离线源，新增第三方包需 Lead 批准并写入离线源。

### 3.2 命名空间分层

| 命名空间 | 职责 | 允许引用 |
|---|---|---|
| `Game.Core` | 接口、事件总线、数学、随机、对象池、存档 | 无（最底层） |
| `Game.Data` | Resource 定义、Excel 生成物加载与校验 | `Game.Core` |
| `Game.Actor` | 人物/怪物：状态、移动、动画驱动 | `Game.Core`、`Game.Data` |
| `Game.Combat` | 伤害结算、命中检测、反馈（HitStop/白闪/击退） | `Game.Core`、`Game.Data`、`Game.Actor` |
| `Game.Skill` | 技能系统**实现**与组合（`ISkill`/`ISkillTarget` 的**声明**在 `Game.Data`，见 §3.2.1） | `Game.Core`、`Game.Data`、`Game.Combat` |
| `Game.Spawn` | 刷怪、关卡编排、波次 | `Game.Core`、`Game.Data` |
| `Game.UI` | HUD、菜单、飘字（**只读数据 + 发事件，不做结算**） | ✅ `Game.Core`（只读接口）、✅ `Game.Data`（只读 DTO）；❌ `Game.Combat` / `Game.Actor` / `Game.Skill` / `Game.Spawn`；❌ `Native.*` |
| `Game.Native` | GDExtension 的 C# 封装层（`Native.*`） | `Game.Core` |

**依赖方向单向向下**。`Game.UI` 只允许 `using Game.Core` / `using Game.Data`（只读视图与 DTO），**禁止**引用 `Game.Combat` / `Game.Actor` / `Game.Skill` / `Game.Spawn`，也**禁止**直接调用 `Native.*`——守卫 `GS004` 按这条口径扫描。

> 完整矩阵见 [ARCHITECTURE.md §2.3](../architecture/ARCHITECTURE.md) 的「允许 / 禁止矩阵」，GS004 的官方口径见 §2.4 末段。

#### 3.2.1 关键接口的声明位置（与 ARCHITECTURE §3.2 保持一致）

接口的**声明位置**（interface 定义在哪个命名空间）与**实现位置**（class 文件所在目录）**可以不同**：

> **判定口诀：声明看「谁最底层要用」，实现看「这是谁的业务」。**

| 接口 | 状态 | 声明位置 | 实现位置 | 为什么这样分 |
|---|---|---|---|---|
| **`IVisualDescriptor`** | **正式接口**（已由候选转正） | `Game.Data` | `scripts/actor/` | 渲染层只认描述符、不感知画的是玩家还是怪物 ⇒「新增一种外观 = 0 个代码文件」（规则7）。它必须能让 Actor 与渲染侧同时看到，故声明在最底层能覆盖双方的 `Game.Data` |
| **`ISkill`** | 正式接口 | **`Game.Data`** | `scripts/skill/` | `SkillDef : Resource` 在 Data 层，数据装配需要引用 `ISkill`；而 `Game.Data` **不能**反向依赖 `Game.Skill`（§3.2 矩阵），所以**声明在 Data、实现在 Skill** |
| **`ISkillTarget`** | 正式接口 | **`Game.Data`** | `scripts/skill/` | 同上，`ISkill.Cast(ISkillTarget)` 的签名需要它，声明随 `ISkill` 一起下沉 |
| **`IActor`** | 正式接口 | `Game.Actor` | `scripts/actor/` | 生命周期/身份属于 Actor 业务 |

**`IActor` v1 的夺舍入口（与 ARCHITECTURE §3.2 逐字一致）**：

```csharp
public interface IActor
{
    ActorId Id { get; }
    IStatProvider Stats { get; }
    IVisualDescriptor Visual { get; }

    /// <summary>夺舍入口。目标不可夺 / 免疫 / 已在进行中时返回 false，不抛异常。</summary>
    bool TryApplyTakeover(in TakeoverSpec spec);
}
```

| 设计点 | 原因 |
|---|---|
| 方法名用 `TryApplyTakeover` 而非 `ApplyTakeover` | 夺舍**会失败**（目标不可夺、免疫、重复夺舍），失败是**预期分支**不是异常 |
| 参数 `in TakeoverSpec spec` | `readonly struct` 传引用，热路径零拷贝、零分配 |
| 返回 `bool` 而非抛异常 | 异常在热路径上是性能灾难，且失败需要被调用方分支处理 |
| 外观通过 `IVisualDescriptor` 间接作用于渲染 | 「换皮/夺舍」不新增代码文件，只换描述符实例（规则7） |

> 完整接口总表（含 `IDamageable`、`IAttacker`、`IDamageModifier`、`IMovementDriver`、`IProjectile`、`IStatProvider`、`IRandomProvider`、`IConfigSource`、`IPoolable`）见 [ARCHITECTURE.md §3.2](../architecture/ARCHITECTURE.md)。
> **本文档不复制接口总表**，只保留与规则 2（选型）相关的这几个，避免两处漂移。

### 3.3 禁用清单（gameplay 热路径）

| ❌ 禁止 | 为什么 | ✅ 替代 |
|---|---|---|
| LINQ（`Where`/`Select`/`ToList`） | 每帧分配 + 迭代器开销 | 手写 `for` 循环 + 复用缓冲 |
| 反射（`GetType()`、`Activator`、`PropertyInfo`） | IL2CPP / 热路径不可接受 | 显式接口 / 静态注册表 |
| 每帧 `new`（类） | GC 卡顿，破坏帧稳定性 | 对象池（`Game.Core.Pool`）、`struct` |
| `foreach` 中分配 | 部分集合的枚举器是 class | 索引 `for` |
| `GetNode<T>("path")` 在 `_Process` 里 | 每帧树查找 | `_Ready` 缓存 + `[Export]` 引用 |
| 通过信号每帧传大量参数 | 跨语言/跨节点开销 | 批量调用 + 直接引用 |
| 字符串拼接构造日志/名字 | 每次分配 | `StringBuilder` 复用 / `ZString` 风格 |

### 3.4 C# 写法要求

| 项 | 规定 |
|---|---|
| 可空性 | 开启 `<Nullable>enable</Nullable>`；公开 API 标注可空性 |
| 字段 | 私有字段 `_camelCase`；常量 `PascalCase` 或 `UPPER_SNAKE`，**常量必须是具名 `const`/`static readonly`，不允许裸字面量**（规则 4） |
| 值类型 | 小数据用 `readonly struct` / `record struct`；避免装箱 |
| 事件 | 用普通 C# `event` 或 `Game.Core.EventBus`，**不要**给每个逻辑类都挂 Godot `[Signal]` |
| 引擎耦合 | 逻辑层尽量不继承 `Node`；`Node` 只做"壳"（如 `ActorEntity : Node3D` 持有一个纯逻辑对象） |
| 文件组织 | 一个文件一个主要类型；文件名 = 类型名 |
| 注释 | 公共接口写 XML 文档注释；为什么这样写 > 写了什么 |

### 3.5 构建与脚本（**血泪经验：PowerShell 5.1 的编码陷阱**）

**症状**：在 Windows PowerShell **5.1** 下，`.ps1` 文件若保存为 **UTF-8 无 BOM** 且正文含中文，解析器会按**系统 ANSI 代码页（本机为 GBK/936）** 读取字节流；UTF-8 的中文字节被当成 GBK 双字节解释，极可能把一个汉字解成 `"` 或 `` ` `` 之类的语法字符，导致 **整个脚本语法崩溃**（报错位置还常常和真正的问题无关），甚至是诡异的"引号不配对"。

```text
UTF-8 无 BOM + 中文 + PowerShell 5.1
        ↓ 解析器按 GBK 读取
   "最后更新"  →  0xE6 0x9C 0x80 ... 被两两组合成 GBK 字符
        ↓ 其中一个字节恰好是 0x22 (")
   语法错误：项缺少结束标记
```

**因此本仓库所有 `.ps1` 必须二选一（不允许第三种）：**

| 方案 | 做法 | 适用 |
|---|---|---|
| **① 纯 ASCII 源码（推荐，当前采用）** | 源码里**不出现任何非 ASCII 字节**；中文用 `[string][char]0x53D8 + [char]0x66F4 ...` 之类的**码点构造**；需要直写输出时用 `[System.IO.File]::WriteAllText` 显式写 UTF-8 字节 | 所有长期维护的 `.ps1`（`check_architecture.ps1`、`env.ps1`） |
| **② UTF-8 带 BOM** | 文件保存为 UTF-8+BOM，BOM 让 5.1 正确识别编码，中文可直写 | 一次性脚本、或确定只在 5.1 上运行的短脚本 |

**配套铁律**：

| # | 规矩 |
|---|---|
| S1 | 写文件一律 `[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))` —— 5.1 的 `Out-File -Encoding utf8` **会加 BOM**，`Set-Content` 同理 |
| S2 | 读文件用 `[System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)`，避免 `Get-Content` 走 ANSI 默认 |
| S3 | 控制台输出前先 `[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false`，避免 GBK 下中文显示为 `??` |
| S4 | **禁用 PS7 专有语法**：`??`、`?:` 三元、`-Parallel`、`&&`/`\|\|` 管道链；只用 5.1 可解析的写法 |
| S5 | 改完必须实测：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\xxx.ps1`（用 5.1，不是 pwsh） |
| **S6** | **改文档时禁止用 `[System.Collections.ArrayList]` 包裹再写回**。`[System.Collections.ArrayList]@($t -split ...)` 会把整个数组**字符串化**成 `"System.Collections.ArrayList"`，一写入就把整份文件变成 28 字节垃圾。写文件前先 `$null -ne $joined -and $joined.Length -gt 1000` 做保护性断言 |

**适用范围（强制）**：**本仓库所有 `.ps1` 一律遵守本约定**，包括但不限于
`tools/check_architecture.ps1`、`tools/env.ps1`，以及将来任何 `tools/**`、`native/**`、`addons/**` 下的 PowerShell 脚本与内联脚本文件。
新增 `.ps1` 时**先选方案①或②再写第一行**，不允许"先写中文再补救"。

**落库前自查命令**（回显 `0` 即合规）：

```powershell
# 纯 ASCII 校验（方案①）：期望输出 0
$b = [System.IO.File]::ReadAllBytes('tools\check_architecture.ps1')
@($b | Where-Object { $_ -gt 127 }).Count

# 全仓库 .ps1 体检：列出所有不合规文件（既非纯 ASCII、又无 BOM 的即为违规）
Get-ChildItem -Recurse -Filter *.ps1 | Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $nonAscii = @($bytes | Where-Object { $_ -gt 127 }).Count
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if ($nonAscii -gt 0 -and -not $hasBom) { "VIOLATION: $($_.Name)  nonAscii=$nonAscii  no BOM" }
}
# 无输出 = 全部合规
```

**当前实测状态（2026-11-03）**：

| 脚本 | 采用方案 | nonAscii | BOM | 5.1 解析 |
|---|---|---|---|---|
| `tools/check_architecture.ps1` | ① 纯 ASCII | **0** | 无 | 0 错误 |
| `tools/env.ps1` | ① 纯 ASCII | **0** | 无 | 0 错误 |

---

## 4. GDExtension 约定（C++）

### 4.1 目录结构

```text
native/
├─ gdextension/
│  ├─ godot-cpp/              ← git submodule，分支 4.7
│  └─ gdextension_interface.h ← 由 --dump-extension-api 生成（首次需跑）
├─ src/                       ← C++ 实现（.cpp）
├─ include/                   ← 可选：对 C# 暴露的头文件
└─ build/                     ← 构建产物，已进 .gitignore
addons/biu_biu_native/bin/    ← .dll 的最终输出位置
```

### 4.2 godot-cpp 获取（版本必须匹配）

```powershell
git submodule add -b 4.7 https://github.com/godotengine/godot-cpp native/gdextension/godot-cpp
git submodule update --init --recursive
```

| 事项 | 说明 |
|---|---|
| ❌ 不要用 vcpkg 的 `godot-cpp` port | `D:\vcpkg` 里只有 **4.4**，与 4.7.2 引擎 ABI 不匹配 |
| ✅ 用 4.7 分支源码 | 与引擎小版本尽量对齐；升级引擎 = 同步升级此 submodule 并记录 |
| CMake | `D:\env\cmake\cmake-3.31.12-windows-x86_64\bin\cmake.exe` + `D:\env\ninja\ninja.exe` |
| 平台 | Windows x86_64，MSVC 工具链；**不需要** `-stdlib=libc++`（那是 macOS 的参数） |

### 4.3 CMakeLists.txt（可复制骨架）

> **状态：待第 1 个 native 任务落地时生成**。下面是骨架，正式落地时按实际模块名调整。

```cmake
cmake_minimum_required(VERSION 3.20)
project(biu_biu_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# 1) 自带的 godot-cpp 子目录（推荐：版本可控，与本仓库一起提交记录）
if(NOT TARGET godot-cpp)
    add_subdirectory(gdextension/godot-cpp)
endif()

# 2) 主干写法：直接链接官方 godot-cpp 目标（其头文件由该目标自带 INTERFACE_INCLUDE_DIRECTORIES）
file(GLOB_RECURSE SOURCES CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp"
)

add_library(biu_biu_native SHARED ${SOURCES})

target_include_directories(biu_biu_native PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

# 3) 静态链接 godot-cpp（Windows 默认形态）
target_link_libraries(biu_biu_native PRIVATE godot-cpp)

# 4) 输出到 addons/biu_biu_native/bin/，文件名必须与 .gdextension 的 [libraries] 键一致：
#    <平台>.<构建类型>.<架构>
set(BIN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../addons/biu_biu_native/bin")
set_target_properties(biu_biu_native PROPERTIES
    PREFIX ""
    RUNTIME_OUTPUT_DIRECTORY "${BIN_DIR}"
)
foreach(cfg Debug Release)
    string(TOUPPER "${cfg}" CFG_UPPER)
    set_target_properties(biu_biu_native PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${CFG_UPPER} "${BIN_DIR}"
        OUTPUT_NAME_${CFG_UPPER} "biu_biu_native.windows.${cfg}.x86_64"
    )
endforeach()
```

> ⚠️ `OUTPUT_NAME` 里的 `Debug` / `Release` 必须与 `.gdextension` 中 `windows.debug.x86_64` / `windows.release.x86_64` 的**文件名大小写完全一致**（Windows 虽然不区分大小写，但换到其他工具链/CI 会暴露问题）。
> ⚠️ 若改用 CMake `IMPORTED` 方式指向预编译的 godot-cpp，才需要显式 `GODOTCPP_TARGET`；`add_subdirectory` 方式不需要额外变量。

**构建命令**（不要手敲，后续应由 `tools/build_native.ps1` 封装）：

```powershell
$cmake = 'D:\env\cmake\cmake-3.31.12-windows-x86_64\bin\cmake.exe'
$ninja = 'D:\env\ninja\ninja.exe'
& $cmake -S native -B native/gdextension/build -G Ninja -DCMAKE_MAKE_PROGRAM=$ninja -DCMAKE_BUILD_TYPE=Release
& $cmake --build native/gdextension/build
```

### 4.4 `.gdextension` 配置

> **状态：待第 1 个 native 任务落地时生成**。`[configuration]` 段键名已用 `Godot_v4.7.2-stable_mono_win64_console.exe` 实测（`ConfigFile.get_value("configuration","entry_symbol")` 读取正确）。

```ini
; res://addons/biu_biu_native/biu_biu_native.gdextension
[configuration]

entry_symbol = "biu_biu_library_init"
compatibility_minimum = "4.7"
is_reloadable = true

[libraries]

windows.debug.x86_64 = "res://addons/biu_biu_native/bin/biu_biu_native.windows.Debug.x86_64.dll"
windows.release.x86_64 = "res://addons/biu_biu_native/bin/biu_biu_native.windows.Release.x86_64.dll"
```

| 键 | 值 | 说明 |
|---|---|---|
| `entry_symbol` | `biu_biu_library_init` | 必须与 C++ 里 `GDExtensionBool ..._init(...)` 导出的符号一致 |
| `compatibility_minimum` | `"4.7"` | 与引擎主版本对齐 |
| `is_reloadable` | `true` | 编辑器内重新编译后可直接重载 |
| `windows.debug.x86_64` / `windows.release.x86_64` | 生成的 `.dll` 路径 | 键格式固定为 `平台.构建类型.架构` |

**踩坑记录（实测）**：写成 `configuration/entry_symbol = "..."`（带斜杠的一整行）会导致 `--import` 报
`GDExtension configuration file must contain a "configuration/entry_symbol" key`。
正确写法是**分段**：`[configuration]` 段下的 `entry_symbol` 键。

### 4.5 C++ 侧最小骨架

```cpp
// native/src/register_types.cpp
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/godot.hpp>

using namespace godot;

void initialize_biu_biu_module(ModuleInitializationLevel p_level) {
    if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) return;
    // GDREGISTER_CLASS(NativeHitBatch);
}

void uninitialize_biu_biu_module(ModuleInitializationLevel p_level) {
    if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) return;
}

extern "C" {
GDExtensionBool GDE_EXPORT biu_biu_library_init(
        GDExtensionInterfaceGetProcAddress p_get_proc_address,
        GDExtensionClassLibraryPtr p_library,
        GDExtensionInitialization *r_initialization) {
    GDExtensionBinding::InitObject init_obj(p_get_proc_address, p_library, r_initialization);
    init_obj.register_initializer(initialize_biu_biu_module);
    init_obj.register_terminator(uninitialize_biu_biu_module);
    init_obj.set_minimum_library_initialization_level(MODULE_INITIALIZATION_LEVEL_SCENE);
    return init_obj.init();
}
}
```

---

## 5. 自定义渲染管线：三级优先顺序

### 5.1 默认立场：不改引擎源码

| 优先级 | 手段 | 何时用 | 代价 |
|---|---|---|---|
| **①** | **CompositorEffect**（Godot 4.x 官方后处理扩展点） | 屏幕空间后处理：调色、描边、色调映射、模糊 | 低：属官方扩展点，随引擎升级基本不破 |
| **②** | **GDExtension**（`native/`） | 需要 RID 级操作、自定义 `RenderPipeline`、几何体批处理；CompositorEffect 做不到的 | 中：需维护 ABI 兼容 |
| **③** | **fork 引擎源码** | 前两者都做不到（如改 Forward+ 内部光照结构） | 高：每升一次引擎就要 rebase，**默认拒绝** |

> 走 ③ 必须在 [ARCHITECTURE.md](../architecture/ARCHITECTURE.md) 记录：fork 的理由、与上游的差异清单、引擎版本锁定策略、升级预案。

### 5.2 CompositorEffect（优先级①的首选形态）

- 用 C# 或 C++ 实现 `CompositorEffect`，在 `.tres` 里配置参数；
- 加进 `Environment.compositor` 的 `compositor_effects` 数组；
- **参数必须可从外部配置**（`[Export]` / `.tres`），不允许写死（规则 4）。

---

## 6. Shader 约定

| 规矩 | 说明 |
|---|---|
| 自研优先 | 视觉表现一律自己写 `.gdshader`，不依赖第三方 shader 资源（可参考思路，不直接引入） |
| 参数外置 | 所有可调参数用 `uniform` 暴露，通过 `.tres`（`ShaderMaterial`）或 C# 设置；**禁止**写死在 shader 里的玩法数值 |
| 命名 | 文件 `snake_case.gdshader`；uniform 也 `snake_case`（如 `tint_color`、`rim_power`） |
| 风格 | Stylized Low-Poly Toybox：色块化、边缘描边、柔和光照，不用 PBR 写实路线 |
| 性能 | 优先 `spatial` shader 的顶点阶段；避免每帧重建材质；能用 `MultiMesh` 就别开 N 个实例 |
| 版本 | Godot 4.7 语法（`shader_type spatial;`），RHI 只保证 **d3d12** 跑通，勿写依赖 Vulkan 特性的代码 |
| 验证 | 新 shader 落地必须在 headless 4.7.2 下编译通过（见 [07-definition-of-done.md](07-definition-of-done.md)） |

---

## 7. 数据流：谁在什么时候用什么语言

```text
设计期（人）      Excel (data/excel/)
                      │
构建期（Python）   tools/data_pipeline/  ──校验①②③④──▶ data/generated/*.tres + index/*.json
                      │
运行期（C#）       Game.Data 加载 / 校验 (C#)
                      │
                  Game.Actor / Game.Combat / Game.Skill / Game.Spawn  ← 逻辑全部 C#
                      │                     │
                  Game.UI（只读）      Game.Native ──▶ native/*.dll（只有下沉的必要部分）
                      │                     │
                  .tscn 场景 + shaders/*.gdshader（表现层）
```

| 阶段 | 语言 | 位置 | 判据 |
|---|---|---|---|
| 数据编辑 | — | `data/excel/` | 唯一真源，**禁止**手写数值到代码 |
| 数据生成 | Python | `tools/data_pipeline/` | 构建期一次性，不进游戏包 |
| 数据加载/校验 | C# | `scripts/data/` | 启动期一次 |
| 玩法逻辑 | C# | `scripts/{actor,combat,skill,spawn}/` | §2 未达判据 |
| 热路径 | C++ | `native/src/` | §2 达到判据 |
| 表现 | `.gdshader` + C# 驱动 | `shaders/`、`scripts/ui/` | 视觉不改变游戏状态 |
| 构建/校验 | PowerShell 5.1 | `tools/*.ps1` | §3.5 编码铁律 |

---

## 8. 与架构守卫的对应

| 规则 | 级别 | 检查内容 | 出处 |
|---|---|---|---|
| `GS003` | error | C++ 源文件（`.cpp`/`.h`/`.hpp`）出现在 `native/` 之外 | §4.1 目录结构 |
| `GS004` | warning | `scripts/ui/**` 下的 `.gd` / `.cs` 出现 `Native.`，或引用 `Game.Combat` / `Game.Actor` / `Game.Skill` / `Game.Spawn`（`using Game.Core` / `using Game.Data` **放行**；注释行与行尾注释不误报） | §3.2 依赖方向与 `Game.UI` 行；官方口径见 ARCHITECTURE §2.4 |
| `GS007` | warning | `.csproj` 的 `AssemblyName` 或 `.cs` 的 `namespace` 以 `Game.` 开头，但文件不在 `layers.logicRoots`（默认 `scripts/`）下 | §3.2 命名空间分层 |

### 8.1 守卫配置的关键字段（与本文档强相关）

配置文件：`config/architecture-rules.json`（**放行白名单唯一来源**）

| 字段 | 本文档对应章节 | 作用 |
|---|---|---|
| `layers.nativeRoot` | §4.1 | `GS003` 用它判断 C++ 是否在 `native/` 下 |
| `layers.logicRoots` | §3.2 | `GS007` 用它判断 `Game.*` 是否在正确目录下 |
| `layers.uiRoot` | §3.2 / §5 | `GS004` / `GS002` 的 UI 边界来源 |
| `numericLiterals.exclude` | 规则 4 | `scripts/ui/**`（布局常量）与 `scripts/core/**`（结构性常量）不参与 GS005 |

### 8.2 GS005 最终判定逻辑（可直接引用到 AGENTS.md）

**一句话**：`scripts/` 下（排除 `scripts/ui/**` 与 `scripts/core/**`）的 `.cs` 文件里，出现在**赋值号 `=` 右侧**、且不在 `allowValues` 里的数值字面量 = 违规；`// arch-ok:` 可逐行豁免。

判定按顺序执行，**第一个命中的分支即决定结果**：

| 顺序 | 判断 | 结果 |
|---|---|---|
| 1 | 文件不在 `numericLiterals.scope`（默认 `scripts/**/*.cs`）内 | **不检查** |
| 2 | 文件命中 `numericLiterals.exclude`（默认 `scripts/ui/**`、`scripts/core/**`） | **不检查**（`exclude` 优先于 `scope`） |
| 3 | 文件命中 `ignore`（如 `obj/**`、`bin/**`） | **不检查** |
| 4 | 该行是注释行（`//`、`/*`、`*`、`#`）、块注释内部，或该行含 `escapeComment`（默认 `// arch-ok:`） | **放行**（`arch-ok` 按**整行**豁免，可写行尾） |
| 5 | 该行**没有**赋值号 `=`（排除 `==`、`>=`、`<=`、`!=`、`=>`、`+=` 等） | 不产生条目 |
| 6 | `=` 右侧的数字字面量，转小写后在 `allowValues` 内（默认 `0, 1, -1, 0.0, 1.0, 0.5`，含 `0f/1f/0.5f/-1f` 等带后缀写法） | **放行** |
| 7 | 其余（浮点非白名单值、绝对值 ≥ 2 的整数） | **违规**，warning |

补充细节：

- **负号归属**：`a = -2.5f` 按 `-2.5f` 判断（`-1` 因在 `allowValues` 内而放行）；
- **字符串/字符字面量先剔除**：`"value 9.75"`、`'0.25'` 不计为数值；
- **输出上限**：只展示前 `maxReported`（默认 50）条，但统计行给出 `reported / shown / hidden` 三个总数，不会漏报总数；
- **`arch-ok` 不是免死金牌**：规则 4 规定单个文件 > 5 处豁免需转人工 Review；`scripts/core/**` 完全豁免于 GS005，属**已知风险点**（见 P9），靠代码审查兜底。

---

## 9. 选型速查表（贴在显示器上）

| 你在写… | 先问 | 结论 |
|---|---|---|
| 一个菜单 | 每帧调用几次？ | 少量 → **C#** |
| 一个 buff 结算 | 需要 GPU 吗？ | 不需要 → **C#** |
| 大规模子弹的碰撞 | 每帧调用次数是否超出 §9.4 预算？ | 未超 → **先 C#**；超出 → profiler 再决定 C++ |
| 屏幕空间的描边 | 能只改材质吗？ | 能 → **`.gdshader`** |
| 一个多 pass 的水面 | 能只改材质吗？ | 不能 → **C++ GDExtension** |
| Excel 转 tres | 运行期需要吗？ | 不需要 → **Python（构建期）** |
| 一键打包脚本 | — | **PowerShell 5.1**（注意 §3.5 编码） |

---

## 10. 常见任务的参考实现路径

| 任务 | 落地步骤（语言标注） |
|---|---|
| 新增一把武器 | 1) `combat.xlsx` 的 `Weapons` 加一行（人）→ 2) 跑 data_pipeline（Python）→ 3) 若需特殊行为，写 `IWeapon` 实现（C#） |
| 新增一种技能 | 1) `combat.xlsx` 的 `Skills` 加一行 → 2) 写 `ISkill` 实现（C#，`scripts/skill/`）→ 3) 效果 `.tres` 引用 |
| 新增一种敌人 | 1) `characters.xlsx` 加行 → 2) `IVisualDescriptor` 配置外观 → 3) 行为复用现有 `IMovementDriver`（C#） |
| 加一个受击特效 | 1) `presentation.xlsx` 的 `Effects` 加行 → 2) `shaders/` 写 `.gdshader` → 3) C# 在 `Game.Combat` 触发 |
| 加一个后处理 | 1) 先试 `.gdshader` 能否做到 → 2) 需 pass 结构则 `CompositorEffect` → 3) 仍不够才 `native/` |
| 优化帧率 | 1) profiler 定位 → 2) 按 §9.5 顺序先修分配 → 3) 仍超 §9 预算才下沉 C++ |
| 加一个可视化调参面板 | `addons/` 下写 C# / `@tool` GDScript（规则 1 允许） |

---

## 11. 反模式清单（12 条）

| # | 反模式 | 后果 | 正确做法 |
|---|---|---|---|
| **P1** | "性能可能不够，先写 C++" | 提前优化，开发效率暴跌，维护成本上升 | 先 C# + 实测，达 §2 判据才下沉 |
| **P2** | 用 GDScript 写 gameplay（GDScript 写 AI、结算） | 违反规则 1，且两套语言范式 | 挪到 `scripts/` 的 C# |
| **P3** | 在 C# 里 `foreach` + LINQ 遍历大批量实体 | GC 抖动，帧时间不稳定 | 手写 `for` + 池化数组 |
| **P4** | 每帧 `new Vector3()` / `new List<T>` | 分配压力 | `struct` + 复用缓冲 |
| **P5** | 用 `GetNode<T>("../X")` 在 `_Process` 里找节点 | 每帧树遍历 | `_Ready` 缓存 |
| **P6** | 每帧跨 C++ 边界传几千个对象 | 封送开销远超计算收益，比纯 C# 还慢 | 粗粒度批量 API + 连续内存 |
| **P7** | 视觉参数写死在 shader 里 | 改一个颜色要改代码，违反规则 4 | `uniform` + `.tres` |
| **P8** | 改引擎源码解决问题 | 升级即地狱 | 优先 CompositorEffect → GDExtension → fork（明确记录） |
| **P9** | 把数值塞进 `scripts/core/`（GS005 不扫这里） | 逃逸规则 4 的检查，本质仍是硬编码 | 数值进 Excel；`core` 只放结构性常量，且需 `// arch-ok:` |
| **P10** | UI 层直接调 `Game.Combat` 结算 / `Native.*` | 破坏分层，UI 能改游戏状态 | 只读 `Game.Core` / `Game.Data`，改动发事件 |
| **P11** | 在 `.ps1` 里直写中文且存为无 BOM UTF-8 | 5.1 按 GBK 解析 → 语法崩溃（§3.5） | 纯 ASCII 源码 / UTF-8+BOM 二选一 |
| **P12** | 引入公网 NuGet 包 / 第三方 shader 资源 | 离线环境断链、来源不可控 | 只用离线源；shader 自研 |

---

## 12. 待 Lead 仲裁 / 已知取舍

| # | 议题 | 当前实现 | 备注 |
|---|---|---|---|
| C5 | `maxReported: 50` 是否会漏掉第 51 条以后的硬编码 | 守卫只**展示**前 50 条，但统计里给出 `reported / shown / hidden` 三项总数 | 若需全量输出，改 JSON 的 `maxReported` 即可，无需改脚本 |
| C6 | `scripts/core/**` 不参与 GS005 | 按 Lead 的配置执行 | 存在"结构性常量"借此藏数值的风险（P9），靠代码审查兜底 |
| C7 | 阈值 H1~H6 的定义位置 | **已裁决**：本文档不复制数值，全部指向 ARCHITECTURE.md §9.2~§9.4；本文档只保留判据名 H1~H6 | 历史上一度写死为旧的一套数值，与真源冲突，已删除 |
| C8 | `GS004` 的匹配范围（**已裁决**） | 正则 `Game\.(Combat|Actor|Skill|Spawn)(?![A-Za-z0-9_])` + `Native\.[A-Za-z_]`；`using Game.Core` / `using Game.Data` 放行；行尾注释先剥离，注释里的字样不误报 | 已知局限：写在**字符串字面量**里的 `"Game.Combat"` 仍会命中，属可接受误报，靠人工复核 |

---

## 13. 自检清单（提交前手过一遍）

- [ ] 新写的逻辑默认在 `scripts/` 的 C# 里，没有为"性能"提前下沉 C++（P1）
- [ ] 若新增 `native/` 下 C++：`GS003` 无报错，`.gdextension` 用 `[configuration]` + `entry_symbol`（不是 `configuration/entry_symbol`）
- [ ] C# 热路径无 LINQ / 反射 / 每帧 new（§3.3）
- [ ] `Game.UI` 没有 `using Game.Combat` / `Game.Skill`（`GS004`）
- [ ] 视觉参数走 `uniform` + `.tres`，没有写死在 shader（规则 4）
- [ ] 没有从公网拉 NuGet 包
- [ ] 若新增/修改了 `.ps1`：**纯 ASCII 或 UTF-8+BOM**，且用 PS 5.1 实跑通过（§3.5）
- [ ] 若下沉了 C++：已回写 ARCHITECTURE.md §9.3 实测值与变更日志
- [ ] 本文档**没有出现新的具体阈值数字**（§2.3）

---

## 变更日志

| 日期 | 版本 | 变更 |
|---|---|---|
| 2026-11-03 | v1 | 建立规则 2 正文：决策矩阵、C++ 下沉硬判据、C#/GDExtension/shader 约定、渲染管线三级顺序、守卫 GS003/GS004/GS007 对应、12 条反模式 |
| 2026-11-03 | v1.1 / v1.2 | 按 Lead 两轮口径收口：① **§2 判据表与 §2.3 不含任何具体阈值**，只保留判据名 `H1~H6` +「阈值见 `ARCHITECTURE.md` §9.2~§9.4（唯一真源），本文档只给判据不给数字」，并清掉 C7 / §9 速查表 / §2 流程里的历史数字副本（曾与真源打架）；② 新增 §3.5「构建与脚本」：PS 5.1 把无 BOM UTF-8 当 GBK 解析的血泪坑、本仓库**所有 `.ps1`** 的「纯 ASCII 源码」或「UTF-8 带 BOM」二选一铁律、落库前全仓库体检命令与实测状态；③ §3.2 依赖方向与 §8 `GS004` 口径：UI 允许 `using Game.Core` / `Game.Data`，禁止 `Game.Combat`/`Game.Actor`/`Game.Skill`/`Game.Spawn` 与 `Native.*`；④ 新增 §3.2.1「关键接口的声明位置」对齐 ARCHITECTURE §3.2（`IVisualDescriptor` 正式接口、`ISkill`/`ISkillTarget` 声明在 `Game.Data`、`IActor.TryApplyTakeover`）；⑤ 新增 §8.2 GS005 最终判定逻辑；⑥ 清理 §4.3 CMake 骨架中不存在的 `GODOTCPP_TARGET` 与错误的 include 路径；⑦ §3.5 增补铁律 S6（禁止用 ArrayList 包裹写回文档，曾导致本文件被截断） |
| 2026-11-03 | v1.3 | 因误操作（ArrayList 字符串化写回）导致本文件被覆盖为 28 字节，已按 v1.2 内容完整重建，结构与章节编号不变，并将该事故的防护规则写入 §3.5 铁律 S6 与反模式 P11 |
