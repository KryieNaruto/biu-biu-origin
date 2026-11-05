using System.Collections.Generic;

namespace Game.Core.Content;

/// <summary>
/// 内容仓库的**只读访问门面**：按 id / 哈希取 <see cref="ObjectArchetype"/> 与配套契约。
/// <para>
/// <b>为什么 Core 只定义接口、实现放在数据层</b>：Excel 生成物的加载、校验、热重载都属 <c>Game.Data</c>；
/// <c>Game.Core.Content</c> 必须零反向依赖（只能被上层引用），否则「核心层依赖数据层」会把整张依赖图翻过来。
/// 用接口后，测试、编辑器预览、以及未来的 C++ 侧生成器都能各自提供实现。
/// </para>
/// <para>
/// <b>为什么查法要区分「哈希」与「字符串」</b>：热路径（每发子弹解析子引用）只允许 <see cref="ContentRef.DefId"/> 这种
/// int 查表，绝不能在射击循环里做字符串哈希；字符串查法只服务加载与编辑器。
/// </para>
/// <para>
/// 本接口刻意保持最小：只包含「取内容」的能力，不含注册 / 清空 —— 注册是数据层的写入行为，
/// 运行期系统不该具备改写内容的权限（同规则6「对外只暴露只读接口」的纪律）。
/// </para>
/// </summary>
public interface IContentWorld
{
    /// <summary>已注册的物件原型数。</summary>
    int Count { get; }

    /// <summary>位矩阵列头（tag 名 ↔ tag id）；tag 总数必须与各矩阵的 <see cref="ObjectBitMatrix.TagCount"/> 一致。</summary>
    ContentRules Rules { get; }

    /// <summary>按定义 id 的稳定哈希取物件（热路径入口；<see cref="ContentRef.DefId"/> 直接喂进来）。不存在返回 <c>null</c>。</summary>
    ObjectArchetype? Find(int defId);

    /// <summary>按字符串主键取物件（加载 / 编辑器 / 报错用）。不存在返回 <c>null</c>。</summary>
    ObjectArchetype? ById(string id);

    /// <summary>
    /// 按显示名 key 取物件（表里经常用「显示名」互相引用，便于策划阅读）。
    /// 同名多条时返回第一条；找不到返回 <c>null</c>。
    /// </summary>
    ObjectArchetype? ByName(string displayKey);

    /// <summary>取物件携带的武器契约；该物件没有 <see cref="ObjectCapability.Weapon"/> 位或契约缺失时返回 <c>null</c>。</summary>
    WeaponContract? WeaponOf(in ObjectArchetype archetype);

    /// <summary>取物件携带的投射物契约；缺失返回 <c>null</c>。</summary>
    ProjectileContract? ProjectileOf(in ObjectArchetype archetype);

    /// <summary>
    /// 枚举同一族（<see cref="ObjectArchetype.ClusterSlot"/>）的全部原型 —— 熔炉「可替换簇」的候选集。
    /// 无此族时返回空序列（不返回 <c>null</c>，调用方不必写判空）。
    /// </summary>
    IReadOnlyList<ObjectArchetype> ClusterOf(int clusterSlot);
}

/// <summary>
/// 内容仓库的**只读内存实现**：直接持有一份数组 + 三张字典。
/// <para>
/// <b>为什么这个实现放在 Core 里而不是数据层</b>：它只依赖 <see cref="ObjectArchetype"/> 这些纯数据契约，
/// 0 行引擎代码，也不碰 Excel —— 于是数据层的加载器、测试里的手工内容、以及未来的生成物加载器
/// 都可以直接复用它（避免三处各写一遍「按 hash 建索引」）。真正的加载与校验逻辑仍在 <c>Game.Data</c>。
/// </para>
/// <para>
/// <b>线程</b>：构建完成后只读，可跨线程共享；构建（<see cref="Register"/>）只应在装配期调用。
/// </para>
/// </summary>
public sealed class ReadOnlyContentWorld : IContentWorld
{
    private readonly List<ObjectArchetype> _archetypes = new List<ObjectArchetype>();
    private readonly Dictionary<int, int> _slotByHash = new Dictionary<int, int>();
    private readonly Dictionary<string, int> _slotById = new Dictionary<string, int>(System.StringComparer.Ordinal);
    private readonly Dictionary<string, int> _slotByName = new Dictionary<string, int>(System.StringComparer.Ordinal);
    private readonly Dictionary<int, List<ObjectArchetype>> _clusters = new Dictionary<int, List<ObjectArchetype>>();

