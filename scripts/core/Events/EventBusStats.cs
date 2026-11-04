using System;

namespace Game.Core.Events;

/// <summary>
/// 事件总线的只读统计：交给性能 HUD 与测试用（`ARCHITECTURE.md` §9 的性能 HUD 需要「一帧派发了多少事件」）。
/// <para>为什么单独一个类型而不是往 <c>EventBus</c> 上挂一堆 <c>public long</c> 字段：
/// 统计是**只读视图**，不能让订阅者顺手改计数（那会让「派发次数」失去诊断价值）。
/// 计数只用 <c>long</c> 递增 —— 单帧递增不会溢出，回放/长时间运行也不会。</para>
/// </summary>
public sealed class EventBusStats
{
    private long _published;
    private long _subscribed;
    private long _invalidated;
    private long _faults;

    /// <summary>最近一次订阅者异常的文本（进日志用；为空表示从未发生）。</summary>
    public string LastFault { get; private set; } = string.Empty;

    /// <summary>累计派发次数（所有事件类型之和）。</summary>
    public long Published => _published;

    /// <summary>累计订阅次数。</summary>
    public long Subscribed => _subscribed;

    /// <summary>累计代际失效次数（每次切场景 +1）。</summary>
    public long InvalidatedGenerations => _invalidated;

    /// <summary>累计「订阅者抛异常被隔离」的次数。稳态必须是 0；非 0 说明某个订阅方有 bug。</summary>
    public long SubscriberFaults => _faults;

    internal void NotifyPublished() => _published++;

    internal void NotifySubscribed() => _subscribed++;

    internal void NotifyGenerationInvalidated() => _invalidated++;

    internal void NotifySubscriberFault(Exception ex)
    {
        _faults++;
        LastFault = ex.GetType().Name + ": " + ex.Message;
    }
}
