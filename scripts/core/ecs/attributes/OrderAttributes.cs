using System;

namespace Game.Core.Ecs;

/// <summary>
/// 一帧里的阶段分组（ECS.md 第 5.4 / 第 6 节）。枚举顺序就是这一帧的执行顺序：
/// PreUpdate -> FixedUpdate -> Update -> LateUpdate。
/// </summary>
public enum UpdateGroup
{
    /// <summary>输入采集、随机种子推进、命令缓冲准备。</summary>
    PreUpdate = 0,

    /// <summary>固定步长逻辑：移动 / AI / 抛射物推进（可确定性回放）。</summary>
    FixedUpdate = 1,

    /// <summary>每帧逻辑：技能冷却 / 生命周期 / 伤害结算 / 夺舍。</summary>
    Update = 2,

    /// <summary>表现只读数据采集、实体位置插值参数。</summary>
    LateUpdate = 3,
}

/// <summary>
/// 声明系统属于哪个分组。不标注 = UpdateGroup.Update
/// （「每帧逻辑」是最常见的默认值，少写一个特性就少一处踩坑）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UpdateInGroupAttribute : Attribute
{
    public UpdateInGroupAttribute(UpdateGroup group) => Group = group;

    public UpdateGroup Group { get; }
}

/// <summary>
/// 「我必须在 systemType 之后执行」。只在同一分组内生效；
/// 目标系统没注册时这条边被忽略——这样宿主可以按需装配系统族，不用每次都把全部系统装上。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UpdateAfterAttribute : Attribute
{
    public UpdateAfterAttribute(Type systemType)
    {
        if (systemType is null)
        {
            throw new GameEcsException("[UpdateAfter] 的目标类型不能为 null。");
        }
        Target = systemType;
    }

    public Type Target { get; }
}

/// <summary>「我必须在 systemType 之前执行」；忽略规则同 UpdateAfterAttribute。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UpdateBeforeAttribute : Attribute
{
    public UpdateBeforeAttribute(Type systemType)
    {
        if (systemType is null)
        {
            throw new GameEcsException("[UpdateBefore] 的目标类型不能为 null。");
        }
        Target = systemType;
    }

    public Type Target { get; }
}

/// <summary>
/// 「这个系统的计算可按 chunk 分片并行」，同时也是将来交给 C++ 批量求解器（ECS.md 第 9 节）的标记。
/// <para>
/// 为什么是标记而不是自动并行：一个系统的 OnExecute 里通常有多次查询，
/// 内核无法证明第 2 次查询不依赖第 1 次的写入。标 [Job] 等于系统作者交付承诺：
/// 「我的回调内没有跨 chunk 的可写共享状态」。这也是 docs/rules/02-tech-selection.md 第 2 节
/// 下沉判据的起点。
/// </para>
/// <para>
/// 本轮只做登记与查询（SystemPipeline.IsJob）；自动调度属于宿主层决策，且必须先有
/// 确定性分块（ECS.md 第 12 节待办 2），否则回放会漂。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobAttribute : Attribute
{
}
