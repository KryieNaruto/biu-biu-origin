using System;
using System.Runtime.InteropServices;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Kernel
{
    // ==================================================================
    // 测试专用组件（全部 unmanaged struct，且各带一个 [Identity] 字段，
    // 满足 ECS.md §4.2「每个组件必须正好有一个 [Identity] 字段」）。
    //
    // 为什么自己定义而不是用官方组件：内核不变量要验证的是「任意 unmanaged 组件组合
    // 都能正确列式存储 / 迁移」，用测试私有组件才能证明内核没有对官方组件做特判。
    // ==================================================================

    /// <summary>位置，用来测 chunk 边界与迁移。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Pos
    {
        /// <summary>实体身份（必须是布局上的第一个字段，ECS.md §4.2 铁律 3）。</summary>
        [Identity]
        public Entity Self;

        /// <summary>X 坐标。</summary>
        public float X;

        /// <summary>Y 坐标。</summary>
        public float Y;

        /// <summary>Z 坐标。</summary>
        public float Z;
    }

    /// <summary>速度。</summary>
    public struct Vel
    {
        /// <summary>实体身份。</summary>
        [Identity]
        public Entity Self;

        /// <summary>X 速度。</summary>
        public float X;
    }

    /// <summary>通用标记，用来测「旧引用失效」与过滤。</summary>
    public struct Marker
    {
        /// <summary>实体身份。</summary>
        [Identity]
        public Entity Self;

        /// <summary>标记值。</summary>
        public int Value;
    }

    /// <summary>血量，用来测「无则添加 / 有则更新」与排除查询。</summary>
    public struct Hp
    {
        /// <summary>实体身份。</summary>
        [Identity]
        public Entity Self;

        /// <summary>当前血量。</summary>
        public float Current;

        /// <summary>血量上限。</summary>
        public float Max;
    }

    // ==================================================================
    // 1. 创建 / 销毁 / 世代号复用
    // ==================================================================

    [TestClass]
    public sealed class EntityLifecycleTests
    {
        [TestMethod]
        public void Create_ReturnsAliveEntity_WithIdentityWrittenEverywhere()
        {
            using World world = new World();

            Pos pos = new Pos { X = 1, Y = 2, Z = 3, Self = Entity.None };
            Vel vel = new Vel { X = 4, Self = Entity.None };
            Entity e = world.Create(pos, vel);

            Assert.IsFalse(e.IsNone, "Create 不应返回 Entity.None。");
            Assert.IsTrue(world.IsAlive(e), "刚创建的实体必须是活的。");

            // [Identity] 字段必须由世界自动写成本实体号（ECS.md §4.2）。
            Assert.AreEqual(e, world.Get<Pos>(e).Self, "Pos.Self（[Identity] 字段）应被内核自动写成实体号。");
            Assert.AreEqual(e, world.Get<Vel>(e).Self, "Vel.Self（[Identity] 字段）应被内核自动写成实体号。");

            // 构造时传入的组件值必须原样落地。
            Assert.AreEqual(1f, world.Get<Pos>(e).X);
            Assert.AreEqual(4f, world.Get<Vel>(e).X);
        }

        [TestMethod]
        public void Destroy_KillsEntity_AndSecondDestroyReturnsFalse()
        {
            using World world = new World();
            Entity e = world.Create(new Marker { Value = 7 }, new Pos());

            Assert.IsTrue(world.IsAlive(e));
            Assert.IsTrue(world.Destroy(e), "第一次 Destroy 应返回 true。");
            Assert.IsFalse(world.IsAlive(e), "销毁后 IsAlive 必须为 false。");
            Assert.IsFalse(world.Destroy(e), "重复 Destroy 同一实体必须返回 false（不是异常）。");
        }

        [TestMethod]
        public void StaleHandle_AfterDestroyAndRecycle_IsNotAlive()
        {
            // ECS.md §5.1 的核心承诺：槽位会被复用，但**旧句柄不能寻址到新实体**。
            using World world = new World();
            Entity old = world.Create(new Marker { Value = 1 }, new Pos());
            int slotIndex = old.Index;

            Assert.IsTrue(world.Destroy(old));

            // 新建一个同构型实体，内核极可能复用同一个槽位。
            Entity next = world.Create(new Marker { Value = 2 }, new Pos());

            if (next.Index != slotIndex)
            {
                // 复用了别的槽位也算通过：此时旧句柄本来就不重叠。
                Assert.AreNotEqual(old, next);
                return;
            }

            // 复用了同一槽位 —— 这才是真正要断言的分支。
            Assert.AreEqual(slotIndex, next.Index, "前提：新实体复用了同一槽位号。");
            Assert.AreNotEqual(old.Generation, next.Generation,
                "同一槽位被复用时世代号必须递增，否则旧句柄会静默指向新实体（ECS.md §5.1）。");
            Assert.IsFalse(world.IsAlive(old), "旧句柄在槽位复用后必须是死的。");
            Assert.IsTrue(world.IsAlive(next), "新实体必须是活的。");

            // 用旧句柄读组件必须抛异常，而不是读到新实体的数据。
            Assert.ThrowsExactly<GameEcsException>(
                () => { Marker dummy = world.Get<Marker>(old); _ = dummy; },
                "用已销毁的旧句柄 Get 必须抛 GameEcsException（不能静默返回新实体的数据）。");
        }

        [TestMethod]
        public void DestroyThenCreateMany_GenerationNeverRegresses()
        {
            using World world = new World();

            Entity first = world.Create(new Marker(), new Pos());
            world.Destroy(first);

            // 连续创建 / 销毁 8 轮，世代号必须严格单调不减。
            int lastGeneration = -1;
            for (int i = 0; i < 8; i++)
            {
                Entity e = world.Create(new Marker { Value = i }, new Pos());
                Assert.IsTrue(world.IsAlive(e), "第 " + i + " 轮新建的实体必须是活的。");
                Assert.IsTrue(e.Generation >= lastGeneration,
                    "世代号不得回退：上一轮 " + lastGeneration + "，本轮 " + e.Generation);
                lastGeneration = e.Generation;
                Assert.IsTrue(world.Destroy(e));
            }
        }

        [TestMethod]
        public void IsAlive_OnEntityNone_IsFalse()
        {
            using World world = new World();
            Assert.IsFalse(world.IsAlive(Entity.None), "Entity.None 永远不是活实体。");
        }

        [TestMethod]
        public void Stats_ReflectEntityCount()
        {
            using World world = new World();
            Assert.AreEqual(0, world.Stats.EntityCount, "空世界的实体数必须是 0。");

            Entity a = world.Create(new Marker(), new Pos());
            Entity b = world.Create(new Marker(), new Pos());
            Entity one = world.Create(new Marker());

            Assert.IsTrue(world.IsAlive(a) && world.IsAlive(b) && world.IsAlive(one));
            Assert.AreEqual(3, world.Stats.EntityCount, "Stats.EntityCount 必须实时反映实体数。");

            world.Destroy(one);
            Assert.AreEqual(2, world.Stats.EntityCount, "销毁后 Stats.EntityCount 必须同步下降。");
        }

        [TestMethod]
        public void Version_AdvancesOnStructuralChange()
        {
            // ECS.md §5.2：每次结构化变更 Version +1，供 Job 检测「遍历期间被改」。
            using World world = new World();
            int before = world.Version;
            Entity e = world.Create(new Marker(), new Pos());
            int afterCreate = world.Version;
            Assert.IsTrue(afterCreate > before, "Create 是一次结构化变更，Version 必须前进。");

            world.Set(e, new Hp { Current = 1, Max = 1 });
            int afterSet = world.Version;
            Assert.IsTrue(afterSet > afterCreate, "Set（新增组件 = 原型迁移）必须让 Version 前进。");

            world.Destroy(e);
            Assert.IsTrue(world.Version > afterSet, "Destroy 必须让 Version 前进。");
        }
    }

    // ==================================================================
    // 2. 原型迁移：加组件 / 去组件 后数据保留，且旧引用失效
    // ==================================================================

    [TestClass]
    public sealed class ArchetypeMigrationTests
    {
        [TestMethod]
        public void Set_AddsComponent_KeepsExistingComponentValues()
        {
            using World world = new World();
            Entity e = world.Create(new Pos { X = 10, Y = 20, Z = 30 });

            Assert.IsFalse(world.Has<Hp>(e), "初始不应有 Hp 组件。");

            world.Set(e, new Hp { Current = 5, Max = 9 });

            Assert.IsTrue(world.Has<Hp>(e), "Set 之后必须持有 Hp。");
            Assert.AreEqual(5f, world.Get<Hp>(e).Current, "Set 写入的值必须可读回。");
            Assert.AreEqual(e, world.Get<Hp>(e).Self, "新组件的 [Identity] 字段必须写成实体号。");

            // 迁移是一次「搬到新原型」，原有组件的数据必须跟着搬过去（不能丢值）。
            Assert.AreEqual(10f, world.Get<Pos>(e).X, "原型迁移后 Pos.X 必须保留。");
            Assert.AreEqual(20f, world.Get<Pos>(e).Y, "原型迁移后 Pos.Y 必须保留。");
            Assert.AreEqual(30f, world.Get<Pos>(e).Z, "原型迁移后 Pos.Z 必须保留。");
            Assert.AreEqual(e, world.Get<Pos>(e).Self, "迁移后 Pos 的 [Identity] 字段必须仍指向本实体。");
        }

        [TestMethod]
        public void Set_OnExistingComponent_UpdatesInPlace_NoNewArchetype()
        {
            using World world = new World();
            Entity e = world.Create(new Pos { X = 1 }, new Hp { Current = 5, Max = 9 });
            int archetypesBefore = world.Stats.ArchetypeCount;

            world.Set(e, new Hp { Current = 42, Max = 50 });

            Assert.AreEqual(42f, world.Get<Hp>(e).Current, "已持有组件时 Set 应原地覆盖。");
            Assert.AreEqual(archetypesBefore, world.Stats.ArchetypeCount,
                "已持有组件时 Set 不得创建新原型（原地更新）。");
        }

        [TestMethod]
        public void Remove_DropsComponent_KeepsRemainingValues()
        {
            using World world = new World();
            Entity e = world.Create(new Pos { X = 7 }, new Hp { Current = 5, Max = 9 });

            Assert.IsTrue(world.Remove<Hp>(e), "Remove 已持有的组件应返回 true。");
            Assert.IsFalse(world.Has<Hp>(e), "Remove 之后不应再持有 Hp。");
            Assert.AreEqual(7f, world.Get<Pos>(e).X, "移除组件后其余组件的数据必须保留。");

            Assert.IsFalse(world.Remove<Hp>(e), "移除不存在的组件应返回 false（不是异常）。");
        }

        [TestMethod]
        public void Get_MissingComponent_ThrowsGameEcsException()
        {
            using World world = new World();
            Entity e = world.Create(new Pos());

            // ECS.md §5.2：Get 未持有组件必须抛 GameEcsException，绝不返回 default（静默错误最贵）。
            GameEcsException ex = Assert.ThrowsExactly<GameEcsException>(
                () => { Hp dummy = world.Get<Hp>(e); _ = dummy; });
            Assert.IsTrue(ex.Message.Length > 0, "异常信息必须有内容（含实体/组件名）。");

            Assert.IsFalse(world.TryGet<Hp>(e, out _), "TryGet 在未持有时应返回 false，不抛异常。");
        }

        [TestMethod]
        public void Get_AfterMigration_RefToOldComponentIsStale_NotAliased()
        {
            // ECS.md §10 明确要求：「组件继承迁移后旧引用失效（Get 拿不到旧数据）」。
            //
            // 迁移会把实体的数据搬到新原型的 chunk 里，旧 chunk 的槽位可能被别的实体复用。
            // 通过「迁移前拿到的 ref」写值，迁移后**不得**影响实体的当前值 ——
            // 换句话：官方 Get 读到的必须仍是迁移前的 100，而不是通过旧 ref 写进去的 999。
            using World world = new World();
            Entity e = world.Create(new Pos { X = 100 });

            ref Pos stale = ref world.Get<Pos>(e);
            Assert.AreEqual(100f, stale.X, "迁移前 ref 读到的是正确值。");

            // 触发一次原型迁移（加组件）。
            world.Set(e, new Hp { Current = 1, Max = 1 });

            // 官方值仍应是 100（数据被搬走且保留）。
            Assert.AreEqual(100f, world.Get<Pos>(e).X, "迁移后官方值必须保留为 100。");

            // 通过旧 ref 写值：只有当旧引用**不再指向实体当前数据**时，官方值才保持不变。
            stale.X = 999f;

            Assert.AreEqual(100f, world.Get<Pos>(e).X,
                "原型迁移后，迁移前取得的 ref 必须失效（写入旧 ref 不得改动实体当前数据）。" +
                "当前值被改成 999 说明旧引用仍与实体数据别名（ECS.md §10 违规）。");

            // 再从查询侧复核一次：遍历拿到的也必须是 100。
            float seen = -1f;
            world.Query<Pos>().ForEach((Span<Pos> span) =>
            {
                for (int i = 0; i < span.Length; i++)
                {
                    seen = span[i].X;
                }
            });
            Assert.AreEqual(100f, seen, "遍历读到的 Pos.X 也必须仍是 100。");
        }

        [TestMethod]
        public void Remove_ThenGet_Throws_MigratedDataStaysConsistent()
        {
            using World world = new World();
            Entity e = world.Create(new Pos { X = 5 }, new Hp { Current = 9, Max = 9 });

            world.Remove<Hp>(e);

            Assert.ThrowsExactly<GameEcsException>(
                () => { Hp dummy = world.Get<Hp>(e); _ = dummy; },
                "移除组件后 Get 必须抛异常。");
            Assert.AreEqual(5f, world.Get<Pos>(e).X, "移除一个组件不得影响另一个组件的值。");
        }
    }

    // ==================================================================
    // 3. Query 的「必含 / 排除」语义
    // ==================================================================

    [TestClass]
    public sealed class QueryFilterTests
    {
        [TestMethod]
        public void Query_BySingleComponent_MatchesOnlyHolders()
        {
            using World world = new World();
            world.Create(new Pos { X = 1 });
            world.Create(new Pos { X = 2 }, new Vel { X = 3 });
            world.Create(new Marker { Value = 9 });

            Query q = world.Query<Pos>();
            Assert.AreEqual(2, q.Count, "Query<Pos>() 的 Count 必须命中恰好 2 个实体。");

            // 强断言：遍历到的一定是持 Pos 的那两个，绝不能混进 Marker-only 实体。
            int seen = 0;
            q.ForEach((Span<Pos> span) =>
            {
                for (int i = 0; i < span.Length; i++)
                {
                    Assert.IsTrue(span[i].X == 1f || span[i].X == 2f,
                        "查询命中的必须是持 Pos 的实体，实际读到 X=" + span[i].X);
                    seen++;
                }
            });
            Assert.AreEqual(2, seen, "遍历到的实体数必须等于 Count。");
        }

        [TestMethod]
        public void Query_ByTwoComponents_RequiresBoth()
        {
            using World world = new World();
            world.Create(new Pos { X = 1 });
            world.Create(new Pos { X = 2 }, new Vel { X = 3 });
            world.Create(new Vel { X = 4 });

            Assert.AreEqual(1, world.Query<Pos, Vel>().Count,
                "Query<Pos,Vel> 必须只命中同时持有两者的实体（交集，不是并集）。");
        }

        [TestMethod]
        public void Query_Excluded_RejectsEntitiesHoldingTheExcludedComponent()
        {
            // ECS.md §10：Query 包含 / 排除都要覆盖。
            using World world = new World();
            world.Create(new Pos { X = 1 });
            world.Create(new Pos { X = 2 }, new Vel { X = 3 });
            world.Create(new Pos { X = 4 }, new Hp { Current = 1, Max = 1 });

            ComponentType pos = world.Register<Pos>();
            ComponentType hp = world.Register<Hp>();

            Query q = world.Query(new[] { pos }, new[] { hp });

            Assert.AreEqual(2, q.Count,
                "Query(required=[Pos], excluded=[Hp]) 必须命中 2 个（排除掉持 Hp 的那个）。");

            // 再验一次「排除生效」的强断言：命中集合里绝不能有任何 X=4 的实体。
            q.ForEach((Span<Pos> span) =>
            {
                for (int i = 0; i < span.Length; i++)
                {
                    Assert.AreNotEqual(4f, span[i].X, "被排除的实体（X=4，持 Hp）绝不能被遍历到。");
                }
            });
        }

        [TestMethod]
        public void Query_ExcludedWithNull_MatchesAllRequired()
        {
            using World world = new World();
            for (int i = 0; i < 5; i++)
            {
                world.Create(new Pos { X = i });
            }

            ComponentType pos = world.Register<Pos>();
            Assert.AreEqual(5, world.Query(new[] { pos }, null).Count,
                "excluded 为 null 时必须等同「不排除任何东西」。");
        }

        [TestMethod]
        public void Query_Without_ChainsExclusion()
        {
            using World world = new World();
            world.Create(new Pos { X = 1 });
            world.Create(new Pos { X = 2 }, new Hp { Current = 1, Max = 1 });

            Query q = world.Query<Pos>().Without<Hp>();
            Assert.AreEqual(1, q.Count, "Query<Pos>().Without<Hp>() 必须只剩不持 Hp 的那个。");
        }

        [TestMethod]
        public void Query_AfterMigration_SeesEntityInNewlyCreatedArchetype()
        {
            // 查询在创建时一次性匹配原型（ECS.md §5.3）。实体迁移到**新建**原型后，
            // 已存在的 Query 必须能看到它 —— 否则「遍历中途迁移」会让实体凭空消失。
            using World world = new World();
            Entity e = world.Create(new Pos { X = 1 });
            Query q = world.Query<Pos, Hp>();

            Assert.AreEqual(0, q.Count, "初始没有持 Pos+Hp 的实体。");

            world.Set(e, new Hp { Current = 1, Max = 1 });

            Assert.AreEqual(1, q.Count,
                "实体迁移进新原型后，之前创建的 Query 必须能匹配到它。");
        }

        [TestMethod]
        public void Query_ExcludedMatchesNothing_WhenAllFilteredOut()
        {
            using World world = new World();
            world.Create(new Pos { X = 1 }, new Hp { Current = 1, Max = 1 });

            Query q = world.Query(new[] { world.Register<Pos>() }, new[] { world.Register<Hp>() });
            Assert.AreEqual(0, q.Count, "全部实体都持排除组件时，命中数必须是 0。");
            Assert.IsTrue(q.IsEmpty, "IsEmpty 必须与 Count == 0 一致。");
        }
    }
}
