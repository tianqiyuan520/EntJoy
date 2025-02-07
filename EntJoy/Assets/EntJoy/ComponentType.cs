using System;

namespace EntJoy
{
    public struct ComponentType : IComparable<ComponentType>
    {
        public int Id { get; }

        public Type Type => ComponentTypeRegistry.GetType(this);

        public ComponentType(int id)
        {
            Id = id;
        }
        
        public static implicit operator ComponentType(Type type)
        {
            return ComponentTypeRegistry.Get(type);
        }

        public int CompareTo(ComponentType other)
        {
            return Id.CompareTo(other.Id);
        }
    }
}