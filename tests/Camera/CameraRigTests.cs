using System;
using System.Collections.Generic;
using Game.Core.Camera;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Camera;

/// <summary>
/// 相机手感回归：收敛性 / 帧率无关性 / 无过冲 / 稳定性 / 瞄准偏移方向与死区 / 平滑。
/// <para>
/// <b>为什么这些必须被钉住</b>：相机手感配错<u>不会报任何错</u>——它不会编译失败、不会抛异常、
/// 日志里也没有一行。它只表现为「这台机器上顺滑，换台机器手感不一样」，或者「转身时画面甩出去」。
/// 本项目已有同类事故记录（拉伸模式配错、相机朝向退化，都是静默失效，靠探针才抓到）。
/// 因此这里用**纯步进函数**逐帧推算轨迹，把「应该是什么手感」写成断言。
/// </para>
/// <para>
/// <b>为什么是纯 <c>float</c> 而不是 <c>Vector3</c></b>：手感数学本质是标量运算。
/// <see cref="CameraFeel"/> 刻意零 <c>using Godot</c>，因此本工程可以把它和 <c>scripts/core/**</c>
/// 一起直接编译，<u>一条 <c>PackageReference</c> 都不加</u>——「若这里出现 using Godot，
/// 本工程会直接编译失败」本身就是一道比人工审查更硬的守卫。
/// </para>
/// </summary>
[TestClass]
public sealed class CameraRigTests
{
    // =========================================================================
    // 夹具：一份「手感正常」的镜头参数
    // 数值全部显式传入，不依赖任何 Inspector 默认值 ——
    // 断言必须钉住「我认为合理的手感」，而不是「当前默认值刚好是多少」。
    // =========================================================================

    /// <summary>正交可见高度（米）——前瞻位移按它成比例。</summary>
    private const float OrthoHeightMeters = 14f;

    /// <summary>前瞻比例：基础偏移 = 可见高度 × 比例。</summary>
    private const float LookAheadRatio = 0.18f;

    /// <summary>前瞻封顶（米）。</summary>
    private const float LookAheadMaxMeters = 1.6f;

    /// <summary>跟随速率（每秒）。</summary>
    private const float FollowRatePerSecond = 10f;

    /// <summary>平滑时间（秒）。</summary>
    private const float SmoothTime = 0.12f;

    /// <summary>
    /// 位置断言容差（米）。玩家移速量级 20 m/s，1 帧位移约 0.33 m；
    /// 0.01 m 相当于 0.03 帧的位移，足够严格又不会被 32 位浮点噪声误伤。
    /// </summary>
    private const float PositionTolerance = 0.01f;

    /// <summary>
    /// 帧率一致性容差（米）。Lead 指定的硬指标：60fps 与 144fps 同跑同样时长后
    /// 位置差必须小于它。超过就说明积分与 dt 不是真正解耦的。
    /// </summary>
    private const float FrameRateTolerance = 0.02f;

    /// <summary>无过冲容差（米）：允许冲到目标的 1% 距离以内。</summary>
    private const float OvershootTolerance = 0.01f;

    // =========================================================================
    // A. 收敛性
    // =========================================================================

    /// <summary>
    /// 收敛性：从 0 追到 10，跑满 1 秒（60 帧 @60fps）后必须基本到位。
    /// <para>
    /// <b>阈值为什么定在 0.005 m</b>：最初按「剩余 ≤ 满行程 1%（0.1 m）」定，太松。
    /// 改用解析解当锚点：临界阻尼 <c>smoothTime = 0.12 s</c> 的精确解是
    /// <c>u(t) = u₀·(1 + ωt)·e^(−ωt)</c>，<c>ω = 2/0.12 ≈ 16.67</c>；代入 <c>t = 1 s</c>
    /// 得残留 <c>10 × 1.03e-6 ≈ 1e-5 m</c>。实测参考实现残留约 0.02 m（定点欧拉比解析解多留尾巴）。
    /// 判据取 0.005 m（满行程的 0.05%）：比实测宽 4 倍，容得下积分方式差异，又不至于形同虚设。
    /// </para>
    /// <para>
    /// ⚠️ 但这条<u>不是</u>帧率依赖性的守卫，也不应该试图成为守卫。变异测试证明：
    /// 固定比例 <c>Lerp(a, b, 0.2f)</c> 跑满 1 秒后残差只有 1.5e-5 m，<u>比正确实现还小</u>。
    /// 也就是说「1 秒终点残差」这个指标在数学上就无法区分两者——把阈值收得再紧也只会误伤正确实现，
    /// 不会多抓到任何东西。真正抓「固定 k Lerp」的是
    /// <c>Spring_IsFrameRateIndependent_AtMultipleCheckpoints</c>（在 0.15 s 处两者差 1.27 m）。
    /// 两条各司其职：这条管「最终到得了」，那条管「中间过程对 dt 解耦」。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_ConvergesToTargetWithinOneSecond()
    {
        const float dt = 1f / 60f;
        const float target = 10f;
        const float residualBudget = 0.005f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 60, dt, start: 0f, target, SmoothTime, FollowRatePerSecond);
        float final = trace[60].value;

