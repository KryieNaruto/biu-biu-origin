using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Randomness;

namespace Game.Spawn.Rooms;

/// <summary>
/// 房间图 → 2D 字符地图的**唯一实现**（<see cref="DungeonMap"/> 的生产者）。
/// <para>
/// <b>规则</b>（每一格恰好一个字符，从「最粗」到「最细」覆盖）：
/// </para>
/// <list type="number">
/// <item>地图以最大房间包围盒为画布（+ 留白），默认整张画布先填 <see cref="DungeonCellKind.Wall"/>。</item>
/// <item>每个房间把 <c>[X, X+Side) × [Z, Z+Side)</c> 填成 <see cref="DungeonCellKind.Floor"/>（房型字符由 <see cref="DungeonMap"/> 渲染）。</item>
/// <item>链路贴边的**那一行/那一列**变成 <see cref="DungeonCellKind.Corridor"/>——两房共存这一行，
/// 于是「门 + 门外的通道格」保证</item>
/// <item>门（<see cref="RoomDoor"/>）落在重叠区中点 → <see cref="DungeonCellKind.Door"/>；
/// 两端各一扇门指向同一个几何位置（这正是「A 的门与 B 的门对得上」的可视化）</item>
/// <item>非关键路径房间撒障碍 → <see cref="DungeonCellKind.Obstacle"/>；起点房默认留空。</item>
/// </list>
/// <para>
/// <b>为什么障碍只用「随机撒 + 约束」而不是雕刻房间</b>：用户要的是「方块代表障碍」——
/// 撒完之后 <see cref="DungeonMapCheck"/> 用 BFS 验证「房内连通、跨房连通」，
/// 撒得不好就报告/修，而不是靠人肉盯图。这样「随机性」与「合理性」各自有独立的、可测的判据。
/// </para>
/// <para>
/// <b>确定性</b>：同一 seed + 同一配置 → 同一张图（逐字符）。随机只走注入的 <see cref="IRandomProvider"/>，
/// 不用 <c>Random.Shared</c>、不依赖字典遍历顺序。
/// </para>
/// </summary>
public static class DungeonMapBuilder
{
    /// <summary>障碍子流盐：与房间图、内容、装饰的随机流互不干扰（改这个值 = 让历史地图回放失效）。</summary>
    private const int ObstacleSalt = 0x4D50; // arch-ok: 随机子流盐常量（名字即契约），非玩法数值

    /// <summary>四向邻居。</summary>
    private static readonly (int Dx, int Dz)[] Neighbours =
    {
        (0, 1), // South
        (1, 0), // East
        (0, -1), // North
        (-1, 0), // West
    };

