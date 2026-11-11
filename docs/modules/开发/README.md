# 开发模块 · 索引 README（作业纪律 / 目录契约 / 选型 / 构建校验 / 坑清单）

> 版本: v1 ｜ 建立日期: 2026-11-11 ｜ 产出任务: `t-dev`（负责人 `engineer`）
> 性质: **索引文档（只增不改）**。本文只做「去哪读、先做什么、别踩什么」，**不复制任何原文契约正文**
> （命令阈值、表结构、守卫判定、语言矩阵一律以原文档为准）。凡与原文冲突，**一律以原文为准**；
> 发现原文档过时或与实现不一致的，写进本文 §8「待核对」，**不直接改原文档**。
> 基线: 模块边界与文档归属见 [../_shared/BOUNDARY.md](../_shared/BOUNDARY.md)（§2.2④ 开发 In/Out、§4.1 主归属 6 份、§4.2 ARCH 逐节切分）。

---

## 0. 三十秒速读

| 你问 | 答案 |
|---|---|
| 这个模块管什么 | 「**所有人怎么干活**」：开工收工的命令级顺序、代码放哪、用什么语言、构建与守卫怎么过、文档怎么就地更新、哪些坑踩过 |
| 我第一步跑什么 | `.\tools\env.ps1 -Verify`（§2.1 第 ④ 步）；只看 `FAIL`，`PEND` 属正常的「待落地」 |
| 收工一条命令 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_all.ps1`（§3.3 一键四步），**但它不替代** `ARCHITECTURE.md` 就地更新 |
| 有第二个会话在并行吗 | 有 → 开工第一件事是 `.\tools\session.ps1 start -Name <会话名>`，在 `main` 上直接开发 = 违规（§2.2） |
| 代码放哪 | 目录契约 `ARCHITECTURE.md` §1.3（谁写谁读 + 禁写），语言分层 §2.3 矩阵；白名单唯一来源 `config/architecture-rules.json`（§4） |
| 用什么语言 | 默认 C#；渲染 / 热路径下沉 C++（`native/`）；外观 `.gdshader` + `uniform`；GDScript 只在 UI 层与探针白名单（§5） |
| 踩过哪些坑 | §7 坑清单 20 条，**每条都给出处**，其中「静默失效」类 11 条 |
| 新人第一天怎么读 | §6 一天时间表（约 4 小时读完 + 1 次环境验证） |

---

## 1. 这个模块负责什么（3~5 行）

- **流程**：开工固定动作（读 → 预判 → 验环境 → 才写代码）与收工 Definition of Done（六步检查表 + 会话七步外壳）的命令级清单。
- **结构**：目录契约（谁写谁读、禁写什么）、六层单向依赖与允许/禁止矩阵、白名单唯一放行渠道。
- **语言**：C# / C++ GDExtension / `.gdshader` / GDScript / Python / PowerShell 5.1 的选型判据与下沉流程。
- **校验**：`tools/` 下五件套（`env.ps1` / `check_architecture.ps1` / `check_all.ps1` / `session.ps1` / `fix_godot_scan_dirs.ps1`）的用法与退出码。
- **记忆**：文档里记录过的「静默失效」坑，逐条给出处，让后来者不必重新炸一遍。

> **不是**：本模块不定义「架构形状」（ECS 架构师）、不定义数值与表结构（策划）、不定义房间规则（地编）、不定义角色包组合（角色）——见 §9。

---

## 2. 我必读的文档

### 2.1 主归属（6 份，本模块是它们的「家」）

| 文档 | 必读章节 | 为什么读 | 行数 |
|---|---|---|---|
| [AGENTS.md](../../../AGENTS.md)（仓库根，入口） | §0 三十秒速读、§1 开工固定动作、§2 八条铁规、§3 红线清单、§4 白名单登记表、§5 目录契约、§6 技术栈事实、§7 收工动作、§9 文档索引 | 项目唯一启动入口；开收工动作的原文 | ~250 |
| [rules/01-gdscript-ban.md](../../rules/01-gdscript-ban.md) | §2「UI 层」唯一定义、§3 白名单三步登记、§4 误用→替代写法表、§5.1 probes 特批、§7 GS001/GS002 口径、§9 自检清单 | 决定「这个 `.gd` 能不能写」 | 296 |
| [rules/02-tech-selection.md](../../rules/02-tech-selection.md) | §1 T1~T10 决策矩阵、§2 H1~H6 下沉判据 + §2.1 下沉流程 + §2.2 边界契约、§2.3 阈值唯一真源（只给判据名）、§3.2 命名空间 / §3.3 热路径禁用清单 / §3.5 PS 5.1 编码铁律、§4 GDExtension、§5 渲染三级顺序、§6 shader、§8 守卫对应、§9 速查表、§11 反模式 | 选型与构建脚本的全部判据 | 595 |
| [rules/07-definition-of-done.md](../../rules/07-definition-of-done.md) | §1.3 红线、§2 六步检查表、§3 扩展性走查、§4 章节影响矩阵、§5 就地覆盖 SOP（含 §5.4 时间戳 / §5.5 日志标题）、§6 日志写法、§7「不改架构也要留一行」、§8.1 读取协议 / §8.2 路由表 / §8.3 影响预判 | 收工动作的原文；**全模块共用** | 466 |
| [rules/08-parallel-sessions.md](../../rules/08-parallel-sessions.md) | §0、§2 六步、§3 主线纪律、§4 冲突处理、§5 目录隔离、§6 共享但不可并发、§7 `ARCHITECTURE.md` 合并特例、§8 反例、§10 `session.ps1` 速查与退出码、§11.3 编码规范 | 多会话并行的硬要求；**全模块共用** | 405 |
| [prompts/QUICK-START.md](../../prompts/QUICK-START.md) + [prompts/task-prompt-template.md](../../prompts/task-prompt-template.md) | 全量版 §0 使用方法、§1 模板正文、§3 开工必读清单 + 章节路由表、§4 收工清单、附录 A 红线 | 怎么派活 / 怎么接活（派任务给 AI 时用它） | 34 + 306 |

### 2.2 次归属（`ARCHITECTURE.md` 里属于本模块的章节，按 BOUNDARY §4.2 切分）

> `ARCHITECTURE.md` 的「家」在 ECS 架构师（托管 ≠ 独占），本模块只读下列部分，**不必通读 3,228 行**。
> 行号是 2026-11-11 实测快照，文档会就地更新，**以章节号为准**。

| 节 | 标题 | 起始行 | 本模块读它的理由 |
|---|---|---|---|
| 文件头 | 状态 v17 / 维护者必读（GS006 判定规则） | 1 | 时间戳与日志标题由守卫判定，改文档的人都要知道怎么改不会被判 stale |
| §0 | 一页速览 TL;DR | 25 | 技术栈事实、校验工具入口、完成定义的一句话 |
| §1 | 工程结构（§1.3 职责/所有者/准入规则、§1.4 约束） | 50（§1.3 在 139） | **目录契约原文**：每个目录谁能写、禁写什么 |
| §2 | 分层与依赖规则（§2.2 六层单向、§2.3 矩阵 + 两条 ECS 专属硬约束） | 180（§2.3 在 234） | 反向依赖是硬红线；`Game.Host` / `Game.Core.Ecs` 的边界 |
| §4 | 数据流与管线（§4.4 校验点汇总、§4.5 生成物 vs 手改、§4.8 约束） | 404 | 改表之后的链路、`data/generated/` 为什么禁止手改、pipeline 为什么必须幂等 |
| §7 | 渲染与特效架构（§7.5.1 stretch、§7.5.2 Node2D 布局） | 848 | 两个窗口缩放类静默失效坑的原文 |
| §8 | UI 层（唯一允许 GDScript 的层） | 1026 | UI 分界与「UI 内部语言选型」 |
| §9 | 性能基线与预算（**§9.4 阈值唯一真源**） | 1089 | 下沉 C++ 的数字只在这里；`rules/02` 只给判据名 |
| §10 | 已知技术债与风险 R1~R15 | 1168 | 收工自检时避开已知雷（如 R12 旧原型、R14 csproj 手维护） |
| §16 | 素材快查看器 v1（`tools/asset_viewer/`，Win32+D3D11） | 1865 | 开发期独立工具；§16.5 血泪教训 |
| §24 | 3D 素材查看器（`tools/asset_viewer2/`，Vulkan+ImGui DockSpace） | 3106 | §24.1 为什么放 `tools/` 不放 `native/`、§24.4/§24.7 静默失效坑 |

**一段式引用（只读相关小节，不进上表）**：§11 变更日志首屏、§12 待办与下一步（P0~P3）——所有模块都读，见 §6。

---

## 3. 开工固定动作与收工 Definition of Done（命令级清单）

> 本节只给「**跑什么、看什么、什么算过**」；每条的原文出处写在行末。命令一律在**项目根或当前会话 worktree 根**执行。

### 3.1 开工：固定顺序（AGENTS.md §1 六步 + rules/08 第 ⓿ 步）

| # | 动作 | 命令 / 产出 | 通过判据 | 出处 |
|---|---|---|---|---|
| ⓿ | **开会话（有第二个会话并行时必做）** | `.\tools\session.ps1 start -Name <会话名>` → `cd ..\.worktrees\<会话名>` | 打印 worktree 绝对路径；**之后所有读写与构建都在该目录里** | AGENTS.md §1 ⓿、rules/08 §2.1~§2.2 |
| ① | 读入口契约 | 读完 `AGENTS.md` 全文 | —— | AGENTS.md §1 |
| ② | 读架构相关章节 | `ARCHITECTURE.md` 文件头 + §0 + §11 首屏 + §12，再按 `rules/07` §8.2 路由表读对应节 | 能一句话说清「现在是什么状态、最近谁改了什么」 | AGENTS.md §1 ②、rules/07 §8.1 |
| ③ | 读本次任务对应的 rules 全文 | 按任务类型查 `rules/07` §8.2 路由表 | 知道这次要遵守哪几条铁规 | AGENTS.md §1 ③ |
| ④ | 验环境 | `.\tools\env.ps1 -Verify` | **只看 `FAIL`**——`PEND`（csproj / native / data 目录未创建）属正常的「待落地」，不阻塞 | AGENTS.md §1 ④、`tools/env.ps1` 文件头 USAGE |
| ⑤ | 写三件套 + 影响预判 | 任务描述里写「边界 · 抽象 · 扩展点」（规则5），并输出 `rules/07` §8.3 的「本次任务影响预判」块 | 预判里列清「必改章节 / 不改章节 / 需 Lead 仲裁」 | AGENTS.md §1 ⑤、rules/07 §8.3 |
| ⑥ | 才允许写代码 | —— | —— | AGENTS.md §1 |

**额外的两处必须知道**：

- **写玩法代码前还要读 ECS**：`ECS.md` §1 七条判定 + §4.2 组件类别表；**任何新增玩法逻辑 = 新增一个 `SystemBase` 子类**（AGENTS.md §1 末段）。细节归 ECS 模块。
- **环境探测的三种用法**：`.\tools\env.ps1`（汇总表）/ `-Json`（机器可读单行）/ `-Verify`（逐项 PASS/FAIL，**有任何 FAIL 退出码 1**）。可用环境变量覆盖本机默认路径（`GODOT_BIN` / `DOTNET_NUGET_SOURCE` / `CMAKE_BIN` / `NINJA_BIN` / `DSH_PYTHON` 等），见 `tools/env.ps1` 文件头。

### 3.2 收工：Definition of Done（六步检查表，逐项勾选）

> 原文是 `rules/07` §2 的整段检查表，**任何一项未勾 = 任务未完成**；只有第 ⑤ 步能自动化。下表是「怎么跑、看什么」。

| 步 | 检查项 | 命令 / 动作 | 通过判据 |
|---|---|---|---|
| ① | 抽象先行（规则5） | 先查 `ARCHITECTURE.md` §3.2 接口清单；新增接口回写该表 | 没有「先搭临时结构、以后再说」 |
| ② | 数值零硬编码（规则4） | `.\tools\check_architecture.ps1`，看 GS005 | **无「新增」的 GS005 条目**；必须留的结构性常量写 `// arch-ok: <理由>` |
| ③ | 扩展性走查（规则7） | 按 `rules/07` §3 判据表逐条核；写下「新增一个 X 需要改 N 个文件」 | 全部在上限内（上限数字见 `rules/06` §2） |
| ④ | 数据契约校验通过 | 改过表：用 DSH 自带 Python 跑 `tools\data_pipeline\validate_content_xlsx.py` | 打印 `OK: <n> 项检查通过`、退出码 0；代码里没有直接读 `.xlsx` |
| ⑤ | 架构守卫无 error | `.\tools\check_architecture.ps1` | **退出码 0 且 `new=0`**；新增 warning 须在汇报里说明；确需接受基线变化才用 `-UpdateBaseline` |
| ⑥ | 就地覆盖 `ARCHITECTURE.md` | 改对应章节正文 + §11 追加一行 + 文件头「最后更新」改今天 | 没有新建 `ARCHITECTURE_v2.md` / 副本 / 分卷；GS006 不报 stale |
| ⑦ | 合并到主线（仅并行时） | 见 §3.4 会话七步 | 合并后在 `main` 上复跑 build + 守卫 |

