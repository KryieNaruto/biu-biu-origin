using System;
using Game.Core;
using Game.Core.Handles;

namespace Game.Actor;

/// <summary>
/// 业务层的事件出口（窄接口）。系统只声明「发生了一件事」，不知道事件最后去哪。
/// <para>
/// <b>为什么要这个接口，而不是让系统直接 <c>Publish</c> 到 <c>EventBus</c></b>：
/// <list type="number">
///   <item><c>EventBus.Publish</c> 是**同步派发**。系统在 Update 里直接发，订阅方会在
///         「本帧命令还没提交」的时刻被唤醒，拿到的句柄随时可能失效；</item>
///   <item>ECS.md §6 把「派发」定在 <c>CommandFlush</c> 之后的独立阶段。于是必须有一个人
///         在帧内**收集**、在帧末**派发**——那是宿主层的时间图职责；</item>
///   <item>系统若直接依赖 <c>EventBus</c>，就等于依赖了「立即派发」这个时间语义，
///         将来换回放/网络预测时会全线崩。依赖一个「只收不发」的接口则完全不受影响。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么放在业务层而不是 <c>Game.Core</c></b>：它描述的是「业务事件语汇」（谁杀谁、谁到期），
/// 而 <c>Game.Core</c> 只提供通用容器（<c>EventBus</c>）。放在业务层可以随玩法演进而扩展，
/// 不必动内核。
/// </para>
/// <para>
/// <b>实现约束</b>：所有方法内部**不得**触发结构化变更（不能 <c>Create/Destroy/Set/Remove</c>），
/// 也不得抛异常——它是帧内热路径。宿主层用值类型队列实现，稳态零分配。
/// </para>
/// </summary>
public interface HostEventSink
{
    /// <summary>伤害已结算（在目标血量写入之后调用）。</summary>
    void RaiseDamageApplied(ActorHandle source, ActorHandle target, float amount, bool isCrit, DamageType type);

    /// <summary>几何命中（在伤害结算之前调用）。</summary>
    void RaiseHit(ActorHandle source, ActorHandle target, float x, float y, float z, DamageType type);

    /// <summary>实体被击杀（同一实体只发一次）。</summary>
    void RaiseActorKilled(ActorHandle victim, ActorHandle killer, DamageType type);

    /// <summary>生命周期到期（实体即将被销毁）。</summary>
    void RaiseLifetimeExpired(ActorHandle entity);

    /// <summary>实体已销毁。</summary>
    void RaiseEntityDestroyed(ActorHandle entity);
}
