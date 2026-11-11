using System;
using System.Collections.Generic;
using Game.Actor;
using Game.Combat;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Handles;
using Game.Core.Randomness;
using Game.Core.Skills;
using Game.Data;
using Game.Host.Bridge;
using Game.Host.Composition;
using Game.Host.Control;
using Game.Host.Presentation;
using Game.Skill;
using Game.UI;
using Godot;

namespace Game.Host;

/// <summary>
/// 一个人物的**运行时装配根**：把「数据层的装配清单」变成「世界里能跑、屏幕上有 HUD、相机跟着走」的角色。
/// <para>
/// <b>这是本项目第一个真正意义上的「组合根」</b>。之前 <c>DungeonDebugScene</c> 只跑了内核冒烟（子弹飞来飞去），
/// 玩家角色从未真正出生。这里要回答三个问题：
/// <list type="number">
/// <item><b>角色怎么出生</b>：<c>CharacterContentSet.Assemblies</c> 里的一行变体 → <c>ActorSpawnRequest</c>
/// → <see cref="ActorSpawnQueue"/> → <see cref="ActorAssemblySystem"/>（PreUpdate）→ 实体。宿主不直接
/// 碰 <c>World.Create</c>（J5）。</item>
/// <item><b>为什么组件能跑起来</b>：变体的模块集合（<c>CharacterModuleSet</c>）决定往管线里
/// <c>AddModule</c> 哪些系统 —— 移动/瞄准/射击是**按需拼上去的**，不是硬编码序列。</item>
/// <item><b>为什么加一个变体只用改数据</b>：本类只在构造函数里读一遍装配清单，其余每帧逻辑对"哪个变体"
/// 完全无感。加 <c>test_hero_lucky</c> 的时候这里 0 行改动（规则7）。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么它不是 Node</b>：它组合的是"节点（相机/准星/HUD）+ 纯数据（世界/管线）"两半，
/// 做成 Node 会让生命周期与场景树纠缠（谁先 _Ready、谁负责 _ExitTree）。这里改成显式
/// <see cref="Tick"/> / <see cref="Dispose"/>，由 <c>DungeonDebugScene</c> 在固定位置调用，
/// 顺序一目了然，也让"这帧到底做了什么"可以在日志里一行一行读出来。
/// </para>
/// <para>
/// <b>引擎边界</b>：本文件在 <c>scripts/host/</c>，是唯一允许 <c>using Godot</c> 的地方（J4）。
/// 世界、管线、系统全部来自 <c>scripts/core|actor|combat|skill</c>，它们不认识 Godot。
/// </para>
/// </summary>
public sealed class CharacterSession : IDisposable
{
    private readonly Node3D _owner;
    private readonly World _world;
    private readonly CharacterContentSet _content;
    private readonly CharacterAssemblyOf<SkillLoadout> _assembly;
    private readonly GodotCameraProvider _cameraProvider;
    private readonly GameViewOptions _viewOptions;

    private readonly ActorSpawnQueue _spawnQueue = new ActorSpawnQueue();
    private readonly ActorIntentSystem _intentSystem;
    /// <summary>宿主侧技能包集：把数据行翻译成真技能（数据层只能放占位）。</summary>
    private readonly ContentSkillSet _skillSet = new ContentSkillSet();
    private readonly SkillLoadoutRegistry _runtimeLoadouts = new SkillLoadoutRegistry();
    private readonly SkillLoadout _playerLoadout;
    private readonly StatSnapshotTable _stats;
    private readonly ActorStatReader _statReader;

    private SystemPipeline? _pipeline;
    private ActorAssemblySystem? _assemblySystem;
    private SkillCastSystem? _skillCast;
    private SessionSkillContext? _skillContext;
    private ImpactEventSystem? _impacts;
    private HitDetectionSystem? _hitDetection;
    private ProjectileSystem? _projectileSystem;
    private DashSystem? _dashSystem;
    private MoveAimSystem? _moveAim;
    private AimAtCursorSystem? _aimAtCursor;
    private FixedFramePipeline? _fixed;
    private TransformBridge? _transformBridge;
    /// <summary>本会话的视图池（与 <see cref="VisualBridge"/> / <see cref="ActorViewDirector"/> 同一个实例）。</summary>
    private ViewRegistry? _views;
    private VisualBridge? _visualBridge;
    private Query _viewQuery = null!;
    /// <summary>
    /// 「<c>View</c> + <c>ViewAppearance</c>」的自有查询（与 <see cref="_viewQuery"/> 分开）。
    /// <para>
    /// <c>ActorViewDirector.ApplyAll</c> 内部走的是 <c>ForEach&lt;View, ViewAppearance&gt;</c>，而 <c>ForEach</c>
    /// 只在**本查询匹配的 archetype** 里找这两列。拿 <c>Transform3 + View</c> 的查询去调，逐个 archetype
    /// 都被跳过 —— 实体明明有外观组件，视图节点却永远是个空壳（症状：人物看不见，屏幕上只剩 HUD 准星）。
    /// </para>
    /// </summary>
    private Query _appearanceQuery = null!;
    private WarningEventSink? _eventSink;
    private TopDownCameraRig? _cameraRig;
    /// <summary>下一帧把相机硬贴到玩家身上（一次性；见 <see cref="SnapCameraToPlayer"/>）。</summary>
    private bool _snapCameraOnce;
    private ActorViewDirector? _viewDirector;
    private HeroCrosshair? _crosshair;
    private HeroHudPanel? _hudPanel;
    private CursorAimInputSource? _cursorAim;

    private Entity _player = Entity.None;
    private bool _spawnRequested;
    private HudSnapshot _hud;
    private bool _disposed;
    private int _framesSinceSpawn;
    private int _hudSubmits;
    private int _castsTotal;
    private int _rejectsTotal;
    private float _elapsedSeconds;

    /// <summary>构造一个人物会话（不生成实体；第一次 <see cref="Tick"/> 时才入队）。</summary>
    /// <param name="owner">宿主节点（用于挂相机/准星/HUD 等子节点）。</param>
    /// <param name="world">ECS 世界（由 <c>DungeonDebugScene</c> 拥有）。</param>
    /// <param name="pipeline">已 Add 过基础系统、<b>尚未 Build</b> 的管线。</param>
    /// <param name="content">角色内容集（= 数据层运行时副本）。</param>
    /// <param name="variantId">要生成哪个变体（<c>test_hero</c> / <c>test_hero_tank</c> / …）。</param>
    /// <param name="spawnPosition">出生点（世界坐标，米）。</param>
    /// <param name="camera">俯视相机节点（可为 null：headless 或 .tscn 没相机时自动建一个）。</param>
    /// <param name="worldOptions">ECS 宿主参数（固定步长/视图池/剔除距离）。</param>
    /// <param name="viewOptions">表现参数（可从 Inspector 覆盖）。</param>
    /// <param name="enableHud">是否创建 HUD（headless 冒烟可关）。</param>
    /// <param name="enableCamera">
    /// 是否新建俯视相机装置。默认 <c>true</c>（现有调用方行为不变）；
    /// 训练房这类「相机由场景自己摆在 .tscn 里」的场景传 <c>false</c>，否则相机会被装置接管、
    /// 场景里那把相机形同虚设（且视角突然变成跟随视角）。
    /// </param>
    public CharacterSession(
        Node3D owner,
        World world,
        SystemPipeline pipeline,
        CharacterContentSet content,
        string variantId,
        System.Numerics.Vector3 spawnPosition,
        Camera3D? camera,
        WorldHostOptions? worldOptions = null,
        GameViewOptions? viewOptions = null,
        bool enableHud = true,
        bool enableCamera = true)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _viewOptions = viewOptions ?? new GameViewOptions();
        _assembly = _content.Assemblies.Get(variantId);
        SpawnPosition = spawnPosition;

