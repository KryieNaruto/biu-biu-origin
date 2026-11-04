using Godot;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 房型登记表的**一行数据**（Resource，可在 Inspector 里加行/改权重）。
/// <para>
/// 规划器只认它翻译出来的 <see cref="RoomCategorySpec"/>；新增一种房型 = 在 <c>.tres</c> 里加一行，
/// 规划器与内容层 **0 代码改动**（规则7）。
/// </para>
/// </summary>
[GlobalClass]
public partial class DungeonRoomSpecResource : Resource
{
    /// <summary>房间类别（拓扑职责）。</summary>
    [Export]
    public RoomCategory Category { get; set; } = RoomCategory.Combat;

    /// <summary>房间模板编号（内容 + 装饰的入口，&gt; 0）。</summary>
    [Export(PropertyHint.Range, "1,9999,1")]
    public int ArchetypeId { get; set; } = ArchetypeIdDefault;

    /// <summary>随机抽取权重（0 = 只能被显式指定）。</summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public int Weight { get; set; } = WeightDefault;

    /// <summary>是否进入随机池。</summary>
    [Export]
    public bool IncludeInRandom { get; set; } = true;

    /// <summary>是否安全房（不刷怪、门默认开放）。</summary>
    [Export]
    public bool IsSafe { get; set; }

    /// <summary>是否必须挂在关键路径上（Boss）。</summary>
    [Export]
    public bool RequiresCriticalPath { get; set; }

    /// <summary>模板编号默认值。</summary>
    public const int ArchetypeIdDefault = 1;  // arch-ok: 无效占位默认值（数据行必须在 .tres 里改），不是玩法数值

    /// <summary>权重默认值。</summary>
    public const int WeightDefault = 1;  // arch-ok: 无效占位默认值（数据行必须在 .tres 里改），不是玩法数值

    /// <summary>翻译成纯 C# 值。</summary>
    public RoomCategorySpec ToSpec()
    {
        return new RoomCategorySpec
        {
            Category = Category,
            Archetype = new RoomArchetypeId(ArchetypeId),
            Weight = Weight,
            IncludeInRandom = IncludeInRandom,
            IsSafe = IsSafe,
            RequiresCriticalPath = RequiresCriticalPath,
        };
    }
}

/// <summary>
/// 装饰登记表的**一行数据**。与 <see cref="DungeonRoomSpecResource"/> 同一范式：
/// 新增一件装饰 = 加一行，装饰布局器 0 代码改动。
/// </summary>
[GlobalClass]
public partial class DungeonDecorSpecResource : Resource
{
    /// <summary>装饰编号（&gt; 0）。</summary>
    [Export(PropertyHint.Range, "1,9999,1")]
    public int DecorId { get; set; } = 1;

    /// <summary>摆放层次（落地 / 贴墙 / 吊顶）。</summary>
    [Export]
    public DecorPlacementLayer Layer { get; set; } = DecorPlacementLayer.Ground;

    /// <summary>抽取权重（0 = 只被显式引用）。</summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public int Weight { get; set; } = 1;

    /// <summary>允许出现的房间类别（勾选式掩码）。</summary>
    [Export]
    public RoomCategoryFlags CategoryMask { get; set; } = RoomCategoryFlags.All;

    /// <summary>水平安全半径（格）：与刷怪点/门/其它装饰的最小间距。</summary>
    [Export(PropertyHint.Range, "0,2,0.05")]
    public float Clearance { get; set; } = ClearanceDefault;

    /// <summary>推荐缩放下限。</summary>
    [Export(PropertyHint.Range, "0.05,10,0.01")]
    public float ScaleMin { get; set; } = ScaleMinDefault;

    /// <summary>推荐缩放上限。</summary>
    [Export(PropertyHint.Range, "0.05,10,0.01")]
    public float ScaleMax { get; set; } = ScaleMaxDefault;

    /// <summary>安全半径默认值（格）。</summary>
    public const float ClearanceDefault = 0.25f;  // arch-ok: 数据行占位默认值（.tres 覆盖），不是玩法数值

    /// <summary>缩放下限默认值。</summary>
    public const float ScaleMinDefault = 0.9f;  // arch-ok: 数据行占位默认值（.tres 覆盖），不是玩法数值

    /// <summary>缩放上限默认值。</summary>
    public const float ScaleMaxDefault = 1.1f;  // arch-ok: 数据行占位默认值（.tres 覆盖），不是玩法数值

    /// <summary>翻译成纯 C# 值。</summary>
    public DecorArchetypeSpec ToSpec()
    {
        return new DecorArchetypeSpec
        {
            Id = new DecorArchetypeId(DecorId),
            Layer = Layer,
            Weight = Weight,
            CategoryMask = (uint)CategoryMask,
            Clearance = Clearance,
            ScaleMin = ScaleMin,
            ScaleMax = ScaleMax,
        };
    }
}

/// <summary>房间类别勾选掩码（Inspector 友好的 Flags 枚举，位与 <see cref="RoomCategory"/> 一致）。</summary>
[System.Flags]
public enum RoomCategoryFlags : uint
{
    /// <summary>无。</summary>
    None = 0,

    /// <summary>起始房。</summary>
    Start = 1u << (int)RoomCategory.Start,

    /// <summary>普通战斗房。</summary>
    Combat = 1u << (int)RoomCategory.Combat,

    /// <summary>特殊功能房。</summary>
    Special = 1u << (int)RoomCategory.Special,

    /// <summary>隐藏房。</summary>
    Hidden = 1u << (int)RoomCategory.Hidden,

    /// <summary>原型测试房。</summary>
    Prototype = 1u << (int)RoomCategory.Prototype,

    /// <summary>Boss 房。</summary>
    Boss = 1u << (int)RoomCategory.Boss,

    /// <summary>安全房。</summary>
    Safe = 1u << (int)RoomCategory.Safe,

    /// <summary>全部房型。</summary>
    All = Start | Combat | Special | Hidden | Prototype | Boss | Safe,
}
