using System;

namespace Game.Core;

/// <summary>
/// Game.Core 层的**统一异常类型**：核心基础设施（对象池、事件总线、随机源、以及下层 ECS 内核）唯一允许抛出的异常类型。
/// <para>为什么不直接抛 <see cref="InvalidOperationException"/> / <see cref="ArgumentException"/>：</para>
/// <list type="bullet">
/// <item>捕获方可以 <c>catch (GameCoreException)</c> 一把兜住「我们自己写的、可预期的编程错误」，
/// 而不会误吞 BCL 或引擎抛出的异常；</item>
/// <item>异常里带**错误码**（<see cref="Code"/>）与**上下文**（<see cref="Context"/>），
/// 排查时不用猜「是哪一层的哪个实体」；</item>
/// <item>ECS 内核的 <c>GameEcsException</c> 继承自本类型 —— 全工程只有**两个**异常家族，
/// 不像「每种误用一个异常类」那样越滚越多。</item>
/// </list>
/// <para>纪律：<b>只在编程错误时抛</b>（配置写错、结构误用）。可预期的失败一律 <c>bool TryXxx</c>
/// （docs/rules/03 §4.2 第 4 条）；热路径上的「找不到 / 已销毁」绝不能靠异常做控制流。</para>
/// </summary>
public sealed class GameCoreException : Exception
{
    /// <summary>错误码枚举：把「为什么炸」变成机器可判定的短标识，测试可以断言具体码而不是字符串。</summary>
    public enum ErrorCode
    {
        /// <summary>未分类。</summary>
        Unknown = 0,

        /// <summary>参数非法（null、越界、区间反了）。</summary>
        InvalidArgument = 1,

        /// <summary>对象池误用：重复归还、归还非本池实例、容量为 0。</summary>
        PoolMisuse = 2,

        /// <summary>事件总线误用：订阅代际非法、发布在遍历期间改订阅等。</summary>
        EventBusMisuse = 3,

        /// <summary>随机源误用：区间为负、种子非法。</summary>
        RandomMisuse = 4,

        /// <summary>ECS 结构误用（Get 未继承的组件、job 内做结构变更、系统排序成环）。由 Ecs 子类使用。</summary>
        EcsMisuse = 5,
    }

    /// <summary>机器可判定的错误类别。</summary>
    public ErrorCode Code { get; }

    /// <summary>出错位置的上下文（实体 / 组件 / 系统 / 池名），用于日志；为空表示未提供。</summary>
    public string Context { get; }

    /// <summary>构造：把所有信息装配成一条**中文可读**的消息，同时在属性里保留结构化的 <see cref="Code"/> 与 <see cref="Context"/>。</summary>
    /// <param name="code">错误类别。</param>
    /// <param name="context">出错上下文，例如 <c>"ObjectPool&lt;Bullet&gt;：重复归还"</c>。</param>
    /// <param name="message">人读的说明（中文）。</param>
    /// <param name="inner">内部异常（可选）。</param>
    public GameCoreException(ErrorCode code, string context, string message, Exception? inner = null)
        : base(Compose(code, context, message), inner)
    {
        Code = code;
        Context = context ?? string.Empty;
    }

    private static string Compose(ErrorCode code, string? context, string? message)
    {
        string ctx = context ?? string.Empty;
        string msg = message ?? string.Empty;
        // 格式：[Game.Core][错误类别] 上下文 —— 说明
        return "[Game.Core][" + code.ToString() + "] " + ctx + " - " + msg;
    }
}
