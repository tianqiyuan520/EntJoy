using EntJoy.Collections;
using EntJoy.Mathematics;
using EntJoy.JobSystem;
using System.Diagnostics;

namespace EntJoy.MovementTest
{
    /// <summary>
    /// 帧循环风格的 NativeArray 位移测试
    /// 每次运行 1 次迭代 → 等待 16ms → 再运行 1 次，共 100 帧
    /// 模拟 GameLoop 中每帧调度的实际表现
    /// 
    /// 正确性验证方式：每个实现在自己的数据副本上累加 100 帧，
    /// 然后与标量在独立副本上的 100 帧结果对比。
    /// 所有副本均从同一初始数据开始。
    /// </summary>
    public class MoveEntitiesFrameTest
    {
        private const int ENTITY_COUNT = 1_000_000;
        private const float VIEWPORT_WIDTH = 1920f;
        private const float VIEWPORT_HEIGHT = 1080f;
        private const float DT = 0.016f;
        private const int FRAMES = 100;
        private const int FRAME_INTERVAL_MS = 16;
        private const int WARMUP_FRAMES = 3;

        // 初始数据（种子相同，永不改变）
        private NativeArray<float2> _initialPositions;
        private NativeArray<float2> _initialVelocities;

        // 每种实现独立的数据副本
        private NativeArray<float2> _positionsScalar;
        private NativeArray<float2> _velocitiesScalar;
        private NativeArray<float2> _positionsParallel;
        private NativeArray<float2> _velocitiesParallel;
        private NativeArray<float2> _positionsJob;
        private NativeArray<float2> _velocitiesJob;
        private NativeArray<float2> _positionsNativeCpp;
        private NativeArray<float2> _velocitiesNativeCpp;
        private NativeArray<float2> _positionsNativeIspc;
        private NativeArray<float2> _velocitiesNativeIspc;
        private NativeArray<float2> _positionsNativeCppStatic;
        private NativeArray<float2> _velocitiesNativeCppStatic;
        private NativeArray<float2> _positionsNativeIspcStatic;
        private NativeArray<float2> _velocitiesNativeIspcStatic;

        // 专用 reference 副本：标量跑完后保存，不被 ResetAllToInitial 清空
        private NativeArray<float2> _frameReferencePositions;
        private NativeArray<float2> _frameReferenceVelocities;

        // 每帧耗时记录（ms）
        private double[] _scalarTimes = new double[FRAMES];
        private double[] _parallelTimes = new double[FRAMES];
        private double[] _jobTimes = new double[FRAMES];
        private double[] _nativeCppTimes = new double[FRAMES];
        private double[] _nativeIspcTimes = new double[FRAMES];
        private double[] _nativeCppStaticTimes = new double[FRAMES];
        private double[] _nativeIspcStaticTimes = new double[FRAMES];

        public MoveEntitiesFrameTest()
        {
            Allocator alloc = Allocator.Persistent;
            _initialPositions = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _initialVelocities = new NativeArray<float2>(ENTITY_COUNT, alloc);

            _positionsScalar = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesScalar = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _positionsParallel = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesParallel = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _positionsJob = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesJob = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _positionsNativeCpp = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesNativeCpp = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _positionsNativeIspc = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesNativeIspc = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _positionsNativeCppStatic = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesNativeCppStatic = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _positionsNativeIspcStatic = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesNativeIspcStatic = new NativeArray<float2>(ENTITY_COUNT, alloc);

            // 生成初始数据（种子固定）
            MoveEntitiesTest.GenerateInitialData(_initialPositions, _initialVelocities, ENTITY_COUNT, seed: 42);

            // 初始化所有副本 = 初始数据
            ResetAllToInitial();
        }

        /// <summary>
        /// 将所有副本重置为初始数据
        /// </summary>
        private void ResetAllToInitial()
        {
            _initialPositions.CopyTo(_positionsScalar);
            _initialVelocities.CopyTo(_velocitiesScalar);
            _initialPositions.CopyTo(_positionsParallel);
            _initialVelocities.CopyTo(_velocitiesParallel);
            _initialPositions.CopyTo(_positionsJob);
            _initialVelocities.CopyTo(_velocitiesJob);
            _initialPositions.CopyTo(_positionsNativeCpp);
            _initialVelocities.CopyTo(_velocitiesNativeCpp);
            _initialPositions.CopyTo(_positionsNativeIspc);
            _initialVelocities.CopyTo(_velocitiesNativeIspc);
            _initialPositions.CopyTo(_positionsNativeCppStatic);
            _initialVelocities.CopyTo(_velocitiesNativeCppStatic);
            _initialPositions.CopyTo(_positionsNativeIspcStatic);
            _initialVelocities.CopyTo(_velocitiesNativeIspcStatic);
        }

