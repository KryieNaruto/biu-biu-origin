using Godot;
using Game.Spawn.Rooms;

namespace Game.Host.Debug;

/// <summary>
/// 「2D 地牢地图 + 实时调参面板」的调试场景根节点：把 <see cref="DungeonMap2DView"/> 与
/// <see cref="DungeonMap2DPanel"/> 连起来，并保证面板**永远在最后**绑定（<c>_Ready</c> 顺序不确定，
/// 用 <c>CallDeferred</c> 推迟一帧最稳）。
/// <para>
/// 与 <c>DungeonDebugScene</c>（3D 白盒）是**两条独立入口**：这里只看地图拓扑，
/// 不建 3D、不跑 ECS、不刷怪。想改 3D 表现请走 <c>scenes/main/DungeonDebug.tscn</c>。
/// </para>
/// </summary>
[GlobalClass]
public partial class DungeonMap2DDebugScene : Godot.Control
{
    /// <summary>配色表（<c>.tres</c> 可替换；为空时视图与面板用内置默认）。</summary>
    [Export]
    public DungeonRoomStyleSet? RoomStyles { get; set; }

    private DungeonMap2DView _view = null!;
    private DungeonMap2DPanel _panel = null!;

    /// <inheritdoc />
    public override void _Ready()
    {
        _view = GetNode<DungeonMap2DView>("Layout/MapScroll/MapView");
        _panel = GetNode<DungeonMap2DPanel>("Layout/Panel");
        CallDeferred(nameof(BindPanel));
    }

    private void BindPanel()
    {
        _panel.Bind(_view, RoomStyles);
        ReportForSelfCheck();
    }

    /// <summary>
    /// 无头自检：把「地图到底建出来没有、连通不连通」写进 <c>user://</c> 与日志。
    /// 无输入、无截图的环境下（CI / agent）靠这个确认面板→规划器→渲染链路真的跑通了，
    /// 而不是「没报错所以大概可以」。正常游玩时这个文件也无害。
    /// </summary>
    private void ReportForSelfCheck()
    {
        string art = _view.ExportArt();
        bool ok = _view.HasMap && art.Length > 0;
        // 物件分类计数直接从字符画里数：这样自检验证的是「玩家真正看到的那张图」，
        // 而不是另外再查一遍数据结构（那只能证明数据对，证明不了画对）。
        int buildings = 0;
        int rewards = 0;
        int breakables = 0;
        int solids = 0;
        for (int i = 0; i < art.Length; i++)
        {
            switch (art[i])
            {
                case 'B':
                    buildings++;
                    break;
                case 'R':
                    rewards++;
                    break;
                case 'b':
                    breakables++;
                    break;
                case 'o':
                    solids++;
                    break;
                default:
                    break;
            }
        }
        // 控件统计：直接数场景树里真实存在的节点类型，而不是相信「我写了 AddSlider 所以它就是滑块」。
        // 无头环境下没有眼睛看面板，这是唯一能证明「数值确实换成了可左右拖动的滑块」的证据。
        int sliders = CountNodes<HSlider>(_panel);
        int spinBoxes = CountNodes<SpinBox>(_panel);

        string summary = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "hasMap={0} size={1}x{2} artLength={3} props(B={4} R={5} b={6} o={7}) sliders={8} spinBoxes={9}",
            _view.HasMap,
            _view.MapWidth,
            _view.MapHeight,
            art.Length,
            buildings,
            rewards,
            breakables,
            solids,
            sliders,
            spinBoxes);
        GD.Print("[DungeonMap2D 自检] " + summary);
        if (!ok)
        {
            GD.PushWarning("[DungeonMap2D 自检] 没拿到地图，字符画为空");
        }
        if (spinBoxes > 0)
        {
            // SpinBox 只能点箭头，用户明确要的是左右拖动；残留任何一个都说明改造漏了。
            GD.PushWarning("[DungeonMap2D 自检] 面板里还有 " + spinBoxes + " 个 SpinBox，没换成滑块");
        }
        using FileAccess file = FileAccess.Open("user://dungeon_map2d_selfcheck.txt", FileAccess.ModeFlags.Write);
        if (file is not null)
        {
            file.StoreString(summary + "\n\n" + art);
        }
    }

    /// <summary>统计某个节点子树里指定类型的节点个数（含自身）。</summary>
    private static int CountNodes<T>(Node root)
        where T : Node
    {
        int count = root is T ? 1 : 0;
        foreach (Node child in root.GetChildren())
        {
            count += CountNodes<T>(child);
        }
        return count;
    }
}
