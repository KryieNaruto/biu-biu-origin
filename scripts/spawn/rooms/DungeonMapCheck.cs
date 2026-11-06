using System;
using System.Collections.Generic;

namespace Game.Spawn.Rooms;

/// <summary>
/// 地图上的「连通性体检」：把用户点名要的两件事——<b>连通性</b>与<b>合理性</b>——写成可执行的判据。
/// <para>
/// 与 <see cref="DungeonValidation"/> 的分工：后者校验**房间图**（拓扑层，房间/链路/门是不是自洽），
/// 本类校验**栅格地图**（格子层，玩家真能走通吗、有没有被障碍堵成死路）。
/// 两者都要过才算「这张图能玩」：拓扑合法但障碍把门堵死，一样是废图。
/// </para>
/// <para>
/// 判据（每条都产出中文问题描述，直接显示在编辑器面板上）：
/// </para>
/// <list type="number">
/// <item><b>起点可达性</b>：从起点房中心格出发，BFS 能走到每间房（隐藏房另算）。</item>
/// <item><b>出口可达</b>：Boss 房中心、起点房的入口门必须能走到。</item>
/// <item><b>隐藏房隔离</b>：隐藏房**不该**从主图直接走到（否则它就不「隐藏」了）；秘密通道需单独报告。</item>
/// <item><b>孤立格</b>：可达连通域之外的房间地面格（被障碍围死的口袋）。</item>
/// <item><b>陷阱格</b>：某一格的可走邻居 ≤ 1 且不是门 → 进去就出不来的死角。</item>
/// <item><b>同房连通</b>：同一间房的地面格必须在同一连通域里（障碍不能把房切两半）。</item>
/// </list>
/// <para>
/// 复杂度：两遍 BFS + 一遍度数扫描，O(W·H)。6×6 网格的图不到 200 格，随便每秒重算。
/// </para>
/// </summary>
public static class DungeonMapCheck
{
    /// <summary>连通性体检的结果。</summary>
    public struct Report
    {
        /// <summary>可通行的格数。</summary>
        public int WalkableCells { get; init; }

        /// <summary>从起点可达的格数。</summary>
        public int ReachedCells { get; init; }

        /// <summary>房间总数。</summary>
        public int RoomCount { get; init; }

        /// <summary>可达的房间数（含起点房自身；隐藏房不计入分母）。</summary>
        public int ReachedRooms { get; init; }

        /// <summary>需要可达的房间数（总数 - 隐藏房）。</summary>
        public int RequiredRooms { get; init; }

        /// <summary>Boss 房是否可达。</summary>
        public bool BossReachable { get; init; }

        /// <summary>入口门（起点房朝外的门）是否可达。</summary>
        public bool EntranceReachable { get; init; }

        /// <summary>隐藏房是否仍然「不可直达」。</summary>
        public bool HiddenRoomsIsolated { get; init; }

        /// <summary>孤立格（可达域之外的房间地面/通道格）数。</summary>
        public int OrphanCells { get; init; }

        /// <summary>陷阱格（死角）数。</summary>
        public int TrapCells { get; init; }

        /// <summary>被障碍切成两块以上的房间数。</summary>
        public int SplitRooms { get; init; }

        /// <summary>全部问题（中文；空 = 全过）。</summary>
        public List<string> Problems { get; init; }

        /// <summary>从起点到 Boss 的一条最短路线（格坐标；无路时为空）。</summary>
        public GridOffset[] Route { get; init; }

        /// <summary>其它房间的最短路线（诊断用；最多 <see cref="DungeonMapConfig.ReportedRouteCount"/> 条）。</summary>
        public GridOffset[][] SampleRoutes { get; init; }

        /// <summary>是否全部判据都通过。</summary>
        public bool IsConnected => Problems is null || Problems.Count == 0;

