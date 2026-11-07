using System;
using System.Reflection;
using System.Threading.Tasks;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public unsafe class RemainingBugBoundaryTests
    {
        private struct EmptyFor : IJobParallelFor { public void Execute(int index) { } }
        private struct EmptyBatch : IJobParallelForBatch { public void Execute(int first, int count) { } }
        private struct InvalidRelation : IRelationComponent, IComponentData { }
        private struct Fill : IJob
        {
            public NativeArray<int> Values;
            public void Execute() { System.Threading.Thread.Sleep(20); Values[0] = 1; }
        }
        private struct Increment : IJobParallelFor
        {
            public NativeArray<int> Values;
            public void Execute(int index) { Values[index] += 1; }
        }
        private struct IncrementBatch : IJobParallelForBatch
        {
            public NativeArray<int> Values;
            public void Execute(int first, int count) { for (int i = first; i < first + count; i++) Values[i] += 1; }
        }

        [Fact]
        public void WriterByteAdditionOverflow_RejectsBeforeChangingState()
        {
            var state = new EcbWriterState { Offset = int.MaxValue - 4, Capacity = int.MaxValue };
            Assert.Throws<OverflowException>(() => state.EnsureCapacity(8));
            Assert.Equal(int.MaxValue - 4, state.Offset);
        }

        [Fact]
        public void MainDestroyOverflow_DoesNotAppendACommandHeader()
        {
            using var buffer = new DeferredCommandBuffer();
            var count = typeof(DeferredCommandBuffer).GetField("_destroyEntityCount", BindingFlags.Instance | BindingFlags.NonPublic);
            count.SetValue(buffer, int.MaxValue);
            try
            {
                Assert.Throws<OverflowException>(() => buffer.DestroyEntity(default));
                Assert.Equal(0, buffer.CommandCount);
                Assert.Equal(0, typeof(DeferredCommandBuffer).GetField("_stagingOffset", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(buffer));
            }
            finally { count.SetValue(buffer, 0); }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void InvalidComponentType_DoesNotAppendAPartialCommand(int operation)
        {
            TestBackend.EnsureInitialized(2);
            using var buffer = new DeferredCommandBuffer();
            using var values = new NativeArray<InvalidRelation>(1, Allocator.Persistent);
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (operation == 0) buffer.AddComponent(default, default(InvalidRelation));
                else if (operation == 1) buffer.RemoveComponent<InvalidRelation>(default);
                else buffer.SetComponentRange(0, values);
            });
            Assert.Equal(0, buffer.CommandCount);
            using var world = new World("FailedCommandRegistration");
            buffer.Playback(world.EntityManager);
            buffer.CreateEntity(typeof(Position));
            buffer.Playback(world.EntityManager);
            Assert.Equal(1, buffer.CreatedEntityCount);
        }

        [Fact]
        public void SharedReservationOverflow_LeavesCursorUnchanged()
        {
            var state = new EcbWriterState { Offset = int.MaxValue - 4, Capacity = int.MaxValue };
            Assert.Throws<InvalidOperationException>(() => state.ReserveShared(8));
            Assert.Equal(int.MaxValue - 4, state.Offset);
        }

        [Fact]
        public void FailedSharedDestroy_DoesNotConsumeEitherReservation()
        {
            byte* bytes = stackalloc byte[12];
            Entity* entities = stackalloc Entity[1];
            var state = new EcbWriterState { Staging = bytes, Capacity = 12, Offset = 12, Destroys = entities, DestroyCapacity = 1 };
            Assert.Throws<InvalidOperationException>(() => state.DestroyEntityShared(default));
            Assert.Equal(0, state.DestroyCount);
            Assert.Equal(12, state.Offset);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NullCounter_PreservesTheNormalDependency(bool batch)
        {
            TestBackend.EnsureInitialized(2);
            using var values = new NativeArray<int>(1, Allocator.Persistent);
            var parent = new Fill { Values = values }.Schedule();
            try
            {
                var child = batch
                    ? new IncrementBatch { Values = values }.ScheduleBatch(1, 1, parent, (ThreadCounter)null)
                    : new Increment { Values = values }.Schedule(1, 1, parent, (ThreadCounter)null);
                child.Complete();
                Assert.Equal(2, values[0]);
            }
            finally { parent.Complete(); }
        }

        [Fact]
        public void MalformedRelationshipQuery_IsRejectedBeforeNativeSubmission()
        {
            using var world = new World("RawRelationshipBoundary");
            var query = new QueryBuilder { HasRelationshipFilter = true };
            var job = new SharedFilterProbeJob();
            Assert.Throws<ArgumentException>(() => ChunkJobScheduler.ScheduleChunkEntityBatchRawWithWorkerCapAndRangeSize(
                ref job, world.EntityManager, query, new IntPtr(1), Array.Empty<int>(), 2, 1));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CounterOverload_DoesNotSilentlyIgnoreTheCounter(bool batch)
        {
            var counter = new ThreadCounter();
            if (batch) Assert.Throws<NotSupportedException>(() => new EmptyBatch().ScheduleBatch(1, 1, default, counter));
            else Assert.Throws<NotSupportedException>(() => new EmptyFor().Schedule(1, 1, default, counter));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void JobRelationshipFilter_WithNoSources_DoesNotExecute(bool schedule)
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("RelationshipJobBoundary");
            World.DefaultWorld = world;
            var parent = world.EntityManager.NewEntity(typeof(Position));
            var query = new QueryBuilder().WithAll<Position>().WithRelationship<ChildOf>(parent);
            SharedFilterProbeJob.Visited = 0;
            if (schedule) new SharedFilterProbeJob().Schedule(query).Complete();
            else new SharedFilterProbeJob().Run(query);
            Assert.Equal(0, SharedFilterProbeJob.Visited);
        }

        [Fact]
        public void WriterTableAllocationOverflow_DoesNotChangeWriterCount()
        {
            using var buffer = new DeferredCommandBuffer();
            Assert.Throws<OverflowException>(() => buffer.EnsureParallelWriters(int.MaxValue));
            Assert.Equal(0, buffer.ParallelWriterCount);
            buffer.EnsureParallelWriters(1);
            Assert.Equal(1, buffer.ParallelWriterCount);
        }

        [Fact]
        public void SharedWriterCreationOverflow_LeavesTheWriterReusable()
        {
            using var buffer = new DeferredCommandBuffer();
            buffer.EnsureParallelWriters(1);
            Assert.Throws<OverflowException>(() => buffer.CreateSharedParallelWriter(0, 64, int.MaxValue));
            Assert.True(buffer.CreateSharedParallelWriter(0, 64, 1).State != null);
        }

        [Fact]
        public void FailedSharedDestroy_DoesNotPoisonTheNextPlaybackRound()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("SharedWriterFailure");
            var entity = world.EntityManager.NewEntity(typeof(Position));
            using var buffer = new DeferredCommandBuffer();
            buffer.EnsureParallelWriters(1);
            var writer = buffer.CreateSharedParallelWriter(0, 32, 1);
            writer.SetComponent(entity, new Position { X = 7 });
            Assert.Throws<InvalidOperationException>(() => writer.DestroyEntity(entity));
            buffer.PlaybackParallel(world.EntityManager);
            Assert.Equal(7f, world.EntityManager.GetComponent<Position>(entity).X);
            writer.DestroyEntity(entity);
            buffer.PlaybackParallel(world.EntityManager);
            Assert.Null(world.EntityManager.GetEntityInfoRef(entity.Id).Archetype);
        }

        [Fact]
        public void SharedWriter_ConcurrentMixedCommandsRemainComplete()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("SharedWriterReservations");
            var entities = world.EntityManager.CreateEntities(256, typeof(Position));
            using var buffer = new DeferredCommandBuffer();
            buffer.EnsureParallelWriters(1);
            var writer = buffer.CreateSharedParallelWriter(0, 16384, 256);
            Parallel.For(0, entities.Length, i =>
            {
                if (i < 128) writer.SetComponent(entities[i], new Position { X = i + 1 });
                else writer.DestroyEntity(entities[i]);
            });
            buffer.PlaybackParallel(world.EntityManager);
            for (int i = 0; i < 128; i++) Assert.Equal(i + 1, world.EntityManager.GetComponent<Position>(entities[i]).X);
            for (int i = 128; i < entities.Length; i++) Assert.Null(world.EntityManager.GetEntityInfoRef(entities[i].Id).Archetype);
        }
    }
}
