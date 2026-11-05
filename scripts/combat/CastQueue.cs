using Game.Core;
using Game.Core.Ecs;
using NVector3 = System.Numerics.Vector3;

namespace Game.Combat;

/// <summary>
/// 一次铸造请求（<b>命令载荷</b>，值类型）：任何系统都能投递，由 <see cref="CastSystem"/> 消费。
/// <para>
/// <b>为什么是队列而不是直接 <c>World.Create</c></b>：投递方可能在 chunk 遍历中被调用，
/// 结构化变更必须留到帧边界（J5）。队列就是这条「留到帧边界」的通道。
/// </para>
/// <para>
/// <b>为什么全部字段都是 unmanaged</b>：一次交火可能在一帧里投递上百条请求，
/// 每条都带字符串或列表就会让队列本身成为 GC 源。这里的 40 个字节可以整块 memcpy，入队 = 一次数组写。
/// </para>
/// </summary>
public readonly struct CastRequest
{
    /// <summary>「没有该槽位 / 没有该实体」的哨兵。</summary>
    public const int NoSlot = -1;

    /// <summary>发射者实体（伤害归属与数值包来源；可为 <see cref="Entity.None"/> = 环境铸造）。</summary>
    public readonly Entity Source;

    /// <summary>这次铸造归属于哪个阵营（子弹按它判敌我；夺舍后由调用方改）。</summary>
    public readonly TeamId Team;

    /// <summary>技能槽位（五个固定槽之一；只作诊断与冷却归属，不影响选物）。</summary>
    public readonly SkillSlot Slot;

    /// <summary>武器寄存器槽位号（<see cref="ContentRegistry.DeclareSlot"/> 返回的那个 int；&lt; 0 表示不指定，走默认武器）。</summary>
    public readonly int WeaponSlotId;

    /// <summary>配方 tag 下标（&lt; 0 表示没有配方，直接按武器契约默认产物铸造）。</summary>
    public readonly int RecipeTagIndex;

    /// <summary>铸造原点（枪口 / 生成点）。</summary>
    public readonly NVector3 Origin;

    /// <summary>瞄准方向（已归一化；由调用方保证，系统不再归一化以省一次开方）。</summary>
    public readonly NVector3 AimDirection;

    /// <summary>瞄准点（世界坐标；<see cref="WeaponAimKind.AimPoint"/> 类武器靠它定方向）。</summary>
    public readonly NVector3 AimPoint;

    /// <summary>发射它的武器实体（可为 <see cref="Entity.None"/>）；用于「子弹继承武器属性」的链式求值。</summary>
    public readonly Entity SourceWeapon;

    /// <summary>可用法力（门控在铸造侧一次性判定，避免下游再查一次数值包）。</summary>
    public readonly float ManaAvailable;

    /// <summary>熔炉链深度（顶层为 0；子弹再射子弹时由生成方 +1，用来防无限递归）。</summary>
    public readonly int ChainDepth;

    /// <summary>构造。</summary>
    public CastRequest(
        Entity source,
        in NVector3 origin,
        in NVector3 aimDirection,
        TeamId team = TeamId.Neutral,
        SkillSlot slot = SkillSlot.None,
        int weaponSlotId = NoSlot,
        int recipeTagIndex = NoSlot,
        in NVector3 aimPoint = default,
        Entity sourceWeapon = default,
        float manaAvailable = 0f,
        int chainDepth = 0)
    {
        Source = source;
        Team = team;
        Slot = slot;
        WeaponSlotId = weaponSlotId;
        RecipeTagIndex = recipeTagIndex;
        Origin = origin;
        AimDirection = aimDirection;
        AimPoint = aimPoint;
        SourceWeapon = sourceWeapon;
        ManaAvailable = manaAvailable;
        ChainDepth = chainDepth;
    }

    /// <summary>以「朝某个点」的形态构造（自动算方向；瞄准点在原点时退化为 +Z）。</summary>
    public static CastRequest Toward(
        Entity source,
        in NVector3 origin,
        in NVector3 aimPoint,
        TeamId team = TeamId.Neutral,
        SkillSlot slot = SkillSlot.None,
        int weaponSlotId = NoSlot,
        int recipeTagIndex = NoSlot,
        Entity sourceWeapon = default,
        float manaAvailable = 0f,
        int chainDepth = 0)
    {
        NVector3 dir = aimPoint - origin;
        float lengthSquared = dir.LengthSquared();
        dir = lengthSquared > 0f ? dir / System.MathF.Sqrt(lengthSquared) : NVector3.UnitZ;
        return new CastRequest(source, in origin, in dir, team, slot, weaponSlotId, recipeTagIndex,
            in aimPoint, sourceWeapon, manaAvailable, chainDepth);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("CastRequest(weapon=", WeaponSlotId.ToString(), ", recipe=", RecipeTagIndex.ToString(),
            ", depth=", ChainDepth.ToString(), ")");
}

/// <summary>
/// 铸造请求队列：<b>环形缓冲</b>而不是 <c>Queue&lt;T&gt;</c>。
/// <para>
/// <b>为什么要自己写一个</b>：<c>Queue&lt;T&gt;</c> 在扩容、以及「队列短暂清空后重新入队」时会重新分配内部数组；
/// 割草局面向 200 敌人发射 500 抛射物，每秒几千次入队出队，哪怕偶尔一次扩容都会给 GC 递刀。
/// 环形缓冲在构造时一次分配、之后指针绕圈，<b>稳态 0 次分配</b>。
/// </para>
/// <para>
/// <b>为什么满了是"拒绝入队 + 计数"而不是扩容</b>：容量给 1024 已经远超一帧的正常铸造量；
/// 满了意味着上游逻辑失控（比如一个死循环在投递），此时安静丢请求并计数，比悄悄扩容成 100MB 更容易发现。
/// </para>
/// </summary>
public sealed class CastQueue
{
    private readonly CastRequest[] _items;
    private int _head;
    private int _count;
    private int _dropped;

    /// <summary>构造（容量在构造期定死）。</summary>
    public CastQueue(int capacity = 256)  // arch-ok: 预分配容量，结构性常量而非玩法数值
    {
        if (capacity <= 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(capacity), capacity, "铸造队列容量必须为正。");
        }
        _items = new CastRequest[capacity];
    }

    /// <summary>当前待处理请求数。</summary>
    public int Pending => _count;

    /// <summary>因队列满被丢弃的请求累计数（调试面板用；恒为 0 才是健康的）。</summary>
    public int Dropped => _dropped;

    /// <summary>队列容量。</summary>
    public int Capacity => _items.Length;

    /// <summary>投递一条请求（队列满返回 false 并计数）。</summary>
    public bool Enqueue(in CastRequest request)
    {
        if (_count >= _items.Length)
        {
            _dropped++;
            return false;
        }
        _items[(_head + _count) % _items.Length] = request;
        _count++;
        return true;
    }

    /// <summary>取出队首；无请求返回 false。</summary>
    public bool TryDequeue(out CastRequest request)
    {
        if (_count == 0)
        {
            request = default;
            return false;
        }
        request = _items[_head];
        _head = (_head + 1) % _items.Length;
        _count--;
        return true;
    }

    /// <summary>清空（重开一局）。</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
        _dropped = 0;
    }
}
