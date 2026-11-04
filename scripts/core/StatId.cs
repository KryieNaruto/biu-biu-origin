namespace Game.Core;

/// <summary>
/// 属性 id 枚举的**号段划分**（docs/rules/05 §5.2）。
/// <para>为什么单独抽一个类而不是把号段写在注释里：号段本身是「契约的一部分」——
/// 新增属性时判断「落哪个号段」要能查、要能引用；写成 <c>const int</c> 后，
/// 数据生成器（Excel → .tres）与代码可用同一个数字，不会两处漂移。</para>
/// <para>这些是**结构性常量**（号段边界），不是游戏数值，不违反 GS005。</para>
/// </summary>
public static class StatIdRanges
{
    /// <summary>生存号段 [1..99]：生命、护甲、减伤、护盾。</summary>
    public const int SurvivalStart = 1;
    public const int SurvivalEnd = 99;

    /// <summary>移动号段 [100..199]：速度、加速度、冲刺。</summary>
    public const int MovementStart = 100;
    public const int MovementEnd = 199;

    /// <summary>攻击号段 [200..299]：伤害、暴击、射速、装填。</summary>
    public const int AttackStart = 200;
    public const int AttackEnd = 299;

    /// <summary>投射物号段 [300..399]。</summary>
    public const int ProjectileStart = 300;
    public const int ProjectileEnd = 399;

    /// <summary>资源/成长号段 [400..499]：经验、掉落、冷却缩减。</summary>
    public const int ProgressionStart = 400;
    public const int ProgressionEnd = 499;

    /// <summary>技能号段 [500..599]。</summary>
    public const int SkillStart = 500;
    public const int SkillEnd = 599;

    /// <summary>预留号段 [900..999]：新品类不得插入中间，只能落在本段之后。</summary>
    public const int ReservedStart = 900;
    public const int ReservedEnd = 999;

    /// <summary>追加区起点：所有「发布后新增」的属性都从这里往上加，保持数值递增。</summary>
    public const int AppendStart = 1000;
}

/// <summary>
/// 属性 id。**只加不改**：禁止删除、禁止重排、禁止改变已有成员的含义。
/// <para>为什么这里只落「骨架 + 生存/移动/攻击最小集」，而不是把 docs/rules/05 §5.1 的整张表抄过来：</para>
/// <list type="bullet">
/// <item>数值层的真源是 Excel 的 <c>StatDefs</c> 表（规则4）。代码枚举只是它的**稳定编码**，
/// 真正的属性清单随数据表增长，抄全了反而会与 Excel 漂移。</item>
/// <item>本任务的目标是「给 ECS 与 Data 层一个可编译、可寻址的 <c>StatId</c>」，不是最终数值表；
/// 后续数值任务按号段规则向 <see cref="StatIdRanges.AppendStart"/> 之上追加即可，不需要改本文件的结构。</item>
/// <item>底层类型用 <c>ushort</c>（与 docs/rules/05 §5.1 一致）：一个 <c>StatId</c> 只占 2 字节，
/// <c>StatBlock</c> 的数组索引会按它做密集下标，省内存也省 cache。</item>
/// </list>
/// <para>校验入口：<see cref="StatIdExtensions.IsAppendZone"/> / <see cref="StatIdExtensions.RangeOf"/>，
/// 数据生成器与单测用它们做构建期校验（新 id 落在错误号段 = 构建失败）。</para>
/// </summary>
public enum StatId : ushort
{
    /// <summary>空属性。0 必须是「无」，这样 <c>default</c> 不会指向一个有意义的属性。</summary>
    None = 0,

    // ── [1..99] 生存 ──
    Hp = 1,
    HpMax = 2,
    ArmorFlat = 4,
    HurtInvulnTime = 8,

    // ── [100..199] 移动 ──
    MoveSpeed = 100,
    MoveSpeedMul = 101,

    // ── [200..299] 攻击 ──
    Damage = 200,
    DamageMul = 201,
    CritChance = 202,
    CritMultiplier = 203,

    // ── [900..999] 预留：新品类不得插入中间 ──
    Reserved900 = 900,

    // ── [1000+] 追加区：后续任务的数值任务从这里往上加，禁止改动上面的任何成员 ──

    // ★ 2026-11-04 角色抽象：三基础数值（生命/法力/护甲）+ 力量/敏捷/幸运等额外数值。
    //   生命用上面的 Hp/HpMax；法力与护甲在此补齐。
    Mana = 1000,
    ManaMax = 1001,
    ManaRegenPerSec = 1002,

    /// <summary>护甲值（参与护甲减免公式 <c>dmg * ArmorK / (ArmorK + armor)</c>）。</summary>
    Armor = 1003,

    /// <summary>护甲公式的软化常数 k：护甲 = k 时正好减伤 50%。来自数据，不在代码里写死。</summary>
    ArmorK = 1004,

    /// <summary>力量（额外数值；影响攻击/生命等由 Formula 表表达）。</summary>
    Strength = 1010,

    /// <summary>敏捷（额外数值；影响移动/攻速）。</summary>
    Dexterity = 1011,

    /// <summary>幸运（额外数值；影响暴击/掉落）。</summary>
    Luck = 1012,

    /// <summary>力量倍率层（装备/Buff 用，默认 1.0）。</summary>
    StrengthMul = 1013,

    /// <summary>敏捷倍率层（默认 1.0）。</summary>
    DexterityMul = 1014,

    /// <summary>幸运倍率层（默认 1.0）。</summary>
    LuckMul = 1015,

    // 次要生存资源（冲刺/重击消耗）
    Stamina = 1020,
    StaminaMax = 1021,
    StaminaRegenPerSec = 1022,
}

/// <summary><see cref="StatId"/> 的号段查询：给数据生成器与单测做构建期校验用。</summary>
public static class StatIdExtensions
{
    /// <summary>是否「空属性」。用名字而不是 <c>== StatId.None</c> 散落各处，将来若引入 <c>Reserved</c> 类哨兵只改这里。</summary>
    public static bool IsNone(this StatId id) => id == StatId.None;

    /// <summary>是否落在「追加区」（≥ <see cref="StatIdRanges.AppendStart"/>）：发布后新增的属性必须在这里。</summary>
    public static bool IsAppendZone(this StatId id) => (int)id >= StatIdRanges.AppendStart;

    /// <summary>取该 id 所属号段的起点；不在任何号段内返回 0（构建期校验会把它判为非法）。</summary>
    public static int RangeOf(this StatId id)
    {
        int v = (int)id;
        if (v >= StatIdRanges.AppendStart) { return StatIdRanges.AppendStart; }
        if (v >= StatIdRanges.SurvivalStart && v <= StatIdRanges.SurvivalEnd) { return StatIdRanges.SurvivalStart; }
        if (v >= StatIdRanges.MovementStart && v <= StatIdRanges.MovementEnd) { return StatIdRanges.MovementStart; }
        if (v >= StatIdRanges.AttackStart && v <= StatIdRanges.AttackEnd) { return StatIdRanges.AttackStart; }
        if (v >= StatIdRanges.ProjectileStart && v <= StatIdRanges.ProjectileEnd) { return StatIdRanges.ProjectileStart; }
        if (v >= StatIdRanges.ProgressionStart && v <= StatIdRanges.ProgressionEnd) { return StatIdRanges.ProgressionStart; }
        if (v >= StatIdRanges.SkillStart && v <= StatIdRanges.SkillEnd) { return StatIdRanges.SkillStart; }
        if (v >= StatIdRanges.ReservedStart && v <= StatIdRanges.ReservedEnd) { return StatIdRanges.ReservedStart; }
        return 0;
    }
}