**什么情况下正文可以不改但仍要留日志**：纯美术资源 / 纯数值调参 / 纯 bug 修复 / 纯命名格式化——正文不动，但**文件头时间戳 + 变更日志一行（写「未改架构」）+ 守卫退出码 0** 三件照做（`rules/07` §7）。

### 3.3 一键校验：`tools/check_all.ps1`（收工前最省事的一条命令）

```powershell
# 项目根执行；默认跑满 4 步
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_all.ps1
```

| 步骤 | 内容 | 备注 |
|---|---|---|
| 1/4 | `dotnet build`（默认配置） | 产出到 `.godot/mono/temp/bin`，让 Godot 编辑器保持可用 |
| 1b/4 | `dotnet build`（`Host` 配置） | **`dotnet build` 单独跑不覆盖 `scripts/host/**`**（唯一允许 `using Godot` 的层），漏这步会让坏掉的宿主层蒙混过关 |
| 2/4 | `tools/check_architecture.ps1` | 守卫 |
| 3/4 | `dotnet test`（`tests/BiuBiuOrigin.Tests.csproj`） | |
| 4/4 | `tools/dungeon_check`（默认 200 seed 地牢性质校验） | 独立控制台程序，因测试工程**不得**编译 `scripts/spawn/rooms/**` |

