using System;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Actor;

// 「系统如何拿到数值」的接缝（规则4 / 规则6）。
//
// 为什么这组接口声明在 scripts/actor/ 而不是 scripts/data/：
//   系统族（移动/瞄准/射击）只该知道自己能读哪些数，不该知道数从 Excel、.tres 还是测试桩里来。
//   把契约放在系统族这边、由数据层实现，依赖方向才是「数据 → 系统」，而不是反过来 ——
//   于是 tests/ 可以给一个纯内存实现，把移动与射击的全部规则在**没有引擎、没有 Excel** 的进程里跑通。
//
// 为什么每个方法是"返回 float"的窄接口，而不是直接暴露 IReadOnlyStatView：
//   IReadOnlyStatView 是「按 StatId 查」的通用视图，用在逐实体热路径上会变成「每实体一次接口调用 + 一次查表」；
//   而移动系统真正需要的只有两个量（移速、加速响应）。窄接口让调用点即契约：
//   "这里读了哪些数"一眼可见，新增一个依赖会立刻在签名上体现出来（规则5 的可审查性）。

/// <summary>
/// 系统视角下的「角色数值读取器」：给定实体，读它<u>折叠之后</u>的最终数值。
/// <para>
/// <b>为什么要一个<u>实体级</u>接口而不复用 <see cref="IReadOnlyStatView"/></b>：数值包是「层栈」，
/// 折叠结果挂在<u>角色</u>上而不是包上（同一个包被 200 只怪共享，但 Buff 层各不相同）。
/// 实现方（数据层/装配层）知道怎么从一个 <c>Entity</c> 找到它的折叠视图；系统只关心值。
/// </para>
/// <para>
/// <b>为什么不在这里塞 setter</b>：规则6 §6.1.1 的硬要求 —— 外部模块不得持有可写引用。
/// 需要改数的地方走「换包」这条正式路径（<c>StatPackageIds</c> + 重新装配），而不是逐个属性写。
/// </para>
/// </summary>
public interface IActorStatReader
{
    /// <summary>读取实体某个属性的最终值；实体不存在或缺该属性返回 <paramref name="fallback"/>。</summary>
    float Read(Entity entity, StatId stat, float fallback);

    /// <summary>实体是否有可读的数值快照（没有的话系统应跳过它，而不是拿 0 去动）。</summary>
    bool HasStats(Entity entity);
}

/// <summary>
/// 移动系统需要的两个量（**窄接口就是文档**：让「移动只依赖这两个数」变成可审查的事实）。
/// </summary>
public interface IMoveStatReader
{
    /// <summary>移速（米/秒）；读不到时返回 <paramref name="fallback"/>。</summary>
    float MoveSpeed(Entity entity, float fallback);

    /// <summary>
    /// 从意图速度逼近目标速度的响应时间（秒）。&lt;= 0 表示"瞬间到位"。
    /// <para>为什么不写死一个 lerp 系数：加速手感是纯玩法数值（重甲起步慢、敏捷起步快），
    /// 必须来自数值包（规则4）。把它表达成"时间"而不是"系数"，是因为策划更容易说"0.08 秒内到位"。</para>
    /// </summary>
    float AccelerationSeconds(Entity entity, float fallback);
}

/// <summary>
/// 照射（瞄准）系统需要的两个量。
/// </summary>
public interface IAimStatReader
{
    /// <summary>转向平滑时间（秒）：角色朝向追上瞄准方向所需时间；&lt;= 0 = 瞬间对准。</summary>
    float TurnResponseSeconds(Entity entity, float fallback);

    /// <summary>瞄准距离上限（米）：光标点过远时夹取到该距离，避免"用手点远处 = 打直线"的怪手感。</summary>
    float AimRangeMeters(Entity entity, float fallback);
}

/// <summary>
/// 射击系统需要的量：武器的<u>最终</u>数值。
/// <para>
/// <b>为什么武器数值要经过这里而不是直接读 <c>WeaponStats</c> 组件</b>：组件里的数是"武器行原始值"，
/// 而实际伤害还要叠敏捷（攻速）/幸运（暴击）/力量（伤害）这些角色数值（规则6 的折叠）。
/// 折算是数据的事（Excel Formula 表），系统的职责只是"按最终值开火"。
/// 实现方通常是「读组件基础值 → 用 <see cref="IActorStatReader"/> 叠角色数值」。
/// </para>
/// </summary>
public interface IWeaponReader
{
    /// <summary>取实体的最终武器数值；实体没有武器返回 <c>false</c>。</summary>
    bool TryRead(Entity entity, out WeaponStats stats);
}

/// <summary>
/// 静态数值提供者：与实体无关、但影响手感的全局谱调（默认兜底值）。
/// <para>为什么把兜底值集中到一个对象：兜底值本身也是"数值"，但它们是<u>缺失数据时的保护值</u>，
/// 必须有一个唯一来源以便审计（出现"为什么这个怪不动"时，第一个怀疑对象就是兜底值被用上了）。
/// 生产环境应当检查 <see cref="HasFallbacks"/>，等于兜底值一旦生效就有日志。</para>
/// </summary>
public sealed class MoveAimTuning
{
    /// <summary>读不到移速时用的兜底移速（米/秒）。</summary>
    public float FallbackMoveSpeed { get; }

