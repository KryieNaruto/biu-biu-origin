using System;
using System.Globalization;
using Godot;
using Game.Core;
using Game.Core.Randomness;
using Game.Spawn.Rooms;

namespace Game.Host.Debug;

/// <summary>
/// 地牢 2D 地图的**实时调参面板**：每根滑条一拖，地图立刻重画。
/// <para>
/// 设计目标（来自需求原话「给出方便我控制地牢的数值拖拉拽，让我可以实时拖拽查看地图生成结果」）：
/// </para>
/// <list type="number">
/// <item>全部参数都是滑条，不用回去改代码重编译；</item>
/// <item>拖动过程中节流重算（<see cref="RebuildDebounceMs"/>），松手立刻算一次最新值；</item>
/// <item>生成失败不崩、不弹框，只在状态行里说明原因，地图保留上一次成功的结果。</item>
/// </list>
/// <para>
/// 面板只负责「读滑条 → 拼 <see cref="DungeonConfig"/> + <see cref="DungeonMapConfig"/> → 调
/// <see cref="BranchDungeonPlanner"/> 与 <see cref="DungeonMapBuilder"/> → 把结果丢给
/// <see cref="DungeonMap2DView"/>」。所有地图逻辑都在 <c>scripts/spawn/rooms/</c> 里，这里不复制一份。
/// </para>
/// </summary>
public partial class DungeonMap2DPanel : PanelContainer
{
    /// <summary>拖动滑条时的重算节流（毫秒）。太小会卡，太大手感黏。</summary>
    private const int RebuildDebounceMs = 90; // arch-ok: 交互节流阈值，不是玩法数值

    private const string PanelTitle = "拖拽下面的滑条，地图会实时重画";

    /// <summary>起始房 / Boss 房比普通小房间多出的边长（格）。</summary>
    private const int SpecialRoomExtraSide = 2; // arch-ok: 面板派生「大房间」边长的结构性常量

    /// <summary>默认每步扩展房间数（与 DungeonOptions 常规配置一致）。</summary>
    private const int DefaultRoomsPerStep = 6; // arch-ok: 与 DungeonOptions 默认值对齐的结构性常量

    /// <summary>关键路径上出特殊房的默认概率（与 DungeonOptions 常规配置一致）。</summary>
    private const float DefaultSpecialOnCriticalPathChance = 0.2f; // arch-ok: 与 DungeonOptions 默认值对齐

    /// <summary>隐藏房默认可出现的最小深度。</summary>
    private const int DefaultMinHiddenDepth = 2; // arch-ok: 与 DungeonOptions 默认值对齐的结构性常量

    /// <summary>一格换算成米的默认值（与 DungeonOptions 常规配置一致）。</summary>
    private const float DefaultCellSizeMeters = 2f; // arch-ok: 与 DungeonOptions 默认值对齐

    /// <summary>百分比滑条 → 0~1 密度的换算分母。</summary>
    private const float PercentScale = 100f; // arch-ok: 百分比换算，不是玩法数值

    /// <summary>
    /// 隐藏房奖励是普通房的几倍。
    /// <para>隐藏房要玩家主动找、常常还要打穿秘密通道，回报必须更高，否则没人会去找。</para>
    /// </summary>
    private const int HiddenRewardMultiplier = 2; // arch-ok: 面板默认推导值，正式数值走 .tres/Excel

    /// <summary>一根滑条的取值范围。</summary>
    /// <param name="Min">最小值。</param>
    /// <param name="Max">最大值。</param>
    /// <param name="Step">步长。</param>
    /// <param name="Initial">初始值。</param>
    private readonly record struct Range(double Min, double Max, double Step, double Initial);

