using System;
using System.Collections.Generic;
using System.Text;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;

namespace Game.Data;

/// <summary>
/// 一行技能数据的**纯数据描述**（对应 Excel <c>SkillLoads</c> 表的一行）。
/// <para>
/// <b>本类型不含任何实现</b>：它只说"这一格装什么"（id / 种类 / 冷却 / 消耗 / 槽位），
/// 把行变成 <see cref="ISkill"/> 实例的活儿由宿主组合根通过 <c>ISkillFactory</c> 完成。
/// 这就是「声明与构造分离」——数据层是被依赖方（AGENTS.md 规则5/规则7），不得反向依赖技能层
/// 去 new 具体实现，否则架构反转且被 <c>tests/Layering</c> 守卫抓。
/// </para>
/// <para>
/// <b><see cref="Kind"/> 故意用稳定字符串而不是枚举</b>：枚举会出现"数据层枚举 ↔ 业务层具体类"
/// 的隐式映射表，等于把依赖换了个位置藏起来；字符串让宿主侧自己做 switch，数据层彻底不认识实现。
/// 已知取值：<see cref="KindFireProjectile"/> = 投射物射击；<see cref="KindData"/> = 占位。
/// </para>
/// <para>
/// <b><see cref="WeaponId"/> 与 <see cref="ProjectileId"/> 是两个不同的概念，不要混用</b>：
/// <list type="bullet">
/// <item><see cref="WeaponId"/> = "哪把枪"（武器行 id，查 <see cref="CharacterWeaponSpec"/> 拿伤害/弹速/射程）；</item>
/// <item><see cref="ProjectileId"/> = "子弹长什么样"（抛射物/外观 id，只给射击技能与命令门面用）。</item>
/// </list>
/// 曾经 v1 用武器 id 兼作子弹外观 id，两者都是 string 所以编译期毫无报错、运行期表现为
/// "日志把 pistol_basic 当子弹 id、外观按不存在的抛射物查表"。拆开是为了让抛射物表将来
/// 独立时不必再动技能行形状（规则7）。
/// </para>
/// </summary>
public readonly record struct CharacterSkillSpec(
    int SkillId,
    string Kind,
    string WeaponId,
    string ProjectileId,
    float CooldownSeconds,
    float ManaCost,
    int Slot)
{
    /// <summary>种类：投射物射击（宿主侧实例化为技能层的射击技能）。</summary>
    public const string KindFireProjectile = "fire_projectile";

    /// <summary>种类：占位（只有 id/冷却/消耗，施放不产出任何东西）。</summary>
    public const string KindData = "data";

    /// <summary>本行是否声明为"真开火"的射击技能。</summary>
    public bool IsProjectile =>
        string.Equals(Kind, KindFireProjectile, StringComparison.OrdinalIgnoreCase);

    /// <summary>按槽位号取枚举（Excel 里 Slot 列是 int；越界返回 false）。</summary>
    public bool TryGetSlot(out SkillSlot slot) => CharacterSkillSpecs.TryToSlot(Slot, out slot);

    /// <summary>
    /// 射击类技能是否踩了"武器 id 兼作子弹外观 id"的坑（两者填成一样）。
    /// 解析期据此打中文警告（不抛异常：v1 允许内容作者这么写，但必须让它在日志里可见）。
    /// </summary>
    public bool SuspectsWeaponProjectileConflated =>
        IsProjectile
        && !string.IsNullOrEmpty(WeaponId)
        && string.Equals(WeaponId, ProjectileId, StringComparison.Ordinal);
}

/// <summary>Excel <c>SkillLoads</c> 表 <c>Kind</c> 列的取值与槽位换算（纯数据层工具）。</summary>
public static class CharacterSkillSpecs
{
    /// <summary>最小槽位号（1 = Dash）。</summary>
    public const int MinSlotNumber = 1;

    /// <summary>最大槽位号（5 = Interact）。</summary>
    public const int MaxSlotNumber = 5;   // arch-ok: 五固定槽是架构约定（SkillSlot 枚举项数），非可调玩法数值

    /// <summary>槽位总数（五固定槽）。</summary>
    public const int SlotCount = 5;   // arch-ok: 五固定槽是架构约定（SkillSlot 枚举项数），非可调玩法数值

    /// <summary>全部合法 Kind 取值（供校验与生成器共用）。</summary>
    public static readonly IReadOnlyList<string> KnownKinds = new[] { CharacterSkillSpec.KindFireProjectile, CharacterSkillSpec.KindData };

    /// <summary>把 Excel 的槽位号换成 <see cref="SkillSlot"/>。</summary>
    public static bool TryToSlot(int slotNumber, out SkillSlot slot)
    {
        slot = slotNumber switch
        {
            1 => SkillSlot.Dash,
            2 => SkillSlot.PrimaryAttack,
            3 => SkillSlot.WeaponSwap,
            4 => SkillSlot.Signature,
            5 => SkillSlot.Interact,
            _ => SkillSlot.Dash,
        };
        return slotNumber >= 1 && slotNumber <= 5;
    }

    /// <summary>把 <see cref="SkillSlot"/> 换成 Excel 槽位号（1..5）。</summary>
    public static int ToSlotNumber(SkillSlot slot) => slot switch
    {
        SkillSlot.Dash => 1,
        SkillSlot.PrimaryAttack => 2,
        SkillSlot.WeaponSwap => 3,
        SkillSlot.Signature => 4,
        SkillSlot.Interact => 5,
        _ => 0,
    };
}

/// <summary>
/// 一个装配的**模块集合**（纯数据）：这堆变体要让管线装上哪几组系统。
/// <para>
/// <b>为什么是"集合 + 判定"而不是"一个枚举"</b>：模块是系统的标签（<c>[Module("move_standard")]</c>），
/// 将来还会出现组合（"标准移动 + 冲刺位移包"），用集合表达才不需要改本类型的形状（规则7）。
/// 本类型只做大小写不敏感的名字查找，**不认识任何一个具体模块 id 的内容** —— 新增模块 = 数据加一个名字。
/// </para>
/// <para>顺序即 <c>SystemPipeline.AddModule</c> 的注册顺序，因此 <see cref="Ids"/> 是有序的、去重的。</para>
/// </summary>
public sealed class CharacterModuleSet
{
    private readonly string[] _ids;

