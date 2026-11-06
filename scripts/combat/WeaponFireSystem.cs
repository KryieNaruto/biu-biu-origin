using Game.Actor;
using Game.Core;
using Game.Core.Content;
using Game.Core.Ecs;
using NVector3 = System.Numerics.Vector3;

namespace Game.Combat;

/// <summary>
/// <b>开火系统</b>：把 <see cref="WeaponTrigger"/> 的七种触发方式真正兑现成开火节奏。
/// <para>
/// <b>为什么需要一个独立系统</b>：在它之前，<c>WeaponTrigger</c> 只在 <c>ContentBinder</c> 里被
/// <i>解析</i>，没有任何代码读它 —— 半自动、连发、蓄力在表里填什么都一样，武器全是"一按就打"。
/// 本系统是这一列的<b>唯一消费者</b>，于是「改表 → 手感变化」重新成立。
/// </para>
/// <para>
/// <b>与 <c>SkillCastSystem</c> 的分工</b>：技能系统管五槽冷却与法力门控（"这个技能能不能放"），
/// 本系统管武器自身的射速节奏（"这把枪一秒钟打几发"）。两者都调 <c>CastSystem</c>，
/// 但写的是不同组件（<c>SkillState</c> vs <c>WeaponState</c>），所以互不争抢（J1 单一写入者）。
/// </para>
/// <para>
/// <b>零分配</b>：<c>Query.ForEach</c> 的 <c>ISpanAction</c> 形态，执行器是 struct；
/// 开火不 new 任何托管对象（请求是值类型，队列是定长环）。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.Update)]
[UpdateAfter(typeof(Game.Skill.SkillCastSystem))]
public sealed class WeaponFireSystem : SystemBase
{
    private readonly ContentIndex _index;
    private readonly CastQueue _queue;
    private readonly int _defaultWeaponSlotId;

    private Query _query = null!;
    private float _deltaTime;
    private int _fired;
    private int _blocked;

    /// <summary>构造。</summary>
    /// <param name="index">内容表（读武器契约的 Trigger / FireDelay / ChargeTime）。</param>
    /// <param name="queue">铸造队列（与 <see cref="CastSystem"/> 共用一个实例）。</param>
    /// <param name="defaultWeaponSlotId">未装武器时用的默认武器（&lt; 0 表示没有）。</param>
    public WeaponFireSystem(ContentIndex index, CastQueue queue, int defaultWeaponSlotId = -1)
    {
        _index = index ?? throw new System.ArgumentNullException(nameof(index));
        _queue = queue ?? throw new System.ArgumentNullException(nameof(queue));
        _defaultWeaponSlotId = defaultWeaponSlotId;
    }

    /// <inheritdoc />
    public override string Name => "WeaponFire";

    /// <summary>上一次执行里成功开火的武器数。</summary>
    public int FiredLastFrame { get; private set; }

    /// <summary>上一次执行里被门控挡住（冷却 / 空弹 / 蓄力中）的武器数。</summary>
    public int BlockedLastFrame { get; private set; }

    /// <summary>累计开火次数。</summary>
    public int TotalFired { get; private set; }

    /// <summary>因队列满被丢弃的开火请求累计数（恒为 0 才是健康的）。</summary>
    public int DroppedByQueue { get; private set; }

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        _query = World.Query<WeaponState, Transform3, Team>().With<ActorIntent>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _deltaTime = deltaTime;
        _fired = 0;
        _blocked = 0;

        FirePass pass = new FirePass(this);
        _query.ForEach<WeaponState, Transform3, Team, ActorIntent>(pass);

