using System;
using System.Collections.Generic;

namespace Game.Core.Skills;

/// <summary>
/// 技能包（**组合思想的落点之一**）：5 个固定槽位 + 每槽一份冷却状态。
/// <para>
/// <b>为什么技能是"包"而不是"角色身上的一个方法"</b>
/// </para>
/// <list type="bullet">
/// <item>组合：同一套冲刺/射击可以被玩家、Boss、召唤物复用（规则7：新角色 = Excel 一行 + 组合现有技能）；</item>
/// <item>替换：夺舍 = 整体换包（规则6「可互相赋值」在技能维度的对应物）；</item>
/// <item>门控：冷却状态是<u>每个实体独立的</u>，所以"包"必须是值 + 一份不该共享的冷却表。</item>
/// </list>
/// <para>
/// <b>关于线程/分配</b>：技能包在生成实体时构造一次、之后只在换包时整体替换；
/// 每帧只读 <see cref="Get"/>，冷却写成定长数组（<see cref="SkillSlotExtensions.TableLength"/>），不产生分配。
/// </para>
/// </summary>
public sealed class SkillLoadout
{
    private static readonly ISkill?[] EmptyTable = new ISkill?[SkillSlotExtensions.TableLength];

    private readonly ISkill?[] _slots;
    private readonly float[] _cooldownRemaining;

    /// <summary>包 id（Excel 主键，调试与存档用）。</summary>
    public string Id { get; }

    /// <summary>该包含多少个非空技能。</summary>
    public int SkillCount { get; }

    /// <summary>构造一个空技能包。</summary>
    public SkillLoadout(string id) : this(id, EmptyTable, Array.Empty<ISkill>())
    {
    }

    private SkillLoadout(string id, ISkill?[] slots, IReadOnlyList<ISkill> entries)
    {
        Id = id ?? string.Empty;
        _slots = slots;
        _cooldownRemaining = new float[SkillSlotExtensions.TableLength];
        SkillCount = entries.Count;
        Entries = entries;
    }

    /// <summary>包内全部技能（只读；HUD 与技能列表展示用）。</summary>
    public IReadOnlyList<ISkill> Entries { get; }

    /// <summary>按槽取技能；空槽返回 <c>null</c>。</summary>
    public ISkill? Get(SkillSlot slot) => IsValidSlot(slot) ? _slots[(int)slot] : null;

    /// <summary>按技能 id 找它所在的槽；找不到返回 <see cref="SkillSlot.None"/>。</summary>
    public SkillSlot SlotOf(int skillId)
    {
        if (skillId == 0)
        {
            return SkillSlot.None;
        }
        for (int i = 1; i < _slots.Length; i++)
        {
            ISkill? skill = _slots[i];
            if (skill is not null && skill.Id == skillId)
            {
                return (SkillSlot)i;
            }
        }
        return SkillSlot.None;
    }

    /// <summary>冷却剩余（秒）；空槽返回 0。</summary>
    public float CooldownRemaining(SkillSlot slot) => IsValidSlot(slot) ? _cooldownRemaining[(int)slot] : 0f;

    /// <summary>冷却进度 0..1（1 = 按满），HUD 画扇形用。</summary>
    public float CooldownProgress(SkillSlot slot)
    {
        ISkill? skill = Get(slot);
        if (skill is null || skill.CooldownSeconds <= 0f)
        {
            return 0f;
        }
        float remaining = _cooldownRemaining[(int)slot];
        if (remaining <= 0f)
        {
            return 0f;
        }
        float ratio = remaining / skill.CooldownSeconds;
        return ratio > 1f ? 1f : ratio;
    }

    /// <summary>该槽是否就绪（有技能且冷却为 0）。</summary>
    public bool IsReady(SkillSlot slot) => Get(slot) is not null && _cooldownRemaining[(int)slot] <= 0f;

    /// <summary>每帧递减所有槽的冷却（由 <c>SkillCooldownSystem</c> 调用）。</summary>
    public void TickCooldowns(float deltaTime)
    {
        if (deltaTime <= 0f)
        {
            return;
        }
        for (int i = 1; i < _cooldownRemaining.Length; i++)
        {
            float remaining = _cooldownRemaining[i];
            if (remaining > 0f)
            {
                remaining -= deltaTime;
                _cooldownRemaining[i] = remaining > 0f ? remaining : 0f;
            }
        }
    }

    /// <summary>让某槽进入冷却（施放成功后调用；时长来自技能数据，不在这里写死）。</summary>
    public void StartCooldown(SkillSlot slot)
    {
        ISkill? skill = Get(slot);
        if (skill is null)
        {
            return;
        }
        float cd = skill.CooldownSeconds;
        _cooldownRemaining[(int)slot] = cd > 0f ? cd : 0f;
    }

    /// <summary>清空所有冷却（重生、换包、调试用）。</summary>
    public void ClearCooldowns() => Array.Clear(_cooldownRemaining, 0, _cooldownRemaining.Length);

