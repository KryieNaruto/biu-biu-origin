"""生成 data/excel/combat.xlsx —— 战斗内容的「一张表装全部配方」数据真源（v1）。

为什么用脚本生成 Excel 而不是让策划手动建：表头 4 行（字段名/类型/中文名/备注）+ 第 5 行
`#data` 起始标记是本项目的硬约定（docs/rules/04-data-driven.md §5.2），手搓容易漏；脚本生成保证
第一次就合规。**重复运行结果一致（幂等）**：所有 Sheet 都从同一份纯数据字面量重建。

生成之后 **Excel 就是唯一真源**：改数值请直接改 xlsx（或改本脚本后重跑 gen 再覆盖）。
禁止手改 data/generated/。

Sheets（Sheet 名 = 表名，见 docs/architecture/ARCHITECTURE.md §4.3）:
  Tags        位矩阵的列定义 = 「属性/标签字典」（TagId / TagIndex / Category / Group / BitMask）
  Objects     唯一的「物体原型」表：人物 / 怪物 / 武器 / 子弹都在这里（这就是「同源」）
  Weapons     武器契约：怎么发射、发射什么、命中干什么（数值 = 规则值，可引用 stat / wstat / flags…）
  Projectiles 子弹契约：怎么飞、打到什么、死去变成什么（数值同样是规则值）
  StatPacks   补齐 Objects 引用到的武器/子弹数值包（与 characters.xlsx 的 StatPacks 同构同列）
  _Meta       SchemaVersion / Owner / Source / Updated

设计要点（给后面接手的程序看）:
  · Objects 的 Weapon/Projectile/Actor/Effect 四个引用**谁都可以指向任何 Objects.Id**，这是
    「高自由度拼装」的全部机制：子弹里可以喷出怪物、子弹可以带武器属性反过来再开一枪。
    因此「谁引用谁」是**任意有向图**，不允许回环（校验器会查），但环以外的组合随便拼。
  · Weapons.ProjectileRef / Projectiles.OnHit* 指向的是 **Objects.Id**（不是 Weapons.Id / 自己的 Id），
    因为运行时拿到的永远是物体原型；Weapons/Projectiles 两张表只是「契约叠加」。
  · 规则值列（见 README「规则值语法」）是字符串，统一由 validate_content_xlsx.py 真解析。
"""
from __future__ import annotations

import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.utils import get_column_letter

ROOT = Path(__file__).resolve().parents[2] / "data" / "excel"
OUT = ROOT / "combat.xlsx"

# v2（2026-11-09）：新增 VisualPacks 表；Objects 追加 VisualPack / AssetPath / AssetAnim 三列。
SCHEMA_VERSION = 2

HEADER_FILL = PatternFill("solid", fgColor="1F4E78")
NOTE_FILL = PatternFill("solid", fgColor="DDEBF7")
TYPE_FILL = PatternFill("solid", fgColor="F2F2F2")

