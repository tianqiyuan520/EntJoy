namespace EntJoy.ECS
{
    /// <summary>
    /// 实体在世界中的位置。块号与块内槽位打包进一个 int（高 16 位块号、低 16 位槽位），
    /// 一次写入即可整体移动，实体表也因此从 24 字节缩到 16 字节。
    /// </summary>
    public struct EntityIndexInWorld
    {
        /// <summary>槽位字段的范围：低 16 位，容量不得超过该值（槽位 0..65535）。</summary>
        public const int PackedSlotLimit = 1 << 16;

        /// <summary>块号字段的范围：高 16 位且必须落在正数区（<see cref="ChunkIndex"/> 用算术右移读出），
        /// 因此块数不得超过该值；再高会读成负数，0xFFFF/0xFFFF 恰好等于清空哨兵 -1。</summary>
        public const int PackedChunkLimit = 1 << 15;

        public Archetype Archetype;
        public int Address;   // (chunkIndex << 16) | slotInChunk
        public int Version;   // 实体版本号，用于检测悬垂引用

        public int ChunkIndex
        {
            get => Address >> 16;
            set => Address = (value << 16) | (Address & 0xFFFF);
        }

        public int SlotInChunk
        {
            get => Address & 0xFFFF;
            set => Address = (Address & ~0xFFFF) | (value & 0xFFFF);
        }

        /// <summary>清空位置：原型置空、打包地址写成 -1（块号读出 -1，槽位读出 65535）。
        /// 存活判定只看 <see cref="Archetype"/>，槽位非负不代表实体存活。</summary>
        public void ClearLocation()
        {
            Archetype = null;
            Address = -1;
        }

        /// <summary>一次写入原型与打包后的位置，避免分三次写同一实体的字段。
        /// 调用方必须保证块号小于 <see cref="PackedChunkLimit"/>、槽位小于 <see cref="PackedSlotLimit"/>；越界会静默截断。</summary>
        public void SetLocation(Archetype archetype, int chunkIndex, int slotInChunk)
        {
            Archetype = archetype;
            Address = (chunkIndex << 16) | (slotInChunk & 0xFFFF);
        }
    }
}