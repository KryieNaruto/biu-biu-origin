using System;
using Game.Core;
using Game.Core.Skills;
using Game.Host.Control;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace Game.Host.Presentation;

/// <summary>
/// 鼠标瞄准意图源：**唯一**读「鼠标屏幕坐标」与「相机 Unproject」的地方（ECS.md J4 引擎边界的收紧）。
/// <para>
/// <b>它做什么</b>：
/// <list type="number">
///   <item>装饰（decorate）一个内层 <see cref="PlayerInputIntentSource"/>：技能按键的按下/按住边沿语义
///         <u>完全复用</u>它（槽位 → InputMap action 的映射只有一处，改键/换手柄包不会两边漂移）；</item>
///   <item>把「鼠标屏幕点 → 相机射线 → 世界点」算出来，用世界点减去玩家位置得到水平朝向，
///         覆盖内层源给出的瞄准方向；</item>
///   <item>把鼠标世界点通过 <see cref="WorldPointChanged"/> 回调交给宿主（准星 / <c>AimTarget</c> 组件用）。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么是「装饰」而不是「继承」</b>：继承会把内层的字段、aim axis 读取、边沿状态全部变成实现细节，
/// 将来换成「手柄方案」时又要抄一遍；组合只要求内层满足 <see cref="IIntentSource"/>，
/// 于是「键鼠方案 + 鼠标世界点」「手柄方案 + 摇杆世界点」可以自由拼接，技能系统一行不改（规则7）。
/// </para>
/// <para>
/// <b>为什么瞄准叫「世界点」而不是「屏幕坐标」</b>：屏幕坐标是引擎概念，跨层传递会把 J4 的边界带进系统层；
/// 世界点（米）是玩法/表现共同的坐标语言，可以直接写进 <c>AimTarget.Point</c> 组件，也可以给准星画环。
/// </para>
/// <para>
/// <b>headless 安全</b>：headless 下 <c>Viewport.GetMousePosition()</c> 返回 (0,0)，
/// 本类检测到「鼠标从未进入窗口」或「射线求交失败」时<u>不更新</u>瞄准与回调，
/// 而不是把方向写成零向量（零向量会让技能系统退化到 +Z，回放/截图会得到错误朝向）。
/// </para>
/// </summary>
public sealed class CursorAimInputSource : IIntentSource
{
    /// <summary>内层意图源（提供按键边沿；瞄准部分会被本类覆盖）。</summary>
    private readonly IIntentSource _inner;

    /// <summary>瞄准平面高度（米）：射线与该水平面的交点是瞄准世界点。</summary>
    private readonly float _planeHeight;

    /// <summary>射线最大求交距离（米）。</summary>
    private readonly float _rayMaxDistance;

    /// <summary>最小瞄准距离（米）：鼠标离玩家太近时改用屏幕方向。</summary>
    private readonly float _minDistance;

    /// <summary>朝向退化阈值（米）。</summary>
    private readonly float _epsilon;

    /// <summary>相机提供者（每帧取一次：相机可能被替换 / 重新装配）。</summary>
    private readonly Func<Camera3D?> _cameraProvider;

    /// <summary>视口提供者（每帧取一次；headless / 无窗口时返回 null）。</summary>
    private readonly Func<Viewport?> _viewportProvider;

    /// <summary>Q16 的 1.0（与 <see cref="InputIntent"/> 的定点约定一致）。</summary>
    private const int FixedOne = 1 << 16; // arch-ok: Q16 定点表示法，不是玩法数值

    /// <summary>上一帧的瞄准方向（世界点不可用时沿用；初值 +X 让开局朝向稳定）。</summary>
    private NVector3 _lastAim = new NVector3(1f, 0f, 0f);

    /// <summary>这一帧是否算出了一个有效世界点（headless / 射线失败时为 false）。</summary>
    private bool _hasWorldPoint;

    /// <summary>最近一次的有效鼠标世界点。</summary>
    private NVector3 _worldPoint;

    /// <summary>最近一次的有效瞄准方向（单位向量，Y = 0）。</summary>
    private NVector3 _aimDirection = new NVector3(1f, 0f, 0f);