**关键行为与开关**（出处：`tools/check_all.ps1` 文件头）：

- 任一步失败 → 非 0 退出码；**所有步骤都会跑完**再汇报，退出码取第一个失败步骤的码。
- `-SkipTests`（只跑 build×2 + 守卫 + 地牢；此时 exit 0 **不再**代表测试绿）/ `-SkipDungeon` / `-DungeonSeeds N`（默认 200）/ `-NoRestore` / `-ProjectPath`。
- 测试工程**刻意不用 `ProjectReference`** 引用主工程，而是直接 `<Compile Include="..\scripts\core\**\*.cs">`——为了守住「`Game.Core` 零 Godot 依赖」这条证明。**改这个工程结构前先读文件头 BLOCKING RULE**。

### 3.4 多会话并行：`tools/session.ps1` 六步 + 收工七步

**规则 8 六步**（rules/08 §2，一步都不能跳）：

| 步 | 命令 | 干什么 |
|---|---|---|
| ① 开会话 | `.\tools\session.ps1 start -Name <会话名>` | 等价 `git worktree add ..\.worktrees\<名> -b session\<名> main`；**已存在则拒绝**，不要换名字重来 |
| ② 隔离作业 | `cd ..\.worktrees\<会话名>` | 读写、`dotnet build`、守卫、改 `ARCHITECTURE.md` **全部在此目录**；随时 `.\tools\session.ps1 status -Name <会话名>` 自查变更清单 |
| ③ 收工自检 | 在 worktree 内走完 §3.2 的 ①~⑥ | —— |
| ④ 提交 | `.\tools\session.ps1 commit -Name <会话名> -Message "<会话名>: <一句话>"` | 提交信息前缀 = 会话名 |
| ⑤ 合并回线 | `.\tools\session.ps1 finish -Name <会话名>` | 检查脏 → 回主工作树 → rebase 到最新 `main` → `merge --no-ff`；冲突则停下给指引 |
| ⑥ 清理 | `.\tools\session.ps1 stop -Name <会话名>` | `worktree remove` + `branch -d`；**未合并则拒绝**，工具没有 `-Force` |

**收工扩成七步的两处硬要求**：

1. **`finish` 不会替你跑架构守卫**——合并完成后由**最后合并者**在 `main` 上复跑 `dotnet build` + `check_architecture.ps1`（两边各自绿 ≠ 合起来绿）。
2. **`ARCHITECTURE.md` 冲突不按行覆盖**：保留双方章节 + 变更日志两边都保留，最后由合并者刷新时间戳并跑守卫确认 GS006（rules/08 §7）。

**退出码约定（脚本化 / agent 判断用）**：`0` 成功、`1` 参数错误、`2` 不是 git 仓库、`3` 找不到 git、`4` 会话/worktree 不存在或已存在、`5` git 命令失败、`6` `finish` 前有未提交改动、`7` rebase 冲突已停下、`8` merge 失败已 abort、`9` `stop` 拒绝（未合并/脏）、`10` `stop` 移除失败（占用）（rules/08 §10）。

