using System;
using Game.Core;
using Game.Core.Ecs;
using Game.Core.Handles;
using Game.Core.Skills;
using Game.Actor;

namespace Game.Combat;

/// <summary>
/// 命中 → 事件 + 特效命令（契约 §3.1 的 <c>ImpactEventSystem</c>）。
/// <para>
/// <b>为什么命中与"命中之后发生什么"要分成两个系统</b>
/// </para>
/// <list type="bullet">
/// <item>命中检测必须在遍历里做（性能），而"发事件/生成特效"是结构变更与外部副作用（必须留到遍历后）；</item>
/// <item>两者之间隔着 <see cref="HitBuffer"/>，于是"命中记录"成为<u>可测试的数据</u>：
///       单测可以只跑检测系统，断言缓冲里的记录（不需要事件总线、不需要引擎）；</item>
/// <item>将来加"穿透/反弹/暴击特效分级"只改本系统，命中检测的几何逻辑一行不动（规则7）。</item>
/// </list>
/// <para>
/// <b>为什么特效走 <see cref="ICommandSink.SpawnEffect"/> 而不是自己 <c>World.Create</c></b>：
/// 命令门面是技能与系统共同的唯一结构变更出口（J5）；它在宿主侧落地（特效要池化节点、要引擎资源）。
/// 本系统只描述"这里出了一个命中火花"，不知道火花长什么样 —— 特效 id 由 <see cref="ImpactSettings"/> 给出（规则4）。
/// </para>
/// <para>
/// <b>为什么伤害不在这里扣</b>：见 <see cref="HitRecord"/> 的说明。本系统只把"打中了"广播出去，
/// 结算系统（护甲/暴击抗性/击杀）落地后订阅事件即可。这样"伤害规则"与"命中规则"可以分别被验证。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.Update)]
[Module("fire_primary")]
public sealed class ImpactEventSystem : SystemBase
{
    private readonly HitBuffer _hits;
    private readonly HostEventSink _events;
    private readonly ISkillContext _context;
    private readonly ImpactSettings _settings;

    /// <summary>本帧消费的命中数（调试/日志用）。</summary>
    public int ConsumedCount { get; private set; }

    /// <summary>本帧生成的特效请求数（可能少于命中数：数据里没配特效时就不发）。</summary>
    public int EffectsRequested { get; private set; }

    /// <summary>累计消费的命中数（宿主日志的 <c>hits=</c> 用它，跨帧不清零）。</summary>
    public int TotalConsumed { get; private set; }

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="hits">与 <see cref="HitDetectionSystem"/> 共享的命中缓冲。</param>
    /// <param name="events">业务事件出口（宿主实现；只收不发，派发时机归宿主）。</param>
    /// <param name="context">命令上下文（生成命中特效；技能上下文门面已经提供了 <see cref="ICommandSink"/>）。</param>
    /// <param name="settings">命中表现的参数（特效 id 等，来自数据）。</param>
    public ImpactEventSystem(
        HitBuffer hits,
        HostEventSink events,
        ISkillContext context,
        ImpactSettings settings)
    {
        _hits = hits ?? throw new ArgumentNullException(nameof(hits));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public override string Name => "ImpactEvent";

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _ = deltaTime;
        ConsumedCount = 0;
        EffectsRequested = 0;

        if (_hits.Count == 0)
        {
            return;
        }

        // 为什么用 ReadOnlySpan 而不是逐条 TryDequeue：命中是一帧几十~几百条的热路径，
        // 连续内存遍历 + 一次 Clear 比逐条出队少几百次函数调用与分支。
        ReadOnlySpan<HitRecord> records = _hits.Records;
        for (int i = 0; i < records.Length; i++)
        {
            HitRecord record = records[i];
            ActorHandle shooter = new ActorHandle(new Entity(record.ShooterIndex, record.ShooterGeneration));
            ActorHandle target = Actors.FromEntity(record.Target);

            // ① 几何命中事件：先广播"打到了"，结算系统据此扣血（本任务不实现结算）。
            _events.RaiseHit(shooter, target, record.Position.X, record.Position.Y, record.Position.Z, record.DamageType);

            // ② 命中特效：只在数据里配了 id 时才请求（空 id = 这个游戏暂时不要命中火花）。
            string effectId = _settings.EffectIdFor(record.Critical);
            if (!string.IsNullOrEmpty(effectId))
            {
                var at = new SpawnPoint(record.Position, System.Numerics.Quaternion.Identity);
                _context.Commands.SpawnEffect(effectId, in at);
                EffectsRequested++;
            }

            ConsumedCount++;
        }

        TotalConsumed += ConsumedCount;

        // 消费完就清空：命中记录是"一帧内的事实"，跨帧留存只会让调试时看到两帧的混在一起。
        _hits.Clear();
    }
}

/// <summary>
/// 命中表现参数（**来自数据**，不在代码里写"命中火花"的 id）。
/// <para>
/// 为什么单独一个类而不是给 <see cref="ImpactEventSystem"/> 加 5 个构造参数：将来命中表现会长出
/// "暴击换颜色""不同材质换火花"等维度，集中成一个对象后，宿主注入点不变，扩展只动本类。
/// </para>
/// </summary>
public sealed class ImpactSettings
{
    /// <summary>普通命中特效 id（空 = 不生成特效）。</summary>
    public string ImpactEffectId { get; }

    /// <summary>暴击命中特效 id（空 = 与普通命中相同）。</summary>
    public string CriticalImpactEffectId { get; }

    /// <summary>构造。</summary>
    public ImpactSettings(string impactEffectId, string criticalImpactEffectId = "")
    {
        ImpactEffectId = impactEffectId ?? string.Empty;
        CriticalImpactEffectId = criticalImpactEffectId ?? string.Empty;
    }

    /// <summary>按是否暴击取特效 id（暴击未单独配置时退回普通命中特效）。</summary>
    public string EffectIdFor(bool critical) =>
        critical && !string.IsNullOrEmpty(CriticalImpactEffectId) ? CriticalImpactEffectId : ImpactEffectId;
}
