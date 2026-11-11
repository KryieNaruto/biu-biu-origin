using System;
using Godot;
using Game.Spawn.Rooms;

namespace Game.Host;

/// <summary>
/// **「一个中等房间占屏幕 200%」这条验收标准 → 相机参数**的唯一换算处。
/// <para>
/// 用户原话（2026-11-11）：「我希望一个中等房间占屏幕大小 200%」。
/// </para>
/// <para>
/// 这句话只有在三个量都钉死之后才可求解，这里逐条给出：
/// <list type="number">
/// <item><b>中房多大</b>：<c>S</c> 格 × <see cref="BoardGeometry.TileSize"/> 米（默认 4 × 6 = 24 米）。
/// 尺寸本身是数据（<c>DungeonOptions.RoomSide</c>），不是这里算的。</item>
/// <item><b>占屏比的定义</b>：<see cref="RoomScreenFill"/> = 房间在屏幕上占据的跨度 / 屏幕能容纳的跨度，
/// <b>逐轴取最大值</b>（横向、纵向各算一次，取更满的那个）。这样「200%」在两个方向上都不含糊：
/// 横向进屏 200%、纵向进屏也会是同样的数。</item>
/// <item><b>正交相机 <c>Size</c> 的语义</b>：Godot 里它是**纵向可见米数**（<c>KeepAspect = Height</c>）。
/// 俯角 θ 时地面深度 V 米只占 <c>V·sinθ</c> 米屏幕高度，所以「可见的**地面纵深**」=
/// <c>Size / sinθ</c>。这一条是最容易搞错的地方，<see cref="VisibleDepthMeters"/> 把两种口径分开命名。</item>
/// </list>
/// 求解：横向 <c>W / (Size·AR) = F</c>，纵向 <c>W·sinθ / Size = F</c>（用「房间宽在纵向上被压缩后的高度」——一排方砖的顶面不是正方形，取这个量两块方砖纵向就不会互相压住）。
/// 两式同时成立 ⇒ <c>Size = W·sinθ / F</c> 且 <c>AR = 1/sinθ</c>（等比方砖）。
/// </para>
/// <para>
/// <b>方砖在屏幕上是正方形（<see cref="BoardGeometry.ScreenShaper"/>）与「严格 200%」不能同时满足</b>：
/// 前者要求 1 米横向 = 1 米纵向，于是房间宽 24 米进横向 200% 时纵向就只能进 <c>2·sinθ = 173%</c>。
/// 这是几何，不是实现取舍。默认给「方砖正方形」（人物按方砖归一时比例是 1:1，最简单），
/// 并把两轴的实际占屏比都打印出来；要严格 200% 就关掉 <c>TileAspectIsotropic</c>（方砖在屏幕上变成横向长条）。
/// </para>
/// </summary>
public static class BoardFraming
{
    /// <summary>
    /// 相机 <c>Size</c>（= **屏幕纵向**可见米数，正交相机；横向 = Size × aspect）。
    /// <para>
    /// <b>这个数决定「中房占屏 F」</b>，而屏幕不是正方形，所以两轴占屏比一定不相等：
    /// 横向可见 <c>Size·ar</c> 米，纵向可见 <c>Size/sinθ</c> 米地面纵深。
    /// 默认按**横向**定（玩家说「占屏幕大小」最常指的是宽度），于是：
    /// <c>Size = W / (F · ar)</c>，横向占屏正好 <c>F</c>，纵向占屏 <c>W·sinθ / Size</c>。
    /// </para>
    /// <para>
    /// <b>实测（1600×900、4×4 格 24m 中房、俯角 60°、F=2.0）</b>：
    /// 横向 200%、纵向 308%。要让纵向也正好 200%，屏幕宽高比必须是 <c>1/sin60° = 1.155</c>（比 16:9 窄得多），
    /// 或者把 <see cref="OverrideMeters"/> 直接设成 <c>W·sinθ/F = 10.39</c>（此时横向变成 130%）。
    /// 两个方向不能同时满足是几何约束，不是实现取舍。
    /// </para>
    /// </summary>
    /// <param name="board">方砖几何（取方砖边长与俯角）。</param>
    /// <param name="mediumRoomSideCells">中房边长（格）。</param>
    /// <param name="fillRatio">中房占屏比例（2 = 200%，按**屏幕横向**）。</param>
    /// <param name="aspect">屏幕宽/高。</param>
    /// <param name="isotropicTiles">true = 方砖在屏幕上等比（按横向定 Size 后纵向被 1/(ars) 拉长）。</param>
    /// <param name="overrideMeters">&gt; 0 时直接采用（人工调试用）。</param>
    public static float CameraSize(BoardGeometry board, int mediumRoomSideCells, float fillRatio, float aspect, bool isotropicTiles, float overrideMeters = 0f)
    {
        if (overrideMeters > 0f)
        {
            return overrideMeters;
        }
        float fill = fillRatio > 0f ? fillRatio : 1f;
        float roomWidth = Math.Max(1, mediumRoomSideCells) * board.TileSize;
        float safeAspect = Math.Max(aspect, ShaperFloor);
        // 横向可见 = Size × ar，要让 roomWidth / (Size·ar) = F ⇒ Size = roomWidth / (F·ar)。
        // 等比方砖（TileAspectIsotropic）时纵向要再除以 sinθ 才能让「1 米横向 = 1 米纵向」——见类注释。
        float size = roomWidth / (fill * safeAspect);
        return isotropicTiles ? size / Math.Max(board.ScreenShaper, ShaperFloor) : size;
    }


