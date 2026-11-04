namespace Game.Core;

/// <summary>
/// 数值包（<c>StatPackage</c>）的**层枚举**。顺序本身就是契约：<c>Fold</c> 必须按声明顺序折叠。
/// <para>为什么在 <c>Game.Core</c> 而不是 docs/rules/05 原写的 <c>Game.Data</c>：
/// ECS 的 <c>StatLayerSlots</c> 组件只存 <c>int</c> 槽位、不直接引用本枚举，但
/// <c>IStatProvider.GetLayer(StatLayer, ...)</c> 出现在 <c>Game.Data</c> 接口上，
/// 而 <c>Game.Data</c> 允许依赖 <c>Game.Core</c>、反之不行 —— 把它放在最底层，
/// 将来 <c>Game.UI</c> / <c>Game.Host</c> 用同一个序号时不会被迫引入 <c>Game.Data</c>。
/// 这是「把接口与其值类型下沉到能被依赖的最底层」（docs/rules/03 §4.1.2）的一次应用。</para>
/// <para>底层类型 <c>byte</c>：层数永远是「几个」，用 byte 省内存，也顺手把「层数 &lt; 256」变成编译期事实。</para>
/// </summary>
public enum StatLayer : byte
{
    /// <summary>基础层：来自 <c>CharacterDef.Base</c>。夺舍替换的就是这一层。</summary>
    Base = 0,

    /// <summary>成长层：等级、升级选择、永久词条。夺舍时**保留**（玩家的成长永远是玩家的）。</summary>
    Growth = 1,

    /// <summary>装备层：武器/护甲/饰品/遗物合成后的一个包。夺舍时**保留**。</summary>
    Equipment = 2,

    /// <summary>增益层：可叠加的 buff 栈。夺舍时按 <c>BuffDef.KeepOnPossess</c> 决定。</summary>
    Buff = 3,

    /// <summary>临时层：限时技能修饰、场景效果。夺舍时**保留**。</summary>
    Temporary = 4,

    // 新层只能追加在 Temporary 之后（5、6…），禁止插入中间 —— 会让既有公式与存档错位。
}

/// <summary>
/// 层标识：同一层可能有多个来源（如多个 Buff），用 <c>(Layer, SlotId)</c> 唯一标识。
/// <para>为什么是 <c>readonly record struct</c>：它是事件 <c>LayersChanged</c> 的载荷、也是存档的键，
/// 需要值语义相等（两个 <c>(Buff,"poison")</c> 必须相等）。<c>SlotId</c> 默认空串而不是 null ——
/// 让「无槽位的单例层」与「有槽位的栈层」共用同一类型，调用方不用写 <c>null</c> 分支。</para>
/// </summary>
public readonly record struct StatLayerId(StatLayer Layer, string SlotId = "")
{
    /// <summary>基础层的固定标识（无槽位）。</summary>
    public static readonly StatLayerId BaseLayer = new(StatLayer.Base, "");

    /// <summary>是否基础层。基础层恒为 1 个，槽位语义对它没有意义。</summary>
    public bool IsBase => Layer == StatLayer.Base;
}
