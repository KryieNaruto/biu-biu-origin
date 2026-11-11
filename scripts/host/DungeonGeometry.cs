using System;
using Godot;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 地牢几何参数：**所有尺寸都由「每格米数」按比例派生**，这里只放比例（不是绝对尺寸）。
/// <para>
/// <b>为什么不写死「墙高 3 米」</b>（规则4 / GS005）：格边长是数据（<see cref="DungeonOptions.CellSizeMeters"/>），
/// 墙高/墙厚/门洞宽都应该是格边长的比例——这样把格边长从 12 米改成 16 米时整层等比例放大，
/// 而不是出现「墙还是 3 米高、门洞却 4 米宽」的比例崩坏。所有比例都带 <c>// arch-ok</c> 理由：
/// 它们是几何比例常量（形态参数），不是玩法数值；要调就改 <see cref="DungeonOptions"/> 新增的 <c>[Export]</c>。
/// </para>
/// </summary>
public readonly struct DungeonRoomGeometry
{
    /// <summary>每格边长（米）。</summary>
    public float CellSize { get; init; }

    /// <summary>墙高（米）。</summary>
    public float WallHeight { get; init; }

    /// <summary>墙厚（米）。</summary>
    public float WallThickness { get; init; }

    /// <summary>门洞宽（米）：相邻房间贴边相接，门洞必须对齐边中点才连通。</summary>
    public float DoorGapWidth { get; init; }

    /// <summary>地面板厚（米）。</summary>
    public float FloorThickness { get; init; }

    /// <summary>门板最小宽度（米）：门洞比两墙厚还窄时的下限，避免生成退化网格。</summary>
    public float DoorBlockMinWidth { get; init; }

    /// <summary>墙段最短长度（米）：短于此的墙段直接省略（否则会生成退化三角面）。</summary>
    public float MinSegmentLength { get; init; }

    // ── 结构性比例常量（全部相对格边长；不是玩法数值） ──

    /// <summary>墙高 / 格边长。</summary>
    public const float WallHeightRatio = 0.18f; // arch-ok: 几何比例常量（墙高相对格边长），由 .tres/Options 间接可调

    /// <summary>墙厚 / 格边长。</summary>
    public const float WallThicknessRatio = 0.045f; // arch-ok: 几何比例常量（墙厚相对格边长）

    /// <summary>门洞宽 / 格边长。</summary>
    public const float DoorGapRatio = 0.34f; // arch-ok: 几何比例常量（门洞宽相对格边长）

    /// <summary>地面板厚 / 格边长。</summary>
    public const float FloorThicknessRatio = 0.03f; // arch-ok: 几何比例常量（地面厚相对格边长）

    /// <summary>门板最小宽 / 格边长。</summary>
    public const float DoorBlockMinRatio = 0.1f; // arch-ok: 几何比例常量（门板最小宽相对格边长）

    /// <summary>墙段最短长度 / 格边长。</summary>
    public const float MinSegmentRatio = 0.01f; // arch-ok: 几何比例常量（避免退化三角面的最小墙段）

    /// <summary>
    /// 由格边长构造全部米制尺寸。
    /// </summary>
    /// <param name="cellSizeMeters">每格边长（米），必须 &gt; 0。</param>
    public static DungeonRoomGeometry FromCellSize(float cellSizeMeters)
    {
        if (!(cellSizeMeters > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSizeMeters), "格边长必须为正。");
        }
        return new DungeonRoomGeometry
        {
            CellSize = cellSizeMeters,
            WallHeight = cellSizeMeters * WallHeightRatio,
            WallThickness = cellSizeMeters * WallThicknessRatio,
            DoorGapWidth = cellSizeMeters * DoorGapRatio,
            FloorThickness = cellSizeMeters * FloorThicknessRatio,
            DoorBlockMinWidth = cellSizeMeters * DoorBlockMinRatio,
            MinSegmentLength = cellSizeMeters * MinSegmentRatio,
        };
    }
}

/// <summary>
/// 房间视图用的**材质池**：全层共享少量材质实例（每类房间的地面材质 + 一份墙体 + 一份门板），
/// 避免「每间房 new 一份 StandardMaterial3D」造成资源爆炸与渲染状态切换抖动。
/// <para>
/// 用自发光 <c>EMISSION</c> 调色：俯视角度下墙面几乎正对着光，纯漫反射会被光照吃掉颜色，
/// 让「Boss 房红 / 商店房黄」这类一眼可辨的语义失效；给一点自发光能让类别色稳定可读。
/// </para>
/// </summary>
public sealed class DungeonMaterialSet
{
    private readonly StandardMaterial3D?[] _floorByCategory;
    private readonly float _emissiveEnergy;
    private readonly Color _doorColor;

