# AGENTS.md — BiuBiuOrigin 开发契约

> 本文件是**项目唯一的启动入口**。任何人（包括 AI agent）在动手写代码前必须先读完本文件。
> 版本: v3 ｜ 建立日期: 2026-11-03 ｜ 最后更新: 2026-11-04 ｜ 状态: ECS 骨架已落地（内核 + 宿主 + 首批系统 + 63 测试全绿）；仓库已入 git 并推送 GitHub；多会话并行工作流（规则8）已建立

---

## 0. 三十秒速读

| 你会被问 | 答案 |
|---|---|
| 这是什么项目 | 俯视角射击肉鸽，Godot 4.7.2 (.NET 10)，风格 Stylized Low-Poly Toybox，手感优先 |
| 运行时架构 | **数据导向 ECS**（自研 `Game.Core.Ecs`：archetype 列式 + unmanaged 组件 + SystemBase 系统 + 帧末命令提交），详见 `docs/architecture/ECS.md` |
| 写代码用什么 | **C# 为主**；渲染与热点路径用 **C++ GDExtension**；特效用**自研 shader**；**UI 层之外禁止 GDScript** |
| 数值放哪 | **Excel 是唯一真源**，构建期生成 `data/generated/`；代码里出现游戏数值 = 违规 |
| 开工流程 | 先想抽象 → 再想扩展点 → 才写实现（`docs/rules/03`、`06`） |
| 收工流程 | 编译通过 → 架构守卫无 error → **就地覆盖更新** `docs/architecture/ARCHITECTURE.md`（`docs/rules/07`） |
| 多会话并行 | **每个会话开自己的临时工作空间（git worktree），干完合并回 main**，绝不在 main 上直接开发（`docs/rules/08`） |

---

## 1. 开工前的固定动作（强制顺序，不得跳步）

```
① 读本文件 AGENTS.md
② 读 docs/architecture/ARCHITECTURE.md 的相关章节（先看「0. 一页速览」和「变更日志」）
③ 读本次任务对应的 docs/rules/NN-*.md 全文
④ 跑 .\tools\env.ps1 -Verify 确认环境（Godot / dotnet / 离线 NuGet / cmake / python）
   注意：输出里的 PEND（csproj/native/data 目录未创建）属正常「待落地」，只有 FAIL 才需要处理
⑤ 在任务描述中写下三件套：边界 · 抽象 · 扩展点（规则5）
⑥ 才开始写代码
```

> **第 ⓿ 步（只要有第二个会话就可能并行，就先做）：** 开一个会话工作空间，别在项目根直接开工。
> ```powershell
> .\tools\session.ps1 start -Name <会话名>     # 例：skill-takeover
> cd ..\.worktrees\<会话名>                     # ★ 之后所有操作都在这个目录里
> ```
> 详见 [docs/rules/08-parallel-sessions.md](docs/rules/08-parallel-sessions.md)。单人单会话时可跳过。

> 停在第 ② 步就卡住，说明 ARCHITECTURE.md 没维护好 —— 先把它补上，再开工。

> **写玩法代码前的额外一步（2026-11-03 起强制）：** 本工程的运行时玩法**跑在数据导向 ECS 上**（`Game.Core.Ecs`，见 [docs/architecture/ECS.md](docs/architecture/ECS.md)）。
> 动手前必须读完 `ECS.md` 的 §1 七条判定（J1~J7）与 §4.2 组件类别表；**任何新增玩法逻辑 = 新增一个 `SystemBase` 子类，而不是往组件里加方法、也不是新写一个 `class Actor`**。

---

## 2. 八条铁规（摘要，全文见 `docs/rules/`）

