using System;
using System.Collections.Generic;
using Game.Core.Ecs;
using Game.Core.Handles;

namespace Game.Actor;

/// <summary>
/// 生命周期系统：把 <see cref="Lifetime.Remaining"/> 递减到 0，然后**投递销毁命令**
/// （绝不直接销毁 —— 遍历中销毁会破坏正在被遍历的 chunk，J5）。
/// <para>
/// <b>为什么在 Update 而不是 FixedUpdate</b>：倒计时是「每帧逻辑」，与 ECS.md §6 的分组语义一致
/// （FixedUpdate 给位移/AI 这类需要确定积分的量，Update 给计时/冷却/结算）。
/// 若将来要求严格确定性，把它移到 FixedUpdate 即可，代码只改一个特性。
/// </para>
/// <para>
/// <b>为什么先发 <see cref="ExpiredEvent"/> 再投递销毁</b>：到期是**因果**，销毁是**结果**。
/// 订阅「这发子弹飞完射程了」的统计逻辑，不该被迫去猜「这次销毁是到期还是别的什么原因」。
/// </para>
/// <para>
/// <b>两遍扫描</b>：第一遍只做减法并把到期实体记进旁路数组；第二遍（遍历已结束）才投递销毁命令。
/// 这样世界结构在整个遍历期间不变，符合 J5。旁路数组是复用的，稳态零分配。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.Update)]
public sealed class LifetimeSystem : SystemBase, ISpanAction<Lifetime>
{
    private readonly HostEventSink _events;

    /// <summary>
    /// 到期实体暂存数组。**故意不是 readonly**：容量不足时要 <c>Array.Resize(ref _expired, ...)</c>
    /// 换成更大的数组，而 <c>ref</c> 参数要求字段可写。
    /// </summary>
    private Entity[] _expired;

    /// <summary>接口视图（自身）：回调里要做 <c>Array.Resize(ref ...)</c>，必须传真身而不是装箱副本。</summary>
    private readonly ISpanAction<Lifetime> _action;

    private Query _query = null!;
    private float _deltaTime;
    private int _expiredCount;

    /// <summary>
    /// 构造：注入事件出口。
    /// <para>
    /// <b>为什么事件走一个 sink 接口而不是直接持有宿主层的队列</b>：业务层不允许 <c>using</c>
    /// 宿主层的类型（那会把业务层拉向引擎依赖）。<see cref="HostEventSink"/> 是一个纯 C# 的窄接口，
    /// 声明在业务层、由宿主实现，于是系统只知道自己「能发事件」，不知道事件最后去哪。
    /// </para>
    /// </summary>
    /// <param name="events">事件出口。</param>
    /// <param name="capacity">到期暂存数组初始容量。</param>
    public LifetimeSystem(HostEventSink events, int capacity = 64)  // arch-ok: 到期暂存数组初始容量（内存预分配，不是玩法数值）
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _expired = new Entity[Math.Max(capacity, 1)];
        _action = this;
    }

    /// <inheritdoc />
    public override string Name => "Lifetime";

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        // 只查 Lifetime 一个组件：Entity 是句柄不是组件列（J1），查 Entity 会触发 Register<Entity>() 并抛异常。
        // 实体号从组件自带的 [Identity] Owner 字段取（见 Execute）。
        _query = World.Query<Lifetime>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _deltaTime = deltaTime;
        _expiredCount = 0;

        // 第一遍：减法 + 收集到期实体（世界结构不变）。
        //
        // 为什么用 _action 这个接口字段而不是 `in this`：`this` 是 class，`in this` 会把它装箱成
        // 接口的**临时副本**，副本是只读的——回调里对 `_expired` 的 Array.Resize(ref ...) 会报 CS0192。
        // 让字段自身就是 ISpanAction 类型，传进去的是真身，字段可写，且稳态零装箱。
        _query.ForEach<Lifetime>(in _action);

        // 第二遍：遍历已结束，可以安全地投递结构化命令。
        for (int i = 0; i < _expiredCount; i++)
        {
            Entity entity = _expired[i];
            _events.RaiseLifetimeExpired(Actors.FromEntity(entity));
            World.Commands.Destroy(entity);
        }
        _expiredCount = 0;
    }

    void ISpanAction<Lifetime>.Execute(Span<Lifetime> lifetimes, int count)
    {
        float dt = _deltaTime;
        int written = _expiredCount;
        for (int i = 0; i < count; i++)
        {
            float remaining = lifetimes[i].Remaining - dt;
            lifetimes[i].Remaining = remaining;
            if (remaining <= 0f)
            {
                if (written >= _expired.Length)
                {
                    Array.Resize(ref _expired, _expired.Length * 2);
                }
                // 实体号来自 [Identity] 字段：内核在生成/迁移时自动写入，系统只需读（J1）。
                _expired[written++] = lifetimes[i].Owner;
            }
        }
        _expiredCount = written;
    }
}
