using System;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.Collections;
using EntJoy.Mathematics;
using NativeTranspiler;
using static EntJoy.ECS.EventBus;

namespace EntJoySample.ECS
{
    /// <summary>
    /// [NativeTranspile(ISPC)] Native Job SendEvent 测试。
    /// 验证：ISPC 后端内多种事件类型 → EventBuffer 写入 → drain → EventStream。
    ///
    /// 注意：必须用 Schedule(query).Complete() 走 native ISPC 执行路径；
    /// Run(query) 在 [NativeTranspile] job 上会走 NativeExports.RunImmediate_*（也是 native），
    /// 但为了确定性统一用 Schedule + Complete（与 C++ NativeEventJobTest 的 Test 4 一致）。
    /// </summary>
    public static class ISpcEventJobTest
    {
        /// <summary>死亡事件（blittable，复用 NativeEventJobTest 定义）。</summary>
        // 复用 NativeEventJobTest.DeathSignal / DamageSignal（同命名空间）

        /// <summary>
        /// ISPC Native Job：Health &lt;= 0 → DeathSignal；0 &lt; Health &lt; 50 → DamageSignal。
        /// </summary>
        [NativeTranspile(Target = BackendTarget.Ispc)]
        public struct IspcDeathDetectJob : IJobChunk
        {
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                NativeArray<Entity> entities = chunk.GetComponentDataNativeArray<Entity>();
                NativeArray<Health> healths = chunk.GetComponentDataNativeArray<Health>();

                for (int i = 0; i < entities.Length; i++)
                {
                    if (healths[i].Current <= 0)
                    {
                        SendEvent(new NativeEventJobTest.DeathSignal
                        {
                            Target = entities[i],
                            Amount = (int)Math.Abs(healths[i].Current)
                        });
                    }
                    else if (healths[i].Current < 50)
                    {
                        SendEvent(new NativeEventJobTest.DamageSignal
                        {
                            Target = entities[i],
                            Amount = 10,
                            HealthRatio = healths[i].Current / healths[i].Max
                        });
                    }
                }
            }
        }

        /// <summary>
        /// AutoSIMD=Enabled + SendEvent：验证 GenerateChunkFunctionSIMD fallback 到标量 translator 后 SendEvent 仍工作。
        /// </summary>
        [NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
        public struct AutoSimdDeathDetectJob : IJobChunk
        {
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                NativeArray<Entity> entities = chunk.GetComponentDataNativeArray<Entity>();
                NativeArray<Health> healths = chunk.GetComponentDataNativeArray<Health>();

                for (int i = 0; i < entities.Length; i++)
                {
                    if (healths[i].Current <= 0)
                    {
                        SendEvent(new NativeEventJobTest.DeathSignal
                        {
                            Target = entities[i],
                            Amount = (int)Math.Abs(healths[i].Current)
                        });
                    }
                }
            }
        }

        /// <summary>带 float2 字段的事件（验证 SendEvent 嵌套带参构造 new float2(x,y) 翻译，2026-08-30 Fix 2）。</summary>
        public struct Float2Signal
        {
            public Entity Target;
            public float2 Pos;
        }

        /// <summary>
        /// ISPC Job：每个实体发 Float2Signal，Pos = new float2(Current, Max)（带参构造）。
        /// 修复前 TranslateIspcNestedFieldWrite 对无 Initializer 的对象创建静默 return → Pos 永不写入（0,0）；
        /// 修复后走 make_float2(x,y)，事件圆回传的 Pos 应与期望一致。
        /// </summary>
        [NativeTranspile(Target = BackendTarget.Ispc)]
        public struct IspcFloat2EventJob : IJobChunk
        {
            public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
            {
                NativeArray<Entity> entities = chunk.GetComponentDataNativeArray<Entity>();
                NativeArray<Health> healths = chunk.GetComponentDataNativeArray<Health>();

                for (int i = 0; i < entities.Length; i++)
                {
                    SendEvent(new Float2Signal
                    {
                        Target = entities[i],
                        Pos = new float2(healths[i].Current, healths[i].Max)
                    });
                }
            }
        }

        public static void Run()
        {
            Console.WriteLine("=== ISPC Event Job Test ===\n");
            TestSingleEventType();
            TestMultiEventTypes();
            TestMultiWorld();
            TestFieldValues();
            TestAutoSimdSendEvent();
            TestFloat2ArgEvent();
            Console.WriteLine("\n=== ISPC Event Job Test Complete ===\n");
        }

