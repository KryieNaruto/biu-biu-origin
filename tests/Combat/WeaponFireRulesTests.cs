using Game.Combat;
using Game.Core.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Combat;

/// <summary>
/// <b>武器触发方式的语义测试</b>（<see cref="WeaponFireRules"/> 纯函数层）。
/// <para>
/// <b>为什么这组测试是本次任务的核心</b>：历史上 <c>WeaponTrigger</c> 只在绑定器里被解析，
/// 没有任何代码读它 —— 表里填 <c>Semi</c> 还是 <c>Auto</c> 行为完全一样，且<b>没有任何测试能发现</b>
/// （所有用例都只用默认值）。本文件的存在就是为了让"表驱动失效"这类事故再也无法静默通过：
/// 每一种触发方式都必须有一条断言证明它与其它方式<b>行为不同</b>。
/// </para>
/// </summary>
[TestClass]
public sealed class WeaponFireRulesTests
{
    private const float Interval = 0.1f;
    private const float ChargeTime = 1.0f;
    private const int HasAmmo = 10;

    private static WeaponFireDecision Eval(
        WeaponTrigger trigger,
        bool pressed,
        bool held,
        float cooldown = 0f,
        int ammo = HasAmmo,
        float chargeElapsed = 0f,
        float chargeTime = ChargeTime,
        bool isCharging = false)
    {
        return WeaponFireRules.Evaluate(
            trigger, pressed, held, Interval, cooldown, ammo, chargeElapsed, chargeTime, isCharging);
    }

    // ── Auto：按住即连射 ──

    [TestMethod]
    public void Auto_Held_FiresWhenCooldownReady()
    {
        Assert.AreEqual(WeaponFireDecision.Fire, Eval(WeaponTrigger.Auto, pressed: false, held: true));
    }

    [TestMethod]
    public void Auto_Held_BlockedByCooldown()
    {
        Assert.AreEqual(WeaponFireDecision.Cooldown, Eval(WeaponTrigger.Auto, pressed: false, held: true, cooldown: 0.05f));
    }

    [TestMethod]
    public void Auto_NotHeld_IsIdle()
    {
        Assert.AreEqual(WeaponFireDecision.Idle, Eval(WeaponTrigger.Auto, pressed: false, held: false));
    }

    // ── Semi：点击一次打一次（这是与 Auto 的唯一区别，也最常被做错）──

    [TestMethod]
    public void Semi_Pressed_Fires()
    {
        Assert.AreEqual(WeaponFireDecision.Fire, Eval(WeaponTrigger.Semi, pressed: true, held: false));
    }

    [TestMethod]
    public void Semi_HeldWithoutPress_DoesNotFire()
    {
        // ★ 关键断言：按住不放，Semi 不发第二发。
        Assert.AreEqual(WeaponFireDecision.Idle, Eval(WeaponTrigger.Semi, pressed: false, held: true));
    }

    [TestMethod]
    public void SemiAndAuto_DifferOnHeld()
    {
        // ★ 直接对照：同一组输入下两种触发必须给出不同结论，
        // 否则说明 Trigger 列又变成了摆设。
        WeaponFireDecision auto = Eval(WeaponTrigger.Auto, pressed: false, held: true);
        WeaponFireDecision semi = Eval(WeaponTrigger.Semi, pressed: false, held: true);
        Assert.AreNotEqual(auto, semi, "Auto 与 Semi 在「按住不放」时必须行为不同，否则 Trigger 列是死的。");
        Assert.AreEqual(WeaponFireDecision.Fire, auto);
        Assert.AreEqual(WeaponFireDecision.Idle, semi);
    }

    // ── Charge：蓄力后松手才发射 ──

    [TestMethod]
    public void Charge_Press_StartsChargingWithoutFiring()
    {
        Assert.AreEqual(WeaponFireDecision.Charging, Eval(WeaponTrigger.Charge, pressed: true, held: false, isCharging: false));
    }

    [TestMethod]
    public void Charge_Held_KeepsCharging()
    {
        Assert.AreEqual(WeaponFireDecision.Charging, Eval(WeaponTrigger.Charge, pressed: false, held: true, chargeElapsed: 0.5f, isCharging: true));
    }

    [TestMethod]
    public void Charge_ReleasedAfterCharging_Fires()
    {
        Assert.AreEqual(WeaponFireDecision.Fire, Eval(WeaponTrigger.Charge, pressed: false, held: false, chargeElapsed: ChargeTime, isCharging: true));
    }

