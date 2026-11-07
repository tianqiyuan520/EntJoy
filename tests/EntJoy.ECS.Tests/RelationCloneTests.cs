using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct CloneSharedValue : ISharedComponentData
    {
        public int Value;
        public static bool FailComparison;
        public override bool Equals(object other)
        {
            if (FailComparison) throw new InvalidOperationException("shared comparison failed");
            return other is CloneSharedValue value && value.Value == Value;
        }
        public override int GetHashCode() => Value;
    }
    public unsafe struct CloneOwned : IComponentData, IDisposable, ICopyable<CloneOwned>
    {
        public nint Pointer;
        public static int Copies, Disposals, Live, FailAt;
        public static bool ThrowOnDispose;
        public static Action DuringCopy;
        public static Action DuringDispose;
        public void OnCopy(in CloneOwned source, ref CloneOwned destination)
        {
            Copies++;
            if (source.Pointer != 0)
            {
                destination.Pointer = Marshal.AllocHGlobal(sizeof(int));
                *(int*)destination.Pointer = *(int*)source.Pointer;
                Live++;
            }
            DuringCopy?.Invoke();
            if (Copies == FailAt) throw new InvalidOperationException("copy preparation failed");
        }
        public void Dispose()
        {
            if (Pointer == 0) return;
            Marshal.FreeHGlobal(Pointer); Pointer = 0; Live--; Disposals++;
            DuringDispose?.Invoke();
            if (ThrowOnDispose) throw new InvalidOperationException("owned disposal failed");
        }
        public static CloneOwned Create(int value)
        {
            ComponentTypeManager.RegisterDisposable<CloneOwned>();
            ComponentTypeManager.RegisterCopyable<CloneOwned>();
            var result = new CloneOwned { Pointer = Marshal.AllocHGlobal(sizeof(int)) };
            *(int*)result.Pointer = value; Live++;
            return result;
        }
        public static void Reset()
        {
            Assert.Equal(0, Live);
            Copies = Disposals = FailAt = 0; ThrowOnDispose = false; DuringCopy = DuringDispose = null;
        }
    }

    public unsafe class RelationCloneTests
    {
        private static Entity New(EntityManager em) => em.NewEntity(typeof(Position));
        private static int Live(EntityManager em) => em.GetAllArchetypes().Sum(arch => arch.EntityCount);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CloneAndRestore_WaitForExistingRelationshipReaders(bool restore)
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("CloneRestoreReadFence");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position), typeof(LifecycleRelation));
            var target = New(em);
            em.AddRelationship<LifecycleRelation>(source, target);
            var snapshot = em.TakeSnapshot();
            Volatile.Write(ref LifecycleRelationReadJob.Started, 0);
            Volatile.Write(ref LifecycleRelationReadJob.Finished, 0);
            var handle = new LifecycleRelationReadJob().Schedule(new QueryBuilder().WithAll<LifecycleRelation>(), world);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref LifecycleRelationReadJob.Started) == 1, TimeSpan.FromSeconds(3)));
                if (restore) em.Restore(snapshot); else em.CloneEntities(new[] { source });
                Assert.Equal(1, Volatile.Read(ref LifecycleRelationReadJob.Finished));
                Assert.Equal(target.Id, LifecycleRelationReadJob.Target);
                Assert.Equal(0, em.VerifyLocateTable(out _));
            }
            finally { handle.Complete(); }
        }

        [Fact]
        public void ExclusiveConflict_IsRejectedBeforeCopyHooksRun()
        {
            CloneOwned.Reset();
            using (var world = new World("CloneExclusivePreflight"))
            {
                var em = world.EntityManager;
                var prefab = em.NewEntity(typeof(Position), typeof(Prefab), typeof(CloneOwned));
                var target = New(em);
                em.SetComponent(prefab, CloneOwned.Create(31));
                em.AddRelationship<OccupiedBy>(prefab, target);
                Assert.Throws<InvalidOperationException>(() => em.SpawnFrom(prefab, 3));
                Assert.Equal(0, CloneOwned.Copies); Assert.Equal(1, CloneOwned.Live);
                Assert.Equal(prefab, Assert.Single(em.GetRelationsOf<OccupiedBy>(target)));
            }
            Assert.Equal(0, CloneOwned.Live);
        }

        [Fact]
        public void DestinationPreparationFailure_DisposesCopiesAndLeavesTheSourceUsable()
        {
            CloneOwned.Reset();
            using (var world = new World("CloneDestinationFailure"))
            {
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position), typeof(CloneOwned), typeof(CloneSharedValue));
                em.SetComponent(source, CloneOwned.Create(54));
                em.SetSharedComponent(source, new CloneSharedValue { Value = 7 });
                int live = Live(em), issued = em.EntityCount;
                CloneSharedValue.FailComparison = true;
                try { Assert.Throws<InvalidOperationException>(() => em.CloneEntities(new[] { source })); }
                finally { CloneSharedValue.FailComparison = false; }
                Assert.Equal(1, CloneOwned.Live); Assert.Equal(1, CloneOwned.Disposals);
                Assert.Equal(live, Live(em)); Assert.Equal(issued, em.EntityCount);
                Assert.Equal(54, *(int*)em.GetComponentReadOnly<CloneOwned>(source).Pointer);
                Assert.Equal(7, em.GetSharedComponent<CloneSharedValue>(source).Value);
                Assert.Single(em.CloneEntities(new[] { source }));
                Assert.Equal(2, CloneOwned.Live);
            }
            Assert.Equal(0, CloneOwned.Live);
        }

        [Fact]
        public void GroupClone_RemapsInternalTargetsAndKeepsExternalTargetsAndPayloads()
        {
            using var world = new World("RelationGroupClone");
            var em = world.EntityManager;
            var external = New(em);
            var parent = em.NewEntity(typeof(Position), typeof(Prefab));
            var child = New(em);
            em.AddRelationship<LifecycleParent>(parent, external);
            em.AddRelationship<LifecycleParent>(child, parent);
            em.AddComponent(child, new LifecycleRelation { Target = RelationSlot.From(parent), Data = 72 });
            int observed = 0;
            em.AddObserver<LifecycleParent>(ObserverEvents.Added, (entities, values) =>
            {
                Assert.Equal(5, Live(em));
                for (int i = 0; i < entities.Length; i++)
                {
                    Assert.True(em.IsRelationshipTargetAlive(values[i].Target));
                    Assert.Contains(entities[i], em.GetRelationsOf<LifecycleParent>(values[i].Target.ToEntity()));
                    observed++;
                }
            });
            var copies = world.CloneEntities(new[] { child, parent });
            Assert.Equal(copies[1], em.GetRelationship<LifecycleParent>(copies[0]));
            Assert.Equal(external, em.GetRelationship<LifecycleParent>(copies[1]));
            Assert.Equal(parent, em.GetRelationship<LifecycleParent>(child));
            Assert.Equal(72, em.GetComponentReadOnly<LifecycleRelation>(copies[0]).Data);
            Assert.Equal(copies[1], em.GetRelationship<LifecycleRelation>(copies[0]));
            Assert.All(copies, copy => Assert.False(em.GetEntityInfoRef(copy.Id).Archetype.Has(typeof(Prefab))));
            Assert.Equal(2, observed);
            Assert.Equal(new[] { copies[0] }, em.GetRelationsOf<LifecycleParent>(copies[1]));
            Assert.Equal(0, em.VerifyLocateTable(out _));
        }

        [Fact]
        public void PrefabSelfReferences_RemapSeparatelyForEveryInstanceAndKeepBatchNotifications()
        {
            int capacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("RelationSelfPrefab");
                var em = world.EntityManager;
                var prefab = em.NewEntity(typeof(Position), typeof(Prefab), typeof(LifecycleRelation));
                em.SetComponent(prefab, new LifecycleRelation { Target = RelationSlot.From(prefab), Data = 91 });
                int callbacks = 0, edges = 0;
                em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (entities, values) =>
                {
                    callbacks++;
                    Assert.Equal(130, entities.Length);
                    for (int i = 0; i < entities.Length; i++) { Assert.True(values[i].Target.Matches(entities[i])); edges++; }
                });
                var copies = world.SpawnFrom(prefab, 130);
                Assert.Equal(1, callbacks); Assert.Equal(130, edges);
                foreach (var copy in copies)
                {
                    Assert.Equal(copy, em.GetRelationship<LifecycleRelation>(copy));
                    Assert.Equal(91, em.GetComponentReadOnly<LifecycleRelation>(copy).Data);
                    Assert.Equal(new[] { copy }, em.GetRelationsOf<LifecycleRelation>(copy));
                }
                em.DestroyEntity(copies[0]);
                Assert.All(copies.Skip(1), copy => Assert.Equal(copy, em.GetRelationship<LifecycleRelation>(copy)));
                Assert.Equal(new[] { prefab }, em.GetRelationsOf<LifecycleRelation>(prefab));
                Assert.Equal(0, em.VerifyLocateTable(out _));
            }
            finally { Archetype.ChunkCapacityOverride = capacity; }
        }

        [Fact]
        public void ExclusiveInternalTargets_CopyWithoutDisplacingOriginalOwners()
        {
            using var world = new World("ExclusiveGroupClone");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position), typeof(Prefab));
            var target = New(em);
            em.AddRelationship<OccupiedBy>(source, target);
            int live = Live(em), version = em.StructuralVersion;
            Assert.Throws<InvalidOperationException>(() => world.SpawnFrom(source, 2));
            Assert.Throws<InvalidOperationException>(() => world.CloneEntities(new[] { source }));
            Assert.Equal(live, Live(em)); Assert.Equal(version, em.StructuralVersion);
            Assert.Equal(source, Assert.Single(em.GetRelationsOf<OccupiedBy>(target)));
            var copies = world.CloneEntities(new[] { target, source });
            Assert.Equal(copies[0], em.GetRelationship<OccupiedBy>(copies[1]));
            Assert.Equal(copies[1], Assert.Single(em.GetRelationsOf<OccupiedBy>(copies[0])));
            Assert.Equal(source, Assert.Single(em.GetRelationsOf<OccupiedBy>(target)));
        }

        [Fact]
        public void SharedValuesAndEnabledBits_AreCopiedWithoutChangingExistingChunks()
        {
            int capacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("SharedRelationClone");
                var em = world.EntityManager;
                var prefab = em.NewEntity(typeof(Position), typeof(Prefab), typeof(Material), typeof(MeshAsset), typeof(RelationReadEnabled));
                var mesh = new MeshAsset("source");
                em.SetSharedComponent(prefab, new Material(7)); em.SetSharedComponent(prefab, mesh);
                em.SetComponentEnabled<RelationReadEnabled>(prefab, false);
                var foreign = em.NewEntity(typeof(Position), typeof(Material), typeof(MeshAsset), typeof(RelationReadEnabled));
                em.SetSharedComponent(foreign, new Material(99)); em.SetSharedComponent(foreign, new MeshAsset("other"));
                var copies = world.SpawnFrom(prefab, 130);
                Assert.All(copies, copy =>
                {
                    Assert.Equal(7, em.GetSharedComponent<Material>(copy).Id);
                    Assert.Same(mesh, em.GetSharedComponent<MeshAsset>(copy));
                    Assert.False(em.IsComponentEnabled<RelationReadEnabled>(copy));
                });
                Assert.Equal(99, em.GetSharedComponent<Material>(foreign).Id);
                Assert.Equal("other", em.GetSharedComponent<MeshAsset>(foreign).Name);
                Assert.True(em.IsComponentEnabled<RelationReadEnabled>(foreign));
                Assert.Equal(0, em.VerifyLocateTable(out _));
            }
            finally { Archetype.ChunkCapacityOverride = capacity; }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void CopyHookFailure_ReleasesPreparedAndPartialResourcesWithoutPublishing(int failAt)
        {
            CloneOwned.Reset();
            using (var world = new World("CloneResourceFailure"))
            {
                var em = world.EntityManager;
                var prefab = em.NewEntity(typeof(Position), typeof(Prefab), typeof(CloneOwned));
                em.SetComponent(prefab, CloneOwned.Create(83));
                int version = em.StructuralVersion, live = Live(em), issued = em.EntityCount;
                int notifications = 0;
                em.AddObserver<Position>(ObserverEvents.Added, (_, _) => notifications++);
                var lookup = em.GetReadOnlyComponentLookup<Position>();
                CloneOwned.FailAt = failAt;
                Assert.Throws<InvalidOperationException>(() => world.SpawnFrom(prefab, 4));
                Assert.Equal(1, CloneOwned.Live); Assert.Equal(failAt, CloneOwned.Disposals);
                Assert.Equal(live, Live(em)); Assert.Equal(issued, em.EntityCount);
                Assert.Equal(version, em.StructuralVersion); Assert.Equal(0, notifications);
                Assert.Equal(0f, lookup[prefab].X);
                Assert.Equal(83, *(int*)em.GetComponentReadOnly<CloneOwned>(prefab).Pointer);
                CloneOwned.FailAt = 0;
                var copies = world.SpawnFrom(prefab, 2);
                Assert.Equal(3, CloneOwned.Live);
                Assert.All(copies, copy => Assert.NotEqual(em.GetComponentReadOnly<CloneOwned>(prefab).Pointer, em.GetComponentReadOnly<CloneOwned>(copy).Pointer));
            }
            Assert.Equal(0, CloneOwned.Live);
        }

        [Theory]
        [InlineData("set")]
        [InlineData("borrow")]
        [InlineData("world-dispose")]
        public void CopyHook_EcsMutationAndWritableBorrowAreRejected(string mutation)
        {
            CloneOwned.Reset();
            using (var world = new World("CloneCopyGuard"))
            {
                var em = world.EntityManager;
                var source = em.NewEntity(typeof(Position), typeof(CloneOwned));
                em.SetComponent(source, CloneOwned.Create(44));
                CloneOwned.DuringCopy = () =>
                {
                    if (mutation == "world-dispose") world.Dispose();
                    else if (mutation == "borrow") em.GetComponentLookup<Position>()[source].X = 99;
                    else em.SetComponent(source, new Position { X = 99 });
                };
                Assert.Throws<InvalidOperationException>(() => em.CloneEntities(new[] { source }));
                Assert.Equal(1, CloneOwned.Live);
                Assert.Equal(0f, em.GetComponentReadOnly<Position>(source).X);
                Assert.Equal(1, world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>()).CalculateEntityCount());
                CloneOwned.DuringCopy = null;
            }
            Assert.Equal(0, CloneOwned.Live);
        }

        [Fact]
        public void InvalidOrDuplicateSources_FailBeforeIssuingIds_AndRecycledIdsRemainDistinct()
        {
            using var world = new World("CloneIdentities");
            var em = world.EntityManager;
            var source = New(em); var dead = New(em);
            em.DestroyEntity(dead);
            int issued = em.EntityCount, version = em.StructuralVersion;
            Assert.Throws<ArgumentException>(() => em.CloneEntities(new[] { source, source }));
            Assert.Throws<InvalidOperationException>(() => em.CloneEntities(new[] { source, dead }));
            Assert.Equal(issued, em.EntityCount); Assert.Equal(version, em.StructuralVersion);
            Assert.Empty(em.CloneEntities(Array.Empty<Entity>()));
            var copy = Assert.Single(em.CloneEntities(new[] { source }));
            Assert.Equal(dead.Id, copy.Id); Assert.NotEqual(dead.Version, copy.Version);
            Assert.Equal(0, em.VerifyLocateTable(out _));
        }

        [Fact]
        public void LongAcyclicGroup_ClonesAndRestoresWithoutRecursiveTraversal()
        {
            using var world = new World("CloneLongGraph");
            var em = world.EntityManager;
            var entities = em.CreateEntities(10000, typeof(Position), typeof(LifecycleParent));
            for (int i = 0; i < entities.Length - 1; i++) em.AddRelationship<LifecycleParent>(entities[i], entities[i + 1]);
            var copies = em.CloneEntities(entities);
            for (int i = 0; i < copies.Length - 1; i++) Assert.Equal(copies[i + 1], em.GetRelationship<LifecycleParent>(copies[i]));
            var snapshot = em.TakeSnapshot();
            em.Restore(snapshot);
            Assert.Equal(copies[9999], em.GetRelationship<LifecycleParent>(copies[9998]));
            Assert.Equal(0, em.VerifyLocateTable(out _));
        }
    }
}
