using System;
using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 世界：实体 / 组件 / 原型的唯一拥有者（ECS.md 第 5.2 节）。
/// <para>
/// <b>职责边界</b>：它只回答「数据在哪、怎么搬、怎么查」，不含任何玩法规则。
/// 「这一步该做什么」属于 <see cref="SystemBase"/>；内核不知道「玩家」「怪物」是什么，
/// 于是永远不会因为新增玩法而改动（规则7）。
/// </para>
/// <para>
/// <b>线程纪律</b>：结构化变更（<see cref="Create(System.Collections.Generic.IReadOnlyList{ComponentType})"/> /
/// <see cref="Destroy"/> / <see cref="Set{T}"/> 的「添加」分支 / <see cref="Remove{T}"/>）只能在本线程发起；
/// 只读遍历可以在 Job 线程里跑。世界<u>不加锁</u>，而是用守卫（<see cref="EnterIteration"/> /
/// <see cref="AssertStructuralAllowed"/>）把误用立刻变成中文异常——加锁会把「遍历期间改结构」
/// 从确定性报错变成随机崩溃，难查一百倍。
/// </para>
/// <para>
/// <b>为什么是 sealed class</b>：World 是唯一实现，方法签名里返回 <c>ref T</c>，
/// 接口包装既无扩展收益又挡不住 ref 语义。测试用「私有 <see cref="ComponentTypeRegistry"/>」
/// 而不是 mock（见 ECS.md 第 10 节）。
/// </para>
/// </summary>
public sealed class World : IDisposable
{
    /// <summary>
    /// 世代号起点：活实体的 generation 恒 ≥ 1（0 保留给「空闲槽位」与 <see cref="Entity.None"/>）。
    /// <para><see cref="GenerationPool"/> 从 0 开始发号，第一次发的就是 1。</para>
    /// </summary>
    public const int InitialGeneration = 0;

    // 批量生成时一次插入原型的最大批量（超出则分次插入）。
    private const int MaxBatchInsertChunk = 512;

    private readonly ComponentTypeRegistry _registry;
    private readonly EntityIndex _index;
    private readonly System.Collections.Generic.List<Archetype> _archetypes;
    private readonly System.Collections.Generic.Dictionary<long, Archetype> _archetypeBySignature;

    // 签名哈希 -> 原型 的二次索引：命令只带 TypeSet（值类型），提交时靠 SignatureHash 找回原型。
    private readonly System.Collections.Generic.Dictionary<long, Archetype> _archetypeByHash;
    private readonly GenerationPool _generations;
    private readonly int _chunkCapacity;
    private readonly CommandBuffer _commands;

    // 命令缓冲用的「组件值队列」：每个组件一份列，命令里只存下标（不装箱）。
    // 按组件 id 索引；元素是弱绑定的 ICommandValueQueue，实际类型 CommandValueQueue<T>。
    private readonly System.Collections.Generic.List<object> _commandValues = new System.Collections.Generic.List<object>();

    // 复用缓冲：查原型 / 建原型 / 排序组件 id 的临时工作区，避免每次分配。
    private readonly int[] _scratchIds = new int[TypeSet.MaxComponentCount];

    private int _version;
    private int _iterationDepth;
    private bool _disposed;

    /// <summary>
    /// 创建世界。<paramref name="registry"/> 不传 = 本世界私有注册表（测试友好、多世界互不污染）；
    /// <paramref name="chunkCapacity"/> 不传 = <see cref="EcsConfig.DefaultChunkCapacity"/>。
    /// </summary>
    public World(ComponentTypeRegistry? registry = null, int? chunkCapacity = null)
    {
        _registry = registry ?? new ComponentTypeRegistry();
        _chunkCapacity = chunkCapacity ?? EcsConfig.DefaultChunkCapacity;
        EcsConfig.ValidateChunkCapacity(_chunkCapacity);

        _index = new EntityIndex(EntityIndex.InitialSlotCapacity);
        _archetypes = new System.Collections.Generic.List<Archetype>(EcsConfig.InitialArchetypeCapacity);
        _archetypeBySignature = new System.Collections.Generic.Dictionary<long, Archetype>(EcsConfig.InitialArchetypeCapacity);
        _archetypeByHash = new System.Collections.Generic.Dictionary<long, Archetype>(EcsConfig.InitialArchetypeCapacity);
        _generations = new GenerationPool(InitialGeneration);
        _commands = new CommandBuffer(this);
        _version = 1;
    }

    // ─────────────────────────────────────────────────────────────
    // 基础属性
    // ─────────────────────────────────────────────────────────────

    /// <summary>本世界使用的组件注册表。</summary>
    public ComponentTypeRegistry Registry => _registry;

    /// <summary>本世界的 chunk 容量（ECS.md 第 5.3 节：默认 128，可经构造参数注入）。</summary>
    public int ChunkCapacity => _chunkCapacity;

    /// <summary>活实体数。</summary>
    public int EntityCount => _index.AliveCount;

    /// <summary>已创建的原型数量（含当前为空的；空原型保留复用，避免刷怪锯齿）。</summary>
    public int ArchetypeCount => _archetypes.Count;

    /// <summary>
    /// 结构化变更版本号：每次生成 / 销毁 / 增删组件后前进。
    /// <para>Job 用它检测「遍历期间世界被改过」；<see cref="Query.SliceJobBatches"/> 会把当时的版本号带出去。</para>
    /// </summary>
    public int Version => VersionGuard.Read(ref _version);

    /// <summary>结构化命令缓冲：帧内随意写，帧末由 <c>CommandFlushSystem</c> 一次提交（J5 的执行方式）。</summary>
    public CommandBuffer Commands => _commands;

