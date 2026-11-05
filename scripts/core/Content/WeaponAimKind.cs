namespace Game.Core.Content;

/// <summary>
/// 武器的**瞄准规则**：这一次发射"朝哪儿"。
/// <para>
/// <b>为什么瞄准是一个数据列而不是技能里的代码</b>：同一把枪可以有「直射 / 追踪 / 环绕 / 屏幕边缘飞入」
/// 多种用法，肉鸽里还常有"词条改变瞄准方式"。如果瞄准写死在技能里，
/// 「把手枪改造成环绕射击」就需要一个新技能类；做成数据后，玩家只换表里一个枚举值。
/// </para>
/// <para>
/// 这里只有<b>语义标签</b>：具体把方向算成什么由瞄准系统按枚举实现一次，
/// 新增瞄准方式 = 追加一个成员 + 一处实现（规则7：新增一种 X 改 2 个文件）。
/// </para>
/// </summary>
public enum WeaponAimKind : byte
{
    /// <summary>沿发射者朝向（最朴素）。</summary>
    Forward = 0,

    /// <summary>朝瞄准点（鼠标 / 手柄摇杆指向的世界点）。</summary>
    AimPoint = 1,

    /// <summary>锁向当前锁定目标（有锁定则追踪，丢失则退回 Forward）。</summary>
    AimLock = 2,

    /// <summary>环绕发射者旋转分布（"环形弹幕"由 Count + 此模式组合而成）。</summary>
    Orbit = 3,

    /// <summary>随机散布（锥角由 SpreadDegrees 规则给）。</summary>
    RandomSpread = 4,

    /// <summary>按网格 / 阵型发射（多枚排成扇形或方阵）。</summary>
    Grid = 5,

    /// <summary>朝目标寻的（与 Motion.Homing 不同：这里是<b>发射时</b>的初速方向）。</summary>
    TargetSeek = 6,

    /// <summary>朝地面（落点 / 地雷 / 冲击波）。</summary>
    Ground = 7,

    /// <summary>从屏幕边缘射入（浪潮式弹幕）。</summary>
    ScreenEdge = 8,

    /// <summary>以自身为中心向外（自爆、全方位脉冲）。</summary>
    AroundSelf = 9,

    /// <summary>朝光标位置（与 AimPoint 的差别：不经过角色本体，用于远程操控类玩法）。</summary>
    Cursor = 10,
}
