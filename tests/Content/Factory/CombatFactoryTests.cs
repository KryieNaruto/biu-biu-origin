using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Combat.Factory;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NVector3 = System.Numerics.Vector3;

namespace BiuBiuOrigin.Tests.Content.Factory
{
    /// <summary>
    /// <b>「基础物件抽象」运行时层验收用例组</b>：寄存器回收、熔炉配方、铸造队列、铸造工厂、弹道推进。
    /// <para>
    /// <b>为什么这些用例是这一层的全部证据</b>：本层对外只承诺四件事 ——
    /// ① 内容表 / 寄存器能把"这一帧该用哪个 def"解析成一个 <c>int</c>；
    /// ② 熔炉能在候选里按配方 tag 选出一个 def，且选不出来时诚实返回 -1；
    /// ③ 铸造必须<b>零托管分配</b>（用户原话："关于工厂，必须要高性能。使用对象池减少 Node 的 GC"）；
    /// ④ 「武器射子弹 / 武器射怪物 / 子弹爆炸再射子弹」是<b>同一段工厂代码</b>，差别只在数据。
    /// 下面每个用例只钉住其中一条，失败时能直接指到具体承诺。
    /// </para>
    /// <para>
    /// <b>为什么能在没有 Godot 的进程里跑</b>：本层（<c>scripts/combat/**</c>）不 <c>using Godot</c>（J4），
    /// 只依赖 <c>Game.Core.Ecs</c> + <c>Game.Core.Content</c>，所以测试工程可以把源码直接编译进来，
    /// 与引擎彻底解耦 —— 一旦有人往本层里加引擎类型，测试工程会先编译失败。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class CombatFactoryTests
    {
        // ─────────────────────────────────────────────────────────────
        // 共享夹具：一套最小的"武器 → 子弹"内容表
        // ─────────────────────────────────────────────────────────────

        /// <summary>固定随机源（数值规则里只有 <c>rand:</c> 会用到它，本夹具全部走字面量）。</summary>
        private static readonly FakeRandom Random = new FakeRandom(0.5f);

        /// <summary>熔炉 / 寄存器共用的求值上下文（全中性世界 + 固定随机）。</summary>
        private static ValueContext Ctx() => new ValueContext(FakeCombatWorld.Instance, default, Random);

        /// <summary>建一份内容表：手枪 + 手枪子弹（+ 可选：怪物、爆炸子弹）。</summary>
        private static ContentIndex BuildIndex(bool withMonster = false, bool withExplosion = false)
        {
            var index = new ContentIndex { Name = "test" };

            // 契约的 ObjectDefId 必须指向"契约属于哪个物件"（簇 / 半径 / 渲染都挂在物件那一行）。
            // 写成 0 是错的：ContentIndex 的哈希查找以 0 作为「未设置」的哨兵，簇查询会静默失败。
            int pistolHash = ContentHash.Fnv1a32("weapon_pistol");
            int bulletHash = ContentHash.Fnv1a32("bullet_pistol");

            var pistolBullet = new ProjectileContract(
                id: 201,
                defId: "bullet_pistol",
                objectDefId: bulletHash,
                motion: ProjectileMotion.Straight,
                hitPolicy: ProjectileHitPolicy.Single,
                onExpire: ProjectileDeath.None,
                speed: ValueSet.From(ValueRef.Literal(20f)),
                lifetime: ValueSet.From(ValueRef.Literal(1f)),
                damage: ValueSet.From(ValueRef.Literal(5f)),
                radius: ValueSet.From(ValueRef.Literal(0.1f)));

            var pistolWeapon = new WeaponContract(
                id: 101,
                defId: "weapon_pistol",
                objectDefId: pistolHash,
                trigger: WeaponTrigger.Auto,
                delivery: WeaponDelivery.Projectile,
                aim: WeaponAimKind.AimPoint,
                projectile: ContentRef.To("bullet_pistol"),
                fireDelay: ValueSet.From(ValueRef.Literal(0.2f)),
                count: ValueSet.From(ValueRef.Literal(1f)));

            // 子弹物件必须先注册：契约里引用的子弹得在表里。
            var bulletArchetype = new ObjectArchetype(
                id: "bullet_pistol",
                capabilities: ObjectCapability.Projectile,
                projectileContract: pistolBullet);
            index.RegisterObject(bulletArchetype);

            var pistol = new ObjectArchetype(
                id: "weapon_pistol",
                capabilities: ObjectCapability.Actor | ObjectCapability.Weapon,
                clusterSlot: 1,
                projectile: ContentRef.To("bullet_pistol"),
                weaponContract: pistolWeapon);
            index.RegisterObject(pistol);

            if (withMonster)
            {
                var monster = new ObjectArchetype(
                    id: "monster_grunt",
                    capabilities: ObjectCapability.Actor,
                    clusterSlot: 2,
                    radius: 0.4f,
                    height: 1.2f);
                index.RegisterObject(monster);
            }

            if (withExplosion)
            {
                var shard = new ProjectileContract(
                    id: 202,
                    defId: "bullet_shard",
                    objectDefId: ContentHash.Fnv1a32("bullet_shard"),
                    onExpire: ProjectileDeath.None,
                    speed: ValueSet.From(ValueRef.Literal(10f)),
                    lifetime: ValueSet.From(ValueRef.Literal(0.5f)),
                    damage: ValueSet.From(ValueRef.Literal(2f)),
                    radius: ValueSet.From(ValueRef.Literal(0.1f)));
                index.RegisterObject(new ObjectArchetype("bullet_shard", ObjectCapability.Projectile, projectileContract: shard));

                var bomb = new ProjectileContract(
                    id: 203,
                    defId: "bullet_bomb",
                    objectDefId: ContentHash.Fnv1a32("bullet_bomb"),
                    onExpire: ProjectileDeath.SpawnProjectile,
                    maxChainDepth: 3,
                    onExpireProjectile: ContentRef.To("bullet_shard"),
                    speed: ValueSet.From(ValueRef.Literal(8f)),
                    lifetime: ValueSet.From(ValueRef.Literal(0.3f)),
                    damage: ValueSet.From(ValueRef.Literal(9f)),
                    radius: ValueSet.From(ValueRef.Literal(0.3f)));
                index.RegisterObject(new ObjectArchetype("bullet_bomb", ObjectCapability.Projectile, projectileContract: bomb));
            }

            return index;
        }

        /// <summary>建一套世界 + 工厂 + 寄存器（默认武器槽位已绑到手枪）。</summary>
        private static (World World, ContentIndex Index, ContentRegistry Registry, EcsObjectFactory Factory, CastQueue Queue)
            BuildRig(ContentIndex index)
        {
            var world = new World(chunkCapacity: 64);
            var registry = new ContentRegistry(index);
            var spawner = new ContentSpawner(world, index, Random);
            var factory = new EcsObjectFactory(spawner, index, registry, Random);
            var queue = new CastQueue(16);

            int weaponSlotId = index.WeaponSlotOf("weapon_pistol");
            registry.DeclareSlot("slot.primary", clusterSlot: 1, defSpace: ContentSpace.Weapon, defaultDefId: weaponSlotId);

            return (world, index, registry, factory, queue);
        }

        // ─────────────────────────────────────────────────────────────
        // ① 内容表：重复 id 必须炸，越界查询必须返回 null（不抛）
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void ContentIndex_DuplicateIdThrowsChinese_OutOfRangeReturnsNull()
        {
            var index = new ContentIndex();
            var archetype = new ObjectArchetype("dup_thing", ObjectCapability.Actor);
            index.RegisterObject(archetype);

            // 为什么必须抛：Excel 的 id 列重复是策划的常见笔误；静默取后者会让"改了表没生效"变成玄学。
            var ex = Assert.ThrowsExactly<ContentException>(() => index.RegisterObject(
                new ObjectArchetype("dup_thing", ObjectCapability.Actor)));
            Assert.IsTrue(ex.Message.Contains("重复"), "异常信息必须点明是重复注册，实际：" + ex.Message);

            // 为什么越界返回 null 而不是抛：内容不是加载期失败的（热重载 / 数据缺失），
            // 运行期每帧都可能查一次，抛异常会把"某颗子弹没数据"升级成"整个游戏崩掉"。
            Assert.IsNull(index.GetObject(999));
            Assert.IsNull(index.GetObject(ContentIndex.NoneSlot));
            Assert.IsNull(index.GetWeapon(999));
            Assert.IsNull(index.GetProjectile(999));

            // 槽位解析是纯 int 查表（热路径）：不认识的 id 给 NoneSlot，绝不抛。
            Assert.AreEqual(ContentIndex.NoneSlot, index.ObjectSlotOf("does_not_exist"));
            Assert.AreEqual(ContentIndex.NoneSlot, index.ProjectileSlotOf("does_not_exist"));
        }

        // ─────────────────────────────────────────────────────────────
        // ② 寄存器：声明 → 绑定 → 解析；Purge 恢复到默认绑定
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void ContentRegistry_BindResolveAndPurge_RestoresDefault()
        {
            var index = BuildIndex(withMonster: true);
            var (_, _, registry, _, _) = BuildRig(index);

            int slotId = registry.SlotIdOf("slot.primary");
            Assert.IsTrue(slotId >= 0, "声明的槽位必须能按 key 查回");

            var world = FakeCombatWorld.Instance;
            var ctx = new ValueContext(world, default, Random);

            int pistolWeapon = index.WeaponSlotOf("weapon_pistol");
            Assert.AreEqual(pistolWeapon, registry.Resolve(slotId, in ctx), "没绑定时解析出默认绑定");

            // 绑一把"别的"东西（同一簇的第二把枪：簇内互换正是熔炉的语义）。
            var rifleContract = new WeaponContract(
                id: 102,
                defId: "weapon_rifle",
                objectDefId: ContentHash.Fnv1a32("weapon_rifle"),
                delivery: WeaponDelivery.Projectile,
                projectile: ContentRef.To("bullet_pistol"));
            // 关键：物件原型也要落在簇 1，否则 IsInCluster 会认为步枪不属于这个槽位的簇。
            index.RegisterObject(new ObjectArchetype(
                "weapon_rifle",
                ObjectCapability.Actor | ObjectCapability.Weapon,
                clusterSlot: 1,
                weaponContract: rifleContract));
            int rifleSlot = index.WeaponSlotOf("weapon_rifle");

            Assert.IsTrue(registry.TryBind(slotId, rifleSlot), "簇内的 def 必须允许绑定");
            Assert.AreEqual(rifleSlot, registry.Resolve(slotId, in ctx), "绑定后解析出绑定值");

            // Purge（generation 回收）：一次射击 / 一段过程结束后，槽位回到默认绑定。
            // 为什么要有它：熔炉写的是"这一次"的选择；不回收的话，上一次的临时状态会污染下一次。
            registry.Purge(generation: 1);
            Assert.AreEqual(pistolWeapon, registry.Resolve(slotId, in ctx), "Purge 后必须回到默认绑定");

            // 簇外的东西绑不进去（否则"同簇互换"这个不变式就没意义了）。
            int monsterSlot = index.ObjectSlotOf("monster_grunt");
            Assert.IsFalse(registry.TryBind(slotId, monsterSlot), "跨簇绑定必须被拒绝");
        }

        // ─────────────────────────────────────────────────────────────
        // ③ 熔炉：单命中 / 加权多命中 / 无匹配 / Perf
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void ForgeRules_SingleHit_SelectsTheOnlyCandidate()
        {
            var candidates = new[]
            {
                new ForgeCandidate(defId: 7, tagMask: 0b0011, weight: 1f, viableCount: 1),
            };

            int selected = ForgeRules.Evaluate(0b0001UL, candidates, Ctx(), out ForgeResult result);

            Assert.AreEqual(7, selected);
            Assert.AreEqual(ForgeOutcome.Matched, result.Outcome);
            Assert.AreEqual(7, result.SelectedDefId);
            Assert.IsTrue(result.IsMatched);
            Assert.AreEqual(1f, result.Perf, 1e-5f, "唯一候选命中 = 100% 性能占比");
        }

        [TestMethod]
        public void ForgeRules_MultipleHits_PrefersViableOverWeight()
        {
            // 三个候选都命中了配方 tag，但只有一个 viable（还有发动次数）。
            // 为什么 viable 优先于 weight：weight 是"这个物件有多希望被选中"，viable 是"它还能不能被选中"；
            // 把没次数的高权物件选中，会让玩家按下去没反应。
            var candidates = new[]
            {
                new ForgeCandidate(defId: 10, tagMask: 0b0001, weight: 1f, viableCount: 0),
                new ForgeCandidate(defId: 11, tagMask: 0b0001, weight: 0.5f, viableCount: 2),
                new ForgeCandidate(defId: 12, tagMask: 0b0001, weight: 0.2f, viableCount: 0),
            };

            int selected = ForgeRules.Evaluate(0b0001UL, candidates, Ctx(), out ForgeResult result);

            Assert.AreEqual(11, selected, "只有 viableCount>0 的那一个可以被选中");
            Assert.AreEqual(ForgeOutcome.Matched, result.Outcome);
        }

        [TestMethod]
        public void ForgeRules_NoMatch_ReturnsMinusOne()
        {
            var candidates = new[]
            {
                new ForgeCandidate(defId: 10, tagMask: 0b0010, weight: 1f, viableCount: 1),
            };

            int selected = ForgeRules.Evaluate(0b0001UL, candidates, Ctx(), out ForgeResult result);

            // 选不出来必须诚实返回 -1 + NoMatch：调用方据此回落到"槽位当前绑定"，
            // 绝不能悄悄选第一个候选（那会让"配方没配"变成"配方配了但行为怪"）。
            Assert.AreEqual(ForgeRules.NoneDefId, selected);
            Assert.AreEqual(ForgeOutcome.NoMatch, result.Outcome);
            Assert.IsFalse(result.IsMatched);
        }

        [TestMethod]
        public void ForgeRules_Perf_IsMatchedWeightOverTotalWeight()
        {
            var candidates = new[]
            {
                new ForgeCandidate(defId: 10, tagMask: 0b0001, weight: 0.75f, viableCount: 1),
                new ForgeCandidate(defId: 11, tagMask: 0b0010, weight: 0.25f, viableCount: 1),
            };

            // 只有 0.75 那份命中配方 → Perf = 0.75 / (0.75 + 0.25)
            float perf = ForgeRules.EvaluatePerf(recipeTag: 0b0001UL, candidates);

            Assert.AreEqual(0.75f, perf, 1e-5f, "Perf = 命中权重 / 总权重");
        }

        [TestMethod]
        public void ForgeRules_FlatArrays_MatchStructSpanBehaviour()
        {
            // 数据层（ContentBinder）摊平出来的候选就是这三条等长数组 + 位矩阵的行。
            // 这里断言"扁平重载"与"ForgeCandidate span 重载"对同一批数据给出同一个答案：
            // 两条路径不能各算各的，否则 table 走哪条路就会得到不同结果。
            int[] defIds = { 10, 11, 12 };
            int[] tagMasks = { 0b0001, 0b0001, 0b0010 };
            float[] weights = { 1f, 1f, 1f };
            int[] viable = { 0, 1, 1 };

            var asStructs = new[]
            {
                new ForgeCandidate(10, 0b0001, 1f, 0),
                new ForgeCandidate(11, 0b0001, 1f, 1),
                new ForgeCandidate(12, 0b0010, 1f, 1),
            };

            int flat = ForgeRules.Evaluate(0b0001UL, defIds, tagMasks, weights, viable, Ctx(), out ForgeResult flatResult);
            int span = ForgeRules.Evaluate(0b0001UL, asStructs, Ctx(), out ForgeResult spanResult);

            Assert.AreEqual(span, flat, "扁平重载与结构体重载必须选出同一个 def");
            Assert.AreEqual(spanResult.Outcome, flatResult.Outcome);
            Assert.AreEqual(spanResult.Perf, flatResult.Perf, 1e-5f);
            Assert.AreEqual(spanResult.MatchedCount, flatResult.MatchedCount);
            Assert.AreEqual(11, flat, "10 号不 viable，命中且有次数的只有 11");
        }

        [TestMethod]
        public void ForgeRules_FlatArrays_EqualWeightsDegradeToUniformRandom()
        {
            // 数据层当前没有权重列，binder 给的全部是 1f。这条用例钉住"全 1 也要正常工作"：
            // 三个都 viable、都命中、权重全等 → 随机值落在哪一段就选哪个。
            int[] defIds = { 20, 21, 22 };
            int[] tagMasks = { 0b0001, 0b0001, 0b0001 };
            float[] weights = { 1f, 1f, 1f };
            int[] viable = { 1, 1, 1 };

            // FakeCombatWorld 的随机源恒返回 0.5 → roll = 0.5 * 3 = 1.5，落在第二段 [1,2)
            int picked = ForgeRules.Evaluate(0b0001UL, defIds, tagMasks, weights, viable, Ctx(), out ForgeResult result);

            Assert.AreEqual(21, picked, "等权重退化成均匀随机：roll=1.5 落在第二段");
            Assert.AreEqual(ForgeOutcome.Matched, result.Outcome);
            Assert.AreEqual(1f, result.Perf, 1e-5f, "全部命中 → Perf 100%");
        }

        [TestMethod]
        public void ForgeRules_FlatArrays_MismatchedLengthsUseShortest()
        {
            // 数据层三条数组理应等长；不等长说明 binder 出了问题。这里不崩，按最短的算 ——
            // 一帧不该因为一格数据错位就抛异常，但要能看出来少用了一个（断言长度语义）。
            int[] defIds = { 30, 31, 32 };
            int[] tagMasks = { 0b0001, 0b0001 };   // 只有 2 个
            float[] weights = { 1f, 1f, 1f };
            int[] viable = { 1, 1, 1 };

            int picked = ForgeRules.Evaluate(0b0001UL, defIds, tagMasks, weights, viable, Ctx(), out ForgeResult result);

            Assert.IsTrue(picked == 30 || picked == 31, "只能在前两个候选里选，实际：" + picked);
            Assert.AreEqual(2, result.MatchedCount, "命中数只按最短长度统计");
        }

        [TestMethod]
        public void ContentRegistry_AssignClusterCandidates_FeedsForgeFromFlatArrays()
        {
            // 模拟 binder 的产出：一个簇里有两个物件，摊成三条等长数组 + 一张按物件槽位号取标签的位矩阵。
            var index = BuildIndex();
            int pistolObject = index.ObjectSlotOf("weapon_pistol");
            Assert.IsTrue(pistolObject >= 0, "夹具里的手枪物件必须存在");

            var rifleContract = new WeaponContract(
                id: 102, defId: "weapon_rifle",
                objectDefId: ContentHash.Fnv1a32("weapon_rifle"),
                delivery: WeaponDelivery.Projectile,
                projectile: ContentRef.To("bullet_pistol"));
            int rifleObject = index.RegisterObject(new ObjectArchetype(
                "weapon_rifle", ObjectCapability.Actor | ObjectCapability.Weapon,
                clusterSlot: 1, weaponContract: rifleContract));

            var (_, _, registry, _, _) = BuildRig(index);
            int slotId = registry.SlotIdOf("slot.primary");

            int[] objectSlots = { pistolObject, rifleObject };
            float[] weights = { 1f, 1f };      // 数据层当前没有权重列 → 全 1
            int[] viables = { 1, 1 };
            int[] tagBySlot = new int[index.ObjectCount];   // 位矩阵的行：按物件槽位号
            for (int i = 0; i < tagBySlot.Length; i++)
            {
                tagBySlot[i] = 0b0001;                      // 两个都带这个 tag
            }

            int assigned = registry.AssignClusterCandidates(
                clusterSlot: 1,
                defSpace: ContentSpace.Weapon,
                objectSlots: objectSlots,
                weights: weights,
                viableCounts: viables,
                // 物件槽位号 → def 槽位号：这里簇成员就是物件自己对应的武器契约。
                anchorObjectSlotOfDef: objectSlot =>
                {
                    ObjectArchetype? archetype = index.GetObject(objectSlot);
                    return archetype?.WeaponContract is null ? -1 : index.WeaponSlotOf(archetype.Id);
                },
                tagsOfObjectSlot: objectSlot => tagBySlot[objectSlot]);

            Assert.AreEqual(2, assigned, "两个簇成员都应登记成候选");

            RegisterSlot slot = registry.GetSlot(slotId)!;
            Assert.AreEqual(2, slot.CandidateCount);
            Assert.AreEqual(1f, slot.Candidates[0].Weight, 1e-5f, "权重原样保留（全 1）");

            // 熔炉能直接吃这批候选：两者都命中且 viable → 随机挑一个（FakeRandom 恒 0.5 → roll=1.0，落在第二段）
            int picked = registry.ResolveWithRegister(slotId, Ctx(), recipeTag: 0b0001UL, out ForgeResult result);
            Assert.AreEqual(ForgeOutcome.Matched, result.Outcome);
            Assert.IsTrue(picked == index.WeaponSlotOf("weapon_pistol") || picked == index.WeaponSlotOf("weapon_rifle"),
                "必须从这两个簇成员里选");

            // 回收后回到装配期初始值
            registry.Purge(1);
            Assert.AreEqual(index.WeaponSlotOf("weapon_pistol"),
                registry.Resolve(slotId, Ctx()), "Purge 后回到默认绑定");
        }

        // ─────────────────────────────────────────────────────────────
        // ④ 零分配：这是用户对工厂唯一的硬性性能要求
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void ForgeRules_FlatArrays_SteadyState_ZeroManagedAllocation()
        {
            // 热路径走的就是这条扁平重载（数据层直接交数组，没有 ForgeCandidate[] 中转），
            // 所以"零分配"必须在它身上也成立，否则掏空主路径换来的只是好看的重载。
            int[] defIds = { 10, 11, 12 };
            int[] tagMasks = { 0b0001, 0b0011, 0b0001 };
            float[] weights = { 1f, 1f, 1f };
            int[] viable = { 1, 1, 1 };

            for (int i = 0; i < 100; i++)
            {
                ForgeRules.Evaluate(0b0001UL, defIds, tagMasks, weights, viable, Ctx(), out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int sink = 0;
            for (int i = 0; i < 1000; i++)
            {
                sink += ForgeRules.Evaluate(0b0001UL, defIds, tagMasks, weights, viable, Ctx(), out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0L, allocated,
                "扁平重载在稳态下必须零托管分配，实际分配 " + allocated.ToString() + " 字节");
            Assert.IsTrue(sink > 0, "求值必须真的跑过（防止被优化掉）");
        }

        [TestMethod]
        public void ForgeRules_SteadyState_ZeroManagedAllocation()
        {
            var candidates = new[]
            {
                new ForgeCandidate(defId: 10, tagMask: 0b0001, weight: 0.6f, viableCount: 3),
                new ForgeCandidate(defId: 11, tagMask: 0b0011, weight: 0.4f, viableCount: 1),
            };

            // 预热：JIT、静态初始化、首次查表缓存都在跑前面，不计入断言。
            for (int i = 0; i < 100; i++)
            {
                ForgeRules.Evaluate(0b0001UL, candidates, Ctx(), out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int sink = 0;
            for (int i = 0; i < 1000; i++)
            {
                sink += ForgeRules.Evaluate(0b0001UL, candidates, Ctx(), out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // 用户原话："关于工厂，必须要高性能。使用对象池减少 Node 的 GC"。
            // 1000 次熔炉判定必须一次托管分配都没有 —— 包括装箱、闭包、Lambda、LINQ。
            Assert.AreEqual(0L, allocated,
                "熔炉判定在稳态下必须零托管分配，实际分配 " + allocated.ToString() + " 字节");
            Assert.IsTrue(sink > 0, "求值必须真的跑过（防止被优化掉）");
        }

        // ─────────────────────────────────────────────────────────────
        // ⑤ 铸造系统：队列 → 实体 → 组件取值正确
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void CastSystem_DequeueSpawnsProjectile_WithCorrectState()
        {
            var index = BuildIndex();
            var (world, _, registry, factory, queue) = BuildRig(index);

            var caster = world.Create(new Transform3 { Position = NVector3.Zero, Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One }, new Team { Value = TeamId.Player });
            var cast = new CastSystem(index, registry, queue, factory, Random, defaultWeaponSlotId: registry.Resolve(registry.SlotIdOf("slot.primary"), new ValueContext(FakeCombatWorld.Instance, default, Random)));

            var pipeline = new SystemPipeline(world);
            pipeline.Add(cast, 0);

            int weaponSlotId = index.WeaponSlotOf("weapon_pistol");
            bool enqueued = queue.Enqueue(new CastRequest(
                source: caster,
                origin: NVector3.Zero,
                aimDirection: NVector3.UnitX,
                team: TeamId.Player,
                weaponSlotId: weaponSlotId,
                manaAvailable: 1f));
            Assert.IsTrue(enqueued, "队列必须接受请求");

            pipeline.ExecuteGroup(UpdateGroup.PreUpdate, 0.016f);
            world.Commands.Flush();

            Assert.AreEqual(1, cast.CastsThisFrame);
            Assert.AreEqual(0, cast.RejectedThisFrame);
            Assert.AreEqual(1, cast.LastProjectiles.Count, "本帧产出的子弹数必须可观测");

            Entity bullet = cast.LastProjectiles[0];
            Assert.IsTrue(world.IsAlive(bullet), "工厂造出的实体必须真的存在");
            Assert.IsTrue(world.Has<ProjectileState>(bullet));

            ref ProjectileState state = ref world.Get<ProjectileState>(bullet);
            Assert.AreEqual(index.ProjectileSlotOf("bullet_pistol"), state.ContractSlot, "组件只存槽位号（J2），不存契约引用");
            Assert.AreEqual(caster, state.Source, "链上要记得是谁射的（子弹变成怪物时怪物得认主）");
            Assert.AreEqual(5f, state.Damage, 1e-5f, "伤害来自数据（规则4）");
            Assert.AreEqual(1f, state.Lifetime, 1e-5f);

            ref Team team = ref world.Get<Team>(bullet);
            Assert.AreEqual(TeamId.Player, team.Value, "阵营从发射者继承");

            ref Velocity velocity = ref world.Get<Velocity>(bullet);
            Assert.AreEqual(20f, velocity.Linear.X, 1e-5f, "速度方向 = 瞄准方向 × 数据里的速度");
        }

        // ─────────────────────────────────────────────────────────────
        // ⑥ 武器射出怪物：同一个工厂，只因为 Delivery=SpawnActor 就走了"造人"分支
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Factory_WeaponSpawnsActor_ProducesHurtboxEntity()
        {
            var index = BuildIndex(withMonster: true);
            var (world, _, registry, factory, _) = BuildRig(index);

            var summonWeapon = new WeaponContract(
                id: 103,
                defId: "weapon_summon",
                objectDefId: 0,
                delivery: WeaponDelivery.SpawnActor,
                actor: ContentRef.To("monster_grunt"),
                count: ValueSet.From(ValueRef.Literal(1f)));
            index.RegisterWeapon(summonWeapon);
            int summonSlot = index.WeaponSlotOf("weapon_summon");

            var request = new CastRequest(
                source: Entity.None,
                origin: new NVector3(1f, 0f, 2f),
                aimDirection: NVector3.UnitZ,
                team: TeamId.Enemy,
                weaponSlotId: summonSlot,
                manaAvailable: 1f);

            Assert.IsTrue(factory.TryCast(in request, out Entity produced));
            world.Commands.Flush();

            // ★ 这就是"高自由度拼装"的证据：这段调用与上面"射子弹"完全一样，
            //   差别只在数据里 Delivery 那一列 —— C# 里没有第二个 SpawnMonster() 方法。
            Assert.IsTrue(world.IsAlive(produced));
            Assert.IsTrue(world.Has<Hurtbox>(produced), "被射出的怪物必须是一个能被命中检测看见的角色实体");
            Assert.IsFalse(world.Has<ProjectileState>(produced), "走造人分支时不该带子弹组件");

            ref Transform3 transform = ref world.Get<Transform3>(produced);
            Assert.AreEqual(1f, transform.Position.X, 1e-5f, "怪物出生在武器的枪口位置");
            Assert.AreEqual(2f, transform.Position.Z, 1e-5f);
        }

        // ─────────────────────────────────────────────────────────────
        // ⑦ 子弹爆炸再射子弹：到期派生的第二段，仍是同一个工厂
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Factory_ProjectileExpirySpawnsSecondProjectile()
        {
            var index = BuildIndex(withExplosion: true);
            var (world, _, registry, factory, _) = BuildRig(index);

            int bombSlot = index.ProjectileSlotOf("bullet_bomb");
            var state = new ProjectileState
            {
                ContractSlot = bombSlot,
                Source = Entity.None,
                Lifetime = 0f,
                ChainDepth = 0,
            };

            ProjectileContract bomb = index.GetProjectile(bombSlot)!;
            Assert.IsTrue(factory.TrySpawnDeath(
                bomb,
                position: new NVector3(3f, 0f, 4f),
                direction: NVector3.UnitX,
                team: TeamId.Player,
                state: in state,
                produced: out Entity shard));
            world.Commands.Flush();

            Assert.IsTrue(world.IsAlive(shard), "到期派生出的第二段必须真的落地");
            ref ProjectileState shardState = ref world.Get<ProjectileState>(shard);
            Assert.AreEqual(index.ProjectileSlotOf("bullet_shard"), shardState.ContractSlot);
            Assert.AreEqual(1, shardState.ChainDepth, "派生时必须把链深 +1，否则递归没有边界");

            // 链深到顶后必须停下 —— 这条断言防的是"子弹分裂子弹"的无限递归。
            var deepState = state;
            deepState.ChainDepth = bomb.MaxChainDepth;
            Assert.IsFalse(factory.TrySpawnDeath(
                bomb, position: NVector3.Zero, direction: NVector3.UnitX,
                team: TeamId.Player, state: in deepState, produced: out _),
                "链深达到 maxChainDepth 后必须拒绝再派生");
        }

        // ─────────────────────────────────────────────────────────────
        // ⑧ 弹道推进：位置积分 + 寿命递减 + 到期计数
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void ProjectileAdvance_IntegratesPosition_AndCountsExpiry()
        {
            var index = BuildIndex();
            var (world, _, _, factory, _) = BuildRig(index);

            int bulletSlot = index.ProjectileSlotOf("bullet_pistol");
            var projector = new ProjectileAdvanceSystem(index, factory);
            var pipeline = new SystemPipeline(world);
            pipeline.Add(projector, 0);

            Entity live = world.Create(
                new ProjectileState { ContractSlot = bulletSlot, Lifetime = 10f },
                new Velocity { Linear = new NVector3(2f, 0f, 0f), Angular = NVector3.Zero },
                new Transform3 { Position = NVector3.Zero, Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One });

            Entity dying = world.Create(
                new ProjectileState { ContractSlot = bulletSlot, Lifetime = 0.05f },
                new Velocity { Linear = NVector3.Zero, Angular = NVector3.Zero },
                new Transform3 { Position = NVector3.Zero, Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One });

            // 一步 0.1s：位置 +0.2，寿命 -0.1
            pipeline.ExecuteGroup(UpdateGroup.FixedUpdate, 0.1f);

            ref Transform3 transform = ref world.Get<Transform3>(live);
            Assert.AreEqual(0.2f, transform.Position.X, 1e-5f, "位置必须按 速度 × dt 积分");
            Assert.AreEqual(9.9f, world.Get<ProjectileState>(live).Lifetime, 1e-5f);

            Assert.AreEqual(2, projector.AdvancedLastFrame);
            Assert.AreEqual(1, projector.ExpiredLastFrame, "寿命归零的那颗必须被记为到期");
            Assert.AreEqual(0, projector.DroppedPending, "本轮到期行为是 None，不该有任何投递丢弃");

            // 再推一步：dying 的寿命已经负了，仍然只会计一次"到期"（不是每帧重复计）。
            pipeline.ExecuteGroup(UpdateGroup.FixedUpdate, 0.1f);
            Assert.AreEqual(1, projector.ExpiredLastFrame, "同一颗子弹不会重复计到期");
            Assert.AreEqual(2, projector.TotalAdvanced / 2, "两步 × 两颗 = 4 次推进");
        }

        // ─────────────────────────────────────────────────────────────
        // ⑨ 结构：铸造系统必须在 PreUpdate 组里被管线跑到（不是靠调用方手工调）
        // ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void CastSystem_IsDiscoveredByPipelineInPreUpdateGroup()
        {
            var index = BuildIndex();
            var (world, _, registry, factory, queue) = BuildRig(index);

            var cast = new CastSystem(index, registry, queue, factory, Random,
                defaultWeaponSlotId: index.WeaponSlotOf("weapon_pistol"));
            var pipeline = new SystemPipeline(world);
            pipeline.Add(cast, 0);

            queue.Enqueue(new CastRequest(
                source: Entity.None,
                origin: NVector3.Zero,
                aimDirection: NVector3.UnitX,
                team: TeamId.Player,
                weaponSlotId: index.WeaponSlotOf("weapon_pistol"),
                manaAvailable: 1f));

            // 为什么只用 ExecuteGroup(PreUpdate) 就该吐子弹：宿主按"组"调管线，
            // 铸造不是"谁想起来谁调"的工具函数 —— 它是一个系统，必须由组的执行顺序兜住（J3）。
            pipeline.ExecuteGroup(UpdateGroup.PreUpdate, 0.016f);
            world.Commands.Flush();

            Assert.AreEqual(1, cast.TotalCasts);
            Assert.AreEqual(0, queue.Pending, "队列必须在组执行时被抽干");
            Assert.AreEqual(1, cast.LastProjectiles.Count);
        }
    }
}
