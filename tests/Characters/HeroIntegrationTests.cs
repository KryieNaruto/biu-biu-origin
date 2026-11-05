using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Game.Actor;
using Game.Combat;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Randomness;
using Game.Core.Skills;
using Game.Data;
using Game.Host.Composition;
using Game.Skill;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Projectile = Game.Combat.Projectile;

namespace BiuBiuOrigin.Tests.Characters
{
    // ==================================================================
    // 需求 3 / 4 / 5 的端到端串联：从数据表一路走到角色真的在动
    // ==================================================================
    //
    // 前面几组各自证明了"零件对"；本组只证明一件事：
    //   **把数据表、装配、系统按宿主的真实顺序串起来，玩家角色真的会朝鼠标方向移动。**
    //
    // 这是最有价值的一组：任何一环（数据缺字段 / 装配漏组件 / 分组写错 / 系统顺序错）都会让它红，
    // 而它不需要 Godot —— 证明"引擎无关可测"这条硬约束是被真实满足的，不只是口号。
    // ==================================================================

    /// <summary>
    /// 端到端装配：`CharacterContentSet.BuildBuiltinV1()` → 入队 → 装配 → 跑帧。
    /// </summary>
    [TestClass]
    public sealed class HeroIntegrationTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>
        /// 加速模型下 <paramref name="frames"/> 帧走出的距离（解析解）。
        /// <para>
        /// <see cref="MoveAimSystem"/> 用 <c>v(t) = speed * (1 - exp(-t/resp))</c> 逼近目标速度，
        /// 于是位移是它的积分：<c>s = speed * (T - resp * (1 - exp(-T/resp)))</c>。
        /// </para>
        /// <para>
        /// <b>为什么要求测试会算这个</b>：如果只能用"扣 10% 容差"这种拍脑袋判据，
        /// 那么系统的加速模型改了（比如换成线性加速），测试不会红 —— 它就失去了守卫作用。
        /// 能写出解析解，才说明这条断言真正约束了实现。
        /// </para>
        /// </summary>
        private static float AnalyticDistance(float speed, float responseSeconds, int frames)
        {
            float total = frames * Dt;
            if (responseSeconds <= 0f)
            {
                return speed * total; // 瞬间到位
            }
            return speed * (total - responseSeconds * (1f - MathF.Exp(-total / responseSeconds)));
        }

        /// <summary>宿主级的"输入 → 意图"最小替身（真实实现 <c>PlayerInputIntentSource</c> 在 host 层，测试不引用它）。</summary>
        [UpdateInGroup(UpdateGroup.PreUpdate)]
        private sealed class PlayerInputStub : SystemBase
        {
            public Vector3 MoveDirection;
            public Vector3 AimDirection = Vector3.UnitZ;
            public uint PressedMask;
            public uint HeldMask;

            protected override void OnExecute(float deltaTime)
            {
                World.Query<MoveIntent>().ForEach((Span<MoveIntent> intents) =>
                {
                    for (int i = 0; i < intents.Length; i++)
                    {
                        intents[i].Velocity = MoveDirection;
                        intents[i].Magnitude = MoveDirection.LengthSquared() > 0f ? 1f : 0f;
                    }
                });

                World.Query<AimTarget>().ForEach((Span<AimTarget> aims) =>
                {
                    for (int i = 0; i < aims.Length; i++)
                    {
                        aims[i].Point = aims[i].Point; // 点由宿主算（屏幕射线求交），本桩只给方向
                        aims[i].Direction = AimDirection;
                        aims[i].HasTarget = true;
                    }
                });

                World.Query<ActorIntent>().ForEach((Span<ActorIntent> actorIntents) =>
                {
                    for (int i = 0; i < actorIntents.Length; i++)
                    {
                        actorIntents[i].PressedMask = PressedMask;
                        actorIntents[i].HeldMask = HeldMask;
                        actorIntents[i].AimX = AimDirection.X;
                        actorIntents[i].AimY = AimDirection.Y;
                        actorIntents[i].AimZ = AimDirection.Z;
                    }
                });
            }
        }

        /// <summary>一套按宿主真实分组搭好的世界（PreUpdate 输入 → FixedUpdate 移动 → Update 技能）。</summary>
        private sealed class GameLoop : IDisposable
        {
            public readonly World World = new World();
            public readonly CharacterContentSet Content = new CharacterContentSet().BuildBuiltinV1();
            public readonly Dictionary<Entity, IReadOnlyStatView> Views = new Dictionary<Entity, IReadOnlyStatView>();
            public readonly TestMoveStatReader Stats;
            public readonly SystemPipeline Pipeline;
            public readonly PlayerInputStub Input = new PlayerInputStub();
            public readonly MoveAimSystem MoveAim;
            public readonly MovementSystem Movement = new MovementSystem();
            public Entity Hero;
            public ActorAssemblySystem Assembly = null!;
            public string VariantId = string.Empty;
            public readonly WorldCommandSink CommandSink;
            public readonly TestSkillContext SkillContext;
            public SkillLoadoutRegistry RealRegistry = new SkillLoadoutRegistry();

            public GameLoop()
            {
                Stats = new TestMoveStatReader(World, Views);
                MoveAim = new MoveAimSystem(Stats, Stats);
                Pipeline = new SystemPipeline(World);
                CommandSink = new WorldCommandSink(World);
                SkillContext = new TestSkillContext(World, CommandSink, Views);
            }

            /// <summary>按宿主的装配顺序起一条管线，并生成一个测试人物。</summary>
            public void Boot(string variantId) => Boot(variantId, withWeaponSource: true);

            /// <summary>
            /// 起管线并生成人物。
            /// <para>
            /// <paramref name="withWeaponSource"/> = false 用于钉住"宿主漏接线"的护栏：
            /// 不注入 <see cref="IWeaponStatsSource"/> 时装配必须<u>不崩</u>，但要把缺口记进
            /// <see cref="ActorAssemblySystem.WeaponSourceMissingCount"/>，让宿主能发现漏配。
            /// </para>
            /// </summary>
            public void Boot(string variantId, bool withWeaponSource)
            {
                var queue = new ActorSpawnQueue();
                var assembly = new ActorAssemblySystem(
                    Content.Definitions,
                    Content.Loadouts,
                    Content.StatPacks,
                    new RepositoryPackageSlotResolver(Content.StatPacks),
                    queue,
                    null,
                    withWeaponSource ? new ContentWeaponStatsSource(Content) : null);
                Assembly = assembly;

                int definitionSlot = Content.Definitions.SlotOf(variantId);
                Assert.IsTrue(definitionSlot >= 0, $"找不到角色定义 '{variantId}'");
                queue.Enqueue(new ActorSpawnRequest(definitionSlot, Vector3.Zero));

                // 装配在 PreUpdate 阶段跑（与宿主一致：本帧入队、本帧就装配出来）。
                Pipeline.Add(assembly, 0);
                Pipeline.Add(Input, 100);
                Pipeline.Add(MoveAim, 200);
                Pipeline.Add(Movement, 300);
                // 技能施放在 Update 组（与宿主一致：晚于 FixedUpdate 的移动/推进）。
                // 技能包走宿主同一条路径实例化（ContentSkillSet）：数据层放的是 DataSkill 占位，
                // 只有经过工厂翻成 FireProjectileSkill 之后，按左键才会真的出子弹。
                RealRegistry = RealLoadouts(variantId);
                Pipeline.Add(new SkillCastSystem(RealRegistry, SkillContext), 400);
                Pipeline.Build();

                // 第一帧 dt=0：只做装配，不推进物理。
                Pipeline.Execute(0f);

                Assert.AreEqual(1, assembly.Assembled, $"'{variantId}' 必须被装配出一个实体");
                Hero = assembly.LastAssembled[0];
                VariantId = variantId;

                CharacterVariantSpec row = Content.Assemblies.Get(variantId).Variant;
                Views[Hero] = Content.StatPacks.Compose(
                    new StatPackageIds(row.BasePackageId, growth: row.GrowthPackageId));
            }

