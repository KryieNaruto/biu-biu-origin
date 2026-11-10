"""构建期数据校验器（纯 Python 读 xlsx，不依赖 C#）。

用法（必须用 DSH 自带 python，系统 python 没有 openpyxl）:
    E:\\.dsh\\dsh-runtimes\\dsh-primary-runtime\\dependencies\\python\\python.exe tools\\data_pipeline\\validate_content_xlsx.py

做什么:
  · 把 data/excel/*.xlsx 里所有 Sheet 按「四行表头 + #data 标记行」读进来；
  · 表结构 / 类型 / 枚举 / 外键 / 能力位一致 / Tags 位阵 / 规则值语法 / StatPacks / 引用回环 逐项检查；
  · 任何一条失败：打印**全部**中文错误（源文件 / Sheet / 行列 / 期望 / 实际），exit 1；
  · 全部通过：打印一行 `OK: <n> 项检查通过`，exit 0。

错误格式（docs/rules/04-data-driven.md §9.2）:
    [错误] <文件>/<Sheet> 第<R>行<列字母>列（<Id>.<字段>）: <中文说明>
  R 是 Excel 真实行号（含 4 行表头 + 1 行 #data 标记偏移：数据从第 6 行开始）。

设计约束:
  · 列清单从 gen_combat_xlsx.py import（唯一来源），生成器改列 → 校验器自动跟上；
  · 规则值是字符串，这里做**真解析**（正则 + 数值 + StatId/TagId 存在性 + 合理范围），不是只判非空；
  · 所有错误收集完再以退出码 1 结束；不抛栈。
"""
from __future__ import annotations

import importlib.util
import re
import sys
from pathlib import Path

from openpyxl import load_workbook
from openpyxl.utils import get_column_letter

PIPELINE_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = PIPELINE_DIR.parents[1]
EXCEL_DIR = PROJECT_ROOT / "data" / "excel"

HEADER_ROWS = 4          # 字段名 / 类型 / 中文名 / 备注
MARKER_ROW = 5           # `#data` 标记行
FIRST_DATA_ROW = 6       # 数据从第 6 行开始

SHEETS_OF_FILE = {
    # v2（2026-11-09）：新增 VisualPacks（模型/材质外观包）
    "combat.xlsx": ["Tags", "Objects", "Weapons", "Projectiles", "StatPacks", "VisualPacks", "_Meta"],
    # v4（hero-1）新增：装配 / 变体 / 外观 / 镜头 / 手柄 / 英雄武器
    "characters.xlsx": ["Characters", "StatPacks", "SkillLoads", "Assemblies", "VariantAssembly",
                        "VisualPacks", "CameraPacks", "InputPacks", "Weapons", "_Meta"],
}

# 各主题文件当前的 SchemaVersion（改表结构必须同时改这里 + gen_*.py 的 SCHEMA_VERSION + C# 读取）
SCHEMA_VERSIONS = {
    "combat.xlsx": 2,
    "characters.xlsx": 6,
}

# ★ 外观包表（combat.xlsx 与 characters.xlsx **同名同构**，共用这一份列定义与同一套校验器）。
#   这正是「同一套解析器」的落点：两文件各有一张 VisualPacks，列集合必须一模一样。
VISUAL_PACK_COLUMNS: "list[tuple[str, str, str, str]]" = [
    ("Id", "string", "外观包id", "表现层选模型/材质；本表唯一"),
    ("ModelPath", "string", "模型路径", "res://素材/... ；★ 构建期校验磁盘真实存在"),
    ("Scale", "float", "缩放", "1.0 = 原始大小，必须 > 0"),
    ("TintHex", "string", "染色", "#RRGGBB；空 = 不染色"),
    ("AnimClips", "string", "动画剪辑", "逗号分隔的剪辑名；空 = 模型自带动画全保留"),
    ("Note", "string", "备注", "人读说明"),
]

# characters.xlsx 的表头（保持向后兼容：这张表由 gen_characters_xlsx.py 生成，列清单镜像自它）
CHARACTERS_COLUMNS: "dict[str, list[tuple[str, str, str, str]]]" = {
    "Characters": [
        ("Id", "string", "角色id", "小写下划线，全局唯一"),
        ("DisplayKey", "string", "显示名key", "本地化 key"),
        ("Kind", "enum:ActorKind", "角色类型", "Player/Monster/Elite/Boss/Summon/Destructible"),
        ("DefaultTeam", "enum:TeamId", "默认阵营", "TeamId"),
        ("Control", "enum:ControlScheme", "控制方案", "PlayerInput/Ai/Script"),
        ("BasePackage", "string", "基础数值包", "必填"),
        ("GrowthPackage", "string", "成长数值包", "可空"),
        ("EquipmentPackage", "string", "装备数值包", "可空"),
        ("SkillLoadout", "string", "技能包", "指向 SkillLoads.LoadoutId"),
        ("HitRadius", "float", "受击半径", "米"),
        ("HitHeight", "float", "受击高度", "米"),
        ("VisualSlot", "int", "视图槽位", "表现层用"),
    ],
    "StatPacks": [
        ("PackId", "string", "数值包id", "同 PackId 多行 = 同一层多个数值"),
        ("Layer", "enum:StatLayer", "层", "Base/Growth/Equipment/Buff/Temporary"),
        ("StatId", "enum:StatId", "属性id", "必须在 StatId 枚举内"),
        ("Value", "float", "值", "倍率型默认 1.0"),
    ],
    "SkillLoads": [
        ("LoadoutId", "string", "技能包id", "指向 Characters.SkillLoadout"),
        ("Slot", "enum:SkillSlot", "槽位", "Dash/PrimaryAttack/WeaponSwap/Signature/Interact"),
        ("SkillId", "int", "技能id", "0 = 留空"),
        ("CooldownSeconds", "float", "冷却", "秒"),
        ("ManaCost", "float", "法力消耗", "本行覆盖技能默认消耗"),
        ("Kind", "enum:SkillKind", "实现种类", "fire_projectile / data"),
        ("WeaponId", "string", "武器行id", "哪把枪；非射击技能留空"),
        ("ProjectileId", "string", "子弹外观id", "非射击技能留空；不得与 WeaponId 相同"),
        ("Note", "string", "备注", "说明"),
    ],
    # ── v4 新增（hero-1：变体装配 / 外观 / 镜头 / 手柄 / 英雄武器）──
    "Assemblies": [
        ("Id", "string", "装配id", "一组变体共用的模块集合"),
        ("Note", "string", "备注", "这个装配代表什么"),
        ("Modules", "string", "模块集合", "逗号分隔的 CharacterModuleSet 成员"),
    ],
    "VariantAssembly": [
        ("Id", "string", "变体id", "一行 = 一个可玩变体"),
        ("AssemblyId", "string", "装配id", "指向 Assemblies.Id"),
        ("DisplayName", "string", "显示名", "人读名字"),
        ("VariantTag", "string", "变体标签", "变体分类"),
        ("BasePackage", "string", "基础数值包", "指向 StatPacks.PackId"),
        ("SkillLoadout", "string", "技能包", "指向 SkillLoads.LoadoutId"),
        ("GrowthPackage", "string", "成长数值包", "可空"),
        ("EquipmentPackage", "string", "装备数值包", "可空"),
        ("Weapon", "string", "武器", "指向 Weapons.Id"),
        ("VisualPack", "string", "外观包", "指向 VisualPacks.Id"),
        ("CameraPack", "string", "镜头包", "指向 CameraPacks.Id"),
        ("InputPack", "string", "手柄包", "指向 InputPacks.Id"),
        ("ExtraModules", "string", "附加模块", "额外挂的模块"),
    ],
    "VisualPacks": VISUAL_PACK_COLUMNS,
    "CameraPacks": [
        ("Id", "string", "镜头包id", "指向 VariantAssembly.CameraPack"),
        ("Distance", "float", "距离", "米"),
        ("Height", "float", "高度", "米"),
        ("FollowRatePerSecond", "float", "跟随收敛速率", ">0，每秒（不是每帧 lerp 系数）"),
        ("FovDegrees", "float", "视场角", "米"),
        ("LookAheadRatio", "float", "瞄准偏移幅度", ">=0，按可视高度比例"),
        ("LookAheadMaxMeters", "float", "瞄准偏移上限", ">=0，米（0 = 不封顶）"),
    ],
    "InputPacks": [
        ("Id", "string", "手柄包id", "指向 VariantAssembly.InputPack"),
        ("LookSensitivity", "float", "转向灵敏度", "倍率"),
        ("StickDeadZone", "float", "摇杆死区", "0..1"),
        ("AimAssistStrength", "float", "辅助瞄准强度", "0..1"),
    ],
    "Weapons": [
        ("Id", "string", "武器行id", "指向 SkillLoads.WeaponId"),
        ("WeaponId", "int", "武器数值id", "运行时武器数值槽位"),
        ("Damage", "float", "伤害", "单发伤害"),
        ("BulletSpeed", "float", "弹速", "米/秒"),
        ("RangeMeters", "float", "射程", "米"),
        ("SpreadDegrees", "float", "散布", "度"),
        ("CriticalChance", "float", "暴击率", "0..1"),
        ("CriticalMultiplier", "float", "暴击倍率", ">1"),
        ("ProjectileRadius", "float", "子弹半径", "米"),
        ("ShotCooldown", "float", "射击间隔", "秒"),
        ("PelletsPerShot", "int", "每次弹丸数", ">=1"),
    ],
}

# l10n.xlsx 的列：四行表头约定与其它主题表一致（字段名 / 类型 / 中文名 / 备注）
L10N_COLUMNS = [
    ("Key",  "string", "键",     "actor./item./proj./fx./tag. 前缀，或枚举名"),
    ("Zh",   "string", "中文名", "★ 直接把中文写在这里"),
    ("Note", "string", "备注",   "可空；只在语义容易误解时填"),
]


# ════════════════════════════════════════════════════════════════════════════
# 0. 已知词表（与 C# / 设计口径对齐；新增枚举成员时改这里 + 改生成器的第 2 行类型）
# ════════════════════════════════════════════════════════════════════════════

ENUMS: "dict[str, list[str]]" = {
    "TagCategory": ["Kind", "Stat", "Element", "Ability", "Trigger"],
    # ★ 标签轴：强化 / 图鉴 / 筛选按轴取范围（RuntimeTagAxes 的 TagFamily）
    "TagTier": ["Logic", "Identity"],
    "TagFamily": ["Kind", "WeaponKind", "ProjectileKind", "Element", "Ability", "Trigger",
                  "Rarity", "Race", "Role", "Effect",
                  # 标识层轴
                  "Loot", "Attract", "Faction", "Theme", "Aura", "Craft"],
    "TagGroup": ["None", "ObjectKind", "WeaponKind", "ProjectileKind", "Element", "Ability", "Chain",
                 "Trigger", "Rarity", "Race", "Role", "Effect"],
    "WeaponTrigger": ["Auto", "Semi", "Burst", "Charge", "Hold", "Triggered", "Manual"],
    "WeaponDelivery": ["Instant", "Projectile", "Beam", "Melee", "Aura", "SpawnActor", "Throw"],
    "WeaponAimKind": ["Forward", "AimPoint", "AimLock", "Orbit", "RandomSpread", "Grid",
                      "TargetSeek", "Ground", "ScreenEdge", "AroundSelf", "Cursor"],
    "ProjectileMotion": ["Straight", "Homing", "Orbit", "Wave", "Boomerang", "Falling",
                         "Spiral", "Anchor"],
    "ProjectileHitPolicy": ["Single", "Pierce", "Bounce", "Splash", "Explode", "Chain",
                            "Return", "Ignore"],
    "ProjectileDeath": ["None", "SpawnEffect", "SpawnProjectile", "SpawnActor", "Explode",
                        "Inherit"],
    "DamageType": ["None", "Kinetic", "Energy", "Fire"],
    "TeamId": ["Neutral", "Player", "Enemy"],
    "StatLayer": ["Base", "Growth", "Equipment", "Buff", "Temporary"],
    # StatId 的成员全集由 STAT_ID_VALUES 单独提供（见下），这里只登记名字避免「未登记枚举」误报
    "StatId": [],
    # characters.xlsx 用到
    "ActorKind": ["Player", "Monster", "Elite", "Boss", "Summon", "Destructible"],
    "ControlScheme": ["PlayerInput", "Ai", "Script"],
    "SkillSlot": ["Dash", "PrimaryAttack", "WeaponSwap", "Signature", "Interact"],
    "SkillKind": ["data", "fire_projectile"],
}

# 标签「组 → 轴」的唯一例外映射（其余情况下 Family 必须等于 Group）。
# ObjectKind 组在 TagFamily 里登记为 Kind（枚举里没有 ObjectKind，而「对象种类」就是 Kind 轴）；
# Chain 组属于 Ability 轴（熔炉链递推是一种能力）。
TAG_GROUP_TO_FAMILY: "dict[str, str]" = {
    "ObjectKind": "Kind",
    "Chain": "Ability",
}

