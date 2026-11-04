using System;

namespace Game.Core.Ecs;

/// <summary>
/// 一个实体槽位的检索项：它现在住在哪个原型的哪个 chunk 的第几格。
/// <para>
/// 为什么存 <see cref="ArchetypeIndex"/>（int）而不是 <see cref="Archetype"/> 引用：int 比对象引用省一半
/// 存储，而且能把「世界已释放但索引残留」变成可判定的空值（-1），不会悄悄留住整棵原型树。
/// </para>
/// <para>
/// 为什么 generation 与位置挤在同一个结构里：<c>IsAlive</c> 与 <c>Get/Set</c> 是两条最热的路径，
/// 一次缓存行内拿到全部信息，比查两个数组快。
/// </para>
/// </summary>
internal struct EntityRecord
{
    /// <summary>槽位上当前实体属于哪个 Archetype；<see cref="EntityIndex.NoneArchetype"/> 表示空闲槽位。</summary>
    public int ArchetypeIndex;

    /// <summary>在原型里的 chunk 序号。</summary>
    public int ChunkIndex;

    /// <summary>在 chunk 里的位置（0 到 ChunkCapacity-1）。</summary>
    public int SlotIndex;

    /// <summary>当前世代的 tag（0 表示空闲；活实体恒为正）。</summary>
    public int Generation;
}

/// <summary>
/// 实体槽位索引：记录每个实体当前住在「哪个原型的哪个 chunk 的第几格」，以及由 <see cref="World"/> 管理的
/// 「可复用实体号」队列。
/// <para>
/// <b>为什么要单独一层索引</b>：chunk 的 swap-remove 会移动实体，<c>World.Get&lt;T&gt;(entity)</c>
/// 必须能在 O(1) 内找到它。若没有槽位索引，就只能遍历原型（割草玩法里一个帧内几百次 Get，
/// 那是灾难）。索引只有 16 字节/槽位，而它换来的 O(1) 定位是整个 API 能「按实体号寻址」的基础。
/// </para>
/// <para>
/// <b>为什么回收队列按组件签名分桶</b>：同签名的实体号可以原样搬进已有的 chunk，连原型都不用换。
/// 这让「子弹池」这类高频生成/销毁的实体几乎不产生新 chunk 分配（见 <see cref="WorldStats.RecycledEntityHits"/>）。
/// </para>
/// <para>
/// <b>为什么队列是 FIFO 而不是 LIFO</b>：LIFO 会让「同一波刷怪」生成的实体号来回跳，日志与回放
/// 读起来像 bug。环形队列保持 O(1) 且不分配，只是代码多几行。
/// </para>
/// <para>
/// <b>为什么世代号恒为正</b>：数组 new 出来的 0 就天然表示「空槽位」，尾部收缩只看是否为 0，
/// 不需要额外的初始化标志。
/// </para>
/// </summary>
internal sealed class EntityIndex
{
    /// <summary>空闲槽位 / 「未找到」在 ArchetypeIndex 里的标记。</summary>
    public const int NoneArchetype = -1;

    /// <summary>Entity 的「无槽位」值。</summary>
    public const int NoneIndex = -1;

    /// <summary>GetHashCode 用的乘法常量（任意质数；Entity 与索引共用同一个数）。</summary>
    public const int HashPrime = 397;

    /// <summary>槽位记录数组初始容量。</summary>
    public const int InitialSlotCapacity = 64;

    /// <summary>清空后记录数组超过这个长度才收缩，避免小世界反复分配。</summary>
    private const int ShrinkThreshold = 1024;

    private EntityRecord[] _records;
    private int _recordCount;
    private readonly System.Collections.Generic.Dictionary<long, RecycleBucket> _buckets;
    private readonly System.Collections.Generic.List<long> _bucketKeys;

    /// <summary>类型化回收命中次数（WorldStats 用：命中率高说明实体号复用在工作）。</summary>
    public int TypedRecycledCount;

