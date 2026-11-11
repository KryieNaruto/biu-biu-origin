# 规则 8：多会话并行 worktree 工作流

> 状态: v1.2  最后更新: 2026-11-11（总架构师裁定：worktree 强制化 M1~M5）  生效范围: **所有会话（人 + AI agent，含总架构师）**，不再以「是否并行」为前提
> 关联文档: [AGENTS.md](../../AGENTS.md)、[07-definition-of-done.md](07-definition-of-done.md)、[03-abstract-first.md](03-abstract-first.md)、[06-extensibility.md](06-extensibility.md)、[../architecture/ARCHITECTURE.md](../architecture/ARCHITECTURE.md)
> 配套工具: [../../tools/session.ps1](../../tools/session.ps1)、[../../config/architecture-rules.json](../../config/architecture-rules.json)

---

## 0. 三十秒速读

| 你问 | 答案 |
|---|---|
| 这条规矩管什么 | 多个会话（多个 AI / 多人）**同时**改同一个工程时的隔离与合并 |
| 怎么实施隔离 | 每个会话一个 **git worktree** + 一条 `session/<名字>` 分支，物理目录在项目根**之外** |
| 一句话要求 | **一个会话 = 一个 worktree = 一条分支；改动只在 worktree 内发生；收工合并回 `main`** |
| 用什么工具 | `.\tools\session.ps1 start / list / status / commit / finish / stop` |
| 最容易被违反的三件事 | 在 `main` 上直接改 ／ 两个会话共用一个目录 ／ worktree 建在项目根里面 |
| 什么时候必须开（2026-11-11 总架构师裁定） | **只要执行任务就开**，不看改动大小、不看是否并发、不看是谁（含总架构师）；做完**必须合并回 `main`**。见 §2.0 M1~M5 |

> 本规则的自动化检查不依赖架构守卫：它靠 `tools/session.ps1` 的 `list` 与 `status` 两件工具把「谁在哪个 worktree、有没有未提交改动」变成可查的事实。

---

## 1. 规则原文与判据

### 1.1 规则原文（第 8 条铁规）

> **规则8：工程同时有多个会话并行，每个会话开临时工作空间，完成后合并到主线。**

### 1.2 一句话判据

> **我这次的每一个文件改动，是不是都发生在 `..\.worktrees\<我的会话名>\` 里，最后通过 `session.ps1 finish` 回到 `main`？**
> 是 → 合规。否 → 立刻停下，先按 §2 开会话，再把改动搬进去。

### 1.3 为什么需要这条规矩

同时开两个会话（两个 agent、或一个 agent + 一个人）分别改代码时，如果它们共用一个工作目录，会出现四类事故：

| 事故 | 具体表现 | 后果 |
|---|---|---|
| **互相覆盖** | 会话 A 读到旧文件 → 会话 B 写入新版本 → 会话 A 基于旧内容整篇写回 | B 的工作无声消失，且没有任何报错 |
| **半成品混入编译** | A 正在改 `scripts/combat/`，B 此时执行 `dotnet build` | B 看到一堆别人的错误，误判成自己的问题 |
| **架构守卫基线漂移** | 两人同时跑 `check_architecture.ps1 -UpdateBaseline` | 基线互相覆盖，守卫从「检查」退化成「记账」 |
| **ARCHITECTURE.md 打架** | 两个会话各改一节，最后一个人整篇写回 | 另一节被冲掉（见 §6 特例） |

### 1.4 为什么用 worktree，而不是 clone 或「切分支」

| 方案 | 卡在哪 | 结论 |
|---|---|---|
| **多个 `git clone`** | 每个克隆有一份独立的 `.git` 对象库，体积翻倍；分支、提交历史要多次 push/pull 才同步；离线环境下（本机 GitHub 不可达）几乎不可行 | ❌ 不用 |
| **同一个目录里切分支** | 同一时刻只能有一条分支被检出；切分支会把另一个会话正在用的目录内容换掉，**Godot 编辑器 / `.godot` 导入缓存随之整体失效并触发全量重导入**；还要求会话手动 stash 在途改动 | ❌ 不用 |
| **`git worktree`** | **共享同一个 `.git` 对象库**（零拷贝、提交立刻互相可见）；每个 worktree 拥有独立的工作目录与独立检出，互不打断；主工作树可以一直在编辑器里开着 | ✅ **本工程采用** |

> 一句话：**worktree 是「同一份仓库历史的多个检出」，clone 是「多份仓库」，切分支是「一份检出反复换衣服」。**
> 我们要的是「并行且互不打断」，只有 worktree 同时满足。

worktree 的目录布局：

```text
E:\workspace\myspace\godot_game\
├─ biu-biu-origin\                 ← 主工作树（main 分支）+ 整个仓库唯一的 .git
│  ├─ .git\                       ← ★ 只有主工作树里是真实目录，所有 worktree 共享它
│  ├─ tools\session.ps1
│  └─ ...（项目本体）
└─ .worktrees\                     ← ★ 会话工作空间容器（在项目根之外，兄弟目录）
   ├─ skill-takeover\             ← 会话 A：分支 session/skill-takeover
   └─ render-outline\             ← 会话 B：分支 session/render-outline