        // ① 属性表：装配期把"折叠后的最终属性"缓存起来，HUD 与技能门控都读它（运行期零查表）。
        _stats = new StatSnapshotTable(_content.StatPacks);
        _statReader = new ActorStatReader(entity => _stats.ViewOf(entity));

        // ② 技能：数据层只给"哪一槽装哪个技能 id"，实例化是组合根的活（见 ISkillFactory 的说明）。
        _playerLoadout = BuildRuntimeLoadout();
        // ★ 槽位号必须是"内容集仓库里的那个槽位" —— <see cref="ActorAssemblySystem"/> 装配时
        // 把 LoadoutId 解析成 Content.Loadouts 的槽位号写进 SkillState.LoadoutSlot，
        // 而 SkillCastSystem 用的正是这个号。这里先镜像整个仓库，再用真技能包**原地替换**同 id 的那一项：
        // 槽位号保持不变，角色指向的技能包却从 DataSkill 占位变成了真有实现的版本。
        // （只注册自己那一个技能包会让槽位号整体错位，技能系统查不到任何技能 —— 静默不施放。）
        for (int slot = 0; slot < _content.Loadouts.Count; slot++)
        {
            SkillLoadout? existing = _content.Loadouts.Get(slot);
            if (existing is not null)
            {
                _runtimeLoadouts.Register(existing);
            }
        }
        _skillSet.PublishTo(_runtimeLoadouts);

        // ③ 意图：鼠标瞄准源**包住**键位源（键位映射只有一处，见 PlayerInputIntentSource）。
        _cameraProvider = new GodotCameraProvider(owner, camera);
        InputIntentSource = new PlayerInputIntentSource();
        _cursorAim = new CursorAimInputSource(
            _viewOptions,
            _cameraProvider.GetCamera,
            _cameraProvider.GetViewport,
            InputIntentSource);
        _intentSystem = new ActorIntentSystem(_cursorAim, NullIntentSource.Instance, NullIntentSource.Instance);

        // ④ ECS 系统：按变体声明的模块拼进管线（本方法结束时管线已经 Build 过）。
        //    世界 / 管线 / 意图源 / 装配队列是**注入**的：同一份内容集上新开的人物（例如训练房的假人）
        //    把这些原样传进来即可，自己不必再拼第二套系统 —— 一个世界一帧只跑一条管线。
        Install(world, pipeline, worldOptions ?? new WorldHostOptions(), enableCamera);

