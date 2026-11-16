using System.Runtime.InteropServices;
using EntJoy.Collections;
using EntJoy.JobSystem;
using EntJoy.Mathematics;
using NativeTranspiler;

namespace NativeTranspilerFixture;

public struct PairField
{
    public uint2 Value;
    public uint Tag;
}

/// <summary>pair 元素差分基准：标量后端（Cpp）逐元素跑出的结果作为期望值。</summary>
[NativeTranspile]
public struct DifferentialPairCppJob : IJobParallelFor
{
    public NativeArray<uint2> Input, Output;
    public int Mode;
    public void Execute(int index)
    {
        uint2 a = Input[index];
        uint x = a.x, y = a.y;
        uint r = x * 3u + (y >> 1);
        if (Mode == 1) r = r / 7u;
        else if (Mode == 2 && x > 0x80000000u) r = x % 5u + 1u;
        Output[index] = new uint2(r, y ^ x);
    }
}

/// <summary>同一算式走 AutoSIMD：pair 走 int2 寄存器，分量除法/取模/移位走无符号 helper。</summary>
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct DifferentialPairSimdJob : IJobParallelFor
{
    public NativeArray<uint2> Input, Output;
    public int Mode;
    public void Execute(int index)
    {
        uint2 a = Input[index];
        uint x = a.x, y = a.y;
        uint r = x * 3u + (y >> 1);
        if (Mode == 1) r = r / 7u;
        else if (Mode == 2 && x > 0x80000000u) r = x % 5u + 1u;
        Output[index] = new uint2(r, y ^ x);
    }
}

/// <summary>结构体字段读取：走字段级 gather（含 uint2 字段与相邻标量字段）。</summary>
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct DifferentialPairFieldJob : IJobParallelFor
{
    public NativeArray<PairField> Items;
    public NativeArray<int> Output;
    public void Execute(int index)
    {
        PairField item = Items[index];
        uint2 v = item.Value;
        uint combined = (v.x ^ v.y) + item.Tag;
        if ((combined & 1u) == 0) combined += v.x / 7u;
        Output[index] = (int)combined;
    }
}

#if ENTJOY_FIXTURE_ISPC
/// <summary>同一算式走 ISPC 后端：验证 uint2 在 ISPC 侧的分量语义与 C++ 侧一致。</summary>
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct DifferentialPairIspcJob : IJobParallelFor
{
    public NativeArray<uint2> Input, Output;
    public int Mode;
    public void Execute(int index)
    {
        uint2 a = Input[index];
        uint x = a.x, y = a.y;
        uint r = x * 3u + (y >> 1);
        if (Mode == 1) r = r / 7u;
        else if (Mode == 2 && x > 0x80000000u) r = x % 5u + 1u;
        Output[index] = new uint2(r, y ^ x);
    }
}
#endif

/// <summary>float2 差分基准：标量后端逐元素跑出的结果作为期望值。</summary>
[NativeTranspile]
public struct DifferentialFloatCppJob : IJobParallelFor
{
    public NativeArray<float2> Input, Output;
    public int Mode;
    public void Execute(int index)
    {
        float2 a = Input[index];
        float r = a.x * 3f + a.y;
        if (Mode == 1) r = r / 7f;
        else if (Mode == 2 && a.x > 0f) r = a.x - a.y;
        Output[index] = new float2(r, a.y * 0.5f);
    }
}

/// <summary>同一算式走 AutoSIMD（float2 用 simd_value&lt;float2&gt; 承载）。</summary>
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct DifferentialFloatSimdJob : IJobParallelFor
{
    public NativeArray<float2> Input, Output;
    public int Mode;
    public void Execute(int index)
    {
        float2 a = Input[index];
        float r = a.x * 3f + a.y;
        if (Mode == 1) r = r / 7f;
        else if (Mode == 2 && a.x > 0f) r = a.x - a.y;
        Output[index] = new float2(r, a.y * 0.5f);
    }
}

