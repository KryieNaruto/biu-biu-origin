using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Core.Randomness;
using Game.Spawn.Rooms;

namespace DungeonCheck;

/// <summary>
/// 把「同一组配置、连续几颗种子」的字符地图写成一个 Markdown 文件，
/// 用于快速肉眼比对<b>随机性</b>（不同种子该长得不一样）与<b>稳定性</b>
/// （同一颗种子必须逐字符一致）。
/// <para>
/// 输出没有行内代码围栏以外的加工：前三行是图例，后面一图一个小节，直接可贴进 issue / 文档。
/// </para>
/// </summary>
internal static class MapSamples
{
    /// <summary>写样例文件。</summary>
    /// <param name="path">输出路径。</param>
    /// <param name="count">张数。</param>
    /// <param name="mapConfig">地图参数。</param>
    /// <param name="dungeonConfig">房间图参数。</param>
    /// <returns>进程退出码（0 = 全部生成成功）。</returns>
    public static int Write(string path, int count, DungeonMapConfig mapConfig, DungeonConfig dungeonConfig)
    {
        StringBuilder sb = new StringBuilder(64 * 1024);
        sb.Append("# 地牢 2D 字符地图样例\n\n");
        sb.Append("> 由 `dotnet run --project tools/dungeon_check -- --samples <N> --out <文件>` 生成。\n");
        sb.Append("> 每格一个字符：`#` 墙 / `.` 普通房地面 / `S` 起点房 / `c` 怪物房 / `$` 商店 / `B` Boss 房 / `s` 安全房\n");
        sb.Append("> / `h` 隐藏房 / `+` 门 / `,` 门外通道 / `X` 障碍 / `?` 秘密通道。\n\n");

        int failures = 0;
        for (int seed = 1; seed <= count; seed++)
        {
            try
            {
                BranchDungeonPlanner planner = new BranchDungeonPlanner(
                    new DeterministicRandom(seed), BranchDungeonPlanner.DefaultCategories());
                DungeonLayout layout = planner.Plan(dungeonConfig, seed, new DungeonGeneration(1));
                DungeonMap map = DungeonMapBuilder.Build(in layout, seed, mapConfig);
                DungeonMapCheck.Report report = DungeonMapCheck.Inspect(in map);

                sb.Append("## seed ").Append(seed).Append(" — ").Append(report.Verdict).Append("\n\n");
                sb.Append("```\n").Append(map.ToArt()).Append("```\n\n");
                sb.Append("房间 ").Append(layout.RoomCount).Append(" 间 / 链路 ").Append(layout.LinkCount)
                  .Append(" 条 / 门 ").Append(layout.DoorCount).Append(" 扇");
                if (report.Route.Length > 0)
                {
                    sb.Append(" / 起点→Boss 最短路线 ").Append(report.Route.Length).Append(" 步");
                }
                sb.Append("\n\n");
                if (!report.IsConnected)
                {
                    failures++;
                    sb.Append("**问题：**\n");
                    for (int i = 0; i < report.Problems.Count; i++)
                    {
                        sb.Append("- ").Append(report.Problems[i]).Append('\n');
                    }
                    sb.Append('\n');
                }
            }
            catch (InvalidOperationException ex)
            {
                failures++;
                sb.Append("## seed ").Append(seed).Append(" — 生成失败\n\n```\n").Append(ex.Message).Append("\n```\n\n");
            }
        }

        sb.Append("---\n\n共 ").Append(count).Append(" 张，不合格 ").Append(failures).Append(" 张。\n");
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine("样例已写入: " + full + "（" + count + " 张，不合格 " + failures + "）");
        return failures == 0 ? 0 : 1;
    }
}
