using System;
using Game.Combat;
using Game.Core;
using Game.Core.Ecs;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Game.Spawn;

/// <summary>
/// 刷怪导演：按配置生成「子弹」实体，写入 Game.Combat.Projectile / Transform3 / Velocity / Lifetime / Team 五个组件。
/// <para>
/// <b>为什么是 <c>Game.Combat.Projectile</c> 而不是某个同名示例组件</b>：玩法子弹的组件只有一个真源。
/// 曾经的 <c>Game.Core.Ecs.Projectile</c> 与本文件靠 <c>using Game.Core.Ecs</c> 静默绑定，字段部分重叠、
/// 编译照样过，但 <c>ProjectileSystem</c>/<c>HitDetectionSystem</c>（查 Combat 版）永远看不到这些子弹 ——
/// 子弹不动、不回收、打不到人，却没有任何报错。2026-11-05 已删除该示例组件并在此改绑。
/// </para>
/// <para>
/// <b>为什么生成必须走 <c>World.Commands</c> 而不是 <c>World.Create</c></b>（J5）：
/// 生成会创建新原型 / 新 chunk，属于结构化变更。若在遍历或系统执行中途直接 <c>Create</c>，
/// 其他系统正在持有的 Span 会失效。命令缓冲把它们排到帧末的 Flush 阶段统一落地。
/// </para>
/// <para>
/// <b>为什么用 <see cref="IRandomProvider"/> 而不是 <c>Random.Shared</c></b>：
/// 肉鸽要「同 seed 同结果」。随机源由宿主注入，且本系统用 <c>Fork</c> 拿自己的子流，
/// 于是「别人多加一次随机调用」不会打乱本系统的序列（回放对得上）。
/// </para>
/// <para>
/// <b>为什么数值全部来自构造参数</b>：速度、寿命、半径都是玩法数值（规则4）。
/// 宿主从 <c>WorldHostOptions</c>（Resource，可在 Inspector 调）读出来注入，
/// Excel 落地后改由 Def 注入，<b>本文件一个字都不用改</b>。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.Update)]
public sealed class SpawnDirectorSystem : SystemBase
{
    /// <summary>随机子流的盐值：与其他系统隔离，互不影响。</summary>
    private const int SpawnSalt = 0x5A17;

    private readonly IRandomProvider _random;
    private readonly SpawnSettings _settings;

    /// <summary>本帧发出的生成请求数（诊断）。</summary>
    public int LastSpawned { get; private set; }

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="random">随机源（宿主注入的父流；本系统会 Fork 自己的子流）。</param>
    /// <param name="settings">生成配置（数值全部来自 Resource / Excel）。</param>
    public SpawnDirectorSystem(IRandomProvider random, SpawnSettings settings)
    {
        if (random is null)
        {
            throw new ArgumentNullException(nameof(random));
        }
        _random = random.Fork(SpawnSalt);
        _settings = settings;
    }

    /// <inheritdoc />
    public override string Name => "SpawnDirector";

    /// <summary>
    /// 生成一批初始子弹（由宿主在 <c>_Ready</c> 调用一次，不进每帧循环）。
    /// </summary>
    /// <param name="count">数量。</param>
    public void SpawnBurst(int count)
    {
        for (int i = 0; i < count; i++)
        {
            SpawnOne();
        }
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        // 冒烟场景的稳态行为：每帧补满到目标数量（被销毁的子弹会被替换）。
        // 这样长时间 --quit-after 运行下，实体数稳定在配置值，便于观察池复用是否收敛。
        int alive = World.EntityCount;
        int want = _settings.TargetAliveCount - alive;
        LastSpawned = 0;
        if (want <= 0)
        {
            return;
        }
        // 每帧最多补一个，避免某一帧突然生成大量实体造成峰刺。
        SpawnOne();
    }

