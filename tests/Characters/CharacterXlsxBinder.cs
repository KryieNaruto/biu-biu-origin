using System;
using System.Collections.Generic;
using System.Globalization;
using BiuBiuOrigin.Tests.Content.Data;
using Game.Actor;
using Game.Core;
using Game.Core.Actors;
using Game.Core.Skills;
using Game.Data;

namespace BiuBiuOrigin.Tests.Characters
{
    /// <summary>
    /// 把 <c>data/excel/characters.xlsx</c> 的三张业务表翻译成 <see cref="CharacterContentSet"/>。
    /// <para>
    /// <b>为什么这个类住在测试工程（与 <c>ContentBinder</c> 同一决策）</b>：ARCHITECTURE §4.8 规定
    /// 运行期代码不得读 <c>.xlsx</c>。但如果没有一个"读真表"的通道，
    /// 「策划改表 → 运行时对象变化」这条链在 CI 里就完全没人验证 ——
    /// 生成器或内置副本写错一个数值，测试照样全绿。所以测试工程必须握有这把尺子。
    /// </para>
    /// <para>
    /// <b>与内置副本的关系</b>：本类产出的对象与 <see cref="CharacterContentSet.BuildBuiltinV1"/>
    /// 走的是同一批 <c>Register*Row</c> 方法，因此两者可以直接逐项机械比对，
    /// 而不是靠人眼核对。比对用例见 <see cref="CharacterXlsxParityTests"/>。
    /// </para>
    /// <para>
    /// <b>行号语义</b>：四行表头 + 第 5 行 <c>#data</c> 后，第 6 行起是数据（Excel 真实行号）。
    /// 任何解析失败都抛 <see cref="FormatException"/> 并带上**真实行号**，方便策划在 Excel 里跳过去改。
    /// </para>
    /// </summary>
    public static class CharacterXlsxBinder
    {
        /// <summary>四行表头 + 一行 <c>#data</c> 标记 = 数据行在网格里的 0 起始下标。</summary>
        private const int DataStartRowIndex = 5;

        /// <summary>从工作簿路径构建一套完整的角色运行时内容。</summary>
        /// <param name="xlsxPath">characters.xlsx 的绝对或相对路径。</param>
        public static CharacterContentSet Build(string xlsxPath)
        {
            XlsxReader reader = XlsxReader.Open(xlsxPath);
            var set = new CharacterContentSet();

            LoadStatPacks(reader, set);
            set.FlushStatPacks();
            LoadSkillLoadouts(reader, set);
            set.FlushLoadouts();
            LoadCharacters(reader, set);

            return set;
        }

