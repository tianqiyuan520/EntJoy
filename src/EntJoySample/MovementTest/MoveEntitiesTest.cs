using EntJoy.Collections;
using EntJoy.Mathematics;
using System.Diagnostics;

namespace EntJoy.MovementTest
{

    // 位置组件，对应 Godot 的 Position struct
    public struct EntityPosition
    {
        public float2 pos;
    }

    // 速度组件，对应 Godot 的 Vel struct
    public struct EntityVelocity
    {
        public float2 vel;
    }

    /// <summary>
    /// 100w 实体位移测试（基准测试 + 正确性验证）
    /// 所有算法使用完全相同的初始数据，结束后验证结果一致
    /// </summary>
    public class MoveEntitiesTest
    {
        private const int ENTITY_COUNT = 1_000_000;
        private const float VIEWPORT_WIDTH = 1920f;
        private const float VIEWPORT_HEIGHT = 1080f;
        private const float DT = 0.016f;

        /// <summary>
        /// 用确定的随机种子生成一致的初始数据
        /// </summary>
        public static void GenerateInitialData(
            NativeArray<float2> positions,
            NativeArray<float2> velocities,
            int count,
            int seed = 42)
        {
            var rnd = new Random(seed);
            for (int i = 0; i < count; i++)
            {
                positions[i] = new float2(
                    (float)(rnd.NextDouble() * VIEWPORT_WIDTH),
                    (float)(rnd.NextDouble() * VIEWPORT_HEIGHT)
                );
                velocities[i] = new float2(
                    (float)(rnd.NextDouble() * 100.0 + 100.0),
                    (float)(rnd.NextDouble() * 400.0 - 200.0)
                );
            }
        }

        /// <summary>
        /// 验证两个结果数组是否一致，返回差值最大值
        /// </summary>
        public static float VerifyResults(
            NativeArray<float2> expected,
            NativeArray<float2> actual,
            string label)
        {
            float maxDiff = 0f;
            int mismatchCount = 0;
            const float EPSILON = 1e-4f;

            for (int i = 0; i < expected.Length; i++)
            {
                float dx = Math.Abs(expected[i].x - actual[i].x);
                float dy = Math.Abs(expected[i].y - actual[i].y);
                float diff = Math.Max(dx, dy);
                if (diff > maxDiff) maxDiff = diff;
                if (diff > EPSILON)
                {
                    mismatchCount++;
                    if (mismatchCount <= 3)
                    {
                        Console.WriteLine($"  [{label}] Entity {i}: expected({expected[i].x:F4},{expected[i].y:F4}) actual({actual[i].x:F4},{actual[i].y:F4}) diff={diff:E4}");
                    }
                }
            }

            if (mismatchCount == 0)
            {
                Console.WriteLine($"  [{label}]  完全正确！maxDiff={maxDiff:E4}");
            }
            else
            {
                Console.WriteLine($"  [{label}]  错误！{mismatchCount}/{expected.Length} 个实体不匹配, maxDiff={maxDiff:E4}");
            }
            return maxDiff;
        }

        // 测试用的数据副本（每个算法独立操作这些副本）
        private NativeArray<float2> _positionsRef;    // 标量参考结果
        private NativeArray<float2> _velocitiesRef;

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

        public MoveEntitiesTest()
        {
            Allocator alloc = Allocator.Persistent;
            _positionsRef = new NativeArray<float2>(ENTITY_COUNT, alloc);
            _velocitiesRef = new NativeArray<float2>(ENTITY_COUNT, alloc);
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

            // 所有数组用相同初始数据填充
            GenerateInitialData(_positionsRef, _velocitiesRef, ENTITY_COUNT);
            _positionsRef.CopyTo(_positionsScalar);
            _velocitiesRef.CopyTo(_velocitiesScalar);
            _positionsRef.CopyTo(_positionsParallel);
            _velocitiesRef.CopyTo(_velocitiesParallel);
            _positionsRef.CopyTo(_positionsJob);
            _velocitiesRef.CopyTo(_velocitiesJob);
            _positionsRef.CopyTo(_positionsNativeCpp);
            _velocitiesRef.CopyTo(_velocitiesNativeCpp);
            _positionsRef.CopyTo(_positionsNativeIspc);
            _velocitiesRef.CopyTo(_velocitiesNativeIspc);
        }

        /// <summary>
        /// C# 单线程标量版本（reference）
        /// </summary>
        public static void RunScalar(NativeArray<float2> pos, NativeArray<float2> vel, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float2 p = pos[i];
                float2 v = vel[i];

                p.x += v.x * DT;
                p.y += v.y * DT;

                if (p.x < 0f || p.x > VIEWPORT_WIDTH) v.x = -v.x;
                if (p.y < 0f || p.y > VIEWPORT_HEIGHT) v.y = -v.y;

                pos[i] = p;
                vel[i] = v;
            }
        }

