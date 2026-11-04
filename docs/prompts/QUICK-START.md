# 任务：<一句话说清要做什么>

> 复制本模板 → 替换尖括号内容 → 整段发给 AI。
> 模板全文与多个填好的范例见 docs/prompts/task-prompt-template.md

## 目标
- 要做的事：<例：新增一种子弹样式「追踪导弹」>
- 验收时能看到什么：<例：场上有 2 种子弹外观，行为不同，数值都来自 Excel>

## 涉及系统（勾选）
- [ ] 数值数据    - [ ] 战斗逻辑   - [ ] 技能
- [ ] 渲染        - [ ] 特效       - [ ] UI
- [ ] 工具链

## 数据来源（规则4：不许写死在代码里）
- 要改/新增哪张表：<例：combat.xlsx 的 Projectiles Sheet>
- 新增哪些字段：<例：HomingStrength, TurnRate>

## 抽象与复用（规则5/7：先想抽象）
- 已有接口里能复用的：<例：IProjectile, StatPackage>
- 预计新增的接口：<没有就写「无，复用现有」>
- 未来可能出现的变体（至少写 3 个）：<例：追踪/散射/延迟引爆>

## 完成后必须做到（Definition of Done）
1. `dotnet build` 零错误
2. `.\tools\check_architecture.ps1` 零 error（exit 0）
3. `docs/architecture/ARCHITECTURE.md` 就地更新 + 变更日志加一行
4. 回报时说明：改了哪几个文件、新增/复用了哪些抽象

## 禁止事项
- 除 UI 层外禁止 GDScript（规则1）
- 禁止硬编码游戏数值（规则4）
- 禁止复制粘贴式的重复实现（规则5）
- 禁止另建 ARCHITECTURE_v2.md（规则3）
