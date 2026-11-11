using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Core.Randomness;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// **整层地牢的建场景器**：读 <see cref="DungeonBuildResult"/>（布局 + 槽位 + 装饰），
/// 在指定的 <see cref="Node3D"/> 下建出全部可视表现。
/// <para>
/// <b>单向</b>（ECS.md §7.1）：只读数据 → 建节点。本类<b>不持有世界、不写组件</b>；
/// 房间视图也只有一个反向口子 <see cref="DungeonRoomView.SetDoorBlocks"/>，用来把「玩法层算出来的开门结果」
/// 画出来（节点只被驱动，永不反向决定逻辑）。
/// </para>
/// <para>
/// <b>规则7 的判据</b>：新增一种房型/装饰 = 在数据里加一行（配色表 / 房型表 / 装饰表），
/// 本文件 0 行改动；新增一种「几何风格」才需要新实现。所以这里没有任何 <c>switch (room.Category)</c>——
/// 只有 <see cref="DungeonRoomStyleSet"/> 的查表。
/// </para>
/// <para>
/// <b>门洞坐标的唯一真源</b>：每个门洞的沿边坐标由 <see cref="DungeonGridMath.TrySharedDoorPosition"/>
/// 算出（两房重叠区中点），本类把它传给两侧房间视图——于是「A 的门」与「B 的门」在世界上严格重合。
/// </para>
/// </summary>
public sealed class DungeonSceneBuilder
{
    private readonly DungeonRoomStyleSet _styles;
    private readonly DungeonRoomGeometry _geometry;
    private readonly DungeonMaterialSet _materials;
    private readonly DungeonLandmarkLoader _landmarks = new();
    private readonly DungeonRoomView.GeometryBackend _backend;

    // 装饰：目录（素材 → 装饰的唯一真源，可选加载）+ 与数据无关的随机源（同种子同摆法）。
    private readonly DungeonDecorCatalog _decorCatalog;

    private readonly List<DungeonRoomView> _rooms = new();
    private readonly List<MeshInstance3D> _decorInstances = new();

    // ── 方砖路径（2D 规则驱动的 3D 关卡；与上面的房间盒子几何互斥）──
    private readonly DungeonOptions _options;
    private readonly BoardGeometry _board;
    private DungeonTileView? _tiles;
    private DungeonCellSet? _cells;
    private TileViewStats _tileStats;

    /// <summary>构造。</summary>
    /// <param name="options">宿主选项（取格边长 / 自发光 / 空隙色）。</param>
    /// <param name="styles">配色表（null → 内置默认）。</param>
    /// <param name="backend">几何后端（默认合并 Mesh）。</param>
    /// <param name="checkerShade">棋盘格明暗系数（&lt; 1 = 稍暗）。</param>
    /// <param name="decorCatalog">装饰目录（null → 尝试从 <see cref="DungeonDecorCatalog.DefaultPath"/> 加载）。</param>
    public DungeonSceneBuilder(DungeonOptions options, DungeonRoomStyleSet? styles,
        DungeonRoomView.GeometryBackend backend = DungeonRoomView.GeometryBackend.MeshFromBoxes,
        float checkerShade = CheckerShadeDefault,
        DungeonDecorCatalog? decorCatalog = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        _styles = styles ?? DungeonRoomStyleSet.CreateDefault();
        _styles.Rebuild();
        _options = options;
        _board = options.ToBoardGeometry();
        _geometry = DungeonRoomGeometry.FromCellSize(options.CellSizeMeters);
        _materials = new DungeonMaterialSet(_styles, options.RoomEmissiveEnergy, checkerShade, options.VoidColor);
        _backend = backend;
        // 可选加载：目录不存在 → 空目录 → 装饰退回 BoxMesh 标记（不崩、不刷屏）。
        _decorCatalog = decorCatalog ?? DungeonDecorCatalog.TryLoad();
    }

    /// <summary>棋盘格明暗系数默认值。</summary>
    public const float CheckerShadeDefault = 0.9f; // arch-ok: 表现参数（明暗系数），不是玩法数值

