namespace Game.Core.Content;

/// <summary>
/// 投射物的**运动学**：每帧位置怎么推进。
/// <para>
/// 与命中策略（<see cref="ProjectileHitPolicy"/>）、消亡行为（<see cref="ProjectileDeath"/>）三个维度正交，
/// 于是「追踪 + 穿透 + 消亡时爆炸」只是一行表的三列，不需要一个「追踪穿透爆炸弹」的专用类。
/// 弹道系统按本枚举实现一次运动积分，新增一种运动 = 加一个枚举值 + 一处实现。
/// </para>
/// </summary>
public enum ProjectileMotion : byte
{
    /// <summary>直线匀速（最常见）。</summary>
    Straight = 0,

    /// <summary>追踪（朝目标转向，转速来自 HomingTurn）。</summary>
    Homing = 1,

    /// <summary>环绕发射者（卫星式弹幕）。</summary>
    Orbit = 2,

    /// <summary>正弦摆动（"蛇形弹"）。</summary>
    Wave = 3,

    /// <summary>回旋镖（飞出后返回发射者）。</summary>
    Boomerang = 4,

    /// <summary>受重力下落（手雷 / 投掷物）。</summary>
    Falling = 5,

    /// <summary>螺旋外扩（涡流弹幕）。</summary>
    Spiral = 6,

    /// <summary>锚定在发射瞬间的位置（地雷 / 延时区域）。</summary>
    Anchor = 7,
}

/// <summary>
/// 投射物的**命中策略**：碰到目标之后怎么办。
/// <para>只要一行枚举值，就能把「穿透弹」「弹墙弹」「爆炸弹」这些在别的项目里各自成类的东西统一掉。</para>
/// </summary>
public enum ProjectileHitPolicy : byte
{
    /// <summary>命中即消失（普通子弹）。</summary>
    Single = 0,

    /// <summary>穿透（次数来自 Pierce 规则）。</summary>
    Pierce = 1,

    /// <summary>反弹（次数来自 Bounce 规则）。</summary>
    Bounce = 2,

    /// <summary>范围伤害（半径来自 Radius 规则）。</summary>
    Splash = 3,

    /// <summary>立即爆炸并消亡。</summary>
    Explode = 4,

    /// <summary>链式传递到附近目标（链深上限见 <see cref="ProjectileContract.MaxChainDepth"/>）。</summary>
    Chain = 5,

    /// <summary>命中后返回（回旋镖类）。</summary>
    Return = 6,

    /// <summary>不与任何东西碰撞（纯视觉 / 穿透地形的传送门用）。</summary>
    Ignore = 7,
}

/// <summary>
/// 投射物的**消亡行为**：寿命到了 / 命中结束之后做什么。
/// <para>
/// 这一列是"子弹自己也是一个物件"的关键：<see cref="SpawnProjectile"/> 与 <see cref="SpawnActor"/>
/// 让「子弹分裂成子弹」「子弹变成怪」变成表数据，而不是系统里的特判分支。
/// </para>
/// </summary>
public enum ProjectileDeath : byte
{
    /// <summary>直接消失。</summary>
    None = 0,

    /// <summary>生成表现特效。</summary>
    SpawnEffect = 1,

    /// <summary>生成投射物（分裂 / 子母弹）。</summary>
    SpawnProjectile = 2,

    /// <summary>生成实体（子弹孵化为怪）。</summary>
    SpawnActor = 3,

    /// <summary>原地爆炸（范围结算）。</summary>
    Explode = 4,

    /// <summary>继承发射者的消亡行为（自适应模板用：自己不填，跟着武器走）。</summary>
    Inherit = 5,
}

