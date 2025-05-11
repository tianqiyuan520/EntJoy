namespace EntJoy  // EntJoy命名空间
{
    public struct Entity  // 实体结构体
    {
        public int Id;  // 实体唯一标识符
        public int Version;  // 实体版本号(用于防止悬垂引用)
    }
}