# ── 标签分层（Tier）──────────────────────────────────────────────────────────
# 逻辑功能层：决定玩法行为（能力位 / 配方字 / 元素抗性 / 范围索引），代码会消费
# 标识层：只做归属 / 吸引 / 主题 / 共鸣匹配，代码不消费，可以随手加
IDENTITY_FAMILIES = {"Loot", "Attract", "Faction", "Theme", "Aura", "Craft"}
LOGIC_FAMILIES = {
    "Kind", "WeaponKind", "ProjectileKind", "Element", "Ability",
    "Trigger", "Rarity", "Race", "Role", "Effect",
}

def family_tier(family: str) -> str:
    """轴 → 应有的层名。"""
    if family in IDENTITY_FAMILIES:
        return "Identity"
    if family in LOGIC_FAMILIES:
        return "Logic"
    return ""

# StatId 全名集 + 数值。必须与 scripts/core/StatId.cs 一致（另一位队友在该文件里加
# Weapon* / Projectile* 号段）。新增属性时**两处都要加**：StatId.cs 与本文件。
STAT_ID_VALUES: "dict[str, int]" = {
    # [1..99] 生存
    "Hp": 1, "HpMax": 2, "ArmorFlat": 4, "HurtInvulnTime": 8,
    # [100..199] 移动
    "MoveSpeed": 100, "MoveSpeedMul": 101,
    # [200..299] 攻击
    "Damage": 200, "DamageMul": 201, "CritChance": 202, "CritMultiplier": 203,
    # [900..999] 预留
    "Reserved900": 900,
    # [1000+] 角色抽象（已在 StatId.cs）
    "Mana": 1000, "ManaMax": 1001, "ManaRegenPerSec": 1002, "Armor": 1003, "ArmorK": 1004,
    "Strength": 1010, "Dexterity": 1011, "Luck": 1012,
    "StrengthMul": 1013, "DexterityMul": 1014, "LuckMul": 1015,
    "Stamina": 1020, "StaminaMax": 1021, "StaminaRegenPerSec": 1022,
    # hero-1：手感三项（加速度 / 转身 / 瞄准距离），与 StatId.cs 1023~1025 逐项一致
    "MoveAccelTime": 1023, "TurnResponseTime": 1024, "AimRange": 1025,
    # [1030..] 武器号段（本任务新增）
    "WeaponFireRate": 1030, "WeaponDamage": 1031, "WeaponProjectileSpeed": 1032,
    "WeaponProjectileCount": 1033, "WeaponSpread": 1034, "WeaponRange": 1035,
    "WeaponPierce": 1036, "WeaponKnockback": 1037, "WeaponRecoil": 1038,
    "WeaponChargeTime": 1039, "WeaponCritChance": 1040, "WeaponCritMultiplier": 1041,
    # [1050..] 弹体号段（本任务新增）
    "ProjectileLifeTime": 1050, "ProjectileRadius": 1051, "ProjectileBounce": 1052,
    "ProjectileHomingSpeed": 1053, "ProjectileGravity": 1054,
    # [1055..] 冲刺号段（dash-skill，与 StatId.cs 1055/1056 逐项一致）
    "DashSpeedMul": 1055, "DashDuration": 1056,
}
STAT_IDS = set(STAT_ID_VALUES)

RULE_LITERAL_RE = re.compile(r"^-?\d+(?:\.\d+)?$")
RULE_STAT_RE = re.compile(r"^stat:([A-Za-z][A-Za-z0-9_]*)(?:([+-])(\d+(?:\.\d+)?)(%?))?$")
RULE_WSTAT_RE = re.compile(
    r"^wstat:([A-Za-z][A-Za-z0-9_]*)(?:([+-])(\d+(?:\.\d+)?)(%?))?(?:\((-?\d+(?:\.\d+)?)\))?$")
RULE_FLAGS_RE = re.compile(r"^flags:([a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*){0,2})$")
RULE_BARE_RE = re.compile(r"^(chain:depth|chain:count|viable:count|perf)$")
RULE_RAND_RANGE_RE = re.compile(r"^rand:(-?\d+(?:\.\d+)?)~(-?\d+(?:\.\d+)?)$")
RULE_RAND_SCALE_RE = re.compile(r"^rand:(-?\d+(?:\.\d+)?)\+(\d+(?:\.\d+)?)$")

# 规则值列的合理范围：(最小, 最大, 允许等于最小, 允许等于最大, 单位)
# 只对「能算出典型值」的形态检查（字面量 / rand），stat:/wstat:/flags: 运行期才知道。
RULE_RANGES: "dict[str, tuple[float, float, bool, bool, str]]" = {
    "FireInterval": (0.0, 60.0, False, True, " 秒"),
    "BaseDamage": (-1000.0, 100000.0, True, True, " 点"),
    "ProjectileSpeed": (0.0, 1000.0, True, True, " 米/秒"),
    "Range": (0.0, 500.0, True, True, " 米"),
    "SpreadDeg": (0.0, 360.0, True, True, " 度"),
    "Count": (0.0, 100.0, True, True, " 个"),
    "Pierce": (0.0, 100.0, True, True, " 次"),
    "Bounce": (0.0, 100.0, True, True, " 次"),
    "Knockback": (0.0, 1000.0, True, True, " 米/秒"),
    "Recoil": (-100.0, 100.0, True, True, " 米"),
    "HitRadius": (0.0, 50.0, True, True, " 米"),
    "Lifetime": (0.0, 120.0, False, True, " 秒"),
    "ChargeTime": (0.0, 30.0, True, True, " 秒"),
    "Speed": (0.0, 1000.0, True, True, " 米/秒"),
    "Radius": (0.0, 50.0, True, True, " 米"),
    "Damage": (-1000.0, 100000.0, True, True, " 点"),
    "Gravity": (-1000.0, 1000.0, True, True, " 米/秒²"),
    "HomingTurn": (0.0, 3600.0, True, True, " 度/秒"),
}

WEAPON_RULE_FIELDS = ["FireInterval", "BaseDamage", "ProjectileSpeed", "Range", "SpreadDeg",
                      "Count", "Pierce", "Bounce", "Knockback", "Recoil", "HitRadius",
                      "Lifetime", "ChargeTime"]
PROJECTILE_RULE_FIELDS = ["Speed", "Radius", "Lifetime", "Damage", "Pierce", "Bounce",
                          "Gravity", "HomingTurn"]
ID_RE = re.compile(r"^[a-z][a-z0-9_.]{1,47}$")       # 表内业务 Id（Objects/Weapons/… 允许带点）
TAG_ID_RE = re.compile(r"^tag\.[a-z][a-z0-9_]*$")      # Tags.TagId 必须带 `tag.` 前缀
PACK_ID_RE = re.compile(r"^[a-z][a-z0-9_]{1,47}$")     # 数值包 id（与 characters.xlsx 口径一致）


# ════════════════════════════════════════════════════════════════════════════
# 1. 错误收集
# ════════════════════════════════════════════════════════════════════════════

class Report:
    """收集全部错误；`passes` 是「成功过的检查项数」，最后打印成 `OK: N 项检查通过`。"""

    def __init__(self) -> None:
        self.errors: "list[str]" = []
        self.warnings: "list[str]" = []
        self.passes = 0

    def ok(self) -> None:
        self.passes += 1

    def warn(self, where: str, message: str) -> None:
        """不中断构建的提醒（例如「这个簇只有 1 个候选，配簇没意义」）。

        仍然计入 `passes`：warning 是「能跑但很可能不是你的本意」，不是「表坏了」。
        """
        self.warnings.append(f"[提醒] {where}: {message}")
        self.passes += 1

    def err(self, where: str, message: str) -> None:
        self.errors.append(f"[错误] {where}: {message}")

    def cell(self, file: str, sheet: str, row: int, col: int, key: str, field: str,
             message: str) -> None:
        """§9.2 格式：文件/Sheet 第R行C列（Id.字段）: 中文说明。"""
        letter = get_column_letter(col) if col > 0 else "?"
        subject = f"（{key}.{field}）" if key else (f"（.{field}）" if field else "")
        self.errors.append(f"[错误] {file}/{sheet} 第{row}行{letter}列{subject}: {message}")


# ════════════════════════════════════════════════════════════════════════════
# 2. 表读取
# ════════════════════════════════════════════════════════════════════════════

def _blank(v) -> bool:
    return v is None or (isinstance(v, str) and v.strip() == "")


def _s(v) -> str:
    return "" if v is None else str(v).strip()


class Table:
    """一张表：第 1 行字段名 → 列下标；数据行原文（类型转换在各校验函数里做）。"""

    def __init__(self, file: str, name: str, fields: "list[str]",
                 rows: "list[tuple[int, list]]") -> None:
        self.file = file
        self.name = name
        self.fields = fields
        self.rows = rows
        self.col_of = {f: i for i, f in enumerate(fields)}
        self.by_id: "dict[str, tuple[int, list]]" = {}
        for row_no, cells in rows:
            if cells and isinstance(cells[0], str) and cells[0]:
                self.by_id.setdefault(cells[0].strip(), (row_no, cells))

    def cell(self, cells: "list", field: str):
        i = self.col_of.get(field, -1)
        return None if i < 0 or i >= len(cells) else cells[i]

    def col(self, field: str) -> int:
        """1-based 列号（报错用）。"""
        return self.col_of.get(field, -1) + 1

    def at(self, field: str) -> int:
        """0-based 列下标（取 cells 用）。"""
        return self.col_of.get(field, -1)


def read_sheet(report: Report, path: Path, sheet: str,
               expected: "list[tuple[str, str, str, str]]") -> "Table | None":
    """按四行表头约定读一张表；结构错误直接记录并返回 None。"""
    file = path.name
    wb = load_workbook(path, data_only=True)
    if sheet not in wb.sheetnames:
        report.err(f"{file}/{sheet}", "缺少该 Sheet（Sheet 名 = 表名，改名 = 断链）")
        return None
    ws = wb[sheet]

    merged = [str(r) for r in ws.merged_cells.ranges]
    if merged:
        report.err(f"{file}/{sheet}", f"禁止合并单元格（解析器无法可靠还原）：{merged}")
        return None

    n_expected = len(expected)
    expected_fields = [c[0] for c in expected]
    expected_types = [c[1] for c in expected]

    # 第 1 行字段名（只读到期望列数，多余的列单独处理）
    actual_fields = [_s(ws.cell(row=1, column=c).value) for c in range(1, max(n_expected, ws.max_column or 0) + 1)]
    while actual_fields and not actual_fields[-1]:
        actual_fields.pop()

    # ── 结构① 四行表头齐全 ──
    for r in range(HEADER_ROWS):
        if r == 0:
            continue  # 字段名行下面单独做集合比对
        row_vals = [_s(ws.cell(row=r + 1, column=c).value) for c in range(1, n_expected + 1)]
        if not any(row_vals):
            report.err(f"{file}/{sheet}", f"第 {r + 1} 行表头为空（四行表头：字段名/类型/中文名/备注）")
            return None
        empty_cols = [f for f, v in zip(expected_fields, row_vals) if not v]
        if empty_cols:
            report.err(f"{file}/{sheet} 第{r + 1}行",
                       f"以下字段的第 {r + 1} 行表头为空：{empty_cols}")
            return None

    # ── 结构② 字段名集合与期望一致（缺列 / 多列都报错）──
    miss = [f for f in expected_fields if f not in actual_fields]
    extra = [f for f in actual_fields if f not in expected_fields]
    for f in miss:
        report.err(f"{file}/{sheet} 第1行",
                   f"缺少列「{f}」（期望列集合见 tools/data_pipeline/gen_combat_xlsx.py 的 COLUMNS）")
    for f in extra:
        report.err(f"{file}/{sheet} 第1行",
                   f"多出未登记的列「{f}」（新增字段必须同时改：生成器 COLUMNS → C# Def 属性 → ARCHITECTURE）")
    if miss or extra:
        return None

    # ── 结构③ Id 必须在第 1 列（docs/rules/04 §5.1）──
    if expected_fields[0] != "Id" and "Id" in actual_fields:
        report.err(f"{file}/{sheet} 第1行",
                   f"「Id」必须在第 1 列（现在是第 {actual_fields.index('Id') + 1} 列）")
        return None
    if expected_fields[0] == "Id" and actual_fields[0] != "Id":
        report.err(f"{file}/{sheet} 第1行", f"第 1 列必须是「Id」，实际是「{actual_fields[0]}」")
        return None

    # ── 结构④ 类型行与期望一致 + 枚举已登记 ──
    for f, want in zip(expected_fields, expected_types):
        c = actual_fields.index(f) + 1
        got = _s(ws.cell(row=2, column=c).value)
        if got != want:
            report.err(f"{file}/{sheet} 第2行{get_column_letter(c)}列",
                       f"字段「{f}」的类型写作「{got}」，期望「{want}」")
        for enum_name in re.findall(r"enum:(\w+)", want):
            if enum_name not in ENUMS:
                report.err(f"{file}/{sheet} 第2行{get_column_letter(c)}列",
                           f"字段「{f}」引用了未登记的枚举「{enum_name}」（先登记进校验器 ENUMS）")

    # ── 结构⑤ `#data` 标记行 ──
    marker = _s(ws.cell(row=MARKER_ROW, column=1).value)
    if marker != "#data":
        report.err(f"{file}/{sheet} 第{MARKER_ROW}行A列",
                   f"缺少 `#data` 数据起始标记行（期望 A{MARKER_ROW} = \"#data\"，实际 \"{marker}\"）")
        return None
    overflow = [_s(ws.cell(row=MARKER_ROW, column=c).value) for c in range(2, (ws.max_column or 0) + 1)]
    if any(overflow):
        report.err(f"{file}/{sheet} 第{MARKER_ROW}行",
                   f"`#data` 行只允许 A 列有内容，其余列应为空（实际 {overflow}）")

    # ── 读数据行 ──
    rows: "list[tuple[int, list]]" = []
    for r in range(FIRST_DATA_ROW, (ws.max_row or 0) + 1):
        cells = [ws.cell(row=r, column=c).value for c in range(1, n_expected + 1)]
        if all(_blank(v) for v in cells):
            continue
        if _s(cells[0]).startswith("#"):
            continue  # 注释行（docs/rules/04 §5.1）
        for c in range(n_expected + 1, (ws.max_column or 0) + 1):
            if not _blank(ws.cell(row=r, column=c).value):
                report.cell(file, sheet, r, c, _s(cells[0]), "",
                            f"第 {n_expected + 1} 列之后不应有内容（本表只有 {n_expected} 列）")
        if _blank(cells[0]):
            report.cell(file, sheet, r, 1, "", "Id", "「Id」不能为空（整行为空请直接删除该行）")
            continue
        rows.append((r, cells))

    return Table(file, sheet, expected_fields, rows)


