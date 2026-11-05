using System;
using System.Collections.Generic;
using System.Numerics;
using Game.Core.Actors;

namespace Game.Core.Content;

// 本文件 = 「数值引用与求值」的全部契约（基础物件抽象 A 段）。
// 设计总纲：所有 Def（ObjectArchetype / WeaponContract / ProjectileContract）里的每一个数字
// 都不是 float 常量，而是一个 ValueRef / ValueSet —— 一个「取值规则」。
// 于是同一张表里的同一条规则可以被无数组合复用（高自由度组合），策划改表不需要改代码（便于改表），
// 而运行期求值走的是定长内联结构（热路径零分配）。

/// <summary>
/// 数值的**来源规则**：说明一个 <see cref="ValueRef"/> 里的数字「从哪来」。
/// <para>
/// <b>为什么要把取值规则做成数据而不是硬编码的字段</b>：
/// </para>
/// <list type="bullet">
/// <item>「伤害 = 武器伤害 × 80% + 投掷者力量 × 0.5」这类公式一旦能写进表，新增武器类型、新增词条、
/// 熔炉派生新物件就全部是<b>填表</b>而不是写代码（规则4：数值不进口码）；</item>
/// <item>反过来，如果 Def 里只有 float，那么「子弹继承武器伤害」「每穿透一次衰减 10%」这些规则
/// 就必须写进系统里，系统会随组合数爆炸 —— 这正是本轮抽象要消灭的东西。</item>
/// </list>
/// <para>
/// 显式给值、只加不改：新增一种来源 = 在末尾追加一个成员 + 在 <see cref="ValueResolver"/> 里加一个 case，
/// 已有表的含义不变（存盘 / 回放仍然可读）。
/// </para>
/// </summary>
public enum ValueSource : byte
{
    /// <summary>字面量：<see cref="ValueRef.A"/> 就是数值本身。</summary>
    Literal = 0,

    /// <summary>角色/物件自身的数值包属性：<c>StatId</c> 编码在 <see cref="ValueRef.StatIdCode"/> 里。</summary>
    Stat = 1,

    /// <summary>「来源武器」属性：链上上一段武器身上的数值；来源武器缺失时回落到 <see cref="ValueRef.A"/> 里的武器默认值。</summary>
    WeaponStat = 2,

    /// <summary>位矩阵规则：<see cref="ValueRef.Tag"/> 指定的 tag 在对应位矩阵行里是否点亮（1 / 0）。</summary>
    Flags = 3,

    /// <summary>熔炉链深度（子弹由武器射出、子弹再射出子弹时逐层 +1）。</summary>
    ChainDepth = 4,

    /// <summary>链上已累计的物件个数（同一次发射算一发，递推时累加）。</summary>
    ChainCount = 5,

    /// <summary>当前「可发动」数量（例如同时存在的同类子弹数），用于「越少越强」类规则。</summary>
    ViableCount = 6,

    /// <summary>性能预算系数 0..1（帧预算紧张时由宿主调低，让重特效自动降级而不是卡到不能玩）。</summary>
    Perf = 7,

    /// <summary>随机缩放：<c>A × (1 + B × rand)</c>，用来做「伤害有 ±10% 的抖动」这类手感。</summary>
    RandomScaled = 8,

    /// <summary>随机区间：<c>A + rand × (B − A)</c>，min/max 都来自表。</summary>
    RandomRange = 9,
}

