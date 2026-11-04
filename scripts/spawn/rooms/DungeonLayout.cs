namespace Game.Spawn.Rooms;

/// <summary>
/// 房间尺寸（单位：格）。v1 一律正方形（用户点名：「场景为简单的正方形即可」），
/// 但**只暴露边长的概念、不暴露 W/H 两个字段**——将来要矩形时改的是本结构 + 布局换算，
/// 所有生成器仍然通过 <see cref="Area"/> 拿「房间占总格数」，不知道正方形这件事。
/// <para>
/// 为什么尺寸在「格」而不是「米」：地牢图是离散的（门只出现在整格边界、房间不重叠、链路对齐），
/// 米制是表现层的换算。把米制换算收紧在 <see cref="DungeonLayout"/> 里，图论算法就能裸跑单测。
/// </para>
/// </summary>
public readonly struct RoomFootprint
{
    /// <summary>边长（格），≥ 1。</summary>
    public int Side { get; }

    /// <summary>构造；非正值收敛为 1（数据错误在加载期校验，这里不炸）。</summary>
    public RoomFootprint(int side)
    {
        Side = side < 1 ? 1 : side;
    }

    /// <summary>边长（格）。</summary>
    public int Width => Side;

    /// <summary>边长（格）。</summary>
    public int Depth => Side;

    /// <summary>房间占的总格数。</summary>
    public int Area => Side * Side;

    /// <inheritdoc />
    public override string ToString() => Side.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x"
        + Side.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 一个房间在地牢图里的**不可变描述**（规划产物）。生成完成后这份数据不再变化。
/// <para>
/// 可变状态（是否已清、是否已访问、已发现的门）<b>不在这里</b>——那属于运行时，
/// 走 ECS 组件（<c>RoomState</c>）或流程层。把两者放进同一个结构是最常见的「越写越乱」起点：
/// 一旦 <c>RoomPlan</c> 里出现 <c>bool Cleared</c>，它就会同时被三个系统写。
/// </para>
/// </summary>
public readonly struct RoomPlan
{
    /// <summary>房间句柄。</summary>
    public RoomId Id { get; init; }

    /// <summary>房间类别（拓扑职责）。</summary>
    public RoomCategory Category { get; init; }

    /// <summary>房间模板编号（内容与装饰的来源）。</summary>
    public RoomArchetypeId Archetype { get; init; }

    /// <summary>网格坐标（格，房间左下/左上角由布局器统一解释）。</summary>
    public GridOffset Cell { get; init; }

    /// <summary>尺寸（格）。</summary>
    public RoomFootprint Footprint { get; init; }

    /// <summary>距起点的出生树步数（起点 = 0；不在出生树上 = <see cref="UnreachableDepth"/>）。</summary>
    public int Depth { get; init; }

    /// <summary>是否在关键路径上（起点 → Boss 的最短链）。</summary>
    public bool OnCriticalPath { get; init; }

    /// <summary>是否只能经秘密通道到达。</summary>
    public bool IsSecretOnly { get; init; }

    /// <summary>房间内的具名锚点（不可变数组，规划期一次建好）。</summary>
    public RoomAnchor[] Anchors { get; init; }

    /// <summary>「不可达」的深度哨兵值。</summary>
    public const int UnreachableDepth = -1; // arch-ok: 拓扑哨兵值（-1 表示不在出生树上），不是玩法数值

    /// <summary>是否可达（在出生树上）。</summary>
    public bool IsReachable => Depth >= 0;

    /// <summary>找一个具名锚点。</summary>
    /// <param name="name">锚点名。</param>
    /// <param name="anchor">找到的锚点。</param>
    public bool TryGetAnchor(string name, out RoomAnchor anchor)
    {
        RoomAnchor[] anchors = Anchors;
        if (anchors is null || name is null)
        {
            anchor = default;
            return false;
        }
        for (int i = 0; i < anchors.Length; i++)
        {
            if (string.Equals(anchors[i].Name, name, System.StringComparison.Ordinal))
            {
                anchor = anchors[i];
                return true;
            }
        }
        anchor = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString()
        => "Room " + Id + " " + Category + " " + Archetype + " @" + Cell + " d=" + Depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 一层地牢的**规划结果**（纯拓扑 + 尺寸，不含任何引擎类型、不含运行时状态）。
/// <para>
/// 这是「随机且合理」的落点：所有随机决策都在 <see cref="DungeonLayout"/> 里、
/// 全部走注入的 <c>IRandomProvider</c>，于是 <b>同 seed → 逐字节相同的一张图</b>（可回放、可复现 bug）。
/// 之后的内容填充 / 装饰摆放 / Host 侧建场景都只读它，不再掷骰。
/// </para>
/// <para>
/// 可变性纪律：数组一次性建好后<b>不许改</b>（<c>readonly struct</c> 只是壳）。
/// 需要「探索中增量生成」的版本请另起类型，不要在这里开一个 <c>Mutable = true</c> 开关。
/// </para>
/// </summary>
public readonly struct DungeonLayout
{
    /// <summary>世代号（每次重建 +1）。</summary>
    public DungeonGeneration Generation { get; init; }

    /// <summary>本层种子（= 生成这张图时用的 seed，写进存档即可复现）。</summary>
    public int Seed { get; init; }

    /// <summary>全部房间（下标即 <see cref="RoomId.Slot"/>）。</summary>
    public RoomPlan[] Rooms { get; init; }

    /// <summary>全部链路（下标即 <see cref="RoomLink.Slot"/>）。</summary>
    public RoomLink[] Links { get; init; }

    /// <summary>全部门（下标即 <see cref="DoorId.Slot"/>）。</summary>
    public RoomDoor[] Doors { get; init; }

    /// <summary>起点房间。</summary>
    public RoomId Start { get; init; }

    /// <summary>Boss 房间（v1 一定存在；不做 Boss 的地牢类型可在数据里把它当成终点房）。</summary>
    public RoomId Boss { get; init; }

    /// <summary>入口门（从上一层进来 / 玩家出生用的那条门）。</summary>
    public DoorId Entrance { get; init; }

    /// <summary>房间数。</summary>
    public int RoomCount => Rooms is null ? 0 : Rooms.Length;

    /// <summary>链路数。</summary>
    public int LinkCount => Links is null ? 0 : Links.Length;

    /// <summary>门数。</summary>
    public int DoorCount => Doors is null ? 0 : Doors.Length;

    /// <summary>取房间（槽位非法时返回 default，调用方应先查 <see cref="TryGetRoom"/>）。</summary>
    /// <param name="id">房间句柄。</param>
    public RoomPlan this[RoomId id] => Rooms[id.Slot];

    /// <summary>是否包含该句柄（世代号必须匹配，旧世代句柄一律 false）。</summary>
    /// <param name="id">房间句柄。</param>
    public bool Contains(RoomId id) => id.Generation == Generation && id.Slot >= 0 && id.Slot < RoomCount;

    /// <summary>按网格坐标找房间（占位查询用，O(n)，n ≤ 网格边长）；找不到返回 false。</summary>
    /// <param name="cell">格坐标。</param>
    /// <param name="room">找到的房间。</param>
    public bool TryGetRoomAt(GridOffset cell, out RoomPlan room)
    {
        RoomPlan[] rooms = Rooms;
        if (rooms is not null)
        {
            for (int i = 0; i < rooms.Length; i++)
            {
                if (rooms[i].Cell == cell)
                {
                    room = rooms[i];
                    return true;
                }
            }
        }
        room = default;
        return false;
    }

    /// <summary>本图是否已经是一张合法的地牢图（供 <see cref="DungeonValidation"/> 复用的快速自检）。</summary>
    public bool IsValid => Rooms is not null && Links is not null && Doors is not null
        && Contains(Start) && Contains(Boss);

    /// <inheritdoc />
    public override string ToString()
        => "Dungeon " + Generation + " seed=" + Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " rooms=" + RoomCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " links=" + LinkCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " doors=" + DoorCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