    /// <summary>建好的房间视图（只读）。</summary>
    public IReadOnlyList<DungeonRoomView> Rooms => _rooms;

    /// <summary>装饰标记节点（诊断）。</summary>
    public IReadOnlyList<MeshInstance3D> DecorInstances => _decorInstances;

    /// <summary>几何参数（相机取景需要）。</summary>
    public DungeonRoomGeometry Geometry => _geometry;

    /// <summary>方砖几何契约（方砖路径生效时非空，人物归一化的基准）。</summary>
    public BoardGeometry Board => _board;

    /// <summary>本次建出来的方砖表（未走方砖路径时为 null）。</summary>
    public DungeonCellSet? Cells => _cells;

    /// <summary>方砖建场景统计（未走方砖路径时为零值）。</summary>
    public TileViewStats TileStats => _tileStats;

    /// <summary>整层实际包围盒（方砖路径按砖数×砖边长算，否则按房间包围盒）。</summary>
    /// <param name="layout">布局。</param>
    public Aabb LayerBounds(in DungeonLayout layout)
        => _cells is not null && _tiles is not null ? _tiles.ComputeBounds(_cells) : ComputeLayerBounds(in layout);

    /// <summary>按房间槽位取视图（越界返回 null）。</summary>
    /// <param name="slot">房间槽位。</param>
    public DungeonRoomView? RoomAt(int slot)
        => slot >= 0 && slot < _rooms.Count ? _rooms[slot] : null;

    /// <summary>
    /// 建整层。
    /// </summary>
    /// <param name="root">父节点（调用方负责生命周期）。</param>
    /// <param name="result">布局产物。</param>
    /// <param name="useLandmarks">是否加载 <c>.glb</c> 中心装饰（headless 冒烟可关）。</param>
    /// <returns>整层包围盒（米），供相机取景。</returns>
    public Aabb Build(Node3D root, DungeonBuildResult result, bool useLandmarks = true)
    {
        if (root is null)
        {
            throw new ArgumentNullException(nameof(root));
        }
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }
        Clear();
        DungeonLayout layout = result.Layout;

        // ── 方砖路径：2D 规则一张图 → 一格一块立方体（用户 2026-11-11 要求的表现形态）──
        // 2D 图由同一份布局 + 同一种子重新栅格化得到（DungeonMapBuilder / DungeonPropPlanner），
        // 于是 3D 看到的障碍/物件/走廊与 2D 地图生成器里看到的是**同一张图**。
        if (_options.UseTileBoard && TryBuildTileBoard(root, in layout, result.Seed, useLandmarks))
        {
            // 方砖路径的包围盒要按砖算（墙高 = 砖边长），不能用房间面积盒（那只有地面板厚）。
            return _tiles is not null && _cells is not null ? _tiles.ComputeBounds(_cells) : ComputeLayerBounds(in layout);
        }

        // ── 背景地面板：让「没有房间的格子」是深色地板而不是虚空，俯视整层时构图完整 ──
        BuildGroundPlate(root, layout);

        // ── 门洞沿边坐标（每扇门在每个房间里各算一次，两侧严格重合） ──
        RoomDoor[] doors = layout.Doors ?? Array.Empty<RoomDoor>();
        Dictionary<int, float>[] doorAlongByRoom = CollectDoorAlongLocals(layout, doors);

        // ── 逐房间 ──
        for (int i = 0; i < layout.RoomCount; i++)
        {
            RoomPlan plan = layout.Rooms[i];
            ReadOnlySpan<RoomDoor> roomDoors = CollectDoors(doors, plan.Id);
            DungeonRoomStyle style = _styles.Get(plan.Category);
            DungeonRoomView view = new DungeonRoomView();
            // 先挂树再建：glb 实例化与 AABB 量测需要节点在树内（GlobalTransform 有效）。
            root.AddChild(view);
            view.Build(in plan, roomDoors, doorAlongByRoom[i], style, _geometry, _backend, _materials,
                useLandmarks ? _landmarks : null);
            _rooms.Add(view);
        }

