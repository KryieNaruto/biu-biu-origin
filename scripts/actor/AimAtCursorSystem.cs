using System;
using System.Numerics;
using Game.Core.Ecs;

namespace Game.Actor;

/// <summary>
/// 瞄准包（<c>aim_cursor</c>）：把「宿主写进来的光标世界点」校验/夹取/平滑成「可用的瞄准方向」（契约 §0 #4）。
/// <para>
/// <b>为什么"点世界点"这一步不在本系统里做</b>：从鼠标屏幕坐标到世界点需要相机射线求交，
/// 那是引擎能力（J4：只有宿主层能 <c>using Godot</c>）。所以分工是：
/// 宿主把光标点写进 <see cref="AimTarget"/>（{ Point, HasTarget = true }）→ 本系统把它变成
/// <see cref="AimTarget.Direction"/>（单位向量 + 距离夹取）。
/// 组件是两条边之间的接缝：宿主与系统只交换值，不交换控制流，因此"换一个手柄包/准星方案"= 换宿主的一个输入包。
/// </para>
/// <para>
/// <b>为什么需要"夹取距离"</b>：俯视角下光标点可能落在几十米外，直接按这个点算方向会让弹道在近处
/// 完全偏离准星（子弹从角色眼睛出发飞向远处一个点，近处目标就打不中）。把瞄准点夹到
/// <see cref="IAimStatReader.AimRangeMeters"/> 之后，方向变成"朝准星方向、但落点在射程内"，
/// 手感上就是"瞄准线"的直觉。
/// </para>
/// <para>
/// <b>为什么平滑在"方向"上而不是在"点"上</b>：平滑点会让准星在角色身边绕大圈（点越远、视觉角速度越小），
/// 平滑方向则是"转身有重量"的直觉手感，且与距离无关。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.FixedUpdate)]
[UpdateBefore(typeof(MovementSystem))]
[Module("aim_cursor")]
public sealed class AimAtCursorSystem : SystemBase, ISpanAction<AimTarget, Transform3>
{
    private readonly IAimStatReader _aimStats;
    private readonly MoveAimTuning _tuning;
    private Query _query = null!;

    private readonly ISpanAction<AimTarget, Transform3> _action;

    /// <summary>本帧真正更新了瞄准方向的实体数（调试用）。</summary>
    public int AimedCount { get; private set; }

    /// <summary>本帧没有新鲜瞄准（宿主没给）而沿用上一帧方向的实体数（调试用）。</summary>
    public int StaleCount { get; private set; }

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="aimStats">瞄准距离 / 转向响应读取器。</param>
    /// <param name="tuning">读不到数值时的兜底值（瞄准距离为 0 时=不夹取，这是安全的空缺行为）。</param>
    public AimAtCursorSystem(IAimStatReader aimStats, MoveAimTuning? tuning = null)
    {
        _aimStats = aimStats ?? throw new ArgumentNullException(nameof(aimStats));
        _tuning = tuning ?? MoveAimTuning.None;
        _action = this;
    }

    /// <inheritdoc />
    public override string Name => "AimAtCursor";

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        // 只需要瞄准组件本身：位置由 AimTarget 的方向表达（射击用方向，不用位置）。
        // 为什么把 Transform3 也查上：射程夹取需要知道"角色在哪"，否则只能对点做绝对坐标夹取。
        _query = World.Query<AimTarget, Transform3>();
    }

    /// <summary>
    /// 每帧执行。
    /// <para>本系统不消费 <paramref name="deltaTime"/>：方向由「光标点 − 角色位置」唯一确定，
    /// 平滑发生在写旋转的 <see cref="MoveAimSystem"/> 里（那里才有"每帧步进"的语义）。
    /// 两个系统都做平滑会让"转向响应时间"被平方，手感参数就说不清是谁在起作用。</para>
    /// </summary>
    protected override void OnExecute(float deltaTime)
    {
        _ = deltaTime;
        AimedCount = 0;
        StaleCount = 0;
        _query.ForEach<AimTarget, Transform3>(_action);
    }

    /// <inheritdoc />
    void ISpanAction<AimTarget, Transform3>.Execute(Span<AimTarget> aims, Span<Transform3> transforms, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!aims[i].HasTarget)
            {
                // 宿主这一帧没给瞄准（鼠标离开窗口 / 手柄回中 / AI 无目标）：
                // 保留上一帧的方向（不要清零），因为"清零 = 角色突然面朝 +Z"，比保持更怪。
                StaleCount++;
                continue;
            }

            Entity owner = aims[i].Owner;
            Vector3 origin = transforms[i].Position;
            Vector3 offset = aims[i].Point - origin;
            offset.Y = 0f; // 俯视角：瞄准只在地面平面上，竖直分量交给"射线命中点"本身决定不了

            float range = _aimStats.AimRangeMeters(owner, _tuning.FallbackAimRangeMeters);
            float lengthSquared = offset.LengthSquared();
            if (lengthSquared <= float.Epsilon)
            {
                // 光标正好在角色身上：方向无定义。这不是错误（鼠标扫过自己），沿用上一帧。
                StaleCount++;
                continue;
            }

            if (range > 0f)
            {
                float length = MathF.Sqrt(lengthSquared);
                if (length > range)
                {
                    offset *= range / length;
                    aims[i].Point = origin + offset;
                }
            }

            aims[i].Direction = Vector3.Normalize(offset);
            // 消费掉"新鲜"标记：下一帧宿主必须重新写入，否则视为没有瞄准。
            // 这样"宿主停止写"与"宿主写了同一个点"在数据上是可区分的（前者保持、后者更新）。
            aims[i].HasTarget = false;
            AimedCount++;
        }
    }
}
