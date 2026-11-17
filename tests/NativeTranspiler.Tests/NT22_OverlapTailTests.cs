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
            Assert.Contains("__count < g_simdWidthInt && ", result.Cpp);
            Assert.Contains("__simd_exit_tail", result.Cpp);
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
