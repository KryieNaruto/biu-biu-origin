using System;
using System.Numerics;
using Game.Actor;
using Game.Combat;
using Game.Core;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Skill;

/// <summary>
/// 左键射击（<see cref="ISkill"/> 实现，装在 <see cref="SkillSlot.PrimaryAttack"/> 槽）：把「瞄准方向」变成一颗子弹。
/// <para>
/// <b>为什么是一个技能，而不是"输入里直接 Spawn 子弹"</b>
/// </para>
/// <list type="bullet">
/// <item>冷却、法力、失败原因由 <c>SkillCastSystem</c> 统一门控 —— 射击与冲刺、换武器走完全相同的管线，
///       于是"新角色换一把武器"只是技能包里换一个技能实例（规则7）；</item>
/// <item>技能是<u>数据 + 策略</u>：本类只管"怎么开火"，伤害/弹速/射程/散布/暴击全部来自施放者身上的
///       <see cref="WeaponStats"/> 组件（由数据行解析而来）。所以同一个技能实例可以被 4 个变体共享，
///       差异全在数据（规则4/规则6）；</item>
/// <item>结构变更走 <c>ctx.World.Commands</c>（J5）：本类<u>不</u>创建实体、不碰 <c>World.Create</c>。</item>
/// </list>
/// <para>
/// <b>为什么"消耗当前帧的意图速度"而不是读按键</b>：本类拿不到输入（那是宿主的活）。
/// "按住左键持续射击"由技能系统每帧重复调用 <see cref="Cast"/> 表达：射速 = 冷却时长（来自武器数据）。
/// 这样"点射/连发"不是技能里的状态机，而是数据上的冷却长短 —— 少一个状态就少一类 bug。
/// </para>
/// <para>
/// <b>为什么子弹的朝向写进 <see cref="Transform3"/>.Rotation 而不是组件里再存一份方向</b>：
/// 子弹的方向一旦发射就不再变，而旋转本来就要给表现层（曳光、尾焰朝向）；同一件事只存一个地方。
/// 约定：模型/弹道前方 = +Z（与 <see cref="Game.Actor.MoveAimSystem.LookTowards"/> 一致）。
/// </para>
/// </summary>
public sealed class FireProjectileSkill : ISkill
{
    /// <summary>技能 id（Excel <c>Skills</c> 主键；由数据侧给，不在代码里发明）。</summary>
    private readonly int _id;

    /// <summary>抛射物定义 id（数据侧用来选外观/行为；空 = 用默认子弹）。</summary>
    private readonly string _projectileId;

    /// <summary>冷却时长（秒）= 两发之间的最小间隔。0 = 每帧都能打（调试用）。</summary>
    private readonly float _cooldownSeconds;

    /// <summary>法力消耗。</summary>
    private readonly float _manaCost;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="id">技能 id（&gt; 0）。</param>
    /// <param name="projectileId">抛射物定义 id（数据侧 id；命令门面据此选外观）。</param>
    /// <param name="cooldownSeconds">两发之间的最小间隔（来自武器数据的 <see cref="WeaponStats.ShotIntervalSeconds"/>）。</param>
    /// <param name="manaCost">每次开火消耗的法力（0 = 免费，绝大多数枪械）。</param>
    /// <param name="slot">装在哪个槽（默认 <see cref="SkillSlot.PrimaryAttack"/>）。</param>
    public FireProjectileSkill(
        int id,
        string projectileId,
        float cooldownSeconds,
        float manaCost = 0f,
        SkillSlot slot = SkillSlot.PrimaryAttack)
    {
        if (id <= 0)
        {
            // 0 在 ISkill 契约里表示"空技能"：如果允许 id=0，那么技能包里"装了"和"没装"就无法区分。
            throw new ArgumentOutOfRangeException(nameof(id), id, "射击技能的 id 必须 > 0（0 表示空技能）。");
        }
        _id = id;
        _projectileId = projectileId ?? string.Empty;
        _cooldownSeconds = cooldownSeconds > 0f ? cooldownSeconds : 0f;
        _manaCost = manaCost > 0f ? manaCost : 0f;
        DefaultSlot = slot;
    }