    /// <summary>俯角正弦的下限（防止俯角为 0 时除零）；命名避开 <see cref="BoardGeometry.ScreenShaper"/> 的读音。</summary>
    public const float ShaperFloor = 0.01f; // arch-ok: 数值安全阈值（除零保护），非玩法数值

    /// <summary>
    /// 相机可见的**地面纵深**（米）= <c>Size / sinθ</c>。与 <see cref="CameraSize"/> 是两种口径，别混。
    /// </summary>
    /// <param name="board">方砖几何。</param>
    /// <param name="cameraSize">相机 <c>Size</c>。</param>
    public static float VisibleDepthMeters(BoardGeometry board, float cameraSize)
        => cameraSize / Math.Max(board.ScreenShaper, ShaperFloor);

    /// <summary>
    /// 相机可见的**屏幕宽度**（米）= <c>Size × aspect</c>（正交相机 Size 是高度）。
    /// </summary>
    /// <param name="cameraSize">相机 <c>Size</c>。</param>
    /// <param name="aspect">屏幕宽/高。</param>
    public static float VisibleWidthMeters(float cameraSize, float aspect) => cameraSize * Math.Max(aspect, ShaperFloor);

    /// <summary>
    /// 中房在屏幕上占的**横向**比例（房间宽 / 屏宽）。
    /// </summary>
    /// <param name="board">方砖几何。</param>
    /// <param name="mediumRoomSideCells">中房边长（格）。</param>
    /// <param name="cameraSize">相机 <c>Size</c>。</param>
    /// <param name="aspect">屏幕宽/高。</param>
    public static float HorizontalFill(BoardGeometry board, int mediumRoomSideCells, float cameraSize, float aspect)
    {
        float width = VisibleWidthMeters(cameraSize, aspect);
        if (width <= 0f)
        {
            return 0f;
        }
        return (Math.Max(1, mediumRoomSideCells) * board.TileSize) / width;
    }

    /// <summary>
    /// 中房在屏幕上占的**纵向**比例。
    /// <para>
    /// 分子取「房间宽 <c>W</c> 沿深度方向被压扁后的高度」<c>W·sinθ</c>——也就是**一排方砖的纵深**，
    /// 而不是整个房间在地面上的深度 <c>W</c>（那会把墙高也算进去，得到的是「房间前后墙的间距」而不是「一排砖占多高」）。
    /// </para>
    /// </summary>
    /// <param name="board">方砖几何。</param>
    /// <param name="mediumRoomSideCells">中房边长（格）。</param>
    /// <param name="cameraSize">相机 <c>Size</c>。</param>
    public static float VerticalFill(BoardGeometry board, int mediumRoomSideCells, float cameraSize)
    {
        if (!(cameraSize > 0f))
        {
            return 0f;
        }
        float rowDepth = Math.Max(1, mediumRoomSideCells) * board.TileSize * Math.Max(board.ScreenShaper, ShaperFloor);
        return rowDepth / cameraSize;
    }

