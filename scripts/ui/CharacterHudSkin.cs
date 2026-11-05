using Godot;

namespace Game.UI;

/// <summary>
/// 角色 HUD 的**皮肤**（`[GlobalClass] Resource`）：所有颜色、字号、尺寸、动画时长都在这里，
/// 运行期绘制代码 <see cref="CharacterHudDrawer"/> 只读它、不写死任何字面量（规则 4）。
/// <para>
/// 为什么不直接塞进 Drawer 的 [Export]：皮肤要能在运行时整包换（不同角色/不同章节换配色 = 换一个 `.tres`），
/// 而 Drawer（Control）还承担「拿快照 / 推动画」的职责。两者分开，皮肤可以跨 Control 复用。
/// </para>
/// <para>
/// 配色思路（Stylized Low-Poly Toybox / Chunky Sticker UI，见 `docs/art-style-and-animation-guide.md` §五）：
/// 深蓝紫的「贴纸底板」+ 4px 暖白描边 + 硬边深色投影（不是高斯模糊），条与技能格用高饱和色块，
/// 强调色只出现在变体标签和暴击率上 —— 一个面板里高饱和色不超过 3 个，玩家才不会觉得在逛菜单。
/// </para>
/// </summary>
[GlobalClass]
public partial class CharacterHudSkin : Resource
{
    // =========================================================================
    // 0. 字体
    // =========================================================================

    /// <summary>主字体（建议圆体 / 厚体，如 Fredoka、Nunito 的圆体版本）。空 = 用引擎默认字体。</summary>
    [ExportGroup("字体")]
    [Export]
    public Font? Font { get; set; }

    // =========================================================================
    // 0.5 分辨率自适应（布局尺寸全部以 ReferenceHeight 为基准书写，运行期等比缩放）
    // =========================================================================

    /// <summary>
    /// 参考分辨率高度（px）。本皮肤里所有「px」尺寸都是在**这个高度**下量出来的设计值。
    /// <para>
    /// 为什么按高度而不是宽度：俯视角 3D 游戏横向视野随宽高比变化，而 HUD 的**字号可读性**只跟屏幕高度有关
    /// （21:9 加宽不该让字变大）。所以缩放系数 = 实际高度 / 参考高度。
    /// </para>
    /// </summary>
    [ExportGroup("分辨率自适应")]
    [Export(PropertyHint.Range, "360,4320,1")]
    public float ReferenceHeight { get; set; } = ReferenceHeightDefault;

    /// <summary>
    /// 是否按 <see cref="ReferenceHeight"/> 等比缩放。false = 1:1 像素（设计稿原尺寸），
    /// 用于超小窗口调试或刻意追求「物理像素精确」的场合。
    /// </summary>
    [Export]
    public bool AutoScale { get; set; } = true;

    /// <summary>缩放系数下限：防止极小分辨率把 HUD 画成针尖（文字已不可读，不如保底）。</summary>
    [Export(PropertyHint.Range, "0.1,5,0.05")]
    public float ScaleMin { get; set; } = ScaleMinDefault;

    /// <summary>缩放系数上限：防止 4K/8K 把面板糊满屏。</summary>
    [Export(PropertyHint.Range, "0.1,8,0.05")]
    public float ScaleMax { get; set; } = ScaleMaxDefault;

    /// <summary>
    /// 计算本帧的像素缩放系数（夹在 [<see cref="ScaleMin"/>, <see cref="ScaleMax"/>] 内）。
    /// <para>缩放系数是**表现参数**不是玩法数值，所以放在皮肤里（规则 4 §3.3 的「界面布局常量」分区）。</para>
    /// </summary>
    /// <param name="frameHeight">当前视口高度（px）。</param>
    public float ComputeScale(float frameHeight)
    {
        if (!AutoScale || !(ReferenceHeight > 0f) || !(frameHeight > 0f))
        {
            return 1f;
        }

        return ClampScale(frameHeight / ReferenceHeight);
    }

    /// <summary>把任意系数夹到皮肤允许的区间，并兜住非法输入（NaN / 负数 / Range 被改成反序）。</summary>
    public float ClampScale(float scale)
    {
        if (float.IsNaN(scale))
        {
            return 1f;
        }

        var lo = ScaleMin > 0f ? ScaleMin : 0f;
        var hi = ScaleMax > 0f ? ScaleMax : lo;
        if (hi < lo)
        {
            // Inspector 里把上下限填反了：以「下限」为准，宁可大一点也别画没
            hi = lo;
        }

        if (scale < lo)
        {
            return lo;
        }

        return scale > hi ? hi : scale;
    }

