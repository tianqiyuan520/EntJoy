namespace EntJoy  // EntJoy命名空间
{

    public unsafe struct EntityIndexInWorld  // 实体在世界的索引结构
    {
        public Entity* Entity;  // 实体引用
        public Archetype Archetype;  // 所属原型

        public int ChunkIndex;  // 所处的chunk位于原型的位置
        public int SlotInChunk;  // 在chunk中的索引
    }
}
