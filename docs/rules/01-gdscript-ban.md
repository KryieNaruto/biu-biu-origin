# 规则 1 · 除 UI 层外禁止 GDScript

> 状态: v1  最后更新: 2026-11-03
> 适用工程: `D:\workspace\myspace\godot_game\biu-biu-origin`（Godot 4.7.2**-mono**）
> 自动检查: `tools/check_architecture.ps1` → **GS001**（error）、**GS002**（warning）
> 关联: [AGENTS.md](../../AGENTS.md) §2/§3/§4 ｜ [02-tech-selection.md](02-tech-selection.md) ｜ [07-definition-of-done.md](07-definition-of-done.md)

---

## 0. 一句话

> **除「UI 层」外，任何地方都不许出现 `.gd`。UI 层也优先用 C#，只在纯布局 / 纯编辑器工具时才允许 `.gd`。**
> 放行只有一个渠道：`config/architecture-rules.json` 的 `allowGDScript`，并且必须同时在本文档与 `AGENTS.md` 登记理由。

## 1. 规则原文

用户原话（2026-11-03）：

> **除 UI 层外禁止 GDScript。**

展开成可执行的判据：

| # | 判据 | 违规判定 |
|---|---|---|
| R1-1 | 工程内任何 `*.gd` 必须被 `config/architecture-rules.json` 的 `allowGDScript` 某一条通配命中 | `GS001` error |
| R1-2 | `.tscn` 场景里挂的脚本 `Path` 必须指向 `.cs`（UI 层白名单目录内的场景除外，见 §4） | 人工审查 + `GS001`（warning，场景侧） |
| R1-3 | 不得用「换个扩展名 / 放进 exempt 目录 / 关掉守卫」的方式绕过 | 代码审查直接打回 |
| R1-4 | 放行条目必须在 `config/architecture-rules.json` + 本文档 §5 + `AGENTS.md` §4 三处**同时**登记 | 缺一处视为违规 |

---

## 2. 「UI 层」的边界（唯一定义）

**UI 层 = `res://scripts/ui/**` + `addons/**` 内置 UI 脚本。**

| 目录 | 属于 UI 层？ | 允许 `.gd`？ | 说明 |
|---|---|---|---|
| `res://scripts/ui/**` | ✅ 是 | 允许（但**优先 C#**） | HUD、菜单、弹窗、飘字、背包格、提示气泡 |
| `addons/**`（插件内的 UI 脚本） | ✅ 是 | 允许 | 第三方插件；不参与玩法结算 |
| `res://tools/probes/**` | ❌ 不是 UI 层 | 允许（**探针特批**） | 只读探针，不进游戏包，理由见 §5 |
| `res://scripts/` 其余任何子目录 | ❌ | **禁止** | core / data / actor / combat / skill / spawn … |
| `res://scenes/`、`res://native/`、`res://shaders/`、`res://tools/`（probes 除外） | ❌ | **禁止** | 场景、C++、shader、构建脚本 |

**「UI 层」不等于「画界面的一切东西」。** 下面这些都不算 UI 层，一律禁止 GDScript：

- 相机控制、震屏、命中停顿（HitStop）→ 影响手感，属于 gameplay，走 C#（`scripts/actor`、`scripts/combat`）
- 输入映射 / 按键重绑定逻辑 → `scripts/core`
- 飘字的数据来源（伤害值计算）→ `scripts/combat`；飘字**显示**组件才属于 `scripts/ui`
- 粒子、描边、后处理 → `.gdshader` + C# 驱动（规则 2），不是 GDScript

### 2.1 UI 层「优先 C#」是什么意思

规则允许 UI 层用 GDScript，但**推荐用 C#**，因为工程是 mono 工程（`[dotnet] project/assembly_name="BiuBiuOrigin"`），混语言会带来：

- 两种语言的信号/事件签名不一致，重构时容易漏改；
- `scripts/ui` 里若有 `.gd` 与 `.cs` 混放，同一子目录内会触发 `GS002` warning；
- 调试时要在两套挂点之间来回切。