    /// <summary>当前是否正在提交 <see cref="CommandBuffer"/>（提交期间临时放开结构化变更守卫）。</summary>
    public bool IsFlushingCommands { get; internal set; }

    /// <summary>当前是否在遍历区间内（<see cref="Query"/> 的 ForEach 期间为 true）。</summary>
    public bool IsIterating => _iterationDepth > 0;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>运行时统计快照（性能 HUD 直接读它）。O(原型数)，不要在每帧的实体循环里调用。</summary>
    public WorldStats Stats
    {
        get
        {
            int chunks = 0;
            long componentBytes = 0;
            for (int i = 0; i < _archetypes.Count; i++)
            {
                chunks += _archetypes[i].ChunkCount;
                componentBytes += _archetypes[i].ComponentMemoryBytes;
            }
            return new WorldStats
            {
                EntityCount = _index.AliveCount,
                ArchetypeCount = _archetypes.Count,
                ChunkCount = chunks,
                ComponentMemoryBytes = componentBytes,
                EntityIndexBytes = _index.Bytes,
                RecycledEntityHits = _index.TypedRecycledCount,
                Version = VersionGuard.Read(ref _version),
                IsFlushingCommands = IsFlushingCommands,
            };
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 原型表（内部：Query 创建时一次性匹配，遍历期间不再匹配）
    // ─────────────────────────────────────────────────────────────

    /// <summary>全部原型（含当前为空的）。Query 构造时拿它做一次匹配。</summary>
    internal Archetype[] ArchetypesSnapshot() => _archetypes.ToArray();

    /// <summary>按 id 取原型。</summary>
    internal Archetype ArchetypeAt(int index) => _archetypes[index];

    // ─────────────────────────────────────────────────────────────
    // 生命周期
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 按组件种类创建一个实体（结构化变更）。组件初值为 default，[Identity] 字段被内核自动写成新实体号。
    /// </summary>
    public Entity Create(System.Collections.Generic.IReadOnlyList<ComponentType> components)
    {
        if (components is null)
        {
            throw new GameEcsException("Create 的组件列表不能为 null（要空实体请传长度为 0 的列表）。");
        }
        Span<int> ids = stackalloc int[TypeSet.MaxComponentCount];
        int count = NormalizeIds(components, ids);
        return CreateCore(ids.Slice(0, count));
    }

    /// <summary>便捷重载：1 个组件。</summary>
    public Entity Create<TA>(in TA a) where TA : unmanaged
    {
        Span<int> ids = stackalloc int[1];
        ids[0] = IdOf<TA>();
        Entity entity = CreateCore(ids);
        ResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot);
        Archetype archetype = _archetypes[archetypeIndex];
        // 注意用 ColumnOf 而不是 Pools[0]：列序由「升序组件 id」决定，单组件原型里列序恒为 0，
        // 但写成 ColumnOf 才能与迁移/查询路径保持同一种写法，改原型布局时不会静默错列。
        ComponentPool<TA> pool = (ComponentPool<TA>)archetype.ChunkAt(chunkIndex)!.Pools[archetype.ColumnOf(ids[0])];
        pool.SlotRef(slot) = a;
        pool.WriteIdentityAt(slot, entity);
        return entity;
    }

    /// <summary>便捷重载：2 个组件。</summary>
    public Entity Create<TA, TB>(in TA a, in TB b) where TA : unmanaged where TB : unmanaged
    {
        Span<int> ids = stackalloc int[2];
        ids[0] = IdOf<TA>();
        ids[1] = IdOf<TB>();
        Entity entity = CreateCore(ids);
        ResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot);
        Archetype archetype = _archetypes[archetypeIndex];
        Chunk chunkObj = archetype.ChunkAt(chunkIndex)!;
        ComponentPool<TA> pa = (ComponentPool<TA>)chunkObj.Pools[archetype.ColumnOf(ids[0])];
        ComponentPool<TB> pb = (ComponentPool<TB>)chunkObj.Pools[archetype.ColumnOf(ids[1])];
        pa.SlotRef(slot) = a;
        pb.SlotRef(slot) = b;
        pa.WriteIdentityAt(slot, entity);
        pb.WriteIdentityAt(slot, entity);
        return entity;
    }

