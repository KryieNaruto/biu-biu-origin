using System;
using System.Collections.Generic;
using System.Numerics;
using Game.Core.Ecs;

namespace Game.Combat;

/// <summary>
/// 投射物推进：直线飞行 + 生命周期（射程 / 寿命）到期销毁（契约 §3.1）。
/// <para>
/// <b>为什么位置在这里推进，而不复用 <see cref="Game.Actor.MovementSystem"/></b>：MovementSystem 是
/// 「意图驱动的角色移动」（速度由数值包与意图算出来）；子弹的速度是<u>一次性的</u>（开火后不再变），
/// 且要顺带累加"已飞距离"用于射程判定。两者共用同一条 <c>位置 += 速度*dt</c> 形式的代码却语义不同，
/// 强行合并会让 MovementSystem 里出现"如果这是子弹就…"的分支（J3 的反面）。
/// </para>
/// <para>
/// <b>为什么销毁要两遍扫描</b>：第一遍（遍历中）只做纯数据写；第二遍（遍历结束）才向
/// <see cref="World.Commands"/> 投递销毁命令 —— 遍历期间改结构会让 chunk 数组失效（J5）。
/// 到期实体临时存在复用数组里，稳态零分配。
/// </para>
/// <para>
/// <b>为什么射程与寿命是两个判据</b>：射程是策划可调的手感量（"这把枪能打多远"），寿命是安全网
/// （数据填错时子弹不会永远飞）。任何一个先到都销毁 —— 于是"射程改大"不会让子弹提前消失，
/// "数据里射程忘了填"也不会留下永远飞行的实体。
/// </para>
/// <para>
/// <b>与 LifetimeSystem 的顺序</b>：本系统在 <see cref="UpdateGroup.FixedUpdate"/>，<see cref="LifetimeSystem"/>
/// 在 <see cref="UpdateGroup.Update"/>，所以本系统<u>总是先跑</u>（组顺序由管线固定，见 ECS.md）。
/// 因此 <see cref="LifetimeExpiredCount"/> 能看到"这一帧寿命恰好归零"的子弹并计数，随后才由
/// <see cref="LifetimeSystem"/> 统一销毁 —— 不要把它挪进 Update 组，否则本系统的范围/寿命判定整体延后，
/// 而且该计数会静默失效（排序器只在<u>组内</u>解析 [UpdateBefore]/[UpdateAfter]，跨组声明是无效的）。
/// </para>
/// </summary>
[Module("fire_primary")]
[UpdateInGroup(UpdateGroup.FixedUpdate)]
public sealed class ProjectileSystem : SystemBase, ISpanAction<Projectile, Transform3>
{
    private Query _query = null!;

    /// <summary>到期待销毁的子弹（系统字段上复用；每帧只 <c>Clear</c>，不重新分配）。</summary>
    private readonly List<Entity> _expired = new(InitialCapacity);

    private readonly ISpanAction<Projectile, Transform3> _action;

    private float _deltaTime;

    /// <summary>初始暂存容量（内存预分配，不是玩法数值）。</summary>
    private const int InitialCapacity = 32; // arch-ok: 待回收数组的预分配容量（内存/性能参数），不是玩法数值

    /// <summary>本帧推进的子弹数（调试/日志用）。</summary>
    public int AdvancedCount { get; private set; }

    /// <summary>本帧因到期销毁的子弹数（调试/日志用）。</summary>
    public int ExpiredCount { get; private set; }

    /// <summary>本帧因寿命兜底销毁的子弹数（&gt; 0 说明射程数据可能有问题）。</summary>
    public int LifetimeExpiredCount { get; private set; }

    /// <summary>构造。</summary>
    public ProjectileSystem()
    {
        _action = this;
    }

