using System;
using System.Linq;
using Xunit;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// 生成器注入的 [NativeTranspile] 属性源码与 Analyzer 侧枚举必须同步。
    /// 两边各有一份枚举（分析器不能引用用户编译里的类型），改名或加值只靠这条用例兜住。
    /// </summary>
    public class AttributeSurfaceTests
    {
        private static string EmittedAttributeSource()
        {
            var (_, _, run, _) = GeneratorHarness.RawRun("public class Empty { }");
            return run.GeneratedTrees
                .Single(tree => tree.FilePath.EndsWith("NativeTranspileAttribute.g.cs", StringComparison.Ordinal))
                .GetText()
                .ToString();
        }

        [Theory]
        [InlineData(typeof(global::NativeTranspiler.Analyzer.NativeTranspiler.BackendTarget))]
        [InlineData(typeof(global::NativeTranspiler.Analyzer.NativeTranspiler.IspcMathLib))]
        [InlineData(typeof(global::NativeTranspiler.Analyzer.NativeTranspiler.CppMathLib))]
        [InlineData(typeof(global::NativeTranspiler.Analyzer.NativeTranspiler.AutoSIMD))]
        [InlineData(typeof(global::NativeTranspiler.Analyzer.NativeTranspiler.SimdMathPrecision))]
        public void EmittedEnumsCarryEveryAnalyzerMember(Type enumType)
        {
            string source = EmittedAttributeSource();
            foreach (string member in Enum.GetNames(enumType))
            {
                // C# 关键字成员在发射文本里以 @ 前缀出现。
                string emitted = member == "default" ? "@default" : member;
                Assert.Contains(emitted, source);
            }
        }

        [Fact]
        public void EmittedAttributeExposesEverySupportedOption()
        {
            string source = EmittedAttributeSource();
            foreach (string property in new[] { "Target", "UseISPC_MT", "MathLib", "CppMathLib", "AutoSIMD", "MathPrecision" })
                Assert.Contains(property + " { get; set; }", source);
        }
    }
}
