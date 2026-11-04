namespace Game.Core.Randomness;

/// <summary>
/// 可播种的确定性随机源：<see cref="IRandomProvider"/> 的默认实现，**纯 C#、无 Godot 依赖**。
/// <para>为什么自己实现而不用 <c>System.Random</c>：</para>
/// <list type="bullet">
/// <item><c>System.Random</c> 的算法在不同 .NET 大版本之间换过（.NET 6 重写了实现），
/// 用它做「同 seed 同结果」等于把回放兼容性绑在运行时版本上。本实现固定为 SplitMix64，
/// **算法是本文件的契约**：改算法 = 让所有历史回放失效，必须走变更日志。</item>
/// <item>SplitMix64 只有加/乘/异或/移位，单次取值在纳秒级，够热路径用；周期 2^64，一局肉鸽用不完。</item>
/// </list>
/// <para>派生随机流的两种语义，分名不混淆：</para>
/// <list type="bullet">
/// <item><see cref="Fork"/>：**稳定子流**。基准是构造根状态（<see cref="_root"/>），
/// 于是「同一个 salt 永远得到同一条子流，与父流推进到哪一步无关」。
/// 这是「给某个系统加随机调用不会打乱其他系统」的实现依据，也是默认该用的那个。</item>
/// <item><see cref="ForkFromCurrent"/>：**当前点子流**。基准是此刻状态，会随父流进度变化；
/// 给「存档续跑后从存档点分叉」「每次施法一条一次性流」这类确实需要变化语义的调用方用。</item>
/// </list>
/// <para>两者都不共享状态：子流不会反过来扰动父流。</para>
/// <para><b>实测教训</b>：本类第一版把 <see cref="Fork"/> 的基准写成了活跃的 <c>_state</c>，
/// 与注释承诺矛盾，被 <c>tests/Determinism</c> 的 <c>Fork_IsIndependentOfParentAdvancement</c> 抓出。
/// 教训是「注释里写的实现依据必须能被一行断言证伪」，否则它就是一句愿望。</para>
/// <para>线程：非线程安全。按子系统各持一个实例；共享同一实例需调用方自己加锁。</para>
/// </summary>
public sealed class DeterministicRandom : IRandomProvider
{
    // SplitMix64 的算法常数：属于「固定算法的一部分」，改动即回放失效，故逐行登记 arch-ok 理由。
    private const ulong Gamma = 0x9E3779B97F4A7C15UL; // arch-ok: SplitMix64 算法常数，非玩法数值
    private const ulong MixMul1 = 0xFF51AFD7ED558CCDUL; // arch-ok: SplitMix64 算法常数，非玩法数值
    private const ulong MixMul2 = 0xC4CEB9FE1A85EC53UL; // arch-ok: SplitMix64 算法常数，非玩法数值
    private const int MixShift1 = 33; // arch-ok: 位混合移位量，非玩法数值
    private const int MixShift2 = 28; // arch-ok: 位混合移位量，非玩法数值
    private const int UpperShift = 32; // arch-ok: 取 64 位的高 32 位，非玩法数值

    /// <summary>
    /// 子流根状态的异或盐：让「根的根」与「根本身当作运气序列用」两套值不重合。
    /// 它是派生树的形状常量，改动会让全部历史回放失效，属于算法契约的一部分。
    /// </summary>
    private const ulong ForkMixSalt = 0xD1B54A32D192ED03UL; // arch-ok: 派生盐常量，非玩法数值

    /// 浮点构造常量：取 24 位有效位（float 尾数宽度）再缩放 2^-24，得到严格 [0,1)。
    private const uint FloatMask = 0x00FFFFFFu; // arch-ok: 浮点构造掩码，非玩法数值
    private const float FloatScale = 1.0f / 16777216.0f; // arch-ok: 2^-24，浮点构造常量，非玩法数值

    /// <summary>
    /// 构造种子混合后的**根状态**：永不随取值推进。`Fork` 以它为基准，
    /// 才能做到「父流推进多少次都不影响子流」。
    /// <para>为什么必须单独存一份、不能复用 <see cref="_state"/>：`_state` 每取一次值就变，
    /// 拿它当 Fork 基准会让「同一个 salt」在不同时刻派生出不同子流 —— 那么任何一处多调一次随机，
    /// 所有下游系统的序列都会全体漂移，回放/联机对不上，且极难定位。这是实测抓到的 bug。</para>
    /// </summary>
    private readonly ulong _root;

