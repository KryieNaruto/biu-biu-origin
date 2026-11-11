using System;
using System.Collections.Generic;
using Game.Core.Ecs;
using Godot;
using NVector3 = System.Numerics.Vector3;

namespace Game.Host.Presentation;

/// <summary>
/// 视图导演：按实体的 <see cref="ViewAppearance"/> 组件给它的视图节点换上「该长什么样」。
/// <para>
/// <b>它与 <c>TransformBridge</c> / <c>VisualBridge</c> 的分工</b>：
/// <list type="bullet">
///   <item><c>VisualBridge</c>：<u>有没有</u>视图（从池里租/还，写回 <c>View.Slot</c>）；</item>
///   <item><c>TransformBridge</c>：视图<u>在哪、朝哪</u>（写 <c>Transform</c>）；</item>
///   <item><b>本类</b>：视图<u>长什么样</u>（外观包 → 网格 + 材质 + 缩放 + 朝向指示）。</item>
/// </list>
/// 三者都只读 ECS、只写 Node（J4 单向），谁都不反写玩法状态。
/// </para>
/// <para>
/// <b>为什么必须复用 <c>ViewRegistry</c> 的池</b>（硬约束）：节点一旦 Instantiate 就是一次跨语言 +
/// 资源解码，每帧 new 会让帧时间出现尖刺。本类<u>从不</u>创建视图节点，只对池里已租出的
/// <c>ViewId</c> 改写其子节点的网格/材质。外观包换掉时也只换子网格，不换父节点。
/// </para>
/// <para>
/// <b>外观从哪来</b>（优先级从高到低）：
/// <list type="number">
///   <item>外观注册表里该 <c>VisualSlotIndex</c> 的 <c>ModelPath</c> 非空 → 尝试 <c>Load</c> 并实例化（可选）；
///         加载失败 → 回退白模，<b>且同一路径只警告一次</b>（否则每帧一条日志会把输出刷爆）；</item>
///   <item>白模 = <c>Box</c> 或 <c>Capsule</c> 网格 + 外观包 <c>ColorHex</c> 的颜色；</item>
///   <item>外观包没给颜色 → 用 <see cref="GameViewOptions.PaletteColor"/> 的保底配色档位。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么不认识 <c>Game.Data</c></b>：外观包是 Excel 行，由 Lead 的 <c>CharacterSession</c> 读成
/// <see cref="ActorViewStyle"/>（纯值结构体）后注册进来。换一套数据表 = 重新 <see cref="Register"/>，
/// 本文件 0 行改动（规则7）。
/// </para>
/// </summary>
public sealed class ActorViewDirector
{
    private readonly ViewRegistry _views;
    private readonly GameViewOptions _options;

    /// <summary>外观注册表：槽位号 → 纯值外观描述。</summary>
    private readonly Dictionary<int, ActorViewStyle> _styles = new();

    /// <summary>已经警告过的模型路径（「只警告一次」的实现点）。</summary>
    private readonly HashSet<string> _warnedModelPaths = new(StringComparer.Ordinal);

    /// <summary>已加载的模型缓存（同一路径只读盘一次；null 表示加载失败，不再重试）。</summary>
    private readonly Dictionary<string, PackedScene?> _modelCache = new(StringComparer.Ordinal);

    /// <summary>每个视图槽的当前白模状态（避免每帧重建网格/材质）。</summary>
    private readonly Dictionary<int, AppliedView> _applied = new();

    /// <summary>白模网格缓存（Box / Capsule 各一份，按尺寸索引）。</summary>
    private readonly Dictionary<(ActorBodyShape, int, int), Mesh> _meshCache = new();

    /// <summary>材质缓存（颜色 + 自发光 → 一份材质，稳态下不重复 new）。</summary>
    private readonly Dictionary<(uint, uint), StandardMaterial3D> _materialCache = new();