| # | 规矩 | 一句话判据 | 全文 | 自动检查 |
|---|---|---|---|---|
| 1 | 除 UI 层外禁止 GDScript | `*.gd` 只允许出现在 `scripts/ui/**` 等白名单 | [01-gdscript-ban.md](docs/rules/01-gdscript-ban.md) | GS001 GS002 |
| 2 | 逻辑用 C#，渲染/性能用 C++ GDExtension，特效自研 shader | 先问「这是不是每帧热路径」，是才考虑下沉 C++ | [02-tech-selection.md](docs/rules/02-tech-selection.md) | GS003 GS004 |
| 3 | 任务完成必须**覆盖更新**架构大纲 | 改的是 `ARCHITECTURE.md` 本身 + 追加一行变更日志 | [07-definition-of-done.md](docs/rules/07-definition-of-done.md) | GS006 |
| 4 | 所有数值走文件 / Excel / 插件可视化修改 | 代码里出现 `1.5f` 这种玩法数值 = 违规 | [04-data-driven.md](docs/rules/04-data-driven.md) | GS005 |
| 5 | 先抽象，不许临时搭建 | 开工第一件事是写接口，不是写实现 | [03-abstract-first.md](docs/rules/03-abstract-first.md) | 人工审查清单 |
| 6 | 人物/怪物数值封装成包，可互相赋值 | 一切都是 `StatPackage` 的不同组合 | [05-numeric-package.md](docs/rules/05-numeric-package.md) | 人工审查 |
| 7 | 必须可扩展，新增功能不大改 | 「新增一种 X 要改几个文件？」答案 ≤ 2 | [06-extensibility.md](docs/rules/06-extensibility.md) | 人工审查 |
| 8 | **多会话并行，每会话开临时工作空间，完成后合并主线** | 改动是不是发生在 `session/<会话名>` 分支的独立 worktree 里？在 `main` 上直接写 = 违规 | [08-parallel-sessions.md](docs/rules/08-parallel-sessions.md) | `tools/session.ps1` + 人工审查 |

---

## 3. 红线清单（违反即打回，不讨论）

1. ❌ 在 UI 层以外新建 `*.gd`、在 `.tscn` 里挂 GDScript。
2. ❌ 在 C# 逻辑代码里写死游戏数值（伤害、血量、速度、射速、冷却、颜色、尺寸、持续时间、特效参数……）。
3. ❌ 复制粘贴另一处逻辑来「快速实现」，而不是先找/抽公共抽象。
4. ❌ 另建 `ARCHITECTURE_v2.md` 之类的平行文档代替就地更新。
5. ❌ 在 `scripts/ui/` 里调用 `Game.Combat` / `Game.Skill` 的结算逻辑（UI 只能读数据和发事件）。
6. ❌ 直接手改 `data/generated/` 下的文件（改 Excel 后重新生成）。
7. ❌ 修改引擎源码（默认不允许；确需时走 [02-tech-selection.md](docs/rules/02-tech-selection.md) 的 fork 流程并记录引擎版本）。
8. ❌ **在 `main` 分支上直接开发**（多个会话并行时）；或在两个会话里同时改同一批文件却不各自开 worktree。
9. ❌ 把会话 worktree 建在项目根内部（会让 Godot 把两份工作树都当项目导入，`.godot/` 互相打架）。
10. ❌ `git push --force` 到 `main`，或删除尚未合并的会话分支。

---

## 4. 白名单登记表（唯一放行渠道）

放行规则由 `config/architecture-rules.json` 驱动，**每一行放行都必须在这里登记理由**，否则视为违规。

| 放行对象 | 允许语言 | 理由 | 登记人 | 日期 |
|---|---|---|---|---|
| `scripts/ui/**` | GDScript / C# | 规则1 原文允许「UI 层」——HUD、菜单、弹窗、飘字 | Lead | 2026-11-03 |
| `addons/**` | 任意 | 第三方插件与自研编辑器工具，不参与运行时玩法 | Lead | 2026-11-03 |
| `tools/probes/**` | GDScript | 只读探针（截帧、相机、色板验证），不进游戏 | Lead | 2026-11-03 |

> 新增放行：改 JSON + 在本表加一行 + 在对应 rules 文档说明，三处缺一不可。

---

## 5. 目录契约（谁写谁读）

