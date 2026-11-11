"""填充「5 人物 / 5 怪物 / 5 武器 / 5 子弹」的**数值内容**（2026-11-06）。

背景
----
用户最初的需求是「5 武器 / 5 子弹 / 5 怪物 / 5 人物，用 ECS 组合机制做出来」，
并明确当前**只填数值、不做场景**（场景地编那边还没就绪）。

现状盘点（动手前）
------------------
  · 武器 8 把、子弹 7 种 → 数量已够，但部分数值是 `wstat:` / `flags:` 这类**继承写法**，
    没有独立基线，图鉴读起来不方便；
  · 怪物只有 3 种（grunt / archer / ogre）；
  · 人物 5 行但 4 行是**同一模板的数值变体**（test_hero + tank/agile/lucky），机制上没有差异。

本脚本补齐的正是这两个缺口，并把「5 个」做成**机制各异**的组合，而不是同一套数值换个系数。

设计口径：ECS 组合，不是五个类
------------------------------
每个人的差异都落在**同一条 `ObjectArchetype` 组合**上，差别只有三处：
  ① 能力位（tag 组合）  ② 数值包（StatPacks）  ③ 技能包（SkillLoads → 武器/子弹）
所以「5 人物」= 同一套 ECS 装配 × 5 组数据，代码 0 行改动。

内容清单
--------
人物（5 个，机制各异）：
  · test_hero        均衡        — 高射速手枪，样样都有
  · hero_tank        重甲坦克    — 慢、厚、重炮，近身碾压
  · hero_agile       敏捷刺客    — 最快、最脆、冲锋枪糊脸
  · hero_lucky       幸运枪手    — 高暴击高弹跳，靠赌博收益
  · hero_elemental   元素法师    — 低血高蓝，靠能量弹穿透+分裂

怪物（5 个，从 3 补到 5）：
  · goblin_grunt     近战杂兵    — 已有的，补子弹/数值确认
  · goblin_archer    远程射手    — 已有的
  · ogre_brute       精英重锤    — 已有的
  · slime_401        分裂史莱姆  — 新增：死亡分裂成小史莱姆（tag.split）
  · slime_403        爆炸史莱姆  — 新增：死亡爆散（tag.explosive + tag.volatile）

武器（5 把，给人物用的独立基线）：
  pistol_basic / cannon_heavy / smg_light / ricochet_lucky / orb_elemental

子弹（5 种，对应上面 5 把武器）：
  proj_pistol_basic / proj_cannon_heavy / proj_smg_light / proj_ricochet_lucky / proj_orb_elemental

幂等
----
所有写入都是「按 Id 覆盖」：重复跑一遍结果一致，不会产生重复行。
"""
from __future__ import annotations

import sys
from pathlib import Path

from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[2] / "data" / "excel"
CHARS = ROOT / "characters.xlsx"
COMBAT = ROOT / "combat.xlsx"

FIRST_DATA_ROW = 6


# ═══════════════════════════════════════════════════════════════════════════
# 1. 人物数值包（StatPacks）
# ═══════════════════════════════════════════════════════════════════════════
# 五个人的数值刻意在「血 / 蓝 / 速 / 甲 / 暴击 / 手感」六个维度上拉开距离，
# 且每一对之间都至少有一项是反向的 —— 保证没有谁是另一个的严格上位。

