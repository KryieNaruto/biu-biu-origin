using Godot;
using GodotControl = Godot.Control;

namespace Game.Host.Ui;

/// <summary>
/// 调试 UI 的**唯一样式真源**：字号 / 间距 / 列宽 / 配色都从这里出，任何文件不再各写一份。
/// <para>
/// 为什么要这一层：滑条自带的文字、滚动条、按钮悬停态都不是我们 <c>AddChild</c> 出来的，
/// 逐个 <c>AddThemeFontSizeOverride</c> 够不到；图例画在 <c>Node2D</c> 里（不在 Control 树里）也吃不到 Theme。
/// 两边都从这里取数，才能保证「面板字」和「图例字」永远同一口径。
/// </para>
/// </summary>
public static class DebugUiStyle
{
    // ── 字号（像素）────────────────────────────────────────────────────

    /// <summary>正文最小可读字号（滑条名 / 数值 / 状态行）。低于这个值在 1080p 上就是「看不清」。</summary>
    public const int FontBody = 14;

    /// <summary>小标题字号（图例分组标题、面板副标题）。</summary>
    public const int FontSmallTitle = 15;

    /// <summary>面板主标题字号。</summary>
    public const int FontTitle = 18;

    /// <summary>
    /// 图例文字字号。**比面板正文大一档**：图例画在地图旁边的空地上、离眼睛更远，
    /// 与面板同号时在 1080p 上要凑近才认得出（用户反馈「标注字太小」）。
    /// </summary>
    public const int FontLegend = 20;

    /// <summary>窗口高度达到这个像素数时升到「大窗口档」，字号整体 +2。</summary>
    public const float LargeWindowHeight = 1100f;

    /// <summary>大窗口档相对基准档的字号增量。</summary>
    public const int LargeWindowFontDelta = 2;

    // ── 间距 / 列宽 ───────────────────────────────────────────────────

    /// <summary>滑条行里「名字」列宽。</summary>
    public const float LabelColumnWidth = 176f;

    /// <summary>滑条行里「当前值」列宽。固定宽度，数字位数变化时整列不跳。</summary>
    public const float ValueColumnWidth = 48f;

    /// <summary>滑条行内控件间距。</summary>
    public const int RowSeparation = 8;

    /// <summary>面板主体各行之间的间距。</summary>
    public const int SectionSeparation = 6;

    /// <summary>面板自身最小宽度（含内边距）。</summary>
    public const float PanelMinWidth = 400f;

    /// <summary>面板内边距。</summary>
    public const int PanelPadding = 10;

    /// <summary>面板滚动区内边距。</summary>
    public const int PanelInnerPadding = 8;

    // ── 图例排版 ─────────────────────────────────────────────────────
    // 图例画在**地图右侧**的空白列里（竖排一列、一项一行），不再塞在地图下方。
    // 竖排的理由：那一列随窗口变高而变高，行数够多也不会被横向挤成一坨；
    // 横排得按「最宽的窗口」预留高度，窗口一窄就换行错位。

    /// <summary>图例色块边长。</summary>
    public const float LegendSwatch = 18f;

    /// <summary>图例色块与文字的间距。</summary>
    public const float LegendSwatchGap = 10f;

    /// <summary>图例项之间的间距。</summary>
    public const float LegendItemGap = 6f;

    /// <summary>地图与图例列之间的间距（水平方向）。</summary>
    public const float LegendTopGap = 24f;

    /// <summary>图例的行距（一行图例占的高度）。</summary>
    public const float LegendRowHeight = 28f;

    /// <summary>图例左/上留白。</summary>
    public const float LegendMargin = 6f;

    /// <summary>图例最窄的占位宽度：再窄中文标签就要被裁掉。</summary>
    public const float LegendMinColumnWidth = 260f;

    /// <summary>图例区占掉的**高度**：图例站在地图右侧，不占地图下方高度，故为 0。</summary>
    public const float LegendReservedHeight = 0f;

    // ── 滚动条 ───────────────────────────────────────────────────────

    /// <summary>滚动条粗细。默认主题只有 12px，换字号后太细不好拖。</summary>
    public const int ScrollBarWidth = 16;

    // ── 配色 ─────────────────────────────────────────────────────────

    /// <summary>面板背景（图例底板也用它，不透明深底）。</summary>
    public static readonly Color PanelBackground = new(0.11f, 0.12f, 0.14f, 0.96f);

    /// <summary>面板边框（图例分隔线也用它）。</summary>
    public static readonly Color PanelBorder = new(0.28f, 0.30f, 0.34f, 1f);

    /// <summary>正文文字色。</summary>
    public static readonly Color TextBody = new(0.86f, 0.88f, 0.91f, 1f);

    /// <summary>标题文字色。</summary>
    public static readonly Color TextTitle = new(1f, 0.92f, 0.70f, 1f);

    /// <summary>分组小标题文字色。</summary>
    public static readonly Color TextSection = new(0.62f, 0.80f, 1f, 1f);

    /// <summary>数值列文字色（比正文亮一点，拖动时眼睛更容易咬住）。</summary>
    public static readonly Color TextValue = new(0.55f, 1f, 0.78f, 1f);

    /// <summary>状态行文字色（灰，不抢注意力）。</summary>
    public static readonly Color TextStatus = new(0.66f, 0.68f, 0.72f, 1f);