    // =========================================================================
    // 1. 贴纸（面板底板）—— 深色投影 + 暖白描边 + 圆角
    // =========================================================================

    /// <summary>贴纸面板宿主的主题名。真实值在 `scenes/ui/character_hud_skin.tres` 里。</summary>
    public const string ThemeName = "Chunky Sticker HUD";

    /// <summary>底板填充色（深蓝紫，压住画面但不像纯黑那样闷）。</summary>
    [ExportGroup("贴纸面板")]
    [Export]
    public Color PanelFill { get; set; } = DefaultPanelFill;

    /// <summary>底板描边色（暖白，比纯白柔和，贴纸感来自它）。</summary>
    [Export]
    public Color PanelBorder { get; set; } = DefaultPanelBorder;

    /// <summary>外投影色（深蓝黑，硬边、半透明）。</summary>
    [Export]
    public Color PanelShadow { get; set; } = DefaultPanelShadow;

    /// <summary>描边宽度（px）。</summary>
    [Export(PropertyHint.Range, "0,12,0.5")]
    public float PanelBorderWidth { get; set; } = DefaultPanelBorderWidth;

    /// <summary>圆角半径（px）。</summary>
    [Export(PropertyHint.Range, "0,48,1")]
    public float PanelCornerRadius { get; set; } = DefaultPanelCornerRadius;

    /// <summary>投影偏移（px，右下为正）。「硬边偏移」而非模糊，这是玩具感的来源。</summary>
    [Export]
    public Vector2 PanelShadowOffset { get; set; } = DefaultPanelShadowOffset;

    /// <summary>面板内边距（px）。</summary>
    [Export]
    public Vector2 PanelPadding { get; set; } = DefaultPanelPadding;

    /// <summary>左上角面板距离屏幕边角的偏移（px）。</summary>
    [Export]
    public Vector2 PanelAnchorOffset { get; set; } = DefaultPanelAnchorOffset;

    // =========================================================================
    // 2. 文本
    // =========================================================================

    /// <summary>角色名颜色（醒目：暖白）。</summary>
    [ExportGroup("文本")]
    [Export]
    public Color NameColor { get; set; } = DefaultNameColor;

    /// <summary>角色名字号。</summary>
    [Export(PropertyHint.Range, "8,64,1")]
    public int NameFontSize { get; set; } = DefaultNameFontSize;

    /// <summary>变体标签颜色（强调色：糖橙）。</summary>
    [Export]
    public Color TagColor { get; set; } = DefaultTagColor;

    /// <summary>变体标签字号（小号）。</summary>
    [Export(PropertyHint.Range, "6,40,1")]
    public int TagFontSize { get; set; } = DefaultTagFontSize;

    /// <summary>变体标签底板色（强调色的暗版，半透明）。</summary>
    [Export]
    public Color TagFill { get; set; } = DefaultTagFill;

    /// <summary>条内数值文字颜色。</summary>
    [Export]
    public Color ValueTextColor { get; set; } = DefaultValueTextColor;

    /// <summary>条内数值文字字号。</summary>
    [Export(PropertyHint.Range, "6,40,1")]
    public int ValueFontSize { get; set; } = DefaultValueFontSize;

    /// <summary>小属性行（护甲/移速/暴击/Lv）的标签色（略暗，让数值跳出来）。</summary>
    [Export]
    public Color StatLabelColor { get; set; } = DefaultStatLabelColor;

    /// <summary>小属性行的数值色。</summary>
    [Export]
    public Color StatValueColor { get; set; } = DefaultStatValueColor;

    /// <summary>小属性行字号。</summary>
    [Export(PropertyHint.Range, "6,40,1")]
    public int StatFontSize { get; set; } = DefaultStatFontSize;

    /// <summary>
    /// 文本描边宽度（px）。≥1 时给文字加一圈深色描边，保证在亮背景上也能读。
    /// Godot 4 中 outline_size 每 +1 约等于每侧 +1px。
    /// </summary>
    [Export(PropertyHint.Range, "0,4,1")]
    public int TextOutlineSize { get; set; } = DefaultTextOutlineSize;

