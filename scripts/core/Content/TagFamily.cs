using System;
using System.Collections.Generic;

namespace Game.Core.Content;

/// <summary>
/// 标签的**索引轴**（Excel <c>Tags.Family</c> 列的取值域）。
/// <para>
/// <b>为什么需要「轴」这层抽象</b>：一个物体身上会同时挂十几种标签（<c>tag.pistol</c>、<c>tag.kinetic</c>、
/// <c>tag.rare</c>、<c>tag.pierce</c>……）。强化、图鉴、筛选、掉落权重这些玩法都要问
/// 「这个范围的物体有哪些」——但「范围」的对象不是某<b>一个</b>标签，而是某<b>一类</b>标签，
/// 例如「所有稀有的」（Rarity 轴）、「所有手枪」（WeaponKind 轴）、「所有带穿透的」（Ability 轴）。
/// 没有轴这一层，代码只能一条条去问具体标签名，等于把内容硬编码回 C#。
/// </para>
/// <para>
/// <b>轴不是分类，是索引坐标系</b>：物体是点，标签是坐标值。<see cref="TagAxisIndex"/> 把每个轴上的
/// 所有标签各压成一个位掩码，于是「命中该轴任一标签」= 一次按位与。
/// </para>
/// </summary>
public enum TagFamily
{
    /// <summary>未声明轴或轴名无法识别。加载期对非空且认不出的轴会直接报错，本值只兜底空值。</summary>
    None = 0,

    /// <summary>物体大类：人物 / 武器 / 子弹。**这一轴同时决定能力位**（见 <see cref="ObjectCapability"/>）。</summary>
    Kind = 1,

    /// <summary>武器种类：手枪 / 步枪 / 弓 / 近战 …… 同类武器共享手感与原料。</summary>
    WeaponKind = 2,

    /// <summary>弹体种类：直飞 / 追踪 / 弹跳 …… 决定弹道运动学选哪个实现。</summary>
    ProjectileKind = 3,

    /// <summary>伤害元素：动能 / 能量 / 爆炸 …… 抗性与元素联动按这一轴索引。</summary>
    Element = 4,

    /// <summary>
    /// 能力（**可叠加轴**）：穿透、弹跳、分裂、召唤、连发、蓄力……
    /// 这是 ECS 组合的主战场 —— 一个弹可以同时穿透 + 弹跳 + 分裂，所以这一轴刻意<b>不互斥</b>。
    /// </summary>
    Ability = 5,

    /// <summary>触发时机：开火瞬间 / 命中 / 爆散 …… 效果系统按这一轴挂回调。</summary>
    Trigger = 6,

    /// <summary>稀有度：普通 / 精良 / 稀有 …… 掉落权重与图鉴配色按这一轴索引（互斥）。</summary>
    Rarity = 7,

    /// <summary>
    /// 族裔：人形（祖先）/ 哥布林 / 史莱姆 / 食人魔（具体）。
    /// **分层轴**：祖先可与具体族裔同时命中，所以「对人形增伤」能打到哥布林。
    /// </summary>
    Race = 8,

    /// <summary>角色定位：英雄 / 精英 / 小怪 / 远程 …… 波次编排与 AI 选型按这一轴索引（互斥）。</summary>
    Role = 9,

    /// <summary>特效类别：枪口 / 命中 / 爆散 …… 表现层选特效按这一轴索引。</summary>
    Effect = 10,

    // ── 以下属于「标识层」（TagTier.Identity）：不做玩法判定，只做匹配 ──────────────

    /// <summary>
    /// 拾取类别（**标识层**）：弹药物 / 回血物 / 金币 / 遗物 / 钥匙 ……
    /// 道具吸附半径、磁铁筛选、自动拾取白名单按这一轴取范围。
    /// </summary>
    Loot = 11,

    /// <summary>
    /// 被哪些磁场吸引（**标识层，可叠加**）：磁铁 / 武器吸附 / 经验吸附。
    /// 「道具互相吸引」就是对这一轴做交集：<c>被吸的 axis ∩ 吸引源的 axis ≠ ∅</c>。
    /// </summary>
    Attract = 12,

    /// <summary>归属方（**标识层**）：玩家 / 敌方 / 中立。用于同阵营高亮、掉落归属、UI 分组。</summary>
    Faction = 13,

    /// <summary>主题分组（**标识层，可叠加**）：森林 / 地牢 / 熔炉章节。章节化随机与皮肤用。</summary>
    Theme = 14,

    /// <summary>光环 / 共鸣标记（**标识层，可叠加**）：同标记互相加强（元素共鸣、熔炉链）。</summary>
    Aura = 15,

    /// <summary>合成原料类别（**标识层，可叠加**）：金属 / 木材 / 宝石 / 精华。</summary>
    Craft = 16,
}

/// <summary>
/// 标签的**层**（Excel <c>Tags.Tier</c> 列）：这个标签有没有资格影响玩法。
/// <para>
/// <b>为什么层与轴是两个维度</b>：轴回答「属于哪一类维度」，层回答「能不能被规则读到」。
/// 混在一起时，看到 Tags 表有 80 行没法判断哪些能随手加。
/// 分层之后纪律很清楚：**Logic 行加之前先想规则；Identity 行随便加**。
/// </para>
/// </summary>
public enum TagTier
{
    /// <summary>未声明层。绑定器对非空且认不出的层名会直接报错，本值只兜底空值。</summary>
    None = 0,

