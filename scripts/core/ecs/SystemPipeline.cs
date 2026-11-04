using System;

namespace Game.Core.Ecs;

/// <summary>
/// 系统管线：把系统按 <see cref="UpdateInGroupAttribute"/> 分组、组内按 <see cref="UpdateAfterAttribute"/> /
/// <see cref="UpdateBeforeAttribute"/> 做拓扑排序，一帧按 PreUpdate → FixedUpdate → Update → LateUpdate
/// 依次执行（ECS.md 第 5.4 / 6 节）。
/// <para>
/// <b>为什么排序在注册期一次算好</b>：系统图是静态的（特性写死在类型上），每帧重算拓扑毫无意义。
/// <see cref="Build"/> 之后 <see cref="Execute"/> 只是一个扁平 for，零分配、可预测。
/// </para>
/// <para>
/// <b>为什么同优先级必须「稳定」</b>：两个系统之间没有显式依赖时，若顺序随运行而变，
/// 回放会漂移、bug 会变成「偶现」。这里取 Kahn 算法、每次从入度归零的候选里挑
/// <u>注册顺序最小</u>的那个，于是「无依赖的系统永远按注册顺序执行」是硬保证。
/// </para>
/// <para>
/// <b>为什么成环要抛异常而不是随便砍一条边</b>：环 = 作者对顺序的声明自相矛盾。静默处理会让
/// 每天都偶发不同结果。异常信息给出环上的系统名与中文说明，照着删一条边就行。
/// </para>
/// <para>
/// <b>为什么「指向未注册系统」的边被忽略</b>：宿主可以按需装配系统族（冒烟场景只装移动和渲染，
/// 不装技能），此时「我必须在 SkillSystem 之后」不该导致失败。
/// </para>
/// </summary>
public sealed class SystemPipeline
{
    /// <summary>注册时缓存的系统元数据（值类型；特性已解析成数组，执行期不再碰反射）。</summary>
    public readonly struct SystemEntry
    {
        /// <summary>系统实例。</summary>
        public readonly SystemBase System;

        /// <summary>注册顺序（同优先级稳定排序的次要键）。</summary>
        public readonly int RegistrationOrder;

        /// <summary>所属分组。</summary>
        public readonly UpdateGroup Group;

        /// <summary>是否标了 [Job]（宿主据此决定是否走并行分片路径）。</summary>
        public readonly bool IsJob;

        /// <summary>我必须在这些类型之后执行（未注册的会被忽略）。</summary>
        public readonly Type[] After;

        /// <summary>我必须在这些类型之前执行（未注册的会被忽略）。</summary>
        public readonly Type[] Before;

        /// <summary>构造一个条目（由 <see cref="Add"/> 填写）。</summary>
        public SystemEntry(SystemBase system, int registrationOrder, UpdateGroup group, bool isJob, Type[] after, Type[] before)
        {
            System = system;
            RegistrationOrder = registrationOrder;
            Group = group;
            IsJob = isJob;
            After = after;
            Before = before;
        }

        /// <summary>系统名。</summary>
        public string Name => System.Name;
    }

    private readonly System.Collections.Generic.List<SystemEntry> _entries;
    private readonly World _world;
    private readonly int _groupCount;
    private int[] _order;
    private readonly int[] _groupStart;
    private readonly int[] _groupEnd;
    private bool _built;

    /// <summary>创建管线（绑定一个世界；系统通过 <see cref="Add"/> 注册）。</summary>
    public SystemPipeline(World world)
    {
        _world = world ?? throw new GameEcsException("SystemPipeline 必须绑定一个 World。");
        _entries = new System.Collections.Generic.List<SystemEntry>(EcsConfig.InitialSystemsPerGroup);
        _groupCount = CountGroups();
        _order = Array.Empty<int>();
        _groupStart = new int[_groupCount];
        _groupEnd = new int[_groupCount];
    }

    /// <summary>已注册系统数。</summary>
    public int Count => _entries.Count;

    /// <summary>是否已完成排序。</summary>
    public bool IsBuilt => _built;

    /// <summary>已注册的系统条目（调试面板只读视图）。</summary>
    public System.Collections.Generic.IReadOnlyList<SystemEntry> Entries => _entries;

    /// <summary>按执行顺序平铺的系统数。</summary>
    public int ExecutionCount => _order.Length;

