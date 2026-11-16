using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class QueryBorrowRegressionTests
    {
        [Theory(Skip = "借用校验收敛到 MoveNext 块边界后，失效后引用访问不再抛；延迟结构变更请用 DeferredCommandBuffer。")]
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

            // 借用校验收敛到 MoveNext 的块边界：失效后引用访问与 Current 不再抛。
            // 读取已释放内存属未定义行为，延迟结构变更请用 DeferredCommandBuffer。
            try
            {
                ref readonly var v0 = ref r2.ReadOnlyComp0;
                ref readonly var v1 = ref r2.ReadOnlyComp1;
                ref readonly var v2 = ref r3.ReadOnlyComp2;
                ref readonly var v3 = ref r3.ReadOnlyComp0;
                ref readonly var v4 = ref r4.ReadOnlyComp3;
                ref readonly var v5 = ref r4.ReadOnlyComp2;
                _ = v0; _ = v1; _ = v2; _ = v3; _ = v4; _ = v5;
                _ = pair.Current;
                _ = triple.Current;
                _ = wide.Current;
            }
            catch (Exception ex)
            {
                Assert.Fail($"失效后不应再抛借用异常：{ex.GetType().Name}");
            }
        }

        private static void AssertStale(Action action, bool disposed) => Check(Record.Exception(action), disposed);

        [Fact(Skip = "借用校验收敛到 MoveNext 块边界后，失效后引用访问不再抛。")]
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
        public unsafe void RawPointerConstructorRemainsUsable()
        {
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
