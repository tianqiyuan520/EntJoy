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

        // ======================== per-value 最近使用缓存（方案 B，2026-08-29 后） ========================

        [Fact]
        public void SetSharedComponent_RepeatedValues_CacheHit_Consistent()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 两实体同值 → 同 chunk
            var e1 = NewBlittable(em, 1);
            var e2 = NewBlittable(em, 1);

            // 高频改值：反复在值 1/2 间移动（每次 Set 走缓存优先查找）
            for (int i = 0; i < 50; i++)
            {
                em.SetSharedComponent(e1, new Material(2));
                em.SetSharedComponent(e1, new Material(1));
            }

            Assert.Equal(1, em.GetSharedComponent<Material>(e1).Id);
            Assert.Equal(1, em.GetSharedComponent<Material>(e2).Id);
            // 注：SetSharedComponent 单实体 chunk 就地改值不合并同值 chunk（既有语义），
            // 因此不断言 e1/e2 同 chunk；只验证各自 chunk 索引有效（同 chunk 同值不变式成立）
            Assert.InRange(em.GetEntityInfoRef(e1.Id).ChunkIndex, 0, em.GetEntityInfoRef(e1.Id).Archetype.ChunkCount - 1);
            Assert.InRange(em.GetEntityInfoRef(e2.Id).ChunkIndex, 0, em.GetEntityInfoRef(e2.Id).Archetype.ChunkCount - 1);
        }

        [Fact]
        public void Cache_Invalidated_After_ChunkRecycled()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // chunk0 = 值1(e1, e2)，chunk1 = 值3(e3)
            var e1 = NewBlittable(em, 1);
            var e2 = NewBlittable(em, 1);
            var e3 = NewBlittable(em, 3);

            // e1 移到值 3 → 缓存记录 (值3 → chunk1)
            em.SetSharedComponent(e1, new Material(3));
            // e1 移回值 1 → 缓存记录 (值1 → chunk0)
            em.SetSharedComponent(e1, new Material(1));

            // 摧毁 e3 → chunk1 变空 → 从列表移除（缓存中 (值3 → 1) 残留，索引越界）
            em.DestroyEntity(e3);

            // 再创建值 3 实体 → lazy 验证失败 → 回退扫描 → 新建 chunk，值必须正确
            var e4 = NewBlittable(em, 3);
            Assert.Equal(3, em.GetSharedComponent<Material>(e4).Id);
            Assert.InRange(em.GetEntityInfoRef(e4.Id).ChunkIndex, 0, em.GetEntityInfoRef(e4.Id).Archetype.ChunkCount - 1);
        }

        [Fact]
        public void Cache_Invalidated_After_InPlaceValueChange()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            // 单实体 chunk（值 1）
            var e1 = NewBlittable(em, 1);
            int chunkBefore = em.GetEntityInfoRef(e1.Id).ChunkIndex;

            // 就地改值 1 → 2（chunk 不变；缓存中 (值1 → chunk) 残留但 chunk 值已变）
            em.SetSharedComponent(e1, new Material(2));
            Assert.Equal(chunkBefore, em.GetEntityInfoRef(e1.Id).ChunkIndex);

            // 再创建值 1 实体 → 缓存验证失败 → 回退扫描 → 无值 1 chunk → 新建
            var e2 = NewBlittable(em, 1);
            Assert.Equal(1, em.GetSharedComponent<Material>(e2).Id);
            Assert.NotEqual(chunkBefore, em.GetEntityInfoRef(e2.Id).ChunkIndex);
        }

        [Fact]
        public void Managed_CacheHit_Path()
        {
            using var world = new World("Test");
            World.DefaultWorld = world;
            var em = world.EntityManager;

            var e1 = NewManaged(em, "a.fbx");
            var e2 = NewManaged(em, "b.fbx");

            // 反复在 a/b 间移动（managed 缓存路径）
            for (int i = 0; i < 50; i++)
            {
                em.SetSharedComponent(e1, new MeshAsset("b.fbx"));
                em.SetSharedComponent(e1, new MeshAsset("a.fbx"));
            }

            Assert.Equal("a.fbx", em.GetSharedComponent<MeshAsset>(e1).Name);
            Assert.Equal("b.fbx", em.GetSharedComponent<MeshAsset>(e2).Name);
            // e1 最终值与同值新实体应在同一 chunk
            var e3 = NewManaged(em, "a.fbx");
            Assert.Equal(em.GetEntityInfoRef(e1.Id).ChunkIndex, em.GetEntityInfoRef(e3.Id).ChunkIndex);
        }
    }
}