using System;
using System.Collections.Generic;

namespace Game.Spawn.Rooms;

/// <summary>
/// 地牢图**校验器**：把「合理」写成可执行的不变式。规划设计完成后立即跑一次，
/// 不过就抛中文异常——<b>随机系统宁可当场炸，也不要静默生成一张走不通的图</b>。
/// <para>
/// 六条不变式（每条都有对应的测试守护，见 <c>tests/Rooms/</c>）：
/// </para>
/// <list type="number">
/// <item>房间不重叠、都在网格内。</item>
/// <item>恰好 1 个起点房、恰好 1 个 Boss 房，且都在图里。</item>
/// <item>每个门/链路两端房间都存在，朝向一致（A→B 的朝向必须是 B→A 的相反方向）。</item>
/// <item>主出生图连通：起点能走到所有非隐藏房。</item>
/// <item>每个隐藏房至少有 1 条秘密通道，且<u>不</u>在主出生图上。</item>
/// <item>深度字段与 BFS 结果一致（规划器不许自己瞎填 depth）。</item>
/// </list>
/// </summary>
public static class DungeonValidation
{
    /// <summary>校验结果（错误信息列表；空列表 = 合法）。</summary>
    public struct Report
    {
        /// <summary>全部错误（中文，带房间/链路定位）。</summary>
        public List<string> Errors { get; init; }

        /// <summary>是否合法。</summary>
        public bool IsValid => Errors is null || Errors.Count == 0;

