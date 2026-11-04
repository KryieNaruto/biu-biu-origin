using System;
using System.IO;
using System.Text;
using Game.Core;
using Game.Core.Ecs;
using Game.Core.Randomness;
using BiuBiuOrigin.Tests.Kernel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Determinism
{
    // ==================================================================
    // 7. 确定性：同 seed 两次跑同一系统序列，组件快照逐字节一致
    // ==================================================================
    //
    // ECS.md §10：「同 seed 两次跑同一系统序列，**组件快照逐字节一致**」。
    // 架构要求（§8 随机行）：系统构造函数必须从 WorldHost 注入 IRandomProvider。
    // 这里用一个最小但真实的系统族：生成系统按随机数散布实体，移动系统推进位置，
    // 生命周期系统递减计时并把到期的实体排进 CommandBuffer。
    //
    // 断言方式：
    //   1) 两次运行的组件快照**逐字节**相同（不是「近似相等」）；
    //   2) 同 seed 的两个 DeterministicRandom 序列完全相同；
    //   3) 不同 seed 必须产出不同结果（否则「确定性」可能是假象：什么都没用上 seed）；
    //   4) Fork 子流不受父流推进行为影响（独立可复现）。
    // ==================================================================

    /// <summary>确定性测试用例用的计时组件。</summary>
    public struct LifeTimer
    {
        /// <summary>实体身份（布局上必须第一个，ECS.md §4.2 铁律 3）。</summary>
        [Identity]
        public Entity Self;

        /// <summary>剩余时间。</summary>
        public float Remaining;
    }

    /// <summary>确定性测试用的生成系统：按注入的随机源散布实体。</summary>
    public sealed class DeterministicSpawnSystem : SystemBase
    {
        private readonly IRandomProvider _random;
        private readonly int _count;
        private readonly float _radius;

        public DeterministicSpawnSystem(IRandomProvider random, int count, float radius)
        {
            _random = random;
            _count = count;
            _radius = radius;
        }

        protected override void OnExecute(float deltaTime)
        {
            for (int i = 0; i < _count; i++)
            {
                float x = (_random.NextFloat() - 0.5f) * _radius;
                float y = (_random.NextFloat() - 0.5f) * _radius;
                float vx = (_random.NextFloat() - 0.5f) * _radius;
                World.Create(
                    new Pos { X = x, Y = y, Z = 0f, Self = Entity.None },
                    new Vel { X = vx, Self = Entity.None });
            }
        }
    }

    /// <summary>确定性测试用的移动系统：Transform3 += Velocity * dt（纯计算，不分配）。</summary>
    public sealed class DeterministicMoveSystem : SystemBase
    {
        /// <inheritdoc />
        protected override void OnExecute(float deltaTime)
        {
            float dt = deltaTime;
            World.Query<Pos, Vel>().ForEach((Span<Pos> pos, Span<Vel> vel) =>
            {
                for (int i = 0; i < pos.Length; i++)
                {
                    pos[i].X += vel[i].X * dt;
                }
            });
        }
    }

    [TestClass]
    public sealed class DeterminismTests
    {
        private const int Seed = 1234;
        private const int SpawnCount = 40;
        private const int Ticks = 16;

        /// <summary>
        /// 跑一遍完整场景，返回「组件快照的字节序列」。
        /// 快照 = 按实体创建顺序，逐个写出 Pos 与 Vel 的原始字节。
        /// </summary>
        private static byte[] RunScenario(int seed)
        {
            using World world = new World();
            IRandomProvider random = new DeterministicRandom(seed);

            SystemPipeline pipeline = new SystemPipeline(world);
            pipeline.Add(new DeterministicSpawnSystem(random, SpawnCount, 100f), 0);
            pipeline.Add(new DeterministicMoveSystem(), 1);
            pipeline.Build();

            // 只跑一帧生成，之后只跑移动 —— 这样快照完全由「生成用的随机数 + 确定性的浮点运算」决定。
            pipeline.Execute(0f);

            for (int t = 0; t < Ticks; t++)
            {
                pipeline.Execute(1f / 60f);
            }

            return SnapshotPositions(world);
        }

        private static byte[] SnapshotPositions(World world)
        {
            // 组件快照必须是「稳定顺序」的：查询顺序未承诺稳定（ECS.md §5.3），
            // 所以按实体号排序后再序列化，避免把「遍历顺序」误当成确定性的一部分。
            int count = world.Stats.EntityCount;
            Entity[] entities = new Entity[count];
            int n = 0;
            world.Query<Pos, Vel>().ForEach((Span<Pos> pos, Span<Vel> vel) =>
            {
                for (int i = 0; i < pos.Length; i++)
                {
                    if (n < entities.Length)
                    {
                        entities[n++] = pos[i].Self;
                    }
                }
            });
            Assert.AreEqual(count, n, "快照：查询到的实体数必须与 Stats.EntityCount 一致。");

            Array.Sort(entities, static (a, b) =>
            {
                int byIndex = a.Index.CompareTo(b.Index);
                return byIndex != 0 ? byIndex : a.Generation.CompareTo(b.Generation);
            });

            using MemoryStream ms = new MemoryStream();
            using BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < entities.Length; i++)
            {
                Pos pos = world.Get<Pos>(entities[i]);
                Vel vel = world.Get<Vel>(entities[i]);
                bw.Write(pos.X);
                bw.Write(pos.Y);
                bw.Write(pos.Z);
                bw.Write(vel.X);
            }

            bw.Flush();
            return ms.ToArray();
        }

        [TestMethod]
        public void SameSeed_TwoRuns_ProduceByteIdenticalComponentSnapshot()
        {
            byte[] first = RunScenario(Seed);
            byte[] second = RunScenario(Seed);

            Assert.AreEqual(first.Length, second.Length,
                "同 seed 两次运行的快照长度必须一致（长度不同说明实体数都不确定了）。");

            // 逐字节比较，并把第一处差异的位置报出来（不是简单的 SequenceEqual，便于定位）。
            int mismatch = -1;
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                {
                    mismatch = i;
                    break;
                }
            }

            Assert.AreEqual(-1, mismatch,
                "同 seed 两次运行的组件快照必须逐字节一致（ECS.md §10 确定性要求）；" +
                (mismatch >= 0 ? ("第一处差异在字节偏移 " + mismatch + "。") : string.Empty));
        }

        [TestMethod]
        public void DifferentSeed_ProducesDifferentSnapshot()
        {
            // 反向守卫：如果换 seed 结果也一样，说明生成过程根本没用到随机源，
            // 上一条「同 seed 一致」的通过就是假象（ECS.md §8：同 seed 同结果的前提是不同 seed 不同结果）。
            byte[] seeded = RunScenario(Seed);
            byte[] other = RunScenario(Seed + 1);

            bool identical = seeded.Length == other.Length;
            if (identical)
            {
                for (int i = 0; i < seeded.Length; i++)
                {
                    if (seeded[i] != other[i])
                    {
                        identical = false;
                        break;
                    }
                }
            }

            Assert.IsFalse(identical,
                "不同 seed 必须产生不同的组件快照，否则说明随机源没有参与生成（确定性是假象）。");
        }

        [TestMethod]
        public void SameSeed_RandomSequenceIsIdentical_AcrossInstances()
        {
            DeterministicRandom a = new DeterministicRandom(Seed);
            DeterministicRandom b = new DeterministicRandom(Seed);

            for (int i = 0; i < 256; i++)
            {
                int ia = a.NextInt(0, int.MaxValue);
                int ib = b.NextInt(0, int.MaxValue);
                Assert.AreEqual(ia, ib, "同 seed 的第 " + i + " 个 NextInt 必须相同。");

                float fa = a.NextFloat();
                float fb = b.NextFloat();
                Assert.AreEqual(fa, fb, "同 seed 的第 " + i + " 个 NextFloat 必须相同。");
            }
        }

        [TestMethod]
        public void DifferentSeed_RandomSequencesDiffer()
        {
            DeterministicRandom a = new DeterministicRandom(Seed);
            DeterministicRandom b = new DeterministicRandom(Seed + 1);

            bool anyDifference = false;
            for (int i = 0; i < 64; i++)
            {
                if (a.NextInt(0, int.MaxValue) != b.NextInt(0, int.MaxValue))
                {
                    anyDifference = true;
                    break;
                }
            }

            Assert.IsTrue(anyDifference, "不同 seed 的随机序列必须不同。");
        }

        [TestMethod]
        public void Fork_IsIndependentOfParentAdvancement()
        {
            // 子流的用途：多个系统各自要随机数但又要整体可复现。
            // 语义断言：用同一个 salt 从一个「已推进 N 步」的父流 fork，与从「未推进」的父流 fork，
            // 得到的子流必须一致 —— 否则父流推进多寡会污染子流，回放无法重现。
            // 这不只是「测试好看」：它是「刷怪系统 Fork(Spawn)、掉落系统 Fork(Loot) 互不干扰」的实现依据，
            // 即任何一处多调一次随机都不会让别的系统的随机序列漂移。
            DeterministicRandom parentA = new DeterministicRandom(Seed);
            DeterministicRandom parentB = new DeterministicRandom(Seed);

            for (int i = 0; i < 100; i++)
            {
                parentB.NextInt(0, int.MaxValue);
            }

            IRandomProvider forkA = parentA.Fork(7);
            IRandomProvider forkB = parentB.Fork(7);

            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(forkA.NextInt(0, int.MaxValue), forkB.NextInt(0, int.MaxValue),
                    "同一 salt 的 Fork 子流必须与父流的推进行为无关（第 " + i + " 个值）。");
            }
        }

        [TestMethod]
        public void Fork_SameObject_RepeatedCalls_ReturnEquivalentStreams()
        {
            // 更直接的判据（不涉及两个父流对比）：**同一个随机源对象**，在第 0 步与第 137 步
            // 各调一次 Fork(7)，两条子流必须逐元素相同。这正是「加一行随机调用不打乱其他系统」的形式化。
            DeterministicRandom parent = new DeterministicRandom(Seed);
            IRandomProvider early = parent.Fork(7);

            for (int i = 0; i < 137; i++)
            {
                parent.NextInt(0, int.MaxValue);
            }

            IRandomProvider late = parent.Fork(7);

            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(early.NextInt(0, int.MaxValue), late.NextInt(0, int.MaxValue),
                    "同一对象上重复 Fork(相同 salt) 必须得到等价子流（第 " + i + " 个值）。");
            }
        }

        [TestMethod]
        public void Fork_DoesNotAdvanceParentState()
        {
            // Fork 只读根状态、绝不推进父流：否则「多 fork 一个子系统」就会打乱父流后续取值。
            // 用 State 快照做判据（它读取不推进序列，是存档/回放的契约）。
            DeterministicRandom subject = new DeterministicRandom(Seed);
            subject.NextInt(0, int.MaxValue);

            ulong stateBefore = subject.State;
            subject.Fork(1);
            subject.Fork(2);
            subject.Fork(3);

            Assert.AreEqual(stateBefore, subject.State, "Fork 不得推进父流状态。");
        }

        [TestMethod]
        public void Fork_DoesNotAdvanceParentState_SequenceUnchanged()
        {
            // 上一条的严格版：fork 过的流与完全没 fork 的同 seed 流，
            // 后续取值必须**逐个相同**。这是「Fork 不消耗父流随机数」的强断言。
            DeterministicRandom forked = new DeterministicRandom(Seed);
            DeterministicRandom untouched = new DeterministicRandom(Seed);

            forked.Fork(1);
            forked.Fork(2);

            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(untouched.NextInt(0, int.MaxValue), forked.NextInt(0, int.MaxValue),
                    "Fork 不得消耗父流随机数，第 " + i + " 个值应一致。");
            }
        }

        [TestMethod]
        public void Fork_DifferentSalt_ProducesDifferentStream()
        {
            // 反向守卫：防「Fork 忽略 salt」这种退化实现（那会让所有子系统共用一条流）。
            DeterministicRandom parent = new DeterministicRandom(Seed);
            IRandomProvider a = parent.Fork(1);
            IRandomProvider b = parent.Fork(2);

            bool anyDifference = false;
            for (int i = 0; i < 16; i++)
            {
                if (a.NextInt(0, int.MaxValue) != b.NextInt(0, int.MaxValue))
                {
                    anyDifference = true;
                }
            }

            Assert.IsTrue(anyDifference, "不同 salt 的 Fork 子流必须不同（否则 salt 形同虚设）。");
        }

        [TestMethod]
        public void ForkFromCurrent_DependsOnParentAdvancement()
        {
            // 反向守卫：证明两种语义**确实是两回事**，防止有人把 Fork 与 ForkFromCurrent 合并成同一个实现
            // 而让上面几条「独立性」断言悄悄失效。
            // ForkFromCurrent 的定位是「从存档点 / 此刻派发一次性随机流」，所以它应当随父流进度变化。
            DeterministicRandom parent = new DeterministicRandom(Seed);
            IRandomProvider early = parent.ForkFromCurrent(7);
            int earlyValue = early.NextInt(0, int.MaxValue);

            for (int i = 0; i < 100; i++)
            {
                parent.NextInt(0, int.MaxValue);
            }

            IRandomProvider late = parent.ForkFromCurrent(7);
            Assert.AreNotEqual(earlyValue, late.NextInt(0, int.MaxValue),
                "ForkFromCurrent 的基准是当前状态，父流推进后同一 salt 必须得到不同子流。");
        }

        [TestMethod]
        public void ForkFromCurrent_SamePoint_IsReproducible()
        {
            // ForkFromCurrent 也要留在「可复现」这一侧：同样的推进步数 + 同样的 salt，
            // 必须得到同样的子流。否则它就不能用于回放。
            DeterministicRandom a = new DeterministicRandom(Seed);
            DeterministicRandom b = new DeterministicRandom(Seed);
            for (int i = 0; i < 50; i++)
            {
                a.NextInt(0, int.MaxValue);
                b.NextInt(0, int.MaxValue);
            }

            IRandomProvider fa = a.ForkFromCurrent(99);
            IRandomProvider fb = b.ForkFromCurrent(99);
            for (int i = 0; i < 16; i++)
            {
                Assert.AreEqual(fa.NextInt(0, int.MaxValue), fb.NextInt(0, int.MaxValue),
                    "相同推进步数 + 相同 salt 的 ForkFromCurrent 必须可复现（第 " + i + " 个值）。");
            }
        }

        [TestMethod]
        public void SameSeed_CommandBufferDrivenScenario_IsByteIdentical()
        {
            // §10 的确定性还要覆盖「结构化变更」这条路径：命令提交顺序一旦不确定，
            // 实体的槽位分配就会漂移，进而影响遍历顺序与后续随机消费。
            byte[] first = RunCommandScenario(Seed);
            byte[] second = RunCommandScenario(Seed);

            Assert.AreEqual(first.Length, second.Length, "命令驱动场景：两次运行快照长度必须一致。");
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                {
                    Assert.Fail("命令驱动场景在字节偏移 " + i + " 处不一致（确定性被破坏）。");
                }
            }
        }

        private static byte[] RunCommandScenario(int seed)
        {
            using World world = new World();
            IRandomProvider random = new DeterministicRandom(seed);

            // 先建 32 个实体，再每隔一个把「DeathMark」标记 + 递增计时写进命令，帧末统一提交。
            for (int i = 0; i < 32; i++)
            {
                world.Create(new Pos { X = i, Self = Entity.None }, new Vel { X = i * 0.5f, Self = Entity.None });
            }

            world.Query<Pos>().ForEach((Span<Pos> pos) =>
            {
                for (int i = 0; i < pos.Length; i++)
                {
                    if ((pos[i].Self.Index % 2) == 0)
                    {
                        world.Commands.Set(pos[i].Self, new LifeTimer { Remaining = random.NextFloat(), Self = Entity.None });
                    }
                }
            });
            world.Commands.Flush();

            byte[] snapshot = SnapshotPositions(world);

            // 再追加「计时器字节」，让快照覆盖命令写入的组件。
            using MemoryStream ms = new MemoryStream();
            ms.Write(snapshot, 0, snapshot.Length);
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                world.Query<LifeTimer>().ForEach((Span<LifeTimer> timers) =>
                {
                    for (int i = 0; i < timers.Length; i++)
                    {
                        bw.Write(timers[i].Self.Index);
                        bw.Write(timers[i].Remaining);
                    }
                });
                bw.Flush();
            }

            return ms.ToArray();
        }
    }
}
