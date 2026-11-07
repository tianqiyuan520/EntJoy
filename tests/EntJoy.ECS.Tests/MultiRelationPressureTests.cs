using System;
using System.Linq;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public unsafe class MultiRelationPressureTests
    {
        [Fact]
        public void HighDegree_IndexTracksCloneRestoreRemovalAndSourceGeneration()
        {
            using var world = new World("HighDegreeMembership");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position));
            var targets = em.CreateEntities(256, typeof(Position));
            foreach (var target in targets) em.AddRelationship<ListLinks>(source, target);
            long version = em.RelationshipVersion;
            foreach (var target in targets) em.AddRelationship<ListLinks>(source, target);
            Assert.Equal(version, em.RelationshipVersion);
            Assert.Equal(targets, em.GetRelationships<ListLinks>(source));
            var copy = Assert.Single(em.CloneEntities(new[] { source }));
            Assert.All(targets, target => Assert.True(em.HasRelationship<ListLinks>(copy, target)));
            var snapshot = em.TakeSnapshot();
            em.RemoveRelationship<ListLinks>(source, targets[63]);
            Assert.False(em.HasRelationship<ListLinks>(source, targets[63]));
            em.Restore(snapshot);
            Assert.True(em.HasRelationship<ListLinks>(source, targets[63]));
            em.DestroyEntity(source);
            var reused = em.NewEntity(typeof(Position));
            Assert.Equal(source.Id, reused.Id);
            Assert.NotEqual(source.Version, reused.Version);
            Assert.Empty(em.GetRelationships<ListLinks>(reused));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void HighDegree_BulkTargetDeletionPreservesOrderAndIsPreparedBeforeMutation(bool fail)
        {
            using var world = new World("HighDegreeBulkUnlink");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position));
            var targets = em.CreateEntities(256, typeof(Position));
            foreach (var target in targets) em.AddRelationship<ListLinks>(source, target);
            var removed = targets.Where((_, i) => i % 2 == 0).ToArray();
            int events = 0;
            em.AddObserver<ListLinks>(ObserverEvents.Removed, (_, _) => events++);
            if (fail)
            {
                em.RelationshipFailureInjector = point => { if (point == "delete-storage-prepared") throw new OutOfMemoryException(); };
                fixed (Entity* pointer = removed)
                {
                    nint address = (nint)pointer;
                    Assert.Throws<OutOfMemoryException>(() => em.DestroyEntities((Entity*)address, removed.Length));
                }
                em.RelationshipFailureInjector = null;
                Assert.Equal(0, events);
                Assert.Equal(targets, em.GetRelationships<ListLinks>(source));
            }
            fixed (Entity* pointer = removed) Assert.Equal(removed.Length, em.DestroyEntities(pointer, removed.Length));
            Assert.Equal(targets.Where((_, i) => i % 2 != 0), em.GetRelationships<ListLinks>(source));
            Assert.Equal(removed.Length, events);
            Assert.All(removed, target => Assert.False(em.HasRelationship<ListLinks>(source, target)));
            Assert.Equal(0, em.VerifyLocateTable(out _));
        }
    }
}
