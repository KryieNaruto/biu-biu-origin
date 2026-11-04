using System;
using Game.Core;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 一次「建一层地牢」的**只读产物**：布局 + 内容槽 + 装饰 + 内容生成统计。
/// <para>
/// 这是宿主与房间系统之间唯一的交界数据。它<b>不含任何引擎类型</b>（J4），
/// 所以既可以被纯 C# 测试构造，也可以被 <see cref="DungeonSceneBuilder"/> 直接消费。
/// </para>
/// </summary>
public sealed class DungeonBuildResult
{
    /// <summary>地牢图（只读规划产物）。</summary>
    public DungeonLayout Layout { get; init; }

    /// <summary>本层全部内容槽（由 <see cref="IRoomContentPlanner"/> 产出；未接入时为 null）。</summary>
    public ContentSlot[]? Slots { get; init; }

    /// <summary>本层全部装饰件（由 <see cref="IRoomDecorLayout"/> 产出；未接入时为 null）。</summary>
    public DecorPlacement[]? Decor { get; init; }

    /// <summary>布局由谁产出（规划器名）。</summary>
    public string LayoutSource { get; init; } = string.Empty;

    /// <summary>内容槽由谁产出（空串 = 没有内容规划器）。</summary>
    public string ContentSource { get; init; } = string.Empty;

    /// <summary>装饰由谁产出（空串 = 没有装饰布局器）。</summary>
    public string DecorSource { get; init; } = string.Empty;

    /// <summary>内容生成器成功认领的房间数。</summary>
    public int PopulatedRoomCount { get; init; }

    /// <summary>没有生成器认领的房间类别（诊断用）。</summary>
    public RoomCategory UnclaimedCategory { get; init; }

    /// <summary>本层种子（进存档即可复现）。</summary>
    public int Seed { get; init; }

    /// <summary>内容槽数量（null 安全）。</summary>
    public int SlotCount => Slots?.Length ?? 0;

    /// <summary>装饰数量（null 安全）。</summary>
    public int DecorCount => Decor?.Length ?? 0;

