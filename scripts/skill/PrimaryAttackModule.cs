using System;
using System.Collections.Generic;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Skill;

/// <summary>
/// 「主攻击模块」（<c>fire_primary</c>）：把射击技能装进技能包，让"一键射击"这件事可以从装配清单里被勾选
/// （契约 §4：模块是拼接的核心）。
/// <para>
/// <b>为什么需要这样一个"非系统"的模块对象</b>
/// </para>
/// <list type="bullet">
/// <item>技能不属于 <c>SystemBase</c>：它由 <c>SkillCastSystem</c> 在槽位上调用，装配发生在<u>技能包</u>这一层，
///       而不是"一帧跑哪些系统"这一层。所以模块机制支持"只有模块名、没有系统"的成员
///       （<see cref="SystemPipeline.AddModule"/> 对空系统数组不报错，正是为此）；</item>
/// <item>"射击由哪个技能实现、装哪个数据技能 id"是<u>内容</u>（换武器/换变体要换），所以它必须是
///       数据侧可配置的一行，而不是硬编码在宿主里的 <c>new FireProjectileSkill(...)</c>；</item>
/// <item>本类把"技能包 id → 技能实例"的装配收在一处，宿主只需要 <c>Install(registry)</c> 一句，
///       将来换一个"散射射击"实现只改这一个文件（规则7）。</item>
/// </list>
/// <para>
/// <b>为什么是 <c>SkillLoadoutRegistry</c> 而不是改组件</b>：技能包本体是托管对象（<see cref="SkillLoadout"/>），
/// 组件里只存它的槽位号（J2）。装配期往注册表里登记一个<u>新</u>技能包（而不是改已有的包），
/// 是为了让"同一套基础技能包 + 不同武器"的变体各自拿到独立的冷却表 —— 冷却必须是<u>每个实体独立</u>的，
/// 共享同一个包会让 A 开火让 B 进入冷却（一个只在多角色同屏时才出现的 bug）。
/// </para>
/// </summary>
[Module("fire_primary")]
public sealed class PrimaryAttackModule
{
    private readonly string _projectileId;
    private readonly int _skillId;
    private readonly float _cooldownSeconds;
    private readonly float _manaCost;

    /// <summary>构造。</summary>
    /// <param name="skillId">技能 id（Excel <c>Skills</c> 主键；&gt; 0）。</param>
    /// <param name="projectileId">抛射物定义 id（数据侧）。</param>
    /// <param name="cooldownSeconds">两发最小间隔（来自武器数据行）。</param>
    /// <param name="manaCost">每次开火的法力消耗。</param>
    public PrimaryAttackModule(int skillId, string projectileId, float cooldownSeconds, float manaCost = 0f)
    {
        _skillId = skillId;
        _projectileId = projectileId ?? string.Empty;
        _cooldownSeconds = cooldownSeconds;
        _manaCost = manaCost;
    }

    /// <summary>本模块的模块 id（与 <see cref="ModuleAttribute"/> 上写的一致，供宿主拼装配清单）。</summary>
    public const string ModuleId = "fire_primary";

    /// <summary>新建一个射击技能实例（每个调用方拿到独立实例：技能对象里带状态，不该共享）。</summary>
    public FireProjectileSkill CreateSkill() => new FireProjectileSkill(_skillId, _projectileId, _cooldownSeconds, _manaCost);

    /// <summary>
    /// 把"基础技能包 + 本模块的射击技能"登记成一个新技能包，返回其槽位号。
    /// <para>
    /// 幂等：同一个 <paramref name="loadoutId"/> 已存在时直接返回它的槽位号，不重复登记
    /// （重复登记会抛异常，而装配清单可能被多个变体共用）。
    /// </para>
    /// </summary>
    /// <param name="loadouts">技能包仓库（宿主/数据层持有）。</param>
    /// <param name="loadoutId">新技能包的 id（例如 <c>test_hero_kit</c>）。</param>
    /// <param name="baseLoadout">基础技能池：只取其中除 PrimaryAttack 以外的槽（避免把旧的射击技能带进新包）。</param>
    public int Install(SkillLoadoutRegistry loadouts, string loadoutId, SkillLoadout? baseLoadout)
    {
        if (loadouts is null)
        {
            throw new ArgumentNullException(nameof(loadouts));
        }
        if (string.IsNullOrEmpty(loadoutId))
        {
            throw new ArgumentException("技能包 id 不能为空：它是数据侧的引用键。", nameof(loadoutId));
        }

        int existing = loadouts.SlotOf(loadoutId);
        if (existing >= 0)
        {
            return existing;
        }

        var skills = new List<ISkill>(SkillSlotExtensions.TableLength);
        if (baseLoadout is not null)
        {
            IReadOnlyList<ISkill> entries = baseLoadout.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                ISkill skill = entries[i];
                if (skill.DefaultSlot == SkillSlot.PrimaryAttack)
                {
                    continue;
                }
                skills.Add(skill);
            }
        }
        skills.Add(CreateSkill());

        return loadouts.Register(SkillLoadout.FromSkills(loadoutId, skills));
    }
}
