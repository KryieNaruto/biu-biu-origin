"""生成 data/excel/l10n.xlsx —— 全项目唯一的中文名真源（2026-11-06）。

为什么单独一个文件
------------------
`combat.xlsx` / `characters.xlsx` 的 `DisplayKey` 列**明文规定不写中文**（见两张表的第 4 行备注），
它是**本地化 key**，不是显示名。所以中文名必须另有一张表，且：

  · 图鉴（`gen_codex.py`）读它 → 策划改完名字重跑图鉴就能看到；
  · 将来的游戏 UI 读它 → **同一份数据，两个消费方**，不会出现"图鉴叫飞弹、游戏里叫炮弹"。

为什么不写死在 Python 里
------------------------
写进脚本 = 中文名变成代码常量，违反规则 4（数值/文案走 Excel）。而且加一个新武器就得改脚本。

表结构
------
一把 `Key` 覆盖四类 key，前缀决定查谁：

| Key 前缀 | 例子 | 谁在用 |
|---|---|---|
| `actor.*` | `actor.slime_401` | `Objects.DisplayKey` / `Characters.DisplayKey` |
| `item.*` | `item.pistol_101` | `Objects.DisplayKey`（武器） |
| `proj.*` | `proj.bullet_101` | `Objects.DisplayKey`（子弹） |
| `fx.*` | `fx.muzzle_small` | `Objects.DisplayKey`（特效） |
| `tag.*` | `tag.pistol` | `Tags.TagId` |
| 枚举名 | `Kinetic` / `Pierce` / `Semi` | 表里的枚举单元格（`Motion` / `HitPolicy` / `Trigger` …） |

后两类（`tag.*` 与枚举名）是**图鉴直接要显示但我没有本地化 key** 的，所以复用枚举值本身当 key。
"""
from __future__ import annotations

import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.utils import get_column_letter

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "data" / "excel" / "l10n.xlsx"

SCHEMA_VERSION = 1

# ═══════════════════════════════════════════════════════════════════════════
# 1. 物体显示名（key = DisplayKey）
# ═══════════════════════════════════════════════════════════════════════════
OBJECTS = [
    # 人物
    ("actor.player_hero",         "初代勇者"),
    ("actor.test_hero",           "标准测试者"),
    ("actor.test_hero_tank",      "重甲测试者"),
    ("actor.test_hero_agile",     "敏捷测试者"),
    ("actor.test_hero_lucky",     "幸运测试者"),
    ("actor.test_hero_elemental", "元素测试者"),
    ("actor.chosen_hero",         "天选者"),
    # 怪物
    ("actor.goblin_grunt",        "哥布林杂兵"),
    ("actor.goblin_archer",       "哥布林弓手"),
    ("actor.goblin_shaman",       "哥布林萨满"),
    ("actor.ogre_brute",          "食人魔重锤"),
    ("actor.slime_401",           "分裂史莱姆"),
    ("actor.slime_402",           "爆裂史莱姆"),
    ("actor.slime_403",           "自爆史莱姆"),
    # 武器
    ("item.pistol_101",           "制式手枪"),
    ("item.smg_102",              "速射手枪"),
    ("item.revolver_103",         "重型左轮"),
    ("item.bow_201",              "猎弓"),
    ("item.rifle_202",            "突击步枪"),
    ("item.sniper_203",           "狙击步枪"),
    ("item.club_401",             "巨力木棒"),
    ("item.orb_501",              "法术法球"),
    # 子弹
    ("proj.bullet_101",           "标准弹"),
    ("proj.bullet_102",           "穿透弹"),
    ("proj.bullet_103",           "重弹"),
    ("proj.arrow_202",            "穿甲箭"),
    ("proj.bullet_201",           "追踪弹"),
    ("proj.bullet_301",           "爆炸弹"),
    ("proj.bullet_501",           "能量弹"),
    ("proj.bullet_hero_pistol",   "手枪弹"),
    ("proj.bullet_hero_cannon",   "重炮弹"),
    ("proj.bullet_hero_smg",      "冲锋枪弹"),
    ("proj.bullet_hero_ricochet", "幸运弹"),
    ("proj.bullet_hero_orb",      "元素球"),
    # 特效
    ("fx.muzzle_small",           "枪口火焰"),
    ("fx.hit_spark",              "命中火花"),
    ("fx.bomb_burst",             "爆炸爆散"),
]

