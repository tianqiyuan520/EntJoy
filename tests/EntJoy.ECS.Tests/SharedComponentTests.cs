using System;
using System.Reflection;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>blittable shared：值内联存于 chunk 内存块。</summary>
    public struct Material : ISharedComponentData
    {
        public int Id;
        public Material(int id) { Id = id; }
    }

    /// <summary>managed shared：值存 EntityManager 扁平数组，chunk 槽位存索引。</summary>
    public sealed class MeshAsset : ISharedComponentData, IEquatable<MeshAsset>
    {
        public string Name;
        public MeshAsset(string name) { Name = name; }
        public bool Equals(MeshAsset other) => other != null && Name == other.Name;
        public override bool Equals(object obj) => Equals(obj as MeshAsset);
        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
    }

    public class SharedComponentTests
    {
        private static readonly ComponentType[] MatPos = { typeof(Material), typeof(Position) };
        private static readonly ComponentType[] MeshPos = { typeof(MeshAsset), typeof(Position) };

        private static Entity NewBlittable(EntityManager em, int matId)
            => em.NewEntity(MatPos, (typeof(Material), (object)new Material(matId)));

        private static Entity NewManaged(EntityManager em, string mesh)
            => em.NewEntity(MeshPos, (typeof(MeshAsset), (object)new MeshAsset(mesh)));

        [Fact]
        public void SameValue_SameChunk()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e1 = NewBlittable(em, 7);
            var e2 = NewBlittable(em, 7);

            Assert.Equal(em.GetEntityInfoRef(e1.Id).ChunkIndex, em.GetEntityInfoRef(e2.Id).ChunkIndex);
            Assert.Same(em.GetEntityInfoRef(e1.Id).Archetype, em.GetEntityInfoRef(e2.Id).Archetype);
        }

        [Fact]
        public void DifferentValue_DifferentChunk_SameArchetype()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e1 = NewBlittable(em, 1);
            var e2 = NewBlittable(em, 2);

            Assert.Same(em.GetEntityInfoRef(e1.Id).Archetype, em.GetEntityInfoRef(e2.Id).Archetype);
            Assert.NotEqual(em.GetEntityInfoRef(e1.Id).ChunkIndex, em.GetEntityInfoRef(e2.Id).ChunkIndex);
        }

        [Fact]
        public void GetSharedComponent_Blittable()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e = NewBlittable(em, 42);
            Assert.Equal(42, em.GetSharedComponent<Material>(e).Id);
        }

        [Fact]
        public void GetSharedComponent_Managed()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e = NewManaged(em, "hero.fbx");
            Assert.Equal("hero.fbx", em.GetSharedComponent<MeshAsset>(e).Name);
        }

        [Fact]
        public void SetSharedComponent_SingleEntity_InPlace()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e = NewBlittable(em, 1);
            int chunkBefore = em.GetEntityInfoRef(e.Id).ChunkIndex;
            em.SetSharedComponent(e, new Material(9));
            Assert.Equal(9, em.GetSharedComponent<Material>(e).Id);
            Assert.Equal(chunkBefore, em.GetEntityInfoRef(e.Id).ChunkIndex);
        }

        [Fact]
        public void SetSharedComponent_MultiEntity_MovesChunk()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e1 = NewBlittable(em, 1);
            var e2 = NewBlittable(em, 1);
            var archBefore = em.GetEntityInfoRef(e1.Id).Archetype;
            int chunkBefore = em.GetEntityInfoRef(e1.Id).ChunkIndex;

            em.SetSharedComponent(e1, new Material(2));

            Assert.Equal(2, em.GetSharedComponent<Material>(e1).Id);
            Assert.Same(archBefore, em.GetEntityInfoRef(e1.Id).Archetype);
            Assert.NotEqual(chunkBefore, em.GetEntityInfoRef(e1.Id).ChunkIndex);
        }

        [Fact]
        public void ManagedValue_Deduplication()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e1 = NewManaged(em, "shared.fbx");
            var e2 = NewManaged(em, "shared.fbx");

            // 同值 → 同 chunk（去重 → 同 index）
            Assert.Equal(em.GetEntityInfoRef(e1.Id).ChunkIndex, em.GetEntityInfoRef(e2.Id).ChunkIndex);
            Assert.Equal("shared.fbx", em.GetSharedComponent<MeshAsset>(e1).Name);
        }

        [Fact]
        public void HashBucket_Dedupes_After_Resizing()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 100 个不同值 → 强制 Dictionary 扩容
            for (int i = 0; i < 100; i++)
                NewManaged(em, $"mesh_{i}");

            int countBefore = GetManagedValueCount(em);

            // 去重：同值不新增
            NewManaged(em, "mesh_0");
            Assert.Equal(countBefore, GetManagedValueCount(em));
        }

        private static int GetManagedValueCount(EntityManager em)
        {
            var field = typeof(EntityManager).GetField("_managedSharedValueCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return (int)field.GetValue(em);
        }
    }
}