    /// <summary>文本描边颜色（深蓝黑，与投影同族色，保持「贴纸」统一）。</summary>
    [Export]
    public Color TextOutlineColor { get; set; } = DefaultTextOutlineColor;

    // =========================================================================
    // 3. 资源条（血 / 蓝 / 体力）
    // =========================================================================

    /// <summary>条槽底色（三条共用，深一档）。</summary>
    [ExportGroup("资源条")]
    [Export]
    public Color BarTrack { get; set; } = DefaultBarTrack;

    /// <summary>条内高光色（顶部一条亮带，制造「塑料条」的圆润感）。</summary>
    [Export]
    public Color BarHighlight { get; set; } = DefaultBarHighlight;

    /// <summary>高光带占条高的比例（0..1）。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float BarHighlightRatio { get; set; } = BarHighlightRatioDefault;

    /// <summary>血量色（健康）。</summary>
    [Export]
    public Color HealthColor { get; set; } = DefaultHealthColor;

    /// <summary>血量低危色（比例低于 <see cref="HealthLowThreshold"/> 时渐变成它）。</summary>
    [Export]
    public Color HealthLowColor { get; set; } = DefaultHealthLowColor;

    /// <summary>低血阈值（0..1）。DTO 不提供「够不够」的语义，HUD 只按比例判断。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float HealthLowThreshold { get; set; } = HealthLowThresholdDefault;

    /// <summary>法力色。</summary>
    [Export]
    public Color ManaColor { get; set; } = DefaultManaColor;

    /// <summary>体力色。</summary>
    [Export]
    public Color StaminaColor { get; set; } = DefaultStaminaColor;

    /// <summary>掉血闪色（短暂覆盖在血条上，制造「挨打」的瞬时反馈）。</summary>
    [Export]
    public Color HealthFlashColor { get; set; } = DefaultHealthFlashColor;

    /// <summary>掉血闪色持续时间（秒）。0 = 关闭闪色。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float HealthFlashDuration { get; set; } = HealthFlashDurationDefault;

    /// <summary>条高（px）。</summary>
    [Export(PropertyHint.Range, "2,48,1")]
    public float BarHeight { get; set; } = DefaultBarHeight;

    /// <summary>条宽（px）。面板宽度 = 条宽 + 左右内边距。</summary>
    [Export(PropertyHint.Range, "60,900,2")]
    public float BarWidth { get; set; } = DefaultBarWidth;

    /// <summary>两条之间的竖直间距（px）。</summary>
    [Export(PropertyHint.Range, "0,24,1")]
    public float BarSpacing { get; set; } = DefaultBarSpacing;

    /// <summary>条的圆角 = 条高 × 本比例。1 = 完全圆头（胶囊），0 = 方头。</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float BarCornerRatio { get; set; } = BarCornerRatioDefault;

    // =========================================================================
    // 4. 技能格（5 个圆角方片）
    // =========================================================================

    /// <summary>技能格边长（px）。</summary>
    [ExportGroup("技能格")]
    [Export(PropertyHint.Range, "18,140,1")]
    public float SkillCellSize { get; set; } = DefaultSkillCellSize;

    /// <summary>技能格之间的间距（px）。</summary>
    [Export(PropertyHint.Range, "0,40,1")]
    public float SkillCellSpacing { get; set; } = DefaultSkillCellSpacing;

    /// <summary>技能格圆角（px）。</summary>
    [Export(PropertyHint.Range, "0,40,1")]
    public float SkillCornerRadius { get; set; } = DefaultSkillCornerRadius;

    /// <summary>技能格描边宽度（px）。</summary>
    [Export(PropertyHint.Range, "0,12,0.5")]
    public float SkillBorderWidth { get; set; } = DefaultSkillBorderWidth;

    /// <summary>技能格底板距屏幕右下角的偏移（px）。</summary>
    [Export]
    public Vector2 SkillAnchorOffset { get; set; } = DefaultSkillAnchorOffset;

    /// <summary>技能条内边距（px）。</summary>
    [Export]
    public Vector2 SkillStripPadding { get; set; } = DefaultSkillStripPadding;

    /// <summary>技能格投影偏移（px）。</summary>
    [Export]
    public Vector2 SkillShadowOffset { get; set; } = DefaultSkillShadowOffset;