        /// <summary>一句话结论（面板标题用）。</summary>
        public string Verdict
        {
            get
            {
                if (!IsConnected)
                {
                    return "不连通 / 不合理：" + (Problems?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) + " 项";
                }
                return "连通 ✓ 房间 " + ReachedRooms.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + RequiredRooms.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " 格 " + ReachedCells.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + WalkableCells.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// 体检一张地图。**纯函数**：不修改地图、不碰引擎、不掷随机。
    /// </summary>
    /// <param name="map">要体检的地图。</param>
    /// <param name="includeSecretPassages">是否把秘密通道当地面走（true = 检查「隐藏房能否经秘密通道进入」）。</param>
    /// <returns>体检结果。</returns>
    public static Report Inspect(in DungeonMap map, bool includeSecretPassages = true)
    {
        List<string> problems = new List<string>();
        if (!map.IsValid)
        {
            problems.Add("地图无效（宽高/数组长度不一致）。");
            return new Report
            {
                Problems = problems,
                Route = Array.Empty<GridOffset>(),
                SampleRoutes = Array.Empty<GridOffset[]>(),
            };
        }

        DungeonLayout layout = map.Layout;
        int width = map.Width;
        int height = map.Height;
        int walkable = 0;
        for (int i = 0; i < map.Kind.Length; i++)
        {
            DungeonCellKind kind = map.Kind[i];
            if (kind.IsWalkable() && (kind != DungeonCellKind.SecretPassage || includeSecretPassages))
            {
                walkable++;
            }
        }

        // 起点：起点房中心格（房型、链路都在它周围，最稳）。
        if (!layout.Contains(layout.Start))
        {
            problems.Add("布局里没有起点房，无法做连通性体检。");
            return new Report
            {
                Problems = problems,
                WalkableCells = walkable,
                Route = Array.Empty<GridOffset>(),
                SampleRoutes = Array.Empty<GridOffset[]>(),
            };
        }
        RoomPlan start = layout[layout.Start];
        (int startX, int startZ) = map.RoomCenter(in start);
        if (!map.Contains(startX, startZ))
        {
            problems.Add("起点房中心格落在地图画布之外（检查 Margin / 房间包围盒）。");
            return new Report
            {
                Problems = problems,
                WalkableCells = walkable,
                Route = Array.Empty<GridOffset>(),
                SampleRoutes = Array.Empty<GridOffset[]>(),
            };
        }

        bool[] reached = Flood(in map, startX, startZ, includeSecretPassages);
        int reachedCells = 0;
        for (int i = 0; i < reached.Length; i++)
        {
            if (reached[i])
            {
                reachedCells++;
            }
        }

        // 逐房间：可达? 同房多连通域?
        int roomCount = layout.RoomCount;
        int hiddenRooms = 0;
        int reachedRooms = 0;
        int splitRooms = 0;
        int orphanCells = 0;
        HashSet<int> reportedSplit = new HashSet<int>();
        for (int r = 0; r < roomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            if (room.Category == RoomCategory.Hidden)
            {
                hiddenRooms++;
                continue;
            }
            if (IsRoomReached(in map, reached, in room, width))
            {
                reachedRooms++;
            }
            else if (r != layout.Start.Slot)
            {
                problems.Add("房间 " + room.Category + "@" + room.Cell + " 从起点走不到（连通性断开）。");
            }
            int domains = CountRoomDomains(in map, room, reached, width, height);
            if (domains > 1)
            {
                splitRooms++;
                if (reportedSplit.Add(r))
                {
                    problems.Add("房间 " + room.Category + "@" + room.Cell + " 被障碍切成 " + domains + " 块（房里走出不去）。");
                }
            }
        }

        // 孤立格：可走但不可达（且不属于「故意隐藏」的隐藏房）
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (z * width) + x;
                if (reached[i] || !map.Kind[i].IsWalkable())
                {
                    continue;
                }
                if (map.Kind[i] == DungeonCellKind.SecretPassage && !includeSecretPassages)
                {
                    continue;
                }
                int slot = map.RoomSalt[i];
                if (slot >= 0 && slot < roomCount && layout.Rooms[slot].Category == RoomCategory.Hidden)
                {
                    continue; // 隐藏房本来就不该从主图走到
                }
                orphanCells++;
            }
        }
        if (orphanCells > 0)
        {
            problems.Add("有 " + orphanCells.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个可走格从起点到不了（孤立口袋 / 屏障错位）。");
        }

        // 陷阱格：度数 <= 1 的非门可走格
        int traps = 0;
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (z * width) + x;
                if (!reached[i] || map.Kind[i] == DungeonCellKind.Door)
                {
                    continue;
                }
                if (Degree(in map, x, z, includeSecretPassages) <= 1)
                {
                    traps++;
                }
            }
        }
        if (traps > 0)
        {
            problems.Add("有 " + traps.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个死角格（可走但进去出不来）。");
        }

        // Boss 可达 + 路线
        GridOffset[] route = Array.Empty<GridOffset>();
        bool bossReachable = false;
        if (layout.Contains(layout.Boss))
        {
            RoomPlan boss = layout[layout.Boss];
            (int bx, int bz) = map.RoomCenter(in boss);
            bossReachable = map.Contains(bx, bz) && reached[(bz * width) + bx];
            if (!bossReachable)
            {
                problems.Add("Boss 房中心从起点走不到（这就是「打不通的图」）。");
            }
            else
            {
                route = ShortestPath(in map, startX, startZ, bx, bz, includeSecretPassages);
            }
        }

        // 入口门可达
        bool entranceReachable = false;
        if (layout.Entrance.IsValid && layout.Entrance.Slot < layout.DoorCount)
        {
            RoomDoor entrance = layout.Doors[layout.Entrance.Slot];
            (int cellX, int cellZ) = map.DoorCell(in layout, in entrance);
            entranceReachable = map.Contains(cellX, cellZ) && reached[(cellZ * width) + cellX];
            if (!entranceReachable)
            {
                problems.Add("起点房的入口门从起点走不到。");
            }
        }

        // 隐藏房隔离：用**不含秘密通道**的泛洪判定。
        // 秘密通道存在的意义就是让人能进隐藏房；含它做判定 = 「必然可达」，这条检查等于没做
        // （2026-11-05 实测：正因为这里用了含通道的 reached，100 个种子里有 37~58 张图被误报）。
        bool[] mainReached = includeSecretPassages
            ? Flood(in map, startX, startZ, includeSecretPassages: false)
            : reached;
        bool hiddenIsolated = true;
        for (int r = 0; r < roomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            if (room.Category != RoomCategory.Hidden)
            {
                continue;
            }
            (int hx, int hz) = map.RoomCenter(in room);
            if (map.Contains(hx, hz) && mainReached[(hz * width) + hx])
            {
                hiddenIsolated = false;
                bool touching = false;
                int[] touchy = DungeonMapBuilder.TouchyHiddenRooms(in layout);
                for (int t = 0; t < touchy.Length; t++)
                {
                    touching |= touchy[t] == r;
                }
                // 分开报：贴边的隐藏房是**摆位**问题（规划器该重试），不贴边却可达才是**挖通道**问题。
                problems.Add(touching
                    ? "隐藏房 " + room.Cell + " 与别的房间贴边，地图上直接连通（不是隐藏房了）；规划器应给它留 1 格隔离带。"
                    : "隐藏房 " + room.Cell + " 不出秘密通道也能走到（通道漏了 / 挖穿了墙）。");
            }
            else if (!map.Contains(hx, hz))
            {
                hiddenIsolated = false;
                problems.Add("隐藏房 " + room.Cell + " 中心格落在地图之外。");
            }
            else if (!reached[(hz * width) + hx])
            {
                hiddenIsolated = false;
                problems.Add("隐藏房 " + room.Cell + " 连秘密通道也走不进去（通道没挖通 / 挖错方向）。");
            }
        }

        GridOffset[][] samples = SampleRoutes(in map, in layout, reached, startX, startZ, width, includeSecretPassages);
        return new Report
        {
            WalkableCells = walkable,
            ReachedCells = reachedCells,
            RoomCount = roomCount,
            ReachedRooms = reachedRooms,
            RequiredRooms = roomCount - hiddenRooms,
            BossReachable = bossReachable,
            EntranceReachable = entranceReachable,
            HiddenRoomsIsolated = hiddenIsolated,
            OrphanCells = orphanCells,
            TrapCells = traps,
            SplitRooms = splitRooms,
            Problems = problems,
            Route = route,
            SampleRoutes = samples,
        };
    }

