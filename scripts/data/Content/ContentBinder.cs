using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;

namespace Game.Data.Content;

/// <summary>
/// 一张内容表里的一行**原始单元格**（列名 → 原文）。
/// <para>
/// <b>为什么绑定器的输入是"行"而不是"xlsx 文件"</b>：
/// </para>
/// <list type="bullet">
/// <item>运行期代码不得读 <c>.xlsx</c>（ARCHITECTURE §4.8）。真正读表的是构建期生成器与测试里的
/// <c>XlsxReader</c>，它们把表变成 <see cref="ContentTable"/>，绑定器只认这一层；</item>
/// <item>于是「策划改表 → 运行时对象」这条链可以在<u>测试里用真实 xlsx</u> 走通，也可以在构建期用生成物走通，
/// 绑定逻辑只有一份（规则7：换一种数据来源 = 换一个"读表器"，不动绑定器）；</item>
/// <item>行里保留 <see cref="RowNumber"/>（Excel 真实行号）：任何解析错误都能直接报回单元格。</item>
/// </list>
/// <para>
/// 单元格一律是字符串：类型解释（float / bool / enum / 规则值）属于绑定器，不属于读表器 ——
/// 「读」与「解释」的边界清晰，换 csv / json 也不用改 <see cref="ContentBinder"/>。
/// </para>
/// </summary>
public sealed class ContentRow
{
    private readonly Dictionary<string, string> _cells;

