using System;
using System.Numerics;
using Game.Core;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Actor;

/// <summary>
/// 意图采集系统（PreUpdate）：把外部输入翻译成 <see cref="ActorIntent"/> 组件，并写下"本帧请求哪个技能槽"。
/// <para>
/// <b>为什么输入不直接调用技能</b>：技能系统可能在同一帧里被多个来源驱动（玩家、AI、脚本、夺舍切换），
/// 若输入直接 <c>Cast()</c>，施放顺序将取决于事件调用顺序（不可复现）。统一走"写意图 → 技能系统按
/// 槽位顺序消费"，顺序由数据（槽位号）决定，同 seed 同结果。
/// </para>
/// <para>
/// <b>为什么有三个 <see cref="IIntentSource"/></b>：玩家读键鼠、AI 读行为树、脚本读过场轨道。
/// 系统按实体的 <see cref="ActorIdentity.Control"/> 选源 —— 因此"怪物被夺舍"只需把 Control 改成
/// <see cref="ControlScheme.PlayerInput"/>，本系统与技能系统都不需要知道"这是玩家还是怪"。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.PreUpdate)]
public sealed class ActorIntentSystem : SystemBase, ISpanAction<ActorIdentity, ActorIntent, Transform3>
{
    private readonly IIntentSource _playerInput;
    private readonly IIntentSource _ai;
    private readonly IIntentSource _script;
    private Query _query = null!;

    /// <summary>接口视图（自身）：避免 <c>in this</c> 把 class 装箱成只读副本。</summary>
    private readonly ISpanAction<ActorIdentity, ActorIntent, Transform3> _action;

    /// <summary>
    /// 构造。三个意图源必须注入（不能为 null）：读 <c>Input</c> 的实现在宿主层，
    /// Core/系统层不接触引擎（J4）。AI 源在 AI 系统落地前可用 <see cref="NullIntentSource"/> 占位。
    /// </summary>
    public ActorIntentSystem(IIntentSource playerInput, IIntentSource ai, IIntentSource script)
    {
        _playerInput = playerInput ?? throw new ArgumentNullException(nameof(playerInput));
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _script = script ?? throw new ArgumentNullException(nameof(script));
        _action = this;
    }

    /// <summary>本帧采集过的实体数（调试用）。</summary>
    public int PolledCount { get; private set; }

    /// <summary>
    /// 按控制方案给某个实体采集一次意图的公开入口（生成/夺舍系统用它给<u>本帧新建</u>的实体补意图）。
    /// <para>为什么需要：意图系统在 PreUpdate 早于装配运行；新实体要到装配之后才存在，
    /// 若不在生成时补一次，它在<u>出生帧</u>就没有输入 —— 对手感是可见的（按着左键生成却不开火）。
    /// 这也是 AI 实体在出生帧就能行动的原因。</para>
    /// </summary>
    public InputIntent PollFor(ControlScheme control, int actorIndex)
    {
        IIntentSource? source = control switch
        {
            ControlScheme.PlayerInput => _playerInput,
            ControlScheme.Ai => _ai,
            ControlScheme.Script => _script,
            _ => null,
        };
        return source is null ? default : source.Poll(actorIndex);
    }

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        _query = World.Query<ActorIdentity, ActorIntent, Transform3>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        PolledCount = 0;
        _query.ForEach<ActorIdentity, ActorIntent, Transform3>(in _action);
    }

    /// <inheritdoc />
    public void Execute(Span<ActorIdentity> identity, Span<ActorIntent> intent, Span<Transform3> transform, int count)
    {
        for (int i = 0; i < count; i++)
        {
            ControlScheme control = identity[i].Control;
            IIntentSource? source = control switch
            {
                ControlScheme.PlayerInput => _playerInput,
                ControlScheme.Ai => _ai,
                ControlScheme.Script => _script,
                _ => null,
            };

            if (source is null)
            {
                // 无控制：清空意图（不让上一帧的按键"粘住"）。
                intent[i].PressedMask = 0u;
                intent[i].HeldMask = 0u;
                intent[i].Sequence++;
                continue;
            }

            // 实体身份用 (index, generation) 压缩成键：意图源需要"是哪个实体在按下"。
            Entity entity = identity[i].Owner;
            InputIntent polled = source.Poll(entity.Index);
            intent[i].PressedMask = polled.PressedMask;
            intent[i].HeldMask = polled.HeldMask;
            intent[i].AimX = polled.AimDirectionX;
            intent[i].AimY = polled.AimDirectionY;
            intent[i].AimZ = polled.AimDirectionZ;
            intent[i].Sequence++;
            _ = transform; // 位置对采集无影响；保留 Span 是为了让查询形状覆盖"有位置的实体"
            PolledCount++;
        }
    }
}

/// <summary>空意图源（AI 系统落地前、或某类控制方案暂不支持的占位实现）。</summary>
public sealed class NullIntentSource : IIntentSource
{
    /// <summary>全局唯一实例（无状态）。</summary>
    public static readonly NullIntentSource Instance = new NullIntentSource();

    /// <inheritdoc />
    public InputIntent Poll(int actorId) => default;
}

/// <summary>把扁平掩码翻译成"本帧请求的槽位"的纯函数（技能系统与测试共用，避免两处规则漂移）。</summary>
public static class IntentRules
{
    /// <summary>
    /// 按"边沿优先、槽位升序"挑出本帧要施放的槽位。
    /// <para>
    /// 为什么一次只放一个：同一帧同时按下冲刺与射击时，两个都放会让位移与弹道互相影响（不可复现）；
    /// 优先级由槽位号表达（<see cref="SkillSlot.Dash"/>=1 先于 <see cref="SkillSlot.PrimaryAttack"/>=2），
    /// 其余按键在下一帧继续生效（输入是"状态"不是"事件"）。
    /// </para>
    /// </summary>
    public static SkillSlot NextRequestedSlot(uint pressedMask, uint heldMask)
    {
        for (int slot = 1; slot < SkillSlotExtensions.TableLength; slot++)
        {
            uint bit = 1u << (slot - 1);
            if ((pressedMask & bit) != 0)
            {
                return (SkillSlot)slot;
            }
        }
        return SkillSlot.None;
    }

    /// <summary>把意图装进组件（供意图源与宿主共用；纯函数，零分配）。</summary>
    public static void Apply(in InputIntent intent, ref ActorIntent target)
    {
        target.PressedMask = intent.PressedMask;
        target.HeldMask = intent.HeldMask;
        target.AimX = intent.AimDirectionX;
        target.AimY = intent.AimDirectionY;
        target.AimZ = intent.AimDirectionZ;
        target.Sequence++;
    }

    /// <summary>意图 → <see cref="SkillContext"/> 的瞄准部分（方向做安全归一化，零长度退化为 +Z）。</summary>
    public static Vector3 AimOrFallback(float x, float y, float z)
    {
        var aim = new Vector3(x, y, z);
        float lengthSquared = aim.LengthSquared();
        if (lengthSquared <= float.Epsilon)
        {
            return new Vector3(0f, 0f, 1f);
        }
        return aim / MathF.Sqrt(lengthSquared);
    }
}
