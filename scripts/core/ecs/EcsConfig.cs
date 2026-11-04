using System;

namespace Game.Core.Ecs;

/// <summary>
/// 内核结构性常量的唯一集中地。
/// <para>
/// <b>为什么要有这个类</b>：规则4 禁止代码里出现游戏数值，但容器容量 / 位宽 / 世代上限这类
/// 「结构性常量」必须写死。把它们集中到一处 + 具名，既满足 GS005 的意图（不允许裸字面量），
/// 也让「chunk 容量」这种将来可能调优（ECS.md 第 9 节路线 D）的数只有一个改动点。
/// </para>
/// <para>
/// <b>这些不是玩法数值</b>：没有任何一个会影响手感、伤害、速度；改动它们只影响内存布局与性能。
/// </para>
/// </summary>
public static class EcsConfig
{
    /// <summary>
    /// 每个 chunk 的默认实体容量（ECS.md 第 5.3 节的默认值）。
    /// <para>
    /// 为什么是 128：一个实体 4~8 个组件、每个组件 8~40 字节时，128 个实体的一整行
    /// 约 10~20KB，能较好地落在二级缓存内；同时 128 是 2 的幂，便于按位分片（Job 分块）。
    /// </para>
    /// </summary>
    public const int DefaultChunkCapacity = 128;

    /// <summary>最小 chunk 容量（再小就纯亏：chunk 管理的固定开销占比过高）。</summary>
    public const int MinChunkCapacity = 1;

    /// <summary>最大 chunk 容量（防止一次误配置分配出过大数组使 LOH 抖动）。</summary>
    public const int MaxChunkCapacity = 4096;

    /// <summary>Job 分片的默认最小 chunk 批次数（ECS.md 第 5.3 节 ForEachJob 的默认 minChunkBatch）。</summary>
    public const int DefaultMinChunkBatch = 1;

    /// <summary>每个原型初始的 chunk 槽位数。</summary>
    public const int InitialChunkSlotCapacity = 4;

    /// <summary>原型表初始容量。</summary>
    public const int InitialArchetypeCapacity = 8;

    /// <summary>CommandBuffer 初始命令容量。</summary>
    public const int InitialCommandCapacity = 64;

    /// <summary>SystemPipeline 每组初始容量。</summary>
    public const int InitialSystemsPerGroup = 8;

    /// <summary>
    /// 单次提交（<see cref="CommandBuffer.Flush"/>）允许的最大「命令触发新命令」深度。
    /// 超过即视为命令逻辑写成了递归，直接抛异常而不是栈溢出。
    /// </summary>
    public const int MaxCommandFlushDepth = 8;

    /// <summary>校验一个 chunk 容量是否合法（构造 Archetype / 注入配置时调用）。</summary>
    public static void ValidateChunkCapacity(int capacity)
    {
        if (capacity < MinChunkCapacity || capacity > MaxChunkCapacity)
        {
            throw new GameEcsException(
                string.Concat("chunk 容量必须在 ", MinChunkCapacity.ToString(), "..", MaxChunkCapacity.ToString(),
                    " 之间，实际为 ", capacity.ToString(), "。"));
        }
    }
}
