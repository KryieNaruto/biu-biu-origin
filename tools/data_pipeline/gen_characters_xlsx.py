"""生成 data/excel/characters.xlsx —— 角色抽象的数据真源（v4）。

为什么用脚本生成 Excel 而不是让策划手动建：表头 4 行（字段名/类型/中文名/备注）是本项目的
硬约定（ARCHITECTURE.md §4.2），手搓容易漏；脚本生成保证第一次就合规，重复运行幂等。
生成之后 **Excel 就是唯一真源**，改数值请直接改 xlsx（或本脚本后重跑），不要改 data/generated/。

Sheets:
  Characters       角色定义：类型 + 默认阵营 + 控制方案 + 数值包组合 + 技能包
  StatPacks        数值包：每行 = 某个包在某一层里的一项数值（(PackId, Layer, StatId) 唯一）
  SkillLoads       技能包：每行 = 某个包在某槽位装哪个技能（含 Kind/WeaponId/ProjectileId）
  Assemblies       装配：模块集合（哪些系统族参与）
  VariantAssembly  变体：一行 = 一个可玩变体（数值包组合 + 技能包 + 武器 + 外观/镜头/手柄）
  VisualPacks      外观包：模型/材质/尺寸
  CameraPacks      镜头包：跟随参数
  InputPacks       手柄包：灵敏度/死区
  Weapons          武器表：伤害/弹速/射程/散布/暴击/弹丸数
  _Meta            schema 版本

v4 变更（相对 v3）：
  - SkillLoads 的 ProjectileId 列（原为「武器行 id」）拆成两列：
      WeaponId     = 哪把枪（武器行 id，查 Weapons.Id 拿伤害/弹速/射程）
      ProjectileId = 子弹长什么样（抛射物外观 id，只给射击技能/命令门面用）
    原因：v1 用武器 id 兼作子弹外观 id，两者都是 string，编译期毫无报错，
    运行期表现为「外观按不存在的抛射物查表」。拆开后抛射物表将来独立不必再动技能行形状。
    硬约束：同一行 WeaponId != ProjectileId（值故意不同），数据校验会打中文警告。
"""
from __future__ import annotations

import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Font, PatternFill

ROOT = Path(__file__).resolve().parents[2] / "data" / "excel"
OUT = ROOT / "characters.xlsx"