    /// <summary>空集合（= 只用默认管线）。</summary>
    public static CharacterModuleSet Empty { get; } = new CharacterModuleSet(Array.Empty<string>());

    /// <summary>构造；<c>null</c>/空白项被忽略，重复项只保留第一次出现的位置。</summary>
    public CharacterModuleSet(IEnumerable<string>? ids)
    {
        var list = new List<string>(4); // arch-ok: 容量提示，不是玩法数值
        if (ids is not null)
        {
            foreach (string? raw in ids)
            {
                string id = Normalize(raw);
                if (id.Length > 0 && !ContainsIn(list, id))
                {
                    list.Add(id);
                }
            }
        }
        _ids = list.ToArray();
    }

    /// <summary>模块数。</summary>
    public int Count => _ids.Length;

    /// <summary>按注册顺序的模块 id（只读）。</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>是否包含某模块（大小写不敏感，两侧空白忽略）。</summary>
    public bool Contains(string? moduleId)
    {
        string id = Normalize(moduleId);
        if (id.Length == 0)
        {
            return false;
        }
        for (int i = 0; i < _ids.Length; i++)
        {
            if (string.Equals(_ids[i], id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>返回并集（新实例；顺序 = 本集合在前、新增项依次追加）。</summary>
    public CharacterModuleSet Union(CharacterModuleSet? other)
    {
        if (other is null || other.Count == 0)
        {
            return this;
        }
        var list = new List<string>(_ids.Length + other._ids.Length);
        list.AddRange(_ids);
        for (int i = 0; i < other._ids.Length; i++)
        {
            if (!ContainsIn(list, other._ids[i]))
            {
                list.Add(other._ids[i]);
            }
        }
        return new CharacterModuleSet(list);
    }

    /// <summary>逗号分隔的模块名（日志用：<c>[Hero] modules=move_standard,aim_cursor</c>）。</summary>
    public string ToLogString() => string.Join(",", _ids);

    /// <inheritdoc />
    public override string ToString() => string.Concat("Modules(", ToLogString(), ')');

    private static bool ContainsIn(List<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string? raw) => raw is null ? string.Empty : raw.Trim();
}

/// <summary>
/// 外观包（Excel <c>VisualPacks</c> 的一行）：角色"长什么样"。
/// <para>
/// 逻辑层不认识这些字段的语义 —— 它们只是<b>数据</b>，被写进 <c>ViewSlot</c> 组件（unmanaged）后交给表现层。
/// 新增一个外观 = 表里加一行，表现层按 <see cref="VisualKey"/> 选外观，**0 行系统代码**（规则7）。
/// </para>
/// </summary>
public sealed class CharacterVisualPack
{
    /// <summary>空外观（没有外观包时使用：白模、默认缩放、不染色）。</summary>
    public static CharacterVisualPack None { get; } =
        // arch-ok: 1 是"原尺寸"这一单位语义，不是平衡数值（docs/rules/04 §3.2 白名单）
        new CharacterVisualPack("none", string.Empty, 1f, -1, 0, string.Empty);

    /// <summary>包 id（表主键，小写下划线）。</summary>
    public string Id { get; }

    /// <summary>模型/预制体路径（<c>res://</c>；v1 白模阶段可以为空 = 用视图槽位默认白模）。</summary>
    public string ModelPath { get; }

    /// <summary>模型缩放（1 = 原尺寸）。</summary>
    public float ModelScale { get; }

    /// <summary>表现层的视图槽位号（宿主外观注册表的下标；-1 = 未解析）。</summary>
    public int VisualSlot { get; }

    /// <summary>配色档位号（表现层取色用）。</summary>
    public int Palette { get; }

    /// <summary>染色（<c>#RRGGBB</c> / <c>#RRGGBBAA</c>；空 = 用配色档位默认色）。</summary>
    public string ColorHex { get; }

    /// <summary>外观包的稳定哈希：写进 <c>ViewSlot.VisualKey</c>（0 预留给"无外观"，这里保证非 0）。</summary>
    public int VisualKey { get; }

    /// <summary>构造（全部字段来自数据表一行）。</summary>
    public CharacterVisualPack(string id, string modelPath, float modelScale, int visualSlot, int palette, string colorHex)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("外观包 Id 不能为空：它是 Excel VisualPacks 的主键。", nameof(id));
        }
        Id = id;
        ModelPath = modelPath ?? string.Empty;
        ModelScale = modelScale;
        VisualSlot = visualSlot;
        Palette = palette;
        ColorHex = colorHex ?? string.Empty;
        VisualKey = StableKeyOf(id);
    }

    /// <summary>id → 稳定非零哈希（FNV-1a 32；0 是"无外观"哨兵，因此撞 0 时顶开，跨进程/跨平台一致）。</summary>
    public static int StableKeyOf(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return 0;
        }
        unchecked
        {
            uint hash = 2166136261u; // arch-ok: FNV-1a 32 位算法的固定初始常量（哈希，非平衡数值）
            for (int i = 0; i < id.Length; i++)
            {
                hash ^= id[i];
                hash *= 16777619u; // arch-ok: FNV-1a 32 位算法的固定质数（哈希，非平衡数值）
            }
            int key = (int)(hash & 0x7FFFFFFF); // arch-ok: 位掩码取非负，逻辑常量
            return key == 0 ? 1 : key; // arch-ok: 哨兵顶开，避免与"无外观"0 冲突
        }
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("VisualPack(", Id, ", slot=", VisualSlot.ToString(), ')');
}

/// <summary>
/// 镜头包（Excel <c>CameraPacks</c> 的一行）：影响"玩家怎么看这个世界"，不影响"他能打多少"。
/// <para>放在数据层而不是表现层的常量里：镜头手感是策划要一天调几十次的东西（规则4）。</para>
/// </summary>
public sealed class CharacterCameraPack
{
    /// <summary>包 id。</summary>
    public string Id { get; }

    /// <summary>正交相机的可视高度（米）。</summary>
    public float Distance { get; }

    /// <summary>俯视角（度）。</summary>
    public float PitchDeg { get; }

    /// <summary>相机水平偏航（度）。</summary>
    public float YawDeg { get; }

    /// <summary>跟随插值系数（0..1，每帧向目标插值多少）。</summary>
    public float FollowLerp { get; }

    /// <summary>光标在屏幕高度上的比例：0.5 = 屏幕中心，越大视野越小（更"狙击镜"）。</summary>
    public float CursorScreenHeightRatio { get; }

    /// <summary>构造（全部字段来自数据表一行）。</summary>
    public CharacterCameraPack(string id, float distance, float pitchDeg, float yawDeg, float followLerp, float cursorScreenHeightRatio)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("镜头包 Id 不能为空：它是 Excel CameraPacks 的主键。", nameof(id));
        }
        Id = id;
        Distance = distance;
        PitchDeg = pitchDeg;
        YawDeg = yawDeg;
        FollowLerp = followLerp;
        CursorScreenHeightRatio = cursorScreenHeightRatio;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("CameraPack(", Id, ')');
}

/// <summary>
/// 手柄包 / 输入包（Excel <c>InputPacks</c> 的一行）：把"物理输入"翻译成意图的那套键位。
/// <para>
/// 字段全是 <b>InputMap action 名</b>而不是具体按键（"左键"是绑定表里 <c>actor_primary_attack</c> 的内容）：
/// 这样换手柄/换键位 = 改 InputMap 或换一个手柄包，系统代码 0 行（规则4 + 规则7）。
/// </para>
/// </summary>
public sealed class CharacterInputPack
{
    /// <summary>包 id。</summary>
    public string Id { get; }

