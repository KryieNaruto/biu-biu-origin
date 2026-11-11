using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Actor;

/// <summary>
/// 角色生成请求（**命令载荷**，值类型）：任何系统都能投递，由 <see cref="ActorAssemblySystem"/> 消费。
/// <para>
/// 为什么是队列而不是直接 <c>World.Create</c>：生成源（刷怪/AI 召唤/重生）可能在 chunk 遍历中被调用，
/// 结构化变更必须留到帧边界（J5）。队列就是这条「留到帧边界」的通道。
/// </para>
/// </summary>
public readonly struct ActorSpawnRequest
{
    /// <summary>「没有该包」哨兵。</summary>
    public const int NoPackage = -1;

    /// <summary>角色定义槽位号（由 <see cref="ActorDefinitionRegistry.SlotOf"/> 解析得到）。</summary>
    public readonly int DefinitionSlot;

    /// <summary>生成位置。</summary>
    public readonly System.Numerics.Vector3 Position;

    /// <summary>阵营覆盖（<c>null</c> = 用定义的默认阵营；夺舍/召唤用这个改阵营）。</summary>
    public readonly TeamId? TeamOverride;

    /// <summary>控制方案覆盖（<c>null</c> = 用定义的默认值；夺舍后玩家控制怪物就填这里）。</summary>
    public readonly ControlScheme? ControlOverride;

    /// <summary>
    /// 武器数值覆盖（<c>null</c> = 用注入的 <see cref="IWeaponStatsSource"/> 按定义查表）。
    /// <para>
    /// <b>为什么武器数值要走"生成请求"这条路提供</b>：武器来自数据表（<c>scripts/data/</c>），
    /// 而装配系统与 <see cref="WeaponStats"/> 组件活在 <c>scripts/actor/</c>（被测试工程直接编译，
    /// 不许引用 <c>Game.Data</c>）。两条路都可行：(a) 注入 reader 由数据层实现；(b) 调用方在投递请求时
    /// 把数塞进来。本字段同时支持 (b) —— 因为"这只怪拿什么枪"本来就是<u>调用方的决定</u>
    /// （刷怪表、夺舍、掉落的武器），而不是角色的固有属性。
    /// </para>
    /// </summary>
    public readonly WeaponStats? WeaponOverride;

    /// <summary>构造。</summary>
    public ActorSpawnRequest(
        int definitionSlot,
        System.Numerics.Vector3 position,
        TeamId? teamOverride = null,
        ControlScheme? controlOverride = null,
        WeaponStats? weaponOverride = null)
    {
        DefinitionSlot = definitionSlot;
        Position = position;
        TeamOverride = teamOverride;
        ControlOverride = controlOverride;
        WeaponOverride = weaponOverride;
    }
}

/// <summary>
/// 「角色定义 → 初始武器」的查询接缝（由数据层/宿主实现，装配系统消费）。
/// <para>
/// <b>为什么是接缝而不是直接引用数据表</b>：<c>scripts/actor/**</c> 被 tests 工程直接编译且不许引用
/// <c>Game.Data</c>（否则引擎/Excel 依赖会渗进纯逻辑测试）。接缝让"武器数值从哪来"成为注入项，
/// 于是同一套装配逻辑在测试里灌内存数据、在游戏里读 Excel，一行不改。
/// </para>
/// <para><b>为什么返回 <c>bool</c> 而不是可空结构</b>：<see cref="WeaponStats"/> 是 unmanaged 组件，
/// 用 <c>out</c> 避免装箱；返回值表达"这个定义是否配了武器"，让"没配武器"可以被区别对待
/// （近战小怪本来就没有枪，不该报数据错误）。</para>
/// </summary>
public interface IWeaponStatsSource
{
    /// <summary>取某个角色定义槽位对应的初始武器数值；没配武器返回 <c>false</c>。</summary>
    bool TryResolve(int definitionSlot, out WeaponStats stats);
}

