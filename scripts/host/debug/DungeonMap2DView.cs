using System;
using Godot;
using Game.Host.Ui;
using Game.Spawn.Rooms;

namespace Game.Host.Debug;

/// <summary>
/// 把 <see cref="DungeonMap"/> 画成一张俯视 2D 格图：**正方形 = 房间格，颜色 = 房型/地砖类型，
/// 方块 = 障碍，深色 = 墙，亮点 = 门，虚线 = 秘密通道**。
/// <para>
/// 为什么单独做一层 2D：3D 白盒虽然能走，但「哪里通、哪里堵」在地牢生成调试里根本看不出来。
/// 这一层**只读** <see cref="DungeonMap"/>（由 <see cref="DungeonMapBuilder"/> 生成），
/// 不参与 3D 场景构建，也不碰 ECS —— 它是「地图体检」的可视化，不是玩法渲染。
/// </para>
/// <para>
/// 颜色**不写死**：地面色一律来自 <see cref="DungeonRoomStyleSet"/> 的 <c>FloorColor</c>，
/// 换配色只改 <c>.tres</c>，这个文件不动（规则4 + 规则7）。
/// </para>
/// </summary>
[GlobalClass]
public partial class DungeonMap2DView : Node2D
{
    /// <summary>一格在屏幕上的边长（像素）。面板可以实时改它来做缩放。</summary>
    [Export(PropertyHint.Range, "4,64,1")]
    public float CellPixels { get; set; } = 16f; // arch-ok: 调试视图的默认像素尺寸，可被场景/面板覆盖

    /// <summary>格子像素上限：自动铺满时最多放这么大，再大就把图居中而不是撑满。</summary>
    [Export(PropertyHint.Range, "4,64,1")]
    public float MaxCellPixels { get; set; } = 40f; // arch-ok: 调试视图的显示上限，可被场景/面板覆盖

    /// <summary>
    /// 是否自动按可用空间算 <see cref="CellPixels"/>（默认开）。
    /// <para>
    /// 关掉它，格子像素就由面板滑条说了算 —— 也就是「手动缩放」模式。
    /// </para>
    /// </summary>
    [Export]
    public bool AutoFit { get; set; } = true;

    /// <summary>配色表；为空时用内置默认配色。</summary>
    [Export]
    public DungeonRoomStyleSet? Styles { get; set; }

    /// <summary>
    /// 是否画图例。
    /// <para>
    /// 图例画在**地图右侧**的空白列里（竖排一列、一项一行）。上一版铺在地图下方：
    /// 把地图整体抬高、被挤成三行横排小字（字号 11），用户反馈「标注字太小」。
    /// </para>
    /// </summary>
    [Export]
    public bool ShowLegend { get; set; } = true;

    /// <summary>是否高亮「起始房 → Boss 房」的一条最短路（用于肉眼确认连通性）。</summary>
    [Export]
    public bool HighlightRoute { get; set; } = true;

    /// <summary>奖励菱形相对一格的半径比例（纯视觉）。</summary>
    private const float RewardDiamondScale = 0.26f; // arch-ok: 图元大小比例（纯视觉排版）

    /// <summary>图例左/上留白（图例列内部）。</summary>
    private const float LegendMargin = DebugUiStyle.LegendMargin; // arch-ok: 图例边距（纯视觉排版）

    /// <summary>
    /// 图例文字字号。**与面板正文同口径但更大一档**：图例站在地图右侧的空地上、离眼睛更远，
    /// 与面板同号时在 1080p 上要凑近才认得出（用户反馈「标注字太小」）。
    /// 字号只从 <see cref="DebugUiStyle"/> 取，本文件不另写数字——以前这里写死 11，和占位高度分家。
    /// </summary>
    private const int LegendFontSize = DebugUiStyle.FontLegend;

    /// <summary>图例标题比条目大几号。只表达层级差异，绝对字号仍由 <see cref="DebugUiStyle"/> 决定。</summary>
    private const int LegendTitleExtra = 3; // arch-ok: 字号层级差（UI 排版）

    /// <summary>图例分隔线的粗细（像素）。</summary>
    private const float LegendRuleWidth = 1f; // arch-ok: 图元粗细（纯视觉排版）