    /// <summary class="sig">鼠标世界点变化回调（宿主在这里写 <c>AimTarget</c> / 移动准星）。</summary>
    public event Action<Vector3>? WorldPointChanged;

    /// <summary>构造鼠标瞄准源。</summary>
    /// <param name="options">表现层参数（瞄准平面高度 / 射线距离 / 最小距离 / 退化阈值）。</param>
    /// <param name="cameraProvider">取当前相机（每帧调用一次；返回 null 表示暂无相机 → 本帧不更新瞄准）。</param>
    /// <param name="viewportProvider">取视口（可用 <c>() =&gt; GetViewport()</c>；返回 null 表示无窗口）。</param>
    /// <param name="inner">内层意图源；null = 新建默认的 <see cref="PlayerInputIntentSource"/>。</param>
    public CursorAimInputSource(
        GameViewOptions options,
        Func<Camera3D?> cameraProvider,
        Func<Viewport?> viewportProvider,
        IIntentSource? inner = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        _cameraProvider = cameraProvider ?? throw new ArgumentNullException(nameof(cameraProvider));
        _viewportProvider = viewportProvider ?? throw new ArgumentNullException(nameof(viewportProvider));
        _inner = inner ?? new PlayerInputIntentSource();
        _planeHeight = options.AimPlaneHeight;
        _rayMaxDistance = options.AimRayMaxDistance;
        _minDistance = options.AimMinDistanceMeters;
        _epsilon = options.AimDirectionEpsilon;
    }

    /// <summary>内层意图源（技能按键的边沿语义来源）。</summary>
    public IIntentSource Inner => _inner;

    /// <summary>这一帧是否算出了有效的鼠标世界点（headless / 射线未命中为 false）。</summary>
    public bool HasWorldPoint => _hasWorldPoint;

    /// <summary>最近一次的有效鼠标世界点（米）。<see cref="HasWorldPoint"/> 为 false 时是上一帧的旧值。</summary>
    public Vector3 WorldPoint => new Vector3(_worldPoint.X, _worldPoint.Y, _worldPoint.Z);

    /// <summary>最近一次的有效瞄准方向（单位向量，Y = 0）。</summary>
    public Vector3 AimDirection => new Vector3(_aimDirection.X, _aimDirection.Y, _aimDirection.Z);

    /// <summary>
    /// 采集本帧意图：按键来自内层源，瞄准方向来自鼠标世界点。
    /// <para>
    /// <b>为什么需要玩家位置</b>：瞄准方向 = 鼠标世界点 - 玩家位置，它在宿主层算完后以「方向」交给 ECS
    /// （<c>InputIntent</c> 只有方向字段），这样系统层永远不需要知道「玩家在哪」也能用对瞄准。
    /// </para>
    /// </summary>
    /// <param name="actorIndex">意图所属实体（透传给内层源；CSR 源本身与实体无关）。</param>
    /// <param name="playerPosition">玩家位置（世界米）；这是本类唯一需要的外部状态。</param>
    public InputIntent Poll(int actorIndex, Vector3 playerPosition)
    {
        SetPlayerContext(playerPosition);
        return Poll(actorIndex);
    }

    /// <summary>
    /// 设置玩家位置（宿主每帧先调它，再让 ECS 管线轮询 <see cref="Poll(int)"/>）。
    /// <para>
    /// 为什么拆成两步：<see cref="IIntentSource.Poll"/> 只带 <c>actorIndex</c>，
    /// 而本类需要「玩家在哪」才能把世界点翻成方向。宿主的每帧回调顺序天然是
    /// 「先喂位置 → 再推管线」，因此两步式比往接口里加参数更小侵入（接口由 core 拥有，不在本任务范围）。
    /// </para>
    /// </summary>
    /// <param name="playerPosition">玩家位置（世界米）。</param>
    public void SetPlayerContext(Vector3 playerPosition)
    {
        _playerPosition = new NVector3(playerPosition.X, playerPosition.Y, playerPosition.Z);
        SampleWorldPoint();
    }

