using System;
using System.Collections.Generic;
using Godot;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 地牢宿主层的**全部可调参数**：房间图尺寸/数量、格边长、随机种子、房型登记表、装饰表与相机参数。
/// <para>
/// <b>为什么是 Resource 而不是普通 class</b>：与 <see cref="WorldHostOptions"/> 同一套理由——
/// 规则4 要求数值走「文件 / Excel / 插件可视化修改」。资源 <c>.tres</c> 既是文件、又能在 Inspector
/// 里可视化调；Excel 落地后只需在 <c>DataLayer</c> 里把本资源的字段按 Def 覆盖一遍，宿主装配代码零改动。
/// </para>
/// <para>
/// <b>为什么它只做「翻译」</b>：本类把 Resource 翻译成纯 C# 的 <see cref="DungeonConfig"/>、
/// <see cref="RoomCategoryTable"/>、<see cref="DecorCatalog"/>、<see cref="DungeonRoomStyleSet"/>，
/// 之后规划器 / 视图 / 建场景器**只认纯 C# 值对象**，不认识 <see cref="Resource"/>
/// （J4：引擎类型只在宿主层出现）。
/// </para>
/// <para>
/// <b>种子语义</b>：<see cref="Seed"/> = 0 表示「本次启动取一个随机种子并打印到控制台」，
/// 非 0 则严格复现。同 seed + 同配置 → 逐字节相同的地牢图（<see cref="IDungeonPlanner"/> 的确定性契约）。
/// </para>
/// <para>
/// <b>为什么房型登记表 / 装饰表是 Resource 行而不是直接暴露 <c>RoomCategorySpec</c> 结构</b>：
/// 那两个结构没有任何 <c>[Export]</c> 支持（纯 C#），在 Inspector 里是一整块不可编辑的 struct；
/// 用 <c>.tres</c> 里的 Resource 行数组才能「加一行 = 加一种房型」（规则7 的可视化落地）。
/// </para>
/// </summary>
[GlobalClass]
public partial class DungeonOptions : Resource
{
    // ─────────────────────────────────────────────────────────────
    // 1. 房间图网格与尺寸（对应 DungeonConfig 的同名字段）
    // ─────────────────────────────────────────────────────────────

    /// <summary>地牢图网格宽（格）。</summary>
    [Export(PropertyHint.Range, "1,32,1")]
    public int GridWidth { get; set; } = GridWidthDefault;

    /// <summary>地牢图网格高（格）。</summary>
    [Export(PropertyHint.Range, "1,32,1")]
    public int GridHeight { get; set; } = GridHeightDefault;

    /// <summary>普通房边长（格）。</summary>
    [Export(PropertyHint.Range, "1,16,1")]
    public int RoomSide { get; set; } = RoomSideDefault;

    /// <summary>起始房边长（格）。</summary>
    [Export(PropertyHint.Range, "1,16,1")]
    public int StartRoomSide { get; set; } = StartRoomSideDefault;

    /// <summary>Boss 房边长（格）。</summary>
    [Export(PropertyHint.Range, "1,16,1")]
    public int BossRoomSide { get; set; } = BossRoomSideDefault;

    // ─────────────────────────────────────────────────────────────
    // 2. 房间数量与拓扑约束
    // ─────────────────────────────────────────────────────────────

    /// <summary>目标普通房数量（不含起点/Boss/隐藏房）。</summary>
    [Export(PropertyHint.Range, "0,64,1")]
    public int TargetRoomCount { get; set; } = TargetRoomCountDefault;

    /// <summary>房间数允许偏差（0 = 必须精确）。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int RoomCountTolerance { get; set; } = RoomCountToleranceDefault;

    /// <summary>隐藏房数量。</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int HiddenRoomCount { get; set; } = HiddenRoomCountDefault;

    /// <summary>特殊功能房（商店等）数量下界。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int MinSpecialRooms { get; set; } = MinSpecialRoomsDefault;

    /// <summary>特殊功能房数量上界。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int MaxSpecialRooms { get; set; } = MaxSpecialRoomsDefault;

    /// <summary>Boss 房离起点的最小出生树步数。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int MinBossDepth { get; set; } = MinBossDepthDefault;

    /// <summary>每个生长步尝试长几间房。</summary>
    [Export(PropertyHint.Range, "1,8,1")]
    public int RoomsPerStep { get; set; } = RoomsPerStepDefault;

    /// <summary>关键路径上允许出现特殊房的概率（0 = 关键路径只放战斗房）。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SpecialOnCriticalPathChance { get; set; } = SpecialOnCriticalPathChanceDefault;

    /// <summary>隐藏房离起点的最小步数。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int MinHiddenDepth { get; set; } = MinHiddenDepthDefault;

    /// <summary>关键路径循环捷径距离（0 = 不连环形捷径）。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int LoopDistance { get; set; } = LoopDistanceDefault;

    // ─────────────────────────────────────────────────────────────
    // 3. 尺度与随机
    // ─────────────────────────────────────────────────────────────

    /// <summary>每格边长（米）。所有几何尺寸（房、墙、门洞）都由它派生，不另写尺寸字面量。</summary>
    [Export(PropertyHint.Range, "0.5,16,0.1")]
    public float CellSizeMeters { get; set; } = CellSizeMetersDefault;

