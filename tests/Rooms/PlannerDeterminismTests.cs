using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Game.Core;
using Game.Core.Randomness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Rooms
{
    // ==================================================================
    // 房间/地牢规划 —— 能在 tests/ 里跑的**地基**那一半
    // ==================================================================
    //
    // 约束（Lead 判定，写在 tests/BiuBiuOrigin.Tests.csproj 里）：
    //   测试程序集只编译 scripts/core/**，用来证明 Game.Core 零引擎依赖。
    //   scripts/spawn/rooms/ 不进测试程序集 → Ro omDirection / RoomId / DungeonLayout
    //   这些类型在测试里**根本不可见**（不是"懒得写"，是编译不过）。
    //
    // 所以本文件只验证「规划器可复现性所依赖的地基」，且全部只依赖 scripts/core/**：
    //   1. IRandomProvider.Fork 的**稳定子流**语义 —— 这是「同 seed 同图」唯一的地基。
    //      规划器的每一层盐（planner / grow / classify / hidden）都靠它，任何一条被破坏，
    //      地牢就会随「别的系统多掷了一次骰」而改变形状。
    //   2. ForkFromCurrent 与 Fork 的**语义必须可区分** —— 两者同名会让调用方无法判断可复现性。
    //   3. NextInt 左闭右开、NextFloat ∈ [0,1) —— 槽位落点/房间生长方向全靠这两条边界，
    //      一旦 NextFloat 能返回 1.0，锚点归一化坐标就会越出 [0,1]。
    //   4. 布局指纹工具：把几何特征串稳定化 + 哈希，供规划器侧回归使用（顺序无关、跨机稳定）。
    // ==================================================================

    /// <summary>随机子流契约（规划器确定性的地基）。</summary>
    [TestClass]
    public sealed class PlannerRandomDeterminismTests
    {
        private const int PlannerSalt = 0x7A02;

        /// <summary>
        /// 同 salt 的 Fork 必须与父流推进无关：先 Fork 与「推进 N 次后 Fork」得到逐元素相同的序列。
        /// <para>这是「多加一次随机判定不会改变地牢形状」的直接保证。</para>
        /// </summary>
        [TestMethod]
        public void Fork_SameSalt_IsIndependentOfParentAdvancement()
        {
            DeterministicRandom parent = new DeterministicRandom(2026);
            IRandomProvider early = parent.Fork(PlannerSalt);

            for (int i = 0; i < 17; i++)
            {
                parent.NextInt(0, 1000);
            }
            IRandomProvider late = parent.Fork(PlannerSalt);

            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(early.NextInt(0, int.MaxValue), late.NextInt(0, int.MaxValue),
                    "同名子流第 " + i.ToString(CultureInfo.InvariantCulture) + " 个值必须相同（Fork 以根状态为基准）。");
            }
        }

        /// <summary>同一 seed 重建随机源，同 salt 子流必须完全一致（跨进程可复现）。</summary>
        [TestMethod]
        public void Fork_IsReproducibleAcrossInstances()
        {
            IRandomProvider a = new DeterministicRandom(4242).Fork(PlannerSalt);
            IRandomProvider b = new DeterministicRandom(4242).Fork(PlannerSalt);

            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(a.NextInt(0, int.MaxValue), b.NextInt(0, int.MaxValue),
                    "同 seed 同 salt 的序列必须在两次构造之间逐元素相同。");
            }
        }

        /// <summary>不同 salt 必须给出不同序列——否则规划器各层盐形同虚设，会长出相关的房间。</summary>
        [TestMethod]
        public void Fork_DifferentSalts_Diverge()
        {
            IRandomProvider planner = new DeterministicRandom(9001).Fork(0x7A02);
            IRandomProvider grow = new DeterministicRandom(9001).Fork(0x5101);

            int same = 0;
            for (int i = 0; i < 256; i++)
            {
                if (planner.NextInt(0, int.MaxValue) == grow.NextInt(0, int.MaxValue))
                {
                    same++;
                }
            }
            Assert.AreEqual(0, same, "不同 salt 的子流不应出现相同取值。");
        }

        /// <summary>
        /// Fork 与 ForkFromCurrent 必须能区分：前者与父流进度无关，后者**必须**随之变化。
        /// <para>规划器只用 Fork；这条测试是为了防止有人把实现改成「当前点派生」而不改注释。</para>
        /// </summary>
        [TestMethod]
        public void ForkFromCurrent_TracksParentState_UnlikeFork()
        {
            DeterministicRandom parent = new DeterministicRandom(31337);
            IRandomProvider stableBefore = parent.Fork(PlannerSalt);

            for (int i = 0; i < 5; i++)
            {
                parent.NextInt(0, 100);
            }

            IRandomProvider stableAfter = parent.Fork(PlannerSalt);
            IRandomProvider movingAfter = parent.ForkFromCurrent(PlannerSalt);

            Assert.AreEqual(stableBefore.NextInt(0, int.MaxValue), stableAfter.NextInt(0, int.MaxValue),
                "Fork 在父流推进后必须仍给出同一条子流。");
            Assert.AreNotEqual(stableAfter.NextInt(0, int.MaxValue), movingAfter.NextInt(0, int.MaxValue),
                "ForkFromCurrent 在父流推进后必须给出不同的子流（否则两种语义无法区分）。");
        }

        /// <summary>不同 seed 的根流起步就不同（「不同 seed 不同图」的前提）。</summary>
        [TestMethod]
        public void DifferentSeeds_ProduceDifferentStreams()
        {
            DeterministicRandom a = new DeterministicRandom(11);
            DeterministicRandom b = new DeterministicRandom(12);

            int same = 0;
            for (int i = 0; i < 256; i++)
            {
                if (a.NextInt(0, int.MaxValue) == b.NextInt(0, int.MaxValue))
                {
                    same++;
                }
            }
            Assert.AreEqual(0, same, "两个不同 seed 的前 256 个取值不应出现相同值。");
        }

        /// <summary>NextInt 左闭右开：永远取不到上界，且两端都能取到。</summary>
        [TestMethod]
        public void NextInt_IsHalfOpenRange()
        {
            DeterministicRandom r = new DeterministicRandom(7);
            int sawMin = 0;
            int sawMax = 0;

            for (int i = 0; i < 20000; i++)
            {
                int v = r.NextInt(-3, 4);
                Assert.IsTrue(v >= -3 && v < 4, "NextInt(-3,4) 越界：" + v.ToString(CultureInfo.InvariantCulture));
                sawMin += v == -3 ? 1 : 0;
                sawMax += v == 3 ? 1 : 0;
            }

            Assert.IsTrue(sawMin > 0, "区间下界必须可取到（左闭）。");
            Assert.IsTrue(sawMax > 0, "区间上界内侧必须可取到（右开但 -1 可达）。");
        }

        /// <summary>
        /// NextFloat 严格落在 [0,1)：锚点归一化坐标 <c>LocalX/Z</c> 的合法性直接依赖这条。
        /// </summary>
        [TestMethod]
        public void NextFloat_StaysInUnitInterval()
        {
            DeterministicRandom r = new DeterministicRandom(555);
            for (int i = 0; i < 20000; i++)
            {
                float f = r.NextFloat();
                Assert.IsTrue(f >= 0f && f < 1f,
                    "NextFloat 必须落在 [0,1)：实测 " + f.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// NextInt 的退化区间契约：空区间返回下界（不抛），真非法区间（maxExc &lt; minInc）抛
        /// <see cref="GameCoreException"/>。
        /// <para>
        /// 实测订正：实现里 <c>maxExc == minInc</c> 是**合法**的「只有一个值」区间，直接返回 minInc；
        /// 只有 <c>maxExc &lt; minInc</c> 才抛。容量计算出空集合时依赖这个退化行为，不能改成抛异常。
        /// </para>
        /// </summary>
        [TestMethod]
        public void NextInt_DegenerateRange_IsContractual()
        {
            DeterministicRandom r = new DeterministicRandom(1);

            Assert.AreEqual(5, r.NextInt(5, 5), "空区间 [5,5) 是合法的单值区间，必须返回 5 而不是抛异常。");
            Assert.ThrowsExactly<GameCoreException>(() => r.NextInt(9, 2), "真非法区间必须抛 GameCoreException。");
        }
    }

    /// <summary>
    /// 地牢布局指纹：把「几何可观察特征」序列化成稳定字符串再取哈希。
    /// <para>
    /// 测试程序集看不到 <c>DungeonLayout</c>，所以规划器侧负责把特征摊成字符串行
    /// （房间矩形/类别/深度/链路端点/门），本工具只做「排序 + 哈希」这一步，
    /// 保证同一张图在任何机器、任何线程、任何哈希种子下都得到同一个指纹。
    /// </para>
    /// </summary>
    public static class LayoutFingerprint
    {
        /// <summary>对一组特征行取 SHA-256 十六进制指纹（输入顺序无关）。</summary>
        /// <param name="lines">特征行；顺序不影响结果。</param>
        public static string Hash(IEnumerable<string> lines)
        {
            if (lines is null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            List<string> sorted = new List<string>(lines);
            sorted.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                sb.Append(sorted[i]).Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        /// <summary>一条房间特征行。</summary>
        /// <param name="slot">房间槽位。</param>
        /// <param name="x">房间左上角 X（格）。</param>
        /// <param name="z">房间左上角 Z（格）。</param>
        /// <param name="side">房间边长（格）。</param>
        /// <param name="category">类别数值。</param>
        /// <param name="depth">距起点的深度。</param>
        /// <param name="onCriticalPath">是否在关键路径上。</param>
        public static string Room(int slot, int x, int z, int side, int category, int depth, bool onCriticalPath)
            => string.Concat(
                "room|",
                slot.ToString(CultureInfo.InvariantCulture), "|",
                x.ToString(CultureInfo.InvariantCulture), "|",
                z.ToString(CultureInfo.InvariantCulture), "|",
                side.ToString(CultureInfo.InvariantCulture), "|",
                category.ToString(CultureInfo.InvariantCulture), "|",
                depth.ToString(CultureInfo.InvariantCulture), "|",
                onCriticalPath ? "1" : "0");

        /// <summary>一条链路特征行。</summary>
        /// <param name="a">A 房间槽位。</param>
        /// <param name="b">B 房间槽位。</param>
        /// <param name="direction">A→B 方向数值。</param>
        /// <param name="onMain">是否属于主图（相对秘密通道）。</param>
        /// <param name="onCriticalPath">是否在关键路径上。</param>
        /// <param name="isSecret">是否秘密通道。</param>
        public static string Link(int a, int b, int direction, bool onMain, bool onCriticalPath, bool isSecret)
            => string.Concat(
                "link|",
                a.ToString(CultureInfo.InvariantCulture), "|",
                b.ToString(CultureInfo.InvariantCulture), "|",
                direction.ToString(CultureInfo.InvariantCulture), "|",
                onMain ? "1" : "0", "|",
                onCriticalPath ? "1" : "0", "|",
                isSecret ? "1" : "0");
    }

    /// <summary>布局指纹工具自身的测试。</summary>
    [TestClass]
    public sealed class LayoutFingerprintTests
    {
        /// <summary>顺序无关：同一组特征行换顺序必须得到同一指纹。</summary>
        [TestMethod]
        public void Hash_IsOrderIndependent()
        {
            string[] forward =
            {
                LayoutFingerprint.Room(0, 0, 0, 5, 1, 0, false),
                LayoutFingerprint.Link(0, 1, 0, true, true, false),
            };
            string[] backward = { forward[1], forward[0] };

            Assert.AreEqual(LayoutFingerprint.Hash(forward), LayoutFingerprint.Hash(backward),
                "特征行换序不应改变指纹（否则布局比较会假阴性）。");
        }

        /// <summary>内容不同 → 指纹必须不同，否则「不同 seed 不同图」的回归会假绿。</summary>
        [TestMethod]
        public void Hash_DistinguishesContent()
        {
            string a = LayoutFingerprint.Hash(new[] { LayoutFingerprint.Room(0, 0, 0, 5, 1, 0, false) });
            string b = LayoutFingerprint.Hash(new[] { LayoutFingerprint.Room(0, 0, 0, 5, 2, 0, false) });
            string c = LayoutFingerprint.Hash(new[] { LayoutFingerprint.Room(1, 0, 0, 5, 1, 0, false) });

            Assert.AreNotEqual(a, b, "类别不同必须得到不同指纹。");
            Assert.AreNotEqual(a, c, "槽位不同必须得到不同指纹。");
        }

        /// <summary>同样输入永远得到同样哈希（跨调用稳定）。</summary>
        [TestMethod]
        public void Hash_IsStable()
        {
            string[] lines =
            {
                LayoutFingerprint.Link(0, 1, 0, true, true, false),
                LayoutFingerprint.Link(1, 2, 3, false, false, true),
            };

            Assert.AreEqual(LayoutFingerprint.Hash(lines), LayoutFingerprint.Hash(lines), "相同输入必须得到相同指纹。");
        }

        /// <summary>空布局也要有确定指纹：不抛异常、固定 64 位十六进制。</summary>
        [TestMethod]
        public void Hash_HandlesEmptyLayout()
        {
            string hex = LayoutFingerprint.Hash(Array.Empty<string>());

            Assert.AreEqual(64, hex.Length, "SHA-256 十六进制应为 64 个字符。");
            Assert.AreEqual(hex, LayoutFingerprint.Hash(Array.Empty<string>()), "空布局指纹必须稳定。");
        }

        /// <summary>空参数必须抛 <see cref="ArgumentNullException"/>（不能静默返回空哈希）。</summary>
        [TestMethod]
        public void Hash_RejectsNull()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => LayoutFingerprint.Hash(null!));
        }
    }
}
