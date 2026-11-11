using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct ExpressionBodyJob : IJobEntity
    {
        public float Scale;
        public void Execute(ref Position position, in Velocity velocity) => position.X += velocity.X * Scale;
    }

    public struct ThrowExpressionBodyJob : IJobEntity
    {
        public void Execute(ref Position position) => throw new System.InvalidOperationException("job failure");
    }

    public struct EntityIdentityJob : IJobEntity
    {
        public void Execute(Entity entity, ref Position position, in Velocity velocity)
        {
            position.X = entity.Id;
            position.Y = entity.Version + velocity.X;
        }
    }

    public struct ParameterSemanticsData : IComponentData
    {
        public float Value;
        public float Increment() => ++Value;
    }

    public struct ParameterSemanticsJob : IJobEntity
    {
        public void Execute(ref Position position, ParameterSemanticsData value, in ParameterSemanticsData readOnly)
        {
            position.X = value.Increment() + value.Increment();
            position.Y = readOnly.Increment() + readOnly.Increment();
        }
    }

    public class ExpressionBodyJobTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ComponentParameters_PreserveValueCopiesAndReadOnlyDefensiveCopies(bool schedule)
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("ParameterSemanticsJob");
            var em = world.EntityManager;
            var entities = new Entity[129];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = em.NewEntity(typeof(Position), typeof(ParameterSemanticsData));
                em.Set(entities[i], new ParameterSemanticsData { Value = i });
            }

            var query = new QueryBuilder().WithAll<Position, ParameterSemanticsData>();
            var job = new ParameterSemanticsJob();
            if (schedule) job.Schedule(query, world).Complete(); else job.Run(query, world);

            for (int i = 0; i < entities.Length; i++)
            {
                var position = em.GetComponentReadOnly<Position>(entities[i]);
                Assert.Equal(i * 2 + 3, position.X);
                Assert.Equal(i * 2 + 2, position.Y);
                Assert.Equal(i, em.GetComponentReadOnly<ParameterSemanticsData>(entities[i]).Value);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void EntityParameter_AcrossChunks_PreservesIdentityAndEnabledFilter(bool schedule, bool filtered)
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("EntityIdentityJob");
            var em = world.EntityManager;
            var entities = new Entity[1025];
            for (int i = 0; i < entities.Length; i++)
            {
                var retired = em.NewEntity(typeof(Position), typeof(Velocity), typeof(RelActiveComponent));
                em.DestroyEntity(retired);
                entities[i] = em.NewEntity(typeof(Position), typeof(Velocity), typeof(RelActiveComponent));
                em.Set(entities[i], new Position { X = -1, Y = -1 });
                em.Set(entities[i], new Velocity { X = i });
                em.SetComponentEnabled<RelActiveComponent>(entities[i], i % 3 == 0);
            }
            var query = new QueryBuilder().WithAll<Position, Velocity>();
            if (filtered) query = query.WithEnabled<RelActiveComponent>();
            var job = new EntityIdentityJob();
            if (schedule) job.Schedule(query, world).Complete(); else job.Run(query, world);

            for (int i = 0; i < entities.Length; i++)
            {
                bool matched = !filtered || i % 3 == 0;
                var position = em.GetComponentReadOnly<Position>(entities[i]);
                Assert.Equal(matched ? entities[i].Id : -1, position.X);
                Assert.Equal(matched ? entities[i].Version + i : -1, position.Y);
                Assert.Equal(i, em.GetComponentReadOnly<Velocity>(entities[i]).X);
            }
        }

        [Fact]
        public void ThrowExpressionBody_Throws()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("ThrowExpressionBodyJob");
            world.EntityManager.NewEntity(typeof(Position));
            var query = new QueryBuilder().WithAll<Position>();
            Assert.Throws<System.InvalidOperationException>(() => new ThrowExpressionBodyJob().Run(query, world));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExpressionBody_UpdatesEveryMatchingEntity(bool schedule)
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("ExpressionBodyJob");
            var em = world.EntityManager;
            var entities = new Entity[65];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = em.NewEntity(typeof(Position), typeof(Velocity));
                em.Set(entities[i], new Velocity { X = i + 1 });
            }

            var query = new QueryBuilder().WithAll<Position, Velocity>();
            var job = new ExpressionBodyJob { Scale = 2 };
            if (schedule) job.Schedule(query, world).Complete(); else job.Run(query, world);

            for (int i = 0; i < entities.Length; i++)
                Assert.Equal((i + 1) * 2, em.GetComponentReadOnly<Position>(entities[i]).X);
        }
    }
}