**命名规范**：`<功能或模块>-<可选后缀>`，只允许小写字母/数字/`-`，≤ 24 字符，不复用旧名（rules/08 §9）。

### 3.5 环境噪声清理：`tools/fix_godot_scan_dirs.ps1`

Godot **不读 `.gitignore`**，会把构建目录里的 `.obj` 当 3D 模型导入（详见 §7 #11）。

```powershell
.\tools\fix_godot_scan_dirs.ps1 -Check   # 只检查：0=就绪，10=有缺失
.\tools\fix_godot_scan_dirs.ps1          # 补齐空 .gdignore（幂等，可反复跑）
```

`.gdignore` 因目录被 `.gitignore` 排除而**永远进不了库**，所以「克隆后 / 首次构建前跑一次」是常规动作；`env.ps1 -Verify` 里有一项 `.gdignore` 检查（optional，不阻塞）。

### 3.6 守卫 `tools/check_architecture.ps1` 速用

| 用法 | 作用 |
|---|---|
| `powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_architecture.ps1` | 默认全量扫描；**有新 error（未进基线）→ 退出码 1**，只有 warning → 0 |
| `-UpdateBaseline` | 把当前全部违规写进基线（默认 `docs/architecture/.guard-baseline.json`，首次运行时生成；未生成时全部条目都算 new），**总是退出 0**——只在判定「这些违规是有意接受」时用，并在汇报里说明理由 |
| `-AsJson` / `-OutFile <path>` | 机器可读输出 / 额外落一份报告（UTF-8 无 BOM） |
| `-Quiet` | 只打汇总行与修复清单 |

检查项（**级别与语义以脚本 `-Help` 头与 `rules/01` §7、`rules/02` §8 为准**）：

| 编号 | 级别 | 查什么 | 规则 |
|---|---|---|---|
| GS001 | error | `.gd` 在白名单外 / 落在 `forbidGDScript` 内 | 规则1 |
| GS002 | warning | UI 目录里 `.gd` 与 `.cs` 混放 | 规则1/2 |
| GS003 | error | `*.cpp` / `*.h` / `*.hpp` 出现在 `native/` 之外 | 规则2 |
| GS004 | warning | `scripts/ui/**` 引用 `Native.*` 或 `Game.Combat/Actor/Skill/Spawn` | 规则2 |
| GS005 | warning | `scripts/**/*.cs` 里的硬编码数值（细则见 `rules/02` §8.2 七分支判定） | 规则4 |
| GS006 | error / warning | `ARCHITECTURE.md` 缺失（error）/ 无变更日志标题或时间戳过期（warning） | 规则3 |
| GS007 | warning | `Game.*` 程序集或命名空间落在 `scripts/` 之外 | 规则2 |

> **说明**：`AGENTS.md` §7 与部分检查表写的是「零 error」，而 GS005 是 warning——两者不矛盾，「零 error」= 退出码 0。**GS005 虽为 warning 但仍按红线处理**（新条目 = 打回）。

---

## 4. 目录契约与白名单

### 4.1 一句话

**`.cs` 只在 `scripts/`**（命名空间以 `Game.` 开头，GS007）；**C++ 只在 `native/`**（GS003）；**`.gd` 只在 UI 层与探针白名单**（GS001）；**`data/generated/` 只由 `tools/data_pipeline/` 写**；**任何白名单放行都要三处同时登记**。

### 4.2 目录职责速查（完整表见 `ARCHITECTURE.md` §1.3；「谁可以写 / 禁写」是原文）

| 目录 | 谁能写 | 禁写什么（摘要） | 出处 |
|---|---|---|---|
| `scripts/core/`、`scripts/data/`、`scripts/combat/`、`scripts/actor/`、`scripts/skill/`、`scripts/spawn/` | 对应任务 | core 不重度用 Godot API；data 不写数值字面量；actor 不直读 Excel；skill 不复制战斗逻辑；spawn 不硬编码出怪表 | ARCH §1.3 |
| `scripts/host/` | ECS / 宿主类任务 | **唯一允许 `using Godot` 的 ECS 边界**，别的层不得依赖它 | ARCH §2.3 硬约束 1 |
| `scripts/core/ecs/` | ECS 架构师 | 不得依赖任何业务命名空间、不得含引擎类型 | ARCH §2.3 硬约束 2 |
| `scripts/ui/` | UI 类任务 | 不引用 `Game.Combat/Actor/Skill/Spawn`、不引用 `Native.*`（GS004）；**UI 不持有真值、不做结算** | ARCH §1.3、§8.3 |
| `scenes/` | 任意 | 不挂 `.gd`（规则1） | ARCH §1.3 |
| `data/excel/` | 策划 / 数值任务 | 不用手改生成物替代真源 | 策划模块 |
| `data/generated/` | **只有 `tools/data_pipeline/`** | 人工编辑（会被覆盖） | ARCH §4.5 |
| `shaders/` | 渲染 / 特效任务 | 硬编码 uniform 值（应由 `.tres` 喂） | ARCH §1.3、§7.3 |
| `native/` | native 类任务 | 放 C# / GDScript | ARCH §1.3 |
| `addons/` | 编辑器工具任务 | 业务逻辑常驻；且在守卫 `ignore` 里（**完全不扫描**） | ARCH §1.3、rules/01 §8 C3 |
| `tools/` | 工具链任务 | 被游戏运行时引用；`tools/probes/**` 只读探针、不进游戏包 | ARCH §1.3、rules/01 §5.1 |
| `config/` | 工具链 / Lead | 业务逻辑；`architecture-rules.json` 是**白名单唯一来源** | ARCH §1.3、rules/02 §8.1 |
| `tests/` | 任意 | 依赖具体场景才能跑；**不得**传递引用 GodotSharp | ARCH §1.3、`tools/check_all.ps1` 头 |
| `docs/` | **任意任务（架构章节就地覆盖）** | 另建 `ARCHITECTURE_v2.md` | ARCH §1.3、rules/07 §1.3 |

