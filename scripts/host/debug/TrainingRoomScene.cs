using System;
using Godot;
using Game.Core.Ecs;
using Game.Data;
using Game.Host;

namespace Game.Host.Debug;

/// <summary>
/// **训练房**：一间灰色房间（四面有墙、地板是棋盘格）+ 一个可操作人物 + 一个站着不动的测试假人。
/// <para>
/// <b>为什么单独一个场景</b>：地牢调试场景要跑整套「布局 → 栅格化 → 物件 → 视图」，一次 12×28 图、
/// 四类物件全上，想安静地看一个人的前后左右、验证移动手感和子弹方向时，满屏都是干扰。
/// 这里只保留最小的一片场地，<b>没有随机、没有生成、每次打开都是同一张图</b>。
/// </para>
/// <para>
/// <b>边界</b>：本类只做「摆场地 + 摆两个东西」，<b>不碰</b> ECS 内核、不碰地牢生成。
/// 人物整块交给 <see cref="CharacterSession"/>（它自己管意图/技能/相机/HUD），
/// 这里 0 行玩法逻辑——所以调移动速度、伤害、血量仍然是改表，不是改这里。
/// </para>
/// <para>
/// <b>为什么假人不是 ECS 实体</b>：当前需求是「站着不动的模型」。真做成实体就要给它
/// <c>Health/SkillState/ActorIntent</c> 一整套组件和系统，那是「可被打的靶子」才需要的形状。
/// 所以假人是纯表现节点（<see cref="Node3D"/> + 两个 <see cref="MeshInstance3D"/>），
/// 子弹会直接穿过去。等要测伤害时，再把它换成 <c>ActorSpawnRequest</c> 生成的真实体。
/// </para>
/// </summary>
public partial class TrainingRoomScene : Node3D
{
    // ─────────────────────────────────────────────────────────────
    // 可在 Inspector 里覆盖的表现参数（都不是玩法数值）
    // ─────────────────────────────────────────────────────────────

    /// <summary>地面根节点名（重建时整棵子树换掉）。</summary>
    public const string GroundRootName = "Ground";

    /// <summary>相机退距相对房间边长的比例。</summary>
    private const float CameraBackFactor = 1.1f; // arch-ok: 表现参数（相机退距比例），非玩法数值

    /// <summary>相机高度相对房间边长的比例。</summary>
    private const float CameraHeightFactor = 1.6f; // arch-ok: 表现参数（相机高度比例），非玩法数值

    /// <summary>材质粗糙度（房间统一哑光，避免高光把灰阶打出斑点）。</summary>
    private const float SurfaceRoughness = 0.9f; // arch-ok: 表现参数（材质粗糙度），非玩法数值

    /// <summary>假人肤色（暖灰，与房间冷灰拉开）。</summary>
    private static readonly Color DummyBodyColor = new(0.78f, 0.72f, 0.64f); // arch-ok: 表现参数（假人配色），非玩法数值

    /// <summary>假人头部颜色（比身体略暗）。</summary>
    private static readonly Color DummyHeadColor = new(0.66f, 0.60f, 0.52f); // arch-ok: 表现参数（假人配色），非玩法数值

    /// <summary>假人身体高度相对格边长的比例。</summary>
    private const float DummyBodyHeightFactor = 1.6f; // arch-ok: 表现参数（假人体型），非玩法数值

    /// <summary>假人身体半径相对格边长的比例。</summary>
    private const float DummyBodyRadiusFactor = 0.32f; // arch-ok: 表现参数（假人体型），非玩法数值

    /// <summary>假人头部半径相对身体半径的比例。</summary>
    private const float DummyHeadRadiusFactor = 0.8f; // arch-ok: 表现参数（假人体型），非玩法数值

    /// <summary>假人部件数（身体 + 头），自检用来确认两个部件都建出来了。</summary>
    private const int DummyPartCount = 2; // arch-ok: 结构计数（身体+头），自检断言用，非玩法数值

    /// <summary>房间四面墙的数量（自检用）。</summary>
    private const int WallCount = 4; // arch-ok: 结构计数（四面墙），自检断言用，非玩法数值

    /// <summary>房间边长（格）。</summary>
    [Export]
    public int RoomCells { get; set; } = 8; // arch-ok: 场景布局参数（格数），由 Inspector 覆盖

    /// <summary>单格边长（米）。</summary>
    [Export]
    public float CellMeters { get; set; } = 1f; // arch-ok: 场景布局参数（格边长），由 Inspector 覆盖

    /// <summary>墙高（米）。棋盘格地板是平的，墙要够高才像「房间」而不是围栏。</summary>
    [Export]
    public float WallHeightMeters { get; set; } = 2.4f; // arch-ok: 表现参数（墙高），由 Inspector 覆盖

