using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Spawn.Rooms;
using static Godot.Vector3;

namespace Game.Host;

/// <summary>
/// **2D 规则 → 3D 方砖关卡**的建场景器：把 <see cref="DungeonCellSet"/> 里每一格画成
/// 一块 <see cref="BoardGeometry.TileSize"/> 见方的立方体，并在砖上摆物件、门、房间中央建筑。
/// <para>
/// <b>单向铁律</b>（ECS.md §7.1）：只读数据 → 建节点。本类不持有世界、不写组件、不改 2D 数据。
/// </para>
/// <para>
/// <b>为什么一整类砖只出 1~2 个 MeshInstance3D</b>：一张 16×16 的图有 256 格，
/// 每格 2 个节点就是 512 次 draw call。地面砖全部合并成一个 <c>ArrayMesh</c>（按房间类别分开，
/// 因为颜色不同），墙/障碍各合并一个。物件与门是独立模型，必须逐格建。
/// </para>
/// <para>
/// <b>方砖的三个层次</b>（改任何一层都不影响另外两层）：
/// <list type="number">
/// <item><see cref="DungeonCellKind"/> → <see cref="DungeonTileKind"/>：2D 规则口径 → 3D 表现口径（<see cref="DungeonCellSet"/>）。</item>
/// <item><see cref="DungeonTileKind"/> + <see cref="DungeonPropKind"/> → 模型/尺寸（<see cref="DungeonTilePieces"/> + 本类的比例常量）。</item>
/// <item>格 → 世界坐标（<see cref="BoardGeometry"/>）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class DungeonTileView
{
    private readonly BoardGeometry _board;
    private readonly DungeonRoomStyleSet _styles;
    private readonly DungeonMaterialSet _materials;
    private readonly DungeonTilePieces _pieces;
    private readonly List<Node3D> _roots = new();

    /// <summary>构造。</summary>
    /// <param name="board">方砖几何契约。</param>
    /// <param name="styles">配色表（按房间类别取地面/墙色）。</param>
    /// <param name="materials">材质池。</param>
    /// <param name="pieces">模型表（null = 全部实体方块）。</param>
    public DungeonTileView(BoardGeometry board, DungeonRoomStyleSet styles, DungeonMaterialSet materials, DungeonTilePieces? pieces)
    {
        _board = board;
        _styles = styles ?? throw new ArgumentNullException(nameof(styles));
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        _pieces = pieces ?? new DungeonTilePieces(null, null);
    }

    /// <summary>本次建场景的实际统计（诊断/自检用）。<b>建完后读</b>，建之前是零值。</summary>
    public TileViewStats Stats { get; private set; }

    /// <summary>建出来的根节点（便于整层清空）。</summary>
    public IReadOnlyList<Node3D> Roots => _roots;

    /// <summary>
    /// 建整层方砖。**幂等**：重复调用前请先 <see cref="Clear"/>。
    /// </summary>
    /// <param name="root">父节点。</param>
    /// <param name="cells">方砖表。</param>
    /// <param name="seed">本层种子（物件抽查的确定性来源）。</param>
    /// <param name="useModels">是否加载 glb（headless 冒烟可关：只出实体方块）。</param>
    /// <returns>整层包围盒（米）。</returns>
    public Aabb Build(Node3D root, DungeonCellSet cells, int seed, bool useModels)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }
        if (cells is null)
        {
            throw new ArgumentNullException(nameof(cells));
        }
        IRandomProvider random = new Game.Core.Randomness.DeterministicRandom(seed);
        TileViewStats stats = new();

        // ── 1. 地面：按房间类别分组，每组合并成一个 ArrayMesh ──
        BuildFloors(root, cells, ref stats);

        // ── 2. 墙 / 障碍：整格立方体合并；有模型时逐格实例化 ──
        BuildBlocking(root, cells, random, useModels, ref stats);

        // ── 3. 门：一小块地面砖 + 两侧门框 ──
        BuildDoors(root, cells, random, useModels, ref stats);

        // ── 4. 物件：奖励/可破坏/不可破坏 ──
        BuildProps(root, cells, random, useModels, ref stats);

        // ── 5. 房间中央地标（建筑） ──
        BuildLandmarks(root, cells, random, useModels, ref stats);

        Stats = stats;
        return ComputeBounds(cells);
    }

    /// <summary>清掉建过的节点（幂等）。</summary>
    public void Clear()
    {
        for (int i = 0; i < _roots.Count; i++)
        {
            Node3D node = _roots[i];
            if (GodotObject.IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }
        _roots.Clear();
    }

    /// <summary>整层包围盒（米）：含墙高。</summary>
    /// <param name="cells">方砖表。</param>
    public Aabb ComputeBounds(DungeonCellSet cells)
    {
        float w = _board.Span(cells.Width);
        float d = _board.Span(cells.Height);
        return new Aabb(Vector3.Zero, new Vector3(Math.Max(w, _board.TileSize), _board.WallHeight, Math.Max(d, _board.TileSize)));
    }

    /// <summary>
    /// 一格的世界中心（含砖厚的一半偏移：地面砖的**顶面**在 y = 0）。
    /// </summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public Vector3 CellCenter(int x, int z) => _board.Center(x, z);

    /// <summary>一格的世界范围（米）：<c>Position</c> = 最小角（y=0），<c>Size</c> = 砖边长。</summary>
    /// <param name="x">列。</param>
    /// <param name="z">行。</param>
    public Aabb CellBounds(int x, int z)
        => new(new Vector3(_board.Center(x, z).X, 0f, _board.Center(x, z).Z), new Vector3(_board.TileSize, _board.WallHeight, _board.TileSize));

    // ─────────────────────────────────────────────────────────────
    // 各层
    // ─────────────────────────────────────────────────────────────

    private void BuildFloors(Node3D root, DungeonCellSet cells, ref TileViewStats stats)
    {
        Node3D floorRoot = NewRoot(root, "Floor");
        float tile = _board.TileSize;
        float thickness = _board.FloorThickness;

        // 按房间类别分桶：同色合并成一个 mesh（7 类房型 + 走廊/秘密通道各一份）。
        Dictionary<RoomCategory, SurfaceTool> buckets = new();
        SurfaceTool? corridor = null;
        for (int z = 0; z < cells.Height; z++)
        {
            for (int x = 0; x < cells.Width; x++)
            {
                DungeonTile tileData = cells.At(x, z);
                if (!tileData.IsVisibleFloor)
                {
                    continue;
                }
                bool checker = ((x + z) & 1) == 1;
                Color color = FloorColor(tileData, checker);
                SurfaceTool tool;
                if (tileData.Category == RoomCategory.Undecided)
                {
                    corridor ??= BeginTool();
                    tool = corridor;
                }
                else
                {
                    if (!buckets.TryGetValue(tileData.Category, out SurfaceTool? existing))
                    {
                        existing = BeginTool();
                        buckets[tileData.Category] = existing;
                    }
                    tool = existing;
                }
                // 地面砖：顶面在 y=0，向下铺 thickness。
                Vector3 center = new(
                    _board.ToWorld(x, z).X + tile * 0.5f,
                    -thickness * 0.5f,
                    _board.ToWorld(x, z).Y + tile * 0.5f);
                AppendBox(tool, center, new Vector3(tile, thickness, tile), color);
                stats.FloorTiles++;
            }
        }

        Material corridorMaterial = _materials.CreateFloorMaterial(RoomCategory.Undecided, _styles.Get(RoomCategory.Undecided).FloorColor);
        EmitBucket(floorRoot, "FloorCorridor", corridor, corridorMaterial);
        foreach (KeyValuePair<RoomCategory, SurfaceTool> pair in buckets)
        {
            DungeonRoomStyle style = _styles.Get(pair.Key);
            EmitBucket(floorRoot, "Floor" + pair.Key, pair.Value, _materials.CreateFloorMaterial(pair.Key, style.FloorColor));
        }
    }

    /// <summary>地面砖颜色：房型地面色 + 棋盘明暗（便于肉眼数格子）。</summary>
    private Color FloorColor(in DungeonTile tile, bool checker)
    {
        Color baseColor = tile.Category == RoomCategory.Undecided
            ? _styles.Get(RoomCategory.Undecided).FloorColor
            : _styles.Get(tile.Category).FloorColor;
        if (tile.Kind == DungeonTileKind.Door)
        {
            baseColor = baseColor.Lightened(DoorFloorLightening);
        }
        return checker ? baseColor.Darkened(1f - _materials.CheckerShade) : baseColor;
    }

    private void BuildBlocking(Node3D root, DungeonCellSet cells, IRandomProvider random, bool useModels, ref TileViewStats stats)
    {
        Node3D blockRoot = NewRoot(root, "Blocking");
        float tile = _board.TileSize;
        float height = _board.WallHeight;

        // 合并的实体方块（模型缺失/未选中时的兜底，也是「没有模型」的那部分）。
        Dictionary<RoomCategory, SurfaceTool> buckets = new();
        bool modelsAvailable = useModels && !_pieces.Catalog.IsEmpty;

        for (int z = 0; z < cells.Height; z++)
        {
            for (int x = 0; x < cells.Width; x++)
            {
                DungeonTile data = cells.At(x, z);
                if (!data.IsSolid)
                {
                    continue;
                }
                Vector3 center = new(
                    _board.ToWorld(x, z).X + tile * 0.5f,
                    height * 0.5f,
                    _board.ToWorld(x, z).Y + tile * 0.5f);

                Node3D? model = null;
                if (modelsAvailable)
                {
                    DungeonDecorEntry? entry = data.Kind == DungeonTileKind.Wall
                        ? _pieces.Find(_wallPieceName)
                        : _pieces.Find(data.PropKind == DungeonPropKind.Solid ? DungeonTilePieces.SolidPieceName : DungeonTilePieces.ObstaclePieceName);
                    if (entry is null)
                    {
                        entry = _pieces.PickBlockingPiece(random, data.Category);
                    }
                    if (entry is not null)
                    {
                        float jitter = Lerp(BlockingScaleMin, BlockingScaleMax, random.NextFloat());
                        // 盖房型墙体材质：素材本身缺贴图时是纯白，会吃掉「这是什么房间」的信息。
                        model = _pieces.Instantiate(entry, blockRoot, new Vector3(center.X, 0f, center.Z),
                            random.NextFloat() * FullTurnDegrees, height, tile, jitter,
                            _materials.CreateFloorMaterial(data.Category, _styles.Get(data.Category).WallColor));
                    }
                }

                if (model is not null)
                {
                    model.Name = BlockingName(data, x, z);
                    stats.BlockingModels++;
                    continue;
                }

                // 兜底：一整块立方体（与方砖严格同尺寸）。
                Color color = WallColorFor(data);
                if (!buckets.TryGetValue(data.Category, out SurfaceTool? tool))
                {
                    tool = BeginTool();
                    buckets[data.Category] = tool;
                }
                AppendBox(tool, center, new Vector3(tile, height, tile), color);
                stats.BlockingBlocks++;
            }
        }

        foreach (KeyValuePair<RoomCategory, SurfaceTool> pair in buckets)
        {
            DungeonRoomStyle style = _styles.Get(pair.Key);
            EmitBucket(blockRoot, "Block" + pair.Key, pair.Value, _materials.CreateFloorMaterial(pair.Key, style.WallColor));
        }
    }

    private void BuildDoors(Node3D root, DungeonCellSet cells, IRandomProvider random, bool useModels, ref TileViewStats stats)
    {
        Node3D doorRoot = NewRoot(root, "Doors");
        float tile = _board.TileSize;
        for (int z = 0; z < cells.Height; z++)
        {
            for (int x = 0; x < cells.Width; x++)
            {
                DungeonTile data = cells.At(x, z);
                if (data.Kind != DungeonTileKind.Door)
                {
                    continue;
                }
                Vector3 center = _board.Center(x, z);
                // 门框：同一格里的两根立柱（沿 Z 两侧），让「这里是门」一眼可辨，且不挡路。
                // 用实体方块而不是模型：门是路口，视觉必须可读且形态精确对齐方砖。
                Vector3 postSize = new(tile * DoorPostWidthRatio, _board.WallHeight, tile * DoorPostDepthRatio);
                SurfaceTool tool = BeginTool();
                Color postColor = _materials.DoorMaterial is StandardMaterial3D doorMat ? doorMat.AlbedoColor : Colors.White;
                AppendBox(tool, new Vector3(center.X - tile * 0.5f + postSize.X * 0.5f, _board.WallHeight * 0.5f, center.Z),
                    postSize, postColor);
                AppendBox(tool, new Vector3(center.X + tile * 0.5f - postSize.X * 0.5f, _board.WallHeight * 0.5f, center.Z),
                    postSize, postColor);
                EmitBucket(doorRoot, "DoorFrame" + x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "x" + z.ToString(System.Globalization.CultureInfo.InvariantCulture), tool, _materials.DoorMaterial);
                stats.DoorTiles++;
                _ = random;
                _ = useModels;
            }
        }
    }

    private void BuildProps(Node3D root, DungeonCellSet cells, IRandomProvider random, bool useModels, ref TileViewStats stats)
    {
        Node3D propRoot = NewRoot(root, "Props");
        bool modelsAvailable = useModels && !_pieces.Catalog.IsEmpty;
        for (int z = 0; z < cells.Height; z++)
        {
            for (int x = 0; x < cells.Width; x++)
            {
                DungeonTile data = cells.At(x, z);
                if (data.PropKind == DungeonPropKind.None || !data.PropAnchor)
                {
                    continue;
                }
                Vector3 center = _board.Center(x, z);
                string pieceName = data.PropKind switch
                {
                    DungeonPropKind.Reward => DungeonTilePieces.RewardPieceName,
                    DungeonPropKind.Breakable => DungeonTilePieces.BreakablePieceName,
                    DungeonPropKind.Solid => DungeonTilePieces.SolidPieceName,
                    _ => DungeonTilePieces.BuildingPieceName,
                };
                float targetHeight = data.PropKind switch
                {
                    DungeonPropKind.Reward => _board.WallHeight * PropRewardHeightRatio,
                    DungeonPropKind.Breakable => _board.WallHeight * PropBreakableHeightRatio,
                    DungeonPropKind.Solid => _board.WallHeight * PropSolidHeightRatio,
                    _ => _board.WallHeight * PropBuildingHeightRatio,
                };
                Node3D? model = null;
                if (modelsAvailable)
                {
                    DungeonDecorEntry? entry = _pieces.Find(pieceName);
                    if (entry is not null)
                    {
                        model = _pieces.Instantiate(entry, propRoot, new Vector3(center.X, 0f, center.Z),
                            random.NextFloat() * FullTurnDegrees, targetHeight, _board.TileSize, 1f,
                            PropMaterial(data.PropKind));
                    }
                }
                if (model is not null)
                {
                    model.Name = "Prop" + data.PropKind + "_" + x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "x" + z.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    stats.PropModels++;
                    continue;
                }
                // 兜底方块：占格的一部分，避免把整条路堵死看不出是「能捡的东西」。
                float side = _board.TileSize * PropBlockSizeRatio;
                MeshInstance3D box = new()
                {
                    Name = "PropBox_" + x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "x" + z.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Mesh = DungeonRoomView.CreateBoxMesh(new Vector3(side, targetHeight, side)),
                    MaterialOverride = PropMaterial(data.PropKind),
                    Position = new Vector3(center.X, targetHeight * 0.5f, center.Z),
                };
                propRoot.AddChild(box);
                stats.PropModels++;
            }
        }
    }

    private void BuildLandmarks(Node3D root, DungeonCellSet cells, IRandomProvider random, bool useModels, ref TileViewStats stats)
    {
        Node3D landmarkRoot = NewRoot(root, "Landmarks");
        float tile = _board.TileSize;
        // 每间房只在「房间中心格」放一个地标：模型数量与房间数同阶，不会随房间变大连带爆炸。
        HashSet<int> seen = new();
        for (int z = 0; z < cells.Height; z++)
        {
            for (int x = 0; x < cells.Width; x++)
            {
                DungeonTile data = cells.At(x, z);
                if (data.RoomSlot < 0 || !data.NeedsFloor || !seen.Add(data.RoomSlot))
                {
                    continue;
                }
                DungeonRoomStyle style = _styles.Get(data.Category);
                float size = Mathf.Max(LandmarkScaleFloorCells, style.LandmarkScaleCells) * tile;
                Vector3 center = RoomCenter(cells, data.RoomSlot);
                Node3D? model = null;
                if (useModels && !string.IsNullOrEmpty(style.LandmarkPath))
                {
                    DungeonDecorEntry? entry = _pieces.Find(style.LandmarkPath);
                    model = entry is not null
                        ? _pieces.Instantiate(entry, landmarkRoot, new Vector3(center.X, 0f, center.Z), style.LandmarkYawDegrees, -1f, size, 1f,
                            _materials.CreateFloorMaterial(data.Category, style.FloorColor))
                        : null;
                }
                if (model is not null)
                {
                    model.Name = "Landmark" + data.RoomSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    stats.Landmarks++;
                    continue;
                }
                float fallback = size * LandmarkFallbackBoxRatio;
                MeshInstance3D box = new()
                {
                    Name = "LandmarkBox" + data.RoomSlot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Mesh = DungeonRoomView.CreateBoxMesh(new Vector3(fallback, fallback, fallback)),
                    MaterialOverride = _materials.CreateFloorMaterial(data.Category, style.WallColor),
                    Position = new Vector3(center.X, fallback * 0.5f, center.Z),
                };
                landmarkRoot.AddChild(box);
                stats.Landmarks++;
                _ = random;
            }
        }
    }

    private Vector3 RoomCenter(DungeonCellSet cells, int roomSlot)
    {
        int minX = int.MaxValue;
        int minZ = int.MaxValue;
        int maxX = int.MinValue;
        int maxZ = int.MinValue;
        for (int z = 0; z < cells.Height; z++)
        {
            for (int x = 0; x < cells.Width; x++)
            {
                if (cells.At(x, z).RoomSlot != roomSlot)
                {
                    continue;
                }
                minX = Math.Min(minX, x);
                minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x);
                maxZ = Math.Max(maxZ, z);
            }
        }
        if (minX == int.MaxValue)
        {
            return Vector3.Zero;
        }
        float tile = _board.TileSize;
        return new Vector3(((minX + maxX + 1) * tile) * 0.5f, 0f, ((minZ + maxZ + 1) * tile) * 0.5f);
    }

    // ─────────────────────────────────────────────────────────────
    // 网格工具
    // ─────────────────────────────────────────────────────────────

    private Node3D NewRoot(Node3D parent, string name)
    {
        Node3D node = new() { Name = name };
        parent.AddChild(node);
        _roots.Add(node);
        return node;
    }

    private static SurfaceTool BeginTool()
    {
        SurfaceTool tool = new();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        return tool;
    }

    private void EmitBucket(Node3D parent, string name, SurfaceTool? tool, Material material)
    {
        if (tool is null)
        {
            return;
        }
        tool.GenerateNormals();
        Mesh mesh = tool.Commit();
        MeshInstance3D instance = new() { Name = name, Mesh = mesh, MaterialOverride = material };
        parent.AddChild(instance);
    }

    /// <summary>
    /// 往 SurfaceTool 里追加一个轴对齐立方体的 6 个面（每面 2 个三角形）。
    /// 与 <see cref="DungeonRoomView"/> 合并墙体的做法一致，但这里只带顶点+法线+顶点色。
    /// </summary>
    /// <param name="tool">目标工具。</param>
    /// <param name="center">立方体中心。</param>
    /// <param name="size">立方体尺寸。</param>
    /// <param name="color">顶点色。</param>
    private static void AppendBox(SurfaceTool tool, Vector3 center, Vector3 size, Color color)
    {
        Vector3 half = size * 0.5f;
        for (int f = 0; f < FaceNormals.Length; f++)
        {
            Vector3 n = FaceNormals[f];
            Vector3 tangent = Math.Abs(n.Y) > TangentAxisEpsilon ? Vector3.Right : Vector3.Up;
            Vector3 bitangent = n.Cross(tangent).Normalized();
            Vector3 t = tangent.Normalized();
            Vector3 centerFace = center + n * half;
            float extentT = Math.Abs(t.X) * half.X + Math.Abs(t.Y) * half.Y + Math.Abs(t.Z) * half.Z;
            float extentB = Math.Abs(bitangent.X) * half.X + Math.Abs(bitangent.Y) * half.Y + Math.Abs(bitangent.Z) * half.Z;
            Vector3 c0 = centerFace - (t * extentT) - (bitangent * extentB);
            Vector3 c1 = centerFace + (t * extentT) - (bitangent * extentB);
            Vector3 c2 = centerFace + (t * extentT) + (bitangent * extentB);
            Vector3 c3 = centerFace - (t * extentT) + (bitangent * extentB);
            AddVertex(tool, c0, n, color);
            AddVertex(tool, c1, n, color);
            AddVertex(tool, c2, n, color);
            AddVertex(tool, c0, n, color);
            AddVertex(tool, c2, n, color);
            AddVertex(tool, c3, n, color);
        }
    }

    private static void AddVertex(SurfaceTool tool, Vector3 position, Vector3 normal, Color color)
    {
        tool.SetNormal(normal);
        tool.SetColor(color);
        tool.AddVertex(position);
    }

    private static Vector3[] FaceNormals { get; } = BuildFaceNormals();

    private static Vector3[] BuildFaceNormals()
    {
        return new[]
        {
            Vector3.Up,
            Vector3.Down,
            Vector3.Left,
            Vector3.Right,
            Vector3.Back,
            Vector3.Forward,
        };
    }

    private static string BlockingName(in DungeonTile data, int x, int z)
        => (data.Kind == DungeonTileKind.Wall ? "Wall_" : "Block_")
           + x.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + "x" + z.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private Color WallColorFor(in DungeonTile data)
    {
        DungeonRoomStyle style = _styles.Get(data.Category);
        return data.PropKind != DungeonPropKind.None ? style.WallColor.Lightened(PropTintLightening) : style.WallColor;
    }

    private Material PropMaterial(DungeonPropKind kind)
    {
        Color color = kind switch
        {
            DungeonPropKind.Reward => RewardColor,
            DungeonPropKind.Breakable => BreakableColor,
            DungeonPropKind.Solid => SolidColor,
            _ => BuildingColor,
        };
        return DungeonMaterialSet.CreateSurface(color, _materials.Emissive);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private string _wallPieceName = DungeonTilePieces.WallPieceName;

    /// <summary>整格障碍模型名的显式覆盖（空 = 按 <see cref="DungeonTilePieces"/> 里的默认选择）。</summary>
    /// <param name="name">目录条目名。</param>
    public void SetWallPieceName(string name)
    {
        _wallPieceName = string.IsNullOrEmpty(name) ? DungeonTilePieces.WallPieceName : name;
    }

    private const float FullTurnDegrees = 360f; // arch-ok: 角度常量（一圈 = 360°），非玩法数值
    private const float TangentAxisEpsilon = 0.5f; // arch-ok: 几何分支阈值（判断法线是否竖直），非玩法数值

    private const float DoorFloorLightening = 0.18f; // arch-ok: 表现比例（门洞地面提亮），非玩法数值
    private const float DoorPostWidthRatio = 0.16f; // arch-ok: 几何比例（门柱宽相对方砖），由 Options 覆盖
    private const float DoorPostDepthRatio = 0.22f; // arch-ok: 几何比例（门柱深相对方砖），由 Options 覆盖
    private const float PropTintLightening = 0.25f; // arch-ok: 表现比例（物件与墙的区分度），非玩法数值
    private const float PropBlockSizeRatio = 0.45f; // arch-ok: 几何比例（兜底物件方块相对方砖），非玩法数值
    private const float PropRewardHeightRatio = 0.35f; // arch-ok: 几何比例（奖励高度相对墙高），非玩法数值
    private const float PropBreakableHeightRatio = 0.55f; // arch-ok: 几何比例（可破坏物高度相对墙高），非玩法数值
    private const float PropSolidHeightRatio = 0.85f; // arch-ok: 几何比例（不可破坏物高度相对墙高），非玩法数值
    private const float PropBuildingHeightRatio = 1.15f; // arch-ok: 几何比例（建筑高度相对墙高），非玩法数值
    private const float LandmarkScaleFloorCells = 0.1f; // arch-ok: 数值安全阈值（防零保护），非玩法数值
    private const float LandmarkFallbackBoxRatio = 0.7f; // arch-ok: 几何比例（降级方块相对地标），非玩法数值
    private const float BlockingScaleMin = 0.92f; // arch-ok: 表现参数（模型随机缩放下限），由 Options 覆盖
    private const float BlockingScaleMax = 1.08f; // arch-ok: 表现参数（模型随机缩放上限），由 Options 覆盖

    /// <summary>
    /// 物件颜色兜底：素材缺失 / 目录为空时，物件仍要能一眼区分出
    /// 「奖励 / 可破坏 / 不可破坏 / 建筑」四类，否则白盒关卡里分不清能捡的东西。
    /// </summary>
    private static readonly Color RewardColor = new(0.95f, 0.78f, 0.24f); // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值
    private static readonly Color BreakableColor = new(0.62f, 0.44f, 0.24f); // arch-ok: 资源默认值，可被 .tres 覆盖
    private static readonly Color SolidColor = new(0.42f, 0.44f, 0.5f); // arch-ok: 资源默认值，可被 .tres 覆盖
    private static readonly Color BuildingColor = new(0.55f, 0.5f, 0.42f); // arch-ok: 资源默认值，可被 .tres 覆盖
}

/// <summary>建方砖场景的统计（诊断输出 + 自检断言用）。</summary>
public struct TileViewStats
{
    /// <summary>地面方砖格数。</summary>
    public int FloorTiles;

    /// <summary>用模型画的墙/障碍格数。</summary>
    public int BlockingModels;

    /// <summary>用实体方块画的墙/障碍格数。</summary>
    public int BlockingBlocks;

    /// <summary>门格数。</summary>
    public int DoorTiles;

    /// <summary>物件数（含兜底方块）。</summary>
    public int PropModels;

    /// <summary>地标数。</summary>
    public int Landmarks;

    /// <inheritdoc />
    public override string ToString()
        => "地面砖=" + FloorTiles.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 墙/障碍(模型)=" + BlockingModels.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 墙/障碍(方块)=" + BlockingBlocks.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 门=" + DoorTiles.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 物件=" + PropModels.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + " 地标=" + Landmarks.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
