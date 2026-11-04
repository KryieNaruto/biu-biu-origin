namespace Game.Spawn;

/// <summary>
/// 刷怪配置的**值对象**：由宿主从 <c>WorldHostOptions</c>（Resource）读出来填好，再注入
/// <see cref="SpawnDirectorSystem"/>。
/// <para>
/// <b>为什么不在系统里直接持有 <c>WorldHostOptions</c></b>：那是一个 <c>Resource</c>（引擎类型），
/// 持有它等于让 <c>Game.Spawn</c> 依赖 Godot，违反 J4「引擎只出现在宿主层」。
/// 值对象把「配置的形状」和「配置从哪来」彻底解耦：
/// </para>
/// <list type="bullet">
///   <item>现在：宿主从 Inspector 可调的 Resource 填；</item>
///   <item>Excel 落地后：<c>DataLayer</c> 从 <c>spawn.xlsx</c> 的行填；</item>
///   <item>测试里：测试自己 <c>new SpawnSettings { ... }</c> 填。</item>
/// </list>
/// <para>三种来源，<see cref="SpawnDirectorSystem"/> 一行都不用改（规则7）。</para>
/// </summary>
public readonly struct SpawnSettings
{
    /// <summary>稳态目标存活实体数（冒烟场景每帧补满到这个数）。</summary>
    public int TargetAliveCount { get; init; }

    /// <summary>生成区半径（米）：以原点为中心的水平圆盘。</summary>
    public float Radius { get; init; }

    /// <summary>生成高度（米）。</summary>
    public float Height { get; init; }

    /// <summary>寿命（秒）。</summary>
    public float LifetimeSeconds { get; init; }

    /// <summary>速度区间下界（米/秒）。</summary>
    public float SpeedMin { get; init; }

    /// <summary>速度区间上界（米/秒）。</summary>
    public float SpeedMax { get; init; }

    /// <summary>视觉缩放。</summary>
    public float Scale { get; init; }

    /// <summary>
    /// 构造并校验。非法组合在<b>宿主启动时</b>就抛，而不是等运行时除以零或生成出个空实体——
    /// 早失败比晚失败省一个小时的调试（ECS.md §9.4「配置在边界处校验」）。
    /// </summary>
    /// <param name="targetAliveCount">目标存活数，&gt;= 0。</param>
    /// <param name="radius">生成半径，&gt; 0。</param>
    /// <param name="height">生成高度。</param>
    /// <param name="lifetimeSeconds">寿命，&gt; 0。</param>
    /// <param name="speedMin">速度下界，&gt;= 0。</param>
    /// <param name="speedMax">速度上界，&gt;= speedMin。</param>
    /// <param name="scale">视觉缩放，&gt; 0。</param>
    /// <returns>校验通过的值对象。</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">任一约束被违反。</exception>
    public static SpawnSettings Create(
        int targetAliveCount,
        float radius,
        float height,
        float lifetimeSeconds,
        float speedMin,
        float speedMax,
        float scale)
    {
        if (targetAliveCount < 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(targetAliveCount), "目标存活数不能为负。");
        }
        if (!(radius > 0f) || float.IsNaN(radius) || float.IsInfinity(radius))
        {
            throw new System.ArgumentOutOfRangeException(nameof(radius), "生成半径必须为正的有限值。");
        }
        if (!(lifetimeSeconds > 0f) || float.IsNaN(lifetimeSeconds) || float.IsInfinity(lifetimeSeconds))
        {
            throw new System.ArgumentOutOfRangeException(nameof(lifetimeSeconds), "寿命必须为正的有限值。");
        }
        if (!(scale > 0f) || float.IsNaN(scale) || float.IsInfinity(scale))
        {
            throw new System.ArgumentOutOfRangeException(nameof(scale), "缩放必须为正的有限值。");
        }
        if (float.IsNaN(speedMin) || speedMin < 0f)
        {
            throw new System.ArgumentOutOfRangeException(nameof(speedMin), "速度下界不能为负。");
        }
        if (speedMax < speedMin)
        {
            throw new System.ArgumentOutOfRangeException(nameof(speedMax), "速度上界不能小于下界。");
        }
        if (float.IsNaN(height) || float.IsInfinity(height))
        {
            throw new System.ArgumentOutOfRangeException(nameof(height), "生成高度必须为有限值。");
        }

        return new SpawnSettings
        {
            TargetAliveCount = targetAliveCount,
            Radius = radius,
            Height = height,
            LifetimeSeconds = lifetimeSeconds,
            SpeedMin = speedMin,
            SpeedMax = speedMax,
            Scale = scale,
        };
    }
}
