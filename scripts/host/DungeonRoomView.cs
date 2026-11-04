using System;
using System.Collections.Generic;
using Godot;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 一间房的**可视表现**：地面 + 四面墙 + 门洞缺口 + 门板 + 中心装饰建筑。
/// <para>
/// <b>单向铁律</b>（ECS.md §7.1）：本节点只读 <see cref="RoomPlan"/> 与配色表，建完之后
/// <b>不反写任何玩法状态</b>；它就是一个「把数据画出来」的静态节点。
/// 门的开合由玩法层决定，宿主通过 <see cref="SetDoorBlocks"/> 把那个决定画出来（节点只被单向驱动）。
/// </para>
/// <para>
/// <b>为什么不给每间房单独写尺寸字面量</b>（规则4 / GS005）：房间几何完全由
/// <see cref="DungeonRoomGeometry"/>（每格米数 + 墙厚/墙高/门洞宽的比值）派生，
/// 本类里没有一处「数值 = 几何尺寸」。想改墙高、门洞宽，改比值即可。
/// </para>
/// <para>
/// <b>几何生成的后端</b>：默认 <see cref="GeometryBackend.MeshFromBoxes"/>——一个 <c>SurfaceTool</c>
/// 把一堆轴对齐盒子的 6 个面拷进一个 <c>Mesh</c>，<u>整间房的墙体只用 1 个 MeshInstance3D</u>；
/// <see cref="GeometryBackend.NodePerBox"/> 时每个盒子一个 MeshInstance3D，便于逐块点选（调试用）。
/// </para>
/// <para>
/// <b>坐标约定</b>（与 <c>RoomBuildContext.ToWorld</c> 一致）：<see cref="RoomPlan.Cell"/> 是房间西北角格，
/// 房间占 <c>[Cell.X, Cell.X+W) × [Cell.Z, Cell.Z+D)</c>；局部格坐标 <c>local</c> → 世界 <c>(Cell + local) × CellSize</c>。
/// 因此 <c>local = 0</c> 是房间第一个格的<u>近边</u>，<c>local = W</c> 是最后一个格的<u>远边</u>；
/// 墙放在这两条边上，门洞必须落在两房共享边界（由建场景器用 <see cref="DungeonGridMath"/> 预算好传进来）。
/// </para>
/// </summary>
public partial class DungeonRoomView : Node3D
{
    /// <summary>几何生成后端。</summary>
    public enum GeometryBackend
    {
        /// <summary>一个房间的墙体合并成一个 Mesh（默认，省 draw call）。</summary>
        MeshFromBoxes = 0,

        /// <summary>每个盒子一个 MeshInstance3D（便于逐块点选 / 调试）。</summary>
        NodePerBox = 1,
    }

    private readonly List<MeshInstance3D> _doorBlocks = new();
    private readonly List<int> _doorLinkSlots = new();
    private readonly List<(BoxSpec Spec, RoomDoor Door)> _doorBlockSpecs = new();
    private readonly Dictionary<int, float> _doorAlong = new();

    /// <summary>门洞中心（沿边方向）的兜底值（米）：没有预算值时用该边中点（仅在数据异常时出现）。</summary>
    private float _edgeMidFallback = 0.5f;

    /// <summary>本视图对应的房间规划（只读快照，建完后不再变）。</summary>
    public RoomPlan Plan { get; private set; }

    /// <summary>房间中心的世界坐标（米）。</summary>
    public Vector3 CenterWorld { get; private set; }

    /// <summary>房间在地面上的包围盒（米）：<c>Position</c> = 最小角，<c>Size.Y</c> = 墙高。</summary>
    public Aabb WorldBounds { get; private set; }

    /// <summary>本房间可见门洞数量（诊断）。</summary>
    public int DoorGapCount { get; private set; }

    /// <summary>门板数量（诊断）。</summary>
    public int DoorBlockCount => _doorBlocks.Count;