    /// <summary>移动轴动作名（WASD 二维轴；空 = 用默认）。</summary>
    public string MoveAction { get; }

    /// <summary>瞄准轴动作名（鼠标/右摇杆；空 = 用光标位置）。</summary>
    public string AimAxisAction { get; }

    /// <summary>主攻击动作名（对应 PrimaryAttack 槽）。</summary>
    public string PrimaryAction { get; }

    /// <summary>冲刺动作名（对应 Dash 槽；空 = 不改默认绑定）。</summary>
    public string DashAction { get; }

    /// <summary>交互动作名（对应 Interact 槽；空 = 不改默认绑定）。</summary>
    public string InteractAction { get; }

    /// <summary>构造（全部字段来自数据表一行）。</summary>
    public CharacterInputPack(
        string id,
        string moveAction,
        string aimAxisAction,
        string primaryAction,
        string dashAction,
        string interactAction)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("手柄包 Id 不能为空：它是 Excel InputPacks 的主键。", nameof(id));
        }
        Id = id;
        MoveAction = moveAction ?? string.Empty;
        AimAxisAction = aimAxisAction ?? string.Empty;
        PrimaryAction = primaryAction ?? string.Empty;
        DashAction = dashAction ?? string.Empty;
        InteractAction = interactAction ?? string.Empty;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("InputPack(", Id, ')');
}

/// <summary>
/// 武器数据行（Excel <c>Weapons</c> 的一行）：<c>WeaponStats</c> 组件的数据来源。
/// <para>
/// <b>为什么武器数值不放进 StatId</b>：武器是"装备"，一件角色可以换武器（WeaponSwap 槽），
/// 而 <see cref="Game.Core.StatId"/> 是"这个人身上"的属性。混在一起会让"换枪后移速变了"这种耦合发生。
/// 于是武器数值独立成行，射击技能只读 <c>WeaponStats</c> 组件、不读 StatId、更不写死数字（规则4）。
/// </para>
/// </summary>
public sealed class CharacterWeaponSpec
{
    /// <summary>武器 id（表主键，小写下划线）。</summary>
    public string Id { get; }

    /// <summary>稳定数字 id（写进 <c>WeaponStats.WeaponId</c>；来自表里的整数主键）。</summary>
    public int WeaponId { get; }

    /// <summary>单发伤害。</summary>
    public float Damage { get; }

    /// <summary>弹速（米/秒）。</summary>
    public float BulletSpeed { get; }

    /// <summary>射程（米）。</summary>
    public float RangeMeters { get; }

    /// <summary>散布角（度，0 = 完全精准）。</summary>
    public float SpreadDegrees { get; }

    /// <summary>暴击率（0..1）。</summary>
    public float CriticalChance { get; }

    /// <summary>暴击倍率（≥1）。</summary>
    public float CriticalMultiplier { get; }

    /// <summary>子弹半径（米）。</summary>
    public float ProjectileRadius { get; }

    /// <summary>射击间隔（秒）。</summary>
    public float ShotCooldown { get; }

    /// <summary>每次射击的弹丸数（1 = 单发；&gt;1 = 霰弹/多弹丸）。</summary>
    public int PelletsPerShot { get; }

    /// <summary>构造（全部字段来自数据表一行）。</summary>
    public CharacterWeaponSpec(
        string id,
        int weaponId,
        float damage,
        float bulletSpeed,
        float rangeMeters,
        float spreadDegrees,
        float criticalChance,
        float criticalMultiplier,
        float projectileRadius,
        float shotCooldown,
        int pelletsPerShot = 1)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("武器 Id 不能为空：它是 Excel Weapons 的主键。", nameof(id));
        }
        Id = id;
        WeaponId = weaponId;
        Damage = damage;
        BulletSpeed = bulletSpeed;
        RangeMeters = rangeMeters;
        SpreadDegrees = spreadDegrees;
        CriticalChance = criticalChance;
        CriticalMultiplier = criticalMultiplier;
        ProjectileRadius = projectileRadius;
        ShotCooldown = shotCooldown;
        PelletsPerShot = pelletsPerShot;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("Weapon(", Id, ", dmg=", Damage.ToString(), ')');
}