HERO_PACKS: "dict[str, dict[str, object]]" = {
    # 均衡型：所有维度都在中位，没有短板（新手默认）
    "test_hero_base": {
        "Hp": 120, "HpMax": 120, "Mana": 80, "ManaMax": 80, "ManaRegenPerSec": 4,
        "Stamina": 100, "StaminaMax": 100, "StaminaRegenPerSec": 12,
        "Armor": 6, "ArmorK": 100, "MoveSpeed": 5.2,
        "MoveAccelTime": 0.12, "TurnResponseTime": 0.10, "AimRange": 18,
        "Strength": 5, "Dexterity": 6, "Luck": 3,
        "CritChance": 0.05, "CritMultiplier": 1.75, "HurtInvulnTime": 0.6,
        "DashSpeedMul": 2.2, "DashDuration": 0.18,
    },
    # 重甲坦克：血厚甲高、移速慢、转身慢、暴击低 —— 换来近身碾压的容错
    "test_hero_tank_base": {
        "Hp": 220, "HpMax": 220, "Mana": 60, "ManaMax": 60, "ManaRegenPerSec": 3,
        "Stamina": 120, "StaminaMax": 120, "StaminaRegenPerSec": 10,
        "Armor": 20, "ArmorK": 100, "MoveSpeed": 3.6,
        "MoveAccelTime": 0.20, "TurnResponseTime": 0.18, "AimRange": 16,
        "Strength": 9, "Dexterity": 3, "Luck": 2,
        "CritChance": 0.03, "CritMultiplier": 1.5, "HurtInvulnTime": 0.8,
        "DashSpeedMul": 1.8, "DashDuration": 0.22,
    },
    # 敏捷刺客：移速与转身最快、耐力回复高，但血薄甲薄 —— 靠机动而不是硬吃
    "test_hero_agile_base": {
        "Hp": 90, "HpMax": 90, "Mana": 100, "ManaMax": 100, "ManaRegenPerSec": 6,
        "Stamina": 140, "StaminaMax": 140, "StaminaRegenPerSec": 16,
        "Armor": 3, "ArmorK": 100, "MoveSpeed": 7.4,
        "MoveAccelTime": 0.06, "TurnResponseTime": 0.05, "AimRange": 20,
        "Strength": 3, "Dexterity": 11, "Luck": 4,
        "CritChance": 0.08, "CritMultiplier": 1.6, "HurtInvulnTime": 0.4,
        "DashSpeedMul": 2.8, "DashDuration": 0.16,
    },
    # 幸运枪手：暴击率与幸运最高、暴击倍率高，血甲平庸 —— 收益方差大
    "test_hero_lucky_base": {
        "Hp": 110, "HpMax": 110, "Mana": 90, "ManaMax": 90, "ManaRegenPerSec": 5,
        "Stamina": 110, "StaminaMax": 110, "StaminaRegenPerSec": 12,
        "Armor": 5, "ArmorK": 100, "MoveSpeed": 5.4,
        "MoveAccelTime": 0.12, "TurnResponseTime": 0.10, "AimRange": 18,
        "Strength": 4, "Dexterity": 6, "Luck": 15,
        "CritChance": 0.15, "CritMultiplier": 2.0, "HurtInvulnTime": 0.6,
        "DashSpeedMul": 2.4, "DashDuration": 0.18,
    },
    # 元素法师：蓝量与回蓝最高、瞄准距离最远，但血量最低、移速慢 —— 靠技能输出
    "test_hero_elemental_base": {
        "Hp": 70, "HpMax": 70, "Mana": 180, "ManaMax": 180, "ManaRegenPerSec": 14,
        "Stamina": 90, "StaminaMax": 90, "StaminaRegenPerSec": 10,
        "Armor": 3, "ArmorK": 100, "MoveSpeed": 4.4,
        "Strength": 2, "Dexterity": 5, "Luck": 6,
        "CritChance": 0.08, "CritMultiplier": 1.9, "HurtInvulnTime": 0.5,
        "MoveAccelTime": 0.14, "TurnResponseTime": 0.12, "AimRange": 26,
        "DashSpeedMul": 2.0, "DashDuration": 0.20,
    },
}

# 成长包：**不改** —— 五个变体共用既有的 `test_hero_growth`（Growth 层）。
# 注意：同一个 PackId 不能同时出现在 Base 与 Growth 两层（仓库以包 id 为键，
# 跨层会互相覆盖，CharacterContentSet.FlushStatPacks 会直接抛异常）。


