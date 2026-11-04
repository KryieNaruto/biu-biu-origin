namespace Game.Core;

/// <summary>
/// 外观槽位：<c>IVisualDescriptor.ApplySlot(VisualSlot, CharacterDef)</c> 的槽位参数，
/// 也是 Excel <c>Effects.AttachSlot</c> 列的 enum（docs/rules/04 §6.4）。
/// <para>为什么是 enum 而不是 string：docs/rules/03 §4.1.1.1（Lead 仲裁 #5）已裁定强类型入参 ——
/// 字符串拼错只能运行期炸，而 enum 传错编译不过。</para>
/// <para>为什么放在 <c>Game.Core</c>：它是纯标签值，<c>Game.Actor</c> 的接口与 <c>Game.Host</c> 的视图桥都要用，
/// 放最底层可以避免为了让桩实现编译而反向依赖 <c>Game.Actor</c>。</para>
/// </summary>
public enum VisualSlot : byte
{
    /// <summary>无槽位（不挂特效 / 不换装）。0 = 安全默认值。</summary>
    None = 0,

    /// <summary>身体：整只怪物的主外观。夺舍换的就是这个槽位。</summary>
    Body = 1,

    /// <summary>头部/头盔。</summary>
    Head = 2,

    /// <summary>枪口：枪口火焰、曳光的挂点。</summary>
    Muzzle = 3,

    /// <summary>命中点：命中特效挂点。</summary>
    Hit = 4,

    /// <summary>脚部：落地/移动尘土特效挂点。</summary>
    Feet = 5,
}

/// <summary><see cref="VisualSlot"/> 的查询扩展：槽位数量是「结构性常量」，供数组预分配与校验使用。</summary>
public static class VisualSlotExtensions
{
    /// <summary>真实槽位个数（不含 <see cref="VisualSlot.None"/>）：视图桥按它预分配槽位数组。</summary>
    public const int Count = 5;

    /// <summary>是否手部挂点（枪口与命中点跟随武器的变换，身体/头部/脚跟随角色根变换）。</summary>
    public static bool IsHandAttach(this VisualSlot slot) => slot == VisualSlot.Muzzle || slot == VisualSlot.Hit;
}
