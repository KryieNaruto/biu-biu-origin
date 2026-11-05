using System;
using System.Collections.Generic;
using System.IO;
using Game.Combat;
using Game.Combat.Factory;
using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using Game.Data.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NVector3 = System.Numerics.Vector3;

namespace BiuBiuOrigin.Tests.Content.Data;

/// <summary>
/// <b>端到端回归</b>：真实 <c>combat.xlsx</c> → <see cref="ContentBinder"/> → <see cref="ContentIndex"/>
/// → <see cref="CastSystem"/> / <see cref="ContentSpawner"/> → 世界里的实体组件集合。
/// <para>
/// <b>为什么要跨层跑这一条</b>：用户的核心需求是「高自由度拼装」——
/// <i>子弹可能是怪物、武器射出怪物、武器具有子弹属性、子弹具有武器属性</i>。
/// 绑定器单测只能证明"表读进来了"，工厂单测只能证明"手搓的契约能造实体"；
/// <u>两者之间断链</u>（列名漂移、引用指向错表、契约没挂上物件）只有把整条链跑通才看得见。
/// 本文件的用例全部从真 <c>.xlsx</c> 出发，因此"改表 → 玩法变化"是被证明的，而不是被声明的。
/// </para>
/// <para>
/// <b>断言什么</b>：不是"函数返回 true"，而是<b>产物实体身上的组件集合</b>——
/// 那是"它是个怪物还是一颗子弹"在 ECS 里的唯一判据（角色有 <see cref="Hurtbox"/> 且没有
/// <see cref="ProjectileState"/>；子弹两者都有）。
/// </para>
/// </summary>
[TestClass]
public sealed class ContentPlayLoopTests
{
    /// <summary>固定随机源：本文件全部走字面量规则值，随机只用于契约里可能出现的 rand。</summary>
    private static readonly FixedRandom Random = new FixedRandom(0.5f);

    /// <summary>
    /// <b>组合闭环 ①：武器射出怪物</b>。
    /// <para>
    /// <c>orb_501</c> 的 <c>Delivery = SpawnActor</c> 且 <c>ActorRef = slime_401</c>。用真表绑出来的内容集
    /// 驱动 <see cref="CastSystem"/>，产出的实体必须是<b>角色</b>（有 <see cref="Hurtbox"/>、无
    /// <see cref="ProjectileState"/>），而不是一颗子弹。
    /// </para>
    /// <para>
    /// <b>为什么这条能证明"物件不是四个类"</b>：这段铸造代码与"武器射子弹"完全相同
    /// （同一个 <see cref="EcsObjectFactory.TryCast"/>），差别只有表里 <c>Delivery</c> 那一格。
    /// </para>
    /// </summary>
    [TestMethod]
    public void WeaponCastingSpawnActor_ProducesActorEntityNotProjectile()
    {
        ContentSet set = RealContent();
        int orbWeapon = set.Index.WeaponSlotOf("orb_501");
        Assert.IsTrue(orbWeapon >= 0, "真表里应有武器 orb_501。");

        WeaponContract orb = set.Index.GetWeapon(orbWeapon)!;
        Assert.AreEqual(WeaponDelivery.SpawnActor, orb.Delivery, "orb_501 的 Delivery 应为 SpawnActor。");
        Assert.IsFalse(orb.Actor.IsEmpty, "orb_501 的 ActorRef 非空（射出的是怪物）。");

        var rig = BuildRig(set);
        var request = new CastRequest(
            source: Entity.None,
            origin: new NVector3(1f, 0f, 2f),
            aimDirection: NVector3.UnitZ,
            team: TeamId.Enemy,
            weaponSlotId: orbWeapon,
            manaAvailable: 1f);

        Assert.IsTrue(rig.Factory.TryCast(in request, out Entity produced), "铸造应成功产出实体。");
        rig.World.Commands.Flush();

        Assert.IsTrue(rig.World.IsAlive(produced), "产物必须真的落地到世界里。");
        Assert.IsTrue(rig.World.Has<Hurtbox>(produced), "武器射出的怪物必须是可被命中检测看见的角色实体。");
        Assert.IsFalse(rig.World.Has<ProjectileState>(produced), "走造人分支时不该带子弹组件（否则会像子弹一样被判飞行/到期）。");
    }

