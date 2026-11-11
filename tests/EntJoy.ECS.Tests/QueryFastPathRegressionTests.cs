using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class QueryFastPathRegressionTests
    {
        private struct ExtraColumn : IComponentData { }

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