    /// <summary>图例标题（只此一处：测量列宽与绘制用同一个来源）。</summary>
    private const string LegendTitleText = "图例";

    /// <summary>房型组的小标题。</summary>
    private const string RoomLegendTitle = "房型 / 格子";

    /// <summary>物件组的小标题。</summary>
    private const string PropLegendTitle = "物件";

    /// <summary>图例末尾统计行的条数。</summary>
    private const int StatLineCount = 4; // arch-ok: 图例统计行数（纯视觉排版）

    /// <summary>
    /// 图例里**全部**文字。只用来给 <see cref="MeasureLegendColumnWidth"/> 量最宽一行——
    /// 列宽必须按真实字宽算，放大字号后写死的列宽会把「不可破坏 / 秘密通道」裁掉半截且不报错。
    /// </summary>
    private static readonly string[] LegendTexts =
    {
        "起始房", "战斗房", "特殊房", "隐藏房", "Boss房", "墙", "门", "秘密通道",
        "建筑", "奖励", "可破坏", "不可破坏", "障碍", RoomLegendTitle, PropLegendTitle, "网格 99×99",
    };

    /// <summary>
    /// 图例底板底色。**不透明**：半透明底板会和窗口底色（#4C4C4C）混成一片灰，
    /// 深色文字压在上面几乎看不见——实测半透明版图例区平均亮度只有 51/255。
    /// </summary>
    private static readonly Color LegendPanelColor =
        new(DebugUiStyle.PanelBackground.R, DebugUiStyle.PanelBackground.G, DebugUiStyle.PanelBackground.B, 1f); // arch-ok: 图例底板（纯视觉排版）

    /// <summary>自动铺满时不许缩得比这更小（再小就只剩噪点，看不清格子）。</summary>
    private const float MinCellPixels = 2f; // arch-ok: 缩放下限（纯显示层）

    /// <summary>正好夹到同一个值时不重画，避免浮点尾数导致的每帧重绘。</summary>
    private const float CellPixelsEpsilon = 0.05f; // arch-ok: 浮点比较容差（显示层）

    /// <summary>滚动条/边框预留的宽度（像素）。</summary>
    private const float ScrollbarReserve = 16f; // arch-ok: 滚动条占位（UI 布局尺寸）

    /// <summary>图例区占掉的高度（像素），配合 <see cref="ShowLegend"/> 使用。</summary>
    /// <summary>
    /// 图例区占掉的**高度**。图例搬到地图右侧后不占地图下方高度，故为 0；
    /// 保留这个常量是因为「可用高度 = 可视区高 − 图例高度」的算式语义仍然成立。
    /// </summary>
    private const float LegendReservedHeight = DebugUiStyle.LegendReservedHeight; // arch-ok: 图例占位（UI 布局尺寸）

    private DungeonMap _map;
    private bool _hasMap;
    private GridOffset[] _route = Array.Empty<GridOffset>();
    private DungeonRoomStyleSet _stylesResolved = DungeonRoomStyleSet.CreateDefault();

    /// <summary>图例列宽度缓存：在 <see cref="FitToViewport"/> 里按字体实测，避免每帧量字体。</summary>
    private float legendColumnWidth = DebugUiStyle.LegendMinColumnWidth;

    // 非房间格的表现色（墙/门/通道/障碍）—— 这些是「格子类型」而不是「房型」，
    // 所以不走配色表；同时登记为资源默认值，可被场景覆盖。
    /// <summary>墙体颜色。</summary>
    [Export]
    public Color WallColor { get; set; } = new Color(0.13f, 0.14f, 0.17f); // arch-ok: 调试视图默认底色

    /// <summary>门格颜色。</summary>
    [Export]
    public Color DoorColor { get; set; } = new Color(0.96f, 0.85f, 0.35f); // arch-ok: 调试视图默认底色

    /// <summary>秘密通道颜色。</summary>
    [Export]
    public Color SecretColor { get; set; } = new Color(0.80f, 0.45f, 0.95f); // arch-ok: 调试视图默认底色

    /// <summary>障碍块颜色。</summary>
    [Export]
    public Color ObstacleColor { get; set; } = new Color(0.10f, 0.10f, 0.12f); // arch-ok: 调试视图默认底色

