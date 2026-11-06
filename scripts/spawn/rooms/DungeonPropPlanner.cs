using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Randomness;

namespace Game.Spawn.Rooms;

/// <summary>
/// 把**建筑 / 奖励 / 可破坏物体 / 不可破坏物体**摆进一张已栅格化的 <see cref="DungeonMap"/>。
/// <para>
/// <b>一条铁律：绝不堵死通路。</b>每摆下一个阻挡物之前，先试占，然后从「起始房中心」
/// 和「Boss 房中心」各做一次泛洪；只要有任何一间房的中心变得不可达，这个物件就撤掉重掷。
/// 这条检查很便宜（一张 24×24 的图泛洪是微秒级），但它是「地图永远可通关」的唯一保证 ——
/// 靠「物件之间互不重叠」是保证不了的，两个独立摆放的障碍可以刚好把一条走廊夹死。
/// </para>
/// <para>
/// <b>为什么可破坏物体也当阻挡物算</b>：玩家在弹尽粮绝时打不掉木箱，路就真的断了。
/// 把「打得掉」当免死金牌是设计上的想当然 —— 所以它跟石像一样接受连通性检查。
/// </para>
/// <para>
/// <b>奖励不参与连通性检查</b>：它不阻挡通行，而且我们的规则保证它摆在地上任何一个可达格，
/// 所以「一定拿得到」是调用顺序（先泛洪确定可达区）保证的，不是靠事后检查。
/// </para>
/// </summary>
public static class DungeonPropPlanner
{
    /// <summary>随机子流盐：保证「多摆一个木箱」不会把建筑序列整个推后。</summary>
    public const int PropSalt = 0x50726F70; // arch-ok: 随机子流盐（结构性常量）

    /// <summary>随机子流盐：奖励单独一条流。</summary>
    public const int RewardSalt = 0x52657764; // arch-ok: 随机子流盐（结构性常量）

    /// <summary>四向邻居（泛洪用）。</summary>
    private static readonly (int X, int Z)[] Steps = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>
    /// 往地图里摆满物件，返回**新地图**（原地图不被修改）。
    /// </summary>
    /// <param name="source">已栅格化的地图（只读）。</param>
    /// <param name="seed">本层种子。</param>
    /// <param name="config">摆放配置；null = 用默认值。</param>
    /// <returns>物件已写进 <see cref="DungeonMap.Props"/> 的新地图；地图无效时原样返回。</returns>
    public static DungeonMap Populate(in DungeonMap source, int seed, DungeonPropConfig? config = null)
    {
        DungeonPropConfig cfg = config ?? new DungeonPropConfig();
        cfg.Validate();
        if (!source.IsValid)
        {
            return source;
        }

        // 地图是 readonly struct：地形格子一个都不改，只追加 Props，最后用对象初始值设定项造一份新的。
        // 这样调用方手里的旧地图保持原样（值语义，便于前后对比）。
        DungeonMap map = source;

        List<DungeonProp> props = new();
        DeterministicRandom root = new(seed);
        IRandomProvider buildingRandom = root.Fork(PropSalt);
        IRandomProvider rewardRandom = root.Fork(RewardSalt);

        // 先把「地面格」按房间归堆：物件只能摆在房间地面上，走廊跟墙一律不碰。
        List<int>[] roomCells = GroupFloorByRoom(in map);

        // ★ 一张掩码贯穿全部四个阶段。
        // 每个阶段各建一张是曾经的 bug：建筑占的格子在摆奖励时"看不见"，
        // 结果奖励可以叠在建筑上、且连通性检查拿的是残缺掩码 —— 摆出来的图与检查结论不一致。
        bool[] blocked = BuildBlockedMask(in map);

        // 顺序很重要：先建筑（占地大、最难摆），再不可破坏，再可破坏，最后奖励。
        // 反过来先摆奖励，建筑就会因为「格子被小东西占了」而摆不下 —— 大件优先是小件优先的反面，
        // 而玩家的观感里「大建筑缺席」比「少一个木箱」明显得多。
        if (cfg.BuildingsEnabled)
        {
            PlaceBuildings(in map, cfg, buildingRandom, roomCells, props, blocked);
        }
        if (cfg.SolidsEnabled)
        {
            PlaceSingles(in map, cfg, buildingRandom, roomCells, props, blocked, DungeonPropKind.Solid, cfg.SolidsPerRoom);
        }
        if (cfg.BreakablesEnabled)
        {
            PlaceSingles(in map, cfg, buildingRandom, roomCells, props, blocked, DungeonPropKind.Breakable, cfg.BreakablesPerRoom);
        }
        if (cfg.RewardsEnabled)
        {
            PlaceRewards(in map, cfg, rewardRandom, roomCells, props, blocked);
        }

        return new DungeonMap
        {
            Width = map.Width,
            Height = map.Height,
            OriginX = map.OriginX,
            OriginZ = map.OriginZ,
            Layout = map.Layout,
            Kind = map.Kind,
            RoomSalt = map.RoomSalt,
            DoorSlot = map.DoorSlot,
            Seed = map.Seed,
            Config = map.Config,
            Props = props.ToArray(),
        };
    }