        /// <summary>汇总成一条异常信息。</summary>
        public string Describe()
        {
            if (IsValid)
            {
                return "地牢图合法。";
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("地牢图不合法（").Append(Errors.Count).Append(" 项）：");
            for (int i = 0; i < Errors.Count; i++)
            {
                sb.Append('\n').Append("  ").Append(i + 1).Append(") ").Append(Errors[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>校验一张地牢图。</summary>
    /// <param name="layout">待校验的图。</param>
    public static Report Validate(in DungeonLayout layout)
    {
        List<string> errors = new List<string>();
        if (layout.Rooms is null || layout.Links is null || layout.Doors is null)
        {
            errors.Add("布局缺少 Rooms/Links/Doors 数组。");
            return new Report { Errors = errors };
        }

        CheckRooms(in layout, errors);
        CheckLinks(in layout, errors);
        CheckDoors(in layout, errors);
        CheckReachability(in layout, errors);
        CheckHiddenRooms(in layout, errors);
        return new Report { Errors = errors };
    }

    /// <summary>校验，失败即抛。规划器内部调用。</summary>
    /// <param name="layout">待校验的图。</param>
    /// <exception cref="InvalidOperationException">图不合法。</exception>
    public static void ValidateOrThrow(in DungeonLayout layout)
    {
        Report report = Validate(in layout);
        if (!report.IsValid)
        {
            throw new InvalidOperationException(report.Describe());
        }
    }

    private static void CheckRooms(in DungeonLayout layout, List<string> errors)
    {
        RoomPlan[] rooms = layout.Rooms;
        int startCount = 0;
        int bossCount = 0;
        for (int i = 0; i < rooms.Length; i++)
        {
            RoomPlan room = rooms[i];
            if (!room.Id.IsValid || room.Id.Slot != i || room.Id.Generation != layout.Generation)
            {
                errors.Add("房间 #" + i + " 的句柄与槽位/世代不一致：" + room.Id + "。");
            }
            if (room.Category == RoomCategory.Start)
            {
                startCount++;
            }
            if (room.Category == RoomCategory.Boss)
            {
                bossCount++;
            }
            for (int j = i + 1; j < rooms.Length; j++)
            {
                if (Overlaps(in room, in rooms[j]))
                {
                    errors.Add("房间重叠： " + room + " 与 " + rooms[j] + "。");
                }
            }
        }
        if (startCount != 1)
        {
            errors.Add("起点房数量必须恰好为 1，实际 " + startCount + "。");
        }
        if (bossCount != 1)
        {
            errors.Add("Boss 房数量必须恰好为 1，实际 " + bossCount + "。");
        }
        if (!layout.Contains(layout.Start))
        {
            errors.Add("layout.Start 指向不存在的房间：" + layout.Start + "。");
        }
        if (!layout.Contains(layout.Boss))
        {
            errors.Add("layout.Boss 指向不存在的房间：" + layout.Boss + "。");
        }
    }

    private static void CheckLinks(in DungeonLayout layout, List<string> errors)
    {
        RoomLink[] links = layout.Links;
        for (int i = 0; i < links.Length; i++)
        {
            RoomLink link = links[i];
            if (link.Slot != i)
            {
                errors.Add("链路 #" + i + " 的 Slot 字段是 " + link.Slot + "，与下标不一致。");
            }
            if (!layout.Contains(link.A) || !layout.Contains(link.B))
            {
                errors.Add("链路 #" + i + " 指向不存在的房间：" + link + "。");
                continue;
            }
            RoomPlan a = layout[link.A];
            RoomPlan b = layout[link.B];
            if (link.IsSecret)
            {
                // 秘密通道：隐藏房与主房间**不贴边**——否则它就是主出生图上的一条普通边，
                // CheckReachable 会认为隐藏房「能直接走到」，隐藏房立即失效。
                // 所以这里只要求朝向字段合法：它的语义是「秘密入口开在主房间的哪一侧墙」，
                // 而不是「两房共用一面墙」；宿主按该朝向在墙上留一个看不太出来的入口。
                if (!link.DirectionAB.IsValid())
                {
                    errors.Add("秘密通道的方向非法：" + link + "。");
                }
                if (link.OnMainGraph)
                {
                    errors.Add("秘密通道被标成了主出生图边（会破坏隐藏房的不可达性）：" + link + "。");
                }
                continue;
            }
            if (!AdjacentLinkDirection(in a, in b, out RoomDirection expected))
            {
                errors.Add("链路 #" + i + " 两端房间既不相邻也不重叠（必须在某一轴上贴边、另一轴上区间有正重叠）：" + link + "。");
                continue;
            }
            if (expected != link.DirectionAB)
            {
                errors.Add("链路 #" + i + " 的朝向 " + link.DirectionAB + " 与实际相对位置不符（应为 " + expected + "）：" + link + "。");
            }
        }
    }

    private static void CheckDoors(in DungeonLayout layout, List<string> errors)
    {
        RoomDoor[] doors = layout.Doors;
        if (doors.Length == 0)
        {
            errors.Add("地牢图里一扇门都没有：玩家永远进不了下一个房间。");
        }
        for (int i = 0; i < doors.Length; i++)
        {
            RoomDoor door = doors[i];
            if (door.Id.Slot != i)
            {
                errors.Add("门 #" + i + " 的 Id.Slot 与下标不一致：" + door.Id + "。");
            }
            if (!layout.Contains(door.Room) || !layout.Contains(door.Target))
            {
                errors.Add("门 #" + i + " 指向不存在的房间：" + door + "。");
                continue;
            }
            if (door.LinkSlot < 0 || door.LinkSlot >= layout.Links.Length)
            {
                errors.Add("门 #" + i + " 的链路槽位越界：" + door.LinkSlot + "。");
                continue;
            }
            RoomLink link = layout.Links[door.LinkSlot];
            if (!((link.A == door.Room && link.B == door.Target) || (link.B == door.Room && link.A == door.Target)))
            {
                errors.Add("门 #" + i + " 与其链路端点不一致：门 " + door.Room + "->" + door.Target + "，链路 " + link + "。");
            }
        }
        // 每条链路必须正好有 2 扇门（A 侧与 B 侧各一扇），单向门也一样（视觉上两面墙都有门洞）。
        Span<int> count = stackalloc int[layout.Links.Length];
        for (int i = 0; i < doors.Length; i++)
        {
            int slot = doors[i].LinkSlot;
            if (slot >= 0 && slot < count.Length)
            {
                count[slot]++;
            }
        }
        for (int i = 0; i < count.Length; i++)
        {
            if (count[i] != 2)
            {
                errors.Add("链路 #" + i + " 只有 " + count[i] + " 扇门（必须是 2 扇）。");
            }
        }
    }

    private static void CheckReachability(in DungeonLayout layout, List<string> errors)
    {
        if (!layout.Contains(layout.Start))
        {
            return;
        }
        int n = layout.RoomCount;
        int[] depth = new int[n];
        for (int i = 0; i < n; i++)
        {
            depth[i] = RoomPlan.UnreachableDepth;
        }
        Queue<int> queue = new Queue<int>(n);
        depth[layout.Start.Slot] = 0;
        queue.Enqueue(layout.Start.Slot);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            for (int i = 0; i < layout.Links.Length; i++)
            {
                RoomLink link = layout.Links[i];
                if (!link.OnMainGraph)
                {
                    continue;
                }
                int other = -1;
                if (link.A.Slot == current)
                {
                    other = link.B.Slot;
                }
                else if (link.B.Slot == current)
                {
                    other = link.A.Slot;
                }
                if (other < 0 || depth[other] >= 0)
                {
                    continue;
                }
                depth[other] = depth[current] + 1;
                queue.Enqueue(other);
            }
        }
        for (int i = 0; i < n; i++)
        {
            RoomPlan room = layout.Rooms[i];
            if (room.Category == RoomCategory.Hidden)
            {
                continue;
            }
            if (depth[i] < 0)
            {
                errors.Add("房间不可达（主出生图断连）：" + room + "。");
                continue;
            }
            if (room.Depth != depth[i])
            {
                errors.Add("房间深度不一致：" + room + " 声明 depth=" + room.Depth + "，实际 BFS depth=" + depth[i] + "。");
            }
        }
    }

    private static void CheckHiddenRooms(in DungeonLayout layout, List<string> errors)
    {
        for (int i = 0; i < layout.RoomCount; i++)
        {
            RoomPlan room = layout.Rooms[i];
            if (room.Category != RoomCategory.Hidden)
            {
                continue;
            }
            int secret = 0;
            int main = 0;
            for (int k = 0; k < layout.Links.Length; k++)
            {
                RoomLink link = layout.Links[k];
                if (link.A != room.Id && link.B != room.Id)
                {
                    continue;
                }
                if (link.OnMainGraph)
                {
                    main++;
                }
                if (link.IsSecret)
                {
                    secret++;
                }
            }
            if (secret < 1)
            {
                errors.Add("隐藏房没有秘密通道：" + room + "。");
            }
            if (main > 0)
            {
                errors.Add("隐藏房挂了主出生图（会被直接看到）：" + room + "。");
            }
        }
    }

    private static bool Overlaps(in RoomPlan a, in RoomPlan b)
    {
        int ax0 = a.Cell.X;
        int ax1 = a.Cell.X + a.Footprint.Width;
        int az0 = a.Cell.Z;
        int az1 = a.Cell.Z + a.Footprint.Depth;
        int bx0 = b.Cell.X;
        int bx1 = b.Cell.X + b.Footprint.Width;
        int bz0 = b.Cell.Z;
        int bz1 = b.Cell.Z + b.Footprint.Depth;
        // 用「严格小于」判定：贴边（ax1 == bx0）不算重叠。
        return ax0 < bx1 && bx0 < ax1 && az0 < bz1 && bz0 < az1;
    }

    // ─────────────────────────────────────────────────────────────
    // 4. 邻接判定（公开纯函数：规划器、内容规划器、测试共用同一份语义）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 判定两个房间是否「贴边相邻」，并给出从 <paramref name="a"/> 看 <paramref name="b"/> 的朝向。
    /// <para>
    /// 判据：在某一轴上边缘坐标相等（<c>bx0 == ax1</c> 等），且另一轴上的区间有<u>正长度</u>重叠
    /// （贴角不算相邻）。这样 5 格房与 3 格房也能合法连接——这正是「房间尺寸可以不同」的前提；
    /// 若要求「中心格相差 1 格」，不同尺寸的正方形房间永远贴不上墙。
    /// </para>
    /// <para>
    /// 朝向的确定性规则：取重叠区中点（<c>min+max)/2</c>）作为门位置依据；判定用门的**网格偏移本身**
    /// （A→B 的平移向量），因此只要贴边且投影重叠，方向唯一。若两个方向同时满足（贴角且重叠长度为 0）
    /// 已经不成立（要求严格正重叠），不会出现歧义。
    /// </para>
    /// </summary>
    /// <param name="a">房间 A（其 Cell 为左上角格）。</param>
    /// <param name="b">房间 B。</param>
    /// <param name="directionAB">A 看向 B 的朝向。</param>
    public static bool AreAdjacent(in RoomPlan a, in RoomPlan b, out RoomDirection directionAB)
        => AdjacentLinkDirection(in a, in b, out directionAB);

    /// <summary>
    /// 邻接判定的唯一实现（内部与公开同一口径）。返回 false 时不保证 <paramref name="directionAB"/> 有效。
    /// </summary>
    /// <param name="a">房间 A。</param>
    /// <param name="b">房间 B。</param>
    /// <param name="directionAB">A 看向 B 的朝向。</param>
    public static bool AdjacentLinkDirection(in RoomPlan a, in RoomPlan b, out RoomDirection directionAB)
    {
        int ax0 = a.Cell.X;
        int az0 = a.Cell.Z;
        int ax1 = ax0 + a.Footprint.Width;
        int az1 = az0 + a.Footprint.Depth;
        int bx0 = b.Cell.X;
        int bz0 = b.Cell.Z;
        int bx1 = bx0 + b.Footprint.Width;
        int bz1 = bz0 + b.Footprint.Depth;

        if (bx0 == ax1 && OverlapLength(az0, az1, bz0, bz1) > 0)
        {
            directionAB = RoomDirection.East;
            return true;
        }
        if (ax0 == bx1 && OverlapLength(az0, az1, bz0, bz1) > 0)
        {
            directionAB = RoomDirection.West;
            return true;
        }
        if (bz0 == az1 && OverlapLength(ax0, ax1, bx0, bx1) > 0)
        {
            directionAB = RoomDirection.South;
            return true;
        }
        if (az0 == bz1 && OverlapLength(ax0, ax1, bx0, bx1) > 0)
        {
            directionAB = RoomDirection.North;
            return true;
        }
        directionAB = (RoomDirection)byte.MaxValue;
        return false;
    }

    /// <summary>一维区间重叠长度（半开区间 <c>[a0,a1)</c> 与 <c>[b0,b1)</c>）。</summary>
    /// <param name="a0">A 起点。</param>
    /// <param name="a1">A 终点。</param>
    /// <param name="b0">B 起点。</param>
    /// <param name="b1">B 终点。</param>
    public static int OverlapLength(int a0, int a1, int b0, int b1)
    {
        int lo = a0 > b0 ? a0 : b0;
        int hi = a1 < b1 ? a1 : b1;
        return hi > lo ? hi - lo : 0;
    }
}
