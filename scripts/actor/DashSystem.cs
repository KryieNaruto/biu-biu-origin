using System;
using System.Collections.Generic;
using System.Numerics;
using Game.Core;
using Game.Core.Ecs;

namespace Game.Actor;

/// <summary>
/// <b>冲刺推进系统</b>：在冲刺窗口内把实体速度强制顶成冲刺速度，窗口结束后移除 <see cref="DashState"/>。
/// <para>
/// <b>为什么需要一个系统，而不是在 <c>DashSkill.Cast</c> 里直接改速度</b>
/// </para>
/// <list type="bullet">
/// <item>技能契约里的 <c>ISkillContext.Ecs</c> 是<u>只读</u>句柄（技能不许写组件），
///       而 <c>ICommandSink</c> 只有"生成/销毁"，没有"给实体加组件"。冲刺要跨帧持续，
///       必须有一个系统在每帧参与速度决定 —— 这正是 <see cref="SystemBase"/> 该干的事（J1）；</item>
/// <item>冲刺的方向/速度在开冲那一刻<u>定格</u>（之后改瞄准不该把冲刺拐弯），
///       这份"定格值"需要一个组件承载，否则每帧都要回查技能配置；</item>
/// <item>和 <see cref="MoveAimSystem"/> 的分工严格单向：MoveAim 读 <see cref="MoveIntent"/> 写
///       <see cref="Velocity"/>；本系统<u>在它之后</u>把 <see cref="Velocity"/> 顶掉。
///       冲刺期间"走不走得动"与"往哪走"解耦，冲刺结束时 MoveAim 自然重新接管，不需要任何复位代码。</item>
/// </list>
/// <para>
/// <b>为什么分两遍扫描</b>：倒计时递减是纯数据写（可以在遍历里做）；而窗口结束要<u>移除组件</u>，
/// 那是一次原型迁移 —— 遍历期间改结构会让 chunk 数组失效（J5）。
/// 所以第一遍只递减并记下"该结束的实体"，第二遍遍历结束后统一 <c>World.Remove</c>。
/// </para>
/// <para>
/// <b>为什么在 <see cref="UpdateGroup.FixedUpdate"/></b>：冲刺是位移，必须与帧率解耦。
/// 90Hz 与 144Hz 下同一段冲刺必须落在同一个位置（肉鸽的确定性需求）。
/// </para>
/// </summary>
[Module("dash_impulse")]
[UpdateInGroup(UpdateGroup.FixedUpdate)]
public sealed class DashSystem : SystemBase, ISpanAction<DashState, Velocity>
{
    private Query _query = null!;

    /// <summary>本帧结束冲刺的实体（复用容器；稳态零分配）。</summary>
    private readonly List<Entity> _finished = new(InitialCapacity);

    /// <summary>初始复用容量（内存预分配，不是玩法数值）。</summary>
    private const int InitialCapacity = 8; // arch-ok: 待处理数组的预分配容量（内存/性能参数），不是玩法数值

    private float _deltaTime;

    /// <summary>
    /// 本系统的接口视图（自身）。
    /// <para>为什么缓存成字段而不是每次 <c>ForEach</c> 里传 lambda：lambda 会捕获 <c>this</c>，
    /// 每次调用都新建一个闭包对象；缓存后稳态零分配（与 <c>ProjectileSystem</c> 的 <c>_action</c> 同一手法）。
    /// 用 <c>ISpanAction</c>（带 <c>count</c> 参数）而不是 <c>SpanAction</c> 委托：后者签名里没有元素个数，
    /// 而本步骤必须按 <c>count</c> 截断 span（chunk 的池容量通常大于实际元素数）。</para>
    /// </summary>
    private readonly ISpanAction<DashState, Velocity> _step;

    /// <summary>本帧处于冲刺中的实体数（调试/日志用）。</summary>
    public int DashingCount { get; private set; }

    /// <summary>本帧冲刺结束的实体数（调试/日志用）。</summary>
    public int FinishedCount { get; private set; }

    /// <summary>构造：把自身绑成接口视图。</summary>
    public DashSystem()
    {
        _step = this;
    }

    /// <inheritdoc />
    public override string Name => "Dash";

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        // 冲刺只需要速度组件：没有 Velocity 的实体（纯摆件）不该被本系统处理。
        _query = World.Query<DashState, Velocity>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _deltaTime = deltaTime;
        DashingCount = 0;
        FinishedCount = 0;
        _finished.Clear();

        // ── 第一遍：纯数据写（顶速度 + 倒计时）──────────────────────────
        QueryChunkIterator();

        // ── 第二遍：遍历已结束，可以安全做结构变更（移除组件）─────────────
        for (int i = 0; i < _finished.Count; i++)
        {
            Entity entity = _finished[i];

            // ★ 先把速度清零再摘组件：冲刺结束后如果留着冲刺速度，
            //   MovementSystem 会继续按这个速度积分 —— 角色像踩在冰上滑出去好几米。
            //   （这一行是端到端测试逼出来的：位移 2.86m vs 理论 2.06m，多出来的就是滑行。）
            if (World.TryGet<Velocity>(entity, out _))
            {
                World.Set(entity, default(Velocity));
            }

            World.Remove<DashState>(entity);
        }
        FinishedCount = _finished.Count;
    }

    /// <summary>
    /// 逐实体推进冲刺。
    /// <para>为什么用 <c>SpanAction</c> 委托形态而不是 <c>ISpanAction</c>：本步骤要同时读写 2 个 span
    /// 并在结束时改世界结构。委托在构造时建立一次（不每帧分配），冲刺是低频路径，
    /// 这点开销远小于"为了零分配再写一个接口视图"的复杂度。</para>
    /// <para><b>为什么必须显式写 <c>SpanAction&lt;…&gt;</c> 而不是裸 lambda</b>：<c>ForEach</c> 有两个重载
    /// （<c>in ISpanAction</c> 与 <c>SpanAction</c>），裸 lambda 会被优先绑定到前者的隐式转换上并报
    /// CS1503。显式类型让重载解析毫无歧义。</para>
    /// </summary>
    private void QueryChunkIterator()
    {
        _query.ForEach<DashState, Velocity>(_step);
    }
    /// <summary>
    /// 逐实体推进冲刺（<see cref="ISpanAction{T0,T1}"/> 实现，每 chunk 一次回调）。
    /// </summary>
    void ISpanAction<DashState, Velocity>.Execute(Span<DashState> states, Span<Velocity> velocities, int count)
    {
        float dt = _deltaTime;
        for (int i = 0; i < count; i++)
        {
            // ① 顶速度：冲刺速度在开冲那一刻已定格，这里只按方向铺开（不吃移动意图、不吃转向）。
            velocities[i].Linear = states[i].Direction * states[i].Speed;

            // ② 倒计时。
            float remaining = states[i].Remaining - dt;
            if (remaining <= 0f)
            {
                // 本帧结束：记下来，遍历结束后统一移除组件（J5）。
                _finished.Add(states[i].Owner);
                continue;
            }
            states[i].Remaining = remaining;
            DashingCount++;
        }
    }
}
