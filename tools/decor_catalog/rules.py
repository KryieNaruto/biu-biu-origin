"""素材装饰目录的**摆放规则与收录策略**（纯数据，不含测量逻辑）。

改「哪些模型进目录、以什么权重/层进」只需要改本文件，然后重跑 `build_catalog.py`。
新增一类素材 = 在 `FOLDER_DEFAULT` 或 `RULES` 里加一条；新增一个模型 = 在 `RULES` 里加一行
（键 = 素材目录/文件名.glb）。规则按「后面的覆盖前面的」顺序应用：

  1. 目录默认行 `FOLDER_DEFAULT[folder]`
  2. 单文件规则 `RULES["folder/file.glb"]`（可写 `{"skip": "原因"}` 排除，或覆盖任意字段）

`id` 不在这里写：由 `build_catalog.py` 按「目录名 → 文件名」排序后顺序编号，保证**重跑后编号不变**。
"""

from __future__ import annotations

# ─────────────────────────────────────────────────────────────
# 0. 全局约定
# ─────────────────────────────────────────────────────────────

SCHEMA_VERSION = 2

#: 缩放基准：模型最大水平边（max(尺寸X, 尺寸Z)）的**目标米数**。
#: 口径 A（v2 采用）：在「逻辑尺寸」坐标系里，让最大水平边落在 [TARGET_MIN, TARGET_MAX]，
#:   理想值 = TARGET_METERS；逻辑尺寸 = 原始 glb 尺寸 × 目录库全局缩放系数 `ASSET_LIBRARY_SCALE`
#:   （素材分类目是打包商各用一套单位：RPG 武器 ~1 单位≈1 米，海盗场景 1 单位≈10 米，炮台 1 单位≈0.5 米。
#:   1000 m/s 级速度与 20 米级的房间尺度要自洽，必须先归一化）。
TARGET_METERS = 6.0
TARGET_MIN = 3.0
TARGET_MAX = 6.0

#: 素材库全局缩放：原始 glb → 逻辑米。（按目录各自量级实测：实体 0.85~0.95 米高、户型到 1.5 米）
ASSET_LIBRARY_SCALE = 1.5

#: 房间类别名（必须与 scripts/spawn/rooms/RoomTypes.cs 的 RoomCategory 一致；Undecided 不进数据）。
ROOM_CATEGORIES = ["Start", "Combat", "Special", "Hidden", "Prototype", "Boss", "Safe"]

ALL_COMBAT = ["Combat", "Boss", "Prototype"]
#: 全部「玩法可达」房间（不含 Prototype：原型房只给机关/怪物，空房间便于对比）。
ALL_ROOMS = ["Start", "Combat", "Special", "Hidden", "Boss", "Safe"]


def _spec(layer, weight, categories, clearance, scale=(0.9, 1.1)):
    return {
        "layer": layer,
        "weight": weight,
        "categoryMask": list(categories),
        "clearance": clearance,
        "scaleMin": scale[0],
        "scaleMax": scale[1],
    }


# ─────────────────────────────────────────────────────────────
# 1. 目录默认行：没被单文件规则命中的模型按目录套默认
# ─────────────────────────────────────────────────────────────

FOLDER_DEFAULT = {
    # RTS 建筑：地面大件（房屋/塔/营地/市场/地形）
    "RTS建筑": _spec("Ground", 6, ALL_ROOMS, 0.5),
    # 海盗场景：木桶/箱子/锚/棕榈/岩石/船
    "海盗场景带动画": _spec("Ground", 10, ALL_ROOMS, 0.4),
    # 马里奥平台：箱/栅栏/管道/标牌/平台
    "马里奥风格平台": _spec("Ground", 10, ALL_ROOMS, 0.4),
    # 小房子：整栋房子做中心建筑
    "小房子": _spec("Ground", 4, ["Start", "Safe", "Special"], 0.6),
    # 炮台：当作房间里的「重机械」落地件
    "炮台": _spec("Ground", 4, ["Combat", "Boss", "Prototype"], 1.6),
}

# ─────────────────────────────────────────────────────────────
# 2. 整目录跳过（非装饰件）
# ─────────────────────────────────────────────────────────────

