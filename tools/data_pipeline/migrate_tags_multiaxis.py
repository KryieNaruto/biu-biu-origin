"""一次性迁移：把 combat.xlsx 的 Tags / Objects 迁到「多轴标签」形态（2026-11-06）。

为什么是独立脚本而不是重跑 gen_combat_xlsx.py：
  · xlsx 生成出来以后就是**唯一真源**，重跑生成器会把其他人手改过的内容冲掉；
  · 所以「加一列 / 加一批标签」必须用**就地迁移**：读原表 → 只在目标位置增列/追加行 → 写回，
    其余单元格原样保留。

本脚本做的事（幂等，跑第二遍会报「已是新结构」并退出）：
  1. Tags 表在第 3 列插入 `Family`（枚举 enum:TagFamily），并给每个 tag 标上它所属的轴；
  2. Tags 表追加 23 个「多轴标签」（稀有度 / 族裔 / 战斗角色 / 攻击形态 / 特效表现），
     TagIndex 从 17 起连续编号，BitMask 全部 -1（只做索引，不改熔炉配方行为）；
  3. Objects 表的 `Tags` 列按轴重写（人物/怪物：Kind + Race + Rarity + Role + 能力；武器/子弹/特效同理）。

只动这两处；Weapons / Projectiles / StatPacks / _Meta 与 characters.xlsx 一个字节都不碰。
"""
from __future__ import annotations

import sys
from copy import copy
from pathlib import Path

from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[2] / "data" / "excel"
COMBAT = ROOT / "combat.xlsx"

MARKER_ROW = 5
FIRST_DATA_ROW = 6

# ── 1. 每个已有 tag 的「轴」（按 TagId 给，不按行号，重排也不会错）────────────────
FAMILY_OF_EXISTING = {
    "tag.actor": "Kind",
    "tag.weapon": "Kind",
    "tag.projectile": "Kind",
    "tag.pistol": "WeaponKind",
    "tag.rifle": "WeaponKind",
    "tag.bow": "WeaponKind",
    "tag.melee": "WeaponKind",
    "tag.homing": "ProjectileKind",
    "tag.energy": "Element",
    "tag.kinetic": "Element",
    "tag.explosive": "Element",
    "tag.pierce": "Ability",
    "tag.bounce": "Ability",
    "tag.split": "Ability",
    "tag.summon": "Ability",
    "tag.chain": "Ability",
    "tag.volatile": "Trigger",
}

# ── 2. 新增标签：TagId, Family, Category, Group, Note（BitMask 一律 -1）──────────
NEW_TAGS = [
    # 稀有度轴（互斥）
    ("tag.common", "Rarity", "Stat", "Rarity", "普通：权重 ≥ 2 的大众货"),
    ("tag.uncommon", "Rarity", "Stat", "Rarity", "精良：权重 1~2"),
    ("tag.rare", "Rarity", "Stat", "Rarity", "稀有：权重 0.5~1，需要构筑才能拿到"),
    ("tag.legendary", "Rarity", "Stat", "Rarity", "传说：权重 < 0.5，或表里唯一的匠人级"),
    # 族裔轴（互斥）
    ("tag.humanoid", "Race", "Stat", "Race", "人形"),
    ("tag.goblin", "Race", "Stat", "Race", "哥布林"),
    ("tag.slime", "Race", "Stat", "Race", "史莱姆"),
    ("tag.ogre", "Race", "Stat", "Race", "食人魔"),
    # 战斗角色轴（互斥）
    ("tag.hero", "Role", "Stat", "Role", "可选英雄（图鉴人物页的入口）"),
    ("tag.grunt", "Role", "Stat", "Role", "小怪：近身杂兵"),
    ("tag.ranged", "Role", "Stat", "Role", "远程：会拉距离输出的敌人"),
    ("tag.caster", "Role", "Stat", "Role", "施法者：靠技能/召唤，血量低"),
    ("tag.elite", "Role", "Stat", "Role", "精英：高血高甲，常带独有技能"),
    ("tag.assassin", "Role", "Stat", "Role", "机动型：高速 / 高会心"),
    # 攻击形态（同属 Ability 轴，可叠加）
    ("tag.projectile_fired", "Ability", "Ability", "Ability", "弹道攻击：靠飞出弹体打人"),
    ("tag.short_range", "Ability", "Ability", "Ability", "近身范围：贴脸才能打"),
    ("tag.multi_shot", "Ability", "Ability", "Ability", "连发：射速高、单发弱"),
    ("tag.charge", "Ability", "Ability", "Ability", "蓄力：按住越久越强"),
    ("tag.ricochet", "Ability", "Ability", "Ability", "弹射：命中后折向新目标"),
    ("tag.high_damage", "Ability", "Ability", "Ability", "重击：单发伤害显著高于同费"),
    # 特效表现轴（互斥）
    ("tag.muzzle", "Effect", "Stat", "Effect", "枪口：开火瞬间"),
    ("tag.hit", "Effect", "Stat", "Effect", "命中：打在受击盒上"),
    ("tag.burst", "Effect", "Stat", "Effect", "爆散：范围爆开"),
]

