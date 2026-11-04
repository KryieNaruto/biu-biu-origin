using Game.Core.Ecs;

namespace Game.Core.Handles;

/// <summary>
/// 句柄：所有跨层传递的「某个东西」都是它（ECS.md §5.1 的 <c>readonly record struct ActorHandle(Entity)</c>）。
/// <para>为什么是 <c>readonly record struct</c> 而不是 class：</para>
/// <list type="bullet">
/// <item>值语义 → 事件载荷、<c>Dictionary</c> 键、数组元素都能直接放，不需要池也不需要装箱；</item>
/// <item>8 字节（<c>Entity</c> = 两个 int）→ 传参走寄存器，热路径零成本；</item>
/// <item><c>record struct</c> 自动给相等的 <c>Equals</c>/<c>GetHashCode</c> 与 <c>==</c>，不会漏写。</item>
/// </list>
/// <para>为什么内部包 <see cref="Entity"/> 而不是重复两个 int：世代号校验逻辑只写一遍
/// （<see cref="Entity"/> 里），句柄负责把「UI 能看见的那层」露出去。</para>
/// <note>ECS.md §5.1 原文把 ActorHandle 画在 <c>Game.Core.Ecs</c> 命名空间里，落地时放到
/// <c>Game.Core.Handles</c>：因为它要同时被 UI/事件/系统族引用，而这些层不允许 <c>using Game.Core.Ecs</c>
/// （见 <see cref="IActorView"/> 的注释）。签名不变，只是命名空间更广。</note>
/// </summary>
public readonly record struct ActorHandle(Entity Id) : IActorView
{
    /// <summary>
    /// 空句柄：不指向任何实体。事件里表示「没有来源 / 没有击杀者」。
    /// <para><b>为什么是 <c>new(Entity.None)</c> 而不是 <c>default</c>：</b><c>default(Entity)</c> 的
    /// <c>Index</c> 是 <b>0</b>（CLR 零初始化），而 <see cref="Entity.IsNone"/> 的判据是 <c>Index &lt; 0</c>——
    /// 于是 <c>default</c> 会被当成「槽位 0 的活实体」，这是本项目实测踩到过的真 bug。
    /// <see cref="Entity.None"/> 才是内核约定的空值（<c>EntityIndex.NoneIndex == -1</c>）。
    /// 别"优化"回 <c>default</c>。</para>
    /// </summary>
    public static ActorHandle None => new(Entity.None);

    /// <inheritdoc />
    public bool IsNone => Id.IsNone;

    /// <inheritdoc />
    public int Index => Id.Index;

    /// <inheritdoc />
    public int Generation => Id.Generation;

    /// <summary>调试用短串（沿用 <see cref="Entity"/> 的 E12:3 格式，方便与 ECS 日志对照）。</summary>
    public override string ToString() => Id.ToString();
}
