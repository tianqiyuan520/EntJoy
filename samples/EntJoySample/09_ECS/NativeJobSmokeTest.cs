using System.Diagnostics;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using NativeTranspiler;

namespace EntJoySample.ECS
{
    /// <summary>
    /// 验证 [NativeTranspile] IJobChunk 的 Schedule（并行）与 Run（ImmediateNative 直执，零 worker 唤醒）。
    /// </summary>
    public static class NativeJobSmokeTest
    {
        [NativeTranspile(Target = BackendTarget.Cpp)]
        public struct NativeMoveJob : IJobChunk
        {
            public float DeltaTime;

            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                EntJoy.Collections.NativeArray<Position> positions = chunk.GetComponentDataNativeArray<Position>();
                for (int i = 0; i < positions.Length; i++)
                {
                    var p = positions[i];
                    p.X += DeltaTime;
                    positions[i] = p;
                }
            }
        }

        public static void Run()
        {
            Console.WriteLine("=== NativeTranspile IJobChunk Smoke Test ===\n");

            var world = new World();
            World.DefaultWorld = world;
            var em = world.EntityManager;

            const int count = 1000;
            var entities = new Entity[count];
            for (int i = 0; i < count; i++)
            {
                entities[i] = em.NewEntity(new ComponentType[] { typeof(Position), typeof(Velocity) });
                em.Set(entities[i], new Position { X = 0, Y = 0 });
                em.Set(entities[i], new Velocity { X = 1, Y = 1 });
            }

            var query = new QueryBuilder().WithAll<Position, Velocity>();
            var job = new NativeMoveJob { DeltaTime = 1f };

            // 1) Schedule + Complete（并行 worker 路径，行为回归验证）
            job.Schedule(query).Complete();
            long sumAfterSchedule = SumX(em, entities);
            Console.WriteLine($"Schedule path  : sumX={sumAfterSchedule}");

            // 2) Run（ImmediateNative：主线程直执翻译后 C++，零 worker 唤醒）
            job.Run(query);
            long sumAfterRun = SumX(em, entities);
            Console.WriteLine($"Run path       : sumX={sumAfterRun}");

            bool ok = sumAfterSchedule == count && sumAfterRun == count * 2;
            Console.WriteLine($"Correctness    : {(ok ? "PASS" : "FAIL")}");

            // Run 性能（100 轮）
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) job.Run(query);
            sw.Stop();
            Console.WriteLine($"Run 100x       : {sw.Elapsed.TotalMilliseconds:F2} ms");

            Console.WriteLine("\n=== Smoke Test Complete ===\n");
        }

        private static long SumX(EntityManager em, Entity[] entities)
        {
            long s = 0;
            foreach (var e in entities)
                s += (long)em.GetComponent<Position>(e).X;
            return s;
        }
    }
}