# ── 3. 每个物体的多轴标签（按 Id 给全量新值；不在表里的行原样保留）──────────────
OBJECT_TAGS = {
    # 人物 / 怪物
    "player_hero": "tag.actor,tag.hero,tag.humanoid,tag.common,tag.ranged,tag.projectile_fired",
    "goblin_grunt": "tag.actor,tag.goblin,tag.humanoid,tag.common,tag.grunt,tag.short_range",
    "goblin_archer": "tag.actor,tag.bow,tag.goblin,tag.humanoid,tag.uncommon,tag.ranged,tag.projectile_fired",
    "goblin_shaman": "tag.actor,tag.energy,tag.goblin,tag.humanoid,tag.rare,tag.caster",
    "ogre_brute": "tag.actor,tag.melee,tag.ogre,tag.uncommon,tag.elite,tag.short_range,tag.high_damage",
    "slime_401": "tag.actor,tag.split,tag.slime,tag.common,tag.grunt",
    "slime_402": "tag.actor,tag.split,tag.explosive,tag.slime,tag.rare,tag.grunt",
    "slime_403": "tag.actor,tag.split,tag.volatile,tag.slime,tag.legendary,tag.assassin",
    # 武器
    "pistol_101": "tag.weapon,tag.pistol,tag.kinetic,tag.common,tag.projectile_fired",
    "smg_102": "tag.weapon,tag.pistol,tag.kinetic,tag.pierce,tag.uncommon,tag.multi_shot,tag.projectile_fired",
    "revolver_103": "tag.weapon,tag.pistol,tag.kinetic,tag.rare,tag.high_damage,tag.projectile_fired",
    "bow_201": "tag.weapon,tag.bow,tag.kinetic,tag.homing,tag.uncommon,tag.charge,tag.projectile_fired",
    "rifle_202": "tag.weapon,tag.rifle,tag.kinetic,tag.common,tag.multi_shot,tag.projectile_fired",
    "sniper_203": "tag.weapon,tag.rifle,tag.kinetic,tag.pierce,tag.rare,tag.charge,tag.high_damage,"
                  "tag.projectile_fired",
    "club_401": "tag.weapon,tag.melee,tag.kinetic,tag.uncommon,tag.short_range,tag.high_damage",
    "orb_501": "tag.weapon,tag.energy,tag.summon,tag.legendary",
    # 子弹
    "bullet_101": "tag.projectile,tag.kinetic,tag.common",
    "bullet_102": "tag.projectile,tag.kinetic,tag.pierce,tag.bounce,tag.uncommon",
    "bullet_103": "tag.projectile,tag.kinetic,tag.pierce,tag.rare,tag.high_damage",
    "arrow_202": "tag.projectile,tag.kinetic,tag.pierce,tag.legendary",
    "bullet_201": "tag.projectile,tag.homing,tag.kinetic,tag.uncommon",
    "bullet_301": "tag.projectile,tag.explosive,tag.summon,tag.split,tag.rare",
    "bullet_501": "tag.projectile,tag.energy,tag.ricochet,tag.uncommon",
    # 特效
    "fx_muzzle_small": "tag.muzzle,tag.kinetic,tag.common",
    "fx_hit_spark": "tag.hit,tag.kinetic,tag.common",
    "fx_bomb_burst": "tag.burst,tag.kinetic,tag.uncommon",
}

