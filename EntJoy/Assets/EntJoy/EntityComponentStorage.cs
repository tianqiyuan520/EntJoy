using System;

namespace EntJoy
{
    public class EntityComponentStorage
    {
        public Entity CreateEntity(Span<ComponentType> componentTypes)
        {
            return new Entity();
        }

        /*public Archetype GetOrCreateArchetype(Span<ComponentType> componentTypes)
        {
            
        }*/
    }
}