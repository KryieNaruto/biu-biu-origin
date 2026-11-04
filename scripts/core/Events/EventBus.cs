using System;
using System.Collections.Generic;

namespace Game.Core.Events;

/// <summary>
/// 类型安全的 C# 事件总线：`Game.Core` 的公共件（docs/rules/03 §4.3、docs/rules/06 §5.2）。
/// <para><b>三条硬规则（违反任意一条就等于回到「字符串 + object」的老路，即反模式 A8）：</b></para>
/// <list type="number">
/// <item><b>泛型 + 值类型载荷</b>：<see cref="Publish{T}"/> 只接受实现了 <see cref="IEventPayload"/> 的
/// <c>struct</c>，编译期就能拦住「把 object 塞进来」。</item>
/// <item><b>订阅代际</b>：每次订阅挂在某一代上。切场景/重开关卡时调 <see cref="InvalidateGeneration"/>
/// 让该代及更早的订阅一次性失效 —— 这是对付「忘了退订」的唯一有效手段（忘了是必然，不会是意外）。</item>
/// <item><b>派发期间不可增删订阅</b>：<see cref="Publish{T}"/> 期间调 Subscribe/Unsubscribe/
/// InvalidateGeneration 会抛 <see cref="GameCoreException"/>（<c>EventBusMisuse</c>）。
/// 原因很实际：派发中列表变化会让「本轮该不该调用新订阅者」变成未定义行为，这类 bug 极难复现。
/// 需要改订阅的订阅者，应把改动排到帧末队列（宿主在命令提交后统一处理）。</item>
/// </list>
/// <para><b>为什么不直接用 <c>C# event</c></b>：<c>event</c> 只能被声明它的类触发，而本项目的发布者（系统）
/// 与订阅者（表现层/UI/成就）在装配期的对象图上并不相邻；并且 <c>event</c> 没有代际失效，场景重载后必然泄漏。</para>
/// <para><b>线程</b>：只在主线程使用（docs/rules/06 §5.2「线程」一行）。这里刻意不加锁 ——
/// 这不是性能取舍，而是纪律：锁会掩盖「在 job 回调里发事件」这个结构错误。</para>
/// </summary>
public sealed class EventBus
{
    private readonly Dictionary<Type, List<Subscription>> _channels = new(16);
    private readonly EventBusStats _stats = new();

    private int _generation;
    private int _dispatchDepth;
    private long _sequence;

    /// <summary>当前订阅代际：新订阅默认挂在这一代上。</summary>
    public int Generation => _generation;

    /// <summary>只读统计（发布计数 / 派发失败计数 / 失效次数）。</summary>
    public EventBusStats Stats => _stats;

    /// <summary>累计派发过的事件条数（回放/调试面板对照用）。</summary>
    public long Sequence => _sequence;

    /// <summary>
    /// 发布一个通知型事件：按订阅顺序逐个调用本代际的订阅者。
    /// <para>载荷按值传递（<c>in</c>）：订阅者拿到快照副本，即使派发途中别处改了源数据也不影响。
    /// 这让「一帧内多次事件」的顺序语义明确 —— 事件是历史记录，不是可变状态。</para>
    /// </summary>
    /// <typeparam name="T">事件类型：必须是实现 <see cref="IEventPayload"/> 的 struct。</typeparam>
    /// <param name="payload">事件载荷。</param>
    public void Publish<T>(in T payload) where T : struct, IEventPayload
    {
        _sequence++;
        _stats.NotifyPublished();

        if (!_channels.TryGetValue(typeof(T), out List<Subscription>? list) || list.Count == 0)
        {
            return;
        }

        _dispatchDepth++;
        try
        {
            // 正序遍历 + 先取长度：本轮要调用的就是「派发开始时刻」的活跃订阅者，
            // 不会因为某个回调在派发中退订而改变本轮其余调用。
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                Subscription sub = list[i];
                if (sub.Dead || sub.Generation < _generation)
                {
                    continue;
                }

                try
                {
                    sub.Invoke(in payload);
                }
                catch (Exception ex)
                {
                    // 一个订阅者炸掉不该拖垮同帧其它订阅者：记容错计数并继续派发。
                    // 但不吞掉 —— 宿主可以读 Stats.SubscriberFaults 决定断言/打印。
                    _stats.NotifySubscriberFault(ex);
                }
            }
        }
        finally
        {
            _dispatchDepth--;
        }

