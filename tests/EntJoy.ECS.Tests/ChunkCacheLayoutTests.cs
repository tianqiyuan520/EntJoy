using System;
using System.Buffers.Binary;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class ChunkCacheLayoutTests
    {
        private static byte[] CacheRecord(int lineSize, int cacheBytes, byte ways, int type = 1, byte level = 1)
        {
            var bytes = new byte[64];
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
            bytes[8] = level;
            bytes[9] = ways;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)lineSize);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), cacheBytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), type);
            return bytes;
        }

        private static byte[] Join(byte[] a, byte[] b)
        {
            var bytes = new byte[a.Length + b.Length];
            a.CopyTo(bytes, 0);
            b.CopyTo(bytes, a.Length);
            return bytes;
        }

        [Theory]
        [InlineData(64, 32768, 8, 4096)]
        [InlineData(64, 49152, 12, 4096)]
        [InlineData(128, 65536, 8, 8192)]
        [InlineData(64, 49152, 8, 6144)]
        public void L1Data_UsesHardwareLineAndCapacityPerWay(int line, int bytes, byte ways, int period)
        {
            var cache = ChunkCacheLayout.FromWindowsCacheRecords(CacheRecord(line, bytes, ways));
            Assert.Equal(line, cache.LineSize);
            Assert.Equal(period, cache.ConflictPeriod);
        }

        [Fact]
        public void InstructionAndHigherLevelCaches_DoNotChangeDataLayout()
        {
            var records = Join(CacheRecord(128, 65536, 8, type: 2), CacheRecord(128, 1048576, 8, level: 2));
            records = Join(records, CacheRecord(64, 32768, 8));
            var cache = ChunkCacheLayout.FromWindowsCacheRecords(records);
            Assert.Equal(64, cache.LineSize);
            Assert.Equal(4096, cache.ConflictPeriod);
        }

        [Fact]
        public void DifferentCoreGeometries_UseLargestLineAndDefaultPeriod()
        {
            var records = Join(CacheRecord(128, 65536, 8), CacheRecord(64, 32768, 8));
            var cache = ChunkCacheLayout.FromWindowsCacheRecords(records);
            Assert.Equal(128, cache.LineSize);
            Assert.Equal(4096, cache.ConflictPeriod);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(255)]
        public void UnknownAssociativity_PreservesKnownLineWithDefaultPeriod(byte ways)
        {
            var cache = ChunkCacheLayout.FromWindowsCacheRecords(CacheRecord(128, 65536, ways));
            Assert.Equal(128, cache.LineSize);
            Assert.Equal(4096, cache.ConflictPeriod);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(96)]
        public void InvalidLine_FallsBackTo64And4096(int line)
        {
            var cache = ChunkCacheLayout.FromWindowsCacheRecords(CacheRecord(line, 32768, 8));
            Assert.Equal(64, cache.LineSize);
            Assert.Equal(4096, cache.ConflictPeriod);
        }

        [Fact]
        public void EmptyOrTruncatedRecords_UseDefaults()
        {
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[7], CacheRecord(128, 65536, 8)[..30] })
            {
                var cache = ChunkCacheLayout.FromWindowsCacheRecords(bytes);
                Assert.Equal(64, cache.LineSize);
                Assert.Equal(4096, cache.ConflictPeriod);
            }
        }
    }
}
