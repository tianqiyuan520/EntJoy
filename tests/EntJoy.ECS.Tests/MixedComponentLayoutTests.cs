using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests;

[Trait("Risk", "MemoryLayout")]
public class MixedComponentLayoutTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PackedValue : IComponentData { public byte Tag; public ushort Value; }
    [StructLayout(LayoutKind.Explicit, Size = 17)]
    private struct WideValue : IComponentData
    {
        [FieldOffset(0)] public byte Tag;
        [FieldOffset(1)] public long Value;
        [FieldOffset(9)] public double Real;
    }
    private struct MoveTag : IComponentData { public int Value; }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(192)]
    [InlineData(257)]
    public unsafe void MixedStrides_PreserveValuesAcrossMovesCompactionAndSnapshots(int capacity)
    {
        int previousCapacity = Archetype.ChunkCapacityOverride;
        Archetype.ChunkCapacityOverride = capacity;
        try
        {
            Assert.Equal(3, Unsafe.SizeOf<PackedValue>());
            Assert.Equal(17, Unsafe.SizeOf<WideValue>());
            using var world = new World("MixedLayout");
            var em = world.EntityManager;
            var entities = em.CreateEntities(capacity * 2 + 3, typeof(PackedValue), typeof(WideValue));
            var alive = new HashSet<int>();
            for (int i = 0; i < entities.Length; i++)
            {
                alive.Add(i);
                em.SetComponent(entities[i], new PackedValue { Tag = (byte)(i + 1), Value = (ushort)(i * 31) });
                em.SetComponent(entities[i], new WideValue { Tag = (byte)(i + 3), Value = long.MinValue + i, Real = i + 0.25 });
            }
            int initialChunks = 0;
            foreach (var archetype in em.GetAllArchetypes()) initialChunks += archetype.ChunkCount;
            Assert.True(initialChunks >= 3);
            Verify(moved: false);
            foreach (int i in new[] { 0, capacity - 1, capacity, capacity * 2, entities.Length - 1 })
            {
                em.DestroyEntity(entities[i]);
                alive.Remove(i);
            }
            foreach (int i in alive)
            {
                if (i % 3 == 0) em.AddComponent(entities[i], new MoveTag { Value = i });
                if (i % 6 == 0) em.RemoveComponent<MoveTag>(entities[i]);
            }
            em.CompactChunks();
            Verify(moved: true);
            var snapshot = world.TakeSnapshot();
            world.Restore(snapshot);
            Verify(moved: true);

            void Verify(bool moved)
            {
                int found = 0;
                foreach (var archetype in em.GetAllArchetypes())
                {
                    Assert.Equal(0, archetype.VerifySlabInvariants());
                    foreach (var chunk in archetype.GetChunks())
                    {
                        Assert.Equal(capacity, chunk.Capacity);
                        Assert.InRange(chunk.EntityCount, 0, capacity);
                        var ranges = new List<(long Start, long End)>();
                        for (int c = 0; c < archetype.ComponentCount; c++)
                        {
                            long start = chunk.GetComponentArrayPointer(c).ToInt64();
                            int stride = chunk.Meta.ComponentSizes[c];
                            long end = start + (long)capacity * stride;
                            Assert.Equal(0, start % ChunkCacheLayout.Current.LineSize);
                            Assert.Equal(ComponentTypeManager.GetComponentType(archetype.Types[c].Type).Size, stride);
                            Assert.True(start >= chunk.MemoryBlock.ToInt64() + (long)capacity * Unsafe.SizeOf<Entity>());
                            Assert.True(end <= chunk.MemoryBlock.ToInt64() + chunk.TotalSize);
                            foreach (var range in ranges) Assert.True(end <= range.Start || start >= range.End);
                            ranges.Add((start, end));
                        }
                        found += chunk.EntityCount;
                    }
                }
                Assert.Equal(alive.Count, found);
                foreach (int i in alive)
                {
                    var packed = em.GetComponent<PackedValue>(entities[i]);
                    var wide = em.GetComponent<WideValue>(entities[i]);
                    Assert.Equal((byte)(i + 1), packed.Tag);
                    Assert.Equal((ushort)(i * 31), packed.Value);
                    Assert.Equal((byte)(i + 3), wide.Tag);
                    Assert.Equal(long.MinValue + i, wide.Value);
                    Assert.Equal(i + 0.25, wide.Real);
                    bool hasMoveTag = moved && i % 3 == 0 && i % 6 != 0;
                    Assert.Equal(hasMoveTag, em.TryGetComponentPointer(entities[i],
                        ComponentTypeManager.GetComponentType(typeof(MoveTag)).Id, sizeof(MoveTag), out _));
                    if (hasMoveTag) Assert.Equal(i, em.GetComponent<MoveTag>(entities[i]).Value);
                }
            }
        }
        finally { Archetype.ChunkCapacityOverride = previousCapacity; }
    }
}
