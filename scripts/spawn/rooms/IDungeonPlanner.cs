namespace Game.Spawn.Rooms;

/// <summary>
/// 房间图**规划器**：「随机且合理」的唯一实现处。
/// <para>
/// 抽象先行（规则5）：规划器只产出 <see cref="DungeonLayout"/>（纯值数据），
/// <b>不知道 ECS、不知道 Godot、不建任何节点、不刷任何怪</b>。于是它可以在裸 .NET 测试里
/// 跑十万次做性质检查（连通性 / 无重叠 / Boss 深度），这是「随机系统可信」的唯一办法。
/// </para>
/// <para>
/// <b>扩展点（规则7）</b>：想要新的拓扑风格（比如「章鱼状 hub-and-spoke」），实现这个接口即可，
/// 宿主只多一行装配；<see cref="ContentSlot"/> / 装饰 / Host 建场景都不受影响。
/// </para>
/// <para>
/// <b>确定性契约</b>：同 seed + 同配置 → 逐字节相同的 <see cref="DungeonLayout"/>。
/// 实现方<b>禁止</b>使用 <c>Random.Shared</c> / <c>DateTime</c> / 字典遍历顺序等不可控来源。
/// </para>
/// </summary>
public interface IDungeonPlanner
{
    /// <summary>规划器名（日志与调试面板用）。</summary>
    string Name { get; }

    /// <summary>
    /// 规划一层房间图。
    /// </summary>
    /// <param name="config">已通过 <see cref="DungeonConfig.Validate"/> 的配置。</param>
    /// <param name="seed">本层种子。同 seed 同结果；规划器内部用 <c>Fork</c> 切子流保证「别人多掷一次骰不影响本图」。</param>
    /// <param name="generation">本图世代号（由调用方递增，用于旧句柄失效）。</param>
    /// <returns>不可变的地牢图。</returns>
    DungeonLayout Plan(DungeonConfig config, int seed, DungeonGeneration generation);
}

/// <summary>
/// 房间类型登记表：房型 → 生成参数。规划器**只认这张表**，不认具体房间名。
/// <para>
/// <b>规则7 的执行方式</b>：新增一种房型 = 在数据里加一行 <see cref="RoomCategorySpec"/>
/// （类别 / 房间模板 / 权重 / 是否出现在随机池 / 是否安全房），规划器与内容层 <b>0 代码改动</b>。
/// 想加一个新的「房间种类」而需要改代码，说明它其实是一种新 <see cref="RoomCategory"/>
/// （新的拓扑职责）——那才是应该改枚举的场景。
/// </para>
/// </summary>
public sealed class RoomCategoryTable
{
    private readonly RoomCategorySpec[] _specs;

    /// <summary>构造。</summary>
    /// <param name="specs">全部房型；必须至少包含 Start 与 Boss，且同类别不允许重复登记（后者生效）。</param>
    public RoomCategoryTable(RoomCategorySpec[] specs)
    {
        if (specs is null || specs.Length == 0)
        {
            throw new System.ArgumentException("RoomCategoryTable 至少要有一行房型配置。", nameof(specs));
        }
        _specs = specs;
    }

    /// <summary>全部房型（只读）。</summary>
    public System.Collections.Generic.IReadOnlyList<RoomCategorySpec> Specs => _specs;

    /// <summary>按类别查房型；未登记返回 false。</summary>
    /// <param name="category">房间类别。</param>
    /// <param name="spec">查到的规格。</param>
    public bool TryGet(RoomCategory category, out RoomCategorySpec spec)
    {
        for (int i = 0; i < _specs.Length; i++)
        {
            if (_specs[i].Category == category)
            {
                spec = _specs[i];
                return true;
            }
        }
        spec = default;
        return false;
    }

    /// <summary>按类别查房型，未登记直接抛（规划器内部用：缺配置是编程错误，不是玩法状态）。</summary>
    /// <param name="category">房间类别。</param>
    public RoomCategorySpec Get(RoomCategory category)
    {
        if (!TryGet(category, out RoomCategorySpec spec))
        {
            throw new System.InvalidOperationException(
                "RoomCategoryTable 里没有登记房型 " + category.ToString() + "，无法生成地牢。");
        }
        return spec;
    }

    /// <summary>可作为「有怪的普通房」出现在随机池里的房型（按权重抽取）。</summary>
    /// <param name="buffer">输出缓冲（稳态零分配由调用方保证大小）。</param>
    /// <param name="state">抽取时需要排除的类别（例如关键路径上禁止出现特殊房）。</param>
    public int CollectRandomPool(System.Span<RoomCategorySpec> buffer, RoomCategory excluded)
    {
        int n = 0;
        for (int i = 0; i < _specs.Length && n < buffer.Length; i++)
        {
            RoomCategorySpec spec = _specs[i];
            if (!spec.IncludeInRandom || spec.Category == excluded)
            {
                continue;
            }
            buffer[n++] = spec;
        }
        return n;
    }
}

/// <summary>房型规格（数据行）。</summary>
public readonly struct RoomCategorySpec
{
    /// <summary>房间类别。</summary>
    public RoomCategory Category { get; init; }

    /// <summary>房间模板编号（内容层的入口）。</summary>
    public RoomArchetypeId Archetype { get; init; }

    /// <summary>随机抽取权重（&gt; 0 才会被抽到；0 = 只能被显式指定）。</summary>
    public int Weight { get; init; }

    /// <summary>是否进入随机池（原型测试房在正式关卡里=false）。</summary>
    public bool IncludeInRandom { get; init; }

    /// <summary>是否安全房（门默认开放、不刷怪、可以被当作「出生点」的邻居）。</summary>
    public bool IsSafe { get; init; }

    /// <summary>是否必须挂在关键路径上（Boss）。</summary>
    public bool RequiresCriticalPath { get; init; }

    /// <inheritdoc />
    public override string ToString() => Category + "->" + Archetype + " w=" + Weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>随机池里的一条候选（房型 + 有效权重）。</summary>
public readonly struct RoomCandidate
{
    /// <summary>房型规格。</summary>
    public RoomCategorySpec Spec { get; init; }

    /// <summary>有效权重（外部可再乘一个「这条路该不该放特殊房」的系数）。</summary>
    public float Weight { get; init; }
}
