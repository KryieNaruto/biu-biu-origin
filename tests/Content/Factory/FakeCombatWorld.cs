using System;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;

namespace BiuBiuOrigin.Tests.Content.Factory
{
    /// <summary>
    /// 测试用的**全中性求值世界**：所有查询都回答"没有"，计数全 0，Perf 恒 1，随机恒 0.5。
    /// <para>
    /// 为什么不用 <c>FakeValueWorld</c>（同目录下已有）：那个是给"数值规则怎么算"用的，需要能登记属性/位掩码；
    /// 本层的用例关心的是"铸造和熔炉的<b>结构</b>"（谁被选中、造出了什么实体、有没有分配），
    /// 不是"数值公式对不对"。所以这里刻意什么都不登记 —— 任何用例若意外依赖了某个数值，
    /// 求值会得到 0 或 1，断言会明显失败而不是碰巧通过。
    /// </para>
    /// </summary>
    internal sealed class FakeCombatWorld : IValueWorld
    {
        /// <summary>无状态单例（本类没有任何可变字段，共享是安全的）。</summary>
        public static readonly FakeCombatWorld Instance = new FakeCombatWorld();

        /// <inheritdoc />
        public bool TryStat(in StatPackageIds ids, StatId id, out float value)
        {
            value = 0f;
            return false;
        }

        /// <inheritdoc />
        public bool TryWeaponStat(StatId id, out float value)
        {
            value = 0f;
            return false;
        }

        /// <inheritdoc />
        public int Flags(byte tag) => 0;

        /// <inheritdoc />
        public int ChainDepth => 0;

        /// <inheritdoc />
        public int ChainCount => 0;

        /// <inheritdoc />
        public int ViableCount => 0;

        /// <inheritdoc />
        public float Perf => 1f;

        /// <inheritdoc />
        public IRandomProvider Random { get; } = new FakeRandom(0.5f);

        /// <inheritdoc />
        public int TagCount => 0;
    }
}
