using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct LifecycleRelation : IRelationComponent, IComponentData { public RelationSlot Target; public int Data; }
    [AcyclicRelation]
    public struct LifecycleParent : IRelationComponent { public RelationSlot Target; }
    [MultiRelation(MaxSlots = 3)]
    public partial struct IncompleteRelationSlots : IRelationComponent
    {
        public RelationSlot Target;
        public RelationSlot Slot1;
    }
    [MultiRelation(MaxSlots = 2)]
    public partial struct MisalignedRelationSlots : IRelationComponent
    {
        public RelationSlot Target;
        public int Data;
        public RelationSlot Slot1;
    }
    [MultiRelation(MaxSlots = -1)]
    public struct NegativeRelationSlots : IRelationComponent { public RelationSlot Target; }
    [ExclusiveRelation, MultiRelation]
    public struct ConflictingRelationModes : IRelationComponent { public RelationSlot Target; }
    [ExclusiveTarget]
    public struct SingleRelationWithMultiConstraint : IRelationComponent { public RelationSlot Target; }

    public struct RelationOwnedResource : IComponentData, IDisposable
    {
        public int Active;
        public static int Disposed;
        public static bool ThrowOnDispose;
        public static Action OnDispose;
        public void Dispose()
        {
            if (Active == 0) return;
            Disposed++;
            OnDispose?.Invoke();
            if (ThrowOnDispose) throw new InvalidOperationException("resource disposal failure");
        }
    }

    public struct LifecycleRelationReadJob : IJobChunk
    {
        public static int Started;
        public static int Finished;
        public static int Target;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Volatile.Write(ref Started, 1);
            Thread.Sleep(80);
            Target = chunk.GetComponentReadOnlyDataSpan<LifecycleRelation>()[0].Target.TargetId;
            Volatile.Write(ref Finished, 1);
        }
    }

    public struct UnrelatedRelationReaderJob : IJobChunk
    {
        public static readonly ManualResetEventSlim Started = new(false);
        public static readonly ManualResetEventSlim Release = new(false);
        public static int Finished;
        public static int ObservedCount;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(2));
            ObservedCount = chunk.GetComponentDataSpan<Velocity>().Length;
            Volatile.Write(ref Finished, 1);
        }
    }

    public unsafe class SingleRelationLifecycleTests
    {
        private static World NewWorld() => new("SingleRelation" + Guid.NewGuid().ToString("N"));
        private static Entity New(EntityManager em) => em.NewEntity(typeof(Position));
        private static bool Alive(EntityManager em, Entity entity)
        {
            ref var info = ref em.GetEntityInfoRef(entity.Id);
            return info.Archetype != null && info.Version == entity.Version;
        }

        [Fact]
        public void EmptyColumns_DoNotReferToEntityZeroOrRaiseAdded()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var zero = New(em);
            Assert.Equal(0, zero.Id);
            int added = 0;
            em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (_, _) => added++);
            var single = em.NewEntity(typeof(Position), typeof(LifecycleRelation));
            var batch = em.CreateEntities(3, typeof(Position), typeof(LifecycleRelation));
            Assert.False(world.TryGetRelationship<LifecycleRelation>(single, out _));
            foreach (var entity in batch) Assert.False(em.HasRelationship<LifecycleRelation>(entity));
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(zero));
            Assert.Equal(0, added);
        }

        [Fact]
        public void TypeRegistration_RejectsMissingAndMisalignedSlots()
        {
            Assert.Throws<InvalidOperationException>(() => ComponentTypeManager.GetComponentType(typeof(IncompleteRelationSlots)));
            Assert.Throws<InvalidOperationException>(() => ComponentTypeManager.GetComponentType(typeof(MisalignedRelationSlots)));
            Assert.Equal(32, ComponentTypeManager.GetComponentType(typeof(HotSkill)).Size);
        }

        [Theory]
        [InlineData(typeof(NegativeRelationSlots))]
        [InlineData(typeof(ConflictingRelationModes))]
        [InlineData(typeof(SingleRelationWithMultiConstraint))]
        public void TypeRegistration_RejectsContradictoryConstraints(Type type)
            => Assert.Throws<InvalidOperationException>(() => ComponentTypeManager.GetComponentType(type));

        [Fact]
        public void ComponentWrites_UpdateTheSameReverseIndexAndPreserveDataOnRetarget()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            em.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(a), Data = 17 });
            Assert.Single(em.GetRelationsOf<LifecycleRelation>(a));
            var arch = em.GetEntityInfoRef(source.Id).Archetype;
            int version = em.StructuralVersion;
            em.AddRelationship<LifecycleRelation>(source, b);
            Assert.Same(arch, em.GetEntityInfoRef(source.Id).Archetype);
            Assert.Equal(version, em.StructuralVersion);
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(a));
            Assert.Equal(17, arch.ChunkList[em.GetEntityInfoRef(source.Id).ChunkIndex]
                .GetComponentReadOnly<LifecycleRelation>(em.GetEntityInfoRef(source.Id).SlotInChunk,
                    arch.GetComponentTypeIndex(typeof(LifecycleRelation))).Data);
            em.SetComponent(source, new LifecycleRelation { Target = RelationSlot.From(a), Data = 23 });
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(b));
            Assert.Single(em.GetRelationsOf<LifecycleRelation>(a));
            em.SetRaw(source, typeof(LifecycleRelation), new LifecycleRelation { Target = RelationSlot.From(b), Data = 29 });
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(a));
            Assert.Single(em.GetRelationsOf<LifecycleRelation>(b));
            em.Set(source, new LifecycleRelation { Target = RelationSlot.From(a), Data = 37 });
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(b));
            em.RemoveComponent<LifecycleRelation>(source);
            Assert.False(em.TryGetRelationship<LifecycleRelation>(source, out _));
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(a));
        }

        [Fact]
        public void Retarget_UpdatesChangeTrackingWithoutAStructuralMove()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            em.AddRelationship<LifecycleRelation>(source, a);
            em.ClearAllChangedBitMasks();
            int structuralVersion = em.StructuralVersion;
            em.AddRelationship<LifecycleRelation>(source, b);
            ref var info = ref em.GetEntityInfoRef(source.Id);
            Assert.True(info.Archetype.ChunkList[info.ChunkIndex].IsEntityChanged(info.SlotInChunk));
            Assert.Equal(structuralVersion, em.StructuralVersion);
        }

        [Fact]
        public void InvalidTarget_FailsBeforeReplacingAnExistingEdge()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var current = New(em);
            var dead = New(em);
            em.AddRelationship<LifecycleRelation>(source, current);
            em.DestroyEntity(dead);
            Assert.Throws<InvalidOperationException>(() => em.AddRelationship<LifecycleRelation>(source, dead));
            Assert.Throws<InvalidOperationException>(() => em.SetComponent(source,
                new LifecycleRelation { Target = RelationSlot.From(dead) }));
            Assert.Equal(current.Id, em.GetRelationship<LifecycleRelation>(source).Id);
            Assert.Single(em.GetRelationsOf<LifecycleRelation>(current));
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(dead));
        }

        [Fact]
        public void RelationPrefabClone_AndUnsupportedSideTableSnapshot_PreserveState()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var target = New(em);
            var prefab = em.NewEntity(typeof(Position), typeof(Prefab));
            em.AddRelationship<LifecycleRelation>(prefab, target);
            long before = em.GetAllArchetypes().Sum(arch => arch.EntityCount);
            var copies = em.SpawnFrom(prefab, 2);
            Assert.Equal(before + 2, em.GetAllArchetypes().Sum(arch => arch.EntityCount));
            Assert.Equal(3, em.GetRelationsOf<LifecycleRelation>(target).Length);
            Assert.All(copies, copy => Assert.Equal(target, em.GetRelationship<LifecycleRelation>(copy)));
            var source = New(em);
            em.AddRelationship<Skill>(source, target);
            Assert.Throws<NotSupportedException>(() => em.TakeSnapshot());
            Assert.Equal(target, Assert.Single(em.GetRelationships<Skill>(source)));
            var plainPrefab = em.NewEntity(typeof(Position), typeof(Prefab));
            Assert.Single(em.SpawnFrom(plainPrefab, 1));
            var emptyRelationPrefab = em.NewEntity(typeof(Position), typeof(Prefab), typeof(LifecycleRelation));
            var emptyClone = Assert.Single(em.SpawnFrom(emptyRelationPrefab, 1));
            Assert.False(em.HasRelationship<LifecycleRelation>(emptyClone));
        }

        [Fact]
        public void AcyclicRelation_RejectsCyclesThroughAllWriteEntrypoints()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var a = New(em);
            var b = New(em);
            var c = New(em);
            em.AddRelationship<LifecycleParent>(a, b);
            em.AddRelationship<LifecycleParent>(b, c);
            Assert.Throws<InvalidOperationException>(() => em.AddRelationship<LifecycleParent>(c, a));
            Assert.Throws<InvalidOperationException>(() => em.AddComponent(c,
                new LifecycleParent { Target = RelationSlot.From(a) }));
            Assert.Throws<InvalidOperationException>(() => em.AddRelationship<LifecycleParent>(c, c));
            Assert.False(em.HasRelationship<LifecycleParent>(c));
            Assert.Equal(b.Id, em.GetRelationship<LifecycleParent>(a).Id);
            Assert.Equal(c.Id, em.GetRelationship<LifecycleParent>(b).Id);
        }

        [Fact]
        public void ExclusiveTransfer_ObserversSeeOnlyTheCommittedGraphOutsideTheLock()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var oldOwner = New(em);
            var nextOwner = New(em);
            var target = em.NewEntity(typeof(Velocity));
            em.AddRelationship<OccupiedBy>(oldOwner, target);
            var events = new List<string>();
            var gate = typeof(EntityManager).GetField("_structuralLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(em)!;
            em.AddObserver<OccupiedBy>(ObserverEvents.Removed, (_, _) =>
            {
                Assert.False(Monitor.IsEntered(gate));
                Assert.False(em.HasRelationship<OccupiedBy>(oldOwner));
                Assert.Equal(nextOwner.Id, em.GetRelationsOf<OccupiedBy>(target).Single().Id);
                events.Add("removed");
            });
            em.AddObserver<OccupiedBy>(ObserverEvents.Added, (_, _) =>
            {
                Assert.False(Monitor.IsEntered(gate));
                Assert.Equal(target.Id, em.GetRelationship<OccupiedBy>(nextOwner).Id);
                events.Add("added");
            });
            em.AddRelationship<OccupiedBy>(nextOwner, target);
            Assert.Equal(new[] { "removed", "added" }, events);
            Assert.False(em.GetEntityInfoRef(oldOwner.Id).Archetype.Has(typeof(OccupiedBy)));
        }

        [Fact]
        public void RelationEvents_HaveCompletePayloadAndDoNotRepeatNoOpAdds()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            var values = new List<int>();
            em.AddObserver<LifecycleRelation>(ObserverEvents.Added | ObserverEvents.Set | ObserverEvents.Removed,
                (_, payload) => values.Add(payload[0].Data));
            em.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(a), Data = 71 });
            em.AddRelationship<LifecycleRelation>(source, a);
            em.AddRelationship<LifecycleRelation>(source, b);
            em.RemoveRelationship<LifecycleRelation>(source);
            Assert.Equal(new[] { 71, 71, 71 }, values);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void AllDeletionEntrypoints_HonorDeclaredCascade(int entrypoint)
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var root = New(em);
            var child = New(em);
            var grandchild = New(em);
            em.AddRelationship<CascadeChildOf>(child, root);
            em.AddRelationship<CascadeChildOf>(grandchild, child);
            var observed = new List<int>();
            em.AddObserver<Position>(ObserverEvents.Destroyed, (entities, _) =>
            {
                Assert.False(Alive(em, root));
                Assert.False(Alive(em, child));
                Assert.False(Alive(em, grandchild));
                observed.Add(entities[0].Id);
            });
            if (entrypoint == 0) em.DestroyEntity(root);
            else if (entrypoint == 1) { Entity* roots = stackalloc Entity[1]; roots[0] = root; Assert.Equal(3, em.DestroyEntities(roots, 1)); }
            else if (entrypoint == 2) Assert.Equal(3, em.DestroyAllInArchetype(em.GetEntityInfoRef(root.Id).Archetype));
            else { using var ecb = new DeferredCommandBuffer(); ecb.DestroyEntity(root); ecb.Playback(em); }
            Assert.Equal(new[] { grandchild.Id, child.Id, root.Id }, observed);
            Assert.Empty(em.GetRelationsOf<CascadeChildOf>(root));
        }

        [Fact]
        public void TargetDeletion_UnlinksSurvivorsAndDoesNotLeaveAnEmptyRelationshipColumn()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var target = New(em);
            var source = New(em);
            em.AddRelationship<LifecycleRelation>(source, target);
            int removed = 0;
            em.AddObserver<LifecycleRelation>(ObserverEvents.Removed, (_, values) =>
            {
                Assert.Equal(target.Id, values[0].Target.TargetId);
                Assert.False(Alive(em, target));
                Assert.False(em.HasRelationship<LifecycleRelation>(source));
                removed++;
            });
            em.DestroyEntity(target);
            Assert.True(Alive(em, source));
            Assert.False(em.GetEntityInfoRef(source.Id).Archetype.Has(typeof(LifecycleRelation)));
            Assert.Equal(1, removed);
        }

        [Fact]
        public void EcbComponentWrites_CannotBypassRelationshipMaintenance()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            em.AddRelationship<LifecycleRelation>(source, a);
            using var ecb = new DeferredCommandBuffer();
            ecb.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(b), Data = 5 });
            ecb.Playback(em);
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(a));
            Assert.Single(em.GetRelationsOf<LifecycleRelation>(b));
        }

        [Fact]
        public void ParallelWriterComponentWrites_UseTheSameRelationshipCore()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            em.AddRelationship<LifecycleRelation>(source, a);
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(1);
            var writer = ecb.CreateParallelWriter(0);
            writer.SetComponent(source, new LifecycleRelation { Target = RelationSlot.From(b), Data = 31 });
            ecb.PlaybackParallel(em);
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(a));
            Assert.Single(em.GetRelationsOf<LifecycleRelation>(b));
        }

        [Fact]
        public void ObserverMutation_IsRejectedAndAnObserverExceptionDoesNotRollBackTheGraph()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            bool rejected = false;
            int notified = 0;
            em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (_, _) =>
            {
                try { em.AddRelationship<LifecycleRelation>(source, b); }
                catch (InvalidOperationException) { rejected = true; }
                Assert.Throws<InvalidOperationException>(() => em.AddComponent(source,
                    new LifecycleRelation { Target = RelationSlot.From(b) }));
                Assert.Throws<InvalidOperationException>(() => em.RemoveComponent<LifecycleRelation>(source));
                throw new Exception("observer failure");
            });
            em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (_, _) => notified++);
            em.AddRelationship<LifecycleRelation>(source, a);
            Assert.True(rejected);
            Assert.Equal(1, notified);
            Assert.Equal(a.Id, em.GetRelationship<LifecycleRelation>(source).Id);
        }

        [Fact]
        public void RelationMigration_MovesResourceOwnershipAndDeletionFinishesAfterDisposeThrows()
        {
            ComponentTypeManager.RegisterDisposable<RelationOwnedResource>();
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position), typeof(RelationOwnedResource));
            em.GetComponent<RelationOwnedResource>(source).Active = 1;
            var target = New(em);
            RelationOwnedResource.Disposed = 0;
            RelationOwnedResource.ThrowOnDispose = false;
            em.AddRelationship<LifecycleRelation>(source, target);
            em.RemoveRelationship<LifecycleRelation>(source);
            Assert.Equal(0, RelationOwnedResource.Disposed);
            em.AddRelationship<LifecycleRelation>(source, target);
            RelationOwnedResource.ThrowOnDispose = true;
            try { em.DestroyEntity(source); }
            finally { RelationOwnedResource.ThrowOnDispose = false; }
            Assert.Equal(1, RelationOwnedResource.Disposed);
            Assert.False(Alive(em, source));
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(target));
        }

        [Fact]
        public void Retarget_WaitsForTheExistingReaderBeforeChangingTheColumn()
        {
            TestBackend.EnsureInitialized(2);
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            em.AddRelationship<LifecycleRelation>(source, a);
            LifecycleRelationReadJob.Started = LifecycleRelationReadJob.Finished = 0;
            var job = new LifecycleRelationReadJob().Schedule(new QueryBuilder().WithAll<LifecycleRelation>(), world);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref LifecycleRelationReadJob.Started) != 0, TimeSpan.FromSeconds(3)));
                em.AddRelationship<LifecycleRelation>(source, b);
                Assert.Equal(1, Volatile.Read(ref LifecycleRelationReadJob.Finished));
                Assert.Equal(a.Id, LifecycleRelationReadJob.Target);
                Assert.Equal(b.Id, em.GetRelationship<LifecycleRelation>(source).Id);
            }
            finally { job.Complete(); }
        }

        [Fact]
        public void ResourceDispose_CannotReenterEntityDestruction()
        {
            ComponentTypeManager.RegisterDisposable<RelationOwnedResource>();
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(RelationOwnedResource));
            em.GetComponent<RelationOwnedResource>(source).Active = 1;
            var target = New(em);
            em.AddRelationship<LifecycleRelation>(source, target);
            bool rejected = false;
            RelationOwnedResource.ThrowOnDispose = false;
            RelationOwnedResource.OnDispose = () =>
            {
                Assert.Throws<InvalidOperationException>(() => em.DestroyEntity(target));
                rejected = true;
            };
            try { em.DestroyEntity(source); }
            finally { RelationOwnedResource.OnDispose = null; }
            Assert.True(rejected);
            Assert.True(Alive(em, target));
            Assert.False(Alive(em, source));
        }

        [Fact]
        public void ValueOnlyRetarget_DoesNotWaitForAnUnrelatedArchetypeReader()
        {
            TestBackend.EnsureInitialized(2);
            using var world = NewWorld();
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            em.AddRelationship<LifecycleRelation>(source, a);
            em.NewEntity(typeof(Velocity));
            UnrelatedRelationReaderJob.Started.Reset();
            UnrelatedRelationReaderJob.Release.Reset();
            UnrelatedRelationReaderJob.Finished = 0;
            var job = new UnrelatedRelationReaderJob().Schedule(new QueryBuilder().WithAll<Velocity>(), world);
            try
            {
                Assert.True(UnrelatedRelationReaderJob.Started.Wait(TimeSpan.FromSeconds(3)));
                em.AddRelationship<LifecycleRelation>(source, b);
                Assert.Equal(0, Volatile.Read(ref UnrelatedRelationReaderJob.Finished));
                Assert.Equal(b.Id, em.GetRelationship<LifecycleRelation>(source).Id);
            }
            finally { UnrelatedRelationReaderJob.Release.Set(); job.Complete(); }
        }

        [Fact]
        public void MixedWritesAndDeletes_AgreeWithAnIndependentEdgeMap()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var alive = Enumerable.Range(0, 64).Select(_ => New(em)).ToList();
            var expected = new Dictionary<Entity, Entity>();
            var random = new Random(731);
            for (int step = 0; step < 500; step++)
            {
                var source = alive[random.Next(alive.Count)];
                var target = alive[random.Next(alive.Count)];
                switch (random.Next(4))
                {
                    case 0:
                        em.AddRelationship<LifecycleRelation>(source, target);
                        expected[source] = target;
                        break;
                    case 1:
                        em.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(target), Data = step });
                        expected[source] = target;
                        break;
                    case 2:
                        em.RemoveComponent<LifecycleRelation>(source);
                        expected.Remove(source);
                        break;
                    default:
                        em.DestroyEntity(source);
                        alive.Remove(source);
                        foreach (var key in expected.Where(pair => pair.Key.Equals(source) || pair.Value.Equals(source)).Select(pair => pair.Key).ToArray())
                            expected.Remove(key);
                        alive.Add(New(em));
                        break;
                }
                foreach (var entity in alive)
                {
                    bool found = em.TryGetRelationship<LifecycleRelation>(entity, out var actual);
                    Assert.Equal(expected.ContainsKey(entity), found);
                    if (found) Assert.Equal(expected[entity], actual);
                    var indexed = em.GetRelationsOf<LifecycleRelation>(entity);
                    var wanted = expected.Where(pair => pair.Value.Equals(entity)).Select(pair => pair.Key).ToHashSet();
                    Assert.True(wanted.SetEquals(indexed));
                }
            }
        }

        [Fact]
        public void ExclusiveTransfersAcrossChunks_PreserveTheBijectionAndSlabOwnership()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var sources = Enumerable.Range(0, 500).Select(_ => New(em)).ToArray();
            var targets = Enumerable.Range(0, 160).Select(_ => em.NewEntity(typeof(Velocity))).ToArray();
            var forward = new Dictionary<Entity, Entity>();
            var reverse = new Dictionary<Entity, Entity>();
            var random = new Random(1039);
            for (int step = 0; step < 1500; step++)
            {
                var source = sources[random.Next(sources.Length)];
                var target = targets[random.Next(targets.Length)];
                if (forward.Remove(source, out var oldTarget)) reverse.Remove(oldTarget);
                if (reverse.Remove(target, out var oldSource)) forward.Remove(oldSource);
                forward[source] = target;
                reverse[target] = source;
                em.AddRelationship<OccupiedBy>(source, target);
                if (step % 37 != 0 && step != 1499) continue;
                foreach (var candidate in sources)
                {
                    Assert.Equal(forward.ContainsKey(candidate), em.TryGetRelationship<OccupiedBy>(candidate, out var actual));
                    if (forward.TryGetValue(candidate, out var wanted)) Assert.Equal(wanted, actual);
                }
                foreach (var cell in targets)
                {
                    var actual = em.GetRelationsOf<OccupiedBy>(cell);
                    if (reverse.TryGetValue(cell, out var wanted)) Assert.Equal(wanted, Assert.Single(actual));
                    else Assert.Empty(actual);
                }
                foreach (var arch in em.GetAllArchetypes()) Assert.Equal(0, arch.VerifySlabInvariants());
            }
        }
    }
}
