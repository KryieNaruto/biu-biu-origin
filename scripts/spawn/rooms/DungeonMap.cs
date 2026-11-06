using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Spawn.Rooms;

// ─────────────────────────────────────────────────────────────────────────────
// 地牢的「一张图」——2D 字符画抽象（Lead 定稿 2026-11-05）。
//
// 用户原话：「正方形代表场地，颜色代表场地类型，地砖类型。方块代表障碍。
//            首先测试连通性与随机性。」
//
// 于是本文件把**一整层地牢画成一张字符网格**：每格恰好一个字符，
// 一行一个字符串。它同时是
//   ① 策划/我肉眼读图的口径（控制台、日志、编辑器左侧预览都用它）；
//   ② 自动测试的口径（连通性 BFS 直接在这张字符图上跑，见 DungeonMapCheck）；
//   ③ 以后换真美术时的「对照表」（哪个字符该长什么样，见 DungeonMapLegend）。
//
// 为什么不直接画 3D：3D 白盒（之前的 out/dungeon/*.png）看得出房间但数不清格子，
// 也数不清门和通道。**图论问题必须在网格上判**，这是地理信息与关卡设计的通行做法：
// 先出 grid map，再上表现层。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 地图上每个格子表达什么。**枚举值显式写出且只加不改**（数据行、回放、日志都按数值走）。
/// <para>
/// 分两类：<b>占据格</b>（<see cref="Solid"/>：墙/障碍，不可通行）与<b>可走格</b>（其余），
/// <see cref="IsWalkable"/> 是唯一判据——连通性判定不许再写第二份「什么能走」。
/// </para>
/// </summary>
public enum DungeonCellKind : byte
{
    /// <summary>图外（不该出现在图里；出现即 bug）。</summary>
    OutOfGrid = 0,

    /// <summary>不可通行的墙/岩石（房间外轮廓）。</summary>
    Wall = 1,

    /// <summary>房间地面（房间类别见同房间的 <c>RoomCategory</c>）。</summary>
    Floor = 2, // arch-ok: 枚举成员显式赋值（图例与存档按数值还原，只加不改）

    /// <summary>门（同一格同时记着它在哪面墙上、通向谁）。</summary>
    Door = 3, // arch-ok: 枚举成员显式赋值

    /// <summary>门外的通道地面：两房之间的**缓冲格**，门只修一间时保证仍连通。</summary>
    Corridor = 4, // arch-ok: 枚举成员显式赋值

    /// <summary>房间内的障碍（柱子/箱子/岩石）。</summary>
    Obstacle = 5, // arch-ok: 枚举成员显式赋值

    /// <summary>隐藏房的秘密通道地面（地图上不主动显示）。</summary>
    SecretPassage = 6, // arch-ok: 枚举成员显式赋值
}

/// <summary><see cref="DungeonCellKind"/> 的判据与字符映射的唯一实现。</summary>
public static class DungeonCellKinds
{
    /// <summary>是否是「占据格」（不可通行）。墙与障碍算占据。</summary>
    /// <param name="kind">格类型。</param>
    public static bool IsSolid(this DungeonCellKind kind)
        => kind == DungeonCellKind.OutOfGrid || kind == DungeonCellKind.Wall || kind == DungeonCellKind.Obstacle;

    /// <summary>是否可通行（非占据格）。连通性判定的唯一口径。</summary>
    /// <param name="kind">格类型。</param>
    public static bool IsWalkable(this DungeonCellKind kind) => kind != DungeonCellKind.OutOfGrid && !kind.IsSolid();

    /// <summary>字符画里代表的字符（ASCII，控制台/日志/测试都能安全打印）。</summary>
    /// <param name="kind">格类型。</param>
    public static char ToChar(this DungeonCellKind kind) => kind switch
    {
        DungeonCellKind.Wall => '#',
        DungeonCellKind.Floor => '.',
        DungeonCellKind.Door => '+',
        DungeonCellKind.Corridor => ',',
        DungeonCellKind.Obstacle => 'X',
        DungeonCellKind.SecretPassage => '?',
        _ => ' ',
    };

    /// <summary>字符 → 格类型（图例与控制台读图共用；未知字符一律当墙）。</summary>
    /// <param name="c">字符。</param>
    public static DungeonCellKind FromChar(char c) => c switch
    {
        '.' => DungeonCellKind.Floor,
        '+' => DungeonCellKind.Door,
        ',' => DungeonCellKind.Corridor,
        'X' => DungeonCellKind.Obstacle,
        '?' => DungeonCellKind.SecretPassage,
        '#' => DungeonCellKind.Wall,
        _ => DungeonCellKind.OutOfGrid,
    };

    /// <summary>房间类别在字符画里用的字符（房间地面用类别字符，一眼分辨房型）。</summary>
    /// <param name="category">房间类别。</param>
    public static char ToChar(this RoomCategory category) => category switch
    {
        RoomCategory.Start => 'S',
        RoomCategory.Combat => 'c',
        RoomCategory.Special => '$',
        RoomCategory.Hidden => 'h',
        RoomCategory.Boss => 'B',
        RoomCategory.Safe => 's',
        RoomCategory.Prototype => 'p',
        _ => '.',
    };

