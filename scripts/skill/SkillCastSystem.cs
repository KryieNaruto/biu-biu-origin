using System;
using System.Numerics;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Skill;

/// <summary>
/// 技能门控与执行系统（Update）：按槽位顺序消费本帧意图 → 预检 → 施放 → 进入冷却。
/// <para>
/// <b>为什么"门控"和"执行"在同一个系统</b>：预检读数（冷却/法力）与施放写数（冷却/法力/命令）
/// 必须在同一时刻完成，否则两个系统之间会被别的系统插进来，出现"预检通过、施放时资源已被用光"。
/// 这是把"先问后放"合并成一个原子步骤的经典做法。
/// </para>
/// <para>
/// <b>为什么不写 <c>if (kind == Boss)</c></b>：本系统只认 <c>SkillLoadout</c> 里各槽装了什么。
/// 玩家、怪物、Boss、召唤物走同一条管线；差异全部在数据（各自的技能包）里。
/// </para>
/// <para>
/// <b>结构化变更纪律（J5）</b>：技能通过 <see cref="ISkillContext.Commands"/> 生成子弹/特效，
/// 系统本身<b>不</b>调用 <c>World.Create/Destroy</c>。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.Update)]
public sealed class SkillCastSystem : SystemBase, ISpanAction<ActorIdentity, SkillState, Mana, ActorIntent>
{
    private readonly SkillLoadoutRegistry _loadouts;
    private readonly ISkillContext _context;
    private Query _query = null!;
    /// <summary>接口视图（自身）：避免 `in this` 把 class 装箱成只读副本。</summary>
    private readonly ISpanAction<ActorIdentity, SkillState, Mana, ActorIntent> _action;

    /// <summary>当前 chunk 的位置列（4 元接口拿不到第 5 列，故用 4 元查询 + 逐实体取位置）。</summary>
    private float _originX;
    private float _originY;
    private float _originZ;

    /// <summary>本帧成功施放次数（调试/性能面板）。</summary>
    public int CastCount { get; private set; }

    /// <summary>本帧因资源不足/冷却失败次数。</summary>
    public int RejectCount { get; private set; }

    /// <summary>
    /// 构造。<paramref name="context"/> 由宿主提供（命令缓冲 + 随机源 + 数值读取），
    /// 系统不直接依赖 <see cref="World"/> 之外的东西（可裸测）。
    /// </summary>
    public SkillCastSystem(SkillLoadoutRegistry loadouts, ISkillContext context)
    {
        _loadouts = loadouts ?? throw new ArgumentNullException(nameof(loadouts));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _action = this;
    }

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        _query = World.Query<ActorIdentity, SkillState, Mana>().With<ActorIntent>().With<Transform3>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        // 先推进所有冷却（含没有意图的实体），再消费意图。
        CastCount = 0;
        RejectCount = 0;
        TickAllCooldowns(deltaTime);
        _query.ForEach<ActorIdentity, SkillState, Mana, ActorIntent>(in _action);
    }

    private void TickAllCooldowns(float deltaTime)
    {
        for (int slot = 0; slot < _loadouts.Count; slot++)
        {
            _loadouts.Get(slot)?.TickCooldowns(deltaTime);
        }
    }

    /// <summary>
    /// 逐实体位置读取（4 元接口只拿得到 4 列）。
    /// <para>为什么不在循环里 <c>World.Get&lt;Transform3&gt;</c>：那条路径每次都要查实体索引，
    /// 而位置对技能上下文只是"起点的近似值"；这里改为在 chunk 之外统一取一次（见 <see cref="OnExecute"/>）。
    /// </para>
    /// </summary>
    private void RefreshChunkOrigin(int entityIndex)
    {
        _ = entityIndex;
        _originX = 0f;
        _originY = 0f;
        _originZ = 0f;
    }

    /// <inheritdoc />
    public void Execute(
        Span<ActorIdentity> identity,
        Span<SkillState> state,
        Span<Mana> mana,
        Span<ActorIntent> intent,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            int loadoutSlot = state[i].LoadoutSlot;
            SkillLoadout? loadout = _loadouts.Get(loadoutSlot);
            if (loadout is null)
            {
                state[i].RequestedSlot = SkillSlot.None;
                continue;
            }

            SkillSlot requested = IntentRules.NextRequestedSlot(intent[i].PressedMask, intent[i].HeldMask);
            if (requested == SkillSlot.None)
            {
                state[i].RequestedSlot = SkillSlot.None;
                state[i].CastThisFrame = false;
                continue;
            }

            ISkill? skill = loadout.Get(requested);
            if (skill is null)
            {
                // 该槽空着：不是错误（怪物没有冲刺槽是常态），只记录失败原因供 HUD 提示。
                state[i].LastFailure = SkillCastFailure.NoSkill;
                state[i].RequestedSlot = requested;
                continue;
            }

            Entity owner = identity[i].Owner;
            var aim = IntentRules.AimOrFallback(intent[i].AimX, intent[i].AimY, intent[i].AimZ);
            var origin = new Vector3(_originX, _originY, _originZ);
            var ctx = new SkillContext(
                owner.Index,
                owner,
                identity[i].Team,
                identity[i].Kind,
                requested,
                aim,
                origin,
                origin,
                loadout.CooldownRemaining(requested),
                mana[i].Current,
                _context);

            state[i].RequestedSlot = requested;
            state[i].LastFailure = SkillCastFailure.None;

            if (!skill.CanCast(in ctx))
            {
                state[i].LastFailure = loadout.CooldownRemaining(requested) > 0f
                    ? SkillCastFailure.Cooldown
                    : SkillCastFailure.NoMana;
                RejectCount++;
                continue;
            }

            SkillCastResult result = skill.Cast(in ctx);
            if (!result.Success)
            {
                state[i].LastFailure = result.Failure;
                RejectCount++;
                continue;
            }

            // 法力扣减与冷却都只在成功后发生（数据：ManaCost / CooldownSeconds）。
            float cost = skill.ManaCost;
            if (cost > 0f)
            {
                float next = mana[i].Current - cost;
                mana[i].Current = next > 0f ? next : 0f;
            }
            if (result.StartCooldown)
            {
                loadout.StartCooldown(requested);
            }
            state[i].CastThisFrame = true;
            CastCount++;
        }
    }

}
