using Xunit;

namespace EntJoy.ECS.Tests;

/// <summary>
/// swap-pop 把同 chunk 末位实体搬到空洞时，只改两张位置表的槽位（原型、块基址、列偏移不变）。
/// 这条用例钉住搬移后的定位条目：原生定位表与托管表必须一致指向新槽位，块基址保持原值。
/// </summary>
public sealed unsafe class SwapPopLocationConsistencyTests
{
    public struct Position : IComponentData
    {
        public float X;
    }

    [Fact]
    public void MovedEntityLocationTracksTheVacatedSlot()
    {
        using var world = new World("SwapPopLocation");
        var em = world.EntityManager;

        var a = em.NewEntity(typeof(Position));
        var b = em.NewEntity(typeof(Position));
        var c = em.NewEntity(typeof(Position));

        em.SetComponent(a, new Position { X = 1f });
        em.SetComponent(b, new Position { X = 2f });
        em.SetComponent(c, new Position { X = 3f });

        ref var infoC = ref em.GetEntityInfoRef(c.Id);
        int chunkIndexOfC = infoC.ChunkIndex;
        int slotOfC = infoC.SlotInChunk;
        EntityLocateB before = em.GetEntityLocatePtr()[c.Id];
        Assert.True(before.IsAllocated);

        // 销毁同 chunk 的另一个实体 ⇒ c 被搬到空洞
        var victim = slotOfC == 0 ? a : b;
        ref var infoVictim = ref em.GetEntityInfoRef(victim.Id);
        int victimSlot = infoVictim.SlotInChunk;
        Assert.Equal(chunkIndexOfC, infoVictim.ChunkIndex);
        em.DestroyEntity(victim);

        ref var moved = ref em.GetEntityInfoRef(c.Id);
        EntityLocateB after = em.GetEntityLocatePtr()[c.Id];

        Assert.Equal(chunkIndexOfC, moved.ChunkIndex);
        Assert.Equal(victimSlot, moved.SlotInChunk);
        Assert.True(after.IsAllocated);
        Assert.Equal(moved.SlotInChunk, after.SlotInChunk);
        Assert.Equal((nint)before.ChunkMemory, (nint)after.ChunkMemory);
        Assert.Equal((nint)before.ChunkOffsets, (nint)after.ChunkOffsets);
        Assert.Equal(3f, em.GetComponent<Position>(c).X);
    }
}
