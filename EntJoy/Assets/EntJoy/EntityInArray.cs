namespace EntJoy
{
    // 描述目标组件在同类型组件数组中的位置
    public struct ComponentInArray
    {
        /// <summary>
        /// 组件数组中的第几个为指定组件
        /// </summary>
        public int componentIndex;
    }

    public struct ArchetypeLocate
    {
        public Archetype Archetype;
        public ComponentInArray ComponentInArray;
    }
}