    /// <summary>
    /// 造一个覆盖整棵调试 UI 子树的 <see cref="Theme"/>。
    /// <para>
    /// <b>为什么必须走 Theme 而不是逐控件 override</b>：<see cref="HSlider"/> 的轨道/抓手上没有文字，
    /// 但 <see cref="Godot.Range"/> 系列会在内部造 <see cref="LineEdit"/> / tooltip，
    /// 那些控件不是我们 <c>AddChild</c> 出来的，拿不到引用 —— 只有 Theme 能按类型覆盖到它们。
    /// </para>
    /// </summary>
    /// <param name="windowHeight">窗口高度（像素），用于决定字号档位。传 0 表示用基准档。</param>
    /// <returns>一个**新建的** Theme 实例（Godot Theme 是引用类型，别跨场景共用同一份可变实例）。</returns>
    public static Theme CreateFor(float windowHeight)
    {
        int delta = windowHeight >= LargeWindowHeight ? LargeWindowFontDelta : 0;

        Theme theme = new Theme
        {
            DefaultFontSize = FontBody + delta,
        };

        // ── 面板容器：底色 + 圆角边框 ───────────────────────────────
        StyleBoxFlat panelStyle = new StyleBoxFlat
        {
            BgColor = PanelBackground,
            BorderColor = PanelBorder,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        panelStyle.SetBorderWidthAll(1);
        panelStyle.SetContentMarginAll(PanelPadding);
        theme.SetStylebox("panel", "PanelContainer", panelStyle);

        // ── 标签 ────────────────────────────────────────────────────
        theme.SetColor("font_color", "Label", TextBody);
        theme.SetFontSize("font_size", "Label", FontBody + delta);

        // ── 滑条：加粗轨道 + 放大抓手（默认 4px 轨道在 1080p 上几乎看不见）──
        StyleBoxFlat sliderTrack = new StyleBoxFlat
        {
            BgColor = new Color(0.20f, 0.22f, 0.26f, 1f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            ContentMarginTop = 3,
            ContentMarginBottom = 3,
        };
        theme.SetStylebox("slider", "HSlider", sliderTrack);

        StyleBoxFlat sliderGrabber = new StyleBoxFlat
        {
            BgColor = new Color(0.55f, 0.78f, 1f, 1f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
        theme.SetStylebox("grabber_area", "HSlider", sliderGrabber);
        theme.SetStylebox("grabber_area_highlight", "HSlider", sliderGrabber);

        // ── 按钮 ────────────────────────────────────────────────────
        StyleBoxFlat buttonNormal = new StyleBoxFlat
        {
            BgColor = new Color(0.19f, 0.21f, 0.25f, 1f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        StyleBoxFlat buttonHover = (StyleBoxFlat)buttonNormal.Duplicate();
        buttonHover.BgColor = new Color(0.26f, 0.29f, 0.35f, 1f);
        StyleBoxFlat buttonPressed = (StyleBoxFlat)buttonNormal.Duplicate();
        buttonPressed.BgColor = new Color(0.30f, 0.55f, 0.80f, 1f);
        theme.SetStylebox("normal", "Button", buttonNormal);
        theme.SetStylebox("hover", "Button", buttonHover);
        theme.SetStylebox("pressed", "Button", buttonPressed);
        theme.SetFontSize("font_size", "Button", FontBody + delta);

        // ── 滚动条 ──────────────────────────────────────────────────
        StyleBoxFlat scrollTrack = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.11f, 0.13f, 0.9f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        StyleBoxFlat scrollGrabber = new StyleBoxFlat
        {
            BgColor = new Color(0.38f, 0.41f, 0.46f, 1f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        theme.SetStylebox("scroll", "VScrollBar", scrollTrack);
        theme.SetStylebox("grabber", "VScrollBar", scrollGrabber);
        theme.SetStylebox("grabber_highlight", "VScrollBar", scrollGrabber);
        theme.SetStylebox("grabber_pressed", "VScrollBar", scrollGrabber);
        theme.SetStylebox("scroll", "HScrollBar", scrollTrack);
        theme.SetStylebox("grabber", "HScrollBar", scrollGrabber);
        theme.SetStylebox("grabber_highlight", "HScrollBar", scrollGrabber);
        theme.SetStylebox("grabber_pressed", "HScrollBar", scrollGrabber);
        theme.SetConstant("minimum_grab_thickness", "VScrollBar", ScrollBarWidth);
        theme.SetConstant("minimum_grab_thickness", "HScrollBar", ScrollBarWidth);

        // ── Tooltip / 内部文本（HSlider 拖动时的浮字走这些）──────────
        theme.SetFontSize("font_size", "TooltipLabel", FontBody + delta);
        theme.SetColor("font_color", "TooltipLabel", TextBody);
        theme.SetFontSize("font_size", "LineEdit", FontBody + delta);
        theme.SetFontSize("font_size", "SpinBox", FontBody + delta);

        return theme;
    }

    /// <summary>
    /// 把样式挂到 <paramref name="root"/> 上；返回实际使用的主题，方便调用方按窗口高度重建。
    /// <para>
    /// 用 <see cref="GodotControl.Theme"/> 而不是逐控件 override：Theme 会**向下继承**，
    /// 根节点挂一份，整棵子树（包括运行时才 AddChild 的滑条行）自动生效。
    /// </para>
    /// </summary>
    /// <param name="root">子树根节点（通常是场景根 Control）。</param>
    /// <param name="windowHeight">当前窗口高度（像素）。</param>
    /// <returns>挂上去的 Theme 实例。</returns>
    public static Theme ApplyTo(GodotControl root, float windowHeight)
    {
        Theme theme = CreateFor(windowHeight);
        root.Theme = theme;
        return theme;
    }
}