# ════════════════════════════════════════════════════════════════════════════
# 3. 类型 / 枚举 / 外键 / 规则值
# ════════════════════════════════════════════════════════════════════════════

def as_int(report: Report, t: Table, row: int, field: str, key: str, v, required: bool = False):
    col = t.col(field)
    if _blank(v):
        if required:
            report.cell(t.file, t.name, row, col, key, field, "必填，不能为空")
        return None
    if isinstance(v, bool):
        report.cell(t.file, t.name, row, col, key, field, f"期望整数，实际是布尔 {v!r}")
        return None
    if isinstance(v, int):
        report.ok()
        return v
    if isinstance(v, float):
        if v.is_integer():
            report.ok()
            return int(v)
        report.cell(t.file, t.name, row, col, key, field, f"期望整数，实际 {v!r}（小数请改用 float 列）")
        return None
    s = _s(v)
    if re.fullmatch(r"[+-]?\d+", s):
        report.ok()
        return int(s)
    report.cell(t.file, t.name, row, col, key, field, f"期望整数，实际 {v!r}")
    return None


def as_float(report: Report, t: Table, row: int, field: str, key: str, v, required: bool = False):
    col = t.col(field)
    if _blank(v):
        if required:
            report.cell(t.file, t.name, row, col, key, field, "必填，不能为空")
        return None
    if isinstance(v, bool):
        report.cell(t.file, t.name, row, col, key, field, f"期望浮点，实际是布尔 {v!r}")
        return None
    if isinstance(v, (int, float)):
        f = float(v)
        if f != f or f in (float("inf"), float("-inf")):
            report.cell(t.file, t.name, row, col, key, field, f"浮点不能是 NaN/Inf，实际 {v!r}")
            return None
        report.ok()
        return f
    s = _s(v)
    if re.fullmatch(r"[+-]?\d+(?:\.\d+)?", s):
        report.ok()
        return float(s)
    report.cell(t.file, t.name, row, col, key, field, f"期望浮点，实际 {v!r}")
    return None


def as_bool(report: Report, t: Table, row: int, field: str, key: str, v):
    if _blank(v):
        return None
    s = _s(v).upper()
    if s in ("TRUE", "FALSE"):
        report.ok()
        return s == "TRUE"
    report.cell(t.file, t.name, row, t.col(field), key, field,
                f"期望布尔（写 TRUE / FALSE），实际 {v!r}")
    return None


def as_enum(report: Report, t: Table, row: int, field: str, key: str, v, enum_name: str):
    if _blank(v):
        return None
    allowed = ENUMS.get(enum_name, [])
    s = _s(v)
    for candidate in allowed:
        if candidate.lower() == s.lower():
            if candidate != s:
                # 大小写不敏感但生成物会规范化：提醒一下，不算失败
                report.ok()
            else:
                report.ok()
            return candidate
    report.cell(t.file, t.name, row, t.col(field), key, field,
                f"「{s}」不是 {enum_name} 的成员（允许：{'/'.join(allowed)}）")
    return None


def as_string(report: Report, t: Table, row: int, field: str, key: str, v, required: bool = False) -> str:
    if _blank(v):
        if required:
            report.cell(t.file, t.name, row, t.col(field), key, field, "必填，不能为空")
        return ""
    if not isinstance(v, str):
        report.cell(t.file, t.name, row, t.col(field), key, field,
                    f"期望字符串，实际是 {type(v).__name__} {v!r}（Id 不能是纯数字）")
        return ""
    report.ok()
    return v.strip()


def as_id(report: Report, t: Table, row: int, field: str, key: str, pattern=None,
          pattern_note: str = "") -> None:
    pat = pattern or ID_RE
    if pat.match(key):
        report.ok()
    else:
        note = pattern_note or "小写字母开头，仅小写字母/数字/下划线（允许 . 分段），2~48 字符"
        report.cell(t.file, t.name, row, t.col(field), key, field,
                    f"Id 格式非法「{key}」（{note}）")


def _suggest(needle: str, pool) -> str:
    """便宜的拼写提示：共同前缀最长且 ≥3 的候选。"""
    nd = needle.lower().replace("-", "_")
    best, best_score = "", 0
    for cand in pool:
        c = cand.lower()
        if c == nd:
            return cand
        prefix = 0
        for a, b in zip(c, nd):
            if a != b:
                break
            prefix += 1
        if prefix >= 3 and prefix > best_score:
            best, best_score = cand, prefix
    return best


def as_ref(report: Report, t: Table, row: int, field: str, key: str, v,
           target: "set[str] | dict[str, object] | None", target_name: str,
           required: bool = False) -> str:
    s = _s(v)
    if not s:
        if required:
            report.cell(t.file, t.name, row, t.col(field), key, field, "必填，不能为空")
        return ""
    if target is None:
        return s
    if s in target:
        report.ok()
        return s
    hint = _suggest(s, target)
    report.cell(t.file, t.name, row, t.col(field), key, field,
                f"引用的「{s}」在 {target_name} 里不存在" + (f"（是否想写 {hint}?）" if hint else ""))
    return s


# ── 规则值解析 ──────────────────────────────────────────────────────────────

def parse_rule_value(text: str):
    """真解析一条规则值。返回 (错误说明, 形态)。错误说明为空串 = 合法。"""
    if RULE_LITERAL_RE.match(text):
        return "", "literal"

    m = RULE_STAT_RE.match(text)
    if m:
        stat, sign, amount, pct = m.groups()
        if stat not in STAT_IDS:
            hint = _suggest(stat, STAT_IDS)
            return f"stat: 后面的「{stat}」不是已知 StatId" + (f"（是否想写 {hint}?）" if hint else ""), "stat"
        if sign and not pct:
            return (f"stat: 的 ± 是**百分比层**，必须带 %：写 stat:{stat}{sign}{amount}%"
                    f"（想要绝对值请写数字字面量）"), "stat"
        return "", "stat"

    m = RULE_WSTAT_RE.match(text)
    if m:
        stat, sign, amount, pct, _default = m.groups()
        if stat not in STAT_IDS:
            hint = _suggest(stat, STAT_IDS)
            return f"wstat: 后面的「{stat}」不是已知 StatId" + (f"（是否想写 {hint}?）" if hint else ""), "wstat"
        if sign and not pct:
            return f"wstat: 的 ± 加成必须带 %（如 wstat:WeaponDamage+20%）", "wstat"
        return "", "wstat"

    m = RULE_FLAGS_RE.match(text)
    if m:
        return "", "flags"

    if RULE_BARE_RE.match(text):
        return "", "bare"

    m = RULE_RAND_SCALE_RE.match(text)
    if m:
        base, frac = float(m.group(1)), float(m.group(2))
        if not (0.0 <= frac <= 1.0):
            return (f"rand:a+b 的 b 是抖动比例（0~1），{frac:g} 非法；"
                    f"「基础值 ±50%」请写 rand:{m.group(1)}+0.5，"
                    f"「a 到 b 的均匀区间」请写 rand:a~b"), "rand_scale"
        return "", "rand_scale"

    m = RULE_RAND_RANGE_RE.match(text)
    if m:
        lo, hi = float(m.group(1)), float(m.group(2))
        if hi < lo:
            return f"rand:a~b 要求 a<=b（实际 {lo:g}~{hi:g}）", "rand_range"
        return "", "rand_range"

    return (f"无法解析的规则值「{text}」；合法写法：12.5 / -5 / stat:MoveSpeed / "
            f"stat:Damage+20% / wstat:WeaponDamage(5) / flags:pierce / chain:depth / "
            f"chain:count / viable:count / perf / rand:2~3 / rand:10+0.5"), ""


def estimate_rule_value(text: str):
    """规则值的「典型值」（范围检查用）：字面量取字面量，rand 取上界，其余返回 None。"""
    if RULE_LITERAL_RE.match(text):
        return float(text)
    m = RULE_RAND_SCALE_RE.match(text)
    if m:
        base, frac = float(m.group(1)), float(m.group(2))
        return base * (1.0 + frac) if base >= 0 else base * (1.0 - frac)
    m = RULE_RAND_RANGE_RE.match(text)
    if m:
        return float(m.group(2))
    return None


def check_rule_cell(report: Report, t: Table, row: int, field: str, key: str, v,
                    tags: "set[str] | None") -> str:
    """校验一格规则值。返回形态名（空串 = 不合法）。"""
    col = t.col(field)
    if _blank(v):
        report.cell(t.file, t.name, row, col, key, field,
                    "规则值列不能为空；「没有效果」请显式写 0（空 = 未填，0 = 明确为零，语义不同）")
        return ""
    text = _s(v)
    err, kind = parse_rule_value(text)
    if err:
        report.cell(t.file, t.name, row, col, key, field, err)
        return ""
    report.ok()

    if kind == "flags" and tags is not None:
        tag = text[len("flags:"):]
        if tag not in tags:
            hint = _suggest(tag, tags)
            report.cell(t.file, t.name, row, col, key, field,
                        f"flags: 后面的「{tag}」不是 Tags 表里的 TagId"
                        + (f"（是否想写 {hint}?）" if hint else ""))
            return ""
        report.ok()

    est = estimate_rule_value(text)
    rng = RULE_RANGES.get(field)
    if est is not None and rng is not None:
        lo, hi, allow_lo, allow_hi, unit = rng
        bad = (est < lo or est > hi or (est == lo and not allow_lo)
               or (est == hi and not allow_hi))
        if bad:
            lo_txt = ("[" if allow_lo else "(") + f"{lo:g}"
            hi_txt = f"{hi:g}" + ("]" if allow_hi else ")")
            report.cell(t.file, t.name, row, col, key, field,
                        f"典型值 {est:g}{unit} 超出合理范围 {lo_txt}, {hi_txt}（原文「{text}」）")
            return ""
        report.ok()
    return kind


def check_recipe(report: Report, t: Table, row: int, key: str, field: str, v,
                 tags: "dict[str, int]") -> None:
    """Recipe：8 个 TagId 连接成 64 位配方字；空 = 不进熔炉。

    ★ 分隔符：设计稿写「用 . 连接」，但 TagId 本身带 `tag.` 前缀，用 `.` 会与前缀撞车
    （tag.pistol.tag.kinetic 无法切分）。因此**分隔符定为 `.`，但切成 token 时按 `tag.`
    前缀切**：`tag.a.tag.b...` → 8 段。同时兼容显式分隔符 `|`（`tag.a|tag.b|...`），
    推荐后者——它没有歧义，人能一眼数清段数。
    """
    s = _s(v)
    if not s:
        return
    col = t.col(field)
    if "|" in s:
        parts = [p.strip() for p in s.split("|")]
    else:
        m = re.match(r"^((?:tag\.[a-z][a-z0-9_]*)+)$", s)
        parts = re.findall(r"tag\.[a-z][a-z0-9_]*", s) if m else [s]
    if len(parts) != 8:
        report.cell(t.file, t.name, row, col, key, field,
                    f"配方必须是 8 个 TagId（64 位配方字 = 8 段 × 8 位），实际 {len(parts)} 段：「{s}」；"
                    f"推荐用 | 分隔，如 tag.pistol|tag.kinetic|…（共 8 段）")
        return
    bad = [p for p in parts if p not in tags]
    if bad:
        hint = ", ".join(f"{p}→{_suggest(p, tags)}" for p in bad if _suggest(p, tags))
        report.cell(t.file, t.name, row, col, key, field,
                    f"配方里的 TagId 不存在：{bad}" + (f"（是否想写 {hint}）" if hint else ""))
        return
    no_bit = [p for p in parts if tags[p] == -1]
    if no_bit:
        report.cell(t.file, t.name, row, col, key, field,
                    f"配方里的 tag 没有配方位（BitMask = -1，不进配方字）：{no_bit}")
        return
    report.ok()


