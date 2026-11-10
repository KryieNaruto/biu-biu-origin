using System;

namespace Game.Core.Camera;

/// <summary>
/// 相机手感数学：<b>零引擎依赖</b>的纯函数集合。
/// <para>
/// <b>为什么单独成文件、且禁止引用引擎命名空间</b>：见 <c>ECS.md</c> §10「分层守卫」——
/// <c>scripts/core/**</c> 必须能在<u>没有引擎的进程</u>里被编译与断言，否则「逻辑与渲染分离」当场破功。
/// 本类因此只用 <see cref="float"/> 与元组，不引用 <c>Vector3</c> / <c>Mathf</c> / <c>Node</c> 中的任何一个。
/// </para>
/// <para>
/// <b>为什么必须抽出来</b>：「帧率无关性」「无过冲」「死区连续」这些东西，
/// 只有在<u>纯步进函数</u>上逐帧推算轨迹才能被断言。挂一个相机节点跑 <c>_Process</c>
/// 是测不出 60fps 与 144fps 的轨迹差异的——帧调度本身就被引擎接管了。
/// 因此：<b>手感数学全在这里（纯、可测）</b>，宿主类只做「持有状态 + 写节点」。
/// </para>
/// </summary>
public static class CameraFeel
{
    // =========================================================================
    // 1. 临界阻尼弹簧（单轴）
    // =========================================================================

    /// <summary>
    /// 临界阻尼弹簧的<b>解析单步</b>（单轴）。
    /// <para>
    /// <b>系统</b>：<c>x'' = ω²(target - x) - 2ωx'</c>，<c>ω = 2 / smoothTime</c>（阻尼比 ζ = 1）。
    /// </para>
    /// <para>
    /// <b>推导</b>（用偏离量 <c>u = x - target</c>，避免误差量 <c>e = target - x</c> 的符号反复出错）：
    /// <c>u'' + 2ωu' + ω²u = 0</c>，初值 <c>u(0) = x - target</c>、<c>u'(0) = v</c>。
    /// ζ=1 时通解 <c>u(t) = (A + Bt)·exp(-ωt)</c>，其中
    /// <c>A = u(0)</c>、<c>B = u'(0) + ω·A</c>。于是
    /// <c>x(t) = target + (A + Bt)·r</c>、<c>v(t) = (B - ωA - ωB·t)·r</c>，<c>r = exp(-ωt)</c>。
    /// </para>
    /// <para>
    /// <b>为什么用解析解而不是显式欧拉</b>：<c>x += v·dt; v += a·dt</c> 在 <c>ω·dt &gt; 2</c> 时增益大于 1，
    /// 会指数发散（相机飞出去）。解析解对任意 dt <u>无条件稳定</u>——切窗口回来引擎给出 0.5s 的巨大 dt 也不炸。
    /// </para>
    /// <para>
    /// <b>为什么速度取自解析式而不是 <c>(x_next - x) / dt</c></b>：后者看似更"自洽"，实测会在起步段
    /// 交替输出 <c>161, 0, 150, 0, ...</c>——因为它把"位置被阻尼缩回的那部分"也当成了速度，
    /// 再喂回去就变成正反馈，最终发散。速度必须与位置来自<u>同一个解析解</u>。
    /// </para>
    /// <para>
    /// <b>手感三段</b>（从静止追一个静止目标，smoothTime = T，初距 e₀）：
    /// <b>① 起步</b>（0 ~ 0.3T）：速度从 0 起步、加速度近似恒定，速度近乎线性上升，
    /// 位置只挪了一点 —— 这点"犹豫"正是滑动感的来源，指数收敛没有它；
    /// <b>② 中段</b>（≈ 0.5T）：速度到峰值 <c>≈ 0.736·e₀/T</c>，位置已走完一半，画面明显"冲起来了"；
    /// <b>③ 到达</b>（1T ~ 2.5T）：速度按 <c>(1+ωt)exp(-ωt)</c> 衰减，位置渐近贴合，<u>绝不过冲</u>
    /// （ζ=1 的定义性质，实测 maxOver = 0）。
    /// </para>
    /// </summary>
    /// <param name="current">当前位置。</param>
    /// <param name="target">目标位置。</param>
    /// <param name="velocity">当前速度（单位/秒）。</param>
    /// <param name="smoothTime">平滑时间（秒，&gt; 0）。小于等于 0 表示硬锁。</param>
    /// <param name="deltaSeconds">本步时长（秒）。小于等于 0 表示不推进。</param>
    /// <returns>推进后的 (值, 速度)。</returns>
    public static (float value, float velocity) SmoothDampStep(
        float current, float target, float velocity, float smoothTime, float deltaSeconds)
    {
        // 硬锁：smoothTime 无效 = 策划要「完全跟随、不滑」。速度必须同时清零，
        // 否则下次切回弹簧时会把残留速度当成本步速度，凭空冲一下。
        if (!(smoothTime > 0f) || float.IsNaN(smoothTime))
        {
            return (target, 0f);
        }
        if (!(deltaSeconds > 0f) || float.IsNaN(deltaSeconds))
        {
            return (current, velocity);
        }

        float omega = OmegaCoefficient / smoothTime;
        float a = current - target;            // u(0)
        float b = velocity + omega * a;        // 由 u'(0) = velocity 推出
        float decay = MathF.Exp(-omega * deltaSeconds);

        float next = target + (a + b * deltaSeconds) * decay;
        float nextVelocity = (b - omega * a - omega * b * deltaSeconds) * decay;
        return (next, nextVelocity);
    }