/// <summary>
/// 一个**可解析的数值引用**（Def 里一个格子）。
/// <para>
/// <b>为什么是 <c>readonly struct</c> 而不是多态类</b>：
/// </para>
/// <list type="bullet">
/// <item>每个武器有十来个这样的格子，每张表上千个；类 = 每人一个堆对象 + 一次虚调用，求值热路径要逐发子弹跑；</item>
/// <item>值类型可以直接躺在 <see cref="ValueSet"/> 的内联槽里，整个 Def 求值过程中 0 次分配（<see cref="ValueSet"/> 的零 GC 承诺靠它成立）；</item>
/// <item>它构成 Def 的「全等比较」基础：两个引用逐字段相等 ⇔ 两条规则完全一样，测试与热重载都能直接比。</item>
/// </list>
/// <para>
/// 字段含义按 <see cref="Source"/> 解释，四个字段在<b>所有</b>来源下都有确定含义（见各工厂方法的注释），
/// 这样序列化器（Excel 生成器）只需要一套列即可覆盖全部分支：<c>Source / A / B / StatIdCode / Percent / Tag</c>。
/// </para>
/// </summary>
public readonly struct ValueRef : IEquatable<ValueRef>
{
    /// <summary>取值来源规则。</summary>
    public readonly ValueSource Source;

    /// <summary>
    /// 第一个数字参数：
    /// <see cref="ValueSource.Literal"/> = 字面量；
    /// <see cref="ValueSource.Stat"/>/<see cref="ValueSource.WeaponStat"/> = 公式系数（<c>value × A</c>，默认 1 = 原值）；
    /// <see cref="ValueSource.Flags"/> = 位掩码；<see cref="ValueSource.RandomScaled"/> = 基准值；<see cref="ValueSource.RandomRange"/> = 下界。
    /// </summary>
    public readonly float A;

    /// <summary>
    /// 第二个数字参数：
    /// <see cref="ValueSource.RandomScaled"/> = 抖动幅度（0..1）；<see cref="ValueSource.RandomRange"/> = 上界；其余来源未使用。
    /// </summary>
    public readonly float B;

    /// <summary>属性编码（<see cref="ValueSource.Stat"/> / <see cref="ValueSource.WeaponStat"/> 使用）。</summary>
    public readonly int StatIdCode;

    /// <summary>是否百分比：0 = 绝对值（0.3 = +0.3），1 = 百分比（0.3 = +30%，用于层叠时的倍率段）。</summary>
    public readonly byte Percent;

    /// <summary>位矩阵 tag 下标（<see cref="ValueSource.Flags"/> 使用）。</summary>
    public readonly byte Tag;

    private ValueRef(ValueSource source, float a, float b, StatId statId, byte percent, byte tag)
    {
        Source = source;
        A = a;
        B = b;
        StatIdCode = (int)statId;
        Percent = percent;
        Tag = tag;
    }

    /// <summary>字面量引用：表里直接写了个数。</summary>
    public static ValueRef Literal(float v) => new ValueRef(ValueSource.Literal, v, 0f, StatId.None, 0, 0);

    /// <summary>
    /// 自身数值包属性。默认 <c>a = 1</c>（系数 1 = 直接取属性原值），因为「读一个属性」是最常见写法；
    /// 需要「属性的 30%」时传 <c>a = 0.3</c>，而不是改 Percent ——
    /// <see cref="Percent"/> 是**层叠语义**（绝对值段 vs 倍率段），不是乘法，两者不可混用。
    /// </summary>
    public static ValueRef OfStat(StatId id, bool percent = false, float a = 1f, float b = 0f) =>
        new ValueRef(ValueSource.Stat, a, b, id, percent ? (byte)1 : (byte)0, 0);

    /// <summary>
    /// 来源武器属性（「子弹具有武器属性」的数据基础）。
    /// <para>
    /// <paramref name="a"/> / <paramref name="b"/> = 武器默认值：链上<b>没有</b>来源武器时（例如子弹由技能直接生成、
    /// 或熔炉把武器拆掉后单独发射），解析结果回落到这两个值。语义按「不可用 = 0」处理 ——
    /// 表里没给默认值（A=B=0）就意味着没有武器在背后撑腰时这一发打不出伤害，而不是静默沿用某个魔法数。
    /// </para>
    /// <para>
    /// 注意与 <see cref="OfStat"/> 的区别：那个 <c>a</c> 是系数，这里的 <c>a</c> 是<b>默认值本体</b>
    /// （因为没有武器就无从「取系数」）。
    /// </para>
    /// </summary>
    public static ValueRef OfWeaponStat(StatId id, bool percent = false, float a = 0f, float b = 0f) =>
        new ValueRef(ValueSource.WeaponStat, a, b, id, percent ? (byte)1 : (byte)0, 0);

    /// <summary>
    /// 无参来源（<see cref="ValueSource.Flags"/> / <see cref="ValueSource.ChainDepth"/> /
    /// <see cref="ValueSource.ChainCount"/> / <see cref="ValueSource.ViableCount"/> / <see cref="ValueSource.Perf"/>）。
    /// <para>
    /// <paramref name="tag"/> 只在 <see cref="ValueSource.Flags"/> 下有意义；把它交给其它无参来源不抛异常
    /// （表里多填一列不该让整局炸掉），只在求值后被忽略 —— 期望为 0（与 <see cref="ValueRef"/> 的默认值一致）。
    /// </para>
    /// </summary>
    public static ValueRef Flag(ValueSource src, byte tag) => new ValueRef(src, 1f, 0f, StatId.None, 0, tag);

    /// <summary>随机区间 [<paramref name="min"/>, <paramref name="max"/>]（含端点近似值，随机源是左闭右开）。</summary>
    public static ValueRef Range(float min, float max) => new ValueRef(ValueSource.RandomRange, min, max, StatId.None, 0, 0);

    /// <summary>
    /// 位矩阵规则带掩码的写法：检查 <paramref name="tag"/> 所在 word 上 <paramref name="mask"/> 里的<b>任意</b>一位是否点亮。
    /// <para>为什么掩码是可配置的：一条规则常常想问「这行勾了 A 或 B 中任何一个吗」，
    /// 用掩码表达后组合逻辑依然全在表里，代码不需要认识任何具体 tag。</para>
    /// </summary>
    public static ValueRef FlagMask(byte tag, int mask) =>
        new ValueRef(ValueSource.Flags, mask, 0f, StatId.None, 0, tag);

    /// <summary>
    /// 带百分比的字面量：<c>PercentOf(0.2f)</c> = 层叠时的「+20%」段。
    /// <para>为什么要这个入口而不是让调用方自己拼位标志：层叠语义（绝对值段 / 倍率段）是契约的一部分，
    /// 用显式工厂方法表达，读表的人一眼能看出这一格属于哪一段。</para>
    /// </summary>
    public static ValueRef PercentOf(float v) => new ValueRef(ValueSource.Literal, v, 0f, StatId.None, 1, 0);

    /// <summary>
    /// 随机缩放 <c>A × (1 + B × rand)</c>，表里一次占两列（基准值 + 抖动幅度），
    /// 比「Range(min,max)」更贴近策划的思考方式（「10 点伤害带 20% 抖动」）。
    /// </summary>
    public static ValueRef RandomScaled(float baseline, float jitter) => new ValueRef(ValueSource.RandomScaled, baseline, jitter, StatId.None, 0, 0);

    /// <summary>是否指向一个属性（<see cref="ValueSource.Stat"/> 或 <see cref="ValueSource.WeaponStat"/>）。</summary>
    public bool IsStatRef => Source == ValueSource.Stat || Source == ValueSource.WeaponStat;

    /// <summary>该引用携带的属性 id（非属性引用返回 <see cref="StatId.None"/>）。</summary>
    public StatId StatValue => IsStatRef ? (StatId)StatIdCode : StatId.None;

    /// <inheritdoc />
    public bool Equals(ValueRef other) =>
        Source == other.Source
        && A.Equals(other.A)
        && B.Equals(other.B)
        && StatIdCode == other.StatIdCode
        && Percent == other.Percent
        && Tag == other.Tag;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine((int)Source, A, B, StatIdCode, Percent, Tag);

    /// <summary>值相等。</summary>
    public static bool operator ==(ValueRef a, ValueRef b) => a.Equals(b);

    /// <summary>值不等。</summary>
    public static bool operator !=(ValueRef a, ValueRef b) => !a.Equals(b);

    /// <inheritdoc />
    public override string ToString() => Source switch
    {
        ValueSource.Literal => string.Concat("Lit(", A.ToString("0.###"), ")"),
        ValueSource.Stat => string.Concat("Stat(", ((StatId)StatIdCode).ToString(), "×", A.ToString("0.###"), Percent != 0 ? "%" : string.Empty, ")"),
        ValueSource.WeaponStat => string.Concat("WStat(", ((StatId)StatIdCode).ToString(), "%default=", A.ToString("0.###"), ")"),
        ValueSource.Flags => string.Concat("Flag[", Tag.ToString(), "&", ((int)A).ToString(), "]"),
        ValueSource.RandomRange => string.Concat("Range(", A.ToString("0.###"), "..", B.ToString("0.###"), ")"),
        ValueSource.RandomScaled => string.Concat("Scaled(", A.ToString("0.###"), "±", B.ToString("0.###"), ")"),
        _ => Source.ToString(),
    };
}

