namespace EntJoy  // EntJoy命名空间
{

    public struct EntityIndexInWorld  // 实体在世界的索引结构
    {
        public Entity Entity;  // 实体引用
        public Archetype Archetype;  // 所属原型
        public int SlotInArchetype;  // 在原型中的索引
    }
}
