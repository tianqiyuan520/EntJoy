using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct ChunkFilterPos : IComponentData { public float X; }
    public struct ChunkFilterVel : IComponentData { public float X; }
    public struct ChunkFilterFlag : IEnableableComponent { public int V; }

    /// <summary>Chunk 级视图与过滤条件同源：块级过滤决定哪些块进入结果，逐实体过滤不支持就要明确拒绝。</summary>
    public class ChunkQueryFilterTests
    {
        [Fact]
        public void Chunks_AppliesSharedFilter()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("ChunkFilter");
            var em = world.EntityManager;
            var zero = em.NewEntity(typeof(Material), typeof(ChunkFilterPos), typeof(ChunkFilterVel));
            var one = em.NewEntity(typeof(Material), typeof(ChunkFilterPos), typeof(ChunkFilterVel));
            em.Set(zero, new ChunkFilterPos { X = 10 });
            em.Set(one, new ChunkFilterPos { X = 20 });
            em.SetSharedComponent(zero, new Material(0));
            em.SetSharedComponent(one, new Material(1));

            var seen = new System.Collections.Generic.List<float>();
            foreach (var chunk in world.Query<ChunkFilterPos, ChunkFilterVel>().WithShared(new Material(1)).Chunks())
            {
                // shared 组件没有逐实体列，取 Span 必须明确拒绝而不是读出别的内存。
                bool rejected = false;
                try { chunk.Span<Material>(); }
                catch (NotSupportedException) { rejected = true; }
                Assert.True(rejected, "shared 组件取 Span 必须拒绝");
                var pos = chunk.Span<ChunkFilterPos>();
                for (int i = 0; i < chunk.Count; i++) seen.Add(pos[i].X);
            }

            Assert.Equal(new[] { 20f }, seen);
        }

        [Fact]
        public void Chunks_RejectsEntityLevelFilter()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("ChunkFilterEnabled");
            var em = world.EntityManager;
            em.NewEntity(typeof(ChunkFilterPos), typeof(ChunkFilterVel), typeof(ChunkFilterFlag));

            Assert.Throws<NotSupportedException>(() =>
            {
                foreach (var chunk in world.Query<ChunkFilterPos, ChunkFilterVel>().WithEnabled<ChunkFilterFlag>().Chunks())
                    _ = chunk.Count;
            });
        }

        [Fact]
        public void Chunks_VisitsTheSameEntitiesAsForeach()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("ChunkEntities");
            var em = world.EntityManager;
            var off = em.NewEntity(typeof(ChunkFilterPos), typeof(ChunkFilterVel), typeof(ChunkFilterFlag));
            var on = em.NewEntity(typeof(ChunkFilterPos), typeof(ChunkFilterVel), typeof(ChunkFilterFlag));
            em.Set(off, new ChunkFilterPos { X = 1 });
            em.Set(on, new ChunkFilterPos { X = 2 });
            em.SetComponentEnabled<ChunkFilterFlag>(off, false);

            int visitedEnabled = 0;
            foreach (var chunk in world.Query<ChunkFilterPos, ChunkFilterVel>().Chunks())
            {
                var positions = chunk.Span<ChunkFilterPos>();
                for (int i = 0; i < positions.Length; i++)
                    if (positions[i].X == 2) visitedEnabled++;
            }

            Assert.Equal(1, visitedEnabled);
        }
    }
}
