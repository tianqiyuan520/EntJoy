using System.Runtime.InteropServices;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace NativeTranspilerFixture;

public static unsafe class TranslationSemanticsFixture
{
    public static void Run()
    {
        nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "NativeTranspiled.dll"));
        int checks = 0;
        try
        {
            foreach (int length in new[] { 1, 7, 8, 9, 17, 129 })
            {
                using var input = new NativeArray<int>(length, Allocator.Persistent);
                using var output = new NativeArray<int>(length, Allocator.Persistent);
                using var visits = new NativeArray<int>(length, Allocator.Persistent);
                for (int i = 0; i < length; i++) ((int*)input.GetUnsafePtr())[i] = i * 3 + 11;

                foreach (int chunk in new[] { 1, 7, 8, 13, 127 })
                {
                    for (int i = 0; i < length; i++) ((int*)visits.GetUnsafePtr())[i] = -999;
                    var masked = new TranslationMaskedStoreJob { Input = input, Output = output, Visits = visits };
                    RunRange(library, ref masked, length, chunk);
                    for (int i = 0; i < length; i++)
                    {
                        Equal(output[i], (i & 1) == 0 ? input[i] : -1, "masked store");
                        Equal(visits[i], -999, "untouched sibling");
                        checks += 2;
                    }
                    var sibling = new TranslationSiblingStoreJob { Input = input, Output = output, Visits = visits };
                    RunRange(library, ref sibling, length, chunk);
                    for (int i = 0; i < length; i++)
                    {
                        Equal(output[i], input[i], "lookup store");
                        Equal(visits[i], (i & 1) == 0 ? 123 : 456, "sibling store");
                        checks += 2;
                    }
                    foreach (bool active in new[] { false, true })
                    {
                        var boolean = new TranslationBoolShadowJob { Output = output, Active = active };
                        RunRange(library, ref boolean, length, chunk);
                        for (int i = 0; i < length; i++)
                        {
                            int actual = output[i];
                            boolean.Execute(i);
                            Equal(actual, output[i], "bool shadow");
                            checks++;
                        }
                    }
                }
                var pointer = new TranslationPointerShadowJob { Output = output };
                RunRange(library, ref pointer, length, length);
                for (int i = 0; i < length; i++) { Equal(output[i], i + 7, "pointer shadow"); checks++; }
                var scalar = new TranslationScalarShadowJob { Output = output, Bias = 19 };
                RunRange(library, ref scalar, length, length);
                for (int i = 0; i < length; i++) { Equal(output[i], i + 21, "scalar shadow"); checks++; }
                var simdScalar = new TranslationSimdScalarShadowJob { Output = output, Bias = 19 };
                RunRange(library, ref simdScalar, length, length);
                for (int i = 0; i < length; i++) { Equal(output[i], i + 40, "SIMD scalar shadow"); checks++; }
            }
            checks += CheckArithmetic(library);
            checks += CheckLoops(library);
            checks += CheckMasks(library);
            Console.WriteLine($"[PASS] C++/AutoSIMD translation semantics: {checks} native comparisons");
        }
        finally { NativeLibrary.Free(library); }
    }

    private static int CheckArithmetic(nint library)
    {
        long[] edges = { long.MinValue, -1, 0, 1, long.MaxValue };
        using var values = new NativeArray<long>(edges.Length, Allocator.Persistent);
        using var output = new NativeArray<int>(edges.Length, Allocator.Persistent);
        for (int i = 0; i < edges.Length; i++) ((long*)values.GetUnsafePtr())[i] = edges[i];
        var wide = new TranslationWideArithmeticJob { Input = values, Output = output };
        RunRange(library, ref wide, edges.Length, edges.Length);
        for (int i = 0; i < edges.Length; i++)
        {
            int actual = output[i];
            wide.Execute(i);
            Equal(actual, output[i], "wide arithmetic");
        }
        var mixed = new TranslationMixedArithmeticJob { Output = values };
        RunRange(library, ref mixed, edges.Length, edges.Length);
        for (int i = 0; i < edges.Length; i++)
            if (values[i] != -4294967295L) throw new InvalidOperationException("Mixed int/uint promotion");

        for (int i = 0; i < output.Length; i++) ((int*)output.GetUnsafePtr())[i] = 0;
        var evaluation = new TranslationEvaluationJob { Output = output };
        var kernel = (delegate* unmanaged[Cdecl]<void*, void>)GetKernel<TranslationEvaluationJob>(library);
        kernel(&evaluation);
        Equal(output[0], 1, "first compound assignment");
        Equal(output[1], 2, "second compound assignment");
        Equal(output[2], 2, "lvalue evaluation count");
        return edges.Length * 2 + 3;
    }

    private static int CheckLoops(nint library)
    {
        int checks = 0;
        const int length = 33;
        using var starts = new NativeArray<int>(length, Allocator.Persistent);
        using var ends = new NativeArray<int>(length, Allocator.Persistent);
        using var output = new NativeArray<int>(length, Allocator.Persistent);
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (int chunk in new[] { 1, 7, 8, 13, 33 })
        {
            for (int i = 0; i < length; i++)
            {
                ((int*)starts.GetUnsafePtr())[i] = mode == 0 ? int.MinValue + i % 2 : i % 2 == 0 ? int.MinValue + 16 : int.MaxValue - 10;
                ((int*)ends.GetUnsafePtr())[i] = mode == 0 ? i + 1 : starts[i] + i % 7 - 1;
            }
            var job = new TranslationReductionBoundsJob { Starts = starts, Ends = ends, Output = output, Mode = mode };
            RunRange(library, ref job, length, chunk);
            for (int i = 0; i < length; i++)
            {
                int actual = output[i];
                job.Execute(i);
                Equal(actual, output[i], $"reduction mode={mode}, chunk={chunk}, index={i}");
                checks++;
            }
        }
        return checks;
    }

    private static int CheckMasks(nint library)
    {
        const int length = 33;
        int checks = 0, failures = 0;
        float[] edges = { float.NaN, float.NegativeInfinity, float.PositiveInfinity, -1f, 0f, 1f, 2f,
            BitConverter.Int32BitsToSingle(int.MinValue) };
        using var input = new NativeArray<float>(length, Allocator.Persistent);
        using var candidate = new NativeArray<float>(length, Allocator.Persistent);
        using var floatOutput = new NativeArray<float>(length, Allocator.Persistent);
        using var output = new NativeArray<int>(length, Allocator.Persistent);
        for (int i = 0; i < length; i++)
        {
            ((float*)input.GetUnsafePtr())[i] = edges[i % edges.Length];
            ((float*)candidate.GetUnsafePtr())[i] = edges[(i + 3) % edges.Length];
        }
        foreach (int chunk in new[] { 1, 7, 8, 13, 33 })
        {
            var chain = new TranslationBooleanChainJob { Output = output };
            RunRange(library, ref chain, length, chunk);
            for (int i = 0; i < length; i++)
            {
                int actual = output[i];
                chain.Execute(i);
                if (actual != output[i]) failures++;
                checks++;
            }
            var negation = new TranslationMaskNegationJob { Input = input, Output = output, Iterations = 3 };
            RunRange(library, ref negation, length, chunk);
            for (int i = 0; i < length; i++)
            {
                int actual = output[i];
                negation.Execute(i);
                if (actual != output[i]) failures++;
                checks++;
            }
            var selection = new TranslationConditionalSelectionJob { Input = input, Candidate = candidate, Output = floatOutput };
            RunRange(library, ref selection, length, chunk);
            for (int i = 0; i < length; i++)
            {
                int actualBits = BitConverter.SingleToInt32Bits(floatOutput[i]);
                selection.Execute(i);
                if (actualBits != BitConverter.SingleToInt32Bits(floatOutput[i])) failures++;
                checks++;
            }
        }
        foreach (bool first in new[] { false, true })
        foreach (bool second in new[] { false, true })
        {
            var variant = new TranslationVariantJob { Output = output, First = first, Second = second };
            for (int mode = 0; mode < 2; mode++)
            {
                if (mode == 0) variant.Run(length);
                else variant.Schedule(length, 7).Complete();
                for (int i = 0; i < length; i++)
                {
                    Equal(output[i], i + (first ? 1 : 0) + (second ? 2 : 0), "boolean variants Run/Schedule");
                    checks++;
                }
            }
        }
        if (failures != 0) throw new InvalidOperationException($"Mask/selection mismatches: {failures}/{checks}");
        return checks;
    }

    private static void RunRange<T>(nint library, ref T job, int length, int chunk) where T : unmanaged
    {
        var kernel = (delegate* unmanaged[Cdecl]<void*, int, int, void>)GetKernel<T>(library);
        fixed (T* context = &job)
            for (int start = 0; start < length; start += chunk)
                kernel(context, start, Math.Min(chunk, length - start));
    }

    private static nint GetKernel<T>(nint library)
        => NativeLibrary.GetExport(library, $"SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute_Adapter");

    private static void Equal(int actual, int expected, string context)
    {
        if (actual != expected) throw new InvalidOperationException($"{context}: {actual} != {expected}");
    }
}
