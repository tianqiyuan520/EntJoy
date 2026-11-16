using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct ChgPos : IComponentData { public int X; }
    public struct ChgVel : IComponentData { public int X; }
    public struct ChgTag : IComponentData { }

    /// <summary>变更过滤是块级过滤：整块里只要有实体被改过，整块都进来（与 DOTS 的 WithChangeFilter 一致）。</summary>
    public class ChangedFilterTests
    {
        [Fact]
        public void Query_WithChanged_YieldsOnlyChunksWithChanges()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("ChangedFilter");
            var em = world.EntityManager;
            var changed = em.NewEntity(typeof(ChgPos), typeof(ChgVel));
            var clean = em.NewEntity(typeof(ChgPos), typeof(ChgVel), typeof(ChgTag));
            em.Set(changed, new ChgPos { X = 1 });
            em.Set(clean, new ChgPos { X = 2 });
            em.ClearAllChangedBitMasks();
            em.Set(changed, new ChgPos { X = 3 });

            int visited = 0;
            foreach (var result in world.Query<ChgPos, ChgVel>().WithChanged<ChgPos>())
            {
                Assert.Equal(3, result.ReadOnlyComp0.X);
                visited++;
            }

            Assert.Equal(1, visited);
        }

        [Fact]
        public void Query_WithChanged_YieldsNothingWhenNoChunkIsDirty()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("ChangedFilterClean");
            var em = world.EntityManager;
            em.NewEntity(typeof(ChgPos), typeof(ChgVel));
            em.Set(em.NewEntity(typeof(ChgPos), typeof(ChgVel)), new ChgPos { X = 1 });
            em.ClearAllChangedBitMasks();

            int visited = 0;
            foreach (var result in world.Query<ChgPos, ChgVel>().WithChanged<ChgPos>()) visited++;
            Assert.Equal(0, visited);
        }
    }
}
