using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;

namespace Game.Data;

/// <summary>
/// 角色内容的**注册清单**（v1）：把 Excel 的 Characters / StatPacks / SkillLoads 三张表
/// 翻译成运行时仓库里的一批对象。
/// <para>
/// <b>为什么需要一个这样的类，而不是在加载器里散着注册</b>：
/// </para>
/// <list type="bullet">
/// <item>它是「数据 → 运行时」的唯一接缝：换一种数据来源（Excel / JSON / 编辑器 Resource）只需要换这个类的填充方式；</item>
/// <item>它可以被<u>无引擎测试</u>直接填充，因此「策划改了表以后装配是否仍然正确」可以在 CI 里验证；</item>
/// <item>等 <c>tools/data_pipeline</c> 的 xlsx→json 生成器落地后，只需要写一个
/// <c>LoadFromGeneratedJson()</c> 来调用这里的 Rebuild 系列方法，本文件其余部分不动（规则7）。</item>
/// </list>
/// </summary>
public sealed class CharacterContentSet
{
    /// <summary>角色定义仓库。</summary>
    public ActorDefinitionRegistry Definitions { get; } = new ActorDefinitionRegistry();

    /// <summary>数值包仓库。</summary>
    public StatPackageRepository StatPacks { get; } = new StatPackageRepository();

    /// <summary>技能包仓库。</summary>
    public SkillLoadoutRegistry Loadouts { get; } = new SkillLoadoutRegistry();

    /// <summary>
    /// 装配清单仓库（v1 追加）：Excel 的 <c>Assemblies</c> / <c>VariantAssembly</c> / <c>VisualPacks</c> /
    /// <c>CameraPacks</c> / <c>InputPacks</c> / <c>Weapons</c> 六张表在这里落地成"可生成的变体"。
    /// <para>
    /// <b>为什么它是仓库上的一个属性而不是一堆静态字段</b>：宿主、HUD、截图脚本都要按变体 id 查装配；
    /// 放在内容集上意味着"一份内容集 = 一份可查询的角色清单"，测试也无引擎即可查询（不需要 Godot）。
    /// </para>
    /// </summary>
    public CharacterAssemblyRepository Assemblies { get; } = new CharacterAssemblyRepository();

    /// <summary>
    /// v1 测试人物（「人物槽位就是测试人物」需求）的装配 id 与默认变体 id。
    /// <para>它们是**数据的入口点**（宿主按名字找到要生成谁），不是数值：数值全在数值包/武器行里。</para>
    /// </summary>
    public const string HeroAssemblyId = "hero";

    /// <summary>默认（标准）变体 id。</summary>
    public const string HeroDefaultVariantId = "test_hero";

    /// <summary>测试人物 4 个变体共用的成长层数值包 id（成长层与 Base 正交：换变体不改成长）。</summary>
    public const string HeroGrowthPackageId = "test_hero_growth";

    /// <summary>是否已经构建过（防止重复注册）。</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>
    /// **按行填充**（唯一正式入口）：把一张表的一行数据注册成运行时对象。
    /// <para>
    /// <b>为什么要把「一行 Excel」拆成公开方法</b>：v1 时内置副本与 <c>characters.xlsx</c> 是两份
    /// 各自手写的内容，靠人工保持同步 —— 一旦漂移就出现「策划改表但游戏没变」这类最难排查的事故。
    /// 把注册拆成逐行方法之后，<b>xlsx 读取与内置副本走的是同一条代码路径</b>，
    /// 于是两者是否一致可以被机械比对，而不是靠人眼（见 <c>tests/Characters/CharacterXlsxParityTests</c>）。
    /// </para>
    /// <para>
    /// <b>累积语义</b>：数值包与技能包在表里都是「一个包里的第 N 行」，而仓库里它们是**一次成型的不可变值**。
    /// 所以 <c>RegisterPackRow</c> / <c>RegisterLoadoutRow</c> 只做累积，真正入仓发生在
    /// <see cref="FlushLoadouts"/> / <see cref="FlushStatPacks"/>。
    /// </para>
    /// <para>
    /// 调用顺序有意义：数值包与技能包必须先于角色行，因为角色行引用前两者的 id。
    /// </para>
    /// </summary>
    public void RegisterPackRow(string packId, StatLayer layer, StatId stat, float value)
    {
        _statPackRows.Add(new StatPackRow(packId, layer, stat, value));
    }

    /// <summary>表里的一行数值包数据（累积期用）。</summary>
    private readonly struct StatPackRow
    {
        public StatPackRow(string packId, StatLayer layer, StatId stat, float value)
        {
            PackId = packId;
            Layer = layer;
            Stat = stat;
            Value = value;
        }

        public string PackId { get; }

        public StatLayer Layer { get; }

        public StatId Stat { get; }

        public float Value { get; }
    }

    /// <summary>累积的数值包行（保持表内顺序，flush 时按包聚合）。</summary>
    private readonly System.Collections.Generic.List<StatPackRow> _statPackRows = new System.Collections.Generic.List<StatPackRow>();

    /// <summary>
    /// 收尾：把累积的数值包行按 (包 id, 层) 聚合后**各注册一次**。
    /// **必须在所有 <see cref="RegisterPackRow"/> 之后调用一次。**
    /// <para>
    /// <b>为什么必须聚合</b>：仓库以包 id 为键、重复注册直接抛异常；而一个包在表里天然是十几行
    /// （`player_base` 就有 13 个属性）。逐行注册会当场炸，不炸则意味着后面的行被静默丢弃 ——
    /// 两种都是不可接受的。
    /// </para>
    /// </summary>
    public void FlushStatPacks()
    {
        var grouped = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<StatId, float>>>(
            StringComparer.Ordinal);
        var order = new System.Collections.Generic.List<string>();
        var layers = new System.Collections.Generic.Dictionary<string, StatLayer>(StringComparer.Ordinal);

        for (int i = 0; i < _statPackRows.Count; i++)
        {
            StatPackRow row = _statPackRows[i];
            if (!grouped.TryGetValue(row.PackId, out System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<StatId, float>>? items))
            {
                items = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<StatId, float>>();
                grouped.Add(row.PackId, items);
                order.Add(row.PackId);
                layers.Add(row.PackId, row.Layer);
            }
            else if (layers[row.PackId] != row.Layer)
            {
                throw new InvalidOperationException(string.Concat(
                    "数值包「", row.PackId, "」在表里同时出现了 ", layers[row.PackId].ToString(),
                    " 与 ", row.Layer.ToString(), " 两层。仓库以包 id 为键，同包跨层会互相覆盖；",
                    "请把不同层的属性拆成不同 PackId。"));
            }
            items.Add(new System.Collections.Generic.KeyValuePair<StatId, float>(row.Stat, row.Value));
        }

        for (int i = 0; i < order.Count; i++)
        {
            string packId = order[i];
            StatPacks.Register(new StatPackageLayer(packId, layers[packId], grouped[packId]));
            _statPackIds.Add(packId);
        }
    }

    /// <summary>按行注册一条角色定义（每个字段显式传入，便于与表列一一对应）。</summary>
    public void RegisterCharacterRow(
        string id,
        string displayKey,
        ActorKind kind,
        TeamId team,
        ControlScheme control,
        StatPackageIds packages,
        string skillLoadoutId,
        float hitRadius,
        float hitHeight,
        int visualSlot)
    {
        Definitions.Register(new ActorDefinition(
            id, displayKey, kind, team, control, packages, skillLoadoutId, hitRadius, hitHeight, visualSlot));
    }