# ════════════════════════════════════════════════════════════════════════════
# 4. 逐表校验
# ════════════════════════════════════════════════════════════════════════════

def validate_tags(report: Report, t: Table) -> "dict[str, int]":
    """Tags：TagIndex 从 0 连续、BitMask ∈ [0,63]∪{-1} 且非 -1 唯一。返回 {TagId: BitMask}。"""
    seen: "dict[str, int]" = {}
    index_owner: "dict[int, str]" = {}
    mask_owner: "dict[int, str]" = {}
    for row, cells in t.rows:
        key = _s(cells[0])
        as_id(report, t, row, "TagId", key, TAG_ID_RE,
              "TagId 必须形如 tag.<小写名>，如 tag.pistol（小写字母开头，仅小写字母/数字/下划线）")
        if key in seen:
            report.cell(t.file, t.name, row, t.col("TagId"), key, "TagId",
                        f"TagId 重复（第 {seen[key]} 行已出现）")
        else:
            seen[key] = row

        idx = as_int(report, t, row, "TagIndex", key, t.cell(cells, "TagIndex"), required=True)
        if idx is not None:
            if idx < 0:
                report.cell(t.file, t.name, row, t.col("TagIndex"), key, "TagIndex",
                            f"不能为负（实际 {idx}）")
            elif idx in index_owner:
                report.cell(t.file, t.name, row, t.col("TagIndex"), key, "TagIndex",
                            f"TagIndex {idx} 与「{index_owner[idx]}」重复")
            else:
                index_owner[idx] = key
                report.ok()

        as_enum(report, t, row, "Family", key, t.cell(cells, "Family"), "TagFamily")
        as_enum(report, t, row, "Category", key, t.cell(cells, "Category"), "TagCategory")
        as_enum(report, t, row, "Group", key, t.cell(cells, "Group"), "TagGroup")

        mask = as_int(report, t, row, "BitMask", key, t.cell(cells, "BitMask"), required=True)
        if mask is not None:
            if mask == -1:
                report.ok()
            elif 0 <= mask <= 63:
                if mask in mask_owner:
                    report.cell(t.file, t.name, row, t.col("BitMask"), key, "BitMask",
                                f"位 {mask} 已被「{mask_owner[mask]}」占用（非 -1 的 BitMask 必须唯一）")
                else:
                    mask_owner[mask] = key
                    report.ok()
            else:
                report.cell(t.file, t.name, row, t.col("BitMask"), key, "BitMask",
                            f"取值应为 0..63 或 -1，实际 {mask}")

        as_string(report, t, row, "Note", key, t.cell(cells, "Note"))

    # TagIndex 必须正好是 0..n-1 的一个排列
    n = len(t.rows)
    got = set(index_owner)
    expect = set(range(n))
    if got != expect:
        missing = sorted(expect - got)
        out = sorted(got - expect)
        detail = []
        if missing:
            detail.append(f"缺少 {missing}")
        if out:
            detail.append(f"超出范围 {out}")
        report.err(f"{t.file}/{t.name} 第{FIRST_DATA_ROW}行起",
                   f"TagIndex 必须从 0 连续递增、不允许空洞或跳跃（共 {n} 个 tag，应为 0..{n - 1}）："
                   + "，".join(detail))
    else:
        report.ok()

    # ★ 按字段名取 BitMask，不按列号：多轴标签扩展在 Family 处插入了新列，
    #   写死 c[4] 会在列结构变化后静默读成 Category → 全部 tag 变成没有配方位。
    return {_s(c[0]): _int_or(t.cell(c, "BitMask"), -1) for _, c in t.rows}


def check_tag_tiers(report: Report, t: Table) -> None:
    """标签分层纪律：层与轴自洽 + **标识层不得影响玩法**。

    标识层（Loot / Attract / Faction / Theme / Aura / Craft）只用于归属与吸引匹配。
    一旦它占了配方位或产生了能力位，它就偷偷变成了逻辑标签 —— 这里把这条路堵死。
    """
    for row, cells in t.rows:
        key = _s(cells[0])
        tier = _s(t.cell(cells, "Tier"))
        family = _s(t.cell(cells, "Family"))
        group = _s(t.cell(cells, "Group"))
        bitmask = _int_or(t.cell(cells, "BitMask"), -1)

        expected = family_tier(family)
        if expected and tier != expected:
            report.cell(t.file, t.name, row, t.col("Tier"), key, "Tier",
                        f"轴「{family}」属于 {expected} 层，Tier 却写着「{tier}」")

        if tier == "Identity":
            if group != "None":
                report.cell(t.file, t.name, row, t.col("Group"), key, "Group",
                            f"标识层标签的 Group 必须是 None（否则会产出能力位），实际「{group}」")
            if bitmask != -1:
                report.cell(t.file, t.name, row, t.col("BitMask"), key, "BitMask",
                            f"标识层标签的 BitMask 必须是 -1（不得进配方字），实际 {bitmask}")
        report.ok()


def check_tag_families(report: Report, t: Table) -> "dict[str, str]":
    """标签轴一致性（多轴标签扩展）：返回 {TagId: Family}。

    · **Family 与 Group 的关系**：Group 是「实现分组」——`ObjectKind` 组决定 Capability 位；
      Family 是「索引轴」——强化 / 图鉴 / 筛选按轴取范围。同一个 tag 两者必须指向同一件事，
      所以只有**一个**历史遗留例外：`ObjectKind` 组在 TagFamily 里登记为 `Kind`
      （枚举里没有 `ObjectKind` 这个名字，而"对象种类"就是 Kind 轴）。
      `Chain` 组是 Ability 轴的分组（熔炉链递推属于能力）。
    · **同轴互斥**的轴见 <see cref="check_tag_axes_per_object"/>。
    """
    family_of: "dict[str, str]" = {}
    for row, cells in t.rows:
        key = _s(cells[0])
        family = _s(t.cell(cells, "Family"))
        group = _s(t.cell(cells, "Group"))
        family_of[key] = family
        if not family or not group:
            continue

        # ★ 标识层标签**不进 Group↔Family 对照**：它们的 Group 恒为 None（不产出能力位），
        #   而 Family 是标识轴（Loot/Attract/...）。两者本来就是两回事，不该要求相等。
        #   标识层的强约束改由 check_tag_tiers 把关（Group 必须 None、BitMask 必须 -1）。
        if family in IDENTITY_FAMILIES:
            report.ok()
            continue

        expected = TAG_GROUP_TO_FAMILY.get(group, group)
        if family != expected:
            report.cell(t.file, t.name, row, t.col("Family"), key, "Family",
                        f"Family「{family}」与 Group「{group}」不一致：组「{group}」应属于轴「{expected}」"
                        f"（Family 是索引轴，Group 是实现分组，对同一个 tag 必须指向同一件事）")
        else:
            report.ok()
    return family_of


def check_tag_axes_per_object(report: Report, obj_t: Table, tag_family: "dict[str, str]") -> None:
    """每个物体的「同轴互斥」检查。

    唯一性轴（Kind / WeaponKind / ProjectileKind / Element / Rarity / Race / Role / Effect）
    每个物体最多命中一个标签：稀有度不可能既是普通又是稀有，族裔不可能既是哥布林又是史莱姆。
    **Ability 轴刻意不互斥**（穿透 + 弹跳 + 分裂可以叠在同一个弹上），这正是 ECS 组合的意义。
    """
    # Race 是**分层轴**（祖先 tag.humanoid + 具体 tag.goblin 可以同时命中），不在这里；
    # 这样「对人形增伤」能命中哥布林，「对哥布林增伤」只命中哥布林。
    exclusive = {"Kind", "WeaponKind", "ProjectileKind", "Element", "Rarity", "Role", "Effect"}
    for row, cells in obj_t.rows:
        key = _s(cells[0])
        by_family: "dict[str, list[str]]" = {}
        for name in _tag_list(t_cell(obj_t, cells, "Tags")):
            family = tag_family.get(name)
            if family:
                by_family.setdefault(family, []).append(name)
        bad = False
        for family, names in by_family.items():
            if family in exclusive and len(names) > 1:
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("Tags"), key, "Tags",
                            f"同轴「{family}」命中了 {len(names)} 个标签 {names}：该轴是互斥轴，"
                            f"一个物体只能有一个（想表达叠加效果的请用 Ability 轴的标签）")
                bad = True
        if not bad:
            report.ok()


def validate_structure_only(report: Report, t: Table) -> None:
    """结构表（v4 的 Assemblies / VariantAssembly / 各种 Pack / 英雄 Weapons）：只校验结构。

    这些表的业务外键由 C# 加载期校验器（tests/Characters/CharacterXlsxBinder）负责，
    那里的报错能直接指到「缺哪个包」。本函数确认 Id 非空且格式合法。
    """
    for row, cells in t.rows:
        key = _s(cells[0])
        as_id(report, t, row, "Id", key)
        if not key:
            report.cell(t.file, t.name, row, 1, "", "Id", "「Id」不能为空（整行为空请直接删除该行）")
        else:
            report.ok()


def _resolve_asset_path(model_path: str) -> "Path | None":
    """把表里的模型路径解析成磁盘上的真实路径；解析不了返回 None。

    Godot 的 `res://` 就是项目根 → 剥掉前缀后在 PROJECT_ROOT 下找。
    也接受 `素材/...` 这种项目根相对写法（charsacters.xlsx 老数据用过）。
    纯资源名（不含 `/` 也不含 `.glb` 等扩展名）视为「内置名」，无法查磁盘，返回 None。
    """
    p = model_path.strip().replace("\\", "/")
    if p.startswith("res://"):
        p = p[len("res://"):]
    elif p.startswith("user://") or p.startswith("uid://"):
        return None          # 引擎内部路径，不是磁盘文件
    if not p:
        return None
    return PROJECT_ROOT / p


def validate_visual_packs(report: Report, vt: "Table | None") -> "set[str]":
    """外观包表：Scale > 0 / TintHex 形如 #RRGGBB / AnimClips 非空项 / ★ 模型路径必须真实存在。

    这是本轮最关键的一条检查：正是因为「模型路径存在性」从未被校验过，
    `res://素材/Characters/TestHero.glb` 这个根本不存在的路径才能一路潜伏到运行时，
    加载失败后再静默退回白模胶囊，没有任何人发现。
    """
    ids: "set[str]" = set()
    if vt is None:
        return ids

    seen: "dict[str, int]" = {}
    for row, cells in vt.rows:
        key = _s(cells[0])
        as_id(report, vt, row, "Id", key)
        if key in seen:
            report.cell(vt.file, vt.name, row, vt.col("Id"), key, "Id",
                        f"Id 重复（与第 {seen[key]} 行相同）")
        else:
            seen[key] = row
            report.ok()
        ids.add(key)

        # ── 模型路径：★ 磁盘存在性 ──
        model_path = as_string(report, vt, row, "ModelPath", key, t_cell(vt, cells, "ModelPath"),
                               required=True)
        if model_path:
            resolved = _resolve_asset_path(model_path)
            if resolved is None:
                report.err(f"{vt.file}/{vt.name} 第{row}行{get_column_letter(vt.col('ModelPath'))}列"
                           f"（{key}.ModelPath）",
                           f"无法解析为磁盘路径：「{model_path}」"
                           f"（必须是 res://素材/... 形式；引擎内部路径查不了存在性）")
            elif not resolved.is_file():
                # 给出最接近的候选，帮策划一眼看出是打错字还是文件根本没进库
                hint = _suggest_similar_asset(model_path)
                report.err(f"{vt.file}/{vt.name} 第{row}行{get_column_letter(vt.col('ModelPath'))}列"
                           f"（{key}.ModelPath）",
                           f"模型文件在磁盘上不存在：「{model_path}」"
                           f"（已按 res:// = 项目根 解析为「{resolved}」）"
                           f"{hint}")
            else:
                report.ok()

        # ── 缩放必须 > 0 ──
        scale = as_float(report, vt, row, "Scale", key, t_cell(vt, cells, "Scale"), required=True)
        if scale is not None:
            if scale <= 0:
                report.cell(vt.file, vt.name, row, vt.col("Scale"), key, "Scale",
                            f"缩放必须 > 0（实际 {scale:g}）；0 或负数会让模型塌成一点或镜像翻转")
            else:
                report.ok()

        # ── 染色 #RRGGBB（可空）──
        tint = as_string(report, vt, row, "TintHex", key, t_cell(vt, cells, "TintHex"))
        if tint and not re.fullmatch(r"#[0-9A-Fa-f]{6}", tint):
            report.cell(vt.file, vt.name, row, vt.col("TintHex"), key, "TintHex",
                        f"必须是 #RRGGBB 形式（实际「{tint}」）；空 = 不染色")
        else:
            report.ok()

        # ── 动画剪辑：逗号分隔，空项 = 空 = 模型自带动画全保留 ──
        clips = as_string(report, vt, row, "AnimClips", key, t_cell(vt, cells, "AnimClips"))
        if clips:
            bad = [c for c in (x.strip() for x in clips.split(",")) if c and
                   not re.fullmatch(r"[A-Za-z0-9_.|: -]+", c)]
            if bad:
                report.cell(vt.file, vt.name, row, vt.col("AnimClips"), key, "AnimClips",
                            f"以下剪辑名含非法字符：{bad}（允许字母/数字/_ . | : 空格 -）")
            else:
                report.ok()
        else:
            report.ok()

    # 表内「同一路径 + 同一染色 + 同一缩放」的重复包 = 冗余（不算错，只提醒）
    sig: "dict[tuple, str]" = {}
    for row, cells in vt.rows:
        key = _s(cells[0])
        s = (_s(t_cell(vt, cells, "ModelPath")), _s(t_cell(vt, cells, "Scale")),
             _s(t_cell(vt, cells, "TintHex")))
        if s in sig and s[0]:
            report.warn(f"{vt.file}/{vt.name} 第{row}行（{key}）",
                        f"与「{sig[s]}」的 ModelPath/Scale/TintHex 完全相同，"
                        f"两个外观包视觉上一模一样（改 TintHex 或 Scale 才有区分意义）")
        else:
            sig.setdefault(s, key)
        report.ok()
    return ids


