using System;
using EntJoy.Collections;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct NativeBufferComponent : IComponentData, IDisposable
    {
        public NativeArray<int> Data;
        public void Dispose() => Data.Dispose();
    }

    public class ComponentLifecycleAndMemoryTests
    {
        [Fact]
        public void DisposableComponent_DestroyEntity_FreesNativeMemory()
        {
            var before = PersistentAllocator.GetStats();
            var world = new World("LifecycleTest");

            for (int i = 0; i < 100; i++)
            {
                var e = world.EntityManager.NewEntity(typeof(NativeBufferComponent));
                world.EntityManager.GetComponent<NativeBufferComponent>(e).Data = new NativeArray<int>(16, Allocator.Persistent);
                world.EntityManager.DestroyEntity(e);
            }
            world.Dispose();

            var after = PersistentAllocator.GetStats();
            Assert.Equal(after.Allocs - before.Allocs, after.Frees - before.Frees);
        }

        [Fact]
        public void DisposableComponent_RemoveComponent_FreesNativeMemory()
        {
            var before = PersistentAllocator.GetStats();
            var world = new World("LifecycleRemoveTest");

            for (int i = 0; i < 100; i++)
            {
                var e = world.EntityManager.NewEntity(typeof(NativeBufferComponent), typeof(Position));
                world.EntityManager.GetComponent<NativeBufferComponent>(e).Data = new NativeArray<int>(16, Allocator.Persistent);
                world.EntityManager.RemoveComponent<NativeBufferComponent>(e);
            }
            world.Dispose();

            var after = PersistentAllocator.GetStats();
            Assert.Equal(after.Allocs - before.Allocs, after.Frees - before.Frees);
        }

        [Fact]
        public void SlabRecycle_DestroyAll_ReleasesSlabs()
        {
            var world = new World("SlabRecycleTest");
            const int n = 10000;
            var entities = world.CreateEntities(n, typeof(Position), typeof(Velocity));
            long slabsBefore = world.GetMemoryReport().TotalSlabBytes;

            for (int i = 0; i < n; i++)
                world.EntityManager.DestroyEntity(entities[i]);

            long slabsAfter = world.GetMemoryReport().TotalSlabBytes;
            Assert.True(slabsAfter < slabsBefore, $"slab 未回收: {slabsBefore} -> {slabsAfter}");
            world.Dispose();
        }

        [Fact]
        public void MemoryReport_ReflectsChunkAndEntityCounts()
        {
            var world = new World("MemoryReportTest");
            world.CreateEntities(500, typeof(Position), typeof(Velocity));

            var report = world.GetMemoryReport();
            Assert.Equal(500, report.TotalEntityCount);
            Assert.True(report.TotalChunkCount >= 1);
            Assert.True(report.TotalSlabBytes > 0);

            world.Dispose();
            Assert.Equal(0, world.GetMemoryReport().TotalEntityCount);
        }
    }
}
