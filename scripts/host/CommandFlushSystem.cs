using System;
using Game.Core.Ecs;

namespace Game.Host;

/// <summary>
/// 本帧**唯一**的结构化变更提交点（ECS.md §6 的 Flush 阶段 / J5 的执行方式）。
/// <para>
/// <b>为什么它要存在，而不是让宿主直接调 <c>world.Commands.Flush()</c></b>：
/// <list type="number">
///   <item>它是唯一能在<u>代码层面</u>被找到的提交点 —— 审计「有没有人在遍历中改结构」时，
///         只需要 grep <c>.Flush(</c>，答案应当只有本文件一处；</item>
///   <item>提交前后的观测（提交了多少条、跳过几条、耗时多少）需要一个固定的挂点；</item>
///   <item>将来若要把 Flush 挪进固定步（例如每 N 个固定步提交一次以降低迁移抖动），
///         改动被限制在这一个类里。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么不继承 SystemBase</b>：SystemBase 的子类会被 SystemPipeline 当作「每帧执行」的系统
/// 纳入 PreUpdate/FixedUpdate/Update/LateUpdate 之一。而 Flush 不属于这四个分组中的任何一个 ——
/// 它是四者之后的<u>独立阶段</u>（ECS.md §6 明确把 Flush 画在 LateUpdate 之后）。
/// 把它塞进某个分组会破坏「顺序即契约」。因此它作为一个普通类，由 <see cref="FixedFramePipeline"/>
/// 在第 ⑤ 阶段显式调用。
/// </para>
/// <para>
/// <b>冲突语义</b>：同一批命令里「目标实体已被销毁」是合法且常见的（两颗子弹同帧打死同一个怪），
/// 内核按静默跳过 + 计数处理。本类只负责把计数暴露出来，不重复定义语义。
/// </para>
/// </summary>
public sealed class CommandFlushSystem
{
    private readonly World _world;

    private long _totalSubmitted;
    private long _totalSkipped;
    private int _lastSubmitted;
    private int _lastSkipped;
    private double _lastMs;

    /// <summary>构造：绑定世界。</summary>
    public CommandFlushSystem(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    /// <summary>累计提交的命令条数。</summary>
    public long TotalSubmitted => _totalSubmitted;

    /// <summary>累计因冲突被跳过的命令条数。</summary>
    public long TotalSkipped => _totalSkipped;

    /// <summary>最近一次提交的命令条数。</summary>
    public int LastSubmitted => _lastSubmitted;

    /// <summary>最近一次提交被跳过的条数。</summary>
    public int LastSkipped => _lastSkipped;

    /// <summary>最近一次提交耗时（毫秒）。</summary>
    public double LastMs => _lastMs;

    /// <summary>当前待提交的命令数（诊断：若长期 > 0 说明有阶段跳过了 Flush）。</summary>
    public int PendingCount => _world.Commands.PendingCount;

    /// <summary>
    /// 提交本帧全部结构化命令。由 <see cref="FixedFramePipeline"/> 在 LateUpdate 之后调用**一次**。
    /// </summary>
    public void Submit()
    {
        CommandBuffer commands = _world.Commands;
        int pending = commands.PendingCount;
        if (pending == 0)
        {
            _lastSubmitted = 0;
            _lastSkipped = 0;
            _lastMs = 0.0;
            return;
        }

        int skippedBefore = commands.SkippedCommandCount;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        commands.Flush();

        long end = System.Diagnostics.Stopwatch.GetTimestamp();

        int executed = commands.LastExecutedCount;
        int skipped = commands.SkippedCommandCount - skippedBefore;

        _lastSubmitted = executed;
        _lastSkipped = skipped;
        _lastMs = (double)(end - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;  // arch-ok: 毫秒/秒单位换算（物理量纲常数，不是玩法数值）

        _totalSubmitted += executed;
        _totalSkipped += skipped;
    }
}