    /// <summary>本层种子（0 = 本次启动取随机种子并打印）。</summary>
    [Export]
    public int Seed { get; set; } = SeedDefault;

    /// <summary>是否把原型测试房纳入随机池（正式关卡应为 false）。</summary>
    [Export]
    public bool IncludePrototypeRooms { get; set; } = IncludePrototypeRoomsDefault;

    /// <summary>规划器内部的重试上限（防止病态配置无限循环；0 = 交给规划器默认值）。</summary>
    [Export(PropertyHint.Range, "0,100000,1")]
    public int PlannerMaxAttempts { get; set; } = PlannerMaxAttemptsDefault;

    // ─────────────────────────────────────────────────────────────
    // 4. 房型登记表（规划器只认这张表，不认具体房间名）
    // ─────────────────────────────────────────────────────────────

    /// <summary>房型规格列表（类别 / 模板 / 权重 / 随机池 / 安全房 / 关键路径）。</summary>
    [Export]
    public Godot.Collections.Array<DungeonRoomSpecResource> RoomSpecs { get; set; } = new();

    /// <summary>内容槽位容量上限（规划期一次性分配，超出应抛而不是截断）。</summary>
    [Export(PropertyHint.Range, "0,65536,16")]
    public int ContentSlotCapacity { get; set; } = ContentSlotCapacityDefault;

    /// <summary>装饰件容量上限（同上）。</summary>
    [Export(PropertyHint.Range, "0,65536,16")]
    public int DecorCapacity { get; set; } = DecorCapacityDefault;

    // ─────────────────────────────────────────────────────────────
    // 5. 装饰表（规划器只认这张表）
    // ─────────────────────────────────────────────────────────────

    /// <summary>装饰规格列表（编号 / 摆放层 / 权重 / 类别掩码 / 间距 / 缩放区间）。</summary>
    [Export]
    public Godot.Collections.Array<DungeonDecorSpecResource> DecorSpecs { get; set; } = new();

    // ─────────────────────────────────────────────────────────────
    // 6. 相机与光照（表现侧的可调参数）
    // ─────────────────────────────────────────────────────────────

    /// <summary>相机俯仰角（度，向下看）。90 = 正俯视。</summary>
    [Export(PropertyHint.Range, "30,90,1")]
    public float CameraPitchDegrees { get; set; } = CameraPitchDegreesDefault;

    /// <summary>相机偏航角（度）：0 = 正北朝上，让 +Z 朝屏幕下方。</summary>
    [Export(PropertyHint.Range, "0,360,1")]
    public float CameraYawDegrees { get; set; } = CameraYawDegreesDefault;

    /// <summary>相机与整层地牢包围盒之间的留白系数（1 = 刚好贴边）。</summary>
    [Export(PropertyHint.Range, "1,2,0.01")]
    public float CameraPaddingFactor { get; set; } = CameraPaddingFactorDefault;

    /// <summary>相机透视 FOV（度）；启用正交相机时兼作「正交可见高度」的推导参数。</summary>
    [Export(PropertyHint.Range, "20,90,1")]
    public float CameraFovDegrees { get; set; } = CameraFovDegreesDefault;

    /// <summary>是否使用正交相机（俯视角看整层更不容易透视变形）。</summary>
    [Export]
    public bool UseOrthographicCamera { get; set; } = UseOrthographicCameraDefault;

    // ─────────────────────────────────────────────────────────────
    // 6.5 方砖与取景（3D 关卡的表现契约；人物归一化的基准）
    // ─────────────────────────────────────────────────────────────

    /// <summary>3D 场景用「方砖」表现（2D 规则一张图 → 一格一块立方体），还是用旧的逐房间盒子几何。</summary>
    /// <remarks>
    /// <b>为什么留着旧的</b>：<see cref="DungeonRoomView"/> 那套是按 <see cref="RoomPlan"/> 建墙的，
    /// 它跟 2D 图（<see cref="Game.Spawn.Rooms.DungeonMap"/>）是**两条独立的真源**——
    /// 2D 图里有障碍/物件/走廊，房间盒子几何里没有。方砖路径才是「2D 规则驱动 3D」的那条。
    /// 旧路径保留是为了对照排查（例如「方砖是不是跟房间对齐了」）。
    /// </remarks>
    [Export]
    public bool UseTileBoard { get; set; } = true;

    /// <summary>
    /// **方砖边长（米）**——整张 3D 关卡、以及将来的人物/子弹尺寸都按它归一。
    /// <para>见 <see cref="BoardGeometry"/> 的类注释：6 米的取舍（素材缩放落在 1.5~5 倍，中房 24 米正好像 2 屏宽）。</para>
    /// </summary>
    [Export(PropertyHint.Range, "1,32,0.5")]
    public float TileSizeMeters { get; set; } = TileSizeMetersDefault;

    /// <summary>一砖含几格（原型阶段恒为 1；正交相机下 1 砖 = 1 格，屏幕上就是正方形）。</summary>
    [Export(PropertyHint.Range, "1,4,1")]
    public int CellSizeCells { get; set; } = CellSizeCellsDefault;

    /// <summary>墙高（米）；&lt;= 0 表示「等于方砖边长」。</summary>
    [Export(PropertyHint.Range, "-1,32,0.5")]
    public float WallHeightMeters { get; set; } = WallHeightMetersDefault;