    /// <summary>建筑（多格，整块铺满）颜色。</summary>
    [Export]
    public Color BuildingColor { get; set; } = new Color(0.62f, 0.42f, 0.26f); // arch-ok: 调试视图默认底色

    /// <summary>奖励物颜色。</summary>
    [Export]
    public Color RewardColor { get; set; } = new Color(1.00f, 0.82f, 0.16f); // arch-ok: 调试视图默认底色

    /// <summary>可破坏物颜色。</summary>
    [Export]
    public Color BreakableColor { get; set; } = new Color(0.52f, 0.74f, 0.42f); // arch-ok: 调试视图默认底色

    /// <summary>不可破坏物颜色。</summary>
    [Export]
    public Color SolidColor { get; set; } = new Color(0.36f, 0.38f, 0.44f); // arch-ok: 调试视图默认底色

    /// <summary>走廊颜色（房间之间的连接地面）。</summary>
    [Export]
    public Color CorridorColor { get; set; } = new Color(0.62f, 0.63f, 0.66f); // arch-ok: 调试视图默认底色

    /// <summary>路线高亮颜色。</summary>
    [Export]
    public Color RouteColor { get; set; } = new Color(1f, 0.25f, 0.25f, 0.85f); // arch-ok: 调试视图默认底色

    /// <summary>当前是否已绑定地图。</summary>
    public bool HasMap => _hasMap;

    /// <summary>导出当前地图的字符画（与 <c>tools/dungeon_check</c> 输出的同一套符号）。</summary>
    /// <returns>逐行拼接的字符画；没有地图时返回空串。</returns>
    public string ExportArt()
    {
        return _hasMap ? _map.ToArt() : string.Empty;
    }

    /// <summary>当前地图的宽（格）。</summary>
    public int MapWidth => _hasMap ? _map.Width : 0;

    /// <summary>当前地图的高（格）。</summary>
    public int MapHeight => _hasMap ? _map.Height : 0;

    /// <summary>绑定一张新地图并重画（面板每次改参数都调这个）。</summary>
    /// <param name="map">要显示的栅格地图。</param>
    public void SetMap(in DungeonMap map)
    {
        _map = map;
        _hasMap = map.IsValid;
        _route = _hasMap && HighlightRoute ? FindRoute(in map) : Array.Empty<GridOffset>();
        QueueRedraw();
    }

    /// <summary>换配色表（<c>.tres</c> 被改后重画）。</summary>
    /// <param name="styles">配色集合；null = 用内置默认。</param>
    public void SetStyles(DungeonRoomStyleSet? styles)
    {
        Styles = styles;
        _stylesResolved = styles ?? DungeonRoomStyleSet.CreateDefault();
        QueueRedraw();
    }

    /// <summary>只改缩放并重画。</summary>
    /// <param name="cellPixels">一格像素边长。</param>
    public void SetCellPixels(float cellPixels)
    {
        CellPixels = Mathf.Max(cellPixels, 1f);
        QueueRedraw();
    }

    /// <summary>
    /// 按「外层容器当前可见的矩形」重算格子像素，让图铺满可视区。
    /// <para>
    /// 为什么不能只看自己的 <c>Size</c>：本节点是 <c>ScrollContainer</c> 的子节点，
    /// Node2D 没有布局尺寸，<c>ScrollContainer</c> 只能拿 <c>GetRect()</c> 当内容大小，
    /// 于是**内容变大滚动区就跟着变大**，永远「刚好装下」——图缩不小也放不大。
    /// 所以这里用**父容器自身的 <c>Size</c>**（它才是真正的可视窗口）当基准。
    /// </para>
    /// <para>
    /// 返回 true 表示格子像素真的变了（调用方据此决定要不要重画，避免每帧刷新）。
    /// </para>
    /// </summary>
    /// <param name="available">可视区尺寸（像素）；非正数时视为「拿不到尺寸」，不做任何事。</param>
    public bool FitToViewport(Vector2 available)
    {
        if (!_hasMap || !AutoFit || available.X <= 0f || available.Y <= 0f)
        {
            return false;
        }

        legendColumnWidth = MeasureLegendColumnWidth();

        // 图例站在地图右侧，横向占一列；否则图例列会伸出可视区被 ScrollContainer 裁掉，
        // 还会逼出横向滚动条。下方只再留滚动条的位置。
        float usable = Mathf.Min(
            (available.X - ScrollbarReserve - ReservedViewportWidth()) / _map.Width,
            (available.Y - ScrollbarReserve - ReservedViewportHeight()) / _map.Height);
        float fitted = Mathf.Clamp(usable, MinCellPixels, MaxCellPixels);
        if (Mathf.Abs(fitted - CellPixels) < CellPixelsEpsilon)
        {
            return false;
        }
        CellPixels = fitted;
        QueueRedraw();
        return true;
    }