            /// <summary>
            /// 用宿主的 <see cref="ContentSkillSet"/> 把本变体的技能行实例化成真技能包，并发布进一个
            /// <u>克隆</u>的仓库（不动 <see cref="CharacterContentSet.Loadouts"/>，避免污染其他用例）。
            /// </summary>
            /// <remarks>
            /// <b>为什么必须走这一步</b>：<c>BuildBuiltinV1()</c> 里的技能包是 <c>DataSkill</c> 占位
            /// （数据层不能 <c>using Game.Skill</c>）。拿占位去施放，冷却与法力都对、子弹却不会出现 ——
            /// 这正是本组要钉住的"宿主漏接工厂"缺陷。
            /// </remarks>
            private SkillLoadoutRegistry RealLoadouts(string variantId)
            {
                CharacterVariantSpec row = Content.Assemblies.Get(variantId).Variant;
                var rows = new List<CharacterSkillSpec>(CharacterSkillSpecs.SlotCount);
                for (int slotNumber = CharacterSkillSpecs.MinSlotNumber;
                     slotNumber <= CharacterSkillSpecs.MaxSlotNumber;
                     slotNumber++)
                {
                    CharacterSkillSpec? spec = Content.SkillRowOf(row.LoadoutId, slotNumber);
                    if (spec is not null)
                    {
                        rows.Add(spec.Value);
                    }
                }

                var set = new ContentSkillSet();
                SkillLoadout? real = set.Build(row.LoadoutId, rows, out List<int> skipped);
                Assert.IsNotNull(real, $"技能包 '{row.LoadoutId}' 必须能实例化出真技能");

                // 左键槽必须是**真的** FireProjectileSkill —— 这是本组用例成立的前提。
                // 其余四槽（冲刺/切武器/独有/交互）在 v1 里还是 KindData 占位（未实现），
                // 它们会以 NullSkill 占住槽位并进 skipped —— 这是预期行为，不是缺陷。
                Assert.IsInstanceOfType<FireProjectileSkill>(real!.Get(SkillSlot.PrimaryAttack),
                    $"技能包 '{row.LoadoutId}' 的左键槽必须是真的 FireProjectileSkill");

                // 关键：技能槽位号均来自 Content.Loadouts 的注册顺序
                // （ActorAssemblySystem 按那个仓库处理 loadoutId → slot）。
                // 如果这里只注册 hero 自己的技能包，槽位号会错位（返回 0 而实际是 5），
                // 技能系统就拿不到技能包 —— 这也是真实宿主会踩的坑，所以模拟它。
                var registry = new SkillLoadoutRegistry();
                for (int i = 0; i < Content.Loadouts.Count; i++)
                {
                    registry.Register(Content.Loadouts.Get(i)!);
                }
                set.PublishTo(registry);
                return registry;
            }

            public void Step(int frames)
            {
                for (int f = 0; f < frames; f++)
                {
                    Pipeline.Execute(Dt);
                }
            }

            public void Dispose() => World.Dispose();
        }

        /// <summary>
        /// 测试用的 <see cref="ISkillContext"/>：把技能需要的三样东西（命令门面 / 只读世界 / 随机源）接到真实内核上。
        /// <para>
        /// <b>为什么自己写而不是用宿主的</b>：真实实现（<c>HostSkillContext</c>）在 <c>scripts/host/**</c>，
        /// 而测试工程刻意不引用 host 层（唯一 allowed 到 <c>using Godot</c> 的地方，引进来就离不开引擎进程）。
        /// 这个替身只做转发，没有逻辑 —— 所以它不会掩盖被测行为。
        /// </para>
        /// </summary>
        internal sealed class TestSkillContext : ISkillContext
        {
            private readonly Dictionary<Entity, IReadOnlyStatView> _views;

            public TestSkillContext(World world, WorldCommandSink commands,
                Dictionary<Entity, IReadOnlyStatView> views)
            {
                Ecs = world;
                Commands = commands;
                _views = views;
            }

            public ICommandSink Commands { get; }

            public World Ecs { get; }

            public IRandomProvider Random { get; set; } = new DeterministicRandom(0);

            /// <summary>帧时长（持续类技能自计时用；英雄 v1 技能包不读它）。</summary>
            public float DeltaTime { get; set; } = Dt;

            public IReadOnlyStatView? StatsOf(int actorId)
            {
                var entity = new Entity(actorId, 0);
                return _views.TryGetValue(entity, out IReadOnlyStatView? view) ? view : null;
            }
        }

        /// <summary>
        /// 测试用的 <see cref="ICommandSink"/>。
        /// <para>
        /// <b>关键事实</b>：<see cref="FireProjectileSkill"/> 生成子弹走的是 <c>ctx.Ecs.Commands</c>
        /// （直接写世界命令缓冲），<u>不经过</u>本门面 —— 门面的 <c>SpawnProjectile</c> 只是留给
        /// "表现层想要一个快速投射物"这类场景的入口，宿主实现里也只打日志。
        /// 所以这里如实照抄：记录计数、不落地实体。若改成"门面也 Create"，测出来的子弹就来自门面，
        /// 而不是来自被测的开火逻辑，用例会失去意义。
        /// </para>
        /// </summary>
        internal sealed class WorldCommandSink : ICommandSink
        {
            private readonly World _world;

            public WorldCommandSink(World world) => _world = world;

            /// <summary>门面被请求生成投射物的次数（诊断用）。</summary>
            public int ProjectileRequests { get; private set; }

            public void SpawnProjectile(string projectileId, in SpawnPoint at, in LaunchVector launch, TeamId team) =>
                ProjectileRequests++;

            public void SpawnActor(string definitionId, in SpawnPoint at, TeamId team) =>
                throw new NotSupportedException("v1 英雄技能包不应召唤角色：" + definitionId);

            public void SpawnEffect(string effectId, in SpawnPoint at)
            {
                // 纯表现特效由视图层消费，逻辑层测试不关心。
            }

            public void Destroy(int actorId, int generation) =>
                _world.Commands.Destroy(new Entity(actorId, generation));
        }

        /// <summary>
        /// 测试用的 <see cref="IWeaponStatsSource"/>：模拟宿主"从装配清单取武器行 → 填 WeaponStats"。
        /// <para>
        /// <b>它为什么必须存在</b>：<c>WeaponStats</c> 是装配系统写进实体的组件，但装配系统看不到 Excel ——
        /// 它只认一个"按定义槽位取武器数值"的接缝。宿主把它接上，玩家才有枪；漏接就是静默的
        /// "能走动、能瞄准、开火没反应"。本类就是那个接缝在测试里的替身。
        /// </para>
        /// <para>
        /// <b>映射规则必须与生产一致</b>：定义槽位 → 变体行 → <c>WeaponRowId</c> → 武器行 → 组件字段。
        /// 这里逐字段抄，不偷懒塞常数 —— 否则断言 <c>Damage &gt; 0</c> 只是在测我自己。
        /// </para>
        /// </summary>
        internal sealed class ContentWeaponStatsSource : IWeaponStatsSource
        {
            private readonly CharacterContentSet _content;

            public ContentWeaponStatsSource(CharacterContentSet content) => _content = content;

