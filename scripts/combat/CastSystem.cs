using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;
using Game.Core.Ecs;
using Game.Combat.Factory;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Game.Combat;

/// <summary>
/// <b>铸造系统</b>：本帧铸造请求的唯一消费者。
/// <para>
/// <b>它在一帧里的位置</b>：<see cref="UpdateGroup.PreUpdate"/> —— 在固定步长与移动之前。
/// 理由：铸造决定"这一帧世界里多了什么"，后面的移动/推进/表现全部只读它的结果；
/// 放在 Update 里会让"同一帧生成的子弹这一帧不动"，手感上表现为开火首帧卡顿。
/// </para>
/// <para>
/// <b>关于"高性能 / 用对象池减少 Node 的 GC"</b>（用户原话）：
/// </para>
/// <list type="bullet">
/// <item>ECS 实体<u>不是 Node</u>：它们住在竞态无关的 chunk 数组里，创建 = 数组写，没有托管堆分配，
/// 也就不需要"对象池"来回收它们。真正的 Node（MeshInstance3D / 特效实例）GC 压力在宿主层的
/// <c>ViewRegistry</c>，那是另一条任务；</item>
/// <item>本系统保证的是<b>每帧 0 次托管分配</b>：所有临时结构是栈上 struct，
/// <see cref="LastProjectiles"/> 是构造期一次性分配的环形缓冲，<see cref="CastQueue"/> 是定长环——稳态不 new 任何东西；</item>
/// <item>因此本系统的"池"指的是<b>结构化缓冲池</b>（预分配 + 复用），而不是 class 对象池：
/// 值类型在栈上分配本来就是免费的。</item>
/// </list>
/// <para>
/// <b>为什么系统自己不造实体</b>：真正的"造"由注入的 <see cref="IObjectFactory"/> 完成。
/// 武器射子弹 / 武器射怪物 / 子弹爆炸再射子弹 / 子弹变成怪物是<b>同一段代码</b>，
/// 差别只在契约里读了哪个字段（见 <see cref="EcsObjectFactory"/> 的注释）。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.PreUpdate)]
public sealed class CastSystem : SystemBase
{
    private readonly ContentIndex _index;
    private readonly ContentRegistry _registry;
    private readonly CastQueue _queue;
    private readonly IObjectFactory _factory;
    private readonly IRandomProvider _random;
    private readonly int _defaultWeaponSlotId;

    // ── 预分配缓冲（构造期一次，之后只读/复用；"稳态零分配"靠它们） ──
    private readonly Entity[] _producedRing;
    private readonly Entity[] _scratchEntities;
    private readonly int _ringCapacity;
    private int _ringHead;
    private int _ringCount;

    // 每帧的统计（诊断 / 测试断言）
    private int _castsThisFrame;
    private int _rejectedThisFrame;

    /// <summary>构造。</summary>
    /// <param name="index">内容表（契约取值用）。</param>
    /// <param name="registry">寄存器（解析"这帧该用哪个武器"）。</param>
    /// <param name="queue">请求队列（上游系统投递）。</param>
    /// <param name="factory">通用铸造门面。</param>
    /// <param name="random">随机源（求值上下文与熔炉都从这里取；子流隔离，不影响别人）。</param>
    /// <param name="defaultWeaponSlotId">请求没指定武器槽位时用的槽位（-1 = 不回落，直接拒绝）。</param>
    /// <param name="ringCapacity">产物环形缓冲容量（给表现层/测试读最近 N 个产物）。</param>
    /// <param name="scratchCapacity">每帧最多处理多少条请求（超出部分留到下一帧，不丢）。</param>
    public CastSystem(
        ContentIndex index,
        ContentRegistry registry,
        CastQueue queue,
        IObjectFactory factory,
        IRandomProvider random,
        int defaultWeaponSlotId = -1,
        int ringCapacity = 256,  // arch-ok: 产物环形缓冲的预分配容量，结构性常量而非玩法数值
        int scratchCapacity = 1024)  // arch-ok: 单帧请求处理预算的预分配容量，结构性常量而非玩法数值
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (random is null)
        {
            throw new ArgumentNullException(nameof(random));
        }
        _random = random.Fork(CastSalt);
        _defaultWeaponSlotId = defaultWeaponSlotId;

