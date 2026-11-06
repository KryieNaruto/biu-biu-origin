namespace Game.Spawn.Rooms;

/// <summary>
/// 地牢里**可摆放的东西**的粗细分类。分成这四类是因为它们的「摆放约束」完全不同，
/// 而不是因为美术不同：
/// <list type="bullet">
/// <item><see cref="Building"/>：**建筑**，多格占地（>1 格），会阻断通行，玩家绕行。</item>
/// <item><see cref="Reward"/>：**奖励**，1 格，可拾取，绝不阻断通路（保证一定拿得到）。</item>
/// <item><see cref="Breakable"/>：**可破坏物体**，1 格，挡路但打得掉（木箱、陶罐、路障）。</item>
/// <item><see cref="Solid"/>：**不可破坏物体**，1 格，挡路且打不掉（石像、铁砧、图腾）。</item>
/// </list>
/// <para>
/// <b>为什么用「分类」而不是给每种物件写一个枚举</b>（规则7）：这四类**各自对应一条摆放规则**，
/// 规则只认分类。将来加「毒爆桶」「可推动石台」，只要是 <see cref="Breakable"/> 就不用动任何摆放代码；
/// 只有出现**第五种摆放约束**时才需要加一个分类。
/// </para>
/// </summary>
public enum DungeonPropKind : byte
{
    /// <summary>未指定（占位，不会出现在成品地图里）。</summary>
    None = 0,

    /// <summary>建筑：多格占地、阻断通行。</summary>
    Building = 1,

    /// <summary>奖励：可拾取，永不堵路。</summary>
    Reward = 2, // arch-ok: 枚举序号（协议值），非玩法数值

    /// <summary>可破坏物体：挡路但能被破坏。</summary>
    Breakable = 3, // arch-ok: 枚举序号（协议值），非玩法数值

    /// <summary>不可破坏物体：挡路且打不掉。</summary>
    Solid = 4, // arch-ok: 枚举序号（协议值），非玩法数值
}

/// <summary>
/// 一个已摆好位置的地牢物件（建筑/奖励/可破坏/不可破坏）。
/// <para>
/// 坐标是**画布格坐标**（与 <see cref="DungeonMap.KindAt"/> 同一套），
/// 不是布局坐标 —— 两者差一个 <c>OriginX/OriginZ</c> 偏移，混用会错位一整片，
/// 所以这里只存画布坐标，转换在 <see cref="DungeonPropPlanner"/> 内部一次性完成。
/// </para>
/// </summary>
public readonly struct DungeonProp
{
    /// <summary>物件粗细分类。</summary>
    public DungeonPropKind Kind { get; init; }

    /// <summary>占地左上角的画布 X（格）。</summary>
    public int X { get; init; }

    /// <summary>占地左上角的画布 Z（格）。</summary>
    public int Z { get; init; }

    /// <summary>占地宽（格）。建筑可以 &gt; 1，其余恒为 1。</summary>
    public int Width { get; init; }

    /// <summary>占地深（格）。</summary>
    public int Depth { get; init; }

    /// <summary>所属房间的槽位号（-1 = 走廊/不属于任何房间）。</summary>
    public int RoomSlot { get; init; }

    /// <summary>子类型编号（同一分类下的具体品种，由调用方解释；-1 = 未指定）。</summary>
    public int Variant { get; init; }

    /// <summary>占地右边界（不含）。</summary>
    public int Right => X + Width;

    /// <summary>占地下边界（不含）。</summary>
    public int Bottom => Z + Depth;

    /// <summary>是否占多个格子（建筑的典型特征）。</summary>
    public bool IsMultiCell => Width > 1 || Depth > 1;

    /// <inheritdoc />
    public override string ToString() =>
        "Prop " + Kind + "(" + X.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "," + Z.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ") " + Width.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "x" + Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " v" + Variant.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
