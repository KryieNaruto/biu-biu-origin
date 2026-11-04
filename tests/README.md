# tests/ — ECS 测试工程

> 真源：`docs/architecture/ECS.md` §10。
> 一键入口：`tools/check_all.ps1`（build → build -c Host → 架构守卫 → `dotnet test`）。

---

## 1. 怎么跑

```powershell
# 全量（收工前跑这个）
.\tools\check_all.ps1

# 只跑测试
dotnet test tests\BiuBiuOrigin.Tests.csproj

# 只跑某一组
dotnet test tests\BiuBiuOrigin.Tests.csproj --filter "FullyQualifiedName~Kernel"
```

要求：仅需 **Windows PowerShell 5.1 / PowerShell 7 + .NET SDK 10.0.401**。**不需要 Godot 运行时**。

---

## 2. 这是 MSTest，不是自研断言（以及为什么能离线跑）

ECS.md §10 原文写的是「测试工程 `tests/BiuBiuOrigin.Tests.csproj` 引用主工程（`<ProjectReference>`），`dotnet test` 一键跑」。
本轮落地时做了两处**偏离原文、并经 Lead 批准**的决定：

### 2.1 用 MSTest（不是自研断言）

| 包 | 版本 | 来源 |
|---|---|---|
| `Microsoft.NET.Test.Sdk` | **18.4.0** | 全局 NuGet 缓存 |
| `MSTest.TestAdapter` | **4.2.1** | 全局 NuGet 缓存 |
| `MSTest.TestFramework` | **4.2.1** | 全局 NuGet 缓存 |
| `MSTest.Analyzers` | **4.2.1**（传递依赖） | 全局 NuGet 缓存 |

**关键事实**：仓库的离线源 `E:\Godot\GodotSharp\Tools\nupkgs` 里**只有** Godot 的四个包，没有测试框架。
真正让测试可跑的是 **`C:\Users\Administrator\.nuget\packages\` 全局包缓存**——它恰好已缓存上面这套包。
`nuget.config` 清空了所有远程源，所以还原只可能命中全局缓存。

### 2.2 离线可用范围（其他机器还原失败时照这张表排查）

| 场景 | 是否可用 | 处理 |
|---|---|---|
| 本机（缓存已就位） | ✅ 实测通过 | — |
| 本机缓存被 `dotnet nuget locals all --clear` 清掉 | ❌ 还原失败 | 无法从公网补（`nuget.config` 禁网）。需 Lead 批准把这 4 个 `.nupkg` 放进离线源或另行登记 |
| 换一台没这套缓存的机器 | ❌ 还原失败 | 同上 |
| 想升级测试框架版本 | ⚠️ | 新版本不在缓存里 → 必然还原失败。**版本号必须钉死在上面这张表**，改动前先确认新版本已缓存并被 Lead 批准 |

> 给未来的排查者：`dotnet restore tests\BiuBiuOrigin.Tests.csproj` 的报错若是 `NU1101 找不到包 X`，
> 先跑 `Get-ChildItem C:\Users\Administrator\.nuget\packages | Select-Object Name` 对照 §2.1 的版本表，
> 而不是去改 `nuget.config` 加公网源（那会违反规则 2 §3.1「禁止从公网拉包」）。

### 2.3 用 `<Compile Include>` 而不是 `<ProjectReference>`（**硬约束，别改回去**）

ECS.md §10 写的是 `ProjectReference`，但那样做会**破坏该节自己的硬约束**：

> 「测试必须能在没有 Godot 的情况下 `dotnet test` 通过（Game.Core 不依赖引擎，这是本工程的硬约束）」

`BiuBiuOrigin.csproj` 是 `Sdk="Godot.NET.Sdk/4.7.2"`，一旦 `ProjectReference` 它，GodotSharp 的整条引用闭包
会被拖进测试进程 —— 于是「内核不依赖引擎」这件事就**从未被证明过**，只是没碰巧炸而已。

所以本项目改为**直接编译同一批无引擎源码**：

```xml
<Compile Include="..\scripts\core\**\*.cs" Link="Game.Core\%(RecursiveDir)%(Filename)%(Extension)" />
```

同一 `AssemblyName`（`BiuBiuOrigin`）、同一命名空间、同一访问级别，行为与引用主程序集等价，
但测试进程里**一个 Godot 类型都不会出现**。

代价与兜底（三条守卫用例，见 §4）：

1. `Game.Core` 里一旦出现 `using Godot` / `Godot.*` 全限定名 → 立即红（这本来就是 ECS.md §10 的守卫组）；
2. 测试程序集的引用表里一旦出现 `Godot*` → 立即红（等价于「有人把 csproj 改回 ProjectReference」）；
3. 被 glob 编进来的源码集合一旦为空 → 立即红（防「0 个文件却测试全绿」的假阳性）。

**`scripts/core/**` 之外的内容不由本工程编译**：宿主层（`scripts/host/**`，唯一允许 `using Godot` 的层）
由 `tools/check_all.ps1` 的 `dotnet build -c Host` 步骤覆盖。两条路径合起来才是完整覆盖。

---

## 3. 测试组与 ECS.md §10 的对应

| 目录 | 组 | 覆盖 |
|---|---|---|
| `Layering/` | 分层守卫 | `Game.Core` 源码 0 个 `using Godot`；测试程序集不引用 Godot；源码集合非空 |
| `Kernel/` | ECS 内核不变量 | 创建/销毁/世代复用、原型迁移后旧引用失效、Query 含/排除、chunk 边界 0/1/128/129、CommandBuffer 顺序与冲突、管线拓扑排序与环检测 |
| `Determinism/` | 确定性 | 同 seed 两次跑同一系统序列，组件快照逐字节一致 |
| `Allocation/` | 零分配 | `Query.ForEach` 稳态 `GC.GetAllocatedBytesForCurrentThread` 前后差 = 0 |

---

## 4. 写用例的规矩

1. **断言必须对应用例语义**。为了跑绿而放松断言（比如把「0 分配」改成「分配 < 1KB」、把「逐字节一致」改成「长度一致」）视为**测试无效**，比没有测试更糟。
2. **不许 `[Ignore]` 掩盖未实现的契约**。内核没落地 = 测试工程编译不过，这是如实信号，不要用空用例凑数。
3. **断言里不写魔法数值**：确定性用「同 seed 序列逐元素相等 / 不同 seed 不等 / Fork 子流独立」表达，不写死具体随机数（实现微调时不会假红，语义不弱）。
4. **零分配用例要预热**：先跑几轮 warm-up 让 JIT / 首次路径完成，再测稳态；只断言稳态那一段的 delta。
5. 离线、无网络、无 Godot 依赖——新增用例不得引入这三类依赖中的任意一种。
