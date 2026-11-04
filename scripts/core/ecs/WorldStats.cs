namespace Game.Core.Ecs;

/// <summary>
/// 世界运行时统计快照（ECS.md 第 5.6 节）。ARK 第 9 节的性能 HUD 直接读它。
/// <para>
/// <b>为什么是 readonly struct 而不是 class</b>：HUD 每帧要读，返回 class 会每帧分配一次；
/// 值快照由 <see cref="World.Stats"/> 在栈上构造，读者按字段取用，零分配。
/// </para>
/// <para>
/// <b>为什么内存是「字节数」而不是「实体数」</b>：ECS 的性能问题几乎都先表现为 cache 压力与
/// 内存带宽，而不是实体数量；同样 1000 个实体可能因为组件大小差 10 倍内存流量。
/// </para>
/// </summary>
public readonly struct WorldStats
{
    /// <summary>活实体数。</summary>
    public int EntityCount { get; init; }

    /// <summary>当前存活的原型（组件组合）数量。</summary>
    public int ArchetypeCount { get; init; }

    /// <summary>当前存活的原型里的 chunk 总数。</summary>
    public int ChunkCount { get; init; }

    /// <summary>组件数据占用的内存字节数（chunk 组件列，不含托管对象开销）。</summary>
    public long ComponentMemoryBytes { get; init; }

    /// <summary>实体槽位索引占用的内存字节数（<see cref="EntityRecord"/> 数组）。</summary>
    public long EntityIndexBytes { get; init; }

    /// <summary>实体索引里被复用的实体号累计命中次数（回收是否在起作用）。</summary>
    public int RecycledEntityHits { get; init; }

    /// <summary>世界版本号（结构化变更计数）。</summary>
    public int Version { get; init; }

    /// <summary>当前是否在为 CommandBuffer 提交（提交期间为 true）。</summary>
    public bool IsFlushingCommands { get; init; }

    /// <summary>调试/Log 用的一行摘要（只在人类可读的场景调用）。</summary>
    public override string ToString()
    {
        return string.Concat(
            "entities=", EntityCount.ToString(),
            " archetypes=", ArchetypeCount.ToString(),
            " chunks=", ChunkCount.ToString(),
            " componentBytes=", ComponentMemoryBytes.ToString(),
            " indexBytes=", EntityIndexBytes.ToString(),
            " recycled=", RecycledEntityHits.ToString(),
            " version=", Version.ToString());
    }
}
