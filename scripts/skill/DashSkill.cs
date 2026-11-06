using System;
using System.Numerics;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Skill;

/// <summary>
/// <b>冲刺</b>（<see cref="ISkill"/> 实现，装在 <see cref="SkillSlot.Dash"/> 槽，默认 Shift 键）：
/// 沿当前移动方向（没在移动时朝瞄准方向）瞬间提速，持续一段极短时间。
/// <para>
/// <b>为什么"冲刺"是一个技能而不是输入里的特殊分支</b>：它和射击走完全相同的管线 ——
/// 冷却、法力、失败原因由 <c>SkillCastSystem</c> 统一门控。于是"这个角色能冲多快、冲几次"
/// 全部是数据（<see cref="StatId.DashSpeedMul"/> / <see cref="StatId.DashDuration"/> / 技能行的冷却与法力），
/// 四个变体共享同一个技能实例，差异只在数值包（规则4/规则6）。
/// </para>
/// <para>
/// <b>为什么本类不直接改速度</b>：施放发生在查询遍历里，此刻改世界结构（加组件 = 原型迁移）
/// 会让 chunk 数组失效（J5）。所以本类只做"预检 + 排一条命令"，真正的挂载在帧末由
/// <c>Commands.Add</c> 落地，位移则由 <see cref="DashSystem"/> 在固定步长里推进。
/// 这条分工还有个好处：<c>Shift</c> 连按时，"已经在冲刺"会被预检拦下，不会叠加成无限速度。
/// </para>
/// <para>
/// <b>为什么方向在 Cast 里定格</b>：冲刺是"朝按下那一刻的方向一冲到底"。若每帧都读最新朝向，
/// 鼠标一抖冲刺就会拐弯，手感会变成"滑行"而不是"冲刺"。
/// </para>
/// </summary>
public sealed class DashSkill : ISkill
{
    /// <summary>技能 id（Excel 主键）。</summary>
    private readonly int _id;

