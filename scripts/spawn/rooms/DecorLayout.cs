using System;

namespace Game.Spawn.Rooms;

/// <summary>
/// 房间**装饰件**：把一个 <see cref="DecorArchetypeId"/> 摆到房间里的某个位置。
/// 装饰是纯表现的（柱子、箱子、篝火），但**摆放规则属于玩法**（别堵门、别压刷怪点），
/// 所以它和其它内容一样经过槽位约束，而不是在 Host 里随手撒。
/// </summary>
public readonly struct DecorPlacement
{
    /// <summary>所属房间。</summary>
    public RoomId Room { get; init; }

    /// <summary>装饰模板编号（→ 素材/模型 ID，由 Host 的解释表翻译成 Path）。</summary>
    public DecorArchetypeId Archetype { get; init; }

    /// <summary>房间局部 X（格）。</summary>
    public float LocalX { get; init; }

    /// <summary>房间局部 Z（格）。</summary>
    public float LocalZ { get; init; }

    /// <summary>绕 Y 轴的旋转（度，0..360）。用 float 角度而不是四元数：这是数据，不是变换。</summary>
    public float YawDegrees { get; init; }

    /// <summary>缩放系数（1 = 原尺寸；由装饰模板推荐的米制尺寸反算，宿主解释）。</summary>
    public float Scale { get; init; }

    /// <summary>是否贴墙（贴墙件需要按墙面向量对齐）。</summary>
    public bool AgainstWall { get; init; }

    /// <inheritdoc />
    public override string ToString() => "Decor " + Room + " " + Archetype + " ("
        + LocalX.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ","
        + LocalZ.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ")";
}

/// <summary>
/// 装饰**布局器**：为房间图挑选并摆放装饰件。纯规算，不碰引擎。
/// <para>
/// 规则7：新增一种装饰 = 数据里加一行（<c>DecorArchetypeId</c> + 权重 + 贴墙/落地属性），
/// 本接口的实现<b>0 代码改动</b>。只有「新的摆放规则」（例如「墙面自动生成踢脚线」）才需要一个新实现。
/// </para>
/// </summary>
public interface IRoomDecorLayout
{
    /// <summary>布局器名。</summary>
    string Name { get; }

    /// <summary>
    /// 为整层房间摆放装饰。
    /// </summary>
    /// <param name="layout">地牢图。</param>
    /// <param name="slots">内容槽（装饰必须避让它们）。</param>
    /// <param name="seed">本层种子（内部 Fork 子流）。</param>
    /// <param name="capacity">输出缓冲容量。</param>
    DecorPlacement[] Layout(DungeonLayout layout, ReadOnlySpan<ContentSlot> slots, int seed, int capacity);
}

/// <summary>
/// 装饰登记表：<see cref="DecorArchetypeId"/> → 摆放属性。与 <see cref="RoomCategoryTable"/> 同一范式：
/// 规划器只认表，不认具体模型。
/// </summary>
public sealed class DecorCatalog
{
    private readonly DecorArchetypeSpec[] _specs;

    /// <summary>构造。</summary>
    /// <param name="specs">全部装饰规格。</param>
    public DecorCatalog(DecorArchetypeSpec[] specs)
    {
        _specs = specs ?? throw new ArgumentNullException(nameof(specs));
    }

    /// <summary>全部规格（只读）。</summary>
    public System.Collections.Generic.IReadOnlyList<DecorArchetypeSpec> Specs => _specs;

    /// <summary>按编号查规格。</summary>
    /// <param name="id">装饰编号。</param>
    /// <param name="spec">规格。</param>
    public bool TryGet(DecorArchetypeId id, out DecorArchetypeSpec spec)
    {
        for (int i = 0; i < _specs.Length; i++)
        {
            if (_specs[i].Id == id)
            {
                spec = _specs[i];
                return true;
            }
        }
        spec = default;
        return false;
    }
}

/// <summary>装饰规格（数据行）。</summary>
public readonly struct DecorArchetypeSpec
{
    /// <summary>装饰编号。</summary>
    public DecorArchetypeId Id { get; init; }

    /// <summary>摆放层次：落地 / 贴墙 / 吊顶（贴墙与落地共享「避让刷怪点」约束）。</summary>
    public DecorPlacementLayer Layer { get; init; }

    /// <summary>抽取权重（0 = 只被显式引用）。</summary>
    public int Weight { get; init; }

    /// <summary>允许出现在哪些房间类别的位掩码（<see cref="RoomCategoryMask"/>）。</summary>
    public uint CategoryMask { get; init; }

    /// <summary>水平安全半径（格）：与刷怪点/门/其它装饰的最小间距。</summary>
    public float Clearance { get; init; }

    /// <summary>推荐的缩放范围下限。</summary>
    public float ScaleMin { get; init; }

    /// <summary>推荐的缩放范围上限。</summary>
    public float ScaleMax { get; init; }

    /// <inheritdoc />
    public override string ToString() => "DecorSpec " + Id + " " + Layer + " w=" + Weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>装饰摆放层。</summary>
public enum DecorPlacementLayer : byte
{
    /// <summary>落地物件（箱子、篝火、碎石）。</summary>
    Ground = 1,

    /// <summary>贴墙物件（火把、壁挂、旗帜）。</summary>
    Wall = 2, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值

    /// <summary>吊顶/高挂物件（吊灯、蛛网）。</summary>
    Ceiling = 3, // arch-ok: 枚举成员显式赋值（存档/回放按数值还原，只加不改），不是玩法数值
}

/// <summary>房间类别位掩码工具（纯整数运算，可进数据）。</summary>
public static class RoomCategoryMask
{
    /// <summary>把类别转成位。</summary>
    /// <param name="category">类别。</param>
    public static uint Bit(RoomCategory category) => 1u << (int)category;

    /// <summary>掩码是否包含该类别。</summary>
    /// <param name="mask">掩码。</param>
    /// <param name="category">类别。</param>
    public static bool Contains(uint mask, RoomCategory category) => (mask & Bit(category)) != 0;
}
