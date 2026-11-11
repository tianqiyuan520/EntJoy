using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 位置表把块号与槽位各打包 16 位，块号还要求落在正数区（<c>ChunkIndex</c> 用算术右移读出）。
    /// 构造与扩块两处调用点不在这里测——`ChunkCapacityOverride` 是全局静态，值会漏给并行测试。
    /// </summary>
    public class PackedLocationLimitTests
    {
        [Fact]
        public void ChunkCapacity_AtSlotLimit_IsAccepted()
        {
            // 容量 65536 时槽位最大 65535，仍在低 16 位内。
            Archetype.EnsureCapacityWithinPackedLimit(EntityIndexInWorld.PackedSlotLimit);
        }

        [Fact]
        public void ChunkCapacity_AboveSlotLimit_Throws()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Archetype.EnsureCapacityWithinPackedLimit(EntityIndexInWorld.PackedSlotLimit + 1));
            Assert.Contains("ChunkCapacityOverride", error.Message);
        }

        [Fact]
        public void ChunkIndex_AtChunkLimit_StaysPositive()
        {
            var info = new EntityIndexInWorld();
            info.SetLocation(null, EntityIndexInWorld.PackedChunkLimit - 1, EntityIndexInWorld.PackedSlotLimit - 1);
            Assert.Equal(EntityIndexInWorld.PackedChunkLimit - 1, info.ChunkIndex);
            Assert.Equal(EntityIndexInWorld.PackedSlotLimit - 1, info.SlotInChunk);
        }

        [Fact]
        public void ChunkIndex_AboveChunkLimit_ReadsNegative()
        {
            // 超过块号上限时高半区的符号位被置上：这是把上限设成 1<<15 的原因，不是可用的坐标。
            var info = new EntityIndexInWorld();
            info.SetLocation(null, EntityIndexInWorld.PackedChunkLimit, 0);
            Assert.True(info.ChunkIndex < 0);
        }

        [Fact]
        public void PackedFields_RoundTripAndClearToExpectedValues()
        {
            var info = new EntityIndexInWorld();
            info.SetLocation(null, 3, 7);
            Assert.Equal(3, info.ChunkIndex);
            Assert.Equal(7, info.SlotInChunk);

            info.ChunkIndex = 5;
            info.SlotInChunk = 9;
            Assert.Equal(5, info.ChunkIndex);
            Assert.Equal(9, info.SlotInChunk);

            // 清空后块号读 -1、槽位读 65535：存活判定只看 Archetype，不看槽位。
            info.ClearLocation();
            Assert.Null(info.Archetype);
            Assert.Equal(-1, info.ChunkIndex);
            Assert.Equal(0xFFFF, info.SlotInChunk);
        }
    }
}
