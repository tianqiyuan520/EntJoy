using System;

namespace EntJoy
{
    public struct ComponentType : IEquatable<ComponentType>
    {
        public readonly int Id;
        
        public ComponentType(int id)
        {
            Id = id;
        }

        public static implicit operator ComponentType(Type type)
        {
            return ComponentTypeRegistry.GetComponentType(type);
        }

        public bool Equals(ComponentType other)
        {
            return Id == other.Id;
        }

        public override bool Equals(object obj)
        {
            return obj is ComponentType other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Id;
        }

        public static bool operator ==(ComponentType left, ComponentType right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ComponentType left, ComponentType right)
        {
            return !left.Equals(right);
        }
    }
}