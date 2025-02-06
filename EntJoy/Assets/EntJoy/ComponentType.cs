using System;

namespace EntJoy
{
    public struct ComponentType
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
    }
}