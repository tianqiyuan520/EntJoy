//using EntJoy.Collections;
//using EntJoy.Mathematics;
//using EntJoy.JobSystem;
//using System.Diagnostics;

//namespace EntJoy.MovementTest
//{
//    /// <summary>
//    /// 帧循环风格的 ECS 位移测试
//    /// 每次运行 1 次迭代 → 等待 16ms → 再运行 1 次，共 100 帧
//    /// 模拟 GameLoop 中每帧调度的实际表现
//    /// </summary>
//    public class EcsMoveFrameTest : IDisposable
//    {
//        private const int ENTITY_COUNT = 1_000_000;
//        private const float VIEWPORT_WIDTH = 1920f;
//        private const float VIEWPORT_HEIGHT = 1080f;
//        private const float DT = 0.016f;
//        private const int FRAMES = 100;
//        private const int FRAME_INTERVAL_MS = 16;
//        private const int WARMUP_FRAMES = 3;

//        private bool _disposed;

//        // 标量 reference（用于正确性验证）
//        private NativeArray<float2> _refPositions;
//        private NativeArray<float2> _refVelocities;

//        // 每帧耗时记录（ms）
//        private double[] _manualTimes = new double[FRAMES];
//        private double[] _chunkTimes = new double[FRAMES];

//        public EcsMoveFrameTest()
//        {
//            _refPositions = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
//            _refVelocities = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
//            MoveEntitiesTest.GenerateInitialData(_refPositions, _refVelocities, ENTITY_COUNT, seed: 42);
//        }

//        /// <summary>
//        /// 创建全新的 World 并填充实体（从 reference 初始数据）
//        /// </summary>
//        private World CreateWorld()
//        {
//            var world = new World("MoveFrameTestWorld");
//            var em = world.EntityManager;
//            for (int i = 0; i < ENTITY_COUNT; i++)
//            {
//                var entity = em.NewEntity(typeof(EcsPosition), typeof(EcsVelocity));
//                em.Set(entity, new EcsPosition { pos = _refPositions[i] });
//                em.Set(entity, new EcsVelocity { vel = _refVelocities[i] });
//            }
//            return world;
//        }

//        /// <summary>
//        /// 运行一个实现并记录每帧耗时（不重置数据，模拟真实 GameLoop）
//        /// </summary>
//        private void RunAndRecord(
//            Action runFn,
//            double[] times,
//            string label,
//            bool preWakeWorkers = false)
//        {
//            // 预热
//            for (int i = 0; i < WARMUP_FRAMES; i++)
//            {
//                runFn();
//                Thread.Sleep(FRAME_INTERVAL_MS);
//            }

//            // 正式测试 FRAMES 帧
//            for (int frame = 0; frame < FRAMES; frame++)
//            {
//                var sw = Stopwatch.StartNew();
//                runFn();
//                sw.Stop();
//                times[frame] = sw.Elapsed.TotalMilliseconds;

//                if ((frame + 1) % 10 == 0)
//                    Console.Write(".");

//                if (frame < FRAMES - 1)
//                    Thread.Sleep(FRAME_INTERVAL_MS);
//            }
//            Console.WriteLine();
//        }

//        /// <summary>
//        /// 统计一组耗时数据
//        /// </summary>
//        private static (double avg, double min, double max, double med) AnalyzeTimes(double[] times)
//        {
//            double sum = 0;
//            double min = double.MaxValue;
//            double max = double.MinValue;
//            foreach (var t in times)
//            {
//                sum += t;
//                if (t < min) min = t;
//                if (t > max) max = t;
//            }
//            double avg = sum / times.Length;

//            var sorted = new double[times.Length];
//            Array.Copy(times, sorted, times.Length);
//            Array.Sort(sorted);
//            double med = sorted.Length % 2 == 0
//                ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
//                : sorted[sorted.Length / 2];

//            return (avg, min, max, med);
//        }

//        public void RunAll()
//        {
//            Console.WriteLine($"\n=== 帧循环风格 ECS 位移测试 ===");
//            Console.WriteLine($"实体数: {ENTITY_COUNT:N0}, 帧数: {FRAMES}, 帧间隔: {FRAME_INTERVAL_MS}ms");
//            Console.WriteLine();

//            // ----- 1. Manual Query -----
//            Console.Write("ECS 手动遍历       : ");
//            var manualWorld = CreateWorld();
//            EcsMoveTest.RunManualQuery(); // warmup & init static ref
//            RunAndRecord(
//                () => EcsMoveTest.RunManualQuery(),
//                _manualTimes,
//                "Manual"
//            );
//            manualWorld.Dispose();

//            // ----- 2. IJobChunk -----
//            Console.Write("ECS IJobChunk       : ");
//            var chunkWorld = CreateWorld();
//            EcsMoveTest.RunJobChunk(); // warmup & init static ref
//            RunAndRecord(
//                () => EcsMoveTest.RunJobChunk(),
//                _chunkTimes,
//                "Chunk",
//                preWakeWorkers: true
//            );
//            chunkWorld.Dispose();

//            var (avgManual, minManual, maxManual, medManual) = AnalyzeTimes(_manualTimes);
//            var (avgChunk, minChunk, maxChunk, medChunk) = AnalyzeTimes(_chunkTimes);

//            // ----- 结果输出 -----
//            Console.WriteLine($"\n--- ECS 结果 ({FRAMES} 帧统计, 每帧间隔 {FRAME_INTERVAL_MS}ms) ---");
//            Console.WriteLine($"{"实现",-22} {"平均(ms)",-10} {"最小(ms)",-10} {"最大(ms)",-10} {"中位数(ms)",-12} {"加速比",-8}");
//            Console.WriteLine(new string('-', 72));
//            Console.WriteLine($"{"ECS 手动遍历",-22} {avgManual,-10:F3} {minManual,-10:F3} {maxManual,-10:F3} {medManual,-12:F3} {"1.00x",-8}");
//            Console.WriteLine($"{"ECS IJobChunk",-22} {avgChunk,-10:F3} {minChunk,-10:F3} {maxChunk,-10:F3} {medChunk,-12:F3} {avgManual / avgChunk,-8:F2}x");
//        }

//        public void Dispose()
//        {
//            if (!_disposed)
//            {
//                _disposed = true;
//                if (_refPositions.IsCreated) _refPositions.Dispose();
//                if (_refVelocities.IsCreated) _refVelocities.Dispose();
//            }
//        }
//    }
//}