    /// <summary>地面板厚（米）；&lt;= 0 表示按方砖比例。</summary>
    [Export(PropertyHint.Range, "-1,8,0.1")]
    public float FloorThicknessMeters { get; set; } = FloorThicknessMetersDefault;

    /// <summary>
    /// 方砖是否在屏幕上等比（1 米横向 = 1 米纵向的象素数）。
    /// <para>
    /// <b>默认 false</b>：因为用户指定的「中房占屏 200%」是**逐轴**算的，
    /// 要让横向与纵向同时 200%，就必须让房间宽 <c>W</c> 与房间纵深压缩后的 <c>W·sinθ</c> 占据等量的屏幕相对高度——
    /// 也就是 1 米横向与 1 米纵向**不再等长**（方砖在屏幕上是横向长条，比值 <c>1/sin60° = 1.155</c>）。
    /// 这是几何硬约束：等比与严格 200% 不能同时成立（见 <see cref="BoardFraming"/> 类注释）。
    /// </para>
    /// <para>
    /// 想要「方砖在屏幕上是正方形」（人物按方砖归一时比例 1:1 最简单），把这个开关打开：
    /// 此时横向仍是 200%，纵向会变成 <c>200%·sinθ = 173%</c>。
    /// </para>
    /// </summary>
    [Export]
    public bool TileAspectIsotropic { get; set; } = false;

    /// <summary>
    /// **取景基准：让一个中等房间占屏幕这么大**（1.0 = 屏宽的 100%，2.0 = 屏宽的 200%）。
    /// <para>
    /// 相机不再「把整层塞进屏幕」——整层 96×96 米塞进 1600×900 时一格只有 8 象素，
    /// 玩家根本看不清方砖，人物也没法按方砖归一。改成「一屏只看固定高度」后，
    /// 中房占屏比例由这个数与方砖尺寸共同决定，启动自检里会逐次核对。
    /// </para>
    /// </summary>
    [Export(PropertyHint.Range, "0.2,8,0.05")]
    public float RoomScreenFillRatio { get; set; } = RoomScreenFillRatioDefault;

    /// <summary>相机纵向可见米数（&gt; 0 时直接用它覆盖自动取景；0 = 按 <see cref="RoomScreenFillRatio"/> 反算）。</summary>
    [Export(PropertyHint.Range, "0,256,0.5")]
    public float CameraVisibleMetersOverride { get; set; } = CameraVisibleMetersOverrideDefault;

    /// <summary>取景用的「中等房间」边长（格）：按 <see cref="RoomSide"/> 算，不从房间表反查，免得被特殊房带偏。</summary>
    public int MediumRoomSideCells => RoomSide;

    // ─────────────────────────────────────────────────────────────
    // 6.6 2D 栅格化参数（3D 方砖路径直接复用 2D 规则的全部开关）
    // ─────────────────────────────────────────────────────────────

    /// <summary>2D 图留白（格）。</summary>
    [Export(PropertyHint.Range, "0,4,1")]
    public int MapMarginCells { get; set; } = 0;

    /// <summary>是否在房间内撒障碍。</summary>
    [Export]
    public bool MapObstaclesEnabled { get; set; } = true;

    /// <summary>障碍密度（0..1，按房间可用格数折算）。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MapObstacleDensity { get; set; } = MapObstacleDensityDefault;

    /// <summary>起点房障碍系数。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MapStartRoomObstacleScale { get; set; } = 0f;

    /// <summary>Boss 房障碍系数。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MapBossRoomObstacleScale { get; set; } = 0.5f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>障碍离墙/离门的最小格距。</summary>
    [Export(PropertyHint.Range, "0,4,1")]
    public int MapObstacleWallClearance { get; set; } = 1; // arch-ok: 走道宽度默认值，由 .tres 覆盖

    /// <summary>保险丝绕路上限（0 = 只报告不修）。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int MapFuseMaxExtraSteps { get; set; } = 0;

    /// <summary>报告路线数上限（纯诊断输出）。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int MapReportedRouteCount { get; set; } = 4; // arch-ok: 报告条数（纯诊断输出）

    /// <summary>是否摆建筑（多格占地）。</summary>
    [Export]
    public bool PropBuildingsEnabled { get; set; } = true;

    /// <summary>每间房建筑数。</summary>
    [Export(PropertyHint.Range, "0,4,1")]
    public int PropBuildingsPerRoom { get; set; } = 1; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>建筑最大边长（格）。</summary>
    [Export(PropertyHint.Range, "2,8,1")]
    public int PropBuildingMaxSide { get; set; } = 3; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>是否摆奖励。</summary>
    [Export]
    public bool PropRewardsEnabled { get; set; } = true;

    /// <summary>战斗房奖励数。</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int PropRewardsPerCombatRoom { get; set; } = 1; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>是否摆可破坏物。</summary>
    [Export]
    public bool PropBreakablesEnabled { get; set; } = true;

    /// <summary>每间房可破坏物数量。</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int PropBreakablesPerRoom { get; set; } = 2; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>是否摆不可破坏物。</summary>
    [Export]
    public bool PropSolidsEnabled { get; set; } = true;

    /// <summary>每间房不可破坏物数量。</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int PropSolidsPerRoom { get; set; } = 1; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>2D 障碍密度默认值（沿用 <see cref="Game.Spawn.Rooms.DungeonMapConfig"/> 的口径）。</summary>
    public const float MapObstacleDensityDefault = 0.08f; // arch-ok: 表现/关卡手感默认值，由 .tres 覆盖

