using System;
using Godot;
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

    /// <summary>是否画左上角的图例条。</summary>
    [Export]
    public bool ShowLegend { get; set; } = true;

    /// <summary>是否高亮「起始房 → Boss 房」的一条最短路（用于肉眼确认连通性）。</summary>
    [Export]
    public bool HighlightRoute { get; set; } = true;

    /// <summary>奖励菱形相对一格的半径比例（纯视觉）。</summary>
    private const float RewardDiamondScale = 0.26f; // arch-ok: 图元大小比例（纯视觉排版）

    /// <summary>图例左边距（像素）。</summary>
    private const float LegendMargin = 2f; // arch-ok: 图例边距（纯视觉排版）

    /// <summary>自动铺满时不许缩得比这更小（再小就只剩噪点，看不清格子）。</summary>
    private const float MinCellPixels = 2f; // arch-ok: 缩放下限（纯显示层）

    /// <summary>正好夹到同一个值时不重画，避免浮点尾数导致的每帧重绘。</summary>
    private const float CellPixelsEpsilon = 0.05f; // arch-ok: 浮点比较容差（显示层）

    /// <summary>滚动条/边框预留的宽度（像素）。</summary>
    private const float ScrollbarReserve = 16f; // arch-ok: 滚动条占位（UI 布局尺寸）

    /// <summary>图例区占掉的高度（像素），配合 <see cref="ShowLegend"/> 使用。</summary>
    private const float LegendReservedHeight = 24f; // arch-ok: 图例占位（UI 布局尺寸）

    private DungeonMap _map;
    private bool _hasMap;
    private GridOffset[] _route = Array.Empty<GridOffset>();
    private DungeonRoomStyleSet _stylesResolved = DungeonRoomStyleSet.CreateDefault();

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

        // 留出图例与滚动条占的高度，否则最右一列与最底一行会被滚动条压住。
        float usable = Mathf.Min(
            (available.X - ScrollbarReserve) / _map.Width,
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
    /// 图例：**每一项都画出真实的色块**，后面跟文字说明。
    /// <para>
    /// 上一版图例是一行纯文字（「■起始 □战斗 …」），用户反馈「注释没有颜色，看不出来什么颜色对应什么房间」——
    /// 文字里的方块和地图上的方块毫无关系，等于没写。所以这里改为「先画一块真颜色，再写名字」，
    /// 色块直接取自当前配色表，换 .tres 配色时图例自动跟着变，不会再对不上。
    /// </para>
    /// </summary>
    private void DrawLegend()
    {
        float cell = CellPixels;
        float y = (_map.Height * cell) + 18f; // arch-ok: 图例与地图像素间距（UI 排版）
        float swatch = 10f; // arch-ok: 图例色块边长（UI 排版）
        float gap = 6f; // arch-ok: 图例色块与文字间距（UI 排版）
        float itemGap = 18f; // arch-ok: 图例项之间的间距（UI 排版）
        float x = LegendMargin;
        Font font = ThemeDB.FallbackFont;

        // 第一行：房型（颜色来自配色表，与地图完全同源）。
        x = LegendMargin;
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "起始房", _stylesResolved.Get(RoomCategory.Start).FloorColor, font);
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "战斗房", _stylesResolved.Get(RoomCategory.Combat).FloorColor, font);
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "特殊房", _stylesResolved.Get(RoomCategory.Special).FloorColor, font);
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "隐藏房", _stylesResolved.Get(RoomCategory.Hidden).FloorColor, font);
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "Boss房", _stylesResolved.Get(RoomCategory.Boss).FloorColor, font);
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "墙", WallColor, font);
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "门", DoorColor, font);
        AddLegendSwatch(x, y, swatch, gap, ref x, itemGap, "秘密通道", SecretColor, font);

        // 第二行：四类物件。
        float y2 = y + 16f; // arch-ok: 图例行高（UI 排版）
        x = LegendMargin;
        AddLegendSwatch(x, y2, swatch, gap, ref x, itemGap, "建筑", BuildingColor, font);
        AddLegendSwatch(x, y2, swatch, gap, ref x, itemGap, "奖励", RewardColor, font);
        AddLegendSwatch(x, y2, swatch, gap, ref x, itemGap, "可破坏", BreakableColor, font);
        AddLegendSwatch(x, y2, swatch, gap, ref x, itemGap, "不可破坏", SolidColor, font);
        AddLegendSwatch(x, y2, swatch, gap, ref x, itemGap, "障碍", ObstacleColor, font);

        // 第三行：统计。
        string sub = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "seed {0}   网格 {1}×{2}   房间 {3}   物件 {4}",
            _map.Seed,
            _map.Width,
            _map.Height,
            _map.Layout.RoomCount,
            _map.PropCount);
        DrawString(font, new Vector2(LegendMargin, y2 + 16f), sub, HorizontalAlignment.Left, -1f, 11); // arch-ok: 图例行高（UI 排版）
    }

    /// <summary>画「一块真颜色 + 文字」的图例项，并推进游标。颜色取自当前配色表/导出色，不会与地图脱节。</summary>
    private void AddLegendSwatch(
        float x,
        float y,
        float swatch,
        float gap,
        ref float cursor,
        float itemGap,
        string label,
        Color color,
        Font font)
    {
        DrawRect(new Rect2(cursor, y - swatch, swatch, swatch), color);
        DrawRect(new Rect2(cursor, y - swatch, swatch, swatch), new Color(0f, 0f, 0f, 0.5f), false, 1f);
        cursor += swatch + gap;
        DrawString(font, new Vector2(cursor, y), label, HorizontalAlignment.Left, -1f, 11);
        // 文字宽度按「一个汉字 ≈ 字号」估算即可，图例是排版辅助，不需要精确字宽测量。
        cursor += (label.Length * 11f) + itemGap; // arch-ok: 图例文字宽度估算（UI 排版）
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
