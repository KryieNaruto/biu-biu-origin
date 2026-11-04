using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Layering
{
    /// <summary>
    /// ECS.md §10「分层守卫」组：<c>Game.Core</c> 源码里 0 个 <c>using Godot</c>。
    ///
    /// <para>
    /// 为什么要读源码而不是反射：类型一旦被 <c>using</c> 进来，运行时反射看不到「没有被引用的
    /// using 指令」，而恰恰是这种残留会让下一个 agent 以为 core 层可以碰引擎。所以这里直接
    /// 扫描 <c>scripts/core/**</c> 的文本，并顺带把「被注释掉的 using」「字符串里的 using」
    /// 这类假阳性一起判清楚，保证守卫不会因为一段示例代码而失效。
    /// </para>
    ///
    /// <para>
    /// 另附第二条守卫，防的是「测试工程悄悄被 Godot 污染」：本测试程序集的引用闭包里不得出现
    /// GodotSharp。这条是 <see cref="BiuBiuOrigin.Tests.csproj"/> 不用 ProjectReference 的
    /// 直接校验 —— 一旦有人把它改回 ProjectReference，这里立刻失败。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class GameCoreLayeringTests
    {
        /// <summary>要从源码树里找的根目录名（相对仓库根）。</summary>
        private const string CoreDirName = "core";

        /// <summary>只认 Game.Core 的实体后缀：.cs。</summary>
        private const string SourcePattern = "*.cs";

        /// <summary>整行被 // 注释掉的行首可能长这样（含 XML 文档注释 ///）。</summary>
        private const string LineCommentPrefix = "//";

        /// <summary>块注释起止。</summary>
        private const string BlockCommentOpen = "/*";
        private const string BlockCommentClose = "*/";

        /// <summary>守卫要禁止的命名空间前缀（完整 token 匹配，GodotSharp 也会命中）。</summary>
        private const string ForbiddenNamespace = "Godot";

        /// <summary>Game.Core 的根命名空间；scripts/core/** 下的类型必须落在它或其子命名空间里。</summary>
        private const string CoreNamespaceRoot = "Game.Core";

        [TestMethod]
        public void GameCoreSources_ContainNoUsingGodot()
        {
            string coreDir = LocateCoreDirectory();
            List<string> files = new List<string>(Directory.GetFiles(coreDir, SourcePattern, SearchOption.AllDirectories));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            Assert.IsTrue(files.Count > 0,
                "守卫本身失效：在 '" + coreDir + "' 下没有找到任何 .cs 文件，无法证明 Game.Core 不依赖 Godot。");

            StringBuilder violations = new StringBuilder();
            int violationCount = 0;

            for (int i = 0; i < files.Count; i++)
            {
                string text = File.ReadAllText(files[i], Encoding.UTF8);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                bool inBlockComment = false;

                for (int lineNo = 0; lineNo < lines.Length; lineNo++)
                {
                    string code = StripComments(lines[lineNo], ref inBlockComment);
                    if (!ContainsUsingGodot(code))
                    {
                        continue;
                    }

                    violationCount++;
                    violations.Append("  ").Append(Relative(files[i], coreDir)).Append(':').Append(lineNo + 1)
                              .Append(" -> ").Append(lines[lineNo].Trim()).Append('\n');
                }
            }

            Assert.AreEqual(
                0,
                violationCount,
                "Game.Core 出现了 " + violationCount + " 处 using Godot（ECS.md J4 / 规则2 违规）。" +
                "引擎耦合只允许出现在宿主层 scripts/host/**；Game.Core 必须是纯 .NET BCL。违规明细：\n" +
                violations.ToString());
        }

        [TestMethod]
        public void GameCoreSources_ReferenceNoGodotTypeEvenWithQualifiedNames()
        {
            // 第二条独立守卫：允许 using 被剥掉但代码里写全限定名 Godot.Vector3 的情况。
            // 用「不在 using 行里的 Godot.」判据，这样 using 违规只会被上一条报一次，不会重复计数。
            string coreDir = LocateCoreDirectory();
            List<string> files = new List<string>(Directory.GetFiles(coreDir, SourcePattern, SearchOption.AllDirectories));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            StringBuilder violations = new StringBuilder();
            int violationCount = 0;

            for (int i = 0; i < files.Count; i++)
            {
                string text = File.ReadAllText(files[i], Encoding.UTF8);
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                bool inBlockComment = false;

                for (int lineNo = 0; lineNo < lines.Length; lineNo++)
                {
                    string code = StripComments(lines[lineNo], ref inBlockComment);
                    if (code.Length == 0)
                    {
                        continue;
                    }

                    // 跳过 using 指令：它已被上一条用例覆盖。
                    if (code.TrimStart().StartsWith("using ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Regex.IsMatch(code, @"(?<![A-Za-z0-9_])Godot\.[A-Za-z_]"))
                    {
                        violationCount++;
                        violations.Append("  ").Append(Relative(files[i], coreDir)).Append(':').Append(lineNo + 1)
                                  .Append(" -> ").Append(lines[lineNo].Trim()).Append('\n');
                    }
                }
            }

            Assert.AreEqual(
                0,
                violationCount,
                "Game.Core 里出现了 " + violationCount + " 处 Godot.* 全限定类型引用（ECS.md J4 违规）。明细：\n" +
                violations.ToString());
        }

        [TestMethod]
        public void TestAssembly_DoesNotReferenceGodotSharp()
        {
            // 测试必须能在没有 Godot 的情况下跑（ECS.md §10 硬约束）。
            // 一旦有人把 csproj 改回 ProjectReference 主工程，GodotSharp 会出现在依赖闭包里；
            // 本用例同时检查「已加载的程序集」与「元数据里的引用表」两处，前者可能因懒加载为空，
            // 后者是编译期真实引用，不会被懒加载骗过。
            StringBuilder found = new StringBuilder();
            int count = 0;

            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                if (IsGodotAssemblyName(loaded[i].GetName().Name))
                {
                    count++;
                    found.Append("  ").Append(loaded[i].GetName().Name).Append(" (loaded)").Append('\n');
                }
            }

            var referenced = typeof(GameCoreLayeringTests).Assembly.GetReferencedAssemblies();
            for (int i = 0; i < referenced.Length; i++)
            {
                if (IsGodotAssemblyName(referenced[i].Name))
                {
                    count++;
                    found.Append("  ").Append(referenced[i].Name).Append(" (referenced)").Append('\n');
                }
            }

            Assert.AreEqual(
                0,
                count,
                "测试程序集依赖了 Godot，说明测试工程不再与引擎解耦（ECS.md §10 要求 dotnet test 不依赖 Godot）。明细：\n" +
                found.ToString());
        }

        /// <summary>
        /// GodotSharp / GodotSharpEditor / Godot.SourceGenerators …… 统一以 "Godot" 开头，
        /// 用 Ordinal 前缀匹配；GodotSharp 的命名空间程序集名就是 "GodotSharp"。
        /// </summary>
        private static bool IsGodotAssemblyName(string? name)
        {
            return !string.IsNullOrEmpty(name) &&
                   name!.StartsWith(ForbiddenNamespace, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static string LocateCoreDirectory()
        {
            // 从测试程序集所在目录向上找仓库根（有 BiuBiuOrigin.csproj 的那层），再进 scripts/core。
            // 不依赖当前工作目录，dotnet test / IDE 里都能定位。
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "BiuBiuOrigin.csproj")))
                {
                    string core = Path.Combine(dir.FullName, "scripts", CoreDirName);
                    Assert.IsTrue(Directory.Exists(core),
                        "守卫失效：仓库根 '" + dir.FullName + "' 下没有 scripts/" + CoreDirName + "，无法校验分层。");
                    return core;
                }

                dir = dir.Parent;
            }

            Assert.Fail("守卫失效：从 '" + AppContext.BaseDirectory + "' 向上找不到 BiuBiuOrigin.csproj，无法定位仓库根。");
            return string.Empty;
        }

        private static string Relative(string path, string root)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(root.Length).TrimStart('\\', '/');
            }

            return path;
        }

        /// <summary>
        /// 把一行里的 // 行注释与 /* */ 块注释剥掉，返回纯代码部分。
        /// 专门处理字符串/字符字面量里出现的 "//" 与 "/*"，避免把 "http://x" 当注释截断。
        /// </summary>
        private static string StripComments(string line, ref bool inBlockComment)
        {
            StringBuilder sb = new StringBuilder(line.Length);
            int i = 0;
            bool inString = false;
            bool inChar = false;
            bool inVerbatimString = false;

            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    int close = line.IndexOf(BlockCommentClose, i, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return sb.ToString();
                    }

                    inBlockComment = false;
                    i = close + BlockCommentClose.Length;
                    sb.Append(' ');
                    continue;
                }

                char c = line[i];

                if (inVerbatimString)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        inVerbatimString = false;
                    }

                    i++;
                    continue;
                }

                if (inString)
                {
                    if (c == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                    }

                    i++;
                    continue;
                }

                if (inChar)
                {
                    if (c == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '\'')
                    {
                        inChar = false;
                    }

                    i++;
                    continue;
                }

                if (c == '"')
                {
                    // @"..." verbatim string 或 "..." regular string
                    if (i > 0 && line[i - 1] == '@')
                    {
                        inVerbatimString = true;
                    }
                    else
                    {
                        inString = true;
                    }

                    i++;
                    continue;
                }

                if (c == '\'')
                {
                    inChar = true;
                    i++;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    return sb.ToString();
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        [TestMethod]
        public void CompiledGameCoreSources_AreNotEmpty()
        {
            // 防「0 个文件却测试全绿」的假阳性：测试工程靠 csproj 里的
            // <Compile Include="..\scripts\core\**\*.cs"> 把无引擎源码编进来。glob 一旦写错
            // （路径挪了、通配符变了），其余用例可能全部靠读源码树通过，而真正被编译的
            // 程序集里其实一个 Game.Core 类型都没有 —— 那种情况下「通过」毫无意义。
            //
            // 判据：本程序集里，注明来自 Game.Core 命名空间（Game.Core 或 Game.Core.*）的公开类型的
            // 数量必须 > 0，且其源码文件必须落在 scripts/core 下。
            Assembly asm = typeof(GameCoreLayeringTests).Assembly;
            Type[] exported = asm.GetExportedTypes();

            int coreTypes = 0;
            StringBuilder sample = new StringBuilder();
            for (int i = 0; i < exported.Length; i++)
            {
                string ns = exported[i].Namespace ?? string.Empty;
                if (ns == CoreNamespaceRoot ||
                    ns.StartsWith(CoreNamespaceRoot + ".", StringComparison.Ordinal))
                {
                    coreTypes++;
                    if (coreTypes <= 5)
                    {
                        sample.Append("  ").Append(exported[i].FullName).Append('\n');
                    }
                }
            }

            Assert.IsTrue(coreTypes > 0,
                "假阳性守卫：测试程序集里没有任何 Game.Core.* 类型，说明 csproj 的 " +
                "<Compile Include=\"..\\scripts\\core\\**\\*.cs\"> 没有编进任何源码。" +
                "此时其余用例的『通过』不能证明任何事。检查 tests/BiuBiuOrigin.Tests.csproj 的 CoreSourceGlob。");

            // 再验一层：源码树里的**每一个** .cs 文件都必须贡献了至少一个 Game.Core 类型。
            // 为什么按文件而不是按类型名对文件：项目既有风格是一个文件里放 `enum X` + `static class XExtensions`
            // （见 StatId.cs / DamageType.cs），按类型名一对一比对会误判；而「文件被编进来了没有」
            // 才是 glob 写错时真正会变的东西。
            //
            // 判据：编译器会把每个类型标记它来自哪个文件 —— 这里改用更稳的方式，
            // 用 PDB 里的文档列表（本程序集以 Debug 编译，PDB 一定存在且同目录）。
            HashSet<string> compiledFiles = ReadCompiledSourceFiles();
            Assert.IsTrue(compiledFiles.Count > 0,
                "漂移守卫失效：读不到 PDB 里的源文件列表，无法判断 glob 是否编全。");

            string coreDir = LocateCoreDirectory();
            string[] coreFiles = Directory.GetFiles(coreDir, SourcePattern, SearchOption.AllDirectories);

            StringBuilder missing = new StringBuilder();
            int missingCount = 0;
            for (int i = 0; i < coreFiles.Length; i++)
            {
                string fileName = Path.GetFileName(coreFiles[i]);
                bool present = false;
                foreach (string compiled in compiledFiles)
                {
                    if (string.Equals(Path.GetFileName(compiled), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                {
                    missingCount++;
                    if (missingCount <= 10)
                    {
                        missing.Append("  ").Append(Relative(coreFiles[i], coreDir)).Append('\n');
                    }
                }
            }

            Assert.AreEqual(
                0,
                missingCount,
                "漂移守卫：scripts/core 下这 " + missingCount + " 个 .cs 文件**没有**被编进测试程序集，" +
                "说明 csproj 的 CoreSourceGlob 与源码树不一致（漏编的文件里的逻辑不会被任何用例覆盖）。明细：\n" +
                missing.ToString());

            Console.WriteLine("[layering] Game.Core 类型已编入测试程序集，数量 = " + coreTypes +
                              "；scripts/core 源码文件数 = " + coreFiles.Length);
        }

        /// <summary>
        /// 从本程序集的 PDB 里读出「被编译进程序集的源文件」清单（绝对路径）。
        /// <para>
        /// 为什么不直接用反射逐类型找 SourceFile：C# 的源文件信息只存在于调试符号里，
        /// 而 PDB 的文档表覆盖**所有**被编译的文件（哪怕里面只有 internal 类型）。
        /// 这是判断「glob 编全了没有」最直接、且不受类型命名风格影响的证据。
        /// </para>
        /// </summary>
        private static HashSet<string> ReadCompiledSourceFiles()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string assemblyPath = typeof(GameCoreLayeringTests).Assembly.Location;
            if (string.IsNullOrEmpty(assemblyPath))
            {
                return result;
            }

            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdbPath))
            {
                return result;
            }

            using FileStream stream = File.OpenRead(pdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            MetadataReader reader = provider.GetMetadataReader();
            foreach (DocumentHandle handle in reader.Documents)
            {
                Document document = reader.GetDocument(handle);
                string? name = reader.GetString(document.Name);
                if (!string.IsNullOrEmpty(name))
                {
                    result.Add(name!);
                }
            }

            return result;
        }

        /// <summary>
        /// 判定一行代码是否是 <c>using Godot</c> / <c>using Godot.X</c> / 别名写法
        /// <c>using V = Godot.Vector3</c> / 静态写法 <c>using static Godot.GD</c>。
        /// 「GodotSharp」这类更长的标识符会被 <c>[^A-Za-z0-9_]</c> 边界挡掉一半，
        /// 这里用显式状态机，保证只有命名空间段恰好是 Godot 才算。
        /// </summary>
        private static bool ContainsUsingGodot(string code)
        {
            string trimmed = code.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            // global using / using static / using X = ... / 普通 using
            if (!trimmed.StartsWith("using ", StringComparison.Ordinal) &&
                !trimmed.StartsWith("global using ", StringComparison.Ordinal))
            {
                return false;
            }

            // 取 using 之后的目标，支持 '=' 别名；任何位置出现 Godot 段都算违规。
            string target = trimmed;
            int eq = target.IndexOf('=');
            if (eq >= 0)
            {
                target = target.Substring(eq + 1);
            }

            return Regex.IsMatch(target, @"(?<![A-Za-z0-9_])Godot(?![A-Za-z0-9_])");
        }
    }
}
