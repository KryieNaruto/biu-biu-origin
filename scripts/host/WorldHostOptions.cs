using System;
using Godot;

namespace Game.Host;

/// <summary>
/// 宿主层的**全部可调参数**：一帧的时钟、视图池规模、冒烟场景的初始实体配置。
/// <para>
/// <b>为什么这些数放在这里而不是写死在系统里</b>（AGENTS.md 红线 2 / 规则4）：
/// 子弹速度、寿命、生成半径都是**玩法数值**，代码里出现即违规。但本轮的 `data/excel/` 尚未落地
/// （ARCHITECTURE.md §6 的 PEND），没有 Excel 行可以承载它们。于是把它们提升为
/// <see cref="Resource"/>：① 可在 Inspector 里可视化改（规则4 的「插件可视化修改」路径）；
/// ② 场景 <c>.tres</c> 即数据文件，改数值 = 改资源，不改代码；③ Excel 落地后，只需在
/// <c>DataLayer</c> 里把本资源的字段按 Def 覆盖一遍，<b>系统侧零改动</b>。
/// </para>
/// <para>
/// <b>为什么是 Resource 而不是普通 C# class</b>：只有 <see cref="Resource"/> 能被 <c>[Export]</c>
/// 暴露到检查器、能被 <c>.tres</c> 序列化、能被策划在编辑器里调。纯 class 会被序列化成空对象。
/// 本文件位于 <c>scripts/host/</c>，是工程内允许接触引擎类型的边界层。
/// </para>
/// <para>
/// 纪律：除本文件与 <c>scripts/host/**</c> 外，任何业务层都不得引用这里的字段——系统只通过
/// 构造注入的普通 C# 参数拿数，从而保持「零引擎依赖」。
/// </para>
/// </summary>
[GlobalClass]
public partial class WorldHostOptions : Resource
{
    // ─────────────────────────────────────────────────────────────
    // 1. 时钟（固定步长累积器）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 逻辑固定步长频率（Hz）。<c>_fixedDt = 1 / FixedHz</c> 是喂给 FixedUpdate 组的唯一 dt。
    /// <para>默认 60：与服务端物理帧、常见刷新率对齐，也是 ECS.md §8「确定性」的基准。</para>
    /// </summary>
    [Export(PropertyHint.Range, "10,240,1")]
    public int FixedHz { get; set; } = FixedHzDefault;

    /// <summary>
    /// 单次 <c>_Process</c> 最多补几个固定步。卡顿/断点恢复后若累积了几百毫秒，
    /// 不设上限会一口气跑几百步，形成「越补越卡」的死亡螺旋。
    /// </summary>
    [Export(PropertyHint.Range, "1,16,1")]
    public int MaxFixedStepsPerFrame { get; set; } = MaxFixedStepsDefault;

    /// <summary>全局逻辑时间缩放（HitStop 的实现位置之一，ECS.md §6.4）。</summary>
    [Export(PropertyHint.Range, "0,4,0.01")]
    public float TimeScale { get; set; } = TimeScaleDefault;

    // ─────────────────────────────────────────────────────────────
    // 2. 视图桥
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 是否启用渲染插值。<c>true</c> 时 <see cref="TransformBridge"/> 在上一固定步与当前逻辑位置之间
    /// 用累积器余量 alpha 做 lerp；60Hz 逻辑在 144Hz 屏上仍然平滑。
    /// </summary>
    [Export]
    public bool EnableInterpolation { get; set; } = true;

    /// <summary>
    /// 相机（或原点）到这个距离之外的实体不再写 <c>GlobalTransform</c>，直接隐藏视图。
    /// <para>写节点矩阵是宿主层最贵的一次跨语言调用；剔除是把它降下来最直接的手段。</para>
    /// </summary>
    [Export(PropertyHint.Range, "0,500,1")]
    public float CullingDistance { get; set; } = CullingDistanceDefault;

    /// <summary>启动时一次性预租的视图数量：避免开局前几帧因为 Instantiate 造成卡顿。</summary>
    [Export(PropertyHint.Range, "0,4096,1")]
    public int ViewPrewarm { get; set; } = ViewPrewarmDefault;

    // ─────────────────────────────────────────────────────────────
    // 3. 冒烟场景的实体生成配置（Excel 落地前由本资源承载）
    // ─────────────────────────────────────────────────────────────

    /// <summary>启动时生成的「子弹」实体数。用于验证池化与批量查询。</summary>
    [Export(PropertyHint.Range, "0,4096,1")]
    public int InitialProjectileCount { get; set; } = InitialProjectilesDefault;

    /// <summary>子弹寿命（秒）。到点由 <c>LifetimeSystem</c> 投递销毁命令。</summary>
    [Export(PropertyHint.Range, "0.1,60,0.1")]
    public float ProjectileLifetimeSeconds { get; set; } = ProjectileLifetimeDefault;

