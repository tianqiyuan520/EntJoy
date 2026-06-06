using EntJoy.Collections;
using EntJoy.Mathematics;
using EntJoy.JobSystem;
using System.Diagnostics;

namespace EntJoy.MovementTest
{
    /// <summary>
    /// 专门测试 ISPC Job 正确性的脚本
    /// 只测试 ISPC Job 和 ISPC Static，对比标量 reference
    /// </summary>
    public class IspcJobTest
    {
        private const int ENTITY_COUNT = 1_000_000;
        private const float VIEWPORT_WIDTH = 1920f;
        private const float VIEWPORT_HEIGHT = 1080f;
        private const float DT = 0.016f;
        private const int ITERATIONS = 5;

        public static void Run()
        {
            Allocator alloc = Allocator.Persistent;

            // 初始数据
            var initialPos = new NativeArray<float2>(ENTITY_COUNT, alloc);
            var initialVel = new NativeArray<float2>(ENTITY_COUNT, alloc);
            MoveEntitiesTest.GenerateInitialData(initialPos, initialVel, ENTITY_COUNT, seed: 42);

            // 各实现独立副本
            var refPos = new NativeArray<float2>(ENTITY_COUNT, alloc);
            var refVel = new NativeArray<float2>(ENTITY_COUNT, alloc);
            var ispcJobPos = new NativeArray<float2>(ENTITY_COUNT, alloc);
            var ispcJobVel = new NativeArray<float2>(ENTITY_COUNT, alloc);
            var ispcStaticPos = new NativeArray<float2>(ENTITY_COUNT, alloc);
            var ispcStaticVel = new NativeArray<float2>(ENTITY_COUNT, alloc);

            Console.WriteLine("=== ISPC Job 正确性专项测试 ===");
            Console.WriteLine($"实体数: {ENTITY_COUNT:N0}, 迭代: {ITERATIONS}");
            Console.WriteLine();

            // ---- 1. 标量 reference（单次迭代）----
            initialPos.CopyTo(refPos);
            initialVel.CopyTo(refVel);
            MoveEntitiesTest.RunScalar(refPos, refVel, ENTITY_COUNT);
            Console.WriteLine("标量 reference 完成");

            // ---- 2. ISPC Job（单次迭代）----
            initialPos.CopyTo(ispcJobPos);
            initialVel.CopyTo(ispcJobVel);
            MoveEntitiesTest.RunNativeIspc(ispcJobPos, ispcJobVel, ENTITY_COUNT);
            Console.WriteLine("ISPC Job 完成");

            // ---- 3. ISPC Static（单次迭代）----
            initialPos.CopyTo(ispcStaticPos);
            initialVel.CopyTo(ispcStaticVel);
            NativeTranspiler.Bindings.NativeExports.RunNativeIspcStatic(ispcStaticPos, ispcStaticVel, ENTITY_COUNT);
            Console.WriteLine("ISPC Static 完成");

            // ---- 验证 ----
            Console.WriteLine();
            Console.WriteLine("--- 正确性验证（单次迭代）---");
            MoveEntitiesTest.VerifyResults(refPos, ispcJobPos, "ISPC Job");
            MoveEntitiesTest.VerifyResults(refPos, ispcStaticPos, "ISPC Static");

            // ---- 多次迭代测试（累加 100 次）----
            Console.WriteLine();
            Console.WriteLine("--- 100 次迭代累加测试 ---");

            // 标量 reference
            initialPos.CopyTo(refPos);
            initialVel.CopyTo(refVel);
            for (int i = 0; i < 100; i++)
                MoveEntitiesTest.RunScalar(refPos, refVel, ENTITY_COUNT);

            // ISPC Job
            initialPos.CopyTo(ispcJobPos);
            initialVel.CopyTo(ispcJobVel);
            for (int i = 0; i < 100; i++)
                MoveEntitiesTest.RunNativeIspc(ispcJobPos, ispcJobVel, ENTITY_COUNT);

            // ISPC Static
            initialPos.CopyTo(ispcStaticPos);
            initialVel.CopyTo(ispcStaticVel);
            for (int i = 0; i < 100; i++)
                NativeTranspiler.Bindings.NativeExports.RunNativeIspcStatic(ispcStaticPos, ispcStaticVel, ENTITY_COUNT);

            Console.WriteLine();
            Console.WriteLine("--- 正确性验证（100 次迭代累加）---");
            MoveEntitiesTest.VerifyResults(refPos, ispcJobPos, "ISPC Job x100");
            MoveEntitiesTest.VerifyResults(refPos, ispcStaticPos, "ISPC Static x100");

            // ---- 不同 batchSize 测试 ----
            Console.WriteLine();
            Console.WriteLine("--- 不同 batchSize 测试（单次迭代，每个 batchSize 使用独立 reference）---");

            int[] batchSizes = { 1, 16, 64, 256, 1024, 4096, 16384, 65536, 1000000 };
            foreach (int batchSize in batchSizes)
            {
                // 为每个 batchSize 创建独立的 reference
                var batchRefPos = new NativeArray<float2>(ENTITY_COUNT, alloc);
                var batchRefVel = new NativeArray<float2>(ENTITY_COUNT, alloc);
                initialPos.CopyTo(batchRefPos);
                initialVel.CopyTo(batchRefVel);
                MoveEntitiesTest.RunScalar(batchRefPos, batchRefVel, ENTITY_COUNT);

                initialPos.CopyTo(ispcJobPos);
                initialVel.CopyTo(ispcJobVel);

                var job = new MoveEntitiesJob_NativeIspc
                {
                    Positions = ispcJobPos,
                    Velocities = ispcJobVel,
                    Dt = DT,
                    ViewportWidth = VIEWPORT_WIDTH,
                    ViewportHeight = VIEWPORT_HEIGHT,
                    Count = ENTITY_COUNT
                };
                JobHandle handle = job.Schedule(ENTITY_COUNT, batchSize);
                handle.Complete();

                float maxDiff = MoveEntitiesTest.VerifyResults(batchRefPos, ispcJobPos, $"ISPC Job batch={batchSize}");
                if (maxDiff > 1e-4f)
                    Console.WriteLine($"  *** batchSize={batchSize} 错误！maxDiff={maxDiff:E4}");

                batchRefPos.Dispose();
                batchRefVel.Dispose();
            }

            // ---- 单线程调度测试（batchSize = ENTITY_COUNT）----
            Console.WriteLine();
            Console.WriteLine("--- 单 batch 调度测试（batchSize = ENTITY_COUNT）---");
            {
                var singleRefPos = new NativeArray<float2>(ENTITY_COUNT, alloc);
                var singleRefVel = new NativeArray<float2>(ENTITY_COUNT, alloc);
                initialPos.CopyTo(singleRefPos);
                initialVel.CopyTo(singleRefVel);
                MoveEntitiesTest.RunScalar(singleRefPos, singleRefVel, ENTITY_COUNT);

                initialPos.CopyTo(ispcJobPos);
                initialVel.CopyTo(ispcJobVel);

                var jobSingle = new MoveEntitiesJob_NativeIspc
                {
                    Positions = ispcJobPos,
                    Velocities = ispcJobVel,
                    Dt = DT,
                    ViewportWidth = VIEWPORT_WIDTH,
                    ViewportHeight = VIEWPORT_HEIGHT,
                    Count = ENTITY_COUNT
                };
                JobHandle handleSingle = jobSingle.Schedule(ENTITY_COUNT, ENTITY_COUNT);
                handleSingle.Complete();
                MoveEntitiesTest.VerifyResults(singleRefPos, ispcJobPos, "ISPC Job single batch");

                singleRefPos.Dispose();
                singleRefVel.Dispose();
            }

            // ---- 直接调用 DllImport 测试（绕过 JobSystem）----
            Console.WriteLine();
            Console.WriteLine("--- 直接 DllImport 调用测试 ---");
            {
                var dllRefPos = new NativeArray<float2>(ENTITY_COUNT, alloc);
                var dllRefVel = new NativeArray<float2>(ENTITY_COUNT, alloc);
                initialPos.CopyTo(dllRefPos);
                initialVel.CopyTo(dllRefVel);
                MoveEntitiesTest.RunScalar(dllRefPos, dllRefVel, ENTITY_COUNT);

                initialPos.CopyTo(ispcJobPos);
                initialVel.CopyTo(ispcJobVel);

                unsafe
                {
                    // ★ 使用 GetUnsafePtr() 获取 NativeArray 的真实缓冲区指针，而非 ToArray() 的副本
                    float2* posPtr = (float2*)ispcJobPos.GetUnsafePtr();
                    float2* velPtr = (float2*)ispcJobVel.GetUnsafePtr();

                    // 直接调用 DllImport
                    float dt = DT;
                    float vpw = VIEWPORT_WIDTH;
                    float vph = VIEWPORT_HEIGHT;
                    int count = ENTITY_COUNT;
                    NativeTranspiler.Bindings.NativeExports.MoveEntitiesJob_NativeIspc_Execute_Batch(
                        0, ENTITY_COUNT,
                        posPtr, ENTITY_COUNT,
                        velPtr, ENTITY_COUNT,
                        &dt, &vpw, &vph, &count);
                }
                MoveEntitiesTest.VerifyResults(dllRefPos, ispcJobPos, "ISPC Direct DllImport");

                dllRefPos.Dispose();
                dllRefVel.Dispose();
            }

            // 清理
            initialPos.Dispose();
            initialVel.Dispose();
            refPos.Dispose();
            refVel.Dispose();
            ispcJobPos.Dispose();
            ispcJobVel.Dispose();
            ispcStaticPos.Dispose();
            ispcStaticVel.Dispose();
        }
    }
}
