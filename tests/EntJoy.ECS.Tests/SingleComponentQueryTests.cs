using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 单组件查询（<c>world.Query&lt;T0&gt;()</c>）的三条遍历入口：实体级 foreach、块循环 ForEach、Chunk 视图。
    /// 三者必须看到同一批实体，且写回生效。
    /// </summary>
    public class SingleComponentQueryTests
    {
        private struct ProbePos : IComponentData { public int Value; }
        private struct ProbeVel : IComponentData { public int Value; }
        private struct ProbeAlive : IComponentData, IEnableableComponent { }

        private struct AddOne : IQueryForeach<ProbePos>
        {
            public void Update(ref ProbePos component0) => component0.Value += 1;
        }

        private static World CreateWorld(int posOnly, int posAndVel)
        {
            TestBackend.EnsureInitialized();
            var world = new World("SingleComponentQuery");
            for (int i = 0; i < posOnly; i++) world.EntityManager.NewEntity(typeof(ProbePos));
            for (int i = 0; i < posAndVel; i++) world.EntityManager.NewEntity(typeof(ProbePos), typeof(ProbeVel));
            return world;
        }

        private static int SumPos(World world)
        {
            int sum = 0;
            foreach (var result in world.Query<ProbePos>()) sum += result.Comp0.Value;
            return sum;
        }

        [Fact]
        public void Foreach_VisitsEveryEntityWithTheComponent_AndWritesThrough()
        {
            using var world = CreateWorld(3, 2);
            int visited = 0;
            foreach (var result in world.Query<ProbePos>())
            {
                result.Comp0.Value += 7;
                visited++;
            }
            Assert.Equal(5, visited);
            Assert.Equal(35, SumPos(world));
        }

        [Fact]
        public void ForEach_VisitsTheSameEntitiesAsForeach()
        {
            using var world = CreateWorld(3, 2);
            var action = new AddOne();
            world.Query<ProbePos>().ForEach(ref action);
            Assert.Equal(5, SumPos(world));
        }

        [Fact]
        public void Chunks_VisitsTheSameEntitiesAsForeach()
        {
            using var world = CreateWorld(3, 2);
            int inChunks = 0;
            foreach (var chunk in world.Query<ProbePos>().Chunks())
            {
                var column = chunk.Span<ProbePos>();
                for (int i = 0; i < column.Length; i++) column[i].Value = 1;
                inChunks += column.Length;
            }
            Assert.Equal(5, inChunks);
            Assert.Equal(5, SumPos(world));
        }

        [Fact]
        public void FilteredSelection_StillAppliesTheFilter()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("SingleComponentQueryFiltered");
            var enabled = world.EntityManager.NewEntity(typeof(ProbePos), typeof(ProbeAlive));
            world.EntityManager.NewEntity(typeof(ProbePos), typeof(ProbeAlive));
            world.EntityManager.SetComponentEnabled<ProbeAlive>(enabled, false);

            int visited = 0;
            foreach (var result in world.Query<ProbePos>().WithEnabled<ProbeAlive>())
            {
                result.Comp0.Value = 9;
                visited++;
            }
            Assert.Equal(1, visited);
            Assert.Equal(9, SumPos(world));
        }
    }
}
