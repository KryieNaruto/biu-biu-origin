using System;
using System.Collections.Generic;
using Game.Core.Actors;

namespace Game.Core.Content;

// 本文件 = 「内容编址」的全部实现（基础物件抽象 B 段）。
//
// 契约形状由 contracts-dev 定稿（ObjectArchetype.cs / ObjectBitMatrix.cs / ValueContext.cs），
// 本文件只做一件事：把三张内容表（物件 / 武器 / 子弹）编址成 int，并把 Def 之间的 ContentRef 解析成槽位号。
//
// 为什么必须是「int 编址 + 数组下标」而不是对象引用：
//   J2 一票否决「组件持有托管引用」—— 组件里只能放 int。于是 Def 必须先被编址成 int，
//   之后热路径（每发子弹、每帧推进）只做数组下标，不碰字典、不做字符串比较。
//
// 为什么不直接复用 ContentRef.DefId（FNV-1a 哈希）当槽位号：
//   ContentRef 带 string 字段 Id，是【托管结构】，既不能进 ECS 组件，也不能用于零分配热路径；
//   而且哈希当数组下标需要一张 2^32 规模的稀疏表。所以：ContentRef 是【表格式】，
//   槽位号是【运行时格式】，两者之间的翻译只在装配期做一次，运行时只剩 int。
//
// 版本记号：2026-11-05 / runtime-dev

/// <summary>
/// 内容编址表：构造期一次性建 <c>string → int</c> 字典 + <c>int → 对象</c> 数组，
/// <b>热路径只剩数组下标</b>（ECS.md §5：组件里只存 int）。
/// <para>
/// <b>为什么重复 id 必须抛中文异常</b>：这是 Excel 主键重复，属于数据错误。
/// 放进游戏会变成「两发子弹打着打着变成同一份」「熔炉抽到的和显示的不是一件」这种极难查的 bug，
/// 而构建期炸掉只需要策划改一格。
/// </para>
/// <para>
/// <b>为什么三种槽位号各自独立编址</b>：物件、武器、子弹是三张表，各自的 0 号互不相干。
/// 组件里的 int 之所以不会串表，是因为它只在「武器槽位」「子弹槽位」这类明确语义下使用，
/// 跨表引用（<see cref="ContentRef"/>）在装配期就被翻译成对应表的槽位号。
/// </para>
/// </summary>
public sealed class ContentIndex
{
    /// <summary>「未注册」哨兵。用 -1 而不是 0，因为 0 是合法的第一个槽位。</summary>
    public const int NoneSlot = -1;

    private readonly List<ObjectArchetype> _objects = new List<ObjectArchetype>();
    private readonly List<WeaponContract> _weapons = new List<WeaponContract>();
    private readonly List<ProjectileContract> _projectiles = new List<ProjectileContract>();

    // string id → 槽位号（装配期用一次，运行期不用）
    private readonly Dictionary<string, int> _objectBySlot = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _weaponBySlot = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _projectileBySlot = new Dictionary<string, int>(StringComparer.Ordinal);

    // ContentRef.DefId（FNV 哈希）→ 槽位号：表里的交叉引用在装配期就被翻译成下标。
    private readonly Dictionary<int, int> _objectByDefId = new Dictionary<int, int>();
    private readonly Dictionary<int, int> _weaponByDefId = new Dictionary<int, int>();
    private readonly Dictionary<int, int> _projectileByDefId = new Dictionary<int, int>();

    /// <summary>已注册物件数。</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>已注册武器数。</summary>
    public int WeaponCount => _weapons.Count;

    /// <summary>已注册子弹数。</summary>
    public int ProjectileCount => _projectiles.Count;

    /// <summary>本索引的名字（诊断用）。</summary>
    public string Name { get; set; } = "content";

    // ─────────────────────────────────────────────────────────────
    // 注册（装配期）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 注册一个物件原型，返回槽位号。同 id 重复抛中文异常。
    /// <para>物件自带的武器 / 子弹契约会随物件一起登记（幂等）：策划只需维护物件表 + 两条子表，
    /// 不需要再把契约手工注册一遍。契约 id 若已存在则复用原槽位，不报重复——
    /// 因为「两个物件引用同一份契约」是合法且必要的（同一颗手枪子弹被两把枪共用）。</para>
    /// </summary>
    public int RegisterObject(ObjectArchetype archetype)
    {
        if (archetype is null)
        {
            throw new ArgumentNullException(nameof(archetype));
        }
        if (_objectBySlot.TryGetValue(archetype.Id, out int existing))
        {
            throw new ContentException(string.Concat(
                "物件原型 id 重复注册：'", archetype.Id, "'（已存在于槽位 ", existing.ToString(),
                "）。Excel 表的 id 列必须唯一。"));
        }
        int slot = _objects.Count;
        _objects.Add(archetype);
        _objectBySlot.Add(archetype.Id, slot);
        _objectByDefId[archetype.IdHash] = slot;

        if (archetype.WeaponContract is not null)
        {
            RegisterWeaponCore(archetype.WeaponContract);
        }
        if (archetype.ProjectileContract is not null)
        {
            RegisterProjectileCore(archetype.ProjectileContract);
        }
        return slot;
    }

