using System;

namespace Game.Core.Ecs;

/// <summary>
/// 组件类型注册表：组件 id 分配 + 元数据解析。每个 <see cref="World"/> 可拥有私有实例
/// （测试友好：两个随机建组件的测试互不影响 id 空间，也不会因为注册顺序不同而漂移）。
/// <para>
/// <b>反射只在这里出现一次</b>：每个组件类型<u>首次注册</u>时，用反射检查
/// 「正好一个 [Identity] 字段、类型是 Entity、偏移 0」，并生成强类型元数据包
/// （<see cref="ComponentTypeInfo{T}"/>）。注册之后所有批量生成/迁移路径都只碰委托，
/// 满足 docs/rules/02-tech-selection.md 第 3.3 节「注册期一次性反射除外」的例外条款。
/// </para>
/// <para>
/// <b>为什么不是 static 全局注册表</b>：A4（静态可变全局状态）会污染单测、无法多世界并存。
/// 大部分业务代码不需要关心它——<c>World.Create&lt;T&gt;()</c> 会自动在所属世界里注册。
/// 需要跨世界共享 id 时，把同一个实例传给多个 World。
/// </para>
/// </summary>
public sealed class ComponentTypeRegistry
{
    private const int InitialTypeCount = 16;

    private readonly System.Collections.Generic.Dictionary<Type, ComponentTypeInfo> _byType;
    private readonly ComponentTypeInfo?[] _byId;
    private int _nextId;

    /// <summary>创建一个空注册表。</summary>
    public ComponentTypeRegistry()
    {
        _byType = new System.Collections.Generic.Dictionary<Type, ComponentTypeInfo>(InitialTypeCount);
        _byId = new ComponentTypeInfo?[ComponentType.MaxId + 1];
        _nextId = 0;
    }

    /// <summary>已注册的组件种类数。</summary>
    public int Count => _nextId;

    /// <summary>取或注册 <typeparamref name="T"/>。</summary>
    public ComponentType Register<T>() where T : unmanaged => Register(typeof(T));

    /// <summary>
    /// 取或注册 <paramref name="type"/>。非 unmanaged 类型会被拒绝：
    /// 组件必须能无装箱地放进连续内存（ECS.md J2 / 第 4.2 节）。
    /// </summary>
    public ComponentType Register(Type type)
    {
        if (type is null)
        {
            throw new GameEcsException("组件类型不能为 null。");
        }
        if (_byType.TryGetValue(type, out ComponentTypeInfo? existing))
        {
            return existing.Data;
        }
        if (!type.IsValueType)
        {
            throw GameEcsException.InvalidComponent(type, "组件必须是 unmanaged struct（值类型），不能是 class。");
        }
        if (type.ContainsGenericParameters)
        {
            throw GameEcsException.InvalidComponent(type, "组件类型不能是开放泛型。");
        }
        if (type.IsByRef || type.IsPointer)
        {
            throw GameEcsException.InvalidComponent(type, "组件类型不能是引用/指针类型。");
        }
        if (!IsUnmanaged(type))
        {
            throw GameEcsException.InvalidComponent(type, "组件必须是 unmanaged struct（不得包含引用类型字段）。");
        }
        if (_nextId > ComponentType.MaxId)
        {
            throw new GameEcsException(
                string.Concat("组件种类已超过上限 ", ComponentType.MaxId.ToString(), "，请调整 TypeSet 的容量常量。"));
        }

        // ── 注册期唯一一次反射：解析并校验 [Identity] 字段。 ──
        System.Reflection.FieldInfo[] fields = type.GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        System.Reflection.FieldInfo? identityField = null;
        for (int i = 0; i < fields.Length; i++)
        {
            System.Reflection.FieldInfo field = fields[i];
            if (!field.IsDefined(typeof(IdentityAttribute), inherit: false))
            {
                continue;
            }
            if (identityField is not null)
            {
                throw GameEcsException.InvalidComponent(type, "一个组件只能有一个 [Identity] 字段。");
            }
            identityField = field;
        }
        if (identityField is null)
        {
            throw GameEcsException.InvalidComponent(
                type,
                "缺少 [Identity] 字段。每个组件必须正好有一个标记为 [Identity] 的 Entity 字段（见 ECS.md 第 4.2 节与 J1）。");
        }
        if (identityField.FieldType != typeof(Entity))
        {
            throw GameEcsException.InvalidComponent(
                type,
                string.Concat("[Identity] 字段 ", identityField.Name, " 的类型必须是 Entity，实际是 ", identityField.FieldType.Name, "。"));
        }
        int offset = (int)System.Runtime.InteropServices.Marshal.OffsetOf(type, identityField.Name);
        if (offset != 0)
        {
            throw GameEcsException.InvalidComponent(
                type,
                string.Concat("[Identity] 字段 ", identityField.Name, " 必须声明为组件的第一个字段（当前偏移 ",
                    offset.ToString(), " 字节）。字段顺序是 chunk 与 C++ 侧（ECS.md 第 9 节）的内存契约。"));
        }

        int id = _nextId;
        ComponentType data = new ComponentType(id, type, System.Runtime.InteropServices.Marshal.SizeOf(type));
        ComponentTypeInfo info = CreateInfo(type, data);

        _byType.Add(type, info);
        _byId[id] = info;
        _nextId = id + 1;

        // 填充静态泛型缓存（每个 T 一份，之后热路径无字典查找）。
        FillStaticCache(type, data);
        return data;
    }

