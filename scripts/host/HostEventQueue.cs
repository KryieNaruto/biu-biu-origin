using System;
using System.Collections.Generic;
using Game.Core.Ecs;
using Game.Core.Events;
using Game.Core.Handles;

namespace Game.Host;

/// <summary>
/// 帧内事件排队 + 帧末统一派发（ECS.md §6 的「事件派发」阶段）。
/// <para>
/// <b>为什么需要这一层</b>：<see cref="EventBus.Publish{T}(in T)"/> 是**立即同步派发**的。
/// 若系统在 <c>Update</c> 里直接 Publish，订阅方（飘字、成就、吸血）会在「本帧命令还没提交」
/// 的时刻被唤醒 —— 此时实体可能已经被同一帧的销毁命令标记，订阅方拿到的句柄马上就要过期，
/// 而且各类订阅方的回调时机取决于系统注册顺序，无法推理。
/// </para>
/// <para>
/// ECS.md §6 把「派发」画在 <c>CommandFlush</c> **之后**的一个独立阶段，本类就是那个阶段：
/// 系统在帧内只往这里排队（<c>Enqueue</c>，值类型、零装箱），帧末由 <see cref="Dispatch"/>
/// 按入队顺序一次性 <c>Publish</c> 给 <see cref="EventBus"/>。于是「订阅方看到的世界」永远是
/// 「本帧命令已提交完毕的世界」，顺序也变成可推理的：入队顺序 == 派发顺序。
/// </para>
/// <para>
/// <b>为什么放在 scripts/host/ 而不是 Game.Core</b>：本轮写域把「帧末派发」实现限制在宿主层，
/// 且它天然绑定「渲染帧」这个宿主概念（固定步里可能一次都没跑、也可能跑了 4 次）。
/// 若将来内核需要自带时间线，可以整体下移，届时 <see cref="EventBus"/> 的签名无需改动。
/// </para>
/// <para>
/// <b>零分配策略</b>：每类事件一个 <c>List&lt;T&gt;</c>，List 复用、派发后只清计数不释放内存；
/// 稳态下入队不产生任何分配（List 容量在预热后不再增长）。
/// </para>
/// </summary>
public sealed class HostEventQueue
{
    private readonly EventBus _bus;

    private readonly List<DamageApplied> _damageApplied = new(32);  // arch-ok: 事件队列初始容量（内存预分配，不是玩法数值）
    private readonly List<HitEvent> _hits = new(32);  // arch-ok: 事件队列初始容量（内存预分配，不是玩法数值）
    private readonly List<ActorKilled> _killed = new(8);  // arch-ok: 事件队列初始容量（内存预分配，不是玩法数值）
    private readonly List<EntityDestroyed> _destroyed = new(16);  // arch-ok: 事件队列初始容量（内存预分配，不是玩法数值）
    private readonly List<LifetimeExpired> _lifetimeExpired = new(16);  // arch-ok: 事件队列初始容量（内存预分配，不是玩法数值）
    private readonly List<StatChanged> _statChanged = new(8);  // arch-ok: 事件队列初始容量（内存预分配，不是玩法数值）

    private long _enqueued;
    private long _dispatched;
    private int _lastEnqueued;

    /// <summary>构造：绑定目标事件总线。</summary>
    public HostEventQueue(EventBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    /// <summary>累计入队事件数。</summary>
    public long TotalEnqueued => _enqueued;

    /// <summary>累计派发事件数。</summary>
    public long TotalDispatched => _dispatched;

    /// <summary>最近一帧入队的事件数。</summary>
    public int LastEnqueued => _lastEnqueued;

    /// <summary>当前待派发的事件总数。</summary>
    public int PendingCount =>
        _damageApplied.Count + _hits.Count + _killed.Count + _destroyed.Count + _lifetimeExpired.Count + _statChanged.Count;

    /// <summary>入队：伤害已结算。</summary>
    public void Enqueue(in DamageApplied e)
    {
        _damageApplied.Add(e);
        _enqueued++;
        _lastEnqueued++;
    }

    /// <summary>入队：几何命中。</summary>
    public void Enqueue(in HitEvent e)
    {
        _hits.Add(e);
        _enqueued++;
        _lastEnqueued++;
    }

    /// <summary>入队：实体被击杀。</summary>
    public void Enqueue(in ActorKilled e)
    {
        _killed.Add(e);
        _enqueued++;
        _lastEnqueued++;
    }

    /// <summary>入队：实体已销毁。</summary>
    public void Enqueue(in EntityDestroyed e)
    {
        _destroyed.Add(e);
        _enqueued++;
        _lastEnqueued++;
    }

    /// <summary>入队：生命周期到期。</summary>
    public void Enqueue(in LifetimeExpired e)
    {
        _lifetimeExpired.Add(e);
        _enqueued++;
        _lastEnqueued++;
    }

    /// <summary>入队：数值变更。</summary>
    public void Enqueue(in StatChanged e)
    {
        _statChanged.Add(e);
        _enqueued++;
        _lastEnqueued++;
    }

    /// <summary>
    /// 帧末统一派发。由 <see cref="FixedFramePipeline"/> 在命令提交之后调用**一次**。
    /// <para>派发顺序固定为「实体销毁 → 击杀 → 伤害 → 命中 → 到期 → 数值」：
    /// 先让订阅方清掉悬空句柄，再让它们处理因果事件，避免订阅方访问已销毁实体。</para>
    /// </summary>
    /// <param name="onDestroyed">每个销毁事件在派发前先回调一次（宿主用来回收视图池）。</param>
    public void Dispatch(Action<ActorHandle>? onDestroyed)
    {
        for (int i = 0; i < _destroyed.Count; i++)
        {
            EntityDestroyed e = _destroyed[i];
            onDestroyed?.Invoke(e.Entity);
            _bus.Publish(in e);
            _dispatched++;
        }
        _destroyed.Clear();

        for (int i = 0; i < _killed.Count; i++)
        {
            ActorKilled e = _killed[i];
            _bus.Publish(in e);
            _dispatched++;
        }
        _killed.Clear();

        for (int i = 0; i < _damageApplied.Count; i++)
        {
            DamageApplied e = _damageApplied[i];
            _bus.Publish(in e);
            _dispatched++;
        }
        _damageApplied.Clear();

        for (int i = 0; i < _hits.Count; i++)
        {
            HitEvent e = _hits[i];
            _bus.Publish(in e);
            _dispatched++;
        }
        _hits.Clear();

        for (int i = 0; i < _lifetimeExpired.Count; i++)
        {
            LifetimeExpired e = _lifetimeExpired[i];
            _bus.Publish(in e);
            _dispatched++;
        }
        _lifetimeExpired.Clear();

        for (int i = 0; i < _statChanged.Count; i++)
        {
            StatChanged e = _statChanged[i];
            _bus.Publish(in e);
            _dispatched++;
        }
        _statChanged.Clear();

        _lastEnqueued = 0;
    }
}
