namespace Game.Spawn.Rooms;

/// <summary>
/// 房间句柄：<c>{世代, 槽位}</c>，不是对象引用。**房间不是实体**，是「地牢图上的一个节点」。
/// <para>
/// <b>为什么房间不复用 <c>Entity</c></b>：房间的生命周期是「一层地牢」级别（生成一次、整层不变），
/// 而实体的生命周期是帧级别的。把它们混进 <c>World</c> 会让 <c>Query</c> 多出几十个不参与
/// 每帧遍历的原型（J6 的查询形状会变脏）。房间图是**一张不可变的规划产物**，
/// 由 <see cref="DungeonLayout"/> 持有；房间里的可破坏物、敌人、道具仍然走 ECS 实体。
/// </para>
/// <para>
/// <b>为什么带世代号</b>：同一局里换层/重开会重建图，旧句柄必须<u>清楚地</u>失效而不是
/// 悄悄指到新一层的同名槽位——后者是最难查的一类 bug（「上一层的 Boss 门被这一层的商店当成自己的门」）。
/// </para>
/// </summary>
public readonly struct RoomId : System.IEquatable<RoomId>
{
    /// <summary>无效句柄。</summary>
    public static readonly RoomId None = new(DungeonGeneration.None, -1);

    /// <summary>所属地牢的世代。</summary>
    public DungeonGeneration Generation { get; }

    /// <summary>在该世代图里的槽位号（0 起）。</summary>
    public int Slot { get; }

    /// <summary>构造。</summary>
    public RoomId(DungeonGeneration generation, int slot)
    {
        Generation = generation;
        Slot = slot;
    }

    /// <summary>是否有效。</summary>
    public bool IsValid => Slot >= 0 && Generation.IsValid;

    /// <inheritdoc />
    public bool Equals(RoomId other) => Slot == other.Slot && Generation == other.Generation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RoomId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (Generation.Value * 397) ^ Slot;

    /// <summary>相等运算符。</summary>
    public static bool operator ==(RoomId a, RoomId b) => a.Equals(b);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(RoomId a, RoomId b) => !a.Equals(b);

    /// <inheritdoc />
    public override string ToString()
        => IsValid ? Generation.ToString() + ":R" + Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "R(none)";
}

/// <summary>门句柄：<c>{世代, 槽位}</c>，与 <see cref="RoomId"/> 同一套失效语义。</summary>
public readonly struct DoorId : System.IEquatable<DoorId>
{
    /// <summary>无效句柄。</summary>
    public static readonly DoorId None = new(DungeonGeneration.None, -1);

    /// <summary>所属地牢的世代。</summary>
    public DungeonGeneration Generation { get; }

    /// <summary>门槽位。</summary>
    public int Slot { get; }

    /// <summary>构造。</summary>
    public DoorId(DungeonGeneration generation, int slot)
    {
        Generation = generation;
        Slot = slot;
    }

    /// <summary>是否有效。</summary>
    public bool IsValid => Slot >= 0 && Generation.IsValid;

    /// <inheritdoc />
    public bool Equals(DoorId other) => Slot == other.Slot && Generation == other.Generation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DoorId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (Generation.Value * 31) ^ Slot;

    /// <summary>相等运算符。</summary>
    public static bool operator ==(DoorId a, DoorId b) => a.Equals(b);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(DoorId a, DoorId b) => !a.Equals(b);

    /// <inheritdoc />
    public override string ToString()
        => IsValid ? Generation.ToString() + ":D" + Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "D(none)";
}

