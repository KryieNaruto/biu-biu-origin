using System;

namespace Game.Spawn.Rooms;

/// <summary>
/// 把 <see cref="DungeonLayout"/>（房间图）**栅格化成一张 2D 字符地图**的全部可调参数。
/// <para>
/// 数值全部来自这里（规则4 / GS005）：宿主从 <c>DungeonOptions</c>（可在 Inspector 调）填，
/// 测试里直接 <c>new DungeonMapConfig { ... }</c>。<see cref="DungeonMapBuilder"/> 一行都不用改。
/// </para>
/// <para>
/// <b>几何约定</b>（与 <see cref="RoomPlan.Cell"/> 一致）：房间的格子坐标就是<b>地图格坐标**
/// （<c>Cell</c> 为左上角、占 <c>[X, X+Side) × [Z, Z+Side)</c>）。房间与房间之间<b>不留空隙**
/// ——贴边的两间房共用一列/一行：这列/行是「墙 + 门」那一行。
/// </para>
/// <para>
/// 为什么「不留空隙」：用户要的是「正方形代表场地」的直接读图。留 1 格缝隙会让整张图变成
/// 「房间A、墙、缝、墙、房间B」，白盒质感且数不清门开在哪；共用一行时，
/// 那一行从左到右就是「墙 墙 门 墙 墙」，门的位置一目了然。
/// </para>
/// </summary>
public sealed class DungeonMapConfig
{
    /// <summary>左右/上下留白（格）：让地图边缘的墙不至于贴死字符串边界。</summary>
    public int Margin { get; init; } = MarginDefault;

    /// <summary>是否在非关键路径的房间内撒障碍（<see cref="DungeonCellKind.Obstacle"/>）。</summary>
    public bool ObstaclesEnabled { get; init; } = ObstaclesEnabledDefault;

    /// <summary>障碍覆盖率（0 = 不撒，1 = 铺满；按房间内可用格数折算）。</summary>
    public float ObstacleDensity { get; init; } = ObstacleDensityDefault;

    /// <summary>起点房（<see cref="RoomCategory.Start"/>）的障碍系数（乘在密度上；0 = 起点房永远空）。</summary>
    public float StartRoomObstacleScale { get; init; } = StartRoomObstacleScaleDefault;

    /// <summary>Boss 房障碍系数。</summary>
    public float BossRoomObstacleScale { get; init; } = BossRoomObstacleScaleDefault;

    /// <summary>障碍格离墙/离门至少要隔几格（1 = 紧贴墙也允许；2 = 留一圈走道）。</summary>
    public int ObstacleWallClearance { get; init; } = ObstacleWallClearanceDefault;

    /// <summary>起终点之间做连通性判定时，「保险丝」通道的最长绕路步数上限（0 = 不修保险丝，只报告）。</summary>
    public int FuseMaxExtraSteps { get; init; } = FuseMaxExtraStepsDefault;

    /// <summary>路线统计最多报告几条（诊断输出用）。</summary>
    public int ReportedRouteCount { get; init; } = ReportedRouteCountDefault;

    /// <summary>留白默认值。</summary>
    public const int MarginDefault = 0; // arch-ok: 排版默认值，由 .tres 覆盖

    /// <summary>障碍默认开关。</summary>
    public const bool ObstaclesEnabledDefault = true;

    /// <summary>障碍密度默认值。</summary>
    public const float ObstacleDensityDefault = 0.08f; // arch-ok: 表现/关卡手感默认值，由 .tres 覆盖

    /// <summary>起点房障碍系数默认值。</summary>
    public const float StartRoomObstacleScaleDefault = 0f;

    /// <summary>Boss 房障碍系数默认值。</summary>
    public const float BossRoomObstacleScaleDefault = 0.5f; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>障碍离墙间距默认值（格）。</summary>
    public const int ObstacleWallClearanceDefault = 1; // arch-ok: 走道宽度默认值，由 .tres 覆盖

    /// <summary>保险丝绕路上限默认值。</summary>
    public const int FuseMaxExtraStepsDefault = 0;

    /// <summary>报告路线上限默认值。</summary>
    public const int ReportedRouteCountDefault = 4; // arch-ok: 报告条数（纯诊断输出）

    /// <summary>校验：不自洽的参数在构建期变成一条明确的中文异常。</summary>
    /// <exception cref="InvalidOperationException">任一约束不成立。</exception>
    public void Validate()
    {
        Require(Margin >= 0, nameof(Margin), Margin);
        Require(ObstacleDensity >= 0f && ObstacleDensity <= 1f, nameof(ObstacleDensity), ObstacleDensity);
        Require(StartRoomObstacleScale >= 0f, nameof(StartRoomObstacleScale), StartRoomObstacleScale);
        Require(BossRoomObstacleScale >= 0f, nameof(BossRoomObstacleScale), BossRoomObstacleScale);
        Require(ObstacleWallClearance >= 0, nameof(ObstacleWallClearance), ObstacleWallClearance);
        Require(FuseMaxExtraSteps >= 0, nameof(FuseMaxExtraSteps), FuseMaxExtraSteps);
        Require(ReportedRouteCount >= 0, nameof(ReportedRouteCount), ReportedRouteCount);
    }

    /// <summary>深拷贝并替换若干字段（编辑器面板「拖动 → 重算」时用；改一个值不必重写全部字段）。</summary>
    /// <param name="margin">留白（null = 不变）。</param>
    /// <param name="obstaclesEnabled">障碍开关（null = 不变）。</param>
    /// <param name="obstacleDensity">障碍密度（null = 不变）。</param>
    /// <param name="startRoomObstacleScale">起点房系数（null = 不变）。</param>
    /// <param name="bossRoomObstacleScale">Boss 房系数（null = 不变）。</param>
    /// <param name="obstacleWallClearance">离墙间距（null = 不变）。</param>
    /// <param name="fuseMaxExtraSteps">保险丝绕路上限（null = 不变）。</param>
    /// <param name="reportedRouteCount">报告路线上限（null = 不变）。</param>
    /// <returns>新配置。</returns>
    public DungeonMapConfig With(
        int? margin = null,
        bool? obstaclesEnabled = null,
        float? obstacleDensity = null,
        float? startRoomObstacleScale = null,
        float? bossRoomObstacleScale = null,
        int? obstacleWallClearance = null,
        int? fuseMaxExtraSteps = null,
        int? reportedRouteCount = null)
    {
        return new DungeonMapConfig
        {
            Margin = margin ?? Margin,
            ObstaclesEnabled = obstaclesEnabled ?? ObstaclesEnabled,
            ObstacleDensity = obstacleDensity ?? ObstacleDensity,
            StartRoomObstacleScale = startRoomObstacleScale ?? StartRoomObstacleScale,
            BossRoomObstacleScale = bossRoomObstacleScale ?? BossRoomObstacleScale,
            ObstacleWallClearance = obstacleWallClearance ?? ObstacleWallClearance,
            FuseMaxExtraSteps = fuseMaxExtraSteps ?? FuseMaxExtraSteps,
            ReportedRouteCount = reportedRouteCount ?? ReportedRouteCount,
        };
    }

    private static void Require(bool condition, string field, float actual)
    {
        if (condition)
        {
            return;
        }
        throw new InvalidOperationException(
            "地牢地图配置非法：DungeonMapConfig." + field + " 不满足约束，实际值 = "
            + actual.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "。");
    }

    private static void Require(bool condition, string field, int actual) => Require(condition, field, (float)actual);
}
