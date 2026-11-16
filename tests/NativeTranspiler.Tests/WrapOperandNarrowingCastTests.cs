using Xunit;
using Xunit.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;

namespace NativeTranspiler.Tests
{
    /// <summary>用户写的窄化转换作为回绕算术的操作数时，转换本身必须保留在加法/乘法之前。</summary>
    public class WrapOperandNarrowingCastTests
    {
        private readonly ITestOutputHelper _out;
        public WrapOperandNarrowingCastTests(ITestOutputHelper output) => _out = output;

        private static string Emit(string body)
        {
            string source = @"
using NativeTranspiler;
using EntJoy.Collections;

[NativeTranspile]
public struct NarrowCastJob : IJob
{
    public void Execute()
    {
" + body + @"
    }
}";
            var cpp = GeneratorHarness.EmitFor(source).Cpp;
            return cpp.Replace("\r\n", "\n");
        }

        [Fact]
        public void UserCastOfFloatOperand_IsNotSwallowedIntoWrap()
        {
            string cpp = Emit(@"
        float f = 1.5f;
        int k = 2;
        int r = (int)(f) * k;");
            _out.WriteLine(cpp);

            // (int)(f) 必须先截断再参与乘法：转换要留在回绕算术的操作数里。
            Assert.Contains("(unsigned)(int)(f)", cpp);
            Assert.DoesNotContain("(f * (unsigned)", cpp);
        }

        [Fact]
        public void UserCastOfLongProduct_KeepsTruncationBeforeAdd()
        {
            string cpp = Emit(@"
        long long a = 3L;
        long long b = 4L;
        int r = (int)(a * b) + 1;");
            _out.WriteLine(cpp);

            // (int)(a * b) 是一个 64 位乘法后截断，必须整体作为左操作数，不能退化成 int 加法。
            Assert.Contains("(unsigned)(int)((long long)", cpp);
        }
    }
}
