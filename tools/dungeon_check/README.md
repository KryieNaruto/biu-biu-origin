# tools/dungeon_check —— 地牢规划器离线校验

一句话：批量跑种子，用 `DungeonValidation` 的六条不变式判定房间图，并检查内容槽位是否合理、是否可复现。

## 为什么不放进 `tests/`

`tests/BiuBiuOrigin.Tests.csproj` **只编译 `scripts/core/**`**，这是「Game.Core 零引擎依赖」的
证明性约束（`docs/architecture/ECS.md` §10）——测试程序集里只要出现一个 `using Godot` 或一个
`scripts/spawn/**` 的类型，那条证明就失去意义。而地牢规划器在 `scripts/spawn/rooms/`，
拉进测试程序集会让约束失效。所以规划器的性质测试放在这个独立控制台工程里：
它直接编译 `scripts/core/** + scripts/spawn/rooms/**` 两份源码，在不依赖 Godot 的进程里跑。

## 用法（在工程根执行）

```powershell
dotnet run --project tools/dungeon_check -- --seeds 200 --out tools/dungeon_check/report.md
```

退出码：全绿 `0`；任一断言失败 `1`；参数错误 `2`。可直接接进 `check_all.ps1`。

## 它断言什么

1. 每个种子都通过 `DungeonValidation`（房间/链路/门/隐藏房/可达性六条不变式）；
2. 同 seed 两次生成结果完全相同（确定性的回归护栏）；
3. 内容槽位在房内、互不重叠（保底槽位允许贴近，但圆心不许重合）；
4. 类别语义：特殊房有交互物或道具、隐藏房有奖励、原型房有刷怪点。

> 命名空间刻意不是 `Game.*`：GS007 把「`Game.*` 命名空间出现在 `scripts/` 之外」判为游戏源跑出边界。
