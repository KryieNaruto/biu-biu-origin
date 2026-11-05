using System;
using Game.Core.Ecs;

namespace BiuBiuOrigin.Tests.Characters
{
    /// <summary>
    /// 把命令缓冲刷进世界的一小节拍（等价于宿主每帧末尾的 <c>Commands.Flush()</c>）。
    /// <para>
    /// 为什么不用 <c>Game.Host.CommandFlushSystem</c>：测试工程刻意<u>不</u>引用 <c>scripts/host/**</c>
    /// （那是唯一允许 <c>using Godot</c> 的目录，引进来整套测试就不能在无引擎进程里跑了 —— ECS.md §10）。
    /// 这里只需要"刷一次缓冲"这一个动作，自己写两行比破坏引擎无关性划算。
    /// </para>
    /// </summary>
    internal sealed class FlushSystem : SystemBase
    {
        protected override void OnExecute(float deltaTime) => World.Commands.Flush();
    }
}