        // ⑤ 表现：相机 / 准星 / HUD。headless 下它们同样被创建（代价很小），
        //    这样"表现层有没有崩"在 CI 里也能跑到。
        BuildPresentation(camera, enableHud, enableCamera);
    }

    // ─────────────────────────────────────────────────────────────
    // 公开只读状态（日志、截图脚本、调试面板用）
    // ─────────────────────────────────────────────────────────────

    /// <summary>本会话正在跑的变体 id。</summary>
    public string VariantId => _assembly.Id;

    /// <summary>装配 id（如 <c>hero</c>）。</summary>
    public string AssemblyId => _assembly.Spec.Id;

    /// <summary>玩家实体（首帧之后有效；未出生前为 <see cref="Entity.None"/>）。</summary>
    public Entity Player => _player;

    /// <summary>玩家出生点。</summary>
    public System.Numerics.Vector3 SpawnPosition { get; }

    /// <summary>HUD 最近一次推送的快照（调试与截图断言用）。</summary>
    public HudSnapshot Hud => _hud;

    /// <summary>玩家出生后经过了多少帧（-1 = 还没出生）。</summary>
    public int FramesSinceSpawn => _spawnRequested ? _framesSinceSpawn : -1;

    /// <summary>HUD 已推送次数（headless 断言用：主循环每帧应 +1）。</summary>
    public int HudSubmitCount => _hudSubmits;

    /// <summary>底层键位意图源（调试与测试可以据此模拟按键）。</summary>
    public PlayerInputIntentSource InputIntentSource { get; }

    /// <summary>已装配的模块名（日志用：证明"系统是按模块拼上去的"）。</summary>
    public IReadOnlyList<string> ModuleNames => _pipeline?.ModuleNames ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>
    /// 给某个实体接上「WASD / 方向键 → 移动」：意图源读移动轴，这里把屏幕基方向按<b>当前相机</b>旋成世界方向，
    /// 写进它的 <see cref="MoveIntent"/>。
    /// <para>
    /// <b>为什么在宿主</b>：输入语义（右/前）→ 世界向量要相机基，相机在宿主层（J4）。系统层只认得世界向量。
    /// </para>
    /// <para>
    /// <b>为什么每帧覆盖而不是只在有输入时写</b>：没输入时必须写零，否则会「松手后一直往原方向滑到底」。
    /// 没调过本方法的实体完全不受影响（它的 <c>MoveIntent</c> 由别的来源负责）。
    /// </para>
    /// <para>
    /// <b>与 <see cref="PlayerInputIntentSource"/> 的分工</b>：玩家实体的移动由它自己按 WASD 拼出屏幕基方向，
    /// 再走 <c>ActorIntentSystem → MoveAimSystem</c>；本方法是「同一个实体的意图已经在 <c>ActorIntent</c> 里，
    /// 只差世界方向」的补位，供小范围脚本驱动（训练房）使用。
    /// </para>
    /// </summary>
    /// <param name="entity">要驱动的角色实体（不在世界里 = 空操作）。</param>
    public void DriveMove(Entity entity)
    {
        if (entity.Equals(Entity.None) || !_world.Has<ActorIntent>(entity))
        {
            return;
        }

        ActorIntent intent = _world.Get<ActorIntent>(entity);
        // Q16 → float。1/65536 是定点表示的还原系数（不是玩法数值）。
        const float FixedToFloat = 1f / 65536f; // arch-ok: Q16 定点还原系数
        float x = intent.MoveX * FixedToFloat;
        float y = intent.MoveY * FixedToFloat;

        System.Numerics.Vector3 world = System.Numerics.Vector3.Zero;
        if (x * x + y * y > 0f)
        {
            // 屏幕基：右 = 相机右向量，前 = 相机「前」在地面上的投影（俯视下就是屏幕上方）。
            Godot.Vector3 right = _cameraProvider.GetCamera()?.GlobalBasis.X ?? Godot.Vector3.Right;
            Godot.Vector3 forward = _cameraProvider.GetCamera()?.GlobalBasis.Z ?? Godot.Vector3.Back;
            forward.Y = 0f;
            right.Y = 0f;
            forward = forward.Normalized();
            right = right.Normalized();
            Godot.Vector3 dir = (right * x) + (forward * y);
            System.Numerics.Vector3 d = new(dir.X, 0f, dir.Z);
            float len = d.Length();
            world = len > 0f ? d / len : System.Numerics.Vector3.Zero;
        }

        MoveIntent move = _world.Get<MoveIntent>(entity);
        move.Velocity = world;
        move.Magnitude = world.Length();
        _world.Set(entity, move);
    }

    /// <summary>本变体的模块集合（日志/验收用）。</summary>
    public CharacterModuleSet Modules => _assembly.Modules;

    /// <summary>命中事件累计。</summary>
    public int ImpactsSeen => _impacts?.ConsumedCount ?? 0;

    /// <summary>技能成功施放次数累计。</summary>
    /// <summary>累计成功施放次数（跨帧累加，与 <c>SkillCastSystem.CastCount</c> 的单帧值区分）。</summary>
    public int CastCount => _castsTotal;

    /// <summary>累计被拒绝次数（冷却/法力/无技能）。</summary>
    public int RejectCount => _rejectsTotal;

    /// <summary>本变体的武器行（没有配武器时为 null）。</summary>
    public CharacterWeaponSpec? Weapon => _assembly.Weapon;

    /// <summary>本变体已实例化的技能包（HUD 冷却从这里读）。</summary>
    public SkillLoadout PlayerLoadout => _playerLoadout;

    /// <summary>底层固定步长管线（调试与截图脚本可以读相位耗时）。</summary>
    public FixedFramePipeline? FramePipeline => _fixed;

    // ─────────────────────────────────────────────────────────────
    // 装配
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 把一个角色生成请求放进本会话所属世界的装配队列（本会话下一次 <see cref="Tick"/> 时出生）。
    /// <para>
    /// 给「往同一个世界再放一个角色」用（训练房的假人）：调用方<b>不</b>碰 <c>World.Create</c>（J5），
    /// 也不新拼一条管线 —— 生成仍然走 <c>ActorSpawnQueue → ActorAssemblySystem</c>，
    /// 与玩家出生是同一条代码路径（等价性），差异只在数据行。
    /// </para>
    /// </summary>
    /// <param name="spawn">生成请求（定义槽位 + 位置 + 可选阵营覆盖）。</param>
    public void EnqueueSpawn(in ActorSpawnRequest spawn) => _spawnQueue.Enqueue(spawn);

    /// <summary>
    /// 一个实体当前的视图号（<c>View.Slot</c>；还没租到视图时为 0 / <c>View</c> 组件不存在）。
    /// <para>诊断与自检用：宿主世界里的「没分到视图的活实体」正是「看不见」的机器可读证据。</para>
    /// </summary>
    public int ViewSlotOf(Entity entity)
    {
        if (entity.Equals(Entity.None) || !_world.Has<View>(entity))
        {
            return 0;
        }
        return _world.Get<View>(entity).Slot;
    }

    /// <summary>视图节点（诊断用；<c>null</c> = 该视图号没有节点）。</summary>
    public Node3D? ViewNodeOf(Entity entity)
    {
        int slot = ViewSlotOf(entity);
        return slot > 0 ? _views?.GetNode(slot) : null;
    }

    private void Install(World world, SystemPipeline pipeline, WorldHostOptions worldOptions, bool enableCamera)
    {
        _pipeline = pipeline;
        CharacterModuleSet modules = _assembly.Modules;

        // ① 意图采集：把"外部输入"变成 ActorIntent 组件（玩家与怪物同一条路径）。
        pipeline.Add(_intentSystem, CharacterSessionRail.Intent);

        // ② 角色装配：队列里的生成请求 → 实体。武器数值与外观都由宿主侧数据源喂进去，
        //    装配系统本身（Game.Actor）不认识 CharacterContentSet（Game.Data）。
        _assemblySystem = new ActorAssemblySystem(
            _content.Definitions,
            _runtimeLoadouts,
            _content.StatPacks,
            new RepositoryPackageSlotResolver(_content.StatPacks),
            _spawnQueue,
            _intentSystem,
            new ContentWeaponStatsSource(_content),
            new ContentActorDecorSource(_content));
        pipeline.Add(_assemblySystem, CharacterSessionRail.Assembly);

        // ③ 技能施放：门控 → 施放 → 扣法力。技能实例已由工厂造好并注册进运行时技能包仓库。
        _eventSink = new WarningEventSink();
        _skillContext = new SessionSkillContext(_owner, world, _eventSink, id => _stats.ViewOf(new Entity(id, 0)));
        _skillCast = new SkillCastSystem(_runtimeLoadouts, _skillContext);
        pipeline.Add(_skillCast, CharacterSessionRail.Skill);

        // ④ 移动与瞄准：两个系统都靠 IMoveStatReader/IAimStatReader 读属性；
        //    没声明对应模块的变体（例如纯摆件）就不会被拼上来。
        if (modules.Contains(CharacterModules.Move))
        {
            _moveAim = new MoveAimSystem(_statReader, _statReader);
            pipeline.AddModule(CharacterModules.Move, _moveAim);
        }

        if (modules.Contains(CharacterModules.Aim))
        {
            _aimAtCursor = new AimAtCursorSystem(_statReader);
            pipeline.AddModule(CharacterModules.Aim, _aimAtCursor);
        }

        // ⑤ 射击：推进 / 命中检测 / 命中事件三件套一起上，缺一不可。
        //    注意：推进用的是 ProjectileSystem（hero-1），不是 main 的 ProjectileAdvanceSystem ——
        //    后者按契约只管"飞"，碰撞/伤害刻意不做；本项目当前的可玩闭环需要"会飞的子弹也能打中并结算"，
        //    所以由 ProjectileSystem 独占推进 + 三条死法（命中 / 射程 / 寿命）。
        //    两者**不能同时注册**，否则同一颗子弹被推进两次（速度翻倍、射程判断提前）。
        //    若将来切到契约路线：删掉 ProjectileSystem，并把命中检测接到 ProjectileAdvanceSystem 的到期产物上。
        if (modules.Contains(CharacterModules.Fire))
        {
            _projectileSystem = new ProjectileSystem();
            HitBuffer hits = new HitBuffer();
            _hitDetection = new HitDetectionSystem(hits);
            _impacts = new ImpactEventSystem(hits, _eventSink, _skillContext, new ImpactSettings(string.Empty));
            pipeline.AddModule(
                CharacterModules.Fire,
                _projectileSystem,
                _hitDetection,
                _impacts);
        }

        // ⑤ 冲刺：把 DashState 翻译成 Velocity。
        //    **无条件注册**，不挂 CharacterModules：DashSystem 只在实体身上真有 DashState 时才做事
        //    （查询本身就筛掉了全部普通帧），因此对"从不冲刺的变体"是纯零成本 —— 比给数据层
        //    再加一个模块位（等于逼全世界策划去填 Excel 才能保持能跑）划算得多。
        //    谁有资格冲刺由**数据**决定：技能包那个槽位是不是 kind=dash（SkillLoads）。没配就没人挂得上
        //    DashState，系统空转，行为与今天完全一致 —— 这就是规则7 的「新增一种能力不惊动其他变体」。
        _dashSystem = new DashSystem();
        pipeline.AddModule(CharacterModules.Dash, _dashSystem);

        // ⑥ 收口：所有系统就位后才初始化（OnInitialize 里会建查询）。
        pipeline.Build();

        // ⑦ 视图池与桥：实体 → 节点。视图池的预制体是一个空壳，外观由 ActorViewDirector 往里塞。
        Node3D viewsRoot = EnsureViewsRoot();
        ViewRegistry views = new ViewRegistry(viewsRoot, BuildViewShellPrefab(), worldOptions.ViewPrewarm);
        _views = views;
        _visualBridge = new VisualBridge(views, worldOptions.ProjectileScale);
        _transformBridge = new TransformBridge(
            world,
            views,
            _cameraProvider.GetCamera()?.GlobalPosition ?? Godot.Vector3.Zero,
            worldOptions.CullingDistance,
            worldOptions.EnableInterpolation);
        _viewDirector = new ActorViewDirector(views, _viewOptions);
        _viewDirector.Register(_assembly.Visual.VisualSlot, BuildViewStyle(_assembly.Visual));

        _viewQuery = world.Query<Transform3, View>();
        _appearanceQuery = world.Query<View, ViewAppearance>();

        CommandFlushSystem flush = new CommandFlushSystem(world);
        _fixed = new FixedFramePipeline(
            world,
            pipeline,
            flush,
            DispatchEvents,
            SyncViews,
            worldOptions.FixedHz,
            worldOptions.MaxFixedStepsPerFrame);
    }

    private Node3D EnsureViewsRoot()
    {
        Node3D? existing = _owner.GetNodeOrNull<Node3D>("Views");
        if (existing is not null)
        {
            return existing;
        }
        Node3D root = new Node3D { Name = "Views" };
        _owner.AddChild(root);
        return root;
    }

    /// <summary>移动驱动对象（见 <see cref="DriveMove"/>；空 = 没人要求本会话补世界方向）。</summary>
    private readonly System.Collections.Generic.List<Entity> _moveDriven = new(4); // arch-ok: 容量提示，不是玩法数值

    /// <summary>要求本会话每帧把某实体的移动输入旋成世界向量（训练房驱动玩家用；见 <see cref="DriveMove"/>）。</summary>
    public void EnableMoveDriving(Entity entity)
    {
        if (!entity.Equals(Entity.None) && !_moveDriven.Contains(entity))
        {
            _moveDriven.Add(entity);
        }
    }

    private void DriveMoves()
    {
        for (int i = 0; i < _moveDriven.Count; i++)
        {
            DriveMove(_moveDriven[i]);
        }
    }

    /// <summary>帧末派发：把本帧攒下的玩法事件交给订阅方（v1 只清队列，表现层按需扩展）。</summary>
    private void DispatchEvents()
    {
        // v1：表现层事件暂由 WarningEventSink 直接打日志消费，这里无需再派发。
    }

    /// <summary>视图桥阶段：先还后租，再推变换（顺序错了会看到同一帧内视图错位）。</summary>
    private void SyncViews(float alpha)
    {
        _visualBridge?.ReapDead(_world, _viewQuery);
        _visualBridge?.RentMissing(_viewQuery);
        // 外观必须在本帧「租出之后、推变换之前」套：新实体的 View.Slot 此刻才分配好，
        // 晚一帧才套外观的话，第一帧会看到一个空壳（没有网格 = 看不见）。
        _viewDirector?.ApplyAll(_world, _appearanceQuery);
        _transformBridge?.Push(alpha);
        _visualBridge?.FlushReleases();

        // ★ 可见性最后统一落盘：上面每个环节都只登记「我想要它可见/隐藏」，这里才真正写 Visible。
        //   顺序无关后，「被剔除的实体因为别处置了一次 true 而永远可见」这类幽灵节点就不会再出现。
        _views?.ApplyVisibility();

    }

    /// <summary>第一次 <see cref="Tick"/> 时投出玩家生成请求。</summary>
    /// <remarks>
    /// <b>为什么出生不在构造函数里</b>：生成实体是结构变更（J5），必须发生在管线的装配阶段；
    /// 构造函数跑在 <c>pipeline.Build()</c> 之后、但世界还没 tick 过，那时入队也可以 ——
    /// 只是"入队时机"和"实体出现时机"差了一帧，日志更容易读（先看 [Hero] variant=…，再看 [Hero] 出生）。
    /// </remarks>
    public void RequestSpawn()
    {
        if (_spawnRequested)
        {
            return;
        }
        _spawnRequested = true;
        _spawnQueue.Enqueue(new ActorSpawnRequest(_assembly.DefinitionSlot, SpawnPosition));
        GD.Print(
            "[Hero] variant=", VariantId,
            " assembly=", AssemblyId,
            " modules=", _assembly.Modules.ToLogString(),
            " definitionSlot=", _assembly.DefinitionSlot,
            " weapon=", _assembly.Weapon?.Id ?? "(none)");
    }

    // ─────────────────────────────────────────────────────────────
    // 每帧
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 跑一帧：ECS 管线（含视图桥）+ 表现层（相机 / 准星 / HUD）。
    /// <para>
    /// 分工：管线负责"角色怎么动、怎么打"；本方法后半段负责"相机怎么跟、准星摆哪、HUD 推什么"。
    /// 表现层只读世界、不写世界（J5 在视图侧的对应约束）。
    /// </para>
    /// </summary>
    /// <param name="deltaSeconds">本帧实际时长（秒）。</param>
    /// <param name="timeScale">时间缩放（调试慢放用；1 = 正常）。</param>
    public void Tick(float deltaSeconds, float timeScale = 1f)
    {
        if (_disposed || _fixed is null)
        {
            return;
        }

        _elapsedSeconds += deltaSeconds;

        // ① 出生：第一次 Tick 时把请求交给装配系统（本帧的 PreUpdate 就会变成实体）。
        if (!_spawnRequested)
        {
            RequestSpawn();
        }

        // ② 鼠标 → 世界点：先喂玩家位置（瞄准源按屏幕基换算），再让管线消费意图。
        if (_cursorAim is not null)
        {
            _cursorAim.SetPlayerContext(PlayerPositionGodot());
        }

        // ②b 移动驱动：把 ActorIntent 里本会话该管的屏幕基方向按相机旋成世界向量，写回 MoveIntent。
        //     必须是意图源（PreUpdate）写完之后、管线读之前；本帧才出生的实体用出生帧意图（装配系统已补过一次）。
        DriveMoves();

        // ③ ECS 一帧（PreUpdate → FixedUpdate×N → Update → LateUpdate → 命令提交 → 事件派发 → 视图桥）。
        _fixed.Tick(deltaSeconds, timeScale);

        // ④ 实体可能本帧才出生：出生后才有位置、才有属性。
        ResolvePlayer();

        Godot.Vector3 playerPos = ToGodot(PlayerPosition());

        // ⑤ 相机：跟玩家；玩家还没出生时跟出生点（避免第一帧相机在地图外）。
        if (_cameraRig is not null)
        {
            Godot.Vector3 aimDir = _cursorAim is not null ? _cursorAim.AimDirection : Godot.Vector3.Forward;
            if (_snapCameraOnce)
            {
                // 特写截图用：请求方在拍照前调用 SnapCameraToPlayer()，让相机**立刻**贴到玩家身上，
                // 而不是等 FollowLerp 收敛（收敛要十几帧，拍出来还是旧构图 —— hero_closeup 与
                // dungeon_overview 几乎同图那次事故的根因）。
                _snapCameraOnce = false;
                _cameraRig.SnapTo(playerPos, aimDir);
            }
            else
            {
                _cameraRig.Tick(playerPos, aimDir, deltaSeconds);
            }
        }

        // ⑥ 准星：落在鼠标的世界点上（这个世界点同时也是子弹会飞过去的方向）。
        if (_crosshair is not null && _cursorAim is not null)
        {
            if (_cursorAim.HasWorldPoint)
            {
                _crosshair.PlaceAt(_cursorAim.WorldPoint, true);
            }
            else
            {
                _crosshair.HideCrosshair();
            }
        }

        // ⑦ 外观：按 ViewAppearance 组件给视图池里的节点换模型/配色（只改已是池内的节点）。
        _viewDirector?.ApplyAll(_world, _viewQuery);

        // ⑧ 累计施放统计（SkillCastSystem 的计数器每帧归零，这里把它们累加成这一局共开了几枪）。
        if (_skillCast is not null)
        {
            _castsTotal += _skillCast.CastCount;
            _rejectsTotal += _skillCast.RejectCount;
        }

        // ⑨ HUD：每帧一次快照。数值全部来自装配期缓存的属性表，不做结算。
        BuildHudSnapshot();
        _hudPanel?.Submit(_hud);
        _hudSubmits++;

        if (_spawnRequested)
        {
            _framesSinceSpawn++;
        }
    }

    /// <summary>
    /// 冒烟用：让玩家持续"瞄准 +X 并按住左键"。
    /// <para>
    /// <b>为什么写在意图源里而不是直接写组件</b>：<c>ActorIntentSystem</c> 每帧用意图源的采样结果
    /// <u>整体覆盖</u> <c>ActorIntent</c>，在管线之前写进去的值会被抹掉。这里改成"伪造一次键鼠采样"，
    /// 下游（技能门控 / 子弹生成 / HUD）全部走真实路径 —— 于是 headless 与截图脚本也能证明
    /// "按下左键 → 出子弹"这条链是通的，而它证明的东西和真的按一下左键是同一件事。
    /// </para>
    /// </summary>
    public void PressPrimaryForSmoke()
    {
        if (_player == Entity.None)
        {
            return;
        }
        InputIntentSource.ForceAimAndPrimary(Godot.Vector3.Forward with { Z = 1f, X = 1f, Y = 0f });
    }

    /// <summary>停止冒烟开火，恢复正常键鼠采样。</summary>
    public void StopSmokeFire() => InputIntentSource.ForceInput(null);

    /// <summary>
    /// 请求"下一帧把相机硬贴到玩家身上"。
    /// <para>
    /// <b>为什么需要这个</b>：相机平时走 <see cref="TopDownCameraRig.Tick"/> 的指数收敛（手感要求，
    /// 不能一帧瞬移）。但截图流程里没有"等几秒让镜头稳定"的余地 —— 只等几帧的话画面还停在旧构图上，
    /// 于是角色特写与总览几乎一模一样（这是实际发生过的事故）。<see cref="TopDownCameraRig.SnapTo"/>
    /// 只影响被显式要求的那一帧，不改变跟随参数，因此是"给截图用的一次性瞬移"而非手感调整。
    /// </para>
    /// </summary>
    public void SnapCameraToPlayer()
    {
        _snapCameraOnce = true;
    }

    /// <summary>释放：解绑输入回调、清空内部表（节点由场景树负责销毁）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_cursorAim is not null)
        {
            _cursorAim.WorldPointChanged -= OnWorldPointChanged;
        }
        _viewDirector?.ClearStyles();
        _viewDirector = null;
    }

    // ─────────────────────────────────────────────────────────────
    // 内部：实体
    // ─────────────────────────────────────────────────────────────

    private void ResolvePlayer()
    {
        if (_player != Entity.None)
        {
            return;
        }

        // 实体是装配系统本帧造出来的 —— 以「它交付的那个实体」为准，而不是反过来按属性表找。
        // 属性快照在出生后立刻绑定（见 BindStats）。
        if (_assemblySystem is null || _assemblySystem.LastAssembled.Count == 0)
        {
            return;
        }

        Entity owner = _assemblySystem.LastAssembled[_assemblySystem.LastAssembled.Count - 1];
        _player = owner;
        BindStats(owner);
        System.Numerics.Vector3 pos = PlayerPosition();
        // 出生时玩家有没有 View 组件 + 视图池租了几次 —— 「人物隐形」的唯一机器证据就在这两项（应为 True / > 0）。
        GD.Print("[Hero] 出生 entity=", owner.Index, ":", owner.Generation,
            " hasView=", _world.Has<View>(owner),
            " rentedViews=", _visualBridge?.LastRentedCount ?? -1,
            " pos=", pos.X.ToString("F2"), ",", pos.Y.ToString("F2"), ",", pos.Z.ToString("F2"),
            " 血量上限=", _stats.ViewOf(owner)?.Get(StatId.HpMax).ToString("F0"));
        PlayerBorn?.Invoke(owner);
    }

    /// <summary>
    /// 玩家实体刚出生（绑定属性表之后立刻触发一次）。
    /// <para>宿主场景用它做「出生后才可能做的事」（例如把无头走查的时间轴交给这个实体），
    /// 这样调用方不必轮询「出生了没有」，也不会拿到一个出生前的占位号。</para>
    /// </summary>
    public event Action<Entity>? PlayerBorn;

    /// <summary>把实体的属性快照登记进表（装配期已经折叠过，这里只是取视图，不做结算）。</summary>
    private void BindStats(Entity owner)
    {
        // 组件里存的是"五层数值包的槽位号"（J2：组件必须 unmanaged，不能放 id 字符串）。
        // 但装配时的包 id 就是装配清单自己给的 —— 直接用它 Compose，不必把槽位号再翻回来。
        // DefinitionSlot 就是 ActorDefinitionRegistry 的槽位号，仓库按它取到定义行再给包 id。
        ActorDefinition? definition = _content.Definitions.Get(_assembly.DefinitionSlot);
        if (definition is null)
        {
            GD.PushWarning("[Hero] 找不到角色定义槽位 ", _assembly.DefinitionSlot, "，属性表未绑定。");
            return;
        }
        _stats.Bind(owner, definition.StatPackageIds, _assembly.DefinitionSlot);
    }

    /// <summary>
    /// 玩家当前位置（世界坐标，米）；还没出生时返回出生点。
    /// <para>公开是给宿主场景（走查/截图）用的只读读数 —— 表现层不反写玩法状态。</para>
    /// </summary>
    public System.Numerics.Vector3 PlayerPosition()
    {
        if (_player != Entity.None && _world.TryGet(_player, out Game.Core.Ecs.Transform3 t))
        {
            return t.Position;
        }
        return SpawnPosition;
    }

    /// <summary>玩家位置（Godot 侧表示，供表现层使用）。</summary>
    private Godot.Vector3 PlayerPositionGodot() => ToGodot(PlayerPosition());

    /// <summary>玩家世界的当前位置（Godot 坐标，截图与调试面板用）。</summary>
    public Godot.Vector3 PlayerWorldPosition() => PlayerPositionGodot();

    /// <summary>
    /// 探针专用：把人物瞬移到指定位置（世界米）。
    /// <para>
    /// 用来制造<b>真实位移</b>，从而让「相机跟没跟上」成为可判定的命题。
    /// 人物静止时，"跟随"与"不跟随"的观测值完全相同 —— 那种测法是假绿。
    /// </para>
    /// </summary>
    /// <param name="position">目标位置（世界米）。</param>
    /// <returns>是否成功写入了人物实体。</returns>
    public bool MovePlayerForProbe(System.Numerics.Vector3 position)
    {
        if (_player == Entity.None || !_world.TryGet(_player, out Game.Core.Ecs.Transform3 t))
        {
            return false;
        }

        t.Position = position;
        _world.Set(_player, t);
        return true;
    }

    /// <summary>
    /// 只读：当前相机装置（探针/调试用）。相机没建出来时为 <c>null</c>。
    /// <para>
    /// 暴露它是为了让「相机真的跟着人物」这件事**可被引擎内的自动化探针验证**。
    /// 纯函数测试只能证明弹簧数学对，证明不了 <see cref="Tick"/> 里那条调用链接对了；
    /// 而这条链路一旦断了（漏 Tick 一次、传错 focus、aim 恒为零），画面依旧能跑、日志依旧干净。
    /// </para>
    /// </summary>
    public TopDownCameraRig? CameraRigForProbe => _cameraRig;

    private void OnWorldPointChanged(Godot.Vector3 point)
    {
        // 鼠标世界点由 CursorAimInputSource 提供，AimTarget 组件由 AimAtCursorSystem 消费；
        // 这里只是订阅点（保持"表现层只读世界"的纪律：不在这里写组件）。
        _ = point;
    }

    // ─────────────────────────────────────────────────────────────
    // 内部：技能
    // ─────────────────────────────────────────────────────────────

    private SkillLoadout BuildRuntimeLoadout()
    {
        string loadoutId = _assembly.Variant.LoadoutId;
        if (string.IsNullOrEmpty(loadoutId))
        {
            GD.PushWarning("[Hero] 变体 ", _assembly.Id, " 没有配技能包，技能槽将为空。");
            return new SkillLoadout(_assembly.Id + "_empty");
        }

        // 技能行（Excel SkillLoads）只说「哪个槽位装哪个 skillId / 什么种类」，
        // 「种类 → 实现类」的绑定由组合根完成（见 ContentSkillSet / ContentSkillFactory）。
        var rows = new List<CharacterSkillSpec>(CharacterSkillSpecs.SlotCount);
        for (int slotNumber = CharacterSkillSpecs.MinSlotNumber; slotNumber <= CharacterSkillSpecs.MaxSlotNumber; slotNumber++)
        {
            CharacterSkillSpec? row = _content.SkillRowOf(loadoutId, slotNumber);
            if (row is not null)
            {
                rows.Add(row.Value);
            }
        }

        SkillLoadout? built = _skillSet.Build(loadoutId, rows, out List<int> skipped);
        if (built is null)
        {
            GD.PushWarning("[Hero] 技能包 ", loadoutId, " 没有任何可用技能。");
            return new SkillLoadout(loadoutId);
        }

        if (skipped.Count > 0)
        {
            GD.PushWarning("[Hero] 技能包 ", loadoutId, " 跳过了 ", skipped.Count,
                " 行（槽位非法或种类未实现）：", string.Join(",", skipped));
        }

        GD.Print("[Hero] 技能包 ", loadoutId, " 实例化 ", CountSkills(built),
            " 个真技能（工厂=", _skillSet.Factory.GetType().Name, "）");
        return built;
    }

    /// <summary>技能包里有几个技能（五个固定槽里非空的个数）。</summary>
    private static int CountSkills(SkillLoadout loadout)
    {
        int count = 0;
        for (int slotNumber = CharacterSkillSpecs.MinSlotNumber; slotNumber <= CharacterSkillSpecs.MaxSlotNumber; slotNumber++)
        {
            if (loadout.Get((SkillSlot)slotNumber) is not null)
            {
                count++;
            }
        }
        return count;
    }

    private void BuildHudSnapshot()
    {
        _hud.EnsureSkills();

        IReadOnlyStatView? view = _stats.ViewOf(_player);
        bool hasCharacter = _player != Entity.None && view is not null;

        _hud.HasCharacter = hasCharacter;
        _hud.CharacterName = _assembly.Variant.DisplayName;
        _hud.VariantTag = _assembly.Variant.VariantTag;
        _hud.Level = _assembly.VariantIndex + 1;
        _hud.FrameSeconds = _elapsedSeconds;

        if (view is null)
        {
            return;
        }

        _world.TryGet(_player, out Health health);
        _world.TryGet(_player, out Mana mana);

        _hud.Health = new HudBar(health.Current, health.Max);
        _hud.Mana = new HudBar(mana.Current, mana.Max);
        _hud.Stamina = new HudBar(view.Get(StatId.Stamina), view.Get(StatId.StaminaMax));

        float armor = view.Get(StatId.Armor);
        float armorK = view.Get(StatId.ArmorK);
        _hud.Armor = armor;
        // 减伤比例与战斗层同一口径：armor / (armor + k)。分母为 0（两侧都没声明）时视为无减伤。
        // 注意是 armor 在分子、k 在分母常数项：k 越大越防高，armor=6/k=100 → 5.7%。
        _hud.ArmorReduction = armor + armorK > 0f ? armor / (armor + armorK) : 0f;

        _hud.MoveSpeed = view.Get(StatId.MoveSpeed);
        _hud.CritChance = view.Get(StatId.CritChance);
        _hud.CritMultiplier = view.Get(StatId.CritMultiplier);

        HudSkillSlot[] slots = _hud.Skills ?? System.Array.Empty<HudSkillSlot>();
        for (int i = 0; i < slots.Length && i + 1 < SkillSlotExtensions.TableLength; i++)
        {
            SkillSlot slot = (SkillSlot)(i + 1);
            ISkill? skill = _playerLoadout.Get(slot);
            slots[i] = new HudSkillSlot(
                hasSkill: skill is not null,
                ready: _playerLoadout.IsReady(slot),
                cooldownProgress: _playerLoadout.CooldownProgress(slot),
                remainingSeconds: _playerLoadout.CooldownRemaining(slot),
                glyph: HudGlyphs.ForSlot(slot),
                keyLabel: HudGlyphs.KeyLabelForSlot(slot));
        }

        _hud.AimAngleDeg = AimAngleDegrees();
    }

    private float AimAngleDegrees()
    {
        if (_world.TryGet(_player, out Game.Core.Ecs.Transform3 t))
        {
            // 朝向用 Rotation 的偏航角；+Z 为 0°，+X 为 90°（与 MoveAimSystem.LookTowards 一致）。
            System.Numerics.Vector3 forward = System.Numerics.Vector3.Transform(
                System.Numerics.Vector3.UnitZ,
                t.Rotation);
            return MathF.Atan2(forward.X, forward.Z) * DegreesPerRadian;
        }
        return 0f;
    }

    // ─────────────────────────────────────────────────────────────
    // 内部：表现
    // ─────────────────────────────────────────────────────────────

    private void BuildPresentation(Camera3D? camera, bool enableHud, bool enableCamera = true)
    {
        // 相机装置：默认接管（跟人走）。训练房这类「相机由 .tscn 摆好」的场景关掉它 ——
        // 但下面 HUD / 准星照建，它们只读世界，不依赖是谁在推相机。
        if (enableCamera)
        {
            // 相机：.tscn 里没有就现建一个（headless 与最小场景都能跑）。
            if (_cameraProvider.GetCamera() is null)
            {
                Camera3D created = new Camera3D { Name = "HeroCamera", Current = true };
                _owner.AddChild(created);
                _cameraProvider.Adopt(created);
            }

            Camera3D cam = _cameraProvider.GetCamera()!;
            // ★ 七个值一个都不能少：FollowRatePerSecond 是「每秒收敛速率」，表现层再换算成平滑时间；
            //   LookAhead* 是「鼠标方向偏移」的幅度与上限 —— 漏传画面照样跑，但策划在 Excel
            //   改这两格就永远不生效（v11「配置了但没接」的同一个坑）。
            CameraPackSettings pack = new CameraPackSettings(
                _assembly.Camera.Distance,
                _assembly.Camera.PitchDeg,
                _assembly.Camera.YawDeg,
                _assembly.Camera.FollowRatePerSecond,
                _assembly.Camera.CursorScreenHeightRatio,
                _assembly.Camera.LookAheadRatio,
                _assembly.Camera.LookAheadMaxMeters);
            CameraRigSettings rig = _viewOptions.BuildCameraSettings(pack, float.NaN);
            _cameraRig = new TopDownCameraRig(cam, rig);
            _cameraRig.SnapTo(ToGodot(SpawnPosition), Godot.Vector3.Forward);
        }

        // 准星：默认关闭 —— UI 层的 Drawer 已经画了屏幕中心的准星，
        // 两个准星同时存在会重影。需要"子弹落点环"时把 CrosshairVisible 打开即可。
        if (_viewOptions.CrosshairVisible)
        {
            _crosshair = new HeroCrosshair(_viewOptions);
            _owner.AddChild(_crosshair);
        }

        if (!enableHud)
        {
            return;
        }

        // HUD：CanvasLayer + UI 层的 Drawer。皮肤资源在 scenes/ui/character_hud_skin.tres。
        CharacterHudDrawer drawer = new CharacterHudDrawer
        {
            Name = "CharacterHudDrawer",
            Skin = LoadHudSkin(),
        };
        _hudPanel = new HeroHudPanel(_viewOptions, CharacterHudDrawerTarget.ForDrawer(drawer))
        {
            Name = "HeroHud",
        };
        _owner.AddChild(_hudPanel);
        _hudPanel.AddChild(drawer);
        drawer.SetAnchorsAndOffsetsPreset(Godot.Control.LayoutPreset.FullRect);
    }

    private static CharacterHudSkin? LoadHudSkin()
    {
        // 皮肤是可选资源：加载失败不该让整局起不来（退回 Drawer 的内置默认值）。
        const string HudSkinPath = "res://scenes/ui/character_hud_skin.tres";
        if (!ResourceLoader.Exists(HudSkinPath))
        {
            GD.PushWarning("[Hero] 找不到 HUD 皮肤资源 ", HudSkinPath, "，使用 UI 层内置默认参数。");
            return null;
        }
        return ResourceLoader.Load<CharacterHudSkin>(HudSkinPath);
    }

    /// <summary>
    /// 一行变体的**纯值外观描述**（数据行 → 视图层的翻译，放在这里是因为它俩只在这里相遇）。
    /// <para>
    /// 同一个世界里的每个角色都要注册它自己的外观包；第二个角色（训练房假人）照抄调用即可，
    /// 不必知道 <see cref="CharacterVisualPack"/> 有哪些列。白模阶段 <c>ModelPath</c> 为空 = 不加载任何 glb，
    /// 由 <c>ActorViewDirector</c> 回落到几何体白模。
    /// </para>
    /// </summary>
    public static ActorViewStyle BuildViewStyle(in CharacterVisualPack visual)
    {
        // 没配颜色文本时用配色档位兜底（TryParseColor 失败 → HasColor=false）。
        ActorViewStyle.TryParseColor(visual.ColorHex, out Color color);
        return ActorViewStyle.Colored(
            visual.VisualSlot,
            color,
            ActorBodyShape.Capsule,
            string.IsNullOrEmpty(visual.ModelPath) ? null : visual.ModelPath,
            visual.ModelScale);
    }

    private static PackedScene BuildViewShellPrefab()
    {
        // 视图池的空壳预制体：池里的节点由外观导演往里塞网格，所以这里只要一个空 MeshInstance3D。
        MeshInstance3D shell = new MeshInstance3D { Name = "ViewShell" };
        PackedScene scene = new PackedScene();
        if (scene.Pack(shell) != Error.Ok)
        {
            throw new InvalidOperationException("视图池预制体打包失败。");
        }
        return scene;
    }

    private static Godot.Vector3 ToGodot(System.Numerics.Vector3 v) => new Godot.Vector3(v.X, v.Y, v.Z);

    // ─────────────────────────────────────────────────────────────
    // 宿主侧数据源实现（Game.Actor 不认识 Game.Data，所以适配放在这里）
    // ─────────────────────────────────────────────────────────────

    private static CharacterAssemblyOf<SkillLoadout>? FindByDefinitionSlot(CharacterContentSet content, int definitionSlot)
    {
        IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> all = content.Assemblies.Assemblies;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].DefinitionSlot == definitionSlot)
            {
                return all[i];
            }
        }
        return null;
    }

    /// <summary>武器数值来源：按定义槽位号查装配清单里的武器行，映射成 <c>WeaponStats</c>。</summary>
    private sealed class ContentWeaponStatsSource : IWeaponStatsSource
    {
        private readonly CharacterContentSet _content;

        public ContentWeaponStatsSource(CharacterContentSet content) => _content = content;

        public bool TryResolve(int definitionSlot, out WeaponStats stats)
        {
            stats = default;
            CharacterAssemblyOf<SkillLoadout>? assembly = FindByDefinitionSlot(_content, definitionSlot);
            if (assembly is null || assembly.Weapon is null)
            {
                return false;
            }

            CharacterWeaponSpec w = assembly.Weapon;
            stats = new WeaponStats
            {
                Damage = w.Damage,
                BulletSpeed = w.BulletSpeed,
                RangeMeters = w.RangeMeters,
                SpreadDegrees = w.SpreadDegrees,
                CriticalChance = w.CriticalChance,
                CriticalMultiplier = w.CriticalMultiplier,
                ProjectileRadius = w.ProjectileRadius,
                ShotIntervalSeconds = w.ShotCooldown,
                PelletsPerShot = w.PelletsPerShot,
                WeaponId = w.WeaponId,
            };
            return true;
        }
    }

    /// <summary>外观与装配标签来源：把变体的外观包映射成 <c>ViewAppearance</c> + <c>ActorTag</c>。</summary>
    private sealed class ContentActorDecorSource : IActorDecorSource
    {
        private readonly CharacterContentSet _content;

        public ContentActorDecorSource(CharacterContentSet content) => _content = content;

        public bool TryResolve(int definitionSlot, out ViewAppearance appearance, out ActorTag tag)
        {
            appearance = default;
            tag = default;
            CharacterAssemblyOf<SkillLoadout>? assembly = FindByDefinitionSlot(_content, definitionSlot);
            if (assembly is null)
            {
                return false;
            }

            CharacterVisualPack visual = assembly.Visual;
            appearance = new ViewAppearance
            {
                VisualKey = visual.VisualKey,
                VisualSlotIndex = visual.VisualSlot,
                PaletteSlot = visual.Palette,
                Scale = visual.ModelScale,
            };
            tag = new ActorTag
            {
                AssemblySlot = assembly.AssemblySlot,
                VariantIndex = assembly.VariantIndex,
            };
            return true;
        }
    }

    private const float DegreesPerRadian = 180f / MathF.PI; // arch-ok: 弧度→角度的单位换算式，不是玩法数值
}