    /// <summary>从一格出发的 4 邻域洪水填充（返回可达位图）。</summary>
    /// <param name="map">地图。</param>
    /// <param name="startX">起点列。</param>
    /// <param name="startZ">起点行。</param>
    /// <param name="includeSecretPassages">是否把秘密通道算作可走。</param>
    /// <returns>长度 = 格数的可达位图。</returns>
    public static bool[] Flood(in DungeonMap map, int startX, int startZ, bool includeSecretPassages = true)
    {
        int width = map.Width;
        int height = map.Height;
        bool[] reached = new bool[width * height];
        if (!map.Contains(startX, startZ) || !IsPassable(in map, startX, startZ, includeSecretPassages))
        {
            return reached;
        }
        List<int> frontier = new List<int>(width * height);
        int startIndex = (startZ * width) + startX;
        reached[startIndex] = true;
        frontier.Add(startIndex);
        int head = 0;
        while (head < frontier.Count)
        {
            int current = frontier[head++];
            int cx = current % width;
            int cz = current / width;
            if (cx > 0)
            {
                Enqueue(in map, cx - 1, cz, reached, frontier, includeSecretPassages);
            }
            if (cx + 1 < width)
            {
                Enqueue(in map, cx + 1, cz, reached, frontier, includeSecretPassages);
            }
            if (cz > 0)
            {
                Enqueue(in map, cx, cz - 1, reached, frontier, includeSecretPassages);
            }
            if (cz + 1 < height)
            {
                Enqueue(in map, cx, cz + 1, reached, frontier, includeSecretPassages);
            }
        }
        return reached;
    }