    /// <summary>环境光颜色。</summary>
    [Export]
    public Color AmbientColor { get; set; } = new(0.42f, 0.46f, 0.55f); // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>环境光强度。</summary>
    [Export(PropertyHint.Range, "0,8,0.01")]
    public float AmbientEnergy { get; set; } = AmbientEnergyDefault;

    /// <summary>主平行光颜色。</summary>
    [Export]
    public Color SunColor { get; set; } = new(1f, 0.94f, 0.82f); // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>太阳（主平行光）强度。</summary>
    [Export(PropertyHint.Range, "0,8,0.01")]
    public float SunEnergy { get; set; } = SunEnergyDefault;

    /// <summary>太阳俯仰角（度）。</summary>
    [Export(PropertyHint.Range, "10,90,1")]
    public float SunPitchDegrees { get; set; } = SunPitchDegreesDefault;

    /// <summary>空隙（房间之间的空白地面）颜色。</summary>
    [Export]
    public Color VoidColor { get; set; } = new(0.12f, 0.13f, 0.16f); // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>无地牢时的占位测试地面颜色。</summary>
    [Export]
    public Color PlaceholderFloorColor { get; set; } = new(0.55f, 0.57f, 0.62f); // arch-ok: 资源默认值，可被 .tres 覆盖

    /// <summary>无地牢时的占位格标记颜色。</summary>
    [Export]
    public Color PlaceholderMarkerColor { get; set; } = new(0.9f, 0.72f, 0.3f); // arch-ok: 资源默认值，可被 .tres 覆盖

    /// <summary>冒烟子弹的占位材质颜色（正式素材接入前）。</summary>
    [Export]
    public Color SmokeBulletColor { get; set; } = new(0.95f, 0.42f, 0.15f); // arch-ok: 资源默认值，可被 .tres 覆盖

    /// <summary>冒烟子弹占位材质自发光强度。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SmokeBulletEmissive { get; set; } = 0.6f; // arch-ok: 资源默认值，可被 .tres 覆盖

    /// <summary>无地牢占位文字的像素字号。</summary>
    [Export(PropertyHint.Range, "8,256,1")]
    public int PlaceholderLabelFontSize { get; set; } = 96; // arch-ok: 资源默认值，可被 .tres 覆盖

    /// <summary>房间地面自发光强度（俯视时材质颜色更忠实，不被光照吃掉）。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float RoomEmissiveEnergy { get; set; } = RoomEmissiveEnergyDefault;

    // ─────────────────────────────────────────────────────────────
    // 内容层（GridContentPlanner / DefaultRoomContentSpawner 的数值全部从这里来）
    // ─────────────────────────────────────────────────────────────

    /// <summary>起点房刷怪点数。</summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int StartRoomSpawnCount { get; set; } = StartRoomSpawnCountDefault;

    /// <summary>战斗房刷怪点数。</summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int CombatRoomSpawnCount { get; set; } = CombatRoomSpawnCountDefault;

    /// <summary>特殊房刷怪点数。</summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int SpecialRoomSpawnCount { get; set; } = SpecialRoomSpawnCountDefault;

    /// <summary>Boss 房杂兵刷怪点数。</summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int BossRoomSpawnCount { get; set; } = BossRoomSpawnCountDefault;

    /// <summary>隐藏房刷怪点数。</summary>
    [Export(PropertyHint.Range, "0,32,1")]
    public int HiddenRoomSpawnCount { get; set; } = HiddenRoomSpawnCountDefault;

    /// <summary>特殊房交互位数量。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int SpecialRoomInteractableCount { get; set; } = SpecialRoomInteractableCountDefault;

    /// <summary>战斗房道具位数量。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int CombatRoomPropCount { get; set; } = CombatRoomPropCountDefault;

    /// <summary>隐藏房奖励位数量。</summary>
    [Export(PropertyHint.Range, "0,16,1")]
    public int HiddenRoomRewardCount { get; set; } = HiddenRoomRewardCountDefault;

    /// <summary>槽位半径（格）：内容不能超出该圆。</summary>
    [Export(PropertyHint.Range, "0.01,4,0.01")]
    public float SlotRadiusCells { get; set; } = SlotRadiusCellsDefault;

    /// <summary>离墙最小距离（格）：必须 &gt;= <see cref="SlotRadiusCells"/>。</summary>
    [Export(PropertyHint.Range, "0.01,4,0.01")]
    public float WallClearanceCells { get; set; } = WallClearanceCellsDefault;

    /// <summary>离门最小距离（格）。</summary>
    [Export(PropertyHint.Range, "0,4,0.01")]
    public float DoorClearanceCells { get; set; } = DoorClearanceCellsDefault;

    /// <summary>槽位之间的额外间距（格）。</summary>
    [Export(PropertyHint.Range, "0,4,0.01")]
    public float SlotPaddingCells { get; set; } = SlotPaddingCellsDefault;

    /// <summary>每个槽位的随机落点尝试次数。</summary>
    [Export(PropertyHint.Range, "1,256,1")]
    public int PlacementAttempts { get; set; } = PlacementAttemptsDefault;