    /// <summary>子弹速度区间下界（米/秒）。</summary>
    [Export(PropertyHint.Range, "0,100,0.1")]
    public float ProjectileSpeedMin { get; set; } = ProjectileSpeedMinDefault;

    /// <summary>子弹速度区间上界（米/秒）。</summary>
    [Export(PropertyHint.Range, "0,100,0.1")]
    public float ProjectileSpeedMax { get; set; } = ProjectileSpeedMaxDefault;

    /// <summary>子弹生成区的半径（米）：在以原点为中心的水平圆盘内均匀撒点。</summary>
    [Export(PropertyHint.Range, "0.5,200,0.5")]
    public float SpawnRadius { get; set; } = SpawnRadiusDefault;

    /// <summary>子弹生成高度（米）。</summary>
    [Export(PropertyHint.Range, "-50,50,0.1")]
    public float SpawnHeight { get; set; } = SpawnHeightDefault;

    /// <summary>子弹缩放（视觉尺寸，不影响判定）。</summary>
    [Export(PropertyHint.Range, "0.01,10,0.01")]
    public float ProjectileScale { get; set; } = ProjectileScaleDefault;

    // ─────────────────────────────────────────────────────────────
    // 4. 诊断
    // ─────────────────────────────────────────────────────────────

    /// <summary>每隔多少次刷新打印一次性能摘要（0 = 关闭）。</summary>
    [Export(PropertyHint.Range, "0,100000,1")]
    public int StatsIntervalFrames { get; set; } = StatsIntervalDefault;

    /// <summary>场景退出时的「跑通」判据：至少跑过这么多帧、且稳态没有致命错误。</summary>
    [Export(PropertyHint.Range, "1,100000,1")]
    public int MinFramesToPass { get; set; } = MinFramesDefault;

    // ─────────────────────────────────────────────────────────────
    // 结构性默认值（集中一处，便于 Excel 落地后统一替换）
    // ─────────────────────────────────────────────────────────────

    /// <summary>固定步长默认频率。</summary>
    public const int FixedHzDefault = 60;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>单帧最大补步数默认值。</summary>
    public const int MaxFixedStepsDefault = 4;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>时间缩放默认值（1 = 实时）。</summary>
    public const float TimeScaleDefault = 1f;

    /// <summary>剔除距离默认值（米）。</summary>
    public const float CullingDistanceDefault = 200f;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>视图预热默认值。</summary>
    public const int ViewPrewarmDefault = 64;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>初始子弹数默认值。</summary>
    public const int InitialProjectilesDefault = 120;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>子弹寿命默认值（秒）。</summary>
    public const float ProjectileLifetimeDefault = 6f;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>子弹速度下界默认值（米/秒）。</summary>
    public const float ProjectileSpeedMinDefault = 2f;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>子弹速度上界默认值（米/秒）。</summary>
    public const float ProjectileSpeedMaxDefault = 5f;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>生成区半径默认值（米）。</summary>
    public const float SpawnRadiusDefault = 12f;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>生成高度默认值（米）。</summary>
    public const float SpawnHeightDefault = 1f;

    /// <summary>子弹缩放默认值。</summary>
    public const float ProjectileScaleDefault = 0.25f;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>统计打印间隔默认值（帧）。</summary>
    public const int StatsIntervalDefault = 60;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>判定跑通的最少帧数默认值。</summary>
    public const int MinFramesDefault = 120;  // arch-ok: Excel 落地前的默认值，由 Inspector/Resource 覆盖（不是硬编码玩法数值）

    /// <summary>固定步长秒数（由 <see cref="FixedHz"/> 派生）。</summary>
    public float FixedDeltaSeconds => FixedHz > 0 ? 1f / FixedHz : 0f;

    /// <summary>
    /// 参数自检：把「配置错」在 <c>_Ready</c> 当场变成一条明确的中文错误，
    /// 而不是让它变成运行几百帧后的一次除零 / 空查询 / 引擎崩溃。
    /// </summary>
    public void Validate()
    {
        if (FixedHz <= 0)
        {
            throw new InvalidOperationException(
                string.Concat(nameof(WorldHostOptions), ".", nameof(FixedHz), " 必须大于 0，实际为 ", FixedHz.ToString(), "。"));
        }
        if (ProjectileSpeedMin > ProjectileSpeedMax)
        {
            throw new InvalidOperationException(string.Concat(
                nameof(WorldHostOptions), " 的速度区间非法：", nameof(ProjectileSpeedMin), "(", ProjectileSpeedMin.ToString(),
                ") 大于 ", nameof(ProjectileSpeedMax), "(", ProjectileSpeedMax.ToString(), ")。"));
        }
        if (SpawnRadius <= 0f)
        {
            throw new InvalidOperationException(
                string.Concat(nameof(WorldHostOptions), ".", nameof(SpawnRadius), " 必须大于 0。"));
        }
        if (ViewPrewarm < 0 || InitialProjectileCount < 0)
        {
            throw new InvalidOperationException(
                string.Concat(nameof(WorldHostOptions), " 的计数不能为负。"));
        }
    }
}
