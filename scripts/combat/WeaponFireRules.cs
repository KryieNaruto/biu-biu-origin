namespace Game.Combat;

/// <summary>
/// 一帧内武器"要不要开火"的判定结果。
/// <para>
/// <b>为什么单独一个枚举而不是 bool</b>：门控失败的原因要能反馈给 HUD 与音效
/// （"空弹夹的咔哒声"和"冷却中的静默"不是同一件事），也要让测试能断言
/// <i>为什么没开火</i>，而不只是"没开火"。这与 <c>SkillCastFailure</c> 保持同一纪律。
/// </para>
/// </summary>
public enum WeaponFireDecision : byte
{
    /// <summary>本帧开火。</summary>
    Fire = 0,

    /// <summary>没有开火意图（未按下 / 未保持）。</summary>
    Idle = 1,

    /// <summary>射速冷却未走完。</summary>
    Cooldown = 2,  // arch-ok: 判别式取值（枚举序号），不是玩法数值

    /// <summary>弹夹打空。</summary>
    Empty = 3,  // arch-ok: 判别式取值（枚举序号）

    /// <summary>正在蓄力，尚未到可释放状态（<see cref="WeaponTrigger.Charge"/>）。</summary>
    Charging = 4,  // arch-ok: 判别式取值（枚举序号）

    /// <summary>这种触发方式不响应直接输入（<see cref="WeaponTrigger.Triggered"/> / <see cref="WeaponTrigger.Manual"/>）。</summary>
    NotInputDriven = 5,  // arch-ok: 判别式取值（枚举序号）
}

/// <summary>
/// <b>武器开火规则</b>：纯函数集合，把 <see cref="Game.Core.Content.WeaponTrigger"/> 翻译成
/// 「本帧开火 / 开始蓄力 / 继续蓄力 / 什么都不做」。
/// <para>
/// <b>为什么是静态纯函数而不是直接写在系统里</b>：射速与蓄力是肉鸽手感的核心，
/// 必须能被表驱动地单独测（"Auto 在按住时按 FireInterval 节奏开火""Semi 按住只打一发"
/// 这类断言不能靠跑整个 World 才能验证）。系统只负责读组件→调本类→回写组件→投递请求。
/// </para>
/// <para>
/// <b>状态放在哪</b>：本类不持有任何状态，全部经 <c>ref WeaponFireRuntime</c> 进出，
/// 于是"上一次开火时间""已蓄力多少"这些数是组件的，不是规则表的（J1 单一所有者）。
/// </para>
/// </summary>
public static class WeaponFireRules
{
    /// <summary>
    /// 判定本帧是否应该开火。
    /// </summary>
    /// <param name="trigger">武器的触发方式（来自契约）。</param>
    /// <param name="pressed">本帧是否按下（边沿）。</param>
    /// <param name="held">本帧是否保持。</param>
    /// <param name="fireInterval">两次开火的最小间隔（秒，来自契约 FireDelay 求值）。</param>
    /// <param name="cooldownRemaining">冷却剩余（秒，&gt; 0 表示还不能打）。</param>
    /// <param name="ammoCurrent">当前弹药（&lt; 0 表示不限弹）。</param>
    /// <param name="chargeElapsed">已蓄力时长（秒）。</param>
    /// <param name="chargeTime">蓄满所需时长（秒）。</param>
    /// <param name="isCharging">当前是否处于蓄力中。</param>
    /// <returns>本帧的判定。</returns>
    public static WeaponFireDecision Evaluate(
        Game.Core.Content.WeaponTrigger trigger,
        bool pressed,
        bool held,
        float fireInterval,
        float cooldownRemaining,
        int ammoCurrent,
        float chargeElapsed,
        float chargeTime,
        bool isCharging)
    {
        // 不响应输入的两种：由事件 / 脚本驱动，直接交给外部调用，不走本门控。
        if (trigger == Game.Core.Content.WeaponTrigger.Triggered
            || trigger == Game.Core.Content.WeaponTrigger.Manual)
        {
            return WeaponFireDecision.NotInputDriven;
        }

        // 弹夹门控先于一切：打空了按什么都不会响（蓄力也不该起手）。
        if (ammoCurrent == 0)
        {
            return WeaponFireDecision.Empty;
        }

        switch (trigger)
        {
            case Game.Core.Content.WeaponTrigger.Auto:
                // 按住即连射：只要按住 + 冷却走完就打。
                if (!held && !pressed)
                {
                    return WeaponFireDecision.Idle;
                }
                return cooldownRemaining > 0f ? WeaponFireDecision.Cooldown : WeaponFireDecision.Fire;

            case Game.Core.Content.WeaponTrigger.Semi:
                // 边沿触发：按住不会连打（这是它与 Auto 的唯一区别，也是最常被做错的一条）。
                if (!pressed)
                {
                    return WeaponFireDecision.Idle;
                }
                return cooldownRemaining > 0f ? WeaponFireDecision.Cooldown : WeaponFireDecision.Fire;

            case Game.Core.Content.WeaponTrigger.Burst:
                // 串射的"串内间隔"由系统按 BurstInterval 连续投递，这里只认起手边沿；
                // 串进行中再次按下不重置（否则会变成"边打边续"的永动机）。
                if (!pressed)
                {
                    return WeaponFireDecision.Idle;
                }
                return cooldownRemaining > 0f ? WeaponFireDecision.Cooldown : WeaponFireDecision.Fire;

            case Game.Core.Content.WeaponTrigger.Charge:
            {
                // 蓄力：按住累积时长，松开才发射；从未开始蓄力时松开 = 什么都不发生。
                if (held)
                {
                    // 蓄满后继续按住不再加时长（上限由表决定），但仍算"可继续蓄力"。
                    if (chargeTime > 0f && chargeElapsed >= chargeTime)
                    {
                        return WeaponFireDecision.Charging;
                    }
                    return WeaponFireDecision.Charging;
                }

                if (pressed && !isCharging)
                {
                    // 点击起手：本帧开始蓄力，不发射。
                    return WeaponFireDecision.Charging;
                }

                // 松手：只有真的蓄过才发射。
                if (!held && isCharging)
                {
                    return cooldownRemaining > 0f ? WeaponFireDecision.Cooldown : WeaponFireDecision.Fire;
                }
                return WeaponFireDecision.Idle;
            }

            case Game.Core.Content.WeaponTrigger.Hold:
                // 持续生效（光束 / 护盾）：按住期间每帧都"开火"（由下游按持续结算处理）。
                if (!held && !pressed)
                {
                    return WeaponFireDecision.Idle;
                }
                return cooldownRemaining > 0f ? WeaponFireDecision.Cooldown : WeaponFireDecision.Fire;

            default:
                return WeaponFireDecision.Idle;
        }
    }

    /// <summary>
    /// 蓄力进度（0..1）：HUD 画蓄力条、音效调音高都用它。
    /// <paramref name="chargeTime"/> &lt;= 0 时视为"瞬发"，恒为 1。
    /// </summary>
    public static float ChargeRatio(float chargeElapsed, float chargeTime)
    {
        if (chargeTime <= 0f)
        {
            return 1f;
        }
        float ratio = chargeElapsed / chargeTime;
        return ratio < 0f ? 0f : (ratio > 1f ? 1f : ratio);
    }
}
