namespace Game.Spawn.Rooms;

/// <summary>
/// 生成一张地牢图的**全部随机与布局参数**（纯 C# 值对象）。
/// <para>
/// <b>数值全部来自这里，代码里不许出现玩法数值</b>（规则4 / GS005）。来源三选一：
/// ① 宿主从 <c>DungeonOptions</c>（<c>[GlobalClass] Resource</c>，可在 Inspector 调）填；
/// ② Excel 落地后从 <c>progression.xlsx</c> 的 <c>Dungeon</c> Sheet 填；
/// ③ 测试里直接 <c>new DungeonConfig { ... }</c>。<see cref="DungeonPlanner"/> 一行都不用改。
/// </para>
/// <para>
/// <b>为什么尺寸是 int/float 而不是「枚举小中大」</b>：枚举大中小会把平衡权从策划手里拿走
/// （改一个数要重编译）。这里每个数都能单独调，<see cref="RoomsPerStep"/> 这类派生量由属性算出，
/// 不允许出现「配置里写了两遍、改一处漏一处」的冗余字段。
/// </para>
/// </summary>
public sealed class DungeonConfig
{
    /// <summary>地牢图的宽（格）。房间都在 <c>[0, Width) × [0, Height)</c> 的格子里。</summary>
    public int GridWidth { get; init; }

    /// <summary>地牢图的高（格）。</summary>
    public int GridHeight { get; init; }

    /// <summary>普通房间边长（格）。</summary>
    public int RoomSide { get; init; }

    /// <summary>起始房边长（格）。通常比普通房大一点，给玩家喘息与教学空间。</summary>
    public int StartRoomSide { get; init; }

    /// <summary>Boss 房边长（格）。</summary>
    public int BossRoomSide { get; init; }

    /// <summary>目标普通房数量（不含 Start/Boss/隐藏房）。实际数量允许 ±<see cref="RoomCountTolerance"/>。</summary>
    public int TargetRoomCount { get; init; }

    /// <summary>房间数允许的偏差（0 = 必须精确）。</summary>
    public int RoomCountTolerance { get; init; }

    /// <summary>隐藏房数量。</summary>
    public int HiddenRoomCount { get; init; }

    /// <summary>特殊功能房数量的下界（商店等）。</summary>
    public int MinSpecialRooms { get; init; }

    /// <summary>特殊功能房数量的上界。</summary>
    public int MaxSpecialRooms { get; init; }

    /// <summary>Boss 房离起点的最小出生树步数（保证「一路打过去」而不是贴脸）。</summary>
    public int MinBossDepth { get; init; }

    /// <summary>每个「生长步」尝试长几间房。步数越少、每步越多 → 图越扁平（分支更多）。</summary>
    public int RoomsPerStep { get; init; }

    /// <summary>关键路径上允许出现特殊房的概率上限（0 = 关键路径上只有战斗房）。</summary>
    public float SpecialOnCriticalPathChance { get; init; }

    /// <summary>隐藏房挂在出生树上时，离起点至少几步（不能开局就撞墙找到）。</summary>
    public int MinHiddenDepth { get; init; }

    /// <summary>
    /// 关键路径循环：把「关键路径上相距 <see cref="LoopDistance"/> 步的两个房间」额外连一条边，
    /// 让地牢更接近「元气骑士」那种可绕回去的环形结构。0 = 不连。
    /// </summary>
    public int LoopDistance { get; init; }

    /// <summary>每格边长（米）。布局换算成世界坐标时用；允许小数（0.5 表示半米格）。</summary>
    public float CellSizeMeters { get; init; }

    /// <summary>
    /// 校验：所有约束必须自洽。**在生成前调用一次**（<see cref="DungeonPlanner"/> 的构造里），
    /// 让配置错误在启动期变成一条明确的中文异常，而不是运行时随机的死循环。
    /// </summary>
    /// <exception cref="System.InvalidOperationException">任一约束被违反，信息里带字段名与实际值。</exception>
    public void Validate()
    {
        Require(GridWidth > 0, nameof(GridWidth), GridWidth);
        Require(GridHeight > 0, nameof(GridHeight), GridHeight);
        Require(RoomSide > 0, nameof(RoomSide), RoomSide);
        Require(StartRoomSide > 0, nameof(StartRoomSide), StartRoomSide);
        Require(BossRoomSide > 0, nameof(BossRoomSide), BossRoomSide);
        Require(TargetRoomCount >= 0, nameof(TargetRoomCount), TargetRoomCount);
        Require(RoomCountTolerance >= 0, nameof(RoomCountTolerance), RoomCountTolerance);
        Require(HiddenRoomCount >= 0, nameof(HiddenRoomCount), HiddenRoomCount);
        Require(MinSpecialRooms >= 0, nameof(MinSpecialRooms), MinSpecialRooms);
        Require(MaxSpecialRooms >= MinSpecialRooms, nameof(MaxSpecialRooms), MaxSpecialRooms);
        Require(MaxSpecialRooms <= TargetRoomCount + RoomCountTolerance + MinSpecialRooms,
            nameof(MaxSpecialRooms), MaxSpecialRooms);
        Require(MinBossDepth >= 0, nameof(MinBossDepth), MinBossDepth);
        Require(RoomsPerStep > 0, nameof(RoomsPerStep), RoomsPerStep);
        Require(SpecialOnCriticalPathChance >= 0f && SpecialOnCriticalPathChance <= 1f,
            nameof(SpecialOnCriticalPathChance), SpecialOnCriticalPathChance);
        Require(MinHiddenDepth >= 0, nameof(MinHiddenDepth), MinHiddenDepth);
        Require(LoopDistance >= 0, nameof(LoopDistance), LoopDistance);
        Require(CellSizeMeters > 0f, nameof(CellSizeMeters), CellSizeMeters);
        Require(StartRoomSide <= GridWidth && StartRoomSide <= GridHeight, nameof(StartRoomSide), StartRoomSide);
        Require(BossRoomSide <= GridWidth && BossRoomSide <= GridHeight, nameof(BossRoomSide), BossRoomSide);
    }

    private static void Require(bool condition, string field, float actual)
    {
        if (condition)
        {
            return;
        }
        throw new System.InvalidOperationException(
            "地牢配置非法：DungeonConfig." + field + " 不满足约束，实际值 = "
            + actual.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "。");
    }

    private static void Require(bool condition, string field, int actual) => Require(condition, field, (float)actual);
}
