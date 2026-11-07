using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct RecordCommandMarker : IComponentData { }
    public struct RecordUnderEcbLockJob : IJobChunk
    {
        public DeferredCommandBuffer Buffer;
        public object RecordingLock;
        public ManualResetEventSlim Started;
        public Entity Source, Target;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            Started.Set();
            Thread.Sleep(40);
            if (!Monitor.TryEnter(RecordingLock, TimeSpan.FromSeconds(1))) throw new TimeoutException("ECB waited for a producer while holding its recording lock.");
            try { Buffer.SetRelationshipTarget<LifecycleRelation>(Source, Target); }
            finally { Monitor.Exit(RecordingLock); }
        }
    }

    public unsafe class RelationCommandTests
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 2048)]
        private struct LargeRelation : IRelationComponent { public RelationSlot Target; public int Data; }
        private static Entity New(EntityManager manager) => manager.NewEntity(typeof(Position));

        [Fact]
        public void Playback_WaitsForTrackedRecordersBeforeAcquiringTheRecordingLock()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("EcbRecordingFence");
            using var ecb = new DeferredCommandBuffer();
            using var started = new ManualResetEventSlim(false);
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position), typeof(RecordCommandMarker));
            var target = New(em);
            var recordingLock = typeof(DeferredCommandBuffer).GetField("_sync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(ecb);
            var handle = new RecordUnderEcbLockJob { Buffer = ecb, RecordingLock = recordingLock, Started = started, Source = source, Target = target }
                .Schedule(new QueryBuilder().WithAll<RecordCommandMarker>(), world);
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
                ecb.Playback(em);
                Assert.Equal(target, em.GetRelationship<LifecycleRelation>(source));
            }
            finally { handle.Complete(); }
        }

        [Fact]
        public void LargePayload_TargetSetPreservesAllFields()
        {
            using var world = new World("LargeRelationCommands");
            var em = world.EntityManager;
            var source = New(em); var a = New(em); var b = New(em);
            em.AddComponent(source, new LargeRelation { Target = RelationSlot.From(a), Data = 99 });
            using var ecb = new DeferredCommandBuffer();
            ecb.SetRelationshipTarget<LargeRelation>(source, b);
            ecb.Playback(em);
            Assert.Equal(b, em.GetRelationship<LargeRelation>(source));
            Assert.Equal(99, em.GetComponentReadOnly<LargeRelation>(source).Data);
        }

        [Fact]
        public void OrdinaryParallelComponentFailure_AlsoConsumesTheUnexecutedWriters()
        {
            using var world = new World("OrdinaryWriterFailure");
            var em = world.EntityManager;
            var source = em.NewEntity(typeof(Position), typeof(LifecycleParent)); var suffix = New(em);
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(2);
            var writer = ecb.CreateParallelWriter(0);
            writer.SetComponent(source, new Position { X = 1 });
            writer.SetComponent(source, new LifecycleParent { Target = RelationSlot.From(source) });
            ecb.CreateParallelWriter(1).SetComponent(suffix, new Position { X = 2 });
            var error = Assert.Throws<CommandPlaybackException>(() => ecb.PlaybackParallel(em));
            Assert.Equal(1, error.CommandIndex);
            Assert.Equal(1, error.UnexecutedCommandCount);
            Assert.Equal(0f, em.GetComponentReadOnly<Position>(suffix).X);
            em.SetComponent(source, default(Position));
            ecb.PlaybackParallel(em);
            Assert.Equal(0f, em.GetComponentReadOnly<Position>(source).X);
            Assert.Equal(0f, em.GetComponentReadOnly<Position>(suffix).X);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TargetSet_PreservesPayloadAndNoOpEvents_AndUnlinkRemovesTheColumn(bool parallel)
        {
            using var world = new World("RelationCommands");
            var em = world.EntityManager;
            var source = New(em);
            var a = New(em);
            var b = New(em);
            em.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(a), Data = 73 });
            int notifications = 0;
            em.AddObserver<LifecycleRelation>(ObserverEvents.Set | ObserverEvents.Removed, (_, values) =>
            { Assert.Equal(73, values[0].Data); notifications++; });
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(1);
            var writer = ecb.CreateParallelWriter(0);
            if (parallel) { writer.SetRelationshipTarget<LifecycleRelation>(1, source, b); writer.SetRelationshipTarget<LifecycleRelation>(2, source, b); }
            else { ecb.SetRelationshipTarget<LifecycleRelation>(source, b); ecb.SetRelationshipTarget<LifecycleRelation>(source, b); }
            Assert.Equal(a, em.GetRelationship<LifecycleRelation>(source));
            int structural = em.StructuralVersion;
            Playback();
            Assert.Equal(structural, em.StructuralVersion);
            Assert.Equal(b, em.GetRelationship<LifecycleRelation>(source));
            Assert.Equal(1, notifications);
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(a));
            Assert.Equal(new[] { source }, em.GetRelationsOf<LifecycleRelation>(b));
            if (parallel) writer.RemoveRelationship<LifecycleRelation>(1, source); else ecb.RemoveRelationship<LifecycleRelation>(source);
            Playback();
            Assert.False(em.GetEntityInfoRef(source.Id).Archetype.Has(typeof(LifecycleRelation)));
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(b));
            Assert.Equal(2, notifications);
            void Playback() { if (parallel) ecb.PlaybackParallel(em); else ecb.Playback(em); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Playback_RevalidatesTargetVersion_ConsumesFailureAndDoesNotReplayThePrefix(bool parallel)
        {
            using var world = new World("RelationCommandFailure");
            var em = world.EntityManager;
            var source = New(em);
            var prefix = New(em);
            var suffix = New(em);
            var a = New(em);
            var doomed = New(em);
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(1);
            var writer = ecb.CreateParallelWriter(0);
            if (parallel)
            {
                writer.SetRelationshipTarget<LifecycleRelation>(1, prefix, a);
                writer.SetRelationshipTarget<LifecycleRelation>(2, source, doomed);
                writer.SetRelationshipTarget<LifecycleRelation>(3, suffix, a);
            }
            else
            {
                ecb.SetRelationshipTarget<LifecycleRelation>(prefix, a);
                ecb.SetRelationshipTarget<LifecycleRelation>(source, doomed);
                ecb.SetRelationshipTarget<LifecycleRelation>(suffix, a);
            }
            em.DestroyEntity(doomed);
            var replacement = New(em);
            Assert.Equal(doomed.Id, replacement.Id);
            var error = Assert.Throws<CommandPlaybackException>(Playback);
            Assert.Equal(1, error.CommandIndex);
            Assert.Equal(1, error.UnexecutedCommandCount);
            Assert.Equal(parallel ? 0 : -1, error.WriterIndex);
            Assert.Equal(a, em.GetRelationship<LifecycleRelation>(prefix));
            Assert.False(em.HasRelationship<LifecycleRelation>(source));
            Assert.False(em.HasRelationship<LifecycleRelation>(suffix));
            em.RemoveRelationship<LifecycleRelation>(prefix);
            Playback();
            Assert.False(em.HasRelationship<LifecycleRelation>(prefix));
            if (parallel) writer.SetRelationshipTarget<LifecycleRelation>(0, source, replacement);
            else ecb.SetRelationshipTarget<LifecycleRelation>(source, replacement);
            Playback();
            Assert.Equal(replacement, em.GetRelationship<LifecycleRelation>(source));
            void Playback() { if (parallel) ecb.PlaybackParallel(em); else ecb.Playback(em); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SourceDestroyedByAnEarlierCommand_IsSkipped(bool parallel)
        {
            using var world = new World("RelationCommandDestroyedSource");
            var em = world.EntityManager;
            var source = New(em);
            var target = New(em);
            using var ecb = new DeferredCommandBuffer();
            if (parallel)
            {
                ecb.EnsureParallelWriters(1);
                var writer = ecb.CreateParallelWriter(0);
                writer.DestroyEntity(source);
                writer.SetRelationshipTarget<LifecycleRelation>(1, source, target);
                writer.RemoveRelationship<LifecycleRelation>(2, source);
                ecb.PlaybackParallel(em);
            }
            else
            {
                ecb.DestroyEntity(source);
                ecb.SetRelationshipTarget<LifecycleRelation>(source, target);
                ecb.RemoveRelationship<LifecycleRelation>(source);
                ecb.Playback(em);
            }
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(target));
            var replacement = New(em);
            Assert.Equal(source.Id, replacement.Id);
            if (parallel)
            {
                ecb.CreateParallelWriter(0).SetRelationshipTarget<LifecycleRelation>(0, source, target);
                ecb.PlaybackParallel(em);
            }
            else { ecb.SetRelationshipTarget<LifecycleRelation>(source, target); ecb.Playback(em); }
            Assert.False(em.HasRelationship<LifecycleRelation>(replacement));
            Assert.Empty(em.GetRelationsOf<LifecycleRelation>(target));
        }

        [Fact]
        public void ParallelSortKeys_ControlExclusiveTransfersAcrossWriterIndices()
        {
            using var world = new World("RelationCommandExclusive");
            var em = world.EntityManager;
            var a = New(em); var b = New(em); var c = New(em); var target = New(em);
            em.AddRelationship<OccupiedBy>(a, target);
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(2);
            ecb.CreateParallelWriter(0).SetRelationshipTarget<OccupiedBy>(30, b, target);
            var other = ecb.CreateParallelWriter(1);
            other.SetRelationshipTarget<OccupiedBy>(10, c, target);
            other.SetRelationshipTarget<OccupiedBy>(20, a, target);
            ecb.PlaybackParallel(em);
            Assert.Equal(new[] { b }, em.GetRelationsOf<OccupiedBy>(target));
            Assert.False(em.HasRelationship<OccupiedBy>(a));
            Assert.False(em.HasRelationship<OccupiedBy>(c));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AcyclicValidationFailure_LeavesTheExistingGraphIntact(bool parallel)
        {
            using var world = new World("RelationCommandAcyclic");
            var em = world.EntityManager;
            var a = New(em); var b = New(em);
            em.AddRelationship<LifecycleParent>(a, b);
            using var ecb = new DeferredCommandBuffer();
            if (parallel)
            {
                ecb.EnsureParallelWriters(1);
                ecb.CreateParallelWriter(0).SetRelationshipTarget<LifecycleParent>(0, b, a);
                Assert.Throws<CommandPlaybackException>(() => ecb.PlaybackParallel(em));
            }
            else
            {
                ecb.SetRelationshipTarget<LifecycleParent>(b, a);
                Assert.Throws<CommandPlaybackException>(() => ecb.Playback(em));
            }
            Assert.Equal(b, em.GetRelationship<LifecycleParent>(a));
            Assert.False(em.HasRelationship<LifecycleParent>(b));
            Assert.Equal(new[] { a }, em.GetRelationsOf<LifecycleParent>(b));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ObserverRecords_AreKeptForTheNextPlayback(bool parallel)
        {
            using var world = new World("RelationCommandReentry");
            var em = world.EntityManager;
            var source = New(em); var a = New(em); var b = New(em);
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(1);
            var writer = ecb.CreateSharedParallelWriter(0, 4096, 8);
            int observed = 0;
            em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (_, _) =>
            {
                observed++;
                if (parallel) writer.SetRelationshipTarget<LifecycleRelation>(1, source, b);
                else ecb.SetRelationshipTarget<LifecycleRelation>(source, b);
            });
            if (parallel) writer.SetRelationshipTarget<LifecycleRelation>(0, source, a); else ecb.SetRelationshipTarget<LifecycleRelation>(source, a);
            Playback();
            Assert.Equal(a, em.GetRelationship<LifecycleRelation>(source));
            Assert.Equal(1, observed);
            Playback();
            Assert.Equal(b, em.GetRelationship<LifecycleRelation>(source));
            Assert.Equal(1, observed);
            void Playback() { if (parallel) ecb.PlaybackParallel(em); else ecb.Playback(em); }
        }

        [Fact]
        public void ObserverCreateCommands_UseTheirOwnTypePoolAndBatchIds()
        {
            using var world = new World("RelationCommandCreateReentry");
            var em = world.EntityManager;
            var source = New(em); var target = New(em);
            using var ecb = new DeferredCommandBuffer();
            em.AddObserver<LifecycleRelation>(ObserverEvents.Added, (_, _) =>
            {
                int batch = ecb.CreateEntitiesRange(1, typeof(Position));
                var values = new EntJoy.Collections.NativeArray<Position>(1);
                try { values[0] = new Position { X = 81 }; ecb.SetComponentRange(batch, values); }
                finally { values.Dispose(); }
            });
            ecb.CreateEntity(typeof(Velocity));
            ecb.SetRelationshipTarget<LifecycleRelation>(source, target);
            ecb.Playback(em);
            Assert.Equal(1, ecb.CreatedEntityCount);
            Assert.Equal(2, ecb.CommandCount);
            ecb.Playback(em);
            Assert.Equal(1, ecb.CreatedEntityCount);
            Assert.Equal(81f, em.GetComponentReadOnly<Position>(ecb.CreatedEntities[0]).X);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConcurrentRecordings_AgreeWithAnIndependentSortedEdgeModel(bool shared)
        {
            using var world = new World("RelationCommandReference");
            var em = world.EntityManager;
            var sources = Enumerable.Range(0, 17).Select(_ => New(em)).ToArray();
            var targets = Enumerable.Range(0, 4).Select(_ => New(em)).ToArray();
            foreach (var source in sources) em.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(targets[0]), Data = source.Id + 100 });
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(shared ? 1 : 4);
            var sharedWriter = shared ? ecb.CreateSharedParallelWriter(0, 32 * 400, 1) : default;
            var tasks = new Task[4];
            for (int worker = 0; worker < 4; worker++)
            {
                int owner = worker;
                var writer = shared ? sharedWriter : ecb.CreateParallelWriter(worker);
                tasks[worker] = Task.Run(() =>
                {
                    for (int step = 0; step < 100; step++)
                    {
                        int key = step * 4 + owner;
                        var source = sources[key % sources.Length];
                        if (key % 7 == 0) writer.RemoveRelationship<LifecycleRelation>(key, source);
                        else writer.SetRelationshipTarget<LifecycleRelation>(key, source, targets[(key / 3) % targets.Length]);
                    }
                });
            }
            Task.WaitAll(tasks);
            ecb.PlaybackParallel(em);
            var model = sources.ToDictionary(entity => entity, _ => targets[0]);
            for (int key = 0; key < 400; key++)
            {
                var source = sources[key % sources.Length];
                if (key % 7 == 0) model.Remove(source); else model[source] = targets[(key / 3) % targets.Length];
            }
            foreach (var source in sources)
            {
                Assert.Equal(model.TryGetValue(source, out var expected), em.TryGetRelationship<LifecycleRelation>(source, out var actual));
                if (model.ContainsKey(source)) Assert.Equal(expected, actual);
            }
            foreach (var target in targets)
                Assert.Equal(model.Where(pair => pair.Value.Equals(target)).Select(pair => pair.Key.Id).OrderBy(id => id),
                    em.GetRelationsOf<LifecycleRelation>(target).Select(entity => entity.Id).OrderBy(id => id));
        }

        [Fact]
        public void MixedParallelCommands_UseZeroForOrdinaryKeysAndPreserveTheReplacedPayload()
        {
            using var world = new World("RelationCommandMixed");
            var em = world.EntityManager;
            var source = New(em); var a = New(em); var b = New(em);
            em.AddComponent(source, new LifecycleRelation { Target = RelationSlot.From(a), Data = 1 });
            using var ecb = new DeferredCommandBuffer();
            ecb.EnsureParallelWriters(1);
            var writer = ecb.CreateParallelWriter(0);
            writer.SetRelationshipTarget<LifecycleRelation>(10, source, b);
            writer.SetComponent(source, new LifecycleRelation { Target = RelationSlot.From(a), Data = 91 });
            ecb.PlaybackParallel(em);
            Assert.Equal(b, em.GetRelationship<LifecycleRelation>(source));
            Assert.Equal(91, em.GetComponentReadOnly<LifecycleRelation>(source).Data);
        }
    }
}
