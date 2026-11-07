using System;
using System.Linq;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct MultiSelectionJob : IJobEntity
    {
        public void Execute(ref Position value) { value.X++; }
    }

    public class MultiRelationIntegrationTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void QueryAndJob_MatchTargetsAndEnabledAcross129Sources(bool bounded)
        {
            if (bounded) Run<BoundedLinks>(); else Run<ListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                TestBackend.EnsureInitialized(2);
                using var world = new World("MultiSelection");
                var em = world.EntityManager;
                var target = em.NewEntity(typeof(Position));
                var other = em.NewEntity(typeof(Position));
                var sources = em.CreateEntities(129, typeof(Position), typeof(RelationReadEnabled));
                for (int i = 0; i < sources.Length; i++)
                {
                    if (i % 3 != 2) em.AddRelationship<T>(sources[i], i % 3 == 0 ? target : other);
                    em.SetComponentEnabled<RelationReadEnabled>(sources[i], i % 5 != 0);
                }
                var builder = new QueryBuilder().WithAll<Position>().WithRelationship<T>(target).WithEnabled<RelationReadEnabled>();
                Assert.Equal(34, world.GetOrCreateEntityQuery(builder).CalculateEntityCount());
                new MultiSelectionJob().Schedule(builder, world).Complete();
                for (int i = 0; i < sources.Length; i++)
                    Assert.Equal(i % 3 == 0 && i % 5 != 0 ? 1f : 0f, em.GetComponentReadOnly<Position>(sources[i]).X);
                em.AddRelationship<T>(sources[1], target);
                new MultiSelectionJob().Schedule(builder, world).Complete();
                Assert.Equal(1f, em.GetComponentReadOnly<Position>(sources[1]).X);
                int enumerated = 0;
                foreach (var _ in world.Query<Position>().WithRelationship<T>(target)) enumerated++;
                Assert.Equal(44, enumerated);
                using var copied = new NativeList<Entity>(2);
                em.CopyRelationships<T>(sources[1], copied);
                Assert.Equal(2, copied.Length);
            }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void ECB_RecordsMultiAddRemoveClearAndSortsParallelWriters(bool bounded)
        {
            if (bounded) Run<BoundedLinks>(); else Run<ListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiCommands");
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position));
                var a = em.NewEntity(typeof(Position)); var b = em.NewEntity(typeof(Position));
                using var commands = new DeferredCommandBuffer();
                commands.AddRelationship<T>(source, a); commands.AddRelationship<T>(source, b);
                Assert.Empty(em.GetRelationships<T>(source));
                commands.Playback(em);
                Assert.Equal(2, em.GetRelationshipCount<T>(source));
                commands.RemoveRelationship<T>(source, a); commands.Playback(em);
                Assert.Equal(new[] { b }, em.GetRelationships<T>(source));
                commands.EnsureParallelWriters(2);
                commands.CreateParallelWriter(0).AddRelationship<T>(30, source, a);
                commands.CreateParallelWriter(1).ClearRelationships<T>(20, source);
                commands.CreateParallelWriter(0).AddRelationship<T>(10, source, b);
                commands.PlaybackParallel(em);
                Assert.Equal(new[] { a }, em.GetRelationships<T>(source));
                commands.RemoveRelationship<T>(source); commands.Playback(em);
                Assert.Empty(em.GetRelationships<T>(source));
            }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void CloneAndSnapshot_RemapEveryTargetAndPreserveExternalEdges(bool bounded)
        {
            if (bounded) Run<BoundedLinks>(); else Run<ListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiCopies");
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position));
                var inside = em.NewEntity(typeof(Position)); var outside = em.NewEntity(typeof(Position));
                em.AddRelationship<T>(source, inside); em.AddRelationship<T>(source, outside);
                var clone = em.CloneEntities(new[] { source, inside });
                Assert.True(em.GetRelationships<T>(clone[0]).ToHashSet().SetEquals(new[] { clone[1], outside }));
                Assert.Equal(new[] { clone[0] }, em.GetRelationsOf<T>(clone[1]));
                var snapshot = world.TakeSnapshot();
                em.ClearRelationships<T>(source);
                em.DestroyEntity(inside);
                world.Restore(snapshot);
                Assert.True(em.GetRelationships<T>(source).ToHashSet().SetEquals(new[] { inside, outside }));
                Assert.Equal(2, em.GetRelationsOf<T>(outside).Length);
                Assert.Equal(0, em.VerifyLocateTable(out _));
            }
        }

        [Theory]
        [InlineData(false, "clone-storage-prepared")] [InlineData(true, "clone-storage-prepared")]
        [InlineData(false, "clone-row-added")] [InlineData(true, "clone-row-added")]
        [InlineData(false, "clone-indexed")] [InlineData(true, "clone-indexed")]
        public void CloneFailure_RollsBackEveryMultiEdgeAndAllowsRetry(bool bounded, string checkpoint)
        {
            if (bounded) Run<BoundedLinks>(checkpoint); else Run<ListLinks>(checkpoint);
            static void Run<T>(string point) where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiCloneRollback");
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position));
                var target = em.NewEntity(typeof(Position));
                em.AddRelationship<T>(source, source); em.AddRelationship<T>(source, target);
                int issued = em.EntityCount, events = 0;
                em.AddObserver<T>(ObserverEvents.Added, (_, _) => events++);
                em.RelationshipFailureInjector = p => { if (p == point) throw new OutOfMemoryException(); };
                Assert.Throws<OutOfMemoryException>(() => em.CloneEntities(new[] { source, target }));
                em.RelationshipFailureInjector = null;
                Assert.Equal(issued, em.EntityCount); Assert.Equal(0, events);
                Assert.Equal(new[] { source }, em.GetRelationsOf<T>(target));
                var clone = em.CloneEntities(new[] { source, target });
                Assert.True(em.GetRelationships<T>(clone[0]).ToHashSet().SetEquals(clone));
                Assert.Equal(2, events);
            }
        }
    }
}
