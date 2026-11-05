using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace BiuBiuOrigin.Tests.Content.Data
{
    /// <summary>
    /// 极简 xlsx 读取器：<b>只存在于测试工程</b>，用 <see cref="ZipArchive"/> + 手写 XML 扫读，
    /// 把一张 Sheet 读成"行 × 列"的纯字符串网格。
    /// <para>
    /// <b>为什么它不能进运行期（ARCHITECTURE §4.8）</b>：
    /// </para>
    /// <list type="bullet">
    /// <item>运行期只能读 <c>data/generated/</c> 的生成物；让游戏进程去解 .xlsx 等于把「构建期」和「运行期」的边界拆掉，
    /// 策划改表后游戏会"看起来生效了"，绕过校验器与 schema 版本检查；</item>
    /// <item>但反过来，如果<u>测试</u>也只读生成物，那么"策划改表 → 运行时对象变化"这条链在 CI 里就没人验证 ——
    /// 生成器写错一个列名，测试照样全绿。所以测试工程里必须有这样一把尺子，直接量真实 xlsx。</item>
    /// </list>
    /// <para>
    /// <b>为什么零第三方依赖</b>：项目 NuGet 是离线源（AGENTS §6），加一个 Excel 包要动 <c>nuget.config</c>；
    /// 而 xlsx 本质是 zip + 两份 XML（共享字符串表 + Sheet），手写两百行即可覆盖四行表头这种规整表格。
    /// 不支持：合并单元格、公式（按规则4 §5.1 本来就禁止）、样式、日期格式 —— 这些出现在表里就是数据错误。
    /// </para>
    /// <para>
    /// <b>单元格坐标是 Excel 真实坐标</b>（含空行空列）：第 5 行数据行读回来就是第 5 行，
    /// 于是错误信息里的"第 N 行"与策划在 Excel 里看到的行号完全一致。
    /// </para>
    /// </summary>
    public sealed class XlsxReader
    {
        /// <summary>共享字符串表 <c>xl/sharedStrings.xml</c> 的固定包内路径（OOXML 规范）。</summary>
        private const string SharedStringsPath = "xl/sharedStrings.xml";

        /// <summary>关系文件 <c>xl/_rels/workbook.xml.rels</c> 的固定包内路径。</summary>
        private const string WorkbookRelsPath = "xl/_rels/workbook.xml.rels";

        /// <summary>工作簿 <c>xl/workbook.xml</c> 的固定包内路径。</summary>
        private const string WorkbookPath = "xl/workbook.xml";

        /// <summary>XML 命名空间（DrawingML 主命名空间）：OOXML 的所有元素都在它下面。</summary>
        private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        /// <summary>关系命名空间（r:id 属性所属）。</summary>
        private const string RelNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>
        /// 列号 / 行号的十进制基数（A=1..Z=26 是"26 进制但不是 0 起始"，所以每位减 1 再乘 26）。
        /// 这是 OOXML 单元格地址的编码规则，不是游戏数值。
        /// </summary>
        private const int ColumnBase = 26;

        private readonly Dictionary<string, string> _sheetPathByName;
        private readonly List<string> _sheetNames;
        private readonly string[] _sharedStrings;
        private readonly string _sourcePath;

        private XlsxReader(string sourcePath, List<string> sheetNames, Dictionary<string, string> sheetPathByName, string[] sharedStrings)
        {
            _sourcePath = sourcePath;
            _sheetNames = sheetNames;
            _sheetPathByName = sheetPathByName;
            _sharedStrings = sharedStrings;
        }

        /// <summary>源文件路径（错误信息与诊断用）。</summary>
        public string SourcePath => _sourcePath;

        /// <summary>工作簿里的全部 Sheet 名（按工作簿内顺序）。</summary>
        public IReadOnlyList<string> SheetNames => _sheetNames;

        /// <summary>
        /// 打开一个 xlsx。包结构不符合 OOXML 最小要求时抛 <see cref="InvalidDataException"/>（信息里带文件名），
        /// 不允许"读不到就当空表"——空表会让所有一致性用例假绿。
        /// </summary>
        public static XlsxReader Open(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("xlsx 路径不能为空。", nameof(path));
            }
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    string.Concat("找不到 xlsx 文件：'", path, "'。数据表缺失时测试必须失败，不允许静默跳过。"), path);
            }

            using FileStream stream = File.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            string[] shared = ReadSharedStrings(zip);
            Dictionary<string, string> relTargets = ReadWorkbookRels(zip);
            (List<string> names, Dictionary<string, string> pathByName) = ReadWorkbook(zip, relTargets);
            return new XlsxReader(path, names, pathByName, shared);
        }

        /// <summary>是否存在该 Sheet。</summary>
        public bool HasSheet(string sheetName) => sheetName is not null && _sheetPathByName.ContainsKey(sheetName);

        /// <summary>
        /// 读一张 Sheet 成网格（<c>rows[r][c]</c>，行列都按 Excel 真实 1 起始坐标补零）。
        /// <para>越界列与空单元格都返回空串：四行表头规约（docs/rules/04 §5.2）规定空单元格 = 取默认值，
        /// 解析器不需要区分"没有这个格子"与"格子是空的"。</para>
        /// </summary>
        public string[][] ReadSheet(string sheetName)
        {
            if (string.IsNullOrEmpty(sheetName))
            {
                throw new ArgumentException("Sheet 名不能为空。", nameof(sheetName));
            }
            if (!_sheetPathByName.TryGetValue(sheetName, out string? sheetPath))
            {
                throw new InvalidDataException(string.Concat(
                    "xlsx '", _sourcePath, "' 中没有 Sheet '", sheetName, "'（现有：", string.Join(",", _sheetNames), "）。"));
            }

            using FileStream stream = File.OpenRead(_sourcePath);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? entry = zip.GetEntry(sheetPath);
            if (entry is null)
            {
                throw new InvalidDataException(string.Concat(
                    "xlsx '", _sourcePath, "' 的 Sheet '", sheetName, "' 指向的包内文件 '", sheetPath, "' 不存在。"));
            }

            using Stream xml = entry.Open();
            return ParseSheet(xml, sheetName);
        }

        // ─────────────────────────────────────────────────────────────
        // OOXML 解析
        // ─────────────────────────────────────────────────────────────

        private string[][] ParseSheet(Stream xml, string sheetName)
        {
            var rows = new List<string[]>();
            var rowCells = new List<KeyValuePair<int, string>>();
            int rowIndex = 0;

            using (XmlReader reader = XmlReader.Create(xml, ReaderSettings()))
            {
                reader.Read();
                while (!reader.EOF)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == MainNamespace)
                    {
                        switch (reader.LocalName)
                        {
                            case "row":
                                FlushRow(rows, ref rowIndex, rowCells);
                                rowIndex = ReadNonNegativeInt(reader.GetAttribute("r"), rows.Count);
                                rowCells.Clear();
                                break;

                            case "c":
                                // 单元格属性顺序不保证（OOXML 不要求），所以逐个取。
                                string? reference = reader.GetAttribute("r");
                                string? type = reader.GetAttribute("t");
                                int column = ColumnOf(reference, rowCells.Count);
                                string value = ReadCellValue(reader, type, sheetName);
                                rowCells.Add(new KeyValuePair<int, string>(column, value));
                                break;
                        }
                    }
                    reader.Read();
                }
            }

            FlushRow(rows, ref rowIndex, rowCells);
            return rows.ToArray();
        }

        /// <summary>读一个 <c>&lt;c&gt;</c> 的文本（读到它的结束标签为止）。</summary>
        private string ReadCellValue(XmlReader reader, string? type, string sheetName)
        {
            if (reader.IsEmptyElement)
            {
                return string.Empty;
            }

            string raw = string.Empty;
            bool isInline = string.Equals(type, "inlineStr", StringComparison.Ordinal);

            int depth = reader.Depth;
            reader.Read();
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == MainNamespace)
                {
                    if (reader.LocalName == "v")
                    {
                        raw = reader.ReadElementContentAsString();
                        continue;
                    }
                    if (isInline && reader.LocalName == "t")
                    {
                        raw = (raw.Length > 0 ? string.Concat(raw, reader.ReadElementContentAsString()) : reader.ReadElementContentAsString());
                        continue;
                    }
                }
                reader.Read();
            }

            if (!isInline && string.Equals(type, "s", StringComparison.Ordinal))
            {
                int index = -1;
                if (raw.Length > 0 && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                {
                    index = parsed;
                }
                if (index < 0 || index >= _sharedStrings.Length)
                {
                    throw new InvalidDataException(string.Concat(
                        "xlsx '", _sourcePath, "' 的 Sheet '", sheetName, "' 引用了不存在的共享字符串下标 ", raw, "。"));
                }
                return _sharedStrings[index];
            }

            if (string.Equals(type, "b", StringComparison.Ordinal))
            {
                // 布尔格：xlsx 存 1/0，转成规约里允许的 TRUE/FALSE（docs/rules/04 §5.4）。
                return raw == "1" ? "TRUE" : "FALSE";
            }

            return raw;
        }

        private static void FlushRow(List<string[]> rows, ref int rowIndex, List<KeyValuePair<int, string>> cells)
        {
            if (cells.Count == 0 && rowIndex == 0)
            {
                return;
            }

            // 行号缺省时按"紧接上一行"补：Excel 对空行不写 <row> 节点，靠 r 定位。
            if (rowIndex <= rows.Count)
            {
                rowIndex = rows.Count + 1;
            }
            while (rows.Count < rowIndex - 1)
            {
                rows.Add(Array.Empty<string>());
            }

            int width = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].Key > width)
                {
                    width = cells[i].Key;
                }
            }
            var line = new string[width];
            for (int i = 0; i < width; i++)
            {
                line[i] = string.Empty;
            }
            for (int i = 0; i < cells.Count; i++)
            {
                line[cells[i].Key - 1] = cells[i].Value;
            }
            rows.Add(line);
            cells.Clear();
            rowIndex = 0;
        }

        private static int ReadNonNegativeInt(string? text, int fallback) =>
            text is not null && int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback + 1;

        /// <summary>把 <c>B7</c> 这类地址里的列字母段翻成 1 起始列号；地址缺失时按出现顺序推定。</summary>
        private static int ColumnOf(string? reference, int fallbackIndex)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return fallbackIndex + 1;
            }

            int column = 0;
            int digits = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                char c = reference[i];
                if (c >= 'A' && c <= 'Z')
                {
                    column = column * ColumnBase + (c - 'A' + 1);
                    digits++;
                }
                else if (c >= 'a' && c <= 'z')
                {
                    column = column * ColumnBase + (c - 'a' + 1);
                    digits++;
                }
                else
                {
                    break;
                }
            }
            return digits == 0 ? fallbackIndex + 1 : column;
        }

        private static string[] ReadSharedStrings(ZipArchive zip)
        {
            ZipArchiveEntry? entry = zip.GetEntry(SharedStringsPath);
            if (entry is null)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            using Stream stream = entry.Open();
            using XmlReader reader = XmlReader.Create(stream, ReaderSettings());
            var builder = new StringBuilder();
            bool inItem = false;

            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == MainNamespace)
                {
                    if (reader.LocalName == "si")
                    {
                        inItem = !reader.IsEmptyElement;
                        builder.Clear();
                        if (reader.IsEmptyElement)
                        {
                            values.Add(string.Empty);
                        }
                    }
                    else if (inItem && reader.LocalName == "t")
                    {
                        // 富文本 <r><t> 分段：直接接起来（表头/Id 不需要富文本，但字典里若有也不该丢字）。
                        builder.Append(reader.ReadElementContentAsString());
                        continue;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == MainNamespace && reader.LocalName == "si")
                {
                    values.Add(builder.ToString());
                    builder.Clear();
                    inItem = false;
                }
                reader.Read();
            }

            return values.ToArray();
        }

        private static Dictionary<string, string> ReadWorkbookRels(ZipArchive zip)
        {
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            ZipArchiveEntry? entry = zip.GetEntry(WorkbookRelsPath);
            if (entry is null)
            {
                throw new InvalidDataException("xlsx 缺少 xl/_rels/workbook.xml.rels，不是有效的 OOXML 工作簿。");
            }

            using Stream stream = entry.Open();
            using XmlReader reader = XmlReader.Create(stream, ReaderSettings());
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "Relationship"
                    && reader.NamespaceURI == "http://schemas.openxmlformats.org/package/2006/relationships")
                {
                    string? id = reader.GetAttribute("Id");
                    string? target = reader.GetAttribute("Target");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                    {
                        targets[id] = NormalizeTarget(target);
                    }
                }
                reader.Read();
            }
            return targets;
        }

        private static (List<string> Names, Dictionary<string, string> PathByName) ReadWorkbook(
            ZipArchive zip, Dictionary<string, string> relTargets)
        {
            ZipArchiveEntry? entry = zip.GetEntry(WorkbookPath);
            if (entry is null)
            {
                throw new InvalidDataException("xlsx 缺少 xl/workbook.xml，不是有效的 OOXML 工作簿。");
            }

            var names = new List<string>();
            var pathByName = new Dictionary<string, string>(StringComparer.Ordinal);
            using Stream stream = entry.Open();
            using XmlReader reader = XmlReader.Create(stream, ReaderSettings());
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == MainNamespace && reader.LocalName == "sheet")
                {
                    string? name = reader.GetAttribute("name");
                    string? relId = reader.GetAttribute("id", RelNamespace);
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                        if (relId is not null && relTargets.TryGetValue(relId, out string? path))
                        {
                            pathByName[name] = path;
                        }
                    }
                }
                reader.Read();
            }
            return (names, pathByName);
        }

        /// <summary>关系里的 Target 可能是相对路径（worksheets/sheet1.xml）也可能以 <c>/xl/</c> 开头。</summary>
        private static string NormalizeTarget(string target)
        {
            string path = target.Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal))
            {
                return path.Substring(1);
            }
            return path.StartsWith("xl/", StringComparison.Ordinal) ? path : string.Concat("xl/", path);
        }

        private static XmlReaderSettings ReaderSettings() => new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
        };
    }
}
