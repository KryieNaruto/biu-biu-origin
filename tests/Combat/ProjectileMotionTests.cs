using System;
using Game.Combat;
using Game.Combat.Factory;
using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NVector3 = System.Numerics.Vector3;

namespace BiuBiuOrigin.Tests.Content.Factory;

/// <summary>
/// <b>弹道运动学测试</b>：用真实 <see cref="World"/> + <see cref="ProjectileAdvanceSystem"/> 逐固定步推进，
/// 断言每种 <see cref="ProjectileMotion"/> 的轨迹<b>确实不同</b>。
/// <para>
/// <b>为什么必须有这组测试</b>：改造前 <c>ProjectileAdvanceSystem</c> 只有一行
/// <c>position += velocity * dt</c> —— 表里填 <c>Homing</c> / <c>Falling</c> / <c>Wave</c>
/// 的子弹<b>全部按直线飞</b>，而 <c>combat.xlsx</c> 里恰好就有这三颗子弹
/// （<c>proj_bullet_201</c> 追踪、<c>proj_bomb_301</c> 重力、<c>proj_bullet_501</c> 蛇形）。
/// 也就是说表里的 <c>Gravity=9.8</c>、<c>HomingTurn=…</c> 是死数据。
/// 本文件把「每种运动有自己的轨迹」钉死，防止再次退化成"全是直线"。
/// </para>
/// </summary>
[TestClass]
public sealed class ProjectileMotionTests
{
    private const float Dt = 1f / 60f;

    /// <summary>恒定随机源。</summary>
    private sealed class FixedRandom : IRandomProvider
    {
        public int NextInt(int minInc, int maxExc) => minInc;

        public float NextFloat() => 0.5f;

        public IRandomProvider Fork(int salt) => this;
    }

    private static readonly FixedRandom Random = new FixedRandom();

    private sealed class Rig
    {
        public World World = null!;
        public ProjectileAdvanceSystem Advance = null!;
        public SystemPipeline Pipeline = null!;
        public ContentIndex Index = null!;
    }

    /// <summary>建一个只含「一颗指定运动的子弹」世界。</summary>
    private static Rig BuildRig(ProjectileMotion motion, float speed = 10f, float lifetime = 1f, float homingTurn = 0f, float gravity = 0f)
    {
        var index = new ContentIndex { Name = "motion-test" };

        var contract = new ProjectileContract(
            id: 901,
            defId: "proj_under_test",
            objectDefId: ContentHash.Fnv1a32("proj_under_test"),
            motion: motion,
            hitPolicy: ProjectileHitPolicy.Single,
            onExpire: ProjectileDeath.None,
            speed: ValueSet.From(ValueRef.Literal(speed)),
            lifetime: ValueSet.From(ValueRef.Literal(lifetime)),
            damage: ValueSet.From(ValueRef.Literal(1f)),
            radius: ValueSet.From(ValueRef.Literal(0.1f)),
            homingTurn: ValueSet.From(ValueRef.Literal(homingTurn)),
            gravity: ValueSet.From(ValueRef.Literal(gravity)));

        index.RegisterObject(new ObjectArchetype("proj_under_test", ObjectCapability.Projectile, projectileContract: contract));

        var world = new World(chunkCapacity: 64);
        var registry = new ContentRegistry(index);
        var spawner = new ContentSpawner(world, index, Random);
        var factory = new EcsObjectFactory(spawner, index, registry, Random);
        var advance = new ProjectileAdvanceSystem(index, factory);

        var pipeline = new SystemPipeline(world);
        pipeline.Add(advance, 0);

        return new Rig { World = world, Advance = advance, Pipeline = pipeline, Index = index };
    }

