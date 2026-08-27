using System;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.Collections;
using NativeTranspiler;
using static EntJoy.ECS.EventBus;

namespace EntJoySample.ECS
{
    /// <summary>
    /// [NativeTranspile] Native Job SendEvent 测试。
    /// 验证：C++ 内多种事件类型 → 各自 EventBuffer → drain → 各自 EventStream。
    /// </summary>
    public static class NativeEventJobTest
    {
        /// <summary>死亡事件（blittable）。</summary>
        public struct DeathSignal
        {
            public Entity Target;
            public int Amount;
        }

        /// <summary>受伤事件（blittable）。</summary>
        public struct DamageSignal
        {
            public Entity Target;
            public int Amount;
            public float HealthRatio;
        }

        /// <summary>
        /// Native Job：
        ///   Health &lt;= 0  → DeathSignal
        ///   0 &lt; Health &lt; 50 → DamageSignal
        /// 验证多事件类型分别写入独立的 EventBuffer。
        /// </summary>
        [NativeTranspile(Target = BackendTarget.Cpp)]
        public struct NativeDeathDetectJob : IJobChunk
        {
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                NativeArray<Entity> entities = chunk.GetComponentDataNativeArray<Entity>();
                NativeArray<Health> healths = chunk.GetComponentDataNativeArray<Health>();

                for (int i = 0; i < entities.Length; i++)
                {
                    if (healths[i].Current <= 0)
                    {
                        SendEvent(new DeathSignal
                        {
                            Target = entities[i],
                            Amount = (int)Math.Abs(healths[i].Current)
                        });
                    }
                    else if (healths[i].Current < 50)
                    {
                        SendEvent(new DamageSignal
                        {
                            Target = entities[i],
                            Amount = 10,
                            HealthRatio = healths[i].Current / healths[i].Max
                        });
                    }
                }
            }
        }

        public static void Run()
        {
            Console.WriteLine("=== Native Event Job Test ===\n");
            TestNativeJobSendEvent();
            TestMultiEventTypes();
            TestMultiWorld();
            TestAsyncSchedule();
            Console.WriteLine("\n=== Native Event Job Test Complete ===\n");
        }

        /// <summary>测试 4：异步 Schedule + Complete 路径 drain。</summary>
        private static void TestAsyncSchedule()
        {
            Console.WriteLine("--- Test 4: Async Schedule + Complete ---");

            var world = new World("NativeAsyncTest");
            var em = world.EntityManager;

            world.RegisterEvent<DeathSignal>();

            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < 5; i++)
            {
                var e = em.NewEntity(types);
                em.Set(e, new Health { Current = -10f, Max = 100f });
            }

            var job = new NativeDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            // 1. Schedule 异步执行
            job.Schedule(query).Complete();    // C++ 执行完 → ChunkCleanup 自动 drain

            world.NextFrameEvents();
            int count = world.GetEventStream<DeathSignal>().ReadBuffer().Length;

            Console.WriteLine($"  Events after Schedule+Complete+Drain: {count} (expected 5)");
            bool ok = count == 5;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }

        /// <summary>测试 1：单事件类型端到端。</summary>
        private static void TestNativeJobSendEvent()
        {
            Console.WriteLine("--- Test 1: [NativeTranspile] IJobChunk → ECS.SendEvent ---");

            var world = new World("NativeEventTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            world.RegisterEvent<DeathSignal>();

            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < 10; i++)
            {
                var e = em.NewEntity(types);
                em.Set(e, new Health { Current = i < 5 ? -10f : 100f, Max = 100f });
            }

            var job = new NativeDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            Console.WriteLine("  Calling job.Run(query)...");
            try
            {
                job.Run(query);
                Console.WriteLine("  Run completed without crash");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Run failed: {ex.GetType().Name}: {ex.Message}");
            }

            world.NextFrameEvents();

            var events = world.GetEventStream<DeathSignal>().ReadBuffer();
            int count = events.Length;

            Console.WriteLine($"  Events received: {count} (expected 5)");
            bool ok = count == 5;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }

        /// <summary>测试 2：多种事件类型分别进入各自的 EventStream。</summary>
        private static void TestMultiEventTypes()
        {
            Console.WriteLine("--- Test 2: Multiple Event Types ---");

            var world = new World("NativeMultiEventTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            world.RegisterEvent<DeathSignal>();
            world.RegisterEvent<DamageSignal>();

            // 10 个实体：
            //   i=0..2  Health = -10   → 死亡（DeathSignal，3 个）
            //   i=3..6  Health = 30    → 受伤（DamageSignal，4 个）
            //   i=7..9  Health = 100   → 无事件
            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < 10; i++)
            {
                var e = em.NewEntity(types);
                float current = i < 3 ? -10f : i < 7 ? 30f : 100f;
                em.Set(e, new Health { Current = current, Max = 100f });
            }

            var job = new NativeDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            job.Run(query);
            world.NextFrameEvents();

            var deathEvents = world.GetEventStream<DeathSignal>().ReadBuffer();
            var damageEvents = world.GetEventStream<DamageSignal>().ReadBuffer();

            Console.WriteLine($"  DeathSignal: {deathEvents.Length} (expected 3)");
            foreach (var evt in deathEvents)
                Console.WriteLine($"    Death: Target.Id={evt.Target.Id}, Amount={evt.Amount}");

            Console.WriteLine($"  DamageSignal: {damageEvents.Length} (expected 4)");
            foreach (var evt in damageEvents)
                Console.WriteLine($"    Damage: Target.Id={evt.Target.Id}, Amount={evt.Amount}, Ratio={evt.HealthRatio:F2}");

            bool ok = deathEvents.Length == 3 && damageEvents.Length == 4;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }
    /// <summary>测试 3：多 World — job.Run(query, world) 写指定 World 的 EventStream。</summary>
        private static void TestMultiWorld()
        {
            Console.WriteLine("--- Test 3: Multiple Worlds ---");

            var world1 = new World("NativeWorld1");
            var world2 = new World("NativeWorld2");
            World.DefaultWorld = world1;

            world1.RegisterEvent<DeathSignal>();
            world2.RegisterEvent<DeathSignal>();

            // world1: 5 个死亡实体；world2: 3 个死亡实体
            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < 5; i++)
            {
                var e = world1.EntityManager.NewEntity(types);
                world1.EntityManager.Set(e, new Health { Current = -10f, Max = 100f });
            }
            for (int i = 0; i < 3; i++)
            {
                var e = world2.EntityManager.NewEntity(types);
                world2.EntityManager.Set(e, new Health { Current = -10f, Max = 100f });
            }

            var job = new NativeDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            // 显式指定 World 调度（不依赖 DefaultWorld）
            job.Run(query, world1);
            job.Run(query, world2);

            world1.NextFrameEvents();
            world2.NextFrameEvents();

            int world1Count = world1.GetEventStream<DeathSignal>().ReadBuffer().Length;
            int world2Count = world2.GetEventStream<DeathSignal>().ReadBuffer().Length;

            Console.WriteLine($"  world1 events: {world1Count} (expected 5)");
            Console.WriteLine($"  world2 events: {world2Count} (expected 3)");
            bool ok = world1Count == 5 && world2Count == 3;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world1.Dispose();
            world2.Dispose();
            Console.WriteLine();
        }
    }
}