**只有下列场景才允许 UI 层写 `.gd`**：

1. **纯布局**：只做 `Control` 的锚点、容器嵌套、`theme` 绑定，不含任何逻辑判断；
2. **纯编辑器工具**：`@tool` 脚本，且只在编辑器里跑（必须同时进 `addons/**` 白名单）；
3. **一次性验证**：临时验证某个 API 行为，验证完**当场删除**，不提交。

> 凡出现「信号处理 + 状态判断 + 数据读写」三件套，就该是 C#。

---

## 3. 白名单机制（唯一放行渠道）

### 3.1 配置文件

`config/architecture-rules.json`（唯一来源）：

```json
{
  "allowGDScript": ["scripts/ui/**", "addons/**", "tools/probes/**"],
  "forbidGDScript": []
}
```

| 字段 | 语义 | 守卫行为 |
|---|---|---|
| `allowGDScript` | 允许出现 `.gd` 的路径通配（相对项目根，`/` 分隔，`**` 表示任意层级） | 命中 → 放行；未命中 → `GS001` error |
| `forbidGDScript` | 即使在白名单内也必须禁止的路径（**优先级最高**） | 命中 → `GS001` error，并显示命中的通配 |
| `ignore` | 整个不扫描的目录（如 `addons/**`） | 不进入任何检查 |

通配语义（脚本实现，刻意保持简单）：

- `**` 与 `*` 都表示「任意字符（含 `/`）」，`?` 表示一个字符；
- **目录通配自动包含其子目录**：`scripts/ui/**` 与写 `scripts/ui` 等价；
- 大小写不敏感；路径统一转成 `scripts/ui/Hud.gd` 这种形式匹配。

### 3.2 放行的三步登记（缺一不可）

1. **改配置**：在 `config/architecture-rules.json` 的 `allowGDScript` 加一条；
2. **登记理由**：在本文档 §5 的登记表加一行（写清为什么必须用 `.gd`、替代方案为何不可行、复审日期）；
3. **登记到 AGENTS.md**：在 `AGENTS.md` §4「白名单登记表」加一行。

> 三处不同步 = 视为违规，代码审查可直接打回。这是**唯一**放行渠道：不接受「在 PR 描述里说明」「在代码注释里写 arch-ok」「临时改脚本跳过」等替代。

### 3.3 已登记的放行（当前状态）

| 放行对象 | 允许语言 | 理由 | 复审日期 |
|---|---|---|---|
| `scripts/ui/**` | GDScript / C# | 规则 1 原文允许「UI 层」 | 2026-12-31 |
| `addons/**` | 任意 | 第三方插件与自研编辑器工具，不参与运行时玩法 | 2026-12-31 |
| `tools/probes/**` | GDScript | 只读探针（截帧、相机、色板验证），不进游戏（见 §5.1） | 2026-12-31 |

---

## 4. 被禁的常见误用 → 替代写法

下面每一行都是真实会出现的借口。**表中「替代写法」列就是唯一正确答案**。