    /// <summary>
    /// 逻辑功能层：决定玩法行为（能力位 / 熔炉配方字 / 元素抗性 / 掉落权重 / 范围索引）。
    /// 代码会**消费**它，所以每加一个都要先想清规则成本。
    /// </summary>
    Logic = 1,

    /// <summary>
    /// 标识层：只用于归属、吸引、主题、共鸣这类**匹配**，代码不消费它的存在与否。
    /// 强约束：这类标签**一律 BitMask = -1 且 Group = None**（不占配方位、不产生能力位）。
    /// </summary>
    Identity = 2,
}

/// <summary><see cref="TagTier"/> 的解析（名字 ↔ 枚举），与 Excel 词表一一对应。</summary>
public static class TagTiers
{
    /// <summary>Excel 里出现的全部合法层名（顺序 = 枚举值顺序）。</summary>
    public static readonly IReadOnlyList<string> Names = Array.AsReadOnly(new[] { "None", "Logic", "Identity" });

    /// <summary>层名 → 枚举；无法识别返回 <see cref="TagTier.None"/>（调用方决定是否报错）。</summary>
    public static TagTier Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TagTier.None;
        }
        for (int i = 0; i < Names.Count; i++)
        {
            if (string.Equals(Names[i], name!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return (TagTier)i;
            }
        }
        return TagTier.None;
    }

    /// <summary>枚举 → 层名（报错文案与调试面板用）。</summary>
    public static string NameOf(TagTier tier) =>
        (int)tier >= 0 && (int)tier < Names.Count ? Names[(int)tier] : "None";
}

/// <summary><see cref="TagFamily"/> 的解析与元数据（名字 ↔ 枚举、是否互斥轴）。</summary>
public static class TagFamilies
{
    /// <summary>Excel 里出现的全部合法轴名（顺序 = 枚举值顺序，供校验器与面板展示）。</summary>
    public static readonly IReadOnlyList<string> Names = Array.AsReadOnly(new[]
    {
        "None", "Kind", "WeaponKind", "ProjectileKind", "Element", "Ability",
        "Trigger", "Rarity", "Race", "Role", "Effect",
        "Loot", "Attract", "Faction", "Theme", "Aura", "Craft",
    });

    /// <summary>
    /// 互斥轴：同一物体在**一个**互斥轴上最多命中一个标签。
    /// <para>
    /// 反例正是它要拦的错：「既是普通又稀有」「既是英雄又是小怪」「既是动能又是能量」。
    /// 例外是刻意的：<see cref="TagFamily.Ability"/>（穿透+弹跳+分裂本就该叠）与
    /// <see cref="TagFamily.Race"/>（祖先 <c>tag.humanoid</c> 与具体 <c>tag.goblin</c> 是层级关系，不是并列）。
    /// </para>
    /// </summary>
    public static bool IsExclusive(TagFamily family) => family switch
    {
        TagFamily.Kind => true,
        TagFamily.WeaponKind => true,
        TagFamily.ProjectileKind => true,
        TagFamily.Element => true,
        TagFamily.Rarity => true,
        TagFamily.Role => true,
        TagFamily.Effect => true,
        // ── 标识层 ──
        TagFamily.Loot => true,      // 一件东西不会既是弹药物又是金币
        TagFamily.Faction => true,   // 一件东西只会属于一方
        _ => false,                  // Attract / Theme / Aura / Craft 可叠加
    };

    /// <summary>
    /// 该轴属于哪一层。
    /// <para>
    /// 用于「按层筛选标签」：图鉴只列 Logic 层，编辑器面板按层分组，
    /// 校验器用它确认 <b>Identity 层标签没被偷偷接进规则</b>。
    /// </para>
    /// </summary>
    public static TagTier TierOf(TagFamily family) => family switch
    {
        TagFamily.Loot => TagTier.Identity,
        TagFamily.Attract => TagTier.Identity,
        TagFamily.Faction => TagTier.Identity,
        TagFamily.Theme => TagTier.Identity,
        TagFamily.Aura => TagTier.Identity,
        TagFamily.Craft => TagTier.Identity,
        TagFamily.None => TagTier.None,
        _ => TagTier.Logic,
    };

    /// <summary>轴名 → 枚举；无法识别返回 <see cref="TagFamily.None"/>（调用方决定是报错还是忽略）。</summary>
    public static TagFamily Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TagFamily.None;
        }
        for (int i = 0; i < Names.Count; i++)
        {
            if (string.Equals(Names[i], name!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return (TagFamily)i;
            }
        }
        return TagFamily.None;
    }

    /// <summary>枚举 → 轴名（用于报错文案与调试面板）。</summary>
    public static string NameOf(TagFamily family) =>
        (int)family >= 0 && (int)family < Names.Count ? Names[(int)family] : "None";

    /// <summary>枚举全部取值数量（<see cref="TagFamily.None"/> 计入，用作索引数组长度）。</summary>
    public const int Count = 17;
}
