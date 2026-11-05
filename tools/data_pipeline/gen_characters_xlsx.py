"""生成 data/excel/characters.xlsx —— 角色抽象的数据真源（v1）。

为什么用脚本生成 Excel 而不是让策划手动建：表头 4 行（字段名/类型/中文名/备注）是本项目的
硬约定（ARCHITECTURE.md §4.2），手搓容易漏；脚本生成保证第一次就合规，重复运行幂等。
生成之后 **Excel 就是唯一真源**，改数值请直接改 xlsx（或本脚本后重跑），不要改 data/generated/。

Sheets:
  Characters  角色定义：类型 + 默认阵营 + 控制方案 + 数值包组合 + 技能包
  StatPacks   数值包：每行 = 某个包在某一层里的一项数值（(PackId, Layer, StatId) 唯一）
  SkillLoads  技能包：每行 = 某个包在某槽位装哪个技能
"""
from __future__ import annotations

import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Font, PatternFill

ROOT = Path(__file__).resolve().parents[2] / "data" / "excel"
OUT = ROOT / "characters.xlsx"

SCHEMA_VERSION = 1

HEADER_FILL = PatternFill("solid", fgColor="1F4E78")
NOTE_FILL = PatternFill("solid", fgColor="DDEBF7")
TYPE_FILL = PatternFill("solid", fgColor="F2F2F2")


def sheet(wb, title: str, rows: list[tuple[str, str, str, str]], data: list[list]):
    """写一张表：4 行表头（字段名/类型/中文名/备注）+ `#data` 标记行 + 数据行。

    ★ 2026-11-05 修复：`rows` 是**每列一条**的四元组列表 → 表头是 4 行 × N 列。
    旧实现把四元组当行 append，结果是「表头横躺、列数 = 4」，任何按四行表头解析的消费者都会读错。
    """
    ws = wb.create_sheet(title)
    for r in range(1, 5):
        for c, column in enumerate(rows, start=1):
            cell = ws.cell(row=r, column=c, value=column[r - 1])
            cell.fill = HEADER_FILL if r == 1 else (TYPE_FILL if r == 2 else NOTE_FILL)
            cell.font = Font(bold=(r == 1), color="FFFFFF" if r == 1 else "000000", size=10)
            cell.alignment = Alignment(wrap_text=True, vertical="top")
    ws.cell(row=5, column=1, value="#data")  # 数据起始标记行，便于人眼阅读
    for i, row in enumerate(data):
        for c, value in enumerate(row, start=1):
            ws.cell(row=6 + i, column=c, value=value)
    for c in range(1, len(rows) + 1):
        ws.column_dimensions[ws.cell(row=1, column=c).column_letter].width = 22
    ws.freeze_panes = "A6"
    return ws


