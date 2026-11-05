using System;
using System.Collections.Generic;
using System.Numerics;
using BiuBiuOrigin.Tests.Characters;
using BiuBiuOrigin.Tests.Combat;
using Game.Actor;
using Game.Combat;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Projectile = Game.Combat.Projectile;

namespace BiuBiuOrigin.Tests.Allocation
{
    // ==================================================================
    // 需求 5 的"零分配"侧面：英雄的热路径每帧不能产生 GC 垃圾
    // ==================================================================
    //
    // ECS.md §10 判据：`Query.ForEach` 稳态分配 = 0（`GC.GetAllocatedBytesForCurrentThread` 前后差）。
    // 英雄这一版新增了三类热路径，都必须过这条：
    //   ① MoveAimSystem（意图归一化 + 指数逼近 + 四元数插值）
    //   ② MovementSystem（积分）
    //   ③ ProjectileSystem（推进 + 到期回收，含"同帧多销毁"的复用数组）
    //
    // 测量纪律（与既有 ZeroAllocationTests 一致）：
    //   1) 先 warm-up 吃掉 JIT / 首次字典查找 / chunk 扩容 / 数组扩容；
    //   2) 再取 before，跑 N 轮，取 after，断言 == 0；
    //   3) 用每线程分配字节，避免别的线程干扰；
    //   4) **第一次测量的结果不算数**时不能放宽容差 —— 而是要查清是谁在分配。
    // ==================================================================

    [TestClass]
    public sealed class HeroZeroAllocationTests
    {
        private const float Dt = 1f / 60f;
        private const int WarmupIterations = 24;
        private const int MeasureIterations = 64;

        /// <summary>
        /// 一帧要零分配的完整英雄管线（输入桩 → 移动/瞄准 → 积分）。
        /// <para>输入桩每帧只写 Span，不开任何新对象 —— 与宿主的意图采集同形状。</para>
        /// </summary>
        private static void RunHeroFrames(SystemPipeline pipeline, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                pipeline.Execute(Dt);
            }
        }

        private static (World World, SystemPipeline Pipeline) BuildHeroPipeline(out Entity hero)
        {
            var world = new World();
            var content = new CharacterContentSet().BuildBuiltinV1();
            var views = new Dictionary<Entity, IReadOnlyStatView>();
            var stats = new TestMoveStatReader(world, views);

            var queue = new ActorSpawnQueue();
            var assembly = new ActorAssemblySystem(
                content.Definitions, content.Loadouts, content.StatPacks,
                new RepositoryPackageSlotResolver(content.StatPacks), queue);
            queue.Enqueue(new ActorSpawnRequest(content.Definitions.SlotOf(CharacterContentSet.HeroDefaultVariantId), Vector3.Zero));

            var pipeline = new SystemPipeline(world);
            pipeline.Add(assembly, 0);
            pipeline.Add(new ConstantMoveStub(), 100);
            pipeline.Add(new MoveAimSystem(stats, stats), 200);
            pipeline.Add(new MovementSystem(), 300);
            pipeline.Build();
            pipeline.Execute(0f);

            Entity spawned = assembly.LastAssembled[0];
            CharacterVariantSpec row = content.Assemblies.Get(CharacterContentSet.HeroDefaultVariantId).Variant;
            views[spawned] = content.StatPacks.Compose(new StatPackageIds(row.BasePackageId, growth: row.GrowthPackageId));
            hero = spawned;
            return (world, pipeline);
        }

        /// <summary>
        /// 每帧写同一方向的输入桩（零分配）。
        /// <para>
        /// <b>为什么用 <see cref="ISpanAction{T}"/> 接口字段而不是 lambda</b>：`ForEach((Span&lt;T&gt; s) =&gt; ...)`
        /// 每次调用都会**新建一个闭包委托实例**（每帧 500 字节以上）—— 这正是本组用例第一版失败的根因。
        /// 内核提供接口回调重载就是为了避开它（MovementSystem / ProjectileSystem 都这么写）。
        /// </para>
        /// </summary>
        [UpdateInGroup(UpdateGroup.PreUpdate)]
        private sealed class ConstantMoveStub : SystemBase, ISpanAction<MoveIntent>, ISpanAction<AimTarget>
        {
            private readonly ISpanAction<MoveIntent> _moveAction;
            private readonly ISpanAction<AimTarget> _aimAction;