/// <summary>
/// 「角色定义 → 外观与标签」的查询接缝（表现数据由宿主/数据层提供）。
/// <para>
/// <b>为什么外观和标签合成一个接缝</b>：它们都是"这个定义在数据表里长什么样"的投影
/// （外观键 + 调色板槽 + 缩放 / 装配槽位 + 变体号），来源是同一行数据，拆成两个接口只会让宿主
/// 写两遍"按槽位查表"的样板。合并后宿主实现一个类、注入一处。
/// </para>
/// <para>
/// <b>为什么是可选的</b>：纯逻辑测试（与数据无关）不需要外观；不注入时装配仍必须成功 ——
/// 只是 <see cref="ViewAppearance.VisualSlotIndex"/> 写 -1（"没有外观来源"的显式标记），
/// 让视图层能把"这只怪没有外观"和"外观键恰好是 0"区分开，而不是静默画成一个默认方块。
/// </para>
/// </summary>
public interface IActorDecorSource
{
    /// <summary>
    /// 取某个角色定义槽位的外观与标签。
    /// </summary>
    /// <param name="definitionSlot">角色定义槽位号（<see cref="ActorDefinitionRegistry.SlotOf"/> 的结果）。</param>
    /// <param name="appearance">外观（VisualKey / PaletteSlot / Scale；VisualSlotIndex 由装配系统填定义槽位号）。</param>
    /// <param name="tag">标签（AssemblySlot / VariantIndex）。</param>
    /// <returns>没有该定义的外观数据时返回 <c>false</c>（装配仍成功，外观标记为"无来源"）。</returns>
    bool TryResolve(int definitionSlot, out ViewAppearance appearance, out ActorTag tag);
}

/// <summary>
/// 生成请求队列（帧内的单向通道；主线程读写，无锁）。
/// </summary>
public sealed class ActorSpawnQueue
{
    private readonly Queue<ActorSpawnRequest> _requests = new Queue<ActorSpawnRequest>();

    /// <summary>待处理请求数。</summary>
    public int Pending => _requests.Count;

    /// <summary>投递一条生成请求。</summary>
    public void Enqueue(in ActorSpawnRequest request) => _requests.Enqueue(request);

    /// <summary>取出队首；无请求返回 false。</summary>
    public bool TryDequeue(out ActorSpawnRequest request) => _requests.TryDequeue(out request);

    /// <summary>清空（重开一局）。</summary>
    public void Clear() => _requests.Clear();
}

/// <summary>
/// 数值包槽位解析器：把 <see cref="StatPackageIds"/> 里的字符串 id 解析成组件可存的 int 槽位号。
/// <para>
/// <b>为什么单独抽一个接口</b>：定义里存的是 id（可读、可存档），组件里只能存 int（J2）。
/// 「id → 槽位号」的映射由实现方（<c>scripts/data/</c> 的仓库）决定，装配系统只消费结果；
/// 测试里可以给一个<u>不依赖 Excel</u> 的简单实现。这样「装配逻辑」与「数据加载」可以分别演进。
/// </para>
/// </summary>
public interface IStatPackageSlotResolver
{
    /// <summary>把包 id 解析成槽位号；空 id 或不存在返回 <see cref="ActorSpawnRequest.NoPackage"/>。</summary>
    int Resolve(string packageId);
}

/// <summary>
/// 默认解析器：包的存在性以 <see cref="IStatPackageRepository"/> 为准，槽位号来自 <c>id → 序号</c> 的登记表。
/// <para>
/// 为什么槽位号不复用 <see cref="ActorDefinitionRegistry"/> 的编址：那是「角色定义」的地址空间，
/// 与「数值包」是两个集合，混用会让同一个 id 在不同表里拿到不同号，且将来删除一条数据就会错位。
/// </para>
/// </summary>
public sealed class RepositoryPackageSlotResolver : IStatPackageSlotResolver
{
    private readonly IStatPackageRepository _packages;
    private readonly Dictionary<string, int> _slots = new Dictionary<string, int>(StringComparer.Ordinal);
    private int _next;

