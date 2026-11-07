using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    [MultiRelation] public struct ListLinks : IRelationComponent { public RelationSlot Target; }
    [MultiRelation(MaxSlots = 2)] public partial struct BoundedLinks : IRelationComponent { public RelationSlot Target; }
    [MultiRelation, ExclusiveTarget] public struct ExclusiveListLinks : IRelationComponent { public RelationSlot Target; }
    [MultiRelation(MaxSlots = 2), ExclusiveTarget] public partial struct ExclusiveBoundedLinks : IRelationComponent { public RelationSlot Target; }

    public class MultiRelationLifecycleTests
    {
        private static Entity New(EntityManager em) => em.NewEntity(typeof(Position));

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void EdgeEvents_AreCompleteAndObserveCommittedGraph(bool bounded)
        {
            if (bounded) Run<BoundedLinks>(); else Run<ListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiEdgeEvents");
                var em = world.EntityManager;
                var source = New(em); var a = New(em); var b = New(em);
                var events = new List<(string Kind, Entity Target)>();
                bool coherent = true;
                void Observe(string kind, ReadOnlySpan<T> values)
                {
                    var value = values[0];
                    var target = Unsafe.As<T, RelationSlot>(ref value).ToEntity();
                    bool present = em.HasRelationship<T>(source, target);
                    coherent &= kind == "Added" ? present : !present;
                    events.Add((kind, target));
                }
                em.AddObserver<T>(ObserverEvents.Added, (_, values) => Observe("Added", values));
                em.AddObserver<T>(ObserverEvents.Removed, (_, values) => Observe("Removed", values));
                em.AddObserver<T>(ObserverEvents.Destroyed, (_, values) => Observe("Destroyed", values));
                em.AddRelationship<T>(source, a);
                long version = em.RelationshipVersion;
                em.AddRelationship<T>(source, a);
                Assert.Equal(version, em.RelationshipVersion);
                em.AddRelationship<T>(source, b);
                em.RemoveRelationship<T>(source, a);
                em.AddRelationship<T>(source, a);
                em.DestroyEntity(b);
                Assert.Equal(new[] { a }, em.GetRelationships<T>(source));
                em.DestroyEntity(source);
                Assert.True(coherent);
                Assert.Equal(new[] { ("Added", a), ("Added", b), ("Removed", a), ("Added", a), ("Removed", b), ("Destroyed", a) }, events);
                Assert.Empty(em.GetRelationsOf<T>(a));
            }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void ExclusiveTransfer_PreparesBothOwnersBeforePublishing(bool bounded)
        {
            if (bounded) Run<ExclusiveBoundedLinks>(); else Run<ExclusiveListLinks>();
            static void Run<T>() where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiTransferPreparation");
                var em = world.EntityManager;
                var oldOwner = New(em); var nextOwner = New(em); var target = New(em);
                em.AddRelationship<T>(oldOwner, target);
                int events = 0;
                em.AddObserver<T>(ObserverEvents.Added | ObserverEvents.Removed, (_, _) => events++);
                em.RelationshipFailureInjector = point => { if (point == "multi-relation-prepared") throw new OutOfMemoryException(); };
                Assert.Throws<OutOfMemoryException>(() => em.AddRelationship<T>(nextOwner, target));
                em.RelationshipFailureInjector = null;
                Assert.Equal(new[] { oldOwner }, em.GetRelationsOf<T>(target));
                Assert.Empty(em.GetRelationships<T>(nextOwner));
                Assert.Equal(0, events);
                em.AddRelationship<T>(nextOwner, target);
                Assert.Empty(em.GetRelationships<T>(oldOwner));
                Assert.Equal(new[] { nextOwner }, em.GetRelationsOf<T>(target));
                Assert.Equal(2, events);
            }
        }

        [Theory]
        [InlineData(false, "multi-unlink-prepared")] [InlineData(true, "multi-unlink-prepared")]
        [InlineData(false, "multi-clear-prepared")] [InlineData(true, "multi-clear-prepared")]
        public void RemoveAndClear_FailureKeepAllEdgesAndSuppressEvents(bool bounded, string point)
        {
            if (bounded) Run<BoundedLinks>(point); else Run<ListLinks>(point);
            static void Run<T>(string checkpoint) where T : unmanaged, IRelationComponent
            {
                using var world = new World("MultiRemovePreparation");
                var em = world.EntityManager;
                var source = New(em); var a = New(em); var b = New(em);
                em.AddRelationship<T>(source, a); em.AddRelationship<T>(source, b);
                int events = 0;
                em.AddObserver<T>(ObserverEvents.Removed, (_, _) => events++);
                em.RelationshipFailureInjector = p => { if (p == checkpoint) throw new OutOfMemoryException(); };
                if (checkpoint == "multi-clear-prepared") Assert.Throws<OutOfMemoryException>(() => em.ClearRelationships<T>(source));
                else Assert.Throws<OutOfMemoryException>(() => em.RemoveRelationship<T>(source, a));
                em.RelationshipFailureInjector = null;
                Assert.Equal(2, em.GetRelationshipCount<T>(source));
                Assert.Equal(0, events);
                em.ClearRelationships<T>(source);
                Assert.Empty(em.GetRelationships<T>(source));
                Assert.Equal(2, events);
            }
        }

        [Fact]
        public void ManagedStore_SeparatesSourceGenerations()
        {
            var store = new RelationListStore();
            var first = new Entity { Id = 7, Version = 0 };
            var reused = new Entity { Id = 7, Version = 1 };
            var target = RelationSlot.From(new Entity { Id = 3, Version = 0 });
            store.Add(1, first, target);
            Assert.False(store.Has(1, reused, target));
            Assert.Equal(0, store.GetCount(1, reused));
        }
    }
}