def _suggest_similar_asset(model_path: str) -> str:
    """在素材库里找同名/近名的 .glb，帮策划定位打错的路径。"""
    name = _s(model_path).replace("\\", "/").rsplit("/", 1)[-1]
    if not name:
        return ""
    stem = name[:-4] if name.lower().endswith(".glb") else name
    asset_root = PROJECT_ROOT / "素材"
    if not asset_root.is_dir():
        return ""
    exact, same_stem, loose = [], [], []
    for p in asset_root.rglob("*.glb"):
        rel = p.relative_to(PROJECT_ROOT).as_posix()
        if p.name == name:
            exact.append(rel)
        elif p.stem == stem:
            same_stem.append(rel)
        elif stem and stem.lower() in p.stem.lower():
            loose.append(rel)
    if exact:
        return f"；素材库里有同名文件：res://{exact[0]}"
    if same_stem:
        return f"；同名（不同扩展名）：res://{same_stem[0]}"
    if loose:
        return f"；名字相近的候选：res://{loose[0]}"
    if stem:
        return f"；素材库里没有任何文件名包含「{stem}」，请确认资产是否已导入 素材/"
    return ""



    try:
        return int(v)
    except (TypeError, ValueError):
        return default


def _int_or(v, default: int) -> int:
    try:
        return int(v)
    except (TypeError, ValueError):
        return default


def validate_objects(report: Report, t: Table, tags: "dict[str, int]") -> None:
    """Objects 逐行类型 / Tags 存在 / StatPacks 五段格式 / 尺寸。外键与能力位在后面专门校验。"""
    seen: "dict[str, int]" = {}
    for row, cells in t.rows:
        key = _s(cells[0])
        as_id(report, t, row, "Id", key)
        if key in seen:
            report.cell(t.file, t.name, row, t.col("Id"), key, "Id",
                        f"Id 重复（第 {seen[key]} 行已出现）")
        else:
            seen[key] = row

        as_string(report, t, row, "DisplayKey", key, t.cell(cells, "DisplayKey"), required=True)

        names = _tag_list(t.cell(cells, "Tags"))
        bad = False
        for name in names:
            if name not in tags:
                hint = _suggest(name, tags)
                report.cell(t.file, t.name, row, t.col("Tags"), key, "Tags",
                            f"未知 TagId「{name}」" + (f"（是否想写 {hint}?）" if hint else ""))
                bad = True
            else:
                report.ok()
        if len(set(names)) != len(names):
            report.cell(t.file, t.name, row, t.col("Tags"), key, "Tags", f"标签重复：{names}")
            bad = True
        if not names:
            # 允许：纯表现物体（特效）不带任何标签 —— 运行时 Capability 落子为 None。
            report.ok()
        if not bad:
            report.ok()

        as_string(report, t, row, "Cluster", key, t.cell(cells, "Cluster"))

        raw_sp = _s(t.cell(cells, "StatPacks"))
        sp_ok = True
        if raw_sp:
            parts = raw_sp.split(";")
            if len(parts) != 5:
                report.cell(t.file, t.name, row, t.col("StatPacks"), key, "StatPacks",
                            f"必须 5 段（Base;Growth;Equipment;Buff;Temporary），"
                            f"实际 {len(parts)} 段：「{raw_sp}」")
                sp_ok = False
            else:
                report.ok()
                for i, seg in enumerate(parts):
                    seg = seg.strip()
                    if seg and not PACK_ID_RE.match(seg):
                        report.cell(t.file, t.name, row, t.col("StatPacks"), key, "StatPacks",
                                    f"第 {i + 1} 段的包名「{seg}」格式非法")
                        sp_ok = False
        _ = sp_ok

        as_string(report, t, row, "RenderId", key, t.cell(cells, "RenderId"))
        for f in ("Radius", "Height"):
            v = as_float(report, t, row, f, key, t.cell(cells, f))
            if v is not None and v < 0:
                report.cell(t.file, t.name, row, t.col(f), key, f, f"不能为负（实际 {v}）")
            elif v is not None:
                report.ok()
        as_string(report, t, row, "Note", key, t.cell(cells, "Note"))


def validate_weapons(report: Report, t: Table, obj_tags: "dict[str, list[str]]",
                     tags: "dict[str, int]") -> "set[str]":
    seen: "dict[str, int]" = {}
    ids: "set[str]" = set()
    for row, cells in t.rows:
        key = _s(cells[0])
        as_id(report, t, row, "Id", key)
        if key in seen:
            report.cell(t.file, t.name, row, t.col("Id"), key, "Id",
                        f"Id 重复（第 {seen[key]} 行已出现）")
        else:
            seen[key] = row
        ids.add(key)

        obj = as_ref(report, t, row, "ObjectId", key, t.cell(cells, "ObjectId"),
                     obj_tags, "Objects", required=True)
        if obj and obj in obj_tags and "tag.weapon" not in obj_tags[obj]:
            report.cell(t.file, t.name, row, t.col("ObjectId"), key, "ObjectId",
                        f"Objects「{obj}」的 Tags 不含 tag.weapon（实际 {obj_tags[obj]}）")

        as_enum(report, t, row, "Trigger", key, t.cell(cells, "Trigger"), "WeaponTrigger")
        delivery = as_enum(report, t, row, "Delivery", key, t.cell(cells, "Delivery"), "WeaponDelivery")
        as_enum(report, t, row, "Aim", key, t.cell(cells, "Aim"), "WeaponAimKind")

        for f in WEAPON_RULE_FIELDS:
            check_rule_cell(report, t, row, f, key, t.cell(cells, f), tags)

        pref = as_ref(report, t, row, "ProjectileRef", key, t.cell(cells, "ProjectileRef"), obj_tags,
                      "Objects")
        if delivery == "Projectile" and not pref:
            report.cell(t.file, t.name, row, t.col("ProjectileRef"), key, "ProjectileRef",
                        "Delivery=Projectile 时必须填 ProjectileRef（否则打出去的是空子弹）")
        if pref and pref in obj_tags and "tag.projectile" not in obj_tags[pref]:
            report.cell(t.file, t.name, row, t.col("ProjectileRef"), key, "ProjectileRef",
                        f"Objects「{pref}」的 Tags 不含 tag.projectile（实际 {obj_tags[pref]}）")

        as_ref(report, t, row, "ActorRef", key, t.cell(cells, "ActorRef"), obj_tags, "Objects")
        as_ref(report, t, row, "EffectRef", key, t.cell(cells, "EffectRef"), obj_tags, "Objects")

        as_enum(report, t, row, "DamageType", key, t.cell(cells, "DamageType"), "DamageType")
        as_enum(report, t, row, "TeamLayer", key, t.cell(cells, "TeamLayer"), "TeamId")
        as_bool(report, t, row, "InheritWeaponStats", key, t.cell(cells, "InheritWeaponStats"))

        cdl = as_int(report, t, row, "ChainDepthLimit", key, t.cell(cells, "ChainDepthLimit"),
                     required=True)
        if cdl is not None:
            if cdl < 1:
                report.cell(t.file, t.name, row, t.col("ChainDepthLimit"), key, "ChainDepthLimit",
                            f"必须 >= 1（实际 {cdl}）")
            else:
                report.ok()
        shrink = as_float(report, t, row, "ChainShrink", key, t.cell(cells, "ChainShrink"))
        if shrink is not None:
            if not (0.0 <= shrink <= 1.0):
                report.cell(t.file, t.name, row, t.col("ChainShrink"), key, "ChainShrink",
                            f"必须在 0..1（每深一层乘一次，实际 {shrink}）")
            else:
                report.ok()

        check_recipe(report, t, row, key, "Recipe", t.cell(cells, "Recipe"), tags)
        as_string(report, t, row, "Note", key, t.cell(cells, "Note"))
    return ids


def validate_projectiles(report: Report, t: Table, obj_tags: "dict[str, list[str]]",
                         tags: "dict[str, int]") -> "set[str]":
    seen: "dict[str, int]" = {}
    ids: "set[str]" = set()
    for row, cells in t.rows:
        key = _s(cells[0])
        as_id(report, t, row, "Id", key)
        if key in seen:
            report.cell(t.file, t.name, row, t.col("Id"), key, "Id",
                        f"Id 重复（第 {seen[key]} 行已出现）")
        else:
            seen[key] = row
        ids.add(key)

        obj = as_ref(report, t, row, "ObjectId", key, t.cell(cells, "ObjectId"),
                     obj_tags, "Objects", required=True)
        if obj and obj in obj_tags and "tag.projectile" not in obj_tags[obj]:
            report.cell(t.file, t.name, row, t.col("ObjectId"), key, "ObjectId",
                        f"Objects「{obj}」的 Tags 不含 tag.projectile（实际 {obj_tags[obj]}）")

        as_enum(report, t, row, "Motion", key, t.cell(cells, "Motion"), "ProjectileMotion")
        as_enum(report, t, row, "HitPolicy", key, t.cell(cells, "HitPolicy"),
                "ProjectileHitPolicy")
        as_enum(report, t, row, "OnExpire", key, t.cell(cells, "OnExpire"), "ProjectileDeath")

        for f in PROJECTILE_RULE_FIELDS:
            check_rule_cell(report, t, row, f, key, t.cell(cells, f), tags)

        for f in ("OnHitProjectile", "OnHitActor", "OnExpireProjectile", "OnExpireActor"):
            as_ref(report, t, row, f, key, t.cell(cells, f), obj_tags, "Objects")

        mcd = as_int(report, t, row, "MaxChainDepth", key, t.cell(cells, "MaxChainDepth"),
                     required=True)
        if mcd is not None:
            if mcd < 1:
                report.cell(t.file, t.name, row, t.col("MaxChainDepth"), key, "MaxChainDepth",
                            f"必须 >= 1（实际 {mcd}）")
            else:
                report.ok()

        check_recipe(report, t, row, key, "Recipe", t.cell(cells, "Recipe"), tags)
        as_string(report, t, row, "Note", key, t.cell(cells, "Note"))
    return ids


def validate_statpacks(report: Report, t: Table, packs: "dict[str, dict[str, set[str]]]") -> None:
    """StatPacks：StatId 白名单 / Layer 合法 / (PackId, Layer, StatId) 唯一 / **一个 PackId 只能占一层**。

    顺带把结果填进 packs = {PackId: {Layer: {StatId}}}，供外键与能力位检查复用。

    ★ 为什么强制「一个 PackId 只能占一层」：
    运行时的 `StatPackageRepository.Register` 以 **PackId 为唯一键**（`Dictionary<string,
    StatPackageLayer>`），一个 PackId 只能存一层。若表里同一个 PackId 同时出现 Base 与 Growth
    两行，建模会得到两个 `StatPackageLayer`（同 id、不同 layer），注册时直接抛
    「数值包 id 重复」。也就是说这是**数据表达不了的形状**，必须在建表期拦下，
    而不是等到运行期崩在装配阶段（那时离「改了哪行 Excel」已经很远）。

    真要做「同名包分层」，那是数据模型变更：键改成 (PackId, Layer)，同时 `Compose` /
    `Find` / `GetLayerValue` 三个入口一并改。在那之前，**不同层请用不同的 PackId**
    （例如 `hero_base` / `hero_growth`），这也是现有 31 个包的写法。
    """
    seen: "dict[tuple[str, str, str], int]" = {}
    # PackId -> 首次出现的 (Layer, 行号)；用来抓「同 id 跨层」
    layer_of_pack: "dict[str, tuple[str, int]]" = {}
    for row, cells in t.rows:
        pid = _s(cells[0])
        if not pid:
            report.cell(t.file, t.name, row, 1, "", "PackId", "必填，不能为空")
            continue
        as_id(report, t, row, "PackId", pid)

        layer = as_enum(report, t, row, "Layer", pid, t.cell(cells, "Layer"), "StatLayer")

        stat = _s(t.cell(cells, "StatId"))
        if not stat:
            report.cell(t.file, t.name, row, t.col("StatId"), pid, "StatId", "必填，不能为空")
        elif stat not in STAT_IDS:
            hint = _suggest(stat, STAT_IDS)
            report.cell(t.file, t.name, row, t.col("StatId"), pid, "StatId",
                        f"「{stat}」不是已知 StatId（新增属性必须先加进 scripts/core/StatId.cs "
                        f"与本校验器的 STAT_ID_VALUES）" + (f"（是否想写 {hint}?）" if hint else ""))
        else:
            report.ok()

        value = as_float(report, t, row, "Value", pid, t.cell(cells, "Value"), required=True)
        if value is not None:
            report.ok()
            if (stat.endswith("Mul") or "CritMultiplier" in stat) and value == 0:
                report.cell(t.file, t.name, row, t.col("Value"), pid, "Value",
                            f"倍率型属性 {stat} 写成 0 会让结果恒为 0（「无加成」应写 1）")

        k = (pid, layer or _s(t.cell(cells, "Layer")), stat)
        if k in seen:
            report.cell(t.file, t.name, row, t.col("StatId"), pid, "StatId",
                        f"同一 (PackId={k[0]}, Layer={k[1]}, StatId={k[2]}) 重复"
                        f"（第 {seen[k]} 行已出现）")
        else:
            seen[k] = row

        # ★ 一个 PackId 只能占一层（见函数 docstring 的「数据表达不了的形状」说明）
        raw_layer = layer or _s(t.cell(cells, "Layer"))
        if raw_layer:
            first = layer_of_pack.get(pid)
            if first is None:
                layer_of_pack[pid] = (raw_layer, row)
            elif first[0] != raw_layer:
                report.cell(t.file, t.name, row, t.col("Layer"), pid, "Layer",
                            f"PackId「{pid}」已作为 {first[0]} 层出现在第 {first[1]} 行，"
                            f"此处又写成 {raw_layer} 层 —— 同一 PackId 不能跨层"
                            f"（运行期仓库以 PackId 为唯一键，注册时会报「数值包 id 重复」）。"
                            f"请拆成两个 PackId，如「{pid}_base」与「{pid}_{raw_layer.lower()}」")
            else:
                report.ok()

        if layer and stat in STAT_IDS:
            packs.setdefault(pid, {}).setdefault(layer, set()).add(stat)


