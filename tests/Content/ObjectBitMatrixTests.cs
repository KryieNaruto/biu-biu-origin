using System;
using Game.Core.Content;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Content
{
    /// <summary>
    /// 位矩阵与 tag 列头的用例组。
    /// <para>
    /// 为什么值得单独钉住：位矩阵是「属性 × 标签」组合的唯一载体，它一旦错位一位，
    /// 表现就是「某些武器加成一会让伤害翻倍、某些组合莫名不生效」—— 这种错位不会崩溃，
    /// 只会让策划怀疑自己填的表。<c>tag 数不是 32 整数倍</c>时最容易错位，所以必须有边界用例。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class ObjectBitMatrixTests
    {
        [TestMethod]
        public void Mask_ReturnsTheWholeWordOfThatTag()
        {
            // 小端位序：tag t ↔ word t>>5，bit t&31。
            // 2 行 × 3 tag → 每行 1 个 word，共 2 个 int。行 0 = 0b101（tag0 + tag2），行 1 = 0。
            var matrix = new ObjectBitMatrix(rowCount: 2, tagCount: 3, packedBits: new[] { 0b101, 0 });

            Assert.AreEqual(1, matrix.WordCount, "3 个 tag 只需要 1 个 word");
            Assert.AreEqual(0b101, matrix.Mask(0, 0), "tag0 所在 word 的整段掩码");
            Assert.AreEqual(0b101, matrix.Mask(0, 2), "同一个 word 内的 tag2 返回同一段掩码（规则用掩码求交）");
            Assert.AreEqual(0, matrix.Mask(1, 0), "行 1 是另一个 word");

            Assert.IsTrue(matrix.Has(0, 0), "tag0 勾了");
            Assert.IsFalse(matrix.Has(0, 1), "tag1 没勾");
            Assert.IsTrue(matrix.Has(0, 2), "tag2 勾了");
            Assert.IsFalse(matrix.Has(1, 0));
        }

        [TestMethod]
        public void Mask_HandlesTagCountNotMultipleOf32()
        {
            // 边界：tag 数 33（= 32 + 1）→ 2 word/行，最后一行只用到 1 个 bit。
            var packed = new int[2 * 2];
            var matrix = new ObjectBitMatrix(rowCount: 2, tagCount: 33, packedBits: packed);

            Assert.AreEqual(2, matrix.WordCount, "ceil(33 / 32) = 2");

            matrix = new ObjectBitMatrix(rowCount: 2, tagCount: 33);
            matrix.Set(0, 0);   // 第一个 word 的 bit0
            matrix.Set(0, 32);  // 第二个 word 的 bit0
            matrix.Set(1, 31);  // 第二个 word 的 bit31（第一个 word 的最后一位）
            ObjectBitMatrix frozen = matrix.Build();

            Assert.AreEqual(0b1, frozen.Mask(0, 0), "tag0 → word0");
            Assert.AreEqual(0b1, frozen.Mask(0, 32), "tag32 → word1（不是 word0）");
            Assert.AreEqual(1 << 31, frozen.Mask(1, 31), "tag31 → word0 的最高位");

            Assert.IsTrue(frozen.Has(0, 32));
            Assert.IsTrue(frozen.Has(1, 31));
            Assert.IsFalse(frozen.Has(0, 1));

            // 越界（tag 33 = 超出最后一个 tag；行 2 = 越界）必须安全返回，而不是读别人的内存。
            Assert.AreEqual(0, frozen.Mask(0, 33), "tag 越界 → 0");
            Assert.IsFalse(frozen.Has(0, 33));
            Assert.AreEqual(0, frozen.Mask(2, 0), "行越界 → 0");
            Assert.AreEqual(0, frozen.Mask(-1, 0));
        }

        [TestMethod]
        public void Constructor_RejectsShapeMismatch()
        {
            // 行数 × word 数 与打包位数不一致 = 表与列头漂移，必须当场炸（否则读到别行的位）。
            bool threw = false;
            try
            {
                _ = new ObjectBitMatrix(rowCount: 3, tagCount: 33, packedBits: new int[4]);
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "形状不匹配必须抛 ArgumentException");
        }

        [TestMethod]
        public void Build_FreezesTheMatrix()
        {
            // 运行期改矩阵 = 绕开 Excel 真源改玩法数据，必须禁止。
            var building = new ObjectBitMatrix(1, 2);
            building.Set(0, 1);
            ObjectBitMatrix frozen = building.Build();

            Assert.IsTrue(frozen.Has(0, 1), "冻结前勾的位要带过去");

            bool threw = false;
            try
            {
                frozen.Set(0, 0);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "冻结后 Set 必须抛 InvalidOperationException");

            // 冻结用的是拷贝：继续改构建器不得影响已冻结的矩阵。
            building.Set(0, 0);
            Assert.IsFalse(frozen.Has(0, 0), "冻结构建器与矩阵之间必须是拷贝语义");
        }

        [TestMethod]
        public void CopyRowTo_CopiesOneRowForTheBatchSolver()
        {
            var matrix = new ObjectBitMatrix(2, 33, new[] { 0b1, 0b10, 0b100, 0b1000 });
            var buffer = new int[2];
            matrix.CopyRowTo(1, buffer);

            Assert.AreEqual(0b100, buffer[0], "行 1 的第一段");
            Assert.AreEqual(0b1000, buffer[1], "行 1 的第二段");

            Assert.AreEqual(2, matrix.PopCount(0), "行 0 有 2 个勾选");
            Assert.AreEqual(2, matrix.PopCount(1), "行 1 有 2 个勾选");
        }

        [TestMethod]
        public void ContentRules_KeepsTagIndicesStableAndSafe()
        {
            // tag 下标 = 表列顺序，是存档与生成物引用的稳定 id：加一列只追加新 id，已有组合含义不变。
            ContentRules rules = ContentRules.FromTags(new[] { "fire", "ice", "chain", "bounce" });

            Assert.AreEqual(4, rules.TagCount);
            Assert.AreEqual(0, rules.TagOf("fire"));
            Assert.AreEqual(2, rules.TagOf("chain"));
            Assert.AreEqual("chain", rules.NameOf(2));
            Assert.AreEqual("bounce", rules.TagNames[3], "TagNames 按表顺序");

            // 越界安全：查错的 tag（表版本不一致 / 编辑器里手填）不该炸掉调试面板。
            Assert.AreEqual(-1, rules.TagOf("does_not_exist"));
            Assert.AreEqual(-1, rules.TagOf(string.Empty));
            Assert.AreEqual(-1, rules.TagOf(null!));
            Assert.AreEqual(string.Empty, rules.NameOf(-1));
            Assert.AreEqual(string.Empty, rules.NameOf(4));
            Assert.AreEqual(string.Empty, rules.NameOf(999));
        }

        [TestMethod]
        public void ContentRules_RejectsEmptyTagNames()
        {
            // 空列名会让 tag id 与列错位，属于表结构错误 —— 构建期就要炸。
            bool threw = false;
            try
            {
                ContentRules.FromTags(new[] { "fire", string.Empty });
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Assert.IsTrue(threw);
        }
    }
}