    /// <inheritdoc />
    public override string Name => "Projectile";

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        // 子弹必须有位置（推进）与寿命（兜底销毁）。没有 Lifetime 的子弹不会被本系统推进 ——
        // 这是刻意的：让"漏挂寿命"变成"子弹不动"这种立刻可见的问题，而不是"子弹永远飞"。
        // 寿命虽然存在通用 Lifetime 组件里，但**倒计时与销毁都由本系统负责**（见 Execute 里的说明），
        // LifetimeSystem 必须跳过子弹，否则两个系统会抢同一颗子弹。
        _query = World.Query<Projectile, Transform3>().With<Lifetime>();
    }
    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _deltaTime = deltaTime;
        AdvancedCount = 0;
        ExpiredCount = 0;
        LifetimeExpiredCount = 0;

        // 复用容器：只清长度、不释放容量。稳态下这里一次分配都没有（规则：稳态零分配）。
        _expired.Clear();

        // 第一遍：纯数据写（推进 + 累加里程 + 记到期）。遍历期间世界结构不变。
        _query.ForEach<Projectile, Transform3>(_action);

        // 第二遍：遍历已结束，可以安全投递结构化命令。
        for (int i = 0; i < _expired.Count; i++)
        {
            World.Commands.Destroy(_expired[i]);
        }
        ExpiredCount = _expired.Count;
    }

    /// <inheritdoc />
    void ISpanAction<Projectile, Transform3>.Execute(Span<Projectile> projectiles, Span<Transform3> transforms, int count)
    {
        float dt = _deltaTime;
        for (int i = 0; i < count; i++)
        {
            if (projectiles[i].Spent)
            {
                // 本帧已被命中检测标记（子弹打空了）：交回销毁队列，不再推进。
                _expired.Add(projectiles[i].Owner);
                continue;
            }

            // 没有配置寿命（没有 Lifetime 组件 / 或设成 <= 0）的子弹**不推进**。
            // 这是刻意的"漏配防护"：数据忘了填寿命时，子弹停在原地立刻可见，
            // 而不是永远飞出去变成隐性 bug（既打不到人也不消失，还占着实体）。
            if (!World.TryGet<Lifetime>(projectiles[i].Owner, out Lifetime lifetime) || lifetime.Remaining <= 0f)
            {
                continue;
            }

            if (dt > 0f)
            {
                // 方向取自 Transform3 的"前方"（+Z 约定，见 Game.Actor.MoveAimSystem.LookTowards）。
                // 用旋转而不是在组件里再存一份方向：子弹的朝向本来就要给表现层（曳光/尾焰），
                // 存两份迟早会不一致。
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, transforms[i].Rotation);
                float step = projectiles[i].Speed * dt;
                transforms[i].Position += forward * step;
                projectiles[i].Traveled += step;
            }

            float range = projectiles[i].RangeMeters;
            if (range > 0f && projectiles[i].Traveled >= range)
            {
                _expired.Add(projectiles[i].Owner);
                continue;
            }

            // 寿命兜底：本系统自己倒计时（字段就在 Projectile 上，不碰通用 Lifetime）。
            // 为什么不交给 LifetimeSystem：它在 Update 组、本系统在 FixedUpdate 组，寿命到 0 发生在本帧末，
            // 而子弹的销毁要到下一个 FixedUpdate 才能做 —— 子弹会多飞一整帧，且在空窗里本系统要么
            // 看不到它（已被对方销毁），要么还看到它活着，LifetimeExpiredCount 曾经因此恒为 0。
            // 子弹有三条死法（命中 / 射程 / 寿命），全部收在一个系统里判，才能保证"销毁恰好一次、
            // 计数与销毁同源"。
            // 寿命兜底：**本系统自己递减 Lifetime.Remaining**（就地写回），不交给 LifetimeSystem。
            // 为什么不交给它：它在 Update 组、本系统在 FixedUpdate 组，寿命在 Update 归零后它当场销毁实体，
            // 下一个 FixedUpdate 本系统的查询里已经没有这颗子弹 —— 寿命判定与 LifetimeExpiredCount
            // 永远不触发（计数恒为 0，诊断信号完全骗人）。
            // 子弹有三条死法（命中 / 射程 / 寿命），全部收在一个系统里判，才能保证"销毁恰好一次、
            // 且计数与销毁同源"。相应地，宿主把 LifetimeSystem 注册到子弹所在的原型时必须跳过子弹
            // （见 LifetimeSystem 的 selfManaged 参数），否则两个系统会抢同一颗子弹。
            float remaining = lifetime.Remaining - dt;
            lifetime.Remaining = remaining;
            World.Set(projectiles[i].Owner, lifetime);
            if (remaining <= 0f)
            {
                LifetimeExpiredCount++;
                _expired.Add(projectiles[i].Owner);
                continue;
            }

            AdvancedCount++;
        }
    }
}