    private static void Enqueue(in DungeonMap map, int x, int z, bool[] reached, List<int> frontier, bool includeSecret)
    {
        int i = (z * map.Width) + x;
        if (reached[i] || !IsPassable(in map, x, z, includeSecret))
        {
            return;
        }
        reached[i] = true;
        frontier.Add(i);
    }

    private static bool IsPassable(in DungeonMap map, int x, int z, bool includeSecretPassages)
    {
        DungeonCellKind kind = map.KindAt(x, z);
        if (!kind.IsWalkable())
        {
            return false;
        }
        return includeSecretPassages || kind != DungeonCellKind.SecretPassage;
    }

    /// <summary>该格在 4 邻域里有几个可走邻居。</summary>
    private static int Degree(in DungeonMap map, int x, int z, bool includeSecretPassages)
    {
        int count = 0;
        if (IsPassable(in map, x - 1, z, includeSecretPassages))
        {
            count++;
        }
        if (IsPassable(in map, x + 1, z, includeSecretPassages))
        {
            count++;
        }
        if (IsPassable(in map, x, z - 1, includeSecretPassages))
        {
            count++;
        }
        if (IsPassable(in map, x, z + 1, includeSecretPassages))
        {
            count++;
        }
        return count;
    }

    private static bool IsRoomReached(in DungeonMap map, bool[] reached, in RoomPlan room, int width)
    {
        int x0 = map.ToMapX(room.Cell.X);
        int z0 = map.ToMapZ(room.Cell.Z);
        for (int z = z0; z < z0 + room.Footprint.Depth; z++)
        {
            for (int x = x0; x < x0 + room.Footprint.Width; x++)
            {
                if (x < 0 || z < 0 || x >= width)
                {
                    continue;
                }
                int i = (z * width) + x;
                if (i >= 0 && i < reached.Length && reached[i])
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>房间内可达地面格分成几个连通域（1 = 没被障碍切开）。坐标一律走地图空间。</summary>
    private static int CountRoomDomains(in DungeonMap map, in RoomPlan room, bool[] globalReached, int width, int height)
    {
        int domains = 0;
        bool[] seen = new bool[width * height];
        List<int> stack = new List<int>(room.Footprint.Area);
        int x0 = map.ToMapX(room.Cell.X);
        int z0 = map.ToMapZ(room.Cell.Z);
        for (int z = z0; z < z0 + room.Footprint.Depth; z++)
        {
            for (int x = x0; x < x0 + room.Footprint.Width; x++)
            {
                if (x < 0 || z < 0 || x >= width || z >= height)
                {
                    continue;
                }
                int i = (z * width) + x;
                if (seen[i] || !globalReached[i] || !map.Kind[i].IsWalkable())
                {
                    continue;
                }
                domains++;
                stack.Clear();
                stack.Add(i);
                seen[i] = true;
                while (stack.Count > 0)
                {
                    int current = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    int cx = current % width;
                    int cz = current / width;
                    TryRoomStep(in map, x0, z0, in room, cx - 1, cz, globalReached, seen, stack);
                    TryRoomStep(in map, x0, z0, in room, cx + 1, cz, globalReached, seen, stack);
                    TryRoomStep(in map, x0, z0, in room, cx, cz - 1, globalReached, seen, stack);
                    TryRoomStep(in map, x0, z0, in room, cx, cz + 1, globalReached, seen, stack);
                }
            }
        }
        return domains;
    }

    private static void TryRoomStep(
        in DungeonMap map, int x0, int z0, in RoomPlan room, int x, int z,
        bool[] globalReached, bool[] seen, List<int> stack)
    {
        if (x < x0 || z < z0
            || x >= x0 + room.Footprint.Width || z >= z0 + room.Footprint.Depth)
        {
            return;
        }
        if (x < 0 || z < 0 || x >= map.Width || z >= map.Height)
        {
            return;
        }
        int i = (z * map.Width) + x;
        if (seen[i] || !globalReached[i] || !map.Kind[i].IsWalkable())
        {
            return;
        }
        seen[i] = true;
        stack.Add(i);
    }

    /// <summary>BFS 最短路径（四向，格）；无路返回空数组。</summary>
    /// <param name="map">地图。</param>
    /// <param name="fromX">起点列。</param>
    /// <param name="fromZ">起点行。</param>
    /// <param name="toX">终点列。</param>
    /// <param name="toZ">终点行。</param>
    /// <param name="includeSecretPassages">是否把秘密通道算作可走。</param>
    public static GridOffset[] ShortestPath(
        in DungeonMap map, int fromX, int fromZ, int toX, int toZ, bool includeSecretPassages = true)
    {
        int width = map.Width;
        int height = map.Height;
        if (!map.Contains(fromX, fromZ) || !map.Contains(toX, toZ))
        {
            return Array.Empty<GridOffset>();
        }
        int[] previous = new int[width * height];
        for (int i = 0; i < previous.Length; i++)
        {
            previous[i] = -2; // arch-ok: -2 = 「未访问」哨兵值，非玩法数值
        }
        int startIndex = (fromZ * width) + fromX;
        int goalIndex = (toZ * width) + toX;
        if (!IsPassable(in map, fromX, fromZ, includeSecretPassages) || !IsPassable(in map, toX, toZ, includeSecretPassages))
        {
            return Array.Empty<GridOffset>();
        }
        previous[startIndex] = -1;
        List<int> queue = new List<int>(width * height);
        queue.Add(startIndex);
        int head = 0;
        bool found = startIndex == goalIndex;
        while (head < queue.Count && !found)
        {
            int current = queue[head++];
            int cx = current % width;
            int cz = current / width;
            found = Relax(in map, previous, queue, current, cx - 1, cz, goalIndex, includeSecretPassages)
                 || Relax(in map, previous, queue, current, cx + 1, cz, goalIndex, includeSecretPassages)
                 || Relax(in map, previous, queue, current, cx, cz - 1, goalIndex, includeSecretPassages)
                 || Relax(in map, previous, queue, current, cx, cz + 1, goalIndex, includeSecretPassages);
        }
        if (previous[goalIndex] == -2)
        {
            return Array.Empty<GridOffset>();
        }
        List<GridOffset> reversed = new List<GridOffset>();
        for (int cursor = goalIndex; cursor >= 0; cursor = previous[cursor])
        {
            reversed.Add(new GridOffset(cursor % width, cursor / width));
        }
        reversed.Reverse();
        return reversed.ToArray();
    }

    private static bool Relax(
        in DungeonMap map, int[] previous, List<int> queue, int from, int x, int z, int goal, bool includeSecretPassages)
    {
        if (!map.Contains(x, z))
        {
            return false;
        }
        int i = (z * map.Width) + x;
        if (previous[i] != -2 || !IsPassable(in map, x, z, includeSecretPassages))
        {
            return false;
        }
        previous[i] = from;
        queue.Add(i);
        return i == goal;
    }

    /// <summary>其它房间的样例路线（诊断面板用：可视化「从起点能走到哪些房」）。</summary>
    private static GridOffset[][] SampleRoutes(
        in DungeonMap map, in DungeonLayout layout, bool[] reached, int startX, int startZ, int width, bool includeSecret)
    {
        int limit = map.Config.ReportedRouteCount;
        if (limit <= 0)
        {
            return Array.Empty<GridOffset[]>();
        }
        List<GridOffset[]> routes = new List<GridOffset[]>(limit);
        for (int r = 0; r < layout.RoomCount && routes.Count < limit; r++)
        {
            RoomPlan room = layout.Rooms[r];
            if (room.Category == RoomCategory.Hidden || room.Depth <= 0)
            {
                continue;
            }
            (int cx, int cz) = map.RoomCenter(in room);
            if (cx < 0 || cz < 0 || cx >= map.Width || cz >= map.Height || !reached[(cz * width) + cx])
            {
                continue;
            }
            GridOffset[] route = ShortestPath(in map, startX, startZ, cx, cz, includeSecret);
            if (route.Length > 0)
            {
                routes.Add(route);
            }
        }
        return routes.ToArray();
    }
}