        _ringCapacity = ringCapacity > 0 ? ringCapacity : 1;
        _producedRing = new Entity[_ringCapacity];
        _scratchEntities = new Entity[scratchCapacity > 0 ? scratchCapacity : 1];
    }

    /// <summary>随机子流的盐值（与其他系统隔离：本系统多抽一次不会打乱别人）。</summary>
    private const int CastSalt = 0xCA57;

    /// <inheritdoc />
    public override string Name => "Cast";

    /// <summary>本帧成功铸造数。</summary>
    public int CastsThisFrame => _castsThisFrame;

    /// <summary>本帧被拒绝的请求数（契约缺失 / 法力不足 / 槽位无解）。</summary>
    public int RejectedThisFrame => _rejectedThisFrame;

    /// <summary>累计成功铸造数。</summary>
    public int TotalCasts { get; private set; }

    /// <summary>累计拒绝数。</summary>
    public int TotalRejected { get; private set; }

    /// <summary>
    /// 本帧产物（最近 N 个，环形缓冲）。<b>只能在本帧 PreUpdate 之后读</b>——
    /// 下一帧会被覆盖。这是"零分配"与"表现层/测试能查到结果"之间的折中：
    /// 想长期持有请复制到自己那里，别拿这个列表当下游状态。
    /// </summary>
    public IReadOnlyList<Entity> LastProjectiles => new EntityRingView(_producedRing, _ringHead, _ringCount, _ringCapacity);

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _castsThisFrame = 0;
        _rejectedThisFrame = 0;
        _ringHead = 0;
        _ringCount = 0;

        // 每帧开始时把"上一帧的熔炉派生"回收掉：熔炉产物只活在一次射击里。
        // 放在这里而不是帧末，是因为帧末之后可能有 HUD 读 CastState.LastPerf，那时还要能读到。
        _registry.Purge(World.Version);

        int processed = 0;
        while (_queue.TryDequeue(out CastRequest request))
        {
            if (processed >= _scratchEntities.Length)
            {
                // 超过本帧预算：剩下的留到下一帧（不丢），避免某一帧突然涌入导致峰刺。
                _queue.Enqueue(in request);
                break;
            }

            if (TryCast(in request, out Entity produced))
            {
                _castsThisFrame++;
                TotalCasts++;
                _scratchEntities[processed] = produced;
                PushProduced(produced);
            }
            else
            {
                _rejectedThisFrame++;
                TotalRejected++;
            }
            processed++;
        }
    }

    /// <summary>
    /// 单条铸造：解析武器 → 构造求值上下文 → 熔炉选物 → 交给工厂。
    /// <para>整条路径只在栈上跑，不产生任何托管分配。</para>
    /// </summary>
    public bool TryCast(in CastRequest request, out Entity produced)
    {
        produced = Entity.None;

        int weaponSlotId = request.WeaponSlotId >= 0 ? request.WeaponSlotId : _defaultWeaponSlotId;
        if (weaponSlotId < 0)
        {
            return false;
        }

        // ① 构造求值上下文：链深来自请求（子弹再射子弹时逐层 +1），随机源是本系统的子流。
        ForgeValueWorld world = new ForgeValueWorld(_index, _registry, request.ChainDepth, 0, 1, _random);
        ValueContext ctx = new ValueContext(world, default, _random);

        // ② 寄存器解析：这帧该用哪个武器槽位上的东西（热路径 = 一次数组读）。
        int weaponDefId = _registry.Resolve(weaponSlotId, in ctx);
        ForgeResult forgeResult = default;

        if (request.RecipeTagIndex >= 0)
        {
            // 有配方：先走熔炉，在槽位所属簇里按 tag 选接收者；没选中就回落到槽位当前值。
            ulong recipeTag = (ulong)(uint)request.RecipeTagIndex;
            int forged = _registry.ResolveWithRegister(weaponSlotId, in ctx, recipeTag, out forgeResult);
            if (forgeResult.IsMatched)
            {
                weaponDefId = forged;
            }
        }

        WeaponContract? weapon = _index.GetWeapon(weaponDefId);
        if (weapon is null)
        {
            return false;
        }

        // ③ 消耗门控：法力消耗由数值包层（Game.Data）提供，本轮契约里没有 ManaCost 列，
        //     所以用「本次发射份数」作为可配平的门控量（0 份 = 发不出来）。
        //     等武器表补上 ManaCost 列，这里换成一次规则求值，其余代码不动。
        float required = ValueResolver.Resolve(weapon.Count, in ctx);
        if (required > request.ManaAvailable)
        {
            return false;
        }

        // ④ 交给工厂：这里开始才可能产生实体（仍然走 World.Commands，帧末才真正落地，J5）。
        if (!_factory.TryCast(in request, out produced))
        {
            return false;
        }

        // ⑤ 诊断向量写回发射者（HUD / 调试面板一次查询就能读到）。
        WriteCastState(request.Source, in forgeResult, weapon, produced);
        return true;
    }

    private void WriteCastState(Entity source, in ForgeResult forgeResult, WeaponContract weapon, Entity produced)
    {
        if (source.IsNone || !World.IsAlive(source) || !World.Has<CastState>(source))
        {
            return;
        }
        // 取 ref 直接改：CastState 是"纯诊断"，不参与任何玩法结算，就地改写不违反"一个字段一个写入者"。
        ref CastState state = ref World.Get<CastState>(source);
        state.LastRecipeTag = forgeResult.MatchedCount > 0 ? forgeResult.MatchedCount : CastState.NoRecipe;
        state.ViableCount = weapon.TeamLayer != TeamId.Neutral ? 1 : 0;
        state.LastPerf = forgeResult.MatchedCount > 0 ? forgeResult.Perf : 0f;
        state.LastReceiver = produced;
    }

    private void PushProduced(Entity produced)
    {
        if (_ringCount < _ringCapacity)
        {
            _producedRing[(_ringHead + _ringCount) % _ringCapacity] = produced;
            _ringCount++;
        }
        else
        {
            // 满了就覆盖最旧的：表现层只关心"最近这一帧的产物"。
            _producedRing[_ringHead] = produced;
            _ringHead = (_ringHead + 1) % _ringCapacity;
        }
    }

    /// <summary>
    /// 环形缓冲的只读视图（<b>索引器零分配</b>：结构体 + 取模，不进堆）。
    /// <para>
    /// <b>为什么 <c>GetEnumerator</c> 返回结构体枚举器</b>：<c>foreach</c> 走 <c>IEnumerable&lt;T&gt;</c> 时会装箱一个
    /// 枚举器对象；返回 <c>RingEnumerator</c> 这种 struct 枚举器能让 <c>foreach</c> 完全在栈上展开（无分配）。
    /// 显式接口实现保留给 <c>IReadOnlyList</c> 的契约，正常代码走不到那条路径。
    /// </para>
    /// </summary>
    private readonly struct EntityRingView : IReadOnlyList<Entity>
    {
        private readonly Entity[] _items;
        private readonly int _head;
        private readonly int _count;
        private readonly int _capacity;

        public EntityRingView(Entity[] items, int head, int count, int capacity)
        {
            _items = items;
            _head = head;
            _count = count;
            _capacity = capacity;
        }

        public int Count => _count;

        public Entity this[int index] => _items[(_head + index) % _capacity];

        public RingEnumerator GetEnumerator() => new RingEnumerator(_items, _head, _count, _capacity);

        IEnumerator<Entity> System.Collections.Generic.IEnumerable<Entity>.GetEnumerator() => new BoxedEnumerator(_items, _head, _count, _capacity);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            new BoxedEnumerator(_items, _head, _count, _capacity);
    }

    /// <summary>按索引顺序遍历环形缓冲的结构体枚举器（无分配）。</summary>
    public struct RingEnumerator
    {
        private readonly Entity[] _items;
        private readonly int _head;
        private readonly int _count;
        private readonly int _capacity;
        private int _index;

        internal RingEnumerator(Entity[] items, int head, int count, int capacity)
        {
            _items = items;
            _head = head;
            _count = count;
            _capacity = capacity;
            _index = -1;
        }

        public Entity Current => _items[(_head + _index) % _capacity];

        public bool MoveNext()
        {
            _index++;
            return _index < _count;
        }
    }

    /// <summary>装箱路径用的枚举器（只服务 <c>IEnumerable&lt;T&gt;</c>；正常 <c>foreach</c> 不会走到）。</summary>
    private sealed class BoxedEnumerator : IEnumerator<Entity>, System.Collections.IEnumerator
    {
        private readonly Entity[] _items;
        private readonly int _head;
        private readonly int _count;
        private readonly int _capacity;
        private int _index;

        public BoxedEnumerator(Entity[] items, int head, int count, int capacity)
        {
            _items = items;
            _head = head;
            _count = count;
            _capacity = capacity;
            _index = -1;
        }

        public Entity Current => _items[(_head + _index) % _capacity];

        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            _index++;
            return _index < _count;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// 求值环境：把「链深 / 可发动数 / 性能预算 / 随机源 / 位矩阵」交给 <see cref="ValueResolver"/>。
/// <para>
/// <b>为什么是 struct</b>：它是每次铸造临时构造一次的对象，值类型让它活在栈上（零 GC）。
/// </para>
/// <para>
/// <b>为什么属性查询返回 false 而不是去查数值包</b>：数值包仓库属于 <c>Game.Data</c>（上层），
/// 战斗层反向依赖它会让依赖图翻转。所以这里把"取属性"委托给一个可选的宿主侧后端
/// （<c>StatQuery</c> 委托，由宿主在装配期注入）；没注入时按"未声明"处理，
/// 规则会回落到字面量——这正是 <see cref="IValueWorld"/> 契约里 <c>TryStat</c> 的设计意图。
/// </para>
/// </summary>
public readonly struct ForgeValueWorld : IValueWorld
{
    /// <summary>「按数值包取属性」的宿主侧后端签名（返回 false 表示该属性未声明）。</summary>
    public delegate bool StatQuery(in StatPackageIds ids, StatId id, out float value);

    private readonly ContentIndex _index;
    private readonly ContentRegistry _registry;
    private readonly IRandomProvider _random;
    private readonly StatQuery? _statQuery;
    private readonly int _chainDepth;
    private readonly int _chainCount;
    private readonly int _viableCount;
    private readonly int _tagCount;

    /// <summary>构造。</summary>
    public ForgeValueWorld(
        ContentIndex index,
        ContentRegistry registry,
        int chainDepth,
        int chainCount,
        int viableCount,
        IRandomProvider random,
        StatQuery? statQuery = null,
        int tagCount = ContentBitLimits.TagBits)
    {
        _index = index;
        _registry = registry;
        _chainDepth = chainDepth;
        _chainCount = chainCount;
        _viableCount = viableCount;
        _random = random;
        _statQuery = statQuery;
        _tagCount = tagCount;
    }

    /// <summary>内容表。</summary>
    public ContentIndex Index => _index;

    /// <summary>寄存器。</summary>
    public ContentRegistry Registry => _registry;

    /// <inheritdoc />
    public int ChainDepth => _chainDepth;

    /// <inheritdoc />
    public int ChainCount => _chainCount;

    /// <inheritdoc />
    public int ViableCount => _viableCount;

    /// <inheritdoc />
    public float Perf => 1f;

    /// <inheritdoc />
    public IRandomProvider Random => _random;

    /// <inheritdoc />
    public int TagCount => _tagCount;

    /// <inheritdoc />
    public bool TryStat(in StatPackageIds ids, StatId id, out float value)
    {
        if (_statQuery is null)
        {
            value = 0f;
            return false;
        }
        return _statQuery(in ids, id, out value);
    }

    /// <inheritdoc />
    public bool TryWeaponStat(StatId id, out float value)
    {
        // 链上没有武器时返回 false，规则会回落到 ValueRef.A（契约里写死的武器默认值）。
        value = 0f;
        return false;
    }

    /// <inheritdoc />
    public int Flags(byte tag) => 0;
}
