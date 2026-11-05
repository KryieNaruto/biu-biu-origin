namespace Game.Core.Content;

/// <summary>
/// 武器的**触发方式**：什么时候开火。
/// <para>
/// 与瞄准（<see cref="WeaponAimKind"/>）、投送（<see cref="WeaponDelivery"/>）正交：
/// 三个维度各自独立填表，组合出「自动连射的追踪飞弹」「蓄力后手动引爆的炸弹」等全部形态，
/// 而不需要为每种组合新增一个武器类（规则7）。
/// </para>
/// </summary>
public enum WeaponTrigger : byte
{
    /// <summary>按住即持续开火（射速由 FireDelay 规则给）。</summary>
    Auto = 0,

    /// <summary>点击一次发一次（受 FireDelay 门控）。</summary>
    Semi = 1,

    /// <summary>点一次打一串（串内间隔与串长也来自表）。</summary>
    Burst = 2,

    /// <summary>按住蓄力，松开释放（蓄力上限来自 ChargeTime）。</summary>
    Charge = 3,

    /// <summary>按住持续生效（光束 / 护盾），不产生"一次发射"。</summary>
    Hold = 4,

    /// <summary>由事件触发（陷阱、地雷、受击反击），不响应直接输入。</summary>
    Triggered = 5,

    /// <summary>手动（第二段输入 / 脚本）触发，常用于引爆。</summary>
    Manual = 6,
}

/// <summary>
/// 武器的**投送方式**：这一次发射"把什么送到哪个目标"。
/// <para>
/// 它与 <see cref="ObjectArchetype.Capabilities"/> 里的 <see cref="ObjectCapability.Weapon"/> 位配合：
/// 有 Weapon 位说明「能开火」，<see cref="WeaponDelivery"/> 说明「开火后生成什么」。
///
/// 例如 <see cref="SpawnActor"/> 让「枪里射出的是怪」成为一条表数据，而不是一个特殊类。
/// </para>
/// </summary>
public enum WeaponDelivery : byte
{
    /// <summary>瞬时命中（射线 / 近战判定当帧结算）。</summary>
    Instant = 0,

    /// <summary>发射投射物（最常见）。</summary>
    Projectile = 1,

    /// <summary>光束（持续性，命中路线上的所有目标）。</summary>
    Beam = 2,

    /// <summary>近战挥击（扇形 / 矩形判定，由 HitRadius 与 SpreadDegrees 描述）。</summary>
    Melee = 3,

    /// <summary>持续光环（自身周围的区域判定）。</summary>
    Aura = 4,

    /// <summary>生成一个实体（召唤、枪里射出怪）。</summary>
    SpawnActor = 5,

    /// <summary>抛掷（轨迹受重力影响，落地后由 OnExpire 规则决定后续）。</summary>
    Throw = 6,
}

