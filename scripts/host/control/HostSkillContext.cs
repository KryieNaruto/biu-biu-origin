using System;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Randomness;
using Game.Core.Skills;
using Game.Skill;
using Godot;

namespace Game.Host.Control;

/// <summary>
/// 宿主侧的技能上下文实现：给技能提供"提交命令 / 取属性 / 取随机数"的三件套。
/// <para>
/// <b>为什么上下文要从外面传进来而不是让技能自己拿世界</b>：技能若直接持有 <see cref="World"/>，
/// 就能在施放过程中做结构性改动（生成/销毁实体），而施放发生在查询遍历里 ——
/// 这正是 ECS 内核的硬禁忌。上下文只暴露<u>提交队列</u>（<see cref="ICommandSink"/>），
/// 真正的结构变更由管线在帧末统一执行。
/// </para>
/// <para>
/// <b>为什么放在宿主层</b>：<see cref="IRandomProvider"/> 与命令队列的落地实现需要宿主的选择
/// （种子来源、池、视图），属于引擎边界（J4）。技能本身（<see cref="ISkill"/> 实现）
/// 依然在 <c>scripts/skill/</c> 里、不碰引擎。
/// </para>
/// </summary>
public sealed class HostSkillContext : ISkillContext
{
    private readonly Node _owner;
    private readonly World _world;
    private readonly IRandomProvider _random;

    /// <summary>构造。</summary>
    /// <param name="owner">宿主节点（v1 只用于日志与生命周期，不直接驱动玩法）。</param>
    /// <param name="world">世界（只读查询属性用；不对外暴露写能力）。</param>
    public HostSkillContext(Node owner, World world)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        // 技能的随机必须走注入，保证同 seed 同结果（可复现是肉鸽的硬需求）。
        _random = new DeterministicRandom((int)(Time.GetTicksUsec() & 0x7FFFFFFF));
        Commands = new HostCommandSink(owner, world);
    }

    /// <inheritdoc />
    public ICommandSink Commands { get; }

    /// <inheritdoc />
    /// <remarks>
    /// <b>只读纪律</b>：这是世界本身，交给技能是为了让它们能 <c>TryGet</c> 施放者身上的组件（武器/瞄准）。
    /// 技能实现禁止调用 <c>Create/Destroy/Set/Remove/Flush</c>；即使写了，内核的"遍历中禁止结构变更"
    /// 守卫也会把它变成一条中文异常而不是静默的 chunk 损坏（见 <see cref="ISkillContext.Ecs"/> 的说明）。
    /// </remarks>
    public World Ecs => _world;

    /// <inheritdoc />
    public IRandomProvider Random => _random;

    /// <inheritdoc />
    public float DeltaTime { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// v1 返回 <c>null</c>：角色的数值快照目前挂在装配系统里，尚未做成查询入口。
    /// 需要时由 <see cref="Game.Actor.ActorAssemblySystem"/> 暴露"按实体取最终属性"即可，
    /// 技能侧代码不用改（只依赖接口）。
    /// </remarks>
    public IReadOnlyStatView? StatsOf(int actorId) => null;
}

/// <summary>
/// 宿主侧命令门面：把技能想做的结构变更<u>入队</u>，而不是立即执行。
/// <para>
/// v1 的实现只做记录与日志（子弹/特效系统落地后在这里接真实的生成逻辑）。
/// 之所以现在就把门面做出来：技能的<u>契约</u>必须先稳定 —— 技能作者只知道
/// "我要生成一颗子弹"，不知道子弹怎么被池化、怎么被桥接成节点。
/// </para>
/// </summary>
internal sealed class HostCommandSink : ICommandSink
{
    private readonly Node _owner;
    private readonly World _world;

    public HostCommandSink(Node owner, World world)
    {
        _owner = owner;
        _world = world;
    }

    /// <summary>本帧累计的"生成子弹"请求数（调试用）。</summary>
    public int PendingProjectiles { get; private set; }

    /// <summary>本帧累计的"生成单位"请求数（调试用）。</summary>
    public int PendingActors { get; private set; }

    /// <inheritdoc />
    public void SpawnActor(string definitionId, in SpawnPoint at, TeamId team)
    {
        PendingActors++;
        GD.Print("[Skill] 请求生成单位 ", definitionId, " 阵营=", team, " 位置=", at);
    }

    /// <inheritdoc />
    public void SpawnProjectile(string projectileId, in SpawnPoint at, in LaunchVector launch, TeamId team)
    {
        PendingProjectiles++;
        // 真正的子弹实体由技能自己通过 `ctx.World.Ecs.Commands.CreateDeferred + Set` 生成
        // （玩法组件 Projectile/Transform3/Velocity/Lifetime/Team 属于战斗层，宿主不该重复组装一份）。
        // 这里保留入口给"纯表现型子弹"（只有外观、没有判定）与统计使用；
        // 首个请求打一行日志，便于确认"射击 → 子弹"这条链真的走通了。
        if (PendingProjectiles == 1)
        {
            GD.Print("[Skill] 首个子弹请求 ", projectileId, " 阵营=", team,
                " 位置=", at.Position.X.ToString("F2"), ",", at.Position.Y.ToString("F2"), ",", at.Position.Z.ToString("F2"),
                " 弹速=", launch.Speed.ToString("F1"));
        }
    }

    /// <inheritdoc />
    public void SpawnEffect(string effectId, in SpawnPoint at)
    {
        // 特效系统未接入；保留入口，保证技能契约稳定。
    }

    /// <inheritdoc />
    public void Destroy(int actorId, int generation)
    {
        // 销毁走 CommandBuffer 的正式路径（scripts/core/ecs/CommandBuffer），不在这里裸调 World。
        _world.Commands.Destroy(new Entity(actorId, generation));
    }

    /// <inheritdoc />
    /// <remarks>
    /// 与 <see cref="Destroy"/> 同理走命令缓冲：施放发生在查询遍历里，
    /// 直接 <c>_world.Set</c> 会在 <c>AssertStructuralAllowed</c> 处抛异常（J5 的正确行为）。
    /// </remarks>
    public void Add<T>(Entity entity, in T value) where T : unmanaged
    {
        _world.Commands.Add(entity, value);
    }
}
