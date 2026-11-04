using System;
using Game.Core;
using Game.Core.Skills;
using Godot;

namespace Game.Host.Control;

/// <summary>
/// 把引擎输入翻译成<u>意图</u>的适配器（宿主层唯一允许碰 <see cref="Input"/> 的地方，见 ECS.md J4）。
/// <para>
/// <b>为什么角色逻辑不能直接读 Input</b>：技能系统只认 <see cref="InputIntent"/>，
/// 因此同一套技能逻辑既能被键盘驱动、也能被 AI 或录制回放驱动。这条边界一旦破
/// （在系统里直接 <c>Input.IsActionPressed</c>），AI 与玩家就无法共用代码，
/// 而"怪物与角色同源"（需求⑤）正是靠共用代码实现的。
/// </para>
/// <para>
/// <b>槽位 → 物理按键的映射放在哪里</b>：映射不是玩法数值，是<u>操作方案</u>，
/// 因此用 Godot 的 InputMap action 名（<c>project.godot</c> 里配置，玩家可改键），
/// 本类只负责"action 名 → 槽位"的固定对应。
/// </para>
/// </summary>
public sealed class PlayerInputIntentSource : IIntentSource
{
    /// <summary>各槽位对应的 InputMap action 名（与 project.godot 的 [input] 段一致）。</summary>
    private readonly string?[] _actionOfSlot = new string?[SkillSlotExtensions.TableLength];

    /// <summary>瞄准轴 action 名（鼠标相对屏幕中心或手柄右摇杆）。</summary>
    private readonly string _aimAxisAction;

    /// <summary>
    /// 构造并绑定默认按键方案：Shift=冲刺、左键=攻击、Q=切换武器、R=独有技能、F=交互。
    /// </summary>
    public PlayerInputIntentSource()
        : this(
            aimAxisAction: "actor_aim",
            dash: "actor_dash",
            primaryAttack: "actor_primary_attack",
            weaponSwap: "actor_weapon_swap",
            signature: "actor_signature",
            interact: "actor_interact")
    {
    }

    /// <summary>可替换 action 名的构造（手柄方案、测试、改键插件用）。</summary>
    public PlayerInputIntentSource(
        string aimAxisAction,
        string dash,
        string primaryAttack,
        string weaponSwap,
        string signature,
        string interact)
    {
        _aimAxisAction = aimAxisAction ?? string.Empty;
        _actionOfSlot[(int)SkillSlot.Dash] = dash;
        _actionOfSlot[(int)SkillSlot.PrimaryAttack] = primaryAttack;
        _actionOfSlot[(int)SkillSlot.WeaponSwap] = weaponSwap;
        _actionOfSlot[(int)SkillSlot.Signature] = signature;
        _actionOfSlot[(int)SkillSlot.Interact] = interact;
    }

    /// <summary>上一帧的按下掩码（用来把"按住"转换成"本帧按下"）。</summary>
    private uint _previousPressed;

    /// <summary>最近一次瞄准方向（鼠标离开窗口或轴为 0 时沿用，避免准心乱跳）。</summary>
    private Vector3 _lastAim = new Vector3(1f, 0f, 0f);

    /// <inheritdoc />
    public InputIntent Poll(int actorId)
    {
        uint held = 0;
        for (int slot = 1; slot < _actionOfSlot.Length; slot++)
        {
            string? action = _actionOfSlot[slot];
            if (!string.IsNullOrEmpty(action) && IsActionHeld(action!))
            {
                held |= InputIntent.MaskOf((SkillSlot)slot);
            }
        }

        // 边沿：本帧按下 = 本帧按住 - 上一帧按住。只在"刚按下"触发，防止按住刷技能。
        uint pressed = held & ~_previousPressed;
        _previousPressed = held;

        Vector3 aim = ReadAim();
        // 方向用定点整数表达（Q16：±65536 = ±1.0），与 ECS 组件的 int 约定一致，避免 float 进组件。
        return new InputIntent(
            pressed,
            held,
            ToFixed(aim.X),
            ToFixed(aim.Y),
            ToFixed(aim.Z));
    }

    /// <summary>方向分量 → Q16 定点。</summary>
    private static int ToFixed(float value) => (int)MathF.Round(value * FixedOne);

    /// <summary>Q16 的 1.0。</summary>
    private const int FixedOne = 1 << 16;

    /// <summary>
    /// 每帧的边沿状态依赖"上一帧"，因此切换角色/暂停恢复时需要重置，否则会吞掉一次输入。
    /// </summary>
    public void Reset()
    {
        _previousPressed = 0;
        _lastAim = new Vector3(1f, 0f, 0f);
    }

    /// <summary>
    /// 读瞄准方向：优先鼠标在屏幕上的世界朝向，退化时用输入轴。
    /// <para>鼠标优先是因为俯视角射击的瞄准手感以鼠标为准；手柄右摇杆只在鼠标不动时接管。</para>
    /// </summary>
    private Vector3 ReadAim()
    {
        Vector2 axis = ReadAxis(_aimAxisAction);
        if (axis.LengthSquared() > AimDeadzoneSquared)
        {
            _lastAim = new Vector3(axis.X, 0f, axis.Y).Normalized();
        }
        return _lastAim;
    }

    /// <summary>轴死区（平方值）：手柄漂移不该让准心抖动。</summary>
    private const float AimDeadzoneSquared = 0.04f;

    private static Vector2 ReadAxis(string action)
    {
        if (string.IsNullOrEmpty(action) || !HasAction(action))
        {
            return Vector2.Zero;
        }
        return Input.GetVector(
            string.Concat(action, "_left"),
            string.Concat(action, "_right"),
            string.Concat(action, "_up"),
            string.Concat(action, "_down"),
            AimDeadzoneSquared);
    }

    private static bool IsActionHeld(string action) => HasAction(action) && Input.IsActionPressed(action);

    private static bool HasAction(string action) => InputMap.HasAction(action);
}
