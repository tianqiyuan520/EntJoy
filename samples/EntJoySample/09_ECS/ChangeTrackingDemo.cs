using System;
using System.Diagnostics;
using EntJoy.ECS;

namespace EntJoySample.ECS
{
    /// <summary>
    /// Change Tracking 测试案例。
    /// 测试 Chunk 级版本号和实体级变更追踪。
    /// </summary>
    public static class ChangeTrackingDemo
    {
        public static void Run()
        {
            Console.WriteLine("=== Change Tracking Demo ===\n");

            var world = new World();
            var entityManager = world.EntityManager;

            // 创建测试实体
            Console.WriteLine("Creating test entities...");
            const int entityCount = 1000;
            var entities = new Entity[entityCount];

            for (int i = 0; i < entityCount; i++)
            {
                entities[i] = entityManager.NewEntity(
                    new ComponentType[] { typeof(Position), typeof(Velocity) });
                entityManager.Set(entities[i], new Position { X = i, Y = i * 2 });
                entityManager.Set(entities[i], new Velocity { X = 1, Y = 1 });
            }

            Console.WriteLine($"Created {entityCount} entities.\n");

            // 测试 1: Chunk 级版本号
            TestChunkVersion(entityManager, entities);

            // 测试 2: 实体级变更追踪
            TestEntityLevelChangeTracking(entityManager, entities);

            // 测试 3: 帧级变更查询
            TestFrameLevelQuery(entityManager, entities);

            Console.WriteLine("\n=== Change Tracking Demo Complete ===\n");
        }

        /// <summary>
        /// 测试 Chunk 级版本号。
        /// </summary>
        private static void TestChunkVersion(EntityManager entityManager, Entity[] entities)
        {
            Console.WriteLine("--- Test 1: Chunk Version ---");

            // 获取初始版本号
            int initialVersion = entityManager.Archetypes[0].GlobalVersion;
            Console.WriteLine($"Initial global version: {initialVersion}");

            // 修改一些实体
            Console.WriteLine("Modifying 100 entities...");
            for (int i = 0; i < 100; i++)
            {
                entityManager.Set(entities[i], new Position { X = i * 10, Y = i * 20 });
            }

            // 检查版本号是否递增
            int newVersion = entityManager.Archetypes[0].GlobalVersion;
            Console.WriteLine($"Global version after modifications: {newVersion}");
            Console.WriteLine($"Version incremented: {newVersion > initialVersion}");

            // 检查 Chunk 版本号
            var archetype = entityManager.Archetypes[0];
            bool anyChunkChanged = false;
            foreach (var chunk in archetype.ChunkSpan)
            {
                if (chunk.HasChangesSince(initialVersion))
                {
                    anyChunkChanged = true;
                    Console.WriteLine($"Chunk version: {chunk.Version} (changed since {initialVersion})");
                    break;
                }
            }
            Console.WriteLine($"Any chunk changed: {anyChunkChanged}\n");
        }

        /// <summary>
        /// 测试实体级变更追踪。
        /// </summary>
        private static void TestEntityLevelChangeTracking(EntityManager entityManager, Entity[] entities)
        {
            Console.WriteLine("--- Test 2: Entity Level Change Tracking ---");

            // 获取 Archetype
            var archetype = entityManager.Archetypes[0];
            var chunks = archetype.ChunkSpan;

            // 清除所有变更标记
            archetype.ClearAllChangedBitMasks();
            Console.WriteLine("Cleared all change masks.");

            // 标记一些实体为已修改
            Console.WriteLine("Marking 50 entities as changed...");
            for (int i = 0; i < 50; i++)
            {
                // 获取实体信息
                ref var info = ref entityManager.GetEntityInfoRef(entities[i].Id);
                if (info.Archetype != null)
                {
                    var chunkList = info.Archetype.ChunkSpan;
                    if (info.ChunkIndex >= 0 && info.ChunkIndex < chunkList.Length)
                    {
                        var chunk = chunkList[info.ChunkIndex];
                        chunk.MarkEntityChanged(info.SlotInChunk);
                    }
                }
            }

            // 检查变更标记
            int changedCount = 0;
            foreach (var chunk in chunks)
            {
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    if (chunk.IsEntityChanged(i))
                    {
                        changedCount++;
                    }
                }
            }
            Console.WriteLine($"Entities marked as changed: {changedCount}");
            Console.WriteLine($"Expected: 50\n");
        }

        /// <summary>
        /// 测试帧级变更查询。
        /// </summary>
        private static void TestFrameLevelQuery(EntityManager entityManager, Entity[] entities)
        {
            Console.WriteLine("--- Test 3: Frame Level Query ---");

            // 记录初始帧版本
            int frameStartVersion = entityManager.Archetypes[0].GlobalVersion;
            Console.WriteLine($"Frame start version: {frameStartVersion}");

            // 模拟一帧：修改一些实体
            Console.WriteLine("Simulating frame: modifying 200 entities...");
            for (int i = 0; i < 200; i++)
            {
                entityManager.Set(entities[i], new Position { X = i * 100, Y = i * 200 });
            }

            int frameEndVersion = entityManager.Archetypes[0].GlobalVersion;
            Console.WriteLine($"Frame end version: {frameEndVersion}");

            // 查询本帧修改过的实体
            int queriedCount = 0;
            var archetype = entityManager.Archetypes[0];
            foreach (var chunk in archetype.ChunkSpan)
            {
                if (!chunk.HasChangesSince(frameStartVersion))
                    continue;

                // Chunk 有变更，计入实体数
                queriedCount += chunk.EntityCount;
            }
            Console.WriteLine($"Chunks with changes queried: contains {queriedCount} entities");
            Console.WriteLine($"(Chunk-level filtering: only chunks modified since frame start are included)\n");
        }
    }
}
