using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Game.Combat.Factory;

/// <summary>
/// <b>内容驱动的铸造入口</b>：不经过「武器」这一步，直接按「一个物件契约的到期 / 命中行为」造东西。
/// <para>
/// <b>为什么它必须存在</b>：子弹到期「再射一颗子弹」或「变成怪物」时，语义上已经没有武器参与
/// （子弹不是武器）。但又必须复用<b>同一段</b>建实体的代码，否则就会出现"武器射出的子弹"与
/// "子弹分裂出的子弹"两套写法慢慢漂移。这个类把「造子弹 / 造角色 / 造特效」三件事各收成一个方法，
/// <see cref="EcsObjectFactory"/> 与 <see cref="ProjectileAdvanceSystem"/> 都只调它。
/// </para>
/// <para>
/// <b>三种产物的字段集</b>（这是"子弹 / 怪物 / 特效"在数据上唯一的区别）：
/// </para>
/// <list type="bullet">
/// <item>子弹：<c>Transform3 + Velocity + Team + ProjectileState + Hurtbox</c>；</item>
/// <item>角色：<c>Transform3 + Velocity + Team + Hurtbox</c>（可被命中，会被 `<c>Query&lt;Hurtbox&gt;</c>` 查到）；</item>
/// <item>特效：<c>Transform3 + Team + Lifetime</c>（纯计时存在，无判定）。</item>
/// </list>
/// </summary>
public sealed class ContentSpawner
{
    private readonly World _world;
    private readonly ContentIndex _index;
    private readonly IRandomProvider _random;
    private readonly ForgeValueWorld.StatQuery? _statQuery;

    private readonly ComponentType[] _projectileComponents;
    private readonly ComponentType[] _actorComponents;
    private readonly ComponentType[] _effectComponents;

    /// <summary>构造。</summary>
    public ContentSpawner(World world, ContentIndex index, IRandomProvider random, ForgeValueWorld.StatQuery? statQuery = null)
    {
        _world = world ?? throw new System.ArgumentNullException(nameof(world));
        _index = index ?? throw new System.ArgumentNullException(nameof(index));
        _random = random ?? throw new System.ArgumentNullException(nameof(random));
        _statQuery = statQuery;

        // 组件类型数组在构造期建好：每帧 new 一个 ComponentType[] 就是一次 GC 分配，
        // 而 World.Create(IReadOnlyList<ComponentType>) 必须要一个列表 —— 所以"预建 + 复用"是本系统
        // 做到"稳态零分配"的关键（用户要求"工厂必须高性能"）。
        _projectileComponents = new[]
        {
            _world.Register<Transform3>(),
            _world.Register<Velocity>(),
            _world.Register<Team>(),
            _world.Register<ProjectileState>(),
            _world.Register<Hurtbox>(),
        };
        _actorComponents = new[]
        {
            _world.Register<Transform3>(),
            _world.Register<Velocity>(),
            _world.Register<Team>(),
            _world.Register<Hurtbox>(),
        };
        _effectComponents = new[]
        {
            _world.Register<Transform3>(),
            _world.Register<Team>(),
            _world.Register<Lifetime>(),
        };
    }

    /// <summary>内容表。</summary>
    public ContentIndex Index => _index;

    /// <summary>
    /// 造一颗子弹。
    /// </summary>
    /// <param name="projectileSlot">子弹槽位号。</param>
    /// <param name="position">出生点。</param>
    /// <param name="direction">飞行方向（已归一化）。</param>
    /// <param name="team">阵营。</param>
    /// <param name="source">发射者。</param>
    /// <param name="sourceWeapon">链上的武器实体（可空）。</param>
    /// <param name="chainDepth">链深。</param>
    /// <param name="damageScale">伤害倍率（分裂 / 链式衰减；1 = 不衰减）。</param>
    /// <param name="produced">产物实体（帧末才真正存在）。</param>
    public bool TrySpawnProjectile(
        int projectileSlot,
        in NVector3 position,
        in NVector3 direction,
        TeamId team,
        Entity source,
        Entity sourceWeapon,
        int chainDepth,
        float damageScale,
        out Entity produced)
    {
        produced = Entity.None;
        ProjectileContract? contract = _index.GetProjectile(projectileSlot);
        if (contract is null)
        {
            return false;
        }

        ForgeValueWorld world = new ForgeValueWorld(_index, registry: null!, chainDepth, 0, 1, _random, _statQuery);
        ValueContext ctx = new ValueContext(world, default, _random);

        float speed = ValueResolver.Resolve(contract.Speed, in ctx);
        float lifetime = ValueResolver.Resolve(contract.Lifetime, in ctx);
        float radius = ValueResolver.Resolve(contract.Radius, in ctx);
        float damage = ValueResolver.Resolve(contract.Damage, in ctx) * damageScale;

        Entity entity = _world.Commands.CreateDeferred(_projectileComponents);
        _world.Commands.Set(entity, new Transform3
        {
            Position = position,
            Rotation = LookRotation(in direction),
            Scale = NVector3.One,
        });
        _world.Commands.Set(entity, new Velocity { Linear = direction * speed, Angular = NVector3.Zero });
        _world.Commands.Set(entity, new Team { Value = team });
        _world.Commands.Set(entity, new ProjectileState
        {
            ContractSlot = projectileSlot,
            ArchetypeDefId = _index.ObjectSlotOfHash(contract.ObjectDefId),
            Source = source,
            SourceWeapon = sourceWeapon,
            Damage = damage,
            Lifetime = lifetime,
            Radius = radius,
            PierceLeft = (int)ValueResolver.Resolve(contract.Pierce, in ctx),
            BounceLeft = (int)ValueResolver.Resolve(contract.Bounce, in ctx),
            ChainDepth = chainDepth,
        });
        _world.Commands.Set(entity, new Hurtbox
        {
            Radius = radius,
            Height = 0f,
            InvulnerableRemaining = 0f,
            FlashRemaining = 0f,
            KnockbackResistance = 0f,
        });

        produced = entity;
        return true;
    }

