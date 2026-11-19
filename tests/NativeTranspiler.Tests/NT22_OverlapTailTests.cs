using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>尾部处理：写入只依赖自身下标的体用一条与主循环重叠的向量迭代覆盖尾部（重算重叠 lane 得到同一结果）；
    /// 读改写同一个数组的体退回标量余数循环。</summary>
    public class NT22_OverlapTailTests
    {
        private readonly ITestOutputHelper _out;
        public NT22_OverlapTailTests(ITestOutputHelper output) => _out = output;

        private const string IndependentStore = @"
using System;
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct OverlapSourceJob : IJobParallelFor
{
    public NativeArray<float> Src;
    public NativeArray<float> Dst;
    public void Execute(int index)
    {
        Dst[index] = MathF.Sin(Src[index]);
    }
}";

        private const string ReadModifyWrite = @"
using System;
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct OverlapClampJob : IJobParallelFor
{
    public NativeArray<float> Value;
    public float Low;
    public void Execute(int index)
    {
        Value[index] = MathF.Max(Value[index], Low);
    }
}";

        [Fact]
        public void IndependentStore_GetsOverlapTail()
        {
            var result = GeneratorHarness.EmitFor(IndependentStore);
            _out.WriteLine(result.Cpp);

            Assert.Contains("total_end_ - g_simdWidthInt", result.Cpp);
            // 读写两个数组同址时不能走重叠尾部，否则重叠 lane 会读到已写回的值
            Assert.Contains("(const void*)Dst_ptr != (const void*)Src_ptr", result.Cpp);
            // 别名守卫生效时标量循环必须兜住尾部，所以窄条件是析取式
            Assert.Contains("(__count < g_simdWidthInt || !(", result.Cpp);
            Assert.Contains("__simd_exit_tail", result.Cpp);
        }

        private const string PointerAlias = @"
using System;
using NativeTranspiler;
using EntJoy.JobSystem;
using EntJoy.Collections;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct OverlapPointerJob : IJobParallelFor
{
    public NativeArray<float> Value;
    public void Execute(int index)
    {
        unsafe
        {
            float* p = (float*)Value.GetUnsafePtr();
            Value[index] = p[index] + 1f;
        }
    }
}";

        [Fact]
        public void PointerAliasRead_KeepsScalarTail()
        {
            var result = GeneratorHarness.EmitFor(PointerAlias);
            _out.WriteLine(result.Cpp);

            Assert.DoesNotContain("total_end_ - g_simdWidthInt", result.Cpp);
        }
        [Fact]
        public void ReadModifyWriteSameArray_KeepsScalarTail()
        {
            var result = GeneratorHarness.EmitFor(ReadModifyWrite);
            _out.WriteLine(result.Cpp);

            Assert.DoesNotContain("total_end_ - g_simdWidthInt", result.Cpp);
            Assert.Contains("for (int index = simd_end_;", result.Cpp);
        }
    }
}