    /// <summary>
    /// 构造视图导演。
    /// </summary>
    /// <param name="views">视图池（必须与 <c>VisualBridge</c> 用的是同一个实例）。</param>
    /// <param name="options">表现层参数（白模尺寸 / 配色档位 / 自发光 …）。</param>
    public ActorViewDirector(ViewRegistry views, GameViewOptions options)
    {
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>已注册的外观数量。</summary>
    public int RegisteredStyleCount => _styles.Count;

    /// <summary>最近一次 <see cref="ApplyAll"/> 处理的实体数。</summary>
    public int LastAppliedCount { get; private set; }

    /// <summary>最近一次 <see cref="ApplyAll"/> 因外观未变化而跳过的实体数（诊断：稳态应等于全部）。</summary>
    public int LastSkippedCount { get; private set; }

    /// <summary>累计回退到白模的次数（诊断：材料缺失时应只在开局增长）。</summary>
    public int FallbackCount { get; private set; }

    /// <summary>
    /// 注册一个外观包（槽位号来自外观包在注册表里的序号，由 Lead 在装配期决定）。
    /// <para>重复注册同一槽位 = 覆盖（换外观包时直接再注册一次）。</para>
    /// </summary>
    /// <param name="visualSlot">外观包槽位号（对应 <c>ViewAppearance.VisualSlotIndex</c>）。</param>
    /// <param name="style">外观描述（纯值）。</param>
    public void Register(int visualSlot, in ActorViewStyle style)
    {
        if (visualSlot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visualSlot), "外观槽位号不能为负（-1 表示未解析）。");
        }
        _styles[visualSlot] = style;
    }

    /// <summary>清空外观注册表（换一批外观包 / 切章节时调用）。</summary>
    public void ClearStyles() => _styles.Clear();

    /// <summary>
    /// 给某个实体的视图套上它的外观。宿主每帧对「有 <see cref="ViewAppearance"/> 的实体」调用一次。
    /// <para>
    /// <b>为什么是「按实体」而不是「按视图号」</b>：外观的权威在组件里（<c>ViewAppearance</c>）；
    /// 视图号只是池里的租约。按实体查，实体被销毁/复用后外观不会串到别人身上。
    /// </para>
    /// </summary>
    /// <param name="entity">实体。</param>
    /// <param name="appearance">该实体的外观组件。</param>
    /// <param name="viewSlot">该实体的视图号（来自 <c>View.Slot</c>）。</param>
    public void Apply(Entity entity, in ViewAppearance appearance, int viewSlot)
    {
        if (viewSlot <= 0)
        {
            return;
        }
        Node3D? root = _views.GetNode(viewSlot);
        if (root is null)
        {
            return;
        }

        ActorViewStyle style = ResolveStyle(appearance.VisualSlotIndex);
        uint styleKey = HashOf(style, appearance);

        if (_applied.TryGetValue(viewSlot, out AppliedView applied) && applied.Key == styleKey)
        {
            LastSkippedCount++;
            return;
        }

        ApplyTo(viewSlot, root, style, appearance);
        _applied[viewSlot] = new AppliedView { Key = styleKey };
        LastAppliedCount++;

        // 视图节点是池里的公共资源：刚归还的节点可能被别人藏着，被新主人租到的那一刻必须点亮
        // （可见性最终在帧末由 ViewRegistry.ApplyVisibility 统一落盘，这里只登记意图）。
        _views.SetVisible(viewSlot, true);
        _ = entity;
    }

    /// <summary>
    /// 一次性遍历所有「有 <see cref="ViewAppearance"/> 的实体」并套用外观。
    /// <para>
    /// 由宿主在视图桥阶段（<c>VisualBridge.RentMissing</c> 之后、<c>TransformBridge.Push</c> 之前）调用：
    /// 此时新实体的 <c>View.Slot</c> 已经分配，套外观才有节点可写。
    /// </para>
    /// <para>
    /// <b>为什么先收集再写节点</b>：与 <c>TransformBridge</c> 同样的纪律——遍历期间不做任何可能有
    /// 副作用的引擎调用，保证将来视图脚本回调了什么也不会在遍历中触发结构化变更。
    /// </para>
    /// </summary>
    /// <param name="world">ECS 世界（只读）。</param>
    /// <param name="viewQuery">「<c>View</c> + <c>ViewAppearance</c>」的查询（由宿主缓存后传入）。</param>
    public void ApplyAll(World world, Query viewQuery)
    {
        if (world is null)
        {
            throw new ArgumentNullException(nameof(world));
        }
        if (viewQuery is null)
        {
            throw new ArgumentNullException(nameof(viewQuery));
        }
        LastAppliedCount = 0;
        LastSkippedCount = 0;
        viewQuery.ForEach<View, ViewAppearance>(ApplySpan);
        _ = world;
    }

    /// <summary>
    /// 归还视图时清掉本类的旁路记录（视图号会被池复用给别的实体，外观必须重新套）。
    /// <para>由宿主在 <c>VisualBridge.FlushReleases</c> 之后对每个归还的视图号调用一次。</para>
    /// </summary>
    /// <param name="viewSlot">被归还的视图号。</param>
    public void Forget(int viewSlot) => _applied.Remove(viewSlot);

    /// <summary>清空全部旁路记录（切场景 / 重建视图池）。</summary>
    public void ForgetAll() => _applied.Clear();

    /// <summary>
    /// 把一个外观套到视图节点上：清掉旧的子节点，挂新的白模（或模型），再按 <c>Scale</c> 缩放。
    /// <para>
    /// <b>为什么清子节点而不复用单个 MeshInstance3D</b>：外观可能从「白模」变成「模型预制体」，
    /// 预制体是一棵子树；统一「先清后建」让两种路径只维护一套逻辑。变更频率极低（出生/换装），
    /// 不在每帧热路径上。
    /// </para>
    /// </summary>
    private void ApplyTo(int viewSlot, Node3D root, in ActorViewStyle style, in ViewAppearance appearance)
    {
        // ① 清掉上次的外观子节点（不动根节点本身：TransformBridge 正在驱动它）。
        for (int i = root.GetChildCount() - 1; i >= 0; i--)
        {
            Node child = root.GetChild(i);
            root.RemoveChild(child);
            child.QueueFree();
        }

        // ② 模型优先：外观包给了 ModelPath 就试加载。
        bool built = false;
        if (!string.IsNullOrEmpty(style.ModelPath))
        {
            PackedScene? scene = LoadModelOnce(style.ModelPath!);
            if (scene is not null)
            {
                built = TryInstantiateModel(scene, style, root);
            }
        }

        // ③ 回退白模：方块 / 胶囊 + 外观包颜色（或配色档位保底色）。
        if (!built)
        {
            BuildPrimitive(style, appearance, root);
            if (!string.IsNullOrEmpty(style.ModelPath))
            {
                FallbackCount++;
            }
        }

        // ④ 整体缩放：组件的 Scale 优先（> 0），否则外观包的 ModelScale。
        float scale = ResolveScale(style, appearance);
        root.Scale = new Vector3(scale, scale, scale);

        // （朝向指示「鼻子」是 BuildPrimitive 里挂在 root 上的另一个直接子节点；它与身体同生同灭，
        //   下一次 ApplyTo 的 ① 会把两者一起清掉 —— 视图池归还再租给别人时不会带走旧主人的鼻子。）
        _ = viewSlot;
    }

    /// <summary>造白模：身体 + 朝向指示（鼻子），颜色来自外观包或配色档位。</summary>
    private void BuildPrimitive(in ActorViewStyle style, in ViewAppearance appearance, Node3D root)
    {
        Color color = ResolveColor(style, appearance);
        StandardMaterial3D material = GetMaterial(color);

        float bodyRadius = style.BodyRadiusOverride > 0f ? style.BodyRadiusOverride : _options.ActorBodyRadius;
        float bodyHeight = style.BodyHeightOverride > 0f ? style.BodyHeightOverride : _options.ActorBodyHeight;

        Mesh mesh = GetMesh(style.BodyShape, bodyRadius, bodyHeight);
        MeshInstance3D body = new MeshInstance3D
        {
            Name = "Body",
            Mesh = mesh,
            MaterialOverride = material,
            // 网格原点在几何中心，而「站立」以底部贴地为准：整体上移半个高度。
            Position = new Vector3(0f, bodyHeight * HalfUnit + _options.ActorGroundOffset, 0f),
        };
        root.AddChild(body);

        // 朝向指示（鼻子）：没有它，俯视下看不出角色朝哪（手感优先项目的必要读图信息）。
        float noseRatio = _options.ActorNoseRatio;
        if (noseRatio > 0f)
        {
            float noseLength = bodyRadius * noseRatio;
            float noseThickness = bodyRadius * _options.ActorNoseThicknessRatio;
            MeshInstance3D nose = new MeshInstance3D
            {
                Name = "Nose",
                Mesh = GetMesh(ActorBodyShape.Box, noseThickness, noseThickness),
                MaterialOverride = GetMaterial(color.Lightened(NoseLightening)),
                Position = new Vector3(noseLength * HalfUnit, bodyHeight * HalfUnit + _options.ActorGroundOffset, 0f),
                Scale = new Vector3(noseLength, noseThickness, noseThickness),
            };
            root.AddChild(nose);
        }
    }

    /// <summary>把模型预制体实例化进视图节点。</summary>
    private bool TryInstantiateModel(PackedScene scene, in ActorViewStyle style, Node3D root)
    {
        Node? instance = scene.Instantiate();
        if (instance is not Node3D model)
        {
            instance?.QueueFree();
            return false;
        }
        model.Name = "Model";
        root.AddChild(model);
        if (style.ModelScale > 0f)
        {
            model.Scale = new Vector3(style.ModelScale, style.ModelScale, style.ModelScale);
        }
        return true;
    }

    /// <summary>按路径加载一次模型（失败只警告一次并缓存失败，避免每帧重试刷日志）。</summary>
    private PackedScene? LoadModelOnce(string path)
    {
        if (_modelCache.TryGetValue(path, out PackedScene? cached))
        {
            return cached;
        }

        PackedScene? loaded = null;
        if (ResourceLoader.Exists(path))
        {
            Resource? resource = ResourceLoader.Load(path);
            loaded = resource as PackedScene;
        }

        if (loaded is null)
        {
            _modelCache[path] = null;
            // 「只警告一次」：同一路径落盘失败只报一条，之后静默回退（池化 + 每帧调用会刷爆日志）。
            if (_warnedModelPaths.Add(path))
            {
                GD.PushWarning(string.Concat(
                    "[ActorViewDirector] 外观模型加载失败，回退白模（此警告同一路径只报一次）：", path));
            }
            return null;
        }

        _modelCache[path] = loaded;
        _warnedModelPaths.Add(path);
        return loaded;
    }

    /// <summary>取（并缓存）白模网格。尺寸变了才重建。</summary>
    private Mesh GetMesh(ActorBodyShape shape, float radius, float height)
    {
        int r = (int)MathF.Round(radius * CacheQuantization);
        int h = (int)MathF.Round(height * CacheQuantization);
        if (_meshCache.TryGetValue((shape, r, h), out Mesh? cached))
        {
            return cached;
        }

        Mesh mesh = shape == ActorBodyShape.Capsule
            ? BuildCapsuleMesh(radius, height)
            : BuildBoxMesh(radius, height);
        _meshCache[(shape, r, h)] = mesh;
        return mesh;
    }

    /// <summary>取（并缓存）材质：相同颜色共用一份，避免每个实体 new 材质。</summary>
    private StandardMaterial3D GetMaterial(Color color)
    {
        (uint, uint) key = (color.ToRgba32(), BitConverter.SingleToUInt32Bits(_options.ActorEmissionEnergy));
        if (_materialCache.TryGetValue(key, out StandardMaterial3D? cached))
        {
            return cached;
        }
        StandardMaterial3D material = new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = _options.ActorRoughness,
            Metallic = 0f,
        };
        float emission = _options.ActorEmissionEnergy;
        if (emission > 0f)
        {
            material.EmissionEnabled = true;
            material.Emission = color;
            material.EmissionEnergyMultiplier = emission;
        }
        _materialCache[key] = material;
        return material;
    }

    /// <summary>解算外观：外观槽位号越界 / 未注册时给一个默认白模（不让实体「隐身」）。</summary>
    private ActorViewStyle ResolveStyle(int visualSlot)
    {
        if (_styles.TryGetValue(visualSlot, out ActorViewStyle style))
        {
            return style;
        }
        return ActorViewStyle.Default(_options.DefaultBodyShape);
    }

    /// <summary>颜色：外观包 ColorHex 优先，否则配色档位保底，再否则默认档位 0。</summary>
    private Color ResolveColor(in ActorViewStyle style, in ViewAppearance appearance)
    {
        if (style.HasColor)
        {
            return style.Color;
        }
        return _options.PaletteColor(appearance.PaletteSlot);
    }

    /// <summary>缩放：组件的 Scale（&gt; 0）优先，否则外观包的 ModelScale，再否则 1。</summary>
    private float ResolveScale(in ActorViewStyle style, in ViewAppearance appearance)
    {
        if (appearance.Scale > 0f)
        {
            return appearance.Scale;
        }
        if (style.ModelScale > 0f)
        {
            return style.ModelScale;
        }
        return 1f;
    }

    /// <summary><c>ForEach</c> 回调：给每个实体套外观。</summary>
    private void ApplySpan(Span<View> views, Span<ViewAppearance> appearances)
    {
        for (int i = 0; i < views.Length; i++)
        {
            Apply(views[i].Owner, appearances[i], views[i].Slot);
        }
    }

    /// <summary>把外观 + 组件组合成一个整数键（稳态下「外观没变」的快速判定）。</summary>
    private static uint HashOf(in ActorViewStyle style, in ViewAppearance appearance)
    {
        unchecked
        {
            uint h = (uint)style.VisualSlot;
            h = h * HashPrime + (uint)style.BodyShape;
            h = h * HashPrime + (style.HasColor ? style.Color.ToRgba32() : 0u);
            h = h * HashPrime + (uint)appearance.PaletteSlot;
            h = h * HashPrime + BitConverter.SingleToUInt32Bits(appearance.Scale);
            h = h * HashPrime + (uint)appearance.VisualKey;
            h = h * HashPrime + (style.ModelPath is null ? 0u : unchecked((uint)style.ModelPath.GetHashCode()));
            return h;
        }
    }

    /// <summary>造一个高度 = <paramref name="height"/> 的方块网格（边长由半径 × 2 决定）。</summary>
    /// <param name="radius">半边长（米）。</param>
    /// <param name="height">高度（米）。</param>
    public static BoxMesh BuildBoxMesh(float radius, float height)
    {
        return new BoxMesh { Size = new Vector3(radius * FullSizeFactor, height, radius * FullSizeFactor) };
    }

    /// <summary>造一个总高 = <paramref name="height"/> 的胶囊网格（半径 = <paramref name="radius"/>）。</summary>
    /// <param name="radius">半径（米）。</param>
    /// <param name="height">总高度（米，含两个半球端）。</param>
    public static CapsuleMesh BuildCapsuleMesh(float radius, float height)
    {
        return new CapsuleMesh
        {
            Radius = radius,
            Height = MathF.Max(height, radius * FullSizeFactor),
        };
    }

    /// <summary>已套用的外观状态（只有键，够用；为什么不用整套样式：判定只看「是否变化」）。</summary>
    private struct AppliedView
    {
        /// <summary>外观键。</summary>
        public uint Key;
    }

    /// <summary>1/2：从中心到边的换算系数。</summary>
    private const float HalfUnit = 0.5f;

    /// <summary>2/1：从半径到边长的换算系数。</summary>
    private const float FullSizeFactor = 2f; // arch-ok: 几何换算（半径 ↔ 边长/直径）

    /// <summary>朝向指示比身体亮多少（0..1）。</summary>
    private const float NoseLightening = 0.3f; // arch-ok: 表现参数（朝向指示提亮系数）

    /// <summary>网格缓存量化精度（1 / 米）。</summary>
    private const float CacheQuantization = 100f; // arch-ok: 缓存键量化精度（不是玩法数值）

    /// <summary>哈希乘数。</summary>
    private const uint HashPrime = 16777619u; // arch-ok: FNV-1a 哈希常数
}

