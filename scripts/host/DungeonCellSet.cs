using System;
using System.Collections.Generic;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 一格在 3D 里长成什么。由 <see cref="DungeonCellSet"/> 从**2D 生成结果**（<see cref="DungeonMap"/>）
/// 一一对应地翻译出来，之后交给 <see cref="DungeonTileView"/> 建方砖。
/// <para>
/// <b>为什么不直接用 <see cref="DungeonCellKind"/></b>：那是「2D 规则的口径」（图论/连通性/门），
/// 这是「3D 表现的口径」（长多高、用什么模型、挡不挡路）。两者今天基本对齐，但**必须分开**——
/// 一旦以后加「半高矮墙」「水面砖」，改 2D 枚举会破坏存档与连通性判定，
/// 而改表现类型只影响 <see cref="DungeonTileView"/>。翻译关系唯一地写在本枚举 + <see cref="DungeonCellSet"/> 里。
/// </para>
/// <para>**枚举值显式写出且只加不改**（可能进存档/回放）。</para>
/// </summary>
public enum DungeonTileKind : byte
{
    /// <summary>图外：不建任何东西。</summary>
    Outside = 0,

    /// <summary>房间地面（行走层）。</summary>
    Floor = 1,

    /// <summary>门外的缓冲地面（行走层，无房间类别）。</summary>
    Corridor = 2, // arch-ok: 枚举成员显式赋值（协议值，只加不改），非玩法数值

    /// <summary>墙体（整格立方体 + 岩石模型）。</summary>
    Wall = 3, // arch-ok: 枚举成员显式赋值（协议值，只加不改），非玩法数值

    /// <summary>门（可通行地面 + 门框模型；门板开合由玩法层驱动）。</summary>
    Door = 4, // arch-ok: 枚举成员显式赋值（协议值，只加不改），非玩法数值

    /// <summary>房间内障碍（整格立方体 + 岩石模型，挡路）。</summary>
    Obstacle = 5, // arch-ok: 枚举成员显式赋值（协议值，只加不改），非玩法数值

    /// <summary>隐藏房的秘密通道（行走层，默认不显形）。</summary>
    SecretPassage = 6, // arch-ok: 枚举成员显式赋值（协议值，只加不改），非玩法数值
}

/// <summary>
/// 一格的完整 3D 摆法：砖类型 + 房间类别（上色）+ **摆在砖上的物件**。
/// </summary>
public readonly struct DungeonTile
{
    /// <summary>砖类型。</summary>
    public DungeonTileKind Kind { get; init; }

    /// <summary>所属房间类别（<see cref="RoomCategory.Undecided"/> = 不属于任何房间，例如走廊）。</summary>
    public RoomCategory Category { get; init; }

    /// <summary>房间槽位（-1 = 无房间）。</summary>
    public int RoomSlot { get; init; }

    /// <summary>摆在本格上的物件类别（<see cref="DungeonPropKind.None"/> = 没有）。</summary>
    public DungeonPropKind PropKind { get; init; }

    /// <summary>本格是否是该建筑的**锚点格**（多格建筑只在锚点格生成模型，其余格只画地面）。</summary>
    public bool PropAnchor { get; init; }

    /// <summary>门槽位（-1 = 无门）。</summary>
    public int DoorSlot { get; init; }

    /// <summary>是否是占据格（挡路）。</summary>
    public bool IsSolid => Kind == DungeonTileKind.Wall || Kind == DungeonTileKind.Obstacle || Kind == DungeonTileKind.Outside;

    /// <summary>是否要铺地面。</summary>
    public bool NeedsFloor => Kind == DungeonTileKind.Floor || Kind == DungeonTileKind.Door
        || Kind == DungeonTileKind.Corridor || Kind == DungeonTileKind.SecretPassage;

    /// <summary>是否是「可见的地面砖」（秘密通道默认不铺，等被发现）。</summary>
    public bool IsVisibleFloor => NeedsFloor && Kind != DungeonTileKind.SecretPassage;
}

/// <summary>
/// **一张 2D 地牢图 → 一组 3D 方砖**的翻译结果（不可变快照）。
/// <para>
/// 这是 2D 与 3D 之间的唯一交界：<see cref="DungeonMap"/>（2D 规则）→ <see cref="DungeonCellSet"/>（本类）
/// → <see cref="DungeonTileView"/>（节点）。三条之间没有回边：3D 永远不改 2D 数据。
/// </para>
/// <para>
/// <b>为什么列式存</b>（与 <see cref="DungeonMap"/> 同构）：每格一个 <see cref="DungeonTile"/> 值 + 一条
/// 稠密数组，遍历是 <c>for i in 0..N</c>，没有字典/链表间接寻址，1600 张方砖建场景耗时以毫秒计。
/// </para>
/// </summary>
public sealed class DungeonCellSet
{
    /// <summary>宽（格）。</summary>
    public int Width { get; }

