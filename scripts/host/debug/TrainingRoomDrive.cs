using System;
using Game.Actor;
using Game.Host.Control;
using Game.Core;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Host.Debug;

/// <summary>
/// 训练房的**无头走查输入**：把脚本写的一串动作（走 → 停 → 开火 → 走）喂给玩家，用真实键鼠的同一套管道。
/// <para>
/// <b>为什么需要它</b>：agent / CI 里没有手。看「人物能不能走、子弹是不是朝鼠标方向飞」只能靠
/// <b>伪造一次键鼠采样</b>：意图源用 <see cref="PlayerInputIntentSource.ForceInput"/> 强制一组输入，
/// 下游（意图系统 → 移动/瞄准/射击 → 视图桥 → HUD）完全走真实路径 ——
/// 它证明的东西和「人真的按了一下 W」是同一件事，而不是「我直接改了位置所以位置会变」。
/// </para>
/// <para>
/// <b>为什么是「时间轴」而不是「按帧数」</b>：真实手速与帧率无关；按秒推进，60/144/无头固定步长下
/// 走的距离都对得上。时间轴里的「走了多远」「射出几发」「打中谁」由调用方在段落结束时读世界来判定，
/// 本类只负责「什么时候按什么」。
/// </para>
/// <para>
/// <b>为什么移动走「强制意图 + 屏幕基方向」</b>：<see cref="InputIntent"/> 只表达「往屏幕右/上」，
/// 屏幕 → 世界的换算要相机（宿主层）。这里造的是玩家按 W/A/S/D 时意图源会给的那个
/// <see cref="InputIntent"/>，世界方向由 <c>CharacterSession.DriveMove</c> 用当前相机旋出来 ——
/// 与真人按键走的是同一条路径，不是直接改 <c>MoveIntent</c>。
/// </para>
/// </summary>
public sealed class TrainingRoomDrive
{
    /// <summary>一段脚本动作（绝对时间轴上的一个瞬间）。</summary>
    public readonly struct Keyframe
    {
        /// <summary>触发时刻（自开始算起的秒数）。</summary>
        public readonly float AtSeconds;

        /// <summary>屏幕基移动 X（-1 左 / 0 / +1 右）。</summary>
        public readonly float MoveX;

        /// <summary>屏幕基移动 Y（-1 后 / 0 / +1 前）。</summary>
        public readonly float MoveY;

        /// <summary>是否按住左键。</summary>
        public readonly bool PrimaryHeld;

        /// <summary>构造一个关键帧。</summary>
        public Keyframe(float atSeconds, float moveX, float moveY, bool primaryHeld)
        {
            AtSeconds = atSeconds;
            MoveX = moveX;
            MoveY = moveY;
            PrimaryHeld = primaryHeld;
        }
    }

    private readonly PlayerInputIntentSource _input;
    private readonly Keyframe[] _timeline;
    private int _next;
    private float _elapsed;

    /// <summary>构造。</summary>
    /// <param name="input">玩家的键位意图源（强制输入写在这上面）。</param>
    /// <param name="timeline">按时间升序排列的关键帧。</param>
    public TrainingRoomDrive(PlayerInputIntentSource input, Keyframe[] timeline)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _timeline = timeline ?? Array.Empty<Keyframe>();
        foreach (Keyframe k in _timeline)
        {
            if (k.AtSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(timeline), "关键帧时间不能为负。");
            }
        }
        for (int i = 1; i < _timeline.Length; i++)
        {
            if (_timeline[i].AtSeconds < _timeline[i - 1].AtSeconds)
            {
                throw new ArgumentException("关键帧必须按时间升序排列。", nameof(timeline));
            }
        }
    }

    /// <summary>时间轴是否已经全部触发（触发完后输入保持最后一段的状态）。</summary>
    public bool Finished => _next >= _timeline.Length;

    /// <summary>已经触发过的关键帧数（自检断言用）。</summary>
    public int FiredCount => _next;

    /// <summary>时间轴已经走过的秒数（诊断心跳用）。</summary>
    public float ElapsedSeconds => _elapsed;

    /// <summary>
    /// 推进一帧：把到期的时间轴动作写进意图源。
    /// <para>本帧触发的关键帧里，<b>最后一条</b>生效（同一时刻写两次以最后一条为准，与"人只按了一次"一致）。</para>
    /// </summary>
    /// <param name="deltaSeconds">本帧时长。</param>
    public void Tick(float deltaSeconds)
    {
        _elapsed += deltaSeconds;
        Keyframe? apply = null;
        while (_next < _timeline.Length && _timeline[_next].AtSeconds <= _elapsed)
        {
            apply = _timeline[_next];
            _next++;
        }
        if (apply.HasValue)
        {
            Apply(apply.Value);
        }
    }

    /// <summary>立刻套用一帧输入（<c>ForceInput</c> 每帧整体覆盖意图，所以按住态要每帧重新给）。</summary>
    private void Apply(in Keyframe k)
    {
        uint mask = InputIntent.MaskOf(SkillSlot.PrimaryAttack);
        const int FixedOne = 1 << 16; // arch-ok: Q16 定点表示法，不是玩法数值
        const int FixedRight = FixedOne; // arch-ok: 朝向常量：+X = 屏幕右
        _input.ForceInput(new InputIntent(
            pressedMask: k.PrimaryHeld ? mask : 0u,
            heldMask: k.PrimaryHeld ? mask : 0u,
            aimX: FixedRight,
            aimY: 0,
            aimZ: 0,
            moveX: (int)MathF.Round(Math.Clamp(k.MoveX, -1f, 1f) * FixedOne),
            moveY: (int)MathF.Round(Math.Clamp(k.MoveY, -1f, 1f) * FixedOne)));
    }
}
