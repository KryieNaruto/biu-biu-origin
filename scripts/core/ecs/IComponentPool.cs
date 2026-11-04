using System;
using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 组件存储池的<u>类型擦除</u>视图：Archetype 只通过它做「建列 / 逐格搬 / swap-remove / 统计」，
/// 完全不认识具体的组件类型 T。
/// <para>
/// <b>为什么必须类型擦除</b>：原型的组件种类是运行时才知道的（第一个实体挂着什么组件就建什么原型）。
/// 若 Archetype 泛型化，World 与 Query 也得跟着泛型化，最后是「泛型地狱」；
/// 而按 <c>object</c> 存列，热路径每次都要拆箱。用接口接管<u>低频</u>的列操作，
/// 热路径只拿 <see cref="ComponentPool{T}"/> 的 Span，一次虚调用都没有。
/// </para>
/// <para>
/// <b>为什么用 <see cref="Memory{T}"/> 而不是 T[]</b>：Memory 可以指向托管数组或原生内存
/// （ECS.md 第 9 节将来 C++ 侧可能直接持有同一块内存），且不额外分配；
/// 而 Span 是栈上类型，不能当字段。
/// </para>
/// </summary>
public interface IComponentPool
{
    /// <summary>组件类型描述符（id / 大小 / 身份写入器）。</summary>
    ComponentType ComponentTypeData { get; }

    /// <summary>本列容量（= 所属 chunk 的容量）。</summary>
    int Capacity { get; }

    /// <summary>本列占用的字节数（chunk 内存统计用）。</summary>
    long ByteSize { get; }

    /// <summary>把 destination 格删除并用 last 格填充（swap-remove，O(1)）。</summary>
    void SwapRemove(int destination, int last);

    /// <summary>把实体号写进本列第 slot 格的 [Identity] 字段。</summary>
    void WriteIdentityAt(int slot, Entity entity);
}

/// <summary>
/// 组件列的「逐格拷贝」能力。抽成独立接口是因为拷贝要知道 T 才能写
/// <c>target[slot] = source[slot]</c>，而 Archetype 不知道 T（类型擦除）。
/// <see cref="ComponentPool{T}"/> 是它的唯一实现，因此这个接口<u>不会带来热路径虚调用</u>：
/// 只有「原型迁移」这条低频路径才走它。
/// </summary>
internal interface ICopySource
{
    /// <summary>把本列 <paramref name="sourceSlot"/> 的值拷进 <paramref name="target"/> 的 <paramref name="targetSlot"/>。</summary>
    void CopyTo(int sourceSlot, IComponentPool target, int targetSlot);
}

/// <summary>
/// <typeparamref name="T"/> 的组件列：一块连续内存。零 GC、可直接 Span 化。
/// <para>
/// <b>为什么是 <c>where T : unmanaged</c></b>：这是 ECS.md J2 的编译期落地——组件不得持有引用，
/// GC 永远不用扫 chunk 内部；也让「整块 memcpy 迁移」合法（没有引用需要修）。
/// </para>
/// <para>
/// <b>为什么一次分配、永不扩容</b>：容量在 chunk 创建时确定（默认 128），满了就新开 chunk。
/// 「池自己扩容」会让迭代回调里正拿着的 Span 失效，那是难查的数据错误。
/// </para>
/// </summary>
public sealed class ComponentPool<T> : IComponentPool, ICopySource where T : unmanaged
{
    private readonly T[] _items;

    /// <summary>创建容量为 <paramref name="capacity"/> 的列（由 Archetype 建 chunk 时调用）。</summary>
    public ComponentPool(int capacity)
    {
        if (capacity < 1)
        {
            throw new GameEcsException(string.Concat("组件列容量必须大于 0，实际为 ", capacity.ToString(), "。"));
        }
        _items = new T[capacity];
    }

    /// <inheritdoc />
    public ComponentType ComponentTypeData => ComponentMeta<T>.Type
        ?? throw new GameEcsException(
            string.Concat("组件 ", typeof(T).Name, " 尚未在任何注册表里注册。请先调用 World.Register<T>() 或 World.Create<T>()。"));

    /// <inheritdoc />
    public int Capacity => _items.Length;

    /// <inheritdoc />
    public long ByteSize => (long)_items.Length * SizeOf.Of<T>();

    /// <summary>整列的 Span 视图（长度 = Capacity；有效数据是前 count 格）。</summary>
    public Span<T> Span => _items;