        FiredLastFrame = _fired;
        BlockedLastFrame = _blocked;
        TotalFired += _fired;
    }

    /// <summary>遍历执行器（struct，零分配）。</summary>
    private readonly struct FirePass : ISpanAction<WeaponState, Transform3, Team, ActorIntent>
    {
        private readonly WeaponFireSystem _owner;

        public FirePass(WeaponFireSystem owner) => _owner = owner;

        public void Execute(
            System.Span<WeaponState> weapons,
            System.Span<Transform3> transforms,
            System.Span<Team> teams,
            System.Span<ActorIntent> intents,
            int count)
        {
            _owner.ProcessChunk(weapons, transforms, teams, intents, count);
        }
    }

    private void ProcessChunk(
        System.Span<WeaponState> weapons,
        System.Span<Transform3> transforms,
        System.Span<Team> teams,
        System.Span<ActorIntent> intents,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            ref WeaponState weapon = ref weapons[i];
            WeaponContract? contract = _index.GetWeapon(weapon.ContractSlot);
            if (contract is null)
            {
                // 契约缺失（数据错误）：不像子弹那样静默跳过，而是不推进冷却 ——
                // 让"武器坏了"表现为"打不出来"，比表现为"能打但打空"更容易被发现。
                continue;
            }

            // 冷却与蓄力是每帧都要推进的时间量，无论本帧打不打。
            if (weapon.CooldownRemaining > 0f)
            {
                weapon.CooldownRemaining -= _deltaTime;
                if (weapon.CooldownRemaining < 0f)
                {
                    weapon.CooldownRemaining = 0f;
                }
            }

            ref ActorIntent intent = ref intents[i];
            uint fireBit = 1u << (int)(SkillSlot.PrimaryAttack - 1);
            bool pressed = (intent.PressedMask & fireBit) != 0;
            bool held = (intent.HeldMask & fireBit) != 0;

            float fireInterval = WeaponValues.ResolveFireInterval(_index, contract, in weapon);
            float chargeTime = WeaponValues.ResolveChargeTime(_index, contract, in weapon);

            bool isCharging = weapon.ChargeElapsed > 0f;

            WeaponFireDecision decision = WeaponFireRules.Evaluate(
                contract.Trigger,
                pressed,
                held,
                fireInterval,
                weapon.CooldownRemaining,
                weapon.AmmoMax > 0 ? weapon.AmmoCurrent : -1,
                weapon.ChargeElapsed,
                chargeTime,
                isCharging);

            // 蓄力计时：进入蓄力就累加，发射/松开就清零。
            if (decision == WeaponFireDecision.Charging && held)
            {
                weapon.ChargeElapsed += _deltaTime;
            }

            if (decision != WeaponFireDecision.Fire)
            {
                if (decision != WeaponFireDecision.Idle)
                {
                    _blocked++;
                }
                // 松开蓄力键但判定为 Idle（从没蓄过）：确保计时归零，不留残值。
                if (!held)
                {
                    weapon.ChargeElapsed = 0f;
                }
                continue;
            }

            if (!TryBuildRequest(ref weapon, contract, in transforms[i], in intent, in teams[i], out CastRequest request))
            {
                _blocked++;
                continue;
            }

            if (!_queue.Enqueue(in request))
            {
                DroppedByQueue++;
                continue;
            }

            // 开火后立刻起冷却（同一帧不会重复触发的保证）。
            weapon.CooldownRemaining = fireInterval > 0f ? fireInterval : 0f;
            weapon.ChargeElapsed = 0f;
            if (weapon.AmmoMax > 0 && weapon.AmmoCurrent > 0)
            {
                weapon.AmmoCurrent--;
            }
            _fired++;
        }
    }

    private bool TryBuildRequest(
        ref WeaponState weapon,
        WeaponContract contract,
        in Transform3 transform,
        in ActorIntent intent,
        in Team team,
        out CastRequest request)
    {
        request = default;

        NVector3 direction = new NVector3(intent.AimX, intent.AimY, intent.AimZ);
        float lengthSquared = direction.LengthSquared();
        direction = lengthSquared > 0f
            ? direction / System.MathF.Sqrt(lengthSquared)
            : NVector3.UnitZ;

        // 枪口位置 = 实体位置 + 朝向偏移（AimOffset 本轮按 0 处理，契约列未接）。
        request = CastRequest.Toward(
            weapon.Owner,
            in transform.Position,
            transform.Position + direction,
            team.Value,
            SkillSlot.PrimaryAttack,
            weapon.ContractSlot,
            CastRequest.NoSlot,
            sourceWeapon: weapon.Owner,
            chainDepth: 0);

        return true;
    }
}