    /// <summary>
    /// 建一间房的可视表现。
    /// </summary>
    /// <param name="plan">房间规划（只读）。</param>
    /// <param name="doors">属于本房间的门（按 <see cref="RoomDoor.Room"/> 过滤后的切片）。</param>
    /// <param name="doorAlongLocals">
    /// 门洞中心在本房间里的「沿边方向局部格坐标」（由 <see cref="DungeonGridMath.TrySharedDoorPosition"/> 算出），
    /// key = <see cref="DoorId.Slot"/>。缺失时该门退化为边中点。
    /// </param>
    /// <param name="style">配色行。</param>
    /// <param name="geometry">几何参数（米制）。</param>
    /// <param name="backend">几何后端。</param>
    /// <param name="materials">材质池（地面/墙体/门板三类材质由建场景器统一管理）。</param>
    /// <param name="landmarkLoader">中心建筑加载器（可为 null：全部退化成方块）。</param>
    public void Build(
        in RoomPlan plan,
        ReadOnlySpan<RoomDoor> doors,
        IReadOnlyDictionary<int, float>? doorAlongLocals,
        DungeonRoomStyle style,
        DungeonRoomGeometry geometry,
        GeometryBackend backend,
        DungeonMaterialSet materials,
        DungeonLandmarkLoader? landmarkLoader)
    {
        if (style is null)
        {
            throw new ArgumentNullException(nameof(style));
        }
        if (materials is null)
        {
            throw new ArgumentNullException(nameof(materials));
        }

        Plan = plan;
        Name = RoomNodeName(plan);
        float cell = geometry.CellSize;
        float width = plan.Footprint.Width * cell;
        float depth = plan.Footprint.Depth * cell;
        Vector3 origin = new Vector3(plan.Cell.X * cell, 0f, plan.Cell.Z * cell);
        CenterWorld = new Vector3(origin.X + width * 0.5f, 0f, origin.Z + depth * 0.5f);
        WorldBounds = new Aabb(origin, new Vector3(width, geometry.WallHeight, depth));
        _edgeMidFallback = Math.Max(width, depth) * 0.5f;

        _doorAlong.Clear();
        if (doorAlongLocals is not null)
        {
            foreach (KeyValuePair<int, float> pair in doorAlongLocals)
            {
                _doorAlong[pair.Key] = pair.Value;
            }
        }

        // ── 1. 地面：按「每格分片数」切块，棋盘格配色便于肉眼数格子 ──
        BuildFloor(style, geometry, backend, materials, origin, width, depth);

        // ── 2. 墙体：四面各由「墙段 + 门洞」拼成 ──
        List<BoxSpec> wallBoxes = new();
        _doorBlockSpecs.Clear();
        DoorGapCount = 0;
        RoomDirection[] edges = { RoomDirection.North, RoomDirection.South, RoomDirection.West, RoomDirection.East };
        for (int e = 0; e < edges.Length; e++)
        {
            BuildEdge(edges[e], doors, geometry, origin, width, depth, wallBoxes);
        }
        EmitBoxes(wallBoxes, "Walls", materials.WallMaterial, backend);

        // ── 3. 门板：每扇可见门一个 MeshInstance3D，宿主可用 SetDoorBlocks 控制开合 ──
        BuildDoorBlocks(materials);

        // ── 4. 中心装饰建筑（Boss 房/商店房一眼可辨；加载失败自动退化成方块） ──
        BuildLandmark(style, geometry, materials, landmarkLoader);
    }

    /// <summary>
    /// 切换某条链路（门）的门板可见性。<b>只动节点，不动 ECS</b>——门开没开由玩法层决定，
    /// 这里只是把那个决定画出来（单向驱动）。
    /// </summary>
    /// <param name="linkSlot">链路槽位（= <see cref="RoomDoor.LinkSlot"/>）。</param>
    /// <param name="blocked">true = 门板落下（封锁）；false = 门板隐藏（可通行）。</param>
    public void SetDoorBlocks(int linkSlot, bool blocked)
    {
        for (int i = 0; i < _doorLinkSlots.Count; i++)
        {
            if (_doorLinkSlots[i] != linkSlot)
            {
                continue;
            }
            MeshInstance3D mesh = _doorBlocks[i];
            if (GodotObject.IsInstanceValid(mesh))
            {
                mesh.Visible = blocked;
            }
        }
    }