# ═══════════════════════════════════════════════════════════════════════════
# 2. 标签名（key = TagId，含 tag. 前缀）
# ═══════════════════════════════════════════════════════════════════════════
TAGS = [
    # Kind 轴
    ("tag.actor",          "角色"),
    ("tag.weapon",         "武器"),
    ("tag.projectile",     "子弹"),
    # WeaponKind 轴
    ("tag.pistol",         "手枪"),
    ("tag.rifle",          "步枪"),
    ("tag.bow",            "弓"),
    ("tag.melee",          "近战"),
    # ProjectileKind 轴
    ("tag.homing",         "追踪"),
    # Element 轴
    ("tag.kinetic",        "动能"),
    ("tag.energy",         "能量"),
    ("tag.explosive",      "爆炸"),
    # Ability 轴
    ("tag.pierce",         "穿透"),
    ("tag.bounce",         "弹跳"),
    ("tag.split",          "分裂"),
    ("tag.summon",         "召唤"),
    ("tag.chain",          "连锁"),
    ("tag.projectile_fired", "射击"),
    ("tag.short_range",    "近战"),
    ("tag.multi_shot",     "多发"),
    ("tag.charge",         "蓄力"),
    ("tag.ricochet",       "弹射"),
    ("tag.high_damage",    "高伤"),
    # Trigger 轴
    ("tag.volatile",       "易爆"),
    # Rarity 轴
    ("tag.common",         "普通"),
    ("tag.uncommon",       "精良"),
    ("tag.rare",           "稀有"),
    ("tag.legendary",      "传说"),
    # Race 轴
    ("tag.humanoid",       "人形"),
    ("tag.goblin",         "哥布林"),
    ("tag.slime",          "史莱姆"),
    ("tag.ogre",           "食人魔"),
    # Role 轴
    ("tag.hero",           "勇者"),
    ("tag.grunt",          "杂兵"),
    ("tag.ranged",         "远程"),
    ("tag.caster",         "施法者"),
    ("tag.elite",          "精英"),
    ("tag.assassin",       "刺客"),
    # Effect 轴
    ("tag.muzzle",         "枪口"),
    ("tag.hit",            "命中"),
    ("tag.burst",          "爆散"),
    # Loot 轴
    ("tag.loot_ammo",      "弹药"),
    ("tag.loot_health",    "回血"),
    ("tag.loot_mana",      "回蓝"),
    ("tag.loot_coin",      "金币"),
    ("tag.loot_relic",     "遗物"),
    ("tag.loot_key",       "钥匙"),
    # Attract 轴
    ("tag.attract_magnet", "磁吸"),
    ("tag.attract_weapon", "武器吸引"),
    ("tag.attract_exp",    "经验吸引"),
    # Faction 轴
    ("tag.own_player",     "我方"),
    ("tag.own_enemy",      "敌方"),
    ("tag.own_neutral",    "中立"),
    # Theme 轴
    ("tag.theme_forest",   "森林"),
    ("tag.theme_dungeon",  "地牢"),
    ("tag.theme_forge",    "熔炉"),
    # Aura 轴
    ("tag.aura_fire",      "火焰光环"),
    ("tag.aura_ice",       "寒冰光环"),
    ("tag.aura_shock",     "雷电光环"),
    # Craft 轴
    ("tag.craft_metal",    "金属"),
    ("tag.craft_wood",     "木材"),
    ("tag.craft_gem",      "宝石"),
    ("tag.craft_essence",  "精华"),
]

# ═══════════════════════════════════════════════════════════════════════════
# 3. 枚举值（key = 枚举成员名，直接出现在表里的单元格）
# ═══════════════════════════════════════════════════════════════════════════
ENUMS = [
    # Trigger（武器触发方式）
    ("Auto",          "全自动"), ("Semi",     "半自动"), ("Burst",  "点射"),
    ("Charge",        "蓄力"),   ("Hold",     "按住"),   ("Triggered", "触发"),
    ("Manual",        "手动"),
    # Delivery（投送方式）
    ("Projectile",    "投射物"), ("Instant",  "瞬时"),   ("Beam",   "光束"),
    ("Melee",         "近战"),   ("Aura",     "光环"),   ("SpawnActor", "生成角色"),
    ("Throw",         "投掷"),
    # ProjectileMotion（运动方式）
    ("Straight",      "直线"),   ("Orbit",    "环绕"),   ("Wave",   "波浪"),
    ("Boomerang",     "回旋"),   ("Falling",  "下坠"),   ("Spiral", "螺旋"),
    ("Anchor",        "锚定"),
    # ProjectileHitPolicy（命中策略）
    ("Single",        "单段"),   ("Pierce",   "穿透"),   ("Bounce", "弹跳"),
    ("Splash",        "溅射"),   ("Explode",  "爆炸"),   ("Chain",  "连锁"),
    ("Return",        "返回"),   ("Ignore",   "穿透忽略"),
    # OnExpire / OnHit 等
    ("None",          "无"),     ("SpawnProjectile", "生成子弹"), ("SpawnEffect", "生成特效"),
    # ActorKind
    ("Player",        "玩家"),   ("Monster",  "怪物"),   ("Elite",  "精英"),
    ("Boss",          "首领"),   ("Summon",   "召唤物"), ("Destructible", "可破坏物"),
    # TeamId
    ("Enemy",         "敌方"),   ("Neutral",  "中立"),
    # ControlScheme
    ("PlayerInput",   "玩家操控"), ("Ai",     "AI 操控"), ("Script", "脚本操控"),
    # SkillKind
    ("data",          "数据技能"), ("fire_projectile", "发射子弹"),
    # SkillSlot
    ("Dash",          "冲刺"),   ("PrimaryAttack", "左键攻击"), ("WeaponSwap", "切武器"),
    ("Signature",     "大招"),   ("Interact", "交互"),
    # AI 行为（后续会用到）
    ("Aggressive",    "主动"),   ("Defensive", "防守"),  ("Passive", "被动"),
    ("Patrol",        "巡逻"),   ("Flee",     "逃跑"),
]

