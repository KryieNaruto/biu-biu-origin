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

    // ★ 2026-11-05 基础物件抽象：武器号段（追加区，接在 1022 之后保持递增）。
    //   这一组是**武器物件身上的数值**：投射物要继承武器属性时，Def 里的
    //   ValueRef.OfWeaponStat(StatId.WeaponXxx) 就是去发射它的那把武器身上读这几个 id
    //   —— 这就是「子弹具有武器属性」在数据层的基础。注意它们只是**标识**，
    //   具体数值仍由 Excel 的 StatDefs / 数值包给（代码里没有射速、伤害的常量）。

    /// <summary>开火间隔的倒数（射速）。子弹继承武器时会读到它 → 「子弹具有武器属性」的数据基础。</summary>
    WeaponFireRate = 1030,

    /// <summary>武器基础伤害。子弹的 Damage 规则写成 <c>OfWeaponStat(WeaponDamage)</c> 即可继承发射者的伤害。</summary>
    WeaponDamage = 1031,

    /// <summary>投射物初速（米/秒）。投射物 Def 用它作为默认速度来源。</summary>
    WeaponProjectileSpeed = 1032,

    /// <summary>一次发射的弹数（散射 / 扇形弹幕的数量来源）。</summary>
    WeaponProjectileCount = 1033,

    /// <summary>散布角度（度）。</summary>
    WeaponSpread = 1034,

    /// <summary>有效射程（米）。</summary>
    WeaponRange = 1035,

    /// <summary>穿透次数（穿过 N 个目标后结束）。</summary>
    WeaponPierce = 1036,

    /// <summary>击退力度（手感层，交给移动系统按质量换算位移）。</summary>
    WeaponKnockback = 1037,

    /// <summary>后坐力（对发射者的反冲量）。</summary>
    WeaponRecoil = 1038,

    /// <summary>蓄力时间（秒，仅蓄力类武器有效）。</summary>
    WeaponChargeTime = 1039,

    /// <summary>武器暴击率（与角色 <see cref="CritChance"/> 分开：词条可以只加武器暴击）。</summary>
    WeaponCritChance = 1040,

    /// <summary>武器暴击倍率（与角色 <see cref="CritMultiplier"/> 分开，同上）。</summary>
    WeaponCritMultiplier = 1041,

    // ★ 投射物号段（追加区）。300~399 是投射物号段，但「已发布枚举不得插入中间」，
    //   所以新增成员只能落在追加区 1050+，保持递增。它们同样是「投射物物件身上的数值」，
    //   投射物 Def 里以 OfWeaponStat / OfStat 混合组合出「继承武器 + 自身特性」的最终值。

    /// <summary>投射物存活时长（秒）。</summary>
    ProjectileLifeTime = 1050,

    /// <summary>投射物碰撞半径（命中判定厚度）。</summary>
    ProjectileRadius = 1051,

    /// <summary>反弹次数（弹墙 / 弹怪）。</summary>
    ProjectileBounce = 1052,

    /// <summary>追踪转速（度/秒），追踪弹的核心手感参数。</summary>
    ProjectileHomingSpeed = 1053,

    /// <summary>投射物受到的重力加速度（抛掷 / 坠落弹用）。</summary>
    ProjectileGravity = 1054,

    // ★ 2026-11-05 hero-1：移动/瞄准手感（契约 docs/design/character-hud-contract.md §3）。
    //   为什么放在追加区而不是移动号段 [100..199]：号段是发布后的稳定编码，已有成员禁止重排；
    //   追加区就是为「发布后新增」开的，放这里不会与任何已有数据/存档的数值号冲突。
    /// <summary>加速响应时间（秒）：从静止到达目标速度所需时间。重甲型填大、敏捷型填小。</summary>
    MoveAccelTime = 1023,

    /// <summary>转向响应时间（秒）：角色朝向追上瞄准方向所需时间。0 = 瞬间对准。</summary>
    TurnResponseTime = 1024,

    /// <summary>瞄准距离上限（米）：光标点被夹取到角色周围的世界半径。</summary>
    AimRange = 1025,

    // ★ 2026-11-06 dash-skill：冲刺（Shift，SkillSlot.Dash）的手感参数。
    //   为什么走数值包而不是写进技能代码：冲刺"冲多远、冲多快"是角色手感的一部分，
    //   和 MoveSpeed 同源 —— 换一个角色只要换一行数据，技能实现类一行不改（规则4/规则6）。
    //   为什么是「倍率 + 时长」而不是「冲刺速度 + 时长」：倍率让冲刺自动跟随该角色的基础移速，
    //   敏捷型（7.4）冲出去自然比重甲型（3.6）远，不必为每个变体各填一个绝对速度。
    /// <summary>冲刺速度倍率：冲刺期间速度 = <see cref="MoveSpeed"/> × 本值（1 = 不加速，按普通走路冲）。</summary>
    DashSpeedMul = 1055,

    /// <summary>冲刺持续时间（秒）：这段时间内速度被强制为冲刺速度，与普通移动意图无关。</summary>
    DashDuration = 1056,
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

