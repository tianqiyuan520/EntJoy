using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace EntJoy
{
    public struct QueryDesc
    {
        public UnsafeList<ComponentType> All;
        public UnsafeList<ComponentType> Any;
        public UnsafeList<ComponentType> None;
        private AllocatorManager.AllocatorHandle allocatorHandle;

        public QueryDesc(AllocatorManager.AllocatorHandle allocator)
        {
            allocatorHandle = allocator;
            All = new UnsafeList<ComponentType>(4, allocator);
            Any = new UnsafeList<ComponentType>(2, allocator);
            None = new UnsafeList<ComponentType>(2, allocator);
        }
        
        public QueryDesc WithAll<T>()
        {
            All.Add(ComponentTypeRegistry.Get(typeof(T)));
            return this;
        }

        public QueryDesc WithAny<T>()
        {
            Any.Add(ComponentTypeRegistry.Get(typeof(T)));
            return this;
        }

        public QueryDesc WithNone<T>()
        {
            None.Add(ComponentTypeRegistry.Get(typeof(T)));
            return this;
        }
    }
}