    /// <summary>
    /// 造一个"角色实体"（能被 <c>Query&lt;Hurtbox&gt;</c> 命中的东西）。
    /// <para>
    /// <b>「武器射出怪物」与「子弹变成怪物」共用本方法</b>——这正是"高自由度拼装"的落点：
    /// 两者在数据上只是 <c>WeaponContract.Actor</c> 与 <c>ProjectileContract.OnExpireActor</c> 两列。
    /// </para>
    /// </summary>
    /// <param name="objectSlot">物件的槽位号（尺寸、外观、数值包都在物件那一行）。</param>
    public bool TrySpawnActor(int objectSlot, in NVector3 position, TeamId team, out Entity produced)
    {
        produced = Entity.None;
        ObjectArchetype? archetype = _index.GetObject(objectSlot);
        if (archetype is null)
        {
            return false;
        }

        Entity entity = _world.Commands.CreateDeferred(_actorComponents);
        _world.Commands.Set(entity, new Transform3
        {
            Position = position,
            Rotation = NQuaternion.Identity,
            Scale = NVector3.One,
        });
        _world.Commands.Set(entity, new Velocity { Linear = NVector3.Zero, Angular = NVector3.Zero });
        _world.Commands.Set(entity, new Team { Value = team });
        _world.Commands.Set(entity, new Hurtbox
        {
            Radius = archetype.Radius,
            Height = archetype.Height,
            InvulnerableRemaining = 0f,
            FlashRemaining = 0f,
            KnockbackResistance = 0f,
        });

        produced = entity;
        return true;
    }

    /// <summary>
    /// 造一个特效实体（纯计时存在，无判定）。生命时长来自物件的数值包，本轮先给 0 由生命周期系统接管。
    /// </summary>
    public bool TrySpawnEffect(int objectSlot, in NVector3 position, float lifetime, out Entity produced)
    {
        produced = Entity.None;
        if (_index.GetObject(objectSlot) is null)
        {
            return false;
        }

        Entity entity = _world.Commands.CreateDeferred(_effectComponents);
        _world.Commands.Set(entity, new Transform3
        {
            Position = position,
            Rotation = NQuaternion.Identity,
            Scale = NVector3.One,
        });
        _world.Commands.Set(entity, new Team { Value = TeamId.Neutral });
        _world.Commands.Set(entity, new Lifetime { Remaining = lifetime, Duration = lifetime });

        produced = entity;
        return true;
    }

    /// <summary>由前向方向构造四元数（右手系 Y-up；弹体对称所以不管滚转）。</summary>
    private static NQuaternion LookRotation(in NVector3 forward)
    {
        float lengthSquared = forward.LengthSquared();
        if (lengthSquared <= Epsilon)
        {
            return NQuaternion.Identity;
        }
        NVector3 f = forward / System.MathF.Sqrt(lengthSquared);
        NVector3 up = System.MathF.Abs(f.Y) < FallbackAxisThreshold ? NVector3.UnitY : NVector3.UnitX;
        NVector3 right = NVector3.Normalize(NVector3.Cross(up, f));
        NVector3 correctedUp = NVector3.Cross(f, right);
        return NQuaternion.CreateFromRotationMatrix(new System.Numerics.Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            correctedUp.X, correctedUp.Y, correctedUp.Z, 0f,
            f.X, f.Y, f.Z, 0f,
            0f, 0f, 0f, 1f));
    }

    /// <summary>长度判定下限（浮点安全阈值，不是玩法数值）。</summary>
    private const float Epsilon = 1e-6f;  // arch-ok: 浮点归一化的安全阈值，不是玩法数值

    /// <summary>方向与某根轴"几乎平行"的判定阈值（叉乘退化用的数学常量，不是玩法数值）。</summary>
    private const float FallbackAxisThreshold = 0.5f;  // arch-ok: 叉乘退化判定的数学阈值
}