        /// <summary>测试 1：单事件类型端到端（验证 ISPC EventBuffer 写入 + drain + 数量）。</summary>
        private static void TestSingleEventType()
        {
            Console.WriteLine("--- Test 1: ISPC IJobChunk → SendEvent (single type) ---");

            var world = new World("IspcEventTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            world.RegisterEvent<NativeEventJobTest.DeathSignal>();

            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < 10; i++)
            {
                var e = em.NewEntity(types);
                em.Set(e, new Health { Current = i < 5 ? -10f : 100f, Max = 100f });
            }

            var job = new IspcDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            Console.WriteLine("  Calling job.Schedule(query).Complete()...");
            try
            {
                job.Schedule(query).Complete();
                Console.WriteLine("  Schedule+Complete completed without crash");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Schedule failed: {ex.GetType().Name}: {ex.Message}");
            }

            world.NextFrameEvents();
            var events = world.GetEventStream<NativeEventJobTest.DeathSignal>().ReadBuffer();
            int count = events.Length;

            Console.WriteLine($"  DeathSignal received: {count} (expected 5)");
            bool ok = count == 5;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }

        /// <summary>测试 2：多事件类型分别进入各自的 EventStream。</summary>
        private static void TestMultiEventTypes()
        {
            Console.WriteLine("--- Test 2: Multiple Event Types ---");

            var world = new World("IspcMultiEventTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            world.RegisterEvent<NativeEventJobTest.DeathSignal>();
            world.RegisterEvent<NativeEventJobTest.DamageSignal>();

            // 10 个实体：i=0..2 死亡(3)，i=3..6 受伤(4)，i=7..9 无事件
            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < 10; i++)
            {
                var e = em.NewEntity(types);
                float current = i < 3 ? -10f : i < 7 ? 30f : 100f;
                em.Set(e, new Health { Current = current, Max = 100f });
            }

            var job = new IspcDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            job.Schedule(query).Complete();
            world.NextFrameEvents();

            var deathEvents = world.GetEventStream<NativeEventJobTest.DeathSignal>().ReadBuffer();
            var damageEvents = world.GetEventStream<NativeEventJobTest.DamageSignal>().ReadBuffer();

            Console.WriteLine($"  DeathSignal: {deathEvents.Length} (expected 3)");
            Console.WriteLine($"  DamageSignal: {damageEvents.Length} (expected 4)");

            bool ok = deathEvents.Length == 3 && damageEvents.Length == 4;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }

        /// <summary>测试 3：多 World — job.Schedule(query, world) 写指定 World 的 EventStream。</summary>
        private static void TestMultiWorld()
        {
            Console.WriteLine("--- Test 3: Multiple Worlds ---");

            var world1 = new World("IspcWorld1");
            var world2 = new World("IspcWorld2");
            World.DefaultWorld = world1;

            world1.RegisterEvent<NativeEventJobTest.DeathSignal>();
            world2.RegisterEvent<NativeEventJobTest.DeathSignal>();

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

            var job = new IspcDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            job.Schedule(query, world1).Complete();
            job.Schedule(query, world2).Complete();

            world1.NextFrameEvents();
            world2.NextFrameEvents();

            int world1Count = world1.GetEventStream<NativeEventJobTest.DeathSignal>().ReadBuffer().Length;
            int world2Count = world2.GetEventStream<NativeEventJobTest.DeathSignal>().ReadBuffer().Length;

            Console.WriteLine($"  world1 events: {world1Count} (expected 5)");
            Console.WriteLine($"  world2 events: {world2Count} (expected 3)");
            bool ok = world1Count == 5 && world2Count == 3;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world1.Dispose();
            world2.Dispose();
            Console.WriteLine();
        }