**目录变动纪律**：目录一旦新增 / 改名，**改动者本人立刻回写 `ARCHITECTURE.md` §1**（§1.5 第 3 条）。

### 4.3 分层：允许 / 禁止（摘要）

- 单向依赖链：`Native → Combat → Actor → Data → Core`（向下单向）；`UI / Skill / Spawn` 只向下（ARCH §2.2）。
- **只有 `Game.Combat` 允许 `using Game.Native`**，其余层经 Combat 转调（向下沉 C++ 时只改一处）。
- 完整 8×8 矩阵与 `Game.Host` / `Game.Core.Ecs` / `Game.Core.Content` 三行新增，见 `ARCHITECTURE.md` §2.3——**改依赖方向前必须读那张表**。

### 4.4 白名单（唯一放行渠道）

- **唯一来源**：`config/architecture-rules.json` 的 `allowGDScript` / `forbidGDScript` / `ignore`（字段语义见 rules/01 §3.1）。
- **三步登记，缺一不可**：① 改 JSON → ② 在 `rules/01` §3.3 登记理由与复审日期 → ③ 在 `AGENTS.md` §4 白名单表加一行（另需在 rules 文档说明）。
- **当前已登记**（明细以 `AGENTS.md` §4 为准）：`scripts/ui/**`、`addons/**`、`tools/probes/**`、`tools/asset_viewer/**`（C++）、`tools/asset_viewer2/**`（C++，见 ARCH §24.1 为什么不能放 `native/`）。
- **`forbidGDScript` 优先级最高**；`ignore` 优先于一切（`addons/**` 实际完全不扫描，是**有意**不审第三方代码）。
- **不接受**的放行方式：PR 描述里说明、代码注释写 `arch-ok`、临时改脚本跳过。

---

## 5. 语言选型判据摘要（C# / C++ / shader / GDScript）

> 完整矩阵在 [rules/02](../../rules/02-tech-selection.md) §1（T1~T10）；**数字阈值只在 `ARCHITECTURE.md` §9.2~§9.4**，`rules/02` 刻意只给判据名 H1~H6。

### 5.1 四问速判（rules/02 §0 + §9 速查表）

| 四问 | 答案 → 结论 |
|---|---|
| Q1 这是**玩法逻辑 / 状态 / 结算 / UI 脚本**吗？ | → **C#**（`scripts/**`，T1/T2）；UI 层也优先 C# |
| Q2 是**逐帧热路径**、**需要 RID 级渲染访问**、或**要改 pass 结构**吗？ | → **C++ GDExtension**（`native/src/`，T4）；但先按 H1~H6 判据**测过再下沉** |
| Q3 只改**外观**（描边、白闪、溶解、色彩分级）吗？ | → **`.gdshader`**（`shaders/`，T6），参数走 `uniform` + `.tres` |
| Q4 是**构建期**的事（Excel→生成物 / 守卫 / 打包）吗？ | → **Python**（`tools/data_pipeline/`，T7）或 **PowerShell 5.1**（`tools/*.ps1`，T8） |

GDScript **不属于**上面的默认答案：只在 rules/01 §2 定义的「UI 层」与 §5.1 探针特批里出现，且 UI 内部**优先 C#**（何时才允许 `.gd`：纯布局 / 纯 `@tool` 编辑器工具 / 当场删除的一次性验证）。

### 5.2 下沉 C++ 的判据与流程（rules/02 §2）

- **判据 H1~H6（满足任意一条才考虑，未达一律先写 C#）**：H1 单帧调用次数超预算 / H2 批量实体数超预算 / H3 需要 `RenderingDevice`·`RenderingServer` 直接操作 / H4 需要逐帧改渲染管线或自定义 pass 内部实现 / H5 C# 逻辑耗时占比超预算且已无分配可省 / H6 稳态托管分配超预算 → **H6 是代码问题，先修分配，不许用下沉掩盖**。
- **下沉流程（必须按序）**：先测 → 先优化 C#（`ARCHITECTURE.md` §9.5 顺序：分配 → 算法 → 批处理/池 → 数据布局 → 语言下沉）→ 仍超标才写下沉申请 → 定义粗粒度边界 → 落地 C++ + `Game.Native` 封装 + 回归基准 → 回写 §9 实测值与变更日志。**跳步 1~3 直接写 C++ = 提前优化，打回**。
- **边界契约**：粗粒度调用、值类型 + 连续内存、零拷贝优先、C++ 不持有 C# 对象引用跨帧、返回码不抛异常、默认主线程、导出类统一 `Native` 前缀。
- **渲染三级顺序（禁止跳级）**：级别 0 引擎现成能力 → 级别 1 扩展点（`CompositorEffect` / RD / 自定义 gdshader）→ 级别 2 fork 引擎（**Lead 仲裁**）。原文在 ARCH §7.2 与 rules/02 §5。

### 5.3 shader 与 GDScript 的两条硬边界

- shader：自研 `.gdshader`、命名 `<feature>_<pass>.gdshader`、**所有可调参数必须是 `uniform`**、d3d12 是唯一目标 RHI（不写 Vulkan 专有特性）、新增 shader 必须登记进 ARCH §7.3 表（rules/02 §6、ARCH §7.3）。
- GDScript：「UI 层」= `res://scripts/ui/**` + `addons/**` 内置 UI 脚本；**相机 / 震屏 / HitStop / 输入映射 / 粒子 / 描边都不算 UI 层**（rules/01 §2）。UI 层能「看得见」的接口都在 `Game.Core` / `Game.Data`，**能改变游戏状态的一律不在 UI 层**（rules/01 §7.1）。

### 5.4 PowerShell 5.1 与 Python（构建脚本的两条铁律）

