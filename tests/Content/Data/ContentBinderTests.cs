using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;
using Game.Data;
using Game.Data.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Content.Data;

/// <summary>
/// 「基础物件抽象」数据层的验收用例：<c>combat.xlsx</c> → 运行期 <see cref="ObjectArchetype"/> /
/// <see cref="WeaponContract"/> / <see cref="ProjectileContract"/> / <see cref="ContentRules"/> / 熔炉候选表。
/// <para>
/// <b>为什么这些用例读真 xlsx 而不是手写内容</b>：本任务的核心承诺是「策划改表 → 运行时对象随之改变」。
/// 手写 Def 只能证明绑定器会算数，不能证明<u>表和代码之间没有断链</u>（列名写错、Sheet 改名、规则值语法漂移）。
/// 每个用例都从 <c>data/excel/*.xlsx</c> 出发，改一格就重新绑定，于是"改表即生效"是被证明的而不是被声称的。
/// </para>
/// <para>
/// <b>为什么 <see cref="XlsxReader"/> 住在测试工程而不是运行时</b>：ARCHITECTURE §4.8 明令运行期代码不得读
/// <c>.xlsx</c>。读表属于构建期工具与测试的职责，所以它是测试代码；运行时只认已绑定的对象。
/// </para>
/// </summary>
[TestClass]
public sealed class ContentBinderTests
{
    /// <summary>仓库里真表的位置（相对测试输出目录向上找到仓库根）。</summary>
    private static string ExcelRoot => Path.Combine(RepositoryRoot(), "data", "excel");

    private static string CombatPath => Path.Combine(ExcelRoot, "combat.xlsx");

    private static string CharactersPath => Path.Combine(ExcelRoot, "characters.xlsx");

    // ─────────────────────────────────────────────────────────────
    // 1. 读表器：五张表都能读出来
    // ─────────────────────────────────────────────────────────────

