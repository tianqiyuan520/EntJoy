using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct WorldProbeSystem : ISystem
    {
        public static string LastWorldName = "";
        public void OnUpdate() => LastWorldName = World.DefaultWorld.Name;
    }

    public class PrefabAndMultiWorldTests
    {
        [Fact]
        public void SpawnFrom_CopiesValues_AndInstancesAreIndependent()
        {
            var world = new World("PrefabTest");
            var prefab = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(Prefab));
            world.EntityManager.Set(prefab, new Position { X = 1, Y = 2 });

            var instances = world.SpawnFrom(prefab, 50);

            Assert.Equal(50, instances.Length);
            foreach (var e in instances)
            {
                ref var pos = ref world.EntityManager.GetComponent<Position>(e);
                Assert.Equal(1f, pos.X);
                Assert.Equal(2f, pos.Y);
            }

            // 改实例不影响 prefab 和其他实例（独立副本）
            world.EntityManager.GetComponent<Position>(instances[0]).X = 99;
            Assert.Equal(1f, world.EntityManager.GetComponent<Position>(prefab).X);
            Assert.Equal(1f, world.EntityManager.GetComponent<Position>(instances[1]).X);

            world.Dispose();
        }

        [Fact]
        public void Prefab_DefaultExcludedFromQuery()
        {
            var world = new World("PrefabExcludeTest");
            var prefab = world.EntityManager.NewEntity(typeof(Position), typeof(Velocity), typeof(Prefab));
            var instances = world.SpawnFrom(prefab, 10);

            int normalCount = 0;
            foreach (var _ in world.Query<Position, Velocity>()) normalCount++;
            Assert.Equal(10, normalCount);  // 只匹配实例，prefab 被排除

            int prefabCount = 0;
            foreach (var _ in world.Query<Position, Velocity>(
                new QueryBuilder().WithAll<Position, Velocity>().WithAll<Prefab>())) prefabCount++;
            Assert.Equal(1, prefabCount);   // 显式包含才匹配 prefab

            world.Dispose();
        }

        [Fact]
        public void SpawnFrom_DisposableComponent_Throws()
        {
            var world = new World("PrefabDisposableTest");
            var prefab = world.EntityManager.NewEntity(typeof(NativeBufferComponent), typeof(Prefab));

            Assert.Throws<InvalidOperationException>(() => world.SpawnFrom(prefab, 1));
            world.Dispose();
        }

        [Fact]
        public void MultiWorld_SystemsAreIsolated()
        {
            var worldA = new World("WorldA");
            var runnerA = new SystemRunner(worldA);
            runnerA.RegisterSystem<WorldProbeSystem>();

            var worldB = new World("WorldB");
            var runnerB = new SystemRunner(worldB);
            runnerB.RegisterSystem<WorldProbeSystem>();

            runnerA.Update();
            Assert.Equal("WorldA", WorldProbeSystem.LastWorldName);

            runnerB.Update();
            Assert.Equal("WorldB", WorldProbeSystem.LastWorldName);

            worldA.Dispose();
            worldB.Dispose();
        }

        [Fact]
        public void CompactChunks_ReducesChunkCount_KeepsEntities()
        {
            var world = new World("DefragTest");
            const int n = 2000;
            var entities = world.CreateEntities(n, typeof(Position), typeof(Velocity));
            for (int i = 0; i < n; i++)
                if (i % 5 != 0)
                    world.EntityManager.RemoveComponent<Velocity>(entities[i]);

            int chunksBefore = world.GetMemoryReport().TotalChunkCount;
            world.EntityManager.CompactChunks();
            int chunksAfter = world.GetMemoryReport().TotalChunkCount;

            Assert.True(chunksAfter < chunksBefore, $"chunk 未减少: {chunksBefore} -> {chunksAfter}");
            Assert.Equal(n, world.GetMemoryReport().TotalEntityCount);
            world.Dispose();
        }
    }
}