/// <summary>
/// 一个 Def 格子的**定长引用集合**：按层叠公式求值 ——
/// <c>Σ(绝对量) × (1 + Σ(百分比))</c>。
/// <para>
/// <b>为什么需要一组而不是一个引用</b>：同一格经常是「基础值 + 武器加成 + 词条倍率」的叠加，
/// 策划希望把这些分开填（一行一条，便于复用与熔炉按部件替换），而不是手工合并成一个公式字符串。
/// </para>
/// <para>
/// <b>为什么是 4 槽内联数组而不是 <c>ValueRef[]</c></b>：
/// </para>
/// <list type="bullet">
/// <item>求值发生在每发子弹的生成/命中路径上，<c>ValueRef[]</c> 每次都要多一次堆访问，构造 ValueSet 还会分配；</item>
/// <item>4 槽足以覆盖「1 基础 + 3 层加成」的绝大多数格子，超出时 <see cref="From(ValueRef[])"/> 会抛异常 ——
/// 这是<b>表写错</b>的信号，宁可构建期炸掉，也不要静默丢规则（丢规则 = 玩家永远看不到某条词条生效）；</item>
/// <item>整个结构 unmanaged，可以进 ECS 组件，也可以被 C++ 侧按同一布局读取（ECS.md §9）。</item>
/// </list>
/// </summary>
public struct ValueSet
{
    /// <summary>内联槽数：固定短集合，避免堆分配。</summary>
    public const int Capacity = 4;