    /// <inheritdoc />
    public int Id => _id;

    /// <inheritdoc />
    public string DefId => _projectileId;

    /// <inheritdoc />
    public SkillSlot DefaultSlot { get; }

    /// <inheritdoc />
    public float CooldownSeconds => _cooldownSeconds;

    /// <inheritdoc />
    public float ManaCost => _manaCost;

    /// <summary>最近一次 <see cref="Cast"/> 是否成功地排出了一颗子弹（调试/HUD 用）。</summary>
    public bool LastCastSpawned { get; private set; }

    /// <summary>
    /// 预检：冷却就绪 + 法力足够 + <u>施放者身上有武器</u>。
    /// <para>
    /// 为什么"没有武器"要在预检里拦：没有武器数据时开火只能打出 0 伤害的子弹，
    /// 那在玩家看来就是"打了没反应"；把它变成一次明确的 <see cref="SkillCastFailure.TargetInvalid"/>，
    /// HUD 能提示、日志能定位（数据没装配好，而不是手感问题）。
    /// </para>
    /// <para>本方法<b>无副作用</b>：不掷骰、不写组件、不排命令。</para>
    /// </summary>
    public bool CanCast(in SkillContext ctx) =>
        ctx.IsOffCooldown
        && ctx.HasMana(_manaCost)
        && ctx.World.Ecs is not null
        && ctx.World.Ecs.TryGet<WeaponStats>(ctx.CasterEntity, out _);

    /// <summary>
    /// 开火：读武器数据 → 掷暴击 → 加散布 → 通过命令门面排出一颗子弹。
    /// <para>
    /// <b>冷却/法力不在这里扣</b>：由 <c>SkillCastSystem</c> 在成功后统一扣（"先问后放"的原子性，
    /// 见该系统的类注释）；技能只是策略。
    /// </para>
    /// </summary>
    public SkillCastResult Cast(in SkillContext ctx)
    {
        LastCastSpawned = false;
        World? world = ctx.World.Ecs;
        if (world is null || !world.TryGet<WeaponStats>(ctx.CasterEntity, out WeaponStats weapon))
        {
            return SkillCastResult.Fail(SkillCastFailure.TargetInvalid);
        }

        if (weapon.BulletSpeed <= 0f)
        {
            // 弹速 0 = 子弹不会动，等于"开了火但没有子弹"。这是数据错误，宁可失败也不要静默生成。
            return SkillCastResult.Fail(SkillCastFailure.TargetInvalid);
        }

        Vector3 origin = MuzzleOrigin(ctx, weapon);
        Vector3 direction = AimDirectionOf(ctx);

        IRandomProvider random = ctx.World.Random;
        bool critical = random is not null && weapon.CriticalChance > 0f && random.NextFloat() < weapon.CriticalChance;
        float damage = critical ? weapon.Damage * weapon.CriticalMultiplier : weapon.Damage;

        // 生命周期 = 射程 / 弹速（由数据算出来，不在代码里写秒数）。
        // 射程未配置时用"存活到本帧结束"的极小值兜底，由 ProjectileSystem 的寿命判据立即回收 ——
        // 这样"数据漏配射程"不会留下永远飞行的实体。
        float lifetime = weapon.RangeMeters > 0f
            ? weapon.RangeMeters / weapon.BulletSpeed
            : 0f;

        int pellets = weapon.PelletsPerShot > 0 ? weapon.PelletsPerShot : 1;
        for (int shot = 0; shot < pellets; shot++)
        {
            Vector3 shotDirection = ApplySpread(direction, weapon.SpreadDegrees, random);
            SpawnProjectile(world, ctx, weapon, shotDirection, origin, damage, critical, lifetime);
        }

        LastCastSpawned = true;
        return SkillCastResult.Ok();
    }

