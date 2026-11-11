using System;
using Godot;

namespace Game.Host;

/// <summary>
/// 视图注册表：ECS 的 <c>ViewId</c>(int) ↔ 引擎 <see cref="Node3D"/> 的**池化旁路表**。
/// <para>
/// <b>为什么需要它（而不是让组件直接存 Node）</b>：J2 一票否决「组件持有 Node3D」。
/// 于是组件里只放一个 <c>int ViewId</c>（官方组件 <c>View</c>），由本表把它翻译成节点。
/// 这样 ECS 侧永远是纯数据、可存档、可回放；节点侧的生命周期完全由宿主层单向驱动
/// （ECS.md §7.3：视图生命周期由 <c>NodeLifecycleSystem</c> 单向驱动，禁止反写）。
/// </para>
/// <para>
/// <b>为什么用「长数组 + 空闲栈」而不是 <c>Dictionary</c></b>：
/// <list type="bullet">
///   <item>稳态下每帧要对<u>全部</u>视图做一次 <c>SyncTransform</c>，数组顺序遍历对 cache 友好，
///         字典遍历会逐项哈希 + 随机跳内存；</item>
///   <item>Rent/Return 只需要 O(1) 的栈操作，不需要哈希；</item>
///   <item>扩容是摊还的一次性 <c>Array.Resize</c>，不进稳态路径。</item>
/// </list>
/// </para>
/// <para>
/// <b>池如何避免每帧 <c>new</c></b>（硬约束）：
/// <list type="number">
///   <item>节点**只 Instantiate 一次**，归还时 <c>Visible = false</c> 并压回空闲栈，**绝不 QueueFree**；</item>
///   <item>空闲栈在启动时按 <c>ViewPrewarm</c> 预填，稳态下 Rent 命中池，不产生分配；</item>
///   <item>所有旁路数组（节点/种类/上帧位置/可见性）按固定容量一次性分配，扩容按 2 倍增长。</item>
/// </list>
/// 验证手段：冒烟场景每 N 帧读 <c>GC.GetAllocatedBytesForCurrentThread()</c>，稳态前后差应当 ≈ 0。
/// </para>
/// </summary>
public sealed class ViewRegistry : IDisposable
{
    /// <summary>一个视图槽位的旁路数据（宿主层私有，绝不进组件）。</summary>
    private struct Slot
    {
        /// <summary>节点（归还期间仍然存在，只是不可见）。</summary>
        public Node3D? Node;

        /// <summary>该槽位当前是否被租用。</summary>
        public bool InUse;

        /// <summary>上一步的逻辑位置（插值用；归还时清零）。</summary>
        public Vector3 PreviousPosition;

        /// <summary>上一步的逻辑旋转（插值用）。</summary>
        public Quaternion PreviousRotation;

        /// <summary>当前是否由宿主设为可见（与 <see cref="InUse"/> 分开：租出 ≠ 可见，剔除会隐藏但仍在租）。</summary>
        public bool Wanted;
    }

    private readonly Node3D _root;
    private readonly PackedScene _prefab;
    /// <summary>空闲槽位栈。**故意不是 readonly**：扩容时 <c>Array.Resize</c>/整体替换需要字段可写。</summary>
    private int[] _freeStack;
    private int _freeCount;
    private Slot[] _slots;
    private int _slotCount;
    private bool _disposed;

    private int _rentCount;
    private int _returnCount;
    private int _instantiateCount;

