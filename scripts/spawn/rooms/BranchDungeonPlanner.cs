using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Spawn.Rooms;

/// <summary>
/// 「元气骑士式」分支地牢规划器：<see cref="IDungeonPlanner"/> 的默认实现。
/// <para>
/// <b>产出什么</b>：一张不可变的 <see cref="DungeonLayout"/> —— 房间矩形（格）、链路（拓扑边）、
/// 每面墙上的门、锚点。**不建节点、不刷怪、不碰 ECS**，所以能在裸 .NET 里跑几万次做性质测试。
/// </para>
/// <para>
/// <b>几何约定</b>（Lead 2026-11-04 统一口径）：<see cref="RoomPlan.Cell"/> 是房间**左上角**格，
/// 占半开区间 <c>[X, X+W) × [Z, Z+D)</c>；相邻 = 「某轴边缘相等 + 另一轴正长度重叠」
/// （见 <see cref="DungeonGridMath.AreEdgeAdjacent"/>），**不要求同尺寸**。
/// 于是 5 格起点房与 3 格普通房可以直接贴墙连通，链路朝向取重叠区中点。
/// </para>
/// <para>
/// <b>算法（五步，全部随机决策都走注入的 <see cref="IRandomProvider"/>）</b>：
/// </para>
/// <list type="number">
/// <item><b>生长</b>：起点房锚在网格中部；随后反复「从全部「墙面还有空间」的房间×方向里随机挑一个，
/// 沿该方向贴边放一间新房（侧向上随机错开 ±N 格，但必须与原房间在该轴有正长度重叠）」，
/// 直到长够目标数或 <see cref="DungeonConfig.RoomsPerStep"/> 轮都长不动。
/// 每个方向都要「平移后仍在网格内」，于是房间不会出界。</item>
/// <item><b>出生树</b>：从起点做 BFS，得到 <see cref="RoomPlan.Depth"/> 与树父；
/// 生长时偶尔长到已有房间旁边形成的环，按 <see cref="DungeonConfig.LoopDistance"/> 决定是否保留为捷径。</item>
/// <item><b>分类</b>（BFS 之后）：Boss = 深度最深且 &gt;= <see cref="DungeonConfig.MinBossDepth"/> 的房间（不够就重试）；
/// 关键路径 = 起点→Boss 的树路径，按 <see cref="DungeonConfig.SpecialOnCriticalPathChance"/> 放少量特殊房；
/// 其余特殊房优先进侧枝叶子，数量落在 [MinSpecialRooms, MaxSpecialRooms]；其余全部战斗房。</item>
/// <item><b>隐藏房</b>：挑一个「不与任何房间贴边」的空闲格（所以它不可能有主图边），
/// 用 <see cref="RoomLink.IsSecret"/>=true、<see cref="RoomLink.OnMainGraph"/>=false 的链路挂上。</item>
/// <item><b>校验</b>：<see cref="DungeonValidation.ValidateOrThrow"/>；不合法就抛中文异常
/// （宁可当场炸，也不静默返回一张走不通的图）。生长失败 → 换子流重试，最多 <c>maxAttempts</c> 次。</item>
/// </list>
/// <para>
/// <b>确定性</b>：同 seed + 同配置 → 逐字段相同的图。内部先对注入的随机源 <c>Fork</c> 本规划器专属盐值
/// （别的系统多掷一次骰不会改变本图），每次重试再 <c>Fork(seed ^ attempt)</c>，各次互不干扰。
/// 不使用 <c>Random.Shared</c> / <c>DateTime</c> / 字典遍历顺序。
/// </para>
/// <para>
/// <b>扩展点（规则7）</b>：换拓扑风格 = 另实现 <see cref="IDungeonPlanner"/>（宿主多一行装配）；
/// 换房型/权重/模板 = 改 <see cref="RoomCategoryTable"/> 数据行（本文件 0 改动）。
/// </para>
/// </summary>
public sealed class BranchDungeonPlanner : IDungeonPlanner
{
    /// <summary>随机子流盐值：与其他系统隔离（改这个值 = 让历史回放失效）。</summary>
    private const int PlannerSalt = 0x7A02; // arch-ok: 随机子流盐常量（名字即契约），非玩法数值

    /// <summary>重试序号参与子流派生的乘子（结构性常量：让各次尝试落在互不相同的分支上）。</summary>
    private const int AttemptSaltStride = 0x0100; // arch-ok: 子流盐步长，非玩法数值

    /// <summary>生长子流盐。</summary>
    private const int GrowSalt = 0x5101; // arch-ok: 随机子流盐常量，非玩法数值

    /// <summary>分类子流盐。</summary>
    private const int ClassifySalt = 0x5102; // arch-ok: 随机子流盐常量，非玩法数值

    /// <summary>隐藏房子流盐。</summary>
    private const int HiddenSalt = 0x5103; // arch-ok: 随机子流盐常量，非玩法数值

    /// <summary>默认最大尝试次数（构造参数可覆盖）。</summary>
    public const int DefaultMaxAttempts = 8; // arch-ok: 重试次数上限（防死循环的工程常量），不是玩法数值

    /// <summary>前沿候选数组容量上限：每个房间最多 4 个方向，预分配够用即可。</summary>
    private const int MaxFrontierCandidates = 512; // arch-ok: 前沿候选数组容量（内存预分配），不是玩法数值

    /// <summary>起点 + Boss 两间额外房间（结构性常量：v1 拓扑恰好各 1 间）。</summary>
    private const int ExtraRooms = 2; // arch-ok: 起点/Boss 房间数（拓扑常量），不是玩法数值

    /// <summary>隐藏房搜索圈数上限（每圈 = 沿四个方向各推进一段）；超出即认为放不下、触发重试。</summary>
    private const int HiddenSearchRings = 48; // arch-ok: 网格搜索圈数上限（防死循环），不是玩法数值

    /// <summary>把「两个房间槽位」打包成一个 <c>long</c> 键时，低位占的位数。</summary>
    private const int SlotKeyShift = 32; // arch-ok: 64 位打包位宽（纯编码常量），不是玩法数值

    /// <summary>隐藏房宿主房间的深度门槛 = 主图最大深度 / 该值（要求宿主足够深，否则秘密通道太容易撞见）。</summary>
    private const int HiddenHostDepthDivisor = 2; // arch-ok: 深度比例分母（结构性阈值），不是玩法数值

    /// <summary>起点抖动幅度除数：把抖动收在中部一半范围内（布局常量）。</summary>
    private const int JitterDivisor = 2; // arch-ok: 起点抖动幅度除数（布局常量），不是玩法数值

    /// <summary>每条链路两扇门（结构性常量）。</summary>
    private const int DoorsPerLink = 2; // arch-ok: 每链路两扇门（拓扑常量），不是玩法数值

    /// <summary>生长时新房间允许的侧向错开量（格）：0..N；错开能让图不那么「一根直肠子」。</summary>
    private const int MaxSideOffset = 2; // arch-ok: 贴边错开搜索范围（布局常量），不是玩法数值

    private readonly IRandomProvider _root;
    private readonly RoomCategoryTable _categories;
    private readonly int _maxAttempts;

    /// <summary>最近一次规划失败时的原因（诊断；成功后保留上次残留值）。</summary>
    public string LastFailureReason { get; private set; } = string.Empty;

    /// <summary>最近一次 <see cref="Plan"/> 用掉的尝试次数（诊断：正常为 1）。</summary>
    public int LastAttempts { get; private set; }

