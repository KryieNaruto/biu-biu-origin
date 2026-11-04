using System;
using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 查询：创建时<u>一次性</u>按组件签名匹配全部原型，之后遍历只走匹配好的原型列表（J6）。
/// <para>
/// <b>为什么「创建一次、复用多次」</b>：每帧重新扫原型表会把 O(原型数) 的匹配成本乘进帧时间，
/// 而原型集合在稳态下几乎不变。系统应在构造函数里建好 Query 字段、在 <c>OnExecute</c> 里反复用。
/// 原型集合变化（出现新组件组合）时，<u>已创建</u>的 Query 不会自动纳入新原型——这是刻意的：
/// 系统只在初始化时声明自己关心什么，运行时不该偷偷扩大职责。
/// </para>
/// <para>
/// <b>为什么遍历期间拒绝结构化变更</b>：遍历拿着指向 chunk 数组的 Span，迁移/swap-remove
/// 会改写它。内核用 <see cref="World.EnterIteration"/> 把这类误用变成确定性的中文异常，
/// 而不是让它变成随机数据错乱（J5）。
/// </para>
/// <para>
/// <b>零分配的三条路径</b>：
/// <list type="number">
///   <item><c>ForEach(in ISpanAction&lt;...&gt;)</c>：接口调用，struct/sealed 实现可去虚化，<b>推荐</b>；</item>
///   <item><c>ForEach(SpanAction&lt;...&gt;)</c>：委托版，适合临时 lambda 与测试；系统里长期持有请缓存到字段；</item>
///   <item><c>ForEachJob(...)</c>：按 chunk 分片成 <see cref="JobBatch"/>，交宿主层调度。</item>
/// </list>
/// </para>
/// </summary>
public sealed class Query
{
    private readonly World _world;
    private readonly TypeSet _required;
    private readonly TypeSet _excluded;
    private readonly int[] _requiredIds;
    private readonly int[] _excludedIds;

    // 匹配结果：按「原型创建顺序」追加，只增不减（原型一旦创建就不会消失，空原型也保留）。
    private Archetype[] _matched;
    private int _matchedCount;

    // 已经考察过的原型数量。新原型只在它之后出现，所以下次只需要看「新增的那一段」。
    private int _scanned;

    /// <summary>构造查询（由 <see cref="World.Query{TA}()"/> 等工厂方法调用）。</summary>
    internal Query(World world, ReadOnlySpan<int> requiredIds, ReadOnlySpan<int> excludedIds)
    {
        _world = world;
        _requiredIds = NormalizeCopy(requiredIds);
        _excludedIds = NormalizeCopy(excludedIds);
        _required = new TypeSet(_requiredIds);
        _excluded = new TypeSet(_excludedIds);
        _matched = Array.Empty<Archetype>();
        _matchedCount = 0;
        _scanned = 0;
        Refresh();
    }

    private static int[] NormalizeCopy(ReadOnlySpan<int> ids)
    {
        Span<int> buffer = stackalloc int[TypeSet.MaxComponentCount];
        int count = TypeSet.Normalize(ids, buffer);
        int[] result = new int[count];
        buffer.Slice(0, count).CopyTo(result);
        return result;
    }