    /// <summary>
    /// 造一颗子弹实体（从原点朝 +X）。
    /// <para>
    /// 直接 <c>World.Create</c> 而不是走工厂铸造：本文件的被测对象是<b>运动学积分</b>，
    /// 只要把 <c>ProjectileState</c> 的运动学字段填成与工厂一致的形状即可，
    /// 不依赖铸造链路（那条链路由 <c>CombatFactoryTests</c> 负责）。
    /// </para>
    /// </summary>
    private static Entity Fire(Rig rig)
    {
        int slot = rig.Index.ProjectileSlotOf("proj_under_test");
        ProjectileContract contract = rig.Index.GetProjectile(slot)!;

        // 运动学参数全部从契约读，与 ContentSpawner 的装配保持一致（避免测试与产线两套填法）。
        var ctx = new ValueContext(FakeCombatWorld.Instance, default, Random);
        float speed = ValueResolver.Resolve(contract.Speed, in ctx);
        float homingTurn = ValueResolver.Resolve(contract.HomingTurn, in ctx);
        float gravity = ValueResolver.Resolve(contract.Gravity, in ctx);

        Entity bullet = rig.World.Create(
            new ProjectileState
            {
                ContractSlot = slot,
                Lifetime = ValueResolver.Resolve(contract.Lifetime, in ctx),
                Motion = (byte)contract.Motion,
                HomingTurn = homingTurn,
                Gravity = gravity,
                BaseSpeed = speed,
                Age = 0f,
                LaunchDirection = NVector3.UnitX,
            },
            new Velocity { Linear = NVector3.UnitX * speed, Angular = NVector3.Zero },
            new Transform3 { Position = NVector3.Zero, Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One });
        rig.World.Commands.Flush();
        return bullet;
    }