    private ulong _state;

    /// <summary>用种子构造：种子相同 → 序列完全相同（本类最重要的契约）。</summary>
    public DeterministicRandom(int seed)
        : this(Mix((ulong)(uint)seed) | 1UL, Mix((ulong)(uint)seed) ^ ForkMixSalt)
    {
    }

    /// <summary>内部构造：同时给出「运行状态」与「Fork 基准根状态」。</summary>
    private DeterministicRandom(ulong state, ulong root)
    {
        _state = state | 1UL;   // 全零状态在 SplitMix64 下会退化，强制非零。
        _root = root | 1UL;     // 同上：根状态也强制非零。
    }

    /// <summary>当前内部状态快照：存档/回放时保存它即可精确续跑。只读，读取不推进序列。</summary>
    public ulong State => _state;

    /// <summary>以给定种子创建随机源（语义同构造函数，便于「只保存 seed」的调用方）。</summary>
    public static DeterministicRandom FromSeed(int seed) => new(seed);

    /// <inheritdoc />
    public int NextInt(int minInc, int maxExc)
    {
        if (maxExc < minInc)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.RandomMisuse,
                nameof(DeterministicRandom) + ".NextInt",
                "区间非法：maxExc 必须 >= minInc");
        }

        if (maxExc == minInc)
        {
            return minInc;
        }

        ulong span = (ulong)((long)maxExc - (long)minInc);
        ulong offset = NextUInt64() % span;
        return (int)((long)minInc + (long)offset);
    }

    /// <inheritdoc />
    public float NextFloat()
    {
        // 取高 32 位中的 24 位有效数字再缩放：分布质量比「% 常数」好，且绝不会越界到 1.0f。
        uint bits = (uint)(NextUInt64() >> UpperShift) & FloatMask;
        return bits * FloatScale;
    }

    /// <summary>
    /// 派生**稳定子流**：基准是构造根状态，**与父流已取过多少个数无关**。
    /// <para>语义保证：同一个随机源对象上，任意时刻调 <c>Fork(salt)</c> 都得到同一条子流；
    /// 所以「刷怪系统 <c>Fork(Spawn)</c>、掉落系统 <c>Fork(Loot)</c>」互不干扰，
    /// 给任何一个系统增加随机调用都不会打乱其他系统的序列。</para>
    /// <para>注意：这里返回的是同一个族里的一条流，再次对它调用 <see cref="Fork"/> 也是相对它自己的根，
    /// 因此「派生树的形状」是全序确定的。</para>
    /// </summary>
    public IRandomProvider Fork(int salt)
    {
        // 与 _state 无关，只用 _root：这是「父流推进不影响子流」的实现依据。
        unchecked
        {
            ulong mixed = Mix(_root ^ Mix((ulong)(uint)salt));
            return new DeterministicRandom(mixed, mixed ^ ForkMixSalt);
        }
    }

    /// <summary>
    /// 派生**当前点子流**：基准是此刻的 <see cref="_state"/>。
    /// <para>与 <see cref="Fork"/> 的区别：同一个 salt 在父流推进前后会得到**不同**的子流。
    /// 适用场景恰恰是「要让子流随父流进度变化」的地方，例如存档续跑后从存档点分叉，
    /// 或者「每次施法都派生一条一次性随机流」。</para>
    /// <para>为什么把两种语义分成两个方法名：两者都叫 <c>Fork</c> 会让调用方无法判断拿到的流是否可复现。
    /// 名字即契约。</para>
    /// </summary>
    public IRandomProvider ForkFromCurrent(int salt)
    {
        unchecked
        {
            ulong mixed = Mix(_state ^ Mix((ulong)(uint)salt));
            return new DeterministicRandom(mixed, mixed ^ ForkMixSalt);
        }
    }

    private ulong NextUInt64()
    {
        unchecked
        {
            _state += Gamma;
            ulong z = _state;
            z = (z ^ (z >> MixShift1)) * MixMul1;
            z = (z ^ (z >> MixShift2)) * MixMul2;
            return z ^ (z >> MixShift1);
        }
    }

    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> MixShift1)) * MixMul1;
            z = (z ^ (z >> MixShift2)) * MixMul2;
            return z ^ (z >> MixShift1);
        }
    }
}
