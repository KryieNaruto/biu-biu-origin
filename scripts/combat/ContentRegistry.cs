using System;
using System.Collections.Generic;
using Game.Core.Content;

namespace Game.Combat;

/// <summary>
/// 一个<b>寄存器槽位</b>（「可替换簇 + 槽位名」全局唯一的那一格）的装配期 + 运行期数据。
/// <para>
/// 为什么槽位是一个 class 而候选是 struct：槽位只在装配期创建（几个 dozen 次），
/// 而它内部平铺的候选数组才是热路径真正遍历的东西 —— 一起放进数组里会让"按 slotId 下标"变成两级间接。
/// </para>
/// </summary>
public sealed class RegisterSlot
{
    /// <summary>待选默认 def 值（<c>ContentRegistry.NoDef</c> 表示未绑）。</summary>
    public const int NoDef = -1;

    internal readonly List<ForgeCandidate> CandidateList = new List<ForgeCandidate>();
    internal ForgeCandidate[] CandidateCache = Array.Empty<ForgeCandidate>();
    internal bool CacheDirty;

    internal RegisterSlot(string slotKey, int clusterSlot, int defSpace, int defaultDefId)
    {
        SlotKey = slotKey;
        ClusterSlot = clusterSlot;
        DefSpace = defSpace;
        DefaultDefId = defaultDefId;
        InitialDefId = defaultDefId;
        BoundDefId = defaultDefId;
    }

    /// <summary>槽位名（如 <c>weapon.slot.primary</c>；全局唯一）。</summary>
    public string SlotKey { get; }

    /// <summary>所属「可替换簇」（<see cref="ObjectArchetype.ClusterSlot"/>）：熔炉在这一簇内选接收者。</summary>
    public int ClusterSlot { get; }

    /// <summary>这个槽位存放的是哪张内容表的槽位号（决定熔炉去哪张表取候选）。</summary>
    public int DefSpace { get; }

    /// <summary>
    /// 装配期声明的初始默认值（<b>不可变</b>：<see cref="ContentRegistry.Purge"/> 恢复到它）。
    /// <para>
    /// 为什么必须与 <see cref="DefaultDefId"/> 分开：<c>TryBind</c> 会改写 <see cref="DefaultDefId"/> 作为"当前装备"，
    /// 如果 <c>Purge</c> 也恢复到它，那么绑过一次之后就再也回不到装配期的初始状态了 ——
    /// 这是一个只在"换过一次装备再回收"时才暴露的静默 bug。
    /// </para>
    /// </summary>
    public int InitialDefId { get; }

    /// <summary>默认实现（装配期 <c>TryBind</c> 定的那一份；会被后续绑定覆盖）。</summary>
    public int DefaultDefId { get; internal set; }

    /// <summary>当时绑定的是哪个内容表下标（用于 <see cref="ContentRegistry"/> 反查 <see cref="ObjectArchetype"/>）。</summary>
    public int DefaultObjectSlot { get; internal set; } = ContentIndex.NoneSlot;

    /// <summary>当前生效的 def 值（默认绑定；熔炉派生后换成选定者，<c>Purge</c> 时恢复）。</summary>
    public int BoundDefId { get; internal set; }

    /// <summary>熔炉派生出来的 def 值（-1 = 当前处于默认绑定）。</summary>
    public int ForgedDefId { get; internal set; } = NoDef;

    /// <summary>当前候选个数。</summary>
    public int CandidateCount => CandidateList.Count;

    /// <summary>候选的只读视图（扁平数组；热路径用 <c>ReadOnlySpan</c> 直接扫，不产生迭代器）。</summary>
    public ReadOnlySpan<ForgeCandidate> Candidates
    {
        get
        {
            if (CacheDirty)
            {
                if (CandidateCache.Length < CandidateList.Count)
                {
                    CandidateCache = new ForgeCandidate[CandidateList.Count];
                }
                CandidateList.CopyTo(CandidateCache);
                CacheDirty = false;
            }
            return new ReadOnlySpan<ForgeCandidate>(CandidateCache, 0, CandidateList.Count);
        }
    }

    internal void MarkDirty() => CacheDirty = true;

    /// <inheritdoc />
    public override string ToString() => string.Concat("RegisterSlot(", SlotKey, ", cluster=", ClusterSlot.ToString(), ")");
}