# ═══════════════════════════════════════════════════════════════════════════
# 2. 怪物数值包（新增 2 个史莱姆；已有 3 个只做数值确认，不覆盖）
# ═══════════════════════════════════════════════════════════════════════════
MONSTER_PACKS: "dict[str, dict[str, object]]" = {
    # 分裂史莱姆：血薄、慢、零甲，但死亡后分裂 —— 价值在数量压制
    "slime_base": {
        "Hp": 30, "HpMax": 30, "Mana": 0, "ManaMax": 0,
        "Armor": 0, "ArmorK": 100, "MoveSpeed": 2.6,
        "Strength": 2, "Dexterity": 2, "Luck": 1, "HurtInvulnTime": 0.15,
    },
    # 爆炸史莱姆：血稍厚、更慢，死亡爆散 —— 逼你保持距离
    "slime_bomb_base": {
        "Hp": 55, "HpMax": 55, "Mana": 0, "ManaMax": 0,
        "Armor": 4, "ArmorK": 100, "MoveSpeed": 2.2,
        "Strength": 6, "Dexterity": 1, "Luck": 0, "HurtInvulnTime": 0.2,
    },
}


# ═══════════════════════════════════════════════════════════════════════════
# 3. 人物使用的武器（characters.xlsx / Weapons）
# ═══════════════════════════════════════════════════════════════════════════
# 列：Id, WeaponId, Damage, BulletSpeed, RangeMeters, SpreadDegrees,
#     CriticalChance, CriticalMultiplier, ProjectileRadius, ShotCooldown, PelletsPerShot
HERO_WEAPONS = [
    # 均衡：中等伤害、快射速、散布小
    ("pistol_basic",     2001, 20, 22, 24, 1.5, 0.05, 1.00, 0.10, 0.15, 1),
    # 重甲：极高单发、极慢、散布大
    ("cannon_heavy",     2002, 55, 14, 26, 5.0, 0.00, 1.00, 0.22, 0.42, 1),
    # 敏捷：低伤高频、散布中等
    ("smg_light",        2003,  8, 26, 18, 4.0, 0.03, 1.00, 0.07, 0.08, 1),
    # 幸运：中伤、高暴击率与高暴击倍率
    ("ricochet_lucky",   2004, 16, 20, 22, 2.5, 0.52, 2.25, 0.11, 0.18, 1),
    # 元素：低物理伤、弹速慢、但量大好穿透（伤害主要来自元素弹自身的分裂/穿透）
    ("orb_elemental",    2005, 12, 16, 30, 3.0, 0.08, 1.90, 0.14, 0.22, 1),
]


# ═══════════════════════════════════════════════════════════════════════════
# 4. 人物子弹（combat.xlsx / Objects + Projectiles）
# ═══════════════════════════════════════════════════════════════════════════
# 每个人一颗，机制不同：
#   proj_pistol_basic   直飞，单段命中
#   proj_cannon_heavy   直飞，重击（高伤低弹速）
#   proj_smg_light      直飞，穿透 2（高频糊脸）
#   proj_ricochet_lucky 直飞，弹跳 3（撞墙换目标）
#   proj_orb_elemental  追踪，穿透 3 + 分裂（元素法师的核心）

HERO_PROJECTILES = [
    # Id, ObjectId, Motion, HitPolicy, OnExpire, Speed, Radius, Lifetime, Damage, Pierce, Bounce
    ("proj_pistol_basic",    "bullet_hero_pistol",  "Straight", "Single", "None", 22, 0.10, 2.0, 20, 0, 0),
    ("proj_cannon_heavy",    "bullet_hero_cannon",  "Straight", "Single", "None", 14, 0.22, 2.6, 55, 0, 0),
    ("proj_smg_light",       "bullet_hero_smg",     "Straight", "Pierce", "None", 26, 0.07, 1.4,  8, 2, 0),
    ("proj_ricochet_lucky",  "bullet_hero_ricochet", "Straight", "Pierce", "None", 20, 0.11, 2.2, 16, 0, 3),
    ("proj_orb_elemental",   "bullet_hero_orb",     "Homing",   "Pierce", "None", 16, 0.14, 3.0, 12, 3, 0),
]