    /// <inheritdoc />
    public void Tick(float deltaTime, in SkillContext ctx)
    {
        // 射击没有持续效果：冷却由技能包统一推进（SkillLoadout.TickCooldowns）。
        _ = deltaTime;
        _ = ctx;
    }

    /// <summary>
    /// 排出一颗子弹（命令门面 → 帧末提交）。
    /// <para>
    /// 为什么要在 <see cref="Cast"/> 里立刻把子弹的组件值填好：<c>ICommandSink</c> 只描述"生成一个抛射物"，
    /// 而子弹的伤害/暴击/射手是<u>玩法数据</u>，必须随子弹一起落进 ECS 组件 —— 否则命中检测读不到。
    /// 做法是：先 <c>SpawnProjectile</c>（记录发射参数，给外观/统计用），
    /// 再在同一个命令缓冲里 <c>Set</c> 组件值（命令按写入顺序执行，所以这两条总是成对且有序）。
    /// </para>
    /// </summary>
    private void SpawnProjectile(
        World world,
        in SkillContext ctx,
        in WeaponStats weapon,
        Vector3 direction,
        Vector3 origin,
        float damage,
        bool critical,
        float lifetime)
    {
        Quaternion rotation = MoveAimSystem.LookTowards(direction);
        var at = new SpawnPoint(origin, rotation);
        var launch = new LaunchVector(direction, weapon.BulletSpeed);

        // ① 让宿主/数据侧知道"要生成一颗定义 id 为 X 的子弹"（外观、池化、统计都在那边）。
        ctx.World.Commands.SpawnProjectile(_projectileId, in at, in launch, ctx.CasterTeam);

        // ② 真正的实体由命令缓冲生成：返回值是<u>已预留</u>的实体号，可以立刻被后续命令引用（J5 的预留机制）。
        var projectile = new Game.Combat.Projectile
        {
            ShooterIndex = ctx.CasterActorId,
            ShooterGeneration = ctx.CasterEntity.Generation,
            Team = ctx.CasterTeam,
            Damage = damage,
            Radius = weapon.ProjectileRadius,
            Speed = weapon.BulletSpeed,
            RangeMeters = weapon.RangeMeters,
            Critical = critical,
            WeaponId = weapon.WeaponId,
            DamageType = DamageType.Kinetic,
            Traveled = 0f,
            Spent = false,
        };

        var transform = new Transform3
        {
            Position = origin,
            Rotation = rotation,
            Scale = Vector3.One,
        };

        var velocity = new Velocity
        {
            Linear = direction * weapon.BulletSpeed,
            Angular = Vector3.Zero,
        };

        // 寿命组件：倒计时与销毁都由 ProjectileSystem 负责（不交给 LifetimeSystem），
        // 这里只提供初值 —— 三条死法（命中/射程/寿命）必须收在同一个系统里才能保证销毁恰好一次。
        var life = new Lifetime
        {
            Remaining = lifetime,
            Duration = lifetime,
        };

        var team = new Team { Value = ctx.CasterTeam };

        // Create<T> 只支持一个组件；多组件用 CreateDeferred（预留实体号），随后逐条 SetValue ——
        // 两条命令按写入顺序执行，因此"创建 → 填值"的先后是确定的。
        Entity entity = world.Commands.CreateDeferred(CreateTypes(world));
        world.Commands.Set(entity, projectile);
        world.Commands.Set(entity, transform);
        world.Commands.Set(entity, velocity);
        world.Commands.Set(entity, life);
        world.Commands.Set(entity, team);
    }

