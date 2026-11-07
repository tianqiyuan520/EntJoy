using System;
using System.Linq;
using System.Reflection;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public unsafe class RelationshipFailureInjectionTests
    {
        [Fact]
        public void IndexPreparationCleanup_RemovesPartialBucketsAndKeepsExistingEdges()
        {
            var index = new RelationIndex();
            var target = new Entity { Id = 3, Version = 2 };
            var source = new Entity { Id = 7, Version = 1 };
            index.PrepareAdd(1, target);
            Assert.True(index.TryGetSources(target, out var types));
            types.Remove(1);
            index.DiscardEmpty(1, target);
            Assert.True(index.IsEmpty);

            index.Add(2, source, target);
            index.PrepareAdd(1, target);
            index.DiscardEmpty(1, target);
            index.DiscardEmpty(2, target);
            Assert.True(index.TryGetSources(target, out types));
            Assert.False(types.ContainsKey(1));
            Assert.Equal(source, Assert.Single(types[2]));
        }

        [Fact]
        public void EntityIdentity_IncludesVersionAndAvoidsBoxingInRelationSets()
        {
            var entity = new Entity { Id = 7, Version = 3 };
            var recycled = new Entity { Id = 7, Version = 4 };
            Assert.False(entity.Equals(recycled));
            var set = new System.Collections.Generic.HashSet<Entity>(16);
            for (int i = 0; i < 100; i++) { set.Add(entity); set.Remove(entity); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { set.Add(entity); set.Remove(entity); }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        private static int Live(EntityManager manager) => manager.GetAllArchetypes().Sum(arch => arch.EntityCount);
        private static RelationIndex Index(EntityManager manager) => (RelationIndex)typeof(EntityManager)
            .GetField("_relationIndex", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
        private static void Fail(EntityManager manager, string checkpoint, int at = 1)
        {
            int count = 0;
            manager.RelationshipFailureInjector = point =>
            {
                if (point == checkpoint && ++count == at) throw new OutOfMemoryException($"Injected at {point}/{at}");
            };
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PreparedMutationFailure_PreservesBothOwnersAndDoesNotPublishEvents(bool exclusive)
        {
            using var world = new World("RelationPrepareFailure");
            var em = world.EntityManager;
            var a = em.NewEntity(typeof(Position)); var b = em.NewEntity(typeof(Position));
            var oldOwner = em.NewEntity(typeof(Position)); var newOwner = em.NewEntity(typeof(Position));
            int events = 0;
            if (exclusive)
            {
                em.AddRelationship<OccupiedBy>(oldOwner, b);
                em.AddRelationship<OccupiedBy>(newOwner, a);
                em.AddObserver<OccupiedBy>(ObserverEvents.Added | ObserverEvents.Set | ObserverEvents.Removed, (_, _) => events++);
            }
            else em.AddObserver<LifecycleRelation>(ObserverEvents.Added | ObserverEvents.Set, (_, _) => events++);
            Fail(em, "relation-prepared");
            if (exclusive) Assert.Throws<OutOfMemoryException>(() => em.AddRelationship<OccupiedBy>(newOwner, b));
            else Assert.Throws<OutOfMemoryException>(() => em.AddRelationship<LifecycleRelation>(newOwner, b));
            em.RelationshipFailureInjector = null;
            Assert.Equal(0, events);
            Assert.Equal(4, Live(em));
            if (exclusive)
            {
                Assert.Equal(oldOwner, Assert.Single(em.GetRelationsOf<OccupiedBy>(b)));
                Assert.Equal(newOwner, Assert.Single(em.GetRelationsOf<OccupiedBy>(a)));
                em.AddRelationship<OccupiedBy>(newOwner, b);
                Assert.Equal(newOwner, Assert.Single(em.GetRelationsOf<OccupiedBy>(b)));
            }
            else
            {
                Assert.True(Index(em).IsEmpty);
                em.AddRelationship<LifecycleRelation>(newOwner, b);
                Assert.Equal(b, em.GetRelationship<LifecycleRelation>(newOwner));
            }
        }

        [Fact]
        public void PreparedUnlinkFailure_PreservesTheOldEdge()
        {
            using var world = new World("UnlinkPrepareFailure");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position)); var target = em.NewEntity(typeof(Position));
            em.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(target), Data = 43 });
            Fail(em, "unlink-prepared");
            Assert.Throws<OutOfMemoryException>(() => em.RemoveRelationship<LifecycleRelation>(source));
            em.RelationshipFailureInjector = null;
            Assert.Equal(target, em.GetRelationship<LifecycleRelation>(source));
            Assert.Equal(43, em.GetComponentReadOnly<LifecycleRelation>(source).Data);
            Assert.Equal(source, Assert.Single(em.GetRelationsOf<LifecycleRelation>(target)));
        }

        [Theory]
        [InlineData("clone-values-prepared", 1)]
        [InlineData("clone-storage-prepared", 1)]
        [InlineData("clone-row-added", 1)]
        [InlineData("clone-row-added", 63)]
        [InlineData("clone-row-added", 64)]
        [InlineData("clone-row-added", 65)]
        [InlineData("clone-row-added", 129)]
        [InlineData("clone-row-added", 130)]
        [InlineData("clone-before-publish", 1)]
        [InlineData("clone-indexed", 1)]
        public void CloneFailure_RollsBackRowsResourcesIdsAndProvisionalIndex(string checkpoint, int at)
        {
            CloneOwned.Reset();
            int capacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using (var world = new World("CloneInjectedFailure"))
                {
                    var em = world.EntityManager;
                    var source = em.NewEntity(typeof(Position), typeof(Prefab), typeof(CloneOwned));
                    em.SetComponent(source, CloneOwned.Create(71));
                    em.AddRelationship<LifecycleRelation>(source, source);
                    int issued = em.EntityCount, events = 0;
                    em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (_, _) => events++);
                    Fail(em, checkpoint, at);
                    Assert.Throws<OutOfMemoryException>(() => em.SpawnFrom(source, 130));
                    em.RelationshipFailureInjector = null;
                    Assert.Equal(1, Live(em)); Assert.Equal(issued, em.EntityCount);
                    Assert.Equal(1, CloneOwned.Live); Assert.Equal(130, CloneOwned.Disposals);
                    Assert.Equal(0, events);
                    Assert.Equal(source, em.GetRelationship<LifecycleRelation>(source));
                    Assert.Equal(source, Assert.Single(em.GetRelationsOf<LifecycleRelation>(source)));
                    for (int id = issued; id < issued + 130; id++)
                        Assert.False(Index(em).TryGetSources(new Entity { Id = id, Version = 0 }, out _));
                    Assert.Equal(0, em.VerifyLocateTable(out _));
                    foreach (var arch in em.GetAllArchetypes()) Assert.Equal(0, arch.VerifySlabInvariants());
                    var copies = em.SpawnFrom(source, 2);
                    Assert.Equal(3, CloneOwned.Live);
                    Assert.All(copies, copy => Assert.Equal(copy, em.GetRelationship<LifecycleRelation>(copy)));
                }
                Assert.Equal(0, CloneOwned.Live);
            }
            finally { Archetype.ChunkCapacityOverride = capacity; }
        }

        [Theory]
        [InlineData("restore-row-loaded")]
        [InlineData("restore-prepared")]
        public void RestorePreparationFailure_DoesNotReplaceTheOldWorld(string checkpoint)
        {
            using var world = new World("RestoreInjectedFailure");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position)); var target = em.NewEntity(typeof(Position));
            em.AddRelationship<LifecycleRelation>(source, target);
            var snapshot = em.TakeSnapshot();
            em.SetComponent(source, new Position { X = 91 });
            var lookup = em.GetReadOnlyComponentLookup<Position>();
            int version = em.StructuralVersion;
            Fail(em, checkpoint);
            Assert.Throws<OutOfMemoryException>(() => em.Restore(snapshot));
            em.RelationshipFailureInjector = null;
            Assert.Equal(version, em.StructuralVersion); Assert.Equal(91f, lookup[source].X);
            Assert.Equal(target, em.GetRelationship<LifecycleRelation>(source));
            Assert.Equal(source, Assert.Single(em.GetRelationsOf<LifecycleRelation>(target)));
            Assert.Equal(0, em.VerifyLocateTable(out _));
        }

        [Theory]
        [InlineData("delete-closure-prepared")]
        [InlineData("delete-storage-prepared")]
        public void DeleteClosureFailure_DoesNotUnlinkSurvivorsOrDestroyDescendants(string checkpoint)
        {
            using var world = new World("DeleteInjectedFailure");
            var em = world.EntityManager;
            var root = em.NewEntity(typeof(Position)); var child = em.NewEntity(typeof(Position));
            var survivor = em.NewEntity(typeof(Position));
            em.AddRelationship<CascadeChildOf>(child, root);
            em.AddRelationship<LifecycleRelation>(survivor, root);
            Fail(em, checkpoint);
            Assert.Throws<OutOfMemoryException>(() => em.DestroyEntity(root));
            em.RelationshipFailureInjector = null;
            Assert.Equal(3, Live(em));
            Assert.Equal(root, em.GetRelationship<CascadeChildOf>(child));
            Assert.Equal(root, em.GetRelationship<LifecycleRelation>(survivor));
        }

        [Fact]
        public void RepeatedLateDeleteFailures_ReuseEmptyPreparedStorage()
        {
            using var world = new World("RepeatedDeleteFailure");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Position));
            for (int i = 0; i < 200; i++)
            {
                var source = em.NewEntity(typeof(Position));
                em.AddRelationship<LifecycleRelation>(source, target);
            }
            Fail(em, "delete-storage-prepared");
            Assert.Throws<OutOfMemoryException>(() => em.DestroyEntity(target));
            int chunks = em.GetMemoryReport().TotalChunkCount;
            em.RelationshipFailureInjector = point => { if (point == "delete-storage-prepared") throw new OutOfMemoryException("prepare failed"); };
            for (int i = 0; i < 100; i++) Assert.Throws<OutOfMemoryException>(() => em.DestroyEntity(target));
            Assert.Equal(chunks, em.GetMemoryReport().TotalChunkCount);
            em.RelationshipFailureInjector = null;
            em.DestroyEntity(target);
            Assert.Equal(200, Live(em));
        }

        [Fact]
        public void ChunkPoolTrim_ReleasesIdleNativeReservations()
        {
            ChunkMemoryPool.Trim();
            long baseline = ChunkMemoryPool.ReservedBytes;
            var pointer = ChunkMemoryPool.Allocate();
            Assert.Equal(baseline + 131072, ChunkMemoryPool.ReservedBytes);
            ChunkMemoryPool.Free(pointer);
            Assert.True(ChunkMemoryPool.PooledBytes >= 131072);
            ChunkMemoryPool.Trim();
            Assert.Equal(baseline, ChunkMemoryPool.ReservedBytes);
        }

        [Theory]
        [InlineData("job-filter-bits")]
        [InlineData("job-filter-enabled")]
        public void NativeFilterPreparationFailure_LeavesTheGraphAndCollectorReusable(string checkpoint)
        {
            using var world = new World("FilterPreparationFailure");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Position));
            var source = em.NewEntity(typeof(Position), typeof(LifecycleRelation), typeof(RelationReadEnabled));
            em.AddRelationship<LifecycleRelation>(source, target);
            var query = new QueryBuilder().WithAll<Position>().WithRelationship<LifecycleRelation>(target).WithEnabled<RelationReadEnabled>();
            var required = new[] { ComponentTypeManager.GetComponentType(typeof(Position)).Id };
            Fail(em, checkpoint);
            Assert.Throws<OutOfMemoryException>(() => ChunkJobCollector.CollectAndBuildNative(em, query, required, out _, out _, out _));
            em.RelationshipFailureInjector = null;
            ChunkJobCollector.CollectAndBuildNative(em, query, required, out var data, out int count, out _);
            try { Assert.Equal(1, count); Assert.Equal(1UL, data[0].filter.relationshipBits[0]); }
            finally { ChunkJobCollector.FreeChunkJobDataPayload(data, count); }
            Assert.Equal(target, em.GetRelationship<LifecycleRelation>(source));
        }

        [Fact]
        public void RelationMigration_KeepsDifferentSharedGroupsAndEnabledBitsIsolated()
        {
            using var world = new World("RelationSharedGroups");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Position));
            var first = em.NewEntity(typeof(Position), typeof(Material), typeof(RelationReadEnabled));
            var second = em.NewEntity(typeof(Position), typeof(Material), typeof(RelationReadEnabled));
            em.SetSharedComponent(first, new Material(71));
            em.SetSharedComponent(second, new Material(92));
            em.SetComponentEnabled<RelationReadEnabled>(first, false);
            em.AddRelationship<LifecycleRelation>(first, target);
            em.AddRelationship<LifecycleRelation>(second, target);
            AssertGroups();
            em.RemoveRelationship<LifecycleRelation>(first);
            AssertGroups();
            em.AddRelationship<LifecycleRelation>(first, target);
            em.DestroyEntity(target);
            AssertGroups();
            Assert.False(em.HasRelationship<LifecycleRelation>(first));
            Assert.False(em.HasRelationship<LifecycleRelation>(second));
            Assert.Equal(0, em.VerifyLocateTable(out _));

            void AssertGroups()
            {
                Assert.Equal(71, em.GetSharedComponent<Material>(first).Id);
                Assert.Equal(92, em.GetSharedComponent<Material>(second).Id);
                Assert.False(em.IsComponentEnabled<RelationReadEnabled>(first));
                Assert.True(em.IsComponentEnabled<RelationReadEnabled>(second));
            }
        }

        [Fact]
        public void BatchedTargetDeletion_RemovesSeveralColumnsInOneMoveAndPreservesSharedEnabledOwnership()
        {
            CloneOwned.Reset();
            using (var world = new World("PreparedMultiColumnDelete"))
            {
                var em = world.EntityManager;
                var a = em.NewEntity(typeof(Position)); var b = em.NewEntity(typeof(Position));
                var source = em.NewEntity(typeof(Position), typeof(CloneOwned), typeof(Material), typeof(RelationReadEnabled));
                em.SetComponent(source, CloneOwned.Create(58));
                em.SetSharedComponent(source, new Material(71));
                em.SetComponentEnabled<RelationReadEnabled>(source, false);
                em.AddRelationship<LifecycleRelation>(source, a);
                em.AddRelationship<LifecycleParent>(source, a);
                em.AddRelationship<OccupiedBy>(source, b);
                Assert.Equal(71, em.GetSharedComponent<Material>(source).Id);
                Assert.False(em.IsComponentEnabled<RelationReadEnabled>(source));
                Entity* targets = stackalloc Entity[2] { a, b };
                Assert.Equal(2, em.DestroyEntities(targets, 2));
                Assert.False(em.HasRelationship<LifecycleRelation>(source));
                Assert.False(em.HasRelationship<LifecycleParent>(source));
                Assert.False(em.HasRelationship<OccupiedBy>(source));
                Assert.Equal(71, em.GetSharedComponent<Material>(source).Id);
                Assert.False(em.IsComponentEnabled<RelationReadEnabled>(source));
                Assert.Equal(58, *(int*)em.GetComponentReadOnly<CloneOwned>(source).Pointer);
                Assert.Equal(1, CloneOwned.Live); Assert.Equal(0, CloneOwned.Copies); Assert.Equal(0, CloneOwned.Disposals);
                Assert.Equal(0, em.VerifyLocateTable(out _));
            }
            Assert.Equal(0, CloneOwned.Live);
        }
    }
}
