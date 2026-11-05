using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BiuBiuOrigin.Tests.Layering
{
    /// <summary>
    /// 分层守卫（Lead 追加的四条）：用**源码文本扫描**实现，不引用 Godot 类型，
    /// 所以它和 <see cref="GameCoreLayeringTests"/> 一样能在无引擎进程里跑。
    ///
    /// <para>
    /// <b>为什么是文本扫描而不是反射</b>：这些规矩约束的是「谁可以 <c>using</c> 谁」，
    /// 而没有被引用的 <c>using</c> 指令在运行时反射里完全不可见 —— 恰恰是这种残留会把
    /// 依赖方向悄悄带偏。同理，<c>World.Create</c> 这类"调用禁令"也必须在源码里看到。
    /// </para>
    ///
    /// <para>
    /// <b>扫描器规则</b>：每行先剥掉 <c>//</c> 与 <c>/* */</c> 注释（并正确跳过字符串/字符字面量里的
    /// <c>//</c>，例如 <c>"http://x"</c>），再匹配。注释里提到这些名字不算违规 —— 说明文字
    /// 常常需要举反例（本文件自己就是）。
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class LayerDependencyTests
    {
        private const string SourcePattern = "*.cs";

        /// <summary>业务层命名空间根（Core 之外的玩法系统族 + 数据层）。</summary>
        private static readonly string[] BusinessNamespaceRoots =
        {
            "Game.Actor",
            "Game.Combat",
            "Game.Skill",
            "Game.Spawn",
            "Game.Data",
            "Game.Host",
        };

        // ==================================================================
        // 守卫 1：scripts/ui/** 不得 using 任何业务层
        // ==================================================================

        [TestMethod]
        public void UiSources_DoNotUseBusinessLayers()
        {
            // UI 只做展示：读数据、发事件。不得依赖 Actor/Combat/Skill 的结算逻辑，
            // 也不得依赖 Host 的管线（AGENTS.md 红线 5）。
            // 注意：Game.Core 是允许的 —— UI 要读组件定义与数值视图接口。
            AssertNoUsing(scriptsSubDir: "ui", forbiddenRoots: BusinessNamespaceRoots,
                rule: "scripts/ui/** 只做展示，不得 using 业务层（AGENTS.md 红线 5 / 规则1）");
        }

        // ==================================================================
        // 守卫 2：scripts/data/** 不得 using 上层系统族
        // ==================================================================

        [TestMethod]
        public void DataSources_DoNotUseActorCombatSkill()
        {
            // 数据层是被依赖方：它描述"角色长什么样、装了什么"，不知道有没有人跑它。
            // 反过来依赖 Actor/Combat/Skill = 架构反转。
            AssertNoUsing(scriptsSubDir: "data",
                forbiddenRoots: new[] { "Game.Actor", "Game.Combat", "Game.Skill", "Game.Spawn", "Game.Host" },
                rule: "scripts/data/** 是被依赖方，不得反向 using Actor/Combat/Skill/Spawn/Host");
        }

        // ==================================================================
        // 守卫 3：scripts/core/** 不得 using 任何上层
        // ==================================================================

        [TestMethod]
        public void CoreSources_DoNotUseDataHostActorCombatSkill()
        {
            // Game.Core 是最底层：它只知道 ECS 内核与代数。数据层、宿主、各系统族都在它之上。
            AssertNoUsing(scriptsSubDir: "core",
                forbiddenRoots: new[] { "Game.Data", "Game.Host", "Game.Actor", "Game.Combat", "Game.Skill", "Game.Spawn" },
                rule: "Game.Core 是底层，不得 using 任何上层命名空间（ECS.md J4 / §10）");
        }

        // ==================================================================
        // 守卫 4：scripts/ui|actor|skill|combat 不得 using Godot
        // ==================================================================

        [TestMethod]
        [DataRow("actor")]
        [DataRow("skill")]
        [DataRow("combat")]
        [DataRow("data")]
        [DataRow("spawn")]
        public void NonHostSources_DoNotUseGodot(string subDir)
        {
            // 引擎边界只有 scripts/host/**（ECS.md §10）。其余**逻辑**目录必须是纯 .NET，
            // 否则测试就跑不进无引擎进程，且"逻辑与渲染分离"当场破功。
            //
            // 注意 ui 不在本列表里：HUD 是用 Godot 的 Control/CanvasItem 画出来的，它**必须**
            // using Godot —— UI 属于表现层，和 host 一样在引擎边界之内。它的约束是"不得 using 业务层"
            // （见 UiSources_DoNotUseBusinessLayers），不是"不得 using Godot"。
            AssertNoGodot(scriptsSubDir: subDir,
                rule: "scripts/" + subDir + "/** 不得 using Godot（引擎边界只允许 scripts/host/** 与 scripts/ui/**）");
        }

        // ==================================================================
        // 守卫 5：scripts/host/view/** 不得做结构变更（J5）
        // ==================================================================

        [TestMethod]
        public void HostViewSources_DoNotCreateOrDestroyEntities()
        {
            // 表现层可以 using 一切（它是引擎边界），但**不许做 ECS 结构变更**：
            // 生成/销毁实体必须走 CommandBuffer，由逻辑层在帧末统一提交（J5）。
            // 视图层直接 Create/Destroy 会让"实体生命周期"分散到两处，且绕过遍历期守卫。
            //
            // 判据：出现 World.Create / World.Destroy / World.Commands.CreateDeferred 等直接调用。
            // 注意 Commands.Destroy 是**允许**的（那是延迟提交的正道），所以只禁 World.* 上的结构方法。
            string dir = Path.Combine(RepoRoot(), "scripts", "host", "view");
            Assert.IsTrue(Directory.Exists(dir),
                "守卫失效：找不到 '" + dir + "'，无法校验 scripts/host/view 的结构变更禁令。");

            List<string> files = ListSources(dir);
            Assert.IsTrue(files.Count > 0,
                "守卫失效：'" + dir + "' 下没有 .cs 文件，无法证明视图层不做结构变更。");

            // 必须锚在地上是「World 类型的实例」。判据分两步：
            //   ① 接收者名字像世界：world / _world / World / Ecs / _ecs / 或以 World/Ecs 结尾（如 _hostWorld）；
            //   ② 或者接收者是 world 的成员链（_world.Commands 之类）——
            //      但 Commands.* 是允许的（延迟提交正道），所以成员链只认 World 本身上的方法。
            //
            // 为什么不能像第一版那样把所有 `.Create(`/`.Remove(` 都算：Dictionary.Remove、
            // List.Create 之类的普通集合操作会被误判成 ECS 结构变更（实打实地误报过一次
            // ActorViewDirector 的 _applied.Remove）。守卫误报比没守卫更糟——它会培养"放宽它"的习惯。
            var receiver = @"(?<![A-Za-z0-9_])(?:\w*[Ww]orld\w*|_?[Ee]cs)(?![A-Za-z0-9_])";
            var method = @"\.(Create|CreateDeferred|Destroy|Set|Remove)\s*[<(]";
            var pattern = new Regex(receiver + method, RegexOptions.Compiled);

            var violations = new StringBuilder();
            int count = 0;

            for (int f = 0; f < files.Count; f++)
            {
                string[] lines = ReadLines(files[f]);
                bool inBlock = false;
                for (int lineNo = 0; lineNo < lines.Length; lineNo++)
                {
                    string code = StripComments(lines[lineNo], ref inBlock);
                    if (code.Length == 0)
                    {
                        continue;
                    }

                    Match m = pattern.Match(code);
                    if (!m.Success)
                    {
                        continue;
                    }

                    count++;
                    if (count <= 20)
                    {
                        violations.Append("  ").Append(Relative(files[f], RepoRoot())).Append(':').Append(lineNo + 1)
                                  .Append(" -> ").Append(lines[lineNo].Trim()).Append('\n');
                    }
                }
            }

            Assert.AreEqual(0, count,
                "scripts/host/view/** 出现 " + count + " 处实体结构变更调用（J5 违规：表现层不许 Create/Destroy/Set/Remove）。" +
                "结构变更必须由逻辑层通过 CommandBuffer 在帧末提交。明细：\n" + violations);
        }

        // ==================================================================
        // 辅助
        // ==================================================================

        private static void AssertNoUsing(string scriptsSubDir, string[] forbiddenRoots, string rule)
        {
            string dir = Path.Combine(RepoRoot(), "scripts", scriptsSubDir);
            Assert.IsTrue(Directory.Exists(dir),
                "守卫失效：找不到 '" + dir + "'，无法校验 " + rule);

            List<string> files = ListSources(dir);
            Assert.IsTrue(files.Count > 0,
                "守卫失效：'" + dir + "' 下没有 .cs 文件，无法证明 " + rule);

            var violations = new StringBuilder();
            int count = 0;

            for (int f = 0; f < files.Count; f++)
            {
                string[] lines = ReadLines(files[f]);
                bool inBlock = false;
                for (int lineNo = 0; lineNo < lines.Length; lineNo++)
                {
                    string code = StripComments(lines[lineNo], ref inBlock);
                    string? matched = MatchedForbiddenUsing(code, forbiddenRoots);
                    if (matched is null)
                    {
                        continue;
                    }

                    count++;
                    if (count <= 20)
                    {
                        violations.Append("  ").Append(Relative(files[f], RepoRoot())).Append(':').Append(lineNo + 1)
                                  .Append(" -> ").Append(lines[lineNo].Trim())
                                  .Append("   (命中 ").Append(matched).Append(")\n");
                    }
                }
            }

            Assert.AreEqual(0, count, rule + "。发现 " + count + " 处违规 using：\n" + violations);
        }

        private static void AssertNoGodot(string scriptsSubDir, string rule)
        {
            string dir = Path.Combine(RepoRoot(), "scripts", scriptsSubDir);
            Assert.IsTrue(Directory.Exists(dir),
                "守卫失效：找不到 '" + dir + "'，无法校验 " + rule);

            List<string> files = ListSources(dir);
            Assert.IsTrue(files.Count > 0,
                "守卫失效：'" + dir + "' 下没有 .cs 文件，无法证明 " + rule);

            var usingGodot = new Regex(@"(?<![A-Za-z0-9_])Godot(?![A-Za-z0-9_])", RegexOptions.Compiled);
            var violations = new StringBuilder();
            int count = 0;

            for (int f = 0; f < files.Count; f++)
            {
                string[] lines = ReadLines(files[f]);
                bool inBlock = false;
                for (int lineNo = 0; lineNo < lines.Length; lineNo++)
                {
                    string code = StripComments(lines[lineNo], ref inBlock);
                    string trimmed = code.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    // 只判 using 指令（普通 / global / static / 别名）；Godot.* 的全限定引用
                    // 由"using 都不许"间接覆盖——写全限定名等于没 using，但仍然违规，
                    // 所以额外再判一次 using 之外的 Godot. 形式。
                    bool isUsing = trimmed.StartsWith("using ", StringComparison.Ordinal) ||
                                   trimmed.StartsWith("global using ", StringComparison.Ordinal);
                    if (!isUsing && !Regex.IsMatch(code, @"(?<![A-Za-z0-9_])Godot\.[A-Za-z_]"))
                    {
                        continue;
                    }

                    if (!usingGodot.IsMatch(code))
                    {
                        continue;
                    }

                    count++;
                    if (count <= 20)
                    {
                        violations.Append("  ").Append(Relative(files[f], RepoRoot())).Append(':').Append(lineNo + 1)
                                  .Append(" -> ").Append(lines[lineNo].Trim()).Append('\n');
                    }
                }
            }

            Assert.AreEqual(0, count, rule + "。发现 " + count + " 处：\n" + violations);
        }

        /// <summary>
        /// 判断这行是否是"被禁止的 using"，返回命中的命名空间根（未命中返回 null）。
        /// 支持：普通 using、global using、using static、using X = 别名。
        /// </summary>
        private static string? MatchedForbiddenUsing(string code, string[] forbiddenRoots)
        {
            string trimmed = code.Trim();
            bool isUsing = trimmed.StartsWith("using ", StringComparison.Ordinal) ||
                           trimmed.StartsWith("global using ", StringComparison.Ordinal);
            if (!isUsing)
            {
                return null;
            }

            // 别名：using Foo = Game.Actor.X; —— 只看等号右边。
            string target = trimmed;
            int eq = target.IndexOf('=');
            if (eq >= 0)
            {
                target = target.Substring(eq + 1);
            }

            for (int i = 0; i < forbiddenRoots.Length; i++)
            {
                string root = forbiddenRoots[i];
                // 必须是完整段前缀：Game.Actor 命中，Game.ActorsX 不命中。
                var re = new Regex(@"(?<![A-Za-z0-9_.])" + Regex.Escape(root) + @"(?![A-Za-z0-9_])");
                if (re.IsMatch(target))
                {
                    return root;
                }
            }

            return null;
        }

        private static List<string> ListSources(string dir)
        {
            var files = new List<string>(Directory.GetFiles(dir, SourcePattern, SearchOption.AllDirectories));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        private static string[] ReadLines(string path) =>
            File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');

        /// <summary>从测试程序集所在目录向上找仓库根（有 BiuBiuOrigin.csproj 的那层）。</summary>
        private static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "BiuBiuOrigin.csproj")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            Assert.Fail("守卫失效：从 '" + AppContext.BaseDirectory + "' 向上找不到 BiuBiuOrigin.csproj。");
            return string.Empty;
        }

        private static string Relative(string path, string root) =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart('\\', '/')
                : path;

        /// <summary>
        /// 剥掉一行的 // 行注释与 /* */ 块注释，跳过字符串/字符字面量里的伪注释。
        /// （与 <see cref="GameCoreLayeringTests"/> 同一实现，保持两个守卫对注释的判定一致。）
        /// </summary>
        private static string StripComments(string line, ref bool inBlockComment)
        {
            var sb = new StringBuilder(line.Length);
            int i = 0;
            bool inString = false;
            bool inChar = false;
            bool inVerbatimString = false;

            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    int close = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return sb.ToString();
                    }

                    inBlockComment = false;
                    i = close + 2;
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
    }
}