/// <summary>
/// 一个**变体**（Excel <c>VariantAssembly</c> 的一行）：把各包拼成"一个可以生成的角色"。
/// <para>
/// <b>这就是"拼接即可批量出变体"的落点</b>：4 个测试人物变体 = 4 行数据，系统代码 0 行。
/// 本类型只持有<b>引用与 id</b>，不含任何数值（规则4）：数值在 <see cref="BasePackageId"/> 等指向的包里。
/// </para>
/// </summary>
public sealed class CharacterVariantSpec
{
    /// <summary>变体 id（= <c>ActorDefinition.Id</c>，宿主按它生成实体；如 <c>test_hero</c>）。</summary>
    public string Id { get; }

    /// <summary>显示名（HUD 用；如「测试人物 · 标准」）。</summary>
    public string DisplayName { get; }

    /// <summary>HUD 上那行小字（如「变体 · 重甲」；空串 = 不画）。</summary>
    public string VariantTag { get; }

    /// <summary>→ <c>ActorDefinitionRegistry</c>。默认等于 <see cref="Id"/>。</summary>
    public string DefinitionId { get; }

    /// <summary>基础数值包 id（必填）。</summary>
    public string BasePackageId { get; }

    /// <summary>成长层数值包 id（可空）。</summary>
    public string GrowthPackageId { get; }

    /// <summary>装备层数值包 id（可空）。</summary>
    public string EquipmentPackageId { get; }

    /// <summary>技能包 id（必填）。</summary>
    public string LoadoutId { get; }

    /// <summary>武器行 id（可空 = 不带武器，<c>WeaponStats</c> 由装配系统给零值）。</summary>
    public string WeaponRowId { get; }

    /// <summary>外观包 id（必填）。</summary>
    public string VisualPackId { get; }

    /// <summary>镜头包 id（可空 = 用默认镜头）。</summary>
    public string CameraPackId { get; }

    /// <summary>手柄包 id（可空 = 用默认手柄）。</summary>
    public string InputPackId { get; }

    /// <summary>该变体额外需要的模块（与所属装配的模块取并集）。</summary>
    public CharacterModuleSet Modules { get; }

    /// <summary>构造（全部字段来自数据表一行，不做任何默认值猜测）。</summary>
    public CharacterVariantSpec(
        string id,
        string displayName,
        string variantTag,
        string definitionId,
        string basePackageId,
        string growthPackageId,
        string equipmentPackageId,
        string loadoutId,
        string weaponRowId,
        string visualPackId,
        string cameraPackId,
        string inputPackId,
        CharacterModuleSet? modules)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("变体 Id 不能为空：它同时是 ActorDefinition.Id（Excel VariantAssembly 主键）。", nameof(id));
        }
        Id = id;
        DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
        VariantTag = variantTag ?? string.Empty;
        DefinitionId = string.IsNullOrEmpty(definitionId) ? id : definitionId;
        BasePackageId = basePackageId ?? string.Empty;
        GrowthPackageId = growthPackageId ?? string.Empty;
        EquipmentPackageId = equipmentPackageId ?? string.Empty;
        LoadoutId = loadoutId ?? string.Empty;
        WeaponRowId = weaponRowId ?? string.Empty;
        VisualPackId = visualPackId ?? string.Empty;
        CameraPackId = cameraPackId ?? string.Empty;
        InputPackId = inputPackId ?? string.Empty;
        Modules = modules ?? CharacterModuleSet.Empty;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat("Variant(", Id, ')');
}

/// <summary>
/// **装配清单**（Excel <c>Assemblies</c> 的一行 + 它下面的 N 行变体）：一组共享模块的变体家族。
/// <para>
/// 例如装配 <c>hero</c> = 模块 {move_standard, aim_cursor, fire_primary, input_cursor,
/// camera_ortho_topdown, hud_character} + 4 个变体（标准 / 重甲 / 敏捷 / 幸运）。
/// "再加一个变体" = <c>VariantAssembly</c> 加一行（0 代码）；
/// "再加一个装配"（比如敌人装配）= <c>Assemblies</c> 加一行 + 它的变体行，同样 0 代码。
/// </para>
/// </summary>
public sealed class CharacterAssemblySpec
{
    /// <summary>装配 id（如 <c>hero</c>）。</summary>
    public string Id { get; }

    /// <summary>装配显示名。</summary>
    public string DisplayName { get; }

    /// <summary>该装配所有变体共用的模块集合。</summary>
    public CharacterModuleSet Modules { get; }

    /// <summary>该装配下的变体（顺序即表里的顺序；第一个通常是默认变体）。</summary>
    public IReadOnlyList<CharacterVariantSpec> Variants { get; }

    /// <summary>默认变体（第一个）；空装配返回 <c>null</c>。</summary>
    public CharacterVariantSpec? DefaultVariant => Variants.Count > 0 ? Variants[0] : null;

    /// <summary>构造。<paramref name="variants"/> 会被复制成只读快照（之后用仓库的 RegisterVariant 追加）。</summary>
    public CharacterAssemblySpec(string id, string displayName, CharacterModuleSet? modules, IReadOnlyList<CharacterVariantSpec>? variants)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("装配 Id 不能为空：它是 Excel Assemblies 的主键。", nameof(id));
        }
        Id = id;
        DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
        Modules = modules ?? CharacterModuleSet.Empty;
        Variants = variants is null
            ? Array.Empty<CharacterVariantSpec>()
            : new List<CharacterVariantSpec>(variants).AsReadOnly();
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Concat("Assembly(", Id, ", variants=", Variants.Count.ToString(), ")");
}