    /// <summary>
    /// <b>组合闭环 ②：子弹到期再射一颗子弹</b>。
    /// <para>
    /// <c>proj_arrow_202</c> 的 <c>OnExpire = SpawnProjectile</c> 且 <c>OnExpireProjectile = bullet_201</c>。
    /// 让第一段到期，第二段必须带着<b>正确的契约槽位</b>与 <c>ChainDepth + 1</c> 落地。
    /// </para>
    /// <para>
    /// <b>为什么链深必须 +1</b>：这是"子弹生子弹"唯一的递归边界。少加一次就会无限派生，
    /// 表现是帧率掉到 0 而不是报错 —— 所以它值得一条断言。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ProjectileExpirySpawnsSecondProjectileFromRealTable()
    {
        ContentSet set = RealContent();
        int arrowSlot = set.Index.ProjectileSlotOf("arrow_202");
        Assert.IsTrue(arrowSlot >= 0, "真表里应有子弹 arrow_202（Projectiles 表按 ObjectId 指向它）。");

        ProjectileContract arrow = set.Index.GetProjectile(arrowSlot)!;
        Assert.AreEqual(ProjectileDeath.SpawnProjectile, arrow.OnExpire, "arrow_202 的 OnExpire 应为 SpawnProjectile。");
        Assert.IsFalse(arrow.OnExpireProjectile.IsEmpty, "arrow_202 的 OnExpireProjectile 非空（到期再射一颗）。");

        var rig = BuildRig(set);

        // 第一段：先按"武器射出子弹"正常铸造一颗（证明链子的起点也在真表上）。
        // 注意：bow_201 射的是 bullet_201（OnExpire = None），所以它本身不会分裂；
        // 分裂子弹是 arrow_202 —— 本用例从它的契约直接驱动第二段。
        int bowSlot = set.Index.WeaponSlotOf("bow_201");
        Assert.IsTrue(bowSlot >= 0, "真表里应有武器 bow_201。");
        var request = new CastRequest(
            source: Entity.None,
            origin: NVector3.Zero,
            aimDirection: NVector3.UnitZ,
            team: TeamId.Player,
            weaponSlotId: bowSlot,
            manaAvailable: 1f);
        Assert.IsTrue(rig.Factory.TryCast(in request, out Entity firstBolt), "第一段子弹应铸造成功。");
        rig.World.Commands.Flush();
        Assert.IsTrue(rig.World.IsAlive(firstBolt), "第一段子弹必须落地。");
        Assert.IsTrue(rig.World.Has<ProjectileState>(firstBolt), "武器射出的应是子弹实体（带 ProjectileState）。");

        // 第二段：让 arrow_202 到期，派生 OnExpireProjectile 指定的那颗。
        var arrowState = new ProjectileState
        {
            ContractSlot = arrowSlot,
            Source = Entity.None,
            Lifetime = 0f,
            ChainDepth = 0,
        };

        Assert.IsTrue(rig.Factory.TrySpawnDeath(
            arrow,
            position: new NVector3(3f, 0f, 4f),
            direction: NVector3.UnitX,
            team: TeamId.Player,
            state: in arrowState,
            produced: out Entity second), "到期应派生第二段子弹。");
        rig.World.Commands.Flush();

        Assert.IsTrue(rig.World.IsAlive(second), "派生出的第二段必须真的落地。");
        ref ProjectileState secondState = ref rig.World.Get<ProjectileState>(second);

        // 第二段的契约必须就是 OnExpireProjectile 指定的那颗（在真表里是 bullet_201 → proj_bullet_201）。
        string expectedDef = arrow.OnExpireProjectile.Id;
        string actualDef = set.Index.ProjectileIdOf(secondState.ContractSlot);
        Assert.AreEqual(expectedDef, actualDef,
            "第二段子弹的契约应是 OnExpireProjectile 指定的 '" + expectedDef + "'，实际 '" + actualDef + "'。");
        Assert.AreEqual(arrowState.ChainDepth + 1, secondState.ChainDepth, "派生时链深必须 +1（递归边界）。");
    }