    /// <summary>读不到加速响应时用的兜底值（秒）。</summary>
    public float FallbackAccelerationSeconds { get; }

    /// <summary>读不到转向响应时用的兜底值（秒）。</summary>
    public float FallbackTurnResponseSeconds { get; }

    /// <summary>读不到瞄准距离上限时用的兜底值（米）。</summary>
    public float FallbackAimRangeMeters { get; }

    /// <summary>构造（全部来自注入；默认 0 表示"不做兜底"，读不到就按 0 处理 —— 会让实体不动，容易被发现）。</summary>
    public MoveAimTuning(
        float fallbackMoveSpeed = 0f,
        float fallbackAccelerationSeconds = 0f,
        float fallbackTurnResponseSeconds = 0f,
        float fallbackAimRangeMeters = 0f)
    {
        FallbackMoveSpeed = fallbackMoveSpeed;
        FallbackAccelerationSeconds = fallbackAccelerationSeconds;
        FallbackTurnResponseSeconds = fallbackTurnResponseSeconds;
        FallbackAimRangeMeters = fallbackAimRangeMeters;
    }

    /// <summary>是否有任何一个兜底值被配置过（宿主据此打一行警告：兜底值=数据缺失）。</summary>
    public bool HasFallbacks =>
        FallbackMoveSpeed > 0f || FallbackAccelerationSeconds > 0f
        || FallbackTurnResponseSeconds > 0f || FallbackAimRangeMeters > 0f;

    /// <summary>不会用兜底值的实例（测试与"数据必须齐全"的构建用）。</summary>
    public static MoveAimTuning None => new MoveAimTuning();
}

/// <summary>
/// 「实体的数值快照」到「系统能读的接口」的最薄适配：把 <see cref="IReadOnlyStatView"/> 包成各窄接口。
/// <para>
/// <b>为什么提供这个类</b>：宿主/数据层往往已经有一个"实体 → IReadOnlyStatView"的表，
/// 让它们再各写一遍 <see cref="IMoveStatReader"/> 是重复劳动且容易两处漂移（规则3：先找公共抽象）。
/// 已有视图的宿主只需要 <c>new ActorStatReader(entity => table[entity])</c> 一行。
/// </para>
/// </summary>
public sealed class ActorStatReader : IActorStatReader, IMoveStatReader, IAimStatReader
{
    private readonly Func<Entity, IReadOnlyStatView?> _resolve;

    /// <summary>构造：给一个「实体 → 数值视图」的解析函数（返回 null = 该实体没有数值）。</summary>
    public ActorStatReader(Func<Entity, IReadOnlyStatView?> resolve)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <inheritdoc />
    public bool HasStats(Entity entity) => _resolve(entity) is not null;

    /// <inheritdoc />
    public float Read(Entity entity, StatId stat, float fallback)
    {
        IReadOnlyStatView? view = _resolve(entity);
        return view is null ? fallback : view.Get(stat);
    }

    /// <inheritdoc />
    public float MoveSpeed(Entity entity, float fallback) => Read(entity, StatId.MoveSpeed, fallback);

    /// <inheritdoc />
    /// <remarks>
    /// 加速响应取 <see cref="StatId.MoveAccelTime"/>（追加区 id，见 StatId 的 1023）。
    /// </remarks>
    public float AccelerationSeconds(Entity entity, float fallback) => Read(entity, StatId.MoveAccelTime, fallback);

    /// <inheritdoc />
    public float TurnResponseSeconds(Entity entity, float fallback) => Read(entity, StatId.TurnResponseTime, fallback);

    /// <inheritdoc />
    public float AimRangeMeters(Entity entity, float fallback) => Read(entity, StatId.AimRange, fallback);
}

/// <summary>
/// 武器数值的默认读取器：直接读实体身上的 <see cref="WeaponStats"/> 组件。
/// <para>
/// <b>为什么默认实现就是"读组件"，而不是去查数据表</b>：武器行的原始值在装配期（<see cref="ActorAssemblySystem"/>）
/// 就已经写进组件了，运行期再回查数据表等于每帧多做一次字典查找，而且会让"组件里的值"与"表里的值"
/// 存在两套真相。组件的值就是<u>本帧生效值</u>；将来要叠 Buff 改武器数值，就在装配/结算系统里改组件
/// （规则6：一切通过包/组件流转），本类不用改一行。
/// </para>
/// <para>
/// <b>为什么它 <c>public</c> 且可被替换</b>：<see cref="IWeaponReader"/> 是接缝，本类是默认实现。
/// 需要"先叠幸运暴击再开火"这类折算时，宿主注入另一个实现即可，射击技能本身不变（规则7）。
/// </para>
/// </summary>
public sealed class ComponentWeaponReader : IWeaponReader
{
    private readonly Game.Core.Ecs.World _world;

    /// <summary>构造：绑定到某个世界（武器组件就在这个世界里）。</summary>
    public ComponentWeaponReader(Game.Core.Ecs.World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    /// <inheritdoc />
    public bool TryRead(Entity entity, out WeaponStats stats) => _world.TryGet(entity, out stats);
}
