namespace EntJoy
{
    public struct EntityIndexInWorld
    {
        public Archetype Archetype;
        public int ChunkIndex;
        public int SlotInChunk;
        public int Version;  // 实体版本号，用于检测悬垂引用
    }
}
