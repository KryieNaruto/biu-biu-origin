using System;
using Game.Core;

namespace Game.Spawn.Rooms;

/// <summary>
/// 房间**内容**的参数：每个数都是玩法数值，所以全部来自注入的配置（规则4 / GS005），
/// 本文件只决定「怎么用」，不决定「用多少」。
/// <para>
/// 来源三选一（与 <see cref="DungeonConfig"/> 同一范式）：① 宿主从 <c>DungeonOptions</c>（Resource）填；
/// ② Excel 落地后从 <c>progression.xlsx</c> 的 <c>RoomContent</c> Sheet 填；③ 测试里直接 <c>new</c>。
/// 换一组数 = 换一份配置，<see cref="GridContentPlanner"/> 一行都不用改。
/// </para>
/// </summary>
public sealed class RoomContentConfig
{
    /// <summary>起点房的刷怪点数（v1 起点无敌人，常用 0，保留用于教学靶子）。</summary>
    public int StartRoomSpawnCount { get; init; }

    /// <summary>普通战斗房的刷怪点数。</summary>
    public int CombatRoomSpawnCount { get; init; }

    /// <summary>特殊功能房的刷怪点数（商店/铁匠一般不刷怪）。</summary>
    public int SpecialRoomSpawnCount { get; init; }

    /// <summary>Boss 房的杂兵刷怪点数（Boss 本体用单独的 <see cref="ContentSlotKind.BossSpawn"/> 槽）。</summary>
    public int BossRoomSpawnCount { get; init; }

    /// <summary>隐藏房的刷怪点数（v1 隐藏房以奖励为主）。</summary>
    public int HiddenRoomSpawnCount { get; init; }

    /// <summary>特殊房的交互位数量（铁砧/治疗泉/祭坛）。</summary>
    public int SpecialRoomInteractableCount { get; init; }

    /// <summary>战斗房的道具位数量（宝箱/补给）。</summary>
    public int CombatRoomPropCount { get; init; }

    /// <summary>隐藏房的奖励位数量（战利品）。</summary>
    public int HiddenRoomRewardCount { get; init; }

    /// <summary>每个槽位自身的半径（格）：内容不能超出该圆，否则会穿墙/压门。</summary>
    public float SlotRadius { get; init; }

    /// <summary>离墙的最小距离（格）：必须 &gt;= <see cref="SlotRadius"/>，否则槽位会骑在墙上。</summary>
    public float WallClearance { get; init; }

    /// <summary>离门的最小距离（格）：门内侧不放东西，否则「开门即贴脸」。</summary>
    public float DoorClearance { get; init; }

    /// <summary>槽位之间额外留的间距（格，叠在半径之上）：编队之间不至于挤成一坨。</summary>
    public float SlotPadding { get; init; }

    /// <summary>每个槽位最多尝试的随机落点数；都失败就放弃该槽位（不无限重试）。</summary>
    public int PlacementAttempts { get; init; }

    /// <summary>刷怪点绕房间中心的散射半径下限（格）。</summary>
    public float SpawnRadiusMin { get; init; }

    /// <summary>刷怪点绕房间中心的散射半径上限（格）。</summary>
    public float SpawnRadiusMax { get; init; }

    /// <summary>校验：数值必须自洽。构造时调用一次。</summary>
    /// <exception cref="InvalidOperationException">任一约束被违反。</exception>
    public void Validate()
    {
        Require(StartRoomSpawnCount >= 0, nameof(StartRoomSpawnCount));
        Require(CombatRoomSpawnCount >= 0, nameof(CombatRoomSpawnCount));
        Require(SpecialRoomSpawnCount >= 0, nameof(SpecialRoomSpawnCount));
        Require(BossRoomSpawnCount >= 0, nameof(BossRoomSpawnCount));
        Require(HiddenRoomSpawnCount >= 0, nameof(HiddenRoomSpawnCount));
        Require(SpecialRoomInteractableCount >= 0, nameof(SpecialRoomInteractableCount));
        Require(CombatRoomPropCount >= 0, nameof(CombatRoomPropCount));
        Require(HiddenRoomRewardCount >= 0, nameof(HiddenRoomRewardCount));
        Require(SlotRadius > 0f, nameof(SlotRadius));
        Require(WallClearance >= SlotRadius, nameof(WallClearance));
        Require(DoorClearance >= 0f, nameof(DoorClearance));
        Require(SlotPadding >= 0f, nameof(SlotPadding));
        Require(PlacementAttempts > 0, nameof(PlacementAttempts));
        Require(SpawnRadiusMin >= 0f && SpawnRadiusMax >= SpawnRadiusMin, nameof(SpawnRadiusMax));
    }