# ═══════════════════════════════════════════════════════════════════════════
# 4. 括号里显示的单位 / 图标（图鉴用，纯展示）
# ═══════════════════════════════════════════════════════════════════════════
UNITS = [
    ("unit.hp",        "生命"),
    ("unit.mana",      "法力"),
    ("unit.armor",     "护甲"),
    ("unit.speed",     "移速"),
    ("unit.damage",    "伤害"),
    ("unit.rate",      "射速"),
    ("unit.range",     "射程"),
]

COLUMNS = ["Key", "Zh", "Note"]


def main() -> int:
    wb = Workbook()
    ws = wb.active
    ws.title = "L10n"

    # 四行表头，与其它主题文件一致：字段名 / 类型 / 中文名 / 备注
    ws.append(COLUMNS)
    ws.append(["string", "string", "string"])
    ws.append(["键", "中文名", "给策划看的说明（可空）"])
    ws.append(["★ 唯一真源：图鉴与游戏 UI 都读这里。前缀 actor./item./proj./fx./tag. 或枚举名",
               "★ 直接把中文写在这里，改完重跑 gen_codex.py 即可看到",
               "留空即可；只在语义容易误解时填"])
    ws.append([])  # 第 5 行：#data 标记行（Python 生成时不需要，保留占位对齐）

    row = len(COLUMNS) and 6
    for key, zh in OBJECTS:
        ws.append([key, zh, None])
    for key, zh in TAGS:
        ws.append([key, zh, None])
    for key, zh in ENUMS:
        ws.append([key, zh, None])
    for key, zh in UNITS:
        ws.append([key, zh, None])

    # 样式
    head_fill = PatternFill("solid", fgColor="1F4E79")
    for c in range(1, len(COLUMNS) + 1):
        cell = ws.cell(row=1, column=c)
        cell.font = Font(bold=True, color="FFFFFF")
        cell.fill = head_fill
        cell.alignment = Alignment(horizontal="center", vertical="center")
    for c in range(1, len(COLUMNS) + 1):
        ws.cell(row=3, column=c).font = Font(bold=True)
        ws.cell(row=4, column=c).alignment = Alignment(wrap_text=True, vertical="top")
    for r in range(1, ws.max_row + 1):
        ws.cell(row=r, column=1).alignment = Alignment(vertical="center")
        ws.cell(row=r, column=2).alignment = Alignment(vertical="center")
    ws.column_dimensions["A"].width = 30
    ws.column_dimensions["B"].width = 20
    ws.column_dimensions["C"].width = 50
    ws.row_dimensions[4].height = 42
    ws.freeze_panes = "A6"

    # _Meta
    meta = wb.create_sheet("_Meta")
    meta.append(["Key", "Value"])
    meta.append(["SchemaVersion", SCHEMA_VERSION])
    meta.append(["Owner", "design(策划编辑区)"])
    meta.append(["Source", "手工维护；tools/data_pipeline/gen_l10n_xlsx.py 仅在首次生成时使用"])

    OUT.parent.mkdir(parents=True, exist_ok=True)
    wb.save(OUT)
    total = len(OBJECTS) + len(TAGS) + len(ENUMS) + len(UNITS)
    print(f"[ok] {OUT}")
    print(f"     物体 {len(OBJECTS)} · 标签 {len(TAGS)} · 枚举 {len(ENUMS)} · 单位 {len(UNITS)} = {total} 条")
    return 0


if __name__ == "__main__":
    sys.exit(main())