- **PS 5.1 编码**：本仓库 `.ps1` 必须「**纯 ASCII 源码**（中文用 Unicode 码点表构造）」或「UTF-8 带 BOM」，不允许第三种；写文件用 `[System.IO.File]::WriteAllText` + `UTF8Encoding($false)`；禁用 PS7 专有语法（`??`、`?:`、`-Parallel`、`&&`）；改文档**禁止**用 `[System.Collections.ArrayList]` 包裹再写回（会把文件写成 28 字节垃圾）（rules/02 §3.5、rules/08 §11.3、`tools/env.ps1` 文件头）。
- **Python 只用 DSH 自带**（含 `openpyxl`）：系统 Python **没有** openpyxl，会直接 `ModuleNotFoundError`（ARCH §4.8、`tools/data_pipeline/README.md` §1）。

---

## 6. 新人第一天阅读顺序

> 目标：**约 4 小时**读完「够用的部分」，并亲手跑通一次环境与守卫。**不要第一天通读 `ARCHITECTURE.md` 3,228 行。**

| 时段 | 读什么 | 读到什么程度 | 时长 |
|---|---|---|---|
| ① 09:00 | [AGENTS.md](../../../AGENTS.md) 全文 | 能背出八条铁规 + 15 条红线；知道开收工动作 | 25 min |
| ② 09:25 | `ARCHITECTURE.md` 文件头 + §0 + §11 首屏 5 行 + §12 | 知道当前阶段、最近谁改了什么、排期在哪 | 20 min |
| ③ 09:45 | `ARCHITECTURE.md` §1（含 §1.3）+ §2.3 | 目录契约与依赖矩阵——**写代码前必须能背** | 30 min |
| ④ 10:15 | **动手**：`.\tools\env.ps1 -Verify` → 记录 FAIL/PEND；`.\tools\fix_godot_scan_dirs.ps1 -Check` | 自己机器上 env 全绿；知道 FIX 怎么补 | 15 min |
| ⑤ 10:30 | [rules/02](../../rules/02-tech-selection.md) §0 §1 §2 §9 §11 + `ARCHITECTURE.md` §9 | 给定一个任务能 1 分钟判出语言 | 40 min |
| ⑥ 11:10 | [rules/01](../../rules/01-gdscript-ban.md) §2 §3 §4 + `ARCHITECTURE.md` §8 | 知道 `.gd` 只能写在哪、为什么相机会话不算 UI | 20 min |
| ⑦ 11:30 | [rules/07](../../rules/07-definition-of-done.md) §2 §5 §7 §8.1 §8.2 | 会照抄检查表、会写变更日志一行 | 30 min |
| ⑧ 12:00 | [rules/08](../../rules/08-parallel-sessions.md) §0 §2 §3 §8 §10 | 会开 / 提交 / 合并 / 清理会话 | 20 min |
| ⑨ 12:20 | 本文 §7 坑清单（只读「一句话症状」列也行） | 以后遇到「没反应 / 卡死 / 画面不变」先回来对号 | 20 min |
| ⑩ 12:40 | **动手**：`.\tools\check_all.ps1 -SkipTests`（想快就 `-SkipTests -SkipDungeon`） | 明白一键四步长什么样，exit 0 长什么样 | 20 min |

**第二天再读（按你接的第一个任务挑）**：`ECS.md` §1 + §4.2（只要写玩法代码就必须读）；`rules/03` §3.1~§3.4（写新概念 / 新数据列时）；`rules/06` §2（任何加功能前先看扩展预算数字）；`ARCHITECTURE.md` 里与你任务相关的专章（§4 数据流 / §7 渲染 / §14 地牢 / §17 内容契约……）。

**分工提醒**：本文只覆盖「怎么干活」。**玩法形状**看 ECS 模块（`ECS.md` 全文、ARCH §13/§17），**数值与表**看策划，**房间**看地编，**角色**看角色模块。

---

## 7. 坑清单（文档里记录过的静默失效与硬伤，逐条给出处）

> **读法**：「静默失效」= **不报错、不崩、日志干净，功能就是不对**——它们只能靠「设值 → 读回 → 比对」的探针或跨层断言抓住，肉眼跑一遍抓不到。
> 每条只写「症状 + 一句话根因 + 去哪读」，**修法与细节一律回原文档**。

### 7.1 静默失效类（最贵的一类）

