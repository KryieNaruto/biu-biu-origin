namespace Game.Core.Randomness;

/// <summary>
/// 全工程随机源的**注入点与子流约定**：系统不再各自 <c>new DeterministicRandom()</c>，
/// 而是从宿主拿到的根随机源派生自己的子流。
/// <para>为什么要这层：如果每个系统直接持有同一个根随机源，那么「给刷怪系统多加一次随机」
/// 会把掉落、暴击的序列全部推后，回放与日种子榜立刻对不上。按 <see cref="RandomSalt"/> 派生子流后，
/// 各系统的序列互不干扰 —— 这正是 docs/rules/03 反模式 A4（静态全局可变状态）的替代方案：
/// 随机源是**被注入的对象**，不是全局变量。</para>
/// <para>用法：
/// <code>
/// IRandomProvider root = new DeterministicRandom(runSeed);
/// IRandomProvider lootRng = RandomStreams.For(root, RandomSalt.Loot);
/// </code></para>
/// </summary>
public static class RandomStreams
{
    /// <summary>
    /// 系统级子流盐值：**只加不改**（改数值会让该系统的历史回放失效）。每个随机消费方一个值。
    /// </summary>
    public static class RandomSalt
    {
        /// <summary>暴击判定。</summary>
        public const int Crit = 0x0100; // arch-ok: 子流盐值（名字即契约），非玩法数值

        /// <summary>掉落与词条选取。</summary>
        public const int Loot = 0x0200; // arch-ok: 子流盐值（名字即契约），非玩法数值

        /// <summary>刷怪点位与波次抽取。</summary>
        public const int Spawn = 0x0300; // arch-ok: 子流盐值（名字即契约），非玩法数值

        /// <summary>AI 决策抖动。</summary>
        public const int Ai = 0x0400; // arch-ok: 子流盐值（名字即契约），非玩法数值

        /// <summary>子弹散布与后坐。</summary>
        public const int Spread = 0x0500; // arch-ok: 子流盐值（名字即契约），非玩法数值
    }

    /// <summary>
    /// 从根随机源派生指定盐值的**稳定子流**（语义依据见 <see cref="IRandomProvider.Fork"/>：
    /// 与根流推进到哪一步无关）。每次调用都得到同一条子流的独立实例（可重复、可分别推进）。
    /// </summary>
    public static IRandomProvider For(IRandomProvider root, int salt) => root.Fork(salt);
}
