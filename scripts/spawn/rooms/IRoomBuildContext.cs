using System;
using Game.Core;
using Game.Core.Ecs;

namespace Game.Spawn.Rooms;

/// <summary>
/// 生成上下文：<see cref="IRoomContentSpawner"/> 能看到的**全部**外部能力。
/// <para>
/// <b>为什么是显式窄接口而不是把 <c>World</c> 整个传进去</b>：内容生成器只需要三件事
/// （写生成命令、拿本房间专属随机流、把局部格坐标换算成世界坐标）。把 <c>World</c> 传进去，
/// 第一个生成器就会开始 <c>World.Create</c> / <c>World.Destroy</c>（违反 J5），
/// 第二个就会开始读别的房间的实体。窄接口让「不该干的事」在编译期就不成立。
/// </para>
/// <para>
/// <b>为什么随机源按房间传</b>：同 seed 下「A 房间的内容」不应因为 B 房间多刷了一个怪而变化。
/// 每次 <see cref="RandomFor"/> 都返回稳定子流（<c>Fork</c> 语义），房间之间互相隔离。
/// </para>
/// </summary>
public interface IRoomBuildContext
{
    /// <summary>本层的规划结果（只读）。生成器可以据此查相邻房间（例如「门对面放怪」）。</summary>
    DungeonLayout Layout { get; }

    /// <summary>当前正在生成的地牢世代。</summary>
    DungeonGeneration Generation { get; }

    /// <summary>ECSv1 的结构化命令缓冲：**唯一**允许的生成/销毁入口（J5）。</summary>
    CommandBuffer Commands { get; }

    /// <summary>取该房间的稳定随机子流。</summary>
    /// <param name="room">房间句柄。</param>
    IRandomProvider RandomFor(RoomId room);

    /// <summary>取某个子系统的稳定随机子流（例如「装饰摆放」）。</summary>
    /// <param name="salt">子流盐值。</param>
    IRandomProvider RandomFor(int salt);

    /// <summary>把「房间局部格坐标」换算成世界坐标（米）。</summary>
    /// <param name="room">房间。</param>
    /// <param name="localX">局部 X（格）。</param>
    /// <param name="localZ">局部 Z（格）。</param>
    System.Numerics.Vector3 ToWorld(in RoomPlan room, float localX, float localZ);

    /// <summary>把槽位换算成世界坐标（米）。</summary>
    /// <param name="slot">内容槽。</param>
    System.Numerics.Vector3 SlotToWorld(in ContentSlot slot);

    /// <summary>该房间是否已经有内容（防止「同一房间被两个生成器都接走」）。</summary>
    /// <param name="room">房间句柄。</param>
    bool IsPopulated(RoomId room);

    /// <summary>标记房间已生成内容（由内容调度器在调用生成器前后维护）。</summary>
    /// <param name="room">房间句柄。</param>
    void MarkPopulated(RoomId room);
}

/// <summary>
/// 生成上下文的**默认实现**：纯 C#，不依赖引擎，可被测试直接构造。
/// <para>
/// 它只持有「世界命令缓冲 + 布局 + 随机根流」，把两者之间的换算收紧在一处：
/// 格坐标 → 米制只发生在 <see cref="ToWorld"/>，将来改格子边长/房间旋转只需要改这里。
/// </para>
/// </summary>
public sealed class RoomBuildContext : IRoomBuildContext
{
    private readonly IRandomProvider _root;
    private readonly float _cellSize;
    private readonly bool[] _populated;

