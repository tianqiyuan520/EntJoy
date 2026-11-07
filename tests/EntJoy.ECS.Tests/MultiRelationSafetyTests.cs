using System;
using System.Linq;
using System.IO;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class MultiRelationSafetyTests
    {
        [Fact]
        public void ManagedFilter_DoesNotExposeANonexistentComponentColumn()
        {
            using var world = new World("ManagedRelationReadBoundary");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position)); var target = em.NewEntity(typeof(Position));
            em.AddRelationship<ListLinks>(source, target);
            var enumerator = world.Query<Position>().WithRelationship<ListLinks>(target).GetEnumerator();
            Assert.True(enumerator.MoveNext());
            var result = enumerator.Current;
            Assert.Throws<NotSupportedException>(() => { _ = result.ReadOnlyComp1.Target; });
            Assert.False(enumerator.MoveNext());
        }

        [Fact]
        public void ParallelMultiFailure_ConsumesTheBatchWithoutReplayingItsPrefix()
        {
            using var world = new World("MultiCommandFailure");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position));
            var targets = em.CreateEntities(3, typeof(Position));
            using var commands = new DeferredCommandBuffer();
            commands.EnsureParallelWriters(1);
            var writer = commands.CreateParallelWriter(0);
            for (int i = 0; i < 3; i++) writer.AddRelationship<BoundedLinks>(i, source, targets[i]);
            writer.ClearRelationships<BoundedLinks>(4, source);
            var error = Assert.Throws<CommandPlaybackException>(() => commands.PlaybackParallel(em));
            Assert.Equal(2, error.CommandIndex); Assert.Equal(1, error.UnexecutedCommandCount);
            Assert.True(em.GetRelationships<BoundedLinks>(source).ToHashSet().SetEquals(targets.Take(2)));
            commands.PlaybackParallel(em);
            Assert.Equal(2, em.GetRelationshipCount<BoundedLinks>(source));
            Assert.Empty(em.GetRelationsOf<BoundedLinks>(targets[2]));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void RemoveComponent_ClearsMultiStateAndNotifiesEachEdgeOnce(bool bounded)
        {
            if (bounded) Run<BoundedLinks>(); else Run<ListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                using var world = new World("RemoveMultiColumn");
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position), typeof(T));
                var a = em.NewEntity(typeof(Position)); var b = em.NewEntity(typeof(Position));
                em.AddRelationship<T>(source, a); em.AddRelationship<T>(source, b);
                int removed = 0;
                em.AddObserver<T>(ObserverEvents.Removed, (_, _) => removed++);
                em.RemoveComponentRaw(source, typeof(T));
                Assert.Empty(em.GetRelationships<T>(source));
                Assert.Empty(em.GetRelationsOf<T>(a));
                Assert.False(em.GetEntityInfoRef(source.Id).Archetype.Has(typeof(T)));
                Assert.Equal(2, removed);
                em.AddRelationship<T>(source, a);
                int destroyed = 0;
                em.AddObserver<T>(ObserverEvents.Destroyed, (_, _) => destroyed++);
                em.DestroyEntity(source);
                Assert.Equal(1, destroyed);
            }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void ExclusiveClone_RejectsExternalTargetAndRemapsInternalTarget(bool bounded)
        {
            if (bounded) Run<ExclusiveBoundedLinks>(); else Run<ExclusiveListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiExclusiveClone");
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position), typeof(Prefab));
                var target = em.NewEntity(typeof(Position));
                em.AddRelationship<T>(source, target);
                Assert.Throws<InvalidOperationException>(() => em.SpawnFrom(source, 1));
                Assert.Equal(new[] { source }, em.GetRelationsOf<T>(target));
                var copies = em.CloneEntities(new[] { source, target });
                Assert.Equal(new[] { copies[1] }, em.GetRelationships<T>(copies[0]));
                Assert.Equal(new[] { copies[0] }, em.GetRelationsOf<T>(copies[1]));
            }
        }

        [Theory]
        [InlineData(false, "restore-row-loaded")] [InlineData(true, "restore-row-loaded")]
        [InlineData(false, "restore-prepared")] [InlineData(true, "restore-prepared")]
        public void RestoreFailure_KeepsCurrentMultiGraph(bool bounded, string checkpoint)
        {
            if (bounded) Run<BoundedLinks>(checkpoint); else Run<ListLinks>(checkpoint);
            static void Run<T>(string point) where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiRestorePreparation");
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position)); var first = em.NewEntity(typeof(Position)); var second = em.NewEntity(typeof(Position));
                em.AddRelationship<T>(source, first);
                var snapshot = world.TakeSnapshot();
                em.RemoveRelationship<T>(source, first); em.AddRelationship<T>(source, second);
                long version = em.RelationshipVersion;
                em.RelationshipFailureInjector = p => { if (p == point) throw new OutOfMemoryException(); };
                Assert.Throws<OutOfMemoryException>(() => world.Restore(snapshot));
                em.RelationshipFailureInjector = null;
                Assert.Equal(version, em.RelationshipVersion);
                Assert.Equal(new[] { second }, em.GetRelationships<T>(source));
                world.Restore(snapshot);
                Assert.Equal(new[] { first }, em.GetRelationships<T>(source));
            }
        }

        [Theory]
        [InlineData("stale")] [InlineData("duplicate")] [InlineData("exclusive")] [InlineData("truncated")]
        public void InvalidManagedSnapshot_IsRejectedBeforeReplacingCurrentWorld(string kind)
        {
            using var world = new World("InvalidManagedRelationSnapshot");
            var em = world.EntityManager;
            var a = em.NewEntity(typeof(Position)); var b = em.NewEntity(typeof(Position)); var c = em.NewEntity(typeof(Position));
            em.AddRelationship<ExclusiveListLinks>(a, b);
            em.AddRelationship<ExclusiveListLinks>(c, c);
            var snapshot = world.TakeSnapshot();
            var bytes = (byte[])snapshot.Data.Clone();
            if (kind == "truncated") bytes = bytes[..^1];
            else if (kind == "stale") BitConverter.GetBytes(999999).CopyTo(bytes, bytes.Length - 8);
            else if (kind == "exclusive")
            {
                BitConverter.GetBytes(b.Id).CopyTo(bytes, bytes.Length - 8);
                BitConverter.GetBytes(b.Version).CopyTo(bytes, bytes.Length - 4);
            }
            else
            {
                BitConverter.GetBytes(2).CopyTo(bytes, bytes.Length - 12);
                bytes = bytes.Concat(bytes[^8..]).ToArray();
            }
            Assert.Throws<InvalidDataException>(() => world.Restore(new WorldSnapshot(bytes)));
            Assert.Equal(new[] { b }, em.GetRelationships<ExclusiveListLinks>(a));
            Assert.Equal(new[] { c }, em.GetRelationships<ExclusiveListLinks>(c));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void DeletePreparationFailure_DoesNotRemoveMultiEdgesOrNotify(bool bounded)
        {
            if (bounded) Run<BoundedLinks>(); else Run<ListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiDeletePreparation");
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position)); var a = em.NewEntity(typeof(Position)); var b = em.NewEntity(typeof(Position));
                em.AddRelationship<T>(source, a); em.AddRelationship<T>(source, b);
                int events = 0;
                em.AddObserver<T>(ObserverEvents.Removed | ObserverEvents.Destroyed, (_, _) => events++);
                em.RelationshipFailureInjector = p => { if (p == "delete-storage-prepared") throw new OutOfMemoryException(); };
                Assert.Throws<OutOfMemoryException>(() => em.DestroyEntity(a));
                em.RelationshipFailureInjector = null;
                Assert.Equal(2, em.GetRelationshipCount<T>(source)); Assert.Equal(0, events);
                em.DestroyEntity(a);
                Assert.Equal(new[] { b }, em.GetRelationships<T>(source)); Assert.Equal(1, events);
            }
        }
    }
}