    /// <summary>构造。</summary>
    public RepositoryPackageSlotResolver(IStatPackageRepository packages)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
    }

    /// <inheritdoc />
    public int Resolve(string packageId)
    {
        if (string.IsNullOrEmpty(packageId))
        {
            return ActorSpawnRequest.NoPackage;
        }
        if (_slots.TryGetValue(packageId, out int slot))
        {
            return slot;
        }
        if (!_packages.TryGet(packageId, out IReadOnlyStatView _))
        {
            return ActorSpawnRequest.NoPackage;
        }
        // 首次见到即登记：槽位号单调递增且进程内稳定（不因查询顺序变化而变）。
        slot = _next++;
        _slots.Add(packageId, slot);
        return slot;
    }
}

/// <summary>
/// 角色装配系统（PreUpdate）：读取本帧投递的 <see cref="ActorSpawnRequest"/>，按定义把实体<u>组合</u>出来。
/// <para>
/// <b>这就是「角色必须符合 ECS 组合思想，能组合数值与技能包」的落地点</b>：
/// 一个角色实体 = <c>[Transform3, Team, Health, Mana, Hurtbox, ActorIdentity, SkillState, ActorIntent]</c>
/// 这组组件的组合，<u>没有任何「Player」或「Monster」类</u>。
/// 「怪物和角色同源，区分阵营」= 采用相同的这一组组件，<see cref="ActorIdentity.Team"/> 取不同值；
/// 血量/护甲/技能差异全部通过 <see cref="StatPackageIds"/> 与 <see cref="SkillLoadout"/> 的**组合**表达。
/// </para>
/// <para>
/// <b>为什么装配是系统而不是构造器</b>：生成请求来自刷怪、重生、夺舍、关卡加载等多个来源，
/// 它们都能<u>投递一条命令</u>而不必知道装配细节（规则7：新增一种角色 = Excel 一行，0 个系统文件改动）。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.PreUpdate)]
public sealed class ActorAssemblySystem : SystemBase
{
    private readonly ActorDefinitionRegistry _definitions;
    private readonly SkillLoadoutRegistry _loadouts;
    private readonly IStatPackageRepository _packages;
    private readonly IStatPackageSlotResolver _slots;
    private readonly ActorSpawnQueue _queue;

    /// <summary>
    /// 出生帧意图源（可选）：生成时给新实体补一次意图，避免"按着键生成的实体在出生帧不响应"。
    /// <c>null</c> = 不补（纯逻辑测试或没有输入层的场景），下一帧起由意图系统正常采集。
    /// </summary>
    private readonly ActorIntentSystem? _intentSource;

    /// <summary>
    /// 武器数值来源（可选）：<c>null</c> = 这个构建里"角色不带枪"（只有生成请求显式给了
    /// <see cref="ActorSpawnRequest.WeaponOverride"/> 才会写 <see cref="WeaponStats"/>）。
    /// </summary>
    private readonly IWeaponStatsSource? _weapons;

    /// <summary>
    /// 外观/标签来源（可选）。<c>null</c> = 没接外观源：外观字段走兜底（<see cref="ViewAppearance.VisualSlotIndex"/>
    /// 仍写定义槽位号，保证角色看得见），而不是 <see cref="NoVisualSource"/>。
    /// </summary>
    private readonly IActorDecorSource? _decor;

    /// <summary>「缺武器来源」的警告是否已打过（只打一次：每帧刷屏会把真正的日志淹没）。</summary>
    private bool _weaponSourceWarned;

    /// <summary>本次执行装配成功的实体数。</summary>
    public int Assembled { get; private set; }

    /// <summary>本次执行因定义/数值包缺失而跳过的实体数（数据错误计数，正常应恒为 0）。</summary>
    public int Skipped { get; private set; }