        /// <summary>
        /// C# Parallel.For 多线程版本
        /// </summary>
        public static void RunParallelFor(NativeArray<float2> pos, NativeArray<float2> vel, int count)
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.For(0, count, options, i =>
            {
                float2 p = pos[i];
                float2 v = vel[i];

                p.x += v.x * DT;
                p.y += v.y * DT;

                if (p.x < 0f || p.x > VIEWPORT_WIDTH) v.x = -v.x;
                if (p.y < 0f || p.y > VIEWPORT_HEIGHT) v.y = -v.y;

                pos[i] = p;
                vel[i] = v;
            });
        }

        /// <summary>
        /// JobSystem IJobParallelFor 版本
        /// </summary>
        public static void RunJobSystem(NativeArray<float2> pos, NativeArray<float2> vel, int count)
        {
            var job = new MoveEntitiesJob
            {
                Positions = pos,
                Velocities = vel,
                Dt = DT,
                ViewportWidth = VIEWPORT_WIDTH,
                ViewportHeight = VIEWPORT_HEIGHT,
                Count = count
            };
            JobHandle handle = job.Schedule(count, 65536);
            handle.Complete();
        }

        /// <summary>
        /// NativeTranspile C++ 编译版本
        /// </summary>
        public static void RunNativeCpp(NativeArray<float2> pos, NativeArray<float2> vel, int count)
        {
            var job = new MoveEntitiesJob_NativeCpp
            {
                Positions = pos,
                Velocities = vel,
                Dt = DT,
                ViewportWidth = VIEWPORT_WIDTH,
                ViewportHeight = VIEWPORT_HEIGHT,
                Count = count
            };
            JobHandle handle = job.Schedule(count, 65536);
            handle.Complete();
        }

        /// <summary>
        /// NativeTranspile ISPC SIMD 编译版本
        /// </summary>
        public static void RunNativeIspc(NativeArray<float2> pos, NativeArray<float2> vel, int count)
        {
            var job = new MoveEntitiesJob_NativeIspc
            {
                Positions = pos,
                Velocities = vel,
                Dt = DT,
                ViewportWidth = VIEWPORT_WIDTH,
                ViewportHeight = VIEWPORT_HEIGHT,
                Count = count
            };
            JobHandle handle = job.Schedule(count, 65536);
            handle.Complete();
        }

        public void RunAll()
        {
            const int WARMUP = 3;
            const int ITERATIONS = 1000;

            Console.WriteLine($"=== 100w 实体位移性能测试 ===");
            Console.WriteLine($"实体数: {ENTITY_COUNT:N0}, 迭代: {ITERATIONS}, 预热: {WARMUP}");
            Console.WriteLine($"逻辑核心: {Environment.ProcessorCount}");
            Console.WriteLine();

            // 预热
            Console.WriteLine("预热中...");
            for (int i = 0; i < WARMUP; i++)
            {
                RunScalar(_positionsScalar, _velocitiesScalar, ENTITY_COUNT);
                RunParallelFor(_positionsParallel, _velocitiesParallel, ENTITY_COUNT);
                RunJobSystem(_positionsJob, _velocitiesJob, ENTITY_COUNT);
                RunNativeCpp(_positionsNativeCpp, _velocitiesNativeCpp, ENTITY_COUNT);
                RunNativeIspc(_positionsNativeIspc, _velocitiesNativeIspc, ENTITY_COUNT);
            }

            Console.WriteLine("开始基准测试...\n");

            // 先重置回初始数据（预热修改了数据）
            _positionsRef.CopyTo(_positionsScalar);
            _velocitiesRef.CopyTo(_velocitiesScalar);
            _positionsRef.CopyTo(_positionsParallel);
            _velocitiesRef.CopyTo(_velocitiesParallel);
            _positionsRef.CopyTo(_positionsJob);
            _velocitiesRef.CopyTo(_velocitiesJob);
            _positionsRef.CopyTo(_positionsNativeCpp);
            _velocitiesRef.CopyTo(_velocitiesNativeCpp);
            _positionsRef.CopyTo(_positionsNativeIspc);
            _velocitiesRef.CopyTo(_velocitiesNativeIspc);

            // ----- 标量基准（也作为参考结果） -----
            double totalScalar = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var sw = Stopwatch.StartNew();
                RunScalar(_positionsScalar, _velocitiesScalar, ENTITY_COUNT);
                sw.Stop();
                totalScalar += sw.Elapsed.TotalMilliseconds;
                if ((i + 1) % 5 == 0) Console.Write(".");
            }
            Console.WriteLine();
            double avgScalar = totalScalar / ITERATIONS;

            // 把标量最终结果作为参考 & 保存到 _positionsRef
            _positionsScalar.CopyTo(_positionsRef);
            _velocitiesScalar.CopyTo(_velocitiesRef);

            // ----- Parallel.For 基准 -----
            double totalParallel = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var sw = Stopwatch.StartNew();
                RunParallelFor(_positionsParallel, _velocitiesParallel, ENTITY_COUNT);
                sw.Stop();
                totalParallel += sw.Elapsed.TotalMilliseconds;
                if ((i + 1) % 5 == 0) Console.Write(".");
            }
            Console.WriteLine();
            double avgParallel = totalParallel / ITERATIONS;

            // ----- JobSystem 基准 -----
            double totalJob = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var sw = Stopwatch.StartNew();
                RunJobSystem(_positionsJob, _velocitiesJob, ENTITY_COUNT);
                sw.Stop();
                totalJob += sw.Elapsed.TotalMilliseconds;
                if ((i + 1) % 5 == 0) Console.Write(".");
            }
            Console.WriteLine();
            double avgJob = totalJob / ITERATIONS;

            // ----- NativeTranspile C++ 基准 -----
            double totalNativeCpp = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var sw = Stopwatch.StartNew();
                RunNativeCpp(_positionsNativeCpp, _velocitiesNativeCpp, ENTITY_COUNT);
                sw.Stop();
                totalNativeCpp += sw.Elapsed.TotalMilliseconds;
                if ((i + 1) % 5 == 0) Console.Write(".");
            }
            Console.WriteLine();
            double avgNativeCpp = totalNativeCpp / ITERATIONS;

            // ----- NativeTranspile ISPC 基准 -----
            double totalNativeIspc = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var sw = Stopwatch.StartNew();
                RunNativeIspc(_positionsNativeIspc, _velocitiesNativeIspc, ENTITY_COUNT);
                sw.Stop();
                totalNativeIspc += sw.Elapsed.TotalMilliseconds;
                if ((i + 1) % 5 == 0) Console.Write(".");
            }
            Console.WriteLine();
            double avgNativeIspc = totalNativeIspc / ITERATIONS;

            // ----- 结果 -----
            Console.WriteLine($"\n--- 结果 ---");
            Console.WriteLine($"C# 单线程标量:              {avgScalar,8:F3} ms");
            Console.WriteLine($"C# Parallel.For:             {avgParallel,8:F3} ms (加速比 {avgScalar / avgParallel:F2}x)");
            Console.WriteLine($"EntJoy JobSystem (C#):       {avgJob,8:F3} ms (加速比 {avgScalar / avgJob:F2}x)");
            Console.WriteLine($"NativeTranspile C++:         {avgNativeCpp,8:F3} ms (加速比 {avgScalar / avgNativeCpp:F2}x)");
            Console.WriteLine($"NativeTranspile ISPC:        {avgNativeIspc,8:F3} ms (加速比 {avgScalar / avgNativeIspc:F2}x)");

            // ----- 正确性验证 -----
            Console.WriteLine($"\n--- 正确性验证（对比标量 reference） ---");
            Console.WriteLine($"初始数据均来自 seed=42 的相同生成序列");
            VerifyResults(_positionsRef, _positionsParallel, "Parallel.For");
            VerifyResults(_positionsRef, _positionsJob, "JobSystem");
            VerifyResults(_positionsRef, _positionsNativeCpp, "Native C++");
            VerifyResults(_positionsRef, _positionsNativeIspc, "Native ISPC");
        }

        public void Dispose()
        {
            if (_positionsRef.IsCreated) _positionsRef.Dispose();
            if (_velocitiesRef.IsCreated) _velocitiesRef.Dispose();
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
        }
    }

    /// <summary>
    /// IJobParallelFor 实现：简单的位移+边界反弹（纯 C# 版本）
    /// </summary>
    public struct MoveEntitiesJob : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;
        public int Count;

        public void Execute(int index)
        {
            float2 pos = Positions[index];
            float2 vel = Velocities[index];

            pos.x += vel.x * Dt;
            pos.y += vel.y * Dt;

            if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
            if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

            Positions[index] = pos;
            Velocities[index] = vel;
        }
    }

    /// <summary>
    /// NativeTranspile C++ 原生编译版本
    /// </summary>
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct MoveEntitiesJob_NativeCpp : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;
        public int Count;

        public void Execute(int index)
        {
            float2 pos = Positions[index];
            float2 vel = Velocities[index];

            pos.x += vel.x * Dt;
            pos.y += vel.y * Dt;

            if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
            if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

            Positions[index] = pos;
            Velocities[index] = vel;
        }
    }

    /// <summary>
    /// NativeTranspile ISPC SIMD 编译版本
    /// </summary>
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct MoveEntitiesJob_NativeIspc : IJobParallelFor
    {
        public NativeArray<float2> Positions;
        public NativeArray<float2> Velocities;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;
        public int Count;

        public void Execute(int index)
        {
            float2 pos = Positions[index];
            float2 vel = Velocities[index];

            pos.x += vel.x * Dt;
            pos.y += vel.y * Dt;

            if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
            if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

            Positions[index] = pos;
            Velocities[index] = vel;
        }
    }
}