    /// <summary>按类型取已注册的元数据包；未注册返回 false（不抛）。</summary>
    public bool TryGetByType(Type type, out ComponentTypeInfo info)
    {
        if (_byType.TryGetValue(type, out ComponentTypeInfo? found))
        {
            info = found;
            return true;
        }
        info = null!;
        return false;
    }

    /// <summary>按类型取元数据包；未注册抛异常。</summary>
    public ComponentTypeInfo GetByType(Type type)
    {
        if (_byType.TryGetValue(type, out ComponentTypeInfo? found))
        {
            return found;
        }
        throw new GameEcsException(string.Concat("组件类型 ", type.Name, " 尚未在该注册表中注册。"));
    }

    /// <summary>按 id 取元数据包。</summary>
    public ComponentTypeInfo GetById(int id)
    {
        if ((uint)id >= (uint)_byId.Length)
        {
            throw new GameEcsException(string.Concat("组件 id ", id.ToString(), " 超出合法范围（应 0..", ComponentType.MaxId.ToString(), "）。"));
        }
        ComponentTypeInfo? found = _byId[id];
        if (found is null)
        {
            throw new GameEcsException(string.Concat("组件 id ", id.ToString(), " 未注册。"));
        }
        return found;
    }

    /// <summary>
    /// 用 <c>typeof(ComponentTypeInfo&lt;&gt;).MakeGenericType(type)</c> 在注册期构造一次元数据包。
    /// 这是整个内核第二次（也是最后一次）反射：之后建列、写身份都走强类型委托。
    /// </summary>
    private static ComponentTypeInfo CreateInfo(Type type, ComponentType data)
    {
        Type infoType = typeof(ComponentTypeInfo<>).MakeGenericType(type);
        object[] args = new object[] { data };
        return (ComponentTypeInfo)(Activator.CreateInstance(infoType, args)
            ?? throw GameEcsException.InvalidComponent(type, "内部错误：无法构造元数据包。"));
    }

    /// <summary>
    /// 把元数据写进 <see cref="ComponentMeta{T}"/> 这个静态泛型槽。
    /// 这里必须用反射调用泛型方法（因为运行时才知道 T），但每个组件只执行一次。
    /// </summary>
    private static void FillStaticCache(Type type, ComponentType data)
    {
        System.Reflection.MethodInfo method = typeof(ComponentTypeRegistry)
            .GetMethod(nameof(FillStaticCacheGeneric), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type);
        method.Invoke(null, new object[] { data });
    }

    /// <summary>泛型桥（仅在注册期由反射调用一次）。</summary>
    private static void FillStaticCacheGeneric<T>(ComponentType data) where T : unmanaged
    {
        // 只填 Type，不填 Owner：Owner 由 World.Register<T> 按「当前世界用自己的注册表」写入，
        // 这样缓存才不会把 id 误导给另一个注册表（见 ComponentMeta<T> 的注释）。
        ComponentMeta<T>.Type = data;
    }

    /// <summary>判断一个值类型是否是 unmanaged（无引用字段）。</summary>
    private static bool IsUnmanaged(Type type)
    {
        if (!type.IsValueType || type.ContainsGenericParameters)
        {
            return false;
        }
        if (type.IsPrimitive || type.IsEnum || type.IsPointer)
        {
            return true;
        }
        System.Reflection.FieldInfo[] fields = type.GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        for (int i = 0; i < fields.Length; i++)
        {
            if (!IsUnmanaged(fields[i].FieldType))
            {
                return false;
            }
        }
        return true;
    }
}
