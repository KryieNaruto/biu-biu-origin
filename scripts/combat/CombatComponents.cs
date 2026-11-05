using Game.Core;
using Game.Core.Ecs;

namespace Game.Combat;

// 战斗系统族的组件（ECS.md J2：纯 unmanaged 数据，正好一个 [Identity] 字段在第一位，无方法、无数值）。
// 为什么放在 scripts/combat/ 而不是官方组件文件：官方组件是「任何系统族都会用到」的基底
// （Transform3/Health/Team…），而「这个实体背着哪份武器契约」「这颗子弹还剩多久」是战斗域的词汇。
// 规则7：新增一种武器/子弹不需要改这里，只需要多一行 Excel（Def 由 ContentIndex 编址成 int）。

/// <summary>
/// 武器状态：实体身上「当前生效的那份武器契约」+ 冷却/蓄力/弹药这些每帧会变的数。
/// <para>
/// <b>为什么 <see cref="ContractSlot"/> 是 int 而不是 <c>WeaponContract</c> 引用</b>：J2 一票否决——
/// 组件持有托管引用（而且 <c>WeaponContract</c> 里还有 string）会让 chunk 被 GC 扫描，
/// 并让「数据层」反向依赖「内容层」。int 只是 <c>ContentIndex</c> 的数组下标，热路径查表 = 一次下标。
/// </para>
/// <para>
/// <b>为什么另外几个 def id 也是 int</b>：它们本来是 <c>ContentRef</c>（含 string，托管），
/// 所以装配期先解析成槽位号存进来（见 <c>ContentRegistry</c>）。
/// 这样「武器射什么」就成了组件里的三个 int，铸造系统读它不需要碰任何托管对象。
/// </para>
/// </summary>
public struct WeaponState
{
    /// <summary>实体自引用（内核在生成/迁移时自动写入，系统只读）。</summary>
    [Identity] public Entity Owner;

    /// <summary>当前生效的武器在 <c>ContentIndex.Weapon*</c> 里的槽位号（&lt; 0 = 没装武器）。</summary>
    public int ContractSlot;

    /// <summary>这枚武器所属的物件原型槽位号（&lt; 0 = 无）。数值包 / 外观 / 标签都挂在物件上。</summary>
    public int ArchetypeDefId;

    /// <summary>当前弹夹内剩余弹药（&lt;= 0 且 <see cref="AmmoMax"/> &gt; 0 时表示打空）。</summary>
    public int AmmoCurrent;

    /// <summary>弹夹容量（&lt;= 0 表示不限弹，此时 <see cref="AmmoCurrent"/> 恒为 0 也不影响开火）。</summary>
    public int AmmoMax;

    /// <summary>冷却剩余时长（秒）；&gt; 0 时本帧不能开火。</summary>
    public float CooldownRemaining;

    /// <summary>已累计的蓄力时长（秒）；只对蓄力类武器有意义。</summary>
    public float ChargeElapsed;

    /// <summary>这条武器产出物的槽位号（子弹 / 单位 / 特效三者只填一个，由 <c>WeaponDelivery</c> 决定读哪个）。</summary>
    public int ActiveProjectileDefId;

    /// <summary>阵营层：子弹按谁的身份结算敌我（来自武器契约，可被夺舍改写）。</summary>
    public TeamId TeamLayer;

    // ── 以下三个是「射什么」的 int 化投影（ContentRef 是托管结构，只能装配期解析） ──

    /// <summary>射出的子弹槽位号（<c>WeaponContract.Projectile</c> 解析结果；&lt; 0 = 无）。</summary>
    public int ProjectileDefId;

    /// <summary>生成的单位槽位号（<c>WeaponContract.Actor</c> 解析结果；&lt; 0 = 无）。</summary>
    public int ActorDefId;

    /// <summary>生成的特效槽位号（<c>WeaponContract.Effect</c> 解析结果；&lt; 0 = 无）。</summary>
    public int EffectDefId;
}

/// <summary>
/// 子弹状态：这颗子弹「用的是哪份契约、谁打的、还剩多少穿透/弹跳/链深」。
/// <para>
/// <b>为什么 <see cref="Lifetime"/> 与官方的 <c>Lifetime</c> 组件并存</b>：官方 <c>Lifetime</c> 是「实体存活的
/// 通用计时器」，而这里的寿命是**弹道契约**的一部分（"走完射程"与"活够时间"在有射程概念时不是一回事）。
/// 子弹同时挂两者时，弹道系统只递减自己的这一份，通用生命周期系统管它的那一份，
/// 两边互不抢占——单个写入者的纪律比字段数量更重要。
/// </para>
/// </summary>
public struct ProjectileState
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>子弹契约在 <c>ContentIndex</c> 里的槽位号（&lt; 0 = 契约缺失，弹道系统会跳过并按数据错误计数）。</summary>
    public int ContractSlot;

    /// <summary>子弹所属的物件原型槽位号（&lt; 0 = 无）。</summary>
    public int ArchetypeDefId;

    /// <summary>发射者实体（伤害归属）；已销毁时用 <c>World.IsAlive</c> 复核。</summary>
    public Entity Source;

    /// <summary>发射它的武器实体（可空 <see cref="Entity.None"/>）——「子弹继承武器属性」的链上上一段。</summary>
    public Entity SourceWeapon;

    /// <summary>伤害值（铸造时按契约规则求值一次，之后不再变化；穿透衰减由本系统按规则递减）。</summary>
    public float Damage;

    /// <summary>剩余寿命（秒，&lt;= 0 时按契约的 <c>OnExpire</c> 处理）。</summary>
    public float Lifetime;

    /// <summary>判定半径（米）。</summary>
    public float Radius;

    /// <summary>剩余穿透次数。</summary>
    public int PierceLeft;

    /// <summary>剩余弹跳次数。</summary>
    public int BounceLeft;

    /// <summary>熔炉链深度（顶层射出为 0；子弹再射子弹逐层 +1，用来防无限递归）。</summary>
    public int ChainDepth;
}

/// <summary>
/// 铸造诊断向量：最近一次「熔炉选物」的结果快照。
/// <para>
/// <b>为什么诊断要作为组件存在、而不是打日志</b>：HUD 要显示「这枪现在装的是什么、命中率多少」，
/// 调试面板要回放「这一枪为什么选了这个配方」。把它做成组件后，
/// 表现层用 <c>Query&lt;CastState&gt;</c> 一次读全，逻辑层不需要任何回调或事件订阅（零分配，J3）。
/// </para>
/// </summary>
public struct CastState
{
    /// <summary>「没有命中配方」哨兵（用 -1 而不是 0，因为 0 是合法的第一个 tag 下标）。</summary>
    public const int NoRecipe = -1;

    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>最近一次命中的配方 tag 下标（&lt; 0 = 没有命中任何配方）。</summary>
    public int LastRecipeTag;

    /// <summary>候选集里「可发动（viable）」的个数。</summary>
    public int ViableCount;

    /// <summary>整簇命中率（命中候选的权重和 / 全部候选的权重和），0..1。</summary>
    public float LastPerf;

    /// <summary>本帧接收了这次铸造的实体（可空 <see cref="Entity.None"/>）。</summary>
    public Entity LastReceiver;
}
