using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;

namespace Game.Data;

/// <summary>数值包的一层（不可变值）：层号 + 一个 `(StatId, 值)` 的有序表。</summary>
public sealed class StatPackageLayer
{
    private readonly StatId[] _ids;
    private readonly float[] _values;
    private readonly Dictionary<StatId, int> _index;

    /// <summary>该层 id（Excel 主键，便于报错定位）。</summary>
    public string Id { get; }

    /// <summary>该层属于哪一层级。</summary>
    public StatLayer Layer { get; }

    /// <summary>本层条目数。</summary>
    public int Count => _ids.Length;

    /// <summary>构造：items 重复的 StatId 会被判为数据错误（同一个包里同一个数值只能写一次）。</summary>
    public StatPackageLayer(string id, StatLayer layer, IReadOnlyList<KeyValuePair<StatId, float>> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        Id = id ?? string.Empty;
        Layer = layer;
        _ids = new StatId[items.Count];
        _values = new float[items.Count];
        _index = new Dictionary<StatId, int>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            StatId stat = items[i].Key;
            if (stat.IsNone())
            {
                throw new ArgumentException(string.Concat("数值包 '", Id, "' 含 StatId.None：这是占位值，不允许出现在数据里。"), nameof(items));
            }
            if (_index.ContainsKey(stat))
            {
                throw new ArgumentException(string.Concat("数值包 '", Id, "' 中属性 ", stat.ToString(), " 重复声明。"), nameof(items));
            }
            _index.Add(stat, i);
            _ids[i] = stat;
            _values[i] = items[i].Value;
        }
    }

    /// <summary>本层是否声明了该数值。</summary>
    public bool Has(StatId stat) => _index.ContainsKey(stat);

    /// <summary>取本层的值（Delta 或绝对值）；未声明返回 0。</summary>
    public float Get(StatId stat) => _index.TryGetValue(stat, out int i) ? _values[i] : 0f;

    /// <summary>按存储顺序取第 i 条的 StatId（Fold 时遍历用，避免字典命中）。</summary>
    public StatId StatAt(int i) => _ids[i];

    /// <summary>按存储顺序取第 i 条的值。</summary>
    public float ValueAt(int i) => _values[i];

    /// <inheritdoc />
    public override string ToString() => string.Concat("Layer(", Id, ',', Layer.ToString(), ')');
}

/// <summary>
/// 数值包仓库（规则6 的实现）：登记各层包，并按 <see cref="StatPackageIds"/> 把它们**折叠**成最终值。
/// <para>
/// <b>Fold 语义（docs/rules/05 §3.3）</b>：<c>Final[id] = Base[id] + Σ Delta层[id]</c>，
/// 其中 Base 是绝对值、其余层是差量。v1 不做「每属性 if 分支」的自定义 FoldKind ——
/// 需要更复杂公式时走 <c>progression.xlsx</c> 的 Formula 表（见 ARCHITECTURE §4）。
/// </para>
/// <para>
/// <b>为什么 Final 用一条按 StatId 密集下标的 float[]</b>：查询是热路径（残血飘字、每一次伤害结算都要读），
/// <c>float[]</c> + 下标是零分配、缓存友好的；<see cref="Get"/> 越界返回 0 而不是抛。
/// </para>
/// </summary>
public sealed class StatPackageRepository : IStatPackageRepository
{
    private readonly Dictionary<string, StatPackageLayer> _layers = new Dictionary<string, StatPackageLayer>(StringComparer.Ordinal);

    /// <summary>登记层数。</summary>
    public int Count => _layers.Count;

    /// <summary>登记一层包（同 id 重复抛异常：数据错误必须当场炸）。</summary>
    public void Register(StatPackageLayer layer)
    {
        if (layer is null)
        {
            throw new ArgumentNullException(nameof(layer));
        }
        if (_layers.ContainsKey(layer.Id))
        {
            throw new ArgumentException(string.Concat("数值包 id 重复：'", layer.Id, "'。"), nameof(layer));
        }
        _layers.Add(layer.Id, layer);
    }

    /// <summary>批量登记。</summary>
    public void RegisterAll(IEnumerable<StatPackageLayer> layers)
    {
        if (layers is null)
        {
            throw new ArgumentNullException(nameof(layers));
        }
        foreach (StatPackageLayer layer in layers)
        {
            Register(layer);
        }
    }