/// <summary>聚合读数的容器：按实体取其在装配期缓存下来的属性表（运行期零查表、零分配）。</summary>
internal sealed class StatSnapshotTable
{
    private readonly IStatPackageRepository _packages;
    private readonly Dictionary<Entity, IReadOnlyStatView> _views = new Dictionary<Entity, IReadOnlyStatView>();
    private readonly Dictionary<int, Entity> _ownerOfDefinitionSlot = new Dictionary<int, Entity>();

    public StatSnapshotTable(IStatPackageRepository packages) => _packages = packages;

    /// <summary>登记一个实体的属性表（装配后由宿主调用；重复登记以最后一次为准）。</summary>
    public void Bind(Entity owner, in StatPackageIds ids, int definitionSlot)
    {
        _views[owner] = _packages.Compose(ids);
        _ownerOfDefinitionSlot[definitionSlot] = owner;
    }

    /// <summary>按实体取属性表；未登记返回 <c>null</c>。</summary>
    public IReadOnlyStatView? ViewOf(Entity entity) =>
        _views.TryGetValue(entity, out IReadOnlyStatView? view) ? view : null;

    /// <summary>按定义槽位号取"那个实体的属性表"；未出生返回 <c>null</c>。</summary>
    public IReadOnlyStatView? ViewOfDefinition(int definitionSlot) =>
        _ownerOfDefinitionSlot.TryGetValue(definitionSlot, out Entity owner) ? ViewOf(owner) : null;