    /// <summary>
    /// 把房间图栅格化成一张 2D 地图。
    /// </summary>
    /// <param name="layout">房间图（<see cref="DungeonLayout"/>；必须已通过 <see cref="DungeonValidation"/>）。</param>
    /// <param name="seed">随机种子（障碍撒点用；同 seed 同图）。</param>
    /// <param name="config">地图参数（null = 默认）。</param>
    /// <returns>不可变的地图快照。</returns>
    /// <exception cref="InvalidOperationException">布局为空/不合法，或参数非法。</exception>
    public static DungeonMap Build(in DungeonLayout layout, int seed, DungeonMapConfig? config = null)
    {
        DungeonMapConfig mapConfig = config ?? new DungeonMapConfig();
        mapConfig.Validate();
        if (layout.Rooms is null || layout.RoomCount == 0)
        {
            throw new InvalidOperationException("地牢地图构建失败：布局里没有房间（DungeonLayout.Rooms 为空）。");
        }

        (int minX, int minZ, int maxX, int maxZ) = RoomBounds(in layout);
        int margin = mapConfig.Margin;
        // 隐藏房必须四面有墙兜住（否则它夹在别的房间中间会被直接走进去）。见 RequireIsolatedHiddenRooms。
        int width = maxX - minX + (margin * 2); // arch-ok: 画布 = 包围盒 + 两侧留白（几何换算）
        int height = maxZ - minZ + (margin * 2); // arch-ok: 画布 = 包围盒 + 上下留白（几何换算）

        int[] roomSalt = new int[width * height];
        int[] doorSlot = new int[width * height];
        DungeonCellKind[] kind = new DungeonCellKind[width * height];
        for (int i = 0; i < kind.Length; i++)
        {
            kind[i] = DungeonCellKind.Wall;
            roomSalt[i] = -1;
            doorSlot[i] = -1;
        }

        // 1) 房间地面
        for (int r = 0; r < layout.RoomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            for (int z = room.Cell.Z; z < room.Cell.Z + room.Footprint.Depth; z++)
            {
                for (int x = room.Cell.X; x < room.Cell.X + room.Footprint.Width; x++)
                {
                    int i = Index(x - minX + margin, z - minZ + margin, width);
                    kind[i] = DungeonCellKind.Floor;
                    roomSalt[i] = r;
                }
            }
        }

        // 2) 链路：共享行/列 → 通道格（门外的缓冲格）
        for (int l = 0; l < layout.LinkCount; l++)
        {
            if (!TryLinkSegment(in layout, in layout.Links[l], out GridOffset start, out GridOffset end))
            {
                continue;
            }
            FillLine(kind, width, minX - margin, minZ - margin, in start, in end, DungeonCellKind.Corridor);
        }

        // 3) 门
        for (int d = 0; d < layout.DoorCount; d++)
        {
            RoomDoor door = layout.Doors[d];
            GridOffset cell = DoorCell(in layout, in door);
            int i = Index(cell.X - minX + margin, cell.Z - minZ + margin, width);
            kind[i] = DungeonCellKind.Door;
            doorSlot[i] = d;
            if (door.Kind == DoorKind.Secret)
            {
                roomSalt[i] = door.Room.Slot;
            }
        }

        // 4) 秘密通道：隐藏房那一段改成「秘密通道地面」字符（地图上不主动显示，但确实可走）
        MarkSecretPassages(kind, doorSlot, layout, minX - margin, minZ - margin, width);

        // 5) 障碍
        if (mapConfig.ObstaclesEnabled && mapConfig.ObstacleDensity > 0f)
        {
            ScatterObstacles(kind, roomSalt, doorSlot, in layout, width, minX - margin, minZ - margin, seed, mapConfig);
        }

        return new DungeonMap
        {
            Width = width,
            Height = height,
            OriginX = -(minX - margin),
            OriginZ = -(minZ - margin),
            Layout = layout,
            Kind = kind,
            RoomSalt = roomSalt,
            DoorSlot = doorSlot,
            Seed = seed,
            Config = mapConfig,
        };
    }

    /// <summary>
    /// 找出「四面没留够隔离带」的隐藏房（只诊断，不抛异常）。
    /// <para>
    /// 为什么只是诊断：地牢是肉鸽的**主循环**，一个房间摆位细节不该让整局游戏建不出来。
    /// 真正拦截坏摆位的地方是 <see cref="DungeonValidation"/>（规划器会重试）。
    /// 这里把结论交给 <see cref="DungeonMapCheck"/>，由它把「这间隐藏房藏不住」报成一条问题。
    /// </para>
    /// </summary>
    /// <param name="layout">布局。</param>
    /// <returns>贴边/重叠的隐藏房下标（可能为空）。</returns>
    public static int[] TouchyHiddenRooms(in DungeonLayout layout)
    {
        List<int> touched = new List<int>();
        for (int r = 0; r < layout.RoomCount; r++)
        {
            if (layout.Rooms[r].Category != RoomCategory.Hidden)
            {
                continue;
            }
            for (int o = 0; o < layout.RoomCount; o++)
            {
                if (o == r || !TouchesOrOverlaps(in layout.Rooms[r], in layout.Rooms[o]))
                {
                    continue;
                }
                touched.Add(r);
                break;
            }
        }
        return touched.ToArray();
    }