    /// <summary>把所有门板设为同一状态（场景初始化时用）。</summary>
    public void SetAllDoorBlocks(bool blocked)
    {
        for (int i = 0; i < _doorBlocks.Count; i++)
        {
            MeshInstance3D mesh = _doorBlocks[i];
            if (GodotObject.IsInstanceValid(mesh))
            {
                mesh.Visible = blocked;
            }
        }
    }

    /// <summary>节点名：带房间槽位与类别，便于在场景树里定位。</summary>
    /// <param name="plan">房间规划。</param>
    public static string RoomNodeName(in RoomPlan plan)
        => "Room" + plan.Id.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + plan.Category.ToString();

    // ─────────────────────────────────────────────────────────────
    // 地面
    // ─────────────────────────────────────────────────────────────

    private void BuildFloor(DungeonRoomStyle style, DungeonRoomGeometry geometry, GeometryBackend backend,
        DungeonMaterialSet materials, Vector3 origin, float width, float depth)
    {
        int divisions = Math.Max(1, style.FloorTilesPerCell) * Math.Max(1, Plan.Footprint.Width);
        float tileSize = width / divisions;
        float thickness = geometry.FloorThickness;
        List<BoxSpec> boxes = new();
        for (int tx = 0; tx < divisions; tx++)
        {
            for (int tz = 0; tz < divisions; tz++)
            {
                bool checker = ((tx + tz) & 1) == 1;
                Color color = checker ? Shade(style.FloorColor, materials.CheckerShade) : style.FloorColor;
                Vector3 c = new Vector3(
                    origin.X + tileSize * (tx + 0.5f),
                    -thickness * 0.5f,
                    origin.Z + tileSize * (tz + 0.5f));
                boxes.Add(new BoxSpec(c, new Vector3(tileSize, thickness, tileSize), color));
            }
        }
        EmitBoxes(boxes, "Floor", materials.CreateFloorMaterial(Plan.Category, style.FloorColor), backend);
    }

    // ─────────────────────────────────────────────────────────────
    // 墙体 / 门洞 / 门板
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 建一面墙（含门洞与门板）。墙以「该边中轴」为中心放置：墙厚一半伸进房间、一半伸出，
    /// 于是相邻两房的墙在共享边界上重合，不会出现一条缝或一道双墙。
    /// <b>秘密通道不画门洞也不画门板</b>（隐藏房入口要能被「发现」）。
    /// </summary>
    private void BuildEdge(
        RoomDirection edge,
        ReadOnlySpan<RoomDoor> doors,
        DungeonRoomGeometry geometry,
        Vector3 origin,
        float width,
        float depth,
        List<BoxSpec> wallBoxes)
    {
        bool alongX = edge == RoomDirection.North || edge == RoomDirection.South;
        float edgeLength = alongX ? width : depth;
        float baseCoord = edge switch
        {
            RoomDirection.North => origin.Z,
            RoomDirection.South => origin.Z + depth,
            RoomDirection.West => origin.X,
            _ => origin.X + width,
        };

        List<(float Center, RoomDoor Door)> gaps = new();
        for (int i = 0; i < doors.Length; i++)
        {
            RoomDoor door = doors[i];
            if (door.Direction != edge || door.IsSecret)
            {
                continue;
            }
            gaps.Add((DoorAlongMeters(door, geometry), door));
            DoorGapCount++;
        }

        if (gaps.Count == 0)
        {
            AddWallSegment(wallBoxes, alongX, baseCoord, 0f, edgeLength, geometry, origin, width, depth);
            return;
        }

        gaps.Sort((a, b) => a.Center.CompareTo(b.Center));
        float cursor = 0f;
        for (int g = 0; g < gaps.Count; g++)
        {
            (float center, RoomDoor door) = gaps[g];
            float half = geometry.DoorGapWidth * 0.5f;
            if (center - half - cursor > geometry.MinSegmentLength)
            {
                AddWallSegment(wallBoxes, alongX, baseCoord, cursor, center - half - cursor, geometry, origin, width, depth);
            }
            cursor = center + half;

            // 门板：盖住门洞、比门洞略窄（避免与墙段穿插）。
            // ×2 = 墙厚一半伸进房内、一半伸出房外，门板两侧各让开一个墙厚。
            float blockWidth = Math.Max(geometry.DoorGapWidth - geometry.WallThickness * DoorBlockWallSides, geometry.DoorBlockMinWidth);
            Vector3 c = PointOnEdge(alongX, baseCoord, origin, width, depth, center, geometry.WallHeight * 0.5f);
            Vector3 size = alongX
                ? new Vector3(blockWidth, geometry.WallHeight, geometry.WallThickness)
                : new Vector3(geometry.WallThickness, geometry.WallHeight, blockWidth);
            _doorBlockSpecs.Add((new BoxSpec(c, size, new Color(1f, 1f, 1f)), door));
        }
        float tailLength = edgeLength - cursor;
        if (tailLength > geometry.MinSegmentLength)
        {
            AddWallSegment(wallBoxes, alongX, baseCoord, cursor, tailLength, geometry, origin, width, depth);
        }
    }