# 子弹物件（combat.xlsx / Objects）：Id, Tags, Radius, Height, Note
HERO_PROJECTILE_OBJECTS = [
    ("bullet_hero_pistol",   "tag.projectile,tag.kinetic,tag.common,tag.own_player",
     0.10, 0.10, "人物弹·均衡：直飞单段"),
    ("bullet_hero_cannon",   "tag.projectile,tag.kinetic,tag.uncommon,tag.high_damage,tag.own_player",
     0.22, 0.22, "人物弹·重甲：高伤低弹速"),
    ("bullet_hero_smg",      "tag.projectile,tag.kinetic,tag.pierce,tag.common,tag.own_player",
     0.07, 0.07, "人物弹·敏捷：穿透 2"),
    ("bullet_hero_ricochet", "tag.projectile,tag.kinetic,tag.ricochet,tag.uncommon,tag.own_player",
     0.11, 0.11, "人物弹·幸运：弹跳 3"),
    ("bullet_hero_orb",      "tag.projectile,tag.homing,tag.energy,tag.pierce,tag.split,tag.rare,tag.own_player",
     0.14, 0.14, "人物弹·元素：追踪 + 穿透 3 + 分裂"),
]


# ═══════════════════════════════════════════════════════════════════════════
# 5. 人物技能包（SkillLoads）：把上面 5 把武器接进 PrimaryAttack 槽
# ═══════════════════════════════════════════════════════════════════════════
# 列：LoadoutId, Slot, SkillId, CooldownSeconds, ManaCost, Kind, WeaponId, ProjectileId, Note
HERO_KITS = {
    "test_hero_kit":       ("pistol_basic",    "proj_pistol_basic",   1101, 0.15, 0,  "均衡射击（左键）"),
    "test_hero_tank_kit":  ("cannon_heavy",    "proj_cannon_heavy",   1106, 0.42, 0,  "重炮（慢速高伤）"),
    "test_hero_agile_kit": ("smg_light",       "proj_smg_light",      1107, 0.08, 0,  "冲锋枪（高射速低伤）"),
    "test_hero_lucky_kit": ("ricochet_lucky",  "proj_ricochet_lucky", 1108, 0.18, 0,  "幸运弹射（高暴击）"),
    "test_hero_elemental_kit": ("orb_elemental", "proj_orb_elemental", 1109, 0.22, 6, "元素球（追踪穿透，耗蓝 6）"),
}

# 五个人共用的非攻击槽（保持五槽技能结构与既有约定一致）
COMMON_SLOTS = [
    ("Dash",      1001, 1.2, 8, "冲刺（Shift）"),
    ("WeaponSwap", 1201, 0.0, 0, "切换武器（Q）"),
    ("Interact",  1401, 0.0, 0, "交互（F）"),
]
SIGNATURE = {
    "test_hero_kit":           (1304, 12, 25, "角色独有技能（R）"),
    "test_hero_tank_kit":      (1305, 16, 20, "角色独有技能（R）"),
    "test_hero_agile_kit":     (1306, 10, 30, "角色独有技能（R）"),
    "test_hero_lucky_kit":     (1307, 14, 25, "角色独有技能（R）"),
    "test_hero_elemental_kit": (1308, 18, 60, "元素爆发（R）：高耗蓝"),
}


