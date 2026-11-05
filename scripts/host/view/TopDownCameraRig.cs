using System;
using Godot;

namespace Game.Host.Presentation;

/// <summary>
/// 正交（<see cref="Camera3D.ProjectionType.Orthogonal"/>）俯视跟随相机。
/// <para>
/// <b>职责边界</b>：只负责「相机在哪、朝哪、取多大景」，<u>不读任何数据表</u>。
/// 全部参数来自构造时注入的 <see cref="CameraRigSettings"/>（纯值结构体，由 Lead 的
/// <c>CharacterSession</c> 从「镜头包」合并后传入），因此本类不认识 <c>Game.Data</c>，
/// 换一套镜头包 = 换一个 <see cref="CameraRigSettings"/>，本文件 0 行改动（规则7）。
/// </para>
/// <para>
/// <b>为什么跟随要用指数收敛而不是 <c>Lerp(a, b, t)</c></b>：<c>Lerp</c> 的第三种写法
/// <c>1 - exp(-rate * dt)</c> 让「每秒收敛比例」与帧率无关——144Hz 与 60Hz 下同样的 rate
/// 得到同样的手感；而 <c>Lerp(a, b, 0.2f)</c> 在 144Hz 下会比 60Hz 快 2.4 倍。手感优先的项目
/// 里帧率无关是硬要求（拖影/抖动会直接毁掉瞄准体验）。
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
    private Vector3 _focus;          // 平滑后的聚焦点（权威：下一帧从这里继续收敛）
    private Vector3 _lookAhead;      // 平滑后的前瞻位移（水平面内）

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
    {
        _settings = new CameraRigSettings(
            _settings.Distance,
            _settings.PitchDegrees,
            _settings.YawDegrees,
            _settings.FollowLerp,
            _settings.OrthoHeight,
            _settings.LookAheadRatio,
            _settings.LookAheadMaxMeters,
            _settings.FollowHeight,
            _settings.NearMeters,
            _settings.FarMeters,
            enabled,
            minXZ,
            maxXZ,
            _settings.BoundsPadding);
    }

    /// <summary>
    /// 立即把相机贴到目标点（不做平滑）。用于开局、传送、切场景：希望「没有飞过去的过程。」
    /// </summary>
    /// <param name="target">跟随目标的世界位置。</param>
    /// <param name="aimDirection">瞄准方向（水平面单位向量；零向量表示无前瞻）。</param>
    public void SnapTo(Vector3 target, Vector3 aimDirection)
    {
        Vector3 clamped = ClampToBounds(target);
        _focus = clamped;
        _lookAhead = ComputeLookAhead(aimDirection, clamped);
        _hasFocus = true;
        ApplyTransform();
    }

    /// <summary>
    /// 推一帧跟随。由视图层的每帧回调调用（<c>_Process</c>）。
    /// </summary>
    /// <param name="target">跟随目标的世界位置（通常 = 玩家的 <c>Transform3.Position</c>）。</param>
    /// <param name="aimDirection">瞄准方向（水平面单位向量；零向量表示无前瞻）。</param>
    /// <param name="deltaSeconds">本帧时长（秒）。用于帧率无关的指数收敛。</param>
    public void Tick(Vector3 target, Vector3 aimDirection, float deltaSeconds)
    {
        if (!_hasFocus)
        {
            SnapTo(target, aimDirection);
            return;
        }

        // ① 目标点先夹到边界内；边界夹取发生在「目标」而不是「结果」上，
        //    否则相机撞边界时会被反复拉扯（结果一直被推出界外再被拉回），画面会抖。
        Vector3 desired = ClampToBounds(target);

        // ② 前瞻：沿瞄准方向前移，让准星不贴边（轻量，按可见高度成比例，上限封顶）。
        Vector3 desiredAhead = ComputeLookAhead(aimDirection, desired);

        // ③ 帧率无关的指数收敛。
        float t = Convergence(deltaSeconds, _settings.FollowLerp);

        // ④ 竖直轴：正交俯视通常不跟高度（地面是平的），免得上下坡时画面抖。
        if (!_settings.FollowHeight)
        {
            desired.Y = _focus.Y;
            desiredAhead.Y = 0f;
        }

        _focus = _focus.Lerp(desired, t);
        _lookAhead = _lookAhead.Lerp(desiredAhead, t);
        ApplyTransform();
    }

    /// <summary>
    /// 指数收敛系数：<c>1 - exp(-rate * dt)</c>，夹到 [0,1]。rate &lt;= 0 表示硬锁（返回 1）。
    /// </summary>
    /// <param name="deltaSeconds">本帧时长。</param>
    /// <param name="rate">每秒收敛速率。</param>
    public static float Convergence(float deltaSeconds, float rate)
    {
        if (!(rate > 0f) || !(deltaSeconds > 0f))
        {
            return 1f;
        }
        float t = 1f - MathF.Exp(-rate * deltaSeconds);
        return t < 0f ? 0f : (t > 1f ? 1f : t);
    }

    /// <summary>
    /// 计算给定聚焦点对应的相机**世界位置**（纯函数，便于裸测）。
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
    /// 「聚焦点 → 相机」的单位方向（俯仰/偏航，度）。yaw = 0、pitch = 90° 时返回 <c>+Y</c>。
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
    /// 计算前瞻位移（水平面内，长度受 <see cref="CameraRigSettings.LookAheadMaxMeters"/> 封顶）。
    /// <para>
    /// <b>为什么不能直接用瞄准方向的水平分量</b>：相机有偏航角时，世界 X/Z 与屏幕左右/上下不对应。
    /// 「朝屏幕外推」的正确做法是把瞄准方向投影到相机的<u>屏幕平面基</u>上：
    /// 取相机的 right 轴（屏幕右）与「世界 -Y 在屏幕平面上的投影」得到屏幕上方向，
    /// 再按分量加权合成世界位移。yaw = 0 时它退化成「瞄准方向的水平分量」，yaw ≠ 0 时也能正确跟随。
    /// </para>
    /// </summary>
    private Vector3 ComputeLookAhead(Vector3 aimDirection, Vector3 focus)
    {
        float ratio = _settings.LookAheadRatio;
        if (!(ratio > 0f))
        {
            return Vector3.Zero;
        }
        // 只取水平面分量：瞄准方向带一点点竖直分量时不该把相机推到地下或天上。
        Vector3 flatAim = new Vector3(aimDirection.X, 0f, aimDirection.Z);
        if (flatAim.LengthSquared() <= float.Epsilon)
        {
            return _hasFocus ? _lookAhead : Vector3.Zero;
        }
        flatAim = flatAim.Normalized();

        // 屏幕基：right = 相机的 +X；screenUp = 「世界里指向画面上方」的方向，
        // 由 forward（聚焦点→相机）与 right 叉乘得到，再投影回水平面。
        Vector3 toCamera = DirectionToCamera(_settings.PitchDegrees, _settings.YawDegrees);
        Vector3 forward = -toCamera;
        Vector3 right = forward.Cross(Vector3.Up);
        if (right.LengthSquared() <= float.Epsilon)
        {
            // 正俯视（pitch = 90°）时 forward 与 Up 平行，叉乘退化 —— 用偏航角直接给出 right。
            float yaw = Mathf.DegToRad(_settings.YawDegrees);
            right = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        }
        right = right.Normalized();
        Vector3 screenUp = right.Cross(forward).Normalized();   // 世界里的「屏幕上方向」（含竖直分量）
        Vector3 screenUpFlat = new Vector3(screenUp.X, 0f, screenUp.Z);
        if (screenUpFlat.LengthSquared() <= float.Epsilon)
        {
            return _hasFocus ? _lookAhead : Vector3.Zero;
        }
        screenUpFlat = screenUpFlat.Normalized();

        // 瞄准方向在屏幕平面上的分量（X = 往屏幕右，Y = 往屏幕上）。
        float sx = flatAim.Dot(right);
        float sy = flatAim.Dot(screenUpFlat);

        float want = _settings.OrthoHeight * ratio;
        float max = _settings.LookAheadMaxMeters;
        float len = MathF.Sqrt(sx * sx + sy * sy) * want;
        if (max > 0f && len > max)
        {
            float scale = max / len;
            sx *= scale;
            sy *= scale;
        }
        _ = focus;
        return right * (sx * want) + screenUpFlat * (sy * want);
    }

    /// <summary>把聚焦点夹进跟随边界（内缩 <see cref="CameraRigSettings.BoundsPadding"/>）。</summary>
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
        Vector3 up = MathF.Abs(_settings.PitchDegrees - RightAngleDegrees) < UpDegenerateEpsilon
            ? Vector3.Forward
            : Vector3.Up;
        _camera.LookAt(focus, up);
    }

    /// <summary>直角（度）：俯仰恰好 90° 时 LookAt 的 up 不能是 +Y。</summary>
    private const float RightAngleDegrees = 90f; // arch-ok: 几何常量（直角定义），不是玩法数值

    /// <summary>判定「俯仰接近 90°」的角度容差（度）。</summary>
    private const float UpDegenerateEpsilon = 0.01f; // arch-ok: 浮点安全阈值
}