    /// <summary>最近一次喂入的玩家位置（未喂过时为原点；此时瞄准方向取自内层源/默认 +X）。</summary>
    private NVector3 _playerPosition;

    /// <inheritdoc />
    public InputIntent Poll(int actorId)
    {
        InputIntent inner = _inner.Poll(actorId);

        // 按键来自内层（边沿语义只有一处实现）；瞄准来自本类（世界点 → 方向）。
        // 没有玩家位置 / 没有有效世界点时，沿用上一帧的瞄准方向，而不是写 0。
        NVector3 aim = _aimDirection;
        return new InputIntent(
            inner.PressedMask,
            inner.HeldMask,
            ToFixed(aim.X),
            ToFixed(aim.Y),
            ToFixed(aim.Z));
    }

    /// <summary>重置边沿状态（切角色 / 暂停恢复时调用，否则会吞掉一次按下）。</summary>
    public void Reset()
    {
        if (_inner is PlayerInputIntentSource player)
        {
            player.Reset();
        }
        _hasWorldPoint = false;
        _lastAim = new NVector3(1f, 0f, 0f);
        _aimDirection = _lastAim;
    }

    /// <summary>
    /// 采样一次鼠标世界点：屏幕坐标 → 相机射线 → 与瞄准平面求交 → 方向。
    /// 失败（无窗口 / 无相机 / 射线与平面平行或超出距离）时保持上一帧的状态。
    /// </summary>
    private void SampleWorldPoint()
    {
        Viewport? viewport = _viewportProvider();
        Camera3D? camera = _cameraProvider();
        if (viewport is null || camera is null || !camera.IsInsideTree())
        {
            _hasWorldPoint = false;
            return;
        }

        Vector2 screen = viewport.GetMousePosition();
        Vector3 origin = camera.ProjectRayOrigin(screen);
        Vector3 direction = camera.ProjectRayNormal(screen);

        // 与水平面 y = planeHeight 求交：t = (plane - origin.y) / direction.y。
        // 正交俯视下 direction.y 通常接近 -1，远离 0；平行时（正交 + 恰好水平）没有唯一交点，视为失败。
        float dy = direction.Y;
        if (MathF.Abs(dy) <= RayParallelEpsilon)
        {
            _hasWorldPoint = false;
            return;
        }
        float t = (_planeHeight - origin.Y) / dy;
        if (!(t > 0f) || t > _rayMaxDistance)
        {
            _hasWorldPoint = false;
            return;
        }

        Vector3 hit = origin + direction * t;
        NVector3 world = new NVector3(hit.X, hit.Y, hit.Z);

        // 方向：世界点 - 玩家（水平面）；两者过近时用「屏幕射线方向的水平分量」兜底，
        // 再不行沿用上一帧（绝不给零向量：零向量在技能层会退化成 +Z）。
        NVector3 flat = new NVector3(world.X - _playerPosition.X, 0f, world.Z - _playerPosition.Z);
        if (flat.LengthSquared() <= _minDistance * _minDistance)
        {
            NVector3 screenFlat = new NVector3(direction.X, 0f, direction.Z);
            if (screenFlat.LengthSquared() > _epsilon * _epsilon)
            {
                flat = screenFlat;
            }
            else
            {
                flat = _lastAim;
            }
        }

        if (flat.LengthSquared() > _epsilon * _epsilon)
        {
            flat = NVector3.Normalize(flat);
            _lastAim = flat;
            _aimDirection = flat;
        }

        _worldPoint = world;
        _hasWorldPoint = true;
        WorldPointChanged?.Invoke(hit);
    }

    /// <summary>方向分量 → Q16 定点（与 <see cref="InputIntent"/> 的 int 约定一致）。</summary>
    private static int ToFixed(float value) => (int)MathF.Round(value * FixedOne);

    /// <summary>射线与瞄准平面「平行」的判定阈值（方向分量的绝对值）。</summary>
    private const float RayParallelEpsilon = 0.0001f; // arch-ok: 浮点安全阈值，不是玩法数值
}
