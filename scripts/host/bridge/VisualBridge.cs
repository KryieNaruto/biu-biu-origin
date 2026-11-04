using System;
using Game.Core.Ecs;
using Godot;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Game.Host.Bridge;

/// <summary>
/// 视图生命周期桥：把「实体有没有视图」变成「节点池有没有租/还」。
/// <para>
/// <b>单向驱动，禁止反写</b>（ECS.md §7.3）：实体出现 → 从池里租一个视图并写回组件的
/// <see cref="View.Slot"/>；实体消失 → 把视图还回池。节点永远不能决定实体存不存在。
/// </para>
/// <para>
/// <b>为什么「生成视图」要写回组件</b>：这是唯一一处允许宿主写 ECS 的地方，理由是它是
/// <u>宿主层的视图号分配</u>，不是玩法状态（J1/J2 说的是「组件不得持有 Node」，而不是「宿主不得写组件」）。
/// 写回发生在 <c>Update</c> 的 <c>SpawnDirectorSystem</c> 之后、<c>CommandFlush</c> 之前，
/// 因此它会随同一次提交落盘，不会出现「实体活着但组件还没视图号」的中间态。
/// </para>
/// <para>
/// <b>为什么用「显式回收队列」而不是让系统各自 Return</b>：销毁命令在帧末提交，
/// 提交后实体已经不在世界上了，只有宿主还知道「它曾经拥有哪个 ViewSlot」。
/// 于是 <see cref="HostEventQueue"/> 里收到的 <c>EntityDestroyed</c> 是最好的回收信号：
/// 它带着句柄，而句柄恰好能索引到我们旁路表里的视图号。
/// </para>
/// </summary>
public sealed class VisualBridge : ISpanAction<View>
{
    private readonly ViewRegistry _registry;

    /// <summary>接口视图（自身）：避免 <c>in this</c> 把 class 装箱成只读副本。</summary>
    private readonly ISpanAction<View> _action;

    /// <summary>回收回调的接口视图（自身），与 <see cref="_action"/> 同理单独缓存。</summary>
    private readonly SpanAction<View> _reaper;

    /// <summary>回收时用的世界引用（<see cref="ReapDead"/> 每次调用前设置）。</summary>
    private World? _reapWorld;

    private readonly float _scale;
    private int[] _pendingRelease;

    private int _pendingReleaseCount;

    /// <summary>本帧新租出的视图数。</summary>
    public int LastRentedCount { get; private set; }

    /// <summary>本帧回收的视图数。</summary>
    public int LastReturnedCount { get; private set; }

