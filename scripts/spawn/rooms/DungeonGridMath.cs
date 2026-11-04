using System;

namespace Game.Spawn.Rooms;

/// <summary>
/// 地牢网格的**纯函数工具箱**：贴边相邻判定、门位置、锚点归一化、局部分布与圆形碰撞。
/// <para>
/// <b>为什么单独一个文件、且只接收值类型</b>（抽象先行 / 规则5、规则7）：规划器里真正容易写错的
/// 是这几件几何判断（贴边、不重叠、方向相反、锚点不出房间、槽位不重叠）。它们一旦藏在
/// <see cref="BranchDungeonPlanner"/> 的私有方法里，测试就只能「跑整张图 200 次再断言」，
/// 失败时也难以定位是哪一条不变式被破坏。抽成 <c>public static</c> 纯函数后：
/// ① 测试可以对每条判定单独做性质测试；② 内容规划器 / 装饰布局器 / 将来的增量探索生成
/// 共用同一套几何语义，不会各写一份「房间左上角到底在哪」。
/// </para>
/// <para>
/// <b>零依赖</b>：不引用引擎类型、不分配、不掷随机；全部输入输出是值类型，可在裸 .NET 测试里直接调用。
/// </para>
/// <para>
/// <b>坐标系</b>：与 <see cref="RoomDirectionExtensions"/> 一致 —— <b>+X 东、+Z 南</b>。
/// <see cref="RoomPlan.Cell"/> 是房间**左上角（西北角）**格，占半开区间
/// <c>[X, X+Width) × [Z, Z+Depth)</c>。
/// </para>
/// <para>
/// <b>相邻语义</b>（Lead 2026-11-04 统一口径，见 <see cref="DungeonValidation.AreAdjacent"/>）：
/// 「某一轴边缘坐标相等 + 另一轴区间有正长度重叠」。<b>不要求同尺寸、也不要求中心格差 1 格</b>；
/// 本文件的 <see cref="AreEdgeAdjacent"/> 与它逐行同义，供规划器与测试使用。
/// </para>
/// </summary>
public static class DungeonGridMath
{
    /// <summary>0..1 归一化坐标的下界（结构性常量，不是玩法数值）。</summary>
    public const float LocalMin = 0f;

    /// <summary>0..1 归一化坐标的上界（结构性常量，不是玩法数值）。</summary>
    public const float LocalMax = 1f;

    /// <summary>浮点零判定阈值（仅用于「退化 / 除零」保护，不是玩法数值）。</summary>
    private const float DegenerateEpsilon = 1e-6f; // arch-ok: 浮点零判定阈值（数值安全常量，不是玩法数值）

    /// <summary>房间的 X（列）范围。</summary>
    /// <param name="room">房间。</param>
    public static (int Min, int Max) RangeX(in RoomPlan room) => (room.Cell.X, room.Cell.X + room.Footprint.Width);

    /// <summary>房间的 Z（行）范围。</summary>
    /// <param name="room">房间。</param>
    public static (int Min, int Max) RangeZ(in RoomPlan room) => (room.Cell.Z, room.Cell.Z + room.Footprint.Depth);

    // ─────────────────────────────────────────────────────────────
    // 1. 贴边相邻（唯一语义；DungeonValidation 的同名判定与本函数同义）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 两个房间是否贴边相邻，并给出「从 <paramref name="a"/> 看向 <paramref name="b"/>」的朝向。
    /// <para>
    /// 与 <see cref="DungeonValidation.AreAdjacent"/> **逐行同义**（前者内部即调用后者）：
    /// 某一轴边缘坐标相等 + 另一轴正长度重叠；贴角（重叠长度 0）不算相邻。
    /// </para>
    /// </summary>
    /// <param name="a">房间 A。</param>
    /// <param name="b">房间 B。</param>
    /// <param name="directionFromA">A 看向 B 的朝向（返回 false 时值无效）。</param>
    public static bool AreEdgeAdjacent(in RoomPlan a, in RoomPlan b, out RoomDirection directionFromA)
        => DungeonValidation.AreAdjacent(in a, in b, out directionFromA);

