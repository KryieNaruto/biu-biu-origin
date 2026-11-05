using System;
using System.Numerics;
using Game.Core;
using Game.Core.Ecs;

namespace Game.Combat;

/// <summary>
/// 命中检测：子弹（球） × 受击盒（球/胶囊）的宽相位 + 窄相位（契约 §3.1）。
/// <para>
/// <b>为什么是两层查询而不是一层</b>：子弹与目标来自两个不同的原型集合，内核的 <c>Query</c> 只表达
/// "同一次匹配"的组件组；"对每一颗子弹遍历所有目标"必须由系统自己写（这就是 system 的职责）。
/// 做法是：先把目标抽进一个复用数组（一次遍历），再对子弹做一次遍历（一次遍历），
/// 两层各自都是连续的 chunk 访问 —— 比"每颗子弹重新建一次查询"快得多，也不会在遍历里建查询。
/// </para>
/// <para>
/// <b>为什么跳过同射手</b>：枪口就在射手身上，不跳过会第一帧就打中自己。
/// 判据是 (槽位号, 世代号) 都相同而不是只比槽位号：槽位会被复用，只比槽位号会把"新生成的另一只怪"
/// 误判成"我自己"（那是一个只在实体生灭频繁时偶发、极难复现的 bug）。
/// </para>
/// <para>
/// <b>为什么用 <see cref="TeamIdExtensions.IsHostileTo"/> 而不是 <c>a != b</c></b>：敌我关系的定义
/// （中立不参战、将来开友军误伤）收口在一处，改规则不需要搜全工程。
/// </para>
/// <para>
/// <b>命中结果不在这里产生副作用</b>：只写 <see cref="Projectile.Spent"/>（纯数据写）和往
/// <see cref="HitBuffer"/> 追加记录；事件与特效由 <see cref="ImpactEventSystem"/> 在遍历结束后处理。
/// 这样"遍历中不允许结构变更"（J5）永远不会被违反。
/// </para>
/// <para>
/// <b>为什么窄相位用"点-球"而不是扫掠</b>：高速子弹在低帧率下会隧穿；v1 用"判定半径 + 每帧位移"兜住
/// 常见情况（子弹速度 × 固定步长远小于半径之和）。扫掠/空间哈希登记在 ECS.md §12 待办 4，
/// 触发时机是"100+ 敌人同屏实测后"—— 现在做等于给自己提前引入没验证的复杂度。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.FixedUpdate)]
[Module("fire_primary")]
public sealed class HitDetectionSystem : SystemBase, ISpanAction<Projectile, Transform3>, ISpanAction<Hurtbox, Team, Transform3>
{
    private readonly HitBuffer _hits;

    private Query _projectileQuery = null!;
    private Query _targetQuery = null!;

    private readonly ISpanAction<Projectile, Transform3> _projectileAction;
    private readonly ISpanAction<Hurtbox, Team, Transform3> _targetAction;

    /// <summary>目标暂存（每帧重填；容量不足时倍增，稳态零分配）。</summary>
    private TargetSnapshot[] _targets = new TargetSnapshot[InitialCapacity];

    /// <summary>目标暂存初始容量（内存预分配，不是玩法数值）。</summary>
    private const int InitialCapacity = 64; // arch-ok: 暂存数组的预分配容量（内存/性能参数），不是玩法数值

    private int _targetCount;

    /// <summary>本帧检测的子弹数（调试用）。</summary>
    public int CheckedProjectiles { get; private set; }

    /// <summary>本帧产生的命中数（调试/日志用）。</summary>
    public int HitCount { get; private set; }

    /// <summary>累计命中数（跨帧；宿主日志的 <c>hits=</c> 就是它）。</summary>
    public int TotalHits { get; private set; }

    /// <summary>构造：注入命中缓冲（与 <see cref="ImpactEventSystem"/> 共享同一个实例）。</summary>
    public HitDetectionSystem(HitBuffer hits)
    {
        _hits = hits ?? throw new ArgumentNullException(nameof(hits));
        _projectileAction = this;
        _targetAction = this;
    }

    /// <inheritdoc />
    public override string Name => "HitDetection";

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        _projectileQuery = World.Query<Projectile, Transform3>();
        // 受击盒 + 阵营 + 位置：没有阵营的目标不参与敌我判定，因此不进候选集（J3：用查询表达筛选）。
        _targetQuery = World.Query<Hurtbox, Team, Transform3>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _ = deltaTime;
        CheckedProjectiles = 0;
        HitCount = 0;