    /// <summary>门洞中心在「沿边方向」上的米制坐标（未预算时退化为边中点）。</summary>
    private float DoorAlongMeters(in RoomDoor door, in DungeonRoomGeometry geometry)
    {
        if (_doorAlong.TryGetValue(door.Id.Slot, out float local))
        {
            return local * geometry.CellSize;
        }
        return _edgeMidFallback;
    }

    private void AddWallSegment(List<BoxSpec> boxes, bool alongX, float baseCoord, float startAlong, float length,
        in DungeonRoomGeometry geometry, Vector3 origin, float width, float depth)
    {
        float mid = startAlong + length * 0.5f;
        Vector3 c = PointOnEdge(alongX, baseCoord, origin, width, depth, mid, geometry.WallHeight * 0.5f);
        Vector3 size = alongX
            ? new Vector3(length, geometry.WallHeight, geometry.WallThickness)
            : new Vector3(geometry.WallThickness, geometry.WallHeight, length);
        boxes.Add(new BoxSpec(c, size, new Color(1f, 1f, 1f)));
    }

    /// <summary>把「沿边方向坐标 + 该边的基准坐标」换算成世界坐标（竖直中心 = 给定 y）。</summary>
    private static Vector3 PointOnEdge(bool alongX, float baseCoord, Vector3 origin, float width, float depth, float along, float y)
    {
        if (alongX)
        {
            // 北/南墙：沿 X 方向铺开，基准坐标是 Z。
            return new Vector3(origin.X + along, y, baseCoord);
        }
        // 东/西墙：沿 Z 方向铺开，基准坐标是 X。
        return new Vector3(baseCoord, y, origin.Z + along);
    }

    private void BuildDoorBlocks(DungeonMaterialSet materials)
    {
        if (_doorBlockSpecs.Count == 0)
        {
            return;
        }
        Node3D root = new Node3D { Name = "Doors" };
        AddChild(root);
        for (int i = 0; i < _doorBlockSpecs.Count; i++)
        {
            (BoxSpec spec, RoomDoor door) = _doorBlockSpecs[i];
            MeshInstance3D mesh = new MeshInstance3D
            {
                Name = "Door_Link" + door.LinkSlot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Mesh = CreateBoxMesh(spec.Size),
                MaterialOverride = materials.DoorMaterial,
                Position = spec.Center,
                Visible = !door.IsOpenByDefault,
            };
            root.AddChild(mesh);
            _doorBlocks.Add(mesh);
            _doorLinkSlots.Add(door.LinkSlot);
        }
        _doorBlockSpecs.Clear();
    }

    // ─────────────────────────────────────────────────────────────
    // 中心建筑
    // ─────────────────────────────────────────────────────────────

