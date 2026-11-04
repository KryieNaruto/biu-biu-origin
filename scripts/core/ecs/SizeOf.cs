using System.Runtime.CompilerServices;

namespace Game.Core.Ecs;

/// <summary>
/// 结构体大小的唯一取值处。
/// <para>
/// <b>为什么封一层</b>：<c>RuntimeHelpers.SizeOf&lt;T&gt;()</c> 只有在<u>泛型上下文</u>里才会按 T 特化；
/// 写在非泛型方法里会被解析成基于 <see cref="System.RuntimeTypeHandle"/> 的重载（编译期实测报 CS0308）。
/// 集中到 <c>AggressiveInlining</c> 的泛型成员里，既保证每个 T 各取各的大小，也保证热路径常量折叠。
/// </para>
/// <para>
/// <b>为什么不用 <c>Marshal.SizeOf</c></b>：那是「封送大小」，会把 <c>bool</c> 当 4 字节并按平台规则膨胀；
/// chunk 内存统计与 C++ 侧（ECS.md 第 9 节）要的是<u>托管布局</u>大小。
/// </para>
/// </summary>
internal static class SizeOf
{
    /// <summary><typeparamref name="T"/> 的托管布局字节数（泛型特化）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Of<T>() where T : unmanaged => Unsafe.SizeOf<T>();

    /// <summary><see cref="Entity"/> 的字节数（实体号列与组件内嵌的 Identity 字段都用它）。</summary>
    public static int EntityBytes => Unsafe.SizeOf<Entity>();
}
