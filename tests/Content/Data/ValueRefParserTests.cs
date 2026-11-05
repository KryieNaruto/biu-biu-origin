using System;
using Game.Core;
using Game.Core.Content;
using Game.Data.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Content.Data;

/// <summary>
/// <see cref="ValueRefParser"/> 的语法表逐条验收：Excel 单元格字符串 → <see cref="ValueRef"/>。
/// <para>
/// <b>为什么值得为"解析器"单独写用例</b>：规则值语法是策划与运行时的**唯一契约**。
/// 它一旦某条写法静默解析成错误的值（例如 <c>stat:Damage+30%</c> 把 30 当绝对值），
/// 表现是"数值手感不对"——比崩掉难查一百倍。所以每条语法都钉死 <see cref="ValueSource"/> + 参数。
/// </para>
/// </summary>
[TestClass]
public sealed class ValueRefParserTests
{
    /// <summary>Tags 名 → 下标的上下文（等价于 Tags 表的列头）。</summary>
    private static readonly ContentRules Rules = ContentRules.FromTags(new[] { "tag.actor", "tag.kinetic", "tag.homing" });

    /// <summary>裸字面量：<c>12.5</c> → <see cref="ValueSource.Literal"/>。</summary>
    [TestMethod]
    public void Literal_ParsesToLiteralSource()
    {
        Assert.IsTrue(ValueRefParser.TryParse("12.5", out ValueRef value, out string error), error);
        Assert.AreEqual(ValueSource.Literal, value.Source, "<c>12.5</c> 应解析为字面量。");
        Assert.AreEqual(12.5f, value.A, "字面量应保留原值。");
    }

    /// <summary>负字面量同样合法（击退、后坐力可以是负的）。</summary>
    [TestMethod]
    public void NegativeLiteral_ParsesToNegativeValue()
    {
        Assert.IsTrue(ValueRefParser.TryParse("-3", out ValueRef value, out string error), error);
        Assert.AreEqual(ValueSource.Literal, value.Source, "负数字面量应解析为字面量。");
        Assert.AreEqual(-3f, value.A, "负号应被保留。");
    }

    /// <summary><c>stat:Dexterity</c> → 读持有者属性，系数 1。</summary>
    [TestMethod]
    public void Stat_ReadsActorStatWithUnitCoefficient()
    {
        Assert.IsTrue(ValueRefParser.TryParse("stat:Dexterity", out ValueRef value, out string error), error);
        Assert.AreEqual(ValueSource.Stat, value.Source, "stat: 应解析为 ValueSource.Stat。");
        Assert.AreEqual((int)StatId.Dexterity, value.StatIdCode, "stat: 的 StatIdCode 应指向 Dexterity。");
        Assert.AreEqual(1f, value.A, "不带系数的 stat: 系数应为 1。");
    }

    /// <summary><c>stat:Damage+30%</c> → 属性 × 0.3（百分号 = 除以 100）。</summary>
    [TestMethod]
    public void Stat_WithPercentCoefficient_ScalesByFraction()
    {
        Assert.IsTrue(ValueRefParser.TryParse("stat:Damage+30%", out ValueRef value, out string error), error);
        Assert.AreEqual(ValueSource.Stat, value.Source, "stat: 应解析为 ValueSource.Stat。");
        Assert.AreEqual((int)StatId.Damage, value.StatIdCode, "stat: 的 StatIdCode 应指向 Damage。");
        Assert.AreEqual(0.3f, value.A, "30% 应折成系数 0.3（百分号必须换算，否则等于放大 100 倍）。");
    }

    /// <summary><c>wstat:WeaponDamage(5)</c> → 读链上武器属性，缺省回落 5。</summary>
    [TestMethod]
    public void WeaponStat_CarriesFallbackValue()
    {
        Assert.IsTrue(ValueRefParser.TryParse("wstat:WeaponDamage(5)", out ValueRef value, out string error), error);
        Assert.AreEqual(ValueSource.WeaponStat, value.Source, "wstat: 应解析为 ValueSource.WeaponStat。");
        Assert.AreEqual((int)StatId.WeaponDamage, value.StatIdCode, "wstat: 的 StatIdCode 应指向 WeaponDamage。");
        Assert.AreEqual(5f, value.A, "括号里的默认值应写进 A（契约 OfWeaponStat 的 a = 默认值本体，不是系数）。");
    }