            public bool TryResolve(int definitionSlot, out WeaponStats stats)
            {
                stats = default;

                // 槽位 → 定义：ActorDefinitionRegistry 是唯一的槽位↔定义权威。
                ActorDefinition? definition = _content.Definitions.Get(definitionSlot);
                if (definition is null)
                {
                    return false;
                }

                CharacterVariantSpec variant = _content.Assemblies.Get(definition.Id).Variant;
                if (string.IsNullOrEmpty(variant.WeaponRowId))
                {
                    return false;
                }

                CharacterWeaponSpec? weapon = _content.Assemblies.FindWeapon(variant.WeaponRowId);
                if (weapon is null)
                {
                    return false;
                }

                // 逐字段抄数据行。PelletsPerShot 现在武器表里有真值了（v1 = 1），
                // 所以这里直接抄，不再是"数据没配"的 0。
                stats = new WeaponStats
                {
                    Damage = weapon.Damage,
                    BulletSpeed = weapon.BulletSpeed,
                    RangeMeters = weapon.RangeMeters,
                    SpreadDegrees = weapon.SpreadDegrees,
                    CriticalChance = weapon.CriticalChance,
                    CriticalMultiplier = weapon.CriticalMultiplier,
                    ProjectileRadius = weapon.ProjectileRadius,
                    ShotIntervalSeconds = weapon.ShotCooldown,
                    PelletsPerShot = weapon.PelletsPerShot,
                    WeaponId = weapon.WeaponId,
                };
                return true;
            }
        }

        [TestMethod]
        [DataRow("test_hero")]
        [DataRow("test_hero_tank")]
        [DataRow("test_hero_agile")]
        [DataRow("test_hero_lucky")]
        public void EndToEnd_AssemblyProducesPlayableComponentSet(string variantId)
        {
            // 需求 3/4/5 的组件基线：光有数据表不够，玩家角色必须**真的带上**这些组件，
            // 否则移动系统 / 瞄准系统 / 射击技能 / HUD / 视图各缺一块，且只在运行期表现为"没反应"。
            using var loop = new GameLoop();
            loop.Boot(variantId);
            Entity hero = loop.Hero;

            Assert.IsTrue(loop.World.IsAlive(hero), $"[{variantId}] 装配出的实体必须存活");
            Assert.IsTrue(loop.World.Has<Transform3>(hero), $"[{variantId}] 缺 Transform3（所有位置的前提）");
            Assert.IsTrue(loop.World.Has<Velocity>(hero), $"[{variantId}] 缺 Velocity（MovementSystem 的积分对象）");
            Assert.IsTrue(loop.World.Has<MoveIntent>(hero), $"[{variantId}] 缺 MoveIntent（WASD 意图的落点）");
            Assert.IsTrue(loop.World.Has<AimTarget>(hero), $"[{variantId}] 缺 AimTarget（鼠标瞄准的落点）");
            Assert.IsTrue(loop.World.Has<ViewAppearance>(hero), $"[{variantId}] 缺 ViewAppearance（表现层要它才画得出来）");
            Assert.IsTrue(loop.World.Has<ActorTag>(hero), $"[{variantId}] 缺 ActorTag（调试/HUD 要它才知道是哪一版角色）");
            Assert.IsTrue(loop.World.Has<Health>(hero), $"[{variantId}] 缺 Health（HUD 血条）");
            Assert.IsTrue(loop.World.Has<ActorIdentity>(hero), $"[{variantId}] 缺 ActorIdentity（句柄/夺舍的身份）");

            ViewAppearance visual = loop.World.Get<ViewAppearance>(hero);
            Assert.IsGreaterThanOrEqualTo(0, visual.VisualSlotIndex,
                $"[{variantId}] VisualSlotIndex 必须 >= 0（-1 表示宿主没接外观源，玩家会看不见自己）");
            Assert.IsGreaterThan(0f, visual.Scale, $"[{variantId}] 外观缩放必须 > 0");

            // 三个新细分的数值必须来自数据且为正 —— 否则移动/转向会退化成"不动"或"瞬移"。
            IReadOnlyStatView stats = loop.Views[hero];
            Assert.IsTrue(stats.Has(StatId.MoveSpeed), $"[{variantId}] 数值包缺 MoveSpeed");
            Assert.IsGreaterThan(0f, stats.Get(StatId.MoveSpeed), $"[{variantId}] MoveSpeed 必须 > 0");
            Assert.IsTrue(stats.Has(StatId.MoveAccelTime), $"[{variantId}] 数值包缺 MoveAccelTime（加速手感）");
            Assert.IsGreaterThanOrEqualTo(0f, stats.Get(StatId.MoveAccelTime), $"[{variantId}] MoveAccelTime 不能为负");
            Assert.IsTrue(stats.Has(StatId.TurnResponseTime), $"[{variantId}] 数值包缺 TurnResponseTime（转向手感）");
            Assert.IsGreaterThanOrEqualTo(0f, stats.Get(StatId.TurnResponseTime), $"[{variantId}] TurnResponseTime 不能为负");
        }

        [TestMethod]
        public void EndToEnd_HeroMovesForwardAtMoveSpeed_ForSixtyFrames()
        {
            // 需求 3 的最终证据：按住 W 一秒（60 帧），角色确实沿 -Z 走了一段，
            // 距离≈ 移速×时间（扣除起步的加速损失）。
            using var loop = new GameLoop();
            loop.Boot(CharacterContentSet.HeroDefaultVariantId);

            float speed = loop.Views[loop.Hero].Get(StatId.MoveSpeed);
            loop.Input.MoveDirection = new Vector3(0f, 0f, -1f);
            loop.Input.AimDirection = new Vector3(0f, 0f, -1f);

            Vector3 start = loop.World.Get<Transform3>(loop.Hero).Position;
            loop.Step(60);
            Vector3 moved = loop.World.Get<Transform3>(loop.Hero).Position - start;

            Assert.IsGreaterThan(0f, moved.Z * -1f, $"按住 W 60 帧后必须朝 -Z 前进，实际位移 {moved}");
            Assert.AreEqual(0f, moved.X, 1e-4f, "只按 W 时不应有侧向位移");

            float expected = speed * 60f * Dt;
            // 精确的解析解，不用"扣 10%"这种拍脑袋容差：加速损失是可以用公式算出来的定值。
            float response = loop.Views[loop.Hero].Get(StatId.MoveAccelTime);
            float analytic = AnalyticDistance(speed, response, 60);
            Assert.AreEqual(analytic, moved.Length(), MathF.Max(analytic * 0.02f, 0.02f),
                $"位移必须等于加速模型的解析解 {analytic:0.000}（理想匀速是 {expected:0.000}）");
            Assert.IsTrue(moved.Length() <= expected + 1e-3f,
                $"位移 {moved.Length():0.00} 不应超过匀速上限 {expected:0.00}（超过说明移速被放大了，例如斜向未归一化）");

            // 稳态速度必须收敛到数值包里的移速。
            Assert.AreEqual(speed, loop.World.Get<Velocity>(loop.Hero).Linear.Length(), 1e-2f,
                "稳态速度必须等于数值包里的 MoveSpeed");
        }

