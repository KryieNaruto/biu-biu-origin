using System;
using System.Collections.Generic;

namespace Game.Core.Actors;

/// <summary>
/// 角色数值的**只读视图**（规则6 §6.1.1 硬要求 M6：对外只暴露只读接口，任何外部模块不得持有可写引用）。
/// <para>
/// <b>为什么读口是"视图"而不是"一份 float 字典"</b>：
/// </para>
/// <list type="bullet">
/// <item>伤害结算、HUD、AI 决策都会读它 —— 如果每次读都返回新对象，等于每帧分配；</item>
/// <item>但如果把可写的数值包直接暴露出去，就会出现「夺舍后原怪物的包被悄悄改掉」（规则6 §6.1.1 描述的真实 bug 类型）；</item>
/// <item>所以这里只承诺「按 id 取值」与「读取整层快照」，**没有任何 setter**，写入一律回到层栈 API。</item>
/// </list>
/// <para>
/// <see cref="Get"/> 对「本角色未声明的属性」返回 <c>0</c> 而不是抛异常：跨 schema 读取（Boss 多出来的属性）
/// 是数据驱动的常态，抛异常会让「新怪物带了一个新属性」把老系统的读取路径炸掉。需要区分时用 <see cref="Has"/>。
/// </para>
/// </summary>
public interface IReadOnlyStatView
{
    /// <summary>取最终值（Fold 之后的 Final）。未声明返回 0。</summary>
    float Get(StatId id);

    /// <summary>该角色是否声明了此属性（跨版本/跨内容读取时区分「0」与「没有」）。</summary>
    bool Has(StatId id);

    /// <summary>取某一层的原始值（不含 Fold）。该层缺失返回 0。</summary>
    float GetLayerValue(StatLayer layer, StatId id);

    /// <summary>数值最后被修改的版本号（UI/Buff 面板用来判断是否需要刷新，避免每帧比较全部属性）。</summary>
    int Version { get; }
}

/// <summary>
/// 角色「类型」定义：一套数据驱动的性格描述（**策划可改的内容，不是代码**）。
/// <para>
/// 关键设计：<see cref="Kind"/> 与 <see cref="DefaultTeam"/> 是两个独立字段。
/// 「怪物和角色同源」就落在这里 —— 同一张表、同一套结构，只有两个标签不同；
/// 于是「玩家被夺舍成怪物」= 把 <see cref="StatPackageIds"/> 换成怪物的、<see cref="DefaultTeam"/> 换成玩家的，
/// 系统侧（伤害/移动/AI 门控）一行不改。
/// </para>
/// <para>
/// 本类型**只承载引用与标签**，不含任何数值：数值在 <see cref="StatPackageIds"/> 指向的数值包层栈里，
/// 技能在 <see cref="SkillLoadoutId"/> 指向的技能包里。这就是规则4（数值不进口码）与规则6（数值封装成包）在角色层的落点。
/// </para>
/// </summary>
public sealed class ActorDefinition
{
    /// <summary>定义 id（Excel 主键，小写下划线）。</summary>
    public string Id { get; }

    /// <summary>显示名 key（本地化 key，不存中文字面量）。</summary>
    public string DisplayKey { get; }

    /// <summary>角色类型（玩家/怪物/Boss…）。</summary>
    public ActorKind Kind { get; }

    /// <summary>初始阵营。与 <see cref="Kind"/> 正交：怪物同源，靠这一列区分敌我。</summary>
    public TeamId DefaultTeam { get; }

    /// <summary>初始控制方案（玩家输入 / AI / 脚本）。</summary>
    public ControlScheme Control { get; }

    /// <summary>数值包 id 组（Base 为必填；其余层可空 = 该角色没有此层）。</summary>
    public StatPackageIds StatPackageIds { get; }

    /// <summary>技能包 id（决定 5 个 <see cref="SkillSlot"/> 各装什么）。</summary>
    public string SkillLoadoutId { get; }

    /// <summary>受击盒半径（米）；来自 Excel <c>Characters.HitRadius</c> 列。</summary>
    public float HitRadius { get; }

    /// <summary>受击盒高度（米）；来自 Excel <c>Characters.HitHeight</c> 列。</summary>
    public float HitHeight { get; }

    /// <summary>视图槽位号（表现层用它选模型；逻辑层不知道它长什么样）。</summary>
    public int VisualSlot { get; }