    /// <summary>4 个内联槽（未使用的槽为 <c>default</c>，即 <see cref="ValueSource.Literal"/> 0，不被 <see cref="Count"/> 计入）。</summary>
    private ValueRef _r0;
    private ValueRef _r1;
    private ValueRef _r2;
    private ValueRef _r3;

    /// <summary>有效槽数（0..<see cref="Capacity"/>）。</summary>
    public int Count { get; private set; }

    /// <summary>空集合：求值结果为 0（绝对值 0 × 倍率 1）。</summary>
    public static ValueSet Empty => default;

    /// <summary>取第 <paramref name="index"/> 条引用；越界抛 <see cref="ArgumentOutOfRangeException"/>（表与代码不一致必须当场暴露）。</summary>
    public ValueRef this[int index] => index switch
    {
        0 when Count > 0 => _r0,
        1 when Count > 1 => _r1,
        2 when Count > 2 => _r2,
        3 when Count > 3 => _r3,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "ValueSet 索引越界（有效范围 0..Count-1）。"),
    };

    /// <summary>单条引用。</summary>
    public static ValueSet From(ValueRef single)
    {
        ValueSet set = default;
        set.Add(in single);
        return set;
    }

    /// <summary>
    /// 多条引用（层叠顺序：先绝对值，再百分比 —— 求值按层段分组，与排列顺序无关）。
    /// 参数为 <c>null</c> 或长度超过 <see cref="Capacity"/> 时抛异常。
    /// </summary>
    public static ValueSet From(params ValueRef[] refs)
    {
        if (refs is null)
        {
            throw new ArgumentNullException(nameof(refs));
        }
        if (refs.Length > Capacity)
        {
            throw new ArgumentException(
                string.Concat("ValueSet 最多容纳 ", Capacity.ToString(), " 条引用，实际 ", refs.Length.ToString(),
                    " 条。请把它拆成两列（如基础值列 + 加成列），不要静默丢弃规则。"),
                nameof(refs));
        }
        ValueSet set = default;
        for (int i = 0; i < refs.Length; i++)
        {
            set.Add(in refs[i]);
        }
        return set;
    }

    /// <summary>是否为空（求值恒为 0）。</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>追加一条引用（构建期用；满 4 条抛异常）。</summary>
    public void Add(in ValueRef r)
    {
        if (Count >= Capacity)
        {
            throw new InvalidOperationException(
                string.Concat("ValueSet 已满（", Capacity.ToString(), " 条），不能再追加。"));
        }
        switch (Count)
        {
            case 0: _r0 = r; break;
            case 1: _r1 = r; break;
            case 2: _r2 = r; break;
            default: _r3 = r; break;
        }
        Count++;
    }

    /// <summary>
    /// 追加另一组引用（构建器拼接两段规则用，例如「基础 + 词条」）。
    /// 超出容量抛异常，理由同 <see cref="Add"/>。
    /// </summary>
    public void AddRange(in ValueSet other)
    {
        if (Count + other.Count > Capacity)
        {
            throw new InvalidOperationException(
                string.Concat("ValueSet 拼接后超过 ", Capacity.ToString(), " 条（", Count.ToString(), " + ", other.Count.ToString(), "）。"));
        }
        for (int i = 0; i < other.Count; i++)
        {
            ValueRef r = other[i];
            Add(in r);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>(Count);
        for (int i = 0; i < Count; i++)
        {
            parts.Add(this[i].ToString());
        }
        return string.Concat("Set{", string.Join(" + ", parts), "}");
    }
}

