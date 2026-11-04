using System;
using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 一个 chunk：容量固定的「实体号数组 + 若干组件列」。这是整个 ECS 的最小存储单元。
/// <para>
/// <b>为什么 chunk 是类而不是结构</b>：chunk 会被原型数组持有、被 Query 反复访问、
/// 还在 Job 里跨线程只读共享。做成引用类型可以直接持有，不必担心结构拷贝把组件列拷走。
/// 每个 chunk 只在「新建时」分配一次（容量固定、永不扩容）。
/// </para>
/// <para>
/// <b>为什么实体号单独一列、而组件里还有 [Identity] 字段</b>：两个用途不同——
/// <see cref="Entities"/> 是内核的「反向索引」（遍历时顺手拿到实体号，不需要额外查组件），
/// 组件的 [Identity] 是业务侧的「自引用」（系统在 <c>ref T</c> 或 Span 里读写时能直接知道对象是谁，
/// 以及跨原型迁移后仍然指向正确的实体）。两者由内核在生成/迁移时一起写，不会不一致。
/// </para>
/// <para>
/// <b>为什么按「删除」用的是 swap-remove</b>：把最后一个活实体搬到被删的位置。
/// 好处是 O(1) 且 chunk 始终是连续的；代价是<u>遍历顺序不稳定</u>（ECS.md 第 5.3 节点明认可，
/// 确定性回放靠第 12 节待办 2 的确定性 chunk 排序解决）。
/// </para>
/// </summary>
public sealed class Chunk
{
    /// <summary>本 chunk 的实体号列（有效区是前 <see cref="Count"/> 格）。</summary>
    internal readonly Entity[] Entities;

    /// <summary>各组件列（下标与所属原型的组件表一一对应）。</summary>
    internal IComponentPool[] Pools;

    /// <summary>本 chunk 在当前原型里的序号（chunk 数组下标）。</summary>
    internal int Index;

    /// <summary>有效实体数（0 到 Capacity）。</summary>
    internal int Count;

    /// <summary>创建容量为 <paramref name="capacity"/> 的空 chunk。</summary>
    internal Chunk(int capacity, IComponentPool[] pools)
    {
        Entities = new Entity[capacity];
        Pools = pools;
        Count = 0;
    }

    /// <summary>chunk 容量。</summary>
    public int Capacity => Entities.Length;

    /// <summary>chunk 有效实体数。</summary>
    public int EntityCount => Count;

    /// <summary>实体号列的只读视图（有效区长度 = <see cref="EntityCount"/>）。</summary>
    public ReadOnlySpan<Entity> EntitySpan => Entities.AsSpan(0, Count);

    /// <summary>实体号列完整数组（迁移/交换时用）。</summary>
    internal Entity[] EntityArray => Entities;

    /// <summary>把指定组件列整体换成新池（原型迁移时用：列全部换成新原型的池）。</summary>
    internal void ReplacePools(IComponentPool[] pools)
    {
        Pools = pools;
    }
}

/// <summary>
/// 原型（Archetype）：一簇「组件组合相同」的实体，按 chunk 列式存储。
/// <para>
/// <b>为什么按原型分块</b>：同一原型的实体布局完全一致，遍历时只需要「一次取列 + 一个 for」，
/// 没有任何分支与虚调用；这也是它能被 C++ 侧按固定 stride 消费的前提（ECS.md 第 9 节）。
/// </para>
/// <para>
/// <b>为什么加入/移除组件要做整块搬迁</b>：搬迁频率远低于逐帧遍历（一次刷怪一个实体只搬一次），
/// 用搬迁换掉「组件独立池 + 间接索引」每次访问的额外指针跳转，是本项目热路径的主收益。
/// </para>
/// <para>
/// <b>为什么 chunk 数组只增不缩（除清空外）</b>：chunk 是「复用单位」，回收后留在槽位里下次直接用，
/// 避免刷怪/清屏的锯齿状分配；只在原型彻底空掉时整体释放。
/// </para>
/// </summary>
public sealed class Archetype
{
    private readonly ComponentType[] _componentTypes;
    private readonly ComponentTypeInfo[] _componentInfos;
    private readonly int[] _componentIds;
    private Chunk?[] _chunks;
    private int _entityCount;

