using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using BiuBiuOrigin.Tests.Characters;
using Game.Actor;
using Game.Combat;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Projectile = Game.Combat.Projectile;

namespace BiuBiuOrigin.Tests.Determinism
{
    // ==================================================================
    // 需求 5 的"确定性"侧面：同 seed + 同输入串 → 逐字节一致
    // ==================================================================
    //
    // 为什么英雄这一版必须补这组：移动已经引入了**平滑**（指数逼近、四元数插值），
    // 平滑是最容易写出"帧率相关"的地方。一旦某处用了 `Time.deltaTime` 的绝对值做非线性换算，
    // 同 seed 两次运行只要有 1 帧差异，位置就会永久发散 —— 回放、录像、服务器校验全部作废。
    //
    // 断言方式与既有 Determinism 组一致：**逐字节**相同，不是"近似相等"。
    // ==================================================================

    /// <summary>可复现的固定输入串：方向与瞄准按帧号查表（不依赖随机，确定性来自"输入相同"）。</summary>
    internal static class RecordedInput
    {
        /// <summary>移动方向序列：前 20 帧 +X，接下来 20 帧 −Z，再 20 帧斜向，其余静止。</summary>
        public static Vector3 MoveDirectionAt(int frame)
        {
            int phase = frame / 20;
            return phase switch
            {
                0 => new Vector3(1f, 0f, 0f),
                1 => new Vector3(0f, 0f, -1f),
                2 => new Vector3(1f, 0f, 1f),
                _ => Vector3.Zero,
            };
        }

        /// <summary>瞄准方向序列：绕 Y 轴匀速旋转（覆盖四元数插值的各种角度）。</summary>
        public static Vector3 AimDirectionAt(int frame)
        {
            float angle = frame * 0.05f;
            return new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
        }
    }

    /// <summary>按帧号回放固定输入的桩（宿主级输入包的替身）。</summary>
    internal sealed class RecordedInputSystem : SystemBase
    {
        public int Frame;
        public bool Loop;

        protected override void OnExecute(float deltaTime)
        {
            Vector3 move = RecordedInput.MoveDirectionAt(Frame);
            Vector3 aim = RecordedInput.AimDirectionAt(Frame);

            World.Query<MoveIntent>().ForEach((Span<MoveIntent> intents) =>
            {
                for (int i = 0; i < intents.Length; i++)
                {
                    intents[i].Velocity = move;
                    intents[i].Magnitude = move.LengthSquared() > 0f ? 1f : 0f;
                }
            });

            World.Query<AimTarget>().ForEach((Span<AimTarget> aims) =>
            {
                for (int i = 0; i < aims.Length; i++)
                {
                    aims[i].Direction = aim;
                    aims[i].Point = aim * 10f;
                    aims[i].HasTarget = true;
                }
            });

            Frame++;
        }
    }


    [TestClass]
    public sealed class HeroDeterminismTests
    {
        private const float Dt = 1f / 60f;
        private const int Frames = 180;

        /// <summary>
        /// 跑一次完整的英雄场景，返回**逐字节**快照（位置 + 旋转 + 速度 + 瞄准 + 存活实体清单）。
        /// <para>
        /// 为什么快照要包含旋转：移动平滑里最容易出错的就是四元数那一路，
        /// 只比位置会漏掉"位移对但朝向抖动"的 bug。
        /// </para>
        /// </summary>
        private static byte[] RunHeroScenario(string variantId)
        {
            using var world = new World();
            var content = new CharacterContentSet().BuildBuiltinV1();
            var views = new Dictionary<Entity, IReadOnlyStatView>();
            var stats = new TestMoveStatReader(world, views);

            var queue = new ActorSpawnQueue();
            var assembly = new ActorAssemblySystem(
                content.Definitions,
                content.Loadouts,
                content.StatPacks,
                new RepositoryPackageSlotResolver(content.StatPacks),
                queue);

            queue.Enqueue(new ActorSpawnRequest(content.Definitions.SlotOf(variantId), Vector3.Zero));

            var input = new RecordedInputSystem();
            var pipeline = new SystemPipeline(world);
            pipeline.Add(assembly, 0);
            pipeline.Add(input, 100);
            pipeline.Add(new MoveAimSystem(stats, stats), 200);
            pipeline.Add(new MovementSystem(), 300);
            pipeline.Build();

            pipeline.Execute(0f);
            Assert.AreEqual(1, assembly.Assembled, $"'{variantId}' 必须被装配出来（确定性用例的前提）");
            Entity hero = assembly.LastAssembled[0];

            CharacterVariantSpec row = content.Assemblies.Get(variantId).Variant;
            views[hero] = content.StatPacks.Compose(new StatPackageIds(row.BasePackageId, growth: row.GrowthPackageId));

            for (int f = 0; f < Frames; f++)
            {
                pipeline.Execute(Dt);
            }

            return Snapshot(world, hero);
        }