    /// <summary>墙厚（米）。</summary>
    [Export]
    public float WallThickness { get; set; } = 0.2f; // arch-ok: 表现参数（墙厚），由 Inspector 覆盖

    /// <summary>地板亮度（0..1）。整个房间是灰的，用它整体调亮/调暗。</summary>
    [Export]
    public float FloorLightness { get; set; } = 0.62f; // arch-ok: 表现参数（灰阶明度），由 Inspector 覆盖

    /// <summary>深色格相对浅色格的明暗系数。</summary>
    [Export]
    public float CheckerShade { get; set; } = 0.72f; // arch-ok: 表现参数（棋盘格明暗系数），由 Inspector 覆盖

    /// <summary>墙色明度（0..1）。比地板暗一点，房间才立得起来。</summary>
    [Export]
    public float WallLightness { get; set; } = 0.34f; // arch-ok: 表现参数（灰阶明度），由 Inspector 覆盖

    /// <summary>人物变体 id（<c>test_hero</c> / <c>test_hero_tank</c> / …）。</summary>
    [Export]
    public string HeroVariantId { get; set; } = CharacterContentSet.HeroDefaultVariantId;

    /// <summary>假人站在房间里距中心多远（格）。</summary>
    [Export]
    public float DummyOffsetCells { get; set; } = 2f; // arch-ok: 场景布局参数（站位偏移），由 Inspector 覆盖

    /// <summary>是否生成 HUD。</summary>
    [Export]
    public bool EnableHud { get; set; } = true;

    /// <summary>是否自检并打印报告（无头环境下唯一的验收证据）。</summary>
    [Export]
    public bool ReportSelfCheck { get; set; } = true;

    // ─────────────────────────────────────────────────────────────
    // 运行时状态
    // ─────────────────────────────────────────────────────────────

    private World? _world;
    private CharacterSession? _character;
    private Camera3D? _camera;
    private Node3D? _dummy;

    /// <summary>自检倒计时（&gt; 0 表示还在等出生落地；0 = 还没排期；&lt; 0 = 已出过报告）。</summary>
    private int _selfCheckFrames = -1;

    /// <summary>假人节点（诊断用；为 null 表示没建出来）。</summary>
    public Node3D? Dummy => _dummy;

    /// <summary>人物会话（诊断用）。</summary>
    public CharacterSession? Character => _character;

    /// <inheritdoc />
    public override void _Ready()
    {
        _camera = GetNodeOrNull<Camera3D>("MainCamera");
        if (_camera is null)
        {
            _camera = new Camera3D { Name = "MainCamera", Current = true };
            AddChild(_camera);
        }

        BuildGround();

        // 人物：整块交给 CharacterSession（组合根），本场景只负责"造一个会话、每帧推一下"。
        // 出生点放在房间靠后一侧，给前面的假人留出射线空间。
        //
        // ★ 这里**不**手动 pipeline.Add 任何系统：CharacterSession 会按变体模块自己拼装
        //   （移动/瞄准/射击/冲刺）并在末尾调 pipeline.Build()。外面再 Add 一次
        //   会让同一个实体被移动两次，且 Build() 之后再 Add 会直接抛异常。
        float half = RoomCells * CellMeters * 0.5f;
        WorldHostOptions worldOptions = new WorldHostOptions();
        _world = new World();
        SystemPipeline pipeline = new SystemPipeline(_world);
        CharacterContentSet content = new CharacterContentSet().BuildBuiltinV1();

        _character = new CharacterSession(
            this,
            _world,
            pipeline,
            content,
            HeroVariantId,
            new System.Numerics.Vector3(0f, 0f, half * 0.5f),
            _camera,
            worldOptions,
            new GameViewOptions(),
            EnableHud);

        BuildDummy();
        FrameCameraToRoom();

        if (ReportSelfCheck)
        {
            // 等出生真的落地再自检：CharacterSession 在第一次 Tick 才把出生请求交给装配系统，
            // 在 _Ready 里立刻数，Player 必然还是空的（会把"正常"误报成"没出生"）。
            _selfCheckFrames = SelfCheckDelayFrames;
        }
    }

    /// <summary>自检前的等待帧数（给出生 + 一帧管线留时间）。</summary>
    private const int SelfCheckDelayFrames = 5; // arch-ok: 自检时序参数，非玩法数值

