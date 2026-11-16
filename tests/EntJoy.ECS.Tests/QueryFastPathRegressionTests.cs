using System;
using System.Collections.Generic;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class QueryFastPathRegressionTests
    {
        private struct ExtraColumn : IComponentData { }
        private struct EnableFlag : IEnableableComponent { public int Value; }

        [Fact]
        public void EnabledFilter_RunExtendsAcrossWordsAndSkipsEmptyWords()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("EnabledRunExtend");
            var em = world.EntityManager;
            const int n = 200;
            var entities = new Entity[n];
            for (int i = 0; i < n; i++)
            {
                entities[i] = em.NewEntity(typeof(Position), typeof(EnableFlag));
                em.Set(entities[i], new Position { X = i });
            }
            // 同时覆盖整字空缺、字接缝上的关闭位与单个启用位。
            for (int i = 64; i < 192; i++) em.SetComponentEnabled<EnableFlag>(entities[i], false);
            em.SetComponentEnabled<EnableFlag>(entities[100], true);

            var expected = new List<int>();
            for (int i = 0; i < n; i++) if (i < 64 || i >= 192 || i == 100) expected.Add(i);

            var visited = new List<int>();
            foreach (var item in world.Query<Position>().WithEnabled<EnableFlag>())
                visited.Add((int)item.ReadOnlyComp0.X);
            Assert.Equal(expected, visited);

            var builder = new QueryBuilder().WithAll<Position>().WithEnabled<EnableFlag>();
            Assert.Equal(expected.Count, em.GetArchetypeQuery(builder).CalculateEntityCount());
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void ChangedOnlyFilter_TracksClearAndWriteAcrossTupleWidths(int width)
        {
            TestBackend.EnsureInitialized();
            using var world = new World("ChangedOnlyFastPath");
            var em = world.EntityManager;
            var entity = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(ExtraColumn));
            em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(ExtraColumn), typeof(RelActiveComponent));
            int unfiltered = 0;
            foreach (var item in world.Query<Position, Velocity, Health, ExtraColumn>()) unfiltered++;
            Assert.Equal(2, unfiltered);
            var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>().WithAll<ExtraColumn>()
                .WithChanged<Position>();

            void AssertCount(int expected)
            {
                int count = 0, chunks = 0;
                if (width == 2)
                    foreach (var item in new QueryEnumerable<Position, Velocity>(em, builder)) count++;
                else if (width == 3)
                    foreach (var item in new QueryEnumerable<Position, Velocity, Health>(em, builder)) count++;
                else
                    foreach (var item in new QueryEnumerable<Position, Velocity, Health, ExtraColumn>(em, builder)) count++;
                foreach (var item in new ChunkEnumerable<Position, Velocity>(em, builder)) chunks++;
                Assert.Equal(expected, count);
                Assert.Equal(expected, chunks);
            }

            em.ClearAllChangedBitMasks();
            AssertCount(0);
            em.Set(entity, new Position { X = 42 });
            AssertCount(1);
            em.ClearAllChangedBitMasks();
            AssertCount(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PairCurrent_RejectsUnstartedAndFinishedEnumeration(bool filtered)
        {
            TestBackend.EnsureInitialized();
            using var world = new World("PairCurrentState");
            var em = world.EntityManager;
            var entity = em.NewEntity(typeof(Position), typeof(Velocity));
            em.Set(entity, new Position { X = 42 });
            var builder = new QueryBuilder().WithAll<Position, Velocity>();
            if (filtered) builder = builder.WithChanged<Position>();
            var iterator = new QueryEnumerable<Position, Velocity>(em, builder).GetEnumerator();

            Exception error = null;
            try { _ = iterator.Current; }
            catch (Exception exception) { error = exception; }
            Assert.IsType<InvalidOperationException>(error);
            Assert.True(iterator.MoveNext());
            Assert.Equal(42, iterator.Current.ReadOnlyComp0.X);
            Assert.False(iterator.MoveNext());
            Assert.False(iterator.MoveNext());
            error = null;
            try { _ = iterator.Current; }
            catch (Exception exception) { error = exception; }
            Assert.IsType<InvalidOperationException>(error);
        }
    }
}
