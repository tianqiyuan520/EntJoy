using System.Runtime.InteropServices;
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

// Keep the source bodies identical: calling a shared helper would exercise a different SIMD lowering path.
[NativeTranspile]
public struct DifferentialCppJob : IJobParallelFor
{
    public NativeArray<int> Input, Output;
    public int Bias, Mode;
    public void Execute(int index)
    {
        int value = Input[index], result = Bias;
        int limit = (value & 7) + 1;
        for (int k = 0; k < limit; k++)
        {
            if (k == 2 && (value & 1) == 0) continue;
            result = unchecked(result * 33 + value + k);
            if (k > 3 && (value & 2) != 0) break;
        }
        if (Mode != 1 || (value & 7) != 3)
        {
            if (Mode == 2) result = ~result;
            Output[index] = result ^ (value << (index & 31));
        }
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct DifferentialSimdJob : IJobParallelFor
{
    public NativeArray<int> Input, Output;
    public int Bias, Mode;
    public void Execute(int index)
    {
        int value = Input[index], result = Bias;
        int limit = (value & 7) + 1;
        for (int k = 0; k < limit; k++)
        {
            if (k == 2 && (value & 1) == 0) continue;
            result = unchecked(result * 33 + value + k);
            if (k > 3 && (value & 2) != 0) break;
        }
        if (Mode != 1 || (value & 7) != 3)
        {
            if (Mode == 2) result = ~result;
            Output[index] = result ^ (value << (index & 31));
        }
    }
}

#if ENTJOY_FIXTURE_ISPC
[NativeTranspile(Target = BackendTarget.Ispc)]
public struct DifferentialIspcJob : IJobParallelFor
{
    public NativeArray<int> Input, Output;
    public int Bias, Mode;
    public void Execute(int index)
    {
        int value = Input[index], result = Bias;
        int limit = (value & 7) + 1;
        for (int k = 0; k < limit; k++)
        {
            if (k == 2 && (value & 1) == 0) continue;
            result = unchecked(result * 33 + value + k);
            if (k > 3 && (value & 2) != 0) break;
        }
        if (Mode != 1 || (value & 7) != 3)
        {
            if (Mode == 2) result = ~result;
            Output[index] = result ^ (value << (index & 31));
        }
    }
}
#endif

public static unsafe class DifferentialTranslationFixture
{
    public static void Run()
    {
        nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "NativeTranspiled.dll"));
        int checks = 0, cases = 0;
        try
        {
            foreach (int seed in new[] { 17, 7919 })
            foreach (int length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 127, 128, 129 })
            {
                const int inputOffset = 1, outputOffset = 3;
                using var inputStorage = new NativeArray<int>(length + 32);
                using var outputStorage = new NativeArray<int>(length + 32);
                var input = NativeArray<int>.CreateView((int*)inputStorage.GetUnsafePtr() + inputOffset, length);
                var output = NativeArray<int>.CreateView((int*)outputStorage.GetUnsafePtr() + outputOffset, length);
                var random = new Random(seed);
                int[] originalInput = new int[inputStorage.Length];
                for (int i = 0; i < originalInput.Length; i++)
                {
                    int value = (i % 6) switch
                    {
                        0 => int.MinValue, 1 => int.MaxValue, 2 => -1, 3 => 0, 4 => 1,
                        _ => (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1)
                    };
                    ((int*)inputStorage.GetUnsafePtr())[i] = originalInput[i] = value;
                }
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    int bias = seed * 1021;
                    ResetOutput();
                    var reference = new DifferentialCppJob { Input = input, Output = output, Bias = bias, Mode = mode };
                    for (int i = 0; i < length; i++) reference.Execute(i);
                    int[] expected = new ReadOnlySpan<int>(outputStorage.GetUnsafePtr(), outputStorage.Length).ToArray();
                    foreach (int batch in new[] { 1, 7, 8, 9, 64 })
                    for (int path = 0; path < 2; path++)
                    {
                        Check(new DifferentialCppJob { Input = input, Output = output, Bias = bias, Mode = mode });
                        Check(new DifferentialSimdJob { Input = input, Output = output, Bias = bias, Mode = mode });
#if ENTJOY_FIXTURE_ISPC
                        Check(new DifferentialIspcJob { Input = input, Output = output, Bias = bias, Mode = mode });
#endif
                        void Check<T>(T job) where T : unmanaged, IJobParallelFor
                        {
                            ResetOutput();
                            if (path == 0)
                            {
                                var kernel = (delegate* unmanaged[Cdecl]<void*, int, int, void>)NativeLibrary.GetExport(
                                    library, $"SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute_Adapter");
                                for (int start = 0; start < length; start += batch)
                                    kernel(&job, start, Math.Min(batch, length - start));
                            }
                            else
                            {
                                var handle = NativeJobScheduler.ScheduleParallelFor(ref job, length, batch);
                                NativeJobScheduler.Complete(ref handle);
                            }
                            for (int i = 0; i < expected.Length; i++)
                            {
                                int actual = ((int*)outputStorage.GetUnsafePtr())[i];
                                if (actual != expected[i])
                                    throw new Exception($"{typeof(T).Name}: seed={seed}, length={length}, mode={mode}, batch={batch}, path={path}, offset={i}, actual={actual}, expected={expected[i]}");
                                if (((int*)inputStorage.GetUnsafePtr())[i] != originalInput[i])
                                    throw new Exception($"{typeof(T).Name} modified its input or input guard at {i}.");
                                checks += 2;
                            }
                            cases++;
                        }
                    }
                }
                void ResetOutput()
                {
                    for (int i = 0; i < outputStorage.Length; i++)
                        ((int*)outputStorage.GetUnsafePtr())[i] = 0x5A5A5A5A ^ (i * 17);
                }
            }
            Console.WriteLine($"PASS: differential translation cases={cases}, comparisons={checks}; guards, integer edges, loops and exits match C#.");
        }
        finally { NativeLibrary.Free(library); }
    }
}
