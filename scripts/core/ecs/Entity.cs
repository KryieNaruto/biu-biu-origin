using System;
using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 实体身份键：Index 是槽位号，Generation 防止「已销毁实体被旧句柄寻址」。
/// <para>
/// 为什么不是 class / object 句柄（J1）：句柄必须是 8 字节的值类型——它要能塞进组件、能在 Span 里
/// 连续存储、能跨线程传递而不共享堆对象。任何「实体对象」的写法都会让 chunk 变成引用图，
/// 从而丢掉 SoA 的全部收益。
/// </para>
/// <para>
/// 为什么需要 generation：槽位会被复用（销毁 -> 新建），若只有 index，一个存了两帧的 Entity
/// 会「变成」另一个实体且不报错。加上 generation 后，用 World.IsAlive 复核就能识别出「旧句柄」，
/// 把静默错误变成可判定失败。
/// </para>
/// <para>
/// 为什么是普通 struct 而不是 record struct：record struct 的 GetHashCode / Equals 会走
/// EqualityComparer&lt;T&gt;.Default 与编译器生成的辅助代码，而 Entity 是本项目最热的键
/// （存在 chunk 自带的 EntityIndex 里，也做字典键），自带实现保证零反射、可内联、
/// 且 operator == 的语义最直观。一个类型同时提供 Equals 与 operator == 时要显式声明后者
/// （编译器对此有提示），这里两者都手写。
/// </para>
/// <para>
/// 交叉引用：ActorHandle(Entity) 由 task-1（ecs-core）落在 scripts/core/Handles/。
/// 本文件只定义 Entity，免得两处同名类型打架。
/// </para>
/// </summary>
public readonly struct Entity : IEquatable<Entity>
{
    private readonly int _index;
    private readonly int _generation;

    /// <summary>创建一个实体键。业务侧不直接调用，只通过 World.Create / CommandBuffer 拿实体。</summary>
    public Entity(int index, int generation)
    {
        _index = index;
        _generation = generation;
    }

    /// <summary>
    /// 「空实体」：<c>Index = -1</c>、<c>Generation = 0</c>。
    /// <para>
    /// <b>与 <c>default(Entity)</c> 的区别</b>：<c>default(Entity)</c> 是 <c>(0, 0)</c>，而槽位 0 是第一个
    /// 真实实体，所以 <c>default</c> <u>不是</u> None。内核的约定是：<b>表示「无实体」只能用
    /// <see cref="None"/>（或它的 <c>(index &lt; 0)</c> 判据），不要用 <c>default</c></b>。
    /// <c>default</c> 的安全性由 <see cref="World.IsAlive"/> 的 <c>generation &lt;= 0</c> 拦截兜底。
    /// </para>
    /// </summary>
    public static Entity None => new Entity(EntityIndex.NoneIndex, World.InitialGeneration);

    /// <summary>槽位号（大于等于 0 才可能是活实体）。</summary>
    public int Index => _index;

    /// <summary>世代号：同一槽位复用时它加一，用来识别旧句柄。</summary>
    public int Generation => _generation;

    /// <summary>
    /// 是否是占位空值。
    /// <para>
    /// <b>为什么判据是 <c>_index &lt; 0</c></b>：<see cref="None"/> 的 index 是 -1；而 <c>default(Entity)</c>
    /// 是 <c>(0, 0)</c>，<u>不能</u>并入 None——槽位 0 是合法实体，把 index 0 一律当空会让第一个实体永久不可用。
    /// </para>
    /// <para>
    /// <b>那 <c>default(Entity)</c> 怎么办</b>：活实体的 generation 恒 ≥ 1（<see cref="World.InitialGeneration"/>
    /// 是发号起点 0，第一个实体拿到 1），所以 <c>default</c> 的 <c>(0, 0)</c> 在
    /// <see cref="World.IsAlive"/> 里天然判死；World 侧另有 <c>generation &lt;= 0</c> 的显式拦截作为兜底。
    /// 结论：**表示「无实体」请用 <see cref="None"/>，不要用 <c>default</c>**（后台数组零初始化得到的就是 default）。
    /// </para>
    /// </summary>
    public bool IsNone => _index < 0;

    /// <summary>值相等：index 与 generation 都相同。</summary>
    public bool Equals(Entity other) => _index == other._index && _generation == other._generation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => unchecked((_index * EntityIndex.HashPrime) ^ _generation);

    public static bool operator ==(Entity left, Entity right) => left._index == right._index && left._generation == right._generation;

    public static bool operator !=(Entity left, Entity right) => !(left == right);

    /// <summary>调试用短串（E12:3）。只允许在日志/异常里调用：每次都会分配字符串。</summary>
    public override string ToString()
    {
        return string.Concat("E", _index.ToString(), ":", _generation.ToString());
    }
}

/// <summary>Entity 的只读辅助：给「拿到 World 之后还要复核句柄」的场景提供一句话写法。</summary>
public static class EntityEquality
{
    /// <summary>句柄复核：槽位存在且世代号一致，才认为这个实体键还指着当初那个对象。</summary>
    public static bool Exists(Entity entity, World world) => world is not null && world.IsAlive(entity);
}