            // 查询在 OnInitialize 里建一次并缓存 —— 与 MoveAimSystem / MovementSystem / ProjectileSystem
            // 同形。`World.Query<T>()` 内部会 NormalizeCopy 出新数组，<u>每次调用都分配</u>；
            // 若每帧在 OnExecute 里现调，就等于把分配引进热路径，本用例断言的"0"永远过不了。
            private Query _moveQuery = null!;
            private Query _aimQuery = null!;

            public ConstantMoveStub()
            {
                _moveAction = this;
                _aimAction = this;
            }

            protected override void OnInitialize()
            {
                _moveQuery = World.Query<MoveIntent>();
                _aimQuery = World.Query<AimTarget>();
            }

            protected override void OnExecute(float deltaTime)
            {
                _moveQuery.ForEach<MoveIntent>(in _moveAction);
                _aimQuery.ForEach<AimTarget>(in _aimAction);
            }

            void ISpanAction<MoveIntent>.Execute(Span<MoveIntent> intents, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    intents[i].Velocity = new Vector3(1f, 0f, 0f);
                    intents[i].Magnitude = 1f;
                }
            }

            void ISpanAction<AimTarget>.Execute(Span<AimTarget> aims, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    aims[i].Direction = new Vector3(1f, 0f, 1f);
                    aims[i].HasTarget = true;
                }
            }
        }

        [TestMethod]
        public void Hero_MoveAndAim_SteadyStateAllocatesNothing()
        {
            (World world, SystemPipeline pipeline) = BuildHeroPipeline(out Entity hero);
            using (world)
            {
                Assert.IsTrue(world.IsAlive(hero));

                // warm-up：JIT、首次 EnsureInitialized、四元数路径的静态初始化都在这之前完成。
                RunHeroFrames(pipeline, WarmupIterations);

                long before = GC.GetAllocatedBytesForCurrentThread();
                RunHeroFrames(pipeline, MeasureIterations);
                long after = GC.GetAllocatedBytesForCurrentThread();

                // 与既有用例一致的口径：热路径稳态必须**恰好** 0。
                // 这里不放宽容差：528 字节/帧看似无害，但 60fps 下是每秒 31KB 的 GC 压力，
                // 而且它的存在说明某个"每帧构造"的东西被引进了 ECS 循环 —— 必须查清是什么。
                Assert.AreEqual(0L, after - before, $"{BuildAllocationReport(pipeline, after - before)}");
            }
        }

        private static string BuildAllocationReport(SystemPipeline pipeline, long delta) =>
            $"奔跑 {MeasureIterations} 帧分配了 {delta} 字节（{delta / (double)MeasureIterations:0.0} 字节/帧）；" +
            "移动/瞄准热路径里出现了分配（闭包、装箱、LINQ、或每帧 new 了容器）";

        [TestMethod]
        public void Hero_MeasurementHarnessActuallySeesAllocations()
        {
            // 自检：如果这个测量方法根本看不到分配，上面的"0"就是假的。
            // 故意制造一次分配，确认测量口径有效。
            long before = GC.GetAllocatedBytesForCurrentThread();
            object garbage = new byte[1024];
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.IsGreaterThan(0L, after - before,
                "测量口径失效：连主动分配 1KB 都测不出来，零分配断言没有意义");
            GC.KeepAlive(garbage);
        }

        [TestMethod]
        public void Projectile_AdvanceAndExpire_SteadyStateAllocatesNothing()
        {
            // 关键在"到期回收"那一路：实现用的是系统字段上复用的 List（只 Clear 不重分配）。
            // 用例特意制造大量同帧到期，逼它走扩容路径 —— 扩容必须发生在 warm-up 里。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            pipeline.Add(new ProjectileSystem(), 0);
            pipeline.Add(new FlushSystem(), 1);
            pipeline.Build();

            // 24 颗子弹：射程设得很远（不会因射程判死），寿命给足，测量期间持续飞行。
            for (int i = 0; i < 24; i++)
            {
                Entity bullet = world.Create(
                    new Transform3
                    {
                        Position = new Vector3(i, 0f, 0f),
                        Rotation = Quaternion.Identity,
                        Scale = Vector3.One,
                    },
                    new Projectile { Speed = 10f, RangeMeters = 1000f });
                world.Set(bullet, new Lifetime { Remaining = 100f, Duration = 100f });
            }

            for (int i = 0; i < WarmupIterations; i++)
            {
                pipeline.Execute(Dt);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasureIterations; i++)
            {
                pipeline.Execute(Dt);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                $"子弹推进 {MeasureIterations} 帧分配了 {after - before} 字节（到期回收数组应当复用）");
        }

        [TestMethod]
        public void Projectile_LifetimeExpiry_IsOwnedByProjectileSystem()
        {
            // 回归：子弹的"寿命到"必须由 ProjectileSystem 自己判定并销毁。
            // 历史 bug：子弹曾经挂通用 Lifetime 组件、由 LifetimeSystem（Update 组）倒计时并销毁，
            // 而 ProjectileSystem 在 FixedUpdate 组 —— 寿命在 Update 归零后实体当场被销毁，
            // 下一个 FixedUpdate 的查询里已经没有这颗子弹，于是它的寿命判定与 LifetimeExpiredCount
            // 永远不触发（计数恒为 0，诊断信号完全骗人）。
            // 现在三条死法（命中/射程/寿命）全在 ProjectileSystem 一个系统里判。
            using var world = new World();
            var projectiles = new ProjectileSystem();
            var pipeline = new SystemPipeline(world);
            pipeline.Add(projectiles, 0);
            pipeline.Add(new LifetimeSystem(NullEventSink.Instance, selfManaged: e => world.Has<Projectile>(e)), 1);
            pipeline.Add(new FlushSystem(), 2);
            pipeline.Build();

            // 射程设得足够远（不会被射程判死），速度 0（原地不动），只让寿命到期。
            Entity bullet = world.Create(
                new Transform3 { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
                new Projectile { Speed = 0f, RangeMeters = 1000f });
            world.Set(bullet, new Lifetime { Remaining = 3f * Dt, Duration = 3f * Dt });

            Assert.AreEqual(0, projectiles.LifetimeExpiredCount, "前提：还没跑帧，计数为 0");

            for (int i = 0; i < 4; i++)
            {
                pipeline.Execute(Dt);
            }

            Assert.AreEqual(1, projectiles.LifetimeExpiredCount,
                "寿命到期必须由 ProjectileSystem 计数（曾经恒为 0）");
            Assert.AreEqual(1, projectiles.ExpiredCount, "同一颗子弹只应销毁一次");
            Assert.IsFalse(world.IsAlive(bullet), "寿命到期后实体必须被销毁");
        }

        [TestMethod]
        public void Hero_ManySimultaneousExpiries_DoNotAllocate()
        {
            // 把"同帧多销毁"逼到超过初始容量（32），验证扩容也发生在 warm-up 内、
            // 且稳态下不再增长。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            pipeline.Add(new ProjectileSystem(), 0);
            pipeline.Add(new LifetimeSystem(NullEventSink.Instance, selfManaged: e => world.Has<Projectile>(e)), 1);
            pipeline.Add(new FlushSystem(), 2);
            pipeline.Build();

            const int Count = 48; // > ProjectileSystem 的初始容量 32，强制扩容一次
            for (int i = 0; i < Count; i++)
            {
                // 子弹必须挂 Lifetime（ProjectileSystem 的查询是 <Projectile, Transform3>.With<Lifetime>，
                // 没挂 Lifetime 的子弹压根不被推进）；倒计时与销毁归 LifetimeSystem。
                // 这里给 0.1 秒，让它们在 warm-up 的前几帧集中到期 —— 一次性把 _expired 撑过初始容量 32。
                Entity bullet = world.Create(
                    new Transform3 { Position = new Vector3(i, 0f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
                    new Projectile { Speed = 10f, RangeMeters = 100f });
                world.Set(bullet, new Lifetime { Remaining = 0.1f, Duration = 0.1f });
            }

            // warm-up 期间它们全部到期并销毁（含扩容）。
            for (int i = 0; i < WarmupIterations; i++)
            {
                pipeline.Execute(Dt);
            }
            Assert.AreEqual(0, world.EntityCount, "全部子弹必须在 warm-up 内被回收");

            // 稳态：世界里没有子弹了，跑空帧不应分配。
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasureIterations; i++)
            {
                pipeline.Execute(Dt);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before, $"空场景跑 {MeasureIterations} 帧仍分配了 {after - before} 字节");
        }
    }
}