    /// <summary>便捷重载：3 个组件。</summary>
    public Entity Create<TA, TB, TC>(in TA a, in TB b, in TC c)
        where TA : unmanaged where TB : unmanaged where TC : unmanaged
    {
        Span<int> ids = stackalloc int[3];
        ids[0] = IdOf<TA>();
        ids[1] = IdOf<TB>();
        ids[2] = IdOf<TC>();
        Entity entity = CreateCore(ids);
        ResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot);
        Archetype archetype = _archetypes[archetypeIndex];
        Chunk chunkObj = archetype.ChunkAt(chunkIndex)!;
        ComponentPool<TA> pa = (ComponentPool<TA>)chunkObj.Pools[archetype.ColumnOf(ids[0])];
        ComponentPool<TB> pb = (ComponentPool<TB>)chunkObj.Pools[archetype.ColumnOf(ids[1])];
        ComponentPool<TC> pc = (ComponentPool<TC>)chunkObj.Pools[archetype.ColumnOf(ids[2])];
        pa.SlotRef(slot) = a;
        pb.SlotRef(slot) = b;
        pc.SlotRef(slot) = c;
        pa.WriteIdentityAt(slot, entity);
        pb.WriteIdentityAt(slot, entity);
        pc.WriteIdentityAt(slot, entity);
        return entity;
    }

    /// <summary>
    /// 批量创建同构型实体（生成器主路径：一次刷一整波子弹）。
    /// 结果写进 <paramref name="destination"/> 前 <paramref name="count"/> 格，全程零分配。
    /// <para>
    /// <b>为什么批量接口是「两个组件的 Span」而不是一个泛型列表</b>：刷怪/开火的输入本来就是
    /// 「位置数组 + 速度数组」（或 DefId 数组），Span 入参让调用方可以直接用栈缓冲或池化数组。
    /// 更多组件请用 <see cref="CommandBuffer.CreateDeferred"/> + <c>Set</c>。
    /// </para>
    /// </summary>
    public void CreateBatch<TA, TB>(ReadOnlySpan<TA> a, ReadOnlySpan<TB> b, int count, Span<Entity> destination)
        where TA : unmanaged where TB : unmanaged
    {
        if (count < 0 || count > destination.Length || count > a.Length || count > b.Length)
        {
            throw new GameEcsException("CreateBatch 的参数长度不足以容纳 count 个实体。");
        }
        if (count == 0)
        {
            return;
        }
        AssertStructuralAllowed("World.CreateBatch", Entity.None);

        Span<int> ids = stackalloc int[2];
        ids[0] = IdOf<TA>();
        ids[1] = IdOf<TB>();
        Archetype archetype = ResolveOrCreateArchetype(ids);
        int columnA = archetype.ColumnOf(ids[0]);
        int columnB = archetype.ColumnOf(ids[1]);

        // 先定实体号（尽量复用同签名回收号），再一次性插入原型，最后一列列写值。
        for (int i = 0; i < count; i++)
        {
            destination[i] = AllocateEntity(archetype);
        }
        int written = 0;
        while (written < count)
        {
            int take = count - written;
            if (take > MaxBatchInsertChunk)
            {
                take = MaxBatchInsertChunk;
            }
            archetype.CreateEntities(destination.Slice(written, take), this);
            written += take;
        }
        for (int i = 0; i < count; i++)
        {
            Entity entity = destination[i];
            ResolveLocation(entity, out _, out int chunkIndex, out int slot);
            Chunk chunkObj = archetype.ChunkAt(chunkIndex)!;
            ComponentPool<TA> poolA = (ComponentPool<TA>)chunkObj.Pools[columnA];
            ComponentPool<TB> poolB = (ComponentPool<TB>)chunkObj.Pools[columnB];
            poolA.SlotRef(slot) = a[i];
            poolB.SlotRef(slot) = b[i];
        }
        VersionGuard.Advance(ref _version);
    }

    /// <summary>
    /// 销毁实体（结构化变更）。返回 false = 实体已不存在，<u>不抛异常</u>：
    /// 帧末提交时对「已销毁实体」的命令必须静默跳过（ECS.md 第 5.5 节），异常会打断整帧。
    /// </summary>
    public bool Destroy(Entity entity)
    {
        if (!TryResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot))
        {
            return false;
        }
        AssertStructuralAllowed("World.Destroy", entity);
        Archetype archetype = _archetypes[archetypeIndex];

        // 记住「同组件集合最近销毁的世代号」：新实体分配时据此避让，保证旧句柄不会被误判成活。
        for (int i = 0; i < archetype.ComponentCount; i++)
        {
            archetype.ComponentAt(i).LastDestructionGeneration = entity.Generation;
        }

        archetype.RemoveAt(chunkIndex, slot, this);
        _index.Release(entity.Index);
        _index.PushRecycled(entity, archetype.Components.SignatureHash);
        VersionGuard.Advance(ref _version);
        return true;
    }

    /// <summary>
    /// 实体是否还活着（跨帧复核句柄的唯一正确方式，ECS.md 第 5.1 节）。
    /// <para>句柄只在「本帧 / 下一帧」内有效；缓存了 <see cref="Entity"/> 的字段必须每帧复核。</para>
    /// <para>
    /// <b>零值兜底（<c>default(Entity)</c> = <c>(0, 0)</c>）</b>：<see cref="Entity.Index"/> 0 是<u>合法槽位</u>
    /// （第一个实体就住这里），所以不能靠「index == 0 即空」来判。真正的保证在 generation 侧：
    /// 活实体的 generation 恒 ≥ 1（发号池从 0 开始，第一个发到的是 1），因此 <c>(*, 0)</c> 永远不可能
    /// 匹配上一条活记录。这里把 <c>generation &lt;= 0</c> 显式写成短路分支，是为了让这条不变量<u>能被执行路径证明</u>，
    /// 而不是靠「碰巧不相等」。
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(Entity entity) =>
        entity.Generation > 0 && entity.Index >= 0 && _index.IsAlive(entity.Index, entity.Generation);

    // ─────────────────────────────────────────────────────────────
    // 组件读写
    // ─────────────────────────────────────────────────────────────

    /// <summary>实体是否继承了组件 T。</summary>
    public bool Has<T>(Entity entity) where T : unmanaged
    {
        if (!TryResolveLocation(entity, out int archetypeIndex, out _, out _))
        {
            return false;
        }
        return _archetypes[archetypeIndex].Components.Contains(IdOf<T>());
    }

    /// <summary>
    /// 取组件引用（<c>ref</c> 直写，零拷贝）。
    /// <para>
    /// <b>为什么未继承时抛异常而不是返回 default</b>：<c>default(Transform3)</c> 会让实体原地不动，
    /// 静默错误是最贵的错误类型；未继承组件是编程错误，必须在开发期炸出来（ECS.md 第 5.2 节）。
    /// </para>
    /// <para>
    /// <b>⚠ 引用生命周期</b>：<c>ref</c> 只在下一次<u>结构化变更</u>（生成/销毁/增删组件）之前有效。
    /// 迁移会让实体搬到另一个 chunk，旧 <c>ref</c> 指向的格位已失效。跨结构变更请重新 <c>Get</c>。
    /// </para>
    /// <para>
    /// <b>热路径提示</b>：每帧循环里<u>不要</u>对每个实体调一次 <c>Get</c>（每次都要查原型列号）。
    /// 用 <see cref="Query.ForEach{T0}(in ISpanAction{T0})"/> 等 Span 接口，一次解析列、整块遍历。
    /// </para>
    /// </summary>
    public ref T Get<T>(Entity entity) where T : unmanaged
    {
        if (!TryResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot))
        {
            throw GameEcsException.DeadEntity(entity, string.Concat("读取组件 ", typeof(T).Name));
        }
        Archetype archetype = _archetypes[archetypeIndex];
        int column = archetype.ColumnOf(IdOf<T>());
        if (column < 0)
        {
            throw GameEcsException.MissingComponent(entity, typeof(T), "Get");
        }
        return ref ((ComponentPool<T>)archetype.ChunkAt(chunkIndex)!.Pools[column]).SlotRef(slot);
    }

    /// <summary>尝试取组件值（不抛异常；需要 ref 直写时用 <see cref="Get{T}"/>）。</summary>
    public bool TryGet<T>(Entity entity, out T value) where T : unmanaged
    {
        if (!TryResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot))
        {
            value = default;
            return false;
        }
        Archetype archetype = _archetypes[archetypeIndex];
        int column = archetype.ColumnOf(IdOf<T>());
        if (column < 0)
        {
            value = default;
            return false;
        }
        value = ((ComponentPool<T>)archetype.ChunkAt(chunkIndex)!.Pools[column]).SlotRef(slot);
        return true;
    }

    /// <summary>
    /// 有则更新、无则继承（= 一次原型迁移）。语义幂等，专为「给实体挂一个 buff / 状态」设计：
    /// 一步到位比 <c>Has -&gt; Add</c> 两步更省，且重复调用无副作用。
    /// </summary>
    public void Set<T>(Entity entity, in T value) where T : unmanaged
    {
        if (!TryResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot))
        {
            throw GameEcsException.DeadEntity(entity, string.Concat("写入组件 ", typeof(T).Name));
        }
        int componentId = IdOf<T>();
        Archetype source = _archetypes[archetypeIndex];
        int existingColumn = source.ColumnOf(componentId);
        if (existingColumn >= 0)
        {
            ((ComponentPool<T>)source.ChunkAt(chunkIndex)!.Pools[existingColumn]).SlotRef(slot) = value;
            return;
        }

        AssertStructuralAllowed("World.Set", entity);
        Archetype target = ResolveOrCreateArchetypeWithExtra(source, componentId);
        Migrate(entity, source, chunkIndex, slot, target);
        ResolveLocation(entity, out _, out int newChunk, out int newSlot);
        ComponentPool<T> pool = (ComponentPool<T>)target.ChunkAt(newChunk)!.Pools[target.ColumnOf(componentId)];
        pool.SlotRef(newSlot) = value;
        // 再写一次 [Identity]：调用方传进来的 value 里那个 Self 字段通常还是 default(Entity)，
        // 整结构赋值会把它盖掉。内核必须保证「组件里的 Self 恒等于它所属的实体」，所以最后一步始终重写。
        pool.WriteIdentityAt(newSlot, entity);
        VersionGuard.Advance(ref _version);
    }

    /// <summary>移除组件（= 一次原型迁移）。没有该组件时返回 false。</summary>
    public bool Remove<T>(Entity entity) where T : unmanaged => RemoveById(entity, IdOf<T>());

    // ─────────────────────────────────────────────────────────────
    // 查询
    // ─────────────────────────────────────────────────────────────

    /// <summary>按组件种类查询：required 全部具备、excluded 一个都不许有（ECS.md 第 5.2 / 5.3 节）。</summary>
    public Query Query(
        System.Collections.Generic.IReadOnlyList<ComponentType> required,
        System.Collections.Generic.IReadOnlyList<ComponentType>? excluded = null)
    {
        if (required is null)
        {
            throw new GameEcsException("Query 的 required 列表不能为 null（要查全部实体请用空列表）。");
        }
        Span<int> req = stackalloc int[TypeSet.MaxComponentCount];
        int reqCount = NormalizeIds(required, req);
        Span<int> exc = stackalloc int[TypeSet.MaxComponentCount];
        int excCount = 0;
        if (excluded is not null)
        {
            excCount = NormalizeIds(excluded, exc);
        }
        return BuildQuery(req.Slice(0, reqCount), exc.Slice(0, excCount));
    }

    /// <summary>便捷重载：查询单个组件。</summary>
    public Query Query<T0>() where T0 : unmanaged
    {
        Span<int> ids = stackalloc int[1];
        ids[0] = IdOf<T0>();
        return BuildQuery(ids, ReadOnlySpan<int>.Empty);
    }

    /// <summary>便捷重载：查询两个组件。</summary>
    public Query Query<T0, T1>() where T0 : unmanaged where T1 : unmanaged
    {
        Span<int> ids = stackalloc int[2];
        ids[0] = IdOf<T0>();
        ids[1] = IdOf<T1>();
        return BuildQuery(ids, ReadOnlySpan<int>.Empty);
    }

    /// <summary>便捷重载：查询三个组件。</summary>
    public Query Query<T0, T1, T2>() where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged
    {
        Span<int> ids = stackalloc int[3];
        ids[0] = IdOf<T0>();
        ids[1] = IdOf<T1>();
        ids[2] = IdOf<T2>();
        return BuildQuery(ids, ReadOnlySpan<int>.Empty);
    }

    // ─────────────────────────────────────────────────────────────
    // 线程 / 重入守卫
    // ─────────────────────────────────────────────────────────────

    /// <summary>进入「遍历区间」：此期间结构化变更会被拒绝（J5 的执行方式）。可重入。</summary>
    public void EnterIteration() => _iterationDepth++;

    /// <summary>离开「遍历区间」。</summary>
    public void ExitIteration()
    {
        if (_iterationDepth > 0)
        {
            _iterationDepth--;
        }
    }

    /// <summary>
    /// 断言「此刻允许做结构化变更」。违反立刻抛中文异常，附带操作名与实体号。
    /// <para>命令提交期间（<see cref="CommandBuffer.Flush"/>）临时让开这条规则——那正是唯一合法的批量变更窗口。</para>
    /// </summary>
    public void AssertStructuralAllowed(string operation, Entity entity)
    {
        if (IsFlushingCommands)
        {
            return;
        }
        if (_iterationDepth > 0)
        {
            throw GameEcsException.InsideIteration(operation, entity);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 内部：组件注册 / 原型解析 / 实体分配 / 迁移
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 注册组件类型并按 id 取回。
    /// <para>
    /// 快路径是静态泛型槽 <see cref="ComponentMeta{T}"/>：一次引用比较 + 一次静态读，
    /// 不碰字典。但<u>必须校验属主</u>——组件 id 是按注册表各自从 0 发的，
    /// 多个 <see cref="World"/> 并存时（重开一局、并行测试）缓存会属于别的注册表，
    /// 直接复用会拿到「本注册表里不存在的 id」。所以命中条件是 <c>Owner == _registry</c>。
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentType Register<T>() where T : unmanaged
    {
        if (ReferenceEquals(ComponentMeta<T>.Owner, _registry))
        {
            ComponentType? cached = ComponentMeta<T>.Type;
            if (cached is not null)
            {
                return cached;
            }
        }
        ComponentType registered = _registry.Register<T>();
        ComponentMeta<T>.Owner = _registry;
        ComponentMeta<T>.Type = registered;
        return registered;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int IdOf<T>() where T : unmanaged => Register<T>().Id;

    /// <summary>把组件种类列表压成 id 数组（不排序；排序由 <see cref="TypeSet.Normalize"/> 在解析原型时做）。</summary>
    private int NormalizeIds(System.Collections.Generic.IReadOnlyList<ComponentType> components, Span<int> destination)
    {
        if (components.Count > destination.Length)
        {
            throw new GameEcsException(
                string.Concat("组件种类上限为 ", destination.Length.ToString(), "，实际传入 ", components.Count.ToString(), " 个。"));
        }
        int count = 0;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is null)
            {
                throw new GameEcsException("组件种类列表里不能有 null。");
            }
            destination[count++] = components[i].Id;
        }
        return count;
    }

    /// <summary>创建实体骨架（分配实体号 + 插入原型 + 写 [Identity]）并返回。</summary>
    private Entity CreateCore(ReadOnlySpan<int> requestedIds)
    {
        AssertStructuralAllowed("World.Create", Entity.None);
        Archetype archetype = ResolveOrCreateArchetype(requestedIds);
        Entity entity = AllocateEntity(archetype);
        Span<Entity> one = stackalloc Entity[1];
        one[0] = entity;
        archetype.CreateEntities(one, this);
        VersionGuard.Advance(ref _version);
        return entity;
    }

    /// <summary>
    /// 分配一个实体号：先试同签名回收队列，再开新槽位。
    /// <para>
    /// <b>三条不变量</b>（对应 ECS.md 第 5.1 节「旧句柄必须失效」）：
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>世代号唯一</b>：新号的 generation 必须<u>同时</u>大于「发号池当前值」与「该物理槽位上一位住户的
    ///     generation」。只比发号池还不够——槽位是复用的，一个老实体可能用着一个大号的 tag，
    ///     单调的发号池正好发到那个数，于是旧句柄 `<c>IsAlive</c>` 又变真了。
    ///   </item>
    ///   <item>
    ///     <b>避开本原型最近销毁号</b>：同组件集合的实体世代号不相邻，跨原型比较也更容易判别。
    ///   </item>
    ///   <item>
    ///     <b>世代号恒为正</b>：0 是「空闲槽位」与 <c>default(Entity)</c> 的哨兵。
    ///   </item>
    /// </list>
    /// </summary>
    private Entity AllocateEntity(Archetype archetype)
    {
        // 同签名的回收号：直接复用它的 (index, generation) 是安全的——那个世代号只在
        // 「它当活实体」的那段时间里被发出去过，它是死的同时就不会有第二个活实体用同一个号。
        // 但**不能**在「槽位号恰好等于当前活实体数」时使用它：那会让 Archetype 把新实体插到
        // 尾部 slot N* 上，而调用方按 index 查到的却是 N —— 位置与实体号错位，后面所有迁移都串。
        // 所以这个分支只在「回收号仍落在当前有效槽位区」时生效，否则回落到正常追加。
        if (_index.TryPopRecycled(archetype.Components.SignatureHash, out Entity recycled)
            && recycled.Index < _index.AliveCount)
        {
            // 复用时必须把回收号的世代号也「记进发号池」，否则发号池停滞不前，
            // 下次别的槽位正好发到同一个数时会让旧句柄复活。
            _generations.AdvancePast(recycled.Generation);
            return recycled;
        }

        int index = _index.AliveCount;
        int generation = _generations.Next();
        int floor = _index.GenerationAt(index);
        if (generation <= floor)
        {
            generation = _generations.AdvancePast(floor);
        }
        // 若这个世代号正好是本原型「上一次销毁」用过的号，就再前进一格，避免旧句柄复活。
        for (int guard = 0; guard <= archetype.ComponentCount; guard++)
        {
            bool clash = false;
            for (int i = 0; i < archetype.ComponentCount && !clash; i++)
            {
                clash = archetype.ComponentAt(i).LastDestructionGeneration == generation;
            }
            if (!clash)
            {
                break;
            }
            generation = _generations.AdvancePast(floor);
            guard = -1;
        }
        index = _index.Allocate(generation);
        return new Entity(index, generation);
    }

    /// <summary>解析或创建原型（按有序组件 id 集合）。</summary>
    private Archetype ResolveOrCreateArchetype(ReadOnlySpan<int> requestedIds)
    {
        int count = TypeSet.Normalize(requestedIds, _scratchIds);
        Span<int> sorted = _scratchIds.AsSpan(0, count);
        long signature = TypeSet.ComputeSignature(sorted);
        if (_archetypeBySignature.TryGetValue(signature, out Archetype? existing))
        {
            return existing;
        }
        ComponentType[] types = new ComponentType[count];
        ComponentTypeInfo[] infos = new ComponentTypeInfo[count];
        for (int i = 0; i < count; i++)
        {
            ComponentTypeInfo info = _registry.GetById(sorted[i]);
            infos[i] = info;
            types[i] = info.Data;
        }
        TypeSet set = new TypeSet(sorted.ToArray());
        Archetype created = new Archetype(_archetypes.Count, set, types, infos, _chunkCapacity);
        _archetypes.Add(created);
        _archetypeBySignature.Add(signature, created);
        _archetypeByHash[set.SignatureHash] = created;
        return created;
    }

    /// <summary>在 source 的签名上加一个组件，解析（或创建）目标原型。</summary>
    private Archetype ResolveOrCreateArchetypeWithExtra(Archetype source, int addedId)
    {
        Span<int> ids = stackalloc int[TypeSet.MaxComponentCount];
        int count = 0;
        bool inserted = false;
        for (int i = 0; i < source.ComponentCount; i++)
        {
            int id = source.ComponentIdAt(i);
            if (!inserted && addedId < id)
            {
                ids[count++] = addedId;
                inserted = true;
            }
            ids[count++] = id;
        }
        if (!inserted)
        {
            ids[count++] = addedId;
        }
        return ResolveOrCreateArchetype(ids.Slice(0, count));
    }

    /// <summary>在 source 的签名上去掉一个组件，解析（或创建）目标原型。</summary>
    private Archetype ResolveOrCreateArchetypeWithout(Archetype source, int removedId)
    {
        Span<int> ids = stackalloc int[TypeSet.MaxComponentCount];
        int count = 0;
        for (int i = 0; i < source.ComponentCount; i++)
        {
            int id = source.ComponentIdAt(i);
            if (id != removedId)
            {
                ids[count++] = id;
            }
        }
        return ResolveOrCreateArchetype(ids.Slice(0, count));
    }

    /// <summary>
    /// 把实体从 source 搬到 target：<u>先拷后删</u>。
    /// <para>顺序很关键：若先删，swap-remove 可能把「待拷的那一格」搬走。</para>
    /// <para>目标原型没有的列不拷贝；新增列由调用方随后写入（<see cref="Set{T}"/> 的添加分支）。</para>
    /// </summary>
    private void Migrate(Entity entity, Archetype source, int sourceChunk, int sourceSlot, Archetype target)
    {
        target.InsertCopy(entity, source, sourceChunk, sourceSlot, this);
        source.RemoveAt(sourceChunk, sourceSlot, this);
    }

    /// <summary>解析实体当前位置（调用方已保证实体活着）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResolveLocation(Entity entity, out int archetype, out int chunk, out int slot)
    {
        ref EntityRecord rec = ref _index.Record(entity.Index);
        archetype = rec.ArchetypeIndex;
        chunk = rec.ChunkIndex;
        slot = rec.SlotIndex;
    }

    /// <summary>尝试解析实体当前位置（不抛）。零值 / None 一律返回 false。</summary>
    private bool TryResolveLocation(Entity entity, out int archetype, out int chunk, out int slot)
    {
        if (entity.Index < 0 || entity.Generation <= 0 || !_index.IsAlive(entity.Index, entity.Generation))
        {
            archetype = EntityIndex.NoneArchetype;
            chunk = 0;
            slot = 0;
            return false;
        }
        ref EntityRecord rec = ref _index.Record(entity.Index);
        archetype = rec.ArchetypeIndex;
        chunk = rec.ChunkIndex;
        slot = rec.SlotIndex;
        return archetype >= 0;
    }

    /// <summary>把实体的槽位索引改到新位置（swap-remove / 迁移后由 Archetype 调用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetEntityLocation(Entity entity, int archetype, int chunk, int slot)
    {
        ref EntityRecord rec = ref _index.Record(entity.Index);
        rec.ArchetypeIndex = archetype;
        rec.ChunkIndex = chunk;
        rec.SlotIndex = slot;
    }

    /// <summary>
    /// 登记一条组件列。这是 ECS.md 第 9 节「C++ 批量求解器需要列清单」的预留落点：
    /// 目前池都挂在 chunk 上，无需额外登记，因此保持空实现而不是让 Archetype 反向依赖 native。
    /// </summary>
    internal void RegisterChunkPool(TypeSet signature)
    {
        _ = signature;
    }

    /// <summary>
    /// 建立查询（供 <see cref="World.Query{TA}()"/> 工厂方法与 <see cref="Query"/> 的 <c>With</c>/<c>Without</c> 重建用）。
    /// <para>查询自己按「已考察原型游标」增量并入新原型，所以这里**不再**传原型快照。</para>
    /// </summary>
    internal Query BuildQuery(ReadOnlySpan<int> requiredIds, ReadOnlySpan<int> excludedIds) =>
        new Query(this, requiredIds, excludedIds);

    // ─────────────────────────────────────────────────────────────
    // 内部：CommandBuffer 的落地接口（帧末提交时才走到这里）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 为「即将在帧末生成的实体」预留实体号。
    /// <para>
    /// <b>为什么入队时就要定号</b>：命令按写入顺序执行，后面的命令可能要引用前面那条 Create 出来的实体
    /// （开火 + 立刻给子弹挂标记）。所以号必须先定，实体本体在提交时才真正落进原型。
    /// </para>
    /// <para>预留期间实体「尚未存在」（<see cref="IsAlive"/> 为 false），提交时由 <see cref="ExecuteDeferredCreate"/> 完成生成。</para>
    /// </summary>
    internal Entity ReserveEntity(System.Collections.Generic.IReadOnlyList<ComponentType> components)
    {
        if (components is null)
        {
            throw new GameEcsException("CommandBuffer 预留实体时组件列表不能为 null。");
        }
        Span<int> ids = stackalloc int[TypeSet.MaxComponentCount];
        int count = NormalizeIds(components, ids);
        Archetype archetype = ResolveOrCreateArchetype(ids.Slice(0, count));
        return AllocateEntity(archetype);
    }

    /// <summary>按组件种类列表构造签名（命令里存签名，提交时用它找回原型）。</summary>
    internal TypeSet TypeSetOf(System.Collections.Generic.IReadOnlyList<ComponentType> components)
    {
        Span<int> ids = stackalloc int[TypeSet.MaxComponentCount];
        int count = NormalizeIds(components, ids);
        int sorted = TypeSet.Normalize(ids.Slice(0, count), _scratchIds);
        return new TypeSet(_scratchIds.AsSpan(0, sorted));
    }

    /// <summary>
    /// 把命令里携带的组件值放进「按组件分桶的值队列」，返回其逻辑下标。
    /// <para><b>为什么不在命令里直接存组件值</b>：命令结构要能携带任意组件类型，只能装箱；这里改成下标。</para>
    /// </summary>
    internal int EnqueueCommandValue<T>(int componentId, in T value) where T : unmanaged
    {
        while (_commandValues.Count <= componentId)
        {
            _commandValues.Add(null!);
        }
        if (_commandValues[componentId] is not CommandValueQueue<T> queue)
        {
            queue = new CommandValueQueue<T>(EcsConfig.InitialCommandCapacity);
            _commandValues[componentId] = queue;
        }
        return queue.Enqueue(value);
    }

    /// <summary>清空所有值队列（每次提交结束 / 世界释放时调用）。队列本体保留复用，稳态零分配。</summary>
    internal void ClearCommandValues()
    {
        for (int i = 0; i < _commandValues.Count; i++)
        {
            if (_commandValues[i] is ICommandValueQueue queue)
            {
                queue.Clear();
            }
        }
    }

    /// <summary>
    /// 提交一条 Create：把预留好的实体真正落进原型（组件初值 default）。
    /// <para>
    /// <b>「已生成」的判据不是 <see cref="IsAlive"/>，而是「有没有落进原型」</b>。
    /// 预留（<see cref="ReserveEntity"/>）会占一个槽位并写入世代号，但把
    /// <c>ArchetypeIndex</c> 置为 <c>NoneArchetype</c> —— 它语义上是「已定号、尚未存在」。
    /// 若这里用 <c>IsAlive</c> 判断，预留实体一进来就被当成「重复命令」跳过，
    /// 整批 <c>CreateDeferred</c> 会全部失效（曾经的真实 bug）。
    /// </para>
    /// </summary>
    internal bool ExecuteDeferredCreate(Entity entity, TypeSet components)
    {
        if (entity.IsNone)
        {
            return false;
        }
        if (!_index.IsAlive(entity.Index, entity.Generation))
        {
            return false;
        }
        ref EntityRecord record = ref _index.Record(entity.Index);
        if (record.ArchetypeIndex != EntityIndex.NoneArchetype)
        {
            // 已经落进原型了（重复命令）：跳过，交给 CommandBuffer 记一次冲突。
            return false;
        }
        Archetype? archetype = FindArchetype(components);
        if (archetype is null)
        {
            throw new GameEcsException("命令缓冲提交时找不到预留实体对应的原型（内部状态不一致）。");
        }
        Span<Entity> one = stackalloc Entity[1];
        one[0] = entity;
        archetype.CreateEntities(one, this);
        VersionGuard.Advance(ref _version);
        return true;
    }
    /// <summary>按签名找原型（命令提交路径用，O(1) 字典查询）。</summary>
    internal Archetype? FindArchetype(in TypeSet components)
    {
        if (_archetypeByHash.TryGetValue(components.SignatureHash, out Archetype? found))
        {
            return found;
        }
        if (_archetypeBySignature.TryGetValue(components.SignatureHash, out found))
        {
            return found;
        }
        return null;
    }

    /// <summary>提交一条 SetValue：有则更新、无则迁移添加（组件值从值队列里取）。</summary>
    internal bool ExecuteDeferredSet(Entity entity, int componentId, int valueIndex)
    {
        if (valueIndex < 0 || componentId < 0 || componentId >= _commandValues.Count)
        {
            return false;
        }
        if (_commandValues[componentId] is not ICommandValueQueue queue)
        {
            return false;
        }
        if (!TryResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot))
        {
            // 实体已不在（被同批命令销毁）：静默跳过，由 CommandBuffer 计数。
            // 但**必须消费掉这个值**，否则后续同组件的 Set 会整体错位一位。
            queue.SkipHead();
            return false;
        }
        Archetype source = _archetypes[archetypeIndex];
        if (!source.Components.Contains(componentId))
        {
            Archetype target = ResolveOrCreateArchetypeWithExtra(source, componentId);
            Migrate(entity, source, chunkIndex, slot, target);
            ResolveLocation(entity, out archetypeIndex, out chunkIndex, out slot);
        }
        VersionGuard.Advance(ref _version);
        return queue.WriteInto(this, entity, archetypeIndex, chunkIndex, slot, valueIndex);
    }

    /// <summary>按组件 id 移除（命令带的是 id，不是泛型）。</summary>
    internal bool RemoveById(Entity entity, int componentId)
    {
        if (!TryResolveLocation(entity, out int archetypeIndex, out int chunkIndex, out int slot))
        {
            return false;
        }
        Archetype source = _archetypes[archetypeIndex];
        if (!source.Components.Contains(componentId))
        {
            return false;
        }
        AssertStructuralAllowed("World.RemoveById", entity);
        Archetype target = ResolveOrCreateArchetypeWithout(source, componentId);
        Migrate(entity, source, chunkIndex, slot, target);
        VersionGuard.Advance(ref _version);
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────

    /// <summary>释放世界：清空原型与回收队列。此后再使用会抛异常。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _index.ClearRecycled();
        _archetypes.Clear();
        _archetypeBySignature.Clear();
        _archetypeByHash.Clear();
        _commands.Reset();
        _commandValues.Clear();
    }
}

