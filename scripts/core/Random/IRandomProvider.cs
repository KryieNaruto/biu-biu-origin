namespace Game.Core;

/// <summary>
/// 可播种随机源：**全工程唯一的随机入口**（docs/rules/03 §4.1 登记，实现位置同为 <c>Game.Core</c>）。
/// <para>为什么必须是接口、而不是直接调 <c>Random.Shared</c> / <c>GD.Randf()</c>：</para>
/// <list type="bullet">
/// <item>肉鸽要能「同 seed 同结果」：回放、日种子榜、bug 复现都建立在这一点上；全局随机源无法播种、无法隔离。</item>
/// <item>每个系统要拿**自己的子流**（<see cref="Fork"/>）：否则「多加一次掉落判定」就会把暴击序列全部推后，
/// 回放对不上。子流隔离后，各系统互不影响。</item>
/// <item>测试可以注入固定序列的假实现，让「暴击 3 次」这类用例变成确定性的。</item>
/// </list>
/// <para>纪律：</para>
/// <list type="bullet">
/// <item>禁止静态可变状态（docs/rules/03 反模式 A4）：随机源是**对象**，由宿主创建并注入。</item>
/// <item>系统构造函数必须从宿主注入本接口（ECS.md §8「随机」一行），系统内部不得自己 <c>new</c>。</item>
/// <item>线程：不要求线程安全 —— 随机源按子系统持有，天然不跨线程共享。</item>
/// </list>
/// </summary>
public interface IRandomProvider
{
    /// <summary>整数区间 <c>[minInc, maxExc)</c>：左闭右开，和 <c>Random.Next</c> 一致，避免两套约定造成的 off-by-one。</summary>
    int NextInt(int minInc, int maxExc);

    /// <summary>浮点数 <c>[0, 1)</c>：左闭右开保证 <c>min + NextFloat() * (max-min)</c> 不会越上界。</summary>
    float NextFloat();

    /// <summary>
    /// 派生**稳定子流**：同一个 <paramref name="salt"/> 永远得到可复现的子流，
    /// **与父流已取过多少个数无关**，且**不影响本流自身的后续序列**。
    /// <para>用法：刷怪系统用 <c>Fork(RandomSalt.Spawn)</c>、掉落系统用 <c>Fork(RandomSalt.Loot)</c>，
    /// 这样给任何一个系统加随机调用都不会打乱别的系统的结果。</para>
    /// <para>接口契约的可判定形式（测试已覆盖）：
    /// <c>p.Fork(s)</c> 与「先把 p 推进若干次、再 <c>p.Fork(s)</c>」必须给出**逐元素相同**的序列。
    /// 实现若要提供「随父流进度变化」的变体，必须**另起方法名**（见 <c>DeterministicRandom.ForkFromCurrent</c>），
    /// 不能悄悄改变本方法的语义 —— 否则每个调用方都无法判断自己拿到的流是否可复现。</para>
    /// </summary>
    IRandomProvider Fork(int salt);
}