        // 派发结束后回收已失效订阅：列表长度与「还在用的事件」同阶，每帧回收代价可忽略。
        Compact(list);
    }

    /// <summary>
    /// 订阅：返回 <see cref="SubscriptionToken"/>，可用它精确退订（也可以完全依赖代际失效整体回收）。
    /// </summary>
    /// <param name="handler">回调。捕获了场景对象的 lambda 必须配合代际失效使用。</param>
    /// <param name="generation">挂在哪一代；默认 -1 表示「当前代」。传旧代际 = 立刻是死订阅（测试用）。</param>
    public SubscriptionToken Subscribe<T>(Action<T> handler, int generation = -1) where T : struct, IEventPayload
    {
        if (handler is null)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.InvalidArgument, "EventBus.Subscribe", "handler 不能为 null");
        }

        ThrowIfDispatching("EventBus.Subscribe");

        int gen = generation < 0 ? _generation : generation;
        if (!_channels.TryGetValue(typeof(T), out List<Subscription>? list))
        {
            list = new List<Subscription>(4);
            _channels.Add(typeof(T), list);
        }

        Subscription sub = new(gen, handler);
        list.Add(sub);
        _stats.NotifySubscribed();

        return new SubscriptionToken(sub);
    }

    /// <summary>精确退订。重复退订是幂等的（返回 false），因为调用点常在 <c>Dispose</c> 里，可能被执行两次。</summary>
    public bool Unsubscribe(in SubscriptionToken token) => token.TryCancel();

    /// <summary>
    /// 让当前代际的**全部订阅**一次性失效，然后推进代际（返回新代际号）。
    /// <para>触发时机：切场景、重开关卡、世界重建。把「忘了退订」变成无害操作是这条纪律存在的全部理由。</para>
    /// </summary>
    public int InvalidateGeneration()
    {
        ThrowIfDispatching("EventBus.InvalidateGeneration");
        _generation++;
        _stats.NotifyGenerationInvalidated();
        return _generation;
    }

    /// <summary>某事件类型当前的活跃订阅数（调试/测试用）。</summary>
    public int SubscriberCount<T>() where T : struct, IEventPayload
    {
        if (!_channels.TryGetValue(typeof(T), out List<Subscription>? list))
        {
            return 0;
        }

        int live = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Dead && list[i].Generation >= _generation)
            {
                live++;
            }
        }

        return live;
    }

    /// <summary>清空所有订阅。只在「整个世界重建」时用；日常切场景请用 <see cref="InvalidateGeneration"/>。</summary>
    public void Clear()
    {
        ThrowIfDispatching("EventBus.Clear");
        foreach (List<Subscription> list in _channels.Values)
        {
            list.Clear();
        }
    }

    private void ThrowIfDispatching(string operation)
    {
        if (_dispatchDepth > 0)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.EventBusMisuse,
                operation,
                "派发过程中不允许增删订阅；请把订阅变更排到帧末队列（命令提交之后）执行");
        }
    }

    private static void Compact(List<Subscription> list)
    {
        int write = 0;
        for (int read = 0; read < list.Count; read++)
        {
            Subscription sub = list[read];
            if (sub.Dead)
            {
                continue;
            }

            if (write != read)
            {
                list[write] = sub;
            }

            write++;
        }

        if (write < list.Count)
        {
            list.RemoveRange(write, list.Count - write);
        }
    }

    /// <summary>
    /// 订阅句柄：可精确退订，且在代际失效后自动变成空操作。
    /// <para>struct + 指向订阅站点 → 免分配；退订通过站点写回，所以 token 本身可以自由拷贝。</para>
    /// <para>为什么不直接用 <c>event -=</c>：委托相等是「同一目标 + 同一方法」，lambda 每次订阅都是新对象，
    /// 减不掉旧的那个。token 是**身份**而不是值比较 —— 这才是「不会漏退订」的原因。</para>
    /// </summary>
    public readonly struct SubscriptionToken
    {
        private readonly Subscription? _subscription;

        /// <summary>
        /// 构造：只由 <see cref="Subscribe{T}"/> 使用。
        /// <para>为什么是 <c>internal</c> 而不是 <c>public</c>：<see cref="Subscription"/> 已经是顶层
        /// <c>internal</c> 类型，token 的 public 构造函数同样会触发 CS0051；改成 internal 后
        /// 「谁能造 token」严格等于「本程序集」，外部只能通过 <see cref="Subscribe{T}"/> 拿到。</para>
        /// </summary>
        internal SubscriptionToken(Subscription subscription)
        {
            _subscription = subscription;
        }

        /// <summary>是否仍然有效（未退订且未被代际失效）。</summary>
        public bool IsAlive => _subscription is not null && !_subscription.Dead;

        /// <summary>退订：返回是否真的从活跃状态转为失效状态（重复退订返回 false）。</summary>
        public bool TryCancel() => _subscription is not null && _subscription.TryCancel();
    }
}