/// <summary>
/// 一个装配好的**变体**（结构化视图）：装配 + 变体 + 已解析的各包 + 两者并集的模块集合。
/// <para>
/// 这是宿主唯一需要的东西：<see cref="DefinitionSlot"/> 去投 <c>ActorSpawnRequest</c>，
/// <see cref="Modules"/> 去 <c>SystemPipeline.AddModule</c>，<see cref="Visual"/> → <c>ViewSlot</c> 组件，
/// <see cref="Weapon"/> → <c>WeaponStats</c> 组件，<see cref="Camera"/> / <see cref="Input"/> 交给表现层。
/// </para>
/// <para>
/// 泛型参数是这个结构的**扩展点**：以后再出现"技能组包 / 成长包 / 表现包"这类新的一层，
/// 加一个 <c>CharacterAssemblyOf&lt;T&gt;</c> 即可，装配清单的类型本身不用改（规则7）。
/// </para>
/// </summary>
/// <typeparam name="TSkill">技能包类型（v1 = <c>SkillLoadout</c>）。</typeparam>
public sealed class CharacterAssemblyOf<TSkill> where TSkill : class
{
    /// <summary>所属装配。</summary>
    public CharacterAssemblySpec Spec { get; }

    /// <summary>本变体数据行。</summary>
    public CharacterVariantSpec Variant { get; }

    /// <summary>变体在装配内的序号（0 起）。</summary>
    public int VariantIndex { get; }

    /// <summary>装配在仓库内的序号（0 起，进程内稳定）。</summary>
    public int AssemblySlot { get; }

    /// <summary>角色定义在 <c>ActorDefinitionRegistry</c> 里的槽位号（投 <c>ActorSpawnRequest</c> 用）。</summary>
    public int DefinitionSlot { get; }

    /// <summary>已解析的技能包对象。</summary>
    public TSkill Loadout { get; }

    /// <summary>已解析的武器行（变体未配武器时为 <c>null</c>）。</summary>
    public CharacterWeaponSpec? Weapon { get; }

    /// <summary>已解析的外观包。</summary>
    public CharacterVisualPack Visual { get; }

    /// <summary>已解析的镜头包（变体未配时为默认镜头包）。</summary>
    public CharacterCameraPack Camera { get; }

    /// <summary>已解析的手柄包（变体未配时为默认手柄包）。</summary>
    public CharacterInputPack Input { get; }

    /// <summary>该变体实际需要注册的模块（装配模块 ∪ 变体模块）。</summary>
    public CharacterModuleSet Modules { get; }

    /// <summary>构造（仓库内部使用；外部只读）。</summary>
    public CharacterAssemblyOf(
        CharacterAssemblySpec spec,
        CharacterVariantSpec variant,
        int variantIndex,
        int assemblySlot,
        int definitionSlot,
        TSkill loadout,
        CharacterWeaponSpec? weapon,
        CharacterVisualPack visual,
        CharacterCameraPack camera,
        CharacterInputPack input,
        CharacterModuleSet modules)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        Variant = variant ?? throw new ArgumentNullException(nameof(variant));
        VariantIndex = variantIndex;
        AssemblySlot = assemblySlot;
        DefinitionSlot = definitionSlot;
        Loadout = loadout ?? throw new ArgumentNullException(nameof(loadout));
        Weapon = weapon;
        Visual = visual ?? CharacterVisualPack.None;
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Modules = modules ?? CharacterModuleSet.Empty;
    }

    /// <summary>变体 id（= 角色定义 id）。</summary>
    public string Id => Variant.Id;

    /// <summary>日志用的一行：<c>variant=test_hero assembly=hero modules=...</c>。</summary>
    public string ToLogString() =>
        string.Concat("variant=", Id, " assembly=", Spec.Id, " modules=", Modules.ToLogString());

    /// <inheritdoc />
    public override string ToString() => string.Concat("Assembly<", typeof(TSkill).Name, ">(", Id, ')');
}

/// <summary>
/// 装配清单仓库：把若干张表（装配 / 变体 / 外观 / 镜头 / 手柄 / 武器）解析成一批
/// <see cref="CharacterAssemblyOf{TSkill}"/>，并在解析期做**全部外键校验**。
/// <para>
/// <b>为什么校验必须在这里、必须是异常</b>：装配清单是"六张表用 id 互相指"的图，一个拼错的 id
/// （<c>test_hero_tank_base</c> 写成 <c>test_hero_tank</c>）在运行期表现为"角色血量是 0 或 1"，极难排查。
/// 这里在<b>加载期</b>就炸，并且异常信息直说「哪个装配的哪个变体、哪一列、缺哪张表」，直接送回 Excel 单元格。
/// </para>
/// <para>
/// <b>扩展方式</b>：新增一类"包" → 加一个 <c>RegisterXxx</c> + 解析段的一段；新增一种变体 / 一个装配 → 纯数据行，本类不动（规则7）。
/// </para>
/// </summary>
public sealed class CharacterAssemblyRepository
{
    private readonly Dictionary<string, CharacterAssemblySpec> _specs =
        new Dictionary<string, CharacterAssemblySpec>(StringComparer.Ordinal);

    private readonly Dictionary<string, CharacterVisualPack> _visuals =
        new Dictionary<string, CharacterVisualPack>(StringComparer.Ordinal);

    private readonly Dictionary<string, CharacterCameraPack> _cameras =
        new Dictionary<string, CharacterCameraPack>(StringComparer.Ordinal);

    private readonly Dictionary<string, CharacterInputPack> _inputs =
        new Dictionary<string, CharacterInputPack>(StringComparer.Ordinal);

    private readonly Dictionary<string, CharacterWeaponSpec> _weapons =
        new Dictionary<string, CharacterWeaponSpec>(StringComparer.Ordinal);

    private IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> _views =
        Array.Empty<CharacterAssemblyOf<SkillLoadout>>();

    private CharacterCameraPack? _defaultCamera;
    private CharacterInputPack? _defaultInput;

    /// <summary>已登记的装配数。</summary>
    public int AssemblyCount => _specs.Count;

