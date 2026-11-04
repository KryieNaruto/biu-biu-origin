using System;
using Game.Actor;
using Game.Core;
using Game.Core.Events;
using Game.Core.Handles;

namespace Game.Host;

/// <summary>
/// 宿主侧的事件出口实现：把业务层的 <see cref="HostEventSink"/> 调用翻译成**帧末队列**的入队。
/// <para>
/// <b>为什么不直接 <c>EventBus.Publish</c></b>（ECS.md §6 / <see cref="HostEventQueue"/>）：
/// 立即派发会让订阅方在「本帧命令还没提交」的时刻被唤醒，拿到即将失效的句柄。
/// 本类让系统只「描述发生了什么事」，派发时机完全由 <see cref="FixedFramePipeline"/> 决定。
/// </para>
/// <para>
/// 载荷里的 <c>Origin</c> / <c>Sequence</c> 对「通知」语义没有意义，入队阶段留默认值；
/// 需要因果链时由派发方在订阅回调里补充。
/// </para>
/// </summary>
public sealed class AppEvents : HostEventSink, IDisposable
{
    private readonly Action? _dispatch;

    /// <summary>构造。</summary>
    /// <param name="bus">事件总线（派发目标；null → 自建）。</param>
    /// <param name="dispatch">帧末派发回调（一般为空：由 <see cref="FixedFramePipeline"/> 的 dispatcher 统一驱动）。</param>
    public AppEvents(EventBus? bus = null, Action? dispatch = null)
    {
        Bus = bus ?? new EventBus();
        Queue = new HostEventQueue(Bus);
        _dispatch = dispatch;
    }

    /// <summary>事件总线。</summary>
    public EventBus Bus { get; }

    /// <summary>帧末队列（宿主在派发阶段调用 <see cref="HostEventQueue.Dispatch"/>）。</summary>
    public HostEventQueue Queue { get; }

    /// <inheritdoc />
    public void RaiseDamageApplied(ActorHandle source, ActorHandle target, float amount, bool isCrit, DamageType type)
        => Queue.Enqueue(new DamageApplied(source, target, amount, isCrit, type, default, 0L));

    /// <inheritdoc />
    public void RaiseHit(ActorHandle source, ActorHandle target, float x, float y, float z, DamageType type)
        => Queue.Enqueue(new HitEvent(source, target, x, y, z, type, default, 0L));

    /// <inheritdoc />
    public void RaiseActorKilled(ActorHandle victim, ActorHandle killer, DamageType type)
        => Queue.Enqueue(new ActorKilled(victim, killer, type, default, 0L));

    /// <inheritdoc />
    public void RaiseLifetimeExpired(ActorHandle entity)
        => Queue.Enqueue(new LifetimeExpired(entity, default, 0L));

    /// <inheritdoc />
    public void RaiseEntityDestroyed(ActorHandle entity)
        => Queue.Enqueue(new EntityDestroyed(entity, default, 0L));

    /// <summary>代管传入的派发回调（宿主在派发阶段调用：<c>Queue.Dispatch(onDestroyed)</c>）。</summary>
    public void Dispatch(Action<ActorHandle>? onDestroyed) => Queue.Dispatch(onDestroyed);

    /// <inheritdoc />
    public void Dispose()
    {
        Bus.Clear();
        _ = _dispatch;
    }
}
