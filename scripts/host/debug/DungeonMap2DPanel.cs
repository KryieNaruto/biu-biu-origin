using System;
using System.Globalization;
using Godot;
using Game.Core;
using Game.Core.Randomness;
using Game.Host.Ui;
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
    /// <summary>
    /// 拖动滑条时的重算节流（毫秒）。太小会卡，太大手感黏。
    /// <para>
    /// 实测重算一次平均 <b>0.57 ms</b>（稳态最差 8.92 ms，仍在一帧 16.6 ms 以内），
    /// 所以这里可以放宽到「几乎每帧都能算」。原先 90 ms 是给 SpinBox 点箭头定的，
    /// 那个场景一次只跳一格，慢了无所谓；换成**连续拖动**后 90 ms 会明显发黏，故收到 30 ms。
    /// </para>
    /// </summary>
    private const int RebuildDebounceMs = 30; // arch-ok: 交互节流阈值，不是玩法数值

    private const string PanelTitle = "左右拖动滑块，地图会实时重画";

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

    /// <summary>滑条行里「名字」列的宽度；够放下最长的那条中文标签。</summary>
    private const float LabelColumnWidth = DebugUiStyle.LabelColumnWidth;

    /// <summary>滑条行里「当前值」列的宽度；固定宽度避免数字位数变化时整列跳动。</summary>
    private const float ValueColumnWidth = DebugUiStyle.ValueColumnWidth;

    /// <summary>滑条行的控件间距。</summary>
    private const int RowSeparation = DebugUiStyle.RowSeparation;

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

    private HSlider _seedBox = null!;
    private HSlider _gridWidth = null!;
    private HSlider _gridHeight = null!;
    private HSlider _roomSide = null!;
    private HSlider _targetRooms = null!;
    private HSlider _tolerance = null!;
    private HSlider _hiddenRooms = null!;
    private HSlider _minSpecials = null!;
    private HSlider _maxSpecials = null!;
    private HSlider _minBossDepth = null!;
    private HSlider _loopDistance = null!;
    private HSlider _obstacleDensity = null!;
    private HSlider _obstacleClearance = null!;
    private HSlider _cellPixels = null!;
    private HSlider _buildingsPerRoom = null!;
    private HSlider _buildingMaxSide = null!;
    private HSlider _rewardsPerRoom = null!;
    private HSlider _breakablesPerRoom = null!;
    private HSlider _solidsPerRoom = null!;
    private HSlider _propClearance = null!;

    private int _pendingRebuild = -1;
    private double _debounceLeft;
    private DungeonGeneration _generation = DungeonGeneration.None;

    /// <summary>面板挂上视图后由场景调用。</summary>
    /// <param name="view">要驱动的 2D 地图视图。</param>
    /// <param name="styles">配色表（可为 null，view 会退回内置默认）。</param>
    public void Bind(DungeonMap2DView view, DungeonRoomStyleSet? styles)
    {
        _view = view;
        _view.SetStyles(styles);
        // 图例画在视图里，配色换完要让它按新配色重算列宽并重画（否则色块停在内置默认色）。
        _view.QueueRedraw();
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
        FitMapToView();
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
        CustomMinimumSize = new Vector2(DebugUiStyle.PanelMinWidth, 0f);
        ScrollContainer scroll = new ScrollContainer();
        scroll.SetAnchorsAndOffsetsPreset(Godot.Control.LayoutPreset.FullRect);
        AddChild(scroll);

        _rows = new VBoxContainer();
        _rows.SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill;
        _rows.AddThemeConstantOverride("separation", DebugUiStyle.SectionSeparation);
        scroll.AddChild(_rows);
        // 滚动条也是在运行时才出现的，只能通过 Theme 加粗（见 DebugUiStyle.ScrollBarWidth）。
        scroll.AddThemeConstantOverride("scroll_deadzone", 4); // arch-ok: 滚动死区（输入手感，UI）

        Label title = new Label { Text = PanelTitle };
        // 字号/颜色一律走 Theme（DebugUiStyle 挂在场景根上向下继承），
        // 这里只补「标题比正文大」这一条差异——它属于语义层级，不是随手改的魔法数。
        title.AddThemeFontSizeOverride("font_size", DebugUiStyle.FontTitle);
        title.AddThemeColorOverride("font_color", DebugUiStyle.TextTitle);
        _rows.AddChild(title);

        // 滑条取值范围是 **UI 控件边界**，不是玩法数值（规则4 管的是战斗/手感数值）。
        // 但写成字面量仍然难看，统一收进 SliderRanges 常量表，改范围只改一处。
        _seedBox = AddSlider("种子 seed", SliderRanges.Seed, out _);
        _gridWidth = AddSlider("网格宽（格）", SliderRanges.GridWidth, out _);
        _gridHeight = AddSlider("网格高（格）", SliderRanges.GridHeight, out _);
        _roomSide = AddSlider("小房间边长（格）", SliderRanges.RoomSide, out _);
        _targetRooms = AddSlider("目标房间数", SliderRanges.TargetRooms, out _);
        _tolerance = AddSlider("房间数容差", SliderRanges.Tolerance, out _);
        _hiddenRooms = AddSlider("隐藏房数量", SliderRanges.HiddenRooms, out _);
        _minSpecials = AddSlider("特殊房下限", SliderRanges.SpecialRooms, out _);
        _maxSpecials = AddSlider("特殊房上限", SliderRanges.MaxSpecials, out _);
        _minBossDepth = AddSlider("Boss 最小深度", SliderRanges.MinBossDepth, out _);
        _loopDistance = AddSlider("环路距离（格）", SliderRanges.LoopDistance, out _);
        _obstacleDensity = AddSlider("障碍密度（%）", SliderRanges.ObstacleDensity, out _);
        _obstacleClearance = AddSlider("障碍离墙格数", SliderRanges.ObstacleClearance, out _);
        _cellPixels = AddSlider("格子像素大小", SliderRanges.CellPixels, out _);

        Label propTitle = new Label { Text = "── 物件（建筑 / 奖励 / 可破坏 / 不可破坏）──" };
        propTitle.AddThemeFontSizeOverride("font_size", DebugUiStyle.FontSmallTitle);
        propTitle.AddThemeColorOverride("font_color", DebugUiStyle.TextSection);
        _rows.AddChild(propTitle);
        _buildingsPerRoom = AddSlider("每房建筑数", SliderRanges.BuildingsPerRoom, out _);
        _buildingMaxSide = AddSlider("建筑最大边长（格）", SliderRanges.BuildingMaxSide, out _);
        _rewardsPerRoom = AddSlider("每房奖励数", SliderRanges.RewardsPerRoom, out _);
        _breakablesPerRoom = AddSlider("每房可破坏数", SliderRanges.BreakablesPerRoom, out _);
        _solidsPerRoom = AddSlider("每房不可破坏数", SliderRanges.SolidsPerRoom, out _);
        _propClearance = AddSlider("物件离墙格数（尽量）", SliderRanges.PropClearance, out _);

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
        _status.AddThemeColorOverride("font_color", DebugUiStyle.TextStatus);
        _rows.AddChild(_status);
    }

    /// <summary>
    /// 加一行「名字 + 可左右拖动的滑块 + 当前值」。
    /// <para>
    /// **为什么用 <see cref="HSlider"/> 而不是 <see cref="SpinBox"/>**：用户要的是「数值左右拖动」，
    /// SpinBox 只能点小箭头或键盘输入，拖不了；而且调参时眼睛在图上、手在滑条上，
    /// 点箭头这种「对准一个小目标」的操作会频繁打断看图。
    /// </para>
    /// <para>
    /// 滑块本身不显示数字，所以旁边固定一个等宽数值标签：既能拖，又随时看得见拖到了多少。
    /// 标签用 <see cref="Godot.Control.CustomMinimumSize"/> 固定宽度，拖动时不会因为数字位数变化而让整列跳动。
    /// </para>
    /// </summary>
    private HSlider AddSlider(string label, Range range, out Label valueLabel)
    {
        HBoxContainer row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", RowSeparation);
        _rows.AddChild(row);

        Label name = new Label { Text = label, CustomMinimumSize = new Vector2(LabelColumnWidth, 0f) };
        row.AddChild(name);

        HSlider slider = new HSlider
        {
            MinValue = range.Min,
            MaxValue = range.Max,
            Step = range.Step,
            Value = range.Initial,
            SizeFlagsHorizontal = Godot.Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Godot.Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(slider);

        Label value = new Label
        {
            Text = FormatValue(range.Initial),
            CustomMinimumSize = new Vector2(ValueColumnWidth, 0f),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        value.AddThemeColorOverride("font_color", DebugUiStyle.TextValue);
        row.AddChild(value);

        slider.ValueChanged += OnAnyValueChanged;
        // 拖动过程中实时刷新数字：ValueChanged 只在值真的变了才触发，够用且不会每帧刷标签。
        slider.ValueChanged += v => value.Text = FormatValue(v);
        valueLabel = value;
        return slider;
    }

    /// <summary>数值显示格式：整数不带小数点，小数保留一位（滑条步长最小是 1，这里主要防浮点尾数）。</summary>
    private static string FormatValue(double value)
    {
        double rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 0.0005) // arch-ok: 浮点比较容差（显示层，非玩法数值）
        {
            return rounded.ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void OnAnyValueChanged(double value)
    {
        _ = value;
        if (_cellPixels is not null && _view is not null)
        {
            // 滑条动过 = 用户想手动定尺寸，交出控制权。
            // 只有值**真的**变了才算，避免任何别的滑条一动就误判。
            // 注意 FitMapToView 回填滑条时走的是 SetValueNoSignal，不会触发这里，
            // 所以「自动铺满」与「手动指定」两种模式不会互相打脸。
            if (Math.Abs(_cellPixels.Value - _view.CellPixels) > 0.01)
            {
                _view.AutoFit = false;
                _view.SetCellPixels((float)_cellPixels.Value);
            }
        }
        _debounceLeft = RebuildDebounceMs;
        _pendingRebuild = 0;
    }

    /// <summary>
    /// 把地图（含右侧图例列）缩放到铺满可视区，并让滑条显示实际生效的格子像素。
    /// <para>
    /// 必须每帧调一次：视图只在 <see cref="DungeonMap2DView.AutoFit"/> 开着时才会自己缩放，
    /// 而调过一次「格子像素」滑条之后 AutoFit 就关掉了（交还手动控制），
    /// 之后拖窗口图就再也不跟着变——用户反馈的「窗口拉大图不变」正是这个口径。
    /// 这里每帧重算一次（两次除法 + 一次比较），代价可以忽略。
    /// </para>
    /// </summary>
    private void FitMapToView()
    {
        if (_view is null)
        {
            return;
        }
        Godot.Control? host = _view.GetParent() as Godot.Control;
        if (host is null)
        {
            return;
        }
        Vector2 available = host.Size;
        if (available.X <= 0f || available.Y <= 0f)
        {
            return;
        }
        // 先问一次是否「正好装得下」：装得下就不动，装不下（窗口变小 / 手动像素超出可视区）
        // 才收回控制权重新铺满。这样窗口变大时手动像素保持不动，窗口变小也不会把图切掉。
        if (_view.AutoFit || !_view.FitsWithin(available))
        {
            _view.AutoFit = true;
            _view.FitToViewport(available);
        }
        if (_cellPixels is not null)
        {
            _cellPixels.SetValueNoSignal(Math.Clamp( // arch-ok: 显示层回填，与滑条范围一致
                _view.CellPixels, _cellPixels.MinValue, _cellPixels.MaxValue));
        }
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
        FitMapToView();
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
