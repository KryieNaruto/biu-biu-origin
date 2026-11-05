using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;
using Game.Data;
using Game.Skill;

namespace Game.Host.Composition;

/// <summary>
/// 把内容集里的**技能行**实例化成真正可施放的技能对象，产出「技能包 id → <see cref="SkillLoadout"/>」。
/// <para>
/// <b>为什么需要这一步（这是"左键开不了枪"的根因）</b>：
/// 数据层在装配技能包时只能放一个 <c>DataSkill</c> 占位（它不允许 <c>using Game.Skill</c>，
/// 见分层守卫）。占位技能被施放时什么也不做 —— 于是左键按下、冷却照走、法力照扣，
/// 世界里却一颗子弹都没有。测试 <c>EndToEnd_PressingPrimarySpawnsRealProjectile</c>
/// 从第一天起就是红的，等的就是这里：**把数据行翻译成真技能**。
/// </para>
/// <para>
/// <b>为什么不改数据层</b>：那会让 <c>Game.Data</c> 反向依赖 <c>Game.Skill</c>（layers 由 tests 钉住），
/// 而且"技能种类 → 实现类"的绑定天然属于组合根：它决定了这一局游戏里 `fire_projectile` 意味着什么。
/// </para>
/// <para>
/// <b>扩展方式（规则7）</b>：新增一种技能种类 = 在 <see cref="ContentSkillFactory.Create"/> 加一个 case。
/// 新增一个技能包 / 一个变体 = 纯数据行，本类 0 行改动。
/// </para>
/// </summary>
public sealed class ContentSkillSet
{
    private readonly Dictionary<string, SkillLoadout> _loadouts = new Dictionary<string, SkillLoadout>(StringComparer.Ordinal);

    /// <summary>已实例化的技能包数量。</summary>
    public int Count => _loadouts.Count;

    /// <summary>宿主的技能工厂（默认 <see cref="ContentSkillFactory"/>；测试可注入探针）。</summary>
    public ISkillFactory Factory { get; }

    /// <summary>构造。</summary>
    /// <param name="factory">技能工厂；null → 默认工厂。</param>
    public ContentSkillSet(ISkillFactory? factory = null) => Factory = factory ?? new ContentSkillFactory();

    /// <summary>
    /// 按「技能包 id + 槽位表」建一个真技能包。
    /// </summary>
    /// <param name="loadoutId">技能包 id（与 Excel <c>SkillLoads.Loadout</c> 一致）。</param>
    /// <param name="rows">该技能包的 4 格槽位行（<see cref="CharacterSkillSpecs.SlotCount"/>）。</param>
    /// <param name="skipped">被跳过的技能 id（槽位非法 / 种类未知），用于日志与断言。</param>
    /// <returns>真技能包；没有任何可用技能时返回 <c>null</c>。</returns>
    public SkillLoadout? Build(string loadoutId, IReadOnlyList<CharacterSkillSpec> rows, out List<int> skipped)
    {
        if (string.IsNullOrEmpty(loadoutId))
        {
            throw new ArgumentException("技能包 id 不能为空。", nameof(loadoutId));
        }
        if (rows is null)
        {
            throw new ArgumentNullException(nameof(rows));
        }

        skipped = new List<int>();
        var skills = new List<ISkill>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            CharacterSkillSpec row = rows[i];
            if (!row.TryGetSlot(out SkillSlot slot) || slot == SkillSlot.None)
            {
                // 槽位号非法是数据错误，但**不阻止整局启动**：跳过并记录，由测试/日志兜住。
                skipped.Add(row.SkillId);
                continue;
            }

            ISkill skill = Factory.Create(
                row.SkillId,
                row.Kind,
                row.ProjectileId,
                row.CooldownSeconds,
                row.ManaCost,
                slot);
            if (skill is NullSkill)
            {
                // 未知种类：照样占住槽位（冷却/法力语义完整），但要能被看见。
                skipped.Add(row.SkillId);
            }
            skills.Add(skill);
        }

        if (skills.Count == 0)
        {
            return null;
        }

        SkillLoadout loadout = SkillLoadout.FromSkills(loadoutId, skills);
        _loadouts[loadoutId] = loadout;
        return loadout;
    }

    /// <summary>已实例化的技能包；不存在返回 <c>null</c>。</summary>
    public SkillLoadout? Find(string loadoutId) =>
        !string.IsNullOrEmpty(loadoutId) && _loadouts.TryGetValue(loadoutId, out SkillLoadout? loadout) ? loadout : null;

    /// <summary>把所有技能包登记进运行时仓库（同一 id 已存在时覆盖 —— 宿主以自己实例化的为准）。</summary>
    /// <param name="registry">运行时技能包仓库（通常是 <c>CharacterContentSet.Loadouts</c>）。</param>
    public void PublishTo(SkillLoadoutRegistry registry)
    {
        if (registry is null)
        {
            throw new ArgumentNullException(nameof(registry));
        }
        // 仓库对重复 id 是抛异常的。这里先按 id 查一次再决定：已存在说明内容集与宿主实例化的是
        // 同一个技能包，以宿主版本为准（它才带真技能实现）。Replace 缺省时退回"跳过并记录"。
        foreach (KeyValuePair<string, SkillLoadout> pair in _loadouts)
        {
            if (registry.SlotOf(pair.Key) == SkillLoadoutRegistry.NoneSlot)
            {
                registry.Register(pair.Value);
            }
            else
            {
                registry.Replace(pair.Key, pair.Value);
            }
        }
    }
}
