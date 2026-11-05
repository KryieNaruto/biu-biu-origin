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

        /// <summary>
        /// 系统声明的模块 id（<see cref="ModuleAttribute"/>，注册期解析一次）。
        /// <para>为什么放在条目里而不是每次读反射：模块判定发生在装配期（可能对每个变体跑一遍），
        /// 而特性读一次就够；热路径（逐帧执行）完全不碰它。</para>
        /// </summary>
        public readonly string[] Modules;

        /// <summary>构造一个条目（由 <see cref="Add"/> 填写）。</summary>
        public SystemEntry(
            SystemBase system,
            int registrationOrder,
            UpdateGroup group,
            bool isJob,
            Type[] after,
            Type[] before,
            string[] modules)
        {
            System = system;
            RegistrationOrder = registrationOrder;
            Group = group;
            IsJob = isJob;
            After = after;
            Before = before;
            Modules = modules;
        }

        /// <summary>
        /// 这个系统是否属于模块 <paramref name="moduleId"/>。
        /// <para>没写 <see cref="ModuleAttribute"/> 的系统恒返回 false —— 它仍然会被 <see cref="Add"/> 正常执行，
        /// 只是「不属于任何可勾选的模块」，因此模块式的装配清单里<u>看不见</u>它。</para>
        /// </summary>
        public bool IsInModule(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId) || Modules is null)
            {
                // Modules 为 null 只有一种来源：<c>default(SystemEntry)</c>（"这个系统还没注册"）。
                // 让它返回 false 而不是 NRE —— "未注册"与"不属于该模块"对调用方是同一种回答，
                // 而 NullReferenceException 会把前因（哪一步传了未注册的系统）埋在堆栈里。
                return false;
            }
            for (int i = 0; i < Modules.Length; i++)
            {
                if (string.Equals(Modules[i], moduleId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
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

    /// <summary>
    /// 已加入的模块 id（按加入顺序、去重）。
    /// <para><b>为什么单独记一份而不是从条目里现算</b>：模块的「加入顺序」是装配清单的可读输出
    /// （日志行 <c>[Hero] ... modules=a,b,c</c>），而条目顺序是系统注册顺序；两者不等价。
    /// 另外宿主可以声明一个「本变体要这一组模块」而其中<u>部分系统尚未注册</u>时，仍需要一条可打印的记录
    /// （见 <see cref="AddModule"/> 的说明），这份列表就是那条记录。</para>
    /// </summary>
    private readonly System.Collections.Generic.List<string> _modules;

    /// <summary>创建管线（绑定一个世界；系统通过 <see cref="Add"/> 注册）。</summary>
    public SystemPipeline(World world)
    {
        _world = world ?? throw new GameEcsException("SystemPipeline 必须绑定一个 World。");
        _entries = new System.Collections.Generic.List<SystemEntry>(EcsConfig.InitialSystemsPerGroup);
        _modules = new System.Collections.Generic.List<string>(EcsConfig.InitialSystemsPerGroup);
        _groupCount = CountGroups();
        _order = Array.Empty<int>();
        _groupStart = new int[_groupCount];
        _groupEnd = new int[_groupCount];
    }

    /// <summary>已注册系统数。</summary>
    public int Count => _entries.Count;

    /// <summary>已加入的模块数量（<see cref="AddModule"/> 成功加入的不同 id 数）。</summary>
    public int ModuleCount => _modules.Count;

    /// <summary>已加入的模块 id（按加入顺序；只读视图，不要缓存到跨帧字段）。</summary>
    public System.Collections.Generic.IReadOnlyList<string> ModuleNames => _modules;

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

    // ─────────────────────────────────────────────────────────────
    // 模块（装配式 ECS，「拼接」的落点；契约 §4）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 加入一个模块：注册所有「声明了该模块 id」的系统，并记录模块名。
    /// <para>
    /// <b>语义（逐条都是刻意的）</b>
    /// </para>
    /// <list type="bullet">
    /// <item><b>系统从哪来</b>：由调用方（宿主装配）把这些系统的实例传进来。管线<u>不做反射扫描程序集</u>——
    /// 系统需要构造参数（随机源、数值读取器、事件出口），扫出来的实例无法正确注入；
    /// 而且「程序集里有哪些类型」在裁剪/AOT 下并不可靠（规则2 的技术路线）。</item>
    /// <item><b>声明校验</b>：传进来的每个系统，其类型上必须有 <see cref="ModuleAttribute"/> 且包含
    /// <paramref name="moduleId"/>，否则抛异常。这条断言是模块机制的全部价值所在：
    /// 它能拦住「清单里写了 move_standard，但装配代码把 AimAtCursorSystem 传了进来」这种
    /// <u>看起来能跑、却少了一个模块</u>的错误 —— 那类错误在运行期只表现为「手感不对」，极难定位。</item>
    /// <item><b>空模块不报错</b>：<paramref name="systems"/> 为空时只登记模块名并返回 0。
    /// 原因：表现层模块（镜头包 / HUD 包）的系统不在 ECS 管线里（它们在宿主层按帧驱动），
    /// 数据侧的装配清单却必须能列出它们；对它们来说「本管线没有对应系统」是正常状态。</item>
    /// <item><b>幂等</b>：同一个模块 id 重复加入只登记一次；单个系统重复出现在 <paramref name="systems"/> 里
    /// 也只会被注册一次（否则 <see cref="Add"/> 会抛「已注册」）。</item>
    /// <item><b>注册顺序</b>：按本次调用内数组的下标递增；同分组内无显式依赖的系统按这个顺序稳定执行
    /// （见类注释）。跨多次调用时，先前的注册顺序恒小于后来的，因此「先加基础模块、再加扩展模块」是可推理的。</item>
    /// </list>
    /// </summary>
    /// <param name="moduleId">模块 id（与数据侧装配清单里的字符串逐字一致）。</param>
    /// <param name="systems">属于该模块的系统实例（可为空）。</param>
    /// <returns>本次<u>新</u>注册的系统数（已注册过的、不属于该模块的（后者会抛）都不计入）。</returns>
    public int AddModule(string moduleId, params SystemBase[] systems)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            throw new GameEcsException("SystemPipeline.AddModule 的模块 id 不能为空：空 id 无法在装配清单里被引用。");
        }
        if (systems is null)
        {
            throw new GameEcsException(
                string.Concat("SystemPipeline.AddModule('", moduleId, "') 的系统数组不能为 null（空模块请传 Array.Empty<SystemBase>()）。"));
        }
        if (_built)
        {
            // 与 Add 同一纪律：Build 之后拓扑序已固定，再塞系统会让「本帧顺序」不可推理。
            // 现有宿主的做法是「装配完再 Build」，因此这里保持严格。
            throw new GameEcsException(
                string.Concat("SystemPipeline.AddModule('", moduleId, "') 必须在 Build() 之前调用；",
                    "运行期动态拔插系统会让帧内顺序不可推理（模块是装配期概念）。"));
        }

        for (int i = 0; i < systems.Length; i++)
        {
            SystemBase system = systems[i];
            if (system is null)
            {
                throw new GameEcsException(
                    string.Concat("SystemPipeline.AddModule('", moduleId, "') 的系统数组里有 null。"));
            }
            // 未注册的系统 default(SystemEntry) 的 Modules 是 null（结构体的字段不会自动给初值），
            // 所以「已注册条目」这条旁路必须判 index >= 0，而不是依赖 IsInModule 对 null 的容忍。
            int existing = IndexOfSystem(system);
            bool declared = existing >= 0
                ? _entries[existing].IsInModule(moduleId)
                : HasModuleAttribute(system.GetType(), moduleId);
            if (!declared)
            {
                throw new GameEcsException(
                    string.Concat("系统 ", system.Name, " 没有声明模块 '", moduleId,
                        "'。每个进入模块的系统都必须在类型上写 [Module(\"", moduleId,
                        "\")] —— 否则「清单说装了、实际没装」这类错误要到运行期才暴露。"));
            }
        }

        if (!_modules.Contains(moduleId))
        {
            _modules.Add(moduleId);
        }

        int added = 0;
        for (int i = 0; i < systems.Length; i++)
        {
            SystemBase system = systems[i];
            if (IndexOfSystem(system) >= 0)
            {
                // 已经在管线里（可能来自另一个模块，也可能是本次调用的重复元素）：不重复注册。
                continue;
            }
            Add(system, _entries.Count);
            added++;
        }
        return added;
    }

    /// <summary>
    /// 取消注册一个系统（<b>只在 <see cref="Build"/> 之前合法</b>，之后抛异常）。
    /// <para>
    /// <b>为什么不在运行期支持</b>：<see cref="Build"/> 之后 <see cref="Execute"/> 走的是扁平 <c>_order</c> 数组，
    /// 运行期删系统会让「谁在谁之前」变成时间依赖 —— 同一个输入序列在不同时刻跑出不同结果，
    /// 回放与确定性立刻失效。装配期（开始一局 / 加载一个变体）的增删才是安全的，那正是模块机制的用法。
    /// </para>
    /// <para>系统实例本身不被销毁：它只是被移出管线，仍可再加入（<see cref="Add"/> / <see cref="AddModule"/>）。</para>
    /// </summary>
    /// <returns>确实移除时返回 true；系统不在管线里返回 false（幂等，便于「按清单回收」的写法）。</returns>
    public bool Remove(SystemBase system)
    {
        if (system is null)
        {
            throw new GameEcsException("SystemPipeline.Remove 的系统不能为 null。");
        }
        if (_built)
        {
            throw new GameEcsException(
                string.Concat("SystemPipeline.Remove(", system.Name, ") 只能在 Build() 之前调用；",
                    "运行期取消注册会让帧内执行顺序不可推理（请改为装配期重建管线）。"));
        }
        int index = IndexOfSystem(system);
        if (index < 0)
        {
            return false;
        }
        _entries.RemoveAt(index);
        // 注册顺序是稳定排序的次要键：移除后重排成 0..n-1，保证「相对顺序」不受历史增删影响。
        for (int i = 0; i < _entries.Count; i++)
        {
            SystemEntry entry = _entries[i];
            if (entry.RegistrationOrder != i)
            {
                _entries[i] = new SystemEntry(
                    entry.System, i, entry.Group, entry.IsJob, entry.After, entry.Before, entry.Modules);
            }
        }
        _built = false;
        return true;
    }

    /// <summary>
    /// 移除一个模块的登记（只改 <see cref="ModuleNames"/>，<b>不</b>移除系统）。
    /// <para>
    /// 为什么分开：一个系统可能同时属于两个模块（<c>[Module("move_standard", "move_tank")]</c>），
    /// 由模块 A 注册进来后又被模块 B 引用；「取消模块登记」若顺手删系统，就会误伤另一个模块。
    /// 真要回收系统请显式 <see cref="Remove"/>。
    /// </para>
    /// </summary>
    public bool RemoveModule(string moduleId)
    {
        if (_built)
        {
            throw new GameEcsException(
                string.Concat("SystemPipeline.RemoveModule('", moduleId, "') 只能在 Build() 之前调用。"));
        }
        return !string.IsNullOrEmpty(moduleId) && _modules.Remove(moduleId);
    }

    /// <summary>某个模块 id 是否已加入本管线。</summary>
    public bool HasModule(string moduleId)
    {
        if (string.IsNullOrEmpty(moduleId))
        {
            return false;
        }
        for (int i = 0; i < _modules.Count; i++)
        {
            if (string.Equals(_modules[i], moduleId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>某个系统是否已在本管线里。</summary>
    public bool Contains(SystemBase system) => system is not null && IndexOfSystem(system) >= 0;

    /// <summary>本管线里属于模块 <paramref name="moduleId"/> 的系统名（按注册顺序；日志与调试面板用）。</summary>
    public System.Collections.Generic.IReadOnlyList<string> SystemNamesOfModule(string moduleId)
    {
        var names = new System.Collections.Generic.List<string>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].IsInModule(moduleId))
            {
                names.Add(_entries[i].Name);
            }
        }
        return names;
    }

    private int IndexOfSystem(SystemBase system)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i].System, system))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>直接读类型上的 <see cref="ModuleAttribute"/>（注册期一次性反射；只用于声明校验与 ReadEntry）。</summary>
    private static bool HasModuleAttribute(Type type, string moduleId)
    {
        object[] attributes = type.GetCustomAttributes(typeof(ModuleAttribute), inherit: false);
        for (int i = 0; i < attributes.Length; i++)
        {
            var module = (ModuleAttribute)attributes[i];
            System.Collections.Generic.IReadOnlyList<string> ids = module.ModuleIds;
            for (int m = 0; m < ids.Count; m++)
            {
                if (string.Equals(ids[m], moduleId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
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
        System.Collections.Generic.List<string> moduleList = new System.Collections.Generic.List<string>(1);
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
            else if (attribute is ModuleAttribute module)
            {
                System.Collections.Generic.IReadOnlyList<string> ids = module.ModuleIds;
                for (int m = 0; m < ids.Count; m++)
                {
                    if (!moduleList.Contains(ids[m]))
                    {
                        moduleList.Add(ids[m]);
                    }
                }
            }
        }

        Type[] after = afterList.Count > 0 ? afterList.ToArray() : Array.Empty<Type>();
        Type[] before = beforeList.Count > 0 ? beforeList.ToArray() : Array.Empty<Type>();
        string[] modules = moduleList.Count > 0 ? moduleList.ToArray() : Array.Empty<string>();
        return new SystemEntry(system, order, group, isJob, after, before, modules);
    }
}
