using System;
using System.Collections.Generic;
using Game.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Content
{
    /// <summary>
    /// <see cref="StatId"/> 号段纪律的回归用例组（docs/rules/05 §5.2「只加不改」）。
    /// <para>
    /// 为什么要有这条守卫：本任务往 <see cref="StatId"/> 追加了「武器身上的数值」与「投射物身上的数值」两组成员。
    /// 一旦有人为了"好看"把它们挪回 200~399 的原生号段，就会改变已发布枚举的数值含义 ——
    /// <c>WeaponDamage</c> 会和某个旧的攻击属性撞号，存盘与 Excel 生成物一起错位，
    /// 而那类错误只会在很远的运行期以"伤害莫名其妙"的形式暴露。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class StatIdAppendZoneTests
    {
        /// <summary>本任务新增的武器数值（号段 1030..）。</summary>
        private static readonly StatId[] WeaponStats =
        {
            StatId.WeaponFireRate, StatId.WeaponDamage, StatId.WeaponProjectileSpeed, StatId.WeaponProjectileCount,
            StatId.WeaponSpread, StatId.WeaponRange, StatId.WeaponPierce, StatId.WeaponKnockback,
            StatId.WeaponRecoil, StatId.WeaponChargeTime, StatId.WeaponCritChance, StatId.WeaponCritMultiplier,
        };

        /// <summary>本任务新增的投射物数值（号段 1050..）。</summary>
        private static readonly StatId[] ProjectileStats =
        {
            StatId.ProjectileLifeTime, StatId.ProjectileRadius, StatId.ProjectileBounce,
            StatId.ProjectileHomingSpeed, StatId.ProjectileGravity,
        };

        [TestMethod]
        public void NewStats_AllLiveInTheAppendZone()
        {
            // 「300~399 是投射物号段」不等于可以往里插：已发布的枚举只允许在追加区末尾增加。
            foreach (StatId id in AllNewStats())
            {
                Assert.IsTrue(id.IsAppendZone(),
                    $"{id} 必须落在追加区（≥ {StatIdRanges.AppendStart}），发布后的新成员不得插入中间号段");
                Assert.AreEqual(StatIdRanges.AppendStart, id.RangeOf(), $"{id} 的号段必须判定为追加区");
                Assert.IsTrue((int)id >= StatIdRanges.AppendStart, $"{id} 的数值太小");
            }
        }

        [TestMethod]
        public void NewStats_KeepAscendingOrderAndDoNotCollide()
        {
            // 递增 + 不重复：数值重复意味着两个成员含义重叠（Excel 生成物按数值映射时会静默合并）。
            int previous = (int)StatId.StaminaRegenPerSec;
            foreach (StatId id in WeaponStats)
            {
                Assert.IsTrue((int)id > previous, $"{id} 必须接在 {StatId.StaminaRegenPerSec} 之后并保持递增");
                previous = (int)id;
            }
            foreach (StatId id in ProjectileStats)
            {
                Assert.IsTrue((int)id > previous, $"{id} 必须保持递增");
                previous = (int)id;
            }

            // 与全部已有成员（含历史成员）无重复：用整张枚举的数值集合做交叉检查。
            var seen = new Dictionary<int, StatId>();
            foreach (StatId id in Enum.GetValues<StatId>())
            {
                Assert.IsFalse(seen.ContainsKey((int)id),
                    $"StatId 数值重复：{id} 与 {seen.GetValueOrDefault((int)id)} 同为 {(int)id}");
                seen.Add((int)id, id);
            }
        }

        [TestMethod]
        public void NewStats_DoNotChangeExistingMembers()
        {
            // 只加不改：已有成员的数值就是它们的身份，一个都不能动（用 int 比较避开枚举类型的重载陷阱）。
            var expected = new (StatId Id, int Value)[]
            {
                (StatId.None, 0), (StatId.Hp, 1), (StatId.HpMax, 2), (StatId.ArmorFlat, 4),
                (StatId.MoveSpeed, 100), (StatId.Damage, 200), (StatId.Reserved900, 900),
                (StatId.Mana, 1000), (StatId.StaminaRegenPerSec, 1022),
            };
            foreach ((StatId id, int value) in expected)
            {
                Assert.AreEqual(value, (int)id, $"{id} 的数值是已发布契约，不得改动");
            }

            // 号段常量本身也是契约，不得为了"顺手"改掉（用变量绕开常量折叠，保证这是真的运行时检查）。
            int appendStart = StatIdRanges.AppendStart;
            int projectileStart = StatIdRanges.ProjectileStart;
            int projectileEnd = StatIdRanges.ProjectileEnd;
            Assert.AreEqual(1000, appendStart);
            Assert.AreEqual(300, projectileStart);
            Assert.AreEqual(399, projectileEnd);
        }

        [TestMethod]
        public void WeaponStats_AreReferencableFromWeaponStatRules()
        {
            // 「子弹具有武器属性」在代码层的样子：投射物 Def 的规则直接引用武器身上的 StatId。
            var rule = Game.Core.Content.ValueRef.OfWeaponStat(StatId.WeaponDamage, percent: false, a: 1f);
            Assert.AreEqual(StatId.WeaponDamage, rule.StatValue, "规则携带的属性必须就是武器数值本身");
            Assert.IsTrue(rule.IsStatRef);

            var percentRule = Game.Core.Content.ValueRef.OfWeaponStat(StatId.WeaponFireRate, percent: true);
            Assert.AreEqual(1, percentRule.Percent, "百分比规则落在倍率段");
        }

        private static IEnumerable<StatId> AllNewStats()
        {
            foreach (StatId id in WeaponStats)
            {
                yield return id;
            }
            foreach (StatId id in ProjectileStats)
            {
                yield return id;
            }
        }
    }
}
