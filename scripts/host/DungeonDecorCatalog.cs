using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using Game.Core;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// 装饰目录（<c>config/decor_catalog.json</c>）的**可选**加载器。
/// <para>
/// <b>为什么是「可选加载」而不是「代码里写路径清单」</b>（规则4 / 规则7）：
/// 素材 → 装饰的映射是**数据**，由队友 asset-cataloger 产出一份 JSON 作为唯一真源。
/// 宿主只负责「按这份数据摆」，一旦在代码里写死 glb 路径清单，就出现了第二份真源，
/// 改一个装饰要改两处，且素材调整必然导致代码与数据不一致。
/// </para>
/// <para>
/// <b>文件不存在时</b>：整条路径降级为空目录，<see cref="DungeonSceneBuilder"/> 退回用 BoxMesh 标记装饰，
/// 场景照样能跑（冒烟/headless 不依赖素材表）。加载失败（坏 JSON / 语义错误）同样降级，只告警不抛。
/// </para>
/// <para>
/// JSON 形状（见队友产出的 schema）：
/// <code>
/// { "schemaVersion":1,
///   "items":[ { "id":1, "name":"...", "path":"res://素材/RTS建筑/Tree.glb",
///               "layer":"Ground|Wall|Ceiling", "weight":10,
///               "categoryMask":["Combat","Special"], "clearance":0.5,
///               "scaleMin":0.8, "scaleMax":1.2, "sizeMeters":[x,y,z] } ] }
/// </code>
/// </para>
/// </summary>
public sealed class DungeonDecorCatalog
{
    /// <summary>默认目录路径（相对工程根；<c>res://</c> 与工程根同源）。</summary>
    public const string DefaultPath = "res://config/decor_catalog.json";

    /// <summary>本加载器按此版本的字段集读取（版本号不匹配只提示，不拒绝）。</summary>
    public const int SupportedSchemaVersion = 2; // arch-ok: 外部数据契约版本号（与 config/decor_catalog.json 对齐），非玩法数值

    private readonly List<DungeonDecorEntry> _items = new();

    /// <summary>目录项（按 JSON 顺序）。</summary>
    public IReadOnlyList<DungeonDecorEntry> Items => _items;

    /// <summary>
    /// 目录是否「完全用不上」：一条可用条目都没有，调用方应直接退回 BoxMesh 装饰。
    /// <para>注意与「文件读不到」区分：文件读到了但资源全缺失，条目仍在，只是都不可用。</para>
    /// </summary>
    public bool IsEmpty => AvailableCount == 0;

    /// <summary>加载来源路径（诊断用；空目录时为 null）。</summary>
    public string? LoadedFrom { get; private set; }

    /// <summary>读到的 schemaVersion（0 = 文件里没写）。</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>被跳过的条目总数（资源缺失 + 格式错误）。保留给旧调用方。</summary>
    public int SkippedCount => SkippedMissingResourceCount + SkippedMalformedCount;

    /// <summary>首个资源缺失的路径（用于一次性告警，判断素材目录是否整体缺失）。</summary>
    private string _firstMissingPath = string.Empty;

    /// <summary>
    /// 从 <c>res://</c> 路径加载目录。**永不抛异常**：任何问题都降级为空目录 + 一条告警。
    /// </summary>
    /// <param name="path">目录路径（默认 <see cref="DefaultPath"/>）。</param>
    public static DungeonDecorCatalog TryLoad(string path = DefaultPath)
    {
        DungeonDecorCatalog catalog = new();
        try
        {
            if (!FileAccess.FileExists(path))
            {
                // 正常分支：队友素材目录还没产出时，静默降级（不是错误，不打 warning 刷屏）。
                GD.Print("[装饰目录] 未找到 " + path + "，装饰退回 BoxMesh 标记。");
                return catalog;
            }
            string json = FileAccess.GetFileAsString(path);
            catalog.Parse(json, path);
        }
        catch (Exception ex)
        {
            GD.PushWarning("[装饰目录] 解析失败，装饰退回 BoxMesh 标记：" + ex.Message);
        }
        return catalog;
    }