/// <summary>
/// <b>武器契约</b>：一把武器的一切「可配置行为」。
/// <para>
/// <b>为什么所有数值都是 <see cref="ValueSet"/> 而不是 float</b>：
/// </para>
/// <list type="bullet">
/// <item>子弹要「继承武器属性」—— <see cref="ValueSource.WeaponStat"/> 只能从武器身上读到属性，
/// 所以武器伤害必须能解析成"一个可被引用的数"，而不是一个死掉的常量；</item>
/// <item>熔炉派生（同一把枪改出十种变体）靠的就是「同一条规则 × 不同的数值包」，
/// 规则留在表里才能被组合；</item>
/// <item>词条（+20% 射速）也走同一套层叠公式，于是武器契约与物件契约不需要两套数值系统。</item>
/// </list>
/// <para>
/// <b>纪律</b>：本类型是纯粹的数据行（无行为方法）。开火时机、弹道、命中结算分别属于
/// 武器系统、弹道系统、战斗系统，它们只读这里，不往这里写。
/// </para>
/// </summary>
public sealed class WeaponContract
{
    /// <summary>构造一把武器契约（字段全部只读，靠构造器一次成型）。</summary>
    public WeaponContract(
        int id,
        string defId,
        int objectDefId,
        WeaponTrigger trigger = WeaponTrigger.Auto,
        WeaponDelivery delivery = WeaponDelivery.Projectile,
        WeaponAimKind aim = WeaponAimKind.AimPoint,
        int multiProjectile = 0,
        DamageType damageType = DamageType.None,
        TeamId teamLayer = TeamId.Neutral,
        bool inheritWeaponStats = false,
        int chainDepthLimit = 0,
        float chainShrink = 0f,
        in ContentRef projectile = default,
        in ContentRef actor = default,
        in ContentRef effect = default,
        ObjectBitMatrix? projectileMatrix = null,
        ObjectBitMatrix? weaponMatrix = null,
        in ValueSet fireDelay = default,
        in ValueSet damage = default,
        in ValueSet speed = default,
        in ValueSet range = default,
        in ValueSet spreadDegrees = default,
        in ValueSet count = default,
        in ValueSet hitRadius = default,
        in ValueSet lifetime = default,
        in ValueSet knockback = default,
        in ValueSet pierce = default,
        in ValueSet recoil = default,
        in ValueSet chargeTime = default,
        in ValueSet aimOffset = default,
        in ValueRotation muzzleRotation = default,
        in ValueSet burstCount = default,
        in ValueSet burstInterval = default,
        in ValueSet damageFalloff = default)
    {
        Id = id;
        DefId = defId ?? string.Empty;
        ObjectDefId = objectDefId;
        Trigger = trigger;
        Delivery = delivery;
        Aim = aim;
        MultiProjectile = multiProjectile;
        DamageType = damageType;
        TeamLayer = teamLayer;
        InheritWeaponStats = inheritWeaponStats;
        ChainDepthLimit = chainDepthLimit;
        ChainShrink = chainShrink;
        Projectile = projectile;
        Actor = actor;
        Effect = effect;
        ProjectileMatrix = projectileMatrix;
        WeaponMatrix = weaponMatrix;

        FireDelay = fireDelay;
        Damage = damage;
        Speed = speed;
        Range = range;
        SpreadDegrees = spreadDegrees;
        Count = count;
        HitRadius = hitRadius;
        Lifetime = lifetime;
        Knockback = knockback;
        Pierce = pierce;
        Recoil = recoil;
        ChargeTime = chargeTime;
        AimOffset = aimOffset;
        MuzzleRotation = muzzleRotation;
        BurstCount = burstCount;
        BurstInterval = burstInterval;
        DamageFalloff = damageFalloff;
    }

    /// <summary>武器数字 id（仓库槽位 / 存档用；0 = 空武器）。</summary>
    public int Id { get; }

    /// <summary>武器定义 id（Excel 主键字符串）。</summary>
    public string DefId { get; }

    /// <summary>指向的 <see cref="ObjectArchetype"/>（同一把武器既是物件也是武器：物件给数值包与外观，这里给行为）。</summary>
    public int ObjectDefId { get; }

    /// <summary>触发方式。</summary>
    public WeaponTrigger Trigger { get; }

    /// <summary>投送方式。</summary>
    public WeaponDelivery Delivery { get; }

    /// <summary>瞄准方式。</summary>
    public WeaponAimKind Aim { get; }

    /// <summary>
    /// 一次发射的弹数：0 = 单发（数量由 <see cref="Count"/> 规则给）；&gt;0 = 固定枚数（表里直接写死的档位）。
    /// <para>为什么要两个来源：固定枚数便于策划做"双管 / 三连发"这类硬档位，而规则数量用于"词条 +1"的动态叠加。</para>
    /// </summary>
    public int MultiProjectile { get; }

    /// <summary>伤害类型（抗性查表用）。</summary>
    public DamageType DamageType { get; }

    /// <summary>
    /// 阵营层：子弹命中时用谁的阵营判定敌我。
    /// 「子弹打谁」因此是数据：玩家射出的子弹通常是 <see cref="TeamId.Player"/>，
    /// 而被夺舍的怪射出的子弹可以仍按原阵营结算（规则6 的「可互相赋值」在阵营维度的落点）。
    /// </summary>
    public TeamId TeamLayer { get; }