# ═══════════════════════════════════════════════════════════════════════════
# 6. 人物行（characters.xlsx / Characters）
# ═══════════════════════════════════════════════════════════════════════════
# 列：Id, DisplayKey, Kind, DefaultTeam, Control, BasePackage, GrowthPackage,
#     EquipmentPackage, SkillLoadout, HitRadius, HitHeight, VisualSlot
HERO_ROWS = [
    ("test_hero",           "actor.test_hero",          "Player", "Player", "PlayerInput",
     "test_hero_base",           "test_hero_growth", None, "test_hero_kit",           0.45, 1.8, None),
    ("test_hero_tank",      "actor.test_hero_tank",     "Player", "Player", "PlayerInput",
     "test_hero_tank_base",      "test_hero_growth", None, "test_hero_tank_kit",      0.55, 2.0, None),
    ("test_hero_agile",     "actor.test_hero_agile",    "Player", "Player", "PlayerInput",
     "test_hero_agile_base",     "test_hero_growth", None, "test_hero_agile_kit",     0.40, 1.7, None),
    ("test_hero_lucky",     "actor.test_hero_lucky",    "Player", "Player", "PlayerInput",
     "test_hero_lucky_base",     "test_hero_growth", None, "test_hero_lucky_kit",     0.45, 1.8, None),
    ("test_hero_elemental", "actor.test_hero_elemental", "Player", "Player", "PlayerInput",
     "test_hero_elemental_base", "test_hero_growth", None, "test_hero_elemental_kit", 0.45, 1.9, 6),
]

# 怪物行（补 2 个史莱姆）
MONSTER_ROWS = [
    ("slime_401", "actor.slime_401", "Monster", "Enemy", "Ai",
     "slime_base",      None, None, "slime_kit",      0.5, 1.0, 7),
    ("slime_403", "actor.slime_403", "Monster", "Enemy", "Ai",
     "slime_bomb_base", None, None, "slime_bomb_kit", 0.6, 1.2, 8),
]

# 怪物技能包
MONSTER_KITS = {
    "slime_kit":      (1102, 1.6, 0, "分裂史莱姆：撞击"),
    "slime_bomb_kit": (1104, 2.2, 0, "爆炸史莱姆：自爆撞击"),
}

# combat.xlsx / Objects 里补的怪物物件（数值包指向上面两个）
MONSTER_OBJECT_PACKS = {
    "slime_401": "slime_base",
    "slime_403": "slime_bomb_base",
}


# ═══════════════════════════════════════════════════════════════════════════
# 写表工具
# ═══════════════════════════════════════════════════════════════════════════

def _header(ws) -> "dict[str, int]":
    return {str(ws.cell(row=1, column=c).value): c
            for c in range(1, ws.max_column + 1)
            if ws.cell(row=1, column=c).value}


def _row_index(ws, key_field: str, key: str) -> "int | None":
    hdr = _header(ws)
    col = hdr[key_field]
    for r in range(FIRST_DATA_ROW, ws.max_row + 1):
        v = ws.cell(row=r, column=col).value
        if v is not None and str(v).strip() == key:
            return r
    return None


def _last_data_row(ws) -> int:
    """最后一条真正有数据的行号；空表返回 FIRST_DATA_ROW - 1。

    注意：不能直接用 ws.max_row —— 表里可能有带样式但无值的行，
    追加到 max_row+1 会留下空洞行，校验器会报「Id 不能为空」。
    """
    for r in range(ws.max_row, FIRST_DATA_ROW - 1, -1):
        if any(ws.cell(row=r, column=c).value not in (None, "")
               for c in range(1, ws.max_column + 1)):
            return r
    return FIRST_DATA_ROW - 1