    private static void Require(bool condition, string field)
    {
        if (!condition)
        {
            throw new InvalidOperationException("房间内容配置非法：RoomContentConfig." + field + " 不满足约束。");
        }
    }
}

/// <summary>
/// 网格式房间内容规划器：把每个房间变成一组 <see cref="ContentSlot"/>（刷怪点、道具位、Boss 位、玩家出生点）。
/// <para>
/// <b>它不刷任何东西</b>：只产出「在房间里的哪个位置、占多大范围、朝哪」。真正生成实体的是
/// <see cref="IRoomContentSpawner"/>（走 ECS 命令缓冲）。这样同一份槽位可以被刷怪 / 摆道具 / 放机关三家消费，
/// 而「别压门、别穿墙、别互相叠」这三条约束**只实现一次**。
/// </para>
/// <para>
/// <b>摆放算法</b>：每个槽位先用房间随机子流掷一个「绕房间中心、半径在
/// [<see cref="RoomContentConfig.SpawnRadiusMin"/>, <see cref="RoomContentConfig.SpawnRadiusMax"/>]」的候选点，
/// 再过四道过滤：① 离墙 &gt;= <see cref="RoomContentConfig.WallClearance"/>；
/// ② 离本房间每一扇门 &gt;= <see cref="RoomContentConfig.DoorClearance"/>；
/// ③ 与已放槽位圆-圆不重叠（半径和 + <see cref="RoomContentConfig.SlotPadding"/>）。
/// 全过才采用；<see cref="RoomContentConfig.PlacementAttempts"/> 次都不行就放弃该槽位——**宁可少放一个，也不叠在一起**。
/// </para>
/// <para>
/// <b>固定槽位</b>：<see cref="ContentSlotKind.PlayerStart"/> 与 <see cref="ContentSlotKind.BossSpawn"/>
/// 不掷骰，直接取布局层写进 <see cref="RoomPlan.Anchors"/> 的具名锚点（<c>PlayerStart</c> / <c>BossSpawn</c>），
/// 于是「出生点贴入口、Boss 在最深处」只由布局层定义，内容层不重复决策。它们会**先占位**，
/// 后面的随机槽位必须绕开它们。
/// </para>
/// <para>
/// <b>确定性</b>：同 layout + 同 seed → 同一组槽位。第 k 间房用 <c>root.Fork(PlannerSalt).Fork(RoomSalt ^ slot ^ seed)</c>，
/// 房间之间互不影响。
/// </para>
/// </summary>
public sealed class GridContentPlanner : IRoomContentPlanner
{
    /// <summary>规划器子流盐（与其他子系统隔离；改它 = 让历史回放失效）。</summary>
    private const int PlannerSalt = 0x6C03; // arch-ok: 随机子流盐常量（名字即契约），非玩法数值

    /// <summary>房间子流盐：再按房间槽位异或，实现「每间房一条独立子流」。</summary>
    private const int RoomSalt = 0x6C04; // arch-ok: 随机子流盐常量，非玩法数值

    /// <summary>房间子流之间的步长（让相邻房间的盐值不重合）。</summary>
    private const int RoomStride = 0x0100; // arch-ok: 子流盐步长，非玩法数值

    /// <summary>锚点名：玩家出生点（<see cref="BranchDungeonPlanner"/> 写入）。</summary>
    public const string PlayerStartAnchor = "PlayerStart";

    /// <summary>锚点名：Boss 出生点（<see cref="BranchDungeonPlanner"/> 写入）。</summary>
    public const string BossSpawnAnchor = "BossSpawn";