    /// <summary>格子所属房间类别 → 地图上表示该格的字符（房间地面按房型上色/换字符）。</summary>
    /// <param name="kind">格类型。</param>
    /// <param name="room">该格所属房间（无房间时传 default）。</param>
    public static char SymbolFor(this DungeonCellKind kind, in RoomPlan room)
    {
        if (kind == DungeonCellKind.Floor && room.Id.IsValid)
        {
            return room.Category.ToChar();
        }
        return kind.ToChar();
    }
}

/// <summary>
/// 一层地牢的**网格快照**（不可变）。生成后不再变化；重新生成 = 换一张新快照。
/// <para>
/// 三张并行数组（<see cref="Kind"/> / <see cref="RoomSalt"/> / <see cref="DoorSlot"/>）是「列式布局」：
/// 按格取一个字段只读一个数组，连通性 BFS 只扫 <see cref="Kind"/> 一个数组就不会被字符串迷路。
/// </para>
/// <para>
/// <b>为什么格是正方形</b>：用户点名「场景为简单的正方形即可」。于是索引是 <c>z * Width + x</c>，
/// 画出来的字符画与屏幕上看到的完全一致（一行一行从上往下读即可）。
/// </para>
/// </summary>
public readonly struct DungeonMap
{
    /// <summary>宽（格）。</summary>
    public int Width { get; init; }

    /// <summary>高（格）。</summary>
    public int Height { get; init; }

    /// <summary>该图对应的地牢布局（房间/链路/门；隐藏房也在里面）。</summary>
    public DungeonLayout Layout { get; init; }

    /// <summary>每格的类型（下标 = <see cref="Index"/>）。</summary>
    public DungeonCellKind[] Kind { get; init; }

    /// <summary>每格所属房间的槽位（-1 = 不属于任何房间）。</summary>
    public int[] RoomSalt { get; init; }

    /// <summary>每格上的门槽位（-1 = 没有门；门在 <see cref="DungeonLayout.Doors"/> 里的下标）。</summary>
    public int[] DoorSlot { get; init; }

    /// <summary>最近一次构建这张图的种子（写进日志即可复现）。</summary>
    public int Seed { get; init; }

    /// <summary>构建参数快照（改这些值要重新生成）。</summary>
    public DungeonMapConfig Config { get; init; }

    /// <summary>总格数。</summary>
    public int CellCount => Width * Height;

    /// <summary>
    /// 布局坐标 → 地图坐标的列偏移（<c>mapX = layoutX + OriginX</c>）。
    /// <para>
    /// <b>为什么必须有这一对函数：</b>地图画布是「房间并集包围盒 + Margin」，
    /// 布局里的房间坐标是<b>原始网格坐标</b>（可能从 0 开始，也可能从别的值开始）。
    /// 两者不一致，任何「拿 RoomPlan.Cell 直接当地图下标」的写法都会错位 ——
    /// 2026-11-05 实测：这导致「起点房中心格落在地图画布之外」的误报。
    /// </para>
    /// </summary>
    public int OriginX { get; init; }

    /// <summary>布局坐标 → 地图坐标的行偏移（<c>mapZ = layoutZ + OriginZ</c>）。</summary>
    public int OriginZ { get; init; }

    /// <summary>布局坐标 → 地图列。</summary>
    /// <param name="layoutX">布局列。</param>
    public int ToMapX(int layoutX) => layoutX + OriginX;

    /// <summary>布局坐标 → 地图行。</summary>
    /// <param name="layoutZ">布局行。</param>
    public int ToMapZ(int layoutZ) => layoutZ + OriginZ;

    /// <summary>该房间中心格的**地图坐标**（越界时返回的画布检查交给调用方）。</summary>
    /// <param name="room">房间。</param>
    public (int X, int Z) RoomCenter(in RoomPlan room)
        => (ToMapX(room.Cell.X + (room.Footprint.Width / 2)), ToMapZ(room.Cell.Z + (room.Footprint.Depth / 2)));

    /// <summary>该门所在格的**地图坐标**。</summary>
    /// <param name="door">门。</param>
    public (int X, int Z) DoorCell(in DungeonLayout layout, in RoomDoor door)
    {
        GridOffset cell = DungeonMapBuilder.DoorCell(in layout, in door);
        return (ToMapX(cell.X), ToMapZ(cell.Z));
    }

    /// <summary>是否是一张可用的图（宽度、高、数组长度自洽）。</summary>
    public bool IsValid => Width > 0 && Height > 0 && Kind is not null && Kind.Length == CellCount
        && RoomSalt is not null && RoomSalt.Length == CellCount
        && DoorSlot is not null && DoorSlot.Length == CellCount;

    /// <summary>格坐标 → 数组下标。</summary>
    /// <param name="x">列（0 = 最左）。</param>
    /// <param name="z">行（0 = 最上）。</param>
    public int Index(int x, int z) => (z * Width) + x;

    /// <summary>是否在图内。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public bool Contains(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height;

    /// <summary>越界安全取格类型。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public DungeonCellKind KindAt(int x, int z) => Contains(x, z) ? Kind[Index(x, z)] : DungeonCellKind.OutOfGrid;

    /// <summary>越界安全取所属房间槽位（-1 = 无）。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public int RoomAt(int x, int z) => Contains(x, z) ? RoomSalt[Index(x, z)] : -1;

    /// <summary>越界安全取该格所属房间（无房间返回 default）。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public RoomPlan PlanAt(int x, int z)
    {
        int slot = RoomAt(x, z);
        return slot >= 0 && Layout.Rooms is not null && slot < Layout.RoomCount ? Layout.Rooms[slot] : default;
    }

    /// <summary>越界安全取门槽位（-1 = 没有门）。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public int DoorAt(int x, int z) => Contains(x, z) ? DoorSlot[Index(x, z)] : -1;

    /// <summary>该格是否可通行。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public bool IsWalkable(int x, int z) => KindAt(x, z).IsWalkable();

    /// <summary>
    /// 一行字符串（<see cref="ToRows"/> 用）。
    /// </summary>
    /// <param name="row">行号。</param>
    /// <param name="sb">输出缓冲。</param>
    private void AppendRow(int row, System.Text.StringBuilder sb)
    {
        sb.Length = 0;
        for (int x = 0; x < Width; x++)
        {
            int i = Index(x, row);
            RoomPlan room = RoomSalt[i] >= 0 && Layout.Rooms is not null && RoomSalt[i] < Layout.RoomCount
                ? Layout.Rooms[RoomSalt[i]]
                : default;
            sb.Append(Kind[i].SymbolFor(in room));
        }
    }

    /// <summary>整张图 → 一行一个字符串（行从上到下、列从左到右）。</summary>
    /// <returns>长度为 <see cref="Height"/> 的字符串数组，每个字符串长为 <see cref="Width"/>。</returns>
    public string[] ToRows()
    {
        if (!IsValid)
        {
            return Array.Empty<string>();
        }
        string[] rows = new string[Height];
        System.Text.StringBuilder sb = new System.Text.StringBuilder(Width);
        for (int z = 0; z < Height; z++)
        {
            AppendRow(z, sb);
            rows[z] = sb.ToString();
        }
        return rows;
    }

    /// <summary>
    /// 带行号列标的完整字符画（控制台/日志/编辑器预览共用）。每行前补 2 位行号 + 空格。
    /// </summary>
    /// <returns>可直接 <c>GD.Print</c> 的多行文本。</returns>
    public string ToArt()
    {
        string[] rows = ToRows();
        System.Text.StringBuilder sb = new System.Text.StringBuilder((Width + ArtIndent) * (Height + 1));
        for (int z = 0; z < rows.Length; z++)
        {
            sb.Append(z.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).Append(" ").Append(rows[z]).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>行号前缀宽度（<c>00  </c>）。</summary>
    private const int ArtIndent = 3; // arch-ok: 文本排版缩进（纯格式），不是玩法数值

    /// <summary>从字符画还原一张图（测试与「手工摆图」用；布局部分为空）。</summary>
    /// <param name="art">字符画。</param>
    /// <returns>还原出的图（<see cref="Layout"/> 为空布局）。</returns>
    public static DungeonMap FromArt(IReadOnlyList<string> art)
    {
        if (art is null || art.Count == 0)
        {
            return default;
        }
        int height = art.Count;
        int width = 0;
        for (int z = 0; z < height; z++)
        {
            width = art[z].Length > width ? art[z].Length : width;
        }
        DungeonCellKind[] kind = new DungeonCellKind[width * height];
        int[] room = new int[width * height];
        int[] door = new int[width * height];
        for (int i = 0; i < kind.Length; i++)
        {
            room[i] = -1;
            door[i] = -1;
        }
        for (int z = 0; z < height; z++)
        {
            string line = art[z];
            for (int x = 0; x < line.Length; x++)
            {
                char c = line[x];
                DungeonCellKind k = DungeonCellKinds.FromChar(c);
                if (k == DungeonCellKind.OutOfGrid)
                {
                    // 房型字符也要能读回来：它们都是「房间地面」。
                    k = IsCategorySymbol(c) ? DungeonCellKind.Floor : DungeonCellKind.Wall;
                }
                kind[(z * width) + x] = k;
            }
        }
        return new DungeonMap
        {
            Width = width,
            Height = height,
            Kind = kind,
            RoomSalt = room,
            DoorSlot = door,
            Layout = default,
        };
    }

    /// <summary>是否是房型字符（<see cref="DungeonCellKinds.ToChar(RoomCategory)"/> 的产物）。</summary>
    /// <param name="c">字符。</param>
    private static bool IsCategorySymbol(char c)
        => c == 'S' || c == 'c' || c == '$' || c == 'h' || c == 'B' || c == 's' || c == 'p';

    /// <inheritdoc />
    public override string ToString()
        => "DungeonMap " + Width.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "x" + Height.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " seed=" + Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " rooms=" + (Layout.Rooms is null ? 0 : Layout.RoomCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