    private void BuildLandmark(DungeonRoomStyle style, DungeonRoomGeometry geometry, DungeonMaterialSet materials,
        DungeonLandmarkLoader? loader)
    {
        float size = Math.Max(LandmarkScaleFloorCells, style.LandmarkScaleCells) * geometry.CellSize;
        Node3D root = new Node3D { Name = "Landmark" };
        AddChild(root);

        Node3D? model = loader?.Instantiate(style.LandmarkPath, root);
        if (model is not null)
        {
            // 归一化到「占地 size 米」：不同 glb 的原始尺度差异极大，不归一化会出现有的房子顶到墙、有的看不见。
            float raw = loader!.MeasureFootprint(model);
            float scale = raw > 0f ? size / raw : 1f;
            model.Name = "LandmarkModel";
            model.Position = new Vector3(CenterWorld.X, 0f, CenterWorld.Z);
            model.RotationDegrees = new Vector3(0f, style.LandmarkYawDegrees, 0f);
            model.Scale = new Vector3(scale, scale, scale);
            return;
        }

        // 降级：一个方块，确保「哪间是 Boss/商店」肉眼可辨，且素材缺失也不会崩。
        MeshInstance3D block = new MeshInstance3D
        {
            Name = "LandmarkFallback",
            Mesh = CreateBoxMesh(new Vector3(size * LandmarkFallbackBoxRatio, size * LandmarkFallbackBoxRatio, size * LandmarkFallbackBoxRatio)),
            MaterialOverride = materials.CreateFloorMaterial(Plan.Category, style.WallColor),
            Position = new Vector3(CenterWorld.X, size * LandmarkFallbackBoxRatio * 0.5f, CenterWorld.Z),
        };
        root.AddChild(block);
    }

    /// <summary>降级方块边长 / 目标占地尺寸。</summary>
    private const float LandmarkFallbackBoxRatio = 0.7f; // arch-ok: 几何比例常量（降级方块相对装饰占地）

    /// <summary>门板两侧各让开的墙厚数量（墙厚一半内、一半外）。</summary>
    private const int DoorBlockWallSides = 2; // arch-ok: 几何推导常量（门板两侧各让一个墙厚），非玩法数值

    /// <summary>地标占地尺寸下限（格）：防止数据给 0 导致模型被压成不可见。</summary>
    private const float LandmarkScaleFloorCells = 0.1f; // arch-ok: 数值安全阈值（防零保护），非玩法数值

    // ─────────────────────────────────────────────────────────────
    // 网格生成（两种后端共用同一批 BoxSpec）
    // ─────────────────────────────────────────────────────────────

    private readonly struct BoxSpec
    {
        public readonly Vector3 Center;
        public readonly Vector3 Size;
        public readonly Color Color;

        public BoxSpec(Vector3 center, Vector3 size, Color color)
        {
            Center = center;
            Size = size;
            Color = color;
        }
    }

    private void EmitBoxes(List<BoxSpec> boxes, string nodeName, Material material, GeometryBackend backend)
    {
        if (boxes.Count == 0)
        {
            return;
        }
        if (backend == GeometryBackend.NodePerBox)
        {
            Node3D root = new Node3D { Name = nodeName };
            AddChild(root);
            for (int i = 0; i < boxes.Count; i++)
            {
                BoxSpec spec = boxes[i];
                MeshInstance3D mesh = new MeshInstance3D
                {
                    Name = nodeName + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Mesh = CreateBoxMesh(spec.Size),
                    MaterialOverride = material,
                    Position = spec.Center,
                };
                root.AddChild(mesh);
            }
            return;
        }

        MeshInstance3D single = new MeshInstance3D
        {
            Name = nodeName,
            Mesh = BuildMergedMesh(boxes),
            MaterialOverride = material,
        };
        AddChild(single);
    }

