using System;
using System.Collections.Generic;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public struct ReplacementLifetimeValue : IComponentData, IDisposable
    {
        public int Value;
        public static int Disposed;
        public void Dispose() { Disposed++; }
    }

    public sealed class CollidingSharedKey : ISharedComponentData
    {
        public readonly int Value;
        public CollidingSharedKey(int value) { Value = value; }
        public override int GetHashCode() => 1;
        public override bool Equals(object obj) => obj is CollidingSharedKey other && Value == other.Value;
    }

    public class FrameworkIdentityAndCacheTests
    {
        private struct EmptyJob : IJob { public void Execute() { } }

        [Fact]
        public void CompletedCopy_RemainsAStableDictionaryKey()
        {
            TestBackend.EnsureInitialized(2);
            var job = new EmptyJob();
            var handle = JobScheduler.Schedule(ref job);
            var copy = handle;
            int hash = copy.GetHashCode();
            var nativeCopy = copy._nativeHandle;
            int nativeHash = nativeCopy.GetHashCode();
            var keys = new Dictionary<JobHandle, int> { [copy] = 7 };
            handle.Complete();
            if (TestBackend.IsNative) Assert.Equal(nativeHash, nativeCopy.GetHashCode());
            Assert.Equal(hash, copy.GetHashCode());
            Assert.True(keys.Remove(copy));
        }

        [Fact]
        public void HandleIdentityComparison_DoesNotAllocateAfterWarmup()
        {
            TestBackend.EnsureInitialized(2);
            var job = new EmptyJob();
            var handle = JobScheduler.Schedule(ref job);
            var copy = handle;
            handle.Complete();
            var comparer = EqualityComparer<JobHandle>.Default;
            for (int i = 0; i < 100; i++) { comparer.Equals(copy, copy); comparer.GetHashCode(copy); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            int hash = 0;
            bool equal = true;
            for (int i = 0; i < 10000; i++) { equal &= comparer.Equals(copy, copy); hash ^= comparer.GetHashCode(copy); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(equal);
            Assert.Equal(0, hash);
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void CompletedCopies_PreserveDistinctIdentities()
        {
            TestBackend.EnsureInitialized(2);
            var job = new EmptyJob();
            var first = JobScheduler.Schedule(ref job);
            var firstCopy = first;
            first.Complete();
            var second = JobScheduler.Schedule(ref job);
            var secondCopy = second;
            second.Complete();
            Assert.NotEqual(default(JobHandle), firstCopy);
            Assert.NotEqual(firstCopy, secondCopy);
        }

        [Fact]
        public void CombinedCompletion_PreservesInputKeyLookup()
        {
            TestBackend.EnsureInitialized(2);
            var job = new EmptyJob();
            var first = JobScheduler.Schedule(ref job);
            var second = JobScheduler.Schedule(ref job);
            var keys = new HashSet<JobHandle> { first, second };
            JobHandle.CombineDependencies(first, second).Complete();
            Assert.Contains(first, keys);
            Assert.Contains(second, keys);
        }

        [Fact]
        public void SharedHashCollision_DoesNotReuseAnotherJobQuery()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("SharedCollision");
            World.DefaultWorld = world;
            var types = new ComponentType[] { typeof(Position), typeof(CollidingSharedKey) };
            for (int i = 0; i < 6; i++) world.EntityManager.NewEntity(types, (typeof(CollidingSharedKey), (object)new CollidingSharedKey(1)));
            for (int i = 0; i < 3; i++) world.EntityManager.NewEntity(types, (typeof(CollidingSharedKey), (object)new CollidingSharedKey(2)));
            foreach (int value in new[] { 1, 2, 1 })
            {
                var query = new QueryBuilder().WithAll<Position>().WithShared(new CollidingSharedKey(value));
                Assert.Equal(value == 1 ? 6 : 3, world.GetOrCreateEntityQuery(query).CalculateEntityCount());
                SharedFilterProbeJob.Visited = 0;
                new SharedFilterProbeJob().Schedule(query).Complete();
                Assert.Equal(value == 1 ? 6 : 3, SharedFilterProbeJob.Visited);
            }
        }

        [Fact]
        public void DefaultQueryKeys_AreSafelyComparable()
            => Assert.Equal(default(QueryKey), default(QueryKey));

        [Fact]
        public void InactiveSharedValue_DoesNotChangeQueryHash()
        {
            var first = new QueryBuilder { SharedFilterValue = 42 };
            var second = new QueryBuilder { SharedFilterValue = 43 };
            var a = new QueryKey(first);
            var b = new QueryKey(second);
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void VersionChangeFilter_IsExplicitlyUnsupported()
            => Assert.Throws<NotSupportedException>(() => new QueryBuilder().WithChanged<Position>().ChangedSince(1));

        [Fact]
        public void ChangedFilter_HasItsOwnEntityQueryIdentity()
        {
            using var world = new World("ChangedIdentity");
            world.EntityManager.NewEntity(typeof(Position));
            world.EntityManager.ClearAllChangedBitMasks();
            var all = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>());
            var changed = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>().WithChanged<Position>());
            Assert.NotSame(all, changed);
            Assert.Equal(1, all.CalculateEntityCount());
            Assert.Equal(0, changed.CalculateEntityCount());
        }

        [Fact]
        public void ChangedJobFilter_RefreshesWithoutStructuralChange()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("ChangedJobCache");
            World.DefaultWorld = world;
            var entity = world.EntityManager.NewEntity(typeof(Position));
            world.EntityManager.Set(entity, new Position { X = 1 });
            var query = new QueryBuilder().WithAll<Position>().WithChanged<Position>();
            SharedFilterProbeJob.Visited = 0;
            new SharedFilterProbeJob().Schedule(query).Complete();
            Assert.Equal(1, SharedFilterProbeJob.Visited);
            int version = world.EntityManager.StructuralVersion;
            world.EntityManager.ClearAllChangedBitMasks();
            Assert.Equal(version, world.EntityManager.StructuralVersion);
            SharedFilterProbeJob.Visited = 0;
            new SharedFilterProbeJob().Schedule(query).Complete();
            Assert.Equal(0, SharedFilterProbeJob.Visited);
        }

        [Fact]
        public unsafe void BulkWrite_SkipsRecycledEntityVersions()
        {
            using var world = new World("BulkWriteVersion");
            var em = world.EntityManager;
            var stale = em.NewEntity(typeof(Position));
            em.DestroyEntity(stale);
            var current = em.NewEntity(typeof(Position));
            Assert.Equal(stale.Id, current.Id);
            Assert.NotEqual(stale.Version, current.Version);
            var value = new Position { X = 42 };
            int type = ComponentTypeManager.GetComponentType(typeof(Position)).Id;
            Assert.Equal(0, em.WriteComponentRange(type, sizeof(Position), &stale, (byte*)&value, 1));
            Assert.Equal(0f, em.GetComponent<Position>(current).X);
        }

        [Fact]
        public void FastComponentWrite_ReleasesTheReplacedValue()
        {
            ComponentTypeManager.RegisterDisposable<ReplacementLifetimeValue>();
            using var world = new World("FastWriteLifetime");
            var entity = world.EntityManager.NewEntity(typeof(ReplacementLifetimeValue));
            world.EntityManager.SetComponent(entity, new ReplacementLifetimeValue { Value = 1 });
            int before = ReplacementLifetimeValue.Disposed;
            world.EntityManager.SetComponent(entity, new ReplacementLifetimeValue { Value = 2 });
            Assert.Equal(before + 1, ReplacementLifetimeValue.Disposed);
            Assert.Equal(2, world.EntityManager.GetComponent<ReplacementLifetimeValue>(entity).Value);
        }

        [Fact]
        public void FastComponentWrite_WaitsForTrackedEntityJobs()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("FastWriteOrdering");
            World.DefaultWorld = world;
            var entity = world.EntityManager.NewEntity(typeof(Position));
            HandleProbeSlowWriteJob.Started = 0;
            HandleProbeSlowWriteJob.Finished = 0;
            var handle = new HandleProbeSlowWriteJob().Schedule(new QueryBuilder().WithAll<Position>());
            try
            {
                world.EntityManager.SetComponent(entity, new Position { X = 99 });
                Assert.Equal(1, HandleProbeSlowWriteJob.Finished);
                handle.Complete();
                Assert.Equal(99f, world.EntityManager.GetComponent<Position>(entity).X);
            }
            finally { handle.Complete(); }
        }

        [Fact]
        public unsafe void FastComponentWrites_PublishChangeTracking()
        {
            using var world = new World("FastWriteChanges");
            var em = world.EntityManager;
            var entity = em.NewEntity(typeof(Position));
            var changed = world.GetOrCreateEntityQuery(new QueryBuilder().WithAll<Position>().WithChanged<Position>());
            em.ClearAllChangedBitMasks();
            em.SetComponent(entity, new Position { X = 1 });
            Assert.Equal(1, changed.CalculateEntityCount());
            em.ClearAllChangedBitMasks();
            var value = new Position { X = 2 };
            int type = ComponentTypeManager.GetComponentType(typeof(Position)).Id;
            Assert.Equal(1, em.WriteComponentRange(type, sizeof(Position), &entity, (byte*)&value, 1));
            Assert.Equal(1, changed.CalculateEntityCount());
        }
    }
}