    /// <summary>图例区占掉的高度（像素）：图例关闭时为 0。</summary>
    private float ReservedViewportHeight()
    {
        return ShowLegend ? LegendReservedHeight : 0f;
    }

    /// <summary>图例区占掉的宽度（像素）：图例关闭时为 0。</summary>
    private float ReservedViewportWidth()
    {
        return ShowLegend ? legendColumnWidth : 0f;
    }

    /// <summary>
    /// 按当前配色表重画图例（面板在运行时换配色后调用）。
    /// <para>
    /// 图例画在<b>视图里</b>（地图右侧那一列），色块取自 <c>_stylesResolved</c>；
    /// 面板 <c>Bind</c> 时通过 <c>SetStyles</c> 换过配色，这里顺带让图例列重新测宽并重画，
    /// 否则图例会停在旧配色 / 旧列宽上，而「颜色对不上地图」不会报任何错。
    /// </para>
    /// </summary>
    public void RefreshLegend()
    {
        legendColumnWidth = MeasureLegendColumnWidth();
        QueueRedraw();
    }

    /// <summary>
    /// 图例列的原点 X（地图右边界 + 间距）。
    /// <para>
    /// 对外开一个方法而不是只在本文件里用：场景自检要拿「地图宽 + 图例宽」
    /// 去和可视区宽度比对，两处必须算同一个数，否则自检报的「通过」没有意义。
    /// </para>
    /// </summary>
    /// <returns>图例列左边界（像素，视图本地坐标）。没有地图时为 0。</returns>
    public float LegendColumnX()
    {
        return _hasMap ? (_map.Width * CellPixels) + DebugUiStyle.LegendTopGap : 0f;
    }

    /// <summary>图例列的宽度（像素）：图例关闭时为 0。</summary>
    public float LegendColumnWidth()
    {
        return ReservedViewportWidth();
    }

    /// <summary>图例当前使用的字号（像素），供自检/探针核对「字真的变大了」。</summary>
    public int LegendFontPixelSize()
    {
        return LegendFontSize;
    }

    /// <summary>
    /// 按<b>当前</b>格子像素，判断「地图 + 右侧图例列」是否还装得进 <paramref name="available"/>。
    /// <para>
    /// 面板用它决定要不要收回手动缩放的控制权：装得下就别动（用户手动定的比例要保住），
    /// 装不下（窗口被拉小 / 手动像素超过可视区）才重新铺满，避免图被裁掉一截而没人发现。
    /// </para>
    /// </summary>
    /// <param name="available">可视区尺寸（像素）。</param>
    /// <returns>装得下为 true；没有地图或拿不到尺寸时也返回 false（让调用方走一次铺满）。</returns>
    public bool FitsWithin(Vector2 available)
    {
        if (!_hasMap || available.X <= 0f || available.Y <= 0f)
        {
            return false;
        }
        float needWidth = (_map.Width * CellPixels) + ReservedViewportWidth() + ScrollbarReserve;
        float needHeight = (_map.Height * CellPixels) + ReservedViewportHeight() + ScrollbarReserve;
        return needWidth <= available.X && needHeight <= available.Y;
    }

