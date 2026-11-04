using System;
using System.Collections.Generic;
using Godot;
using Game.Actor;
using Game.Core;
using Game.Core.Ecs;
using Game.Core.Handles;
using Game.Core.Randomness;
using Game.Host.Bridge;
using Game.Spawn;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 地牢调试主场景：**把 ECS 宿主 + 房间系统 + 视图/建场景一次性装配起来跑通**，并让相机俯视整层。
/// <para>
/// <b>_Ready 顺序</b>：① 读/建 <see cref="DungeonOptions"/>；② <see cref="DungeonLayoutProvider"/>
/// 规划一层（<b>没有规划器时根本不生成地牢</b>，走 <see cref="BuildPlaceholderScene"/> 的「测试地面 + 相机 + 调试文字」）；
/// ③ <see cref="DungeonSceneBuilder"/> 建整层节点；④ 光照 + 相机取景；⑤ ECS 世界、七阶段管线、视图池与桥。
/// </para>
/// <para>
/// <b>为什么规划器是「编译期注入 + 可空」而不是反射</b>：队友的规划器交付后，只需改
/// <see cref="CreateLayoutProvider"/> 里的一行装配即可切换；反射会在运行时才暴露
/// 「类型不存在 / 构造签名变了」，失败点离起因很远。见 <see cref="DungeonLayoutProvider"/>。
/// </para>
/// <para>
/// <b>为什么这里可以 <c>using Godot</c></b>：本文件位于 <c>scripts/host/</c>，是工程内唯一允许接触引擎的边界层（J4）。
/// 玩法数据仍然是纯 C# 值对象（<see cref="Game.Spawn.Rooms.DungeonConfig"/> / <see cref="Game.Spawn.Rooms.DungeonLayout"/> / <see cref="SpawnSettings"/>）。
/// </para>
/// </summary>
public sealed partial class DungeonDebugScene : Node3D
{
    private World? _world;
    private FixedFramePipeline? _fixed;
    private ViewRegistry? _views;
    private TransformBridge? _transformBridge;
    private VisualBridge? _visualBridge;
    private AppEvents? _events;
    private SpawnDirectorSystem? _spawnDirector;
    private Query _actorQuery = null!;

    private Node3D? _dungeonRoot;
    private Camera3D? _camera;
    private DungeonSceneBuilder? _builder;
    private DungeonBuildResult? _result;
    private DungeonLayoutProvider? _provider;
    private DungeonOptions? _dungeonOptions;
    private DungeonRoomStyleSet? _styles;
    private Label3D? _debugLabel;

    private int _frameCounter;
    private long _statsTimer;
    private int _startSeed;
    private bool _wantShots;
    private ShotPhase _shotPhase = ShotPhase.Idle;
    private string _closeupRoomLabel = string.Empty;

    /// <summary>地牢可调参数（可在 Inspector 里挂一个 <c>.tres</c> 覆盖全部默认值）。</summary>
    [Export]
    public DungeonOptions? Options { get; set; }

    /// <summary>配色表（可在 Inspector 里挂 <c>.tres</c>；为空则用内置默认配色）。</summary>
    [Export]
    public DungeonRoomStyleSet? RoomStyles { get; set; }

    /// <summary>ECS 宿主参数（为空则用内置默认值；主要影响冒烟子弹池）。</summary>
    [Export]
    public WorldHostOptions? WorldOptions { get; set; }

    /// <summary>是否生成冒烟子弹（验证 ECS 管线在真实场景里也跑得动）。</summary>
    [Export]
    public bool SpawnSmokeBullets { get; set; } = true;

    /// <summary>是否加载 <c>.glb</c> 中心装饰（headless 时关掉可以省掉网格/贴图解码）。</summary>
    [Export]
    public bool UseLandmarkModels { get; set; } = true;

    /// <summary>是否把相机放到整层俯视（关掉则保留 .tscn 里手摆的相机）。</summary>
    [Export]
    public bool FrameCameraToLayer { get; set; } = true;

    /// <summary>运行满这么多帧后自动退出（0 = 不自动退出）。用于 headless 冒烟与截图脚本。</summary>
    [Export(PropertyHint.Range, "0,100000,1")]
    public int QuitAfterFrames { get; set; }

    /// <summary>
    /// 硬上限帧数：无论发生什么（截图卡住、阶段机未走完）跑够这么多帧就退出。
    /// 0 = 关闭（不建议在 headless/CI 里关闭，否则脚本可能被永久挂住）。
    /// </summary>
    [Export(PropertyHint.Range, "0,100000,1")]
    public int MaxFramesBeforeQuit { get; set; } = MaxFramesBeforeQuitDefault;