    /// <summary>
    /// <b>组合闭环 ③：子弹到期变成怪物</b>（用户原话"子弹可能是怪物"）。
    /// <para>
    /// <c>proj_bomb_301</c> 的 <c>OnExpire = SpawnActor</c>、<c>OnExpireActor</c> 指向某个物件。
    /// 到期产出的必须是角色实体而不是子弹。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ProjectileExpirySpawnsActorFromRealTable()
    {
        ContentSet set = RealContent();
        int bombSlot = set.Index.ProjectileSlotOf("bullet_301");
        Assert.IsTrue(bombSlot >= 0, "真表里应有子弹 bullet_301（Projectiles 表按 ObjectId 指向它）。");

        ProjectileContract bomb = set.Index.GetProjectile(bombSlot)!;
        Assert.AreEqual(ProjectileDeath.SpawnActor, bomb.OnExpire, "bullet_301 的 OnExpire 应为 SpawnActor。");
        Assert.IsFalse(bomb.OnExpireActor.IsEmpty, "bullet_301 的 OnExpireActor 非空（子弹变成怪物）。");

        var rig = BuildRig(set);
        var state = new ProjectileState
        {
            ContractSlot = bombSlot,
            Source = Entity.None,
            Lifetime = 0f,
            ChainDepth = 0,
        };

        Assert.IsTrue(rig.Factory.TrySpawnDeath(
            bomb,
            position: new NVector3(5f, 0f, 6f),
            direction: NVector3.UnitX,
            team: TeamId.Enemy,
            state: in state,
            produced: out Entity spawned), "到期应派生出怪物实体。");
        rig.World.Commands.Flush();

        Assert.IsTrue(rig.World.IsAlive(spawned), "派生出的怪物必须落地。");
        Assert.IsTrue(rig.World.Has<Hurtbox>(spawned), "子弹变成的怪物必须是个角色（可被命中）。");
        Assert.IsFalse(rig.World.Has<ProjectileState>(spawned), "变成怪物后不该带子弹组件。");
    }

    /// <summary>
    /// <b>组合闭环 ④：武器具有子弹属性</b>。
    /// <para>
    /// 这不是新玩法，而是一条<u>数据可达性</u>断言：<c>orb_501</c> 身为武器却挂了 <c>tag.projectile</c>
    /// 之外的组合标签，且 <c>Weapons.ProjectileRef</c> 指向的物件与 <c>Objects.Projectile</c> 完全对得上 ——
    /// 也就是"枪里有子弹、子弹能被打出去"这条路没有被外键体系挡死。
    /// </para>
    /// <para>
    /// 真正"武器继承子弹数值"的求值在 <c>ValueContext/ValueResolver</c>，不在本轮数据层范围内；
    /// 本用例只钉住数据层能让这条链成立（引用非空 + 契约非空 + 索引可达）。
    /// </para>
    /// </summary>
    [TestMethod]
    public void WeaponHoldingProjectileContract_IsReachableThroughIndex()
    {
        ContentSet set = RealContent();

        // 找一把「射击类」武器：它有 ProjectileRef，物件行也挂了 Projectile，且索引里能取到契约。
        int checkedCount = 0;
        for (int weaponSlot = 0; weaponSlot < set.Index.WeaponCount; weaponSlot++)
        {
            WeaponContract weapon = set.Index.GetWeapon(weaponSlot)!;
            if (weapon.Projectile.IsEmpty)
            {
                continue;
            }

            int objectSlot = set.Index.ObjectSlotOf(set.Index.WeaponIdOf(weaponSlot));
            Assert.IsTrue(objectSlot >= 0, "武器 '" + weapon.DefId + "' 的物件行必须能在索引里找到。");

            ObjectArchetype archetype = set.Index.GetObject(objectSlot)!;
            Assert.IsFalse(archetype.Projectile.IsEmpty,
                "武器 '" + weapon.DefId + "' 填了 ProjectileRef，它的物件行也应填 Projectile 列（两表口径必须一致）。");
            Assert.IsNotNull(archetype.ProjectileContract,
                "物件 '" + archetype.Id + "' 应挂上已绑定的子弹契约，而不是空引用。");
            Assert.AreEqual(archetype.Projectile.Id, weapon.Projectile.Id,
                "武器与物件的子弹引用必须指向同一颗（否则打出去的东西和物件上挂的不是一回事）。");
            checkedCount++;
        }

        Assert.IsTrue(checkedCount > 0, "真表里应至少有一把带子弹契约的武器。");
    }

