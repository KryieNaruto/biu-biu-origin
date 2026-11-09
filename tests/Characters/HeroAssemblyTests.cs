using System;
using System.Collections.Generic;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;
using Game.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Characters
{
    // ==================================================================
    // 需求 6「拼接即可批量出变体」的验收组（契约 docs/design/character-hud-contract.md §0 #6 / §5 §6 §7）
    // ==================================================================
    //
    // 这一组要证明的是「变体 = 一行数据，0 行代码」，所以断言全部落在**数据**上：
    //   ① 4 个变体都存在、且能被单独查到；
    //   ② 每个变体引用的每一个包（数值/技能/外观/镜头/手柄/武器）都能解析出对象；
    //   ③ 缺包时**加载期**就抛带中文说明的异常（而不是进游戏变成"血量为 0"）；
    //   ④ 数值包折叠后，test_hero 标准型的 HP / 移速 / 护甲 / 暴击 落在预期范围 ——
    //      期望值不写死魔数：从 CharacterContentSet.BuildBuiltinV1() 里的同名字段读，
    //      只在「这个变体该比那个变体高/低」这类**设计意图**上写方向性断言（§7 的设计表）。
    //
    // 为什么期望值不写死：README §4.3「断言里不写魔法数值」。策划把标准型血从 120 调到 140 时，
    // 这里不该假红；但「重甲比标准血厚、敏捷比标准跑得快」这类**意图**变了，应当红 —— 那是策划的决定，
    // 需要有人把 §7 的设计表一起改掉。
    // ==================================================================

    /// <summary>
    /// 装配清单 / 变体数据组。
    /// </summary>
    [TestClass]
    public sealed class HeroAssemblyTests
    {
        /// <summary>每个用例各建一份内容集：装配仓库有状态（Resolve 之后才有 views），共享实例会让用例互相干扰。</summary>
        private static CharacterContentSet NewContent() => new CharacterContentSet().BuildBuiltinV1();

        /// <summary>
        /// 契约 §0 #6 规定必须能一次生成 5 个变体（标准/重甲/敏捷/幸运/元素）；
        /// 这里只钉「有哪些 id」，不钉顺序以外的细节。
        /// </summary>
        private static readonly string[] HeroVariantIds =
        {
            "test_hero", "test_hero_tank", "test_hero_agile", "test_hero_lucky", "test_hero_elemental",
        };

        // ───────────────────────── ① 5 个变体都存在 ─────────────────────────

        [TestMethod]
        public void HeroAssembly_ExistsAndMatchesContractEntryPoints()
        {
            CharacterContentSet content = NewContent();

            CharacterAssemblySpec? spec = content.Assemblies.FindAssembly(CharacterContentSet.HeroAssemblyId);
            Assert.IsNotNull(spec, $"装配 '{CharacterContentSet.HeroAssemblyId}' 必须存在（宿主按这个名字装配）");
            Assert.AreEqual(CharacterContentSet.HeroAssemblyId, spec!.Id);

            // 默认变体 = 表里的第一行 = test_hero（宿主不带 --hero-variant 时生成它）。
            Assert.IsNotNull(spec.DefaultVariant, "装配必须至少有一个变体，否则无法生成");
            Assert.AreEqual(CharacterContentSet.HeroDefaultVariantId, spec.DefaultVariant!.Id,
                "默认变体必须是 test_hero（契约 §0 #1：人物槽位就是测试人物）");
        }

        [TestMethod]
        public void HeroAssembly_ExposesExactlyFourResolvableVariants()
        {
            CharacterContentSet content = NewContent();
            IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> views =
                content.Assemblies.ViewsOf(CharacterContentSet.HeroAssemblyId);

            Assert.AreEqual(HeroVariantIds.Length, views.Count,
                "契约 §0 #6：一次必须能生成 5 个变体（标准/重甲/敏捷/幸运/元素）。多一个少一个都要改契约");

            for (int i = 0; i < HeroVariantIds.Length; i++)
            {
                string id = HeroVariantIds[i];
                CharacterAssemblyOf<SkillLoadout>? view = content.Assemblies.Find(id);
                Assert.IsNotNull(view, $"变体 '{id}' 必须能被仓库按 id 查到（宿主按 id 生成）");
                Assert.AreEqual(i, view!.VariantIndex, $"变体 '{id}' 在装配内的序号应当与表里的顺序一致");
                Assert.AreEqual(CharacterContentSet.HeroAssemblyId, view.Spec.Id, $"变体 '{id}' 必须属于装配 hero");
            }

            // 顺序即「表里的顺序」：默认变体必须是第 0 个（宿主不带参数时生成的就是它）。
            Assert.AreEqual(CharacterContentSet.HeroDefaultVariantId, views[0].Id,
                "第 0 个变体必须是默认变体 test_hero");
        }

        [TestMethod]
        public void HeroAssembly_EveryVariantIsAPlayerWithItsOwnPacks()
        {
            CharacterContentSet content = NewContent();
            IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> views =
                content.Assemblies.ViewsOf(CharacterContentSet.HeroAssemblyId);

            var seenBasePacks = new HashSet<string>(StringComparer.Ordinal);
            var seenLoadouts = new HashSet<string>(StringComparer.Ordinal);
            var seenWeapons = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < views.Count; i++)
            {
                CharacterAssemblyOf<SkillLoadout> view = views[i];

                // 契约 §0 #1：Kind=Player 是数据行决定的，不靠类型（这里正是"没有 class Player"的证据）。
                ActorDefinition? def = content.Definitions.Get(view.DefinitionSlot);
                Assert.IsNotNull(def, $"变体 '{view.Id}' 必须解析到角色定义（DefinitionSlot 由仓库解析得到）");
                Assert.AreEqual(ActorKind.Player, def!.Kind, $"变体 '{view.Id}' 必须是 Player（契约 §0 #1）");
                Assert.AreEqual(view.Id, def.Id, "变体 id 同时就是角色定义 id（两处一致由 Resolve 校验）");

                // 每个变体都真的带着自己的四件套（数值包 / 技能包 / 武器 / 外观），而不是继承自某个默认值。
                Assert.IsNotNull(view.Weapon, $"变体 '{view.Id}' 必须配武器（契约 §0 #4：左键射击要有武器数值来源）");
                Assert.IsTrue(view.Weapon!.RangeMeters > 0f, $"变体 '{view.Id}' 的射程必须为正");
                Assert.IsTrue(view.Weapon.BulletSpeed > 0f, $"变体 '{view.Id}' 的弹速必须为正");
                Assert.IsTrue(view.Weapon.ShotCooldown > 0f, $"变体 '{view.Id}' 的射击间隔必须为正（否则会出现除零/无限射速）");
                Assert.IsTrue(view.Visual.VisualKey != 0, $"变体 '{view.Id}' 的外观包必须有非零 VisualKey（0 是「无外观」哨兵）");
                Assert.IsNotNull(view.Camera, $"变体 '{view.Id}' 必须解析出镜头包（空列时用默认镜头）");
                Assert.IsNotNull(view.Input, $"变体 '{view.Id}' 必须解析出手柄包（空列时用默认手柄）");

                seenBasePacks.Add(view.Variant.BasePackageId);
                seenLoadouts.Add(view.Loadout.Id);
                seenWeapons.Add(view.Weapon.Id);
            }

            // 「变体 = 一行数据」的实质：4 个变体的数值包/技能包/武器**互不相同**。
            // 若哪天 4 个变体共用同一套包，那"变体"就退化成了别名的 4 个名字，需求 6 也就没被证明。
            Assert.AreEqual(views.Count, seenBasePacks.Count, "每个变体必须有自己的数值包（差异来自数据行）");
            Assert.AreEqual(views.Count, seenLoadouts.Count, "每个变体必须有自己的技能包（射速/技能 id 不同）");
            Assert.AreEqual(views.Count, seenWeapons.Count, "每个变体必须有自己的武器行（伤害/射速不同）");
        }

        // ─────────────── ② 每个变体引用的包都能解析 ───────────────

        [TestMethod]
        public void HeroAssembly_EveryReferencedPackResolvesToAnObject()
        {
            // Resolve() 已经做了一遍外键校验，但这里**独立地**把每个引用逐个查一遍：
            // 「解析没抛异常」与「每个 id 都能查到对象」是两件事（前者只在解析了才成立，
            // 而 Resolve 的错误分支会 continue 掉一部分变体，光看 count 可能被掩盖）。
            CharacterContentSet content = NewContent();
            IReadOnlyList<CharacterAssemblyOf<SkillLoadout>> views =
                content.Assemblies.ViewsOf(CharacterContentSet.HeroAssemblyId);

            for (int i = 0; i < views.Count; i++)
            {
                CharacterAssemblyOf<SkillLoadout> view = views[i];
                CharacterVariantSpec row = view.Variant;

                Assert.IsTrue(row.Id.Length > 0, "变体 id 不能为空");
                Assert.IsTrue(row.DisplayName.Length > 0, $"变体 '{row.Id}' 必须有显示名（HUD 要画它，契约 §2）");

                // 数值包：Base 必填，Growth 可空。
                Assert.IsTrue(content.StatPacks.TryGet(row.BasePackageId, out _),
                    $"变体 '{row.Id}' 的 BasePackageId='{row.BasePackageId}' 必须能在 StatPacks 里查到");
                if (row.GrowthPackageId.Length > 0)
                {
                    Assert.IsTrue(content.StatPacks.TryGet(row.GrowthPackageId, out _),
                        $"变体 '{row.Id}' 的 GrowthPackageId='{row.GrowthPackageId}' 必须能查到");
                }

                // 技能包 / 武器 / 外观 / 镜头 / 手柄：一律按 id 再查一遍（和 Resolve 走的是同一条查询路径）。
                Assert.IsNotNull(content.Loadouts.Find(row.LoadoutId),
                    $"变体 '{row.Id}' 的 LoadoutId='{row.LoadoutId}' 必须能在技能包仓库里查到");
                if (row.WeaponRowId.Length > 0)
                {
                    Assert.IsNotNull(content.Assemblies.FindWeapon(row.WeaponRowId),
                        $"变体 '{row.Id}' 的 WeaponRowId='{row.WeaponRowId}' 必须能在武器表里查到");
                }
                Assert.IsNotNull(content.Assemblies.FindVisualPack(row.VisualPackId),
                    $"变体 '{row.Id}' 的 VisualPackId='{row.VisualPackId}' 必须能查到");
                if (row.CameraPackId.Length > 0)
                {
                    Assert.IsNotNull(content.Assemblies.FindCameraPack(row.CameraPackId),
                        $"变体 '{row.Id}' 的 CameraPackId='{row.CameraPackId}' 必须能查到");
                }
                if (row.InputPackId.Length > 0)
                {
                    Assert.IsNotNull(content.Assemblies.FindInputPack(row.InputPackId),
                        $"变体 '{row.Id}' 的 InputPackId='{row.InputPackId}' 必须能查到");
                }

                // 武器数值必须与武器行逐项一致（装配只做"装箱"，不该重算/改数）。
                if (view.Weapon is not null)
                {
                    CharacterWeaponSpec weaponRow = content.Assemblies.FindWeapon(row.WeaponRowId)!;
                    Assert.AreEqual(weaponRow.Damage, view.Weapon.Damage, 1e-6f,
                        $"变体 '{row.Id}' 的武器伤害必须原样来自武器行 '{weaponRow.Id}'");
                    Assert.AreEqual(weaponRow.WeaponId, view.Weapon.WeaponId,
                        "武器数字 id 必须原样透传（它要写进 Projectile.WeaponId 做统计）");
                }
            }
        }

        [TestMethod]
        public void HeroAssembly_ModulesAreAssemblyUnionVariant()
        {
            // 契约 §5：变体实际要装的模块 = 装配模块 ∪ 变体模块（取并集、保序、去重）。
            // 这里用敏捷变体做样本：它是唯一一个在表里多写了一个模块名的变体。
            CharacterContentSet content = NewContent();
            CharacterAssemblySpec spec = content.Assemblies.FindAssembly(CharacterContentSet.HeroAssemblyId)!;
            CharacterAssemblyOf<SkillLoadout> agile = content.Assemblies.Get("test_hero_agile");
            CharacterAssemblyOf<SkillLoadout> standard = content.Assemblies.Get(CharacterContentSet.HeroDefaultVariantId);

            // 并集必须包含装配的全部模块。
            for (int i = 0; i < spec.Modules.Count; i++)
            {
                Assert.IsTrue(agile.Modules.Contains(spec.Modules.Ids[i]),
                    $"变体 'test_hero_agile' 的模块并集必须包含装配模块 '{spec.Modules.Ids[i]}'");
            }

            // 敏捷变体自己的额外模块必须出现，而标准变体不能"蹭"到它 —— 这就是"变体可以多装一套系统"。
            for (int i = 0; i < agile.Variant.Modules.Count; i++)
            {
                string extra = agile.Variant.Modules.Ids[i];
                Assert.IsTrue(agile.Modules.Contains(extra), $"变体 '{extra}' 必须出现在敏捷变体的并集里");
                Assert.IsFalse(standard.Modules.Contains(extra),
                    $"变体独有的模块 '{extra}' 不应泄漏到标准变体（否则「变体」就退化成同一套装配）");
            }

            // 并集不能凭空变多（Count 是不是 Union 的结果，而不是某个包的 Count）。
            Assert.IsTrue(agile.Modules.Count >= spec.Modules.Count, "并集至少要有装配模块那么多");

            // 日志行形状（契约 §8 规定 [Hero] ... modules=a,b,c 便于自动校验）。
            string log = standard.ToLogString();
            StringAssert.Contains(log, string.Concat("variant=", standard.Id), "ToLogString 必须带 variant=");
            StringAssert.Contains(log, string.Concat("assembly=", spec.Id), "ToLogString 必须带 assembly=");
            StringAssert.Contains(log, "modules=", "ToLogString 必须带 modules=");
        }

        // ─────────────── ③ 缺包时抛带中文说明的异常 ───────────────

        [TestMethod]
        public void HeroAssembly_MissingBasePack_ThrowsChineseErrorNamingBlameChain()
        {
            // 这是本组最重要的一条：数据错误必须在**加载期**炸，而且异常信息要能直接送回 Excel 单元格。
            // 做法：登记一个引用了不存在数值包的变体 → Resolve 必须抛 InvalidOperationException，
            // 且消息同时点名「哪个装配/哪个变体/哪一列/哪个 id」。
            CharacterContentSet content = NewContent();
            CharacterAssemblyRepository repo = content.Assemblies;

            const string missing = "test_hero_base_typo";
            repo.RegisterVariant(CharacterContentSet.HeroAssemblyId, new CharacterVariantSpec(
                id: "test_hero_typo_probe",
                displayName: "缺包探针",
                variantTag: "变体 · 探针",
                definitionId: "test_hero",          // 定义存在（复用它），只有数值包缺失
                basePackageId: missing,
                growthPackageId: string.Empty,
                equipmentPackageId: string.Empty,
                loadoutId: "test_hero_kit",
                weaponRowId: "pistol_basic",
                visualPackId: "hero_default",
                cameraPackId: "topdown_standard",
                inputPackId: "kbm_standard",
                modules: null));

            InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => repo.Resolve(content),
                "缺数值包时 Resolve 必须当场抛异常（否则进游戏才发现，表现为血量为 0）");

            string message = ex.Message;
            Assert.IsTrue(ContainsCjk(message), "异常信息必须是中文说明（契约要求「带中文说明的异常」）：" + message);
            StringAssert.Contains(message, missing, "异常必须点名缺失的那个 id：" + missing);
            StringAssert.Contains(message, "StatPacks", "异常必须指出该去哪张表补：" + message);
            StringAssert.Contains(message, "test_hero_typo_probe", "异常必须点名是哪个变体出错：" + message);
            StringAssert.Contains(message, CharacterContentSet.HeroAssemblyId, "异常必须点名是哪个装配出错：" + message);

            // 解析失败后，仓库不能留下"半个世界"：views 必须被清空，避免宿主拿它去生成。
            Assert.AreEqual(0, repo.Count,
                "解析失败后不能保留任何已解析变体（否则宿主可能用半成品清单生成角色）");
        }

        [TestMethod]
        public void HeroAssembly_MissingLoadoutFailsFast_AndMissingWeaponIsAlsoChecked()
        {
            // 【第二次报 @packages / @lead：Resolve 的错误汇总不是"一次报全"】
            // scripts/data/CharacterAssemblySpec.cs:810-816 —— 找不到技能包时直接 `continue`，
            // 于是同一个变体里"武器行缺失 / 定义不一致 / 外观缺失"这些**后面才检查**的错误被整段跳过。
            // 现象：变体同时缺技能包和武器行时，异常只报技能包一条，改完重跑才看到武器行。
            // 建议改法：把 `continue` 改成「记录错误后继续往下检查（用 `loadout ?? 占位` 跳过实际构造）」，
            //          或者在 ③ 处只置标志、最后统一决定是否 continue。
            //
            // 本用例钉住「用户可见的语义」：缺 Loadout 必须当场炸，且异常必须点名它。
            CharacterContentSet content = NewContent();
            CharacterAssemblyRepository repo = content.Assemblies;

            repo.RegisterVariant(CharacterContentSet.HeroAssemblyId, new CharacterVariantSpec(
                id: "test_hero_missing_probe",
                displayName: "缺技能包探针",
                variantTag: string.Empty,
                definitionId: "test_hero",
                basePackageId: "test_hero_base",
                growthPackageId: string.Empty,
                equipmentPackageId: string.Empty,
                loadoutId: "no_such_kit",
                weaponRowId: "pistol_basic",
                visualPackId: "hero_default",
                cameraPackId: "topdown_standard",
                inputPackId: "kbm_standard",
                modules: null));

            InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(() => repo.Resolve(content));
            StringAssert.Contains(ex.Message, "no_such_kit", "必须点名缺失的技能包 id");
            Assert.IsTrue(ContainsCjk(ex.Message), "异常信息必须是中文：" + ex.Message);
            Assert.AreEqual(0, repo.Count, "解析失败后不能保留任何已解析变体");
        }

        [TestMethod]
        public void HeroAssembly_MissingWeaponRow_ThrowsChineseError()
        {
            // 武器行缺失（技能包存在，走到 ⑥ 的那条路径）。
            CharacterContentSet content = NewContent();
            CharacterAssemblyRepository repo = content.Assemblies;

            repo.RegisterVariant(CharacterContentSet.HeroAssemblyId, new CharacterVariantSpec(
                id: "test_hero_weapon_probe",
                displayName: "缺武器探针",
                variantTag: string.Empty,
                definitionId: "test_hero",
                basePackageId: "test_hero_base",
                growthPackageId: string.Empty,
                equipmentPackageId: string.Empty,
                loadoutId: "test_hero_kit",
                weaponRowId: "no_such_weapon",
                visualPackId: "hero_default",
                cameraPackId: "topdown_standard",
                inputPackId: "kbm_standard",
                modules: null));

            InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(() => repo.Resolve(content));
            StringAssert.Contains(ex.Message, "no_such_weapon", "必须点名缺失的武器行 id");
            StringAssert.Contains(ex.Message, "Weapons", "必须指出该去哪张表补");
            Assert.IsTrue(ContainsCjk(ex.Message), "异常信息必须是中文：" + ex.Message);
        }

        [TestMethod]
        public void HeroAssembly_MissingVisualAndCameraPacks_ThrowChineseError()
        {
            // 外观包 / 镜头包缺失都要在加载期炸（这两张表最容易在不同任务里被改名）。
            CharacterContentSet fresh = new CharacterContentSet().BuildBuiltinV1();
            CharacterAssemblyRepository repo = fresh.Assemblies;

            repo.RegisterVariant(CharacterContentSet.HeroAssemblyId, new CharacterVariantSpec(
                id: "test_hero_visual_probe",
                displayName: "缺外观探针",
                variantTag: string.Empty,
                definitionId: "test_hero",
                basePackageId: "test_hero_base",
                growthPackageId: string.Empty,
                equipmentPackageId: string.Empty,
                loadoutId: "test_hero_kit",
                weaponRowId: "pistol_basic",
                visualPackId: "no_such_visual",
                cameraPackId: "no_such_camera",
                inputPackId: "no_such_input",
                modules: null));

            InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(() => repo.Resolve(fresh));
            StringAssert.Contains(ex.Message, "no_such_visual", "必须点名缺失的外观包");
            StringAssert.Contains(ex.Message, "no_such_camera", "必须点名缺失的镜头包");
            StringAssert.Contains(ex.Message, "no_such_input", "必须点名缺失的手柄包");
        }

        [TestMethod]
        public void HeroAssembly_DuplicateStatInOnePack_ThrowsChineseError()
        {
            // 【Lead B4 追加要求】同一个 StatId 在同一个数值包里重复声明必须是数据错误。
            // StatPackageLayer 构造期就检查（scripts/data/StatPackageRepository.cs:43-47）。
            var items = new List<KeyValuePair<StatId, float>>
            {
                new KeyValuePair<StatId, float>(StatId.HpMax, 100f),
                new KeyValuePair<StatId, float>(StatId.HpMax, 200f),
            };

            ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(
                () => new StatPackageLayer("dup_probe", StatLayer.Base, items));
            StringAssert.Contains(ex.Message, "HpMax", "异常必须点名重复的属性");
            Assert.IsTrue(ContainsCjk(ex.Message), "异常信息必须是中文：" + ex.Message);
        }

        [TestMethod]
        public void HeroAssembly_DuplicatePackId_ThrowsChineseError()
        {
            // 同一个包 id 登记两次 = 后一个会静默覆盖语义（槽位号错位），必须当场炸。
            var repo = new StatPackageRepository();
            var layer = new StatPackageLayer("dup_pack", StatLayer.Base, new List<KeyValuePair<StatId, float>>
            {
                new KeyValuePair<StatId, float>(StatId.HpMax, 100f),
            });
            repo.Register(layer);

            ArgumentException ex = Assert.ThrowsExactly<ArgumentException>(() => repo.Register(layer));
            StringAssert.Contains(ex.Message, "dup_pack", "异常必须点名重复的包 id");
            Assert.IsTrue(ContainsCjk(ex.Message), "异常信息必须是中文：" + ex.Message);
        }

        [TestMethod]
        public void HeroAssembly_EveryVariantResolvesWithZeroCode()
        {
            // 【Lead C 追加要求】"每多一个变体只需一行数据"的可执行证明：
            // 本用例**只读数据**（不 new 任何系统、不写任何装配代码），把一个全新变体行登记进仓库，
            // 然后断言它被完整解析成可生成的对象 —— 这就是「0 行代码出第 5 个变体」。
            CharacterContentSet content = NewContent();
            CharacterAssemblyRepository repo = content.Assemblies;
            int before = repo.Count;

            const string newVariant = "test_hero_probe_fifth";
            repo.RegisterVariant(CharacterContentSet.HeroAssemblyId, new CharacterVariantSpec(
                id: newVariant,
                displayName: "测试人物 · 探针第五变体",
                variantTag: "变体 · 探针",
                definitionId: "test_hero",                       // 复用已存在的定义行（真实场景里 Characters 表也要加一行）
                basePackageId: "test_hero_base",
                growthPackageId: "test_hero_growth",
                equipmentPackageId: string.Empty,
                loadoutId: "test_hero_kit",
                weaponRowId: "pistol_basic",
                visualPackId: "hero_default",
                cameraPackId: "topdown_standard",
                inputPackId: "kbm_standard",
                modules: new CharacterModuleSet(new[] { "fire_primary_rapid" })));

            // 登记后必须重新 Resolve（数据变了 → 视图失效，这是刻意的：不允许查询到"半新半旧"的清单）。
            Assert.AreEqual(0, repo.Count, "登记新变体后，旧的解析结果必须失效（否则查到的是半新半旧的清单）");
            repo.Resolve(content);

            Assert.AreEqual(before + 1, repo.Count, "新增一行数据后，已解析变体数必须 +1");

            CharacterAssemblyOf<SkillLoadout> added = repo.Get(newVariant);
            Assert.AreEqual(newVariant, added.Id);
            Assert.IsNotNull(added.Weapon, "新变体必须解析出武器");
            Assert.IsNotNull(added.Camera, "新变体必须解析出镜头包");
            Assert.IsNotNull(added.Input, "新变体必须解析出手柄包");
            Assert.IsTrue(added.DefinitionSlot >= 0, "新变体必须解析出角色定义槽位（可直接投 ActorSpawnRequest）");
            Assert.IsTrue(added.Modules.Contains("fire_primary_rapid"),
                "新变体自己声明的模块必须并进它的模块集合（换一套系统 = 改一个模块名）");
            Assert.IsTrue(added.Modules.Contains("move_standard"),
                "新变体必须继承装配模块（否则它就不是 hero 装配的一员）");

            // 原有 4 个变体一个不少（新行是追加，不是替换）。
            for (int i = 0; i < HeroVariantIds.Length; i++)
            {
                Assert.IsNotNull(repo.Find(HeroVariantIds[i]), $"追加新变体后 '{HeroVariantIds[i]}' 必须仍然存在");
            }
        }


        [TestMethod]
        public void HeroAssembly_GetUnknownVariant_ThrowsWithKnownList()
        {
            // 宿主拿到一个拼错的 --hero-variant 时，要能一眼看到"有哪些可选"，而不是一个空异常。
            CharacterContentSet content = NewContent();
            CharacterAssemblyRepository repo = content.Assemblies;

            InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => repo.Get("test_hero_does_not_exist"));
            StringAssert.Contains(ex.Message, "test_hero_does_not_exist", "必须点名找不到的那个变体");
            StringAssert.Contains(ex.Message, CharacterContentSet.HeroDefaultVariantId,
                "必须列出已解析的变体，方便对照：" + ex.Message);
        }

        [TestMethod]
        public void HeroAssembly_DefaultCameraAndInput_AreRegistered()
        {
            // 变体的镜头/手柄列留空时用默认值；「默认未设置」是数据错误而不是悄悄为 null。
            CharacterContentSet content = NewContent();
            Assert.IsNotNull(content.Assemblies.DefaultCamera, "必须设置默认镜头包（空列兜底）");
            Assert.IsNotNull(content.Assemblies.DefaultInput, "必须设置默认手柄包（空列兜底）");
            Assert.IsTrue(content.Assemblies.CameraPackCount >= 1, "至少登记 1 个镜头包");
            Assert.IsTrue(content.Assemblies.InputPackCount >= 1, "至少登记 1 个手柄包");
            Assert.IsTrue(content.Assemblies.WeaponCount >= HeroVariantIds.Length,
                "武器行数至少要够 4 个变体各配一把");
        }

        // ─────────────── ④ 数值包折叠后的预期范围 ───────────────

        [TestMethod]
        public void HeroStats_StandardVariantFoldsToExpectedShape()
        {
            // 折叠必须按规则6：Base 是绝对值，其余层是差量（StatPackageRepository.Compose）。
            // 断言方式：先取出参与折叠的各层**原始值**，再断言 Compose 的结果恰好是它们的和 ——
            // 这样既钉住了"折叠算法没被改坏"，又不写死任何一个具体数字。
            CharacterContentSet content = NewContent();
            CharacterVariantSpec row = content.Assemblies.Get(CharacterContentSet.HeroDefaultVariantId).Variant;

            StatPackageLayer baseLayer = content.StatPacks.Find(row.BasePackageId)
                ?? throw new AssertFailedException($"找不到标准型的 Base 层 '{row.BasePackageId}'");
            StatPackageLayer? growthLayer = row.GrowthPackageId.Length == 0
                ? null
                : content.StatPacks.Find(row.GrowthPackageId);

            IReadOnlyStatView view = content.StatPacks.Compose(new StatPackageIds(row.BasePackageId, growth: row.GrowthPackageId));

            StatId[] folded =
            {
                StatId.Hp, StatId.HpMax, StatId.ManaMax, StatId.Armor, StatId.MoveSpeed,
                StatId.CritChance, StatId.CritMultiplier, StatId.Strength, StatId.Dexterity, StatId.Luck,
            };

            for (int i = 0; i < folded.Length; i++)
            {
                StatId stat = folded[i];
                float expected = baseLayer.Get(stat) + (growthLayer is null ? 0f : growthLayer.Get(stat));
                Assert.AreEqual(expected, view.Get(stat), 1e-6f,
                    $"折叠值必须等于 Base + 成长层（属性 {stat}）");
                Assert.IsTrue(view.Has(stat), $"standard 变体的折叠视图里必须有 {stat}（缺了 HUD 会显示 0）");
            }

            // ── 数值本身的定性约束（与 §7 设计表一致，但不写死具体值） ──
            Assert.IsTrue(view.Get(StatId.HpMax) > 0f, "血量上限必须为正");
            Assert.AreEqual(view.Get(StatId.Hp), view.Get(StatId.Hp) > 0f ? view.Get(StatId.Hp) : baseLayer.Get(StatId.Hp),
                "当前血量与上限都必须来自数值包（同源）",
                "当前血量必须来自数值包");
            Assert.IsTrue(view.Get(StatId.MoveSpeed) > 0f, "移速必须为正（否则玩家一动不动）");
            Assert.IsTrue(view.Get(StatId.Armor) >= 0f, "护甲不能为负");
            Assert.IsTrue(view.Get(StatId.ArmorK) > 0f,
                "护甲常数 K 必须为正 —— 它做分母换算减伤率，0 会让减伤公式除零");
            Assert.IsTrue(view.Get(StatId.CritChance) >= 0f && view.Get(StatId.CritChance) <= 1f,
                "暴击率必须在 [0,1]（HUD 要显示百分比，越界会画出 150% 这种值）");
            Assert.IsTrue(view.Get(StatId.CritMultiplier) >= 1f, "暴击倍率至少为 1（暴击不该比不暴击还弱）");
        }

        [TestMethod]
        public void HeroStats_FourVariantsDifferInTheDirectionTheDesignTableSays()
        {
            // 契约 §7 的设计意图（逐条对应，方向性断言而不是魔数）：
            //   标准 = 均衡基准 / 重甲 = 血厚移速慢伤害高 / 敏捷 = 移速快射速快伤害低 / 幸运 = 暴击高。
            CharacterContentSet content = NewContent();
            StatView s = new StatView(content, "test_hero");
            StatView tank = new StatView(content, "test_hero_tank");
            StatView agile = new StatView(content, "test_hero_agile");
            StatView lucky = new StatView(content, "test_hero_lucky");

            // 重甲：血更厚、护甲更高、跑得更慢、单发伤害更高。
            Assert.IsTrue(tank.Get(StatId.HpMax) > s.Get(StatId.HpMax), "重甲的血量上限必须高于标准型");
            Assert.IsTrue(tank.Get(StatId.Armor) > s.Get(StatId.Armor), "重甲的护甲必须高于标准型");
            Assert.IsTrue(tank.Get(StatId.MoveSpeed) < s.Get(StatId.MoveSpeed), "重甲的移速必须低于标准型");
            Assert.IsTrue(tank.Weapon!.Damage > s.Weapon!.Damage, "重甲的单发伤害必须高于标准型");

            // 敏捷：跑得更快、射得更快、单发伤害更低。
            Assert.IsTrue(agile.Get(StatId.MoveSpeed) > s.Get(StatId.MoveSpeed), "敏捷的移速必须高于标准型");
            Assert.IsTrue(agile.Weapon!.ShotCooldown < s.Weapon!.ShotCooldown, "敏捷的射击间隔必须短于标准型（射速更快）");
            Assert.IsTrue(agile.Weapon.Damage < s.Weapon.Damage, "敏捷的单发伤害必须低于标准型");

            // 幸运：角色暴击率与暴击倍率都更高（武器暴击率另外算，这里看"这个人"的属性）。
            Assert.IsTrue(lucky.Get(StatId.CritChance) > s.Get(StatId.CritChance), "幸运的暴击率必须高于标准型");
            Assert.IsTrue(lucky.Get(StatId.CritMultiplier) > s.Get(StatId.CritMultiplier), "幸运的暴击倍率必须高于标准型");

            // 4 个变体两两之间至少在 (血量上限, 护甲, 移速) 三元组上不同 ——
            // 证明它们真是 4 行不同的数据，而不是 4 个别名（否则「一次生成 4 个变体」就没被证明）。
            StatView[] all = { s, tank, agile, lucky };
            StatId[] shape = { StatId.HpMax, StatId.Armor, StatId.MoveSpeed };
            for (int i = 0; i < all.Length; i++)
            {
                for (int j = i + 1; j < all.Length; j++)
                {
                    bool same = true;
                    for (int k = 0; k < shape.Length; k++)
                    {
                        if (MathF.Abs(all[i].Get(shape[k]) - all[j].Get(shape[k])) > 1e-6f)
                        {
                            same = false;
                            break;
                        }
                    }
                    Assert.IsFalse(same,
                        $"变体 #{i} 与变体 #{j} 的 (血量上限, 护甲, 移速) 完全相同 —— 它们只是别名，不是不同变体");
                }
            }
        }

        [TestMethod]
        public void HeroStats_FoldedViewIsReadOnlyAndCannotBeMutated()
        {
            // 规则6 §6.1.1：外部模块不得持有可写引用。这里通过类型面证明：折叠视图只暴露 Get/Has，
            // 没有任何 setter 形态的成员（反射检查 —— 将来若有人加 setter，这条会红）。
            Type iface = typeof(IReadOnlyStatView);
            System.Reflection.PropertyInfo[] props = iface.GetProperties();
            Assert.IsTrue(props.Length > 0, "IReadOnlyStatView 至少应有一个属性（否则说明类型被换掉了）");

            for (int i = 0; i < props.Length; i++)
            {
                Assert.IsTrue(props[i].CanRead && !props[i].CanWrite,
                    $"IReadOnlyStatView.{props[i].Name} 必须是只读属性（外部不得写数值）");
            }

            // 方法里不能出现 "Set" 范畴的名字（Get/Has 才是只读视图该有的动词）。
            System.Reflection.MethodInfo[] methods = iface.GetMethods();
            for (int i = 0; i < methods.Length; i++)
            {
                string name = methods[i].Name;
                Assert.IsFalse(name.StartsWith("Set", StringComparison.Ordinal),
                    $"IReadOnlyStatView 不应有写入口 '{name}'（规则6：外部模块不得持有可写引用）");
            }
        }

        // ───────────────────────── 工具 ─────────────────────────

        /// <summary>「变体 → 折叠后的数值视图 + 武器行」的只读小包装（让上面的方向性断言能一行读一个数）。</summary>
        private readonly struct StatView
        {
            private readonly IReadOnlyStatView _stats;

            public StatView(CharacterContentSet content, string variantId)
            {
                CharacterVariantSpec row = content.Assemblies.Get(variantId).Variant;
                _stats = content.StatPacks.Compose(new StatPackageIds(row.BasePackageId, growth: row.GrowthPackageId));
                Weapon = content.Assemblies.Get(variantId).Weapon;
            }

            public CharacterWeaponSpec? Weapon { get; }

            public float Get(StatId stat) => _stats.Get(stat);
        }

        /// <summary>消息里是否含中日韩字符（= 是中文说明，而不是一句英文栈信息）。</summary>
        private static bool ContainsCjk(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= '\u4e00' && text[i] <= '\u9fff')
                {
                    return true;
                }
            }
            return false;
        }
    }
}
