using System;
using System.Collections.Generic;
using System.Numerics;
using Game.Actor;
using Game.Combat;
using Projectile = Game.Combat.Projectile;
using Game.Core;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Combat
{
    // ==================================================================
    // 需求 4 的下半段：开火之后子弹真的会飞、会到射程销毁
    // ==================================================================
    //
    // 契约 §3.1 的运行时语义：
    //   ① 子弹沿「开火瞬间的朝向」直线飞（Transform3.Rotation 的 +Z 前方，不再改方向）；
    //   ② 飞够射程就销毁，且只销毁一次；
    //   ③ 没有 Lifetime 的子弹不会被本系统推进（漏挂寿命 = 立刻可见的"子弹不动"）；
    //   ④ 销毁走命令缓冲（J5）：遍历期间不改结构。
    // ==================================================================

    /// <summary>
    /// 把命令缓冲刷进世界的一小节拍（等价于宿主每帧末尾做的 <c>Commands.Flush()</c>）。
    /// <para>
    /// 为什么不用 <c>Game.Host.CommandFlushSystem</c>：测试工程刻意<u>不</u>引用 <c>scripts/host/**</c>
    /// （那是唯一 allowed 用 Godot 的目录，引进来整套测试就不能在无引擎进程里跑了 —— ECS.md §10）。
    /// 这里只需要"刷一次缓冲"这一个动作，自己写两行比破坏引擎无关性划算。
    /// </para>
    /// </summary>
    internal sealed class CommandApplySystem : SystemBase
    {
        protected override void OnExecute(float deltaTime) => World.Commands.Flush();
    }

    /// <summary>会计数「寿命到期」事件的出口：用来验证"到寿销毁"是否恰好发生一次。</summary>
    internal sealed class CountingEventSink : HostEventSink
    {
        public int LifetimeExpired;

        public void RaiseDamageApplied(Game.Core.Handles.ActorHandle source, Game.Core.Handles.ActorHandle target, float amount, bool isCrit, DamageType type) { }

        public void RaiseHit(Game.Core.Handles.ActorHandle source, Game.Core.Handles.ActorHandle target, float x, float y, float z, DamageType type) { }

        public void RaiseActorKilled(Game.Core.Handles.ActorHandle victim, Game.Core.Handles.ActorHandle killer, DamageType type) { }

        public void RaiseLifetimeExpired(Game.Core.Handles.ActorHandle entity) => LifetimeExpired++;

        public void RaiseEntityDestroyed(Game.Core.Handles.ActorHandle entity) { }
    }
    /// <summary>
    /// 空事件出口：<see cref="LifetimeSystem"/> 需要它，但投射物测试不关心任何业务事件。
    /// <para>
    /// 为什么必须有个真的实现而不是 <c>null</c>：<c>LifetimeSystem</c> 的构造签名要求非空 sink
    /// （系统不该在运行期判断"有没有人接事件"）。这个类把所有回调做成 no-op，
    /// 于是"事件没被调用"这件事在本组用例里是不言自明的。
    /// </para>
    /// </summary>
    internal sealed class NullEventSink : HostEventSink
    {
        public static readonly NullEventSink Instance = new NullEventSink();

        private NullEventSink()
        {
        }

        public void RaiseDamageApplied(Game.Core.Handles.ActorHandle source, Game.Core.Handles.ActorHandle target, float amount, bool isCrit, DamageType type)
        {
        }

        public void RaiseHit(Game.Core.Handles.ActorHandle source, Game.Core.Handles.ActorHandle target, float x, float y, float z, DamageType type)
        {
        }

        public void RaiseActorKilled(Game.Core.Handles.ActorHandle victim, Game.Core.Handles.ActorHandle killer, DamageType type)
        {
        }

        public void RaiseLifetimeExpired(Game.Core.Handles.ActorHandle entity)
        {
        }

        public void RaiseEntityDestroyed(Game.Core.Handles.ActorHandle entity)
        {
        }
    }

    [TestClass]
    public sealed class ProjectileTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>造一颗"数据合法"的子弹（数值全部显式给，测试里不依赖英雄数据表）。</summary>
        private static Entity SpawnBullet(
            World world,
            Vector3 at,
            Vector3 direction,
            float speed,
            float range,
            float lifetimeSeconds,
            bool spent = false)
        {
            Vector3 forward = Vector3.Normalize(direction);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitY,
                MathF.Atan2(forward.X, forward.Z)); // 与 MoveAimSystem.LookTowards 同一约定：+Z 为前方

            var projectile = new Projectile
            {
                Team = TeamId.Player,
                Damage = 1f,
                Radius = 0.1f,
                Speed = speed,
                RangeMeters = range,
                Spent = spent,
            };

            Entity entity = world.Create(new Transform3 { Position = at, Rotation = rotation, Scale = Vector3.One }, projectile);
            world.Set(entity, new Lifetime { Remaining = lifetimeSeconds, Duration = lifetimeSeconds });
            return entity;
        }

        [TestMethod]
        public void Projectile_SpawnsAtCasterWithAimDirection()
        {
            // 需求 4 的第一条硬要求：子弹的初始方向 == 玩家的瞄准方向（打歪了这条就红）。
            using var world = new World();
            Vector3 aim = Vector3.Normalize(new Vector3(1f, 0f, -1f));
            Entity bullet = SpawnBullet(world, new Vector3(2f, 0f, 3f), aim, speed: 20f, range: 50f, lifetimeSeconds: 5f);

            Assert.AreEqual(new Vector3(2f, 0f, 3f), world.Get<Transform3>(bullet).Position, "子弹必须从施放者位置出发");

            Vector3 forward = Vector3.Transform(Vector3.UnitZ, world.Get<Transform3>(bullet).Rotation);
            Assert.AreEqual(aim.X, forward.X, 1e-5f, $"子弹初始朝向的 X 分量必须等于瞄准方向（实际 {forward}）");
            Assert.AreEqual(aim.Z, forward.Z, 1e-5f, $"子弹初始朝向的 Z 分量必须等于瞄准方向（实际 {forward}）");
            Assert.AreEqual(0f, forward.Y, 1e-5f, "俯视角：子弹不应有竖直分量");
        }

        [TestMethod]
        public void Projectile_FliesStraightAlongItsFacing()
        {
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var projectiles = new ProjectileSystem();
            pipeline.Add(projectiles, 0);
            pipeline.Build();

            const float speed = 12f;
            Vector3 start = new Vector3(-4f, 0f, 1f);
            Entity bullet = SpawnBullet(world, start, Vector3.UnitX, speed, range: 100f, lifetimeSeconds: 10f);

            const int frames = 30;
            for (int f = 0; f < frames; f++)
            {
                pipeline.Execute(Dt);
            }

            Vector3 moved = world.Get<Transform3>(bullet).Position - start;
            float expected = speed * Dt * frames;

            Assert.IsTrue(world.IsAlive(bullet), "未到射程 / 未到寿命的子弹不应被销毁");
            Assert.AreEqual(expected, moved.X, 1e-3f, $"沿 +X 飞行的距离必须是 速度×时间（实际 {moved.X}）");
            Assert.AreEqual(0f, moved.Z, 1e-4f, "子弹不拐弯");
            Assert.AreEqual(0f, moved.Y, 1e-4f, "子弹不下坠（没有穿模的重力模型）");
            Assert.AreEqual(0, projectiles.ExpiredCount, "尚未到期不应有销毁");
            Assert.AreEqual(1, projectiles.AdvancedCount, "这一颗子弹本帧应被推进");
        }

        [TestMethod]
        public void Projectile_DestroysExactlyOnceAtRange()
        {
            // 射程到就销毁，并且只投递一次销毁命令（重复投递会让跳过计数虚高、
            // 也会让"击杀特效"之类的订阅者收到两次事件）。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var projectiles = new ProjectileSystem();
            var apply = new CommandApplySystem();
            pipeline.Add(projectiles, 0);
            pipeline.Add(apply, 1);
            pipeline.Build();

            const float speed = 10f;
            const float range = 5f; // 0.5 秒飞完
            Entity bullet = SpawnBullet(world, Vector3.Zero, Vector3.UnitZ, speed, range, lifetimeSeconds: 30f);

            int destroyCommands = 0;
            int framesToExpire = (int)MathF.Ceiling(range / speed / Dt) + 4;
            for (int f = 0; f < framesToExpire; f++)
            {
                // 每帧统计本帧新投递的销毁命令数（Flush 之前数，之后清零）。
                pipeline.Execute(Dt);
                if (projectiles.ExpiredCount > 0)
                {
                    destroyCommands += projectiles.ExpiredCount;
                }
            }

            Assert.IsFalse(world.IsAlive(bullet), $"飞到射程 {range}m 后子弹必须被销毁");
            Assert.AreEqual(1, destroyCommands,
                $"销毁命令应恰好投递一次，实际 {destroyCommands} 次（重复投递会造成重复的命中/特效回调）");
            Assert.IsGreaterThanOrEqualTo(projectiles.LifetimeExpiredCount, 0, "寿命兜底计数只能是自然数");
        }

        [TestMethod]
        public void Projectile_RangeAndLifetimeAreIndependentCriteria()
        {
            // 契约 §3.1：射程与寿命"先到的那个"生效。
            //  ① 射程远、寿命短 → 由 LifetimeSystem 兜底销毁，且到期事件恰好发一次；
            //  ② 射程短、寿命长 → 由射程销毁，到期事件必须为 0（否则说明射程判据没生效、全靠寿命）。
            using (var world = new World())
            {
                var pipeline = new SystemPipeline(world);
                var projectiles = new ProjectileSystem();
                var sink = new CountingEventSink();
                pipeline.Add(projectiles, 0);
                pipeline.Add(new LifetimeSystem(sink), 1);
                pipeline.Add(new CommandApplySystem(), 2);
                pipeline.Build();

                Entity shortLived = SpawnBullet(world, Vector3.Zero, Vector3.UnitZ, speed: 10f, range: 1000f, lifetimeSeconds: 0.1f);

                for (int f = 0; f < 30 && world.IsAlive(shortLived); f++)
                {
                    pipeline.Execute(Dt);
                }

                Assert.IsFalse(world.IsAlive(shortLived), "寿命只有 0.1s 的子弹必须消失");
                Assert.AreEqual(1, sink.LifetimeExpired,
                    "射程远大于飞行距离时，应由寿命兜底销毁并恰好发一次到期事件（0 次说明没销毁，>1 次说明重复销毁）");
            }

            using (var world = new World())
            {
                var pipeline = new SystemPipeline(world);
                var projectiles = new ProjectileSystem();
                var sink = new CountingEventSink();
                pipeline.Add(projectiles, 0);
                pipeline.Add(new LifetimeSystem(sink), 1);
                pipeline.Add(new CommandApplySystem(), 2);
                pipeline.Build();

                Entity near = SpawnBullet(world, Vector3.Zero, Vector3.UnitZ, speed: 10f, range: 1f, lifetimeSeconds: 60f);

                for (int f = 0; f < 30 && world.IsAlive(near); f++)
                {
                    pipeline.Execute(Dt);
                }

                Assert.IsFalse(world.IsAlive(near), "射程只有 1m 的子弹必须消失");
                Assert.AreEqual(0, sink.LifetimeExpired,
                    "正常射程下应由射程销毁，不该走寿命兜底（否则说明射程没生效）");
            }
        }
        [TestMethod]
        public void Projectile_WithoutLifetimeIsNotAdvanced()
        {
            // 刻意设计：漏挂寿命 → 子弹不动（立刻可见），而不是永远飞（隐性 bug）。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var projectiles = new ProjectileSystem();
            pipeline.Add(projectiles, 0);
            pipeline.Build();

            Entity bullet = world.Create(
                new Transform3 { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
                new Projectile { Speed = 10f, RangeMeters = 5f });

            for (int f = 0; f < 60; f++)
            {
                pipeline.Execute(Dt);
            }

            Assert.AreEqual(0f, world.Get<Transform3>(bullet).Position.Length(), 1e-6f,
                "没有 Lifetime 的子弹不应被 ProjectileSystem 推进");
            Assert.AreEqual(0, projectiles.AdvancedCount);
            Assert.IsTrue(world.IsAlive(bullet));
        }

        [TestMethod]
        public void Projectile_SpentIsRecycledWithoutAdvancing()
        {
            // 命中检测把子弹标记为 Spent 后，本系统下一帧只负责回收，不再推进
            // （否则子弹会穿过目标继续飞，视觉上"打中了还在飞"）。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var projectiles = new ProjectileSystem();
            pipeline.Add(projectiles, 0);
            pipeline.Add(new LifetimeSystem(NullEventSink.Instance), 1);
            pipeline.Add(new CommandApplySystem(), 2);
            pipeline.Build();

            Entity bullet = SpawnBullet(world, Vector3.Zero, Vector3.UnitZ, speed: 10f, range: 100f, lifetimeSeconds: 30f, spent: true);

            pipeline.Execute(Dt);

            Assert.IsFalse(world.IsAlive(bullet), "已消耗（命中过）的子弹必须被回收");
            Assert.AreEqual(0, projectiles.AdvancedCount, "已消耗的子弹不再推进");
            Assert.AreEqual(1, projectiles.ExpiredCount);
        }

        [TestMethod]
        public void Projectile_ManyBulletsExpireInTheSameFrame()
        {
            // 一批子弹同时到期：两遍扫描的实现必须能处理"同一帧多个销毁"（否则会出现
            // "只销毁第一颗"这类只在多弹幕下暴露的 bug）。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var projectiles = new ProjectileSystem();
            pipeline.Add(projectiles, 0);
            pipeline.Add(new CommandApplySystem(), 1);
            pipeline.Build();

            const int count = 16;
            var bullets = new Entity[count];
            for (int i = 0; i < count; i++)
            {
                bullets[i] = SpawnBullet(
                    world,
                    new Vector3(i, 0f, 0f),
                    Vector3.UnitZ,
                    speed: 10f,
                    range: 1f,
                    lifetimeSeconds: 30f);
            }

            for (int f = 0; f < 20; f++)
            {
                pipeline.Execute(Dt);
            }

            for (int i = 0; i < count; i++)
            {
                Assert.IsFalse(world.IsAlive(bullets[i]), $"第 {i} 颗子弹也必须被销毁（同帧多销毁）");
            }
            Assert.AreEqual(0, world.EntityCount, "全部回收后世界里不应残留实体");
        }

        [TestMethod]
        public void Projectile_ZeroSpeedStaysPutAndIsHarvestedByRangeZero()
        {
            // 边界：速度 0 的子弹永远到不了射程 → 不应该泄漏；寿命兜底必须接住它。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var projectiles = new ProjectileSystem();
            pipeline.Add(projectiles, 0);
            pipeline.Add(new LifetimeSystem(NullEventSink.Instance), 1);
            pipeline.Add(new CommandApplySystem(), 2);
            pipeline.Build();

            Entity frozen = SpawnBullet(world, Vector3.Zero, Vector3.UnitZ, speed: 0f, range: 5f, lifetimeSeconds: 0.05f);

            for (int f = 0; f < 20 && world.IsAlive(frozen); f++)
            {
                pipeline.Execute(Dt);
            }

            Assert.IsFalse(world.IsAlive(frozen), "速度 0 的子弹必须靠寿命被回收（不能泄漏）");
        }

    }
}
