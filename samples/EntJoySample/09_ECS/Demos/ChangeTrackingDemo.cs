using System;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// Change Tracking 测试：Set 自动标记 + WithChanged 查询 + ClearAll。
    ///
    /// 演示：
    /// 1. Set&lt;T&gt; 后 changed bit 自动标记
    /// 2. ClearAllChangedBitMasks 清零
    /// 3. Set 部分实体后只有被修改的被标记
    /// </summary>
    public static class ChangeTrackingDemo
    {
        private static World _world = null!;

        public static void Run()
        {
            Console.WriteLine("=== Change Tracking Demo ===\n");

            _world = new World("ChangeTrackingDemo");
            var em = _world.EntityManager;

            TestSetMarksChanged(em);
            TestClearAll(em);
            TestPartialSet(em);

            _world.Dispose();
            Console.WriteLine("\n=== Change Tracking Demo Complete ===\n");
        }

        /// <summary>测试 1：Set 后 changed bit 自动标记。</summary>
        private static void TestSetMarksChanged(EntityManager em)
        {
            Console.WriteLine("--- Test 1: Set Marks Changed ---");

            var types = new ComponentType[] { typeof(Position), typeof(Velocity) };
            var e1 = em.NewEntity(types);
            var e2 = em.NewEntity(types);
            em.Set(e1, new Position { X = 0, Y = 0 });
            em.Set(e2, new Position { X = 1, Y = 1 });

            var info1 = em.GetEntityInfoRef(e1.Id);
            var info2 = em.GetEntityInfoRef(e2.Id);
            var chunk1 = info1.Archetype.ChunkList[info1.ChunkIndex];
            bool e1Changed = chunk1.IsEntityChanged(info1.SlotInChunk);
            bool e2Changed = chunk1.IsEntityChanged(info2.SlotInChunk);

            Console.WriteLine($"  e1 after Set: changed={e1Changed} (expected True)");
            Console.WriteLine($"  e2 after Set: changed={e2Changed} (expected True)");
            bool ok = e1Changed && e2Changed;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>测试 2：ClearAllChangedBitMasks 后 changed bit 清零。</summary>
        private static void TestClearAll(EntityManager em)
        {
            Console.WriteLine("--- Test 2: ClearAllChangedBitMasks ---");

            var types = new ComponentType[] { typeof(Position), typeof(Velocity) };
            var e = em.NewEntity(types);
            em.Set(e, new Position { X = 0, Y = 0 });

            var info = em.GetEntityInfoRef(e.Id);
            var chunk = info.Archetype.ChunkList[info.ChunkIndex];

            bool before = chunk.IsEntityChanged(info.SlotInChunk);
            Console.WriteLine($"  After Set: changed={before} (expected True)");

            em.ClearAllChangedBitMasks();
            bool after = chunk.IsEntityChanged(info.SlotInChunk);
            Console.WriteLine($"  After Clear: changed={after} (expected False)");

            bool ok = before && !after;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }

        /// <summary>测试 3：Clear 后只 Set 部分实体，只有被 Set 的被标记。</summary>
        private static void TestPartialSet(EntityManager em)
        {
            Console.WriteLine("--- Test 3: Partial Set ---");

            var types = new ComponentType[] { typeof(Position), typeof(Velocity) };
            var entities = new Entity[5];
            for (int i = 0; i < 5; i++)
                entities[i] = em.NewEntity(types);

            // Clear 后只修改 e[1] 和 e[3]
            em.ClearAllChangedBitMasks();
            em.Set(entities[1], new Position { X = 10, Y = 10 });
            em.Set(entities[3], new Position { X = 30, Y = 30 });

            int changedCount = 0;
            foreach (var e in entities)
            {
                var info = em.GetEntityInfoRef(e.Id);
                var chunk = info.Archetype.ChunkList[info.ChunkIndex];
                if (chunk.IsEntityChanged(info.SlotInChunk))
                    changedCount++;
            }
            Console.WriteLine($"  Changed entities: {changedCount} (expected 2)");
            bool ok = changedCount == 2;
            Console.WriteLine($"  Result: {(ok ? "PASS" : "FAIL")}");
            Console.WriteLine();
        }
    }
}