    /// <summary>构造。</summary>
    /// <param name="layout">本层布局。</param>
    /// <param name="root">本层随机根流（内部按房间/盐值 Fork）。</param>
    /// <param name="commands">世界命令缓冲。</param>
    /// <param name="cellSizeMeters">每格边长（米）。</param>
    public RoomBuildContext(DungeonLayout layout, IRandomProvider root, CommandBuffer commands, float cellSizeMeters)
    {
        Layout = layout;
        _root = root ?? throw new ArgumentNullException(nameof(root));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        if (!(cellSizeMeters > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSizeMeters), "格子边长必须为正。");
        }
        _cellSize = cellSizeMeters;
        _populated = new bool[layout.RoomCount];
    }

    /// <inheritdoc />
    public DungeonLayout Layout { get; }

    /// <inheritdoc />
    public DungeonGeneration Generation => Layout.Generation;

    /// <inheritdoc />
    public CommandBuffer Commands { get; }

    /// <inheritdoc />
    public IRandomProvider RandomFor(RoomId room) => _root.Fork(room.Slot ^ RoomSaltOffset);

    /// <inheritdoc />
    public IRandomProvider RandomFor(int salt) => _root.Fork(salt);

    /// <inheritdoc />
    public System.Numerics.Vector3 ToWorld(in RoomPlan room, float localX, float localZ)
    {
        // 房间的格子原点在 <see cref="RoomPlan.Cell"/>（左上角语义由布局器统一）。局部 (0,0) 即该角。
        return new System.Numerics.Vector3(
            (room.Cell.X + localX) * _cellSize,
            0f,
            (room.Cell.Z + localZ) * _cellSize);
    }

    /// <inheritdoc />
    public System.Numerics.Vector3 SlotToWorld(in ContentSlot slot)
    {
        RoomPlan room = Layout[slot.Room];
        return ToWorld(in room, slot.LocalX, slot.LocalZ);
    }

    /// <inheritdoc />
    public bool IsPopulated(RoomId room)
        => Layout.Contains(room) && room.Slot < _populated.Length && _populated[room.Slot];

    /// <inheritdoc />
    public void MarkPopulated(RoomId room)
    {
        if (Layout.Contains(room) && room.Slot < _populated.Length)
        {
            _populated[room.Slot] = true;
        }
    }

    /// <summary>房间子流盐偏移：保证「房间随机」与「子系统随机」落在不同分支上。</summary>
    private const int RoomSaltOffset = 0x1708; // arch-ok: 随机子流盐常量，非玩法数值
}

/// <summary>
/// 内容生成调度器：按 <see cref="IRoomContentSpawner.CanHandle"/> 把房间分派给生成器，
/// 每个房间**最多被一个生成器接走**（重复接走说明分派规则有歧义，直接抛）。
/// <para>
/// 规则7：「新增一种房间内容 = 实现一个 <see cref="IRoomContentSpawner"/> + 装配期加一行」。
/// </para>
/// </summary>
public sealed class RoomContentDispatcher
{
    private readonly IRoomContentSpawner[] _spawners;

    /// <summary>构造。</summary>
    /// <param name="spawners">按优先级排列的生成器（先注册者优先接走房间）。</param>
    public RoomContentDispatcher(IRoomContentSpawner[] spawners)
    {
        _spawners = spawners ?? Array.Empty<IRoomContentSpawner>();
    }

    /// <summary>最近一次生成的房间数（诊断）。</summary>
    public int LastPopulatedRoomCount { get; private set; }

    /// <summary>最近一次没有生成器认领的房间类别（诊断：通常是配置缺失）。</summary>
    public RoomCategory LastUnclaimed { get; private set; }

    /// <summary>
    /// 为全部房间生成内容。
    /// </summary>
    /// <param name="layout">地牢图。</param>
    /// <param name="slots">内容规划器产出的全部槽位。</param>
    /// <param name="context">生成上下文。</param>
    public void PopulateAll(DungeonLayout layout, ReadOnlySpan<ContentSlot> slots, IRoomBuildContext context)
    {
        int populated = 0;
        LastUnclaimed = RoomCategory.Undecided;
        for (int r = 0; r < layout.RoomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            if (context.IsPopulated(room.Id))
            {
                continue;
            }
            for (int s = 0; s < _spawners.Length; s++)
            {
                if (!_spawners[s].CanHandle(in room))
                {
                    continue;
                }
                _spawners[s].Spawn(in room, SlotsOf(room.Id, slots), context);
                context.MarkPopulated(room.Id);
                populated++;
                break;
            }
            if (!context.IsPopulated(room.Id) && room.Category != RoomCategory.Undecided)
            {
                LastUnclaimed = room.Category;
            }
        }
        LastPopulatedRoomCount = populated;
    }

    /// <summary>取某个房间的全部槽位（保持规划顺序）。</summary>
    /// <param name="room">房间句柄。</param>
    /// <param name="slots">全部槽位。</param>
    public static ContentSlot[] SlotsOf(RoomId room, ReadOnlySpan<ContentSlot> slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Room == room)
            {
                count++;
            }
        }
        if (count == 0)
        {
            return Array.Empty<ContentSlot>();
        }
        ContentSlot[] result = new ContentSlot[count];
        int n = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Room == room)
            {
                result[n++] = slots[i];
            }
        }
        return result;
    }
}
