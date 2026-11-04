using System;

namespace Game.Core.Ecs;

/// <summary>
/// 结构化命令缓冲：帧内所有「生成 / 销毁 / 增删组件」都写在这里，帧末一次提交（J5 的执行方式）。
/// <para>
/// <b>为什么必须缓冲</b>：遍历中途改结构会让正在被遍历的 chunk 数组失效（swap-remove / 迁移）。
/// 缓冲把「决策」与「执行」拆开：系统在 <c>ref</c> / Span 里算完就写下命令，帧末由
/// <c>CommandFlushSystem</c> 在<u>没有遍历进行中</u>的时候统一执行。
/// </para>
/// <para>
/// <b>为什么按「写入顺序」执行而不是分类执行</b>：分类执行（先生成、再销毁、最后改组件）会让
/// 「销毁后又生成」的语义不可推理。按序执行 + 冲突静默跳过 = 读自己的代码就能推断结果。
/// </para>
/// <para>
/// <b>为什么冲突是「静默跳过 + 计数」而不是抛异常</b>：帧末提交时「目标实体已被同批命令销毁」
/// 既合法又常见（两颗子弹同帧打死同一个怪）。抛异常会中断整帧；静默并把次数记在
/// <see cref="SkippedCommandCount"/>，既能跑完，又能让测试断言「跳过数符合预期」。
/// </para>
/// <para>
/// <b>为什么能提前拿到 Entity</b>：<see cref="CreateDeferred"/> / <see cref="Create{T}"/> 入队时就向世界
/// <u>预留实体号</u>，于是系统可以在后续命令里引用「还没生成出来的实体」（开火 + 立刻给子弹挂标记）。
/// 命令按写入顺序执行，引用因此可靠。
/// </para>
/// <para>
/// <b>组件值怎么放进去而不装箱</b>：命令本身是值类型数组；组件值写进「按组件分桶的强类型值队列」
/// （由 <see cref="World"/> 持有），命令只存一个 int 下标。队列复用、帧末清空，稳态零分配。
/// </para>
/// <para>
/// <b>⚠ 预留实体的可见性</b>：对「已预留但未提交」的实体调用 <see cref="Set{T}"/> / <see cref="Remove{T}"/>
/// 会返回「冲突跳过」（因为它此刻还不是活实体）。要给新实体带初值，请用 <see cref="Create{T}"/>，
/// 或先用 <see cref="CreateDeferred"/> 生成、下一帧再 Set。
/// </para>
/// </summary>
public sealed class CommandBuffer
{
    private readonly World _world;
    private Command[] _commands;
    private int _count;
    private int _flushCount;
    private int _skipped;
    private int _lastExecuted;

    /// <summary>当前待提交的命令数。</summary>
    public int PendingCount => _count;

    /// <summary>累计提交次数（诊断用）。</summary>
    public int FlushCount => _flushCount;

    /// <summary>累计因冲突被静默跳过的命令数（测试断言「冲突计数」用）。</summary>
    public int SkippedCommandCount => _skipped;

    /// <summary>最近一次提交实际执行的命令数。</summary>
    public int LastExecutedCount => _lastExecuted;

    internal CommandBuffer(World world)
    {
        _world = world;
        _commands = new Command[EcsConfig.InitialCommandCapacity];
    }

    /// <summary>
    /// 延迟创建一个实体（帧末 <see cref="Flush"/> 时才真正落进原型），返回已预留的实体号。
    /// <para>组件初值全为 default；需要初值请用 <see cref="Create{T}(in T)"/>。</para>
    /// </summary>
    public Entity CreateDeferred(System.Collections.Generic.IReadOnlyList<ComponentType> components)
    {
        if (components is null)
        {
            throw new GameEcsException("CommandBuffer.CreateDeferred 的组件列表不能为 null。");
        }
        Entity entity = _world.ReserveEntity(components);
        TypeSet set = _world.TypeSetOf(components);
        Write(new Command(CommandKind.Create, entity, set, NoComponent, NoValue));
        return entity;
    }

    /// <summary><see cref="CreateDeferred"/> 的别名（ECS.md 第 5.5 节的原名）。</summary>
    public Entity Create(System.Collections.Generic.IReadOnlyList<ComponentType> components) => CreateDeferred(components);

    /// <summary>创建一个带单个组件初值的实体（最常见形态：Create 一颗带 Projectile 的子弹）。</summary>
    public Entity Create<T>(in T value) where T : unmanaged
    {
        ComponentType type = _world.Register<T>();
        Entity entity = _world.ReserveEntity(CommandBufferSingletons.Of(type));
        TypeSet set = _world.TypeSetOf(CommandBufferSingletons.Of(type));
        Write(new Command(CommandKind.Create, entity, set, NoComponent, NoValue));
        Write(new Command(CommandKind.SetValue, entity, default, type.Id, _world.EnqueueCommandValue(type.Id, value)));
        return entity;
    }

    /// <summary>销毁实体（帧末执行；若已被同批命令销毁则静默跳过）。</summary>
    public void Destroy(Entity entity)
    {
        Write(new Command(CommandKind.Destroy, entity, default, NoComponent, NoValue));
    }

    /// <summary>
    /// 添加组件（等价于「必须没有时才添加」，已有同名组件则覆盖）。
    /// <para>与 <see cref="Set{T}"/> 的差别只在语义表达：<c>Add</c> 明确表示「这是新挂上去的」，
    /// 便于读代码；实现上复用同一条 <see cref="CommandKind.SetValue"/> 命令。</para>
    /// </summary>
    public void Add<T>(Entity entity, in T value) where T : unmanaged => Set(entity, value);