    /// <summary>
    /// 图例列实际需要的宽度：取 <see cref="DebugUiStyle.LegendMinColumnWidth"/> 与「最长一条图例」的较大者。
    /// <para>
    /// 按字体实测宽度算而不是猜一个数：字号一旦放大，写死的列宽会把「不可破坏 / 秘密通道」裁掉半截，
    /// 而裁掉不会报错、日志也看不出来。
    /// </para>
    /// </summary>
    private float MeasureLegendColumnWidth()
    {
        Font font = ThemeDB.FallbackFont;
        float longest = 0f;
        foreach (string label in LegendTexts)
        {
            float width = font.GetStringSize(label, HorizontalAlignment.Left, -1f, LegendFontSize).X;
            longest = Mathf.Max(longest, width);
        }
        float title = font.GetStringSize(LegendTitleText, HorizontalAlignment.Left, -1f, LegendFontSize + LegendTitleExtra).X;
        longest = Mathf.Max(longest, title);
        return Mathf.Max(
            DebugUiStyle.LegendMinColumnWidth,
            (LegendMargin * 2f) + DebugUiStyle.LegendSwatch + DebugUiStyle.LegendSwatchGap + longest);
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _ = delta;
        // 每帧问一次可视区：窗口拖动没有「尺寸变了」的事件能可靠订阅到
        // （ScrollContainer 大小变化 → 子节点 Size 变化，两者都不保证发信号），
        // 而这里只是一次比较 + 极少次重画，成本可以忽略。
        Godot.Control? host = GetParent() as Godot.Control;
        if (host is not null)
        {
            FitToViewport(host.Size);
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _stylesResolved = Styles ?? DungeonRoomStyleSet.CreateDefault();
        _stylesResolved.Rebuild();
    }

    /// <inheritdoc />
    public override void _Draw()
    {
        if (!_hasMap)
        {
            DrawString(
                ThemeDB.FallbackFont,
                new Vector2(4f, 16f),
                "（还没有地图：点面板上的「生成」）",
                HorizontalAlignment.Left,
                -1f,
                12);
            return;
        }

        DrawCells();
        DrawProps();
        if (_route.Length > 0)
        {
            DrawRoute();
        }
        if (ShowLegend)
        {
            DrawLegend();
        }
    }

    /// <summary>
    /// 画四类物件。**形状区分比颜色更可靠**（色弱/小屏都认得出），所以每类有自己的画法：
    /// 建筑 = 整块铺满并描边，奖励 = 小菱形，可破坏 = 小圆点，不可破坏 = 小方块。
    /// </summary>
    private void DrawProps()
    {
        DungeonProp[] props = _map.Props;
        if (props == null || props.Length == 0)
        {
            return;
        }
        float cell = CellPixels;
        for (int i = 0; i < props.Length; i++)
        {
            DungeonProp prop = props[i];
            var rect = new Rect2(prop.X * cell, prop.Z * cell, prop.Width * cell, prop.Depth * cell);
            switch (prop.Kind)
            {
                case DungeonPropKind.Building:
                    DrawRect(rect, BuildingColor);
                    DrawRect(rect, BuildingColor.Darkened(0.45f), false, Mathf.Max(cell * 0.08f, 1f));
                    break;
                case DungeonPropKind.Reward:
                    DrawRewardDiamond(prop, cell);
                    break;
                case DungeonPropKind.Breakable:
                    DrawCircle(
                        new Vector2((prop.X * cell) + (cell * 0.5f), (prop.Z * cell) + (cell * 0.5f)),
                        cell * 0.22f,
                        BreakableColor);
                    break;
                case DungeonPropKind.Solid:
                    DrawRect(
                        new Rect2((prop.X * cell) + (cell * 0.3f), (prop.Z * cell) + (cell * 0.3f), cell * 0.4f, cell * 0.4f),
                        SolidColor);
                    break;
                default:
                    break;
            }
        }
    }

    private void DrawRewardDiamond(in DungeonProp prop, float cell)
    {
        float cx = (prop.X * cell) + (cell * 0.5f);
        float cz = (prop.Z * cell) + (cell * 0.5f);
        float r = cell * RewardDiamondScale;
        Vector2[] points =
        {
            new Vector2(cx, cz - r),
            new Vector2(cx + r, cz),
            new Vector2(cx, cz + r),
            new Vector2(cx - r, cz),
        };
        DrawColoredPolygon(points, RewardColor);
    }

    private void DrawCells()
    {
        float cell = CellPixels;
        for (int z = 0; z < _map.Height; z++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                DungeonCellKind kind = _map.KindAt(x, z);
                if (kind == DungeonCellKind.OutOfGrid)
                {
                    continue;
                }
                Color color = ColorFor(kind, x, z);
                DrawRect(new Rect2(x * cell, z * cell, cell - 1f, cell - 1f), color);
                if (kind == DungeonCellKind.Obstacle)
                {
                    // 障碍画成更小的实心块，一眼能和地板区分开。
                    DrawRect(
                        new Rect2((x * cell) + (cell * 0.18f), (z * cell) + (cell * 0.18f), cell * 0.64f, cell * 0.64f),
                        ObstacleColor);
                }
                else if (kind == DungeonCellKind.Door)
                {
                    DrawRect(
                        new Rect2((x * cell) + (cell * 0.2f), (z * cell) + (cell * 0.2f), cell * 0.6f, cell * 0.6f),
                        DoorColor);
                }
                else if (_map.RoomAt(x, z) >= 0 && kind == DungeonCellKind.Floor)
                {
                    // 房间中心画一个短竖线，方便数「这是第几间房」。
                    int slot = _map.RoomAt(x, z);
                    (int cx, int cz) = _map.RoomCenter(in _map.Layout.Rooms[slot]);
                    if (cx == x && cz == z)
                    {
                        DrawLine(
                            new Vector2((x * cell) + (cell * 0.5f), (z * cell) + (cell * 0.25f)),
                            new Vector2((x * cell) + (cell * 0.5f), (z * cell) + (cell * 0.75f)),
                            new Color(0f, 0f, 0f, 0.45f),
                            1.5f);
                    }
                }
            }
        }
    }