    /// <summary>本帧发现「实体已死但还占着视图」的个数（诊断：稳态应为 0）。</summary>
    public int LastReapedCount { get; private set; }

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="registry">视图池。</param>
    /// <param name="scale">视图缩放（视觉尺寸，不影响逻辑）。</param>
    /// <param name="releaseCapacity">回收队列初始容量。</param>
    public VisualBridge(ViewRegistry registry, float scale, int releaseCapacity = 64)  // arch-ok: 释放队列初始容量（内存预分配，不是玩法数值）
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _scale = scale;
        _pendingRelease = new int[Math.Max(releaseCapacity, 1)];
        _action = this;
        _reaper = ReapSpan;    }

    /// <summary>
    /// 为「有 View 但还没分配槽位」的实体租视图。在视图桥阶段调用（TransformBridge 之前）。
    /// <para>
    /// <b>回调形状为什么是 <c>ForEach&lt;View&gt;</c> 而不是 <c>ForEach&lt;Entity, View&gt;</c></b>：
    /// <c>Entity</c> 是**句柄**（J1），不是可查询的组件列——<c>ForEach&lt;T0,T1&gt;</c> 内部会
    /// <c>Register&lt;T0&gt;()</c>，把 <c>Entity</c> 当组件注册会直接抛「缺少 [Identity] 字段」。
    /// 需要实体号时从组件自带的 <c>[Identity] Owner</c> 字段取（这里是 <c>views[i].Owner</c>），
    /// 这正是「实体号隐式存在于每个组件的第一个字段」这一内核约定的用法。
    /// </para>
    /// </summary>
    /// <param name="query">View 组件的查询（由宿主缓存传入，避免每帧建）。</param>
    public void RentMissing(Query query)
    {
        LastRentedCount = 0;
        query.ForEach<View>(in _action);
    }

    /// <summary>回调：给 Slot == 0 的实体分配视图。</summary>
    void ISpanAction<View>.Execute(Span<View> views, int count)
    {
        for (int i = 0; i < count; i++)
        {
            View view = views[i];
            if (view.Slot != 0)
            {
                // Slot != 0 表示已经有视图了（或该实体不需要视图），跳过。
                continue;
            }
            int id = _registry.Rent(Vector3.Zero, Quaternion.Identity, _scale);
            view.Slot = id;
            views[i] = view;
            LastRentedCount++;
        }
    }

    /// <summary>
    /// 回收「实体已经不在世界上」的视图，并清掉残留的 <see cref="View.Slot"/>。
    /// <para>
    /// <b>为什么需要它</b>：<c>EntityDestroyed</c> 事件目前在整条链路上没有生产者——
    /// <c>World.Destroy</c> 不通知任何人、<c>CommandBuffer.Flush</c> 直接调 <c>World.Destroy</c>、
    /// 而 <c>HostEventSink.RaiseEntityDestroyed</c> 没有任何调用点（<c>LifetimeSystem</c> 只发
    /// <c>LifetimeExpired</c>）。于是 <see cref="QueueRelease"/> 永远收不到东西，
    /// 视图只租不还，<c>ViewRegistry.InUseCount</c> 单调上涨（实测稳态 1 万+ 帧后涨到 2161）。
    /// </para>
    /// <para>
    /// <b>为什么用「实体存活复核」而不是继续等事件</b>：<c>View</c> 的槽位就在组件里，
    /// 「实体没了但组件槽位非 0」这个状态只有宿主看得见，而 <c>World.IsAlive</c> 本来就是
    /// 跨帧复核句柄的官方手段（ECS.md §5.1）。这条路径不依赖任何事件、不依赖别人改内核，
    /// 且天然幂等：活着的实体不动，死了的回收一次后槽位归零，下一帧不再命中。
    /// </para>
    /// <para>
    /// <b>为什么顺手把 Slot 清 0</b>：实体被销毁后如果句柄被复用（generation 不同但 index 相同），
    /// 新实体继承的组件可能带着旧槽位号，那会指向别人的视图。清 0 让 <see cref="RentMissing"/>
    /// 下一帧重新分配，杜绝「两个实体共用一个节点」。
    /// </para>
    /// <para>
    /// <b>未来简化</b>：等内核在 <c>World.Destroy</c> / <c>CommandBuffer.Flush</c> 里补上
    /// <c>EntityDestroyed</c> 的生产者（已进 ARCHITECTURE.md §12 待办），此处可简化为纯事件驱动的
    /// <see cref="QueueRelease"/> 调用。<b>现阶段不依赖事件是有意为之</b>：事件尚无生产者，
    /// 依赖它等于永不回收；而存活复核本身就是 ECS.md §5.1 对「跨帧句柄」的官方要求，
    /// 日后两条路径并存也不冲突（同一条回收队列，幂等）。
    /// </para>
    /// </summary>
    /// <param name="world">实体世界（只读查询，不改玩法状态）。</param>
    /// <param name="views">View 组件的查询（由宿主缓存传入）。</param>
    public void ReapDead(World world, Query views)
    {
        _reapWorld = world;
        LastReapedCount = 0;
        // 用委托重载而不是第二个 ISpanAction：一个类型只能实现一次 ISpanAction<View>.Execute，
        // 而租（RentMissing）和还（ReapDead）是两种不同的遍历语义，不能共用一个方法体。
        views.ForEach<View>(ReapSpan);
    }

    /// <summary>回调：落在死实体上的视图归还池子。</summary>
    /// <param name="views">View 列的 span（已是本 chunk 的有效范围）。</param>
    private void ReapSpan(Span<View> views)
    {
        if (_reapWorld is null)
        {
            return;
        }
        for (int i = 0; i < views.Length; i++)
        {
            View view = views[i];
            if (view.Slot == 0)
            {
                continue;
            }
            if (_reapWorld.IsAlive(view.Owner))
            {
                continue;
            }
            QueueRelease(view.Slot);
            view.Slot = 0;
            views[i] = view;
            LastReapedCount++;
        }
    }

    /// <summary>
    /// 记一个待回收的视图号。由 <see cref="ReapDead"/>（实体存活复核）驱动；
    /// 将来内核补上 <c>EntityDestroyed</c> 生产者后，也可由事件驱动，两条路径共用同一队列。
    /// 队列是值类型的，稳态零分配。
    /// </summary>
    /// <param name="viewSlot">视图槽位号（&lt;= 0 忽略）。</param>
    public void QueueRelease(int viewSlot)
    {
        if (viewSlot <= 0)
        {
            return;
        }
        if (_pendingReleaseCount >= _pendingRelease.Length)
        {
            Array.Resize(ref _pendingRelease, _pendingRelease.Length * 2);
        }
        _pendingRelease[_pendingReleaseCount++] = viewSlot;
    }

    /// <summary>把回收队列里的视图全部还回池。在视图桥阶段的最后调用一次。</summary>
    public void FlushReleases()
    {
        LastReturnedCount = 0;
        for (int i = 0; i < _pendingReleaseCount; i++)
        {
            _registry.Return(_pendingRelease[i]);
        }
        LastReturnedCount = _pendingReleaseCount;
        _pendingReleaseCount = 0;
    }
}
