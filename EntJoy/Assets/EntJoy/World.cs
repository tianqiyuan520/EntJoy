using System;

namespace EntJoy
{
    public class World
    {
        public readonly EntityComponentStorage Storage = new();

        public Entity CreateEntity()
        {
            return CreateEntity(Span<ComponentType>.Empty);
        }

        public Entity CreateEntity(Span<ComponentType> componentTypes)
        {
            return Storage.CreateEntity(componentTypes);
        }

        public void Add<T>() where T : struct
        {
            
            
        }
    }
}