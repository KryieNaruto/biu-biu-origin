namespace Game.Spawn.Rooms;

/// <summary>
/// 房间**内容槽**：房间内部「可放东西的地方」。由 <see cref="IRoomContentPlanner"/> 一次性产出，
/// 之后内容层按槽位去刷怪 / 摆道具 / 放铁砧。
/// <para>
/// <b>为什么要先抽象出槽位</b>：房间内容有三类需求（敌人编队、道具位、机关/道具交互位），
/// 它们的共同点是「在房间里的某个位置、占某个范围」。如果每类各自随机撒点，会出现
/// 「门板正中站一个怪」「道具卡在墙里」这类问题。统一成槽位后，
/// <b>合理摆放变成一次性约束</b>（槽位之间不重叠、离门与墙有安全距离），三类内容共享同一套约束。
/// </para>
/// </summary>
public readonly struct ContentSlot
{
    /// <summary>槽位所属房间。</summary>
    public RoomId Room { get; init; }

    /// <summary>槽位序号（房间内唯一，方便调试日志定位）。</summary>
    public int Index { get; init; }

    /// <summary>槽位用途。</summary>
    public ContentSlotKind Kind { get; init; }

    /// <summary>房间局部坐标 X（0..房间宽-1，单位格，含小数）。由布局换算成世界坐标。</summary>
    public float LocalX { get; init; }

    /// <summary>房间局部坐标 Z（同上）。</summary>
    public float LocalZ { get; init; }

    /// <summary>该槽位的安全半径（格）：内容不能超出这个圆，否则会穿墙/压门。</summary>
    public float Radius { get; init; }

    /// <summary>期望朝向（道具面对门 / 炮台面向房间中心）。</summary>
    public RoomDirection Facing { get; init; }

    /// <inheritdoc />
    public override string ToString() => "Slot " + Room + "#" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " " + Kind + " (" + LocalX.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ","
        + LocalZ.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ")";
}

/// <summary>内容槽用途。</summary>
public enum ContentSlotKind : byte
{
    /// <summary>未指定（规划期占位）。</summary>
    Unspecified = 0,

    /// <summary>敌人编队点（一组敌人围绕它刷出）。</summary>
    Spawn = 1,

    /// <summary>玩家出生点（起点房里唯一的那个）。</summary>
    PlayerStart = 2, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>Boss 出生点（Boss 房里唯一的那个，且在房间最深处）。</summary>
    BossSpawn = 3, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>道具位（商店货架 / 宝箱 / 拾取物）。</summary>
    Prop = 4, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>交互位（铁砧、治疗泉、祭坛）。</summary>
    Interactable = 5, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>装饰点（纯表现：柱子、篝火、地面贴花）。不参与碰撞。</summary>
    Decor = 6, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>奖励点（隐藏房的战利品 / 商店货架）。</summary>
    Reward = 7, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值
}
