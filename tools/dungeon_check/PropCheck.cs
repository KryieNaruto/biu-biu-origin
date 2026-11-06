using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Core;
using Game.Core.Randomness;
using Game.Spawn.Rooms;

namespace DungeonCheck;

/// <summary>
/// 物件摆放（建筑 / 奖励 / 可破坏 / 不可破坏）的批量验证。
/// <para>
/// 验的就是一条铁律：<b>摆完东西之后，从起始房仍然能走到每一间（隐藏房除外）</b>。
/// 这条不是「物件之间别重叠」能保证的 —— 两个独立摆放的障碍可以刚好把走廊夹死，
/// 所以每一张图都必须真泛洪一次。
/// </para>
/// </summary>
internal static class PropCheck
{
    /// <summary>统计结果：各类物件平均每图多少个。</summary>
    private readonly struct Stats
    {
        public Stats(double building, double reward, double breakable, double solid, double maps)
        {
            Building = building;
            Reward = reward;
            Breakable = breakable;
            Solid = solid;
            Maps = maps;
        }

        public double Building { get; }
        public double Reward { get; }
        public double Breakable { get; }
        public double Solid { get; }
        public double Maps { get; }
    }

    /// <summary>跑 N 个种子，报告物件数量与连通性。</summary>
    /// <param name="seeds">种子数。</param>
    /// <param name="mapConfig">栅格化配置。</param>
    /// <param name="propConfig">物件配置。</param>
    /// <param name="dungeonConfig">地牢配置。</param>
    /// <param name="printGallery">打印几张字符画样例。</param>
    /// <returns>0 = 全绿；1 = 有失败。</returns>
    public static int Run(
        int seeds, DungeonMapConfig mapConfig, DungeonPropConfig propConfig, DungeonConfig dungeonConfig, int printGallery)
    {
        int maps = 0;
        int blockedFailures = 0;
        int planFailures = 0;
        int doorViolations = 0;
        int wallViolations = 0;
        long buildings = 0;
        long rewards = 0;
        long breakables = 0;
        long solids = 0;
        int galleryLeft = printGallery;
        List<string> failures = new();

        for (int seed = 0; seed < seeds; seed++)
        {
            DungeonLayout layout;
            try
            {
                IRandomProvider random = new DeterministicRandom(seed).Fork(PropSaltForPlanner);
                BranchDungeonPlanner planner = new(random, BranchDungeonPlanner.DefaultCategories());
                layout = planner.Plan(dungeonConfig, seed, new DungeonGeneration(seed + 1));
            }
            catch (Exception ex)
            {
                planFailures++;
                if (failures.Count < MaxFailureLines)
                {
                    failures.Add("seed " + seed.ToString(CultureInfo.InvariantCulture) + " 规划失败: " + ex.Message);
                }
                continue;
            }

            DungeonMap terrain = DungeonMapBuilder.Build(in layout, seed, mapConfig);
            DungeonMap map = DungeonPropPlanner.Populate(in terrain, seed, propConfig);
            maps++;

            int b = 0;
            int r = 0;
            int br = 0;
            int s = 0;
            for (int i = 0; i < map.PropCount; i++)
            {
                switch (map.Props[i].Kind)
                {
                    case DungeonPropKind.Building:
                        b++;
                        break;
                    case DungeonPropKind.Reward:
                        r++;
                        break;
                    case DungeonPropKind.Breakable:
                        br++;
                        break;
                    case DungeonPropKind.Solid:
                        s++;
                        break;
                    default:
                        break;
                }
            }
            buildings += b;
            rewards += r;
            breakables += br;
            solids += s;

            // 铁律 1：摆完东西后仍然互通。
            if (!ReachableAfterProps(in map))
            {
                blockedFailures++;
                if (failures.Count < MaxFailureLines)
                {
                    failures.Add(
                        "seed " + seed.ToString(CultureInfo.InvariantCulture)
                        + " 物件堵死了通路（建筑 " + b.ToString(CultureInfo.InvariantCulture)
                        + " / 可破坏 " + br.ToString(CultureInfo.InvariantCulture)
                        + " / 不可破坏 " + s.ToString(CultureInfo.InvariantCulture) + "）");
                    // 失败必须能复现：把这张图的字符画直接打出来，
                    // 否则「300 张里挂了 1 张」只能靠猜，定位成本远高于打一行字。
                    Console.WriteLine(map.ToArt());
                }
            }

            // 铁律 2/3：不许压在门格上、不许压在非地面格上。
            for (int i = 0; i < map.PropCount; i++)
            {
                DungeonProp prop = map.Props[i];
                for (int z = prop.Z; z < prop.Bottom; z++)
                {
                    for (int x = prop.X; x < prop.Right; x++)
                    {
                        if (map.KindAt(x, z) == DungeonCellKind.Door)
                        {
                            doorViolations++;
                        }
                        else if (map.KindAt(x, z) != DungeonCellKind.Floor)
                        {
                            wallViolations++;
                        }
                    }
                }
            }

            if (galleryLeft > 0)
            {
                galleryLeft--;
                Console.WriteLine(
                    "--- seed " + seed.ToString(CultureInfo.InvariantCulture) + " ---");
                Console.WriteLine(map.ToArt());
            }
        }

        // ── 报告 ──────────────────────────────────────────────
        Console.WriteLine("物件摆放验证：");
        Console.WriteLine(
            "  地牢图：" + maps.ToString(CultureInfo.InvariantCulture)
            + " / " + seeds.ToString(CultureInfo.InvariantCulture)
            + "（规划失败 " + planFailures.ToString(CultureInfo.InvariantCulture) + "）");
        Stats stats = new(
            maps == 0 ? 0 : (double)buildings / maps,
            maps == 0 ? 0 : (double)rewards / maps,
            maps == 0 ? 0 : (double)breakables / maps,
            maps == 0 ? 0 : (double)solids / maps,
            maps);
        Console.WriteLine("  平均每图：建筑 " + F(stats.Building) + "  奖励 " + F(stats.Reward)
            + "  可破坏 " + F(stats.Breakable) + "  不可破坏 " + F(stats.Solid));
        Console.WriteLine("  物件堵死通路：" + blockedFailures.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  物件压门：" + doorViolations.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  物件压非地面：" + wallViolations.ToString(CultureInfo.InvariantCulture));

        bool ok = blockedFailures == 0 && doorViolations == 0 && wallViolations == 0;
        if (!ok)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine("- " + failures[i]);
            }
            Console.WriteLine();
            Console.WriteLine("RESULT: 物件摆放存在问题（见上）。");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("RESULT: 物件摆放全部通过（通路未被堵死、无压门、无压墙）。");
        return 0;
    }

    private static string F(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>起始房到每间（非隐藏）房仍然可达。</summary>
    private static bool ReachableAfterProps(in DungeonMap map)
    {
        int width = map.Width;
        int total = width * map.Height;
        bool[] reached = new bool[total];
        int startSlot = map.Layout.Start.Slot;
        (int sx, int sz) = map.RoomCenter(in map.Layout.Rooms[startSlot]);
        if (!Passable(in map, sx, sz))
        {
            if (!TryOpenCellIn(map, startSlot, out sx, out sz))
            {
                return false;
            }
        }

        Queue<int> queue = new();
        int start = map.Index(sx, sz);
        reached[start] = true;
        queue.Enqueue(start);
        ReadOnlySpan<(int X, int Z)> steps = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur % width;
            int cz = cur / width;
            for (int d = 0; d < steps.Length; d++)
            {
                int nx = cx + steps[d].X;
                int nz = cz + steps[d].Z;
                if (!Passable(in map, nx, nz))
                {
                    continue;
                }
                int ni = map.Index(nx, nz);
                if (!reached[ni])
                {
                    reached[ni] = true;
                    queue.Enqueue(ni);
                }
            }
        }

        for (int slot = 0; slot < map.Layout.RoomCount; slot++)
        {
            RoomPlan room = map.Layout.Rooms[slot];
            if (room.Category == RoomCategory.Hidden || slot == startSlot)
            {
                continue;
            }
            bool any = false;
            (int x0, int z0) = (map.ToMapX(room.Cell.X), map.ToMapZ(room.Cell.Z));
            for (int dz = 0; dz < room.Footprint.Depth && !any; dz++)
            {
                for (int dx = 0; dx < room.Footprint.Width && !any; dx++)
                {
                    int cx = x0 + dx;
                    int cz = z0 + dz;
                    if (map.Contains(cx, cz) && reached[map.Index(cx, cz)])
                    {
                        any = true;
                    }
                }
            }
            if (!any)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Passable(in DungeonMap map, int x, int z)
    {
        if (!map.Contains(x, z))
        {
            return false;
        }
        int propIndex = map.PropAt(x, z);
        if (propIndex >= 0 && map.Props[propIndex].Kind != DungeonPropKind.Reward)
        {
            // 奖励是「踩过去捡」的东西，不挡路；建筑/可破坏/不可破坏都挡路。
            // 这里与 DungeonPropPlanner.ConnectivityHolds 同一口径 —— 两处口径不一致时，
            // 生成器认为合法、校验器却报「堵死」，排查会绕很久。
            return false;
        }
        DungeonCellKind kind = map.KindAt(x, z);
        return kind is DungeonCellKind.Floor or DungeonCellKind.Door
            or DungeonCellKind.Corridor or DungeonCellKind.SecretPassage;
    }

    private static bool TryOpenCellIn(in DungeonMap map, int slot, out int x, out int z)
    {
        x = -1;
        z = -1;
        RoomPlan room = map.Layout.Rooms[slot];
        (int x0, int z0) = (map.ToMapX(room.Cell.X), map.ToMapZ(room.Cell.Z));
        for (int dz = 0; dz < room.Footprint.Depth; dz++)
        {
            for (int dx = 0; dx < room.Footprint.Width; dx++)
            {
                int cx = x0 + dx;
                int cz = z0 + dz;
                if (Passable(in map, cx, cz))
                {
                    x = cx;
                    z = cz;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>规划器的随机子流盐（与 tools 里其他模式保持一致）。</summary>
    private const int PropSaltForPlanner = 0x50726F70; // arch-ok: 随机子流盐（工具侧常量）

    /// <summary>失败明细最多打印几条。</summary>
    private const int MaxFailureLines = 20;
}