```text
biu-biu-origin/
├─ AGENTS.md                  ← 你正在读的入口契约
├─ project.godot              ← 由 Godot 编辑器维护，AI 不手改（除明确要求）
├─ BiuBiuOrigin.csproj        ← 由 Godot 生成（Sdk=Godot.NET.Sdk/4.7.2），不要手删
├─ nuget.config               ← ★ 离线源（只指向 E:\Godot\GodotSharp\Tools\nupkgs），禁止联网拉包
├─ config/
│   └─ architecture-rules.json ← 架构守卫配置（放行白名单唯一来源）
├─ scripts/                   ← C# 游戏逻辑（Godot 场景脚本也放这里）
│   ├─ core/        界面无关的基础设施：接口、事件、数学、随机、池
│   │   └─ ecs/     ★ ECS 内核（Game.Core.Ecs）：World/Archetype/Query/Pipeline/CommandBuffer + 官方组件
│   ├─ host/        ★ ECS 宿主（Game.Host）：唯一允许 using Godot 的 ECS 边界（WorldHost/帧管线/桥接/视图池）
│   ├─ data/        数据层：Resource 定义、Excel 生成物的加载与校验
│   ├─ actor/       系统族：移动/AI/生命周期（ECS）
│   ├─ combat/      系统族：命中检测、伤害结算、反馈（HitStop/白闪/击退）（ECS）
│   ├─ skill/       系统族：技能装配、冷却门控、夺舍（ECS）
│   ├─ spawn/       系统族：刷怪、波次、房间编排（ECS）
│   └─ ui/          ★唯一允许 GDScript 的目录，只做展示，不做结算
├─ 素材/                      ← 美术素材库（.glb 模型/骨骼动画/Synty 风格资源），**只读**，不手改
├─ scenes/                    ← .tscn（场景里挂的脚本路径指向 scripts/ 下的 C#）
├─ data/
│   ├─ excel/        ★策划编辑区（唯一真源）
│   └─ generated/    由 tools/data_pipeline 生成，禁止手改、可随时重建
├─ shaders/                   ← 自研 .gdshader（uniform 必须可被外部 .tres 配置）
├─ native/                    ← C++ GDExtension
│   ├─ gdextension/  libgodot-cpp（godot-cpp master + 锁定 hash）+ gdextension_interface.h
│   ├─ include/      对 C# 暴露的 C 布局头（EcsBatchLayout 等）
│   └─ src/          C++ 实现
├─ addons/                    ← 编辑器插件（可视化调参工具、导入插件）
├─ tools/
│   ├─ data_pipeline/        Excel → tres/json 生成器（Python）
│   ├─ check_architecture.ps1 架构守卫（收工前必跑）
│   ├─ check_all.ps1         一键校验（build + 守卫 + 测试）
│   ├─ env.ps1               环境探测
│   ├─ session.ps1           ★多会话工作空间管理（规则8：start/commit/finish/stop）
│   └─ probes/               只读探针
├─ tests/                     C# 测试工程（引用主工程，dotnet test / dotnet run 一键跑）
└─ docs/
    ├─ rules/                01~08 规矩全文
    ├─ architecture/
    │   ├─ ARCHITECTURE.md   ★架构大纲（收工必须更新，唯一入口）
    │   └─ ECS.md            ★ECS 数据导向实现的全部形状（ARCHITECTURE 的下游章节）
    └─ prompts/              任务提示词模板（复制给 AI 用）

../.worktrees/<会话名>/        ← ★会话工作空间（git worktree），在项目根【之外】
                                  各会话独立分支 session/<会话名>，共享同一个 .git 对象库
                                  规则8 强制要求；不随主仓提交，各 worktree 自己 build
```

---

## 6. 技术栈事实（本机实测，勿凭记忆猜）