    /// <summary>两个房间是否贴边相邻（不关心朝向）。</summary>
    /// <param name="a">房间 A。</param>
    /// <param name="b">房间 B。</param>
    public static bool AreEdgeAdjacent(in RoomPlan a, in RoomPlan b)
        => DungeonValidation.AreAdjacent(in a, in b, out _);

    /// <summary>一维正长度重叠（与 <see cref="DungeonValidation.OverlapLength"/> 同义）。</summary>
    /// <param name="a0">A 起点。</param>
    /// <param name="a1">A 终点。</param>
    /// <param name="b0">B 起点。</param>
    /// <param name="b1">B 终点。</param>
    public static int Overlap1D(int a0, int a1, int b0, int b1) => DungeonValidation.OverlapLength(a0, a1, b0, b1);

    /// <summary>
    /// 两个房间之间的链路朝向：要求贴边相邻（<see cref="AreEdgeAdjacent"/>）；
    /// 不相邻时返回非法方向（<see cref="RoomDirectionExtensions.IsValid"/> 为 false）。
    /// </summary>
    /// <param name="a">房间 A。</param>
    /// <param name="b">房间 B。</param>
    public static RoomDirection LinkDirection(in RoomPlan a, in RoomPlan b)
    {
        if (DungeonValidation.AreAdjacent(in a, in b, out RoomDirection direction))
        {
            return direction;
        }
        return InvalidDirection;
    }

    /// <summary>由「B 相对 A 的格偏移」推出 A → B 的朝向；零偏移返回非法方向。用于隐藏房的近似朝向。</summary>
    /// <param name="delta">格偏移。</param>
    public static RoomDirection DirectionOf(in GridOffset delta)
    {
        if (delta.X == 0 && delta.Z == 0)
        {
            return InvalidDirection;
        }
        if (Math.Abs(delta.X) >= Math.Abs(delta.Z))
        {
            return delta.X > 0 ? RoomDirection.East : RoomDirection.West;
        }
        return delta.Z > 0 ? RoomDirection.South : RoomDirection.North;
    }

    // ─────────────────────────────────────────────────────────────
    // 2. 占用与网格边界
    // ─────────────────────────────────────────────────────────────

    /// <summary>房间占据的矩形（半开区间，单位：格）。</summary>
    /// <param name="room">房间。</param>
    public static (int X0, int Z0, int X1, int Z1) Bounds(in RoomPlan room)
        => (room.Cell.X, room.Cell.Z, room.Cell.X + room.Footprint.Width, room.Cell.Z + room.Footprint.Depth);

    /// <summary>两个房间是否重叠（贴边不算重叠；与 <see cref="DungeonValidation"/> 口径一致）。</summary>
    /// <param name="a">房间 A。</param>
    /// <param name="b">房间 B。</param>
    public static bool Overlaps(in RoomPlan a, in RoomPlan b)
    {
        (int ax0, int az0, int ax1, int az1) = Bounds(in a);
        (int bx0, int bz0, int bx1, int bz1) = Bounds(in b);
        return ax0 < bx1 && bx0 < ax1 && az0 < bz1 && bz0 < az1;
    }

    /// <summary>房间是否完整落在 <c>[0, width) × [0, height)</c> 网格内。</summary>
    /// <param name="room">房间。</param>
    /// <param name="width">网格宽（格）。</param>
    /// <param name="height">网格高（格）。</param>
    public static bool IsInsideGrid(in RoomPlan room, int width, int height)
    {
        (int x0, int z0, int x1, int z1) = Bounds(in room);
        return x0 >= 0 && z0 >= 0 && x1 <= width && z1 <= height;
    }