    /// <summary>创建实体索引。</summary>
    public EntityIndex(int initialCapacity)
    {
        if (initialCapacity < 1)
        {
            initialCapacity = 1;
        }
        _records = new EntityRecord[initialCapacity];
        _recordCount = 0;
        _buckets = new System.Collections.Generic.Dictionary<long, RecycleBucket>(InitialSlotCapacity);
        _bucketKeys = new System.Collections.Generic.List<long>(InitialSlotCapacity);
    }

    /// <summary>已分配槽位数（含尾部空闲）。</summary>
    public int SlotCapacity => _records.Length;

    /// <summary>活实体数（= 有效槽位区长度）。</summary>
    public int AliveCount => _recordCount;

    /// <summary>槽位记录数组占用的托管字节数（WorldStats 用）。</summary>
    public long Bytes => (long)_records.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<EntityRecord>();

    /// <summary>取槽位检索项的引用（调用方保证 index 在 0..AliveCount-1）。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public ref EntityRecord Record(int index) => ref _records[index];

    /// <summary>World.IsAlive 的内部实现：槽位在范围内且世代号一致。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(int index, int generation) =>
        (uint)index < (uint)_recordCount && _records[index].Generation == generation;

    /// <summary>取某个物理槽位当前（或最后一次使用过）的世代号；槽位越界返回 0。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int GenerationAt(int index) => (uint)index < (uint)_recordCount ? _records[index].Generation : 0;

    /// <summary>
    /// 分配一个新的槽位（追加在有效区尾部）并写入世代 tag。
    /// <para>tag 由调用方从 <see cref="GenerationPool"/> 取——因为还要和 Archetype 里
    /// 「上一批被销毁的世代号」比一比，这是 World 的裁决。</para>
    /// </summary>
    public int Allocate(int generationTag)
    {
        int index = _recordCount;
        if (index >= _records.Length)
        {
            int newCapacity = _records.Length * 2;
            if (newCapacity < InitialSlotCapacity)
            {
                newCapacity = InitialSlotCapacity;
            }
            Array.Resize(ref _records, newCapacity);
        }
        _recordCount = index + 1;
        ref EntityRecord rec = ref _records[index];
        rec.ArchetypeIndex = NoneArchetype;
        rec.ChunkIndex = 0;
        rec.SlotIndex = 0;
        rec.Generation = generationTag;
        return index;
    }

    /// <summary>把槽位还给空闲区（调用方已保证 Archetype 里的数据已腾空）。</summary>
    public void Release(int index)
    {
        ref EntityRecord rec = ref _records[index];
        rec.ArchetypeIndex = NoneArchetype;
        rec.Generation = 0;
        rec.ChunkIndex = 0;
        rec.SlotIndex = 0;

        // 只回收尾部：连续销毁（清屏）时整段退回去，避免记录数组无界增长。
        while (_recordCount > 0 && _records[_recordCount - 1].Generation == 0)
        {
            _recordCount--;
        }
        if (_recordCount == 0 && _records.Length > ShrinkThreshold)
        {
            _records = new EntityRecord[InitialSlotCapacity];
        }
    }

    /// <summary>登记一个可复用的实体号（按组件集合签名分桶）。</summary>
    public void PushRecycled(Entity entity, long componentSignature)
    {
        if (!_buckets.TryGetValue(componentSignature, out RecycleBucket? bucket))
        {
            bucket = new RecycleBucket();
            _buckets.Add(componentSignature, bucket);
            _bucketKeys.Add(componentSignature);
        }
        bucket.Push(entity);
    }

    /// <summary>按组件集合签名取一个可复用实体号；没有命中返回 false。</summary>
    public bool TryPopRecycled(long componentSignature, out Entity entity)
    {
        if (_buckets.TryGetValue(componentSignature, out RecycleBucket? bucket) && bucket.TryPop(out entity))
        {
            TypedRecycledCount++;
            return true;
        }
        entity = Entity.None;
        return false;
    }