    /// <summary>
    /// 全部滑条的取值范围。这是 **UI 控件边界**（能拖到哪儿），不是玩法数值；
    /// 默认值取 <see cref="DungeonConfig"/> 的常规配置，方便一打开就看到一张典型的地图。
    /// </summary>
    /// <remarks>
    /// 整块登记 <c>// arch-ok:</c>：这些数字只决定「滑条拖到哪儿为止」，
    /// 不影响任何战斗/手感结算，改它们不会改变游戏平衡（规则4 管的是玩法数值）。
    /// 真正的玩法数值仍然全部从 <see cref="DungeonConfig"/> / <see cref="DungeonMapConfig"/> 进。
    /// </remarks>
    // arch-ok: 整块是 UI 控件取值范围（min/max/step/初始值），非玩法数值
    private static class SliderRanges
    {
        public static readonly Range Seed = new(0, 999999, 1, 0); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range GridWidth = new(5, 40, 1, 13); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range GridHeight = new(8, 60, 1, 30); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range RoomSide = new(2, 10, 1, 4); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range TargetRooms = new(3, 40, 1, 9); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range Tolerance = new(0, 6, 1, 2); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range HiddenRooms = new(0, 4, 1, 1); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range SpecialRooms = new(0, 8, 1, 1); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range MaxSpecials = new(0, 8, 1, 3); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range MinBossDepth = new(1, 12, 1, 3); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range LoopDistance = new(0, 10, 1, 2); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range ObstacleDensity = new(0, 40, 1, 8); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range ObstacleClearance = new(0, 3, 1, 1); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range CellPixels = new(6, 40, 1, 16); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range BuildingsPerRoom = new(0, 4, 1, 1); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range BuildingMaxSide = new(2, 5, 1, 3); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range RewardsPerRoom = new(0, 6, 1, 1); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range BreakablesPerRoom = new(0, 8, 1, 2); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range SolidsPerRoom = new(0, 6, 1, 1); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
        public static readonly Range PropClearance = new(0, 2, 1, 1); // arch-ok: 滑条取值范围（UI 控件边界），非玩法数值
    }

    private VBoxContainer _rows = null!;
    private Label _status = null!;
    private DungeonMap2DView _view = null!;

    private SpinBox _seedBox = null!;
    private SpinBox _gridWidth = null!;
    private SpinBox _gridHeight = null!;
    private SpinBox _roomSide = null!;
    private SpinBox _targetRooms = null!;
    private SpinBox _tolerance = null!;
    private SpinBox _hiddenRooms = null!;
    private SpinBox _minSpecials = null!;
    private SpinBox _maxSpecials = null!;
    private SpinBox _minBossDepth = null!;
    private SpinBox _loopDistance = null!;
    private SpinBox _obstacleDensity = null!;
    private SpinBox _obstacleClearance = null!;
    private SpinBox _cellPixels = null!;
    private SpinBox _buildingsPerRoom = null!;
    private SpinBox _buildingMaxSide = null!;
    private SpinBox _rewardsPerRoom = null!;
    private SpinBox _breakablesPerRoom = null!;
    private SpinBox _solidsPerRoom = null!;
    private SpinBox _propClearance = null!;

    private int _pendingRebuild = -1;
    private double _debounceLeft;
    private DungeonGeneration _generation = DungeonGeneration.None;

