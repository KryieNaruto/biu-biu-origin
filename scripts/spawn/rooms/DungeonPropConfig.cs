using System;

namespace Game.Spawn.Rooms;

/// <summary>
/// 往地牢里摆**建筑 / 奖励 / 可破坏物体 / 不可破坏物体**的全部可调参数。
/// <para>
/// 与 <see cref="DungeonMapConfig"/> 分开的理由（规则7）：栅格化决定「地形长什么样」，
/// 这里决定「地形上摆什么」。两者的调参者不同（地形是关卡结构，内容是关卡手感），
/// 合在一起会让「我想多摆几个木箱」也得重跑整张地形的测试。
/// </para>
/// <para>
/// <b>几何约定</b>：所有位置都是**画布格坐标**（与 <see cref="DungeonMap"/> 同一套）。
/// 物件只能占用 <see cref="DungeonCellKind.Floor"/> 格，不许压墙/门/走廊。
/// </para>
/// </summary>
public sealed class DungeonPropConfig
{
    /// <summary>是否摆放建筑（多格占地的大件）。</summary>
    public bool BuildingsEnabled { get; init; } = BuildingsEnabledDefault;

    /// <summary>每间房最多尝试摆几栋建筑（房间太小会摆不下，实际更少）。</summary>
    public int BuildingsPerRoom { get; init; } = BuildingsPerRoomDefault;

    /// <summary>建筑的最大边长（格）。必须 ≥ 2 才可能多格占地。</summary>
    public int BuildingMaxSide { get; init; } = BuildingMaxSideDefault;

    /// <summary>
    /// 房间至少要多大才允许放建筑（取宽深较小值）。
    /// <para>
    /// <b>为什么要有这个门槛</b>：3×3 的小房间塞进一个 2×2 建筑后只剩 5 格，
    /// 扣掉几扇门的门口通道就真的堵死了 —— 连通性检查会正确拒绝，但那样「建筑能不能出现」
    /// 就变成了房间数和门数的副产品，调参时完全不可预期。
    /// 与其让检查事后拒绝，不如在挑选房间时就把「装不下」的房排除掉。
    /// </para>
    /// </summary>
    public int BuildingMinRoomSide { get; init; } = BuildingMinRoomSideDefault;

    /// <summary>是否摆放奖励。</summary>
    public bool RewardsEnabled { get; init; } = RewardsEnabledDefault;

    /// <summary>战斗房的奖励个数（隐藏房/Boss 房另有保底，见 <see cref="DungeonPropPlanner"/>）。</summary>
    public int RewardsPerCombatRoom { get; init; } = RewardsPerCombatRoomDefault;

    /// <summary>隐藏房奖励个数（隐藏房是「翻箱倒柜」的地方，给得更多）。</summary>
    public int RewardsPerHiddenRoom { get; init; } = RewardsPerHiddenRoomDefault;

    /// <summary>是否摆放可破坏物体。</summary>
    public bool BreakablesEnabled { get; init; } = BreakablesEnabledDefault;

    /// <summary>每间房可破坏物体个数上限（实际受房间大小限制）。</summary>
    public int BreakablesPerRoom { get; init; } = BreakablesPerRoomDefault;

    /// <summary>是否摆放不可破坏物体。</summary>
    public bool SolidsEnabled { get; init; } = SolidsEnabledDefault;

    /// <summary>每间房不可破坏物体个数上限。</summary>
    public int SolidsPerRoom { get; init; } = SolidsPerRoomDefault;

    /// <summary>物件离房间墙壁至少要隔几格（0 = 可以贴墙）。</summary>
    public int WallClearance { get; init; } = WallClearanceDefault;

    /// <summary>物件离门至少要隔几格：保证门口永远是通的。</summary>
    public int DoorClearance { get; init; } = DoorClearanceDefault;

    /// <summary>起点房是否摆放物件（默认不摆，出生点周围保持清爽）。</summary>
    public bool PlaceInStartRoom { get; init; } = PlaceInStartRoomDefault;

    /// <summary>建筑默认开关。</summary>
    public const bool BuildingsEnabledDefault = true;

    /// <summary>每间房建筑尝试数默认值。</summary>
    public const int BuildingsPerRoomDefault = 1; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>建筑最大边长默认值（格）。</summary>
    public const int BuildingMaxSideDefault = 3; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>允许放建筑的最小房间边长默认值（格）。</summary>
    public const int BuildingMinRoomSideDefault = 4; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>奖励默认开关。</summary>
    public const bool RewardsEnabledDefault = true;

    /// <summary>战斗房奖励数默认值。</summary>
    public const int RewardsPerCombatRoomDefault = 1; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>隐藏房奖励数默认值。</summary>
    public const int RewardsPerHiddenRoomDefault = 2; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>可破坏物体默认开关。</summary>
    public const bool BreakablesEnabledDefault = true;

    /// <summary>每间房可破坏物体默认上限。</summary>
    public const int BreakablesPerRoomDefault = 2; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>不可破坏物体默认开关。</summary>
    public const bool SolidsEnabledDefault = true;

    /// <summary>每间房不可破坏物体默认上限。</summary>
    public const int SolidsPerRoomDefault = 1; // arch-ok: 关卡手感默认值，由 .tres 覆盖

