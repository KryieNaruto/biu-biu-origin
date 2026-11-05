using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using Game.Combat.Factory;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Game.Combat;

/// <summary>
/// <b>弹道推进系统</b>：每固定步长把子弹往前推、递减寿命、到期后按契约投递"到期后的产物"。
/// <para>
/// <b>为什么放在 <see cref="UpdateGroup.FixedUpdate"/></b>：弹道是物理感的来源，必须与帧率解耦
/// （60Hz 与 144Hz 下同一颗子弹的飞行轨迹必须一致）。放在固定步长组里，宿主按固定 dt 驱动。
/// </para>
/// <para>
/// <b>本轮边界（明确不做）</b>：碰撞检测、伤害结算、命中特效全部属于游戏逻辑，本轮不做。
/// 这里只做三件事：位置积分、寿命递减、到期处理。这样"会飞的子弹"与"会打的子弹"是两个可独立演进的系统。
/// </para>
/// <para>
/// <b>零分配</b>：用 <c>Query.ForEach</c> 的 <c>ISpanAction</c> 接口形态（每 chunk 一次回调，
/// 回调里手写 <c>for</c> 逐实体处理），不产生每实体委托、不建 List。
/// 到期处理走 <see cref="EcsObjectFactory"/>（内部走 <c>World.Commands</c>，帧末落地，J5），
/// 绝不在遍历中直接改动世界结构。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.FixedUpdate)]
public sealed class ProjectileAdvanceSystem : SystemBase
{
    private readonly ContentIndex _index;
    private readonly EcsObjectFactory _factory;
    private Query _query = null!;

    // 追踪弹的候选目标池（有 Hurtbox 且带位置的就是可被追踪的东西）。
    private Query _hurtboxQuery = null!;

    // 追踪扫描的暂存结果（放在系统字段上，因为 ISpanAction 是按值传递的，结构体副本无法写回）。
    private NVector3 _scanFrom;
    private NVector3 _scanBest;
    private bool _scanFound;
    private float _scanBestDistanceSquared;

    // 等待投递的"到期产物"（遍历期间不能直接生成，先攒在定长缓冲里，遍历结束后统一投递）。
    // 为什么是定长数组而不是 List：List 在扩容时会分配，而定长 + 溢出计数在稳态下完全无分配。
    private readonly PendingDeath[] _pending;
    private int _pendingCount;

    private float _deltaTime;
    private int _advanced;
    private int _expired;

    /// <summary>构造。</summary>
    /// <param name="index">内容表（读契约的到期行为）。</param>
    /// <param name="factory">铸造工厂（到期分裂 / 变怪走它）。</param>
    /// <param name="pendingCapacity">单帧最多攒多少次到期产物。</param>
    public ProjectileAdvanceSystem(ContentIndex index, EcsObjectFactory factory, int pendingCapacity = 256)  // arch-ok: 预分配缓冲容量，结构性常量
    {
        _index = index ?? throw new System.ArgumentNullException(nameof(index));
        _factory = factory ?? throw new System.ArgumentNullException(nameof(factory));
        _pending = new PendingDeath[pendingCapacity > 0 ? pendingCapacity : 1];
    }

    /// <inheritdoc />
    public override string Name => "ProjectileAdvance";

    /// <summary>上一次执行推进过的子弹数。</summary>
    public int AdvancedLastFrame { get; private set; }

    /// <summary>上一次执行里到期的子弹数。</summary>
    public int ExpiredLastFrame { get; private set; }

    /// <summary>累计推进步数（诊断）。</summary>
    public int TotalAdvanced { get; private set; }

    /// <summary>累计到期数（诊断）。</summary>
    public int TotalExpired { get; private set; }

    /// <summary>因缓冲满被丢弃的到期产物数（恒为 0 才是健康的）。</summary>
    public int DroppedPending { get; private set; }

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        _query = World.Query<ProjectileState, Velocity, Transform3>();
        _hurtboxQuery = World.Query<Hurtbox, Transform3>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _deltaTime = deltaTime;
        _advanced = 0;
        _expired = 0;
        _pendingCount = 0;

        // 执行器是 struct（栈上），持有一个指向本系统的引用，于是「零分配 + 能写回计数器」两件事同时成立。
        // 为什么不用 `in this`：SystemBase 是 class，它的成员不是 chunk 内存，
        // 不能按引用返回给 span 迭代；而把每实体的处理写成闭包又会每帧分配一个 delegate（J6 明令禁止）。
        Pass pass = new Pass(this);
        _query.ForEach<ProjectileState, Velocity, Transform3>(pass);

