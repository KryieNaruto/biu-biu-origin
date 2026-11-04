using System;
using Godot;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 「一类房间长什么样」的配色表：地面色、墙面色、是否在中心摆建筑装饰。
/// <para>
/// <b>为什么颜色在宿主层而不是业务层</b>：颜色是纯表现（J2：组件不得携带材质语义），
/// 由宿主把 <see cref="RoomPlan.Category"/> 翻译成材质。类别枚举 → 配色行的映射只在这里出现一次，
/// 新增房型 = 加一行配色（规则7），<see cref="DungeonRoomView"/> 不改。
/// </para>
/// <para>
/// 颜色本身以 <see cref="Color"/> 字面量登记为「数据行默认值」，并全员带 <c>// arch-ok</c>：
/// 它们是美术调色数据，可在 <c>.tres</c> 覆盖（规则4 的「文件 + 可视化」两条路径都满足）。
/// </para>
/// </summary>
[GlobalClass]
public partial class DungeonRoomStyle : Resource
{
    /// <summary>房间类别。</summary>
    [Export]
    public RoomCategory Category { get; set; } = RoomCategory.Combat;

    /// <summary>地面颜色。</summary>
    [Export]
    public Color FloorColor { get; set; } = new Color(0.55f, 0.57f, 0.62f); // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>墙体颜色。</summary>
    [Export]
    public Color WallColor { get; set; } = new Color(0.28f, 0.3f, 0.35f); // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>中心装饰建筑的相对路径（<c>res://素材/...</c>）；空 = 不摆建筑，退化成 BoxMesh 方块。</summary>
    [Export(PropertyHint.File, "*.glb")]
    public string LandmarkPath { get; set; } = string.Empty;

    /// <summary>中心建筑相对格边长的大小系数（1 = 占满一整格）。</summary>
    [Export(PropertyHint.Range, "0.1,3,0.01")]
    public float LandmarkScaleCells { get; set; } = 0.8f; // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>中心建筑绕 Y 的旋转（度）。</summary>
    [Export(PropertyHint.Range, "0,360,1")]
    public float LandmarkYawDegrees { get; set; }

    /// <summary>地面一格的分格数（1 = 整间一块地面；&gt; 1 会画出棋盘格，便于数格子）。</summary>
    [Export(PropertyHint.Range, "1,8,1")]
    public int FloorTilesPerCell { get; set; } = 1;

    /// <summary>默认配色集合：每个 <see cref="RoomCategory"/> 恰好一行。</summary>
    /// <returns>可直接绑到 <see cref="DungeonRoomStyleSet"/> 的数组。</returns>
    public static DungeonRoomStyle[] CreateDefaults()
    {
        return new DungeonRoomStyle[]
        {
            Row(RoomCategory.Start, new Color(0.56f, 0.72f, 0.86f), new Color(0.34f, 0.48f, 0.62f), "res://素材/小房子/House.glb", 0.7f),
            Row(RoomCategory.Combat, new Color(0.78f, 0.70f, 0.58f), new Color(0.44f, 0.38f, 0.32f), "res://素材/RTS建筑/House.glb", 0.85f),
            Row(RoomCategory.Special, new Color(0.88f, 0.74f, 0.44f), new Color(0.55f, 0.42f, 0.24f), "res://素材/RTS建筑/Market Stalls.glb", 0.9f),
            Row(RoomCategory.Hidden, new Color(0.62f, 0.52f, 0.76f), new Color(0.34f, 0.28f, 0.46f), "res://素材/海盗场景带动画/Chest Gold.glb", 0.5f),
            Row(RoomCategory.Prototype, new Color(0.70f, 0.70f, 0.70f), new Color(0.40f, 0.40f, 0.42f), string.Empty, 0.5f),
            Row(RoomCategory.Boss, new Color(0.82f, 0.40f, 0.38f), new Color(0.52f, 0.22f, 0.20f), "res://素材/RTS建筑/Castle Fortress.glb", 0.95f),
            Row(RoomCategory.Safe, new Color(0.62f, 0.82f, 0.62f), new Color(0.34f, 0.5f, 0.34f), "res://素材/RTS建筑/Temple.glb", 0.8f),
            Row(RoomCategory.Undecided, new Color(0.5f, 0.5f, 0.52f), new Color(0.3f, 0.3f, 0.32f), string.Empty, 0.5f),
        };
    }

    private static DungeonRoomStyle Row(RoomCategory category, Color floor, Color wall, string landmark, float landmarkScale)
    {
        return new DungeonRoomStyle
        {
            Category = category,
            FloorColor = floor,
            WallColor = wall,
            LandmarkPath = landmark,
            LandmarkScaleCells = landmarkScale,
        };
    }
}

/// <summary>
/// 配色表集合：把 <see cref="DungeonRoomStyle"/> 数组包成可在 <c>.tres</c> 里整体替换的资源，
/// 并在查询时做一次 O(1) 查表（按枚举值下标，缺行自动回退到 <see cref="RoomCategory.Undecided"/> 配色）。
/// </summary>
[GlobalClass]
public partial class DungeonRoomStyleSet : Resource
{
    /// <summary>全部配色行。</summary>
    [Export]
    public Godot.Collections.Array<DungeonRoomStyle> Styles { get; set; } = new();

    private DungeonRoomStyle?[]? _byCategory;

    /// <summary>行数。</summary>
    public int Count => Styles is null ? 0 : Styles.Count;

    /// <summary>按类别查配色（缺行回退 Undecided，再缺则返回内置默认）。</summary>
    /// <param name="category">房间类别。</param>
    public DungeonRoomStyle Get(RoomCategory category)
    {
        DungeonRoomStyle?[]? index = _byCategory;
        if (index is null)
        {
            index = BuildIndex();
        }
        int i = (int)category;
        if (i >= 0 && i < index.Length && index[i] is DungeonRoomStyle row)
        {
            return row;
        }
        int fallback = (int)RoomCategory.Undecided;
        if (fallback >= 0 && fallback < index.Length && index[fallback] is DungeonRoomStyle fallbackRow)
        {
            return fallbackRow;
        }
        return DungeonRoomStyle.CreateDefaults()[fallback];
    }

    /// <summary>用内置默认配色构造一个集合（<c>.tres</c> 未提供时宿主用它兜底）。</summary>
    public static DungeonRoomStyleSet CreateDefault()
    {
        DungeonRoomStyleSet set = new DungeonRoomStyleSet();
        DungeonRoomStyle[] defaults = DungeonRoomStyle.CreateDefaults();
        for (int i = 0; i < defaults.Length; i++)
        {
            set.Styles.Add(defaults[i]);
        }
        set.Rebuild();
        return set;
    }

    /// <summary>重建查表（Inspector 改行后调用；宿主在 <c>_Ready</c> 里也会调一次）。</summary>
    public void Rebuild()
    {
        _byCategory = null;
        BuildIndex();
    }

    private DungeonRoomStyle?[] BuildIndex()
    {
        int categories = Enum.GetValues<RoomCategory>().Length;
        DungeonRoomStyle?[] index = new DungeonRoomStyle?[categories];
        Godot.Collections.Array<DungeonRoomStyle>? styles = Styles;
        if (styles is not null)
        {
            for (int i = 0; i < styles.Count; i++)
            {
                DungeonRoomStyle? row = styles[i];
                if (row is null)
                {
                    continue;
                }
                int c = (int)row.Category;
                if (c >= 0 && c < index.Length)
                {
                    index[c] = row;
                }
            }
        }
        _byCategory = index;
        return index;
    }
}