| # | 被禁的常见误用 | 为什么在别处会踩坑 | 替代写法（C# / C++ / shader / 构建脚本） |
|---|---|---|---|
| U1 | **工具探针**：`tools/xxx_probe.gd` 用来截帧、验相机、验色板 | 探针会随手引用 `get_node("/root/...")`，久了变成第二个入口；且探针里的逻辑常被复制进游戏 | ① 优先：**C# 探针** `tools/probes/*.cs` + `dotnet run`；② 必须用 Godot 场景时，放 `tools/probes/*.gd`（已登记放行），且文件头写 `# PROBE-ONLY: 不进游戏包` |
| U2 | **测试脚本**：`scripts/test/test_dummy.gd` 造怪/造场景 | 测试脚本一旦被 `.tscn` 引用就进了发布包 | 用 **C# 单元测试**（`tools/` 下的 xunit 工程）或 C# 场景脚本；需要场景时用 `scenes/dev/` + C# `#if DEBUG` |
| U3 | **shader 预处理**：用 `.gd` 生成 / 拼接 shader 源码 | 构建期生成 shader 无法在编辑器里所见即所得，改一个参数要重跑 | shader 一律写 **`.gdshader`**（`res://shaders/**`）；需要参数化暴露 `uniform`，由 `.tres` 外部配置（规则 4）；批量生成走**构建脚本**（`tools/data_pipeline` 的 Python） |
| U4 | **场景脚本**：`.tscn` 的根节点挂 `.gd` 做关卡逻辑 | 场景脚本最容易长成隐形 gameplay，且和 C# 场景脚本两套范式 | 挂 **`.cs`**（`scripts/` 下，命名空间 `Game.*`）；场景里 `[Export]` 暴露参数 |
| U5 | **Resource 脚本**：自定义 `Resource`（`class_name BulletDef` 用 `.gd`） | 数据类必须能被校验、被生成、被 C# 读；双语言 Resource 在序列化时会丢类型 | 用 **C# `[GlobalClass]` `partial class ... : Resource`**，配 `[Export]` 字段；由 `tools/data_pipeline` 从 Excel 生成 `.tres` |
| U6 | **Autoload**：`core/events.gd` 当全局事件总线 | Autoload 名会出现在全工程，迁移成本极高；GDScript 单例和 C# 静态服务并列会造成两套状态 | 用 **C# Autoload**（`Game.Core.EventBus`，`partial class ... : Node`，在 `project.godot` 的 `[autoload]` 注册），或纯 C# 静态事件中心 |
| U7 | **Editor plugin**：自研可视化调参插件用 `.gd` 写主体 | 插件与运行时共享类型会互相污染；插件崩溃会带崩编辑器 | 插件脚手架可以 `.gd`（`addons/**` 已放行），但**调参面板读写的数据类必须是 C# Resource**；重逻辑走 GDExtension（规则 2） |
| U8 | **一次性验证**：临时写个 `.gd` 试 API | 临时文件泄漏进仓库 | 用 C# 的 `GD.Print` 试，或 **当场删除**；`tools/probes/**` 下的探针必须带 `# PROBE-ONLY` 头 |
| U9 | **兼容老原型**：把 `biu-biu` 老工程的 `.gd` 直接拷进来 | 老原型是全 GDScript 的，拷进来等于规则 1 全面失守 | 只读参考，**逐行重写为 C#**；见 §6 |

### 4.1 场景侧约束（`.tscn` 不许挂 `.gd`）

```ini
; ❌ 禁止：gameplay 场景挂 GDScript
[ext_resource type="Script" path="res://scripts/actor/player.gd" id="1_player"]
; ✅ 正确：挂 C#
[ext_resource type="Script" path="res://scripts/actor/PlayerController.cs" id="1_player"]
```

- 判定口径：**场景所在目录**决定放行。`res://scripts/ui/**` 与 `res://addons/**` 下的场景可以挂 `.gd`；其余目录的场景只许挂 `.cs`。
- 守卫脚本对「位于 GDScript 白名单目录、却挂了 C# 脚本」的场景给出 `GS001`（warning），提示 UI 层保持单一语言。
- Godot 4 的 C# 脚本同样用 `type="Script"`，只是路径以 `.cs` 结尾；**不要**为 C# 手写 `[ext_resource type="CSharpScript"]`。

---

## 5. 历史与现状

### 5.1 `tools/probes/**` 为什么特批 GDScript

旧原型 `D:\workspace\myspace\godot_game\biu-biu` 里有一整套 **`.gd` 探针**（`basis_probe`、`camera_probe`、`color_probe`、`cull_probe`、`frame_probe`、`frustum_probe`、`ground_only_probe`、`hud_probe`、`layout_probe`、`mesh_probe`、`occlusion_probe`、`rt_probe`、`screen_probe`、`smoke_test`、`viewport_probe`、`vis_probe` 等）——它们的特点是：**只读、单文件、`--headless` 或一次性运行、从不被游戏场景引用**。

