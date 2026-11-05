using Godot;

namespace Game.Host;

/// <summary>
/// 表现层（视图 / 镜头 / 输入 / 准星）的**全部可调参数**：一帧的表现侧预算、角色外观、相机跟随与准星画法。
/// <para>
/// <b>为什么是 Resource 而不是写在类里</b>（红线 2 / 规则4）：本文件里的每个数都属<u>表现参数</u>
/// （镜头俯仰、跟随平滑、准星粗细、配色），它们既不是玩法数值也不该硬编码在绘制代码里——
/// 提升为 <see cref="Resource"/> 后：① Inspector 里可视化调；② <c>.tres</c> 即数据文件，改数值 = 改资源；
/// ③ 镜头包的字段（distance/pitch/yaw/followLerp/orthoHeight）由 Lead 的 <c>CharacterSession</c> 在
/// 装配期<u>覆盖</u>本资源的默认值（Excel 是镜头数值的真源，本文件只是「表没给时的保底与其余表现参数」）。
/// </para>
/// <para>
/// <b>为什么镜头包会覆盖它</b>：契约 §5 规定 <c>CharacterCameraPack</c>（Excel 一行）携带
/// distance / pitch / yaw / followLerp / cursorScreenHeightRatio，那是「玩家怎么玩」的一部分；
/// 本资源的相机段是「没有任何镜头包时」的默认机身。二者合并顺序见
/// <see cref="BuildCameraSettings"/>，覆盖后 <c>CharacterSession</c> 仍可逐项微调。
/// </para>
/// <para>
/// <b>纪律</b>：除 <c>scripts/host/**</c> 外，任何层都不得引用本类——系统层只通过构造注入的
/// 普通 C# 值对象（<see cref="CameraRigSettings"/> / <see cref="ActorViewStyle"/> 等）拿数，保持零引擎依赖。
/// </para>
/// </summary>
[GlobalClass]
public partial class GameViewOptions : Resource
{
    // =========================================================================
    // 1. 相机默认机身（会被 CharacterCameraPack 覆盖；见 BuildCameraSettings）
    // =========================================================================

    /// <summary>
    /// 相机到聚焦点的**水平**距离（米）。正交投影下它不影响取景大小，只影响「抬高多少」以避免
    /// 角色被近裁剪面切掉、以及阴影/光照的观察角度。
    /// </summary>
    [ExportGroup("相机 · 默认机身")]
    [Export(PropertyHint.Range, "1,80,0.1")]
    public float CameraDistance { get; set; } = CameraDistanceDefault;

    /// <summary>俯仰角（度）。90 = 正俯视（看不到侧立面）；60 = 常见俯视射击角度。</summary>
    [Export(PropertyHint.Range, "10,90,0.5")]
    public float CameraPitchDegrees { get; set; } = CameraPitchDegreesDefault;

    /// <summary>偏航角（度）。0 = 相机在 -Z 侧看向 +Z（玩家往上走时画面「朝上」）。</summary>
    [Export(PropertyHint.Range, "-180,180,1")]
    public float CameraYawDegrees { get; set; } = CameraYawDegreesDefault;

    /// <summary>
    /// 跟随平滑（指数收敛速率，每秒）。值越大越「硬」，0 = 瞬移（硬锁）。
    /// 实现为 <c>1 - exp(-rate * dt)</c>，因此与帧率无关（144Hz 与 60Hz 手感一致）。
    /// </summary>
    [Export(PropertyHint.Range, "0,40,0.1")]
    public float CameraFollowLerp { get; set; } = CameraFollowLerpDefault;

    /// <summary>
    /// 正交相机的**可见高度**（米）。这是正交投影下唯一决定「看多大范围」的参数。
    /// 宽高比由视口决定（可见宽度 = 可见高度 × 宽高比），因此本值只锁「上下能看到多少米」。
    /// </summary>
    [Export(PropertyHint.Range, "1,120,0.1")]
    public float CameraOrthoHeight { get; set; } = CameraOrthoHeightDefault;

