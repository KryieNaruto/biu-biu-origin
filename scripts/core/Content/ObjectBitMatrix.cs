using System;
using System.Collections.Generic;

namespace Game.Core.Content;

/// <summary>
/// 「物件 × 标签」的**位矩阵**：策划在表里勾出来的网格，代码只认 tag 下标、不认 tag 名字。
/// <para>
/// <b>为什么需要它（这是本轮抽象的核心组合点）</b>：
/// </para>
/// <list type="bullet">
/// <item>「这发子弹能不能点燃」「这颗子弹被武器加成吗」「这个物体是不是可被熔炉熔掉」这类联动，
/// 如果写成 <c>if (projectile.Kind == Fire)</c>，每加一种组合就要改一次代码，组合数一多就必然失控；</item>
/// <item>位矩阵把这些结论变成<b>数据</b>：标签是表头列（<see cref="ContentRules"/>），行是物件（武器行 / 投射物行），
/// 格子 = 勾选与否。于是同一份矩阵可以被任意新的武器、子弹、怪物组合复用，代码里 0 个具体 tag（规则7）；</item>
/// <item>求值端只需要一句 <c>(row word &amp; mask) != 0</c>，一行数据一个 int，正好铺满 cache line。</item>
/// </list>
/// <para>
/// <b>布局</b>（小端位序，与 <c>BitArray</c> / C++ <c>std::bitset</c> 的直觉一致）：
/// tag <c>t</c> 落在第 <c>t &gt;&gt; 5</c> 个 int 的第 <c>t &amp; 31</c> 位。
/// 之所以返回「整个 word」而不是「单个 bit 的 mask」：规则层（<see cref="ValueSource.Flags"/>）已经用
/// <see cref="ValueRef.A"/> 表达要检查哪几位，掩码求交才是热路径需要的形状。
/// </para>
/// <para>
/// <b>不可变</b>：构造后只读（<see cref="Set"/> 只在构建期可用，运行期改矩阵 = 改内容，违反「Excel 唯一真源」）。
/// 多线程只读安全。
/// </para>
/// </summary>
public sealed class ObjectBitMatrix
{
    /// <summary>一个 int 承载的 tag 数（32 位）。是布局常量，不是游戏数值。</summary>
    public const int BitsPerWord = 32;

    private readonly int[] _bits;
    private readonly bool _sealed;

    /// <summary>行数（一件物件一行；也用于「按 defId 行号查行」）。</summary>
    public int RowCount { get; }

    /// <summary>tag 总数（= <see cref="ContentRules.TagCount"/>；两者不一致就是表与代码漂移）。</summary>
    public int TagCount { get; }

    /// <summary>每行占用的 int 数 = <c>ceil(TagCount / 32)</c>。</summary>
    public int WordCount { get; }

