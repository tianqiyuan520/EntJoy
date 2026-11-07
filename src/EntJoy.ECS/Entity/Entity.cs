
namespace EntJoy.ECS
{
    public struct Entity : System.IEquatable<Entity>
    {
        public int Id;
        public int Version;  // 实体版本号(用于防止悬垂引用)

        public readonly bool Equals(Entity other) => Id == other.Id && Version == other.Version;
        public override readonly bool Equals(object obj) => obj is Entity other && Equals(other);
        public override readonly int GetHashCode() => System.HashCode.Combine(Id, Version);

    }

}