        // ① 抽目标（一次遍历，把需要的数据搬进连续数组，窄相位时全是缓存内访问）。
        _targetCount = 0;
        _targetQuery.ForEach<Hurtbox, Team, Transform3>(_targetAction);

        // ② 对子弹做命中判定（一次遍历）。
        _projectileQuery.ForEach<Projectile, Transform3>(_projectileAction);

        TotalHits += HitCount;
    }

    /// <inheritdoc />
    void ISpanAction<Hurtbox, Team, Transform3>.Execute(Span<Hurtbox> hurtboxes, Span<Team> teams, Span<Transform3> transforms, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (hurtboxes[i].InvulnerableRemaining > 0f)
            {
                // 无敌帧中的目标不进入候选集：不是"命中了但忽略"，而是"根本没打中"（音效/命中数都不该算）。
                continue;
            }
            if (_targetCount >= _targets.Length)
            {
                Array.Resize(ref _targets, _targets.Length * 2);
            }
            _targets[_targetCount++] = new TargetSnapshot(
                hurtboxes[i].Owner,
                transforms[i].Position,
                hurtboxes[i].Radius,
                hurtboxes[i].Height,
                teams[i].Value);
        }
    }

    /// <inheritdoc />
    void ISpanAction<Projectile, Transform3>.Execute(Span<Projectile> projectiles, Span<Transform3> transforms, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (projectiles[i].Spent)
            {
                continue;
            }

            Vector3 position = transforms[i].Position;
            float radius = projectiles[i].Radius;
            TeamId team = projectiles[i].Team;
            int shooterIndex = projectiles[i].ShooterIndex;
            int shooterGeneration = projectiles[i].ShooterGeneration;

            int best = -1;
            float bestDistanceSquared = 0f;

            for (int t = 0; t < _targetCount; t++)
            {
                TargetSnapshot target = _targets[t];

                // 自己不打自己：槽位号 + 世代号都要一致才算"同一个实体"。
                if (target.Entity.Index == shooterIndex && target.Entity.Generation == shooterGeneration)
                {
                    continue;
                }

                // 敌我：中立不参战（环境伤害走独立结算入口，不经过子弹）。
                if (!team.IsHostileTo(target.Team))
                {
                    continue;
                }

                // 竖直方向：胶囊近似 —— 命中体高的一半 + 子弹半径，避免子弹"从头顶飞过"却打中。
                float verticalTolerance = target.Height * 0.5f + radius;
                float dy = position.Y - target.Position.Y;
                if (dy > verticalTolerance || dy < -verticalTolerance)
                {
                    continue;
                }

                // 水平方向：球-球。
                float dx = position.X - target.Position.X;
                float dz = position.Z - target.Position.Z;
                float reach = radius + target.Radius;
                float distanceSquared = dx * dx + dz * dz;
                if (distanceSquared > reach * reach)
                {
                    continue;
                }

                // 取最近的命中：一帧内子弹可能同时压到两个目标，选更近的那个，另一颗留给后面的子弹（穿透由系统扩展）。
                if (best < 0 || distanceSquared < bestDistanceSquared)
                {
                    best = t;
                    bestDistanceSquared = distanceSquared;
                }
            }

            CheckedProjectiles++;

            if (best < 0)
            {
                continue;
            }

            TargetSnapshot hit = _targets[best];
            projectiles[i].Spent = true;
            HitCount++;
            _hits.Add(new HitRecord(
                projectiles[i].Owner,
                shooterIndex,
                shooterGeneration,
                hit.Entity,
                position,
                projectiles[i].Damage,
                projectiles[i].Critical,
                projectiles[i].DamageType,
                projectiles[i].WeaponId,
                World.Commands.FlushCount));
        }
    }

    /// <summary>目标快照（值类型：把窄相位需要的数据从 chunk 里搬出来，避免在双层循环里反复查列）。</summary>
    private readonly struct TargetSnapshot
    {
        public readonly Entity Entity;
        public readonly Vector3 Position;
        public readonly float Radius;
        public readonly float Height;
        public readonly TeamId Team;

        public TargetSnapshot(Entity entity, Vector3 position, float radius, float height, TeamId team)
        {
            Entity = entity;
            Position = position;
            Radius = radius;
            Height = height;
            Team = team;
        }
    }
}
