# AGENTS.md — BiuBiuOrigin 开发契约

> 本文件是**项目唯一的启动入口**。任何人（包括 AI agent）在动手写代码前必须先读完本文件。
> 版本: v7 ｜ 建立日期: 2026-11-03 ｜ 最后更新: 2026-11-09（GDExtension Debug 产物 + Godot 扫描目录豁免） ｜ 状态: ECS 骨架已落地（内核 + 宿主 + 首批系统）；**角色抽象已落地**（数值包组合 + 五槽技能包 + 玩家怪物同源）；**基础物件抽象已落地**（武器/子弹/怪物同源 `ObjectArchetype` + 熔炉组合 + `combat.xlsx` + 零分配铸造工厂）；**第一个可玩人物已落地**（`test_hero` + 三个纯数据变体：WASD 移动 / 左键瞄准射击 / 真子弹 / HUD 全量数值）；仓库已入 git 并推送 GitHub；多会话并行工作流（规则8）已建立
> 版本: v7 ｜ 建立日期: 2026-11-03 ｜ 最后更新: 2026-11-09（2D 自绘图随窗口缩放 + 窗口视口修复 + 数值包跨层校验） ｜ 状态: ECS 骨架已落地（内核 + 宿主 + 首批系统）；**角色抽象已落地**（数值包组合 + 五槽技能包 + 玩家怪物同源）；**基础物件抽象已落地**（武器/子弹/怪物同源 `ObjectArchetype` + 熔炉组合 + `combat.xlsx` + 零分配铸造工厂）；**第一个可玩人物已落地**（`test_hero` + 三个纯数据变体：WASD 移动 / 左键瞄准射击 / 真子弹 / HUD 全量数值）；仓库已入 git 并推送 GitHub；多会话并行工作流（规则8）已建立

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
| `tools/asset_viewer/**` | C++ | 旧版素材查看器，独立桌面程序，不链接 Godot | Lead | 2026-11-06 |
| `tools/asset_viewer2/**` | C++ | **3D 素材查看器**（Vulkan + ImGui），独立桌面程序，有自己的 `main()` 与 Vulkan 设备，与引擎零交互；放进 `native/` 会污染 GDExtension 构建（见 ARCHITECTURE §24.1） | Lead | 2026-11-06 |

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
│   ├─ fix_godot_scan_dirs.ps1 ★补齐构建目录的 .gdignore（防 Godot 误导入 .obj）
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
| 2026-11-06 | v5 | **基础物件抽象落地（武器/子弹/怪物同源）** | **新增 `ARCHITECTURE.md` §17**：人物/怪物/武器/子弹**不是四个类**，而是同一个 `ObjectArchetype`（`combat.xlsx` 的 `Objects` 一行）——差异只在 ① `ObjectCapability` 能力位（`[Flags]`，可任意组合）② 四个 `ContentRef` 引用（Weapon/Projectile/Actor/Effect，**谁都能指向任何 Objects.Id**）③ 位矩阵 + 规则值。**规则值 `ValueRef`**（10 种来源）让「子弹继承武器属性」= 表里写 `wstat:WeaponDamage(5)`，代码 0 行；**熔炉 `ForgeRules`** 64 位配方字 × 簇内加权轮盘，纯 `ReadOnlySpan` 零分配（用例断言 1000 次迭代 0 字节）；**工厂等价性**——武器射子弹/武器射怪物/子弹炸开再射子弹/子弹变怪物在 `EcsObjectFactory.TryCast` 里是同一段代码；**`combat.xlsx`** 6 Sheet + `validate_content_xlsx.py` 2217 项构建期检查 + 零第三方包 `XlsxReader` 读真表的回归（改表→运行期变化可证）。**明确不做**：碰撞/空间划分/伤害结算（游戏逻辑）。修 6 个静默失效 bug。测试 101 → 174，守卫 0/0。§2.3 矩阵补 `Game.Core.Content` 子命名空间；`ECS.md` 升 v2（组件表 + 零分配约定）。**开工注意**：新增武器/子弹/怪物只改 `data/excel/combat.xlsx`，不得改 `scripts/combat/**` | `AGENTS.md`、`docs/architecture/ARCHITECTURE.md`、`docs/architecture/ECS.md`、`scripts/core/Content/**`、`scripts/core/StatId.cs`、`scripts/combat/**`、`scripts/data/Content/**`、`data/excel/combat.xlsx`、`data/excel/characters.xlsx`、`tools/data_pipeline/**`、`tests/Content/**`、`tests/BiuBiuOrigin.Tests.csproj` |
| 2026-11-05 | v4 | **角色抽象（Character）落地** | **新增 `ARCHITECTURE.md` §15 角色抽象**：角色 = 「五层数值包 + 一个技能包 + 阵营/控制两个标签」的 ECS 组合，玩家与怪物**同一组件集合**（`Transform3/Team/Health/Mana/Hurtbox/ActorIdentity/SkillState/ActorIntent`），差异只在数据行。① 数值：`StatId` 追加法力/护甲/力量·敏捷·幸运号段；`StatPackageRepository.Compose` 严格按规则6 折叠（Base 绝对值 + 四层差量，`GetLayerValue` 保留单层读取）。② 技能：`SkillSlot` 五固定槽（Shift 冲刺 / 左键射击 / Q 切武器 / R 独有 / F 交互），`SkillCastSystem` 每帧只消费一个槽，门控 `CanCast→Cast→扣法力→起冷却`，失败原因入组件供 HUD/音效反馈。③ 组合：`ActorSpawnRequest` 是唯一造人入口，**夺舍 = 改 `ControlOverride`/`TeamOverride` 两个字段**；组件只存槽位号不存引用（J2）。④ 策划可改：`data/excel/characters.xlsx`（Characters/StatPacks/SkillLoads，四行表头）为唯一真源，`CharacterContentSet` 提供逐项一致的运行时副本兜底，9 条回归用例钉住「表 ↔ 运行时」。⑤ 引擎边界：`PlayerInputIntentSource` 是唯一碰 `Input` 的地方，把 InputMap action 翻成 `InputIntent`（Q16 定点），故玩家/AI/回放共用同一代码路径。**测试 63 → 101 全绿**，架构守卫 0 error / 0 warning | `AGENTS.md`、`docs/architecture/ARCHITECTURE.md`、`scripts/core/**`、`scripts/actor/**`、`scripts/skill/**`、`scripts/data/**`、`scripts/host/control/**`、`scripts/host/DungeonDebugScene.cs`、`project.godot`、`data/excel/characters.xlsx`、`tools/data_pipeline/gen_characters_xlsx.py`、`tests/Characters/**`、`tests/BiuBiuOrigin.Tests.csproj` |
| 2026-11-05 | v5 | **第一个可玩人物 `test_hero` 落地** | **在 ECS + 角色抽象之上把一个人物真正做出来**（`ARCHITECTURE.md` 新增 §18）：① **组合根** `scripts/host/composition/`（`ISkillFactory`/`ContentSkillFactory`/`ContentSkillSet`/`NullSkill`）——「数据行 `kind` → 技能实现类」的唯一定义点，刻意不 `using Godot`，测试工程直接编译；② **人物会话** `scripts/host/CharacterSession.cs`——把出生/意图/装配/技能/移动/瞄准/视图/HUD 收成一个可 `Tick` 的单元，`DungeonDebugScene` 支持 `--hero`/`--hero-variant=`/`--hero-fire`/角色特写截图；③ **四个变体纯数据**（标准/重甲/敏捷/幸运），换变体只改 xlsx 一行，C# 0 行改动；④ **修复四个静默失效的接线**：玩家实体发现死循环（人物不出生）、技能包注册表槽位号错位（按左键永不开枪）、`DataSkill` 占位不生成子弹、意图覆盖方向写错（在管线前写组件会被意图系统抹掉）；⑤ **HUD** 显示 HP/法力/耐力/护甲减伤/移速/暴击/瞄准角/五技能槽，护甲减伤口径修正为 `armor/(armor+k)`；⑥ `TypeSet.MaxComponentCount` 16 → 64（人物 12 类组件超原上限导致运行时崩溃）；**测试 101 → 219 全绿**，架构守卫 0 error / 0 warning | `scripts/host/CharacterSession.cs`、`scripts/host/composition/**`、`scripts/host/DungeonDebugScene.cs`、`scripts/host/control/PlayerInputIntentSource.cs`、`scripts/host/view/**`、`scripts/host/bridge/**`、`scripts/core/ecs/TypeSet.cs`、`scripts/core/ecs/Actors/ActorRegistries.cs`、`scripts/actor/**`、`scripts/combat/**`、`scripts/skill/**`、`scripts/data/**`、`scripts/ui/**`、`tests/**`、`docs/architecture/ARCHITECTURE.md`、`project.godot` |
| 2026-11-09 | v8 | **2D 自绘图随窗口自动缩放** | 修「拖动窗口渲染画面仍不变大变小」（`DungeonMap2D` 场景）。上一版只修了 `stretch/mode`（视口跟着窗口变），**没修画在视口里的 2D 内容**：地图是 `Node2D` 在 `ScrollContainer` 里逐格 `DrawRect`，格子固定 16px，且 `Node2D` 没有布局尺寸 → `ScrollContainer` 拿 `GetRect()` 当内容大小 → 图撑大滚动区、永远刚好装下，缩不小也放不大。修复：`FitToViewport` **用父容器 `Size`** 算格子像素并 `clamp`，`_Process` 轮询；滑条一动即关 `AutoFit` 交出控制权。新增探针 `tools/probes/dungeon_map_autofit_probe.gd`：四种窗口下 14.64→37.14px 全部铺满；**撤掉改动后恒为 16px、2 个失败**。守卫 0/0，测试 357/357。详见 `ARCHITECTURE.md` §7.5.2 | `scripts/host/debug/DungeonMap2DView.cs`、`scripts/host/debug/DungeonMap2DPanel.cs`、`tools/probes/dungeon_map_autofit_probe.gd`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-06 | v7 | **数值包跨层校验 + 窗口缩放修复** | ① `validate_content_xlsx.py` 新增「同一 PackId 不能跨层」（`ARCHITECTURE.md` §4.3.1）：运行期仓库以 PackId 为唯一键，同 id 跨层注册必抛「数值包 id 重复」，属数据表达不了的形状，建表期拦下。现有 31 包全合规，为预防性约束；**刻意不改运行期键**为 `(PackId, Layer)`。② 修「拖动窗口不改变渲染画面」（§7.5.1）：`window/stretch/mode` 由 `canvas_items` → `disabled` 并补 1600×900 基准，根因是前者把画面钉死在引擎默认 1152×648。新增只读探针 `tools/probes/viewport_resize_probe.gd` 实测对照：旧 4/4 失败、新 4/4 通过。**经验**：stretch 配错不报任何错，只静默缩放，编译/日志/守卫均无法发现，必须靠读回尺寸的探针钉住。 | `tools/data_pipeline/validate_content_xlsx.py`、`project.godot`、`tools/probes/viewport_resize_probe.gd`、`docs/architecture/ARCHITECTURE.md` |
| 2026-11-06 | v8 | **3D 素材查看器 `tools/asset_viewer2/` 落地** | **独立的 Vulkan + ImGui 桌面程序**，用来浏览 `素材/` 下的 3D 资产：① **渲染** `VkContext` 自管设备/交换链/render pass/depth，`RenderPipeline` 走推送常量（model/viewProj/normalMatrix/baseColorFactor/hasTex），片元做 3 点光照 + Reinhard + gamma；② **加载** glTF 走 cgltf（中文路径必须先用宽字符 API 读进内存再 `cgltf_parse`，`cgltf_parse_file` 的 `fopen` 会失败）、FBX 走自研二进制解析器（7200/7400/7500 全覆盖，70/70 通过）；③ **UI** 全屏视口 + 三个 ImGui 浮动面板（文件夹树 / 资产列表 / 检视器四页签），宽度按窗口比例自适应，中文字体从 `msyh.ttc` 加载并按 DPI 缩放；④ **修三个静默失效 bug**：(a) **矩阵约定**——CPU 行主序+行向量，`ToColumnMajor` 转置后 GLSL 必须写 `row_vector * mat4`，写成 `mat4 * vec4` 不报错但几何体飞出屏幕；(b) **过期 `.spv`**——`shaders/` 源目录里的旧 spv 被 POST_BUILD `copy_directory` 反复覆盖新编译结果，导致「改 shader 画面不变」，现改由 CMake 调 `glslc` 生成；(c) **交换链尺寸**——Windows 不返回 `OUT_OF_DATE_KHR`，改在 `BeginFrame` 主动比对 framebuffer 尺寸重建；⑤ **素材过滤**：纯碰撞体（`Collision` 目录 / `_Collision` 后缀）不入列表，371 → **358 资产 / 12 目录**，`loader_test` 358/358 全绿；⑥ 新增 `ARCHITECTURE.md` §24 与白名单登记（它是独立桌面程序，放 `native/` 会污染 GDExtension 构建）。发布物在 `dist/asset_viewer2/` | `tools/asset_viewer2/**`、`dist/asset_viewer2/**`、`docs/architecture/ARCHITECTURE.md`、`AGENTS.md`、`config/architecture-rules.json` |
| 2026-11-09 | v9 | **打开 Godot 报错清理：GDExtension Debug 产物 + 扫描目录豁免** | 用户打开编辑器刷 5 类 ERROR，逐条定位并根治：① **`GDExtension dynamic library not found`**——`.gdextension` 的 `[libraries]` 声明了 `windows.debug` / `windows.release` 两条路径，但 `addons/biu_biu_native/bin/` 里**只有 Release 版 dll**；编辑器走 debug 模板 → 找不到 `Debug.x86_64.dll` → `open_dynamic_library` 失败。**报错里显示的路径是 `.gdextension` 而不是 `.dll`，具有误导性**。修复：用 `native/build-debug`（`-DCMAKE_BUILD_TYPE=Debug`）编出 1.8 MB 的 Debug dll，两条路径都齐。② **`Couldn't read OBJ file ... .cpp.obj` + `Error importing`**——Godot 不读 `.gitignore`，把 `tools/asset_viewer2/build/CMakeFiles/**/*.cpp.obj` 当成 3D 模型去做 OBJ 导入。修复：给所有「非 Godot 产物 + 扩展名会被误判」的目录放空文件 `.gdignore`（`native/build`、`native/build-debug`、`tools/asset_viewer{,2}/build`、`tools/asset_viewer{,2}/dist`、`dist/`、`libgodot-cpp`）。③ **踩坑记录（重要）**：那些构建目录本身被 `.gitignore` 排除，而 **git 无法用 `!` 规则重新包含「被排除目录内的文件」**——所以 `.gdignore` 永远进不了库，只在本机有效。新增 `tools/fix_godot_scan_dirs.ps1`（`-Check` 退出码 10 表示缺失）作为幂等补齐手段，并在 `tools/env.ps1 -Verify` 增加 `.gdignore` 检查项（optional，不阻塞）。④ 顺带修掉仓库一处**残留脏索引**：`AGENTS.md` 与 `ARCHITECTURE.md` 停在 `UU`（both modified）状态却没有任何冲突标记，是早期 `stash pop` 冲突被文本解决后未 `git add` 遗留的；核对「工作区相对 ours 为纯新增」后解除。**验证**：`--headless --import` 错误行数 **0**（修复前 6 行）；`dotnet build` 0 错 0 警；`dotnet test` **372/372**；架构守卫 `new=0 existing=0 errors=0 warnings=0` | `native/build-debug/**`、`addons/biu_biu_native/bin/biu_biu_native.windows.Debug.x86_64.dll`、`tools/fix_godot_scan_dirs.ps1`、`tools/env.ps1`、`AGENTS.md`、`.gitignore`、各构建目录 `.gdignore` |