    /// <summary>
    /// 由打包好的位构造。数据来自 Excel 生成物（或测试）—— 形状必须是
    /// <c>rowCount × WordCount(TagCount)</c> 个 int，否则说明表与列头不一致，当场炸掉而不是读出别的行的位。
    /// </summary>
    public ObjectBitMatrix(int rowCount, int tagCount, int[] packedBits)
    {
        if (rowCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "行数不能为负。");
        }
        if (tagCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tagCount), tagCount, "tag 数不能为负。");
        }

        int wordCount = (tagCount + BitsPerWord - 1) / BitsPerWord;
        if (packedBits is null)
        {
            throw new ArgumentNullException(nameof(packedBits));
        }
        if (packedBits.Length != rowCount * wordCount)
        {
            throw new ArgumentException(
                string.Concat("位矩阵形状不匹配：需要 ", (rowCount * wordCount).ToString(), " 个 int（", rowCount.ToString(),
                    " 行 × ", wordCount.ToString(), " word），实际 ", packedBits.Length.ToString(),
                    " 个。多半是 Excel 的 tag 列数与生成物不一致。"),
                nameof(packedBits));
        }

        RowCount = rowCount;
        TagCount = tagCount;
        WordCount = wordCount;
        _bits = packedBits;
        _sealed = true;
    }

    /// <summary>
    /// 构建期的空矩阵（全部未勾选）。用 <see cref="Set"/> 逐格填好后调用
    /// <see cref="Build"/> 冻结 —— 典型的「加载器边读表边填位」流程。
    /// </summary>
    public ObjectBitMatrix(int rowCount, int tagCount)
    {
        if (rowCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount), rowCount, "行数不能为负。");
        }
        if (tagCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tagCount), tagCount, "tag 数不能为负。");
        }

        RowCount = rowCount;
        TagCount = tagCount;
        WordCount = (tagCount + BitsPerWord - 1) / BitsPerWord;
        _bits = WordCount * rowCount == 0 ? Array.Empty<int>() : new int[WordCount * rowCount];
        _sealed = false;
    }

    /// <summary>
    /// 取「该 tag 所在 word」的整段掩码（规则求值用：<c>(Mask(row,tag) &amp; 规则掩码) != 0</c>）。
    /// 越界（行或 tag 不在范围内）返回 0 —— <b>越界不是错误</b>：规则可能来自另一张表版本，
    /// 读到越界 tag 的语义是「这条规则对本物件不成立」，而让整局游戏因为一个 tag 越界而崩掉是不划算的。
    /// </summary>
    public int Mask(int row, int tag)
    {
        if ((uint)row >= (uint)RowCount || (uint)tag >= (uint)TagCount)
        {
            return 0;
        }
        return _bits[row * WordCount + (tag / BitsPerWord)];
    }

    /// <summary>该行该 tag 是否勾选（等价于 <c>(Mask(row,tag) &amp; (1 &lt;&lt; (tag &amp; 31))) != 0</c>，但只读一个位）。</summary>
    public bool Has(int row, int tag)
    {
        if ((uint)row >= (uint)RowCount || (uint)tag >= (uint)TagCount)
        {
            return false;
        }
        return (_bits[row * WordCount + (tag / BitsPerWord)] & (1 << (tag & (BitsPerWord - 1)))) != 0;
    }

    /// <summary>
    /// 构建期勾选一位。<paramref name="value"/> 为 false 时清除。
    /// 冻结后（<see cref="Build"/> 或按打包位构造之后）调用抛异常：
    /// 运行期改矩阵等于绕开 Excel 真源改玩法数据。
    /// </summary>
    public void Set(int row, int tag, bool value = true)
    {
        if (_sealed)
        {
            throw new InvalidOperationException(
                "位矩阵已冻结：运行期不得修改。要改组合请改 Excel 并重新生成（规则4：表是唯一真源）。");
        }
        if ((uint)row >= (uint)RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, string.Concat("行越界（共 ", RowCount.ToString(), " 行）。"));
        }
        if ((uint)tag >= (uint)TagCount)
        {
            throw new ArgumentOutOfRangeException(nameof(tag), tag, string.Concat("tag 越界（共 ", TagCount.ToString(), " 个 tag）。"));
        }

        int idx = row * WordCount + (tag / BitsPerWord);
        int bit = 1 << (tag & (BitsPerWord - 1));
        if (value)
        {
            _bits[idx] |= bit;
        }
        else
        {
            _bits[idx] &= ~bit;
        }
    }

    /// <summary>
    /// 冻结构建期矩阵：之后 <see cref="Set"/> 会抛异常。
    /// 返回 <c>this</c>，便于 <c>ObjectBitMatrix m = new(...).Build();</c> 的写法。
    /// <para>为什么要显式冻结：加载器写完表后如果有引用漏出来，别的系统就能在运行期偷偷改掉
    /// 「这颗子弹是否吃武器加成」—— 这类 bug 只在特定组合下出现，极难排查。</para>
    /// </summary>
    public ObjectBitMatrix Build()
    {
        var frozen = new ObjectBitMatrix(RowCount, TagCount, (int[])_bits.Clone());
        return frozen;
    }

    /// <summary>把整行位复制到目标 span（C++ 侧 / 批量求值要一次性拿多行时用，避免逐格调用）。</summary>
    public void CopyRowTo(int row, Span<int> destination)
    {
        if ((uint)row >= (uint)RowCount)
        {
            return;
        }
        if (destination.Length < WordCount)
        {
            throw new ArgumentException(
                string.Concat("目标 span 太短：需要 ", WordCount.ToString(), " 个 int。"), nameof(destination));
        }
        _bits.AsSpan(row * WordCount, WordCount).CopyTo(destination);
    }

    /// <summary>
    /// 该行勾选了多少个 tag（数据校验 / 调试面板用；热路径不要调）。
    /// </summary>
    public int PopCount(int row)
    {
        if ((uint)row >= (uint)RowCount)
        {
            return 0;
        }
        int total = 0;
        for (int w = 0; w < WordCount; w++)
        {
            total += System.Numerics.BitOperations.PopCount((uint)_bits[row * WordCount + w]);
        }
        return total;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("BitMatrix(", RowCount.ToString(), "×", TagCount.ToString(), ", wordsPerRow=", WordCount.ToString(), ")");
}

