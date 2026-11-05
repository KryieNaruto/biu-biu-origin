using Game.Core;

namespace Game.Core.Ecs;

// 官方组件 v1（ECS.md 第 4.2 节）。三条硬纪律，逐条都对应一条判定：
//   ① 一律 unmanaged struct（J2）：不得持有 Node / Resource / 任何托管引用，GC 永不扫 chunk；
//   ② 正好一个 [Identity] 字段、且必须是第一个字段：内核靠它把实体号写进组件（J1 的执行方式）；
//   ③ 组件里没有方法、没有玩法数值：数值来自 Excel 生成物（规则4），行为在 System 的 for 循环里（J3）。
//
// 为什么所有组件都放在同一个文件：它们是一张契约表（ECS.md 第 4.2 节）的逐行落地，
// 放在一起可以让「新增一种组件要改几个文件」走查时一眼看出——答案是 0 个内核文件，
// 只需要在业务侧新加一个 struct（规则7）。若将来组件数量过百，再按用途拆文件。

/// <summary>
/// 世界变换：位置 / 旋转（四元数）/ 缩放。数值以 <see cref="System.Numerics.Vector3"/> 表达。
/// <para>
/// 为什么用 <c>System.Numerics</c> 而不是 Godot 的 <c>Vector3</c>：内核必须零 Godot 依赖（J4）。
/// 两者都是 12 字节、布局一致，宿主层（<c>TransformBridge</c>）做一次字段级转换即可，
/// 而且 <c>System.Numerics</c> 在 .NET 10 上已可用 SIMD 指令加速。
/// </para>
/// <para>
/// 为什么旋转用四元数：射击游戏到处是「朝向目标」的插值，欧拉角会万向锁；
/// 而四元数 Slerp 只需要 4 个 float 的连续内存，能整块交给向量化。
/// </para>
/// </summary>
public struct Transform3
{
    /// <summary>实体自引用（内核在生成/迁移时自动写入，系统只读）。</summary>
    [Identity] public Entity Owner;

    /// <summary>世界坐标（米）。</summary>
    public System.Numerics.Vector3 Position;

    /// <summary>旋转（四元数，已归一化）。</summary>
    public System.Numerics.Quaternion Rotation;

    /// <summary>缩放（默认全 1 由系统在生成时设置，不能放 default 里——default 是 0 会「消失」）。</summary>
    public System.Numerics.Vector3 Scale;
}

/// <summary>
/// 速度：线速度 + 角速度。为什么单独一个组件而不是塞进 <see cref="Transform3"/>：
/// 静止实体（建筑、掉落物）不会有速度，把运动学分量拆出来能让「只有 Transform3」的实体少占 16 字节，
/// 也让「移动系统」的查询（Velocity + Transform3）天然排除静态物。
/// </summary>
public struct Velocity
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>线速度（米/秒）。</summary>
    public System.Numerics.Vector3 Linear;

    /// <summary>角速度（弧度/秒，绕世界 Y 轴为主；三维自由翻滚时用完整轴角由系统展开）。</summary>
    public System.Numerics.Vector3 Angular;
}

/// <summary>
/// 阵营。字段类型是 <see cref="TeamId"/>（<c>Game.Core</c> 的 enum，由 ecs-core 提供）。
/// <para>
/// <b>为什么阵营是独立组件而不是塞进 Health</b>：命中判定要读阵营（<c>IsHostileTo</c>），
/// 而子弹、可摧毁场景物、护盾也都需要阵营，「有 Health 才有阵营」不成立。
/// </para>
/// </summary>
public struct Team
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>阵营；默认 <see cref="TeamId.Neutral"/>（忘填阵营的实体不会被误判成玩家）。</summary>
    public Game.Core.TeamId Value;
}

/// <summary>
/// 生命周期计时：存活时长（秒）与「是否只走固定步长」。
/// <para>
/// 为什么不用「销毁时间戳」而用「剩余秒数」：剩余秒数在 <c>Update</c> 里做一次减法即可，
/// 不需要每帧读时钟，也不受帧率抖动影响（固定步长下完全确定）。
/// </para>
/// </summary>
public struct Lifetime
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>剩余寿命（秒）。<= 0 时由 <c>LifetimeSystem</c> 投递销毁命令。</summary>
    public float Remaining;

    /// <summary>初始寿命（秒）；粒子/特效重播时用它复位，避免再查一次 Def。</summary>
    public float Duration;
}

