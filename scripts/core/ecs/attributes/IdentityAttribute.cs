namespace Game.Core.Ecs;

/// <summary>
/// 标记组件里「实体自身身份」的那一个字段（ECS.md §4.2 / J1 的执行方式）。
/// <para>
/// <b>为什么需要它</b>：chunk 里必须能原地写回 <see cref="Entity"/> 值（批量生成、槽位回收复用、
/// 原型迁移），但内核不知道业务组件长什么样。有了这个特性，内核只在「注册期读一次」反射元数据
/// （<see cref="ComponentTypeRegistry"/> 把结果缓存成一次性的写委托），之后把新实体号写进组件
/// 时不经过任何反射，也不要求系统手写 <c>c.Unit = e;</c>（漏写 = 最难查的一类静默 bug）。
/// </para>
/// <para>
/// <b>铁律</b>（违反在<u>该组件首次被内核使用</u>时抛 <see cref="GameEcsException"/>，信息含组件名）：
/// <list type="number">
///   <item>每个组件类型<u>有且只有一个</u> <c>[Identity]</c> 字段；</item>
///   <item>被标记字段的类型必须正好是 <see cref="Entity"/>；</item>
///   <item>被标记字段必须是该组件<u>布局上的第一个字段</u>。这不是洁癖：ECS.md §4.2 的组件表都按
///         「身份在前」书写，§9 的 C++ 侧还要按固定偏移读 chunk，字段顺序本身就是内存契约。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么允许一次反射</b>：组件是运行时泛型（<c>World.Create&lt;TA,TB&gt;()</c>），编译器无法逐个检查；
/// 注册期是本项目唯一允许出现反射的地方（docs/rules/02-tech-selection.md §3.3 的例外），而这里的错误
/// 必须尽早炸——注册期炸最便宜。
/// </para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class IdentityAttribute : System.Attribute
{
}