# ── 列清单（第 1 行字段名, 第 2 行类型, 第 3 行中文名, 第 4 行备注）──────────────────
# ★ 这里是字段名的唯一来源：校验器 validate_content_xlsx.py 从本文件 import 这几张表，
#   保证「生成器写的列」与「校验器要求的列」永远一致，不会两处漂移。
COLUMNS: "dict[str, list[tuple[str, str, str, str]]]" = {
    "Tags": [
        ("TagId", "string", "标签id", "小写下划线，如 tag.pistol；= 运行时 tag 名的唯一来源"),
        ("TagIndex", "int", "标签下标", "★ 必须从 0 连续递增，= 运行时 tag id（位矩阵的行下标）"),
        ("Tier", "enum:TagTier", "标签层", "★ Logic=逻辑功能层（决定玩法行为，代码会消费，加之前先想规则）；"
                                            "Identity=标识层（只做归属/吸引/主题匹配，代码不消费，可以随手加）"),
        ("Family", "enum:TagFamily", "标签轴", "★ 标签的「轴」（Kind/WeaponKind/Element/Ability/Role/Rarity/Race/Effect）；"
                                        "强化/图鉴/筛选按轴取范围，同轴只能命中一个的轴是 Group 的延伸"),
        ("Category", "enum:TagCategory", "类别", "Kind/Stat/Element/Ability/Trigger"),
        ("Group", "enum:TagGroup", "分组", "ObjectKind/WeaponKind/ProjectileKind/Element/Ability/Chain/Trigger/"
                                      "Role/Rarity/Race/Effect；运行时按「所有 ObjectKind 组 → Capability 位」落子，"
                                      "代码里不出现具体 tag 名"),
        ("BitMask", "int", "配方位", "该 tag 在 64 位配方字里的位，0..63 唯一；-1 = 不进配方字"),
        ("Note", "string", "备注", "人读说明"),
    ],
    "Objects": [
        ("Id", "string", "物体id", "小写下划线；人物/怪物/武器/子弹/爆炸都在这一张表里"),
        ("DisplayKey", "string", "显示名key", "本地化 key，不写中文"),
        ("Tags", "string", "标签", "逗号分隔的 TagId，必须都存在于 Tags 表"),
        ("Cluster", "string", "可替换簇", "同簇的物件可互相替换（熔炉按权重随机选一个）；空 = 独立不参与熔炉；"
                                        "★ 同簇必须能力位一致（同一 ObjectKind 标签）"),
        ("ClusterWeight", "float", "熔炉权重", "同簇内按权重加权随机，必填且 > 0；越大越常被选中（稀有子弹给小值）"),
        ("StatPacks", "string", "数值包", "分号分隔五层：Base;Growth;Equipment;Buff;Temporary；"
                                        "指向 characters.xlsx 或本文件 StatPacks 的 PackId；空段 = 该层无包"),
        ("Weapon", "string", "武器契约", "指向 Weapons.Id；空 = 没有武器"),
        ("Projectile", "string", "子弹契约", "指向 Objects.Id，且该物件必须被某行 Projectiles 引用；空 = 没有子弹契约"),
        ("Actor", "string", "角色契约", "指向 Objects.Id；★ 子弹也能引用怪物（从弹体里喷出怪物）"),
        ("Effect", "string", "特效契约", "指向 Objects.Id；空 = 无"),
        ("RenderId", "string", "表现id", "表现层选模型/材质的 key，逻辑层不解释"),
        ("VisualPack", "string", "外观包", "指向 VisualPacks.Id；空 = 无外观包，回落 AssetPath"),
        ("AssetPath", "string", "模型路径", "res:// 路径；外观包缺失时的兜底；空 = 无模型（白模胶囊）"),
        ("AssetAnim", "string", "默认动画", "动画剪辑名（如 …|Idle）；空 = 无默认动画"),
        ("Radius", "float", "半径", "米，碰撞/贴地用"),
        ("Height", "float", "高度", "米"),
        ("Note", "string", "备注", "人读说明"),
    ],
    "VisualPacks": [
        ("Id", "string", "外观包id", "表现层选模型/材质；本表唯一"),
        ("ModelPath", "string", "模型路径", "res://素材/... ；★ 构建期校验磁盘真实存在"),
        ("Scale", "float", "缩放", "1.0 = 原始大小，必须 > 0"),
        ("TintHex", "string", "染色", "#RRGGBB；空 = 不染色"),
        ("AnimClips", "string", "动画剪辑", "逗号分隔的剪辑名；空 = 模型自带动画全保留"),
        ("Note", "string", "备注", "人读说明"),
    ],
    "Weapons": [
        ("Id", "string", "武器id", "小写下划线，本表唯一"),
        ("ObjectId", "string", "物体原型", "必填，指向 Objects.Id，且该 Objects 的 Tags 必须含 tag.weapon"),
        ("Trigger", "enum:WeaponTrigger", "触发方式", "Auto/Semi/Burst/Charge/Hold/Triggered/Manual"),
        ("Delivery", "enum:WeaponDelivery", "投送方式", "Instant/Projectile/Beam/Melee/Aura/SpawnActor/Throw"),
        ("Aim", "enum:WeaponAimKind", "瞄准方式",
         "Forward/AimPoint/AimLock/Orbit/RandomSpread/Grid/TargetSeek/Ground/ScreenEdge/AroundSelf/Cursor"),
        ("FireInterval", "规则值", "射击间隔", "秒；规则值写法见 README"),
        ("BaseDamage", "规则值", "基础伤害", "规则值"),
        ("ProjectileSpeed", "规则值", "弹速", "米/秒；规则值"),
        ("Range", "规则值", "射程", "米；规则值"),
        ("SpreadDeg", "规则值", "散布角", "度；规则值"),
        ("Count", "规则值", "单次发射数", "规则值"),
        ("Pierce", "规则值", "穿透", "规则值"),
        ("Bounce", "规则值", "弹跳", "规则值"),
        ("Knockback", "规则值", "击退", "规则值"),
        ("Recoil", "规则值", "后坐力", "规则值"),
        ("HitRadius", "规则值", "命中半径", "米；规则值"),
        ("Lifetime", "规则值", "存活时长", "秒；规则值"),
        ("ChargeTime", "规则值", "蓄力时长", "秒；规则值，0 = 不蓄力"),
        ("ProjectileRef", "string", "发射的子弹", "指向 Objects.Id（该 Objects 的 Tags 应含 tag.projectile）"),
        ("ActorRef", "string", "生成的物体", "指向 Objects.Id；空 = 无"),
        ("EffectRef", "string", "特效", "指向 Objects.Id；空 = 无"),
        ("DamageType", "enum:DamageType", "伤害类型", "None/Kinetic/Energy/Fire"),
        ("TeamLayer", "enum:TeamId", "阵营层", "Neutral/Player/Enemy"),
        ("InheritWeaponStats", "bool", "继承武器数值", "TRUE = 发射出去的子弹吃武器的 wstat:*"),
        ("ChainDepthLimit", "int", "熔炉链深上限", ">=1"),
        ("ChainShrink", "float", "链深衰减", "0..1，每深一层乘一次"),
        ("Recipe", "string", "熔炉配方", "8 个 TagId 用 | 连接（tag. 前缀与 . 分隔会撞车，故用 |）；空 = 不进熔炉"),
        ("Note", "string", "备注", "人读说明"),
    ],
    "Projectiles": [
        ("Id", "string", "子弹id", "小写下划线，本表唯一"),
        ("ObjectId", "string", "物体原型", "必填，指向 Objects.Id，且该 Objects 的 Tags 必须含 tag.projectile"),
        ("Motion", "enum:ProjectileMotion", "运动方式", "Straight/Homing/Orbit/Wave/Boomerang/Falling/Spiral/Anchor"),
        ("HitPolicy", "enum:ProjectileHitPolicy", "命中策略", "Single/Pierce/Bounce/Splash/Explode/Chain/Return/Ignore"),
        ("OnExpire", "enum:ProjectileDeath", "消散行为", "None/SpawnEffect/SpawnProjectile/SpawnActor/Explode/Inherit"),
        ("Speed", "规则值", "速度", "米/秒；规则值"),
        ("Radius", "规则值", "半径", "米；规则值"),
        ("Lifetime", "规则值", "存活时长", "秒；规则值"),
        ("Damage", "规则值", "伤害", "规则值"),
        ("Pierce", "规则值", "穿透", "规则值"),
        ("Bounce", "规则值", "弹跳", "规则值"),
        ("Gravity", "规则值", "重力", "米/秒²；规则值"),
        ("HomingTurn", "规则值", "追踪转向", "度/秒；规则值"),
        ("OnHitProjectile", "string", "命中生成子弹", "指向 Objects.Id；空 = 无"),
        ("OnHitActor", "string", "命中生成物体", "指向 Objects.Id；空 = 无（例：子弹里喷出怪物）"),
        ("OnExpireProjectile", "string", "消散生成子弹", "指向 Objects.Id；空 = 无"),
        ("OnExpireActor", "string", "消散生成物体", "指向 Objects.Id；空 = 无"),
        ("MaxChainDepth", "int", "熔炉链深上限", ">=1"),
        ("Recipe", "string", "熔炉配方", "8 个 TagId 用 | 连接（tag. 前缀与 . 分隔会撞车，故用 |）；空 = 不进熔炉"),
        ("Note", "string", "备注", "人读说明"),
    ],
    "StatPacks": [
        ("PackId", "string", "数值包id", "同一 PackId 的多行 = 同一层的多个数值"),
        ("Layer", "enum:StatLayer", "层", "Base/Growth/Equipment/Buff/Temporary；Base 是绝对值，其余是差量"),
        ("StatId", "enum:StatId", "属性id", "必须存在于 StatId 枚举；号段规则见 docs/rules/05"),
        ("Value", "float", "值", "倍率型(以 Mul 结尾)默认 1.0"),
    ],
}

# ── 数据 ──────────────────────────────────────────────────────────────────────

