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
    private readonly Dictionary<string, int> _tagOf;

    /// <summary>tag 名列表（索引 = tag id，按表顺序稳定）。</summary>
    public IReadOnlyList<string> TagNames { get; }

    /// <summary>tag 总数（= 位矩阵每行的位宽来源）。</summary>
    public int TagCount => _tagNames.Length;

    private ContentRules(string[] tagNames)
    {
        _tagNames = tagNames;
        TagNames = Array.AsReadOnly(tagNames);
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
        return new ContentRules(names.ToArray());
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

    /// <inheritdoc />
    public override string ToString() => string.Concat("ContentRules(tags=", TagCount.ToString(), ")");
}