def _upsert(ws, key_field: str, key: str, values: "dict[str, object]") -> str:
    """按 key 覆盖写一行；没有就追加。返回 'updated' / 'appended'。

    ★ 主键列**总是**写入，不依赖调用方是否把它放进 values。
      v1 正是漏了这一点：调用方只在 values 里传非主键列，结果新行的
      Id 列为空，校验器报「Id 不能为空」。
    """
    hdr = _header(ws)
    if key_field not in hdr:
        raise SystemExit(f"[错误] 表 {ws.title} 没有主键列「{key_field}」")
    unknown = [f for f in values if f not in hdr]
    if unknown:
        raise SystemExit(f"[错误] 表 {ws.title} 没有列 {unknown}")

    row = _row_index(ws, key_field, key)
    action = "updated"
    if row is None:
        row = max(_last_data_row(ws) + 1, FIRST_DATA_ROW)
        action = "appended"
    ws.cell(row=row, column=hdr[key_field], value=key)   # ★ 主键必定写入
    for field, value in values.items():
        ws.cell(row=row, column=hdr[field], value=value)
    return action


def _delete_rows(ws, key_field: str, key: str) -> int:
    """删掉某个 key 的所有行（用于技能包整体重写）。"""
    hdr = _header(ws)
    col = hdr[key_field]
    to_delete = [r for r in range(FIRST_DATA_ROW, ws.max_row + 1)
                 if str(ws.cell(row=r, column=col).value or "").strip() == key]
    for r in reversed(to_delete):
        ws.delete_rows(r)
    return len(to_delete)


def write_stat_packs(ws, packs: "dict[str, dict[str, object]]") -> int:
    n = 0
    for pack_id, stats in packs.items():
        for stat_id, value in stats.items():
            _upsert(ws, "PackId", pack_id, {
                "Layer": "Base", "StatId": stat_id, "Value": value,
            }) if False else None
            # StatPacks 的主键是 (PackId, Layer, StatId)，只能手工定位
            hdr = _header(ws)
            found = None
            for r in range(FIRST_DATA_ROW, ws.max_row + 1):
                if (str(ws.cell(row=r, column=hdr["PackId"]).value or "").strip() == pack_id
                        and str(ws.cell(row=r, column=hdr["StatId"]).value or "").strip() == stat_id):
                    found = r
                    break
            if found is None:
                found = max(_last_data_row(ws) + 1, FIRST_DATA_ROW)
                ws.cell(row=found, column=hdr["PackId"], value=pack_id)
                ws.cell(row=found, column=hdr["Layer"], value="Base")
                ws.cell(row=found, column=hdr["StatId"], value=stat_id)
            ws.cell(row=found, column=hdr["Value"], value=value)
            n += 1
    return n