/// <summary>
/// 命令值队列的弱绑定接口：<see cref="World"/> 按组件 id 存裸队列，避免为每种组件各开一个字段。
/// </summary>
internal interface ICommandValueQueue
{
    /// <summary>清空（保留底层数组复用）。</summary>
    void Clear();

    /// <summary>把队首值写进实体的组件列（只有队首可消费，保证「命令顺序 = 值顺序」）。</summary>
    bool WriteInto(World world, Entity entity, int archetypeIndex, int chunkIndex, int slot, int logicalIndex);

    /// <summary>
    /// 丢弃队首值。命令被跳过时必须调用，否则「第 k 条命令 ↔ 第 k 个值」的配对整体错位一位。
    /// </summary>
    void SkipHead();
}

/// <summary>
/// 某个组件的「命令值队列」。
/// <para>
/// <b>为什么需要它</b>：命令结构要能携带任意组件值，而命令数组是值类型数组（不能装箱）。
/// 于是「值」进这条按组件分桶的强类型队列，命令里只留一个 int 下标——
/// 既保住零装箱，又保证提交时按写入顺序逐个消费。
/// </para>
/// <para>
/// <b>为什么是 FIFO 而不是栈</b>：命令按写入顺序执行，同一组件的第 k 条命令必须拿到第 k 个值。
/// </para>
/// </summary>
internal sealed class CommandValueQueue<T> : ICommandValueQueue where T : unmanaged
{
    private T[] _values;
    private int _head;
    private int _count;

