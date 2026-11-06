using System;
using Game.Core;
using Game.Core.Skills;
using Game.Skill;

namespace Game.Host.Composition;

/// <summary>
/// 把数据行的 <c>Kind</c> 绑到具体的技能实现类（本工程里唯一的「技能种类」映射表）。
/// <para>
/// <b>为什么在宿主层</b>：数据层禁止 <c>using Game.Skill</c>（分层守卫），它只描述"这个槽位是什么种类"；
/// "种类 → 类"的绑定是组合根的职责 —— 它决定了"这一局里 <c>fire_projectile</c> 意味着什么"。
/// 新增一种技能 = 数据加一行 + 这里加一个 case —— 那次改动换来的是<u>一项新能力</u>，
/// 不是一条新配置，符合规则7 的判据。
/// </para>
/// <para>
/// <b>为什么单独成文件、且不碰 Godot</b>：它必须能被无引擎进程（<c>tests/</c>）编译，
/// 这样"数据行 → 真技能 → 真子弹"这条链才有一份<u>与宿主完全同源</u>的验证。
/// 本文件一旦出现 <c>using Godot</c>，测试工程会直接编译失败（硬守卫）。
/// </para>
/// </summary>
public sealed class ContentSkillFactory : ISkillFactory
{
    /// <summary>技能种类：发射一颗投射物（左键射击 / 部分独有技能）。</summary>
    public const string KindFireProjectile = "fire_projectile";

    /// <summary>
    /// 技能种类：冲刺（Shift）。参数（倍率/时长）不在技能行里，而是施放时从<u>角色数值包</u>读
    /// （<see cref="StatId.DashSpeedMul"/> / <see cref="StatId.DashDuration"/>），
    /// 所以同一个技能实例装在四个变体上会冲出四种手感 —— 这正是「拼接变体」要的效果。
    /// </summary>
    public const string KindDash = "dash";

    /// <inheritdoc />
    public ISkill Create(
        int skillId,
        string kind,
        string projectileId,
        float cooldownSeconds,
        float manaCost,
        SkillSlot slot)
    {
        if (string.Equals(kind, KindFireProjectile, StringComparison.OrdinalIgnoreCase))
        {
            return new FireProjectileSkill(skillId, projectileId, cooldownSeconds, manaCost, slot);
        }

        if (string.Equals(kind, KindDash, StringComparison.OrdinalIgnoreCase))
        {
            return new DashSkill(skillId, cooldownSeconds, manaCost, slot);
        }

        // 其余种类（"data" 占位、以及将来新增但运行时尚未实现的）走空技能：
        // 槽位照样占住、冷却照样倒计时，绝不因为一行数据配错就让整局起不来。
        // 调用方（ContentSkillSet）会把这些 id 记进 skipped，让"配了却没实现"在日志与测试里可见。
        return new NullSkill(skillId, projectileId, cooldownSeconds, manaCost, slot);
    }
}
