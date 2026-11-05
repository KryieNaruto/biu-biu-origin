using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using Game.Combat.Factory;
using NVector3 = System.Numerics.Vector3;

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
            transform.Position += velocity.Linear * _deltaTime;
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

    /// <summary>一条待投递的到期产物（值类型，装进定长数组零分配）。</summary>
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
