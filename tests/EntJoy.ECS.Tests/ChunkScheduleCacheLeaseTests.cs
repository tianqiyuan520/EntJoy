using System;
using System.Runtime.InteropServices;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public unsafe class ChunkScheduleCacheLeaseTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EntityBatchCache_RetirementWaitsForBothLeaseKinds(bool releaseSynchronousFirst)
        {
            var batches = (EntityBatchData*)Marshal.AllocHGlobal(sizeof(EntityBatchData));
            *batches = default;
            var cache = new ChunkJobScheduler.EntityBatchScheduleCache(0, batches, 1, null, null);
            var asynchronousLease = cache.RetainLease();
            Assert.Null(cache.RetainLease(synchronous: true));
            cache.Dispose();
            try
            {
                if (releaseSynchronousFirst) cache.ReleaseLease();
                else asynchronousLease.Dispose();
                // A retired cache remains usable while either kind of lease still holds its buffers.
                cache.RetainLease().Dispose();
            }
            finally
            {
                if (releaseSynchronousFirst) asynchronousLease.Dispose();
                else cache.ReleaseLease();
            }
            Assert.Throws<ObjectDisposedException>(() => cache.RetainLease());
            asynchronousLease.Dispose();
            cache.Dispose();
        }
    }
}
