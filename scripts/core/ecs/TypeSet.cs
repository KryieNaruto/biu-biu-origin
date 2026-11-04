using System;
using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 有序组件集合 = 原型签名。
/// <para>
/// <b>为什么要有「序」</b>：ECS.md 第 5.3 节要求「创建 Query 时匹配一次类型集合」，
/// 而按集合哈希做等值比较前提是两侧完全同序。这里统一按 <see cref="ComponentType.Id"/> 升序排，
/// <c>TypeSet</c> 的相等/哈希就退化成「逐元素比 int」，不分配、可内联，还能直接当原型字典的键。
/// </para>
/// <para>
/// <b>为什么长度上限是 16</b>：unmanaged 组件平均 32~64 字节，16 个组件已足以覆盖本项目全部官方组件
/// （ECS.md 第 4.2 节共 11 个）。把上限写死成常量，让签名哈希能放进一个 long（每个组件 4 bit），
/// Journal 与原型索引都用同一个 long 当键，热路径不做任何对象分配。
/// </para>
/// <para>
/// <b>为什么是不可变结构</b>：一旦被 <see cref="Archetype"/> 持有，签名就不能变——
/// 变签名 = 换原型，换原型 = 实体搬迁。把「不可变」编码在类型里，可以避免有人偷偷在运行时改签名。
/// </para>
/// </summary>
public readonly struct TypeSet : IEquatable<TypeSet>
{
    /// <summary>一个原型最多能装的组件种类数。</summary>
    public const int MaxComponentCount = 16;

    private readonly long _bits;
    private readonly long _hash;
    private readonly int _count;

    /// <summary>
    /// 升序去重的组件 id 列表（命令缓冲提交时需要它来重建原型）。
    /// <para>
    /// <b>为什么只对「原型签名的所有实例」共享一份数组</b>：TypeSet 是值类型、会被大量拷贝，
    /// 若每个实例各带一份 int[]，拷贝就变成引用拷贝（仍共享，但会让值语义看起来像有状态）。
    /// 实际做法是：<see cref="World.ResolveOrCreateArchetype"/> 建原型时用「已排序的 id 数组」构造，
    /// 该数组生命周期与原型相同且不可变；短命 TypeSet（如命令里的签名）不带 id 数组，
    /// <see cref="Ids"/> 会退化为空 —— 提交路径只用原型上的那份。
    /// </para>
    /// </summary>
    private readonly int[]? _ids;

    /// <summary>按组件 id 构造（内部已排序去重）。<paramref name="ids"/> 必须是升序且无重复。</summary>
    internal TypeSet(ReadOnlySpan<int> sortedIds)
    {
        if (sortedIds.Length > MaxComponentCount)
        {
            throw new GameEcsException(
                string.Concat("组件种类上限为 ", MaxComponentCount.ToString(), "，实际为 ", sortedIds.Length.ToString(), "。"));
        }
        _count = sortedIds.Length;
        long bits = 0;
        for (int i = 0; i < sortedIds.Length; i++)
        {
            bits |= 1L << sortedIds[i];
        }
        _bits = bits;
        // 与 ComputeSignature 用同一套折叠：原型表按 SignatureHash 建键，
        // 两处必须一致，否则「查得到」和「存进去」会分叉（曾导致 {Pos} 与 {Vel} 撞成同一原型）。
        _hash = ComputeSignature(sortedIds);
        _ids = null;
    }

    /// <summary>按组件 id 构造，并持有 id 数组本身（原型签名用：之后提交命令要靠它找回原型）。</summary>
    internal TypeSet(int[] sortedIds)
    {
        if (sortedIds.Length > MaxComponentCount)
        {
            throw new GameEcsException(
                string.Concat("组件种类上限为 ", MaxComponentCount.ToString(), "，实际为 ", sortedIds.Length.ToString(), "。"));
        }
        _count = sortedIds.Length;
        long bits = 0;
        for (int i = 0; i < sortedIds.Length; i++)
        {
            bits |= 1L << sortedIds[i];
        }
        _bits = bits;
        _hash = ComputeSignature(sortedIds);
        _ids = sortedIds;
    }

    /// <summary>升序去重的组件 id（未持有 id 数组的短命 TypeSet 返回空 Span）。</summary>
    internal ReadOnlySpan<int> Ids => _ids is null ? ReadOnlySpan<int>.Empty : _ids.AsSpan(0, _count);

    /// <summary>组件种类数。</summary>
    public int Count => _count;

    /// <summary>按位集合的签名（每个组件 id 占一位），用于快速包含判定。</summary>
    public long Bits => _bits;

    /// <summary>不依赖分配的顺序无关哈希（原型字典 / 回收桶的键）。</summary>
    public long SignatureHash => _hash;

    /// <summary>是否包含某个组件 id。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(int componentId) => (_bits & (1L << componentId)) != 0;

    /// <summary>是否包含另一个集合的全部组件。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsAll(in TypeSet other) => (_bits & other._bits) == other._bits;

    /// <summary>是否与另一个集合没有任何交集（用于 Without 的快速排除）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Overlaps(in TypeSet other) => (_bits & other._bits) != 0;

    /// <summary>两个集合是否相同（组件种类完全一致）。</summary>
    public bool Equals(TypeSet other) => _bits == other._bits;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TypeSet other && _bits == other._bits;

    /// <inheritdoc />
    public override int GetHashCode() => unchecked((int)(_hash ^ (_hash >> 32)));

    public static bool operator ==(TypeSet left, TypeSet right) => left._bits == right._bits;

    public static bool operator !=(TypeSet left, TypeSet right) => left._bits != right._bits;

    /// <summary>把 id 序列折进一个顺序敏感的 long（FNV 变体：乘 31 再加）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Append(long hash, int id) => unchecked((hash << 5) - hash + id);

    /// <summary>
    /// 原型签名的**规范**折叠：组件 id 已升序去重，所以「不同集合」必须得到「不同签名」。
    /// <para>
    /// <b>为什么不能直接用 <see cref="Append"/> 从 0 开始折叠</b>（曾经的真实 bug）：
    /// <c>Append(0, id) = 0*31 + id = id</c>，于是「只有一个组件 id=k」的集合签名恒等于 k；
    /// 而「组件 id 0 与空集」都折出 0。原型表是按签名做键的，一旦签名碰撞，
    /// <c>{Pos}</c> 与 <c>{Vel}</c> 会被误认为同一个原型，实体被塞进错误布局的 chunk —— 数据错乱级故障。
    /// </para>
    /// <para>
    /// 这里改成「先掺入集合长度与固定黄金比例种子，再逐项折叠」，让空集、单元素集、
    /// 多元素集落在互不相干的位置。签名只在建原型/查原型时算一次，不在帧内热路径上。
    /// </para>
    /// </summary>
    internal static long ComputeSignature(ReadOnlySpan<int> sortedIds)
    {
        // 0x9E3779B97F4A7C15 = 2^64 / φ，经典散列种子；先混入长度，杜绝「长度不同但折叠结果相同」。
        long hash = unchecked((long)0x9E3779B97F4A7C15UL) ^ (long)sortedIds.Length;
        for (int i = 0; i < sortedIds.Length; i++)
        {
            // 每步再乘一次黄金比例，避免低 id 的线性可预测性。
            hash = unchecked((hash ^ (uint)sortedIds[i]) * (long)0x9E3779B97F4A7C15UL);
        }
        // 最后再折一次长数，把高位差异混到低位（字典取模只看低位是常见实现）。
        hash ^= hash >> 29;
        return unchecked(hash * (long)0xC2B2AE3D27D4EB4FUL);
    }

    /// <summary>把若干组件 id 排序去重到调用方缓冲；返回实际长度。</summary>
    internal static int Normalize(ReadOnlySpan<int> source, Span<int> destination)
    {
        if (source.Length > destination.Length)
        {
            throw new GameEcsException(
                string.Concat("组件种类上限为 ", destination.Length.ToString(), "，实际为 ", source.Length.ToString(), "。"));
        }
        int count = 0;
        for (int i = 0; i < source.Length; i++)
        {
            int id = source[i];
            int at = 0;
            while (at < count && destination[at] < id)
            {
                at++;
            }
            if (at < count && destination[at] == id)
            {
                continue;
            }
            for (int j = count; j > at; j--)
            {
                destination[j] = destination[j - 1];
            }
            destination[at] = id;
            count++;
        }
        return count;
    }
}