    /// <summary>
    /// 画面「前瞻」：相机沿瞄准方向前移的比例 × 可见高度。0 = 不前瞻（角色永远居中）。
    /// 轻量前瞻能让准星远离边缘、射击手感更好，但过大会让角色跑出画面中心产生晕眩。
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float CameraLookAheadRatio { get; set; } = CameraLookAheadRatioDefault;

    /// <summary>前瞻的最大位移（米）：防止准星贴边时相机甩得太远。</summary>
    [Export(PropertyHint.Range, "0,20,0.1")]
    public float CameraLookAheadMaxMeters { get; set; } = CameraLookAheadMaxMetersDefault;

    /// <summary>跟随的竖直轴（Y）：正交俯视下地面是平面，通常不跟随高度，免得上下坡时画面抖。</summary>
    [Export]
    public bool CameraFollowHeight { get; set; } = true;

    /// <summary>
    /// 跟随边界（关卡矩形，米）：相机聚焦点被夹在矩形内，防止跑到房间外的虚空。
    /// 矩形由宿主（<c>CharacterSession</c>）按地牢包围盒设置；<see cref="CameraBoundsEnabled"/> 关掉则不限。
    /// </summary>
    [ExportGroup("相机 · 边界")]
    [Export]
    public bool CameraBoundsEnabled { get; set; }

    /// <summary>边界矩形最小角（X/Z，米）。</summary>
    [Export]
    public Vector2 CameraBoundsMin { get; set; } = Vector2.Zero;

    /// <summary>边界矩形最大角（X/Z，米）。</summary>
    [Export]
    public Vector2 CameraBoundsMax { get; set; } = Vector2.Zero;

    /// <summary>
    /// 边界内缩（米）：聚焦点离边界至少这么远。用于不让相机「半个屏幕看出界」——
    /// 由于是夹取聚焦点而不是视锥，内缩应 ≥ 可见尺寸的一半才能完全避免出界，故默认按 0（由宿主按需给）。
    /// </summary>
    [Export(PropertyHint.Range, "0,60,0.1")]
    public float CameraBoundsPadding { get; set; }

    // =========================================================================
    // 2. 相机通用（不来自镜头包）
    // =========================================================================

    /// <summary>近裁剪面（米）。太小会让贴近的东西被切，太大会让墙近处闪烁。</summary>
    [ExportGroup("相机 · 通用")]
    [Export(PropertyHint.Range, "0.01,2,0.01")]
    public float CameraNearPlaneMeters { get; set; } = CameraNearPlaneMetersDefault;

    /// <summary>远裁剪面（米）。正交下它决定阴影投射距离，给足即可。</summary>
    [Export(PropertyHint.Range, "10,2000,1")]
    public float CameraFarPlaneMeters { get; set; } = CameraFarPlaneMetersDefault;

    // =========================================================================
    // 3. 角色白模外观（VisualPack 的 ModelPath 为空 / 加载失败时的表现）
    // =========================================================================

    /// <summary>
    /// 角色白模的**身体**网格种类。v1 只有「方块 / 胶囊」两种，配合外观包的高饱和配色即可区分变体。
    /// </summary>
    [ExportGroup("角色外观")]
    [Export]
    public ActorBodyShape DefaultBodyShape { get; set; } = ActorBodyShape.Capsule;

    /// <summary>身体高度（米）。角色的视觉尺寸，不参与判定（判定走 Hurtbox）。</summary>
    [Export(PropertyHint.Range, "0.2,6,0.05")]
    public float ActorBodyHeight { get; set; } = ActorBodyHeightDefault;

    /// <summary>身体半径（米）。方块用「边长 = 半径 × 2」，胶囊用「直径 = 半径 × 2」。</summary>
    [Export(PropertyHint.Range, "0.1,3,0.01")]
    public float ActorBodyRadius { get; set; } = ActorBodyRadiusDefault;

    /// <summary>身体底部离地高度（米）：让白模「站在」地面上而不是插进去。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ActorGroundOffset { get; set; } = ActorGroundOffsetDefault;

    /// <summary>朝向指示（鼻子）的长度比例 × 身体半径。0 = 不显示朝向（看不出角色朝哪）。</summary>
    [Export(PropertyHint.Range, "0,3,0.01")]
    public float ActorNoseRatio { get; set; } = ActorNoseRatioDefault;

