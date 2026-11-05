using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using NVector3 = System.Numerics.Vector3;

namespace Game.Combat.Factory;

/// <summary>
/// <b>ECS 铸造工厂</b>：把一次 <see cref="CastRequest"/> 变成世界里的一个实体（走 <c>World.Commands</c>，帧末落地）。
/// <para>
/// <b>★ 本轮最重要的等价关系（用户要的"高自由度拼装"）★</b>
/// </para>
/// <para>
/// 下面四种玩法，在代码里是<b>同一处分发、同一套建实体的代码</b>（实际落地在 <see cref="ContentSpawner"/>），
/// 区别只有「契约里哪个引用是非空的」：
/// </para>
/// <list type="table">
/// <item><term>武器射子弹</term><description><c>WeaponContract.Delivery = Projectile</c> + <c>Projectile</c> 非空 → <see cref="ProjectileState"/> 实体</description></item>
/// <item><term>武器射怪物</term><description><c>WeaponContract.Delivery = SpawnActor</c> + <c>Actor</c> 非空 → 带 <see cref="Hurtbox"/> 的角色实体</description></item>
/// <item><term>子弹爆炸再射子弹</term><description><c>ProjectileDeath.SpawnProjectile</c> + <c>OnExpireProjectile</c> 非空 → 同一个 Spawner 再来一次</description></item>
/// <item><term>子弹变成怪物</term><description><c>ProjectileDeath.SpawnActor</c> + <c>OnExpireActor</c> 非空 → 同一个 Spawner，走"造人"分支</description></item>
/// </list>
/// <para>
/// 这就是"基础物件抽象"的目的：<b>物件不是四个类，而是同一张表里"填了哪几列"的差别</b>。
/// 新增一种玩法 = 加一行 Excel（规则4/7），本文件 0 行改动。
/// </para>
/// <para>
/// <b>为什么所有创建都走 <c>World.Commands</c> 而不是 <c>World.Create</c></b>：
/// 铸造可能在 <see cref="ProjectileAdvanceSystem"/> 的 chunk 遍历里被触发（子弹到期分裂）。
/// 遍历中途改世界结构会让正在持有的 Span 失效（J5），所以一律排队到帧末。
/// </para>
/// </summary>
public sealed class EcsObjectFactory : IObjectFactory
{
    private readonly ContentIndex _index;
    private readonly ContentRegistry _registry;
    private readonly ContentSpawner _spawner;
    private readonly IRandomProvider _random;

    private int _castCount;
    private int _rejectCount;

    /// <summary>构造。</summary>
    /// <param name="spawner">真正建实体的那层（三种产物各一个方法）。</param>
    /// <param name="index">内容表。</param>
    /// <param name="registry">寄存器（熔炉派生时写回槽位）。</param>
    /// <param name="random">随机源。</param>
    public EcsObjectFactory(ContentSpawner spawner, ContentIndex index, ContentRegistry registry, IRandomProvider random)
    {
        _spawner = spawner ?? throw new System.ArgumentNullException(nameof(spawner));
        _index = index ?? throw new System.ArgumentNullException(nameof(index));
        _registry = registry ?? throw new System.ArgumentNullException(nameof(registry));
        _random = random ?? throw new System.ArgumentNullException(nameof(random));
    }

    /// <inheritdoc />
    public int CastCount => _castCount;

    /// <inheritdoc />
    public int RejectCount => _rejectCount;

    /// <summary>底层建实体器（测试/调试可以直接用它造单颗子弹）。</summary>
    public ContentSpawner Spawner => _spawner;

    /// <inheritdoc />
    public bool TryCast(in CastRequest request, out Entity produced)
    {
        produced = Entity.None;

        int weaponSlotId = request.WeaponSlotId;
        WeaponContract? weapon = _index.GetWeapon(weaponSlotId);
        if (weapon is null)
        {
            _rejectCount++;
            return false;
        }

        bool ok = weapon.Delivery == WeaponDelivery.SpawnActor
            ? SpawnActorOf(in request, weapon, out produced)
            : SpawnProjectileOf(in request, weapon, out produced);

        if (ok)
        {
            _castCount++;
        }
        else
        {
            _rejectCount++;
        }
        return ok;
    }