    /// <summary>按固定步长推进 N 步。</summary>
    private static void Step(Rig rig, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            rig.Pipeline.ExecuteGroup(UpdateGroup.FixedUpdate, Dt);
            rig.World.Commands.Flush();
        }
    }

    /// <summary>
    /// 造一个「发射者」实体并把它作为子弹的 Source。
    /// <para>
    /// Orbit / Boomerang 的运动学依赖发射者位置（绕它转 / 朝它折返），没有发射者时
    /// 按设计退化为直线 —— 所以对照测试必须提供发射者，否则测的是退化路径而非目标行为。
    /// </para>
    /// </summary>
    private static Entity AttachSource(Rig rig, Entity bullet, NVector3 sourcePosition)
    {
        Entity source = rig.World.Create(
            new Transform3 { Position = sourcePosition, Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One });
        rig.World.Commands.Flush();
        rig.World.Get<ProjectileState>(bullet).Source = source;
        return source;
    }

    // ── 基线：直线弹 ──

    [TestMethod]
    public void Straight_TravelsInStraightLine()
    {
        Rig rig = BuildRig(ProjectileMotion.Straight, speed: 10f);
        Entity bullet = Fire(rig);

        Step(rig, 60);

        NVector3 position = rig.World.Get<Transform3>(bullet).Position;
        // 1 秒 × 10 米/秒 = 10 米，且 Y/Z 不应偏移。
        Assert.AreEqual(10f, position.X, 0.2f, "直线弹应沿 +X 前进约 10 米");
        Assert.AreEqual(0f, position.Y, 0.01f, "直线弹不应有 Y 偏移");
        Assert.AreEqual(0f, position.Z, 0.01f, "直线弹不应有 Z 偏移");
    }

    // ── Falling：重力让子弹下坠 ──

    [TestMethod]
    public void Falling_DropsUnderGravity()
    {
        Rig rig = BuildRig(ProjectileMotion.Falling, speed: 10f, gravity: 9.8f);
        Entity bullet = Fire(rig);

        Step(rig, 30);

        NVector3 position = rig.World.Get<Transform3>(bullet).Position;
        Assert.IsTrue(position.Y < -0.5f,
            "带重力 9.8 的子弹 0.5 秒后应明显下坠（实际 Y=" + position.Y.ToString("F2") + "）；"
            + "若不成立说明 Gravity 列是死数据。");
    }

    [TestMethod]
    public void StraightAndFalling_DifferVertically()
    {
        // ★ 对照断言：同样的速度/寿命，仅 Motion 不同，落点必须在竖直方向分道扬镳。
        Rig straightRig = BuildRig(ProjectileMotion.Straight, speed: 10f, gravity: 9.8f);
        Entity straightBullet = Fire(straightRig);
        Step(straightRig, 30);

        Rig fallingRig = BuildRig(ProjectileMotion.Falling, speed: 10f, gravity: 9.8f);
        Entity fallingBullet = Fire(fallingRig);
        Step(fallingRig, 30);

        float straightY = straightRig.World.Get<Transform3>(straightBullet).Position.Y;
        float fallingY = fallingRig.World.Get<Transform3>(fallingBullet).Position.Y;

        Assert.AreNotEqual(straightY, fallingY, 0.1f,
            "Straight 与 Falling 的竖直位移必须不同，否则 Motion 列是死的。");
    }

    // ── Homing：朝目标转向 ──

    [TestMethod]
    public void Homing_TurnsTowardNearestTarget()
    {
        Rig rig = BuildRig(ProjectileMotion.Homing, speed: 10f, lifetime: 2f, homingTurn: 360f);
        Entity bullet = Fire(rig);

        // 在子弹初始朝向（+X）的「侧后方」放一个目标，追踪弹应当明显偏离原始 +X 轴。
        var target = rig.World.Create(
            new Transform3 { Position = new NVector3(5f, 0f, 5f), Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One },
            new Hurtbox { Radius = 0.5f, Height = 1f, InvulnerableRemaining = 0f, FlashRemaining = 0f, KnockbackResistance = 0f });
        rig.World.Commands.Flush();

        Step(rig, 60);

        NVector3 position = rig.World.Get<Transform3>(bullet).Position;
        Assert.IsTrue(position.Z > 0.5f,
            "追踪弹应朝位于 +Z 侧的目标转向（实际 Z=" + position.Z.ToString("F2") + "）；"
            + "若不成立说明 HomingTurn 是死数据。");
    }

    [TestMethod]
    public void Homing_WithoutTarget_FliesStraight()
    {
        // 没有目标时不该乱飞（退化为直线是合理行为，不是崩溃）。
        Rig rig = BuildRig(ProjectileMotion.Homing, speed: 10f, homingTurn: 360f);
        Entity bullet = Fire(rig);

        Step(rig, 30);

        NVector3 position = rig.World.Get<Transform3>(bullet).Position;
        Assert.AreEqual(0f, position.Y, 0.01f);
        Assert.AreEqual(0f, position.Z, 0.01f);
        Assert.IsTrue(position.X > 1f, "无目标时追踪弹仍应前进");
    }

    // ── Wave：侧向摆动 ──

    [TestMethod]
    public void Wave_OscillatesLaterally()
    {
        Rig rig = BuildRig(ProjectileMotion.Wave, speed: 10f, lifetime: 2f);
        Entity bullet = Fire(rig);

        // 采样多步，记录偏离原始轴的最大幅度与「是否有正有负」（摆动而非单向偏移）。
        float maxAbsLateral = 0f;
        bool sawPositive = false;
        bool sawNegative = false;
        for (int i = 0; i < 60; i++)
        {
            rig.Pipeline.ExecuteGroup(UpdateGroup.FixedUpdate, Dt);
            rig.World.Commands.Flush();
            NVector3 p = rig.World.Get<Transform3>(bullet).Position;
            float lateral = p.Z;
            if (lateral > maxAbsLateral)
            {
                maxAbsLateral = lateral;
            }
            if (lateral < -maxAbsLateral)
            {
                maxAbsLateral = -lateral;
            }
            if (lateral > 0.05f)
            {
                sawPositive = true;
            }
            if (lateral < -0.05f)
            {
                sawNegative = true;
            }
        }

        Assert.IsTrue(maxAbsLateral > 0.1f,
            "蛇形弹必须产生侧向偏移（实际最大偏移 " + maxAbsLateral.ToString("F3") + "）");
        Assert.IsTrue(sawPositive && sawNegative,
            "蛇形弹必须来回摆动（同时出现正负偏移），而不是单向漂移");
    }

    // ── Orbit / Boomerang：依赖发射者 ──

    [TestMethod]
    public void Orbit_CirclesAroundSource()
    {
        Rig rig = BuildRig(ProjectileMotion.Orbit, speed: 10f, lifetime: 3f);
        Entity bullet = Fire(rig);
        // 发射者放在子弹后方 (0,0,-3)：子弹应绕着它转而不是一路向前。
        Entity source = AttachSource(rig, bullet, new NVector3(0f, 0f, -3f));

        Step(rig, 45);

        NVector3 bulletPos = rig.World.Get<Transform3>(bullet).Position;
        NVector3 sourcePos = rig.World.Get<Transform3>(source).Position;
        float radius = NVector3.Distance(bulletPos, sourcePos);

        // 绕行的判据：到发射者的距离有界（不会像直线弹那样越飞越远）。
        Assert.IsTrue(radius < 8f,
            "环绕弹到发射者的距离应保持有界（实际 " + radius.ToString("F2") + "）；"
            + "若一路增长说明 Orbit 退化成直线。");
    }

    [TestMethod]
    public void Boomerang_CurvesBackTowardSource()
    {
        Rig rig = BuildRig(ProjectileMotion.Boomerang, speed: 10f, lifetime: 3f);
        Entity bullet = Fire(rig);
        Entity source = AttachSource(rig, bullet, new NVector3(0f, 0f, 0f));

        // 记录前段与后段到发射者的距离：回旋镖应当先远离、后接近。
        Step(rig, 10);
        float earlyDistance = NVector3.Distance(rig.World.Get<Transform3>(bullet).Position, rig.World.Get<Transform3>(source).Position);

        Step(rig, 50);
        float lateDistance = NVector3.Distance(rig.World.Get<Transform3>(bullet).Position, rig.World.Get<Transform3>(source).Position);

        Assert.IsTrue(lateDistance < earlyDistance,
            "回旋镖应在后段朝发射者折返（早期距离 " + earlyDistance.ToString("F2")
            + "，后期距离 " + lateDistance.ToString("F2") + "）");
    }

    // ── Anchor：原地不动 ──
    [TestMethod]
    public void Anchor_StaysInPlace()
    {
        Rig rig = BuildRig(ProjectileMotion.Anchor, speed: 10f, lifetime: 2f);
        Entity bullet = Fire(rig);

        Step(rig, 60);

        NVector3 position = rig.World.Get<Transform3>(bullet).Position;
        Assert.AreEqual(0f, position.Length(), 0.001f,
            "锚定弹（地雷 / 延时区域）必须原地不动，实际位移 " + position.Length().ToString("F3"));
    }

    // ── 全枚举覆盖 ──

    [TestMethod]
    public void EveryMotion_AdvancesWithoutProducingNaN()
    {
        // 目的：给 WeaponTrigger 那类的同类保护 —— 将来新增一种运动时，
        // 若实现里出现除零 / 非法叉积，这里会立刻暴露成 NaN。
        foreach (ProjectileMotion motion in Enum.GetValues<ProjectileMotion>())
        {
            Rig rig = BuildRig(motion, speed: 10f, lifetime: 2f, homingTurn: 90f, gravity: 9.8f);
            Entity bullet = Fire(rig);

            Step(rig, 30);

            NVector3 position = rig.World.Get<Transform3>(bullet).Position;
            Assert.IsFalse(float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z),
                "运动 " + motion.ToString() + " 产生了 NaN（除零 / 退化叉积）");
            Assert.IsFalse(float.IsInfinity(position.X) || float.IsInfinity(position.Y) || float.IsInfinity(position.Z),
                "运动 " + motion.ToString() + " 产生了 Infinity");
        }
    }

    [TestMethod]
    public void DifferentMotions_ProduceDifferentTrajectories()
    {
        // ★ 最强的一条：把所有运动的落点收集起来，若"各不相同"的结论不成立，
        // 说明又有枚举值退化成直线了。
        var landing = new System.Collections.Generic.Dictionary<ProjectileMotion, NVector3>();
        foreach (ProjectileMotion motion in Enum.GetValues<ProjectileMotion>())
        {
            Rig rig = BuildRig(motion, speed: 10f, lifetime: 2f, homingTurn: 90f, gravity: 9.8f);
            Entity bullet = Fire(rig);

            // Orbit / Boomerang 需要发射者才有独特轨迹（没有则按设计退化为直线）。
            if (motion == ProjectileMotion.Orbit || motion == ProjectileMotion.Boomerang)
            {
                AttachSource(rig, bullet, new NVector3(0f, 0f, -3f));
            }

            // 追踪弹需要一个目标才能体现转向。
            if (motion == ProjectileMotion.Homing)
            {
                rig.World.Create(
                    new Transform3 { Position = new NVector3(5f, 0f, 5f), Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One },
                    new Hurtbox { Radius = 0.5f, Height = 1f });
                rig.World.Commands.Flush();
            }

            Step(rig, 45);
            landing[motion] = rig.World.Get<Transform3>(bullet).Position;
        }

        int distinct = 0;
        var seen = new System.Collections.Generic.List<NVector3>();
        foreach (NVector3 p in landing.Values)
        {
            bool duplicate = false;
            foreach (NVector3 s in seen)
            {
                if (NVector3.Distance(s, p) < 0.05f)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                distinct++;
                seen.Add(p);
            }
        }

        // 8 种运动里，Anchor（原地）与 Straight（直线）之外都应有各自的轨迹。
        // 放宽到「至少 6 种不同」，给未来可能新增的相似运动留余地，
        // 但仍能抓住"多种运动退化成同一轨迹"的回归。
        Assert.IsTrue(distinct >= 6,
            "8 种运动至少应产生 6 条不同轨迹，实际只有 " + distinct.ToString()
            + " 条。说明有 Motion 枚举值没有真正实现（退化成直线）。");
    }
}
