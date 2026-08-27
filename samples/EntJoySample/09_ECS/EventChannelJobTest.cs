using System;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;

namespace EntJoySample.ECS
{
    /// <summary>
    /// Managed Job 路径 Event Channel 测试：
    /// IJobChunk.Run 从主线程直接执行 SendEvent，验证 Event Channel 与 Job 体系集成。
    /// </summary>
    public static class EventChannelJobTest
    {
        private struct DamageSignal : IComponentData
        {
            public Entity Target;
            public int Amount;
        }

        /// <summary>Job：遍历所有 Health &lt; 0 的实体，发送 DamageSignal。</summary>
        private struct DeathDetectJob : IJobChunk
        {
            public World World;

            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining |
                System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                var entities = chunk.GetEntitySpan();
                var healths = chunk.GetComponentDataSpan<Health>();

                for (int i = 0; i < chunk.Count; i++)
                {
                    if (healths[i].Current <= 0)
                    {
                        World.SendEvent(new DamageSignal
                        {
                            Target = entities[i],
                            Amount = (int)Math.Abs(healths[i].Current)
                        });
                    }
                }
            }
        }

        public static void Run()
        {
            Console.WriteLine("=== Event Channel Job Test ===\n");

            TestManagedJobSendEvent();

            Console.WriteLine("\n=== Event Channel Job Test Complete ===\n");
        }

        private static void TestManagedJobSendEvent()
        {
            Console.WriteLine("--- Test: Managed IJobChunk → SendEvent ---");

            var world = new World("JobEventTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 注册事件
            world.RegisterEvent<DamageSignal>();

            // 创建 10 个实体，前 5 个 Health <= 0
            var types = new ComponentType[] { typeof(Health) };
            for (int i = 0; i < 10; i++)
            {
                var e = em.NewEntity(types);
                em.Set(e, new Health
                {
                    Current = i < 5 ? -10f : 100f,
                    Max = 100f
                });
            }

            // 构建 Job + Run（主线程同步执行）
            var job = new DeathDetectJob { World = world };
            var builder = new QueryBuilder().WithAll<Health>();
            job.Run(builder);

            // NextFrame → 事件可读
            world.NextFrameEvents();

            // 读取事件
            var events = world.GetEventStream<DamageSignal>().ReadBuffer();

            int count = 0;
            foreach (var evt in events)
            {
                count++;
                Console.WriteLine($"  Signal: Target.Id={evt.Target.Id}, Amount={evt.Amount}");
            }

            Console.WriteLine($"  Events received: {count} (expected 5)");
            bool ok = count == 5;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");

            world.Dispose();
            Console.WriteLine();
        }
    }
}