/// <summary>
/// 门（= 房间之间的一条通道）。**无向**：<see cref="FromRoom"/>/<see cref="ToRoom"/> 只是写入顺序，
/// 通行方向由 <see cref="IsTwoWay"/> 与 <see cref="Kind"/> 决定。
/// <para>
/// 一条链路上的两个门是**两个 <see cref="DoorId"/>**（各自属于各自的房间），但它们共享
/// <see cref="LinkSlot"/>（指向 <see cref="DungeonLayout.Links"/> 里的那条链路）——
/// 这样「A 的门开着」与「B 的门开着」不会分叉，开门系统只需更新链路状态。
/// </para>
/// </summary>
public readonly struct RoomDoor
{
    /// <summary>门自身句柄。</summary>
    public DoorId Id { get; init; }

    /// <summary>门所在的房间。</summary>
    public RoomId Room { get; init; }

    /// <summary>门在房间内的朝向（朝向哪个方向的那面墙）。</summary>
    public RoomDirection Direction { get; init; }

    /// <summary>门通向的房间（<see cref="DoorKind.Secret"/> 的目标可能就是隐藏房）。</summary>
    public RoomId Target { get; init; }

    /// <summary>所属链路槽位（<see cref="DungeonLayout.Links"/> 的下标）。</summary>
    public int LinkSlot { get; init; }

    /// <summary>通道性质。</summary>
    public DoorKind Kind { get; init; }

    /// <summary>是否双向（<see cref="DoorKind.OneWay"/> 为 false）。</summary>
    public bool IsTwoWay => Kind != DoorKind.OneWay;

    /// <summary>是否藏起来（不能在地图上直接看到）。</summary>
    public bool IsSecret => Kind == DoorKind.Secret;

    /// <summary>是否默认开放（安全房之间的门直接开放，避免「开局被自己房间锁住」）。</summary>
    public bool IsOpenByDefault => Kind == DoorKind.Standard && !IsSecret;

    /// <inheritdoc />
    public override string ToString() => "Door " + Id + " " + Room + "->" + Target + " " + Direction + "/" + Kind;
}

/// <summary>
/// 链路（房间图的一条边）：这是「拓扑」的唯一真源，门只是它的**表现**。
/// <para>
/// 为什么拓扑边与门分开存：拓扑算法（出生树、关键路径、连通性校验）只关心「谁连谁」，
/// 门关心「哪面墙、什么类型」。合成一个结构会让算法被迫处理门的细节（J3 的反面：
/// 系统里出现与职责无关的分支）。分开后：① 拓扑算法纯整数图论，可在测试里裸跑；
/// ② 新增一种门（滑门/桥）只改门表，拓扑零改动。
/// </para>
/// </summary>
public readonly struct RoomLink
{
    /// <summary>链路槽位（= 在 <see cref="DungeonLayout.Links"/> 中的下标）。</summary>
    public int Slot { get; init; }

    /// <summary>端点 A。</summary>
    public RoomId A { get; init; }

    /// <summary>端点 B。</summary>
    public RoomId B { get; init; }

    /// <summary>A → B 的朝向。</summary>
    public RoomDirection DirectionAB { get; init; }

    /// <summary>是否属于「主出生树」（不含隐藏房的秘密通道）。</summary>
    public bool OnMainGraph { get; init; }

    /// <summary>是否为关键路径的一部分（起点→Boss 的最短链）。</summary>
    public bool OnCriticalPath { get; init; }

    /// <summary>是否为隐藏房的秘密通道。</summary>
    public bool IsSecret { get; init; }

    /// <summary>另一端的房间句柄。</summary>
    /// <param name="from">已知的一端。</param>
    public RoomId Other(RoomId from) => from == A ? B : A;

    /// <inheritdoc />
    public override string ToString()
        => "Link" + Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + A + "<->" + B;
}

/// <summary>
/// 房间锚点：房间内的具名相对位置（0..1 的归一化坐标，房间模板自己解释）。
/// <para>
/// <b>为什么要具名锚点</b>：内容填充（刷怪点、道具位、Boss 位）若靠「房间中心 + 随机偏移」，
/// 每个内容生成器都要知道房间尺寸；一旦房间形状从正方形变成 L 形，所有生成器都要改。
/// 锚点把「这块地方能放什么」的语义收进房间模板（数据），生成器只说「给我 3 个生成点」。
/// </para>
/// </summary>
public readonly struct RoomAnchor
{
    /// <summary>锚点名（来自房间模板数据：<c>SpawnL</c>/<c>SpawnR</c>/<c>Center</c>/<c>Exit</c>…）。</summary>
    public string Name { get; init; }

    /// <summary>房间局部 X（0 = 西墙，1 = 东墙）。</summary>
    public float LocalX { get; init; }

    /// <summary>房间局部 Z（0 = 北墙，1 = 南墙）。</summary>
    public float LocalZ { get; init; }

    /// <summary>朝向（道具/敌人出生朝向）。</summary>
    public RoomDirection Facing { get; init; }

    /// <inheritdoc />
    public override string ToString() => "Anchor(" + Name + "," + LocalX.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + "," + LocalZ.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")";
}
