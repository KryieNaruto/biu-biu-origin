using System;
using Game.Actor;
using Game.Combat;
using Game.Combat.Factory;
using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NVector3 = System.Numerics.Vector3;

namespace BiuBiuOrigin.Tests.Content.Factory;

/// <summary>
/// <b>开火系统集成测试</b>：把 <see cref="WeaponFireSystem"/> 挂进真实 <see cref="SystemPipeline"/>，
/// 用真实 <c>combat.xlsx</c> 绑定的内容，验证「按表里填的 Trigger 真的产生了不同开火节奏」。
/// <para>
/// <b>为什么必须有这组测试</b>：<see cref="WeaponFireRulesTests"/> 只证明规则函数正确，
/// 但规则函数正确 ≠ 系统被接上了 —— 历史上 <c>WeaponTrigger</c> 恰恰是"规则没写给谁看"。
/// 本文件把「意图 → 系统 → 队列」这条线整个跑一遍，任何一环断开都会失败。
/// </para>
/// </summary>
[TestClass]
public sealed class WeaponFireSystemTests
{
    private const float Dt = 1f / 60f;

    private sealed class FixedRandom : IRandomProvider
    {
        public int NextInt(int minInc, int maxExc) => minInc;
        public float NextFloat() => 0.5f;
        public IRandomProvider Fork(int salt) => this;
    }

    private static readonly FixedRandom Random = new FixedRandom();

    /// <summary>开火测试的最小世界：一把指定 Trigger 的武器实体 + 开火系统。</summary>
    private sealed class Rig
    {
        public World World = null!;
        public WeaponFireSystem Fire = null!;
        public SystemPipeline Pipeline = null!;
        public CastQueue Queue = null!;
        public Entity Shooter;

        public int FireBit => 1 << ((int)SkillSlot.PrimaryAttack - 1);

        public void SetIntent(bool pressed, bool held)
        {
            ref ActorIntent intent = ref World.Get<ActorIntent>(Shooter);
            intent.PressedMask = pressed ? (uint)FireBit : 0u;
            intent.HeldMask = held ? (uint)FireBit : 0u;
            intent.AimX = 0f;
            intent.AimY = 0f;
            intent.AimZ = 1f;
        }

        /// <summary>推进一步并清空队列，返回本步开火数。</summary>
        public int Step(float interval, bool pressed, bool held)
        {
            SetIntent(pressed, held);
            Pipeline.ExecuteGroup(UpdateGroup.Update, interval);
            World.Commands.Flush();
            int fired = Fire.FiredLastFrame;
            while (Queue.TryDequeue(out _)) { }
            return fired;
        }
    }

    private static Rig BuildRig(WeaponTrigger trigger, float fireDelay = 0.2f, float chargeTime = 1f, int ammoMax = 0)
    {
        var index = new ContentIndex { Name = "fire-test" };
        int weaponHash = ContentHash.Fnv1a32("weapon_under_test");

        var weapon = new WeaponContract(
            id: 101,
            defId: "weapon_under_test",
            objectDefId: weaponHash,
            trigger: trigger,
            delivery: WeaponDelivery.Projectile,
            aim: WeaponAimKind.AimPoint,
            projectile: ContentRef.To("proj_under_test"),
            fireDelay: ValueSet.From(ValueRef.Literal(fireDelay)),
            chargeTime: ValueSet.From(ValueRef.Literal(chargeTime)));

        var projectile = new ProjectileContract(
            id: 201,
            defId: "proj_under_test",
            objectDefId: ContentHash.Fnv1a32("proj_under_test"),
            speed: ValueSet.From(ValueRef.Literal(20f)),
            lifetime: ValueSet.From(ValueRef.Literal(1f)),
            damage: ValueSet.From(ValueRef.Literal(5f)),
            radius: ValueSet.From(ValueRef.Literal(0.1f)));

        index.RegisterObject(new ObjectArchetype("proj_under_test", ObjectCapability.Projectile, projectileContract: projectile));
        index.RegisterObject(new ObjectArchetype("weapon_under_test", ObjectCapability.Weapon, weaponContract: weapon));

        var world = new World(chunkCapacity: 64);
        var registry = new ContentRegistry(index);
        var spawner = new ContentSpawner(world, index, Random);
        var factory = new EcsObjectFactory(spawner, index, registry, Random);
        var queue = new CastQueue(256);
        int weaponSlot = index.WeaponSlotOf("weapon_under_test");

        var fire = new WeaponFireSystem(index, queue, weaponSlot);
        var pipeline = new SystemPipeline(world);
        pipeline.Add(fire, 0);

        Entity shooter = world.Create(
            new WeaponState
            {
                ContractSlot = weaponSlot,
                AmmoCurrent = ammoMax,
                AmmoMax = ammoMax,
                CooldownRemaining = 0f,
                ChargeElapsed = 0f,
                TeamLayer = TeamId.Player,
            },
            new Transform3 { Position = NVector3.Zero, Rotation = System.Numerics.Quaternion.Identity, Scale = NVector3.One },
            new Team { Value = TeamId.Player });
        // World.Create 最多三组件，ActorIntent 用 Set 补上（结构化变更仍在 Flush 前，安全）。
        world.Set(shooter, new ActorIntent());
        world.Commands.Flush();

        return new Rig { World = world, Fire = fire, Pipeline = pipeline, Queue = queue, Shooter = shooter };
    }