/// <summary>
/// <b>寄存器与注册表</b>：把「某个槽位上现在生效的是哪份 Def」做成一个可被熔炉改写、也能被回收的状态。
/// <para>
/// <b>它解决的两个问题</b>：
/// </para>
/// <list type="number">
/// <item><b>高性能</b>：<see cref="Resolve"/> 是纯数组下标 + 一次 int 比较，没有任何字典、字符串、委托；
///   一帧几百次开火也不产生一次分配（"工厂必须高性能"的落地方式）。</item>
/// <item><b>对象池式复用</b>：Ecs 实体本身就是 chunk 里的槽位（见 <c>World</c>），真正的 GC 压力来自 Node，
///   那部分在宿主层 <c>ViewRegistry</c>；本层保证的是「每帧 0 次托管分配」——
///   所有临时结构都是栈上 struct，候选数组在装配期一次成型、之后只读。</item>
/// </list>
/// <para>
/// <b>为什么"槽位"要声明两次（Declare 时按簇 + 名字）</b>：名字保证全局唯一（装配期就能拦住重复声明），
/// 簇保证熔炉知道去哪个内容表里找候选。二者缺一个都会让「把 A 槽位的东西塞进 B 槽位」变成运行时错误。
/// </para>
/// <para>
/// <b>线程</b>：装配期（<see cref="DeclareSlot"/> / <see cref="TryBind"/> / <see cref="RefreshCluster"/>）单线程；
/// 运行期 <see cref="Resolve"/> 只读，可并行。这与 ECS「结构化变更只在主线程」同一条纪律。
/// </para>
/// </summary>
public sealed class ContentRegistry
{
    /// <summary>"未注册"哨兵（与 <c>ContentIndex.NoneSlot</c> 同值）。</summary>
    public const int NoDef = RegisterSlot.NoDef;

    private readonly ContentIndex _index;
    private readonly List<RegisterSlot> _slots = new List<RegisterSlot>(8);  // arch-ok: 槽位表初始容量，结构性常量
    private readonly Dictionary<string, int> _slotByKey = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<int, List<int>> _slotsByCluster = new Dictionary<int, List<int>>();

    // Resolve 用的「当前生效值」快照：与 _slots 平行，热路径只读这个 int[]，不追对象字段。
    private int[] _effective = Array.Empty<int>();