    /// <summary>刷怪散射半径下限（格）。</summary>
    [Export(PropertyHint.Range, "0,4,0.01")]
    public float SpawnRadiusMinCells { get; set; } = SpawnRadiusMinCellsDefault;

    /// <summary>刷怪散射半径上限（格）。</summary>
    [Export(PropertyHint.Range, "0,4,0.01")]
    public float SpawnRadiusMaxCells { get; set; } = SpawnRadiusMaxCellsDefault;

    /// <summary>普通敌人受击半径（米）。</summary>
    [Export(PropertyHint.Range, "0.01,8,0.01")]
    public float EnemyHurtboxRadius { get; set; } = EnemyHurtboxRadiusDefault;

    /// <summary>普通敌人碰撞高度（米；0 = 纯球体）。</summary>
    [Export(PropertyHint.Range, "0,8,0.01")]
    public float EnemyHurtboxHeight { get; set; } = EnemyHurtboxHeightDefault;

    /// <summary>普通敌人最大生命。</summary>
    [Export(PropertyHint.Range, "1,100000,1")]
    public float EnemyHealth { get; set; } = EnemyHealthDefault;

    /// <summary>Boss 受击半径（米）。</summary>
    [Export(PropertyHint.Range, "0.01,8,0.01")]
    public float BossHurtboxRadius { get; set; } = BossHurtboxRadiusDefault;

    /// <summary>Boss 最大生命。</summary>
    [Export(PropertyHint.Range, "1,1000000,1")]
    public float BossHealth { get; set; } = BossHealthDefault;

    /// <summary>道具/奖励受击半径（米）。</summary>
    [Export(PropertyHint.Range, "0.01,8,0.01")]
    public float PropHurtboxRadius { get; set; } = PropHurtboxRadiusDefault;

    /// <summary>表现槽位：敌人。</summary>
    [Export(PropertyHint.Range, "1,255,1")]
    public int ViewKindEnemy { get; set; } = ViewKindEnemyDefault;

    /// <summary>表现槽位：Boss。</summary>
    [Export(PropertyHint.Range, "1,255,1")]
    public int ViewKindBoss { get; set; } = ViewKindBossDefault;

    /// <summary>表现槽位：道具。</summary>
    [Export(PropertyHint.Range, "1,255,1")]
    public int ViewKindProp { get; set; } = ViewKindPropDefault;

    // ─────────────────────────────────────────────────────────────
    // 结构性默认值（集中一处；Inspector / .tres 可覆盖）
    // ─────────────────────────────────────────────────────────────

    /// <summary>网格宽默认值（格）。</summary>
    public const int GridWidthDefault = 16;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>网格高默认值（格）。</summary>
    public const int GridHeightDefault = 16;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>普通房边长默认值（格）。</summary>
    public const int RoomSideDefault = 4;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>起始房边长默认值（格）。</summary>
    public const int StartRoomSideDefault = 4;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>Boss 房边长默认值（格）。</summary>
    public const int BossRoomSideDefault = 6;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>目标普通房数默认值。</summary>
    public const int TargetRoomCountDefault = 7;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>房间数容差默认值。</summary>
    public const int RoomCountToleranceDefault = 1;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>隐藏房数默认值。</summary>
    public const int HiddenRoomCountDefault = 1;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>特殊房下界默认值。</summary>
    public const int MinSpecialRoomsDefault = 1;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>特殊房上界默认值。</summary>
    public const int MaxSpecialRoomsDefault = 2;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>Boss 最小深度默认值。</summary>
    public const int MinBossDepthDefault = 2;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>每步长房数默认值。</summary>
    public const int RoomsPerStepDefault = 1;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>关键路径特殊房概率默认值。</summary>
    public const float SpecialOnCriticalPathChanceDefault = 0f;

    /// <summary>隐藏房最小深度默认值。</summary>
    public const int MinHiddenDepthDefault = 2;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>循环捷径距离默认值。</summary>
    public const int LoopDistanceDefault = 0;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>每格米数默认值。</summary>
    public const float CellSizeMetersDefault = 6f;  // arch-ok: Excel/资源落地前的默认值，由 .tres 覆盖

    /// <summary>种子默认值（0 = 随机）。</summary>
    public const int SeedDefault = 0;

    /// <summary>是否纳入原型测试房默认值。</summary>
    public const bool IncludePrototypeRoomsDefault = false;

    /// <summary>规划器尝试上限默认值（0 = 交给规划器）。</summary>
    public const int PlannerMaxAttemptsDefault = 0;

    /// <summary>内容槽容量默认值。</summary>
    public const int ContentSlotCapacityDefault = 512;  // arch-ok: 预分配容量（内存策略，不是玩法数值）

    /// <summary>装饰件容量默认值。</summary>
    public const int DecorCapacityDefault = 256;  // arch-ok: 预分配容量（内存策略，不是玩法数值）

    /// <summary>相机俯仰默认值（度）。</summary>
    public const float CameraPitchDegreesDefault = 60f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>相机偏航默认值（度）。</summary>
    public const float CameraYawDegreesDefault = 0f;

    /// <summary>相机留白系数默认值。</summary>
    public const float CameraPaddingFactorDefault = 1.06f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>相机 FOV 默认值（度）。</summary>
    public const float CameraFovDegreesDefault = 45f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>是否默认正交相机。</summary>
    public const bool UseOrthographicCameraDefault = true;

