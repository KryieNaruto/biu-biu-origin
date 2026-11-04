using System;
using System.Collections.Generic;
using Game.Core.Skills;

namespace Game.Core;

/// <summary>
/// 技能运行时（**纯数据契约**，docs/rules/03 §4.1 仲裁 #3-2：声明在底层、实现在 <c>scripts/skill/</c>）。
/// <para>
/// 一个技能 = 一组<u>数据</u>（id / 占哪个槽 / 冷却 / 消耗）+ 一个<u>执行策略</u>。
/// 召唤、射击、冲刺、切换武器、交互，在系统眼里都是同一个东西 —— 于是
/// 「新增一个技能」= 新增一个 <see cref="ISkill"/> 实现 + Excel 一行（规则7）。
/// </para>
/// <para>
/// <b>纪律</b>：实现类不得持有场景节点、不得直接改世界结构（结构化变更走 <see cref="ISkillContext.Commands"/>）。
/// <see cref="Tick"/> 只做倒计时与持续效果推进，不做事后结算（结算属于 Combat 层）。
/// </para>
/// </summary>
public interface ISkill
{
    /// <summary>技能 id（= Excel <c>Skills</c> 表主键；0 表示空技能）。</summary>
    int Id { get; }

    /// <summary>数据定义 id（Excel 行的字符串主键，空串 = 无独立配置）。</summary>
    string DefId { get; }

    /// <summary>该技能默认占据的槽位（技能包按它装填；可在包内被重定向）。</summary>
    SkillSlot DefaultSlot { get; }

    /// <summary>冷却时长（秒）。0 = 无冷却。</summary>
    float CooldownSeconds { get; }

    /// <summary>法力消耗；不足则 <see cref="CanCast"/> 返回 false。</summary>
    float ManaCost { get; }

    /// <summary>预检（先问后放）：冷却是否就绪、资源是否足够、是否满足目标条件。不得有副作用。</summary>
    bool CanCast(in SkillContext ctx);

    /// <summary>执行一次。返回 <see cref="SkillCastResult"/> 表示是否成功、是否触发冷却、失败原因。</summary>
    SkillCastResult Cast(in SkillContext ctx);

    /// <summary>每帧推进（冷却由 <c>SkillCooldownSystem</c> 统一管理，这里只处理技能自身的持续效果）。</summary>
    void Tick(float deltaTime, in SkillContext ctx);
}

/// <summary>
/// 一次施放的上下文：谁放、朝哪、什么时候。
/// <para>
/// 为什么所有字段都是 <c>readonly</c>/值类型：技能会在 Job 线程之外的任意系统里被调用，
/// 上下文必须是**快照**（本帧意图），不能持有可被后续系统改写的引用。
/// </para>
/// </summary>
public readonly struct SkillContext
{
    /// <summary>施放者（实体 id）。</summary>
    public readonly int CasterActorId;

    /// <summary>施放者阵营（免伤判定用）。</summary>
    public readonly TeamId CasterTeam;

    /// <summary>施放者类型（角色独有技能可借此分支；但不应用于「玩家/怪物」的分流）。</summary>
    public readonly ActorKind CasterKind;

    /// <summary>施放时占用的槽位。</summary>
    public readonly SkillSlot Slot;

    /// <summary>瞄准方向（单位向量，世界空间）。</summary>
    public readonly System.Numerics.Vector3 AimDirection;

    /// <summary>瞄准点（世界空间）。</summary>
    public readonly System.Numerics.Vector3 AimPoint;

    /// <summary>施放者位置（世界空间，冗余以求零查表）。</summary>
    public readonly System.Numerics.Vector3 Origin;

    /// <summary>该技能的冷却剩余（秒，由冷却系统镜像进来；0 = 就绪）。</summary>
    public readonly float CooldownRemaining;

    /// <summary>施放者法力当前值（由数值系统镜像）。</summary>
    public readonly float ManaCurrent;

    /// <summary>承接施放副作用的上下文（命令 / 随机 / 事件）。</summary>
    public readonly ISkillContext World;

    /// <summary>构造一个施放上下文。</summary>
    public SkillContext(
        int casterActorId,
        TeamId casterTeam,
        ActorKind casterKind,
        SkillSlot slot,
        System.Numerics.Vector3 aimDirection,
        System.Numerics.Vector3 aimPoint,
        System.Numerics.Vector3 origin,
        float cooldownRemaining,
        float manaCurrent,
        ISkillContext world)
    {
        CasterActorId = casterActorId;
        CasterTeam = casterTeam;
        CasterKind = casterKind;
        Slot = slot;
        AimDirection = aimDirection;
        AimPoint = aimPoint;
        Origin = origin;
        CooldownRemaining = cooldownRemaining;
        ManaCurrent = manaCurrent;
        World = world;
    }

    /// <summary>是否冷却就绪。</summary>
    public bool IsOffCooldown => CooldownRemaining <= 0f;

    /// <summary>法力是否足够支付 <paramref name="cost"/>。</summary>
    public bool HasMana(float cost) => ManaCurrent >= cost;
}