    private static bool IsValidSlot(SkillSlot slot) => (int)slot > 0 && (int)slot < SkillSlotExtensions.TableLength;

    /// <summary>
    /// 由技能清单构造一个包：每个技能装进它的 <see cref="ISkill.DefaultSlot"/>；同槽冲突时后者覆盖并在开发期可断言。
    /// <para>返回新实例（值语义：包一经构造便不再修改槽内容，只有冷却在变）。</para>
    /// </summary>
    public static SkillLoadout FromSkills(string id, IReadOnlyList<ISkill> skills)
    {
        if (skills is null)
        {
            throw new ArgumentNullException(nameof(skills));
        }
        var table = new ISkill?[SkillSlotExtensions.TableLength];
        for (int i = 0; i < skills.Count; i++)
        {
            ISkill skill = skills[i];
            int slotIndex = (int)skill.DefaultSlot;
            if (slotIndex > 0 && slotIndex < table.Length)
            {
                table[slotIndex] = skill;
            }
        }
        return new SkillLoadout(id, table, skills);
    }

    /// <summary>把某个技能重定向到另一个槽（装备/天赋改键），返回新包并丢弃旧冷却。</summary>
    public SkillLoadout WithSlotOverride(SkillSlot from, SkillSlot to)
    {
        ISkill? skill = Get(from);
        if (skill is null || !IsValidSlot(to))
        {
            return this;
        }
        var table = (ISkill?[])_slots.Clone();
        table[(int)from] = null;
        table[(int)to] = skill;
        return new SkillLoadout(Id, table, Entries);
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("Loadout(", Id, ", skills=", SkillCount.ToString(), ")");
}

/// <summary>
/// 输入意图（**一帧的快照**，纯值类型）。输入系统把它写进组件，技能系统读它决定这一帧要不要施放。
/// <para>
/// 为什么意图里只有「按下/保持/瞄准」这种抽象动词，没有一个具体的键名：
/// 键位在 <c>InputBindings</c> 里（可改、可本地化、可换手柄），意图是玩法的稳定词汇。
/// 这样「Shift 冲刺」可以改成「空格冲刺」「手柄 B 键冲刺」，而系统代码 0 行改动。
/// </para>
/// </summary>
public readonly struct InputIntent : IEquatable<InputIntent>
{
    /// <summary>本次 input 帧号的位掩码：槽位 i 的"本帧按下"= bit (i-1)。</summary>
    public readonly uint PressedMask;

    /// <summary>保持按住（冲刺/持续射击）的位掩码，语义同 <see cref="PressedMask"/>。</summary>
    public readonly uint HeldMask;

    /// <summary>瞄准方向（单位向量，世界空间）。零向量表示"未瞄准"。</summary>
    public readonly int AimDirectionX;
    public readonly int AimDirectionY;
    public readonly int AimDirectionZ;

    /// <summary>构造意图。</summary>
    public InputIntent(uint pressedMask, uint heldMask, int aimX, int aimY, int aimZ)
    {
        PressedMask = pressedMask;
        HeldMask = heldMask;
        AimDirectionX = aimX;
        AimDirectionY = aimY;
        AimDirectionZ = aimZ;
    }

    /// <summary>该槽本帧是否被按下（边沿触发）。</summary>
    public bool WasPressed(SkillSlot slot) => slot != SkillSlot.None && (PressedMask & MaskOf(slot)) != 0;

    /// <summary>该槽当前是否被按住（电平触发）。</summary>
    public bool IsHeld(SkillSlot slot) => slot != SkillSlot.None && (HeldMask & MaskOf(slot)) != 0;

    /// <summary>槽位 → 位掩码。</summary>
    public static uint MaskOf(SkillSlot slot) => 1u << ((int)slot - 1);

    /// <inheritdoc />
    public bool Equals(InputIntent other) =>
        PressedMask == other.PressedMask && HeldMask == other.HeldMask
        && AimDirectionX == other.AimDirectionX
        && AimDirectionY == other.AimDirectionY
        && AimDirectionZ == other.AimDirectionZ;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InputIntent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(PressedMask, HeldMask, AimDirectionX, AimDirectionY, AimDirectionZ);
}

/// <summary>
/// 意图来源：把"外部世界"翻译成 <see cref="InputIntent"/>。
/// <para>
/// <b>为什么抽象成一个接口而不是直接读 <c>Input</c></b>：玩家读真实输入，AI 读行为树，脚本读过场轨道 ——
/// 三者对技能系统是同一件事。主角与怪物因此走<u>同一条</u>技能管线（怪物和角色同源），
/// 差别只在装配了哪个实现。宿主层每帧把 <see cref="Poll"/> 的结果写进组件（J4：Core 不碰引擎）。
/// </para>
/// </summary>
public interface IIntentSource
{
    /// <summary>采集本帧意图。<paramref name="actorId"/> 是意图所属实体（同一实现可服务多个实体）。</summary>
    InputIntent Poll(int actorId);
}