    /// <summary>把房间沿某方向平移一格（保持边长），用于「朝这面墙外长一格」的候选。</summary>
    /// <param name="origin">原房间。</param>
    /// <param name="direction">方向。</param>
    public static RoomPlan Shifted(in RoomPlan origin, RoomDirection direction)
    {
        GridOffset step = direction.Offset();
        // readonly struct + init-only 属性：只能整体重建。
        return new RoomPlan
        {
            Id = origin.Id,
            Category = origin.Category,
            Archetype = origin.Archetype,
            Cell = origin.Cell + step,
            Footprint = origin.Footprint,
            Depth = origin.Depth,
            OnCriticalPath = origin.OnCriticalPath,
            IsSecretOnly = origin.IsSecretOnly,
            Anchors = origin.Anchors,
        };
    }

    /// <summary>
    /// 把房间沿某方向平移到「与 <paramref name="origin"/> 贴边」的位置，并给定另一轴上的偏移。
    /// <para>
    /// 这是「长房」唯一的候选构造方式：新房间紧贴原房间的那面墙，且在侧向上错开
    /// <paramref name="sideOffset"/> 格。只要错开量让两房在侧轴上有正长度重叠，
    /// 就是一条合法链路（边长可以不同）。</para>
    /// </summary>
    /// <param name="origin">原房间。</param>
    /// <param name="direction">从原房间看新房间的方向。</param>
    /// <param name="side">
    /// 新房间边长（格）。</param>
    /// <param name="sideOffset">侧轴偏移（格，可为负）。</param>
    public static RoomPlan ShiftedToEdge(in RoomPlan origin, RoomDirection direction, int side, int sideOffset)
    {
        int width = origin.Footprint.Width;
        int depth = origin.Footprint.Depth;
        int x = origin.Cell.X;
        int z = origin.Cell.Z;
        switch (direction)
        {
            case RoomDirection.East:
                x = origin.Cell.X + width;
                z = origin.Cell.Z + sideOffset;
                break;
            case RoomDirection.West:
                x = origin.Cell.X - side;
                z = origin.Cell.Z + sideOffset;
                break;
            case RoomDirection.South:
                x = origin.Cell.X + sideOffset;
                z = origin.Cell.Z + depth;
                break;
            default: // North
                x = origin.Cell.X + sideOffset;
                z = origin.Cell.Z - side;
                break;
        }
        return new RoomPlan
        {
            Id = origin.Id,
            Archetype = origin.Archetype,
            Cell = new GridOffset(x, z),
            Footprint = new RoomFootprint(side),
            Depth = RoomPlan.UnreachableDepth,
            Anchors = Array.Empty<RoomAnchor>(),
        };
    }

    /// <summary>在已有房间集合里找与候选重叠的第一个下标；没有返回 -1。</summary>
    /// <param name="rooms">已有房间。</param>
    /// <param name="count">有效房间数。</param>
    /// <param name="candidate">候选房间。</param>
    public static int FindOverlap(RoomPlan[] rooms, int count, in RoomPlan candidate)
    {
        if (rooms is null || count <= 0)
        {
            return -1;
        }
        int limit = count < rooms.Length ? count : rooms.Length;
        RoomPlan probe = candidate;
        for (int i = 0; i < limit; i++)
        {
            if (Overlaps(in rooms[i], in probe))
            {
                return i;
            }
        }
        return -1;
    }

    // ─────────────────────────────────────────────────────────────
    // 3. 门位置（两侧共用同一份几何，保证「A 的门」与「B 的门」对得上）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 门在房间局部坐标里的位置（格，含小数）：贴在该朝向的墙上、落在两房**重叠区**的中点
    /// （不重叠时退化为该墙居中）。返回值保证在房间内（<c>[0, side-1]</c>）。
    /// </summary>
    /// <param name="room">房间。</param>
    /// <param name="direction">门所在墙的方向。</param>
    public static (float X, float Z) DoorLocalPosition(in RoomPlan room, RoomDirection direction)
    {
        // 墙的「内侧」坐标 = 边长 - 1（Cell 是左上角，房间占 [0, side) 个格，最后一格下标 side-1）。
        float wallX = room.Footprint.Width - 1;
        float wallZ = room.Footprint.Depth - 1;
        float centerX = wallX * 0.5f;
        float centerZ = wallZ * 0.5f;
        return direction switch
        {
            RoomDirection.East => (wallX, centerZ),
            RoomDirection.West => (LocalMin, centerZ),
            RoomDirection.South => (centerX, wallZ),
            RoomDirection.North => (centerX, LocalMin),
            _ => (centerX, centerZ),
        };
    }

