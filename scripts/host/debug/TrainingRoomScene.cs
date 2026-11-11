using System;
using Godot;
using Game.Actor;
using Game.Combat;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;
using Game.Core.Ecs;
using Game.Data;
using Game.Host;

namespace Game.Host.Debug;

/// <summary>
/// **训练房**：一间灰色房间（四面有墙、地板是棋盘格）+ 一个可操作人物 + 一个站着不动的假人；人物带 HUD。
/// <para>
/// <b>为什么单独一个场景</b>：地牢调试场景要跑整套「布局 → 栅格化 → 物件 → 视图」，一次 12×28 图、
/// 四类物件全上，想安静地看一个人的前后左右、验证移动手感和子弹方向时，满屏都是干扰。
/// 这里只保留最小的一片场地，<b>没有随机、没有生成、每次打开都是同一张图</b>。
/// </para>
/// <para>
/// <b>边界</b>：本类只做「摆场地 + 摆两个东西」，<b>不碰</b> ECS 内核、不碰地牢生成。
/// 人物整块交给 <see cref="CharacterSession"/>（它自己管意图/技能/视图/HUD），
/// 这里 0 行玩法逻辑——所以调移动速度、伤害、血量仍然是改表，不是改这里。
/// </para>
/// <para>
/// <b>假人是真角色</b>（2026-11-11 起）：它和玩家走同一条出生路径 —— 先在内容集里登记一行
/// 「测试假人」变体（<see cref="RegisterDummyVariant"/>），再往<b>玩家所在的那个世界</b>的装配队列
/// 里塞一个生成请求（<see cref="CharacterSession.EnqueueSpawn"/>），阵营覆盖成 <see cref="TeamId.Enemy"/>。
/// 宿主从不直接 <c>World.Create</c>（J5），也不拼第二条管线。
/// </para>
/// <para>
/// <b>白模</b>：两个角色都用占位白模渲染（外观包 <c>ModelPath</c> 为空 → 几何体），
/// <b>不引用任何 <c>.glb</c></b>。子弹同样走 ECS 视图池的白模。
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

    /// <summary>房间四面墙的数量（自检用）。</summary>
    private const int WallCount = 4; // arch-ok: 结构计数（四面墙），自检断言用，非玩法数值

    /// <summary>
    /// 假人变体 id。它是 <c>CharacterContentSet</c> 里的一行——真正的数值（血/甲/速度）在表里，
    /// 这里只是「选哪一行」，与 <see cref="HeroVariantId"/> 同性质。
    /// </summary>
    public const string DummyVariantId = "test_dummy";

    /// <summary>假人的外观包 id（灰色白模，与玩家的蓝色胶囊一眼区分）。</summary>
    public const string DummyVisualPackId = "dummy_gray";

    /// <summary>挂在本会话视图池里的角色数（玩家 + 假人）：自检判定「两个都分到视图」用。</summary>
    private const int ActorCount = 2; // arch-ok: 结构计数（玩家+假人），自检断言用，非玩法数值

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

    /// <summary>是否生成 HUD（复用 <c>CharacterSession</c> 的 <c>HeroHudPanel</c> + <c>CharacterHudDrawer</c>）。</summary>
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
    private Entity _dummy = Entity.None;

    /// <summary>自检倒计时（&gt; 0 表示还在等出生落地；0 = 还没排期；&lt; 0 = 已出过报告）。</summary>
    private int _selfCheckFrames = -1;

    /// <summary>假人实体（诊断用；<see cref="Entity.None"/> 表示还没出生）。</summary>
    public Entity Dummy => _dummy;

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

        _walkActive = WalkThroughWanted();

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
        RegisterDummyVariant(content);

        // 相机不开装置接管：本场景的 MainCamera 在 .tscn 里按房间尺寸摆好（见 FrameCameraToRoom），
        // CharacterSession 只读它做「鼠标 → 世界点」的换算。HUD 照建（复用同一个 HeroHudPanel）。
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
            EnableHud,
            enableCamera: false);

        // 假人：往**同一个世界**的装配队列里塞一行，下一次 Tick 由同一个装配系统生下它。
        _dummy = SpawnDummy(content);

        FrameCameraToRoom();

        if (ReportSelfCheck || _walkActive)
        {
            // 出生是异步的（第一次 Tick 才把请求交给装配系统，下一帧才落地）。
            // 自检 / 走查都不能在 _Ready 里立刻读 Player，否则会把「正常」误报成「没出生」。
            int wait = ReportSelfCheck ? SelfCheckDelayFrames : 0;
            _character.PlayerBorn += entity => OnPlayerBorn(entity, wait);
        }
    }

    private bool _walkActive;

    /// <summary>自检前的等待帧数（给出生 + 一帧管线留时间）。</summary>
    private const int SelfCheckDelayFrames = 5; // arch-ok: 自检时序参数，非玩法数值

    /// <summary>走查的时间轴：走 → 停 → 开火 → 走（秒；最后一段保持到场景结束）。</summary>
    private static readonly TrainingRoomDrive.Keyframe[] WalkTimeline =
    {
        new(0.0f, 0f, 0f, false),
        new(0.2f, 0f, 1f, false),   // 向前走（屏幕上方）
        new(0.9f, 0f, 0f, false),   // 停
        new(1.0f, 0f, 0f, true),    // 按住左键开火
        new(1.6f, 0f, 0f, false),   // 松手
        new(2.0f, 1f, 0f, true),    // 向右横移 + 边打
    };

    /// <summary>走查开始后至少走多少米才算「真的走动了」（判定「没动」的阈值，不是速度）。</summary>
    private const float WalkMinimumMeters = 0.3f; // arch-ok: 自检阈值（位移判定），非玩法数值

    /// <summary>命中判定：假人离哪个世界点这么近就算「子弹打到了它附近」（自检阈值）。</summary>
    private const float DummyHitRadiusMeters = 1.0f; // arch-ok: 自检阈值（命中距离），非玩法数值

    private TrainingRoomDrive? _drive;
    private System.Numerics.Vector3 _walkStartPos;
    private int _walkEndFrames = -1;
    private int _shotsSeen;
    private int _shotsHitDummy;

    /// <summary>
    /// 这次启动要不要跑走查。两条触发途径：
    /// <para>
    /// ① 命令行带 <c>--walk</c>；② <c>user://training_room_walk</c> 存在（跑完即删，不会重复触发）。
    /// </para>
    /// <para>
    /// <b>为什么还要命令行</b>：<c>user://</c> 属于引擎进程的目录，受限环境里本会话写不进去，
    /// 请求文件那条路会静默失效（走查永远不触发、也不报错）。命令行属于进程自己，任何环境都能传。
    /// </para>
    /// </summary>
    private static bool WalkThroughWanted()
    {
        string path = WalkRequestPath;
        if (FileAccess.FileExists(path))
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
            return true;
        }

        foreach (string arg in System.Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, "--walk", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
    /// <summary>走查请求文件（放 <c>user://</c>：不污染仓库，且同一台机器重复跑不会误触发）。</summary>
    public static string WalkRequestPath => "user://training_room_walk";

    /// <summary>请求下一次启动时跑一次无头走查（写请求文件，见 <see cref="WalkRequestPath"/>）。</summary>
    public static void RequestWalkThrough()
    {
        using FileAccess file = FileAccess.Open(WalkRequestPath, FileAccess.ModeFlags.Write);
        file?.StoreString("1");
    }

    /// <summary>
    /// 玩家出生那一刻：玩家自己永远没有输入源（键鼠由 <see cref="PlayerInputIntentSource"/> 驱动，
    /// 而无头环境里没有手），所以这里把「屏幕基方向 → 世界方向」的补位交给会话，并（可选）挂上时间轴。
    /// </summary>
    private void OnPlayerBorn(Entity player, int selfCheckDelayFrames)
    {
        if (_character is null)
        {
            return;
        }

        _character.EnableMoveDriving(player);

        if (_walkActive)
        {
            _drive = new TrainingRoomDrive(_character.InputIntentSource, WalkTimeline);
            _walkStartPos = _character.PlayerPosition();
        }

        if (selfCheckDelayFrames > 0)
        {
            _selfCheckFrames = selfCheckDelayFrames;
        }
    }

    /// <summary>
    /// 在内容集里登记「测试假人」这一行：角色定义 + 变体 + 灰度白模外观。
    /// <para>
    /// 假人<b>复用</b> <c>test_hero</c> 已有的数值包 / 技能包 / 武器行（<c>test_hero_base</c> /
    /// <c>test_hero_kit</c> / <c>pistol_basic</c>）—— 它不是一套新角色，只是同一套东西换了一个阵营。
    /// 真正「假人该有多少血」要调的时候，换 <c>basePackageId</c> 指向一个专门的数值包即可（改数据，不改这里）。
    /// </para>
    /// <para>
    /// <b>为什么不给武器行</b>：假人只站着被打，拿枪反而会在准星方向空射（它没有输入源）。
    /// 空武器行 = <c>WeaponStats</c> 全零，装配路径本来就允许（"没有武器的角色也合法"）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">内置内容集没有 <c>test_hero</c>（说明装配表被改坏了）。</exception>
    private static void RegisterDummyVariant(CharacterContentSet content)
    {
        CharacterAssemblyOf<SkillLoadout> hero = content.Assemblies.Get(CharacterContentSet.HeroDefaultVariantId);
        ActorDefinition? heroVariantDefinition = content.Definitions.Get(content.Definitions.SlotOf(hero.Variant.DefinitionId));
        if (heroVariantDefinition is null)
        {
            throw new InvalidOperationException("内容集里找不到测试人物的角色定义，假人无法复用它的受击盒尺寸。");
        }

        content.Assemblies.RegisterVisualPack(
            new CharacterVisualPack(DummyVisualPackId, string.Empty, 1f, 100, 0, DummyColorHex));
        // 假人跟玩家共用同一套受击盒尺寸（同一行变体度量，不在这里另写数）。
        content.Definitions.Register(new ActorDefinition(
            DummyVariantId,
            string.Concat("actor.", DummyVariantId),
            ActorKind.Monster,
            TeamId.Enemy,
            ControlScheme.Ai,
            new StatPackageIds(hero.Variant.BasePackageId, growth: hero.Variant.GrowthPackageId),
            hero.Variant.LoadoutId,
            heroVariantDefinition.HitRadius,
            heroVariantDefinition.HitHeight,
            heroVariantDefinition.VisualSlot));

        // 先登记装配（假人单独一族，模块只留移动/瞄准：不掉血就不会有别的行为），再挂变体，最后重解一次。
        // 「加一个装配 = 一行数据」——不是新写一个类（规则7）。
        content.Assemblies.RegisterAssembly(new CharacterAssemblySpec(
            DummyVariantId, "训练房假人", new CharacterModuleSet(new[] { "move_standard", "aim_cursor" }), null));
        content.Assemblies.RegisterVariant(DummyVariantId, new CharacterVariantSpec(
            id: DummyVariantId,
            displayName: "训练假人",
            variantTag: "测试对象",
            definitionId: DummyVariantId,
            basePackageId: hero.Variant.BasePackageId,
            growthPackageId: hero.Variant.GrowthPackageId,
            equipmentPackageId: string.Empty,
            loadoutId: hero.Variant.LoadoutId,
            weaponRowId: string.Empty,
            visualPackId: DummyVisualPackId,
            cameraPackId: hero.Variant.CameraPackId,
            inputPackId: hero.Variant.InputPackId,
            modules: CharacterModuleSet.Empty));
        content.Assemblies.Resolve(content);
    }

    /// <summary>
    /// 把假人放进玩家的世界：找到它在内容集里的定义槽位 → 入队 → 返回占位实体号。
    /// <para>
    /// 返回的 <see cref="Entity"/> 是<b>出生前</b>的占位：调用方拿到的是「等它出生的那个号」，
    /// 真正的实体由装配系统创建。所以这里的号只用于日志；出生后的号由自检按
    /// 「世界里除玩家以外的那一个角色实体」反查（不缓存实体号 = 不会缓存一个失效句柄）。
    /// </para>
    /// </summary>
    private Entity SpawnDummy(CharacterContentSet content)
    {
        if (_character is null)
        {
            return Entity.None;
        }

        int definitionSlot = content.Definitions.SlotOf(DummyVariantId);
        if (definitionSlot < 0)
        {
            GD.PushError("[训练房] 内容集里没有假人定义 '" + DummyVariantId + "'，假人不会出生。");
            return Entity.None;
        }

        float offset = DummyOffsetCells * CellMeters;
        var spawn = new ActorSpawnRequest(
            definitionSlot,
            new System.Numerics.Vector3(0f, 0f, -offset),
            teamOverride: TeamId.Enemy);
        _character.EnqueueSpawn(spawn);
        return Entity.None;
    }

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
        // 走查输入：必须先于 Tick 写进意图源（Tick 内部才采样意图）。
        _drive?.Tick((float)delta);

        if (_character is not null)
        {
            _character.Tick((float)delta);
        }

        // 走查采样：每帧记一下子弹（新出现 = 射了一发；落地时离假人够近 = 打中了附近）。
        if (_drive is not null)
        {
            SampleWalk((float)delta);
        }

        if (_selfCheckFrames > 0)
        {
            _selfCheckFrames--;
            if (_selfCheckFrames == 0)
            {
                EmitSelfCheck();
            }
        }

        // 走查时每 60 帧打一行心跳：无头环境里没有截图可看，卡在"时间轴没走完"和
        // "死循环"长一个样 —— 有帧号与时间轴进度才分得清。
        if (_drive is not null && _walkEndFrames == -1 && (_frameCount % 60) == 0)
        {
            GD.Print("[训练房 走查] f=", _frameCount, " t=", _drive.ElapsedSeconds, " finished=", _drive.Finished, " pos=", _character?.PlayerPosition());
        }
        _frameCount++;

        if (_walkEndFrames > 0)
        {
            _walkEndFrames--;
            if (_walkEndFrames == 0)
            {
                EmitWalkReport();
            }
        }
    }

    /// <summary>走查的字符串结果（报告与 <c>user://</c>，截图脚本读它判断过不过）。</summary>
    public static string WalkResultPath => "user://training_room_walk_result.txt";

    private int _lastShotSeen;

    /// <summary>本场景跑过的帧数（诊断心跳用）。</summary>
    private int _frameCount;

    /// <summary>
    /// 走查采样（每帧）：
    /// <list type="number">
    /// <item><b>出新弹</b>：本帧子弹数比上帧多 = 刚才真的射了一发（按住左键会被射击冷却限制成「每 0.15 s 一发」，不在这里数）；</item>
    /// <item><b>打中附近</b>：每颗子弹这一帧第一次离假人这么近，记一次命中判定；</item>
    /// <item><b>结算</b>：时间轴走完后还得等子弹飞完（它们要 1 秒左右才到远处的假人），等够再出报告。</item>
    /// </list>
    /// </summary>
    private void SampleWalk(float deltaSeconds)
    {
        if (_character is null || _world is null)
        {
            return;
        }

        Entity player = _character.Player;
        int shots = 0;
        int hits = 0;
        Entity dummy = _dummy;
        System.Numerics.Vector3 dummyPos = dummy.Equals(Entity.None)
            ? default
            : _world.Get<Transform3>(dummy).Position;
        float radiusSquared = DummyHitRadiusMeters * DummyHitRadiusMeters;

        Query projectiles = _world.Query<Projectile, Transform3>();
        projectiles.ForEach<Projectile, Transform3>((Span<Projectile> ps, Span<Transform3> ts) =>
        {
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].Team != TeamId.Player)
                {
                    continue;
                }
                shots++;
                if (!dummy.Equals(Entity.None)
                    && (ts[i].Position - dummyPos).LengthSquared() <= radiusSquared)
                {
                    hits++;
                }
            }
        });

        if (shots > _lastShotSeen)
        {
            _shotsSeen += shots - _lastShotSeen;
        }
        _lastShotSeen = shots;
        _shotsHitDummy = Math.Max(_shotsHitDummy, hits);

        if (_drive is null || !_drive.Finished || _walkEndFrames != 0)
        {
            return;
        }

        // 时间轴走完：再等一会儿让最后几发子弹飞到假人，然后出报告。
        _walkEndFrames = (int)MathF.Round(WalkSettleSeconds / MathF.Max(deltaSeconds, 0.001f));
        GD.Print("[训练房 走查] 时间轴走完，", WalkSettleSeconds, " s 后结算。");
    }

    /// <summary>时间轴走完后等子弹落地的秒数。</summary>
    private const float WalkSettleSeconds = 1.5f; // arch-ok: 自检时序参数（等弹时间），非玩法数值

    /// <summary>走查报告：位移 / 射出数 / 打中 / 假人是否还在原地（白模不动）。</summary>
    private void EmitWalkReport()
    {
        _walkEndFrames = -1;
        if (_character is null || _world is null)
        {
            return;
        }

        System.Numerics.Vector3 end = _character.PlayerPosition();
        System.Numerics.Vector3 d = end - _walkStartPos;
        float moved = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        bool walked = moved >= WalkMinimumMeters;
        bool fired = _shotsSeen > 0;
        bool hit = _shotsHitDummy > 0;

        System.Numerics.Vector3 dummyPos = _dummy.Equals(Entity.None)
            ? default
            : _world.Get<Transform3>(_dummy).Position;
        float dummyOff = MathF.Sqrt((dummyPos.X * dummyPos.X) + (dummyPos.Z * dummyPos.Z));
        float expectedOffset = DummyOffsetCells * CellMeters;
        bool dummyStill = _dummy.Equals(Entity.None) == false && MathF.Abs(dummyOff - expectedOffset) < WalkMinimumMeters;

        string summary = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "walked={0} moved={1:F2}m from=({2:F2},{3:F2}) to=({4:F2},{5:F2}) shots={6} hitsNearDummy={7} dummyInPlace={8}",
            walked, moved,
            _walkStartPos.X, _walkStartPos.Z, end.X, end.Z,
            _shotsSeen, _shotsHitDummy, dummyStill);
        bool ok = walked && fired && hit && dummyStill;
        GD.Print("[训练房 走查] " + (ok ? "通过 " : "失败 ") + summary);

        using FileAccess file = FileAccess.Open(WalkResultPath, FileAccess.ModeFlags.Write);
        if (file is not null)
        {
            file.StoreString((ok ? "PASS " : "FAIL ") + summary + "\n");
        }
        if (!ok)
        {
            GD.PushWarning("[训练房 走查] 走查未通过：" + summary);
        }

        // 走查是「一次性」的：跑完就退出（无头循环下由截图/CI 脚本用 --quit-after 兜底）。
        if (System.Environment.GetCommandLineArgs() is { } args)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--quit-on-walk", StringComparison.Ordinal))
                {
                    GetTree().Quit(ok ? 0 : 1);
                    return;
                }
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

    /// <summary>假人配色（中灰，跟房间冷灰、玩家蓝都拉开，一眼能分出「这不是墙、也不是我」）。</summary>
    private const string DummyColorHex = "#B8B8B8"; // arch-ok: 表现参数（假人白模配色），非玩法数值

    // ─────────────────────────────────────────────────────────────
    // 自检
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 无头自检：把「房间建出来没有、假人出生没有、两个角色分到视图没有、HUD 在不在」写进日志与 <c>user://</c>。
    /// <para>
    /// agent 与 CI 里没有眼睛看画面，「没报错」不等于「东西真的在」——
    /// 这里数的是<b>世界与场景树里真实存在的实体/节点</b>，不是「我写了 Spawn 所以它一定在」。
    /// 角色「有实体但没视图节点」正是历史上「人物隐形、屏幕上只剩 HUD」的形态，所以视图号也要数。
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

        // 角色：玩家（会话持有）+ 假人（第一个不是玩家的角色实体，见 SpawnDummy 的说明）。
        int heroView = _character?.ViewSlotOf(_character.Player) ?? 0;
        int dummyView = 0;
        if (_character is not null && _world is not null)
        {
            Entity dummy = FirstEnemyActor();
            _dummy = dummy;
            dummyView = _character.ViewSlotOf(dummy);
        }
        bool heroOk = _character is not null && _character.Player != Entity.None;
        bool dummyOk = dummyView > 0;
        bool viewsOk = CountActorGuests() == ActorCount;
        bool hudOk = _character is not null && _character.HudSubmitCount > 0;

        bool ok = floors == expectedFloors && walls == expectedWalls && dummyOk && heroOk && viewsOk && hudOk;

        string summary = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "room={0}x{1} cells cell={2:F1}m floors={3}/{4} walls={5}/{6} hero={7} view={8} dummy={9} dummyView={10} actors={11}/{12} hud={13}/{14}",
            RoomCells, RoomCells, CellMeters,
            floors, expectedFloors,
            walls, expectedWalls,
            heroOk,
            heroView,
            dummyOk,
            dummyView,
            CountActorGuests(), ActorCount,
            hudOk,
            _character?.HudSubmitCount ?? 0);
        GD.Print("[训练房 自检] " + summary);

        if (!ok)
        {
            GD.PushWarning("[训练房 自检] 场景不完整：" + summary);
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

    /// <summary>
    /// 世界里第一个「阵营 = 敌方」的角色实体（训练房里就是那只假人；没有返回 <see cref="Entity.None"/>）。
    /// <para>
    /// 为什么按阵营数而不是缓存出生后的实体号：实体号由装配系统在出生时才确定，
    /// 场景里缓存一个「以为会是它」的号，等于赌系统实现不变——换个装配顺序就变成查一个不存在的实体。
    /// </para>
    /// </summary>
    private Entity FirstEnemyActor()
    {
        if (_world is null)
        {
            return Entity.None;
        }
        Entity found = Entity.None;
        Query enemies = _world.Query<Team, ActorIdentity>();
        enemies.ForEach<Team, ActorIdentity>((Span<Team> teams, Span<ActorIdentity> identities) =>
        {
            for (int i = 0; i < teams.Length; i++)
            {
                if (teams[i].Value == TeamId.Enemy)
                {
                    found = identities[i].Owner;
                    return;
                }
            }
        });
        return found;
    }

    /// <summary>数本场景视图池（<c>Views</c> 根节点）下当前可见的角色视图节点数（幽灵节点 / 没分到视图都会让这个数不对）。</summary>
    private int CountActorGuests()
    {
        Node3D? views = GetNodeOrNull<Node3D>("Views");
        if (views is null)
        {
            return 0;
        }
        int count = 0;
        foreach (Node child in views.GetChildren())
        {
            if (child is Node3D node && node.Visible && node.GetChildCount() > 0)
            {
                count++;
            }
        }
        return count;
    }
}