    /// <summary>空槽填充色（暗灰蓝）。</summary>
    [Export]
    public Color SkillEmptyFill { get; set; } = DefaultSkillEmptyFill;

    /// <summary>空槽描边色。</summary>
    [Export]
    public Color SkillEmptyBorder { get; set; } = DefaultSkillEmptyBorder;

    /// <summary>就绪格填充色。</summary>
    [Export]
    public Color SkillReadyFill { get; set; } = DefaultSkillReadyFill;

    /// <summary>就绪格描边色（亮边：技能可用时最抢眼的那一笔）。</summary>
    [Export]
    public Color SkillReadyBorder { get; set; } = DefaultSkillReadyBorder;

    /// <summary>冷却中的填充色（暗版，和空槽区分开：空槽更灰，冷却更有颜色）。</summary>
    [Export]
    public Color SkillCoolingFill { get; set; } = DefaultSkillCoolingFill;

    /// <summary>冷却中的描边色。</summary>
    [Export]
    public Color SkillCoolingBorder { get; set; } = DefaultSkillCoolingBorder;

    /// <summary>冷却扇形遮罩色（半透明深色，从中心顺时针扫过）。</summary>
    [Export]
    public Color SkillCooldownMask { get; set; } = DefaultSkillCooldownMask;

    /// <summary>字形颜色（就绪）。</summary>
    [Export]
    public Color SkillGlyphColor { get; set; } = DefaultSkillGlyphColor;

    /// <summary>字形颜色（冷却中，压暗）。</summary>
    [Export]
    public Color SkillGlyphDimColor { get; set; } = DefaultSkillGlyphDimColor;

    /// <summary>字形字号。</summary>
    [Export(PropertyHint.Range, "6,64,1")]
    public int SkillGlyphFontSize { get; set; } = DefaultSkillGlyphFontSize;

    /// <summary>剩余秒数文字颜色。</summary>
    [Export]
    public Color SkillCountdownColor { get; set; } = DefaultSkillCountdownColor;

    /// <summary>剩余秒数字号。</summary>
    [Export(PropertyHint.Range, "6,48,1")]
    public int SkillCountdownFontSize { get; set; } = DefaultSkillCountdownFontSize;

    /// <summary>按键标签颜色。</summary>
    [Export]
    public Color SkillKeyColor { get; set; } = DefaultSkillKeyColor;

    /// <summary>按键标签字号。</summary>
    [Export(PropertyHint.Range, "6,32,1")]
    public int SkillKeyFontSize { get; set; } = DefaultSkillKeyFontSize;

    /// <summary>按键标签底板色。</summary>
    [Export]
    public Color SkillKeyFill { get; set; } = DefaultSkillKeyFill;

    /// <summary>按键标签底板的圆角（px）。</summary>
    [Export(PropertyHint.Range, "0,16,0.5")]
    public float SkillKeyBadgeRadius { get; set; } = DefaultSkillKeyBadgeRadius;

    /// <summary>就绪格呼吸脉动的幅度（0..1，0 = 不脉动）。克制：只闪亮边，不闪整格。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SkillReadyPulseAmount { get; set; } = SkillReadyPulseAmountDefault;

    /// <summary>就绪格脉动周期（秒）。</summary>
    [Export(PropertyHint.Range, "0.2,5,0.05")]
    public float SkillReadyPulsePeriod { get; set; } = SkillReadyPulsePeriodDefault;

    // =========================================================================
    // 5. 准星
    // =========================================================================

    /// <summary>准星线色（半透明白）。</summary>
    [ExportGroup("准星")]
    [Export]
    public Color CrosshairColor { get; set; } = DefaultCrosshairColor;

    /// <summary>准星描边色（深色，保证任何背景都能看见）。</summary>
    [Export]
    public Color CrosshairOutlineColor { get; set; } = DefaultCrosshairOutlineColor;

    /// <summary>准星描边宽度（px，画在线的下面）。</summary>
    [Export(PropertyHint.Range, "0,6,0.5")]
    public float CrosshairOutlineWidth { get; set; } = DefaultCrosshairOutlineWidth;

    /// <summary>准星线宽（px）。</summary>
    [Export(PropertyHint.Range, "0.5,10,0.5")]
    public float CrosshairLineWidth { get; set; } = DefaultCrosshairLineWidth;

    /// <summary>准星线长（px，从中心向外）。</summary>
    [Export(PropertyHint.Range, "1,40,0.5")]
    public float CrosshairLineLength { get; set; } = DefaultCrosshairLineLength;

