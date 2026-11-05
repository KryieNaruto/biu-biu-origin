using System;
using System.Globalization;
using Godot;

namespace Game.UI;

/// <summary>
/// 角色 HUD 画布：纯 C# 立即模式绘制。**不建子节点，也不在 <c>_Draw</c> 里 new 托管对象。**
/// <para>
/// <b>画什么</b>（Chunky Sticker UI：硬边投影 + 暖白描边 + 圆角）：
/// <list type="number">
/// <item>左上「贴纸面板」：第一行角色名（醒目）+ 变体标签（小号强调色胶囊）；第二段血/蓝/体力三条圆头资源条
/// （带内高光，数值右对齐画在条内，掉血闪色 + 低血变红）；第三段小属性「护甲 · 移速 · 暴击 · Lv」。</item>
/// <item>右下「技能条」：5 个圆角方片。空槽暗灰 / 就绪亮边（克制脉动）/ 冷却扇形遮罩 + 剩余秒数 / 角落按键标签。</item>
/// <item>屏幕中心准星：小十字 + 朝向指示，半透明白 + 深色描边，任何背景都看得见。</item>
/// </list>
/// </para>
/// <para>
/// <b>不做什么</b>（红线 5 / 规则 1 / 规则 4）：不读 ECS、不调结算、不持有游戏真值；颜色尺寸字号全部来自
/// <see cref="Skin"/>；不吃输入（准星只是「画在中心」，真正的鼠标逻辑在宿主）。
/// </para>
/// <para>
/// <b>每帧预算</b>（契约：字符串格式化 ≤ 8 次、托管分配 ≈ 0）：
/// 托管对象全部在「皮肤或视图尺寸变化」时预分配（点集、描边盒）；数值文本按整数刻度缓存，值不变就不重新格式化
/// —— 稳态每帧 0 次格式化，最坏 3 次（血/蓝/体力文字各一次）。
/// </para>
/// </summary>
[GlobalClass]
public partial class CharacterHudDrawer : Control
{
    // =========================================================================
    // 0. [Export]：Inspector 可调
    // =========================================================================

    /// <summary>皮肤资源：所有颜色 / 尺寸 / 字号 / 时长。为空 = 不画并告警一次。</summary>
    [Export]
    public CharacterHudSkin? Skin { get; set; }

    /// <summary>是否画左上角角色面板。</summary>
    [ExportGroup("可见性")]
    [Export]
    public bool ShowCharacterPanel { get; set; } = true;

    /// <summary>是否画右下角技能条。</summary>
    [Export]
    public bool ShowSkillBar { get; set; } = true;

    /// <summary>是否画屏幕中心准星（含朝向指示）。</summary>
    [Export]
    public bool ShowCrosshair { get; set; } = true;

    /// <summary>是否在资源条内画「当前/上限」数值。</summary>
    [Export]
    public bool ShowBarValues { get; set; } = true;

    /// <summary>是否在技能格角落画按键标签（LMB / LSHIFT ...）。</summary>
    [Export]
    public bool ShowSkillKeyLabels { get; set; } = true;

    /// <summary>HUD 整体不透明度（0..1），用于过场淡入淡出。</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float HudOpacity { get; set; } = 1f;

    /// <summary>调试：只画底板与格子，不画文字（排查遮挡）。</summary>
    [Export]
    public bool DebugDrawBoxesOnly { get; set; }

    // =========================================================================
    // 1. 结构性常量
    // =========================================================================

    /// <summary>契约规定的「每帧字符串格式化次数」上限。</summary>
    public const int FormatBudgetPerFrame = 8;

    /// <summary>技能格数量（五固定槽，见 <c>Game.Core.SkillSlot</c>）。</summary>
    private const int SkillCount = HudSnapshot.SkillSlotCount;

    /// <summary>冷却扇形最多用几个点（扇心另计）。</summary>
    private const int WedgeSegments = 24;

    /// <summary>点集缓冲大小：扇形的 1 个扇心 + <see cref="WedgeSegments"/> 个弧点。</summary>
    private const int PointBufferSize = WedgeSegments + 2;

    private const string Empty = "";
    private const string DotSeparator = "\u00B7";   // · 属性之间的轻分隔
    private const string CrossSeparator = "\u00D7"; // × 暴击「暴率×倍率」
    private const string PercentSuffix = "%";
    private const string MultiplierSuffix = "x";
    private const string SpeedFormat = "0.0";

    // =========================================================================
    // 2. 运行状态
    // =========================================================================

    private HudSnapshot _snapshot;

    // --- 表现状态（只由 FrameSeconds 与快照差值推进，绝不写回逻辑层）---
    private float _lastHealth = float.NaN;
    private float _flashRemaining;
    private float _flashStrength;
    private float _presentationSeconds;

    private float _shownHealth;
    private float _shownMana;
    private float _shownStamina;
    private float _shownArmor;
    private float _shownSpeed;
    private float _shownCritChance;
    private float _shownCritMultiplier;

    // --- 皮肤 / 字体缓存 ---
    private CharacterHudSkin? _cachedSkin;
    private Font? _font;

    // --- 几何缓存（视图尺寸或皮肤变了才重建）---
    private Vector2 _cachedViewSize;
    private CharacterHudSkin? _geometrySkin;
    private Rect2 _panelRect;
    private Rect2 _barHealth;
    private Rect2 _barMana;
    private Rect2 _barStamina;
    private Vector2 _statOrigin;
    private float _statLineHeight;
    private Rect2 _skillStrip;
    private Vector2 _skillCell0;
    private float _skillCellSize;
    private float _skillCellStep;
    private float _scale = 1f;
    private float _titleLineHeight;
    private Vector2 _titleBaseline;
    private Vector2 _titleBadgePad;
    private float _titleGap;
    private float _sectionGap;

    // --- 预分配的点集与描边盒（_Draw 里只写入复用，绝不 new）---
    private readonly Vector2[] _points = new Vector2[PointBufferSize];
    private readonly Color[] _uvs = new Color[PointBufferSize];
    private readonly StyleBoxFlat _box = new StyleBoxFlat { AntiAliasing = true };

    private bool _warnedNoSkin;

    // =========================================================================
    // 3. 公开 API
    // =========================================================================

