using System;
using System.Numerics;
using Game.Core;
using Game.Core.Ecs;

namespace Game.Spawn.Rooms;

/// <summary>
/// 房间内容标记组件：说明「这个实体是哪个房间、哪类槽位搬进来的」。
/// <para>
/// 为什么需要一个新组件而不是复用现有字段：运行时要回答「这间房还有几个活着的怪」——那要靠
/// <c>Query&lt;RoomContent, Health&gt;</c> 按 <see cref="Room"/> 过滤（J6：查询形状稳定）。
/// 房间号必须是 unmanaged 值（J2），所以存 <see cref="DungeonGeneration"/> 与槽位两个 int。
/// 组件里没有方法、没有数值（J3）：分组/统计逻辑都在系统的循环里。
/// </para>
/// </summary>
public struct RoomMember
{
    /// <summary>实体自引用（ECS.md §4.2：必须是布局上的第一个字段）。</summary>
    [Identity] public Entity Owner;

    /// <summary>所属房间槽位（<see cref="RoomId.Slot"/>，-1 = 不属于任何房间）。</summary>
    public int RoomSlot;

    /// <summary>所属地牢世代（旧世代实体不会被误算进新一层）。</summary>
    public int Generation;

    /// <summary>来源槽位用途（<see cref="ContentSlotKind"/> 的数值）。</summary>
    public byte SlotKind;

    /// <summary>来源槽位序号（调试/日志定位用）。</summary>
    public int SlotIndex;
}

/// <summary>
/// 房间内容**参数**：每类内容实体的数值（半径、高度、生命）全部来自这里（规则4 / GS005）。
/// <para>来源与 <see cref="RoomContentConfig"/> 同一范式：宿主 Resource → Excel → 测试 <c>new</c>。</para>
/// </summary>
public sealed class RoomContentSpawnConfig
{
    /// <summary>普通敌人的受击半径（米）。</summary>
    public float EnemyHurtboxRadius { get; init; }

    /// <summary>普通敌人的碰撞高度（米；0 = 纯球体）。</summary>
    public float EnemyHurtboxHeight { get; init; }

    /// <summary>普通敌人的最大生命。</summary>
    public float EnemyHealth { get; init; }

    /// <summary>Boss 的受击半径（米）。</summary>
    public float BossHurtboxRadius { get; init; }

    /// <summary>Boss 的最大生命。</summary>
    public float BossHealth { get; init; }

    /// <summary>道具/奖励类实体的受击半径（米；拾取物也要能被射线/吸附检测到）。</summary>
    public float PropHurtboxRadius { get; init; }

    /// <summary>表现槽位：敌人。</summary>
    public int ViewKindEnemy { get; init; }

    /// <summary>表现槽位：Boss。</summary>
    public int ViewKindBoss { get; init; }

    /// <summary>表现槽位：道具。</summary>
    public int ViewKindProp { get; init; }

    /// <summary>校验。</summary>
    public void Validate()
    {
        Require(EnemyHurtboxRadius > 0f, nameof(EnemyHurtboxRadius));
        Require(EnemyHurtboxHeight >= 0f, nameof(EnemyHurtboxHeight));
        Require(EnemyHealth > 0f, nameof(EnemyHealth));
        Require(BossHurtboxRadius > 0f, nameof(BossHurtboxRadius));
        Require(BossHealth > 0f, nameof(BossHealth));
        Require(PropHurtboxRadius > 0f, nameof(PropHurtboxRadius));
        Require(ViewKindEnemy > 0, nameof(ViewKindEnemy));
        Require(ViewKindBoss > 0, nameof(ViewKindBoss));
        Require(ViewKindProp > 0, nameof(ViewKindProp));
    }

    private static void Require(bool condition, string field)
    {
        if (!condition)
        {
            throw new InvalidOperationException("房间内容生成配置非法：RoomContentSpawnConfig." + field + " 不满足约束。");
        }
    }
}

