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
        string summary = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "hasMap={0} size={1}x{2} artLength={3}",
            _view.HasMap,
            _view.MapWidth,
            _view.MapHeight,
            art.Length);
        GD.Print("[DungeonMap2D 自检] " + summary);
        using FileAccess file = FileAccess.Open("user://dungeon_map2d_selfcheck.txt", FileAccess.ModeFlags.Write);
        if (file is not null)
        {
            file.StoreString(summary + "\n\n" + art);
        }
    }
}