    private void SpawnOne()
    {
        // 水平圆盘内均匀撒点：sqrt(u) 保证面积均匀（否则会向圆心聚集）。
        float u = _random.NextFloat();
        float angle = _random.NextFloat() * Tau;
        float radius = _settings.Radius * (float)Math.Sqrt(u);

        float x = MathF.Cos(angle) * radius;
        float z = MathF.Sin(angle) * radius;
        float y = _settings.Height;

        // 朝向：沿径向向外（看起来像从中心迸发）。
        NVector3 dir = new NVector3(x, 0f, z);
        float len = dir.Length();
        if (len <= Epsilon)
        {
            dir = NVector3.UnitZ;
            len = 1f;
        }
        dir = NVector3.Normalize(dir);

        float speed = _settings.SpeedMin + _random.NextFloat() * (_settings.SpeedMax - _settings.SpeedMin);
        NVector3 velocity = dir * speed;

        // 从「位置」指向「位置 + 速度」的四元数（LookAt 语义）。
        NQuaternion rotation = LookRotation(dir, NVector3.UnitY);

        // 缩放：视觉尺寸，不影响判定逻辑（判定走下面子弹的 Radius）。
        float scale = _settings.Scale;

        // 阵营：冒烟子弹统一按敌方发射（与下面的 Team 组件同源，两处必须一致）。
        TeamId projectileTeam = TeamId.Enemy;

        Transform3 transform = new Transform3
        {
            Position = new NVector3(x, y, z),
            Rotation = rotation,
            Scale = new NVector3(scale, scale, scale),
        };
        Velocity vel = new Velocity { Linear = velocity, Angular = NVector3.Zero };
        Lifetime life = new Lifetime { Remaining = _settings.LifetimeSeconds, Duration = _settings.LifetimeSeconds };
        Team team = new Team { Value = projectileTeam };
        Projectile projectile = new Projectile
        {
            // 无射手：冒烟子弹由场景机关发射，不属于任何实体。HitDetectionSystem 的「自己不打自己」
            // 判据是 (槽位号, 世代号) 同时相等，这两个哨兵值永远匹配不上真实实体，因此不会误伤/误放行。
            ShooterIndex = Entity.None.Index,
            ShooterGeneration = Entity.None.Generation,
            Team = projectileTeam,
            // 伤害：冒烟子弹不参与对角色结算（没有 Hurtbox 目标时命中检测直接跳过），
            // 这里只填"不产生结算"的零值，不给它编一个玩法数值。
            Damage = 0f,
            // 判定半径：沿用冒烟场景既有的池化尺寸 —— ProjectileScale 是这颗子弹的"视觉直径缩放"，
            // 它的实际模型约为 1 米，因此半径 = 缩放的一半，跟视觉体积对得上。
            // arch-ok: 由 Resource 的 ProjectileScale 派生，不是硬编码玩法数值（Excel 落地后改读 Def 的 CollisionRadius）
            Radius = scale * SampleRadiusRatio,
            Speed = speed,
            // 射程：0 = 不看射程，只靠 Lifetime 兜底（与 ProjectileSystem 的约定一致），
            // 冒烟的模型就是"飞到寿命结束"，所以不给它编一个射程数值。
            RangeMeters = 0f,
            Critical = false,
            WeaponId = 0,
            DamageType = DamageType.Kinetic,
            Traveled = 0f,
            Spent = false,
        };
        View view = new View { Slot = 0, Kind = 0, Dirty = false };

        // 走命令缓冲：世界结构在 Flush 阶段才变（J5）。
        //
        // 先 CreateDeferred 定号，再逐个 Set 补初值。为什么不用单组件的 World.Commands.Create<T>：
        // 那会先把实体放进「只有 T 一个组件」的原型，后续 Set 追加组件要触发一次迁移——
        // 每生成一颗子弹白迁移一次，还多建一个原型。这里一次性按完整原型预留，Set 只是原地写值。
        Entity entity = World.Commands.CreateDeferred(ViewModel.Components(World));
        World.Commands.Set(entity, projectile);
        World.Commands.Set(entity, transform);
        World.Commands.Set(entity, vel);
        World.Commands.Set(entity, life);
        World.Commands.Set(entity, team);
        World.Commands.Set(entity, view);

        LastSpawned++;
    }

    /// <summary>判定半径 = 视觉缩放 × 本比例（1 米模型 → 半径为缩放的一半）。结构性比例，不是玩法数值。</summary>
    private const float SampleRadiusRatio = 0.5f;  // arch-ok: 由 ProjectileScale 派生半径的比例常量（模型 1 米 → r = scale/2），不是玩法数值

    /// <summary>π 的两倍（结构性常量，不是玩法数值）。</summary>
    private const float Tau = (float)(Math.PI * 2.0);  // arch-ok: 2π 三角常量（数学常数，不是玩法数值）

    /// <summary>长度判定下限（浮点安全阈值）。</summary>
    private const float Epsilon = 1e-6f;  // arch-ok: 浮点零判定阈值（数值安全常量，不是玩法数值）

    /// <summary>由前向 + 世界上方向构造四元数（右手系 Y-up）。</summary>
    private static NQuaternion LookRotation(in NVector3 forward, in NVector3 up)
    {
        NVector3 f = NVector3.Normalize(forward);
        NVector3 r = NVector3.Cross(up, f);
        if (r.LengthSquared() <= Epsilon)
        {
            r = NVector3.UnitX;
        }
        r = NVector3.Normalize(r);
        NVector3 u = NVector3.Cross(f, r);
        return NQuaternion.CreateFromRotationMatrix(new System.Numerics.Matrix4x4(
            r.X, r.Y, r.Z, 0f,
            u.X, u.Y, u.Z, 0f,
            f.X, f.Y, f.Z, 0f,
            0f, 0f, 0f, 1f));
    }
}