    /// <summary>按行注册一个技能包里的一个槽位。</summary>
    public void RegisterLoadoutRow(string loadoutId, SkillSlot slot, int skillId, float cooldownSeconds, float manaCost)
    {
        if (!_loadoutBuilders.TryGetValue(loadoutId, out System.Collections.Generic.List<ISkill>? builder))
        {
            builder = new System.Collections.Generic.List<ISkill>();
            _loadoutBuilders.Add(loadoutId, builder);
        }
        builder.Add(new DataSkill(skillId, string.Concat("skill_", skillId.ToString()), slot, cooldownSeconds, manaCost));
    }

    /// <summary>技能包分批累积：表是按槽位逐行写的，注册到仓库要一次成型。</summary>
    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ISkill>> _loadoutBuilders =
        new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ISkill>>(StringComparer.Ordinal);

    /// <summary>已登记的全部数值包 id（只读快照，供一致性校验与调试面板遍历）。</summary>
    public string[] StatPacksIds() => _statPackIds.ToArray();

    /// <summary>登记过的数值包 id（按注册顺序，便于输出稳定）。</summary>
    private readonly System.Collections.Generic.List<string> _statPackIds = new System.Collections.Generic.List<string>();

    /// <summary>
    /// 收尾：把累积的技能包真正注册进仓库。**必须在所有 <see cref="RegisterLoadoutRow"/> 之后调用一次。**
    /// </summary>
    public void FlushLoadouts()
    {
        foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.List<ISkill>> pair in _loadoutBuilders)
        {
            Loadouts.Register(SkillLoadout.FromSkills(pair.Key, pair.Value.ToArray()));
        }
    }

    /// <summary>
    /// v1 内置示例内容（与 <c>data/excel/characters.xlsx</c> 保持一致）。
    /// <para>
    /// <b>xlsx→生成物的 pipeline 尚未落地（ARCHITECTURE §4.1）</b>，这里先让运行时有东西可跑；
    /// 等 pipeline 落地后，体被替换为读取生成物即可，所有调用点（宿主、测试、调试场景）不用改。
    /// **唯一的真源是 Excel**：本方法现在改为调用逐行注册方法（与 xlsx 读取同一路径），
    /// 其值必须与表逐项一致，由 <c>CharacterXlsxParityTests</c> 直接读真表机械比对。
    /// <b>为什么 v1 在代码里写一份、Excel 里也写一份</b>：xlsx→生成物的 pipeline 尚未落地（ARCHITECTURE §4.1），
    /// 而游戏又要能跑起来，所以先有这份手写副本；它和 Excel 的一致性由
    /// <c>tools/data_pipeline/gen_characters_xlsx.py</c> 与 <c>tests</c> 下的内容回归用例钉住。
    /// </para>
    /// <para>
    /// 一旦 pipeline 落地，本方法会被 <c>LoadFromGeneratedJson()</c> 取代，**Excel 才成为唯一真源**；
    /// 在那之前，改数值必须两边一起改，否则就是"测试全绿但游戏是错的"。
    /// </para>
    /// </summary>
    public CharacterContentSet BuildBuiltinV1()
    {
        if (IsBuilt)
        {
            // 重复注册会打乱槽位号并让已有实体失效 —— 宁可在构建期炸掉，也不要在运行期静默错乱。
            throw new InvalidOperationException("CharacterContentSet 只能构建一次：重复注册会打乱槽位号并让已有实体失效。");
        }

        RegisterPack("player_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 120f), (StatId.HpMax, 120f), (StatId.Mana, 80f), (StatId.ManaMax, 80f),
            (StatId.ManaRegenPerSec, 4f), (StatId.Armor, 6f), (StatId.ArmorK, 100f),
            (StatId.MoveSpeed, 5.2f), (StatId.Strength, 5f), (StatId.Dexterity, 6f), (StatId.Luck, 3f),
            (StatId.CritChance, 0.05f), (StatId.CritMultiplier, 1.75f), (StatId.HurtInvulnTime, 0.6f),
        });
        RegisterPack("player_growth_demo", StatLayer.Growth, new (StatId, float)[]
        {
            (StatId.HpMax, 10f), (StatId.Strength, 1f),
        });

        RegisterPack("goblin_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 40f), (StatId.HpMax, 40f), (StatId.Mana, 0f), (StatId.ManaMax, 0f),
            (StatId.Armor, 2f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 3.4f),
            (StatId.Strength, 3f), (StatId.Dexterity, 3f), (StatId.Luck, 1f), (StatId.HurtInvulnTime, 0.2f),
        });

        RegisterPack("goblin_archer_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 32f), (StatId.HpMax, 32f), (StatId.Mana, 20f), (StatId.ManaMax, 20f),
            (StatId.Armor, 1f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 3.0f),
            (StatId.Strength, 2f), (StatId.Dexterity, 5f), (StatId.Luck, 1f), (StatId.HurtInvulnTime, 0.2f),
        });

        RegisterPack("ogre_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 300f), (StatId.HpMax, 300f), (StatId.Mana, 0f), (StatId.ManaMax, 0f),
            (StatId.Armor, 25f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 2.2f),
            (StatId.Strength, 18f), (StatId.Dexterity, 2f), (StatId.Luck, 0f), (StatId.HurtInvulnTime, 0.3f),
        });

        RegisterPack("chosen_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 150f), (StatId.HpMax, 150f), (StatId.Mana, 140f), (StatId.ManaMax, 140f),
            (StatId.ManaRegenPerSec, 9f), (StatId.Armor, 4f), (StatId.ArmorK, 100f),
            (StatId.MoveSpeed, 4.8f), (StatId.Strength, 4f), (StatId.Dexterity, 7f), (StatId.Luck, 8f),
            (StatId.CritChance, 0.12f), (StatId.CritMultiplier, 2.0f), (StatId.HurtInvulnTime, 0.8f),
            (StatId.DashSpeedMul, 2.0f), (StatId.DashDuration, 0.2f),
        });

        // ── 怪物/旧角色的技能包（v1 遗留：只有槽位 + 技能 id + 冷却/消耗，没有 Kind）──
        //    数据层的 SkillRows 会把它们补成 Kind = data，让宿主工厂有统一输入。
        RegisterLoadout("player_kit", new (SkillSlot, int, float, float)[]
        {
            (SkillSlot.Dash, 1001, 1.2f, 8f),
            (SkillSlot.PrimaryAttack, 1101, 0.12f, 0f),
            (SkillSlot.WeaponSwap, 1201, 0f, 0f),
            (SkillSlot.Signature, 1301, 6f, 25f),
            (SkillSlot.Interact, 1401, 0f, 0f),
        });
        RegisterLoadout("goblin_kit", new (SkillSlot, int, float, float)[]
        {
            (SkillSlot.PrimaryAttack, 1102, 1.4f, 0f),
        });
        RegisterLoadout("goblin_archer_kit", new (SkillSlot, int, float, float)[]
        {
            (SkillSlot.PrimaryAttack, 1103, 1.8f, 0f),
        });
        RegisterLoadout("ogre_kit", new (SkillSlot, int, float, float)[]
        {
            (SkillSlot.PrimaryAttack, 1104, 2.4f, 0f),
            (SkillSlot.Signature, 1302, 9f, 0f),
        });
        RegisterLoadout("chosen_kit", new (SkillSlot, int, float, float)[]
        {
            (SkillSlot.Dash, 1001, 1.0f, 8f),
            (SkillSlot.PrimaryAttack, 1105, 0.16f, 0f),
            (SkillSlot.WeaponSwap, 1201, 0f, 0f),
            (SkillSlot.Signature, 1303, 8f, 40f),
            (SkillSlot.Interact, 1401, 0f, 0f),
        });

        Definitions.Register(new ActorDefinition("player_hero", "actor.player_hero",
            ActorKind.Player, TeamId.Player, ControlScheme.PlayerInput,
            new StatPackageIds("player_base", growth: "player_growth_demo"), "player_kit", 0.45f, 1.8f, 1));

        Definitions.Register(new ActorDefinition("goblin_grunt", "actor.goblin_grunt",
            ActorKind.Monster, TeamId.Enemy, ControlScheme.Ai,
            new StatPackageIds("goblin_base"), "goblin_kit", 0.4f, 1.2f, 2));

        Definitions.Register(new ActorDefinition("goblin_archer", "actor.goblin_archer",
            ActorKind.Monster, TeamId.Enemy, ControlScheme.Ai,
            new StatPackageIds("goblin_archer_base"), "goblin_archer_kit", 0.4f, 1.2f, 3));

        Definitions.Register(new ActorDefinition("ogre_brute", "actor.ogre_brute",
            ActorKind.Elite, TeamId.Enemy, ControlScheme.Ai,
            new StatPackageIds("ogre_base"), "ogre_kit", 0.9f, 2.6f, 4));

        Definitions.Register(new ActorDefinition("chosen_hero", "actor.chosen_hero",
            ActorKind.Player, TeamId.Player, ControlScheme.PlayerInput,
            new StatPackageIds("chosen_base"), "chosen_kit", 0.5f, 2.0f, 5));

        // ── 补到 5 种怪物：分裂史莱姆 / 爆炸史莱姆 ──
        Definitions.Register(new ActorDefinition("slime_401", "actor.slime_401",
            ActorKind.Monster, TeamId.Enemy, ControlScheme.Ai,
            new StatPackageIds("slime_base"), "slime_kit", 0.5f, 1.0f, 7));

        Definitions.Register(new ActorDefinition("slime_403", "actor.slime_403",
            ActorKind.Monster, TeamId.Enemy, ControlScheme.Ai,
            new StatPackageIds("slime_bomb_base"), "slime_bomb_kit", 0.6f, 1.2f, 8));

        // ══════════════════════════════════════════════════════════════════════════
        // v1 测试人物（hero-1 需求）：一个装配（hero）+ 4 个变体（标准/重甲/敏捷/幸运）。
        // 关键性质：变体之间的差异**全部来自数据行**，系统代码 0 行改动（规则7）。
        // 上面那些旧角色（player_hero / 怪物）保留不动，避免破坏已有测试；
        // 下面新造的数值包/武器/技能包/外观/镜头/输入全部登记进 CharacterAssemblyRepository。
        // ══════════════════════════════════════════════════════════════════════════
        BuildMonsterKits();
        BuildMonsterArsenal();
        BuildHeroV1();

        IsBuilt = true;
        return this;
    }

    /// <summary>
    /// 构建 v1 的 4 个测试人物变体。
    /// <para>
    /// <b>变体之间的差异只在数据</b>：共用数值包的结构、共用武器行的结构，差别只是每一行填的值不同。
    /// </para>
    /// <list type="table">
    /// <listheader><term>变体</term><description>设计意图</description></listheader>
    /// <item><term>标准 test_hero</term><description>均衡基准：120 血 / 80 蓝 / 6 甲 / 5.2 移速 @ 0.15s 冷却</description></item>
    /// <item><term>重甲 test_hero_tank</term><description>血厚、护甲高、移速慢、手感沉</description></item>
    /// <item><term>敏捷 test_hero_agile</term><description>移速快、射速快、伤害低</description></item>
    /// <item><term>幸运 test_hero_lucky</term><description>暴击率高、暴击倍率高</description></item>
    /// </list>
    /// <para>
    /// <b>为什么 Base 包里也放 CritChance/CritMultiplier</b>：
    /// 暴击是"人物属性"，不是"武器属性"；武器只提供它自己那点额外暴击，两者相加。
    /// 所以 4 个变体各自 Base 里都有一份，武器行里再叠一份。
    /// </para>
    /// </summary>
    private void BuildHeroV1()
    {
        // ── 4 个变体的 Base 数值包（五层里的 Base 层 = 绝对值）+ 1 个共用成长包 ──
        RegisterPack("test_hero_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 120f), (StatId.HpMax, 120f),
            (StatId.Mana, 80f), (StatId.ManaMax, 80f), (StatId.ManaRegenPerSec, 4f),
            (StatId.Stamina, 100f), (StatId.StaminaMax, 100f), (StatId.StaminaRegenPerSec, 12f),
            (StatId.Armor, 6f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 5.2f),
            (StatId.MoveAccelTime, 0.12f), (StatId.TurnResponseTime, 0.10f), (StatId.AimRange, 18f),
            (StatId.Strength, 5f), (StatId.Dexterity, 6f), (StatId.Luck, 3f),
            (StatId.CritChance, 0.05f), (StatId.CritMultiplier, 1.75f), (StatId.HurtInvulnTime, 0.6f),
            (StatId.DashSpeedMul, 2.2f), (StatId.DashDuration, 0.18f),
        });
        RegisterPack("test_hero_growth", StatLayer.Growth, new (StatId, float)[]
        {
            (StatId.HpMax, 10f), (StatId.Strength, 1f),
        });
        RegisterPack("test_hero_tank_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 220f), (StatId.HpMax, 220f),
            (StatId.Mana, 60f), (StatId.ManaMax, 60f), (StatId.ManaRegenPerSec, 3f),
            (StatId.Stamina, 120f), (StatId.StaminaMax, 120f), (StatId.StaminaRegenPerSec, 10f),
            (StatId.Armor, 20f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 3.6f),
            (StatId.MoveAccelTime, 0.20f), (StatId.TurnResponseTime, 0.18f), (StatId.AimRange, 16f),
            (StatId.Strength, 9f), (StatId.Dexterity, 3f), (StatId.Luck, 2f),
            (StatId.CritChance, 0.03f), (StatId.CritMultiplier, 1.5f), (StatId.HurtInvulnTime, 0.8f),
            (StatId.DashSpeedMul, 1.8f), (StatId.DashDuration, 0.22f),
        });
        RegisterPack("test_hero_agile_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 90f), (StatId.HpMax, 90f),
            (StatId.Mana, 100f), (StatId.ManaMax, 100f), (StatId.ManaRegenPerSec, 6f),
            (StatId.Stamina, 140f), (StatId.StaminaMax, 140f), (StatId.StaminaRegenPerSec, 16f),
            (StatId.Armor, 3f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 7.4f),
            (StatId.MoveAccelTime, 0.06f), (StatId.TurnResponseTime, 0.05f), (StatId.AimRange, 20f),
            (StatId.Strength, 3f), (StatId.Dexterity, 11f), (StatId.Luck, 4f),
            (StatId.CritChance, 0.08f), (StatId.CritMultiplier, 1.6f), (StatId.HurtInvulnTime, 0.4f),
            (StatId.DashSpeedMul, 2.8f), (StatId.DashDuration, 0.16f),
        });
        RegisterPack("test_hero_lucky_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 110f), (StatId.HpMax, 110f),
            (StatId.Mana, 90f), (StatId.ManaMax, 90f), (StatId.ManaRegenPerSec, 5f),
            (StatId.Stamina, 110f), (StatId.StaminaMax, 110f), (StatId.StaminaRegenPerSec, 12f),
            (StatId.Armor, 5f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 5.4f),
            (StatId.MoveAccelTime, 0.12f), (StatId.TurnResponseTime, 0.10f), (StatId.AimRange, 18f),
            (StatId.Strength, 4f), (StatId.Dexterity, 6f), (StatId.Luck, 15f),
            (StatId.CritChance, 0.15f), (StatId.CritMultiplier, 2.0f), (StatId.HurtInvulnTime, 0.6f),
            (StatId.DashSpeedMul, 2.4f), (StatId.DashDuration, 0.18f),
        });
        // 元素法师：蓝量与回蓝最高、瞄准距离最远，但血量最低、移速慢 —— 靠技能输出
        RegisterPack("test_hero_elemental_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 70f), (StatId.HpMax, 70f),
            (StatId.Mana, 180f), (StatId.ManaMax, 180f), (StatId.ManaRegenPerSec, 14f),
            (StatId.Stamina, 90f), (StatId.StaminaMax, 90f), (StatId.StaminaRegenPerSec, 10f),
            (StatId.Armor, 3f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 4.4f),
            (StatId.MoveAccelTime, 0.14f), (StatId.TurnResponseTime, 0.12f), (StatId.AimRange, 26f),
            (StatId.Strength, 2f), (StatId.Dexterity, 5f), (StatId.Luck, 6f),
            (StatId.CritChance, 0.08f), (StatId.CritMultiplier, 1.9f), (StatId.HurtInvulnTime, 0.5f),
            (StatId.DashSpeedMul, 2.0f), (StatId.DashDuration, 0.20f),
        });

        // ── 新增怪物：两种史莱姆（把怪物补齐到 5 种） ──
        // 分裂史莱姆：血薄、慢、零甲，靠数量压制
        RegisterPack("slime_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 30f), (StatId.HpMax, 30f),
            (StatId.Mana, 0f), (StatId.ManaMax, 0f),
            (StatId.Armor, 0f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 2.6f),
            (StatId.Strength, 2f), (StatId.Dexterity, 2f), (StatId.Luck, 1f),
            (StatId.HurtInvulnTime, 0.15f),
        });
        // 爆炸史莱姆：血稍厚、更慢，死亡爆散
        RegisterPack("slime_bomb_base", StatLayer.Base, new (StatId, float)[]
        {
            (StatId.Hp, 55f), (StatId.HpMax, 55f),
            (StatId.Mana, 0f), (StatId.ManaMax, 0f),
            (StatId.Armor, 4f), (StatId.ArmorK, 100f), (StatId.MoveSpeed, 2.2f),
            (StatId.Strength, 6f), (StatId.Dexterity, 1f), (StatId.Luck, 0f),
            (StatId.HurtInvulnTime, 0.2f),
        });

        // ── 武器行（WeaponStats 的数据来源） ──
        // 先建武器行：技能包的**左键冷却直接取武器行的 ShotCooldown**（单一真源）。
        // arch-ok: 以下 4 行是 Excel Weapons 表的运行时镜像，逐项与 data/excel/characters.xlsx 一致，
        //          真源是 Excel；由 tools/data_pipeline/gen_characters_xlsx.py 生成并被回归用例钉住。
        var pistol = new CharacterWeaponSpec("pistol_basic", 2001, 20f, 22f, 24f, 1.5f, 0.05f, 1.0f, 0.10f, 0.15f, 1); // arch-ok: 镜像 Weapons 表
        var cannon = new CharacterWeaponSpec("cannon_heavy", 2002, 55f, 14f, 26f, 5.0f, 0.00f, 1.00f, 0.22f, 0.42f, 1); // arch-ok: 镜像 Weapons 表
        var smg = new CharacterWeaponSpec("smg_light", 2003, 8f, 26f, 18f, 4.0f, 0.03f, 1.00f, 0.07f, 0.08f, 1); // arch-ok: 镜像 Weapons 表
        var ricochet = new CharacterWeaponSpec("ricochet_lucky", 2004, 16f, 20f, 22f, 2.5f, 0.52f, 2.25f, 0.11f, 0.18f, 1); // arch-ok: 镜像 Weapons 表
        var orbElemental = new CharacterWeaponSpec("orb_elemental", 2005, 12f, 16f, 30f, 3.0f, 0.08f, 1.90f, 0.14f, 0.22f, 1); // arch-ok: 镜像 Weapons 表
        Assemblies.RegisterWeapon(pistol);
        Assemblies.RegisterWeapon(cannon);
        Assemblies.RegisterWeapon(smg);
        Assemblies.RegisterWeapon(ricochet);
        Assemblies.RegisterWeapon(orbElemental);

        // ── 技能包：4 个（左键槽 = FireProjectileSkill，真开火） ──
        // 技能 id 号段（与 Excel SkillLoads 表一致，全项目唯一）：
        //   1001 通用冲刺 / 1101 标准射击 / 1102 哥布林近战 / 1103 弓箭 / 1104 重砸 / 1105 法弹 /
        //   1106 重型射击 / 1107 快速射击 / 1108 幸运连射 / 1201 切武器 / 130x 角色独有 / 1401 交互。
        //
        // 左键冷却 = 武器行的 ShotCooldown（单一真源），所以这里不重复填 0.15/0.42/0.08/0.18；
        // 冲刺/独有技的 Cooldown 与 ManaCost 是技能包自己的属性，才在这里给数。
        RegisterHeroKit("test_hero_kit", 1101, pistol, 1001, 1.2f, 8f, 1304, 12f, 30f);
        RegisterHeroKit("test_hero_tank_kit", 1106, cannon, 1001, 2.0f, 10f, 1305, 16f, 35f);
        RegisterHeroKit("test_hero_agile_kit", 1107, smg, 1001, 0.8f, 6f, 1306, 10f, 25f);
        RegisterHeroKit("test_hero_lucky_kit", 1108, ricochet, 1001, 1.2f, 8f, 1307, 14f, 30f);
        RegisterHeroKit("test_hero_elemental_kit", 1109, orbElemental, 1001, 1.4f, 12f, 1308, 18f, 60f);

        // ── 外观包 ──
        Assemblies.RegisterVisualPack(new CharacterVisualPack("hero_default", string.Empty, 1.00f, 1, 0, "#4FA3D9"));
        Assemblies.RegisterVisualPack(new CharacterVisualPack("hero_tank", string.Empty, 1.25f, 2, 1, "#D97A3F"));
        Assemblies.RegisterVisualPack(new CharacterVisualPack("hero_agile", string.Empty, 0.90f, 3, 2, "#5DD97A"));
        Assemblies.RegisterVisualPack(new CharacterVisualPack("hero_lucky", string.Empty, 1.00f, 4, 3, "#D9C64F"));
        Assemblies.RegisterVisualPack(new CharacterVisualPack("hero_elemental", string.Empty, 1.00f, 6, 6, "#9B6FD9"));

        // ── 镜头包（俯视角正交相机；两台：标准 / 宽景） ──
        // arch-ok: 这里是 Excel 的运行时镜像，数值与 data/excel/characters.xlsx 的 CameraPacks 表逐项一致；
        //          真源是 Excel，由 tools/data_pipeline/gen_characters_xlsx.py 生成并被回归用例钉住。
        var camStandard = new CharacterCameraPack(
            "topdown_standard", 18f, 58f, 45f,
            followRatePerSecond: 10f, cursorScreenHeightRatio: 0.55f,
            lookAheadRatio: 0.18f, lookAheadMaxMeters: 1.6f); // arch-ok: 镜像 CameraPacks 表（默认手感）
        var camWide = new CharacterCameraPack(
            "topdown_wide", 22f, 62f, 45f,
            followRatePerSecond: 14f, cursorScreenHeightRatio: 0.60f,
            lookAheadRatio: 0.24f, lookAheadMaxMeters: 2.0f); // arch-ok: 镜像 CameraPacks 表（宽景派生取值）
        Assemblies.RegisterCameraPack(camStandard);
        Assemblies.RegisterCameraPack(camWide);

        // ── 手柄/输入包 ──
        var kbmStandard = new CharacterInputPack("kbm_standard",
            "actor_move", "actor_aim", "actor_primary_attack", "actor_dash", "actor_interact");
        var kbmAlt = new CharacterInputPack("kbm_alt",
            "actor_move", "actor_aim", "actor_primary_attack", "actor_dash", "actor_interact");
        Assemblies.RegisterInputPack(kbmStandard);
        Assemblies.RegisterInputPack(kbmAlt);
        Assemblies.SetDefaults(camStandard, kbmStandard);

        // ── 4 个变体行（= Excel Characters 表 4 行 + VariantAssembly 表 4 行）──
        // 每个变体各自一行；差异全部在这 4 行里的数据，系统代码 0 行。
        // 登记走 RegisterHeroVariant：一行数据同时喂 ActorDefinition 与 CharacterVariantSpec。
        var rows = new[]
        {
            new HeroRow("test_hero", "测试人物", "变体 · 标准", "test_hero_base", "test_hero_kit",
                pistol, "hero_default", camStandard, kbmStandard, CharacterModuleSet.Empty, 0.45f, 1.8f, 1),
            new HeroRow("test_hero_tank", "测试人物", "变体 · 重甲", "test_hero_tank_base", "test_hero_tank_kit",
                cannon, "hero_tank", camStandard, kbmStandard, CharacterModuleSet.Empty, 0.55f, 2.0f, 2),
            new HeroRow("test_hero_agile", "测试人物", "变体 · 敏捷", "test_hero_agile_base", "test_hero_agile_kit",
                smg, "hero_agile", camWide, kbmStandard,
                // 敏捷型额外挂一个"快速射击"模块（示例：变体可以比装配多出模块）。
                new CharacterModuleSet(new[] { "fire_primary_rapid" }), 0.40f, 1.7f, 3),
            new HeroRow("test_hero_lucky", "测试人物", "变体 · 幸运", "test_hero_lucky_base", "test_hero_lucky_kit",
                ricochet, "hero_lucky", camStandard, kbmAlt, CharacterModuleSet.Empty, 0.45f, 1.8f, 4),
            new HeroRow("test_hero_elemental", "测试人物", "变体 · 元素", "test_hero_elemental_base", "test_hero_elemental_kit",
                orbElemental, "hero_elemental", camWide, kbmStandard, CharacterModuleSet.Empty, 0.45f, 1.9f, 6),
        };

        // ── 装配（hero）：4 个变体共用的一批模块 ──
        // 装配 id = 宿主按名字找的那个；变体额外模块由 AddModule 追加后与这里取并集。
        // 覆盖：移动 + 瞄准 + 射击 + 输入 + 相机 + HUD，共 6 个模块。
        var heroModules = new CharacterModuleSet(new[]
        {
            "move_standard", "aim_cursor", "fire_primary", "input_cursor",
            "camera_ortho_topdown", "hud_character",
        });

        var hero = new CharacterAssemblySpec(HeroAssemblyId, "测试人物装配", heroModules, null);
        Assemblies.RegisterAssembly(hero);
        for (int i = 0; i < rows.Length; i++)
        {
            RegisterHeroVariant(hero, rows[i]);
        }

        // 最后：按 id 把变体引用的各包（数值包/技能包/武器/外观/镜头/输入）全部解析一遍，
        // 断链在构建期立刻报错，而不是留到运行期变成"人物没有外观"。
        Assemblies.Resolve(this);
    }

    /// <summary>
    /// 一行变体数据（= Excel 的 VariantAssembly 表的一行）。
    /// <para>把"定义 + 变体 + 各包引用"收成一行数据，避免在注册函数里散落十几个参数；用 readonly 结构体以免分配。</para>
    /// </summary>
    private readonly struct HeroRow
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly string VariantTag;
        public readonly string BasePack;
        public readonly string LoadoutId;
        public readonly CharacterWeaponSpec Weapon;
        public readonly string VisualPack;
        public readonly CharacterCameraPack Camera;
        public readonly CharacterInputPack Input;
        public readonly CharacterModuleSet Modules;
        public readonly float HitRadius;
        public readonly float HitHeight;
        public readonly int VisualSlot;

        public HeroRow(string id, string displayName, string variantTag, string basePack, string loadoutId,
            CharacterWeaponSpec weapon, string visualPack, CharacterCameraPack camera, CharacterInputPack input,
            CharacterModuleSet modules, float hitRadius, float hitHeight, int visualSlot)
        {
            Id = id;
            DisplayName = displayName;
            VariantTag = variantTag;
            BasePack = basePack;
            LoadoutId = loadoutId;
            Weapon = weapon;
            VisualPack = visualPack;
            Camera = camera;
            Input = input;
            Modules = modules;
            HitRadius = hitRadius;
            HitHeight = hitHeight;
            VisualSlot = visualSlot;
        }
    }

    /// <summary>
    /// 登记一个测试人物变体（= Excel 的 Characters 表一行 + VariantAssembly 表一行）。
    /// </summary>
    private void RegisterHeroVariant(CharacterAssemblySpec assembly, in HeroRow row)
    {
        Definitions.Register(new ActorDefinition(row.Id, string.Concat("actor.", row.Id),
            ActorKind.Player, TeamId.Player, ControlScheme.PlayerInput,
            new StatPackageIds(row.BasePack, growth: HeroGrowthPackageId), row.LoadoutId,
            row.HitRadius, row.HitHeight, row.VisualSlot));

        Assemblies.RegisterVariant(assembly.Id, new CharacterVariantSpec(
            id: row.Id,
            displayName: row.DisplayName,
            variantTag: row.VariantTag,
            definitionId: row.Id,
            basePackageId: row.BasePack,
            growthPackageId: HeroGrowthPackageId,
            equipmentPackageId: string.Empty,
            loadoutId: row.LoadoutId,
            weaponRowId: row.Weapon.Id,
            visualPackId: row.VisualPack,
            cameraPackId: row.Camera.Id,
            inputPackId: row.Input.Id,
            modules: row.Modules));
    }

    private void RegisterPack(string packId, StatLayer layer, (StatId Stat, float Value)[] entries)
    {
        var items = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<StatId, float>>(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            items.Add(new System.Collections.Generic.KeyValuePair<StatId, float>(entries[i].Stat, entries[i].Value));
        }
        StatPacks.Register(new StatPackageLayer(packId, layer, items));
        if (!_statPackIds.Contains(packId))
        {
            _statPackIds.Add(packId);
        }
    }

    /// <summary>
    /// 登记一个"会开火"的技能包：左键槽声明为 <see cref="CharacterSkillSpec.KindFireProjectile"/>。
    /// 其余四槽（冲刺/切武器/独有/交互）用 <see cref="CharacterSkillSpec.KindData"/> 占位。
    /// <para>
    /// <b>为什么这里只出数据、不 new 具体技能类</b>：数据层不许 <c>using Game.Skill</c>（AGENTS.md 规则5、规则7），
    /// 否则数据层就反向依赖了业务层，<c>ISkillFactory</c> 也就失去意义。
    /// 所以这里只把 5 行技能行塞进 <see cref="SkillRows"/>（键 = SkillId），实例化交给宿主。
    /// </para>
    /// <para>
    /// <b>左键冷却直接取 <see cref="CharacterWeaponSpec.ShotCooldown"/></b>：这是"单一真源"——武器的射击间隔
    /// 就是左键技能的冷却，两边各填一份数迟早会不一致（曾经出现过两份对不上的事故）。
    /// 冲刺/独有技的冷却与消耗是技能包自己的属性，由调用方传入。
    /// </para>
    /// </summary>
    private void RegisterHeroKit(
        string loadoutId,
        int attackSkillId,
        CharacterWeaponSpec weapon,
        int dashSkillId, float dashCooldown, float dashMana,
        int signatureSkillId, float signatureCooldown, float signatureMana)
    {
        // 子弹外观 id：与武器 id 分开（值故意不同），避免"武器 id 兼作抛射物 id"的语义混淆。
        string projectileId = string.Concat("proj_", weapon.Id);

        var rows = new[]
        {
            // 冲刺：kind=dash。倍率/时长不在这一行，而在角色的数值包
            // （StatId.DashSpeedMul / DashDuration）—— 四个变体因此冲出四种手感，本行 0 处重复。
            new CharacterSkillSpec(dashSkillId, CharacterSkillSpec.KindDash,
                string.Empty, string.Concat("skill_", dashSkillId.ToString()),
                dashCooldown, dashMana, CharacterSkillSpecs.ToSlotNumber(SkillSlot.Dash)),
            // 左键：真开火。WeaponId = 哪把枪（查伤害/弹速/射程）；ProjectileId = 子弹外观。
            new CharacterSkillSpec(attackSkillId, CharacterSkillSpec.KindFireProjectile,
                weapon.Id, projectileId,
                weapon.ShotCooldown, 0f, CharacterSkillSpecs.ToSlotNumber(SkillSlot.PrimaryAttack)),
            new CharacterSkillSpec(1201, CharacterSkillSpec.KindData, string.Empty, "skill_1201",
                0f, 0f, CharacterSkillSpecs.ToSlotNumber(SkillSlot.WeaponSwap)),
            new CharacterSkillSpec(signatureSkillId, CharacterSkillSpec.KindData,
                string.Empty, string.Concat("skill_", signatureSkillId.ToString()),
                signatureCooldown, signatureMana, CharacterSkillSpecs.ToSlotNumber(SkillSlot.Signature)),
            new CharacterSkillSpec(1401, CharacterSkillSpec.KindData, string.Empty, "skill_1401",
                0f, 0f, CharacterSkillSpecs.ToSlotNumber(SkillSlot.Interact)),
        };

        // ① 技能行按 SkillId 进字典（宿主 ISkillFactory 的输入，数据层的对外契约）。
        for (int i = 0; i < rows.Length; i++)
        {
            _skillRows[rows[i].SkillId] = rows[i];
            // 技能包 = 「槽位 → SkillId」映射（宿主工厂按它逐槽组装）。
            RecordLoadoutSlot(loadoutId, rows[i].Slot, rows[i].SkillId);

            // ② 防"武器 id 兼作子弹外观 id"回归：装配解析时记一条中文警告（不抛，v1 允许但必须可见）。
            if (rows[i].SuspectsWeaponProjectileConflated)
            {
                _warnings.Add(string.Concat(
                    "技能包 '", loadoutId, "' 的槽位 ", rows[i].Slot.ToString(),
                    "（技能 ", rows[i].SkillId.ToString(), "）的 WeaponId 与 ProjectileId 相同（都是 '",
                    rows[i].WeaponId, "'）：武器 id 不应兼作子弹外观 id，否则抛射物表独立时会对不上。"));
            }
        }

        // ③ 同时保留一个占位技能包，保证"技能包能被组合、冷却与资源能被门控"这条验收点
        //    在宿主接上工厂之前依然成立（DataSkill 是数据层自有的行投影，不依赖 Game.Skill）。
        var skills = new ISkill[rows.Length];
        for (int i = 0; i < skills.Length; i++)
        {
            CharacterSkillSpec row = rows[i];
            row.TryGetSlot(out SkillSlot slot);
            skills[i] = new DataSkill(row.SkillId, row.ProjectileId, slot, row.CooldownSeconds, row.ManaCost);
        }
        Loadouts.Register(SkillLoadout.FromSkills(loadoutId, skills));
    }

    /// <summary>
    /// 全部技能行（键 = <see cref="CharacterSkillSpec.SkillId"/>）。宿主 <c>ISkillFactory</c>
    /// 按 <see cref="CharacterSkillSpec.Kind"/> 决定实例化成什么实现 —— 数据层只出描述，不构造。
    /// </summary>
    public IReadOnlyDictionary<int, CharacterSkillSpec> SkillRows => _skillRows;

    /// <summary>
    /// 内容装配过程中发现的**非致命**数据问题（中文说明）。
    /// <para>
    /// 例如"武器 id 兼作子弹外观 id"这类 v1 允许、但将来会踩坑的写法。
    /// 与解析异常不同：这些不阻止启动，但必须在日志里可见，否则就是埋雷。
    /// 测试拿它当"内容质量总闸"——正常情况下必须是 0 条。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = new();

    private readonly Dictionary<int, CharacterSkillSpec> _skillRows = new();

    private void RegisterLoadout(string loadoutId, (SkillSlot Slot, int SkillId, float Cooldown, float Mana)[] entries)
    {
        var skills = new ISkill[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            skills[i] = new DataSkill(
                entries[i].SkillId, string.Concat("skill_", entries[i].SkillId.ToString()),
                entries[i].Slot, entries[i].Cooldown, entries[i].Mana);

            // 怪物/旧角色的技能行同样进字典（Kind = data），让宿主的工厂有统一的输入来源。
            if (!_skillRows.ContainsKey(entries[i].SkillId))
            {
                _skillRows[entries[i].SkillId] = new CharacterSkillSpec(
                    entries[i].SkillId, CharacterSkillSpec.KindData,
                    string.Empty, string.Concat("skill_", entries[i].SkillId.ToString()),
                    entries[i].Cooldown, entries[i].Mana,
                    CharacterSkillSpecs.ToSlotNumber(entries[i].Slot));
            }

            // 技能包 = 「槽位 → SkillId」映射（宿主工厂按它逐槽组装）。
            RecordLoadoutSlot(loadoutId, CharacterSkillSpecs.ToSlotNumber(entries[i].Slot), entries[i].SkillId);
        }
        Loadouts.Register(SkillLoadout.FromSkills(loadoutId, skills));
    }

    /// <summary>
    /// 两种史莱姆的技能包：只有左键一槽（怪物没有冲刺/切武器/交互）。
    /// <para>
    /// 与人物技能包同构 —— 怪物和玩家用的是同一套技能行结构，
    /// 差异只在**填了几个槽**。这是"玩家怪物同源"的又一处体现。
    /// </para>
    /// </summary>
    private void BuildMonsterKits()
    {
        RegisterLoadout("slime_kit", new[]
        {
            (SkillSlot.PrimaryAttack, 1102, 1.6f, 0f),
        });
        RegisterLoadout("slime_bomb_kit", new[]
        {
            (SkillSlot.PrimaryAttack, 1104, 2.2f, 0f),
        });
    }

    /// <summary>
    /// 给 5 只怪物配齐「默认技能 + 默认武器 + 默认子弹」，让它们真正能开枪。
    /// <para>
    /// <b>为什么单独一个方法</b>：<see cref="BuildMonsterKits"/> 出的是"技能包骨架"（早期只有
    /// 一个 <see cref="CharacterSkillSpec.KindData"/> 占位槽，怪物实质上永远不开火）；本方法把
    /// 其中"会开火"的那几只补成与人物<u>完全同构</u>的形状 —— 同样走
    /// <see cref="RegisterMonsterKit"/> → <see cref="CharacterSkillSpec.KindFireProjectile"/>
    /// → 宿主 <c>ContentSkillFactory</c> 实例化 <c>FireProjectileSkill</c> 这一条路。
    /// </para>
    /// <para>
    /// <b>近战怪为什么没有子弹</b>：<c>goblin_grunt</c> 与 <c>ogre_brute</c> 的武器行
    /// <c>ProjectileId</c> 为空是**刻意的**（近战不发射抛射物，靠碰撞体结算）。它们仍然登记了
    /// 武器行，所以"左键 → 武器 → 伤害数值"这条链是通的，只是末端不出子弹。
    /// 这正是"武器→子弹可空"与"武器→子弹缺失"的区别，回归用例会分别断言。
    /// </para>
    /// </summary>
    private void BuildMonsterArsenal()
    {
        // ── 怪物武器行（号段 30xx，与人物 2001..2005 分开，避免查表串味） ──
        // 数值刻意比人物低一档：小怪是消耗品，不该和玩家同一火力基线。
        var gruntClub = new CharacterWeaponSpec("club_goblin", 3001,
            damage: 6f, bulletSpeed: 0f, rangeMeters: 1.6f, spreadDegrees: 0f,
            criticalChance: 0f, criticalMultiplier: 1f, projectileRadius: 0.16f,
            shotCooldown: 0.9f);                                              // arch-ok: 镜像 characters.xlsx/Weapons
        var archerBow = new CharacterWeaponSpec("bow_goblin", 3002,
            damage: 9f, bulletSpeed: 16f, rangeMeters: 14f, spreadDegrees: 3f,
            criticalChance: 0.05f, criticalMultiplier: 1.5f, projectileRadius: 0.09f,
            shotCooldown: 1.4f);                                              // arch-ok: 镜像 characters.xlsx/Weapons
        var ogreMaul = new CharacterWeaponSpec("maul_ogre", 3003,
            damage: 22f, bulletSpeed: 0f, rangeMeters: 2.2f, spreadDegrees: 0f,
            criticalChance: 0f, criticalMultiplier: 1f, projectileRadius: 0.4f,
            shotCooldown: 1.8f);                                              // arch-ok: 镜像 characters.xlsx/Weapons
        var slimeSpit = new CharacterWeaponSpec("spit_slime", 3004,
            damage: 5f, bulletSpeed: 11f, rangeMeters: 9f, spreadDegrees: 6f,
            criticalChance: 0f, criticalMultiplier: 1f, projectileRadius: 0.13f,
            shotCooldown: 1.6f);                                              // arch-ok: 镜像 characters.xlsx/Weapons
        var slimeBomb = new CharacterWeaponSpec("bomb_slime", 3005,
            damage: 14f, bulletSpeed: 9f, rangeMeters: 8f, spreadDegrees: 0f,
            criticalChance: 0f, criticalMultiplier: 1f, projectileRadius: 0.22f,
            shotCooldown: 2.2f);                                              // arch-ok: 镜像 characters.xlsx/Weapons

        Assemblies.RegisterWeapon(gruntClub);
        Assemblies.RegisterWeapon(archerBow);
        Assemblies.RegisterWeapon(ogreMaul);
        Assemblies.RegisterWeapon(slimeSpit);
        Assemblies.RegisterWeapon(slimeBomb);

        // ── 技能包：怪物同样"左键 = 开火" ──
        // skill id 复用既有号段（1102 近战 / 1103 弓射 / 1104 重砸 / 1105 法弹），
        // 所以这里不能用 RegisterMonsterKit 的默认 projectileId 推导，必须显式传。
        RegisterMonsterKit("goblin_kit", 1102, gruntClub, string.Empty);          // 近战：无抛射物
        RegisterMonsterKit("goblin_archer_kit", 1103, archerBow, "proj_arrow_goblin");
        RegisterMonsterKit("ogre_kit", 1104, ogreMaul, string.Empty);             // 近战：无抛射物
        RegisterMonsterKit("slime_kit", 1102, slimeSpit, "proj_spit_slime");
        RegisterMonsterKit("slime_bomb_kit", 1104, slimeBomb, "proj_bomb_slime");

        // ── 定义 id → 武器行 ──
        // 怪物没有 VariantAssembly 行（那是人物专用的"装配清单"），所以与武器的关联在这里显式登记。
        // 统一喂进 _actorWeapons，让「定义 → 武器」对人物和怪物只留一条查询路径
        // （WeaponOfActor），否则宿主得为两类角色各写一套分支。
        _actorWeapons["goblin_grunt"] = gruntClub;
        _actorWeapons["goblin_archer"] = archerBow;
        _actorWeapons["ogre_brute"] = ogreMaul;
        _actorWeapons["slime_401"] = slimeSpit;
        _actorWeapons["slime_403"] = slimeBomb;
    }

    /// <summary>
    /// 「角色定义 id → 武器行」的唯一查询入口（人物查变体清单、怪物查显式登记表）。
    /// <para>
    /// <b>为什么要有它</b>：宿主的 <c>IWeaponStatsSource</c> 早先只认
    /// <c>Assemblies.Find(variantId).Weapon</c>，而怪物没有变体行 —— 于是怪物装配时
    /// <c>Get()</c> 直接抛异常（或静默没枪）。把两条来源收进一个方法后，
    /// 宿主只需调用它一次，新增一类怪物只改数据（规则7）。
    /// </para>
    /// </summary>
    /// <param name="definitionId">角色定义 id，如 <c>goblin_archer</c> / <c>test_hero</c>。</param>
    /// <returns>武器行；该角色没配武器（或不认识这个 id）返回 <c>null</c>。</returns>
    public CharacterWeaponSpec? WeaponOfActor(string definitionId)
    {
        if (string.IsNullOrEmpty(definitionId))
        {
            return null;
        }

        // ① 人物/变体：装配清单是权威（WeaponRowId 已在解析期校验过存在性）。
        CharacterAssemblyOf<SkillLoadout>? assembly = Assemblies.Find(definitionId);
        if (assembly?.Weapon is not null)
        {
            return assembly.Weapon;
        }

        // ② 怪物等无变体的角色：查显式登记表。
        return _actorWeapons.TryGetValue(definitionId, out CharacterWeaponSpec? weapon) ? weapon : null;
    }

    /// <summary>
    /// 登记一个"怪物会开火"的技能包：左键槽 = <see cref="CharacterSkillSpec.KindFireProjectile"/>。
    /// <para>
    /// 与 <see cref="RegisterHeroKit"/> 的差别只有两点：① 怪物不占冲刺/独有技槽，
    /// 只出左键一槽；② 冷却直接取武器行（同一"单一真源"约定），不再另填。
    /// 其余完全同构 —— 这就是"玩家怪物同源"在技能层的落点。
    /// </para>
    /// </summary>
    /// <param name="loadoutId">技能包 id（= 角色定义的 <c>SkillLoadoutId</c>）。</param>
    /// <param name="attackSkillId">左键技能 id。</param>
    /// <param name="weapon">武器行（决定伤害/冷却/射程）。</param>
    /// <param name="projectileId">子弹外观 id；空串 = 近战，不发射抛射物。</param>
    private void RegisterMonsterKit(
        string loadoutId, int attackSkillId, CharacterWeaponSpec weapon, string projectileId)
    {
        var row = new CharacterSkillSpec(attackSkillId, CharacterSkillSpec.KindFireProjectile,
            weapon.Id, projectileId,
            weapon.ShotCooldown, 0f, CharacterSkillSpecs.ToSlotNumber(SkillSlot.PrimaryAttack));

        // ① 技能行进字典 —— 覆盖 BuildMonsterKits 里那条 KindData 占位行。
        _skillRows[attackSkillId] = row;
        RecordLoadoutSlot(loadoutId, row.Slot, attackSkillId);

        if (row.SuspectsWeaponProjectileConflated)
        {
            _warnings.Add(string.Concat(
                "技能包 '", loadoutId, "' 的 WeaponId 与 ProjectileId 相同（都是 '",
                row.WeaponId, "'）：武器 id 不应兼作子弹外观 id。"));
        }

        // ② 技能包重建：用真行原地替换掉 RegisterLoadout 建的 KindData 占位包
        //    （Replace 而非重注册 —— 槽位号不变，已生成角色的 LoadoutSlot 继续有效）。
        var skills = new ISkill[]
        {
            new DataSkill(row.SkillId, row.ProjectileId, SkillSlot.PrimaryAttack,
                row.CooldownSeconds, row.ManaCost),
        };
        Loadouts.Replace(loadoutId, SkillLoadout.FromSkills(loadoutId, skills));
    }

    /// <summary>记录「技能包 + 槽位号 → SkillId」（Register* 系列统一从这里维护索引）。</summary>
    private void RecordLoadoutSlot(string loadoutId, int slotNumber, int skillId)
    {
        if (slotNumber < CharacterSkillSpecs.MinSlotNumber || slotNumber > CharacterSkillSpecs.MaxSlotNumber)
        {
            return;
        }
        if (!_loadoutSlots.TryGetValue(loadoutId, out int[]? table))
        {
            table = new int[CharacterSkillSpecs.SlotCount];
            _loadoutSlots[loadoutId] = table;
        }
        table[slotNumber - CharacterSkillSpecs.MinSlotNumber] = skillId;
    }

    /// <summary>
    /// 按「技能包 + 槽位号」查一行技能数据（**宿主 <c>ISkillFactory</c> 的主入口**）。
    /// <para>
    /// 数据层的契约 = 纯数据行：调用方拿到 <see cref="CharacterSkillSpec"/> 后，
    /// 自己按 <c>Kind</c> / <c>WeaponId</c> / <c>ProjectileId</c> / 冷却 / 消耗决定 new 哪个
    /// <see cref="ISkill"/> 并塞进 <see cref="SkillLoadout"/>，数据层不参与构造。
    /// </para>
    /// <para>
    /// <b>为什么不抛异常</b>：内容是策划改的，缺一行/填错槽位是很常见的编辑态错误；
    /// 返回 <c>null</c> 让宿主决定是兜底还是报错，比在数据层里炸掉更好定位。
    /// </para>
    /// </summary>
    /// <param name="loadoutId">技能包 id，如 <c>test_hero_kit</c>。</param>
    /// <param name="slotNumber">槽位号 1..5（1=Dash 2=PrimaryAttack 3=WeaponSwap 4=Signature 5=Interact）。</param>
    /// <returns>技能行；技能包不存在 / 槽位越界 / 该槽留空（SkillId=0）都返回 <c>null</c>。</returns>
    public CharacterSkillSpec? SkillRowOf(string loadoutId, int slotNumber)
    {
        if (string.IsNullOrEmpty(loadoutId)
            || slotNumber < CharacterSkillSpecs.MinSlotNumber
            || slotNumber > CharacterSkillSpecs.MaxSlotNumber
            || !_loadoutSlots.TryGetValue(loadoutId, out int[]? table))
        {
            return null;
        }

        int skillId = table[slotNumber - CharacterSkillSpecs.MinSlotNumber];
        return skillId != 0 && _skillRows.TryGetValue(skillId, out CharacterSkillSpec row) ? row : null;
    }

    /// <summary>
    /// 按技能包取全部技能 id（**按槽位顺序 1..5**，留空槽为 0）。
    /// <para>
    /// 与 <see cref="SkillRowOf"/> 互补：这个给"一次性看整个包"的调用方（校验、调试面板、测试），
    /// 那个给"逐槽组装"的宿主工厂。不存在的技能包返回空数组而不是抛异常。
    /// </para>
    /// </summary>
    public IReadOnlyList<int> SkillIdsOf(string loadoutId)
    {
        if (!string.IsNullOrEmpty(loadoutId) && _loadoutSlots.TryGetValue(loadoutId, out int[]? table))
        {
            return table;
        }
        return Array.Empty<int>();
    }

    /// <summary>技能包 id → 5 个槽位的 SkillId（索引 = 槽位号 - 1；0 = 该槽留空）。</summary>
    private readonly Dictionary<string, int[]> _loadoutSlots = new(StringComparer.Ordinal);

    /// <summary>角色定义 id → 武器行（只收「没有变体行」的角色，如怪物；人物的武器在装配清单里）。</summary>
    private readonly Dictionary<string, CharacterWeaponSpec> _actorWeapons = new(StringComparer.Ordinal);
}