# 标签是**多轴的**：一个物体在不同轴上可以各挂一个（例如「稀有·精英·火元素·远程·爆炸怪」）。
# Family（轴）与 Group（组）的区别：
#   · Group 是「实现分组」——ObjectKind 组决定 Capability 位（代码只认这一组）；
#   · Family 是「**索引轴**」——强化 / 图鉴 / 筛选按轴取范围（"本周所有火焰类伤害 +20%"）。
# 同一物体在**唯一性轴**（Kind 与 Group 中标记为互斥的那些）上最多命中一个：
#   ObjectKind / WeaponKind / ProjectileKind / Element / Rarity / Race / Role；
#   Ability 轴**刻意不做唯一**（可以同时穿甲 + 弹跳 + 分裂，这正是 ECS 组合的意义）。
#
# ★ 只加不改：TagIndex 增量追加；新标签默认 BitMask = -1（不进熔炉配方字），
#   只有真的要被配方引用时才分配位（0..63 全局唯一）。
TAGS: list[list] = [
    # TagId, TagIndex, Family, Category, Group, BitMask, Note
    # ── 轴 1：Kind / ObjectKind（决定 Capability 位；互斥）────────────────────────
    ["tag.actor", 0, "Logic", "Kind", "Kind", "ObjectKind", 0, "人物/怪物"],
    ["tag.weapon", 1, "Logic", "Kind", "Kind", "ObjectKind", 1, "武器"],
    ["tag.projectile", 2, "Logic", "Kind", "Kind", "ObjectKind", 2, "子弹"],
    # Category 用表里已有的三个值（Kind/Stat/Element/Ability/Trigger），不私自扩词表：
    # 「能不能叠加」由下面的 Family 轴决定，与 Category 无关。
    # ── 轴 2：武器形态 / WeaponKind（互斥）──────────────────────────────────────
    ["tag.pistol", 3, "Logic", "WeaponKind", "Kind", "WeaponKind", 3, "手枪类"],
    ["tag.rifle", 4, "Logic", "WeaponKind", "Kind", "WeaponKind", 4, "步枪/冲锋类"],
    ["tag.bow", 5, "Logic", "WeaponKind", "Kind", "WeaponKind", 5, "弓类（蓄力）"],
    ["tag.melee", 6, "Logic", "WeaponKind", "Kind", "WeaponKind", 6, "近战类"],
    # ── 轴 3：弹体行为 / ProjectileKind（互斥）──────────────────────────────────
    ["tag.homing", 7, "Logic", "ProjectileKind", "Kind", "ProjectileKind", 7, "追踪弹"],
    # ── 轴 4：元素 / Element（互斥）────────────────────────────────────────────
    ["tag.energy", 8, "Logic", "Element", "Element", "Element", 8, "能量元素"],
    ["tag.kinetic", 9, "Logic", "Element", "Element", "Element", 9, "动能元素"],
    ["tag.explosive", 10, "Logic", "Element", "Element", "Element", 10, "爆炸元素"],
    # ── 轴 5：能力 / Ability（**可叠加**，非互斥）───────────────────────────────
    ["tag.pierce", 11, "Logic", "Ability", "Kind", "Ability", 11, "穿透"],
    ["tag.bounce", 12, "Logic", "Ability", "Kind", "Ability", 12, "弹跳"],
    ["tag.split", 13, "Logic", "Ability", "Kind", "Ability", 13, "分裂"],
    ["tag.summon", 14, "Logic", "Ability", "Kind", "Ability", 14, "召唤"],
    # ── 轴 6：熔炉链 / Chain ────────────────────────────────────────────────────
    ["tag.chain", 15, "Logic", "Ability", "Kind", "Chain", 15, "熔炉链递推"],
    # ── 轴 7：行为开关 / Trigger（不进配方字）───────────────────────────────────
    ["tag.volatile", 16, "Logic", "Trigger", "Trigger", "Trigger", -1, "不稳定：不进配方字，只做规则开关"],
    # ══════════════════════════════════════════════════════════════════════════
    # 以下为「多轴标签」扩展（2026-11-06）。全部 BitMask = -1：只做索引，不改熔炉行为。
    # ── 轴 8：稀有度 / Rarity（互斥；强化词条按稀有度加权、图鉴按稀有度分档）────
    ["tag.common", 17, "Logic", "Rarity", "Stat", "Rarity", -1, "普通：权重 ≥ 2 的大众货"],
    ["tag.uncommon", 18, "Logic", "Rarity", "Stat", "Rarity", -1, "精良：权重 1~2"],
    ["tag.rare", 19, "Logic", "Rarity", "Stat", "Rarity", -1, "稀有：权重 0.5~1，需要构筑才能拿到"],
    ["tag.legendary", 20, "Logic", "Rarity", "Stat", "Rarity", -1, "传说：权重 < 0.5，或表里唯一的匠人级"],
    # ── 轴 9：族裔 / Race（互斥；图鉴分组 + 「对某族增伤」类强化）─────────────────
    ["tag.humanoid", 21, "Logic", "Race", "Stat", "Race", -1, "人形"],
    ["tag.goblin", 22, "Logic", "Race", "Stat", "Race", -1, "哥布林"],
    ["tag.slime", 23, "Logic", "Race", "Stat", "Race", -1, "史莱姆"],
    ["tag.ogre", 24, "Logic", "Race", "Stat", "Race", -1, "食人魔"],
    # ── 轴 10：战斗角色 / Role（互斥；AI 与强化都按它取范围）─────────────────────
    ["tag.hero", 25, "Logic", "Role", "Stat", "Role", -1, "可选英雄（图鉴人物页的入口）"],
    ["tag.grunt", 26, "Logic", "Role", "Stat", "Role", -1, "小怪：近身杂兵"],
    ["tag.ranged", 27, "Logic", "Role", "Stat", "Role", -1, "远程：会拉距离输出的敌人"],
    ["tag.caster", 28, "Logic", "Role", "Stat", "Role", -1, "施法者：靠技能/召唤，血量低"],
    ["tag.elite", 29, "Logic", "Role", "Stat", "Role", -1, "精英：高血高甲，常带独有技能"],
    ["tag.assassin", 30, "Logic", "Role", "Stat", "Role", -1, "机动型：高速 / 高会心"],
    # ── 轴 11：攻击形态 / Ability（双轴归属：同属能力轴，便于叠词条）───────────────
    ["tag.projectile_fired", 31, "Logic", "Ability", "Ability", "Ability", -1, "弹道攻击：靠飞出弹体打人"],
    ["tag.short_range", 32, "Logic", "Ability", "Ability", "Ability", -1, "近身范围：贴脸才能打"],
    ["tag.multi_shot", 33, "Logic", "Ability", "Ability", "Ability", -1, "连发：射速高、单发弱"],
    ["tag.charge", 34, "Logic", "Ability", "Ability", "Ability", -1, "蓄力：按住越久越强"],
    ["tag.ricochet", 35, "Logic", "Ability", "Ability", "Ability", -1, "弹射：命中后折向新目标"],
    ["tag.high_damage", 36, "Logic", "Ability", "Ability", "Ability", -1, "重击：单发伤害显著高于同费"],
    # ── 轴 12：特效表现 / Effect（互斥；表现层按轴挑池，逻辑层不解释）─────────────
    ["tag.muzzle", 37, "Logic", "Effect", "Stat", "Effect", -1, "枪口：开火瞬间"],
    ["tag.hit", 38, "Logic", "Effect", "Stat", "Effect", -1, "命中：打在受击盒上"],
    ["tag.burst", 39, "Logic", "Effect", "Stat", "Effect", -1, "爆散：范围爆开"],
    # ── 标识层 / Identity：不做玩法判定，只做归属 ↔ 吸引 ↔ 主题匹配 ─────────────
    #    强约束：BitMask 一律 -1（不进配方字）、Group 一律 None（不产出能力位）。
    #    加这类标签不需要想规则成本，可以随手加。]
    ["tag.loot_ammo", 40, "Identity", "Loot", "Stat", "None", -1, "拾取：弹药物"],
    ["tag.loot_health", 41, "Identity", "Loot", "Stat", "None", -1, "拾取：回血物"],
    ["tag.loot_mana", 42, "Identity", "Loot", "Stat", "None", -1, "拾取：回蓝物"],
    ["tag.loot_coin", 43, "Identity", "Loot", "Stat", "None", -1, "拾取：金币（钱袋/掉落钱）"],
    ["tag.loot_relic", 44, "Identity", "Loot", "Stat", "None", -1, "拾取：遗物（永久加成，肉鸽核心构筑物）"],
    ["tag.loot_key", 45, "Identity", "Loot", "Stat", "None", -1, "拾取：钥匙（开门用）"],
    ["tag.attract_magnet", 46, "Identity", "Attract", "Stat", "None", -1, "被磁铁吸引：磁铁道具按这一轴取范围"],
    ["tag.attract_weapon", 47, "Identity", "Attract", "Stat", "None", -1, "被「武器吸附」吸引：枪/弹类掉落可被特定道具吸过来"],
    ["tag.attract_exp", 48, "Identity", "Attract", "Stat", "None", -1, "被经验吸附吸引：经验球专用的磁场"],
    ["tag.own_player", 49, "Identity", "Faction", "Stat", "None", -1, "玩家所属：玩家掉落物 / 玩家召唤物"],
    ["tag.own_enemy", 50, "Identity", "Faction", "Stat", "None", -1, "敌方所属：怪物掉落物"],
    ["tag.own_neutral", 51, "Identity", "Faction", "Stat", "None", -1, "中立所属：场景道具 / 可破坏物"],
    ["tag.theme_forest", 52, "Identity", "Theme", "Stat", "None", -1, "主题：森林章节"],
    ["tag.theme_dungeon", 53, "Identity", "Theme", "Stat", "None", -1, "主题：地牢章节"],
    ["tag.theme_forge", 54, "Identity", "Theme", "Stat", "None", -1, "主题：熔炉章节（与 Aura 联动）"],
    ["tag.aura_fire", 55, "Identity", "Aura", "Stat", "None", -1, "光环：火（与 tag.explosive 共鸣）"],
    ["tag.aura_ice", 56, "Identity", "Aura", "Stat", "None", -1, "光环：冰（减速/冻结类）"],
    ["tag.aura_shock", 57, "Identity", "Aura", "Stat", "None", -1, "光环：电（连锁类，与 tag.chain 共鸣）"],
    ["tag.craft_metal", 58, "Identity", "Craft", "Stat", "None", -1, "原料：金属"],
    ["tag.craft_wood", 59, "Identity", "Craft", "Stat", "None", -1, "原料：木材"],
    ["tag.craft_gem", 60, "Identity", "Craft", "Stat", "None", -1, "原料：宝石"],
    ["tag.craft_essence", 61, "Identity", "Craft", "Stat", "None", -1, "原料：精华/液体"],
]