    private void Parse(string json, string path)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            GD.PushWarning("[装饰目录] 缺少 items 数组，装饰退回 BoxMesh 标记：" + path);
            return;
        }

        // 版本只做「提示」不做「拒绝」：本加载器只读 path/layer/weight/categoryMask/clearance/scale*/sizeMeters，
        // 这些字段在各版本间语义稳定；将来若真的改成不兼容形状，再加硬校验。
        int schemaVersion = doc.RootElement.TryGetProperty("schemaVersion", out JsonElement v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int parsed) ? parsed : 0;
        SchemaVersion = schemaVersion;
        if (schemaVersion != SupportedSchemaVersion)
        {
            GD.Print("[装饰目录] schemaVersion=" + schemaVersion + "（本加载器按 v" + SupportedSchemaVersion + " 的字段集读取）");
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            DungeonDecorEntry? entry = ParseEntry(item);
            if (entry is null)
            {
                SkippedMalformedCount++;
                continue;
            }
            // 资源可能整体被搬走（例如 素材/ 目录不在工程里）：这类条目保留在目录里但标记为不可用，
            // 加载期只记一个计数、不刷屏、不抛异常；真正实例化时由摆放器跳过。
            if (!ResourceLoader.Exists(entry.Path))
            {
                SkippedMissingResourceCount++;
                if (_firstMissingPath.Length == 0)
                {
                    _firstMissingPath = entry.Path;
                }
                _items.Add(entry.WithAvailable(false));
                continue;
            }
            _items.Add(entry);
        }
        LoadedFrom = path;
        GdPrintSummary(path);
    }

    /// <summary>可用条目数（资源确实存在、能实例化的）。</summary>
    public int AvailableCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Available)
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>资源缺失而跳过的条数。</summary>
    public int SkippedMissingResourceCount { get; private set; }

    /// <summary>字段格式不合法而跳过的条数。</summary>
    public int SkippedMalformedCount { get; private set; }

    /// <summary>打印一行汇总：数据总量、可用量、两类跳过原因各多少。</summary>
    /// <param name="path">目录文件路径（诊断用）。</param>
    private void GdPrintSummary(string path)
    {
        string summary = "[装饰目录] 共 " + _items.Count + " 条：可用 " + AvailableCount
            + "（跳过 " + (SkippedMissingResourceCount + SkippedMalformedCount) + " 条：资源缺失 "
            + SkippedMissingResourceCount + " / 格式错误 " + SkippedMalformedCount + "）";
        if (SkippedMissingResourceCount > 0)
        {
            // 只提示一次，且带上第一个缺失路径，方便判断是不是整个素材目录都搬走了。
            GD.PushWarning(summary + "；首个缺失路径：" + _firstMissingPath
                + "（素材目录可能未挂载，装饰将退回 BoxMesh 标记）");
        }
        GD.Print(summary + "：" + path);
    }

    private static DungeonDecorEntry? ParseEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? decorPath = ReadString(item, "path");
        if (string.IsNullOrWhiteSpace(decorPath))
        {
            return null;
        }
        if (!Enum.TryParse(ReadString(item, "layer") ?? string.Empty, ignoreCase: true, out DecorLayer layer))
        {
            return null;
        }
        RoomCategoryFlags mask = RoomCategoryFlags.None;
        if (item.TryGetProperty("categoryMask", out JsonElement maskElement) && maskElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in maskElement.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                if (Enum.TryParse(name.GetString(), ignoreCase: true, out RoomCategory category))
                {
                    mask |= ToFlags(category);
                }
            }
        }
        // 空掩码 = 不限制（允许任何房型摆放），比「哪儿都不摆」更符合数据缺省的直觉。
        if (mask == RoomCategoryFlags.None)
        {
            mask = RoomCategoryFlags.All;
        }

        return new DungeonDecorEntry
        {
            Id = ReadInt(item, "id", 0),
            Name = ReadString(item, "name") ?? string.Empty,
            Path = decorPath!,
            Layer = layer,
            Weight = ReadInt(item, "weight", 1),
            CategoryMask = mask,
            Clearance = (float)ReadDouble(item, "clearance", 0d),
            ScaleMin = (float)ReadDouble(item, "scaleMin", 1d),
            ScaleMax = (float)ReadDouble(item, "scaleMax", 1d),
            SizeMeters = ReadVector(item, "sizeMeters"),
        };
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int ReadInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static double ReadDouble(JsonElement obj, string name, double fallback)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : fallback;

    /// <summary>从 JSON 里读 <c>[x,y,z]</c>；缺失或形状不对返回零向量。</summary>
    private static Vector3 ReadVector(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array
            || v.GetArrayLength() < VectorComponents)
        {
            return Vector3.Zero;
        }
        double[] xyz = new double[VectorComponents];
        int i = 0;
        foreach (JsonElement n in v.EnumerateArray())
        {
            if (i >= VectorComponents)
            {
                break;
            }
            xyz[i++] = n.ValueKind == JsonValueKind.Number ? n.GetDouble() : 0d;
        }
        return new Vector3((float)xyz[0], (float)xyz[1], (float)xyz[2]);
    }

    /// <summary>三维向量的分量数（JSON 数组长度）。</summary>
    private const int VectorComponents = 3; // arch-ok: 维度常量（三维向量分量数），非玩法数值

    /// <summary>房型 → 位标志（与 <see cref="DungeonRoomSpecResource"/> 的掩码保持同一套映射）。</summary>
    /// <param name="category">房型。</param>
    public static RoomCategoryFlags ToFlags(RoomCategory category) => category switch
    {
        RoomCategory.Start => RoomCategoryFlags.Start,
        RoomCategory.Combat => RoomCategoryFlags.Combat,
        RoomCategory.Special => RoomCategoryFlags.Special,
        RoomCategory.Hidden => RoomCategoryFlags.Hidden,
        RoomCategory.Prototype => RoomCategoryFlags.Prototype,
        RoomCategory.Boss => RoomCategoryFlags.Boss,
        RoomCategory.Safe => RoomCategoryFlags.Safe,
        // Undecided 是「还没定类别」的占位房型，不参与装饰掩码（枚举里没有对应位）。
        _ => RoomCategoryFlags.None,
    };

    /// <summary>某房型可用的装饰条目（位掩码匹配）。</summary>
    /// <param name="category">房型。</param>
    public bool CanDecorate(RoomCategory category, DungeonDecorEntry entry)
        => (entry.CategoryMask & ToFlags(category)) != RoomCategoryFlags.None;

    /// <summary>
    /// 按权重挑一条装饰（确定性：由调用方传入已播种的随机源，保证同种子同布局）。
    /// </summary>
    /// <param name="category">房型（用于过滤可用的条目）。</param>
    /// <param name="random">随机源。</param>
    /// <param name="layer">限定层（null = 任意层）。</param>
    public DungeonDecorEntry? Pick(RoomCategory category, IRandomProvider random, DecorLayer? layer = null)
    {
        if (_items.Count == 0)
        {
            return null;
        }
        int total = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            DungeonDecorEntry e = _items[i];
            // 资源缺失的条目参与不了摆放：不占权重，避免"挑中又放不下"。
            if (!e.Available || (layer.HasValue && e.Layer != layer.Value))
            {
                continue;
            }
            if (!CanDecorate(category, e))
            {
                continue;
            }
            total += Math.Max(e.Weight, 1);
        }
        if (total <= 0)
        {
            return null;
        }
        int roll = random.NextInt(0, total);
        for (int i = 0; i < _items.Count; i++)
        {
            DungeonDecorEntry e = _items[i];
            if (!e.Available || (layer.HasValue && e.Layer != layer.Value))
            {
                continue;
            }
            if (!CanDecorate(category, e))
            {
                continue;
            }
            roll -= Math.Max(e.Weight, 1);
            if (roll < 0)
            {
                return e;
            }
        }
        return null;
    }
}