    /// <summary>离墙间距默认值（格）。</summary>
    public const int WallClearanceDefault = 1; // arch-ok: 走道宽度默认值，由 .tres 覆盖

    /// <summary>离门间距默认值（格）。</summary>
    public const int DoorClearanceDefault = 1; // arch-ok: 走道宽度默认值，由 .tres 覆盖

    /// <summary>起点房是否摆放物件默认值。</summary>
    public const bool PlaceInStartRoomDefault = false;

    /// <summary>校验：不自洽的参数在构建期变成一条明确的中文异常。</summary>
    /// <exception cref="InvalidOperationException">任一约束不成立。</exception>
    public void Validate()
    {
        Require(BuildingsPerRoom >= 0, nameof(BuildingsPerRoom));
        Require(RewardsPerCombatRoom >= 0, nameof(RewardsPerCombatRoom));
        Require(RewardsPerHiddenRoom >= 0, nameof(RewardsPerHiddenRoom));
        Require(BreakablesPerRoom >= 0, nameof(BreakablesPerRoom));
        Require(SolidsPerRoom >= 0, nameof(SolidsPerRoom));
        Require(WallClearance >= 0, nameof(WallClearance));
        Require(DoorClearance >= 0, nameof(DoorClearance));
        Require(BuildingMinRoomSide >= 0, nameof(BuildingMinRoomSide));
        Require(
            !BuildingsEnabled || BuildingMaxSide >= MinBuildingSide,
            "BuildingMaxSide 至少要为 " + MinBuildingSide.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "（1 格的不是建筑，是障碍）");
    }

    /// <summary>建筑的最小边长：小于它就算普通障碍，不该走建筑这条路。</summary>
    public const int MinBuildingSide = 2; // arch-ok: 结构性下限（1 格=障碍不是建筑），不是可调玩法数值

    /// <summary>逐项覆盖后的新配置（不可变，便于面板拼装）。</summary>
    /// <param name="buildingsEnabled">是否摆建筑；null = 保持原值。</param>
    /// <param name="buildingsPerRoom">每间房建筑尝试数；null = 保持原值。</param>
    /// <param name="buildingMaxSide">建筑最大边长；null = 保持原值。</param>
    /// <param name="buildingMinRoomSide">允许放建筑的最小房间边长；null = 保持原值。</param>
    /// <param name="rewardsEnabled">是否摆奖励；null = 保持原值。</param>
    /// <param name="rewardsPerCombatRoom">战斗房奖励数；null = 保持原值。</param>
    /// <param name="rewardsPerHiddenRoom">隐藏房奖励数；null = 保持原值。</param>
    /// <param name="breakablesEnabled">是否摆可破坏物体；null = 保持原值。</param>
    /// <param name="breakablesPerRoom">每间房可破坏物体数；null = 保持原值。</param>
    /// <param name="solidsEnabled">是否摆不可破坏物体；null = 保持原值。</param>
    /// <param name="solidsPerRoom">每间房不可破坏物体数；null = 保持原值。</param>
    /// <param name="wallClearance">离墙间距；null = 保持原值。</param>
    /// <param name="doorClearance">离门间距；null = 保持原值。</param>
    /// <param name="placeInStartRoom">起点房是否摆物件；null = 保持原值。</param>
    /// <returns>覆盖后的新配置实例。</returns>
    public DungeonPropConfig With(
        bool? buildingsEnabled = null,
        int? buildingsPerRoom = null,
        int? buildingMaxSide = null,
        int? buildingMinRoomSide = null,
        bool? rewardsEnabled = null,
        int? rewardsPerCombatRoom = null,
        int? rewardsPerHiddenRoom = null,
        bool? breakablesEnabled = null,
        int? breakablesPerRoom = null,
        bool? solidsEnabled = null,
        int? solidsPerRoom = null,
        int? wallClearance = null,
        int? doorClearance = null,
        bool? placeInStartRoom = null)
    {
        return new DungeonPropConfig
        {
            BuildingsEnabled = buildingsEnabled ?? BuildingsEnabled,
            BuildingsPerRoom = buildingsPerRoom ?? BuildingsPerRoom,
            BuildingMaxSide = buildingMaxSide ?? BuildingMaxSide,
            BuildingMinRoomSide = buildingMinRoomSide ?? BuildingMinRoomSide,
            RewardsEnabled = rewardsEnabled ?? RewardsEnabled,
            RewardsPerCombatRoom = rewardsPerCombatRoom ?? RewardsPerCombatRoom,
            RewardsPerHiddenRoom = rewardsPerHiddenRoom ?? RewardsPerHiddenRoom,
            BreakablesEnabled = breakablesEnabled ?? BreakablesEnabled,
            BreakablesPerRoom = breakablesPerRoom ?? BreakablesPerRoom,
            SolidsEnabled = solidsEnabled ?? SolidsEnabled,
            SolidsPerRoom = solidsPerRoom ?? SolidsPerRoom,
            WallClearance = wallClearance ?? WallClearance,
            DoorClearance = doorClearance ?? DoorClearance,
            PlaceInStartRoom = placeInStartRoom ?? PlaceInStartRoom,
        };
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "DungeonPropConfig." + name + " 必须 ≥ 0（当前配置不自洽）。检查 DungeonOptions / 面板滑条 / 测试里的 new DungeonPropConfig{}。");
        }
    }
}