    /// <summary>取第 slot 格的引用（ref 直写组件字段的最快路径）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T SlotRef(int slot) => ref _items[slot];

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SwapRemove(int destination, int last)
    {
        if (destination != last)
        {
            _items[destination] = _items[last];
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteIdentityAt(int slot, Entity entity)
    {
        ref T first = ref _items[slot];
        // 安全前提：注册期已断言「[Identity] 字段偏移 = 0 且类型 = Entity」，
        // 所以组件首格重解释为 Entity 就是那个字段。这是一次内存写，无反射、无装箱。
        System.Runtime.InteropServices.MemoryMarshal.Cast<T, Entity>(
            System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref first, 1))[0] = entity;
    }

    /// <summary>批量写入实体号（新 chunk 建立时调用一次）。</summary>
    internal void FillIdentityRange(int count, Entity entity)
    {
        for (int i = 0; i < count; i++)
        {
            WriteIdentityAt(i, entity);
        }
    }

    /// <inheritdoc />
    public void CopyTo(int sourceSlot, IComponentPool target, int targetSlot)
    {
        if (target is ComponentPool<T> typed)
        {
            typed._items[targetSlot] = _items[sourceSlot];
            return;
        }
        throw new GameEcsException(
            string.Concat("组件列类型不匹配：期望 ComponentPool<", typeof(T).Name, ">，实际是 ", target.GetType().Name, "。"));
    }
}

/// <summary>
/// 每个组件类型一份的静态元数据代理。静态泛型类<u>由类型参数决定实例</u>：
/// 每个 T 只有一份，读取就是一次静态字段访问（可内联），不需要查注册表字典。
/// <para>
/// 为什么需要它：热路径（批量生成、迁移写身份）不该走 <c>Dictionary&lt;Type, ComponentType&gt;</c>。
/// 注册时把 <see cref="Type"/> 填进去，之后只读。
/// </para>
/// <para>
/// <b>⚠ 为什么缓存必须记住「属主注册表」</b>：组件 id 是<u>每个 <see cref="ComponentTypeRegistry"/>
/// 各自从 0 开始发</u>的（世界可独立创建多个：重开一局、多场景并行、并行测试）。若只缓存
/// <see cref="Type"/>，第二个世界会拿着第一个世界的 id 去查自己的 <c>_byId</c>，得到「组件 id N 未注册」。
/// 因此缓存是 <c>(Owner, Type)</c> 二元组，命中条件是 <c>Owner == 当前注册表</c>；换世界时自动失效重填。
/// 代价是一次引用比较（<c>O(1)</c>、零分配），换来「多世界安全」。
/// </para>
/// </summary>
internal static class ComponentMeta<T> where T : unmanaged
{
    /// <summary>缓存属于哪个注册表（null = 尚未填过）。</summary>
    public static ComponentTypeRegistry? Owner = null;

    /// <summary>
    /// 注册时写入。<b>未注册（或属于另一个注册表）时为 null</b>，此时必须走
    /// <see cref="ComponentTypeRegistry.Register{T}"/> 重新注册并覆写本缓存。
    /// </summary>
    public static ComponentType? Type = null;
}

/// <summary>
/// 组件字典里存的「已注册组件的元数据包」：显式组件类型 + 可直接调用的强类型列工厂。
/// <para>
/// 为什么用密封类而不是直接存 <see cref="ComponentType"/>：一个 sealed class 实例刚好容得下
/// 泛型工厂委托的强类型版本（<c>Func&lt;int, ComponentPool&lt;T&gt;&gt;</c>），
/// Archetype 建 chunk 时不必再走 <c>MakeGenericType</c> + 反射构造。
/// </para>
/// </summary>
public abstract class ComponentTypeInfo
{
    /// <summary>未注册占位（任何使用都会抛异常，用于「先查后报错」而不是 NullReferenceException）。</summary>
    public static readonly ComponentTypeInfo Unregistered = new UnregisteredInfo();

    /// <summary>组件类型描述符。</summary>
    public abstract ComponentType Data { get; }

    /// <summary>创建一列（容量由 chunk 决定）。</summary>
    internal abstract IComponentPool CreatePool(int capacity);

    /// <summary>把实体号写进列的第 slot 格。</summary>
    internal abstract void WriteIdentity(IComponentPool pool, int slot, Entity entity);

    private sealed class UnregisteredInfo : ComponentTypeInfo
    {
        public override ComponentType Data =>
            throw new GameEcsException("组件尚未注册：请先调用 ComponentTypeRegistry.Register<T>() 或通过 World.Create<T>() 触发注册。");

        internal override IComponentPool CreatePool(int capacity) =>
            throw new GameEcsException("组件尚未注册：无法创建组件列。");

        internal override void WriteIdentity(IComponentPool pool, int slot, Entity entity) =>
            throw new GameEcsException("组件尚未注册：无法写入 [Identity] 字段。");
    }
}

/// <summary><typeparamref name="T"/> 的元数据包实现（由注册表在注册期 new 一次）。</summary>
internal sealed class ComponentTypeInfo<T> : ComponentTypeInfo where T : unmanaged
{
    private readonly ComponentType _data;

    public ComponentTypeInfo(ComponentType data)
    {
        _data = data;
    }

    public override ComponentType Data => _data;

    internal override IComponentPool CreatePool(int capacity) => new ComponentPool<T>(capacity);

    internal override void WriteIdentity(IComponentPool pool, int slot, Entity entity)
    {
        if (pool is ComponentPool<T> typed)
        {
            typed.WriteIdentityAt(slot, entity);
            return;
        }
        throw new GameEcsException(
            string.Concat("组件列类型不匹配：期望 ComponentPool<", typeof(T).Name, ">，实际是 ", pool.GetType().Name, "。"));
    }
}