/// <summary>装饰层：地面 / 贴墙 / 顶部悬挂。</summary>
public enum DecorLayer
{
    /// <summary>地面摆放（家具、箱子、树）。</summary>
    Ground = 0,

    /// <summary>贴墙（火把、旗帜），摆放时会往墙内推 <see cref="DungeonDecorEntry.Clearance"/>。</summary>
    Wall = 1,

    /// <summary>顶部悬挂（吊灯），抬高到墙高之上。</summary>
    Ceiling = 2, // arch-ok: 枚举序号（层标识），由 JSON 的 layer 字段字符串解析而来，非玩法数值
}

/// <summary>装饰目录的一条数据行。</summary>
public sealed class DungeonDecorEntry
{
    /// <summary>数据行 id（诊断用）。</summary>
    public int Id { get; init; } = 0;

    /// <summary>人读名字。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary><c>res://</c> 素材路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// 资源是否真的能用（<c>ResourceLoader.Exists</c>）。
    /// <para>
    /// false 的条目仍保留在目录里（保留权重分布与数据可追溯），但<b>不会被挑中</b>，
    /// 摆放时退回 BoxMesh 标记。素材目录被搬走时全部条目都会变成 false。
    /// </para>
    /// </summary>
    public bool Available { get; init; } = true;

    /// <summary>返回一个只改 <see cref="Available"/> 的副本（entries 是 init-only，不能就地改）。</summary>
    /// <param name="available">新的可用性。</param>
    public DungeonDecorEntry WithAvailable(bool available) => new()
    {
        Id = Id,
        Name = Name,
        Path = Path,
        Available = available,
        Layer = Layer,
        Weight = Weight,
        CategoryMask = CategoryMask,
        Clearance = Clearance,
        ScaleMin = ScaleMin,
        ScaleMax = ScaleMax,
        SizeMeters = SizeMeters,
    };

    /// <summary>摆放层。</summary>
    public DecorLayer Layer { get; init; } = DecorLayer.Ground;

    /// <summary>拾取权重（&lt;= 0 视为 1）。</summary>
    public int Weight { get; init; } = 1;

    /// <summary>适用房型掩码。</summary>
    public RoomCategoryFlags CategoryMask { get; init; } = RoomCategoryFlags.All;

    /// <summary>贴墙间隙（米）：Wall 层往墙内退让。</summary>
    public float Clearance { get; init; }

    /// <summary>随机缩放下限（相对目标尺寸）。</summary>
    public float ScaleMin { get; init; } = 1f;

    /// <summary>随机缩放上限（相对目标尺寸）。</summary>
    public float ScaleMax { get; init; } = 1f;

    /// <summary>素材原始尺寸（米）；全零表示数据未给，按量测值归一化。</summary>
    public Vector3 SizeMeters { get; init; }
}