    /// <summary>构造：必须绑定一份内容表（槽位的 def 值都是它的下标）。</summary>
    public ContentRegistry(ContentIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>绑定的内容表。</summary>
    public ContentIndex Index => _index;

    /// <summary>已声明的槽位数。</summary>
    public int SlotCount => _slots.Count;

    // ─────────────────────────────────────────────────────────────
    // 装配期
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 声明一个寄存器槽位，返回稳定的 <paramref name="slotId"/>（从 0 起递增，进程内不变）。
    /// <para>同名重复声明直接抛异常：槽位名是装配期的"设备地址"，重复意味着两份代码抢同一个槽，
    /// 后绑的那份会静默覆盖前一份 —— 必须当场失败。</para>
    /// </summary>
    /// <param name="slotKey">槽位名（全局唯一，如 <c>weapon.slot.primary</c>）。</param>
    /// <param name="clusterSlot">所属可替换簇（<see cref="ObjectArchetype.ClusterSlot"/>；NoCluster 表示不参与熔炉）。</param>
    /// <param name="defSpace">内容表种类（见 <see cref="ContentSpace"/>；默认武器表）。</param>
    /// <param name="defaultDefId">初始默认值（-1 = 未绑，解析时返回 <see cref="NoDef"/>）。</param>
    public int DeclareSlot(string slotKey, int clusterSlot, int defSpace = ContentSpace.Weapon, int defaultDefId = NoDef)
    {
        if (string.IsNullOrEmpty(slotKey))
        {
            throw new ArgumentException("寄存器槽位名不能为空：它是装配期的稳定地址。", nameof(slotKey));
        }
        if (_slotByKey.TryGetValue(slotKey, out int existing))
        {
            throw new GameCombatException(string.Concat(
                "寄存器槽位重复声明：'", slotKey, "'（已存在于 slotId ", existing.ToString(),
                "）。同一槽位名只能被一份装配代码拥有，否则后绑的会静默覆盖先绑的。"));
        }

        int slotId = _slots.Count;
        RegisterSlot slot = new RegisterSlot(slotKey, clusterSlot, defSpace, defaultDefId);
        slot.DefaultObjectSlot = ResolveObjectSlot(clusterSlot, defSpace, defaultDefId);
        _slots.Add(slot);
        _slotByKey.Add(slotKey, slotId);
        EnsureEffectiveCapacity(slotId + 1);
        _effective[slotId] = defaultDefId;

        if (clusterSlot != ObjectArchetype.NoCluster && !_slotsByCluster.TryGetValue(clusterSlot, out List<int>? members))
        {
            members = new List<int>();
            _slotsByCluster.Add(clusterSlot, members);
        }
        if (clusterSlot != ObjectArchetype.NoCluster)
        {
            _slotsByCluster[clusterSlot].Add(slotId);
        }

        return slotId;
    }

    /// <summary>按槽位名取 slotId；不存在返回 <see cref="NoDef"/>。</summary>
    public int SlotIdOf(string slotKey) =>
        !string.IsNullOrEmpty(slotKey) && _slotByKey.TryGetValue(slotKey, out int slotId) ? slotId : NoDef;

    /// <summary>按 slotId 取槽位对象；越界返回 <c>null</c>。</summary>
    public RegisterSlot? GetSlot(int slotId) => slotId >= 0 && slotId < _slots.Count ? _slots[slotId] : null;

    /// <summary>
    /// 绑定默认实现（装配期，通常来自 Excel 的「默认装备」列）。
    /// <para>返回 false 表示 <paramref name="defId"/> 不在该槽位的簇里 ——
    /// 「把步枪塞进子弹槽」是数据错误，但不该让加载中断，所以返回 false 由调用方记数报错。</para>
    /// </summary>
    public bool TryBind(int slotId, int defId)
    {
        RegisterSlot? slot = GetSlot(slotId);
        if (slot is null)
        {
            return false;
        }
        if (defId >= 0 && !IsInCluster(slot.ClusterSlot, slot.DefSpace, defId))
        {
            return false;
        }
        slot.DefaultDefId = defId;
        slot.DefaultObjectSlot = ResolveObjectSlot(slot.ClusterSlot, slot.DefSpace, defId);
        if (slot.ForgedDefId == NoDef)
        {
            slot.BoundDefId = defId;
            _effective[slotId] = defId;
        }
        return true;
    }

    /// <summary>
    /// 把一个内容表里的全部成员登记成某簇的候选（装配期一次）。
    /// <para>候选是 <see cref="ForgeCandidate"/> 的扁平数组：<c>DefId / TagMask / Weight / ViableCount</c>
    /// 全部在这里算好，热路径不再碰字典（ECS.md §5 的"只存 int"精神在候选层的延续）。</para>
    /// </summary>
    /// <param name="clusterSlot">簇号。</param>
    /// <param name="defSpace">内容表种类。</param>
    public int RefreshCluster(int clusterSlot, int defSpace = ContentSpace.Weapon)
    {
        if (clusterSlot == ObjectArchetype.NoCluster)
        {
            return 0;
        }
        if (!_slotsByCluster.TryGetValue(clusterSlot, out List<int>? slots))
        {
            return 0;
        }

        int registered = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            RegisterSlot slot = _slots[slots[i]];
            slot.CandidateList.Clear();
            CollectCandidates(clusterSlot, defSpace, slot.CandidateList);
            slot.MarkDirty();
            registered = slot.CandidateList.Count;
        }
        return registered;
    }

