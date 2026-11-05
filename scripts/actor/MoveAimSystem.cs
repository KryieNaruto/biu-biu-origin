using System;
using System.Numerics;
using Game.Core.Ecs;

namespace Game.Actor;

/// <summary>
/// 标准移动包：把「意图」解析成「物理速度 + 朝向」（契约 §0 #3/#5：<c>MoveAimSystem</c>）。
/// <para>
/// <b>为什么它是一个系统，而不是在输入回调里直接改位置</b>
/// </para>
/// <list type="bullet">
/// <item>输入回调（宿主）只写<u>意图</u>，系统在一帧的固定位置读取意图 → 写速度。于是玩家、AI、回放
///       驱动同一只怪时走的是同一段代码（怪物与角色同源）；</item>
/// <item>速度与朝向必须和数值包（移速 / 加速响应 / 转向响应）一起算，而这套算法要能在没有引擎的测试里跑
///       （<c>tests/</c> 直接编译本文件），所以它不能读 <c>Input</c>、不能碰任何 Godot 类型；</item>
/// <item>把它做成"模块"（<see cref="ModuleAttribute"/> <c>move_standard</c>）后，一个"不会动的炮塔"
///       变体只要在装配清单里不勾这一项，就<u>不注册</u>本系统 —— 不需要在系统里写 <c>if (kind == Turret)</c>（J3）。</item>
/// </list>
/// <para>
/// <b>为什么"意图 → 速度"要经过一个响应时间</b>：键盘只有 0/1，直接赋值会让角色像刚体一样瞬移起步；
/// 手感需要"起步有惯性、停步有滑行"，而这两者都必须是数值（重甲慢、敏捷快）。用"到达目标速度所需时间"
/// 表达（<see cref="IMoveStatReader.AccelerationSeconds"/>）而不是写死一个 lerp 系数，就是为了让它能进 Excel。
/// </para>
/// <para>
/// <b>为什么不在这里写 <see cref="Velocity"/>.Angular</b>：朝向只影响表现与弹道起点，不影响位移；
/// 角速度留给"旋转着的炮台"这类实体自己维护。本系统只写 <see cref="Transform3"/>.Rotation（世界朝向上的偏航）。
/// </para>
/// <para>
/// <b>执行阶段</b>：<see cref="UpdateGroup.FixedUpdate"/>，且排在 <see cref="MovementSystem"/> 之前 ——
/// 本系统产出的速度必须在本固定步内立刻被积分，否则同 seed 两次运行的位移会差一个步长。
/// </para>
/// <para>
/// <b>硬契约：没有 <see cref="AimTarget"/> 的实体，本系统<b>连速度都不写</b></b>（不只是不转向）。
/// 这条门槛是刻意"整只跳过"的：本系统的输出是"朝向 + 速度"这一对，一旦允许只写一半，就会出现
/// "有速度、但没有可解释的朝向来源"的实体 —— 表现层拿不到该朝哪，调试时也说不清是谁推的。
/// 于是一个"纯移动、不瞄准"的 NPC 应该在装配清单里换成只含移动的模块，而<u>不是</u>靠本系统里的
/// 条件分支绕过去（J3：逻辑差异用装配表达，不用 if）。
/// 已由 <c>MoveAim_SkipsEntitiesWithoutAimTarget</c> 钉住：无瞄准组件时速度保持原值、<see cref="MovedCount"/> 不增。
/// </para>
/// </summary>
[UpdateInGroup(UpdateGroup.FixedUpdate)]
[UpdateBefore(typeof(MovementSystem))]
[Module("move_standard")]
public sealed class MoveAimSystem : SystemBase, ISpanAction<MoveIntent, Velocity, Transform3>
{
    private readonly IMoveStatReader _moveStats;
    private readonly IAimStatReader _aimStats;
    private readonly MoveAimTuning _tuning;
    private Query _query = null!;
    private float _deltaTime;

    /// <summary>接口视图（自身）：避免 <c>in this</c> 把 class 装箱成只读副本。</summary>
    private readonly ISpanAction<MoveIntent, Velocity, Transform3> _action;

    /// <summary>本帧实际推动过的实体数（调试/性能面板用）。</summary>
    public int MovedCount { get; private set; }

    /// <summary>本帧因为读不到移速而按兜底值处理的实体数（&gt; 0 表示数据不齐，宿主应打日志）。</summary>
    public int MissingSpeedCount { get; private set; }

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="moveStats">移速 / 加速响应读取器（由宿主或数据层注入；测试给一个纯内存实现）。</param>
    /// <param name="aimStats">转向响应读取器（与 <see cref="AimAtCursorSystem"/> 共用同一个实例）。</param>
    /// <param name="tuning">读不到数值时的兜底值（默认 0 = 不动，宁可见地不动也不要静默乱动）。</param>
    public MoveAimSystem(IMoveStatReader moveStats, IAimStatReader aimStats, MoveAimTuning? tuning = null)
    {
        _moveStats = moveStats ?? throw new ArgumentNullException(nameof(moveStats));
        _aimStats = aimStats ?? throw new ArgumentNullException(nameof(aimStats));
        _tuning = tuning ?? MoveAimTuning.None;
        _action = this;
    }