/// <summary>
/// 数据层自有的技能占位实现：只携带 id / 槽位 / 冷却 / 消耗，施放不产出任何东西。
/// <para>
/// <b>为什么数据层要自带一个 <see cref="ISkill"/></b>：数据层不许 <c>using Game.Skill</c>（规则5、规则7）。
/// 但"技能包能被组合、冷却与资源能被门控"这条能力属于数据层自己的验收范围，
/// 需要一个不依赖业务层的实现来跑通门控，所以在数据层内提供一个最小行投影。
/// 真正会开火的技能由宿主 <c>ISkillFactory</c> 按 <c>Kind</c> 实例化，与本类无关。
/// </para>
/// <para><u>重要</u>：本类的 <c>Cast</c> 永远返回成功但**不产生任何效果**，所以"能倒计时"≠"能开火"。</para>
/// </summary>
public sealed class DataSkill : ISkill
{
    /// <summary>构造一行技能占位。</summary>
    public DataSkill(int id, string defId, SkillSlot slot, float cooldownSeconds, float manaCost)
    {
        Id = id;
        DefId = defId ?? string.Empty;
        DefaultSlot = slot;
        CooldownSeconds = cooldownSeconds;
        ManaCost = manaCost;
    }

    /// <inheritdoc />
    public int Id { get; }

    /// <inheritdoc />
    public string DefId { get; }

    /// <inheritdoc />
    public SkillSlot DefaultSlot { get; }

    /// <inheritdoc />
    public float CooldownSeconds { get; }

    /// <inheritdoc />
    public float ManaCost { get; }

    /// <inheritdoc />
    public bool CanCast(in SkillContext ctx) => ctx.IsOffCooldown && ctx.HasMana(ManaCost);

    /// <inheritdoc />
    public SkillCastResult Cast(in SkillContext ctx) => SkillCastResult.Ok();

    /// <inheritdoc />
    public void Tick(float deltaTime, in SkillContext ctx)
    {
    }
}
