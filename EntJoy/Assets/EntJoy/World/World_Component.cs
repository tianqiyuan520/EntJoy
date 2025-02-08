using System;

namespace EntJoy
{
    public partial class World
    {
        public void AddComponent<T0>(Entity entity, T0 t0)
        {
            var info = entities[entity.Id];
            var arch = info.Archetype;
           // arch.MoveEntityToAnotherArchetypeByAdd<T0>(info.);
        }
    }
}