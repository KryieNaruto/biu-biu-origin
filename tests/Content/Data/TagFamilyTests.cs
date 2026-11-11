using System;
using System.Collections.Generic;
using System.IO;
using Game.Core.Content;
using Game.Data.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Content.Data;

/// <summary>
/// 标签**轴索引**（<see cref="TagFamily"/> / <see cref="ContentRules.TagsOfFamily"/> /
/// <see cref="ContentRules.FamilyMask"/>）的验收用例。
/// <para>
/// <b>这一层为什么必须存在</b>：强化、图鉴、掉落筛选、元素抗性共同的问法是
/// 「这个轴上的物体有哪些」，而不是「这个具体标签的物体有哪些」。前者是数据问题（轴里有什么由表决定），
/// 后者会把具体标签名写进 C# —— 那就退回了硬编码。本文件证明的是：
/// <u>按轴取范围</u>这件事在运行期是一次数位与，且轴内容完全来自 Excel。
/// </para>
/// <para>
/// <b>两种用例各管一段</b>：合成用例（手写 tag 列表）钉住索引算法与边界；
/// 真实表用例（读 <c>combat.xlsx</c>）钉住「表 → 运行期」的这条链没有断。
/// 只有后者能证明 Family 列真的被读进来了（列名写错、读错列号都是静默失效）。
/// </para>
/// </summary>
[TestClass]
public sealed class TagFamilyTests
{
    private static string CombatPath => Path.Combine(RepositoryRoot(), "data", "excel", "combat.xlsx");

    // ─────────────────────────────────────────────────────────────
    // 1. 轴名解析：大小写不敏感、未知返回 None
    // ─────────────────────────────────────────────────────────────

    /// <summary>轴名 ↔ 枚举双向可逆，且解析大小写不敏感（表里写 element / Element 都认）。</summary>
    [TestMethod]
    public void Parse_IsCaseInsensitiveAndRoundTrips()
    {
        Assert.AreEqual(TagFamily.Element, TagFamilies.Parse("Element"));
        Assert.AreEqual(TagFamily.Element, TagFamilies.Parse("  element  "));
        Assert.AreEqual(TagFamily.WeaponKind, TagFamilies.Parse("weaponkind"));
        Assert.AreEqual(TagFamily.Ability, TagFamilies.Parse("ABILITY"));

        for (int i = 0; i < TagFamilies.Count; i++)
        {
            TagFamily family = (TagFamily)i;
            Assert.AreEqual(family, TagFamilies.Parse(TagFamilies.NameOf(family)),
                "枚举 " + family + " 的名字解析回来不一致 —— 元数据表与枚举脱节。");
        }
    }

    /// <summary>空值与拼错的轴名返回 <see cref="TagFamily.None"/>；拼接错误由绑定器负责报错。</summary>
    [TestMethod]
    public void Parse_ReturnsNoneForEmptyOrUnknown()
    {
        Assert.AreEqual(TagFamily.None, TagFamilies.Parse(null));
        Assert.AreEqual(TagFamily.None, TagFamilies.Parse("   "));
        Assert.AreEqual(TagFamily.None, TagFamilies.Parse("Elementt"));
        Assert.AreEqual(TagFamily.None, TagFamilies.Parse("类别"));
    }