    /// <summary>构造。</summary>
    /// <param name="random">随机根流（宿主注入）。本规划器内部会 <c>Fork</c> 自己的子流。</param>
    /// <param name="categories">房型登记表（Start / Boss 必须登记，否则构造即抛）。</param>
    /// <param name="maxAttempts">最大重试次数；&lt;= 0 时取 <see cref="DefaultMaxAttempts"/>。</param>
    public BranchDungeonPlanner(IRandomProvider random, RoomCategoryTable categories, int maxAttempts = DefaultMaxAttempts)
    {
        _root = random ?? throw new ArgumentNullException(nameof(random));
        _categories = categories ?? throw new ArgumentNullException(nameof(categories));
        _maxAttempts = maxAttempts > 0 ? maxAttempts : DefaultMaxAttempts;

        // 构造期就把「生成这张图必需的两类房型」校验掉：缺配置是装配错误，应当在启动期炸。
        _ = _categories.Get(RoomCategory.Start);
        _ = _categories.Get(RoomCategory.Boss);
    }

    /// <summary>固定房型表：Start / Combat / Special / Hidden / Boss 五类，外加数据驱动的权重。
    /// <para>它只是**一份给宿主与测试用的默认数据行**，不是玩法数值的来源——玩法数值仍由宿主从
    /// <c>DungeonOptions</c>/Excel 构造 <see cref="DungeonConfig"/> 注入；这里只解决「房型表没有默认值就没人能跑」。</para>
    /// </summary>
    /// <returns>默认房型表（每个调用返回新实例，避免共享可变状态）。</returns>
    public static RoomCategoryTable DefaultCategories() => new RoomCategoryTable(new[]
    {
        // 原型编号：房间原型（房间预制体）的**标识符**，不是玩法数值。
        // 为什么写成常量而不是查表：房型↔原型的对应关系属于美术/预制体绑定，宿主会用自己的
        // 原型表整体替换 DefaultCategories()；这里只是一份能立刻跑起来的兜底。
        new RoomCategorySpec { Category = RoomCategory.Start, Archetype = new RoomArchetypeId(ArchetypeStart), Weight = 0, IncludeInRandom = false, IsSafe = true, RequiresCriticalPath = false },
        new RoomCategorySpec { Category = RoomCategory.Combat, Archetype = new RoomArchetypeId(ArchetypeCombat), Weight = 0, IncludeInRandom = true, IsSafe = false, RequiresCriticalPath = false },
        new RoomCategorySpec { Category = RoomCategory.Special, Archetype = new RoomArchetypeId(ArchetypeSpecial), Weight = 0, IncludeInRandom = true, IsSafe = false, RequiresCriticalPath = false },
        new RoomCategorySpec { Category = RoomCategory.Hidden, Archetype = new RoomArchetypeId(ArchetypeHidden), Weight = 0, IncludeInRandom = false, IsSafe = false, RequiresCriticalPath = false },
        new RoomCategorySpec { Category = RoomCategory.Boss, Archetype = new RoomArchetypeId(ArchetypeBoss), Weight = 0, IncludeInRandom = false, IsSafe = false, RequiresCriticalPath = true },
    });

    /// <summary>兜底原型：起始房。</summary>
    private const int ArchetypeStart = 1; // arch-ok: 房间原型标识符（宿主会整体替换房型表），非玩法数值

    /// <summary>兜底原型：普通战斗房。</summary>
    private const int ArchetypeCombat = 2; // arch-ok: 房间原型标识符，非玩法数值

    /// <summary>兜底原型：特殊房。</summary>
    private const int ArchetypeSpecial = 3; // arch-ok: 房间原型标识符，非玩法数值

    /// <summary>兜底原型：隐藏房。</summary>
    private const int ArchetypeHidden = 4; // arch-ok: 房间原型标识符，非玩法数值

    /// <summary>兜底原型：Boss 房。</summary>
    private const int ArchetypeBoss = 5; // arch-ok: 房间原型标识符，非玩法数值

    /// <inheritdoc />
    public string Name => "BranchDungeon";

