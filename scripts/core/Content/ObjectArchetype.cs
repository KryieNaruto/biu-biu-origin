using System;
using Game.Core.Actors;

namespace Game.Core.Content;

/// <summary>
/// 物件的**能力位**：这个 Def 能扮演哪些角色。
/// <para>
/// <b>为什么是位掩码而不是「四个类」</b>：本轮核心抽象是「人物 / 怪物 / 武器 / 子弹不是四个类，
/// 而是同一个 <see cref="ObjectArchetype"/>」。用位掩码表达后，
/// <c>Actor | Weapon</c>（会开枪的怪）、<c>Weapon | Projectile</c>（自己飞出枪口的枪）、
/// <c>Projectile | Actor</c>（子弹就是怪物）都只是<b>表的取值</b>，
/// 而系统侧只需要问「你有 Weapon 位吗」，永远不需要问「你是什么类」—— 组合数从 N 种类型降为 2^7 种能力组合，
/// 且新增组合 0 行代码（规则7）。
/// </para>
/// <para>
/// 显式给值、只加不改：位值会被写入生成物与存档，改动会让已发布内容错位。
/// </para>
/// </summary>
[Flags]
public enum ObjectCapability : ushort
{
    /// <summary>没有任何能力（数据错误或纯表现物件）。</summary>
    None = 0,

    /// <summary>可以作为一个「实体」被生成并拥有数值包（人物 / 怪物 / 召唤物 / 可破坏物）。</summary>
    Actor = 1 << 0,

    /// <summary>可以被发射：携带 <see cref="WeaponContract"/> 开火。</summary>
    Weapon = 1 << 1,

    /// <summary>可以作为一个飞行物件存在：携带 <see cref="ProjectileContract"/>。</summary>
    Projectile = 1 << 2,

    /// <summary>可以持续产出子物件（枪口、刷怪口、被动光环）。</summary>
    Emitter = 1 << 3,

    /// <summary>可以被事件触发（踩到地板、进入子弹判定区、计时到点）。</summary>
    Trigger = 1 << 4,

    /// <summary>可被消耗（弹药、拾取物、一次性道具）。</summary>
    Consumable = 1 << 5,

    /// <summary>静态结构（墙、箱子、掩体）：有受击盒、不移动。</summary>
    Structure = 1 << 6,
}

/// <summary>
/// 物件的**引用槽位**：一个 Def 指向另一个 Def 时用的值类型。
/// <para>
/// <b>为什么不是直接持有 <see cref="ObjectArchetype"/> 引用</b>：
/// </para>
/// <list type="bullet">
/// <item>Def 是 Excel 生成物，可能互相引用成环（子弹射出物体、物体再发射子弹）——
/// 直接持有对象引用会让加载期构造出环并阻断热重载与循环引用检测；</item>
/// <item>引用必须是可存档、可比较、可进 ECS 组件的（J2：组件只能放 int），所以用 <see cref="DefId"/>；</item>
/// <item><see cref="Weight"/> 让「熔炉随机挑一个子弹」变成<b>数据</b>：权重是策划在表里填的，
/// 随机选择的实现只需要读它，不需要知道子弹的类别。</item>
/// </list>
/// <para>
/// <see cref="DefId"/> 的值是目标 <see cref="ObjectArchetype.IdHash"/>（稳定 FNV-1a 哈希），
/// 于是查表不需要字符串哈希：热路径用 int、调试时再用 <see cref="Id"/> 找回字符串。
/// </para>
/// </summary>
public readonly struct ContentRef : IEquatable<ContentRef>
{
    /// <summary>
    /// 空引用的字典哨兵值。**注意：不靠这个值判断空引用** —— 稳定哈希是 32 位有符号数，
    /// 真实 id 完全可能哈希成负数（例如 <c>bullet_pistol</c> 就是负数），所以「负数 = 空」是错的。
    /// 空引用的唯一判据是 <see cref="HasTarget"/>（由构造函数置位）。
    /// </summary>
    public const int None = -1;

    /// <summary>目标 Def 的稳定哈希（= <see cref="ObjectArchetype.IdHash"/>）。</summary>
    public readonly int DefId;

    /// <summary>随机选择权重（熔炉组合时按它抽；≤0 表示「不参与随机，只能被显式指定」）。</summary>
    public readonly float Weight;

    /// <summary>目标 Def 的字符串主键（仅调试与表校验用；解析走 <see cref="DefId"/>）。</summary>
    public readonly string Id;

    /// <summary>是否真的指向某个 Def（唯一判据；<c>default(ContentRef)</c> 为 false）。</summary>
    public readonly bool HasTarget;

    /// <summary>构造一个引用（<paramref name="hasTarget"/> = false 时表示空引用）。</summary>
    public ContentRef(int defId, float weight, string id = "", bool hasTarget = true)
    {
        DefId = defId;
        Weight = weight;
        Id = id ?? string.Empty;
        HasTarget = hasTarget;
    }

    /// <summary>空引用（没有目标）。</summary>
    public static ContentRef Empty => default;

    /// <summary>是否为空引用（没有目标）。</summary>
    public bool IsEmpty => !HasTarget;

    /// <summary>由字符串主键构造（构建期用：加载器 / 测试里手写内容时）。</summary>
    public static ContentRef To(string id, float weight = 1f) =>
        string.IsNullOrEmpty(id)
            ? Empty
            : new ContentRef(ContentHash.Fnv1a32(id), weight, id);

    /// <inheritdoc />
    public bool Equals(ContentRef other) =>
        HasTarget == other.HasTarget && DefId == other.DefId && Weight.Equals(other.Weight) && Id == other.Id;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContentRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(DefId, Weight, Id, HasTarget);

    /// <summary>值相等。</summary>
    public static bool operator ==(ContentRef a, ContentRef b) => a.Equals(b);

    /// <summary>值不等。</summary>
    public static bool operator !=(ContentRef a, ContentRef b) => !a.Equals(b);

    /// <inheritdoc />
    public override string ToString() => IsEmpty ? "Ref(empty)" : string.Concat("Ref(", Id.Length > 0 ? Id : DefId.ToString(), " w=", Weight.ToString("0.##"), ")");
}