/// <summary>
/// 求值的**环境**：把「世界里的当前状态」交给规则去读。
/// <para>
/// <b>为什么是接口而不是直接传 <c>World</c></b>：
/// </para>
/// <list type="bullet">
/// <item><c>Game.Core.Content</c> 不能依赖 <c>Game.Data</c>（数值包仓库在数据层）也不能依赖系统族；
/// 接口让数据层 / 宿主 / 测试各自实现，<c>Game.Core</c> 保持零反向依赖（J4 的同类纪律）；</item>
/// <item>测试可以塞一个假世界，把「属性 20、武器属性缺省、随机恒 0.5」写死，于是规则求值变成纯函数可用例钉住；</item>
/// <item>宿主的实现可以只做数组下标（属性值来自已折叠的数值包快照），不产生任何分配。</item>
/// </list>
/// <para>
/// 方法都带 <c>TryXxx</c> 而不是「查不到返回 0」：<b>「没有这个值」和「这个值是 0」是两件事</b> ——
/// 武器默认值回落、百分比段是否参与，都取决于前者。
/// </para>
/// </summary>
public interface IValueWorld
{
    /// <summary>
    /// 按数值包组合取属性最终值。<paramref name="ids"/> 由 <see cref="ValueContext"/> 传入：
    /// Core 不认识数值包仓库，实现方（Game.Data）拿它去 <c>IStatPackageRepository.Compose</c> 折叠。
    /// 未声明返回 false（调用方按 0 处理，不抛异常：跨 schema 读取是数据驱动的常态）。
    /// </summary>
    bool TryStat(in StatPackageIds ids, StatId id, out float value);