        /// <summary>
        /// 运行一个实现并记录每帧耗时
        /// </summary>
        private void RunAndRecord(
            Action<NativeArray<float2>, NativeArray<float2>, int> runFn,
            NativeArray<float2> positions,
            NativeArray<float2> velocities,
            double[] times,
            bool preWakeWorkers = false)
        {
            // 预热
            for (int i = 0; i < WARMUP_FRAMES; i++)
            {
                runFn(positions, velocities, ENTITY_COUNT);
                Thread.Sleep(FRAME_INTERVAL_MS);
            }

            // 正式测试 FRAMES 帧
            for (int frame = 0; frame < FRAMES; frame++)
            {
                var sw = Stopwatch.StartNew();
                runFn(positions, velocities, ENTITY_COUNT);
                sw.Stop();
                times[frame] = sw.Elapsed.TotalMilliseconds;

                if ((frame + 1) % 10 == 0)
                    Console.Write(".");

                if (frame < FRAMES - 1)
                    Thread.Sleep(FRAME_INTERVAL_MS);
            }
            Console.WriteLine();
        }

        private static (double avg, double min, double max, double med) AnalyzeTimes(double[] times)
        {
            double sum = 0;
            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (var t in times)
            {
                sum += t;
                if (t < min) min = t;
                if (t > max) max = t;
            }
            double avg = sum / times.Length;

            var sorted = new double[times.Length];
            Array.Copy(times, sorted, times.Length);
            Array.Sort(sorted);
            double med = sorted.Length % 2 == 0
                ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
                : sorted[sorted.Length / 2];

            return (avg, min, max, med);
        }

