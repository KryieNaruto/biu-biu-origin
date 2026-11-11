"""迁移：把标签拆成「逻辑功能层」与「标识层」两层（2026-11-06）。

背景
----
上一轮给所有内容加了「轴」（Family）做范围索引。但两个用途被混在同一张表里：

  · **逻辑功能层**（Tier = Logic）：标签决定玩法行为 —— 是个人物、是动能伤害、
    能穿透、是稀有度。代码会*消费*它：能力位、熔炉配方字、元素抗性、掉落权重。
  · **标识层**（Tier = Identity）：标签不做任何判定，只回答「这属于谁 / 这吸引谁」。
    道具吸附、磁铁、拾取归属、分组高亮、主题皮肤都靠它。代码*不消费*它，只做匹配。

为什么必须分开
--------------
标识标签是可以**大量增加**的（每加一件道具、一个主题就要加几个），而逻辑标签必须**克制**
（每加一个都可能要改规则）。混在一起时，看到 Tags 表有 80 行没法判断「哪些能随便加」。
分层之后，`Tier` 列就是这条纪律：**Logic 行加之前先想规则；Identity 行随便加。**

层与轴不是一回事
----------------
  · 轴（Family）回答「这个标签属于哪一类维度」。同轴互斥的规则由轴决定。
  · 层（Tier）回答「这个标签能不能影响玩法」。由层决定它有没有资格被规则读到。

一个标签既有层也有轴：`tag.kinetic` 是 Logic 层的 Element 轴；`tag.loot.ammo` 是 Identity 层的 Loot 轴。

幂等
----
跑第二遍会看到 Tier 列已存在 → 打印「已是新结构」并退出，不改任何单元格。
"""
from __future__ import annotations

import sys
from copy import copy
from pathlib import Path

from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[2] / "data" / "excel"
COMBAT = ROOT / "combat.xlsx"

FIRST_DATA_ROW = 6

# ── 1. 已有 40 个标签全是「逻辑功能」层 ──────────────────────────────────────
LOGIC_TAGS = {
    "tag.actor", "tag.weapon", "tag.projectile", "tag.pistol", "tag.rifle", "tag.bow", "tag.melee",
    "tag.homing", "tag.energy", "tag.kinetic", "tag.explosive", "tag.pierce", "tag.bounce", "tag.split",
    "tag.summon", "tag.chain", "tag.volatile", "tag.common", "tag.uncommon", "tag.rare", "tag.legendary",
    "tag.humanoid", "tag.goblin", "tag.slime", "tag.ogre", "tag.hero", "tag.grunt", "tag.ranged",
    "tag.caster", "tag.elite", "tag.assassin", "tag.projectile_fired", "tag.short_range",
    "tag.multi_shot", "tag.charge", "tag.ricochet", "tag.high_damage", "tag.muzzle", "tag.hit", "tag.burst",
}

