using System;
using Game.Core;
using Game.Core.Skills;

namespace Game.Host.Composition;

/// <summary>
/// 技能工厂（组合根接缝）：把数据层的**纯描述行** <c>Game.Data.CharacterSkillSpec</c> 变成可施放的 <see cref="ISkill"/> 实例。
/// <para>
/// <b>为什么要有这层</b>：数据层（<c>scripts/data/</c>）是「被依赖方」，不允许 <c>using Game.Skill</c>
/// （那会让它反向依赖业务层，分层守卫 <c>DataSources_DoNotUseActorCombatSkill</c> 会红）。
/// 于是它只产出"这个槽位装哪个技能 id、是什么种类、冷却多少"这样的描述行；
/// 「描述行 → 具体类」的最后一步只有组合根能做 —— 组合根天然可以依赖一切。
/// </para>
/// <para>
/// <b>为什么接口的参数是泛型而不是 <c>CharacterSkillSpec</c></b>：接口住在宿主层、不该认识数据层的结构体类型，
/// 否则数据表字段一变这里就要跟着改。泛型约束只要求"能取到创建技能所需的五个值"，
/// 将来换成 JSON/DTO 直接适配即可（规则7：新增一种技能描述 = 0 个接口改动）。
/// </para>
/// <para>
/// <b>实现纪律</b>：工厂只做"查表 + new"，<u>不得</u>读取世界、不得写组件、不得施放技能。
/// 施放时机归 <c>SkillCastSystem</c>，工厂在装配期被调用一次。
/// </para>
/// </summary>
public interface ISkillFactory
{
    /// <summary>
    /// 把一条技能描述实例化成技能。实现必须保证：同一描述行产出的 <see cref="ISkill.Id"/> 与
    /// 数据行的技能 id 一致（HUD 与冷却表按 id 对齐），冷却/法力消耗**逐字段照抄数据**，不得改写。
    /// </summary>
    /// <param name="skillId">技能 id（= 数据行主键）。</param>
    /// <param name="kind">实现种类（数据行 <c>Kind</c>，如 <c>fire_projectile</c>）。</param>
    /// <param name="projectileId">抛射物定义 id（<u>不是</u>武器 id；命令门面据此选外观）。</param>
    /// <param name="cooldownSeconds">冷却秒数（数据行原值，不许在这里换算）。</param>
    /// <param name="manaCost">法力消耗（数据行原值）。</param>
    /// <param name="slot">安装到技能包的哪个槽（与数据行槽位一致）。</param>
    /// <returns>技能实例；种类未知时返回一个"空技能"（施放成功但不产生任何效果），并保证不抛异常 ——
    /// 一个配错的技能行不该让整局起不来。</returns>
    ISkill Create(
        int skillId,
        string kind,
        string projectileId,
        float cooldownSeconds,
        float manaCost,
        SkillSlot slot);
}

/// <summary>
/// 技能工厂的**空实现**（不造任何东西）。用于"没有技能来源"的宿主（只跑渲染/调试）与测试替身。
/// <para>存在的意义：宿主漏接工厂时行为是"技能槽空"，而不是空引用崩在施放那一刻。</para>
/// </summary>
public sealed class NullSkillFactory : ISkillFactory
{
    /// <summary>共享实例（无状态）。</summary>
    public static readonly NullSkillFactory Instance = new NullSkillFactory();

    /// <inheritdoc />
    public ISkill Create(
        int skillId,
        string kind,
        string projectileId,
        float cooldownSeconds,
        float manaCost,
        SkillSlot slot) =>
        new NullSkill(skillId, projectileId, cooldownSeconds, manaCost, slot);
}

/// <summary>
/// 什么都不做的技能：占满槽位（HUD 能显示"有这个技能、冷却多少"），施放恒成功但无效果。
/// <para>用于数据里配了技能但运行时还没实现该种类（例如 <c>data</c> 占位行）的情形。</para>
/// </summary>
public sealed class NullSkill : ISkill
{
    /// <summary>构造。</summary>
    /// <param name="id">技能 id。</param>
    /// <param name="projectileId">数据行的定义 id（空串 = 无独立配置）。</param>
    /// <param name="cooldownSeconds">冷却秒数。</param>
    /// <param name="manaCost">法力消耗。</param>
    /// <param name="slot">安装槽。</param>
    public NullSkill(int id, string projectileId, float cooldownSeconds, float manaCost, SkillSlot slot)
    {
        Id = id;
        DefId = projectileId ?? string.Empty;
        CooldownSeconds = cooldownSeconds > 0f ? cooldownSeconds : 0f;
        ManaCost = manaCost > 0f ? manaCost : 0f;
        DefaultSlot = slot;
    }

    /// <inheritdoc />
    public int Id { get; }

    /// <inheritdoc />
    public string DefId { get; }

    /// <inheritdoc />
    public float CooldownSeconds { get; }

    /// <inheritdoc />
    public float ManaCost { get; }

    /// <inheritdoc />
    public SkillSlot DefaultSlot { get; }

    /// <inheritdoc />
    public bool CanCast(in SkillContext context) => true;

    /// <inheritdoc />
    public SkillCastResult Cast(in SkillContext context) => SkillCastResult.Ok();

    /// <inheritdoc />
    public void Tick(float deltaTime, in SkillContext context)
    {
        // 空技能没有持续效果：冷却推进由 SkillCastSystem 统一负责，这里什么都不做。
    }
}
