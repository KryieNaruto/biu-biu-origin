using System;
using Game.Core.Ecs;

namespace Game.Host;

/// <summary>
/// 一帧的阶段计时快照（值类型，零分配）。用途：控制台性能摘要 + 将来的调试面板。
/// </summary>
public readonly struct FramePhases
{
    /// <summary>PreUpdate 耗时（毫秒）。</summary>
    public double PreUpdateMs { get; init; }

    /// <summary>FixedUpdate 总耗时（毫秒，含多次固定步）。</summary>
    public double FixedUpdateMs { get; init; }

    /// <summary>本帧实际跑了几个固定步。</summary>
    public int FixedSteps { get; init; }

    /// <summary>Update 耗时（毫秒）。</summary>
    public double UpdateMs { get; init; }

    /// <summary>LateUpdate 耗时（毫秒）。</summary>
    public double LateUpdateMs { get; init; }

    /// <summary>命令提交耗时（毫秒）。</summary>
    public double FlushMs { get; init; }

    /// <summary>事件派发耗时（毫秒）。</summary>
    public double DispatchMs { get; init; }

    /// <summary>视图桥耗时（毫秒）。</summary>
    public double ViewMs { get; init; }

    /// <summary>本帧 ECS 侧合计耗时（毫秒）。</summary>
    public double TotalMs => PreUpdateMs + FixedUpdateMs + UpdateMs + LateUpdateMs + FlushMs + DispatchMs + ViewMs;
}

/// <summary>
/// 固定步长累积器 + **一帧阶段图**。这是 ECS.md §6「顺序即契约」的唯一落地处：
/// 阶段顺序只在这个类的 <see cref="Tick"/> 里写一遍，任何改动都是架构变更。
/// <para>
/// <b>一帧的七个阶段（顺序不可换）</b>：
/// <code>
///   PreUpdate → FixedUpdate×N → Update → LateUpdate → CommandFlush → 事件派发 → 视图桥
/// </code>
/// </para>
/// <para>
/// <b>为什么 Flush 必须排在 LateUpdate 之后</b>：<c>SpawnDirectorSystem</c>（Update）写的生成命令、
/// <c>LifetimeSystem</c>（Update）写的销毁命令，都要和 <c>StatSyncSystem</c>（LateUpdate）对同一批
/// 实体做的数值同步落在同一帧。先 Flush 会让「本帧新生成的实体错过本帧的数值同步」，
/// 出现一帧肉眼可见的血量/外观不一致。
/// </para>
/// <para>
/// <b>为什么不读 <c>_PhysicsProcess</c> 的 delta</b>：引擎物理步长由
/// <c>physics/common/physics_ticks_per_second</c> 决定，改它等于改玩法；而且物理回调的时机由引擎
/// 内部决定，无法保证与渲染帧的关系。宿主自持累积器后，FixedUpdate 只吃恒定 dt，
/// 于是「同 seed + 同输入 → 同快照」成立（ECS.md §8 确定性要求，回放/预测的前提）。
/// </para>
/// <para>
/// <b>为什么需要 <c>_maxStepsPerFrame</c></b>：断点、加载、窗口拖动会让某一帧的 dt 达到几百毫秒。
/// 若照单全收地补 20 步，下一步又更久，形成死亡螺旋。上限把「补帧」变成有限预算，
/// 超出的时间直接丢弃（宁可让逻辑慢半拍，也不能让帧时间失控）。
/// </para>
/// </summary>
public sealed class FixedFramePipeline
{
    private readonly World _world;
    private readonly SystemPipeline _systems;
    private readonly CommandFlushSystem _flush;
    private readonly Action _dispatchEvents;
    private readonly Action<float> _syncViews;

    private readonly double _fixedDt;
    private readonly int _maxStepsPerFrame;

    private double _accumulator;
    private float _alpha;
    private int _frameCount;
    private long _droppedSecondsTicks;
    private FramePhases _lastPhases;