    /// <summary>按定义槽位号取实体；未出生返回 false。</summary>
    public bool TryGetOwnerOfDefinition(int definitionSlot, out Entity owner) =>
        _ownerOfDefinitionSlot.TryGetValue(definitionSlot, out owner);
}

    /// <summary>会话级技能上下文：命令缓冲 + 随机源 + 属性读取（引擎边界只体现在 Random 的种子来源）。</summary>
    internal sealed class SessionSkillContext : ISkillContext
    {
        private readonly IRandomProvider _random;

        public SessionSkillContext(Node owner, World world, HostEventSink events, Func<int, IReadOnlyStatView?> statsOf)
        {
            _ = owner;
            Ecs = world ?? throw new ArgumentNullException(nameof(world));
            Events = events ?? throw new ArgumentNullException(nameof(events));
            Commands = new SessionCommandSink(world, events);
            StatsOfFunc = statsOf ?? (_ => null);
            // 技能随机必须走注入，保证同 seed 同结果（可复现是肉鸽的硬需求）。
            _random = new DeterministicRandom((int)(Time.GetTicksUsec() & 0x7FFFFFFF));
        }

        public ICommandSink Commands { get; }

        public World Ecs { get; }

        public IRandomProvider Random => _random;

        public float DeltaTime { get; set; }

        public HostEventSink Events { get; }

        private Func<int, IReadOnlyStatView?> StatsOfFunc { get; }

        public IReadOnlyStatView? StatsOf(int actorId) => StatsOfFunc(actorId);
    }