    private void DrawRoute()
    {
        float cell = CellPixels;
        float half = cell * 0.5f;
        Color color = RouteColor;
        for (int i = 0; i < _route.Length; i++)
        {
            GridOffset p = _route[i];
            Vector2 a = new Vector2((p.X * cell) + half, (p.Z * cell) + half);
            if (i + 1 < _route.Length)
            {
                GridOffset q = _route[i + 1];
                Vector2 b = new Vector2((q.X * cell) + half, (q.Z * cell) + half);
                DrawLine(a, b, color, Mathf.Max(cell * 0.16f, 1.5f));
            }
        }
    }

    /// <summary>
    /// 图例：**每一项都画出真实的色块**，后面跟名字；画在**地图右侧**的空白列里。
    /// <para>
    /// 历史：上一版是地图下方的三行横排小字（字号 11）。原因不只是字号——
    /// 横排为了塞进一行，行距与留白都被压到最小；地图右侧这一列本来就空着大片，
    /// 竖排后每项独占一行，字号可以放到 <see cref="DebugUiStyle.FontLegend"/> 而不会被挤成一坨。
    /// </para>
    /// <para>
    /// 色块颜色一律取自当前配色表 / 导出色（与地图完全同源），换配色时图例自动跟着变；
    /// 再上一版纯文字图例「看不出来什么颜色对应什么房间」，正是在这里修掉的。
    /// </para>
    /// </summary>
    private void DrawLegend()
    {
        float swatch = DebugUiStyle.LegendSwatch;
        float gap = DebugUiStyle.LegendSwatchGap;
        float rowHeight = DebugUiStyle.LegendRowHeight;
        float itemGap = DebugUiStyle.LegendItemGap;
        int fontPx = LegendFontSize;
        int titlePx = fontPx + LegendTitleExtra;
        Font font = ThemeDB.FallbackFont;

        // Node2D 原点在地图左上角，所以「地图右侧」就是 +X 方向；以地图顶部为基准往下竖排。
        float x0 = LegendColumnX();
        float x1 = x0 + Mathf.Max(legendColumnWidth, DebugUiStyle.LegendMinColumnWidth);

        // 底板：右边这列在地图外，给它一块自己的底，看起来才像「一块说明区」而不是浮在空里的字。
        DrawRect(new Rect2(x0, 0f, x1 - x0, _map.Height * CellPixels), LegendPanelColor);

        // 图例**不吃控件的 Theme**（Node2D 不在 Control 树里），字号只能从 DebugUiStyle 取，
        // 这样面板字与图例字同一口径 —— 以前两边各写一个数字，改一处另一处就不跟。
        float y = LegendMargin + titlePx;
        DrawString(font, new Vector2(x0 + LegendMargin, y), LegendTitleText, HorizontalAlignment.Left, -1f, titlePx);
        y += DrawLegendRule(y, itemGap);

        // 各组竖排：每组先一行小标题，再一行一项。
        y = DrawLegendGroup(font, x0, y, RoomLegendTitle, RoomLegendItems(), swatch, gap, rowHeight, itemGap, fontPx);
        y = DrawLegendGroup(font, x0, y, PropLegendTitle, PropLegendItems(), swatch, gap, rowHeight, itemGap, fontPx);
        y += DrawLegendRule(y, itemGap);

        // 末尾：统计行。以前是一整行「seed · 网格 · 房间 · 物件」横排，
        // 在竖列里那行比别的行宽一倍、会顶出图片边界，所以拆成一条一项。
        for (int i = 0; i < StatLineCount; i++)
        {
            y += rowHeight;
            DrawString(font, new Vector2(x0 + LegendMargin, y), StatLine(i), HorizontalAlignment.Left, -1f, fontPx);
        }
    }