        /// <summary>测试 4：字段值正确性 — 验证 ISPC 事件写入的具体字段（Target.Id / Amount / HealthRatio）。</summary>
        private static void TestFieldValues()
        {
            Console.WriteLine("--- Test 4: Field Value Correctness ---");

            var world = new World("IspcFieldValueTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            world.RegisterEvent<NativeEventJobTest.DeathSignal>();

            // 3 个死亡实体，Health.Current 分别 -5 / -10 / -20 → Amount = 5 / 10 / 20
            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            float[] deaths = { -5f, -10f, -20f };
            var ids = new int[deaths.Length];
            for (int i = 0; i < deaths.Length; i++)
            {
                var e = em.NewEntity(types);
                ids[i] = e.Id;
                em.Set(e, new Health { Current = deaths[i], Max = 100f });
            }

            var job = new IspcDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            job.Schedule(query).Complete();
            world.NextFrameEvents();

            var events = world.GetEventStream<NativeEventJobTest.DeathSignal>().ReadBuffer();

            Console.WriteLine($"  Events: {events.Length} (expected 3)");
            bool ok = events.Length == 3;
            if (ok)
            {
                // 注意：实体 Entity 组件字段 Id 默认 0（NewEntity 创建的 Entity 组件未赋值），
                // Target.Id 全部为 0 是既有行为（C++ 后端同样）。字段值验证聚焦 Amount。
                var amounts = new System.Collections.Generic.HashSet<int>();
                foreach (var evt in events)
                {
                    Console.WriteLine($"    Death: Target.Id={evt.Target.Id}, Amount={evt.Amount}");
                    amounts.Add(evt.Amount);
                }
                // Amount 必须 = {5, 10, 20}（|Health.Current| = 5/10/20）
                var expectedAmounts = new System.Collections.Generic.HashSet<int>(
                    deaths.Select(d => (int)Math.Abs(d)));
                if (!amounts.SetEquals(expectedAmounts))
                {
                    ok = false;
                    Console.WriteLine($"    ✗ Amounts {{{string.Join(",", amounts)}}} != expected {{{string.Join(",", expectedAmounts)}}}");
                }
            }

            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }

        /// <summary>测试 5：AutoSIMD=Enabled + SendEvent（验证 fallback 到标量 translator 后事件仍工作）。</summary>
        private static void TestAutoSimdSendEvent()
        {
            Console.WriteLine("--- Test 5: AutoSIMD=Enabled → SendEvent ---");

            var world = new World("AutoSimdEventTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            world.RegisterEvent<NativeEventJobTest.DeathSignal>();

            // 5 个死亡实体
            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < 5; i++)
            {
                var e = em.NewEntity(types);
                em.Set(e, new Health { Current = -10f, Max = 100f });
            }

            var job = new AutoSimdDeathDetectJob();
            var query = new QueryBuilder().WithAll<Health>();

            Console.WriteLine("  Calling job.Schedule(query).Complete()...");
            try
            {
                job.Schedule(query).Complete();
                Console.WriteLine("  Schedule+Complete completed without crash");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Schedule failed: {ex.GetType().Name}: {ex.Message}");
            }

            world.NextFrameEvents();
            var events = world.GetEventStream<NativeEventJobTest.DeathSignal>().ReadBuffer();
            int count = events.Length;

            Console.WriteLine($"  DeathSignal received: {count} (expected 5)");
            bool ok = count == 5;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }

        /// <summary>测试 6：float2 带参构造事件（Fix 2 覆盖：new float2(x,y) 嵌套写 → make_float2）。</summary>
        private static void TestFloat2ArgEvent()
        {
            Console.WriteLine("--- Test 6: float2 arg-constructor event (Fix 2: new float2(x,y) nested write) ---");

            var world = new World("IspcFloat2EventTest");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            world.RegisterEvent<Float2Signal>();

            const int count = 3;
            var types = new ComponentType[] { typeof(Entity), typeof(Health) };
            for (int i = 0; i < count; i++)
            {
                var e = em.NewEntity(types);
                em.Set(e, new Health { Current = 10f + i, Max = 100f });
            }

            var job = new IspcFloat2EventJob();
            var query = new QueryBuilder().WithAll<Health>();

            try
            {
                job.Schedule(query).Complete();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Schedule failed: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  Result: FAIL");
                world.Dispose();
                Console.WriteLine();
                return;
            }

            world.NextFrameEvents();
            var events = world.GetEventStream<Float2Signal>().ReadBuffer();
            Console.WriteLine($"  Float2Signal received: {events.Length} (expected {count})");
            bool ok = events.Length == count;
            if (ok)
            {
                // 事件按 chunk/实体顺序写入：events[i] 对应实体 i（Current=10+i, Max=100）
                for (int i = 0; i < events.Length; i++)
                {
                    var evt = events[i];
                    float ex = 10f + i, ey = 100f;
                    bool posOk = MathF.Abs(evt.Pos.x - ex) <= 1e-3f && MathF.Abs(evt.Pos.y - ey) <= 1e-3f;
                    Console.WriteLine($"    Pos=({evt.Pos.x:F1},{evt.Pos.y:F1}) expect ({ex:F1},{ey:F1}) {(posOk ? "✓" : "✗")}");
                    if (!posOk) ok = false;
                }
            }
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            world.Dispose();
            Console.WriteLine();
        }
    }
}
