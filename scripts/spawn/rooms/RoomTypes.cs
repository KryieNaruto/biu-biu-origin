using System;

namespace Game.Spawn.Rooms;

// ─────────────────────────────────────────────────────────────────────────────
// 房间/地牢系统的**基础词汇表**（v1，Lead 定稿 2026-11-04）。
//
// 形状真源：docs/architecture/ARCHITECTURE.md §14「房间与地牢」。
// 本文件只放「没有行为的值」：枚举、Id 结构、方向。它们必须 unmanaged / 纯值，
// 因为 ① 会进 ECS 组件（J2）；② 会被写进存档与回放（§4.7）。
//
// 命名纪律：
//   Room*    = 运行时状态 / 值对象（Game.Spawn.Rooms 私有词汇）
//   IP*      = 规划期接口（Planner），只在「生成路线图」时出现
//   I*       = 生成期接口（Content / Decor）
// 三者都在 scripts/spawn/rooms/ 下，Host 只碰它们的公开方法，业务数据全是 unmanaged。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 房间类别。**枚举值显式写出且只加不改**（存档/回放按数值还原，docs/rules/06 §6）。
/// <para>
/// v1 只区分用户点名的四类 + 一条安全回退，刻意不做「更多房间种类」——
/// 因为「新增一种房间」的正确做法是 <see cref="RoomArchetypeId"/> 指到一条新配置
/// （内容、装饰、权重都在数据里），而不是加一个枚举值。枚举只表达「这个房间在
/// 地牢拓扑里承担什么职责」，这才是需要系统 switch 的东西。
/// </para>
/// </summary>
public enum RoomCategory : byte
{
    /// <summary>未分配的占位（探索中 / 预留格）。不参与任何拓扑规则。</summary>
    Undecided = 0,

    /// <summary>起始房：玩家出生点。整张图恰好 1 个，无敌人，不参与随机掷骰。</summary>
    Start = 1,

    /// <summary>普通战斗房（怪物房）。清空敌人后开门——战斗房的「门」是流程最核心的反馈。</summary>
    Combat = 2, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>特殊功能房：商店、铁匠、治疗泉……具体是什么由 <see cref="RoomArchetypeId"/> 决定。</summary>
    Special = 3, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>隐藏房：必须有 ≥1 条「不可见的秘密通道」通到它，不挂在主出生树上。</summary>
    Hidden = 4, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>原型测试房：直出可刷怪/刷机关，不出现在正式关卡的随机池里（<c>IncludeInRandom</c>=false）。</summary>
    Prototype = 5, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>Boss 房：离起点最远的关键路径终点（v1 只做拓扑位置，不做 Boss 内容）。</summary>
    Boss = 6, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>安全房：无条件可通行，不刷怪，通常紧邻起点（用来放存档点/教学标牌）。</summary>
    Safe = 7, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值
}

/// <summary>
/// 门/房间在网格上的朝向。值是 90° 的整数倍序号，<see cref="RoomDirectionExtensions"/> 提供换算。
/// <para>为什么用 enum 而不是 <c>Vector2Int</c>：跨层传值只会是「四选一」，枚举可在编译期拦住非法方向；
/// 换算成网格偏移的函数收紧在本文件，改坐标系只需要改一处。</para>
/// </summary>
public enum RoomDirection : byte
{
    /// <summary>+Z（俯视角屏幕下方）。</summary>
    South = 0,

    /// <summary>+X（俯视角屏幕右方）。</summary>
    East = 1,

    /// <summary>-Z（俯视角屏幕上方）。</summary>
    North = 2, // arch-ok: 枚举成员显式赋值（方向序号，只加不改），不是玩法数值

    /// <summary>-X（俯视角屏幕左方）。</summary>
    West = 3, // arch-ok: 枚举成员显式赋值（方向序号，只加不改），不是玩法数值
}