    /// <summary>
    /// 装配出的实体里"没有任何武器来源"的累计次数（&gt; 0 且宿主本该有枪 → 说明忘了注入
    /// <see cref="IWeaponStatsSource"/>，表现会是"按左键没反应"）。
    /// </summary>
    public int WeaponSourceMissingCount { get; private set; }

    private readonly List<Entity> _lastAssembled = new List<Entity>();

    /// <summary>
    /// "不画"的显式标记：<see cref="ViewAppearance.VisualSlotIndex"/> 取此值表示该实体没有可见外观
    /// （纯逻辑实体、隐藏的碰撞体等）。
    /// <para>
    /// <b>装配路径不会写它</b>：即使没注入 <see cref="IActorDecorSource"/>，装配也会用定义槽位号兜底，
    /// 因为"没配外观来源"是宿主的配置问题，不该让玩家角色隐形。需要隐藏实体时，由调用方在装配后
    /// 自己 <c>World.Set</c> 覆盖成这个值。
    /// </para>
    /// </summary>
    public const int NoVisualSource = -1;

    /// <summary>
    /// 本帧新建的实体句柄（含世代号，可直接用于 <c>World.Get/Set</c>）。
    /// <para>为什么系统要把它公开：生成之后几乎总要紧接一件事 —— 视图层要建节点、AI 要认领、HUD 要挂血条。
    /// 让它们从"生成事件"里拿句柄，比让它们自己遍历全世界找"刚出现的那个"要可靠得多。</para>
    /// </summary>
    public IReadOnlyList<Entity> LastAssembled => _lastAssembled;

