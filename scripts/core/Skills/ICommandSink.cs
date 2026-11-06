using System;
using Game.Core;

namespace Game.Core.Skills;

/// <summary>
/// 技能能发起的**结构化变更**（生成/销毁）的最小门面。
/// <para>
/// <b>为什么不让技能直接拿 <c>CommandBuffer</c></b>：
/// </para>
/// <list type="bullet">
/// <item><see cref="ISkill"/> 的实现在 <c>scripts/skill/</c>，而技能系统在 <c>Game.Core</c> 契约层；
/// 门面让"技能能做的事"被显式枚举出来（生成什么、销毁什么），而不是把整个命令缓冲能力交出去；</item>
/// <item>单测里可以给一个<u>记录型</u>实现（见 <c>RecordingCommandSink</c>），不需要 Godot 也不需要真实世界；</item>
/// <item>将来若把生成路径下沉到 C++（ECS.md §9），只换门面的实现，所有技能 0 行改动。</item>
/// </list>
/// </summary>
public interface ICommandSink
{
    /// <summary>请求生成一个角色（按定义 id；技能用它召唤小怪/分身）。</summary>
    void SpawnActor(string definitionId, in SpawnPoint at, TeamId team);

    /// <summary>请求生成投射物（按投射物定义 id）。</summary>
    void SpawnProjectile(string projectileId, in SpawnPoint at, in LaunchVector launch, TeamId team);

    /// <summary>请求生成纯表现特效（不回写任何战斗数值）。</summary>
    void SpawnEffect(string effectId, in SpawnPoint at);

    /// <summary>请求销毁一个实体（实体号 + 世代号，避免误杀已复用的槽位）。</summary>
    void Destroy(int actorId, int generation);

    /// <summary>
    /// 请求给<u>已存在的实体</u>挂上一个组件（例：冲刺给自己挂 <c>DashState</c>）。
    /// <para>
    /// <b>为什么门面需要这个方法</b>：技能施放发生在查询遍历里，此刻直接改世界结构
    /// （加组件 = 一次原型迁移）会让 chunk 数组失效（J5）。而 <see cref="ISkillContext.Ecs"/> 又是
    /// <u>只读</u>句柄 —— 于是"给自己加一个有时限的状态"这件事在旧门面里<u>没有合法路径</u>，
    /// 只能靠"在技能里存一个计时器"绕过去，而那会让共享同一技能包的两个角色状态互相污染。
    /// </para>
    /// <para>
    /// <b>为什么带默认实现</b>：这是接口的<u>纯增量</u>扩展。已有的测试替身（记录型 sink、旧实现）
    /// 不需要改就能继续编译，行为退化为"什么都不做"；新的实现（真实命令缓冲、记录型替身）
    /// 可以覆写它来真正落地/断言。规则7 的判据在这里是"新增一种技能 = 0 个已有实现改动"。
    /// </para>
    /// </summary>
    /// <typeparam name="T">组件类型（必须 unmanaged，与 ECS 组件契约一致）。</typeparam>
    /// <param name="entity">目标实体（通常是施放者自己）。</param>
    /// <param name="value">组件初始值。</param>
    void Add<T>(Game.Core.Ecs.Entity entity, in T value) where T : unmanaged
    {
        // 默认实现刻意留空：不认识的 sink 退化为"没有这个能力"，而不是编译失败。
        _ = entity;
        _ = value;
    }
}

/// <summary>生成点（世界坐标 + 朝向）。为什么不用 Godot 的 Transform：Core 层零引擎依赖（J4）。</summary>
public readonly struct SpawnPoint
{
    /// <summary>世界坐标。</summary>
    public readonly System.Numerics.Vector3 Position;

    /// <summary>朝向（四元数）。</summary>
    public readonly System.Numerics.Quaternion Rotation;

    /// <summary>构造。</summary>
    public SpawnPoint(System.Numerics.Vector3 position, System.Numerics.Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }

    /// <summary>由位置构造（默认无旋转）。</summary>
    public static SpawnPoint At(System.Numerics.Vector3 position) =>
        new SpawnPoint(position, System.Numerics.Quaternion.Identity);
}

/// <summary>投射物的发射参数（速度 = 方向 × 速率；两者都来自技能数据，不在代码里写死）。</summary>
public readonly struct LaunchVector
{
    /// <summary>发射方向（单位向量）。</summary>
    public readonly System.Numerics.Vector3 Direction;

    /// <summary>速率（米/秒）。</summary>
    public readonly float Speed;

    /// <summary>构造。</summary>
    public LaunchVector(System.Numerics.Vector3 direction, float speed)
    {
        Direction = direction;
        Speed = speed;
    }
}