    /// <summary>两倍的 π：角度换算常量（数学常数，不是玩法数值）。</summary>
    private const float Tau = (float)(Math.PI * 2.0); // arch-ok: 2π 三角常量（数学常数，不是玩法数值）

    /// <summary>起点房固定槽位数：1 个 PlayerStart + 1 个 Spawn（结构性常量）。</summary>
    private const int StartSlots = 2; // arch-ok: 起点房固定槽位数（拓扑常量），不是玩法数值

    /// <summary>Boss 房固定槽位数：1 个 BossSpawn（结构性常量）。</summary>
    private const int BossSlots = 1; // arch-ok: Boss 房固定槽位数（拓扑常量），不是玩法数值

    private readonly IRandomProvider _root;
    private readonly RoomContentConfig _config;

    /// <summary>构造。</summary>
    /// <param name="random">随机根流（宿主注入）。</param>
    /// <param name="config">内容参数（数值全部来自这里）。</param>
    public GridContentPlanner(IRandomProvider random, RoomContentConfig config)
    {
        _root = random ?? throw new ArgumentNullException(nameof(random));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();
    }

    /// <inheritdoc />
    public string Name => "GridContent";

    /// <inheritdoc />
    public ContentSlot[] Plan(DungeonLayout layout, int seed, int capacity)
    {
        if (!layout.IsValid)
        {
            throw new InvalidOperationException("GridContentPlanner：传入的地牢图不合法（缺少 Rooms/Links/Doors 或起止房间）。");
        }
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "容量不能为负。");
        }

        int required = CountRequiredSlots(in layout);
        if (required > capacity)
        {
            // 容量不足必须抛：静默截断 = 有些房间永远没怪，而日志里什么都看不见。
            throw new InvalidOperationException(
                "GridContentPlanner：槽位容量不足（需要 " + required.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "，容量 " + capacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "）。请调大宿主配置的槽位容量或调小 RoomContentConfig 的数量。");
        }

        ContentSlot[] result = new ContentSlot[required];
        int n = 0;
        for (int r = 0; r < layout.RoomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            IRandomProvider random = _root.Fork(PlannerSalt)
                .Fork(unchecked(RoomSalt ^ (room.Id.Slot * RoomStride) ^ seed));
            n = FillRoom(in layout, in room, random, result, n);
        }

        // 必须按实际写入数量裁剪：FillRoom 可能少放（摆不下），
        // 直接返回 required 长度的数组会把尾部默认值（Room = R(none)、Kind = Unspecified）当成真槽位发出去。
        if (n == result.Length)
        {
            return result;
        }
        ContentSlot[] trimmed = new ContentSlot[n];
        Array.Copy(result, trimmed, n);
        return trimmed;
    }

    // ─────────────────────────────────────────────────────────────
    // 数量
    // ─────────────────────────────────────────────────────────────

    /// <summary>整个布局需要的槽位总数（容量预检用；与 <see cref="FillRoom"/> 的上界一致）。</summary>
    /// <param name="layout">布局。</param>
    public int CountRequiredSlots(in DungeonLayout layout)
    {
        int total = 0;
        for (int r = 0; r < layout.RoomCount; r++)
        {
            total += MaxSlotsFor(layout.Rooms[r].Category);
        }
        return total;
    }

    /// <summary>某类房间的槽位数**上界**（实际可能少于此值：摆不下的槽位会被放弃）。</summary>
    /// <param name="category">房间类别。</param>
    /// <remarks>
    /// Special / Hidden / Prototype 三类有**保底槽位**：即使随机摆放全部失败，
    /// 也会补一个兜底槽位（Prop / Spawn），所以它们至少各占 1 个格，这里要把这 1 个算进容量，
    /// 否则 <see cref="Plan"/> 的容量校验会与实际写入数量对不上（越界写）。
    /// </remarks>
    public int MaxSlotsFor(RoomCategory category) => category switch
    {
        RoomCategory.Start => StartSlots + _config.StartRoomSpawnCount,
        RoomCategory.Boss => BossSlots + _config.BossRoomSpawnCount,
        RoomCategory.Special => GuaranteeFloor(_config.SpecialRoomSpawnCount + _config.SpecialRoomInteractableCount),
        RoomCategory.Hidden => GuaranteeFloor(_config.HiddenRoomSpawnCount + _config.HiddenRoomRewardCount),
        RoomCategory.Prototype => GuaranteeFloor(_config.CombatRoomSpawnCount + _config.CombatRoomPropCount),
        _ => _config.CombatRoomSpawnCount + _config.CombatRoomPropCount,
    };

    /// <summary>保底槽位占用：配置全为 0 时也要留 1 个格（保底逻辑最多补 1 个）。</summary>
    private static int GuaranteeFloor(int configured) => configured > 0 ? configured : 1;

    // ─────────────────────────────────────────────────────────────
    // 每间房
    // ─────────────────────────────────────────────────────────────

    /// <summary>填一间房的槽位，返回新的写入位置。</summary>
    private int FillRoom(in DungeonLayout layout, in RoomPlan room, IRandomProvider random, ContentSlot[] buffer, int start)
    {
        if (start >= buffer.Length)
        {
            // 缓冲区已满：理论上不会发生（required 是所有房间的上界之和），
            // 但作为兜底必须拦住，否则越界写会污染相邻对象。
            return start;
        }

        // 本房间的门（只读布局，不掷骰）：门避让要用。
        Span<(float X, float Z)> doors = stackalloc (float, float)[MaxDoorsPerRoom];
        int doorCount = CollectDoorPositions(in layout, in room, doors);

        int n = start;
        int index = 0;

        // 1) 固定槽位（锚点）先放：后面随机槽位必须绕开它们。
        switch (room.Category)
        {
            case RoomCategory.Start:
                n = EmitAnchorSlot(in room, PlayerStartAnchor, ContentSlotKind.PlayerStart, RoomDirection.North, buffer, n, ref index);
                n = EmitRandom(in room, random, doors, doorCount, _config.StartRoomSpawnCount, ContentSlotKind.Spawn, buffer, n, ref index);
                break;

            case RoomCategory.Boss:
                n = EmitAnchorSlot(in room, BossSpawnAnchor, ContentSlotKind.BossSpawn, RoomDirection.South, buffer, n, ref index);
                n = EmitRandom(in room, random, doors, doorCount, _config.BossRoomSpawnCount, ContentSlotKind.Spawn, buffer, n, ref index);
                break;

            case RoomCategory.Special:
                n = EmitRandom(in room, random, doors, doorCount, _config.SpecialRoomSpawnCount, ContentSlotKind.Spawn, buffer, n, ref index);
                n = EmitRandomGuaranteed(in room, random, doors, doorCount, _config.SpecialRoomInteractableCount, ContentSlotKind.Prop, ContentSlotKind.Interactable, buffer, n, ref index);
                break;

            case RoomCategory.Hidden:
                n = EmitRandom(in room, random, doors, doorCount, _config.HiddenRoomSpawnCount, ContentSlotKind.Spawn, buffer, n, ref index);
                n = EmitRandomGuaranteed(in room, random, doors, doorCount, _config.HiddenRoomRewardCount, ContentSlotKind.Spawn, ContentSlotKind.Reward, buffer, n, ref index);
                break;

            case RoomCategory.Prototype:
                // 原型房是「直出可刷怪/刷机关」的调试房：保底至少 1 个 Spawn。
                n = EmitRandomGuaranteed(in room, random, doors, doorCount, _config.CombatRoomSpawnCount, ContentSlotKind.Prop, ContentSlotKind.Spawn, buffer, n, ref index);
                n = EmitRandom(in room, random, doors, doorCount, _config.CombatRoomPropCount, ContentSlotKind.Prop, buffer, n, ref index);
                break;

            default: // Combat / Safe / Undecided
                n = EmitRandom(in room, random, doors, doorCount, _config.CombatRoomSpawnCount, ContentSlotKind.Spawn, buffer, n, ref index);
                n = EmitRandom(in room, random, doors, doorCount, _config.CombatRoomPropCount, ContentSlotKind.Prop, buffer, n, ref index);
                break;
        }
        return n;
    }

    /// <summary>一间房最多同时拥有的门数（四向，结构性常量）。</summary>
    private const int MaxDoorsPerRoom = 4; // arch-ok: 四向最大门数（拓扑常量），不是玩法数值

    private static int CollectDoorPositions(in DungeonLayout layout, in RoomPlan room, Span<(float X, float Z)> buffer)
    {
        int n = 0;
        RoomDoor[] doors = layout.Doors;
        if (doors is null)
        {
            return 0;
        }
        for (int i = 0; i < doors.Length && n < buffer.Length; i++)
        {
            if (doors[i].Room != room.Id)
            {
                continue;
            }
            buffer[n] = DungeonGridMath.DoorLocalPosition(in room, doors[i].Direction);
            n++;
        }
        return n;
    }

    /// <summary>从具名锚点取一个槽位；锚点不存在时退化为房间中心。</summary>
    private int EmitAnchorSlot(
        in RoomPlan room, string anchorName, ContentSlotKind kind, RoomDirection fallbackFacing,
        ContentSlot[] buffer, int n, ref int index)
    {
        float x;
        float z;
        RoomDirection facing = fallbackFacing;
        if (room.TryGetAnchor(anchorName, out RoomAnchor anchor))
        {
            // 锚点是 0..1 归一化，ContentSlot 是格：换算只发生在这里（全工程唯一一处）。
            x = DungeonGridMath.AnchorToLocal(anchor.LocalX, room.Footprint.Width);
            z = DungeonGridMath.AnchorToLocal(anchor.LocalZ, room.Footprint.Depth);
            facing = anchor.Facing;
        }
        else
        {
            (float cx, float cz) = DungeonGridMath.CenterLocal(in room);
            x = cx;
            z = cz;
        }
        buffer[n] = new ContentSlot
        {
            Room = room.Id,
            Index = index++,
            Kind = kind,
            LocalX = x,
            LocalZ = z,
            Radius = _config.SlotRadius,
            Facing = facing,
        };
        return n + 1;
    }

    /// <summary>掷若干同类槽位（摆不下的会少放，索引仍然连续）。</summary>
    private int EmitRandom(
        in RoomPlan room, IRandomProvider random, Span<(float X, float Z)> doors, int doorCount,
        int count, ContentSlotKind kind, ContentSlot[] buffer, int n, ref int index)
    {
        for (int i = 0; i < count; i++)
        {
            if (!TryPlace(random, in room, doors, doorCount, buffer, n, out float x, out float z))
            {
                continue;
            }
            buffer[n] = new ContentSlot
            {
                Room = room.Id,
                Index = index++,
                Kind = kind,
                LocalX = x,
                LocalZ = z,
                Radius = _config.SlotRadius,
                Facing = FacingTowardCenter(in room, x, z),
            };
            n++;
        }
        return n;
    }

    /// <summary>
    /// 掷若干同类槽位，但**保底至少 1 个**：第一个槽位会用「放宽约束」的重试预算，
    /// 只有真的一次都摆不下时才放弃（此时房间过小，属于配置问题而非算法退化）。
    /// <para>
    /// 为什么需要保底：特殊房没有交互物、隐藏房没有奖励、原型房没有刷怪点，等于这间房是空的——
    /// 玩法上是「白开一间房」。随机摆放本身是可能连续失败的（门避让 + 槽间距），所以需要这一层。
    /// </para>
    /// </summary>
    private int EmitRandomGuaranteed(
        in RoomPlan room, IRandomProvider random, Span<(float X, float Z)> doors, int doorCount,
        int count, ContentSlotKind fallbackKind, ContentSlotKind kind, ContentSlot[] buffer, int n, ref int index)
    {
        if (count <= 0)
        {
            return n;
        }

        // 第一个：用当前已有槽位做约束，正常重试；失败则用加倍的预算再试一次。
        if (!TryPlace(random, in room, doors, doorCount, buffer, n, out float gx, out float gz))
        {
            if (!TryPlaceRelaxed(random, in room, doors, doorCount, buffer, n, out gx, out gz))
            {
                // 连放宽都摆不下：退化为「主图类别该有的那种内容」，至少不是空房。
                if (fallbackKind != kind)
                {
                    (float fx, float fz) = DungeonGridMath.CenterLocal(in room);
                    buffer[n] = new ContentSlot
                    {
                        Room = room.Id,
                        Index = index++,
                        Kind = fallbackKind,
                        LocalX = fx,
                        LocalZ = fz,
                        Radius = _config.SlotRadius,
                        Facing = FacingTowardCenter(in room, fx, fz),
                    };
                    return n + 1;
                }
                return n;
            }
        }

        buffer[n] = new ContentSlot
        {
            Room = room.Id,
            Index = index++,
            Kind = kind,
            LocalX = gx,
            LocalZ = gz,
            Radius = _config.SlotRadius,
            Facing = FacingTowardCenter(in room, gx, gz),
        };
        n++;

        // 其余按正常规则补满（摆不下就少放）。
        return EmitRandom(in room, random, doors, doorCount, count - 1, kind, buffer, n, ref index);
    }

    /// <summary>
    /// 放宽约束的保底重试：随机失败后，改为**确定性网格扫描**整间房，取第一个合法点。
    /// <para>
    /// 为什么随机不够：房间只有 3×3 格、门又开在墙中点时，合法区域是一个很窄的环——
    /// 60 次「均匀圆盘采样」几乎必然打不中（实测隐藏房因此有 53% 是空的）。
    /// 扫描一遍只有几十个候选点，代价可忽略，但把「几乎必然失败」变成「必然成功」。
    /// </para>
    /// <para>扫描顺序固定（Z 外层、X 内层，步长 <see cref="FallbackScanStep"/>），保证同配置同结果。</para>
    /// </summary>
    private bool TryPlaceRelaxed(
        IRandomProvider random, in RoomPlan room, Span<(float X, float Z)> doors, int doorCount,
        ContentSlot[] placed, int placedCount, out float x, out float z)
    {
        // 1) 先按原规则再随机试若干次（小房间外的情况通常很快命中，结果也更自然）。
        for (int attempt = 0; attempt < _config.PlacementAttempts; attempt++)
        {
            (float cx, float cz) = DungeonGridMath.CenterLocal(in room);
            float angle = random.NextFloat() * Tau;
            float radius = _config.SpawnRadiusMin + random.NextFloat() * (_config.SpawnRadiusMax - _config.SpawnRadiusMin);
            (float px, float pz) = DungeonGridMath.Polar(in room, cx, cz, angle, radius);
            if (!IsPlacementLegal(in room, px, pz, doors, doorCount, placed, placedCount, ignoreSlotPadding: true))
            {
                continue;
            }
            x = px;
            z = pz;
            return true;
        }

        // 2) 随机打不中就整间房确定性扫描（忽略槽间距，墙/门仍然避让）。
        //    扫描范围必须从「离墙安全距离」起算：从 0 开始的采样点全部落在墙里，
        //    白跑一圈还发现不了那些贴着安全线的合法窄带。
        float maxX = room.Footprint.Width - 1;
        float maxZ = room.Footprint.Depth - 1;
        float lo = _config.WallClearance;
        for (float sz = lo; sz <= maxZ - lo; sz += FallbackScanStep)
        {
            for (float sx = lo; sx <= maxX - lo; sx += FallbackScanStep)
            {
                if (!IsPlacementLegal(in room, sx, sz, doors, doorCount, placed, placedCount, ignoreSlotPadding: true))
                {
                    continue;
                }
                x = sx;
                z = sz;
                return true;
            }
        }

        // 3) 精确扫描没命中：再按 1/16 格细扫一遍（3×3 房间也只有约 400 个点）。
        //    只靠粗扫会因为步长跳过那条「贴着离墙安全线的窄带」。
        float fine = FallbackScanStep / FineScanDivisor;
        for (float sz = lo; sz <= maxZ - lo; sz += fine)
        {
            for (float sx = lo; sx <= maxX - lo; sx += fine)
            {
                if (!IsPlacementLegal(in room, sx, sz, doors, doorCount, placed, placedCount, ignoreSlotPadding: true))
                {
                    continue;
                }
                x = sx;
                z = sz;
                return true;
            }
        }

        // 4) 连细扫都没有合法点（房间比门避让区还小）：退回房间中心并报告失败。
        (x, z) = DungeonGridMath.CenterLocal(in room);
        return false;
    }

    /// <summary>保底扫描的步长（格）：1/4 格精度。</summary>
    private const float FallbackScanStep = 0.25f; // arch-ok: 扫描步长（纯搜索精度），不是玩法数值

    /// <summary>细扫步长 = 粗扫步长 / 该值（1/16 格）。</summary>
    private const float FineScanDivisor = 4f; // arch-ok: 扫描精度倍率（纯搜索精度），不是玩法数值

    // ─────────────────────────────────────────────────────────────
    // 摆放约束
    // ─────────────────────────────────────────────────────────────

    /// <summary>掷一个满足全部约束的落点，最多 <see cref="RoomContentConfig.PlacementAttempts"/> 次。</summary>
    private bool TryPlace(
        IRandomProvider random, in RoomPlan room, Span<(float X, float Z)> doors, int doorCount,
        ContentSlot[] placed, int placedCount, out float x, out float z)
    {
        (float cx, float cz) = DungeonGridMath.CenterLocal(in room);
        for (int attempt = 0; attempt < _config.PlacementAttempts; attempt++)
        {
            float angle = random.NextFloat() * Tau;
            float radius = _config.SpawnRadiusMin + random.NextFloat() * (_config.SpawnRadiusMax - _config.SpawnRadiusMin);
            (float px, float pz) = DungeonGridMath.Polar(in room, cx, cz, angle, radius);
            if (!IsPlacementLegal(in room, px, pz, doors, doorCount, placed, placedCount))
            {
                continue;
            }
            x = px;
            z = pz;
            return true;
        }
        x = 0f;
        z = 0f;
        return false;
    }

    /// <summary>
    /// 落点是否满足全部约束（纯判定，测试可直接调用）：
    /// ① 离墙 &gt;= WallClearance；② 离每扇门 &gt;= DoorClearance；
    /// ③ 与已放槽位圆-圆不重叠（半径和 + SlotPadding）。
    /// </summary>
    /// <param name="room">房间。</param>
    /// <param name="x">局部 X（格）。</param>
    /// <param name="z">局部 Z（格）。</param>
    /// <param name="doors">本房间门的位置（格）。</param>
    /// <param name="doorCount">门个数。</param>
    /// <param name="placed">已放槽位。</param>
    /// <param name="placedCount">已放个数。</param>
    /// <param name="ignoreSlotPadding">
    /// true = 忽略「与已放槽位保持间距」这一条（只用于保底槽位：宁可挤一点，也不能让房间空着）。
    /// 墙 / 门 / 房内边界三条硬约束**永远**生效。
    /// </param>
    public bool IsPlacementLegal(
        in RoomPlan room, float x, float z, Span<(float X, float Z)> doors, int doorCount,
        ContentSlot[] placed, int placedCount, bool ignoreSlotPadding = false)
    {
        if (DungeonGridMath.DistanceToWall(in room, x, z) < _config.WallClearance)
        {
            return false;
        }
        for (int i = 0; i < doorCount; i++)
        {
            float sum = _config.SlotRadius + _config.DoorClearance;
            if (DungeonGridMath.CirclesOverlap(x, z, sum, doors[i].X, doors[i].Z, 0f))
            {
                return false;
            }
        }
        if (ignoreSlotPadding)
        {
            return true;
        }
        for (int i = 0; i < placedCount; i++)
        {
            float sum = _config.SlotRadius + placed[i].Radius + _config.SlotPadding;
            if (DungeonGridMath.CirclesOverlap(x, z, sum, placed[i].LocalX, placed[i].LocalZ, 0f))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>槽位朝向：面向房间中心（炮台/道具的默认摆法）；正好在中心时朝南（固定值保证确定性）。</summary>
    private static RoomDirection FacingTowardCenter(in RoomPlan room, float x, float z)
    {
        (float cx, float cz) = DungeonGridMath.CenterLocal(in room);
        GridOffset delta = new GridOffset((int)MathF.Round(cx - x), (int)MathF.Round(cz - z));
        if (delta.X == 0 && delta.Z == 0)
        {
            return RoomDirection.South;
        }
        return DungeonGridMath.DirectionOf(in delta);
    }
}