    // ─────────────────────────────────────────────────────────────
    // 装配
    // ─────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public World World = null!;
        public ContentIndex Index = null!;
        public ContentRegistry Registry = null!;
        public EcsObjectFactory Factory = null!;
        public CastQueue Queue = null!;
        public CastSystem Cast = null!;
    }

    /// <summary>用真表构造内容集：读 combat.xlsx 五张表 → 绑定器 → 运行时内容集。</summary>
    private static ContentSet RealContent()
    {
        XlsxReader reader = XlsxReader.Open(CombatXlsxPath());
        return ContentBinder.BuildFrom(
            ReadTable(reader, "Tags"),
            ReadTable(reader, "Objects"),
            ReadTable(reader, "Weapons"),
            ReadTable(reader, "Projectiles"),
            ReadTable(reader, "StatPacks"),
            packages: null);
    }

    /// <summary>
    /// 把一张 sheet 读成 <see cref="ContentTable"/>：第 1 行是表头，数据从第 6 行起
    /// （表头前三行是策划说明、第 4 行是字段规约、第 5 行是 #data 标记，见 docs/rules/04 §5.2）。
    /// </summary>
    private static ContentTable ReadTable(XlsxReader reader, string sheetName)
    {
        const int DataStartRowIndex = 5;   // arch-ok: Excel 四行表头 + #data 标记行的结构常量

        string[][] grid = reader.ReadSheet(sheetName);
        var columns = new List<string>();
        if (grid.Length > 0)
        {
            for (int c = 0; c < grid[0].Length; c++)
            {
                string name = grid[0][c].Trim();
                if (name.Length > 0)
                {
                    columns.Add(name);
                }
            }
        }

        var rows = new List<ContentRow>();
        for (int r = DataStartRowIndex; r < grid.Length; r++)
        {
            string[] line = grid[r];
            bool any = false;
            for (int c = 0; c < line.Length; c++)
            {
                if (!string.IsNullOrWhiteSpace(line[c]))
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                continue;
            }

            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < columns.Count && c < line.Length; c++)
            {
                cells[columns[c]] = line[c] ?? string.Empty;
            }
            rows.Add(new ContentRow(sheetName, r + 1, cells));
        }
        return new ContentTable(sheetName, columns, rows);
    }

    /// <summary>从测试产物目录往上找仓库根（以 data/excel/combat.xlsx 存在为标志）。</summary>
    private static string CombatXlsxPath()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "data", "excel", "combat.xlsx");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到 data/excel/combat.xlsx —— 测试需从仓库根运行。");
    }

    /// <summary>把内容集接成一套可铸造的世界：世界 + 寄存器 + 工厂 + 队列 + CastSystem。</summary>
    private static Rig BuildRig(ContentSet set)
    {
        var world = new World(chunkCapacity: 64);
        var registry = new ContentRegistry(set.Index);
        var spawner = new ContentSpawner(world, set.Index, Random);
        var factory = new EcsObjectFactory(spawner, set.Index, registry, Random);
        var queue = new CastQueue(16);

        // 声明一个主武器槽位并绑到表里的第一把武器：CastSystem 需要一个"默认武器"才能处理
        // 未指定 WeaponSlotId 的请求。
        int firstWeapon = set.Index.WeaponSlotOf("orb_501");
        registry.DeclareSlot("slot.primary", clusterSlot: ObjectArchetype.NoCluster,
            defSpace: ContentSpace.Weapon, defaultDefId: firstWeapon);

        var cast = new CastSystem(
            set.Index, registry, queue, factory, Random,
            defaultWeaponSlotId: firstWeapon);

        return new Rig
        {
            World = world,
            Index = set.Index,
            Registry = registry,
            Factory = factory,
            Queue = queue,
            Cast = cast,
        };
    }

    /// <summary>恒定随机源（本文件全部走字面量规则值）。</summary>
    private sealed class FixedRandom : IRandomProvider
    {
        private readonly float _value;

        public FixedRandom(float value) => _value = value;

        public int NextInt(int minInc, int maxExc) => minInc;

        public float NextFloat() => _value;

        public IRandomProvider Fork(int salt) => this;
    }
}