    /// <summary>面板挂上视图后由场景调用。</summary>
    /// <param name="view">要驱动的 2D 地图视图。</param>
    /// <param name="styles">配色表（可为 null，view 会退回内置默认）。</param>
    public void Bind(DungeonMap2DView view, DungeonRoomStyleSet? styles)
    {
        _view = view;
        if (styles is not null)
        {
            _view.SetStyles(styles);
        }
        RebuildNow();
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        BuildLayout();
        RebuildNow();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_pendingRebuild < 0)
        {
            return;
        }
        _debounceLeft -= delta * 1000.0;
        if (_debounceLeft <= 0.0)
        {
            _pendingRebuild = -1;
            RebuildNow();
        }
    }

    private void BuildLayout()
    {
        CustomMinimumSize = new Vector2(340f, 0f); // arch-ok: 面板最小宽度（UI 布局尺寸）
        ScrollContainer scroll = new ScrollContainer();
        scroll.SetAnchorsAndOffsetsPreset(Godot.Control.LayoutPreset.FullRect);
        AddChild(scroll);

        _rows = new VBoxContainer();
        _rows.SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill;
        _rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_rows);

        Label title = new Label { Text = PanelTitle };
        title.AddThemeFontSizeOverride("font_size", 12);
        _rows.AddChild(title);

        // 滑条取值范围是 **UI 控件边界**，不是玩法数值（规则4 管的是战斗/手感数值）。
        // 但写成字面量仍然难看，统一收进 SliderRanges 常量表，改范围只改一处。
        _seedBox = AddSpinBox("种子 seed", SliderRanges.Seed);
        _gridWidth = AddSpinBox("网格宽（格）", SliderRanges.GridWidth);
        _gridHeight = AddSpinBox("网格高（格）", SliderRanges.GridHeight);
        _roomSide = AddSpinBox("小房间边长（格）", SliderRanges.RoomSide);
        _targetRooms = AddSpinBox("目标房间数", SliderRanges.TargetRooms);
        _tolerance = AddSpinBox("房间数容差", SliderRanges.Tolerance);
        _hiddenRooms = AddSpinBox("隐藏房数量", SliderRanges.HiddenRooms);
        _minSpecials = AddSpinBox("特殊房下限", SliderRanges.SpecialRooms);
        _maxSpecials = AddSpinBox("特殊房上限", SliderRanges.MaxSpecials);
        _minBossDepth = AddSpinBox("Boss 最小深度", SliderRanges.MinBossDepth);
        _loopDistance = AddSpinBox("环路距离（格）", SliderRanges.LoopDistance);
        _obstacleDensity = AddSpinBox("障碍密度（%）", SliderRanges.ObstacleDensity);
        _obstacleClearance = AddSpinBox("障碍离墙格数", SliderRanges.ObstacleClearance);
        _cellPixels = AddSpinBox("格子像素大小", SliderRanges.CellPixels);

        Label propTitle = new Label { Text = "── 物件（建筑 / 奖励 / 可破坏 / 不可破坏）──" };
        propTitle.AddThemeFontSizeOverride("font_size", 11);
        _rows.AddChild(propTitle);
        _buildingsPerRoom = AddSpinBox("每房建筑数", SliderRanges.BuildingsPerRoom);
        _buildingMaxSide = AddSpinBox("建筑最大边长（格）", SliderRanges.BuildingMaxSide);
        _rewardsPerRoom = AddSpinBox("每房奖励数", SliderRanges.RewardsPerRoom);
        _breakablesPerRoom = AddSpinBox("每房可破坏数", SliderRanges.BreakablesPerRoom);
        _solidsPerRoom = AddSpinBox("每房不可破坏数", SliderRanges.SolidsPerRoom);
        _propClearance = AddSpinBox("物件离墙格数（尽量）", SliderRanges.PropClearance);

        HBoxContainer buttons = new HBoxContainer();
        _rows.AddChild(buttons);
        Button randomSeed = new Button { Text = "随机种子" };
        randomSeed.Pressed += OnRandomSeed;
        buttons.AddChild(randomSeed);
        Button regenerate = new Button { Text = "重新生成" };
        regenerate.Pressed += RebuildNow;
        buttons.AddChild(regenerate);
        Button export = new Button { Text = "导出字符画" };
        export.Pressed += OnExportArt;
        buttons.AddChild(export);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", 11);
        _rows.AddChild(_status);
    }

    private SpinBox AddSpinBox(string label, Range range)
    {
        HBoxContainer row = new HBoxContainer();
        _rows.AddChild(row);
        Label name = new Label { Text = label, CustomMinimumSize = new Vector2(150f, 0f) }; // arch-ok: 标签列宽（UI 布局尺寸）
        name.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(name);
        SpinBox box = new SpinBox
        {
            MinValue = range.Min,
            MaxValue = range.Max,
            Step = range.Step,
            Value = range.Initial,
            SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill,
        };
        box.ValueChanged += OnAnyValueChanged;
        row.AddChild(box);
        return box;
    }

    private void OnAnyValueChanged(double value)
    {
        _ = value;
        if (_cellPixels is not null && _view is not null
            && Math.Abs(_cellPixels.Value - _view.CellPixels) > 0.01)
        {
            _view.SetCellPixels((float)_cellPixels.Value);
        }
        _debounceLeft = RebuildDebounceMs;
        _pendingRebuild = 0;
    }

    private void OnRandomSeed()
    {
        // 用时间当种子源：这是「随机按钮」，不是生成逻辑本身，不影响种子复现。
        _seedBox.Value = (int)(Time.GetTicksUsec() % 1000000UL);
        RebuildNow();
    }

    private DungeonConfig BuildConfig()
    {
        int side = (int)_roomSide.Value;
        // 只覆盖滑条管着的字段；其余（RoomsPerStep / 特殊房概率 / 最小隐藏深度 / 格转米）
        // 保持 DungeonOptions 的常规配置，不给面板额外加不属于「地图形状」的旋钮。
        return new DungeonConfig
        {
            GridWidth = (int)_gridWidth.Value,
            GridHeight = (int)_gridHeight.Value,
            RoomSide = side,
            StartRoomSide = side + SpecialRoomExtraSide,
            BossRoomSide = side + SpecialRoomExtraSide,
            TargetRoomCount = (int)_targetRooms.Value,
            RoomCountTolerance = (int)_tolerance.Value,
            HiddenRoomCount = (int)_hiddenRooms.Value,
            MinSpecialRooms = (int)_minSpecials.Value,
            MaxSpecialRooms = (int)_maxSpecials.Value,
            MinBossDepth = (int)_minBossDepth.Value,
            RoomsPerStep = DefaultRoomsPerStep,
            SpecialOnCriticalPathChance = DefaultSpecialOnCriticalPathChance,
            MinHiddenDepth = DefaultMinHiddenDepth,
            LoopDistance = (int)_loopDistance.Value,
            CellSizeMeters = DefaultCellSizeMeters,
        };
    }

    private DungeonMapConfig BuildMapConfig()
    {
        // DungeonMapConfig 自带默认值，这里只覆盖面板管的那几个。
        return new DungeonMapConfig
        {
            ObstaclesEnabled = _obstacleDensity.Value > 0.0,
            ObstacleDensity = (float)_obstacleDensity.Value / PercentScale,
            ObstacleWallClearance = (int)_obstacleClearance.Value,
        };
    }

    /// <summary>
    /// 从「物件」那一组滑条拼配置。
    /// <para>
    /// 数量为 0 就等于「关掉这一类」，不必再加一个开关 —— 少一个控件就少一处不一致。
    /// 奖励按房间类别分档（普通房 / 隐藏房），面板只暴露一个「每房奖励数」，
    /// 隐藏房取它的两倍：隐藏房是玩家冒风险才找到的，回报必须更高。
    /// </para>
    /// </summary>
    private DungeonPropConfig BuildPropConfig()
    {
        int rewards = (int)_rewardsPerRoom.Value;
        return new DungeonPropConfig
        {
            BuildingsEnabled = _buildingsPerRoom.Value > 0.0,
            BuildingsPerRoom = (int)_buildingsPerRoom.Value,
            BuildingMaxSide = (int)_buildingMaxSide.Value,
            RewardsEnabled = rewards > 0,
            RewardsPerCombatRoom = rewards,
            RewardsPerHiddenRoom = rewards * HiddenRewardMultiplier,
            BreakablesEnabled = _breakablesPerRoom.Value > 0.0,
            BreakablesPerRoom = (int)_breakablesPerRoom.Value,
            SolidsEnabled = _solidsPerRoom.Value > 0.0,
            SolidsPerRoom = (int)_solidsPerRoom.Value,
            WallClearance = (int)_propClearance.Value,
        };
    }

    /// <summary>按当前滑条值重新规划 + 栅格化 + 重画，并把体检结果写进状态行。</summary>
    private void RebuildNow()
    {
        _pendingRebuild = -1;
        if (_view is null)
        {
            return;
        }
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int seed = (int)_seedBox.Value;
        DungeonConfig dungeonConfig = BuildConfig();
        DungeonMapConfig mapConfig = BuildMapConfig();
        DungeonLayout layout;
        try
        {
            // 每次重算都换一代 generation：旧句柄随之失效（与 DungeonLayoutProvider 的做法一致）。
            _generation = new DungeonGeneration(_generation.Value + 1);
            IRandomProvider random = new DeterministicRandom(seed);
            BranchDungeonPlanner planner = new BranchDungeonPlanner(random, BranchDungeonPlanner.DefaultCategories());
            layout = planner.Plan(dungeonConfig, seed, _generation);
        }
        catch (InvalidOperationException ex)
        {
            _status.Text = "生成失败：" + ex.Message + "（地图保持上一次结果）";
            return;
        }

        DungeonMap map = DungeonMapBuilder.Build(in layout, seed, mapConfig);
        // 物件（建筑/奖励/可破坏/不可破坏）在栅格化之后叠加：它们依赖「哪些格是地板、哪些格是门」，
        // 这些信息只有栅格化完才齐。Populate 不修改原图，返回一张带 Props 的新图。
        try
        {
            map = DungeonPropPlanner.Populate(in map, seed, BuildPropConfig());
        }
        catch (InvalidOperationException ex)
        {
            _status.Text = "物件配置非法：" + ex.Message + "（地图保持无物件）";
        }
        DungeonMapCheck.Report report = DungeonMapCheck.Inspect(in map);
        _view.SetMap(in map);
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 // arch-ok: 计时单位换算（秒→毫秒）
            / System.Diagnostics.Stopwatch.Frequency;
        string timing = string.Format(CultureInfo.InvariantCulture, " · {0:F1} ms", elapsedMs);
        _status.Text = (report.IsConnected
            ? string.Format(
                CultureInfo.InvariantCulture,
                "✓ 连通：{0} 房间 / {1} 可达格全部到达（seed {2}，{3}×{4}）",
                layout.RoomCount,
                report.ReachedCells,
                seed,
                map.Width,
                map.Height)
            : "⚠ 有问题：" + string.Join("；", report.Problems)) + timing;
    }

    private void OnExportArt()
    {
        if (_view is null || !_view.HasMap)
        {
            _status.Text = "还没有地图可以导出。";
            return;
        }
        string path = "user://dungeon_map_current.txt";
        using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            _status.Text = "导出失败：" + FileAccess.GetOpenError();
            return;
        }
        file.StoreString(_view.ExportArt());
        _status.Text = "已导出：" + ProjectSettings.GlobalizePath(path);
    }
}