    /// <summary>注册一把武器，返回槽位号。同 id 重复抛中文异常。</summary>
    public int RegisterWeapon(WeaponContract weapon)
    {
        if (weapon is null)
        {
            throw new ArgumentNullException(nameof(weapon));
        }
        if (_weaponBySlot.TryGetValue(weapon.DefId, out int existing))
        {
            throw new ContentException(string.Concat(
                "武器契约 id 重复注册：'", weapon.DefId, "'（已存在于槽位 ", existing.ToString(), "）。Excel 表的 id 列必须唯一。"));
        }
        return RegisterWeaponCore(weapon);
    }

    /// <summary>注册一颗子弹，返回槽位号。同 id 重复抛中文异常。</summary>
    public int RegisterProjectile(ProjectileContract projectile)
    {
        if (projectile is null)
        {
            throw new ArgumentNullException(nameof(projectile));
        }
        if (_projectileBySlot.TryGetValue(projectile.DefId, out int existing))
        {
            throw new ContentException(string.Concat(
                "子弹契约 id 重复注册：'", projectile.DefId, "'（已存在于槽位 ", existing.ToString(), "）。Excel 表的 id 列必须唯一。"));
        }
        return RegisterProjectileCore(projectile);
    }

    private int RegisterWeaponCore(WeaponContract weapon)
    {
        if (_weaponBySlot.TryGetValue(weapon.DefId, out int existing))
        {
            return existing;
        }
        int slot = _weapons.Count;
        _weapons.Add(weapon);
        _weaponBySlot.Add(weapon.DefId, slot);
        _weaponByDefId[ContentHash.Fnv1a32(weapon.DefId)] = slot;
        return slot;
    }

    private int RegisterProjectileCore(ProjectileContract projectile)
    {
        if (_projectileBySlot.TryGetValue(projectile.DefId, out int existing))
        {
            return existing;
        }
        int slot = _projectiles.Count;
        _projectiles.Add(projectile);
        _projectileBySlot.Add(projectile.DefId, slot);
        _projectileByDefId[ContentHash.Fnv1a32(projectile.DefId)] = slot;
        return slot;
    }

    // ─────────────────────────────────────────────────────────────
    // 查表（越界返回 null，不抛）
    // ─────────────────────────────────────────────────────────────

    /// <summary>按槽位号取物件原型；越界返回 <c>null</c>（内容缺失由生成方计成「拒绝」，不该炸掉一帧）。</summary>
    public ObjectArchetype? GetObject(int defId) => defId >= 0 && defId < _objects.Count ? _objects[defId] : null;

    /// <summary>按槽位号取武器；越界返回 <c>null</c>。</summary>
    public WeaponContract? GetWeapon(int contractSlot) =>
        contractSlot >= 0 && contractSlot < _weapons.Count ? _weapons[contractSlot] : null;

    /// <summary>按槽位号取子弹；越界返回 <c>null</c>。</summary>
    public ProjectileContract? GetProjectile(int contractSlot) =>
        contractSlot >= 0 && contractSlot < _projectiles.Count ? _projectiles[contractSlot] : null;

    // ─────────────────────────────────────────────────────────────
    // 地址换算（装配期 / 测试用；热路径不调字典）
    // ─────────────────────────────────────────────────────────────

    /// <summary>物件槽位号；不存在返回 <see cref="NoneSlot"/>。</summary>
    public int ObjectSlotOf(string id) => Lookup(_objectBySlot, id);

    /// <summary>武器槽位号；不存在返回 <see cref="NoneSlot"/>。</summary>
    public int WeaponSlotOf(string id) => Lookup(_weaponBySlot, id);

    /// <summary>子弹槽位号；不存在返回 <see cref="NoneSlot"/>。</summary>
    public int ProjectileSlotOf(string id) => Lookup(_projectileBySlot, id);

    /// <summary>取物件槽位（引用已注册的话按 id 回查）。</summary>
    public int ObjectSlot(ObjectArchetype archetype) => archetype is null ? NoneSlot : ObjectSlotOf(archetype.Id);

    /// <summary>取武器槽位。</summary>
    public int WeaponSlot(WeaponContract weapon) => weapon is null ? NoneSlot : WeaponSlotOf(weapon.DefId);

    /// <summary>取子弹槽位。</summary>
    public int ProjectileSlot(ProjectileContract projectile) =>
        projectile is null ? NoneSlot : ProjectileSlotOf(projectile.DefId);