    /// <summary>中心留空半径（px，让十字不挡住目标）。</summary>
    [Export(PropertyHint.Range, "0,24,0.5")]
    public float CrosshairGap { get; set; } = DefaultCrosshairGap;

    /// <summary>朝向指示的长度（px）：从中心沿 AimAngleDeg 方向画一条短线。</summary>
    [Export(PropertyHint.Range, "0,64,0.5")]
    public float CrosshairAimLength { get; set; } = DefaultCrosshairAimLength;

    /// <summary>朝向指示的三角形头大小（px）。</summary>
    [Export(PropertyHint.Range, "0,20,0.5")]
    public float CrosshairAimHead { get; set; } = DefaultCrosshairAimHead;

    /// <summary>中心点半径（px，0 = 不画中心点）。</summary>
    [Export(PropertyHint.Range, "0,8,0.5")]
    public float CrosshairDotRadius { get; set; } = DefaultCrosshairDotRadius;

    // =========================================================================
    // 6. 动画（纯表现，不影响任何结算）
    // =========================================================================

    /// <summary>低血脉动周期（秒）。低血时血量色在两个红色之间来回 —— 提醒但不刺眼。</summary>
    [ExportGroup("动画")]
    [Export(PropertyHint.Range, "0.2,5,0.05")]
    public float HealthLowPulsePeriod { get; set; } = HealthLowPulsePeriodDefault;

    /// <summary>低血脉动的混色幅度（0..1）。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float HealthLowPulseAmount { get; set; } = HealthLowPulseAmountDefault;

    /// <summary>数值变化时的数字滚动速度（每秒追赶比例，1 = 瞬间到位）。</summary>
    [Export(PropertyHint.Range, "0.05,1,0.01")]
    public float NumberLerpPerSecond { get; set; } = NumberLerpPerSecondDefault;

    // =========================================================================
    // 7. 默认值（结构性占位；真实值在 scenes/ui/character_hud_skin.tres）
    // 为什么构造里给默认值：皮肤是 Resource，没挂 `.tres` 时也要能画出一个像样的 HUD，
    // 不能让「忘记赋皮肤」变成全屏黑块。这些默认值 = 设计稿上那套配色。
    // =========================================================================

    /// <summary>底板填充默认值。</summary>
    public static readonly Color DefaultPanelFill = new Color(0.102f, 0.118f, 0.2f, 0.88f);

    /// <summary>底板描边默认值。</summary>
    public static readonly Color DefaultPanelBorder = new Color(0.976f, 0.949f, 0.882f, 1f);

    /// <summary>投影默认值。</summary>
    public static readonly Color DefaultPanelShadow = new Color(0.043f, 0.051f, 0.102f, 0.62f);

    /// <summary>描边宽度默认值（px）。</summary>
    public const float DefaultPanelBorderWidth = 3f;

    /// <summary>圆角默认值（px）。</summary>
    public const float DefaultPanelCornerRadius = 16f;

    /// <summary>面板投影偏移默认值（px）。</summary>
    public static readonly Vector2 DefaultPanelShadowOffset = new Vector2(0f, 5f);

    /// <summary>面板内边距默认值（px）。</summary>
    public static readonly Vector2 DefaultPanelPadding = new Vector2(16f, 12f);

    /// <summary>面板锚点偏移默认值（px）。</summary>
    public static readonly Vector2 DefaultPanelAnchorOffset = new Vector2(24f, 24f);

    /// <summary>角色名颜色默认值。</summary>
    public static readonly Color DefaultNameColor = new Color(1f, 0.98f, 0.94f, 1f);

    /// <summary>角色名字号默认值。</summary>
    public const int DefaultNameFontSize = 22;

    /// <summary>变体标签颜色默认值。</summary>
    public static readonly Color DefaultTagColor = new Color(1f, 0.851f, 0.42f, 1f);

    /// <summary>变体标签字号默认值。</summary>
    public const int DefaultTagFontSize = 13;

    /// <summary>变体标签底板默认值。</summary>
    public static readonly Color DefaultTagFill = new Color(0.42f, 0.24f, 0.06f, 0.85f);

    /// <summary>条内数值文字颜色默认值。</summary>
    public static readonly Color DefaultValueTextColor = new Color(1f, 1f, 1f, 1f);

