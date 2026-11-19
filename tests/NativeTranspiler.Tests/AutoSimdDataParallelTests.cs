using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>
/// AutoSIMD 的 IJob/静态方法路径：整段体就是一个逐元素循环时，把它摊到 SIMD 链路上
/// （si 批循环 + v_i 批内下标 + 标量尾巴），而不是退回标量翻译。
/// </summary>
public class AutoSimdDataParallelTests
{
    private static string Emit(string body) =>
        GeneratorHarness.EmitFor(@"using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct DataLoopJob : IJob {
    public NativeArray<float> A;
    public NativeArray<float> B;
    public NativeArray<float> Result;
    public int Count;
    public void Execute() {
        " + body + @"
    }
}").Cpp;

    [Fact]
    public void ElementwiseLoopUsesVectorBatchAndScalarTail()
    {
        var cpp = Emit("for (int i = 0; i < Count; i++) Result[i] = A[i] * B[i];");
        Assert.Contains("for (int si = __dp_begin; si < __simd_end; si += g_simdWidthInt)", cpp);
        Assert.Contains("n_load_ps(A_ptr + si)", cpp);
        Assert.Contains("n_load_ps(B_ptr + si)", cpp);
        Assert.Contains("n_store_ps(Result_ptr + si", cpp);
        Assert.DoesNotContain("gathf", cpp);
        Assert.Contains("for (int i = __simd_end; i < __dp_end; ++i)", cpp);
    }

    [Fact]
    public void CrossIterationDependencyKeepsScalarLoop()
    {
        // a[i] 读 a[i-1]：跨迭代依赖，摊到 SIMD 会算错。
        var cpp = Emit("for (int i = 1; i < Count; i++) A[i] = A[i - 1] + 1.0f;");
        Assert.DoesNotContain("__simd_end", cpp);
        Assert.Contains("for (int i = 1; i < Count;", cpp);
    }

    [Fact]
    public void ReductionToOuterScalarKeepsScalarLoop()
    {
        var cpp = Emit("float sum = 0.0f; for (int i = 0; i < Count; i++) sum += A[i]; Result[0] = sum;");
        Assert.DoesNotContain("__simd_end", cpp);
    }
}