    /// <summary>构造：依赖全部注入，便于裸测。</summary>
    public ActorAssemblySystem(
        ActorDefinitionRegistry definitions,
        SkillLoadoutRegistry loadouts,
        IStatPackageRepository packages,
        IStatPackageSlotResolver slots,
        ActorSpawnQueue queue,
        ActorIntentSystem? intentSource = null,
        IWeaponStatsSource? weapons = null,
        IActorDecorSource? decor = null)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _loadouts = loadouts ?? throw new ArgumentNullException(nameof(loadouts));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _intentSource = intentSource;
        _weapons = weapons;
        _decor = decor;
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        Assembled = 0;
        Skipped = 0;
        _lastAssembled.Clear();
        while (_queue.TryDequeue(out ActorSpawnRequest request))
        {
            Entity entity = Assemble(in request);
            // 用 Entity.None 作"失败"哨兵：它的 Index 是 NoneIndex，World.None 永远不会被真的创建出来。
            if (!Entity.None.Equals(entity))
            {
                Assembled++;
                _lastAssembled.Add(entity);
            }
            else
            {
                Skipped++;
            }
        }
    }

    /// <summary>
    /// 装配一个角色实体。返回 false 表示数据不完整（定义/Base 数值包缺失）。
    /// <para>结构化变更（<c>World.Create</c> / <c>Set</c>）发生在帧边界的主线程，不在遍历中途（J5）。</para>
    /// </summary>
    private Entity Assemble(in ActorSpawnRequest request)
    {
        ActorDefinition? def = _definitions.Get(request.DefinitionSlot);
        if (def is null)
        {
            return Entity.None;
        }

        StatPackageIds ids = def.StatPackageIds;
        int baseSlot = _slots.Resolve(ids.Base);
        if (baseSlot < 0)
        {
            // Base 层缺失 = 没有血量/速度，属于数据错误；不生成半成品实体。
            return Entity.None;
        }

        IReadOnlyStatView stats = _packages.Compose(ids);
        float hpMax = stats.Get(StatId.HpMax);
        float manaMax = stats.Get(StatId.ManaMax);
        TeamId team = request.TeamOverride ?? def.DefaultTeam;

        // 组件清单显式列出（内核只提供 ≤3 元泛型重载，一次原型查找完成 —— 比逐条 Set 少 n-1 次原型迁移）。
        var components = new List<ComponentType>(16) // arch-ok: 容量提示不是玩法数值
        {
            World.Register<Transform3>(),
            World.Register<Velocity>(),
            World.Register<Team>(),
            World.Register<Health>(),
            World.Register<Mana>(),
            World.Register<Hurtbox>(),
            World.Register<ActorIdentity>(),
            World.Register<SkillState>(),
            World.Register<ActorIntent>(),
            // ── 移动/瞄准/表现三个模块的"挂载点" ──
            // 为什么在装配期就给空组件而不是等模块写：组件<u>存在与否</u>是查询的筛选条件（J3）。
            // 一个角色生成时没有 Velocity，MovementSystem 的查询就看不见它，于是"第一个模块还没跑"的
            // 那一帧它也不动 —— 这种"少一个空组件导致某系统永远跳过它"的 bug 极难定位。
            // 空组件（全零）语义明确：意图为 0 = 不想动；瞄准无目标 = 不转身；表现槽位待定。
            World.Register<MoveIntent>(),
            World.Register<AimTarget>(),
            // ★ View 必须和 ViewAppearance 一起注册：视图池按 View 租节点（VisualBridge.RentMissing），
            //   外观按 View+ViewAppearance 找实体套样式（ActorViewDirector.ApplyAll）。
            //   只给 ViewAppearance 不给 View，会话里"外观组件齐全"但视图永远租不出来 ——
            //   症状就是人物隐形，屏幕上只剩 HUD 准星。Slot=0 是"待分配"哨兵，租出时由桥层写回。
            World.Register<View>(),
            World.Register<ViewAppearance>(),
            World.Register<ActorTag>(),
        };
        Entity entity = World.Create(components);

        World.Set(entity, new View { Slot = 0, Kind = 0, Dirty = true });

        World.Set(entity, new Transform3
        {
            Position = request.Position,
            Rotation = System.Numerics.Quaternion.Identity,
            Scale = System.Numerics.Vector3.One,
        });

        // 线速度/角速度从 0 起：由 MoveAimSystem 每帧写线速度，角速度暂留给旋转类实体自己维护。
        World.Set(entity, new Velocity());

        // 意图清零：出生帧的意图由下面的 birthIntent 补（避免"按着键生成的角色出生帧不响应"）。
        World.Set(entity, new MoveIntent());

        // 瞄准初始"朝 +Z、无目标"：方向给一个合法的单位向量而不是零向量，
        // 因为零向量在"归一化朝向"的代码里没有意义（等于"朝哪都不对"），而 +Z 是模型的默认前方。
        World.Set(entity, new AimTarget
        {
            Point = request.Position + System.Numerics.Vector3.UnitZ,
            Direction = System.Numerics.Vector3.UnitZ,
            HasTarget = false,
        });

        // 外观与标签：来自注入的接缝（数据行 → VisualKey/调色板/缩放 + 装配槽位/变体号）。
        ViewAppearance appearance = default;
        ActorTag tag = default;
        // 外观来源没注入（纯逻辑装配）或解析失败时，仍然要给出「可用的」外观引用：
        // 用定义槽位号兜底。为什么不写 NoVisualSource（-1）：视觉槽位是视图层拿外观的<u>唯一</u>
        // 索引，写 -1 等于「这台机器上没有外观」—— 玩家角色会直接看不见，而这不该是「没配装饰源」
        // 的默认后果。定义槽位号在任何情况下都是稳定且非负的，先让人看得见，外观再补。
        bool hasDecor = _decor is not null
            && _decor.TryResolve(request.DefinitionSlot, out appearance, out tag);

        if (!hasDecor)
        {
            appearance.Scale = 1f; // arch-ok: 中性的"原始尺寸"填充值；真实缩放来自数据行（1 = 不缩放）
            tag.AssemblySlot = request.DefinitionSlot;
            tag.VariantIndex = request.DefinitionSlot;
        }

        // VisualSlotIndex 由装配系统统一填写（不让每个 IActorDecorSource 实现复述同一语义）：
        // 有外观来源 = 定义槽位号（"这套外观属于哪个定义"的稳定引用）；没有来源时同样落到定义槽位号，
        // 因为"没有外观来源"和"没有外观"是两件事 —— 前者是宿主配置问题，不应该让角色隐形。
        // NoVisualSource（-1）保留给调用方显式想要"不画"的场合（如纯逻辑实体），装配路径不写它。
        appearance.VisualSlotIndex = request.DefinitionSlot;
        World.Set(entity, appearance);
        World.Set(entity, tag);

        World.Set(entity, new Team { Value = team });

        World.Set(entity, new Health { Current = hpMax, Max = hpMax, IsDead = false });

        World.Set(entity, new Mana { Current = manaMax, Max = manaMax });

        World.Set(entity, new Hurtbox
        {
            Radius = def.HitRadius,
            Height = def.HitHeight,
            InvulnerableRemaining = 0f,
            FlashRemaining = 0f,
            // 击退抗性当前借用 ArmorFlat；正式伤害公式落地后改由 Armor 换算（见 ARCHITECTURE §6.2）。
            KnockbackResistance = stats.Get(StatId.ArmorFlat),
        });

        World.Set(entity, new ActorIdentity
        {
            Kind = def.Kind,
            Team = team,
            Control = request.ControlOverride ?? def.Control,
            DefinitionSlot = request.DefinitionSlot,
            BasePackageSlot = baseSlot,
            GrowthPackageSlot = _slots.Resolve(ids.Growth),
            EquipmentPackageSlot = _slots.Resolve(ids.Equipment),
            BuffPackageSlot = _slots.Resolve(ids.Buff),
            TemporaryPackageSlot = _slots.Resolve(ids.Temporary),
        });

        World.Set(entity, new SkillState
        {
            LoadoutSlot = _loadouts.SlotOf(def.SkillLoadoutId),
            LastFailure = SkillCastFailure.None,
            RequestedSlot = SkillSlot.None,
            RequestedHold = false,
            CastThisFrame = false,
        });

        ControlScheme control = request.ControlOverride ?? def.Control;
        InputIntent birthIntent = _intentSource is null
            ? default
            : _intentSource.PollFor(control, entity.Index);
        var actorIntent = default(ActorIntent);
        IntentRules.Apply(in birthIntent, ref actorIntent);
        World.Set(entity, actorIntent);

        // ── 武器：生成请求优先（刷怪表/掉落/夺舍当场指定），否则按定义查表 ──
        // 为什么放在最后而不是塞进上面的组件清单：不是所有角色都有枪（近战小怪没有），
        // 有枪的角色多一个组件 → 走一次原型变更。用 Set 而不是 Create 里的清单，
        // 让"有没有武器"成为<u>运行期事实</u>而非"两种原型"，查询侧继续用 With<WeaponStats>/Without。
        WeaponStats weapon;
        if (request.WeaponOverride.HasValue)
        {
            weapon = request.WeaponOverride.Value;
        }
        else if (_weapons is not null && _weapons.TryResolve(request.DefinitionSlot, out WeaponStats resolved))
        {
            weapon = resolved;
        }
        else
        {
            // 没有任何武器来源 = 这个构建里"角色不带枪"。不写组件、不抛异常（近战小怪本来就没有枪），
            // 但**必须让宿主知道**：否则表现是"左键没反应"，而原因藏在"忘了注入 IWeaponStatsSource"里。
            // 只警告一次：装配是每帧都可能发生的（刷怪），逐次警告会淹没真正的日志。
            if (!_weaponSourceWarned)
            {
                _weaponSourceWarned = true;
                WeaponSourceMissingCount++;
            }
            return entity;
        }

        // 组件里的第一字段是 [Identity] Owner：内核 Set 时会自己填，这里是纯值拷贝。
        weapon.Owner = entity;
        World.Set(entity, weapon);
        return entity;
    }
}