    /// <summary>
    /// 构造管线。
    /// </summary>
    /// <param name="world">被驱动的世界。</param>
    /// <param name="systems">已 <c>Build</c> 过的系统管线（本类不再排序）。</param>
    /// <param name="flush">唯一结构化提交点。</param>
    /// <param name="dispatchEvents">帧末事件派发（由宿主注入，避免本类依赖事件总线实现）。</param>
    /// <param name="syncViews">视图桥（参数是插值 alpha）。</param>
    /// <param name="fixedHz">固定步长频率。</param>
    /// <param name="maxStepsPerFrame">单帧最大补步数。</param>
    public FixedFramePipeline(
        World world,
        SystemPipeline systems,
        CommandFlushSystem flush,
        Action dispatchEvents,
        Action<float> syncViews,
        int fixedHz,
        int maxStepsPerFrame)
    {
        if (world is null)
        {
            throw new ArgumentNullException(nameof(world));
        }
        if (systems is null)
        {
            throw new ArgumentNullException(nameof(systems));
        }
        if (flush is null)
        {
            throw new ArgumentNullException(nameof(flush));
        }
        if (fixedHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedHz), "固定步长频率必须大于 0。");
        }
        if (maxStepsPerFrame <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStepsPerFrame), "单帧最大补步数必须大于 0。");
        }

        _world = world;
        _systems = systems;
        _flush = flush;
        _dispatchEvents = dispatchEvents ?? throw new ArgumentNullException(nameof(dispatchEvents));
        _syncViews = syncViews ?? throw new ArgumentNullException(nameof(syncViews));
        _fixedDt = 1.0 / fixedHz;
        _maxStepsPerFrame = maxStepsPerFrame;
    }

    /// <summary>固定步长（秒）。</summary>
    public double FixedDeltaSeconds => _fixedDt;

    /// <summary>渲染插值系数，∈ [0,1)：表示「下一个固定步已经走了多少比例」。</summary>
    public float Alpha => _alpha;

    /// <summary>已推进的渲染帧数。</summary>
    public int FrameCount => _frameCount;

    /// <summary>因为超过补步上限而被丢弃的时间（秒，累计）。非 0 说明出现过卡顿。</summary>
    public double DroppedSeconds => (double)_droppedSecondsTicks / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>最近一帧的分阶段耗时。</summary>
    public FramePhases LastPhases => _lastPhases;

    /// <summary>逻辑帧号：每推进一个固定步 +1。服务端权威帧与回放对齐都用它。</summary>
    public long TickIndex { get; private set; }

    /// <summary>
    /// 推进一帧。由 <c>WorldHost._Process</c> 每帧调用一次。
    /// <para>内部严格按 ECS.md §6 的七阶段顺序执行；没有 <c>if</c> 分支决定「某阶段跳不跳」，
    /// 除了 <c>FixedUpdate</c> 的步数（累积器决定，0 步也是合法的）。</para>
    /// </summary>
    /// <param name="frameDeltaSeconds">渲染帧 dt（秒）。</param>
    /// <param name="timeScale">全局时间缩放。</param>
    public void Tick(double frameDeltaSeconds, float timeScale)
    {
        if (frameDeltaSeconds < 0.0)
        {
            frameDeltaSeconds = 0.0;
        }

        double scaled = frameDeltaSeconds * timeScale;
        _accumulator += scaled;

        double preMs;
        double fixedMs;
        double updateMs;
        double lateMs;
        double flushMs;
        double dispatchMs;
        double viewMs;
        int steps;

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // ① PreUpdate：输入采集、随机推进、命令缓冲准备。
        _systems.ExecuteGroup(UpdateGroup.PreUpdate, (float)scaled);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        preMs = ElapsedMs(t0, t1);

        // ② FixedUpdate：恒定 dt、可 0..N 次。
        steps = 0;
        // 恒定 dt：这是确定性的来源，不能用 scaled（scaled 会随帧率抖动）。
        float fixedDtFloat = (float)_fixedDt;
        while (_accumulator >= _fixedDt && steps < _maxStepsPerFrame)
        {
            _systems.ExecuteGroup(UpdateGroup.FixedUpdate, fixedDtFloat);
            _accumulator -= _fixedDt;
            steps++;
            TickIndex++;
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        fixedMs = ElapsedMs(t1, t2);

        // 超出补步上限的积压直接丢弃：宁可慢半拍，也不要死亡螺旋。
        if (_accumulator >= _fixedDt * _maxStepsPerFrame)
        {
            long dropped = (long)(_accumulator * System.Diagnostics.Stopwatch.Frequency);
            _droppedSecondsTicks += dropped;
            _accumulator = 0.0;
        }

        // 插值 alpha：累积器余量 / 固定步长。落在 [0,1)。
        _alpha = (float)(_accumulator / _fixedDt);
        if (_alpha < 0f)
        {
            _alpha = 0f;
        }
        else if (_alpha >= 1f)
        {
            _alpha = 0.99999994f;  // arch-ok: 浮点开区间上界 1-eps，防止插值 alpha 取到 1（数值安全常量）
        }

        // ③ Update：每帧一次的玩法逻辑（生命周期、伤害结算等）。
        _systems.ExecuteGroup(UpdateGroup.Update, (float)scaled);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        updateMs = ElapsedMs(t2, t3);

        // ④ LateUpdate：表现只读数据的采集、数值同步。
        _systems.ExecuteGroup(UpdateGroup.LateUpdate, (float)scaled);
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        lateMs = ElapsedMs(t3, t4);

        // ⑤ 命令提交：本帧唯一允许结构化变更的时刻（J5）。
        _flush.Submit();
        long t5 = System.Diagnostics.Stopwatch.GetTimestamp();
        flushMs = ElapsedMs(t4, t5);

        // ⑥ 事件派发：帧末统一派发，订阅方只读。
        _dispatchEvents();
        long t6 = System.Diagnostics.Stopwatch.GetTimestamp();
        dispatchMs = ElapsedMs(t5, t6);

        // ⑦ 视图桥：写引擎节点（Transform / 生命周期）。
        _syncViews(_alpha);
        long t7 = System.Diagnostics.Stopwatch.GetTimestamp();
        viewMs = ElapsedMs(t6, t7);

        _frameCount++;
        _lastPhases = new FramePhases
        {
            PreUpdateMs = preMs,
            FixedUpdateMs = fixedMs,
            FixedSteps = steps,
            UpdateMs = updateMs,
            LateUpdateMs = lateMs,
            FlushMs = flushMs,
            DispatchMs = dispatchMs,
            ViewMs = viewMs,
        };
    }

    private static double ElapsedMs(long start, long end)
        => (double)(end - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>一帧的组件内存/实体数摘要（诊断用，只在打印时调用）。</summary>
    public WorldStats SnapshotStats() => _world.Stats;
}
