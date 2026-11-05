using System;
using Godot;

namespace Game.Host.Presentation;

/// <summary>
/// 准星：鼠标世界点处的一个**环**（3D 网格，画在世界空间的地面上）。
/// <para>
/// <b>为什么是世界空间的环而不是屏幕十字</b>：俯视角射击里「子弹落点」比「鼠标在哪」更重要——
/// 世界空间的环会随透视正确贴合地面（相机有俯仰时，屏幕十字画在地面交点上会飘）；
/// 同时它天然是「射程指示」的锚点（将来加扩散圈直接改半径即可）。
/// </para>
/// <para>
/// <b>为什么用 ImmediateMesh 自建环而不是 <c>.glb</c> / <c>MeshInstance3D</c> 预制体</b>：
/// v1 的形状与粗细都来自 <see cref="GameViewOptions"/>，用一个纯函数生成的环最省；
/// 且它<u>只有一份</u>（不是每个实体一个），不需要进 <c>ViewRegistry</c> 的池。
/// 网格在构造时只生成一次（按分段数变化时重建），稳态下每帧只写一个 Transform。
/// </para>
/// <para>
/// <b>为什么不参与拾取/碰撞</b>：准星是纯表现，<c>NoDepthTest</c> 关闭但材质设为无光照，
/// 且不加 <c>CollisionObject3D</c>——宿主层的射线求交永远不会碰到它。
/// </para>
/// </summary>
public sealed partial class HeroCrosshair : Node3D
{
    private readonly MeshInstance3D _ring;
    private readonly StandardMaterial3D _material;
    private readonly GameViewOptions _options;

    private int _builtSegments;
    private float _builtRadius;
    private float _builtThickness;
    private bool _visible;

    /// <summary>
    /// 构造准星节点（**尚未**加入场景树，由调用方 <c>AddChild</c>）。
    /// </summary>
    /// <param name="options">表现层参数（半径 / 线宽 / 颜色 / 分段数 / 离地高度）。</param>
    public HeroCrosshair(GameViewOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Name = "HeroCrosshair";

        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = options.CrosshairColor,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = false,
        };
        if (options.CrosshairEmissionEnergy > 0f)
        {
            _material.EmissionEnabled = true;
            _material.Emission = options.CrosshairColor;
            _material.EmissionEnergyMultiplier = options.CrosshairEmissionEnergy;
        }

        _ring = new MeshInstance3D
        {
            Name = "Ring",
            Mesh = BuildRing(options.CrosshairRadius, options.CrosshairThickness, options.CrosshairSegments),
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _builtSegments = options.CrosshairSegments;
        _builtRadius = options.CrosshairRadius;
        _builtThickness = options.CrosshairThickness;

        AddChild(_ring);
        Visible = false;
    }

    /// <summary>准星当前是否可见。</summary>
    public bool IsCrosshairVisible => _visible;

    /// <summary>
    /// 把准星放到世界点（水平面）。由宿主每帧调用；<paramref name="visible"/> 为 false 时隐藏
    /// （鼠标离开窗口 / headless 没有有效世界点）。
    /// </summary>
    /// <param name="worldPoint">鼠标与瞄准平面的交点（世界米）。</param>
    /// <param name="visible">是否显示。</param>
    public void PlaceAt(in Vector3 worldPoint, bool visible)
    {
        bool show = visible && _options.CrosshairVisible;
        if (show)
        {
            GlobalPosition = new Vector3(worldPoint.X, worldPoint.Y + _options.CrosshairHeight, worldPoint.Z);
        }
        if (Visible != show)
        {
            Visible = show;
            _visible = show;
        }
    }

    /// <summary>隐藏准星（切场景 / 打开菜单）。</summary>
    public void HideCrosshair()
    {
        if (Visible)
        {
            Visible = false;
        }
        _visible = false;
    }

    /// <summary>
    /// 运行时改形状（换皮肤 / 调参预览）。参数未变时不重建网格（稳态零分配）。
    /// </summary>
    /// <param name="radius">外半径。</param>
    /// <param name="thickness">线宽。</param>
    /// <param name="segments">分段数。</param>
    public void Restyle(float radius, float thickness, int segments)
    {
        if (segments == _builtSegments && Mathf.IsEqualApprox(radius, _builtRadius) && Mathf.IsEqualApprox(thickness, _builtThickness))
        {
            return;
        }
        _ring.Mesh = BuildRing(radius, thickness, segments);
        _builtSegments = segments;
        _builtRadius = radius;
        _builtThickness = thickness;
    }

    /// <summary>运行时改颜色。</summary>
    /// <param name="color">新颜色。</param>
    public void SetColor(Color color)
    {
        _material.AlbedoColor = color;
        if (_material.EmissionEnabled)
        {
            _material.Emission = color;
        }
    }

    /// <summary>
    /// 生成一个环（平面圆环带，位于 XZ 平面、法线朝 +Y）。
    /// <para>
    /// <b>为什么是「环形带」而不是 <c>TorusMesh</c></b>：Godot 的 Torus 朝向固定且无法控制线宽
    /// 相对半径的「扁平」程度；俯视下要的是一个薄圆环（像准星环），自建 4 顶点 × N 段最可控也最省。
    /// </para>
    /// </summary>
    /// <param name="radius">外半径（米）。</param>
    /// <param name="thickness">环带宽度（米）。</param>
    /// <param name="segments">分段数（≥ 3）。</param>
    public static ArrayMesh BuildRing(float radius, float thickness, int segments)
    {
        int seg = segments < MinRingSegments ? MinRingSegments : segments;
        float inner = MathF.Max(radius - thickness, 0f);

        // 每个分段 2 个顶点（内圈 + 外圈），首尾各补一圈让环闭合。
        int vertexCount = (seg + 1) * VerticesPerRingStep;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        int[] indices = new int[seg * IndicesPerRingStep];

        for (int i = 0; i <= seg; i++)
        {
            float angle = MathF.Tau * i / seg;
            float cx = MathF.Cos(angle);
            float cz = MathF.Sin(angle);
            vertices[i * VerticesPerRingStep] = new Vector3(cx * inner, 0f, cz * inner);
            vertices[i * VerticesPerRingStep + 1] = new Vector3(cx * radius, 0f, cz * radius);
            normals[i * VerticesPerRingStep] = Vector3.Up;
            normals[i * VerticesPerRingStep + 1] = Vector3.Up;
        }

        for (int i = 0; i < seg; i++)
        {
            int a = i * VerticesPerRingStep;
            int b = (i + 1) * VerticesPerRingStep;
            int n = i * IndicesPerRingStep;
            indices[n] = a;
            indices[n + 1] = a + 1;
            indices[n + 2] = b + 1;
            indices[n + 3] = a;
            indices[n + 4] = b + 1;
            indices[n + 5] = b;
        }

        Godot.Collections.Array arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        ArrayMesh mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>环的最小分段数（再少构不成环）。</summary>
    private const int MinRingSegments = 3; // arch-ok: 几何下限（三角形才成环）

    /// <summary>环每一步（一个角度分段）占的顶点数：内圈 1 + 外圈 1。</summary>
    private const int VerticesPerRingStep = 2; // arch-ok: 网格拓扑常量（环形带），不是玩法数值

    /// <summary>环每一步的三角形索引数：2 个三角形 × 3 个索引。</summary>
    private const int IndicesPerRingStep = 6; // arch-ok: 网格拓扑常量（环形带），不是玩法数值
}
