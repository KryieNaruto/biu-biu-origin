using System;

namespace Game.Core.Ecs;

/// <summary>
/// 组件的池分配工厂（注册期生成的一次性委托）+ 身份写入器的类型擦除签名。
/// <para>
/// 两个委托只在这个文件里声明、在 <see cref="ComponentTypeRegistry"/> 里生成、在
/// <see cref="Archetype"/> / <see cref="ComponentPool{T}"/> 里调用——都是为了把「注册期的反射结果」
/// 变成「热路径上的直接调用」。
/// </para>
/// </summary>
internal delegate IComponentPool ComponentPoolFactory(int capacity);

/// <summary>
/// 把实体号写进某个组件列的第 slot 格。类型擦除版本（Archetype 用；不关心具体组件类型）。
/// </summary>
internal delegate void IdentityFieldWriter(IComponentPool pool, int slot, Entity entity);
