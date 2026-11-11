using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class QueryBorrowRegressionTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Tuples_RejectStaleMoveNextCurrentAndResults(bool dispose)
        {
            TestBackend.EnsureInitialized();
            using var world = new World("TupleBorrow");
            var em = world.EntityManager;
            em.CreateEntities(8, typeof(Position), typeof(Velocity), typeof(Health), typeof(RelActiveComponent));
            var pair = world.Query<Position, Velocity>().GetEnumerator();
            var triple = world.Query<Position, Velocity, Health>().GetEnumerator();
            var wide = world.Query<Position, Velocity, Health, RelActiveComponent>().GetEnumerator();
            Assert.True(pair.MoveNext());
            Assert.True(triple.MoveNext());
            Assert.True(wide.MoveNext());
            var r2 = pair.Current;
            var r3 = triple.Current;
            var r4 = wide.Current;
            if (dispose) world.Dispose();
            else em.NewEntity(typeof(Position));

            // 只获取引用，不解引用已失效的地址。
            AssertStale(() => { ref readonly var value = ref r2.ReadOnlyComp0; }, dispose);
            AssertStale(() => { ref readonly var value = ref r2.Comp1; }, dispose);
            AssertStale(() => { ref readonly var value = ref r3.ReadOnlyComp2; }, dispose);
            AssertStale(() => { ref readonly var value = ref r3.Comp0; }, dispose);
            AssertStale(() => { ref readonly var value = ref r4.ReadOnlyComp3; }, dispose);
            AssertStale(() => { ref readonly var value = ref r4.Comp2; }, dispose);
            Exception error = null;
            try { pair.MoveNext(); } catch (Exception ex) { error = ex; }
            Check(error, dispose);
            error = null;
            try { triple.MoveNext(); } catch (Exception ex) { error = ex; }
            Check(error, dispose);
            error = null;
            try { wide.MoveNext(); } catch (Exception ex) { error = ex; }
            Check(error, dispose);
            error = null;
            try { _ = pair.Current; } catch (Exception ex) { error = ex; }
            Check(error, dispose);
            error = null;
            try { _ = triple.Current; } catch (Exception ex) { error = ex; }
            Check(error, dispose);
            error = null;
            try { _ = wide.Current; } catch (Exception ex) { error = ex; }
            Check(error, dispose);
        }

        private static void AssertStale(Action action, bool disposed) => Check(Record.Exception(action), disposed);

        [Fact]
        public void RelationshipRetarget_InvalidatesFilteredResultsOnly()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("RelationshipBorrow");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Position));
            var other = em.NewEntity(typeof(Position));
            var source = em.NewEntity(typeof(Position), typeof(Velocity), typeof(Health), typeof(RelActiveComponent));
            em.AddRelationship<ChildOf>(source, target);
            var ordinary = world.Query<Position, Velocity>().GetEnumerator();
            Assert.True(ordinary.MoveNext());
            var normal = ordinary.Current;
            var builder = new QueryBuilder().WithAll<Position, Velocity>().WithAll<Health>()
                .WithAll<RelActiveComponent>().WithRelationship<ChildOf>(target);
            var pair = new QueryEnumerable<Position, Velocity>(em, builder).GetEnumerator();
            var triple = new QueryEnumerable<Position, Velocity, Health>(em, builder).GetEnumerator();
            var wide = new QueryEnumerable<Position, Velocity, Health, RelActiveComponent>(em, builder).GetEnumerator();
            Assert.True(pair.MoveNext());
            Assert.True(triple.MoveNext());
            Assert.True(wide.MoveNext());
            var r2 = pair.Current;
            var r3 = triple.Current;
            var r4 = wide.Current;
            em.AddRelationship<ChildOf>(source, other);
            AssertStale(() => { ref readonly var value = ref r2.ReadOnlyComp0; }, false);
            AssertStale(() => { ref readonly var value = ref r3.ReadOnlyComp2; }, false);
            AssertStale(() => { ref readonly var value = ref r4.Comp3; }, false);
            Assert.Equal(0, normal.ReadOnlyComp0.X);
        }

        [Fact]
        public unsafe void DefaultResultsRejectAccess_AndRawPointerConstructorRemainsUsable()
        {
            // 同时生成三、四元组的默认结果保护。
            var pair = default(EntityQueryResult<Position, Velocity>);
            var triple = default(EntityQueryResult<Position, Velocity, Health>);
            var wide = default(EntityQueryResult<Position, Velocity, Health, RelActiveComponent>);
            AssertStale(() => { ref readonly var value = ref pair.ReadOnlyComp0; }, false);
            AssertStale(() => { ref readonly var value = ref triple.ReadOnlyComp1; }, false);
            AssertStale(() => { ref readonly var value = ref wide.Comp3; }, false);
            Position position = new Position { X = 7 };
            Velocity velocity = default;
            var raw = new EntityQueryResult<Position, Velocity>(&position, &velocity);
            Assert.Equal(7, raw.ReadOnlyComp0.X);
            raw.Comp0.X = 8;
            Assert.Equal(8, position.X);
        }

        private static void Check(Exception error, bool disposed)
        {
            if (disposed) Assert.IsType<ObjectDisposedException>(error);
            else Assert.IsType<InvalidOperationException>(error);
        }
    }
}