| # | 一句话症状 | 一句话根因 | 出处 |
|---|---|---|---|
| 1 | **拖大窗口，3D 画面一点不变** | `stretch/mode="canvas_items"` 把内容钉死在引擎默认 1152×648；必须 `disabled` + 显式基准分辨率 | ARCH §7.5.1（探针 `tools/probes/viewport_resize_probe.gd`：旧 4/4 失败、新 4/4 通过） |
| 2 | **视口跟着变了，但 2D 自绘图不变** | `Node2D` **没有布局尺寸**，`ScrollContainer` 只能拿 `GetRect()` 当内容大小 → 「图永远刚好装下」的死结；正解是用**父容器 `Size`** 算格子并 clamp | ARCH §7.5.2（探针 `tools/probes/dungeon_map_autofit_probe.gd`） |
| 3 | **鼠标在 3D 视口里完全失灵** | 用 `!io.WantCaptureMouse` 当「鼠标在视口里」——它回答的是「鼠标归 ImGui 还是归应用」，视口本身是 ImGui 窗口时恒为 true；应判 `IsWindowHovered` | ARCH §24.7.1 |
| 4 | **来回点两个模型就卡死** | 描述符集**只分配不归还**，池 `maxSets` 耗尽后绑定 `VK_NULL_HANDLE`；修法关键前提是池必须带 `FREE_DESCRIPTOR_SET_BIT`（漏了会直接 `0xC0000005`） | ARCH §24.7.3 |
| 5 | **改了 shader，画面不变** | `shaders/` 源目录里躺着旧 `.spv`，POST_BUILD 的 `copy_directory` 把它盖回新编译结果；改由 CMake 调 `glslc` 生成 | ARCH §24.4 第 2 条 |
| 6 | **几何体飞出屏幕 / 什么都没画出来** | 矩阵约定：CPU 行主序 + 行向量，`ToColumnMajor()` 后 GLSL **必须** `row_vector * mat4`，写反不报错 | ARCH §24.4 第 1 条、§16.5（`MakeLookAt` z 轴取反） |
| 7 | **改了 `set(... CACHE ...)` 却不生效，一堆 `C2039`** | 漏 `FORCE`，`CMakeCache.txt` 一直指着别处的 ImGui | ARCH §24.4 第 3 条 |
| 8 | **数据填满、校验通过、运行期一个都不读** | `combat.xlsx` 26 行全填 `VisualPack`，`scripts/` 里运行期读取点 = 0；编译器 / 守卫 / 356 个测试 / 校验器 / 评审**全部拦不住** | ARCH §17.11.1~§17.11.2 |
| 9 | **玩家实体永远不出生 / 按左键永不开枪** | 四条真实接线坑：discovery 死循环、技能包槽位号错位、`DataSkill` 占位不生成子弹、意图覆盖方向被静默抹掉 | ARCH §18.4 |
| 10 | **射击武器运行时静默打不出子弹**（及同批 5 条） | `ContentRef.DefId` 是**有符号** FNV-1a32，用 `< 0` 判空会误判近一半；行主键与引用列 id 口径不一致 | ARCH §17.8（1~6 条） |
| 11 | **打开 Godot 刷 5 类 ERROR** | ① `.gdextension` 声明了 Debug/Release 两条路径但只编了 Release（**报错显示的是 `.gdextension` 路径，有误导性**）② Godot 不读 `.gitignore`，把 `.cpp.obj` 当 OBJ 模型导入 ③ 早期 stash 冲突留下的脏索引 | ARCH §11 变更日志 2026-11-09「打开 Godot 报错清理」条 |
| 12 | **反序列化 / 回放不稳定的随机** | 全工程随机只经 `IRandomProvider`，**禁用** `GD.Randf()` / `System.Random` 直接调用（R9） | ARCH §10 R9 |
| 13 | **ECS「看起来能跑、结果是错的」四条** | 原型签名碰撞、命令值错位一位、预留实体误判「已生成」、`.csproj` glob 吞 MSBuild 产物 | ARCH §13.5 |
| 14 | **改了 shader/脚本不生效、参数「存了但没接」** | 相机 `FollowLerp` 存的是逐帧 lerp 系数却被当每秒速率用 → 相机几乎不动 | ARCH §11 变更日志 v17 条 |
| 15 | **数据列没人读 / 枚举没实现却伪装成「不该发生」** | 死数据列、静默 `default: return false;` | `rules/03` §3.4（红线 12~14 见 AGENTS §3） |
| 16 | **「假守护」用例永远不可能失败** | 声称「与 xlsx 逐行对应」的用例从不打开 xlsx，比的是 C# 常量与 C# 常量——**任何声称守护跨层一致性的用例必须真的触碰另一层** | ARCH §4.1.1 |
| 17 | **打开引擎看到「文件不存在」但游戏照跑** | 素材库 `素材/` 是原料区不是运行时资源区，指过去的路径失效后**静默回退白模** | ARCH §17.10.1 |

### 7.2 构建 / 工具链 / 流程硬伤

| # | 症状 | 根因 | 出处 |
|---|---|---|---|
| 18 | `.ps1` 报「Unexpected token '}'」等怪错，且错误位置与真问题无关 | PS 5.1 按 ANSI（GBK）解析**无 BOM** 的 UTF-8 中文脚本，字节对吞掉换行/引号——**必须**纯 ASCII 或带 BOM | `rules/02` §3.5、`rules/08` §11.3、`tools/env.ps1` 文件头 |
| 19 | `dotnet build` 报 8 条 CS0579「特性重复」 | Godot SDK 默认 `**/*.cs` 收进 `.godot/mono/temp/obj/**/*.AssemblyInfo.cs`；`<Compile Remove>` 放 Project 级 ItemGroup **不生效**，必须用 `DefaultItemExcludes` | ARCH §13.5 第 4 条、§10 R14 |
| 20 | Godot 启动刷「Couldn't read OBJ file … .cpp.obj」 | Godot 不读 `.gitignore`；`.gdignore` 因目录被排除而**进不了库**（git 无法用 `!` 重新包含被排除目录内的文件）→ 每个新克隆都要跑一次 `fix_godot_scan_dirs.ps1` | `tools/fix_godot_scan_dirs.ps1` 文件头、ARCH §11 变更日志 2026-11-09 |
| 21 | 「我明明并行了」——结果 `main` 被污染、别人 rebase 全乱 | 在 `main` 上直接开发 / 两个会话共用一个目录 / worktree 建在项目根内部（两份 `.godot/` 打架） | `rules/08` §5、§8 反例 1~4 |
| 22 | 合并后 `main` 编译挂了 | 两边各自绿 ≠ 合起来绿；`finish` 不跑守卫 | `rules/08` §3 纪律 4、§11.2 |
| 23 | 数直接改 `combat.xlsx` 却「改错了」 | 重跑 `gen_combat_xlsx.py` 会**整体覆盖**手改真源（历史事故：BitMask 位被清零） | ARCH §20.8（数据侧细节归策划模块） |

> 与其它模块的分界：§7 只收「**工程怎么坏**」。数据表本身的填法坑（空≠0、PackId 跨层、表头横躺……）归**策划**；ECS 内核形状归 **ECS 架构师**；探针与相机手感归**地编 / 角色**。上表为让开发能对号，给出了跨模块坑的出处链接，**口径仍以原文档为准**。

---

## 8. 我今天不管什么（Out + 交界处）