def validate_camera_packs(report: Report, t: Table) -> None:
    """CameraPacks：Id 唯一 / FollowRatePerSecond > 0 / LookAheadRatio >= 0 / LookAheadMaxMeters >= 0。

    ★ 为什么 FollowRatePerSecond 一定要 > 0：
    代码里 `TopDownCameraRig.Convergence` 用 `1 - exp(-rate * dt)` 计算每帧推进比例。
    rate = 0 时该式恒为 0 —— 相机永远停在原地不跟随，而**不会报任何错**（画面只是不动）。
    这正是本项目反复踩到的「静默失效」类型，必须在建表期拦下。

    ★ 为什么这里卡 <= 0 而不是卡 0..1：
    旧表头写「0~1，越大越跟手」，诱导策划填 0.14。但代码一直把它当**每秒速率**用，
    填 0.14 等于「每秒只收敛 13%」——开局几乎不动，看起来像相机坏了。
    照 0..1 去校验等于把这个 bug 固化下来，所以这里改按物理量纲校验。
    """
    seen: "dict[str, int]" = {}
    for row, cells in t.rows:
        pid = _s(cells[0])
        if not pid:
            report.cell(t.file, t.name, row, 1, "", "Id", "必填，不能为空")
            continue
        as_id(report, t, row, "Id", pid)

        if pid in seen:
            report.cell(t.file, t.name, row, t.col("Id"), pid, "Id",
                        f"镜头包 id 重复（首次出现在第 {seen[pid]} 行）")
        else:
            seen[pid] = row

        rate = as_float(report, t, row, "FollowRatePerSecond", pid,
                        t.cell(cells, "FollowRatePerSecond"))
        if rate is None or rate <= 0:
            raw = t.cell(cells, "FollowRatePerSecond")
            hint = ""
            if rate is not None and 0 < rate < 1:
                hint = ("；这个值看起来像「每帧 lerp 系数」——本列是**每秒**收敛速率，"
                        "填 0.14 会让相机几乎不跟随。镜头手感典型值在 7~14 之间")
            report.cell(t.file, t.name, row, t.col("FollowRatePerSecond"), pid,
                        "FollowRatePerSecond", f"必须 > 0（当前 {raw!r}）" + hint)
        else:
            report.ok()

        ratio = as_float(report, t, row, "LookAheadRatio", pid,
                         t.cell(cells, "LookAheadRatio"))
        if ratio is None or ratio < 0:
            report.cell(t.file, t.name, row, t.col("LookAheadRatio"), pid,
                        "LookAheadRatio",
                        f"必须 >= 0（0 = 不偏移；负值会让镜头往瞄准反方向跑，当前 {t.cell(cells, 'LookAheadRatio')!r}）")
        else:
            report.ok()

        maxm = as_float(report, t, row, "LookAheadMaxMeters", pid,
                        t.cell(cells, "LookAheadMaxMeters"))
        if maxm is None or maxm < 0:
            report.cell(t.file, t.name, row, t.col("LookAheadMaxMeters"), pid,
                        "LookAheadMaxMeters",
                        f"必须 >= 0（0 = 不封顶；当前 {t.cell(cells, 'LookAheadMaxMeters')!r}）")
        else:
            report.ok()


def validate_characters(report: Report, t: Table, packs: "dict[str, dict[str, set[str]]]",
                        loadouts: "dict[str, set[str]]") -> None:
    """characters.xlsx 的 Characters 表（向后兼容）。"""
    seen: "dict[str, int]" = {}
    for row, cells in t.rows:
        key = _s(cells[0])
        as_id(report, t, row, "Id", key)
        if key in seen:
            report.cell(t.file, t.name, row, t.col("Id"), key, "Id",
                        f"Id 重复（第 {seen[key]} 行已出现）")
        else:
            seen[key] = row

        as_string(report, t, row, "DisplayKey", key, t.cell(cells, "DisplayKey"), required=True)
        as_enum(report, t, row, "Kind", key, t.cell(cells, "Kind"), "ActorKind")
        as_enum(report, t, row, "DefaultTeam", key, t.cell(cells, "DefaultTeam"), "TeamId")
        as_enum(report, t, row, "Control", key, t.cell(cells, "Control"), "ControlScheme")

        base = _s(t.cell(cells, "BasePackage"))
        if not base:
            report.cell(t.file, t.name, row, t.col("BasePackage"), key, "BasePackage",
                        "必填，指向 StatPacks 的 Base 层（缺 Base 包 = 装配时被跳过）")
        elif base not in packs or "Base" not in packs[base]:
            report.cell(t.file, t.name, row, t.col("BasePackage"), key, "BasePackage",
                        f"StatPacks 里没有「{base}」的 Base 层")
        else:
            report.ok()

        for f, layer in (("GrowthPackage", "Growth"), ("EquipmentPackage", "Equipment")):
            v = _s(t.cell(cells, f))
            if not v:
                continue
            if v not in packs or layer not in packs[v]:
                report.cell(t.file, t.name, row, t.col(f), key, f,
                            f"StatPacks 里没有「{v}」的 {layer} 层")
            else:
                report.ok()

        loadout = _s(t.cell(cells, "SkillLoadout"))
        if loadout:
            if loadout not in loadouts:
                report.cell(t.file, t.name, row, t.col("SkillLoadout"), key, "SkillLoadout",
                            f"SkillLoads 里没有技能包「{loadout}」")
            else:
                report.ok()

        for f in ("HitRadius", "HitHeight"):
            v = as_float(report, t, row, f, key, t.cell(cells, f))
            if v is not None:
                if v < 0:
                    report.cell(t.file, t.name, row, t.col(f), key, f, f"不能为负（实际 {v}）")
                else:
                    report.ok()
        as_int(report, t, row, "VisualSlot", key, t.cell(cells, "VisualSlot"))


def validate_skillloads(report: Report, t: Table, loadouts: "dict[str, set[str]]") -> None:
    seen: "dict[tuple[str, str], int]" = {}
    for row, cells in t.rows:
        lid = _s(cells[0])
        if not lid:
            report.cell(t.file, t.name, row, 1, "", "LoadoutId", "必填，不能为空")
            continue
        as_id(report, t, row, "LoadoutId", lid)
        slot = as_enum(report, t, row, "Slot", lid, t.cell(cells, "Slot"), "SkillSlot")
        as_int(report, t, row, "SkillId", lid, t.cell(cells, "SkillId"), required=True)
        for f in ("CooldownSeconds", "ManaCost"):
            v = as_float(report, t, row, f, lid, t.cell(cells, f))
            if v is not None:
                if v < 0:
                    report.cell(t.file, t.name, row, t.col(f), lid, f, f"不能为负（实际 {v}）")
                else:
                    report.ok()
        as_string(report, t, row, "Note", lid, t.cell(cells, "Note"))
        if slot:
            k = (lid, slot)
            if k in seen:
                report.cell(t.file, t.name, row, t.col("Slot"), lid, "Slot",
                            f"同一技能包的槽位「{slot}」重复（第 {seen[k]} 行已出现）")
            else:
                seen[k] = row
            loadouts.setdefault(lid, set()).add(slot)


# ════════════════════════════════════════════════════════════════════════════
# 5. 跨表：外键 / 能力位一致 / 引用回环 / 数值包闭合
# ════════════════════════════════════════════════════════════════════════════

def _tag_list(v) -> "list[str]":
    return [s.strip() for s in _s(v).split(",") if s.strip()]


def check_object_refs(report: Report, obj_t: Table, weapon_ids: "set[str]",
                      projectile_obj_ids: "set[str]") -> None:
    """Objects 四个引用列的外键校验。

    · Weapon → Weapons.Id
    · Projectile → **Objects.Id**（不是 Projectiles.Id）：物体原型是唯一真源，
      Projectiles 只是叠加在某个 Objects 上的契约；因此这里既查 Objects 存在性，
      也要求该物体确实被某个 Projectiles 行引用（否则填了也拿不到子弹契约）。
    · Actor / Effect → Objects.Id
    """
    for row, cells in obj_t.rows:
        key = _s(cells[0])
        as_ref(report, obj_t, row, "Weapon", key, t_cell(obj_t, cells, "Weapon"),
               weapon_ids, "Weapons")
        proj = as_ref(report, obj_t, row, "Projectile", key, t_cell(obj_t, cells, "Projectile"),
                      obj_t.by_id, "Objects")
        if proj and proj in obj_t.by_id and proj not in projectile_obj_ids:
            report.cell(obj_t.file, obj_t.name, row, obj_t.col("Projectile"), key, "Projectile",
                        f"Objects「{proj}」没有被任何 Projectiles 行引用（没有子弹契约）")
        as_ref(report, obj_t, row, "Actor", key, t_cell(obj_t, cells, "Actor"),
               obj_t.by_id, "Objects")
        as_ref(report, obj_t, row, "Effect", key, t_cell(obj_t, cells, "Effect"),
               obj_t.by_id, "Objects")


def t_cell(t: Table, cells: "list", field: str):
    return t.cell(cells, field)


def check_capabilities(report: Report, obj_t: Table, weapon_obj: "dict[str, int]",
                       projectile_obj: "dict[str, int]") -> None:
    """能力位一致：Objects.Tags 含 tag.weapon ⇔ 被某 Weapons 行引用；tag.projectile 同理；
    含 tag.actor ⇔ StatPacks 的 Base 段非空。"""
    for row, cells in obj_t.rows:
        key = _s(cells[0])
        names = _tag_list(t_cell(obj_t, cells, "Tags"))
        col = obj_t.col("Tags")
        has_w = "tag.weapon" in names
        has_p = "tag.projectile" in names
        has_a = "tag.actor" in names

        kinds = [n for n in ("tag.actor", "tag.weapon", "tag.projectile") if n in names]
        if len(kinds) > 1:
            report.cell(obj_t.file, obj_t.name, row, col, key, "Tags",
                        f"一个物体不能同时命中多个 ObjectKind 标签：{kinds}"
                        f"（运行时按「命中的 ObjectKind → Capability 位」落子，必须唯一）")
        else:
            report.ok()

        if has_w != (key in weapon_obj):
            if has_w:
                report.cell(obj_t.file, obj_t.name, row, col, key, "Tags",
                            f"带 tag.weapon，但 Weapons 表里没有任何一行的 ObjectId = {key}"
                            f"（这个物体拿不到武器契约）")
            else:
                report.cell(obj_t.file, obj_t.name, row, col, key, "Tags",
                            f"Weapons 表第 {weapon_obj[key]} 行把「{key}」当武器用，"
                            f"但它的 Tags 里没有 tag.weapon")
        else:
            report.ok()

        if has_p != (key in projectile_obj):
            if has_p:
                report.cell(obj_t.file, obj_t.name, row, col, key, "Tags",
                            f"带 tag.projectile，但 Projectiles 表里没有任何一行的 ObjectId = {key}")
            else:
                report.cell(obj_t.file, obj_t.name, row, col, key, "Tags",
                            f"Projectiles 表第 {projectile_obj[key]} 行把「{key}」当子弹用，"
                            f"但它的 Tags 里没有 tag.projectile")
        else:
            report.ok()

        raw_sp = _s(t_cell(obj_t, cells, "StatPacks"))
        parts = raw_sp.split(";") if raw_sp else []
        base = parts[0].strip() if len(parts) == 5 else ""
        if has_a:
            # 只有 tag.actor 的物体以「非空 Base 段」作为「这是个角色」的证据。
            if not base:
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("StatPacks"), key, "StatPacks",
                            "带 tag.actor 的物体必须有非空 Base 段（否则装配出来没血没速度）")
            else:
                report.ok()
        else:
            # 武器/子弹/纯特效也能带 Base 数值包：数值挂在物体上，契约（Weapons/Projectiles）
            # 用 wstat:* 去读它。这是「子弹数值也能引用武器数值」的前提，因此不判错。
            report.ok()


