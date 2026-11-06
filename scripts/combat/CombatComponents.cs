using System;
using Game.Core;
using Game.Core.Ecs;
using NVector3 = System.Numerics.Vector3;

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

    // ── 以下字段来自 hero-1 会话的弹道快照（main 的契约式 ProjectileState 原本没有） ──
    // 契约式设计把"伤害/半径"放在投射物上、把"速度/射程"放进 ProjectileAdvanceSystem 读的
    // WeaponContract；但 hero-1 的 FireProjectileSkill 需要一个自足的子弹快照，
    // 才能在"开火那一刻的武器数值"上飞行（Buff 改数值 / 换武器后已飞出的子弹不受影响）。
    // 两者共用同一个组件、同一个实体的不同字段，互不覆盖（规则：单写入者）。
    /// <summary>速度（米/秒，标量；方向来自 <c>Transform3</c>.Rotation 的前方）。</summary>
    public float Speed;

    /// <summary>射程（米）：已飞距离 ≥ 本值时销毁（&lt;= 0 表示不看射程，只靠寿命兜底）。</summary>
    public float RangeMeters;

    /// <summary>是否暴击（开火时掷骰决定；表现层据此换特效颜色）。</summary>
    public bool Critical;

    /// <summary>武器行 id（统计/命中反馈按武器分流用；0 = 无）。</summary>
    public int WeaponId;

    /// <summary>伤害类型（决定是否吃元素抗性）。</summary>
    public DamageType DamageType;

    /// <summary>已飞行距离（米，由弹道系统累加）。</summary>
    public float Traveled;

    /// <summary>已命中/待销毁标记：置位后不再参与命中判定。</summary>
    public bool Spent;

    // ── 以下四个是「弹道运动学」的运行时状态（见 ProjectileMotion） ──
    // 为什么把运动参数放组件而不是每次查契约：弹道系统每固定步都要求值一次，
    // 而契约里的 ValueSet 解析要建 ValueContext、可能掷随机数（rand:），每步重算会让
    // 「速度/角度」逐帧漂移 —— 随机项必须只在发射瞬间定一次。这不是缓存优化，是语义正确性。
    //
    // 与上面 hero-1 的弹道快照字段是**两组正交数据**，同组件共存、互不覆盖：
    // · hero-1 那组是「命中/伤害链」要的自足快照（Speed/RangeMeters/Critical/WeaponId/
    //   DamageType/Traveled/Spent），由 FireProjectileSkill 在开火瞬间填；
    // · 下面这组是「运动学」要的轨迹参数（Motion/HomingTurn/Gravity/BaseSpeed/Age/
    //   LaunchDirection），由 ContentSpawner 在铸发射瞬间按契约填。
    // 走契约铸造的子弹两组都会填；走 FireProjectileSkill 的子弹只有上一组，Motion 保持 0
    // （= Straight），运动学自然退化为匀速直线，不需要分支判断。
    // 注意 `Speed`（hero-1，标量，方向来自 Transform3.Rotation）与 `BaseSpeed`（本组，
    // 发射瞬间基准速率，供 Wave/Spiral 做加减）语义不同，不可合并。

    /// <summary>本步的运动学（发射瞬间由契约拷入；契约被热重载也不会让在飞的子弹改变轨迹）。</summary>
    public byte Motion;

    /// <summary>追踪转速（度/秒，仅 Homing 用）。</summary>
    public float HomingTurn;

    /// <summary>重力加速度（米/秒²，仅 Falling 用；负值 = 向下）。</summary>
    public float Gravity;

    /// <summary>发射瞬间的基准速率（米/秒）——所有运动学围着它做加减，改动它等于改「弹速」。</summary>
    public float BaseSpeed;

    /// <summary>已飞行时长（秒）；Wave / Orbit / Spiral 用它算相位，Anchor 用它算淡出。</summary>
    public float Age;

    /// <summary>发射瞬间的朝向（Wave / Boomerang / Spiral 需要「原始轴」才能绕行，转向后仍要用）。</summary>
    public NVector3 LaunchDirection;
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