    /// <inheritdoc />
    public override string Name => "MoveAim";

    /// <inheritdoc />
    protected override void OnInitialize()
    {
        // 查询只取三个组件：内核的 Query 泛型最多 3 个（J3 的编译期约束），
        // 第 4 个（AimTarget）在循环里用 TryGet 读 —— 命中率极高（几乎每个角色都有瞄准组件），
        // 但语义上它是"可选数据"，可选数据放 TryGet 比塞进查询更能表达意图。
        _query = World.Query<MoveIntent, Velocity, Transform3>();
    }

    /// <inheritdoc />
    protected override void OnExecute(float deltaTime)
    {
        _deltaTime = deltaTime;
        MovedCount = 0;
        MissingSpeedCount = 0;
        _query.ForEach<MoveIntent, Velocity, Transform3>(_action);
    }

    /// <inheritdoc />
    void ISpanAction<MoveIntent, Velocity, Transform3>.Execute(
        Span<MoveIntent> intents,
        Span<Velocity> velocities,
        Span<Transform3> transforms,
        int count)
    {
        float dt = _deltaTime;
        if (dt <= 0f)
        {
            return;
        }

        // 平滑使用的插值系数：alpha = 1 - exp(-dt / response)。
        // 为什么用指数而不是 alpha = dt / response：后者在 dt > response 时会超过 1（速度过冲），
        // 而且帧率变化时"视觉上的响应快慢"会变（手感不稳）。指数形式与帧率无关。
        for (int i = 0; i < count; i++)
        {
            Entity owner = intents[i].Owner;

            // ── 0) 门槛：没有 AimTarget 的实体（纯移动的 NPC）不归本系统管 ──
            // 契约钉住了这条（MoveAim_SkipsEntitiesWithoutAimTarget）：本系统产出的<u>朝向</u>必须总能被
            // 下游解释为"朝瞄准点看"，所以没有瞄准的实体连速度都不该由本系统写 —— 它的移动走它自己的包。
            // 代价是"移动"与"瞄准"被绑成一个包（move_standard）；这正是模块化的意义：
            // 想要"只移动不瞄准"的 NPC，就在装配清单里换一个只有移动的系统，而不是在本系统里加分支（J3）。
            if (!World.TryGet<AimTarget>(owner, out AimTarget aim))
            {
                continue;
            }

            // ── 1) 意图 → 目标速度（数值来自注入的读取器，不在代码里写移速） ──
            float speed = _moveStats.MoveSpeed(owner, _tuning.FallbackMoveSpeed);
            if (speed <= 0f && _tuning.FallbackMoveSpeed <= 0f)
            {
                MissingSpeedCount++;
            }

            Vector3 desired = intents[i].Velocity;
            float magnitude = intents[i].Magnitude;
            float intentLengthSquared = desired.LengthSquared();
            if (intentLengthSquared > 0f)
            {
                // 归一化后用 Magnitude 长度：让"摇杆半推"与"方向输入"彻底解耦
                // （只靠向量长度的话，键鼠（长度恒为 1）与手柄（长度 0..1）会走两套语义）。
                float intentLength = MathF.Sqrt(intentLengthSquared);
                float scale = speed * magnitude / intentLength;
                desired = new Vector3(desired.X * scale, desired.Y * scale, desired.Z * scale);
            }
            else
            {
                desired = Vector3.Zero;
            }

            // ── 2) 逼近：response <= 0 视为"瞬间到位"（街机手感；重甲型用数据把它调慢） ──
            // 语义：response = "从静止走完一整段距离所需的时间"，于是<u>每帧可以走</u>
            // remaining * (dt / response) —— 这是"匀速走完"的限速，而不是指数逼近。
            // 为什么不用纯指数 1-exp(-dt/τ)：指数<u>永远到不了</u>目标。表现在手感上就是
            // "按住 W 永远差 10% 速度"，在测试上就是"稳态速度永远不等于 MoveSpeed"（只能写宽容差）。
            // 限速形式在 dt >= response 时一帧走完，剩余量低于容差时直接贴上，于是"到位"是一个
            // 可断言的事实而不是渐近极限。同时它仍然每帧限速 → 起步/停步依旧有惯性（手感保留）。
            float response = _moveStats.AccelerationSeconds(owner, _tuning.FallbackAccelerationSeconds);
            Vector3 current = velocities[i].Linear;
            Vector3 next;
            if (response <= 0f)
            {
                next = desired;
            }
            else
            {
                Vector3 delta = desired - current;
                float remaining = delta.Length();
                // 指数补间负责"手感"（起步有惯性、帧率无关），额外一步负责"到位"：
                // 本帧应走的距离 = 剩余量 × (1 - exp(-dt/τ))。当这个距离与剩余量相差不到一个
                // 亚像素级别的比例时，说明再走也看不出差别了 —— 直接贴上目标。
                // 这一步<u>不是</u>为了好看：没有它，所谓"稳态速度"永远是一个逼近极限，
                // 玩家看到的是"按住 W 比标称移速慢一点"，测试则永远无法断言 == MoveSpeed。
                float alpha = OneMinusExp(-dt / response);
                float step = remaining * alpha;
                // settleEpsilon：剩余量小到"这一帧之后剩余部分已不可感知"时就落地。
                const float settleEpsilon = 1e-4f; // arch-ok: 浮点收敛阈值，不是玩法数值
                next = step >= remaining - settleEpsilon ? desired : current + delta * alpha;
            }
            velocities[i].Linear = new Vector3(next.X, 0f, next.Z);

            // 写速度与 MovedCount 都在"有瞄准目标"之后：本系统只推动"既有意图又有瞄准"的实体。
            MovedCount++;

            // ── 3) 朝向：只朝"有瞄准目标"的那一帧转，避免 AI 没给瞄准时模型乱转 ──
            //     朝向写在 Transform3 的偏航上（俯视角：绕世界 Y 轴）。用不含 y 的水平分量，
            //     因为角色模型不应该因为"鼠标在上方"而前倾（那是相机的活）。
            //     方向为零 = 本帧不转向（速度照写），这是刻意的：刚生成的角色还没瞄准过，不该乱转。
            Vector3 aimDirection = aim.Direction;
            float aimLengthSquared = aimDirection.X * aimDirection.X + aimDirection.Z * aimDirection.Z;
            if (aimLengthSquared <= float.Epsilon)
            {
                continue;
            }

            float turnResponse = _aimStats.TurnResponseSeconds(owner, _tuning.FallbackTurnResponseSeconds);
            Quaternion targetRotation = LookTowards(aimDirection);
            // 转向与速度用同一套语义（见上面 ② 的说明）：一个响应周期转完剩余角度，剩余角度小到
            // 本帧能转完时直接贴上目标。用角度而不是四元数插值系数，是因为"还剩多少度"才有物理含义
            // （也才能说清楚"转过 5° 和转过 5° 但方向相反"不是一回事）。
            Quaternion rotation;
            if (turnResponse <= 0f)
            {
                rotation = targetRotation;
            }
            else
            {
                Quaternion currentRotation = transforms[i].Rotation;
                float dot = MathF.Abs(Quaternion.Dot(currentRotation, targetRotation));
                float remainingRadians = 2f * MathF.Acos(MathF.Min(dot, 1f)); // arch-ok: 四元数→夹角的数学常量，非玩法数值
                float alpha = OneMinusExp(-dt / turnResponse);
                float stepRadians = remainingRadians * alpha;
                // 剩余角度小到"这一帧之后剩下的已经看不出来"时直接贴上（与速度同一判据）。
                const float settleRadians = 1e-4f; // arch-ok: 浮点收敛阈值（≈0.006°），不是玩法数值
                rotation = stepRadians >= remainingRadians - settleRadians
                    ? targetRotation
                    : Quaternion.Slerp(currentRotation, targetRotation, alpha);
            }
            transforms[i].Rotation = Quaternion.Normalize(rotation);
        }
    }

