using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 带数据的关系组件：首个字段必须是 RelationSlot Target（偏移 0），可携带任意 blittable 数据。
    /// 列宽 = Unsafe.SizeOf&lt;Owns&gt; = 12B（8B slot + 4B count），验证列宽 >8B 路径。
    /// </summary>
    public struct Owns : IRelationComponent { public RelationSlot Target; public int Count; }

    /// <summary>带数据关系测试（Phase 1：关系数据）。</summary>
    public class RelationDataTests
    {
        private static World NewWorld() => new World("RelData" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void RelationComponent_WithData_RegistersRealSize()
        {
            var ct = ComponentTypeManager.GetComponentType(typeof(Owns));
            // 列宽 = 真实 SizeOf（8B slot + 4B count = 12B），不再强制 8B
            Assert.True(ct.Size > 8, $"Owns column width should exceed 8B, got {ct.Size}");
            Assert.Equal(12, ct.Size);
        }

        [Fact]
        public void AddRelationship_WithData_WritesSlotAndData()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));
            em.AddRelationship<Owns>(player, sword);

            // Target 可读（前 8B）
            Assert.Equal(sword.Id, em.GetRelationship<Owns>(player).Id);
            Assert.Equal(sword.Version, em.GetRelationship<Owns>(player).Version);
            Assert.True(em.HasRelationship<Owns>(player));

            // 数据字段初始为 default(0)
            var info = em.GetEntityInfoRef(player.Id);
            var chunk = info.Archetype.ChunkList[info.ChunkIndex];
            ref readonly var owns = ref chunk.GetComponentReadOnly<Owns>(info.SlotInChunk, info.Archetype.GetComponentTypeIndex(typeof(Owns)));
            Assert.Equal(0, owns.Count);
            Assert.Equal(sword.Id, owns.Target.TargetId);
        }

        [Fact]
        public void AddRelationship_Overwrite_KeepsDataFieldDefault()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));
            var bow = em.NewEntity(typeof(Position));
            em.AddRelationship<Owns>(player, sword);
            em.AddRelationship<Owns>(player, bow);   // 重绑目标，保留初始业务数据

            Assert.Equal(bow.Id, em.GetRelationship<Owns>(player).Id);
            var info = em.GetEntityInfoRef(player.Id);
            var chunk = info.Archetype.ChunkList[info.ChunkIndex];
            ref readonly var owns = ref chunk.GetComponentReadOnly<Owns>(info.SlotInChunk, info.Archetype.GetComponentTypeIndex(typeof(Owns)));
            Assert.Equal(bow.Id, owns.Target.TargetId);
            Assert.Equal(0, owns.Count);
        }

        [Fact]
        public void MutateDataField_ThenReadBack()
        {
            using var world = NewWorld();
            var em = world.EntityManager;
            var player = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));
            em.AddRelationship<Owns>(player, sword);
            var value = em.GetComponentReadOnly<Owns>(player);
            value.Count = 3;
            em.SetComponent(player, value);
            var actual = em.GetComponentReadOnly<Owns>(player);
            Assert.Equal(3, actual.Count);
            Assert.Equal(sword.Id, actual.Target.TargetId);
        }

        [Fact]
        public void WithRelationship_Filter_WorksWithWideColumn()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var ownerA = em.NewEntity(typeof(Position));
            var ownerB = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));
            var bow = em.NewEntity(typeof(Position));

            // 4 个实体，2 个指向 sword（含数据列），2 个指向 bow
            var e1 = em.NewEntity(typeof(Position));
            var e2 = em.NewEntity(typeof(Position));
            var e3 = em.NewEntity(typeof(Position));
            var e4 = em.NewEntity(typeof(Position));
            em.AddRelationship<Owns>(e1, sword);
            em.AddRelationship<Owns>(e2, sword);
            em.AddRelationship<Owns>(e3, bow);
            em.AddRelationship<Owns>(e4, bow);

            int swordCount = 0, bowCount = 0;
            foreach (var r in world.Query<Position>().WithRelationship<Owns>(sword)) swordCount++;
            foreach (var r in world.Query<Position>().WithRelationship<Owns>(bow)) bowCount++;
            Assert.Equal(2, swordCount);
            Assert.Equal(2, bowCount);
        }

        [Fact]
        public void RemoveRelationship_WithData_RemovesColumn()
        {
            using var world = NewWorld();
            var em = world.EntityManager;

            var player = em.NewEntity(typeof(Position));
            var sword = em.NewEntity(typeof(Position));
            em.AddRelationship<Owns>(player, sword);
            Assert.True(em.HasRelationship<Owns>(player));

            em.RemoveRelationship<Owns>(player);
            Assert.False(em.HasRelationship<Owns>(player));
            Assert.Equal(default, em.GetRelationship<Owns>(player));
        }

        [Fact]
        public void EmptyRelationStruct_ThrowsOnRegister()
        {
            // 空 struct（无 Target 字段）必须注册失败（防越界误用）
            Assert.Throws<InvalidOperationException>(() =>
                ComponentTypeManager.GetComponentType(typeof(EmptyRel)));
        }

        public struct EmptyRel : IRelationComponent { }
    }
}