    /// <summary>硬上限帧数默认值（约 10 秒 @ 60fps，足够跑完冒烟与截图）。</summary>
    public const int MaxFramesBeforeQuitDefault = 600; // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>截图输出目录（相对工程根或绝对路径）；命令行 <c>--shots</c> 时才真正写盘。</summary>
    [Export(PropertyHint.Dir, "")]
    public string ShotOutputDirectory { get; set; } = ShotDirectoryDefault;

    /// <summary>截图输出目录默认值。</summary>
    public const string ShotDirectoryDefault = "out/dungeon"; // arch-ok: 资源默认值，可被 .tres 覆盖，不是硬编码玩法数值

    /// <summary>全层总览截图文件名。</summary>
    public const string ShotOverviewFileDefault = "dungeon_overview.png"; // arch-ok: 资源默认值，可被 .tres 覆盖

    /// <summary>单间特写截图文件名。</summary>
    public const string ShotRoomFileDefault = "dungeon_room_closeup.png"; // arch-ok: 资源默认值，可被 .tres 覆盖

    /// <summary>
    /// 冒烟时是否注册 <c>LifetimeSystem</c>。
    /// 曾经的 <c>Query&lt;Entity, Lifetime&gt;</c> 校验 bug 已由 Lead 修复，现在默认开启。
    /// </summary>
    [Export]
    public bool IncludeLifetimeSystemSmoke { get; set; } = true;

    /// <summary>
    /// 冒烟时是否启用 <c>VisualBridge.RentMissing</c>。
    /// 曾经的 <c>ForEach&lt;Entity, View&gt;</c> bug 已由 Lead 修复，现在默认开启。
    /// </summary>
    [Export]
    public bool IncludeVisualBridgeSmoke { get; set; } = true;

    /// <summary>启动摘要（headless 冒烟用的「活着且在推进」证据）。</summary>
    public string StartupSummary { get; private set; } = string.Empty;

    /// <summary>最近一次建出来的布局产物（没有规划器时为 null）。</summary>
    public DungeonBuildResult? BuildResult => _result;

    /// <inheritdoc />
    public override void _Ready()
    {
        try
        {
            BuildDungeonOrPlaceholder();
            BuildHost();
        }
        catch (Exception ex)
        {
            GD.PushError("地牢调试场景初始化失败：" + ex);
            return;
        }
        _wantShots = Array.IndexOf(OS.GetCmdlineUserArgs(), ShotArgName) >= 0;
        _statsTimer = (long)Time.GetTicksMsec();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_fixed is null)
        {
            return;
        }
        _fixed.Tick(delta, 1f);
        _frameCounter++;

        if (QuitAfterFrames > 0 && _frameCounter >= QuitAfterFrames)
        {
            QuitNow("到达 QuitAfterFrames=" + QuitAfterFrames);
            return;
        }

        // 硬兜底：不管走哪条路径（截图卡住、阶段机没走完、headless 无渲染等），
        // 跑够 MaxFramesBeforeQuit 一定退出。headless 卡死会挂住整条 CI/校验脚本，
        // 比画面好看与否严重得多——这条兜底优先于一切「优雅收尾」。
        if (MaxFramesBeforeQuit > 0 && _frameCounter >= MaxFramesBeforeQuit)
        {
            QuitNow("到达硬上限 MaxFramesBeforeQuit=" + MaxFramesBeforeQuit);
            return;
        }