/// <summary>
/// 位矩阵的**列头**：tag 名 ↔ tag 下标 的唯一映射（表里第一行的列名）。
/// <para>
/// <b>为什么 tag 是「表序索引」而不是枚举</b>：枚举意味着「新增一种组合要改代码 + 重新编译 + 存档兼容」；
/// 而表序索引意味着新增一种 tag 只是 Excel 里多一列（索引按列顺延，天然稳定）。
/// 代价是代码里不能写 <c>TagOf("burn")</c> 这种具体名字 —— 这是<u>刻意的</u>：
/// 一旦代码开始认具体 tag，位矩阵就又退化成硬编码的分支了。
/// </para>
/// <para>
/// 名字查表是构建期/编辑器行为（字典建一次，之后只在加载与表校验时用），不在帧热路径上。
/// </para>
/// </summary>
public sealed class ContentRules
{
    private readonly string[] _tagNames;
    private readonly string[] _tagFamilies;
    private readonly Dictionary<string, int> _tagOf;
    private readonly Dictionary<TagFamily, IReadOnlyList<int>> _tagsByFamily;
    private readonly TagTier[] _tagTiers;
    private readonly Dictionary<TagTier, IReadOnlyList<int>> _tagsByTier;

    /// <summary>tag 名列表（索引 = tag id，按表顺序稳定）。</summary>
    public IReadOnlyList<string> TagNames { get; }

    /// <summary>tag 总数（= 位矩阵每行的位宽来源）。</summary>
    public int TagCount => _tagNames.Length;

    /// <summary>
    /// 各轴上挂了哪些 tag（<b>轴 → tag id 列表</b>）。
    /// <para>
    /// 这是「按标签范围索引」的唯一入口：强化要「所有稀有武器」、图鉴要「所有 Element 轴标签」、
    /// 掉落要「所有 Rarity=common 的候选」，都从这里取范围，再去位矩阵上筛。
    /// 没有这一层，每个玩法都要自己遍历 tag 名字做字符串匹配。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<TagFamily, IReadOnlyList<int>> TagsByFamily => _tagsByFamily;

    /// <summary>各层挂了哪些 tag（**层 → tag id 列表**）。图鉴 / 编辑器面板按层分组用。</summary>
    public IReadOnlyDictionary<TagTier, IReadOnlyList<int>> TagsByTier => _tagsByTier;