    /// <summary>
    /// 注册系统。<paramref name="order"/> 是注册顺序（0 起）：同优先级系统按它稳定排序。
    /// 宿主一般用装配顺序递增传入，或者直接用 <see cref="AddAll"/>。
    /// </summary>
    public void Add(SystemBase system, int order)
    {
        if (system is null)
        {
            throw new GameEcsException("SystemPipeline.Add 的系统不能为 null。");
        }
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i].System, system))
            {
                throw new GameEcsException(
                    string.Concat("系统 ", system.Name, " 已经被注册过；一个系统实例只能进入一条管线一次。"));
            }
        }
        system.Attach(_world);
        _entries.Add(ReadEntry(system, order));
        _built = false;
    }

    /// <summary>按给定顺序批量注册（顺序 = 数组下标）。</summary>
    public void AddAll(params SystemBase[] systems)
    {
        if (systems is null)
        {
            throw new GameEcsException("SystemPipeline.AddAll 的参数不能为 null。");
        }
        for (int i = 0; i < systems.Length; i++)
        {
            Add(systems[i], i);
        }
    }

    /// <summary>
    /// 拓扑排序（注册期调用一次，可重复调用）。之后 <see cref="Execute"/> 无任何排序开销。
    /// </summary>
    public void Build()
    {
        int count = _entries.Count;
        if (_order.Length < count)
        {
            _order = new int[count];
        }
        int filled = 0;
        for (int g = 0; g < _groupCount; g++)
        {
            _groupStart[g] = filled;
            SortGroup((UpdateGroup)g, filled, out int consumed);
            filled += consumed;
            _groupEnd[g] = filled;
        }
        for (int g = 0; g < _groupCount; g++)
        {
            _groupStart[g] = Math.Min(_groupStart[g], filled);
            _groupEnd[g] = Math.Min(_groupEnd[g], filled);
        }
        _built = true;
    }

    /// <summary>一帧执行：按排序结果逐个调用 <see cref="SystemBase.Tick"/>。</summary>
    public void Execute(float deltaTime)
    {
        if (!_built)
        {
            Build();
        }
        for (int i = 0; i < _order.Length; i++)
        {
            SystemEntry entry = _entries[_order[i]];
            if (!entry.System.Enabled)
            {
                continue;
            }
            entry.System.EnsureInitialized();
            entry.System.Tick(deltaTime);
        }
    }

    /// <summary>只执行某个分组（宿主按阶段分别驱动时用，例如 FixedUpdate 单独跑）。</summary>
    public void ExecuteGroup(UpdateGroup group, float deltaTime)
    {
        if (!_built)
        {
            Build();
        }
        int g = (int)group;
        for (int i = _groupStart[g]; i < _groupEnd[g]; i++)
        {
            SystemEntry entry = _entries[_order[i]];
            if (!entry.System.Enabled)
            {
                continue;
            }
            entry.System.EnsureInitialized();
            entry.System.Tick(deltaTime);
        }
    }

    /// <summary>按执行顺序取系统（调试面板 / 测试断言排序结果用）。</summary>
    public SystemBase SystemAt(int executionOrderIndex) => _entries[_order[executionOrderIndex]].System;

    /// <summary>某个系统是否标了 <see cref="JobAttribute"/>（宿主据此决定是否启用 Job 分片路径）。</summary>
    public bool IsJob(SystemBase system)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i].System, system))
            {
                return _entries[i].IsJob;
            }
        }
        return false;
    }

    /// <summary>某个系统所属分组（未注册返回 false）。</summary>
    public bool TryGetGroup(SystemBase system, out UpdateGroup group)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i].System, system))
            {
                group = _entries[i].Group;
                return true;
            }
        }
        group = UpdateGroup.Update;
        return false;
    }

    private static int CountGroups()
    {
        // 分组是固定枚举（ECS.md 第 5.4 节），数量取枚举值上界 + 1。
        int max = 0;
        Array values = Enum.GetValues(typeof(UpdateGroup));
        for (int i = 0; i < values.Length; i++)
        {
            int value = (int)values.GetValue(i)!;
            if (value > max)
            {
                max = value;
            }
        }
        return max + 1;
    }

    /// <summary>
    /// 组内拓扑排序（Kahn + 稳定选择）：每次从「入度已归零」的候选里取注册顺序最小的那个。
    /// 结果写进 <see cref="_order"/> 的 [start, start+consumed) 区间。
    /// </summary>
    private void SortGroup(UpdateGroup group, int start, out int consumed)
    {
        int count = _entries.Count;
        int[] inDegree = new int[count];
        bool[] emitted = new bool[count];
        System.Collections.Generic.List<int>[] outgoing = new System.Collections.Generic.List<int>[count];
        int members = 0;

        for (int i = 0; i < count; i++)
        {
            inDegree[i] = _entries[i].Group == group ? 0 : EntityIndex.NoneArchetype;
            if (inDegree[i] == 0)
            {
                members++;
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (inDegree[i] < 0)
            {
                continue;
            }
            // [UpdateAfter(X)]：X 必须先于我 -> 边 X -> me（me 的入度 +1）。
            Type[] after = _entries[i].After;
            for (int a = 0; a < after.Length; a++)
            {
                int x = IndexOfType(after[a], group, i);
                if (x < 0)
                {
                    continue;
                }
                if (x == i)
                {
                    throw GameEcsException.Cycle(SingleCycle(_entries[i].System.GetType()));
                }
                outgoing[x] ??= new System.Collections.Generic.List<int>(4);
                outgoing[x].Add(i);
                inDegree[i]++;
            }
            // [UpdateBefore(X)]：我必须先于 X -> 边 me -> X（X 的入度 +1）。
            Type[] before = _entries[i].Before;
            for (int b = 0; b < before.Length; b++)
            {
                int x = IndexOfType(before[b], group, i);
                if (x < 0)
                {
                    continue;
                }
                if (x == i)
                {
                    throw GameEcsException.Cycle(SingleCycle(_entries[i].System.GetType()));
                }
                outgoing[i] ??= new System.Collections.Generic.List<int>(4);
                outgoing[i].Add(x);
                inDegree[x]++;
            }
        }

        int emittedCount = 0;
        int cursor = start;
        while (emittedCount < members)
        {
            int pick = EntityIndex.NoneArchetype;
            int pickOrder = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (inDegree[i] != 0 || emitted[i])
                {
                    continue;
                }
                int order = _entries[i].RegistrationOrder;
                if (order < pickOrder)
                {
                    pickOrder = order;
                    pick = i;
                }
            }
            if (pick < 0)
            {
                throw GameEcsException.Cycle(CollectCycle(group, inDegree, emitted));
            }
            emitted[pick] = true;
            emittedCount++;
            _order[cursor++] = pick;
            System.Collections.Generic.List<int>? next = outgoing[pick];
            if (next is not null)
            {
                for (int n = 0; n < next.Count; n++)
                {
                    inDegree[next[n]]--;
                }
            }
        }
        consumed = emittedCount;
    }

    /// <summary>自环（系统声明「我必须在我自己之后」）。</summary>
    private static System.Collections.Generic.List<Type> SingleCycle(Type type)
    {
        System.Collections.Generic.List<Type> list = new System.Collections.Generic.List<Type>(2);
        list.Add(type);
        list.Add(type);
        return list;
    }

    /// <summary>收集一个具体的环（用于异常信息）：从任一未发射的节点沿出边走。</summary>
    private System.Collections.Generic.List<Type> CollectCycle(UpdateGroup group, int[] inDegree, bool[] emitted)
    {
        System.Collections.Generic.List<Type> cycle = new System.Collections.Generic.List<Type>();
        int count = _entries.Count;
        int start = EntityIndex.NoneArchetype;
        for (int i = 0; i < count; i++)
        {
            if (inDegree[i] > 0 && !emitted[i])
            {
                start = i;
                break;
            }
        }
        if (start < 0)
        {
            return cycle;
        }
        int current = start;
        for (int step = 0; step < count + 1; step++)
        {
            cycle.Add(_entries[current].System.GetType());
            int next = EntityIndex.NoneArchetype;
            Type[] before = _entries[current].Before;
            for (int b = 0; b < before.Length; b++)
            {
                int target = IndexOfType(before[b], group, current);
                if (target >= 0 && !emitted[target])
                {
                    next = target;
                    break;
                }
            }
            if (next < 0)
            {
                Type[] after = _entries[current].After;
                for (int a = 0; a < after.Length; a++)
                {
                    int target = IndexOfType(after[a], group, current);
                    if (target >= 0 && !emitted[target])
                    {
                        next = target;
                        break;
                    }
                }
            }
            if (next < 0 || next == start)
            {
                break;
            }
            current = next;
        }
        return cycle;
    }

    /// <summary>在同组内查找某个类型对应的系统下标；未注册返回 -1。</summary>
    private int IndexOfType(Type type, UpdateGroup group, int exclude)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (i == exclude || _entries[i].Group != group)
            {
                continue;
            }
            if (_entries[i].System.GetType() == type)
            {
                return i;
            }
        }
        return EntityIndex.NoneArchetype;
    }

    /// <summary>
    /// 读取系统的注册元数据。
    /// <para>
    /// <b>本项目第二处（也是最后一处）允许的「注册期一次性反射」</b>：只在 <see cref="Add"/> 时读一次
    /// 特性，之后排序与执行只碰 int 数组，满足 docs/rules/02-tech-selection.md 第 3.3 节的例外条款。
    /// </para>
    /// </summary>
    private static SystemEntry ReadEntry(SystemBase system, int order)
    {
        Type type = system.GetType();
        UpdateGroup group = UpdateGroup.Update;
        object[] attributes = type.GetCustomAttributes(inherit: false);

        System.Collections.Generic.List<Type> afterList = new System.Collections.Generic.List<Type>(2);
        System.Collections.Generic.List<Type> beforeList = new System.Collections.Generic.List<Type>(2);
        bool isJob = false;

        for (int i = 0; i < attributes.Length; i++)
        {
            object attribute = attributes[i];
            if (attribute is UpdateInGroupAttribute inGroup)
            {
                group = inGroup.Group;
            }
            else if (attribute is UpdateAfterAttribute afterAttr)
            {
                afterList.Add(afterAttr.Target);
            }
            else if (attribute is UpdateBeforeAttribute beforeAttr)
            {
                beforeList.Add(beforeAttr.Target);
            }
            else if (attribute is JobAttribute)
            {
                isJob = true;
            }
        }

        Type[] after = afterList.Count > 0 ? afterList.ToArray() : Array.Empty<Type>();
        Type[] before = beforeList.Count > 0 ? beforeList.ToArray() : Array.Empty<Type>();
        return new SystemEntry(system, order, group, isJob, after, before);
    }
}