        if (_frameCounter % StatsEveryFrames == 0)
        {
            long now = (long)Time.GetTicksMsec();
            double elapsed = now - _statsTimer;
            _statsTimer = now;
            FramePhases phases = _fixed.LastPhases;
            GD.Print("[DungeonDebug] 帧 ", _frameCounter, " tick=", _fixed.TickIndex,
                " 实体=", _world!.EntityCount,
                " 视图在用=", _views!.InUseCount,
                " ECS=", phases.TotalMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), "ms",
                " 间隔=", (elapsed / MsPerSecond).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), "s");
        }

        TryCaptureScreenshots();
    }

    /// <summary>收尾退出：打印摘要并让场景树退出。所有退出路径都走这里，
    /// 保证「为什么退的」一定在日志里留痕（否则 headless 卡住时无从判断）。
    /// </summary>
    /// <param name="reason">退出原因（写进日志）。</param>
    private void QuitNow(string reason)
    {
        GD.Print("[DungeonDebug] 退出：", reason, "（帧 ", _frameCounter, "）");
        PrintSummary();
        GetTree().Quit();
    }

    /// <summary>
    /// 命令行带 <c>--shots</c> 时按阶段截图：总览 → 挪相机 → 特写。
    /// <para>
    /// 关键点：<c>GetTexture().GetImage()</c> 拿到的是<b>上一帧已渲染</b>的画面，
    /// 所以「改相机」与「存图」必须跨帧分开——同一帧里连拍两张会得到两张一模一样的图。
    /// 这里用一个阶段机，每帧只推进一格。
    /// </para>
    /// </summary>
    private void TryCaptureScreenshots()
    {
        if (!_wantShots || _shotPhase == ShotPhase.Done)
        {
            return;
        }
        if (DisplayServer.GetName() == "headless")
        {
            // headless 用 dummy 渲染器：GetTexture().GetImage() 拿不到有效图像。
            // 明确报「不支持」并立刻结束阶段机，而不是静默什么都不做。
            _shotPhase = ShotPhase.Done;
            GD.PrintErr("[DungeonDebug] 截图需要真实渲染：当前 display-driver=headless，"
                + "不产出任何 PNG（请去掉 --headless 再跑 --shots）。");
            return;
        }
        if (_frameCounter < ShotFrame)
        {
            return;
        }

        switch (_shotPhase)
        {
            case ShotPhase.Idle:
                // 这一帧结束后总览画面才在显存里；下一帧再来取。
                _shotPhase = ShotPhase.SaveOverview;
                break;

            case ShotPhase.SaveOverview:
                SaveShot(ShotOverviewFileDefault, "全层总览");
                _shotPhase = MoveCameraForCloseup() ? ShotPhase.SaveCloseup : ShotPhase.Done;
                break;

            case ShotPhase.SaveCloseup:
                SaveShot(ShotRoomFileDefault, "单间特写 " + _closeupRoomLabel);
                _shotPhase = ShotPhase.Done;
                break;
        }

        // 阶段机走完（无论成败）且这是本次唯一任务时直接退出，避免挂住脚本。
        if (_shotPhase == ShotPhase.Done && QuitAfterFrames <= 0)
        {
            QuitNow("截图流程结束");
        }
    }

    /// <summary>把相机挪到一间普通战斗房的俯视位置（下一帧生效，故不能同帧存图）。</summary>
    private bool MoveCameraForCloseup()
    {
        if (_result is null || _dungeonOptions is null)
        {
            return false;
        }
        Camera3D? cam = GetNodeOrNull<Camera3D>("MainCamera") ?? _camera;
        if (cam is null)
        {
            return false;
        }
        if (!TryFocusRoom(cam, out RoomPlan room))
        {
            return false;
        }
        cam.MakeCurrent();
        _closeupRoomLabel = room.Id.ToString();
        return true;
    }

    /// <summary>截图存盘。</summary>
    /// <param name="fileName">文件名。</param>
    /// <param name="label">日志标签。</param>
    private void SaveShot(string fileName, string label)
    {
        string dir = ProjectSettings.GlobalizePath(ShotOutputDirectory);
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string path = dir.PathJoin(fileName);

        Image? image = GetViewport().GetTexture()?.GetImage();
        if (image is null || image.IsEmpty())
        {
            GD.PushWarning("[DungeonDebug] 截图失败（视口无图像）：" + path);
            return;
        }
        Error err = image.SavePng(path);
        if (err != Error.Ok)
        {
            GD.PushWarning("[DungeonDebug] 截图保存失败：" + path + " -> " + err);
            return;
        }
        GD.Print("[DungeonDebug] 已保存截图（", label, "）：", path, " ", image.GetWidth(), "x", image.GetHeight());
    }

    /// <summary>把相机移到某间房的俯视位置，用于特写。</summary>
    /// <param name="cam">相机。</param>
    /// <param name="room">选中的房间。</param>
    private bool TryFocusRoom(Camera3D cam, out RoomPlan room)
    {
        room = default;
        IReadOnlyList<RoomPlan> rooms = _result!.Layout.Rooms;
        if (rooms.Count == 0)
        {
            return false;
        }
        // 优先挑一间有门的普通战斗房：门洞、装饰、内容槽标记都能看到。
        RoomPlan? picked = null;
        for (int i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].Category == RoomCategory.Combat)
            {
                picked = rooms[i];
                break;
            }
        }
        room = picked ?? rooms[rooms.Count - 1];

        float cellSize = _dungeonOptions!.CellSizeMeters;
        int side = room.Footprint.Side;
        Vector3 center = new(
            (room.Cell.X + side * 0.5f) * cellSize,
            0f,
            (room.Cell.Z + side * 0.5f) * cellSize);
        float span = side * cellSize * RoomFocusMargin;
        cam.Projection = Camera3D.ProjectionType.Orthogonal;
        cam.Size = span;
        // 正交相机高度不影响取景，抬高只是为了让墙不被近裁剪面切掉。
        float dist = span * RoomFocusDistanceRatio;
        cam.LookAtFromPosition(center + Vector3.Up * dist, center, Vector3.Forward);
        return true;
    }

    /// <summary>把相机放到「能看全整层」的位置（正交：按包围盒缩放；透视：按 FOV 算距离）。</summary>
    /// <param name="bounds">整层包围盒（米）。</param>
    /// <param name="camera">相机。</param>
    /// <param name="options">可调参数。</param>
    public static void FrameCamera(Aabb bounds, Camera3D camera, DungeonOptions options)
    {
        if (camera is null)
        {
            throw new ArgumentNullException(nameof(camera));
        }
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        Vector3 center = bounds.GetCenter();
        float pitch = Mathf.DegToRad(options.CameraPitchDegrees);
        float yaw = Mathf.DegToRad(options.CameraYawDegrees);
        Vector3 forward = new Vector3(
            Mathf.Cos(pitch) * Mathf.Sin(yaw),
            -Mathf.Sin(pitch),
            Mathf.Cos(pitch) * Mathf.Cos(yaw)).Normalized();

        // 俯视平面上的半径：取包围盒对角线的一半 × 留白系数（任意偏航角都盖得住）。
        float radius = new Vector2(bounds.Size.X, bounds.Size.Z).Length() * 0.5f * options.CameraPaddingFactor;

        float distance;
        if (options.UseOrthographicCamera)
        {
            // 正交：可见高度 = Size。半径是「中心到最远角」，直径 = 2 × 半径，正好盖住整层。
            camera.Projection = Camera3D.ProjectionType.Orthogonal;
            camera.Size = Math.Max(radius * OrthographicDiameterFactor, OrthographicSizeMinMeters);
            distance = bounds.Size.Y + radius;
        }
        else
        {
            camera.Projection = Camera3D.ProjectionType.Perspective;
            camera.Fov = options.CameraFovDegrees;
            double halfFov = Mathf.DegToRad(options.CameraFovDegrees) * 0.5;
            distance = radius / (float)Math.Tan(halfFov) / Math.Max(PerspectiveTiltFloor, Mathf.Sin(pitch));
            distance += bounds.Size.Y * PerspectiveHeightCompensation;
        }

        camera.Position = center - forward * distance;
        camera.LookAt(center, Vector3.Up);
        camera.Far = Math.Max(distance * FarPlaneRatio, FarPlaneMinMeters);
        camera.Near = NearPlaneMeters;
        camera.Current = true;
    }

    // ─────────────────────────────────────────────────────────────
    // 地牢 / 占位场景
    // ─────────────────────────────────────────────────────────────

    private void BuildDungeonOrPlaceholder()
    {
        _dungeonOptions = Options ?? new DungeonOptions();
        _dungeonOptions.Validate();
        _styles = RoomStyles ?? DungeonRoomStyleSet.CreateDefault();
        _styles.Rebuild();
        _startSeed = _dungeonOptions.ResolveSeed((int)(Time.GetTicksMsec() & int.MaxValue));

        ApplyEnvironment(_dungeonOptions);
        _camera = GetNodeOrNull<Camera3D>("MainCamera");
        if (_camera is null)
        {
            _camera = new Camera3D { Name = "MainCamera", Current = true };
            AddChild(_camera);
        }

        // 内容生成器要往命令缓冲里投递命令；这里先用一个**临时世界**，让「规划 + 生成」
        // 早于正式宿主世界跑完（headless 也能验证），命令在这个临时世界上 flush 掉。
        using World staging = new World();
        _provider = CreateLayoutProvider(_dungeonOptions, staging, _startSeed);
        bool ok = _provider.TryBuild(
            _dungeonOptions.ToConfig(),
            _startSeed,
            _dungeonOptions.ToRoomCategoryTable(),
            _dungeonOptions.ContentSlotCapacity,
            _dungeonOptions.DecorCapacity,
            (layout, random) => new Game.Spawn.Rooms.RoomBuildContext(
                layout, random, staging.Commands, _dungeonOptions.CellSizeMeters),
            out DungeonBuildResult result);
        staging.Commands.Flush();

        if (!ok)
        {
            BuildPlaceholderScene();
            StartupSummary = "[DungeonDebug] 无地牢占位场景（规划失败）：相机已就位。原因见上方规划器日志。";
            GD.Print(StartupSummary);
            return;
        }

        _result = result;
        _dungeonRoot = GetNodeOrNull<Node3D>("Dungeon") ?? CreateNode("Dungeon");
        _builder = new DungeonSceneBuilder(_dungeonOptions, _styles);
        bool useLandmarks = UseLandmarkModels && !IsHeadless();
        Aabb bounds = _builder.Build(_dungeonRoot, result, useLandmarks);

        // 门的初始画法：起点房/安全房一侧的门板不落（玩家第一步要能走出去），其余按门自身默认态。
        _builder.ApplyDoorStates(result, door => !IsPlayerFacingRoom(door.Room));

        if (FrameCameraToLayer && _camera is not null)
        {
            FrameCamera(bounds, _camera, _dungeonOptions);
        }

        StartupSummary = result.Describe()
            + " | 种子=" + _startSeed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 格边长=" + _dungeonOptions.CellSizeMeters.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
            + " 包围盒=" + bounds.Size.ToString();
        GD.Print("[DungeonDebug] " + StartupSummary);
        GD.Print("[DungeonDebug] 素材：" + _builder.DescribeAssets());
    }

    /// <summary>
    /// **没有规划器时的占位场景**：一块测试地面 + 网格状标记 + 调试文字，**不伪造任何房间数据**。
    /// 目的只是证明「相机 + 光照 + ECS 宿主」这条链是通的，等规划器交付后由正式布局替换。
    /// </summary>
    private void BuildPlaceholderScene()
    {
        _dungeonRoot = GetNodeOrNull<Node3D>("Dungeon") ?? CreateNode("Dungeon");
        float cell = _dungeonOptions!.CellSizeMeters;
        float side = cell * PlaceholderCellsPerSide;
        StandardMaterial3D floorMat = DungeonMaterialSet.CreateSurface(_dungeonOptions.PlaceholderFloorColor, _dungeonOptions.RoomEmissiveEnergy);
        MeshInstance3D floor = new MeshInstance3D
        {
            Name = "PlaceholderFloor",
            Mesh = DungeonRoomView.CreateBoxMesh(new Vector3(side, cell * PlaceholderFloorThicknessRatio, side)),
            MaterialOverride = floorMat,
            Position = new Vector3(0f, -cell * PlaceholderFloorThicknessRatio * 0.5f, 0f),
        };
        _dungeonRoot.AddChild(floor);

        // 测试标记：每格中心一个方块，便于肉眼确认格边长与相机取景。
        StandardMaterial3D markerMat = DungeonMaterialSet.CreateSurface(_dungeonOptions.PlaceholderMarkerColor, _dungeonOptions.RoomEmissiveEnergy);
        for (int x = 0; x < PlaceholderCellsPerSide; x++)
        {
            for (int z = 0; z < PlaceholderCellsPerSide; z++)
            {
                float size = cell * PlaceholderMarkerRatio;
                MeshInstance3D marker = new MeshInstance3D
                {
                    Name = "PlaceholderMarker" + x + "_" + z,
                    Mesh = DungeonRoomView.CreateBoxMesh(new Vector3(size, size, size)),
                    MaterialOverride = markerMat,
                    Position = new Vector3((x + 0.5f) * cell, size * 0.5f, (z + 0.5f) * cell),
                };
                _dungeonRoot.AddChild(marker);
            }
        }

        _debugLabel = new Label3D
        {
            Name = "DebugText",
            Text = "无地牢：IDungeonPlanner 未接入\n占位测试地面 " + PlaceholderCellsPerSide + "x" + PlaceholderCellsPerSide,
            Position = new Vector3(side * 0.5f, cell * PlaceholderLabelHeightRatio, side * 0.5f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = _dungeonOptions.PlaceholderLabelFontSize,
            Modulate = new Color(1f, 1f, 1f),
        };
        _dungeonRoot.AddChild(_debugLabel);

        if (FrameCameraToLayer && _camera is not null)
        {
            FrameCamera(new Aabb(Vector3.Zero, new Vector3(side, cell, side)), _camera, _dungeonOptions);
        }
    }

    private void ApplyEnvironment(DungeonOptions options)
    {
        WorldEnvironment worldEnv = GetNodeOrNull<WorldEnvironment>("WorldEnvironment") ?? CreateNode<WorldEnvironment>("WorldEnvironment");
        // 显式全名：本文件同时 using System（Exception）与 Godot，裸 Environment 会有二义性。
        Godot.Environment env = worldEnv.Environment ?? new Godot.Environment();
        env.BackgroundMode = Godot.Environment.BGMode.Color;
        env.BackgroundColor = options.VoidColor.Darkened(EnvBackgroundDarken);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = options.AmbientColor;
        env.AmbientLightEnergy = options.AmbientEnergy;
        env.TonemapMode = Godot.Environment.ToneMapper.Filmic;
        worldEnv.Environment = env;

        DirectionalLight3D sun = GetNodeOrNull<DirectionalLight3D>("Sun") ?? CreateNode<DirectionalLight3D>("Sun");
        sun.LightColor = options.SunColor;
        sun.LightEnergy = options.SunEnergy;
        sun.ShadowEnabled = true;
        sun.RotationDegrees = new Vector3(-options.SunPitchDegrees, options.CameraYawDegrees, 0f);
    }

    private bool IsPlayerFacingRoom(Game.Spawn.Rooms.RoomId room)
    {
        Game.Spawn.Rooms.DungeonLayout layout = _result!.Layout;
        if (!layout.Contains(room))
        {
            return false;
        }
        Game.Spawn.Rooms.RoomCategory category = layout[room].Category;
        return category == Game.Spawn.Rooms.RoomCategory.Start || category == Game.Spawn.Rooms.RoomCategory.Safe;
    }

    // ─────────────────────────────────────────────────────────────
    // ECS 宿主装配
    // ─────────────────────────────────────────────────────────────

    private void BuildHost()
    {
        WorldHostOptions worldOptions = WorldOptions ?? new WorldHostOptions();
        worldOptions.Validate();

        _world = new World();
        _events = new AppEvents();
        HostEventSink sink = _events;

        SystemPipeline pipeline = new SystemPipeline(_world);
        pipeline.Add(new MovementSystem(), 0);

        // LifetimeSystem 目前 OnInitialize 里用 Query<Entity, Lifetime>() —— Entity 是隐式标识不是组件，
        // 一旦进管线就会在 Initialize 阶段抛 GameEcsException，并在之后每帧抛 NullReferenceException。
        // 该文件不在宿主侧写权限内（scripts/actor/），已上报；默认不注册，
        // 需要复现时在 Inspector 勾上 IncludeLifetimeSystemSmoke 即可（不改别人的代码）。
        if (IncludeLifetimeSystemSmoke)
        {
            pipeline.Add(new LifetimeSystem(sink, worldOptions.ViewPrewarm), 1);
        }
        else
        {
            GD.Print("[DungeonDebug] 冒烟：已跳过 LifetimeSystem（其 Query<Entity, Lifetime> 校验失败，已上报）。");
        }

        _spawnDirector = new SpawnDirectorSystem(
            new DeterministicRandom(_startSeed),
            SpawnSettings.Create(
                SpawnSmokeBullets ? worldOptions.InitialProjectileCount : 0,
                worldOptions.SpawnRadius,
                worldOptions.SpawnHeight,
                worldOptions.ProjectileLifetimeSeconds,
                worldOptions.ProjectileSpeedMin,
                worldOptions.ProjectileSpeedMax,
                worldOptions.ProjectileScale));
        pipeline.Add(_spawnDirector, 2);
        pipeline.Build();

        CommandFlushSystem flush = new CommandFlushSystem(_world);

        Node3D viewsRoot = CreateNode("Views");
        _views = new ViewRegistry(viewsRoot, BuildFallbackPrefab(_dungeonOptions), worldOptions.ViewPrewarm);
        _visualBridge = new VisualBridge(_views, worldOptions.ProjectileScale);
        _transformBridge = new TransformBridge(
            _world,
            _views,
            _camera?.GlobalPosition ?? Vector3.Zero,
            worldOptions.CullingDistance,
            worldOptions.EnableInterpolation);

        // 视图租借的查询口径与 TransformBridge 保持一致（Transform3 + View）。
        _actorQuery = _world.Query<Transform3, View>();
        VisualBridge? visualBridge = IncludeVisualBridgeSmoke ? _visualBridge : null;
        TransformBridge transformBridge = _transformBridge;

        _fixed = new FixedFramePipeline(
            _world,
            pipeline,
            flush,
            dispatchEvents: DispatchEvents,
            syncViews: alpha =>
            {
                // VisualBridge.RentMissing 目前用 ForEach<Entity, View>（同 LifetimeSystem 的 bug，已上报）。
                // 关掉它时仍然推变换，保证「数据 → 节点」这条路是通的、且能出截图。
                // 先还后租：先回收「实体已死但还占着视图」的槽位，再给新实体分配，
                // 这样同一帧内死掉的实体的视图能被活着的实体复用，池子规模不随帧数上涨。
                if (visualBridge is not null)
                {
                    visualBridge.ReapDead(_world!, _actorQuery);
                }
                visualBridge?.RentMissing(_actorQuery);
                transformBridge.Push(alpha);
                visualBridge?.FlushReleases();
            },
            worldOptions.FixedHz,
            worldOptions.MaxFixedStepsPerFrame);

        if (SpawnSmokeBullets && _spawnDirector is not null)
        {
            _spawnDirector.SpawnBurst(worldOptions.InitialProjectileCount);
            _world.Commands.Flush();
            GD.Print("[DungeonDebug] 冒烟子弹 ", worldOptions.InitialProjectileCount, " 个");
        }
    }

    /// <summary>
    /// 帧末派发：把本帧攒下的事件交给订阅方。
    /// 冒烟场景目前没有订阅方，只做队列清空——保留这个方法是为了让「派发时机归宿主」这条边界不被绕过。
    /// </summary>
    private void DispatchEvents()
    {
        _events!.Dispatch(_ =>
        {
            // 需要联动视图/音效时在这里按事件类型驱动（单向：事件 → 表现）。
        });
    }

    /// <summary>降级子弹预制体：正式素材接入前，用一个自发光方块占位。</summary>
    /// <param name="options">地牢选项（提供占位颜色与自发光强度）；null 时退回新建的默认值。</param>
    private static PackedScene BuildFallbackPrefab(DungeonOptions? options)
    {
        DungeonOptions resolved = options ?? new DungeonOptions();
        MeshInstance3D mesh = new MeshInstance3D
        {
            Name = "Bullet",
            Mesh = DungeonRoomView.CreateBoxMesh(Vector3.One),
            MaterialOverride = DungeonMaterialSet.CreateSurface(resolved.SmokeBulletColor, resolved.SmokeBulletEmissive),
        };
        PackedScene scene = new PackedScene();
        Error err = scene.Pack(mesh);
        if (err != Error.Ok)
        {
            throw new InvalidOperationException("冒烟子弹预制体打包失败：" + err);
        }
        return scene;
    }

    private void PrintSummary()
    {
        if (_world is null)
        {
            return;
        }
        GD.Print("[DungeonDebug] 退出：帧=", _frameCounter,
            " tick=", _fixed?.TickIndex ?? 0,
            " 实体=", _world.EntityCount,
            " 视图在用=", _views?.InUseCount ?? 0,
            " 视图租/还=", _views?.RentCount ?? 0, "/", _views?.ReturnCount ?? 0,
            " 死视图回收=", _visualBridge?.LastReapedCount ?? 0,
            " 实例化次数=", _views?.InstantiateCount ?? 0);
    }

    private Node3D CreateNode(string name)
    {
        Node3D node = new Node3D { Name = name };
        AddChild(node);
        return node;
    }

    private T CreateNode<T>(string name) where T : Node, new()
    {
        T node = new T { Name = name };
        AddChild(node);
        return node;
    }

    private static bool IsHeadless() => DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────
    // 装配点（队友交付后改这里）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 构造布局提供者：**这里是唯一把各层实现装配到一起的地方**。
    /// <para>
    /// 装配的都是「接口 + 注入」：规划器只认 <see cref="IDungeonPlanner"/>，生成器只认
    /// <see cref="IRoomContentSpawner"/>。换实现（换算法、换数据表）只改这个方法，其余代码不动（规则7）。
    /// </para>
    /// <para>
    /// 所有数值都从 <see cref="DungeonOptions"/> 来：格数、半径、生命、表现槽位……代码里只有「装配」没有「数值」。
    /// </para>
    /// </summary>
    /// <param name="options">地牢选项。</param>
    /// <param name="world">生成器要写入的 ECS 世界（只经由 <c>IRoomBuildContext.Commands</c> 投递命令）。</param>
    /// <param name="seed">本局种子（各子系统从这里 Fork 自己的子流）。</param>
    private static DungeonLayoutProvider CreateLayoutProvider(DungeonOptions options, World world, int seed)
    {
        // 每个子系统一条独立随机子流（同 seed 同结果，且互不干扰：改一个系统不会推后另一个的序列）。
        DeterministicRandom root = new DeterministicRandom(seed);

        // 房型表：配了 .tres 就用数据，没配就用规划器自带的默认拓扑（它自己会校验 Start/Boss 是否存在）。
        RoomCategoryTable categories = options.ToRoomCategoryTable() ?? BranchDungeonPlanner.DefaultCategories();

        return new DungeonLayoutProvider(
            planner: new BranchDungeonPlanner(root.Fork(PlannerStreamSalt), categories, options.PlannerMaxAttempts),
            contentPlanner: new GridContentPlanner(root.Fork(ContentStreamSalt), options.ToRoomContentConfig()),
            contentSpawners: new IRoomContentSpawner[]
            {
                new DefaultRoomContentSpawner(world, options.ToContentSpawnConfig()),
            },
            decorLayout: null);
    }

    // ─────────────────────────────────────────────────────────────
    // 结构性常量（相机 / 占位场景；不是玩法数值）
    // ─────────────────────────────────────────────────────────────

    /// <summary>每多少帧打印一次摘要。</summary>
    private const int StatsEveryFrames = 60; // arch-ok: 诊断打印频率（不影响玩法）

    /// <summary>启用截图的命令行开关（<c>-- --shots</c> 的形式传给用户参数）。</summary>
    private const string ShotArgName = "--shots";

    /// <summary>截图阶段机：每帧只推进一格，保证「改相机」与「存图」跨帧生效。</summary>
    private enum ShotPhase
    {
        /// <summary>还没开始。</summary>
        Idle,

        /// <summary>下一帧存全层总览（确保总览已渲染完成）。</summary>
        SaveOverview,

        /// <summary>下一帧存单间特写（相机已挪好并渲染完成）。</summary>
        SaveCloseup,

        /// <summary>结束。</summary>
        Done,
    }

    /// <summary>第几帧开始截图（先让相机与光照稳定一帧）。</summary>
    private const int ShotFrame = 12; // arch-ok: 时序常量（等待渲染稳定的帧数），非玩法数值

    /// <summary>单间特写取景相对房间跨度的放大系数（&gt; 1 留边距）。</summary>
    private const float RoomFocusMargin = 1.25f; // arch-ok: 相机取景系数

    /// <summary>单间特写相机高度 / 取景跨度。</summary>
    private const float RoomFocusDistanceRatio = 1.2f; // arch-ok: 相机取景系数

    /// <summary>毫秒 / 秒。</summary>
    private const double MsPerSecond = 1000.0; // arch-ok: 毫秒→秒单位换算（量纲常数）

    /// <summary>相机远裁剪面 / 取景距离。</summary>
    private const float FarPlaneRatio = 4f; // arch-ok: 相机参数（远裁剪面倍数）

    /// <summary>相机远裁剪面下限（米）。</summary>
    private const float FarPlaneMinMeters = 200f; // arch-ok: 相机参数（远裁剪面下限）

    /// <summary>相机近裁剪面（米）。</summary>
    private const float NearPlaneMeters = 0.1f; // arch-ok: 相机参数（近裁剪面）

    /// <summary>透视取景时 sin(pitch) 的下限（避免贴近正俯视时距离算成极大）。</summary>
    private const float PerspectiveTiltFloor = 0.2f; // arch-ok: 相机取景数值安全阈值

    /// <summary>透视模式按包围盒高度补偿的距离系数。</summary>
    private const float PerspectiveHeightCompensation = 1.5f; // arch-ok: 相机取景补偿系数

    /// <summary>正交相机的可见高度 = 半径 × 该系数（半径是中心到最远角，直径 = 2 倍半径）。</summary>
    private const float OrthographicDiameterFactor = 2f; // arch-ok: 相机取景几何推导（直径=2×半径），非玩法数值

    /// <summary>正交相机可见尺寸下限（米）：防止空场景时 Size 取到 0 导致什么都看不见。</summary>
    private const float OrthographicSizeMinMeters = 1f; // arch-ok: 相机参数安全下限，非玩法数值

    /// <summary>占位测试地面的边长（格）。</summary>
    private const int PlaceholderCellsPerSide = 2; // arch-ok: 占位场景尺寸（无地牢时的测试地面，不是玩法数值）

    /// <summary>占位地面厚度 / 格边长。</summary>
    private const float PlaceholderFloorThicknessRatio = 0.03f; // arch-ok: 几何比例常量（占位地面厚度）

    /// <summary>占位标记尺寸 / 格边长。</summary>
    private const float PlaceholderMarkerRatio = 0.2f; // arch-ok: 几何比例常量（占位标记尺寸）

    /// <summary>占位文字高度 / 格边长。</summary>
    private const float PlaceholderLabelHeightRatio = 0.3f; // arch-ok: 几何比例常量（占位文字高度）

    /// <summary>背景色压暗系数（让地面比天空亮，俯视时房间轮廓更清楚）。</summary>
    private const float EnvBackgroundDarken = 0.25f; // arch-ok: 表现参数（背景色压暗系数）

    /// <summary>
    /// 布局规划器的随机子流盐。
    /// 让「布局」与「内容」用两条互不干扰的随机流：调整内容参数不会让布局整体错位
    /// （否则同 seed 回放会失效，截图/回归都没法比对）。
    /// </summary>
    private const int PlannerStreamSalt = 0x4C41; // arch-ok: 随机子流盐（名字即契约），非玩法数值

    /// <summary>内容规划器的随机子流盐。</summary>
    private const int ContentStreamSalt = 0x434F; // arch-ok: 随机子流盐，非玩法数值
}