    /// <summary>
    /// 子弹的组件清单（一次组装、多次复用：每颗子弹都要用，避免每次 <c>new List</c>）。
    /// <para>为什么用字段缓存而不是每次构造：射击是高频路径，每秒几十次分配会让 GC 在手感敏感的时刻
    /// 触发回收（卡顿）。组件清单在第一次开火后就不再变化。</para>
    /// </summary>
    private System.Collections.Generic.List<ComponentType>? _bulletTypes;
    private ComponentTypeRegistry? _bulletTypesOwner;

    private System.Collections.Generic.IReadOnlyList<ComponentType> CreateTypes(World world)
    {
        ComponentTypeRegistry registry = world.Registry;
        if (_bulletTypes is not null && ReferenceEquals(_bulletTypesOwner, registry))
        {
            return _bulletTypes;
        }

        _bulletTypes = new System.Collections.Generic.List<ComponentType>(5) // arch-ok: 列表初始容量（与游戏数值无关），下面是 5 个组件
        {
            world.Register<Game.Combat.Projectile>(),
            world.Register<Transform3>(),
            world.Register<Velocity>(),
            world.Register<Lifetime>(),
            world.Register<Team>(),
        };
        _bulletTypesOwner = registry;
        return _bulletTypes;
    }

    /// <summary>枪口位置：施放者位置 + 沿瞄准方向的枪口偏移（偏移来自武器数据；0 = 从身体中心出发）。</summary>
    private static Vector3 MuzzleOrigin(in SkillContext ctx, in WeaponStats weapon)
    {
        _ = weapon;
        // 为什么目前不偏移：武器数据里还没有"枪口偏移"这一列，凭空造一个默认值就等于在代码里写数值（规则4）。
        // 需要时由 packages 在武器表加一列 OffsetForward，届时这里改一行。
        return ctx.Origin;
    }

    /// <summary>
    /// 方向：优先用施放者身上的 <see cref="AimTarget.Direction"/>（由瞄准包每帧算好的单位向量），
    /// 没有就退回上下文里的瞄准方向（AI/脚本直接给方向的情况）。
    /// </summary>
    private static Vector3 AimDirectionOf(in SkillContext ctx)
    {
        World? world = ctx.World.Ecs;
        if (world is not null && world.TryGet<AimTarget>(ctx.CasterEntity, out AimTarget aim))
        {
            Vector3 direction = aim.Direction;
            float lengthSquared = direction.LengthSquared();
            if (lengthSquared > float.Epsilon)
            {
                return direction / MathF.Sqrt(lengthSquared);
            }
        }

        Vector3 fallback = ctx.AimDirection;
        float fallbackLengthSquared = fallback.LengthSquared();
        return fallbackLengthSquared > float.Epsilon
            ? fallback / MathF.Sqrt(fallbackLengthSquared)
            : Vector3.UnitZ;
    }

    /// <summary>
    /// 在水平面上绕 Y 轴随机偏转 ±(spread/2) 度（散布 = 全角，0 = 完全精准）。
    /// <para>
    /// 为什么绕 Y 轴而不是任意轴：俯视角射击的散布是"左右偏一点"，竖直散布会让子弹飞过头顶，
    /// 手感上更像 bug。需要 3D 散布时再换实现，接口不变。
    /// </para>
    /// </summary>
    private static Vector3 ApplySpread(Vector3 direction, float spreadDegrees, IRandomProvider? random)
    {
        if (spreadDegrees <= 0f || random is null)
        {
            return direction;
        }
        float halfAngleRadians = spreadDegrees * 0.5f * (MathF.PI / 180f); // arch-ok: 角度制→弧度制的单位换算（PI/180），不是玩法数值
        float offset = (random.NextFloat() * 2f - 1f) * halfAngleRadians; // arch-ok: [0,1)→[-1,1] 的区间映射常数，不是玩法数值
        float cos = MathF.Cos(offset);
        float sin = MathF.Sin(offset);
        return new Vector3(
            direction.X * cos + direction.Z * sin,
            direction.Y,
            direction.Z * cos - direction.X * sin);
    }
}
