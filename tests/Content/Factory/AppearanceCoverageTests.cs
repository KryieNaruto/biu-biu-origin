using System;
using Game.Combat.Factory;
using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NVector3 = System.Numerics.Vector3;

namespace BiuBiuOrigin.Tests.Content.Factory
{
    /// <summary>
    /// <b>「视觉组件齐备」验收用例组</b>：钉住「凡是由工厂造出来的实体，必定带 <see cref="View"/> + <see cref="ViewAppearance"/>」。
    /// <para>
    /// <b>为什么需要这一组用例（真实事故）</b>：2026-11-09 用户发现「只有人物有模型，怪物 / 武器 / 子弹连模型字段都没有」。
    /// 根因不是组件缺失，而是<b>三个产物各自决定要不要带视觉</b>：子弹带 <c>View</c> 不带 <c>ViewAppearance</c>、
    /// 技能子弹连 <c>View</c> 都没有、特效什么都没有。数据表每行都填了 VisualPack、构建期还校验磁盘存在 ——
    /// 校验全过、运行期一个都不读，属于「管道修好了但没人接水」。
    /// </para>
    /// <para>
    /// <b>为什么钉在工厂层而不是渲染层</b>：渲染层（<c>ActorViewDirector</c>）是通用的 —— 只要组件里有外观槽位它就能画。
    /// 漏的是「谁往组件里写字」。所以守卫点必须放在<b>唯一的造实体入口</b>（<c>ContentSpawner</c>）上：
    /// 将来新增第四种产物，走这个工厂就自动被本组用例覆盖。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class AppearanceCoverageTests
    {
        /// <summary>子弹物件的视觉槽位（模拟策划在 combat.xlsx 里填的值）。</summary>
        private const int BulletVisualSlot = 7;

        /// <summary>怪物物件的视觉槽位。</summary>
        private const int MonsterVisualSlot = 8;

        /// <summary>特效物件的视觉槽位。</summary>
        private const int EffectVisualSlot = 9;

        private static readonly FakeRandom Random = new FakeRandom(0.5f);

        /// <summary>
        /// 一份最小内容表：一颗子弹 + 一个怪物 + 一个特效，每个都**显式带视觉槽位**。
        /// <para>刻意不带武器：本组用例只关心「造出来有没有视觉」，与开火链路无关。</para>
        /// </summary>
        private static ContentIndex BuildIndex()
        {
            var index = new ContentIndex { Name = "appearance-test" };

            var bulletContract = new ProjectileContract(
                id: 301,
                defId: "bullet_visual",
                objectDefId: ContentHash.Fnv1a32("bullet_visual"),
                motion: ProjectileMotion.Straight,
                hitPolicy: ProjectileHitPolicy.Single,
                onExpire: ProjectileDeath.None,
                speed: ValueSet.From(ValueRef.Literal(20f)),
                lifetime: ValueSet.From(ValueRef.Literal(1f)),
                damage: ValueSet.From(ValueRef.Literal(5f)),
                radius: ValueSet.From(ValueRef.Literal(0.1f)));

            index.RegisterObject(new ObjectArchetype(
                id: "bullet_visual",
                capabilities: ObjectCapability.Projectile,
                visualSlot: BulletVisualSlot,
                projectileContract: bulletContract));

            index.RegisterObject(new ObjectArchetype(
                id: "monster_visual",
                capabilities: ObjectCapability.Actor,
                clusterSlot: 2,
                radius: 0.4f,
                height: 1.2f,
                visualSlot: MonsterVisualSlot));

            // 特效的能力位是 None（真表里的 fx_* 也是这样）：特效本就不挂 glb，视觉走 shader/粒子。
            // 这里仍给它一个视觉槽位，用来证明「工厂对所有产物一视同仁地写视觉组件」——
            // 哪怕最终模型为空，组件也必须存在，表现层才有机会决定画白模还是不画。
            index.RegisterObject(new ObjectArchetype(
                id: "effect_visual",
                capabilities: ObjectCapability.None,
                visualSlot: EffectVisualSlot));

            return index;
        }

        /// <summary>建一个世界 + 内容表，并把延迟命令刷进世界（否则帧末才存在的实体查不到）。</summary>
        private static (World World, ContentIndex Index, ContentSpawner Spawner) BuildRig()
        {
            ContentIndex index = BuildIndex();
            var world = new World(chunkCapacity: 64);
            var spawner = new ContentSpawner(world, index, Random);
            return (world, index, spawner);
        }

        /// <summary>
        /// <b>中心用例</b>：三种产物都必须同时带 <see cref="View"/> 与 <see cref="ViewAppearance"/>。
        /// <para>失败即代表「又长出一种没有视觉的产物」——这正是本组用例存在的唯一理由。</para>
        /// </summary>
        [TestMethod]
        public void EverySpawnedEntity_CarriesViewAndAppearance()
        {
            (World world, ContentIndex index, ContentSpawner spawner) = BuildRig();

            int bulletSlot = index.ProjectileSlotOf("bullet_visual");
            int monsterSlot = index.ObjectSlotOf("monster_visual");
            int effectSlot = index.ObjectSlotOf("effect_visual");

            Assert.IsTrue(spawner.TrySpawnProjectile(
                bulletSlot, NVector3.Zero, NVector3.UnitX, TeamId.Player,
                Entity.None, Entity.None, 0, 1f, out Entity bullet), "子弹必须造得出来");
            Assert.IsTrue(spawner.TrySpawnActor(monsterSlot, NVector3.Zero, TeamId.Enemy, out Entity monster),
                "怪物必须造得出来");
            Assert.IsTrue(spawner.TrySpawnEffect(effectSlot, NVector3.Zero, 1f, out Entity effect),
                "特效必须造得出来");

            // 延迟创建的命令在帧末提交；本用例直接冲刷，把「帧末才存在」的实体变成真实体。
            world.Commands.Flush();

            AssertMirrored(world, bullet, "子弹", BulletVisualSlot);
            AssertMirrored(world, monster, "怪物", MonsterVisualSlot);
            AssertMirrored(world, effect, "特效", EffectVisualSlot);
        }

        /// <summary>断言实体同时具备 View 与 ViewAppearance，且外观槽位等于内容表里填的值。</summary>
        private static void AssertMirrored(World world, Entity entity, string label, int expectedVisualSlot)
        {
            Assert.IsTrue(world.Has<View>(entity), $"{label}必须带 View 组件（否则宿主不会给它租视图节点）");
            Assert.IsTrue(world.Has<ViewAppearance>(entity),
                $"{label}必须带 ViewAppearance 组件（否则宿主不知道该画成什么样）");

            View view = world.Get<View>(entity);
            Assert.AreEqual(0, view.Slot,
                $"{label}出生时 View.Slot 必须是 0（= 还没租节点，由宿主 VisualBridge 分配），实际 {view.Slot}");

            ViewAppearance appearance = world.Get<ViewAppearance>(entity);
            Assert.AreEqual(expectedVisualSlot, appearance.VisualSlotIndex,
                $"{label}的外观槽位必须来自内容表（{expectedVisualSlot}），实际 {appearance.VisualSlotIndex}");
            Assert.AreNotEqual(0, appearance.VisualKey, $"{label}的 VisualKey 必须是内容表 id 的稳定哈希，不能是 0");
        }

        /// <summary>
        /// 内容表没给视觉槽位时（老数据 / 未配置），实体仍然必须有 <c>ViewAppearance</c>，
        /// 只是槽位为「未解析」——表现层据此回退白模，而不是让实体整个隐身。
        /// <para>这条把「有意为白模」与「忘了接视觉」区分开：前者组件在、后者组件缺。</para>
        /// </summary>
        [TestMethod]
        public void MissingVisualSlot_StillCarriesAppearance_MarkedUnresolved()
        {
            var index = new ContentIndex { Name = "no-visual" };
            index.RegisterObject(new ObjectArchetype(
                id: "monster_blank",
                capabilities: ObjectCapability.Actor,
                radius: 0.4f,
                height: 1.2f));
            index.RegisterObject(new ObjectArchetype(
                id: "bullet_blank",
                capabilities: ObjectCapability.Projectile,
                projectileContract: new ProjectileContract(
                    id: 302,
                    defId: "bullet_blank",
                    objectDefId: ContentHash.Fnv1a32("bullet_blank"),
                    onExpire: ProjectileDeath.None,
                    speed: ValueSet.From(ValueRef.Literal(10f)),
                    lifetime: ValueSet.From(ValueRef.Literal(1f)),
                    damage: ValueSet.From(ValueRef.Literal(1f)),
                    radius: ValueSet.From(ValueRef.Literal(0.1f)))));

            var world = new World(chunkCapacity: 64);
            var spawner = new ContentSpawner(world, index, Random);

            Assert.IsTrue(spawner.TrySpawnActor(
                index.ObjectSlotOf("monster_blank"), NVector3.Zero, TeamId.Enemy, out Entity monster));
            world.Commands.Flush();

            Assert.IsTrue(world.Has<ViewAppearance>(monster),
                "即使内容表没配视觉槽位，也必须带 ViewAppearance —— 区分「有意白模」与「忘了接」");
            Assert.IsTrue(world.Get<ViewAppearance>(monster).VisualSlotIndex < 0,
                "未配置视觉槽位时必须是负值（未解析），表现层据此回退白模");
        }
    }
}
