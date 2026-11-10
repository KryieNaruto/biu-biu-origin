using System;
using Game.Core.Camera;
using Godot;

namespace Game.Host.Presentation;

/// <summary>
/// 正交（<see cref="Camera3D.ProjectionType.Orthogonal"/>）俯视跟随相机 —— <b>薄壳</b>。
/// <para>
/// <b>职责边界</b>：本类只做三件事 —— ① 持有弹簧状态（聚焦点 / 速度 / 瞄准偏移）；
/// ② 每帧调用 <see cref="CameraFeel"/> 的<u>纯函数</u>推进状态；③ 把结果写进 <see cref="Camera3D"/>。
/// <b>所有手感数学都在 <see cref="CameraFeel"/> 里</b>（零引擎依赖、可逐帧断言），
/// 本文件不含任何积分/插值公式 —— 这样「帧率无关性」才能在没有引擎的进程里被证明。
/// </para>
/// <para>
/// <b>为什么跟随用「临界阻尼弹簧」而不是 <c>Lerp</c> / 指数收敛</b>：指数收敛是<u>一阶低通</u>，
/// 只记位置、不记速度——起步瞬间就按比例贴上去，没有速度积累，观感是「粘着走」。
/// 临界阻尼弹簧是<u>二阶系统</u>（位置 + 速度两个状态）：起步时速度从 0 逐渐攒起来 → 加速冲出，
/// 临近目标时阻尼吃掉速度 → 自然减速且<u>不过冲</u>。这就是动作游戏相机「滑动感」的物理来源。
/// 详细的三段轨迹见 <see cref="CameraFeel.SmoothDampStep"/> 的文档。
/// </para>
/// <para>
/// <b>帧率无关性怎么保证</b>：三重保障（全部实现在 <see cref="CameraFeel"/> 里）——
/// ① 每个子步用<u>解析解</u>推进，不是显式欧拉（后者 dt 稍大就发散）；
/// ② 一帧 dt 拆成不超过 <see cref="CameraFeel.MaxSubStepSeconds"/> 的固定上限子步；
/// ③ 整帧 dt 先夹到 [0, <see cref="CameraFeel.MaxFrameSeconds"/>]，防止切窗口回来时 dt 巨大把相机弹飞。
/// </para>
/// <para>
/// <b>为什么永远 <c>LookAt</c> 玩家</b>：正交投影下「看向哪」决定了画面里的一切是否正面朝向玩家；
/// 一旦相机只是平移而不朝向，墙面会以奇怪的角度斜着出现。因此每帧都重算聚焦点并朝向它，
/// 旋转不插值（插值会让玩家在快停时看到画面绕着转）。
/// </para>
/// <para>
/// <b>headless 安全</b>：本类只操作 <see cref="Camera3D"/> 的子集属性（Transform / Projection / Size /
/// Near / Far / Current），不采样鼠标、不做屏幕求交，因此在 <c>--headless</c> 下同样能跑满帧而不崩。
/// </para>
/// </summary>
public sealed class TopDownCameraRig
{
    private readonly Camera3D _camera;
    private CameraRigSettings _settings;

    private bool _hasFocus;
    private Vector3 _focus;             // 平滑后的聚焦点（权威：下一帧从这里继续推进）
    private Vector3 _focusVelocity;     // 聚焦点速度（米/秒）—— 弹簧的第二个状态
    private Vector3 _lookAhead;         // 平滑后的前瞻位移（水平面内）
    private Vector3 _lookAheadVelocity; // 前瞻位移速度（米/秒）