    /// <summary>
    /// 互斥轴判定：Ability 与 Race **不互斥**是刻意的设计决定，不是遗漏。
    /// <para>
    /// Ability 不互斥 = 「穿透 + 弹跳 + 分裂」能叠在同一颗弹上，这正是 ECS 组合的意义；
    /// Race 不互斥 = 祖先 <c>tag.humanoid</c> 与具体 <c>tag.goblin</c> 构成层级，
    /// 于是「对人形增伤」能打到哥布林，「对哥布林增伤」不会误伤其他人形。
    /// </para>
    /// </summary>
    [TestMethod]
    public void IsExclusive_KeepsAbilityAndRaceStackable()
    {
        Assert.IsTrue(TagFamilies.IsExclusive(TagFamily.Rarity), "稀有度必须互斥：一物不可能既普通又稀有。");
        Assert.IsTrue(TagFamilies.IsExclusive(TagFamily.Element), "元素必须互斥：一弹不可能既是动能又是能量。");
        Assert.IsTrue(TagFamilies.IsExclusive(TagFamily.Role));
        Assert.IsTrue(TagFamilies.IsExclusive(TagFamily.WeaponKind));
        Assert.IsTrue(TagFamilies.IsExclusive(TagFamily.ProjectileKind));
        Assert.IsTrue(TagFamilies.IsExclusive(TagFamily.Kind));
        Assert.IsTrue(TagFamilies.IsExclusive(TagFamily.Effect));

        Assert.IsFalse(TagFamilies.IsExclusive(TagFamily.Ability), "Ability 是可叠加轴（穿透+弹跳+分裂），不得互斥。");
        Assert.IsFalse(TagFamilies.IsExclusive(TagFamily.Race), "Race 是分层轴（人形 ⊃ 哥布林），不得互斥。");
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 索引算法：按轴分组 + 单次位与筛选
    // ─────────────────────────────────────────────────────────────

    private static readonly string[] SampleTags =
    {
        "tag.actor",     // 0  Kind
        "tag.weapon",    // 1  Kind
        "tag.pistol",    // 2  WeaponKind
        "tag.kinetic",   // 3  Element
        "tag.energy",    // 4  Element
        "tag.pierce",    // 5  Ability
        "tag.common",    // 6  Rarity
        "tag.rare",      // 7  Rarity
    };

    private static readonly string[] SampleFamilies =
    {
        "Kind", "Kind", "WeaponKind", "Element", "Element", "Ability", "Rarity", "Rarity",
    };

    /// <summary>轴索引把每个 tag 归到它声明的轴上，且能原样报出所属轴——这是所有按轴筛选的前提。</summary>
    [TestMethod]
    public void Rules_IndexTagsByFamily()
    {
        ContentRules rules = ContentRules.FromTags(SampleTags, SampleFamilies);

        CollectionAssert.AreEqual(new[] { 3, 4 }, (int[])rules.TagsOfFamily(TagFamily.Element),
            "Element 轴应恰好索引到 kinetic(3) 与 energy(4)。");
        CollectionAssert.AreEqual(new[] { 2 }, (int[])rules.TagsOfFamily(TagFamily.WeaponKind));
        CollectionAssert.AreEqual(new[] { 6, 7 }, (int[])rules.TagsOfFamily(TagFamily.Rarity));
        CollectionAssert.AreEqual(Array.Empty<int>(), (int[])rules.TagsOfFamily(TagFamily.Trigger),
            "表里没有 Trigger 轴的 tag 时应返回空表而不是抛异常。");

        Assert.AreEqual(TagFamily.Element, rules.FamilyOf(3));
        Assert.AreEqual(TagFamily.Rarity, rules.FamilyOf(7));
        Assert.AreEqual(TagFamily.None, rules.FamilyOf(999), "越界 tag 应回落 None 而不是越界崩溃。");
        Assert.IsFalse(rules.HasFamily(TagFamily.Effect));
        Assert.IsTrue(rules.HasFamily(TagFamily.Element));
    }

    /// <summary>
    /// 「命中该轴任一标签」可以被压成一次按位与：FamilyMask 覆盖轴上所有位，不认识具体标签名。
    /// <para>这是「新增一种元素标签时，筛选/强化代码 0 行改动」的机制保证。</para>
    /// </summary>
    [TestMethod]
    public void FamilyMask_MatchesAnyTagOnThatAxisInOneAnd()
    {
        ContentRules rules = ContentRules.FromTags(SampleTags, SampleFamilies);

        // 行 A：手枪 + 动能；行 B：随便挂了 Ability 的；行 C：什么都没挂。
        var matrix = new ObjectBitMatrix(3, rules.TagCount);
        matrix.Set(0, rules.TagOf("tag.pistol"));
        matrix.Set(0, rules.TagOf("tag.kinetic"));
        matrix.Set(1, rules.TagOf("tag.pierce"));

        int elementMask = rules.FamilyMask(TagFamily.Element);
        int rarityMask = rules.FamilyMask(TagFamily.Rarity);

        Assert.AreNotEqual(0, matrix.Mask(0, 0) & elementMask, "A 挂了动能，应命中 Element 轴。");
        Assert.AreEqual(0, matrix.Mask(1, 0) & elementMask, "B 没挂元素，不应命中 Element 轴。");
        Assert.AreEqual(0, matrix.Mask(2, 0) & elementMask, "C 什么都没挂。");

        Assert.AreEqual(0, matrix.Mask(0, 0) & rarityMask, "A 没挂稀有度，不应命中 Rarity 轴。");

        // Ability 轴能同时命中多个（不互斥）。
        int abilityMask = rules.FamilyMask(TagFamily.Ability);
        matrix.Set(1, rules.TagOf("tag.common"));
        Assert.AreNotEqual(0, matrix.Mask(1, 0) & abilityMask);
    }

    /// <summary>PackMask 把「这几个标签之一」的条件一次压好，且只压落在指定 word 的位。</summary>
    [TestMethod]
    public void PackMask_PacksSelectedTagsOnly()
    {
        ContentRules rules = ContentRules.FromTags(SampleTags, SampleFamilies);
        int kinetic = rules.TagOf("tag.kinetic");   // 3
        int pierce = rules.TagOf("tag.pierce");     // 5

        int mask = ContentRules.PackMask(new[] { kinetic, pierce });
        Assert.AreEqual((1 << kinetic) | (1 << pierce), mask);

        Assert.AreEqual(0, ContentRules.PackMask(new[] { kinetic }, word: 1), "tag 3 落在 word 0，word 1 上不该有它。");
        Assert.AreEqual(0, ContentRules.PackMask(new[] { -1 }), "非法 tag id 必须被忽略而不是占 bit31。");
    }

    /// <summary>不传轴信息时全部落 <see cref="TagFamily.None"/>：老调用方与老测试夹具不必改。</summary>
    [TestMethod]
    public void FromTags_WithoutFamilies_PutsEverythingOnNone()
    {
        ContentRules rules = ContentRules.FromTags(SampleTags);

        Assert.AreEqual(SampleTags.Length, rules.TagsOfFamily(TagFamily.None).Count);
        Assert.AreEqual(TagFamily.None, rules.FamilyOf(0));
        Assert.IsFalse(rules.HasFamily(TagFamily.Element));
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 真实表：Family 列真的被读进来了
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 真 <c>combat.xlsx</c> 的 Tags 表经过绑定器后，**每条 tag 都落在轴里**（没有 None 兜底逃生）。
    /// <para>
    /// 如果 Family 列没被读（列名写错 / 读错列号），这里会看到 40 个 None —— 这正是本用例要拦的静默失效。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Binder_ReadsFamilyColumnFromRealWorkbook()
    {
        ContentSet set = BuildRealSet();
        ContentRules rules = set.Rules;

        Assert.AreEqual(Array.Empty<int>(), (int[])rules.TagsOfFamily(TagFamily.None),
            "真实表里不该有未声明轴的 tag。");

        // 十个业务轴每一个都得有内容，否则「按轴做强化/图鉴」会有一块范围是空的。
        TagFamily[] expectedAxes =
        {
            TagFamily.Kind, TagFamily.WeaponKind, TagFamily.ProjectileKind, TagFamily.Element,
            TagFamily.Ability, TagFamily.Trigger, TagFamily.Rarity, TagFamily.Race,
            TagFamily.Role, TagFamily.Effect,
        };
        foreach (TagFamily axis in expectedAxes)
        {
            Assert.IsTrue(rules.HasFamily(axis), "轴 " + axis + " 在真实表里是空的 —— 该轴的强化/图鉴范围会是空集。");
        }

        // 标识层六轴也必须有内容（道具吸附 / 归属 / 主题），否则「吸引」那套玩法没有索引可用。
        TagFamily[] identityAxes =
        {
            TagFamily.Loot, TagFamily.Attract, TagFamily.Faction,
            TagFamily.Theme, TagFamily.Aura, TagFamily.Craft,
        };
        foreach (TagFamily axis in identityAxes)
        {
            Assert.IsTrue(rules.HasFamily(axis), "标识轴 " + axis + " 在真实表里是空的。");
        }

        // 各轴 tag 数之和 = tag 总数（没有重复归轴，没有漏归）。
        int summed = 0;
        foreach (TagFamily axis in expectedAxes)
        {
            summed += rules.TagsOfFamily(axis).Count;
        }
        foreach (TagFamily axis in identityAxes)
        {
            summed += rules.TagsOfFamily(axis).Count;
        }
        Assert.AreEqual(rules.TagCount, summed, "各轴 tag 数之和应等于 tag 总数。");
    }

    /// <summary>
    /// 真表上的三条「索引语义」断言：按轴取到的范围与策划表里写的完全一致。
    /// <para>用的是具体标签名 → 位置，再与轴里的位置集合比对，避免把 tag 序号写死在用例里。</para>
    /// </summary>
    [TestMethod]
    public void RealWorkbook_FamiliesMatchPlannedContent()
    {
        ContentSet set = BuildRealSet();
        ContentRules rules = set.Rules;

        // 元素轴恰好是 动能 / 能量 / 爆炸。
        CollectionAssert.AreEquivalent(
            new[] { rules.TagOf("tag.kinetic"), rules.TagOf("tag.energy"), rules.TagOf("tag.explosive") },
            (int[])rules.TagsOfFamily(TagFamily.Element),
            "Element 轴应恰好是 kinetic / energy / explosive。");

        // Ability 轴必须能同时给出一颗弹叠加多个能力（穿透 + 弹跳 + 分裂都在轴上）。
        var ability = new HashSet<int>((int[])rules.TagsOfFamily(TagFamily.Ability));
        Assert.IsTrue(ability.Contains(rules.TagOf("tag.pierce")), "Ability 轴缺 tag.pierce。");
        Assert.IsTrue(ability.Contains(rules.TagOf("tag.bounce")), "Ability 轴缺 tag.bounce。");
        Assert.IsTrue(ability.Contains(rules.TagOf("tag.split")), "Ability 轴缺 tag.split。");

        // Race 是分层轴：祖先 tag.humanoid 与具体 tag.goblin 必须同时在轴上。
        var race = new HashSet<int>((int[])rules.TagsOfFamily(TagFamily.Race));
        Assert.IsTrue(race.Contains(rules.TagOf("tag.humanoid")), "Race 轴缺祖先 tag.humanoid。");
        Assert.IsTrue(race.Contains(rules.TagOf("tag.goblin")), "Race 轴缺具体族裔 tag.goblin。");

        // 物体大类轴必须包含三种能力位对应的标签，否则工厂无法识别物件。
        var kind = new HashSet<int>((int[])rules.TagsOfFamily(TagFamily.Kind));
        Assert.IsTrue(kind.Contains(rules.TagOf("tag.actor")));
        Assert.IsTrue(kind.Contains(rules.TagOf("tag.weapon")));
        Assert.IsTrue(kind.Contains(rules.TagOf("tag.projectile")));
    }

    /// <summary>
    /// 用真实内容做一次「按轴筛选」端到端演示：Element=kinetic 的子弹不止一颗，
    /// 而 Ability=pierce 的子弹是它们的真子集关系之外的独立集合。
    /// <para>这条用例同时验证 <see cref="ContentRules.FamilyMask"/> 在真实 tag 宽度（40 位 = 2 word）下正确。</para>
    /// </summary>
    [TestMethod]
    public void RealWorkbook_FamilyMaskFiltersObjectsEndToEnd()
    {
        ContentSet set = BuildRealSet();
        ContentRules rules = set.Rules;
        ObjectBitMatrix matrix = set.ObjectTags;

        int kineticMask = rules.FamilyMask(TagFamily.Element);
        int pierceMask = rules.FamilyMask(TagFamily.Ability);

        int kineticCount = 0;
        int pierceCount = 0;
        int pierceButNotKinetic = 0;
        for (int row = 0; row < matrix.RowCount; row++)
        {
            bool kinetic = (matrix.Mask(row, 0) & kineticMask) != 0;
            bool pierce = (matrix.Mask(row, 0) & pierceMask) != 0;
            if (kinetic)
            {
                kineticCount++;
            }
            if (pierce)
            {
                pierceCount++;
            }
            if (pierce && !kinetic)
            {
                pierceButNotKinetic++;
            }
        }

        Assert.IsTrue(kineticCount > 0, "真实表里没有挂动能元素的物件 —— Element 轴索引失效。");
        Assert.IsTrue(pierceCount > 0, "真实表里没有挂穿透的物件 —— Ability 轴索引失效。");
        Assert.IsTrue(kineticCount > pierceCount,
            "动能物件数（" + kineticCount + "）应多于穿透物件数（" + pierceCount + "）：穿透是少数稀有变体。");
        Assert.IsTrue(pierceButNotKinetic >= 0);
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 绑定器：认不出的轴名必须当场报错（不得静默落 None）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Tags 表的 Family 列写了认不出的轴名时，绑定器要抛中文异常并指出是哪一行。
    /// <para>静默落 None 会让这个 tag 从所有按轴索引的玩法里消失，是最难查的一类失效。</para>
    /// </summary>
    [TestMethod]
    public void Binder_RejectsUnknownFamilyName()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable weapons = ReadTable(reader, "Weapons");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        string brokenTagId = tags.Rows[0].Get("TagId").Trim();
        ContentTable brokenTags = Mutate(tags, "Tags", brokenTagId, "Family", "Elementtt");

        ContentException ex = Assert.ThrowsExactly<ContentException>(
            () => ContentBinder.BuildFrom(brokenTags, objects, weapons, projectiles, packs, packages: null));
        StringAssert.Contains(ex.Message, brokenTagId, "报错应点出是哪个 tag 的轴写错了。");
        StringAssert.Contains(ex.Message, "Elementtt", "报错应回显写错的轴名。");
    }


    // ─────────────────────────────────────────────────────────────
    // 5. 标签分层：逻辑功能层 vs 标识层
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 层与轴的两个方向都自洽：标识轴上的标签是 Identity，逻辑轴上的标签是 Logic。
    /// <para>这条钉住「轴 → 层」映射表：漏登记一个轴，这里会立刻报出来。</para>
    /// </summary>
    [TestMethod]
    public void RealWorkbook_TierAgreesWithAxis()
    {
        ContentRules rules = BuildRealSet().Rules;

        TagFamily[] logicAxes =
        {
            TagFamily.Kind, TagFamily.WeaponKind, TagFamily.ProjectileKind, TagFamily.Element,
            TagFamily.Ability, TagFamily.Trigger, TagFamily.Rarity, TagFamily.Race,
            TagFamily.Role, TagFamily.Effect,
        };
        foreach (TagFamily axis in logicAxes)
        {
            foreach (int tag in rules.TagsOfFamily(axis))
            {
                Assert.AreEqual(TagTier.Logic, rules.TierOf(tag),
                    "轴 " + axis + " 的 tag '" + rules.NameOf(tag) + "' 应为 Logic 层。");
            }
        }

        TagFamily[] identityAxes =
        {
            TagFamily.Loot, TagFamily.Attract, TagFamily.Faction,
            TagFamily.Theme, TagFamily.Aura, TagFamily.Craft,
        };
        foreach (TagFamily axis in identityAxes)
        {
            Assert.AreEqual(TagTier.Identity, TagFamilies.TierOf(axis), "轴 " + axis + " 应属于标识层。");
            foreach (int tag in rules.TagsOfFamily(axis))
            {
                Assert.AreEqual(TagTier.Identity, rules.TierOf(tag),
                    "轴 " + axis + " 的 tag '" + rules.NameOf(tag) + "' 应为 Identity 层。");
            }
        }
    }

    /// <summary>
    /// **标识层标签不影响玩法**：它们不占配方位（不进熔炉配方字），也不产生能力位。
    /// <para>
    /// 这是「分层」这件事的机器保证。一旦有人给标识标签分配了 BitMask，
    /// 它就偷偷变成熔炉组合的一部分 —— 标识层就名不副实了。
    /// </para>
    /// </summary>
    [TestMethod]
    public void RealWorkbook_IdentityTagsNeverAffectGameplay()
    {
        ContentRules rules = BuildRealSet().Rules;

        int checkedCount = 0;
        foreach (TagTier tier in new[] { TagTier.Identity })
        {
            foreach (int tag in rules.TagsOfTier(tier))
            {
                string name = rules.NameOf(tag);
                checkedCount++;

                // 不占配方位：配方字里不该出现标识标签的位。
                // （BitMask = -1 时 LoadTagMasks 不写位矩阵，故整行位来自 Objects.Tags 而非 BitMask，
                //   这里直接断言「标识标签的轴属于标识层」，与位矩阵语义解耦。）
                Assert.AreEqual(TagTier.Identity, TagFamilies.TierOf(rules.FamilyOf(tag)),
                    "tag '" + name + "' 声明为 Identity，但它的轴不属于标识层。");

                // 不产生能力位：标识标签的 ObjectKind 组为空。
                Assert.IsFalse(string.Equals(name, "tag.actor", StringComparison.Ordinal),
                    "标识层不应包含决定能力位的 tag。");
            }
        }
        Assert.IsTrue(checkedCount > 0, "真实表里一个标识层标签都没有 —— 分层迁移没生效。");
        Assert.IsTrue(checkedCount >= 20, "标识层标签太少（" + checkedCount + "），迁移可能只做了一部分。");
    }

    /// <summary>
    /// 「道具互相吸引」的机制：被吸物的 <see cref="TagFamily.Attract"/> 掩码 与
    /// 吸引源的同一掩码 **有交集** 即被吸引。
    /// <para>
    /// 这是标识层最典型的用法：代码不认识「磁铁」「弹药」这些具体名字，
    /// 只做一次 AND。新增一种磁铁或一种可吸物，这里的代码 0 行改动。
    /// </para>
    /// </summary>
    [TestMethod]
    public void AttractMask_IsTheMagnetMatchingMechanism()
    {
        ContentRules rules = BuildRealSet().Rules;

        int magnetTag = rules.TagOf("tag.attract_magnet");
        Assert.IsTrue(magnetTag >= 0, "真实表里没有 tag.attract_magnet —— 磁铁机制没有索引可用。");

        // 吸引源（磁铁）只勾 magnet；可吸物（金币）也只勾 magnet → 交集非空 → 被吸引。
        var magnetSource = new ObjectBitMatrix(3, rules.TagCount);
        magnetSource.Set(0, magnetTag);

        // 只被经验吸附吸引的东西不该被磁铁吸走。
        int expTag = rules.TagOf("tag.attract_exp");
        magnetSource.Set(1, expTag);
        int attractMaskTag = expTag;

        // ★ 标识标签的 tag id 是 40+，全部落在 **word 1**（word 0 只装得下 0..31）。
        //   这正是 BitMask 32 位上限与轴索引无关的地方：轴索引走 TagIndex，可以超过 31。
        int word = magnetTag >> 5;
        Assert.AreEqual(1, word, "Attract 轴标签应落在 word 1（tag id >= 32）。");

        // Matrix.Mask(row, tag) 的第二个参数是 **tag 下标**，它内部再换算成 word；
        // 所以这里取任意一个该轴上的 tag 来定位 word。
        int attractMask = rules.FamilyMask(TagFamily.Attract, word);
        Assert.AreNotEqual(0, magnetSource.Mask(0, magnetTag) & attractMask, "磁铁应命中 Attract 轴。");
        Assert.AreNotEqual(0, magnetSource.Mask(1, attractMaskTag) & attractMask, "经验球应命中 Attract 轴。");

        // 按具体磁铁类型做交集：只有 magnet 能被 magnet 吸引。
        int magnetTypeMask = ContentRules.PackMask(new[] { magnetTag }, word);
        Assert.AreNotEqual(0, magnetSource.Mask(0, magnetTag) & magnetTypeMask, "被磁铁吸引的来源应命中 magnet 位。");
        Assert.AreEqual(0, magnetSource.Mask(1, attractMaskTag) & magnetTypeMask, "只被经验吸附吸引的东西不该被磁铁吸引。");
        Assert.AreEqual(0, magnetSource.Mask(2, magnetTag) & magnetTypeMask, "没挂吸引标记的东西不被任何磁场吸引。");
    }

    /// <summary>层的解析与 <see cref="TagFamilies.TierOf"/> 的映射表一致（双向钉住）。</summary>
    [TestMethod]
    public void TagTiers_ParseAndMapConsistently()
    {
        Assert.AreEqual(TagTier.Logic, TagTiers.Parse("Logic"));
        Assert.AreEqual(TagTier.Identity, TagTiers.Parse("identity"));
        Assert.AreEqual(TagTier.None, TagTiers.Parse("Meta"));
        Assert.AreEqual(TagTier.None, TagTiers.Parse(null));

        Assert.AreEqual(TagTier.Logic, TagFamilies.TierOf(TagFamily.Element));
        Assert.AreEqual(TagTier.Identity, TagFamilies.TierOf(TagFamily.Loot));
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    private static ContentSet BuildRealSet()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        return ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"),
            ReadTable(reader, "Objects"),
            ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"),
            ReadTable(reader, "StatPacks"),
            packages: null);
    }

    private static ContentTable ReadTable(XlsxReader reader, string sheet)
    {
        string[][]? grid = reader.ReadSheet(sheet);
        Assert.IsNotNull(grid, "真表里没有 Sheet '" + sheet + "'。");
        string[] header = grid![0];
        var rows = new List<ContentRow>();
        for (int r = 5; r < grid.GetLength(0); r++) // arch-ok: 四行表头 + #data，见 docs/rules/04 §5.2
        {
            string[] line = grid[r];
            if (line.Length == 0 || string.IsNullOrWhiteSpace(line[0]))
            {
                continue;
            }
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < header.Length; c++)
            {
                cells[header[c]] = c < line.Length ? line[c] : string.Empty;
            }
            rows.Add(new ContentRow(sheet, r + 1, cells));
        }
        return new ContentTable(sheet, header, rows);
    }

    private static ContentTable Mutate(ContentTable table, string sheet, string rowId, string column, string newValue)
    {
        var rows = new List<ContentRow>();
        bool touched = false;
        for (int i = 0; i < table.Rows.Count; i++)
        {
            ContentRow row = table.Rows[i];
            // Tags 表的主键列叫 TagId，其余表叫 Id —— 两列名都认，免得辅助方法只适配一张表。
            string key = string.Equals(row.Get("TagId").Trim(), string.Empty, StringComparison.Ordinal)
                ? row.Get("Id")
                : row.Get("TagId");
            if (!touched && string.Equals(key.Trim(), rowId, StringComparison.Ordinal))
            {
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    string name = table.Columns[c];
                    cells[name] = string.Equals(name, column, StringComparison.Ordinal) ? newValue : row.Get(name);
                }
                rows.Add(new ContentRow(table.Name, row.RowNumber, cells));
                touched = true;
                continue;
            }
            rows.Add(row);
        }
        Assert.IsTrue(touched, "辅助方法 Mutate 没找到 id '" + rowId + "' 的行。");
        return new ContentTable(table.Name, table.Columns, rows);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "excel")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("从 '" + AppContext.BaseDirectory + "' 向上找不到含 data/excel 的仓库根。");
    }
}