        private static byte[] Snapshot(World world, Entity hero)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(world.EntityCount);
                bw.Write(world.IsAlive(hero));

                Transform3 t = world.Get<Transform3>(hero);
                WriteVector(bw, t.Position);
                bw.Write(t.Rotation.X);
                bw.Write(t.Rotation.Y);
                bw.Write(t.Rotation.Z);
                bw.Write(t.Rotation.W);
                WriteVector(bw, t.Scale);

                WriteVector(bw, world.Get<Velocity>(hero).Linear);
                WriteVector(bw, world.Get<MoveIntent>(hero).Velocity);
                bw.Write(world.Get<MoveIntent>(hero).Magnitude);

                AimTarget aim = world.Get<AimTarget>(hero);
                WriteVector(bw, aim.Point);
                WriteVector(bw, aim.Direction);
                bw.Write(aim.HasTarget);
            }
            return ms.ToArray();
        }

        private static void WriteVector(BinaryWriter bw, Vector3 v)
        {
            bw.Write(v.X);
            bw.Write(v.Y);
            bw.Write(v.Z);
        }

        [TestMethod]
        [DataRow("test_hero")]
        [DataRow("test_hero_tank")]
        [DataRow("test_hero_agile")]
        [DataRow("test_hero_lucky")]
        public void SameInput_TwoRuns_ProduceByteIdenticalSnapshot(string variantId)
        {
            // 核心断言：**逐字节**相同。平滑（指数逼近 + 四元数插值）必须与"这一帧的 dt"完全绑定，
            // 不能有任何 Time/帧计数的隐藏依赖。
            byte[] first = RunHeroScenario(variantId);
            byte[] second = RunHeroScenario(variantId);

            Assert.AreEqual(first.Length, second.Length, $"[{variantId}] 两次快照长度不同");
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                {
                    Assert.Fail($"[{variantId}] 第 {i} 字节不同（{first[i]} vs {second[i]}）：同输入两次运行结果不一致，" +
                        "移动/瞄准里存在非确定性来源（回放与服务器校验会失效）");
                }
            }
        }

        [TestMethod]
        public void DifferentVariants_ProduceDifferentSnapshots()
        {
            // 反证：如果 4 个变体跑出**完全相同**的快照，说明数值包没生效，
            // 那上面的"逐字节一致"就是没有意义的（永远一样的东西当然一致）。
            byte[] standard = RunHeroScenario("test_hero");
            byte[] tank = RunHeroScenario("test_hero_tank");
            byte[] agile = RunHeroScenario("test_hero_agile");

            Assert.IsFalse(ByteEquals(standard, tank),
                "标准型与重甲型（移速/加速不同）在同输入下必须走出不同轨迹");
            Assert.IsFalse(ByteEquals(standard, agile),
                "标准型与敏捷型（移速/加速不同）在同输入下必须走出不同轨迹");
        }

        [TestMethod]
        public void DifferentFrameCounts_DivergeOnlyAfterTheShorterRun()
        {
            // 前 90 帧两次都必须一致（重复跑本身是确定的）；但 90 帧与 180 帧的最终状态**必须不同**，
            // 否则说明"跑更多帧没有改变任何东西"——那意味着位移根本没被积分（或每帧都被重置）。
            // 两个方向都断言，比只断言"一致"更有守卫价值。
            byte[] ninetyA = RunHeroScenarioForFrames("test_hero", 90);
            byte[] ninetyB = RunHeroScenarioForFrames("test_hero", 90);
            Assert.IsTrue(ByteEquals(ninetyA, ninetyB), "跑 90 帧两次必须逐字节一致");

            byte[] oneEighty = RunHeroScenarioForFrames("test_hero", 180);
            Assert.IsFalse(ByteEquals(ninetyA, oneEighty),
                "90 帧与 180 帧的最终状态必须不同：相同说明位移没有被逐帧积分");
        }

        private static byte[] RunHeroScenarioForFrames(string variantId, int frames)
        {
            using var world = new World();
            var content = new CharacterContentSet().BuildBuiltinV1();
            var views = new Dictionary<Entity, IReadOnlyStatView>();
            var stats = new TestMoveStatReader(world, views);
            var queue = new ActorSpawnQueue();
            var assembly = new ActorAssemblySystem(
                content.Definitions, content.Loadouts, content.StatPacks,
                new RepositoryPackageSlotResolver(content.StatPacks), queue);
            queue.Enqueue(new ActorSpawnRequest(content.Definitions.SlotOf(variantId), Vector3.Zero));

            var input = new RecordedInputSystem();
            var pipeline = new SystemPipeline(world);
            pipeline.Add(assembly, 0);
            pipeline.Add(input, 100);
            pipeline.Add(new MoveAimSystem(stats, stats), 200);
            pipeline.Add(new MovementSystem(), 300);
            pipeline.Build();
            pipeline.Execute(0f);
            Entity hero = assembly.LastAssembled[0];
            CharacterVariantSpec row = content.Assemblies.Get(variantId).Variant;
            views[hero] = content.StatPacks.Compose(new StatPackageIds(row.BasePackageId, growth: row.GrowthPackageId));

            for (int f = 0; f < frames; f++)
            {
                pipeline.Execute(Dt);
            }
            return Snapshot(world, hero);
        }

        [TestMethod]
        public void ProjectileFlight_IsDeterministic()
        {
            // 子弹也必须在同输入下逐字节一致（它是回放里最重要的"证据"：
            // 两次回放子弹落点不同 = 命中判定不可信）。
            byte[] first = RunProjectileScenario();
            byte[] second = RunProjectileScenario();
            Assert.IsTrue(ByteEquals(first, second), "同输入两次的子弹轨迹必须逐字节一致");
        }

        private static byte[] RunProjectileScenario()
        {
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            pipeline.Add(new ProjectileSystem(), 0);
            pipeline.Add(new FlushSystem(), 1);
            pipeline.Build();

            // 三颗不同朝向的子弹（覆盖旋转的不同象限）。
            var directions = new[]
            {
                Vector3.UnitZ,
                Vector3.Normalize(new Vector3(1f, 0f, 1f)),
                Vector3.Normalize(new Vector3(-1f, 0f, 0.5f)),
            };

            for (int i = 0; i < directions.Length; i++)
            {
                Vector3 forward = directions[i];
                Quaternion rotation = Quaternion.CreateFromAxisAngle(
                    Vector3.UnitY, MathF.Atan2(forward.X, forward.Z));
                Entity bullet = world.Create(
                    new Transform3 { Position = new Vector3(i, 0f, 0f), Rotation = rotation, Scale = Vector3.One },
                    new Projectile { Speed = 12f, RangeMeters = 50f });
                world.Set(bullet, new Lifetime { Remaining = 10f, Duration = 10f });
            }

            for (int f = 0; f < 90; f++)
            {
                pipeline.Execute(Dt);
            }

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(world.EntityCount);
                var buffer = new List<Vector3>();
                world.Query<Transform3>().ForEach((Span<Transform3> transforms) =>
                {
                    for (int i = 0; i < transforms.Length; i++)
                    {
                        buffer.Add(transforms[i].Position);
                    }
                });
                // 按 X 排序：chunk 顺序是稳定的，但排序让快照与"实体插入顺序"解耦，
                // 更严格地验证"位置本身可复现"而不是"遍历顺序可复现"。
                buffer.Sort((a, b) => a.X.CompareTo(b.X));
                foreach (Vector3 p in buffer)
                {
                    WriteVector(bw, p);
                }
            }
            return ms.ToArray();
        }

        private static bool ByteEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