    /// <summary>两房是否重叠，或紧贴（包围盒外扩 1 格后相交，含只在角上相贴）。</summary>
    private static bool TouchesOrOverlaps(in RoomPlan a, in RoomPlan b)
    {
        (int ax0, int az0, int ax1, int az1) = DungeonGridMath.Bounds(in a);
        (int bx0, int bz0, int bx1, int bz1) = DungeonGridMath.Bounds(in b);
        return ax0 - 1 < bx1 && bx0 - 1 < ax1 && az0 - 1 < bz1 && bz0 - 1 < az1;
    }

    // ─────────────────────────────────────────────────────────────
    // 几何辅助（公开：编辑器预览、内容摆放、测试都与地图共用同一套坐标换算）
    // ─────────────────────────────────────────────────────────────

    /// <summary>房间并集包围盒（格，含端点：x ∈ [minX, maxX)）。</summary>
    /// <param name="layout">布局。</param>
    public static (int MinX, int MinZ, int MaxX, int MaxZ) RoomBounds(in DungeonLayout layout)
    {
        int minX = int.MaxValue;
        int minZ = int.MaxValue;
        int maxX = int.MinValue;
        int maxZ = int.MinValue;
        for (int i = 0; i < layout.RoomCount; i++)
        {
            RoomPlan room = layout.Rooms[i];
            minX = Math.Min(minX, room.Cell.X);
            minZ = Math.Min(minZ, room.Cell.Z);
            maxX = Math.Max(maxX, room.Cell.X + room.Footprint.Width);
            maxZ = Math.Max(maxZ, room.Cell.Z + room.Footprint.Depth);
        }
        if (minX == int.MaxValue)
        {
            return (0, 0, 0, 0);
        }
        return (minX, minZ, maxX, maxZ);
    }

