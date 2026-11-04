using System;
using Game.Core.Ecs;
using Godot;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Game.Host.Bridge;

/// <summary>
/// 变换桥：把 ECS 的 <see cref="Transform3"/>（纯数据、权威）推到引擎节点（表现、从属）。
/// <para>
/// <b>单向铁律</b>（ECS.md §7.1）：<b>只允许 ECS → Node，禁止 Node → ECS</b>。
/// 节点上的任何位移（动画、物理、手动拖动）都不得反写 <see cref="Transform3"/>；
/// 一旦允许反写，「谁是权威」就变得不可判定，回放与服务器校验全部失效。
/// </para>
/// <para>
/// <b>插值的 alpha 从哪来</b>：来自 <see cref="FixedFramePipeline.Alpha"/>，
/// 即「累积器当前余量 ÷ 固定步长」。它表达的是「下一个固定步已经走了多少比例」。
/// 固定步长下 <see cref="Transform3"/> 只每步更新一次，若直接把权威位置写给节点，
/// 在 144Hz 屏上会看到「逻辑每 4 帧跳一下」的阶梯抖动。用
/// <c>lerp(上一步位置, 本步位置, alpha)</c> 可以让渲染位置连续。
/// 注意 alpha 来自我们自己的累积器，<u>不是</u>引擎的物理插值 —— ECS 没有节点物理，
/// 引擎插值参数对我们没有意义。
/// </para>
/// <para>
/// <b>上一步位置存在哪</b>：<see cref="ViewRegistry"/> 的旁路槽位里，<b>不进组件</b>。
/// 理由：① 组件必须是 unmanaged 且不含表现语义；② 插值历史不属于玩法状态，
/// 不该进存档、不该参与确定性回放。
/// </para>
/// </summary>
public sealed class TransformBridge : ISpanAction<Transform3, View>
{
    private readonly World _world;

    /// <summary>接口视图（自身）：避免 <c>in this</c> 把 class 装箱成只读副本。</summary>
    private readonly ISpanAction<Transform3, View> _action;
    private readonly ViewRegistry _views;
    private readonly Query _query;

    private NVector3[] _positions;
    private NQuaternion[] _rotations;
    private int[] _viewIds;
    private float[] _scales;
    private int _capacity;
    private int _count;

    private readonly Vector3 _cullingOrigin;
    private readonly float _cullingDistance;
    private readonly bool _interpolate;

    /// <summary>最近一次同步写入的节点数（诊断：剔除是否生效）。</summary>
    public int LastSyncedCount { get; private set; }

    /// <summary>最近一次同步被剔除（隐藏）的节点数。</summary>
    public int LastCulledCount { get; private set; }

    /// <summary>
    /// 构造变换桥。
    /// </summary>
    /// <param name="world">世界。</param>
    /// <param name="views">视图注册表。</param>
    /// <param name="cullingOrigin">剔除原点（通常是相机位置）。</param>
    /// <param name="cullingDistance">剔除距离（米）；&lt;= 0 表示不剔除。</param>
    /// <param name="interpolate">是否做渲染插值。</param>
    /// <param name="initialCapacity">收集缓冲初始容量。</param>
    public TransformBridge(
        World world,
        ViewRegistry views,
        Vector3 cullingOrigin,
        float cullingDistance,
        bool interpolate,
        int initialCapacity = 128)  // arch-ok: 变换批处理缓冲初始容量（内存预分配，不是玩法数值）
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _query = world.Query<Transform3, View>();

        _cullingOrigin = cullingOrigin;
        _cullingDistance = cullingDistance;
        _interpolate = interpolate;
        _action = this;

        _capacity = Math.Max(initialCapacity, 1);
        _positions = new NVector3[_capacity];
        _rotations = new NQuaternion[_capacity];
        _viewIds = new int[_capacity];
        _scales = new float[_capacity];
    }

    /// <summary>
    /// 同步一帧。由 <see cref="FixedFramePipeline"/> 在视图桥阶段调用**一次**。
    /// <para>
    /// <b>为什么先收集、再写节点</b>：<c>Query.ForEach</c> 的回调里持有 chunk 的 Span，
    /// 而写节点是一次跨语言调用（可能触发引擎侧的信号/脚本回调）。虽然当前节点都是纯视觉节点、
    /// 不会回到这里来改 ECS，但「遍历期间不做任何可能有副作用的外部调用」是一条纪律：
    /// 它保证将来即使视图脚本回调了什么东西，也不会在遍历中触发结构化变更。
    /// </para>
    /// </summary>
    public void Push(float alpha)
    {
        _count = 0;

        // 阶段 1：只读收集（纯数据搬运，无引擎调用）。
        CollectPass();

        // 阶段 2：写节点（每个实体一次跨语言调用）。
        int synced = 0;
        int culled = 0;
        bool doCull = _cullingDistance > 0f;
        float cullSq = _cullingDistance * _cullingDistance;
        bool interpolate = _interpolate;

        for (int i = 0; i < _count; i++)
        {
            int viewId = _viewIds[i];
            NVector3 p = _positions[i];

            if (doCull)
            {
                float dx = p.X - _cullingOrigin.X;
                float dy = p.Y - _cullingOrigin.Y;
                float dz = p.Z - _cullingOrigin.Z;
                if (dx * dx + dy * dy + dz * dz > cullSq)
                {
                    _views.SetVisible(viewId, false);
                    culled++;
                    continue;
                }
            }

            NQuaternion r = _rotations[i];
            float s = _scales[i];
            Vector3 gv = new Vector3(p.X, p.Y, p.Z);
            Quaternion gq = new Quaternion(r.X, r.Y, r.Z, r.W);

            // 缩放只在租借时写一次（它不随每帧变化），这里只写位置与旋转。
            _views.SyncTransform(viewId, in gv, in gq, alpha, interpolate);
            _views.SetVisible(viewId, true);
            synced++;
        }

        LastSyncedCount = synced;
        LastCulledCount = culled;
    }

    /// <summary>
    /// 收集阶段的回调（只读，不碰引擎）。由 <see cref="CollectPass"/> 通过
    /// <c>Query.ForEach(ISpanAction&lt;T0,T1&gt;)</c> 接口调用 —— 用接口而不是委托，
    /// 是为了让 JIT 能去虚化 + 内联，且不产生任何委托对象。
    /// </summary>
    void ISpanAction<Transform3, View>.Execute(Span<Transform3> transforms, Span<View> views, int count)
    {
        // 空间不够就扩容（摊还一次；稳态下永不触发）。
        if (_count + count > _capacity)
        {
            Grow(_count + count);
        }
        for (int i = 0; i < count; i++)
        {
            Transform3 t = transforms[i];
            int index = _count++;
            _positions[index] = t.Position;
            _rotations[index] = t.Rotation;
            _viewIds[index] = views[i].Slot;
            _scales[index] = 1f;
        }
    }

    /// <summary>把本帧需要写的实体收集进旁路缓冲（不触发任何引擎调用）。</summary>
    private void CollectPass()
    {
        _query.ForEach<Transform3, View>(in _action);
    }

    private void Grow(int required)
    {
        int grown = _capacity;
        while (grown < required)
        {
            grown *= 2;
        }
        Array.Resize(ref _positions, grown);
        Array.Resize(ref _rotations, grown);
        Array.Resize(ref _viewIds, grown);
        Array.Resize(ref _scales, grown);
        _capacity = grown;
    }
}