    private ContentRules(string[] tagNames, string[]? tagFamilies, string[]? tagTiers)
    {
        _tagNames = tagNames;
        TagNames = Array.AsReadOnly(tagNames);

        // 轴信息缺省时全部落在 None：兼容「只关心 tag id 与位」的老调用方与测试夹具。
        _tagFamilies = new string[tagNames.Length];
        var buckets = new Dictionary<TagFamily, List<int>>();
        for (int i = 0; i < tagNames.Length; i++)
        {
            TagFamily family = tagFamilies is not null && i < tagFamilies.Length
                ? TagFamilies.Parse(tagFamilies[i])
                : TagFamily.None;
            _tagFamilies[i] = TagFamilies.NameOf(family);
            if (!buckets.TryGetValue(family, out List<int>? bucket))
            {
                bucket = new List<int>(4);
                buckets.Add(family, bucket);
            }
            bucket.Add(i);
        }

        // 层索引：缺省时全部落 Logic —— 本字段是后加的，老调用方不该因为没传层就丢掉轴功能。
        _tagTiers = new TagTier[tagNames.Length];
        var tierBuckets = new Dictionary<TagTier, List<int>>();
        for (int i = 0; i < tagNames.Length; i++)
        {
            TagTier tier = tagTiers is not null && i < tagTiers.Length
                ? TagTiers.Parse(tagTiers[i])
                : TagTier.Logic;
            _tagTiers[i] = tier;
            if (!tierBuckets.TryGetValue(tier, out List<int>? tierBucket))
            {
                tierBucket = new List<int>(8);
                tierBuckets.Add(tier, tierBucket);
            }
            tierBucket.Add(i);
        }

        _tagsByTier = new Dictionary<TagTier, IReadOnlyList<int>>(tierBuckets.Count);
        foreach (KeyValuePair<TagTier, List<int>> pair in tierBuckets)
        {
            _tagsByTier.Add(pair.Key, pair.Value.ToArray());
        }

        _tagsByFamily = new Dictionary<TagFamily, IReadOnlyList<int>>(buckets.Count);
        foreach (KeyValuePair<TagFamily, List<int>> pair in buckets)
        {
            _tagsByFamily.Add(pair.Key, pair.Value.ToArray());
        }

        _tagOf = new Dictionary<string, int>(tagNames.Length, StringComparer.Ordinal);
        for (int i = 0; i < tagNames.Length; i++)
        {
            // 同名列只认第一次出现：后面那列照样占一个 id（否则位宽会变、已有生成物全错位），
            // 但按名查找会落到第一列上，配合表校验能把重复列报出来。
            if (!_tagOf.ContainsKey(tagNames[i]))
            {
                _tagOf.Add(tagNames[i], i);
            }
        }
    }

    /// <summary>
    /// 按表列顺序构建。<b>索引即 tag id</b>：同一个 Excel 表永远生成同一份规则，
    /// 所以「加一列 tag」只会追加新 id，不会让已发布的组合含义漂移。
    /// </summary>
    public static ContentRules FromTags(IEnumerable<string> tags)
    {
        return FromTags(tags, null, null);
    }

    /// <summary>带轴信息构建（层缺省为 Logic）。</summary>
    public static ContentRules FromTags(IEnumerable<string> tags, IEnumerable<string>? families)
    {
        return FromTags(tags, families, null);
    }

    /// <summary>
    /// 带轴信息构建。<paramref name="families"/> 与 <paramref name="tags"/> <b>按位置一一对应</b>
    /// （第 i 个轴名属于第 i 个 tag），长度不一致时缺的部分按 <see cref="TagFamily.None"/> 处理。
    /// </summary>
    public static ContentRules FromTags(
        IEnumerable<string> tags, IEnumerable<string>? families, IEnumerable<string>? tiers)
    {
        if (tags is null)
        {
            throw new ArgumentNullException(nameof(tags));
        }

        var names = new List<string>();
        foreach (string tag in tags)
        {
            if (string.IsNullOrEmpty(tag))
            {
                throw new ArgumentException("tag 名不能为空（列头为空列会让 tag id 与列错位）。", nameof(tags));
            }
            names.Add(tag);
        }
        return new ContentRules(
            names.ToArray(),
            families is null ? null : new List<string>(families).ToArray(),
            tiers is null ? null : new List<string>(tiers).ToArray());
    }