    [TestMethod]
    public void Charge_ReleasedWithoutCharging_IsIdle()
    {
        // 从没蓄力就松手：不该凭空打出一发。
        Assert.AreEqual(WeaponFireDecision.Idle, Eval(WeaponTrigger.Charge, pressed: false, held: false, isCharging: false));
    }

    [TestMethod]
    public void Charge_NeverFiresWhileHeld()
    {
        // ★ 蓄力武器的标志：按住期间一发都不打。
        Assert.AreNotEqual(WeaponFireDecision.Fire, Eval(WeaponTrigger.Charge, pressed: false, held: true, chargeElapsed: ChargeTime, isCharging: true));
    }

    // ── 串射 / 持续 / 事件驱动 ──

    [TestMethod]
    public void Burst_Pressed_Fires()
    {
        Assert.AreEqual(WeaponFireDecision.Fire, Eval(WeaponTrigger.Burst, pressed: true, held: false));
    }

    [TestMethod]
    public void Burst_HeldWithoutPress_IsIdle()
    {
        Assert.AreEqual(WeaponFireDecision.Idle, Eval(WeaponTrigger.Burst, pressed: false, held: true));
    }

    [TestMethod]
    public void Hold_Held_Fires()
    {
        Assert.AreEqual(WeaponFireDecision.Fire, Eval(WeaponTrigger.Hold, pressed: false, held: true));
    }

    [TestMethod]
    public void Triggered_IgnoresInput()
    {
        // 由事件驱动，即使按住也不该由输入打出火。
        Assert.AreEqual(WeaponFireDecision.NotInputDriven, Eval(WeaponTrigger.Triggered, pressed: true, held: true));
    }

    [TestMethod]
    public void Manual_IgnoresInput()
    {
        Assert.AreEqual(WeaponFireDecision.NotInputDriven, Eval(WeaponTrigger.Manual, pressed: true, held: true));
    }

    // ── 弹药门控优先于一切 ──

    [DataTestMethod]
    [DataRow(WeaponTrigger.Auto)]
    [DataRow(WeaponTrigger.Semi)]
    [DataRow(WeaponTrigger.Burst)]
    [DataRow(WeaponTrigger.Charge)]
    [DataRow(WeaponTrigger.Hold)]
    public void EmptyAmmo_BlocksEveryInputDrivenTrigger(WeaponTrigger trigger)
    {
        // 打空了按什么都不会响（包括蓄力也不该起手）。
        Assert.AreEqual(WeaponFireDecision.Empty, Eval(trigger, pressed: true, held: true, ammo: 0));
    }

    [TestMethod]
    public void UnlimitedAmmo_IsNotTreatedAsEmpty()
    {
        // ammo &lt; 0 表示不限弹。
        Assert.AreEqual(WeaponFireDecision.Fire, Eval(WeaponTrigger.Semi, pressed: true, held: false, ammo: -1));
    }

    // ── 蓄力进度 ──

    [DataTestMethod]
    [DataRow(0f, 1.0f, 0f)]
    [DataRow(0.5f, 1.0f, 0.5f)]
    [DataRow(1.0f, 1.0f, 1f)]
    [DataRow(2.0f, 1.0f, 1f)]
    [DataRow(0.5f, 0f, 1f)]
    public void ChargeRatio_ClampsAndHandlesInstant(float elapsed, float total, float expected)
    {
        Assert.AreEqual(expected, WeaponFireRules.ChargeRatio(elapsed, total), 1e-5f);
    }

    // ── 全枚举覆盖：确保不是漏掉某种触发 ──

    [TestMethod]
    public void EveryTrigger_IsClassified()
    {
        // 这条测试的作用：将来有人给 WeaponTrigger 加了一个新值却没在规则里处理时，
        // 它会立刻失败（默认分支返回 Idle 会让新触发方式悄悄变成"永远不开火"）。
        foreach (WeaponTrigger trigger in System.Enum.GetValues<WeaponTrigger>())
        {
            WeaponFireDecision decision = Eval(trigger, pressed: true, held: true);
            bool isEventDriven = trigger == WeaponTrigger.Triggered || trigger == WeaponTrigger.Manual;
            if (isEventDriven)
            {
                Assert.AreEqual(WeaponFireDecision.NotInputDriven, decision, trigger.ToString());
            }
            else
            {
                Assert.AreNotEqual(WeaponFireDecision.Idle, decision,
                    "触发方式 " + trigger.ToString() + " 在「按下+保持」时不该是 Idle —— 说明规则里缺这个分支。");
            }
        }
    }
}