def check_clusters(report: Report, obj_t: Table) -> None:
    """熔炉簇校验（Cluster + ClusterWeight）。

    熔炉按 `Cluster` 分组，在同簇候选里**按 `ClusterWeight` 加权随机**挑一个替换。
    所以一条簇必须满足：
      ① `ClusterWeight` 必填且 > 0（权重 <= 0 的候选永远选不中 / 会让随机数除零）；
      ② 同簇的物件**能力位一致** —— 它们会被互相替换，至少都要带同一个 ObjectKind 标签
         （全 projectile / 全 weapon / 全 actor / 全为纯表现）。混装 = 子弹被换成怪物；
      ③ 单候选簇给 warning（不算错）：一个簇只有 1 个候选时，无论权重多少都是它，玩法上等于没配簇。
    """
    if obj_t.at("Cluster") < 0 or obj_t.at("ClusterWeight") < 0:
        return  # 表结构检查已经报过缺列，这里不重复刷屏

    kinds_of = ("tag.actor", "tag.weapon", "tag.projectile")
    clusters: "dict[str, list[tuple[int, list, str, float | None, tuple]]]" = {}

    for row, cells in obj_t.rows:
        key = _s(cells[0])
        names = _tag_list(t_cell(obj_t, cells, "Tags"))
        capability = tuple(sorted(n for n in kinds_of if n in names))
        cluster = _s(t_cell(obj_t, cells, "Cluster"))

        # ① 权重本身
        w = as_float(report, obj_t, row, "ClusterWeight", key,
                     t_cell(obj_t, cells, "ClusterWeight"), required=True)
        if w is not None and w <= 0:
            report.cell(obj_t.file, obj_t.name, row, obj_t.col("ClusterWeight"), key, "ClusterWeight",
                        f"熔炉权重必须 > 0（实际 {w:g}）；同簇内按权重加权随机，"
                        f"写 0 或负数会让这个候选永远选不中")
        else:
            report.ok()

        if not cluster:
            if w is not None and abs(w - 1.0) > 1e-9:
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("ClusterWeight"), key, "ClusterWeight",
                            f"没有填 Cluster（不参与熔炉），ClusterWeight={w:g} 无意义；建议写 1")
            else:
                report.ok()
            continue

        clusters.setdefault(cluster, []).append((row, cells, key, w, capability))

    # ② 同簇能力位一致 + ③ 单候选 warning
    for cluster, members in sorted(clusters.items()):
        caps = {m[4] for m in members}
        if len(caps) > 1:
            detail = "；".join(
                f"{m[2]}={'/'.join(m[4]) if m[4] else '（纯表现，无 ObjectKind）'}"
                for m in members)
            # 只报一次（挂在最后一个成员行上），否则一个坏簇会刷满整屏 N 行同样的错。
            row, _cells, key, _w, _cap = members[-1]
            report.cell(obj_t.file, obj_t.name, row, obj_t.col("Cluster"), key, "Cluster",
                        f"簇「{cluster}」里的候选能力位不一致，熔炉会把它们互换，"
                        f"但它们的 ObjectKind 不同（{detail}）；"
                        f"同簇必须都是同一种物体（例：全 projectile）")
        else:
            report.ok()

        if len(members) == 1:
            row, _c, key, _w, _cap = members[0]
            report.warn(f"{obj_t.file}/{obj_t.name} 第{row}行{get_column_letter(obj_t.col('Cluster'))}列"
                        f"（{key}.Cluster）",
                        f"簇「{cluster}」只有 1 个候选，"
                        f"加权随机的结果恒定，配簇没有意义（可留空或再补候选）")
        else:
            report.ok()


def check_object_visuals(report: Report, obj_t: Table, combat_vp: "set[str]",
                         chars_vp: "set[str]") -> None:
    """Objects 的三个外观列：VisualPack 外键闭合 + AssetPath 磁盘存在 + AssetAnim 非空即合法。

    `VisualPack` 可以指向 **两个文件里任意一张** VisualPacks（两张表同名同构），
    所以外键闭合要把 combat.xlsx 与 characters.xlsx 的 Id 合起来查。
    """
    if obj_t.at("VisualPack") < 0:
        return
    all_packs = combat_vp | chars_vp

    for row, cells in obj_t.rows:
        key = _s(cells[0])

        # ── VisualPack → VisualPacks.Id（可空）──
        pack = as_string(report, obj_t, row, "VisualPack", key, t_cell(obj_t, cells, "VisualPack"))
        if pack:
            if pack not in all_packs:
                hint = ""
                lowered = pack.lower()
                near = sorted(p for p in all_packs if lowered in p.lower() or p.lower() in lowered)[:3]
                if near:
                    hint = f"（是否想写：{' / '.join(near)}）"
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("VisualPack"), key, "VisualPack",
                            f"引用的外观包「{pack}」在 combat.xlsx 与 characters.xlsx 的 "
                            f"VisualPacks 里都不存在{hint}")
            else:
                report.ok()

        # ── AssetPath：res:// 路径必须真实存在（可空）──
        asset = as_string(report, obj_t, row, "AssetPath", key, t_cell(obj_t, cells, "AssetPath"))
        if asset:
            resolved = _resolve_asset_path(asset)
            if resolved is None:
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("AssetPath"), key, "AssetPath",
                            f"无法解析为磁盘路径：「{asset}」（必须 res://素材/... 形式）")
            elif not resolved.is_file():
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("AssetPath"), key, "AssetPath",
                            f"模型文件在磁盘上不存在：「{asset}」"
                            f"（已解析为「{resolved}」）{_suggest_similar_asset(asset)}")
            else:
                report.ok()
        else:
            report.ok()

        # ── AssetAnim：有动画名就必须有地方放这个模型 ──
        anim = as_string(report, obj_t, row, "AssetAnim", key, t_cell(obj_t, cells, "AssetAnim"))
        if anim:
            if not pack and not asset:
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("AssetAnim"), key, "AssetAnim",
                            f"填了默认动画「{anim}」，但 VisualPack 与 AssetPath 都空 —— "
                            f"没有模型可播这段动画（应留空或先配模型）")
            else:
                report.ok()
        else:
            report.ok()


def check_reference_cycles(report: Report, obj_t: Table) -> None:
    """Objects 的 Weapon/Projectile/Actor/Effect 四列构成的有向图不能有环。

    「高自由度拼装」允许任意组合（子弹带武器、子弹里出怪、怪拿起子弹…），但环会让加载期
    构造器无限递归、熔炉无限递推，属于构建期必须挡住的结构错误。
    """
    edges: "dict[str, list[tuple[str, int, str]]]" = {}
    for row, cells in obj_t.rows:
        key = _s(cells[0])
        for f in ("Weapon", "Projectile", "Actor", "Effect"):
            v = _s(t_cell(obj_t, cells, f))
            if v:
                edges.setdefault(key, []).append((v, row, f))

    WHITE, GREY, BLACK = 0, 1, 2
    color: "dict[str, int]" = {}
    stack: "list[str]" = []

    def dfs(node: str):
        """返回 (环, 触发行, 触发字段) 或 None。"""
        color[node] = GREY
        stack.append(node)
        for nxt, row, field in edges.get(node, []):
            if nxt not in edges:
                continue
            c = color.get(nxt, WHITE)
            if c == GREY:
                i = stack.index(nxt) if nxt in stack else 0
                return stack[i:] + [nxt], row, field
            if c == WHITE:
                got = dfs(nxt)
                if got:
                    return got
        stack.pop()
        color[node] = BLACK
        return None

    for node in list(edges):
        if color.get(node, WHITE) == WHITE:
            got = dfs(node)
            if got:
                chain, row, field = got
                key = _s(_row_cells(obj_t, row)[0]) if row else ""
                report.cell(obj_t.file, obj_t.name, row, obj_t.col(field), key, field,
                            f"引用成环：{' → '.join(chain)}"
                            f"（加载期构造器会无限递归；把环上某一条引用清空即可）")
                return
    report.ok()


def _row_cells(obj_t: Table, row: int) -> "list":
    for r, cells in obj_t.rows:
        if r == row:
            return cells
    return [""]


def check_object_statpacks(report: Report, obj_t: Table,
                           char_packs: "dict[str, dict[str, set[str]]]",
                           combat_packs: "dict[str, dict[str, set[str]]]") -> None:
    """Objects.StatPacks 五段里的每个非空包名，必须在 characters.xlsx / combat.xlsx 的 StatPacks 里存在。"""
    known = set(char_packs) | set(combat_packs)
    for row, cells in obj_t.rows:
        key = _s(cells[0])
        parts = _s(t_cell(obj_t, cells, "StatPacks")).split(";")
        if len(parts) != 5:
            continue  # 已由逐行校验报过
        for i, seg in enumerate(parts):
            seg = seg.strip()
            if not seg:
                continue
            if seg in known:
                report.ok()
            else:
                hint = _suggest(seg, known)
                report.cell(obj_t.file, obj_t.name, row, obj_t.col("StatPacks"), key, "StatPacks",
                            f"第 {i + 1} 段的数值包「{seg}」在 characters.xlsx / combat.xlsx 的 "
                            f"StatPacks 里都不存在" + (f"（是否想写 {hint}?）" if hint else ""))


def check_weapon_has_base_pack(report: Report, w_t: Table, obj_t: Table,
                               char_packs: "dict[str, dict[str, set[str]]]",
                               combat_packs: "dict[str, dict[str, set[str]]]") -> None:
    """武器应有 Base 数值包（wstat:* 的落点）；缺 Base 时 wstat 全部回落默认值，多半是漏填。"""
    known = set(char_packs) | set(combat_packs)
    for row, cells in w_t.rows:
        key = _s(cells[0])
        obj = _s(t_cell(w_t, cells, "ObjectId"))
        if obj not in obj_t.by_id:
            continue
        parts = _s(t_cell(obj_t, obj_t.by_id[obj][1], "StatPacks")).split(";")
        base = parts[0].strip() if len(parts) == 5 else ""
        if not base:
            report.cell(w_t.file, w_t.name, row, w_t.col("ObjectId"), key, "ObjectId",
                        f"Objects「{obj}」没有 Base 数值包：本行所有 wstat:* 都会回落到括号默认值")
        elif base in known:
            report.ok()


def check_projectile_has_base_pack(report: Report, p_t: Table, obj_t: Table,
                                   char_packs: "dict[str, dict[str, set[str]]]",
                                   combat_packs: "dict[str, dict[str, set[str]]]") -> None:
    """子弹应有 Base 数值包（ProjectileLifeTime / Damage … 的落点）。"""
    known = set(char_packs) | set(combat_packs)
    for row, cells in p_t.rows:
        key = _s(cells[0])
        obj = _s(t_cell(p_t, cells, "ObjectId"))
        if obj not in obj_t.by_id:
            continue
        parts = _s(t_cell(obj_t, obj_t.by_id[obj][1], "StatPacks")).split(";")
        base = parts[0].strip() if len(parts) == 5 else ""
        if not base and any(k.startswith("Projectile") for k in known):
            report.cell(p_t.file, p_t.name, row, p_t.col("ObjectId"), key, "ObjectId",
                        f"Objects「{obj}」没有 Base 数值包：弹体数值（寿命/半径/弹跳）无处可填")
        else:
            report.ok()


# ════════════════════════════════════════════════════════════════════════════
# 6. 主流程
# ════════════════════════════════════════════════════════════════════════════

