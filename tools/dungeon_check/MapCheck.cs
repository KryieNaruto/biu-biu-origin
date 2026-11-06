using System;
using System.Collections.Generic;
using System.Text;
using Game.Core.Randomness;
using Game.Spawn.Rooms;

namespace DungeonCheck;

/// <summary>
/// 2D 地图体检器：对 <b>随机生成的地牢图</b> 做栅格化 → 连通性体检 → 统计。
/// <para>
/// 这是「连通性与随机性」的第一道自动证据：主播（人）只看几张贴图，看不出「10 万次里有没有一次断连」；
/// 本程序让它在**裸 .NET** 里跑几千次并把每一次的失败原因打成中文。
/// </para>
/// <para>
/// 用法：<c>dotnet run --project tools/dungeon_check -- --map --seeds 1000</c>（详见 README.md）。
/// </para>
/// </summary>
internal static class MapCheck
{
    /// <summary>跑地图体检。</summary>
    /// <param name="seeds">种子数量。</param>
    /// <param name="mapConfig">地图参数。</param>
    /// <param name="dungeonConfig">房间图参数。</param>
    /// <param name="printGallery">是否打印前若干张字符画。</param>
    /// <returns>进程退出码（0 = 全过）。</returns>
    public static int Run(int seeds, DungeonMapConfig mapConfig, DungeonConfig dungeonConfig, int printGallery)
    {
        int failures = 0;
        int total = 0;
        long totalCells = 0;
        long totalWalkable = 0;
        long totalReached = 0;
        int totalProblems = 0;
        double totalObstacleRatio = 0.0;
        int attempted = 0;
        int planFailures = 0;
        Dictionary<string, int> problemKinds = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int seed = 1; seed <= seeds; seed++)
        {
            DungeonLayout layout;
            try
            {
                BranchDungeonPlanner planner = new BranchDungeonPlanner(
                    new DeterministicRandom(seed), BranchDungeonPlanner.DefaultCategories());
                layout = planner.Plan(dungeonConfig, seed, new DungeonGeneration(1));
            }
            catch (InvalidOperationException ex)
            {
                planFailures++;
                Console.WriteLine("[SEED " + seed + "] 房间图生成失败：" + ex.Message);
                continue;
            }

            attempted++;
            DungeonMap map = DungeonMapBuilder.Build(in layout, seed, mapConfig);
            DungeonMapCheck.Report report = DungeonMapCheck.Inspect(in map);
            total++;
            totalCells += map.CellCount;
            totalWalkable += report.WalkableCells;
            totalReached += report.ReachedCells;
            totalObstacleRatio += ObstacleRatio(in map);
            if (!report.IsConnected)
            {
                failures++;
                totalProblems += report.Problems.Count;
                for (int i = 0; i < report.Problems.Count; i++)
                {
                    string key = DefectKey(report.Problems[i]);
                    problemKinds.TryGetValue(key, out int current);
                    problemKinds[key] = current + 1;
                }
                if (failures <= GalleryFailures)
                {
                    Console.WriteLine("[SEED " + seed + "] " + report.Verdict);
                    for (int i = 0; i < report.Problems.Count; i++)
                    {
                        Console.WriteLine("    - " + report.Problems[i]);
                    }
                    Console.WriteLine(Indent(map.ToArt()));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 2D 地图体检 ===");
        Console.WriteLine("房间图生成尝试：" + attempted + " / " + seeds + "（失败 " + planFailures + "）");
        Console.WriteLine("栅格地图：" + total + " 张，平均 " + Avg(totalCells, total) + " 格");
        Console.WriteLine("平均可走格：" + Avg(totalWalkable, total) + "  平均可达格：" + Avg(totalReached, total));
        Console.WriteLine("平均障碍占比：" + (total == 0 ? "0" : (totalObstacleRatio / total).ToString("P1", System.Globalization.CultureInfo.InvariantCulture)));
        Console.WriteLine("连通性不合格：" + failures + " / " + total + (failures == 0 ? "（全部通过）" : "（问题 " + totalProblems + " 项）"));
        foreach (KeyValuePair<string, int> pair in problemKinds)
        {
            Console.WriteLine("    " + pair.Key + " × " + pair.Value);
        }

        if (printGallery > 0)
        {
            PrintGallery(printGallery, mapConfig, dungeonConfig);
        }

        bool ok = failures == 0;
        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: 2D 地图连通性全部通过。"
            : "RESULT: 2D 地图存在不合格样本（见上）。");
        if (planFailures > 0)
        {
            // 房间图规划失败不算「2D 地图不合格」—— 那是规划器的参数/容量问题，
            // 这里只如实报出，不掩盖；上层（check_all）按 exit code 判断要不要拦。
            Console.WriteLine("注意：" + planFailures + " 个种子的**房间图**没生成出来，未参与 2D 检查（原因见上）。");
        }
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 测「规划 + 栅格化 + 体检」的单次耗时。用于回答「调试面板拖滑条会不会卡」：
    /// 单次超过 16.6 ms（60 Hz 一帧）就说明拖动手感会掉，需要改成松手才算。
    /// </summary>
    /// <param name="iterations">测多少个种子。</param>
    /// <param name="mapConfig">栅格化参数。</param>
    /// <param name="dungeonConfig">房间图参数。</param>
    /// <returns>进程退出码（0 = 全部连通）。</returns>
    public static int RunBench(int iterations, DungeonMapConfig mapConfig, DungeonConfig dungeonConfig)
    {        double totalMs = 0;
        double worstMs = 0;
        double worstSteadyMs = 0;
        int gen = 0;
        int bad = 0;
        System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        for (int seed = 0; seed < iterations; seed++)
        {
            watch.Restart();
            BranchDungeonPlanner planner = new BranchDungeonPlanner(
                new Game.Core.Randomness.DeterministicRandom(seed), BranchDungeonPlanner.DefaultCategories());
            DungeonLayout layout = planner.Plan(dungeonConfig, seed, new DungeonGeneration(++gen));
            DungeonMap map = DungeonMapBuilder.Build(in layout, seed, mapConfig);
            DungeonMapCheck.Report report = DungeonMapCheck.Inspect(in map);
            watch.Stop();
            if (!report.IsConnected)
            {
                bad++;
            }
            double ms = watch.Elapsed.TotalMilliseconds;
            totalMs += ms;
            if (ms > worstMs)
            {
                worstMs = ms;
            }
            // 前几次含 JIT 与方法预热，不代表稳态手感；单独统计。
            if (seed >= WarmupIterations && ms > worstSteadyMs)
            {
                worstSteadyMs = ms;
            }
        }
        Console.WriteLine("=== 重算耗时（面板拖滑条用） ===");
        Console.WriteLine("样本 " + iterations + " 次（前 " + WarmupIterations + " 次为预热，不计入稳态）");
        Console.WriteLine("平均 " + (totalMs / iterations).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        Console.WriteLine("最差（含预热） " + worstMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        Console.WriteLine("最差（稳态） " + worstSteadyMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        Console.WriteLine(worstSteadyMs <= 16.6
            ? "RESULT: 稳态单次远低于一帧，拖滑条可以实时重画。"
            : "RESULT: 稳态单次超过一帧，拖动会掉帧；建议改成松手才重画。");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>坏样本最多打印几张完整字符画。</summary>
    private const int GalleryFailures = 3; // arch-ok: 纯诊断输出条数

    /// <summary>预热样本数：前几次含 JIT，不计入稳态统计。</summary>
    private const int WarmupIterations = 20; // arch-ok: 性能测量用的预热次数

    private static void PrintGallery(int count, DungeonMapConfig mapConfig, DungeonConfig dungeonConfig)
    {
        Console.WriteLine();
        Console.WriteLine("=== 字符画样例（图例见 docs/architecture/ARCHITECTURE.md §14.8） ===");
        int printed = 0;
        for (int seed = 1; seed <= count; seed++)
        {
            try
            {
                BranchDungeonPlanner planner = new BranchDungeonPlanner(
                    new DeterministicRandom(seed), BranchDungeonPlanner.DefaultCategories());
                DungeonLayout layout = planner.Plan(dungeonConfig, seed, new DungeonGeneration(1));
                DungeonMap map = DungeonMapBuilder.Build(in layout, seed, mapConfig);
                DungeonMapCheck.Report report = DungeonMapCheck.Inspect(in map);
                Console.WriteLine("--- seed " + seed + " --- " + report.Verdict);
                Console.WriteLine(map.ToArt());
                printed++;
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("--- seed " + seed + " --- 生成失败：" + ex.Message);
            }
        }
        Console.WriteLine("已打印 " + printed + " 张。");
    }

    private static double ObstacleRatio(in DungeonMap map)
    {
        int obstacles = 0;
        int walkable = 0;
        for (int i = 0; i < map.Kind.Length; i++)
        {
            if (map.Kind[i] == DungeonCellKind.Obstacle)
            {
                obstacles++;
            }
            else if (map.Kind[i].IsWalkable())
            {
                walkable++;
            }
        }
        int total = obstacles + walkable;
        return total == 0 ? 0.0 : (double)obstacles / total;
    }

    /// <summary>把一条中文问题归到稳定的「缺陷种类」上（统计用，去掉数字与坐标）。</summary>
    private static string DefectKey(string problem)
    {
        int colon = problem.IndexOf('：');
        string head = colon > 0 ? problem.Substring(0, colon) : problem;
        StringBuilder sb = new StringBuilder(head.Length);
        foreach (char c in head)
        {
            if (char.IsDigit(c))
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string Avg(long total, int n)
        => n == 0 ? "0" : ((double)total / n).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Indent(string art)
    {
        string[] lines = art.Split('\n');
        StringBuilder sb = new StringBuilder(art.Length + (lines.Length * 4));
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }
            sb.Append("      ").Append(lines[i]).Append('\n');
        }
        return sb.ToString();
    }
}