# ── 2. 新增「标识」层标签：TagId, Family, Group, Note ────────────────────────
#    约束：BitMask 一律 -1（不进配方字）；Group 一律 None（不产生任何能力位）；Tier = Identity。
IDENTITY_TAGS = [
    # Loot 轴（互斥）：这件东西在拾取时归哪一类 —— 道具吸附半径、磁铁筛选、自动拾取白名单
    ("tag.loot.ammo", "Loot", "拾取：弹药物"), 
    ("tag.loot.health", "Loot", "拾取：回血物"),
    ("tag.loot.mana", "Loot", "拾取：回蓝物"),
    ("tag.loot.coin", "Loot", "拾取：金币（钱袋/掉落钱）"),
    ("tag.loot.relic", "Loot", "拾取：遗物（永久加成，肉鸽核心构筑物）"),
    ("tag.loot.key", "Loot", "拾取：钥匙（开门用）"),
    # Attract 轴（可叠加）：被哪些磁场吸引。「道具互相吸引」就靠这一轴做交集
    ("tag.attract.magnet", "Attract", "被磁铁吸引：磁铁道具按这一轴取范围"),
    ("tag.attract.weapon", "Attract", "被「武器吸附」吸引：枪/弹类掉落可被特定道具吸过来"),
    ("tag.attract.exp", "Attract", "被经验吸附吸引：经验球专用的磁场"),
    # Faction 轴（互斥）：归属方。用于高亮同阵营道具、掉落归属结算、UI 分组
    ("tag.own.player", "Faction", "玩家所属：玩家掉落物 / 玩家召唤物"),
    ("tag.own.enemy", "Faction", "敌方所属：怪物掉落物"),
    ("tag.own.neutral", "Faction", "中立所属：场景道具 / 可破坏物"),
    # Theme 轴（可叠加）：主题分组。给道具/房间/敌人打主题，用于章节化随机与皮肤
    ("tag.theme.forest", "Theme", "主题：森林章节"),
    ("tag.theme.dungeon", "Theme", "主题：地牢章节"),
    ("tag.theme.forge", "Theme", "主题：熔炉章节（与 Aura 联动）"),
    # Aura 轴（可叠加）：光环 / 共鸣标记。与熔炉链、元素共鸣这类「同标记互相加强」的机制挂钩
    ("tag.aura.fire", "Aura", "光环：火（与 tag.explosive 共鸣）"),
    ("tag.aura.ice", "Aura", "光环：冰（减速/冻结类）"),
    ("tag.aura.shock", "Aura", "光环：电（连锁类，与 tag.chain 共鸣）"),
    # Craft 轴（可叠加）：craft 只表示「这个物品在合成时属于哪类原料」
    ("tag.craft.metal", "Craft", "原料：金属"),
    ("tag.craft.wood", "Craft", "原料：木材"),
    ("tag.craft.gem", "Craft", "原料：宝石"),
    ("tag.craft.essence", "Craft", "原料：精华/液体"),
]

TIER_COLUMN = (
    "Tier", "enum:TagTier", "标签层",
    "★ Logic=逻辑功能层（决定玩法行为，代码会消费，加之前先想规则）；"
    "Identity=标识层（不做任何判定，只用于归属/吸引/主题匹配，可以随便加）",
)


def migrate_tags(ws) -> None:
    header = [ws.cell(row=1, column=c).value for c in range(1, ws.max_column + 1)]
    if "Tier" in header:
        raise SystemExit("[skip] Tags 表已经有 Tier 列，已是新结构")

    # ① 在第 3 列插入 Tier（Family 之前在「TagIndex」后）
    ws.insert_cols(3)
    for r in range(1, 5):
        cell = ws.cell(row=r, column=3, value=TIER_COLUMN[r - 1])
        src = ws.cell(row=r, column=2)
        cell.fill = copy(src.fill)
        cell.font = copy(src.font)
        cell.alignment = copy(src.alignment)

    # 插列后布局：1 TagId | 2 TagIndex | 3 Tier | 4 Family | 5 Category | 6 Group | 7 BitMask | 8 Note
    filled = 0
    last_index = -1
    for r in range(FIRST_DATA_ROW, ws.max_row + 1):
        tag_id = ws.cell(row=r, column=1).value
        if not tag_id:
            continue
        tag_id = str(tag_id).strip()
        if tag_id not in LOGIC_TAGS:
            raise SystemExit(f"[错误] Tags 第 {r} 行的「{tag_id}」没有登记层，请先在脚本里补 LOGIC_TAGS")
        ws.cell(row=r, column=3, value="Logic")
        filled += 1
        last_index = int(ws.cell(row=r, column=2).value)

    # ② 追加标识层标签
    r = ws.max_row + 1
    for offset, (tag_id, family, note) in enumerate(IDENTITY_TAGS):
        row = r + offset
        ws.cell(row=row, column=1, value=tag_id)
        ws.cell(row=row, column=2, value=last_index + 1 + offset)
        ws.cell(row=row, column=3, value="Identity")
        ws.cell(row=row, column=4, value=family)
        ws.cell(row=row, column=5, value="Stat")
        ws.cell(row=row, column=6, value="None")   # ★ 不产生任何能力位
        ws.cell(row=row, column=7, value=-1)       # ★ 不进配方字
        ws.cell(row=row, column=8, value=note)

    print(f"[ok] Tags: 插入 Tier 列，{filled} 个已有标签标为 Logic，追加 {len(IDENTITY_TAGS)} 个 Identity 标签"
          f"（TagIndex {last_index + 1}..{last_index + len(IDENTITY_TAGS)}）")


def main() -> int:
    wb = load_workbook(COMBAT)
    migrate_tags(wb["Tags"])
    wb.save(COMBAT)
    print(f"wrote {COMBAT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
