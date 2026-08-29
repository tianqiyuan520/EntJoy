using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// Event Channel 测试：零结构变更的系统间事件传递。
    ///
    /// 双缓冲模式：SendEvent 写 buffer[0]，NextFrame swap，
    /// 下一帧 ReadBuffer 读 buffer[1]（上一帧的事件）。
    /// </summary>
    public static class EventChannelDemo
    {
        private static World _world = null!;

        public static void Run()
        {
            Console.WriteLine("=== Event Channel Demo ===\n");

            _world = new World("EventChannelDemo");

            TestSingleProducerSingleConsumer();
            TestMultipleConsumers();
            TestNextFrameClearsOldEvents();
            TestCapacityOverflow();
            TestMultiFrame();

            _world.Dispose();
            Console.WriteLine("\n=== Event Channel Demo Complete ===\n");
        }

        /// <summary>测试 1：SendEvent → NextFrame → 消费者读取。</summary>
        private static void TestSingleProducerSingleConsumer()
        {
            Console.WriteLine("--- Test 1: Single Producer + Single Consumer ---");

            // 帧 N：生产 3 个事件
            _world.SendEvent(new DamageEvent { Target = new Entity { Id = 1, Version = 0 }, Amount = 10 });
            _world.SendEvent(new DamageEvent { Target = new Entity { Id = 2, Version = 0 }, Amount = 20 });
            _world.SendEvent(new DamageEvent { Target = new Entity { Id = 3, Version = 0 }, Amount = 30 });

            _world.NextFrameEvents();

            // 帧 N+1：读取
            var events = _world.GetEventStream<DamageEvent>().ReadBuffer();
            int totalDamage = 0;
            foreach (var evt in events)
                totalDamage += evt.Amount;

            Console.WriteLine($"  Events read: {events.Length} (expected 3)");
            Console.WriteLine($"  Total damage: {totalDamage} (expected 60)");
            bool ok = events.Length == 3 && totalDamage == 60;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>测试 2：两个消费者读取同一事件流（每帧只读一次，不需要独立 reader）。</summary>
        private static void TestMultipleConsumers()
        {
            Console.WriteLine("--- Test 2: Multiple Consumers ---");

            _world.SendEvent(new CollisionEvent
            {
                A = new Entity { Id = 10, Version = 0 },
                B = new Entity { Id = 11, Version = 0 },
                Force = 5.0f
            });
            _world.SendEvent(new CollisionEvent
            {
                A = new Entity { Id = 20, Version = 0 },
                B = new Entity { Id = 21, Version = 0 },
                Force = 8.0f
            });

            _world.NextFrameEvents();

            // 每个 System 每帧调一次 ReadBuffer，拿到完整事件列表
            var eventsA = _world.GetEventStream<CollisionEvent>().ReadBuffer();
            var eventsB = _world.GetEventStream<CollisionEvent>().ReadBuffer();

            Console.WriteLine($"  Consumer A read: {eventsA.Length} (expected 2)");
            Console.WriteLine($"  Consumer B read: {eventsB.Length} (expected 2)");
            bool ok = eventsA.Length == 2 && eventsB.Length == 2;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>测试 3：NextFrame 后旧事件清空。</summary>
        private static void TestNextFrameClearsOldEvents()
        {
            Console.WriteLine("--- Test 3: NextFrame Clears Old Events ---");

            _world.SendEvent(new DamageEvent { Target = new Entity { Id = 100, Version = 0 }, Amount = 100 });
            _world.NextFrameEvents();

            var events1 = _world.GetEventStream<DamageEvent>().ReadBuffer();
            int countFrameN = events1.Length;

            _world.NextFrameEvents();

            var events2 = _world.GetEventStream<DamageEvent>().ReadBuffer();
            int countFrameN2 = events2.Length;

            Console.WriteLine($"  Frame N+1 read: {countFrameN} (expected 1)");
            Console.WriteLine($"  Frame N+2 read: {countFrameN2} (expected 0)");
            bool ok = countFrameN == 1 && countFrameN2 == 0;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>测试 4：超过容量时丢弃事件。</summary>
        private static void TestCapacityOverflow()
        {
            Console.WriteLine("--- Test 4: Capacity Overflow ---");

            var smallStream = new EventStream<DamageEvent>(capacity: 5);

            int sent = 0;
            for (int i = 0; i < 8; i++)
            {
                if (smallStream.SendEvent(new DamageEvent { Target = new Entity { Id = i, Version = 0 }, Amount = i }))
                    sent++;
            }

            smallStream.NextFrame();
            var buffer = smallStream.ReadBuffer();

            Console.WriteLine($"  Sent: {sent} (expected 5, 3 discarded)");
            Console.WriteLine($"  Buffered after NextFrame: {buffer.Length} (expected 5)");
            bool ok = sent == 5 && buffer.Length == 5;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>测试 5：多帧连续生产消费。</summary>
        private static void TestMultiFrame()
        {
            Console.WriteLine("--- Test 5: Multi-Frame Production + Consumption ---");

            int totalRead = 0;

            for (int frame = 0; frame < 4; frame++)
            {
                for (int i = 0; i <= frame; i++)
                {
                    _world.SendEvent(new DamageEvent
                    {
                        Target = new Entity { Id = frame * 10 + i, Version = 0 },
                        Amount = (frame + 1) * 10
                    });
                }

                _world.NextFrameEvents();

                totalRead += _world.GetEventStream<DamageEvent>().ReadBuffer().Length;
            }

            Console.WriteLine($"  Total events read across frames: {totalRead} (expected 10)");
            bool ok = totalRead == 10;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }
    }
}