这类工具用 GDScript 写的成本（一个文件、零编译）远低于 C#（要过 `dotnet build`、要挂场景）。因此本工程**有条件放行**：

- 位置：**只能**在 `tools/probes/**`；
- 文件头必须写：`# PROBE-ONLY: 只读验证，不得被 res://scenes 或 res://scripts 引用`；
- 不得 `preload` / `load` 任何 `res://scripts/**` 下的脚本（探针必须独立）；
- 不得被 `.tscn` 引用；一旦被引用，视为 U4（场景脚本）违规。

### 5.2 老原型只读，不许拷代码

| 项 | 事实 |
|---|---|
| 老工程 | `D:\workspace\myspace\godot_game\biu-biu` |
| 语言 | **全 GDScript**，32 个 `.gd`（其中 `scripts/**` 16 个 + `tools/**` 16 个探针） |
| 本工程章程 | 只可**只读参考**，**不得拷贝进 `biu-biu-origin`** |
| 拷贝的后果 | 直接被 `GS001` 拦下（它们都不在 `scripts/ui/**` 白名单内） |

老工程目录（供重写时对照）：

```text
biu-biu/
├─ scripts/
│  ├─ actors/   bullet.gd  player.gd
│  ├─ camera/   camera_rig.gd
│  ├─ core/     events.gd  game_const.gd  game_flow.gd  game_math.gd  game_state.gd  main.gd
│  ├─ scenarios/ test_scene.gd
│  ├─ style/    lowpoly_mat.gd  mesh_kit.gd  palette.gd
│  ├─ test/     test_dummy.gd  test_prop.gd
│  └─ ui/       hud.gd          ← 只有这一个在「UI 层」语义内
└─ tools/     basis_probe.gd  camera_probe.gd  color_probe.gd  cull_probe.gd  frame_probe.gd
              frustum_probe.gd  ground_only_probe.gd  hud_probe.gd  layout_probe.gd
              mesh_probe.gd  occlusion_probe.gd  rt_probe.gd  screen_probe.gd  smoke_test.gd
              viewport_probe.gd  vis_probe.gd
```

**重写对照**（老 `.gd` → 新工程归属）：

| 老文件 | 新工程归属 | 语言 |
|---|---|---|
| `core/events.gd` | `scripts/core/EventBus.cs` | C#（见 U6） |
| `core/game_const.gd` | `data/excel/*.xlsx` → `data/generated/*.tres` | 数据（规则 4） |
| `core/game_math.gd` | `scripts/core/MathX.cs` 或 GDExtension（若热点） | C# / C++ |
| `core/game_flow.gd` / `game_state.gd` | `scripts/core/GameFlow.cs` | C# |
| `core/main.gd` | `scripts/core/GameRoot.cs`（挂 `scenes/main.tscn`） | C# |
| `actors/player.gd` / `bullet.gd` | `scripts/actor/` / `scripts/combat/` | C# |
| `camera/camera_rig.gd` | `scripts/actor/CameraRig.cs`（手感参数走 Excel） | C# |
| `style/*.gd`（调色板 / 材质 / 网格工具） | `scripts/core/`（生成逻辑）+ `data/excel/Palette` 表 | C# + 数据 |
| `test/*.gd` / `scenarios/test_scene.gd` | `scenes/dev/` + C# `#if DEBUG` | C# |
| `ui/hud.gd` | `scripts/ui/Hud.cs`（**推荐** C#）或原样 `.gd`（允许） | C# / GDScript |
| `tools/*_probe.gd` | `tools/probes/*.gd`（放行）或改写为 C# | GDScript / C# |

---

## 6. 豁免 / 迁移申请表