    private static int Lookup(Dictionary<string, int> map, string id) =>
        !string.IsNullOrEmpty(id) && map.TryGetValue(id, out int slot) ? slot : NoneSlot;

    /// <summary>
    /// 把表里的 <see cref="ContentRef"/> 解析成<b>物件槽位号</b>：先按字符串主键，再按 FNV 哈希，都查不到返回 <see cref="NoneSlot"/>。
    /// <para><b>为什么两种都试</b>：生成物里的引用通常带字符串（便于表校验），手写内容 / 运行时构造的引用只有哈希。
    /// 两者指向同一个物件，查法不同而已。</para>
    /// </summary>
    public int ObjectSlotOf(in ContentRef reference) => ResolveRef(reference, _objectBySlot, _objectByDefId);

    /// <summary>把引用解析成武器槽位号。</summary>
    public int WeaponSlotOf(in ContentRef reference) => ResolveRef(reference, _weaponBySlot, _weaponByDefId);

    /// <summary>把引用解析成子弹槽位号。</summary>
    public int ProjectileSlotOf(in ContentRef reference) => ResolveRef(reference, _projectileBySlot, _projectileByDefId);

    private static int ResolveRef(in ContentRef reference, Dictionary<string, int> byId, Dictionary<int, int> byDefId)
    {
        // 只有真正的「空引用」才短路：ContentRef.None(-1) 且没有字符串主键。
        // 不能用 `DefId < 0` 判断"未设置"——FNV-1a 32 位哈希有近一半落成负数，
        // 那会把合法引用误判成空引用（物件悄悄丢掉簇 / 半径，且不报错）。
        if (reference.DefId == ContentRef.None && string.IsNullOrEmpty(reference.Id))
        {
            return NoneSlot;
        }
        if (!string.IsNullOrEmpty(reference.Id) && byId.TryGetValue(reference.Id, out int slot))
        {
            return slot;
        }
        if (reference.DefId != ContentRef.None && byDefId.TryGetValue(reference.DefId, out slot))
        {
            return slot;
        }
        return NoneSlot;
    }

    /// <summary>
    /// 按物件定义哈希取物件槽位号（热路径入口：<see cref="ContentRef.DefId"/> 直接喂进来）。
    /// 不存在返回 <see cref="NoneSlot"/>。为什么单独开一个方法而不是让调用方自己凑 <see cref="ContentRef"/>：
    /// 构造一个 <c>ContentRef</c> 会带上 string 引用，这里只需要一次字典查 int。
    /// <para>
    /// <b>注意不能按 <c>&gt;= 0</c> 过滤</b>：<see cref="ContentHash.Fnv1a32"/> 返回的是 true 32 位哈希，
    /// 绝大多数取值落到负数区间。把"负数 = 未设置"当作约定会让差不多一半的合法哈希静默查不到物件
    /// （簇、半径、渲染全部丢失，而且不报错）。所以这里只把 <see cref="NoneSlot"/> 留给"没传"，
    /// 其余任何值都按真实哈希去查字典。
    /// </para>
    /// </summary>
    public int ObjectSlotOfHash(int objectDefHash) =>
        _objectByDefId.TryGetValue(objectDefHash, out int slot) ? slot : NoneSlot;

    /// <summary>把槽位号还原成字符串 id（诊断 / 调试面板用，不在热路径上）。</summary>
    public string ObjectIdOf(int slot) => slot >= 0 && slot < _objects.Count ? _objects[slot].Id : string.Empty;

    /// <summary>武器槽位 → 字符串 id。</summary>
    public string WeaponIdOf(int slot) => slot >= 0 && slot < _weapons.Count ? _weapons[slot].DefId : string.Empty;

    /// <summary>子弹槽位 → 字符串 id。</summary>
    public string ProjectileIdOf(int slot) => slot >= 0 && slot < _projectiles.Count ? _projectiles[slot].DefId : string.Empty;

    /// <summary>
    /// 取某个物件所属的「可替换簇」（<see cref="ObjectArchetype.ClusterSlot"/>）：
    /// 同一簇的原型可以互换（手枪换步枪），熔炉就在簇内选接收者。
    /// </summary>
    public int ClusterOf(int objectSlot) =>
        objectSlot >= 0 && objectSlot < _objects.Count ? _objects[objectSlot].ClusterSlot : ObjectArchetype.NoCluster;
}

/// <summary>
/// 内容层异常：数据表错误（主键重复、引用越界）统一用它。
/// <para>为什么单独一个异常类型而不是 <see cref="ArgumentException"/>：调用方（数据加载 / 宿主）需要区分
/// 「表坏了」和「代码参数写错了」——前者在编辑器里就该报错退出，后者是开发 bug。</para>
/// </summary>
public sealed class ContentException : Exception
{
    /// <summary>构造。</summary>
    public ContentException(string message)
        : base(message)
    {
    }

    /// <summary>构造（带内部异常）。</summary>
    public ContentException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
