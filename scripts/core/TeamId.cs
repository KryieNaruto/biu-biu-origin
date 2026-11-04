namespace Game.Core;

/// <summary>
/// 阵营标识（ECS.md §4.2 的 <c>Team</c> 组件字段类型；docs/rules/03 §4.1 <c>IActor.Team</c> 的返回类型）。
/// <para>为什么用 enum 而不是 <c>readonly record struct TeamId(int)</c>：</para>
/// <list type="bullet">
/// <item>阵营在本项目里是「小集合 + 高频相等比较」（每发子弹命中都要判敌我）；enum 天生 <c>unmanaged</c>，
/// 可以直接放进 ECS 组件的 chunk 列，没有额外分配与间接层。</item>
/// <item>Excel 的 <c>enum:TeamId</c> 列（docs/rules/04 §6.1）按成员名写值，enum 才能被数据生成器直接映射成整数。</item>
/// <item>record struct 还得自己补 ToString / Excel 双向映射，收益为零。</item>
/// </list>
/// <para>纪律：<b>显式给值、只加不改、禁止重排</b>（存档与回放要按数值还原阵营）。</para>
/// </summary>
public enum TeamId : int
{
    /// <summary>中立：环境伤害、无主抛射物。「默认值必须是安全值」在这里的体现 —— 忘填阵营的实体变成中立而不是玩家。</summary>
    Neutral = 0,

    /// <summary>玩家阵营。</summary>
    Player = 1,

    /// <summary>敌方阵营。</summary>
    Enemy = 2,

    // 预留号段：3..15 留给「友军 / 召唤物 / 夺舍期间的特殊阵营」等后续扩展。
    // 新成员只能从 16 往上追加，绝不允许插进上面的号里。
}

/// <summary>
/// <see cref="TeamId"/> 的判定扩展：把「敌我」的定义收在一个地方，而不是散落成 <c>a != b</c>。
/// <para>为什么必须收口：一旦将来引入「友军阵营」，游戏里几十处 <c>a != b</c> 会全部变成语义错误，
/// 而且编译器不会报 —— 这是典型的静默错误。热路径统一走这里，改规则只改本文件的判定体。</para>
/// <para>全部是 <c>in</c>/值传参的静态纯函数：不进虚表、不分配，可以在 chunk 循环里逐实体调用。</para>
/// </summary>
public static class TeamIdExtensions
{
    /// <summary>
    /// 是否允许友军误伤。当前玩法是 PVE，同阵营互不伤害；将来做 PVP 只翻这个开关，
    /// 不需要在伤害系统里加 <c>if</c>。
    /// </summary>
    public const bool AllowFriendlyFire = false;

    /// <summary>
    /// 敌我判定唯一入口：来自 <paramref name="self"/> 的伤害是否应当落到 <paramref name="target"/> 上。
    /// <para>中立不参与敌对：环境伤害走独立结算入口，不经过本判定，所以中立对任何人都是 false。</para>
    /// </summary>
    public static bool IsHostileTo(this TeamId self, TeamId target)
    {
        if (self == target)
        {
            return false;
        }

        if (self == TeamId.Neutral || target == TeamId.Neutral)
        {
            return false;
        }

        return true;
    }

    /// <summary>同阵营判定（治疗、增益、友军 AI 分组用）。中立对中立算同阵营。</summary>
    public static bool IsAllyOf(this TeamId self, TeamId target) => self == target;
}
