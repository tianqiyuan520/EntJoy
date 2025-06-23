

namespace EntJoy
{
    public class Chunk
    {
        public StructArray<Entity> Entities;
        public StructArray[] Components;
        public int Count;

        public Chunk(int entityCapacity, ComponentType[] componentTypes)
        {
            Entities = new StructArray<Entity>(entityCapacity);
            Components = new StructArray[componentTypes.Length];

            for (int i = 0; i < componentTypes.Length; i++)
            {
                var genericType = typeof(StructArray<>).MakeGenericType(componentTypes[i].Type);
                Components[i] = (StructArray)Activator.CreateInstance(genericType, entityCapacity);
            }
        }
    }
}