    /// <summary>取一层包；不存在返回 null。</summary>
    public StatPackageLayer? Find(string id) =>
        !string.IsNullOrEmpty(id) && _layers.TryGetValue(id, out StatPackageLayer? layer) ? layer : null;

    /// <inheritdoc />
    public bool TryGet(string packageId, out IReadOnlyStatView view)
    {
        StatPackageLayer? layer = Find(packageId);
        if (layer is null)
        {
            view = null!;
            return false;
        }
        // 单层视图：Base 层当绝对值，其它层当差量（Final = Delta 本身）。
        view = new PackageView(new[] { layer }, foldDeltas: layer.Layer != StatLayer.Base);
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyStatView Compose(in StatPackageIds ids)
    {
        var layers = new List<StatPackageLayer>(5); // arch-ok: 容量提示对应 StatLayer 五层
        AddIfPresent(ids.Base, layers);
        AddIfPresent(ids.Growth, layers);
        AddIfPresent(ids.Equipment, layers);
        AddIfPresent(ids.Buff, layers);
        AddIfPresent(ids.Temporary, layers);
        return new PackageView(layers, foldDeltas: true);
    }

    private void AddIfPresent(string id, List<StatPackageLayer> layers)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        StatPackageLayer? layer = Find(id);
        if (layer is not null)
        {
            layers.Add(layer);
        }
    }

    /// <summary>
    /// 折叠后的只读视图。构造时一次性算出 Final；之后读取是纯数组下标。
    private sealed class PackageView : IReadOnlyStatView
    {
        /// <summary>StatId 的最大值：Final 数组按它开长（StatId 是 ushort，密集下标）。</summary>
        private static readonly int MaxStat = ComputeMaxStat();

        private readonly float[] _final;
        private readonly bool[] _present;
        private readonly float[] _baseValues;
        private readonly bool[] _hasBase;

        public int Version { get; }

        public PackageView(IReadOnlyList<StatPackageLayer> layers, bool foldDeltas)
        {
            if (layers is null)
            {
                throw new ArgumentNullException(nameof(layers));
            }

            int length = MaxStat + 1;
            _final = new float[length];
            _present = new bool[length];
            _baseValues = new float[length];
            _hasBase = new bool[length];

            // 规则6 Fold：Base 是绝对值，其余层是差量；按 "Base → Growth → Equipment → Buff → Temporary" 顺序折叠。
            for (int l = 0; l < layers.Count; l++)
            {
                StatPackageLayer layer = layers[l];
                bool isBase = layer.Layer == StatLayer.Base;
                for (int i = 0; i < layer.Count; i++)
                {
                    int idx = (int)layer.StatAt(i);
                    if (idx < 0 || idx >= length)
                    {
                        continue;
                    }
                    float value = layer.ValueAt(i);
                    if (isBase)
                    {
                        // 绝对值层：先记原值（GetLayerValue 要能读到它），再"覆盖"进 Final ——
                        // 必须覆盖而不是叠加，否则差量层会反倒把 Base 抬成"Base + Delta"。
                        _baseValues[idx] = value;
                        _final[idx] = value;
                        _hasBase[idx] = true;
                    }
                    else
                    {
                        // 差量层：叠加。单层视图（foldDeltas=false）时 Final 就是这个 Delta 本身。
                        _final[idx] += value;
                    }
                    _present[idx] = true;
                }
            }

            Version = 1;
        }

        public float Get(StatId id)
        {
            int idx = (int)id;
            return idx >= 0 && idx < _final.Length ? _final[idx] : 0f;
        }

        public bool Has(StatId id)
        {
            int idx = (int)id;
            return idx >= 0 && idx < _present.Length && _present[idx];
        }

        public float GetLayerValue(StatLayer layer, StatId id) => layer == StatLayer.Base ? GetBaseRaw(id) : 0f;

        private float GetBaseRaw(StatId id)
        {
            int idx = (int)id;
            return idx >= 0 && idx < _baseValues.Length ? _baseValues[idx] : 0f;
        }

        private static int ComputeMaxStat()
        {
            var all = (StatId[])Enum.GetValues(typeof(StatId));
            int max = 0;
            for (int i = 0; i < all.Length; i++)
            {
                int v = (int)all[i];
                if (v > max)
                {
                    max = v;
                }
            }
            return max;
        }
    }
}