    /// <summary>
    /// 投递方式的唯一分叉点：<see cref="WeaponDelivery.SpawnActor"/> 之外的投递方式本轮都产出子弹。
    /// <para>为什么 <c>Instant / Beam / Melee / Aura</c> 也走子弹：它们的"命中结算"属于游戏逻辑（本轮明确不做），
    /// 但表现上仍需要一个能被推进的物件；等命中系统落地后，这几种改走射线/扇形判定即可，本方法多一个分支。</para>
    /// </summary>
    private bool SpawnProjectileOf(in CastRequest request, WeaponContract weapon, out Entity produced)
    {
        int projectileSlot = _index.ProjectileSlotOf(weapon.Projectile);
        TeamId team = weapon.TeamLayer != TeamId.Neutral ? weapon.TeamLayer : request.Team;
        return _spawner.TrySpawnProjectile(
            projectileSlot,
            in request.Origin,
            in request.AimDirection,
            team,
            request.Source,
            request.SourceWeapon,
            request.ChainDepth,
            DamageScaleOf(weapon),
            out produced);
    }

    private bool SpawnActorOf(in CastRequest request, WeaponContract weapon, out Entity produced)
    {
        int actorSlot = _index.ObjectSlotOf(weapon.Actor);
        TeamId team = weapon.TeamLayer != TeamId.Neutral ? weapon.TeamLayer : request.Team;
        return _spawner.TrySpawnActor(actorSlot, in request.Origin, team, out produced);
    }

    /// <summary>
    /// 伤害衰减倍率：子弹链上「传给下一个物件的倍率」。
    /// <para>本轮取 1（不衰减）；等数据表补上 <c>DamageDealtToNext</c> 的解析后端后，这里换成一次规则求值，
    /// 系统其余部分一行不动（规则7 的"改 2 个文件"以内）。</para>
    /// </summary>
    private static float DamageScaleOf(WeaponContract? weapon)
    {
        _ = weapon;
        return 1f;
    }

    /// <summary>
    /// 子弹到期时按契约派生下一段（<see cref="ProjectileDeath.SpawnProjectile"/> / <see cref="ProjectileDeath.SpawnActor"/>）。
    /// <para><b>为什么这个方法属于工厂而不是弹道系统</b>：弹道系统只该回答"到期了"，
    /// "到期后变成什么"是内容问题；把它留在工厂里，弹道系统就不需要知道子弹能变成怪物。</para>
    /// </summary>
    /// <param name="deathContract">到期子弹的契约。</param>
    /// <param name="position">到期位置。</param>
    /// <param name="direction">到期时的飞行方向。</param>
    /// <param name="team">阵营。</param>
    /// <param name="state">到期子弹的状态（取发射者、链深）。</param>
    /// <param name="produced">派生出的实体。</param>
    public bool TrySpawnDeath(
        ProjectileContract deathContract,
        in NVector3 position,
        in NVector3 direction,
        TeamId team,
        in ProjectileState state,
        out Entity produced)
    {
        produced = Entity.None;
        switch (deathContract.OnExpire)
        {
            case ProjectileDeath.SpawnProjectile:
            {
                // 链深到顶就停：防止"子弹分裂子弹"无限递归（数据里 maxChainDepth 是唯一的刹车）。
                if (state.ChainDepth >= deathContract.MaxChainDepth)
                {
                    _rejectCount++;
                    return false;
                }
                int slot = _index.ProjectileSlotOf(deathContract.OnExpireProjectile);
                bool ok = _spawner.TrySpawnProjectile(
                    slot, in position, in direction, team, state.Source, state.SourceWeapon,
                    state.ChainDepth + 1, DamageScaleOf(null), out produced);
                if (ok)
                {
                    _castCount++;
                }
                else
                {
                    _rejectCount++;
                }
                return ok;
            }

            case ProjectileDeath.SpawnActor:
            {
                if (state.ChainDepth >= deathContract.MaxChainDepth)
                {
                    _rejectCount++;
                    return false;
                }
                int slot = _index.ObjectSlotOf(deathContract.OnExpireActor);
                bool ok = _spawner.TrySpawnActor(slot, in position, team, out produced);
                if (ok)
                {
                    _castCount++;
                }
                else
                {
                    _rejectCount++;
                }
                return ok;
            }

            case ProjectileDeath.SpawnEffect:
            {
                // 特效表本轮未接（特效物件没有独立的表，需要与渲染层协商一个 effect 表）。
                // 显式返回 false 而不是静默走默认分支：调用方能从 RejectCount 看出"数据里配了一个还没实现的到期行为"。
                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>寄存器（熔炉派生的槽位写回方）。</summary>
    public ContentRegistry Registry => _registry;

    /// <summary>随机源（工厂内的随机求值用）。</summary>
    public IRandomProvider Random => _random;
}
