using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class SharedCompactionRegressionTests
    {
        public struct ThrowingShared : ISharedComponentData
        {
            public int Id;
            public static bool ThrowOnEquals;
            public override bool Equals(object obj) => ThrowOnEquals
                ? throw new InvalidOperationException("shared comparison failed")
                : obj is ThrowingShared other && Id == other.Id;
            public override int GetHashCode() => Id;
        }

        [Fact]
        public void Compaction_ComparisonFailureInvalidatesBorrowAfterRemovingEmptyChunk()
        {
            TestBackend.EnsureInitialized();
            using var world = new World("CompactionFailure");
            var em = world.EntityManager;
            Entity Create(int id) => em.NewEntity(
                new ComponentType[] { typeof(Position), typeof(Velocity), typeof(ThrowingShared) },
                (typeof(ThrowingShared), (object)new ThrowingShared { Id = id }));
            var first = Create(1);
            var second = Create(2);
            var removed = Create(3);
            em.DestroyEntity(removed);
            var arch = em.GetEntityInfoRef(first.Id).Archetype;
            Assert.Equal(3, arch.ChunkCount);
            var iterator = world.Query<Position, Velocity>().GetEnumerator();
            Assert.True(iterator.MoveNext());
            var result = iterator.Current;
            ThrowingShared.ThrowOnEquals = true;
            try
            {
                Assert.Throws<InvalidOperationException>(() => em.CompactChunks(100));
            }
            finally { ThrowingShared.ThrowOnEquals = false; }

            Assert.Equal(2, arch.ChunkCount);
            Assert.Throws<InvalidOperationException>(() => { ref readonly var value = ref result.ReadOnlyComp0; });
            Assert.Equal(1, em.GetSharedComponent<ThrowingShared>(first).Id);
            Assert.Equal(2, em.GetSharedComponent<ThrowingShared>(second).Id);
            Assert.Equal(0, arch.VerifySlabInvariants());
            Assert.Equal(0, em.VerifyLocateTable(out int count));
            Assert.Equal(2, count);
            em.CompactChunks(100);
            count = 0;
            foreach (var row in world.Query<Position, Velocity>()) count++;
            Assert.Equal(2, count);
        }

        [Theory]
        [InlineData(7, "a", true)]
        [InlineData(8, "a", false)]
        [InlineData(7, "b", false)]
        public void Compaction_PreservesCompleteSharedCombination(int material, string mesh, bool merges)
        {
            TestBackend.EnsureInitialized();
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("SharedCompaction");
                var em = world.EntityManager;
                Entity Create(int id, string name) => em.NewEntity(
                    new ComponentType[] { typeof(Position), typeof(Material), typeof(MeshAsset) },
                    (typeof(Material), (object)new Material(id)), (typeof(MeshAsset), new MeshAsset(name)));
                var first = Create(7, "a");
                var removed = new Entity[63];
                for (int i = 0; i < removed.Length; i++) removed[i] = Create(7, "a");
                var second = Create(material, mesh);
                foreach (var entity in removed) em.DestroyEntity(entity);

                em.CompactChunks(100);
                em.CompactChunks(100);

                Assert.Equal(7, em.GetSharedComponent<Material>(first).Id);
                Assert.Equal("a", em.GetSharedComponent<MeshAsset>(first).Name);
                Assert.Equal(material, em.GetSharedComponent<Material>(second).Id);
                Assert.Equal(mesh, em.GetSharedComponent<MeshAsset>(second).Name);
                Assert.Equal(merges, em.GetEntityInfoRef(first.Id).ChunkIndex == em.GetEntityInfoRef(second.Id).ChunkIndex);
                Assert.Equal(0, em.VerifyLocateTable(out int checkedEntities));
                Assert.Equal(2, checkedEntities);
            }
            finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        }

        [Fact]
        public void Compaction_SkipsIncompatibleChunkAfterTargetFills()
        {
            TestBackend.EnsureInitialized();
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("SharedCompactionTargets");
                var em = world.EntityManager;
                Entity Create(int id) => em.NewEntity(new ComponentType[] { typeof(Position), typeof(Material) },
                    (typeof(Material), (object)new Material(id)));
                var a0 = Create(7);
                for (int i = 0; i < 62; i++) Create(7);
                var a1 = Create(7);
                var b0 = Create(8);
                var a2 = Create(7);
                var a4 = Create(7);
                var removed = new Entity[62];
                for (int i = 0; i < removed.Length; i++) removed[i] = Create(7);
                em.DestroyEntity(a1);
                foreach (var entity in removed) em.DestroyEntity(entity);

                em.CompactChunks(100);

                foreach (var entity in new[] { a0, a2, a4 })
                    Assert.Equal(7, em.GetSharedComponent<Material>(entity).Id);
                Assert.Equal(8, em.GetSharedComponent<Material>(b0).Id);
                Assert.Equal(0, em.VerifyLocateTable(out int checkedEntities));
                Assert.Equal(66, checkedEntities);
            }
            finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        }
    }
}
