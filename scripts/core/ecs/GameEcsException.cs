using System;
using System.Collections.Generic;

namespace Game.Core.Ecs;

/// <summary>
/// 内核唯一异常类型（ECS.md 第 5.6 节）。结构误用（Get 未继承组件、job 内做结构化变更、
/// 系统排序成环、[Identity] 字段不合法……）一律抛它。
/// <para>
/// <b>为什么只要一个异常类型</b>：ECS 内核的错误都是「代码写错了」，宿主要么在开发期炸掉、
/// 要么根本不该捕获（捕获后继续跑只会让错误扩散）。单一类型让 <c>catch (GameEcsException)</c>
/// 的边界非常清楚：整层逻辑只有它一种可预期的失败。
/// </para>
/// <para>
/// <b>为什么继承 <see cref="InvalidOperationException"/> 而不是 <see cref="Exception"/></b>：
/// 「对一个已销毁的实体读写」「遍历中途改结构」本质都是非法操作顺序，
/// 用 BCL 里语义最近的基类能让 IDE 与日志工具更准确地提示。
/// </para>
/// <para>
/// <b>信息用中文</b>：这个项目的人工排查者是中文母语；异常信息必须能直接读完并定位到
/// 「哪个实体 / 哪个组件 / 哪个系统」，所以不写英文通用串。
/// </para>
/// </summary>
public sealed class GameEcsException : InvalidOperationException
{
    /// <summary>用一句中文说明构造异常。</summary>
    public GameEcsException(string message)
        : base(message)
    {
    }

    /// <summary>带内部异常构造（例如把反射期的 <see cref="MissingMemberException"/> 包一层）。</summary>
    public GameEcsException(string message, Exception inner)
        : base(message, inner)
    {
    }

    // ── 下面三个工厂方法把「错误信息格式」收敛到一处：
    //    各个调用点只给原始事实（实体、组件名、系统名），格式漂移只会在这一处以一处的方式被修好。

    /// <summary>「实体已销毁 / 是旧句柄」。</summary>
    public static GameEcsException DeadEntity(Entity entity, string operation)
    {
        return new GameEcsException(
            string.Concat("ECS 操作失败：对已有实体做 ", operation, " 时发现它已销毁或句柄过期（", entity.ToString(), "）。",
                "跨帧缓存的句柄必须每帧用 World.IsAlive 复核。"));
    }

    /// <summary>「实体没有继承这个组件」。</summary>
    public static GameEcsException MissingComponent(Entity entity, Type componentType, string operation)
    {
        return new GameEcsException(
            string.Concat("ECS 操作失败：", operation, " 要求实体 ", entity.ToString(), " 继承组件 ",
                Describe(componentType), "，但它没有；请先用 World.Has&lt;", Describe(componentType), "&gt; 判定。"));
    }

    /// <summary>「组件类型不满足内核要求」（不是 unmanaged struct、[Identity] 形状不对等）。</summary>
    public static GameEcsException InvalidComponent(Type componentType, string reason)
    {
        return new GameEcsException(
            string.Concat("ECS 组件校验失败：", Describe(componentType), " -> ", reason));
    }

    /// <summary>「系统图成环」。</summary>
    public static GameEcsException Cycle(IReadOnlyList<Type> cycle)
    {
        return new GameEcsException(
            string.Concat("ECS 系统排序失败：分组内存在循环依赖（", DescribeCycle(cycle),
                "）；请删掉 [UpdateAfter]/[UpdateBefore] 环上任意一条边。"));
    }

    /// <summary>「在遍历/Job 回调里做了结构化变更」。</summary>
    public static GameEcsException InsideIteration(string operation, Entity entity)
    {
        return new GameEcsException(
            string.Concat("ECS 结构变更被拒绝：", operation, "（", entity.ToString(),
                "）发生在 Query 遍历或 Job 回调内部；请改写成 World.Commands.XXX，帧末统一提交。"));
    }

    // ── 格式化帮手（只在抛异常路径上跑，分配无所谓）。 ──

    private static string Describe(Type t) => t is null ? "<null>" : t.Name;

    private static string DescribeCycle(IReadOnlyList<Type> cycle)
    {
        if (cycle is null || cycle.Count == 0)
        {
            return "<空>";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < cycle.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" -> ");
            }
            sb.Append(cycle[i].Name);
        }
        return sb.ToString();
    }
}
