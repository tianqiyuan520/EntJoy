using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;
using EntJoy.MovementTest;
using System.Diagnostics;

namespace EntJoySample.SpritesRandomMove
{
    public enum MoveExecutionMode
    {
        EcsIJobChunk,
        CSharpJob,
        NativeCppJob,
        NativeIspcJob
    }

    public sealed class SpritesRandomMoveLikeTest : IDisposable
    {
        private const int EntityCount = 1_000_000;
        private const float ViewportWidth = 1920f;
        private const float ViewportHeight = 1080f;
        private const float DeltaTime = 1f / 60f;
        private const double PhysicsFrameMilliseconds = 1000.0 / 60.0;

        private readonly QueryBuilder _moveQuery = new QueryBuilder().WithAll<EcsPosition, EcsVelocity>();
        private NativeArray<float2> _initialPositions;
        private NativeArray<float2> _initialVelocities;
        private NativeArray<float2> _nativePositions;
        private NativeArray<float2> _nativeVelocities;
        private bool _nativeInitialized;
        private World? _world;

        public SpritesRandomMoveLikeTest()
        {
            _initialPositions = new NativeArray<float2>(EntityCount, Allocator.Persistent);
            _initialVelocities = new NativeArray<float2>(EntityCount, Allocator.Persistent);
            MoveEntitiesTest.GenerateInitialData(_initialPositions, _initialVelocities, EntityCount, seed: 42);
        }

        public void CreateWorld()
        {
            _world?.Dispose();
            _world = new World("SpritesRandomMoveLikeWorld");
        }

        public void CreateEntities()
        {
            if (_world == null)
            {
                CreateWorld();
            }
            var entityManager = _world!.EntityManager;
            for (int index = 0; index < EntityCount; index++)
            {
                var entity = entityManager.NewEntity(typeof(EcsPosition), typeof(EcsVelocity));
                entityManager.Set(entity, new EcsPosition { pos = _initialPositions[index] });
                entityManager.Set(entity, new EcsVelocity { vel = _initialVelocities[index] });
            }
        }

        public void InitNativeArraysFromInitialData()
        {
            EnsureNativeArrays();
            _initialPositions.CopyTo(_nativePositions);
            _initialVelocities.CopyTo(_nativeVelocities);
            _nativeInitialized = true;
        }

        public void InitNativeArraysFromWorld()
        {
            EnsureWorld();
            EnsureNativeArrays();

            int writeIndex = 0;
            foreach (var chunk in SystemAPI.QueryChunks<EcsPosition, EcsVelocity>())
            {
                var positions = chunk.GetSpan0();
                var velocities = chunk.GetSpan1();
                for (int index = 0; index < chunk.Length; index++)
                {
                    _nativePositions[writeIndex] = positions[index].pos;
                    _nativeVelocities[writeIndex] = velocities[index].vel;
                    writeIndex++;
                }
            }

            _nativeInitialized = true;
        }