**用法**：一行 = 一条豁免。复制表格到 `docs/plan/` 下的任务说明，或直接在本文档 §7 追加。**只填表不改 JSON = 没生效**。

```markdown
| 编号 | 申请人 | 日期 | 违规对象（路径/通配） | 违规类型 | 为什么必须这样 | 替代方案为何不可行 | 计划消除日期 | 守卫配置改动 | AGENTS.md §4 已登记 | Lead 批准 |
|---|---|---|---|---|---|---|---|---|---|---|
| EX-001 |  |  | `res://tools/xxx.gd` | GS001 |  |  |  | `allowGDScript += "tools/xxx.gd"` | ☐ | ☐ |
```

填写要求：

| 字段 | 要求 |
|---|---|
| 编号 | `EX-NNN` 递增，全局唯一 |
| 违规对象 | 精确到文件或最小通配，**禁止**写 `scripts/**` 这种大范围 |
| 违规类型 | `GS001` / `GS002` / `GS003`（C++ 位置）/ 场景侧 |
| 为什么必须这样 | 说明 GDScript 相对于 C# 的**不可替代性**（例如「需要 `@tool` 且必须即时反映在编辑器里」） |
| 替代方案为何不可行 | 至少写一条尝试过的替代方案及其失败原因 |
| 计划消除日期 | 必填，最长 3 个月；到期未消除 = 自动失效，守卫会重新报 error |
| 守卫配置改动 | 贴出 `config/architecture-rules.json` 的 diff |
| Lead 批准 | 必须真人确认；AI 不得自行批准 |

> **迁移申请**（老原型 `.gd` → 新工程）不需要走本表：迁移的目标形态必须是 C# / C++ / 数据表，见 §5.2 对照表。

---

## 7. 守卫如何检查（GS001 / GS002 实现口径）

### 7.1 与规则 2 的交叉点：`GS004` 对 UI 层的口径

UI 层放行 GDScript，**不等于**放行任意引用。`GS004` 对 `scripts/ui/**` 下的 `.gd` 与 `.cs` 一视同仁地扫描：

| 引用 | 结果 |
|---|---|
| `using Game.Core` / `Game.Core.*`（只读接口） | ✅ 放行（UI 读数据的唯一合法通道） |
| `using Game.Data` / `Game.Data.*`（只读 DTO） | ✅ 放行 |
| `Game.Combat` / `Game.Actor` / `Game.Skill` / `Game.Spawn` | ⚠️ warning（越过了结算边界） |
| `Native.*`（GDExtension 封装） | ⚠️ warning（UI 不得直接摸原生层） |
| 上述字串只出现在注释里 | ✅ 不报 |

**UI 层具体能读哪些接口**（声明位置见 [ARCHITECTURE.md §3.2](../architecture/ARCHITECTURE.md)，选型理由见 [02-tech-selection.md](02-tech-selection.md) §3.2.1）：

| 想读 | 走哪条合法通道 |
|---|---|
| 当前血量 / 冷却 / 波次进度 | `Game.Core` 的**只读视图接口**（UI 只订阅，不调结算方法） |
| 外观/换装描述 | `IVisualDescriptor`（声明在 `Game.Data`，正式接口） |
| 技能列表展示 | `ISkill` / `ISkillTarget`（声明在 `Game.Data`，**只读 `CanCast` 做置灰**，不得调用 `Cast`） |
| 配置与数值显示 | `Game.Data` 的只读 DTO / `IStatProvider.Current` |
| 想触发一次夺舍 | ❌ 不行。`IActor.TryApplyTakeover` 属 `Game.Actor`，UI 必须发事件给 `Game.Core` 的事件总线，由逻辑层调用 |

> 一句话：**UI 能"看得见"的接口都在 `Game.Core` / `Game.Data`；能"改变游戏状态"的调用一律不在 UI 层。**

> 官方口径：`docs/architecture/ARCHITECTURE.md` §2.3 允许/禁止矩阵 + §2.4。选型理由见 [02-tech-selection.md](02-tech-selection.md) §3.2、§8。


见 [02-tech-selection.md §8](02-tech-selection.md#8-与架构守卫的对应) 的完整说明，这里只列要点：

| 规则 ID | 级别 | 实际实现 |
|---|---|---|
| `GS001` | **error** | 全工程扫 `*.gd`（`*.gd.uid` 排除），比对 `allowGDScript`；未命中报 error，命中 `forbidGDScript` 报 error（forbid 优先） |
| `GS001`（场景侧） | warning | `scripts/ui/**`、`addons/**` 下的 `.tscn` 若挂了 `.cs`，提示「UI 层统一用一种语言」 |
| `GS002` | **warning** | `scripts/ui/**` 下同一目录同时存在 `.gd` 与 `.cs` 时提示（规则 1 允许 UI 用 GDScript，因此**不报 error**；由 Lead 裁决，见 §8） |

**为什么 GS002 只是 warning**：用户规则原文是「除 UI 层外禁止 GDScript」，UI 层同时用两种语言**并不违反规则 1**，只是维护成本高。把它降级为 warning 是 Lead 的显式决定（2026-11-03），避免守卫在合法的 UI 代码上阻断 `dotnet build` 后的收工流程。

---


## 8. 待 Lead 仲裁 / 已知取舍

| # | 议题 | 当前最保守实现 | 影响 |
|---|---|---|---|
| C1 | UI 层是否**完全**禁 GDScript（规则原文允许） | 允许，只报 `GS002` warning | 若日后想收紧，把 `allowGDScript` 里的 `scripts/ui/**` 移到 `forbidGDScript` 即可，无需改脚本 |
| C2 | `tools/probes/**` 的放行期限 | 已写「复审日期 2026-12-31」，但守卫脚本**不做到期检查** | 到期后需人工复审；若要自动到期，需在 JSON 增加 `expires` 字段并改脚本（属新任务） |
| C3 | `addons/**` 在 `ignore` 与 `allowGDScript` 中同时出现 | `ignore` 优先，`addons/**` 实际**完全不扫描** | 第三方插件里若有 `.cs` 与 `.gd` 混用，守卫看不见；这是有意的（不审第三方代码） |
| C4 | 老原型里 `scripts/ui/hud.gd` 之外的 15 个 gameplay `.gd` | 全部需重写为 C#，不提供任何批量放行 | 这是规则 1 的核心，不列为可仲裁项 |

---

## 9. 自检清单（提交前手过一遍）

- [ ] `Get-ChildItem -Recurse -Filter *.gd` 的结果**只有** `scripts/ui/**`、`addons/**`、`tools/probes/**` 下的文件
- [ ] 新增的 `.tscn` 挂的脚本路径以 `.cs` 结尾（UI 白名单目录除外）
- [ ] 没有从 `biu-biu` 老工程拷贝 `.gd`
- [ ] 若新增放行：`config/architecture-rules.json` + 本文档 §3.3 + `AGENTS.md` §4 三处同步，并填了 §6 申请表
- [ ] `.\tools\check_architecture.ps1` 无新增 `GS001` 条目（`GS002` warning 需在汇报里说明原因）

---

## 变更日志

| 日期 | 版本 | 变更 |
|---|---|---|
| 2026-11-03 | v1 | 建立规则 1 正文：UI 层边界、白名单机制、9 条常见误用替代写法、老原型重写对照、豁免申请表 |
| 2026-11-03 | v1.1 | 按 Lead 口径校正守卫描述：`GS002` 恒为 warning（规则 1 本身允许 UI 层 GDScript，不存在 error）；`GS004` 只拦 `Game.Combat/Actor/Skill/Spawn` 与 `Native.*`，`using Game.Core` / `using Game.Data` 属合法只读引用；`GS001` 判定前提为文件位于 `forbidGDScript` 或不在 `allowGDScript` |