# 每行：Id, DisplayKey, Tags, Cluster, ClusterWeight, StatPacks, Weapon, Projectile, Actor, Effect,
#       RenderId, VisualPack, AssetPath, AssetAnim, Radius, Height, Note
#
# ★ 簇设计原则（熔炉按簇加权随机）：
#   1. 同簇的物件必须**能力位一致** —— 能被互换，至少都带同一个 ObjectKind 标签
#      （全部 tag.projectile / 全部 tag.weapon / 全部 tag.actor）。
#   2. 同簇至少 3 个候选、权重各不相同，回归测试才能断言「改 ClusterWeight → 选中的候选变化」。
#   3. 权重体现稀有度：越常见越大，稀有物件给小值（王弹 0.1 vs 手枪弹 3.0）。
#
# ★ 模型列（VisualPack / AssetPath / AssetAnim）：
#   · VisualPack 指向 VisualPacks 表；AssetPath 是外观包缺失时的兜底 res:// 路径。
#   · 两者都空 = 白模胶囊（视图层尚未接模型渲染，故本轮只补字段与绑定）。
#   · 所有模型路径都经 Test-Path 实测存在，且构建期由 validate_content_xlsx.py 再验一遍。
#   · 特效物件（fx_*）刻意留空：特效走 shader/粒子，不挂 glb。
_IDLE = "CharacterArmature|CharacterArmature|CharacterArmature|Idle"
OBJECTS: list[list] = [
    # ── 人物 / 怪物（簇 actor_goblin：三个哥布林，能力位都是 actor）──
    # ★ 标签按轴挂（TagFamily）：Kind + WeaponKind/ProjectileKind + Element + 能力 + Rarity + Race + Role + Effect。
    #   同轴互斥的轴（Kind/WeaponKind/ProjectileKind/Element/Rarity/Race/Role/Effect）每行最多一个，校验器会查；
    #   Ability 轴允许叠（pierce + bounce + split 可以同时在同一个弹上）。
    ["player_hero", "actor.player",
     "tag.actor,tag.hero,tag.humanoid,tag.common,tag.projectile_fired", "actor_player", 1.0, "player_base;;;;", "pistol_101", "", "", "",
     "capsule_player", "vp_hero_humanoid", "", _IDLE, 0.45, 1.8, "玩家本体；Weapon 指向默认武器（单候选簇，独立不参与熔炉）"],
    ["goblin_grunt", "actor.goblin_grunt",
     "tag.actor,tag.goblin,tag.humanoid,tag.common,tag.grunt,tag.short_range", "actor_goblin", 5.0, "goblin_base;;;;", "", "", "", "",
     "capsule_goblin", "vp_goblin_humanoid", "", _IDLE, 0.40, 1.20, "普通哥布林（近战，无远程武器）——最常见"],
    ["goblin_archer", "actor.goblin_archer",
     "tag.actor,tag.bow,tag.goblin,tag.humanoid,tag.uncommon,tag.ranged,tag.projectile_fired", "actor_goblin", 2.0, "goblin_archer_base;;;;", "bow_201", "bullet_201", "", "",
     "capsule_archer", "vp_goblin_humanoid", "", _IDLE, 0.40, 1.20, "哥布林弓手：Weapon/Projectile 都有，同源拼装"],
    ["goblin_shaman", "actor.goblin_shaman",
     "tag.actor,tag.energy,tag.goblin,tag.humanoid,tag.rare,tag.caster", "actor_goblin", 0.8, "goblin_shaman_base;;;;", "", "", "", "",
     "capsule_shaman", "vp_goblin_humanoid", "", _IDLE, 0.40, 1.30, "哥布林萨满：稀有精英怪（权重小）"],
    ["ogre_brute", "actor.ogre_brute",
     "tag.actor,tag.melee,tag.ogre,tag.uncommon,tag.elite,tag.short_range,tag.high_damage", "actor_ogre", 1.0, "ogre_base;;;;", "club_401", "", "", "",
     "capsule_ogre", "vp_ogre_turret", "", "", 0.90, 2.60, "食人魔精英：近战武器（单候选簇）"],
    ["slime_401", "actor.slime_401",
     "tag.actor,tag.split,tag.slime,tag.common,tag.grunt", "actor_slime", 3.0, "slime_401_base;;;;", "", "", "", "",
     "blob_slime", "", "res://素材/海盗场景带动画/Skull.glb", "", 0.35, 0.70, "分裂史莱姆（可被子弹喷出来 → 弹体里出怪）"],
    ["slime_402", "actor.slime_402",
     "tag.actor,tag.split,tag.explosive,tag.slime,tag.rare,tag.grunt", "actor_slime", 1.0, "slime_402_base;;;;", "", "", "", "",
     "blob_slime_fire", "", "res://素材/海盗场景带动画/Bomb.glb", "", 0.40, 0.80, "爆炸史莱姆：稀有变种"],
    ["slime_403", "actor.slime_403",
     "tag.actor,tag.split,tag.volatile,tag.slime,tag.legendary,tag.assassin", "actor_slime", 0.4, "slime_403_base;;;;", "", "", "", "",
     "blob_slime_wild", "", "res://素材/海盗场景带动画/Skull.glb", "", 0.30, 0.60, "不稳定史莱姆：极稀有"],
    # ── 武器（簇 weapon_gun：三把手枪类；簇 weapon_rifle：三把长枪类）──
    ["pistol_101", "item.pistol_101",
     "tag.weapon,tag.pistol,tag.kinetic,tag.common,tag.projectile_fired", "weapon_gun", 3.0, "wpn_pistol_101;;;;", "", "bullet_101", "", "",
     "gun_pistol", "vp_weapon_pistol", "", "", 0.10, 0.10, "玩家左键手枪——最常见"],
    ["smg_102", "item.smg_102",
     "tag.weapon,tag.pistol,tag.kinetic,tag.pierce,tag.uncommon,tag.multi_shot,tag.projectile_fired", "weapon_gun", 1.5, "wpn_smg_102;;;;", "", "bullet_102", "", "",
     "gun_smg", "vp_weapon_smg", "", "", 0.10, 0.10, "冲锋枪：高速散射 + 分裂"],
    ["revolver_103", "item.revolver_103",
     "tag.weapon,tag.pistol,tag.kinetic,tag.rare,tag.high_damage,tag.projectile_fired", "weapon_gun", 0.6, "wpn_revolver_103;;;;", "", "bullet_103", "", "",
     "gun_revolver", "vp_weapon_revolver", "", "", 0.10, 0.10, "左轮：稀有高伤"],
    ["bow_201", "item.bow_201",
     "tag.weapon,tag.bow,tag.kinetic,tag.homing,tag.uncommon,tag.charge,tag.projectile_fired", "weapon_rifle", 1.0, "wpn_bow_201;;;;", "", "bullet_201", "", "",
     "bow_basic", "vp_weapon_bow", "", "", 0.10, 0.10, "弓：蓄力 + 追踪，子弹继承弓的数值"],
    ["rifle_202", "item.rifle_202",
     "tag.weapon,tag.rifle,tag.kinetic,tag.common,tag.multi_shot,tag.projectile_fired", "weapon_rifle", 2.5, "wpn_rifle_202;;;;", "", "bullet_102", "", "",
     "gun_rifle", "vp_weapon_rifle", "", "", 0.10, 0.10, "突击步枪：常见长枪"],
    ["sniper_203", "item.sniper_203",
     "tag.weapon,tag.rifle,tag.kinetic,tag.pierce,tag.rare,tag.charge,tag.high_damage,tag.projectile_fired", "weapon_rifle", 0.5, "wpn_sniper_203;;;;", "", "bullet_103", "", "",
     "gun_sniper", "vp_weapon_sniper", "", "", 0.10, 0.10, "狙击枪：低射速高穿透"],
    ["club_401", "item.club_401",
     "tag.weapon,tag.melee,tag.kinetic,tag.uncommon,tag.short_range,tag.high_damage", "weapon_melee", 1.0, "wpn_club_401;;;;", "", "", "", "",
     "club_basic", "vp_weapon_club", "", "", 0.30, 0.30, "食人魔大棒：Melee 投送，不发射子弹（单候选簇）"],
    ["orb_501", "item.orb_501",
     "tag.weapon,tag.energy,tag.summon,tag.legendary", "weapon_staff", 1.0, "wpn_orb_501;;;;", "", "bullet_501", "", "",
     "orb_staff", "vp_weapon_orb", "", "", 0.15, 0.15, "能量法球：SpawnActor 投送（打出史莱姆）（单候选簇）"],
    # ── 子弹（簇 proj_kinetic：四种动能弹，权重差别明显）──
    ["bullet_101", "proj.bullet_101",
     "tag.projectile,tag.kinetic,tag.common", "proj_kinetic", 5.0, "bullet_101_base;;;;", "", "", "", "",
     "orb_small", "vp_proj_bullet_small", "", "", 0.05, 0.05, "手枪弹——最常见"],
    ["bullet_102", "proj.bullet_102",
     "tag.projectile,tag.kinetic,tag.pierce,tag.bounce,tag.uncommon", "proj_kinetic", 2.0, "bullet_102_base;;;;", "", "", "", "",
     "orb_small", "vp_proj_bullet_small", "", "", 0.05, 0.05, "冲锋枪弹"],
    ["bullet_103", "proj.bullet_103",
     "tag.projectile,tag.kinetic,tag.pierce,tag.rare,tag.high_damage", "proj_kinetic", 0.5, "bullet_103_base;;;;", "", "", "", "",
     "orb_heavy", "vp_proj_bullet_heavy", "", "", 0.06, 0.06, "重弹：稀有"],
    ["arrow_202", "proj.arrow_202",
     "tag.projectile,tag.kinetic,tag.pierce,tag.legendary", "proj_kinetic", 0.2, "arrow_202_base;;;;", "bow_201", "", "", "",
     "arrow_pierce", "vp_proj_arrow", "", "", 0.06, 0.06, "★ 具有武器属性的子弹：Weapon=bow_201，落地后再射一发（极稀有）"],
    ["bullet_201", "proj.bullet_201",
     "tag.projectile,tag.homing,tag.kinetic,tag.uncommon", "proj_homing", 1.0, "bullet_201_base;;;;", "", "", "", "",
     "orb_homing", "vp_proj_arrow", "", "", 0.07, 0.07, "追踪箭（被弓发射）（单候选簇）"],
    ["bullet_301", "proj.bullet_301",
     "tag.projectile,tag.explosive,tag.summon,tag.split,tag.rare", "proj_explosive", 1.0, "bullet_301_base;;;;", "", "", "slime_401", "",
     "orb_bomb", "vp_proj_bomb", "", "", 0.12, 0.12, "★ 分裂炸弹：消散时喷出史莱姆（子弹 → 怪物）（单候选簇）"],
    ["bullet_501", "proj.bullet_501",
     "tag.projectile,tag.energy,tag.ricochet,tag.uncommon", "proj_energy", 1.0, "bullet_501_base;;;;", "", "", "", "",
     "orb_energy", "vp_proj_energy", "", "", 0.08, 0.08, "能量弹（被法球发射）（单候选簇）"],
    # ── 特效（也是物体：Effect 引用谁都可以指向这里；无 Kind 标签 → Capability = None，不进熔炉）──
    # ★ 特效不挂模型：视觉由 shader/粒子负责，三个 fx_* 的三列模型列全部留空（有意为之，非遗漏）。
    ["fx_muzzle_small", "fx.muzzle_small",
     "tag.muzzle,tag.kinetic,tag.common", "", 1.0, ";;;;", "", "", "", "",
     "fx_muzzle_small", "", "", "", 0.10, 0.10, "枪口火花（无 Kind 标签 → Capability = None）"],
    ["fx_hit_spark", "fx.hit_spark",
     "tag.hit,tag.kinetic,tag.common", "", 1.0, ";;;;", "", "", "", "",
     "fx_hit_spark", "", "", "", 0.10, 0.10, "命中火花"],
    ["fx_bomb_burst", "fx.bomb_burst",
     "tag.burst,tag.kinetic,tag.uncommon", "", 1.0, ";;;;", "", "", "", "",
     "fx_bomb_burst", "", "", "", 0.60, 0.60, "爆炸爆散"],
]

