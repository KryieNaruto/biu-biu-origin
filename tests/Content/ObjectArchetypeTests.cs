using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Content
{
    /// <summary>
    /// 基础物件抽象的核心主张用例组：<b>人物 / 怪物 / 武器 / 子弹不是四个类，而是同一个
    /// <see cref="ObjectArchetype"/> 的不同取值</b>。
    /// <para>
    /// 为什么这些用例最重要：它们直接钉住本轮抽象是否成立 ——
    /// 只要有一条路径要求「新增一个物件类型就要新增一个 C# 类」，规则7（新功能不大改）就破了。
    /// 所以这里用同一段构造代码同时造出武器、子弹、怪物、以及「既是武器又是发射器」「既是子弹又是怪物」
    /// 这种混合体，并断言它们的差异<b>只来自数据</b>。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class ObjectArchetypeTests
    {
        /// <summary>同一份 tag 列头（模拟一张 Excel 的列头行）。</summary>
        private static readonly ContentRules Rules = ContentRules.FromTags(new[] { "fire", "ice", "inherit_weapon", "piercing" });

        [TestMethod]
        public void ThreeArchetypes_SameShape_DifferOnlyByData()
        {
            // 一把枪：能开火，有武器契约，射出子弹。
            var pistol = new ObjectArchetype(
                id: "weapon_pistol",
                capabilities: ObjectCapability.Actor | ObjectCapability.Weapon,
                clusterSlot: 1,
                stats: new StatPackageIds("pistol_base"),
                radius: 0.2f,
                height: 0.3f,
                projectile: ContentRef.To("bullet_pistol"),
                tags: 1 << Rules.TagOf("inherit_weapon"),
                weaponContract: BuildPistolContract());

            // 一颗子弹：是物件、是投射物，能被武器发射出去。
            var bullet = new ObjectArchetype(
                id: "bullet_pistol",
                capabilities: ObjectCapability.Projectile,
                stats: new StatPackageIds("pistol_bullet_base"),
                radius: 0.05f,
                height: 0.05f,
                effect: ContentRef.To("fx_hit_basic"),
                tags: 1 << Rules.TagOf("inherit_weapon"),
                projectileContract: BuildBulletContract());

            // 一只怪：是物件、能开火（拿着枪）、能生成东西。
            var goblin = new ObjectArchetype(
                id: "goblin_shooter",
                capabilities: ObjectCapability.Actor | ObjectCapability.Weapon | ObjectCapability.Emitter,
                clusterSlot: ObjectArchetype.NoCluster,
                stats: new StatPackageIds("goblin_base"),
                radius: 0.4f,
                height: 1.2f,
                weapon: ContentRef.To("weapon_pistol", weight: 3f),
                weaponContract: BuildPistolContract());

            // ① 同一个类型表达三种物件：类型只有 ObjectArchetype 一种。
            Assert.AreEqual(typeof(ObjectArchetype), pistol.GetType());
            Assert.AreEqual(typeof(ObjectArchetype), bullet.GetType());
            Assert.AreEqual(typeof(ObjectArchetype), goblin.GetType());

            // ② 差异完全由「能力位 + 子引用」表达。
            Assert.IsTrue(pistol.HasCapability(ObjectCapability.Weapon));
            Assert.IsFalse(pistol.HasCapability(ObjectCapability.Projectile), "枪本身不按投射物结算");
            Assert.IsTrue(bullet.HasCapability(ObjectCapability.Projectile));
            Assert.IsFalse(bullet.HasCapability(ObjectCapability.Weapon), "子弹自己不开火（但可以同时是武器，见下一个用例）");
            Assert.IsTrue(goblin.HasCapability(ObjectCapability.Actor));
            Assert.IsTrue(goblin.HasCapability(ObjectCapability.Weapon), "持枪的怪同样是武器物件");
            Assert.IsTrue(goblin.HasCapability(ObjectCapability.Emitter));

            Assert.AreEqual(ObjectCapability.Actor | ObjectCapability.Weapon, pistol.Capabilities);
            Assert.AreEqual(ObjectCapability.Projectile, bullet.Capabilities);

            Assert.IsTrue(!pistol.Projectile.IsEmpty, "枪指向子弹（非空引用）");
            Assert.AreEqual(bullet.IdHash, pistol.Projectile.DefId, "子引用用稳定哈希指向子弹（热路径不用字符串）");
            Assert.IsTrue(!goblin.Weapon.IsEmpty, "怪指向枪");
            Assert.AreEqual(3f, goblin.Weapon.Weight, "权重来自表：熔炉随机挑子物件时按它抽");

            // 没有填的子引用必须是「空」，且不能靠「DefId < 0」判断 ——
            // 稳定哈希是有符号 32 位，真实 id 完全可能哈希成负数（bullet_pistol 就是）。
            Assert.IsTrue(pistol.Weapon.IsEmpty, "枪没有指向另一把武器");
            Assert.AreEqual(ContentRef.Empty, pistol.Weapon);
            Assert.IsTrue(bullet.IdHash < 0, "本用例特意用一个哈希为负的真实 id 钉住「负数 ≠ 空引用」");
        }

        [TestMethod]
        public void ObjectCanBeBothWeaponAndEmitter()
        {
            // 混合体的数据形状：Capability 位可以任意组合，系统只问"有没有这个位"。
            var turret = new ObjectArchetype(
                id: "turret_cannon",
                capabilities: ObjectCapability.Structure | ObjectCapability.Weapon | ObjectCapability.Emitter,
                stats: new StatPackageIds("turret_base"),
                projectile: ContentRef.To("bullet_cannon"),
                weaponContract: BuildPistolContract(),
                projectileContract: BuildBulletContract());

            Assert.IsTrue(turret.HasCapability(ObjectCapability.Weapon | ObjectCapability.Emitter),
                "同时是武器又是发射器：HasCapability 支持组合位判定");
            Assert.IsTrue(turret.HasCapability(ObjectCapability.Structure));
            Assert.IsFalse(turret.HasCapability(ObjectCapability.Actor), "固定炮台不参与 AI/控制");
            Assert.IsFalse(turret.HasCapability(ObjectCapability.Consumable));

            // 「子弹自己也是怪物」：同一个物件同时是投射物与 Actor。
            var livingBullet = new ObjectArchetype(
                id: "bullet_living",
                capabilities: ObjectCapability.Projectile | ObjectCapability.Actor,
                stats: new StatPackageIds("living_bullet_base"),
                projectileContract: BuildBulletContract());

            Assert.IsTrue(livingBullet.HasCapability(ObjectCapability.Projectile));
            Assert.IsTrue(livingBullet.HasCapability(ObjectCapability.Actor));
        }

        [TestMethod]
        public void BitMatrix_ExpressesCombinationsWithoutCode()
        {
            // 「子弹吃武器加成 / 会不会点燃」全部是位矩阵里的勾选，代码不认识任何具体 tag。
            int rowWeapon = 0;   // 武器行
            int rowBullet = 1;   // 子弹行
            var building = new ObjectBitMatrix(rowCount: 2, tagCount: Rules.TagCount);
            building.Set(rowWeapon, Rules.TagOf("fire"));
            building.Set(rowBullet, Rules.TagOf("fire"));
            building.Set(rowBullet, Rules.TagOf("inherit_weapon"));
            ObjectBitMatrix matrix = building.Build();

            Assert.IsTrue(matrix.Has(rowBullet, Rules.TagOf("fire")), "这颗子弹是火焰弹 —— 全靠表里勾的");
            Assert.IsTrue(matrix.Has(rowBullet, Rules.TagOf("inherit_weapon")));
            Assert.IsFalse(matrix.Has(rowBullet, Rules.TagOf("ice")));
            Assert.IsFalse(matrix.Has(rowWeapon, Rules.TagOf("inherit_weapon")), "武器行没勾继承位");

            // 同一条 Flag 规则换一个 tag 就能表达完全不同的玩法，不需要新代码。
            var world = new FakeValueWorld { TagCountValue = matrix.TagCount };
            world.SetFlags((byte)Rules.TagOf("fire"), matrix.Mask(rowBullet, Rules.TagOf("fire")));
            var ctx = new ValueContext(world, new StatPackageIds("living_bullet_base"), new FakeRandom(0f));

            Assert.AreEqual(1f, ctx.Resolve(ValueRef.Flag(ValueSource.Flags, (byte)Rules.TagOf("fire"))),
                "位矩阵勾了就求出 1：规则端只认 tag id");
        }

        [TestMethod]
        public void ContentWorld_LooksUpByHashIdAndCluster()
        {
            var pistol = new ObjectArchetype("weapon_pistol", ObjectCapability.Weapon, clusterSlot: 7, displayKey: "ui.weapon.pistol",
                weaponContract: BuildPistolContract());
            var rifle = new ObjectArchetype("weapon_rifle", ObjectCapability.Weapon, clusterSlot: 7, displayKey: "ui.weapon.rifle",
                weaponContract: BuildPistolContract());
            var bullet = new ObjectArchetype("bullet_pistol", ObjectCapability.Projectile, displayKey: "ui.bullet.pistol",
                projectileContract: BuildBulletContract());

            Assert.AreEqual(ObjectArchetype.NoCluster, bullet.ClusterSlot, "缺省值必须是 NoCluster");

            var content = new ReadOnlyContentWorld(Rules);
            content.RegisterAll(new[] { pistol, rifle, bullet });

            Assert.AreEqual(3, content.Count);
            Assert.AreSame(pistol, content.Find(pistol.IdHash), "热路径：按哈希取（ContentRef.DefId 直接喂进来）");
            Assert.AreSame(rifle, content.ById("weapon_rifle"), "加载/编辑器：按字符串取");
            Assert.AreSame(rifle, content.ByName("ui.weapon.rifle"), "也能按显示名取");
            Assert.IsNull(content.Find(ContentRef.None), "空引用查不到东西，且不抛异常");
            Assert.IsNull(content.ById("nope"));

            // 族 / 可替换簇：熔炉「同簇互换」靠它。
            IReadOnlyList<ObjectArchetype> sameCluster = content.ClusterOf(7);
            Assert.AreEqual(2, sameCluster.Count);
            Assert.AreEqual(0, content.ClusterOf(999).Count);
            Assert.AreEqual(0, content.ClusterOf(ObjectArchetype.NoCluster).Count, "NoCluster(-1) 不是一个真实的簇");
        }

        [TestMethod]
        public void ContentWorld_GuardsCapabilityAgainstContract()
        {
            // 没有 Weapon 位却挂了武器契约 = 表写错；读取端必须按"能力位"回答，而不是把契约漏出去。
            var weird = new ObjectArchetype("weird_thing", ObjectCapability.Structure,
                weaponContract: BuildPistolContract());
            var content = new ReadOnlyContentWorld(Rules);
            content.Register(weird);

            Assert.IsNull(content.WeaponOf(weird), "没有 Weapon 位就不该暴露武器契约");
            Assert.IsNull(content.ProjectileOf(weird));
        }

        [TestMethod]
        public void ContentHash_IsStableAndDistinct()
        {
            // 内容 id 的哈希必须跨进程 / 跨平台一致（生成物与运行时算出的必须相同）。
            Assert.AreEqual(ContentHash.Fnv1a32("bullet_pistol"), ContentHash.Fnv1a32("bullet_pistol"));
            Assert.AreNotEqual(ContentHash.Fnv1a32("bullet_pistol"), ContentHash.Fnv1a32("bullet_rifle"));

            // FNV-1a 32 的已知取值（"a" → 0xe40c292c），证明实现没写错而不是"随便一个哈希"。
            Assert.AreEqual(unchecked((int)0xe40c292cu), ContentHash.Fnv1a32("a"));

            var archetype = new ObjectArchetype("x", ObjectCapability.None);
            Assert.AreEqual(ContentHash.Fnv1a32("x"), archetype.IdHash);
        }

        private static WeaponContract BuildPistolContract() => new WeaponContract(
            id: 101,
            defId: "weapon_pistol",
            objectDefId: 0,
            trigger: WeaponTrigger.Auto,
            delivery: WeaponDelivery.Projectile,
            aim: WeaponAimKind.AimPoint,
            inheritWeaponStats: true,
            projectile: ContentRef.To("bullet_pistol"),
            fireDelay: ValueSet.From(ValueRef.Literal(0.2f)),
            damage: ValueSet.From(ValueRef.OfWeaponStat(StatId.WeaponDamage, percent: false, a: 5f)));

        private static ProjectileContract BuildBulletContract() => new ProjectileContract(
            id: 201,
            defId: "bullet_pistol",
            objectDefId: 0,
            motion: ProjectileMotion.Straight,
            hitPolicy: ProjectileHitPolicy.Single,
            onExpire: ProjectileDeath.SpawnEffect,
            speed: ValueSet.From(ValueRef.OfWeaponStat(StatId.WeaponProjectileSpeed, percent: false, a: 20f)),
            lifetime: ValueSet.From(ValueRef.Literal(1f)));
    }
}
