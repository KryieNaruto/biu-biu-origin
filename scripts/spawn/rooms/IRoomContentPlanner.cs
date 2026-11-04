namespace Game.Spawn.Rooms;

/// <summary>
/// 房间**内容规划器**：把「房间里该有什么」（敌人编队、道具、Boss、玩家出生）变成
/// <see cref="ContentSlot"/> 列表。**不刷任何东西、不碰 ECS**。
/// <para>
/// 与 <see cref="IDungeonPlanner"/> 分开的理由（规则7）：地牢拓扑（图论）与房间内容
/// （题材/节奏）的变更频率与负责人完全不同。合在一起的话，改一次怪物编队就要重跑整张图的测试。
/// 分开后：新题材 = 换一个实现；同一个实现换一组权重 = 换数据。
/// </para>
/// </summary>
public interface IRoomContentPlanner
{
    /// <summary>规划器名。</summary>
    string Name { get; }

    /// <summary>
    /// 为一层地牢规划全部房间的内容槽。
    /// </summary>
    /// <param name="layout">已生成的地牢图（只读）。</param>
    /// <param name="seed">本层种子（内部必须 Fork 子流，禁止共享随机源）。</param>
    /// <param name="capacity">输出缓冲容量（槽总数上限；超出时应抛异常而不是静默截断）。</param>
    /// <returns>槽位列表（由调用方预分配缓冲，本方法不分配）。</returns>
    ContentSlot[] Plan(DungeonLayout layout, int seed, int capacity);
}

/// <summary>
/// 房间**内容生成器**：把槽位变成真正的游戏对象。这是唯一允许「写 ECS 命令缓冲」的一层。
/// <para>
/// <b>为什么做成接口而不是一个 switch</b>：房间类别（战斗/商店/隐藏）只是「默认组合」，
/// 将来会有「商店+隐藏陷阱」「Boss 前置机关」这类混搭。接口让每个种类独立演进，
/// 而调度方只按 <see cref="CanHandle"/> 问一句「你能接吗」。
/// </para>
/// </summary>
public interface IRoomContentSpawner
{
    /// <summary>能否处理这类房间。</summary>
    /// <param name="room">房间规划（只读）。</param>
    bool CanHandle(in RoomPlan room);

    /// <summary>
    /// 为房间生成内容。实现方必须通过 <see cref="IRoomBuildContext"/> 写生成命令（J5），
    /// 不得直接改 <c>World</c> 结构、不得创建 Godot 节点（宿主负责表现）。
    /// </summary>
    /// <param name="room">房间规划。</param>
    /// <param name="slots">属于该房间的槽位（只读，按 <see cref="ContentSlot.Kind"/> 过滤）。</param>
    /// <param name="context">生成上下文（世界命令缓冲、随机源、布局换算）。</param>
    void Spawn(in RoomPlan room, System.ReadOnlySpan<ContentSlot> slots, IRoomBuildContext context);
}
