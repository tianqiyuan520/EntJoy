//using EntJoy;
//using EntJoySample.GpuGridSearch;
//using EntJoySample.GpuIlgpu;
//using EntJoy.JobSystem;

//namespace EntJoySample.GpuFinalCompare
//{
//    /// <summary>
//    /// 统一最终对比入口（10）：一次运行覆盖三个负载 × 四个实现类别（C# / C++ / ISPC / GPU）。
//    ///   ① HeavyMove@1M + LightMove@1M —— GpuIlgpuBenchmark（C#单/C#多/C++/ISPC/ISPC MT/GPU 全列）
//    ///   ② GridSearch@100k/100k        —— GpuGridSearchBenchmark（C#单/C#多/ISPC/GPU；C++ 无独立查询符号）
//    /// 末尾打印一张最终比较表。NativeJobScheduler 生命周期在此一次性 init/shutdown
//    /// （08/09 的 Run 已改为不自管，见各自 Program 注释）。
//    /// </summary>
//    public static class Program
//    {
//        public static void Main()
//        {
//            NativeJobScheduler.Initialize();
//            NativeJobScheduler.PrewakeWorkersOnce();
//            try
//            {
//                using var benchMove = new GpuIlgpuBenchmark(verbose: false);
//                benchMove.Run();

//                using var benchGrid = new GpuGridSearchBenchmark(verbose: false);
//                benchGrid.Run();

//                PrintFinalTable(benchMove, benchGrid);
//            }
//            finally
//            {
//                NativeJobScheduler.Shutdown();
//            }
//        }

//        private static string F(double v) => v > 0 ? v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "  n/a";

//        private static void PrintFinalTable(GpuIlgpuBenchmark b8, GpuGridSearchBenchmark b9)
//        {
//            const string noCpp = "无 native 符号";
//            string Row(string name, string hm, string gs, string lm) =>
//                $"  {name,-24}{hm,-15}{gs,-17}{lm,-14}";

//            Console.WriteLine();
//            Console.WriteLine("===================== 最终对比（p50, ms, RTX 4060 Laptop） =====================");
//            Console.WriteLine("  GridSearch 列 = Query（每帧）；创建网格（一次性）见下方单独表");
//            Console.WriteLine(Row("实现", "HeavyMove@1M", "GridSearch Query", "LightMove@1M"));
//            Console.WriteLine(Row("C# 单线程", F(b8.HeavyCsSingleMs), F(b9.CpuSingleMs), F(b8.LightCsSingleMs)));
//            Console.WriteLine(Row("C# 多线程(Parallel.For)", F(b8.HeavyCsParallelMs), F(b9.CsParallelMs), F(b8.LightCsParallelMs)));
//            Console.WriteLine(Row("C++ (JobSystem)", F(b8.HeavyCppMs), noCpp, F(b8.LightCppMs)));
//            Console.WriteLine(Row("ISPC (JobSystem)", F(b8.HeavyIspcMs), F(b9.IspcMs), F(b8.LightIspcMs)));
//            Console.WriteLine(Row("GPU 常驻(无传输)", F(b8.HeavyGpuResidentMs), F(b9.GpuResidentKernelMs), F(b8.LightGpuResidentMs)));
//            Console.WriteLine(Row("GPU 往返(页锁定)", F(b8.HeavyGpuPlMs), F(b9.GpuRoundtripPlMs), F(b8.LightGpuPlMs)));

//            Console.WriteLine();
//            Console.WriteLine("----- GridSearch@100k 创建网格（一次性, p50, ms；重建才重传） -----");
//            Console.WriteLine($"  C# 单线程       : {F(b9.BuildCsSingleMs),8:F3}");
//            Console.WriteLine($"  C# 多线程       : {F(b9.BuildCsParallelMs),8:F3}");
//            Console.WriteLine($"  C++ (JobSystem) : {F(b9.BuildCppMs),8:F3}   (transpiled 构建 jobs: bbox/hash计数/前缀和/放置/fill)");
//            Console.WriteLine($"  ISPC            : 无独立 build 符号（构建 jobs 为 C++ transpile）");
//            Console.WriteLine($"  GPU (ILGPU)     : {F(b9.BuildGpuMs),8:F3}   (原子计数/分段前缀和/放置/fill kernels; positions 常驻上传不含; bbox 在 CPU 算, GPU 端归约是 Gate 2 方向)");
//            Console.WriteLine($"  注: 构建一次性; grid 常驻 GPU 后 Query 每帧仅 781KB↑ + 390KB↓（见 docs/13 §3）；GPU 构建时 grid 直接留在 GPU buffer，免重建上传");

//            Console.WriteLine();
//            Console.WriteLine("GPU 页锁定全往返 vs ISPC 加速比（>1 赢）：");
//            string Ratio(string name, double gpu, double ispc) =>
//                gpu > 0 && ispc > 0 ? $"  {name,-24}{ispc / gpu,10:F2}x   (GPU {gpu:F3} vs ISPC {ispc:F3} ms)" : $"  {name,-24}  n/a";
//            Console.WriteLine(Ratio("HeavyMove@1M", b8.HeavyGpuPlMs, b8.HeavyIspcMs));
//            Console.WriteLine(Ratio("GridSearch@100k", b9.GpuRoundtripPlMs, b9.IspcMs));
//            Console.WriteLine(Ratio("LightMove@1M", b8.LightGpuPlMs, b8.LightIspcMs));

//            Console.WriteLine();
//            Console.WriteLine("解读（本跑实测，数值随机器负载波动 ±10-30%，方向稳定）：");
//            double hmRatio = b8.HeavyGpuPlMs > 0 ? b8.HeavyIspcMs / b8.HeavyGpuPlMs : 0;
//            double gsRatio = b9.GpuRoundtripPlMs > 0 ? b9.IspcMs / b9.GpuRoundtripPlMs : 0;
//            double lmRatio = b8.LightGpuPlMs > 0 ? b8.LightIspcMs / b8.LightGpuPlMs : 0;
//            Console.WriteLine($"  · HeavyMove（计算密度中, 传输量大 8MB↑+8MB↓）：页锁定把传输压到链路峰值, GPU 页锁定往返 vs ISPC = {hmRatio:F2}x");
//            Console.WriteLine($"  · GridSearch（计算密度高, 传输小 0.8+0.4MB）：kernel > 传输, GPU 页锁定往返 vs ISPC = {gsRatio:F2}x（本跑负载偏高）");
//            Console.WriteLine($"  · LightMove（计算密度低, 传输主导）：全量往返物理过不了带宽墙, GPU {lmRatio:F2}x 输；唯一出路 = 常驻（{F(b8.LightGpuResidentMs)} vs ISPC {F(b8.LightIspcMs)}）");
//            Console.WriteLine("  · C++ 列 GridSearch 的 Query 无独立符号（ClosestPointJobPointer 仅 ISPC transpile）；创建网格有 C++（构建 jobs 默认 C++ transpile, 见上方建网格表）");
//            Console.WriteLine("  · 细节/parity/三拆见上方各 benchmark 完整输出；方法学见 docs/gpu/13 §2.4/§2.7");
//        }
//    }
//}