    /// <summary>高（格）。</summary>
    public int Height { get; }

    /// <summary>每格的摆法（下标 = <see cref="Index"/>）。</summary>
    public DungeonTile[] Tiles { get; }

    /// <summary>来源 2D 图的种子（写日志即可复现）。</summary>
    public int Seed { get; }

    /// <summary>各砖类型的格数（诊断/自检用）。</summary>
    public TileKindCounts Counts { get; }

    private DungeonCellSet(DungeonMap map, DungeonTile[] tiles, TileKindCounts counts)
    {
        Width = map.Width;
        Height = map.Height;
        Tiles = tiles;
        Seed = map.Seed;
        Counts = counts;
    }

    /// <summary>格坐标 → 下标。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public int Index(int x, int z) => (z * Width) + x;

    /// <summary>是否在图内。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public bool Contains(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height;

    /// <summary>越界安全取一格。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public DungeonTile At(int x, int z) => Contains(x, z) ? Tiles[Index(x, z)] : default;

    /// <summary>
    /// 从 2D 图建一张方砖表。<b>这是 2D → 3D 的唯一入口</b>。
    /// </summary>
    /// <param name="map">2D 生成结果（含物件）。</param>
    /// <returns>方砖集合；<paramref name="map"/> 无效时返回空集合（宽高为 0，不抛）。</returns>
    public static DungeonCellSet FromMap(DungeonMap map)
    {
        if (!map.IsValid)
        {
            return new DungeonCellSet(default, Array.Empty<DungeonTile>(), default);
        }
        int n = map.CellCount;
        DungeonTile[] tiles = new DungeonTile[n];
        TileKindCounts counts = new();

        // ① 先按 2D 格类型铺砖。
        for (int z = 0; z < map.Height; z++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                int i = map.Index(x, z);
                DungeonCellKind kind = map.Kind[i];
                RoomPlan room = map.PlanAt(x, z);
                DungeonTile tile = new()
                {
                    Kind = Translate(kind),
                    Category = room.Id.IsValid ? room.Category : RoomCategory.Undecided,
                    RoomSlot = room.Id.IsValid ? room.Id.Slot : -1,
                    PropKind = DungeonPropKind.None,
                    DoorSlot = map.DoorAt(x, z),
                };
                tiles[i] = tile;
                counts.Add(tile.Kind);
            }
        }

        // ② 物件盖在砖上。**只有锚点格**（占地左上角）生成模型：
        //    否则一个 2×2 的建筑会在 4 个格子上各生一份，模型叠 4 层。
        DungeonProp[]? props = map.Props;
        if (props is not null)
        {
            for (int p = 0; p < props.Length; p++)
            {
                DungeonProp prop = props[p];
                if (!map.Contains(prop.X, prop.Z))
                {
                    continue;
                }
                int i = map.Index(prop.X, prop.Z);
                DungeonTile tile = tiles[i];
                tiles[i] = new DungeonTile
                {
                    Kind = tile.Kind,
                    Category = tile.Category,
                    RoomSlot = tile.RoomSlot,
                    PropKind = prop.Kind,
                    PropAnchor = true,
                    DoorSlot = tile.DoorSlot,
                };
                // 物件占地格：挡住通行（奖励除外——奖励是踩上去捡的，路面仍然是地面）。
                if (prop.Kind != DungeonPropKind.Reward)
                {
                    for (int dz = 0; dz < prop.Depth; dz++)
                    {
                        for (int dx = 0; dx < prop.Width; dx++)
                        {
                            if (dx == 0 && dz == 0)
                            {
                                continue;
                            }
                            int fx = prop.X + dx;
                            int fz = prop.Z + dz;
                            if (!map.Contains(fx, fz))
                            {
                                continue;
                            }
                            int fi = map.Index(fx, fz);
                            DungeonTile foot = tiles[fi];
                            tiles[fi] = new DungeonTile
                            {
                                Kind = foot.Kind,
                                Category = foot.Category,
                                RoomSlot = foot.RoomSlot,
                                PropKind = foot.PropKind,
                                PropAnchor = false,
                                DoorSlot = foot.DoorSlot,
                            };
                        }
                    }
                }
            }
        }

        // ③ 物件让这一格**变成障碍**：连通性口径已经在 2D 侧算过，这里只影响 3D 画法。
        for (int i = 0; i < n; i++)
        {
            DungeonTile tile = tiles[i];
            if (tile.PropKind == DungeonPropKind.None || tile.PropKind == DungeonPropKind.Reward)
            {
                continue;
            }
            if (tile.Kind == DungeonTileKind.Floor)
            {
                tiles[i] = new DungeonTile
                {
                    Kind = DungeonTileKind.Obstacle,
                    Category = tile.Category,
                    RoomSlot = tile.RoomSlot,
                    PropKind = tile.PropKind,
                    PropAnchor = tile.PropAnchor,
                    DoorSlot = tile.DoorSlot,
                };
            }
        }