/// <summary>会话级命令门面：技能想做的结构变更在此入队，真正的变更由管线在帧末统一执行（J5）。</summary>
internal sealed class SessionCommandSink : ICommandSink
{
    private readonly World _world;
    private readonly HostEventSink _events;

    public SessionCommandSink(World world, HostEventSink events)
    {
        _world = world;
        _events = events;
    }

    public void SpawnActor(string definitionId, in SpawnPoint at, TeamId team)
    {
        // v1：召唤类技能尚未实现。真实现应走 ActorSpawnQueue（见 CharacterSession.RequestSpawn），
        // 绝不在这里直接 World.Create —— 造人只有一个入口（J5）。
        GD.Print("[Combat] 召唤请求 def=", definitionId, " 阵营=", team,
            " 位置=", at.Position.X.ToString("F1"), ",", at.Position.Z.ToString("F1"));
    }

    public void SpawnProjectile(string projectileId, in SpawnPoint at, in LaunchVector launch, TeamId team)
    {
        // 技能已经用 ctx.Ecs.Commands 直接建好了子弹实体；这里只做"外观/统计"登记。
        GD.Print("[Combat] 子弹请求 id=", projectileId, " 阵营=", team,
            " 弹速=", launch.Speed.ToString("F1"));
    }

    public void SpawnEffect(string effectId, in SpawnPoint at)
    {
        _ = effectId;
        _ = at;
    }