    /// <inheritdoc />
    public DungeonLayout Plan(DungeonConfig config, int seed, DungeonGeneration generation)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }
        // 先校验配置：网格太小 / 目标房间比总容量还多这类错误，必须在掷骰之前就变成明确的中文异常。
        config.Validate();
        RequireFeasible(config);

        IRandomProvider plannerStream = _root.Fork(PlannerSalt);

        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            IRandomProvider stream = plannerStream.Fork(unchecked(seed ^ (attempt * AttemptSaltStride)));
            LastAttempts = attempt + 1;
            if (TryBuild(config, seed, generation, stream, out DungeonLayout layout))
            {
                DungeonValidation.ValidateOrThrow(in layout); // 自身不变式被破坏 = bug，不重试，直接炸
                return layout;
            }
        }

        throw new InvalidOperationException(
            "地牢生成失败：连续 " + _maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 次尝试都没能生成一张合法的图（最近一次原因：" + LastFailureReason + "）。"
            + "请检查 DungeonConfig：网格 " + config.GridWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "×" + config.GridHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 是否容得下 " + config.TargetRoomCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 个普通房（容差 " + config.RoomCountTolerance.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "）+ " + config.HiddenRoomCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 个隐藏房，以及 MinBossDepth 是否可达。");
    }

    // ─────────────────────────────────────────────────────────────
    // 容量预检（与随机无关的不可行条件，提前抛，不靠重试去撞）
    // ─────────────────────────────────────────────────────────────

    private static void RequireFeasible(DungeonConfig config)
    {
        long cells = (long)config.GridWidth * config.GridHeight;
        long wanted = (long)config.TargetRoomCount + config.RoomCountTolerance
                    + config.HiddenRoomCount + config.MinSpecialRooms + ExtraRooms;
        if (wanted > cells)
        {
            throw new InvalidOperationException(
                "地牢配置不可行：网格只有 " + cells.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " 格，却要求放 " + wanted.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " 个房间（目标房数 + 容差 + 隐藏房 + 特殊房下界 + 起点与 Boss 各 1）。请调大网格或调小目标数量。");
        }
        if (config.RoomSide > config.GridWidth || config.RoomSide > config.GridHeight)
        {
            throw new InvalidOperationException(
                "地牢配置不可行：普通房边长 " + config.RoomSide.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " 大于网格 " + config.GridWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "×" + config.GridHeight.ToString(System.Globalization.CultureInfo.InvariantCulture) + "，房间放不下。");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 单次尝试
    // ─────────────────────────────────────────────────────────────

    private bool TryBuild(DungeonConfig config, int seed, DungeonGeneration generation, IRandomProvider stream, out DungeonLayout layout)
    {
        layout = default;

        // 一条尝试内部的随机再分三路：生长 / 分类 / 隐藏房。于是「隐藏房多掷一次」不会改变房间类别。
        IRandomProvider growRandom = stream.Fork(GrowSalt);
        IRandomProvider classifyRandom = stream.Fork(ClassifySalt);
        IRandomProvider hiddenRandom = stream.Fork(HiddenSalt);

        int targetRooms = ChooseTargetRoomCount(config, growRandom);
        Draft draft = new Draft(config, generation, targetRooms);
        draft.AddStart(config, PickStartCell(config, growRandom));
        Grow(config, growRandom, draft);

        if (draft.Count < targetRooms)
        {
            return Fail("只长出 " + draft.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " 间房（目标 " + targetRooms.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）");
        }

        BuildBirthTree(draft, out int[] depth, out int[] treeParent);
        int bossSlot = FindBoss(config, draft, depth);
        if (bossSlot < 0)
        {
            return Fail("最深房间深度只有 " + MaxDepth(depth).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "，达不到 MinBossDepth=" + config.MinBossDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Classify(config, classifyRandom, draft, treeParent, bossSlot);
        draft.Boss = bossSlot;

        List<RoomLink> links = BuildMainLinks(config, draft, depth, treeParent, bossSlot);
        if (!AddHiddenRooms(config, hiddenRandom, draft, depth, links))
        {
            return Fail("隐藏房找不到「不与任何房间贴边」的位置");
        }

        // 深度以主图 BFS 为准写回 RoomPlan（隐藏房保持 UnreachableDepth）。
        // RoomPlan 是 readonly struct + init-only 属性：只能整体重建，不能逐字段改。
        for (int i = 0; i < draft.Count; i++)
        {
            RoomPlan source = draft.Rooms[i];
            draft.Rooms[i] = new RoomPlan
            {
                Id = source.Id,
                Category = draft.Category[i],
                Archetype = source.Archetype,
                Cell = source.Cell,
                Footprint = source.Footprint,
                Depth = draft.Category[i] == RoomCategory.Hidden ? RoomPlan.UnreachableDepth : depth[i],
                OnCriticalPath = draft.OnCriticalPath[i],
                IsSecretOnly = draft.Category[i] == RoomCategory.Hidden,
                Anchors = source.Anchors,
            };
        }

        RoomLink[] linkArray = links.ToArray();
        List<RoomDoor> doors = BuildDoors(config, generation, draft, linkArray);
        RefillAnchors(draft, doors);
        RoomPlan[] rooms = draft.TakeRooms();

        layout = new DungeonLayout
        {
            Generation = generation,
            Seed = seed,
            Rooms = rooms,
            Links = linkArray,
            Doors = doors.ToArray(),
            Start = new RoomId(generation, draft.Start),
            Boss = new RoomId(generation, bossSlot),
            Entrance = new DoorId(generation, FindEntranceDoor(config, draft, doors)),
        };
        return true;
    }

    private bool Fail(string reason)
    {
        LastFailureReason = reason;
        return false;
    }

    private static int ChooseTargetRoomCount(DungeonConfig config, IRandomProvider random)
    {
        int min = config.TargetRoomCount - config.RoomCountTolerance;
        int max = config.TargetRoomCount + config.RoomCountTolerance;
        if (min < 1)
        {
            min = 1; // 起点 + Boss 至少两间（v1 拓扑）
        }
        return max <= min ? min : random.NextInt(min, max + 1);
    }

    // ─────────────────────────────────────────────────────────────
    // 1. 生长
    // ─────────────────────────────────────────────────────────────

    private static void Grow(DungeonConfig config, IRandomProvider random, Draft draft)
    {
        int maxSteps = draft.TargetRooms * config.RoomsPerStep;
        Span<int> hosts = stackalloc int[MaxFrontierCandidates];
        Span<RoomDirection> dirs = stackalloc RoomDirection[MaxFrontierCandidates];

        for (int step = 0; step < maxSteps && draft.Count < draft.TargetRooms; step++)
        {
            int frontiers = CollectFrontiers(config, draft, hosts, dirs);
            if (frontiers == 0)
            {
                break;
            }

            // Fisher–Yates：让分支在图里散开，而不是永远沿「下标最小」的方向一条线堆下去。
            for (int i = frontiers - 1; i > 0; i--)
            {
                int j = random.NextInt(0, i + 1);
                (hosts[i], hosts[j]) = (hosts[j], hosts[i]);
                (dirs[i], dirs[j]) = (dirs[j], dirs[i]);
            }

            for (int i = 0; i < frontiers && draft.Count < draft.TargetRooms; i++)
            {
                RoomPlan origin = draft.Rooms[hosts[i]];
                TryGrowOne(config, random, draft, in origin, dirs[i], config.RoomSide);
            }
        }
    }

    /// <summary>
    /// 从 <paramref name="origin"/> 的 <paramref name="direction"/> 那面墙外长一间 <paramref name="side"/> 边长的房间。
    /// 侧向错开量从候选里随机挑；只要贴边且与这一侧有正重叠就合法（边长可不同）。
    /// </summary>
    private static bool TryGrowOne(
        DungeonConfig config, IRandomProvider random, Draft draft, in RoomPlan origin, RoomDirection direction, int side)
    {
        // 可用的侧向错开区间：让新房间与原房间在侧轴上至少有 1 格重叠。
        int originSide = direction is RoomDirection.East or RoomDirection.West ? origin.Footprint.Depth : origin.Footprint.Width;
        int minOffset = 1 - side;
        int maxOffset = originSide - 1;
        if (maxOffset < minOffset)
        {
            return false; // 理论不可达（side/originSide >= 1），保留以防除零式退化
        }
        int lo = minOffset > -MaxSideOffset ? minOffset : -MaxSideOffset;
        int hi = maxOffset < MaxSideOffset ? maxOffset : MaxSideOffset;
        if (hi < lo)
        {
            return false;
        }
        int offset = random.NextInt(lo, hi + 1);
        RoomPlan candidate = DungeonGridMath.ShiftedToEdge(in origin, direction, side, offset);
        if (!IsFree(config, draft, in candidate))
        {
            return false;
        }
        draft.AddNormal(in candidate);
        return true;
    }

    /// <summary>候选房间是否可放：在网格内、且与所有已有房间不重叠。</summary>
    private static bool IsFree(DungeonConfig config, Draft draft, in RoomPlan candidate)
    {
        if (!DungeonGridMath.IsInsideGrid(in candidate, config.GridWidth, config.GridHeight))
        {
            return false;
        }
        for (int i = 0; i < draft.Count; i++)
        {
            if (DungeonGridMath.Overlaps(draft.Rooms[i], in candidate))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 收集所有可用门位：房间 × 方向，且该方向平移一格后整个房间仍在网格内。
    /// 返回个数（受 <paramref name="hosts"/> 容量限制）。
    /// </summary>
    private static int CollectFrontiers(DungeonConfig config, Draft draft, Span<int> hosts, Span<RoomDirection> dirs)
    {
        int n = 0;
        for (int r = 0; r < draft.Count && n < hosts.Length; r++)
        {
            RoomPlan room = draft.Rooms[r];
            for (int d = 0; d < RoomDirectionExtensions.Count && n < hosts.Length; d++)
            {
                RoomDirection direction = (RoomDirection)d;
                // 「还能往这个方向长」的粗判：把房间整体平移一格后仍在网格内。
                RoomPlan shifted = DungeonGridMath.Shifted(in room, direction);
                if (!DungeonGridMath.IsInsideGrid(in shifted, config.GridWidth, config.GridHeight))
                {
                    continue;
                }
                hosts[n] = r;
                dirs[n] = direction;
                n++;
            }
        }
        return n;
    }

    /// <summary>起点房位置：网格中部的一个随机格（X、Z 各自在「放得下起点房」的范围内抖动）。</summary>
    private static GridOffset PickStartCell(DungeonConfig config, IRandomProvider random)
    {
        int side = config.StartRoomSide;
        int freeX = config.GridWidth - side;
        int freeZ = config.GridHeight - side;
        int x = freeX <= 0 ? 0 : freeX / JitterDivisor;
        int z = freeZ <= 0 ? 0 : freeZ / JitterDivisor;
        if (freeX > 0)
        {
            x = Clamp(x + random.NextInt(-freeX, freeX + 1) / JitterDivisor, 0, freeX);
        }
        if (freeZ > 0)
        {
            z = Clamp(z + random.NextInt(-freeZ, freeZ + 1) / JitterDivisor, 0, freeZ);
        }
        return new GridOffset(x, z);
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    // ─────────────────────────────────────────────────────────────
    // 2. 出生树 / 深度
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 对主图（此时还没有隐藏房）跑一次 BFS：产出每间房的真实深度与树父（用于区分树边与环边）。
    /// 深度是玩法语义（「离起点几步」），必须以图论结果为准，不能沿用生长时的排名。
    /// </summary>
    private static void BuildBirthTree(Draft draft, out int[] depth, out int[] treeParent)
    {
        int n = draft.Count;
        depth = new int[n];
        treeParent = new int[n];
        bool[] seen = new bool[n];
        for (int i = 0; i < n; i++)
        {
            depth[i] = RoomPlan.UnreachableDepth;
            treeParent[i] = -1;
        }
        if (n == 0 || draft.Start < 0)
        {
            return;
        }
        int[] queue = new int[n];
        int head = 0;
        int tail = 0;
        seen[draft.Start] = true;
        depth[draft.Start] = 0;
        queue[tail++] = draft.Start;
        while (head < tail)
        {
            int current = queue[head++];
            for (int other = 0; other < n; other++)
            {
                if (seen[other] || !DungeonGridMath.AreEdgeAdjacent(draft.Rooms[current], draft.Rooms[other]))
                {
                    continue;
                }
                seen[other] = true;
                depth[other] = depth[current] + 1;
                treeParent[other] = current;
                queue[tail++] = other;
            }
        }
    }

    /// <summary>Boss 房：深度最深且 &gt;= MinBossDepth 的房间；并列取槽位靠前者（确定性）。</summary>
    private static int FindBoss(DungeonConfig config, Draft draft, int[] depth)
    {
        int best = -1;
        int bestDepth = -1;
        for (int i = 0; i < draft.Count; i++)
        {
            if (i == draft.Start || depth[i] < config.MinBossDepth)
            {
                continue;
            }
            if (depth[i] > bestDepth)
            {
                bestDepth = depth[i];
                best = i;
            }
        }
        return best;
    }

    private static int MaxDepth(int[] depth)
    {
        int max = 0;
        for (int i = 0; i < depth.Length; i++)
        {
            max = depth[i] > max ? depth[i] : max;
        }
        return max;
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 分类
    // ─────────────────────────────────────────────────────────────

    private void Classify(DungeonConfig config, IRandomProvider random, Draft draft, int[] treeParent, int bossSlot)
    {
        for (int i = 0; i < draft.Count; i++)
        {
            draft.Category[i] = i == draft.Start ? RoomCategory.Start : RoomCategory.Combat;
        }
        draft.Category[bossSlot] = RoomCategory.Boss;

        List<int> critical = CriticalPath(draft, treeParent, bossSlot);
        for (int i = 0; i < critical.Count; i++)
        {
            draft.OnCriticalPath[critical[i]] = true;
        }

        int specialBudget = ChooseSpecialCount(config, random);
        int placed = 0;

        // 关键路径上的特殊房：两端（出门口那间、Boss 前那间）保底是战斗/终点，玩家一路打得下去。
        for (int i = 1; i < critical.Count - 1 && placed < specialBudget; i++)
        {
            if (random.NextFloat() >= config.SpecialOnCriticalPathChance)
            {
                continue;
            }
            if (draft.ReplaceCategory(critical[i], RoomCategory.Combat, RoomCategory.Special))
            {
                placed++;
            }
        }

        // 关键路径以外的特殊房：优先放侧枝叶子（岔路尽头），没有叶子时再任选。
        Span<int> buffer = stackalloc int[draft.Count];
        while (placed < specialBudget)
        {
            int count = CollectSideLeaves(draft, buffer);
            if (count == 0)
            {
                count = CollectNonCriticalCombat(draft, buffer);
            }
            if (count == 0)
            {
                break;
            }
            int pick = random.NextInt(0, count);
            if (!draft.ReplaceCategory(buffer[pick], RoomCategory.Combat, RoomCategory.Special))
            {
                break;
            }
            placed++;
        }

        TryPlaceSafeRoom(random, draft);
    }

    private static int ChooseSpecialCount(DungeonConfig config, IRandomProvider random)
    {
        int min = config.MinSpecialRooms;
        int max = config.MaxSpecialRooms;
        return max > min ? random.NextInt(min, max + 1) : min;
    }

    /// <summary>侧枝叶子：非关键路径、且没有任何非关键路径邻居的战斗房。</summary>
    private static int CollectSideLeaves(Draft draft, Span<int> buffer)
    {
        int n = 0;
        for (int i = 0; i < draft.Count && n < buffer.Length; i++)
        {
            if (draft.OnCriticalPath[i] || i == draft.Start || i == draft.Boss || draft.Category[i] != RoomCategory.Combat)
            {
                continue;
            }
            bool hasNeighbour = false;
            for (int j = 0; j < draft.Count; j++)
            {
                if (j != i && !draft.OnCriticalPath[j] && DungeonGridMath.AreEdgeAdjacent(draft.Rooms[i], draft.Rooms[j]))
                {
                    hasNeighbour = true;
                    break;
                }
            }
            if (!hasNeighbour)
            {
                buffer[n++] = i;
            }
        }
        return n;
    }

    private static int CollectNonCriticalCombat(Draft draft, Span<int> buffer)
    {
        int n = 0;
        for (int i = 0; i < draft.Count && n < buffer.Length; i++)
        {
            if (!draft.OnCriticalPath[i] && i != draft.Start && draft.Category[i] == RoomCategory.Combat)
            {
                buffer[n++] = i;
            }
        }
        return n;
    }

    /// <summary>
    /// 起点旁边放一间安全房（最多 1 间），**完全由数据开关**：房型表里登记了 <see cref="RoomCategorySpec.IsSafe"/>、
    /// <c>IncludeInRandom</c> 且权重 &gt; 0 的类别才会被考虑；掷一次权重，命中就替换起点邻居里的一间战斗房。
    /// </summary>
    private void TryPlaceSafeRoom(IRandomProvider random, Draft draft)
    {
        RoomCategorySpec safeSpec = default;
        bool found = false;
        int totalWeight = 0;
        for (int i = 0; i < _categories.Specs.Count; i++)
        {
            RoomCategorySpec spec = _categories.Specs[i];
            if (!spec.IncludeInRandom || spec.Weight <= 0 || spec.Category == RoomCategory.Start)
            {
                continue;
            }
            totalWeight += spec.Weight;
            if (spec.IsSafe)
            {
                safeSpec = spec;
                found = true;
            }
        }
        if (!found || random.NextInt(0, totalWeight) >= safeSpec.Weight)
        {
            return;
        }
        Span<int> candidates = stackalloc int[draft.Count];
        int n = 0;
        for (int i = 0; i < draft.Count && n < candidates.Length; i++)
        {
            if (i != draft.Start && draft.Category[i] == RoomCategory.Combat
                && DungeonGridMath.AreEdgeAdjacent(draft.Rooms[draft.Start], draft.Rooms[i]))
            {
                candidates[n++] = i;
            }
        }
        if (n > 0)
        {
            draft.ReplaceCategory(candidates[random.NextInt(0, n)], RoomCategory.Combat, safeSpec.Category);
        }
    }

    /// <summary>起点 → Boss 的树路径（含两端）。</summary>
    private static List<int> CriticalPath(Draft draft, int[] treeParent, int bossSlot)
    {
        List<int> path = new List<int>();
        int current = bossSlot;
        for (int guard = 0; guard <= draft.Count && current >= 0; guard++)
        {
            path.Add(current);
            if (current == draft.Start)
            {
                break;
            }
            current = treeParent[current];
        }
        path.Reverse();
        return path;
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 链路
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 主图链路：树边必留；非树边（生长时形成的环）在 <see cref="DungeonConfig.LoopDistance"/> 允许时保留为捷径。
    /// 同一条边只登记一次。
    /// </summary>
    private static List<RoomLink> BuildMainLinks(
        DungeonConfig config, Draft draft, int[] depth, int[] treeParent, int bossSlot)
    {
        int n = draft.Count;
        List<RoomLink> links = new List<RoomLink>(n);
        long[] used = new long[n * MaxDegreeEstimate];
        int usedCount = 0;

        bool[] onCritical = draft.OnCriticalPath;
        List<int> critical = CriticalPath(draft, treeParent, bossSlot);
        for (int i = 0; i < critical.Count; i++)
        {
            onCritical[critical[i]] = true;
        }

        // 4.1 树边
        for (int i = 0; i < n; i++)
        {
            int parent = treeParent[i];
            if (i == draft.Start || parent < 0 || !DungeonGridMath.AreEdgeAdjacent(draft.Rooms[parent], draft.Rooms[i]))
            {
                continue;
            }
            if (TryRegisterEdge(used, ref usedCount, parent, i))
            {
                links.Add(MakeLink(draft, parent, i, onCritical, isSecret: false, links.Count));
            }
        }

        // 4.2 捷径（环边）：几何相邻但不是树边，且两端深度差 < LoopDistance 时才连。
        if (config.LoopDistance > 0)
        {
            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    if (!DungeonGridMath.AreEdgeAdjacent(draft.Rooms[a], draft.Rooms[b]))
                    {
                        continue;
                    }
                    if (Math.Abs(depth[a] - depth[b]) >= config.LoopDistance)
                    {
                        continue;
                    }
                    if (!TryRegisterEdge(used, ref usedCount, a, b))
                    {
                        continue;
                    }
                    links.Add(MakeLink(draft, a, b, onCritical, isSecret: false, links.Count));
                }
            }
        }

        return links;
    }

    /// <summary>每条边登记槽位上限（每房最多 4 条边：四向各 1）。</summary>
    private const int MaxDegreeEstimate = 4; // arch-ok: 每房度数上限（拓扑常量），不是玩法数值

    private static bool TryRegisterEdge(long[] used, ref int usedCount, int a, int b)
    {
        int lo = a < b ? a : b;
        int hi = a < b ? b : a;
        long key = ((long)lo << SlotKeyShift) | (uint)hi; // arch-ok: 64 位打包用的位宽（纯编码常量），非玩法数值
        for (int i = 0; i < usedCount; i++)
        {
            if (used[i] == key)
            {
                return false;
            }
        }
        if (usedCount >= used.Length)
        {
            return false; // 容量兜底（理论上不会发生：度数上限 × 房间数）
        }
        used[usedCount++] = key;
        return true;
    }

    private static RoomLink MakeLink(Draft draft, int a, int b, bool[] onCritical, bool isSecret, int slot)
    {
        RoomDirection direction = DungeonGridMath.LinkDirection(draft.Rooms[a], draft.Rooms[b]);
        return new RoomLink
        {
            Slot = slot,
            A = new RoomId(draft.Generation, a),
            B = new RoomId(draft.Generation, b),
            DirectionAB = direction,
            OnMainGraph = !isSecret,
            OnCriticalPath = onCritical[a] && onCritical[b],
            IsSecret = isSecret,
        };
    }

    /// <summary>每条链路两端各一扇门（单向门也一样：两面墙都有门洞，通行规则由 Kind 解释）。</summary>
    private static List<RoomDoor> BuildDoors(
        DungeonConfig config, DungeonGeneration generation, Draft draft, RoomLink[] links)
    {
        _ = config;
        List<RoomDoor> doors = new List<RoomDoor>(links.Length * DoorsPerLink);
        for (int i = 0; i < links.Length; i++)
        {
            RoomLink link = links[i];
            DoorKind kind = link.IsSecret ? DoorKind.Secret : DoorKind.Standard;
            doors.Add(new RoomDoor
            {
                Id = new DoorId(generation, doors.Count),
                Room = link.A,
                Direction = link.DirectionAB,
                Target = link.B,
                LinkSlot = i,
                Kind = kind,
            });
            doors.Add(new RoomDoor
            {
                Id = new DoorId(generation, doors.Count),
                Room = link.B,
                Direction = link.DirectionAB.Opposite(),
                Target = link.A,
                LinkSlot = i,
                Kind = kind,
            });
        }
        return doors;
    }

    /// <summary>入口门：起点房里朝向地图外的那扇墙上的门；没有朝外墙面时退化为起点房的第一扇门。</summary>
    private static int FindEntranceDoor(DungeonConfig config, Draft draft, List<RoomDoor> doors)
    {
        int fallback = -1;
        RoomPlan start = draft.Rooms[draft.Start];
        for (int i = 0; i < doors.Count; i++)
        {
            if (doors[i].Room.Slot != draft.Start)
            {
                continue;
            }
            if (fallback < 0)
            {
                fallback = i;
            }
            RoomPlan shifted = DungeonGridMath.Shifted(in start, doors[i].Direction);
            if (!DungeonGridMath.IsInsideGrid(in shifted, config.GridWidth, config.GridHeight))
            {
                return i;
            }
        }
        return fallback < 0 ? 0 : fallback;
    }

    // ─────────────────────────────────────────────────────────────
    // 5. 隐藏房
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 追加隐藏房：挑深度靠后的主图房间当出口，在它周围找「不与任何房间贴边」的空格，
    /// 再补一条秘密链路（<see cref="RoomLink.OnMainGraph"/>=false，门是 <see cref="DoorKind.Secret"/>）。
    /// 放不下返回 false（触发整张图重试）。
    /// </summary>
    private static bool AddHiddenRooms(
        DungeonConfig config, IRandomProvider random, Draft draft, int[] mainDepth, List<RoomLink> links)
    {
        // stackalloc 放在循环外：CA2014 会因为它出现在循环体内而告警
        // （连续多个隐藏房时栈帧会被反复占用，理论上有溢出风险）。
        Span<int> tried = stackalloc int[draft.Count];
        for (int h = 0; h < config.HiddenRoomCount; h++)
        {
            // 宿主房**逐个退让**再试，而不是抓到第一个放不下就让整张图作废。
            // 加了「秘密通道必须真挖得通」的判定之后，单次命中率明显下降：
            // 实测 1000 个种子里失败数从 1 涨到 46（2026-11-05）——都被这里吞掉了。
            int host = -1;
            int slot = -1;
            int triedCount = 0;
            while (triedCount < draft.Count)
            {
                int candidateHost = PickHiddenHost(draft, mainDepth, random, tried, triedCount);
                if (candidateHost < 0)
                {
                    break;
                }
                tried[triedCount++] = candidateHost;
                if (TryPlaceHidden(config, random, draft, candidateHost, links, out slot))
                {
                    host = candidateHost;
                    break;
                }
            }
            if (host < 0)
            {
                return false;
            }
            RoomPlan placed = draft.Rooms[slot];
            RoomPlan hostRoom = draft.Rooms[host];
            links.Add(new RoomLink
            {
                Slot = links.Count,
                A = new RoomId(draft.Generation, host),
                B = new RoomId(draft.Generation, slot),
                // 隐藏房与 host 不贴边，没有「公共墙面」可言；朝向取「从 host 指向隐藏房的象限方向」，
                // 语义是「秘密入口开在 host 的哪一侧」。门不落在这面墙上（FindEntrance/门洞只在主图上用）。
                DirectionAB = DungeonGridMath.DirectionOf(new GridOffset(
                    placed.Cell.X - hostRoom.Cell.X, placed.Cell.Z - hostRoom.Cell.Z)),
                OnMainGraph = false,
                OnCriticalPath = false,
                IsSecret = true,
            });
        }
        return true;
    }

    private static int PickHiddenHost(
        Draft draft, int[] mainDepth, IRandomProvider random, ReadOnlySpan<int> exclude, int excludeCount)
    {
        Span<int> deep = stackalloc int[draft.Count];
        int n = 0;
        int threshold = MaxDepth(mainDepth) / HiddenHostDepthDivisor; // arch-ok: 深度比例分母（结构性阈值），非玩法数值
        for (int i = 0; i < draft.Count && n < deep.Length; i++)
        {
            if (i == draft.Start || draft.Category[i] == RoomCategory.Boss)
            {
                continue;
            }
            if (mainDepth[i] >= threshold)
            {
                deep[n++] = i;
            }
        }
        if (n == 0)
        {
            for (int i = 0; i < draft.Count && n < deep.Length; i++)
            {
                if (i != draft.Start && draft.Category[i] != RoomCategory.Boss)
                {
                    deep[n++] = i;
                }
            }
        }
        // 去掉这一轮已经试过的宿主房，从剩下的随机挑一个。
        int avail = 0;
        for (int i = 0; i < n; i++)
        {
            bool skip = false;
            for (int k = 0; k < excludeCount; k++)
            {
                if (exclude[k] == deep[i])
                {
                    skip = true;
                    break;
                }
            }
            if (!skip)
            {
                deep[avail++] = deep[i];
            }
        }
        return avail == 0 ? -1 : deep[random.NextInt(0, avail)];
    }

    /// <summary>
    /// 围绕 <paramref name="host"/> 找「不与任何房间贴边」的空格。候选沿四向逐圈扩大、起始方向随机；
    /// 最近者优先——最近的那个最像「藏在隔壁墙后的密室」。
    /// <para>
    /// <b>2026-11-05 追加：还要求「从 host 到候选格之间确实能挖出一条不穿别人屋的墙缝」。</b>
    /// 只卡「不贴边」是不够的：实测 seed 2 隐藏房被摆在 host 正下方、
    /// 中间隔着另一间房 (R9)，秘密通道的每一条候选路线都必然穿过 R9 的格子 ——
    /// 于是通道挖不出来，隐藏房成了死房间。这类摆位必须在挑选阶段就排除。
    /// </para>
    /// </summary>
    private static bool TryPlaceHidden(
        DungeonConfig config, IRandomProvider random, Draft draft, int host, List<RoomLink> mainLinks, out int slot)
    {
        _ = mainLinks;
        slot = -1;
        RoomPlan hostRoom = draft.Rooms[host];
        int side = config.RoomSide;
        int startDir = random.NextInt(0, RoomDirectionExtensions.Count);

        for (int ring = 1; ring <= HiddenSearchRings; ring++)
        {
            for (int d = 0; d < RoomDirectionExtensions.Count; d++)
            {
                RoomDirection dir = (RoomDirection)((d + startDir) % RoomDirectionExtensions.Count);
                GridOffset step = dir.Offset();
                // 间隔按**宿主房的实际尺寸**算，不是按配置里的 RoomSide：
                // 起始/Boss 房比小房间大，用 RoomSide 会让候选位落进宿主房里（被 IsIsolated 判成重叠）；
                // 反向（RoomSide 比宿主大）又会把隐藏房推得老远。取两者较大值才两头都对。
                int hostSpan = Math.Max(hostRoom.Footprint.Width, hostRoom.Footprint.Depth);
                int gap = Math.Max(hostSpan, side) + ring;
                RoomPlan candidate = new RoomPlan
                {
                    Cell = new GridOffset(hostRoom.Cell.X + step.X * gap, hostRoom.Cell.Z + step.Z * gap),
                    Footprint = new RoomFootprint(side),
                };
                if (!IsIsolated(config, draft, in candidate))
                {
                    continue;
                }
                if (!CanConnectSecretly(config, draft, in hostRoom, in candidate, mainLinks))
                {
                    continue;
                }
                slot = draft.AddHidden(in candidate, host);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 用**真实的栅格化流程干跑一遍**，验证「秘密通道真能连通宿主房与隐藏房」。
    /// <para>
    /// 为什么不再自己写一遍 BFS：2026-11-05 实测，手写的规划器 BFS 与
    /// <c>DungeonMapBuilder.CarveSecretPassage</c> 的过滤条件（起点只认墙格、终点只认房间环上的墙格、
    /// 门旁墙格不许挖）总有出入 —— 规划器说能挖通，实际挖出来 goal=-1，
    /// 100 个种子里 21 张图的隐藏房成了死房间。唯一可靠的做法是**共用同一份实现**：
    /// 拿当前已摆好的房间造一张只含这两间的临时布局，跑一次 <see cref="DungeonMapBuilder.Build"/>，
    /// 再看隐藏房中心是否可达。
    /// </para>
    /// <para>
    /// 代价：每个候选位多一次小网格建图（9×24 级别，微秒量级），只在规划阶段、只在隐藏房候选上跑。
    /// </para>
    /// </summary>
    private static bool CanConnectSecretly(
        DungeonConfig config, Draft draft, in RoomPlan host, in RoomPlan hidden, List<RoomLink> mainLinks)
    {
        // 把**整个 draft** 都放进干跑布局：别的房间占的格子、以及主图房间之间的
        // 走廊（Corridor）都会挡住挖通道 —— 只放两间房会算出「能挖通」的假象。
        int count = draft.Count;
        RoomPlan[] rooms = new RoomPlan[count + 1];
        RoomId hostId = default;
        bool probeHostFound = false;
        DungeonGeneration generation = draft.Generation;
        for (int i = 0; i < count; i++)
        {
            rooms[i] = draft.Rooms[i];
            if (IsSame(draft.Rooms[i], in host))
            {
                hostId = new RoomId(generation, i);
                probeHostFound = true;
            }
        }
        rooms[count] = hidden;
        RoomId hiddenId = new RoomId(generation, count);
        if (!probeHostFound)
        {
            // 宿主房必须已经在 draft 里，否则这个候选位无意义。
            return false;
        }
        // 主图链路照抄（走廊位置要一致），末尾追加一条临时秘密链路。
        int mainCount = mainLinks.Count;
        RoomLink[] links = new RoomLink[mainCount + 1];
        for (int i = 0; i < mainCount; i++)
        {
            links[i] = mainLinks[i];
        }
        System.Array.Copy(mainLinks.ToArray(), links, mainCount);
        links[mainCount] = new RoomLink
        {
            Slot = mainCount,
            A = hostId,
            B = hiddenId,
            DirectionAB = DungeonGridMath.DirectionOf(new GridOffset(
                hidden.Cell.X - host.Cell.X, hidden.Cell.Z - host.Cell.Z)),
            OnMainGraph = false,
            OnCriticalPath = false,
            IsSecret = true,
        };
        // 门也照抄：CarveSecretPassage 不挖「门旁边的墙格」（门是主图入口，贴着门挖等于开门），
        // 干跑时漏掉门，就会误判成「这条路能挖」，正式建图时又被门卡死 —— 实测这类假阳性有 9/100。
        List<RoomDoor> probeDoors = BuildDoors(config, generation, draft, links);
        DungeonLayout probe = new DungeonLayout
        {
            Generation = generation,
            Seed = 0,
            Rooms = rooms,
            Links = links,
            Doors = probeDoors.ToArray(),
            Start = hostId,
            Boss = hostId,
        };
        // 关掉障碍散布：它与本判定无关，还会随种子抖动。
        DungeonMapConfig mapConfig = new DungeonMapConfig
        {
            Margin = 0,
            ObstaclesEnabled = false,
            ObstacleDensity = 0f,
            StartRoomObstacleScale = 0f,
            BossRoomObstacleScale = 0f,
            ObstacleWallClearance = 1,
            FuseMaxExtraSteps = 0,
            ReportedRouteCount = 0,
        };
        DungeonMap map = DungeonMapBuilder.Build(in probe, 0, mapConfig);
        (int hx, int hz) = map.RoomCenter(in hidden);
        if (!map.Contains(hx, hz))
        {
            return false;
        }
        // 从宿主房出发、**允许走秘密通道**地泛洪，看隐藏房中心是否可达。
        bool[] reached = FloodWithSecrets(in map);
        return reached[(hz * map.Width) + hx];
    }

    /// <summary>从地图的起始房间出发泛洪，秘密通道算可走。用于「通道到底通没通」的验证。</summary>
    private static bool[] FloodWithSecrets(in DungeonMap map)
    {
        bool[] reached = new bool[map.Width * map.Height];
        Queue<(int X, int Z)> queue = new Queue<(int, int)>();
        for (int z = 0; z < map.Height; z++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if (map.KindAt(x, z) == DungeonCellKind.Floor
                    && map.RoomAt(x, z) == map.Layout.Start.Slot)
                {
                    reached[(z * map.Width) + x] = true;
                    queue.Enqueue((x, z));
                }
            }
        }
        while (queue.Count > 0)
        {
            (int x, int z) = queue.Dequeue();
            for (int d = 0; d < SecretSteps.Length; d++)
            {
                int nx = x + SecretSteps[d].X;
                int nz = z + SecretSteps[d].Z;
                if (!map.Contains(nx, nz))
                {
                    continue;
                }
                int i = (nz * map.Width) + nx;
                if (reached[i] || !map.IsWalkable(nx, nz))
                {
                    continue;
                }
                reached[i] = true;
                queue.Enqueue((nx, nz));
            }
        }
        return reached;
    }

    private static bool IsSame(in RoomPlan a, in RoomPlan b)
        => a.Cell.X == b.Cell.X && a.Cell.Z == b.Cell.Z
        && a.Footprint.Width == b.Footprint.Width && a.Footprint.Depth == b.Footprint.Depth;

    private static readonly GridOffset[] SecretSteps =
    {
        new GridOffset(1, 0),
        new GridOffset(-1, 0),
        new GridOffset(0, 1),
        new GridOffset(0, -1),
    };

    /// <summary>
    /// 隐藏房候选判据：在网格内、不与任何房间重叠、**也不与任何房间贴边**。
    /// 「不贴边」是 <see cref="DungeonValidation"/> 认可「隐藏房没挂主图」的几何依据。
    /// </summary>
    private static bool IsIsolated(DungeonConfig config, Draft draft, in RoomPlan candidate)
    {
        if (!DungeonGridMath.IsInsideGrid(in candidate, config.GridWidth, config.GridHeight))
        {
            return false;
        }
        for (int i = 0; i < draft.Count; i++)
        {
            if (TouchesOrOverlaps(draft.Rooms[i], in candidate))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 「贴边或重叠」的**统一口径**：包围盒各向外扩 1 格后相交。
    /// <para>
    /// 为什么不直接用 <see cref="DungeonGridMath.AreEdgeAdjacent"/>：那个判定要求「另一轴有<b>正长度</b>重叠」，
    /// 于是**只共享一个角**的两间房被判成「不相邻」—— 可在栅格地图上它们是对角贴着的，
    /// 隐藏房从角上直接连通主图。规划器原来用 <c>AreEdgeAdjacent</c> 卡隔离带，
    /// 60 个种子里 30 个都栽在这个漏洞上（实测 2026-11-05）。
    /// </para>
    /// <para>
    /// 本函数与 <see cref="DungeonValidation"/> 的 <c>CheckHiddenIsolation</c> 同义，
    /// 保证「规划器认为干净」= 「校验器认为干净」，不再靠重试碰运气。
    /// </para>
    /// </summary>
    private static bool TouchesOrOverlaps(in RoomPlan a, in RoomPlan b)
    {
        (int ax0, int az0, int ax1, int az1) = DungeonGridMath.Bounds(in a);
        (int bx0, int bz0, int bx1, int bz1) = DungeonGridMath.Bounds(in b);
        return ax0 - 1 < bx1 && bx0 - 1 < ax1 && az0 - 1 < bz1 && bz0 - 1 < az1;
    }

    // ─────────────────────────────────────────────────────────────
    // 6. 锚点
    // ─────────────────────────────────────────────────────────────

    /// <summary>锚点名（房间模板与内容层共用；改名字 = 改数据契约）。</summary>
    private static class AnchorNames
    {
        public const string Center = "Center";

        public const string Spawn = "Spawn";

        public const string ExitNorth = "ExitNorth";

        public const string ExitSouth = "ExitSouth";

        public const string ExitEast = "ExitEast";

        public const string ExitWest = "ExitWest";

        public const string BossSpawn = "BossSpawn";

        public const string PlayerStart = "PlayerStart";
    }

    /// <summary>
    /// 生成全部房间的锚点（0..1 归一化局部坐标）：
    /// <c>Center</c>、按门生成的 <c>ExitNorth/ExitSouth/ExitEast/ExitWest</c>、<c>Spawn</c>，
    /// 以及起点房的 <c>PlayerStart</c>、Boss 房的 <c>BossSpawn</c>。
    /// </summary>
    private static void RefillAnchors(Draft draft, List<RoomDoor> doors)
    {
        List<RoomAnchor> anchors = new List<RoomAnchor>();
        for (int r = 0; r < draft.Count; r++)
        {
            anchors.Clear();
            RoomPlan room = draft.Rooms[r];
            (float cx, float cz) = DungeonGridMath.CenterLocal(in room);
            anchors.Add(MakeAnchor(in room, AnchorNames.Center, cx, cz, RoomDirection.South));

            bool hasNorth = false;
            bool hasSouth = false;
            bool hasEast = false;
            bool hasWest = false;
            int southDoorDir = -1;
            for (int d = 0; d < doors.Count; d++)
            {
                if (doors[d].Room.Slot != r)
                {
                    continue;
                }
                switch (doors[d].Direction)
                {
                    case RoomDirection.North:
                        hasNorth = true;
                        break;
                    case RoomDirection.South:
                        hasSouth = true;
                        southDoorDir = d;
                        break;
                    case RoomDirection.East:
                        hasEast = true;
                        break;
                    default:
                        hasWest = true;
                        break;
                }
            }
            if (hasNorth)
            {
                AddExitAnchor(anchors, in room, RoomDirection.North, AnchorNames.ExitNorth);
            }
            if (hasSouth)
            {
                AddExitAnchor(anchors, in room, RoomDirection.South, AnchorNames.ExitSouth);
            }
            if (hasEast)
            {
                AddExitAnchor(anchors, in room, RoomDirection.East, AnchorNames.ExitEast);
            }
            if (hasWest)
            {
                AddExitAnchor(anchors, in room, RoomDirection.West, AnchorNames.ExitWest);
            }

            anchors.Add(MakeAnchor(in room, AnchorNames.Spawn, cx, cz, hasSouth ? RoomDirection.South : RoomDirection.North));

            if (room.Category == RoomCategory.Boss)
            {
                // Boss 出生点：离入口最远的那面墙内侧。入口取朝南的门（没有就朝北）。
                RoomDirection entrance = southDoorDir >= 0 ? RoomDirection.South : RoomDirection.North;
                (float bx, float bz) = DeepestLocal(in room, entrance);
                anchors.Add(MakeAnchor(in room, AnchorNames.BossSpawn, bx, bz, entrance.Opposite()));
            }
            if (room.Category == RoomCategory.Start)
            {
                (float px, float pz, RoomDirection facing) = EntranceLocal(in room, doors, r);
                anchors.Add(MakeAnchor(in room, AnchorNames.PlayerStart, px, pz, facing));
            }

            draft.Rooms[r] = new RoomPlan
            {
                Id = room.Id,
                Category = room.Category,
                Archetype = room.Archetype,
                Cell = room.Cell,
                Footprint = room.Footprint,
                Depth = room.Depth,
                OnCriticalPath = room.OnCriticalPath,
                IsSecretOnly = room.IsSecretOnly,
                Anchors = anchors.ToArray(),
            };
        }
    }

    private static RoomAnchor MakeAnchor(in RoomPlan room, string name, float localX, float localZ, RoomDirection facing) => new RoomAnchor
    {
        Name = name,
        LocalX = DungeonGridMath.Normalize(localX, room.Footprint.Width),
        LocalZ = DungeonGridMath.Normalize(localZ, room.Footprint.Depth),
        Facing = facing,
    };

    /// <summary>Exit 锚点：门在那面墙上的位置与朝向。</summary>
    private static void AddExitAnchor(List<RoomAnchor> anchors, in RoomPlan room, RoomDirection direction, string name)
    {
        (float x, float z) = DungeonGridMath.DoorLocalPosition(in room, direction);
        anchors.Add(MakeAnchor(in room, name, x, z, direction.Opposite()));
    }

    /// <summary>Boss 房离入口最远的局部点（格，含小数）：沿入口反方向走到墙，另一轴居中。</summary>
    private static (float X, float Z) DeepestLocal(in RoomPlan room, RoomDirection entrance)
    {
        float maxX = room.Footprint.Width - 1;
        float maxZ = room.Footprint.Depth - 1;
        return entrance switch
        {
            RoomDirection.South => (maxX * 0.5f, LocalZero),
            RoomDirection.North => (maxX * 0.5f, maxZ),
            RoomDirection.East => (LocalZero, maxZ * 0.5f),
            RoomDirection.West => (maxX, maxZ * 0.5f),
            _ => (maxX * 0.5f, maxZ * 0.5f),
        };
    }

    /// <summary>0.0f 的结构性常量（用于「贴墙」坐标）。</summary>
    private const float LocalZero = 0f; // arch-ok: 网格原点坐标（结构性常量），不是玩法数值

    /// <summary>玩家出生点：入口门内侧一格。</summary>
    private static (float X, float Z, RoomDirection Facing) EntranceLocal(in RoomPlan room, List<RoomDoor> doors, int roomSlot)
    {
        for (int d = 0; d < doors.Count; d++)
        {
            if (doors[d].Room.Slot != roomSlot)
            {
                continue;
            }
            (float x, float z) = DungeonGridMath.DoorLocalPosition(in room, doors[d].Direction);
            RoomDirection inward = doors[d].Direction.Opposite();
            (float ix, float iz) = DungeonGridMath.DirectionVector(inward);
            // 从墙内侧再往房间内挪一格：DirectionVector 是「单位格」，直接叠加即可（>= 1 格是硬性玩法安全距离）。
            float px = DungeonGridMath.ClampLocal(x + ix, room.Footprint.Width);
            float pz = DungeonGridMath.ClampLocal(z + iz, room.Footprint.Depth);
            return (px, pz, inward);
        }
        return (LocalZero, LocalZero, RoomDirection.North);
    }

    // ─────────────────────────────────────────────────────────────
    // 内部：规划期草稿
    // ─────────────────────────────────────────────────────────────

    /// <summary>规划期的可变草稿：<see cref="RoomPlan"/> 并行数组 + 分类/关键路径。只活在一次 <see cref="Plan"/> 里。</summary>
    private sealed class Draft
    {
        public readonly RoomPlan[] Rooms;
        public readonly RoomCategory[] Category;
        public readonly bool[] OnCriticalPath;
        public readonly int TargetRooms;
        public readonly DungeonGeneration Generation;
        public int Count;
        public int Start;
        public int Boss;

        public Draft(DungeonConfig config, DungeonGeneration generation, int targetRooms)
        {
            int capacity = targetRooms + config.HiddenRoomCount + ExtraRooms;
            Rooms = new RoomPlan[capacity];
            Category = new RoomCategory[capacity];
            OnCriticalPath = new bool[capacity];
            TargetRooms = targetRooms;
            Generation = generation;
        }

        /// <summary>放起点房（类别 Start，深度 0）。</summary>
        public void AddStart(DungeonConfig config, GridOffset cell)
        {
            int slot = Count;
            Rooms[slot] = MakeRoom(slot, cell, config.StartRoomSide, RoomCategory.Start);
            Category[slot] = RoomCategory.Start;
            Start = slot;
            Count++;
        }

        /// <summary>追加一间普通房（分类阶段会按规则改类别）。</summary>
        public void AddNormal(in RoomPlan room)
        {
            int slot = Count;
            Rooms[slot] = WithSlot(in room, slot);
            Category[slot] = RoomCategory.Combat;
            Count++;
        }

        /// <summary>追加一间隐藏房（深度不可达，类别 Hidden）。</summary>
        public int AddHidden(in RoomPlan room, int host)
        {
            int slot = Count;
            Rooms[slot] = WithSlot(in room, slot);
            Category[slot] = RoomCategory.Hidden;
            Count++;
            _ = host;
            return slot;
        }

        private RoomPlan MakeRoom(int slot, GridOffset cell, int side, RoomCategory category) => new RoomPlan
        {
            Id = new RoomId(Generation, slot),
            Category = category,
            Archetype = RoomArchetypeId.None,
            Cell = cell,
            Footprint = new RoomFootprint(side),
            Depth = 0,
            OnCriticalPath = false,
            IsSecretOnly = false,
            Anchors = Array.Empty<RoomAnchor>(),
        };

        private RoomPlan WithSlot(in RoomPlan room, int slot)
        {
            // readonly struct + init-only：整体重建（不能用 copy.Id = ... 改）。
            return new RoomPlan
            {
                Id = new RoomId(Generation, slot),
                Category = room.Category,
                Archetype = room.Archetype,
                Cell = room.Cell,
                Footprint = room.Footprint,
                Depth = room.Depth,
                OnCriticalPath = room.OnCriticalPath,
                IsSecretOnly = room.IsSecretOnly,
                Anchors = room.Anchors,
            };
        }

        /// <summary>把 <paramref name="slot"/> 的类别从 <paramref name="from"/> 改为 <paramref name="to"/>；类别不符返回 false。</summary>
        public bool ReplaceCategory(int slot, RoomCategory from, RoomCategory to)
        {
            if (Category[slot] != from)
            {
                return false;
            }
            Category[slot] = to;
            return true;
        }

        /// <summary>打包成不可变数组（只拷有效范围）。</summary>
        public RoomPlan[] TakeRooms()
        {
            RoomPlan[] result = new RoomPlan[Count];
            for (int i = 0; i < Count; i++)
            {
                result[i] = Rooms[i];
            }
            return result;
        }
    }
}
