using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;

namespace Game.Actor;

/// <summary>
/// 角色定义仓库：把 Excel 生成物里的 <see cref="ActorDefinition"/> 收进定长槽位，供生成/装配按 id 查。
/// <para>
/// <b>为什么是"仓库 + 全局唯一槽位号"/b>：
/// </para>
/// <list type="bullet">
/// <item>组件里只能放值和 int（J2），所以定义必须被"编址"；</item>
/// <item>生成系统（<c>SpawnDirectorSystem</c>、房间内容生成器）每帧可能要刷上百个实体，
/// 按 string 查字典会带来哈希与分配；预解析成 int 之后热路径只剩数组下标；</item>
/// <item>槽位号在<u>进程内全局稳定</u>（同一个 id 永远同一个号），所以组件、存档、多世界都能安全共享。</item>
/// </list>
/// <para>
/// <b>线程</b>：只读查询安全；<see cref="Register"/> 只在装配期（关卡加载）调用，不在逐帧路径上。
/// </para>
/// </summary>
public sealed class ActorDefinitionRegistry
{
    /// <summary>"未注册"哨兵。用 -1 而不是 0，因为 0 是合法的第一个槽位。</summary>
    public const int NoneSlot = -1;

    private readonly List<ActorDefinition> _definitions = new List<ActorDefinition>();
    private readonly Dictionary<string, int> _byId = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>已注册的定义数。</summary>
    public int Count => _definitions.Count;

    /// <summary>注册一个角色定义（同 id 重复注册会抛异常：数据错误必须当场炸）。</summary>
    public int Register(ActorDefinition definition)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }
        if (_byId.TryGetValue(definition.Id, out int existing))
        {
            throw new ArgumentException(string.Concat("角色定义 id 重复注册：'", definition.Id,
                "'（已存在槽位 ", existing.ToString(), "）。Excel Characters 表主键必须唯一。"), nameof(definition));
        }
        int slot = _definitions.Count;
        _definitions.Add(definition);
        _byId.Add(definition.Id, slot);
        return slot;
    }

    /// <summary>批量注册。</summary>
    public void RegisterAll(IEnumerable<ActorDefinition> definitions)
    {
        if (definitions is null)
        {
            throw new ArgumentNullException(nameof(definitions));
        }
        foreach (ActorDefinition def in definitions)
        {
            Register(def);
        }
    }

    /// <summary>按 id 查槽位号；不存在返回 <see cref="NoneSlot"/>。</summary>
    public int SlotOf(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return NoneSlot;
        }
        return _byId.TryGetValue(id, out int slot) ? slot : NoneSlot;
    }

    /// <summary>按槽位号取定义；越界返回 <c>null</c>。</summary>
    public ActorDefinition? Get(int slot) =>
        slot >= 0 && slot < _definitions.Count ? _definitions[slot] : null;

    /// <summary>按 id 取定义；不存在返回 <c>null</c>。</summary>
    public ActorDefinition? Find(string id)
    {
        int slot = SlotOf(id);
        return slot < 0 ? null : _definitions[slot];
    }

    /// <summary>清空（重开一局时调用；槽位号会重排，所以清空前必须先销毁所有实体）。</summary>
    public void Clear()
    {
        _definitions.Clear();
        _byId.Clear();
    }
}

/// <summary>
/// 技能包仓库：把 <see cref="SkillLoadout"/>（托管对象）编址成 int，供组件引用（J2 的 D1 约束）。
/// <para>
/// 与 <see cref="ActorDefinitionRegistry"/> 同一套路数：热路径只做数组下标，
/// 装配系统在生成实体时按定义里的 <c>SkillLoadoutId</c> 解析出槽位号写进 <see cref="SkillState"/>。
/// </para>
/// </summary>
public sealed class SkillLoadoutRegistry
{
    /// <summary>"未注册"哨兵。</summary>
    public const int NoneSlot = -1;

    private readonly List<SkillLoadout> _loadouts = new List<SkillLoadout>();
    private readonly Dictionary<string, int> _byId = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>已注册技能包数。</summary>
    public int Count => _loadouts.Count;

    /// <summary>注册（同 id 重复抛异常）。</summary>
    public int Register(SkillLoadout loadout)
    {
        if (loadout is null)
        {
            throw new ArgumentNullException(nameof(loadout));
        }
        if (_byId.TryGetValue(loadout.Id, out int existing))
        {
            throw new ArgumentException(string.Concat("技能包 id 重复注册：'", loadout.Id,
                "'（已存在槽位 ", existing.ToString(), "）。"), nameof(loadout));
        }
        int slot = _loadouts.Count;
        _loadouts.Add(loadout);
        _byId.Add(loadout.Id, slot);
        return slot;
    }

    /// <summary>批量注册。</summary>
    public void RegisterAll(IEnumerable<SkillLoadout> loadouts)
    {
        if (loadouts is null)
        {
            throw new ArgumentNullException(nameof(loadouts));
        }
        foreach (SkillLoadout loadout in loadouts)
        {
            Register(loadout);
        }
    }

    /// <summary>按 id 查槽位号；不存在返回 <see cref="NoneSlot"/>。</summary>
    public int SlotOf(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return NoneSlot;
        }
        return _byId.TryGetValue(id, out int slot) ? slot : NoneSlot;
    }

    /// <summary>按槽位号取技能包；越界返回 <c>null</c>。</summary>
    public SkillLoadout? Get(int slot) => slot >= 0 && slot < _loadouts.Count ? _loadouts[slot] : null;

    /// <summary>按 id 直接取技能包；不存在返回 <c>null</c>（与 <see cref="ActorDefinitionRegistry.Find"/> 对称）。</summary>
    public SkillLoadout? Find(string id) => Get(SlotOf(id));

    /// <summary>清空。</summary>
    public void Clear()
    {
        _loadouts.Clear();
        _byId.Clear();
    }
}
