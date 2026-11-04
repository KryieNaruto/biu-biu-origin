using System;
using System.Collections.Generic;

namespace Game.Core.Pool;

/// <summary>
/// 统一对象池：**只池化引用类型**（<c>class</c>），值类型永远不需要池（它们的分配在栈上）。
/// <para>为什么必须池：docs/rules/02 §3.3 把「每帧 new（类）」列为禁用项 ——
/// 割草局面向 200+ 敌人发射 500+ 抛射物，每帧都 new 会让 GC 抖动直接体现在帧时间上。
/// 对象池把「稳态分配」压到 0。</para>
/// <para>为什么用「栈」而不是「队列」：后进先出对 CPU cache 更友好（刚归还的对象大概率还在 L1），
/// 而且顺序与「租借深度」一致（递归租借时归还顺序天然对齐）。</para>
/// <para>为什么池里存 <see cref="IPoolable"/> 而不是 <c>T</c>：省掉每次 <c>Rent</c> 的强制转换
/// （虽然 JIT 会消掉大部分，但接口调用是确定的），且池本身不需要知道 <c>T</c> 的字段布局。</para>
/// <para>线程：**非线程安全，只在主线程用**。这与 ECS 的「结构化变更只在主线程」同一条纪律。</para>
/// </summary>
/// <typeparam name="T">池化成员类型：引用类型 + 实现 <see cref="IPoolable"/>。</typeparam>
public sealed class ObjectPool<T> where T : class, IPoolable
{
    private readonly Func<T> _factory;
    private readonly Stack<T> _free;
    private readonly int _maxRetained;
    private readonly string _name;

    private int _rented;

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="factory">创建新实例的工厂。必须是**无副作用**的构造（真正的初始化在 <see cref="IPoolable.OnRent"/>）。
    /// 用委托而不是 <c>new()</c> 约束：外部需要注入构造函数参数（如节点路径、配置），约束会把它堵死。</param>
    /// <param name="prewarm">预热数量：提前创建并清洗 N 个实例，避免开局第一波掉帧。</param>
    /// <param name="maxRetained">池内最多保留多少个空闲实例（超出部分在归还时直接丢弃，交给 GC）。
    /// 这是**内存上界**而不是行为开关：设太大 = 泄漏内存，设太小 = 稳态仍有分配。</param>
    /// <param name="name">池名，只用于异常信息，便于定位是哪个池出错。</param>
    public ObjectPool(Func<T> factory, int prewarm = 0, int maxRetained = 1024, string name = "")
    {
        _factory = factory ?? throw new GameCoreException(
            GameCoreException.ErrorCode.InvalidArgument, "ObjectPool", "factory 不能为 null");

        if (prewarm < 0)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.InvalidArgument, "ObjectPool", "prewarm 不能为负");
        }

        if (maxRetained < 0)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.InvalidArgument, "ObjectPool", "maxRetained 不能为负");
        }

        _free = new Stack<T>(prewarm);
        _maxRetained = maxRetained;
        _name = string.IsNullOrEmpty(name) ? typeof(T).Name : name;

        Prewarm(prewarm);
    }

    /// <summary>当前空闲实例数（调试 HUD / 测试断言用）。</summary>
    public int FreeCount => _free.Count;

    /// <summary>当前已租出且未归还的实例数（调试 HUD 用）。</summary>
    public int RentedCount => _rented;

    /// <summary>
    /// 租借一个实例：从空闲栈顶取出（或新建），并调用 <see cref="IPoolable.ResetState"/> + <see cref="IPoolable.OnRent"/>。
    /// <para>为什么租借时也调 <see cref="IPoolable.ResetState"/>：预热实例与「丢弃后重建」的实例状态保证一致，
    /// 调用方无需知道对象是新的还是复用的 —— 这是「复用与新建等价」的实现依据。</para>
    /// </summary>
    public T Rent()
    {
        T item;
        if (_free.Count > 0)
        {
            item = _free.Pop();
            item.ResetState();
        }
        else
        {
            item = CreateNew();
        }

        item.OnRent();
        _rented++;
        return item;
    }

    /// <summary>
    /// 归还原实例：调用 <see cref="IPoolable.OnReturn"/> 后放回空闲栈。
    /// <para>重复归还 / 归还外来实例**直接抛异常**而不是静默忽略：这是典型的双释放 bug，
    /// 静默会让同一对象被两处同时使用，症状极难复现。开发期就炸掉最便宜。</para>
    /// </summary>
    public void Return(T item)
    {
        if (item is null)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.PoolMisuse, _name, "归还不能是 null");
        }

        item.OnReturn();

        if (_rented <= 0)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.PoolMisuse, _name, "归还次数多于租借次数（疑似重复归还）");
        }

        _rented--;

        if (_free.Count >= _maxRetained)
        {
            // 超出上界：丢弃，交给 GC。池的职责是「吸收波动」，不是「无限缓存」。
            return;
        }

        _free.Push(item);
    }

    /// <summary>批量预热：补齐到 <paramref name="count"/> 个空闲实例（已够则什么都不做）。开局或切场景时调用。</summary>
    public void Prewarm(int count)
    {
        while (_free.Count < count)
        {
            _free.Push(CreateNew());
        }
    }

    /// <summary>清空空闲实例（**不影响已租出实例**，租出的实例仍必须归还才算数）。切关卡/卸载资源时调用。</summary>
    public void Clear()
    {
        _free.Clear();
    }

    private T CreateNew()
    {
        T created = _factory();
        if (created is null)
        {
            throw new GameCoreException(
                GameCoreException.ErrorCode.InvalidArgument, _name, "factory 返回了 null");
        }

        created.ResetState();
        return created;
    }
}