    // ── Auto：按住即连射 ──

    [TestMethod]
    public void Auto_ProductionRateFollowsFireDelay()
    {
        Rig rig = BuildRig(WeaponTrigger.Auto, fireDelay: 0.2f);

        // 按住 1 秒：0.2 秒间隔 → 约 5 发（首次立刻发射）。
        int total = 0;
        for (int i = 0; i < 60; i++)
        {
            total += rig.Step(Dt, pressed: false, held: true);
        }

        Assert.IsTrue(total >= 4 && total <= 7,
            "Auto 武器按住 1 秒、间隔 0.2 秒应开火约 5 次，实际 " + total.ToString()
            + " 次。若为 0 说明 Trigger 仍未接上；若为 60 说明门控没生效。");
    }

    // ── Semi：按住只打一发 ──

    [TestMethod]
    public void Semi_HoldingFiresOnlyOnce()
    {
        Rig rig = BuildRig(WeaponTrigger.Semi, fireDelay: 0.05f);

        int total = 0;
        for (int i = 0; i < 60; i++)
        {
            // 只有第一帧是 pressed（边沿），之后一直 held。
            total += rig.Step(Dt, pressed: i == 0, held: true);
        }

        Assert.AreEqual(1, total,
            "Semi 武器按住 1 秒只应开火 1 次（边沿触发），实际 " + total.ToString() + " 次。");
    }

    // ── Charge：按住期间不开火，松手才发射 ──

    [TestMethod]
    public void Charge_DoesNotFireWhileHeld_FiresOnRelease()
    {
        Rig rig = BuildRig(WeaponTrigger.Charge, fireDelay: 0.1f, chargeTime: 0.5f);

        // 按住 0.5 秒：一发都不该有（蓄力中）。
        int whileHeld = 0;
        for (int i = 0; i < 30; i++)
        {
            whileHeld += rig.Step(Dt, pressed: i == 0, held: true);
        }
        Assert.AreEqual(0, whileHeld, "蓄力期间不应开火，实际 " + whileHeld.ToString() + " 次");

        // 松手：应该发射一发。
        int onRelease = rig.Step(Dt, pressed: false, held: false);
        Assert.AreEqual(1, onRelease, "松开蓄力键应当发射 1 发，实际 " + onRelease.ToString() + " 次");
    }

    // ── 弹药门控 ──

    [TestMethod]
    public void EmptyMagazine_StopsFiring()
    {
        Rig rig = BuildRig(WeaponTrigger.Auto, fireDelay: 0.05f, ammoMax: 2);

        int total = 0;
        for (int i = 0; i < 60; i++)
        {
            total += rig.Step(Dt, pressed: false, held: true);
        }

        Assert.AreEqual(2, total, "弹夹只有 2 发时最多开火 2 次，实际 " + total.ToString() + " 次");
        Assert.AreEqual(0, rig.World.Get<WeaponState>(rig.Shooter).AmmoCurrent, "打空后弹药应为 0");
    }

    // ── 事件驱动：不吃输入 ──

    [TestMethod]
    public void Triggered_IgnoresInputEntirely()
    {
        Rig rig = BuildRig(WeaponTrigger.Triggered);

        int total = 0;
        for (int i = 0; i < 60; i++)
        {
            total += rig.Step(Dt, pressed: true, held: true);
        }

        Assert.AreEqual(0, total, "事件驱动的武器不应响应输入，实际开火 " + total.ToString() + " 次");
    }

    // ── 冷却真的在走 ──

    [TestMethod]
    public void Cooldown_Decrements()
    {
        Rig rig = BuildRig(WeaponTrigger.Semi, fireDelay: 0.2f);

        rig.Step(Dt, pressed: true, held: false);
        float afterFire = rig.World.Get<WeaponState>(rig.Shooter).CooldownRemaining;
        Assert.IsTrue(afterFire > 0f, "开火后必须起冷却，实际 " + afterFire.ToString("F3"));

        rig.Step(Dt, pressed: false, held: false);
        float later = rig.World.Get<WeaponState>(rig.Shooter).CooldownRemaining;
        Assert.IsTrue(later < afterFire, "冷却必须随时间递减");
    }

    // ── 与 Semi 对照：Auto 在同一输入下打得更多 ──

    [TestMethod]
    public void AutoFiresMoreThanSemi_OnSameHeldInput()
    {
        Rig auto = BuildRig(WeaponTrigger.Auto, fireDelay: 0.1f);
        Rig semi = BuildRig(WeaponTrigger.Semi, fireDelay: 0.1f);

        int autoTotal = 0;
        int semiTotal = 0;
        for (int i = 0; i < 60; i++)
        {
            autoTotal += auto.Step(Dt, pressed: i == 0, held: true);
            semiTotal += semi.Step(Dt, pressed: i == 0, held: true);
        }

        Assert.IsTrue(autoTotal > semiTotal,
            "同样的「按一下再按住」输入，Auto 应明显多于 Semi（Auto=" + autoTotal.ToString()
            + "，Semi=" + semiTotal.ToString() + "）。这个差值就是 Trigger 列存在的意义。");
    }
}