        public void RunAll()
        {
            Allocator alloc = Allocator.Persistent;
            _frameReferencePositions = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _frameReferenceVelocities = new NativeArray<float2>(ENTITY_COUNT, alloc);

            Console.WriteLine($"\n=== 帧循环风格 NativeArray 位移测试 ===");
            Console.WriteLine($"实体数: {ENTITY_COUNT:N0}, 帧数: {FRAMES}, 帧间隔: {FRAME_INTERVAL_MS}ms");
            Console.WriteLine($"逻辑核心: {Environment.ProcessorCount}");
            Console.WriteLine();

            // ---- 1. 先跑标量并保存 reference ----
            Console.Write("C# 单线程标量     : ");
            ResetAllToInitial();
            RunAndRecord(MoveEntitiesTest.RunScalar, _positionsScalar, _velocitiesScalar, _scalarTimes);
            // 保存标量结果到专用 reference 副本
            _positionsScalar.CopyTo(_frameReferencePositions);
            _velocitiesScalar.CopyTo(_frameReferenceVelocities);
            var (avgScalar, minScalar, maxScalar, medScalar) = AnalyzeTimes(_scalarTimes);

            // ---- 2. Parallel.For ----
            Console.Write("C# Parallel.For    : ");
            ResetAllToInitial();
            RunAndRecord(MoveEntitiesTest.RunParallelFor, _positionsParallel, _velocitiesParallel, _parallelTimes);
            var (avgParallel, minParallel, maxParallel, medParallel) = AnalyzeTimes(_parallelTimes);
            MoveEntitiesTest.VerifyResults(_frameReferencePositions, _positionsParallel, "Parallel.For");

            // ---- 3. JobSystem ----
            Console.Write("EntJoy JobSystem    : ");
            ResetAllToInitial();
            RunAndRecord(MoveEntitiesTest.RunJobSystem, _positionsJob, _velocitiesJob, _jobTimes, preWakeWorkers: true);
            var (avgJob, minJob, maxJob, medJob) = AnalyzeTimes(_jobTimes);
            MoveEntitiesTest.VerifyResults(_frameReferencePositions, _positionsJob, "JobSystem");

            // ---- 4. Native C++ (Job) ----
            Console.Write("NativeTranspile C++ : ");
            ResetAllToInitial();
            RunAndRecord(MoveEntitiesTest.RunNativeCpp, _positionsNativeCpp, _velocitiesNativeCpp, _nativeCppTimes, preWakeWorkers: true);
            var (avgNativeCpp, minNativeCpp, maxNativeCpp, medNativeCpp) = AnalyzeTimes(_nativeCppTimes);
            MoveEntitiesTest.VerifyResults(_frameReferencePositions, _positionsNativeCpp, "Native C++");

            // ---- 5. Native ISPC (Job) ----
            Console.Write("NativeTranspile ISPC: ");
            ResetAllToInitial();
            RunAndRecord(MoveEntitiesTest.RunNativeIspc, _positionsNativeIspc, _velocitiesNativeIspc, _nativeIspcTimes, preWakeWorkers: true);
            var (avgNativeIspc, minNativeIspc, maxNativeIspc, medNativeIspc) = AnalyzeTimes(_nativeIspcTimes);

            // ★ 立即验证 ISPC Job，避免后续 ResetAllToInitial() 覆盖数据
            MoveEntitiesTest.VerifyResults(_frameReferencePositions, _positionsNativeIspc, "Native ISPC Job");

            // ---- 6. Native C++ (Static) ----
            Console.Write("NativeTranspile C++(Static): ");
            ResetAllToInitial();
            RunAndRecord(NativeTranspiler.Bindings.NativeExports.RunNativeCppStatic, _positionsNativeCppStatic, _velocitiesNativeCppStatic, _nativeCppStaticTimes, preWakeWorkers: true);
            var (avgNativeCppStatic, minNativeCppStatic, maxNativeCppStatic, medNativeCppStatic) = AnalyzeTimes(_nativeCppStaticTimes);
            MoveEntitiesTest.VerifyResults(_frameReferencePositions, _positionsNativeCppStatic, "Native C++ Static");

            // ---- 7. Native ISPC (Static) ----
            Console.Write("NativeTranspile ISPC(Static): ");
            ResetAllToInitial();
            RunAndRecord(NativeTranspiler.Bindings.NativeExports.RunNativeIspcStatic, _positionsNativeIspcStatic, _velocitiesNativeIspcStatic, _nativeIspcStaticTimes, preWakeWorkers: true);
            var (avgNativeIspcStatic, minNativeIspcStatic, maxNativeIspcStatic, medNativeIspcStatic) = AnalyzeTimes(_nativeIspcStaticTimes);

            // ----- 结果输出 -----
            Console.WriteLine($"\n--- 结果 ({FRAMES} 帧统计, 每帧间隔 {FRAME_INTERVAL_MS}ms) ---");
            Console.WriteLine($"{"实现",-30} {"平均(ms)",-10} {"最小(ms)",-10} {"最大(ms)",-10} {"中位数(ms)",-12} {"加速比",-8}");
            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"{"C# 单线程标量",-30} {avgScalar,-10:F3} {minScalar,-10:F3} {maxScalar,-10:F3} {medScalar,-12:F3} {"1.00x",-8}");
            Console.WriteLine($"{"C# Parallel.For",-30} {avgParallel,-10:F3} {minParallel,-10:F3} {maxParallel,-10:F3} {medParallel,-12:F3} {avgScalar / avgParallel,-8:F2}x");
            Console.WriteLine($"{"EntJoy JobSystem",-30} {avgJob,-10:F3} {minJob,-10:F3} {maxJob,-10:F3} {medJob,-12:F3} {avgScalar / avgJob,-8:F2}x");
            Console.WriteLine($"{"NativeTranspile C++ (Job)",-30} {avgNativeCpp,-10:F3} {minNativeCpp,-10:F3} {maxNativeCpp,-10:F3} {medNativeCpp,-12:F3} {avgScalar / avgNativeCpp,-8:F2}x");
            Console.WriteLine($"{"NativeTranspile ISPC (Job)",-30} {avgNativeIspc,-10:F3} {minNativeIspc,-10:F3} {maxNativeIspc,-10:F3} {medNativeIspc,-12:F3} {avgScalar / avgNativeIspc,-8:F2}x");
            Console.WriteLine($"{"NativeTranspile C++ (Static)",-30} {avgNativeCppStatic,-10:F3} {minNativeCppStatic,-10:F3} {maxNativeCppStatic,-10:F3} {medNativeCppStatic,-12:F3} {avgScalar / avgNativeCppStatic,-8:F2}x");
            Console.WriteLine($"{"NativeTranspile ISPC (Static)",-30} {avgNativeIspcStatic,-10:F3} {minNativeIspcStatic,-10:F3} {maxNativeIspcStatic,-10:F3} {medNativeIspcStatic,-12:F3} {avgScalar / avgNativeIspcStatic,-8:F2}x");

            // ----- ISPC Static 最后验证（单独打印） -----
            Console.Write($"  [Native ISPC Static]  ");
            MoveEntitiesTest.VerifyResults(_frameReferencePositions, _positionsNativeIspcStatic, "Native ISPC Static");
        }

        public void Dispose()
        {
            if (_initialPositions.IsCreated) _initialPositions.Dispose();
            if (_initialVelocities.IsCreated) _initialVelocities.Dispose();
            if (_frameReferencePositions.IsCreated) _frameReferencePositions.Dispose();
            if (_frameReferenceVelocities.IsCreated) _frameReferenceVelocities.Dispose();
            if (_positionsScalar.IsCreated) _positionsScalar.Dispose();
            if (_velocitiesScalar.IsCreated) _velocitiesScalar.Dispose();
            if (_positionsParallel.IsCreated) _positionsParallel.Dispose();
            if (_velocitiesParallel.IsCreated) _velocitiesParallel.Dispose();
            if (_positionsJob.IsCreated) _positionsJob.Dispose();
            if (_velocitiesJob.IsCreated) _velocitiesJob.Dispose();
            if (_positionsNativeCpp.IsCreated) _positionsNativeCpp.Dispose();
            if (_velocitiesNativeCpp.IsCreated) _velocitiesNativeCpp.Dispose();
            if (_positionsNativeIspc.IsCreated) _positionsNativeIspc.Dispose();
            if (_velocitiesNativeIspc.IsCreated) _velocitiesNativeIspc.Dispose();
            if (_positionsNativeCppStatic.IsCreated) _positionsNativeCppStatic.Dispose();
            if (_velocitiesNativeCppStatic.IsCreated) _velocitiesNativeCppStatic.Dispose();
            if (_positionsNativeIspcStatic.IsCreated) _positionsNativeIspcStatic.Dispose();
            if (_velocitiesNativeIspcStatic.IsCreated) _velocitiesNativeIspcStatic.Dispose();
        }
    }
}