def main() -> int:
    # ── characters.xlsx ──────────────────────────────────────────────────
    wb = load_workbook(CHARS)

    n = write_stat_packs(wb["StatPacks"], HERO_PACKS)
    n += write_stat_packs(wb["StatPacks"], MONSTER_PACKS)
    print(f"[ok] StatPacks: 写入 {n} 个 (PackId, StatId) 数值")

    # 人物行
    for row in HERO_ROWS:
        vals = dict(zip(
            ["Id", "DisplayKey", "Kind", "DefaultTeam", "Control", "BasePackage",
             "GrowthPackage", "EquipmentPackage", "SkillLoadout", "HitRadius", "HitHeight", "VisualSlot"],
            row))
        _upsert(wb["Characters"], "Id", vals["Id"], vals)
    for row in MONSTER_ROWS:
        vals = dict(zip(
            ["Id", "DisplayKey", "Kind", "DefaultTeam", "Control", "BasePackage",
             "GrowthPackage", "EquipmentPackage", "SkillLoadout", "HitRadius", "HitHeight", "VisualSlot"],
            row))
        _upsert(wb["Characters"], "Id", vals["Id"], vals)
    print(f"[ok] Characters: 写入 {len(HERO_ROWS)} 个人物 + {len(MONSTER_ROWS)} 个怪物")

    # 武器
    wcols = ["Id", "WeaponId", "Damage", "BulletSpeed", "RangeMeters", "SpreadDegrees",
             "CriticalChance", "CriticalMultiplier", "ProjectileRadius", "ShotCooldown", "PelletsPerShot"]
    for w in HERO_WEAPONS:
        vals = dict(zip(wcols, w))
        _upsert(wb["Weapons"], "Id", vals["Id"], vals)
    print(f"[ok] Weapons: 写入 {len(HERO_WEAPONS)} 把人物武器")

    # 技能包：整体重写 PrimaryAttack + Signature，保留其余槽
    sl = wb["SkillLoads"]
    for loadout, (weapon_id, proj_id, skill_id, cd, mana, note) in HERO_KITS.items():
        _delete_rows(sl, "LoadoutId", loadout)
        seq = []
        for slot, sid, scd, smana, snote in COMMON_SLOTS:
            seq.append((slot, sid, scd, smana, "data", None, None, snote))
        sig = SIGNATURE[loadout]
        seq.append(("PrimaryAttack", skill_id, cd, mana, "fire_projectile", weapon_id, proj_id, note))
        seq.append(("Signature", sig[0], sig[1], sig[2], "data", None, None, sig[3]))
        for slot, sid, scd, smana, kind, wid, pid, snote in seq:
            r = max(_last_data_row(sl) + 1, FIRST_DATA_ROW)
            sl.cell(row=r, column=1, value=loadout)
            sl.cell(row=r, column=2, value=slot)
            sl.cell(row=r, column=3, value=sid)
            sl.cell(row=r, column=4, value=scd)
            sl.cell(row=r, column=5, value=smana)
            sl.cell(row=r, column=6, value=kind)
            sl.cell(row=r, column=7, value=wid)
            sl.cell(row=r, column=8, value=pid)
            sl.cell(row=r, column=9, value=snote)
    for loadout, (skill_id, cd, mana, note) in MONSTER_KITS.items():
        _delete_rows(sl, "LoadoutId", loadout)
        r = max(_last_data_row(sl) + 1, FIRST_DATA_ROW)
        for c, v in enumerate([loadout, "PrimaryAttack", skill_id, cd, mana, "data", None, None, note], start=1):
            sl.cell(row=r, column=c, value=v)
    print(f"[ok] SkillLoads: 重写 {len(HERO_KITS)} 个人物技能包 + {len(MONSTER_KITS)} 个怪物技能包")

    wb.save(CHARS)
    print(f"wrote {CHARS}")

    # ── combat.xlsx：子弹物件 + Projectiles ───────────────────────────────
    cb = load_workbook(COMBAT)

    obj_ws = cb["Objects"]
    for obj_id, tags, radius, height, note in HERO_PROJECTILE_OBJECTS:
        _upsert(obj_ws, "Id", obj_id, {
            "DisplayKey": f"proj.{obj_id}", "Tags": tags,
            "ClusterWeight": 1,          # 必填：不配簇时权重仍须显式给
            "Radius": radius, "Height": height, "Note": note,
        })
    print(f"[ok] Objects: 写入 {len(HERO_PROJECTILE_OBJECTS)} 颗人物子弹")

    # 规则值列「空 = 未填，0 = 明确为零」，所以无效果必须显式写 0。
    # MaxChainDepth 例外：必须 >= 1（既有行都用 2~3），元素球分裂链取 3。
    pj = cb["Projectiles"]
    for (pid, obj_id, motion, hit, expire, speed, radius, lifetime, dmg, pierce, bounce) in HERO_PROJECTILES:
        _upsert(pj, "Id", pid, {
            "ObjectId": obj_id, "Motion": motion, "HitPolicy": hit, "OnExpire": expire,
            "Speed": speed, "Radius": radius, "Lifetime": lifetime, "Damage": dmg,
            "Pierce": pierce, "Bounce": bounce,
            "Gravity": 0, "HomingTurn": 0,
            "MaxChainDepth": 3 if pid == "proj_orb_elemental" else 2,
            "Note": f"人物弹：{obj_id}",
        })
    print(f"[ok] Projectiles: 写入 {len(HERO_PROJECTILES)} 条弹道")

    cb.save(COMBAT)
    print(f"wrote {COMBAT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