    /// <summary>
    /// 中房两轴上**更大的那个**占屏比例（「200%」按这个判）。
    /// </summary>
    /// <param name="board">方砖几何。</param>
    /// <param name="mediumRoomSideCells">中房边长（格）。</param>
    /// <param name="cameraSize">相机 <c>Size</c>。</param>
    /// <param name="aspect">屏幕宽/高。</param>
    public static float MaxFill(BoardGeometry board, int mediumRoomSideCells, float cameraSize, float aspect)
        => Math.Max(HorizontalFill(board, mediumRoomSideCells, cameraSize, aspect),
            VerticalFill(board, mediumRoomSideCells, cameraSize));

    /// <summary>
    /// 把相机摆成「正交 + 固定 Size + 居中于 <paramref name="focus"/>」。
    /// </summary>
    /// <param name="camera">相机。</param>
    /// <param name="focus">地面上的注视点（米）。</param>
    /// <param name="board">方砖几何（俯角）。</param>
    /// <param name="cameraSize">纵向可见米数。</param>
    public static void Apply(Camera3D camera, Vector3 focus, BoardGeometry board, float cameraSize)
    {
        if (camera is null)
        {
            throw new ArgumentNullException(nameof(camera));
        }
        camera.Projection = Camera3D.ProjectionType.Orthogonal;
        camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
        camera.Size = Math.Max(cameraSize, MinCameraSizeMeters);
        camera.Position = CameraPosition(focus, board.CameraPitchDegrees, camera.Size);
        camera.LookAt(focus, Vector3.Up);
        camera.Far = Math.Max(camera.Size, MinCameraSizeMeters) * FarPlaneFactor;
        camera.Near = NearPlaneMeters;
        camera.Current = true;
    }

    /// <summary>
    /// 正交相机的位置：沿俯角反向退一段距离。离多远都不影响成像（正交），只要不穿地。
    /// </summary>
    /// <param name="focus">注视点。</param>
    /// <param name="pitchDegrees">俯角。</param>
    /// <param name="cameraSize">纵向可见米数（决定后退距离与远平面）。</param>
    public static Vector3 CameraPosition(Vector3 focus, float pitchDegrees, float cameraSize)
    {
        float pitch = Mathf.DegToRad(Mathf.Clamp(pitchDegrees, BoardGeometry.PitchMinDegrees, BoardGeometry.PitchMaxDegrees));
        Vector3 forward = new(0f, -Mathf.Sin(pitch), Mathf.Cos(pitch));
        float distance = Math.Max(cameraSize, MinCameraSizeMeters) * CameraBackOffFactor;
        return focus - (forward.Normalized() * distance);
    }

    /// <summary>相机最小 Size（米）：防止配置把 Size 调成 0 导致全黑。</summary>
    public const float MinCameraSizeMeters = 0.5f; // arch-ok: 数值安全阈值（防零保护），非玩法数值

    /// <summary>相机后退距离 / Size（正交相机只要够远、不穿地即可）。</summary>
    public const float CameraBackOffFactor = 2f; // arch-ok: 几何推导常量（正交相机后退相对可见高度），非玩法数值

    /// <summary>远平面 = Size × 该系数（保证整层都在裁剪范围内）。</summary>
    public const float FarPlaneFactor = 128f; // arch-ok: 几何推导常量（远平面相对可见高度），非玩法数值

    /// <summary>近平面（米）。</summary>
    public const float NearPlaneMeters = 0.05f; // arch-ok: 引擎裁剪默认值，非玩法数值

    /// <summary>占屏比例自检的容差（±0.01，即 1 个百分点）。</summary>
    public const float FillTolerance = 0.01f; // arch-ok: 自检阈值（浮点容差），非玩法数值

    /// <summary>一句话描述（日志/自检打印）。</summary>
    /// <param name="board">方砖几何。</param>
    /// <param name="sideCells">中房边长（格）。</param>
    /// <param name="cameraSize">相机 Size。</param>
    /// <param name="aspect">屏幕宽/高。</param>
    public static string Describe(BoardGeometry board, int sideCells, float cameraSize, float aspect)
    {
        float h = HorizontalFill(board, sideCells, cameraSize, aspect);
        float v = VerticalFill(board, sideCells, cameraSize);
        return "中房 " + sideCells.ToString(System.Globalization.CultureInfo.InvariantCulture) + "格="
               + (sideCells * board.TileSize).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
               + " | 可见 宽" + VisibleWidthMeters(cameraSize, aspect).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
               + " 高" + cameraSize.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
               + " 纵深" + VisibleDepthMeters(board, cameraSize).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "m"
               + " | 占屏 横" + (h * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
               + " 纵" + (v * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}