    /// <summary>
    /// 把一批轴对齐盒子拷进一个 <c>ArrayMesh</c>（每个盒子 6 面 × 6 顶点，含法线/UV/切线）。
    /// 单个盒子时直接返回内置 BoxMesh（顶点更省）。
    /// </summary>
    private static Mesh BuildMergedMesh(List<BoxSpec> boxes)
    {
        if (boxes.Count == 1)
        {
            return CreateBoxMesh(boxes[0].Size);
        }
        SurfaceTool st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int b = 0; b < boxes.Count; b++)
        {
            BoxSpec box = boxes[b];
            Vector3 half = box.Size * 0.5f;
            Vector3 min = box.Center - half;
            Vector3 max = box.Center + half;
            for (int f = 0; f < FaceNormals.Length; f++)
            {
                Vector3 n = FaceNormals[f];
                int axis = DominantAxis(n);
                // 取「与 axis 正交的另外两个轴」：轮换 +1 与 +2（等价于 +1 后再 +1）对 3 取模。
                // 这里用 AxisCount 而不是字面量 3；两个 +1 是轴轮换本身，不是可调参数。
                int u = (axis + 1) % AxisCount;
                int v = (u + 1) % AxisCount;
                bool positive = n[axis] > 0f;

                Vector3 p0 = Corner(min, max, axis, u, v, positive, false, false);
                Vector3 p1 = Corner(min, max, axis, u, v, positive, true, false);
                Vector3 p2 = Corner(min, max, axis, u, v, positive, true, true);
                Vector3 p3 = Corner(min, max, axis, u, v, positive, false, true);

                // 绕序保证从法线方向看是逆时针（Godot 以逆时针为正面）。
                AddFaceVertex(st, n, new Vector2(0f, 0f), p0);
                AddFaceVertex(st, n, new Vector2(1f, 0f), p1);
                AddFaceVertex(st, n, new Vector2(1f, 1f), p2);
                AddFaceVertex(st, n, new Vector2(0f, 0f), p0);
                AddFaceVertex(st, n, new Vector2(1f, 1f), p2);
                AddFaceVertex(st, n, new Vector2(0f, 1f), p3);
            }
        }
        st.GenerateTangents();
        return st.Commit();
    }

    private static void AddFaceVertex(SurfaceTool st, Vector3 normal, Vector2 uv, Vector3 position)
    {
        st.SetNormal(normal);
        st.SetUV(uv);
        st.AddVertex(position);
    }

    /// <summary>六面法线表：±X / ±Y / ±Z 各一次（顺序无所谓，每面都独立生成两个三角）。</summary>
    private static readonly Vector3[] FaceNormals =
    {
        Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward,
    };

    /// <summary>求法线的主轴索引：取绝对值最大的那一维。</summary>
    /// <param name="n">单位轴法线（只有一维非零）。</param>
    private static int DominantAxis(Vector3 n)
    {
        // 轴法线只有一个分量为 ±1，其余为 0，所以「绝对值最大的轴」就是主轴。
        int best = 0;
        for (int i = 1; i < AxisCount; i++)
        {
            if (Math.Abs(n[i]) > Math.Abs(n[best]))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>三维空间的轴数（轴轮换取模用）。</summary>
    private const int AxisCount = 3; // arch-ok: 维度常量（坐标轴个数），非玩法数值

    private static Vector3 Corner(Vector3 min, Vector3 max, int axis, int u, int v, bool positive, bool uMax, bool vMax)
    {
        Vector3 p = min;
        p[axis] = positive ? max[axis] : min[axis];
        p[u] = uMax ? max[u] : min[u];
        p[v] = vMax ? max[v] : min[v];
        return p;
    }

    /// <summary>构造一个内置 BoxMesh。</summary>
    /// <param name="size">尺寸（米）。</param>
    public static Mesh CreateBoxMesh(Vector3 size)
    {
        return new BoxMesh { Size = size };
    }

    /// <summary>颜色明暗微调（棋盘格用）：乘上一个系数并夹到 [0,1]。</summary>
    private static Color Shade(Color c, float factor)
    {
        return new Color(
            Math.Clamp(c.R * factor, 0f, 1f),
            Math.Clamp(c.G * factor, 0f, 1f),
            Math.Clamp(c.B * factor, 0f, 1f),
            c.A);
    }
}