    /// <summary>
    /// 把「构造之后新出现的原型」并进匹配表。
    /// <para>
    /// <b>为什么要有这一步（而不是构造时匹配一次就封死）</b>：迁移会创建新原型
    /// （<c>Set</c> 加组件就是一次迁移）。若 Query 只认构造那一刻的原型集合，
    /// 一个有实体的原型会在遍历里「凭空消失」——这是数据丢失级 bug，不是性能取舍能换的。
    /// </para>
    /// <para>
    /// <b>为什么是增量而不是每次全表重扫</b>：原型只增不减且顺序固定，
    /// 记一个「已考察到第几个」的游标即可，稳态下就是一次 <c>int</c> 比较
    /// （<c>_scanned == world.ArchetypeCount</c>），零分配、不进内层循环。
    /// </para>
    /// </summary>
    private void Refresh()
    {
        int archetypeCount = _world.ArchetypeCount;
        if (_scanned >= archetypeCount)
        {
            return;
        }
        if (_matched.Length < archetypeCount)
        {
            // 只在「首次」或「匹配表装不下」时分配；上界是原型总数，之后不再增长。
            int grown = _matched.Length == 0 ? EcsConfig.InitialArchetypeCapacity : _matched.Length;
            while (grown < archetypeCount)
            {
                grown *= 2;
            }
            Archetype[] resized = new Archetype[grown];
            Array.Copy(_matched, resized, _matchedCount);
            _matched = resized;
        }
        int count = _matchedCount;
        for (int i = _scanned; i < archetypeCount; i++)
        {
            Archetype archetype = _world.ArchetypeAt(i);
            if (!archetype.Components.ContainsAll(_required))
            {
                continue;
            }
            if (_excludedIds.Length > 0 && archetype.Components.Overlaps(_excluded))
            {
                continue;
            }
            _matched[count++] = archetype;
        }
        _matchedCount = count;
        _scanned = archetypeCount;
    }

    /// <summary>确认匹配表是最新的（所有公开读取 / 遍历入口的第一步）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureFresh()
    {
        if (_scanned != _world.ArchetypeCount)
        {
            Refresh();
        }
    }

    /// <summary>匹配的原型数量。</summary>
    public int MatchedArchetypeCount
    {
        get
        {
            EnsureFresh();
            return _matchedCount;
        }
    }

    /// <summary>当前匹配到的实体总数（O(原型数)，不进每实体循环；系统里做「有没有活」的快判）。</summary>
    public int Count
    {
        get
        {
            EnsureFresh();
            int total = 0;
            for (int a = 0; a < _matchedCount; a++)
            {
                total += _matched[a].EntityCount;
            }
            return total;
        }
    }

    /// <summary>是否一个实体都没有（提前返回用）。</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>追加「必须没有」的组件，返回新查询（原型匹配再跑一次，初始化期调用）。</summary>
    public Query Without<T>() where T : unmanaged
    {
        int id = _world.Register<T>().Id;
        return Rebuild(_requiredIds, WithAppended(_excludedIds, id));
    }

    /// <summary>追加「必须没有」的组件（按组件种类）。</summary>
    public Query Without(ComponentType componentType)
    {
        if (componentType is null)
        {
            throw new GameEcsException("Without 的组件类型不能为 null。");
        }
        return Rebuild(_requiredIds, WithAppended(_excludedIds, componentType.Id));
    }

    /// <summary>追加「必须有」的组件（= 缩小查询）。初始化期调用。</summary>
    public Query With<T>() where T : unmanaged
    {
        int id = _world.Register<T>().Id;
        return Rebuild(WithAppended(_requiredIds, id), _excludedIds);
    }

    /// <summary>追加「必须有」的组件（按组件种类）。</summary>
    public Query With(ComponentType componentType)
    {
        if (componentType is null)
        {
            throw new GameEcsException("With 的组件类型不能为 null。");
        }
        return Rebuild(WithAppended(_requiredIds, componentType.Id), _excludedIds);
    }

    private static int[] WithAppended(int[] source, int extraId)
    {
        int[] result = new int[source.Length + 1];
        Array.Copy(source, result, source.Length);
        result[source.Length] = extraId;
        return result;
    }

    private Query Rebuild(int[] required, int[] excluded) => _world.BuildQuery(required, excluded);