    /// <summary>棋盘格明暗系数。</summary>
    public float CheckerShade { get; }

    /// <summary>地面自发光强度（方砖视图的兜底材质共用同一个强度）。</summary>
    public float Emissive => _emissiveEnergy;

    /// <summary>墙体材质。</summary>
    public StandardMaterial3D WallMaterial { get; }

    /// <summary>门板材质。</summary>
    public StandardMaterial3D DoorMaterial { get; }

    /// <summary>空隙地面材质（房间之间的空白）。</summary>
    public StandardMaterial3D VoidMaterial { get; }

    /// <summary>构造材质池。</summary>
    /// <param name="styles">配色表。</param>
    /// <param name="emissiveEnergy">自发光强度（0..1）。</param>
    /// <param name="checkerShade">棋盘格明暗系数（一般 &lt; 1 表示稍暗）。</param>
    /// <param name="voidColor">空隙地面颜色。</param>
    public DungeonMaterialSet(DungeonRoomStyleSet styles, float emissiveEnergy, float checkerShade, Color voidColor)
    {
        if (styles is null)
        {
            throw new ArgumentNullException(nameof(styles));
        }
        _emissiveEnergy = emissiveEnergy;
        CheckerShade = checkerShade;

        int categories = Enum.GetValues<RoomCategory>().Length;
        _floorByCategory = new StandardMaterial3D?[categories];
        for (int i = 0; i < categories; i++)
        {
            DungeonRoomStyle style = styles.Get((RoomCategory)i);
            _floorByCategory[i] = CreateSurface(style.FloorColor, _emissiveEnergy * CheckerShade);
        }

        DungeonRoomStyle wallRef = styles.Get(RoomCategory.Undecided);
        WallMaterial = CreateSurface(wallRef.WallColor, _emissiveEnergy);
        _doorColor = wallRef.WallColor.Lightened(DoorColorLightening);        DoorMaterial = CreateSurface(_doorColor, _emissiveEnergy);
        VoidMaterial = CreateSurface(voidColor, 0f);
    }

    /// <summary>门板颜色相对墙色的提亮量（0 = 与墙同色；1 = 全白）。</summary>
    public const float DoorColorLightening = 0.45f; // arch-ok: 表现参数默认值（门板提亮），由 .tres 覆盖

    /// <summary>表面粗糙度（写实度：1 = 全漫反射，0 = 镜面）。</summary>
    public const float SurfaceRoughness = 0.85f; // arch-ok: 表现参数默认值（材质粗糙度），由 .tres 覆盖

    /// <summary>按房间类别取地面材质（每类房间共享一份，避免每间房 new 材质）。</summary>
    /// <param name="category">房间类别。</param>
    /// <param name="floorColor">该房间地面色（类别越界时用于临时构造一份）。</param>
    public StandardMaterial3D CreateFloorMaterial(RoomCategory category, Color floorColor)
    {
        int c = (int)category;
        if (c >= 0 && c < _floorByCategory.Length && _floorByCategory[c] is StandardMaterial3D material)
        {
            return material;
        }
        return CreateSurface(floorColor, _emissiveEnergy);
    }

    /// <summary>构造一份逐面显示的自发光材质。</summary>
    /// <param name="color">颜色。</param>
    /// <param name="emissive">自发光强度（0 = 纯漫反射）。</param>
    public static StandardMaterial3D CreateSurface(Color color, float emissive)
    {
        StandardMaterial3D material = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = SurfaceRoughness,
            Metallic = 0f,
        };
        if (emissive > 0f)
        {
            material.EmissionEnabled = true;
            material.Emission = color;
            material.EmissionEnergyMultiplier = emissive;
        }
        return material;
    }
}