def main() -> int:
    wb = Workbook()
    wb.remove(wb.active)

    sheet(
        wb,
        "Characters",
        [
            ("Id", "string", "角色id", "小写下划线，全局唯一；= ActorDefinition.Id"),
            ("DisplayKey", "string", "显示名key", "本地化 key，不写中文"),
            ("Kind", "enum:ActorKind", "角色类型", "Player/Monster/Elite/Boss/Summon/Destructible"),
            ("DefaultTeam", "enum:TeamId", "默认阵营", "怪物与角色同源，靠这一列区分敌我"),
            ("Control", "enum:ControlScheme", "控制方案", "PlayerInput/Ai/Script；夺舍只改这个"),
            ("BasePackage", "string", "基础数值包", "必填，指向 StatPacks.Base"),
            ("GrowthPackage", "string", "成长数值包", "可空"),
            ("EquipmentPackage", "string", "装备数值包", "可空"),
            ("SkillLoadout", "string", "技能包", "指向 SkillLoads.LoadoutId"),
            ("HitRadius", "float", "受击半径", "米"),
            ("HitHeight", "float", "受击高度", "米"),
            ("VisualSlot", "int", "视图槽位", "表现层选模型用；逻辑层不解释"),
        ],
        [
            ["player_hero", "actor.player_hero", "Player", "Player", "PlayerInput",
             "player_base", "", "", "player_kit", 0.45, 1.8, 1],
            ["goblin_grunt", "actor.goblin_grunt", "Monster", "Enemy", "Ai",
             "goblin_base", "", "", "goblin_kit", 0.4, 1.2, 2],
            ["goblin_archer", "actor.goblin_archer", "Monster", "Enemy", "Ai",
             "goblin_archer_base", "", "", "goblin_archer_kit", 0.4, 1.2, 3],
            ["ogre_brute", "actor.ogre_brute", "Elite", "Enemy", "Ai",
             "ogre_base", "", "", "ogre_kit", 0.9, 2.6, 4],
            ["chosen_hero", "actor.chosen_hero", "Player", "Player", "PlayerInput",
             "chosen_base", "", "", "chosen_kit", 0.5, 2.0, 5],
        ],
    )

    sheet(
        wb,
        "StatPacks",
        [
            ("PackId", "string", "数值包id", "同一 PackId 的多行 = 同一层的多个数值"),
            ("Layer", "enum:StatLayer", "层", "Base/Growth/Equipment/Buff/Temporary；Base 是绝对值，其余是差量"),
            ("StatId", "enum:StatId", "属性id", "必须存在于 StatId 枚举；号段规则见 docs/rules/05"),
            ("Value", "float", "值", "倍率型(以 Mul 结尾)默认 1.0"),
        ],
        [
            # ── 玩家 ──
            ["player_base", "Base", "Hp", 120],
            ["player_base", "Base", "HpMax", 120],
            ["player_base", "Base", "Mana", 80],
            ["player_base", "Base", "ManaMax", 80],
            ["player_base", "Base", "ManaRegenPerSec", 4],
            ["player_base", "Base", "Armor", 6],
            ["player_base", "Base", "ArmorK", 100],
            ["player_base", "Base", "MoveSpeed", 5.2],
            ["player_base", "Base", "Strength", 5],
            ["player_base", "Base", "Dexterity", 6],
            ["player_base", "Base", "Luck", 3],
            ["player_base", "Base", "CritChance", 0.05],
            ["player_base", "Base", "CritMultiplier", 1.75],
            ["player_base", "Base", "HurtInvulnTime", 0.6],
            # 玩家的成长层：升级带来的差量（示例值，策划可直接改）
            ["player_growth_demo", "Growth", "HpMax", 10],
            ["player_growth_demo", "Growth", "Strength", 1],
            # ── 哥布林（普通怪）──
            ["goblin_base", "Base", "Hp", 40],
            ["goblin_base", "Base", "HpMax", 40],
            ["goblin_base", "Base", "Mana", 0],
            ["goblin_base", "Base", "ManaMax", 0],
            ["goblin_base", "Base", "Armor", 2],
            ["goblin_base", "Base", "ArmorK", 100],
            ["goblin_base", "Base", "MoveSpeed", 3.4],
            ["goblin_base", "Base", "Strength", 3],
            ["goblin_base", "Base", "Dexterity", 3],
            ["goblin_base", "Base", "Luck", 1],
            ["goblin_base", "Base", "HurtInvulnTime", 0.2],
            # ── 哥布林弓手 ──
            ["goblin_archer_base", "Base", "Hp", 32],
            ["goblin_archer_base", "Base", "HpMax", 32],
            ["goblin_archer_base", "Base", "Mana", 20],
            ["goblin_archer_base", "Base", "ManaMax", 20],
            ["goblin_archer_base", "Base", "Armor", 1],
            ["goblin_archer_base", "Base", "ArmorK", 100],
            ["goblin_archer_base", "Base", "MoveSpeed", 3.0],
            ["goblin_archer_base", "Base", "Strength", 2],
            ["goblin_archer_base", "Base", "Dexterity", 5],
            ["goblin_archer_base", "Base", "Luck", 1],
            ["goblin_archer_base", "Base", "HurtInvulnTime", 0.2],
            # ── 食人魔精英 ──
            ["ogre_base", "Base", "Hp", 300],
            ["ogre_base", "Base", "HpMax", 300],
            ["ogre_base", "Base", "Mana", 0],
            ["ogre_base", "Base", "ManaMax", 0],
            ["ogre_base", "Base", "Armor", 25],
            ["ogre_base", "Base", "ArmorK", 100],
            ["ogre_base", "Base", "MoveSpeed", 2.2],
            ["ogre_base", "Base", "Strength", 18],
            ["ogre_base", "Base", "Dexterity", 2],
            ["ogre_base", "Base", "Luck", 0],
            ["ogre_base", "Base", "HurtInvulnTime", 0.3],
            # ── 天选者（第二个玩家角色：验证「换数值包 = 换角色」）──
            ["chosen_base", "Base", "Hp", 150],
            ["chosen_base", "Base", "HpMax", 150],
            ["chosen_base", "Base", "Mana", 140],
            ["chosen_base", "Base", "ManaMax", 140],
            ["chosen_base", "Base", "ManaRegenPerSec", 9],
            ["chosen_base", "Base", "Armor", 4],
            ["chosen_base", "Base", "ArmorK", 100],
            ["chosen_base", "Base", "MoveSpeed", 4.8],
            ["chosen_base", "Base", "Strength", 4],
            ["chosen_base", "Base", "Dexterity", 7],
            ["chosen_base", "Base", "Luck", 8],
            ["chosen_base", "Base", "CritChance", 0.12],
            ["chosen_base", "Base", "CritMultiplier", 2.0],
            ["chosen_base", "Base", "HurtInvulnTime", 0.8],
        ],
    )

    sheet(
        wb,
        "SkillLoads",
        [
            ("LoadoutId", "string", "技能包id", "指向 Characters.SkillLoadout"),
            ("Slot", "enum:SkillSlot", "槽位", "Dash/PrimaryAttack/WeaponSwap/Signature/Interact"),
            ("SkillId", "int", "技能id", "指向 Skills 表主键；0 = 该槽留空"),
            ("CooldownSeconds", "float", "冷却", "秒；本行覆盖技能默认冷却"),
            ("ManaCost", "float", "法力消耗", "本行覆盖技能默认消耗"),
            ("Note", "string", "备注", "说明这个槽为什么这么配"),
        ],
        [
            ["player_kit", "Dash", 1001, 1.2, 8, "冲刺"],
            ["player_kit", "PrimaryAttack", 1101, 0.12, 0, "瞄准射击（左键）"],
            ["player_kit", "WeaponSwap", 1201, 0.0, 0, "切换武器（Q）"],
            ["player_kit", "Signature", 1301, 6.0, 25, "角色独有技能（R）"],
            ["player_kit", "Interact", 1401, 0.0, 0, "交互（F）"],
            ["goblin_kit", "PrimaryAttack", 1102, 1.4, 0, "近身挥击"],
            ["goblin_archer_kit", "PrimaryAttack", 1103, 1.8, 0, "远程射击"],
            ["ogre_kit", "PrimaryAttack", 1104, 2.4, 0, "重砸"],
            ["ogre_kit", "Signature", 1302, 9.0, 0, "冲锋（精英独有）"],
            ["chosen_kit", "Dash", 1001, 1.0, 8, "冲刺"],
            ["chosen_kit", "PrimaryAttack", 1105, 0.16, 0, "法术弹"],
            ["chosen_kit", "WeaponSwap", 1201, 0.0, 0, "切换武器"],
            ["chosen_kit", "Signature", 1303, 8.0, 40, "天选者：时间减速"],
            ["chosen_kit", "Interact", 1401, 0.0, 0, "交互"],
        ],
    )

    # Meta：schema 版本（ARCHITECTURE §4.2 校验点 ④）
    meta = wb.create_sheet("_Meta")
    meta.append(["Key", "Value"])
    meta.append(["SchemaVersion", SCHEMA_VERSION])
    meta.append(["Owner", "design(策划编辑区)"])
    meta.append(["Source", "唯一真源；生成物在 data/generated/，禁止手改"])
    for c in range(1, 3):
        meta.cell(row=1, column=c).fill = HEADER_FILL
        meta.cell(row=1, column=c).font = Font(bold=True, color="FFFFFF")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    wb.save(OUT)
    print(f"wrote {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