/// <summary>
/// 默认房间内容生成器：把 <see cref="ContentSlot"/> 翻译成 ECS 生成命令。
/// <para>
/// <b>J5 纪律</b>：**只**通过 <see cref="IRoomBuildContext.Commands"/> 写命令，绝不调用 <c>World.Create</c> /
/// <c>World.Destroy</c>，也不创建任何 Godot 节点（表现由宿主按 <see cref="View"/> 拉取）。
/// 于是「一次性生成几十个实体」不会让正在被遍历的 chunk 失效。
/// </para>
/// <para>
/// <b>J3 纪律</b>：类别判断只读 <see cref="RoomPlan.Category"/> 与 <see cref="ContentSlot.Kind"/>，
/// 用「槽位种类 → 实体配方」的查表代替 <c>if (kind == Boss)</c> 式分支；
/// 新增一种内容 = 在 <see cref="SlotRecipe"/> 里加一行配方（规则7：本文件 + 数据，≤ 2 处）。
/// </para>
/// <para>
/// <b>为什么一次 <c>CreateDeferred</c> 定完整原型</b>：生成会创建新原型 / 新 chunk（结构化变更），
/// 而逐组件 <c>Set</c> 会触发一次迁移。这里按「实体配方」一次性预留完整组件组合，
/// 后续 Set 只是原地写值（沿用 <c>SpawnDirectorSystem</c> 的写法）。
/// </para>
/// </summary>
public sealed class DefaultRoomContentSpawner : IRoomContentSpawner
{
    /// <summary>「全向」朝向：内容实体的初始旋转（单位四元数 = 无旋转）。</summary>
    private static readonly Quaternion NoRotation = Quaternion.Identity;

    private readonly RoomContentSpawnConfig _config;
    private readonly ComponentType[][] _prototypes;
    private readonly World? _world;

    /// <summary>最近一次生成写下的命令数（诊断）。</summary>
    public int LastCommandCount { get; private set; }

    /// <summary>构造。</summary>
    /// <param name="world">
    /// 组件注册入口。生成器需要把组件类型登记进世界（<see cref="World.Register{T}"/>），
    /// 才能拼出原型；它**只**用世界来注册类型，不用世界做任何结构化变更（J5）。
    /// </param>
    /// <param name="config">数值配置。</param>
    public DefaultRoomContentSpawner(World world, RoomContentSpawnConfig config)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();

