using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Content
{
    /// <summary>
    /// 数值规则解释器的用例组：<see cref="ValueRef"/> 的每一种 <see cref="ValueSource"/> 分支、
    /// 以及 <see cref="ValueSet"/> 的层叠公式。
    /// <para>
    /// 为什么这些用例是基础物件抽象的「地基测试」：所有 Def 里的每一个数字都是规则，
    /// 规则解释错一个分支就会表现为「表里填了但游戏里没生效」——这类 bug 不抛异常，只静默算出 0。
    /// 所以每个来源都必须有一条独立的、可读的断言。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class ContentValueRuleTests
    {
        private static readonly StatPackageIds HeroStats = new StatPackageIds("hero_base");

        [TestMethod]
        public void LiteralRule_ReturnsItsOwnValue()
        {
            var ctx = new ValueContext(new FakeValueWorld(), HeroStats, new FakeRandom(0f));

            Assert.AreEqual(7.5f, ctx.Resolve(ValueRef.Literal(7.5f)), "字面量规则必须原样返回，不经过倍率换算");
            Assert.AreEqual(0f, ctx.Resolve(ValueRef.Literal(0f)));
        }

        [TestMethod]
        public void StatRule_ReadsThroughTheContextsPackageIds()
        {
            // 同一个世界问出不同的值 = 属性不是全局状态，而是「这份上下文代表谁」决定的（ctx.Stats 透传进 world）。
            var world = new FakeValueWorld();
            world.SetStat(new StatPackageIds("hero_base"), StatId.Strength, 20f);
            world.SetStat(new StatPackageIds("goblin_base"), StatId.Strength, 5f);

            var hero = new ValueContext(world, HeroStats, new FakeRandom(0f));
            var goblin = new ValueContext(world, new StatPackageIds("goblin_base"), new FakeRandom(0f));

            Assert.AreEqual(20f, hero.Resolve(ValueRef.OfStat(StatId.Strength)), "OfStat 默认系数 1：直接取属性原值");
            Assert.AreEqual(6f, hero.Resolve(ValueRef.OfStat(StatId.Strength, percent: false, a: 0.3f)), "系数是乘法：20 × 0.3");
            Assert.AreEqual(5f, goblin.Resolve(ValueRef.OfStat(StatId.Strength)), "换一份数值包组合就换了一组属性");

            // 跨 schema 读取（这个角色没声明这个属性）= 0，不抛异常。
            Assert.AreEqual(0f, hero.Resolve(ValueRef.OfStat(StatId.Luck)));
        }

        [TestMethod]
        public void FlagRule_ReadsTheBitMatrixWordMask()
        {
            // 位矩阵是策划勾出来的网格；规则只描述「哪个 tag + 哪些位」，不含任何具体 tag 语义。
            var world = new FakeValueWorld { TagCountValue = 64 };
            world.SetFlags(tag: 3, mask: 0b0101);
            var ctx = new ValueContext(world, HeroStats, new FakeRandom(0f));

            Assert.AreEqual(1f, ctx.Resolve(ValueRef.Flag(ValueSource.Flags, 3)), "默认掩码 = 1，该位点亮 → 1");

            // 掩码求交：tag 3 的整段掩码是 0b0101；问 bit1（0b0010）→ 不亮；问 bit2（0b0100）→ 亮。
            Assert.AreEqual(0f, ctx.Resolve(ValueRef.FlagMask(tag: 3, mask: 0b0010)));
            Assert.AreEqual(1f, ctx.Resolve(ValueRef.FlagMask(tag: 3, mask: 0b0100)));

            // tag 越界（世界只有 64 个 tag 时问 200）= 0，不读到别的行的内存、也不炸。
            Assert.AreEqual(0f, ctx.Resolve(ValueRef.FlagMask(tag: 200, mask: 1)));
        }

        [TestMethod]
        public void ChainAndPerfRules_ReadScalarWorldState()
        {
            var world = new FakeValueWorld
            {
                ChainDepthValue = 2,
                ChainCountValue = 5,
                ViableCountValue = 3,
                PerfOrNull = 0.5f,
            };
            var ctx = new ValueContext(world, HeroStats, new FakeRandom(0f));

            Assert.AreEqual(2f, ctx.Resolve(ValueRef.Flag(ValueSource.ChainDepth, 0)));
            Assert.AreEqual(5f, ctx.Resolve(ValueRef.Flag(ValueSource.ChainCount, 0)));
            Assert.AreEqual(3f, ctx.Resolve(ValueRef.Flag(ValueSource.ViableCount, 0)));
            Assert.AreEqual(0.5f, ctx.Resolve(ValueRef.Flag(ValueSource.Perf, 0)));

            // 世界完全不知道性能预算时按满预算（1）处理：没实现预算的宿主不该让所有数值缩水。
            var bare = new ValueContext(new FakeValueWorld(), HeroStats, new FakeRandom(0f));
            Assert.AreEqual(1f, bare.Resolve(ValueRef.Flag(ValueSource.Perf, 0)));
        }

        [TestMethod]
        public void RandomRules_UseTheInjectedRandomSource()
        {
            // 随机来自注入（同 seed 同结果）：这里用固定的假随机，把随机规则变成确定性断言。
            // 随机源挂在 world 上（随机是有状态的，必须来自同一处，回放才可复现）。
            var world = new FakeValueWorld { Random = new FakeRandom(0.5f) };
            var ctx = new ValueContext(world, HeroStats, new FakeRandom(0f));

            Assert.AreEqual(15f, ctx.Resolve(ValueRef.Range(10f, 20f)), "Range = A + rand × (B − A) = 10 + 0.5 × 10");
            Assert.AreEqual(11f, ctx.Resolve(ValueRef.RandomScaled(10f, 0.2f)), "Scaled = A × (1 + B × rand) = 10 × 1.1");
        }

        [TestMethod]
        public void WeaponStatRule_FallsBackToTableDefault_WhenNoSourceWeapon()
        {
            // 「子弹具有武器属性」在来源武器缺失时的行为：回落到表里填的武器默认值，而不是 0 或某个写死的数。
            var world = new FakeValueWorld();
            var ctx = new ValueContext(world, HeroStats, new FakeRandom(0f));

            ValueRef withDefault = ValueRef.OfWeaponStat(StatId.WeaponDamage, percent: false, a: 12f);
            Assert.AreEqual(12f, ctx.Resolve(withDefault), "链上没有武器 → 用 A 里的武器默认值");

            // 没填默认值（A = 0）= 「不可用」，解析成 0：不会静默沿用魔法数。
            Assert.AreEqual(0f, ctx.Resolve(ValueRef.OfWeaponStat(StatId.WeaponFireRate)));

            // 有来源武器时读武器身上的数，默认值被忽略。
            world.SetWeaponStat(StatId.WeaponDamage, 40f);
            Assert.AreEqual(40f, ctx.Resolve(withDefault), "有来源武器时必须读武器，而不是默认值");
        }

        [TestMethod]
        public void ValueSet_StacksAbsoluteAndPercentLayers()
        {
            // 层叠公式：Σ(绝对量) × (1 + Σ(百分比))。
            var world = new FakeValueWorld();
            world.SetStat(HeroStats, StatId.WeaponDamage, 10f);
            world.SetStat(HeroStats, StatId.Strength, 0.2f);
            world.SetStat(HeroStats, StatId.Dexterity, 0.1f);
            var ctx = new ValueContext(world, HeroStats, new FakeRandom(0f));

            ValueSet set = ValueSet.From(
                ValueRef.OfStat(StatId.WeaponDamage),
                ValueRef.OfStat(StatId.Strength, percent: true, a: 1f));
            Assert.AreEqual(12f, ctx.Resolve(set), "10 绝对量 × (1 + 20%) = 12");

            // 多个百分比段相加、多个绝对量相加，且与书写顺序无关（表里换行不应改变伤害）。
            ValueSet twoPct = ValueSet.From(
                ValueRef.OfStat(StatId.WeaponDamage),
                ValueRef.OfStat(StatId.Strength, percent: true, a: 1f),
                ValueRef.OfStat(StatId.Dexterity, percent: true, a: 1f),
                ValueRef.PercentOf(0.1f));
            Assert.AreEqual(14f, ctx.Resolve(twoPct), "10 × (1 + 0.2 + 0.1 + 0.1) = 14");

            // 单条引用的求值不做 1+x 换算（换算只在层叠时发生）。
            Assert.AreEqual(0.2f, ctx.Resolve(ValueRef.PercentOf(0.2f)), "单条百分比的直接值就是 0.2（不是 1.2）");
            ValueSet absPlusHalf = ValueSet.From(ValueRef.Literal(2f), ValueRef.PercentOf(0.5f));
            Assert.AreEqual(3f, ctx.Resolve(absPlusHalf), "2 × (1 + 50%) = 3");
        }

        [TestMethod]
        public void ValueSet_IsFixedCapacityAndRejectsOverflowLoudly()
        {
            // 超出容量必须抛异常：静默丢规则 = 玩家永远看到不到某条词条生效，是最难查的一类 bug。
            ValueSet full = ValueSet.From(
                ValueRef.Literal(1f), ValueRef.Literal(2f), ValueRef.Literal(3f), ValueRef.Literal(4f));
            Assert.AreEqual(4, full.Count, "4 槽内联数组装得下");
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual((float)(i + 1), ValueOf(full[i]), "索引访问按加入顺序");
            }

            bool threw = false;
            try
            {
                ValueSet.From(
                    ValueRef.Literal(1f), ValueRef.Literal(2f), ValueRef.Literal(3f),
                    ValueRef.Literal(4f), ValueRef.Literal(5f));
            }
            catch (System.ArgumentException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "第 5 条引用必须抛 ArgumentException，而不是被悄悄丢掉");

            Assert.AreEqual(0, ValueSet.Empty.Count, "空集合求值恒为 0");
            var ctx = new ValueContext(new FakeValueWorld(), HeroStats, new FakeRandom(0f));
            Assert.AreEqual(0f, ctx.Resolve(ValueSet.Empty));
        }

        private static float ValueOf(ValueRef r) => r.A;

        [TestMethod]
        public void ValueContext_WithStats_SwitchesThePackageWithoutTouchingTheWorld()
        {
            // 熔炉派生 / 夺舍的求值形态：换一份数值包组合，其余输入不变。
            var world = new FakeValueWorld();
            world.SetStat(new StatPackageIds("small"), StatId.WeaponDamage, 3f);
            world.SetStat(new StatPackageIds("big"), StatId.WeaponDamage, 9f);
            var ctx = new ValueContext(world, new StatPackageIds("small"), new FakeRandom(0f));

            ValueRef r = ValueRef.OfStat(StatId.WeaponDamage);
            Assert.AreEqual(3f, ctx.Resolve(r));

            ValueContext promoted = ctx.WithStats(new StatPackageIds("big"));
            Assert.AreEqual(9f, promoted.Resolve(r), "WithStats 之后必须读到新组合的属性");
            Assert.AreEqual(3f, ctx.Resolve(r), "原上下文是值语义快照，不受影响");
        }
    }
}
