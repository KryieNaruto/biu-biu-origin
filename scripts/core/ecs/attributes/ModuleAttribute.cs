using System;
using System.Collections.Generic;

namespace Game.Core.Ecs;

/// <summary>
/// 「这个系统属于哪些**模块**」——装配式 ECS（「拼接」）的标记特性（契约 docs/design/character-hud-contract.md §4）。
/// <para>
/// <b>为什么需要模块，而不是让宿主自己记住「我装过些什么」</b>
/// </para>
/// <list type="bullet">
/// <item>「一个变体 = 一组合计（镜头包 / 手柄包 / 移动包 / 瞄准包 / 射击包 / HUD 包）」这件事必须能被
///       <u>自描述</u>：系统自己在类型上声明「我属于 move_standard」，数据侧的装配清单只写模块 id 字符串；
///       两边不需要维护第二份「模块 → 系统类型」的表（那张表一定会漂移）。</item>
/// <item>宿主装配时可以先 <see cref="SystemPipeline.Add"/> 全部候选系统、再按变体勾选模块，
///       于是「换一个变体」= 换一个模块 id 列表，0 行代码（规则7）。</item>
/// <item>模块是<u>装配期</u>概念：它决定一帧里跑哪些系统；运行期不动态拔插（见 <see cref="SystemPipeline.Remove"/>）。</item>
/// </list>
/// <para>
/// <b>为什么允许多个模块 id</b>：同一个系统天然可被多个变体复用
/// （例如「标准移动」既被标准型也用、也被重甲型用）：<c>[Module("move_standard", "move_tank")]</c>
/// 让一个实现同时满足两组装配，避免为每个变体复制一份系统。
/// </para>
/// <para>
/// <b>为什么 id 是字符串而不是枚举</b>：模块集合会随内容（Excel / 装配清单）增长，写在代码里的枚举
/// 意味着「加一个新模块 = 改核心枚举 = 改内核」，违反规则7；字符串让数据侧直接引用，代码侧零改动。
/// 代价是拼错只能在装配期发现 —— 所以 <see cref="SystemPipeline.AddModule"/> 对「无系统认领的模块 id」
/// 会当场抛异常，把拼写错误变成开局即崩，而不是「某个系统悄悄没装」。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ModuleAttribute : Attribute
{
    private readonly string[] _moduleIds;

    /// <summary>标记系统属于哪些模块（至少一个 id；空 id 会被立刻拒绝）。</summary>
    public ModuleAttribute(params string[] moduleIds)
    {
        if (moduleIds is null || moduleIds.Length == 0)
        {
            throw new GameEcsException("[Module] 至少需要一个模块 id（否则这个系统无法被任何装配清单勾选）。");
        }
        _moduleIds = new string[moduleIds.Length];
        for (int i = 0; i < moduleIds.Length; i++)
        {
            string id = moduleIds[i];
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new GameEcsException("[Module] 的模块 id 不能为空串：空 id 会让「未声明模块」与「声明了空模块」无法区分。");
            }
            _moduleIds[i] = id;
        }
    }

    /// <summary>本系统声明的模块 id（按声明顺序、只读）。</summary>
    public IReadOnlyList<string> ModuleIds => _moduleIds;
}
