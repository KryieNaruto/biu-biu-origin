using System;
using Game.Core;
using Game.Core.Content;

namespace Game.Combat;

/// <summary>
/// 熔炉候选：一簇「可替换物件」里的一个成员，<b>由 <c>ContentIndex</c> 在装配期预先算好、扁平存进数组</b>。
/// <para>
/// <b>为什么是一个 readonly struct 而不是对象</b>：熔炉在每次开火时都要跑一遍候选筛选，
/// 一簇可能有十几个候选、一帧可能几十次开火。对象会让热路径产生依赖链路 / 缓存未命中；
/// 这个 struct（16 字节）正好一条 cache line 装四个，遍历就是一次连续内存扫描。
/// </para>
/// <para>
/// <b>为什么不直接持有 <c>ObjectArchetype</c> 引用</b>：那会让 <see cref="ForgeRules"/> 依赖内容层的托管类型，
/// 而熔炉规则本身是「纯算术 + 纯选择」——保持它对内容类型零依赖，才能在测试里用几个数字直接钉住规则。
/// </para>
/// </summary>
public readonly struct ForgeCandidate
{
    /// <summary>候选的 def 槽位号（选中时原样返回，由调用方去 <c>ContentIndex</c> 取对象）。</summary>
    public readonly int DefId;

    /// <summary>候选的标签掩码（配方命中的依据：(TagMask &amp; recipeMask) != 0）。</summary>
    public readonly int TagMask;

    /// <summary>权重（加权随机用；&lt;= 0 的候选不参与加权，只能靠 viable 计数胜出）。</summary>
    public readonly float Weight;

    /// <summary>当前可发动数量（同簇同时存在的实例数；越大越"饱和"，按规则会被降权）。</summary>
    public readonly int ViableCount;

    /// <summary>构造一个候选（装配期用）。</summary>
    public ForgeCandidate(int defId, int tagMask, float weight, int viableCount)
    {
        DefId = defId;
        TagMask = tagMask;
        Weight = weight;
        ViableCount = viableCount;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("ForgeCandidate(#", DefId.ToString(), ", tags=", TagMask.ToString(), ")");
}

/// <summary>熔炉的判定结果类别。</summary>
public enum ForgeOutcome : byte
{
    /// <summary>命中：<see cref="ForgeResult.SelectedDefId"/> 有效。</summary>
    Matched = 0,

    /// <summary>有候选命中了配方，但全部不 viable（被预算/冷却/簇上限挡住）→ 拒绝。</summary>
    Rejected = 1,

    /// <summary>没有任何候选命中配方 → 无匹配。</summary>
    NoMatch = 2,  // arch-ok: 枚举序号，结构性常量
}

/// <summary>
/// 熔炉判定结果（<b>零分配的值类型</b>：返回值 + out 参数，不进堆）。
/// </summary>
public readonly struct ForgeResult
{
    /// <summary>判定类别。</summary>
    public readonly ForgeOutcome Outcome;

    /// <summary>选中的 def 槽位号（仅 <see cref="ForgeOutcome.Matched"/> 时有效，否则为 -1）。</summary>
    public readonly int SelectedDefId;

    /// <summary>
    /// 整簇命中率 0..1：命中配方的候选权重和 / 全部候选权重和。
    /// <para><b>为什么用权重和而不是个数</b>：个数会让「一个稀有候选 + 九个垃圾候选」看起来有 10% 命中率，
    /// 而策划真正想看到的是「这次熔炉被多少『有用的东西』托住了」——权重是策划分给每个候选的"重要性"。</para>
    /// </summary>
    public readonly float Perf;

    /// <summary>命中配方的候选个数（诊断用）。</summary>
    public readonly int MatchedCount;

    /// <summary>构造。</summary>
    public ForgeResult(ForgeOutcome outcome, int selectedDefId, float perf, int matchedCount)
    {
        Outcome = outcome;
        SelectedDefId = selectedDefId;
        Perf = perf;
        MatchedCount = matchedCount;
    }

    /// <summary>是否为成功命中。</summary>
    public bool IsMatched => Outcome == ForgeOutcome.Matched;

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat(Outcome.ToString(), "(#", SelectedDefId.ToString(), ", perf=", Perf.ToString("0.00"), ")");
}

/// <summary>
/// <b>熔炉规则</b>：给定「配方 tag」与「一簇候选」，选出这一次该用哪个物件。纯静态、零分配、无状态。
/// <para>
/// <b>为什么熔炉必须是纯函数而不是系统</b>：
/// </para>
/// <list type="bullet">
/// <item>它要被三个地方复用：<see cref="CastSystem"/> 开火时选武器、子弹到期时选下一段子弹、测试直接钉规则；</item>
/// <item>纯函数没有字段 → 不会被"上一次的残留状态"污染 → 同一帧同一输入永远同一输出（回放对得上）；</item>
/// <item>规则里<b>不出现任何具体 tag</b>：配方是 <c>ulong</c>，候选是位掩码，命中判定只有一次按位与（规则7：
/// 新增一种 tag / 一种配方 = 改数据，改 0 个文件）。</item>
/// </list>
/// <para>
/// <b>三条选择规则（全部数据驱动）</b>：
/// </para>
/// <list type="number">
/// <item><b>命中</b>：<c>(candidate.TagMask &amp; recipeMask) != 0</c> 才算命中配方；</item>
/// <item><b>viable 优先</b>：命中候选里 viable（<see cref="ForgeCandidate.ViableCount"/> &gt; 0）的优先胜出，
///   一个 viable 都没有 → <see cref="ForgeOutcome.Rejected"/>（"配方对得上但现在发不出来"）；</item>
/// <item><b>权重加权随机</b>：viable 候选之间按 <see cref="ForgeCandidate.Weight"/> 加权抽签，
///   随机源来自 <see cref="ValueContext"/>（同 seed 同结果）。</item>
/// </list>
/// </summary>
public static class ForgeRules
{
    /// <summary>「没有选中」哨兵（与 <c>ContentIndex.NoneSlot</c> 同值）。</summary>
    public const int NoneDefId = -1;

    /// <summary>配方 tag 占多少位（64 位 = 8 个字符的 tag 编码，由数据层决定编码方式）。</summary>
    public const int RecipeMaskBits = 64;  // arch-ok: ulong 的位宽，语言事实而非玩法数值

    /// <summary>
    /// 求值：在 <paramref name="candidates"/> 里按 <paramref name="recipeTag"/> 选出一个 def 槽位号。
    /// </summary>
    /// <param name="recipeTag">配方：8 字符 tag 编码成的 64 位整数（编码方式由数据层决定，这里只认 ulong）。</param>
    /// <param name="candidates">候选（扁平数组的一段，调用方用 <c>ReadOnlySpan</c> 给出，**不复制**）。</param>
    /// <param name="ctx">求值环境（随机源从它取；同 seed 同结果）。</param>
    /// <param name="result">判定结果（含 Perf 命中率，调用方写进 <see cref="CastState.LastPerf"/>）。</param>
    /// <returns>选中的 def 槽位号；没选中返回 <see cref="NoneDefId"/>。</returns>
    public static int Evaluate(ulong recipeTag, ReadOnlySpan<ForgeCandidate> candidates, in ValueContext ctx, out ForgeResult result)
    {
        // 配方只用到低 32 位做位与：候选的 TagMask 是 int（32 个 tag 位，见 ContentBitLimits.TagBits）。
        // 高 32 位留给数据层做「配方版本 / 强度档位」等扩展，本规则不解释它。
        int recipeMask = (int)recipeTag;

        if (candidates.Length == 0)
        {
            result = new ForgeResult(ForgeOutcome.NoMatch, NoneDefId, 0f, 0);
            return NoneDefId;
        }

        // ── 第一遍：统计「全部权重和」（Perf 的分母）与「命中权重和 / 命中个数 / viable 权重和」 ──
        // 一遍扫描完成，避免对同一段内存走两遍（候选极少，但规则应写成"一趟"的习惯）。
        float totalWeight = 0f;
        float matchedWeight = 0f;
        float viableWeight = 0f;
        int matchedCount = 0;
        int viableCount = 0;

        for (int i = 0; i < candidates.Length; i++)
        {
            ref readonly ForgeCandidate c = ref candidates[i];
            // 权重为负视为 0：负权重会让"加权抽签"变成"负概率"，是表错误的信号，
            // 这里按 0 处理而不是抛异常（一帧不该因为一格表写错而中断）。
            float weight = c.Weight > 0f ? c.Weight : 0f;
            totalWeight += weight;

            if ((c.TagMask & recipeMask) == 0)
            {
                continue;
            }
            matchedCount++;
            matchedWeight += weight;
            if (c.ViableCount > 0 && weight > 0f)
            {
                viableCount++;
                viableWeight += weight;
            }
        }

        float perf = totalWeight > 0f ? matchedWeight / totalWeight : 0f;

        if (matchedCount == 0)
        {
            result = new ForgeResult(ForgeOutcome.NoMatch, NoneDefId, perf, 0);
            return NoneDefId;
        }
        if (viableCount == 0)
        {
            // 配方命中但现在一个都发不出来：Rejected 与 NoMatch 分开，是因为前者该走"冷却/预算"提示，
            // 后者该报"表里没这个配方"的数据错误，表现层的处理完全不同。
            result = new ForgeResult(ForgeOutcome.Rejected, NoneDefId, perf, matchedCount);
            return NoneDefId;
        }

        // ── 第二遍：在 viable 命中候选里按权重抽签 ──
        IRandomProvider? random = ValueResolver.RandomOf(in ctx);
        int picked = Pick(ref candidates, recipeMask, viableWeight, random);

        int selected = picked >= 0 ? picked : FirstViable(ref candidates, recipeMask);
        result = new ForgeResult(
            selected >= 0 ? ForgeOutcome.Matched : ForgeOutcome.Rejected,
            selected,
            perf,
            matchedCount);
        return selected;
    }

    /// <summary>
    /// <b>扁平候选重载</b>：直接吃数据层摊平的「槽位号 / 权重 / 静态可用数 / 标签掩码」四条数组，
    /// 不为开炉构造 <see cref="ForgeCandidate"/> 数组。
    /// <para>
    /// <b>为什么需要它</b>：<c>ContentSet.CandidatesOf(clusterSlot)</c> 交出来的就是这种扁平三段式
    /// （<c>objectSlots / weights / viableCounts</c>），标签掩码在 <c>ObjectTags</c> 里按行取。
    /// 若强制调用方先 <c>new ForgeCandidate[n]</c> 再喂进来，等于每次开炉多一次分配 + 一次拷贝，
    /// 直接把"零分配"这条硬指标打穿。这里用 <c>ReadOnlySpan</c> 接收，四条数组都<b>不复制</b>。
    /// </para>
    /// </summary>
    /// <param name="recipeTag">配方 tag。</param>
    /// <param name="defIds">候选的 def 槽位号（扁平）。</param>
    /// <param name="tagMasks">与 <paramref name="defIds"/> 等长的标签掩码（取自位矩阵的行）。</param>
    /// <param name="weights">与 <paramref name="defIds"/> 等长的权重（全 1 时退化为等概率随机挑）。</param>
    /// <param name="viableCounts">与 <paramref name="defIds"/> 等长的静态可用数（&gt;0 才可被选中）。</param>
    /// <param name="ctx">求值环境（随机源从它取）。</param>
    /// <param name="result">判定结果。</param>
    /// <returns>选中的 def 槽位号；没选中返回 <see cref="NoneDefId"/>。</returns>
    public static int Evaluate(
        ulong recipeTag,
        ReadOnlySpan<int> defIds,
        ReadOnlySpan<int> tagMasks,
        ReadOnlySpan<float> weights,
        ReadOnlySpan<int> viableCounts,
        in ValueContext ctx,
        out ForgeResult result)
    {
        int recipeMask = (int)recipeTag;
        int count = Min4(defIds.Length, tagMasks.Length, weights.Length, viableCounts.Length);
        if (count == 0)
        {
            result = new ForgeResult(ForgeOutcome.NoMatch, NoneDefId, 0f, 0);
            return NoneDefId;
        }

        float totalWeight = 0f;
        float matchedWeight = 0f;
        float viableWeight = 0f;
        int matchedCount = 0;
        int viableCount = 0;

        for (int i = 0; i < count; i++)
        {
            float weight = weights[i] > 0f ? weights[i] : 0f;
            totalWeight += weight;
            if ((tagMasks[i] & recipeMask) == 0)
            {
                continue;
            }
            matchedCount++;
            matchedWeight += weight;
            if (viableCounts[i] > 0 && weight > 0f)
            {
                viableCount++;
                viableWeight += weight;
            }
        }

        float perf = totalWeight > 0f ? matchedWeight / totalWeight : 0f;

        if (matchedCount == 0)
        {
            result = new ForgeResult(ForgeOutcome.NoMatch, NoneDefId, perf, 0);
            return NoneDefId;
        }
        if (viableCount == 0)
        {
            result = new ForgeResult(ForgeOutcome.Rejected, NoneDefId, perf, matchedCount);
            return NoneDefId;
        }

        IRandomProvider? random = ValueResolver.RandomOf(in ctx);
        int picked = PickFlat(defIds, tagMasks, weights, viableCounts, count, recipeMask, viableWeight, random);
        if (picked < 0)
        {
            picked = FirstViableFlat(defIds, tagMasks, viableCounts, count, recipeMask);
        }

        result = new ForgeResult(
            picked >= 0 ? ForgeOutcome.Matched : ForgeOutcome.Rejected,
            picked,
            perf,
            matchedCount);
        return picked;
    }

    private static int Min4(int a, int b, int c, int d)
    {
        int m = a < b ? a : b;
        m = m < c ? m : c;
        return m < d ? m : d;
    }

    /// <summary>在 viable 命中候选里按权重抽签；<paramref name="random"/> 为空时退化为第一个 viable 命中。</summary>
    private static int Pick(ref ReadOnlySpan<ForgeCandidate> candidates, int recipeMask, float viableWeight, IRandomProvider? random)
    {
        if (random is null || viableWeight <= 0f)
        {
            // 没有随机源（纯逻辑测试 / 回放里没给随机）：退化为「第一个 viable 命中」，
            // 保证确定性 —— 随机永远不应是"结果的必要条件"。
            return FirstViable(ref candidates, recipeMask);
        }
        float roll = random.NextFloat() * viableWeight;
        float cursor = 0f;
        int picked = NoneDefId;
        for (int i = 0; i < candidates.Length; i++)
        {
            ref readonly ForgeCandidate c = ref candidates[i];
            if ((c.TagMask & recipeMask) == 0 || c.ViableCount <= 0 || c.Weight <= 0f)
            {
                continue;
            }
            picked = c.DefId;
            cursor += c.Weight;
            if (roll < cursor)
            {
                break;
            }
        }
        return picked;
    }

    private static int PickFlat(
        ReadOnlySpan<int> defIds,
        ReadOnlySpan<int> tagMasks,
        ReadOnlySpan<float> weights,
        ReadOnlySpan<int> viableCounts,
        int count,
        int recipeMask,
        float viableWeight,
        IRandomProvider? random)
    {
        if (random is null || viableWeight <= 0f)
        {
            return FirstViableFlat(defIds, tagMasks, viableCounts, count, recipeMask);
        }
        float roll = random.NextFloat() * viableWeight;
        float cursor = 0f;
        int picked = NoneDefId;
        for (int i = 0; i < count; i++)
        {
            if ((tagMasks[i] & recipeMask) == 0 || viableCounts[i] <= 0 || weights[i] <= 0f)
            {
                continue;
            }
            picked = defIds[i];
            cursor += weights[i];
            if (roll < cursor)
            {
                break;
            }
        }
        return picked;
    }

    private static int FirstViableFlat(
        ReadOnlySpan<int> defIds,
        ReadOnlySpan<int> tagMasks,
        ReadOnlySpan<int> viableCounts,
        int count,
        int recipeMask)
    {
        for (int i = 0; i < count; i++)
        {
            if ((tagMasks[i] & recipeMask) != 0 && viableCounts[i] > 0)
            {
                return defIds[i];
            }
        }
        return NoneDefId;
    }

    /// <summary>不带候选的简化重载：只算「有没有命中」（用于只关心 Perf 的 HUD 采样）。</summary>
    public static float EvaluatePerf(ulong recipeTag, ReadOnlySpan<ForgeCandidate> candidates)
    {
        int recipeMask = (int)recipeTag;
        float totalWeight = 0f;
        float matchedWeight = 0f;
        for (int i = 0; i < candidates.Length; i++)
        {
            ref readonly ForgeCandidate c = ref candidates[i];
            float weight = c.Weight > 0f ? c.Weight : 0f;
            totalWeight += weight;
            if ((c.TagMask & recipeMask) != 0)
            {
                matchedWeight += weight;
            }
        }
        return totalWeight > 0f ? matchedWeight / totalWeight : 0f;
    }

    /// <summary>
    /// 由「物件的判定半径」推导默认可发动数：半径 &gt; 0 的物件算 1 个可发动槽位，否则算 0（不可发动）。
    /// <para><b>为什么用半径而不是专门的"容量"列</b>：本轮契约里还没有容量列，而"能不能生成"最接近的
    /// 数据事实就是"它有没有实体尺寸"。等数据表加 <c>MaxAlive</c> 列，只需改 <c>ContentRegistry.MakeCandidate</c> 一处。</para>
    /// </summary>
    public static int ViableDefCount(float radius) => radius > 0f ? 1 : 0;

    /// <summary>
    /// 把「tag 位掩码」与「上下文里的位矩阵」合成一个配方掩码。
    /// <para>
    /// 为什么需要它：有些配方不是策划直接填的，而是「本帧命中了这些 tag」动态算出来的
    /// （比如"当前处于燃烧状态"）。把两者按位或后交给 <see cref="Evaluate"/>，
    /// 熔炉本身依旧只认一个 <c>ulong</c>，不需要知道 tag 从哪来。
    /// </para>
    /// </summary>
    public static ulong ComposeRecipe(ulong baseRecipeTag, int runtimeTagMask) =>
        baseRecipeTag | (uint)runtimeTagMask;

    private static int FirstViable(ref ReadOnlySpan<ForgeCandidate> candidates, int recipeMask)
    {
        for (int i = 0; i < candidates.Length; i++)
        {
            ref readonly ForgeCandidate c = ref candidates[i];
            if ((c.TagMask & recipeMask) != 0 && c.ViableCount > 0)
            {
                return c.DefId;
            }
        }
        return NoneDefId;
    }
}
