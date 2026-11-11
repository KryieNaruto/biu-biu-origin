# docs/modules/ · 五大模块索引目录

> 版本: v1 ｜ 建立日期: 2026-11-11 ｜ 产出任务: `t-map`（整合）
> 本目录是**只增不改**的归纳产物：把 `docs/` 下 17 份原有文档按五大模块重新组织成可读的索引，
> **不修改、不搬移、不复制**任何原有文档。总入口是 [`../MODULE_MAP.md`](../MODULE_MAP.md)。

---

## 从哪里开始

| 你是 | 从哪进 |
|---|---|
| 第一次来 / 不知道从哪读 | [`../MODULE_MAP.md`](../MODULE_MAP.md) **§1 新人 30 分钟路线** |
| 想知道某份文档归谁 | [`../MODULE_MAP.md`](../MODULE_MAP.md) **§2 文档 × 模块矩阵** |
| 知道自己属于哪个模块 | 下表的模块 README |
| 手里有具体任务 | [`../MODULE_MAP.md`](../MODULE_MAP.md) **§4 任务路由表** |
| 想核对我们改了什么 / 原文哪里过时 | [`../MODULE_MAP.md`](../MODULE_MAP.md) **§5 待核对汇总（27 条）** |
| 想复核模块边界与归属判据 | [`_shared/BOUNDARY.md`](_shared/BOUNDARY.md) |

---

## 目录结构

```text
docs/
├─ MODULE_MAP.md              ★ 总索引（一页读完）
└─ modules/
   ├─ README.md               ← 本文件
   ├─ _shared/
   │  └─ BOUNDARY.md          模块边界定义 + 文档归属总表 + ARCHITECTURE.md 逐节切分（基线）
   ├─ 策划/README.md          数值 / 数据表 / 内容扩展
   ├─ 地编/README.md          房间 / 地牢 / 装饰 / 外观包摆放 / 训练房
   ├─ 角色/README.md          角色抽象 / 五槽技能 / HUD 契约 / 加角色流程
   ├─ 开发/README.md          开收工流程 / 目录契约 / 选型 / 构建校验 / 坑清单
   └─ ECS架构师/README.md     判定 J1~J7 / 组件规范 / 内核 API / 扩展决策树
```

## 五个模块

| 目录 | 中文名 | 英文 id | 主归属的文档 | README |
|---|---|---|---|---|
| [`策划/`](策划/README.md) | 策划 | `planning` | `rules/04`、`rules/05`、`codex/index.html` | 386 行 |
| [`地编/`](地编/README.md) | 地编 | `level` | `artifacts/decor_catalog.md`、`decor_contact_sheet.png` | 391 行 |
| [`角色/`](角色/README.md) | 角色 | `character` | `design/character-hud-contract.md` | 270 行 |
| [`开发/`](开发/README.md) | 开发 | `dev` | `rules/01`、`rules/02`、`rules/07`、`rules/08`、`prompts` ×2 | 384 行 |
| [`ECS架构师/`](ECS架构师/README.md) | ECS 架构师 | `ecs` | `architecture/ARCHITECTURE.md`、`architecture/ECS.md`、`rules/03`、`rules/06` | 334 行 |

> **目录名以中文为准**（captain 裁决）。英文 id 只用于检索与交叉引用；
> `_shared/BOUNDARY.md` §2.1 里出现的 `planning/level/character/dev/ecs` 是**已作废的草稿**，不作为目录名。

---

## 五份 README 的统一结构

对齐 `_shared/BOUNDARY.md` §2.3 的六段模板（各负责人在此基础上做了扩展，节号不完全相同）：

1. 这个模块负责什么（3~5 行）
2. 我必读的文档（表：路径 · 章节 · 为什么读）
3. 我读一部分就够的（次归属）
4. 常见任务 → 该读哪一节（路由表）
5. 我不管什么（Out + 交界处）
6. 待核对（**只记录，不改原文**）→ 已全部汇总进 [`../MODULE_MAP.md`](../MODULE_MAP.md) §5

---

## 使用纪律

1. **只增不改**：本目录以外的 `docs/` 文件（17 份 + `AGENTS.md`）**一个字节都不动**。发现过时处写进模块 README 的「待核对」，由整合阶段统一记入 `MODULE_MAP.md` §5，**不擅自改原文**。
2. **不复制契约**：本目录所有文件只写「章节号 + 一句话 + 相对路径」。精确签名、阈值、表结构一律以原文档为准——`ARCHITECTURE.md` 是唯一架构真源，ECS 形状以 `ECS.md` 为准。
3. **只写章节号，不写行号**（行号会随时间漂移）。
4. **规则编号写文件名** `rules/NN`（如 `rules/04`），不要写「规则 4」这类数字别名（工程里存在规则编号双体系）。
5. **本目录不描述新的架构形状**：它不是 `ARCHITECTURE_v2`，只是导航。任何架构变更仍按 `rules/07` 就地覆盖 `ARCHITECTURE.md`。

---

## 相关但不在本目录的必读

| 文档 | 位置 | 为什么 |
|---|---|---|
| `AGENTS.md` | 仓库根 | 项目唯一启动入口；五份 README 均已把它列为必读 |
| `architecture/ARCHITECTURE.md` | [`../architecture/ARCHITECTURE.md`](../architecture/ARCHITECTURE.md) | 唯一架构真源（3,228 行，**按节读**，切分见 BOUNDARY §4.2） |
| `architecture/ECS.md` | [`../architecture/ECS.md`](../architecture/ECS.md) | ECS 形状真源（519 行） |
| `rules/07` / `rules/08` | [`../rules/07-definition-of-done.md`](../rules/07-definition-of-done.md) · [`../rules/08-parallel-sessions.md`](../rules/08-parallel-sessions.md) | 跨模块必读：收工动作与并行纪律 |

---

## 变更日志

| 日期 | 版本 | 任务 | 变更摘要 |
|---|---|---|---|
| 2026-11-11 | v1 | `t-map` | 建立本目录说明：入口导航、目录结构、五模块一览、README 统一结构、使用纪律 |