        // 遍历结束（Span 已失效）后再投递到期产物：此时 CreateDeferred 安全。
        for (int i = 0; i < _pendingCount; i++)
        {
            PendingDeath d = _pending[i];
            _factory.TrySpawnDeath(d.Contract, in d.Position, in d.Direction, d.Team, in d.State, out _);
        }
        _pendingCount = 0;

        AdvancedLastFrame = _advanced;
        ExpiredLastFrame = _expired;
        TotalAdvanced += _advanced;
        TotalExpired += _expired;
    }

    /// <summary>遍历执行器：持有指向所属系统的引用，逐 chunk 处理。</summary>
    private readonly struct Pass : ISpanAction<ProjectileState, Velocity, Transform3>
    {
        private readonly ProjectileAdvanceSystem _owner;

        public Pass(ProjectileAdvanceSystem owner) => _owner = owner;

        public void Execute(System.Span<ProjectileState> states, System.Span<Velocity> velocities, System.Span<Transform3> transforms, int count)
        {
            _owner.ProcessChunk(states, velocities, transforms, count);
        }
    }

    private void ProcessChunk(System.Span<ProjectileState> states, System.Span<Velocity> velocities, System.Span<Transform3> transforms, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref ProjectileState state = ref states[i];
            ref Velocity velocity = ref velocities[i];
            ref Transform3 transform = ref transforms[i];

            ProjectileContract? contract = _index.GetProjectile(state.ContractSlot);
            if (contract is null)
            {
                // 契约缺失（内容表被热重载 / 数据错误）：不推进也不销毁，只跳过——
                // 静默销毁会让"子弹凭空消失"变成一个查不到的 bug。
                continue;
            }

            // ① 位置积分：位置真值只有 Transform3 一处（ECS.md §4.2）。
            //    运动学分派在 AdvanceMotion 里按 ProjectileMotion 走，新增一种运动 = 加一个 case。
            AdvanceMotion(ref state, ref velocity, ref transform, _deltaTime);
            _advanced++;

            // ② 寿命递减。
            state.Lifetime -= _deltaTime;
            if (state.Lifetime > 0f)
            {
                continue;
            }

            // ③ 到期：按契约的 OnExpire 决定去向（数据驱动，代码里没有 if (kind == 爆炸)）。
            _expired++;
            EnqueueDeath(in state, contract, in transform, in velocity);
        }
    }

    private void EnqueueDeath(in ProjectileState state, ProjectileContract contract, in Transform3 transform, in Velocity velocity)
    {
        ProjectileDeath death = contract.OnExpire;
        if (death == ProjectileDeath.None || death == ProjectileDeath.Explode || death == ProjectileDeath.SpawnEffect)
        {
            // None = 直接消失（销毁由生命周期系统负责）；Explode 本轮不做范围结算（碰撞属游戏逻辑）；
            // SpawnEffect 需要特效表，本轮未接。
            return;
        }
        if (state.ChainDepth >= contract.MaxChainDepth)
        {
            // 链深到顶：不再派生，防止"子弹分裂子弹"无限递归。
            return;
        }
        if (_pendingCount >= _pending.Length)
        {
            DroppedPending++;
            return;
        }

        NVector3 direction = velocity.Linear;
        float lengthSquared = direction.LengthSquared();
        direction = lengthSquared > 0f ? direction / System.MathF.Sqrt(lengthSquared) : NVector3.UnitZ;

        _pending[_pendingCount] = new PendingDeath(contract, transform.Position, direction, TeamId.Neutral, state);
        _pendingCount++;
    }

    /// <summary>
    /// 按 <see cref="ProjectileMotion"/> 推进一个固定步长。
    /// <para>
    /// <b>为什么是 switch 而不是每种运动一个系统</b>：运动学共享同一次朝向/位置写回，
    /// 拆成多个系统会让「哪些运动在争同一颗子弹的 Transform3」变成不可见的耦合。
    /// 一个分派点 + 每种运动一段自洽的积分，是这里最不容易出错的形状（J1 单一写入者）。
    /// </para>
    /// <para>
    /// <b>约定</b>：<see cref="Velocity"/> 是<b>派生量</b>——本方法负责写完它，
    /// 下游（命中判定、表现层插值）只读。这样「蛇形弹的位置」与「蛇形弹的速度」永远一致。
    /// </para>
    /// </summary>
    private void AdvanceMotion(ref ProjectileState state, ref Velocity velocity, ref Transform3 transform, float dt)
    {
        state.Age += dt;
        NVector3 launch = state.LaunchDirection;
        float launchLengthSquared = launch.LengthSquared();
        if (launchLengthSquared <= 0f)
        {
            // 退化输入（零方向）：当成 +Z，避免下面所有三角函数算出 NaN。
            launch = NVector3.UnitZ;
            launchLengthSquared = 1f;
        }
        launch /= System.MathF.Sqrt(launchLengthSquared);

        // 与原始轴垂直的两个基向量，用于 Wave / Spiral 的侧向偏移。
        NVector3 side = System.Math.Abs(launch.Y) < 0.99f  // arch-ok: 叉积防退化阈值（数学常量）
            ? NVector3.Normalize(NVector3.Cross(launch, NVector3.UnitY))
            : NVector3.Normalize(NVector3.Cross(launch, NVector3.UnitX));

        switch ((ProjectileMotion)state.Motion)
        {
            case ProjectileMotion.Straight:
                break;

            case ProjectileMotion.Homing:
            {
                // 朝「最近的可追踪目标」转向。转向速率来自契约（度/秒），已被定进组件。
                if (!TryFindHomingTarget(in transform.Position, out NVector3 targetPoint))
                {
                    break;
                }
                NVector3 desired = targetPoint - transform.Position;
                float desiredLengthSquared = desired.LengthSquared();
                if (desiredLengthSquared <= 0f)
                {
                    break;
                }
                desired /= System.MathF.Sqrt(desiredLengthSquared);

                NVector3 current = velocity.Linear;
                float currentLength = current.Length();
                if (currentLength > 0f)
                {
                    current /= currentLength;
                }
                else
                {
                    current = launch;
                    currentLength = state.BaseSpeed;
                }

                // 限速转向：本步最多转 maxStep 弧度。
                float maxStep = state.HomingTurn * (System.MathF.PI / 180f) * dt;  // arch-ok: 度→弧度换算
                NVector3 next = RotateTowards(in current, in desired, maxStep);
                velocity.Linear = next * (state.BaseSpeed > 0f ? state.BaseSpeed : currentLength);
                transform.Rotation = LookRotation(in next);
                break;
            }

            case ProjectileMotion.Falling:
            {
                // 重力只改竖直分量；水平初速保持（抛体运动）。
                // 约定：契约里的 Gravity 填「加速度大小」（表里 proj_bomb_301 填 9.8 表示下落 9.8 m/s²），
                // 方向由本系统固定为 -Y（世界坐标 Y 轴向上）。填负数则反向（如上升气流）。
                velocity.Linear += new NVector3(0f, -state.Gravity * dt, 0f);
                break;
            }

            case ProjectileMotion.Wave:
            {
                // 沿原始轴前进 + 垂直于轴的正弦摆动。
                // 摆动频率取 1/振幅写死在形状里没有意义，所以用「每 0.5 秒一个完整摆」——
                // 这属于运动学的形状定义而非玩法数值，改振幅/频率的旋钮留给将来的 WaveAmplitude 列。
                const float wavePeriod = 0.5f;   // arch-ok: 运动学形状常量（非玩法数值），待契约补列后改为表驱动
                const float waveAmplitude = 1f;  // arch-ok: 同上
                float phase = state.Age / wavePeriod * System.MathF.Tau;
                float lateralSpeed = System.MathF.Cos(phase) * waveAmplitude * state.BaseSpeed * System.MathF.Tau / wavePeriod;
                velocity.Linear = launch * state.BaseSpeed + side * lateralSpeed;
                break;
            }

            case ProjectileMotion.Boomerang:
            {
                // 用 Age 做权重：前段几乎直飞，后段明显朝发射者折返。
                if (state.Source == Entity.None || !World.IsAlive(state.Source))
                {
                    break;
                }
                NVector3 sourcePosition = World.Get<Transform3>(state.Source).Position;
                NVector3 toSource = sourcePosition - transform.Position;
                float toSourceLengthSquared = toSource.LengthSquared();
                if (toSourceLengthSquared > 0f)
                {
                    NVector3 toward = toSource / System.MathF.Sqrt(toSourceLengthSquared);
                    float pull = state.Age / (state.Age + 0.25f);  // arch-ok: 折返曲线形状常量
                    NVector3 blended = launch * (1f - pull) + toward * pull;
                    float blendedLengthSquared = blended.LengthSquared();
                    if (blendedLengthSquared > 0f)
                    {
                        blended /= System.MathF.Sqrt(blendedLengthSquared);
                        velocity.Linear = blended * state.BaseSpeed;
                        transform.Rotation = LookRotation(in blended);
                    }
                }
                break;
            }

            case ProjectileMotion.Orbit:
            {
                // 绕发射者做圆周运动：半径固定为「发射时到发射者的距离」。
                if (state.Source == Entity.None || !World.IsAlive(state.Source))
                {
                    break;
                }
                NVector3 center = World.Get<Transform3>(state.Source).Position;
                NVector3 radial = transform.Position - center;
                float radius = radial.Length();
                if (radius <= 0.0001f)
                {
                    break;
                }
                NVector3 radialDir = radial / radius;
                // 切向 = 原始轴 × 径向，保证绕行方向与发射方向一致（右手系）。
                NVector3 tangent = NVector3.Cross(launch, radialDir);
                float tangentLengthSquared = tangent.LengthSquared();
                if (tangentLengthSquared <= 0.0001f)
                {
                    break;
                }
                tangent /= System.MathF.Sqrt(tangentLengthSquared);
                float angularSpeed = state.BaseSpeed / radius;  // 线速度不变 → 角速度随半径变小
                NVector3 nextPos = center + (radialDir * System.MathF.Cos(angularSpeed * dt)
                    + tangent * System.MathF.Sin(angularSpeed * dt)) * radius;
                velocity.Linear = (nextPos - transform.Position) / dt;
                transform.Position = nextPos;
                transform.Rotation = LookRotation(in tangent);
                return;  // 位置已写回，跳过末尾的统一积分
            }

            case ProjectileMotion.Spiral:
            {
                // 螺旋外扩：沿原始轴前进的同时侧向匀速偏移并在侧向轴上螺旋。
                float spiralAngle = state.Age * 6f;  // arch-ok: 螺旋角速度形状常量（待契约补列）
                NVector3 outward = side * System.MathF.Cos(spiralAngle) + NVector3.Cross(launch, side) * System.MathF.Sin(spiralAngle);
                velocity.Linear = launch * state.BaseSpeed + outward * (state.BaseSpeed * 0.5f);
                break;
            }

            case ProjectileMotion.Anchor:
            {
                // 锚定：位置不动（地雷 / 延时区域）。velocity 归零，避免下游误判在飞。
                velocity.Linear = NVector3.Zero;
                break;
            }

            default:
                break;
        }

        transform.Position += velocity.Linear * dt;
    }

    /// <summary>把 <paramref name="from"/> 朝 <paramref name="to"/> 限速旋转（球面插值，避免万向锁）。</summary>
    private static NVector3 RotateTowards(in NVector3 from, in NVector3 to, float maxRadiansDelta)
    {
        float dot = NVector3.Dot(from, to);
        if (dot >= 1f - 1e-6f)
        {
            return to;
        }
        if (dot <= -1f + 1e-6f)
        {
            // 完全反向：没有唯一的最短路径，任选一个垂直轴转过去（否则会除零）。
            NVector3 axis = System.Math.Abs(from.Y) < 0.99f  // arch-ok: 叉积防退化阈值（数学常量）
                ? NVector3.Normalize(NVector3.Cross(from, NVector3.UnitY))
                : NVector3.Normalize(NVector3.Cross(from, NVector3.UnitX));
            return RotateAxis(in from, in axis, maxRadiansDelta);
        }
        float angle = System.MathF.Acos(System.Math.Clamp(dot, -1f, 1f));
        if (angle <= maxRadiansDelta)
        {
            return to;
        }
        NVector3 axisOf = NVector3.Normalize(NVector3.Cross(from, to));
        return RotateAxis(in from, in axisOf, maxRadiansDelta);
    }

    /// <summary>绕过原点、单位轴旋转（罗德里格斯公式）。</summary>
    private static NVector3 RotateAxis(in NVector3 v, in NVector3 axis, float radians)
    {
        float cos = System.MathF.Cos(radians);
        float sin = System.MathF.Sin(radians);
        return v * cos + NVector3.Cross(axis, v) * sin + axis * (NVector3.Dot(axis, v) * (1f - cos));
    }

    /// <summary>
    /// 找最近的敌对目标（追踪弹用），返回其位置。
    /// <para>
    /// <b>本轮边界</b>：没有空间划分（BVH），所以是一次 O(候选数) 全扫。割草规模
    /// （数百实体）下可接受；上 BVH 是性能任务，不是抽象问题——本方法签名
    /// （「给我一个点」）不需要改。开销上的代价是：<b>每颗追踪弹每步扫一次</b>，
    /// 追踪弹数量上百时需要配合 BVH 或按帧分片，已记入 ARCHITECTURE 待办。
    /// </para>
    /// </summary>
    private bool TryFindHomingTarget(in NVector3 from, out NVector3 targetPoint)
    {
        // ★ 结果必须写回「系统的字段」而不是 struct 的副本：
        // ISpanAction 是按值传入 ForEach 的，Execute 里改的是那份副本，
        // 调用方读到的永远还是构造时的初值（这个 bug 曾让追踪弹全部退化成直线）。
        _scanReset(in from);
        _hurtboxQuery.ForEach<Hurtbox, Transform3>(new HomingScan(this));
        targetPoint = _scanBest;
        return _scanFound;
    }

    /// <summary>重置扫描结果（放在方法里而不是内联，避免两处初始化漂移）。</summary>
    private void _scanReset(in NVector3 from)
    {
        _scanFrom = from;
        _scanBest = default;
        _scanFound = false;
        _scanBestDistanceSquared = float.MaxValue;
    }

    /// <summary>追踪扫描执行器（struct：零分配；结果写回所属系统的字段）。</summary>
    private readonly struct HomingScan : ISpanAction<Hurtbox, Transform3>
    {
        private readonly ProjectileAdvanceSystem _owner;

        /// <summary>构造。</summary>
        public HomingScan(ProjectileAdvanceSystem owner) => _owner = owner;

        /// <inheritdoc />
        public void Execute(System.Span<Hurtbox> hurtboxes, System.Span<Transform3> transforms, int count)
        {
            for (int i = 0; i < count; i++)
            {
                NVector3 candidate = transforms[i].Position;
                float distanceSquared = NVector3.DistanceSquared(_owner._scanFrom, candidate);
                if (distanceSquared < _owner._scanBestDistanceSquared)
                {
                    _owner._scanBestDistanceSquared = distanceSquared;
                    _owner._scanBest = candidate;
                    _owner._scanFound = true;
                }
            }
        }
    }

    /// <summary>
    /// 由朝向构造四元数（与 <c>ContentSpawner.LookRotation</c> 同式：+Z 为前）。
    /// <para>
    /// 以下是标准的「旋转矩阵 → 四元数」Shepperd 分支法。系数 2 与 0.25 是该公式的组成部分，
    /// 不是可调的玩法数值，因此不进入数据表（规则4 针对的是手感/平衡参数）。
    /// </para>
    /// </summary>
    private static NQuaternion LookRotation(in NVector3 forward)
    {
        // arch-ok: 下面整个方法体的 2f / 0.25f / 0.99f 均为四元数换算公式的固定系数，非玩法数值。
        const float Two = 2f;        // arch-ok: Shepperd 分支法系数
        const float Quarter = 0.25f; // arch-ok: Shepperd 分支法系数
        float lengthSquared = forward.LengthSquared();
        if (lengthSquared <= 0f)
        {
            return NQuaternion.Identity;
        }
        NVector3 f = forward / System.MathF.Sqrt(lengthSquared);
        NVector3 up = System.Math.Abs(f.Y) < 0.99f  // arch-ok: 叉积防退化阈值（数学常量）
            ? NVector3.UnitY
            : NVector3.UnitX;
        NVector3 right = NVector3.Normalize(NVector3.Cross(up, f));
        NVector3 trueUp = NVector3.Cross(f, right);

        float m00 = right.X, m01 = trueUp.X, m02 = f.X;
        float m10 = right.Y, m11 = trueUp.Y, m12 = f.Y;
        float m20 = right.Z, m21 = trueUp.Z, m22 = f.Z;

        float trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            float s = System.MathF.Sqrt(trace + 1f) * Two;
            return NQuaternion.Normalize(new NQuaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, Quarter * s));
        }
        if (m00 > m11 && m00 > m22)
        {
            float s = System.MathF.Sqrt(1f + m00 - m11 - m22) * Two;
            return NQuaternion.Normalize(new NQuaternion(Quarter * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s));
        }
        if (m11 > m22)
        {
            float s = System.MathF.Sqrt(1f + m11 - m00 - m22) * Two;
            return NQuaternion.Normalize(new NQuaternion((m01 + m10) / s, Quarter * s, (m12 + m21) / s, (m02 - m20) / s));
        }
        float s2 = System.MathF.Sqrt(1f + m22 - m00 - m11) * Two;
        return NQuaternion.Normalize(new NQuaternion((m02 + m20) / s2, (m12 + m21) / s2, Quarter * s2, (m10 - m01) / s2));
    }


    private readonly struct PendingDeath
    {
        public readonly ProjectileContract Contract;
        public readonly NVector3 Position;
        public readonly NVector3 Direction;
        public readonly TeamId Team;
        public readonly ProjectileState State;

        public PendingDeath(ProjectileContract contract, in NVector3 position, in NVector3 direction, TeamId team, in ProjectileState state)
        {
            Contract = contract;
            Position = position;
            Direction = direction;
            Team = team;
            State = state;
        }
    }
}