    /// <summary>房型与格子类型图例（颜色来自配色表，与地图完全同源）。</summary>
    private (string Label, Color Color)[] RoomLegendItems()
    {
        return new (string, Color)[]
        {
            ("起始房", _stylesResolved.Get(RoomCategory.Start).FloorColor),
            ("战斗房", _stylesResolved.Get(RoomCategory.Combat).FloorColor),
            ("特殊房", _stylesResolved.Get(RoomCategory.Special).FloorColor),
            ("隐藏房", _stylesResolved.Get(RoomCategory.Hidden).FloorColor),
            ("Boss房", _stylesResolved.Get(RoomCategory.Boss).FloorColor),
            ("墙", WallColor),
            ("门", DoorColor),
            ("秘密通道", SecretColor),
        };
    }

    /// <summary>四类物件 + 障碍的图例。</summary>
    private (string Label, Color Color)[] PropLegendItems()
    {
        return new (string, Color)[]
        {
            ("建筑", BuildingColor),
            ("奖励", RewardColor),
            ("可破坏", BreakableColor),
            ("不可破坏", SolidColor),
            ("障碍", ObstacleColor),
        };
    }

    /// <summary>图例末尾的统计行（按行号取，便于「测宽」与「画」用同一份文本）。</summary>
    private string StatLine(int index)
    {
        return index switch
        {
            0 => string.Format(System.Globalization.CultureInfo.InvariantCulture, "seed {0}", _map.Seed),
            1 => string.Format(System.Globalization.CultureInfo.InvariantCulture, "网格 {0}×{1}", _map.Width, _map.Height),
            2 => string.Format(System.Globalization.CultureInfo.InvariantCulture, "房间 {0}", _map.Layout.RoomCount),
            _ => string.Format(System.Globalization.CultureInfo.InvariantCulture, "物件 {0}", _map.PropCount),
        };
    }

    /// <summary>画一条图例分隔线，返回它占掉的高度（含上下空隙）。</summary>
    private float DrawLegendRule(float y, float itemGap)
    {
        float x0 = LegendColumnX();
        float x1 = x0 + Mathf.Max(legendColumnWidth, DebugUiStyle.LegendMinColumnWidth);
        float lineY = y + itemGap;
        DrawLine(
            new Vector2(x0 + LegendMargin, lineY),
            new Vector2(x1 - LegendMargin, lineY),
            DebugUiStyle.PanelBorder,
            LegendRuleWidth);
        return itemGap * 2f;
    }

    /// <summary>画一组「小标题 + 竖排色块项」，返回下一组应该从哪个 y（文字基线）开始。</summary>
    private float DrawLegendGroup(
        Font font,
        float x0,
        float y,
        string groupTitle,
        (string Label, Color Color)[] items,
        float swatch,
        float gap,
        float rowHeight,
        float itemGap,
        int fontPx)
    {
        y += fontPx + itemGap;
        DrawString(font, new Vector2(x0 + LegendMargin, y), groupTitle, HorizontalAlignment.Left, -1f, fontPx);
        foreach ((string label, Color color) in items)
        {
            y += rowHeight;
            AddLegendSwatch(x0 + LegendMargin, y, swatch, gap, label, color, font, fontPx);
        }
        return y;
    }

