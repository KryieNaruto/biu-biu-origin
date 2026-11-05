// HUD 的数据契约（DTO）：只有值、没有行为、没有任何引擎类型。
// 契约真源：docs/design/character-hud-contract.md §2。字段名/类型改动 = 破坏性变更，必须先改契约并通知 Lead。
namespace Game.UI;

/// <summary>
/// 一条资源条（血 / 蓝 / 体力）的**只读**数值。
/// <para>为什么是 <c>readonly struct</c>：HUD 每帧从宿主拿一次快照，值语义让它能直接挂在
/// <see cref="HudSnapshot"/> 里而不产生任何堆分配（UI 每帧 new 托管对象 = 9.4 GC 预算违规）。</para>
/// </summary>
public readonly struct HudBar
{
    /// <summary>当前值（可为 0，可为负 —— 结算层偶尔会有溢出，展示层负责夹取）。</summary>
    public readonly float Value;

    /// <summary>上限（0 或负数表示「该资源不存在」，HUD 按满条处理，不画空条）。</summary>
    public readonly float Max;

    /// <summary>当前值 / 上限，夹取到 [0,1]；<see cref="Max"/> 非法时返回 1（满）。</summary>
    public float Ratio
    {
        get
        {
            if (!(Max > 0f))
            {
                return 1f;
            }

            var r = Value / Max;
            if (r < 0f)
            {
                return 0f;
            }

            return r > 1f ? 1f : r;
        }
    }

    /// <summary>构造一条资源条。</summary>
    public HudBar(float value, float max)
    {
        Value = value;
        Max = max;
    }
}

/// <summary>
/// 一个技能格的**只读**状态。HUD 只负责「画」，不认识技能是什么（槽位字形与按键文案都由宿主填）。
/// </summary>
public readonly struct HudSkillSlot
{
    /// <summary>该槽是否装了技能。false = 空槽（画暗灰占位）。</summary>
    public readonly bool HasSkill;

    /// <summary>是否就绪（false 且有技能 = 冷却中，画扇形遮罩）。</summary>
    public readonly bool Ready;

    /// <summary>冷却进度：0 = 就绪，1 = 刚放完。HUD 用它画扇形遮罩的面积。</summary>
    public readonly float CooldownProgress;

    /// <summary>
    /// 剩余冷却秒数（就绪时为 0）。HUD 把它画成技能格下缘的倒计时。
    /// <para>
    /// 为什么 DTO 里既有进度又有剩余秒数：进度画扇形（几何量，0..1），秒数画文字（玩家真正关心的量）。
    /// 只给进度就得让 UI 反推总时长，那是「UI 参与结算」；所以由宿主把两者一起算好交过来（红线 5）。
    /// </para>
    /// </summary>
    public readonly float RemainingSeconds;

    /// <summary>槽位字形（如 "&gt;&gt;" / "ATK" / "SWAP" / "SIG" / "INT"）。HUD 只画不解释。</summary>
    public readonly string? Glyph;

    /// <summary>角落按键标签（"LMB" / "LSHIFT" / "Q" / "R" / "F"）。空串 = 不画。</summary>
    public readonly string? KeyLabel;

    /// <summary>构造一个技能格状态（不含倒计时秒数；需要时用 6 参重载）。</summary>
    public HudSkillSlot(bool hasSkill, bool ready, float cooldownProgress, string? glyph, string? keyLabel)
        : this(hasSkill, ready, cooldownProgress, 0f, glyph, keyLabel)
    {
    }

    /// <summary>构造一个技能格状态（含倒计时秒数）。</summary>
    public HudSkillSlot(
        bool hasSkill,
        bool ready,
        float cooldownProgress,
        float remainingSeconds,
        string? glyph,
        string? keyLabel)
    {
        HasSkill = hasSkill;
        Ready = ready;
        CooldownProgress = cooldownProgress;
        RemainingSeconds = remainingSeconds > 0f ? remainingSeconds : 0f;
        Glyph = glyph;
        KeyLabel = keyLabel;
    }
}

/// <summary>
/// 一帧 HUD 需要的全部数据（宿主每帧填一次，HUD 每帧只读一次）。
/// <para>
/// 设计纪律（规则 1 / 红线 5 / 规则 4）：这是 UI 与逻辑层**唯一**的通道 —— HUD 不持有任何
/// 结算对象、不调任何 Set/Cast/Apply，所有「已经算好的值」（如 <see cref="ArmorReduction"/>、
/// <see cref="CooldownProgress"/>、<see cref="AimAngleDeg"/>）都由宿主算好再交过来。
/// </para>
/// <para>
/// 为什么是可变 <c>struct</c> 而不是 <c>record</c>：宿主每帧原地覆写同一个字段，零分配；
/// <c>record</c> 的 <c>with</c> 每次都会复制一份，反而更贵。
/// </para>
/// </summary>
public struct HudSnapshot
{
    /// <summary>本帧是否有一个可显示的角色。false = HUD 整体不画（读档中 / 切场景）。</summary>
    public bool HasCharacter;

    /// <summary>角色名（例："测试人物 · 标准型"）。空串 = 不画。</summary>
    public string? CharacterName;

    /// <summary>变体标签（例："变体 · 重甲"）。空串 = 不画。</summary>
    public string? VariantTag;

    /// <summary>生命值。</summary>
    public HudBar Health;

    /// <summary>法力值。</summary>
    public HudBar Mana;

    /// <summary>体力值。</summary>
    public HudBar Stamina;

    /// <summary>护甲值（展示原值）。</summary>
    public float Armor;

    /// <summary>护甲减伤比例 0..1（**已算好**，HUD 不参与公式）。</summary>
    public float ArmorReduction;

    /// <summary>移动速度（米/秒）。</summary>
    public float MoveSpeed;

    /// <summary>暴击率 0..1。</summary>
    public float CritChance;

    /// <summary>暴击倍率（≥1，常数倍，例 2 = 200%）。</summary>
    public float CritMultiplier;

    /// <summary>等级。</summary>
    public int Level;

    /// <summary>
    /// 5 个技能格，索引 = <c>(int)SkillSlot - 1</c>（槽位 1..5）。
    /// 由宿主调 <see cref="EnsureSkills"/> 建一次，之后原地覆写，不再重分配。
    /// </summary>
    public HudSkillSlot[]? Skills;

    /// <summary>瞄准朝向（度，0 = 屏幕右，顺时针为正）。准星用它画朝向指示。</summary>
    public float AimAngleDeg;

    /// <summary>本帧时长（秒）。HUD 用它推进「掉血闪色 / 低血脉动」等纯表现计时。</summary>
    public float FrameSeconds;

    /// <summary>技能格数量（结构性常量：五固定槽，见 <c>Game.Core.SkillSlot</c>）。</summary>
    public const int SkillSlotCount = 5;

    /// <summary>
    /// 宿主调用：<see cref="Skills"/> 为 null 时新建长度 <see cref="SkillSlotCount"/> 的数组（**仅一次**），
    /// 并顺手把没装技能的槽标成空槽。已有数组时什么都不做，避免每帧分配。
    /// </summary>
    public void EnsureSkills()
    {
        if (Skills != null && Skills.Length == SkillSlotCount)
        {
            return;
        }

        Skills = new HudSkillSlot[SkillSlotCount];
    }
}