    public void Destroy(int actorId, int generation)
    {
        _world.Commands.Destroy(new Entity(actorId, generation));
    }

    /// <summary>
    /// 给已存在实体挂组件（冲刺挂 <see cref="Game.Actor.DashState"/> 走这里）。
    /// <para>
    /// 走命令缓冲而不是 <c>_world.Set</c>：施放发生在查询遍历里，直接改结构会撞上
    /// <c>AssertStructuralAllowed</c> 守卫（J5）。命令在帧末 <c>Flush</c> 时落地，
    /// 因此"排了命令"与"组件真的在身上"之间隔着本帧剩余的时间 —— 这是契约要求的顺序。
    /// </para>
    /// </summary>
    public void Add<T>(Entity entity, in T value) where T : unmanaged
    {
        _world.Commands.Add(entity, value);
    }
}

/// <summary>警告版事件出口：宿主还没有表现层订阅方时，把玩法事件打成日志便于验证。</summary>
/// <remarks>
/// 真正接表现（飘字/音效/白闪）时应换成一个把事件翻译成视图调用的实现；
/// 本类保持"只收不发"的纪律，绝不反过来改世界。
/// </remarks>
internal sealed class WarningEventSink : HostEventSink
{
    /// <summary>累计收到的伤害事件数（验收/日志用）。</summary>
    public int DamageCount { get; private set; }