# 每行：Id, ModelPath, Scale, TintHex, AnimClips, Note
# ★ 所有 ModelPath 都经 PowerShell Test-Path 实测存在；构建期再校验一次（剥 res:// 后在项目根下查）。
# ★ TintHex 用在「同一 glb 复用给多个物件」的场合：靠染色区分哥布林三兄弟 / 史莱姆三变种。
VISUAL_PACKS: list[list] = [
    ["vp_hero_humanoid", "res://素材/卡通人物带全套动画/史蒂夫.glb", 1.0, "#7FB2D8",
     "CharacterArmature|CharacterArmature|CharacterArmature|Idle,CharacterArmature|CharacterArmature|CharacterArmature|Run",
     "玩家：史蒂夫（含全套骨骼动画 18 个剪辑）"],
    ["vp_goblin_humanoid", "res://素材/卡通人物带全套动画/金发白男.glb", 0.85, "#7FB26B",
     "CharacterArmature|CharacterArmature|CharacterArmature|Idle,CharacterArmature|CharacterArmature|CharacterArmature|Run",
     "哥布林三兄弟共用：靠 Objects 覆盖染色区分（绿）"],
    ["vp_npc_merchant", "res://素材/卡通人物带全套动画/商人无动画.glb", 1.0, "#C8A165",
     "", "商人 NPC：无骨骼动画，仅静态模型"],
    ["vp_ogre_turret", "res://素材/炮台/Gun Cannon Turret.glb", 1.6, "#8C7B63",
     "", "食人魔：素材库无大型人形怪物，用重炮台枪塔代替（诚实配型，见 Note）"],
    ["vp_weapon_pistol", "res://素材/枪械/Pistol.glb", 1.0, "", "", "手枪"],
    ["vp_weapon_smg", "res://素材/枪械/Submachine Gun.glb", 1.0, "", "", "冲锋枪"],
    ["vp_weapon_revolver", "res://素材/枪械/Revolver.glb", 1.0, "", "", "左轮"],
    ["vp_weapon_bow", "res://素材/RPG武器/Wooden Bow.glb", 1.0, "", "", "木弓"],
    ["vp_weapon_rifle", "res://素材/枪械/Assault Rifle.glb", 1.0, "", "", "突击步枪"],
    ["vp_weapon_sniper", "res://素材/枪械/Sniper Rifle.glb", 1.0, "", "", "狙击步枪"],
    ["vp_weapon_club", "res://素材/RPG武器/Doublesided Hammer.glb", 1.0, "", "", "大棒 → 双面锤（最接近的近战钝器）"],
    ["vp_weapon_orb", "res://素材/RPG武器/Potion Bottle.glb", 1.2, "#8FD8FF", "", "能量法球：素材库无法球模型，用药水瓶代替 + 蓝色染色"],
    ["vp_proj_bullet_small", "res://素材/RPG武器/Mineral.glb", 0.4, "#FFD966", "", "小子弹：用小矿石当弹体（素材库无子弹模型）"],
    ["vp_proj_bullet_heavy", "res://素材/RPG武器/Coin.glb", 0.5, "#C0C0C0", "", "大子弹：用金币当弹体"],
    ["vp_proj_arrow", "res://素材/RPG武器/Arrow.glb", 1.0, "", "", "箭矢（弓的弹药 + 追踪箭）"],
    ["vp_proj_bomb", "res://素材/海盗场景带动画/Bomb.glb", 1.0, "", "", "炸弹"],
    ["vp_proj_energy", "res://素材/海盗场景带动画/Gem Blue.glb", 0.6, "#7FE0FF", "", "能量弹：用蓝宝石当弹体"],
]

