using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Core;
using Game.Core.Randomness;
using Game.Spawn.Rooms;

namespace DungeonCheck;

/// <summary>
/// 地牢规划器离线校验器：批量跑种子，用 <see cref="DungeonValidation"/> 判定，并检查内容槽位。
/// <para>用法：<c>dotnet run --project tools/dungeon_check -- --seeds 200 --out report.md</c></para>
/// <para>退出码：全绿 0；任一断言失败 1；参数错误 2。</para>
/// </summary>
internal static class Program
{
    private const int DefaultSeeds = 200;
    private const int PlannerSeed = 1234;
    private const int ContentSeed = 99;
    private const int MaxFailureLines = 50;

    private static int Main(string[] args)
    {
        int seeds = DefaultSeeds;
        string outPath = "dungeon_check_report.md";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seeds" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out seeds))
                    {
                        Console.Error.WriteLine("--seeds 需要整数。");
                        return 2;
                    }
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("用法: dungeon_check [--seeds N] [--out <路径>]");
                    return 0;
                default:
                    Console.Error.WriteLine("未知参数: " + args[i]);
                    return 2;
            }
        }

        if (seeds <= 0)
        {
            Console.Error.WriteLine("--seeds 必须为正数。");
            return 2;
        }

        RunResult result = Run(seeds);
        string report = BuildReport(result, seeds);

        Console.Write(report);
        if (!string.IsNullOrEmpty(outPath))
        {
            string full = Path.GetFullPath(outPath);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(full, report, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("报告已写入: " + full);
        }

        return result.Failures.Count == 0 ? 0 : 1;
    }

    private sealed class RunResult
    {
        public List<string> Failures { get; } = new List<string>();

        public HashSet<string> LayoutHashes { get; } = new HashSet<string>(StringComparer.Ordinal);

        public int SeedsValidated { get; set; }

        public int GenerationFailures { get; set; }

        public int TotalRooms { get; set; }

        public int TotalLinks { get; set; }

        public int TotalDoors { get; set; }

        public int SecretLinks { get; set; }

        public int TotalSlots { get; set; }

        public int SlotOutOfBounds { get; set; }

        public int SlotOverlaps { get; set; }

        public int EmptyHiddenRooms { get; set; }

        public int EmptySpecialRooms { get; set; }

        public int EmptyPrototypeRooms { get; set; }

        public Dictionary<string, int> CategoryCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public Dictionary<string, int> KindCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private static RunResult Run(int seeds)
    {
        RunResult result = new RunResult();
        DungeonConfig config = BuildConfig();
        RoomContentConfig contentConfig = BuildContentConfig();
        var planner = new BranchDungeonPlanner(new DeterministicRandom(PlannerSeed), BranchDungeonPlanner.DefaultCategories());
        var contentPlanner = new GridContentPlanner(new DeterministicRandom(ContentSeed), contentConfig);

        for (int seed = 0; seed < seeds; seed++)
        {
            DungeonLayout layout;
            try
            {
                layout = planner.Plan(config, seed, new DungeonGeneration(1));
            }
            catch (Exception ex)
            {
                result.GenerationFailures++;
                result.Failures.Add("seed " + seed + " 生成抛异常: " + ex.GetType().Name + ": " + ex.Message);
                continue;
            }

            DungeonValidation.Report report = DungeonValidation.Validate(in layout);
            if (!report.IsValid)
            {
                result.Failures.Add("seed " + seed + " 布局不合法: " + JoinErrors(report));
                continue;
            }

            result.SeedsValidated++;
            result.TotalRooms += layout.RoomCount;
            result.TotalLinks += layout.LinkCount;
            result.TotalDoors += layout.DoorCount;

            for (int r = 0; r < layout.RoomCount; r++)
            {
                string key = layout.Rooms[r].Category.ToString();
                result.CategoryCounts.TryGetValue(key, out int c);
                result.CategoryCounts[key] = c + 1;
            }
            for (int l = 0; l < layout.LinkCount; l++)
            {
                if (layout.Links[l].IsSecret)
                {
                    result.SecretLinks++;
                }
            }

            // 确定性：同 seed 再生成一次必须得到同一张图
            DungeonLayout again = planner.Plan(config, seed, new DungeonGeneration(1));
            if (!SameLayout(in layout, in again))
            {
                result.Failures.Add("seed " + seed + " 不可复现（同 seed 两次生成结果不同）。");
            }
            result.LayoutHashes.Add(HashLayout(in layout));

            CheckSlots(result, in layout, seed, contentPlanner, contentConfig);
        }

        return result;
    }

    private static void CheckSlots(
        RunResult result, in DungeonLayout layout, int seed,
        GridContentPlanner contentPlanner, RoomContentConfig contentConfig)
    {
        int capacity = contentPlanner.CountRequiredSlots(in layout);
        ContentSlot[] slots;
        try
        {
            slots = contentPlanner.Plan(layout, seed, capacity);
        }
        catch (Exception ex)
        {
            result.Failures.Add("seed " + seed + " 内容规划抛异常: " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        result.TotalSlots += slots.Length;

        Dictionary<int, List<ContentSlot>> byRoom = new Dictionary<int, List<ContentSlot>>();
        for (int i = 0; i < slots.Length; i++)
        {
            ContentSlot slot = slots[i];
            if (!layout.Contains(slot.Room))
            {
                result.Failures.Add("seed " + seed + " 槽位指向不存在的房间: " + slot);
                continue;
            }

            RoomPlan room = layout[slot.Room];
            if (slot.LocalX < 0f || slot.LocalZ < 0f
                || slot.LocalX > room.Footprint.Width - 1 || slot.LocalZ > room.Footprint.Depth - 1)
            {
                result.SlotOutOfBounds++;
                result.Failures.Add("seed " + seed + " 槽位越出房间边界: " + slot);
            }

            if (!byRoom.TryGetValue(slot.Room.Slot, out List<ContentSlot>? list))
            {
                list = new List<ContentSlot>();
                byRoom[slot.Room.Slot] = list;
            }
            for (int j = 0; j < list.Count; j++)
            {
                // 保底槽位允许挤：只把「圆心几乎重合」记为问题，间距偏小不算错。
                if (DungeonGridMath.CirclesOverlap(slot.LocalX, slot.LocalZ, slot.Radius,
                        list[j].LocalX, list[j].LocalZ, list[j].Radius * HalfRadiusFactor))
                {
                    result.SlotOverlaps++;
                    result.Failures.Add("seed " + seed + " 槽位重叠: " + slot + " 与 " + list[j]);
                }
            }
            list.Add(slot);

            string kindKey = slot.Kind.ToString();
            result.KindCounts.TryGetValue(kindKey, out int kc);
            result.KindCounts[kindKey] = kc + 1;
        }

        for (int r = 0; r < layout.RoomCount; r++)
        {
            RoomPlan room = layout.Rooms[r];
            byRoom.TryGetValue(room.Id.Slot, out List<ContentSlot>? roomSlots);
            bool hasInteractable = ContainsKind(roomSlots, ContentSlotKind.Interactable);
            bool hasProp = ContainsKind(roomSlots, ContentSlotKind.Prop);
            bool hasReward = ContainsKind(roomSlots, ContentSlotKind.Reward);
            bool hasSpawn = ContainsKind(roomSlots, ContentSlotKind.Spawn);

            switch (room.Category)
            {
                case RoomCategory.Special:
                    if (contentConfig.SpecialRoomInteractableCount > 0 && !hasInteractable && !hasProp)
                    {
                        result.EmptySpecialRooms++;
                        result.Failures.Add("seed " + seed + " 特殊房没有任何交互物/道具: " + room.Id);
                    }
                    break;
                case RoomCategory.Hidden:
                    if (contentConfig.HiddenRoomRewardCount > 0 && !hasReward)
                    {
                        result.EmptyHiddenRooms++;
                        result.Failures.Add("seed " + seed + " 隐藏房没有奖励: " + room.Id);
                    }
                    break;
                case RoomCategory.Prototype:
                    if (!hasSpawn)
                    {
                        result.EmptyPrototypeRooms++;
                        result.Failures.Add("seed " + seed + " 原型房没有刷怪点: " + room.Id);
                    }
                    break;
            }
        }
    }

    private const float HalfRadiusFactor = 0.5f; // arch-ok: 重叠容忍系数（半个半径），结构性阈值

    private static bool ContainsKind(List<ContentSlot>? slots, ContentSlotKind kind)
    {
        if (slots is null)
        {
            return false;
        }
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Kind == kind)
            {
                return true;
            }
        }
        return false;
    }

    private static DungeonConfig BuildConfig() => new DungeonConfig
    {
        GridWidth = 9,
        GridHeight = 24,
        RoomSide = 3,
        StartRoomSide = 5,
        BossRoomSide = 5,
        TargetRoomCount = 9,
        RoomCountTolerance = 2,
        HiddenRoomCount = 1,
        MinSpecialRooms = 1,
        MaxSpecialRooms = 3,
        MinBossDepth = 3,
        RoomsPerStep = 6,
        SpecialOnCriticalPathChance = 0.2f,
        MinHiddenDepth = 2,
        LoopDistance = 2,
        CellSizeMeters = 2f,
    };

    private static RoomContentConfig BuildContentConfig() => new RoomContentConfig
    {
        StartRoomSpawnCount = 0,
        CombatRoomSpawnCount = 4,
        SpecialRoomSpawnCount = 0,
        BossRoomSpawnCount = 2,
        HiddenRoomSpawnCount = 0,
        SpecialRoomInteractableCount = 1,
        CombatRoomPropCount = 1,
        HiddenRoomRewardCount = 2,
        SlotRadius = 0.45f,
        WallClearance = 0.6f,
        DoorClearance = 0.9f,
        SlotPadding = 0.3f,
        PlacementAttempts = 60,
        SpawnRadiusMin = 0f,
        SpawnRadiusMax = 2f,
    };

    private static bool SameLayout(in DungeonLayout a, in DungeonLayout b)
    {
        if (a.RoomCount != b.RoomCount || a.LinkCount != b.LinkCount || a.DoorCount != b.DoorCount)
        {
            return false;
        }
        for (int i = 0; i < a.RoomCount; i++)
        {
            RoomPlan x = a.Rooms[i];
            RoomPlan y = b.Rooms[i];
            if (x.Id != y.Id || x.Category != y.Category || x.Cell != y.Cell || x.Depth != y.Depth
                || x.Footprint.Side != y.Footprint.Side || x.OnCriticalPath != y.OnCriticalPath)
            {
                return false;
            }
        }
        for (int i = 0; i < a.LinkCount; i++)
        {
            if (a.Links[i].A != b.Links[i].A || a.Links[i].B != b.Links[i].B
                || a.Links[i].DirectionAB != b.Links[i].DirectionAB || a.Links[i].IsSecret != b.Links[i].IsSecret)
            {
                return false;
            }
        }
        return a.Start == b.Start && a.Boss == b.Boss;
    }

    private static string HashLayout(in DungeonLayout layout)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < layout.RoomCount; i++)
        {
            RoomPlan r = layout.Rooms[i];
            sb.Append(r.Id.Slot).Append(',')
              .Append((int)r.Category).Append(',')
              .Append(r.Cell.X).Append(',')
              .Append(r.Cell.Z).Append(',')
              .Append(r.Footprint.Side).Append(',')
              .Append(r.Depth).Append(';');
        }
        for (int i = 0; i < layout.LinkCount; i++)
        {
            RoomLink link = layout.Links[i];
            sb.Append(link.A.Slot).Append('-').Append(link.B.Slot).Append(',')
              .Append((int)link.DirectionAB).Append(link.IsSecret ? ",S" : ",M").Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string JoinErrors(DungeonValidation.Report report)
    {
        if (report.Errors is null)
        {
            return "(无明细)";
        }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < report.Errors.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" / ");
            }
            sb.Append(report.Errors[i]);
        }
        return sb.ToString();
    }

    private static string BuildReport(RunResult r, int seeds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 地牢规划器校验报告");
        sb.AppendLine();
        sb.AppendLine("结论: **" + (r.Failures.Count == 0 ? "PASS" : "FAIL") + "**");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| 种子数 | " + Int(seeds) + " |");
        sb.AppendLine("| 通过 DungeonValidation | " + Int(r.SeedsValidated) + " |");
        sb.AppendLine("| 生成抛异常 | " + Int(r.GenerationFailures) + " |");
        sb.AppendLine("| 不同布局数 | " + Int(r.LayoutHashes.Count) + " |");
        sb.AppendLine("| 房间总数 | " + Int(r.TotalRooms) + " |");
        sb.AppendLine("| 链路总数 | " + Int(r.TotalLinks) + " |");
        sb.AppendLine("| 其中秘密链路 | " + Int(r.SecretLinks) + " |");
        sb.AppendLine("| 门总数 | " + Int(r.TotalDoors) + " |");
        sb.AppendLine("| 内容槽位总数 | " + Int(r.TotalSlots) + " |");
        sb.AppendLine("| 槽位越界 | " + Int(r.SlotOutOfBounds) + " |");
        sb.AppendLine("| 槽位重叠 | " + Int(r.SlotOverlaps) + " |");
        sb.AppendLine("| 空特殊房 | " + Int(r.EmptySpecialRooms) + " |");
        sb.AppendLine("| 空隐藏房 | " + Int(r.EmptyHiddenRooms) + " |");
        sb.AppendLine("| 空原型房 | " + Int(r.EmptyPrototypeRooms) + " |");
        sb.AppendLine();

        sb.AppendLine("## 房间类别分布");
        sb.AppendLine();
        sb.AppendLine("| 类别 | 数量 |");
        sb.AppendLine("|---|---|");
        foreach (KeyValuePair<string, int> kv in Sorted(r.CategoryCounts))
        {
            sb.AppendLine("| " + kv.Key + " | " + Int(kv.Value) + " |");
        }
        sb.AppendLine();

        sb.AppendLine("## 内容槽位种类分布");
        sb.AppendLine();
        sb.AppendLine("| 种类 | 数量 |");
        sb.AppendLine("|---|---|");
        foreach (KeyValuePair<string, int> kv in Sorted(r.KindCounts))
        {
            sb.AppendLine("| " + kv.Key + " | " + Int(kv.Value) + " |");
        }
        sb.AppendLine();

        if (r.Failures.Count == 0)
        {
            sb.AppendLine("全部断言通过，无失败明细。");
        }
        else
        {
            sb.AppendLine("## 失败明细（共 " + Int(r.Failures.Count) + " 条，最多显示 " + Int(MaxFailureLines) + " 条）");
            sb.AppendLine();
            int shown = Math.Min(r.Failures.Count, MaxFailureLines);
            for (int i = 0; i < shown; i++)
            {
                sb.AppendLine("- " + r.Failures[i]);
            }
            if (r.Failures.Count > shown)
            {
                sb.AppendLine("- …… 其余 " + Int(r.Failures.Count - shown) + " 条省略");
            }
        }

        return sb.ToString();
    }

    private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> map)
    {
        List<KeyValuePair<string, int>> items = new List<KeyValuePair<string, int>>(map);
        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return items;
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
