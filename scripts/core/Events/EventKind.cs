namespace Game.Core.Events;

/// <summary>
/// 纯逻辑事件的**分类标签**（docs/rules/06 §5.1/§5.2）。
/// <para>为什么需要它而不是「全部一视同仁」：两类事件的生命周期纪律完全不同 ——</para>
/// <list type="bullet">
/// <item><see cref="Notify"/>：已经发生的事实（伤害已结算、实体已销毁）。订阅方**只读副作用**，
/// 帧末统一派发，不允许反过来改帧内状态。</item>
/// <item><see cref="Command"/>：跨系统的「请求」（生成一波怪、播放一次顿帧）。
/// 必须由**命令的接收方**在下一阶段消费（通常进 <c>CommandBuffer</c>），
/// 所以它必须带 <see cref="EventOrigin"/> 标记是谁发的，接收方才能去重/限流/调试。</item>
/// </list>
/// <para>把这两类显式区分开，是为了让「谁在什么时候能被谁影响」变成可审查的东西，
/// 而不是靠每个订阅方自觉。</para>
/// </summary>
public enum EventKind : byte
{
    /// <summary>通知：已发生的事实，订阅方只读（默认值 = 安全默认，漏标不会变成命令）。</summary>
    Notify = 0,

    /// <summary>命令：请求他方执行，接收方在下一阶段消费。</summary>
    Command = 1,
}

/// <summary>
/// 事件来源：命令型事件的发出者标识。用 int 而不是对象引用 —— 命令要被<b>延后</b>处理，
/// 延后期间发出者可能已被销毁，持有引用会导致「拿到半死对象」。
/// <para>具体含义由各系统定义（可以是实体 index、系统 id、波次编号）；
/// 本类型只约束它是 <c>unmanaged</c> 的值，能进 <c>readonly record struct</c> 载荷。</para>
/// </summary>
public readonly record struct EventOrigin(int Value)
{
    /// <summary>未指定来源。0 = 安全默认值。</summary>
    public static readonly EventOrigin None = new(0);
}