| 项 | 值 |
|---|---|
| 引擎 | `E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe`（4.7.2.stable.mono） |
| 渲染 | Godot 4.7 Forward+，Windows 走 **d3d12**；物理为 **Jolt** |
| .NET | SDK **10.0.401**，目标框架 `net10.0` |
| NuGet | **离线源** `E:\Godot\GodotSharp\Tools\nupkgs`（含 Godot.NET.Sdk 4.7.2 / GodotSharp 4.7.2 / Godot.SourceGenerators 4.7.2） |
| 构建工具 | CMake `D:\env\cmake\cmake-3.31.12-windows-x86_64\bin\cmake.exe`，Ninja `D:\env\ninja\ninja.exe` |
| vcpkg | `D:\vcpkg`（**注意**：其 `godot-cpp` port 只有 4.4，与引擎 4.7.2 不匹配，**不要用**） |
| godot-cpp | **没有 `4.7` 分支**（上游 4.x 分支最多到 4.5，2026-11-03 实测）。正确做法：`master` 分支 + **锁定 commit hash** + `-DGODOTCPP_API_VERSION=4.7`（master 内置 `extension_api-4-7.json`）。详见 `native/README.md` |
| MSVC | Visual Studio 18 Community；`cl.exe` **不在 PATH**，构建前必须先调 `vcvars64.bat` |
| Python | 系统版**无** openpyxl；请使用 DSH 自带 `python.exe`（自带 openpyxl 3.1.5 / XlsxWriter 3.2.9） |
| Shell | PowerShell **5.1**（脚本禁用 PS7 专有语法：`??`、三元 `?:`、`-Parallel`） |

> 引擎升级 = 改本表 + 改 `ARCHITECTURE.md` §9 + 变更日志一行。

---

## 7. 收工前的固定动作（Definition of Done，详见规则7）

```powershell
dotnet build                          # 1. C# 编译零错误
.\tools\check_architecture.ps1        # 2. 架构守卫零 error（GS001~GS007）
# 3. 逐项勾选 docs/rules/07-definition-of-done.md 的检查表
# 4. 就地覆盖更新 docs/architecture/ARCHITECTURE.md + 追加一行变更日志 + 刷新文件头时间戳
```

只跑 1、2 不更新 3、4 的任务，视为**未完成**。

**多会话并行时，收工顺序变成 7 步（规则8）：**

```powershell
# 在这个会话的 worktree 目录里做 1~4（不是项目根！）
.\tools\session.ps1 commit -Name <会话名> -Message "<一句话说清改了什么>"
.\tools\session.ps1 finish -Name <会话名>   # rebase 到最新 main → merge --no-ff 回 main
# 合并完成后再回项目根复跑一次构建与守卫，确认 merge 没引入新问题
.\tools\session.ps1 stop   -Name <会话名>   # 清理 worktree 与分支
```

> `finish` **不会**替你跑架构守卫（避免工具替你决定要不要 `-UpdateBaseline`），合并后的复跑必须人来做。

---

## 8. 想让 AI 干活时怎么用

1. 复制 [docs/prompts/task-prompt-template.md](docs/prompts/task-prompt-template.md) 的模板，填空后丢给 agent。
2. agent 会自动按 §1 的开工顺序读本文件 → ARCHITECTURE.md → 对应 rules。
3. 交付时检查它是否真的走了 §7 收工动作（尤其 `check_architecture.ps1` 与 ARCHITECTURE.md 的变更日志）。

推荐一轮任务的生命周期：

```text
模板提示词 → 开工五问 → 抽象先行 → 数据表先行 → 实现 → 自测 → 架构守卫 → 更新架构大纲 → 汇报
```

---

## 9. 文档索引

| 文档 | 用途 |
|---|---|
| [docs/rules/01-gdscript-ban.md](docs/rules/01-gdscript-ban.md) | GDScript 禁令与 UI 层边界 |
| [docs/rules/02-tech-selection.md](docs/rules/02-tech-selection.md) | C# / C++ / shader / 渲染管线选型矩阵 |
| [docs/rules/03-abstract-first.md](docs/rules/03-abstract-first.md) | 抽象优先流程与通用接口清单 |
| [docs/rules/04-data-driven.md](docs/rules/04-data-driven.md) | Excel 数据驱动与字段规约 |
| [docs/rules/05-numeric-package.md](docs/rules/05-numeric-package.md) | StatPackage 数值包与互相赋值 |
| [docs/rules/06-extensibility.md](docs/rules/06-extensibility.md) | 可扩展性判据与「夺舍」走查 |
| [docs/rules/07-definition-of-done.md](docs/rules/07-definition-of-done.md) | 任务完成定义与架构大纲更新 SOP |
| [docs/rules/08-parallel-sessions.md](docs/rules/08-parallel-sessions.md) | ★多会话并行工作流（worktree 临时工作空间、合并主线、冲突处理） |
| [docs/architecture/ARCHITECTURE.md](docs/architecture/ARCHITECTURE.md) | ★架构大纲（每任务必更，唯一入口） |
| [docs/architecture/ECS.md](docs/architecture/ECS.md) | ★ECS 数据导向实现的全部形状（七条判定 / 组件规范 / 内核 API / 一帧顺序 / 引擎边界 / 性能路线） |
| [docs/prompts/task-prompt-template.md](docs/prompts/task-prompt-template.md) | 任务提示词模板 |
| [config/architecture-rules.json](config/architecture-rules.json) | 架构守卫配置（放行白名单） |
| [native/README.md](native/README.md) | GDExtension 构建复现（godot-cpp hash、vcvars64、接口头生成） |

