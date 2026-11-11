using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class ChunkReuseRegressionTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(64)]
        public void Churn_ReusesPartialAndEmptyChunks(int removedPerRound)
        {
            TestBackend.EnsureInitialized();
            int saved = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("ChunkReuse");
                var em = world.EntityManager;
                var live = em.CreateEntities(128, typeof(Position));
                var arch = em.GetEntityInfoRef(live[0].Id).Archetype;
                for (int round = 0; round < 40; round++)
                {
                    int start = round % 2 * 64;
                    for (int i = start; i < start + removedPerRound; i++) em.DestroyEntity(live[i]);
                    for (int i = start; i < start + removedPerRound; i++)
                    {
                        live[i] = em.NewEntity(typeof(Position));
                        em.Set(live[i], new Position { X = round });
                    }
                    Assert.Equal(2, arch.ChunkCount);
                    Assert.Equal(0, arch.VerifySlabInvariants());
                    Assert.Equal(0, em.VerifyLocateTable(out int count));
                    Assert.Equal(128, count);
                }
            }
            finally { Archetype.ChunkCapacityOverride = saved; }
        }

        [Fact]
        public void Reuse_AfterCompactionAndClear_KeepsLocationsAndSlabsValid()
        {
            TestBackend.EnsureInitialized();
            int saved = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("ReuseAfterCompaction");
                var em = world.EntityManager;
                var entities = em.CreateEntities(192, typeof(Position));
                var arch = em.GetEntityInfoRef(entities[0].Id).Archetype;
                for (int i = 0; i < 64; i++) em.DestroyEntity(entities[i]);
                for (int i = 128; i < 160; i++) em.DestroyEntity(entities[i]);
                em.CompactChunks(100);
                em.CreateEntities(96, typeof(Position));
                Assert.Equal(3, arch.ChunkCount);
                Assert.Equal(0, em.VerifyLocateTable(out int count));
                Assert.Equal(192, count);
                Assert.Equal(0, arch.VerifySlabInvariants());
                em.DestroyAllInArchetype(arch);
                em.CreateEntities(192, typeof(Position));
                Assert.Equal(3, arch.ChunkCount);
                Assert.Equal(0, arch.VerifySlabInvariants());
                Assert.Equal(0, em.VerifyLocateTable(out count));
                Assert.Equal(192, count);
            }
            finally { Archetype.ChunkCapacityOverride = saved; }
        }
    }
}