    /// <summary>创建原型（由 <see cref="World"/> 在首次出现新组件组合时调用）。</summary>
    internal Archetype(int index, in TypeSet components, ComponentType[] componentTypes, ComponentTypeInfo[] componentInfos, int chunkCapacity)
    {
        Index = index;
        Components = components;
        ChunkCapacity = chunkCapacity;
        _componentTypes = componentTypes;
        _componentInfos = componentInfos;
        _componentIds = new int[componentTypes.Length];
        for (int i = 0; i < componentTypes.Length; i++)
        {
            _componentIds[i] = componentTypes[i].Id;
        }
        _chunks = new Chunk?[EcsConfig.InitialChunkSlotCapacity];
        _entityCount = 0;
    }

    /// <summary>原型 id（在同一 World 内唯一、稳定，可当数组下标）。</summary>
    public int Index { get; }

    /// <summary>有序组件集合 = 原型签名。</summary>
    public TypeSet Components { get; }

    /// <summary>每 chunk 容量（ECS.md 第 5.3 节：默认 128，经配置注入）。</summary>
    public int ChunkCapacity { get; }

    /// <summary>当前实体数。</summary>
    public int EntityCount => _entityCount;

    /// <summary>
    /// 当前 chunk 数（含已空但保留复用的 chunk）。
    /// <para>
    /// <b>为什么从「实体数」直接算而不是维护一个计数器</b>：计数器要在生成/删除/迁移三处同步，
    /// 漏一处就会让 Query 遍历到不存在的 chunk（空引用）或漏掉最后一个 chunk（丢实体）。
    /// 用 <c>(EntityCount + Capacity - 1) / Capacity</c> 是纯算术，永远与真实数据一致，
    /// 且只有除加，成本可以忽略。
    /// </para>
    /// </summary>
    public int ChunkCount => _entityCount <= 0 ? 0 : (_entityCount + ChunkCapacity - 1) / ChunkCapacity;

    /// <summary>组件种类数。</summary>
    public int ComponentCount => _componentTypes.Length;

    /// <summary>按列序取组件类型（下标顺序 = <see cref="Components"/> 的升序 id 顺序）。</summary>
    public ComponentType ComponentAt(int column) => _componentTypes[column];

    /// <summary>按列序取组件 id。</summary>
    internal int ComponentIdAt(int column) => _componentIds[column];

