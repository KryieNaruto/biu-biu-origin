namespace Game.Core;

/// <summary>
/// 伤害类型。是 <c>DamageApplied</c> 事件与 <c>Weapons.DamageType</c> 列（docs/rules/04 §6.2）的共同类型。
/// <para>为什么放在 <c>Game.Core</c>：它是纯逻辑枚举，<c>Game.Combat</c> / <c>Game.Data</c> / UI 只读视图都要用，
/// 下沉到最底层后谁也不用为了一个标签去依赖 <c>Game.Combat</c>。</para>
/// <para>具体数值只与「抗性公式按类型查表」有关，公式来自 Excel（规则4），所以这里只有标签、没有倍率。</para>
/// </summary>
public enum DamageType : byte
{
    /// <summary>无伤害类型（治疗、纯击退等不需要走抗性的结算）。0 必须是安全默认值。</summary>
    None = 0,

    /// <summary>动能：子弹、撞击。</summary>
    Kinetic = 1,

    /// <summary>能量：光束、爆能。</summary>
    Energy = 2,

    /// <summary>火焰：灼烧、爆炸。</summary>
    Fire = 3,

    // 新伤害类型只能追加在末尾；Excel 的 enum 列按名映射，数值变 = 存档里旧伤害标签错位。
}

/// <summary>伤害类型的语义查询：把「哪些类型吃元素抗性」这类判定收在一处，避免在结算里写 <c>switch</c>。</summary>
public static class DamageTypeExtensions
{
    /// <summary>是否元素伤害（走元素抗性表）。判定改变时只改这里，结算管线与 UI 同时生效。</summary>
    public static bool IsElemental(this DamageType type) => type == DamageType.Energy || type == DamageType.Fire;
}
