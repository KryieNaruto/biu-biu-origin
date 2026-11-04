using System;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Allocation
{
    // ==================================================================
    // 8. 零分配：Query.ForEach 稳态分配 = 0
    // ==================================================================
    //
    // ECS.md §10：「`Query.ForEach` 稳态分配 = 0（`GC.GetAllocatedBytesForCurrentThread` 前后差）」。
    // 这是 J6「查询形状稳定，按 chunk 批处理」的唯一可执行判据，也是规则2 判据 H6 的测量口径。
    //
    // 测量纪律（否则用例本身就是噪声源）：
    //   1) 先 warm-up（JIT 编译、首次字典查找、chunk 扩容都在 warm-up 里完成）；
    //   2) 再取 before，跑 N 轮，取 after，断言 after - before == 0；
    //   3) 用「每线程分配字节」而不是 GC.GetTotalAllocatedBytes —— 后者会被别的线程干扰；
    //   4) 回调必须是**无捕获实例方法组**或接口实现，不能是闭包 lambda。
    //      本组同时验证「接口版」（ISpanAction）与「委托版」，两者都必须零分配。
    //
    // 注意：这里不为了绿而放宽阈值。稳态就是稳态，1 字节也算失败。
    // ==================================================================

    /// <summary>零分配测试用的组件。</summary>
    public struct AllocProbe
    {
        /// <summary>实体身份（布局上必须第一个，ECS.md §4.2 铁律 3）。</summary>
        [Identity]
        public Entity Self;

        /// <summary>参与累加的数值。</summary>
        public float Value;
    }

    /// <summary>零分配测试用的第二个组件（验证两元查询同样零分配）。</summary>
    public struct AllocProbe2
    {
        /// <summary>实体身份。</summary>
        [Identity]
        public Entity Self;

        /// <summary>参与累加的数值。</summary>
        public float Value;
    }

    /// <summary>
    /// 无捕获的接口版回调（零分配正路）。作为 **sealed class** 实例复用：
    /// 接口调用单态点可以被 JIT 去虚化并内联，且不用每次构造。
    /// </summary>
    public sealed class SumAction1 : ISpanAction<AllocProbe>
    {
        /// <summary>累计和（读取它防止 JIT 把整个循环消除掉）。</summary>
        public float Total;

        /// <inheritdoc />
        public void Execute(Span<AllocProbe> a, int count)
        {
            float total = Total;
            for (int i = 0; i < count; i++)
            {
                total += a[i].Value;
            }

            Total = total;
        }
    }

    /// <summary>无捕获的两元接口版回调。</summary>
    public sealed class SumAction2 : ISpanAction<AllocProbe, AllocProbe2>
    {
        /// <summary>累计和。</summary>
        public float Total;

        /// <inheritdoc />
        public void Execute(Span<AllocProbe> a, Span<AllocProbe2> b, int count)
        {
            float total = Total;
            for (int i = 0; i < count; i++)
            {
                total += a[i].Value + b[i].Value;
            }

            Total = total;
        }
    }

    /// <summary>无捕获的委托版回调持有者（方法组转委托只在构造时发生一次）。</summary>
    public sealed class DelegateHolder
    {
        /// <summary>累计和。</summary>
        public float Total;

        /// <summary>缓存的委托（构造期建一次，热路径复用）。</summary>
        public readonly SpanAction<AllocProbe> One;
        public readonly SpanAction<AllocProbe, AllocProbe2> Two;

        public DelegateHolder()
        {
            One = Run1;
            Two = Run2;
        }

        private void Run1(Span<AllocProbe> a)
        {
            float total = Total;
            for (int i = 0; i < a.Length; i++)
            {
                total += a[i].Value;
            }

            Total = total;
        }

        private void Run2(Span<AllocProbe> a, Span<AllocProbe2> b)
        {
            float total = Total;
            for (int i = 0; i < a.Length; i++)
            {
                total += a[i].Value + b[i].Value;
            }

            Total = total;
        }
    }

    [TestClass]
    public sealed class ZeroAllocationTests
    {
        /// <summary>实体数量：刻意跨 chunk 边界（> 128），让循环真的走多 chunk 路径。</summary>
        private const int EntityCount = 300;

        /// <summary>测量轮数（足够让统计稳定，但不至于让用例变慢）。</summary>
        private const int Iterations = 100;

        /// <summary>warm-up 轮数（吃掉 JIT 与首次路径）。</summary>
        private const int WarmupIterations = 16;

        private static World CreateWorld()
        {
            World world = new World();
            for (int i = 0; i < EntityCount; i++)
            {
                world.Create(
                    new AllocProbe { Value = i, Self = Entity.None },
                    new AllocProbe2 { Value = i * 2, Self = Entity.None });
            }

            return world;
        }

        [TestMethod]
        public void ForEach_InterfaceOneComponent_SteadyStateAllocatesZeroBytes()
        {
            using World world = CreateWorld();
            Query query = world.Query<AllocProbe>();
            SumAction1 impl1 = new SumAction1();
            ISpanAction<AllocProbe> action = impl1;

            // warm-up：JIT、首次列解析、chunk 扩容都必须在这之前完成。
            for (int i = 0; i < WarmupIterations; i++)
            {
                query.ForEach<AllocProbe>(in action);
            }

            impl1.Total = 0f;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                query.ForEach<AllocProbe>(in action);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;

            Assert.IsTrue(impl1.Total > 0f, "回调必须真的执行过（否则零分配是因为什么都没做）。");
            Assert.AreEqual(0L, delta,
                "Query.ForEach（接口版 / 1 组件）稳态必须零分配，实测分配了 " + delta + " 字节（ECS.md §10）。");
        }

        [TestMethod]
        public void ForEach_InterfaceTwoComponents_SteadyStateAllocatesZeroBytes()
        {
            using World world = CreateWorld();
            Query query = world.Query<AllocProbe, AllocProbe2>();
            SumAction2 impl2 = new SumAction2();
            ISpanAction<AllocProbe, AllocProbe2> action = impl2;

            for (int i = 0; i < WarmupIterations; i++)
            {
                query.ForEach<AllocProbe, AllocProbe2>(in action);
            }

            impl2.Total = 0f;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                query.ForEach<AllocProbe, AllocProbe2>(in action);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;

            Assert.IsTrue(impl2.Total > 0f, "回调必须真的执行过。");
            Assert.AreEqual(0L, delta,
                "Query.ForEach（接口版 / 2 组件）稳态必须零分配，实测分配了 " + delta + " 字节。");
        }

        [TestMethod]
        public void ForEach_DelegateOneComponent_SteadyStateAllocatesZeroBytes()
        {
            // 委托版同样要求零分配：委托实例在构造期缓存一次（DelegateHolder 构造函数），
            // 热路径不许再 new。若这里是「每帧 new 一个 lambda」，本用例会立刻红。
            using World world = CreateWorld();
            Query query = world.Query<AllocProbe>();
            DelegateHolder holder = new DelegateHolder();
            SpanAction<AllocProbe> cached = holder.One;

            for (int i = 0; i < WarmupIterations; i++)
            {
                query.ForEach(cached);
            }

            holder.Total = 0f;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                query.ForEach(cached);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;

            Assert.IsTrue(holder.Total > 0f, "回调必须真的执行过。");
            Assert.AreEqual(0L, delta,
                "Query.ForEach（委托版，委托已缓存）稳态必须零分配，实测分配了 " + delta + " 字节。");
        }

        [TestMethod]
        public void ForEach_DelegateTwoComponents_SteadyStateAllocatesZeroBytes()
        {
            using World world = CreateWorld();
            Query query = world.Query<AllocProbe, AllocProbe2>();
            DelegateHolder holder = new DelegateHolder();
            SpanAction<AllocProbe, AllocProbe2> cached = holder.Two;

            for (int i = 0; i < WarmupIterations; i++)
            {
                query.ForEach(cached);
            }

            holder.Total = 0f;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                query.ForEach(cached);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;

            Assert.IsTrue(holder.Total > 0f, "回调必须真的执行过。");
            Assert.AreEqual(0L, delta,
                "Query.ForEach（委托版 / 2 组件）稳态必须零分配，实测分配了 " + delta + " 字节。");
        }

        [TestMethod]
        public void QueryCachedAtInit_RepeatedForEach_DoesNotAllocateForQueryConstruction()
        {
            // 「查询形状稳定」的另一半：Query 对象应在系统初始化期建一次并缓存（ECS.md §5.3），
            // 热路径只调用 ForEach。这里直接度量「复用已缓存的 Query」这条路是否零分配。
            using World world = CreateWorld();
            Query cached = world.Query<AllocProbe>();
            SumAction1 impl1 = new SumAction1();
            ISpanAction<AllocProbe> action = impl1;

            for (int i = 0; i < WarmupIterations; i++)
            {
                cached.ForEach<AllocProbe>(in action);
            }

            impl1.Total = 0f;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                cached.ForEach<AllocProbe>(in action);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;

            Assert.IsTrue(impl1.Total > 0f, "回调必须真的执行过。");
            Assert.AreEqual(0L, delta,
                "复用已缓存的 Query 进行遍历，稳态必须零分配，实测分配了 " + delta + " 字节。");
        }

        [TestMethod]
        public void GetAllocatedBytes_MeasurementItself_IsSensitive()
        {
            // 测量设备自检：如果 GC.GetAllocatedBytesForCurrentThread 在本进程里根本测不出分配，
            // 那么上面所有「delta == 0」都是无意义的。这里主动分配一次，确认它能被测到。
            long before = GC.GetAllocatedBytesForCurrentThread();
            object[] sink = new object[8];
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.IsTrue(after > before,
                "测量设备自检失败：主动分配 8 个 object 后 GetAllocatedBytesForCurrentThread 没有增长，" +
                "说明本进程内该 API 不可用于判定零分配，上面的零分配断言也就不可信。");
            GC.KeepAlive(sink);
        }
    }
}
