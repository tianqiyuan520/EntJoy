using System;
using System.Runtime.InteropServices;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class ChunkColumnLayoutTests
    {
        private struct ColumnA : IComponentData { public int Value; }
        private struct ColumnB : IComponentData { public int Value; }
        private struct ColumnC : IComponentData { public int Value; }
        private struct ByteColumn : IComponentData { public byte Value; }
        [StructLayout(LayoutKind.Sequential, Size = 160)]
        private struct LargeColumn : IComponentData { public long Value; }

        [Theory]
        [InlineData(64)]
        [InlineData(1024)]
        [InlineData(4096)]
        [InlineData(1015808)]
        public void Columns_KeepAlignmentAndBounds_WithoutAdjacentCachePeriodAliases(int capacity)
        {
            ComponentType[] types = { typeof(ColumnA), typeof(ColumnB), typeof(ColumnC) };
            using var archetype = new Archetype(types);
            var metadata = ChunkMetadata.Create(archetype, capacity, types);
            var cache = ChunkCacheLayout.Current;
            int previousOffset = 0, end = capacity * Marshal.SizeOf<Entity>();
            for (int i = 0; i < types.Length; i++)
            {
                int offset = metadata.ComponentOffsets[i];
                Assert.Equal(0, offset % cache.LineSize);
                Assert.True(offset >= end);
                Assert.NotEqual(previousOffset % cache.ConflictPeriod, offset % cache.ConflictPeriod);
                previousOffset = offset;
                end = offset + capacity * metadata.ComponentSizes[i];
            }
            Assert.True(metadata.TotalSize >= end);
        }

        [Theory]
        [InlineData(64, 4096, 1024)]
        [InlineData(128, 8192, 64)]
        [InlineData(128, 8192, 2048)]
        [InlineData(64, 6144, 3072)]
        public unsafe void HardwareLayouts_AlignEveryChunkAndKeepColumnsWithinStride(int line, int period, int capacity)
        {
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = capacity;
            try
            {
                var cache = new ChunkCacheLayout(line, period);
                ComponentType[] types = { typeof(ColumnA), typeof(ColumnB), typeof(ColumnC) };
                using var archetype = new Archetype(types, cache);
                archetype.CreateChunk();
                archetype.CreateChunk();
                var chunks = archetype.GetChunks();
                for (int c = 0; c < chunks.Count; c++)
                {
                    var chunk = chunks[c];
                    Assert.Equal(0, chunk.MemoryBlock.ToInt64() % line);
                    long previous = chunk.MemoryBlock.ToInt64();
                    for (int i = 0; i < types.Length; i++)
                    {
                        long address = chunk.GetComponentArrayPointer(i).ToInt64();
                        Assert.Equal(0, address % line);
                        Assert.NotEqual(previous % period, address % period);
                        Assert.True(chunk.Meta.ComponentOffsets[i] + chunk.Capacity * sizeof(int) <= chunk.TotalSize);
                        previous = address;
                        var values = new Span<int>((void*)address, chunk.Capacity);
                        values.Fill(100 * c + i + 1);
                    }
                }
                long first = chunks[0].MemoryBlock.ToInt64(), second = chunks[1].MemoryBlock.ToInt64();
                Assert.True(first + chunks[0].TotalSize <= second || second + chunks[1].TotalSize <= first);
                for (int c = 0; c < chunks.Count; c++)
                    for (int i = 0; i < types.Length; i++)
                    {
                        var values = new ReadOnlySpan<int>((void*)chunks[c].GetComponentArrayPointer(i), chunks[c].Capacity);
                        foreach (int value in values) Assert.Equal(100 * c + i + 1, value);
                    }
            }
            finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        }

        [Theory]
        [InlineData(64, 4096)]
        [InlineData(128, 8192)]
        public void AutomaticCapacity_IncludesAlignmentAndPadding(int line, int period)
        {
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 0;
            try
            {
                ComponentType[] types = { typeof(ColumnA), typeof(ColumnB), typeof(ColumnC) };
                using var archetype = new Archetype(types, new ChunkCacheLayout(line, period));
                archetype.CreateChunk();
                var chunk = archetype.GetChunks()[0];
                Assert.Equal(128, chunk.Capacity);
                Assert.True(chunk.TotalSize <= 16 * 1024);
            }
            finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        }

        [Theory]
        [InlineData(64)]
        [InlineData(128)]
        public unsafe void AutomaticCapacity_CapsSmallComponentsAt128(int line)
        {
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 0;
            try
            {
                using var archetype = new Archetype(new ComponentType[] { typeof(ByteColumn) }, new ChunkCacheLayout(line, 256));
                archetype.CreateChunk();
                var chunk = archetype.GetChunks()[0];
                Assert.Equal(128, chunk.Capacity);
                Assert.True(chunk.TotalSize <= 16 * 1024);
                var values = new Span<ByteColumn>((void*)chunk.GetComponentArrayPointer(0), chunk.Capacity);
                values[0] = new ByteColumn { Value = 1 };
                values[^1] = new ByteColumn { Value = 2 };
                Assert.Equal(1, values[0].Value);
                Assert.Equal(2, values[^1].Value);
            }
            finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        }

        [Theory]
        [InlineData(64, 4096)]
        [InlineData(128, 8192)]
        public void AutomaticCapacity_LargeComponentsKeepLayoutWithinBudget(int line, int period)
        {
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 0;
            try
            {
                using var archetype = new Archetype(new ComponentType[] { typeof(LargeColumn) }, new ChunkCacheLayout(line, period));
                archetype.CreateChunk();
                var chunk = archetype.GetChunks()[0];
                Assert.Equal(64, chunk.Capacity);
                Assert.True(chunk.TotalSize <= 16 * 1024);
            }
            finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        }

        [Fact]
        public void PaddedColumns_PreserveValuesAcrossChunksAndEntityRemoval()
        {
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 1024;
            try
            {
                using var world = new World("ColumnLayout" + Guid.NewGuid().ToString("N"));
                var em = world.EntityManager;
                var entities = em.CreateEntities(1050, typeof(ColumnA), typeof(ColumnB), typeof(ColumnC));
                for (int i = 0; i < entities.Length; i++)
                {
                    em.SetComponent(entities[i], new ColumnA { Value = i });
                    em.SetComponent(entities[i], new ColumnB { Value = -i });
                    em.SetComponent(entities[i], new ColumnC { Value = i * 7 });
                }
                em.DestroyEntity(entities[0]);
                em.DestroyEntity(entities[1024]);
                for (int i = 1; i < entities.Length; i++)
                {
                    if (i == 1024) continue;
                    Assert.Equal(i, em.GetComponent<ColumnA>(entities[i]).Value);
                    Assert.Equal(-i, em.GetComponent<ColumnB>(entities[i]).Value);
                    Assert.Equal(i * 7, em.GetComponent<ColumnC>(entities[i]).Value);
                }
            }
            finally { Archetype.ChunkCapacityOverride = savedCapacity; }
        }
    }
}
