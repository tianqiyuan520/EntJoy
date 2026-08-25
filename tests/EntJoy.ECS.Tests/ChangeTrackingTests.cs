using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class ChangeTrackingTests
    {
        [Fact]
        public void QueryBuilder_WithAll_ShouldWork()
        {
            var query = new QueryBuilder()
                .WithAll<Position, Velocity>();

            Assert.NotNull(query.All);
            Assert.Equal(2, query.All.Length);
        }

        [Fact]
        public void QueryBuilder_WithNone_ShouldWork()
        {
            var query = new QueryBuilder()
                .WithAll<Position>()
                .WithNone<Health>();

            Assert.NotNull(query.None);
            Assert.Single(query.None);
            Assert.Contains(typeof(Health), query.None);
        }

        [Fact]
        public void QueryBuilder_ChainedFilters_ShouldWork()
        {
            var query = new QueryBuilder()
                .WithAll<Position, Velocity>()
                .WithNone<Health>()
                .WithAny<Armor>();

            Assert.NotNull(query.All);
            Assert.Equal(2, query.All.Length);
            Assert.NotNull(query.None);
            Assert.Single(query.None);
            Assert.NotNull(query.Any);
            Assert.Single(query.Any);
        }
    }
}