#if ENTJOY_FIXTURE_ISPC
/// <summary>同一 float2 算式走 ISPC 后端，验证 pair 分量语义与 C++ 侧一致。</summary>
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct DifferentialFloatIspcJob : IJobParallelFor
{
    public NativeArray<float2> Input, Output;
    public int Mode;
    public void Execute(int index)
    {
        float2 a = Input[index];
        float r = a.x * 3f + a.y;
        if (Mode == 1) r = r / 7f;
        else if (Mode == 2 && a.x > 0f) r = a.x - a.y;
        Output[index] = new float2(r, a.y * 0.5f);
    }
}
#endif

public static unsafe class DifferentialPairFixture
{
    public static void Run()
    {
        nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "NativeTranspiled.dll"));
        int cases = 0, checks = 0;
        try
        {
            (int pairCases, int pairChecks) = RunPair(library);
            (int fieldCases, int fieldChecks) = RunPairField(library);
            (int floatCases, int floatChecks) = RunFloat(library);
            cases += pairCases + fieldCases + floatCases;
            checks += pairChecks + fieldChecks + floatChecks;
        }
        finally { NativeLibrary.Free(library); }
        Console.WriteLine($"PASS: differential pair cases={cases}, comparisons={checks}; uint2/float2 arithmetic and pair field gather match C#.");
    }

    private static (int cases, int checks) RunPair(nint library)
    {
        int cases = 0, checks = 0;
        uint[] seeds = { 0u, 1u, 3u, 7u, 0x7FFFFFFFu, 0x80000000u, 0xFFFFFFFEu, 0xFFFFFFFFu, 0x01000001u };
        foreach (int length in new[] { 0, 1, 7, 8, 9, 17, 33, 129 })
        {
            using var input = new NativeArray<uint2>(length);
            using var output = new NativeArray<uint2>(length);
            using var expected = new NativeArray<uint2>(length);
            var raw = (uint2*)input.GetUnsafePtr();
            for (int i = 0; i < length; i++)
                raw[i] = new uint2(seeds[i % seeds.Length], seeds[(i * 5 + 2) % seeds.Length]);

            foreach (int mode in new[] { 0, 1, 2 })
            {
                var reference = new DifferentialPairCppJob { Input = input, Output = expected, Mode = mode };
                for (int i = 0; i < length; i++) reference.Execute(i);

                foreach (int batch in new[] { 1, 7, 8, 9, 64 })
                {
                    CheckPairJob(new DifferentialPairSimdJob { Input = input, Output = output, Mode = mode });
#if ENTJOY_FIXTURE_ISPC
                    CheckPairJob(new DifferentialPairIspcJob { Input = input, Output = output, Mode = mode });
#endif
                    void CheckPairJob<T>(T job) where T : unmanaged
                    {
                        Reset(output);
                        var kernel = (delegate* unmanaged[Cdecl]<void*, int, int, void>)NativeLibrary.GetExport(
                            library, $"SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute_Adapter");
                        for (int start = 0; start < length; start += batch)
                            kernel(&job, start, Math.Min(batch, length - start));
                        for (int i = 0; i < length; i++)
                        {
                            uint2 actual = ((uint2*)output.GetUnsafePtr())[i];
                            uint2 want = ((uint2*)expected.GetUnsafePtr())[i];
                            if (actual != want)
                                throw new InvalidOperationException(
                                    $"{typeof(T).Name}: len={length} mode={mode} batch={batch} i={i} actual=({actual.x},{actual.y}) expected=({want.x},{want.y})");
                            checks++;
                        }
                        cases++;
                    }
                }
            }
        }
        return (cases, checks);
    }

    private static (int cases, int checks) RunPairField(nint library)
    {
        int cases = 0, checks = 0;
        foreach (int length in new[] { 1, 7, 8, 9, 17, 33 })
        {
            using var items = new NativeArray<PairField>(length);
            using var output = new NativeArray<int>(length);
            using var expected = new NativeArray<int>(length);
            var raw = (PairField*)items.GetUnsafePtr();
            for (int i = 0; i < length; i++)
                raw[i] = new PairField
                {
                    Value = new uint2((uint)(i * 37 + 1), (uint)(0xF0000000u + (uint)i)),
                    Tag = (uint)(i % 4)
                };

            // 期望值用托管侧同一算式算，避免依赖 Cpp 后端对结构体字段的写法。
            var reference = new DifferentialPairFieldJob { Items = items, Output = expected };
            using var itemsCopy = new NativeArray<PairField>(length);
            for (int i = 0; i < length; i++) ((PairField*)itemsCopy.GetUnsafePtr())[i] = raw[i];
            var referenceOnCopy = new DifferentialPairFieldJob { Items = itemsCopy, Output = expected };
            for (int i = 0; i < length; i++) referenceOnCopy.Execute(i);

            foreach (int batch in new[] { 1, 8, 9, 64 })
            {
                for (int i = 0; i < length; i++) ((int*)output.GetUnsafePtr())[i] = 0;
                var job = new DifferentialPairFieldJob { Items = items, Output = output };
                var kernel = (delegate* unmanaged[Cdecl]<void*, int, int, void>)NativeLibrary.GetExport(
                    library, "SharpNative_Job_NativeTranspilerFixture_DifferentialPairFieldJob_Execute_Adapter");
                for (int start = 0; start < length; start += batch)
                    kernel(&job, start, Math.Min(batch, length - start));

                for (int i = 0; i < length; i++)
                {
                    int actual = ((int*)output.GetUnsafePtr())[i];
                    int want = ((int*)expected.GetUnsafePtr())[i];
                    if (actual != want)
                        throw new InvalidOperationException(
                            $"DifferentialPairFieldJob: len={length} batch={batch} i={i} actual={actual} expected={want}");
                    checks++;
                }
                cases++;
            }
            _ = reference;
        }
        return (cases, checks);
    }

    private static (int cases, int checks) RunFloat(nint library)
    {
        int cases = 0, checks = 0;
        float[] seeds = { 0f, -0f, 1f, -1f, 3.5f, 1e-30f, 1e30f, float.MaxValue, float.MinValue };
        foreach (int length in new[] { 0, 1, 7, 8, 9, 17, 33, 129 })
        {
            using var input = new NativeArray<float2>(length);
            using var output = new NativeArray<float2>(length);
            using var expected = new NativeArray<float2>(length);
            var raw = (float2*)input.GetUnsafePtr();
            for (int i = 0; i < length; i++)
                raw[i] = new float2(seeds[i % seeds.Length], seeds[(i * 7 + 3) % seeds.Length]);

            foreach (int mode in new[] { 0, 1, 2 })
            {
                var reference = new DifferentialFloatCppJob { Input = input, Output = expected, Mode = mode };
                for (int i = 0; i < length; i++) reference.Execute(i);

                foreach (int batch in new[] { 1, 7, 8, 9, 64 })
                {
                    CheckFloatJob(new DifferentialFloatSimdJob { Input = input, Output = output, Mode = mode });
#if ENTJOY_FIXTURE_ISPC
                    CheckFloatJob(new DifferentialFloatIspcJob { Input = input, Output = output, Mode = mode });
#endif
                    void CheckFloatJob<T>(T job) where T : unmanaged
                    {
                        for (int i = 0; i < length; i++) ((float2*)output.GetUnsafePtr())[i] = new float2(0.5f, -0.5f);
                        var kernel = (delegate* unmanaged[Cdecl]<void*, int, int, void>)NativeLibrary.GetExport(
                            library, $"SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute_Adapter");
                        for (int start = 0; start < length; start += batch)
                            kernel(&job, start, Math.Min(batch, length - start));
                        for (int i = 0; i < length; i++)
                        {
                            float2 actual = ((float2*)output.GetUnsafePtr())[i];
                            float2 want = ((float2*)expected.GetUnsafePtr())[i];
                            if (actual.x != want.x || actual.y != want.y)
                                throw new InvalidOperationException(
                                    $"{typeof(T).Name}: len={length} mode={mode} batch={batch} i={i} actual=({actual.x},{actual.y}) expected=({want.x},{want.y})");
                            checks++;
                        }
                        cases++;
                    }
                }
            }
        }
        return (cases, checks);
    }

    private static void Reset(NativeArray<uint2> output)
    {
        var raw = (uint2*)output.GetUnsafePtr();
        for (int i = 0; i < output.Length; i++) raw[i] = new uint2(0x5A5A5A5Au, 0xA5A5A5A5u);
    }
}
