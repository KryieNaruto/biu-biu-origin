using Game.Core.Ecs;

namespace Game.Core.Handles;

/// <summary>
/// 句柄工具：跨帧缓存的句柄在使用前必须复核（ECS.md §5.1「句柄规则」）。
/// <para>为什么把复核做成一个类型而不是让调用方自己写 <c>world.IsAlive(h.Id)</c>：</para>
/// <list type="bullet">
/// <item>「句柄是否过期」的判据将来可能变化（加入关卡/世界归属校验），收口后只改一处；</item>
/// <item>调用方写法统一成 <c>if (Actors.TryResolve(world, handle, out Entity e))</c>，
/// 代码评审时一眼能看出「这里做了复核」，而不是逐处判断有没有漏。</item>
/// </list>
/// <para>纪律：<c>TryResolve</c> **不抛异常**、不分配；过期句柄是正常运行状态（子弹飞出后目标炸了），
/// 不是错误。</para>
/// </summary>
public static class Actors
{
    /// <summary>
    /// 把句柄解析回实体键。返回 false 表示句柄已过期（实体被销毁 / 槽位被复用 / 句柄为空）。
    /// </summary>
    /// <param name="world">当前世界（唯一能回答「这个世代号还活着吗」的地方）。</param>
    /// <param name="handle">要复核的句柄。</param>
    /// <param name="entity">解析成功时的实体键。</param>
    public static bool TryResolve(World world, ActorHandle handle, out Entity entity)
    {
        entity = handle.Id;
        if (world is null || handle.IsNone)
        {
            return false;
        }

        return world.IsAlive(entity);
    }

    /// <summary>实体是否存活（不关心具体是谁）。与 <see cref="TryResolve"/> 二选一，别两套都写。</summary>
    public static bool IsAlive(World world, ActorHandle handle) => TryResolve(world, handle, out _);

    /// <summary>由实体键包装成句柄。总长度 0 行逻辑，但把「句柄怎么造」收口成唯一入口。</summary>
    public static ActorHandle FromEntity(Entity entity) => new(entity);
}
