using Game.Core;
using Game.Core.Ecs;
using Game.Core.Skills;

namespace Game.Actor;

// 角色系统族的组件（ECS.md J2：纯 unmanaged 数据，一个 [Identity] 字段，无方法、无数值）。
// 为什么角色相关组件放在 scripts/actor/ 而不是官方组件文件里：官方组件是「任何系统族都可能用到」的
// 基底（Transform/Health/Team…），而「谁在控制这个实体」「它背着哪套技能」是角色域的词汇。
// 规则7：新增一种角色不需要改这里，只需要多一行 Excel。

/// <summary>
/// 角色身份：类型 + 阵营 + 控制方案 + 数值包槽位。
/// <para>
/// <b>为什么把 Kind 与 Control 都放进组件</b>：AI 系统用 <c>Query.Without&lt;TakeoverState&gt;</c> 排除夺舍单位，
/// 而「谁在控制」需要一帧可读的判定；把它们做成组件值后，查询本身就能表达筛选（J3：
/// 不写 <c>if (kind == Boss)</c>，而是查询「有 Control=Ai 的实体」）。
/// </para>
/// <para>
/// <b>为什么数值包 id 用 int 槽位号 + 仓库查表</b>：组件不能持有托管引用（J2），
/// 字符串 id 会破坏 unmanaged；由<b>装配系统</b>在生成时把 id 解析成仓库槽位号写进这里。
/// </para>
/// </summary>
public struct ActorIdentity
{
    /// <summary>实体自引用（内核自动写入）。</summary>
    [Identity] public Game.Core.Ecs.Entity Owner;

    /// <summary>角色类型。</summary>
    public ActorKind Kind;

    /// <summary>当前阵营（夺舍时被改写；初始值来自定义）。</summary>
    public TeamId Team;

    /// <summary>当前控制方案。</summary>
    public ControlScheme Control;

    /// <summary>角色定义在仓库中的槽位号（&lt; 0 表示未装配，装配系统会跳过并计数）。</summary>
    public int DefinitionSlot;

    /// <summary>数值包基础层槽位号（=-1 表示缺 Base，属于数据错误）。</summary>
    public int BasePackageSlot;

    /// <summary>成长层槽位号（-1 = 无）。</summary>
    public int GrowthPackageSlot;

    /// <summary>装备层槽位号（-1 = 无）。</summary>
    public int EquipmentPackageSlot;

    /// <summary>Buff 层槽位号（-1 = 无）。</summary>
    public int BuffPackageSlot;

    /// <summary>临时层槽位号（-1 = 无）。</summary>
    public int TemporaryPackageSlot;
}

/// <summary>
/// 法力：当前值 / 上限（与官方 <c>Health</c> 同构；为什么不用一个通用 <c>Resource{ Hp, Mp }</c>：
/// 生命已被官方 <c>Health</c> 占用并被伤害系统唯一写入，再合并会让两套写入点互相打架）。
/// </summary>
public struct Mana
{
    /// <summary>实体自引用。</summary>
    [Identity] public Game.Core.Ecs.Entity Owner;

    /// <summary>当前法力。</summary>
    public float Current;

    /// <summary>法力上限（来自数值包 Hp 对应的 ManaMax）。</summary>
    public float Max;
}

/// <summary>
/// 角色的技能冷热数据：技能包槽位号 + 当前控制来源。
/// <para>
/// 技能包本体（<see cref="SkillLoadout"/>）是托管对象，不能放进组件；
/// 这里只存它在宿主侧 <c>SkillLoadoutRegistry</c> 里的槽位号（J2 的 D1 约束）。
/// </para>
/// </summary>
public struct SkillState
{
    /// <summary>实体自引用。</summary>
    [Identity] public Game.Core.Ecs.Entity Owner;

    /// <summary>技能包槽位号（&gt;= 0；-1 表示尚未装配）。</summary>
    public int LoadoutSlot;

    /// <summary>最近一次施放结果（调试/HUD 用；0 = 成功）。</summary>
    public SkillCastFailure LastFailure;

    /// <summary>本帧想施放的槽位（由意图系统写入，技能系统消费后清空）。</summary>
    public SkillSlot RequestedSlot;

    /// <summary>本帧请求是否应以"保持"语义施放（冲刺需要按住，射击需要持续）。</summary>
    public bool RequestedHold;

    /// <summary>本帧是否已成功施放过（防止同一帧多次触发）。</summary>
    public bool CastThisFrame;
}

/// <summary>
/// 输入意图组件（J1：组件只存值）。瞄准方向用 3 个 float 而不是 <see cref="System.Numerics.Vector3"/>
/// —— 两者布局相同，但显式字段让「组件不能有引用类型」这条在阅读时一目了然。
/// </summary>
public struct ActorIntent
{
    /// <summary>实体自引用。</summary>
    [Identity] public Game.Core.Ecs.Entity Owner;

    /// <summary>本帧按下掩码。</summary>
    public uint PressedMask;

    /// <summary>保持掩码。</summary>
    public uint HeldMask;

    /// <summary>瞄准方向 X。</summary>
    public float AimX;

    /// <summary>瞄准方向 Y。</summary>
    public float AimY;

    /// <summary>瞄准方向 Z。</summary>
    public float AimZ;

    /// <summary>意图序列号：每采集一帧 +1（空意图帧也能被下游识别）。</summary>
    public int Sequence;
}
