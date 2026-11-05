using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;

namespace Game.Data.Content;

/// <summary>
/// 把 Excel 单元格里的**规则值字符串**翻成 <see cref="ValueRef"/>。
/// <para>
/// <b>为什么需要一层"规则值语法"（这是"策划能改数值"的关键）</b>：
/// </para>
/// <list type="bullet">
/// <item>如果表里只能填 <c>12.5</c>，那么"子弹伤害 = 武器伤害 × 80%"就必须写进代码 —— 策划改不动，
/// 而肉鸽游戏的乐趣恰恰在组合：同一颗子弹挂在不同枪上要算出不同的数；</item>
/// <item>规则值语法让这句话直接写在格子里（<c>wstat:WeaponDamage(12)</c>），于是新增组合 = 改一格单元格；</item>
/// <item>它同时是<u>编译期</u>的翻译：解析发生一次，得到的是定长 <see cref="ValueRef"/>（4 个 float/int），
/// 之后每发子弹求值都只有一次 switch（见 <see cref="ValueResolver"/>），没有字符串比较。</item>
/// </list>
/// <para>
/// <b>语法表</b>（单元格原样，不区分大小写的前缀、区分大小写的枚举名）：
/// </para>
/// <code>
/// 12.5                  字面量
/// stat:Dexterity        读持有者数值包最终值（系数 1）
/// stat:Damage+30%       读属性并按 0.3 作为系数缩放（"+" 是"叠加"的写法，"-30%" 为负系数）
/// wstat:WeaponDamage(5) 读链上来源武器属性；链上没有武器时回落到括号里的默认值 5
/// flags:pierce(1)       位矩阵里该 tag 的取值（tag 是 Tags 表的 TagIndex，掩码默认为 1）
/// chain:depth           熔炉链深度        chain:count  链上物件数
/// viable:count          当前可发动数量    perf         性能预算系数
/// rand:2.0~3.0          随机区间          rand:10+0.5  基准值 ±50% 抖动
/// </code>
/// <para>
/// <b>为什么解析失败要抛「中文 + 表/行/列/原文」</b>：数据错误的修复者是策划，不是程序。
/// 报错必须直接把人送回具体单元格（docs/rules/04 §9.2）；只说"解析失败"等于把排查成本转嫁给别人。
/// 本类的 <see cref="TryParse"/> 返回布尔 + 错误原因，由调用方（绑定器）拼上位置信息再抛 <see cref="ContentException"/>。
/// </para>
/// <para>
/// <b>分配纪律</b>：本类只在装配期运行（每张表一次），允许 substring / 异常；
/// 但它<u>产生的对象</u>必须是零分配的，所以成功路径只构造 <see cref="ValueRef"/> 值类型。
/// </para>
/// <para>
/// <b>tag 的下标从哪来</b>：<c>flags:</c> 后面写的是 Tags 表的 TagId（如 <c>tag.kinetic</c>），
/// 需要用 <see cref="ContentRules"/> 的列头翻成下标；本类不持有规则表，由调用方通过
/// <see cref="TryParse(string, ContentRules?, out ValueRef, out string)"/> 传进来。
/// 传 <c>null</c> 时 <c>flags:</c> 会被判为"无 tag 表上下文"，不允许静默当成 0 ——
/// 那会让"这条规则到底生效没有"变成不可判定。
/// </para>
/// </summary>
public static class ValueRefParser
{
    /// <summary>字面量与 <see cref="ValueSource.Stat"/> 系语法共用的小数样式：固定小数点、不做千分位。</summary>
    private const NumberStyles FloatStyle = NumberStyles.Float;

    /// <summary>整数样式的允许写法：不允许浮点尾巴（<c>flags:pierce(1.5)</c> 是表写错）。</summary>
    private const NumberStyles IntegerStyle = NumberStyles.Integer;

    /// <summary>
    /// 解析一条规则值（无 tag 上下文）。
    /// <para>
    /// 只用于不涉及 <c>flags:</c> 的场景（单测、纯字面量的批量转换）。
    /// 含 <c>flags:</c> 时必须用带 <see cref="ContentRules"/> 的重载，否则会被明确判错。
    /// </para>
    /// </summary>
    public static bool TryParse(string? text, out ValueRef value, out string error) =>
        TryParse(text, null, out value, out error);