    /// <summary>环境光默认强度。</summary>
    public const float AmbientEnergyDefault = 0.10f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>太阳默认强度。</summary>
    public const float SunEnergyDefault = 1.35f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>太阳俯仰默认值（度）。</summary>
    public const float SunPitchDegreesDefault = 62f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>房间地面自发光默认强度。</summary>
    public const float RoomEmissiveEnergyDefault = 0.05f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    // ── 方砖与取景默认值（3D 关卡表现契约；人物按这个数归一）──

    /// <summary>方砖边长默认值（米）。**人物按它归一**。</summary>
    public const float TileSizeMetersDefault = 6f;  // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>一砖几格默认值。</summary>
    public const int CellSizeCellsDefault = BoardGeometry.CellSizeCellsDefault;

    /// <summary>墙高默认值（&lt;= 0 表示等于方砖）。</summary>
    public const float WallHeightMetersDefault = BoardGeometry.WallHeightFromTile;

    /// <summary>地面板厚默认值（&lt;= 0 表示按比例）。</summary>
    public const float FloorThicknessMetersDefault = BoardGeometry.FloorThicknessFromTile;

    /// <summary>取景基准默认值：中房占屏 200%。</summary>
    public const float RoomScreenFillRatioDefault = 2f;  // arch-ok: 表现参数默认值（用户指定的占屏比例），由 .tres 覆盖

    /// <summary>可见米数覆盖默认值（0 = 按占比反算）。</summary>
    public const float CameraVisibleMetersOverrideDefault = 0f;

    /// <summary>中房边长的下限（格）：方砖棋盘至少 2×2 才看得出「房间」而不是「一个格子」。</summary>
    public const int MinRoomSideCells = 2;  // arch-ok: 结构下限（不是可调玩法数值）

    // ── 内容层默认值（Excel/资源落地前；全部由 .tres 覆盖，不在别处重复写字面量） ──

    /// <summary>起点房刷怪点数默认值。</summary>
    public const int StartRoomSpawnCountDefault = 0;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>战斗房刷怪点数默认值。</summary>
    public const int CombatRoomSpawnCountDefault = 4;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>特殊房刷怪点数默认值。</summary>
    public const int SpecialRoomSpawnCountDefault = 0;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>Boss 房杂兵刷怪点数默认值。</summary>
    public const int BossRoomSpawnCountDefault = 2;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>隐藏房刷怪点数默认值。</summary>
    public const int HiddenRoomSpawnCountDefault = 0;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>特殊房交互位默认值。</summary>
    public const int SpecialRoomInteractableCountDefault = 1;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>战斗房道具位默认值。</summary>
    public const int CombatRoomPropCountDefault = 1;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>隐藏房奖励位默认值。</summary>
    public const int HiddenRoomRewardCountDefault = 2;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>槽位半径默认值（格）。</summary>
    public const float SlotRadiusCellsDefault = 0.18f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>离墙最小距离默认值（格）。</summary>
    public const float WallClearanceCellsDefault = 0.28f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>离门最小距离默认值（格）。</summary>
    public const float DoorClearanceCellsDefault = 0.30f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>槽位额外间距默认值（格）。</summary>
    public const float SlotPaddingCellsDefault = 0.06f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>落点尝试次数默认值。</summary>
    public const int PlacementAttemptsDefault = 32;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>刷怪散射半径下限默认值（格）。</summary>
    public const float SpawnRadiusMinCellsDefault = 0f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>刷怪散射半径上限默认值（格）。</summary>
    public const float SpawnRadiusMaxCellsDefault = 0.30f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>普通敌人受击半径默认值（米）。</summary>
    public const float EnemyHurtboxRadiusDefault = 0.45f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>普通敌人碰撞高度默认值（米）。</summary>
    public const float EnemyHurtboxHeightDefault = 1.7f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>普通敌人生命默认值。</summary>
    public const float EnemyHealthDefault = 30f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>Boss 受击半径默认值（米）。</summary>
    public const float BossHurtboxRadiusDefault = 1.2f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>Boss 生命默认值。</summary>
    public const float BossHealthDefault = 600f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>道具受击半径默认值（米）。</summary>
    public const float PropHurtboxRadiusDefault = 0.35f;  // arch-ok: Excel 落地前的默认值，由 .tres 覆盖

    /// <summary>表现槽位：敌人默认值。</summary>
    public const int ViewKindEnemyDefault = 1;  // arch-ok: 表现槽位编号（预留给 ViewRegistry 的预制体表）

    /// <summary>表现槽位：Boss 默认值。</summary>
    public const int ViewKindBossDefault = 2;  // arch-ok: 表现槽位编号

    /// <summary>表现槽位：道具默认值。</summary>
    public const int ViewKindPropDefault = 3;  // arch-ok: 表现槽位编号

    // ─────────────────────────────────────────────────────────────
    // 翻译（Resource → 纯 C# 值对象）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 翻译成规划器吃的纯 C# 配置。调用前 <see cref="Validate"/> 已在边界处做过一次明确校验。
    /// </summary>
    public DungeonConfig ToConfig()
    {
        return new DungeonConfig
        {
            GridWidth = GridWidth,
            GridHeight = GridHeight,
            RoomSide = RoomSide,
            StartRoomSide = StartRoomSide,
            BossRoomSide = BossRoomSide,
            TargetRoomCount = TargetRoomCount,
            RoomCountTolerance = RoomCountTolerance,
            HiddenRoomCount = HiddenRoomCount,
            MinSpecialRooms = MinSpecialRooms,
            MaxSpecialRooms = MaxSpecialRooms,
            MinBossDepth = MinBossDepth,
            RoomsPerStep = RoomsPerStep,
            SpecialOnCriticalPathChance = SpecialOnCriticalPathChance,
            MinHiddenDepth = MinHiddenDepth,
            LoopDistance = LoopDistance,
            CellSizeMeters = CellSizeMeters,
        };
    }