    /// <summary>
    /// 按房间尺寸摆相机：站到房间南侧上方，俯视整间屋子。
    /// <para>
    /// <b>为什么不写死在 .tscn 里</b>：<see cref="RoomCells"/> / <see cref="CellMeters"/> 都是可在
    /// Inspector 改的，写死的相机在放大房间后会只框住一角。这里让它跟着房间推导，
    /// 「改房间大小」就只需要改一个数字。
    /// </para>
    /// </summary>
    private void FrameCameraToRoom()
    {
        if (_camera is null)
        {
            return;
        }

        float side = RoomCells * CellMeters;
        float half = side * 0.5f;

        // 站到南墙外侧、抬高到能俯视整屋；距离与高度都正比于房间边长。
        float back = side * CameraBackFactor; // arch-ok: 表现参数（相机退距相对房间边长）
        float up = side * CameraHeightFactor; // arch-ok: 表现参数（相机高度相对房间边长）
        _camera.Position = new Vector3(0f, up, half + back);
        _camera.LookAt(new Vector3(0f, 0f, 0f), Vector3.Up);
        _camera.Current = true;
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (_character is not null)
        {
            _character.Tick((float)delta);
        }

        if (_selfCheckFrames > 0)
        {
            _selfCheckFrames--;
            if (_selfCheckFrames == 0)
            {
                EmitSelfCheck();
            }
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        _character?.Dispose();
        _character = null;
    }

    // ─────────────────────────────────────────────────────────────
    // 场地
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 建「灰色房间」：棋盘格地板 + 四面墙。
    /// <para>
    /// 地板按格逐块 <see cref="MeshInstance3D"/>，深/浅灰按 <c>(x + z) % 2</c> 交替——这就是「斑马网格」。
    /// 用<b>逐格</b>而不是一张贴图：格数一变贴图就要重画，逐格只改循环次数，且格线天然对齐。
    /// </para>
    /// </summary>
    private void BuildGround()
    {
        Node3D root = new() { Name = GroundRootName };
        AddChild(root);

        float cell = CellMeters;
        int cells = RoomCells;
        float half = cells * cell * 0.5f;

        StandardMaterial3D lightMat = MakeGrayMaterial(FloorLightness);
        StandardMaterial3D darkMat = MakeGrayMaterial(FloorLightness * CheckerShade);
        StandardMaterial3D wallMat = MakeGrayMaterial(WallLightness);

        // ── 地板：逐格棋盘 ──
        // 稍微薄一点的盒子而不是 PlaneMesh：Plane 是单面的，侧看会消失；盒子从任何角度都对。
        float slabThickness = cell * 0.05f; // arch-ok: 表现参数（地板厚度相对格边长）
        for (int x = 0; x < cells; x++)
        {
            for (int z = 0; z < cells; z++)
            {
                bool dark = ((x + z) & 1) == 1;
                MeshInstance3D slab = new()
                {
                    Name = "Floor_" + x + "_" + z,
                    Mesh = new BoxMesh { Size = new Vector3(cell, slabThickness, cell) },
                    MaterialOverride = dark ? darkMat : lightMat,
                    Position = new Vector3(
                        (-half) + (x * cell) + (cell * 0.5f),
                        -slabThickness * 0.5f,
                        (-half) + (z * cell) + (cell * 0.5f)),
                };
                root.AddChild(slab);
            }
        }

        // ── 四面墙 ──
        // 墙立在地板外侧，长度取「房间全长 + 墙厚」把四个角封住，不留缝。
        float spanFull = (cells * cell) + WallThickness;
        float wallY = WallHeightMeters * 0.5f;
        BuildWall(root, "Wall_North", new Vector3(spanFull, WallHeightMeters, WallThickness),
            new Vector3(0f, wallY, -half - (WallThickness * 0.5f)), wallMat);
        BuildWall(root, "Wall_South", new Vector3(spanFull, WallHeightMeters, WallThickness),
            new Vector3(0f, wallY, half + (WallThickness * 0.5f)), wallMat);
        BuildWall(root, "Wall_West", new Vector3(WallThickness, WallHeightMeters, cells * cell),
            new Vector3(-half - (WallThickness * 0.5f), wallY, 0f), wallMat);
        BuildWall(root, "Wall_East", new Vector3(WallThickness, WallHeightMeters, cells * cell),
            new Vector3(half + (WallThickness * 0.5f), wallY, 0f), wallMat);
    }

    private static void BuildWall(Node3D root, string name, Vector3 size, Vector3 position,
        StandardMaterial3D material)
    {
        MeshInstance3D wall = new()
        {
            Name = name,
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material,
            Position = position,
        };
        root.AddChild(wall);
    }

    /// <summary>灰阶材质：<paramref name="lightness"/> 越小越黑，1 = 白。</summary>
    private static StandardMaterial3D MakeGrayMaterial(float lightness)
    {
        float v = Math.Clamp(lightness, 0f, 1f);
        return MakeSolidMaterial(new Color(v, v, v));
    }

    /// <summary>纯色材质（房间统一走哑光，避免高光把灰阶打出斑点）。</summary>
    private static StandardMaterial3D MakeSolidMaterial(Color albedo)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = albedo,
            Roughness = SurfaceRoughness,
            Metallic = 0f,
        };
    }

    // ─────────────────────────────────────────────────────────────
    // 测试假人
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 建假人：一个立着的胶囊（身体）+ 一个球（头），纯表现节点，不参与 ECS。
    /// <para>
    /// 用几何体而不是 <c>素材/</c> 里的 <c>.glb</c>：按 2026-11-10 的决定「先全部用白模」，
    /// 训练房不该依赖任何外部素材路径——素材库大改也不会把训练房弄挂。
    /// </para>
    /// </summary>
    private void BuildDummy()
    {
        float cell = CellMeters;
        float offset = DummyOffsetCells * cell;
        // CapsuleMesh.Height 是「含两端半球」的总高（不是圆柱段长度）。
        float bodyHeight = cell * DummyBodyHeightFactor;
        float bodyRadius = cell * DummyBodyRadiusFactor;

        Node3D dummy = new() { Name = "TestDummy" };
        AddChild(dummy);
        _dummy = dummy;

        // 假人做成偏暖的浅灰，跟房间的冷灰拉开，一眼能分出"这不是墙"。
        StandardMaterial3D bodyMat = MakeSolidMaterial(DummyBodyColor);
        StandardMaterial3D headMat = MakeSolidMaterial(DummyHeadColor);

        CapsuleMesh body = new()
        {
            Radius = bodyRadius,
            Height = bodyHeight,
        };
        MeshInstance3D bodyNode = new()
        {
            Name = "Body",
            Mesh = body,
            MaterialOverride = bodyMat,
            Position = new Vector3(0f, bodyHeight * 0.5f, 0f),
        };
        dummy.AddChild(bodyNode);

        float headRadius = bodyRadius * DummyHeadRadiusFactor;
        SphereMesh head = new()
        {
            Radius = headRadius,
            Height = headRadius + headRadius, // 球的总高 = 直径（SphereMesh.Height 与 CapsuleMesh 同口径）
        };
        MeshInstance3D headNode = new()
        {
            Name = "Head",
            Mesh = head,
            MaterialOverride = headMat,
            Position = new Vector3(
                0f,
                bodyHeight + headRadius, // 头正好坐在身体顶上，不留缝也不重叠
                0f),
        };
        dummy.AddChild(headNode);

        // 站位：房间靠前一侧，正对出生的玩家。
        dummy.Position = new Vector3(0f, 0f, -offset);
    }

    // ─────────────────────────────────────────────────────────────
    // 自检
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 无头自检：把「房间建出来没有、假人在不在、人物出生没有」写进日志与 <c>user://</c>。
    /// <para>
    /// agent 与 CI 里没有眼睛看画面，「没报错」不等于「东西真的在」——
    /// 这里数的是<b>场景树里真实存在的节点</b>，不是「我写了 Build 所以它一定在」。
    /// </para>
    /// </summary>
    private void EmitSelfCheck()
    {
        Node3D? ground = GetNodeOrNull<Node3D>(GroundRootName);
        int floors = 0;
        int walls = 0;

        if (ground is not null)
        {
            foreach (Node child in ground.GetChildren())
            {
                if (child is not MeshInstance3D mesh)
                {
                    continue;
                }
                if (mesh.Name.ToString().StartsWith("Floor_", StringComparison.Ordinal))
                {
                    floors++;
                }
                else if (mesh.Name.ToString().StartsWith("Wall_", StringComparison.Ordinal))
                {
                    walls++;
                }
            }
        }

        int expectedFloors = RoomCells * RoomCells;
        int expectedWalls = WallCount;
        bool dummyOk = _dummy is not null && _dummy.GetChildCount() == DummyPartCount;
        bool heroOk = _character is not null && _character.Player != Entity.None;
        bool ok = floors == expectedFloors && walls == expectedWalls && dummyOk;

        string summary = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "room={0}x{1} cells cell={2:F1}m floors={3}/{4} walls={5}/{6} dummy={7} parts={8} hero={9} variant={10}",
            RoomCells, RoomCells, CellMeters,
            floors, expectedFloors,
            walls, expectedWalls,
            _dummy is not null,
            _dummy?.GetChildCount() ?? 0,
            heroOk,
            HeroVariantId);
        GD.Print("[训练房 自检] " + summary);

        if (!ok)
        {
            GD.PushWarning("[训练房 自检] 场地不完整：" + summary);
        }
        if (!heroOk)
        {
            GD.PushWarning("[训练房 自检] 人物没有出生（CharacterSession.Player 为空）");
        }

        using FileAccess file = FileAccess.Open("user://training_room_selfcheck.txt", FileAccess.ModeFlags.Write);
        if (file is not null)
        {
            file.StoreString(summary + "\n");
        }
    }
}
