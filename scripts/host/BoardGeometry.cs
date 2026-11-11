using System;
using Godot;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// **统一方砖的几何契约**——整个 3D 关卡里「一格 = 多远」的唯一换算点。
/// <para>
/// 用户原话（2026-11-11）：「统一场景方砖大小，后续我会将人物根据你的方砖大小归一，
/// 你需要设计好地图大小。我希望一个中等房间占屏幕大小 200%。」
/// </para>
/// <para>
/// 于是这里把三件事钉成一份契约：
/// <list type="number">
/// <item><b>方砖 = 一格 = 一个轴对齐立方体</b>（边长 <see cref="TileSize"/>）。
/// 地面方砖、墙方砖、门方砖、物件方砖全部用同一个边长——不存在「半格台子」「1.5 格柱子」。</item>
/// <item><b>平面象素密度是统一的</b>：屏幕上 1 米横向 = 1 米纵向的 <see cref="ScreenShaper"/> 倍。
/// 这道「纵向压缩」由正交相机的俯角决定（俯视越平，纵向越像地面被压扁）——
/// 人物、方砖、房间全部吃同一个压缩率，于是<b>它们之间的比例永远不会因为改相机而变</b>。</item>
/// <item><b>取景按固定可见高度</b>而不是「把整层塞进屏幕」（见 <see cref="FrameCamera"/>）：
/// 整层 96×96 米塞进 1600×900 时一格只有 8 象素，一个中房占屏幕 20%，玩家根本看不清方砖。
/// 固定可见高度 = 一屏只看十来格，这才是玩法视角。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么单位是米而不是「格」</b>：角色/武器/子弹的数值（<c>characters.xlsx</c> / <c>combat.xlsx</c>）
/// 全是米制（<c>MoveSpeed</c> 米/秒、<c>ProjectileRadius</c> 米）。方砖改用它已有的单位，
/// 「人物按方砖归一」就变成「人物按米归一」——两套单位不会各说各话。
/// </para>
/// <para>
/// <b>扩展点（规则7）</b>：想改方砖大小只改 <see cref="TileSize"/> 的数据源（<c>DungeonOptions.TileSizeMeters</c>）；
/// 想换取景（例如改成「按房间存活数量动态缩放」）只改 <see cref="FrameCamera"/>。
/// 方砖分类（<see cref="DungeonCellSet"/>）与摆件（<c>DungeonTilePieces</c>）都不受影响。
/// </para>
/// </summary>
public readonly struct BoardGeometry
{
    /// <summary>
    /// 方砖边长（米）——**人物归一化的基准**。
    /// <para>
    /// 6 米的取舍：① 现有 D&D 风格场景里 1 格 = 5 英尺 × 1.5 ≈ 2.3 米太小，一张 4×4 的中房只有 9 米，
    /// 俯视角下人物占满半屏；② 12 米太大，一段 <c>Rock</c> 素材要被放大到 20 倍才够一格（剖面撕裂、法线错乱）；
    /// ③ 6 米时素材缩放落在 1.5~5 倍（正常范围），一个中房 24 米，在 1600×900 上正好 2 屏宽。
    /// </para>
    /// </summary>
    public float TileSize { get; init; }

    /// <summary>方砖的格边长（整数，用于地图网格索引）。</summary>
    public int CellSizeCells { get; init; }

    /// <summary>墙体高度（米）。等于方砖边长 = 1 个立方体高。</summary>
    public float WallHeight { get; init; }

    /// <summary>地面方砖/地面板的厚度（米）。</summary>
    public float FloorThickness { get; init; }

    /// <summary>
    /// 相机俯角（度，90 = 正俯视）。**屏幕纵横比压缩率的唯一来源**。
    /// </summary>
    public float CameraPitchDegrees { get; init; }

    /// <summary>
    /// 屏幕纵向 / 横向的米数比 = <see cref="ScreenShaper"/>。
    /// <para>
    /// 正交相机（<c>Camera3D.Size</c> = 纵向可见米数）俯仰 θ 时，深度 D 米的地面在屏幕上占
    /// <c>tanθ·D</c> 米纵向；而横向 <c>W</c> 米占 <c>W</c> 米。所以「纵深 → 屏幕纵向」被压缩了 <c>sinθ</c> 倍，
    /// 相机本身是等比投影，这部分必须由 <see cref="ScreenShaper"/> 补回来：
    /// <c>Size_x = Size_y / ScreenShaper</c>，于是 1 米横向 = 1 米纵向的象素数完全一致。
    /// </para>
    /// <para>
    /// 把「横向与纵向是否等比」做成 <c>DungeonOptions.TileAspectIsotropic</c> 而不是写死：
    /// 等比时方砖在屏幕上是正方形（美术/人物换算是 1:1，推荐）；
    /// 不等比时沿用 16:9 的「投影等比」，方砖在屏幕上被压成横向长条（俯视看更多纵深）。
    /// </para>
    /// </summary>
    public float ScreenShaper => Mathf.Sin(Mathf.DegToRad(Mathf.Clamp(CameraPitchDegrees, PitchMinDegrees, PitchMaxDegrees)));

    /// <summary>
    /// 方砖在世界坐标里的**跨度**（米）：一格从 <c>cell * TileSize</c> 到 <c>(cell+1) * TileSize</c>。
    /// 所有「格 → 米」换算都必须走这里，不许别处再写 <c>* cellSize</c>。
    /// </summary>
    /// <param name="cells">格数。</param>
    public float Span(float cells) => cells * TileSize;

    /// <summary>格坐标 → 世界坐标（格 <c>i</c> 的**近边**在 <c>i * TileSize</c>）。</summary>
    /// <param name="xCells">X 格。</param>
    /// <param name="zCells">Z 格。</param>
    public Vector2 ToWorld(float xCells, float zCells) => new(xCells * TileSize, zCells * TileSize);

    /// <summary>一格的**中心**世界坐标（多格房间的中心由调用方按 <see cref="Span"/> 算）。</summary>
    /// <param name="xCells">X 格。</param>
    /// <param name="zCells">Z 格。</param>
    public Vector3 Center(int xCells, int zCells)
        => new((xCells + HalfSpan) * TileSize, 0f, (zCells + HalfSpan) * TileSize);

    /// <summary>格数 → 米（与 <see cref="Span"/> 同义，读起来更顺的别名）。</summary>
    /// <param name="cells">格数。</param>
    public float Meters(int cells) => cells * TileSize;

    /// <summary>由方砖边长构造全部派生尺寸。</summary>
    /// <param name="tileSizeMeters">方砖边长（米），必须 &gt; 0。</param>
    /// <param name="cameraPitchDegrees">相机俯角（度）。</param>
    /// <param name="cellSizeCells">一砖几格（原型阶段恒为 1）。</param>
    /// <param name="wallHeightMeters">墙高（米）；&lt;= 0 表示「等于方砖边长」。</param>
    /// <param name="floorThicknessMeters">地面板厚（米）；&lt;= 0 表示取比例。</param>
    public static BoardGeometry Create(
        float tileSizeMeters,
        float cameraPitchDegrees,
        int cellSizeCells = CellSizeCellsDefault,
        float wallHeightMeters = WallHeightFromTile,
        float floorThicknessMeters = FloorThicknessFromTile)
    {
        if (!(tileSizeMeters > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(tileSizeMeters), "方砖边长必须为正。");
        }
        int cells = cellSizeCells > 0 ? cellSizeCells : CellSizeCellsDefault;
        float tile = tileSizeMeters * cells;
        return new BoardGeometry
        {
            TileSize = tile,
            CellSizeCells = cells,
            WallHeight = wallHeightMeters > 0f ? wallHeightMeters : tile,
            FloorThickness = floorThicknessMeters > 0f ? floorThicknessMeters : tile * FloorThicknessRatio,
            CameraPitchDegrees = cameraPitchDegrees,
        };
    }

    /// <summary>一砖几格的默认值（原型阶段恒为 1：方砖 = 地图格）。</summary>
    public const int CellSizeCellsDefault = 1;

    /// <summary>墙高取「等于方砖」的哨兵（&lt;= 0 时生效，避免与合法墙高撞值）。</summary>
    public const float WallHeightFromTile = -1f; // arch-ok: 「未配置」哨兵值（与所有合法墙高不撞），非玩法数值

    /// <summary>地面板厚取「等于比例」的哨兵。</summary>
    public const float FloorThicknessFromTile = -1f; // arch-ok: 「未配置」哨兵值（与所有合法板厚不撞），非玩法数值

    /// <summary>地面板厚 / 方砖边长。</summary>
    public const float FloorThicknessRatio = 0.12f; // arch-ok: 几何比例常量（地面板厚相对方砖边长），由 Options 覆盖

    /// <summary>横向半格（格中心 = 起点 + 半格）。</summary>
    private const float HalfSpan = 0.5f; // arch-ok: 几何推导常量（格中心相对格近边的偏移），非玩法数值

    /// <summary>相机俯角下限（度）：再平就看不到地面了。</summary>
    public const float PitchMinDegrees = 10f; // arch-ok: 几何安全阈值（俯角下限），非玩法数值

    /// <summary>相机俯角上限（度）。</summary>
    public const float PitchMaxDegrees = 90f; // arch-ok: 几何安全阈值（俯角上限），非玩法数值

    /// <inheritdoc />
    public override string ToString()
        => "方砖 " + TileSize.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
           + " 墙高 " + WallHeight.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
           + " 地面厚 " + FloorThickness.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
           + " 俯角 " + CameraPitchDegrees.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "°"
           + " 塑形 " + ScreenShaper.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
}