    /// <summary>
    /// 门在地图格里落的那一格（**地图格坐标**，不是房间局部坐标）。
    /// <para>
    /// 门在「房间局部坐标」里的位置是「重叠区中点」（见 <see cref="DungeonGridMath.TrySharedDoorPosition"/>），
    /// 可能落在两格交界处；地图格把局部坐标<b>四舍五入</b>成整数格 —— 于是共享同一段墙的两扇门
    /// 会得到同一个地图格。
    /// </para>
    /// </summary>
    /// <param name="layout">布局。</param>
    /// <param name="door">门。</param>
    public static GridOffset DoorCell(in DungeonLayout layout, in RoomDoor door)
    {
        RoomPlan room = layout[door.Room];
        RoomPlan target = layout[door.Target];
        float localX;
        float localZ;
        if (DungeonGridMath.TrySharedDoorPosition(in room, in target, door.Direction, out (float X, float Z) la, out (float X, float Z) lb))
        {
            localX = la.X;
            localZ = la.Z;
            _ = lb;
        }
        else
        {
            (float x, float z) = DungeonGridMath.DoorLocalPosition(in room, door.Direction);
            localX = x;
            localZ = z;
        }
        int cellX = room.Cell.X + (int)Math.Round(localX, MidpointRounding.AwayFromZero);
        int cellZ = room.Cell.Z + (int)Math.Round(localZ, MidpointRounding.AwayFromZero);
        return new GridOffset(
            Clamp(cellX, room.Cell.X, room.Cell.X + room.Footprint.Width - 1),
            Clamp(cellZ, room.Cell.Z, room.Cell.Z + room.Footprint.Depth - 1));
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static int Index(int x, int z, int width) => (z * width) + x;

    /// <summary>把一条水平/竖直线段上的格写成指定类型（越界自动跳过）。</summary>
    private static void FillLine(
        DungeonCellKind[] kind, int width, int originX, int originZ, in GridOffset a, in GridOffset b, DungeonCellKind fill)
    {
        if (a.Z == b.Z)
        {
            int x0 = Math.Min(a.X, b.X);
            int x1 = Math.Max(a.X, b.X);
            for (int x = x0; x <= x1; x++)
            {
                Write(kind, width, originX, originZ, x, a.Z, fill, onlyIfWall: false);
            }
            return;
        }
        if (a.X == b.X)
        {
            int z0 = Math.Min(a.Z, b.Z);
            int z1 = Math.Max(a.Z, b.Z);
            for (int z = z0; z <= z1; z++)
            {
                Write(kind, width, originX, originZ, a.X, z, fill, onlyIfWall: false);
            }
        }
    }

    private static void Write(
        DungeonCellKind[] kind, int width, int originX, int originZ, int x, int z, DungeonCellKind value, bool onlyIfWall)
    {
        int cx = x - originX;
        int cz = z - originZ;
        if (cx < 0 || cz < 0 || cx >= width || cz >= kind.Length / width)
        {
            return;
        }
        int i = Index(cx, cz, width);
        if (onlyIfWall && kind[i] != DungeonCellKind.Wall)
        {
            return;
        }
        kind[i] = value;
    }

    /// <summary>
    /// 一条链路在地图上占据的线段（两房共用边界那一行/一列，只取重叠段）。
    /// 隐藏房的秘密通道（两房不贴边）返回 false —— 它没有「共享墙面」。
    /// </summary>
    private static bool TryLinkSegment(in DungeonLayout layout, in RoomLink link, out GridOffset start, out GridOffset end)
    {
        start = default;
        end = default;
        if (!layout.Contains(link.A) || !layout.Contains(link.B))
        {
            return false;
        }
        RoomPlan a = layout[link.A];
        RoomPlan b = layout[link.B];
        if (!DungeonValidation.AdjacentLinkDirection(in a, in b, out RoomDirection direction))
        {
            return false;
        }
        (int ax0, int az0, int ax1, int az1) = DungeonGridMath.Bounds(in a);
        (int bx0, int bz0, int bx1, int bz1) = DungeonGridMath.Bounds(in b);
        if (direction == RoomDirection.East || direction == RoomDirection.West)
        {
            // 竖墙：共用 x 列 = A 的右边界（东侧）或左边界（西侧）。
            int sharedX = direction == RoomDirection.East ? ax1 : ax0;
            int lo = Math.Max(az0, bz0);
            int hi = Math.Min(az1, bz1);
            if (hi <= lo)
            {
                return false;
            }
            start = new GridOffset(sharedX, lo);
            end = new GridOffset(sharedX, hi - 1);
            return true;
        }
        int sharedZ = direction == RoomDirection.South ? az1 : az0;
        int loX = Math.Max(ax0, bx0);
        int hiX = Math.Min(ax1, bx1);
        if (hiX <= loX)
        {
            return false;
        }
        start = new GridOffset(loX, sharedZ);
        end = new GridOffset(hiX - 1, sharedZ);
        return true;
    }

    /// <summary>
    /// 挖秘密通道：从**宿主房间的边缘格**出发，只在**墙格**上做 BFS，走到**隐藏房边缘格**为止。
    /// <para>
    /// 为什么不是直线开挖（第一版就是直线，结果被实测打回）：直线会穿过别的房间 ——
    /// 隐藏房紧贴包围盒边缘时根本不存在「直线」，于是通道从另一间房的墙上冒出来，
    /// 隐藏房就变成「从主图直接走得到」了。BFS 只允许经过墙格，天然就绕开了所有地板/门/通道。
    /// </para>
    /// <para>
    /// 另外这里**不做任何 throw**：规划器没给隐藏房留隔离带时，秘密通道只是挖不通（秘密通道断开），
    /// 由 <see cref="DungeonMapCheck"/> 把问题报出来。地牢是肉鸽的**主循环**，
    /// 一个房间摆位细节不该让整局游戏建不出来。
    /// </para>
    /// </summary>
    private static void MarkSecretPassages(
        DungeonCellKind[] kind, int[] doorSlot, in DungeonLayout layout, int originX, int originZ, int width)
    {
        int height = kind.Length / width;
        for (int l = 0; l < layout.LinkCount; l++)
        {
            RoomLink link = layout.Links[l];
            if (!link.IsSecret || !layout.Contains(link.A) || !layout.Contains(link.B))
            {
                continue;
            }
            RoomPlan from = layout[link.A];
            RoomPlan to = layout[link.B];
            RoomPlan hidden = to.Category == RoomCategory.Hidden ? to : from;
            RoomPlan host = to.Category == RoomCategory.Hidden ? from : to;
            CarveSecretPassage(kind, doorSlot, in layout, width, height, originX, originZ, in host, in hidden);
        }
    }

    /// <summary>在墙格上 BFS 出一条「宿主 → 隐藏房」的通道，沿途改成 <see cref="DungeonCellKind.SecretPassage"/>。</summary>
    private static void CarveSecretPassage(
        DungeonCellKind[] kind, int[] doorSlot, in DungeonLayout layout, int width, int height, int originX, int originZ,
        in RoomPlan host, in RoomPlan hidden)
    {
        // 起点：宿主四周一圈的墙格；终点：隐藏房四周一圈的墙格。
        Queue<(int X, int Z)> queue = new Queue<(int, int)>();
        int[] previous = new int[width * height];
        for (int i = 0; i < previous.Length; i++)
        {
            previous[i] = -1;
        }
        bool[] seen = new bool[width * height];
        CollectRing(kind, doorSlot, in layout, width, height, originX, originZ, in host, in hidden, queue, seen, previous);
        int goalFrom = width * height;
        for (int z = hidden.Cell.Z - 1; z <= hidden.Cell.Z + hidden.Footprint.Depth; z++)
        {
            for (int x = hidden.Cell.X - 1; x <= hidden.Cell.X + hidden.Footprint.Width; x++)
            {
                int cx = x - originX;
                int cz = z - originZ;
                if (cx < 0 || cz < 0 || cx >= width || cz >= height)
                {
                    continue;
                }
                int i = Index(cx, cz, width);
                if (kind[i] != DungeonCellKind.Wall)
                {
                    continue;
                }
                goalFrom = i;
                goto FoundGoal;
            }
        }
    FoundGoal:
        if (goalFrom == width * height)
        {
            return;
        }
        // BFS 只在墙格上走（起点/终点两圈除外，它们本身也必须是墙格）。
        int goal = -1;
        while (queue.Count > 0)
        {
            (int x, int z) = queue.Dequeue();
            int i = Index(x - originX, z - originZ, width);
            for (int d = 0; d < Directions.Length; d++)
            {
                int nx = x + Directions[d].X;
                int nz = z + Directions[d].Z;
                int ncx = nx - originX;
                int ncz = nz - originZ;
                if (ncx < 0 || ncz < 0 || ncx >= width || ncz >= height)
                {
                    continue;
                }
                int ni = Index(ncx, ncz, width);
                if (seen[ni])
                {
                    continue;
                }
                // ── 这一格能不能挖/能不能过 ───────────────────────────────
                //   ① 必须是墙格（挖）或宿主房自己的地板（借道，不改字符）；
                //   ② 不许落在**别的房间的包围盒**里 —— 那是人家隔墙，挖穿就进屋了；
                //   ③ 不许贴着门 —— 门是主图入口，贴门的墙一挖就等于开门。
                // 注意：**不**禁止沿墙根走。一度加过「不许 8 邻接别的房间」的规则，
                // 结果 999 张图里 660 张的隐藏房彻底挖不通（2026-11-05 实测）——
                // 隐藏房本来就挤在房间之间，沿墙根走是唯一的路，禁掉等于把隐藏房废了。
                bool isWall = kind[ni] == DungeonCellKind.Wall;
                bool isHostFloor = kind[ni] == DungeonCellKind.Floor
                    && nx >= host.Cell.X && nx < host.Cell.X + host.Footprint.Width
                    && nz >= host.Cell.Z && nz < host.Cell.Z + host.Footprint.Depth;
                if (!isWall && !isHostFloor)
                {
                    continue;
                }
                if (isWall && IsInsideAnyOtherRoom(in layout, in host, in hidden, nx, nz))
                {
                    continue;
                }
                if (TouchesDoor(kind, doorSlot, width, ncx, ncz))
                {
                    continue;
                }
                // 终点判定放在所有过滤之后：只有「合格的空墙格」才允许当出口。
                bool isGoalCell = isWall && IsRingOf(in hidden, nx, nz);
                seen[ni] = true;
                previous[ni] = i;
                if (isGoalCell)
                {
                    goal = ni;
                    queue.Clear();
                    break;
                }
                queue.Enqueue((nx, nz));
            }
            if (goal >= 0)
            {
                break;
            }
        }
        for (int i = goal; i >= 0; i = previous[i])
        {
            if (kind[i] == DungeonCellKind.Wall)
            {
                kind[i] = DungeonCellKind.SecretPassage;
            }
        }
    }

    /// <summary>
    /// 某格是否落在「除宿主和隐藏房之外」的任何房间的格子范围内。
    /// <para>
    /// 秘密通道挖墙时必须问一句：这堵墙是不是别人家的？是就别挖。
    /// 同理，隐藏房自己的墙也不许动（它就是要藏）。
    /// </para>
    /// </summary>
    private static bool IsInsideAnyOtherRoom(in DungeonLayout layout, in RoomPlan host, in RoomPlan hidden, int x, int z)
    {
        for (int r = 0; r < layout.RoomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            if (room.Cell.X == host.Cell.X && room.Cell.Z == host.Cell.Z
                && room.Footprint.Width == host.Footprint.Width && room.Footprint.Depth == host.Footprint.Depth)
            {
                continue;
            }
            if (room.Cell.X == hidden.Cell.X && room.Cell.Z == hidden.Cell.Z
                && room.Footprint.Width == hidden.Footprint.Width && room.Footprint.Depth == hidden.Footprint.Depth)
            {
                continue;
            }
            if (x >= room.Cell.X && x < room.Cell.X + room.Footprint.Width
                && z >= room.Cell.Z && z < room.Cell.Z + room.Footprint.Depth)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 某格是否与「除宿主和隐藏房之外」的任何房间**紧贴**（8 邻接）。
    /// <para>
    /// 只拦「最近的一圈」：通道可以沿墙根走、也可以隔着一格走，
    /// 只要不在别的房间墙边开缝 —— 那条缝玩家一抬头就看见了，隐藏房当场暴露。
    /// </para>
    /// </summary>
    private static bool TouchesRoomDiagonally(in DungeonLayout layout, in RoomPlan host, in RoomPlan hidden, int x, int z)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if ((dx | dz) == 0)
                {
                    continue;
                }
                if (IsInsideAnyOtherRoom(in layout, in host, in hidden, x + dx, z + dz))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void CollectRing(
        DungeonCellKind[] kind, int[] doorSlot, in DungeonLayout layout, int width, int height, int originX, int originZ,
        in RoomPlan room, in RoomPlan hidden, Queue<(int X, int Z)> queue, bool[] seen, int[] previous)
    {
        for (int z = room.Cell.Z - 1; z <= room.Cell.Z + room.Footprint.Depth; z++)
        {
            for (int x = room.Cell.X - 1; x <= room.Cell.X + room.Footprint.Width; x++)
            {
                if (!IsRingOf(in room, x, z))
                {
                    continue;
                }
                int cx = x - originX;
                int cz = z - originZ;
                if (cx < 0 || cz < 0 || cx >= width || cz >= height)
                {
                    continue;
                }
                int i = Index(cx, cz, width);
                if (kind[i] != DungeonCellKind.Wall || seen[i])
                {
                    continue;
                }
                // 起点也必须是「贴门以外」的干净墙格，且不能紧挨着别的房间内的门。
                if (TouchesDoor(kind, doorSlot, width, cx, cz))
                {
                    continue;
                }
                seen[i] = true;
                previous[i] = -1;
                queue.Enqueue((x, z));
            }
        }
    }

    /// <summary>
    /// 某格是否是该房间**外扩一圈、且正对房间本体**的墙格。
    /// <para>
    /// 「正对」这一条是关键：外圈有 4 条边和 4 个角，只有<b>边上的外圈格</b>才与房间本体相邻；
    /// 角上的外圈格与本体只是斜着相贴，通道从角上钻进去等于没进去（实测 seed 5：
    /// 通道末格落在角上，隐藏房 4 格全走不到）。
    /// </para>
    /// </summary>
    private static bool IsRingOf(in RoomPlan room, int x, int z)
    {
        bool inExtended = x >= room.Cell.X - 1 && x <= room.Cell.X + room.Footprint.Width
            && z >= room.Cell.Z - 1 && z <= room.Cell.Z + room.Footprint.Depth;
        bool inBody = x >= room.Cell.X && x < room.Cell.X + room.Footprint.Width
            && z >= room.Cell.Z && z < room.Cell.Z + room.Footprint.Depth;
        if (!inExtended || inBody)
        {
            return false;
        }
        // 必须在某条边的**中段**（不落在两条边的延长线上）：即 x 严格在 [X, X+W) 或 z 严格在 [Z, Z+D)。
        bool onVerticalSide = x >= room.Cell.X && x < room.Cell.X + room.Footprint.Width;
        bool onHorizontalSide = z >= room.Cell.Z && z < room.Cell.Z + room.Footprint.Depth;
        return onVerticalSide || onHorizontalSide;
    }

    private static readonly GridOffset[] Directions =
    {
        new GridOffset(1, 0),
        new GridOffset(-1, 0),
        new GridOffset(0, 1),
        new GridOffset(0, -1),
    };

    /// <summary>
    /// 撒障碍：逐房间按「密度 × 房型系数」算目标个数，再在**离墙/离门至少有间距**的格子里随机取。
    /// 不做连通性修补 —— 那是 <see cref="DungeonMapCheck"/> 的职责（先测量，再决定是否修）。
    /// </summary>
    private static void ScatterObstacles(
        DungeonCellKind[] kind, int[] roomSalt, int[] doorSlot, in DungeonLayout layout,
        int width, int originX, int originZ, int seed, DungeonMapConfig config)
    {
        IRandomProvider random = new DeterministicRandom(seed).Fork(ObstacleSalt);
        int height = kind.Length / width;
        for (int r = 0; r < layout.RoomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            int clearance = config.ObstacleWallClearance;
            // 可用格：离墙 clearance 格以内不进，相邻格有门也不进（保证门前走道）。
            Span<int> candidates = new int[room.Footprint.Area];
            int available = 0;
            for (int z = room.Cell.Z + clearance; z < room.Cell.Z + room.Footprint.Depth - clearance; z++)
            {
                for (int x = room.Cell.X + clearance; x < room.Cell.X + room.Footprint.Width - clearance; x++)
                {
                    int cx = x - originX;
                    int cz = z - originZ;
                    if (cx < 0 || cz < 0 || cx >= width || cz >= height)
                    {
                        continue;
                    }
                    int i = Index(cx, cz, width);
                    if (kind[i] != DungeonCellKind.Floor || roomSalt[i] != r)
                    {
                        continue;
                    }
                    if (TouchesDoor(kind, doorSlot, width, cx, cz))
                    {
                        continue;
                    }
                    candidates[available++] = i;
                }
            }
            if (available == 0)
            {
                continue;
            }

            float scale = room.Category == RoomCategory.Start ? config.StartRoomObstacleScale
                : room.Category == RoomCategory.Boss ? config.BossRoomObstacleScale
                : 1f;
            int target = (int)Math.Round(available * config.ObstacleDensity * scale, MidpointRounding.AwayFromZero);
            target = Clamp(target, 0, available);
            // Fisher–Yates 部分洗牌：取前 target 个，保证「不重复 + 均匀」。
            for (int i = target; i < available; i++)
            {
                int j = random.NextInt(i, available);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            for (int i = 0; i < target; i++)
            {
                kind[candidates[i]] = DungeonCellKind.Obstacle;
            }
        }
    }

    /// <summary>该格（或其四邻）是否有门。</summary>
    private static bool TouchesDoor(DungeonCellKind[] kind, int[] doorSlot, int width, int x, int z)
    {
        int height = kind.Length / width;
        for (int n = -1; n < Neighbours.Length; n++)
        {
            int nx = n < 0 ? x : x + Neighbours[n].Dx;
            int nz = n < 0 ? z : z + Neighbours[n].Dz;
            if (nx < 0 || nz < 0 || nx >= width || nz >= height)
            {
                continue;
            }
            int i = Index(nx, nz, width);
            if (kind[i] == DungeonCellKind.Door || doorSlot[i] >= 0)
            {
                return true;
            }
        }
        return false;
    }
}