    /// <summary>
    /// 翻译成方砖几何契约（<see cref="BoardGeometry"/>）。
    /// <para>
    /// <b>为什么这里不读 <see cref="CellSizeMeters"/></b>：它是**规划期**的口径（2D 图/内容槽以「格」为单位时
    /// 折算成米），而方砖是**表现期**的口径。两者今天数值相同（都 6 米），但语义不同：
    /// 将来若让 1 砖 = 2 格，<see cref="CellSizeMeters"/> 不变而方砖边长翻倍。
    /// </para>
    /// </summary>
    public BoardGeometry ToBoardGeometry()
        => BoardGeometry.Create(TileSizeMeters, CameraPitchDegrees, CellSizeCells, WallHeightMeters, FloorThicknessMeters);

    /// <summary>
    /// 翻译成 2D 地图栅格化配置（3D 方砖路径用它重跑同一套 2D 规则）。
    /// <para>
    /// <b>为什么 3D 要重跑 2D 栅格化</b>：地图的「障碍/物件/走廊」只存在于 <see cref="Game.Spawn.Rooms.DungeonMap"/> 里，
    /// 3D 手上只有布局。用同一个种子重跑一张图，比在 3D 里另写一套摆放规则要可靠得多——
    /// 后者就是「同一概念两条真源」（规则5 §3.4-A 明令禁止）。
    /// </para>
    /// </summary>
    public DungeonMapConfig ToMapConfig()
        => new()
        {
            Margin = MapMarginCells,
            ObstaclesEnabled = MapObstaclesEnabled,
            ObstacleDensity = MapObstacleDensity,
            StartRoomObstacleScale = MapStartRoomObstacleScale,
            BossRoomObstacleScale = MapBossRoomObstacleScale,
            ObstacleWallClearance = MapObstacleWallClearance,
            FuseMaxExtraSteps = MapFuseMaxExtraSteps,
            ReportedRouteCount = MapReportedRouteCount,
        };

    /// <summary>翻译成 2D 物件摆放配置（建筑/奖励/可破坏/不可破坏）。</summary>
    public DungeonPropConfig ToPropConfig()
        => new()
        {
            BuildingsEnabled = PropBuildingsEnabled,
            BuildingsPerRoom = PropBuildingsPerRoom,
            BuildingMaxSide = PropBuildingMaxSide,
            RewardsEnabled = PropRewardsEnabled,
            RewardsPerCombatRoom = PropRewardsPerCombatRoom,
            BreakablesEnabled = PropBreakablesEnabled,
            BreakablesPerRoom = PropBreakablesPerRoom,
            SolidsEnabled = PropSolidsEnabled,
            SolidsPerRoom = PropSolidsPerRoom,
        };

    /// <summary>
    /// 翻译成内容规划器的纯 C# 配置。
    /// <para>
    /// <b>为什么格 → 米不在这里换算</b>：<see cref="RoomContentConfig"/> 全部以「格」为单位，
    /// 而玩法实体以「米」为单位，换算只发生在真正落地实体的
    /// <see cref="RoomContentSpawnConfig"/>（<see cref="ToContentSpawnConfig"/>）。
    /// 两处各管一段，避免「同一份数值被换算两次」。
    /// </para>
    /// </summary>
    public RoomContentConfig ToRoomContentConfig()
    {
        return new RoomContentConfig
        {
            StartRoomSpawnCount = StartRoomSpawnCount,
            CombatRoomSpawnCount = CombatRoomSpawnCount,
            SpecialRoomSpawnCount = SpecialRoomSpawnCount,
            BossRoomSpawnCount = BossRoomSpawnCount,
            HiddenRoomSpawnCount = HiddenRoomSpawnCount,
            SpecialRoomInteractableCount = SpecialRoomInteractableCount,
            CombatRoomPropCount = CombatRoomPropCount,
            HiddenRoomRewardCount = HiddenRoomRewardCount,
            SlotRadius = SlotRadiusCells,
            WallClearance = WallClearanceCells,
            DoorClearance = DoorClearanceCells,
            SlotPadding = SlotPaddingCells,
            PlacementAttempts = PlacementAttempts,
            SpawnRadiusMin = SpawnRadiusMinCells,
            SpawnRadiusMax = SpawnRadiusMaxCells,
        };
    }

    /// <summary>翻译成内容生成器的纯 C# 配置（生命/受击体等按米计）。</summary>
    public RoomContentSpawnConfig ToContentSpawnConfig()
    {
        return new RoomContentSpawnConfig
        {
            EnemyHurtboxRadius = EnemyHurtboxRadius,
            EnemyHurtboxHeight = EnemyHurtboxHeight,
            EnemyHealth = EnemyHealth,
            BossHurtboxRadius = BossHurtboxRadius,
            BossHealth = BossHealth,
            PropHurtboxRadius = PropHurtboxRadius,
            ViewKindEnemy = ViewKindEnemy,
            ViewKindBoss = ViewKindBoss,
            ViewKindProp = ViewKindProp,
        };
    }