    // =========================================================================
    // 2. 临界阻尼弹簧（XZ 平面）
    // =========================================================================

    /// <summary>
    /// <see cref="SmoothDampStep"/> 的 XZ 平面版：两个轴<u>各自独立</u>跑一个单轴弹簧。
    /// <para>
    /// <b>为什么两轴独立</b>：正交俯视相机的 X/Z 与世界轴对齐，玩家沿斜线移动时两轴分量互不影响；
    /// 各跑一轴 = 结果与「先走 X 再走 Z」无关（路径无关），且与单轴版共用同一份数学，行为可被同一组断言证明。
    /// </para>
    /// <para>
    /// 刻意<b>不提供 <c>Vector3</c> 重载</b>——那会把引擎类型拖进本文件，当场破坏「零引擎依赖」这条硬约束。
    /// </para>
    /// </summary>
    /// <param name="x">当前 X。</param>
    /// <param name="z">当前 Z。</param>
    /// <param name="targetX">目标 X。</param>
    /// <param name="targetZ">目标 Z。</param>
    /// <param name="vx">X 速度。</param>
    /// <param name="vz">Z 速度。</param>
    /// <param name="smoothTime">平滑时间（秒，&gt; 0）。小于等于 0 表示硬锁。</param>
    /// <param name="deltaSeconds">本步时长（秒）。</param>
    /// <returns>推进后的 (x, z, vx, vz)。</returns>
    public static (float x, float z, float vx, float vz) SmoothDampStepXZ(
        float x, float z, float targetX, float targetZ, float vx, float vz,
        float smoothTime, float deltaSeconds)
    {
        (float nx, float nvx) = SmoothDampStep(x, targetX, vx, smoothTime, deltaSeconds);
        (float nz, float nvz) = SmoothDampStep(z, targetZ, vz, smoothTime, deltaSeconds);
        return (nx, nz, nvx, nvz);
    }

    // =========================================================================
    // 3. 瞄准偏移的未平滑目标值（纯函数，无状态）
    // =========================================================================