/// <summary>
/// 通道类型（门 / 连接的性质）。
/// <para>
/// <b>为什么「隐藏房入口」不做成 <see cref="RoomCategory.Hidden"/> 之外的东西</b>：
/// 「谁能走这条道」是门自己的属性，不是房间的。把 <see cref="Secret"/> 放进门以后，
/// 迷雾/开门判定只需读门，不必反查目标房间。
/// </para>
/// </summary>
public enum DoorKind : byte
{
    /// <summary>未确定的门（规划期占位）。生成期的开门规则一律当普通门处理。</summary>
    Undecided = 0,

    /// <summary>普通门：房间未清场时封锁，清场或无条件后开放。</summary>
    Standard = 1,

    /// <summary>锁门：需要钥匙（v1 只登记属性，钥匙玩法未落地）。</summary>
    Locked = 2, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>秘密通道：不显示在地图上，走到贴图位置才暴露（隐藏房的唯一入口）。</summary>
    Secret = 3, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>单向门：只能从 A 走到 B（v1 只登记属性，通行规则由移动/流程系统解释）。</summary>
    OneWay = 4, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值
}

/// <summary>
/// 房间 Archetype 编号：指向「一组数据」的稳定 int 键。
/// <para>
/// 之所以是 int 而不是 string：它要进 <c>RoomState</c> 这种 unmanaged 组件（J2/D1）。
/// 映射表（id → 权重/大小/内容模板）由数据承载（<c>.tres</c> → Excel），
/// 代码只认数字。0 = 无效，便于「忘记赋值」在生成期立刻报错。
/// </para>
/// </summary>
public readonly struct RoomArchetypeId : IEquatable<RoomArchetypeId>
{
    /// <summary>无效编号（默认值）。任何生成器都不许把 0 分配给正式房间。</summary>
    public static readonly RoomArchetypeId None = new(0);

    /// <summary>数值（&gt; 0 有效）。</summary>
    public int Value { get; }

    /// <summary>构造；负值一律归为 <see cref="None"/>（数据错误在加载期校验，不在这里炸）。</summary>
    public RoomArchetypeId(int value)
    {
        Value = value > 0 ? value : 0;
    }

    /// <summary>是否有效。</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc />
    public bool Equals(RoomArchetypeId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RoomArchetypeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <summary>相等运算符（值语义，可放进字典键与组件比较）。</summary>
    public static bool operator ==(RoomArchetypeId a, RoomArchetypeId b) => a.Value == b.Value;

    /// <summary>不等运算符。</summary>
    public static bool operator !=(RoomArchetypeId a, RoomArchetypeId b) => a.Value != b.Value;

    /// <inheritdoc />
    public override string ToString() => IsValid ? "RA" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "RA(none)";
}

/// <summary>
/// 装饰 Archetype 编号：<see cref="RoomArchetypeId"/> 的孪生兄弟，单独一个类型是为了
/// 「把房间模板填进装饰槽」这种低级错误在编译期就报出来。
/// </summary>
public readonly struct DecorArchetypeId : IEquatable<DecorArchetypeId>
{
    /// <summary>无效编号。</summary>
    public static readonly DecorArchetypeId None = new(0);

    /// <summary>数值（&gt; 0 有效）。</summary>
    public int Value { get; }

    /// <summary>构造。</summary>
    public DecorArchetypeId(int value)
    {
        Value = value > 0 ? value : 0;
    }

    /// <summary>是否有效。</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc />
    public bool Equals(DecorArchetypeId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DecorArchetypeId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <summary>相等运算符。</summary>
    public static bool operator ==(DecorArchetypeId a, DecorArchetypeId b) => a.Value == b.Value;

    /// <summary>不等运算符。</summary>
    public static bool operator !=(DecorArchetypeId a, DecorArchetypeId b) => a.Value != b.Value;

    /// <inheritdoc />
    public override string ToString() => IsValid ? "DA" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "DA(none)";
}

/// <summary>
/// <see cref="RoomDirection"/> 的换算工具：网格偏移、相反方向、房间内锚点参数。
/// <para>坐标系约定（与 <c>Transform3</c> 一致）：<b>+X 东、+Z 南、Y 是高度</b>。
/// 偏移是「格」不是「米」——米制换算只发生在 <c>DungeonLayout</c>（它持有格子边长）。</para>
/// </summary>
public static class RoomDirectionExtensions
{
    /// <summary>方向总数（结构性常量：四向网格）。</summary>
    public const int Count = 4; // arch-ok: 四向网格的拓扑基数（不是可调玩法数值）

    /// <summary>该方向的格子偏移，单位「格」。</summary>
    /// <param name="direction">方向。</param>
    public static GridOffset Offset(this RoomDirection direction) => direction switch
    {
        RoomDirection.South => new GridOffset(0, 1),
        RoomDirection.East => new GridOffset(1, 0),
        RoomDirection.North => new GridOffset(0, -1),
        RoomDirection.West => new GridOffset(-1, 0),
        _ => default,
    };

    /// <summary>相反方向（北↔南、东↔西）。</summary>
    /// <param name="direction">方向。</param>
    public static RoomDirection Opposite(this RoomDirection direction)
        => (RoomDirection)(((int)direction + 2) % Count);

    /// <summary>是否合法方向值。</summary>
    /// <param name="direction">方向。</param>
    public static bool IsValid(this RoomDirection direction) => (uint)direction < Count;
}

/// <summary>
/// 网格坐标（整数格，可比较、unmanaged、零分配）。用它而不是 <c>(int,int)</c> 元组：
/// 元组做字典键时哈希稳定但可读性差，且无法挂 <c>ToString</c> 调试格式。
/// </summary>
public readonly struct GridOffset : IEquatable<GridOffset>
{
    /// <summary>X 格坐标（东为正）。</summary>
    public int X { get; }

    /// <summary>Z 格坐标（南为正）。</summary>
    public int Z { get; }

    /// <summary>构造。</summary>
    public GridOffset(int x, int z)
    {
        X = x;
        Z = z;
    }

    /// <summary>零偏移。</summary>
    public static GridOffset Zero => default;

    /// <inheritdoc />
    public bool Equals(GridOffset other) => X == other.X && Z == other.Z;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GridOffset other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (X * 397) ^ Z;

    /// <summary>相等运算符。</summary>
    public static bool operator ==(GridOffset a, GridOffset b) => a.X == b.X && a.Z == b.Z;

    /// <summary>不等运算符。</summary>
    public static bool operator !=(GridOffset a, GridOffset b) => !(a == b);

    /// <summary>相加（用于「格坐标 + 方向偏移」）。</summary>
    public static GridOffset operator +(GridOffset a, GridOffset b) => new(a.X + b.X, a.Z + b.Z);

    /// <summary>曼哈顿距离（四向网格的最短步数，用于「离起点几步」这类纯拓扑判定）。</summary>
    public static int ManhattanDistance(GridOffset a, GridOffset b)
        => Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z);

    /// <inheritdoc />
    public override string ToString() => "(" + X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + Z.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
}

/// <summary>
/// 「这次生成在哪一层」的世代编号。房间图重建（换层 / 重新开局）时 +1，
/// 用来让上一层的 <c>RoomId</c> 自动失效——同一套「世代号防旧句柄」思想，见 <c>Entity</c>。
/// </summary>
public readonly struct DungeonGeneration : IEquatable<DungeonGeneration>
{
    /// <summary>尚未生成任何地牢。</summary>
    public static readonly DungeonGeneration None = new(0);

    /// <summary>世代号（≥ 0）。</summary>
    public int Value { get; }

    /// <summary>构造。</summary>
    public DungeonGeneration(int value)
    {
        Value = value < 0 ? 0 : value;
    }

    /// <summary>是否有效（≥ 1）。</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc />
    public bool Equals(DungeonGeneration other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DungeonGeneration other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <summary>相等运算符。</summary>
    public static bool operator ==(DungeonGeneration a, DungeonGeneration b) => a.Value == b.Value;

    /// <summary>不等运算符。</summary>
    public static bool operator !=(DungeonGeneration a, DungeonGeneration b) => a.Value != b.Value;

    /// <inheritdoc />
    public override string ToString() => "G" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