    /// <summary>朝向指示（鼻子）的粗细比例 × 身体半径。</summary>
    [Export(PropertyHint.Range, "0.02,1,0.01")]
    public float ActorNoseThicknessRatio { get; set; } = ActorNoseThicknessRatioDefault;

    /// <summary>
    /// 白模表面的自发光强度（0 = 纯漫反射）。俯视射击里给一点自发光能让高饱和配色在阴影中仍然可读，
    /// 与地牢材质（<c>DungeonMaterialSet</c>）的处理一致。
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ActorEmissionEnergy { get; set; } = ActorEmissionEnergyDefault;

    /// <summary>白模表面粗糙度（1 = 全漫反射，0 = 镜面）。玩具塑料感 = 偏高的粗糙度 + 一点自发光。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ActorRoughness { get; set; } = ActorRoughnessDefault;

    /// <summary>
    /// 外观包 <c>ColorHex</c> 为空或非法时用的**配色档位表**（每个 <c>PaletteSlot</c> 一个颜色）。
    /// 为什么在代码里留一张表：外观包的 <c>ColorHex</c> 是首选真源，本表是「外观包没给颜色」时的保底，
    /// 保证任何变体都有可辨识的高饱和色，而不是一片灰色。
    /// </summary>
    [ExportGroup("角色外观 · 配色档位")]
    [Export]
    public Color Palette0 { get; set; } = DefaultPalette0;

    /// <summary>配色档位 1。</summary>
    [Export]
    public Color Palette1 { get; set; } = DefaultPalette1;

    /// <summary>配色档位 2。</summary>
    [Export]
    public Color Palette2 { get; set; } = DefaultPalette2;

    /// <summary>配色档位 3。</summary>
    [Export]
    public Color Palette3 { get; set; } = DefaultPalette3;

    /// <summary>配色档位 4。</summary>
    [Export]
    public Color Palette4 { get; set; } = DefaultPalette4;

    /// <summary>配色档位 5。</summary>
    [Export]
    public Color Palette5 { get; set; } = DefaultPalette5;

    // =========================================================================
    // 4. 准星
    // =========================================================================

    /// <summary>准星环的外半径（米，世界空间）。显示在鼠标与地面的交点处。</summary>
    [ExportGroup("准星")]
    [Export(PropertyHint.Range, "0.02,3,0.01")]
    public float CrosshairRadius { get; set; } = CrosshairRadiusDefault;

    /// <summary>准星环的线宽（米）。</summary>
    [Export(PropertyHint.Range, "0.005,0.5,0.005")]
    public float CrosshairThickness { get; set; } = CrosshairThicknessDefault;

    /// <summary>准星离地高度（米）：略微抬起，避免与地面 z-fighting。</summary>
    [Export(PropertyHint.Range, "0,1,0.005")]
    public float CrosshairHeight { get; set; } = CrosshairHeightDefault;

    /// <summary>准星环的分段数（越大越圆，成本线性上升；俯视小环 24~40 足够）。</summary>
    [Export(PropertyHint.Range, "8,96,1")]
    public int CrosshairSegments { get; set; } = CrosshairSegmentsDefault;

    /// <summary>准星颜色（高饱和亮色：在深色地面与亮色地面上都能读出）。</summary>
    [Export]
    public Color CrosshairColor { get; set; } = DefaultCrosshairColor;

    /// <summary>准星自发光强度（0 = 纯漫反射）。自发光让准星在任何光照下都保持亮度。</summary>
    [Export(PropertyHint.Range, "0,4,0.01")]
    public float CrosshairEmissionEnergy { get; set; } = CrosshairEmissionEnergyDefault;

    /// <summary>准星在屏幕上平滑跟随鼠标的速率（每秒，0 = 硬跟）。只在无物理射线时用于插值显示点。</summary>
    [Export(PropertyHint.Range, "0,60,0.1")]
    public float CrosshairFollowLerp { get; set; } = CrosshairFollowLerpDefault;

    /// <summary>准星可见性开关（截图/回归时可以关掉）。</summary>
    [Export]
    public bool CrosshairVisible { get; set; } = true;