    /// <summary>
    /// 解析一条规则值；<paramref name="rules"/> 提供 tag 名 → 下标的映射。
    /// </summary>
    public static bool TryParse(string? text, ContentRules? rules, out ValueRef value, out string error)
    {
        if (!TryParseCore(text, rules, out value, out error))
        {
            return false;
        }

        // 判空后 text 必然非 null（TryParseCore 已经把空白判成错误）。
        string trimmed = text!.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// 解析一条规则值，失败直接抛异常（信息里带 <paramref name="where"/> = "表名 第N行 列名"）。
    /// <para>绑定器的正常路径用这个：它在装配期一次处理一整张表，任何一格写错都应当让构建失败。</para>
    /// </summary>
    public static ValueRef Parse(string? text, ContentRules? rules, string where)
    {
        if (!TryParse(text, rules, out ValueRef value, out string error))
        {
            throw new ContentException(string.Concat(
                where, " 列 '", text ?? string.Empty, "'：", error));
        }
        return value;
    }

    private static bool TryParseCore(string? text, ContentRules? rules, out ValueRef value, out string error)
    {
        value = default;
        if (text is null)
        {
            error = "规则值为空（空单元格不能当作 0：那样表里漏填一格就会被静默当成 0 伤害）。";
            return false;
        }

        string s = text.Trim();
        if (s.Length == 0)
        {
            error = "规则值为空（空单元格不能当作 0：那样表里漏填一格就会被静默当成 0 伤害）。";
            return false;
        }

        int colon = s.IndexOf(':');
        if (colon < 0)
        {
            // 无冒号：要么是裸字面量，要么是**无参来源**（perf）。
            // 为什么 perf 走这条分支：语法表里 perf 就是三个字母、没有冒号；校验器的规则就是这么定的，
            // 若只认 "perf:..." 这种带冒号写法，表里合法的 "perf" 会被判成解析失败。
            if (string.Equals(s, "perf", StringComparison.OrdinalIgnoreCase))
            {
                value = ValueRef.Flag(ValueSource.Perf, 0);
                error = string.Empty;
                return true;
            }

            // 裸数字：绝大多数格子（速度、半径、寿命）就是个数，
            // 逼策划写 "lit:1.2" 只会增加出错概率。
            if (!TryParseFloat(s, out float literal))
            {
                error = "无法解析为规则值（可写：字面量 12.5 / stat:Xxx / wstat:Xxx(默认值) / flags:Xxx / chain:depth / viable:count / perf / rand:a~b）";
                return false;
            }
            value = ValueRef.Literal(literal);
            error = string.Empty;
            return true;
        }

        string head = s.Substring(0, colon).Trim().ToLowerInvariant();
        string tail = s.Substring(colon + 1).Trim();
        if (tail.Length == 0)
        {
            error = string.Concat("规则值缺少冒号后的内容（写法形如 '", head, ":...'）。");
            return false;
        }

        switch (head)
        {
            case "literal":
            case "lit":
                if (!TryParseFloat(tail, out float lit))
                {
                    error = string.Concat("字面量 '", tail, "' 不是合法数字。");
                    return false;
                }
                value = ValueRef.Literal(lit);
                error = string.Empty;
                return true;

            case "stat":
                return TryParseStat(tail, ValueSource.Stat, rules, out value, out error);

            case "wstat":
            case "weaponstat":
                return TryParseStat(tail, ValueSource.WeaponStat, rules, out value, out error);

            case "flags":
            case "flag":
                return TryParseFlags(tail, rules, out value, out error);

            case "chain":
                return TryParseChain(tail, out value, out error);

            case "viable":
                if (!string.Equals(tail, "count", StringComparison.OrdinalIgnoreCase))
                {
                    error = string.Concat("未知的 viable 规则 '", tail, "'（只支持 viable:count）。");
                    return false;
                }
                value = ValueRef.Flag(ValueSource.ViableCount, 0);
                error = string.Empty;
                return true;

            case "perf":
                // perf 没有参数，但允许写成 perf:1 这种"带默认值"的形式以兼容手误；参数会被忽略。
                if (!string.Equals(tail, "perf", StringComparison.OrdinalIgnoreCase) && !IsPlainNumber(tail))
                {
                    error = string.Concat("perf 不接受参数 '", tail, "'（写法就是 'perf'）。");
                    return false;
                }
                value = ValueRef.Flag(ValueSource.Perf, 0);
                error = string.Empty;
                return true;

            case "rand":
                return TryParseRandom(tail, out value, out error);

            default:
                error = string.Concat("未知的规则前缀 '", head, "'（可用：stat / wstat / flags / chain / viable / perf / rand）。");
                return false;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // stat: / wstat:
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>stat:Xxx</c> 与 <c>wstat:Xxx(默认值)</c>。
    /// <para>
    /// 系数写法 <c>stat:Damage+30%</c> 被翻译成 <see cref="ValueRef.OfStat"/> 的 <c>a = 0.3</c>（系数），
    /// <b>而不是</b> <c>Percent = true</c>：后者是"层叠时的倍率段"语义（决定它加在绝对段还是倍率段），
    /// 这里策划想表达的是"取这个属性的 30%"。两者混用会让同一个格子在不同上下文里算出不同的数。
    /// </para>
    /// </summary>
    private static bool TryParseStat(string tail, ValueSource source, ContentRules? rules, out ValueRef value, out string error)
    {
        value = default;

        // wstat 允许括号里的默认值；stat 的括号同样是"系数"，统一按 1 处理以免出现两种语义。
        string name = tail;
        float fallback = 0f;
        float coefficient = 1f;
        bool hasCoefficient = false;

        int open = tail.IndexOf('(');
        if (open >= 0)
        {
            if (!tail.EndsWith(")", StringComparison.Ordinal))
            {
                error = string.Concat("括号没有闭合：'", tail, "'。");
                return false;
            }
            // 括号内的文本：【左括号后一位 到 右括号前一位】，两个 1 是"索引偏移"而非配置数值。
            string inner = tail.Substring(open + 1, tail.Length - open - 2).Trim(); // arch-ok: Substring 索引偏移（开括号位/闭括号位），非游戏数值
            if (!TryParseFloat(inner, out fallback))
            {
                error = string.Concat("括号里的默认值 '", inner, "' 不是合法数字。");
                return false;
            }
            name = tail.Substring(0, open).Trim();
        }

        // 系数尾缀：+30% / -0.3 / *0.8。放在括号之前（括号是 wstat 的默认值）。
        int sign = IndexOfAny(name, '+', '-', '*');
        if (sign > 0)
        {
            string coefText = name.Substring(sign).Trim();
            name = name.Substring(0, sign).Trim();
            if (!TryParseCoefficient(coefText, out coefficient))
            {
                error = string.Concat("系数写法 '", coefText, "' 无法解析（形如 +30% / -0.25 / *0.8）。");
                return false;
            }
            hasCoefficient = true;
        }

        if (name.Length == 0)
        {
            error = "缺少属性名（写法形如 'stat:Damage'）。";
            return false;
        }

        if (!TryParseStatId(name, out StatId stat))
        {
            error = string.Concat("'", name, "' 不是 StatId 的成员（属性名区分大小写，须与 C# StatId 枚举一致）。");
            return false;
        }
        if (stat.IsNone())
        {
            error = "StatId.None 不能作为规则来源（它是占位值）。";
            return false;
        }

        if (source == ValueSource.WeaponStat)
        {
            // OfWeaponStat 的 a 是"没有来源武器时的默认值本体"，不是系数 —— 所以系数在这里先乘进默认值。
            if (hasCoefficient)
            {
                fallback *= coefficient;
            }
            value = ValueRef.OfWeaponStat(stat, percent: false, a: fallback);
        }
        else
        {
            if (open >= 0 && hasCoefficient)
            {
                error = string.Concat("stat: 不能同时写系数与括号（'", tail, "'）：stat 的括号不是默认值。");
                return false;
            }
            if (open >= 0)
            {
                coefficient = fallback;
            }
            value = ValueRef.OfStat(stat, percent: false, a: coefficient);
        }

        // rules 在本分支只用于标签，不参与属性名解析；保留参数以统一签名。
        _ = rules;
        error = string.Empty;
        return true;
    }

    private static bool TryParseStatId(string name, out StatId stat)
    {
        stat = StatId.None;
        if (!Enum.TryParse(name, ignoreCase: false, out StatId parsed) || !Enum.IsDefined(typeof(StatId), parsed))
        {
            return false;
        }
        stat = parsed;
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // flags:
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>flags:pierce</c> / <c>flags:tag.pierce(1)</c>。
    /// <para>
    /// 写入 <see cref="ValueRef.Flag"/> 的 <c>tag</c> 必须是 **tag 下标**（<see cref="ContentRules.TagOf"/> 的结果），
    /// 因为求值端只认下标（<see cref="ObjectBitMatrix.Mask"/> 的参数）；表里给人写的是 TagId 名字，
    /// 翻译就在这一层完成 —— 代码里因此 0 个具体 tag 字符串（规则7）。
    /// </para>
    /// </summary>
    private static bool TryParseFlags(string tail, ContentRules? rules, out ValueRef value, out string error)
    {
        value = default;
        string name = tail;
        int mask = 1;
        int open = tail.IndexOf('(');
        if (open >= 0)
        {
            if (!tail.EndsWith(")", StringComparison.Ordinal))
            {
                error = string.Concat("括号没有闭合：'", tail, "'。");
                return false;
            }
            // 括号内的文本：【左括号后一位 到 右括号前一位】，两个 1 是"索引偏移"而非配置数值。
            string inner = tail.Substring(open + 1, tail.Length - open - 2).Trim(); // arch-ok: Substring 索引偏移（开括号位/闭括号位），非游戏数值
            if (!int.TryParse(inner, IntegerStyle, CultureInfo.InvariantCulture, out mask))
            {
                error = string.Concat("掩码 '", inner, "' 不是合法整数。");
                return false;
            }
            name = tail.Substring(0, open).Trim();
        }

        if (name.Length == 0)
        {
            error = "缺少 tag 名（写法形如 'flags:tag.kinetic'）。";
            return false;
        }
        if (rules is null)
        {
            error = string.Concat("无法解析 tag '", name, "'：当前没有 Tags 表上下文（flags 规则必须提供 ContentRules）。");
            return false;
        }

        int tag = rules.TagOf(name);
        if (tag < 0)
        {
            error = string.Concat("Tags 表里没有 tag '", name, "'（flags 后面必须写 Tags 表的 TagId）。");
            return false;
        }
        if (tag > byte.MaxValue)
        {
            error = string.Concat("tag 下标 ", tag.ToString(CultureInfo.InvariantCulture), " 超出 ValueRef.Tag 的单字节容量。");
            return false;
        }
        if (mask < 0)
        {
            error = string.Concat("掩码 '", mask.ToString(CultureInfo.InvariantCulture), "' 不能为负（掩码是位与，负数等于检查最高位）。");
            return false;
        }

        // 掩码 1 = 「只问这一位」；表里写了括号就按掩码求交（合同层见 ValueRef.FlagMask）。
        value = ValueRef.FlagMask((byte)tag, mask);
        error = string.Empty;
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // chain:
    // ─────────────────────────────────────────────────────────────

    private static bool TryParseChain(string tail, out ValueRef value, out string error)
    {
        value = default;
        if (string.Equals(tail, "depth", StringComparison.OrdinalIgnoreCase))
        {
            value = ValueRef.Flag(ValueSource.ChainDepth, 0);
            error = string.Empty;
            return true;
        }
        if (string.Equals(tail, "count", StringComparison.OrdinalIgnoreCase))
        {
            value = ValueRef.Flag(ValueSource.ChainCount, 0);
            error = string.Empty;
            return true;
        }
        error = string.Concat("未知的 chain 规则 '", tail, "'（只支持 chain:depth / chain:count）。");
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    // rand:
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>rand:2.0~3.0</c>（区间）与 <c>rand:10+0.5</c>（基准 ± 抖动）。
    /// <para>
    /// 抖动写法里 <c>+0.5</c> 表示 **±50%**（<see cref="ValueSource.RandomScaled"/> 的 B 是幅度），
    /// 与 <c>stat:Damage+30%</c> 的百分比是同一套直觉：策划写 "伤害 10 带 50% 抖动" 最自然。
    /// </para>
    /// </summary>
    private static bool TryParseRandom(string tail, out ValueRef value, out string error)
    {
        value = default;

        int tilde = tail.IndexOf('~');
        if (tilde > 0)
        {
            string loText = tail.Substring(0, tilde).Trim();
            string hiText = tail.Substring(tilde + 1).Trim();
            if (!TryParseFloat(loText, out float lo) || !TryParseFloat(hiText, out float hi))
            {
                error = string.Concat("随机区间 '", tail, "' 的两端都必须是数字（写法 rand:min~max）。");
                return false;
            }
            if (hi < lo)
            {
                error = string.Concat("随机区间 '", tail, "' 的上界小于下界（求值端按 min + rand × (max − min) 计算，会恒等于下界）。");
                return false;
            }
            value = ValueRef.Range(lo, hi);
            error = string.Empty;
            return true;
        }

        // 抖动写法：基准 ± 幅度。幅度可写 0.5 或 50%。
        int sign = IndexOfAny(tail, '+', '-');
        if (sign > 0)
        {
            string baseText = tail.Substring(0, sign).Trim();
            string jitterText = tail.Substring(sign).Trim();
            if (!TryParseFloat(baseText, out float baseline))
            {
                error = string.Concat("随机抖动 '", tail, "' 的基准值不是数字（写法 rand:基准±幅度）。");
                return false;
            }
            if (!TryParseCoefficient(jitterText, out float jitter))
            {
                error = string.Concat("随机抖动 '", tail, "' 的幅度不是合法写法（形如 +0.5 / +50%）。");
                return false;
            }
            value = ValueRef.RandomScaled(baseline, jitter);
            error = string.Empty;
            return true;
        }

        error = string.Concat("随机规则 '", tail, "' 缺少区间或抖动（写法 rand:2.0~3.0 或 rand:10+0.5）。");
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    // 数字与系数
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析浮点：同时接受 <c>,</c> 作小数点（Excel 中文区域会把 1,25 存成文本）。
    /// <para>docs/rules/04 §5.5 要求统一写 <c>.</c>，但把它当错误拒绝会让策划在中文输入法下反复踩坑；这里宽容读取。</para>
    /// </summary>
    private static bool TryParseFloat(string text, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string s = text.Trim();
        if (float.TryParse(s, FloatStyle, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        int comma = s.IndexOf(',');
        if (comma > 0 && s.IndexOf('.') < 0)
        {
            string dotted = string.Concat(s.AsSpan(0, comma), ".", s.AsSpan(comma + 1));
            return float.TryParse(dotted, FloatStyle, CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    /// <summary>解析系数尾缀：<c>+30%</c> / <c>-0.25</c> / <c>*0.8</c> / <c>*1.5</c>。</summary>
    private static bool TryParseCoefficient(string text, out float coefficient)
    {
        coefficient = 1f;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim();
        bool multiply = false;
        if (s[0] == '*')
        {
            multiply = true;
            s = s.Substring(1).Trim();
        }

        bool percent = s.EndsWith("%", StringComparison.Ordinal);
        if (percent)
        {
            s = s.Substring(0, s.Length - 1).Trim();
        }

        if (!TryParseFloat(s, out float parsed))
        {
            return false;
        }

        // "%" 一律按百分点换算（30% → 0.3）；乘号写法 *0.8 已是系数本身，不再除以 100。
        if (percent)
        {
            coefficient = parsed / PercentScale;
        }
        else if (multiply)
        {
            coefficient = parsed;
        }
        else
        {
            // 无 % 的 +0.3 直接当系数（与 docs/rules/04 §5.5「百分比写小数」一致）。
            coefficient = parsed;
        }
        return true;
    }

    /// <summary>
    /// 百分比换算的分母（100）。它是"百分点 → 小数"的单位换算，不是游戏数值，
    /// 因此允许以 const 形式存在；改名保留 <c>arch-ok</c> 说明以便守卫人工复核。
    /// </summary>
    private const float PercentScale = 100f; // arch-ok: 百分点到小数的单位换算，非平衡数值

    private static bool IsPlainNumber(string text) =>
        float.TryParse(text, FloatStyle, CultureInfo.InvariantCulture, out _);

    private static int IndexOfAny(string text, char a, char b, char c)
    {
        // 从 1 开始：首字符的符号属于数字本身（"-1"）。
        for (int i = 1; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == a || ch == b || ch == c)
            {
                return i;
            }
        }
        return -1;
    }

    private static int IndexOfAny(string text, char a, char b)
    {
        for (int i = 1; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == a || ch == b)
            {
                return i;
            }
        }
        return -1;
    }
}