    private readonly float _cooldownSeconds;
    private readonly float _manaCost;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="id">技能 id（&gt; 0；0 在契约里表示空技能）。</param>
    /// <param name="cooldownSeconds">冷却秒数（数据行原值）。</param>
    /// <param name="manaCost">法力消耗（数据行原值）。</param>
    /// <param name="slot">安装槽（默认 <see cref="SkillSlot.Dash"/>）。</param>
    public DashSkill(
        int id,
        float cooldownSeconds,
        float manaCost,
        SkillSlot slot = SkillSlot.Dash)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "冲刺技能的 id 必须 > 0（0 表示空技能）。");
        }
        _id = id;
        _cooldownSeconds = cooldownSeconds > 0f ? cooldownSeconds : 0f;
        _manaCost = manaCost > 0f ? manaCost : 0f;
        DefaultSlot = slot;
    }

    /// <summary>
    /// 数据没配倍率时的回退值 = 1（按普通移速冲）。
    /// <para>为什么这是"数学恒等"而不是玩法数值：1 表示"不加速"，它不定义手感，
    /// 只定义"缺配置时不要造出一个 0 速度的静止冲刺"。真实倍率由 <see cref="StatId.DashSpeedMul"/> 给。</para>
    /// </summary>
    public const float DefaultSpeedMul = 1f; // arch-ok: 乘法恒等元（缺配置时的安全回退），不是玩法数值

    /// <inheritdoc />
    public int Id => _id;

    /// <inheritdoc />
    public string DefId => string.Empty;

    /// <inheritdoc />
    public SkillSlot DefaultSlot { get; }

    /// <inheritdoc />
    public float CooldownSeconds => _cooldownSeconds;

    /// <inheritdoc />
    public float ManaCost => _manaCost;

    /// <summary>最近一次 <see cref="Cast"/> 是否成功排出了冲刺（调试用）。</summary>
    public bool LastCastAccepted { get; private set; }

    /// <summary>
    /// 预检：冷却就绪 + 法力足够 + 角色配了冲刺时长 + <u>当前不在冲刺中</u>。
    /// <para>
    /// 最后一条是关键：连按 Shift 若每次都成功，冲刺会不断被重置（等于无限速度）。
    /// 把"正在冲刺"做成一次明确的失败，玩家看到的是一句提示，而不是一个跑得比谁都快的主角。
    /// </para>
    /// <para>本方法<b>无副作用</b>：只读数值包与该实体身上是否已有 <see cref="DashState"/>。</para>
    /// </summary>
    /// <summary>
    /// 预检：冷却就绪 + 法力足够 + 角色配了冲刺时长 + <u>当前不在冲刺中</u>。
    /// <para>
    /// 最后一条是关键：连按 Shift 若每次都成功，冲刺会不断被重置（等于无限速度）。
    /// 把"正在冲刺"做成一次明确的失败，玩家看到的是一句提示，而不是一个跑得比谁都快的主角。
    /// </para>
    /// <para>本方法<b>无副作用</b>：只读数值包与该实体身上是否已有 <see cref="DashState"/>。</para>
    /// </summary>
    public bool CanCast(in SkillContext ctx)
    {
        if (!ctx.IsOffCooldown || !ctx.HasMana(_manaCost) || DurationOf(ctx) <= 0f)
        {
            return false;
        }

        // 没有世界句柄 = 测试桩没注入，此时"已经在冲"无从判断，按"没在冲"放行（由 Cast 兜底失败）。
        if (ctx.World?.Ecs is not World ecs)
        {
            return false;
        }

        // 身上已有 DashState = 正在冲刺：拒绝重入（否则会叠加组件、速度翻倍）。
        return !ecs.TryGet<DashState>(ctx.CasterEntity, out _);
    }

    /// <summary>
    /// 排出一条"给施放者挂 <see cref="DashState"/>"的命令。
    /// <para>真正的组件挂载在帧末（<c>Commands.Flush</c>），因此本方法返回后到下一帧之间，
    /// 施放者身上还看不到 <see cref="DashState"/> —— 这正是 J5 要求的顺序。</para>
    /// <para><b>为什么开头要自己再问一次 <see cref="CanCast"/></b>：契约规定由 <c>SkillCastSystem</c>
    /// 先预检再施放，但 <c>Cast</c> 是公开 API（测试、AI、将来的连招系统都会直接调它）。
    /// 一旦有人绕过门控，"冲刺中再冲"就会真的把 <see cref="DashState"/> 叠加一层 ——
    /// 速度翻倍、而且 <c>Remaining</c> 被重置成满值，等于无限速度。<b>门控必须由能力自己兜底</b>，
    /// 不能依赖调用方守规矩。</para>
    /// </summary>
    public SkillCastResult Cast(in SkillContext ctx)
    {
        LastCastAccepted = false;

        if (!CanCast(in ctx))
        {
            // 分清"为什么被拦"，让 HUD/音效/测试都能拿到准确原因。
            if (!ctx.IsOffCooldown)
            {
                return SkillCastResult.Fail(SkillCastFailure.Cooldown);
            }
            if (!ctx.HasMana(_manaCost))
            {
                return SkillCastResult.Fail(SkillCastFailure.NoMana);
            }
            return SkillCastResult.Fail(SkillCastFailure.TargetInvalid);
        }

        World? world = ctx.World.Ecs;
        float duration = DurationOf(ctx);
        if (world is null || duration <= 0f)
        {
            // 数据没配 DashDuration：明确失败，而不是冲一帧就停（那看起来像 bug）。
            return SkillCastResult.Fail(SkillCastFailure.TargetInvalid);
        }

        Vector3 direction = DashDirectionOf(ctx);
        float baseSpeed = BaseMoveSpeedOf(ctx, world);
        float speedMul = SpeedMulOf(ctx);

        var dash = new DashState
        {
            Remaining = duration,
            Duration = duration,
            Direction = direction,
            Speed = baseSpeed * speedMul,
        };

        ctx.World.Commands.Add(ctx.CasterEntity, dash);
        LastCastAccepted = true;
        return SkillCastResult.Ok();
    }

    /// <summary>
    /// 冲刺时长（秒）：读施放者数值包的 <see cref="StatId.DashDuration"/>；未配置返回 0（= 不能冲）。
    /// <para>为什么在 <b>施放时</b>读而不是构造时注入：技能实例被整个技能包共享，
    /// 而"冲多久"属于<u>角色</u>的数值。同一个 <c>DashSkill</c> 实例装在敏捷型身上冲得更久，
    /// 换到重甲型身上立刻变短 —— 这正是用户要的"拼接变体"。</para>
    /// </summary>
    private static float DurationOf(in SkillContext ctx) => StatOf(ctx, StatId.DashDuration);

    /// <summary>冲刺速度倍率：读 <see cref="StatId.DashSpeedMul"/>；未配置回退 <see cref="DefaultSpeedMul"/>。</summary>
    private static float SpeedMulOf(in SkillContext ctx)
    {
        float mul = StatOf(ctx, StatId.DashSpeedMul);
        return mul > 0f ? mul : DefaultSpeedMul;
    }

    /// <summary>按 <see cref="StatId"/> 取施放者的最终属性；取不到（没有属性视图/未声明）返回 0。</summary>
    private static float StatOf(in SkillContext ctx, StatId id)
    {
        IReadOnlyStatView? stats = ctx.World.StatsOf(ctx.CasterActorId);
        return stats is null ? 0f : stats.Get(id);
    }

    /// <inheritdoc />
    public void Tick(float deltaTime, in SkillContext ctx)
    {
        // 冲刺的推进由 DashSystem 在固定步长里做（它要改 Velocity，技能拿的是只读句柄）。
        _ = deltaTime;
        _ = ctx;
    }

    /// <summary>
    /// 冲刺方向：优先"当前移动意图"，没有再退到"瞄准方向"。
    /// <para>
    /// 为什么移动优先：站着不动时按 Shift 想的是"朝准心闪一下"，跑着按 Shift 想的是"顺着跑的方向加速"。
    /// 两种情况都符合直觉，靠的正是这个优先级；反过来（瞄准优先）会让边跑边冲变成"横着闪"，很别扭。
    /// </para>
    /// </summary>
    private static Vector3 DashDirectionOf(in SkillContext ctx)
    {
        World? world = ctx.World.Ecs;
        if (world is not null && world.TryGet<MoveIntent>(ctx.CasterEntity, out MoveIntent move))
        {
            Vector3 heading = Flatten(move.Velocity);
            if (heading.LengthSquared() > float.Epsilon)
            {
                return heading / MathF.Sqrt(heading.LengthSquared());
            }
        }

        Vector3 aim = Flatten(ctx.AimDirection);
        return aim.LengthSquared() > float.Epsilon
            ? aim / MathF.Sqrt(aim.LengthSquared())
            : Vector3.UnitZ;
    }

    /// <summary>
    /// 冲刺的基础速度：优先读施放者的 <see cref="StatId.MoveSpeed"/>（角色现在能跑多快就冲多快），
    /// 读不到时退回移动意图的长度，再退回 1。
    /// <para>为什么读属性而不读意图：意图是"这一帧想怎么走"（可能为 0 = 站着），
    /// 而冲刺速度应当是这个角色的<u>能力</u>，与这一帧是否在按 WASD 无关。</para>
    /// </summary>
    private static float BaseMoveSpeedOf(in SkillContext ctx, World world)
    {
        float speed = StatOf(ctx, StatId.MoveSpeed);
        if (speed > 0f)
        {
            return speed;
        }

        // 拿不到属性视图时退回移动意图的长度（至少别造出一个 0 速度的冲刺）。
        if (world.TryGet<MoveIntent>(ctx.CasterEntity, out MoveIntent move))
        {
            float len = Flatten(move.Velocity).Length();
            if (len > 0f)
            {
                return len;
            }
        }

        return 1f; // arch-ok: 兜底单位速度（乘法恒等元），真实速度来自 StatId.MoveSpeed —— 不是玩法数值
    }

    /// <summary>压平到水平面（冲刺不爬升：俯视角射击里 Y 分量会让角色飘起来）。</summary>
    private static Vector3 Flatten(Vector3 v) => new(v.X, 0f, v.Z);
}