    /// <summary>条内数值字号默认值。</summary>
    public const int DefaultValueFontSize = 13;

    /// <summary>属性标签颜色默认值。</summary>
    public static readonly Color DefaultStatLabelColor = new Color(0.639f, 0.69f, 0.796f, 1f);

    /// <summary>属性数值颜色默认值。</summary>
    public static readonly Color DefaultStatValueColor = new Color(0.937f, 0.961f, 1f, 1f);

    /// <summary>属性行字号默认值。</summary>
    public const int DefaultStatFontSize = 13;

    /// <summary>文本描边宽度默认值（px）。</summary>
    public const int DefaultTextOutlineSize = 3;

    /// <summary>文本描边颜色默认值。</summary>
    public static readonly Color DefaultTextOutlineColor = new Color(0.043f, 0.051f, 0.102f, 0.9f);

    /// <summary>条槽底色默认值。</summary>
    public static readonly Color DefaultBarTrack = new Color(0.051f, 0.063f, 0.118f, 0.94f);

    /// <summary>条内高光默认值。</summary>
    public static readonly Color DefaultBarHighlight = new Color(1f, 1f, 1f, 0.28f);

    /// <summary>高光带比例默认值。</summary>
    public const float BarHighlightRatioDefault = 0.34f;

    /// <summary>血量色默认值（玩具红）。</summary>
    public static readonly Color DefaultHealthColor = new Color(0.949f, 0.29f, 0.31f, 1f);

    /// <summary>血量低危色默认值（深一号的红）。</summary>
    public static readonly Color DefaultHealthLowColor = new Color(0.67f, 0.118f, 0.192f, 1f);

    /// <summary>低血阈值默认值。</summary>
    public const float HealthLowThresholdDefault = 0.35f;

    /// <summary>法力色默认值（玩具蓝）。</summary>
    public static readonly Color DefaultManaColor = new Color(0.29f, 0.6f, 0.97f, 1f);

    /// <summary>体力色默认值（玩具绿）。</summary>
    public static readonly Color DefaultStaminaColor = new Color(0.44f, 0.83f, 0.4f, 1f);

    /// <summary>掉血闪色默认值。</summary>
    public static readonly Color DefaultHealthFlashColor = new Color(1f, 0.945f, 0.878f, 1f);

    /// <summary>掉血闪色时长默认值（秒）。</summary>
    public const float HealthFlashDurationDefault = 0.22f;

    /// <summary>条高默认值（px）。</summary>
    public const float DefaultBarHeight = 16f;

    /// <summary>条宽默认值（px）。</summary>
    public const float DefaultBarWidth = 268f;

    /// <summary>条间距默认值（px）。</summary>
    public const float DefaultBarSpacing = 6f;

    /// <summary>条圆角比例默认值。</summary>
    public const float BarCornerRatioDefault = 0.5f;

    /// <summary>技能格边长默认值（px）。</summary>
    public const float DefaultSkillCellSize = 56f;

    /// <summary>技能格间距默认值（px）。</summary>
    public const float DefaultSkillCellSpacing = 8f;

    /// <summary>技能格圆角默认值（px）。</summary>
    public const float DefaultSkillCornerRadius = 12f;

    /// <summary>技能格描边宽度默认值（px）。</summary>
    public const float DefaultSkillBorderWidth = 3f;

    /// <summary>技能条锚点偏移默认值（px）。</summary>
    public static readonly Vector2 DefaultSkillAnchorOffset = new Vector2(24f, 24f);

    /// <summary>技能条内边距默认值（px）。</summary>
    public static readonly Vector2 DefaultSkillStripPadding = new Vector2(12f, 10f);

    /// <summary>技能格投影偏移默认值（px）。</summary>
    public static readonly Vector2 DefaultSkillShadowOffset = new Vector2(0f, 4f);

    /// <summary>空槽填充默认值。</summary>
    public static readonly Color DefaultSkillEmptyFill = new Color(0.137f, 0.153f, 0.224f, 0.86f);

    /// <summary>空槽描边默认值。</summary>
    public static readonly Color DefaultSkillEmptyBorder = new Color(0.278f, 0.31f, 0.4f, 1f);

    /// <summary>就绪填充默认值。</summary>
    public static readonly Color DefaultSkillReadyFill = new Color(0.216f, 0.278f, 0.451f, 0.94f);

