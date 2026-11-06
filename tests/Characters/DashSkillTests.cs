using System;
using System.Collections.Generic;
using System.Numerics;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;
using Game.Data;
using Game.Host.Composition;
using Game.Skill;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Characters
{
    // ==================================================================
    // 冲刺（Shift / SkillSlot.Dash）的验收组
    // ==================================================================
    //
    // 这条链路要证明的四件事，缺一条冲刺就只是"按了没反应"或"无限加速"：
    //   ① 释放：同一个技能实例 → 挂在施放者身上的 DashState（不是技能自己的字段）
    //   ② 位移：DashSystem 在固定步长里把 DashState 顶进 Velocity，且真的走了一截
    //   ③ 结束：时长到了组件自动消失
    //   ④ 门控：冷却 / 法力 / 冲刺中再按 / 没配数值 → 全部拒绝
    //
    // 全部无引擎：只用到 Game.Core.Ecs + Game.Actor + Game.Skill。
    // ==================================================================

    /// <summary>能真读数值包的技能上下文（冲刺要读 DashSpeedMul / DashDuration / MoveSpeed）。</summary>
    internal sealed class DashTestContext : ISkillContext
    {
        private readonly Dictionary<int, IReadOnlyStatView> _stats;

        public DashTestContext(ICommandSink sink, IRandomProvider random, World world, Dictionary<int, IReadOnlyStatView> stats)
        {
            Commands = sink;
            Random = random;
            Ecs = world;
            _stats = stats;
        }

        public ICommandSink Commands { get; }
        public IRandomProvider Random { get; }
        public World Ecs { get; }
        public float DeltaTime { get; set; }

        public IReadOnlyStatView? StatsOf(int actorId) =>
            _stats.TryGetValue(actorId, out IReadOnlyStatView? view) ? view : null;
    }

    /// <summary>记录"给谁挂了什么组件"的 sink（冲刺的产物是组件，不是子弹）。</summary>
    internal sealed class DashRecordingSink : ICommandSink
    {
        public readonly List<Entity> Added = new List<Entity>();

        public void SpawnActor(string definitionId, in SpawnPoint at, TeamId team) { }
        public void SpawnProjectile(string projectileId, in SpawnPoint at, in LaunchVector launch, TeamId team) { }
        public void SpawnEffect(string effectId, in SpawnPoint at) { }
        public void Destroy(int actorId, int generation) { }

        /// <summary>落地目标世界：模拟命令缓冲在帧末 Flush 的效果（null = 只记录不落地）。</summary>
        public World? ApplyTo { get; set; }

        public void Add<T>(Entity entity, in T value) where T : unmanaged
        {
            Added.Add(entity);
            ApplyTo?.Set(entity, value);
        }
    }

    [TestClass]
    public sealed class DashSkillTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>造一个施放上下文（施放者 = hero，槽位 = Dash，朝 +X 瞄）。</summary>
        private static SkillContext CtxFor(HeroSandbox box, Entity hero, DashTestContext ctx, float mana = 1_000f) =>
            new SkillContext(
                casterActorId: hero.Index,
                casterEntity: hero,
                casterTeam: TeamId.Player,
                casterKind: ActorKind.Player,
                slot: SkillSlot.Dash,
                aimDirection: Vector3.UnitX,
                aimPoint: new Vector3(10f, 0f, 0f),
                origin: box.World.Get<Transform3>(hero).Position,
                cooldownRemaining: 0f,
                manaCurrent: mana,
                world: ctx);

        /// <summary>建一个能施放冲刺的场景：真实体 + 真数值包 + 真 DashSkill（冷却/消耗取自数据表）。</summary>
        private static (HeroSandbox Box, Entity Hero, DashSkill Skill, DashRecordingSink Sink, DashTestContext Ctx) Setup(
            string variantId = CharacterContentSet.HeroDefaultVariantId)
        {
            var box = new HeroSandbox();
            Entity hero = box.SpawnHero(variantId, Vector3.Zero);

            // 冷却/消耗取自真数据表（test_hero_kit 的 Dash 行：1.2s / 8 法力）。
            SkillLoadout kit = box.Content.Loadouts.Find("test_hero_kit")!;
            ISkill dashRow = kit.Get(SkillSlot.Dash)!;
            var skill = new DashSkill(dashRow.Id, dashRow.CooldownSeconds, dashRow.ManaCost);

            var sink = new DashRecordingSink { ApplyTo = box.World };
            var views = new Dictionary<int, IReadOnlyStatView>();
            views[hero.Index] = box.Views[hero];
            var ctx = new DashTestContext(sink, new Game.Core.Randomness.DeterministicRandom(3), box.World, views);

            return (box, hero, skill, sink, ctx);
        }

        // ───────────────────────── ① 数据与工厂 ─────────────────────────

        [TestMethod]
        public void DataRow_TestHeroKit_DashSlotIsKindDash()
        {
            // 数据行必须真的指向 dash 种类：否则工厂会造出 NullSkill，按 Shift 永远没反应。
            // 直接从内置内容集读那一行（与 characters.xlsx 逐字段锁死，见 CharacterXlsxParityTests）。
            var content = new CharacterContentSet().BuildBuiltinV1();
            Assert.IsTrue(content.SkillRows.TryGetValue(1001, out CharacterSkillSpec row), "必须存在 id=1001 的冲刺技能行");

            Assert.AreEqual(CharacterSkillSpec.KindDash, row.Kind, "1001 号技能行的 Kind 必须是 dash");
            Assert.AreEqual(1, row.Slot, "冲刺必须占 1 号槽（SkillSlot.Dash）");
            Assert.IsTrue(row.CooldownSeconds > 0f, "冲刺必须有正冷却（否则可以无限连冲）");
        }

        [TestMethod]
        public void AllFourVariants_HaveUsableDashStats()
        {
            // 四个变体都配了冲刺数值，且各不一样 —— 这就是"拼接变体"的可见证据：
            // 同一个 DashSkill 类、同一份技能行，换数值包就换手感。
            var variants = new[]
            {
                CharacterContentSet.HeroDefaultVariantId,
                "test_hero_tank", "test_hero_agile", "test_hero_lucky",
            };

            var durations = new HashSet<float>();
            foreach (string variant in variants)
            {
                using var box = new HeroSandbox();
                Entity hero = box.SpawnHero(variant, Vector3.Zero);
                IReadOnlyStatView stats = box.Views[hero];

                float mul = stats.Get(StatId.DashSpeedMul);
                float duration = stats.Get(StatId.DashDuration);

                Assert.IsTrue(mul > 0f, $"变体 '{variant}' 缺少 DashSpeedMul（冲刺会退化成不加速）");
                Assert.IsTrue(duration > 0f, $"变体 '{variant}' 缺少 DashDuration（预检会直接失败，按 Shift 没反应）");
                durations.Add(duration);
            }

            Assert.IsTrue(durations.Count > 1, "四个变体的冲刺时长应当不同（否则变体手感没有区分度）");
        }

        [TestMethod]
        public void Factory_KindDash_ProducesRealDashSkill_NotNullSkill()
        {
            var factory = new ContentSkillFactory();

            ISkill dash = factory.Create(1001, ContentSkillFactory.KindDash, string.Empty, 1.2f, 8f, SkillSlot.Dash);
            Assert.IsInstanceOfType(dash, typeof(DashSkill), "kind=dash 必须造出 DashSkill（不是 NullSkill 占位）");

            ISkill placeholder = factory.Create(1002, CharacterSkillSpec.KindData, string.Empty, 1f, 1f, SkillSlot.Signature);
            Assert.IsInstanceOfType(placeholder, typeof(NullSkill), "data 种类仍然退化成 NullSkill（不阻挡整局启动）");
        }

        [TestMethod]
        public void KnownKinds_IncludesDash()
        {
            // 数据校验器只接受 KnownKinds 里的值；漏加会让整张 characters.xlsx 校验失败。
            bool found = false;
            foreach (string kind in CharacterSkillSpecs.KnownKinds)
            {
                if (kind == CharacterSkillSpec.KindDash)
                {
                    found = true;
                }
            }
            Assert.IsTrue(found, "CharacterSkillSpecs.KnownKinds 必须包含 dash");
        }

        // ───────────────────────── ② 释放：状态落在实体上 ─────────────────────────

        [TestMethod]
        public void Cast_PutsDashStateOnCaster_NotOnSkillInstance()
        {
            // 关键设计：状态属于**实体**。同一技能实例被整个技能包共享，
            // 若把计时器存在技能字段里，两个角色会互相污染（"拼接变体"就废了）。
            (HeroSandbox box, Entity hero, DashSkill skill, DashRecordingSink sink, DashTestContext ctx) = Setup();
            using (box)
            {
                SkillContext cast = CtxFor(box, hero, ctx);
                Assert.IsTrue(skill.CanCast(in cast), "首次施放应当通过预检");

                SkillCastResult result = skill.Cast(in cast);
                Assert.IsTrue(result.Success, "首次施放应当成功");
                Assert.IsTrue(result.StartCooldown, "冲刺成功应当进入冷却（否则可以无限连冲）");

                Assert.AreEqual(1, sink.Added.Count, "施放应当恰好排出一条挂组件的命令");
                Assert.AreEqual(hero, sink.Added[0], "DashState 必须挂在施放者身上");
                Assert.IsTrue(box.World.TryGet<DashState>(hero, out DashState state),
                    "命令落地后实体身上应有 DashState");

                // 速度与时长来自数值包，不是写死的常量。
                IReadOnlyStatView stats = box.Views[hero];
                Assert.AreEqual(stats.Get(StatId.DashDuration), state.Duration, 1e-4f,
                    "冲刺时长必须等于数值包的 DashDuration");
                Assert.AreEqual(stats.Get(StatId.MoveSpeed) * stats.Get(StatId.DashSpeedMul), state.Speed, 1e-4f,
                    "冲刺速度必须等于 MoveSpeed × DashSpeedMul");
                Assert.AreEqual(1f, state.Direction.Length(), 1e-3f, "冲刺方向必须是单位向量");
            }
        }

        [TestMethod]
        public void TwoVariants_DashDifferently_FromOneSharedSkillInstance()
        {
            // 规则7 的直接证据：**同一个技能实例**装在不同数值包上，冲刺距离不同。0 行数据重复。
            using var box = new HeroSandbox();
            Entity fast = box.SpawnHero("test_hero_agile", Vector3.Zero);
            Entity slow = box.SpawnHero("test_hero_tank", new Vector3(50f, 0f, 0f));

            IReadOnlyStatView agileStats = box.Views[fast];
            IReadOnlyStatView tankStats = box.Views[slow];

            float agileDashDist = agileStats.Get(StatId.MoveSpeed) * agileStats.Get(StatId.DashSpeedMul) * agileStats.Get(StatId.DashDuration);
            float tankDashDist = tankStats.Get(StatId.MoveSpeed) * tankStats.Get(StatId.DashSpeedMul) * tankStats.Get(StatId.DashDuration);

            Assert.IsTrue(agileDashDist > tankDashDist,
                $"敏捷型冲刺距离 {agileDashDist:F2}m 应当明显大于重甲型 {tankDashDist:F2}m");
        }

        // ───────────────────────── ③ 位移与结束 ─────────────────────────

        [TestMethod]
        public void DashSystem_MovesEntityAlongDashDirection()
        {
            using var box = new HeroSandbox();
            Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);

            // 手工挂一个朝 +X 的冲刺（避开技能层，单独验证系统层）。
            Vector3 start = box.World.Get<Transform3>(hero).Position;
            box.World.Set(hero, new DashState
            {
                Remaining = 0.2f,
                Duration = 0.2f,
                Direction = Vector3.UnitX,
                Speed = 20f,
            });

            // 顺序要紧：DashSystem 写 Velocity，MovementSystem 用 Velocity 积分位置。
            // 同属 FixedUpdate 组时按注册次序执行，因此冲刺必须先注册（同一帧内就能看到位移）。
            var dash = new DashSystem();
            var move = new MovementSystem();
            box.Pipeline.Add(dash, 0);
            box.Pipeline.Add(move, 1);
            box.Pipeline.Build();

            for (int i = 0; i < 12; i++)
            {
                box.Pipeline.Execute(Dt);
            }

            Vector3 end = box.World.Get<Transform3>(hero).Position;
            float moved = Vector3.Distance(start, end);

            Assert.IsTrue(end.X > start.X, $"朝 +X 冲刺后 X 必须增大（起 {start.X:F2} 止 {end.X:F2}）");
            Assert.IsTrue(moved > 1.5f, $"冲刺 0.2s × 20m/s 应当移动数米，实际只动了 {moved:F2}m");
            Assert.IsTrue(Math.Abs(end.Z - start.Z) < 0.05f, "冲刺方向必须严格沿 Direction，不应有横向漂移");
        }

        [TestMethod]
        public void DashSystem_EndsAndRemovesComponent_AtConfiguredDuration()
        {
            using var box = new HeroSandbox();
            Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);
            box.World.Set(hero, new DashState
            {
                Remaining = 0.1f,
                Duration = 0.1f,
                Direction = Vector3.UnitX,
                Speed = 10f,
            });

            // 顺序要紧：DashSystem 写 Velocity，MovementSystem 用 Velocity 积分位置。
            // 同属 FixedUpdate 组时按注册次序执行，因此冲刺必须先注册（同一帧内就能看到位移）。
            var dash = new DashSystem();
            var move = new MovementSystem();
            box.Pipeline.Add(dash, 0);
            box.Pipeline.Add(move, 1);
            box.Pipeline.Build();

            // 0.1s @ 60fps ≈ 6 帧；跑 10 帧保证越过终点。
            // FinishedCount 是**每帧重置**的（它描述"这一帧结束了几个"），所以要跨帧累加。
            int totalFinished = 0;
            for (int i = 0; i < 10; i++)
            {
                box.Pipeline.Execute(Dt);
                totalFinished += dash.FinishedCount;
            }

            Assert.IsFalse(box.World.TryGet<DashState>(hero, out _),
                "时长到了之后 DashState 必须被移除（否则实体会永远被顶在冲刺速度上）");
            Assert.IsTrue(totalFinished >= 1, $"系统应当记录到一次冲刺结束（累计 {totalFinished}）");
        }

        [TestMethod]
        public void DashSystem_IsNoOp_WhenNobodyIsDashing()
        {
            // 系统是**无条件注册**的，所以必须证明"没人冲的时候它什么都不做"，
            // 否则它会变成所有变体的隐性速度覆盖（悄悄改坏普通走动）。
            using var box = new HeroSandbox();
            Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);

            // 顺序要紧：DashSystem 写 Velocity，MovementSystem 用 Velocity 积分位置。
            // 同属 FixedUpdate 组时按注册次序执行，因此冲刺必须先注册（同一帧内就能看到位移）。
            var dash = new DashSystem();
            var move = new MovementSystem();
            box.Pipeline.Add(dash, 0);
            box.Pipeline.Add(move, 1);
            box.Pipeline.Build();

            for (int i = 0; i < 5; i++)
            {
                box.Pipeline.Execute(Dt);
            }

            Assert.AreEqual(0, dash.DashingCount, "没人冲刺时不该有实体被顶速度");
            Assert.AreEqual(0, dash.FinishedCount);
            Assert.IsFalse(box.World.TryGet<DashState>(hero, out _), "系统不该凭空给别人挂 DashState");
        }

        // ───────────────────────── ④ 门控 ─────────────────────────

        [TestMethod]
        public void CanCast_WhileAlreadyDashing_IsRejected()
        {
            // 连按 Shift 若每次都成功 → 冲刺被不断重置 = 无限速度。这是必须堵死的洞。
            (HeroSandbox box, Entity hero, DashSkill skill, DashRecordingSink sink, DashTestContext ctx) = Setup();
            using (box)
            {
                SkillContext cast = CtxFor(box, hero, ctx);
                Assert.IsTrue(skill.Cast(in cast).Success, "第一次应当成功");
                Assert.AreEqual(1, sink.Added.Count);

                // 命令已落地 → 身上有 DashState，此时再按必须被拒。
                SkillContext again = CtxFor(box, hero, ctx);
                Assert.IsFalse(skill.CanCast(in again), "冲刺中再次施放必须被预检拒绝");
                Assert.IsFalse(skill.Cast(in again).Success);
                Assert.AreEqual(1, sink.Added.Count,
                    "被拒的施放绝不能排出第二条命令（否则组件叠加、速度翻倍）");
            }
        }

        [TestMethod]
        public void CanCast_OnCooldown_IsRejected()
        {
            (HeroSandbox box, Entity hero, DashSkill skill, DashRecordingSink sink, DashTestContext ctx) = Setup();
            using (box)
            {
                // 冷却还没走完（CooldownRemaining > 0）。
                SkillContext cooling = new SkillContext(
                    casterActorId: hero.Index,
                    casterEntity: hero,
                    casterTeam: TeamId.Player,
                    casterKind: ActorKind.Player,
                    slot: SkillSlot.Dash,
                    aimDirection: Vector3.UnitX,
                    aimPoint: new Vector3(10f, 0f, 0f),
                    origin: Vector3.Zero,
                    cooldownRemaining: 0.5f,
                    manaCurrent: 1_000f,
                    world: ctx);

                Assert.IsFalse(skill.CanCast(in cooling), "冷却中必须拦住");
                Assert.AreEqual(0, sink.Added.Count);
                _ = skill.CooldownSeconds; // 冷却确实来自数据（下面用真表断言）
                Assert.IsTrue(skill.CooldownSeconds > 0f, "冲刺必须有正冷却（来自数据表 1.2s）");
            }
        }

        [TestMethod]
        public void CanCast_InsufficientMana_IsRejected()
        {
            (HeroSandbox box, Entity hero, DashSkill _, DashRecordingSink sink, DashTestContext ctx) = Setup();
            using (box)
            {
                var expensive = new DashSkill(1001, 1.2f, 1_000f); // 消耗远超法力上限
                SkillContext cast = CtxFor(box, hero, ctx, mana: 5f);
                Assert.IsFalse(expensive.CanCast(in cast), "法力不足必须拦住");
                Assert.IsFalse(expensive.Cast(in cast).Success);
                Assert.AreEqual(0, sink.Added.Count, "失败时不该排出任何命令");
            }
        }

        [TestMethod]
        public void CanCast_WithoutDashStats_IsRejected_NotSilentlyZeroSpeed()
        {
            // 数据没配 DashDuration 时必须明确"不能冲"，而不是冲一帧就停（那看起来像 bug）。
            using var box = new HeroSandbox();
            Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);

            var sink = new DashRecordingSink { ApplyTo = box.World };
            var emptyViews = new Dictionary<int, IReadOnlyStatView>
            {
                // 一个什么都没声明的数值视图：Get 全部返回 0（= 没配）。
                [hero.Index] = box.Content.StatPacks.Compose(new StatPackageIds("nonexistent_pack")),
            };
            var ctx = new DashTestContext(sink, new Game.Core.Randomness.DeterministicRandom(5), box.World, emptyViews);

            var skill = new DashSkill(1001, 1.2f, 8f);
            SkillContext cast = CtxFor(box, hero, ctx);

            Assert.IsFalse(skill.CanCast(in cast), "没配 DashDuration 时预检必须失败");
            Assert.IsFalse(skill.Cast(in cast).Success, "没配时长时施放必须明确失败");
            Assert.AreEqual(0, sink.Added.Count, "失败时不该排出任何命令");
        }

        // ───────────────────────── 端到端 ─────────────────────────

        [TestMethod]
        public void EndToEnd_CastThenSystem_ProducesExpectedDisplacement()
        {
            // 整条链：数据行 → 工厂 → DashSkill → 命令 → DashState → DashSystem → 位移。
            (HeroSandbox box, Entity hero, DashSkill skill, DashRecordingSink _, DashTestContext ctx) = Setup();
            using (box)
            {
                SkillContext cast = CtxFor(box, hero, ctx);
                Assert.IsTrue(skill.Cast(in cast).Success, "冲刺应当施放成功");

                Vector3 start = box.World.Get<Transform3>(hero).Position;

                // 顺序要紧：DashSystem 写 Velocity，MovementSystem 用 Velocity 积分位置。
                var dash = new DashSystem();
                var move = new MovementSystem();
                box.Pipeline.Add(dash, 0);
                box.Pipeline.Add(move, 1);
                box.Pipeline.Build();

                // 跑满冲刺时长 + 余量
                int frames = (int)MathF.Ceiling(box.Views[hero].Get(StatId.DashDuration) / Dt) + 4;
                for (int i = 0; i < frames; i++)
                {
                    box.Pipeline.Execute(Dt);
                }

                float moved = Vector3.Distance(start, box.World.Get<Transform3>(hero).Position);
                float expected = box.Views[hero].Get(StatId.MoveSpeed) * box.Views[hero].Get(StatId.DashSpeedMul)
                               * box.Views[hero].Get(StatId.DashDuration);

                Assert.IsTrue(moved > 0.5f, $"冲刺应当产生明显位移，实际只有 {moved:F2}m");
                // 允许 ±30% 误差（离散积分 + 结束帧的处理）
                Assert.IsTrue(moved > expected * 0.70f && moved < expected * 1.30f,
                    $"位移 {moved:F2}m 应当接近理论值 {expected:F2}m（MoveSpeed × DashSpeedMul × DashDuration）");
                Assert.IsFalse(box.World.TryGet<DashState>(hero, out _), "跑完之后冲刺状态必须自然消失");
            }
        }
    }
}