    /// <summary>
    /// 构造视图注册表。
    /// </summary>
    /// <param name="root">所有视图的统一父节点（由宿主在 <c>_Ready</c> 创建一次）。</param>
    /// <param name="prefab">视图预制体（子弹/角色共用；按需扩展成"按种类选预制体"）。</param>
    /// <param name="prewarm">启动时一次性预租的槽位数。</param>
    /// <param name="initialCapacity">旁路数组初始容量。</param>
    public ViewRegistry(Node3D root, PackedScene prefab, int prewarm, int initialCapacity = 64)  // arch-ok: 视图池初始容量（内存预分配，不是玩法数值）
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
        if (initialCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), "初始容量必须 >= 1。");
        }

        _slots = new Slot[initialCapacity];
        _freeStack = new int[initialCapacity];
        _freeCount = 0;
        _slotCount = 0;

        // 预热：一次性 Instantiate，之后全部走复用。
        // 空闲节点必须立刻隐藏：新节点默认 Visible = true，预热的空壳会全留在场景里
        // （空壳没有网格，肉眼看不出来，但「池里哪个被租出去了」就再也无法靠可见性判断）。Rent 时会重新点亮。
        for (int i = 0; i < prewarm; i++)
        {
            int id = _slotCount;
            EnsureCapacity(id + 1);
            Node3D node = InstantiateNode();
            node.Visible = false;
            _slots[id] = new Slot { Node = node, InUse = false, Wanted = false };
            _freeStack[_freeCount++] = id;
            _slotCount++;
            _instantiateCount++;
        }
    }

    /// <summary>已分配的槽位总数（含空闲）。</summary>
    public int SlotCount => _slotCount;

    /// <summary>当前正在使用的视图数。</summary>
    public int InUseCount => _slotCount - _freeCount;

    /// <summary>累计 Instantiate 次数（稳态下不应再增长）。</summary>
    public int InstantiateCount => _instantiateCount;

    /// <summary>累计租借次数。</summary>
    public int RentCount => _rentCount;

    /// <summary>累计归还次数。</summary>
    public int ReturnCount => _returnCount;

    /// <summary>
    /// 租一个视图，返回 <c>ViewId</c>。空闲栈非空时**不分配、不 Instantiate**。
    /// </summary>
    public int Rent(Vector3 position, Quaternion rotation, float scale)
    {
        int id;
        if (_freeCount > 0)
        {
            id = _freeStack[--_freeCount];
        }
        else
        {
            id = _slotCount;
            EnsureCapacity(id + 1);
            _slots[id] = new Slot { Node = InstantiateNode(), InUse = false };
            _slotCount++;
            _instantiateCount++;
        }

        Slot slot = _slots[id];
        Node3D? node = slot.Node;
        slot.InUse = true;
        slot.Wanted = true;
        slot.PreviousPosition = position;
        slot.PreviousRotation = rotation;
        _slots[id] = slot;

        if (node is not null)
        {
            node.Visible = true;
            node.Transform = new Transform3D(new Basis(rotation), position);
            node.Scale = new Vector3(scale, scale, scale);
        }

        _rentCount++;
        return id;
    }

    /// <summary>
    /// 归还视图：隐藏并压回空闲栈。**不销毁节点**（销毁会让下一帧重新 Instantiate）。
    /// 对已失效的 id 是安全的空操作。
    /// </summary>
    public void Return(int viewId)
    {
        if (viewId < 0 || viewId >= _slotCount)
        {
            return;
        }
        Slot slot = _slots[viewId];
        if (!slot.InUse)
        {
            return;
        }

        slot.InUse = false;
        slot.Wanted = false;
        _slots[viewId] = slot;

        if (slot.Node is not null)
        {
            slot.Node.Visible = false;
        }
        _returnCount++;
        _freeStack[_freeCount++] = viewId;
    }

    /// <summary>取视图节点（可能为 null；调用方必须判空）。诊断/测试用，热路径请用 <see cref="SyncTransform"/>。</summary>
    public Node3D? GetNode(int viewId)
    {
        if (viewId < 0 || viewId >= _slotCount)
        {
            return null;
        }
        Slot slot = _slots[viewId];
        return slot.InUse ? slot.Node : null;
    }

    /// <summary>
    /// 写一个视图的渲染变换（插值后）。宿主层最热的跨语言调用，所以：
    /// ① 只写 <c>Transform</c> 一个属性；② 不判空则直接用空传播；
    /// ③ 上帧位置在这里摊平，供下一帧插值。
    /// </summary>
    /// <param name="viewId">视图号。</param>
    /// <param name="currentPos">本帧逻辑位置（权威值）。</param>
    /// <param name="currentRot">本帧逻辑旋转（权威值）。</param>
    /// <param name="alpha">插值系数，∈[0,1)；小于 0 表示不做插值。</param>
    /// <param name="interpolate">是否启用插值。</param>
    public void SyncTransform(int viewId, in Vector3 currentPos, in Quaternion currentRot, float alpha, bool interpolate)
    {
        if (viewId < 0 || viewId >= _slotCount)
        {
            return;
        }
        Slot slot = _slots[viewId];
        if (!slot.InUse || slot.Node is null)
        {
            return;
        }

        Vector3 shown;
        if (interpolate)
        {
            shown = slot.PreviousPosition.Lerp(currentPos, alpha);
        }
        else
        {
            shown = currentPos;
        }

        slot.Node.Transform = new Transform3D(new Basis(slot.PreviousRotation.Slerp(currentRot, interpolate ? alpha : 1f)), shown);

        // 摊平：本帧的权威值成为下一帧的插值起点。
        slot.PreviousPosition = currentPos;
        slot.PreviousRotation = currentRot;
        _slots[viewId] = slot;
    }

    /// <summary>设置可见性（剔除用：远距离实体不写矩阵，直接隐藏）。</summary>
    public void SetVisible(int viewId, bool visible)
    {
        if (viewId < 0 || viewId >= _slotCount)
        {
            return;
        }
        Slot slot = _slots[viewId];
        if (!slot.InUse || slot.Node is null)
        {
            return;
        }

        // ★ 只记「宿主想要什么」，真正写 Visible 推迟到帧末 ApplyVisibility。
        //   为什么：视图池是所有实体共用的，而每帧写 Visible 的地方不止一个（VisualBridge、
        //   TransformBridge 的剔除、ActorViewDirector 的朝向占位）。谁最后写谁生效，
        //   结果就是「被剔除的实体因为租出时置了一次 true 而永远可见」。
        //   改成同帧内累加意图、帧末按「租用 × 宿主想要的可见性」统一落盘，顺序就不再影响结果。
        slot.Wanted = visible;
        _slots[viewId] = slot;
    }

    /// <summary>
    /// 把本帧累计的可见性意图落盘（帧末调一次，O(已分配的槽位数)）。
    /// <para>
    /// 规则只有一条：<b>可见 = 在租用 且 宿主想要可见</b>。池外的节点一律隐藏，
    /// 这样「谁的节点漏隐藏」这类 bug 不会以「幽灵节点」的形式潜伏在场景里。
    /// </para>
    /// </summary>
    /// <returns>本帧真正改变了可见性的槽位数（稳态下应为 0；诊断用）。</returns>
    public int ApplyVisibility()
    {
        int changed = 0;
        for (int i = 0; i < _slotCount; i++)
        {
            Node3D? node = _slots[i].Node;
            if (node is null || !GodotObject.IsInstanceValid(node))
            {
                continue;
            }
            bool visible = _slots[i].InUse && _slots[i].Wanted;
            if (node.Visible != visible)
            {
                node.Visible = visible;
                changed++;
            }
        }
        return changed;
    }

    private Node3D InstantiateNode()
    {
        Node instantiated = _prefab.Instantiate();
        if (instantiated is not Node3D node)
        {
            instantiated.QueueFree();
            throw new InvalidOperationException(string.Concat(
                "视图预制体 '", _prefab.ResourcePath, "' 的根节点必须继承 Node3D（TransformBridge 需要 3D 变换），实际为 ",
                instantiated.GetType().Name, "。"));
        }
        _root.AddChild(node);
        return node;
    }

    private void EnsureCapacity(int required)
    {
        if (_slots.Length >= required)
        {
            return;
        }
        int grown = _slots.Length;
        while (grown < required)
        {
            grown *= 2;
        }
        Array.Resize(ref _slots, grown);

        int[] freeStack = new int[Math.Max(grown, _freeCount + 1)];
        Array.Copy(_freeStack, freeStack, _freeCount);
        _freeStack = freeStack;
    }

    /// <summary>释放：把所有视图从树上摘掉（场景切换时必须调用，否则节点会残留）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        for (int i = 0; i < _slotCount; i++)
        {
            Node3D? node = _slots[i].Node;
            _slots[i] = default;
            if (node is not null && GodotObject.IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }
        _slotCount = 0;
        _freeCount = 0;
        _slots = Array.Empty<Slot>();
    }
}