SKIP_FOLDER = {
    "RPG武器": "武器/护甲/珠宝/书卷类：属于拾取物或装备外观，不是房间静态装饰。"
               "个别可用作货架摆件（Shield/Book/Chalice）已作为**货架陈列**在 docs 里登记，未进 v1 目录。",
    "枪械": "枪械与瞄具：拾取物/装备外观，不是房间静态装饰。",
    "卡通人物带全套动画": "带骨骼动画的人物（4 个）：属于角色，不是装饰；将来走角色/怪物素材管线。",
    "POLYGON_Starter_SourceFiles_v2": "源工程文件包，不含 .glb。",
}

# ─────────────────────────────────────────────────────────────
# 3. 单文件规则（覆盖目录默认；skip 表示排除）
# ─────────────────────────────────────────────────────────────

RULES = {
    # ── 海盗场景带动画：有骨骼/动画的实体不是静态装饰 ──
    "海盗场景带动画/Anne.glb": {"skip": "带骨骼动画的人物"},
    "海盗场景带动画/Henry.glb": {"skip": "带骨骼动画的人物"},
    "海盗场景带动画/Mako.glb": {"skip": "带骨骼动画的人物"},
    "海盗场景带动画/Pirate Captain.glb": {"skip": "带骨骼动画的人物"},
    "海盗场景带动画/Skeleton.glb": {"skip": "带骨骼动画的人物"},
    "海盗场景带动画/Skeleton-yq5ATpujSt.glb": {"skip": "带骨骼动画的人物"},
    "海盗场景带动画/Shark.glb": {"skip": "带骨骼动画的动物"},
    "海盗场景带动画/Sharky.glb": {"skip": "带骨骼动画的动物"},
    "海盗场景带动画/Bird.glb": {"skip": "动物（非场景装饰）"},
    "海盗场景带动画/Tentacle.glb": {"skip": "动画触手，尺寸 19 米级、不属于房间静态装饰"},
    "海盗场景带动画/Red X.glb": {"skip": "地面记号（贴花），不是立体装饰"},
    "海盗场景带动画/Wheat Icon.glb": {"skip": "图标"},
    "海盗场景带动画/Chicken Leg.glb": {"skip": "食物小件，俯视看不清"},
    "海盗场景带动画/Fish Mackerel.glb": {"skip": "拾取物级小件"},
    "海盗场景带动画/Fish Tuna.glb": {"skip": "拾取物级小件"},
    "海盗场景带动画/Cutlass.glb": {"skip": "手持武器"},
    "海盗场景带动画/Dagger.glb": {"skip": "手持武器"},
    "海盗场景带动画/Axe.glb": {"skip": "手持武器"},
    "海盗场景带动画/Axe-FkzZ6I1zjT.glb": {"skip": "手持武器"},
    "海盗场景带动画/Axe Rifle.glb": {"skip": "手持武器"},
    "海盗场景带动画/Sword.glb": {"skip": "手持武器"},
    "海盗场景带动画/Sword-Ds2bJiNI5w.glb": {"skip": "手持武器"},
    "海盗场景带动画/Swords.glb": {"skip": "手持武器"},
    "海盗场景带动画/Rifle.glb": {"skip": "手持武器"},
    "海盗场景带动画/Pistol.glb": {"skip": "手持武器"},
    "海盗场景带动画/Shotgun.glb": {"skip": "手持武器"},
    "海盗场景带动画/Lute.glb": {"skip": "乐器小件，俯视看不清"},
    "海盗场景带动画/Paper.glb": {"skip": "纸张小件"},
    "海盗场景带动画/Prop Bottle.glb": {"skip": "酒瓶小件"},
    "海盗场景带动画/Prop Bottle-uRzqB8lopZ.glb": {"skip": "酒瓶小件"},
    "海盗场景带动画/Coins.glb": {"skip": "金币小件"},
    "海盗场景带动画/Gem Blue.glb": {"skip": "宝石小件"},
    "海盗场景带动画/Gem Green.glb": {"skip": "宝石小件"},
    "海盗场景带动画/Gem Pink.glb": {"skip": "宝石小件"},
    "海盗场景带动画/Gold Bag.glb": {"skip": "金币袋小件（奖励物）"},
    "海盗场景带动画/Gold ore.glb": {"skip": "矿石小件"},
    "海盗场景带动画/Skull.glb": {"skip": "头骨小件"},
    "海盗场景带动画/Skull-VGtSTNRf2O.glb": {"skip": "头骨小件"},
    "海盗场景带动画/Large Bone.glb": {"skip": "巨型骨架，6 米级超宽，遮挡视野"},
    "海盗场景带动画/Bomb.glb": {"skip": "炸弹：属于玩法道具，不是装饰"},
    "海盗场景带动画/Post.glb": {"skip": "3 米细杆，作为装饰太单薄"},
    # 船：尺寸 15 米级航船，房间放不下（单独留给「码头房」玩法）
    "海盗场景带动画/Ship.glb": {"skip": "15 米级大船，房间放不下"},
    "海盗场景带动画/Small Ship.glb": {"skip": "5 米级船，属于载具玩法，不是装饰"},
    "海盗场景带动画/Dock.glb": {"weight": 4, "clearance": 0.8},
    "海盗场景带动画/Dock Broken.glb": {"weight": 4, "clearance": 0.8},
    # 大块岩石群 / 大宅：占地过大，房间放不下
    "海盗场景带动画/House-2kytqGs4rH.glb": {"skip": "8.5×10.6 米级大宅，房间放不下"},
    "海盗场景带动画/House-g7eSJLFi4V.glb": {"skip": "13.5 米级超宽宅邸，房间放不下"},
    "海盗场景带动画/House.glb": {"skip": "9.4 米高的塔楼式房子，与 RTS 建筑重复且过高"},
    "海盗场景带动画/Rocks-IFU6cm2Xow.glb": {"skip": "12×10 米级岩群，房间放不下"},
    "海盗场景带动画/Rocks-fy3szRMvuE.glb": {"skip": "11×10 米级岩群，房间放不下"},
    "海盗场景带动画/Rocks-38eDa0gjwZ.glb": {"skip": "10×9 米级岩群，房间放不下"},
    "海盗场景带动画/Rocks-e1rgb5i2kF.glb": {"skip": "8.4×8.1 米级岩群，房间放不下"},
    "海盗场景带动画/Rocks.glb": {"skip": "岩石小件（0.4 米），单独摆放看不见"},
    # 小房子 → 整栋房子只在「非战斗房」当中心建筑
    # 棕榈树：高度 3.5 米以上，做「垂吊绿植」不合适，落地树
    "海盗场景带动画/Palm Tree.glb": {"weight": 6},
    "海盗场景带动画/Palm Tree-A6cKJYFsIb.glb": {"weight": 6},
    "海盗场景带动画/Palm Tree-P0tgwyXBgr.glb": {"weight": 6},
    "海盗场景带动画/Sawmill.glb": {"weight": 4},
    "海盗场景带动画/Cannon.glb": {"weight": 4, "clearance": 0.8, "categories": ALL_COMBAT},
    # 宝箱/金币箱：奖励房的视觉锚点
    "海盗场景带动画/Chest Gold.glb": {"layer": "Ground", "weight": 3, "categories": ["Special", "Hidden", "Boss"], "clearance": 0.8},
    "海盗场景带动画/Chest Closed.glb": {"layer": "Ground", "weight": 3, "categories": ["Special", "Hidden", "Boss"], "clearance": 0.8},

    # ── RTS 建筑：按「占地/高度」分层 ──
    # 巨构：只在 Boss/起点做中央地标
    "RTS建筑/Castle Fortress.glb": {"weight": 3, "categories": ["Boss"], "clearance": 1.2},
    "RTS建筑/Castle.glb": {"weight": 3, "categories": ["Boss"], "clearance": 1.2},
    "RTS建筑/Castle-y15yE6kWLY.glb": {"skip": "与 Castle.glb 重复的变体"},
    "RTS建筑/Fortress.glb": {"weight": 4, "categories": ["Boss", "Special"], "clearance": 1.0},
    "RTS建筑/Wooden Fortress.glb": {"weight": 4, "categories": ["Boss", "Combat"], "clearance": 1.0},
    "RTS建筑/Wonder First Age Leve.glb": {"weight": 2, "categories": ["Special"], "clearance": 1.2},
    "RTS建筑/Temple-CE2Mn7lh6A.glb": {"weight": 2, "categories": ["Special", "Safe"], "clearance": 1.2},
    "RTS建筑/Temple-v36uJiErXS.glb": {"skip": "塔庙变体，与 Temple-CE2Mn7lh6A 重复"},
    "RTS建筑/Temple First Age Leve.glb": {"weight": 3, "categories": ["Special", "Safe"], "clearance": 1.0},
    "RTS建筑/Wodden Temple.glb": {"skip": "与 Temple First Age Leve 同几何的木质变体，二选一"},
    "RTS建筑/Temple.glb": {"weight": 4, "categories": ["Special", "Safe", "Start"], "clearance": 1.0},
    "RTS建筑/Temple-nR264crTSr.glb": {"skip": "方形神庙变体，与 Temple 重复"},
    "RTS建筑/Docks.glb": {"weight": 3, "clearance": 1.0},
    "RTS建筑/Dock.glb": {"weight": 4, "categories": ["Combat", "Special", "Safe"]},
    "RTS建筑/Dock-XViKoBh2UN.glb": {"skip": "与 Dock.glb 重复的变体"},
    "RTS建筑/Port.glb": {"weight": 3, "clearance": 1.0},
    "RTS建筑/Port-4sE6lmhGPF.glb": {"skip": "与 Port.glb 重复的变体"},
    "RTS建筑/Shipping Port.glb": {"weight": 3, "clearance": 1.0},
    "RTS建筑/Shipping Port-rDX8W8uqip.glb": {"skip": "与 Shipping Port.glb 重复的变体"},
    "RTS建筑/Mountains.glb": {"skip": "山脉群 4.6×3.8 米，超出房间装饰尺度"},
    "RTS建筑/Mountain Group.glb": {"weight": 3, "clearance": 1.5, "categories": ["Combat", "Boss", "Prototype"]},
    "RTS建筑/Mountain.glb": {"weight": 4, "clearance": 1.0, "categories": ALL_COMBAT},
    "RTS建筑/Mountain-XY4ej3Zg3I.glb": {"skip": "与 Mountain.glb 重复的变体"},
    "RTS建筑/Castle Gate.glb": {"weight": 3, "clearance": 1.0, "categories": ["Boss", "Combat"]},
    "RTS建筑/Stone Wall Towers.glb": {"weight": 3, "clearance": 1.0, "categories": ["Boss", "Combat"]},
    "RTS建筑/Wall Towers Door Seco.glb": {"skip": "与 Stone Wall Towers 重复的门楼变体"},
    "RTS建筑/Wall Towers.glb": {"weight": 4, "clearance": 0.8, "categories": ["Boss", "Combat"]},
    "RTS建筑/Wooden Wall.glb": {"weight": 5, "clearance": 0.6, "categories": ["Boss", "Combat", "Special"]},
    "RTS建筑/Wooden Wall-L0TxLurnES.glb": {"skip": "与 Wooden Wall.glb 重复的变体"},
    "RTS建筑/Wooden Fortress Gate.glb": {"weight": 3, "clearance": 0.8, "categories": ["Boss", "Combat"]},
    "RTS建筑/Stone Wall.glb": {"layer": "Wall", "weight": 6, "clearance": 0.3, "categories": ["Boss", "Combat", "Special"]},
    # 地形平铺件（农田/树桩）：薄片贴地，没有立体感
    "RTS建筑/Farm Dirt.glb": {"skip": "地面贴地块（0.06 米厚），没有立体轮廓"},
    "RTS建筑/Farm-O4wpTLSfIn.glb": {"weight": 4},
    "RTS建筑/Farm-91wMLb9kKo.glb": {"weight": 4},
    "RTS建筑/Crops.glb": {"weight": 3, "clearance": 0.8, "categories": ["Start", "Safe", "Special", "Combat"]},
    "RTS建筑/Trees cut.glb": {"skip": "被砍倒的树，低矮地面件，俯视辨识度低"},
    "RTS建筑/Trees cut-zhm3D5KKRh.glb": {"skip": "同 Trees cut.glb 的变体"},
    "RTS建筑/Trees.glb": {"weight": 6, "clearance": 0.8},
    "RTS建筑/Pine Trees.glb": {"weight": 7, "clearance": 0.8},
    # 小碎石：0.2 米级，俯视看不见
    "RTS建筑/Rock.glb": {"skip": "碎石 0.15 米级，俯视不可辨"},
    "RTS建筑/Rocks.glb": {"skip": "碎石 0.4 米级，用 Rocks-* 组或 Rock-*.glb 单体"},
    "RTS建筑/Rock-JmFMh7ztL9.glb": {"weight": 8, "clearance": 0.3},
    "RTS建筑/Rock-RtLRqYjfMs.glb": {"weight": 8, "clearance": 0.3},
    "RTS建筑/Gold rocks.glb": {"weight": 5, "clearance": 0.4, "categories": ["Special", "Hidden", "Boss"]},
    "RTS建筑/Resource Gold.glb": {"weight": 5, "clearance": 0.3, "categories": ["Special", "Hidden"]},
    "RTS建筑/Logs.glb": {"weight": 7, "clearance": 0.3},
    "RTS建筑/Town Center.glb": {"skip": "只有地基（0.3 米高），不是完整建筑"},
    "RTS建筑/Town Center Second Age.glb": {"skip": "与 Town Center-cuQuFJEIfH 同几何的第二时代变体，二选一"},
    "RTS建筑/Town Center-76GTkSh4KM.glb": {"weight": 3, "categories": ["Start", "Safe", "Special"], "clearance": 0.8},
    "RTS建筑/Town Center-CoERW5nFdE.glb": {"skip": "与 Town Center-76GTkSh4KM 重复的变体"},
    "RTS建筑/Town Center-cuQuFJEIfH.glb": {"weight": 3, "categories": ["Start", "Special"], "clearance": 1.0},
    "RTS建筑/Market Stalls.glb": {"weight": 6, "categories": ["Special", "Start", "Safe"]},
    "RTS建筑/Market Stalls Compact.glb": {"skip": "与 Market Stalls.glb 重复的紧凑变体"},
    "RTS建筑/Market Stalls-4ZAhRv2tLG.glb": {"skip": "与 Market Stalls.glb 重复的变体"},
    "RTS建筑/Market Stalls-OLd8vu6lPL.glb": {"skip": "与 Market Stalls.glb 重复的变体"},
    "RTS建筑/Market Stalls-PUZZ5F91OE.glb": {"skip": "与 Market Stalls.glb 重复的变体"},
    "RTS建筑/Village Market.glb": {"weight": 4, "categories": ["Special", "Start"]},
    "RTS建筑/Archery Building.glb": {"weight": 4, "categories": ALL_COMBAT},
    "RTS建筑/Archery Second Age Le.glb": {"skip": "Archery Building 的第二时代变体，二选一"},
    "RTS建筑/Archery Towers.glb": {"weight": 4, "categories": ALL_COMBAT},
    "RTS建筑/Archery Towers-IA5qOjIAiT.glb": {"skip": "与 Archery Towers.glb 重复的变体"},
    "RTS建筑/Archery Training Grounds.glb": {"weight": 4, "categories": ALL_COMBAT},
    "RTS建筑/Archery Training Grounds-5dWpau7C3k.glb": {"skip": "与 Archery Training Grounds.glb 重复的变体"},
    "RTS建筑/Barracks.glb": {"weight": 5},
    "RTS建筑/Barracks-lnyADheSvA.glb": {"skip": "兵营变体，与 Barracks.glb 二选一"},
    "RTS建筑/Barracks-UXCOwRBSxx.glb": {"skip": "兵营变体"},
    "RTS建筑/Barracks-a1C1L8gJTX.glb": {"skip": "兵营变体"},
    "RTS建筑/Barracks-dvlksXgxWc.glb": {"skip": "兵营变体"},
    "RTS建筑/Barracks-puD1kbV4kf.glb": {"skip": "兵营变体"},
    "RTS建筑/Business Building.glb": {"weight": 4, "categories": ["Special", "Start"]},
    "RTS建筑/Storage House.glb": {"weight": 5, "categories": ["Special", "Combat", "Safe"]},
    "RTS建筑/Storage Hut.glb": {"weight": 5, "categories": ["Combat", "Safe", "Start"]},
    "RTS建筑/Storage shed.glb": {"skip": "与 Storage shed-fkTsDdpQAA 重复的变体，二选一"},
    "RTS建筑/Storage shed-fkTsDdpQAA.glb": {"weight": 5, "categories": ["Combat", "Safe"]},
    "RTS建筑/Mine.glb": {"weight": 4, "categories": ["Combat", "Special"], "clearance": 0.8},
    "RTS建筑/Windmill.glb": {"skip": "风车旋翼外扩，Boss 房全景视角会穿模"},
    "RTS建筑/Windmill-jpHoi9xDLG.glb": {"skip": "Windmill.glb 变体"},
    "RTS建筑/Small Farm.glb": {"weight": 4, "categories": ["Start", "Safe", "Special"]},
    "RTS建筑/Farm.glb": {"skip": "Farm-91wMLb9kKo / Farm-O4wpTLSfIn 已覆盖，三选二"},
    "RTS建筑/House.glb": {"weight": 5, "categories": ["Start", "Safe", "Combat", "Special"]},
    "RTS建筑/House-k6tP5nFUd2.glb": {"skip": "House 变体，与 House.glb 二选一"},
    "RTS建筑/House-nihGGju7DW.glb": {"skip": "House 变体"},
    "RTS建筑/House-oJJIRwv6Bo.glb": {"skip": "House 变体"},
    "RTS建筑/House-RSwoYSLblu.glb": {"skip": "House 变体"},
    "RTS建筑/House-vZ1CLbWmSx.glb": {"skip": "House 变体"},
    "RTS建筑/House-YlADpCJU8U.glb": {"skip": "House 变体"},
    "RTS建筑/Houses.glb": {"skip": "与 Houses-gEjAC1UvVU / Houses-vCoDG5hFyI 重复的变体"},
    "RTS建筑/Houses-gEjAC1UvVU.glb": {"weight": 3, "categories": ["Start", "Special"], "clearance": 1.0},
    "RTS建筑/Houses-vCoDG5hFyI.glb": {"skip": "Houses 变体"},
    "RTS建筑/Hut.glb": {"weight": 6, "categories": ["Start", "Safe", "Combat"]},
    "RTS建筑/Hut-4MJWbyd6vw.glb": {"skip": "Hut 变体"},
    "RTS建筑/Hut-wxi3kAu5ey.glb": {"skip": "Hut 变体"},
    "RTS建筑/Huts.glb": {"weight": 5, "categories": ["Start", "Safe", "Combat"]},
    "RTS建筑/Shack.glb": {"weight": 6, "categories": ["Combat", "Prototype"]},
    "RTS建筑/Shack-HuzLJcbUd2.glb": {"skip": "Shack 变体"},
    "RTS建筑/Tower House.glb": {"weight": 4, "categories": ["Combat", "Boss"]},
    "RTS建筑/Wooden house tower.glb": {"weight": 4, "categories": ["Combat", "Boss"]},
    "RTS建筑/Watch Tower.glb": {"weight": 5, "categories": ALL_COMBAT},
    "RTS建筑/Watch Tower-cMxuj2gt7D.glb": {"skip": "Watch Tower 变体"},
    "RTS建筑/Watch Tower-VJZZW37Vsk.glb": {"skip": "Watch Tower 变体"},
    "RTS建筑/Small Watch Tower.glb": {"weight": 6, "categories": ALL_COMBAT},
    "RTS建筑/Stone Tower.glb": {"weight": 5, "categories": ALL_COMBAT},
    "RTS建筑/Stone Tower-dJLAD6p90F.glb": {"skip": "Stone Tower 变体"},
    "RTS建筑/Wooden Encampment.glb": {"weight": 4, "categories": ["Start", "Safe", "Combat"]},
    "RTS建筑/Wooden Monument.glb": {"weight": 4, "categories": ["Special", "Safe"]},

    # ── 马里奥平台：低多边形卡通件 ──
    "马里奥风格平台/Crate.glb": {"weight": 12, "clearance": 0.3},
    "马里奥风格平台/Yellow Box.glb": {"weight": 10, "clearance": 0.3},
    "马里奥风格平台/Mushroom.glb": {"weight": 8, "clearance": 0.3},
    "马里奥风格平台/Pipe.glb": {"weight": 6, "clearance": 0.5},
    "马里奥风格平台/Fence.glb": {"layer": "Wall", "weight": 8, "clearance": 0.2},
    "马里奥风格平台/Sign.glb": {"layer": "Wall", "weight": 6, "clearance": 0.2},
    "马里奥风格平台/Sign-lGC5MHYT85.glb": {"skip": "与 Sign.glb 重复的变体"},
    "马里奥风格平台/Flag.glb": {"layer": "Wall", "weight": 5, "clearance": 0.3},
    "马里奥风格平台/Ladder.glb": {"layer": "Wall", "weight": 4, "clearance": 0.2},
    "马里奥风格平台/Platform.glb": {"skip": "7.3 米超宽平台，与房间地面重复"},
    "马里奥风格平台/Spike Platform.glb": {"weight": 4, "clearance": 0.5, "categories": ["Combat", "Prototype"]},
    "马里奥风格平台/Bear Trap.glb": {"weight": 4, "clearance": 0.5, "categories": ["Combat", "Prototype"]},
    "马里奥风格平台/Bomb.glb": {"skip": "炸弹：玩法道具"},
    "马里奥风格平台/Coin.glb": {"skip": "金币小件"},
    "马里奥风格平台/Heart.glb": {"skip": "生命值图标"},
    "马里奥风格平台/Key.glb": {"skip": "钥匙小件"},
    "马里奥风格平台/Star.glb": {"skip": "星星小件"},
    "马里奥风格平台/Cloud.glb": {"layer": "Ceiling", "weight": 5, "clearance": 0.0},
    "马里奥风格平台/Cloud-F6DzCxDz6I.glb": {"layer": "Ceiling", "weight": 4, "clearance": 0.0},
    "马里奥风格平台/Cloud-jTIsWRQd7T.glb": {"skip": "与 Cloud.glb 重复的变体"},
    "马里奥风格平台/Cloud-KdFNOVn1Gf.glb": {"skip": "与 Cloud.glb 重复的变体"},
    "马里奥风格平台/Tree.glb": {"weight": 6, "clearance": 0.8},
    "马里奥风格平台/Tree-8qABc2Nslz.glb": {"skip": "与 Tree.glb 重复的变体"},

    # ── 炮台：重机械落地件，只给战斗类房间，间距放大 ──
    "炮台/Emp Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Flamethrower Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Gatelng Gun Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Gun Cannon Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Hive Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Lighting Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Missile Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Plasma Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Rail Gun Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},
    "炮台/Shield Turret.glb": {"categories": ["Combat", "Boss", "Prototype"]},

    # ── 小房子 ──
    "小房子/House.glb": {"weight": 6, "categories": ["Start", "Safe", "Special"], "clearance": 0.5},
    "小房子/House-7VSVwAg2T3.glb": {"skip": "House.glb 变体"},
    "小房子/Two story house.glb": {"weight": 5, "categories": ["Start", "Safe", "Special"]},
    "小房子/Two story house-9N6ROCbmO1.glb": {"skip": "Two story house 变体"},
    "小房子/Two story house-hmXhiLDf8D.glb": {"skip": "Two story house 变体"},
    "小房子/Two story house-htvFgnVP4d.glb": {"skip": "Two story house 变体"},
    "小房子/Two story house-QsF9E0PqyN.glb": {"skip": "Two story house 变体"},
    "小房子/Two story house-sGgL4Nt7I7.glb": {"skip": "Two story house 变体"},
}