        /// <summary>读 <c>StatPacks</c> 表：一行 = 一个 (包, 层, 属性) 三元组。</summary>
        private static void LoadStatPacks(XlsxReader reader, CharacterContentSet set)
        {
            string[][] grid = reader.ReadSheet("StatPacks");
            Dictionary<string, int> col = Columns(grid, "StatPacks");

            for (int r = DataStartRowIndex; r < grid.Length; r++)
            {
                string[] line = grid[r];
                if (IsSkippable(line))
                {
                    continue;
                }

                string packId = Cell(line, col, "PackId").Trim();
                string layerText = Cell(line, col, "Layer").Trim();
                string statText = Cell(line, col, "StatId").Trim();
                string valueText = Cell(line, col, "Value").Trim();
                int excelRow = r + 1;

                if (!Enum.TryParse(layerText, ignoreCase: false, out StatLayer layer))
                {
                    throw new FormatException(
                        "characters.xlsx/StatPacks 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                        + "行 Layer 无法解析：「" + layerText + "」；应为 Base/Growth/Equipment/Buff/Temporary。");
                }
                if (!Enum.TryParse(statText, ignoreCase: false, out StatId stat) || !Enum.IsDefined(typeof(StatId), stat))
                {
                    throw new FormatException(
                        "characters.xlsx/StatPacks 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                        + "行 StatId 无法解析：「" + statText + "」；应为主 StatId 枚举成员。");
                }
                if (!float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    throw new FormatException(
                        "characters.xlsx/StatPacks 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                        + "行 Value 无法解析：「" + valueText + "」。");
                }

                set.RegisterPackRow(packId, layer, stat, value);
            }
        }

        /// <summary>读 <c>SkillLoads</c> 表：一行 = 一个技能包里的一个槽。</summary>
        private static void LoadSkillLoadouts(XlsxReader reader, CharacterContentSet set)
        {
            string[][] grid = reader.ReadSheet("SkillLoads");
            Dictionary<string, int> col = Columns(grid, "SkillLoads");

            for (int r = DataStartRowIndex; r < grid.Length; r++)
            {
                string[] line = grid[r];
                if (IsSkippable(line))
                {
                    continue;
                }

                string loadoutId = Cell(line, col, "LoadoutId").Trim();
                string slotText = Cell(line, col, "Slot").Trim();
                string skillIdText = Cell(line, col, "SkillId").Trim();
                string cooldownText = Cell(line, col, "CooldownSeconds").Trim();
                string manaText = Cell(line, col, "ManaCost").Trim();
                int excelRow = r + 1;

                if (!Enum.TryParse(slotText, ignoreCase: false, out SkillSlot slot) || !Enum.IsDefined(typeof(SkillSlot), slot))
                {
                    throw new FormatException(
                        "characters.xlsx/SkillLoads 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                        + "行 Slot 无法解析：「" + slotText + "」；应为 Dash/PrimaryAttack/WeaponSwap/Signature/Interact。");
                }

                set.RegisterLoadoutRow(
                    loadoutId,
                    slot,
                    ParseInt(skillIdText, "SkillLoads", excelRow, "SkillId"),
                    ParseFloat(cooldownText, "SkillLoads", excelRow, "CooldownSeconds"),
                    ParseFloat(manaText, "SkillLoads", excelRow, "ManaCost"));
            }
        }

        /// <summary>读 <c>Characters</c> 表：一行 = 一个角色定义。</summary>
        private static void LoadCharacters(XlsxReader reader, CharacterContentSet set)
        {
            string[][] grid = reader.ReadSheet("Characters");
            Dictionary<string, int> col = Columns(grid, "Characters");

            for (int r = DataStartRowIndex; r < grid.Length; r++)
            {
                string[] line = grid[r];
                if (IsSkippable(line))
                {
                    continue;
                }

                int excelRow = r + 1;
                string id = Cell(line, col, "Id").Trim();
                string displayKey = Cell(line, col, "DisplayKey").Trim();
                string kindText = Cell(line, col, "Kind").Trim();
                string teamText = Cell(line, col, "DefaultTeam").Trim();
                string controlText = Cell(line, col, "Control").Trim();

                if (!Enum.TryParse(kindText, ignoreCase: false, out ActorKind kind) || !Enum.IsDefined(typeof(ActorKind), kind))
                {
                    throw new FormatException(
                        "characters.xlsx/Characters 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                        + "行 Kind 无法解析：「" + kindText + "」。");
                }
                if (!Enum.TryParse(teamText, ignoreCase: false, out TeamId team) || !Enum.IsDefined(typeof(TeamId), team))
                {
                    throw new FormatException(
                        "characters.xlsx/Characters 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                        + "行 DefaultTeam 无法解析：「" + teamText + "」。");
                }
                if (!Enum.TryParse(controlText, ignoreCase: false, out ControlScheme control)
                    || !Enum.IsDefined(typeof(ControlScheme), control))
                {
                    throw new FormatException(
                        "characters.xlsx/Characters 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                        + "行 Control 无法解析：「" + controlText + "」。");
                }

                var packages = new StatPackageIds(
                    Cell(line, col, "BasePackage").Trim(),
                    NullIfBlank(Cell(line, col, "GrowthPackage")) ?? string.Empty,
                    NullIfBlank(Cell(line, col, "EquipmentPackage")) ?? string.Empty);

                set.RegisterCharacterRow(
                    id,
                    displayKey,
                    kind,
                    team,
                    control,
                    packages,
                    Cell(line, col, "SkillLoadout").Trim(),
                    ParseFloat(Cell(line, col, "HitRadius"), "Characters", excelRow, "HitRadius"),
                    ParseFloat(Cell(line, col, "HitHeight"), "Characters", excelRow, "HitHeight"),
                    ParseInt(Cell(line, col, "VisualSlot"), "Characters", excelRow, "VisualSlot"));
            }
        }

        /// <summary>把第 1 行表头读成「列名 → 下标」，并校验列齐全（缺列是数据错误，必须当场炸）。</summary>
        private static Dictionary<string, int> Columns(string[][] grid, string sheetName)
        {
            if (grid.Length == 0)
            {
                throw new FormatException("characters.xlsx/" + sheetName + " 是空表，缺少四行表头。");
            }

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] header = grid[0];
            for (int c = 0; c < header.Length; c++)
            {
                string name = header[c].Trim();
                if (name.Length > 0)
                {
                    map[name] = c;
                }
            }
            return map;
        }

        /// <summary>按列名取值；缺列抛 <see cref="FormatException"/>（而不是静默返回空串）。</summary>
        private static string Cell(string[] line, Dictionary<string, int> col, string column)
        {
            if (!col.TryGetValue(column, out int index))
            {
                throw new FormatException("characters.xlsx 表头缺少列「" + column + "」。");
            }
            return index < line.Length ? line[index] : string.Empty;
        }

        /// <summary>注释行（<c>#</c> 开头）与整行空白都跳过。</summary>
        private static bool IsSkippable(string[] line)
        {
            bool allBlank = true;
            for (int c = 0; c < line.Length; c++)
            {
                if (line[c].Trim().StartsWith("#", StringComparison.Ordinal))
                {
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(line[c]))
                {
                    allBlank = false;
                }
            }
            return allBlank;
        }

        /// <summary>空串 → <c>null</c>（<see cref="StatPackageIds"/> 用 null 表示该层无包）。</summary>
        private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        private static int ParseInt(string text, string sheet, int excelRow, string column)
        {
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new FormatException(
                    "characters.xlsx/" + sheet + " 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                    + "行 " + column + " 无法解析为整数：「" + text + "」。");
            }
            return value;
        }

        private static float ParseFloat(string text, string sheet, int excelRow, string column)
        {
            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                throw new FormatException(
                    "characters.xlsx/" + sheet + " 第" + excelRow.ToString(CultureInfo.InvariantCulture)
                    + "行 " + column + " 无法解析为小数：「" + text + "」。");
            }
            return value;
        }
    }
}