    /// <summary>
    /// 「朝某方向看」的四元数（<b>契约里只列了三个系统，这个纯函数是为满足"模型朝向"而必须有的最小工具</b>）。
    /// <para>
    /// 为什么不放进组件或另一个系统：它没有状态、没有数值、不依赖世界，是纯数学。
    /// 放在这里而不是 <c>MovementSystem</c>：MovementSystem 只做积分，不该知道"模型前方是哪条轴"；
    /// 而"哪个方向算前方"属于表现约定，本系统正是唯一写旋转的地方，写在一处就不会有两个真相。
    /// </para>
    /// <para>约定：模型前方 = +Z（与 Godot 里 <c>-Z</c> 为前的默认不同，因此宿主层的视图桥负责差一个 180°；
    /// 这条约定必须写在唯一写旋转的地方，见 <c>Game.Core.Ecs.Transform3</c> 的存在理由）。</para>
    /// </summary>
    public static Quaternion LookTowards(in Vector3 direction)
    {
        float yaw = MathF.Atan2(direction.X, direction.Z);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    /// <summary><c>1 - exp(-x)</c>：帧率无关的插值系数（x 为"已经过了多少个响应时间"）。</summary>
    private static float OneMinusExp(float x)
    {
        // 极端情况下 exp 溢出成 0：夹到 [0,1]，保证"最多一步到位、绝不反向"。
        float e = MathF.Exp(x);
        float value = 1f - e;
        if (value < 0f)
        {
            return 0f;
        }
        return value > 1f ? 1f : value;
    }
}