    /// <summary>清空所有回收队列（World.Dispose）。</summary>
    public void ClearRecycled()
    {
        for (int i = 0; i < _bucketKeys.Count; i++)
        {
            _buckets[_bucketKeys[i]].Clear();
        }
        _buckets.Clear();
        _bucketKeys.Clear();
    }

    /// <summary>单个组件签名下的回收队列：环形队列 + 容量倍增，稳定 O(1)。</summary>
    private sealed class RecycleBucket
    {
        private Entity[] _items = new Entity[4];
        private int _head;
        private int _count;

        public void Push(Entity entity)
        {
            if (_count == _items.Length)
            {
                Entity[] grown = new Entity[_items.Length * 2];
                for (int i = 0; i < _count; i++)
                {
                    grown[i] = _items[(int)(((uint)(_head + i)) % (uint)_items.Length)];
                }
                _items = grown;
                _head = 0;
            }
            _items[(int)(((uint)(_head + _count)) % (uint)_items.Length)] = entity;
            _count++;
        }

        public bool TryPop(out Entity entity)
        {
            if (_count == 0)
            {
                entity = Entity.None;
                return false;
            }
            entity = _items[_head];
            _head = (int)(((uint)_head + 1u) % (uint)_items.Length);
            _count--;
            return true;
        }

        public void Clear()
        {
            _count = 0;
            _head = 0;
        }
    }
}

/// <summary>
/// 世代号池：按「组件集合（原型）」各自维护一个单调递增的 tag。
/// <para>
/// 为什么每个原型各用一个池：割草玩法里「子弹原型」的销毁/生成频率远高于「敌人原型」，
/// 分池让两边独立前进，不会因为一边刷屏把另一边的世代号推爆（int 只有 21 亿级）。
/// </para>
/// </summary>
internal sealed class GenerationPool
{
    private int _next;

    /// <summary>从 <paramref name="start"/> 开始发号（第一个发出的值 = start + 1）。</summary>
    public GenerationPool(int start)
    {
        _next = start;
    }

    /// <summary>取下一个世代号（严格单调递增且不等于 0）。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int Next()
    {
        _next++;
        if (_next <= 0)
        {
            _next = 1;
        }
        return _next;
    }

    /// <summary>
    /// 取一个<u>严格大于</u> <paramref name="floor"/> 的世代号（用于「槽位复用时避免旧 tag 复活」）。
    /// <para>若发号池已经跑在 floor 前面就只前进一格，否则直接跳到 floor+1——两种情况下总代价都是一次加法。</para>
    /// </summary>
    public int AdvancePast(int floor)
    {
        int next = Next();
        if (next > floor)
        {
            return next;
        }
        _next = floor + 1;
        if (_next <= 0)
        {
            _next = 1;
        }
        return _next;
    }
}

/// <summary>
/// 世界版本号：每次<u>结构性变更</u>（生成/销毁/增删组件）后前进。
/// <para>
/// 为什么用 volatile 读写而不是完整屏障：Job 线程读版本号只关心「是否变化」，不依赖其他数据的
/// 可见性顺序；volatile 的 acquire/release 已足够避免读到撕裂值，且成本远低于 lock。
/// </para>
/// <para>
/// 为什么退化到 0 时回到 1：0 被保留为「从未变更」的哨兵值，多线程读到一个回绕的 0
/// 会误判为「世界是干净的」，那是最坏的一种误判。
/// </para>
/// </summary>
internal static class VersionGuard
{
    /// <summary>读取版本号。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int Read(ref int version) => System.Threading.Volatile.Read(ref version);

    /// <summary>前进版本号（主线程在结构化变更后调用）。</summary>
    public static void Advance(ref int version)
    {
        int current = System.Threading.Volatile.Read(ref version) + 1;
        if (current <= 0)
        {
            current = 1;
        }
        System.Threading.Volatile.Write(ref version, current);
    }
}