    /// <summary>取「来源武器」（熔炉链上一段的武器物件）的属性；链上没有武器返回 false，规则回落到 <see cref="ValueRef.A"/>。</summary>
    bool TryWeaponStat(StatId id, out float value);

    /// <summary>该 tag 在对应位矩阵行里的整段掩码（tag t 落在 word t&gt;&gt;5 的 bit t&amp;31；本方法返回该 word）。</summary>
    int Flags(byte tag);

    /// <summary>熔炉链深度（顶层物件为 0）。</summary>
    int ChainDepth { get; }

    /// <summary>链上累计物件个数。</summary>
    int ChainCount { get; }

    /// <summary>当前可发动数量。</summary>
    int ViableCount { get; }

    /// <summary>性能预算 0..1（1 = 满预算）。缺省实现给 1，保证「没实现预算的宿主」行为与满速一致。</summary>
    float Perf { get; }

    /// <summary>随机源（必须注入：同 seed 同结果，回放与测试才可复现）。</summary>
    IRandomProvider Random { get; }

    /// <summary>位矩阵 tag 总数（求值前用它做 tag 越界判定，避免读到别的行的内存）。</summary>
    int TagCount { get; }
}

/// <summary>
/// 求值上下文：<b>一次求值所需的全部输入</b>（世界 + 数值包组合 + 随机源）。
/// <para>
/// <b>为什么把 <see cref="StatPackageIds"/> 放在这里，而不是让 <see cref="IValueWorld"/> 自己持有</b>：
/// 同一个世界里同时存在玩家、怪物、子弹、武器 —— 谁身上的「属性」取决于<b>正在求值的是哪个 Def</b>，
/// 不属于世界的全局状态。把它做成上下文字段后，「求玩家武器伤害」与「求怪物子弹伤害」
/// 只需要 <see cref="WithStats"/> 换一下组合，世界实现完全不用知道自己是替谁算的。
/// </para>
/// <para>
/// 它也是 <c>readonly struct</c>：每发子弹求值十几格，如果上下文是类就得反复传递堆引用并冒「被后续系统改掉」的风险；
/// 值语义保证求值期间输入是<b>快照</b>。
/// </para>
/// </summary>
public readonly struct ValueContext
{
    /// <summary>求值环境（实现方：Game.Data / 宿主 / 测试）。为空时求值退化为「只有字面量可用」。</summary>
    public readonly IValueWorld World;

    /// <summary>本轮求值所代表的物件身上的数值包组合。</summary>
    public readonly StatPackageIds Stats;

    /// <summary>随机源。<b>注意</b>：随机规则优先用 <see cref="World"/> 的随机源 —— 随机源是有状态的，
    /// 若上下文另带一个，就容易出现「两条规则各自推进不同序列」导致回放分叉；
    /// 只有在 <see cref="World"/> 没给随机源时才用这里（测试里只关心随机、不关心其它状态时很方便）。</summary>
    public readonly IRandomProvider Random;

    /// <summary>构造上下文。</summary>
    public ValueContext(IValueWorld world, in StatPackageIds stats, IRandomProvider random)
    {
        World = world;
        Stats = stats;
        Random = random;
    }

    /// <summary>换一份数值包组合（熔炉派生新物件、夺舍、武器加成生效时用），其余保持不变。</summary>
    public ValueContext WithStats(in StatPackageIds stats) => new ValueContext(World, stats, Random);

    /// <summary>解析单个引用。</summary>
    public float Resolve(in ValueRef r) => ValueResolver.Resolve(in r, in this);

    /// <summary>解析一组引用（层叠公式）。</summary>
    public float Resolve(in ValueSet set) => ValueResolver.Resolve(in set, in this);
}