        [TestMethod]
        public void EndToEnd_AllFourVariantsMoveAtTheirOwnSpeed()
        {
            // 规则6（数值包可互相赋值）的运行时证据：4 个变体的移速差异直接体现为位移差异，
            // 而**代码一行没变** —— 差异全部来自数据。
            var distances = new Dictionary<string, float>();
            float[] speeds = new float[4];
            float[] accels = new float[4];
            string[] ids = { "test_hero", "test_hero_tank", "test_hero_agile", "test_hero_lucky" };

            for (int i = 0; i < ids.Length; i++)
            {
                using var loop = new GameLoop();
                loop.Boot(ids[i]);
                speeds[i] = loop.Views[loop.Hero].Get(StatId.MoveSpeed);
                accels[i] = loop.Views[loop.Hero].Get(StatId.MoveAccelTime);

                loop.Input.MoveDirection = Vector3.UnitX;
                Vector3 start = loop.World.Get<Transform3>(loop.Hero).Position;
                loop.Step(60);
                distances[ids[i]] = (loop.World.Get<Transform3>(loop.Hero).Position - start).Length();
            }

            // 每个变体的位移都必须与自己的移速成正比（而不是与某个共同常量成正比）。
            // 用与上面同一条解析解：加速损失按各自的 MoveAccelTime 算，所以"重甲起步慢"也会被正确衡量。
            for (int i = 0; i < ids.Length; i++)
            {
                float analytic = AnalyticDistance(speeds[i], accels[i], 60);
                Assert.AreEqual(analytic, distances[ids[i]], MathF.Max(analytic * 0.02f, 0.02f),
                    $"'{ids[i]}' 位移 {distances[ids[i]]:0.000} 必须等于它自己移速 {speeds[i]} 的解析解 {analytic:0.000}");
            }

            // 至少有一组变体移速不同 —— 否则"装配成不同变体"就毫无意义。
            var distinct = new HashSet<float>(speeds);
            Assert.IsGreaterThan(1, distinct.Count,
                $"4 个变体出现了相同移速 {string.Join("/", speeds)}：数值包没有真正区分开");
        }

        [TestMethod]
        public void EndToEnd_HeroTurnsTowardsAimWhileMoving()
        {
            // 需求 4 的前半段：一边按 W 前进、一边把鼠标指向 +X，角色必须转向 +X 而行进方向仍是 -Z
            // （"朝哪看"和"往哪走"是两件独立的事 —— 这是双摇杆手感的定义）。
            using var loop = new GameLoop();
            loop.Boot(CharacterContentSet.HeroDefaultVariantId);

            loop.Input.MoveDirection = new Vector3(0f, 0f, -1f);
            loop.Input.AimDirection = Vector3.UnitX;

            Vector3 start = loop.World.Get<Transform3>(loop.Hero).Position;
            loop.Step(60);
            Vector3 moved = loop.World.Get<Transform3>(loop.Hero).Position - start;
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, loop.World.Get<Transform3>(loop.Hero).Rotation);