    /// <summary>子弹是否继承发射武器的数值（决定 <see cref="ValueSource.WeaponStat"/> 规则有没有值）。</summary>
    public bool InheritWeaponStats { get; }

    /// <summary>熔炉链深上限（防止"子弹射子弹"无限递归；0 = 不限制，由数据负责）。</summary>
    public int ChainDepthLimit { get; }

    /// <summary>每层熔炉衰减比例（0.1 = 每派生一层，数值乘 0.9）。</summary>
    public float ChainShrink { get; }

    /// <summary>子引用：发射的投射物。</summary>
    public ContentRef Projectile { get; }

    /// <summary>子引用：生成的实体（Delivery = SpawnActor 时用）。</summary>
    public ContentRef Actor { get; }

    /// <summary>子引用：开火特效。</summary>
    public ContentRef Effect { get; }

    /// <summary>
    /// 投射物位矩阵（子弹继承武器属性就靠它：子弹那一行的勾选决定它吃哪些武器加成）。
    /// 是「投射物 × 标签」的整张网格；某一行是哪颗子弹由 <see cref="Projectile"/> 的 <see cref="ContentRef.DefId"/> 决定。
    /// </summary>
    public ObjectBitMatrix? ProjectileMatrix { get; }

    /// <summary>武器自身位矩阵（武器 × 标签）。</summary>
    public ObjectBitMatrix? WeaponMatrix { get; }

    /// <summary>开火间隔（秒）。这是"射速"的唯一来源，代码里不出现任何射速常量。</summary>
    public ValueSet FireDelay { get; }

    /// <summary>单发伤害（通常是 <c>WeaponStat(WeaponDamage)</c> × 系数，或对 Stat.Damage 的百分比）。</summary>
    public ValueSet Damage { get; }

    /// <summary>投射物初速（米/秒）。</summary>
    public ValueSet Speed { get; }

    /// <summary>有效射程（超过后由 Expire 规则处理）。</summary>
    public ValueSet Range { get; }

    /// <summary>散布角度（度）。</summary>
    public ValueSet SpreadDegrees { get; }

    /// <summary>弹数（多发射击 / 环形弹幕）。</summary>
    public ValueSet Count { get; }

    /// <summary>命中判定半径（投射物半径 / 近战判定厚度）。</summary>
    public ValueSet HitRadius { get; }

    /// <summary>投射物存活时长（秒）。</summary>
    public ValueSet Lifetime { get; }

    /// <summary>击退力度（传给受击方，具体位移由移动系统按质量换算）。</summary>
    public ValueSet Knockback { get; }

    /// <summary>穿透次数（穿过 N 个目标后才结束）。</summary>
    public ValueSet Pierce { get; }

    /// <summary>后坐力（对发射者的反冲，手感层）。</summary>
    public ValueSet Recoil { get; }

    /// <summary>蓄力时间（仅 <see cref="WeaponTrigger.Charge"/> 有效）。</summary>
    public ValueSet ChargeTime { get; }

    /// <summary>枪口偏移（在朝向坐标系里的位置，用作发射点；塔防 / 双管炮用）。</summary>
    public ValueSet AimOffset { get; }

    /// <summary>枪口朝向偏移（四元数各分量一条规则；「枪口斜 15°」这类挂点细节进表）。</summary>
    public ValueRotation MuzzleRotation { get; }

    /// <summary>连发次数（仅 <see cref="WeaponTrigger.Burst"/> 有效）。</summary>
    public ValueSet BurstCount { get; }

    /// <summary>连发间隔（秒）。</summary>
    public ValueSet BurstInterval { get; }

    /// <summary>伤害随距离的衰减曲线（倍率规则；空 = 不衰减）。</summary>
    public ValueSet DamageFalloff { get; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("Weapon(", DefId.Length > 0 ? DefId : Id.ToString(), ", ", Trigger.ToString(), "/", Delivery.ToString(), "/", Aim.ToString(), ")");
}
