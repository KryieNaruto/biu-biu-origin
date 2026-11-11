using System;
using Godot;
using Game.Core;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// **方砖长什么样的数据表**：把 <see cref="DungeonTileKind"/> + <see cref="DungeonPropKind"/>
/// 映射到「一个模型 + 归一化规则」。代码里没有任何 glb 路径与尺寸字面量。
/// <para>
/// <b>为什么整格障碍物一律是「地形类模型」</b>：方砖是轴对齐立法体，一砖占满 6×6 米。
/// 把一栋 2 米高的人造小屋放大到 3 倍填满一砖，屋顶和墙会互相穿插（素材是按自己的比例建的）。
/// 地形类素材（岩石）没有「门/窗/屋顶」这类必须保持比例的特征，放大到填满一格最不容易露馅，
/// 而且天然符合「挡路的石头」这一玩法语义。建筑类素材仍然照常摆在房间中央（地标）。
/// </para>
/// </summary>
public sealed class DungeonTilePieces
{
    private readonly DungeonDecorCatalog _catalog;
    private readonly DungeonLandmarkLoader _loader;
    private readonly float _fillDensity;

    /// <summary>构造。</summary>
    /// <param name="catalog">装饰目录（null = 全部退回实体方块）。</param>
    /// <param name="loader">模型加载器（null = 全部退回实体方块）。</param>
    /// <param name="fillDensity">整格砖「真的有模型」的比例（0..1）。</param>
    public DungeonTilePieces(DungeonDecorCatalog? catalog, DungeonLandmarkLoader? loader, float fillDensity = FillBlockingDensityDefault)
    {
        _catalog = catalog ?? new DungeonDecorCatalog();
        _loader = loader ?? new DungeonLandmarkLoader();
        _fillDensity = Mathf.Clamp(fillDensity, 0f, 1f);
    }

    /// <summary>目录（只读，供调用方按名字查条目）。</summary>
    public DungeonDecorCatalog Catalog => _catalog;

    /// <summary>
    /// 为「墙/障碍砖」挑一个模型：在目录里按 <see cref="_fillDensity"/> 抽签，
    /// 未选中或素材缺失 → 返回 null（调用方画实体方块）。**确定性**：随机源由调用方播种。
    /// </summary>
    /// <param name="random">已播种的随机源。</param>
    /// <param name="category">所在房间类别（用于房型掩码过滤）。</param>
    public DungeonDecorEntry? PickBlockingPiece(IRandomProvider random, RoomCategory category)
    {
        if (_catalog.IsEmpty || random.NextFloat() >= _fillDensity)
        {
            return null;
        }
        return _catalog.Pick(category, random, DecorLayer.Ground);
    }