    /// <summary>已登记的外观包数。</summary>
    public int VisualPackCount => _visuals.Count;

    /// <summary>已登记的镜头包数。</summary>
    public int CameraPackCount => _cameras.Count;

    /// <summary>已登记的手柄包数。</summary>
    public int InputPackCount => _inputs.Count;

    /// <summary>已登记的武器行数。</summary>
    public int WeaponCount => _weapons.Count;

    /// <summary>默认镜头包（变体未指定时使用）。</summary>
    public CharacterCameraPack? DefaultCamera => _defaultCamera;

    /// <summary>默认手柄包（变体未指定时使用）。</summary>
    public CharacterInputPack? DefaultInput => _defaultInput;

    // ── 登记（装配期调用一次；重复 id 当场抛） ───────────────────────────

    /// <summary>登记一个装配（同 id 重复抛异常）。登记后必须再次 <see cref="Resolve"/>。</summary>
    public void RegisterAssembly(CharacterAssemblySpec spec)
    {
        if (spec is null)
        {
            throw new ArgumentNullException(nameof(spec));
        }
        if (_specs.ContainsKey(spec.Id))
        {
            throw new ArgumentException(
                string.Concat("装配 id 重复：'", spec.Id, "'。Excel Assemblies 表主键必须唯一。"), nameof(spec));
        }
        _specs.Add(spec.Id, spec);
        InvalidateResolved();
    }

    /// <summary>
    /// 往已登记的装配里追加一个变体（同变体 id 重复抛异常）。登记后同样需要再次 <see cref="Resolve"/>。
    /// <para>
    /// 「加一个变体 = 一行数据」在 C# 侧的落点：<see cref="CharacterAssemblySpec.Variants"/> 是只读快照，
    /// 所以追加必须经过这里而不是直接改列表（只读视图纪律，docs/rules/05 §6.1.1）。
    /// </para>
    /// </summary>
    public void RegisterVariant(string assemblyId, CharacterVariantSpec variant)
    {
        if (variant is null)
        {
            throw new ArgumentNullException(nameof(variant));
        }
        if (string.IsNullOrEmpty(assemblyId) || !_specs.TryGetValue(assemblyId, out CharacterAssemblySpec? spec))
        {
            throw new ArgumentException(string.Concat(
                "找不到装配 '", assemblyId ?? "<null>", "'：必须先 RegisterAssembly 再登记它的变体。"), nameof(assemblyId));
        }
        foreach (KeyValuePair<string, CharacterAssemblySpec> kv in _specs)
        {
            if (FindVariant(kv.Value, variant.Id) is not null)
            {
                throw new ArgumentException(string.Concat(
                    "变体 id 重复：'", variant.Id, "' 已存在于装配 '", kv.Key,
                    "'。变体 id 同时是 ActorDefinition.Id，必须全局唯一。"), nameof(variant));
            }
        }
        var list = new List<CharacterVariantSpec>(spec.Variants.Count + 1);
        list.AddRange(spec.Variants);
        list.Add(variant);
        _specs[assemblyId] = new CharacterAssemblySpec(spec.Id, spec.DisplayName, spec.Modules, list);
        InvalidateResolved();
    }

    /// <summary>登记外观包（同 id 重复抛异常）。</summary>
    public void RegisterVisualPack(CharacterVisualPack pack) =>
        AddUnique(_visuals, pack?.Id, pack, "外观包", "VisualPacks");

    /// <summary>登记镜头包（同 id 重复抛异常）。</summary>
    public void RegisterCameraPack(CharacterCameraPack pack) =>
        AddUnique(_cameras, pack?.Id, pack, "镜头包", "CameraPacks");

    /// <summary>登记手柄包（同 id 重复抛异常）。</summary>
    public void RegisterInputPack(CharacterInputPack pack) =>
        AddUnique(_inputs, pack?.Id, pack, "手柄包", "InputPacks");

    /// <summary>登记武器行（同 id 重复抛异常；同一 WeaponId 数字重复同样抛）。</summary>
    public void RegisterWeapon(CharacterWeaponSpec weapon)
    {
        if (weapon is null)
        {
            throw new ArgumentNullException(nameof(weapon));
        }
        if (_weapons.ContainsKey(weapon.Id))
        {
            throw new ArgumentException(
                string.Concat("武器 id 重复：'", weapon.Id, "'。Excel Weapons 表主键必须唯一。"), nameof(weapon));
        }
        foreach (KeyValuePair<string, CharacterWeaponSpec> kv in _weapons)
        {
            if (kv.Value.WeaponId == weapon.WeaponId)
            {
                throw new ArgumentException(string.Concat(
                    "武器数字 id 重复：'", weapon.Id, "' 与 '", kv.Key, "' 都是 ", weapon.WeaponId.ToString(),
                    "。Weapons.WeaponId 必须唯一（它会写进 WeaponStats.WeaponId）。"), nameof(weapon));
            }
        }
        _weapons.Add(weapon.Id, weapon);
        InvalidateResolved();
    }

    /// <summary>设置默认镜头包 / 默认手柄包（变体列留空时兜底）。</summary>
    public void SetDefaults(CharacterCameraPack camera, CharacterInputPack input)
    {
        _defaultCamera = camera ?? throw new ArgumentNullException(nameof(camera));
        _defaultInput = input ?? throw new ArgumentNullException(nameof(input));
        InvalidateResolved();
    }

    // ── 查询（登记期即可用，不依赖 Resolve） ──────────────────────────────

    /// <summary>按 id 取外观包；不存在返回 <c>null</c>。</summary>
    public CharacterVisualPack? FindVisualPack(string id) => FindIn(_visuals, id);

    /// <summary>按 id 取镜头包；不存在返回 <c>null</c>。</summary>
    public CharacterCameraPack? FindCameraPack(string id) => FindIn(_cameras, id);

    /// <summary>按 id 取手柄包；不存在返回 <c>null</c>。</summary>
    public CharacterInputPack? FindInputPack(string id) => FindIn(_inputs, id);