    /// <summary><c>flags:tag.kinetic</c> → 位矩阵里该 tag 的值；tag 名必须翻成下标。</summary>
    [TestMethod]
    public void Flags_TranslatesTagNameToIndex()
    {
        int expectedIndex = Rules.TagOf("tag.kinetic");
        Assert.IsTrue(expectedIndex >= 0, "测试上下文里应登记 tag.kinetic。");

        Assert.IsTrue(ValueRefParser.TryParse("flags:tag.kinetic", Rules, out ValueRef value, out string error), error);
        Assert.AreEqual(ValueSource.Flags, value.Source, "flags: 应解析为 ValueSource.Flags。");
        Assert.AreEqual(expectedIndex, (int)value.Tag, "flags: 的 tag 字段应为 Tags 表下标。");
        Assert.AreEqual(1, (int)value.A, "不带掩码的 flags: 默认掩码为 1。");

        // 没有 tag 表上下文时，flags: 必须明确失败（不能静默当 0 —— 那样"规则到底生效没有"不可判定）。
        Assert.IsFalse(ValueRefParser.TryParse("flags:tag.kinetic", out _, out _),
            "无 ContentRules 上下文时 flags: 必须失败。");
    }

    /// <summary><c>chain:depth</c> / <c>chain:count</c> / <c>viable:count</c> / <c>perf</c> 四个无参来源。</summary>
    [TestMethod]
    public void ContextSources_ParseToTheirValueSources()
    {
        AssertSource("chain:depth", ValueSource.ChainDepth);
        AssertSource("chain:count", ValueSource.ChainCount);
        AssertSource("viable:count", ValueSource.ViableCount);
        AssertSource("perf", ValueSource.Perf);
    }

    /// <summary><c>rand:2.0~3.0</c> → 随机区间；<c>rand:10+0.5</c> → 基准 ±50%。</summary>
    [TestMethod]
    public void Random_ParsesRangeAndJitterForms()
    {
        Assert.IsTrue(ValueRefParser.TryParse("rand:2.0~3.0", out ValueRef range, out string error), error);
        Assert.AreEqual(ValueSource.RandomRange, range.Source, "a~b 应解析为随机区间。");
        Assert.AreEqual(2f, range.A, "区间下界应写进 A。");
        Assert.AreEqual(3f, range.B, "区间上界应写进 B。");

        Assert.IsTrue(ValueRefParser.TryParse("rand:10+0.5", out ValueRef jitter, out string jitterError), jitterError);
        Assert.AreEqual(ValueSource.RandomScaled, jitter.Source, "基准+抖动应解析为 RandomScaled。");
        Assert.AreEqual(10f, jitter.A, "基准值应写进 A。");
        Assert.AreEqual(0.5f, jitter.B, "抖动幅度应写进 B。");
    }

    /// <summary>非法输入必须失败，且错误原因说清"能写什么"（供绑定器拼上表/行/列再抛）。</summary>
    [TestMethod]
    public void InvalidText_FailsWithExplainedReason()
    {
        Assert.IsFalse(ValueRefParser.TryParse("abc", out _, out string reason), "'abc' 不应被解析成规则值。");
        Assert.IsTrue(reason.Length > 0, "失败必须给出原因。");

        Assert.IsFalse(ValueRefParser.TryParse("stat:NotAStat", out _, out _), "未登记的 StatId 必须失败（不能静默当 0）。");
        Assert.IsFalse(ValueRefParser.TryParse("", out _, out _), "空文本必须失败（空 ≠ 0）。");
        Assert.IsFalse(ValueRefParser.TryParse(null, out _, out _), "null 必须失败（空 ≠ 0）。");
    }

    /// <summary>
    /// <c>Parse</c> 抛出的异常必须带「表名 行号 列名 原文」——
    /// 策划看报错就知道去改哪一个单元格。
    /// </summary>
    [TestMethod]
    public void Parse_ThrowsWithTableRowColumnAndText()
    {
        ContentException ex = Assert.ThrowsExactly<ContentException>(
            () => ValueRefParser.Parse("abc", Rules, "combat.xlsx Weapons 第12行 FireInterval"));
        Assert.IsTrue(ex.Message.Contains("Weapons", StringComparison.Ordinal), "报错应含表名，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("第12行", StringComparison.Ordinal), "报错应含行号，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("FireInterval", StringComparison.Ordinal), "报错应含列名，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("abc", StringComparison.Ordinal), "报错应含原文，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("规则值", StringComparison.Ordinal), "报错应说明期望的是规则值，实际：" + ex.Message);
    }

    private static void AssertSource(string text, ValueSource expected)
    {
        Assert.IsTrue(ValueRefParser.TryParse(text, Rules, out ValueRef value, out string error), text + "：" + error);
        Assert.AreEqual(expected, value.Source, "'" + text + "' 应解析为 " + expected + "。");
    }
}