FAMILY_COLUMN = ("Family", "enum:TagFamily", "标签轴",
                 "★ 标签的「轴」：强化/图鉴/筛选按轴取范围；同轴互斥的轴（Kind/Element/Rarity/Race/Role 等）"
                 "每个物体最多命中一个，Ability 轴可叠加")


def _data_rows(ws):
    """返回 [(excel_row, {字段名: 列号})]（跳过表头与 #data）。"""
    header = {}
    for c in range(1, (ws.max_column or 0) + 1):
        name = ws.cell(row=1, column=c).value
        if name:
            header[str(name)] = c
    rows = []
    for r in range(FIRST_DATA_ROW, (ws.max_row or 0) + 1):
        if ws.cell(row=r, column=1).value in (None, ""):
            continue
        rows.append((r, header))
    return rows


def migrate_tags(ws) -> None:
    header = [ws.cell(row=1, column=c).value for c in range(1, ws.max_column + 1)]
    if "Family" in header:
        raise SystemExit("[skip] Tags 表已经有 Family 列，已是新结构")

    # ① 在第 3 列插入 Family（openpyxl 的 insert_cols 会保持其余数据右移）
    ws.insert_cols(3)
    for r in range(1, 5):
        value = FAMILY_COLUMN[r - 1]
        cell = ws.cell(row=r, column=3, value=value)
        src = ws.cell(row=r, column=2)
        cell.fill = copy(src.fill)
        cell.font = copy(src.font)
        cell.alignment = copy(src.alignment)

    # ② 给已有 tag 行填 Family
    tag_col = 1  # TagId 仍是第 1 列
    index_col = 2
    family_col = 3
    filled = 0
    last_index = -1
    for r in range(FIRST_DATA_ROW, ws.max_row + 1):
        tag_id = ws.cell(row=r, column=tag_col).value
        if not tag_id:
            continue
        tag_id = str(tag_id).strip()
        if tag_id not in FAMILY_OF_EXISTING:
            raise SystemExit(f"[错误] Tags 第 {r} 行的「{tag_id}」没有登记轴，请先在脚本里补 FAMILY_OF_EXISTING")
        ws.cell(row=r, column=family_col, value=FAMILY_OF_EXISTING[tag_id])
        filled += 1
        last_index = int(ws.cell(row=r, column=index_col).value)

    # ③ 追加新标签（TagIndex 连续递增，BitMask = -1）
    r = ws.max_row + 1
    for offset, (tag_id, family, category, group, note) in enumerate(NEW_TAGS):
        ws.cell(row=r + offset, column=1, value=tag_id)
        ws.cell(row=r + offset, column=2, value=last_index + 1 + offset)
        ws.cell(row=r + offset, column=3, value=family)
        ws.cell(row=r + offset, column=4, value=category)
        ws.cell(row=r + offset, column=5, value=group)
        ws.cell(row=r + offset, column=6, value=-1)
        ws.cell(row=r + offset, column=7, value=note)
    print(f"[ok] Tags: 插入 Family 列，{filled} 个已有 tag 标轴，追加 {len(NEW_TAGS)} 个新标签"
          f"（TagIndex {last_index + 1}..{last_index + len(NEW_TAGS)}）")


def migrate_objects(ws) -> None:
    updated = 0
    missing = []
    for r in range(FIRST_DATA_ROW, ws.max_row + 1):
        obj_id = ws.cell(row=r, column=1).value
        if not obj_id:
            continue
        obj_id = str(obj_id).strip()
        if obj_id not in OBJECT_TAGS:
            missing.append(obj_id)
            continue
        ws.cell(row=r, column=3, value=OBJECT_TAGS[obj_id])  # C 列 = Tags
        updated += 1
    if missing:
        raise SystemExit(f"[错误] Objects 里这些行没有登记标签（表里多出来的物件？）：{missing}")
    print(f"[ok] Objects: 重写 {updated} 行的多轴标签")


def main() -> int:
    wb = load_workbook(COMBAT)
    migrate_tags(wb["Tags"])
    migrate_objects(wb["Objects"])
    wb.save(COMBAT)
    print(f"wrote {COMBAT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