    // =========================================================================
    // 5. 输入（鼠标 → 瞄准意图）
    // =========================================================================

    /// <summary>
    /// 瞄准平面高度（米）：鼠标射线与该水平面的交点就是瞄准世界点。
    /// 由于是正交俯视，射线几乎垂直，交点对高度不敏感；取角色胸口高度最稳（穿墙/无碰撞体时也不会跑飞）。
    /// </summary>
    [ExportGroup("输入 · 鼠标瞄准")]
    [Export(PropertyHint.Range, "-10,20,0.01")]
    public float AimPlaneHeight { get; set; } = AimPlaneHeightDefault;

    /// <summary>
    /// 鼠标射线与瞄准平面的求交最大距离（米）。超出则视为「鼠标没指向地面」，本次不更新瞄准。
    /// 正交相机下这个值只需要略大于相机距离 + 场景深度。
    /// </summary>
    [Export(PropertyHint.Range, "1,1000,1")]
    public float AimRayMaxDistance { get; set; } = AimRayMaxDistanceDefault;

    /// <summary>
    /// 鼠标离角色太近（米）时，改用「鼠标屏幕方向」而不是「鼠标世界点 - 角色位置」来求朝向：
    /// 鼠标压在角色身上时，世界点减角色位置会退化成零向量，朝向会乱跳。
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.01")]
    public float AimMinDistanceMeters { get; set; } = AimMinDistanceMetersDefault;

    /// <summary>
    /// 鼠标世界点相对「角色位置」做了这一半径的最小推移后仍过近时，沿用上一帧朝向。
    /// 与 <see cref="AimMinDistanceMeters"/> 配合，保证 never 出现零长度朝向（技能系统会归一化并退化）。
    /// </summary>
    [Export(PropertyHint.Range, "0,2,0.001")]
    public float AimDirectionEpsilon { get; set; } = AimDirectionEpsilonDefault;

    // =========================================================================
    // 6. HUD 面板
    // =========================================================================

    /// <summary>HUD 图层的层号（<c>CanvasLayer.Layer</c>）。数字大 = 画在更前面。</summary>
    [ExportGroup("HUD")]
    [Export(PropertyHint.Range, "-128,128,1")]
    public int HudCanvasLayer { get; set; } = HudCanvasLayerDefault;

    /// <summary>
    /// 是否隐藏操作系统的鼠标指针（准星替代它）。headless / 截图模式下引擎会忽略，不会报错。
    /// </summary>
    [Export]
    public bool HideOsCursor { get; set; } = true;

    // =========================================================================
    // 派生 / 校验
    // =========================================================================

    /// <summary>
    /// 把「镜头包」的五个字段（由 Lead 的 <c>CharacterSession</c> 从 Excel 行读成普通 C# 值）
    /// 合并成一份**纯值对象**镜头设置，交给 <c>TopDownCameraRig</c>。
    /// <para>
    /// <b>合并规则</b>：镜头包的字段若为「有效值」则覆盖本资源的默认机身，否则沿用本资源
    /// （Excel 允许留空 = 用默认，避免策划漏填导致相机跑到原点）。
    /// 前瞻比例 / 最大位移 / 边界 / 裁剪面不来自镜头包，直接取自本资源。
    /// </para>
    /// <para>
    /// <b>为什么返回普通 struct 而不是把自己传过去</b>：<c>TopDownCameraRig</c> 不认识 <c>Game.Data</c>、
    /// 也不该为一个 Resource 的字段顺序背书；传入值对象让它可被裸测（不建节点也能算相机位置）。
    /// </para>
    /// </summary>
    /// <param name="imported">镜头包的值（全部为 <c>NaN</c> / 非正数表示「表里没给」）。</param>
    /// <param name="orthoHeightOverride">镜头包附带的可见高度（&lt;= 0 = 用本资源默认）。</param>
    public CameraRigSettings BuildCameraSettings(in CameraPackSettings imported, float orthoHeightOverride)
    {
        float distance = imported.Distance > 0f ? imported.Distance : CameraDistance;
        float pitch = imported.PitchDegrees > 0f ? imported.PitchDegrees : CameraPitchDegrees;
        float yaw = float.IsNaN(imported.YawDegrees) ? CameraYawDegrees : imported.YawDegrees;
        float follow = imported.FollowLerp >= 0f ? imported.FollowLerp : CameraFollowLerp;
        float ortho = orthoHeightOverride > 0f ? orthoHeightOverride : CameraOrthoHeight;

        return new CameraRigSettings(
            distance,
            pitch,
            yaw,
            follow,
            ortho,
            CameraLookAheadRatio,
            CameraLookAheadMaxMeters,
            CameraFollowHeight,
            CameraNearPlaneMeters,
            CameraFarPlaneMeters,
            CameraBoundsEnabled,
            CameraBoundsMin,
            CameraBoundsMax,
            CameraBoundsPadding);
    }