# 每行：Id, ObjectId, Trigger, Delivery, Aim, FireInterval, BaseDamage, ProjectileSpeed, Range,
#       SpreadDeg, Count, Pierce, Bounce, Knockback, Recoil, HitRadius, Lifetime, ChargeTime,
#       ProjectileRef, ActorRef, EffectRef, DamageType, TeamLayer, InheritWeaponStats,
#       ChainDepthLimit, ChainShrink, Recipe, Note
WEAPONS: list[list] = [
    ["pistol_101", "pistol_101", "Semi", "Projectile", "AimPoint",
     "wstat:WeaponFireRate(0.12)", "wstat:WeaponDamage(10)",
     "wstat:WeaponProjectileSpeed(25)", "wstat:WeaponRange(18)",
     "rand:2+0.5", "1", "0", "0", "2", "1", "0.05", "2.0", "0",
     "bullet_101", "", "fx_muzzle_small", "Kinetic", "Player", "TRUE", 2, 0.85,
     "tag.pistol|tag.pistol|tag.kinetic|tag.pierce|tag.kinetic|tag.pistol|tag.kinetic|tag.pistol",
     "玩家左键：点射（全部数值走武器数值包，改表即改手感）"],
    ["smg_102", "smg_102", "Auto", "Projectile", "RandomSpread",
     "0.06", "6", "22", "16", "rand:3+0.5", "1", "2", "0", "0.5", "1", "0.5", "0.04", "1.2",
     "bullet_102", "", "fx_muzzle_small", "Kinetic", "Player", "TRUE", 2, 0.80,
     "tag.rifle|tag.rifle|tag.kinetic|tag.split|tag.pierce|tag.kinetic|tag.bounce|tag.rifle",
     "冲锋枪：扫射 + 穿透 2"],
    ["bow_201", "bow_201", "Charge", "Projectile", "AimLock",
     "1.0", "18", "30", "20", "0", "1", "0", "0", "5", "1", "0", "0.07", "3.0",
     "bullet_201", "", "", "Kinetic", "Enemy", "FALSE", 3, 0.75,
     "tag.bow|tag.bow|tag.homing|tag.chain|tag.homing|tag.bow|tag.pierce|tag.homing",
     "弓：蓄力 + 追踪，子弹继承弓的数值"],
    ["club_401", "club_401", "Semi", "Melee", "Forward",
     "1.6", "rand:24+0.15", "0", "2.4", "rand:10+0.3", "1", "0", "0", "8", "2", "1.2", "0.25", "0",
     "", "", "fx_hit_spark", "Kinetic", "Enemy", "FALSE", 1, 1.0,
     "", "食人魔大棒：近战挥击（不发射子弹，Recipe 空 = 不进熔炉）"],
    ["orb_501", "orb_501", "Semi", "SpawnActor", "AimPoint",
     "2.2", "flags:tag.split", "12", "22", "0", "1", "0", "0", "0", "0", "0.8", "8.0", "0",
     "", "slime_401", "", "Energy", "Enemy", "FALSE", 2, 0.9,
     "tag.energy|tag.energy|tag.summon|tag.split|tag.summon|tag.energy|tag.summon|tag.energy",
     "法球：伤害 = 是否带 tag.split（位矩阵 → 数值），投送方式 = 直接生成怪物"],
    ["revolver_103", "revolver_103", "Semi", "Projectile", "AimPoint",
     "wstat:WeaponFireRate(0.55)", "wstat:WeaponDamage(22)",
     "wstat:WeaponProjectileSpeed(32)", "wstat:WeaponRange(22)",
     "rand:1+0.5", "1", "0", "0", "3", "2", "0.06", "2.4", "0",
     "bullet_103", "", "fx_muzzle_small", "Kinetic", "Player", "TRUE", 2, 0.85,
     "tag.pistol|tag.pistol|tag.kinetic|tag.pierce|tag.kinetic|tag.pistol|tag.kinetic|tag.pistol",
     "左轮：同簇 weapon_gun 的稀有变体（权重 0.6）"],
    ["rifle_202", "rifle_202", "Auto", "Projectile", "RandomSpread",
     "wstat:WeaponFireRate(0.14)", "wstat:WeaponDamage(12)",
     "wstat:WeaponProjectileSpeed(28)", "wstat:WeaponRange(24)",
     "rand:2+0.5", "1", "0", "0", "1", "1", "0.05", "2.0", "0",
     "bullet_102", "", "fx_muzzle_small", "Kinetic", "Player", "TRUE", 2, 0.85,
     "tag.rifle|tag.rifle|tag.kinetic|tag.pierce|tag.kinetic|tag.rifle|tag.kinetic|tag.rifle",
     "突击步枪：同簇 weapon_rifle 的常见变体（权重 2.5）"],
    ["sniper_203", "sniper_203", "Charge", "Projectile", "AimLock",
     "wstat:WeaponFireRate(1.4)", "wstat:WeaponDamage(40)",
     "wstat:WeaponProjectileSpeed(45)", "wstat:WeaponRange(40)",
     "0", "1", "3", "0", "4", "3", "0.06", "2.6", "1.2",
     "bullet_103", "", "fx_muzzle_small", "Kinetic", "Player", "TRUE", 2, 0.85,
     "tag.rifle|tag.rifle|tag.kinetic|tag.pierce|tag.kinetic|tag.rifle|tag.kinetic|tag.rifle",
     "狙击枪：同簇 weapon_rifle 的稀有变体（权重 0.5）"],
]