/// <summary>
/// 生命值组件：当前值 / 最大值 / 是否已死标志。
/// <para>
/// <b>为什么不在这里放「护甲、减伤、暴击抗性」</b>：那些属于 <c>StatPackage</c>（规则6），
/// 通过 <see cref="StatLayerSlots"/> 按槽位引用。组件只留「会被每帧写」的数——
/// chunk 里的字节预算要留给热数据。
/// </para>
/// </summary>
public struct Health
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>当前血量（<= 0 视为死亡）。</summary>
    public float Current;

    /// <summary>血量上限（来自 Def，生成时写入）。</summary>
    public float Max;

    /// <summary>是否已被标记死亡（防止同一帧被多颗子弹重复结算死亡结果）。</summary>
    public bool IsDead;
}

/// <summary>
/// 数值包槽位：把「实体 → StatPackage 的哪一层」的引用压成 5 个 int（规则6 的接缝，见 ECS.md 第 4.3 节）。
/// <para>
/// <b>为什么是 5 个具名槽位而不是 <c>int[]</c> 或 <c>StatPackage</c> 引用</b>：
/// 引用会让组件失去 <c>unmanaged</c>（J2 一票否决）。5 个槽位正好覆盖规则6 的分层
/// （基础 / 成长 / 装备 / Buff / 临时），且每个都是 int——数值系统可以按「槽位 + 索引」查表，
/// 不需要实体持有任何托管对象。
/// </para>
/// <para>
/// <b>未使用的层填空串 id</b>：业务约定 0 = 无。这里不定义常量，避免与 ecs-core 的 StatId 表重复。
/// </para>
/// </summary>
public struct StatLayerSlots
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>基础层（角色 Def 的固定数值）槽位 id。</summary>
    public int BaseId;

    /// <summary>成长层（等级 / 品质成长）槽位 id。</summary>
    public int GrowthId;

    /// <summary>装备层槽位 id。</summary>
    public int EquipmentId;

    /// <summary>Buff 层槽位 id（词条、临时增益）。</summary>
    public int BuffId;

    /// <summary>临时层槽位 id（本帧结算用的修饰，帧末清空）。</summary>
    public int TemporaryId;
}

/// <summary>
/// 刚体运动状态：质量 / 阻力 / 是否受重力 / 是否贴地。
/// <para>
/// 为什么不由 Jolt 物理直接写这个组件：物理是表现层的「近似」，玩法结算必须能脱离物理独立跑
/// （回放、单测）。这里存的是<u>玩法侧</u>的运动参数，宿主层每帧把权威值同步给物理体，
/// 物理回调再写回位置——单向，避免物理抖动污染玩法确定性。
/// </para>
/// </summary>
public struct Rigid
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>质量（千克）；0 表示「无穷大」（不可被击退）。</summary>
    public float Mass;

    /// <summary>线性阻力系数（每秒衰减比例，由公式在系统里解释）。</summary>
    public float LinearDamping;

    /// <summary>是否受重力影响。</summary>
    public bool UseGravity;

    /// <summary>是否贴地（贴地实体不参与重力与坠落判定）。</summary>
    public bool IsGrounded;
}

// 注意：本文件曾有一个示例组件 struct Projectile（Owner/Source/Damage/PierceLeft/BounceLeft/Speed/Traveled）。
// 玩法子弹的组件是 Game.Combat.Projectile（scripts/combat/CombatComponents.cs），两者同名不同命名空间，
// 字段部分重叠会导致「查询选错也能编译」，已于 2026-11-05 删除死代码（规则7：不留重复定义）。

/// <summary>
/// 「夺舍」状态：玩家临时控制某个敌方实体的玩法状态（规则6 / ECS.md 第 4.2 节）。
/// <para>
/// 为什么需要一个显式组件而不是用「控制器 id」：夺舍期间实体需要同时保留原阵营、原数值、
/// 并接受玩家输入；这三个状态互相纠缠，显式化成组件后，系统只需要一次
/// <c>Query.Without&lt;TakeoverState&gt;()</c> 就能把「夺舍中的敌人」排除出 AI 系统。
/// </para>
/// </summary>
public struct TakeoverState
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>原属阵营（夺舍结束后恢复）。</summary>
    public Game.Core.TeamId OriginalTeam;

    /// <summary>剩余夺舍时长（秒，<= 0 表示无限，由规则决定）。</summary>
    public float RemainingSeconds;

    /// <summary>夺舍来源的实体（施放者）。</summary>
    public Entity Source;

    /// <summary>是否处于夺舍中（便于系统里做一次 bool 判定，而不是比对实体号）。</summary>
    public bool Active;
}