    /// <summary>
    /// 按 <see cref="PaletteSlot"/> 取保底配色档位（0..5 循环；越界回落到档位 0）。
    /// <para>为什么循环而不是夹取：外观包多给几种配色时，档位号可以随意大，不会因为越界变成透明。</para>
    /// </summary>
    /// <param name="paletteSlot">配色档位号（负数 = 默认档位 0）。</param>
    public Color PaletteColor(int paletteSlot)
    {
        int slot = paletteSlot < 0 ? 0 : paletteSlot % PaletteSlotCount;
        return slot switch
        {
            1 => Palette1,
            2 => Palette2,
            3 => Palette3,
            4 => Palette4,
            5 => Palette5,
            _ => Palette0,
        };
    }

    /// <summary>
    /// 参数自检：把「配置错」在 <c>_Ready</c> 当场变成一条明确的中文错误，
    /// 而不是让它变成运行几百帧后的一次除零 / 相机钻到地下 / 准星看不见。
    /// </summary>
    public void Validate()
    {
        if (CameraDistance <= 0f)
        {
            throw new System.InvalidOperationException(
                string.Concat(nameof(GameViewOptions), ".", nameof(CameraDistance), " 必须大于 0。"));
        }
        if (CameraPitchDegrees <= 0f || CameraPitchDegrees > AngleLimitDegrees)
        {
            throw new System.InvalidOperationException(string.Concat(
                nameof(GameViewOptions), ".", nameof(CameraPitchDegrees), " 必须在 (0, ",
                AngleLimitDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture), "] 度之间。"));
        }
        if (CameraFollowLerp < 0f)
        {
            throw new System.InvalidOperationException(
                string.Concat(nameof(GameViewOptions), ".", nameof(CameraFollowLerp), " 不能为负。"));
        }
        if (CameraOrthoHeight <= 0f)
        {
            throw new System.InvalidOperationException(
                string.Concat(nameof(GameViewOptions), ".", nameof(CameraOrthoHeight), " 必须大于 0。"));
        }
        if (ActorBodyRadius <= 0f || ActorBodyHeight <= 0f)
        {
            throw new System.InvalidOperationException(
                string.Concat(nameof(GameViewOptions), " 的角色白模尺寸必须大于 0。"));
        }
        if (CrosshairSegments < MinCrosshairSegments)
        {
            throw new System.InvalidOperationException(string.Concat(
                nameof(GameViewOptions), ".", nameof(CrosshairSegments), " 至少为 ",
                MinCrosshairSegments.ToString(System.Globalization.CultureInfo.InvariantCulture), "。"));
        }
        if (CameraBoundsEnabled && (CameraBoundsMax.X <= CameraBoundsMin.X || CameraBoundsMax.Y <= CameraBoundsMin.Y))
        {
            throw new System.InvalidOperationException(
                string.Concat(nameof(GameViewOptions), " 的相机边界为空：Max 必须大于 Min。"));
        }
    }

    // =========================================================================
    // 结构性常量与默认值（集中一处，便于 Excel 落地后统一替换）
    // =========================================================================

    /// <summary>配色档位数量（与 <see cref="PaletteColor"/> 的表长一致）。</summary>
    public const int PaletteSlotCount = 6; // arch-ok: 结构性常量（配色表长），不是玩法数值

    /// <summary>角度上下限（度）。</summary>
    private const float AngleLimitDegrees = 90f; // arch-ok: 角度定义域（数学常量）

    /// <summary>准星环最小分段数（再少就不是圆了）。</summary>
    private const int MinCrosshairSegments = 3; // arch-ok: 几何下限（三角形才成环）

    /// <summary>相机水平距离默认值（米）。</summary>
    public const float CameraDistanceDefault = 18f; // arch-ok: 表现参数默认值，由 .tres / 镜头包覆盖

    /// <summary>相机俯仰默认值（度）。</summary>
    public const float CameraPitchDegreesDefault = 60f; // arch-ok: 表现参数默认值，由 .tres / 镜头包覆盖

    /// <summary>相机偏航默认值（度）。</summary>
    public const float CameraYawDegreesDefault = 0f;

    /// <summary>相机跟随平滑默认值（每秒）。</summary>
    public const float CameraFollowLerpDefault = 10f; // arch-ok: 表现参数默认值，由 .tres / 镜头包覆盖

    /// <summary>正交可见高度默认值（米）。</summary>
    public const float CameraOrthoHeightDefault = 14f; // arch-ok: 表现参数默认值，由 .tres / 镜头包覆盖

    /// <summary>前瞻比例默认值。</summary>
    public const float CameraLookAheadRatioDefault = 0.18f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>前瞻最大位移默认值（米）。</summary>
    public const float CameraLookAheadMaxMetersDefault = 1.6f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>近裁剪面默认值（米）。</summary>
    public const float CameraNearPlaneMetersDefault = 0.1f; // arch-ok: 相机参数默认值，由 .tres 覆盖

    /// <summary>远裁剪面默认值（米）。</summary>
    public const float CameraFarPlaneMetersDefault = 400f; // arch-ok: 相机参数默认值，由 .tres 覆盖

    /// <summary>角色身体高度默认值（米）。</summary>
    public const float ActorBodyHeightDefault = 1.1f; // arch-ok: 表现参数默认值，由 .tres / 外观包覆盖

    /// <summary>角色身体半径默认值（米）。</summary>
    public const float ActorBodyRadiusDefault = 0.35f; // arch-ok: 表现参数默认值，由 .tres / 外观包覆盖

    /// <summary>角色贴地间隙默认值（米）。</summary>
    public const float ActorGroundOffsetDefault = 0f;

    /// <summary>朝向指示长度比例默认值。</summary>
    public const float ActorNoseRatioDefault = 0.8f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>朝向指示粗细比例默认值。</summary>
    public const float ActorNoseThicknessRatioDefault = 0.45f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>角色自发光默认值。</summary>
    public const float ActorEmissionEnergyDefault = 0.25f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>角色粗糙度默认值。</summary>
    public const float ActorRoughnessDefault = 0.6f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>配色档位 0 默认值（玩具红）。</summary>
    public static readonly Color DefaultPalette0 = new Color(0.95f, 0.29f, 0.31f, 1f); // arch-ok: 表现配色默认值，由 .tres / 外观包 ColorHex 覆盖

    /// <summary>配色档位 1 默认值（玩具蓝）。</summary>
    public static readonly Color DefaultPalette1 = new Color(0.29f, 0.6f, 0.97f, 1f); // arch-ok: 表现配色默认值，由 .tres / 外观包 ColorHex 覆盖

    /// <summary>配色档位 2 默认值（玩具绿）。</summary>
    public static readonly Color DefaultPalette2 = new Color(0.44f, 0.83f, 0.4f, 1f); // arch-ok: 表现配色默认值，由 .tres / 外观包 ColorHex 覆盖

    /// <summary>配色档位 3 默认值（糖橙）。</summary>
    public static readonly Color DefaultPalette3 = new Color(1f, 0.6f, 0.2f, 1f); // arch-ok: 表现配色默认值，由 .tres / 外观包 ColorHex 覆盖

    /// <summary>配色档位 4 默认值（葡萄紫）。</summary>
    public static readonly Color DefaultPalette4 = new Color(0.66f, 0.4f, 0.96f, 1f); // arch-ok: 表现配色默认值，由 .tres / 外观包 ColorHex 覆盖

    /// <summary>配色档位 5 默认值（柠檬黄）。</summary>
    public static readonly Color DefaultPalette5 = new Color(1f, 0.85f, 0.42f, 1f); // arch-ok: 表现配色默认值，由 .tres / 外观包 ColorHex 覆盖

    /// <summary>准星环半径默认值（米）。</summary>
    public const float CrosshairRadiusDefault = 0.32f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>准星环线宽默认值（米）。</summary>
    public const float CrosshairThicknessDefault = 0.055f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>准星离地高度默认值（米）。</summary>
    public const float CrosshairHeightDefault = 0.03f; // arch-ok: 表现参数默认值（避免 z-fighting），由 .tres 覆盖

    /// <summary>准星分段数默认值。</summary>
    public const int CrosshairSegmentsDefault = 32; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>准星颜色默认值（亮青：与玩具配色互补，任何地面上都醒目）。</summary>
    public static readonly Color DefaultCrosshairColor = new Color(0.35f, 0.98f, 1f, 1f); // arch-ok: 表现配色默认值，由 .tres 覆盖

    /// <summary>准星自发光默认值。</summary>
    public const float CrosshairEmissionEnergyDefault = 1.4f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>准星显示平滑默认值（每秒）。</summary>
    public const float CrosshairFollowLerpDefault = 32f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>瞄准平面高度默认值（米，角色胸口附近）。</summary>
    public const float AimPlaneHeightDefault = 0.6f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>瞄准射线最大距离默认值（米）。</summary>
    public const float AimRayMaxDistanceDefault = 200f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>瞄准最小距离默认值（米）。</summary>
    public const float AimMinDistanceMetersDefault = 0.15f; // arch-ok: 表现参数默认值，由 .tres 覆盖

    /// <summary>朝向退化阈值默认值（米）。</summary>
    public const float AimDirectionEpsilonDefault = 0.001f; // arch-ok: 浮点安全阈值（不是玩法数值）

    /// <summary>HUD 图层默认层号。</summary>
    public const int HudCanvasLayerDefault = 10; // arch-ok: 结构性常量（图层顺序），由 .tres 覆盖
}

