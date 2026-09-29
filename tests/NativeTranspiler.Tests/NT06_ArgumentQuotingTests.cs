using System;
using System.Collections.Generic;
using System.Text;
using NativeTranspiler.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-06（High）：MSBuild 任务拼命令行时**只会按空格加引号、且不做反斜杠转义**。
    ///
    /// 旧实现（<c>NativeCompileTask.RunProcessWithTimeout</c>）：
    /// <c>string.Join(" ", arguments.Select(a =&gt; a.Contains(' ') ? "\"" + a + "\"" : a))</c>。
    ///
    /// Windows 命令行（CRT / <c>CommandLineToArgvW</c>）的规则是：反斜杠只在**紧跟引号**时才有转义
    /// 含义，而 <c>\</c> 紧跟 <c>"</c> 会把它变成**字面引号**、闭合引号失效。仓库自带的实参恰好永远以
    /// <c>\</c> 结尾（<c>src/EntJoy.Jobs/msbuild/EntJoy.Jobs.targets</c>：
    /// <c>NativeCodeGenDir="$(MSBuildProjectDirectory)\NativeTranspiler_Generated\"</c>）
    /// ⇒ 只要项目路径含空格，<c>cmake -S "…\" -B …</c> 就整体错位（不是一个能"跑错"的命令行，
    /// 而是参数被吞）。
    /// </summary>
    public class NT06_ArgumentQuotingTests
    {
        private readonly ITestOutputHelper _out;
        public NT06_ArgumentQuotingTests(ITestOutputHelper output) => _out = output;

        /// <summary>仓库自带的实参形态（targets 里硬编码，永远以 <c>\</c> 结尾）。</summary>
        private const string RepoGenDir = @"E:\My Projects\Game\NativeTranspiler_Generated\";

        /// <summary>
        /// 按 Windows CRT / <c>CommandLineToArgvW</c> 的核心规则切分命令行：
        /// 2n 个反斜杠 + 引号 ⇒ n 个字面反斜杠 + 引号定界符；
        /// 2n+1 个反斜杠 + 引号 ⇒ n 个字面反斜杠 + 字面引号。
        /// </summary>
        private static List<string> SplitCommandLine(string cmdline)
        {
            var args = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false, hasArg = false;
            int i = 0;
            while (i < cmdline.Length)
            {
                char c = cmdline[i];
                if (c == '\\')
                {
                    int n = 0;
                    while (i < cmdline.Length && cmdline[i] == '\\') { n++; i++; }
                    if (i < cmdline.Length && cmdline[i] == '"')
                    {
                        cur.Append('\\', n / 2);
                        if (n % 2 == 1) { cur.Append('"'); i++; }   // 2n+1 ⇒ 字面引号
                        else { inQuotes = !inQuotes; i++; }          // 2n   ⇒ 定界引号
                    }
                    else cur.Append('\\', n);
                    hasArg = true;
                    continue;
                }
                if (c == '"') { inQuotes = !inQuotes; hasArg = true; i++; continue; }
                if ((c == ' ' || c == '\t') && !inQuotes)
                {
                    if (hasArg) { args.Add(cur.ToString()); cur.Clear(); hasArg = false; }
                    i++;
                    continue;
                }
                cur.Append(c);
                hasArg = true;
                i++;
            }
            if (hasArg) args.Add(cur.ToString());
            return args;
        }

        /// <summary>缺陷锚点：旧拼法下 `cmake -S "…\" -B …` 的参数被吞掉（不是一个合法命令行）。</summary>
        [Fact]
        public void DefectAnchor_NaiveQuotingSwallowsArguments()
        {
            string naive = $"-S \"{RepoGenDir}\" -B out";
            _out.WriteLine("naive cmdline: " + naive);
            var parsed = SplitCommandLine(naive);
            foreach (var a in parsed) _out.WriteLine("  arg: <" + a + ">");

            Assert.NotEqual(4, parsed.Count);        // 期望 -S / <dir> / -B / out
            Assert.NotEqual(RepoGenDir.TrimEnd('\\'), parsed[1]);
        }

        [Fact]
        public void PathWithSpaces_IsQuoted()
        {
            Assert.Equal("\"E:\\My Projects\\Game\"", CommandLineQuoting.QuoteArgument(@"E:\My Projects\Game"));
        }

        [Fact]
        public void PlainPath_IsQuotedToo()
        {
            // 无条件加引号：既省掉"靠空格猜"的启发式，也让含 & / ( / ; / TAB 的参数安全。
            Assert.Equal("\"C:\\Game\"", CommandLineQuoting.QuoteArgument(@"C:\Game"));
            Assert.Equal("\"-DCMAKE_BUILD_TYPE=Release\"", CommandLineQuoting.QuoteArgument("-DCMAKE_BUILD_TYPE=Release"));
        }

        [Fact]
        public void TrailingSeparator_IsStripped_SoClosingQuoteSurvives()
        {
            string q = CommandLineQuoting.QuoteArgument(RepoGenDir);
            _out.WriteLine("quoted: " + q);

            Assert.Equal("\"E:\\My Projects\\Game\\NativeTranspiler_Generated\"", q);
            Assert.Equal(@"E:\My Projects\Game\NativeTranspiler_Generated", SplitCommandLine(q)[0]);
        }

        [Fact]
        public void DriveRoot_KeepsItsSeparator_AndDoublesTheBackslash()
        {
            // 根路径不能剥成 `C:`（那会变成"当前目录"）⇒ 只能靠倍增反斜杠让闭合引号生效。
            string q = CommandLineQuoting.QuoteArgument(@"C:\");
            _out.WriteLine("quoted: " + q);

            Assert.Equal("\"C:\\\\\"", q);
            Assert.Equal(@"C:\", SplitCommandLine(q)[0]);
        }

        [Fact]
        public void EmbeddedQuote_IsRejectedLoudly()
        {
            // 内嵌引号无法安全表达（`\"` + 反斜杠倍增）⇒ 必须报错，而不是静默生成另一个参数。
            var ex = Assert.Throws<ArgumentException>(() => CommandLineQuoting.QuoteArgument("C:\\a\"b\\c"));
            _out.WriteLine(ex.Message);
            Assert.Contains("双引号", ex.Message);
        }

        [Fact]
        public void FullCommandLine_RoundTrips()
        {
            // 端到端：真实调用点的形状（configure + build 两处参数）必须逐参数还原。
            var arguments = new[]
            {
                "-S", RepoGenDir,
                "-B", @"E:\My Projects\Game\NativeTranspiler_Generated\build",
                "-T", "ClangCL",
                "--build", @"E:\My Projects\Game\NativeTranspiler_Generated\build",
                "--config", "Release", "--parallel", "--target", "NativeTranspiled",
            };
            string cmdline = "cmake " + string.Join(" ", Array.ConvertAll(arguments, CommandLineQuoting.QuoteArgument));
            _out.WriteLine(cmdline);

            var parsed = SplitCommandLine(cmdline);
            Assert.Equal(arguments.Length + 1, parsed.Count);
            Assert.Equal("cmake", parsed[0]);
            for (int i = 1; i < parsed.Count; i++)
                Assert.Equal(arguments[i - 1].TrimEnd('\\'), parsed[i]);
        }

        [Fact]
        public void EmptyAndNull_AreHandled()
        {
            Assert.Equal("\"\"", CommandLineQuoting.QuoteArgument(""));
            Assert.Throws<ArgumentNullException>(() => CommandLineQuoting.QuoteArgument(null));
        }
    }
}