/// <summary>
/// 命中记录（**命令载荷**，值类型）：由命中检测投递、由 <see cref="ImpactEventSystem"/> 在遍历结束后消费。
/// <para>
/// <b>为什么是队列而不是"命中时立刻发事件/生成特效"</b>：命中检测在 chunk 遍历里（J5：遍历期间不能改结构），
/// 而命中要产生两件副作用——把子弹标记 <see cref="Projectile.Spent"/>（纯写，允许）与生成命中特效
/// （结构变更，必须排队）。把"发生了什么"记成值记录，遍历结束后统一消费，顺序与入队顺序一致：
/// 顺序可推理 → 特效与伤害的先后关系可推理 → 回放一致。
/// </para>
/// <para>
/// <b>为什么命中不在这里直接扣血</b>：伤害结算是独立任务（护甲/暴击抗性/无敌帧/吸血/击杀判定都在那里）。
/// 本任务只负责"几何上打到了"，并把伤害快照一起带过去；结算系统落地时直接消费本记录即可，
/// 不需要改动命中检测（规则7：新增结算逻辑 ≤ 2 个文件）。
/// </para>
/// </summary>
public readonly struct HitRecord
{
    /// <summary>子弹实体（Index/Generation 快照）。</summary>
    public readonly Entity Projectile;

    /// <summary>射手实体槽位号（可能已被销毁）。</summary>
    public readonly int ShooterIndex;

    /// <summary>射手世代号。</summary>
    public readonly int ShooterGeneration;

    /// <summary>被命中的目标实体。</summary>
    public readonly Entity Target;

    /// <summary>命中点（世界坐标）。</summary>
    public readonly System.Numerics.Vector3 Position;

    /// <summary>伤害快照（未计护甲）。</summary>
    public readonly float Damage;

    /// <summary>是否暴击。</summary>
    public readonly bool Critical;

    /// <summary>伤害类型。</summary>
    public readonly DamageType DamageType;

    /// <summary>武器 id。</summary>
    public readonly int WeaponId;

    /// <summary>命中发生在哪一帧之后（调试/乱序检测用）。</summary>
    public readonly int Step;

    /// <summary>构造一条命中记录。</summary>
    public HitRecord(
        Entity projectile,
        int shooterIndex,
        int shooterGeneration,
        Entity target,
        System.Numerics.Vector3 position,
        float damage,
        bool critical,
        DamageType damageType,
        int weaponId,
        int step)
    {
        Projectile = projectile;
        ShooterIndex = shooterIndex;
        ShooterGeneration = shooterGeneration;
        Target = target;
        Position = position;
        Damage = damage;
        Critical = critical;
        DamageType = damageType;
        WeaponId = weaponId;
        Step = step;
    }
}