/// <summary>
/// 角色身体白模的形状（<see cref="Resource"/> 的 <see cref="ExportAttribute"/> 友好枚举）。
/// <para>为什么是枚举而不是各自一个预制体：v1 只有两种占位外观，用一个枚举 + 两个网格生成函数最省；
/// 正式素材接入后由外观包的 <c>ModelPath</c> 接管，本枚举只作为「没有模型时」的保底。</para>
/// </summary>
public enum ActorBodyShape
{
    /// <summary>方块（Toybox 的「积木」感，最适合正交俯视读朝向）。</summary>
    Box = 0,

    /// <summary>胶囊（更适合第三人称，但俯视下更圆润）。</summary>
    Capsule = 1,
}

/// <summary>
/// 镜头包 → 镜头设置的**纯值中转**（不引用 <c>Game.Data</c>，由 Lead 在 <c>CharacterSession</c> 里填充）。
/// <para>
/// 与 <c>Game.Data.CharacterCameraPack</c> 的对应关系：<see cref="Distance"/> ← <c>Distance</c>、
/// <see cref="PitchDegrees"/> ← <c>PitchDeg</c>、<see cref="YawDegrees"/> ← <c>YawDeg</c>、
/// <see cref="FollowLerp"/> ← <c>FollowLerp</c>、<see cref="CursorScreenHeightRatio"/> ←
/// <c>CursorScreenHeightRatio</c>。后三表没给时留 <c>NaN</c> 或负数，表示「用默认」。
/// </para>
/// <para>
/// <b>为什么要这层中转</b>：<c>scripts/host/view/**</c> 不认识 <c>Game.Data</c>（保持视图层可裸测、
/// 不把 Excel 结构渗进渲染代码）；Lead 在装配期把数据行读成这个 struct，视图层只认它。
/// </para>
/// </summary>
public readonly struct CameraPackSettings
{
    /// <summary>镜头距离（米）；&lt;= 0 表示表里没给。</summary>
    public readonly float Distance;

    /// <summary>俯仰角（度）；&lt;= 0 表示表里没给。</summary>
    public readonly float PitchDegrees;

    /// <summary>偏航角（度）；<c>NaN</c> 表示表里没给。</summary>
    public readonly float YawDegrees;

    /// <summary>跟随平滑（每秒）；&lt; 0 表示表里没给。</summary>
    public readonly float FollowLerp;

    /// <summary>光标屏幕高度比例（0..1，镜头包自带；本类实现不消费，留给 Lead 决定是否用于准星）。</summary>
    public readonly float CursorScreenHeightRatio;

    /// <summary>构造镜头包中转值。</summary>
    public CameraPackSettings(float distance, float pitchDegrees, float yawDegrees, float followLerp, float cursorScreenHeightRatio)
    {
        Distance = distance;
        PitchDegrees = pitchDegrees;
        YawDegrees = yawDegrees;
        FollowLerp = followLerp;
        CursorScreenHeightRatio = cursorScreenHeightRatio;
    }

    /// <summary>「表里什么都没给」的空值：全部用默认机身。</summary>
    public static CameraPackSettings Empty => new(float.NaN, float.NaN, float.NaN, -1f, 0f);
}