            Assert.IsTrue(moved.Z < -0.1f, $"必须仍朝 -Z 移动，实际位移 {moved}");
            Assert.AreEqual(0f, moved.X, 1e-3f, "瞄准方向不应影响位移方向");
            Assert.AreEqual(1f, forward.X, 1e-2f, $"模型必须转向 +X，实际前向 {forward}");
        }

        [TestMethod]
        public void EndToEnd_StoppingStopsTheHero()
        {
            // 松手必须停下来（且不残留速度），否则"贴墙滑行""失去控制"这类手感问题会没人发现。
            using var loop = new GameLoop();
            loop.Boot(CharacterContentSet.HeroDefaultVariantId);

            loop.Input.MoveDirection = Vector3.UnitX;
            loop.Step(60);
            Assert.IsGreaterThan(0f, loop.World.Get<Velocity>(loop.Hero).Linear.Length(), "前提：先跑起来");

            loop.Input.MoveDirection = Vector3.Zero;
            loop.Step(120);

            Assert.AreEqual(0f, loop.World.Get<Velocity>(loop.Hero).Linear.Length(), 1e-3f,
                "松开所有键 2 秒后速度必须为 0");

            Vector3 rest = loop.World.Get<Transform3>(loop.Hero).Position;
            loop.Step(30);
            Assert.AreEqual(rest.X, loop.World.Get<Transform3>(loop.Hero).Position.X, 1e-5f,
                "停下后不应继续滑行");
        }

        [TestMethod]
        public void EndToEnd_HeroAliveAndQueryableAfterSixtyFrames()
        {
            // 跑帧过程中不能把自己跑没（过期/误销毁）。
            using var loop = new GameLoop();
            loop.Boot(CharacterContentSet.HeroDefaultVariantId);
            loop.Input.MoveDirection = Vector3.UnitZ;
            loop.Step(60);

            Assert.IsTrue(loop.World.IsAlive(loop.Hero), "角色 60 帧后必须仍然存活");
            Assert.AreEqual(1, loop.World.Query<Transform3, Velocity, MoveIntent>().Count,
                "角色必须仍能被移动查询命中（装配出的组件没有被跑丢）");
        }

        [TestMethod]
        [DataRow("test_hero")]
        [DataRow("test_hero_tank")]
        [DataRow("test_hero_agile")]
        [DataRow("test_hero_lucky")]
        public void EndToEnd_HostWiresWeaponStatsFromData(string variantId)
        {
            // 需求 4（射击）的**前一半**：角色身上必须有武器数值。
            // 四个变体的武器行在 CharacterContentSet 里是齐的，所以"填不出来"一定是宿主漏接线，
            // 而不是数据不存在 —— 因此断言按"必须有且 > 0"写死，不给宽容。
            using var loop = new GameLoop();
            loop.Boot(variantId);

            Assert.IsTrue(loop.World.TryGet<WeaponStats>(loop.Hero, out WeaponStats weapon),
                $"'{variantId}' 装配后必须带 WeaponStats 组件（宿主没接 IWeaponStatsSource？）");

            // 与数据行逐字段对照：不是在测我自己编的数，而是钉住"数据 → 组件"没有走样。
            CharacterVariantSpec variant = loop.Content.Assemblies.Get(variantId).Variant;
            CharacterWeaponSpec row = loop.Content.Assemblies.FindWeapon(variant.WeaponRowId)
                ?? throw new AssertFailedException($"'{variantId}' 的武器行 '{variant.WeaponRowId}' 在数据里不存在");

            Assert.AreEqual(row.Damage, weapon.Damage, 1e-4f, "Damage 必须等于武器行");
            Assert.AreEqual(row.BulletSpeed, weapon.BulletSpeed, 1e-4f, "BulletSpeed 必须等于武器行");
            Assert.AreEqual(row.RangeMeters, weapon.RangeMeters, 1e-4f, "RangeMeters 必须等于武器行");
            Assert.AreEqual(row.ShotCooldown, weapon.ShotIntervalSeconds, 1e-4f,
                "ShotIntervalSeconds 必须等于武器行 ShotCooldown（HUD 展示的真实射速）");
            Assert.AreEqual(row.WeaponId, weapon.WeaponId, "WeaponId 必须等于武器行主键");
            Assert.AreEqual(row.PelletsPerShot, weapon.PelletsPerShot,
                "PelletsPerShot 必须等于武器行（v1 全 = 1；将来加散弹由数据驱动，不在代码里写死）");

            // 产品要求的"必须存在且有值"三条（Lead 定：这是产品要求，不是可选行为）。
            Assert.IsGreaterThan(0f, weapon.Damage, $"'{variantId}' 的 Damage 必须 > 0");
            Assert.IsGreaterThan(0f, weapon.ShotIntervalSeconds, $"'{variantId}' 的 ShotIntervalSeconds 必须 > 0");
            Assert.IsGreaterThan(0, weapon.WeaponId, $"'{variantId}' 的 WeaponId 必须 > 0");
        }

        [TestMethod]
        [DataRow("test_hero")]
        [DataRow("test_hero_tank")]
        [DataRow("test_hero_agile")]
        [DataRow("test_hero_lucky")]
        public void EndToEnd_MissingWeaponSource_DoesNotCrashButIsCounted(string variantId)
        {
            // 护栏：宿主**漏接** IWeaponStatsSource 时，装配不许崩 —— 但要留下可观测的缺口计数。
            // 为什么这是必需的：漏接线最坏的失败形态是"人物能走能瞄、开火静默无反应"，
            // 没有任何异常，只有玩家自己发现。把缺口变成计数器，宿主 boot 时一行日志就能发现。
            using var loop = new GameLoop();
            loop.Boot(variantId, withWeaponSource: false);

            Assert.AreEqual(1, loop.Assembly.Assembled, $"'{variantId}' 没注入武器源也必须装配成功");
            Assert.IsTrue(loop.World.IsAlive(loop.Hero), "装配出的实体必须存活");

            Assert.IsGreaterThan(0, loop.Assembly.WeaponSourceMissingCount,
                $"'{variantId}' 没注入武器源时必须计入 WeaponSourceMissingCount（宿主漏接线的护栏）");

            Assert.IsFalse(loop.World.Has<WeaponStats>(loop.Hero),
                "没有武器源时不应凭空写一个 WeaponStats 出来");
        }

        [TestMethod]
        [DataRow("test_hero")]
        [DataRow("test_hero_tank")]
        [DataRow("test_hero_agile")]
        [DataRow("test_hero_lucky")]
        public void EndToEnd_PrimaryAttackSlotIsFireProjectileSkill(string variantId)
        {
            // 需求 4 的数据侧前置：左键槽装的必须是**真开火**技能，不是 DataSkill 占位。
            // 历史状态：四个 hero 的左键槽一度都是 DataSkill（能"施放"但不产生子弹），
            // 端到端测试因此永远看不到弹丸。这条用例钉住它不许退回去。
            using var loop = new GameLoop();
            loop.Boot(variantId);

            CharacterVariantSpec variant = loop.Content.Assemblies.Get(variantId).Variant;
            SkillLoadout loadout = loop.Content.Loadouts.Get(loop.Content.Definitions.SlotOf(variantId));

            // ① 运行时：左键槽装了技能（不再是"槽是空的"）。
            ISkill primary = loadout.Get(SkillSlot.PrimaryAttack)
                ?? throw new AssertFailedException($"'{variantId}' 左键槽必须装技能");

            // ② 数据侧（**真正的契约**）：左键槽那一行必须声明成"真开火"。
            //
            // 为什么改查数据行而不是断言 `primary is FireProjectileSkill`：
            // 架构整改后数据层**不再构造具体技能实例**（声明与构造分离，见 CharacterSkillSpec 的注释），
            // 它只产出一行 `CharacterSkillSpec{Kind="fire_projectile"}`，由宿主的 ISkillFactory
            // 在组合根实例化。所以"这是不是真开火"的权威表述在**数据行**上，
            // 而不是测试里握着的那个 ISkill 实例的 CLR 类型。
            // 若断言运行期类型，本用例会被"宿主还没接工厂"误报成数据错误，反而失去诊断价值。
            int primarySkillId = primary.Id;
            Assert.AreNotEqual(0, primarySkillId, $"'{variantId}' 左键槽必须有技能 id");

            Assert.IsTrue(loop.Content.SkillRows.TryGetValue(primarySkillId, out CharacterSkillSpec primaryRow),
                $"'{variantId}' 左键技能 id={primarySkillId} 必须在 SkillRows 里有数据行");

            Assert.IsTrue(primaryRow.IsProjectile,
                $"'{variantId}' 左键槽必须声明为 Kind={CharacterSkillSpec.KindFireProjectile}，" +
                $"实际 Kind='{primaryRow.Kind}'（退化成占位技能 = 左键不出子弹）");

            Assert.IsTrue(primaryRow.TryGetSlot(out SkillSlot declaredSlot) && declaredSlot == SkillSlot.PrimaryAttack,
                $"'{variantId}' 该技能行的 Slot 必须就是 PrimaryAttack");

            // ③ 冷却必须与武器行 ShotCooldown 一致 —— 否则"HUD 显示 0.12 秒一发、实际 1 秒一发"。
            CharacterWeaponSpec row = loop.Content.Assemblies.FindWeapon(variant.WeaponRowId)
                ?? throw new AssertFailedException($"'{variantId}' 的武器行不存在");
            Assert.AreEqual(row.ShotCooldown, primaryRow.CooldownSeconds, 1e-4f,
                $"'{variantId}' 开火技能行冷却必须等于武器行 ShotCooldown（单真源）");

            // ④ 运行时实例的冷却同样等于武器行 —— 这条钉住 ISkillFactory 没有把冷却改走样。
            Assert.AreEqual(row.ShotCooldown, primary.CooldownSeconds, 1e-4f,
                $"'{variantId}' 实例化后的技能冷却必须仍等于武器行 ShotCooldown");
        }

        [TestMethod]
        [DataRow("test_hero")]
        [DataRow("test_hero_tank")]
        [DataRow("test_hero_agile")]
        [DataRow("test_hero_lucky")]
        public void HeroSkillRows_DeclareRealProjectileSkillsPointingAtExistingWeapons(string variantId)
        {
            // 纯数据层断言（不依赖宿主的 ISkillFactory）：
            // 数据层的技能行里，4 个 hero 的左键行必须声明成"真开火"，
            // 且它的 ProjectileId 必须指向**真实存在的武器行** —— 否则宿主工厂拿到一个
            // 指向空气的 id，会在开火时找不到武器数值（静默不出子弹，不报错）。
            //
            // ⚠️ v1 已知语义债（packages 已报 Lead 待裁决）：
            //    `CharacterSkillSpec.ProjectileId` 目前存的其实是**武器行 id**（如 "pistol_basic"），
            //    而 `FireProjectileSkill` 的同名参数语义是"抛射物定义 id（子弹外观）"。
            //    两者现在恰好同源，所以 `FindWeapon(row.ProjectileId)` 是对的。
            //    等字段拆分（WeaponId + ProjectileId）落地后，这里要改成 `FindWeapon(row.WeaponId)`。
            var content = new CharacterContentSet().BuildBuiltinV1();
            CharacterVariantSpec variant = content.Assemblies.Get(variantId).Variant;

            // 用数据层契约 API 按槽位直接取行，不再手写槽位遍历。
            CharacterSkillSpec? maybeRow = content.SkillRowOf(variant.LoadoutId, PrimaryAttackSlotNumber);
            Assert.IsNotNull(maybeRow,
                $"'{variantId}' 的技能包 '{variant.LoadoutId}' 槽位 {PrimaryAttackSlotNumber} 必须有技能行");

            CharacterSkillSpec row = maybeRow!.Value;
            Assert.AreEqual(PrimaryAttackSlotNumber, row.Slot,
                $"'{variantId}' 该技能行的 Slot 必须就是 PrimaryAttack");

            Assert.IsTrue(row.IsProjectile,
                $"'{variantId}' 左键技能行必须声明 Kind='{CharacterSkillSpec.KindFireProjectile}'，实际 '{row.Kind}'");

            Assert.IsTrue(row.TryGetSlot(out SkillSlot slot) && slot == SkillSlot.PrimaryAttack,
                $"'{variantId}' 该行的 Slot 必须能翻成 SkillSlot.PrimaryAttack");

            // v1 语义债已由数据层拆开（WeaponId / ProjectileId 两个字段）：
            //   WeaponId     = "哪把枪"     → 查武器行拿伤害/弹速/射程/射速
            //   ProjectileId = "子弹长什么样" → 给技能与命令门面选外观
            // 两者都是 string，混用**不会有编译错误**，运行期表现为"日志把 pistol_basic 当子弹 id"。
            // 所以这里两条都钉死。
            Assert.IsFalse(string.IsNullOrEmpty(row.WeaponId),
                $"'{variantId}' 射击技能的 WeaponId 不能为空（靠它查武器数值）");
            Assert.IsNotNull(content.Assemblies.FindWeapon(row.WeaponId),
                $"'{variantId}' 的 WeaponId='{row.WeaponId}' 必须指向存在的武器行");

            Assert.IsFalse(string.IsNullOrEmpty(row.ProjectileId),
                $"'{variantId}' 射击技能的 ProjectileId 不能为空（表现层拿它选子弹外观）");

            // 两值一旦相等，就是"武器 id 兼职子弹外观 id"——v1 曾经的坑，数据层自己也标了可疑。
            Assert.IsFalse(row.SuspectsWeaponProjectileConflated,
                $"'{variantId}' 的 WeaponId 与 ProjectileId 都是 '{row.WeaponId}'：" +
                "武器 id 不得兼作子弹外观 id（语义不同的两件事）");

            // 数据行的 ProjectileId 是"外观 id"，得能被表现层当真——v1 约定外观 id 带 proj_ 前缀。
            // 这条同时是"宿主工厂没把两值传反"的第一道网（工厂传错时传进去的是 pistol_basic）。
            Assert.IsTrue(row.ProjectileId.StartsWith(ProjectileIdPrefix, StringComparison.Ordinal),
                $"'{variantId}' 的 ProjectileId='{row.ProjectileId}' 必须以外观前缀 '{ProjectileIdPrefix}' 开头。" +
                "若它是武器 id（如 pistol_basic），说明 SkillLoads 表把 WeaponId/ProjectileId 两列填混了。");

            Assert.IsGreaterThan(0f, row.CooldownSeconds,
                $"'{variantId}' 射击技能行冷却必须 > 0（0 = 可以无限连发）");

            // 冷却单真源：技能行冷却 == 该变体武器行的 ShotCooldown。
            CharacterWeaponSpec weapon = content.Assemblies.FindWeapon(variant.WeaponRowId)
                ?? throw new AssertFailedException($"'{variantId}' 的武器行 '{variant.WeaponRowId}' 不存在");
            Assert.AreEqual(weapon.ShotCooldown, row.CooldownSeconds, 1e-4f,
                $"'{variantId}' 技能行冷却必须等于武器行 ShotCooldown");
        }

        [TestMethod]
        [DataRow("test_hero", 1001, 1101, 1201, 1304, 1401)]
        [DataRow("test_hero_tank", 1001, 1106, 1201, 1305, 1401)]
        [DataRow("test_hero_agile", 1001, 1107, 1201, 1306, 1401)]
        [DataRow("test_hero_lucky", 1001, 1108, 1201, 1307, 1401)]
        public void HeroSkillLoadouts_HaveCorrectSkillIdAtEverySlot(
            string variantId, int dash, int primary, int weaponSwap, int signature, int interact)
        {
            // 5 槽位 × 4 变体的全表校验。为什么值得单独一条：
            // 宿主把技能包装到实体上时是**按槽位号**写的，槽位接错不会有任何编译错误、
            // 也不会有异常 —— 表现是"按冲刺键打出的是普攻"。数据表槽位列一旦写错，
            // 只有这种全表断言能抓住。
            //
            // 期望值出处：data/excel/characters.xlsx → SkillLoads 表
            //             （LoadoutId + Slot → SkillId）。数据从第 11 行起，前 10 行是竖向排布的表头。
            //
            // 硬编码 id 是否"测试跟着实现走"？不是 —— 槽位→技能 id 是**内容设计事实**
            // （三处独立表达：Excel 真源、运行时副本、本 DataRow）。钉死它意味着
            // "有人改了槽位配置必须手动确认一次"，这正是我们想要的行为。
            // 真正的护栏是下面的 HeroSkillLoadoutSlots_FollowVariantStructure 结构断言：
            // 即使数据表调整，也只有具体 id 这条会红，不会整片炸。
            var content = new CharacterContentSet().BuildBuiltinV1();
            CharacterVariantSpec variant = content.Assemblies.Get(variantId).Variant;

            IReadOnlyList<int> ids = content.SkillIdsOf(variant.LoadoutId);
            CollectionAssert.AreEqual(
                new[] { dash, primary, weaponSwap, signature, interact },
                ids.ToArray(),
                $"'{variantId}' 技能包 '{variant.LoadoutId}' 的 5 个槽位技能 id 与数据表不一致。\n" +
                "【怎么处理】若你是有意改了 data/excel/characters.xlsx 的 SkillLoads 槽位配置，" +
                "请同步本用例 [DataRow] 的期望值（4 个变体各一行）；" +
                "若是无意改动，说明技能装配串了槽位 —— 典型表现是「按冲刺键打出普攻」，" +
                "检查 SkillLoads 表的 Slot 列是否被移动或复制粘贴错行。");
        }

        [TestMethod]
        public void HeroSkillLoadoutSlots_FollowVariantStructure()
        {
            // 结构性护栏（不依赖任何具体 id），兜住"改了配置但忘了改上面的 DataRow"：
            //   ・Dash(1) / WeaponSwap(3) / Interact(5) —— 四变体必须**完全相同**
            //   ・PrimaryAttack(2) / Signature(4)       —— 四变体必须**各不相同**
            //
            // 这条与 HeroSkillLoadouts_HaveCorrectSkillIdAtEverySlot 分工明确：
            //   结构断言 = 真实护栏（不随风而变）；
            //   具体 id 断言 = 显式确认点（改了就该有人盖章）。
            //
            // 为什么左键和 R 必须各不相同：四个变体是"同一角色的四种取向"，
            // 若某些变体的左键 id 相同，说明有人复制粘贴行忘了改 id，
            // 此时"装配不同变体"实际装配的是同一个东西 —— 数据看起来正常，行为却重复。
            var content = new CharacterContentSet().BuildBuiltinV1();
            string[] variants = { "test_hero", "test_hero_tank", "test_hero_agile", "test_hero_lucky" };

            int[] sharedSlotNumbers = { 1, 3, 5 };   // 冲刺 / 切武器 / 交互
            int[] variantSlotNumbers = { 2, 4 };     // 左键 / R 独有技

            var idsByVariant = variants
                .ToDictionary(
                    v => v,
                    v => content.SkillIdsOf(content.Assemblies.Get(v).Variant.LoadoutId));

            // ① 共用槽：四变体必须一致。
            foreach (int slotNumber in sharedSlotNumbers)
            {
                int expected = idsByVariant[variants[0]][slotNumber - 1];
                Assert.AreNotEqual(0, expected,
                    $"槽位 {slotNumber} 必须有技能（0 = 该槽在数据表里没配，宿主会装空槽）");

                foreach (string variantId in variants)
                {
                    Assert.AreEqual(expected, idsByVariant[variantId][slotNumber - 1],
                        $"'{variantId}' 槽位 {slotNumber} 的技能必须与其他变体相同" +
                        "（冲刺/切武器/交互是四变体共用的，只有左键与 R 随变体不同）。" +
                        "若这是有意改动，请同步 HeroSkillLoadouts_HaveCorrectSkillIdAtEverySlot 的 DataRow。");
                }
            }

            // ② 变体槽：四变体必须互不相同。
            foreach (int slotNumber in variantSlotNumbers)
            {
                var values = variants
                    .Select(v => idsByVariant[v][slotNumber - 1])
                    .ToArray();

                foreach (int value in values)
                {
                    Assert.AreNotEqual(0, value,
                        $"槽位 {slotNumber} 在每个变体上都必须有技能（0 = 数据表漏配）");
                }

                Assert.AreEqual(values.Length, values.Distinct().Count(),
                    $"槽位 {slotNumber} 的技能 id 在四个变体之间必须互不相同，实际：[" +
                    string.Join(", ", values) + "]。相同意味着有人复制粘贴变体行却没改技能 id ——" +
                    "数据看起来正常，但「换变体」实际换的是同一个技能。");
            }
        }

        [TestMethod]
        public void HeroWeaponRows_SatisfyCombatSemantics()
        {
            // 语义约束（不是具体数值）——挡住"数值离谱但结构合法"这类灾难。
            //
            // 为什么这条必要：装配用例的期望值都取自**武器行本身**，所以它们只能证明
            // "装配没改数"，**证明不了"数据是合理的"**。这一课是血换的：
            // Excel Weapons 表曾经把 CriticalMultiplier 填成 0.0 / 0.4 / 0.6
            // （按"暴击加成"理解），而代码是 `Damage * CriticalMultiplier` ——
            // 照那张表跑，暴击伤害反而更轻，填 0 的两把武器暴击直接零伤害。
            // 当时 25 处字段不一致、216 条用例全绿，没有一条抓到。
            //
            // weapon id 取自 Excel `Weapons` 表主键（四把英雄武器）。
            string[] weaponIds = { "pistol_basic", "cannon_heavy", "smg_light", "ricochet_lucky" };
            var content = new CharacterContentSet().BuildBuiltinV1();

            foreach (string weaponId in weaponIds)
            {
                CharacterWeaponSpec weapon = content.Assemblies.FindWeapon(weaponId)
                    ?? throw new AssertFailedException($"武器行 '{weaponId}' 不存在");

                // FireProjectileSkill 算的是 Damage * CriticalMultiplier，契约要求 >= 1：
                // < 1 让暴击比不暴击还轻，= 0 让暴击零伤害。两种都是静默的数据灾难。
                Assert.IsGreaterThanOrEqualTo(1.0f, weapon.CriticalMultiplier,
                    $"武器 '{weaponId}' 的 CriticalMultiplier={weapon.CriticalMultiplier}：" +
                    "必须 >= 1.0（代码是 Damage × CriticalMultiplier；填 0.5 会让暴击更轻，填 0 则暴击零伤害）");

                Assert.IsGreaterThanOrEqualTo(0f, weapon.CriticalChance,
                    $"武器 '{weaponId}' 的 CriticalChance={weapon.CriticalChance} 不能为负");
                Assert.IsLessThanOrEqualTo(1f, weapon.CriticalChance,
                    $"武器 '{weaponId}' 的 CriticalChance={weapon.CriticalChance} 必须 <= 1（它是概率）");

                Assert.IsGreaterThan(0f, weapon.Damage,
                    $"武器 '{weaponId}' 的 Damage={weapon.Damage} 必须 > 0（否则这把枪打不死任何东西）");
                Assert.IsGreaterThan(0f, weapon.BulletSpeed,
                    $"武器 '{weaponId}' 的 BulletSpeed={weapon.BulletSpeed} 必须 > 0（否则子弹不动）");
                Assert.IsGreaterThan(0f, weapon.RangeMeters,
                    $"武器 '{weaponId}' 的 RangeMeters={weapon.RangeMeters} 必须 > 0");
                Assert.IsGreaterThan(0f, weapon.ShotCooldown,
                    $"武器 '{weaponId}' 的 ShotCooldown={weapon.ShotCooldown} 必须 > 0（0 = 无限连发）");
                Assert.IsGreaterThan(0, weapon.PelletsPerShot,
                    $"武器 '{weaponId}' 的 PelletsPerShot={weapon.PelletsPerShot} 必须 >= 1（0 = 开一枪不出子弹）");
                Assert.IsGreaterThanOrEqualTo(0f, weapon.SpreadDegrees,
                    $"武器 '{weaponId}' 的 SpreadDegrees={weapon.SpreadDegrees} 不能为负");
                Assert.IsGreaterThanOrEqualTo(0f, weapon.ProjectileRadius,
                    $"武器 '{weaponId}' 的 ProjectileRadius={weapon.ProjectileRadius} 不能为负");
            }
        }

        [TestMethod]
        public void HeroContentAssembly_ProducesNoWarnings()
        {
            // 数据层在装配解析期会收集**非致命**内容问题（如"武器 id 兼作子弹外观 id"）。
            // 这些不阻止启动，只是进 Warnings 列表 —— 但如果不钉住，它们就会变成
            // "日志里有一条没人看的警告"，等于没记录。
            //
            // 为什么这条覆盖面比单个字段断言广：将来任何内容作者踩的坑
            // （槽位越界、武器指空气、外观 id 撞车……）都会先在这里冒出来。
            var content = new CharacterContentSet().BuildBuiltinV1();

            Assert.AreEqual(0, content.Warnings.Count,
                $"内置 v1 内容不应有任何装配警告，实际 {content.Warnings.Count} 条：" +
                string.Join(" | ", content.Warnings));
        }

        [TestMethod]
        public void HeroSkillRowAccessors_ReturnNullForUnknownInputInsteadOfThrowing()
        {
            // 数据层查询入口的健壮性契约：不存在 / 越界返回 null 或空，**不抛异常**。
            // 为什么重要：宿主会在"变体被删了但存档还引用它"这类情况下查表，
            // 抛异常会把一次查询失败升级成整局崩溃；返回 null 才能让调用方降级。
            var content = new CharacterContentSet().BuildBuiltinV1();
            string heroKit = content.Assemblies.Get(CharacterContentSet.HeroDefaultVariantId).Variant.LoadoutId;

            Assert.IsNull(content.SkillRowOf("does_not_exist_kit", PrimaryAttackSlotNumber),
                "不存在的技能包必须返回 null");
            Assert.IsNull(content.SkillRowOf(heroKit, 0), "槽位号 0 越界必须返回 null");
            Assert.IsNull(content.SkillRowOf(heroKit, 99), "槽位号 99 越界必须返回 null");

            Assert.AreEqual(0, content.SkillIdsOf("does_not_exist_kit").Count,
                "不存在的技能包必须返回空列表，而不是抛异常");
        }

        /// <summary>Excel <c>SkillLoads.Slot</c> 列里 PrimaryAttack 的槽位号（1..5 制）。</summary>
        private const int PrimaryAttackSlotNumber = 2;

        /// <summary>抛射物外观 id 前缀（v1 约定：<c>proj_</c> + 武器 id）。</summary>
        private const string ProjectileIdPrefix = "proj_";

        [TestMethod]
        public void EndToEnd_PressingPrimarySpawnsRealProjectileFlyingDownAimDirection()
        {
            // 需求 4 的**完整链路**：装配 → 瞄准 +X → 按下左键 → 世界里出现真 Projectile，
            // 且它朝 +X 飞。这是"能开枪"的唯一可信证据 —— 组件齐、技能装对都不等于有子弹。
            using var loop = new GameLoop();
            loop.Boot(CharacterContentSet.HeroDefaultVariantId);

            // 瞄准 +X（模型前向 = +Z，转向由 MoveAimSystem 的 Slerp 完成）。
            loop.Input.AimDirection = Vector3.UnitX;
            loop.Step(30); // 让朝向真正收敛到 +X

            loop.Input.HeldMask = InputIntent.MaskOf(SkillSlot.PrimaryAttack);
            loop.Input.PressedMask = InputIntent.MaskOf(SkillSlot.PrimaryAttack);
            loop.Step(1); // 本帧施放
            loop.Input.PressedMask = 0u;

            // 命令是延迟提交的：世界里的子弹要等 Flush 之后才可见。
            loop.World.Commands.Flush();

            int bulletCount = loop.World.Query<Projectile, Transform3>().Count;
            Assert.IsGreaterThan(0, bulletCount, "按下左键后世界里必须出现 Projectile 实体");

            // 单发武器：按一次左键应恰好出 PelletsPerShot 颗子弹（v1 全 = 1）。
            // 这条钉住 PelletsPerShot 真的被用上了 —— 否则将来做霰弹时
            // 即使把数据改成 5，开一枪还是只出 1 颗，而且不会有任何报错。
            CharacterVariantSpec heroVariant = loop.Content.Assemblies.Get(loop.VariantId).Variant;
            CharacterWeaponSpec heroWeapon = loop.Content.Assemblies.FindWeapon(heroVariant.WeaponRowId)
                ?? throw new AssertFailedException($"武器行 '{heroVariant.WeaponRowId}' 不存在");
            Assert.AreEqual(heroWeapon.PelletsPerShot, bulletCount,
                $"按一次左键应出 {heroWeapon.PelletsPerShot} 颗子弹（武器 '{heroWeapon.Id}' 的 PelletsPerShot）");

            // 取第一颗子弹（世界里只有英雄，所以它是唯一/第一颗）。
            Entity? bullet = null;
            loop.World.Query<Projectile, Transform3>().ForEach((Span<Projectile> ps, Span<Transform3> ts) =>
            {
                for (int i = 0; i < ps.Length; i++)
                {
                    bullet = ps[i].Owner;
                }
            });

            Assert.IsNotNull(bullet, "子弹查询必须能拿到实体");
            Assert.IsTrue(loop.World.TryGet<Transform3>(bullet!.Value, out Transform3 bulletTransform),
                "子弹必须带 Transform3");

            // 子弹前向 = 旋转后的 +Z；应指向 +X（瞄准方向）。
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, bulletTransform.Rotation);
            Assert.IsGreaterThan(0.9f, forward.X,
                $"子弹必须朝 +X 飞（瞄准方向），实际前向 = ({forward.X:0.000}, {forward.Y:0.000}, {forward.Z:0.000})");

            // ── 武器侧闭环：真正决定伤害的那半 ───────────────────────────────
            //
            // 为什么这组断言必须存在（Lead 裁定为本轮最重要一条）：
            // 如果宿主工厂把 WeaponId / ProjectileId **两个参数整体传反**，
            // 那么技能实例的 DefId 会凑巧等于正确的外观 id，"外观侧"断言全绿 ——
            // 而武器数值（伤害/弹速/射程）全都来自错误的武器行。
            // 只有从**子弹实体**反查武器行，才能抓到这种手误。
            Assert.IsTrue(loop.World.TryGet<Projectile>(bullet.Value, out Projectile bulletProjectile),
                "子弹必须带 Projectile 组件");

            // ① 子弹记录的武器主键 == 该变体武器行的主键（2001，不是 0、不是别的变体的）。
            Assert.AreEqual(heroWeapon.WeaponId, bulletProjectile.WeaponId,
                $"子弹的 WeaponId 必须来自武器行 '{heroWeapon.Id}'（{heroWeapon.WeaponId}）");

            // ② 弹速/射程必须来自同一把武器 —— 用比值而非绝对值，避开"暴击浮点"等无关差异。
            Assert.AreEqual(heroWeapon.RangeMeters / heroWeapon.BulletSpeed,
                bulletProjectile.RangeMeters / bulletProjectile.Speed, 1e-4f,
                "子弹的 射程/弹速 必须同时来自武器行（分子或分母取自别的武器都会露馅）");

            // ③ 伤害：非暴击时应精确等于武器行伤害；暴击时等于 伤害 × 暴击倍率。
            //    掷骰结果在 bulletProjectile.Critical 里，所以两种分支都能独立验，
            //    不需要假设随机数种子（不去猜 RNG，只验"与它自己的暴击标记自洽"）。
            float expectedDamage = bulletProjectile.Critical
                ? heroWeapon.Damage * heroWeapon.CriticalMultiplier
                : heroWeapon.Damage;
            Assert.AreEqual(expectedDamage, bulletProjectile.Damage, 1e-3f,
                $"子弹伤害应为 {(bulletProjectile.Critical ? "暴击" : "普通")} 值 {expectedDamage}" +
                $"（武器行 '{heroWeapon.Id}'：Damage={heroWeapon.Damage}, " +
                $"CriticalMultiplier={heroWeapon.CriticalMultiplier}）");

            // ④ 判定半径同样来自武器行。
            Assert.AreEqual(heroWeapon.ProjectileRadius, bulletProjectile.Radius, 1e-4f,
                "子弹判定半径必须来自武器行的 ProjectileRadius");
        }

        /// <summary>
        /// 回归：技能包注册表的**槽位号**必须与内容集仓库一致。
        /// <para>
        /// <b>这条钉住的真实事故（2026-11-05）</b>：宿主的运行时技能包注册表原来只
        /// <c>Register</c> 了英雄自己那一个技能包，于是它的槽位是 0；而
        /// <c>ActorAssemblySystem</c> 是按 <c>Content.Loadouts</c>（内含 player_kit / goblin_kit 等历史技能包）
        /// 把 <c>LoadoutId</c> 解析成槽位号写进 <c>SkillState.LoadoutSlot</c> 的，英雄解析出来是 5。
        /// 两边错位后 <c>SkillCastSystem</c> 每帧都查不到技能包 —— <u>不报错、不崩溃、日志干净</u>，
        /// 只是<b>按左键永远不开枪</b>。这类"静默不工作"的接线错误，只有跨层断言能抓。
        /// </para>
        /// <para>
        /// 正确做法：先镜像整个仓库保住槽位号，再用真技能包<b>原地替换</b>同 id 的那一项
        /// （<see cref="SkillLoadoutRegistry.Replace"/>），槽位号不变、实现换成真的。
        /// </para>
        /// </summary>
        [TestMethod]
        public void RuntimeSkillRegistry_KeepsContentSlotNumbers()
        {
            var content = new CharacterContentSet().BuildBuiltinV1();
            var registry = new SkillLoadoutRegistry();

            // 模拟宿主 CharacterSession 的接线方式。
            // 模拟宿主 CharacterSession 的接线方式。
            for (int slot = 0; slot < content.Loadouts.Count; slot++)
            {
                SkillLoadout? existing = content.Loadouts.Get(slot);
                if (existing is not null)
                {
                    registry.Register(existing);
                }
            }
            var set = new ContentSkillSet();
            string heroKitId = CharacterContentSet.HeroDefaultVariantId + "_kit";
            var heroRows = new List<CharacterSkillSpec>();
            for (int slot = CharacterSkillSpecs.MinSlotNumber; slot <= CharacterSkillSpecs.MaxSlotNumber; slot++)
            {
                CharacterSkillSpec? row = content.SkillRowOf(heroKitId, slot);
                if (row is not null)
                {
                    heroRows.Add(row.Value);
                }
            }
            set.Build(heroKitId, heroRows, out _);
            set.PublishTo(registry);

            // ① 内容集里的每一项在原槽位上仍然查得到（槽位号没有因为"只注册自己"而整体前移）。
            for (int slot = 0; slot < content.Loadouts.Count; slot++)
            {
                SkillLoadout? expected = content.Loadouts.Get(slot);
                if (expected is null)
                {
                    continue;
                }
                SkillLoadout? actual = registry.Get(slot);
                Assert.IsNotNull(actual, $"槽位 {slot} 上的技能包 '{expected.Id}' 在运行时注册表里丢了");
                Assert.AreEqual(expected.Id, actual!.Id, $"槽位 {slot} 上的技能包必须是 '{expected.Id}'");
            }

            // ② 英雄技能包的槽位号 == 内容集里的槽位号（这是 ActorAssemblySystem 会写进组件的值）。
            int heroSlot = -1;
            for (int slot = 0; slot < content.Loadouts.Count; slot++)
            {
                if (content.Loadouts.Get(slot)?.Id == CharacterContentSet.HeroDefaultVariantId + "_kit")
                {
                    heroSlot = slot;
                    break;
                }
            }
            Assert.IsGreaterThanOrEqualTo(0, heroSlot, "内容集里必须能找到英雄技能包");

            // ③ 该槽位上已是真实现（不是 DataSkill 占位）。
            SkillLoadout heroLoadout = registry.Get(heroSlot)!;
            Assert.IsInstanceOfType<FireProjectileSkill>(heroLoadout.Get(SkillSlot.PrimaryAttack),
                $"英雄技能包槽位 {heroSlot} 的左键必须是 FireProjectileSkill");
        }
    }
}
