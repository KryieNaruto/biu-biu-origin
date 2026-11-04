using System;
using Game.Core.Ecs;
using NVector3 = System.Numerics.Vector3;

namespace Game.Actor;

/// <summary>
/// 移动系统：把 <see cref="Velocity"/> 积分进 <see cref="Transform3"/>（固定步长、可确定性回放）。
/// <para>
/// <b>为什么在 <see cref="UpdateGroup.FixedUpdate"/></b>：位移必须用恒定 dt 积分，
/// 否则同 seed 两次运行的结果会因帧率不同而漂移，回放/服务器校验立刻失效。
/// </para>
/// <para>
/// <b>为什么组件里没有 <c>Update()</c> 方法</b>（J3）：逻辑只活在这个 <c>for</c> 循环里。
/// 新增一种移动行为 = 新增一个系统或替换 <see cref="Velocity"/> 的写入方，而不是往组件里加分支。
/// </para>
/// <para>
/// <b>零分配</b>：通过 <see cref="ISpanAction{T0,T1}"/> 接口回调（不创建委托），
/// 每个 chunk 一次调用，内层是纯 <c>for</c> 循环。
/// </para>
/// <para>
/// <b>dt 怎么进到回调里</b>：<see cref="ISpanAction{T0,T1}.Execute"/> 的签名由内核定死（只给 Span + count），
/// 不带 dt。所以 <see cref="OnExecute"/> 把本帧 dt 存进实例字段，回调里再读它。
/// 这在单线程、同步调用的前提下是安全的；之所以不用 lambda 捕获 dt，是因为捕获会造成闭包分配。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.FixedUpdate)]
public sealed class MovementSystem : SystemBase, ISpanAction<Transform3, Velocity>
{
    private Query _query = null!;
    private float _deltaTime;

    /// <summary>接口视图（自身）：避免 <c>in this</c> 把 class 装箱成只读副本。</summary>
    private readonly ISpanAction<Transform3, Velocity> _action;

    /// <summary>构造。</summary>
    public MovementSystem()
    {
        _action = this;
    }

    /// <inheritdoc />
    public override string Name => "Movement";

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        _query = World.Query<Transform3, Velocity>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _deltaTime = deltaTime;
        _query.ForEach<Transform3, Velocity>(in _action);
    }

    /// <summary>积分本体：位置 += 速度 * dt。旋转交给需要转向的系统，这里只做线速度。</summary>
    void ISpanAction<Transform3, Velocity>.Execute(Span<Transform3> transforms, Span<Velocity> velocities, int count)
    {
        float dt = _deltaTime;
        for (int i = 0; i < count; i++)
        {
            NVector3 p = transforms[i].Position;
            NVector3 v = velocities[i].Linear;
            transforms[i].Position = new NVector3(p.X + v.X * dt, p.Y + v.Y * dt, p.Z + v.Z * dt);
        }
    }
}
