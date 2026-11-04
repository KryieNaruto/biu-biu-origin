namespace Game.Core;

/// <summary>
/// 角色「类型」的**稳定编码**（角色抽象的第 1 块：类型属于数据，不属于类）。
/// <para>
/// <b>为什么角色/怪物/召唤物共用一份枚举，而不是 <c>Player : Character</c> / <c>Monster : Character</c></b>：
/// 规则6（数值封装成包、可互相赋值）要求「玩家能变成怪、怪能被召唤、Boss 能被夺舍」，一旦用继承表达
/// 「我是谁」，<c>is/as</c> 会渗进战斗层。类型在这里只是<u>一个标签值</u>：它决定
/// ① 出生时套哪个数值包（<c>CharacterDef</c> 的 <c>ActorKind</c> 列）、② 交给哪套系统跑（玩家控制 / AI）。
/// 阵营由 <see cref="TeamId"/> 独立表达 —— 这正是「怪物和角色同源、靠阵营区分」的落点：
/// <c>Kind = Monster, Team = Enemy</c> 与 <c>Kind = Monster, Team = Player</c>（夺舍成功）是同一份数据形状。
/// </para>
/// <para>
/// <b>纪律</b>：显式给值、只加不改、禁止重排（同 <see cref="TeamId"/>）。新成员只能从 16 往上追加。
/// </para>
/// </summary>
public enum ActorKind : int
{
    /// <summary>未知/未指定。默认值是「安全值」：生成的实体不会被误判成玩家。</summary>
    None = 0,

    /// <summary>玩家角色。由玩家输入驱动（<see cref="ControlScheme.PlayerInput"/>）。</summary>
    Player = 1,

    /// <summary>普通怪物。由 AI 驱动。</summary>
    Monster = 2,

    /// <summary>精英怪（数值包与技能包不同，系统流程完全一致）。</summary>
    Elite = 3,

    /// <summary>Boss（多阶段、专属技能包）。</summary>
    Boss = 4,

    /// <summary>召唤物：归属于某个召唤者的友军单位。</summary>
    Summon = 5,

    /// <summary>可破坏物：有血量与受击盒，但没有技能与 AI。</summary>
    Destructible = 6,

    // 预留 7..15：后续扩展（NPC / 陷阱 / 载具 / 分身…）。新成员从 16 起追加。
}

/// <summary>
/// 控制来源：实体「这一帧听谁的」。与 <see cref="ActorKind"/> 正交 —— 怪物被夺舍后
/// <c>Kind</c> 仍是 <see cref="ActorKind.Monster"/>，但 <c>Control</c> 变成 <see cref="PlayerInput"/>。
/// <para>为什么单独一个枚举：若把「谁控制」塞进 Kind，就有了 <c>MonsterControlledByPlayer</c> 这种组合爆炸值，
/// 而实际上它是两个独立维度的笛卡尔积。</para>
/// </summary>
public enum ControlScheme : byte
{
    /// <summary>无控制（可破坏物、待机状态）。</summary>
    None = 0,

    /// <summary>玩家输入驱动（键鼠手柄）。</summary>
    PlayerInput = 1,

    /// <summary>AI 驱动（由 <c>IBehaviorSource</c> 提供意图）。</summary>
    Ai = 2,

    /// <summary>脚本驱动（过场、教学、机关）。</summary>
    Script = 3,
}