    /// <summary>构造一行。</summary>
    /// <param name="table">表名（= Sheet 名，错误信息用）。</param>
    /// <param name="rowNumber">Excel 真实行号（1 起始，错误信息用）。</param>
    /// <param name="cells">列名 → 单元格原文。</param>
    public ContentRow(string table, int rowNumber, Dictionary<string, string> cells)
    {
        Table = table ?? string.Empty;
        RowNumber = rowNumber;
        _cells = cells ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>表名（Sheet 名）。</summary>
    public string Table { get; }

    /// <summary>Excel 真实行号（含四行表头偏移）。</summary>
    public int RowNumber { get; }

    /// <summary>取单元格原文；列不存在返回空串（缺列由绑定器统一按表头检查上报）。</summary>
    public string Get(string column) => _cells.TryGetValue(column, out string? text) ? text : string.Empty;

    /// <summary>是否存在该列。</summary>
    public bool Has(string column) => _cells.ContainsKey(column);

    /// <summary>"表名 第N行 列名" —— 错误信息前缀，直接把人送回单元格。</summary>
    public string WhereOf(string column) =>
        string.Concat(Table, " 第", RowNumber.ToString(CultureInfo.InvariantCulture), "行 ", column);

    /// <inheritdoc />
    public override string ToString() => string.Concat(Table, '#', RowNumber.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// 一张内容表的**内存形态**：表名 + 表头字段名 + 数据行。
/// <para>表头带进来是为了能报"表头缺列"（docs/rules/04 §5.2 四行表头规约），
/// 而不是把整张表的每一格都当成"值缺失"。</para>
/// </summary>
public sealed class ContentTable
{
    /// <summary>构造。</summary>
    public ContentTable(string name, IReadOnlyList<string> columns, IReadOnlyList<ContentRow> rows)
    {
        Name = name ?? string.Empty;
        Columns = columns ?? Array.Empty<string>();
        Rows = rows ?? Array.Empty<ContentRow>();
    }

    /// <summary>表名（= Sheet 名）。</summary>
    public string Name { get; }

    /// <summary>表头字段名（第 1 行）。</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>数据行（第 5 行 <c>#data</c> 标记之后）。</summary>
    public IReadOnlyList<ContentRow> Rows { get; }
}

/// <summary>
/// 一个「可替换簇」的**平铺候选表**：熔炉在同一簇内换枪时，热路径只做数组下标。
/// <para>
/// <b>为什么预先摊平</b>：熔炉每次开炉都要"在簇内按权重抽一个候选"。若候选集是
/// <c>Dictionary&lt;string, List&lt;对象&gt;&gt;</c>，每次开炉都要分配 + 哈希字符串；
/// 摊平后候选就是三条等长数组（槽位号 / 权重 / 静态可用数），开炉只做一次前缀和扫描。
/// </para>
/// </summary>
public sealed class ClusterCandidates
{
    /// <summary>构造。</summary>
    public ClusterCandidates(string cluster, int[] objectSlots, float[] weights, int[] viableCounts)
    {
        Cluster = cluster ?? string.Empty;
        ObjectSlots = objectSlots ?? Array.Empty<int>();
        Weights = weights ?? Array.Empty<float>();
        ViableCounts = viableCounts ?? Array.Empty<int>();
    }

    /// <summary>簇名（Excel <c>Objects.Cluster</c> 原文；<see cref="ObjectArchetype.ClusterSlot"/> 是它的稳定编号）。</summary>
    public string Cluster { get; }

    /// <summary>候选物件的槽位号（扁平，按表内行序稳定）。</summary>
    public int[] ObjectSlots { get; }

    /// <summary>候选权重（与 <see cref="ObjectSlots"/> 等长）。</summary>
    public float[] Weights { get; }

    /// <summary>每个候选的静态可用数（与 <see cref="ObjectSlots"/> 等长）。</summary>
    public int[] ViableCounts { get; }

    /// <summary>候选数（三条数组等长）。</summary>
    public int Count => ObjectSlots.Length;

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("Cluster(", Cluster, ", n=", Count.ToString(CultureInfo.InvariantCulture), ")");
}

/// <summary>
/// 内容装配器的产物：运行期真正被系统族读取的那一组对象。
/// <para>
/// 它把三张表编址后的 <see cref="ContentIndex"/>（热路径唯一入口）与几份"装配期算好、运行期只读"的
/// 辅助结构（标签矩阵、配方字、熔炉候选表）打成一包 —— 系统只拿这一个对象，
/// 不需要知道"表"长什么样（规则7：换数据来源不动系统）。
/// </para>
/// <para>
/// <b>为什么这里既放 <see cref="Index"/> 又放 <see cref="ObjectTags"/></b>：<see cref="ContentIndex"/>
/// 负责"槽位 → 契约"，位矩阵负责"这一行勾了哪些 tag"。两者都以物件槽位号为行号，
/// 于是"第 7 个物件的武器伤害加成"= <c>Index.GetObject(7)</c> + <c>ObjectTags.Mask(7, tag)</c>，
/// 全程数组下标、零字典、零字符串。
/// </para>
/// </summary>
public sealed class ContentSet
{
    private readonly Dictionary<string, int> _clusterSlotByName;
    private readonly ClusterCandidates?[] _clustersBySlot;

    /// <summary>构造（只由 <see cref="ContentBinder"/> 调用；外部自己拼容易漏掉某份辅助表）。</summary>
    public ContentSet(
        ContentRules rules,
        ContentIndex index,
        StatPackageRepository? statPacks,
        ObjectBitMatrix objectTags,
        ContentIndex? projectileObjects,
        IReadOnlyList<ClusterCandidates> clusters,
        Dictionary<string, int> clusterSlotByName,
        ClusterCandidates?[] clustersBySlot)
    {
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        Index = index ?? throw new ArgumentNullException(nameof(index));
        StatPacks = statPacks;
        ObjectTags = objectTags ?? throw new ArgumentNullException(nameof(objectTags));
        ProjectileObjects = projectileObjects;
        Clusters = clusters ?? Array.Empty<ClusterCandidates>();
        _clusterSlotByName = clusterSlotByName ?? new Dictionary<string, int>(StringComparer.Ordinal);
        _clustersBySlot = clustersBySlot ?? Array.Empty<ClusterCandidates?>();
    }

    /// <summary>位矩阵列头（tag 名 ↔ tag 下标）。</summary>
    public ContentRules Rules { get; }

    /// <summary>编址后的内容表：热路径查物件 / 武器 / 子弹的唯一入口。</summary>
    public ContentIndex Index { get; }

    /// <summary>本文件 StatPacks 表注册出来的数值包仓库（调用方负责与 characters.xlsx 的仓库合并）。</summary>
    public StatPackageRepository? StatPacks { get; }

    /// <summary>物件 × 标签位矩阵（行号 = <see cref="ContentIndex"/> 里的物件槽位号）。</summary>
    public ObjectBitMatrix ObjectTags { get; }

    /// <summary>子弹物件 id → 物件槽位号（熔炉/分裂按 <see cref="ContentRef"/> 找子弹那一行时用）。</summary>
    /// <remarks>
    /// 子弹表的 <c>ObjectId</c> 指向的是「Objects 里的子弹物件」，而不是子弹契约本身；
    /// 于是"这颗子弹那行的位矩阵"必须先经过这层翻译。翻译在装配期做完，运行期只查数组。
    /// </remarks>
    public ContentIndex? ProjectileObjects { get; }

    /// <summary>全部簇的候选表（按簇名首次出现顺序）。</summary>
    public IReadOnlyList<ClusterCandidates> Clusters { get; }

    /// <summary>按簇名取稳定簇编号（= <see cref="ObjectArchetype.ClusterSlot"/>）；未登记返回 <see cref="ObjectArchetype.NoCluster"/>。</summary>
    public int ClusterSlotOf(string cluster) =>
        !string.IsNullOrEmpty(cluster) && _clusterSlotByName.TryGetValue(cluster, out int slot)
            ? slot
            : ObjectArchetype.NoCluster;

    /// <summary>按簇编号取候选表（热路径：一次数组下标）；越界返回 <c>null</c>。</summary>
    public ClusterCandidates? CandidatesOf(int clusterSlot) =>
        clusterSlot >= 0 && clusterSlot < _clustersBySlot.Length ? _clustersBySlot[clusterSlot] : null;

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("ContentSet(objects=", Index.ObjectCount.ToString(CultureInfo.InvariantCulture),
            ", weapons=", Index.WeaponCount.ToString(CultureInfo.InvariantCulture),
            ", projectiles=", Index.ProjectileCount.ToString(CultureInfo.InvariantCulture),
            ", tags=", Rules.TagCount.ToString(CultureInfo.InvariantCulture), ")");
}

/// <summary>
/// <b>内容装配器</b>：把 <c>combat.xlsx</c> 的 Tags / Objects / Weapons / Projectiles / StatPacks
/// 绑定成运行期的 <see cref="ContentRules"/> + <see cref="ContentIndex"/> 一组对象。
/// <para>
/// <b>两阶段绑定（为什么必须分两步）</b>：
/// </para>
/// <list type="number">
/// <item><b>建索引</b>：先扫一遍全部行，把 Id → 槽位号确定下来（物件 / 武器 / 子弹各一套）。
/// 槽位号按<b>表内行序</b>发号，同一份表每次装配结果完全相同（存档与回放依赖这一点）；</item>
/// <item><b>逐行解析</b>：此时任何外键（武器→物件、物件→武器契约、子弹→物件）都已有确定的槽位号可查，
/// "引用了一个不存在的 id"在装配期就被抓住，而不是进游戏第一次开火时。</item>
/// </list>
/// <para>
/// <b>为什么外键要在校验器之外再查一次</b>：校验器（<c>tools/data_pipeline/validate_content_xlsx.py</c>）
/// 与绑定器是两份独立代码。校验器漏掉一种引用（新加一列）时，绑定器若不查就会把 <see cref="ContentRef.None"/>
/// 静静写进 Def，表现为"这把枪打不出子弹"这种零报错的线上 bug。宁可装配期抛 <see cref="ContentException"/>。
/// </para>
/// <para>
/// <b>热路径纪律</b>：装配期允许字符串解析与分配；产出的 Def 里所有数值都是 <see cref="ValueRef"/>
/// （定长值类型）、所有交叉引用都是 int 槽位 / 哈希，运行期读取只剩数组下标与一次 switch。
/// </para>
/// </summary>
public sealed class ContentBinder
{
    // ── 列清单：与 combat.xlsx 第 1 行并行维护（结构性标识，不是游戏数值）──────────

    /// <summary>Tags 表必需列。</summary>
    private static readonly string[] TagColumns = { "TagId", "TagIndex", "Category", "Group", "BitMask" };

    /// <summary>Objects 表必需列。</summary>
    private static readonly string[] ObjectColumns =
    {
        "Id", "DisplayKey", "Tags", "Cluster", "StatPacks", "Weapon", "Projectile", "Actor", "Effect",
        "RenderId", "Radius", "Height",
    };

    /// <summary>
    /// Objects 表的<b>可选</b>列 <c>ClusterWeight</c>（熔炉在簇内抽候选的权重）。
    /// <para>
    /// <b>为什么是可选而不是必需</b>：这一列是后加的。已有表（以及任何别人手里的一份旧表）没有它时，
    /// 绑定器必须仍然能跑 —— 缺列 = 同簇等概率（<see cref="ClusterWeightDefault"/>），
    /// 而不是让整张表因为"少一列"装配失败。加列则立即生效，属于纯数据驱动的增量。
    /// </para>
    /// </summary>
    private const string ClusterWeightColumn = "ClusterWeight";

    /// <summary>
    /// 缺省熔炉权重 1：一个候选与别人"一样常被抽到"。
    /// 这是<b>表格式缺省值</b>（docs/rules/04 §5.2「空单元格 = 取默认值」），不是玩法数值。
    /// </summary>
    private const float ClusterWeightDefault = 1f;

    /// <summary>Weapons 表必需列。</summary>
    private static readonly string[] WeaponColumns =
    {
        "Id", "ObjectId", "Trigger", "Delivery", "Aim", "FireInterval", "BaseDamage", "ProjectileSpeed", "Range",
        "SpreadDeg", "Count", "Pierce", "Bounce", "Knockback", "Recoil", "HitRadius", "Lifetime", "ChargeTime",
        "ProjectileRef", "ActorRef", "EffectRef", "DamageType", "TeamLayer", "InheritWeaponStats",
        "ChainDepthLimit", "ChainShrink", "Recipe",
    };

    /// <summary>Projectiles 表必需列。</summary>
    private static readonly string[] ProjectileColumns =
    {
        "Id", "ObjectId", "Motion", "HitPolicy", "OnExpire", "Speed", "Radius", "Lifetime", "Damage", "Pierce",
        "Bounce", "Gravity", "HomingTurn", "OnHitProjectile", "OnHitActor", "OnExpireProjectile", "OnExpireActor",
        "MaxChainDepth", "Recipe",
    };

    /// <summary>StatPacks 表必需列。</summary>
    private static readonly string[] StatPackColumns = { "PackId", "Layer", "StatId", "Value" };

    /// <summary>五层数值包在 <c>StatPacks</c> 列里的分隔符（<c>Base;Growth;Equipment;Buff;Temporary</c>）。</summary>
    private const char PackSeparator = ';';

    /// <summary>Tags / 引用列表列的分隔符。</summary>
    private const char ListSeparator = ',';

    /// <summary>Recipe 列的连接符（8 个 TagId 用 <c>.</c> 连接 = 一个 64 位配方字）。</summary>
    private const char RecipeSeparator = '.';

    /// <summary>
    /// 五层数值包的层数（Base/Growth/Equipment/Buff/Temporary）。
    /// 这是 <c>StatPacks</c> 列的**格式约定**（docs/rules/04 §5.4），不是游戏数值。
    /// </summary>
    private const int PackLayerCount = 5; // arch-ok: StatPacks 列格式固定 5 层，规则4 规定的表格式

    /// <summary>
    /// <c>StatPacks</c> 列里五段在 <see cref="StatLayer"/> 顺序下的位置。
    /// 数值等于层号（Base=0 … Temporary=4），命名后与枚举顺序的绑定关系显式可见。
    /// </summary>
    private const int BaseLayerIndex = 0;

    /// <summary>第 2 段 = Growth。</summary>
    private const int GrowthLayerIndex = 1;

    /// <summary>第 3 段 = Equipment。</summary>
    private const int EquipmentLayerIndex = 2; // arch-ok: StatPacks 列第 3 段位置（= StatLayer.Equipment 层号）

    /// <summary>第 4 段 = Buff。</summary>
    private const int BuffLayerIndex = 3; // arch-ok: StatPacks 列第 4 段位置（= StatLayer.Buff 层号）

    /// <summary>第 5 段 = Temporary。</summary>
    private const int TemporaryLayerIndex = 4; // arch-ok: StatPacks 列第 5 段位置（= StatLayer.Temporary 层号）

    /// <summary>配方字的位宽（Tags 表 <c>BitMask</c> 列取值域 0..63）。表列定义，不是游戏数值。</summary>
    private const int RecipeBits = 64; // arch-ok: 配方字位宽，表列定义

    private readonly ContentRules _rules;
    private readonly IStatPackageRepository? _packages;
    private readonly Dictionary<string, int> _tagIndexById;

    // tag 下标 → 配方位（-1 = 不进配方字）
    private readonly int[] _tagBitMask;

    // 槽位号（按表内行序稳定发号）
    private readonly Dictionary<string, int> _objectSlotById = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _weaponSlotById = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _projectileSlotById = new Dictionary<string, int>(StringComparer.Ordinal);

    // 槽位号 → 表内行（第一阶段结果；第二阶段按下标遍历，不再查字典）
    private readonly List<ContentRow> _objectRows = new List<ContentRow>();
    private readonly List<ContentRow> _weaponRows = new List<ContentRow>();
    private readonly List<ContentRow> _projectileRows = new List<ContentRow>();

    // 已绑定的契约定（物件要挂它们）
    private readonly Dictionary<string, WeaponContract> _weaponsByDefId = new Dictionary<string, WeaponContract>(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectileContract> _projectilesByDefId = new Dictionary<string, ProjectileContract>(StringComparer.Ordinal);

    // 物件槽位 → 熔炉候选权重（来自 Objects.ClusterWeight 列；列缺失时全部为 ClusterWeightDefault）
    private float[] _clusterWeightBySlot = Array.Empty<float>();

    // 物件 id → 它的子弹契约（由 Projectiles.ObjectId 反向索引而来；物件行上的 Projectile 列指向 Objects.Id）
    private readonly Dictionary<string, ProjectileContract> _projectileContractByObjectId = new Dictionary<string, ProjectileContract>(StringComparer.Ordinal);

    // 簇编号（按表中首次出现顺序稳定发号）
    private readonly Dictionary<string, int> _clusterSlotByName = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly List<string> _clusterNames = new List<string>();

    private StatPackageRepository? _packs;

    private ContentBinder(ContentRules rules, IStatPackageRepository? packages)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _packages = packages;
        _tagIndexById = new Dictionary<string, int>(_rules.TagCount, StringComparer.Ordinal);
        _tagBitMask = new int[_rules.TagCount];
        for (int i = 0; i < _rules.TagCount; i++)
        {
            string name = _rules.NameOf(i);
            if (name.Length > 0 && !_tagIndexById.ContainsKey(name))
            {
                _tagIndexById.Add(name, i);
            }
            _tagBitMask[i] = -1;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 工厂入口
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 由 <see cref="ContentRules"/> + 数值包仓库构造绑定器。
    /// <paramref name="packages"/> 用于校验 <c>Objects.StatPacks</c> 里写的包 id 真的存在
    /// （combat.xlsx 会引用 characters.xlsx 的包，跨表引用最容易拼错）。传 <c>null</c> 跳过该检查（纯绑定单测）。
    /// </summary>
    public static ContentBinder Create(ContentRules rules, IStatPackageRepository? packages) =>
        new ContentBinder(rules, packages);

    /// <summary>
    /// Tags 表 → <see cref="ContentRules"/>：tag 名按 <c>TagIndex</c> 排位。
    /// <para>
    /// <b>为什么按 TagIndex 而不是行序</b>：位矩阵的位号就是 tag 下标，表里允许为了可读性调整行序；
    /// TagIndex 是策划显式声明的稳定编号，按它排位才能保证"插一行 tag 不让所有位错位"。
    /// </para>
    /// <para>下标必须从 0 连续：位矩阵按连续下标分配位，空号会让每一位的含义整体漂移。</para>
    /// </summary>
    public static ContentRules LoadRules(ContentTable tags)
    {
        EnsureColumns(tags, TagColumns);

        var byIndex = new SortedDictionary<int, ContentRow>();
        var byId = new Dictionary<string, ContentRow>(StringComparer.Ordinal);
        for (int r = 0; r < tags.Rows.Count; r++)
        {
            ContentRow row = tags.Rows[r];
            string id = Required(row, "TagId");
            int index = ParseIntCell(row, "TagIndex");

            if (index < 0)
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("TagIndex"), "：tag 下标必须 >= 0，实际 ",
                    index.ToString(CultureInfo.InvariantCulture), "。"));
            }
            if (byIndex.TryGetValue(index, out ContentRow? clash))
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("TagIndex"), "：tag 下标 ", index.ToString(CultureInfo.InvariantCulture),
                    " 与第 ", clash.RowNumber.ToString(CultureInfo.InvariantCulture), " 行的 '", clash.Get("TagId"),
                    "' 重复（下标是位矩阵的位号，必须唯一）。"));
            }
            if (byId.TryGetValue(id, out ContentRow? sameId))
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("TagId"), "：TagId '", id, "' 与第 ",
                    sameId.RowNumber.ToString(CultureInfo.InvariantCulture), " 行重复。"));
            }
            byIndex.Add(index, row);
            byId.Add(id, row);
        }

        var names = new string[byIndex.Count];
        int expected = 0;
        foreach (KeyValuePair<int, ContentRow> pair in byIndex)
        {
            if (pair.Key != expected)
            {
                throw new ContentException(string.Concat(
                    tags.Name, " TagIndex：tag 下标必须从 0 连续递增，缺少 ",
                    expected.ToString(CultureInfo.InvariantCulture), "（下一行是 ",
                    pair.Key.ToString(CultureInfo.InvariantCulture), "，tag '", pair.Value.Get("TagId"), "'）。"));
            }
            names[expected] = pair.Value.Get("TagId").Trim();
            expected++;
        }
        return ContentRules.FromTags(names);
    }

    /// <summary>
    /// 一步到位：从五张表构建内容集。
    /// <para>顺序是"先 Tags → 再 StatPacks → 再三张内容表"，因为后者都依赖前两者的校验结果。</para>
    /// </summary>
    public static ContentSet BuildFrom(
        ContentTable tags,
        ContentTable objects,
        ContentTable weapons,
        ContentTable projectiles,
        ContentTable? statPacks = null,
        IStatPackageRepository? packages = null)
    {
        ContentRules rules = LoadRules(tags);
        var binder = new ContentBinder(rules, packages);
        binder.LoadTagMasks(tags);
        binder.LoadTagGroups(tags);
        StatPackageRepository? packs = statPacks is null ? null : binder.BindStatPacks(statPacks);
        return binder.BindAll(objects, weapons, projectiles, packs);
    }

    // ─────────────────────────────────────────────────────────────
    // Tags 表的 BitMask 列（配方字用）
    // ─────────────────────────────────────────────────────────────

    /// <summary>tag 名 → 配方位（-1 = 不进配方字）；未登记的 tag 返回 <see cref="int.MinValue"/>。</summary>
    public int BitMaskOf(string tagId) =>
        _tagIndexById.TryGetValue(tagId, out int index) ? _tagBitMask[index] : int.MinValue;

    /// <summary>
    /// 读 Tags 表的 <c>BitMask</c> 列：每个 TagId 在 64 位配方字里占一位（-1 = 不进配方字）。
    /// <para>
    /// <b>为什么配方字是 64 位而不是"一串 tag 名"</b>：熔炉要判断"两件东西能不能熔在一起"，
    /// 压成 <c>ulong</c> 后判定就是一次 <c>&amp;</c>，而字符串比较要哈希 + 分支。
    /// </para>
    /// </summary>
    public void LoadTagMasks(ContentTable tags)
    {
        EnsureColumns(tags, TagColumns);
        var used = new bool[RecipeBits];
        for (int r = 0; r < tags.Rows.Count; r++)
        {
            ContentRow row = tags.Rows[r];
            string id = Required(row, "TagId");
            int bit = ParseIntCell(row, "BitMask");
            int index = TagIndexOf(id, row, "TagId");

            if (bit == -1)
            {
                _tagBitMask[index] = -1;
                continue;
            }
            if (bit < 0 || bit >= RecipeBits)
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("BitMask"), "：配方位必须在 0..", (RecipeBits - 1).ToString(CultureInfo.InvariantCulture),
                    " 之间或 -1（不进配方），实际 ", bit.ToString(CultureInfo.InvariantCulture), "。"));
            }
            if (used[bit])
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("BitMask"), "：配方位 ", bit.ToString(CultureInfo.InvariantCulture),
                    " 已被占用（一个配方位只能属于一个 tag，否则熔炉匹配会误判）。"));
            }
            used[bit] = true;
            _tagBitMask[index] = bit;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 阶段一：建索引（Id → 槽位号）
    // ─────────────────────────────────────────────────────────────

    /// <summary>建立 Objects 表的 id 索引；重复 id 抛中文异常。</summary>
    public IReadOnlyDictionary<string, int> IndexObjects(ContentTable objects)
    {
        EnsureColumns(objects, ObjectColumns);
        _objectRows.Clear();
        _objectSlotById.Clear();
        for (int i = 0; i < objects.Rows.Count; i++)
        {
            ContentRow row = objects.Rows[i];
            string id = Required(row, "Id");
            if (_objectSlotById.TryGetValue(id, out int existing))
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("Id"), "：Objects 表 id '", id, "' 重复（第 ",
                    _objectRows[existing].RowNumber.ToString(CultureInfo.InvariantCulture), " 行已有同名物件）。"));
            }
            _objectSlotById.Add(id, i);
            _objectRows.Add(row);
        }
        return _objectSlotById;
    }

    /// <summary>建立 Weapons 表的 id 索引；重复 id 抛中文异常。</summary>
    public IReadOnlyDictionary<string, int> IndexWeapons(ContentTable weapons)
    {
        EnsureColumns(weapons, WeaponColumns);
        _weaponRows.Clear();
        _weaponSlotById.Clear();
        for (int i = 0; i < weapons.Rows.Count; i++)
        {
            ContentRow row = weapons.Rows[i];
            string id = Required(row, "Id");
            if (_weaponSlotById.TryGetValue(id, out int existing))
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("Id"), "：Weapons 表 id '", id, "' 重复（第 ",
                    _weaponRows[existing].RowNumber.ToString(CultureInfo.InvariantCulture), " 行已有同名武器）。"));
            }
            _weaponSlotById.Add(id, i);
            _weaponRows.Add(row);
        }
        return _weaponSlotById;
    }

    /// <summary>建立 Projectiles 表的 id 索引；重复 id 抛中文异常。</summary>
    public IReadOnlyDictionary<string, int> IndexProjectiles(ContentTable projectiles)
    {
        EnsureColumns(projectiles, ProjectileColumns);
        _projectileRows.Clear();
        _projectileSlotById.Clear();
        for (int i = 0; i < projectiles.Rows.Count; i++)
        {
            ContentRow row = projectiles.Rows[i];
            string id = Required(row, "Id");
            if (_projectileSlotById.TryGetValue(id, out int existing))
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("Id"), "：Projectiles 表 id '", id, "' 重复（第 ",
                    _projectileRows[existing].RowNumber.ToString(CultureInfo.InvariantCulture), " 行已有同名子弹）。"));
            }
            _projectileSlotById.Add(id, i);
            _projectileRows.Add(row);
        }
        return _projectileSlotById;
    }

    // ─────────────────────────────────────────────────────────────
    // StatPacks 表
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 绑定 StatPacks 表：同一个 <c>PackId</c> 的多行汇总成一层 <see cref="StatPackageLayer"/>。
    /// <para>
    /// <b>为什么这里产出一份新仓库而不是直接注册进传入的仓库</b>：combat.xlsx 与 characters.xlsx 各有一张
    /// StatPacks 表，两者必须合并到<b>同一个</b>仓库（角色引用 combat 的包、子弹引用角色的包）。
    /// 合并是调用方的决定，所以绑定器只产出一份"待合并"的仓库；<see cref="Create"/> 传入的仓库只用于
    /// "包名是否撞车"的检查 —— 同名不同内容静默覆盖会让"角色血量突然变了"变成无法追查的 bug。
    /// </para>
    /// </summary>
    public StatPackageRepository BindStatPacks(ContentTable statPacks)
    {
        EnsureColumns(statPacks, StatPackColumns);
        var registry = new StatPackageRepository();
        var order = new List<string>();
        var grouped = new Dictionary<string, List<KeyValuePair<StatId, float>>>(StringComparer.Ordinal);
        var layerOf = new Dictionary<string, StatLayer>(StringComparer.Ordinal);

        for (int i = 0; i < statPacks.Rows.Count; i++)
        {
            ContentRow row = statPacks.Rows[i];
            string packId = Required(row, "PackId");
            StatLayer layer = ParseEnumCell<StatLayer>(row, "Layer");
            StatId stat = ParseEnumCell<StatId>(row, "StatId");
            float value = ParseFloatCell(row, "Value");

            if (!grouped.TryGetValue(packId, out List<KeyValuePair<StatId, float>>? items))
            {
                items = new List<KeyValuePair<StatId, float>>();
                grouped.Add(packId, items);
                layerOf.Add(packId, layer);
                order.Add(packId);
            }
            else if (layerOf[packId] != layer)
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("Layer"), "：数值包 '", packId, "' 同时出现 ", layerOf[packId].ToString(), " 与 ",
                    layer.ToString(), " 两层（一个包只能属于一层）。"));
            }
            items.Add(new KeyValuePair<StatId, float>(stat, value));
        }

        for (int i = 0; i < order.Count; i++)
        {
            string packId = order[i];
            if (_packages is not null && _packages.TryGet(packId, out _))
            {
                throw new ContentException(string.Concat(
                    statPacks.Name, "：数值包 id '", packId,
                    "' 已存在于数值包仓库（characters.xlsx 与本表用了同一个包名，必须改名或合并）。"));
            }
            try
            {
                registry.Register(new StatPackageLayer(packId, layerOf[packId], grouped[packId]));
            }
            catch (ArgumentException ex)
            {
                throw new ContentException(string.Concat(
                    statPacks.Name, "：数值包 '", packId, "' 绑定失败 —— ", ex.Message), ex);
            }
        }

        _packs = registry;
        return registry;
    }

    // ─────────────────────────────────────────────────────────────
    // 阶段二：逐行绑定
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 绑定 Objects 表的一行。需要先建好三张表的索引（物件要挂已绑定的武器 / 子弹契约）。
    /// </summary>
    public ObjectArchetype BindObject(ContentRow row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }
        EnsureIndexed(_objectSlotById, "Objects");
        EnsureIndexed(_weaponSlotById, "Weapons");
        EnsureIndexed(_projectileSlotById, "Projectiles");

        string id = Required(row, "Id");
        string displayKey = row.Get("DisplayKey").Trim();
        int clusterSlot = ClusterSlotOf(row.Get("Cluster").Trim());

        // 能力位 = 「挂了哪几类契约」+ 「ObjectKind 标签」。具体 tag 名不出现（规则7）。
        ObjectCapability capability = ObjectCapability.None;
        ContentRef weaponRef = ResolveRef(row, "Weapon", RefDomain.Weapons);
        // ★ Projectile 列指向 **Objects.Id** 而不是 Projectiles.Id（见下方 ResolveRef 的注释与校验器
        //   check_object_refs 的权威口径）：物体原型是唯一真源，Projectiles 只是叠加在某个物体上的契约。
        ContentRef projectileRef = ResolveRef(row, "Projectile", RefDomain.Objects);
        ContentRef actorRef = ResolveRef(row, "Actor", RefDomain.Objects);
        ContentRef effectRef = ResolveRef(row, "Effect", RefDomain.Objects);

        WeaponContract? weapon = null;
        if (!weaponRef.IsEmpty)
        {
            weapon = _weaponsByDefId[weaponRef.Id];
            capability |= ObjectCapability.Weapon;
        }

        // 子弹契约不在物件行上直接给出，而是问"哪一行 Projectiles 的 ObjectId 指向我"。
        // 一个物件最多被一行 Projectiles 引用（否则同一个弹体有两套契约，飞出去该按谁的算？），
        // 所以这里预留一张 物件槽位 → 子弹契约 的旁表，在 BindAll 里先绑子弹再绑物件。
        //
        // ★ 两条来源都要认：
        //   ① Objects.Projectile 列显式填了引用 —— 要验它指向的物件真有契约（否则是策划填错）；
        //   ② 该物件本身**被某行 Projectiles 引用**（projectiles 的真正归属方向）。
        // 只认 ①会让"物件自己有 Projectiles 行、但 Objects.Projectile 列留空"的情况
        //  silently 丢掉契约 —— 正是带 tag.projectile 的物件却查不到 ProjectileContract 的成因。
        ProjectileContract? projectile = null;
        if (_projectileContractByObjectId.TryGetValue(id, out ProjectileContract? ownContract))
        {
            projectile = ownContract;
        }

        if (!projectileRef.IsEmpty)
        {
            if (_projectileContractByObjectId.TryGetValue(projectileRef.Id, out ProjectileContract? bound))
            {
                projectile = bound;
            }
            else
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("Projectile"), "：引用的物件 '", projectileRef.Id,
                    "' 没有被任何 Projectiles 行引用（Objects.", "Projectile → Objects.Id 存在，但它没有子弹契约）。"));
            }
        }

        if (projectile is not null)
        {
            capability |= ObjectCapability.Projectile;
        }
        if (!actorRef.IsEmpty)
        {
            capability |= ObjectCapability.Actor;
        }

        // ObjectKind 组 → 能力位：组名是 Tags 表的列定义（ObjectKind/WeaponKind/ProjectileKind），
        // 由 TableRules 记录；本文件只认"组"这个结构，不认具体 tag。
        capability |= CapabilitiesOf(row, "Tags");

        StatPackageIds packIds = ParseStatPacksColumn(row);

        return new ObjectArchetype(
            id: id,
            capabilities: capability,
            clusterSlot: clusterSlot,
            stats: packIds,
            renderId: row.Get("RenderId").Trim(),
            radius: ParseFloatCellOr(row, "Radius", 0f),
            height: ParseFloatCellOr(row, "Height", 0f),
            weapon: weaponRef,
            projectile: projectileRef,
            actor: actorRef,
            effect: effectRef,
            displayKey: displayKey,
            weaponContract: weapon,
            projectileContract: projectile);
    }

    /// <summary>绑定 Weapons 表的一行。外键指向 Objects 时必须已建索引。</summary>
    public WeaponContract BindWeapon(ContentRow row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }
        EnsureIndexed(_objectSlotById, "Objects");

        string id = Required(row, "Id");
        int slot = SlotIn(_weaponRows, row, "Weapons");
        int objectSlot = ObjectSlotOf(row, "ObjectId");

        WeaponTrigger trigger = ParseEnumCell<WeaponTrigger>(row, "Trigger");
        WeaponDelivery delivery = ParseEnumCell<WeaponDelivery>(row, "Delivery");
        WeaponAimKind aim = ParseEnumCell<WeaponAimKind>(row, "Aim");
        DamageType damageType = ParseEnumOrCell(row, "DamageType", DamageType.None);
        TeamId teamLayer = ParseEnumOrCell(row, "TeamLayer", TeamId.Neutral);
        bool inherit = ParseBoolCell(row, "InheritWeaponStats");
        int chainDepthLimit = ParseIntCellOr(row, "ChainDepthLimit", 0);
        float chainShrink = ParseFloatCellOr(row, "ChainShrink", 0f);

        return new WeaponContract(
            id: slot,
            defId: id,
            objectDefId: objectSlot,
            trigger: trigger,
            delivery: delivery,
            aim: aim,
            damageType: damageType,
            teamLayer: teamLayer,
            inheritWeaponStats: inherit,
            chainDepthLimit: chainDepthLimit,
            chainShrink: chainShrink,
            projectile: ResolveRef(row, "ProjectileRef", RefDomain.Objects),
            actor: ResolveRef(row, "ActorRef", RefDomain.Objects),
            effect: ResolveRef(row, "EffectRef", RefDomain.Objects),
            fireDelay: Rule(row, "FireInterval"),
            damage: Rule(row, "BaseDamage"),
            speed: Rule(row, "ProjectileSpeed"),
            range: Rule(row, "Range"),
            spreadDegrees: Rule(row, "SpreadDeg"),
            count: Rule(row, "Count"),
            hitRadius: Rule(row, "HitRadius"),
            lifetime: Rule(row, "Lifetime"),
            knockback: Rule(row, "Knockback"),
            pierce: Rule(row, "Pierce"),
            recoil: Rule(row, "Recoil"),
            chargeTime: Rule(row, "ChargeTime"));
    }

    /// <summary>绑定 Projectiles 表的一行。</summary>
    public ProjectileContract BindProjectile(ContentRow row)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }
        EnsureIndexed(_objectSlotById, "Objects");

        string id = Required(row, "Id");
        int slot = SlotIn(_projectileRows, row, "Projectiles");
        int objectSlot = ObjectSlotOf(row, "ObjectId");

        // DefId 用 ObjectId 而不是行 Id：所有引用列按物件 id 指向子弹，
        // 契约主键必须跟引用口径一致，否则 ContentIndex 查不到（详见 BindAll 里的说明）。
        string referencedId = Required(row, "ObjectId");

        return new ProjectileContract(
            id: slot,
            defId: referencedId,
            objectDefId: objectSlot,
            motion: ParseEnumCell<ProjectileMotion>(row, "Motion"),
            hitPolicy: ParseEnumCell<ProjectileHitPolicy>(row, "HitPolicy"),
            onExpire: ParseEnumCell<ProjectileDeath>(row, "OnExpire"),
            maxChainDepth: ParseIntCellOr(row, "MaxChainDepth", 0),
            onHitProjectile: ResolveRef(row, "OnHitProjectile", RefDomain.Objects),
            onHitActor: ResolveRef(row, "OnHitActor", RefDomain.Objects),
            onExpireProjectile: ResolveRef(row, "OnExpireProjectile", RefDomain.Objects),
            onExpireActor: ResolveRef(row, "OnExpireActor", RefDomain.Objects),
            speed: Rule(row, "Speed"),
            radius: Rule(row, "Radius"),
            lifetime: Rule(row, "Lifetime"),
            damage: Rule(row, "Damage"),
            pierce: Rule(row, "Pierce"),
            bounce: Rule(row, "Bounce"),
            gravity: Rule(row, "Gravity"),
            homingTurn: Rule(row, "HomingTurn"));
    }

    // ─────────────────────────────────────────────────────────────
    // 全量装配
    // ─────────────────────────────────────────────────────────────

    /// <summary>两阶段绑定全部三张表，返回内容集。</summary>
    public ContentSet BindAll(ContentTable objects, ContentTable weapons, ContentTable projectiles, StatPackageRepository? packs = null)
    {
        // 阶段一：建索引（后面所有外键都靠它）
        IndexObjects(objects);
        IndexWeapons(weapons);
        IndexProjectiles(projectiles);

        // 阶段二 A：先绑子表（物件要挂它们）。
        // 契约的槽位号按"子表行序"，与物件槽位号是两套编号，互不影响。
        _weaponsByDefId.Clear();
        _projectilesByDefId.Clear();
        for (int i = 0; i < _weaponRows.Count; i++)
        {
            WeaponContract weapon = BindWeapon(_weaponRows[i]);
            if (!_weaponsByDefId.TryAdd(weapon.DefId, weapon))
            {
                throw new ContentException(string.Concat("Weapons 表：武器 '", weapon.DefId, "' 绑定两次（索引与行不一致）。"));
            }
        }
        for (int i = 0; i < _projectileRows.Count; i++)
        {
            ProjectileContract projectile = BindProjectile(_projectileRows[i]);
            if (!_projectilesByDefId.TryAdd(projectile.DefId, projectile))
            {
                throw new ContentException(string.Concat("Projectiles 表：子弹 '", projectile.DefId, "' 绑定两次（索引与行不一致）。"));
            }
        }

        // 子弹契约的**主键口径**必须与所有引用列一致，否则运行时按引用查不到槽位：
        //   · Projectiles 表的 Id 列是 `proj_bullet_201`（行主键）；
        //   · 但 Weapons.ProjectileRef / Objects.Projectile / OnExpireProjectile / OnHitProjectile
        //     填的都是**物件 id** `bullet_201`（见 validate_content_xlsx.py 的 check_object_refs）。
        // 若 DefId 用行主键，ContentIndex.ProjectileSlotOf("bullet_201") 恒返回 NoneSlot，
        // 于是每一把射击武器在运行时都"射不出东西"——而且不报错，只是静默拒绝（本循环抓到的真实缺陷）。
        // 故这里以**物件 id** 作为契约的 DefId，行主键 `proj_bullet_201` 只留在表校验与调试信息里。

        // 索引里的子弹**别名**：Projectiles 表的 Id 列是 `proj_bullet_201`，而所有引用列
        // （Weapons.ProjectileRef / Objects.Projectile / OnExpireProjectile / OnHitProjectile）
        // 填的都是**物件 id** `bullet_201`。不登记别名的话，ContentIndex.ProjectileSlotOf("bullet_201")
        // 会返回 NoneSlot，于是每一把射击武器在运行时都"射不出东西"——而且不报错，只是静默拒绝。
        //
        // 两套 id 都登记，指向同一份契约对象（同一个槽位号）：
        //   · `proj_bullet_201` —— Projectiles 表自己的主键（熔炉 / 编辑期按行引用时用）；
        //   · `bullet_201`      —— 物件 id（运行时所有内容引用走这条）。
        // 注意：别名只加在**索引**里，不改契约的 DefId —— DefId 仍是行主键，
        // 表校验的错误信息要按行主键报，才和策划看到的那一行对得上。

        // 反查表：物件 id → 它的子弹契约（一个物件最多一个，重复 = 契约来源不唯一，装配期就报）。
        _projectileContractByObjectId.Clear();
        for (int i = 0; i < _projectileRows.Count; i++)
        {
            ContentRow row = _projectileRows[i];
            string objectId = Required(row, "ObjectId");
            ProjectileContract contract = _projectilesByDefId[Required(row, "ObjectId")];
            if (!_projectileContractByObjectId.TryAdd(objectId, contract))
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("ObjectId"), "：物件 '", objectId, "' 已被另一行 Projectiles 引用（一个物件只能有一套子弹契约）。"));
            }
        }

        // 阶段二 B：物件按行号顺序绑定；行号 = 槽位号，位矩阵逐行填。
        var archetypes = new List<ObjectArchetype>(_objectRows.Count);
        var tagBuilder = new ObjectBitMatrix(_objectRows.Count, _rules.TagCount);
        _clusterWeightBySlot = new float[_objectRows.Count];
        bool hasWeightColumn = HasColumn(objects, ClusterWeightColumn);
        for (int i = 0; i < _objectRows.Count; i++)
        {
            ContentRow row = _objectRows[i];
            ObjectArchetype archetype = BindObject(row);
            archetypes.Add(archetype);

            // 熔炉权重：有列就按列读（空格子 = 缺省 1），没列就恒为缺省 1 —— 两条路径最终都写满整个数组，
            // 于是 BuildClusters 不必再判空（热路径少一个分支）。
            _clusterWeightBySlot[i] = hasWeightColumn
                ? ParseFloatCellOr(row, ClusterWeightColumn, ClusterWeightDefault)
                : ClusterWeightDefault;

            foreach (string tagId in SplitList(row.Get("Tags")))
            {
                tagBuilder.Set(i, TagIndexOf(tagId, row, "Tags"));
            }
        }
        ObjectBitMatrix objectTags = tagBuilder.Build();

        var index = new ContentIndex { Name = objects.Name };
        var projectileObjects = new ContentIndex { Name = string.Concat(objects.Name, ".projectiles") };
        for (int i = 0; i < archetypes.Count; i++)
        {
            ObjectArchetype archetype = archetypes[i];
            index.RegisterObject(archetype);
            if (archetype.HasCapability(ObjectCapability.Projectile))
            {
                projectileObjects.RegisterObject(archetype);
            }
        }

        // 契约必须单独登记一遍：ContentIndex.RegisterObject 只登记「物件身上挂的那份」，
        // 而 Weapons / Projectiles 表里存在**没有被任何物件引用**的契约行（例如近战大棒不发射子弹，
        // 但它仍是一把可被配装 / 熔炉使用的武器）。只靠物件登记会让这类契约从索引里消失，
        // 于是「表里 5 把枪，游戏里只认得 3 把」—— 这正是本循环要防的静默丢数据。
        // 已被物件登记的契约跳过（RegisterWeapon 对重复 id 会抛异常，那是给真正的表重名用的）。
        for (int i = 0; i < _weaponRows.Count; i++)
        {
            WeaponContract weapon = _weaponsByDefId[Required(_weaponRows[i], "Id")];
            if (index.WeaponSlotOf(weapon.DefId) == ContentIndex.NoneSlot)
            {
                index.RegisterWeapon(weapon);
            }
        }
        for (int i = 0; i < _projectileRows.Count; i++)
        {
            ProjectileContract projectile = _projectilesByDefId[Required(_projectileRows[i], "ObjectId")];
            if (index.ProjectileSlotOf(projectile.DefId) == ContentIndex.NoneSlot)
            {
                index.RegisterProjectile(projectile);
            }
        }

        (List<ClusterCandidates> clusters, ClusterCandidates?[] bySlot) = BuildClusters(archetypes, index);

        return new ContentSet(_rules, index, packs, objectTags, projectileObjects, clusters, _clusterSlotByName, bySlot);
    }

    // ─────────────────────────────────────────────────────────────
    // 熔炉候选表（按 Cluster 分组 + 扁平数组）
    // ─────────────────────────────────────────────────────────────

    private (List<ClusterCandidates> Clusters, ClusterCandidates?[] BySlot) BuildClusters(
        IReadOnlyList<ObjectArchetype> archetypes, ContentIndex index)
    {
        var members = new List<List<int>>(_clusterNames.Count);
        for (int i = 0; i < _clusterNames.Count; i++)
        {
            members.Add(new List<int>());
        }

        for (int i = 0; i < archetypes.Count; i++)
        {
            int clusterSlot = archetypes[i].ClusterSlot;
            if (clusterSlot >= 0 && clusterSlot < members.Count)
            {
                members[clusterSlot].Add(index.ObjectSlotOf(archetypes[i].Id));
            }
        }

        var flat = new List<ClusterCandidates>(_clusterNames.Count);
        var bySlot = new ClusterCandidates?[_clusterNames.Count];
        for (int i = 0; i < _clusterNames.Count; i++)
        {
            List<int> list = members[i];
            var slots = new int[list.Count];
            var weights = new float[list.Count];
            var viable = new int[list.Count];
            for (int c = 0; c < list.Count; c++)
            {
                slots[c] = list[c];
                // 权重来自 Objects.ClusterWeight 列（按物件槽位查回）。列缺失时该槽位记的是
                // ClusterWeightDefault，于是旧表仍然可用 ——「同簇等概率」是最不会出错的缺省。
                weights[c] = ClusterWeightOf(list[c]);
                // 静态可用数 = 同簇成员数。运行时若某候选被锁定，由熔炉系统在它自己的 int[] 上减，
                // 不改这份只读表（表是装配期产物，运行期可变状态不进表）。
                viable[c] = list.Count;
            }
            var candidates = new ClusterCandidates(_clusterNames[i], slots, weights, viable);
            flat.Add(candidates);
            bySlot[i] = candidates;
        }
        return (flat, bySlot);
    }

    /// <summary>按物件槽位取熔炉权重；未记录（列缺失）返回 <see cref="ClusterWeightDefault"/>。</summary>
    private float ClusterWeightOf(int objectSlot) =>
        objectSlot >= 0 && objectSlot < _clusterWeightBySlot.Length
            ? _clusterWeightBySlot[objectSlot]
            : ClusterWeightDefault;

    // ─────────────────────────────────────────────────────────────
    // 能力位：标签组 → ObjectCapability
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// tag 下标 → 能力位（由 Tags 表的 <c>Group</c> 列推出；<see cref="ObjectCapability.None"/> = 该 tag 不贡献能力）。
    /// <para>
    /// <b>为什么映射从表里来、而不是在代码里 switch tag 名</b>：规则7 —— 新增一种物件族（比如"陷阱"）应当只在
    /// Excel 加一行 <c>tag.trap / ObjectKind</c>，不要在 C# 里再加一个 <c>case "tag.trap"</c>。
    /// 代码只认 <c>Group</c> 这个**列**（结构），不认具体 tag（数据）。
    /// </para>
    /// <para>
    /// <b>为什么 ObjectKind 之外还有 WeaponKind / ProjectileKind</b>：物件可能带 <c>tag.pistol</c> 这类细分标签，
    /// 它们只描述"哪一类"，不该额外贡献能力位 —— 能力只由 <c>tag.weapon</c> 这一个 ObjectKind 标签决定。
    /// 于是"一把枪"= Weapon 位，"一支箭"= Projectile 位，与细分标签正交。
    /// </para>
    /// </summary>
    private ObjectCapability[] _tagCapability = Array.Empty<ObjectCapability>();

    /// <summary>Tags 表的 <c>Group</c> 列名（结构约定，docs/rules/04 §5.2）。</summary>
    private const string GroupColumn = "Group";

    /// <summary>
    /// <c>ObjectKind</c> 组名：只有这一组里的 tag 贡献 <see cref="ObjectCapability"/> 位。
    /// 组名本身是 Tags 表的枚举值（表头部约定），不是游戏数值。
    /// </summary>
    private const string ObjectKindGroup = "ObjectKind";

    /// <summary>
    /// 读 Tags 表的 <c>Group</c> 列，建立"tag 下标 → 能力位"映射。
    /// 必需在任何 <see cref="BindObject"/> 之前调用（<see cref="LoadRules"/> 只管 tag 名与位序）。
    /// </summary>
    public void LoadTagGroups(ContentTable tags)
    {
        EnsureColumns(tags, TagColumns);
        _tagCapability = new ObjectCapability[_rules.TagCount];

        for (int r = 0; r < tags.Rows.Count; r++)
        {
            ContentRow row = tags.Rows[r];
            int index = TagIndexOf(Required(row, "TagId"), row, "TagId");
            string group = row.Get(GroupColumn).Trim();

            // 组名 → 能力位：组名必须是 ObjectCapability 的成员名（Weapon/Projectile/Actor/Equippable/Possessable），
            // 或者是不贡献能力的普通分组（Element / Ability / ObjectKind 之外的 WeaponKind ...）。
            if (string.Equals(group, ObjectKindGroup, StringComparison.Ordinal))
            {
                // ObjectKind 组的 tag 贡献能力：tag 名去掉 "tag." 前缀后与能力位同名，靠 Enum.TryParse 翻译。
                // 这也是为什么代码里不出现 "tag.weapon" 这种字面量 —— 前缀是表的命名规约，不是协议。
                _tagCapability[index] = CapabilityFromTagId(row, Required(row, "TagId"));
            }
            else
            {
                _tagCapability[index] = CapabilityFromGroupName(group);
            }
        }
    }

    /// <summary>把 <c>tag.weapon</c> 这类 id 翻成能力位：取最后一段与 <see cref="ObjectCapability"/> 成员名比对。</summary>
    private static ObjectCapability CapabilityFromTagId(ContentRow row, string tagId)
    {
        int dot = tagId.LastIndexOf('.');
        string leaf = dot >= 0 && dot + 1 < tagId.Length ? tagId.Substring(dot + 1) : tagId;
        if (Enum.TryParse(leaf, ignoreCase: true, out ObjectCapability capability) && capability != ObjectCapability.None)
        {
            return capability;
        }
        throw new ContentException(string.Concat(
            row.WhereOf("TagId"), "：tag '", tagId, "' 属于 ", ObjectKindGroup, " 组，但去掉前缀后 '", leaf,
            "' 不是 ObjectCapability 的成员（可用：", string.Join("/", Enum.GetNames(typeof(ObjectCapability))),
            "）。", ObjectKindGroup, " 组的 tag 决定物件能力位，命名必须与能力位同名。"));
    }

    /// <summary>非 ObjectKind 组：组名若恰好是能力位名则也认（允许策划把装备/可持有单独成组），否则不贡献能力。</summary>
    private static ObjectCapability CapabilityFromGroupName(string group)
    {
        if (group.Length > 0 && Enum.TryParse(group, ignoreCase: true, out ObjectCapability capability)
            && Enum.IsDefined(typeof(ObjectCapability), capability))
        {
            return capability;
        }
        return ObjectCapability.None;
    }

    /// <summary>把一行 <c>Tags</c> 列里"属于 ObjectKind 组"的 tag 翻成能力位（不贡献能力的 tag 直接跳过）。</summary>
    private ObjectCapability CapabilitiesOf(ContentRow row, string column)
    {
        if (_tagCapability.Length == 0)
        {
            throw new InvalidOperationException(string.Concat(
                "ContentBinder.LoadTagGroups 尚未调用：物件能力位需要 Tags 表的 ", GroupColumn,
                " 列（必须先 LoadRules 再 LoadTagGroups 再 BindAll）。"));
        }

        ObjectCapability result = ObjectCapability.None;
        foreach (string tagId in SplitList(row.Get(column)))
        {
            result |= _tagCapability[TagIndexOf(tagId, row, column)];
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────
    // 单元格解释
    // ─────────────────────────────────────────────────────────────

    private static void EnsureIndexed(Dictionary<string, int> index, string table)
    {
        if (index.Count == 0)
        {
            throw new InvalidOperationException(string.Concat(
                "ContentBinder 还没建立 ", table, " 表的索引：必须先调用 IndexObjects / IndexWeapons / IndexProjectiles 再逐行绑定。"));
        }
    }

    private static void EnsureColumns(ContentTable table, string[] required)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        for (int i = 0; i < required.Length; i++)
        {
            bool found = false;
            for (int c = 0; c < table.Columns.Count; c++)
            {
                if (string.Equals(table.Columns[c], required[i], StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                throw new ContentException(string.Concat(
                    table.Name, "：表头缺少列 '", required[i], "'（docs/rules/04 §5.2 四行表头，第 1 行必须是字段名）。"));
            }
        }
    }

    /// <summary>表头里是否有该列（可选列的兼容判据：有列才读，没列走缺省值）。</summary>
    private static bool HasColumn(ContentTable table, string column)
    {
        for (int c = 0; c < table.Columns.Count; c++)
        {
            if (string.Equals(table.Columns[c], column, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string Required(ContentRow row, string column)
    {
        string text = row.Get(column).Trim();
        if (text.Length == 0)
        {
            throw new ContentException(string.Concat(row.WhereOf(column), "：必填字段为空。"));
        }
        return text;
    }

    private static int ParseIntCell(ContentRow row, string column)
    {
        string text = row.Get(column).Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new ContentException(string.Concat(row.WhereOf(column), " 列 '", text, "'：无法解析为整数。"));
        }
        return value;
    }

    private static int ParseIntCellOr(ContentRow row, string column, int fallback)
    {
        string text = row.Get(column).Trim();
        return text.Length == 0 ? fallback : ParseIntCell(row, column);
    }

    private static float ParseFloatCell(ContentRow row, string column)
    {
        string text = row.Get(column).Trim();
        if (!TryParseFloat(text, out float value))
        {
            throw new ContentException(string.Concat(row.WhereOf(column), " 列 '", text, "'：无法解析为数字。"));
        }
        return value;
    }

    private static float ParseFloatCellOr(ContentRow row, string column, float fallback)
    {
        string text = row.Get(column).Trim();
        return text.Length == 0 ? fallback : ParseFloatCell(row, column);
    }

    private static bool TryParseFloat(string text, out float value)
    {
        if (text.Length == 0)
        {
            value = 0f;
            return false;
        }
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        // 中文区域会把 1,25 存成文本；宽容读取，避免策划被小数点坑（规则4 §5.5 要求写 . ，但不该因此卡住构建）。
        return text.IndexOf('.') < 0
            && float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool ParseBoolCell(ContentRow row, string column)
    {
        string text = row.Get(column).Trim();
        if (text.Length == 0)
        {
            return false;
        }
        if (bool.TryParse(text, out bool value))
        {
            return value;
        }
        if (text == "1" || string.Equals(text, "是", StringComparison.Ordinal))
        {
            return true;
        }
        if (text == "0" || string.Equals(text, "否", StringComparison.Ordinal))
        {
            return false;
        }
        throw new ContentException(string.Concat(row.WhereOf(column), " 列 '", text, "'：无法解析为布尔（TRUE/FALSE/1/0/是/否）。"));
    }

    private static TEnum ParseEnumCell<TEnum>(ContentRow row, string column)
        where TEnum : struct, Enum
    {
        string text = Required(row, column);
        if (!Enum.TryParse(text, ignoreCase: true, out TEnum value) || !Enum.IsDefined(typeof(TEnum), value))
        {
            throw new ContentException(string.Concat(
                row.WhereOf(column), " 列 '", text, "'：不是 ", typeof(TEnum).Name, " 的成员（可用：",
                string.Join("/", Enum.GetNames(typeof(TEnum))), "）。"));
        }
        return value;
    }

    private static TEnum ParseEnumOrCell<TEnum>(ContentRow row, string column, TEnum fallback)
        where TEnum : struct, Enum
    {
        string text = row.Get(column).Trim();
        return text.Length == 0 ? fallback : ParseEnumCell<TEnum>(row, column);
    }

    /// <summary>
    /// 读一格规则值。空单元格 = 空 <see cref="ValueSet"/>（求出 0），不报错：
    /// 物件表里大量列本来就留空（如特效物件的 StatPacks），把"留空"判成错误会让装配无法进行。
    /// 但**武器 / 子弹的数值列**留空会被校验器挡下，所以这里不必重复报。
    /// </summary>
    private ValueSet Rule(ContentRow row, string column)
    {
        string text = row.Get(column).Trim();
        if (text.Length == 0)
        {
            return ValueSet.Empty;
        }
        ValueRef value = ValueRefParser.Parse(text, _rules, row.WhereOf(column));
        return ValueSet.From(value);
    }

    private StatPackageIds ParseStatPacksColumn(ContentRow row)
    {
        string text = row.Get("StatPacks").Trim();
        if (text.Length == 0)
        {
            return default;
        }

        string[] parts = text.Split(PackSeparator);
        if (parts.Length != PackLayerCount)
        {
            throw new ContentException(string.Concat(
                row.WhereOf("StatPacks"), "：五层格式 Base;Growth;Equipment;Buff;Temporary 必须是 ",
                PackLayerCount.ToString(CultureInfo.InvariantCulture), " 段，实际 ",
                parts.Length.ToString(CultureInfo.InvariantCulture), " 段（'", text, "'）。"));
        }

        // 逐段取五层：用语义命名的常量而不是 0..4 魔法下标 —— 下标本身就是 StatLayer 的层号，
        // 写成名字后与 StatLayer.Base/Growth/... 的对应关系一眼可查（顺序写反 = 角色的成长值变成装备值）。
        var ids = new StatPackageIds(
            parts[BaseLayerIndex].Trim(),
            parts[GrowthLayerIndex].Trim(),
            parts[EquipmentLayerIndex].Trim(),
            parts[BuffLayerIndex].Trim(),
            parts[TemporaryLayerIndex].Trim());

        if (_packages is not null)
        {
            for (int layer = 0; layer < PackLayerCount; layer++)
            {
                string packId = ids.IdOf((StatLayer)layer);
                if (packId.Length > 0 && !_packages.TryGet(packId, out _))
                {
                    throw new ContentException(string.Concat(
                        row.WhereOf("StatPacks"), "：第 ", (layer + 1).ToString(CultureInfo.InvariantCulture),
                        " 段引用的数值包 '", packId,
                        "' 在数值包仓库里不存在（characters.xlsx 与 combat.xlsx 的 StatPacks 表都没有它）。"));
                }
            }
        }

        return ids;
    }

    // ─────────────────────────────────────────────────────────────
    // 外键解析
    // ─────────────────────────────────────────────────────────────

    private enum RefDomain
    {
        Objects,
        Weapons,
        Projectiles,
    }

    private int ObjectSlotOf(ContentRow row, string column)
    {
        string text = Required(row, column);
        if (!_objectSlotById.TryGetValue(text, out int slot))
        {
            throw MissingForeignKey(row, column, text, "Objects", "Id");
        }
        return slot;
    }

    /// <summary>
    /// 解析引用列：目标必须是对应表里真实存在的 id，否则抛"两表两 id"都点名的中文异常。
    /// <para>
    /// <b>Objects 表的四个引用列指向哪里（以校验器 <c>check_object_refs</c> 为权威口径）</b>：
    /// </para>
    /// <list type="bullet">
    /// <item><c>Weapon</c> → <c>Weapons.Id</c>（武器是一条独立的契约行）；</item>
    /// <item><c>Projectile</c> / <c>Actor</c> / <c>Effect</c> → <b><c>Objects.Id</c></b>，<u>不是</u> <c>Projectiles.Id</c>。
    /// 理由：物体原型是唯一真源，Projectiles 只是叠加在某个物体上的一层契约；填 <c>bullet_201</c>
    /// 指的是"那个子弹物体"，它真正的那套契约由 Projectiles 表里 <c>ObjectId = bullet_201</c> 的那一行提供。</item>
    /// </list>
    /// <para>
    /// <b>一处要留意的表内矛盾</b>：Objects 表第 4 行（备注区）把 <c>Projectile</c> 写成"指向 Projectiles.Id"，
    /// 与校验器 <c>check_object_refs</c> 的说明（以及真实数据里填的 <c>bullet_201</c> 这类物件 id）不一致。
    /// 以校验器 + 真实数据为准；同时在本类里补上备注漏掉的第二半条件 ——
    /// 被指向的物件若没有任何 Projectiles 行引用它，就等于"填了也拿不到契约"，属表写错，装配期直接报。
    /// </para>
    /// </summary>
    private ContentRef ResolveRef(ContentRow row, string column, RefDomain domain)
    {
        string text = row.Get(column).Trim();
        if (text.Length == 0)
        {
            return ContentRef.Empty;
        }

        bool found;
        switch (domain)
        {
            case RefDomain.Objects:
                found = _objectSlotById.ContainsKey(text);
                break;
            case RefDomain.Weapons:
                found = _weaponSlotById.ContainsKey(text);
                break;
            default:
                found = _projectileSlotById.ContainsKey(text);
                break;
        }

        if (!found)
        {
            throw MissingForeignKey(row, column, text, TableNameOf(domain), "Id");
        }
        return new ContentRef(ContentHash.Fnv1a32(text), 1f, text);
    }

    private static string TableNameOf(RefDomain domain) => domain switch
    {
        RefDomain.Objects => "Objects",
        RefDomain.Weapons => "Weapons",
        _ => "Projectiles",
    };

    private static ContentException MissingForeignKey(ContentRow row, string column, string target, string targetTable, string targetColumn) =>
        new ContentException(string.Concat(
            row.WhereOf(column), "：引用的 id '", target, "' 在 ", targetTable, " 表的 ", targetColumn,
            " 列里不存在（", row.Table, '.', column, " → ", targetTable, '.', targetColumn, " 外键缺失）。"));

    // ─────────────────────────────────────────────────────────────
    // 每个物件一行的位矩阵数据（ObjectArchetype.Tags）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 Tags 列的 TagId 列表压成 <c>int[]</c>（小端位序：tag t 在 word[t/32] 的 bit[t%32]）。
    /// <para>行数 = Objects 行数、列数 = TagCount，与 <see cref="ObjectBitMatrix"/> 同一布局。</para>
    /// </summary>
    private int[] PackTagBits(ContentRow row)
    {
        int words = WordCountOf(_rules.TagCount);
        if (words == 0)
        {
            return Array.Empty<int>();
        }
        int[] bits = new int[words];
        foreach (string tagId in SplitList(row.Get("Tags")))
        {
            int tag = TagIndexOf(tagId, row, "Tags");
            bits[tag / ObjectBitMatrix.BitsPerWord] |= 1 << (tag % ObjectBitMatrix.BitsPerWord);
        }
        return bits;
    }

    private static int WordCountOf(int tagCount) =>
        (tagCount + ObjectBitMatrix.BitsPerWord - 1) / ObjectBitMatrix.BitsPerWord;

    /// <summary>Recipe 列（<c>a.b.c</c>）→ 64 位配方字；<c>-1</c> 位的 tag 不允许出现在配方里。</summary>
    private ulong ParseRecipeList(ContentRow row, string column)
    {
        string text = row.Get(column).Trim();
        if (text.Length == 0)
        {
            return 0UL;
        }
        ulong recipe = 0UL;
        string[] parts = text.Split(RecipeSeparator);
        for (int i = 0; i < parts.Length; i++)
        {
            string tagId = parts[i].Trim();
            if (tagId.Length == 0)
            {
                continue;
            }
            int bit = BitMaskOf(tagId);
            if (bit == int.MinValue)
            {
                throw new ContentException(string.Concat(
                    row.WhereOf(column), "：tag '", tagId, "' 不存在于 Tags 表。"));
            }
            if (bit < 0)
            {
                throw new ContentException(string.Concat(
                    row.WhereOf(column), "：tag '", tagId, "' 的 BitMask 是 -1（不进配方字），不能出现在 Recipe 列。"));
            }
            recipe |= 1UL << bit;
        }
        return recipe;
    }

    /// <summary>Tags 列（<c>a,b,c</c>）→ 位或的配方字（只看有配方位的 tag）。</summary>
    private ulong RecipeOf(ContentRow row)
    {
        ulong recipe = 0UL;
        foreach (string tagId in SplitList(row.Get("Tags")))
        {
            int bit = BitMaskOf(tagId);
            if (bit == int.MinValue)
            {
                throw new ContentException(string.Concat(
                    row.WhereOf("Tags"), "：tag '", tagId, "' 不存在于 Tags 表。"));
            }
            if (bit >= 0)
            {
                recipe |= 1UL << bit;
            }
        }
        return recipe;
    }

    private int TagIndexOf(string tagId, ContentRow row, string column)
    {
        if (_tagIndexById.TryGetValue(tagId, out int index))
        {
            return index;
        }
        throw new ContentException(string.Concat(
            row.WhereOf(column), "：tag '", tagId, "' 不存在于 Tags 表（该列必须写 Tags 表的 TagId）。"));
    }

    private int ClusterSlotOf(string cluster)
    {
        if (cluster.Length == 0)
        {
            return ObjectArchetype.NoCluster;
        }
        if (!_clusterSlotByName.TryGetValue(cluster, out int slot))
        {
            slot = _clusterNames.Count;
            _clusterSlotByName.Add(cluster, slot);
            _clusterNames.Add(cluster);
        }
        return slot;
    }

    private static int SlotIn(List<ContentRow> rows, ContentRow row, string table)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }
        throw new ContentException(string.Concat(
            table, "：这一行（第 ", row.RowNumber.ToString(CultureInfo.InvariantCulture),
            " 行）不在本绑定器的索引里 —— 单行绑定必须先 Index", table, "。"));
    }

    private static IEnumerable<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }
        string[] parts = text.Split(ListSeparator);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length > 0)
            {
                yield return part;
            }
        }
    }
}
