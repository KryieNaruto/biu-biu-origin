using System;
using System.Collections.Generic;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;
using Game.Data;
using Game.Skill;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Characters
{
    /// <summary>
    /// 内容一致性 / 回归用例组：守护「内置副本自身是自洽的」。
    /// <para>
    /// <b>这组用例能证明什么、不能证明什么</b>：
    /// </para>
    /// <list type="bullet">
    /// <item>能证明：<u>内置副本</u>是自洽的 —— 每个角色都能真的装配出实体、每个数值包都能被解析；</item>
    /// <item><b>不能证明</b>：内置副本与 <c>characters.xlsx</c> 一致。本组用例完全不读 xlsx，
    /// 它比较的是 C# 常量与 C# 常量。</item>
    /// </list>
    /// <para>
    /// <b>「表 = 运行时」由 <see cref="CharacterXlsxParityTests"/> 守护</b>：那组用例打开真实
    /// <c>data/excel/characters.xlsx</c> 逐字段比对，并已被注入式验证过 ——
    /// 把表里某个数值改掉，它会精确报出「预期 A、实际 B」。
    /// 本组用例保留是因为它守护的是另一件事（装配自洽），两者互补而非重复。
    /// </para>
    /// <para>
    /// 等 <c>data/generated/</c> pipeline 落地后，这些用例改成读生成物即可，断言形状不动。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class CharacterContentRegressionTests
    {
        private static readonly CharacterContentSet Content = new CharacterContentSet().BuildBuiltinV1();

        [TestMethod]
        public void BuiltinContent_HasEveryCharacterFromDesignTable()
        {
            // 期望值镜像 data/excel/characters.xlsx 的 Characters 表；但**本用例不读 xlsx**，
            // 真正的「表 = 运行时」比对在 CharacterXlsxParityTests 里（它直接解析真表）。
            string[] expected =
            {
                "player_hero", "goblin_grunt", "goblin_archer", "ogre_brute", "chosen_hero",
            };

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.IsTrue(Content.Definitions.Find(expected[i]) is not null, $"角色表里的 {expected[i]} 必须能查到");
            }
        }

        [TestMethod]
        public void BuiltinContent_EveryCharacterCanActuallyBeAssembled()
        {
            // 只"注册成功"不算数 —— 必须每个角色都能真的装配出实体，
            // 否则策划新加一行数据、把包名拼错，只有进游戏才会炸。
            string[] ids =
            {
                "player_hero", "goblin_grunt", "goblin_archer", "ogre_brute", "chosen_hero",
            };

            for (int i = 0; i < ids.Length; i++)
            {
                var world = new World();
                var pipeline = new SystemPipeline(world);
                var queue = new ActorSpawnQueue();
                var slots = new RepositoryPackageSlotResolver(Content.StatPacks);
                int slot = Content.Definitions.SlotOf(ids[i]);
                Assert.IsTrue(slot >= 0, $"{ids[i]} 应当有定义槽位");

                pipeline.Add(new ActorAssemblySystem(Content.Definitions, Content.Loadouts, Content.StatPacks, slots, queue), 0);

                queue.Enqueue(new ActorSpawnRequest(slot, System.Numerics.Vector3.Zero));
                pipeline.Execute(0f);

                var asm = (ActorAssemblySystem)pipeline.SystemAt(0);
                Assert.AreEqual(0, asm.Skipped,
                    $"{ids[i]} 装配不应被跳过（缺 Base 包是最常见的数据错误）");
                Assert.AreEqual(1, asm.Assembled, $"{ids[i]} 应当装配出实体");
            }
        }

        [TestMethod]
        public void BuiltinContent_EveryCharacterHasPositiveHpAndArmorSlots()
        {
            // 需求①：血量/法力/护甲三个基础数值必须在每个角色的 Base 包里存在。
            string[] ids =
            {
                "player_hero", "goblin_grunt", "goblin_archer", "ogre_brute", "chosen_hero",
            };

            for (int i = 0; i < ids.Length; i++)
            {
                ActorDefinition def = Content.Definitions.Get(Content.Definitions.SlotOf(ids[i]))!;
                IReadOnlyStatView view = Content.StatPacks.Compose(def.StatPackageIds);

                Assert.IsTrue(view.Get(StatId.HpMax) > 0f, $"{ids[i]} 必须有正的血量上限");
                Assert.IsTrue(view.Has(StatId.Armor), $"{ids[i]} 必须有护甲");
                Assert.IsTrue(view.Has(StatId.ManaMax), $"{ids[i]} 必须有法力上限（可以是 0）");
            }
        }

        [TestMethod]
        public void BuiltinContent_PlayerKitCoversAllFiveFixedSlots()
        {
            // 需求③：五个固定槽位（冲刺/射击/切武器/独有技能/交互）必须全配满。
            Assert.IsTrue(Content.Loadouts.Find("player_kit") is not null, "玩家技能包必须存在");

            SkillLoadout kit = Content.Loadouts.Get(Content.Loadouts.SlotOf("player_kit"))!;
            SkillSlot[] slots =
            {
                SkillSlot.Dash, SkillSlot.PrimaryAttack, SkillSlot.WeaponSwap, SkillSlot.Signature, SkillSlot.Interact,
            };

            for (int i = 0; i < slots.Length; i++)
            {
                Assert.IsTrue(kit.Get(slots[i]) is not null, $"玩家技能包的 {slots[i]} 槽必须装有技能");
            }
        }

        [TestMethod]
        public void BuiltinContent_MonsterAndPlayerShareLoadoutMechanism()
        {
            // 需求⑤：怪物与角色同源 —— 两者的技能包都是同一种对象，只是内容不同。
            Assert.IsTrue(Content.Loadouts.Find("goblin_kit") is not null, "怪物也要有技能包");
            Assert.IsTrue(Content.Loadouts.Find("player_kit") is not null);

            SkillLoadout monster = Content.Loadouts.Get(Content.Loadouts.SlotOf("goblin_kit"))!;
            Assert.IsTrue(monster.Get(SkillSlot.PrimaryAttack) is not null, "哥布林至少会普通攻击");
            Assert.IsNull(monster.Get(SkillSlot.Dash), "哥布林没有冲刺 —— 差异只来自数据，不来自类型");
        }

        [TestMethod]
        public void BuiltinContent_SecondPlayerCharacterExistsToProveDataOnlyDifference()
        {
            // 「换一个玩家角色」= 换数值包 + 换技能包，零代码改动。
            ActorDefinition hero = Content.Definitions.Get(Content.Definitions.SlotOf("player_hero"))!;
            ActorDefinition chosen = Content.Definitions.Get(Content.Definitions.SlotOf("chosen_hero"))!;

            Assert.AreEqual(ActorKind.Player, hero.Kind);
            Assert.AreEqual(ActorKind.Player, chosen.Kind);
            Assert.AreEqual(hero.DefaultTeam, chosen.DefaultTeam);
            Assert.AreNotEqual(hero.StatPackageIds, chosen.StatPackageIds, "两个角色的差异应当完全由数值包表达");
            Assert.AreNotEqual(hero.SkillLoadoutId, chosen.SkillLoadoutId, "技能包也应不同");
        }

        [TestMethod]
        public void BuiltinContent_CannotBeBuiltTwice()
        {
            // 重复构建会让槽位重排、让已有实体句柄指错人 —— 必须响亮失败而不是静默。
            var once = new CharacterContentSet().BuildBuiltinV1();
            Assert.IsTrue(once.IsBuilt);

            bool threw = false;
            try
            {
                once.BuildBuiltinV1();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.IsTrue(threw, "重复构建必须抛异常");
        }

        [TestMethod]
        public void ContentSet_FeedsTheRealAssemblyPath_EndToEnd()
        {
            // 端到端：从"内容集"直接进装配管线，产出的实体血量 = 表里的值。
            var world = new World();
            var pipeline = new SystemPipeline(world);
            var queue = new ActorSpawnQueue();
            var slots = new RepositoryPackageSlotResolver(Content.StatPacks);
            var assembly = new ActorAssemblySystem(Content.Definitions, Content.Loadouts, Content.StatPacks, slots, queue);
            pipeline.Add(assembly, 0);

            ActorDefinition hero = Content.Definitions.Get(Content.Definitions.SlotOf("player_hero"))!;
            queue.Enqueue(new ActorSpawnRequest(Content.Definitions.SlotOf("player_hero"), System.Numerics.Vector3.Zero));
            pipeline.Execute(0f);

            Assert.AreEqual(1, assembly.Assembled, "应当装配出一个玩家");
            Entity entity = assembly.LastAssembled[0];

            Assert.IsTrue(world.TryGet(entity, out Health health), "装配出的实体必须有 Health");
            // player_base.hp = 120，成长层 +10 → 130（规则6：Base 绝对值 + 差量叠加）
            Assert.AreEqual(130f, health.Current, "HP 来自数值包（Base 120 + 成长层 10）");
            Assert.IsTrue(world.TryGet(entity, out Mana mana), "装配出的实体必须有 Mana");
            Assert.AreEqual(80f, mana.Max, "法力上限来自数值包");
        }
    }
}