    /// <summary>就绪描边默认值（亮边）。</summary>
    public static readonly Color DefaultSkillReadyBorder = new Color(1f, 0.851f, 0.42f, 1f);

    /// <summary>冷却填充默认值。</summary>
    public static readonly Color DefaultSkillCoolingFill = new Color(0.16f, 0.192f, 0.31f, 0.92f);

    /// <summary>冷却描边默认值。</summary>
    public static readonly Color DefaultSkillCoolingBorder = new Color(0.361f, 0.404f, 0.522f, 1f);

    /// <summary>冷却遮罩默认值。</summary>
    public static readonly Color DefaultSkillCooldownMask = new Color(0.024f, 0.031f, 0.078f, 0.66f);

    /// <summary>字形颜色默认值。</summary>
    public static readonly Color DefaultSkillGlyphColor = new Color(1f, 0.98f, 0.94f, 1f);

    /// <summary>冷却字形颜色默认值。</summary>
    public static readonly Color DefaultSkillGlyphDimColor = new Color(0.639f, 0.69f, 0.796f, 0.75f);

    /// <summary>字形字号默认值。</summary>
    public const int DefaultSkillGlyphFontSize = 17;

    /// <summary>剩余秒数颜色默认值。</summary>
    public static readonly Color DefaultSkillCountdownColor = new Color(1f, 0.851f, 0.42f, 1f);

    /// <summary>剩余秒数字号默认值。</summary>
    public const int DefaultSkillCountdownFontSize = 16;

    /// <summary>按键标签颜色默认值。</summary>
    public static readonly Color DefaultSkillKeyColor = new Color(0.937f, 0.961f, 1f, 1f);

    /// <summary>按键标签字号默认值。</summary>
    public const int DefaultSkillKeyFontSize = 10;

    /// <summary>按键标签底板默认值。</summary>
    public static readonly Color DefaultSkillKeyFill = new Color(0.043f, 0.051f, 0.102f, 0.72f);

    /// <summary>按键标签底板圆角默认值（px）。</summary>
    public const float DefaultSkillKeyBadgeRadius = 4f;

    /// <summary>就绪脉动幅度默认值。</summary>
    public const float SkillReadyPulseAmountDefault = 0.16f;

    /// <summary>就绪脉动周期默认值（秒）。</summary>
    public const float SkillReadyPulsePeriodDefault = 2f;

    /// <summary>准星线色默认值。</summary>
    public static readonly Color DefaultCrosshairColor = new Color(1f, 1f, 1f, 0.85f);

    /// <summary>准星描边颜色默认值。</summary>
    public static readonly Color DefaultCrosshairOutlineColor = new Color(0.043f, 0.051f, 0.102f, 0.72f);

    /// <summary>准星描边宽度默认值（px）。</summary>
    public const float DefaultCrosshairOutlineWidth = 3f;

    /// <summary>准星线宽默认值（px）。</summary>
    public const float DefaultCrosshairLineWidth = 2f;

    /// <summary>准星线长默认值（px）。</summary>
    public const float DefaultCrosshairLineLength = 12f;

    /// <summary>准星中心留空默认值（px）。</summary>
    public const float DefaultCrosshairGap = 5f;

    /// <summary>朝向指示长度默认值（px）。</summary>
    public const float DefaultCrosshairAimLength = 20f;

    /// <summary>朝向指示三角头默认值（px）。</summary>
    public const float DefaultCrosshairAimHead = 5f;

    /// <summary>准星中心点半径默认值（px）。</summary>
    public const float DefaultCrosshairDotRadius = 1.5f;

    /// <summary>低血脉动周期默认值（秒）。</summary>
    public const float HealthLowPulsePeriodDefault = 0.9f;

    /// <summary>低血脉动幅度默认值。</summary>
    public const float HealthLowPulseAmountDefault = 0.55f;

    /// <summary>数字滚动速度默认值（每秒）。</summary>
    public const float NumberLerpPerSecondDefault = 0.35f;

    /// <summary>参考分辨率高度默认值（px）：设计稿按 1080p 量尺寸。</summary>
    public const float ReferenceHeightDefault = 1080f;

    /// <summary>缩放系数下限默认值。</summary>
    public const float ScaleMinDefault = 0.6f;

    /// <summary>缩放系数上限默认值。</summary>
    public const float ScaleMaxDefault = 3f;
}
