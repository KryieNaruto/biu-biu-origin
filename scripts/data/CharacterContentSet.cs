using System;
using Game.Actor;
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

    /// <summary>是否已经注册过（防止重复注册导致槽位重排）。</summary>
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
    /// </para>
    /// </summary>
    public CharacterContentSet BuildBuiltinV1()
    {
        if (IsBuilt)
        {
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
        });

        // 技能槽位的数据化装配：v1 先用「占位技能」表达"哪个槽装了哪个技能 id + 冷却/消耗"，
        // 真正的技能行为在 scripts/skill/ 里逐步落地（每个技能 = 一个 ISkill 实现）。
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

        IsBuilt = true;
        return this;
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

    private void RegisterLoadout(string loadoutId, (SkillSlot Slot, int SkillId, float Cooldown, float Mana)[] entries)
    {
        var skills = new ISkill[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            skills[i] = new DataSkill(
                entries[i].SkillId, string.Concat("skill_", entries[i].SkillId.ToString()),
                entries[i].Slot, entries[i].Cooldown, entries[i].Mana);
        }
        Loadouts.Register(SkillLoadout.FromSkills(loadoutId, skills));
    }
}

/// <summary>
/// 数据驱动的技能**占位实现**：只有 id / 槽位 / 冷却 / 消耗，没有具体行为（施放时什么也不产出）。
/// <para>
/// 为什么需要它：角色抽象这一层的验收点是"技能包能被组合、冷却与资源能被门控"，
/// 具体技能行为（射击/冲刺/切武器/交互）属于后续战斗任务。用它可以在技能逻辑落地前
/// 就把数据管线、槽位、冷却、法力全部跑通，且新增真实技能时只需替换这个类（规则7）。
/// </para>
/// <para>注意：它<u>不会</u>生成任何东西 —— 施放成功但不产生效果，是有意为之，避免测试误以为"技能已实现"。</para>
/// </summary>
public sealed class DataSkill : ISkill
{
    /// <summary>构造。</summary>
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