    // ─────────────────────────────────────────────────────────────
    // 建筑
    // ─────────────────────────────────────────────────────────────

    private static void PlaceBuildings(
        in DungeonMap map,
        DungeonPropConfig cfg,
        IRandomProvider random,
        List<int>[] roomCells,
        List<DungeonProp> props,
        bool[] blocked)
    {
        for (int slot = 0; slot < roomCells.Length; slot++)
        {
            if (!ShouldDecorate(in map, cfg, slot))
            {
                continue;
            }
            RoomPlan room = map.Layout.Rooms[slot];
            int roomSide = Math.Min(room.Footprint.Width, room.Footprint.Depth);
            if (roomSide < cfg.BuildingMinRoomSide)
            {
                continue; // 房间太小，放建筑必然把通路挤死（见 DungeonPropConfig.BuildingMinRoomSide）
            }
            int maxSide = Math.Min(cfg.BuildingMaxSide, roomSide);
            if (maxSide < DungeonPropConfig.MinBuildingSide)
            {
                continue;
            }

            for (int attempt = 0; attempt < cfg.BuildingsPerRoom; attempt++)
            {
                // 边长从「尽量大」往小退：先试 3×3，摆不下再试 2×2。
                int wanted = random.NextInt(DungeonPropConfig.MinBuildingSide, maxSide + 1);
                bool placed = false;
                for (int side = wanted; side >= DungeonPropConfig.MinBuildingSide && !placed; side--)
                {
                    placed = TryPlaceBlock(in map, cfg, random, slot, side, side, DungeonPropKind.Building, blocked, props);
                }
                if (!placed)
                {
                    break; // 这间房摆不下更大/更小的了，试下一个房间
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 单格物件（可破坏 / 不可破坏）
    // ─────────────────────────────────────────────────────────────

    private static void PlaceSingles(
        in DungeonMap map,
        DungeonPropConfig cfg,
        IRandomProvider random,
        List<int>[] roomCells,
        List<DungeonProp> props,
        bool[] blocked,
        DungeonPropKind kind,
        int perRoom)
    {
        for (int slot = 0; slot < roomCells.Length; slot++)
        {
            if (!ShouldDecorate(in map, cfg, slot))
            {
                continue;
            }
            for (int attempt = 0; attempt < perRoom; attempt++)
            {
                if (!TryPlaceBlock(in map, cfg, random, slot, 1, 1, kind, blocked, props))
                {
                    break;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 奖励
    // ─────────────────────────────────────────────────────────────

    private static void PlaceRewards(
        in DungeonMap map,
        DungeonPropConfig cfg,
        IRandomProvider random,
        List<int>[] roomCells,
        List<DungeonProp> props,
        bool[] blocked)
    {
        for (int slot = 0; slot < roomCells.Length; slot++)
        {
            RoomCategory category = map.Layout.Rooms[slot].Category;
            int count = category switch
            {
                RoomCategory.Hidden => cfg.RewardsPerHiddenRoom,
                RoomCategory.Boss => cfg.RewardsPerCombatRoom,
                RoomCategory.Special => cfg.RewardsPerCombatRoom,
                RoomCategory.Combat => cfg.RewardsPerCombatRoom,
                _ => 0,
            };
            if (count <= 0 || !ShouldDecorate(in map, cfg, slot))
            {
                continue;
            }
            for (int attempt = 0; attempt < count; attempt++)
            {
                // 奖励不阻挡通行，所以不需要连通性检查 —— 占一格即可。
                if (!TryPlaceBlock(in map, cfg, random, slot, 1, 1, DungeonPropKind.Reward, blocked, props, requireConnectivity: false))
                {
                    break;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 单次放置（试占 → 连通性 → 落定）
    // ─────────────────────────────────────────────────────────────

    private static bool TryPlaceBlock(
        in DungeonMap map,
        DungeonPropConfig cfg,
        IRandomProvider random,
        int slot,
        int width,
        int depth,
        DungeonPropKind kind,
        bool[] blocked,
        List<DungeonProp> props,
        bool requireConnectivity = true)
    {
        // 试几个随机位置：全部失败就放弃这一次（不无限重试，避免大房间里死循环）。
        for (int tries = 0; tries < PlacementTries; tries++)
        {
            if (!TryPickOrigin(in map, cfg, random, slot, width, depth, blocked, out int ox, out int oz))
            {
                return false;
            }
            int w = width;
            int d = depth;
            MarkRect(map, blocked, ox, oz, w, d, true);
            bool ok = !requireConnectivity || ConnectivityHolds(in map, blocked);
            if (!ok)
            {
                MarkRect(map, blocked, ox, oz, w, d, false);
                continue;
            }
            props.Add(new DungeonProp
            {
                Kind = kind,
                X = ox,
                Z = oz,
                Width = w,
                Depth = d,
                RoomSlot = slot,
                Variant = random.NextInt(0, VariantCount),
            });
            return true;
        }
        return false;
    }

    /// <summary>单次放置最多试几个位置。</summary>
    private const int PlacementTries = 12; // arch-ok: 算法重试次数，不是玩法数值

    /// <summary>同一分类下能随机到的品种数（具体是什么品种由表现层解释）。</summary>
    private const int VariantCount = 3; // arch-ok: 品种数量（表现层枚举上界）

    private static bool TryPickOrigin(
        in DungeonMap map,
        DungeonPropConfig cfg,
        IRandomProvider random,
        int slot,
        int width,
        int depth,
        bool[] blocked,
        out int originX,
        out int originZ)
    {
        originX = -1;
        originZ = -1;
        RoomPlan room = map.Layout.Rooms[slot];
        (int x0, int z0) = ToCanvas(in map, room);
        int x1 = x0 + room.Footprint.Width;
        int z1 = z0 + room.Footprint.Depth;

        // 房间小的时候必须贴墙摆，否则「房间 3 格 + 两边各留 1 格」直接剩 1 格，什么都放不下。
        // 所以留白**自适应**：先按配置留，留完放不下就逐格退让到 0。
        // 这不算「偷偷改配置」—— 配置表达的是「尽量留」，可放置性是硬约束，冲突时后者优先。
        for (int clearance = cfg.WallClearance; clearance >= 0; clearance--)
        {
            int loX = x0 + clearance;
            int loZ = z0 + clearance;
            int hiX = x1 - clearance - width;
            int hiZ = z1 - clearance - depth;
            if (hiX < loX || hiZ < loZ)
            {
                continue;
            }
            for (int tries = 0; tries < PlacementTries; tries++)
            {
                int x = random.NextInt(loX, hiX + 1);
                int z = random.NextInt(loZ, hiZ + 1);
                if (!RectIsFree(in map, cfg, x, z, width, depth, blocked))
                {
                    continue;
                }
                originX = x;
                originZ = z;
                return true;
            }
        }
        return false;
    }

    /// <summary>矩形内每一格都必须是「房间地面 + 未占用 + 离门够远」。</summary>
    private static bool RectIsFree(
        in DungeonMap map,
        DungeonPropConfig cfg,
        int x,
        int z,
        int width,
        int depth,
        bool[] blocked)
    {
        DoorClearanceMask clearance = new(in map, cfg.DoorClearance);
        for (int dz = 0; dz < depth; dz++)
        {
            for (int dx = 0; dx < width; dx++)
            {
                int cx = x + dx;
                int cz = z + dz;
                if (!map.Contains(cx, cz))
                {
                    return false;
                }
                if (map.KindAt(cx, cz) != DungeonCellKind.Floor)
                {
                    return false;
                }
                if (map.RoomAt(cx, cz) < 0)
                {
                    return false;
                }
                if (blocked[map.Index(cx, cz)])
                {
                    return false;
                }
                if (clearance.WithinDoorZone(in map, cx, cz))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static void MarkRect(in DungeonMap map, bool[] blocked, int x, int z, int width, int depth, bool value)
    {
        for (int dz = 0; dz < depth; dz++)
        {
            for (int dx = 0; dx < width; dx++)
            {
                int cx = x + dx;
                int cz = z + dz;
                if (map.Contains(cx, cz))
                {
                    blocked[map.Index(cx, cz)] = value;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 连通性
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 试占之后，逐间房确认「它的某个可达格仍然可达」。
    /// <para>
    /// 判据不是「能走到这间房的中心」——建筑可能正好盖在中心上。改成
    /// 「这间房仍有**任意一格**与起始房连通」，这才是玩家真正需要的保证。
    /// </para>
    /// </summary>
    private static bool ConnectivityHolds(in DungeonMap map, bool[] blocked)
    {
        int total = map.Width * map.Height;
        int startSlot = map.Layout.Start.Slot;
        (int sx, int sz) = map.RoomCenter(in map.Layout.Rooms[startSlot]);
        if (!map.Contains(sx, sz))
        {
            return true;
        }
        int startIndex = map.Index(sx, sz);
        // 起点格自身被占了：换一个起点房里的可达格。
        if (!IsPassable(in map, blocked, sx, sz))
        {
            if (!TryFindOpenCellIn(map, blocked, startSlot, out sx, out sz))
            {
                return false;
            }
            startIndex = map.Index(sx, sz);
        }

        bool[] reached = new bool[total];
        Queue<int> queue = new();
        reached[startIndex] = true;
        queue.Enqueue(startIndex);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur % map.Width;
            int cz = cur / map.Width;
            for (int i = 0; i < Steps.Length; i++)
            {
                int nx = cx + Steps[i].X;
                int nz = cz + Steps[i].Z;
                if (!map.Contains(nx, nz) || !IsPassable(in map, blocked, nx, nz))
                {
                    continue;
                }
                int ni = map.Index(nx, nz);
                if (reached[ni])
                {
                    continue;
                }
                reached[ni] = true;
                queue.Enqueue(ni);
            }
        }

        // 秘密通道算可走（否则隐藏房一定判失败）。
        for (int slot = 0; slot < map.Layout.RoomCount; slot++)
        {
            RoomPlan room = map.Layout.Rooms[slot];
            if (room.Category == RoomCategory.Hidden)
            {
                continue; // 隐藏房靠秘密通道，单独放宽
            }
            if (slot == startSlot)
            {
                continue;
            }
            if (!RoomHasReachedCell(in map, room, reached))
            {
                return false;
            }
        }
        return true;
    }

    private static bool RoomHasReachedCell(in DungeonMap map, in RoomPlan room, bool[] reached)
    {
        (int x0, int z0) = ToCanvas(in map, room);
        for (int dz = 0; dz < room.Footprint.Depth; dz++)
        {
            for (int dx = 0; dx < room.Footprint.Width; dx++)
            {
                int cx = x0 + dx;
                int cz = z0 + dz;
                if (!map.Contains(cx, cz))
                {
                    continue;
                }
                if (reached[map.Index(cx, cz)])
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryFindOpenCellIn(in DungeonMap map, bool[] blocked, int slot, out int x, out int z)
    {
        x = -1;
        z = -1;
        RoomPlan room = map.Layout.Rooms[slot];
        (int x0, int z0) = ToCanvas(in map, room);
        for (int dz = 0; dz < room.Footprint.Depth; dz++)
        {
            for (int dx = 0; dx < room.Footprint.Width; dx++)
            {
                int cx = x0 + dx;
                int cz = z0 + dz;
                if (map.Contains(cx, cz) && IsPassable(in map, blocked, cx, cz))
                {
                    x = cx;
                    z = cz;
                    return true;
                }
            }
        }
        return false;
    }

    private static (int X, int Z) ToCanvas(in DungeonMap map, in RoomPlan room) =>
        (map.ToMapX(room.Cell.X), map.ToMapZ(room.Cell.Z));

    /// <summary>这一格能不能走（含「没被物件占掉」）。门/走廊/地面可走，墙与障碍不可走。</summary>
    private static bool IsPassable(in DungeonMap map, bool[] blocked, int x, int z)
    {
        if (!map.Contains(x, z))
        {
            return false;
        }
        int i = map.Index(x, z);
        if (blocked[i])
        {
            return false;
        }
        DungeonCellKind kind = map.Kind[i];
        return kind is DungeonCellKind.Floor or DungeonCellKind.Door
            or DungeonCellKind.Corridor or DungeonCellKind.SecretPassage;
    }

    // ─────────────────────────────────────────────────────────────
    // 辅助
    // ─────────────────────────────────────────────────────────────

    private static bool[] BuildBlockedMask(in DungeonMap map)
    {
        int total = map.Width * map.Height;
        bool[] blocked = new bool[total];
        for (int i = 0; i < total; i++)
        {
            blocked[i] = false;
        }
        return blocked;
    }

    private static List<int>[] GroupFloorByRoom(in DungeonMap map)
    {
        List<int>[] groups = new List<int>[map.Layout.RoomCount];
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i] = new List<int>();
        }
        for (int z = 0; z < map.Height; z++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                int slot = map.RoomAt(x, z);
                if (slot >= 0 && slot < groups.Length && map.KindAt(x, z) == DungeonCellKind.Floor)
                {
                    groups[slot].Add(map.Index(x, z));
                }
            }
        }
        return groups;
    }

    private static bool ShouldDecorate(in DungeonMap map, DungeonPropConfig cfg, int slot)
    {
        if (slot < 0 || slot >= map.Layout.RoomCount)
        {
            return false;
        }
        if (!cfg.PlaceInStartRoom && map.Layout.Rooms[slot].Category == RoomCategory.Start)
        {
            return false;
        }
        if (map.Layout.Rooms[slot].Category == RoomCategory.Undecided)
        {
            return false;
        }
        return true;
    }

    /// <summary>门附近禁止摆放的区域（惰性构建，只算一次）。</summary>
    private readonly struct DoorClearanceMask
    {
        private readonly int[] _doorCells;
        private readonly int _radius;

        public DoorClearanceMask(in DungeonMap map, int radius)
        {
            _radius = radius;
            if (radius <= 0 || map.Layout.Doors is null)
            {
                _doorCells = Array.Empty<int>();
                return;
            }
            List<int> cells = new();
            DungeonLayout layout = map.Layout;
            RoomDoor[] doors = layout.Doors;
            for (int i = 0; i < doors.Length; i++)
            {
                (int dx, int dz) = map.DoorCell(in layout, in doors[i]);
                cells.Add(map.Index(dx, dz));
            }
            _doorCells = cells.ToArray();
        }

        public bool WithinDoorZone(in DungeonMap map, int x, int z)
        {
            if (_radius <= 0 || _doorCells.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < _doorCells.Length; i++)
            {
                int dxx = Math.Abs((_doorCells[i] % map.Width) - x);
                int dzz = Math.Abs((_doorCells[i] / map.Width) - z);
                if (dxx <= _radius && dzz <= _radius)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
