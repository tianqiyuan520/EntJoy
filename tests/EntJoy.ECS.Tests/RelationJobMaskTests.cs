using System;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct RelationMaskEntityJob : IJobEntity
    {
        public void Execute(ref Position position) { position.X += 1; }
    }

    public struct RelationMaskChunkJob : IJobChunk
    {
        public static int Calls;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(chunk.Count, mask.Length);
            Assert.Equal((chunk.Count + 63) / 64, mask.Bits.Length);
            int tail = chunk.Count & 63;
            if (tail != 0) Assert.Equal(0UL, mask.Bits[mask.Bits.Length - 1] >> tail);
            var values = chunk.GetComponentDataSpan<Position>();
            int cursor = 0;
            while (mask.TryGetNextRange(ref cursor, out int first, out int end))
            {
                Assert.InRange(first, 0, chunk.Count - 1);
                Assert.InRange(end, first + 1, chunk.Count);
                for (int i = first; i < end; i++)
                {
                    Assert.True(mask.IsEnabled(i));
                    values[i].X += 1;
                }
            }
        }
    }

    public sealed class RelationJobGate
    {
        public readonly ManualResetEventSlim Started = new(false), Release = new(false);
        public EntityManager Manager;
        public Entity Target;
        public bool SawAlive;
        public int Finished;
    }

    public struct RelationMaskBlockingJob : IJobChunk
    {
        public RelationJobGate Gate;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            Gate.Started.Set();
            if (!Gate.Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Relation Job gate timed out.");
            Gate.SawAlive = Gate.Manager.IsRelationshipTargetAlive(RelationSlot.From(Gate.Target));
            Volatile.Write(ref Gate.Finished, 1);
        }
    }

    public struct RelationMaskEnableJob : IJobChunk
    {
        public ManualResetEventSlim Release;
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask)
        {
            if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Enabled writer gate timed out.");
            for (int i = 0; i < chunk.Count; i++) chunk.SetComponentEnabled<RelationReadEnabled>(i, i % 2 == 0);
        }
    }

    public class RelationJobMaskTests
    {
        [Fact]
        public unsafe void NativeCollectorPayloads_CarryTheSameRelationshipAndEnabledFilters()
        {
            using var world = new World("NativeCollectorFilters");
            var em = world.EntityManager;
            var a = em.NewEntity(typeof(Velocity));
            var b = em.NewEntity(typeof(Velocity));
            for (int i = 0; i < 3; i++)
            {
                var source = em.NewEntity(typeof(Position), typeof(LifecycleRelation), typeof(RelationReadEnabled));
                em.AddRelationship<LifecycleRelation>(source, i == 1 ? b : a);
                em.SetComponentEnabled<RelationReadEnabled>(source, i != 2);
            }
            var query = new QueryBuilder().WithAll<Position>().WithRelationship<LifecycleRelation>(a).WithEnabled<RelationReadEnabled>();
            int[] required = { ComponentTypeManager.GetComponentType(typeof(Position)).Id };
            ChunkJobCollector.CollectAndBuildNative(em, query, required, out var chunks, out int count, out _);
            try { Assert.Equal(1, count); AssertFilter(chunks[0].filter); }
            finally { ChunkJobCollector.FreeChunkJobDataPayload(chunks, count); }
            ChunkJobCollector.CollectAndBuildEntityBatch(em, query, required, out var batches, out int batchCount, out _);
            try { Assert.Equal(1, batchCount); AssertFilter(batches[0].filter); }
            finally
            {
                for (int i = 0; i < batchCount; i++) ChunkJobScheduler.FreeNativeFilter(batches[i].filter);
                if (batchCount > 0) System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)batches[0].componentArrays);
                System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)batches);
            }
            static void AssertFilter(EntityFilterData filter)
            {
                Assert.Equal(5UL, filter.relationshipBits[0]);
                Assert.Equal(1, filter.enabledCount);
                Assert.Equal(1UL, filter.relationshipBits[0] & ((ulong*)filter.enabledBitMaps[0])[0] & 7UL);
            }
        }

        [Fact]
        public void NativeFilterPayloads_MatchTheX64HeaderLayouts()
        {
            Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<EntityFilterData>());
            Assert.Equal(120, System.Runtime.InteropServices.Marshal.SizeOf<ChunkJobData>());
            Assert.Equal(80, System.Runtime.InteropServices.Marshal.SizeOf<ChunkData>());
            Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf<EntityBatchData>());
            Assert.Equal(new IntPtr(96), System.Runtime.InteropServices.Marshal.OffsetOf<ChunkJobData>(nameof(ChunkJobData.filter)));
            Assert.Equal(new IntPtr(56), System.Runtime.InteropServices.Marshal.OffsetOf<ChunkData>(nameof(ChunkData.filter)));
            Assert.Equal(new IntPtr(24), System.Runtime.InteropServices.Marshal.OffsetOf<EntityBatchData>(nameof(EntityBatchData.filter)));
        }

        [Fact]
        public void RelationshipMask_ReadsEnabledBitsAfterItsDependencyCompletes()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("RelationMaskDependency");
            using var release = new ManualResetEventSlim(false);
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Velocity));
            var entities = new Entity[65];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = em.NewEntity(typeof(Position), typeof(LifecycleRelation), typeof(RelationReadEnabled));
                em.AddRelationship<LifecycleRelation>(entities[i], target);
                em.SetComponentEnabled<RelationReadEnabled>(entities[i], false);
            }
            var all = new QueryBuilder().WithAll<Position>().WithAll<RelationReadEnabled>();
            var parent = new RelationMaskEnableJob { Release = release }.Schedule(all, world);
            try
            {
                var filtered = all.WithEnabled<RelationReadEnabled>().WithRelationship<LifecycleRelation>(target);
                var child = new RelationMaskEntityJob().Schedule(filtered, world, dependsOn: parent);
                release.Set();
                child.Complete();
                for (int i = 0; i < entities.Length; i++)
                    Assert.Equal(i % 2 == 0 ? 1f : 0f, em.GetComponentReadOnly<Position>(entities[i]).X);
            }
            finally { release.Set(); parent.Complete(); }
        }

        [Theory]
        [InlineData(63, false, false)]
        [InlineData(64, false, true)]
        [InlineData(65, true, false)]
        [InlineData(65, true, true)]
        [InlineData(129, false, true)]
        [InlineData(4097, true, true)]
        public void RunAndSchedule_VisitExactRelationshipAndEnabledIntersection(int count, bool entityJob, bool schedule)
        {
            TestBackend.EnsureInitialized(2);
            int previous = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 65;
            try
            {
                using var world = new World("RelationMask");
                var em = world.EntityManager;
                var a = em.NewEntity(typeof(Velocity));
                var b = em.NewEntity(typeof(Velocity));
                var entities = new Entity[count];
                for (int i = 0; i < count; i++)
                {
                    entities[i] = em.NewEntity(typeof(Position), typeof(LifecycleRelation), typeof(RelationReadEnabled));
                    em.AddRelationship<LifecycleRelation>(entities[i], i % 3 == 0 ? a : b);
                    em.SetComponentEnabled<RelationReadEnabled>(entities[i], i % 5 != 0);
                }
                var query = new QueryBuilder().WithAll<Position>().WithEnabled<RelationReadEnabled>().WithRelationship<LifecycleRelation>(a);
                if (entityJob)
                {
                    var job = new RelationMaskEntityJob();
                    if (schedule) job.Schedule(query, world).Complete(); else job.Run(query, world);
                }
                else
                {
                    var job = new RelationMaskChunkJob();
                    if (schedule) job.Schedule(query, world).Complete(); else job.Run(query, world);
                }
                for (int i = 0; i < count; i++)
                    Assert.Equal(i % 3 == 0 && i % 5 != 0 ? 1f : 0f, em.GetComponentReadOnly<Position>(entities[i]).X);
            }
            finally { Archetype.ChunkCapacityOverride = previous; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EmptyAndFullMatches_UseRealMasksAndSkipEmptyChunks(bool schedule)
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("RelationMaskExtremes");
            var em = world.EntityManager;
            var a = em.NewEntity(typeof(Velocity));
            var b = em.NewEntity(typeof(Velocity));
            var entities = new Entity[65];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = em.NewEntity(typeof(Position), typeof(LifecycleRelation), typeof(RelationReadEnabled));
                em.AddRelationship<LifecycleRelation>(entities[i], a);
            }
            var query = new QueryBuilder().WithAll<Position>().WithRelationship<LifecycleRelation>(b);
            RelationMaskChunkJob.Calls = 0;
            Execute();
            Assert.Equal(0, RelationMaskChunkJob.Calls);
            query = query.WithRelationship<LifecycleRelation>(a);
            Execute();
            Assert.True(RelationMaskChunkJob.Calls > 0);
            foreach (var entity in entities) Assert.Equal(1f, em.GetComponentReadOnly<Position>(entity).X);
            query = query.WithEnabled<RelationReadEnabled>();
            foreach (var entity in entities) em.SetComponentEnabled<RelationReadEnabled>(entity, false);
            RelationMaskChunkJob.Calls = 0;
            Execute();
            Assert.Equal(0, RelationMaskChunkJob.Calls);
            void Execute()
            {
                if (schedule) new RelationMaskChunkJob().Schedule(query, world).Complete();
                else new RelationMaskChunkJob().Run(query, world);
            }
        }

        [Fact]
        public void ReusedScheduledQuery_ObservesInPlaceRebindingAndTargetVersion()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("RelationMaskCache");
            var em = world.EntityManager;
            var a = em.NewEntity(typeof(Velocity));
            var b = em.NewEntity(typeof(Velocity));
            var source = em.NewEntity(typeof(Position), typeof(LifecycleRelation));
            var query = new QueryBuilder().WithAll<Position>().WithRelationship<LifecycleRelation>(a);
            em.AddRelationship<LifecycleRelation>(source, b);
            var job = new RelationMaskEntityJob();
            job.Schedule(query, world).Complete();
            int structural = em.StructuralVersion;
            em.AddRelationship<LifecycleRelation>(source, a);
            Assert.Equal(structural, em.StructuralVersion);
            job.Schedule(query, world).Complete();
            Assert.Equal(1f, em.GetComponentReadOnly<Position>(source).X);
            em.AddRelationship<LifecycleRelation>(source, b);
            job.Schedule(query, world).Complete();
            Assert.Equal(1f, em.GetComponentReadOnly<Position>(source).X);
            em.DestroyEntity(a);
            var recycled = em.NewEntity(typeof(Velocity));
            Assert.Equal(a.Id, recycled.Id);
            em.AddRelationship<LifecycleRelation>(source, recycled);
            job.Schedule(query, world).Complete();
            Assert.Equal(1f, em.GetComponentReadOnly<Position>(source).X);
        }

        [Theory]
        [InlineData("retarget")]
        [InlineData("destroy")]
        [InlineData("create")]
        public void RelationshipFilterJob_HoldsSourceAndTargetLifetimeEvenWithoutRelationParameters(string mutation)
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("RelationMaskLifetime");
            var em = world.EntityManager;
            var target = em.NewEntity(typeof(Velocity));
            var other = em.NewEntity(typeof(Velocity));
            var source = em.NewEntity(typeof(Position), typeof(LifecycleRelation));
            em.AddRelationship<LifecycleRelation>(source, target);
            var gate = new RelationJobGate { Manager = em, Target = target };
            var query = new QueryBuilder().WithAll<Position>().WithRelationship<LifecycleRelation>(target);
            var handle = new RelationMaskBlockingJob { Gate = gate }.Schedule(query, world);
            try
            {
                Assert.True(gate.Started.Wait(TimeSpan.FromSeconds(3)));
                var release = Task.Run(() => { Thread.Sleep(60); gate.Release.Set(); });
                if (mutation == "retarget") em.AddRelationship<LifecycleRelation>(source, other);
                else if (mutation == "destroy") em.DestroyEntity(target);
                else em.NewEntity(typeof(Velocity));
                Assert.Equal(1, Volatile.Read(ref gate.Finished));
                Assert.True(gate.SawAlive);
                release.GetAwaiter().GetResult();
            }
            finally { gate.Release.Set(); handle.Complete(); gate.Started.Dispose(); gate.Release.Dispose(); }
        }
    }
}