    /// <summary>按名查 tag id；不存在返回 -1（与 <c>Registry.NoneSlot</c> 同一哨兵约定）。</summary>
    public int TagOf(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return -1;
        }
        return _tagOf.TryGetValue(name, out int index) ? index : -1;
    }

    /// <summary>按 id 取 tag 名；越界返回空串（越界是「表版本不一致」，不该炸掉调试面板）。</summary>
    public string NameOf(int tag) => tag >= 0 && tag < _tagNames.Length ? _tagNames[tag] : string.Empty;

    /// <summary>按 id 取 tag 所属轴；越界返回 <see cref="TagFamily.None"/>。</summary>
    public TagFamily FamilyOf(int tag) =>
        tag >= 0 && tag < _tagFamilies.Length ? TagFamilies.Parse(_tagFamilies[tag]) : TagFamily.None;

    /// <summary>按 id 取 tag 所属层；越界返回 <see cref=TagTier.None/>。</summary>
    public TagTier TierOf(int tag) =>
        tag >= 0 && tag < _tagTiers.Length ? _tagTiers[tag] : TagTier.None;

    /// <summary>某层上挂了哪些 tag id；该层没有任何 tag 时返回空列表（不抛异常）。</summary>
    public IReadOnlyList<int> TagsOfTier(TagTier tier) =>
        _tagsByTier.TryGetValue(tier, out IReadOnlyList<int>? tags) ? tags : Array.Empty<int>();

    /// <summary>
    /// 某层全部标签压成的单一位掩码。用于「这个物体挂的标识标签里，有没有任意一个属于该层」。
    /// <para>
    /// 典型用法：判断「这堆掉落里哪些是被磁铁吸引的」——
    /// <c>(matrix.Mask(row,0) &amp; rules.FamilyMask(TagFamily.Attract,0)) != 0</c>；
    /// 而判断「这件东西带不带任何标识标签」用 <c>rules.TierMask(TagTier.Identity)</c>。
    /// </para>
    /// </summary>
    public int TierMask(TagTier tier, int word = 0) => PackMask(TagsOfTier(tier), word);

    /// <summary>某轴上是否至少有一个 tag（用于报「这一轴是空的」这类表配置错误）。</summary>
    public bool HasFamily(TagFamily family) => _tagsByFamily.TryGetValue(family, out IReadOnlyList<int>? tags) && tags.Count > 0;

    /// <summary>某轴上的全部 tag id；该轴没有任何 tag 时返回空列表（不抛异常 —— 表可以合法地少用一个轴）。</summary>
    public IReadOnlyList<int> TagsOfFamily(TagFamily family) =>
        _tagsByFamily.TryGetValue(family, out IReadOnlyList<int>? tags) ? tags : Array.Empty<int>();

    /// <summary>
    /// 某轴全部标签压成的**单一位掩码**（按 <paramref name="word"/> 取第几段 32 位）。
    /// <para>
    /// 用法：<c>(matrix.Mask(row, 0) &amp; rules.FamilyMask(TagFamily.Element, 0)) != 0</c> 即「这个物体挂了元素轴上的某个标签」。
    /// 一次 AND 就把整个轴筛完，不认识具体标签名 —— 新增元素标签时这里 0 行改动。
    /// </para>
    /// </summary>
    public int FamilyMask(TagFamily family, int word = 0)
    {
        if (word < 0)
        {
            return 0;
        }
        int mask = 0;
        IReadOnlyList<int> tags = TagsOfFamily(family);
        for (int i = 0; i < tags.Count; i++)
        {
            int tag = tags[i];
            if ((tag >> 5) == word)
            {
                mask |= 1 << (tag & 31);
            }
        }
        return mask;
    }

    /// <summary>
    /// 把一组 tag id 压成位掩码（只压落在 <paramref name="word"/> 那一段的位）。
    /// 强化 / 筛选条件通常表达成「这几个标签之一」，用本函数一次压好、之后每帧只做 AND。
    /// </summary>
    public static int PackMask(IReadOnlyList<int> tags, int word = 0)
    {
        if (tags is null || word < 0)
        {
            return 0;
        }
        int mask = 0;
        for (int i = 0; i < tags.Count; i++)
        {
            int tag = tags[i];
            if (tag >= 0 && (tag >> 5) == word)
            {
                mask |= 1 << (tag & 31);
            }
        }
        return mask;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("ContentRules(tags=", TagCount.ToString(), ")");
}