    /// <summary>
    /// 两房之间门的位置（**同一扇门**在各自房间里的局部格坐标）：取重叠区中点，
    /// 于是 A 侧的门与 B 侧的门在世界上重合。两房必须贴边相邻。
    /// </summary>
    /// <param name="a">房间 A。</param>
    /// <param name="b">房间 B。</param>
    /// <param name="directionAB">A 看向 B 的朝向。</param>
    /// <param name="localA">门在 A 中的局部坐标（格）。</param>
    /// <param name="localB">门在 B 中的局部坐标（格）。</param>
    public static bool TrySharedDoorPosition(
        in RoomPlan a, in RoomPlan b, RoomDirection directionAB, out (float X, float Z) localA, out (float X, float Z) localB)
    {
        localA = default;
        localB = default;
        if (!DungeonValidation.AreAdjacent(in a, in b, out RoomDirection expected) || expected != directionAB)
        {
            return false;
        }
        (int ax0, int az0, int ax1, int az1) = Bounds(in a);
        (int bx0, int bz0, int bx1, int bz1) = Bounds(in b);
        if (directionAB == RoomDirection.East || directionAB == RoomDirection.West)
        {
            int lo = az0 > bz0 ? az0 : bz0;
            int hi = az1 < bz1 ? az1 : bz1;
            // 重叠区 [lo, hi) 在「格」坐标系里的中点；0.5 是半格偏移（结构性几何常量，不是玩法数值）。
            float mid = (lo + hi - 1) * 0.5f;
            float zA = ClampLocal(mid - a.Cell.Z, a.Footprint.Depth);
            float zB = ClampLocal(mid - b.Cell.Z, b.Footprint.Depth);
            localA = (directionAB == RoomDirection.East ? a.Footprint.Width - 1 : LocalMin, zA);
            localB = (directionAB == RoomDirection.East ? LocalMin : b.Footprint.Width - 1, zB);
            return true;
        }
        int loX = ax0 > bx0 ? ax0 : bx0;
        int hiX = ax1 < bx1 ? ax1 : bx1;
        float midX = (loX + hiX - 1) * 0.5f; // 同上：重叠区中点（半格偏移）
        float xa = ClampLocal(midX - a.Cell.X, a.Footprint.Width);
        float xb = ClampLocal(midX - b.Cell.X, b.Footprint.Width);
        localA = (xa, directionAB == RoomDirection.South ? a.Footprint.Depth - 1 : LocalMin);
        localB = (xb, directionAB == RoomDirection.South ? LocalMin : b.Footprint.Depth - 1);
        return true;
    }

    /// <summary>房间中心在房间局部坐标里的位置（格，含小数）。</summary>
    /// <param name="room">房间。</param>
    public static (float X, float Z) CenterLocal(in RoomPlan room)
        => ((room.Footprint.Width - 1) * 0.5f, (room.Footprint.Depth - 1) * 0.5f);

    /// <summary>从房间中心指向某朝向的单位向量（局部，单位格）。深度类布局用它做「靠这面墙」的定点。</summary>
    /// <param name="direction">方向。</param>
    public static (float X, float Z) DirectionVector(RoomDirection direction) => direction switch
    {
        RoomDirection.North => (LocalMin, -LocalMax),
        RoomDirection.South => (LocalMin, LocalMax),
        RoomDirection.East => (LocalMax, LocalMin),
        RoomDirection.West => (-LocalMax, LocalMin),
        _ => (LocalMin, LocalMin),
    };

