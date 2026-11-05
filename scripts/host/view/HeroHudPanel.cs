using System;
using Game.UI;
using Godot;

namespace Game.Host.Presentation;

/// <summary>
/// HUD 绘制的**最小接缝**：宿主层只认「能把一份 <see cref="HudSnapshot"/> 画出来」这件能力。
/// <para>
/// <b>为什么不直接依赖 <c>Game.UI.CharacterHudDrawer</c></b>：Drawer 由 ui 会话交付，宿主层在它落地前
/// 也要能编译（并行开发）。定义接口后：① 宿主只认接口，Drawer 的类名/构造怎么变都不影响 <c>scripts/host</c>；
/// ② 测试可以塞一个只记调用的假 Drawer 验证「每帧恰好提交一次快照」；③ <c>using Game.UI</c> 仍只在
/// <c>scripts/host</c>（红线：UI 不许反向依赖 Host）。
/// </para>
/// <para>
/// <b>适配方式</b>：<see cref="CharacterHudDrawerTarget"/> 把任意实现了 <see cref="ISubmitHudSnapshot"/>
/// 的 Control 包成 <see cref="IHeroHudTarget"/>；若 ui 会话的 Drawer <u>没有</u>实现这个接口
/// （它的公开签名是 <c>Submit(in HudSnapshot)</c> 之类），用
/// <see cref="CharacterHudDrawerTarget.FromDelegate"/> 传一个委托适配即可 —— 宿主层无需改动。
/// </para>
/// </summary>
public interface IHeroHudTarget
{
    /// <summary>提交一帧快照（宿主每帧调用一次；失败不得抛异常把帧带崩）。</summary>
    /// <param name="snapshot">本帧 HUD 数据（只读）。</param>
    void Submit(in HudSnapshot snapshot);
}

/// <summary>
/// 把「实现了 <see cref="ISubmitHudSnapshot"/> 的 Control（典型是 ui 会话的 <c>CharacterHudDrawer</c>）」
/// 适配成 <see cref="IHeroHudTarget"/>。
/// <para>
/// <b>为什么用 <see cref="Node.GetNodeOrNull{T}"/></b>：Drawer 可能已经是场景里的子节点，
/// 找不到就自己建一个挂上去 —— 这样 <c>HeroHudPanel</c> 既支持「Lead 注入现成实例」，也支持「按需自建」。
/// </para>
/// </summary>
public sealed class CharacterHudDrawerTarget : IHeroHudTarget
{
    private readonly ISubmitHudSnapshot _drawer;

    /// <summary>包装一个 Drawer。</summary>
    /// <param name="drawer">实现 <see cref="ISubmitHudSnapshot"/> 的节点。</param>
    public CharacterHudDrawerTarget(ISubmitHudSnapshot drawer)
    {
        _drawer = drawer ?? throw new ArgumentNullException(nameof(drawer));
    }

    /// <inheritdoc />
    public void Submit(in HudSnapshot snapshot) => _drawer.Submit(snapshot);

    /// <summary>
    /// 适配现成的 <c>Game.UI.CharacterHudDrawer</c>（它的公开签名是 <c>Submit(in HudSnapshot)</c>）。
    /// <para>
    /// <b>为什么单独一个工厂</b>：Drawer 不实现本文件的 <see cref="ISubmitHudSnapshot"/>（它是 ui 会话的类，
    /// 不该反向依赖宿主层的接口）。这里直接按它已有的签名接一个委托，Lead 接线只需一行：
    /// <c>new CharacterHudDrawerTarget(drawer)</c>。若将来 Drawer 改了签名/名字，只改这一个方法。
    /// </para>
    /// </summary>
    /// <param name="drawer">ui 会话的 HUD 绘制节点。</param>
    public static IHeroHudTarget ForDrawer(Game.UI.CharacterHudDrawer drawer)
    {
        if (drawer is null)
        {
            throw new ArgumentNullException(nameof(drawer));
        }
        return new DrawerAdapter(drawer);
    }

    /// <summary>
    /// 用委托适配一个「签名不同」的 Drawer（例：<c>drawer.Submit(in snapshot)</c> 或 <c>drawer.Push(snapshot)</c>）。
    /// <para>为什么需要它：Drawer 的公开签名由 ui 会话决定，宿主层不该为「参数是不是 <c>in</c>」写死。</para>
    /// </summary>
    /// <param name="submit">提交回调。</param>
    public static IHeroHudTarget FromDelegate(Action<HudSnapshot> submit)
    {
        if (submit is null)
        {
            throw new ArgumentNullException(nameof(submit));
        }
        return new DelegateHudTarget(submit);
    }

    /// <summary>委托适配器。</summary>
    private sealed class DelegateHudTarget : IHeroHudTarget
    {
        private readonly Action<HudSnapshot> _submit;

        /// <summary>构造委托适配器。</summary>
        public DelegateHudTarget(Action<HudSnapshot> submit) => _submit = submit;

        /// <inheritdoc />
        public void Submit(in HudSnapshot snapshot) => _submit(snapshot);
    }

    /// <summary>
    /// <c>Game.UI.CharacterHudDrawer</c> 的适配器：保留 <c>in HudSnapshot</c> 语义（不复制结构体）。
    /// </summary>
    private sealed class DrawerAdapter : IHeroHudTarget
    {
        private readonly Game.UI.CharacterHudDrawer _drawer;