    /// <summary>
    /// 用数据层<b>已经摊平</b>的簇候选装配槽位（装配期一次），不再扫内容表。
    /// <para>
    /// <b>为什么单独提供这条入口</b>：<c>ContentBinder</c> 在装配期就已经把每个簇的成员扫好、
    /// 摊成 <c>objectSlots / weights / viableCounts</c> 三条等长数组（见 <c>ClusterCandidates</c>）。
    /// 若这里还走 <see cref="RefreshCluster"/> 去重新扫表，等于把 binder 的成果丢掉再算一遍，
    /// 而且扫表过程要碰 <see cref="ContentIndex"/> 的字符串 / 字典。直接吃扁平数组则是一次线性拷贝。
    /// </para>
    /// <para>
    /// 标签掩码由 <paramref name="tagsOfObjectSlot"/> 提供：数据层的位矩阵按<b>物件槽位号</b>取行，
    /// 而锚点 def（武器 / 子弹槽位号）要先换算成物件槽位号才取得到行 —— 这个换算由 <paramref name="anchorObjectSlotOfDef"/> 给出。
    /// 两者都是装配期回调，热路径不参与。
    /// </para>
    /// </summary>
    /// <param name="clusterSlot">簇号。</param>
    /// <param name="defSpace">内容表种类（决定 def 槽位号指向哪张表）。</param>
    /// <param name="objectSlots">候选物件槽位号（扁平）。</param>
    /// <param name="weights">候选权重（与 <paramref name="objectSlots"/> 等长；全 1 时熔炉退化为等概率随机挑）。</param>
    /// <param name="viableCounts">候选静态可用数（与 <paramref name="objectSlots"/> 等长）。</param>
    /// <param name="anchorObjectSlotOfDef">把「物件槽位号」换算成「这个 def 的槽位号」（按 <paramref name="defSpace"/>）。</param>
    /// <param name="tagsOfObjectSlot">取某个物件槽位号的标签掩码（数据层位矩阵的行）。</param>
    /// <returns>登记到槽位里的候选数（无槽位声明时为 0）。</returns>
    public int AssignClusterCandidates(
        int clusterSlot,
        int defSpace,
        ReadOnlySpan<int> objectSlots,
        ReadOnlySpan<float> weights,
        ReadOnlySpan<int> viableCounts,
        DefSlotOfObjectSlot anchorObjectSlotOfDef,
        TagsOfObjectSlot tagsOfObjectSlot)
    {
        if (clusterSlot == ObjectArchetype.NoCluster)
        {
            return 0;
        }
        if (!_slotsByCluster.TryGetValue(clusterSlot, out List<int>? slots))
        {
            return 0;
        }
        if (anchorObjectSlotOfDef is null)
        {
            throw new ArgumentNullException(nameof(anchorObjectSlotOfDef));
        }
        if (tagsOfObjectSlot is null)
        {
            throw new ArgumentNullException(nameof(tagsOfObjectSlot));
        }

        int count = objectSlots.Length;
        if (weights.Length < count)
        {
            count = weights.Length;
        }
        if (viableCounts.Length < count)
        {
            count = viableCounts.Length;
        }

        int registered = 0;
        for (int s = 0; s < slots.Count; s++)
        {
            RegisterSlot slot = _slots[slots[s]];
            if (slot.DefSpace != defSpace)
            {
                // 同簇不同表：这个槽位存的是另一张表的 def，簇成员要按它自己的表换算。
                continue;
            }
            slot.CandidateList.Clear();
            for (int i = 0; i < count; i++)
            {
                int objectSlot = objectSlots[i];
                int defId = anchorObjectSlotOfDef(objectSlot);
                if (defId < 0)
                {
                    continue;
                }
                float weight = weights[i] > 0f ? weights[i] : 0f;
                slot.CandidateList.Add(new ForgeCandidate(defId, tagsOfObjectSlot(objectSlot), weight, viableCounts[i]));
            }
            slot.MarkDirty();
            registered = slot.CandidateList.Count;
        }
        return registered;
    }

    /// <summary>把「物件槽位号」换算成「def 槽位号」（<see cref="AssignClusterCandidates"/> 的装配期回调）。</summary>
    /// <param name="objectSlot">物件槽位号。</param>
    /// <returns>该物件在当前内容表里的 def 槽位号；没有对应 def 返回 -1。</returns>
    public delegate int DefSlotOfObjectSlot(int objectSlot);

    /// <summary>取某个物件槽位号的标签掩码（<see cref="AssignClusterCandidates"/> 的装配期回调）。</summary>
    /// <param name="objectSlot">物件槽位号。</param>
    public delegate int TagsOfObjectSlot(int objectSlot);

    private void CollectCandidates(int clusterSlot, int defSpace, List<ForgeCandidate> into)
    {        switch (defSpace)
        {
            case ContentSpace.Object:
                // 物件表：簇就长在物件自己身上，直接扫一遍。
                for (int i = 0; i < _index.ObjectCount; i++)
                {
                    ObjectArchetype? archetype = _index.GetObject(i);
                    if (archetype is null || archetype.ClusterSlot != clusterSlot)
                    {
                        continue;
                    }
                    into.Add(MakeCandidate(i, archetype));
                }
                break;

            case ContentSpace.Projectile:
                for (int i = 0; i < _index.ProjectileCount; i++)
                {
                    ProjectileContract? contract = _index.GetProjectile(i);
                    if (contract is null)
                    {
                        continue;
                    }
                    int objectSlot = _index.ObjectSlotOfHash(contract.ObjectDefId);
                    ObjectArchetype? owner = _index.GetObject(objectSlot);
                    if (owner is null || owner.ClusterSlot != clusterSlot)
                    {
                        continue;
                    }
                    // 子弹契约的标签 vs 需求：标签挂在【物件】上（一颗子弹的"能不能点燃"是它这个物件的属性），
                    // 所以候选的 TagMask 取 owner.Tags，而不是契约自己的某个字段。
                    into.Add(MakeCandidate(i, owner));
                }
                break;

            case ContentSpace.Weapon:
            default:
                for (int i = 0; i < _index.WeaponCount; i++)
                {
                    WeaponContract? contract = _index.GetWeapon(i);
                    if (contract is null)
                    {
                        continue;
                    }
                    int objectSlot = _index.ObjectSlotOfHash(contract.ObjectDefId);
                    ObjectArchetype? owner = _index.GetObject(objectSlot);
                    if (owner is null || owner.ClusterSlot != clusterSlot)
                    {
                        continue;
                    }
                    into.Add(MakeCandidate(i, owner));
                }
                break;
        }
    }