```

---

## 2. 标准流程（6 步，一步都不能跳）

### 2.0 强制口径（总架构师裁定，**无条件**适用）

> 判据不再看「有没有别的会话在并行」，而是：**只要要执行任务，就必须新开 worktree；禁止在 `main` 上直接修改任何文件；完成后必须合并回 `main`。**

| # | 强制条款 | 一句话判据 | 违规长相 |
|---|---|---|---|
| **M1** | **每个任务开一个新 worktree** | 任务产出的**每一个**文件改动都在 `..\.worktrees\<会话名>\` 内 | 在项目根（主工作树，`main`）里编辑、生成、`dotnet build` 后手改产物 |
| **M2** | **禁止在 `main` 上直接修改** | `git status` 在主工作树里**没有**本任务的未提交改动；`main` 上只有合并提交 | 「先改完再补 worktree」「README 小改不用开」——**一律违规**（取消旧 §12「唯一会话可不开」的豁免） |
| **M3** | **完成后必须合并回 `main`** | 走完 `commit → finish → 合并后在 main 复跑 build + 守卫 → stop` | 任务做完停在 `session/*` 分支上不合；直接 `merge` 而跳过收工自检 |
| **M4** | **绝不直接 `git add -A` 提交** | 提交前逐个 `git add <本任务文件>`；`ARCHITECTURE.md` / `AGENTS.md` / `data/excel/*.xlsx` 等共享文件**只提交 diff 属于本任务的部分** | 在脏的主工作树里 `git add -A`，把别的任务的改动、`*.xlsx.bak*`、临时调试目录一起提交 |
| **M5** | **总架构师会话同样受约束** | 本文档的 `M1~M4` 也包括**总架构师自己**：规则类改动同样先进 worktree | 总架构师在主工作树里改 `AGENTS.md` / `docs/rules/**` 后直接提交 |

> 与旧 §12 的关系：旧条目「我是唯一的会话，可以不开吗 → 可以」**作废**，按 M2 执行。
> M4 是新增的硬纪律：`session.ps1 commit` 内部是 `git add -A`，在「主工作树长期有别人半成品」的条件下会**把别人的改动卷进本任务提交**——所以共享文件必须先人工暂存。

```text
① 开会话      .\tools\session.ps1 start -Name <会话名>
② 隔离作业    cd 进 worktree；本会话所有读写、编译、守卫都在 worktree 内
③ 收工自检    dotnet build + .\tools\check_architecture.ps1 + 覆盖更新 ARCHITECTURE.md（规则3）
④ 提交        .\tools\session.ps1 commit -Name <会话名> -Message "..."
⑤ 合并回线    .\tools\session.ps1 finish -Name <会话名>
⑥ 清理        .\tools\session.ps1 stop -Name <会话名>
```

### 2.1 第 ① 步：开会话

```powershell
# 在项目根（主工作树）执行，当前分支应为 main
.\tools\session.ps1 start -Name skill-takeover
```

- 等价于 `git worktree add ..\.worktrees\skill-takeover -b session\skill-takeover main`。
- 脚本打印出 worktree 的**绝对路径**，并提示「cd 进去再开工」。
- 会话名已存在时脚本**拒绝创建**并提示改用 `status` / `list` 查看 —— 不要为了「重来一次」而换名字，那只会留下两条僵尸分支。

### 2.2 第 ② 步：隔离作业（最容易违反的一步）

```powershell
cd E:\workspace\myspace\godot_game\.worktrees\skill-takeover
# 之后所有动作都在这里：读写文件、dotnet build、check_architecture.ps1、更新 ARCHITECTURE.md
```

**硬性要求：**

1. 本会话的**所有文件改动**只能落在该 worktree 目录内；
2. 本会话的**所有构建/守卫/测试命令**只能在 worktree 内执行（在该目录下跑，产物才会落在该 worktree 的 `obj/` `bin/` `out/`）；
3. 不要 `cd` 回项目根去改文件。项目根 = `main` = **别人的领地**（见 §3）。

> **怎么自查**：随时执行 `.\tools\session.ps1 status -Name <会话名>`，看「变更文件列表」是否都在预期范围内；
> 或者 `.\tools\session.ps1 list` 一眼看完所有会话的分支、路径与脏/净状态。

### 2.3 第 ③ 步：收工自检

在 worktree 内按 [07-definition-of-done.md](07-definition-of-done.md) 的 6 步检查表逐项打勾，其中 ⑤、⑥ 两步可执行：

```powershell
# 当前目录 = worktree 根
dotnet build
.\tools\check_architecture.ps1          # 退出码必须为 0
# ⑥ 覆盖更新 ARCHITECTURE.md 正文 + 变更日志 + 文件头时间戳（见 07 文档 §5）
```

### 2.4 第 ④ 步：提交

```powershell
.\tools\session.ps1 commit -Name skill-takeover -Message "skill: 夺舍切换目标与冷却门控"
```

- 等价于在该 worktree 内 `git add -A` + `git commit -m "..."`。
- **约定提交信息前缀 = 会话名**（`<会话名>: <一句话>`），合并时一眼能看出这串提交属于哪个会话。
- 没有改动时脚本会提示「无可提交」，不是错误。
- **⚠️ 共享文件纪律（M4）**：提交前先 `git status` 核对清单，`ARCHITECTURE.md` / `AGENTS.md` / `data/excel/*.xlsx` / `config/architecture-rules.json` 这几类**多人共写**的文件，逐个 `git add -p`（或先整文件 `git add` 再 `git diff --cached` 复核）确认只含本任务内容；
  不确定属于本任务的改动**不要提交**，留在工作区并告知其归属会话。

### 2.5 第 ⑤ 步：合并回主线

```powershell
# 在项目根执行（或在任意目录执行，脚本会自己定位主工作树）
.\tools\session.ps1 finish -Name skill-takeover
```

脚本做的四件事（缺一不可）：

1. 在会话 worktree 内**再检查一次**有无未提交改动，有则停下并提示先 `commit`；
2. 回到**主工作树**并检出 `main`；
3. `git merge --no-ff session/skill-takeover` —— **`--no-ff` 是硬要求**，保证每次合并留下一个合并提交，历史里能看清「哪个会话、在什么时候、合进了哪些提交」；
4. 合并成功后提示可以 `stop` 清理（`-NoDelete` 或提示保留 worktree 供复查）。

> 本工程 `main` 只做快进/合并，不在 `main` 上直接开发（§3）。

### 2.6 第 ⑥ 步：清理

```powershell
.\tools\session.ps1 stop -Name skill-takeover
```

- 等价于 `git worktree remove <path>` + `git branch -d session/skill-takeover`；
- **未合并到 `main` 的分支会被拒绝删除**（工具用 `git branch -d` 而**不是** `-D`，并用合并检查兜底），脚本会提示「请先 finish」；
- 想要强制删除，必须由人明确拍板并手工执行 —— 工具本身不提供 `-Force`。

---

## 3. 主线纪律（`main` 的规矩）

| # | 纪律 | 理由 |
|---|---|---|
| 1 | **`main` 永远只接受合并，不在 `main` 上直接开发** | 主工作树可能一直开着编辑器；在 `main` 上直接改，等于把自己变成「公共缓冲区」，冲突无处隔离 |
| 2 | **合并前先 rebase 到最新 `main`**（`session.ps1 finish` 会先做一次 `git rebase main`，冲突则停下给指引） | 让会话分支始终保持线性、可快进的历史；避免合并提交里混进「main 前进了一半」的纠缠 |
| 3 | **一次 `finish` 只合并一件事** | 一次合并几十个混合主题的提交，review 与回滚都不可能 |
| 4 | **合并完成后立即在 `main` 上跑一遍 `check_architecture.ps1` 与 `dotnet build`** | 两个分支各自绿，不代表合起来绿（典型：两边都新增了同名接口 / 都改了同一处白名单） |
| 5 | **不许对 `main` 做 `git push --force` / `reset --hard` / `rebase` 改写已合并历史** | `main` 是所有人唯一的公共参照，改写它 = 让所有在途会话的 rebase 全部重来 |

> ⚠️ 合并完成后的 green check 属于**最后合并者**的责任，不是「反正 CI 会跑」。本机没有 CI，`main` 的健康由合并者担保。

---

## 4. 冲突处理

### 4.1 判定与归属

| 情形 | 处理 |
|---|---|
| 同一文件被两个会话改了**不同区域** | git 自动合并；合并后在 `main` 跑 build + 守卫确认 |
| 同一文件被两个会话改了**同一区域** | 冲突。**后合并者负责 rebase 解决**（谁后 `finish`，谁解决） |
| 冲突 ≤ 3 处且是**文本级**（改注释、改格式、相邻行） | 后合并者按语义解决，并在合并提交信息里写明「与 session/X 的冲突点」 |
| 冲突 > 3 处，或触及**值语义 / 接口签名 / 目录分层** | **停下，拆任务重做**：把冲突区域重新切分成两个互不相交的任务，各自重开 worktree |
| 同一个「事实」在两个分支被写成两种形状（例如两种 StatPackage 字段命名） | 视为设计冲突，**找 Lead 仲裁**，仲裁前不许合并 |

### 4.2 硬红线：不许「随便选一边」

冲突解决时的**唯一合法依据**是「哪一边符合 [03-abstract-first.md](03-abstract-first.md) 与 [06-extensibility.md](06-extensibility.md)」，而不是「哪一边打得快」。

```text
❌ 随手 Accept Current / Accept Incoming / 把两段都留着用注释括起来
❌ 为了过编译，把两边的实现都塞进同一个方法（先让它能跑）
✅ 先读两边的提交意图 → 判断哪个抽象更对 → 保留对的那一份，把另一份的语义吸收进去 → 在合并提交里写清理由
```

### 4.3 冲突解决的操作口径

```powershell
# finish 停下来之后，按脚本打印的指引：
cd E:\workspace\myspace\godot_game\.worktrees\<会话名>
git status                        # 看冲突文件
# 逐个解决（只在 worktree 内解决，不要在 main 上解决）
git add <已解决的文件>
git rebase --continue             # 若还有下一个冲突则重复
# 回到 finish 完成合并
.\tools\session.ps1 finish -Name <会话名>
# 实在想放弃这次 rebase：
git rebase --abort
```

---

## 5. 目录隔离（worktree 必须建在项目根之外）

### 5.1 规定

- 所有会话 worktree 统一放在 **项目根的兄弟目录**：`E:\workspace\myspace\godot_game\.worktrees\<会话名>\`；
- **严禁**把 worktree 建在项目根内部（例如 `biu-biu-origin\.worktrees\<name>\`）。

### 5.2 为什么必须在外面

| 建在外面（✅） | 建在里面（❌） |
|---|---|
| Godot 只把 `biu-biu-origin` 当作项目根 | Godot 会把两个工作树都当成「项目」，分别生成 `.godot/` 导入缓存 |
| 只有一个 `.godot/`，导入缓存唯一 | 两份 `.godot/` 互相覆盖、互相判脏，导入记录错乱 |
| 架构守卫扫描根唯一 | 守卫会在 `main` 里扫到另一个会话的**半成品源码**，误报一片 |
| `.gitignore` 里无需特判 | 必须额外忽略，且极易漏掉 |

> 这就是为什么 `config/architecture-rules.json` 的 `ignore` 里要登记 `.worktrees/**` 与 `out/**`：
> 前者是「就算有人把会话目录建到里面，守卫也不会去扫它」的**第二道保险**；后者是各 worktree 的临时/中间产物目录。

### 5.3 工具已经替你处理

`session.ps1 start` 永远把 worktree 建到 `..\.worktrees\<name>`，不接受自定义路径参数 —— **把「可能建错」的入口直接删掉**。

---

## 6. 共享但不可并发的东西

以下东西在多个 worktree 之间**语义上是同一份**，但物理上是多份检出。它们各自有独立的合并策略：

| 对象 | 性质 | 合并策略 |
|---|---|---|
| `data/excel/*.xlsx` | 二进制，git 无法逐行合并 | **字段并集优先，不用「行覆盖」**：冲突时以「新字段 / 新 Sheet / 新数据行取并集，同一单元格取谁以真源负责人的意见为准」为原则；两人改同一 Sheet 的同一区域 → 拆任务 |
| `docs/architecture/ARCHITECTURE.md` | 多方并发写的高冲突文件 | **不按行覆盖**，见 §7 特例 |
| `native/build/` | CMake 构建目录 | **不入库**，每个 worktree 各自 build 各自一份 |
| `addons/*/bin/` | 插件二进制产物 | 同上，各自生成；不跨 worktree 复用 |
| `data/generated/` | Excel 生成物 | **不入库**，各自从 `data/excel/` 重新生成 |
| `obj/` `bin/` `out/` `.godot/` | 编译/导入/临时产物 | **不入库、不共享**，各自生成（`.godot/` 每个 worktree 一份，这是隔离的代价，也是隔离的意义） |
| `config/architecture-rules.json` | 放行白名单（**只追加**） | 冲突时取**并集**；新增放行仍要同步登记 AGENTS.md §4 白名单表 |

> **判据**：一个文件如果是「人编辑的真源」，它需要合并策略；如果是「工具生成的产物」，它一律各 worktree 自产自销、绝不入库。

---

## 7. 特例：`ARCHITECTURE.md` 的合并（不按行覆盖）

`docs/architecture/ARCHITECTURE.md` 是**所有会话都必须改**的文件（规则3 的收工动作），因此它是天然的高冲突点。规定如下：

```text
1. 冲突时【不按行覆盖】—— 不许 Accept Current / Accept Incoming 一刀切
2. 保留双方章节：两个会话各自新增/修改的章节都要在，不许为了"干净"删掉对方那一节
3. 变更日志【两边都保留】：双方各追加的行都留在 §11 表格里，并按日期排序
   （同一天的多行，按合并进来的先后顺序排，谁都不删）