# 每行：Id, ObjectId, Motion, HitPolicy, OnExpire, Speed, Radius, Lifetime, Damage, Pierce, Bounce,
#       Gravity, HomingTurn, OnHitProjectile, OnHitActor, OnExpireProjectile, OnExpireActor,
#       MaxChainDepth, Recipe, Note
PROJECTILES: list[list] = [
    ["proj_bullet_101", "bullet_101", "Straight", "Single", "None",
     "wstat:WeaponProjectileSpeed(25)", "0.05", "2.0", "6", "0", "0", "0", "0",
     "", "", "", "", 2, "tag.kinetic|tag.pierce|tag.kinetic|tag.pierce|tag.kinetic|tag.pierce|tag.kinetic|tag.pierce", "手枪弹（wstat 缺省时回落 25 弹速）"],
    ["proj_bullet_102", "bullet_102", "Straight", "Pierce", "None",
     "22", "0.05", "1.2", "4", "2", "0", "0", "0",
     "", "", "", "", 2, "tag.kinetic|tag.pierce|tag.bounce|tag.kinetic|tag.pierce|tag.bounce|tag.kinetic|tag.pierce", "冲锋枪弹：穿透 2"],
    ["proj_bullet_103", "bullet_103", "Straight", "Pierce", "None",
     "32", "0.06", "2.4", "14", "1", "0", "0", "0",
     "", "", "", "", 2, "tag.kinetic|tag.pierce|tag.kinetic|tag.pierce|tag.kinetic|tag.pierce|tag.kinetic|tag.pierce", "重弹：同簇 proj_kinetic 的稀有变体（权重 0.5）"],
    ["proj_bullet_201", "bullet_201", "Homing", "Single", "None",
     "30", "0.07", "3.0", "18", "0", "0", "0", "rand:180+0.25",
     "", "", "", "", 3, "tag.homing|tag.kinetic|tag.chain|tag.homing|tag.kinetic|tag.chain|tag.homing|tag.kinetic", "追踪箭：继承弓的数值"],
    ["proj_bomb_301", "bullet_301", "Falling", "Explode", "SpawnActor",
     "14", "0.12", "1.6", "rand:20+0.3", "0", "0", "9.8", "0",
     "", "", "", "slime_401", 2, "tag.explosive|tag.split|tag.summon|tag.explosive|tag.split|tag.summon|tag.explosive|tag.split", "★ 炸弹消散时喷出史莱姆"],
    ["proj_arrow_202", "arrow_202", "Straight", "Pierce", "SpawnProjectile",
     "38", "0.06", "1.0", "wstat:WeaponDamage(12)", "3", "0", "0", "0",
     "", "", "bullet_201", "", 3, "tag.kinetic|tag.pierce|tag.chain|tag.kinetic|tag.pierce|tag.chain|tag.kinetic|tag.pierce", "★ 带武器属性的子弹：消散后再射一发追踪箭"],
    ["proj_bullet_501", "bullet_501", "Wave", "Chain", "SpawnEffect",
     "wstat:ProjectileHomingSpeed(12)", "0.08", "1.8", "rand:8+0.25", "0", "0", "0", "0",
     "", "", "", "", 2, "tag.energy|tag.chain|tag.energy|tag.chain|tag.energy|tag.chain|tag.energy|tag.chain", "能量弹：伤害随熔炉链深增长"],
]

# 每行：PackId, Layer, StatId, Value
STAT_PACKS: list[list] = [
    # ── 武器数值包（Weapons 用 wstat:* 读它们）──
    ["wpn_pistol_101", "Base", "WeaponDamage", 10],
    ["wpn_pistol_101", "Base", "WeaponFireRate", 0.12],
    ["wpn_pistol_101", "Base", "WeaponProjectileSpeed", 25],
    ["wpn_pistol_101", "Base", "WeaponRange", 18],
    ["wpn_pistol_101", "Base", "WeaponCritChance", 0.05],
    ["wpn_smg_102", "Base", "WeaponDamage", 6],
    ["wpn_smg_102", "Base", "WeaponFireRate", 0.06],
    ["wpn_smg_102", "Base", "WeaponProjectileSpeed", 22],
    ["wpn_smg_102", "Base", "WeaponRange", 16],
    ["wpn_smg_102", "Base", "WeaponSpread", 3],
    ["wpn_smg_102", "Base", "WeaponPierce", 2],
    ["wpn_bow_201", "Base", "WeaponDamage", 18],
    ["wpn_bow_201", "Base", "WeaponFireRate", 1.0],
    ["wpn_bow_201", "Base", "WeaponProjectileSpeed", 30],
    ["wpn_bow_201", "Base", "WeaponRange", 20],
    ["wpn_bow_201", "Base", "WeaponChargeTime", 0.8],
    ["wpn_club_401", "Base", "WeaponDamage", 24],
    ["wpn_club_401", "Base", "WeaponFireRate", 1.6],
    ["wpn_club_401", "Base", "WeaponKnockback", 8],
    ["wpn_orb_501", "Base", "WeaponDamage", 1],
    ["wpn_orb_501", "Base", "WeaponFireRate", 2.2],
    ["wpn_orb_501", "Base", "WeaponProjectileCount", 1],
    ["wpn_revolver_103", "Base", "WeaponDamage", 22],
    ["wpn_revolver_103", "Base", "WeaponFireRate", 0.55],
    ["wpn_revolver_103", "Base", "WeaponProjectileSpeed", 32],
    ["wpn_revolver_103", "Base", "WeaponRange", 22],
    ["wpn_revolver_103", "Base", "WeaponCritChance", 0.15],
    ["wpn_rifle_202", "Base", "WeaponDamage", 12],
    ["wpn_rifle_202", "Base", "WeaponFireRate", 0.14],
    ["wpn_rifle_202", "Base", "WeaponProjectileSpeed", 28],
    ["wpn_rifle_202", "Base", "WeaponRange", 24],
    ["wpn_sniper_203", "Base", "WeaponDamage", 40],
    ["wpn_sniper_203", "Base", "WeaponFireRate", 1.4],
    ["wpn_sniper_203", "Base", "WeaponProjectileSpeed", 45],
    ["wpn_sniper_203", "Base", "WeaponRange", 40],
    ["wpn_sniper_203", "Base", "WeaponPierce", 3],
    # ── 子弹数值包（Projectiles 读它；子弹自己也可以带 wstat 继承武器）──
    ["bullet_101_base", "Base", "Damage", 6],
    ["bullet_101_base", "Base", "ProjectileLifeTime", 2.0],
    ["bullet_101_base", "Base", "ProjectileRadius", 0.05],
    ["bullet_102_base", "Base", "Damage", 4],
    ["bullet_102_base", "Base", "ProjectileLifeTime", 1.2],
    ["bullet_102_base", "Base", "ProjectileBounce", 2],
    ["bullet_103_base", "Base", "Damage", 14],
    ["bullet_103_base", "Base", "ProjectileLifeTime", 2.4],
    ["bullet_103_base", "Base", "ProjectileRadius", 0.06],
    ["bullet_201_base", "Base", "Damage", 18],
    ["bullet_201_base", "Base", "ProjectileLifeTime", 3.0],
    ["bullet_201_base", "Base", "ProjectileHomingSpeed", 180],
    ["bullet_301_base", "Base", "Damage", 20],
    ["bullet_301_base", "Base", "ProjectileLifeTime", 1.6],
    ["bullet_301_base", "Base", "ProjectileGravity", 9.8],
    ["arrow_202_base", "Base", "Damage", 12],
    ["arrow_202_base", "Base", "ProjectileLifeTime", 1.0],
    ["bullet_501_base", "Base", "Damage", 8],
    ["bullet_501_base", "Base", "ProjectileLifeTime", 1.8],
    ["bullet_501_base", "Base", "ProjectileHomingSpeed", 12],
    # ── 史莱姆（actor_base 里 objects 表直接引用的那个包）──
    ["slime_401_base", "Base", "Hp", 30],
    ["slime_401_base", "Base", "HpMax", 30],
    ["slime_401_base", "Base", "Mana", 0],
    ["slime_401_base", "Base", "ManaMax", 0],
    ["slime_401_base", "Base", "Armor", 0],
    ["slime_401_base", "Base", "ArmorK", 100],
    ["slime_401_base", "Base", "MoveSpeed", 3.8],
    ["slime_401_base", "Base", "Strength", 2],
    ["slime_401_base", "Base", "Dexterity", 2],
    ["slime_401_base", "Base", "Luck", 1],
    ["slime_401_base", "Base", "HurtInvulnTime", 0.2],
    ["slime_402_base", "Base", "Hp", 45],
    ["slime_402_base", "Base", "HpMax", 45],
    ["slime_402_base", "Base", "Mana", 0],
    ["slime_402_base", "Base", "ManaMax", 0],
    ["slime_402_base", "Base", "Armor", 2],
    ["slime_402_base", "Base", "ArmorK", 100],
    ["slime_402_base", "Base", "MoveSpeed", 3.2],
    ["slime_402_base", "Base", "Strength", 4],
    ["slime_402_base", "Base", "Dexterity", 2],
    ["slime_402_base", "Base", "Luck", 1],
    ["slime_402_base", "Base", "HurtInvulnTime", 0.2],
    ["slime_403_base", "Base", "Hp", 22],
    ["slime_403_base", "Base", "HpMax", 22],
    ["slime_403_base", "Base", "Mana", 0],
    ["slime_403_base", "Base", "ManaMax", 0],
    ["slime_403_base", "Base", "Armor", 0],
    ["slime_403_base", "Base", "ArmorK", 100],
    ["slime_403_base", "Base", "MoveSpeed", 5.2],
    ["slime_403_base", "Base", "Strength", 3],
    ["slime_403_base", "Base", "Dexterity", 4],
    ["slime_403_base", "Base", "Luck", 3],
    ["slime_403_base", "Base", "HurtInvulnTime", 0.1],
    ["goblin_shaman_base", "Base", "Hp", 60],
    ["goblin_shaman_base", "Base", "HpMax", 60],
    ["goblin_shaman_base", "Base", "Mana", 40],
    ["goblin_shaman_base", "Base", "ManaMax", 40],
    ["goblin_shaman_base", "Base", "ManaRegenPerSec", 2],
    ["goblin_shaman_base", "Base", "Armor", 1],
    ["goblin_shaman_base", "Base", "ArmorK", 100],
    ["goblin_shaman_base", "Base", "MoveSpeed", 3.0],
    ["goblin_shaman_base", "Base", "Strength", 3],
    ["goblin_shaman_base", "Base", "Dexterity", 6],
    ["goblin_shaman_base", "Base", "Luck", 4],
    ["goblin_shaman_base", "Base", "HurtInvulnTime", 0.25],
    # ── 怪物武器补包（让「敌人射击」也走数值包，而不是写死在代码里）──
    ["enemy_bow_module", "Base", "WeaponDamage", 8],
    ["enemy_bow_module", "Base", "WeaponProjectileSpeed", 26],
]


