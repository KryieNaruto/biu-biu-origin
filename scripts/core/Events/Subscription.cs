using System;

namespace Game.Core.Events;

/// <summary>
/// 一次订阅的实体：持有委托与代际。失效只置位，数组槽由 <see cref="EventBus"/> 的派发后回收逻辑清理。
/// <para>为什么是**顶层 internal 类**而不是 <c>EventBus</c> 的嵌套 private 类：
/// 嵌套在 <c>EventBus</c> 里的 private 类型，其有效可访问性是 <c>private</c>；
/// 而 <see cref="EventBus.SubscriptionToken"/> 是 public 类型 —— 它的构造函数参数不能比它自己更窄，
/// 否则 CS0051。把站点提升到 internal，token 的 <c>internal</c> 构造函数才成立，
/// 同时程序集外部仍然看不到它。</para>
/// <para>为什么不让它更"聪明"（比如自带退订回调）：订阅的失效只有两条路 ——
/// 显式退订、代际失效。前者由 token 调 <see cref="TryCancel"/>，后者由派发时比对代际完成，
/// 不需要回调链，也就不需要在这里持引用回总线。</para>
/// </summary>
internal sealed class Subscription
{
    private readonly Delegate _handler;

    /// <summary>构造：代际 + 回调。回调的委托类型必须与发布时的事件类型 <c>T</c> 一致（由 <see cref="Invoke{T}"/> 校验）。</summary>
    internal Subscription(int generation, Delegate handler)
    {
        Generation = generation;
        _handler = handler;
    }

    /// <summary>订阅时所在代际；低于总线当前代际即为失效。</summary>
    internal int Generation { get; }

    /// <summary>是否已被显式退订。</summary>
    internal bool Dead { get; private set; }

    /// <summary>按事件类型派发：类型不匹配时**静默跳过**（订阅与发布类型由泛型约束保证一致，这里只做防御）。</summary>
    internal void Invoke<T>(in T payload) where T : struct, IEventPayload
    {
        if (_handler is Action<T> typed)
        {
            typed(payload);
        }
    }

    /// <summary>退订：返回是否真的从活跃状态转为失效状态（重复退订返回 false，幂等）。</summary>
    internal bool TryCancel()
    {
        if (Dead)
        {
            return false;
        }

        Dead = true;
        return true;
    }
}