    /// <summary>按名字精确取一条可用条目（道具/门框/地标用；找不到返回 null）。</summary>
    /// <param name="name">目录里的 <c>name</c> 字段。</param>
    public DungeonDecorEntry? Find(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        for (int i = 0; i < _catalog.Items.Count; i++)
        {
            DungeonDecorEntry entry = _catalog.Items[i];
            if (entry.Available && string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// 实例化一条目录条目到 <paramref name="parent"/> 下，并**按目标高度归一化**
    /// （<paramref name="targetHeightMeters"/> &lt;= 0 时按目标水平尺寸归一化）。
    /// </summary>
    /// <param name="entry">目录条目。</param>
    /// <param name="parent">父节点。</param>
    /// <param name="position">世界位置（脚底中心）。</param>
    /// <param name="yawDegrees">绕 Y 轴旋转。</param>
    /// <param name="targetHeightMeters">目标高度（米）。</param>
    /// <param name="targetFootprintMeters">目标水平尺寸（米）。</param>
    /// <param name="jitter">额外缩放系数（1 = 不变）。</param>
    /// <param name="overrideMaterial">盖在模型所有表面上的材质（null = 用素材自带材质）。</param>
    /// <returns>建出的节点；失败返回 null。</returns>
    public Node3D? Instantiate(DungeonDecorEntry entry, Node3D parent, Vector3 position, float yawDegrees,
        float targetHeightMeters, float targetFootprintMeters, float jitter = 1f, Material? overrideMaterial = null)
    {
        Node3D? model = _loader.Instantiate(entry.Path, parent);
        if (model is null)
        {
            return null;
        }
        bool byHeight = targetHeightMeters > 0f;
        float raw = byHeight ? MeasureHeight(model) : _loader.MeasureFootprint(model);
        float target = byHeight ? targetHeightMeters : targetFootprintMeters;
        float scale = raw > 0f ? target / raw * jitter : jitter;
        model.Position = position;
        model.RotationDegrees = new Vector3(0f, yawDegrees, 0f);
        model.Scale = new Vector3(scale, scale, scale);
        if (overrideMaterial is not null)
        {
            ApplyMaterial(model, overrideMaterial);
        }
        return model;
    }

    /// <summary>
    /// 把材质盖到一棵子树里所有 <see cref="MeshInstance3D"/> 的**全部表面**上。
    /// <para>
    /// <b>为什么必须盖</b>：这批素材 glb 多数没有内嵌贴图，缺 UV 时会渲染成接近纯白——
    /// 一张 16×15 的方砖图里 60% 的象素是 192~224 的亮色，既分不出房型，也和「白盒关卡 + 数据上色」
    /// 的既定方向相反（AGENTS.md v12：模型只提供形状，颜色来自数据）。
    /// </para>
    /// </summary>
    /// <param name="root">模型根节点。</param>
    /// <param name="material">要盖上的材质。</param>
    public static void ApplyMaterial(Node root, Material material)
    {
        if (root is MeshInstance3D instance)
        {
            int surfaces = instance.Mesh is Mesh mesh ? Math.Max(mesh.GetSurfaceCount(), 1) : 1;
            for (int s = 0; s < surfaces; s++)
            {
                instance.SetSurfaceOverrideMaterial(s, material);
            }
        }
        int children = root.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            ApplyMaterial(root.GetChild(i), material);
        }
    }

    /// <summary>
    /// 量一个已挂树模型的包围盒高度（米）。与 <see cref="DungeonLandmarkLoader.MeasureFootprint"/> 同一套
    /// 「累积变换 × 网格 AABB」口径，只是取 Y 而不是 XZ。
    /// </summary>
    /// <param name="node">模型节点。</param>
    public static float MeasureHeight(Node3D node)
    {
        Aabb box = Measure(node, Transform3D.Identity, out bool has);
        return has ? box.Size.Y : 0f;
    }

    private static Aabb Measure(Node node, Transform3D accumulated, out bool has)
    {
        Transform3D local = accumulated;
        if (node is Node3D n3)
        {
            local = accumulated * n3.Transform;
        }
        Aabb result = default;
        has = false;
        if (node is MeshInstance3D instance && instance.Mesh is Mesh mesh)
        {
            result = local * mesh.GetAabb();
            has = true;
        }
        int children = node.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            Aabb child = Measure(node.GetChild(i), local, out bool childHas);
            if (!childHas)
            {
                continue;
            }
            result = has ? result.Merge(child) : child;
            has = true;
        }
        return result;
    }

    /// <summary>整格砖里「真的有模型」的比例默认值（1 = 每块都是模型）。</summary>
    public const float FillBlockingDensityDefault = 1f; // arch-ok: 表现参数默认值（模型覆盖率），由 Options 覆盖

    /// <summary>目录里「墙」用的模型名（整格立方体的视觉皮）。</summary>
    public const string WallPieceName = "Rock-BvlfuHFAuI"; // arch-ok: 数据选择（材质/外观），由 Options 覆盖

    /// <summary>目录里「普通障碍」用的模型名。</summary>
    public const string ObstaclePieceName = "Rock"; // arch-ok: 数据选择（材质/外观），由 Options 覆盖

    /// <summary>目录里「奖励」用的模型名。</summary>
    public const string RewardPieceName = "Chest Gold"; // arch-ok: 数据选择（材质/外观），由 Options 覆盖

    /// <summary>目录里「可破坏物」用的模型名。</summary>
    public const string BreakablePieceName = "Barrel"; // arch-ok: 数据选择（材质/外观），由 Options 覆盖

    /// <summary>目录里「不可破坏物」用的模型名。</summary>
    public const string SolidPieceName = "Cannon"; // arch-ok: 数据选择（材质/外观），由 Options 覆盖

    /// <summary>目录里「建筑」用的模型名（房间中央地标）。</summary>
    public const string BuildingPieceName = "Barracks"; // arch-ok: 数据选择（材质/外观），由 Options 覆盖
}