---

## 10. 变更日志

| 日期 | 版本 | 任务 | 变更摘要 |
|---|---|---|---|
| 2026-11-03 | v1 | 立规矩 | 建立 7 条开发规约、架构守卫、环境探测、架构大纲、提示词模板；工程为 Godot 4.7.2-mono 空壳 |
| 2026-11-03 | v1 | ECS 架构立骨架（Lead 定稿） | 运行时架构定为**数据导向 ECS**：新增 `docs/architecture/ECS.md` 为唯一 ECS 形状真源（七条判定 J1~J7、archetype 列式 + unmanaged 组件、SystemBase 系统族、帧末命令提交、`Game.Host` 作为唯一引擎边界）；目录契约新增 `scripts/core/ecs/`、`scripts/host/`、`tests/`、`nuget.config`；§6 技术栈**事实订正**：godot-cpp 无 4.7 分支 → master + 锁 hash + `-DGODOTCPP_API_VERSION=4.7`，cl.exe 需 vcvars64；新增 ECS 开工前置（§1） | `AGENTS.md`、`docs/architecture/ECS.md`、`docs/architecture/ARCHITECTURE.md`、`BiuBiuOrigin.csproj`、`nuget.config` |
| 2026-11-03 | v2 | ECS 骨架落地验收 | **ECS 内核 + 宿主 + 首批系统全部落地并通过验收**：`scripts/core/ecs/`（14 文件内核）、`scripts/host/`（七阶段管线/视图池/事件队列/桥接）、`scripts/actor`+`scripts/spawn`（3 系统）、`tests/`（63 用例全绿）、`tools/check_all.ps1` 一键校验；修复 4 个真实内核缺陷（原型签名碰撞、命令值错位、预留实体误判、csproj glob 吞产物）。`ARCHITECTURE.md` §0/§1.1/§2.3/§3.2.0/§6.1.1/§10/§13 就地更新 | `AGENTS.md`、`docs/architecture/ARCHITECTURE.md`、`scripts/**`、`tests/**`、`tools/check_all.ps1`、`BiuBiuOrigin.csproj` |
| 2026-11-04 | v3 | 规则8 多会话并行 + 仓库入库 | **新增第 8 条铁规「多会话并行，每会话开临时工作空间，完成后合并主线」**：新增 `docs/rules/08-parallel-sessions.md`（6 步标准流程 / 主线纪律 / 冲突处理 / `ARCHITECTURE.md` 合并特例 / 8 条反例 / 会话命名规范）；新增 `tools/session.ps1`（start/list/status/commit/finish/stop，34 条断言自测全绿）；`07` 检查表追加 ⑦ 合并到主线；本文件 §0/§1/§2/§3/§5/§7/§9 同步；**git 仓库初始化并推送 `https://github.com/KryieNaruto/biu-biu-origin`**（首个提交 276 文件 / 2.75 MB），`.gitignore` 修正三处会污染仓库的规则（`libgodot-cpp` 嵌套仓库、`tools/*/bin\|obj` 缓存、`!tools/**` 过度放行） | `AGENTS.md`、`docs/rules/08-parallel-sessions.md`、`docs/rules/07-definition-of-done.md`、`tools/session.ps1`、`config/architecture-rules.json`、`.gitignore` |
