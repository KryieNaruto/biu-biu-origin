using System;
using System.Diagnostics;
using Godot;

namespace Game.Host.Debug;

/// <summary>
/// 给「拖滑条 → 地图重画」这条链路计时，用来回答一个很实际的问题：
/// **拖的时候会不会卡？** 每次重算都要跑一遍规划器 + 栅格化 + 体检，
/// 如果单次超过一帧（16.6 ms），拖动就会明显掉帧，那就得改成「松手才算」。
/// <para>
/// 只负责测量与打印，不改变任何行为；随场景加载即生效，正常游玩时只留一行日志。
/// </para>
/// </summary>
public partial class DungeonMapRegenTimer : Node
{
    private readonly Stopwatch _watch = new();
    private int _samples;
    private long _worstTicks;

    /// <summary>开始一次重算计时。</summary>
    public void Begin() => _watch.Restart();

    /// <summary>结束一次重算计时并累计统计。</summary>
    /// <returns>本次耗时（毫秒）。</returns>
    public double End()
    {
        _watch.Stop();
        double ms = _watch.Elapsed.TotalMilliseconds;
        _samples++;
        if (_watch.ElapsedTicks > _worstTicks)
        {
            _worstTicks = _watch.ElapsedTicks;
        }
        return ms;
    }

    /// <summary>把累计统计写成一行（重算次数 / 最差耗时）。</summary>
    public void PrintSummary()
    {
        if (_samples == 0)
        {
            return;
        }
        double worstMs = _worstTicks * 1000.0 / Stopwatch.Frequency; // arch-ok: 计时单位换算
        GD.Print("[DungeonMap2D 性能] 重算 "
            + _samples.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 次，最差一次 "
            + worstMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + " ms（超过 16.6 ms 拖动就会掉帧）");
    }
}