/// <summary>
/// 规则解释器：把 <see cref="ValueRef"/> 变成 float。
/// <para>
/// <b>为什么规则解释器是 <c>static</c> 纯函数而不是上下文的方法</b>：求值是无状态推导，
/// 放在静态类里可以被系统、编辑器预览、数据校验脚本<b>同一份</b>调用（策划在插件里预览的数值
/// 与运行时算出来的必须是同一个数，否则「表里看着没事、进游戏不对」会天天发生）。
/// </para>
/// <para>
/// <b>热路径纪律</b>：全程 <c>in</c> 传值、无枚举装箱、无闭包、无 <c>List</c>；
/// 每个分支只做几次乘加。逐发子弹多跑十几次是可以接受的，前提是「不分配」。
/// </para>
/// </summary>
public static class ValueResolver
{
    /// <summary>解析一组引用：<c>Σ(绝对量) × (1 + Σ(百分比))</c>。</summary>
    /// <remarks>
    /// 层叠公式为什么长这样：绝对值段回答「基础是多少」，百分比段回答「相对地加成多少」，
    /// 两段分开填表才能让「+10 基础伤害」与「+20% 伤害」各自独立地来自不同词条 / 不同熔炉层，
    /// 并且相加顺序不影响结果（避免同一套词条因为表里的行序变化而算出不同伤害）。
    /// </remarks>
    public static float Resolve(in ValueSet set, in ValueContext ctx)
    {
        float abs = 0f;
        float pct = 0f;
        for (int i = 0; i < set.Count; i++)
        {
            ValueRef r = set[i];
            if (r.Percent != 0)
            {
                // 百分比段也不应该是「带符号的倍率」以外的语义：A=0.3 → +30%。
                pct += Resolve(in r, in ctx);
            }
            else
            {
                abs += Resolve(in r, in ctx);
            }
        }
        return abs * (1f + pct);
    }

    /// <summary>解析单个引用（单个引用按「直接值」返回，不做 1+x 的换算 —— 换算只在层叠时发生）。</summary>
    public static float Resolve(in ValueRef r, in ValueContext ctx)
    {
        switch (r.Source)
        {
            case ValueSource.Literal:
                return r.A;

            case ValueSource.Stat:
            {
                // 属性引用：系数 × 属性值。属性不存在按 0（跨 schema 读取是常态，不该炸）。
                if (ctx.World is not null && ctx.World.TryStat(in ctx.Stats, r.StatValue, out float v))
                {
                    return v * (r.A == 0f ? 1f : r.A);
                }
                return 0f;
            }

            case ValueSource.WeaponStat:
            {
                // 「子弹具有武器属性」的数据落点：优先问链上的来源武器，问不到才用表里填的默认值。
                if (ctx.World is not null && ctx.World.TryWeaponStat(r.StatValue, out float v))
                {
                    return v;
                }
                return r.A;
            }

            case ValueSource.Flags:
            {
                // 位矩阵：规则只描述「哪个 tag」，不知道任何具体 tag 是谁 —— 组合逻辑全在表里。
                if (ctx.World is null || ctx.World.TagCount <= r.Tag)
                {
                    return 0f;
                }
                int mask = (int)r.A;
                return (ctx.World.Flags(r.Tag) & mask) != 0 ? 1f : 0f;
            }

            case ValueSource.ChainDepth:
                return ctx.World?.ChainDepth ?? 0;

            case ValueSource.ChainCount:
                return ctx.World?.ChainCount ?? 0;

            case ValueSource.ViableCount:
                return ctx.World?.ViableCount ?? 0;

            case ValueSource.Perf:
                // 缺省 1（满预算）：没实现预算的宿主不该让所有特效突然变弱。
                return ctx.World?.Perf ?? 1f;

            case ValueSource.RandomScaled:
            {
                IRandomProvider random = RandomOf(in ctx);
                if (random is null)
                {
                    return r.A;
                }
                return r.A * (1f + r.B * random.NextFloat());
            }

            case ValueSource.RandomRange:
            {
                IRandomProvider random = RandomOf(in ctx);
                if (random is null)
                {
                    return r.A;
                }
                return r.A + random.NextFloat() * (r.B - r.A);
            }

            default:
                // 未知来源（表里填了未来版本才有的规则名）：返回 0 并让上层用「表校验」报错，
                // 而不是抛异常把整局游戏炸掉 —— 一张坏表不该让玩家掉线。
                return 0f;
        }
    }