    /// <summary>构造并立即把相机配置成俯视正交（第一帧就位，避免开局从原点飞过去）。</summary>
    /// <param name="camera">要被驱动的相机（必须是已在场景树上的节点）。</param>
    /// <param name="settings">镜头设置（纯值）。</param>
    public TopDownCameraRig(Camera3D camera, in CameraRigSettings settings)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _settings = settings;
        ApplyStaticProjection();
    }

    /// <summary>当前镜头设置（只读快照）。</summary>
    public CameraRigSettings Settings => _settings;

    /// <summary>平滑后的聚焦点（= 相机看向的世界点）。诊断/截图脚本可读。</summary>
    public Vector3 Focus => _focus;

    /// <summary>聚焦点当前速度（米/秒）。诊断用；也可用来做「速度越大 FOV 越大」之类的后续效果。</summary>
    public Vector3 FocusVelocity => _focusVelocity;

    /// <summary>当前平滑后的瞄准偏移（水平面内，米）。诊断用。刻意不叫 LookAheadOffset：那名字被静态纯函数占用了。</summary>
    public Vector3 CurrentLookAhead => _lookAhead;

    /// <summary>最近一次计算的相机世界位置。</summary>
    public Vector3 CameraPosition => _camera.GlobalPosition;

    /// <summary>正交可见高度（米）——即 <see cref="Camera3D.Size"/>。</summary>
    public float OrthoHeight => _camera.Size;

    /// <summary>
    /// 热替换镜头设置（换镜头包 / 换变体时调用）。只重设投影相关的静态量，不影响当前平滑状态。
    /// </summary>
    /// <param name="settings">新的镜头设置。</param>
    public void ApplySettings(in CameraRigSettings settings)
    {
        _settings = settings;
        ApplyStaticProjection();
    }

    /// <summary>
    /// 设置跟随边界（关卡包围盒，X/Z 平面）。用于「相机不跑出房间」。
    /// </summary>
    /// <param name="minXZ">最小角。</param>
    /// <param name="maxXZ">最大角。</param>
    /// <param name="enabled">是否立即启用。</param>
    public void SetBounds(Vector2 minXZ, Vector2 maxXZ, bool enabled)
        => _settings = _settings.WithBounds(minXZ, maxXZ, enabled);

    /// <summary>
    /// 立即把相机贴到目标点（不做平滑）。用于开局、传送、切场景：希望「没有飞过去的过程」。
    /// <para>速度状态一并清零——否则贴过去之后残余速度会让相机继续滑一段。</para>
    /// </summary>
    /// <param name="target">跟随目标的世界位置。</param>
    /// <param name="aimDirection">瞄准方向（水平面单位向量；零向量表示无前瞻）。</param>
    public void SnapTo(Vector3 target, Vector3 aimDirection)
    {
        Vector3 clamped = ClampToBounds(target);
        _focus = clamped;
        _focusVelocity = Vector3.Zero;
        _lookAhead = ComputeLookAheadTarget(aimDirection, clamped);
        _lookAheadVelocity = Vector3.Zero;
        _hasFocus = true;
        ApplyTransform();
    }

    /// <summary>
    /// 推一帧跟随。由视图层的每帧回调调用（<c>_Process</c>）。
    /// <para>
    /// 内部把 dt 拆成固定上限子步，每个子步对「聚焦点」和「瞄准偏移」各推进一个独立的临界阻尼弹簧。
    /// </para>
    /// </summary>
    /// <param name="target">跟随目标的世界位置（通常 = 玩家的 <c>Transform3.Position</c>）。</param>
    /// <param name="aimDirection">瞄准方向（水平面单位向量；零向量表示无前瞻）。</param>
    /// <param name="deltaSeconds">本帧时长（秒）。</param>
    public void Tick(Vector3 target, Vector3 aimDirection, float deltaSeconds)
    {
        if (!_hasFocus)
        {
            SnapTo(target, aimDirection);
            return;
        }

        float dt = CameraFeel.ClampFrameDelta(deltaSeconds);
        if (!(dt > 0f))
        {
            // dt 为 0 / NaN：什么都不推进，但相机可能被别人动过，仍重写一次变换。
            ApplyTransform();
            return;
        }

        // ① 目标点先夹到边界内；边界夹取发生在「目标」而不是「结果」上，
        //    否则相机撞边界时会被反复拉扯（结果一直被推出界外再被拉回），画面会抖。
        Vector3 desired = ClampToBounds(target);

        // ② 前瞻：算出「最终该偏移到哪」（纯函数），平滑交给下面的弹簧。
        Vector3 desiredAhead = ComputeLookAheadTarget(aimDirection, desired);

        // ③ 竖直轴：正交俯视通常不跟高度（地面是平的），免得上下坡时画面抖。
        if (!_settings.FollowHeight)
        {
            desired.Y = _focus.Y;
            desiredAhead.Y = 0f;
        }

        // ④ 拆子步推进两个独立弹簧（跟随 / 瞄准偏移各有一套 smoothTime）。
        int steps = CameraFeel.SubStepCount(dt);
        float step = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            (float fx, float fz, float fvx, float fvz) = CameraFeel.SmoothDampStepXZ(
                _focus.X, _focus.Z, desired.X, desired.Z,
                _focusVelocity.X, _focusVelocity.Z,
                _settings.FollowSmoothTime, step);
            _focus = new Vector3(fx, desired.Y, fz);
            _focusVelocity = new Vector3(fvx, 0f, fvz);

            (float lx, float lz, float lvx, float lvz) = CameraFeel.SmoothDampStepXZ(
                _lookAhead.X, _lookAhead.Z, desiredAhead.X, desiredAhead.Z,
                _lookAheadVelocity.X, _lookAheadVelocity.Z,
                _settings.LookAheadSmoothTime, step);
            _lookAhead = new Vector3(lx, 0f, lz);
            _lookAheadVelocity = new Vector3(lvx, 0f, lvz);
        }

        ApplyTransform();
    }

    // =========================================================================
    // 纯函数代理（全部委托给 Game.Core.Camera.CameraFeel）
    //
    // 这些 static 转发并非多余：① 测试与诊断脚本按「相机」的语义找它们；
    // ② 让表现层调用点集中在此，将来 Game.Core 拆库时只改一行。
    // =========================================================================

    /// <summary>临界阻尼弹簧单步（单轴）。见 <see cref="CameraFeel.SmoothDampStep"/>。</summary>
    /// <param name="current">当前位置。</param>
    /// <param name="target">目标位置。</param>
    /// <param name="velocity">当前速度。</param>
    /// <param name="smoothTime">平滑时间（秒）。</param>
    /// <param name="deltaSeconds">本步时长（秒）。</param>
    /// <returns>推进后的 (值, 速度)。</returns>
    public static (float value, float velocity) SmoothDampStep(
        float current, float target, float velocity, float smoothTime, float deltaSeconds)
        => CameraFeel.SmoothDampStep(current, target, velocity, smoothTime, deltaSeconds);

    /// <summary>临界阻尼弹簧单步（XZ 平面，两轴独立）。见 <see cref="CameraFeel.SmoothDampStepXZ"/>。</summary>
    /// <param name="x">当前 X。</param>
    /// <param name="z">当前 Z。</param>
    /// <param name="targetX">目标 X。</param>
    /// <param name="targetZ">目标 Z。</param>
    /// <param name="vx">X 速度。</param>
    /// <param name="vz">Z 速度。</param>
    /// <param name="smoothTime">平滑时间（秒）。</param>
    /// <param name="deltaSeconds">本步时长（秒）。</param>
    /// <returns>推进后的 (x, z, vx, vz)。</returns>
    public static (float x, float z, float vx, float vz) SmoothDampStepXZ(
        float x, float z, float targetX, float targetZ, float vx, float vz,
        float smoothTime, float deltaSeconds)
        => CameraFeel.SmoothDampStepXZ(x, z, targetX, targetZ, vx, vz, smoothTime, deltaSeconds);

    /// <summary>
    /// 指数收敛系数：<c>1 - exp(-rate * dt)</c>。见 <see cref="CameraFeel.Convergence"/>。
    /// <para>相机跟随已不再使用它；保留供准星跟随 / HUD 数值滚动等一阶平滑场景。</para>
    /// </summary>
    /// <param name="deltaSeconds">本帧时长。</param>
    /// <param name="rate">每秒速率；小于等于 0 返回 1（硬锁）。</param>
    /// <returns>本帧推进比例（0..1）。</returns>
    public static float Convergence(float deltaSeconds, float rate)
        => CameraFeel.Convergence(deltaSeconds, rate);

    /// <summary>「每秒速率」→「平滑时间」换算。见 <see cref="CameraFeel.SmoothTimeFromRate"/>。</summary>
    /// <param name="ratePerSecond">每秒速率。</param>
    /// <param name="fallbackSmoothTime">速率无效时的保底平滑时间（秒）。</param>
    /// <returns>平滑时间（秒）。</returns>
    public static float SmoothTimeFromRate(float ratePerSecond, float fallbackSmoothTime)
        => CameraFeel.SmoothTimeFromRate(ratePerSecond, fallbackSmoothTime);

    /// <summary>
    /// 计算瞄准偏移的<b>未平滑目标值</b>（世界空间，水平面内）。纯函数，不读实例状态。
    /// <para>
    /// <b>为什么不能直接用瞄准方向的水平分量</b>：相机有偏航角时，世界 X/Z 与屏幕左右/上下不对应。
    /// 「朝屏幕外推」的正确做法是把瞄准方向投影到相机的<u>屏幕平面基</u>上：
    /// 取相机的 right 轴（屏幕右）与「世界 -Y 在屏幕平面上的投影」得到屏幕上方向，
    /// 再按分量加权合成世界位移。yaw = 0 时它退化成「瞄准方向的水平分量」，yaw 不为 0 时也能正确跟随。
    /// </para>
    /// <para>
    /// 死区 / 封顶 / 比例全部由 <see cref="CameraFeel.LookAheadOffset"/> 负责（纯、可测）。
    /// </para>
    /// </summary>
    /// <param name="aimDirection">瞄准方向（水平面；零向量表示无方向）。</param>
    /// <param name="settings">镜头设置。</param>
    /// <returns>未平滑的偏移（世界空间，Y 恒为 0）。</returns>
    public static Vector3 LookAheadOffset(Vector3 aimDirection, CameraRigSettings settings)
    {
        // 只取水平面分量：瞄准方向带一点点竖直分量时不该把相机推到地下或天上。
        Vector3 flatAim = new Vector3(aimDirection.X, 0f, aimDirection.Z);
        if (flatAim.LengthSquared() <= float.Epsilon || !(settings.LookAheadRatio > 0f))
        {
            return Vector3.Zero;
        }
        flatAim = flatAim.Normalized();

        // 屏幕平面基。
        Vector3 toCamera = DirectionToCamera(settings.PitchDegrees, settings.YawDegrees);
        Vector3 forward = -toCamera;
        Vector3 right = forward.Cross(Vector3.Up);
        if (right.LengthSquared() <= float.Epsilon)
        {
            // 正俯视（pitch ≈ 90°）时 forward 与 Up 平行，叉乘退化 —— 用偏航角直接给出 right。
            // 注意：这里绝不能写 pitch == 90f，32 位浮点下 cos 会有 -4.37E-08 的残差。
            float yaw = Mathf.DegToRad(settings.YawDegrees);
            right = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        }
        right = right.Normalized();

        Vector3 screenUp = right.Cross(forward).Normalized();
        Vector3 screenUpFlat = new Vector3(screenUp.X, 0f, screenUp.Z);
        if (screenUpFlat.LengthSquared() <= float.Epsilon)
        {
            return Vector3.Zero;
        }
        screenUpFlat = screenUpFlat.Normalized();

        // 把世界瞄准方向投到屏幕平面上，得到 (往屏幕右, 往屏幕上) 两个分量。
        float aimRight = flatAim.Dot(right);
        float aimUp = flatAim.Dot(screenUpFlat);

        // 手感数学（死区 / 比例 / 封顶）全部在 Game.Core.Camera.CameraFeel 里，纯函数。
        (float offsetRight, float offsetUp) = CameraFeel.LookAheadOffset(
            aimRight, aimUp, settings.OrthoHeight,
            settings.LookAheadRatio, settings.LookAheadMaxMeters, settings.LookAheadDeadZoneRatio);

        return right * offsetRight + screenUpFlat * offsetUp;
    }

    /// <summary>
    /// 计算给定聚焦点对应的相机世界位置（纯函数，便于裸测）。
    /// <para>
    /// 几何：俯仰 pitch、偏航 yaw 定义一条从「聚焦点朝反方向」的单位向量，
    /// 相机 = 聚焦点 - 该方向 × 距离。pitch = 90° 时相机正上方，pitch = 0° 时相机在水平面。
    /// </para>
    /// </summary>
    /// <param name="focus">聚焦点。</param>
    /// <param name="distance">距离。</param>
    /// <param name="pitchDegrees">俯仰角（度）。</param>
    /// <param name="yawDegrees">偏航角（度）。</param>
    public static Vector3 PositionFor(Vector3 focus, float distance, float pitchDegrees, float yawDegrees)
    {
        Vector3 toCamera = DirectionToCamera(pitchDegrees, yawDegrees);
        return focus + toCamera * distance;
    }

    /// <summary>
    /// 「聚焦点 → 相机」的单位方向（俯仰/偏航，度）。yaw = 0、pitch = 90° 时返回 +Y。
    /// </summary>
    /// <param name="pitchDegrees">俯仰角（度）。</param>
    /// <param name="yawDegrees">偏航角（度）。</param>
    public static Vector3 DirectionToCamera(float pitchDegrees, float yawDegrees)
    {
        float pitch = Mathf.DegToRad(pitchDegrees);
        float yaw = Mathf.DegToRad(yawDegrees);
        float horizontal = MathF.Cos(pitch);
        return new Vector3(
            horizontal * MathF.Sin(yaw),
            MathF.Sin(pitch),
            horizontal * MathF.Cos(yaw)).Normalized();
    }

    /// <summary>
    /// 计算「平滑后的偏移该朝哪走」—— 这是<u>状态机</u>部分，与纯函数
    /// <see cref="LookAheadOffset"/> 分开：瞄准方向为零时保持上一帧，而不是让相机弹回中心。
    /// </summary>
    private Vector3 ComputeLookAheadTarget(Vector3 aimDirection, Vector3 focus)
    {
        _ = focus;
        if (!(aimDirection.X * aimDirection.X + aimDirection.Z * aimDirection.Z > float.Epsilon))
        {
            // 没有瞄准方向（鼠标没指向地面 / headless）：保持上一帧偏移，避免画面突然回中。
            return _hasFocus ? _lookAhead : Vector3.Zero;
        }
        return LookAheadOffset(aimDirection, _settings);
    }

    /// <summary>把聚焦点夹进跟随边界（内缩 BoundsPadding）。</summary>
    private Vector3 ClampToBounds(Vector3 p)
    {
        if (!_settings.BoundsEnabled)
        {
            return p;
        }
        float pad = _settings.BoundsPadding;
        float minX = _settings.BoundsMin.X + pad;
        float maxX = _settings.BoundsMax.X - pad;
        float minZ = _settings.BoundsMin.Y + pad;
        float maxZ = _settings.BoundsMax.Y - pad;

        // 边界比内缩还窄时退化为「取中心」，而不是把 min/max 夹反了导致抖动。
        if (minX > maxX)
        {
            float cx = (_settings.BoundsMin.X + _settings.BoundsMax.X) * 0.5f;
            minX = cx;
            maxX = cx;
        }
        if (minZ > maxZ)
        {
            float cz = (_settings.BoundsMin.Y + _settings.BoundsMax.Y) * 0.5f;
            minZ = cz;
            maxZ = cz;
        }

        return new Vector3(
            p.X < minX ? minX : (p.X > maxX ? maxX : p.X),
            p.Y,
            p.Z < minZ ? minZ : (p.Z > maxZ ? maxZ : p.Z));
    }

    /// <summary>写相机的静态投影属性（正交 / 可见高度 / 裁剪面）。只在设置变化时调用。</summary>
    private void ApplyStaticProjection()
    {
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        _camera.Size = _settings.OrthoHeight;
        _camera.Near = _settings.NearMeters;
        _camera.Far = _settings.FarMeters;
        _camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
        _camera.Current = true;
        if (_hasFocus)
        {
            ApplyTransform();
        }
    }

    /// <summary>按当前聚焦点 + 前瞻写相机的世界变换，并朝向聚焦点。</summary>
    private void ApplyTransform()
    {
        Vector3 focus = _focus + new Vector3(_lookAhead.X, 0f, _lookAhead.Z);
        Vector3 position = PositionFor(focus, _settings.Distance, _settings.PitchDegrees, _settings.YawDegrees);
        _camera.GlobalPosition = position;
        // 朝向聚焦点：正交下这决定了画面里一切物的朝向（见类文档「为什么永远 LookAt 玩家」）。
        // 传入的 up 必须与视线不平行，否则 LookAt 会退化；俯仰 90° 时用 -Z（画面「上方」= 世界 +Z 方向）。
        // 注意：用|相减| < eps 而不是 == 90f —— 32 位浮点下 90° 会有残差。
        Vector3 up = MathF.Abs(_settings.PitchDegrees - RightAngleDegrees) < UpDegenerateEpsilon
            ? Vector3.Forward
            : Vector3.Up;
        _camera.LookAt(focus, up);
    }

    /// <summary>直角（度）：俯仰恰好 90° 时 LookAt 的 up 不能是 +Y。</summary>
    private const float RightAngleDegrees = 90f; // arch-ok: 几何常量（直角定义），不是玩法数值

    /// <summary>判定「俯仰接近 90°」的角度容差（度）。32 位浮点下 90° 有 -4.37E-08 残差，必须留容差。</summary>
    private const float UpDegenerateEpsilon = 0.01f; // arch-ok: 浮点安全阈值
}
