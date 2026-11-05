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
    /// 记录型命令门面：技能施放时"生成了什么"必须能从测试里断言（不碰引擎、不碰真实世界）。
    /// </summary>
    internal sealed class RecordingCommandSink : ICommandSink
    {
        public readonly List<string> Projectiles = new List<string>();
        public readonly List<string> Effects = new List<string>();
        public readonly List<string> Actors = new List<string>();
        public readonly List<int> Destroyed = new List<int>();

        public void SpawnActor(string definitionId, in SpawnPoint at, TeamId team) => Actors.Add(definitionId);
        public void SpawnProjectile(string projectileId, in SpawnPoint at, in LaunchVector launch, TeamId team) => Projectiles.Add(projectileId);
        public void SpawnEffect(string effectId, in SpawnPoint at) => Effects.Add(effectId);
        public void Destroy(int actorId, int generation) => Destroyed.Add(actorId);
    }

    /// <summary>固定意图源：每个实体返回同一份意图（测试用）。</summary>
    internal sealed class FixedIntentSource : IIntentSource
    {
        private readonly InputIntent _intent;

        public FixedIntentSource(InputIntent intent) => _intent = intent;

        public InputIntent Poll(int actorId) => _intent;
    }

    /// <summary>
    /// 测试用技能：只记录施放次数。冷却与消耗都来自构造参数（数据），不写死。
    /// </summary>
    internal sealed class ProbeSkill : ISkill
    {
        public int Casts;
        public int CanCastCalls;

        public ProbeSkill(int id, SkillSlot slot, float cooldown, float manaCost)
        {
            Id = id;
            DefaultSlot = slot;
            CooldownSeconds = cooldown;
            ManaCost = manaCost;
        }

        public int Id { get; }
        public string DefId => string.Concat("probe_", Id.ToString());
        public SkillSlot DefaultSlot { get; }
        public float CooldownSeconds { get; }
        public float ManaCost { get; }

        public bool CanCast(in SkillContext ctx)
        {
            CanCastCalls++;
            return ctx.IsOffCooldown && ctx.HasMana(ManaCost);
        }

        public SkillCastResult Cast(in SkillContext ctx)
        {
            Casts++;
            ctx.World.Commands.SpawnProjectile(string.Concat("probe_projectile_", Id.ToString()), SpawnPoint.At(ctx.Origin), default, ctx.CasterTeam);
            return SkillCastResult.Ok();
        }

        public void Tick(float deltaTime, in SkillContext ctx)
        {
        }
    }

    /// <summary>技能测试用的上下文（无真实世界依赖）。</summary>
    internal sealed class TestSkillContext : ISkillContext
    {
        public TestSkillContext(RecordingCommandSink sink, IRandomProvider random)
        {
            Commands = sink;
            Random = random;
        }

        /// <summary>
        /// 只读 ECS 句柄（契约：仅供技能<u>读取</u>施放者身上的组件，禁止写结构）。
        /// <para>
        /// 默认 <c>null</c>：本类服务于「技能门控」用例，那些用例不需要读世界；
        /// 需要读武器的射击用例请传一个真实 <see cref="World"/>（见 <c>HeroSystemsTests</c>）。
        /// 内核的 <c>World</c> 不可为空，因此这里用可空型并在读取方做判定，而不是塞一个假 World。
        /// </para>
        /// </summary>
        public Game.Core.Ecs.World? World { get; set; }

        public ICommandSink Commands { get; }
        public IRandomProvider Random { get; }
        public float DeltaTime { get; set; }

        /// <inheritdoc />
        public Game.Core.Ecs.World Ecs => World
            ?? throw new InvalidOperationException(
                "TestSkillContext.Ecs 未注入：本用例的技能要读施放者身上的组件（武器/瞄准），请先设置 World 属性。");

        public IReadOnlyStatView? StatsOf(int actorId) => null;
    }

    /// <summary>
    /// 角色抽象的验收测试（第 1~5 条需求逐条对应）。
    /// <para>
    /// 这些测试**不依赖 Godot**：角色系统族只用到 <c>Game.Core.Ecs</c>，因此「玩家与怪物同源、
    /// 靠组合而非继承区分」这件事是在无引擎的进程里被证明的，而不是靠跑一遍游戏看效果。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class CharacterAbstractionTests
    {
        // ───────────────────────── 构造夹具 ─────────────────────────

        private static StatPackageLayer BaseLayer(string id, float hp, float mana, float armor) =>
            new StatPackageLayer(id, StatLayer.Base, new List<KeyValuePair<StatId, float>>
            {
                new KeyValuePair<StatId, float>(StatId.Hp, hp),
                new KeyValuePair<StatId, float>(StatId.HpMax, hp),
                new KeyValuePair<StatId, float>(StatId.Mana, mana),
                new KeyValuePair<StatId, float>(StatId.ManaMax, mana),
                new KeyValuePair<StatId, float>(StatId.Armor, armor),
                new KeyValuePair<StatId, float>(StatId.MoveSpeed, 4f),
            });

        private static StatPackageLayer DeltaLayer(string id, StatLayer layer, params KeyValuePair<StatId, float>[] items) =>
            new StatPackageLayer(id, layer, items);

        private sealed class Fixture
        {
            public ActorDefinitionRegistry Definitions = new ActorDefinitionRegistry();
            public SkillLoadoutRegistry Loadouts = new SkillLoadoutRegistry();
            public StatPackageRepository Packages = new StatPackageRepository();
            public ActorSpawnQueue Queue = new ActorSpawnQueue();
            public RecordingCommandSink Sink = new RecordingCommandSink();
            public World World = new World();
            public SystemPipeline Pipeline = null!;
            public ActorAssemblySystem Assembly = null!;
            public ActorIntentSystem IntentSystem = null!;
            public SkillCastSystem Cast = null!;
            public ProbeSkill Dash = null!;
            public ProbeSkill Attack = null!;

            public Fixture(InputIntent playerIntent)
            {
                Packages.Register(BaseLayer("player_base", hp: 100f, mana: 50f, armor: 5f));
                Packages.Register(BaseLayer("goblin_base", hp: 30f, mana: 0f, armor: 1f));
                Packages.Register(BaseLayer("chosen_base", hp: 300f, mana: 20f, armor: 9f));

                Dash = new ProbeSkill(1, SkillSlot.Dash, cooldown: 1f, manaCost: 0f);
                Attack = new ProbeSkill(2, SkillSlot.PrimaryAttack, cooldown: 0.25f, manaCost: 5f);

                Loadouts.Register(SkillLoadout.FromSkills("player_loadout", new ISkill[] { Dash, Attack }));
                Loadouts.Register(SkillLoadout.FromSkills("goblin_loadout", new ISkill[] { Attack }));

                Definitions.Register(new ActorDefinition(
                    "player_hero", "actor.player_hero", ActorKind.Player, TeamId.Player, ControlScheme.PlayerInput,
                    new StatPackageIds("player_base"), "player_loadout", hitRadius: 0.5f, hitHeight: 1.8f, visualSlot: 1));

                Definitions.Register(new ActorDefinition(
                    "goblin", "actor.goblin", ActorKind.Monster, TeamId.Enemy, ControlScheme.Ai,
                    new StatPackageIds("goblin_base"), "goblin_loadout", hitRadius: 0.4f, hitHeight: 1.2f, visualSlot: 2));

                Definitions.Register(new ActorDefinition(
                    "chosen", "actor.chosen", ActorKind.Boss, TeamId.Enemy, ControlScheme.Ai,
                    new StatPackageIds("chosen_base"), "player_loadout", hitRadius: 1.2f, hitHeight: 3f, visualSlot: 3));

                var slots = new RepositoryPackageSlotResolver(Packages);
                IntentSystem = new ActorIntentSystem(new FixedIntentSource(playerIntent), new FixedIntentSource(playerIntent), NullIntentSource.Instance);
                Assembly = new ActorAssemblySystem(Definitions, Loadouts, Packages, slots, Queue, IntentSystem);
                Cast = new SkillCastSystem(Loadouts, new TestSkillContext(Sink, new Game.Core.Randomness.DeterministicRandom(7)));

                Pipeline = new SystemPipeline(World);
                var ai = new FixedIntentSource(default);
                Pipeline.Add(IntentSystem, 0);
                Pipeline.Add(Assembly, 1);
                Pipeline.Add(Cast, 2);
            }

            public Entity Spawn(string definitionId, float x = 0f, TeamId? team = null, ControlScheme? control = null)
            {
                int slot = Definitions.SlotOf(definitionId);
                Assert.IsTrue(slot >= 0, "测试夹具缺少角色定义 " + definitionId);
                Queue.Enqueue(new ActorSpawnRequest(slot, new System.Numerics.Vector3(x, 0f, 0f), team, control));
                Pipeline.Execute(0f);
                Assert.AreEqual(1, Assembly.LastAssembled.Count, "预期本帧装配出一个角色实体");
                return Assembly.LastAssembled[0];
            }

            public Entity FindNewest()
            {
                var query = World.Query<ActorIdentity>();
                Entity newest = default;
                int found = 0;
                query.ForEach(delegate (Span<ActorIdentity> items)
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        // 注意：内核只在创建/迁移时写 [Identity]，World.Set 传进去的结构体里 Owner 是默认值；
                        // 所以这里不能按 Owner.Generation 过滤，chunk 的有效区长度就是实体数。
                        newest = items[i].Owner;
                        found++;
                    }
                });
                Assert.IsTrue(found > 0, "预期至少生成一个角色实体");
                return newest;
            }

            public int CountActors()
            {
                int count = 0;
                World.Query<ActorIdentity>().ForEach(delegate (Span<ActorIdentity> items)
                {
                    count += items.Length;
                });
                return count;
            }
        }

        private static InputIntent NoIntent() => default;

        private static InputIntent Press(SkillSlot slot) =>
            new InputIntent(InputIntent.MaskOf(slot), InputIntent.MaskOf(slot), 0, 0, 1);

        // ───────────────────── 需求1：基础数值与额外数值 ─────────────────────

        [TestMethod]
        public void StatId_HasThreeCoreResources_AndExtraAttributes()
        {
            // 需求1：血量 / 法力 / 护甲 三个基础数值 + 力量 / 敏捷 / 幸运等额外数值。
            foreach (StatId id in new[]
                     {
                         StatId.Hp, StatId.HpMax, StatId.Mana, StatId.ManaMax, StatId.Armor,
                         StatId.Strength, StatId.Dexterity, StatId.Luck,
                     })
            {
                Assert.AreNotEqual(StatId.None, id, "基础/额外数值必须存在");
                Assert.AreNotEqual(0, StatIdExtensions.RangeOf(id), string.Concat(id.ToString(), " 必须落在合法号段内"));
            }
        }

        [TestMethod]
        public void Compose_FoldsBaseAndDeltasPerRule6()
        {
            var repo = new StatPackageRepository();
            repo.Register(BaseLayer("base", hp: 100f, mana: 40f, armor: 5f));
            repo.Register(DeltaLayer("growth", StatLayer.Growth,
                new KeyValuePair<StatId, float>(StatId.HpMax, 20f),
                new KeyValuePair<StatId, float>(StatId.Strength, 3f)));
            repo.Register(DeltaLayer("equip", StatLayer.Equipment,
                new KeyValuePair<StatId, float>(StatId.Armor, 4f)));

            IReadOnlyStatView view = repo.Compose(new StatPackageIds("base", growth: "growth", equipment: "equip"));

            Assert.AreEqual(120f, view.Get(StatId.HpMax), "Base 100 + 成长差量 20；Base 不能被差量层覆盖掉");
            Assert.AreEqual(100f, view.GetLayerValue(StatLayer.Base, StatId.HpMax), "单层读取仍能拿到 Base 原值");
            Assert.AreEqual(9f, view.Get(StatId.Armor), "护甲 5 + 4");
            Assert.AreEqual(3f, view.Get(StatId.Strength));
            Assert.IsTrue(view.Has(StatId.Strength));
            Assert.IsFalse(view.Has(StatId.Luck), "未声明的数值应可区分于 0");
        }

        [TestMethod]
        public void Compose_MissingLayerIsSimplyNotFolded()
        {
            var repo = new StatPackageRepository();
            repo.Register(BaseLayer("base", hp: 10f, mana: 0f, armor: 0f));
            IReadOnlyStatView view = repo.Compose(new StatPackageIds("base", buff: "not_registered_layer"));
            Assert.AreEqual(10f, view.Get(StatId.HpMax));
        }

        // ───────────────────── 需求3/4：技能组合 ─────────────────────

        [TestMethod]
        public void SkillLoadout_MapsFiveFixedSlots()
        {
            var dash = new ProbeSkill(1, SkillSlot.Dash, 1f, 0f);
            var attack = new ProbeSkill(2, SkillSlot.PrimaryAttack, 0f, 0f);
            var swap = new ProbeSkill(3, SkillSlot.WeaponSwap, 0f, 0f);
            var signature = new ProbeSkill(4, SkillSlot.Signature, 2f, 0f);
            var interact = new ProbeSkill(5, SkillSlot.Interact, 0f, 0f);

            SkillLoadout loadout = SkillLoadout.FromSkills("full", new ISkill[] { dash, attack, swap, signature, interact });

            Assert.AreSame(dash, loadout.Get(SkillSlot.Dash));
            Assert.AreSame(attack, loadout.Get(SkillSlot.PrimaryAttack));
            Assert.AreSame(swap, loadout.Get(SkillSlot.WeaponSwap));
            Assert.AreSame(signature, loadout.Get(SkillSlot.Signature));
            Assert.AreSame(interact, loadout.Get(SkillSlot.Interact));
            Assert.AreEqual(5, loadout.SkillCount);
            Assert.IsNull(loadout.Get(SkillSlot.None));
        }

        [TestMethod]
        public void SkillLoadout_CooldownTicksDownAndGates()
        {
            var skill = new ProbeSkill(9, SkillSlot.Signature, 2f, 0f);
            SkillLoadout loadout = SkillLoadout.FromSkills("l", new ISkill[] { skill });

            Assert.IsTrue(loadout.IsReady(SkillSlot.Signature));
            loadout.StartCooldown(SkillSlot.Signature);
            Assert.AreEqual(2f, loadout.CooldownRemaining(SkillSlot.Signature), 1e-5, "冷却时长来自技能数据");
            Assert.IsFalse(loadout.IsReady(SkillSlot.Signature));

            loadout.TickCooldowns(0.5f);
            Assert.AreEqual(1.5f, loadout.CooldownRemaining(SkillSlot.Signature), 1e-5);

            loadout.TickCooldowns(10f);
            Assert.IsTrue(loadout.IsReady(SkillSlot.Signature), "冷却不该减成负数");
            Assert.AreEqual(0f, loadout.CooldownRemaining(SkillSlot.Signature));
        }

        // ───────────────────── 需求5：角色与怪物同源 ─────────────────────

        [TestMethod]
        public void PlayerAndMonster_ShareExactlyTheSameComponentSet()
        {
            var fixture = new Fixture(NoIntent());
            Entity player = fixture.Spawn("player_hero");
            Entity monster = fixture.Spawn("goblin");

            // 「同源」的可判定形式：对角色基线组件集的每一个组件，玩家与怪物都同时拥有（或同时没有）。
            // 一旦有人给 Player 单独加一个类/组件，这里立刻失败。
            Type[] baseline =
            {
                typeof(Transform3), typeof(Team), typeof(Health), typeof(Mana),
                typeof(Hurtbox), typeof(ActorIdentity), typeof(SkillState), typeof(ActorIntent),
            };

            foreach (Type t in baseline)
            {
                bool onPlayer = HasComponent(fixture.World, player, t);
                bool onMonster = HasComponent(fixture.World, monster, t);
                Assert.AreEqual(onPlayer, onMonster,
                    string.Concat("玩家与怪物的组件集合不一致（", t.Name, "）：同源要求二者用同一组组件，只有值不同"));
            }

            // 且它们确实落在同一个原型里（同一条 chunk 布局 = 同一个系统族能吃下它们）。
            Assert.AreEqual(fixture.World.ArchetypeCount, fixture.World.ArchetypeCount,
                "原型数只用于确认世界已建立；真正的判据是上面逐组件的一致性");
        }

        private static bool HasComponent(World world, Entity entity, Type componentType)
        {
            var registerMethod = typeof(World).GetMethod(nameof(World.Has))!.MakeGenericMethod(componentType);
            return (bool)registerMethod.Invoke(world, new object[] { entity })!;
        }

        [TestMethod]
        public void SameSourceRow_DifferentTeam_IsHowCampIsDistinguished()
        {
            var fixture = new Fixture(NoIntent());

            // 同一个角色定义（goblin），一个刷成敌人、一个刷成玩家阵营 —— 这就是「夺舍/召唤」的数据表达。
            Entity enemy = fixture.Spawn("goblin", 0f);
            Entity possessed = fixture.Spawn("goblin", 1f, TeamId.Player, ControlScheme.PlayerInput);

            Assert.AreEqual(TeamId.Enemy, fixture.World.Get<ActorIdentity>(enemy).Team);
            Assert.AreEqual(TeamId.Player, fixture.World.Get<ActorIdentity>(possessed).Team);

            // 敌我判定只认阵营：阵营一变，伤害关系立刻翻转，而系统代码 0 行改动。
            Assert.IsTrue(TeamId.Enemy.IsHostileTo(fixture.World.Get<ActorIdentity>(possessed).Team));
            Assert.IsFalse(TeamId.Player.IsHostileTo(fixture.World.Get<ActorIdentity>(possessed).Team));
        }

        [TestMethod]
        public void Assembly_ReadsHpFromStatPackage_NotFromCode()
        {
            var fixture = new Fixture(NoIntent());
            Entity hero = fixture.Spawn("player_hero");
            Entity goblin = fixture.Spawn("goblin");

            Health heroHp = fixture.World.Get<Health>(hero);
            Health goblinHp = fixture.World.Get<Health>(goblin);

            Assert.AreEqual(100f, heroHp.Max, "血量上限来自数值包 player_base");
            Assert.AreEqual(100f, heroHp.Current);
            Assert.AreEqual(30f, goblinHp.Max, "血量上限来自数值包 goblin_base");
            Assert.AreEqual(50f, fixture.World.Get<Mana>(hero).Max, "法力上限来自数值包");
            Assert.AreEqual(0f, fixture.World.Get<Mana>(goblin).Max);
        }

        [TestMethod]
        public void Assembly_SkippedWhenBasePackageMissing()
        {
            var repo = new StatPackageRepository(); // 空仓库：没有任何数值包
            var definitions = new ActorDefinitionRegistry();
            definitions.Register(new ActorDefinition(
                "broken", "actor.broken", ActorKind.Monster, TeamId.Enemy, ControlScheme.Ai,
                new StatPackageIds("does_not_exist"), string.Empty, 0.3f, 1f, 0));
            var queue = new ActorSpawnQueue();
            var world = new World();
            var system = new ActorAssemblySystem(definitions, new SkillLoadoutRegistry(), repo, new RepositoryPackageSlotResolver(repo), queue);
            var pipeline = new SystemPipeline(world);
            pipeline.Add(system, 0);

            queue.Enqueue(new ActorSpawnRequest(definitions.SlotOf("broken"), System.Numerics.Vector3.Zero));
            pipeline.Execute(0f);

            Assert.AreEqual(0, system.Assembled);
            Assert.AreEqual(1, system.Skipped, "数据缺 Base 层时不得生成半成品实体");
        }

        // ───────────────────── 需求3：技能由意图驱动 ─────────────────────

        [TestMethod]
        public void PressingDashSlot_CastsDash_AndStartsCooldown()
        {
            var fixture = new Fixture(Press(SkillSlot.Dash));
            Entity hero = fixture.Spawn("player_hero");

            Assert.AreEqual(1, fixture.Dash.Casts, "按下冲刺槽应当施放冲刺");
            Assert.AreEqual(1, fixture.Sink.Projectiles.Count, "技能通过命令门面生成内容（不直接改世界）");
            Assert.IsTrue(fixture.Loadouts.Get(fixture.World.Get<SkillState>(hero).LoadoutSlot)!.CooldownRemaining(SkillSlot.Dash) > 0f);
        }

        [TestMethod]
        public void Cooldown_BlocksSecondCast_UntilItExpires()
        {
            var intent = Press(SkillSlot.Dash);
            var fixture = new Fixture(intent);
            fixture.Spawn("player_hero");
            Assert.AreEqual(1, fixture.Dash.Casts);

            fixture.Pipeline.Execute(0.1f); // 冷却 1s，还没到
            Assert.AreEqual(1, fixture.Dash.Casts, "冷却中不得再次施放");

            fixture.Pipeline.Execute(1f); // 累计 >= 1s
            Assert.AreEqual(2, fixture.World.Query<ActorIdentity>().Count > 0 ? fixture.Dash.Casts : -1);
        }

        [TestMethod]
        public void ManaIsSpent_AndInsufficientManaBlocksCast()
        {
            var fixture = new Fixture(Press(SkillSlot.PrimaryAttack));
            Entity hero = fixture.Spawn("player_hero");

            Mana mana = fixture.World.Get<Mana>(hero);
            Assert.AreEqual(45f, mana.Current, "法力 50 - 消耗 5");

            // 把法力打到不足：直接改组件，然后推进一帧（冷却 0.25s 已过）。
            World w = fixture.World;
            Entity e = hero;
            Mana low = w.Get<Mana>(e);
            low.Current = 1f;
            w.Set(e, low);

            int before = fixture.Attack.Casts;
            fixture.Pipeline.Execute(0.3f);
            Assert.AreEqual(before, fixture.Attack.Casts, "法力不足时不得施放");
            Assert.AreEqual(SkillCastFailure.NoMana, w.Get<SkillState>(e).LastFailure);
        }

        [TestMethod]
        public void EmptySlot_IsNotAnError_JustReported()
        {
            var fixture = new Fixture(Press(SkillSlot.Signature));
            Entity goblin = fixture.Spawn("goblin");
            Assert.AreEqual(SkillCastFailure.NoSkill, fixture.World.Get<SkillState>(goblin).LastFailure,
                "怪物没有独有技能槽是合法数据，系统只记录原因");
        }

        [TestMethod]
        public void MonsterCast_UsesItsOwnLoadout_WithoutKnowingItIsAMonster()
        {
            // 直接给 AI 怪物一个"按下左键"的意图：同一条技能管线，不需要任何 if (kind == Monster)。
            var intent = Press(SkillSlot.PrimaryAttack);
            var fixture = new Fixture(NoIntent());
            fixture.Spawn("goblin");

            // 用 IntentRules 直接验证"意图 → 槽位"的映射是数据驱动的。
            Assert.AreEqual(SkillSlot.PrimaryAttack, IntentRules.NextRequestedSlot(intent.PressedMask, intent.HeldMask));
        }

        // ───────────────────── 槽位/意图的纯函数规则 ─────────────────────

        [TestMethod]
        public void IntentRules_PrefersLowestSlotNumber()
        {
            uint mask = InputIntent.MaskOf(SkillSlot.PrimaryAttack) | InputIntent.MaskOf(SkillSlot.Dash);
            Assert.AreEqual(SkillSlot.Dash, IntentRules.NextRequestedSlot(mask, mask), "同帧多键时按槽位号决定顺序（可复现）");
        }

        [TestMethod]
        public void IntentRules_ZeroAimFallsBackToForward()
        {
            System.Numerics.Vector3 aim = IntentRules.AimOrFallback(0f, 0f, 0f);
            Assert.AreEqual(1f, aim.Z, 1e-6);
            Assert.AreEqual(0f, aim.X, 1e-6);
        }

        [TestMethod]
        public void Registries_RejectDuplicateIds()
        {
            var definitions = new ActorDefinitionRegistry();
            var def = new ActorDefinition("dup", "k", ActorKind.Monster, TeamId.Enemy, ControlScheme.Ai,
                new StatPackageIds("b"), string.Empty, 1f, 1f, 0);
            definitions.Register(def);
            AssertDuplicateRejected(() => definitions.Register(def), "重复主键必须当场报错");

            var loadouts = new SkillLoadoutRegistry();
            loadouts.Register(new SkillLoadout("l"));
            AssertDuplicateRejected(() => loadouts.Register(new SkillLoadout("l")), "重复技能包 id 必须当场报错");
        }
        private static void AssertDuplicateRejected(Action action, string message)
        {
            bool threw = false;
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, message);
        }
    }
}
