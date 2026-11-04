using System;

namespace Game.Core.Ecs;

/// <summary>
/// 系统的抽象基类：一个系统 = 一个小类 + 手写 <c>for</c> 循环（ECS.md 第 5.4 节）。
/// <para>
/// <b>为什么是类而不是 interface</b>：系统的生命周期是「构造（建 Query）→ 每帧 OnExecute」，
/// 需要在构造期缓存 Query 与委托、在帧间持有计数器。这些都依赖状态。
/// 同时系统本身<u>不继承任何引擎类型</u>（零 Godot 依赖，J4），所以它是一个干净的纯 C# 类，
/// 可以在没有引擎的测试里直接 new 出来跑（ECS.md 第 10 节）。
/// </para>
/// <para>
/// <b>为什么 OnExecute 是 abstract 而不是 virtual</b>：一个系统没有「默认行为」，
/// 强制实现比让它默认空转、然后在调试时发现某个系统什么都没干要安全。
/// </para>
/// <para>
/// <b>写法示例</b>（新增一个系统 = 1 个文件，规则7）：
/// <code>
/// [UpdateInGroup(UpdateGroup.FixedUpdate)]
/// [UpdateAfter(typeof(SpawnSystem))]
/// public sealed class MoveSystem : SystemBase, ISpanAction&lt;Velocity, Transform3&gt;
/// {
///     private Query _query = null!;
///     protected override void OnInitialize() =&gt; _query = World.Query&lt;Velocity, Transform3&gt;();
///     protected override void OnExecute(float dt) =&gt; _query.ForEach(in this);
///     public void Execute(Span&lt;Velocity&gt; v, Span&lt;Transform3&gt; t, int n)
///     {
///         for (int i = 0; i &lt; n; i++) { t[i].Position += v[i].Linear * dt; }
///     }
/// }
/// </code>
/// </para>
/// </summary>
public abstract class SystemBase
{
    private World? _world;
    private bool _initialized;
    private bool _enabled = true;

    /// <summary>该系统所属的世界（<see cref="SystemPipeline.Add"/> 时注入）。</summary>
    public World World => _world
        ?? throw new GameEcsException(
            string.Concat("系统 ", GetType().Name, " 尚未加入 SystemPipeline，无法访问 World。请先 pipeline.Add(system)。"));

    /// <summary>系统名（默认类型名；调试面板与异常信息用）。</summary>
    public virtual string Name => GetType().Name;

    /// <summary>是否启用（禁用后 pipeline 跳过它，但保留排序位置）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>已初始化标志（pipeline 的 <see cref="SystemPipeline.Initialize"/> 调用一次）。</summary>
    public bool IsInitialized => _initialized;

    /// <summary>注入世界并调用一次 <see cref="OnInitialize"/>。由 pipeline 调用，业务侧不要手动调。</summary>
    internal void Attach(World world)
    {
        if (_world is not null && !ReferenceEquals(_world, world))
        {
            throw new GameEcsException(
                string.Concat("系统 ", Name, " 已属于另一个 World；一个系统实例不能跨世界复用（请每世界各建一份）。"));
        }
        _world = world;
    }

    /// <summary>初始化（建 Query / 缓存委托）。pipeline 在首次 Execute 前调用一次。</summary>
    internal void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }
        if (_world is null)
        {
            throw new GameEcsException(string.Concat("系统 ", Name, " 未加入任何 World，无法初始化。"));
        }
        _initialized = true;
        OnInitialize();
    }

    /// <summary>建 Query、缓存委托的时机（只会被调用一次）。</summary>
    protected virtual void OnInitialize()
    {
    }

    /// <summary>每帧执行本体。系统必须在里面手写 <c>for</c> 循环，不允许每实体回调。</summary>
    protected abstract void OnExecute(float deltaTime);

    /// <summary>由 pipeline 调用：处理启用标志后转调 <see cref="OnExecute"/>。</summary>
    internal void Tick(float deltaTime)
    {
        if (!_enabled)
        {
            return;
        }
        OnExecute(deltaTime);
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