    /// <summary>构造一个空仓库（需要先给 tag 列头）。</summary>
    public ReadOnlyContentWorld(ContentRules rules)
    {
        Rules = rules ?? ContentRules.FromTags(System.Array.Empty<string>());
    }

    /// <summary>已注册的物件原型数。</summary>
    public int Count => _archetypes.Count;

    /// <summary>位矩阵列头。</summary>
    public ContentRules Rules { get; }

    /// <summary>注册一个物件原型，返回它的槽位号（同 id / 同哈希重复注册抛异常：数据错误必须当场炸）。</summary>
    public int Register(ObjectArchetype archetype)
    {
        if (archetype is null)
        {
            throw new System.ArgumentNullException(nameof(archetype));
        }
        if (_slotById.TryGetValue(archetype.Id, out int existing))
        {
            throw new System.ArgumentException(
                string.Concat("物件定义 id 重复注册：'", archetype.Id, "'（已存在槽位 ", existing.ToString(), "）。Excel 主键必须唯一。"),
                nameof(archetype));
        }

        int slot = _archetypes.Count;
        _archetypes.Add(archetype);
        _slotById.Add(archetype.Id, slot);
        _slotByHash[archetype.IdHash] = slot;
        if (archetype.DisplayKey.Length > 0 && !_slotByName.ContainsKey(archetype.DisplayKey))
        {
            _slotByName.Add(archetype.DisplayKey, slot);
        }
        if (archetype.ClusterSlot != ObjectArchetype.NoCluster)
        {
            if (!_clusters.TryGetValue(archetype.ClusterSlot, out List<ObjectArchetype>? members))
            {
                members = new List<ObjectArchetype>();
                _clusters.Add(archetype.ClusterSlot, members);
            }
            members.Add(archetype);
        }
        return slot;
    }

    /// <summary>批量注册。</summary>
    public void RegisterAll(IEnumerable<ObjectArchetype> archetypes)
    {
        if (archetypes is null)
        {
            throw new System.ArgumentNullException(nameof(archetypes));
        }
        foreach (ObjectArchetype archetype in archetypes)
        {
            Register(archetype);
        }
    }

    /// <summary>按槽位号取物件（越界返回 <c>null</c>）。</summary>
    public ObjectArchetype? At(int slot) => slot >= 0 && slot < _archetypes.Count ? _archetypes[slot] : null;

    /// <inheritdoc />
    public ObjectArchetype? Find(int defId) => _slotByHash.TryGetValue(defId, out int slot) ? _archetypes[slot] : null;

    /// <inheritdoc />
    public ObjectArchetype? ById(string id) =>
        !string.IsNullOrEmpty(id) && _slotById.TryGetValue(id, out int slot) ? _archetypes[slot] : null;

    /// <inheritdoc />
    public ObjectArchetype? ByName(string displayKey) =>
        !string.IsNullOrEmpty(displayKey) && _slotByName.TryGetValue(displayKey, out int slot) ? _archetypes[slot] : null;

    /// <inheritdoc />
    public WeaponContract? WeaponOf(in ObjectArchetype archetype) =>
        archetype.HasCapability(ObjectCapability.Weapon) ? archetype.WeaponContract : null;

    /// <inheritdoc />
    public ProjectileContract? ProjectileOf(in ObjectArchetype archetype) =>
        archetype.HasCapability(ObjectCapability.Projectile) ? archetype.ProjectileContract : null;

    /// <inheritdoc />
    public IReadOnlyList<ObjectArchetype> ClusterOf(int clusterSlot) =>
        _clusters.TryGetValue(clusterSlot, out List<ObjectArchetype>? members)
            ? (IReadOnlyList<ObjectArchetype>)members
            : System.Array.Empty<ObjectArchetype>();
}
