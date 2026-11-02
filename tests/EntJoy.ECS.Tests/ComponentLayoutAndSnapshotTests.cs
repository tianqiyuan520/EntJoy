using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ByteFlags : IComponentData { public bool Enabled; public byte Tag; }
    public struct CharFlags : IComponentData { public bool Enabled; public char Code; public byte Tag; }
    public struct SharedFlags : ISharedComponentData { public bool Enabled; public byte Tag; }
    public struct AlignedFlagEvent { public bool Enabled; public byte Tag; public char Code; }

    public class ComponentLayoutAndSnapshotTests
    {
        private struct ReferenceValue { public string Text; }

        [Fact]
        public void BoolAndCharColumns_UseTheClrElementStride()
        {
            TestBackend.EnsureInitialized(2);
            Assert.Equal(Unsafe.SizeOf<ByteFlags>(), ComponentTypeManager.GetComponentType(typeof(ByteFlags)).Size);
            Assert.Equal(Unsafe.SizeOf<CharFlags>(), ComponentTypeManager.GetComponentType(typeof(CharFlags)).Size);
            using var world = new World("ClrColumns");
            var em = world.EntityManager;
            var entities = em.CreateEntities(130, typeof(ByteFlags), typeof(CharFlags));
            for (int i = 0; i < entities.Length; i++)
            {
                em.SetRaw(entities[i], typeof(ByteFlags), new ByteFlags { Enabled = true, Tag = (byte)i });
                em.SetComponent(entities[i], new CharFlags { Enabled = true, Code = '\u4e2d', Tag = (byte)i });
            }
            for (int i = 0; i < entities.Length; i++)
            {
                em.SetComponent(entities[i], new ByteFlags { Enabled = false, Tag = (byte)(i + 1) });
                Assert.Equal((byte)(i + 1), em.GetComponent<ByteFlags>(entities[i]).Tag);
                Assert.False(em.GetComponent<ByteFlags>(entities[i]).Enabled);
                Assert.Equal('\u4e2d', em.GetComponent<CharFlags>(entities[i]).Code);
                Assert.Equal((byte)i, em.GetComponent<CharFlags>(entities[i]).Tag);
            }
        }

        [Fact]
        public void ReferenceFields_AreNotRawValueComponents()
        {
            Assert.False(ComponentTypeManager.IsBlittable(typeof(ReferenceValue)));
            Assert.Throws<InvalidOperationException>(() => ComponentTypeManager.GetComponentType(typeof(ReferenceValue)));
        }

        [Fact]
        public void BoolSharedValues_UseTheSameBoxedAndTypedLayout()
        {
            using var world = new World("ClrShared");
            var types = new ComponentType[] { typeof(Position), typeof(SharedFlags) };
            world.EntityManager.NewEntity(types, (typeof(SharedFlags), (object)new SharedFlags { Enabled = true, Tag = 73 }));
            world.EntityManager.NewEntity(types, (typeof(SharedFlags), (object)new SharedFlags { Enabled = true, Tag = 74 }));
            var query = new QueryBuilder().WithAll<Position>().WithShared(new SharedFlags { Enabled = true, Tag = 74 });
            Assert.Equal(1, world.GetOrCreateEntityQuery(query).CalculateEntityCount());
            Assert.Equal(Unsafe.SizeOf<SharedFlags>(), ComponentTypeManager.GetComponentType(typeof(SharedFlags)).Size);
        }

        [Fact]
        public unsafe void NativeEventBuffer_UsesTheClrElementSize()
        {
            ChunkJobScheduler.RegisterEventBufferMeta(typeof(ComponentLayoutAndSnapshotTests), new[] { typeof(ByteFlags) });
            Assert.Throws<InvalidOperationException>(() => ChunkJobScheduler.AllocateEventBuffers(typeof(ComponentLayoutAndSnapshotTests)));
            ChunkJobScheduler.RegisterEventBufferMeta(typeof(ComponentLayoutAndSnapshotTests), new[] { typeof(AlignedFlagEvent) });
            var headers = ChunkJobScheduler.AllocateEventBuffers(typeof(ComponentLayoutAndSnapshotTests));
            try { Assert.Equal(Unsafe.SizeOf<AlignedFlagEvent>(), headers[0].elementSize); }
            finally
            {
                foreach (var header in headers)
                {
                    Marshal.FreeHGlobal(header.dataPtr);
                    Marshal.FreeHGlobal(header.countPtr);
                }
            }
            ChunkJobScheduler.RegisterEventBufferMeta(typeof(ComponentLayoutAndSnapshotTests), new[] { typeof(CharFlags) });
            // This type's six CLR bytes do not meet the existing four-byte event contract.
            Assert.Throws<InvalidOperationException>(() => ChunkJobScheduler.AllocateEventBuffers(typeof(ComponentLayoutAndSnapshotTests)));
        }

        [Fact]
        public void Snapshot_RestoresClrBytesAndEntityVersions()
        {
            using var world = new World("ClrSnapshot");
            var em = world.EntityManager;
            var previous = em.NewEntity(typeof(ByteFlags));
            em.DestroyEntity(previous);
            var current = em.NewEntity(typeof(ByteFlags));
            em.SetComponent(current, new ByteFlags { Enabled = true, Tag = 73 });
            var snapshot = world.TakeSnapshot();
            world.Restore(snapshot);
            Assert.Equal((byte)73, em.GetComponent<ByteFlags>(current).Tag);
            em.SetComponent(current, new ByteFlags { Tag = 74 });
            Assert.Equal((byte)74, em.GetComponent<ByteFlags>(current).Tag);
        }

        [Theory]
        [InlineData("legacy")]
        [InlineData("size")]
        [InlineData("truncated")]
        [InlineData("module")]
        public void RejectedSnapshot_PreservesTheExistingWorld(string corruption)
        {
            using var source = new World("SnapshotSource");
            source.EntityManager.NewEntity(typeof(ByteFlags));
            byte[] data = (byte[])source.TakeSnapshot().Data.Clone();
            if (corruption == "legacy") data = System.Text.Encoding.ASCII.GetBytes("ENTJOY\0\0\0\0\0\0\0\0");
            else if (corruption == "truncated") Array.Resize(ref data, data.Length - 1);
            else
            {
                using var stream = new MemoryStream(data);
                using var reader = new BinaryReader(stream);
                reader.ReadChars(8);
                reader.ReadInt32(); // Pointer width.
                reader.ReadInt32(); // Archetype count.
                reader.ReadInt32(); // Component count.
                reader.ReadInt32(); // Component ID.
                if (corruption == "size") BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan((int)stream.Position), 8);
                else { reader.ReadInt32(); reader.ReadString(); data[stream.Position] ^= 1; }
            }
            using var target = new World("SnapshotTarget");
            var entity = target.EntityManager.NewEntity(typeof(Position));
            target.EntityManager.SetComponent(entity, new Position { X = 99 });
            Assert.Throws<InvalidDataException>(() => target.Restore(new WorldSnapshot(data)));
            Assert.Equal(99f, target.EntityManager.GetComponent<Position>(entity).X);
        }

        [Fact]
        public void Snapshot_WaitsForTrackedWritersBeforeCopying()
        {
            TestBackend.EnsureInitialized(2);
            using var world = new World("SnapshotWriter");
            World.DefaultWorld = world;
            var entity = world.EntityManager.NewEntity(typeof(Position));
            HandleProbeSlowWriteJob.Started = 0;
            HandleProbeSlowWriteJob.Finished = 0;
            var handle = new HandleProbeSlowWriteJob().Schedule(new QueryBuilder().WithAll<Position>());
            try
            {
                var snapshot = world.TakeSnapshot();
                Assert.Equal(1, HandleProbeSlowWriteJob.Finished);
                world.Restore(snapshot);
                Assert.Equal(42f, world.EntityManager.GetComponent<Position>(entity).X);
            }
            finally { handle.Complete(); }
        }

        [Fact]
        public void Snapshot_RejectsOwnedNativeResourceBytes()
        {
            ComponentTypeManager.RegisterDisposable<NativeBufferComponent>();
            using var world = new World("ResourceSnapshot");
            world.EntityManager.NewEntity(typeof(NativeBufferComponent));
            Assert.Throws<NotSupportedException>(() => world.TakeSnapshot());
        }

        [Fact]
        public void Snapshot_DoesNotPretendToSerializeSharedSideTables()
        {
            using var world = new World("SharedSnapshot");
            world.EntityManager.NewEntity(new ComponentType[] { typeof(Position), typeof(Material) },
                (typeof(Material), (object)new Material(1)));
            Assert.Throws<NotSupportedException>(() => world.TakeSnapshot());
        }
    }
}