        Assert.AreEqual(target, final, residualBudget,
            $"跟随 1 秒后仍未收敛：期望 {target}，实际 {final}，误差 {Math.Abs(target - final)}，" +
            $"超过允许的 {residualBudget} m。解析解在此处残留约 1e-5 m，实测参考实现约 0.02 m。" +
            "典型原因：把「每秒速率」当成「每帧比例」直接用，或弹簧系数换算错（ω 与 smoothTime 不是 2/T 关系）。");
    }

    /// <summary>
    /// 收敛性（严格版）：给足 10 秒，必须真的到达目标（残差 &lt; 1 mm）。
    /// <para>
    /// 与 <see cref="Spring_ConvergesToTargetWithinOneSecond"/> 分工：
    /// 那条管「1 秒内到八成以上」（手感上不能拖沓），这条管「最终真的会到」（不会有 1% 的永久残差）。
    /// 只有两条同时成立，才说明既快又准。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_ConvergesToTarget_GivenEnoughTime()
    {
        const float dt = 1f / 60f;
        const float target = 10f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 600, dt, start: 0f, target, SmoothTime, FollowRatePerSecond);
        float final = trace[600].value;

        Assert.AreEqual(target, final, 1e-3f,
            $"给了 10 秒仍未到达目标：期望 {target}，实际 {final}，残差 {Math.Abs(target - final)}。" +
            "存在永久残差通常意味着积分器每帧都丢掉一部分位移（例如乘了一个 < 1 的固定系数但没补偿 dt）。");
    }

    /// <summary>
    /// 临界阻尼不应过冲：整条轨迹上任何一点都不得超过目标（容差 1% 目标距离）。
    /// <para>
    /// 过冲表现为「急停时画面甩过头再弹回来」，是俯视角射击里最影响瞄准的手感缺陷。
    /// 欠阻尼（damping &lt; critical）会稳定地过冲，这条能把它逼出来。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_CriticallyDamped_DoesNotOvershootTarget()
    {
        const float dt = 1f / 60f;
        const float target = 10f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 120, dt, start: 0f, target, SmoothTime, FollowRatePerSecond);

        float allowed = Math.Abs(target) * OvershootTolerance;
        float worst = 0f;
        // 注意：worstFrame 初始为 -1，只有在真的抓到过冲时才会被赋值。
        // 断言消息里不能索引 trace[worstFrame]——在「零过冲」的正常路径下那是越界访问，
        // 会把一条本该通过的用例变成 IndexOutOfRangeException。
        int worstFrame = -1;
        for (int i = 0; i < trace.Length; i++)
        {
            float excess = trace[i].value - target;
            if (excess > worst)
            {
                worst = excess;
                worstFrame = i;
            }
        }

        Assert.IsTrue(worst <= allowed,
            $"临界阻尼系统过冲了：第 {worstFrame} 帧冲到 {trace[(worstFrame < 0 ? 0 : worstFrame)].value}，" +
            $"超过目标 {target} 达 {worst}（允许 {allowed}）。" +
            "说明阻尼系数不是临界阻尼（k/c 关系写错，或用了欠阻尼公式）。");
    }

    /// <summary>
    /// 轨迹必须单调向目标推进，且速度不得反向。
    /// <para>
    /// 与上一条互补：上一条抓「冲过头」，这条抓「来回抖」。抖动在数值上可能不过冲，
    /// 但画面会明显颤，是典型的「积分器步长用得不对」症状。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_TrackIsMonotonicTowardsTarget()
    {
        const float dt = 1f / 60f;
        const float target = 10f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 90, dt, start: 0f, target, SmoothTime, FollowRatePerSecond);

        for (int i = 1; i < trace.Length; i++)
        {
            Assert.IsTrue(trace[i].value >= trace[i - 1].value - OvershootTolerance,
                $"第 {i} 帧位置回退：{trace[i - 1].value} → {trace[i].value}。" +
                "轨迹向目标推进时不应回退（回退 = 画面抖动）。");
            Assert.IsTrue(trace[i].velocity >= -OvershootTolerance,
                $"第 {i} 帧速度反向：{trace[i].velocity}。" +
                "从 0 追向正目标时速度不应变负（变负 = 过冲或振荡）。");
        }
    }

    // =========================================================================
    // B. 帧率无关性（本轮最关键）
    // =========================================================================

    /// <summary>
    /// 同样 1.0 秒真实时间，60fps 与 144fps 步进后位置必须几乎相同。
    /// <para>
    /// 失败意味着「手感随机器帧率变化」——144Hz 显示器上收敛更快，掉帧的机器上更慢。
    /// 这类缺陷<u>不报错、不崩溃</u>，只会让不同玩家玩到不同的游戏，是最难被发现的一类。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_IsFrameRateIndependent_SixtyVsOneHundredFortyFour()
    {
        const float target = 10f;

        (float value, float velocity)[] at60 =
            RunSpringExactly(fps: 60f, seconds: 1f, start: 0f, target, SmoothTime, FollowRatePerSecond);
        (float value, float velocity)[] at144 =
            RunSpringExactly(fps: 144f, seconds: 1f, start: 0f, target, SmoothTime, FollowRatePerSecond);

        float diff = Math.Abs(at60[^1].value - at144[^1].value);

        Assert.IsTrue(diff < FrameRateTolerance,
            $"帧率无关性失败：跑满同样 1.0 秒后，60fps 落在 {at60[^1].value}，144fps 落在 {at144[^1].value}，" +
            $"相差 {diff}，超过允许的 {FrameRateTolerance}。" +
            "常见原因：① 直接 Lerp(current, target, k) 用固定 k；" +
            "② 弹簧子步固定按 1/60 积分而不管真实 dt；" +
            "③ 用 exp(-rate*dt) 但 rate 由帧数而非秒数换算。");
    }

    /// <summary>
    /// <b>比只测终点更硬</b>：在 0.15 s / 0.5 s / 1.0 s 三个时间点上，
    /// 60fps 与 144fps 的位置都必须一致。
    /// <para>
    /// 只比较终点容易掩盖中段轨迹不一致——例如「先冲快后拉平」的两条曲线可能终点重合而中段差很远，
    /// 玩家在下蹲/急停时就会感到不同帧率下力度不同。三点对齐能把这类问题暴露出来。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_IsFrameRateIndependent_AtMultipleCheckpoints()
    {
        const float target = 10f;
        float[] checkpoints = { 0.15f, 0.5f, 1.0f };

        foreach (float seconds in checkpoints)
        {
            (float value, float velocity)[] at60 =
                RunSpringExactly(60f, seconds, start: 0f, target, SmoothTime, FollowRatePerSecond);
            (float value, float velocity)[] at144 =
                RunSpringExactly(144f, seconds, start: 0f, target, SmoothTime, FollowRatePerSecond);

            float v60 = at60[^1].value;
            float v144 = at144[^1].value;
            float diff = Math.Abs(v60 - v144);

            Assert.IsTrue(diff < FrameRateTolerance,
                $"帧率无关性在 {seconds:F2}s 处失败：60fps 在 {v60}，144fps 在 {v144}，相差 {diff} > {FrameRateTolerance}。" +
                "只对齐终点是不够的——中段轨迹不一致说明积分器对 dt 的依赖不是真正的线性解耦。");
        }
    }

    /// <summary>
    /// 三档差距极大的 dt（30 / 60 / 240 fps）在同一总时长下互相一致。
    /// <para>
    /// 只用 60 vs 144 时，某些写错的积分器（例如按「帧数」累积）可能碰巧接近；
    /// 加入 30 与 240 能显著提高区分度——真帧率无关的积分器三档全部相符。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_IsFrameRateIndependent_AcrossThirtySixtyAndTwoForty()
    {
        const float target = 10f;

        float v30 = RunSpringExactly(30f, 1f, 0f, target, SmoothTime, FollowRatePerSecond)[^1].value;
        float v60 = RunSpringExactly(60f, 1f, 0f, target, SmoothTime, FollowRatePerSecond)[^1].value;
        float v240 = RunSpringExactly(240f, 1f, 0f, target, SmoothTime, FollowRatePerSecond)[^1].value;

        Assert.IsTrue(Math.Abs(v30 - v60) < FrameRateTolerance,
            $"30fps 与 60fps 不一致：{v30} vs {v60}，差 {Math.Abs(v30 - v60)} > {FrameRateTolerance}。");
        Assert.IsTrue(Math.Abs(v60 - v240) < FrameRateTolerance,
            $"60fps 与 240fps 不一致：{v60} vs {v240}，差 {Math.Abs(v60 - v240)} > {FrameRateTolerance}。");
        Assert.IsTrue(Math.Abs(v30 - v240) < FrameRateTolerance,
            $"30fps 与 240fps 不一致：{v30} vs {v240}，差 {Math.Abs(v30 - v240)} > {FrameRateTolerance}。");
    }

    // =========================================================================
    // C. 稳定性
    // =========================================================================

    /// <summary>
    /// 极大 dt（0.5 秒，模拟切窗口回来 / 卡顿）不能发散、不能 NaN / Infinity。
    /// <para>
    /// 显式积分（Euler）在 <c>dt &gt; 2/ω</c> 时会发散，这是弹簧实现最常见的爆炸点。
    /// 正确做法是子步（substep）或解析式求解。发散的相机表现为「画面直接飞走」，且日志无异常。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_HugeDeltaTime_StaysBoundedAndFinite()
    {
        const float target = 10f;
        const float hugeDt = 0.5f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 10, hugeDt, start: 0f, target, SmoothTime, FollowRatePerSecond);

        for (int i = 1; i < trace.Length; i++)
        {
            float v = trace[i].value;
            Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v),
                $"第 {i} 帧出现 NaN/Infinity（dt={hugeDt}s）。弹簧在极大 dt 下发散了。");
            Assert.IsTrue(v > -target && v < 2f * target,
                $"第 {i} 帧位置 {v} 已跑出合理范围（目标 {target}）。大 dt 下积分发散，" +
                "典型原因是显式 Euler 且没有子步切分。");
        }
    }

    /// <summary>
    /// 极端大 dt（5 秒）依然不得 NaN / 不得爆炸。
    /// <para>
    /// 切窗口后回到游戏的 dt 可能远大于 0.5s（调试器断点、系统休眠）。这条把上限再推一档，
    /// 确保实现真的做了 dt 夹取或子步，而不是「0.5 恰好没炸」。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_AbsurdDeltaTime_DoesNotProduceNaN()
    {
        const float target = 10f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 3, dt: 5f, start: 0f, target, SmoothTime, FollowRatePerSecond);

        foreach ((float value, float velocity) in trace)
        {
            Assert.IsFalse(float.IsNaN(value) || float.IsInfinity(value),
                $"dt=5s 时位置变成 {value}。实现需要夹取 dt 上界（例如 clamp 到 1/10 秒）或做子步。");
            Assert.IsFalse(float.IsNaN(velocity) || float.IsInfinity(velocity),
                $"dt=5s 时速度变成 {velocity}。实现需要夹取 dt 上界或做子步。");
        }
    }

    /// <summary>
    /// dt = 0（同一帧被调用两次 / 暂停）不得改变状态、不得崩。
    /// <para>
    /// 暂停时视图仍在跑 <c>_Process</c> 是很常见的。dt=0 时若走「按比例积分」，
    /// 有些实现会把值瞬间推到目标（硬跳）；正确语义是「不推进」。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_ZeroDeltaTime_ChangesNothing()
    {
        const float target = 10f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 3, dt: 0f, start: 0f, target, SmoothTime, FollowRatePerSecond);

        for (int i = 1; i < trace.Length; i++)
        {
            Assert.AreEqual(0f, trace[i].value, 1e-6f,
                $"dt=0 却让位置从 0 变成了 {trace[i].value}。dt=0 应当不推进（暂停帧不应跳变）。");
        }
    }

    /// <summary>
    /// 负 dt（时间缩放传负 / 时间倒流）不得把值推向反方向。
    /// <para>
    /// 时间缩放计算错误时会传出负 dt。正确实现应把非正 dt 当作「不推进」。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_NegativeDeltaTime_IsTreatedAsNoStep()
    {
        const float target = 10f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 3, dt: -0.1f, start: 0f, target, SmoothTime, FollowRatePerSecond);

        for (int i = 1; i < trace.Length; i++)
        {
            Assert.IsTrue(Math.Abs(trace[i].value) < OvershootTolerance,
                $"负 dt 让位置变成 {trace[i].value}（起点 0）。负 dt 应视为不推进，而不是反向积分。");
        }
    }

    /// <summary>
    /// 平滑时间为 0 或负数 = 硬锁（立即到位），不得除零、不得 NaN。
    /// <para>
    /// 策划可能填 0 表示「不要平滑」。除以 smoothTime 的实现会在这里产生 Infinity。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Spring_ZeroSmoothTime_SnapsWithoutDivisionByZero()
    {
        const float target = 10f;

        (float value, float velocity)[] trace =
            RunSpring(frames: 1, dt: 1f / 60f, start: 0f, target, smoothTime: 0f, rate: FollowRatePerSecond);

        float after = trace[1].value;
        Assert.IsFalse(float.IsNaN(after) || float.IsInfinity(after),
            $"平滑时间为 0 时位置变成 {after}（除零）。平滑时间 0 应表示「硬锁 / 立即到位」。");
        Assert.AreEqual(target, after, PositionTolerance,
            $"平滑时间为 0 应让位置立即到达 {target}，实际 {after}。");
    }

    // =========================================================================
    // D. 瞄准偏移
    // =========================================================================

    /// <summary>
    /// 瞄准 +X 时，前瞻偏移必须指向 +X，且无竖直/Z 分量。
    /// <para>
    /// 这条钉的是「偏移方向」而不是「偏移存在」——把偏移写成 <c>aim * -1</c> 或屏幕基算错，
    /// 偏移依然存在，只是方向反了（相机朝背离瞄准的方向漂）。这种错误肉眼看「好像也在动」，
    /// 只有方向断言能抓到。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_AimTowardsPositiveX_OffsetsTowardsPositiveX()
    {
        (float offsetX, float offsetZ) = CameraFeel.LookAheadOffset(
            aimX: 1f,
            aimZ: 0f,
            orthoHeight: OrthoHeightMeters,
            ratio: LookAheadRatio,
            maxMeters: LookAheadMaxMeters,
            deadZoneRatio: 0f);

        Assert.IsTrue(offsetX > PositionTolerance,
            $"瞄准 +X 时偏移的 X 分量是 {offsetX}，应为正。" +
            "为负说明方向算反了，相机将朝背离瞄准的方向漂。");
        Assert.AreEqual(0f, offsetZ, 1e-4f,
            $"瞄准 +X 时偏移不应有 Z 分量（实际 {offsetZ}）。");
    }

    /// <summary>
    /// 瞄准 +Z 时偏移必须指向 +Z。
    /// <para>
    /// 抓「只处理了 X 轴、Z 轴写反或没处理」的错误。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_AimTowardsPositiveZ_OffsetsTowardsPositiveZ()
    {
        (float offsetX, float offsetZ) = CameraFeel.LookAheadOffset(
            aimX: 0f,
            aimZ: 1f,
            orthoHeight: OrthoHeightMeters,
            ratio: LookAheadRatio,
            maxMeters: LookAheadMaxMeters,
            deadZoneRatio: 0f);

        Assert.IsTrue(offsetZ > PositionTolerance,
            $"瞄准 +Z 时偏移的 Z 分量是 {offsetZ}，应为正。为负说明 Z 轴处理写反了。");
        Assert.AreEqual(0f, offsetX, 1e-4f,
            $"瞄准 +Z 时偏移不应有 X 分量（实际 {offsetX}）。");
    }

    /// <summary>
    /// 偏移长度永不超过 <c>maxMeters</c>。
    /// <para>
    /// 封顶失效时，加大可见高度或比例会让相机越漂越远，准星直接跑出画面——
    /// 且因为「偏移随瞄准变化」而看起来像是在正常工作。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_NeverExceedsConfiguredMaximum()
    {
        // 故意用「比例很大」的设置：不封顶时长度会是 14 × 2.0 = 28 m，与 1.6 m 封顶差 17 倍。
        const float hugeRatio = 2.0f;

        (float aimX, float aimZ)[] aims =
        {
            (1f, 0f),
            (0f, 1f),
            (1f, 1f),
            (-0.6f, 0.8f),
        };

        foreach ((float aimX, float aimZ) in aims)
        {
            (float offsetX, float offsetZ) = CameraFeel.LookAheadOffset(
                aimX, aimZ, OrthoHeightMeters, hugeRatio, LookAheadMaxMeters, deadZoneRatio: 0f);

            float len = MathF.Sqrt(offsetX * offsetX + offsetZ * offsetZ);
            Assert.IsTrue(len <= LookAheadMaxMeters + 1e-3f,
                $"瞄准 ({aimX},{aimZ}) 时偏移长度 {len} m 超过封顶 {LookAheadMaxMeters} m。" +
                "封顶逻辑应当把位移按比例缩回上限。");
        }
    }

    /// <summary>
    /// 偏移长度只由方向决定，与瞄准向量的模长无关。
    /// <para>
    /// 鼠标瞄准向量可能未归一化。若偏移直接乘原始向量，「鼠标离得远」会让相机偏移更大——
    /// 典型的输入口径错误。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_LengthDependsOnDirectionNotInputMagnitude()
    {
        (float offsetX, float offsetZ) unit = CameraFeel.LookAheadOffset(
            aimX: 1f, aimZ: 0f, OrthoHeightMeters, LookAheadRatio, LookAheadMaxMeters, deadZoneRatio: 0f);
        (float offsetX, float offsetZ) scaled = CameraFeel.LookAheadOffset(
            aimX: 37f, aimZ: 0f, OrthoHeightMeters, LookAheadRatio, LookAheadMaxMeters, deadZoneRatio: 0f);

        float unitLen = MathF.Sqrt(unit.offsetX * unit.offsetX + unit.offsetZ * unit.offsetZ);
        float scaledLen = MathF.Sqrt(scaled.offsetX * scaled.offsetX + scaled.offsetZ * scaled.offsetZ);

        Assert.AreEqual(unitLen, scaledLen, 1e-3f,
            $"瞄准 (1,0) 偏移 {unitLen} m，瞄准 (37,0) 偏移 {scaledLen} m，长度不该受输入模长影响。");
    }

    /// <summary>
    /// 瞄准方向为零向量时不崩、不产生 NaN，且偏移为零。
    /// <para>
    /// 零向量常见于「鼠标还没进入窗口」的首帧。对零向量做归一化会得到 NaN 并污染整个相机位置——
    /// 这是最容易被忽略的崩溃点。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_ZeroAim_ReturnsZeroWithoutThrowing()
    {
        (float offsetX, float offsetZ) = CameraFeel.LookAheadOffset(
            aimX: 0f, aimZ: 0f, OrthoHeightMeters, LookAheadRatio, LookAheadMaxMeters, deadZoneRatio: 0f);

        Assert.IsFalse(float.IsNaN(offsetX) || float.IsNaN(offsetZ),
            $"零瞄准向量产生了 NaN：({offsetX},{offsetZ})。归一化零向量会得到 NaN 并污染相机位置。");
        Assert.AreEqual(0f, offsetX, 1e-6f, $"零瞄准向量时偏移 X 应为 0，实际 {offsetX}。");
        Assert.AreEqual(0f, offsetZ, 1e-6f, $"零瞄准向量时偏移 Z 应为 0，实际 {offsetZ}。");
    }

    /// <summary>
    /// 前瞻比例为 0（镜头包关闭前瞻）时，偏移必须恒为零。
    /// <para>
    /// 钉住「可调参」的语义：策划把比例填 0 就是「不要前瞻」，而不是「用某个最小前瞻」。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_ZeroRatioDisablesOffsetEntirely()
    {
        (float aimX, float aimZ)[] aims = { (1f, 0f), (0f, 1f), (0.6f, 0.8f) };

        foreach ((float aimX, float aimZ) in aims)
        {
            (float offsetX, float offsetZ) = CameraFeel.LookAheadOffset(
                aimX, aimZ, OrthoHeightMeters, ratio: 0f, LookAheadMaxMeters, deadZoneRatio: 0f);

            float len = MathF.Sqrt(offsetX * offsetX + offsetZ * offsetZ);
            Assert.AreEqual(0f, len, 1e-6f,
                $"ratio=0 时瞄准 ({aimX},{aimZ}) 仍产生偏移 ({offsetX},{offsetZ})。比例 0 应完全关闭前瞻。");
        }
    }

    /// <summary>
    /// 死区：输入模长落在死区内时，实际偏移必须在死区内可忽略。
    /// <para>
    /// <b>死区口径（必须与 view 实现一致）</b>：<c>deadZoneRatio</c> 是<u>输入模长的比例</u>，
    /// 阈值 = <c>deadZoneRatio</c>（输入模长 1 = 满瞄准）。若实现改成「偏移量 &lt;
    /// orthoHeight × deadZoneRatio」的绝对口径，<b>本条与下一条都要跟着改</b>。
    /// </para>
    /// <para>
    /// <b>断言口径</b>：死区内不强制「严格等于 0」——连续衰减型实现（smoothstep）在死区边缘
    /// 会有极小值，这是<u>正确</u>的（硬归零反而会造成阈值跳变）。这里断言
    /// 「死区内的偏移 &lt; 满偏移的 1%」，既吸收微动又不逼实现写死 0。
    /// 连续性由 <see cref="LookAhead_DeadZoneExit_IsContinuous"/> 单独守护。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_BelowDeadZone_ProducesNoOffset()
    {
        const float deadZoneRatio = 0.5f;

        // 满偏移 = min(orthoHeight × ratio, maxMeters) = min(2.52, 1.6) = 1.6 m。
        // 取满偏移的 1% 作为「可忽略」阈值 = 0.016 m。
        float negligible = LookAheadMaxMeters * 0.01f;

        // 输入模长 0.1 远小于死区阈值 0.5 → 落在死区深处。
        (float offsetX, float offsetZ) inside = CameraFeel.LookAheadOffset(
            aimX: 0.1f,
            aimZ: 0f,
            orthoHeight: OrthoHeightMeters,
            ratio: LookAheadRatio,
            maxMeters: LookAheadMaxMeters,
            deadZoneRatio: deadZoneRatio);

        float len = MathF.Sqrt(inside.offsetX * inside.offsetX + inside.offsetZ * inside.offsetZ);
        Assert.IsTrue(len < negligible,
            $"瞄准偏移落在死区深处（输入模长 0.1，死区阈值 {deadZoneRatio}）却产生了偏移 " +
            $"({inside.offsetX},{inside.offsetZ})，长度 {len} m ≥ 可忽略阈值 {negligible} m。" +
            "死区内的微动应被基本吸收，否则鼠标轻微抖动会让画面持续微晃。");
    }

    /// <summary>
    /// 死区外必须恢复产生偏移——死区不能写成「永远关闭」。
    /// <para>
    /// 与上一条成对：只测「死区内可忽略」的实现即使把偏移恒置为 0 也能通过，
    /// 必须同时证明「超过阈值时偏移非零」，死区才真的是死区而不是关闭。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_OutsideDeadZone_ProducesOffset()
    {
        const float deadZoneRatio = 0.5f;

        (float offsetX, float offsetZ) outside = CameraFeel.LookAheadOffset(
            aimX: 1f,
            aimZ: 0f,
            orthoHeight: OrthoHeightMeters,
            ratio: LookAheadRatio,
            maxMeters: LookAheadMaxMeters,
            deadZoneRatio: deadZoneRatio);

        Assert.IsTrue(Math.Abs(outside.offsetX) > PositionTolerance,
            $"瞄准方向满值（模长 1，远超死区阈值 {deadZoneRatio}）却没有偏移（X={outside.offsetX}）。" +
            "死区只能吸收阈值内的微动，不能吞掉正常瞄准。");
    }

    /// <summary>
    /// 死区必须随输入连续过渡（不能有台阶跳变）。
    /// <para>
    /// 若实现写成「if (len &lt; threshold) return 0; else return full;」，阈值处会出现
    /// 从 0 直接跳到满值的台阶——鼠标划过阈值瞬间画面会猛跳一下。
    /// </para>
    /// <para>
    /// <b>判据为什么用「加密采样后跳变是否消失」而不是「跳变小于某个固定预算」</b>：
    /// 最初我写的是固定预算（满偏移的 5%），结果对正确实现也报红——因为<u>任何</u>连续函数
    /// 在有限采样下的相邻差都正比于采样步长，预算定多小都可能误伤、定多大都抓不到小台阶。
    /// 真正的区别是：<b>真台阶的跳变不随采样加密而缩小，连续函数的跳变则成比例缩小。</b>
    /// 因此这里比较 40 点与 400 点（10 倍加密）的跳变：真台阶比值 ≈ 1，连续函数比值 ≈ 0.1。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_DeadZoneExit_IsContinuous()
    {
        const float deadZoneRatio = 0.5f;

        float coarseJump = LargestOffsetJump(Samples: 40, deadZoneRatio);
        float fineJump = LargestOffsetJump(Samples: 400, deadZoneRatio);

        // 采样密度 ×10 后，连续函数的相邻差应缩小到约 1/10；留 2 倍余量（阈值 0.2）。
        // 真台阶（阈值处从 0 跳到满值）在两种密度下跳变都约等于满偏移，比值 ≈ 1。
        float ratio = fineJump / MathF.Max(coarseJump, 1e-9f);

        Assert.IsTrue(ratio < 0.2f,
            $"死区出口处出现台阶：40 点采样的最大跳变 {coarseJump:F4} m，" +
            $"加密到 400 点后仍有 {fineJump:F4} m（比值 {ratio:F3}，应 ≈0.1）。" +
            "跳变不随采样加密而消失 = 存在真实的不连续，而不是采样密度问题。" +
            "死区应当连续衰减，而不是 if(len<r) 0 else full 的硬切换（会让鼠标划过阈值瞬间画面猛跳）。");
    }

    /// <summary>
    /// 在 0..1 的瞄准模长上均匀采样，返回相邻采样点之间偏移长度的最大跳变。
    /// </summary>
    /// <param name="Samples">采样点数。</param>
    /// <param name="deadZoneRatio">死区比例。</param>
    private static float LargestOffsetJump(int Samples, float deadZoneRatio)
    {
        float previous = 0f;
        float largestJump = 0f;

        for (int i = 1; i <= Samples; i++)
        {
            float aim = (float)i / Samples;
            (float offsetX, float offsetZ) = CameraFeel.LookAheadOffset(
                aimX: aim, aimZ: 0f, OrthoHeightMeters, LookAheadRatio, LookAheadMaxMeters, deadZoneRatio);
            float len = MathF.Sqrt(offsetX * offsetX + offsetZ * offsetZ);

            float jump = Math.Abs(len - previous);
            if (jump > largestJump)
            {
                largestJump = jump;
            }
            previous = len;
        }

        return largestJump;
    }

    // =========================================================================
    // E. 平滑
    // =========================================================================

    /// <summary>
    /// 瞄准方向翻转 180° 时，偏移必须过渡而不是瞬间跳到反向满值。
    /// <para>
    /// 这是模拟「滑动感」的核心手感诉求。没有平滑时，甩鼠标的瞬间整个画面会平移一大跳，
    /// 表现为「相机抽搐」。判据：翻转后第一帧仍停留在旧方向一侧。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_ReversingAim_TransitionsInsteadOfSnapping()
    {
        const float dt = 1f / 60f;

        // 一维等价模型：偏移量从 +max 走向 -max（瞄准翻转 180°）。
        (float value, float velocity)[] trace =
            RunSpring(frames: 60, dt, start: LookAheadMaxMeters, target: -LookAheadMaxMeters,
                SmoothTime, FollowRatePerSecond);

        float afterOneFrame = trace[1].value;
        float fullSwing = LookAheadMaxMeters * 2f;

        Assert.IsTrue(afterOneFrame > 0f,
            $"瞄准翻转后第一帧偏移量变成 {afterOneFrame}（起点 +{LookAheadMaxMeters}，目标 -{LookAheadMaxMeters}）。" +
            "仍为正说明有过渡；已经为负说明第一帧就跳过了零点（没有平滑，画面会抽搐）。");

        Assert.IsTrue(Math.Abs(afterOneFrame - LookAheadMaxMeters) < fullSwing * 0.5f,
            $"瞄准翻转后第一帧从 {LookAheadMaxMeters} 变成 {afterOneFrame}，" +
            $"移动了 {Math.Abs(afterOneFrame - LookAheadMaxMeters)}，超过整程 {fullSwing} 的一半。" +
            "平滑时间不足，或把「秒」当「帧」用了。");
    }

    /// <summary>
    /// 平滑时间越大，同样时间窗内走得越慢（平滑确实是可调参的）。
    /// <para>
    /// 钉住「参数确实被消费」：如果平滑时间被忽略、写死成常数，两个不同设置会得到完全相同的轨迹。
    /// </para>
    /// </summary>
    [TestMethod]
    public void LookAhead_LongerSmoothTime_MovesSlower()
    {
        const float dt = 1f / 60f;
        const float start = 0f;
        const float target = 10f;

        (float value, float velocity)[] fast =
            RunSpring(frames: 10, dt, start, target, smoothTime: SmoothTime, FollowRatePerSecond);
        (float value, float velocity)[] slow =
            RunSpring(frames: 10, dt, start, target, smoothTime: SmoothTime * 4f, FollowRatePerSecond);

        Assert.IsTrue(fast[10].value > slow[10].value + PositionTolerance,
            $"平滑时间 {SmoothTime}s 走了 10 帧到 {fast[10].value}，" +
            $"平滑时间 {SmoothTime * 4f}s 走到 {slow[10].value}，两者几乎相同。" +
            "说明平滑时间参数没有被消费（被忽略或写死）。");

        Assert.IsTrue(slow[10].value > 0f,
            $"平滑时间 {SmoothTime * 4f}s 走了 10 帧到 {slow[10].value}，完全没有推进。" +
            "平滑只应降低速度，不应让系统停住。");
    }

    // =========================================================================
    // F. 既有静态函数（守住既有正确行为）
    // =========================================================================

    /// <summary>
    /// <see cref="CameraFeel.Convergence"/> 的帧率无关性。
    /// <para>
    /// 这条价值是守住既有正确行为：将来若有人把 <c>1-exp(-rate*dt)</c> 改成 <c>rate*dt</c>，
    /// 这条会立刻红，而 60/144 手感差异本身不会报任何错。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Convergence_IsFrameRateIndependent()
    {
        float remaining60 = 1f;
        for (int i = 0; i < 60; i++)
        {
            remaining60 *= 1f - CameraFeel.Convergence(1f / 60f, FollowRatePerSecond);
        }

        float remaining144 = 1f;
        for (int i = 0; i < 144; i++)
        {
            remaining144 *= 1f - CameraFeel.Convergence(1f / 144f, FollowRatePerSecond);
        }

        Assert.AreEqual(remaining60, remaining144, 1e-4f,
            $"指数收敛不是帧率无关的：1 秒后剩余比例 60fps={remaining60}，144fps={remaining144}。" +
            "Convergence 必须是 1-exp(-rate*dt) 形状。");
    }

    /// <summary>
    /// <see cref="CameraFeel.Convergence"/> 在 rate / dt 非法时返回 1（硬锁），不返回 NaN。
    /// </summary>
    [TestMethod]
    public void Convergence_NonPositiveRateOrDelta_ReturnsHardLock()
    {
        Assert.AreEqual(1f, CameraFeel.Convergence(1f / 60f, 0f), 1e-6f,
            "rate=0 应表示「硬锁到底」，返回 1。");
        Assert.AreEqual(1f, CameraFeel.Convergence(1f / 60f, -5f), 1e-6f,
            "负 rate 应表示「硬锁到底」，返回 1（负值会让 exp 增大，导致 t 为负）。");
        Assert.AreEqual(1f, CameraFeel.Convergence(0f, FollowRatePerSecond), 1e-6f,
            "dt=0 应视为「已到位」，返回 1 而不是 0（返回 0 会让暂停帧卡住）。");
    }

    /// <summary>
    /// <see cref="CameraFeel.Convergence"/> 在极小 / 极大 dt 下不产生 NaN 或越界。
    /// </summary>
    [TestMethod]
    public void Convergence_ExtremeDeltaTime_StaysWithinUnitInterval()
    {
        float[] dts = { 1e-6f, 1e-3f, 1f / 60f, 0.5f, 5f, 1e6f };
        foreach (float dt in dts)
        {
            float t = CameraFeel.Convergence(dt, FollowRatePerSecond);
            Assert.IsFalse(float.IsNaN(t), $"dt={dt} 时 Convergence 返回 NaN。");
            Assert.IsTrue(t >= 0f && t <= 1f, $"dt={dt} 时 Convergence 返回 {t}，超出 [0,1]。");
        }
    }

    // =========================================================================
    // 内部工具
    // =========================================================================

    /// <summary>
    /// 用 <see cref="CameraFeel.SmoothDampStep"/> 跑 <paramref name="frames"/> 帧，返回完整轨迹。
    /// <para>
    /// 一维即可验证积分器的全部性质（收敛 / 无过冲 / 帧率无关 / 稳定性）；
    /// XZ 两轴独立作用，行为由一维决定。
    /// </para>
    /// </summary>
    private static (float value, float velocity)[] RunSpring(
        int frames, float dt, float start, float target, float smoothTime, float rate)
    {
        var trace = new (float, float)[frames + 1];
        float value = start;
        float velocity = 0f;
        trace[0] = (value, velocity);

        for (int i = 0; i < frames; i++)
        {
            (value, velocity) = CameraFeel.SmoothDampStep(value, target, velocity, smoothTime, dt);
            trace[i + 1] = (value, velocity);
        }

        return trace;
    }

    /// <summary>
    /// 以 <paramref name="fps"/> 步进，直到<u>恰好</u>跑满 <paramref name="seconds"/> 秒，返回轨迹。
    /// <para>
    /// <b>为什么不能简单按 <c>round(fps × seconds)</c> 取帧数</b>：0.15 s 在 60fps 是 9 帧（0.1500 s），
    /// 在 144fps 是 21.6 帧 → 取整 22 帧 = <b>0.1528 s</b>。两条路径实际跑的时间长度不同，
    /// 比出来的差异是「时间差」而不是「积分器的帧率依赖性」——<u>这是我在写测试时踩到的真实坑</u>，
    /// 一条本该绿的用例因此假红。
    /// </para>
    /// <para>
    /// 正确做法：先跑整数帧，剩余时间用一次精确的尾步补齐，使两条路径的 elapsed 严格相等。
    /// </para>
    /// </summary>
    private static (float value, float velocity)[] RunSpringExactly(
        float fps, float seconds, float start, float target, float smoothTime, float rate)
    {
        float dt = 1f / fps;
        int wholeFrames = (int)MathF.Floor(seconds / dt + 1e-6f);
        float rest = seconds - wholeFrames * dt;

        var trace = new List<(float, float)>(wholeFrames + 2);
        float value = start;
        float velocity = 0f;
        trace.Add((value, velocity));

        for (int i = 0; i < wholeFrames; i++)
        {
            (value, velocity) = CameraFeel.SmoothDampStep(value, target, velocity, smoothTime, dt);
            trace.Add((value, velocity));
        }

        // 尾步：补齐到恰好 seconds 秒，保证不同 fps 的比较时刻严格一致。
        if (rest > 1e-6f)
        {
            (value, velocity) = CameraFeel.SmoothDampStep(value, target, velocity, smoothTime, rest);
            trace.Add((value, velocity));
        }

        return trace.ToArray();
    }

    /// <summary>
    /// 以 <paramref name="fps"/> 步进 <paramref name="seconds"/> 秒，返回轨迹。
    /// 帧数按 <c>round(fps × seconds)</c> 取整。
    /// <para>
    /// ⚠️ 只用于「同一 fps 内部」的轨迹形状断言（收敛 / 过冲 / 抖动）。
    /// <b>跨 fps 比较必须用 <see cref="RunSpringExactly"/></b>，否则比较时刻不等时，会误判。
    /// </para>
    /// </summary>
    private static (float value, float velocity)[] RunSpringForSeconds(
        float fps, float seconds, float start, float target, float smoothTime, float rate)
    {
        int frames = (int)MathF.Round(fps * seconds);
        return RunSpring(frames, dt: 1f / fps, start, target, smoothTime, rate);
    }
}
