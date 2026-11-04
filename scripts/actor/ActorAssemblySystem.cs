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

    /// <summary>构造。</summary>
    public ActorSpawnRequest(
        int definitionSlot,
        System.Numerics.Vector3 position,
        TeamId? teamOverride = null,
        ControlScheme? controlOverride = null)
    {
        DefinitionSlot = definitionSlot;
        Position = position;
        TeamOverride = teamOverride;
        ControlOverride = controlOverride;
    }
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

    /// <summary>本次执行装配成功的实体数。</summary>
    public int Assembled { get; private set; }

    /// <summary>本次执行因定义/数值包缺失而跳过的实体数（数据错误计数，正常应恒为 0）。</summary>
    public int Skipped { get; private set; }

    private readonly List<Entity> _lastAssembled = new List<Entity>();

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
        ActorIntentSystem? intentSource = null)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _loadouts = loadouts ?? throw new ArgumentNullException(nameof(loadouts));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _intentSource = intentSource;
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

        // 8 个组件的创建：内核只提供 ≤3 元泛型重载，这里显式列出组件类型列表（一次原型查找）。
        var components = new List<ComponentType>(8)
        {
            World.Register<Transform3>(),
            World.Register<Team>(),
            World.Register<Health>(),
            World.Register<Mana>(),
            World.Register<Hurtbox>(),
            World.Register<ActorIdentity>(),
            World.Register<SkillState>(),
            World.Register<ActorIntent>(),
        };
        Entity entity = World.Create(components);

        World.Set(entity, new Transform3
        {
            Position = request.Position,
            Rotation = System.Numerics.Quaternion.Identity,
            Scale = System.Numerics.Vector3.One,
        });

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
        return entity;
    }
}