def load_columns():
    """从生成器 import 列清单（唯一来源），避免「生成器列」「校验器列」两处漂移。"""
    spec = importlib.util.spec_from_file_location(
        "gen_combat_xlsx", PIPELINE_DIR / "gen_combat_xlsx.py")
    if spec is None or spec.loader is None:
        raise RuntimeError("找不到 tools/data_pipeline/gen_combat_xlsx.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def check_sheet_names(report: Report, path: Path) -> None:
    """每张 Sheet 都必须登记过，_Meta 必须在（文档里没有第五种主题文件）。"""
    file = path.name
    wanted = SHEETS_OF_FILE.get(file)
    if wanted is None:
        report.err(file, "未登记的 Excel 主题文件（docs/architecture/ARCHITECTURE.md §4.3 只有 "
                         "characters/combat/presentation/progression 四个主题文件）")
        return
    wb = load_workbook(path, read_only=True, data_only=True)
    names = list(wb.sheetnames)
    wb.close()
    for name in wanted:
        if name not in names:
            report.err(f"{file}", f"缺少 Sheet「{name}」（Sheet 名 = 表名）")
        else:
            report.ok()
    extra = [n for n in names if n not in wanted]
    if extra:
        report.err(f"{file}", f"出现未登记的 Sheet {extra}（新表必须先登记进 ARCHITECTURE §4.3 与本校验器）")


def check_meta(report: Report, path: Path, expected_version: int) -> None:
    file = path.name
    wb = load_workbook(path, data_only=True)
    if "_Meta" not in wb.sheetnames:
        return  # 已由 check_sheet_names 报过
    ws = wb["_Meta"]
    kv: "dict[str, object]" = {}
    for row in ws.iter_rows(min_row=2, max_col=2, values_only=True):
        if row and row[0]:
            kv[_s(row[0])] = row[1]
    for required in ("SchemaVersion", "Owner", "Source"):
        if required not in kv:
            report.err(f"{file}/_Meta", f"缺少键「{required}」")
        else:
            report.ok()
    if "SchemaVersion" in kv:
        try:
            v = int(kv["SchemaVersion"])
        except (TypeError, ValueError):
            report.err(f"{file}/_Meta", f"SchemaVersion 必须是整数，实际 {kv['SchemaVersion']!r}")
            return
        if v != expected_version:
            report.err(f"{file}/_Meta",
                       f"SchemaVersion = {v}，管线期望 {expected_version}"
                       f"（版本不符必须显式升级并同步 C# SchemaVersions）")
        else:
            report.ok()


def _l10n_namespace(key: str) -> str:
    """给 key 分命名空间，决定「同名中文」要不要报提醒。

    `tag.*` → 标签（同名 = 玩家真分不清）
    `actor./item./proj./fx.*` → 物件（同名 = 两张卡长得一样）
    裸标识（枚举名）→ 枚举（与标签同名是正常的，如 Pierce 与 tag.pierce 都叫"穿透"）
    """
    for p in ("tag.", "actor.", "item.", "proj.", "fx."):
        if key.startswith(p):
            return "object" if p != "tag." else "tag"
    return "enum"


def validate_l10n(report: Report, path: Path,
                  referenced: "dict[str, str]") -> None:
    """校验 `l10n.xlsx`：中文名唯一、非空、且**覆盖所有被引用的 key**。

    这张表很容易出两类静默问题：
      · `Key` 重复 —— openpyxl 建字典时后一条覆盖前一条，图鉴会显示其中一个，没人发现；
      · 中文名留空 / 只填空格 —— 图鉴退化成显示英文 key，看起来「像还没做」而不是「填错了」。

    `referenced` = {key: 谁引用它}，用来报「缺中文名」时告诉策划到底缺哪条。
    """
    file = path.name
    if not path.exists():
        report.err(file, "缺少 l10n.xlsx（全项目中文名真源；图鉴与游戏 UI 都读它）")
        return
    t = read_sheet(report, path, "L10n", L10N_COLUMNS)
    if t is None:
        return

    seen: "dict[str, int]" = {}
    zh_owner: "dict[str, str]" = {}   # 中文名 → 第一次占用它的 key
    for row, cell in t.rows:
        key = _s(t_cell(t, cell, "Key"))
        zh = _s(t_cell(t, cell, "Zh"))
        if not key:
            continue
        if key in seen:
            report.err(f"{file}/L10n 第{row}行", f"「Key」重复：{key}（首次出现在第 {seen[key]} 行）")
        else:
            seen[key] = row
            report.ok()
        if not zh.strip():
            report.err(f"{file}/L10n 第{row}行", f"「Zh」为空：{key}（不会显示名，图鉴会退回英文 key）")
        else:
            report.ok()
        # 中文名重名：只在**同一命名空间内**才算问题。
        #   · 标签之间同名 → 玩家真的分不清（两个"近战"）
        #   · 枚举与标签同名 → 正常且必要（Pierce 与 tag.pierce 都该叫"穿透"）
        #   · 物件同名 → 两张卡片名字一样（两个"制式手枪"）
        # 所以按命名空间分桶，不同桶之间不比较。
        ns = _l10n_namespace(key)
        if zh:
            owner_key = zh_owner.get(zh)
            if owner_key is not None and _l10n_namespace(owner_key) == ns:
                report.warn(f"{file}/L10n 第{row}行",
                            f"中文名「{zh}」与 {owner_key} 相同（同一类命名空间里重名，玩家会分不清）")
            elif owner_key is None:
                zh_owner[zh] = key
        # 中文名里混进英文 id 写法，多半是复制粘贴没改
        if zh and re.fullmatch(r"[A-Za-z0-9_.]+", zh):
            report.warn(f"{file}/L10n 第{row}行",
                        f"中文名「{zh}」看起来仍是英文标识，是否忘了翻译")

    # ── 覆盖性：被引用的 key 必须都有中文名 ──
    for key in sorted(referenced):
        if key not in seen:
            report.err(f"{file}/L10n", f"缺少中文名：{key}（被 {referenced[key]} 引用）")
        else:
            report.ok()


def collect_l10n_refs(combat: Path, chars: Path) -> "dict[str, str]":
    """扫出「图鉴与 UI 会显示、因此必须有中文名」的全部 key。

    只收**确定会显示**的：DisplayKey 与 Tags.TagId。枚举名与人物武器 id 由图鉴的
    `l10n.missing` 记账兜底（那两者是软引用，加一个新枚举不该让构建失败）。
    """
    refs: "dict[str, str]" = {}
    wb = load_workbook(combat, data_only=True)

    def hdr_of(ws) -> "dict[str, int]":
        return {_s(ws.cell(row=1, column=c).value): c
                for c in range(1, ws.max_column + 1) if ws.cell(row=1, column=c).value}

    if "Objects" in wb.sheetnames:
        ws = wb["Objects"]
        hdr = hdr_of(ws)
        if "DisplayKey" in hdr:
            for r in range(6, ws.max_row + 1):
                dk = _s(ws.cell(row=r, column=hdr["DisplayKey"]).value)
                if dk:
                    refs.setdefault(dk, f"Objects.{_s(ws.cell(row=r, column=hdr.get('Id', 1)).value)}")

    if "Tags" in wb.sheetnames:
        ws = wb["Tags"]
        hdr = hdr_of(ws)
        if "TagId" in hdr:
            for r in range(6, ws.max_row + 1):
                tid = _s(ws.cell(row=r, column=hdr["TagId"]).value)
                if tid:
                    refs.setdefault(tid, f"Tags.{tid}")

    wb2 = load_workbook(chars, data_only=True)
    if "Characters" in wb2.sheetnames:
        ws = wb2["Characters"]
        hdr = hdr_of(ws)
        if "Id" in hdr:
            for r in range(6, ws.max_row + 1):
                cid = _s(ws.cell(row=r, column=hdr["Id"]).value)
                if not cid:
                    continue
                dk = _s(ws.cell(row=r, column=hdr["DisplayKey"]).value) if "DisplayKey" in hdr else ""
                # 人物表里 DisplayKey 可能为空 → 按约定回退查 actor.<Id>
                refs.setdefault(dk or f"actor.{cid}", f"Characters.{cid}")
    return refs


def main(argv: "list[str]") -> int:
    report = Report()
    gen = load_columns()
    combat_path = EXCEL_DIR / "combat.xlsx"
    chars_path = EXCEL_DIR / "characters.xlsx"

    if not combat_path.exists():
        print(f"[错误] 找不到 {combat_path}；请先运行 tools/data_pipeline/gen_combat_xlsx.py",
              file=sys.stderr)
        return 1
    if not chars_path.exists():
        print(f"[错误] 找不到 {chars_path}", file=sys.stderr)
        return 1

    for path in (combat_path, chars_path):
        check_sheet_names(report, path)
        check_meta(report, path, SCHEMA_VERSIONS.get(path.name, gen.SCHEMA_VERSION))

    # ── l10n.xlsx：中文名（图鉴与游戏 UI 的唯一真源）──
    # 覆盖性检查放在最后做：需要先知道 combat/characters 里引用了哪些 key。
    l10n_path = EXCEL_DIR / "l10n.xlsx"
    l10n_refs = collect_l10n_refs(combat_path, chars_path)

    # ── combat.xlsx ──
    tags_t = read_sheet(report, combat_path, "Tags", gen.COLUMNS["Tags"])
    obj_t = read_sheet(report, combat_path, "Objects", gen.COLUMNS["Objects"])
    w_t = read_sheet(report, combat_path, "Weapons", gen.COLUMNS["Weapons"])
    p_t = read_sheet(report, combat_path, "Projectiles", gen.COLUMNS["Projectiles"])
    csp_t = read_sheet(report, combat_path, "StatPacks", gen.COLUMNS["StatPacks"])
    cvp_t = read_sheet(report, combat_path, "VisualPacks", gen.COLUMNS["VisualPacks"])

    # ── characters.xlsx（向后兼容：四个业务 Sheet 一起校验）──
    ct_t = read_sheet(report, chars_path, "Characters", CHARACTERS_COLUMNS["Characters"])
    chsp_t = read_sheet(report, chars_path, "StatPacks", CHARACTERS_COLUMNS["StatPacks"])
    sl_t = read_sheet(report, chars_path, "SkillLoads", CHARACTERS_COLUMNS["SkillLoads"])

    # v4 结构表：read_sheet 已覆盖「列集合 + 类型 + 枚举」，这里再确认 Id 合法
    chars_vp_t = None
    chars_cam_t = None
    for name in ("Assemblies", "VariantAssembly", "VisualPacks", "CameraPacks", "InputPacks", "Weapons"):
        v4_t = read_sheet(report, chars_path, name, CHARACTERS_COLUMNS[name])
        if v4_t is not None:
            # VisualPacks 在下面单独走 validate_visual_packs（要查磁盘存在性 + Scale/TintHex）
            if name == "VisualPacks":
                chars_vp_t = v4_t
            elif name == "CameraPacks":
                # 镜头包除结构外还有手感参数的量纲约束（收敛速率 > 0 等）
                chars_cam_t = v4_t
                validate_structure_only(report, v4_t)
                validate_camera_packs(report, v4_t)
            else:
                validate_structure_only(report, v4_t)

    # ── 外观包（两张同名同构表共用同一套校验器，含 ★ 模型路径磁盘存在性）──
    combat_vp_ids = validate_visual_packs(report, cvp_t)
    chars_vp_ids = validate_visual_packs(report, chars_vp_t)

    if any(t is None for t in (tags_t, obj_t, w_t, p_t, csp_t, ct_t, chsp_t, sl_t)):
        return _finish(report)

    tags = validate_tags(report, tags_t)
    tag_family = check_tag_families(report, tags_t)
    check_tag_tiers(report, tags_t)
    obj_tag_map = {_s(c[0]): _tag_list(t_cell(obj_t, c, "Tags")) for _, c in obj_t.rows}

    validate_objects(report, obj_t, tags)
    weapon_ids = validate_weapons(report, w_t, obj_tag_map, tags)
    projectile_ids = validate_projectiles(report, p_t, obj_tag_map, tags)

    char_packs: "dict[str, dict[str, set[str]]]" = {}
    combat_packs: "dict[str, dict[str, set[str]]]" = {}
    validate_statpacks(report, csp_t, combat_packs)
    validate_statpacks(report, chsp_t, char_packs)
    loadouts: "dict[str, set[str]]" = {}
    validate_skillloads(report, sl_t, loadouts)
    validate_characters(report, ct_t, char_packs, loadouts)

    # ── 跨表 ──
    weapon_obj = {_s(t_cell(w_t, c, "ObjectId")): r for r, c in w_t.rows
                  if _s(t_cell(w_t, c, "ObjectId"))}
    projectile_obj = {_s(t_cell(p_t, c, "ObjectId")): r for r, c in p_t.rows
                      if _s(t_cell(p_t, c, "ObjectId"))}
    check_object_refs(report, obj_t, weapon_ids, set(projectile_obj))
    check_capabilities(report, obj_t, weapon_obj, projectile_obj)
    check_tag_axes_per_object(report, obj_t, tag_family)
    check_clusters(report, obj_t)
    check_object_visuals(report, obj_t, combat_vp_ids, chars_vp_ids)
    check_reference_cycles(report, obj_t)
    check_object_statpacks(report, obj_t, char_packs, combat_packs)
    check_weapon_has_base_pack(report, w_t, obj_t, char_packs, combat_packs)
    check_projectile_has_base_pack(report, p_t, obj_t, char_packs, combat_packs)

    # ── l10n.xlsx：中文名（放最后，此时引用清单才完整）──
    validate_l10n(report, l10n_path, l10n_refs)

    return _finish(report)


def _finish(report: Report) -> int:
    if report.errors:
        for e in report.errors:
            print(e)
        for w in report.warnings:
            print(w)
        print(f"共 {len(report.errors)} 个错误，构建中止（data/excel）")
        return 1
    # 提醒不中断构建，但一定要让策划看见。★ 先逐条列提醒，最后一行固定是 `OK: …`，
    # 这样 `... | Select-Object -Last 2` 之类的取尾用法永远拿到「提醒 + 结论」，不会被截断。
    for w in report.warnings:
        print(w)
    print(f"OK: {report.passes} 项检查通过")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