        public void Step(MoveExecutionMode mode)
        {
            switch (mode)
            {
                case MoveExecutionMode.EcsIJobChunk:
                    EnsureWorld();
                    new EcsMoveJobChunk
                    {
                        Dt = DeltaTime,
                        ViewportWidth = ViewportWidth,
                        ViewportHeight = ViewportHeight
                    }.Schedule(_moveQuery).Complete();
                    break;
                case MoveExecutionMode.CSharpJob:
                    EnsureNativeArrays();
                    new MoveEntitiesJob
                    {
                        Positions = _nativePositions,
                        Velocities = _nativeVelocities,
                        Dt = DeltaTime,
                        ViewportWidth = ViewportWidth,
                        ViewportHeight = ViewportHeight,
                        Count = EntityCount
                    }.Schedule(EntityCount, 65536).Complete();
                    break;
                case MoveExecutionMode.NativeCppJob:
                    EnsureNativeArrays();
                    new MoveEntitiesJob_NativeCpp
                    {
                        Positions = _nativePositions,
                        Velocities = _nativeVelocities,
                        Dt = DeltaTime,
                        ViewportWidth = ViewportWidth,
                        ViewportHeight = ViewportHeight,
                        Count = EntityCount
                    }.Schedule(EntityCount, 0).Complete();
                    break;
                case MoveExecutionMode.NativeIspcJob:
                    EnsureNativeArrays();
                    new MoveEntitiesJob_NativeIspc
                    {
                        Positions = _nativePositions,
                        Velocities = _nativeVelocities,
                        Dt = DeltaTime,
                        ViewportWidth = ViewportWidth,
                        ViewportHeight = ViewportHeight,
                        Count = EntityCount
                    }.Schedule(EntityCount, 0).Complete();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        public NativeArray<float2> CapturePositions(MoveExecutionMode mode)
        {
            return mode == MoveExecutionMode.EcsIJobChunk ? CaptureEcsPositions() : CaptureNativePositions();
        }

        public double RunBenchmark(MoveExecutionMode mode, int warmup = 3, int iterations = 1000, double frameIntervalMilliseconds = PhysicsFrameMilliseconds)
        {
            for (int iteration = 0; iteration < warmup; iteration++)
            {
                var frameStopwatch = Stopwatch.StartNew();
                Step(mode);
                WaitForNextFrame(frameStopwatch, frameIntervalMilliseconds);
            }

            double totalMilliseconds = 0;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var frameStopwatch = Stopwatch.StartNew();
                var stopwatch = Stopwatch.StartNew();
                Step(mode);
                stopwatch.Stop();
                totalMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
                WaitForNextFrame(frameStopwatch, frameIntervalMilliseconds);
            }

            return totalMilliseconds / iterations;
        }

        public FrameStats RunFrameLoop(MoveExecutionMode mode, int frames = 60, double frameIntervalMilliseconds = PhysicsFrameMilliseconds)
        {
            var samples = new double[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                var frameStopwatch = Stopwatch.StartNew();
                var stopwatch = Stopwatch.StartNew();
                Step(mode);
                stopwatch.Stop();
                samples[frame] = stopwatch.Elapsed.TotalMilliseconds;
                WaitForNextFrame(frameStopwatch, frameIntervalMilliseconds);
            }

            return FrameStats.From(samples);
        }

        public void RunParitySuite(int frames = 60, int benchmarkIterations = 200)
        {
            Console.WriteLine("=== SpritesRandomMove 风格运动测试 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, 帧数: {frames}, Benchmark迭代: {benchmarkIterations}");
            Console.WriteLine($"物理帧间隔: {PhysicsFrameMilliseconds:F4}ms (60 FPS)");
            Console.WriteLine("说明: Parity Suite 会对每种实现回到同一初始基线后再做对拍。");
            Console.WriteLine();

            ResetModeState(MoveExecutionMode.EcsIJobChunk);
            var ecsStats = RunFrameLoop(MoveExecutionMode.EcsIJobChunk, frames, PhysicsFrameMilliseconds);
            using var ecsReference = CapturePositions(MoveExecutionMode.EcsIJobChunk);

            Console.WriteLine($"[ECS IJobChunk] avg={ecsStats.Average:F3} ms min={ecsStats.Min:F3} ms max={ecsStats.Max:F3} ms median={ecsStats.Median:F3} ms");

            foreach (var mode in new[]
                     {
                         MoveExecutionMode.CSharpJob,
                         MoveExecutionMode.NativeCppJob,
                         MoveExecutionMode.NativeIspcJob
                     })
            {
                ResetModeState(mode);
                var frameStats = RunFrameLoop(mode, frames, PhysicsFrameMilliseconds);
                using var snapshot = CapturePositions(mode);
                MoveEntitiesTest.VerifyResults(ecsReference, snapshot, mode.ToString());
                ResetModeState(mode);
                double averageBenchmark = RunBenchmark(mode, iterations: benchmarkIterations, frameIntervalMilliseconds: PhysicsFrameMilliseconds);
                Console.WriteLine($"[{mode}] avg={frameStats.Average:F3} ms min={frameStats.Min:F3} ms max={frameStats.Max:F3} ms median={frameStats.Median:F3} ms benchmark={averageBenchmark:F3} ms");
            }

            ResetModeState(MoveExecutionMode.EcsIJobChunk);
            double ecsBenchmark = RunBenchmark(MoveExecutionMode.EcsIJobChunk, iterations: benchmarkIterations, frameIntervalMilliseconds: PhysicsFrameMilliseconds);
            Console.WriteLine($"[ECS IJobChunk] benchmark={ecsBenchmark:F3} ms");
        }

        public void RunRealtimeLoop(MoveExecutionMode mode, int reportEveryFrames = 30, double frameIntervalMilliseconds = PhysicsFrameMilliseconds)
        {
            EnsureModeReady(mode);

            long totalFrames = 0;
            double accumulatedMilliseconds = 0;

            Console.WriteLine($"开始运行 [{mode}]，按 60 FPS 固定间隔 ({frameIntervalMilliseconds:F4}ms) 调度，每 {reportEveryFrames} 帧汇报一次。");

            while (true)
            {
                var frameStopwatch = Stopwatch.StartNew();
                var stopwatch = Stopwatch.StartNew();
                Step(mode);
                stopwatch.Stop();

                double elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                totalFrames++;
                accumulatedMilliseconds += elapsedMilliseconds;

                if (totalFrames % reportEveryFrames == 0)
                {
                    double averageMilliseconds = accumulatedMilliseconds / reportEveryFrames;
                    Console.WriteLine($"[{mode}] avg={averageMilliseconds:F4} ms");
                    accumulatedMilliseconds = 0;
                }

                WaitForNextFrame(frameStopwatch, frameIntervalMilliseconds);
            }
        }

        public void Dispose()
        {
            _world?.Dispose();
            if (_initialPositions.IsCreated) _initialPositions.Dispose();
            if (_initialVelocities.IsCreated) _initialVelocities.Dispose();
            if (_nativePositions.IsCreated) _nativePositions.Dispose();
            if (_nativeVelocities.IsCreated) _nativeVelocities.Dispose();
        }

        private void EnsureModeReady(MoveExecutionMode mode)
        {
            if (mode == MoveExecutionMode.EcsIJobChunk)
            {
                EnsureWorld();
                return;
            }

            EnsureNativeData();
        }

        private NativeArray<float2> CaptureEcsPositions()
        {
            EnsureWorld();
            var result = new NativeArray<float2>(EntityCount, Allocator.Temp);
            int writeIndex = 0;
            foreach (var chunk in SystemAPI.QueryChunks<EcsPosition, EcsVelocity>())
            {
                var positions = chunk.GetSpan0();
                for (int index = 0; index < chunk.Length; index++)
                {
                    result[writeIndex] = positions[index].pos;
                    writeIndex++;
                }
            }

            return result;
        }

        private NativeArray<float2> CaptureNativePositions()
        {
            EnsureNativeArrays();
            var result = new NativeArray<float2>(EntityCount, Allocator.Temp);
            _nativePositions.CopyTo(result);
            return result;
        }

        private void EnsureWorld()
        {
            if (_world == null)
            {
                CreateWorld();
            }

            if (_world!.EntityManager.EntityCount == 0)
            {
                CreateEntities();
            }
        }

        private void EnsureNativeArrays()
        {
            if (!_nativePositions.IsCreated)
            {
                _nativePositions = new NativeArray<float2>(EntityCount, Allocator.Persistent);
            }

            if (!_nativeVelocities.IsCreated)
            {
                _nativeVelocities = new NativeArray<float2>(EntityCount, Allocator.Persistent);
            }
        }

        private void EnsureNativeData()
        {
            if (_nativeInitialized)
            {
                EnsureNativeArrays();
                return;
            }

            if (_world != null && _world.EntityManager.EntityCount > 0)
            {
                InitNativeArraysFromWorld();
                return;
            }

            InitNativeArraysFromInitialData();
        }

        private void ResetModeState(MoveExecutionMode mode)
        {
            if (mode == MoveExecutionMode.EcsIJobChunk)
            {
                CreateWorld();
                CreateEntities();
                return;
            }

            InitNativeArraysFromInitialData();
        }

        private static void WaitForNextFrame(Stopwatch frameStopwatch, double frameIntervalMilliseconds)
        {
            double remainingMilliseconds = frameIntervalMilliseconds - frameStopwatch.Elapsed.TotalMilliseconds;
            if (remainingMilliseconds <= 0)
            {
                return;
            }

            int sleepMilliseconds = (int)Math.Ceiling(remainingMilliseconds);
            if (sleepMilliseconds > 0)
            {
                Thread.Sleep(sleepMilliseconds);
            }
        }
    }

    public readonly record struct FrameStats(double Average, double Min, double Max, double Median)
    {
        public static FrameStats From(double[] samples)
        {
            double total = 0;
            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (double sample in samples)
            {
                total += sample;
                min = Math.Min(min, sample);
                max = Math.Max(max, sample);
            }

            var sorted = new double[samples.Length];
            Array.Copy(samples, sorted, samples.Length);
            Array.Sort(sorted);

            double median = sorted.Length % 2 == 0
                ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * 0.5
                : sorted[sorted.Length / 2];

            return new FrameStats(total / samples.Length, min, max, median);
        }
    }
}