/// <summary>
/// 内容主键的**稳定哈希**（FNV-1a 32 位）。
/// <para>
/// <b>为什么 Def 既存字符串又存哈希</b>：字符串给人看（表 / 日志 / 报错），
/// 哈希给热路径用（每帧比较、字典键、进 ECS 组件）；FNV-1a 实现简单、无分配、跨平台一致，
/// 所以生成物与运行时算出来的值永远相同（换运行时 / 换进程不影响内容 id）。
/// </para>
/// <para>碰撞概率对「几千条内容」量级可以忽略；真撞了会在加载期由表校验按字符串报出来。</para>
/// </summary>
public static class ContentHash
{
    /// <summary>FNV-1a 32 位偏移基准（算法常量，不是游戏数值）。</summary>
    private const int OffsetBasis = unchecked((int)2166136261u);

    /// <summary>FNV-1a 32 位质数（算法常量，不是游戏数值）。</summary>
    private const uint Prime = 16777619u;

    /// <summary>计算字符串的稳定哈希；<c>null</c> 当作空串。</summary>
    public static int Fnv1a32(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return OffsetBasis;
        }
        unchecked
        {
            uint hash = (uint)OffsetBasis;
            for (int i = 0; i < text.Length; i++)
            {
                // 按字符的低字节混入（同 .NET 内置 <c>System.HashCode</c> 的 char 混法，也与常见 FNV-1a 在 ASCII 上一致）：
                // 实现简单、无分配、跨平台稳定，生成物（数据管线）与运行时算出来的数永远相同。
                hash = (hash ^ (byte)(text[i] & 0xFF)) * Prime;
            }
            return (int)hash;
        }
    }
}

/// <summary>
/// <b>物体原型</b>：本轮抽象的「一行 Excel」= 一个人 / 怪 / 武器 / 子弹 / 陷阱 / 可破坏物。
/// <para>
/// <b>为什么人物、怪物、武器、子弹共用同一个类型</b>：
/// </para>
/// <list type="bullet">
/// <item>玩法上它们本来就互相转化：子弹可以带武器属性、武器可以射出怪物、子弹自己也可以是怪物、
/// 怪物可以被熔炉折成武器。用四个类 + 继承，每多一种转化就要加一组 <c>is/as</c>；</item>
/// <item>用「同一个结构 + 能力位 + 标签 + 位矩阵」后，任何一种组合都是表的取值，
/// 系统侧只问「你有哪几个位」，于是「新增一种物件」= Excel 一行（规则7）；</item>
/// <item>数值全部走同一套五层数值包（规则6），所以「怪物被夺舍成玩家」「子弹继承武器伤害」都是换包 / 读包，不是换类型。</item>
/// </list>
/// <para>
/// <b>只放数据</b>（规则：Def 是纯数据契约，不许有行为方法）：这里既没有 <c>Tick</c> 也没有 <c>switch</c>，
/// 组合逻辑全部体现在字段形状（能力位 / 子引用 / 位矩阵）里，由系统族与规则解释器消费。
/// </para>
/// </summary>
public sealed class ObjectArchetype
{
    /// <summary>族槽位「无」。用 -1 与各 Registry 的 <c>NoneSlot</c> 保持一致。</summary>
    public const int NoCluster = -1;