    /// <summary>按 id 取武器行；不存在返回 <c>null</c>。</summary>
    public CharacterWeaponSpec? FindWeapon(string id) => FindIn(_weapons, id);

    /// <summary>按 id 取装配清单；不存在返回 <c>null</c>。</summary>
    public CharacterAssemblySpec? FindAssembly(string id) => FindIn(_specs, id);

    /// <summary>按变体 id 找它所属的装配；不存在返回 <c>null</c>。</summary>
    public CharacterAssemblySpec? FindAssemblyOfVariant(string variantId)
    {
        if (string.IsNullOrEmpty(variantId))
        {
            return null;
        }
        foreach (KeyValuePair<string, CharacterAssemblySpec> kv in _specs)
        {
            if (FindVariant(kv.Value, variantId) is not null)
            {
                return kv.Value;
            }
        }
        return null;
    }

    /// <summary>按变体 id 取"解析前的数据行"（调试 / 测试用）；不存在返回 <c>null</c>。</summary>
    public CharacterVariantSpec? FindVariantSpec(string variantId)
    {
        CharacterAssemblySpec? spec = FindAssemblyOfVariant(variantId);
        return spec is null ? null : FindVariant(spec, variantId);
    }

    // ── 解析 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析全部装配：把每个变体的各包 id 解析成具体对象，并做**全部外键校验**。
    /// <para>
    /// 所需的外部仓库（角色定义 / 技能包 / 数值包）从 <paramref name="set"/> 里取，本类不持有它们 ——
    /// 这样"内容仓库"与"装配图层"可以分别演进（规则7）。
    /// </para>
    /// </summary>
    /// <param name="set">已构建的角色内容集。</param>
    /// <exception cref="InvalidOperationException">任何缺包 / 缺定义 / 缺武器 / 缺默认镜头手柄；消息里逐条列中文说明。</exception>
    public void Resolve(CharacterContentSet set)
    {
        if (set is null)
        {
            throw new ArgumentNullException(nameof(set));
        }

        var errors = new List<string>(4); // arch-ok: 容量提示，不是玩法数值
        var views = new List<CharacterAssemblyOf<SkillLoadout>>(_specs.Count);

        int assemblySlot = 0;
        foreach (KeyValuePair<string, CharacterAssemblySpec> kv in _specs)
        {
            CharacterAssemblySpec spec = kv.Value;
            IReadOnlyList<CharacterVariantSpec> variants = spec.Variants;
            for (int v = 0; v < variants.Count; v++)
            {
                CharacterVariantSpec variant = variants[v];
                string owner = string.Concat("装配 '", spec.Id, "' 的变体 '", variant.Id, "'");

                // ① 角色定义
                int defSlot = set.Definitions.SlotOf(variant.DefinitionId);
                ActorDefinition? def = defSlot < 0 ? null : set.Definitions.Get(defSlot);
                if (def is null)
                {
                    errors.Add(string.Concat(owner, ": 找不到角色定义 '", variant.DefinitionId,
                        "'（Characters 表缺这一行，或 DefinitionId 拼错）。"));
                    continue;
                }

                // ② 数值包（Base 必填；其余层可空，但有引用就必须存在）
                CheckPack(owner, "BasePackage", variant.BasePackageId, true, set, errors);
                CheckPack(owner, "GrowthPackage", variant.GrowthPackageId, false, set, errors);
                CheckPack(owner, "EquipmentPackage", variant.EquipmentPackageId, false, set, errors);

                // ③ 技能包
                SkillLoadout? loadout = set.Loadouts.Find(variant.LoadoutId);
                if (loadout is null)
                {
                    errors.Add(string.Concat(owner, ": 找不到技能包 '", variant.LoadoutId,
                        "'（SkillLoads 表缺这个 LoadoutId）。"));
                    continue;
                }

                // ④ 外观包
                CharacterVisualPack? visual = FindIn(_visuals, variant.VisualPackId);
                if (visual is null)
                {
                    errors.Add(string.Concat(owner, ": 找不到外观包 '", variant.VisualPackId,
                        "'（VisualPacks 表缺这个 Id）。"));
                }

                // ⑤ 镜头包 / 手柄包（空 = 用默认；默认未设置 = 数据错误）
                CharacterCameraPack? camera = string.IsNullOrEmpty(variant.CameraPackId)
                    ? _defaultCamera
                    : FindIn(_cameras, variant.CameraPackId);
                if (camera is null)
                {
                    errors.Add(string.IsNullOrEmpty(variant.CameraPackId)
                        ? string.Concat(owner, ": CameraPackId 为空且没有设置默认镜头包（SetDefaults）。")
                        : string.Concat(owner, ": 找不到镜头包 '", variant.CameraPackId, "'（CameraPacks 表缺这个 Id）。"));
                }
                CharacterInputPack? input = string.IsNullOrEmpty(variant.InputPackId)
                    ? _defaultInput
                    : FindIn(_inputs, variant.InputPackId);
                if (input is null)
                {
                    errors.Add(string.IsNullOrEmpty(variant.InputPackId)
                        ? string.Concat(owner, ": InputPackId 为空且没有设置默认手柄包（SetDefaults）。")
                        : string.Concat(owner, ": 找不到手柄包 '", variant.InputPackId, "'（InputPacks 表缺这个 Id）。"));
                }

                // ⑥ 武器行（可空：没有武器的角色也合法，装配时 WeaponStats 为零）
                CharacterWeaponSpec? weapon = null;
                if (!string.IsNullOrEmpty(variant.WeaponRowId))
                {
                    weapon = FindIn(_weapons, variant.WeaponRowId);
                    if (weapon is null)
                    {
                        errors.Add(string.Concat(owner, ": 找不到武器行 '", variant.WeaponRowId, "'（Weapons 表缺这个 Id）。"));
                    }
                }

                if (visual is null || camera is null || input is null)
                {
                    continue;
                }

                // ⑦ 定义里的数值包 / 技能包必须与变体行一致。
                //    两处都写 id 是刻意的冗余（表可读 + 定义可独立查），但一旦漂移，
                //    "表里看到的技能包"和"真正装上的技能包"就不是同一个 —— 必须当场炸。
                if (!string.Equals(def.StatPackageIds.Base, variant.BasePackageId, StringComparison.Ordinal))
                {
                    errors.Add(string.Concat(owner, ": BasePackage='", variant.BasePackageId,
                        "' 与角色定义 '", def.Id, "' 的 Base='", def.StatPackageIds.Base, "' 不一致。"));
                }
                if (!string.Equals(def.SkillLoadoutId, variant.LoadoutId, StringComparison.Ordinal))
                {
                    errors.Add(string.Concat(owner, ": LoadoutId='", variant.LoadoutId,
                        "' 与角色定义 '", def.Id, "' 的 SkillLoadout='", def.SkillLoadoutId, "' 不一致。"));
                }

                views.Add(new CharacterAssemblyOf<SkillLoadout>(
                    spec, variant, v, assemblySlot, defSlot, loadout, weapon, visual, camera, input,
                    spec.Modules.Union(variant.Modules)));
            }
            assemblySlot++;
        }

        if (errors.Count > 0)
        {
            _views = Array.Empty<CharacterAssemblyOf<SkillLoadout>>();
            throw new InvalidOperationException(BuildErrorMessage(errors));
        }

        _views = views.AsReadOnly();
    }