    /// <summary>构造一个角色定义（全部字段来自数据表一行）。</summary>
    public ActorDefinition(
        string id,
        string displayKey,
        ActorKind kind,
        TeamId defaultTeam,
        ControlScheme control,
        StatPackageIds statPackageIds,
        string skillLoadoutId,
        float hitRadius,
        float hitHeight,
        int visualSlot)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("ActorDefinition.Id 不能为空：它是 Excel 主键，生成的实体靠它找回数值包。", nameof(id));
        }
        Id = id;
        DisplayKey = displayKey ?? string.Empty;
        Kind = kind;
        DefaultTeam = defaultTeam;
        Control = control;
        StatPackageIds = statPackageIds;
        SkillLoadoutId = skillLoadoutId ?? string.Empty;
        HitRadius = hitRadius;
        HitHeight = hitHeight;
        VisualSlot = visualSlot;
    }

    /// <inheritdoc />
    public override string ToString() => string.Concat(Id, '(', Kind.ToString(), ',', DefaultTeam.ToString(), ')');
}

/// <summary>
/// 数值包的**槽位引用组**：一个角色由 5 层包组合而成，这里只记 id。
/// <para>
/// <b>为什么不直接持有 <c>StatPackage</c></b>：角色定义是只读配置（Excel 生成物），
/// 而数值包在运行期会被 <c>With()</c> 逐层替换（Buff 到期、装备、夺舍）。
/// 定义若直接持有包，任何一次替换都要改配置对象 —— 那是「把运行时状态写进配置」的经典错误。
/// 持有 id、按 id 查层栈，才能让「同一个怪物定义刷 200 只」共享同一份只读配置。
/// </para>
/// </summary>
public readonly struct StatPackageIds : IEquatable<StatPackageIds>
{
    /// <summary>基础层包 id（必填）。</summary>
    public readonly string Base;

    /// <summary>成长层包 id（空 = 无）。</summary>
    public readonly string Growth;

    /// <summary>装备层包 id（空 = 无）。</summary>
    public readonly string Equipment;

    /// <summary>Buff 层包 id（空 = 无）。</summary>
    public readonly string Buff;

    /// <summary>临时层包 id（空 = 无）。</summary>
    public readonly string Temporary;

    /// <summary>构造一个包引用组。</summary>
    public StatPackageIds(string baseId, string growth = "", string equipment = "", string buff = "", string temporary = "")
    {
        Base = baseId ?? string.Empty;
        Growth = growth ?? string.Empty;
        Equipment = equipment ?? string.Empty;
        Buff = buff ?? string.Empty;
        Temporary = temporary ?? string.Empty;
    }

    /// <summary>按层取包 id；<see cref="StatLayer"/> 不在 0..4 范围返回空串。</summary>
    public string IdOf(StatLayer layer) => layer switch
    {
        StatLayer.Base => Base,
        StatLayer.Growth => Growth,
        StatLayer.Equipment => Equipment,
        StatLayer.Buff => Buff,
        StatLayer.Temporary => Temporary,
        _ => string.Empty,
    };

    /// <summary>替换某一层的包 id，返回新结构（值语义，不动原值）。</summary>
    public StatPackageIds WithId(StatLayer layer, string packageId)
    {
        return layer switch
        {
            StatLayer.Base => new StatPackageIds(packageId, Growth, Equipment, Buff, Temporary),
            StatLayer.Growth => new StatPackageIds(Base, packageId, Equipment, Buff, Temporary),
            StatLayer.Equipment => new StatPackageIds(Base, Growth, packageId, Buff, Temporary),
            StatLayer.Buff => new StatPackageIds(Base, Growth, Equipment, packageId, Temporary),
            StatLayer.Temporary => new StatPackageIds(Base, Growth, Equipment, Buff, packageId),
            _ => this,
        };
    }

    /// <inheritdoc />
    public bool Equals(StatPackageIds other) =>
        Base == other.Base && Growth == other.Growth && Equipment == other.Equipment
        && Buff == other.Buff && Temporary == other.Temporary;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StatPackageIds other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Base, Growth, Equipment, Buff, Temporary);

    /// <summary>值相等。</summary>
    public static bool operator ==(StatPackageIds a, StatPackageIds b) => a.Equals(b);

    /// <summary>值不等。</summary>
    public static bool operator !=(StatPackageIds a, StatPackageIds b) => !a.Equals(b);
}

/// <summary>
/// 数值包层栈的**只读仓库**：按 id 取 <see cref="IReadOnlyStatView"/>。
/// <para>
/// 实现类在 <c>scripts/data/</c>（Excel 生成物加载）；角色系统只依赖这个接口，
/// 因此在 <c>tests/</c> 里可以用一个假的实现跑满逻辑，不需要 Godot 也不需要 Excel。
/// </para>
/// </summary>
public interface IStatPackageRepository
{
    /// <summary>按 id 取一个包/层栈视图；不存在返回 <c>false</c>。</summary>
    bool TryGet(string packageId, out IReadOnlyStatView view);

    /// <summary>构建一个「由 5 层包 id 组成」的合成视图（Fold 之后的 Final）。</summary>
    IReadOnlyStatView Compose(in StatPackageIds ids);
}
