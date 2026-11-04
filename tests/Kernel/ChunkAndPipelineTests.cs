using System;
using System.Collections.Generic;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Kernel
{
    // ==================================================================
    // 4. ForEach 的 chunk 边界（0 / 1 / 128 / 129）
    // ==================================================================

    /// <summary>
    /// ECS.md §5.3 迭代语义：<b>每个 chunk 回调一次</b>，<c>Span.Length</c> 即本 chunk 有效实体数。
    /// 边界值 0 / 1 / 128（正好一个 chunk）/ 129（跨 chunk）是最容易写错的地方：
    /// off-by-one 会让第 128 或第 129 个实体被漏掉或重复处理。
    /// </summary>
    [TestClass]
    public sealed class ChunkBoundaryTests
    {
        /// <summary>chunk 容量（取内核默认值，而不是在测试里写死 128）。</summary>
        private static readonly int Capacity = EcsConfig.DefaultChunkCapacity;

        [TestMethod]
        public void ForEach_ZeroEntities_NeverInvokesCallback()
        {
            using World world = new World();

            int calls = 0;
            world.Query<Pos>().ForEach((Span<Pos> span) => { calls++; });

            Assert.AreEqual(0, calls, "0 个实体时 ForEach 不得调用回调（没有 chunk 要遍历）。");
            Assert.AreEqual(0, world.Query<Pos>().Count, "0 个实体时 Count 必须是 0。");
        }

        [TestMethod]
        public void ForEach_OneEntity_VisitsExactlyIt()
        {
            using World world = new World();
            world.Create(new Pos { X = 1 });

            int entities = 0;
            int calls = 0;
            world.Query<Pos>().ForEach((Span<Pos> span) =>
            {
                calls++;
                Assert.AreEqual(1, span.Length, "1 个实体时 Span.Length 必须是 1。");
                entities += span.Length;
            });

            Assert.AreEqual(1, entities, "1 个实体时必须恰好遍历到 1 个。");
            Assert.AreEqual(1, calls, "1 个实体只占 1 个 chunk，回调应恰好 1 次。");
        }

        [TestMethod]
        public void ForEach_ExactlyChunkCapacity_OneChunkOneCallback()
        {
            using World world = new World();
            for (int i = 0; i < Capacity; i++)
            {
                world.Create(new Pos { X = i });
            }

            int entities = 0;
            int calls = 0;
            int maxSpan = 0;
            world.Query<Pos>().ForEach((Span<Pos> span) =>
            {
                calls++;
                entities += span.Length;
                if (span.Length > maxSpan)
                {
                    maxSpan = span.Length;
                }
            });

            Assert.AreEqual(Capacity, entities, "正好一个 chunk 实心时必须遍历到 " + Capacity + " 个，一个都不能漏。");
            Assert.AreEqual(1, calls,
                "正好 " + Capacity + " 个实体（= chunk 容量）时只应产生 1 个 chunk 回调，实际 " + calls + " 次。");
            Assert.AreEqual(Capacity, maxSpan, "实心 chunk 的 Span.Length 必须等于 chunk 容量。");
        }

        [TestMethod]
        public void ForEach_OneBeyondChunkCapacity_TwoChunksNoLossNoDuplicate()
        {
            using World world = new World();
            int total = Capacity + 1;
            for (int i = 0; i < total; i++)
            {
                world.Create(new Pos { X = i });
            }

            int entities = 0;
            int calls = 0;
            world.Query<Pos>().ForEach((Span<Pos> span) =>
            {
                calls++;
                entities += span.Length;
            });

            Assert.AreEqual(total, entities,
                "越过 chunk 边界 1 个（" + total + "）时必须恰好遍历 " + total + " 个：少 1 = 漏掉边界实体，多 1 = 重复遍历。");
            Assert.AreEqual(2, calls,
                total + " 个实体（容量 " + Capacity + "）必须切成 2 个 chunk，实际 " + calls + " 次回调。");
        }

        [TestMethod]
        public void ForEach_TwoFullChunks_VisitsAll()
        {
            // 再补一个 2×Capacity 的用例：连续两个实心 chunk 之间不得有「空 chunk 跳过一次」的偏差。
            using World world = new World();
            int total = Capacity * 2;
            for (int i = 0; i < total; i++)
            {
                world.Create(new Pos { X = i });
            }

            int entities = 0;
            int calls = 0;
            world.Query<Pos>().ForEach((Span<Pos> span) =>
            {
                calls++;
                entities += span.Length;
            });

            Assert.AreEqual(total, entities, "两个满 chunk 时必须遍历 " + total + " 个。");
            Assert.AreEqual(2, calls, "两个满 chunk 应产生 2 次回调，实际 " + calls + "。");
        }

        [TestMethod]
        public void ForEach_AfterDestroyAtBoundary_RestoresExactCount()
        {
            // 边界上的销毁最容易暴露「槽位搬移后计数不更新」的 bug：删掉实心 chunk 中间的一个，
            // 末尾实体被搬进空位，计数必须回到 total-1，且遍历到的实体号不能重复。
            using World world = new World();
            int total = Capacity + 1;
            List<Entity> created = new List<Entity>(total);
            for (int i = 0; i < total; i++)
            {
                created.Add(world.Create(new Pos { X = i }));
            }

            Assert.IsTrue(world.Destroy(created[0]), "销毁 chunk 内第一个实体应成功。");

            int entities = 0;
            world.Query<Pos>().ForEach((Span<Pos> span) => { entities += span.Length; });

            Assert.AreEqual(total - 1, entities,
                "销毁 1 个后必须遍历到 " + (total - 1) + " 个（槽位搬移不得让计数漂移）。");
        }

        [TestMethod]
        public void ForEach_WithTwoComponents_SpansAreAligned()
        {
            // 两个组件列必须「同一实体同一槽位」，否则位置 += 速度会串行错位。
            using World world = new World();
            int total = Capacity + 1;
            for (int i = 0; i < total; i++)
            {
                world.Create(new Pos { X = i }, new Vel { X = i * 10 });
            }

            int seen = 0;
            world.Query<Pos, Vel>().ForEach((Span<Pos> pos, Span<Vel> vel) =>
            {
                Assert.AreEqual(pos.Length, vel.Length, "两个组件的 Span 必须等长。");
                for (int i = 0; i < pos.Length; i++)
                {
                    Assert.AreEqual(pos[i].X * 10f, vel[i].X,
                        "同一 slot 上的 Pos 与 Vel 必须属于同一实体（列对齐）。");
                    seen++;
                }
            });

            Assert.AreEqual(total, seen, "两组件遍历必须覆盖全部 " + total + " 个实体。");
        }
    }

    // ==================================================================
    // 5. CommandBuffer：顺序、冲突跳过、帧末清空
    // ==================================================================

    [TestClass]
    public sealed class CommandBufferTests
    {
        [TestMethod]
        public void Commands_AreDeferred_NotAppliedBeforeFlush()
        {
            // J5：结构变更只能排到帧末。入队后、提交前，世界必须保持原样。
            using World world = new World();
            Entity e = world.Create(new Pos { X = 1 });

            world.Commands.Set(e, new Hp { Current = 5, Max = 5 });
            Assert.IsTrue(world.Commands.PendingCount > 0, "入队后 PendingCount 必须 > 0。");
            Assert.IsFalse(world.Has<Hp>(e), "提交之前组件不得出现在世界上（命令是延迟的）。");

            int executed = world.Commands.Flush();

            Assert.IsTrue(executed > 0, "Flush 应执行至少 1 条命令。");
            Assert.IsTrue(world.Has<Hp>(e), "提交之后组件必须出现。");
            Assert.AreEqual(5f, world.Get<Hp>(e).Current, "提交之后值必须落地。");
        }

        [TestMethod]
        public void Flush_EmptiesPendingCount()
        {
            // ECS.md §5.5：每帧提交后 PendingCount == 0。
            using World world = new World();
            Entity e = world.Create(new Pos());

            world.Commands.Set(e, new Hp { Current = 1, Max = 1 });
            Assert.IsTrue(world.Commands.PendingCount > 0);

            world.Commands.Flush();

            Assert.AreEqual(0, world.Commands.PendingCount, "提交之后 PendingCount 必须归零。");
        }

        [TestMethod]
        public void Commands_ExecuteInWriteOrder()
        {
            // ECS.md §5.5：提交顺序 = 写入顺序。用同一组件的连续 Set 来验证：
            // 最后落地的必须是**最后一条**命令写入的值（顺序执行）。
            using World world = new World();
            Entity e = world.Create(new Pos());

            world.Commands.Set(e, new Hp { Current = 1, Max = 9 });
            world.Commands.Set(e, new Hp { Current = 2, Max = 9 });
            world.Commands.Set(e, new Hp { Current = 3, Max = 9 });

            world.Commands.Flush();

            Assert.AreEqual(3f, world.Get<Hp>(e).Current,
                "同一组件连续 Set 时，最后一条命令必须生效（证明是按写入顺序执行，而不是后写先到）。");
        }

        [TestMethod]
        public void Commands_CreateThenSet_SameFlush_AppliesBoth()
        {
            // 顺序语义的另一种验证：先 Create 再对返回的实体 Set，同一批提交里必须都生效。
            using World world = new World();
            ComponentType marker = world.Register<Marker>();

            Entity created = world.Commands.Create(new[] { marker });
            world.Commands.Set(created, new Hp { Current = 77, Max = 100 });

            world.Commands.Flush();

            Assert.IsTrue(world.IsAlive(created), "命令创建出的实体在提交后必须是活的。");
            Assert.IsTrue(world.Has<Marker>(created), "Create 的组件必须存在。");
            Assert.IsTrue(world.Has<Hp>(created), "同批次的 Set 必须作用在同一实体上。");
            Assert.AreEqual(77f, world.Get<Hp>(created).Current, "Set 的值必须落地。");
        }

        [TestMethod]
        public void Commands_DestroyThenAddOnSameEntity_SkipsGracefullyAndCounts()
        {
            // ECS.md §5.5：同一实体「先 Destroy 后 Add」等冲突按顺序判定；
            // 后者对已销毁实体必须**静默跳过并计数**，不抛异常（防止 C# 结构化错误打断一帧）。
            using World world = new World();
            Entity e = world.Create(new Pos());
            int skippedBefore = world.Commands.SkippedCommandCount;

            world.Commands.Destroy(e);
            world.Commands.Set(e, new Hp { Current = 1, Max = 1 });

            // 关键：提交不得抛异常。
            world.Commands.Flush();

            Assert.IsFalse(world.IsAlive(e), "Destroy 已生效，实体必须是死的。");
            Assert.IsTrue(world.Commands.SkippedCommandCount > skippedBefore,
                "对已销毁实体的后续命令必须被计入 SkippedCommandCount（静默跳过要有痕迹）。");
        }

        [TestMethod]
        public void Commands_DoubleDestroy_SecondIsSkipped()
        {
            using World world = new World();
            Entity e = world.Create(new Pos());
            int skippedBefore = world.Commands.SkippedCommandCount;

            world.Commands.Destroy(e);
            world.Commands.Destroy(e);
            world.Commands.Flush();

            Assert.IsFalse(world.IsAlive(e));
            Assert.AreEqual(skippedBefore + 1, world.Commands.SkippedCommandCount,
                "重复 Destroy 必须恰好多算 1 条被跳过的命令。");
        }

        [TestMethod]
        public void Commands_WrittenInsideForEach_AreAppliedOnlyAfterFlush()
        {
            // 这是 J5 的实战形态：遍历中途不能改世界，只能写命令；提交后才看得到结果。
            using World world = new World();
            for (int i = 0; i < 3; i++)
            {
                world.Create(new Pos { X = i });
            }

            world.Query<Pos>().ForEach((Span<Pos> pos) =>
            {
                for (int i = 0; i < pos.Length; i++)
                {
                    // 遍历中直接结构化变更应该被拒绝（见下一个用例）；写命令是允许的。
                    world.Commands.Set(pos[i].Self, new Hp { Current = pos[i].X, Max = 100 });
                }
            });

            Assert.IsTrue(world.Commands.PendingCount > 0, "遍历中写入的命令必须堆积起来。");

            world.Commands.Flush();

            int withHp = 0;
            world.Query<Pos, Hp>().ForEach((Span<Pos> pos, Span<Hp> hp) =>
            {
                for (int i = 0; i < hp.Length; i++)
                {
                    Assert.AreEqual(pos[i].X, hp[i].Current, "每条命令必须作用到正确的实体上。");
                    withHp++;
                }
            });
            Assert.AreEqual(3, withHp, "3 条命令都必须生效。");
        }

        [TestMethod]
        public void StructuralChange_InsideForEach_Throws()
        {
            // J5 的另一半：遍历区间内直接 Create/Destroy/Set/Remove 必须立刻抛异常。
            using World world = new World();
            world.Create(new Pos { X = 1 });

            bool threw = false;
            try
            {
                world.Query<Pos>().ForEach((Span<Pos> pos) =>
                {
                    Entity target = pos[0].Self;
                    world.Destroy(target);
                });
            }
            catch (GameEcsException)
            {
                threw = true;
            }

            Assert.IsTrue(threw,
                "在 Query.ForEach 回调里直接 Destroy 必须抛 GameEcsException（ECS.md §5.3 / J5）。");
        }

        [TestMethod]
        public void Commands_RemoveOnMissingComponent_IsSkipped()
        {
            using World world = new World();
            Entity e = world.Create(new Pos());
            int skippedBefore = world.Commands.SkippedCommandCount;

            world.Commands.Remove<Hp>(e);
            world.Commands.Flush();

            Assert.IsTrue(world.IsAlive(e), "Remove 一条无效命令不得连带销毁实体。");
            Assert.AreEqual(skippedBefore + 1, world.Commands.SkippedCommandCount,
                "对不持有组件的 Remove 必须被计为跳过。");
        }
    }

    // ==================================================================
    // 6. SystemPipeline：分组 / 拓扑排序 / 环检测
    // ==================================================================

    /// <summary>记录执行顺序的探针系统基类。</summary>
    public abstract class ProbeSystem : SystemBase
    {
        /// <summary>所有探针共享的执行记录（测试内同步访问）。</summary>
        public static readonly List<string> Log = new List<string>();

        /// <inheritdoc />
        protected override void OnExecute(float deltaTime) => Log.Add(Name);
    }

    [UpdateInGroup(UpdateGroup.FixedUpdate)]
    public sealed class ProbeA : ProbeSystem { }

    [UpdateInGroup(UpdateGroup.FixedUpdate)]
    [UpdateAfter(typeof(ProbeA))]
    public sealed class ProbeB : ProbeSystem { }

    [UpdateInGroup(UpdateGroup.FixedUpdate)]
    [UpdateAfter(typeof(ProbeB))]
    public sealed class ProbeC : ProbeSystem { }

    // 环：CycleA 要求排在 CycleB 之后，CycleB 又要求排在 CycleA 之后 -> 无解。
    [UpdateAfter(typeof(CycleB))]
    public sealed class CycleA : ProbeSystem { }

    [UpdateAfter(typeof(CycleA))]
    public sealed class CycleB : ProbeSystem { }

    [TestClass]
    public sealed class SystemPipelineTests
    {
        [TestInitialize]
        public void Setup() => ProbeSystem.Log.Clear();

        [TestMethod]
        public void Build_AppliesUpdateAfterOrdering()
        {
            using World world = new World();
            SystemPipeline pipeline = new SystemPipeline(world);

            // 故意乱序注册：排序必须由 [UpdateAfter] 决定，而不是注册顺序。
            pipeline.Add(new ProbeC(), 0);
            pipeline.Add(new ProbeB(), 1);
            pipeline.Add(new ProbeA(), 2);
            pipeline.Build();

            pipeline.Execute(0f);

            CollectionAssert.AreEqual(
                new[] { "ProbeA", "ProbeB", "ProbeC" },
                ProbeSystem.Log.ToArray(),
                "[UpdateAfter] 链 A<-B<-C 必须让实际执行顺序为 A,B,C（与注册顺序无关）。");
        }

        [TestMethod]
        public void Build_WithinGroupWithoutConstraints_KeepsRegistrationOrder()
        {
            // ECS.md §5.4：同优先级按注册顺序稳定排序。
            using World world = new World();
            SystemPipeline pipeline = new SystemPipeline(world);

            pipeline.Add(new ProbeC(), 0);
            pipeline.Add(new ProbeA(), 1);
            pipeline.Build();
            pipeline.Execute(0f);

            // ProbeC 标了 [UpdateAfter(ProbeB)]，ProbeB 没注册 -> 该边被忽略；
            // 于是组内无约束，应保持注册顺序 C,A。
            CollectionAssert.AreEqual(
                new[] { "ProbeC", "ProbeA" },
                ProbeSystem.Log.ToArray(),
                "未注册的 [UpdateAfter] 目标应被忽略，组内退化为注册顺序。");
        }

        [TestMethod]
        public void Build_Cycle_ThrowsGameEcsExceptionWithSystemNames()
        {
            // ECS.md §5.4：存在环 -> 构造管线时抛 GameEcsException（中文信息，指出环上的系统）。
            using World world = new World();
            SystemPipeline pipeline = new SystemPipeline(world);
            pipeline.Add(new CycleA(), 0);
            pipeline.Add(new CycleB(), 1);

            GameEcsException ex = Assert.ThrowsExactly<GameEcsException>(
                () => pipeline.Build(),
                "存在排序环时 Build 必须抛 GameEcsException。");

            Assert.IsTrue(ex.Message.Length > 0, "环检测的异常必须带中文说明信息。");

            string message = ex.Message;
            bool namesCycle = message.IndexOf("CycleA", StringComparison.Ordinal) >= 0 ||
                              message.IndexOf("CycleB", StringComparison.Ordinal) >= 0;
            Assert.IsTrue(namesCycle,
                "环检测的异常信息必须指出环上的系统名，实际信息：" + message);
        }

        [TestMethod]
        public void ExecuteGroup_RunsOnlyThatGroup()
        {
            using World world = new World();
            SystemPipeline pipeline = new SystemPipeline(world);
            pipeline.Add(new ProbeA(), 0);       // FixedUpdate
            pipeline.Add(new ProbeC(), 1);       // FixedUpdate
            pipeline.Add(new UpdateGroupProbe(), 2); // Update
            pipeline.Build();

            pipeline.ExecuteGroup(UpdateGroup.FixedUpdate, 0f);

            CollectionAssert.DoesNotContain(ProbeSystem.Log, "UpdateGroupProbe",
                "ExecuteGroup(FixedUpdate) 不得执行 Update 分组的系统。");
            CollectionAssert.Contains(ProbeSystem.Log, "ProbeA", "FixedUpdate 分组的系统必须被执行。");
        }

        [TestMethod]
        public void Execute_DisabledSystem_IsSkipped()
        {
            using World world = new World();
            SystemPipeline pipeline = new SystemPipeline(world);
            ProbeA a = new ProbeA();
            ProbeB b = new ProbeB();
            pipeline.Add(a, 0);
            pipeline.Add(b, 1);
            pipeline.Build();

            b.Enabled = false;
            pipeline.Execute(0f);

            CollectionAssert.DoesNotContain(ProbeSystem.Log, "ProbeB", "禁用后的系统不得执行。");
            CollectionAssert.Contains(ProbeSystem.Log, "ProbeA", "未禁用的系统必须照常执行。");
        }
    }

    [UpdateInGroup(UpdateGroup.Update)]
    public sealed class UpdateGroupProbe : ProbeSystem { }
}