    /// <summary>
    /// 取随机源：优先 <see cref="ValueContext.World"/> 的（有状态随机源必须来自同一处，回放才可复现），
    /// 其次上下文自带的，两者都没有时返回 <c>null</c>（随机规则退化到「无抖动」的确定性结果）。
    /// </summary>
    public static IRandomProvider RandomOf(in ValueContext ctx) => ctx.World?.Random ?? ctx.Random;
}

/// <summary>
/// 定长向量：<c>(ValueSet X, Y, Z)</c>（方向、偏移之类「一格里要填三个数」的字段）。
/// <para>
/// <b>为什么每轴各存一条规则</b>：随机抖动常常只需要一个轴（「水平散布」），
/// 拆开存之后调用方可以按需只解析需要的轴，避免「只改 X 却推进随机源三次」造成回放分叉。
/// </para>
/// <para>整体 unmanaged：可以进 ECS 组件，也可以按同一布局被 C++ 侧读取。</para>
/// </summary>
public readonly struct ValueVector
{
    /// <summary>X 轴规则。</summary>
    public readonly ValueSet X;

    /// <summary>Y 轴规则。</summary>
    public readonly ValueSet Y;

    /// <summary>Z 轴规则。</summary>
    public readonly ValueSet Z;

    /// <summary>构造。</summary>
    public ValueVector(in ValueSet x, in ValueSet y, in ValueSet z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>全零向量（空格子）。</summary>
    public static ValueVector Zero => default;

    /// <summary>把各轴规则按给定上下文解析成 <see cref="System.Numerics.Vector3"/>。</summary>
    public Vector3 Eval(in ValueContext ctx) =>
        new Vector3(ValueResolver.Resolve(in X, in ctx), ValueResolver.Resolve(in Y, in ctx), ValueResolver.Resolve(in Z, in ctx));
}

/// <summary>
/// 定长四元数：<c>(X, Y, Z, W)</c> 各分量一条规则（「枪口斜 15°」这类挂点朝向进表，不进代码）。
/// </summary>
public readonly struct ValueRotation
{
    /// <summary>四元数 X 分量规则。</summary>
    public readonly ValueSet X;

    /// <summary>四元数 Y 分量规则。</summary>
    public readonly ValueSet Y;

    /// <summary>四元数 Z 分量规则。</summary>
    public readonly ValueSet Z;

    /// <summary>四元数 W 分量规则。</summary>
    public readonly ValueSet W;

    /// <summary>构造。</summary>
    public ValueRotation(in ValueSet x, in ValueSet y, in ValueSet z, in ValueSet w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    /// <summary>单位四元数（空格子）。</summary>
    public static ValueRotation Identity =>
        new ValueRotation(ValueSet.From(ValueRef.Literal(0f)), ValueSet.From(ValueRef.Literal(0f)),
            ValueSet.From(ValueRef.Literal(0f)), ValueSet.From(ValueRef.Literal(1f)));

    /// <summary>把各分量规则按给定上下文解析成 <see cref="System.Numerics.Quaternion"/>。</summary>
    public Quaternion Eval(in ValueContext ctx) =>
        new Quaternion(ValueResolver.Resolve(in X, in ctx), ValueResolver.Resolve(in Y, in ctx),
            ValueResolver.Resolve(in Z, in ctx), ValueResolver.Resolve(in W, in ctx));
}
