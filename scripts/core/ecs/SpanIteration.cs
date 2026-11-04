using System;

namespace Game.Core.Ecs;

/// <summary>
/// 遍历回调的委托定义集合（ECS.md 第 5.3 / 5.6 节）。
/// <para>
/// <b>为什么是「每 chunk 一次」而不是「每实体一次」</b>（J6）：
/// 每实体一次回调意味着每个实体一次委托调用，200 个抛射物就是 200 次；更糟的是每实体回调
/// 通常被迫捕获状态，从而<u>每次调用的委托实例都不同</u>（分配）。改成「一次给一整块 Span」，
/// 回调次数从 O(实体) 降到 O(实体/128)，且系统作者可以在块内手写 <c>for</c> 做向量化友好的运算。
/// </para>
/// <para>
/// <b>为什么这些委托本身要缓存成字段而不是每次 new</b>：方法组转换成委托<u>如果捕获了 this</u>，
/// 编译器在部分情况下会每次分配闭包。本项目的写法纪律是：系统的回调以<u>无捕获</u>的实例方法
/// （<c>Run</c>）存在，或者只在构造/注册期把 <c>this</c> 方法组缓存成字段一次。
/// 具体见 <see cref="Query"/> 的注释与 tests 的零分配断言。
/// </para>
/// <para>
/// <b>为什么只到 T3</b>：ECS.md 第 5.3 节定下 T0..T3。四个组件的查询已覆盖本项目全部热路径
/// （Transform3+Velocity / Projectile+Health / Lifetime+Velocity 等）。需要更多时按 ECS.md
/// 第 12 节待办 1 用 Source Generator 统一生成，而不是手写第 5 份几乎一样的代码。
/// </para>
/// </summary>
public delegate void SpanAction<T0>(Span<T0> items) where T0 : unmanaged;

/// <summary>两个组件的 chunk 回调（<paramref name="a"/> 与 <paramref name="b"/> 等长）。</summary>
public delegate void SpanAction<T0, T1>(Span<T0> a, Span<T1> b) where T0 : unmanaged where T1 : unmanaged;

/// <summary>三个组件的 chunk 回调（三个 Span 等长）。</summary>
public delegate void SpanAction<T0, T1, T2>(Span<T0> a, Span<T1> b, Span<T2> c)
    where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged;

/// <summary>四个组件的 chunk 回调（四个 Span 等长）。</summary>
public delegate void SpanAction<T0, T1, T2, T3>(Span<T0> a, Span<T1> b, Span<T2> c, Span<T3> d)
    where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged;

// ── 实例方法版本（零委托分配的正路） ──
//
// 为什么不直接用上面的 SpanAction<T...> 委托：一个「无捕获实例方法组」转委托时，C# 编译器
// 会为每个 (方法, 实例) 组合缓存一份委托（等价缓存），所以 <c>query.ForEach(OnTick)</c> 在
// 同一个系统实例上确实不分配。但跨实例（每次 new 一个系统）就会各缓存一份，且 JIT 无法把它
// 内联成直接调用。下面这组「回调接口」把「方法体」升成一个类型：调用是 <c>in</c> 约束的接口
// 调用，能被去虚化（struct 实现）/ 正内联（sealed class 实现的单态调用点），零分配且可向量化。
// SpanAction 系列保留给「临时 lambda / 测试 / 罕见路径」，接口系列用于系统热路径。

/// <summary>组件查询的 1 元回调（按 chunk 调用，零委托）。</summary>
public interface ISpanAction<T0> where T0 : unmanaged
{
    /// <summary>处理一个 chunk：<paramref name="a"/> 长度 = chunk 容量，有效区是前 <paramref name="count"/> 格。</summary>
    void Execute(Span<T0> a, int count);
}

/// <summary>组件查询的 2 元回调。</summary>
public interface ISpanAction<T0, T1> where T0 : unmanaged where T1 : unmanaged
{
    /// <summary>处理一个 chunk（两个 Span 等长）。</summary>
    void Execute(Span<T0> a, Span<T1> b, int count);
}

/// <summary>组件查询的 3 元回调。</summary>
public interface ISpanAction<T0, T1, T2> where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged
{
    /// <summary>处理一个 chunk（三个 Span 等长）。</summary>
    void Execute(Span<T0> a, Span<T1> b, Span<T2> c, int count);
}

/// <summary>组件查询的 4 元回调。</summary>
public interface ISpanAction<T0, T1, T2, T3>
    where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    /// <summary>处理一个 chunk（四个 Span 等长）。</summary>
    void Execute(Span<T0> a, Span<T1> b, Span<T2> c, Span<T3> d, int count);
}


/// <summary>
/// 单个 chunk 的只读视图：把「组件列 + 实体数组」放进一个值类型里，适合需要「整块批处理 /
/// 统计 / 交给 C++ 批量求解器（ECS.md 第 9 节）」的场景。
/// <para>
/// <b>为什么是 ref struct</b>：它必须能安全地持有 <see cref="Span{T}"/>。
/// 代价是它只能在同步调用内使用（不能存字段、不能跨 await），这正是我们想要的：
/// Span/Chunk 的生命周期严格限定在回调内。
/// </para>
/// </summary>
public readonly ref struct Chunk<T0, T1> where T0 : unmanaged where T1 : unmanaged
{
    /// <summary>构造一个 chunk 视图（由 Query 内部使用）。</summary>
    public Chunk(Span<T0> a, Span<T1> b, ReadOnlySpan<Entity> entities, int count, int archetypeId)
    {
        A = a;
        B = b;
        Entities = entities;
        Count = count;
        ArchetypeId = archetypeId;
    }

    /// <summary>第一个组件的列（长度 = Capacity，有效区是前 <see cref="Count"/> 格）。</summary>
    public Span<T0> A { get; }

    /// <summary>第二个组件的列。</summary>
    public Span<T1> B { get; }

    /// <summary>本 chunk 的实体号数组（有效区前 <see cref="Count"/> 格）。</summary>
    public ReadOnlySpan<Entity> Entities { get; }

    /// <summary>本 chunk 有效实体数。</summary>
    public int Count { get; }

    /// <summary>所属原型 id（诊断/调试用）。</summary>
    public int ArchetypeId { get; }
}

/// <summary>
/// 两个组件的 chunk 回调。<paramref name="count"/> 单独传是为了让回调能「只处理有效区」，
/// 同时保持 <see cref="Chunk{T0,T1}.A"/> 的长度 = Capacity（将来 C++ 侧要按固定 stride 读，ECS.md 第 9 节）。
/// </summary>
public delegate void ChunkAction<T0, T1>(Chunk<T0, T1> chunk, int count)
    where T0 : unmanaged where T1 : unmanaged;
