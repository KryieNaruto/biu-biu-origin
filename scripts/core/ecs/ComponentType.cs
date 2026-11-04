using System;
using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 组件类型描述符：id、大小、以及「怎么建一列 / 怎么把实体号写进它的 [Identity] 字段」。
/// <para>
/// <b>为什么把「池工厂」放在这里</b>：Archetype 按组件组合建 chunk，建 chunk 时要为每一列要一个
/// 「容量为 N 的列」。把工厂做成一次性的委托，Archetype 就完全不认识具体的 T
/// （只拿到 <see cref="IComponentPool"/> 与 <see cref="ComponentTypeInfo"/>），
/// 从而做到「新增一种组件 = 新增 1 个文件，Archetype/World/Query 一行都不用改」（规则7）。
/// </para>
/// <para>
/// <b>为什么 id 是全局非负 int 而不是 Type</b>：热路径比较 int 比比较对象引用快；
/// 且 <see cref="TypeSet"/> 的位集合签名要求 id 落在 0..63。
/// </para>
/// </summary>
public sealed class ComponentType
{
    /// <summary>组件 id 上限（含）：与 <see cref="TypeSet"/> 的 16 个 4-bit 槽对齐。</summary>
    public const int MaxId = TypeSet.MaxComponentCount - 1;

    internal ComponentType(int id, Type type, int sizeOf)
    {
        Id = id;
        Type = type;
        SizeOf = sizeOf;
    }

    /// <summary>组件 id（0..MaxId），同一注册表内唯一且稳定。</summary>
    public int Id { get; }

    /// <summary>CLR 类型（调试与异常信息用；热路径不碰）。</summary>
    public Type Type { get; }

    /// <summary>组件大小（字节）。chunk 内存统计与 C++ 侧偏移契约都用它（ECS.md 第 9 节）。</summary>
    public int SizeOf { get; }

    /// <summary>人类可读名（异常信息用）。</summary>
    public string Name => Type.Name;

    /// <summary>
    /// 持有该组件的原型里，最近一次被销毁实体的世代号。
    /// <para>
    /// 用途：<see cref="World"/> 分配新实体时若发现「候选世代号 == 这个值」，说明和新实体撞号的风险，
    /// 于是换下一个世代号。这就是「世代号判活」能可靠排除旧句柄的根本原因。
    /// </para>
    /// </summary>
    internal int LastDestructionGeneration { get; set; } = EntityIndex.NoneArchetype;

    /// <summary>已为该组件创建的列数量（诊断：可发现「同一组件被多种原型各持一份」的增长是否异常）。</summary>
    internal int AllocatedPoolCount { get; set; }

    /// <inheritdoc />
    public override string ToString() => string.Concat(Type.Name, "#", Id.ToString());
}