        TileKindCounts recount = new();
        for (int i = 0; i < n; i++)
        {
            recount.Add(tiles[i].Kind);
        }
        return new DungeonCellSet(map, tiles, recount);
    }

    /// <summary>
    /// 2D 格类型 → 3D 砖类型。<b>一一对应的唯一实现</b>（新增/改语义只改这里）。
    /// </summary>
    /// <param name="kind">2D 格类型。</param>
    public static DungeonTileKind Translate(DungeonCellKind kind) => kind switch
    {
        DungeonCellKind.Floor => DungeonTileKind.Floor,
        DungeonCellKind.Corridor => DungeonTileKind.Corridor,
        DungeonCellKind.Wall => DungeonTileKind.Wall,
        DungeonCellKind.Door => DungeonTileKind.Door,
        DungeonCellKind.Obstacle => DungeonTileKind.Obstacle,
        DungeonCellKind.SecretPassage => DungeonTileKind.SecretPassage,
        DungeonCellKind.OutOfGrid => DungeonTileKind.Outside,
        _ => DungeonTileKind.Wall,
    };

    /// <summary>
    /// 房间的**占据格数**（按房间槽位统计 <see cref="DungeonTileKind.Floor"/> 及门/障碍格）。
    /// 方砖尺寸契约验收用：一个「中等房间」必须正好是一块 <c>RoomSide × RoomSide</c> 的方砖阵。
    /// </summary>
    /// <param name="slots">输出缓冲（下标 = 房间槽位；长度不足时只统计能放下的部分）。</param>
    /// <returns>实际写入的槽位数。</returns>
    public int CountRoomCells(Span<int> slots)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = 0;
        }
        int maxSlot = 0;
        for (int i = 0; i < Tiles.Length; i++)
        {
            int slot = Tiles[i].RoomSlot;
            if (slot < 0)
            {
                continue;
            }
            if (slot >= slots.Length)
            {
                maxSlot = Math.Max(maxSlot, slot + 1);
                continue;
            }
            slots[slot]++;
        }
        return Math.Max(maxSlot, slots.Length);
    }

    /// <summary>一行砖类型（日志/测试的快速读图）。</summary>
    /// <returns>长度为 <see cref="Height"/> 的字符串数组。</returns>
    public string[] ToRows()
    {
        if (Width <= 0 || Height <= 0)
        {
            return Array.Empty<string>();
        }
        string[] rows = new string[Height];
        char[] line = new char[Width];
        for (int z = 0; z < Height; z++)
        {
            for (int x = 0; x < Width; x++)
            {
                line[x] = ToChar(At(x, z).Kind);
            }
            rows[z] = new string(line);
        }
        return rows;
    }

    /// <summary>砖类型 → 字符（与 2D 字符画同形，便于两张图直接对照）。</summary>
    /// <param name="kind">砖类型。</param>
    public static char ToChar(DungeonTileKind kind) => kind switch
    {
        DungeonTileKind.Floor => '.',
        DungeonTileKind.Corridor => ',',
        DungeonTileKind.Wall => '#',
        DungeonTileKind.Door => '+',
        DungeonTileKind.Obstacle => 'X',
        DungeonTileKind.SecretPassage => '?',
        _ => ' ',
    };

    /// <inheritdoc />
    public override string ToString()
        => "方砖表 " + Width.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + "x" + Height.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 地面=" + Counts.Floor.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 墙=" + Counts.Wall.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 门=" + Counts.Door.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 障碍=" + Counts.Obstacle.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 走廊=" + Counts.Corridor.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 秘密=" + Counts.SecretPassage.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>各砖类型的格数（值语义，避免用 7 个 <c>int</c> 字段在各处重复算）。</summary>
public struct TileKindCounts
{
    /// <summary>地面。</summary>
    public int Floor;

    /// <summary>走廊。</summary>
    public int Corridor;

    /// <summary>墙。</summary>
    public int Wall;

    /// <summary>门。</summary>
    public int Door;

    /// <summary>障碍。</summary>
    public int Obstacle;

    /// <summary>秘密通道。</summary>
    public int SecretPassage;

    /// <summary>计数 +1。</summary>
    /// <param name="kind">砖类型。</param>
    public void Add(DungeonTileKind kind)
    {
        switch (kind)
        {
            case DungeonTileKind.Floor: Floor++; break;
            case DungeonTileKind.Corridor: Corridor++; break;
            case DungeonTileKind.Wall: Wall++; break;
            case DungeonTileKind.Door: Door++; break;
            case DungeonTileKind.Obstacle: Obstacle++; break;
            case DungeonTileKind.SecretPassage: SecretPassage++; break;
            default: break;
        }
    }

    /// <summary>需要铺地面的格数（可见地面 + 门 + 走廊）。</summary>
    public int VisibleFloorTotal => Floor + Corridor + Door;
}
