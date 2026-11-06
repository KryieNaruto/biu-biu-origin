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

    /// <summary>配色表；为空时用内置默认配色。</summary>
    [Export]
    public DungeonRoomStyleSet? Styles { get; set; }

    /// <summary>是否画左上角的图例条。</summary>
    [Export]
    public bool ShowLegend { get; set; } = true;

    /// <summary>是否高亮「起始房 → Boss 房」的一条最短路（用于肉眼确认连通性）。</summary>
    [Export]
    public bool HighlightRoute { get; set; } = true;

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
        if (_route.Length > 0)
        {
            DrawRoute();
        }
        if (ShowLegend)
        {
            DrawLegend();
        }
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

    private void DrawLegend()
    {
        string text = "■起始 □战斗 □特殊 □隐藏 □Boss ■墙 ▣门 ·障碍 ┅秘密通道 ─最短路";
        float y = (_map.Height * CellPixels) + 16f; // arch-ok: 图例与地图像素间距（UI 排版）
        DrawString(ThemeDB.FallbackFont, new Vector2(2f, y), text, HorizontalAlignment.Left, -1f, 11);
        string sub = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "seed {0}  网格 {1}×{2}  房间 {3}",
            _map.Seed,
            _map.Width,
            _map.Height,
            _map.Layout.RoomCount);
        DrawString(ThemeDB.FallbackFont, new Vector2(2f, y + 14f), sub, HorizontalAlignment.Left, -1f, 11);
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