    /// <summary>
    /// 画一项「一块真颜色 + 名字」。颜色取自当前配色表 / 导出色，不会与地图脱节。
    /// <para>
    /// 色块底边贴着文字基线：中文在绘制坐标里是从基线往上长的，
    /// 若按色块**顶边**对齐基线，色块会整体压到文字下方，看起来像被切掉一半。
    /// </para>
    /// </summary>
    private void AddLegendSwatch(float x, float baseline, float swatch, float gap, string label, Color color, Font font, int fontPx)
    {
        Rect2 box = new(x, baseline - swatch, swatch, swatch);
        DrawRect(box, color);
        DrawRect(box, new Color(0f, 0f, 0f, 0.5f), false, 1f);
        DrawString(font, new Vector2(x + swatch + gap, baseline), label, HorizontalAlignment.Left, -1f, fontPx);
    }

    private Color ColorFor(DungeonCellKind kind, int x, int z)
    {
        switch (kind)
        {
            case DungeonCellKind.Wall:
                return WallColor;
            case DungeonCellKind.Door:
                return DoorColor;
            case DungeonCellKind.SecretPassage:
                return SecretColor;
            case DungeonCellKind.Obstacle:
                return FloorColorAt(x, z).Darkened(0.65f);
            case DungeonCellKind.Corridor:
                return CorridorColor;
            case DungeonCellKind.Floor:
                return FloorColorAt(x, z);
            default:
                return WallColor;
        }
    }

    private Color FloorColorAt(int x, int z)
    {
        int slot = _map.RoomAt(x, z);
        if (slot < 0 || slot >= _map.Layout.RoomCount)
        {
            return CorridorColor;
        }
        RoomCategory category = _map.Layout.Rooms[slot].Category;
        Color floor = _stylesResolved.Get(category).FloorColor;
        // 地砖感：按格奇偶微调亮度，让「一格一格」看得出来（用户要求「颜色代表地砖类型」）。
        float checker = ((x + z) & 1) == 0 ? 1f : 0.92f; // arch-ok: 棋盘格亮度差（纯视觉），非玩法数值
        return new Color(floor.R * checker, floor.G * checker, floor.B * checker, floor.A);
    }

    /// <summary>起始房 → Boss 房的最短路（BFS，秘密通道算可走；找不到返回空）。</summary>
    private static GridOffset[] FindRoute(in DungeonMap map)
    {
        (int sx, int sz) = map.RoomCenter(in map.Layout.Rooms[map.Layout.Start.Slot]);
        (int bx, int bz) = map.RoomCenter(in map.Layout.Rooms[map.Layout.Boss.Slot]);
        if (!map.Contains(sx, sz) || !map.Contains(bx, bz))
        {
            return Array.Empty<GridOffset>();
        }
        int total = map.Width * map.Height;
        int[] previous = new int[total];
        for (int i = 0; i < total; i++)
        {
            previous[i] = -1;
        }
        bool[] seen = new bool[total];
        System.Collections.Generic.Queue<GridOffset> queue = new();
        GridOffset start = new GridOffset(sx, sz);
        seen[map.Index(sx, sz)] = true;
        queue.Enqueue(start);
        int goal = -1;
        ReadOnlySpan<(int X, int Z)> steps = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (queue.Count > 0)
        {
            GridOffset cur = queue.Dequeue();
            if (cur.X == bx && cur.Z == bz)
            {
                goal = map.Index(bx, bz);
                break;
            }
            for (int d = 0; d < steps.Length; d++)
            {
                int nx = cur.X + steps[d].X;
                int nz = cur.Z + steps[d].Z;
                if (!map.Contains(nx, nz) || !map.IsWalkable(nx, nz))
                {
                    continue;
                }
                int i = map.Index(nx, nz);
                if (seen[i])
                {
                    continue;
                }
                seen[i] = true;
                previous[i] = map.Index(cur.X, cur.Z);
                queue.Enqueue(new GridOffset(nx, nz));
            }
        }
        if (goal < 0)
        {
            return Array.Empty<GridOffset>();
        }
        System.Collections.Generic.List<GridOffset> path = new();
        for (int i = goal; i >= 0; i = previous[i])
        {
            path.Add(new GridOffset(i % map.Width, i / map.Width));
        }
        path.Reverse();
        return path.ToArray();
    }
}