# v6：CameraPacks 新增 LookAheadRatio / LookAheadMaxMeters，FollowLerp 改名为 FollowRatePerSecond（与运行时语义对齐）
SCHEMA_VERSION = 6

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

    # ─────────────────────────────── Characters ───────────────────────────────
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
            # ── 旧主角行（main 的 §15 角色抽象时期）：保留它是为了不打断既有测试与
            #    CharacterContentSet 的内置副本；新内容一律用下面的 test_hero 四个变体。──
            ["player_hero", "actor.player_hero", "Player", "Player", "PlayerInput",
             "player_base", "player_growth_demo", "", "player_kit", 0.45, 1.8, 1],
            # ── 测试人物（v1 主角，4 个变体共用一套装配）──
            ["test_hero", "actor.test_hero", "Player", "Player", "PlayerInput",
             "test_hero_base", "test_hero_growth", "", "test_hero_kit", 0.45, 1.8, 1],
            ["test_hero_tank", "actor.test_hero_tank", "Player", "Player", "PlayerInput",
             "test_hero_tank_base", "test_hero_growth", "", "test_hero_tank_kit", 0.55, 2.0, 2],
            ["test_hero_agile", "actor.test_hero_agile", "Player", "Player", "PlayerInput",
             "test_hero_agile_base", "test_hero_growth", "", "test_hero_agile_kit", 0.40, 1.7, 3],
            ["test_hero_lucky", "actor.test_hero_lucky", "Player", "Player", "PlayerInput",
             "test_hero_lucky_base", "test_hero_growth", "", "test_hero_lucky_kit", 0.45, 1.8, 4],
            # ── 怪物（与玩家同源，只改数据行）──
            # VisualSlot 与 CharacterContentSet 的内置副本逐项一致（1~4 已被四个英雄变体占用）。
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

    # ─────────────────────────────── StatPacks ───────────────────────────────
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
            # ── 旧主角数值包（与 CharacterContentSet 内置副本逐项一致）──
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
            ["player_growth_demo", "Growth", "HpMax", 10],
            ["player_growth_demo", "Growth", "Strength", 1],
            # ── 测试人物 · 标准（均衡基准）──
            # Hp=120 是**基础值**，+10 由 test_hero_growth 的 Growth 层叠加，合计 130
            # （与 CharacterContentSet 内置副本逐项一致；两者口径必须先说清再改任何一边）。
            ["test_hero_base", "Base", "Hp", 120],
            ["test_hero_base", "Base", "HpMax", 120],
            ["test_hero_base", "Base", "Mana", 80],
            ["test_hero_base", "Base", "ManaMax", 80],
            ["test_hero_base", "Base", "ManaRegenPerSec", 4],
            ["test_hero_base", "Base", "Stamina", 100],
            ["test_hero_base", "Base", "StaminaMax", 100],
            ["test_hero_base", "Base", "StaminaRegenPerSec", 12],
            ["test_hero_base", "Base", "Armor", 6],
            ["test_hero_base", "Base", "ArmorK", 100],
            ["test_hero_base", "Base", "MoveSpeed", 5.2],
            ["test_hero_base", "Base", "Strength", 5],
            ["test_hero_base", "Base", "Dexterity", 6],
            ["test_hero_base", "Base", "Luck", 3],
            ["test_hero_base", "Base", "CritChance", 0.05],
            ["test_hero_base", "Base", "CritMultiplier", 1.75],
            ["test_hero_base", "Base", "HurtInvulnTime", 0.6],
            ["test_hero_base", "Base", "MoveAccelTime", 0.12],
            ["test_hero_base", "Base", "TurnResponseTime", 0.10],
            ["test_hero_base", "Base", "AimRange", 18],
            ["test_hero_base", "Base", "DashSpeedMul", 2.2],
            ["test_hero_base", "Base", "DashDuration", 0.18],
            # ── 测试人物 · 重甲（血厚/护甲高/移速慢/手感沉）──
            ["test_hero_tank_base", "Base", "Hp", 220],
            ["test_hero_tank_base", "Base", "HpMax", 220],
            ["test_hero_tank_base", "Base", "Mana", 60],
            ["test_hero_tank_base", "Base", "ManaMax", 60],
            ["test_hero_tank_base", "Base", "ManaRegenPerSec", 3],
            ["test_hero_tank_base", "Base", "Stamina", 120],
            ["test_hero_tank_base", "Base", "StaminaMax", 120],
            ["test_hero_tank_base", "Base", "StaminaRegenPerSec", 10],
            ["test_hero_tank_base", "Base", "Armor", 20],
            ["test_hero_tank_base", "Base", "ArmorK", 100],
            ["test_hero_tank_base", "Base", "MoveSpeed", 3.6],
            ["test_hero_tank_base", "Base", "MoveAccelTime", 0.2],
            ["test_hero_tank_base", "Base", "TurnResponseTime", 0.18],
            ["test_hero_tank_base", "Base", "AimRange", 16],
            ["test_hero_tank_base", "Base", "Strength", 9],
            ["test_hero_tank_base", "Base", "Dexterity", 3],
            ["test_hero_tank_base", "Base", "Luck", 2],
            ["test_hero_tank_base", "Base", "CritChance", 0.03],
            ["test_hero_tank_base", "Base", "CritMultiplier", 1.5],
            ["test_hero_tank_base", "Base", "HurtInvulnTime", 0.8],
            ["test_hero_tank_base", "Base", "DashSpeedMul", 1.8],
            ["test_hero_tank_base", "Base", "DashDuration", 0.22],
            # ── 测试人物 · 敏捷（移速快/射速快/伤害低）──
            ["test_hero_agile_base", "Base", "Hp", 90],
            ["test_hero_agile_base", "Base", "HpMax", 90],
            ["test_hero_agile_base", "Base", "Mana", 100],
            ["test_hero_agile_base", "Base", "ManaMax", 100],
            ["test_hero_agile_base", "Base", "ManaRegenPerSec", 6],
            ["test_hero_agile_base", "Base", "Stamina", 140],
            ["test_hero_agile_base", "Base", "StaminaMax", 140],
            ["test_hero_agile_base", "Base", "StaminaRegenPerSec", 16],
            ["test_hero_agile_base", "Base", "Armor", 3],
            ["test_hero_agile_base", "Base", "ArmorK", 100],
            ["test_hero_agile_base", "Base", "MoveSpeed", 7.4],
            ["test_hero_agile_base", "Base", "MoveAccelTime", 0.06],
            ["test_hero_agile_base", "Base", "TurnResponseTime", 0.05],
            ["test_hero_agile_base", "Base", "AimRange", 20],
            ["test_hero_agile_base", "Base", "Strength", 3],
            ["test_hero_agile_base", "Base", "Dexterity", 11],
            ["test_hero_agile_base", "Base", "Luck", 4],
            ["test_hero_agile_base", "Base", "CritChance", 0.08],
            ["test_hero_agile_base", "Base", "CritMultiplier", 1.6],
            ["test_hero_agile_base", "Base", "HurtInvulnTime", 0.4],
            ["test_hero_agile_base", "Base", "DashSpeedMul", 2.8],
            ["test_hero_agile_base", "Base", "DashDuration", 0.16],
            # ── 测试人物 · 幸运（暴击高）──
            ["test_hero_lucky_base", "Base", "Hp", 110],
            ["test_hero_lucky_base", "Base", "HpMax", 110],
            ["test_hero_lucky_base", "Base", "Mana", 90],
            ["test_hero_lucky_base", "Base", "ManaMax", 90],
            ["test_hero_lucky_base", "Base", "ManaRegenPerSec", 5],
            ["test_hero_lucky_base", "Base", "Stamina", 110],
            ["test_hero_lucky_base", "Base", "StaminaMax", 110],
            ["test_hero_lucky_base", "Base", "StaminaRegenPerSec", 12],
            ["test_hero_lucky_base", "Base", "Armor", 5],
            ["test_hero_lucky_base", "Base", "ArmorK", 100],
            ["test_hero_lucky_base", "Base", "MoveSpeed", 5.4],
            ["test_hero_lucky_base", "Base", "MoveAccelTime", 0.12],
            ["test_hero_lucky_base", "Base", "TurnResponseTime", 0.1],
            ["test_hero_lucky_base", "Base", "AimRange", 18],
            ["test_hero_lucky_base", "Base", "Strength", 4],
            ["test_hero_lucky_base", "Base", "Dexterity", 6],
            ["test_hero_lucky_base", "Base", "Luck", 15],
            ["test_hero_lucky_base", "Base", "CritChance", 0.15],
            ["test_hero_lucky_base", "Base", "CritMultiplier", 2],
            ["test_hero_lucky_base", "Base", "HurtInvulnTime", 0.6],
            ["test_hero_lucky_base", "Base", "DashSpeedMul", 2.4],
            ["test_hero_lucky_base", "Base", "DashDuration", 0.18],
            # ── 成长层：4 个变体共用（成长与 Base 正交：换变体不改成长）──
            # 成长层与内置副本逐项一致：HpMax +10 / Strength +1（合计 Hp 130 = 120 Base + 10 Growth）
            ["test_hero_growth", "Growth", "HpMax", 10],
            ["test_hero_growth", "Growth", "Strength", 1],
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
            ["chosen_base", "Base", "DashSpeedMul", 2.0],
            ["chosen_base", "Base", "DashDuration", 0.2],
        ],
    )

    # ─────────────────────────────── SkillLoads ───────────────────────────────
    # 槽位号：1=Dash 2=PrimaryAttack 3=WeaponSwap 4=Signature 5=Interact
    sheet(
        wb,
        "SkillLoads",
        [
            ("LoadoutId", "string", "技能包id", "指向 Characters.SkillLoadout"),
            ("Slot", "enum:SkillSlot", "槽位", "Dash/PrimaryAttack/WeaponSwap/Signature/Interact"),
            ("SkillId", "int", "技能id", "指向 Skills 表主键；0 = 该槽留空"),
            ("CooldownSeconds", "float", "冷却", "秒；本行覆盖技能默认冷却；射击技能必须 == 武器行 ShotCooldown"),
            ("ManaCost", "float", "法力消耗", "本行覆盖技能默认消耗"),
            ("Kind", "enum:SkillKind", "实现种类", "fire_projectile=投射物射击 / dash=冲刺（参数读数值包 DashSpeedMul·DashDuration，不写在本表） / data=占位（施放不产出）。数据层只声明种类，实例化由宿主 ISkillFactory 做"),
            ("WeaponId", "string", "武器行id", "哪把枪（指向 Weapons.Id）；查伤害/弹速/射程用。非射击技能留空"),
            ("ProjectileId", "string", "子弹外观id", "子弹长什么样（如 proj_pistol_basic），只给射击技能/命令门面用。非射击技能留空。不得与 WeaponId 填成一样"),
            ("Note", "string", "备注", "说明这个槽为什么这么配"),
        ],
        [
            # ── 旧主角技能包（与 CharacterContentSet 内置副本逐项一致）──
            ["player_kit", "Dash", 1001, 1.2, 8, "dash", "", "", "冲刺（Shift）"],
            ["player_kit", "PrimaryAttack", 1101, 0.12, 0, "fire_projectile", "pistol_basic", "", "左键射击"],
            ["player_kit", "WeaponSwap", 1201, 0, 0, "data", "", "", "切武器（Q）"],
            ["player_kit", "Signature", 1301, 6, 25, "data", "", "", "大招（R）"],
            ["player_kit", "Interact", 1401, 0, 0, "data", "", "", "交互（F）"],
            # ── 测试人物 · 标准 ──
            ["test_hero_kit", "Dash", 1001, 1.2, 8, "dash", "", "", "冲刺（Shift）"],
            ["test_hero_kit", "PrimaryAttack", 1101, 0.15, 0, "fire_projectile", "pistol_basic", "proj_pistol_basic", "瞄准射击（左键）"],
            ["test_hero_kit", "WeaponSwap", 1201, 0.0, 0, "data", "", "", "切换武器（Q）"],
            ["test_hero_kit", "Signature", 1304, 12.0, 25, "data", "", "", "角色独有技能（R）"],
            ["test_hero_kit", "Interact", 1401, 0.0, 0, "data", "", "", "交互（F）"],
            # ── 测试人物 · 重甲 ──
            ["test_hero_tank_kit", "Dash", 1001, 1.2, 8, "dash", "", "", "冲刺（Shift）"],
            ["test_hero_tank_kit", "PrimaryAttack", 1106, 0.42, 0, "fire_projectile", "cannon_heavy", "proj_cannon_heavy", "重炮（慢速高伤）"],
            ["test_hero_tank_kit", "WeaponSwap", 1201, 0.0, 0, "data", "", "", "切换武器（Q）"],
            ["test_hero_tank_kit", "Signature", 1305, 16.0, 20, "data", "", "", "角色独有技能（R）"],
            ["test_hero_tank_kit", "Interact", 1401, 0.0, 0, "data", "", "", "交互（F）"],
            # ── 测试人物 · 敏捷 ──
            ["test_hero_agile_kit", "Dash", 1001, 1.2, 8, "dash", "", "", "冲刺（Shift）"],
            ["test_hero_agile_kit", "PrimaryAttack", 1107, 0.08, 0, "fire_projectile", "smg_light", "proj_smg_light", "冲锋枪（高射速低伤）"],
            ["test_hero_agile_kit", "WeaponSwap", 1201, 0.0, 0, "data", "", "", "切换武器（Q）"],
            ["test_hero_agile_kit", "Signature", 1306, 10.0, 30, "data", "", "", "角色独有技能（R）"],
            ["test_hero_agile_kit", "Interact", 1401, 0.0, 0, "data", "", "", "交互（F）"],
            # ── 测试人物 · 幸运 ──
            ["test_hero_lucky_kit", "Dash", 1001, 1.2, 8, "dash", "", "", "冲刺（Shift）"],
            ["test_hero_lucky_kit", "PrimaryAttack", 1108, 0.18, 0, "fire_projectile", "ricochet_lucky", "proj_ricochet_lucky", "幸运弹射（高暴击）"],
            ["test_hero_lucky_kit", "WeaponSwap", 1201, 0.0, 0, "data", "", "", "切换武器（Q）"],
            ["test_hero_lucky_kit", "Signature", 1307, 14.0, 25, "data", "", "", "角色独有技能（R）"],
            ["test_hero_lucky_kit", "Interact", 1401, 0.0, 0, "data", "", "", "交互（F）"],
            # ── 怪物（沿用 v1，占位种类）──
            ["goblin_kit", "PrimaryAttack", 1102, 1.4, 0, "data", "", "", "近身挥击"],
            ["goblin_archer_kit", "PrimaryAttack", 1103, 1.8, 0, "data", "", "", "远程射击"],
            ["ogre_kit", "PrimaryAttack", 1104, 2.4, 0, "data", "", "", "重砸"],
            ["ogre_kit", "Signature", 1302, 9.0, 0, "data", "", "", "冲锋（精英独有）"],
            ["chosen_kit", "Dash", 1001, 1.0, 8, "dash", "", "", "冲刺"],
            ["chosen_kit", "PrimaryAttack", 1105, 0.16, 0, "data", "", "", "法术弹"],
            ["chosen_kit", "WeaponSwap", 1201, 0.0, 0, "data", "", "", "切换武器"],
            ["chosen_kit", "Signature", 1303, 8.0, 40, "data", "", "", "天选者：时间减速"],
            ["chosen_kit", "Interact", 1401, 0.0, 0, "data", "", "", "交互"],
        ],
    )

    # ─────────────────────────────── Assemblies ───────────────────────────────
    sheet(
        wb,
        "Assemblies",
        [
            ("Id", "string", "装配id", "一组变体共用的模块集合；如 hero"),
            ("Note", "string", "备注", "这个装配代表什么"),
            ("Modules", "string", "模块集合", "逗号分隔的 CharacterModuleSet 成员；宿主按它挂系统族"),
        ],
        [
            ["hero", "测试人物装配", "Stat,Skill,Weapon,Aim,Move,Lifecycle,PlayerInput"],
        ],
    )

    # ──────────────────────────── VariantAssembly ────────────────────────────
    sheet(
        wb,
        "VariantAssembly",
        [
            ("Id", "string", "变体id", "全局唯一；= ActorDefinition.Id"),
            ("AssemblyId", "string", "所属装配", "指向 Assemblies.Id"),
            ("DisplayName", "string", "显示名", "HUD 用中文，如「测试人物」"),
            ("VariantTag", "string", "变体标签", "HUD 小字，如「变体 · 重甲」；空串 = 不画"),
            ("BasePackage", "string", "基础数值包", "必填，指向 StatPacks.Base"),
            ("SkillLoadout", "string", "技能包", "指向 SkillLoads.LoadoutId"),
            ("GrowthPackage", "string", "成长数值包", "可空"),
            ("EquipmentPackage", "string", "装备数值包", "可空"),
            ("Weapon", "string", "武器行", "指向 Weapons.Id；空 = 不带武器"),
            ("VisualPack", "string", "外观包", "指向 VisualPacks.Id"),
            ("CameraPack", "string", "镜头包", "指向 CameraPacks.Id；空 = 用默认"),
            ("InputPack", "string", "手柄包", "指向 InputPacks.Id；空 = 用默认"),
            ("ExtraModules", "string", "变体额外模块", "逗号分隔；与装配模块取并集"),
        ],
        [
            ["test_hero", "hero", "测试人物", "变体 · 标准", "test_hero_base", "test_hero_kit",
             "test_hero_growth", "", "pistol_basic", "lit_test_hero", "follow_std", "pad_std", ""],
            ["test_hero_tank", "hero", "测试人物", "变体 · 重甲", "test_hero_tank_base", "test_hero_tank_kit",
             "test_hero_growth", "", "cannon_heavy", "lit_test_hero_tank", "follow_heavy", "pad_heavy", ""],
            ["test_hero_agile", "hero", "测试人物", "变体 · 敏捷", "test_hero_agile_base", "test_hero_agile_kit",
             "test_hero_growth", "", "smg_light", "lit_test_hero_agile", "follow_fast", "pad_fast", ""],
            ["test_hero_lucky", "hero", "测试人物", "变体 · 幸运", "test_hero_lucky_base", "test_hero_lucky_kit",
             "test_hero_growth", "", "ricochet_lucky", "lit_test_hero_lucky", "follow_std", "pad_std", ""],
        ],
    )

    # ─────────────────────────────── VisualPacks ───────────────────────────────
    sheet(
        wb,
        "VisualPacks",
        [
            ("Id", "string", "外观包id", "指向 VariantAssembly.VisualPack"),
            ("ModelPath", "string", "模型路径", "res:// 或 素材/ 相对路径"),
            ("Scale", "float", "缩放", "1.0 = 原始大小"),
            ("TintHex", "string", "染色", "#RRGGBB；表现层解读，逻辑层不读"),
        ],
        [
            ["lit_test_hero", "res://素材/Characters/TestHero.glb", 1.0, "#7FB2D8"],
            ["lit_test_hero_tank", "res://素材/Characters/TestHero.glb", 1.12, "#8C7B63"],
            ["lit_test_hero_agile", "res://素材/Characters/TestHero.glb", 0.94, "#7FCFA8"],
            ["lit_test_hero_lucky", "res://素材/Characters/TestHero.glb", 1.0, "#D8C67F"],
        ],
    )

    # ─────────────────────────────── CameraPacks ───────────────────────────────
    sheet(
        wb,
        "CameraPacks",
        [
            ("Id", "string", "镜头包id", "指向 VariantAssembly.CameraPack"),
            ("Distance", "float", "距离", "米"),
            ("Height", "float", "高度", "米"),
            ("FollowRatePerSecond", "float", "跟随收敛速率",
             "每秒收敛速率，必须 > 0；越大越跟手（帧率无关）。"
             "★不是每帧 lerp 系数：代码按 1-exp(-rate*dt) 算，填 0.14 会慢到几乎不动"),
            ("FovDegrees", "float", "视场角", "度"),
            ("LookAheadRatio", "float", "瞄准偏移幅度",
             "按可视高度的比例；0 = 不偏移。越大准星方向看得越远"),
            ("LookAheadMaxMeters", "float", "瞄准偏移上限",
             "米；0 = 不封顶。防止大视野下偏移把角色推出屏幕"),
        ],
        [
            ["follow_std", 12.0, 9.0, 10.0, 55, 0.18, 1.6],
            ["follow_heavy", 13.5, 9.8, 7.0, 55, 0.14, 1.4],
            ["follow_fast", 11.0, 8.4, 14.0, 60, 0.24, 2.0],
        ],
    )

    # ─────────────────────────────── InputPacks ───────────────────────────────
    sheet(
        wb,
        "InputPacks",
        [
            ("Id", "string", "手柄包id", "指向 VariantAssembly.InputPack"),
            ("LookSensitivity", "float", "视角灵敏度", "倍率"),
            ("StickDeadZone", "float", "摇杆死区", "0~1"),
            ("AimAssistStrength", "float", "瞄准辅助强度", "0~1"),
        ],
        [
            ["pad_std", 1.0, 0.18, 0.35],
            ["pad_heavy", 0.85, 0.22, 0.45],
            ["pad_fast", 1.15, 0.15, 0.25],
        ],
    )

    # ─────────────────────────────── Weapons ───────────────────────────────
    sheet(
        wb,
        "Weapons",
        [
            ("Id", "string", "武器id", "行 id（可读）；VariantAssembly.Weapon / SkillLoads.WeaponId 指向它"),
            ("WeaponId", "int", "武器数字主键", "全局唯一，方便存档/网络用整数"),
            ("Damage", "float", "单发伤害", "点"),
            ("BulletSpeed", "float", "弹速", "米/秒"),
            ("RangeMeters", "float", "射程", "米"),
            ("SpreadDegrees", "float", "散布", "度；0 = 完全精准"),
            ("CriticalChance", "float", "武器暴击率", "与 StatId.CritChance 相加"),
            ("CriticalMultiplier", "float", "武器暴击倍率", "**必须 ≥ 1.0**（1.0 = 暴击不加成）。代码是 Damage * CriticalMultiplier，填 0.5 会让暴击比不暴击还轻，填 0 = 暴击零伤害。与 StatId.CritMultiplier 相加后 -1"),
            ("ProjectileRadius", "float", "弹丸半径", "米"),
            ("ShotCooldown", "float", "射击冷却", "秒；必须 == SkillLoads 左键行 CooldownSeconds"),
            ("PelletsPerShot", "int", "单次弹丸数", "1 = 单发；>1 = 霰弹"),
        ],
        [
            ["pistol_basic", 2001, 20.0, 22.0, 24.0, 1.5, 0.05, 1.00, 0.10, 0.15, 1],
            ["cannon_heavy", 2002, 55.0, 14.0, 26.0, 5.0, 0.00, 1.00, 0.22, 0.42, 1],
            ["smg_light", 2003, 8.0, 26.0, 18.0, 4.0, 0.03, 1.00, 0.07, 0.08, 1],
            ["ricochet_lucky", 2004, 16.0, 20.0, 22.0, 2.5, 0.52, 2.25, 0.11, 0.18, 1],
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
