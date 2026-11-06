using Game.Core;
using Game.Core.Content;

namespace Game.Combat;

/// <summary>
/// 把武器契约里的 <see cref="ValueSet"/> 求值成开火系统需要的标量。
/// <para>
/// <b>为什么单独抽一个类</b>：<c>WeaponFireSystem</c> 里的规则判定必须是纯函数才好测，
/// 而"求值"需要 <see cref="ValueContext"/>（可能掷随机数、查数值包）。
/// 把求值隔离到这里，规则表就不需要知道 ValueSet 的存在；
/// 将来数值来源从"契约直读"换成"数值包层叠"时，只改本类。
/// </para>
/// <para>
/// <b>为什么用 <see cref="WeaponState"/> 而不是实体</b>：开火阶段的求值上下文只要能拿到
/// 武器自己的属性就够了；敌人/玩家的数值包层叠属于伤害结算侧，不在射速门控的范围内。
/// </para>
/// </summary>
public static class WeaponValues
{
    /// <summary>一次开火的最小间隔（秒）。表里 &lt;= 0 时按"每帧都能打"处理。</summary>
    public static float ResolveFireInterval(ContentIndex index, WeaponContract contract, in WeaponState weapon)
    {
        return Resolve(index, contract.FireDelay, in weapon);
    }

    /// <summary>蓄满所需时长（秒）。&lt;= 0 表示瞬发。</summary>
    public static float ResolveChargeTime(ContentIndex index, WeaponContract contract, in WeaponState weapon)
    {
        return Resolve(index, contract.ChargeTime, in weapon);
    }

    /// <summary>连发间隔（秒）。</summary>
    public static float ResolveBurstInterval(ContentIndex index, WeaponContract contract, in WeaponState weapon)
    {
        return Resolve(index, contract.BurstInterval, in weapon);
    }

    /// <summary>连发次数。</summary>
    public static int ResolveBurstCount(ContentIndex index, WeaponContract contract, in WeaponState weapon)
    {
        float count = Resolve(index, contract.BurstCount, in weapon);
        return count > 0f ? (int)count : 1;
    }

    /// <summary>
    /// 统一求值入口。
    /// <para>
    /// 用 <see cref="ForgeValueWorld"/> 的"无数值包"形态建上下文：武器自身的 FireDelay 通常就是
    /// 一个 <see cref="ValueSource.Literal"/> 或一层层叠，不需要实体数值包也能求。
    /// 若某条规则引用了 <c>wstat:</c>（武器属性），取值回落到契约自带的对应字段。
    /// </para>
    /// </summary>
    private static float Resolve(ContentIndex index, in ValueSet set, in WeaponState weapon)
    {
        ForgeValueWorld world = new ForgeValueWorld(index, registry: null!, chainDepth: 0, 0, 1, null!, null!);
        ValueContext ctx = new ValueContext(world, default, null!);
        float value = ValueResolver.Resolve(set, in ctx);
        return value;
    }
}