/// <summary>
/// 视图槽位：把「实体 → 表现层对象」的映射压成一个 int（<see cref="Game.Core.VisualSlot"/> 号段）。
/// <para>
/// <b>为什么组件里存 int 而不是 Node3D 引用</b>：J2 的一票否决项——组件持有 Node 会让 chunk
/// 被 GC 扫描、并让「数据层」反向依赖「引擎层」。宿主层的 <c>ViewRegistry</c> 用这个 int
/// 去自己的池里取节点；ECS 侧永远不知道 Node 的存在（J4）。
/// </para>
/// </summary>
public struct View
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>表现槽位号（0 = 无视图）。由宿主层的 <c>ViewRegistry</c> 分配与解释。</summary>
    public int Slot;

    /// <summary>视图种类（角色 / 抛射物 / 特效 / 场景物），宿主据此选池。</summary>
    public int Kind;

    /// <summary>是否需要在下一帧重建视图（换装、换模型）。</summary>
    public bool Dirty;
}

/// <summary>
/// 受击盒：可被命中的几何体与后处理（受击闪白、无敌帧、击退抗性）。
/// <para>
/// <b>为什么「是否有受击盒」决定能不能被打</b>：用组件存在性表达能力，是 ECS 的惯用手法——
/// 命中检测系统只查 <c>Query&lt;Hurtbox, Transform3&gt;()</c>，不需要任何 <c>if (kind == Boss)</c>
/// 之类的分支（J3 的反面）。
/// </para>
/// </summary>
public struct Hurtbox
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>判定半径（米，球体近似；胶囊体由系统按 <see cref="Height"/> 展开）。</summary>
    public float Radius;

    /// <summary>判定高度（米）；0 表示纯球体。</summary>
    public float Height;

    /// <summary>无敌帧剩余时长（秒，> 0 时忽略命中）。</summary>
    public float InvulnerableRemaining;

    /// <summary>受击闪白剩余时长（秒，供表现层读，逻辑层不解释）。</summary>
    public float FlashRemaining;

    /// <summary>击退抗性（0 = 完全不受击退，1 = 完整承受）。</summary>
    public float KnockbackResistance;
}

// ─────────────────────────────────────────────────────────────────
// 2026-11-05 hero-1（契约 docs/design/character-hud-contract.md §3）
// ─────────────────────────────────────────────────────────────────
//
// 为什么瞄准/移动意图是**官方组件**而不是角色域组件：它们描述的是「任何有位置的实体都可能收到的一种意图」——
// 炮塔、可被击退的道具、被 AI 接管的召唤物都会用到；而「谁在控制系统」才是角色域的事（ActorIdentity）。
// 放在官方组件表里，意味着将来「新增一种会瞄的实体」不需要改 scripts/actor/（规则7）。

/// <summary>
/// 瞄准目标：世界空间的点 + 方向（单位向量）。由宿主输入或 AI 写入，由瞄准/射击系统消费。
/// <para>
/// <b>为什么两点（点 + 方向）都存</b>：点用于「子弹该往哪飞」（鼠标射线与地面的交点），方向用于
/// 「模型该朝哪转」（也是弹道方向）；两者在垂直瞄准（打空中目标）时并不等价，合成一个字段会丢信息。
/// </para>
/// <para>
/// <b>为什么宿主能直接写这个组件而不用经过系统</b>：读鼠标射线需要引擎的射线求交（<c>J4</c>：只有宿主层能碰引擎），
/// 所以「光标 → 世界点」这一步必须发生在宿主层；系统只做<u>校验与平滑</u>（见 <c>AimAtCursorSystem</c>）。
/// 组件作为两条边的接缝，让宿主与系统之间只交换值，不交换控制流。
/// </para>
/// </summary>
public struct AimTarget
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>瞄准的世界坐标（米）。</summary>
    public System.Numerics.Vector3 Point;

    /// <summary>瞄准方向（单位向量；零向量表示"本帧没有有效瞄准"，系统会用上一帧 / 默认朝向兜底）。</summary>
    public System.Numerics.Vector3 Direction;

    /// <summary>
    /// 本帧是否有一个"新鲜"的瞄准目标（由写入方置 true，被消费后由系统清掉）。
    /// <para>为什么需要它：<see cref="Direction"/> 为 0 与「宿主这一帧没给瞄准」是不同的语义
    /// （前者是数据错误，后者是正常状态：鼠标离开窗口、手柄回中、AI 暂时没目标）。
    /// 用组件存在性表达不了「这一帧有没有更新」，所以留一个 bool，而不是让系统去猜。</para>
    /// </summary>
    public bool HasTarget;
}

