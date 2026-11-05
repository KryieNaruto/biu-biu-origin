using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Reflection;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;
using Game.Data;
using Game.Skill;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Characters
{
    // ==================================================================
    // 需求 3 / 4 / 5 的验收组：组件形状 + 模块机制 + 移动 + 开火节流
    // ==================================================================
    //
    // 覆盖契约 §0 #3（WASD 移动）、#4（鼠标瞄准射击）、#5（符合 ECS 思想）、§3（组件形状）、§4（模块）。
    //
    // 本组**全部在无引擎进程里跑**：移动/瞄准/技能门控只用到 Game.Core.Ecs，
    // 输入与数值从测试桩注入 —— 这正是「引擎无关可测」那条硬约束的证据。
    // ==================================================================

    // ───────────────────────── 测试夹具 ─────────────────────────

    /// <summary>朴素数值读取器：直接从 <see cref="World"/> 读数值包折叠后的快照（测试用，不碰 Excel）。</summary>
    internal sealed class TestMoveStatReader : IMoveStatReader, IAimStatReader
    {
        private readonly World _world;
        private readonly Dictionary<Entity, IReadOnlyStatView> _views;

        public TestMoveStatReader(World world, Dictionary<Entity, IReadOnlyStatView> views)
        {
            _world = world;
            _views = views;
        }

        public bool HasStats(Entity entity) => _views.ContainsKey(entity);

        public float Read(Entity entity, StatId stat, float fallback) =>
            _views.TryGetValue(entity, out IReadOnlyStatView? view) ? view.Get(stat) : fallback;

        public float MoveSpeed(Entity entity, float fallback) => Read(entity, StatId.MoveSpeed, fallback);
        public float AccelerationSeconds(Entity entity, float fallback) => Read(entity, StatId.MoveAccelTime, fallback);
        public float TurnResponseSeconds(Entity entity, float fallback) => Read(entity, StatId.TurnResponseTime, fallback);
        public float AimRangeMeters(Entity entity, float fallback) => Read(entity, StatId.AimRange, fallback);
    }

    /// <summary>
    /// 可编程意图源：让测试在每一帧指定"按了哪些键、朝哪瞄"。
    /// <para>意图只表达"想怎么动 / 朝哪看"，不含速度 —— 速度由 <see cref="MoveAimSystem"/> 按数值包算。</para>
    /// </summary>
    internal sealed class ScriptedIntentSource : IIntentSource
    {
        /// <summary>固定返回的意图（测试每帧自己改）。</summary>
        public InputIntent Current;

        public InputIntent Poll(int actorId) => Current;
    }

    /// <summary>
    /// 把输入方向写进 <see cref="MoveIntent"/> 的系统（等价于宿主的 <c>PlayerInputIntentSource</c>，
    /// 但它是纯逻辑：把"按了哪几个方向键"翻译成世界空间方向）。
    /// <para>
    /// <b>为什么它出现在测试里</b>：WASD → 方向的翻译属于"操作方案"（宿主层的输入包），
    /// 而"方向 + 数值包 → 速度"属于 <see cref="MoveAimSystem"/>。本测试要证明的是后者，
    /// 所以前者用一个 10 行的等价物代替（它同时钉住"WASD 四个方向 = ±X/±Z 的约定"）。
    /// </para>
    /// </summary>
    internal static class WasdDirections
    {
        /// <summary>W/A/S/D 在该项目的世界约定（俯视角：W = -Z 前、D = +X 右）。</summary>
        public static readonly (string Name, Vector3 Direction)[] All =
        {
            ("W", new Vector3(0f, 0f, -1f)),
            ("S", new Vector3(0f, 0f, 1f)),
            ("A", new Vector3(-1f, 0f, 0f)),
            ("D", new Vector3(1f, 0f, 0f)),
        };
    }

    /// <summary>一个「测完就扔」的小世界：包 + 管线 + 常用系统，省得每个用例抄一遍。</summary>
    internal sealed class HeroSandbox : IDisposable
    {
        public readonly World World = new World();
        public readonly CharacterContentSet Content = new CharacterContentSet().BuildBuiltinV1();
        public readonly Dictionary<Entity, IReadOnlyStatView> Views = new Dictionary<Entity, IReadOnlyStatView>();
        public readonly TestMoveStatReader Stats;
        public readonly SystemPipeline Pipeline;

        public HeroSandbox()
        {
            Stats = new TestMoveStatReader(World, Views);
            Pipeline = new SystemPipeline(World);
        }

        /// <summary>
        /// 按变体 id 生成一个实体（走真实的 <see cref="ActorAssemblySystem"/>），并登记它的数值视图。
        /// <para>
        /// <b>刻意不在这里 Build/Execute 管线</b>：装配系统只在本方法里临时跑一次（用一个独立的小管线），
        /// 跑完立刻移出。若把它留在被测管线里，用例每帧都会白跑一次装配；
        /// 更重要的是，被测系统的 <c>OnInitialize</c> 会提前发生在"实体还没被 Set 上 AimTarget"之前 ——
        /// 那会让查询缓存与本用例的装配顺序耦合，测出来的行为就不是被测系统自己的了。
        /// </para>
        /// </summary>
        public Entity SpawnHero(string variantId, Vector3 at)
        {
            var queue = new ActorSpawnQueue();
            var slots = new RepositoryPackageSlotResolver(Content.StatPacks);
            var assembly = new ActorAssemblySystem(Content.Definitions, Content.Loadouts, Content.StatPacks, slots, queue);
            var bootstrap = new SystemPipeline(World);
            bootstrap.Add(assembly, 0);

            int slot = Content.Definitions.SlotOf(variantId);
            Assert.IsTrue(slot >= 0, $"找不到角色定义 '{variantId}'");
            queue.Enqueue(new ActorSpawnRequest(slot, at));
            bootstrap.Build();
            bootstrap.Execute(0f);

            Assert.AreEqual(1, assembly.Assembled, $"'{variantId}' 必须被装配出一个实体");
            Entity entity = assembly.LastAssembled[0];

            CharacterVariantSpec row = Content.Assemblies.Get(variantId).Variant;
            Views[entity] = Content.StatPacks.Compose(new StatPackageIds(row.BasePackageId, growth: row.GrowthPackageId));
            return entity;
        }

        public void Dispose() => World.Dispose();
    }

    // ───────────────────────── 组件形状（J1 / J2） ─────────────────────────

    /// <summary>
    /// 契约 §3 里"已经定义死的形状"：组件必须是 unmanaged、且**正好一个** <c>[Identity]</c> 字段并且是第一个字段。
    /// <para>
    /// 为什么这几条值得单独立一组守卫：它们是整个 ECS 的性能与正确性前提
    /// （J2 unmanaged = chunk 里没有 GC 引用；[Identity] 位置 = 内核能把实体号写进组件）。
    /// 一个 `string` 字段就能悄悄让整块内存被 GC 扫，而编译不会报错。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class HeroComponentShapeTests
    {
        /// <summary>契约 §3 明确要求的组件（按实现名；契约里的 ViewSlot 已改名为 ViewAppearance）。</summary>
        private static readonly (string Name, Type Type)[] Required =
        {
            ("AimTarget", typeof(AimTarget)),
            ("MoveIntent", typeof(MoveIntent)),
            ("ViewAppearance（契约 §3 的 ViewSlot）", typeof(ViewAppearance)),
            ("ActorTag", typeof(ActorTag)),
            ("WeaponStats", typeof(WeaponStats)),
        };

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        public void HeroComponents_AreUnmanagedValueTypes(int index)
        {
            (string name, Type type) = Required[index];
            Assert.IsTrue(type.IsValueType, $"{name} 必须是 struct（J1：不能是 class）");
            Assert.IsFalse(type.IsGenericTypeDefinition, $"{name} 不应是泛型定义");
            Assert.IsTrue(HasNoManagedReference(type),
                $"{name} 必须是 unmanaged（不含任何引用字段）—— 否则 chunk 会被 GC 扫描（J2）");
            Assert.IsFalse(typeof(Delegate).IsAssignableFrom(type), $"{name} 不能是委托类型");
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        public void HeroComponents_HaveExactlyOneIdentityFieldAndItIsFirst(int index)
        {
            (string name, Type type) = Required[index];
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

            Assert.IsTrue(fields.Length > 0, $"{name} 必须有至少一个字段");
            Assert.AreEqual(typeof(Entity), fields[0].FieldType,
                $"{name} 的第一个字段必须是 Entity（[Identity] Owner）");

            int identityCount = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                bool hasIdentity = fields[i].IsDefined(typeof(IdentityAttribute), inherit: true);
                if (!hasIdentity)
                {
                    continue;
                }

                identityCount++;
                Assert.AreEqual(0, i, $"{name} 的 [Identity] 字段必须<第一个>（ECS.md §4.2 铁律 3）");
                Assert.AreEqual(typeof(Entity), fields[i].FieldType,
                    $"{name} 的 [Identity] 字段类型必须是 Entity，实际是 {fields[i].FieldType.Name}");
            }

            Assert.AreEqual(1, identityCount, $"{name} 必须有且仅有一个 [Identity] 字段，实际 {identityCount} 个");
        }

        [TestMethod]
        public void HeroComponents_DeclareNoMethodsAndNoGameplayConstants()
        {
            // ECS.md：组件里没有方法、没有玩法数值（行为在 system 的 for 循环里，数值在数据表里）。
            foreach ((string name, Type type) in Required)
            {
                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Assert.AreEqual(0, methods.Length,
                    $"{name} 不应declare 任何实例方法（J3：行为属于系统）。实际有：{string.Join(", ", Array.ConvertAll(methods, m => m.Name))}");

                FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                Assert.AreEqual(0, fields.Length,
                    $"{name} 不应有静态字段（尤其不能是 const 玩法数值，规则4）。实际有：{string.Join(", ", Array.ConvertAll(fields, f => f.Name))}");
            }
        }

        /// <summary>
        /// 递归判断类型是否**不含任何托管引用**（= C# 的 unmanaged 约束成立）。
        /// <para>
        /// 为什么不用 <c>RuntimeHelpers.IsReferenceOrContainsReferences&lt;T&gt;()</c>：它是泛型方法，
        /// 需要 <c>where T : struct</c>（泛型约束），而这里拿到的是运行时 <c>Type</c>。
        /// 反射递归只跑一次（测试期），开销无所谓，判据与 C# 的 unmanaged 定义一致。
        /// </para>
        /// </summary>
        private static bool HasNoManagedReference(Type type)
        {
            if (type.IsPointer)
            {
                return false; // 指针本身不允许出现在组件里
            }
            if (type.IsPrimitive || type.IsEnum)
            {
                return true;
            }
            if (!type.IsValueType)
            {
                return false; // 引用类型
            }

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!HasNoManagedReference(fields[i].FieldType))
                {
                    return false;
                }
            }
            return true;
        }

        [TestMethod]
        public void HeroComponents_CanBeStoredAndReadBackThroughWorld()
        {
            // 形状对还不够：必须真的能进 chunk、能读回来、并且 #[Identity] 被内核正确写成实体号。
            using var world = new World();
            var aim = new AimTarget { Point = new Vector3(3f, 0f, 4f), Direction = Vector3.UnitX, HasTarget = true };
            var move = new MoveIntent { Velocity = new Vector3(0f, 0f, -1f), Magnitude = 0.5f };
            var view = new ViewAppearance { VisualKey = 12345, VisualSlotIndex = 6, PaletteSlot = 2, Scale = 1.25f };
            var tag = new ActorTag { AssemblySlot = 0, VariantIndex = 3 };
            var weapon = new WeaponStats { Damage = 7f, BulletSpeed = 20f, RangeMeters = 24f, ShotIntervalSeconds = 0.12f };

            Entity entity = world.Create(aim, move);
            world.Set(entity, view);
            world.Set(entity, tag);
            world.Set(entity, weapon);

            Assert.IsTrue(world.TryGet(entity, out AimTarget gotAim));
            Assert.AreEqual(aim.Direction, gotAim.Direction);
            Assert.AreEqual(entity, gotAim.Owner, "内核必须把实体号写进 AimTarget.Owner（[Identity]）");

            Assert.IsTrue(world.TryGet(entity, out MoveIntent gotMove));
            Assert.AreEqual(move.Magnitude, gotMove.Magnitude);

            Assert.IsTrue(world.TryGet(entity, out ViewAppearance gotView));
            Assert.AreEqual(12345, gotView.VisualKey);
            Assert.AreEqual(entity, gotView.Owner);

            Assert.IsTrue(world.TryGet(entity, out ActorTag gotTag));
            Assert.AreEqual(3, gotTag.VariantIndex);
            Assert.AreEqual(entity, gotTag.Owner);

            Assert.IsTrue(world.TryGet(entity, out WeaponStats gotWeapon));
            Assert.AreEqual(0.12f, gotWeapon.ShotIntervalSeconds, 1e-6f);
            Assert.AreEqual(entity, gotWeapon.Owner);
        }
    }

    // ───────────────────────── 模块机制（契约 §4） ─────────────────────────

    /// <summary>未声明任何模块的探针系统（用来验证 AddModule 的声明校验）。</summary>
    public sealed class UnmodularProbeSystem : SystemBase
    {
        public int Ticks;

        public override string Name => "UnmodularProbe";

        protected override void OnExecute(float deltaTime) => Ticks++;
    }

    /// <summary>只声明 move_standard 的探针系统。</summary>
    [Module("move_standard")]
    public sealed class MoveStandardProbeSystem : SystemBase
    {
        public int Ticks;

        public override string Name => "MoveStandardProbe";

        protected override void OnExecute(float deltaTime) => Ticks++;
    }

    /// <summary>声明两个模块的探针系统（验证"一个系统被多个装配复用"）。</summary>
    [Module("aim_cursor", "fire_primary")]
    public sealed class DualModuleProbeSystem : SystemBase
    {
        public int Ticks;

        public override string Name => "DualModuleProbe";

        protected override void OnExecute(float deltaTime) => Ticks++;
    }

    /// <summary>
    /// 模块驱动的装配（契约 §4 + 规则7「新增一种变体不改系统代码」）。
    /// </summary>
    [TestClass]
    public sealed class HeroModuleTests
    {
        [TestMethod]
        public void AddModule_RegistersOnlyDeclaredSystemsAndRecordsName()
        {
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var moveProbe = new MoveStandardProbeSystem();

            int added = pipeline.AddModule("move_standard", moveProbe);

            Assert.AreEqual(1, added, "AddModule 应返回本次新注册的系统数");
            Assert.AreEqual(1, pipeline.ModuleCount);
            CollectionAssert.AreEqual(new[] { "move_standard" }, new List<string>(pipeline.ModuleNames));
            Assert.IsTrue(pipeline.HasModule("move_standard"));
            Assert.IsTrue(pipeline.Contains(moveProbe), "属于该模块的系统必须被真的注册进管线");

            // 执行一次，确认它真的参与了一帧（注册 ≠ 执行）。
            pipeline.Build();
            pipeline.Execute(0.016f);
            Assert.AreEqual(1, moveProbe.Ticks, "已注册的系统必须在 Execute 里被调用");
        }

        [TestMethod]
        public void AddModule_RejectsSystemThatDoesNotDeclareTheModule()
        {
            // 能拦住「清单写了 move_standard，装配代码却把别的系统传了进来」——
            // 这类错误运行期只表现为"手感不对"，极难定位，所以必须在装配期炸。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var untouched = new UnmodularProbeSystem();

            GameEcsException ex = Assert.ThrowsExactly<GameEcsException>(
                () => pipeline.AddModule("move_standard", untouched));

            StringAssert.Contains(ex.Message, "UnmodularProbe", "异常必须点名是哪个系统没声明模块");
            StringAssert.Contains(ex.Message, "move_standard", "异常必须点名是哪个模块");
            StringAssert.Contains(ex.Message, "[Module(", "异常必须告诉作者该怎么改（加特性）");
            Assert.IsFalse(pipeline.Contains(untouched), "校验失败时不得把系统注册进去");
        }

        [TestMethod]
        public void AddModule_AcceptsSystemDeclaringAnyOfItsModules()
        {
            // [Module("aim_cursor", "fire_primary")] 的系统，两个模块都能把它收进来；
            // 但同一个实例只能注册一次（第二次返回 0，不抛）。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var dual = new DualModuleProbeSystem();

            Assert.AreEqual(1, pipeline.AddModule("aim_cursor", dual));
            Assert.AreEqual(0, pipeline.AddModule("fire_primary", dual), "同一实例重复加入不应重复注册");
            Assert.AreEqual(1, pipeline.Count, "管线里只能有一份这个系统");
            Assert.AreEqual(2, pipeline.ModuleCount, "两个模块名都要被登记（哪怕系统只注册一次）");
            CollectionAssert.AreEqual(new[] { "aim_cursor", "fire_primary" }, new List<string>(pipeline.ModuleNames));
        }

        [TestMethod]
        public void AddModule_EmptyModuleIsRecordedButAddsNoSystem()
        {
            // 镜头包 / HUD 包的系统不在 ECS 管线里（它们在宿主层按帧驱动），
            // 但装配清单必须能列出它们 —— 所以"空模块"是合法的，只是不注册系统。
            using var world = new World();
            var pipeline = new SystemPipeline(world);

            int added = pipeline.AddModule("hud_character", Array.Empty<SystemBase>());

            Assert.AreEqual(0, added);
            Assert.AreEqual(1, pipeline.ModuleCount);
            Assert.IsTrue(pipeline.HasModule("hud_character"), "空模块名仍必须被登记（日志要能打出它）");
            Assert.AreEqual(0, pipeline.Count);
        }

        [TestMethod]
        public void AddModule_IncrementsOncePerModuleAndPreservesOrder()
        {
            // ModuleNames 的顺序 = AddModule 的调用顺序（契约 §8 的日志行靠它可读）。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            string[] ids = { "move_standard", "aim_cursor", "fire_primary", "input_cursor", "camera_ortho_topdown", "hud_character" };

            for (int i = 0; i < ids.Length; i++)
            {
                pipeline.AddModule(ids[i], Array.Empty<SystemBase>());
                Assert.AreEqual(i + 1, pipeline.ModuleCount, $"加入第 {i + 1} 个模块后 ModuleCount 必须等于它");
            }

            CollectionAssert.AreEqual(ids, new List<string>(pipeline.ModuleNames), "模块名必须按加入顺序记录");
        }

        [TestMethod]
        public void AddModule_AfterBuildIsRejected()
        {
            // 模块是装配期概念：Build 之后拓扑序已固定，再塞系统会让"本帧顺序"不可推理。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            pipeline.Build();

            GameEcsException ex = Assert.ThrowsExactly<GameEcsException>(
                () => pipeline.AddModule("move_standard", new MoveStandardProbeSystem()));
            StringAssert.Contains(ex.Message, "Build", "异常必须说明 build 之后不能再加模块");
        }

        [TestMethod]
        public void AddModule_RejectsEmptyModuleId()
        {
            using var world = new World();
            var pipeline = new SystemPipeline(world);

            Assert.ThrowsExactly<GameEcsException>(() => pipeline.AddModule("  ", Array.Empty<SystemBase>()));
            Assert.AreEqual(0, pipeline.ModuleCount, "空 id 不应被登记");
        }

        [TestMethod]
        public void HeroAssemblyModules_CanAllBeAddedToAPipeline()
        {
            // 把数据侧声明的全部模块逐个 AddModule 到一条真实管线：
            // 证明「装配清单 → 管线」这条路是通的，且没有拼错的模块名。
            // （镜头/HUD 模块传空数组：它们的系统在宿主层，不在 ECS 管线里。）
            CharacterContentSet content = new CharacterContentSet().BuildBuiltinV1();
            CharacterAssemblyOf<SkillLoadout> view = content.Assemblies.Get(CharacterContentSet.HeroDefaultVariantId);

            using var world = new World();
            var pipeline = new SystemPipeline(world);
            var probe = new MoveStandardProbeSystem();

            for (int i = 0; i < view.Modules.Count; i++)
            {
                string moduleId = view.Modules.Ids[i];
                SystemBase[] systems = moduleId == "move_standard" ? new SystemBase[] { probe } : Array.Empty<SystemBase>();
                pipeline.AddModule(moduleId, systems);
            }

            Assert.AreEqual(view.Modules.Count, pipeline.ModuleCount,
                "装配清单里的每个模块都必须能被 AddModule 接受（模块名不能只在数据侧存在）");
            CollectionAssert.AreEqual(new List<string>(view.Modules.Ids), new List<string>(pipeline.ModuleNames),
                "管线的模块顺序必须与装配清单一致（日志可对照）");
            Assert.IsTrue(pipeline.Contains(probe));
        }

        [TestMethod]
        public void SystemEntry_ExposesModuleMembershipForDebugPanels()
        {
            // 调试面板要回答"这个系统属于哪些装配模块"，所以条目上必须能查到（而不是只靠反射）。
            using var world = new World();
            var pipeline = new SystemPipeline(world);
            pipeline.AddModule("aim_cursor", new DualModuleProbeSystem());

            SystemPipeline.SystemEntry entry = pipeline.Entries[0];
            Assert.IsTrue(entry.IsInModule("aim_cursor"));
            Assert.IsTrue(entry.IsInModule("fire_primary"));
            Assert.IsFalse(entry.IsInModule("move_standard"));
            Assert.IsFalse(entry.IsInModule(string.Empty), "空 moduleId 恒为 false");
        }
    }

    // ───────────────────────── 移动（契约 §0 #3） ─────────────────────────

    /// <summary>
    /// WASD 移动：意图 + 数值包 → 速度 → 位移（契约 §0 #3）。
    /// <para>
    /// 断言分三层，任何一层坏了都要红：
    /// ① 方向对（四个键 → 四个世界方向）；
    /// ② 大小对（速度 = 数值包里的 MoveSpeed，且**斜向不加速**：归一化）；
    /// ③ 真的动了（MovementSystem 积分后位置变化 ≈ 速度 × 时间）。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class HeroMovementTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>
        /// 跑到"速度基本收敛"所需的帧数。
        /// <para>
        /// 为什么不能直接用 <c>accel / Dt</c>：<c>MoveAimSystem</c> 用的是 <c>alpha = 1 - exp(-dt/response)</c>
        /// 逼近，数学上是**渐近**的 —— 到 <c>accel</c> 时长时只走完约 63%，永远不严格等于目标值。
        /// 要断言"稳态速度 == MoveSpeed"就必须跑到残差可忽略（exp(-t/response) &lt; 1e-4 ≈ 9.2 倍时间常数）。
        /// </para>
        /// </summary>
        private static int ConvergeFrames(float responseSeconds)
        {
            const float SettleTimeConstants = 9.5f; // arch-ok: 数值收敛判据（数学常数），不是玩法数值
            return Math.Max(4, (int)MathF.Ceiling(SettleTimeConstants * MathF.Max(responseSeconds, Dt) / Dt));
        }

        /// <summary>把"方向"写进 MoveIntent 的等价输入包（见 <see cref="WasdDirections"/>）。</summary>
        [UpdateInGroup(UpdateGroup.PreUpdate)]
        private sealed class WasdMoveIntentSystem : SystemBase
        {
            public Vector3 Direction;
            public float Magnitude = 1f;

            protected override void OnExecute(float deltaTime)
            {
                World.Query<MoveIntent>().ForEach((Span<MoveIntent> intents) =>
                {
                    for (int i = 0; i < intents.Length; i++)
                    {
                        intents[i].Velocity = Direction;
                        intents[i].Magnitude = Magnitude;
                    }
                });
            }
        }

        private static (HeroSandbox Box, Entity Hero, WasdMoveIntentSystem Input, MoveAimSystem MoveAim) Build(Vector3 at)
        {
            var box = new HeroSandbox();
            Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, at);

            // MoveAimSystem 有一个刻意的门槛（MoveAimSystem.cs:114）：没有 AimTarget 的实体不归它管
            // （纯移动的 NPC 走自己的包）。所以凡是测"移动"的用例，都必须先给这个角色一个瞄准方向 ——
            // 这不是测试的额外要求，而是"玩家角色总是同时具备移动与瞄准"这条装配事实的体现。
            box.World.Set(hero, new AimTarget
            {
                Point = at + Vector3.UnitZ,
                Direction = Vector3.UnitZ,
                HasTarget = true,
            });

            var input = new WasdMoveIntentSystem();
            var moveAim = new MoveAimSystem(box.Stats, box.Stats);
            box.Pipeline.Add(input, 0);
            box.Pipeline.Add(moveAim, 1);
            box.Pipeline.Add(new MovementSystem(), 2);
            box.Pipeline.Build();
            return (box, hero, input, moveAim);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void Wasd_MovesHeroInAllFourDirections(int index)
        {
            (string name, Vector3 direction) = WasdDirections.All[index];
            (HeroSandbox box, Entity hero, WasdMoveIntentSystem input, MoveAimSystem moveAim) = Build(Vector3.Zero);
            using (box)
            {
                IReadOnlyStatView view = box.Views[hero];
                float speed = view.Get(StatId.MoveSpeed);
                float accel = view.Get(StatId.MoveAccelTime);

                input.Direction = direction;

                // 跑足够多帧让"加速响应"收敛（响应时间来自数值包，不在这里写死帧数以外的假设）。
                int frames = ConvergeFrames(accel);
                Vector3 start = box.World.Get<Transform3>(hero).Position;
                for (int f = 0; f < frames; f++)
                {
                    box.Pipeline.Execute(Dt);
                }

                Vector3 end = box.World.Get<Transform3>(hero).Position;
                Vector3 moved = end - start;

                // ① 方向：必须平行于按下的键（只在该键的轴上有效分量）。
                float along = Vector3.Dot(moved, direction);


                Assert.IsTrue(along > 0f,
                    ShowQuery(box, hero, $"按 {name} 时角色必须沿 {direction} 方向移动，实际位移 {moved} | "));
                Vector3 lateral = moved - direction * along;
                Assert.AreEqual(0f, lateral.Length(), 1e-4f, $"按 {name} 时不应有侧向位移（实际 {lateral}）");

                // ② 大小：稳态速度 = 数值包里的 MoveSpeed（不是写死的数）。
                Velocity v = box.World.Get<Velocity>(hero);
                Assert.AreEqual(speed, v.Linear.Length(), 1e-3f,
                    $"按 {name} 时的稳态速度必须等于数值包里的 MoveSpeed（{speed}）");
                Assert.AreEqual(0f, v.Linear.Y, 1e-4f, "俯视角：线速度不应有竖直分量");

                // ③ 位移总量：至少走过 (速度 × 时间 - 一段加速损失)。
                float traveled = moved.Length();
                Assert.IsTrue(traveled > speed * frames * Dt * 0.5f,
                    $"按 {name} {frames} 帧后位移 {traveled} 过小（速度 {speed}），移动系统可能没有把速度积分进位置");
            }
        }

        /// <summary>
        /// 把"角色身上到底有哪些组件 / 意图系统有没有跑"拼成诊断串，失败时附在消息后面。
        /// <para>为了不在测试里散落 <c>Console.WriteLine</c>：只有红了才需要这些信息。</para>
        /// </summary>
        private static string ShowQuery(HeroSandbox box, Entity hero, string prefix) =>
            string.Concat(
                prefix,
                "组件=", DescribeComponents(box, hero),
                "；Views=", box.Views.ContainsKey(hero) ? "有" : "无",
                "；Pipeline=", box.Pipeline.Count);

        private static string DescribeOrder(HeroSandbox box)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < box.Pipeline.Count; i++)
            {
                sb.Append(box.Pipeline.Entries[i].System.Name).Append(' ').Append(box.Pipeline.Entries[i].Group).Append(" | ");
            }
            return sb.ToString();
        }

        private static string DescribeComponents(HeroSandbox box, Entity hero)
        {
            var present = new List<string>();
            if (box.World.Has<Transform3>(hero)) { present.Add("Transform3"); }
            if (box.World.Has<Velocity>(hero)) { present.Add("Velocity"); }
            if (box.World.Has<MoveIntent>(hero)) { present.Add("MoveIntent"); }
            if (box.World.Has<AimTarget>(hero)) { present.Add("AimTarget"); }
            if (box.World.Has<ActorTag>(hero)) { present.Add("ActorTag"); }
            if (box.World.Has<WeaponStats>(hero)) { present.Add("WeaponStats"); }
            if (box.World.Has<ActorIdentity>(hero)) { present.Add("ActorIdentity"); }
            if (box.World.Has<Team>(hero)) { present.Add("Team"); }
            if (box.World.Has<Health>(hero)) { present.Add("Health"); }
            if (box.World.Has<Mana>(hero)) { present.Add("Mana"); }
            if (box.World.Has<SkillState>(hero)) { present.Add("SkillState"); }
            if (box.World.Has<ActorIntent>(hero)) { present.Add("ActorIntent"); }
            if (box.World.Has<ViewAppearance>(hero)) { present.Add("ViewAppearance"); }
            return present.Count == 0 ? "(无)" : string.Join(",", present);
        }


        [TestMethod]
        public void Wasd_DiagonalInputIsNormalized_NotFasterThanSingleAxis()
        {
            // 键盘斜向（W+D）如果直接把两个单位向量相加，速度会变成 √2 倍 —— 经典手感 bug。
            // 本用例把"斜向更快"变成一条永不回归的断言。
            float singleAxis = RunAndReadSpeed(new Vector3(1f, 0f, 0f));
            float diagonal = RunAndReadSpeed(new Vector3(1f, 0f, -1f));
            float lengthThree = RunAndReadSpeed(new Vector3(1f, 0f, -1f));

            Assert.AreEqual(singleAxis, diagonal, 1e-3f,
                $"斜向速度({diagonal})必须等于单轴速度({singleAxis})：意图必须被归一化");
            Assert.AreEqual(singleAxis, lengthThree, 1e-3f);
        }

        [TestMethod]
        public void Wasd_MagnitudeScalesSpeed_WithoutChangingDirection()
        {
            // Magnitude 是"摇杆推一半走一半"的接缝：手柄半推与键盘全按走同一条代码路径（规则7）。
            (HeroSandbox box, Entity hero, WasdMoveIntentSystem input, _) = Build(Vector3.Zero);
            using (box)
            {
                float speed = box.Views[hero].Get(StatId.MoveSpeed);
                float accel = box.Views[hero].Get(StatId.MoveAccelTime);
                input.Direction = new Vector3(0f, 0f, 1f);
                input.Magnitude = 0.5f;

                int frames = ConvergeFrames(accel);
                for (int f = 0; f < frames; f++)
                {
                    box.Pipeline.Execute(Dt);
                }

                Velocity v = box.World.Get<Velocity>(hero);
                Assert.AreEqual(speed * 0.5f, v.Linear.Length(), 1e-3f, "半推的稳态速度必须是半速");
                Assert.IsTrue(v.Linear.Z > 0f, "方向不变（仍向 +Z）");
            }
        }

        [TestMethod]
        public void Wasd_ZeroIntentDeceleratesToRest()
        {
            // 松开所有键必须停下来（而不是永远滑行）。Stop 语义由 MoveAimSystem 的"逼近 0"承担。
            (HeroSandbox box, Entity hero, WasdMoveIntentSystem input, _) = Build(Vector3.Zero);
            using (box)
            {
                float accel = box.Views[hero].Get(StatId.MoveAccelTime);
                input.Direction = Vector3.UnitZ;

                int frames = ConvergeFrames(accel);
                for (int f = 0; f < frames; f++)
                {
                    box.Pipeline.Execute(Dt);
                }
                Assert.IsTrue(box.World.Get<Velocity>(hero).Linear.Length() > 0f, "前提：先跑起来");

                input.Direction = Vector3.Zero;
                for (int f = 0; f < frames; f++)
                {
                    box.Pipeline.Execute(Dt);
                }

                Assert.AreEqual(0f, box.World.Get<Velocity>(hero).Linear.Length(), 1e-3f,
                    "松开按键后速度必须收敛到 0");
            }
        }

        [TestMethod]
        public void MoveAim_TurnsHeroTowardsAimDirection()
        {
            // 朝向只由 AimTarget.Direction 决定（契约 §3：点与方向分开存）。
            // 断言：模型前方（+Z 约定）转过去对准瞄准方向。
            (HeroSandbox box, Entity hero, WasdMoveIntentSystem input, _) = Build(Vector3.Zero);
            using (box)
            {
                input.Direction = Vector3.Zero;
                box.World.Set(hero, new AimTarget { Point = new Vector3(1f, 0f, 1f), Direction = Vector3.Normalize(new Vector3(1f, 0f, 1f)), HasTarget = true });
                // 转向响应来自数据（TurnResponseTime）；跑够收敛的帧数。
                float turn = box.Views[hero].Get(StatId.TurnResponseTime);
                int frames = ConvergeFrames(turn);
                for (int f = 0; f < frames; f++)
                {
                    box.Pipeline.Execute(Dt);
                }

                Vector3 forward = Vector3.Transform(Vector3.UnitZ, box.World.Get<Transform3>(hero).Rotation);
                Assert.AreEqual(1f / MathF.Sqrt(2f), forward.X, 1e-2f, $"朝向 +X 分量不对：{forward}");
                Assert.AreEqual(1f / MathF.Sqrt(2f), forward.Z, 1e-2f, $"朝向 +Z 分量不对：{forward}");
                Assert.AreEqual(0f, forward.Y, 1e-2f, "俯视角：朝向不应有俯仰");
            }
        }

        [TestMethod]
        public void MoveAim_LookTowardsMapsDirectionsToYaw()
        {
            // 纯函数直测（不建世界）：+Z 为前方，绕 Y 轴偏航。
            Assert.AreEqual(0f, YawOf(Vector3.UnitZ), 1e-4f, "+Z 应当是零偏航（模型前方约定）");
            Assert.AreEqual(MathF.PI / 2f, MathF.Abs(YawOf(Vector3.UnitX)), 1e-4f, "+X 应当是 90° 偏航");
            Assert.AreEqual(MathF.PI, MathF.Abs(YawOf(-Vector3.UnitZ)), 1e-4f, "-Z 应当是 180° 偏航");
        }

        [TestMethod]
        public void MoveAim_SkipsEntitiesWithoutAimTarget()
        {
            // 契约：没有 AimTarget 的实体（纯移动的 NPC）不应被本系统推动 —— 否则"转向"会写到没有瞄准的实体上。
            using (HeroSandbox box = new HeroSandbox())
            {
                CharacterContentSet content = box.Content;
                Entity plain = box.World.Create(
                    new Transform3 { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
                    new Velocity(),
                    new MoveIntent { Velocity = Vector3.UnitZ, Magnitude = 1f });

                box.Views[plain] = content.StatPacks.Compose(new StatPackageIds("test_hero_base", growth: "test_hero_growth"));
                var moveAim = new MoveAimSystem(box.Stats, box.Stats);
                box.Pipeline.Add(moveAim, 0);
                box.Pipeline.Add(new MovementSystem(), 1);
                box.Pipeline.Build();

                for (int f = 0; f < 30; f++)
                {
                    box.Pipeline.Execute(Dt);
                }

                Assert.AreEqual(0, moveAim.MovedCount, "没有 AimTarget 的实体不应被 MoveAimSystem 推动");
                Assert.AreEqual(0f, box.World.Get<Transform3>(plain).Position.Length(), 1e-6f, "因此它也不应移动");
            }
        }

        [TestMethod]
        public void MoveAim_MissingSpeedIsCountedNotGuessed()
        {
            // 数据不齐时必须"可发现"：MissingSpeedCount++ 让宿主能打一行日志，
            // 而不是拿一个写死的兜底移速悄悄跑起来（规则4：代码里不许出现玩法数值）。
            using (HeroSandbox box = new HeroSandbox())
            {
                Entity plain = box.World.Create(
                    new Transform3 { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One },
                    new Velocity(),
                    new MoveIntent { Velocity = Vector3.UnitZ, Magnitude = 1f });
                box.World.Set(plain, new AimTarget { HasTarget = false });

                var moveAim = new MoveAimSystem(box.Stats, box.Stats, MoveAimTuning.None);
                box.Pipeline.Add(moveAim, 0);
                box.Pipeline.Build();
                box.Pipeline.Execute(Dt);

                Assert.AreEqual(1, moveAim.MissingSpeedCount, "读不到移速的实体必须被计数（供宿主报警）");
                Assert.AreEqual(0f, box.World.Get<Velocity>(plain).Linear.Length(), 1e-6f,
                    "兜底值为 0 时不得让实体乱动");
            }
        }

        // ── 工具 ──

        private static float RunAndReadSpeed(Vector3 direction)
        {
            (HeroSandbox box, Entity hero, WasdMoveIntentSystem input, _) = Build(Vector3.Zero);
            using (box)
            {
                float accel = box.Views[hero].Get(StatId.MoveAccelTime);
                input.Direction = direction;
                int frames = ConvergeFrames(accel);
                for (int f = 0; f < frames; f++)
                {
                    box.Pipeline.Execute(Dt);
                }
                return box.World.Get<Velocity>(hero).Linear.Length();
            }
        }

        private static float YawOf(Vector3 direction)
        {
            Quaternion q = MoveAimSystem.LookTowards(direction);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, q);
            return MathF.Atan2(forward.X, forward.Z);
        }
    }

    // ───────────────────────── 开火节流（契约 §0 #4） ─────────────────────────

    /// <summary>
    /// 「按住左键 → 按冷却节流生成投射物」的语义（契约 §0 #4）。
    /// <para>
    /// <b>为什么先用桩而不是等 FireProjectileSkill</b>：节流语义属于 <c>SkillCastSystem</c> + <c>SkillLoadout</c>
    /// （冷却从哪来、什么时候扣、同一帧能不能多放），与"子弹长什么样"无关。
    /// 用记录型 <see cref="RecordingCommandSink"/> 钉住语义后，真实技能只要"在 Cast 里发一条 SpawnProjectile"
    /// 就能直接复用同一套断言 —— 这就是本用例留的价值。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class HeroFireThrottleTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>按给定掩码写入 <see cref="ActorIntent"/> 的等价输入系统。</summary>
        [UpdateInGroup(UpdateGroup.PreUpdate)]
        private sealed class HoldPrimaryButtonSystem : SystemBase
        {
            public uint PressedMask;
            public uint HeldMask;

            protected override void OnExecute(float deltaTime)
            {
                World.Query<ActorIntent>().ForEach((Span<ActorIntent> intents) =>
                {
                    for (int i = 0; i < intents.Length; i++)
                    {
                        intents[i].PressedMask = PressedMask;
                        intents[i].HeldMask = HeldMask;
                    }
                });
            }
        }

        [TestMethod]
        public void PrimaryAttack_BitMatchesContractSlot()
        {
            // 意图掩码的位序是宿主与系统之间的约定（契约 §2）：PrimaryAttack 槽占哪一位必须唯一且稳定。
            uint mask = InputIntent.MaskOf(SkillSlot.PrimaryAttack);
            Assert.AreNotEqual(0u, mask, "PrimaryAttack 槽必须有对应的掩码位");
            Assert.AreNotEqual(InputIntent.MaskOf(SkillSlot.Dash), mask, "不同槽位的掩码位不能相同");
            Assert.AreNotEqual(InputIntent.MaskOf(SkillSlot.WeaponSwap), mask);
            Assert.IsTrue(IntentRules.NextRequestedSlot(mask, 0u) == SkillSlot.PrimaryAttack,
                "只按左键时，意图规则应当选中 PrimaryAttack 槽");
        }

        [TestMethod]
        public void Fire_HoldingLeftButton_RespectsCooldownInterval()
        {
            // 核心语义：按住左键连续射击，但**射速由冷却（数据）决定**，不是每帧一颗。
            using (HeroSandbox box = new HeroSandbox())
            {
                Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);

                // 换一个"记录型射击技能"：冷却/消耗取自数据表的 PrimaryAttack 行，行为是记录一次开火。
                SkillLoadout kit = box.Content.Loadouts.Find("test_hero_kit")!;
                ISkill original = kit.Get(SkillSlot.PrimaryAttack)!;
                float cooldown = original.CooldownSeconds;
                Assert.IsTrue(cooldown > 0f, "测试人物的左键技能必须有正冷却（否则射速会退化成每帧一发）");

                var sink = new RecordingCommandSink();
                var skillContext = new TestSkillContext(sink, new Game.Core.Randomness.DeterministicRandom(7)) { World = box.World };
                var probe = new ProbeSkill(1101, SkillSlot.PrimaryAttack, cooldown, original.ManaCost);
                SkillLoadout probeKit = SkillLoadout.FromSkills("probe_kit", new ISkill[] { probe });
                box.Content.Loadouts.Register(probeKit);
                int loadoutSlot = box.Content.Loadouts.SlotOf("probe_kit");
                box.World.Set(hero, new SkillState
                {
                    LoadoutSlot = loadoutSlot,
                    LastFailure = SkillCastFailure.None,
                    RequestedSlot = SkillSlot.None,
                });

                var hold = new HoldPrimaryButtonSystem { PressedMask = InputIntent.MaskOf(SkillSlot.PrimaryAttack) };
                var cast = new SkillCastSystem(box.Content.Loadouts, skillContext);
                box.Pipeline.Add(hold, 0);
                box.Pipeline.Add(cast, 1);
                box.Pipeline.Build();

                const int frames = 120;
                for (int f = 0; f < frames; f++)
                {
                    box.Pipeline.Execute(Dt);
                }

                // 期望开火次数 = 持续时间 / 冷却（向上取整，容差 ±1 帧）；**绝不能接近帧数**。
                float seconds = frames * Dt;
                float expectedRaw = seconds / cooldown;
                int expectedMin = Math.Max(1, (int)MathF.Floor(expectedRaw) - 1);
                int expectedMax = (int)MathF.Ceiling(expectedRaw) + 1;

                Assert.IsTrue(probe.Casts >= expectedMin && probe.Casts <= expectedMax,
                    $"按住左键 {seconds:0.00}s、冷却 {cooldown:0.00}s 应当开火约 {expectedRaw:0.0} 次，" +
                    $"实际 {probe.Casts} 次（允许 {expectedMin}..{expectedMax}）");
                Assert.IsTrue(probe.Casts < frames / 2,
                    $"开火次数 {probe.Casts} 接近帧数 {frames}：冷却节流没有生效");
                Assert.AreEqual(probe.Casts, sink.Projectiles.Count,
                    "每次成功开火必须产生恰好一个投射物（不多不少）");
            }
        }

        [TestMethod]
        public void Fire_TappingLeftButtonOnce_ProducesOneProjectile()
        {
            using (HeroSandbox box = new HeroSandbox())
            {
                Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);
                var sink = new RecordingCommandSink();
                var skillContext = new TestSkillContext(sink, new Game.Core.Randomness.DeterministicRandom(11)) { World = box.World };

                var probe = new ProbeSkill(1101, SkillSlot.PrimaryAttack, 0.15f, 0f);
                SkillLoadout probeKit = SkillLoadout.FromSkills("probe_kit_tap", new ISkill[] { probe });
                box.Content.Loadouts.Register(probeKit);
                box.World.Set(hero, new SkillState { LoadoutSlot = box.Content.Loadouts.SlotOf("probe_kit_tap"), RequestedSlot = SkillSlot.None });

                var hold = new HoldPrimaryButtonSystem { PressedMask = InputIntent.MaskOf(SkillSlot.PrimaryAttack) };
                box.Pipeline.Add(hold, 0);
                box.Pipeline.Add(new SkillCastSystem(box.Content.Loadouts, skillContext), 1);
                box.Pipeline.Build();

                box.Pipeline.Execute(Dt);       // 这一帧按下 → 开火一次
                hold.PressedMask = 0u;           // 立刻松开
                box.Pipeline.Execute(Dt);
                box.Pipeline.Execute(Dt);

                Assert.AreEqual(1, probe.Casts, "单击一次只应开火一次（掩码是状态不是电平，由意图规则保证）");
                Assert.AreEqual(1, sink.Projectiles.Count);
            }
        }

        [TestMethod]
        public void Fire_InsufficientMana_RecordsFailureAndDoesNotFire()
        {
            // 失败原因要写回组件（契约 §2：HUD 要显示"法力不足"）而不是静默吞掉。
            using (HeroSandbox box = new HeroSandbox())
            {
                Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);
                var sink = new RecordingCommandSink();
                var skillContext = new TestSkillContext(sink, new Game.Core.Randomness.DeterministicRandom(13)) { World = box.World };

                var probe = new ProbeSkill(1999, SkillSlot.PrimaryAttack, 0.1f, 1000f); // 消耗远大于法力
                SkillLoadout kit = SkillLoadout.FromSkills("probe_kit_poor", new ISkill[] { probe });
                box.Content.Loadouts.Register(kit);
                box.World.Set(hero, new SkillState { LoadoutSlot = box.Content.Loadouts.SlotOf("probe_kit_poor"), RequestedSlot = SkillSlot.None });

                var hold = new HoldPrimaryButtonSystem { PressedMask = InputIntent.MaskOf(SkillSlot.PrimaryAttack) };
                box.Pipeline.Add(hold, 0);
                box.Pipeline.Add(new SkillCastSystem(box.Content.Loadouts, skillContext), 1);
                box.Pipeline.Build();
                box.Pipeline.Execute(Dt);

                Assert.AreEqual(0, probe.Casts, "法力不足时不得开火");
                Assert.AreEqual(0, sink.Projectiles.Count);
                Assert.AreEqual(SkillCastFailure.NoMana, box.World.Get<SkillState>(hero).LastFailure,
                    "法力不足的失败原因必须写回组件（HUD 要显示它）");
            }
        }

        [TestMethod]
        public void Fire_CooldownFailureIsVisibleOnTheComponent()
        {
            using (HeroSandbox box = new HeroSandbox())
            {
                Entity hero = box.SpawnHero(CharacterContentSet.HeroDefaultVariantId, Vector3.Zero);
                var sink = new RecordingCommandSink();
                var skillContext = new TestSkillContext(sink, new Game.Core.Randomness.DeterministicRandom(17)) { World = box.World };

                var probe = new ProbeSkill(1101, SkillSlot.PrimaryAttack, 1f, 0f); // 1 秒冷却
                SkillLoadout kit = SkillLoadout.FromSkills("probe_kit_cd", new ISkill[] { probe });
                box.Content.Loadouts.Register(kit);
                box.World.Set(hero, new SkillState { LoadoutSlot = box.Content.Loadouts.SlotOf("probe_kit_cd"), RequestedSlot = SkillSlot.None });

                var hold = new HoldPrimaryButtonSystem { PressedMask = InputIntent.MaskOf(SkillSlot.PrimaryAttack) };
                box.Pipeline.Add(hold, 0);
                var cast = new SkillCastSystem(box.Content.Loadouts, skillContext);
                box.Pipeline.Add(cast, 1);
                box.Pipeline.Build();

                box.Pipeline.Execute(Dt);   // 第一帧：开火
                Assert.AreEqual(1, probe.Casts);

                box.Pipeline.Execute(Dt);   // 第二帧：冷却中
                Assert.AreEqual(1, probe.Casts, "冷却中不应再次开火");
                Assert.AreEqual(SkillCastFailure.Cooldown, box.World.Get<SkillState>(hero).LastFailure,
                    "冷却中的失败原因必须写回组件");
                Assert.AreEqual(1, cast.RejectCount, "被拒绝的次数必须被计数（宿主日志用）");
            }
        }
    }
}