    /// <summary>
    /// 计算「瞄准偏移」的<b>未平滑目标值</b>（水平面内，米）。纯函数：同样的输入永远同样的输出。
    /// <para>
    /// <b>职责切分</b>：本函数只回答「最终该偏移到哪」；<u>平滑</u>由调用方用
    /// <see cref="SmoothDampStep"/> 做，<u>「瞄准为零时保持上一帧」</u>这种状态机也归调用方。
    /// 这样「偏移方向对不对」「死区是否连续」与「平滑得好不好」可以分别断言。
    /// </para>
    /// <para>
    /// <b>死区</b>：<paramref name="aimX"/>/<paramref name="aimZ"/> 是瞄准方向在屏幕平面上的
    /// 两个分量（满偏时模长为 1）。模长小于 <paramref name="deadZoneRatio"/> 时返回零偏移；
    /// 超过后把 <c>[deadZone, 1]</c> 线性重映射到 <c>[0, 1]</c> 再乘最大位移。
    /// 因此死区边界上偏移量<b>连续</b>（两侧都是 0），不会出现「准星在边界抖 → 相机一下一下跳」。
    /// </para>
    /// </summary>
    /// <param name="aimX">瞄准方向的第 1 个屏幕平面分量（0 表示无方向）。</param>
    /// <param name="aimZ">瞄准方向的第 2 个屏幕平面分量（0 表示无方向）。</param>
    /// <param name="orthoHeight">正交可见高度（米）——偏移按它成比例，换分辨率不改变手感比例。</param>
    /// <param name="ratio">偏移比例（&lt;= 0 表示不偏移）。</param>
    /// <param name="maxMeters">偏移封顶（米）；&lt;= 0 表示不封顶。</param>
    /// <param name="deadZoneRatio">死区（0..1，相对满偏比例；0 = 无死区）。</param>
    /// <returns>未平滑的 (offsetX, offsetZ)。</returns>
    public static (float offsetX, float offsetZ) LookAheadOffset(
        float aimX, float aimZ, float orthoHeight, float ratio, float maxMeters, float deadZoneRatio)
    {
        if (!(ratio > 0f) || !(orthoHeight > 0f) || float.IsNaN(aimX) || float.IsNaN(aimZ))
        {
            return (0f, 0f);
        }

        float len = MathF.Sqrt(aimX * aimX + aimZ * aimZ);
        if (!(len > MinDirectionLength))
        {
            return (0f, 0f);   // 零向量：没有方向就没有偏移（调用方负责「保持上一帧」）
        }

        // 归一化后再算比例 —— 调用方传进来的不一定是单位向量，不能假设。
        float nx = aimX / len;
        float nz = aimZ / len;

        // 相对满偏的比例（0 = 中心，1 = 满偏）。
        float magnitude = orthoHeight * ratio;
        float fullness;
        if (deadZoneRatio > 0f)
        {
            float dz = deadZoneRatio > MaxRatio ? MaxRatio : deadZoneRatio;
            if (!(len > dz))
            {
                return (0f, 0f);
            }
            float span = 1f - dz;
            fullness = span > 0f ? (len - dz) / span : 1f;
        }
        else
        {
            fullness = len > 1f ? 1f : len;
        }

        magnitude *= fullness;
        if (maxMeters > 0f && magnitude > maxMeters)
        {
            magnitude = maxMeters;
        }

        return (nx * magnitude, nz * magnitude);
    }

    // =========================================================================
    // 4. 参数换算
    // =========================================================================

    /// <summary>
    /// 「每秒速率」→「弹簧平滑时间」的换算：<c>smoothTime = 1 / ratePerSecond</c>。
    /// <para>
    /// <b>为什么需要换算</b>：数据层（Excel 镜头包）用「每秒收敛速率」这个策划友好的单位，
    /// 表现层用「平滑时间（秒）」这个物理单位。二者是倒数关系，在<u>边界上换算一次</u>，
    /// 两边各自保持自己最自然的语义（规则7：换镜头包只改数据，不改这里）。
    /// </para>
    /// </summary>
    /// <param name="ratePerSecond">每秒速率；小于等于 0 / NaN / 无穷时返回 <paramref name="fallbackSmoothTime"/>。</param>
    /// <param name="fallbackSmoothTime">速率无效时的保底平滑时间（秒）。</param>
    /// <returns>平滑时间（秒）。</returns>
    public static float SmoothTimeFromRate(float ratePerSecond, float fallbackSmoothTime)
    {
        if (!(ratePerSecond > 0f) || float.IsInfinity(ratePerSecond) || float.IsNaN(ratePerSecond))
        {
            return fallbackSmoothTime;
        }
        return 1f / ratePerSecond;
    }