    /// <summary>XlsxReader 能读出 combat.xlsx 的五张业务表，且表头与数据行在真实行号上。</summary>
    [TestMethod]
    public void XlsxReader_ReadsAllFiveSheetsOfCombatWorkbook()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);

        string[] expected = { "Tags", "Objects", "Weapons", "Projectiles", "StatPacks" };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.IsTrue(reader.HasSheet(expected[i]), "combat.xlsx 缺少 Sheet '" + expected[i] + "'。");
            ContentTable table = ReadTable(reader, expected[i]);
            Assert.IsTrue(table.Rows.Count > 0, "Sheet '" + expected[i] + "' 没有数据行。");
            Assert.IsTrue(table.Columns.Count > 0, "Sheet '" + expected[i] + "' 没有表头。");
            // 四行表头 + #data 标记 = 数据从第 6 行起（docs/rules/04 §5.2）。
            Assert.AreEqual(6, table.Rows[0].RowNumber, "Sheet '" + expected[i] + "' 的数据应从 Excel 第 6 行开始。");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 真实表全量绑定：零外键缺失
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 绑定真 <c>combat.xlsx</c>：0 外键缺失（任何一处引用拼错都会在这里抛中文异常），
    /// 并且物件 / 武器 / 子弹与表里的行数一致。
    /// </summary>
    [TestMethod]
    public void Binder_BindsRealWorkbookWithoutForeignKeyMisses()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable weapons = ReadTable(reader, "Weapons");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        ContentSet set = ContentBinder.BuildFrom(tags, objects, weapons, projectiles, packs, packages: null);

        Assert.AreEqual(objects.Rows.Count, set.Index.ObjectCount, "物件数应与 Objects 表数据行数一致。");
        Assert.AreEqual(weapons.Rows.Count, set.Index.WeaponCount, "武器数应与 Weapons 表数据行数一致。");
        Assert.AreEqual(projectiles.Rows.Count, set.Index.ProjectileCount, "子弹数应与 Projectiles 表数据行数一致。");
        Assert.AreEqual(tags.Rows.Count, set.Rules.TagCount, "tag 数应与 Tags 表数据行数一致。");

        // 每个物件都要能从 id 走回自己的槽位，再取回同一个 Def。
        for (int i = 0; i < objects.Rows.Count; i++)
        {
            string id = objects.Rows[i].Get("Id");
            int slot = set.Index.ObjectSlotOf(id);
            Assert.IsTrue(slot >= 0, "物件 '" + id + "' 在内容索引里找不到槽位。");
            Assert.AreEqual(id, set.Index.GetObject(slot).Id, "槽位号与物件 id 不对应。");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 标签矩阵：bullet_101 = 动能 1 / 追踪 0
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 位矩阵按「行 = 物件槽位、列 = tag 下标」编址：<c>bullet_101</c> 勾了动能、没勾追踪。
    /// <para>这条用例钉住的是"tag 名字 → 位下标"的翻译：翻译错位会让熔炉的匹配条件整体漂移。</para>
    /// </summary>
    [TestMethod]
    public void TagMatrix_MarksKineticAndNotHomingForBullet101()
    {
        ContentSet set = BuildRealSet();
        int row = set.Index.ObjectSlotOf("bullet_101");
        Assert.IsTrue(row >= 0, "bullet_101 应存在于 Objects 表。");

        int kinetic = set.Rules.TagOf("tag.kinetic");
        int homing = set.Rules.TagOf("tag.homing");
        Assert.IsTrue(kinetic >= 0, "Tags 表应登记 tag.kinetic。");
        Assert.IsTrue(homing >= 0, "Tags 表应登记 tag.homing。");

        Assert.IsTrue(set.ObjectTags.Has(row, kinetic), "bullet_101 应带 tag.kinetic。");
        Assert.AreNotEqual(0, set.ObjectTags.Mask(row, kinetic) & (1 << (kinetic % ObjectBitMatrix.BitsPerWord)), "Mask 里 kinetic 位应为 1。");
        Assert.IsFalse(set.ObjectTags.Has(row, homing), "bullet_101 不应带 tag.homing。");
        Assert.AreEqual(0, set.ObjectTags.Mask(row, homing) & (1 << (homing % ObjectBitMatrix.BitsPerWord)), "Mask 里 homing 位应为 0。");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 契约：武器把数值列解析成 ValueRef（而不是数字）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>pistol_101.BaseDamage = "wstat:WeaponDamage(10)"</c> 必须绑成
    /// <see cref="ValueSource.WeaponStat"/>，而不是被当成字面量 0 —— 否则"同一颗子弹挂不同枪"就失效了。
    /// </summary>
    [TestMethod]
    public void WeaponContract_BindsRuleValueStringsToValueSources()
    {
        ContentSet set = BuildRealSet();
        int slot = set.Index.WeaponSlotOf("pistol_101");
        Assert.IsTrue(slot >= 0, "pistol_101 应存在于 Weapons 表。");
        WeaponContract pistol = set.Index.GetWeapon(slot);

        Assert.AreEqual("pistol_101", pistol.DefId, "武器 DefId 应与表里 Id 一致。");
        Assert.AreEqual(ValueSource.WeaponStat, pistol.Damage[0].Source, "BaseDamage 应绑成 wstat 规则值。");
        Assert.AreEqual(ValueSource.WeaponStat, pistol.FireDelay[0].Source, "FireInterval 应绑成 wstat 规则值。");
        // SpreadDeg = rand:2+0.5 → 随机抖动
        Assert.AreEqual(ValueSource.RandomScaled, pistol.SpreadDegrees[0].Source, "SpreadDeg 应绑成 rand:基准+抖动。");

        // 冲锋枪的 BaseDamage 是裸字面量 6。
        WeaponContract smg = set.Index.GetWeapon(set.Index.WeaponSlotOf("smg_102"));
        Assert.AreEqual(ValueSource.Literal, smg.Damage[0].Source, "smg_102 的 BaseDamage '6' 应绑成字面量。");
        Assert.AreEqual(6f, smg.Damage[0].A, "BaseDamage 字面量应保留原值。");
    }

    /// <summary>
    /// <c>orb_501.BaseDamage = "flags:tag.split"</c>：伤害来自位矩阵的 tag 位。
    /// 求值时位为 0 就得 0 伤害 —— 这条链路若不翻译成 tag 下标，求值端会把名字当 0。
    /// </summary>
    [TestMethod]
    public void WeaponContract_BindsFlagRuleToTagIndex()
    {
        ContentSet set = BuildRealSet();
        WeaponContract orb = set.Index.GetWeapon(set.Index.WeaponSlotOf("orb_501"));
        ValueRef damage = orb.Damage[0];

        Assert.AreEqual(ValueSource.Flags, damage.Source, "flags: 规则应绑成 ValueSource.Flags。");
        Assert.AreEqual(set.Rules.TagOf("tag.split"), damage.Tag, "flags: 的 tag 必须翻成 Tags 表的下标。");
        Assert.AreEqual(1, damage.A, "不带掩码的 flags: 默认掩码为 1。");
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 契约：子弹的链式生成引用指向物件
    // ─────────────────────────────────────────────────────────────

    /// <summary><c>proj_arrow_202.OnExpireProjectile = "bullet_201"</c> 必须绑成非空引用，且能回溯到物件。</summary>
    /// <remarks>
    /// 查槽位用<b>物件 id</b>（<c>arrow_202</c>），不是 Projectiles 表的主键 <c>proj_arrow_202</c>：
    /// 所有引用列（Weapons.ProjectileRef / Objects.Projectile / OnExpireProjectile）都按物件 id 指子弹，
    /// 契约主键必须跟引用口径一致，否则 ContentIndex.ProjectileSlotOf 恒返回 NoneSlot。
    /// </remarks>
    [TestMethod]
    public void ProjectileContract_BindsSpawnReferencesToObjects()
    {
        ContentSet set = BuildRealSet();
        ProjectileContract arrow = set.Index.GetProjectile(set.Index.ProjectileSlotOf("arrow_202"));

        Assert.AreEqual(ProjectileMotion.Straight, arrow.Motion, "Motion 列应绑成枚举。");
        Assert.AreEqual(ProjectileHitPolicy.Pierce, arrow.HitPolicy, "HitPolicy 列应绑成枚举。");
        Assert.AreEqual(ProjectileDeath.SpawnProjectile, arrow.OnExpire, "OnExpire 列应绑成枚举。");
        Assert.IsFalse(arrow.OnExpireProjectile.IsEmpty, "OnExpireProjectile 不应是空引用。");
        Assert.AreEqual("bullet_201", arrow.OnExpireProjectile.Id, "OnExpireProjectile 应指向 Objects.Id 的 bullet_201。");
        Assert.IsTrue(set.Index.ObjectSlotOf(arrow.OnExpireProjectile.Id) >= 0, "被引用的物件必须能在索引里找到。");

        // 没有填引用的列必须是空引用（而不是一个指向槽位 0 的假引用）。
        ProjectileContract bullet = set.Index.GetProjectile(set.Index.ProjectileSlotOf("bullet_101"));
        Assert.IsTrue(bullet.OnExpireActor.IsEmpty, "bullet_101 没填 OnExpireActor，应为空引用。");
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 熔炉候选表：按 Cluster 分组 + 扁平数组
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 熔炉候选表按 <c>Objects.Cluster</c> 分组，每组是三条等长扁平数组（槽位 / 权重 / 可用数），
    /// 且候选集合与表里该簇的物件集合一一对应。
    /// </summary>
    [TestMethod]
    public void FurnaceCandidates_GroupByClusterIntoFlatArrays()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable materialized = ReadTable(reader, "Objects");
        ContentSet set = ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"), materialized, ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"), ReadTable(reader, "StatPacks"), packages: null);
        Assert.IsTrue(set.Clusters.Count > 0, "熔炉候选表不应为空。");

        int total = 0;
        for (int i = 0; i < set.Clusters.Count; i++)
        {
            ClusterCandidates cluster = set.Clusters[i];
            Assert.IsTrue(cluster.Count > 0, "簇 '" + cluster.Cluster + "' 不应是空簇。");
            Assert.AreEqual(cluster.Count, cluster.ObjectSlots.Length, "ObjectSlots 与权重数组必须等长。");
            Assert.AreEqual(cluster.Count, cluster.Weights.Length, "Weights 长度必须与候选数一致。");
            Assert.AreEqual(cluster.Count, cluster.ViableCounts.Length, "ViableCounts 长度必须与候选数一致。");
            total += cluster.Count;
        }

        int kineticSlot = set.ClusterSlotOf("proj_kinetic");
        Assert.IsTrue(kineticSlot >= 0, "Objects 表应出现 cluster 'proj_kinetic'。");
        ClusterCandidates kinetic = set.CandidatesOf(kineticSlot)!;
        Assert.IsNotNull(kinetic, "按簇编号应能取回候选表（热路径一次数组下标）。");

        // 候选数按表算，不写死：策划往 proj_kinetic 簇里再加一颗子弹时，
        // 这条用例不该因为"数字变了"而翻红 —— 它要守的是"候选表与该簇成员一一对应"。
        int expectedKinetic = 0;
        for (int i = 0; i < materialized.Rows.Count; i++)
        {
            if (materialized.Rows[i].Get("Cluster").Trim() == "proj_kinetic")
            {
                expectedKinetic++;
            }
        }
        Assert.AreEqual(expectedKinetic, kinetic.Count, "proj_kinetic 簇的候选数应与表里该簇的物件数一致。");
        Assert.IsTrue(kinetic.Count >= 2, "proj_kinetic 簇应至少有两个候选（否则不构成可替换簇）。");

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < kinetic.Count; i++)
        {
            names.Add(set.Index.GetObject(kinetic.ObjectSlots[i]).Id);
            Assert.AreEqual(kineticSlot, set.Index.GetObject(kinetic.ObjectSlots[i]).ClusterSlot, "候选物件的簇编号应与候选表一致。");
        }
        Assert.IsTrue(names.Contains("bullet_101"), "proj_kinetic 簇应含 bullet_101。");
        Assert.IsTrue(names.Contains("arrow_202"), "proj_kinetic 簇应含 arrow_202。");
    }

    // ─────────────────────────────────────────────────────────────
    // 7. 「策划改表 → 运行时改变」闭环
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 改一格 <c>BaseDamage</c> 后重新绑定，<see cref="WeaponContract.Damage"/> 跟着变。
    /// <para>
    /// 这是本任务最重要的一条：它证明数值<u>没有</u>被编译器内联成常量，读路径确实走了绑定产物。
    /// </para>
    /// </summary>
    [TestMethod]
    public void DesignerEdit_ChangesBoundWeaponDamage()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        // 把 smg_102 的 BaseDamage 从 6 改成 42。为了让规则值求值，这里用字面量（不依赖武器数值包）。
        ContentTable edited = Mutate(ReadTable(reader, "Weapons"), "smg_102", "BaseDamage", "42");

        ContentSet before = ContentBinder.BuildFrom(tags, objects, ReadTable(reader, "Weapons"), projectiles, packs, packages: null);
        ContentSet after = ContentBinder.BuildFrom(tags, objects, edited, projectiles, packs, packages: null);

        float damageBefore = before.Index.GetWeapon(before.Index.WeaponSlotOf("smg_102")).Damage[0].A;
        float damageAfter = after.Index.GetWeapon(after.Index.WeaponSlotOf("smg_102")).Damage[0].A;

        Assert.AreEqual(6f, damageBefore, "改表前 smg_102 的 BaseDamage 应为 6。");
        Assert.AreEqual(42f, damageAfter, "改表后 smg_102 的 BaseDamage 应为 42（绑定器必须读表而不是读常量）。");
        Assert.AreNotEqual(damageBefore, damageAfter, "改一格表应改变运行时数值。");
    }

    // ─────────────────────────────────────────────────────────────
    // 8. 错误路径：重复 id / 外键缺失 / 列名拼错
    // ─────────────────────────────────────────────────────────────

    /// <summary>Objects 表里出现重复 Id：必须抛中文异常，并指出两行行号。</summary>
    [TestMethod]
    public void DuplicateObjectId_ThrowsChineseContentException()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable weapons = ReadTable(reader, "Weapons");
        ContentTable projectiles = ReadTable(reader, "Projectiles");

        // 把第 2 个物件的 Id 改成第 1 个的 Id。
        ContentTable broken = Mutate(objects, objects.Rows[1].Get("Id"), "Id", objects.Rows[0].Get("Id"));

        ContentException ex = Assert.ThrowsExactly<ContentException>(
            () => ContentBinder.BuildFrom(tags, broken, weapons, projectiles, packages: null));

        Assert.IsTrue(ex.Message.Contains("重复", StringComparison.Ordinal),
            "重复 id 的报错应说明'重复'，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains(objects.Rows[0].Get("Id"), StringComparison.Ordinal),
            "报错应点名重复的 id，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("第", StringComparison.Ordinal),
            "报错应给出 Excel 行号，实际：" + ex.Message);
    }

    /// <summary>外键拼错：报错必须同时点名两张表与缺失的 id，否则策划不知道去哪儿改。</summary>
    [TestMethod]
    public void MissingForeignKey_ThrowsExceptionNamingBothTables()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        // pistol_101 的 ProjectileRef 指向不存在的物件。
        ContentTable broken = Mutate(ReadTable(reader, "Weapons"), "pistol_101", "ProjectileRef", "bullet_does_not_exist");

        ContentException ex = Assert.ThrowsExactly<ContentException>(
            () => ContentBinder.BuildFrom(tags, objects, broken, projectiles, packs, packages: null));

        Assert.IsTrue(ex.Message.Contains("bullet_does_not_exist", StringComparison.Ordinal),
            "报错应点名缺失的目标 id，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("Weapons", StringComparison.Ordinal),
            "报错应点名来源表 Weapons，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("Objects", StringComparison.Ordinal),
            "报错应点名目标表 Objects，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("外键", StringComparison.Ordinal),
            "报错应说明是外键问题，实际：" + ex.Message);
    }

    /// <summary>规则值写坏（<c>'abc'</c>）：报错必须带"表名 行号 列名 原文"。</summary>
    [TestMethod]
    public void MalformedRuleValue_ReportsTableRowColumnAndText()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        ContentTable broken = Mutate(ReadTable(reader, "Weapons"), "pistol_101", "FireInterval", "abc");

        ContentException ex = Assert.ThrowsExactly<ContentException>(
            () => ContentBinder.BuildFrom(tags, objects, broken, projectiles, packs, packages: null));

        Assert.IsTrue(ex.Message.Contains("Weapons", StringComparison.Ordinal), "报错应含表名，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("行", StringComparison.Ordinal), "报错应含行号，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("FireInterval", StringComparison.Ordinal), "报错应含列名，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("abc", StringComparison.Ordinal), "报错应含原文，实际：" + ex.Message);
    }

    // ─────────────────────────────────────────────────────────────
    // 9. 跨表合并：characters.xlsx 与 combat.xlsx 的数值包合成一个仓库
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>characters.xlsx</c> 与 <c>combat.xlsx</c> 各有一张 StatPacks 表，两者必须能合并进同一个
    /// <see cref="StatPackageRepository"/>（角色引用 combat 的包、武器引用角色的包，否则两张表互相看不见）。
    /// </summary>
    [TestMethod]
    public void StatPacks_MergeFromTwoWorkbooksIntoOneRepository()
    {
        XlsxReader combat = XlsxReader.Open(CombatPath);
        XlsxReader characters = XlsxReader.Open(CharactersPath);

        ContentBinder combatBinder = ContentBinder.Create(
            ContentBinder.LoadRules(ReadTable(combat, "Tags")), packages: null);
        StatPackageRepository combatPacks = combatBinder.BindStatPacks(ReadTable(combat, "StatPacks"));

        ContentTable characterPacks = ReadTable(characters, "StatPacks");
        ContentBinder characterBinder = ContentBinder.Create(ContentRules.FromTags(Array.Empty<string>()), packages: null);
        StatPackageRepository merged = characterBinder.BindStatPacks(characterPacks);

        Assert.IsTrue(combatPacks.Count > 0, "combat.xlsx 的 StatPacks 应注册出至少一个包。");
        Assert.IsTrue(merged.Count > 0, "characters.xlsx 的 StatPacks 应注册出至少一个包。");

        ContentTable combatStatPacks = ReadTable(combat, "StatPacks");
        string combatPackId = PackIdOf(combatStatPacks.Rows[0]);
        string characterPackId = PackIdOf(characterPacks.Rows[0]);
        Assert.AreNotEqual(combatPackId, characterPackId, "两张表的包名不应撞车（否则合并会静默覆盖数值）。");

        // 合并前后各只看得到自己那张表。
        Assert.IsTrue(combatPacks.Find(combatPackId) is not null, "combat 仓库应含 combat 的包 '" + combatPackId + "'。");
        Assert.IsTrue(combatPacks.Find(characterPackId) is null, "合并前 combat 仓库不应含角色的包。");
        Assert.IsTrue(merged.Find(characterPackId) is not null, "角色仓库应含角色的包 '" + characterPackId + "'。");

        // 合并：把 combat 的包登记进同一个仓库，两份都能查到。
        merged.Register(combatPacks.Find(combatPackId)!);
        Assert.IsTrue(merged.Find(combatPackId) is not null, "合并后仓库应含 combat 的包 '" + combatPackId + "'。");
        Assert.IsTrue(merged.Find(characterPackId) is not null, "合并后仓库应仍含角色的包 '" + characterPackId + "'。");
        // 同名重复登记 = 静默覆盖数值，必须报错。
        StatPackageLayer again = combatPacks.Find(combatPackId)!;
        Assert.ThrowsExactly<ArgumentException>(() => merged.Register(again));
    }

    // ─────────────────────────────────────────────────────────────
    // 10. 读路径零分配
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 热路径读取（索引 Get + 位矩阵 Has）在稳定状态下<u>不分配</u>：
    /// 这正是"表里的字符串在装配期全部翻成 int 槽位 / 下标"的意义所在。
    /// </summary>
    [TestMethod]
    public void HotPathReads_AllocateZeroBytes()
    {
        ContentSet set = BuildRealSet();
        int slot = set.Index.ObjectSlotOf("bullet_101");
        int tag = set.Rules.TagOf("tag.kinetic");
        int weaponSlot = set.Index.WeaponSlotOf("pistol_101");

        // 预热：JIT 与首次访问的分配（含字符串比较缓存）都在测量前完成。
        for (int i = 0; i < 100; i++)
        {
            Consume(set.Index.GetObject(slot));
            Consume(set.ObjectTags.Has(slot, tag));
            Consume(set.Index.GetWeapon(weaponSlot));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool sink = false;
        WeaponContract? weaponSink = null;
        for (int i = 0; i < 10000; i++)
        {
            sink ^= set.ObjectTags.Has(slot, tag);
            weaponSink = set.Index.GetWeapon(weaponSlot);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated, "热路径读取分配了 " + allocated.ToString(CultureInfo.InvariantCulture) + " 字节。");
        Assert.AreNotEqual(null, weaponSink, "循环不应被优化掉。");
        GC.KeepAlive(weaponSink);
        Assert.IsTrue(sink || !sink, "保持 sink 存活。");
    }

    // ─────────────────────────────────────────────────────────────
    // 11. 负向：Objects.Projectile 指向的物件必须真的被某行 Projectiles 引用
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Objects.Projectile</c> 指向一个<b>存在</b>但没有被任何 <c>Projectiles</c> 行引用的物件 →
    /// 必须抛中文异常，而不是静默挂上一个 <see cref="ProjectileContract.None"/>。
    /// <para>
    /// <b>为什么单独钉这一条</b>：表里 Objects 第 4 行备注把 <c>Projectile</c> 写成了「指向 Projectiles.Id」，
    /// 而校验器 <c>check_object_refs</c> 的权威口径是「指向 <c>Objects.Id</c> <u>且</u>该物件必须被某行
    /// Projectiles 引用」。实现里这是<u>两个</u>条件；将来若有人把它简化成"只查 Objects 存在性"，
    /// 这个用例会立刻失败 —— 否则表现是"枪配好了但打出去没有子弹行为"，零报错、极难查。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ObjectProjectilePointingAtObjectWithoutContract_ThrowsChineseException()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable weapons = ReadTable(reader, "Weapons");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        // 「存在但不是子弹」的物件：装备 / 怪物这类没有 Projectiles 行的物件。
        // club_401 是近战武器物件（不发射子弹），Objects 表里有它、Projectiles 表里没有它。
        Assert.IsTrue(HasRow(objects, "club_401"), "前提：Objects 表里应有 club_401。");
        Assert.IsFalse(HasProjectileRow(projectiles, "club_401"),
            "前提：club_401 不应被任何 Projectiles 行引用（否则本用例没有区分度）。");

        ContentTable broken = Mutate(objects, "pistol_101", "Projectile", "club_401");

        ContentException ex = Assert.ThrowsExactly<ContentException>(
            () => ContentBinder.BuildFrom(tags, broken, weapons, projectiles, packs, packages: null));

        // 报错必须说清"物件存在、但没有子弹契约"，而不是笼统的"引用不存在"。
        Assert.IsTrue(ex.Message.Contains("club_401", StringComparison.Ordinal),
            "报错应点名那个没有契约的物件，实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("Projectiles", StringComparison.Ordinal),
            "报错应点名 Projectiles 表（缺少契约的来源），实际：" + ex.Message);
        Assert.IsTrue(ex.Message.Contains("没有", StringComparison.Ordinal) && ex.Message.Contains("契约", StringComparison.Ordinal),
            "报错应说明'该物件没有子弹契约'（而不是只说找不到 id），实际：" + ex.Message);
    }

    /// <summary>
    /// <b>正向守卫</b>：真表里每个带 <c>tag.projectile</c> 的物件，都必须能在
    /// <see cref="ContentIndex"/> 里查到对应的 <see cref="ProjectileContract"/>。
    /// <para>
    /// <b>这条守的是"孤儿契约静默丢失"</b>：<see cref="ContentIndex.RegisterObject"/> 只会登记
    /// <u>物件身上挂的那份</u>契约，而 <c>Projectiles</c> 表里可能存在没有被任何物件引用的行。
    /// 早先正是这个机制让"表里 5 把枪、游戏里只认得 3 把"——不报错、只少东西，
    /// 是那种上线后才被玩家发现的 bug。绑定器现在会补登这些契约行，本用例钉住它别再退化。
    /// </para>
    /// <para>
    /// <b>为什么查槽位要用物件 id</b>：所有引用列（<c>Weapons.ProjectileRef</c> /
    /// <c>Objects.Projectile</c> / <c>OnExpireProjectile</c>）填的都是 <c>Objects.Id</c>
    /// （见 <c>validate_content_xlsx.py</c> 的 <c>check_object_refs</c>），
    /// 所以契约的对外主键也必须按物件 id 登记，否则运行时每次都查不到槽位。
    /// </para>
    /// </summary>
    [TestMethod]
    public void EveryProjectileTaggedObject_HasReachableProjectileContract()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable objects = ReadTable(reader, "Objects");
        ContentSet set = BuildRealSet();

        // 物件的标签是位矩阵，先确认 tag.projectile 在 Tags 表里存在。
        int projectileTag = set.Rules.TagOf("tag.projectile");
        Assert.IsTrue(projectileTag >= 0, "Tags 表里应有 tag.projectile。");

        int checkedCount = 0;
        for (int objectSlot = 0; objectSlot < set.Index.ObjectCount; objectSlot++)
        {
            if (!set.ObjectTags.Has(objectSlot, projectileTag))
            {
                continue;
            }

            ObjectArchetype archetype = set.Index.GetObject(objectSlot)!;
            Assert.IsTrue(set.Index.ProjectileSlotOf(archetype.Id) >= 0,
                "带 tag.projectile 的物件 '" + archetype.Id + "' 查不到子弹契约槽位 —— "
                + "契约主键与引用口径不一致（或该物件的契约被静默丢弃）。");
            Assert.IsNotNull(archetype.ProjectileContract,
                "物件 '" + archetype.Id + "' 应挂上已绑定的子弹契约。");
            checkedCount++;
        }

        Assert.IsTrue(checkedCount > 0, "真表里应至少有一个带 tag.projectile 的物件（否则本用例没有覆盖面）。");

        // 反向：每一条 Projectiles 行都必须能在索引里按物件 id 查到槽位（防契约行被丢）。
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        for (int i = 0; i < projectiles.Rows.Count; i++)
        {
            string objectId = projectiles.Rows[i].Get("ObjectId").Trim();
            if (objectId.Length == 0)
            {
                continue;
            }
            Assert.IsTrue(set.Index.ProjectileSlotOf(objectId) >= 0,
                "Projectiles 行引用的物件 '" + objectId + "' 应在索引里有子弹槽位。");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 12. 熔炉权重：Objects.ClusterWeight 列
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Objects.ClusterWeight</c> 列 → <see cref="ClusterCandidates.Weights"/>；
    /// 再用<b>固定随机源</b>（不是统计）验证权重大的候选确实更容易被抽中。
    /// <para>
    /// <b>为什么用固定 u 而不是跑一万次看频率</b>：统计断言会因为抽样噪声偶发翻红，而且失败时说不清错在哪。
    /// 固定 u ∈ [0,1) 后，"加权轮盘落在哪个区间"是可以手算的确定性结论 —— 权重变大 → 区间变宽 → 命中可预测。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ClusterWeightColumn_FeedsFurnaceCandidateWeights()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable objects = ReadTable(reader, "Objects");
        Assert.IsTrue(HasColumn(objects, "ClusterWeight"),
            "Objects 表应有 ClusterWeight 列（熔炉权重来源）。若 data-dev 改名，本用例需同步。");

        // ① 真实表：权重按行读进候选表，且与表里的值逐项一致。
        ContentSet real = ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"), objects, ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"), ReadTable(reader, "StatPacks"), packages: null);

        ClusterCandidates kinetic = real.CandidatesOf(real.ClusterSlotOf("proj_kinetic"))!;
        Assert.AreEqual(4, kinetic.Count, "proj_kinetic 簇应有 4 个候选。");
        AssertWeightMatchesTable(real, kinetic, objects, "bullet_101");
        AssertWeightMatchesTable(real, kinetic, objects, "bullet_102");
        AssertWeightMatchesTable(real, kinetic, objects, "arrow_202");

        // ② 权重不同的两个候选：固定 u 的加权轮盘判定，权重大的更容易被选中。
        //    用内存改表把 proj_kinetic 改成「A=9, 其余=1」，于是总权重 = 9+1+1+1 = 12，A 占 9/12 区间。
        ContentTable weighted = Mutate(objects, "bullet_101", "ClusterWeight", "9");
        weighted = Mutate(weighted, "bullet_102", "ClusterWeight", "1");
        weighted = Mutate(weighted, "bullet_103", "ClusterWeight", "1");
        weighted = Mutate(weighted, "arrow_202", "ClusterWeight", "1");

        ContentSet set = ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"), weighted, ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"), ReadTable(reader, "StatPacks"), packages: null);
        ClusterCandidates pool = set.CandidatesOf(set.ClusterSlotOf("proj_kinetic"))!;

        const float Total = 12f;
        const float HighShare = 9f / Total;

        // u 落在高权重候选区间内 → 选中 bullet_101（它的区间最宽，从 0 到 9/12）。
        Assert.IsTrue(WeightedPick(set, pool, 0f, "bullet_101"), "u=0 应命中区间起点所在的候选。");
        Assert.IsTrue(WeightedPick(set, pool, HighShare - 0.01f, "bullet_101"),
            "u 略小于 9/12 应命中高权重候选 bullet_101。");
        // u 越过高权重区间 → 落到低权重候选（证明"权重大的占了更宽的区间"）。
        Assert.IsTrue(WeightedPick(set, pool, HighShare + 0.01f, "bullet_102"),
            "u 略大于 9/12 应落到低权重候选 bullet_102。");

        // ③ 兼容路径：表里没有 ClusterWeight 列时，权重回落到 1（旧表仍能装配）。
        ContentTable legacy = RemoveColumn(objects, "ClusterWeight");
        ContentSet legacySet = ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"), legacy, ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"), ReadTable(reader, "StatPacks"), packages: null);
        ClusterCandidates legacyPool = legacySet.CandidatesOf(legacySet.ClusterSlotOf("proj_kinetic"))!;
        for (int i = 0; i < legacyPool.Count; i++)
        {
            Assert.AreEqual(1f, legacyPool.Weights[i], "缺列时同簇应等概率（权重回落 1）。");
        }
    }

    /// <summary>断言候选表里的权重与 Objects 表 <c>ClusterWeight</c> 列的值一致。</summary>
    private static void AssertWeightMatchesTable(
        ContentSet set, ClusterCandidates cluster, ContentTable objects, string objectId)
    {
        float expected = 0f;
        bool found = false;
        for (int i = 0; i < objects.Rows.Count; i++)
        {
            if (string.Equals(objects.Rows[i].Get("Id").Trim(), objectId, StringComparison.Ordinal))
            {
                string text = objects.Rows[i].Get("ClusterWeight").Trim();
                expected = text.Length == 0 ? 1f : float.Parse(text, CultureInfo.InvariantCulture);
                found = true;
                break;
            }
        }
        Assert.IsTrue(found, "Objects 表里应有 '" + objectId + "'。");
        Assert.AreEqual(expected, WeightOf(set, cluster, objectId),
            "'" + objectId + "' 的熔炉权重应与表里的 ClusterWeight 一致。");
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    /// <summary>去掉一列（模拟"旧表还没有这一列"，用于验证兼容回落路径）。</summary>
    private static ContentTable RemoveColumn(ContentTable table, string column)
    {
        var columns = new List<string>();
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (!string.Equals(table.Columns[i], column, StringComparison.Ordinal))
            {
                columns.Add(table.Columns[i]);
            }
        }
        return new ContentTable(table.Name, columns, table.Rows);
    }

    /// <summary>取某候选在候选表里的权重；找不到候选则断言失败。</summary>
    private static float WeightOf(ContentSet set, ClusterCandidates cluster, string objectId)
    {
        for (int i = 0; i < cluster.Count; i++)
        {
            if (set.Index.GetObject(cluster.ObjectSlots[i]).Id == objectId)
            {
                return cluster.Weights[i];
            }
        }
        Assert.Fail("候选里没有 '" + objectId + "'。");
        return 0f;
    }

    /// <summary>
    /// 加权轮盘抽取：按 <paramref name="u"/> ∈ [0,1) 在簇内选一个候选，
    /// 判断结果是不是 <paramref name="expectedId"/>。
    /// <para>实现与熔炉系统的口径一致：<c>u × Σweight</c> 落在前缀和区间即为命中。</para>
    /// </summary>
    private static bool WeightedPick(
        ContentSet set, ClusterCandidates cluster, float u, string expectedId)
    {
        float total = 0f;
        for (int i = 0; i < cluster.Count; i++)
        {
            total += cluster.Weights[i];
        }
        Assert.IsTrue(total > 0f, "簇内权重之和必须为正，否则无从抽取。");

        float target = u * total;
        float cumulative = 0f;
        int picked = -1;
        for (int i = 0; i < cluster.Count; i++)
        {
            cumulative += cluster.Weights[i];
            if (target < cumulative)
            {
                picked = i;
                break;
            }
        }
        Assert.IsTrue(picked >= 0, "u=" + u.ToString("0.###", CultureInfo.InvariantCulture) + " 应能选中一个候选。");
        return set.Index.GetObject(cluster.ObjectSlots[picked]).Id == expectedId;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Consume<T>(T value) => GC.KeepAlive(value);

    // ─────────────────────────────────────────────────────────────
    // 12. 外观列（VisualPack）：**唯一入口**、可选读、只影响外观
    //
    //     历史教训（2026-11-09 收敛）：同一概念曾经有三条并存的写入路径 ——
    //     `VisualPack`（走外观包）/ `AssetPath`（直连模型路径）/ 两者都不写。
    //     结果真表里必然长出「有的有、有的没有」，用户一眼就看出来：
    //     “为什么有的有、有的没有？当时没抽象吗？”
    //     抽象其实是有的，坏的是**没有唯一入口**。现只保留 `VisualPack` 一列：
    //       · 有模型 → 指向真外观包；
    //       · 有意没有模型（特效/纯逻辑物件）→ 指向 `vp_none`（包里 ModelPath 留空）；
    //       · 两列旁路已删除，所以「忘了填」不再有任何表达方式 —— 只能填，或者显式写 vp_none。
    //
    //     契约两条，缺一不可：
    //     ① 老表没有这一列时必须照样能装配（否则“加一个美术字段”就让所有旧表失效，
    //        违反规则7「新增功能不大改」）——这是可选列模式的核心；
    //     ② 有了这一列时，逻辑身份（IdHash / Capabilities）一个字节都不许变，
    //        否则“换一个模型”会连带改变子弹归属、配方命中与能力筛选。
    // ─────────────────────────────────────────────────────────────

    private static readonly string[] VisualColumns = { "VisualPack" };

    /// <summary>
    /// 把 <c>Objects</c> 的外观列整列删掉后照样能绑定：每个物件都装出来，
    /// 且 <see cref="ObjectArchetype.VisualPackId"/> 为空串（不是 null），外观回退白模。
    /// </summary>
    [TestMethod]
    public void Binder_TableWithoutVisualColumns_StillBindsWithEmptyVisualFields()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable weapons = ReadTable(reader, "Weapons");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        // 先确认真表里确实有这一列 —— 否则这个用例会变成"删了不存在的列还通过"的假绿。
        for (int i = 0; i < VisualColumns.Length; i++)
        {
            Assert.IsTrue(HasColumn(objects, VisualColumns[i]),
                "combat.xlsx/Objects 应当有可选列 '" + VisualColumns[i] + "'；真表没有它，本用例验证不到向下兼容。");
        }

        // 可选列被删掉后，视觉外键校验也必须跟着放过（旧表不认识 VisualPack，不能强求引用存在）。
        ContentSet stripped = ContentBinder.BuildFrom(tags, DropColumns(objects, VisualColumns),
            weapons, projectiles, packs, packages: null);

        Assert.AreEqual(objects.Rows.Count, stripped.Index.ObjectCount,
            "删掉外观列后物件数应不变——可选列不得影响绑定行数。");

        for (int i = 0; i < objects.Rows.Count; i++)
        {
            string id = objects.Rows[i].Get("Id").Trim();
            int slot = stripped.Index.ObjectSlotOf(id);
            Assert.IsTrue(slot >= 0, "物件 '" + id + "' 在缺列的旧表里找不到了。");
            ObjectArchetype def = stripped.Index.GetObject(slot);
            Assert.AreEqual(string.Empty, def.VisualPackId, "缺列时 '" + id + "' 的 VisualPackId 应为空串而非 null。");
        }
    }

    /// <summary>
    /// 真表里**每个物件都恰好填了一个外观包**，且该包在 <c>VisualPacks</c> 里真实存在。
    /// <para>
    /// <b>为什么必须钉住</b>：这条正是本轮要根治的缺陷。列存在不等于列被填 ——
    /// 旧写法下 `VisualPack` / `AssetPath` 二选一（甚至都不写）也能通过，于是表里
    /// 「有的有、有的没有」。现在只允许一种表达：填包，或者显式填 `vp_none`。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Binder_EveryObject_HasExactlyOneExistingVisualPack()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable visualPacks = ReadTable(reader, "VisualPacks");

        HashSet<string> knownPacks = new(StringComparer.Ordinal);
        for (int i = 0; i < visualPacks.Rows.Count; i++)
        {
            knownPacks.Add(visualPacks.Rows[i].Get("Id").Trim());
        }
        Assert.IsTrue(knownPacks.Count > 0, "combat.xlsx/VisualPacks 是空的，没有包可引用。");

        ContentSet set = ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"), objects,
            ReadTable(reader, "Weapons"), ReadTable(reader, "Projectiles"),
            ReadTable(reader, "StatPacks"), packages: null);

        for (int i = 0; i < objects.Rows.Count; i++)
        {
            string id = objects.Rows[i].Get("Id").Trim();
            ObjectArchetype def = set.Index.GetObject(set.Index.ObjectSlotOf(id));

            Assert.AreNotEqual(string.Empty, def.VisualPackId,
                "'" + id + "' 没有外观包。外观只走 VisualPack 这一条路："
                + "有模型的填真包，有意没有模型的填 vp_none —— 不许留空。");
            Assert.IsTrue(knownPacks.Contains(def.VisualPackId),
                "'" + id + "' 引用的外观包 '" + def.VisualPackId + "' 在 VisualPacks 表里不存在。");
        }
    }

    /// <summary>
    /// 外观列存在时，值被逐字读进 <see cref="ObjectArchetype"/>；改一格再绑，值跟着变（改表即生效）。
    /// </summary>
    [TestMethod]
    public void Binder_ReadsVisualColumnsIntoArchetype()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable objects = ReadTable(reader, "Objects");

        // 任取一行有值的即可 —— 真表现在每行都有值（上一个用例钉住），这里只是取个样本。
        string rowId = null!;
        string pack = null!;
        for (int i = 0; i < objects.Rows.Count; i++)
        {
            ContentRow row = objects.Rows[i];
            string candidate = row.Get("VisualPack").Trim();
            if (candidate.Length > 0)
            {
                rowId = row.Get("Id").Trim();
                pack = candidate;
                break;
            }
        }
        Assert.IsNotNull(rowId, "combat.xlsx/Objects 里应至少有一行填了 VisualPack，否则取不到值。");

        ContentSet set = ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"),
            objects,
            ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"),
            ReadTable(reader, "StatPacks"),
            packages: null);

        Assert.AreEqual(pack, set.Index.GetObject(set.Index.ObjectSlotOf(rowId)).VisualPackId,
            "VisualPack 列应原样读进 VisualPackId。");

        // 改一格 → 重新绑定 → 值跟着变（"策划改表即生效"的闭环）。
        const string newPack = "vp_不许存在的包";
        ContentSet mutated = ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"),
            Mutate(objects, rowId, "VisualPack", newPack),
            ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"),
            ReadTable(reader, "StatPacks"),
            packages: null);
        ObjectArchetype after = mutated.Index.GetObject(mutated.Index.ObjectSlotOf(rowId));
        Assert.AreEqual(newPack, after.VisualPackId, "改了 VisualPack 一格后重新绑定，值应随之改变。");
    }

    /// <summary>
    /// 外观<b>不参与逻辑身份</b>：同一行的外观列换成任意值，<c>IdHash</c> / <c>Capabilities</c>
    /// / 四个 <see cref="ContentRef"/> 一个都不变。
    /// <para>
    /// <b>为什么必须钉住</b>：<c>ContentRef.DefId</c> 存的就是目标物件的 <c>IdHash</c>。若外观字段
    /// 混进哈希，给子弹换一个模型就会让所有指向它的引用失效 —— 且失败方式是静默的（引用找不到目标）。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Binder_VisualColumns_DoNotAffectLogicIdentity()
    {
        XlsxReader reader = XlsxReader.Open(CombatPath);
        ContentTable tags = ReadTable(reader, "Tags");
        ContentTable objects = ReadTable(reader, "Objects");
        ContentTable weapons = ReadTable(reader, "Weapons");
        ContentTable projectiles = ReadTable(reader, "Projectiles");
        ContentTable packs = ReadTable(reader, "StatPacks");

        ContentSet plain = ContentBinder.BuildFrom(tags, objects, weapons, projectiles, packs, packages: null);

        // 整列删掉 vs 全行改写，两个方向都验一遍：外观怎么变都不该动逻辑。
        ContentSet noVisual = ContentBinder.BuildFrom(tags, DropColumns(objects, VisualColumns),
            weapons, projectiles, packs, packages: null);

        ContentTable rewritten = objects;
        for (int i = 0; i < objects.Rows.Count; i++)
        {
            string id = objects.Rows[i].Get("Id").Trim();
            rewritten = Mutate(rewritten, id, "VisualPack", "vp_" + i.ToString(CultureInfo.InvariantCulture));
        }
        ContentSet allRewritten = ContentBinder.BuildFrom(tags, rewritten, weapons, projectiles, packs, packages: null);

        Assert.AreEqual(objects.Rows.Count, plain.Index.ObjectCount, "基线物件数不对，后面的比较没有意义。");
        for (int i = 0; i < objects.Rows.Count; i++)
        {
            string id = objects.Rows[i].Get("Id").Trim();
            ObjectArchetype expected = plain.Index.GetObject(plain.Index.ObjectSlotOf(id));

            ObjectArchetype a = noVisual.Index.GetObject(noVisual.Index.ObjectSlotOf(id));
            ObjectArchetype b = allRewritten.Index.GetObject(allRewritten.Index.ObjectSlotOf(id));

            Assert.AreEqual(expected.IdHash, a.IdHash, "'" + id + "' 删掉外观列后 IdHash 变了。");
            Assert.AreEqual(expected.IdHash, b.IdHash, "'" + id + "' 改写外观列后 IdHash 变了。");
            Assert.AreEqual(expected.Capabilities, a.Capabilities, "'" + id + "' 删掉外观列后能力位变了。");
            Assert.AreEqual(expected.Capabilities, b.Capabilities, "'" + id + "' 改写外观列后能力位变了。");
            Assert.AreEqual(expected.Weapon, b.Weapon, "'" + id + "' 改写外观列后 Weapon 引用变了。");
            Assert.AreEqual(expected.Projectile, b.Projectile, "'" + id + "' 改写外观列后 Projectile 引用变了。");
            Assert.AreEqual(expected.Actor, b.Actor, "'" + id + "' 改写外观列后 Actor 引用变了。");
            Assert.AreEqual(expected.Effect, b.Effect, "'" + id + "' 改写外观列后 Effect 引用变了。");

            // 反向自证：改写确实生效了，否则"没变"可能只是没改到。
            Assert.AreNotEqual(expected.VisualPackId, b.VisualPackId,
                "'" + id + "' 的 VisualPackId 改写没有生效，用例假绿。");
        }
    }

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

    private static string PackIdOf(ContentRow row) => row.Get("PackId").Trim();

    /// <summary>表里是否有该 id 的行（按 Id 列）。</summary>
    private static bool HasRow(ContentTable table, string id)
    {
        for (int i = 0; i < table.Rows.Count; i++)
        {
            if (string.Equals(table.Rows[i].Get("Id").Trim(), id, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>表里是否有某列引用该物件 id（按 ObjectId 列）。</summary>
    private static bool HasProjectileRow(ContentTable table, string objectId)
    {
        for (int i = 0; i < table.Rows.Count; i++)
        {
            if (string.Equals(table.Rows[i].Get("ObjectId").Trim(), objectId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>表头里是否有该列。</summary>
    private static bool HasColumn(ContentTable table, string column)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i], column, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static int CountRowsForPackCol(ContentTable table) => table.Rows.Count;

    /// <summary>
    /// 把 xlsx 的一张 Sheet 转成 <see cref="ContentTable"/>：第 1 行是字段名，第 6 行起是数据。
    /// <para>跳过 <c>#data</c> 标记行与四行表头（docs/rules/04 §5.2），行号保留 Excel 真实行号。</para>
    /// </summary>
    private static ContentTable ReadTable(XlsxReader reader, string sheetName)
    {
        string[][] grid = reader.ReadSheet(sheetName);
        var columns = new List<string>();
        string[][] header = grid.Length > 0 ? grid : Array.Empty<string[]>();
        if (header.Length > 0)
        {
            for (int c = 0; c < header[0].Length; c++)
            {
                string name = header[0][c].Trim();
                if (name.Length > 0)
                {
                    columns.Add(name);
                }
            }
        }

        var rows = new List<ContentRow>();
        for (int r = DataStartRowIndex; r < grid.Length; r++)
        {
            string[] line = grid[r];
            bool any = false;
            for (int c = 0; c < line.Length; c++)
            {
                if (!string.IsNullOrWhiteSpace(line[c]))
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                continue;
            }

            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < columns.Count && c < line.Length; c++)
            {
                cells[columns[c]] = line[c] ?? string.Empty;
            }
            rows.Add(new ContentRow(sheetName, r + 1, cells));
        }
        return new ContentTable(sheetName, columns, rows);
    }

    /// <summary>
    /// 复制一张表并删掉若干列（可选的"模型属性"列的向下兼容用例靠它构造）。
    /// </summary>
    private static ContentTable DropColumns(ContentTable table, params string[] drop)
    {
        var keep = new List<string>();
        for (int c = 0; c < table.Columns.Count; c++)
        {
            string name = table.Columns[c];
            bool dropped = false;
            for (int d = 0; d < drop.Length; d++)
            {
                if (string.Equals(name, drop[d], StringComparison.Ordinal))
                {
                    dropped = true;
                    break;
                }
            }
            if (!dropped)
            {
                keep.Add(name);
            }
        }
        Assert.IsTrue(keep.Count < table.Columns.Count, "辅助方法 DropColumns 一列都没删掉，用例无意义。");

        var rows = new List<ContentRow>(table.Rows.Count);
        for (int i = 0; i < table.Rows.Count; i++)
        {
            ContentRow row = table.Rows[i];
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < keep.Count; c++)
            {
                cells[keep[c]] = row.Get(keep[c]);
            }
            rows.Add(new ContentRow(table.Name, row.RowNumber, cells));
        }
        return new ContentTable(table.Name, keep, rows);
    }

    /// <summary>
    /// 复制一张表并改一格：返回新表，原表不动（"改表后重新绑定"的闭环靠它构造）。
    /// </summary>
    private static ContentTable Mutate(ContentTable table, string rowId, string column, string newValue)
    {
        var rows = new List<ContentRow>(table.Rows.Count);
        bool touched = false;
        for (int i = 0; i < table.Rows.Count; i++)
        {
            ContentRow row = table.Rows[i];
            if (!touched && string.Equals(row.Get("Id").Trim(), rowId, StringComparison.Ordinal))
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

    /// <summary>数据行在 <see cref="XlsxReader.ReadSheet"/> 网格里的 0 起始下标（跳过 4 行表头 + #data）。</summary>
    private const int DataStartRowIndex = 5; // arch-ok: 四行表头 + #data 标记行，docs/rules/04 §5.2 表格式

    /// <summary>从测试输出目录向上找到含 <c>data/excel</c> 的仓库根。</summary>
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