    /// <summary>
    /// 视觉槽位「未解析」：内容表没填 <c>VisualPack</c> 时用 -1，<b>不能用 0</b>。
    /// <para>
    /// <b>为什么 0 是个坑</b>：0 是一个合法的外观槽位号（宿主外观注册表从 0 开始编号）。
    /// 用 0 当"没配"的默认值，会让「忘了配外观」与「配的是第 0 号外观」在运行期完全无法区分 ——
    /// 表现层只能猜，结果就是实体要么画成别人的样子，要么静默隐身。
    /// 用 -1 之后，「有意为白模」与「忘了配」在组件里是两个不同的值，可被断言。
    /// </para>
    /// </summary>
    public const int UnresolvedVisualSlot = -1;

    /// <summary>构造一个物件原型（字段极多且全部只读，全部走构造器以免出现「半初始化」的 Def）。</summary>
    public ObjectArchetype(
        string id,
        ObjectCapability capabilities = ObjectCapability.None,
        int clusterSlot = NoCluster,
        bool isAuthoritative = true,
        bool isAdaptive = false,
        in StatPackageIds stats = default,
        string renderId = "",
        float radius = 0f,
        float height = 0f,
        in ContentRef weapon = default,
        in ContentRef projectile = default,
        in ContentRef actor = default,
        in ContentRef effect = default,
        int tags = 0,
        string displayKey = "",
        int visualSlot = UnresolvedVisualSlot,
        WeaponContract? weaponContract = null,
        ProjectileContract? projectileContract = null,
        ObjectBitMatrix? bitMatrix = null,
        string hitboxAnchor = "",
        string visualPackId = "")
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("ObjectArchetype.Id 不能为空：它是 Excel 主键，也是热路径哈希的来源。", nameof(id));
        }

        Id = id;
        IdHash = ContentHash.Fnv1a32(id);
        DisplayKey = displayKey ?? string.Empty;
        Capabilities = capabilities;
        ClusterSlot = clusterSlot;
        IsAuthoritative = isAuthoritative;
        IsAdaptive = isAdaptive;
        Stats = stats;
        RenderId = renderId ?? string.Empty;
        Radius = radius;
        Height = height;
        Weapon = weapon;
        Projectile = projectile;
        Actor = actor;
        Effect = effect;
        Tags = tags;
        VisualSlot = visualSlot;
        VisualPackId = visualPackId ?? string.Empty;
        WeaponContract = weaponContract;
        ProjectileContract = projectileContract;
        BitMatrix = bitMatrix;
        HitboxAnchor = hitboxAnchor ?? string.Empty;
    }

    /// <summary>物件定义 id（Excel 主键，如 <c>bullet_pistol_basic</c>）。</summary>
    public string Id { get; }

    /// <summary>定义 id 的稳定哈希（<see cref="ContentHash.Fnv1a32"/>；热路径比较与 <see cref="ContentRef.DefId"/> 用）。</summary>
    public int IdHash { get; }

    /// <summary>显示名 key（本地化 key，不存中文字面量）。</summary>
    public string DisplayKey { get; }

    /// <summary>能力位：这个 Def 能扮演哪些角色（可同时是武器 + 发射器 + 子弹）。</summary>
    public ObjectCapability Capabilities { get; }

    /// <summary>
    /// 族 / 可替换簇：同一簇的原型可以被熔炉互换（"把手枪换成步枪"= 同一个簇内换行）。
    /// <see cref="NoCluster"/> = 不参与互换。
    /// </summary>
    public int ClusterSlot { get; }

    /// <summary>
    /// 是否**权威**物件：权威 = 真实存在、占实体槽、可被存档的物体；
    /// 非权威 = 由熔炉按配方生成 / 派生出来的物体（例如喷出的弹幕、临时召唤），可以不入存档、可被批量回收。
    /// </summary>
    public bool IsAuthoritative { get; }

    /// <summary>
    /// 是否**自适应模板**：表里缺的字段按同簇 / 默认推导（给策划「只填三列就能试玩」的自由），
    /// 而不是报数据错误。自适应 = 可以运行，但校验器会把它标成「待补全」。
    /// </summary>
    public bool IsAdaptive { get; }

    /// <summary>数值包组合（五层）：人物 / 怪物 / 武器 / 子弹走同一套（规则6）。</summary>
    public StatPackageIds Stats { get; }

    /// <summary>
    /// 表现资源 id（视图槽位池 / 模型 id；逻辑层不知道它长什么样）。
    /// <para>
    /// <b>它不是路径</b>：只是一串标识符（如 <c>capsule_player</c> / <c>orb_small</c>），
    /// 全工程没有任何代码能靠它找到磁盘上的模型文件。要找模型请看
    /// <see cref="VisualPackId"/> —— 那是唯一的入口。
    /// </para>
    /// </summary>
    public string RenderId { get; }

    /// <summary>
    /// 外观包 id（指向 <c>VisualPacks.Id</c>）。<b>这是模型的唯一入口</b>。
    /// <para>
    /// <b>为什么只留这一个字段</b>：模型路径、缩放、色调、动画剪辑全部收进外观包，
    /// 物件行只说「我用哪个包」。三种写法并存（包 / 直连路径 / 都不填）会让「这个物件
    /// 用什么模型」有多个答案，也必然长出「有的有、有的没有」——2026-11-09 已收敛掉
    /// <c>AssetPath</c> / <c>AssetAnim</c> 两个旁路字段，全工程只有这一条路。
    /// </para>
    /// <para>
    /// <b>特效怎么办</b>：特效的外观由表现层（shader / 粒子）自己决定，物件行填
    /// <c>vp_none</c>（<c>ModelPath</c> 留空）——「有意没有模型」是一个被显式表达的取值，
    /// 而不是一个空缺，这样它和「忘了填」在数据上永远可区分。
    /// </para>
    /// <para>
    /// <b>逻辑层不读它</b>：它只是给宿主层选模型用的字符串，不参与任何玩法结算，
    /// 也不改变 <see cref="IdHash"/> / <see cref="Capabilities"/>。
    /// </para>
    /// </summary>
    public string VisualPackId { get; }

    /// <summary>碰撞 / 视觉半径（数值本身来自表；这里只是从表搬过来的常量，不参与规则求值）。</summary>
    public float Radius { get; }

    /// <summary>碰撞 / 视觉高度。</summary>
    public float Height { get; }

    /// <summary>子物件引用：它射出的武器（怪物持枪、武器嵌套武器）。</summary>
    public ContentRef Weapon { get; }

    /// <summary>子物件引用：它发射的投射物（武器 → 子弹）。</summary>
    public ContentRef Projectile { get; }

    /// <summary>子物件引用：它生成的实体（子弹命中后变成怪物、召唤物）。</summary>
    public ContentRef Actor { get; }

    /// <summary>子物件引用：它的表现特效（命中 / 销毁 / 拾取）。</summary>
    public ContentRef Effect { get; }

    /// <summary>标签位集（tag id → bit；tag 名字见 <see cref="ContentRules.TagNames"/>）。代码里不出现任何具体 tag。</summary>
    public int Tags { get; }

    /// <summary>视图槽位号（表现层选模型用，与 <see cref="RenderId"/> 二选一或并用）。</summary>
    public int VisualSlot { get; }

    /// <summary>武器契约（<see cref="ObjectCapability.Weapon"/> 位有效时才有意义；否则为 <c>null</c>）。</summary>
    public WeaponContract? WeaponContract { get; }

    /// <summary>投射物契约（<see cref="ObjectCapability.Projectile"/> 位有效时才有意义；否则为 <c>null</c>）。</summary>
    public ProjectileContract? ProjectileContract { get; }

    /// <summary>该物件自己的位矩阵行（属性 × 标签网格；空 = 没有额外组合规则）。</summary>
    public ObjectBitMatrix? BitMatrix { get; }

    /// <summary>
    /// 受击盒锚点（骨骼 / 挂点名，空 = 用物件原点）：命中的是"手里那把枪的枪管"还是"身体"，
    /// 靠它而不是靠代码里的分支。
    /// </summary>
    public string HitboxAnchor { get; }

    /// <summary>是否具备某个能力位（系统族只用这一句做筛选，不写 <c>switch(type)</c>）。</summary>
    public bool HasCapability(ObjectCapability capability) => (Capabilities & capability) == capability;

    /// <summary>是否有某个标签（tag 来自 <see cref="ContentRules.TagOf"/>；越界 tag 恒为 false）。</summary>
    public bool HasTag(int tag) => tag >= 0 && tag < ContentBitLimits.TagBits && (Tags & (1 << tag)) != 0;

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("Archetype(", Id, ", ", Capabilities.ToString(), ", cluster=", ClusterSlot.ToString(), ")");
}

/// <summary>
/// 物件标签位集的**布局常量**（不是游戏数值，也不含任何具体 tag）：
/// 一个 <see cref="ObjectArchetype.Tags"/> 是 32 位，能表达的标签上限就是 32。
/// <para>为什么不用 <c>long</c>：位矩阵一行 32 个 tag 已经够一张表的列宽；
/// 需要更多时应当<b>拆表</b>（按族拆成多张位矩阵），而不是把单个字段撑宽 —— 撑宽会让存档与 C++ 侧布局一起变。</para>
/// </summary>
public static class ContentBitLimits
{
    /// <summary>物件标签位集的位宽（32）。</summary>
    public const int TagBits = 32;
}