    // =========================================================================
    // 5. 一阶指数收敛（保留：准星跟随 / HUD 数值滚动仍在用）
    // =========================================================================

    /// <summary>
    /// 指数收敛系数：<c>1 - exp(-rate * dt)</c>，夹到 [0,1]。rate 或 dt 非法时返回 1（硬锁）。
    /// <para>
    /// 相机跟随已改用临界阻尼弹簧，但<u>准星跟随</u>、<u>HUD 数值滚动</u>这类「无惯性、贴上去就行」
    /// 的场景仍应使用一阶平滑——二阶弹簧会给它们带来本不需要的过冲/延迟。
    /// </para>
    /// </summary>
    /// <param name="deltaSeconds">本帧时长。</param>
    /// <param name="rate">每秒收敛速率。</param>
    /// <returns>本帧应向目标推进的比例（0..1）。</returns>
    public static float Convergence(float deltaSeconds, float rate)
    {
        if (!(rate > 0f) || !(deltaSeconds > 0f) || float.IsNaN(rate) || float.IsNaN(deltaSeconds))
        {
            return 1f;
        }
        float t = 1f - MathF.Exp(-rate * deltaSeconds);
        return t < 0f ? 0f : (t > 1f ? 1f : t);
    }

    // =========================================================================
    // 6. 帧时长安全夹取与子步切分
    // =========================================================================

    /// <summary>单帧 dt 上限（秒）：切窗口 / 断点后引擎会给巨大 dt，不夹会让弹簧瞬移出地图。</summary>
    public const float MaxFrameSeconds = 0.1f;

    /// <summary>单子步 dt 上限（秒）：1/240s，保证任意帧率下子步都足够细。</summary>
    public const float MaxSubStepSeconds = 1f / 240f;

    /// <summary>把整帧 dt 夹进 [0, <see cref="MaxFrameSeconds"/>]；NaN / 负数返回 0。</summary>
    /// <param name="deltaSeconds">引擎给出的帧时长。</param>
    /// <returns>安全的本帧时长。</returns>
    public static float ClampFrameDelta(float deltaSeconds)
    {
        if (float.IsNaN(deltaSeconds) || deltaSeconds <= 0f)
        {
            return 0f;
        }
        return deltaSeconds > MaxFrameSeconds ? MaxFrameSeconds : deltaSeconds;
    }

    /// <summary>把一整帧 dt 拆成的子步数，保证每个子步不超过 <see cref="MaxSubStepSeconds"/>。</summary>
    /// <param name="deltaSeconds">整帧时长（秒）。</param>
    /// <returns>子步数（至少 1）。</returns>
    public static int SubStepCount(float deltaSeconds)
    {
        if (!(deltaSeconds > 0f) || float.IsNaN(deltaSeconds))
        {
            return 1;
        }
        int steps = (int)MathF.Ceiling(deltaSeconds / MaxSubStepSeconds);
        return steps < 1 ? 1 : steps;
    }

    // =========================================================================
    // 常量
    // =========================================================================

    /// <summary>临界阻尼系统的 ω 系数：ω = 系数 / smoothTime，ζ = 1 时取 2。</summary>
    private const float OmegaCoefficient = 2f; // arch-ok: 临界阻尼数学定义（ζ=1 时 ω=2/T），不是玩法数值

    /// <summary>判定「方向向量非零」的长度下限（纯浮点安全阈值）。</summary>
    private const float MinDirectionLength = 1e-6f; // arch-ok: 浮点安全阈值（归一化除零保护）

    /// <summary>比例上限（死区夹取用）。</summary>
    private const float MaxRatio = 1f; // arch-ok: 比例定义域上限
}