    // ─────────────────────────────────────────────────────────────
    // 1 元
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 1 元遍历（接口版，零委托分配）。每个 chunk 调一次，整块 Span 交给你的循环。
    /// <para>遍历区间内禁止结构化变更；要改请写 <c>World.Commands</c>，帧末统一提交。</para>
    /// </summary>
    public void ForEach<T0>(in ISpanAction<T0> action) where T0 : unmanaged
    {
        int id = _world.Register<T0>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int column = archetype.ColumnOf(id);
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> span = ((ComponentPool<T0>)chunk.Pools[column]).Span.Slice(0, count);
                    action.Execute(span, count);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    /// <summary>1 元遍历（委托版；临时 lambda/测试用，系统热路径请用接口版）。</summary>
    public void ForEach<T0>(SpanAction<T0> action) where T0 : unmanaged
    {
        GuardDelegate(action);
        int id = _world.Register<T0>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int column = archetype.ColumnOf(id);
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    action(((ComponentPool<T0>)chunk.Pools[column]).Span.Slice(0, count));
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2 元
    // ─────────────────────────────────────────────────────────────

    /// <summary>2 元遍历（接口版）。两个 Span 等长，天然支持「位置 += 速度」这类成对运算。</summary>
    public void ForEach<T0, T1>(in ISpanAction<T0, T1> action) where T0 : unmanaged where T1 : unmanaged
    {
        int id0 = _world.Register<T0>().Id;
        int id1 = _world.Register<T1>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int c0 = archetype.ColumnOf(id0);
                int c1 = archetype.ColumnOf(id1);
                if (c0 < 0 || c1 < 0)
                {
                    continue;
                }
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> a0 = ((ComponentPool<T0>)chunk.Pools[c0]).Span.Slice(0, count);
                    Span<T1> a1 = ((ComponentPool<T1>)chunk.Pools[c1]).Span.Slice(0, count);
                    action.Execute(a0, a1, count);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    /// <summary>2 元遍历（委托版）。</summary>
    public void ForEach<T0, T1>(SpanAction<T0, T1> action) where T0 : unmanaged where T1 : unmanaged
    {
        GuardDelegate(action);
        int id0 = _world.Register<T0>().Id;
        int id1 = _world.Register<T1>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int c0 = archetype.ColumnOf(id0);
                int c1 = archetype.ColumnOf(id1);
                if (c0 < 0 || c1 < 0)
                {
                    continue;
                }
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> a0 = ((ComponentPool<T0>)chunk.Pools[c0]).Span.Slice(0, count);
                    Span<T1> a1 = ((ComponentPool<T1>)chunk.Pools[c1]).Span.Slice(0, count);
                    action(a0, a1);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3 元
    // ─────────────────────────────────────────────────────────────

    /// <summary>3 元遍历（接口版）。</summary>
    public void ForEach<T0, T1, T2>(in ISpanAction<T0, T1, T2> action)
        where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged
    {
        int id0 = _world.Register<T0>().Id;
        int id1 = _world.Register<T1>().Id;
        int id2 = _world.Register<T2>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int c0 = archetype.ColumnOf(id0);
                int c1 = archetype.ColumnOf(id1);
                int c2 = archetype.ColumnOf(id2);
                if (c0 < 0 || c1 < 0 || c2 < 0)
                {
                    continue;
                }
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> a0 = ((ComponentPool<T0>)chunk.Pools[c0]).Span.Slice(0, count);
                    Span<T1> a1 = ((ComponentPool<T1>)chunk.Pools[c1]).Span.Slice(0, count);
                    Span<T2> a2 = ((ComponentPool<T2>)chunk.Pools[c2]).Span.Slice(0, count);
                    action.Execute(a0, a1, a2, count);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    /// <summary>3 元遍历（委托版）。</summary>
    public void ForEach<T0, T1, T2>(SpanAction<T0, T1, T2> action)
        where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged
    {
        GuardDelegate(action);
        int id0 = _world.Register<T0>().Id;
        int id1 = _world.Register<T1>().Id;
        int id2 = _world.Register<T2>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int c0 = archetype.ColumnOf(id0);
                int c1 = archetype.ColumnOf(id1);
                int c2 = archetype.ColumnOf(id2);
                if (c0 < 0 || c1 < 0 || c2 < 0)
                {
                    continue;
                }
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> a0 = ((ComponentPool<T0>)chunk.Pools[c0]).Span.Slice(0, count);
                    Span<T1> a1 = ((ComponentPool<T1>)chunk.Pools[c1]).Span.Slice(0, count);
                    Span<T2> a2 = ((ComponentPool<T2>)chunk.Pools[c2]).Span.Slice(0, count);
                    action(a0, a1, a2);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4 元
    // ─────────────────────────────────────────────────────────────

    /// <summary>4 元遍历（接口版）。</summary>
    public void ForEach<T0, T1, T2, T3>(in ISpanAction<T0, T1, T2, T3> action)
        where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        int id0 = _world.Register<T0>().Id;
        int id1 = _world.Register<T1>().Id;
        int id2 = _world.Register<T2>().Id;
        int id3 = _world.Register<T3>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int c0 = archetype.ColumnOf(id0);
                int c1 = archetype.ColumnOf(id1);
                int c2 = archetype.ColumnOf(id2);
                int c3 = archetype.ColumnOf(id3);
                if (c0 < 0 || c1 < 0 || c2 < 0 || c3 < 0)
                {
                    continue;
                }
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> a0 = ((ComponentPool<T0>)chunk.Pools[c0]).Span.Slice(0, count);
                    Span<T1> a1 = ((ComponentPool<T1>)chunk.Pools[c1]).Span.Slice(0, count);
                    Span<T2> a2 = ((ComponentPool<T2>)chunk.Pools[c2]).Span.Slice(0, count);
                    Span<T3> a3 = ((ComponentPool<T3>)chunk.Pools[c3]).Span.Slice(0, count);
                    action.Execute(a0, a1, a2, a3, count);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    /// <summary>4 元遍历（委托版）。</summary>
    public void ForEach<T0, T1, T2, T3>(SpanAction<T0, T1, T2, T3> action)
        where T0 : unmanaged where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        GuardDelegate(action);
        int id0 = _world.Register<T0>().Id;
        int id1 = _world.Register<T1>().Id;
        int id2 = _world.Register<T2>().Id;
        int id3 = _world.Register<T3>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int c0 = archetype.ColumnOf(id0);
                int c1 = archetype.ColumnOf(id1);
                int c2 = archetype.ColumnOf(id2);
                int c3 = archetype.ColumnOf(id3);
                if (c0 < 0 || c1 < 0 || c2 < 0 || c3 < 0)
                {
                    continue;
                }
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> a0 = ((ComponentPool<T0>)chunk.Pools[c0]).Span.Slice(0, count);
                    Span<T1> a1 = ((ComponentPool<T1>)chunk.Pools[c1]).Span.Slice(0, count);
                    Span<T2> a2 = ((ComponentPool<T2>)chunk.Pools[c2]).Span.Slice(0, count);
                    Span<T3> a3 = ((ComponentPool<T3>)chunk.Pools[c3]).Span.Slice(0, count);
                    action(a0, a1, a2, a3);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // chunk 视图 / Job 分片
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 2 元 chunk 遍历：一次拿到「组件列 + 实体号列 + 有效数」，适合需要实体号的批量处理
    /// （命中结算、命令入队），也是交给 C++ 批量求解器的入口形状（ECS.md 第 9 节）。
    /// </summary>
    public void ForEachChunk<T0, T1>(ChunkAction<T0, T1> action) where T0 : unmanaged where T1 : unmanaged
    {
        GuardDelegate(action);
        int id0 = _world.Register<T0>().Id;
        int id1 = _world.Register<T1>().Id;
        EnsureFresh();
        _world.EnterIteration();
        try
        {
            for (int a = 0; a < _matchedCount; a++)
            {
                Archetype archetype = _matched[a];
                int c0 = archetype.ColumnOf(id0);
                int c1 = archetype.ColumnOf(id1);
                if (c0 < 0 || c1 < 0)
                {
                    continue;
                }
                int chunkCapacity = archetype.ChunkCapacity;
                int chunkCount = archetype.ChunkCount;
                for (int c = 0; c < chunkCount; c++)
                {
                    Chunk chunk = archetype.ChunkAt(c)!;
                    int count = chunk.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    Span<T0> a0 = ((ComponentPool<T0>)chunk.Pools[c0]).Span.Slice(0, count);
                    Span<T1> a1 = ((ComponentPool<T1>)chunk.Pools[c1]).Span.Slice(0, count);
                    Chunk<T0, T1> view = new Chunk<T0, T1>(a0, a1, chunk.EntitySpan, count, archetype.Index);
                    action(view, count);
                }
            }
        }
        finally
        {
            _world.ExitIteration();
        }
    }

    /// <summary>
    /// 2 元 Job 分片：把匹配到的 chunk 切成 ≤ <paramref name="batches"/>.Length 个批次写入
    /// <paramref name="batches"/>，返回批次数，并由 <paramref name="worldVersion"/> 输出分片前的世界版本号。
    /// <para>
    /// <b>本轮限制（如实说明）</b>：只做「确定性分片 + 批次描述」，<u>不</u>在内部开线程池调度——
    /// 调度属于宿主层（<c>FixedFramePipeline</c>），而且必须先有确定性 chunk 排序（ECS.md 第 12 节待办 2），
    /// 否则并行写入顺序会漂。宿主拿到批次后自行 <c>Parallel.For</c> 或交给 C++ 求解器；
    /// 跑完后必须复核 <c>World.Version == worldVersion</c>，不等则本帧结果作废。
    /// </para>
    /// <para>
    /// <b>为什么这里不接受回调</b>：跨线程执行回调时 <c>ChunkAction</c> 会被多线程同时调用，
    /// 让内核替你调度就必须假定回调线程安全；而「批次描述」是纯数据，宿主想怎么调度都行。
    /// </para>
    /// </summary>
    public int SliceJobBatches(int minChunkBatch, Span<JobBatch> batches, out int worldVersion)
    {
        if (minChunkBatch < 1)
        {
            minChunkBatch = EcsConfig.DefaultMinChunkBatch;
        }
        EnsureFresh();
        int totalChunks = 0;
        for (int a = 0; a < _matchedCount; a++)
        {
            totalChunks += _matched[a].ChunkCount;
        }
        worldVersion = _world.Version;
        if (totalChunks == 0 || batches.Length == 0)
        {
            return 0;
        }
        int maxBatches = (totalChunks + minChunkBatch - 1) / minChunkBatch;
        if (maxBatches > batches.Length)
        {
            maxBatches = batches.Length;
        }
        int perBatch = (totalChunks + maxBatches - 1) / maxBatches;

        int batchIndex = 0;
        int cursor = 0;
        for (int a = 0; a < _matchedCount && batchIndex < maxBatches; a++)
        {
            Archetype archetype = _matched[a];
            int archetypeChunks = archetype.ChunkCount;
            int start = cursor;
            int end = cursor + archetypeChunks;
            while (cursor < end && batchIndex < maxBatches)
            {
                int take = perBatch;
                if (cursor + take > end)
                {
                    take = end - cursor;
                }
                batches[batchIndex] = new JobBatch(archetype, cursor - start, cursor - start + take);
                batchIndex++;
                cursor += take;
            }
        }
        return batchIndex;
    }

    private static void GuardDelegate(object? action)
    {
        if (action is null)
        {
            throw new GameEcsException("遍历回调不能为 null。");
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Concat("Query[required=", _requiredIds.Length.ToString(), ", excluded=", _excludedIds.Length.ToString(),
            ", archetypes=", _matchedCount.ToString(), "]");
    }
}

/// <summary>
/// Job 批次：一个原型内的连续 chunk 区间 [<see cref="StartChunk"/>, <see cref="EndChunk"/>)。
/// 值类型、只含一个原型引用，宿主层可以直接塞进线程池任务结构而不分配。
/// </summary>
public readonly struct JobBatch
{
    /// <summary>本批次所属原型。</summary>
    public Archetype Archetype { get; }

    /// <summary>起始 chunk（含）。</summary>
    public int StartChunk { get; }

    /// <summary>结束 chunk（不含）。</summary>
    public int EndChunk { get; }

    /// <summary>本批次的 chunk 数量。</summary>
    public int ChunkCount => EndChunk - StartChunk;

    /// <summary>构造一个批次。</summary>
    public JobBatch(Archetype archetype, int startChunk, int endChunk)
    {
        Archetype = archetype;
        StartChunk = startChunk;
        EndChunk = endChunk;
    }
}