    /// <summary>
    /// 由一个「物件」造候选：标签与权重都从物件那一行取（物件是内容的最小单元，契约只是它的配件）。
    /// <para>权重：权威物件记 1 票、自适应模板记 0.5 票 —— 「表里填全的」天然比「靠推导兜底的」更该被选中。
    /// 这两条规则是唯一的硬编码策略，将来数据表加了 weight 列只需改这里。</para>
    /// </summary>
    private static ForgeCandidate MakeCandidate(int defId, ObjectArchetype owner) =>
        new ForgeCandidate(defId, owner.Tags, owner.IsAuthoritative ? 1f : 0.5f, ForgeRules.ViableDefCount(owner.Radius));

    /// <summary>按物件定义哈希取它的标签位集（契约只带 <c>ObjectDefId</c> 哈希，标签仍挂在物件上）。</summary>
    private int TagsOfObject(int objectDefHash)
    {
        int slot = _index.ObjectSlotOfHash(objectDefHash);
        return slot >= 0 ? _index.GetObject(slot)?.Tags ?? 0 : 0;
    }

    /// <summary>判断某个 def 是否属于指定簇（装配期用；热路径不调）。</summary>
    public bool IsInCluster(int clusterSlot, int defSpace, int defId)
    {
        switch (defSpace)
        {
            case ContentSpace.Object:
                return _index.ClusterOf(defId) == clusterSlot;

            case ContentSpace.Projectile:
            {
                ProjectileContract? contract = _index.GetProjectile(defId);
                return contract is not null && _index.ClusterOf(_index.ObjectSlotOfHash(contract.ObjectDefId)) == clusterSlot;
            }

            case ContentSpace.Weapon:
            default:
            {
                WeaponContract? contract = _index.GetWeapon(defId);
                return contract is not null && _index.ClusterOf(_index.ObjectSlotOfHash(contract.ObjectDefId)) == clusterSlot;
            }
        }
    }

