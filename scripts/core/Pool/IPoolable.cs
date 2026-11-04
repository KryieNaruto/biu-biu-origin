namespace Game.Core;

/// <summary>
/// 可池化对象的最小契约（docs/rules/03 §4.1 表中登记，实现位置在 <c>Game.Combat</c> / <c>Game.Actor</c>）。
/// <para>为什么必须把「归还」和「重置」拆成两个方法：</para>
/// <list type="bullet">
/// <item><see cref="OnReturn"/> 是**归还瞬间**的收尾（停计时器、解绑事件），此时对象还能读到自己的旧状态；</item>
/// <item><see cref="ResetState"/> 是**归还之后、租借之前**的状态清洗（清零字段、置空引用），
/// 保证下一次 <c>Rent</c> 拿到的是干净对象，而不是「上一次的残留」。</item>
/// </list>
/// <para>拆开的收益：池可以在<em>预热</em>阶段只调 <see cref="ResetState"/>（不会碰任何外部资源），
/// 也可以在<em>容量溢出</em>时跳过重置直接丢弃。合成一个方法就丢掉了这个余地。</para>
/// <para>纪律：<see cref="OnRent"/> / <see cref="OnReturn"/> / <see cref="ResetState"/> 内部都**不得抛异常**
/// —— 池是高频设施，一次异常就会打断一帧。</para>
/// </summary>
public interface IPoolable
{
    /// <summary>从池里取出后立刻调用：把对象从「休眠」切换到「在用」。可以挂特效、开计时器。</summary>
    void OnRent();

    /// <summary>归还时立刻调用：停掉所有外部副作用。调用后对象不应再被任何人引用。</summary>
    void OnReturn();

    /// <summary>清洗为初始状态：字段清零、引用置空。保证下次 <c>Rent</c> 与「新建」等价。</summary>
    void ResetState();
}
