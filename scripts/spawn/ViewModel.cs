using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Core.Ecs;

namespace Game.Spawn;

/// <summary>
/// 「一种实体的组件组合」的**声明式描述**：生成时把 N 次 <c>Create&lt;T&gt;</c> 收敛成一次
/// <c>CreateDeferred(components)</c>，避免每帧生成时反复查表拼原型。
/// <para>
/// <b>为什么需要这一层</b>：<see cref="CommandBuffer"/> 的 <c>CreateDeferred</c> 要求一次性给出
/// 完整组件类型列表（内核据此选原型）。如果每个生成点都自己 <c>new List&lt;ComponentType&gt;</c>，
/// 一是稳态会分配，二是「子弹有哪些组件」这件事散落在多处，改一处漏一处。
/// 收敛到几个静态只读列表后，新增一种实体 = 加一个静态属性（规则7：改 1 个文件）。
/// </para>
/// <para>
/// <b>线程安全</b>：列表在首次访问时构建，构建后只读。主线程构世界期间预热一次即可，
/// 不要在 job 里首次访问（<see cref="Lazy{T}"/> 的默认线程安全已覆盖，但预热更省心）。
/// </para>
/// </summary>
public static class ViewModel
{
    /// <summary>
    /// 子弹的组件组合：<see cref="Projectile"/> + <see cref="Transform3"/> + <see cref="Velocity"/>
    /// + <see cref="Lifetime"/> + <see cref="Team"/>。
    /// <para>
    /// <b>与 <c>FireProjectileSkill</c> 造的子弹逐项一致</b>：那份清单是
    /// <c>Game.Combat.Projectile / Transform3 / Velocity / Lifetime / Team</c> 五项，
    /// 两处必须保持同一形状 —— 缺 <see cref="Lifetime"/> 子弹不被推进，缺 <see cref="Transform3"/>
    /// 则 <c>ProjectileSystem</c>/<c>HitDetectionSystem</c> 的查询<u>静默</u>把子弹排除在外（那正是
    /// 「同名不同命名空间选错也能编译」事故的形状，见 <c>OfficialComponents.cs</c> 顶部的指针注释）。
    /// </para>
    /// <para>
    /// <b>哪来的 <see cref="Projectile"/></b>：本文件 <c>using Game.Combat;</c>，所以
    /// <see cref="Projectile"/> 绑定的是唯一真源 <c>Game.Combat.Projectile</c>。
    /// <b>不要</b>在这里改用 <c>Game.Core.Ecs</c> 下的任何同名类型（那个示例组件已于 2026-11-05 删除）。
    /// </para>
    /// <para>
    /// <b>唯一的差异</b>：这里没有 <see cref="View"/>。视图由宿主 <c>VisualBridge</c> 按
    /// <see cref="Projectile"/> 的存在性补挂，而不是由生成侧写死 —— 生成侧只负责玩法组件，
    /// 表现层挂件属于宿主边界（J4）。
    /// </para>
    /// </summary>
    /// <param name="world">用于把类型注册进 <see cref="ComponentTypeRegistry"/>。</param>
    /// <returns>该原型要含的组件类型列表。</returns>
    public static IReadOnlyList<ComponentType> Components(World world)
    {
        if (world is null)
        {
            throw new ArgumentNullException(nameof(world));
        }

        // 每个组件都走 world.Register<T>()：它会返回（或首次注册）该类型的 ComponentType。
        // 显式逐个列出而不是反射扫描，是为了让「子弹到底有哪些组件」在 Code Review 时一眼可见
        // （ECS.md §4.1：组件组合即实体身份声明，必须显式）。
        return new[]
        {
            world.Register<Projectile>(),
            world.Register<Transform3>(),
            world.Register<Velocity>(),
            world.Register<Lifetime>(),
            world.Register<Team>(),
        };
    }
}