/// <summary>
/// 一个外观包的**纯值描述**（由 Lead 的 <c>CharacterSession</c> 从 Excel <c>VisualPacks</c> 行读入）。
/// <para>为什么不用 <c>Game.Data.CharacterVisualPack</c> 直接传：视图层不认识数据层，
/// 换数据表结构时只改 <c>CharacterSession</c> 的映射（规则7：新增一种外观包 = 0 个视图层文件改动）。</para>
/// </summary>
public readonly struct ActorViewStyle
{
    /// <summary>外观包槽位号（= 注册时的键；-1 表示未解析）。</summary>
    public readonly int VisualSlot;

    /// <summary>模型路径（<c>res://</c> 或 <c>user://</c>）；空串 = 用白模。</summary>
    public readonly string? ModelPath;

    /// <summary>模型整体缩放；&lt;= 0 表示沿用组件 Scale / 1。</summary>
    public readonly float ModelScale;

    /// <summary>白模形状（没有模型时生效）。</summary>
    public readonly ActorBodyShape BodyShape;

    /// <summary>白模身体半径覆盖（米）；&lt;= 0 表示用 <see cref="GameViewOptions.ActorBodyRadius"/>。</summary>
    public readonly float BodyRadiusOverride;

    /// <summary>白模身体高度覆盖（米）；&lt;= 0 表示用 <see cref="GameViewOptions.ActorBodyHeight"/>。</summary>
    public readonly float BodyHeightOverride;

    /// <summary>高饱和配色；<see cref="HasColor"/> 为 false 时用配色档位保底。</summary>
    public readonly Color Color;

    /// <summary>是否给了有效颜色。</summary>
    public readonly bool HasColor;

    /// <summary>外观包额外给的身体缩放（外观包 <c>ModelScale</c> 的另一用途）。</summary>
    public readonly float BodyScale;

    /// <summary>构造外观描述。</summary>
    public ActorViewStyle(
        int visualSlot,
        string? modelPath,
        float modelScale,
        ActorBodyShape bodyShape,
        float bodyRadiusOverride,
        float bodyHeightOverride,
        Color color,
        bool hasColor,
        float bodyScale)
    {
        VisualSlot = visualSlot;
        ModelPath = modelPath;
        ModelScale = modelScale;
        BodyShape = bodyShape;
        BodyRadiusOverride = bodyRadiusOverride;
        BodyHeightOverride = bodyHeightOverride;
        Color = color;
        HasColor = hasColor;
        BodyScale = bodyScale;
    }

    /// <summary>只给了颜色的快捷构造（Excel 里最常见的一行：id + ColorHex）。</summary>
    /// <param name="visualSlot">槽位号。</param>
    /// <param name="color">颜色。</param>
    /// <param name="bodyShape">白模形状。</param>
    /// <param name="modelPath">模型路径（可为 null）。</param>
    /// <param name="modelScale">模型缩放。</param>
    public static ActorViewStyle Colored(int visualSlot, Color color, ActorBodyShape bodyShape, string? modelPath, float modelScale)
    {
        return new ActorViewStyle(visualSlot, modelPath, modelScale, bodyShape, 0f, 0f, color, true, 1f);
    }

    /// <summary>默认可视外观（外观包未注册时用，保证实体不会「隐身」）。</summary>
    /// <param name="shape">白模形状。</param>
    public static ActorViewStyle Default(ActorBodyShape shape)
    {
        return new ActorViewStyle(-1, null, 1f, shape, 0f, 0f, default, false, 1f);
    }

    /// <summary>
    /// 把 Excel 里常见的 <c>#RRGGBB</c> / <c>#RRGGBBAA</c> 文本解析成颜色。
    /// <para>失败返回 false 并给出「无颜色」，调用方回退到配色档位（而不是抛异常把整局带崩）。</para>
    /// </summary>
    /// <param name="hex">颜色文本（可带或不带 #）。</param>
    /// <param name="color">解析结果。</param>
    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }
        string text = hex!.Trim();
        if (text.StartsWith('#'))
        {
            text = text.Substring(1);
        }

        bool hasAlpha = text.Length == HexRgbaLength;
        if (!hasAlpha && text.Length != HexRgbLength)
        {
            return false;
        }
        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint raw))
        {
            return false;
        }

        // 6 位：#RRGGBB（alpha = FF）；8 位：#RRGGBBAA。
        byte r = hasAlpha ? (byte)((raw >> ShiftR8) & ByteMask) : (byte)((raw >> ShiftR6) & ByteMask);
        byte g = hasAlpha ? (byte)((raw >> ShiftG8) & ByteMask) : (byte)((raw >> ShiftG6) & ByteMask);
        byte b = hasAlpha ? (byte)((raw >> ShiftB8) & ByteMask) : (byte)(raw & ByteMask);
        byte a = hasAlpha ? (byte)(raw & ByteMask) : byte.MaxValue;
        color = Color.Color8(r, g, b, a);
        return true;
    }

    /// <summary>8 位十六进制里 R 通道的右移位数（#RRGGBBAA）。</summary>
    private const int ShiftR8 = 24; // arch-ok: 位运算常量（颜色编码），不是玩法数值

    /// <summary>8 位十六进制里 G 通道的右移位数。</summary>
    private const int ShiftG8 = 16; // arch-ok: 位运算常量（颜色编码），不是玩法数值

    /// <summary>8 位十六进制里 B 通道的右移位数。</summary>
    private const int ShiftB8 = 8; // arch-ok: 位运算常量（颜色编码），不是玩法数值

    /// <summary>6 位十六进制里 R 通道的右移位数（#RRGGBB）。</summary>
    private const int ShiftR6 = 16; // arch-ok: 位运算常量（颜色编码），不是玩法数值

    /// <summary>6 位十六进制里 G 通道的右移位数。</summary>
    private const int ShiftG6 = 8; // arch-ok: 位运算常量（颜色编码），不是玩法数值

    /// <summary>单字节掩码。</summary>
    private const uint ByteMask = 0xFF; // arch-ok: 位运算常量（颜色编码），不是玩法数值

    /// <summary><c>#RRGGBB</c> 的字符数。</summary>
    private const int HexRgbLength = 6; // arch-ok: 颜色文本格式常量

    /// <summary><c>#RRGGBBAA</c> 的字符数。</summary>
    private const int HexRgbaLength = 8; // arch-ok: 颜色文本格式常量
}
