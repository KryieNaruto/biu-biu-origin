using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;

namespace BiuBiuOrigin.Tests.Content
{
    /// <summary>
    /// 测试用的**假求值世界**：把「世界当前状态」写死在字典里。
    /// <para>
    /// 为什么需要它：<see cref="ValueResolver"/> 是纯函数，规则解释对不对完全取决于喂进去的世界状态。
    /// 真实世界（Game.Data 的数值包仓库 + 宿主）会把两百个数值、位矩阵、链深混在一起，
    /// 一旦某条规则算错，很难判断是"规则错了"还是"数据错了"。这个假世界只提供用例关心的那几项，
    /// 于是每条断言都只证明一件事。
    /// </para>
    /// <para>
    /// 它也顺带证明了 <see cref="IValueWorld"/> 的形状是够用的：
    /// 只要实现 <c>TryStat / TryWeaponStat / Flags / 三个计数 / Perf / Random / TagCount</c>，
    /// 就能驱动全部数值规则，不需要任何引擎对象。
    /// </para>
    /// </summary>
    internal sealed class FakeValueWorld : IValueWorld
    {
        private readonly Dictionary<(StatPackageIds Ids, StatId Id), float> _stats = new Dictionary<(StatPackageIds, StatId), float>();
        private readonly Dictionary<StatId, float> _weaponStats = new Dictionary<StatId, float>();
        private readonly Dictionary<byte, int> _flags = new Dictionary<byte, int>();

        /// <summary>位矩阵 tag 总数（缺省 0 = 所有 Flag 规则都判定为「不存在该 tag」）。</summary>
        public int TagCountValue { get; set; }

        /// <summary>链深。</summary>
        public int ChainDepthValue { get; set; }

        /// <summary>链累计物件数。</summary>
        public int ChainCountValue { get; set; }

        /// <summary>可发动数量。</summary>
        public int ViableCountValue { get; set; }

        /// <summary>性能预算；<c>null</c> 表示「这个世界不实现预算」（求值应退化为 1）。</summary>
        public float? PerfOrNull { get; set; } = 1f;

        /// <inheritdoc />
        public int TagCount => TagCountValue;

        /// <inheritdoc />
        public int ChainDepth => ChainDepthValue;

        /// <inheritdoc />
        public int ChainCount => ChainCountValue;

        /// <inheritdoc />
        public int ViableCount => ViableCountValue;

        /// <inheritdoc />
        public float Perf => PerfOrNull ?? 1f;

        /// <inheritdoc />
        public IRandomProvider Random { get; set; } = new FakeRandom(0f);

        /// <summary>登记一份数值包组合下的属性值。</summary>
        public void SetStat(StatPackageIds ids, StatId id, float value) => _stats[(ids, id)] = value;

        /// <summary>登记「来源武器」身上的属性值。</summary>
        public void SetWeaponStat(StatId id, float value) => _weaponStats[id] = value;

        /// <summary>登记某个 tag 所在 word 的整段掩码。</summary>
        public void SetFlags(byte tag, int mask) => _flags[tag] = mask;

        /// <inheritdoc />
        public bool TryStat(in StatPackageIds ids, StatId id, out float value) => _stats.TryGetValue((ids, id), out value);

        /// <inheritdoc />
        public bool TryWeaponStat(StatId id, out float value) => _weaponStats.TryGetValue(id, out value);

        /// <inheritdoc />
        public int Flags(byte tag) => _flags.TryGetValue(tag, out int mask) ? mask : 0;
    }

    /// <summary>
    /// 测试用的**固定随机源**：<see cref="NextFloat"/> 恒返回构造时给的值。
    /// <para>为什么不用 <c>DeterministicRandom</c>：用例要断言的是「随机规则怎么用这个数」，
    /// 而不是「随机序列对不对」（那有自己的用例组）。恒值让随机规则的期望值可以用手算出来。</para>
    /// </summary>
    internal sealed class FakeRandom : IRandomProvider
    {
        private readonly float _value;

        /// <summary>构造一个恒定随机源。</summary>
        public FakeRandom(float value) => _value = value;

        /// <inheritdoc />
        public int NextInt(int minInc, int maxExc) => minInc;

        /// <inheritdoc />
        public float NextFloat() => _value;

        /// <inheritdoc />
        public IRandomProvider Fork(int salt) => this;
    }
}