| 我不管 | 归谁 | 交界处怎么处理 |
|---|---|---|
| 玩法契约：组件/系统/查询/帧顺序/引擎边界 | ECS 架构师（`ECS.md` 全文） | 分层矩阵 §2.3 的**判据**由 ECS 解释，**守卫怎么查**由开发负责 |
| 数值与表结构（Excel 每列含义、五层数值包、Tags、l10n） | 策划（`rules/04`/`05`） | 数值零硬编码的**执行**（GS005）归开发，**数据怎么写**归策划 |
| 房间 / 地牢 L1~L4、房型、装饰目录 | 地编（ARCH §14、`decor_catalog.md`） | 宿主场景接线（`Dungeon*`）归开发；「应该长什么样」归地编 |
| 角色包组合、五槽技能、HUD 显示什么 | 角色（ARCH §15/§18/§19、`character-hud-contract.md`） | 「能不能用 `.gd`」归开发，「HUD 显示什么」归角色 |
| 架构大纲正文（`ARCHITECTURE.md` 各章） | 托管于 ECS 架构师（托管 ≠ 独占） | 每个任务按 `rules/07` 就地覆盖**自己那一节** + 留日志行；本文不改它 |

**越界红线**：不要在 `docs/modules/开发/**` 之外写文件；不要改 `docs/` 下任何原有文档来「顺手修个错别字」——写进下面的待核对。

---

## 9. 待核对 / 我没把握的点（交整合阶段裁决）

| # | 问题 | 我的处理 | 建议 |
|---|---|---|---|
| D1 | `rules/07` 标题写「6 步检查表」，实际列出 **①~⑦**（⑦ 是「会话外壳」） | 本文 §3.2 照实列 7 行，并说明 ⑦ 的性质 | 保持；若整合要统一口径，建议把「六步」解释为 ①~⑥ |
| D2 | GS005 级别：`tools/check_architecture.ps1` 头写 **warning**，`rules/04` §10 写 **Error（阻断 CI）** | 本文按脚本实现（warning）写，但强调「新条目 = 打回」 | 以脚本为准；`rules/04` §10 的严重度描述待仲裁 |
| D3 | `tools/check_architecture.ps1` 头说「不是 git 仓库时正常、不抛异常」，`rules/08` §10 说「打印请先 git init 并以退出码 2 正常退出」 | 本文只写「按脚本行为」，不写死 | 两处措辞对齐一次 |
| D4 | `ARCHITECTURE.md` §7.1「现状」仍写 `stretch = canvas_items`，而 §7.5.1 与变更日志已改为 `disabled` | **未改原文**（只增不改） | 建议维护者在下次就地覆盖时同步 §7.1 表 |
| D5 | `session.ps1 start` 是否写分支 `session/<名>`：`rules/08` §2.1 写「等价 `git worktree add … -b session/<名> main`」 | 照抄 §2.1 的命令形态 | 以脚本 `-Help` 为准 |
| D6 | 本 README 只写章节号与「快照行号」，行号会随文档就地更新漂移 | 遵循 BOUNDARY Q7 建议：**以章节号为准**，行号仅作定位提示 | 整合时若要求去掉行号，删 §2.2 起始行列即可 |
| D7 | 模块目录用中文名 `docs/modules/开发/`，与 BOUNDARY §2.1 的 `docs/modules/dev/` 不一致 | 按本次任务指派的路径 `docs/modules/开发/**` 落盘 | 需要整合阶段统一（并入 `MODULE_MAP.md` 时注明路径） |
| D8 | ARCH §16（素材快查看器 v1）与 §24（3D 查看器 v2）并存，§16 是否仍维护未说明 | 两节都列进次归属，§16 标注「旧版工具」 | 建议标明 v1 是否废弃 |

---

## 10. 常见任务 → 该读哪一节（路由表）

> 这是 `rules/07` §8.2 的「开发者视角」镜像：只列本模块负责的部分，完整表见原文。

| 我要做的事 | 先读 | 收工额外注意 |
|---|---|---|
| 新建一个 `.gd` / 往 `.tscn` 挂脚本 | rules/01 §2 §3 §4 | 白名单三步登记；UI 子目录统一一种语言 |
| 判断「这段逻辑该用 C# 还是 C++」 | rules/02 §1 §2 §9；阈值去 ARCH §9.4 | 未达判据先写 C#；下沉要写申请 |
| 写 shader / 调外观 | rules/02 §5 §6；ARCH §7.2 §7.3 | 新增 shader 登记 §7.3；参数走 `uniform` |
| 写 / 改 PowerShell 脚本 | rules/02 §3.5；`tools/env.ps1` 文件头 | 纯 ASCII 或带 BOM；PS 5.1 实测 |
| 改数据管线 / 加表 | `tools/data_pipeline/README.md`；ARCH §4.4 §4.5 §4.8；rules/04 §9 | 用 DSH Python 跑校验；生成物禁止手改 |
| 构建挂了 / 找不到 Debug dll / 编辑器刷 ERROR | §7.2 #18~#20 | `fix_godot_scan_dirs.ps1`、`.gdextension` 两条路径都要有产物 |
| 并行开会话 / 合并冲突 | rules/08 §2 §4 §7 §10 | `finish` 后复跑守卫；`ARCHITECTURE.md` 并集合并 |
| 收工不知道漏了什么 | rules/07 §2 §9；本文 §3.2 | `check_all.ps1` 跑一遍 |
| 新增目录 / 目录改名 | ARCH §1.3 §1.5 | 改动者本人回写 §1 |
| 新增一条规则 / 一条白名单 | rules/01 §3.2；AGENTS §4 | 三处同步 |

---

## 11. 变更日志（本文自身）

| 日期 | 版本 | 任务 | 变更摘要 |
|---|---|---|---|
| 2026-11-11 | v1 | `t-dev` 开发模块索引 | 建立本模块 README：六段式结构（职责 / 必读 + 次归属 / 开收工命令级清单 / 目录契约与白名单 / 语言选型摘要 / 新人第一天顺序 / 坑清单 23 条逐条给出处 / 边界 / 待核对 D1~D8 / 任务路由表）。只新增 `docs/modules/开发/**`，未改动任何现有文档。 |