    // ─────────────────────────────────────────────────────────────
    // 4. 归一化 / 夹取 / 圆形碰撞
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 把局部格坐标归一化到 0..1（<see cref="RoomAnchor"/> 的坐标约定）：
    /// 分母是「最长可走索引」<c>side-1</c>。边长为 1 时返回 0。
    /// </summary>
    /// <param name="local">局部格坐标。</param>
    /// <param name="side">边长（格）。</param>
    public static float Normalize(float local, int side)
    {
        float max = side - 1;
        if (max <= DegenerateEpsilon)
        {
            return LocalMin;
        }
        float result = local / max; // arch-ok: 归一化换算（分母 = side-1），不是玩法数值
        return result < LocalMin ? LocalMin : result > LocalMax ? LocalMax : result;
    }

    /// <summary>把局部格坐标夹到房间内 <c>[0, side-1]</c>。</summary>
    /// <param name="local">局部格坐标。</param>
    /// <param name="side">边长（格）。</param>
    public static float ClampLocal(float local, int side)
    {
        float max = side - 1;
        if (max < LocalMin)
        {
            return LocalMin;
        }
        return local < LocalMin ? LocalMin : local > max ? max : local;
    }

    /// <summary>
    /// 把 0..1 归一化锚点坐标换算回局部格坐标（<see cref="Normalize"/> 的逆运算，夹进房间）。
    /// <para>为什么必须是唯一实现：<see cref="RoomAnchor"/> 用 0..1、<see cref="ContentSlot"/> 用格，
    /// 两种单位混用是房间系统里最容易出错的地方，换算只允许存在这一处。</para>
    /// </summary>
    /// <param name="normalized">0..1 归一化坐标。</param>
    /// <param name="side">边长（格）。</param>
    public static float AnchorToLocal(float normalized, int side)
        => ClampLocal(normalized * (side - 1), side);

    /// <summary>极坐标取点并夹进房间（<paramref name="angle"/> 由调用方掷随机，本函数不含随机）。</summary>
    /// <param name="room">房间。</param>
    /// <param name="centerX">圆心 X（格）。</param>
    /// <param name="centerZ">圆心 Z（格）。</param>
    /// <param name="angle">角度（弧度）。</param>
    /// <param name="radius">半径（格）。</param>
    public static (float X, float Z) Polar(in RoomPlan room, float centerX, float centerZ, float angle, float radius)
    {
        float cos = (float)Math.Cos(angle);
        float sin = (float)Math.Sin(angle);
        return (ClampLocal(centerX + cos * radius, room.Footprint.Width),
                ClampLocal(centerZ + sin * radius, room.Footprint.Depth));
    }

    /// <summary>两个圆是否相交（圆心距 &lt; 半径和）。槽位防重叠与装饰避让共用这一条判定。</summary>
    /// <param name="ax">A 圆心 X。</param>
    /// <param name="az">A 圆心 Z。</param>
    /// <param name="aRadius">A 半径。</param>
    /// <param name="bx">B 圆心 X。</param>
    /// <param name="bz">B 圆心 Z。</param>
    /// <param name="bRadius">B 半径。</param>
    public static bool CirclesOverlap(float ax, float az, float aRadius, float bx, float bz, float bRadius)
    {
        float dx = ax - bx;
        float dz = az - bz;
        float sum = aRadius + bRadius;
        return dx * dx + dz * dz < sum * sum;
    }

    /// <summary>点到房间边界的最近距离（格）：离墙/离门的安全距离判定用。</summary>
    /// <param name="room">房间。</param>
    /// <param name="localX">局部 X。</param>
    /// <param name="localZ">局部 Z。</param>
    public static float DistanceToWall(in RoomPlan room, float localX, float localZ)
    {
        float maxX = room.Footprint.Width - 1;
        float maxZ = room.Footprint.Depth - 1;
        float d = localX < maxX - localX ? localX : maxX - localX;
        float dz = localZ < maxZ - localZ ? localZ : maxZ - localZ;
        return d < dz ? d : dz;
    }

    /// <summary>非法方向哨兵（越界枚举值，<see cref="RoomDirectionExtensions.IsValid"/> 为 false）。</summary>
    private static readonly RoomDirection InvalidDirection = (RoomDirection)byte.MaxValue; // arch-ok: 非法方向哨兵值，不是玩法数值
}
