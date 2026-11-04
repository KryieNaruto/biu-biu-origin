namespace Game.Core;

/// <summary>
/// 技能槽位（**输入绑定与技能包的接缝**）。需求里的「Shift 冲刺 / 左键瞄准射击 / Q 切换武器 / R 角色独有技能 / F 交互」
/// 在这里被抽象成 5 个**固定槽位**：键位是表现，槽位是数据。
/// <para>
/// <b>为什么要分槽位、而不是让每个技能自带一个按键字符串</b>：
/// </para>
/// <list type="bullet">
/// <item>键位可改（设置界面、手柄、夺舍后换一套技能）→ 绑定表把 <see cref="SkillSlot"/> 映射到物理输入，
/// 技能包只说「我占用哪个槽」，两边解耦；</item>
/// <item>夺舍 = 整体替换技能包，槽位不变 → 输入逻辑 0 行改动（规则7）；</item>
/// <item>HUD 显示冷却时按槽位画，不关心槽里装的是什么。</item>
/// </list>
/// <para>
/// <b>为什么 <see cref="None"/>=0 且顺序固定</b>：槽位是技能包的数组下标（<c>SkillSlotTable</c> 的定长表），
/// 顺序即存档兼容。只加不改、显式给值。
/// </para>
/// </summary>
public enum SkillSlot : byte
{
    /// <summary>空槽（该槽未装技能）。</summary>
    None = 0,

    /// <summary>位移/闪避（默认 Shift）。玩家角色的默认内容是「冲刺」。</summary>
    Dash = 1,

    /// <summary>主攻击（默认鼠标左键）。玩家角色的默认内容是「瞄准射击」。</summary>
    PrimaryAttack = 2,

    /// <summary>武器切换（默认 Q）。玩家角色的默认内容是「切换武器」。</summary>
    WeaponSwap = 3,

    /// <summary>角色独有技能（默认 R）。每套技能包只有一个；这是「角色类型」的玩法辨识点。</summary>
    Signature = 4,

    /// <summary>交互（默认 F）：开门、拾取、对话、机关。</summary>
    Interact = 5,

    // 预留 6..15：后续可加（副攻击 / 终极技能 / 召唤…）。新槽位只能从 16 起追加。
}

/// <summary><see cref="SkillSlot"/> 的常量与判定。</summary>
public static class SkillSlotExtensions
{
    /// <summary>可装技能的槽位数量（不含 <see cref="SkillSlot.None"/>）。技能包是定长表，长度 = 本值 + 1。</summary>
    public const int ConfigurableCount = 5;

    /// <summary>槽位表的最大下标 + 1（含 None，用于定长数组）。</summary>
    public const int TableLength = (int)SkillSlot.Interact + 1;

    /// <summary>是否是一个「可以装技能」的槽位。</summary>
    public static bool IsConfigurable(this SkillSlot slot) => slot != SkillSlot.None;
}