    /// <summary>
    /// 翻译成房型登记表。表为空时返回 <c>null</c>（= 规划器不配表，走它自己的默认拓扑），
    /// 这样「还没配数据」与「配错了」能区分开。
    /// </summary>
    public RoomCategoryTable? ToRoomCategoryTable()
    {
        Godot.Collections.Array<DungeonRoomSpecResource>? specs = RoomSpecs;
        if (specs is null || specs.Count == 0)
        {
            return null;
        }
        List<RoomCategorySpec> rows = new List<RoomCategorySpec>(specs.Count);
        for (int i = 0; i < specs.Count; i++)
        {
            DungeonRoomSpecResource? row = specs[i];
            if (row is null)
            {
                continue;
            }
            rows.Add(row.ToSpec());
        }
        return rows.Count == 0 ? null : new RoomCategoryTable(rows.ToArray());
    }

    /// <summary>翻译成装饰登记表；表为空时返回 <c>null</c>（装饰是可选的）。</summary>
    public DecorCatalog? ToDecorCatalog()
    {
        Godot.Collections.Array<DungeonDecorSpecResource>? specs = DecorSpecs;
        if (specs is null || specs.Count == 0)
        {
            return null;
        }
        List<DecorArchetypeSpec> rows = new List<DecorArchetypeSpec>(specs.Count);
        for (int i = 0; i < specs.Count; i++)
        {
            DungeonDecorSpecResource? row = specs[i];
            if (row is null)
            {
                continue;
            }
            rows.Add(row.ToSpec());
        }
        return rows.Count == 0 ? null : new DecorCatalog(rows.ToArray());
    }

    /// <summary>解决种子：<see cref="Seed"/> 非 0 直接用；为 0 时用传入的启动种子（由宿主打印出来便于复现）。</summary>
    /// <param name="fallbackSeed">启动种子（宿主在 <c>_Ready</c> 里随机一次或从命令行覆盖）。</param>
    public int ResolveSeed(int fallbackSeed) => Seed != 0 ? Seed : fallbackSeed;

    /// <summary>
    /// 参数自检：把配置错误在启动期变成一条明确的中文异常，而不是运行期的无限循环 / 空数组 / 越界。
    /// </summary>
    /// <exception cref="InvalidOperationException">任一约束不成立。</exception>
    public void Validate()
    {
        if (GridWidth <= 0 || GridHeight <= 0)
        {
            throw new InvalidOperationException(Message("网格宽高", "必须大于 0", GridWidth + "x" + GridHeight));
        }
        if (RoomSide <= 0 || StartRoomSide <= 0 || BossRoomSide <= 0)
        {
            throw new InvalidOperationException(Message("房间边长", "必须大于 0", RoomSide + "/" + StartRoomSide + "/" + BossRoomSide));
        }
        if (MinSpecialRooms > MaxSpecialRooms)
        {
            throw new InvalidOperationException(Message("特殊房数量区间", "下界不得大于上界", MinSpecialRooms + ".." + MaxSpecialRooms));
        }
        if (SpecialOnCriticalPathChance < 0f || SpecialOnCriticalPathChance > 1f)
        {
            throw new InvalidOperationException(Message("SpecialOnCriticalPathChance", "必须在 [0,1]", SpecialOnCriticalPathChance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (!(CellSizeMeters > 0f))
        {
            throw new InvalidOperationException(Message("CellSizeMeters", "必须大于 0", CellSizeMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (ContentSlotCapacity <= 0 || DecorCapacity <= 0)
        {
            throw new InvalidOperationException(Message("ContentSlotCapacity/DecorCapacity", "必须大于 0", ContentSlotCapacity + "/" + DecorCapacity));
        }
        if (CameraPaddingFactor < 1f)
        {
            throw new InvalidOperationException(Message("CameraPaddingFactor", "必须 >= 1", CameraPaddingFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (!(TileSizeMeters > 0f))
        {
            throw new InvalidOperationException(Message("TileSizeMeters", "必须大于 0（方砖边长是人物归一化的基准）", TileSizeMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (CellSizeCells <= 0)
        {
            throw new InvalidOperationException(Message("CellSizeCells", "必须 >= 1", CellSizeCells.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (!(RoomScreenFillRatio > 0f))
        {
            throw new InvalidOperationException(Message("RoomScreenFillRatio", "必须大于 0", RoomScreenFillRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (CameraVisibleMetersOverride < 0f)
        {
            throw new InvalidOperationException(Message("CameraVisibleMetersOverride", "不得为负（0 = 按占屏比例反算）", CameraVisibleMetersOverride.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (RoomSide < MinRoomSideCells)
        {
            throw new InvalidOperationException(Message("RoomSide", "中房边长至少 " + MinRoomSideCells.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 格（方砖棋盘）", RoomSide.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // 配置层自带的约束校验（规划器的前置条件），失败信息会指出具体字段。
        DungeonConfig config = ToConfig();
        config.Validate();
    }

    private static string Message(string field, string rule, string actual)
        => nameof(DungeonOptions) + "." + field + " 非法：" + rule + "，实际值 = " + actual + "。";
}