    /// <summary>宿主每帧调用：提交本帧快照（按值拷贝，零分配）。之后自行 QueueRedraw 或调 <see cref="Submit"/>。</summary>
    public void SetSnapshot(in HudSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    /// <summary>宿主每帧调用：提交快照并排一次重绘。</summary>
    public void Submit(in HudSnapshot snapshot)
    {
        _snapshot = snapshot;
        QueueRedraw();
    }

    /// <summary>最近一次提交的快照（测试 / 截图脚本读）。</summary>
    public HudSnapshot Snapshot => _snapshot;

    /// <summary>当前是否有角色可显示。</summary>
    public bool HasCharacter => _snapshot.HasCharacter;

    /// <summary>清空 HUD（切场景 / 读档），表现状态一并复位。</summary>
    public void Clear()
    {
        _snapshot = default;
        ResetPresentationState();
        QueueRedraw();
    }

    /// <summary>复位纯表现状态（掉血闪色 / 数字滚动 / 脉动计时）。换角色时宿主可调。</summary>
    public void ResetPresentationState()
    {
        _lastHealth = float.NaN;
        _flashRemaining = 0f;
        _flashStrength = 0f;
        _presentationSeconds = 0f;
        _shownHealth = 0f;
        _shownMana = 0f;
        _shownStamina = 0f;
        _shownArmor = 0f;
        _shownSpeed = 0f;
        _shownCritChance = 0f;
        _shownCritMultiplier = 0f;
        InvalidateTextCache();
    }

    // =========================================================================
    // 4. 生命周期
    // =========================================================================

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    // =========================================================================
    // 5. 纯函数（无引擎状态，供单元测试钉住）
    // =========================================================================

    /// <summary>
    /// 资源条最终颜色。只有血条额外吃「低血渐变」与「掉血闪光」。
    /// <para>DTO 不提供「血够不够」的语义，所以低血判定**只看比例**（<see cref="CharacterHudSkin.HealthLowThreshold"/>）。</para>
    /// </summary>
    /// <param name="skin">皮肤。</param>
    /// <param name="baseColor">该条基色。</param>
    /// <param name="ratio">当前比例 0..1。</param>
    /// <param name="isHealthBar">是否血条。</param>
    /// <param name="flash">掉血闪光强度 0..1。</param>
    /// <param name="pulse">低血脉动 0..1（正弦后的值）。</param>
    public static Color ResolveBarColor(
        CharacterHudSkin skin,
        Color baseColor,
        float ratio,
        bool isHealthBar,
        float flash,
        float pulse)
    {
        var c = baseColor;
        if (isHealthBar)
        {
            var low = skin.HealthLowThreshold;
            if (low > 0f && ratio < low)
            {
                var t = 1f - (ratio / low);
                if (t < 0f)
                {
                    t = 0f;
                }
                else if (t > 1f)
                {
                    t = 1f;
                }

                // 越接近见底越红，再叠一层「心跳」式的明暗脉动
                c = baseColor.Lerp(skin.HealthLowColor, t);
                var pulseMix = skin.HealthLowPulseAmount * pulse * t;
                if (pulseMix > 0f)
                {
                    c = c.Lerp(skin.HealthLowColor.Lightened(0.3f), pulseMix);
                }
            }

            if (flash > 0f)
            {
                var f = flash > 1f ? 1f : flash;
                c = c.Lerp(skin.HealthFlashColor, f);
            }
        }

        return c;
    }

    /// <summary>技能格三态配色：空槽 / 就绪（描边随脉动提亮）/ 冷却中。</summary>
    public static void ResolveSkillColors(
        CharacterHudSkin skin,
        in HudSkillSlot slot,
        float readyPulse,
        out Color fill,
        out Color border)
    {
        if (!slot.HasSkill)
        {
            fill = skin.SkillEmptyFill;
            border = skin.SkillEmptyBorder;
            return;
        }

        if (slot.Ready)
        {
            fill = skin.SkillReadyFill;
            border = skin.SkillReadyBorder.Lightened(skin.SkillReadyPulseAmount * readyPulse);
            return;
        }

        fill = skin.SkillCoolingFill;
        border = skin.SkillCoolingBorder;
    }

    /// <summary>冷却进度 0..1 → 遮罩弧角（弧度；0 = 无遮罩，2π = 全遮）。</summary>
    public static float CooldownArcRadians(float cooldownProgress)
    {
        if (!(cooldownProgress > 0f))
        {
            return 0f;
        }

        var p = cooldownProgress > 1f ? 1f : cooldownProgress;
        return Mathf.Tau * p;
    }

    /// <summary>
    /// 掉血闪光强度：<paramref name="deltaHp"/> 相对「一管血的 12%」归一，夹到 [0.45, 1]。
    /// 小擦伤也看得见，大伤害不至于把血条闪成一片白。
    /// </summary>
    public static float FlashStrengthFor(float deltaHp, float maxHp)
    {
        if (!(deltaHp > 0f))
        {
            return 0f;
        }

        var denom = maxHp > 0f ? maxHp : 1f;
        var s = deltaHp / (denom * 0.12f);
        if (s < 0.45f)
        {
            return 0.45f;
        }

        return s > 1f ? 1f : s;
    }

    /// <summary>「当前/上限」：当前向上取整（39.2 显示 40，不会「差一点死」还显示 39）。上限为 0 时只显示当前值。</summary>
    public static string FormatBarValue(float value, float max)
    {
        var v = value > 0f ? (int)MathF.Ceiling(value) : 0;
        var m = max > 0f ? (int)MathF.Round(max) : 0;
        if (m <= 0)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        return string.Concat(v.ToString(CultureInfo.InvariantCulture), "/", m.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>护甲：「12(+8%)」—— 数值 + 宿主已算好的减伤百分比。</summary>
    public static string FormatArmor(float armor, float reduction01)
    {
        var a = (int)MathF.Round(armor);
        var p = (int)MathF.Round(reduction01 * 100f);
        return string.Concat(a.ToString(CultureInfo.InvariantCulture), "(+", p.ToString(CultureInfo.InvariantCulture), PercentSuffix, ")");
    }

    /// <summary>移速：「6.0」。</summary>
    public static string FormatSpeed(float speed)
    {
        return speed.ToString(SpeedFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>暴击率：「15.0%」。</summary>
    public static string FormatCritChance(float chance01)
    {
        return string.Concat((chance01 * 100f).ToString(SpeedFormat, CultureInfo.InvariantCulture), PercentSuffix);
    }

    /// <summary>暴击倍率：「2.0x」。</summary>
    public static string FormatCritMultiplier(float multiplier)
    {
        return string.Concat(multiplier.ToString(SpeedFormat, CultureInfo.InvariantCulture), MultiplierSuffix);
    }

    /// <summary>等级数字。</summary>
    public static string FormatLevel(int level)
    {
        return level.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>技能剩余秒数：「1.2」。</summary>
    public static string FormatSeconds(float seconds)
    {
        return seconds.ToString(SpeedFormat, CultureInfo.InvariantCulture);
    }

    // =========================================================================
    // 6. 绘制入口
    // =========================================================================

    public override void _Draw()
    {
        var skin = Skin;
        if (skin == null)
        {
            if (!_warnedNoSkin)
            {
                _warnedNoSkin = true;
                GD.PushWarning("[HUD] CharacterHudDrawer.Skin is null; assign res://scenes/ui/character_hud_skin.tres.");
            }

            return;
        }

        var opacity = HudOpacity;
        if (!(opacity > 0f))
        {
            return;
        }

        var viewSize = Size;
        if (viewSize.X <= 0f || viewSize.Y <= 0f)
        {
            return;
        }

        var frame = _snapshot.FrameSeconds;
        if (!(frame > 0f))
        {
            frame = 0f;
        }

        _presentationSeconds += frame;

        EnsureSkinCached(skin);
        AdvancePresentation(frame, skin);

        if (_cachedViewSize != viewSize || !ReferenceEquals(_geometrySkin, skin))
        {
            RebuildGeometry(skin, viewSize);
        }

        var ctx = new DrawCtx(this, skin, new Color(1f, 1f, 1f, opacity), _points, _uvs, _box, _scale);

        if (_snapshot.HasCharacter)
        {
            if (ShowCharacterPanel)
            {
                DrawCharacterPanel(ctx);
            }

            if (ShowSkillBar)
            {
                DrawSkillBar(ctx);
            }
        }

        if (ShowCrosshair)
        {
            DrawCrosshair(ctx, viewSize);
        }
    }

    // -------------------------------------------------------------------------
    // 6.1 表现状态推进（写不到逻辑层任何东西）
    // -------------------------------------------------------------------------

    private void AdvancePresentation(float frameSeconds, CharacterHudSkin skin)
    {
        var hp = _snapshot.Health.Value;
        var hpMax = _snapshot.Health.Max;

        if (!float.IsNaN(_lastHealth) && hp < _lastHealth)
        {
            var strength = FlashStrengthFor(_lastHealth - hp, hpMax);
            if (strength > 0f && skin.HealthFlashDuration > 0f)
            {
                // 取「更强的那次」而不是叠加时长：连续挨打时 HUD 不该常亮
                if (strength > _flashStrength)
                {
                    _flashStrength = strength;
                }

                _flashRemaining = skin.HealthFlashDuration;
            }
        }

        _lastHealth = hp;

        if (_flashRemaining > 0f)
        {
            _flashRemaining -= frameSeconds;
            if (_flashRemaining < 0f)
            {
                _flashRemaining = 0f;
            }
        }

        var k = frameSeconds * skin.NumberLerpPerSecond;
        if (!(k > 0f))
        {
            return;
        }

        if (k > 1f)
        {
            k = 1f;
        }

        _shownHealth = LerpToward(_shownHealth, hp, k);
        _shownMana = LerpToward(_shownMana, _snapshot.Mana.Value, k);
        _shownStamina = LerpToward(_shownStamina, _snapshot.Stamina.Value, k);
        _shownArmor = LerpToward(_shownArmor, _snapshot.Armor, k);
        _shownSpeed = LerpToward(_shownSpeed, _snapshot.MoveSpeed, k);
        _shownCritChance = LerpToward(_shownCritChance, _snapshot.CritChance, k);
        _shownCritMultiplier = LerpToward(_shownCritMultiplier, _snapshot.CritMultiplier, k);
    }

    private static float LerpToward(float current, float target, float k)
    {
        var d = target - current;
        if (MathF.Abs(d) < 0.02f)
        {
            return target;
        }

        return current + (d * k);
    }

    // -------------------------------------------------------------------------
    // 6.2 几何（视图尺寸 / 皮肤变化时重建一次）
    // -------------------------------------------------------------------------

    private void RebuildGeometry(CharacterHudSkin skin, Vector2 viewSize)
    {
        _cachedViewSize = viewSize;
        _geometrySkin = skin;

        // 所有几何都以「参考分辨率下的设计值」书写，统一乘 s 换算成物理像素。
        // 字号取整（字体渲染用整数 size 更锐利），其余尺寸保留浮点（圆角/描边需要亚像素平滑）。
        var s = skin.ComputeScale(viewSize.Y);
        _scale = s;

        var pad = skin.PanelPadding * s;
        var barH = skin.BarHeight * s;
        var barGap = skin.BarSpacing * s;
        var nameFontSize = ScaleFontSize(skin.NameFontSize, s);
        var tagFontSize = ScaleFontSize(skin.TagFontSize, s);
        var statFontSize = ScaleFontSize(skin.StatFontSize, s);
        _titleLineHeight = nameFontSize * 1.3f;
        _sectionGap = pad.Y * 0.9f;
        _titleBadgePad = new Vector2(tagFontSize * 0.5f, tagFontSize * 0.1f);
        _titleGap = tagFontSize * 0.6f;
        _statLineHeight = statFontSize * 1.4f;

        var barsHeight = (barH * 3f) + (barGap * 2f);
        var contentW = skin.BarWidth * s;
        var contentH = _titleLineHeight + _sectionGap + barsHeight + _sectionGap + _statLineHeight;

        _panelRect = new Rect2(skin.PanelAnchorOffset * s, new Vector2(contentW + (pad.X * 2f), contentH + (pad.Y * 2f)));

        var x = _panelRect.Position.X + pad.X;
        var y = _panelRect.Position.Y + pad.Y;
        _titleBaseline = new Vector2(x, y + (_titleLineHeight * 0.5f) + (nameFontSize * 0.36f));
        y += _titleLineHeight + _sectionGap;

        _barHealth = new Rect2(x, y, contentW, barH);
        y += barH + barGap;
        _barMana = new Rect2(x, y, contentW, barH);
        y += barH + barGap;
        _barStamina = new Rect2(x, y, contentW, barH);
        y += barH + _sectionGap;
        _statOrigin = new Vector2(x, y);

        // 技能条：5 格固定，锚定右下角
        var cell = skin.SkillCellSize * s;
        var spacing = skin.SkillCellSpacing * s;
        var stripPad = skin.SkillStripPadding * s;
        var stripW = (cell * SkillCount) + (spacing * (SkillCount - 1)) + (stripPad.X * 2f);
        var stripH = cell + (stripPad.Y * 2f);
        var anchor = skin.SkillAnchorOffset * s;
        var stripPos = new Vector2(viewSize.X - anchor.X - stripW, viewSize.Y - anchor.Y - stripH);
        _skillStrip = new Rect2(stripPos, new Vector2(stripW, stripH));
        _skillCell0 = new Vector2(stripPos.X + stripPad.X, stripPos.Y + stripPad.Y);
        _skillCellStep = cell + spacing;
        _skillCellSize = cell;
    }

    /// <summary>字号缩放：四舍五入到整数后至少 1px（0 号字在 Godot 里等于不可见）。</summary>
    private static int ScaleFontSize(int fontSize, float scale)
    {
        var scaled = (int)MathF.Round(fontSize * scale);
        return scaled < 1 ? 1 : scaled;
    }

    // -------------------------------------------------------------------------
    // 6.3 左上：贴纸角色面板
    // -------------------------------------------------------------------------

    private void DrawCharacterPanel(in DrawCtx ctx)
    {
        var skin = ctx.Skin;

        // 三层贴纸：硬边投影 → 暖白描边 → 深色填充（顺序不能换）
        DrawStickerPanel(ctx, _panelRect, ctx.Px(skin.PanelCornerRadius), ctx.Px(skin.PanelBorderWidth), skin.PanelShadowOffset * ctx.Scale);

        DrawTitleLine(ctx);

        if (DebugDrawBoxesOnly)
        {
            var r = skin.BarHeight * ctx.Scale * skin.BarCornerRatio;
            ctx.DrawRoundedRect(_barHealth, r, skin.BarTrack);
            ctx.DrawRoundedRect(_barMana, r, skin.BarTrack);
            ctx.DrawRoundedRect(_barStamina, r, skin.BarTrack);
            return;
        }

        var pulse = Sine01(_presentationSeconds, skin.HealthLowPulsePeriod);
        var flash = skin.HealthFlashDuration > 0f ? _flashRemaining / skin.HealthFlashDuration : 0f;

        DrawResourceBar(ctx, _barHealth, _snapshot.Health,
            ResolveBarColor(skin, skin.HealthColor, _snapshot.Health.Ratio, true, flash, pulse), BarKind.Health);
        DrawResourceBar(ctx, _barMana, _snapshot.Mana,
            ResolveBarColor(skin, skin.ManaColor, _snapshot.Mana.Ratio, false, 0f, 0f), BarKind.Mana);
        DrawResourceBar(ctx, _barStamina, _snapshot.Stamina,
            ResolveBarColor(skin, skin.StaminaColor, _snapshot.Stamina.Ratio, false, 0f, 0f), BarKind.Stamina);

        DrawStatLine(ctx);
    }

    private void DrawTitleLine(in DrawCtx ctx)
    {
        var skin = ctx.Skin;

        var nameWidth = 0f;
        var name = _snapshot.CharacterName;
        var nameFontSize = ctx.FontPx(skin.NameFontSize);
        var nameFont = ctx.SizedFont(nameFontSize);
        if (nameFont != null && !string.IsNullOrEmpty(name))
        {
            ctx.DrawTextOutlined(nameFont, _titleBaseline, name!, nameFontSize,
                skin.NameColor, ctx.FontPx(skin.TextOutlineSize), skin.TextOutlineColor);
            nameWidth = ctx.Measure(nameFont, name!, nameFontSize);
        }

        var tag = _snapshot.VariantTag;
        var tagFontSize = ctx.FontPx(skin.TagFontSize);
        var tagFont = ctx.SizedFont(tagFontSize);
        if (tagFont == null || string.IsNullOrEmpty(tag))
        {
            return;
        }

        var tagSize = ctx.MeasureSize(tagFont, tag!, tagFontSize);
        var badgeSize = new Vector2(tagSize.X + (_titleBadgePad.X * 2f), tagSize.Y + (_titleBadgePad.Y * 2f));
        var badge = new Rect2(
            new Vector2(_titleBaseline.X + nameWidth + _titleGap, _titleBaseline.Y - (nameFontSize * 0.36f) - (_titleLineHeight * 0.5f) + ((_titleLineHeight - badgeSize.Y) * 0.5f)),
            badgeSize);

        ctx.DrawRoundedRect(badge, badgeSize.Y * 0.38f, skin.TagFill);
        ctx.DrawText(tagFont,
            new Vector2(badge.Position.X + _titleBadgePad.X, badge.Position.Y + (badgeSize.Y * 0.5f) + (tagFontSize * 0.36f)),
            tag!, tagFontSize, skin.TagColor);
    }

    private void DrawResourceBar(in DrawCtx ctx, Rect2 rect, in HudBar bar, Color fill, BarKind kind)
    {
        var skin = ctx.Skin;
        var radius = rect.Size.Y * skin.BarCornerRatio;

        // 槽
        ctx.DrawRoundedRect(rect, radius, skin.BarTrack);

        var ratio = bar.Ratio;
        if (ratio > 0f)
        {
            var fillWidth = rect.Size.X * ratio;
            if (fillWidth < 1f)
            {
                fillWidth = 1f;
            }

            var fillRect = new Rect2(rect.Position, new Vector2(fillWidth, rect.Size.Y));
            ctx.DrawRoundedRect(fillRect, MathF.Min(radius, fillWidth * 0.5f), fill);

            // 内高光：顶部一道细亮带 = 「塑料圆条」的圆润感（玩具感的关键一笔）
            var highlightHeight = rect.Size.Y * skin.BarHighlightRatio;
            var inset = rect.Size.Y * 0.16f;
            var highlightWidth = fillWidth - (inset * 2f);
            if (highlightHeight > 0.5f && highlightWidth > 1f)
            {
                var highlight = new Rect2(
                    new Vector2(fillRect.Position.X + inset, fillRect.Position.Y + (rect.Size.Y * 0.12f)),
                    new Vector2(highlightWidth, highlightHeight * 0.45f));
                ctx.DrawRoundedRect(highlight, highlight.Size.Y * 0.5f, skin.BarHighlight);
            }
        }

        if (!ShowBarValues)
        {
            return;
        }

        var valueFontSize = ctx.FontPx(skin.ValueFontSize);
        var valueFont = ctx.SizedFont(valueFontSize);
        if (valueFont == null)
        {
            return;
        }

        var text = BarText(kind, bar);
        var width = ctx.Measure(valueFont, text, valueFontSize);
        var pos = new Vector2(
            rect.Position.X + rect.Size.X - width - (rect.Size.Y * 0.45f),
            rect.Position.Y + (rect.Size.Y * 0.5f) + (valueFontSize * 0.36f));
        ctx.DrawTextOutlined(valueFont, pos, text, valueFontSize,
            skin.ValueTextColor, ctx.FontPx(skin.TextOutlineSize), skin.TextOutlineColor);
    }

    private void DrawStatLine(in DrawCtx ctx)
    {
        var skin = ctx.Skin;
        var statFontSize = ctx.FontPx(skin.StatFontSize);
        var font = ctx.SizedFont(statFontSize);
        if (font == null)
        {
            return;
        }

        var baselineY = _statOrigin.Y + (_statLineHeight * 0.5f) + (statFontSize * 0.36f);
        var gap = statFontSize * 0.7f;
        var sepGap = statFontSize * 0.45f;
        var sepColor = new Color(
            skin.StatLabelColor.R, skin.StatLabelColor.G, skin.StatLabelColor.B,
            skin.StatLabelColor.A * 0.5f);

        // 等级右对齐，左边三段从左往右排
        var levelValue = FormatLevel(_snapshot.Level);
        var levelLabel = "Lv";
        var levelWidth = ctx.Measure(font, levelLabel, statFontSize)
                         + (statFontSize * 0.28f)
                         + ctx.Measure(font, levelValue, statFontSize);
        var levelX = _statOrigin.X + (skin.BarWidth * ctx.Scale) - levelWidth;

        var x = _statOrigin.X;

        // 护甲 12(+8%)
        x = DrawStatPair(ctx, font, x, baselineY, "ARM", ArmorText, skin.StatValueColor);
        x = DrawSeparator(ctx, font, x, baselineY, DotSeparator, sepColor, gap, sepGap);

        // 移速 6.0
        x = DrawStatPair(ctx, font, x, baselineY, "SPD", SpeedText, skin.StatValueColor);
        x = DrawSeparator(ctx, font, x, baselineY, DotSeparator, sepColor, gap, sepGap);

        // 暴击 15.0% × 2.0x（只有暴率用强调色，倍率保持中性 —— 一个面板里高饱和色不超过 3 个）
        x = DrawStatPair(ctx, font, x, baselineY, "CRIT", CritChanceText, skin.TagColor);
        x = DrawSeparator(ctx, font, x, baselineY, CrossSeparator, sepColor, gap, sepGap);
        x = DrawStatPair(ctx, font, x, baselineY, Empty, CritMultiplierText, skin.StatValueColor);

        if (levelX > x + gap)
        {
            DrawStatPair(ctx, font, levelX, baselineY, levelLabel, levelValue, skin.TagColor);
        }
    }

    private float DrawStatPair(in DrawCtx ctx, Font font, float x, float baselineY, string label, string value, Color valueColor)
    {
        var skin = ctx.Skin;
        var statFontSize = ctx.FontPx(skin.StatFontSize);
        var cursor = x;
        if (label.Length > 0)
        {
            ctx.DrawText(font, new Vector2(cursor, baselineY), label, statFontSize, skin.StatLabelColor);
            cursor += ctx.Measure(font, label, statFontSize);
        }

        if (value.Length == 0)
        {
            return cursor;
        }

        cursor += statFontSize * 0.28f;
        ctx.DrawTextOutlined(font, new Vector2(cursor, baselineY), value, statFontSize,
            valueColor, ctx.FontPx(skin.TextOutlineSize), skin.TextOutlineColor);
        return cursor + ctx.Measure(font, value, statFontSize);
    }

    private float DrawSeparator(in DrawCtx ctx, Font font, float x, float baselineY, string glyph, Color color, float gap, float sepGap)
    {
        var statFontSize = ctx.FontPx(ctx.Skin.StatFontSize);
        var gx = x + gap;
        ctx.DrawText(font, new Vector2(gx, baselineY), glyph, statFontSize, color);
        return gx + ctx.Measure(font, glyph, statFontSize) + sepGap;
    }

    // -------------------------------------------------------------------------
    // 6.4 右下：技能条
    // -------------------------------------------------------------------------

    private void DrawSkillBar(in DrawCtx ctx)
    {
        var skin = ctx.Skin;
        var slots = _snapshot.Skills;
        if (slots == null || slots.Length < SkillCount)
        {
            return;
        }

        DrawStickerPanel(ctx, _skillStrip, ctx.Px(skin.SkillCornerRadius), ctx.Px(skin.SkillBorderWidth), skin.SkillShadowOffset * ctx.Scale);

        var pulse = Sine01(_presentationSeconds, skin.SkillReadyPulsePeriod);
        var cell = _skillCellSize;
        var step = _skillCellStep;

        for (var i = 0; i < SkillCount; i++)
        {
            var rect = new Rect2(new Vector2(_skillCell0.X + (step * i), _skillCell0.Y), new Vector2(cell, cell));
            DrawSkillCell(ctx, rect, in slots[i], pulse);
        }
    }

    private void DrawSkillCell(in DrawCtx ctx, Rect2 rect, in HudSkillSlot slot, float readyPulse)
    {
        var skin = ctx.Skin;
        var radius = ctx.Px(skin.SkillCornerRadius);
        var border = ctx.Px(skin.SkillBorderWidth);

        // 投影 → 描边 → 填充（和贴纸面板同一套语法，所以技能格看起来像「贴纸方片」）
        if (skin.SkillShadowOffset.LengthSquared() > 0f)
        {
            ctx.DrawRoundedRect(new Rect2(rect.Position + (skin.SkillShadowOffset * ctx.Scale), rect.Size), radius, skin.PanelShadow);
        }

        ResolveSkillColors(skin, slot, readyPulse, out var fill, out var borderColor);
        ctx.DrawRoundedRect(rect, radius, borderColor);

        var inset = MathF.Min(border, MathF.Min(rect.Size.X, rect.Size.Y) * 0.5f);
        var inner = new Rect2(
            rect.Position + new Vector2(inset, inset),
            rect.Size - new Vector2(inset * 2f, inset * 2f));
        ctx.DrawRoundedRect(inner, MathF.Max(radius - inset, 0f), fill);

        var cooling = slot.HasSkill && !slot.Ready;

        // 冷却遮罩先画（字形压在遮罩上才读得出）
        if (cooling)
        {
            var arc = CooldownArcRadians(slot.CooldownProgress);
            if (arc > 0f)
            {
                ctx.DrawCooldownWedge(inner, arc, skin.SkillCooldownMask);
            }
        }

        if (DebugDrawBoxesOnly)
        {
            return;
        }

        var centerX = rect.Position.X + (rect.Size.X * 0.5f);
        var centerY = rect.Position.Y + (rect.Size.Y * 0.5f);

        // 字形（居中）
        var glyph = slot.Glyph;
        var glyphFontSize = ctx.FontPx(skin.SkillGlyphFontSize);
        var glyphFont = ctx.SizedFont(glyphFontSize);
        if (glyphFont != null && !string.IsNullOrEmpty(glyph))
        {
            var glyphColor = cooling ? skin.SkillGlyphDimColor : skin.SkillGlyphColor;
            var glyphWidth = ctx.Measure(glyphFont, glyph!, glyphFontSize);
            ctx.DrawTextOutlined(glyphFont,
                new Vector2(centerX - (glyphWidth * 0.5f), centerY + (glyphFontSize * 0.36f)),
                glyph!, glyphFontSize, glyphColor, ctx.FontPx(skin.TextOutlineSize), skin.TextOutlineColor);
        }

        // 冷却中：格子下缘的剩余秒数（DTO 的 RemainingSeconds，由宿主算好）
        if (cooling && slot.RemainingSeconds > 0f)
        {
            var cdFontSize = ctx.FontPx(skin.SkillCountdownFontSize);
            var cdFont = ctx.SizedFont(cdFontSize);
            if (cdFont != null)
            {
                var text = CountdownText(slot.RemainingSeconds);
                var textWidth = ctx.Measure(cdFont, text, cdFontSize);
                var textY = rect.Position.Y + rect.Size.Y - (cdFontSize * 0.4f) - (border * 1.4f);
                ctx.DrawTextOutlined(cdFont, new Vector2(centerX - (textWidth * 0.5f), textY), text,
                    cdFontSize, skin.SkillCountdownColor, ctx.FontPx(skin.TextOutlineSize), skin.TextOutlineColor);
            }
        }

        // 按键标签：左下角小暗胶囊（永远压在格子最上层，保证可读）
        var key = slot.KeyLabel;
        var keyFontSize = ctx.FontPx(skin.SkillKeyFontSize);
        var keyFont = ctx.SizedFont(keyFontSize);
        if (!ShowSkillKeyLabels || keyFont == null || string.IsNullOrEmpty(key))
        {
            return;
        }

        var keySize = ctx.MeasureSize(keyFont, key!, keyFontSize);
        var keyPad = new Vector2(keyFontSize * 0.4f, keyFontSize * 0.1f);
        var badgeSize = new Vector2(keySize.X + (keyPad.X * 2f), keySize.Y + (keyPad.Y * 2f));
        var badge = new Rect2(
            new Vector2(rect.Position.X + border, rect.Position.Y + rect.Size.Y - border - badgeSize.Y),
            badgeSize);

        ctx.DrawRoundedRect(badge, ctx.Px(skin.SkillKeyBadgeRadius), skin.SkillKeyFill);
        ctx.DrawText(keyFont,
            new Vector2(badge.Position.X + keyPad.X, badge.Position.Y + (badgeSize.Y * 0.5f) + (keyFontSize * 0.36f)),
            key!, keyFontSize, skin.SkillKeyColor);
    }

    // -------------------------------------------------------------------------
    // 6.5 屏幕中心准星
    // -------------------------------------------------------------------------

    private void DrawCrosshair(in DrawCtx ctx, Vector2 viewSize)
    {
        var skin = ctx.Skin;

        // 结构性常量：准星就在屏幕正中（皮肤只配「长什么样」，不配「在哪」）
        var center = new Vector2(viewSize.X * 0.5f, viewSize.Y * 0.5f);

        var outlineWidth = ctx.Px(skin.CrosshairOutlineWidth);
        var lineWidth = ctx.Px(skin.CrosshairLineWidth);
        var length = ctx.Px(skin.CrosshairLineLength);
        var gap = ctx.Px(skin.CrosshairGap);
        var outlineColor = skin.CrosshairOutlineColor;
        var lineColor = skin.CrosshairColor;

        // 四条臂：先描边（粗、深）再叠主线（细、半透明白）→ 任何背景上都能看见
        DrawCrosshairArm(ctx, new Vector2(center.X - gap - length, center.Y), new Vector2(center.X - gap, center.Y), outlineColor, lineColor, lineWidth + outlineWidth, lineWidth);
        DrawCrosshairArm(ctx, new Vector2(center.X + gap, center.Y), new Vector2(center.X + gap + length, center.Y), outlineColor, lineColor, lineWidth + outlineWidth, lineWidth);
        DrawCrosshairArm(ctx, new Vector2(center.X, center.Y - gap - length), new Vector2(center.X, center.Y - gap), outlineColor, lineColor, lineWidth + outlineWidth, lineWidth);
        DrawCrosshairArm(ctx, new Vector2(center.X, center.Y + gap), new Vector2(center.X, center.Y + gap + length), outlineColor, lineColor, lineWidth + outlineWidth, lineWidth);

        var dotRadius = ctx.Px(skin.CrosshairDotRadius);
        if (dotRadius > 0f)
        {
            ctx.DrawCircle(center, dotRadius + (outlineWidth * 0.5f), outlineColor);
            ctx.DrawCircle(center, dotRadius, lineColor);
        }

        // 朝向指示：沿 AimAngleDeg 从外圈伸出一条短线 + 三角头
        var aimLength = ctx.Px(skin.CrosshairAimLength);
        if (!(aimLength > 0f))
        {
            return;
        }

        var radians = Mathf.DegToRad(_snapshot.AimAngleDeg);
        var dir = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var perp = new Vector2(-dir.Y, dir.X);
        var start = center + (dir * (gap + length + (lineWidth * 1.5f)));
        var tip = start + (dir * aimLength);

        DrawCrosshairArm(ctx, start, tip, outlineColor, lineColor, lineWidth + outlineWidth, lineWidth);

        var head = ctx.Px(skin.CrosshairAimHead);
        if (!(head > 0f))
        {
            return;
        }

        var basePoint = tip - (dir * head);
        var halfWidth = head * 0.75f;
        var o = outlineWidth * 0.5f;
        var tipOutlined = tip + (dir * o);
        var baseOutlined = tip - (dir * (head + o));
        ctx.DrawTriangle(tipOutlined, baseOutlined + (perp * (halfWidth + o)), baseOutlined - (perp * (halfWidth + o)), outlineColor);
        ctx.DrawTriangle(tip, basePoint + (perp * halfWidth), basePoint - (perp * halfWidth), lineColor);
    }

    private static void DrawCrosshairArm(in DrawCtx ctx, Vector2 from, Vector2 to, Color outline, Color line, float outlineThickness, float lineThickness)
    {
        if (outlineThickness > 0f)
        {
            ctx.DrawLine(from, to, outline, outlineThickness);
        }

        if (lineThickness > 0f)
        {
            ctx.DrawLine(from, to, line, lineThickness);
        }
    }

    // -------------------------------------------------------------------------
    // 6.6 贴纸底板
    // -------------------------------------------------------------------------

    /// <summary>贴纸底板：硬边深色投影（整块偏移、纯色，不做模糊）+ 描边 + 填充。这就是「贴纸」的全部秘密。</summary>
    private static void DrawStickerPanel(in DrawCtx ctx, Rect2 rect, float radius, float borderWidth, Vector2 shadowOffset)
    {
        var skin = ctx.Skin;

        if (shadowOffset.LengthSquared() > 0f)
        {
            ctx.DrawRoundedRect(new Rect2(rect.Position + shadowOffset, rect.Size), radius, skin.PanelShadow);
        }

        ctx.DrawRoundedRect(rect, radius, skin.PanelBorder);

        var inset = MathF.Min(borderWidth, MathF.Min(rect.Size.X, rect.Size.Y) * 0.5f);
        if (inset > 0f)
        {
            ctx.DrawRoundedRect(
                new Rect2(rect.Position + new Vector2(inset, inset), rect.Size - new Vector2(inset * 2f, inset * 2f)),
                MathF.Max(radius - inset, 0f),
                skin.PanelFill);
        }
    }

    // =========================================================================
    // 7. 文本缓存（稳态每帧 0 次格式化）
    // =========================================================================

    private enum BarKind
    {
        Health = 0,
        Mana = 1,
        Stamina = 2,
    }

    private readonly string[] _barTextCache = { Empty, Empty, Empty };
    private readonly int[] _barCacheValue = { int.MinValue, int.MinValue, int.MinValue };
    private readonly int[] _barCacheMax = { int.MinValue, int.MinValue, int.MinValue };

    private string _armorText = Empty;
    private string _speedText = Empty;
    private string _critChanceText = Empty;
    private string _critMultiplierText = Empty;
    private string _countdownText = Empty;

    private int _armorCacheValue = int.MinValue;
    private int _armorCachePercent = int.MinValue;
    private int _speedCacheTenths = int.MinValue;
    private int _critCachePermille = int.MinValue;
    private int _critMultiplierCacheTenths = int.MinValue;
    private int _countdownCacheTenths = int.MinValue;

    private string BarText(BarKind kind, in HudBar bar)
    {
        var i = (int)kind;
        var v = bar.Value > 0f ? (int)MathF.Ceiling(bar.Value) : 0;
        var m = bar.Max > 0f ? (int)MathF.Round(bar.Max) : 0;
        if (v != _barCacheValue[i] || m != _barCacheMax[i])
        {
            _barCacheValue[i] = v;
            _barCacheMax[i] = m;
            _barTextCache[i] = FormatBarValue(bar.Value, bar.Max);
        }

        return _barTextCache[i];
    }

    private string ArmorText
    {
        get
        {
            var a = (int)MathF.Round(_shownArmor);
            var p = (int)MathF.Round(_snapshot.ArmorReduction * 100f);
            if (a != _armorCacheValue || p != _armorCachePercent)
            {
                _armorCacheValue = a;
                _armorCachePercent = p;
                _armorText = FormatArmor(_shownArmor, _snapshot.ArmorReduction);
            }

            return _armorText;
        }
    }

    private string SpeedText
    {
        get
        {
            var tenths = (int)MathF.Round(_shownSpeed * 10f);
            if (tenths != _speedCacheTenths)
            {
                _speedCacheTenths = tenths;
                _speedText = FormatSpeed(tenths / 10f);
            }

            return _speedText;
        }
    }

    private string CritChanceText
    {
        get
        {
            var permille = (int)MathF.Round(_shownCritChance * 1000f);
            if (permille != _critCachePermille)
            {
                _critCachePermille = permille;
                _critChanceText = FormatCritChance(permille / 1000f);
            }

            return _critChanceText;
        }
    }

    private string CritMultiplierText
    {
        get
        {
            var tenths = (int)MathF.Round(_shownCritMultiplier * 10f);
            if (tenths != _critMultiplierCacheTenths)
            {
                _critMultiplierCacheTenths = tenths;
                _critMultiplierText = FormatCritMultiplier(tenths / 10f);
            }

            return _critMultiplierText;
        }
    }

    private string CountdownText(float remainingSeconds)
    {
        var tenths = (int)MathF.Ceiling(remainingSeconds * 10f);
        if (tenths != _countdownCacheTenths)
        {
            _countdownCacheTenths = tenths;
            _countdownText = FormatSeconds(tenths / 10f);
        }

        return _countdownText;
    }

    private void InvalidateTextCache()
    {
        for (var i = 0; i < _barCacheValue.Length; i++)
        {
            _barCacheValue[i] = int.MinValue;
            _barCacheMax[i] = int.MinValue;
        }

        _armorCacheValue = int.MinValue;
        _armorCachePercent = int.MinValue;
        _speedCacheTenths = int.MinValue;
        _critCachePermille = int.MinValue;
        _critMultiplierCacheTenths = int.MinValue;
        _countdownCacheTenths = int.MinValue;
    }

    // =========================================================================
    // 8. 皮肤 / 字体缓存
    // =========================================================================

    private void EnsureSkinCached(CharacterHudSkin skin)
    {
        if (ReferenceEquals(_cachedSkin, skin))
        {
            return;
        }

        _cachedSkin = skin;
        _font = skin.Font ?? ThemeDB.FallbackFont;
        _geometrySkin = null;
    }

    // =========================================================================
    // 9. 工具
    // =========================================================================

    /// <summary>0..1 的正弦脉动；周期 ≤ 0 时返回 0（= 关闭脉动）。</summary>
    private float Sine01(float seconds, float period)
    {
        if (!(period > 0f))
        {
            return 0f;
        }

        return 0.5f + (0.5f * MathF.Sin(seconds * Mathf.Tau / period));
    }

    // =========================================================================
    // 10. 绘制上下文（readonly struct，零分配）
    // =========================================================================

    /// <summary>
    /// 绘制门面：把 Control 的 <c>Draw*</c>、皮肤、缩放色与**复用的点集 / 描边盒**打包成值类型传下去。
    /// <para>
    /// 字号不在这里设：所有 <c>DrawString</c>/<c>GetStringSize</c> 都显式传 size，
    /// 因此同一帧里用同一个字体对象就能画多种字号，不需要为每个字号建 <see cref="FontVariation"/>。
    /// </para>
    /// </summary>
    private readonly struct DrawCtx
    {
        private readonly CanvasItem _canvas;
        private readonly Color _tint;
        private readonly Vector2[] _points;
        private readonly Color[] _uvs;
        private readonly StyleBoxFlat _box;

        public readonly CharacterHudSkin Skin;

        /// <summary>
        /// 像素缩放系数（= 视口高 / 皮肤参考高，已按皮肤上下限夹取）。
        /// 皮肤里的所有 px 尺寸都是「参考分辨率下的设计值」，绘制时乘它得到物理像素。
        /// </summary>
        public readonly float Scale;

        public DrawCtx(CanvasItem canvas, CharacterHudSkin skin, Color tint, Vector2[] points, Color[] uvs, StyleBoxFlat box, float scale)
        {
            _canvas = canvas;
            Skin = skin;
            _tint = tint;
            _points = points;
            _uvs = uvs;
            _box = box;
            Scale = scale;
        }

        // ---- 尺寸换算 ----

        /// <summary>把皮肤里的设计像素值换算成当前实际像素。</summary>
        public float Px(float designPixels)
        {
            return designPixels * Scale;
        }

        /// <summary>把设计字号换算成整数实际字号（至少 1px）。</summary>
        public int FontPx(int designFontSize)
        {
            var scaled = (int)MathF.Round(designFontSize * Scale);
            return scaled < 1 ? 1 : scaled;
        }

        // ---- 字体 / 度量 ----

        /// <summary>取字体对象。皮肤没配字体时退回引擎默认字体（「忘记挂字体」也不会一片空白）。</summary>
        public Font? SizedFont(int size)
        {
            _ = size;
            return Skin.Font ?? ThemeDB.FallbackFont;
        }

        public float Measure(Font font, string text, int size)
        {
            return font.GetStringSize(text, HorizontalAlignment.Left, -1f, size).X;
        }

        public Vector2 MeasureSize(Font font, string text, int size)
        {
            return font.GetStringSize(text, HorizontalAlignment.Left, -1f, size);
        }

        // ---- 基础图元 ----

        public void DrawLine(Vector2 from, Vector2 to, Color color, float width)
        {
            if (!(width > 0f))
            {
                return;
            }

            _canvas.DrawLine(from, to, color * _tint, width, true);
        }

        public void DrawCircle(Vector2 center, float radius, Color color)
        {
            if (!(radius > 0f))
            {
                return;
            }

            _canvas.DrawCircle(center, radius, color * _tint);
        }

        public void DrawTriangle(Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            var tinted = color * _tint;
            _points[0] = a;
            _points[1] = b;
            _points[2] = c;
            for (var i = 0; i < 3; i++)
            {
                _uvs[i] = tinted;
            }

            _canvas.DrawPolygon(_points.AsSpan(0, 3), _uvs.AsSpan(0, 3), ReadOnlySpan<Vector2>.Empty, null);
        }

        public void DrawText(Font font, Vector2 pos, string text, int size, Color color)
        {
            _canvas.DrawString(font, pos, text, HorizontalAlignment.Left, -1f, size, color * _tint);
        }

        public void DrawTextOutlined(Font font, Vector2 pos, string text, int size, Color color, int outlineSize, Color outlineColor)
        {
            if (outlineSize > 0)
            {
                _canvas.DrawStringOutline(font, pos, text, HorizontalAlignment.Left, -1f, size, outlineSize, outlineColor * _tint);
            }

            _canvas.DrawString(font, pos, text, HorizontalAlignment.Left, -1f, size, color * _tint);
        }

        // ---- 圆角矩形（复用 StyleBoxFlat：它是 Resource 句柄，只写字段不 new）----

        public void DrawRoundedRect(Rect2 rect, float radius, Color color)
        {
            if (rect.Size.X <= 0f || rect.Size.Y <= 0f)
            {
                return;
            }

            var tinted = color * _tint;
            if (tinted.A <= 0f)
            {
                return;
            }

            var rounded = (int)MathF.Round(radius);
            var maxRadius = (int)MathF.Floor(MathF.Min(rect.Size.X, rect.Size.Y) * 0.5f);
            if (rounded > maxRadius)
            {
                rounded = maxRadius;
            }

            if (rounded < 0)
            {
                rounded = 0;
            }

            _box.BgColor = tinted;
            _box.SetBorderWidthAll(0);
            _box.CornerRadiusTopLeft = rounded;
            _box.CornerRadiusTopRight = rounded;
            _box.CornerRadiusBottomRight = rounded;
            _box.CornerRadiusBottomLeft = rounded;
            _canvas.DrawStyleBox(_box, rect);
        }

        /// <summary>冷却扇形：从 12 点方向顺时针扫过 <paramref name="arc"/> 弧度（扇心 + 弧点，凹多边形一次提交）。</summary>
        public void DrawCooldownWedge(Rect2 rect, float arc, Color color)
        {
            if (!(arc > 0f))
            {
                return;
            }

            var tinted = color * _tint;
            if (tinted.A <= 0f)
            {
                return;
            }

            // 全遮：直接铺满整格，避免扇心堆点产生锯齿
            if (arc >= Mathf.Tau - 0.001f)
            {
                DrawRoundedRect(rect, MathF.Min(rect.Size.X, rect.Size.Y) * 0.2f, color);
                return;
            }

            var center = rect.Position + (rect.Size * 0.5f);
            var radius = MathF.Sqrt((rect.Size.X * rect.Size.X) + (rect.Size.Y * rect.Size.Y)) * 0.5f;

            var segments = (int)MathF.Ceiling(arc * 8f);
            if (segments < 3)
            {
                segments = 3;
            }
            else if (segments > WedgeSegments - 1)
            {
                segments = WedgeSegments - 1;
            }

            var startAngle = -Mathf.Pi * 0.5f;
            _points[0] = center;
            _uvs[0] = tinted;

            var count = 1;
            for (var i = 0; i <= segments; i++)
            {
                var a = startAngle + (arc * i / segments);
                _points[count] = center + (new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius);
                _uvs[count] = tinted;
                count++;
            }

            _canvas.DrawPolygon(_points.AsSpan(0, count), _uvs.AsSpan(0, count), ReadOnlySpan<Vector2>.Empty, null);
        }
    }
}