/// <summary>
/// 技能执行时能用到的世界能力（**薄门面**，不是 <c>World</c> 本身）。
/// <para>
/// 为什么要这层门面：技能声明在 <c>Game.Core</c>，而 <c>World</c> 也在 <c>Game.Core.Ecs</c>，
/// 技术上可以直接暴露 <c>World</c>；但那样一来 <c>scripts/skill/</c> 的实现就能
/// 绕过命令缓冲直接 <c>Create/Destroy</c>（违反 J5）。门面把「技能能做的那几件事」<u>显式列出来</u>：
/// 写命令、取随机、发事件、读数值。新增一种技能不需要更大的权限。
/// </para>
/// </summary>
public interface ISkillContext
{
    /// <summary>结构化命令入口（生成子弹/特效/单位），帧末统一提交。</summary>
    ICommandSink Commands { get; }

    /// <summary>随机源（必须来自注入，保证同 seed 同结果）。</summary>
    IRandomProvider Random { get; }

    /// <summary>取实体最终属性（只读，值语义快照）；目标不存在时返回 <c>null</c>。</summary>
    Actors.IReadOnlyStatView? StatsOf(int actorId);

    /// <summary>推进一帧时间（供持续类技能自计时使用）。</summary>
    float DeltaTime { get; }
}

/// <summary>技能预检/施放的结果。</summary>
public readonly struct SkillCastResult
{
    /// <summary>是否成功施放。</summary>
    public readonly bool Success;

    /// <summary>是否进入冷却（有些技能成功但不冷却，例如切换武器）。</summary>
    public readonly bool StartCooldown;

    /// <summary>失败原因（调试与飘字用；成功时为 <see cref="SkillCastFailure.None"/>）。</summary>
    public readonly SkillCastFailure Failure;

    private SkillCastResult(bool success, bool startCooldown, SkillCastFailure failure)
    {
        Success = success;
        StartCooldown = startCooldown;
        Failure = failure;
    }

    /// <summary>成功（默认进入冷却）。</summary>
    public static SkillCastResult Ok() => new(true, true, SkillCastFailure.None);

    /// <summary>成功但不进冷却。</summary>
    public static SkillCastResult OkNoCooldown() => new(true, false, SkillCastFailure.None);

    /// <summary>失败。</summary>
    public static SkillCastResult Fail(SkillCastFailure reason) => new(false, false, reason);

    /// <inheritdoc />
    public override string ToString() => Success ? "Ok" : string.Concat("Fail(", Failure.ToString(), ")");
}

/// <summary>技能施放失败的原因（枚举而非字符串：可比较、可本地化、零分配）。</summary>
public enum SkillCastFailure : byte
{
    /// <summary>无失败。</summary>
    None = 0,

    /// <summary>该槽没有技能。</summary>
    NoSkill = 1,

    /// <summary>冷却中。</summary>
    Cooldown = 2,

    /// <summary>法力不足。</summary>
    NoMana = 3,

    /// <summary>目标条件不满足（无法瞄准/无交互对象）。</summary>
    TargetInvalid = 4,

    /// <summary>状态禁止（死亡、眩晕、夺舍中）。</summary>
    Forbidden = 5,
}
