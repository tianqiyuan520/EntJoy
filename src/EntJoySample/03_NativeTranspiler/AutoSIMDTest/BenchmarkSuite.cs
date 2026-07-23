using System.Diagnostics;
using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;

namespace EntJoySample.AutoSIMDTest
{
    /// <summary>单个 case 的基准测试结果</summary>
    public struct CaseResult
    {
        public string Name;
        public double CSharp_MS;      // IJobParallelFor C# 基线
        public double Cpp_MS;         // C++ 标量
        public double SIMD_MS;        // C++ Auto-SIMD (IJobFor)
        public double SIMD_IJob_MS;   // C++ Auto-SIMD (IJob)
        public double ISPC_MS;        // ISPC
        public bool ValidCSharp, ValidCpp, ValidSIMD, ValidSIMDIJob, ValidISPC;
    }

    /// <summary>统一基准测试框架：调度、计时、验证、报告</summary>
    public static class BenchmarkSuite
    {
        public const int DataSize = 100000;
        public const int Warmup = 3;
        public const int Iterations = 100;

        /// <summary>运行 IJobParallelFor 并返回平均耗时(ms)</summary>
        public static double RunJob<T>(ref T job, int count) where T : struct, IJobParallelFor
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                job.Execute(i);
            }
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds / count) * 1000; // μs → ms
        }

        /// <summary>运行 IJobFor 并返回平均耗时(ms)</summary>
        public static double RunJobFor<T>(ref T job, int count) where T : struct, IJobFor
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                job.Execute(i);
            }
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds / count) * 1000;
        }

        /// <summary>运行 IJob（单次调用）并返回耗时(ms)</summary>
        public static double RunJobSingle<T>(ref T job) where T : struct, IJob
        {
            var sw = Stopwatch.StartNew();
            job.Execute();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1000; // s → ms
        }

        /// <summary>运行 ISPC（调用 batch 函数）并返回平均耗时</summary>
        public static double RunISPC<T>(ref T job, int count) where T : struct, IJobParallelFor
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                job.Execute(i);
            sw.Stop();
            return (sw.Elapsed.TotalMilliseconds / count) * 1000;
        }

        public static double Measure(Action action, int warmup, int iterations)
        {
            for (int w = 0; w < warmup; w++)
                action();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                action();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / iterations;
        }

        public static void PrintHeader()
        {
            Console.WriteLine();
            Console.WriteLine("=".PadRight(58, '='));
            Console.WriteLine("  Auto-SIMD Benchmark Suite (C# 线下对比)");
            Console.WriteLine($"  数据量: {DataSize:N0}, 预热: {Warmup}, 迭代: {Iterations}");
            Console.WriteLine("  3 列均为 C# 直接执行（需 NativeCompileTask 构建链跑 C++/ISPC）");
            Console.WriteLine("=".PadRight(58, '='));
            Console.WriteLine();
            Console.WriteLine($"{"Case",-22} {"C# IJobPF(ms)",-14} {"IJobFor(ms)",-12} {"IJob(ms)",-10}");
            Console.WriteLine("-".PadRight(58, '-'));
        }

        public static void PrintRow(CaseResult r)
        {
            string csharp  = r.ValidCSharp  ? $"{r.CSharp_MS,7:F3}" : "    N/A";
            string simd    = r.ValidSIMD    ? $"{r.SIMD_MS,7:F3}" : "    N/A";
            string simdIJob = r.ValidSIMDIJob ? $"{r.SIMD_IJob_MS,7:F3}" : "    N/A";
            Console.WriteLine($"{r.Name,-22} {csharp,-14} {simd,-12} {simdIJob,-10}");
        }

        public static void PrintFooter()
        {
            Console.WriteLine("-".PadRight(58, '-'));
            Console.WriteLine("  ms = 毫秒 (越小越好)");
            Console.WriteLine();
        }
    }
}