def sheet(wb: Workbook, title: str, columns: list[tuple[str, str, str, str]], data: list[list]):
    """写一张表：4 行表头（字段名/类型/中文名/备注，**每列一条**）+ `#data` 标记行 + 数据行。

    注意：columns 是「每列一条」的四元组列表 → 第 1 行是字段名、第 2 行是类型……，
    所以这里是按**行**逐行写（4 行 × N 列），不是把四元组当行 append。
    """
    ws = wb.create_sheet(title)
    for r in range(1, 5):
        for c, column in enumerate(columns, start=1):
            cell = ws.cell(row=r, column=c, value=column[r - 1])
            cell.fill = HEADER_FILL if r == 1 else (TYPE_FILL if r == 2 else NOTE_FILL)
            cell.font = Font(bold=(r == 1), color="FFFFFF" if r == 1 else "000000", size=10)
            cell.alignment = Alignment(wrap_text=True, vertical="top")
    # 数据起始标记行，便于人眼阅读（校验器据此确认表头就是 4 行）
    ws.cell(row=5, column=1, value="#data")
    for i, row in enumerate(data):
        for c, value in enumerate(row, start=1):
            ws.cell(row=6 + i, column=c, value=value)
    for c in range(1, len(columns) + 1):
        ws.column_dimensions[get_column_letter(c)].width = 22
    ws.freeze_panes = "A6"
    return ws


def main() -> int:
    # 生成前自检「每个引用都闭合」这一条太强，交给 validate_content_xlsx.py；
    # 这里只做最便宜的一致性检查：行宽与列数一致（否则 openpyxl 会静默错位）。
    for title, rows in (("Tags", TAGS), ("Objects", OBJECTS), ("Weapons", WEAPONS),
                        ("Projectiles", PROJECTILES), ("StatPacks", STAT_PACKS),
                        ("VisualPacks", VISUAL_PACKS)):
        width = len(COLUMNS[title])
        for i, row in enumerate(rows, start=6):
            if len(row) != width:
                print(f"[错误] {title} 第 {i} 行有 {len(row)} 列，期望 {width} 列", file=sys.stderr)
                return 2

    wb = Workbook()
    wb.remove(wb.active)

    sheet(wb, "Tags", COLUMNS["Tags"], TAGS)
    sheet(wb, "Objects", COLUMNS["Objects"], OBJECTS)
    sheet(wb, "Weapons", COLUMNS["Weapons"], WEAPONS)
    sheet(wb, "Projectiles", COLUMNS["Projectiles"], PROJECTILES)
    sheet(wb, "StatPacks", COLUMNS["StatPacks"], STAT_PACKS)
    sheet(wb, "VisualPacks", COLUMNS["VisualPacks"], VISUAL_PACKS)

    # Meta：schema 版本（ARCHITECTURE §4.2 校验点 ④）
    meta = wb.create_sheet("_Meta")
    meta.append(["Key", "Value"])
    meta.append(["SchemaVersion", SCHEMA_VERSION])
    meta.append(["Owner", "design(策划编辑区)"])
    meta.append(["Source", "唯一真源；生成物在 data/generated/，禁止手改"])
    meta.append(["Updated", "2026-11-09"])
    for c in range(1, 3):
        meta.cell(row=1, column=c).fill = HEADER_FILL
        meta.cell(row=1, column=c).font = Font(bold=True, color="FFFFFF")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    wb.save(OUT)
    print(f"wrote {OUT}")
    print(f"  Tags={len(TAGS)} Objects={len(OBJECTS)} Weapons={len(WEAPONS)} "
          f"Projectiles={len(PROJECTILES)} StatPacks={len(STAT_PACKS)} "
          f"VisualPacks={len(VISUAL_PACKS)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