    /// <summary>一句话摘要（控制台打印用）。</summary>
    public string Describe()
    {
        DungeonLayout layout = Layout;
        return layout.ToString()
               + " | 布局=" + LayoutSource
               + " 内容=" + (string.IsNullOrEmpty(ContentSource) ? "无规划器" : ContentSource) + "(" + SlotCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "槽)"
               + " 装饰=" + (string.IsNullOrEmpty(DecorSource) ? "无布局器" : DecorSource) + "(" + DecorCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "件)"
               + " 已填充房间=" + PopulatedRoomCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 地牢布局**提供者**：只依赖接口（<see cref="IDungeonPlanner"/> / <see cref="IRoomContentPlanner"/> /
/// <see cref="IRoomContentSpawner"/> / <see cref="IRoomDecorLayout"/>），**不依赖任何具体实现类型**。
/// <para>
/// <b>为什么用「接口 + 可空实例」而不是反射</b>：反射在运行时才发现「类型不存在 / 构造签名变了」，
/// 失败点离起因很远。这里规划器由 <c>DungeonDebugScene</c> 在**编译期** new 出来注入
/// （或为 <c>null</c>），装配期就能看见依赖；队友的规划器交付后，宿主只需改一行装配。
/// </para>
/// <para>
/// <b>没有规划器时不伪造地牢</b>：<see cref="TryBuild"/> 返回 false，调用方走
/// 「无地牢的占位场景」（一块测试地面 + 相机 + 调试文字）。理由：手造 <see cref="RoomPlan"/> /
/// <see cref="RoomLink"/> / <see cref="RoomDoor"/> 假数据极易造出与校验器、门朝向规则打架的非法图，
/// 而且一旦有人把降级路径当正式逻辑依赖，排查成本极高。
/// </para>
/// </summary>
public sealed class DungeonLayoutProvider
{
    private DungeonGeneration _generation = DungeonGeneration.None;

    /// <summary>构造。</summary>
    /// <param name="planner">房间图规划器（null = 未交付 → 不生成地牢）。</param>
    /// <param name="contentPlanner">内容规划器（可为 null）。</param>
    /// <param name="contentSpawners">内容生成器列表（可为 null / 空数组）。</param>
    /// <param name="decorLayout">装饰布局器（可为 null）。</param>
    public DungeonLayoutProvider(
        IDungeonPlanner? planner,
        IRoomContentPlanner? contentPlanner,
        IRoomContentSpawner[]? contentSpawners,
        IRoomDecorLayout? decorLayout)
    {
        Planner = planner;
        ContentPlanner = contentPlanner;
        ContentSpawners = contentSpawners ?? Array.Empty<IRoomContentSpawner>();
        DecorLayout = decorLayout;
    }

    /// <summary>房间图规划器（null = 未交付）。</summary>
    public IDungeonPlanner? Planner { get; }

    /// <summary>内容规划器。</summary>
    public IRoomContentPlanner? ContentPlanner { get; }

    /// <summary>内容生成器。</summary>
    public IRoomContentSpawner[] ContentSpawners { get; }

    /// <summary>装饰布局器。</summary>
    public IRoomDecorLayout? DecorLayout { get; }

    /// <summary>是否接上了真实规划器。</summary>
    public bool HasRealPlanner => Planner is not null;

    /// <summary>当前世代号（每次成功构建 +1）。</summary>
    public DungeonGeneration Generation => _generation;

    /// <summary>
    /// 规划并校验一层，产出内容槽与装饰。<b>不建任何引擎节点、不写 ECS 结构</b>
    /// （内容生成器只往命令缓冲投递命令，由调用方在帧末提交）。
    /// </summary>
    /// <param name="config">已校验的地牢配置。</param>
    /// <param name="seed">本层种子。</param>
    /// <param name="roomTable">房型登记表（可为 null：交给规划器自己的默认拓扑）。</param>
    /// <param name="slotCapacity">内容槽容量上限。</param>
    /// <param name="decorCapacity">装饰容量上限。</param>
    /// <param name="buildContextFactory">内容生成上下文工厂（由调用方提供命令缓冲与随机根流）。</param>
    /// <param name="result">成功时产出的结果。</param>
    /// <returns>true = 成功；false = 没有规划器（调用方应走占位场景）。</returns>
    public bool TryBuild(
        DungeonConfig config,
        int seed,
        RoomCategoryTable? roomTable,
        int slotCapacity,
        int decorCapacity,
        Func<DungeonLayout, IRandomProvider, IRoomBuildContext>? buildContextFactory,
        out DungeonBuildResult result)
    {
        result = null!;
        if (Planner is null)
        {
            return false;
        }
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }
        config.Validate();

        DungeonGeneration generation = new DungeonGeneration(_generation.Value + 1);
        DungeonLayout layout = Planner.Plan(config, seed, generation);
        _generation = generation;

        // 规划结果必须当场过校验：宁可炸，也不要静默生成一张走不通的图。
        DungeonValidation.ValidateOrThrow(in layout);

        ContentSlot[]? slots = ContentPlanner?.Plan(layout, seed, slotCapacity);
        string contentSource = ContentPlanner?.Name ?? string.Empty;
        int populated = 0;
        RoomCategory unclaimed = RoomCategory.Undecided;
        if (slots is not null && ContentSpawners.Length > 0 && buildContextFactory is not null)
        {
            IRandomProvider root = new Game.Core.Randomness.DeterministicRandom(seed);
            IRoomBuildContext context = buildContextFactory(layout, root);
            RoomContentDispatcher dispatcher = new RoomContentDispatcher(ContentSpawners);
            dispatcher.PopulateAll(layout, slots, context);
            populated = dispatcher.LastPopulatedRoomCount;
            unclaimed = dispatcher.LastUnclaimed;
        }

        DecorPlacement[]? decor = DecorLayout?.Layout(layout, slots is null ? ReadOnlySpan<ContentSlot>.Empty : slots, seed, decorCapacity);
        string decorSource = DecorLayout?.Name ?? string.Empty;

        result = new DungeonBuildResult
        {
            Layout = layout,
            Slots = slots,
            Decor = decor,
            LayoutSource = Planner.Name,
            ContentSource = contentSource,
            DecorSource = decorSource,
            PopulatedRoomCount = populated,
            UnclaimedCategory = unclaimed,
            Seed = seed,
        };
        return true;
    }
}
