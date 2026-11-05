using Game.Core.Ecs;

namespace Game.Combat.Factory;

/// <summary>
/// <b>通用铸造门面</b>：把「一次铸造请求」变成「一个实体」。
/// <para>
/// <b>为什么需要抽象成接口</b>（规则5 先抽象）：
/// </para>
/// <list type="bullet">
/// <item>系统层（<see cref="CastSystem"/>、<see cref="ProjectileAdvanceSystem"/>）只认"会产生实体"这件事，
/// 不认它到底造的是子弹、怪物还是一团特效；</item>
/// <item>测试可以塞一个只记数的实现，把「生成了几次、每次生成了什么」变成可断言的纯数据，
/// 而不必搭一个真实世界（<c>tests/Characters</c> 里的 <c>RecordingCommandSink</c> 就是同一思路）；</item>
/// <item>将来若要换成 C++ 侧批量生成（<c>native/</c>）或走对象池化的 Node 视图，只需换一个实现，
/// 系统 0 行改动（规则7）。</item>
/// </list>
/// <para>
/// 返回 <c>out Entity</c> 而不是只返回 bool：投递方经常要"造完之后立刻给它加个组件/打个标记"，
/// 而 <c>World.Commands.CreateDeferred</c> 已经预留了实体号，此时可以安全引用。
/// </para>
/// </summary>
public interface IObjectFactory
{
    /// <summary>
    /// 按请求铸造一个实体。
    /// <para>返回 false 表示<b>被拒绝</b>（契约缺失 / 数据越界 / 冷却未好 / 法力不足），
    /// 这不是异常：拒绝是正常业务结果，由调用方计数并给 HUD 反馈。</para>
    /// </summary>
    bool TryCast(in CastRequest request, out Entity produced);

    /// <summary>累计铸造成功数（诊断）。</summary>
    int CastCount { get; }

    /// <summary>累计拒绝数（诊断）。</summary>
    int RejectCount { get; }
}