    /// <summary>有则更新、无则添加（帧末执行）。</summary>
    public void Set<T>(Entity entity, in T value) where T : unmanaged
    {
        ComponentType type = _world.Register<T>();
        int valueIndex = _world.EnqueueCommandValue(type.Id, value);
        Write(new Command(CommandKind.SetValue, entity, default, type.Id, valueIndex));
    }

    /// <summary>移除组件（帧末执行；实体已销毁或没有该组件时静默跳过）。</summary>
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        ComponentType type = _world.Register<T>();
        Write(new Command(CommandKind.Remove, entity, default, type.Id, NoComponent));
    }

    /// <summary>
    /// 提交：按<u>写入顺序</u>逐条执行，执行期间世界临时开放结构化变更。
    /// <para>提交完成后命令数组清空并复用（零分配）；返回值 = 实际执行条数。</para>
    /// <para>
    /// <b>为什么执行期间还可能有新命令入队</b>：某条命令执行触发的后续逻辑可能再入队（生成后立刻加组件）。
    /// 这里用「滑动窗口」按序消费直到队列不再增长，并用 <see cref="EcsConfig.MaxCommandFlushDepth"/>
    /// 兜底，防止自增命令无限循环（那会导致本帧卡死）。
    /// </para>
    /// </summary>
    public int Flush()
    {
        if (_count == 0)
        {
            _lastExecuted = 0;
            return 0;
        }
        _flushCount++;
        bool previous = _world.IsFlushingCommands;
        _world.IsFlushingCommands = true;
        int executed = 0;
        try
        {
            int processed = 0;
            int guard = 0;
            int maxCommands = _commands.Length * EcsConfig.MaxCommandFlushDepth;
            while (processed < _count)
            {
                Command command = _commands[processed];
                processed++;
                if (Execute(command))
                {
                    executed++;
                }
                guard++;
                if (guard > maxCommands)
                {
                    throw new GameEcsException(
                        "CommandBuffer 提交时命令数量异常增长（疑似命令提交里无限入队新命令），已中止本帧提交。");
                }
            }
        }
        finally
        {
            _world.IsFlushingCommands = previous;
            _world.ClearCommandValues();
            _count = 0;
        }
        _lastExecuted = executed;
        return executed;
    }

    /// <summary>丢弃所有未提交命令（关卡切换 / 世界重建时调用）。</summary>
    public void Clear()
    {
        _world.ClearCommandValues();
        _count = 0;
    }

    /// <summary>清空并重置统计（<see cref="World.Dispose"/> 用）。</summary>
    internal void Reset()
    {
        _world.ClearCommandValues();
        _count = 0;
        _skipped = 0;
        _lastExecuted = 0;
        _flushCount = 0;
    }

    private void Write(in Command command)
    {
        if (_count == _commands.Length)
        {
            Array.Resize(ref _commands, _commands.Length * 2);
        }
        _commands[_count++] = command;
    }

    /// <summary>执行一条命令。返回 true = 真正改了世界；false = 被跳过（冲突）。</summary>
    private bool Execute(in Command command)
    {
        switch (command.Kind)
        {
            case CommandKind.Create:
                if (!_world.ExecuteDeferredCreate(command.Entity, command.Components))
                {
                    _skipped++;
                    return false;
                }
                return true;

            case CommandKind.Destroy:
                if (!_world.Destroy(command.Entity))
                {
                    _skipped++;
                    return false;
                }
                return true;

            case CommandKind.Remove:
                if (!_world.RemoveById(command.Entity, command.ComponentId))
                {
                    _skipped++;
                    return false;
                }
                return true;

            case CommandKind.SetValue:
                if (!_world.ExecuteDeferredSet(command.Entity, command.ComponentId, command.ValueIndex))
                {
                    _skipped++;
                    return false;
                }
                return true;

            default:
                _skipped++;
                return false;
        }
    }

    /// <summary>「无组件」哨兵（命令里不涉及组件 id 时填它）。</summary>
    private const int NoComponent = -1;

    /// <summary>「无值」哨兵（命令里不涉及组件值时填它）。</summary>
    private const int NoValue = -1;
}

/// <summary>单元素组件列表的复用容器：避免 <c>Create&lt;T&gt;</c> 每次 <c>new[] { type }</c> 分配。</summary>
internal static class CommandBufferSingletons
{
    [ThreadStatic] private static ComponentType[]? _buffer;

    /// <summary>把 <paramref name="type"/> 装进本线程复用的单元素列表。</summary>
    public static System.Collections.Generic.IReadOnlyList<ComponentType> Of(ComponentType type)
    {
        ComponentType[] buffer = _buffer ??= new ComponentType[1];
        buffer[0] = type;
        return buffer;
    }
}

/// <summary>命令种类（值类型命令的判别字段）。</summary>
internal enum CommandKind
{
    /// <summary>创建实体（组件初值 default）。</summary>
    Create = 0,

    /// <summary>销毁实体。</summary>
    Destroy = 1,

    /// <summary>移除组件。</summary>
    Remove = 2,

    /// <summary>有则更新、无则添加组件值。</summary>
    SetValue = 3,
}

/// <summary>一条结构化命令（值类型、不含托管引用，装进数组零分配）。</summary>
internal readonly struct Command
{
    public readonly CommandKind Kind;
    public readonly Entity Entity;
    public readonly TypeSet Components;
    public readonly int ComponentId;
    public readonly int ValueIndex;

    public Command(CommandKind kind, Entity entity, TypeSet components, int componentId, int valueIndex)
    {
        Kind = kind;
        Entity = entity;
        Components = components;
        ComponentId = componentId;
        ValueIndex = valueIndex;
    }
}