        // 配方表：每种槽位 → 该实体的完整组件组合。只建一次（稳态零分配）。
        _prototypes = new[]
        {
            BuildEnemyPrototype(world),      // ContentSlotKind.Spawn
            BuildPlayerStartPrototype(world), // ContentSlotKind.PlayerStart
            BuildBossPrototype(world),       // ContentSlotKind.BossSpawn
            BuildPropPrototype(world),       // ContentSlotKind.Prop
            BuildInteractablePrototype(world), // ContentSlotKind.Interactable
            BuildPropPrototype(world),       // ContentSlotKind.Reward
        };
    }

    /// <inheritdoc />
    public string Name => "DefaultRoomContent";

    /// <inheritdoc />
    public bool CanHandle(in RoomPlan room) => room.Category is RoomCategory.Start
        or RoomCategory.Combat
        or RoomCategory.Special
        or RoomCategory.Hidden
        or RoomCategory.Boss
        or RoomCategory.Safe;

    /// <inheritdoc />
    public void Spawn(in RoomPlan room, ReadOnlySpan<ContentSlot> slots, IRoomBuildContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        LastCommandCount = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            ContentSlot slot = slots[i];
            if (slot.Room != room.Id)
            {
                throw new InvalidOperationException(
                    "DefaultRoomContentSpawner：传入的槽位 " + slot + " 不属于房间 " + room.Id + "（分派层过滤有误）。");
            }
            SpawnSlot(in room, in slot, context);
        }
    }

    /// <summary>按槽位种类派发到对应的配方（查表，不是长 switch）。</summary>
    private void SpawnSlot(in RoomPlan room, in ContentSlot slot, IRoomBuildContext context)
    {
        int recipe = RecipeIndex(slot.Kind);
        if (recipe < 0)
        {
            return; // Unspecified / Decor：由装饰布局器负责，不在本生成器职责内
        }
        ComponentType[] prototype = _prototypes[recipe];
        Vector3 world = context.SlotToWorld(in slot);

        Entity entity = context.Commands.CreateDeferred(prototype);
        LastCommandCount++;

        context.Commands.Set(entity, new Transform3
        {
            Position = world,
            Rotation = NoRotation,
            Scale = Vector3.One,
        });
        context.Commands.Set(entity, new Team
        {
            Value = slot.Kind == ContentSlotKind.PlayerStart ? TeamId.Player : TeamId.Enemy,
        });
        context.Commands.Set(entity, new RoomMember
        {
            RoomSlot = room.Id.Slot,
            Generation = room.Id.Generation.Value,
            SlotKind = (byte)slot.Kind,
            SlotIndex = slot.Index,
        });
        context.Commands.Set(entity, new View
        {
            Slot = NoViewSlot,
            Kind = ViewKindOf(slot.Kind),
            Dirty = true,
        });

        if (HasHurtbox(slot.Kind))
        {
            context.Commands.Set(entity, new Hurtbox
            {
                Radius = HurtboxRadiusOf(slot.Kind),
                Height = HurtboxHeightOf(slot.Kind),
                InvulnerableRemaining = NoInvulnerability,
                FlashRemaining = NoFlash,
                KnockbackResistance = FullKnockbackResistance,
            });
        }
        if (HasHealth(slot.Kind))
        {
            float max = MaxHealthOf(slot.Kind);
            context.Commands.Set(entity, new Health { Current = max, Max = max, IsDead = false });
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 配方（组件组合）
    // ─────────────────────────────────────────────────────────────

    private static ComponentType[] BuildEnemyPrototype(World world) => new[]
    {
        world.Register<Transform3>(),
        world.Register<Team>(),
        world.Register<Health>(),
        world.Register<Hurtbox>(),
        world.Register<View>(),
        world.Register<RoomMember>(),
    };

    private static ComponentType[] BuildBossPrototype(World world) => new[]
    {
        world.Register<Transform3>(),
        world.Register<Team>(),
        world.Register<Health>(),
        world.Register<Hurtbox>(),
        world.Register<View>(),
        world.Register<RoomMember>(),
    };

    private static ComponentType[] BuildPlayerStartPrototype(World world) => new[]
    {
        world.Register<Transform3>(),
        world.Register<Team>(),
        world.Register<View>(),
        world.Register<RoomMember>(),
    };

    private static ComponentType[] BuildPropPrototype(World world) => new[]
    {
        world.Register<Transform3>(),
        world.Register<Team>(),
        world.Register<Hurtbox>(),
        world.Register<View>(),
        world.Register<RoomMember>(),
    };

    private static ComponentType[] BuildInteractablePrototype(World world) => new[]
    {
        world.Register<Transform3>(),
        world.Register<Team>(),
        world.Register<View>(),
        world.Register<RoomMember>(),
    };

    /// <summary>槽位种类 → 配方下标；-1 = 本生成器不处理。</summary>
    private static int RecipeIndex(ContentSlotKind kind) => kind switch
    {
        ContentSlotKind.Spawn => 0,
        ContentSlotKind.PlayerStart => 1,
        ContentSlotKind.BossSpawn => 2,
        ContentSlotKind.Prop => 3,
        ContentSlotKind.Interactable => 4,
        ContentSlotKind.Reward => 5,
        _ => -1,
    };

    private static bool HasHurtbox(ContentSlotKind kind) => kind is ContentSlotKind.Spawn
        or ContentSlotKind.BossSpawn
        or ContentSlotKind.Prop
        or ContentSlotKind.Reward;

    private static bool HasHealth(ContentSlotKind kind) => kind is ContentSlotKind.Spawn or ContentSlotKind.BossSpawn;

    private int ViewKindOf(ContentSlotKind kind) => kind switch
    {
        ContentSlotKind.BossSpawn => _config.ViewKindBoss,
        ContentSlotKind.Spawn => _config.ViewKindEnemy,
        _ => _config.ViewKindProp,
    };

    private float HurtboxRadiusOf(ContentSlotKind kind) => kind switch
    {
        ContentSlotKind.BossSpawn => _config.BossHurtboxRadius,
        ContentSlotKind.Spawn => _config.EnemyHurtboxRadius,
        _ => _config.PropHurtboxRadius,
    };

    private float HurtboxHeightOf(ContentSlotKind kind) => kind == ContentSlotKind.Spawn ? _config.EnemyHurtboxHeight : 0f;

    private float MaxHealthOf(ContentSlotKind kind) => kind == ContentSlotKind.BossSpawn ? _config.BossHealth : _config.EnemyHealth;

    /// <summary>「无视图」槽位（宿主 ViewRegistry 看到 0 会自动分配）。</summary>
    private const int NoViewSlot = 0; // arch-ok: 视图槽位 0 = 未分配（协议默认值），不是玩法数值

    /// <summary>初始无无敌帧。</summary>
    private const float NoInvulnerability = 0f; // arch-ok: 「无」的神态值（0 = 不存在），不是玩法数值

    /// <summary>初始无闪白。</summary>
    private const float NoFlash = 0f; // arch-ok: 「无」的常态值（0 = 不存在），不是玩法数值

    /// <summary>完整承受击退（1 = 不受减益）。</summary>
    private const float FullKnockbackResistance = 0f; // arch-ok: 击退抗性 0 = 完整承受（抗性语义，非玩法数值）

    /// <summary>世界引用（诊断用；生成过程不读世界结构）。</summary>
    public World? World => _world;
}
