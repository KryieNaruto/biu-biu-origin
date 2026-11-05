using System;
using System.Collections.Generic;
using System.IO;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;
using Game.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Characters
{
    /// <summary>
    /// **真表比对**：直接打开 <c>data/excel/characters.xlsx</c>，与运行期内置副本逐项对比。
    /// <para>
    /// <b>为什么必须有这组用例</b>：在这组用例存在之前，"Excel 是唯一真源"这句话是**没有自动证据**的 ——
    /// 原有的 <see cref="CharacterContentRegressionTests"/> 只比对 C# 常量与 C# 常量，
    /// 策划改了一行表、忘了同步代码，测试照样全绿，游戏照样不生效。这是最难排查的一类事故，
    /// 因此必须有一把"直接量真实 xlsx"的尺子。
    /// </para>
    /// <para>
    /// <b>它与生成物的关系</b>：等 <c>data/generated/</c> 真正落地后，这组用例应从"比对内置副本"
    /// 改为"比对生成物"，断言形状不变 —— 它守护的契约（表 = 运行时）与数据来自哪一层无关。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class CharacterXlsxParityTests
    {
        private static CharacterContentSet? _fromXlsx;
        private static CharacterContentSet? _builtin;

        private static CharacterContentSet FromXlsx =>
            _fromXlsx ??= CharacterXlsxBinder.Build(FindCharactersXlsx());

        private static CharacterContentSet Builtin =>
            _builtin ??= new CharacterContentSet().BuildBuiltinV1();

        /// <summary>从测试输出目录往上找工程根，再定位 data/excel/characters.xlsx。</summary>
        private static string FindCharactersXlsx()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "BiuBiuOrigin.csproj")))
                {
                    string path = Path.Combine(dir.FullName, "data", "excel", "characters.xlsx");
                    Assert.IsTrue(File.Exists(path),
                        "找不到 characters.xlsx：" + path + "。它必须在工程根的 data/excel/ 下（Excel 是唯一真源）。");
                    return path;
                }
                dir = dir.Parent;
            }
            Assert.Fail("向上找不到 BiuBiuOrigin.csproj，无法定位工程根目录。");
            return string.Empty;
        }

        /// <summary>真表能被读取并解析成完整的运行时内容（三张表都非空）。</summary>
        [TestMethod]
        public void Xlsx_ProducesCompleteRuntimeContent()
        {
            CharacterContentSet set = FromXlsx;

            Assert.IsTrue(set.Definitions.Count > 0, "Characters 表应至少有一个角色定义");
            Assert.IsTrue(set.StatPacks.Count > 0, "StatPacks 表应至少有一个数值包");
            Assert.IsTrue(set.Loadouts.Count > 0, "SkillLoads 表应至少有一个技能包");
        }

        /// <summary>表里的每个角色，真表读出来的与内置副本读出来的**字段逐一相同**。</summary>
        [TestMethod]
        public void EveryCharacter_FromXlsx_MatchesBuiltinFieldByField()
        {
            int checkedCount = 0;
            for (int i = 0; i < FromXlsx.Definitions.Count; i++)
            {
                ActorDefinition? fromTable = FromXlsx.Definitions.Get(i);
                Assert.IsNotNull(fromTable);

                ActorDefinition? fromCode = Builtin.Definitions.Find(fromTable.Id);
                Assert.IsNotNull(fromCode,
                    string.Concat("characters.xlsx 里的角色「", fromTable.Id,
                        "」在 C# 内置副本里不存在 —— 策划加了一行表但代码没同步。"));

                Assert.AreEqual(fromCode!.DisplayKey, fromTable.DisplayKey, fromTable.Id + ".DisplayKey");
                Assert.AreEqual(fromCode.Kind, fromTable.Kind, fromTable.Id + ".Kind");
                Assert.AreEqual(fromCode.DefaultTeam, fromTable.DefaultTeam, fromTable.Id + ".DefaultTeam");
                Assert.AreEqual(fromCode.Control, fromTable.Control, fromTable.Id + ".Control");
                Assert.AreEqual(fromCode.SkillLoadoutId, fromTable.SkillLoadoutId, fromTable.Id + ".SkillLoadout");
                Assert.AreEqual(fromCode.HitRadius, fromTable.HitRadius, 1e-6f, fromTable.Id + ".HitRadius");
                Assert.AreEqual(fromCode.HitHeight, fromTable.HitHeight, 1e-6f, fromTable.Id + ".HitHeight");
                Assert.AreEqual(fromCode.VisualSlot, fromTable.VisualSlot, fromTable.Id + ".VisualSlot");
                Assert.AreEqual(fromCode.StatPackageIds.Base, fromTable.StatPackageIds.Base, fromTable.Id + ".BasePackage");
                checkedCount++;
            }

            Assert.IsTrue(checkedCount > 0, "至少要校验到一个角色");
        }

        /// <summary>内置副本里不能有表里不存在的角色（防止代码里留了已删除的数据行）。</summary>
        [TestMethod]
        public void EveryBuiltinCharacter_ExistsInXlsx()
        {
            for (int i = 0; i < Builtin.Definitions.Count; i++)
            {
                ActorDefinition? fromCode = Builtin.Definitions.Get(i);
                Assert.IsNotNull(fromCode);
                Assert.IsNotNull(FromXlsx.Definitions.Find(fromCode.Id),
                    string.Concat("C# 内置副本里的角色「", fromCode.Id,
                        "」在 characters.xlsx 里不存在 —— 要么补表，要么删代码。"));
            }
        }

        /// <summary>
        /// 数值包逐项相同：这守护的是规则6（数值全部来自表，而不是代码里的字面量）。
        /// <para>
        /// 做法：把两张表各自「(包 id, 层) → 属性→值」摊平后整体比较。
        /// 摊平是为了让失败信息直接点名是哪个包哪个属性，而不是"数量不等"这种无从下手的断言。
        /// </para>
        /// </summary>
        [TestMethod]
        public void EveryStatPack_FromXlsx_MatchesBuiltinValueByValue()
        {
            Dictionary<string, SortedDictionary<StatId, float>> table = FlattenStatPacks(FromXlsx);
            Dictionary<string, SortedDictionary<StatId, float>> code = FlattenStatPacks(Builtin);

            CollectionAssert.AreEquivalent(
                new List<string>(code.Keys), new List<string>(table.Keys), "数值包集合不一致（包名或层不同）");

            foreach (KeyValuePair<string, SortedDictionary<StatId, float>> pack in code)
            {
                SortedDictionary<StatId, float> want = pack.Value;
                SortedDictionary<StatId, float> got = table[pack.Key];

                CollectionAssert.AreEquivalent(
                    new List<StatId>(want.Keys), new List<StatId>(got.Keys),
                    string.Concat("数值包「", pack.Key, "」的属性集合不一致"));

                foreach (KeyValuePair<StatId, float> stat in want)
                {
                    Assert.AreEqual(stat.Value, got[stat.Key], 1e-6f,
                        string.Concat("数值包「", pack.Key, "」的属性 ", stat.Key.ToString(), " 的值不一致"));
                }
            }
        }

        /// <summary>
        /// 把仓库里所有包摊平成「包id → 属性→值」。
        /// <para>
        /// 仓库以包 id 为键、同 id 重复注册会抛异常，因此这里不需要担心多层重叠。
        /// </para>
        /// </summary>
        private static Dictionary<string, SortedDictionary<StatId, float>> FlattenStatPacks(CharacterContentSet set)
        {
            var result = new Dictionary<string, SortedDictionary<StatId, float>>(StringComparer.Ordinal);
            foreach (string packId in set.StatPacksIds())
            {
                StatPackageLayer? layer = set.StatPacks.Find(packId);
                Assert.IsNotNull(layer, "仓库里登记的包「" + packId + "」按 id 查不到");
                var stats = new SortedDictionary<StatId, float>();
                for (int i = 0; i < layer!.Count; i++)
                {
                    stats[layer.StatAt(i)] = layer.ValueAt(i);
                }
                result[packId] = stats;
            }
            return result;
        }

        /// <summary>内置副本与真表产出的内容在"能否装配出实体"上等价。</summary>
        [TestMethod]
        public void XlsxContent_CanAssembleEveryCharacter()
        {
            for (int i = 0; i < FromXlsx.Definitions.Count; i++)
            {
                ActorDefinition? def = FromXlsx.Definitions.Get(i);
                Assert.IsNotNull(def);

                var world = new Game.Core.Ecs.World();
                var pipeline = new Game.Core.Ecs.SystemPipeline(world);
                var queue = new ActorSpawnQueue();
                var slots = new RepositoryPackageSlotResolver(FromXlsx.StatPacks);
                int slot = FromXlsx.Definitions.SlotOf(def.Id);

                pipeline.Add(new ActorAssemblySystem(
                    FromXlsx.Definitions, FromXlsx.Loadouts, FromXlsx.StatPacks, slots, queue), 0);

                queue.Enqueue(new ActorSpawnRequest(slot, System.Numerics.Vector3.Zero));
                pipeline.Execute(0f);

                var asm = (ActorAssemblySystem)pipeline.SystemAt(0);
                Assert.AreEqual(1, asm.LastAssembled.Count,
                    string.Concat("真表内容里的角色「", def.Id, "」无法装配出实体"));
            }
        }
    }
}