    /// <summary>把「def 值」换算成「物件表的槽位号」（熔炉的候选与集群信息都挂在物件上）。</summary>
    public int ResolveObjectSlot(int clusterSlot, int defSpace, int defId)
    {
        if (defId < 0)
        {
            return ContentIndex.NoneSlot;
        }
        switch (defSpace)
        {
            case ContentSpace.Object:
                return _index.ClusterOf(defId) == clusterSlot || clusterSlot == ObjectArchetype.NoCluster
                    ? defId
                    : ContentIndex.NoneSlot;

            case ContentSpace.Projectile:
            {
                ProjectileContract? contract = _index.GetProjectile(defId);
                return contract is null ? ContentIndex.NoneSlot : _index.ObjectSlotOfHash(contract.ObjectDefId);
            }

            case ContentSpace.Weapon:
            default:
            {
                WeaponContract? contract = _index.GetWeapon(defId);
                return contract is null ? ContentIndex.NoneSlot : _index.ObjectSlotOfHash(contract.ObjectDefId);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 运行期（热路径：无分配、无字典）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析：返回该槽位当前生效的 def 值。
    /// <para><b>这就是热路径</b>：一次边界检查 + 一次数组读。没有字典、没有字符串、没有分配。</para>
    /// </summary>
    public int Resolve(int slotId, in ValueContext ctx)
    {
        _ = ctx; // 当前槽位是"静态绑定 + 熔炉派生"的，与求值上下文无关；保留参数是为了将来支持"按上下文切换槽位"。
        if ((uint)slotId >= (uint)_effective.Length)
        {
            return NoDef;
        }
        return _effective[slotId];
    }

    /// <summary>
    /// 熔炉解析：在槽位所属簇的候选里按 <paramref name="recipeTag"/> 选出接收者，并<b>改写该槽位</b>的生效值。
    /// <para>
    /// <b>为什么改写槽位而不是返回一个值让调用方自己记</b>：熔炉的语义是「这个槽位现在装的东西被换掉了」
    /// （手枪熔成步枪）。槽位是唯一的真相，调用方拿到的实体组件里的 <c>ContractSlot</c> 下一帧自然读到新值。
    /// </para>
    /// </summary>
    /// <param name="slotId">槽位号。</param>
    /// <param name="ctx">求值上下文（随机源从这里取）。</param>
    /// <param name="recipeTag">配方 tag（8 字符编码成 ulong，由数据层决定编码）。</param>
    /// <param name="result">熔炉判定结果（含 Perf）。</param>
    public int ResolveWithRegister(int slotId, in ValueContext ctx, ulong recipeTag, out ForgeResult result)
    {
        RegisterSlot? slot = GetSlot(slotId);
        if (slot is null)
        {
            result = new ForgeResult(ForgeOutcome.NoMatch, ForgeRules.NoneDefId, 0f, 0);
            return NoDef;
        }

        int selected = ForgeRules.Evaluate(recipeTag, slot.Candidates, in ctx, out result);
        if (selected < 0)
        {
            // 熔炉没选中：保留当前生效值（不把槽位打成空）。
            return Resolve(slotId, in ctx);
        }
        slot.ForgedDefId = selected;
        slot.BoundDefId = selected;
        _effective[slotId] = selected;
        return selected;
    }

    /// <summary>
    /// 寄存器回收：<b>生成结束</b>（一次射击 / 一波刷怪结束）时把所有槽位恢复成默认绑定。
    /// <para>
    /// <b>为什么必须有这一步</b>：熔炉派生出来的物件是"这一发临时产物"（非权威物件），
    /// 若不定时回收，玩家的手枪会永久变成上一发熔出来的东西，且 <c>_effective</c> 会因为不断派生而失去可复现性。
    /// 参数 <paramref name="generation"/> 留给将来做「分代回收」（只回收某一代派生），当前实现全量回收。
    /// </para>
    /// </summary>
    public void Purge(int generation)
    {
        _ = generation;
        for (int i = 0; i < _slots.Count; i++)
        {
            RegisterSlot slot = _slots[i];
            slot.ForgedDefId = NoDef;
            // 恢复到装配期初始值（不是 TryBind 之后的值）：Purge 的语义是"这一段过程结束，回到出厂状态"。
            slot.DefaultDefId = slot.InitialDefId;
            slot.DefaultObjectSlot = ResolveObjectSlot(slot.ClusterSlot, slot.DefSpace, slot.InitialDefId);
            slot.BoundDefId = slot.InitialDefId;
            _effective[i] = slot.InitialDefId;
        }
    }

    private void EnsureEffectiveCapacity(int needed)
    {
        if (_effective.Length >= needed)
        {
            return;
        }
        int grown = _effective.Length == 0 ? 8 : _effective.Length;  // arch-ok: 快照数组增长起始长度，结构性常量
        while (grown < needed)
        {
            grown *= 2;
        }
        int[] resized = new int[grown];
        Array.Copy(_effective, resized, _effective.Length);
        _effective = resized;
    }
}

/// <summary>寄存器槽位存放的内容表种类（与 <see cref="ContentRegistry"/> 内部 switch 对应）。</summary>
public static class ContentSpace
{
    /// <summary>物件表（<see cref="ContentIndex.GetObject"/>）。</summary>
    public const int Object = 0;

    /// <summary>武器表（<see cref="ContentIndex.GetWeapon"/>）。</summary>
    public const int Weapon = 1;

    /// <summary>子弹表（<see cref="ContentIndex.GetProjectile"/>）。</summary>
    public const int Projectile = 2;  // arch-ok: 内容表枚举序号，结构性常量
}

/// <summary>
/// 战斗系统族的异常：装配错误（槽位重复声明、契约缺失）与结构性误用统一用它。
/// <para>为什么不用 <c>InvalidOperationException</c>：装配错误在启动阶段就该定位到"哪张表 / 哪个槽"，
/// 专用类型让宿主能把它和普通 bug 分开处理（前者提示策划改表，后者是程序 bug）。</para>
/// </summary>
public sealed class GameCombatException : Exception
{
    /// <summary>构造。</summary>
    public GameCombatException(string message)
        : base(message)
    {
    }

    /// <summary>构造（带内部异常）。</summary>
    public GameCombatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