/// <summary>
/// 中心装饰建筑的**素材加载器**：把 <see cref="DungeonRoomStyle.LandmarkPath"/>（<c>res://素材/*.glb</c>）
/// 变成节点。加载失败 / 路径为空 / 素材缺失 → 返回 <c>null</c>，调用方自动退化成 BoxMesh，
/// <b>绝不让缺素材把整个场景带崩</b>。
/// <para>
/// 已加载的 PackedScene 按路径缓存：同一模型在多个房间复用时不重复读盘（glb 已 import，读取很快但仍应缓存）。
/// </para>
/// </summary>
public sealed class DungeonLandmarkLoader
{
    private readonly System.Collections.Generic.Dictionary<string, PackedScene?> _cache = new(StringComparer.Ordinal);
    private int _loadedCount;
    private int _failedCount;
    private string _firstMissingPath = string.Empty;

    /// <summary>成功加载过的模型种数。</summary>
    public int LoadedCount => _loadedCount;

    /// <summary>加载失败次数（缺素材 / 非 3D 场景）。</summary>
    public int FailedCount => _failedCount;

    /// <summary>
    /// 加载一个模型。路径为空或加载失败返回 <c>null</c>（已记一次失败）。
    /// </summary>
    /// <param name="path"><c>res://</c> 路径。</param>
    public PackedScene? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        if (_cache.TryGetValue(path, out PackedScene? cached))
        {
            return cached;
        }
        PackedScene? scene = null;
        if (!ResourceLoader.Exists(path))
        {
            NoteMissing(path, "不存在");
        }
        else
        {
            Resource? res = ResourceLoader.Load(path);
            scene = res as PackedScene;
            if (scene is null)
            {
                NoteMissing(path, "不是 PackedScene");
            }
            else
            {
                _loadedCount++;
            }
        }
        _cache[path] = scene;
        return scene;
    }

    /// <summary>
    /// 记一次素材失败。<b>只在第一条时告警</b>——素材目录整体被搬走时会有上百条同时失效，
    /// 逐条 PushWarning 会把控制台刷穿，反而看不见真正的问题。
    /// </summary>
    /// <param name="path">失败的路径。</param>
    /// <param name="reason">失败原因（写进日志）。</param>
    private void NoteMissing(string path, string reason)
    {
        _failedCount++;
        if (string.IsNullOrEmpty(_firstMissingPath))
        {
            _firstMissingPath = path;
            GD.PushWarning("地牢装饰素材不可用（", reason, "），之后同类全部降级为方块，不再重复告警：" + path);
        }
    }

    /// <summary>首个失败路径（诊断用；全部成功时为空）。</summary>
    public string FirstMissingPath => _firstMissingPath;

    /// <summary>实例化并挂到 <paramref name="parent"/> 下；失败返回 <c>null</c>。</summary>
    /// <param name="path">素材路径。</param>
    /// <param name="parent">父节点。</param>
    public Node3D? Instantiate(string? path, Node3D parent)
    {
        PackedScene? scene = Load(path);
        if (scene is null)
        {
            return null;
        }
        if (scene.Instantiate() is not Node3D node)
        {
            NoteMissing(path ?? string.Empty, "根节点不是 Node3D");
            return null;
        }
        parent.AddChild(node);
        return node;
    }

    /// <summary>
    /// 量一个已实例化模型在世界 XZ 平面上的**最大占地边长**（米），用于把它归一化到目标占地尺寸。
    /// 取 AABB 的 max(x, z)：不同 glb 的原始尺度差异巨大（有的以厘米建模），不归一化会顶穿墙体或小到看不见。
    /// </summary>
    /// <param name="node">已挂到树上的节点（需要 GlobalTransform 有效）。</param>
    public float MeasureFootprint(Node3D node)
    {
        Aabb box = ComputeAabb(node, Transform3D.Identity);
        return Math.Max(box.Size.X, box.Size.Z);
    }

    private static Aabb ComputeAabb(Node node, Transform3D accumulated)
    {
        Transform3D local = accumulated;
        if (node is Node3D n3)
        {
            local = accumulated * n3.Transform;
        }
        Aabb result = default;
        bool has = false;
        if (node is MeshInstance3D meshInstance && meshInstance.Mesh is Mesh mesh)
        {
            result = local * mesh.GetAabb();
            has = true;
        }
        int children = node.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            Aabb child = ComputeAabb(node.GetChild(i), local);
            if (child.Size == Vector3.Zero && child.Position == Vector3.Zero)
            {
                continue;
            }
            result = has ? result.Merge(child) : child;
            has = true;
        }
        return has ? result : new Aabb(Vector3.Zero, Vector3.Zero);
    }
}