    public CommandValueQueue(int initialCapacity)
    {
        _values = new T[initialCapacity];
    }

    /// <summary>待消费的值数量。</summary>
    public int Count => _count;

    /// <summary>入队一个值，返回它在队列里的逻辑下标（相对队首，0 起）。</summary>
    public int Enqueue(in T value)
    {
        if (_head + _count == _values.Length)
        {
            if (_head > 0)
            {
                // 先尝试把有效区搬回数组头部（滑动窗口回收），避免无谓扩容。
                if (_count > 0)
                {
                    Array.Copy(_values, _head, _values, 0, _count);
                }
                _head = 0;
            }
            else
            {
                Array.Resize(ref _values, _values.Length * 2);
            }
        }
        _values[_head + _count] = value;
        _count++;
        return _count - 1;
    }

    /// <summary>
    /// 把队首值写进实体的组件列。
    /// <para>
    /// <b>为什么忽略 <paramref name="logicalIndex"/>、永远消费队首</b>：值的消费必须与「该组件
    /// 第几条 <c>Set</c> 命令」严格一一对应。命令按写入顺序执行，所以只要每条 SetValue 命令
    /// 都消费一次队首，第 k 条命令自然拿到第 k 个值。
    /// </para>
    /// <para>
    /// <b>为什么不能用入队时记录的绝对下标</b>（曾经的真实 bug）：一旦某条命令在中途被跳过
    /// （实体已销毁 / 组件列缺失 / 迁移失败），它的值就不会被消费，而后续命令记录的绝对下标
    /// 已经「越过」了这些未消费的槽位 —— 于是整批值错位一位，第 k 个实体拿到第 k+1 个值，
    /// 最后一个实体拿不到值。队首消费天然免疫这种错位。
    /// </para>
    /// <para>
    /// 跳过路径同样必须消费队首：值在入队时就按「同组件第几条命令」排好了序，
    /// 该命令被跳过，它那个值也就不该再被后面的命令捡走。
    /// </para>
    /// </summary>
    public bool WriteInto(World world, Entity entity, int archetypeIndex, int chunkIndex, int slot, int logicalIndex)
    {
        _ = logicalIndex; // 见上方说明：按队首消费，不依赖绝对下标。
        if (_count == 0)
        {
            return false;
        }
        T value = _values[_head];
        _head++;
        _count--;
        Archetype archetype = world.ArchetypeAt(archetypeIndex);
        int column = archetype.ColumnOf(ComponentMeta<T>.Type!.Id);
        if (column < 0)
        {
            return false;
        }
        ComponentPool<T> pool = (ComponentPool<T>)archetype.ChunkAt(chunkIndex)!.Pools[column];
        pool.SlotRef(slot) = value;
        pool.WriteIdentityAt(slot, entity);
        return true;
    }

    /// <summary>
    /// 丢弃队首值（命令被跳过时调用），保证「一条 SetValue 命令 ↔ 一个值」的配对不错位。
    /// </summary>
    public void SkipHead()
    {
        if (_count > 0)
        {
            _head++;
            _count--;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