// ══════════════════════════════════════════════════════════════════════════════
// 以下来自 hero-1 会话：**碰撞/命中/伤害快照**方向的组件与缓冲。
//
// 与上面 main 的 ProjectileState 的分工（合并时的裁定，必须读懂）：
//   · ProjectileState  —— **飞行动力学**：契约槽位、寿命、穿透/弹跳/链深。
//     由 main 的 ProjectileAdvanceSystem 驱动（位置积分 + 寿命递减 + 到期产物）。
//   · Projectile       —— **命中与伤害结算快照**：开火那一刻的伤害/暴击/武器/伤害类型，
//     以及"已飞距离 / 是否已消耗"。由 HitDetectionSystem 与 ImpactEventSystem 使用。
//
// 为什么不是同一个组件：两者是**不同的写入者与不同的生命周期**
//   （前者每固定步长被推进系统整体重写，后者开火时写一次、命中时置 Spent）。
//   合成一个会让"推进系统每帧覆盖伤害字段"变成潜在事故 —— 单写入者纪律优先于字段数量。
//   两个组件同时挂在一颗子弹上，各自只碰自己的字段，互不覆盖。
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 投射物：一个沿直线飞行、命中后消失的实体。
/// <para>
/// <b>为什么射手用 (Index, Generation) 两个 int 而不是 <see cref="Entity"/></b>：两者布局等价，
/// 但显式两个 int 让「这是用于<u>比较</u>的快照，不是要解引用的句柄」这件事在阅读时一目了然；
/// 命中检测里要做的正是"跳过自己"的相等比较，而不是去访问射手实体。
/// </para>
/// <para>
/// <b>为什么伤害/暴击/半径都在子弹上而不是"开火时再查武器"</b>：开火到命中之间有飞行时间，
/// 这期间射手的数值可能被 Buff 改掉、武器可能被换掉、射手可能已经死了。
/// 把"开火那一刻的数值快照"烙在子弹上，才能做到"飞出去的子弹永远按开火时的规则结算"（回放/确定性）。
/// </para>
/// <para>
/// <b>为什么同时存 Speed 与 Traveled</b>：射程用"已飞距离"表达比用"剩余寿命"更贴近策划的语言
/// （射程 20 米 = 飞 20 米就消失），而 Speed 留给表现层（弹道有速度感）。寿命当作兜底安全网，
/// 二者任何一个先到就销毁（见 <c>ProjectileSystem</c>）。
/// </para>
/// </summary>
public struct Projectile
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>射手实体槽位号（-1 = 无射手，例如场景机关发射的子弹）。</summary>
    public int ShooterIndex;

    /// <summary>射手世代号（与槽位号一起构成"这是不是同一只"的判据）。</summary>
    public int ShooterGeneration;

    /// <summary>子弹所属阵营（命中判定用；与射手阵营在开火时一致）。</summary>
    public TeamId Team;

    /// <summary>本发子弹的伤害快照（已含暴击倍率；护甲在结算时另算）。</summary>
    public float Damage;

    /// <summary>判定半径（米）。</summary>
    public float Radius;

    /// <summary>速度（米/秒，标量；方向来自 <see cref="Transform3"/>.Rotation 的前方）。</summary>
    public float Speed;

    /// <summary>射程（米）：已飞距离 ≥ 本值时销毁（&lt;= 0 表示不看射程，只靠寿命兜底）。</summary>
    public float RangeMeters;

    /// <summary>是否暴击（开火时掷骰决定；表现层据此换特效颜色）。</summary>
    public bool Critical;

    /// <summary>武器行 id（统计/命中反馈按武器分流用；0 = 无）。</summary>
    public int WeaponId;

    /// <summary>伤害类型（决定是否吃元素抗性）；默认 <see cref="DamageType.Kinetic"/> 来自数据，不在代码里挑。</summary>
    public DamageType DamageType;

    /// <summary>已飞行距离（米，由 <c>ProjectileSystem</c> 累加）。</summary>
    public float Traveled;

    /// <summary>已命中/待销毁标记：置位后不再参与命中判定（同一帧内避免一颗子弹打穿三个目标）。</summary>
    public bool Spent;
}

/// <summary>
/// 命中缓冲：一帧内的命中记录队列（复用数组、稳态零分配）。
/// <para>
/// <b>为什么不用 <c>Queue&lt;HitRecord&gt;</c></b>：命中是高频事件（割草游戏一帧几十条），
/// <c>Queue</c> 的环形缓冲虽然不分配，但每帧的清空与容量增长管理不如"复用数组 + 计数"直接；
/// 而且这个结构要被两端（检测系统追加 / 事件系统消费）共享，暴露 <c>AsSpan()</c> 让消费者零拷贝遍历，
/// 避免逐条出队的函数调用。
/// </para>
/// </summary>
public sealed class HitBuffer
{
    /// <summary>初始容量（内存预分配，不是玩法数值）。</summary>
    private const int InitialCapacity = 64; // arch-ok: 缓冲区预分配容量（内存/性能参数），不是玩法数值

    private HitRecord[] _items = new HitRecord[InitialCapacity];
    private int _count;

    /// <summary>当前待消费的命中数。</summary>
    public int Count => _count;

    /// <summary>累计写入的命中数（跨帧不清零；统计用）。</summary>
    public int TotalEnqueued { get; private set; }

    /// <summary>追加一条命中（只追加；容量不足时倍增，稳态不再分配）。</summary>
    public void Add(in HitRecord record)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }
        _items[_count++] = record;
        TotalEnqueued++;
    }

    /// <summary>只读视图（有效区是前 <see cref="Count"/> 条）。</summary>
    public ReadOnlySpan<HitRecord> Records => _items.AsSpan(0, _count);

    /// <summary>清空（消费方在决定"跳过"时也可直接调用）。</summary>
    public void Clear() => _count = 0;
}