    /// <summary>查找某组件在本原型里的列号；不存在返回 -1（热路径：线性扫，组件数 ≤ 16）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ColumnOf(int componentId)
    {
        int[] ids = _componentIds;
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] == componentId)
            {
                return i;
            }
        }
        return EntityIndex.NoneArchetype;
    }

    /// <summary>本原型所有 chunk 占用的组件内存字节数。</summary>
    public long ComponentMemoryBytes
    {
        get
        {
            long total = 0;
            for (int c = 0; c < ChunkCount; c++)
            {
                Chunk? chunk = _chunks[c];
                if (chunk is null)
                {
                    continue;
                }
                total += chunk.Entities.Length * SizeOf.EntityBytes;
                for (int p = 0; p < chunk.Pools.Length; p++)
                {
                    total += chunk.Pools[p].ByteSize;
                }
            }
            return total;
        }
    }

    /// <summary>取第 index 个 chunk（未分配时返回 null）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunk? ChunkAt(int index) => _chunks[index];

    /// <summary>取第 index 个 chunk，不存在则分配一个新 chunk（同时登记进 <paramref name="world"/> 的索引）。</summary>
    internal Chunk GetOrCreateChunk(int index, World world)
    {
        Chunk? chunk = _chunks[index];
        if (chunk is not null)
        {
            return chunk;
        }
        IComponentPool[] pools = new IComponentPool[_componentTypes.Length];
        for (int i = 0; i < pools.Length; i++)
        {
            pools[i] = _componentInfos[i].CreatePool(ChunkCapacity);
            _componentTypes[i].AllocatedPoolCount++;
            world.RegisterChunkPool(Components);
        }
        chunk = new Chunk(ChunkCapacity, pools);
        chunk.Index = index;
        _chunks[index] = chunk;
        return chunk;
    }

    /// <summary>确保至少存在 <paramref name="chunkCount"/> 个 chunk 槽位（尾部扩容）。</summary>
    internal void EnsureChunkSlots(int chunkCount)
    {
        if (chunkCount <= _chunks.Length)
        {
            return;
        }
        int newLength = _chunks.Length;
        while (newLength < chunkCount)
        {
            newLength *= 2;
        }
        Array.Resize(ref _chunks, newLength);
    }

    /// <summary>登记「本原型里出现了一个实体」（由 World 在写入槽位后调用；chunk 数由 <see cref="ChunkCount"/> 现算）。</summary>
    internal void OnEntityAdded() => _entityCount++;

    /// <summary>登记「本原型里少了一个实体」。</summary>
    internal void OnEntityRemoved()
    {
        if (_entityCount > 0)
        {
            _entityCount--;
        }
    }

    /// <summary>
    /// 批量生成：连续 <paramref name="entities"/> 个实体一次性写进本原型。
    /// 这是生成器（刷怪、开火）的主路径：只分配「新用到的 chunk」，其余全部复用。
    /// <para>
    /// 返回时 <paramref name="entities"/> 里的每个 Entity 都已被写进所有列的 [Identity] 字段。
    /// </para>
    /// </summary>
    internal void CreateEntities(ReadOnlySpan<Entity> entities, World world)
    {
        int remaining = entities.Length;
        int cursor = 0;
        while (remaining > 0)
        {
            int chunkIndex = _entityCount / ChunkCapacity;
            int slot = _entityCount % ChunkCapacity;
            int take = ChunkCapacity - slot;
            if (take > remaining)
            {
                take = remaining;
            }
            EnsureChunkSlots(chunkIndex + 1);
            Chunk chunk = GetOrCreateChunk(chunkIndex, world);
            Entity[] ids = chunk.Entities;
            IComponentPool[] pools = chunk.Pools;
            for (int e = 0; e < take; e++)
            {
                Entity entity = entities[cursor + e];
                ids[slot + e] = entity;
                world.SetEntityLocation(entity, Index, chunkIndex, slot + e);
                for (int p = 0; p < pools.Length; p++)
                {
                    pools[p].WriteIdentityAt(slot + e, entity);
                }
            }
            chunk.Count = slot + take;
            _entityCount += take;
            cursor += take;
            remaining -= take;
        }
    }
    /// <summary>
    /// 往本原型的某个位置插入一个实体，并把它的所有组件列写进 <paramref name="source"/> 提供的值
    /// （跨原型迁移的主路径：调用方按列号逐个拷贝）。
    /// </summary>
    internal void InsertCopy(Entity entity, Archetype source, int sourceChunk, int sourceSlot, World world)
    {
        int chunkIndex = _entityCount / ChunkCapacity;
        int slot = _entityCount % ChunkCapacity;
        EnsureChunkSlots(chunkIndex + 1);
        Chunk chunk = GetOrCreateChunk(chunkIndex, world);
        chunk.Entities[slot] = entity;
        chunk.Count = slot + 1;
        _entityCount++;
        world.SetEntityLocation(entity, Index, chunkIndex, slot);

        // 逐列搬：本原型有的列，若源原型也有就拷过来；没有的列留 default（由调用方随后写入）。
        Chunk sourceChunkObj = source.ChunkAt(sourceChunk)!;
        for (int p = 0; p < _componentIds.Length; p++)
        {
            int sourceColumn = source.ColumnOf(_componentIds[p]);
            if (sourceColumn >= 0)
            {
                CopySlot(sourceChunkObj.Pools[sourceColumn], sourceSlot, chunk.Pools[p], slot);
            }
            chunk.Pools[p].WriteIdentityAt(slot, entity);
        }
    }

    /// <summary>
    /// 把源列的一格搬到目标列的一格（类型擦除版：同 T 的情况）。
    /// 每种组件都通过 <see cref="ComponentTypeInfo{T}"/> 强类型重载实现，这里是统一入口。
    /// </summary>
    internal static void CopySlot(IComponentPool source, int sourceSlot, IComponentPool target, int targetSlot)
    {
        if (source is ICopySource copySource)
        {
            copySource.CopyTo(sourceSlot, target, targetSlot);
            return;
        }
        throw new GameEcsException("内部错误：组件列不支持逐格拷贝。");
    }

    /// <summary>
    /// 从本原型删除 (chunkIndex, slot) 的实体，并用本原型最后一个实体填位（swap-remove）。
    /// <para>返回被搬过来的「填充实体」；若删的就是最后一个，返回 <see cref="Entity.None"/>。</para>
    /// <para>
    /// <b>为什么这里靠 <c>_entityCount</c> 算出「最后一个在哪」</b>：原型内的实体永远是
    /// <u>按位置稠密排列</u>的（chunk 0 填满再填 chunk 1），所以最后一个恒在
    /// <c>(_entityCount-1) / Capacity</c> 号 chunk 的 <c>(_entityCount-1) % Capacity</c> 格。
    /// 删完把 <c>_entityCount</c> 减一即等于「砍掉尾格」，不需要再单独维护每 chunk 的 Count 之外的状态。
    /// </para>
    /// </summary>
    internal Entity RemoveAt(int chunkIndex, int slot, World world)
    {
        Chunk chunk = _chunks[chunkIndex]!;
        int lastIndex = _entityCount - 1;
        int lastChunk = lastIndex / ChunkCapacity;
        int lastSlot = lastIndex % ChunkCapacity;
        Entity movedEntity = Entity.None;
        bool movingSomething = lastIndex != (chunkIndex * ChunkCapacity + slot);

        if (movingSomething)
        {
            Chunk lastChunkObj = _chunks[lastChunk]!;
            chunk.Entities[slot] = lastChunkObj.Entities[lastSlot];
            for (int p = 0; p < chunk.Pools.Length; p++)
            {
                if (chunkIndex == lastChunk)
                {
                    chunk.Pools[p].SwapRemove(slot, lastSlot);
                }
                else
                {
                    CopySlot(lastChunkObj.Pools[p], lastSlot, chunk.Pools[p], slot);
                }
            }
            movedEntity = chunk.Entities[slot];
            world.SetEntityLocation(movedEntity, Index, chunkIndex, slot);
        }

        // 每个 chunk 的 Count 都必须跟着「本原型实体数」重算：它是遍历时 Span 长度的唯一来源，
        // 漏更新会让 Query 遍历到已清空的格位（读到别的实体的残值），这是最难查的一类串数据。
        chunk.Count = Math.Min(ChunkCapacity, _entityCount - 1 - (chunkIndex * ChunkCapacity));

        // 尾部 chunk 空了：释放它的数组（保留槽位为 null，下次直接重建，避免反复 Array.Resize）。
        if (lastChunk != chunkIndex && _chunks[lastChunk] is Chunk tail)
        {
            tail.Count = Math.Min(ChunkCapacity, _entityCount - 1 - (lastChunk * ChunkCapacity));
            if (tail.Count <= 0)
            {
                _chunks[lastChunk] = null;
            }
        }

        _entityCount = lastIndex;
        return movedEntity;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Concat("Archetype#", Index.ToString(), "[", Components.Count.ToString(), " components, ", _entityCount.ToString(), " entities]");
    }
}

