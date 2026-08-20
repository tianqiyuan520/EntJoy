
namespace EntJoy
{
    public struct Entity
    {
        public int Id;
        public int Version;  // 实体版本号(用于防止悬垂引用)

    }

}