        // ── 装饰：内容槽（Decor 种类）与装饰布局器产出的装饰件 ──
        BuildDecor(root, result);

        return ComputeLayerBounds(layout);
    }

    /// <summary>
    /// 按布局重算整层包围盒（含空隙与墙体高度）。
    /// </summary>
    /// <param name="layout">地牢图。</param>
    public Aabb ComputeLayerBounds(in DungeonLayout layout)
    {
        float cell = _geometry.CellSize;
        int maxX = 0;
        int maxZ = 0;
        RoomPlan[]? rooms = layout.Rooms;
        if (rooms is not null)
        {
            for (int i = 0; i < rooms.Length; i++)
            {
                maxX = Math.Max(maxX, rooms[i].Cell.X + rooms[i].Footprint.Width);
                maxZ = Math.Max(maxZ, rooms[i].Cell.Z + rooms[i].Footprint.Depth);
            }
        }
        if (maxX <= 0 || maxZ <= 0)
        {
            return new Aabb(Vector3.Zero, new Vector3(cell, _geometry.WallHeight, cell));
        }
        return new Aabb(Vector3.Zero, new Vector3(maxX * cell, _geometry.WallHeight, maxZ * cell));
    }

    /// <summary>
    /// 把「门开/开」的画法同步给视图层。<b>单向</b>：参数由调用方（将来是流程系统）算出，节点只负责画。
    /// </summary>
    /// <param name="result">布局产物。</param>
    /// <param name="isBlocked">判定某扇门是否应当封锁的回调（null = 不判断，保持建场景时的初始状态）。</param>
    public void ApplyDoorStates(DungeonBuildResult result, Func<RoomDoor, bool>? isBlocked)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }
        if (isBlocked is null)
        {
            return;
        }
        RoomDoor[] doors = result.Layout.Doors ?? Array.Empty<RoomDoor>();
        for (int i = 0; i < _rooms.Count; i++)
        {
            DungeonRoomView view = _rooms[i];
            for (int d = 0; d < doors.Length; d++)
            {
                RoomDoor door = doors[d];
                if (door.Room != view.Plan.Id)
                {
                    continue;
                }
                view.SetDoorBlocks(door.LinkSlot, isBlocked(door));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 方砖路径
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 用 2D 地图规则重建一张字符图，再翻译成方砖。
    /// <para>
    /// <b>为什么要重新栅格化</b>：3D 建场景器手上的数据是 <see cref="DungeonLayout"/>（房间/链路/门），
    /// 而障碍、物件、走廊只在 <see cref="DungeonMap"/> 里。<see cref="DungeonMapBuilder"/> 与
    /// <see cref="DungeonPropPlanner"/> 正是「布局 → 那张图」的规则实现，用同一种子重跑一次，
    /// 得到的图与 2D 地图生成器里的**逐格一致**。不复制一份几何、不在两边各写一套摆放规则。
    /// </para>
    /// </summary>
    /// <param name="root">父节点。</param>
    /// <param name="layout">布局。</param>
    /// <param name="seed">种子（与 2D 图同种子 → 同地图）。</param>
    /// <param name="useModels">是否加载 glb。</param>
    /// <returns>是否成功建出方砖（失败时返回 false，调用方退回房间盒子几何）。</returns>
    private bool TryBuildTileBoard(Node3D root, in DungeonLayout layout, int seed, bool useModels)
    {
        try
        {
            DungeonMap map = DungeonMapBuilder.Build(layout, seed, _options.ToMapConfig());
            map = DungeonPropPlanner.Populate(in map, seed, _options.ToPropConfig());
            DungeonCellSet cells = DungeonCellSet.FromMap(map);
            if (cells.Width <= 0 || cells.Height <= 0)
            {
                GD.PushWarning("[方砖] 2D 图为空，退回房间盒子几何。");
                return false;
            }
            DungeonTilePieces pieces = new(_decorCatalog, _landmarks);
            DungeonTileView view = new(_board, _styles, _materials, pieces);
            Aabb bounds = view.Build(root, cells, seed, useModels);
            _cells = cells;
            _tiles = view;
            // ★ 必须在 Build 之后再取统计：Build 内部才算完（结构体是按值传的，早取就是零值）。
            _tileStats = view.Stats;
            GD.Print("[方砖] " + cells + " | 包围盒=" + bounds.Size
                + " 砖边长=" + _board.TileSize.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
                + " | " + _tileStats);
            return true;
        }
        catch (Exception ex)
        {
            GD.PushWarning("[方砖] 建场景失败，退回房间盒子几何：" + ex.Message);
            return false;
        }
    }

    /// <summary>清掉建过的全部节点（重建地牢时调用；幂等）。</summary>
    public void Clear()
    {
        for (int i = 0; i < _rooms.Count; i++)
        {
            DungeonRoomView view = _rooms[i];
            if (GodotObject.IsInstanceValid(view))
            {
                view.QueueFree();
            }
        }
        _rooms.Clear();
        _decorInstances.Clear();
        _tiles?.Clear();
        _tiles = null;
        _cells = null;
        _tileStats = default;
    }

    /// <summary>素材加载统计（诊断）：中心建筑成功/失败、装饰条目可用/降级、装饰标记数。</summary>
    public string DescribeAssets()
    {
        string decor = "装饰目录 可用=" + _decorCatalog.AvailableCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       + " 资源缺失=" + _decorCatalog.SkippedMissingResourceCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       + " 格式错误=" + _decorCatalog.SkippedMalformedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "中心建筑 loaded=" + _landmarks.LoadedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + " failed=" + _landmarks.FailedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + " | " + decor
               + " | 装饰标记=" + _decorInstances.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // ─────────────────────────────────────────────────────────────
    // 内部
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 为每个房间准备「门 Id → 沿边方向局部格坐标」表。
    /// 优先用 <see cref="DungeonGridMath.TrySharedDoorPosition"/>（两房重叠区中点）；
    /// 目标房间不可解析时退化为 <see cref="DungeonGridMath.DoorLocalPosition"/>（该墙居中）。
    /// </summary>
    private static Dictionary<int, float>[] CollectDoorAlongLocals(in DungeonLayout layout, RoomDoor[] doors)
    {
        int roomCount = layout.RoomCount;
        Dictionary<int, float>[] result = new Dictionary<int, float>[Math.Max(roomCount, 1)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Dictionary<int, float>();
        }
        for (int d = 0; d < doors.Length; d++)
        {
            RoomDoor door = doors[d];
            if (!layout.Contains(door.Room))
            {
                continue;
            }
            RoomPlan room = layout[door.Room];
            bool horizontal = door.Direction == RoomDirection.East || door.Direction == RoomDirection.West;
            float along = horizontal
                ? DungeonGridMath.DoorLocalPosition(in room, door.Direction).Z
                : DungeonGridMath.DoorLocalPosition(in room, door.Direction).X;
            if (layout.Contains(door.Target))
            {
                RoomPlan target = layout[door.Target];
                if (DungeonGridMath.TrySharedDoorPosition(in room, in target, door.Direction, out (float X, float Z) localSelf, out _))
                {
                    along = horizontal ? localSelf.Z : localSelf.X;
                }
            }
            result[door.Room.Slot][door.Id.Slot] = along;
        }
        return result;
    }

    private static RoomDoor[] CollectDoors(RoomDoor[] doors, RoomId room)
    {
        int n = 0;
        for (int i = 0; i < doors.Length; i++)
        {
            if (doors[i].Room == room)
            {
                n++;
            }
        }
        if (n == 0)
        {
            return Array.Empty<RoomDoor>();
        }
        RoomDoor[] result = new RoomDoor[n];
        int k = 0;
        for (int i = 0; i < doors.Length; i++)
        {
            if (doors[i].Room == room)
            {
                result[k++] = doors[i];
            }
        }
        return result;
    }

    private void BuildGroundPlate(Node3D root, in DungeonLayout layout)
    {
        Aabb bounds = ComputeLayerBounds(in layout);
        float cell = _geometry.CellSize;
        // 外扩半格，让最外一圈墙的外侧也有地面（否则俯视会看到墙外是虚空）。
        Aabb plate = bounds.Grow(cell * GroundPlateMarginRatio);
        float thickness = _geometry.FloorThickness * PlateThicknessRatio;
        MeshInstance3D mesh = new MeshInstance3D
        {
            Name = "GroundPlate",
            Mesh = DungeonRoomView.CreateBoxMesh(new Vector3(plate.Size.X, thickness, plate.Size.Z)),
            MaterialOverride = _materials.VoidMaterial,
            Position = new Vector3(plate.GetCenter().X, -thickness * 0.5f - _geometry.FloorThickness * 0.5f, plate.GetCenter().Z),
        };
        root.AddChild(mesh);
    }

    private void BuildDecor(Node3D root, DungeonBuildResult result)
    {
        Node3D decorRoot = new Node3D { Name = "Decor" };
        root.AddChild(decorRoot);
        DungeonLayout layout = result.Layout;
        // 装饰摆放必须确定性：同种子 → 同摆法（否则截图对不上、回归无法比对）。
        IRandomProvider random = new DeterministicRandom(result.Seed ^ DecorSeedSalt);

        // ① 内容槽里的「装饰点」：纯表现，不参与碰撞（内容生成器不认领它们，这里只画）。
        ContentSlot[]? slots = result.Slots;
        if (slots is not null)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Kind != ContentSlotKind.Decor || !layout.Contains(slots[i].Room))
                {
                    continue;
                }
                RoomPlan room = layout[slots[i].Room];
                Vector3 world = RoomLocalToWorld(in room, slots[i].LocalX, slots[i].LocalZ);
                SpawnDecorAt(decorRoot, "SlotDecor" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    world, room, random);
            }
        }

        // ② 装饰布局器产出的装饰件：编号→素材的解释留给数据（.tres / 目录），这里只负责摆放。
        DecorPlacement[]? decor = result.Decor;
        if (decor is not null)
        {
            for (int i = 0; i < decor.Length; i++)
            {
                DecorPlacement placement = decor[i];
                if (!layout.Contains(placement.Room))
                {
                    continue;
                }
                RoomPlan room = layout[placement.Room];
                Vector3 world = RoomLocalToWorld(in room, placement.LocalX, placement.LocalZ);
                SpawnDecorAt(decorRoot, "Decor" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    world, room, random, placement.YawDegrees, placement.Scale);
            }
        }
    }

    /// <summary>
    /// 在一点上摆一件装饰。
    /// <para>
    /// <b>目录优先，BoxMesh 兜底</b>：<see cref="DungeonDecorCatalog"/> 里有匹配本房型的条目就用 <c>.glb</c>
    /// （按 <see cref="DungeonDecorEntry.SizeMeters"/> 或量测值归一化到「格边长的固定比例」），
    /// 否则画一个方块标记。**代码里没有任何 glb 路径字面量**——路径全部来自数据。
    /// </para>
    /// </summary>
    private void SpawnDecorAt(Node3D parent, string name, Vector3 world, in RoomPlan room,
        IRandomProvider random, float yawDegrees = 0f, float scale = 1f)
    {
        DungeonDecorEntry? entry = _decorCatalog.IsEmpty ? null : _decorCatalog.Pick(room.Category, random);
        float targetSize = _geometry.CellSize * DecorTargetSizeRatio * Math.Max(DecorScaleFloor, scale);

        if (entry is not null)
        {
            Node3D? model = _landmarks.Instantiate(entry.Path, parent);
            if (model is not null)
            {
                float raw = entry.SizeMeters.LengthSquared() > 0f
                    ? Math.Max(entry.SizeMeters.X, entry.SizeMeters.Z)
                    : _landmarks.MeasureFootprint(model);
                float normalize = raw > 0f ? targetSize / raw : 1f;
                float jitter = Lerp(entry.ScaleMin, entry.ScaleMax, random.NextFloat());
                model.Name = name;
                model.Position = ApplyLayerOffset(world, entry, parent);
                model.RotationDegrees = new Vector3(0f, yawDegrees, 0f);
                model.Scale = new Vector3(normalize * jitter, normalize * jitter, normalize * jitter);
                return;
            }
            // 实例化失败已由加载器告警；这里继续走 BoxMesh 兜底。
        }

        float size = _geometry.CellSize * DecorMarkerSizeRatio * Math.Max(DecorScaleFloor, scale);
        MeshInstance3D mesh = new MeshInstance3D
        {
            Name = name,
            Mesh = DungeonRoomView.CreateBoxMesh(new Vector3(size, size, size)),
            MaterialOverride = _materials.DoorMaterial,
            Position = new Vector3(world.X, size * 0.5f, world.Z),
            RotationDegrees = new Vector3(0f, yawDegrees, 0f),
        };
        parent.AddChild(mesh);
        _decorInstances.Add(mesh);
    }

    /// <summary>
    /// 按装饰层调整竖直/贴墙偏移：Ground 贴地、Wall 抬高到墙高一半并往房间内侧退 <c>clearance</c>、
    /// Ceiling 吊到墙顶之上。**分层是数据（<see cref="DecorLayer"/>），不是硬编码的类别判断。**
    /// </summary>
    private Vector3 ApplyLayerOffset(Vector3 world, DungeonDecorEntry entry, Node3D anchor)
    {
        _ = anchor;
        return entry.Layer switch
        {
            DecorLayer.Wall => new Vector3(world.X, _geometry.WallHeight * DecorWallHeightRatio, world.Z),
            DecorLayer.Ceiling => new Vector3(world.X, _geometry.WallHeight * DecorCeilingHeightRatio, world.Z),
            _ => new Vector3(world.X, 0f, world.Z),
        };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private Vector3 RoomLocalToWorld(in RoomPlan room, float localX, float localZ)
    {
        // 与 RoomBuildContext.ToWorld 完全一致：全局格 × 格边长。
        float cell = _geometry.CellSize;
        return new Vector3((room.Cell.X + localX) * cell, 0f, (room.Cell.Z + localZ) * cell);
    }

    /// <summary>地面板外扩量 / 格边长。</summary>
    private const float GroundPlateMarginRatio = 0.5f; // arch-ok: 几何比例常量（地面板外扩相对格边长）

    /// <summary>地面板厚度 / 地面板厚。</summary>
    private const float PlateThicknessRatio = 0.5f; // arch-ok: 几何比例常量（地面板厚度相对地面板厚）

    /// <summary>装饰标记尺寸 / 格边长（目录为空或素材缺失时的方块标记）。</summary>
    private const float DecorMarkerSizeRatio = 0.12f; // arch-ok: 几何比例常量（装饰标记相对格边长）

    /// <summary>目录装饰的目标水平尺寸 / 格边长。</summary>
    private const float DecorTargetSizeRatio = 0.22f; // arch-ok: 几何比例常量（装饰归一化目标尺寸相对格边长）

    /// <summary>缩放下限（防止数据给 0 把模型压成不可见）。</summary>
    private const float DecorScaleFloor = 0.05f; // arch-ok: 数值安全阈值（不是玩法数值）

    /// <summary>Wall 层装饰高度 / 墙高。</summary>
    private const float DecorWallHeightRatio = 0.55f; // arch-ok: 几何比例常量（挂墙装饰高度相对墙高）

    /// <summary>Ceiling 层装饰高度 / 墙高。</summary>
    private const float DecorCeilingHeightRatio = 1.05f; // arch-ok: 几何比例常量（吊饰高度相对墙高）

    /// <summary>装饰随机种子扰动（与地牢种子异或，让「房间布局种子」与「装饰摆法种子」不耦合）。</summary>
    private const int DecorSeedSalt = 0x5F3D; // arch-ok: 种子扰动常量（不影响玩法数值，只影响摆法）
}