        /// <summary>包装一个 Drawer。</summary>
        public DrawerAdapter(Game.UI.CharacterHudDrawer drawer) => _drawer = drawer;

        /// <inheritdoc />
        public void Submit(in HudSnapshot snapshot) => _drawer.Submit(snapshot);
    }
}

/// <summary>「能接一份快照」的最小接口（供 ui 会话的 <c>CharacterHudDrawer</c> 可选实现）。</summary>
public interface ISubmitHudSnapshot
{
    /// <summary>接一帧快照并重绘。</summary>
    /// <param name="snapshot">HUD 数据（只读）。</param>
    void Submit(in HudSnapshot snapshot);
}

/// <summary>
/// HUD 面板：一个 <see cref="CanvasLayer"/>，每帧把宿主算好的 <see cref="HudSnapshot"/> 转交给 Drawer。
/// <para>
/// <b>为什么这一层存在</b>（而不让 <c>CharacterSession</c> 直接调 Drawer）：
/// <list type="bullet">
///   <item>HUD 需要自己的 <see cref="CanvasLayer"/> 层级（画在 3D 之上、且独立于世界坐标）；</item>
///   <item>「快照从哪来」是宿主的事，「快照怎么画」是 UI 的事 —— 面板是两者唯一的接缝，
///         将来加飘字/小地图/背包只改这一层，不碰 Drawer 也不碰 <c>CharacterSession</c>（规则7）；</item>
///   <item><see cref="HeroHudPanel"/> 位于 <c>scripts/host</c>，是唯一允许 <c>using Game.UI</c> 的一侧；
///         <c>scripts/ui/**</c> 绝不反向 <c>using Game.Host</c>（红线 5 / J4）。</item>
/// </list>
/// </para>
/// <para>
/// <b>headless 安全</b>：<see cref="CanvasLayer"/> 在 headless 下同样可创建；若 Drawer 为 null 则
/// 只记账不绘制（不抛异常），这样无头冒烟能跑完整条链。
/// </para>
/// </summary>
public sealed partial class HeroHudPanel : CanvasLayer
{
    private IHeroHudTarget? _target;
    private bool _lastHadCharacter;

    /// <summary>
    /// 构造 HUD 面板（**尚未**加入场景树，由调用方 <c>AddChild</c>）。
    /// </summary>
    /// <param name="options">表现层参数（图层号 / 是否隐藏系统光标）。</param>
    /// <param name="target">绘制目标（可为 null：此时只记账不绘制，headless 冒烟用）。</param>
    public HeroHudPanel(GameViewOptions options, IHeroHudTarget? target)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        Name = "HeroHudPanel";
        Layer = options.HudCanvasLayer;
        _target = target;
        _hideOsCursor = options.HideOsCursor;
    }

    /// <summary>是否隐藏操作系统光标（由 <see cref="GameViewOptions.HideOsCursor"/> 决定）。</summary>
    private readonly bool _hideOsCursor;

    /// <summary>当前绘制目标（切 HUD 皮肤/重建 Drawer 时热替换）。</summary>
    public IHeroHudTarget? Target
    {
        get => _target;
        set => _target = value;
    }

    /// <summary>累计提交次数（诊断：应约等于帧数）。</summary>
    public int SubmitCount { get; private set; }

    /// <summary>最近一次提交的快照（只有宿主侧诊断读得到；Drawer 不持有它）。</summary>
    public HudSnapshot LastSnapshot { get; private set; }

    /// <summary>最近一次提交是否包含可显示角色。</summary>
    public bool LastHadCharacter => _lastHadCharacter;

    /// <inheritdoc />
    public override void _Ready()
    {
        // 隐藏系统光标：准星（world-space 环）替代它。headless 下 DisplayServer 会忽略该调用。
        if (_hideOsCursor && !IsHeadless())
        {
            Input.MouseMode = Input.MouseModeEnum.Hidden;
        }
    }

    /// <summary>
    /// 提交一帧快照（宿主在视图层每帧调用一次）。
    /// <para>
    /// <b>为什么吞掉 Drawer 的异常</b>：HUD 是表现层，绘制出错不该让整局逻辑停摆（ECS 已经推进完了）。
    /// 首次失败报一条错误，之后静默 —— 与 <c>ActorViewDirector</c> 的「只警告一次」同理。
    /// </para>
    /// </summary>
    /// <param name="snapshot">本帧 HUD 数据。</param>
    public void Submit(in HudSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        _lastHadCharacter = snapshot.HasCharacter;
        SubmitCount++;
        IHeroHudTarget? target = _target;
        if (target is null)
        {
            return;
        }
        try
        {
            target.Submit(snapshot);
        }
        catch (Exception ex) when (!_submitFailed)
        {
            _submitFailed = true;
            GD.PushError("[HeroHudPanel] HUD 绘制失败（后续同错静默）：" + ex.Message);
        }
    }

    /// <summary>是否已经报过绘制失败（保证只报一次）。</summary>
    private bool _submitFailed;

    /// <summary>是否处于无头模式（无 DisplayServer 时 <see cref="DisplayServer.GetName"/> 仍返回 "headless"）。</summary>
    private static bool IsHeadless()
    {
        return DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);
    }
}
