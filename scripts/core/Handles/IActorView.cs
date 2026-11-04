using Game.Core.Ecs;

namespace Game.Core.Handles;

/// <summary>
/// 跨层传递的**只读实体视图**：`Game.Core` 唯一允许暴露的「一个东西」的形状。
/// <para>为什么需要它而不是直接传 <see cref="Entity"/>：</para>
/// <list type="bullet">
/// <item><see cref="Entity"/> 属于 <c>Game.Core.Ecs</c> 内核，UI 层按 J4 只许依赖 <c>Game.Core</c>；
/// 让 UI 直接引 <c>Game.Core.Ecs</c> 会把「UI 可以读 World」的门打开（ECS.md §8「UI」一行明令禁止）。</item>
/// <item>句柄是**语义**（「某个可被瞄准的东西」），实体键是**实现**（槽位 + 世代）。
/// 将来换存储策略（分块世界、网络 id）时，只换 <see cref="ActorHandle"/> 的实现，不动订阅方。</item>
/// </list>
/// <para>生命周期纪律（ECS.md §5.1「句柄规则」）：句柄只在**本帧/下一帧**内有效；
/// 跨帧缓存必须每帧复核（见 <see cref="Actors.TryResolve"/>）。</para>
/// </summary>
public interface IActorView
{
    /// <summary>是否指向一个真实存在的句柄（<see cref="ActorHandle.None"/> 返回 false）。</summary>
    bool IsNone { get; }

    /// <summary>槽位号。只用于日志与排序；SQL 之外不要拿它做身份判断（会被复用）。</summary>
    int Index { get; }

    /// <summary>世代号：槽位被复用后它会变，用于识别「旧句柄」。0 表示空。</summary>
    int Generation { get; }
}