/// <summary>
/// 相机机身的**纯值设置**（<c>TopDownCameraRig</c> 的唯一输入，可由 <see cref="GameViewOptions"/> 派生，
/// 也可在测试里直接构造）。不引用任何引擎之外的层。
/// </summary>
public readonly struct CameraRigSettings
{
    /// <summary>相机到聚焦点的水平距离（米）。</summary>
    public readonly float Distance;

    /// <summary>俯仰角（度，10..90）。</summary>
    public readonly float PitchDegrees;

    /// <summary>偏航角（度）。</summary>
    public readonly float YawDegrees;

    /// <summary>跟随平滑速率（每秒，0 = 硬锁）。</summary>
    public readonly float FollowLerp;

    /// <summary>正交可见高度（米）。</summary>
    public readonly float OrthoHeight;

    /// <summary>前瞻比例（0 = 不前移）。</summary>
    public readonly float LookAheadRatio;

    /// <summary>前瞻最大位移（米）。</summary>
    public readonly float LookAheadMaxMeters;

    /// <summary>是否跟随 Y。</summary>
    public readonly bool FollowHeight;

    /// <summary>近裁剪面（米）。</summary>
    public readonly float NearMeters;

    /// <summary>远裁剪面（米）。</summary>
    public readonly float FarMeters;

    /// <summary>是否启用跟随边界。</summary>
    public readonly bool BoundsEnabled;

    /// <summary>边界最小角（X/Z）。</summary>
    public readonly Vector2 BoundsMin;

    /// <summary>边界最大角（X/Z）。</summary>
    public readonly Vector2 BoundsMax;

    /// <summary>边界内缩（米）。</summary>
    public readonly float BoundsPadding;

    /// <summary>构造镜头设置。</summary>
    public CameraRigSettings(
        float distance,
        float pitchDegrees,
        float yawDegrees,
        float followLerp,
        float orthoHeight,
        float lookAheadRatio,
        float lookAheadMaxMeters,
        bool followHeight,
        float nearMeters,
        float farMeters,
        bool boundsEnabled,
        Vector2 boundsMin,
        Vector2 boundsMax,
        float boundsPadding)
    {
        Distance = distance;
        PitchDegrees = pitchDegrees;
        YawDegrees = yawDegrees;
        FollowLerp = followLerp;
        OrthoHeight = orthoHeight;
        LookAheadRatio = lookAheadRatio;
        LookAheadMaxMeters = lookAheadMaxMeters;
        FollowHeight = followHeight;
        NearMeters = nearMeters;
        FarMeters = farMeters;
        BoundsEnabled = boundsEnabled;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        BoundsPadding = boundsPadding;
    }

    /// <summary>用 <see cref="GameViewOptions"/> 的默认机身构造（等同「表里没给镜头包」）。</summary>
    /// <param name="options">表现层参数。</param>
    public static CameraRigSettings FromOptions(GameViewOptions options)
    {
        return options.BuildCameraSettings(CameraPackSettings.Empty, 0f);
    }
}
