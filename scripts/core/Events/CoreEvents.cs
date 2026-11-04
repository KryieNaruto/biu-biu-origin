using Game.Core.Ecs;
using Game.Core.Handles;

namespace Game.Core.Events;

/// <summary>
/// 事件载荷的公共头：每个逻辑事件都带「谁发的 / 属于哪条通知类别」，避免每个事件各写一套来源字段。
/// <para>为什么用 <c>interface</c> + 显式实现的只读属性，而不是抽象基类：
/// C# 单继承，<c>readonly record struct</c> 必须继承 <see cref="System.ValueType"/>，
/// 所以 struct 事件只能靠接口共享形状（docs/rules/03 §3.2.2 的第 1 条理由）。</para>
/// <para>纪律：<see cref="Kind"/> 只允许 <see cref="EventKind.Notify"/>；
/// 命令型载荷另走命令缓冲（ECS 的 <c>CommandBuffer</c>），不进本总线 —— 命令要能重放，通知不需要。</para>
/// </summary>
public interface IEventPayload
{
    /// <summary>事件类别。通知型事件的实现必须返回 <see cref="EventKind.Notify"/>。</summary>
    EventKind Kind { get; }

    /// <summary>发出者标识（用于日志、调试面板与「谁导致了这一击」的因果链）。</summary>
    EventOrigin Origin { get; }

    /// <summary>安全起点：派发顺序（同帧多个事件按序号递增，回放时按序号重放即可对齐）。</summary>
    long Sequence { get; }
}

/// <summary>
/// 「伤害已结算」：**结算之后**的事实（docs/rules/06 §5.2 的示例事件在此类型化落地）。
/// <para>为什么是「已结算」而不是「请求结算」：请求结算是一次函数调用（<c>DamageResolver.Resolve</c>），
/// 用事件表达会让「到底谁在结算」变得不可查；而「已结算」是订阅方（吸血、成就、飘字、连击）唯一需要的东西 ——
/// 它们永远不需要、也不允许回头改这一笔伤害。</para>
/// <para>为什么载荷里是 <see cref="ActorHandle"/> 而不是 <c>IActor</c>：句柄是值语义、可存可比较、可进回放；
/// <c>IActor</c> 的实现类由 <c>Game.Host</c> 提供，<c>Game.Core</c> 不能依赖它（docs/rules/03 §3.3）。</para>
/// </summary>
public readonly record struct DamageApplied(
    ActorHandle Source,
    ActorHandle Target,
    float Amount,
    bool IsCrit,
    DamageType Type,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;
}

/// <summary>
/// 「命中已发生」：**几何层面**的命中（抛射物扫掠命中盒），此时还没有结算伤害。
/// <para>为什么与 <see cref="DamageApplied"/> 分开：命中与结算是两件事 ——
/// 命中可能需要做击杀判定、材质音效、穿甲判断；一旦合并，所有「只想听声音」的订阅方
/// 都会被拖进伤害语义。分开后，命中事件由命中系统发，伤害事件由结算系统发，两者顺序即管线顺序。</para>
/// </summary>
public readonly record struct HitEvent(
    ActorHandle Source,
    ActorHandle Target,
    float PositionX,
    float PositionY,
    float PositionZ,
    DamageType Type,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;
}

/// <summary>
/// 「实体被击杀」：生命值越过死亡线的**那一次**结算才会发出（同一实体不会重复发）。
/// <para>为什么把 <see cref="Killer"/> 可空：环境击杀（掉进坑、毒池）没有来源。
/// 用 <see cref="ActorHandle"/> 的 <c>None</c> 表示「无来源」，而不是可空引用类型 ——
/// struct 事件里塞 <c>ActorHandle?</c> 会让载荷不再是 blittable，得不偿失。</para>
/// </summary>
public readonly record struct ActorKilled(
    ActorHandle Victim,
    ActorHandle Killer,
    DamageType Type,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;

    /// <summary>是否有明确击杀者（环境击杀为 false）。</summary>
    public bool HasKiller => !Killer.IsNone;
}

/// <summary>
/// 「实体已销毁」：ECS 里实体被回收之后发出。
/// <para>为什么订阅方需要它：句柄（<see cref="ActorHandle"/>）只在帧内/下一帧有效，
/// 表现桥、UI 缓存、AI 记忆表都要靠这个事件清掉悬空句柄 —— 这是「句柄会过期」的
/// 唯一回收信号（ECS.md §5.1「句柄规则」）。</para>
/// </summary>
public readonly record struct EntityDestroyed(
    ActorHandle Entity,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;
}

/// <summary>
/// 「生命周期到期」：<c>Lifetime</c> 组件倒计时归零，实体即将被销毁（通常由帧末命令缓冲销毁）。
/// <para>为什么不复用 <see cref="EntityDestroyed"/>：到期是**因果**（子弹飞完射程、特效播完），
/// 销毁是**结果**。订阅方常常只关心前者（例如「这发子弹没命中也该结算命中率」），
/// 而销毁还包含手动销毁、关卡清场等无关情形。</para>
/// </summary>
public readonly record struct LifetimeExpired(
    ActorHandle Entity,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;
}

/// <summary>
/// 「夺舍开始」：玩家夺取目标已完成四件事的同一次提交（数值层、技能、外观、阵营）。
/// <para>为什么需要独立事件而不是靠订阅方自己比较状态：表现层（变身特效、镜头、UI 血条配色）
/// 需要的是**一次明确的时刻**，而不是逐帧 diff；并且它由 <c>TakeoverSystem</c> 发出，
/// 保证「四者同时生效」这件事在事件层面也是原子的。</para>
/// </summary>
public readonly record struct TakeoverStarted(
    ActorHandle Caster,
    ActorHandle Target,
    float DurationSeconds,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;
}

/// <summary>
/// 「夺舍结束」：无论是自然到期还是主动取消，都走这一个事件（docs/rules/06 §4.1 步骤 5）。
/// <para>为什么把「怎么结束的」也带上：表现层要区分「到期变回」与「中途取消」的过渡动画，
/// 但两者都必须走同一条回滚路径 —— 用一个事件 + 一个原因枚举，比两个事件少一处容易漏接的入口。</para>
/// </summary>
public readonly record struct TakeoverEnded(
    ActorHandle Caster,
    ActorHandle Target,
    TakeoverEndReason Reason,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;
}

/// <summary>夺舍结束的原因。只加不改（未来可能进统计与成就判定）。</summary>
public enum TakeoverEndReason : byte
{
    /// <summary>倒计时自然结束。</summary>
    Expired = 0,

    /// <summary>被主动取消（再次按键 / 状态被打断）。</summary>
    Cancelled = 1,

    /// <summary>宿主/目标被击杀，夺舍被迫终止。</summary>
    HostLost = 2,
}

/// <summary>
/// 「数值已变更」：某个数值层的某个槽位换了一份不可变快照（docs/rules/05 §M2）。
/// <para>为什么载荷里带 <see cref="StatLayerId"/> 而不是新旧值：事件是**通知**不是数据源 ——
/// 订阅方（UI、结算）需要读值时去 <c>IStatProvider</c> 拿最新的不可变快照，
/// 把值塞进事件会让「谁持有权威」变模糊（而且每帧都要装箱大量浮点数组）。</para>
/// </summary>
public readonly record struct StatChanged(
    ActorHandle Actor,
    StatLayerId Layer,
    EventOrigin Origin,
    long Sequence) : IEventPayload
{
    /// <inheritdoc />
    public EventKind Kind => EventKind.Notify;
}
