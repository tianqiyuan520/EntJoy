using System.Runtime.InteropServices;
using EntJoy.Collections;
using EntJoy.Mathematics;

namespace NativeTranspilerFixture;

/// <summary>uint2 SIMD 算术的真机比对：原生产物与托管 Execute 逐元素一致。</summary>
public static unsafe class Uint2SimdFixture
{
    public static void Run()
    {
        nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "NativeTranspiled.dll"));
        int checks = 0;
        try
        {
            // 无符号语义最容易错的位置：符号位、最大/次大、非 2 的幂分界。
            uint[] seeds = { 0u, 1u, 3u, 7u, 0x80000000u, 0xFFFFFFFEu, 0xFFFFFFFFu, 0x01000001u };
            foreach (int length in new[] { 1, 7, 8, 9, 17, 129 })
            {
                using var input = new NativeArray<uint2>(length, Allocator.Persistent);
                using var output = new NativeArray<int>(length, Allocator.Persistent);
                using var managed = new NativeArray<int>(length, Allocator.Persistent);

                var raw = (uint2*)input.GetUnsafePtr();
                for (int i = 0; i < length; i++)
                    raw[i] = new uint2(seeds[i % seeds.Length], seeds[(i * 3 + 1) % seeds.Length]);

                var job = new Uint2SimdJob { Input = input, Output = output };
                var reference = new Uint2SimdJob { Input = input, Output = managed };
                for (int i = 0; i < length; i++) reference.Execute(i);

                foreach (int chunk in new[] { 1, 7, 8, 13, 127 })
                {
                    for (int i = 0; i < length; i++) ((int*)output.GetUnsafePtr())[i] = 0;
                    RunRange(library, ref job, length, chunk);
                    for (int i = 0; i < length; i++)
                    {
                        int actual = ((int*)output.GetUnsafePtr())[i];
                        int expected = ((int*)managed.GetUnsafePtr())[i];
                        if (actual != expected)
                            throw new InvalidOperationException(
                                $"uint2 SIMD arithmetic[{i}] len={length} chunk={chunk}: {actual} != {expected}");
                        checks++;
                    }
                }
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
        Console.WriteLine($"  [ok] uint2 SIMD arithmetic (mul/div/mod/shift): {checks} checks");
    }

    private static void RunRange<T>(nint library, ref T job, int length, int chunk) where T : unmanaged
    {
        var kernel = (delegate* unmanaged[Cdecl]<void*, int, int, void>)NativeLibrary.GetExport(
            library, $"SharpNative_Job_NativeTranspilerFixture_{typeof(T).Name}_Execute_Adapter");
        fixed (T* context = &job)
            for (int start = 0; start < length; start += chunk)
                kernel(context, start, Math.Min(chunk, length - start));
    }
}