    /// <summary>累计收到的命中事件数。</summary>
    public int HitCount { get; private set; }

    /// <inheritdoc />
    public void RaiseDamageApplied(ActorHandle source, ActorHandle target, float amount, bool isCrit, DamageType type)
    {
        DamageCount++;
        GD.Print("[Combat] 伤害 ", amount.ToString("F1"), isCrit ? " 暴击" : string.Empty,
            " 类型=", type, " 来源=", source.Index, ":", source.Generation,
            " 目标=", target.Index, ":", target.Generation);
    }

    /// <inheritdoc />
    public void RaiseHit(ActorHandle source, ActorHandle target, float x, float y, float z, DamageType type)
    {
        HitCount++;
        _ = source;
        _ = target;
        _ = x;
        _ = y;
        _ = z;
        _ = type;
    }

    /// <inheritdoc />
    public void RaiseActorKilled(ActorHandle victim, ActorHandle killer, DamageType type)
    {
        _ = killer;
        _ = type;
        GD.Print("[Combat] 击倒 ", victim.Index, ":", victim.Generation);
    }

    /// <inheritdoc />
    public void RaiseLifetimeExpired(ActorHandle entity) => _ = entity;

    /// <inheritdoc />
    public void RaiseEntityDestroyed(ActorHandle entity) => _ = entity;
}

/// <summary>相机 / 视口的供应者：把"往场景树里找"这件事收敛到一个地方，也让 headless 有兜底。</summary>
internal sealed class GodotCameraProvider
{
    private readonly Node _owner;
    private Camera3D? _camera;

    public GodotCameraProvider(Node owner, Camera3D? camera)
    {
        _owner = owner;
        _camera = camera;
    }

    /// <summary>接管一个新建的相机（<c>.tscn</c> 里没有相机时）。</summary>
    public void Adopt(Camera3D camera) => _camera = camera;

    public Camera3D? GetCamera()
    {
        if (_camera is not null && GodotObject.IsInstanceValid(_camera))
        {
            return _camera;
        }
        return _owner.GetViewport()?.GetCamera3D();
    }

    public Viewport? GetViewport() => _owner.GetViewport();
}

/// <summary>HUD 技能格上的字形与键位文案（纯展示常量，不参与玩法）。</summary>
internal static class HudGlyphs
{
    public static string ForSlot(SkillSlot slot) => slot switch
    {
        SkillSlot.Dash => ">>",
        SkillSlot.PrimaryAttack => "ATK",
        SkillSlot.WeaponSwap => "SWAP",
        SkillSlot.Signature => "SIG",
        SkillSlot.Interact => "INT",
        _ => string.Empty,
    };

    public static string KeyLabelForSlot(SkillSlot slot) => slot switch
    {
        SkillSlot.Dash => "LSHIFT",
        SkillSlot.PrimaryAttack => "LMB",
        SkillSlot.WeaponSwap => "Q",
        SkillSlot.Signature => "R",
        SkillSlot.Interact => "F",
        _ => string.Empty,
    };
}

/// <summary>本会话往管线上加系统时用的固定次序号（越小越先初始化；跨组执行顺序由各系统的声明决定）。</summary>
internal static class CharacterSessionRail
{
    /// <summary>意图采集（PreUpdate）。</summary>
    public const int Intent = 20; // arch-ok: 管线次序号（结构性常量，不是玩法数值）

    /// <summary>角色装配（PreUpdate，意图之后）。</summary>
    public const int Assembly = 21; // arch-ok: 管线次序号（结构性常量）

    /// <summary>技能施放（Update）。</summary>
    public const int Skill = 22; // arch-ok: 管线次序号（结构性常量）
}

/// <summary>本变体需要的模块 id（与数据层 <c>CharacterContentSet</c> 里声明的字符串一致）。</summary>
internal static class CharacterModules
{
    /// <summary>标准移动（输入意图 → 速度 → 位移）。</summary>
    public const string Move = "move_standard";

    /// <summary>鼠标瞄准（世界点 → 朝向）。</summary>
    public const string Aim = "aim_cursor";

    /// <summary>左键射击（投射物 + 命中 + 命中事件）。</summary>
    public const string Fire = "fire_primary";

    /// <summary>
    /// 冲刺（Shift）：把 <c>DashState</c> 翻译成速度。
    /// <para>本 id 只作为管线注册键，<u>不</u>出现在 Excel 的 Assemblies 里 —— 它由会话无条件挂上，
    /// 因为系统本身在没人 <c>DashState</c> 时零成本。是否真的能冲由技能包数据决定
    /// （<c>SkillLoads.Kind = "dash"</c>）。</para>
    /// </summary>
    public const string Dash = "dash_impulse";
}