/// <summary>
/// <b>投射物契约</b>：一颗子弹的一切可配置行为。
/// <para>
/// <b>为什么数值同样全是 <see cref="ValueSet"/></b>：同一颗"手枪子弹"被不同武器发射时，
/// 速度 / 伤害 / 存活时间往往要跟着武器变 —— 用 <see cref="ValueSource.WeaponStat"/> 规则表达，
/// 于是「子弹具有武器属性」就是一句数据，而不是系统里的一次特殊赋值。
/// </para>
/// <para>
/// 与 <see cref="WeaponContract"/> 的分工：武器回答「怎么发出去」，投射物回答「飞起来之后怎么算、怎么没的」。
/// 两者通过 <see cref="WeaponContract.Projectile"/> 的 <see cref="ContentRef"/> 连接，
/// 所以「一把枪换一颗子弹」= 改一个引用，不碰代码。
/// </para>
/// </summary>
public sealed class ProjectileContract
{
    /// <summary>构造一颗投射物契约（字段全部只读）。</summary>
    public ProjectileContract(
        int id,
        string defId,
        int objectDefId,
        ProjectileMotion motion = ProjectileMotion.Straight,
        ProjectileHitPolicy hitPolicy = ProjectileHitPolicy.Single,
        ProjectileDeath onExpire = ProjectileDeath.None,
        int maxChainDepth = 0,
        in ContentRef onHitProjectile = default,
        in ContentRef onHitActor = default,
        in ContentRef onExpireProjectile = default,
        in ContentRef onExpireActor = default,
        ObjectBitMatrix? matrix = null,
        in ValueSet speed = default,
        in ValueSet radius = default,
        in ValueSet lifetime = default,
        in ValueSet damage = default,
        in ValueSet pierce = default,
        in ValueSet bounce = default,
        in ValueSet gravity = default,
        in ValueSet homingTurn = default,
        in ValueSet thrust = default,
        in ValueSet damageDealtToNext = default)
    {
        Id = id;
        DefId = defId ?? string.Empty;
        ObjectDefId = objectDefId;
        Motion = motion;
        HitPolicy = hitPolicy;
        OnExpire = onExpire;
        MaxChainDepth = maxChainDepth;
        OnHitProjectile = onHitProjectile;
        OnHitActor = onHitActor;
        OnExpireProjectile = onExpireProjectile;
        OnExpireActor = onExpireActor;
        Matrix = matrix;

        Speed = speed;
        Radius = radius;
        Lifetime = lifetime;
        Damage = damage;
        Pierce = pierce;
        Bounce = bounce;
        Gravity = gravity;
        HomingTurn = homingTurn;
        Thrust = thrust;
        DamageDealtToNext = damageDealtToNext;
    }

    /// <summary>投射物数字 id（仓库槽位 / 存档用）。</summary>
    public int Id { get; }

    /// <summary>投射物定义 id（Excel 主键字符串）。</summary>
    public string DefId { get; }

    /// <summary>指向的 <see cref="ObjectArchetype"/>（子弹也是物件：有自己的数值包、外观、标签）。</summary>
    public int ObjectDefId { get; }

    /// <summary>运动学。</summary>
    public ProjectileMotion Motion { get; }

    /// <summary>命中策略。</summary>
    public ProjectileHitPolicy HitPolicy { get; }

    /// <summary>消亡行为。</summary>
    public ProjectileDeath OnExpire { get; }

    /// <summary>链式传递的深度上限（防止无限分裂）。</summary>
    public int MaxChainDepth { get; }

    /// <summary>子引用：命中时生成的投射物。</summary>
    public ContentRef OnHitProjectile { get; }

    /// <summary>子引用：命中时生成的实体。</summary>
    public ContentRef OnHitActor { get; }

    /// <summary>子引用：消亡时生成的投射物。</summary>
    public ContentRef OnExpireProjectile { get; }

    /// <summary>子引用：消亡时生成的实体。</summary>
    public ContentRef OnExpireActor { get; }

    /// <summary>投射物位矩阵（投射物 × 标签；子弹能不能吃武器加成、会不会点燃，都由它表达）。</summary>
    public ObjectBitMatrix? Matrix { get; }

    /// <summary>初速（米/秒）；未给出时可用武器 Speed。</summary>
    public ValueSet Speed { get; }

    /// <summary>碰撞半径（命中判定厚度）。</summary>
    public ValueSet Radius { get; }

    /// <summary>存活时长（秒）。</summary>
    public ValueSet Lifetime { get; }

    /// <summary>伤害（可用 <see cref="ValueSource.WeaponStat"/> 继承武器伤害）。</summary>
    public ValueSet Damage { get; }

    /// <summary>穿透次数。</summary>
    public ValueSet Pierce { get; }

    /// <summary>反弹次数。</summary>
    public ValueSet Bounce { get; }

    /// <summary>重力加速度（Falling 运动用）。</summary>
    public ValueSet Gravity { get; }

    /// <summary>追踪转速（Homing 运动用，度/秒）。</summary>
    public ValueSet HomingTurn { get; }

    /// <summary>持续推力（螺旋 / 加速弹用）。</summary>
    public ValueSet Thrust { get; }

    /// <summary>传递给下一个物件的伤害倍率（分裂 / 链式衰减：0.5 = 子弹伤害减半）。</summary>
    public ValueSet DamageDealtToNext { get; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("Projectile(", DefId.Length > 0 ? DefId : Id.ToString(), ", ", Motion.ToString(), "/", HitPolicy.ToString(), "/", OnExpire.ToString(), ")");
}