    /// <summary>
    /// 全部已解析变体（按装配登记顺序 × 变体内顺序）。这是本类的**唯一查询真源**：
    /// <see cref="Count"/> / <see cref="Find"/> / <see cref="ViewsOf"/> 都从它派生。
    /// </summary>
    public IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> Assemblies => _views;

    /// <summary>已解析的变体总数。</summary>
    public int Count => _views.Count;

    /// <summary>按变体 id 取结构化视图；不存在或尚未解析返回 <c>null</c>。</summary>
    public CharacterAssemblyOf<SkillLoadout>? Find(string variantId)
    {
        if (string.IsNullOrEmpty(variantId))
        {
            return null;
        }
        IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> all = _views;
        for (int i = 0; i < all.Count; i++)
        {
            if (string.Equals(all[i].Id, variantId, StringComparison.Ordinal))
            {
                return all[i];
            }
        }
        return null;
    }

    /// <summary>按变体 id 取，找不到当场抛（宿主用：拼错变体 id 不应静默生成一个空世界）。</summary>
    public CharacterAssemblyOf<SkillLoadout> Get(string variantId) => Find(variantId)
        ?? throw new InvalidOperationException(string.Concat(
            "找不到变体 '", variantId ?? "<null>", "'。已解析的变体：", DescribeVariants(), "。"));

    /// <summary>某装配下的全部已解析变体（换角色 / 循环预览用）。</summary>
    public IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> ViewsOf(string assemblyId)
    {
        var list = new List<CharacterAssemblyOf<SkillLoadout>>(4); // arch-ok: 容量提示，不是玩法数值
        IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> all = _views;
        for (int i = 0; i < all.Count; i++)
        {
            if (string.Equals(all[i].Spec.Id, assemblyId, StringComparison.Ordinal))
            {
                list.Add(all[i]);
            }
        }
        return list;
    }

    private void InvalidateResolved() => _views = Array.Empty<CharacterAssemblyOf<SkillLoadout>>();

    private static void CheckPack(
        string owner, string column, string packId, bool requireNonEmpty, CharacterContentSet set, List<string> errors)
    {
        if (string.IsNullOrEmpty(packId))
        {
            if (requireNonEmpty)
            {
                errors.Add(string.Concat(owner, ": ", column, " 为空 —— 角色必须有基础数值包。"));
            }
            return;
        }
        if (!set.StatPacks.TryGet(packId, out IReadOnlyStatView _))
        {
            errors.Add(string.Concat(owner, ": 找不到数值包 '", packId,
                "'（StatPacks 表缺这个 PackId，或 Layer 写错）。"));
        }
    }

    private static string BuildErrorMessage(List<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("角色装配清单解析失败：共 ").Append(errors.Count).Append(" 个错误，装配中止。");
        for (int i = 0; i < errors.Count; i++)
        {
            sb.Append('\n').Append("  [错误] ").Append(errors[i]);
        }
        sb.Append("\n请回到 data/excel/characters.xlsx 修正上述 Id 后重新生成（tools/data_pipeline/gen_characters_xlsx.py）。");
        return sb.ToString();
    }

    private string DescribeVariants()
    {
        IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> all = _views;
        if (all.Count == 0)
        {
            return "<尚未解析>";
        }
        var sb = new StringBuilder();
        for (int i = 0; i < all.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(all[i].Id);
        }
        return sb.ToString();
    }

    private static void AddUnique<T>(Dictionary<string, T> map, string? id, T? value, string what, string table)
        where T : class
    {
        if (value is null || string.IsNullOrEmpty(id))
        {
            throw new ArgumentException(string.Concat(what, " Id 不能为空：它是 Excel ", table, " 表的主键。"));
        }
        if (map.ContainsKey(id))
        {
            throw new ArgumentException(string.Concat(what, " id 重复：'", id, "'。Excel ", table, " 表主键必须唯一。"));
        }
        map.Add(id, value);
    }

    private static T? FindIn<T>(Dictionary<string, T> map, string? id) where T : class =>
        !string.IsNullOrEmpty(id) && map.TryGetValue(id, out T? found) ? found : null;

    private static CharacterVariantSpec? FindVariant(CharacterAssemblySpec spec, string id)
    {
        IReadOnlyList<CharacterVariantSpec> variants = spec.Variants;
        for (int i = 0; i < variants.Count; i++)
        {
            if (string.Equals(variants[i].Id, id, StringComparison.Ordinal))
            {
                return variants[i];
            }
        }
        return null;
    }
}