4. 最后合并者负责收尾：
   ├─ 刷新文件头「最后更新」为今天（GS006 依赖它）
   ├─ 跑 .\tools\check_architecture.ps1 确认 GS006 通过（不 stale）
   └─ 在合并提交信息里注明「ARCHITECTURE.md 采用保留双方章节方式合并」
```

**为什么不能按行覆盖**：ARCHITECTURE.md 的每一节都是**独立的下游读者入口**。按行覆盖等于「谁最后写谁说了算」，而另一节可能正是下一个 agent 要读的施工图。一旦被冲掉，下一个任务就会照着过期地图施工 —— 这是规则3 明确列为红线的事故（见 07 文档 §1.3）。

> 换句话说：**代码冲突可以选一边（因为语义互斥），文档章节冲突只能并集（因为语义互补）。**

---

## 8. 反例清单（命中任意一条 = 违规）

| # | 反例 | 为什么错 | 正确做法 |
|---|---|---|---|
| 1 | **在 `main` 上直接改代码**（在主工作树里动手，不开 worktree） | `main` 变成多方共用缓冲区，冲突无处隔离；编辑器里的导入缓存会与别人的改动打架 | `session.ps1 start` → `cd` 进 worktree → 再动手 |
| 2 | **两个会话共用一个工作目录**（第二个人直接 `cd` 进第一个人的 worktree，或都留在项目根） | 一方整篇写入会无声覆盖另一方，且没有任何报错 | 每个会话一个独立 worktree |
| 3 | **worktree 建在项目根内部**（`biu-biu-origin\.worktrees\`） | Godot 把两个树都当项目，`.godot/` 导入缓存互相覆盖 | 统一建在 `..\.worktrees\<name>`（工具已固化） |
| 4 | **在 `main` 上开发完再「补一个 worktree」走流程** | 流程沦为形式；`main` 已经被污染，后续所有会话的 rebase 都基于脏基线 | 开工第一件事就是开会话，**顺序不可颠倒** |
| 5 | **用 `git branch -D` / `worktree remove --force` 清理未合并的会话** | 未合并的提交就此丢失，且无法从日志复现 | 先 `finish`；真不要了就回去把改动 `git stash` / 另行备份再说 |
| 6 | **冲突时 Accept 一边了事**，不看语义 | 会把另一方正确的抽象冲掉，且事后无人知晓 | 按 §4.2，以抽象正确性为准解决 |
| 7 | **共享构建产物**（把主工作树的 `obj/`、`bin/`、`native/build/` 软链或复制进 worktree） | 两套编译参数/时间戳互相污染，产物不可信 | 各 worktree 自己 build（见 §6） |
| 8 | **忘了 `stop`**，堆一堆 `.worktrees\*` 与已合并的 `session/*` 分支 | 目录越堆越多，`git worktree list` 变成垃圾堆，新人无法分辨哪个是活跃会话 | `finish` 后立刻 `stop`；每周 `session.ps1 list` 看一遍 |
| 9 | **做完任务不合回 `main`**（停在 `session/*` 上） | 主线拿不到成果，下个会话的基线是过期的；越拖冲突越大 | 收工即 `commit` → `finish` → 复跑 → `stop`（M3） |
| 10 | **共享文件用 `git add -A` 一把提交**（`ARCHITECTURE.md` 里混进别人正在写的章节、`*.xlsx` 混进 `*.bak`） | 把不属于本任务的改动绑进本任务提交，回滚/审查都失真；`*.bak*` 还会被当数据读 | 提交前核对 `git status`，共享文件用 `git add -p` / `git diff --cached` 复核（M4） |
| 11 | **总架构师在主工作树里直接改规则文档** | 规则特权一旦放开，所有人都会照着「大改动才开 worktree」给自己找台阶 | 规则类改动同样走 worktree（M5） |

---

## 9. 会话名命名规范

```text
<功能或模块>-<可选序号或短后缀>
```

| 规则 | 说明 |
|---|---|
| 字符集 | 只允许小写英文字母、数字、`-`；**不用中文、空格、下划线、大写** |
| 长度 | ≤ 24 字符（它同时是目录名、分支名后缀） |
| 语义 | 一眼看出「这次会话在干什么」，而不是 `s1`/`test`/`tmp` |
| **一个会话一个名字** | 会话结束就 `stop`；**不要复用旧名字**开新会话（历史会混在一起） |

**正例：**

| 会话名 | 分支 | 干什么 |
|---|---|---|
| `skill-takeover` | `session/skill-takeover` | 技能装配与夺舍 |
| `render-outline` | `session/render-outline` | 描边渲染 pass |
| `hitstop-tuning` | `session/hitstop-tuning` | 打击停顿调参 |
| `excel-schema-v2` | `session/excel-schema-v2` | Excel 表结构升级 |
| `wave-spawner` | `session/wave-spawner` | 波次刷怪系统 |

**反例：** `Session1`（大写）、`fix bug`（空格）、`测试`（中文）、`a`（无意义）、`skill-takeover` 跑第二次（复用旧名）。

---

## 10. 工具速查（`tools/session.ps1`）

| 子命令 | 作用 | 关键行为 |
|---|---|---|
| `list` | 列出所有会话 | 主工作树 + 各 worktree 的分支、路径、脏净、最近提交 |
| `start -Name X [-Base main]` | 开会话 | `git worktree add ..\.worktrees\X -b session\X <base>`；已存在则拒绝；打印绝对路径 + 「cd 进去再开工」 |
| `status -Name X` | 查单个会话状态 | 分支 / 路径 / HEAD / 是否已并入基线 / 领先落后条数 / 变更文件列表；**主工作树（main）上有未提交改动**时给出警告（那类改动不属于任何会话分支） |
| `commit -Name X -Message "..."` | 在会话内提交 | `git add -A` + `git commit -m`；无改动则提示 |
| `finish -Name X [-NoDelete]` | 提交并合并回 main | 检查脏 → 回到主工作树 → `rebase` 到最新 main → `merge --no-ff`；冲突则停下并打印指引；`-NoDelete` 保留 worktree |
| `stop -Name X` | 清理会话 | `worktree remove` + `branch -d`；**未合并则拒绝**并提示先 `finish` |

> `-NoDelete`（`finish` 时用）的作用只是**跳过并入后立即提示清理**、保留 worktree 供复查；清理仍由 `stop` 完成 —— 二者不是替代关系。

**前置条件与失败姿态：**

- 需要 `git`（本机 2.55.0）与**已初始化的 git 仓库**（项目根下的 `.git`）。仓库尚未初始化时，脚本会**打印「请先完成 git init」并以退出码 2 正常退出**（不是异常崩溃）；
- 任何删除类操作都**不做强制覆盖**：未合并分支、脏 worktree 一律拒绝并给出下一步命令；工具**没有** `-Force` 开关；
- `finish` 遇到 rebase 冲突时**停下**（退出码 7），此刻两个分支都没有被脚本改动；按打印的解决指引处理完再跑一次 `finish` 即可；
- 脚本是 **PowerShell 5.1 + 纯 ASCII 源码**（中文文案由 Unicode 码点表构造，与 `tools/env.ps1` 同一规范），不要在脚本里粘贴明文中文（见 §11.3）。

**退出码约定（便于脚本化 / agent 判断）：**

| 退出码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 参数错误（缺子命令、会话名不合法、commit 缺 `-Message`） |
| 2 | 不是 git 仓库（或找不到主工作树）→ 等 `git init` |
| 3 | 找不到 `git` 可执行文件 |
| 4 | 会话/worktree/基线不存在，或会话已存在 |
| 5 | git 命令执行失败 |
| 6 | `finish` 前会话仍有未提交改动 |
| 7 | `finish` 的 rebase 冲突，已停下（两个分支均未改动） |
| 8 | `finish` 的 merge 失败（已尝试 `merge --abort` 回滚） |
| 9 | `stop` 拒绝：分支未合并，或 worktree 有未提交改动 |
| 10 | `stop` 移除 worktree / 删除分支失败（通常是占用） |

> 脚本上方的「当前不是 git 仓库」提示属于**预期行为**：`git init` 之后 `start` 才能工作。

---

## 11. 与既有体系的衔接

### 11.1 与规则3（07 完成定义）的衔接

- 07 文档的 6 步检查表是**单个任务**的完成判据；
- 本规则的 6 步流程是**会话级**的外壳：③ 收工自检 = 跑完 07 的检查表；
- 因此 07 文档 §8.1 的读取协议与 §9 的自检清单**在 worktree 内执行**；
- 07 文档已追加「合并到主线」一环，与本规则 §2 第 ⑤ 步一一对应。

### 11.2 与架构守卫的衔接

- `session.ps1 finish` **不自动**跑 `check_architecture.ps1`：守卫只应存在于「收工自检」（会写基线的 `-UpdateBaseline` 绝不能在无人判断时自动跑）；
- 合并完成后由**最后合并者**在 `main` 上手动跑一次守卫；
- `config/architecture-rules.json` 的 `ignore` 增加了 `.worktrees/**` 与 `out/**`（只追加，不动原有条目）。

### 11.3 与 `tools/env.ps1` 编码规范的衔接

本工程所有 PowerShell 脚本必须遵守同一编码规范，理由见 [../../tools/env.ps1](../../tools/env.ps1) 文件头：

> **纯 ASCII 源码 + 中文文案用 Unicode 码点表构造 + 输出用显式 UTF-8 字节。**
> PS 5.1 解析无 BOM 脚本时按 ANSI（本机 GBK）解码，明文 UTF-8 中文会被拆坏，某些字节甚至吞掉换行导致「Unexpected token '}'」这类解析崩溃。

`tools/session.ps1` 照此办理，**不要**「顺手优化」成明文中文。

---

## 12. 常见问题

| 问题 | 回答 |
|---|---|
| 我就改一个小文件，也要开 worktree 吗？ | **要。** 按 §2.0 的 M1，触发条件是「要执行任务」而不是「改动大小」或「是否并发」。 |
| 我是唯一的会话，可以不开吗？ | **不可以**（§2.0 M2，原「可以」已作废）。连改一行 README 也先在 worktree 里改、提交、合并。 |
| 总架构师改 `AGENTS.md` / `docs/rules/**` 也要开 worktree 吗？ | **要**（§2.0 M5）。规则类改动同样走 `start → commit → finish`，并在合并后的 `main` 上复跑守卫。 |
| `start` 报「请先完成 git init」怎么办？ | 说明项目根还不是 git 仓库。由 Lead 完成 `git init` + 首次提交后，`start` 即可用。 |
| 在 worktree 里跑 `dotnet build` 会不会太慢 / 太占地方？ | 会各有一份 `obj/` `bin/`，这是隔离的代价；它们都在 `.gitignore` 里、随时可删。 |
| 我能同时在两个 worktree 里开 Godot 编辑器吗？ | 可以，但它们会各自维护 `.godot/`；建议只有主工作树常开编辑器，会话内用命令行（`dotnet build` / 守卫）自检。 |
| `finish` 合并后 `main` 编译挂了怎么办？ | 属于**最后合并者**的责任：在 `main` 上修（或 revert 这次合并提交），修完再让后续会话 rebase。见 §3 纪律 4。 |
| 两个会话改了同一个 UI 组件怎么办？ | 按 §4：>3 处或触及接口/分层 → 拆任务；否则后合并者按抽象正确性解决。 |
| 会话做了一半要中断，怎么处理？ | `commit`（提交「WIP:」前缀也行）→ 直接走开。worktree 保留，下次 `cd` 回来继续；不要 `stop`，否则未合并提交会被拒删。 |
| 可以直接对 session 分支 `git push` 吗？ | **本规则不涉及远端**。本机 GitHub 不可达，远端目标未定，所有操作仅限本地；远端策略待 Lead 决策后再另立条目。 |
| `stop` 提示「未合并拒绝删除」，但我确实不想要了？ | 先把 worktree 里的改动备份出去，或 `git checkout -b` 另存一条分支，再决定是否强制删除 —— **不要**用 `-D` 掩盖「丢提交」这件事。 |

---

## 13. 变更日志

| 日期 | 版本 | 任务 | 变更摘要 | 影响文件 |
|---|---|---|---|---|
| 2026-11-04 | v1 | 规则8：多会话并行 worktree 工作流 | 建立规则8 正文：worktree 选型理由（vs clone / 切分支）、6 步标准流程、主线纪律、冲突处理与「不许随便选一边」红线、ARCHITECTURE.md 保留双方章节的合并特例、目录隔离、共享不可并发对象表、8 条反例、会话名规范；配套 `tools/session.ps1` 六子命令、`07` 追加合并环节与检查项、`architecture-rules.json` 追加 `.worktrees/**` 与 `out/**` | `docs/rules/08-parallel-sessions.md`、`tools/session.ps1`、`docs/rules/07-definition-of-done.md`、`config/architecture-rules.json` |
| 2026-11-11 | v1.2 | 总架构师裁定：worktree **强制化** | 新增 §2.0「强制口径 M1~M5」：① 只要执行任务就必须新开 worktree（取消「唯一会话可不开」豁免）；② 禁止在 `main` 上直接修改；③ 完成后必须合并回 `main`；④ 共享文件禁止 `git add -A`，必须逐文件暂存；⑤ 总架构师自身同样受约束。配合修订：§2.4 追加共享文件暂存纪律、§8 新增反例 9~11、§12 常见问题两条改写并新增一条、§5 路径示例盘符订正为 `E:` | `docs/rules/08-parallel-sessions.md` |
| 2026-11-04 | v1.1 | 规则8：多会话并行 worktree 工作流（工具定稿对齐） | 按 `tools/session.ps1` 实测行为订正 §10：补全**退出码约定表**（0/1/2/3/4/5/6/7/8/9/10，便于 agent 判断）、明确前置条件（需已 `git init`，未初始化时退出码 2 + 提示而非崩溃）、工具**无 `-Force`** 的安全口径、`finish` 冲突时两分支均未被改动、`-NoDelete` 与 `stop` 的关系；修正 `status` 行为描述（改为「分支 HEAD / 是否已并入基线 / 领先落后条数 / 变更文件列表」，并说明未提交改动不属于分支）| `docs/rules/08-parallel-sessions.md` |