/// <summary>
/// 移动意图：本帧<u>期望</u>的速度（米/秒，世界空间）。由输入/寻路写入，由移动系统解析成 <see cref="Velocity"/>。
/// <para>
/// <b>为什么意图与速度分开两个组件</b>：意图是「玩家/AI 想怎么动」，速度是「物理上实际怎么动」——
/// 后者还要叠加击退、惯性、地形限制、减速地带。若只留一个组件，那么「被击退时玩家仍能全速反向跑」
/// 这类手感问题就没有可写的落点（谁覆盖谁取决于系统顺序，不可推理）。分开后：
/// 意图 <c>MoveIntent</c>（输入写）→ <c>MoveAimSystem</c> 结合数值包算出 <c>Velocity</c> → <c>MovementSystem</c> 积分。
/// </para>
/// <para>
/// <b>为什么是速度而不是按键位掩码</b>：把"WASD 这个键"翻译成方向属于操作方案（手柄/键位/摇杆，
/// 宿主的输入包），系统只该看到已经合成的方向与大小。于是换一套手柄包、加一个"鼠标相对移动"的移动方式，
/// 系统一行不改（规则7）。
/// </para>
/// </summary>
public struct MoveIntent
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>期望速度（米/秒，世界空间；长度 &gt; 1 表示"加速"，由移动系统的数值上限决定是否封顶）。</summary>
    public System.Numerics.Vector3 Velocity;

    /// <summary>强度 0..1：用于"摇杆推一半就走一半"，与 <see cref="Velocity"/> 的长度解耦（键盘 = 1）。</summary>
    public float Magnitude;
}

/// <summary>
/// 视图外观：外观包编号 + 配色档位 + 缩放。表现层据此挑模型/材质；<b>逻辑层不解释它的内容</b>。
/// <para>
/// <b>为什么不复用官方 <see cref="View"/></b>：<see cref="View"/> 是「视图池的租约」（借了哪个节点，
/// 由桥在租还时写）；本组件是「长什么样」（配置），它在实体出生时就定了、整个生命周期基本不变。
/// 把两者混在一起会让桥在换装时必须交出池管理权，且在"没分配视图"时丢失外观信息。
/// </para>
/// <para>
/// <b>为什么叫 <c>ViewAppearance</c> 而不是契约里的 <c>ViewSlot</c></b>：
/// 本文件已经有一个 <see cref="Game.Core.VisualSlot"/>（枚举），把新组件也叫 <c>ViewSlot</c> 会让
/// <c>using Game.Core;</c> + <c>using Game.Core.Ecs;</c> 的文件里出现歧义（<c>ViewSlot.Body</c> vs
/// <c>ViewSlot.Owner</c>），而宿主视图桥恰好要同时用两者。改名只影响本文件的类型名，组件语义与契约 §3 一行未变。
/// </para>
/// <para>
/// <b>为什么外观 id 是 int 而不是字符串</b>：组件必须 unmanaged（J2）；外观包由宿主在装配期解析成
/// 槽位号写进来（同 <c>ActorIdentity</c> 的数值包槽位做法）。<see cref="VisualKey"/> 额外留一个稳定哈希，
/// 是为了「数据侧换了外观包 id 写法」时仍能按 key 复用视图（0 = 不用）。
/// </para>
/// </summary>
public struct ViewAppearance
{
    /// <summary>实体自引用。</summary>
    [Identity] public Entity Owner;

    /// <summary>外观包 id 的稳定哈希（0 = 未指定，桥退回按 <see cref="VisualSlotIndex"/> 选外观）。</summary>
    public int VisualKey;

    /// <summary>外观包在宿主外观注册表里的槽位号（-1 = 未解析）。</summary>
    public int VisualSlotIndex;

    /// <summary>配色档位号（表现层取色用；-1 = 默认配色）。</summary>
    public int PaletteSlot;

    /// <summary>视图缩放（相对预制体原始尺寸；1 = 原尺寸）。表现层专用，不参与任何判定。</summary>
    public float Scale;
}
